using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TrackPlacer.World
{
	/// <summary>
	/// Ergänzt die Vegetations-Masken um das, was eine Maske NICHT erreicht:
	///  1. Fest „gemalte“ Büsche und Bäume aus dem Persistent Storage von Vegetation Studio. Für diese Instanzen
	///     gelten Masken nicht (nur prozedurale Regeln wie Gras werden maskiert) – darum blieben Büsche/Bäume
	///     stehen, während das Gras verschwand. Sie werden direkt aus den Speicher-Zellen genommen (nur im Speicher,
	///     nichts auf der Platte) und beim Entfernen der Fläche wieder eingesetzt.
	///  2. Pflanzen, die als normale Szenen-Objekte platziert sind (z. B. Büsche im Lokschuppen): ausblenden.
	/// Alles per Reflection (keine Abhängigkeit von der VSP-DLL).
	/// </summary>
	internal static class VegetationCutter
	{
		private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

		internal sealed class Record
		{
			public Vector2[] pts = Array.Empty<Vector2>();
			public float lineWidth;
			public Rect rect;
			public readonly List<(object system, IList list, object item)> storage = new List<(object, IList, object)>();
			public readonly List<GameObject> props = new List<GameObject>();
		}

		private static readonly List<Record> s_active = new List<Record>();
		private static bool s_sceneHook;

		public static void OnWorldUnloaded()
		{
			s_active.Clear();
			s_renderers = null;
			s_typeById = null;
		}

		// ------------------------------------------------------------------
		// Geometrie (statisch, Draufsicht)
		// ------------------------------------------------------------------

		private static bool Inside(Record r, Vector2 p)
		{
			if (!r.rect.Contains(p)) return false;
			if (r.lineWidth > 0f) return DistPolyline(p, r.pts, false) <= r.lineWidth * 0.5f;
			return PointInPolygon(p, r.pts) || DistPolyline(p, r.pts, true) <= 0.3f;
		}

		private static float DistPolyline(Vector2 p, Vector2[] pts, bool closed)
		{
			float best = float.MaxValue;
			int n = pts.Length;
			for (int i = 0; i < (closed ? n : n - 1); i++)
			{
				Vector2 a = pts[i], b = pts[(i + 1) % n], ab = b - a;
				float l2 = ab.sqrMagnitude;
				float t = l2 > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2) : 0f;
				best = Mathf.Min(best, (a + ab * t - p).sqrMagnitude);
			}
			return Mathf.Sqrt(best);
		}

		private static bool PointInPolygon(Vector2 p, Vector2[] poly)
		{
			bool c = false;
			for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
				if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
					p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x) c = !c;
			return c;
		}

		// ------------------------------------------------------------------
		// Anwenden / Zurücknehmen
		// ------------------------------------------------------------------

		/// <summary>Fläche (lineWidth = 0, Polygon) oder Linie (Breite) ausräumen. nodes statisch.</summary>
		public static Record Cut(string label, Vector3[] nodes, float lineWidth, Type? systemType)
		{
			var r = new Record { pts = nodes.Select(n => new Vector2(n.x, n.z)).ToArray(), lineWidth = lineWidth };
			float pad = lineWidth * 0.5f + 1f;
			float minX = r.pts.Min(p => p.x) - pad, maxX = r.pts.Max(p => p.x) + pad;
			float minY = r.pts.Min(p => p.y) - pad, maxY = r.pts.Max(p => p.y) + pad;
			r.rect = Rect.MinMaxRect(minX, minY, maxX, maxY);
			int stored = 0, props = 0;
			try { stored = CutStorage(r, systemType); } catch (Exception e) { Main.Log($"{label}: Büsche/Bäume (Vegetation-Speicher) nicht entfernbar: {e.Message}"); }
			try { props = HideProps(r); } catch (Exception e) { Main.Log($"{label}: Pflanzen-Objekte nicht ausblendbar: {e.Message}"); }
			s_active.Add(r);
			HookScenes();
			if (stored > 0 || props > 0 || lineWidth <= 0f)
				Main.Log($"{label}: {stored} Büsche/Bäume aus dem Vegetations-Speicher entfernt, {props} Pflanzen-Objekte ausgeblendet");
			return r;
		}

		public static void Restore(Record? r)
		{
			if (r == null) return;
			s_active.Remove(r);
			foreach (var (_, list, item) in r.storage)
				try { list.Add(item); } catch { /* Zelle weg (Welt entladen) */ }
			r.storage.Clear();
			foreach (var go in r.props) if (go != null) go.SetActive(true);
			r.props.Clear();
		}

		// ------------------------------------------------------------------
		// 1. Persistent Storage
		// ------------------------------------------------------------------

		private static Dictionary<string, string>? s_typeById;

		/// <summary>VegetationItemID → Typ (Grass/Plant/Tree/Objects/LargeObjects).</summary>
		private static string TypeOf(object system, string id)
		{
			if (s_typeById == null)
			{
				s_typeById = new Dictionary<string, string>();
				if (system.GetType().GetField("VegetationPackageProList", Inst)?.GetValue(system) is IList packages)
					foreach (var pkg in packages)
						if (pkg?.GetType().GetField("VegetationInfoList", Inst)?.GetValue(pkg) is IList infos)
							foreach (var info in infos)
							{
								if (info == null) continue;
								var t = info.GetType();
								string? vid = t.GetField("VegetationItemID", Inst)?.GetValue(info) as string;
								object? vt = t.GetField("VegetationType", Inst)?.GetValue(info);
								if (vid != null) s_typeById[vid] = vt?.ToString() ?? "";
							}
			}
			return s_typeById.TryGetValue(id, out var s) ? s : "";
		}

		private static int CutStorage(Record r, Type? systemType)
		{
			if (systemType == null) return 0;
			int n = 0;
			foreach (var system in Object.FindObjectsOfType(systemType))
			{
				if (system == null) continue;
				var st = system.GetType();
				object? storage = st.GetField("PersistentVegetationStorage", Inst)?.GetValue(system);
				object? package = storage?.GetType().GetField("PersistentVegetationStoragePackage", Inst)?.GetValue(storage);
				if (!(package?.GetType().GetField("PersistentVegetationCellList", Inst)?.GetValue(package) is IList cells) || cells.Count == 0) continue;
				Vector3 sysPos = st.GetProperty("VegetationSystemPosition", Inst)?.GetValue(system) is Vector3 v ? v : Vector3.zero;

				// Betroffene Zellen über den Quadtree des Systems (Positionen in Zellen sind relativ zu sysPos)
				object? quad = st.GetField("VegetationCellQuadTree", Inst)?.GetValue(system);
				var query = quad?.GetType().GetMethod("Query", Inst);
				if (quad == null || query == null) continue;
				var cellType = quad.GetType().GetGenericArguments().FirstOrDefault();
				if (cellType == null) continue;
				var found = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(cellType))!;
				// Quadtree in statischen Koordinaten (wie VSP selbst: RemoveVegetationItemInstance fragt mit der Position ab
				// und zieht erst für den Speicher VegetationSystemPosition ab)
				query.Invoke(quad, new object[] { r.rect, found });
				var fIndex = cellType.GetField("Index", Inst);
				var touched = new List<object>();

				foreach (var cell in found)
				{
					if (cell == null || !(fIndex?.GetValue(cell) is int idx) || idx < 0 || idx >= cells.Count) continue;
					object? pcell = cells[idx];
					if (!(pcell?.GetType().GetField("PersistentVegetationInfoList", Inst)?.GetValue(pcell) is IList infos)) continue;
					bool any = false;
					foreach (var info in infos)
					{
						if (info == null) continue;
						var it = info.GetType();
						string id = it.GetField("VegetationItemID", Inst)?.GetValue(info) as string ?? "";
						if (TypeOf(system, id) == "Grass") continue; // Gras macht die Maske
						if (!(it.GetField("VegetationItemList", Inst)?.GetValue(info) is IList items) || items.Count == 0) continue;
						FieldInfo? fPos = null;
						for (int i = items.Count - 1; i >= 0; i--)
						{
							object item = items[i];
							fPos ??= item.GetType().GetField("Position", Inst);
							if (fPos == null || !(fPos.GetValue(item) is Vector3 local)) continue;
							Vector3 p = local + sysPos; // statisch
							if (!Inside(r, new Vector2(p.x, p.z))) continue;
							r.storage.Add((system, items, item));
							items.RemoveAt(i);
							n++; any = true;
						}
					}
					if (any) touched.Add(cell);
				}

				// Betroffene Zellen neu aufbauen lassen
				var clear = st.GetMethod("ClearCache", Inst, null, new[] { cellType }, null);
				foreach (var cell in touched) clear?.Invoke(system, new[] { cell });
			}
			return n;
		}

		// ------------------------------------------------------------------
		// 2. Pflanzen als Szenen-Objekte
		// ------------------------------------------------------------------

		// Objektnamen: nur eindeutige Pflanzenwörter (nicht „tree“ → „street“, nicht „plant“ → „PowerPlant“)
		private static readonly string[] NameWords = { "bush", "shrub", "fern", "ivy", "hedge", "weed", "foliage", "leaves", "busch", "overgrown" };
		// Material-/Shadernamen: zusätzlich typische Vegetations-Shader
		private static readonly string[] MatWords = { "bush", "shrub", "fern", "ivy", "hedge", "weed", "foliage", "leaves", "leaf", "tree", "bark", "plant", "grass", "flower", "nature/", "vegetation", "speedtree" };
		private static Renderer[]? s_renderers;
		private static int s_renderersFrame = -1;

		private static bool Has(string s, string[] words)
		{
			string l = s.ToLowerInvariant().Replace("street", "").Replace("powerplant", "").Replace("plantform", "");
			foreach (var w in words) if (l.Contains(w)) return true;
			return false;
		}

		private static bool IsPlant(Renderer r)
		{
			if (Has(r.name, NameWords) || (r.transform.parent != null && Has(r.transform.parent.name, NameWords))) return true;
			foreach (var m in r.sharedMaterials)
				if (m != null && (Has(m.name, MatWords) || (m.shader != null && Has(m.shader.name, MatWords)))) return true;
			return false;
		}

		private static int HideProps(Record r, Renderer[]? only = null)
		{
			if (only == null && (s_renderers == null || s_renderersFrame != Time.frameCount))
			{
				s_renderers = Object.FindObjectsOfType<Renderer>();
				s_renderersFrame = Time.frameCount;
			}
			Vector3 shift = GameAccess.OriginShift;
			int n = 0;
			foreach (var rend in only ?? s_renderers!)
			{
				if (rend == null || !rend.enabled || !rend.gameObject.activeInHierarchy) continue;
				Vector3 c = rend.bounds.center - shift;
				if (!Inside(r, new Vector2(c.x, c.z))) continue;
				if (rend.bounds.size.magnitude > 30f) continue; // Gebäude/Gelände nie
				if (rend.GetComponentInParent<RailTrack>() != null || rend.GetComponentInParent<CloneBuilder.CloneMarker>() != null) continue;
				if (rend.name.StartsWith("[TP]") || rend.GetType().Name == "ParticleSystemRenderer" || !IsPlant(rend)) continue;
				// ganze Pflanze (mit LOD-Stufen) nehmen
				GameObject go = rend.gameObject;
				var lod = rend.GetComponentInParent<LODGroup>();
				if (lod != null && lod.transform != null && lod.GetComponentInParent<RailTrack>() == null && Size(lod.gameObject) < 30f) go = lod.gameObject;
				if (go.transform.parent == null) continue; // Szenen-Wurzel nie
				go.SetActive(false);
				r.props.Add(go);
				n++;
			}
			return n;
		}

		private static float Size(GameObject go)
		{
			Bounds? b = null;
			foreach (var x in go.GetComponentsInChildren<Renderer>())
			{
				if (b == null) b = x.bounds; else { var y = b.Value; y.Encapsulate(x.bounds); b = y; }
			}
			return b?.size.magnitude ?? 0f;
		}

		/// <summary>Nachgeladene Welt-Kacheln: Pflanzen-Objekte in aktiven Flächen erneut ausblenden.</summary>
		private static void HookScenes()
		{
			if (s_sceneHook) return;
			s_sceneHook = true;
			UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) =>
			{
				if (s_active.Count == 0) return;
				WorldLoader.RunAfterFrames(5, () =>
				{
					// nur die neue Szene durchsuchen – ein FindObjectsOfType über die ganze Welt bei jeder nachgeladenen
					// Kachel ergab beim Fahren spürbare Ruckler
					if (!scene.IsValid() || !scene.isLoaded) return;
					var list = new List<Renderer>();
					foreach (var root in scene.GetRootGameObjects()) list.AddRange(root.GetComponentsInChildren<Renderer>());
					var all = list.ToArray();
					foreach (var r in s_active.ToList())
						try { HideProps(r, all); } catch { /* egal */ }
				});
			};
		}
	}
}
