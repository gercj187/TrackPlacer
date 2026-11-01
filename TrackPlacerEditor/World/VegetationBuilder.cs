using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TrackPlacer.Data;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TrackPlacer.World
{
	/// <summary>
	/// Vegetation entfernen: Gras, Büsche und Bäume kommen im Spiel aus Vegetation Studio Pro (prozedural, keine
	/// einzelnen Objekte). Rechteckige Flächen verwenden VegetationMaskArea, Gleise VegetationMaskLine entlang der
	/// tatsächlichen Bezierkurve. Alles bleibt per Reflection, damit die Mod ohne Verweis auf die VSP-DLL baut.
	/// </summary>
	internal static class VegetationBuilder
	{
		private const string NamePrefix = "[TP]_vegmask_";
		private const string TrackPrefix = "track_";
		private const float TrackClearWidth = 7f;
		private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

		private static Type? s_lineType, s_areaType, s_systemType;
		private static bool s_searched, s_warned;
		private static readonly Dictionary<string, GameObject> s_built = new Dictionary<string, GameObject>();
		private static readonly Dictionary<string, int> s_pendingClones = new Dictionary<string, int>();
		private static readonly List<Bounds> s_pendingRefresh = new List<Bounds>();
		private static int s_cloneVersion, s_worldGeneration;
		private static bool s_refreshScheduled, s_waitingForRuntime;

		private sealed class MaskMarker : MonoBehaviour { public Bounds bounds; public VegetationCutter.Record? cut; }

		// ------------------------------------------------------------------
		// KOORDINATEN: Vegetation Studio rechnet in STATISCHEN Koordinaten (ohne Origin-Shift) und gleicht den
		// Shift erst beim Zeichnen aus. Das Spiel macht es genauso (RailwayVegetationRemover: eigenes Objekt in der
		// Szenen-Wurzel, Knoten = PointSet-Punkte = statisch). Deshalb liegen alle Masken unter einer eigenen,
		// NICHT verschobenen Wurzel und bekommen statische Knoten. Unter [railway] (verschoben) oder mit
		// +OriginShift landeten die Flächen weit weg vom Kartenursprung daneben – Gras blieb stehen.
		// ------------------------------------------------------------------
		private static GameObject? s_root;

		private static Transform Root
		{
			get
			{
				if (s_root == null) s_root = new GameObject("[TrackPlacer] Vegetationsmasken"); // Szenen-Wurzel, wird nicht verschoben
				return s_root.transform;
			}
		}

		public static IReadOnlyDictionary<string, GameObject> Built => s_built;
		public static bool Available => AreaType != null;

		public static void OnWorldUnloaded()
		{
			s_root = null; // gehört zur alten Welt
			s_worldGeneration++;
			s_built.Clear();
			s_pendingClones.Clear();
			s_pendingRefresh.Clear();
			s_refreshScheduled = false;
			s_waitingForRuntime = false;
			VegetationCutter.OnWorldUnloaded();
		}

		/// <summary>Maske eines Klons verzögert erneuern (beim Drehen/Verschieben nicht bei jedem Mausrad-Schritt).</summary>
		public static void ForCloneLater(PlacedClone c)
		{
			int version = ++s_cloneVersion;
			int generation = s_worldGeneration;
			s_pendingClones[c.id] = version;
			WorldLoader.RunDelayed(0.35f, () =>
			{
				if (generation != s_worldGeneration || !s_pendingClones.TryGetValue(c.id, out int current) || current != version) return;
				s_pendingClones.Remove(c.id);
				var saved = LayoutStore.Layout.clones.Find(x => x.id == c.id);
				if (saved != null && CloneBuilder.Built.TryGetValue(saved.id, out var go) && go != null) ForClone(saved, go);
			});
		}

		private static void FindTypes()
		{
			if (s_searched) return;
			s_searched = true;
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				try
				{
					s_lineType ??= asm.GetType("AwesomeTechnologies.VegetationMaskLine", false);
					s_areaType ??= asm.GetType("AwesomeTechnologies.VegetationMaskArea", false);
					s_systemType ??= asm.GetType("AwesomeTechnologies.VegetationSystem.VegetationSystemPro", false);
				}
				catch { /* optionale Spiel-DLL */ }
				if (s_lineType != null && s_areaType != null) break;
			}
			if (s_lineType == null || s_areaType == null)
				Main.Log("Vegetation Studio (VegetationMaskArea/Line) nicht gefunden – Vegetation kann nicht entfernt werden.");
		}

		private static Type? LineType { get { FindTypes(); return s_lineType; } }
		private static Type? AreaType { get { FindTypes(); return s_areaType; } }
		private static Type? SystemType { get { FindTypes(); return s_systemType; } }

		/// <summary>Komponenten allein reichen nicht: vor InitDone registriert VSP Masken in keine Zellen.</summary>
		private static bool RuntimeReady
		{
			get
			{
				var type = SystemType;
				if (type == null) return false;
				var init = type.GetField("InitDone", Inst);
				foreach (var system in Object.FindObjectsOfType(type))
					if (system != null && (init == null || init.GetValue(system) is bool ready && ready)) return true;
				return false;
			}
		}

		private static void Set(object o, string name, object value)
		{
			var t = o.GetType();
			var f = t.GetField(name, Inst);
			if (f != null) { f.SetValue(o, Convert.ChangeType(value, f.FieldType)); return; }
			var p = t.GetProperty(name, Inst);
			if (p != null && p.CanWrite) p.SetValue(o, Convert.ChangeType(value, p.PropertyType));
		}

		private static void Configure(Component mask)
		{
			foreach (var flag in new[] { "RemoveGrass", "RemovePlants", "RemoveTrees", "RemoveObjects", "RemoveLargeObjects" })
				Set(mask, flag, true);

			// VSP prüft den Pivot der Instanz, nicht die sichtbare Krone. Darum brauchen Büsche/Bäume einen
			// zusätzlichen Rand. Min = Max hält die Kante deterministisch (kein Perlin-Zufallsrand).
			foreach (var pair in new[]
			{
				("Grass", 0f), ("Plant", 5f), ("Tree", 8f), ("Object", 6f), ("LargeObject", 10f),
			})
			{
				Set(mask, "Additional" + pair.Item1 + "Perimiter", pair.Item2);
				Set(mask, "Additional" + pair.Item1 + "PerimiterMax", pair.Item2);
			}
		}

		private static void AddNodes(Component mask, Vector3[] worldNodes)
		{
			var type = mask.GetType();
			type.GetMethod("ClearNodes", Inst, null, Type.EmptyTypes, null)?.Invoke(mask, null);
			var add = type.GetMethods(Inst).FirstOrDefault(m => m.Name == "AddNodesToEnd" && m.GetParameters().Length == 1
				&& m.GetParameters()[0].ParameterType == typeof(Vector3[]));
			if (add == null) throw new MissingMethodException(type.FullName, "AddNodesToEnd(Vector3[])");
			// VegetationMask.AddNodeToEnd(Vector3 worldPosition) rechnet selbst mit transform.InverseTransformPoint in
			// lokale Knoten um (geprüft in AwesomeTechnologies.VegetationStudioPro.Runtime.dll). Also WELT-Koordinaten
			// übergeben – vorher umgerechnete Knoten wurden ein zweites Mal verschoben (Maske um ihren Mittelpunkt versetzt).
			add.Invoke(mask, new object[] { worldNodes });
		}

		private static Bounds MaskBounds(Vector3[] nodes, float padding)
		{
			float minX = nodes.Min(p => p.x) - padding, maxX = nodes.Max(p => p.x) + padding;
			float minZ = nodes.Min(p => p.z) - padding, maxZ = nodes.Max(p => p.z) + padding;
			return new Bounds(new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f),
				new Vector3(Mathf.Max(1f, maxX - minX), 20000f, Mathf.Max(1f, maxZ - minZ)));
		}

		private static void ScheduleRefresh(Bounds bounds)
		{
			// Nahe/überlappende Bereiche zusammenfassen, damit dieselbe Vegetationszelle nur einmal geleert wird.
			for (int i = 0; i < s_pendingRefresh.Count; i++)
			{
				Bounds expanded = s_pendingRefresh[i];
				expanded.Expand(10f);
				if (!expanded.Intersects(bounds)) continue;
				Bounds merged = s_pendingRefresh[i];
				merged.Encapsulate(bounds);
				s_pendingRefresh[i] = merged;
				goto queued;
			}
			s_pendingRefresh.Add(bounds);
		queued:
			if (s_refreshScheduled) return;
			s_refreshScheduled = true;
			int generation = s_worldGeneration;
			WorldLoader.RunAfterFrames(2, () => FlushRefresh(generation));
		}

		private static void FlushRefresh(int generation)
		{
			s_refreshScheduled = false;
			if (generation != s_worldGeneration) return;
			var type = SystemType;
			if (type == null || s_pendingRefresh.Count == 0) return;
			var bounds = s_pendingRefresh.ToArray();
			s_pendingRefresh.Clear();
			var clear = type.GetMethod("ClearCache", Inst, null, new[] { typeof(Bounds) }, null);
			var colliders = type.GetMethod("RefreshColliderSystem", Inst, null, Type.EmptyTypes, null);
			var prefabs = type.GetMethod("RefreshRuntimePrefabSpawner", Inst, null, Type.EmptyTypes, null);
			foreach (var system in Object.FindObjectsOfType(type))
			{
				if (system == null) continue;
				foreach (var area in bounds) clear?.Invoke(system, new object[] { area });
				colliders?.Invoke(system, null);
				prefabs?.Invoke(system, null);
			}
		}

		private static void DestroyMask(GameObject go)
		{
			var marker = go.GetComponent<MaskMarker>();
			if (marker != null) { VegetationCutter.Restore(marker.cut); marker.cut = null; ScheduleRefresh(marker.bounds); }
			Object.Destroy(go);
		}

		private static GameObject? Create(string name, Transform parent, Type type, Vector3[] nodes, float lineWidth = 0f)
		{
			try
			{
				if (nodes.Length < 2 || !RuntimeReady) return null;
				var go = new GameObject(name);
				go.SetActive(false);
				go.transform.SetParent(parent, false);
				go.transform.position = nodes.Aggregate(Vector3.zero, (sum, p) => sum + p) / nodes.Length;
				var mask = go.AddComponent(type);
				Configure(mask);
				if (lineWidth > 0f) Set(mask, "LineWidth", Mathf.Max(0.5f, lineWidth));
				AddNodes(mask, nodes);
				go.SetActive(true);
				type.GetMethod("UpdateVegetationMask", Inst, null, Type.EmptyTypes, null)?.Invoke(mask, null);
				// LineWidth ist die Gesamtbreite; der große Rand erwischt auch Kronen, deren Pivot außerhalb liegt.
				Bounds bounds = MaskBounds(nodes, (lineWidth > 0f ? lineWidth * 0.5f : 0f) + 10f);
				var marker = go.AddComponent<MaskMarker>();
				marker.bounds = bounds;
				// Büsche/Bäume aus dem Vegetations-Speicher und Pflanzen-Objekte: die Maske allein erreicht sie nicht
				marker.cut = VegetationCutter.Cut(name.Replace(NamePrefix, "Vegetation "), nodes, lineWidth, SystemType);
				ScheduleRefresh(bounds);
				return go;
			}
			catch (Exception e)
			{
				if (!s_warned) { s_warned = true; Main.Log("Vegetations-Maske fehlgeschlagen: " + e); }
				return null;
			}
		}

		private static GameObject? CreateAreaMask(string name, Transform parent, Vector3[] polygon)
		{
			var type = AreaType;
			return type == null ? null : Create(name, parent, type, polygon);
		}

		private static GameObject? CreateLineMask(string name, Transform parent, Vector3[] nodes, float width)
		{
			var type = LineType;
			return type == null ? null : Create(name, parent, type, nodes, width);
		}

		// ------------------------------------------------------------------
		// Unter Klonen: echte Polygonmaske statt einer beinahe punktförmigen Linienmaske
		// ------------------------------------------------------------------

		private static string CloneKey(string id) => "clone_" + id;

		public static void ForClone(PlacedClone c, GameObject clone)
		{
			RemoveClone(c.id);
			if (c.keepVegetation || AreaType == null || !RuntimeReady) return;

			var b = CloneHelper.LocalBounds(clone.transform);
			const float margin = 0.3f;
			float x0 = b.min.x - margin, x1 = b.max.x + margin;
			float z0 = b.min.z - margin, z1 = b.max.z + margin;
			float y = b.min.y;
			Vector3 shift = GameAccess.OriginShift;
			// statisch: Weltposition des Klons minus Origin-Shift
			Vector3 S(float x, float z) => clone.transform.position + clone.transform.rotation * new Vector3(x, y, z) - shift;
			var made = CreateAreaMask(NamePrefix + CloneKey(c.id), Root, new[] { S(x0, z0), S(x1, z0), S(x1, z1), S(x0, z1) });
			if (made != null) s_built[CloneKey(c.id)] = made;
		}

		public static void RemoveClone(string id)
		{
			string key = CloneKey(id);
			if (s_built.TryGetValue(key, out var go) && go != null) DestroyMask(go);
			s_built.Remove(key);
		}

		// ------------------------------------------------------------------
		// Eigene Gleise: persistente 7-m-Linienmaske entlang der Bezierkurve
		// ------------------------------------------------------------------

		private static string TrackKey(string id) => TrackPrefix + id;

		private static Vector3 Cubic(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
		{
			float u = 1f - t;
			return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
		}

		private static Vector3[] TrackNodes(PlacedTrack t)
		{
			var anchors = t.ToAnchors();
			var nodes = new List<Vector3>();
			// Anker sind statisch – genau das braucht Vegetation Studio
			for (int i = 0; i + 1 < anchors.Count; i++)
			{
				var a = anchors[i]; var b = anchors[i + 1];
				Vector3 p0 = a.position, p1 = a.position + a.handle2;
				Vector3 p2 = b.position + b.handle1, p3 = b.position;
				float estimate = Vector3.Distance(p0, p1) + Vector3.Distance(p1, p2) + Vector3.Distance(p2, p3);
				int steps = Mathf.Max(1, Mathf.CeilToInt(estimate / 4f));
				for (int j = i == 0 ? 0 : 1; j <= steps; j++) nodes.Add(Cubic(p0, p1, p2, p3, j / (float)steps));
			}
			return nodes.ToArray();
		}

		public static bool BuildTrack(PlacedTrack t)
		{
			string key = TrackKey(t.id);
			if (!t.clearVegetation) { RemoveTrack(t.id); return true; }
			if (s_built.TryGetValue(key, out var old) && old != null) return true;
			if (!GameAccess.TracksReady || GameAccess.Registry == null || LineType == null || !RuntimeReady) return false;
			var made = CreateLineMask(NamePrefix + key, Root, TrackNodes(t), TrackClearWidth);
			if (made == null) return false;
			s_built[key] = made;
			Main.Log($"Gleis {t.id}: Vegetation auf {TrackClearWidth:0.#} m Breite freigeschnitten");
			return true;
		}

		public static bool RebuildTrack(PlacedTrack t)
		{
			RemoveTrack(t.id);
			return !t.clearVegetation || BuildTrack(t);
		}

		public static void RemoveTrack(string id)
		{
			string key = TrackKey(id);
			if (s_built.TryGetValue(key, out var go) && go != null) DestroyMask(go);
			s_built.Remove(key);
		}

		private static string VanillaKey(string id) => "vanilla_" + id;

		private static Vector3[] VanillaNodes(RailTrack track)
		{
			var ps = track.GetKinkedPointSet();
			if (ps?.points == null || ps.points.Length == 0) return Array.Empty<Vector3>();
			var nodes = new List<Vector3>();
			// PointSet-Punkte sind statisch – genau das braucht Vegetation Studio (wie RailwayVegetationRemover)
			for (int i = 0; i < ps.points.Length; i += 4) nodes.Add((Vector3)ps.points[i].position);
			Vector3 last = (Vector3)ps.points[ps.points.Length - 1].position;
			if (nodes.Count == 0 || (nodes[nodes.Count - 1] - last).sqrMagnitude > 0.01f) nodes.Add(last);
			return nodes.ToArray();
		}

		public static bool BuildVanilla(VanillaTrackEdit e, RailTrack track)
		{
			string key = VanillaKey(e.id);
			if (!e.clearVegetation) { RemoveVanilla(e.id); return true; }
			if (s_built.TryGetValue(key, out var old) && old != null) return true;
			if (!GameAccess.TracksReady || GameAccess.Registry == null || LineType == null || !RuntimeReady) return false;
			var made = CreateLineMask(NamePrefix + key, Root, VanillaNodes(track), TrackClearWidth);
			if (made == null) return false;
			s_built[key] = made;
			Main.Log($"Spielgleis {track.name}: Vegetation auf {TrackClearWidth:0.#} m Breite freigeschnitten");
			return true;
		}

		public static bool RebuildVanilla(VanillaTrackEdit e, RailTrack track)
		{
			RemoveVanilla(e.id);
			return !e.clearVegetation || BuildVanilla(e, track);
		}

		public static void RemoveVanilla(string id)
		{
			string key = VanillaKey(id);
			if (s_built.TryGetValue(key, out var go) && go != null) DestroyMask(go);
			s_built.Remove(key);
		}

		// ------------------------------------------------------------------
		// Eigene Flächen: gedrehte quadratische Polygonmaske
		// ------------------------------------------------------------------

		private static Vector3[] Polygon(PlacedVegetationMask m)
		{
			Vector3 p = m.Position; // statisch
			Quaternion rot = Quaternion.Euler(0f, m.yaw, 0f);
			float r = Mathf.Max(0.5f, m.radius);
			return new[]
			{
				p + rot * new Vector3(-r, 0f, -r), p + rot * new Vector3(r, 0f, -r),
				p + rot * new Vector3(r, 0f, r), p + rot * new Vector3(-r, 0f, r),
			};
		}

		public static bool Build(PlacedVegetationMask m)
		{
			if (s_built.TryGetValue(m.id, out var go) && go != null) return true;
			if (!GameAccess.TracksReady || GameAccess.Registry == null || AreaType == null || !RuntimeReady) return false;
			var made = CreateAreaMask(NamePrefix + m.id, Root, Polygon(m));
			if (made == null) return false;
			s_built[m.id] = made;
			return true;
		}

		public static void Rebuild(PlacedVegetationMask m)
		{
			Remove(m.id);
			Build(m);
		}

		public static void Remove(string id)
		{
			if (s_built.TryGetValue(id, out var go) && go != null) DestroyMask(go);
			s_built.Remove(id);
		}

		/// <summary>Gespeicherte Masken einmalig nach VSP-Initialisierung bauen; kein periodischer Weltscan.</summary>
		public static void BuildSavedWhenReady()
		{
			if (s_waitingForRuntime) return;
			s_waitingForRuntime = true;
			int generation = s_worldGeneration;
			float deadline = Time.realtimeSinceStartup + 300f;
			void TryBuild()
			{
				if (generation != s_worldGeneration) return;
				if (!GameAccess.TracksReady || !RuntimeReady || !LoadOrder.Objects)
				{
					if (Time.realtimeSinceStartup < deadline) WorldLoader.RunDelayed(0.5f, TryBuild);
					else { s_waitingForRuntime = false; Main.Log("Vegetation Studio wurde nicht rechtzeitig bereit – Masken nicht gebaut."); }
					return;
				}
				s_waitingForRuntime = false;
				int made = 0;
				foreach (var m in LayoutStore.Layout.vegetationMasks)
					if ((!s_built.TryGetValue(m.id, out var area) || area == null) && Build(m)) made++;
				foreach (var t in LayoutStore.Layout.tracks)
					if (t.clearVegetation && (!s_built.TryGetValue(TrackKey(t.id), out var line) || line == null) && BuildTrack(t)) made++;
				foreach (var e in LayoutStore.Layout.vanillaTrackEdits)
					if (e.clearVegetation && (!s_built.TryGetValue(VanillaKey(e.id), out var vanilla) || vanilla == null))
					{
						var track = VanillaRemover.Find(e.name, e.Mid);
						if (track != null && BuildVanilla(e, track)) made++;
					}
				// Unter Klonen wird keine Vegetation mehr entfernt (nur noch über das Gras-Werkzeug).
				Main.Log($"Vegetationsmasken nach VSP-Start lokal registriert: {made}; globale Maskenregeln unverändert");
			}
			TryBuild();
		}

		/// <summary>Eigene Fläche, in der ein Punkt (statisch) liegt.</summary>
		public static PlacedVegetationMask? At(Vector3 staticPos)
		{
			foreach (var m in LayoutStore.Layout.vegetationMasks)
			{
				Vector3 local = Quaternion.Euler(0f, -m.yaw, 0f) * (staticPos - m.Position);
				if (Mathf.Abs(local.x) <= m.radius && Mathf.Abs(local.z) <= m.radius && Mathf.Abs(local.y) < 10f) return m;
			}
			return null;
		}

		/// <summary>Umriss (statisch) zum Anzeigen.</summary>
		public static List<Vector3> Outline(Vector3 center, float r, float yaw)
		{
			var rot = Quaternion.Euler(0f, yaw, 0f);
			var pts = new List<Vector3>();
			foreach (var c in new[] { new Vector3(-r, 0, -r), new Vector3(r, 0, -r), new Vector3(r, 0, r), new Vector3(-r, 0, r), new Vector3(-r, 0, -r) })
				pts.Add(center + rot * c + Vector3.up * 0.15f);
			return pts;
		}
	}
}
