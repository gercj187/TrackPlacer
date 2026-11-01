using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TrackPlacer.Data;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TrackPlacer.World
{
	/// <summary>
	/// Szenenobjekte deaktivieren (Gebäude, Zäune, Masten …), gespeichert in der JSON.
	///  - Bearbeitungsmodus (Settings.editMode): Objekt halbtransparent, Collider aus → testen und rückgängig machen
	///  - Spielmodus: Objekt komplett ausgeblendet (SetActive(false))
	/// Gestreamte Objekte (Welt-Kacheln) können später geladen oder neu erzeugt werden; ein kleiner Läufer
	/// prüft darum alle zwei Sekunden, ob gespeicherte Objekte aufgetaucht sind, und wendet sie an.
	/// </summary>
	internal static class ObjectDisabler
	{
		private sealed class Live
		{
			public DisabledObject data = null!;
			public GameObject go = null!;
			public bool ghost;            // Bearbeitungsmodus angewendet
			public bool hidden;           // Spielmodus angewendet
			public readonly List<(Renderer r, Material[] mats, UnityEngine.Rendering.ShadowCastingMode shadows)> renderers = new List<(Renderer, Material[], UnityEngine.Rendering.ShadowCastingMode)>();
			public readonly List<(Collider c, bool enabled)> colliders = new List<(Collider, bool)>();
			public Bounds staticBounds;
			// Schattenwerfer außerhalb des Objekts (Schatten-Ersatzmodelle, Fern-LOD-Kacheln): Renderer, alter Modus, an?
			public readonly List<(Renderer r, UnityEngine.Rendering.ShadowCastingMode shadows, bool enabled)> twins = new List<(Renderer, UnityEngine.Rendering.ShadowCastingMode, bool)>();
			// Zugehörige Collider-Gruppen außerhalb des Objekts („Colliders“, „Cube“ …): mit ausschalten
			public readonly List<(GameObject go, bool active)> companions = new List<(GameObject, bool)>();
			public readonly List<(Collider c, bool enabled)> companionColliders = new List<(Collider, bool)>();
		}

		/// <summary>Hilfsobjekte, die nie einzeln auswählbar sein sollen (Collider-Gruppen, Platzhalter-Würfel).</summary>
		public static bool IsHelperName(string name)
		{
			string n = name.ToLowerInvariant();
			return n.StartsWith("cube") || n.Contains("collider");
		}

		private static void FindCompanions(Live l)
		{
			l.companions.Clear();
			l.companionColliders.Clear();
			foreach (var go in CloneHelper.ColliderCompanions(l.go))
				l.companions.Add((go, go.activeSelf));
		}

		private static void HideCompanions(Live l, bool hideCompletely)
		{
			foreach (var (go, _) in l.companions)
			{
				if (go == null) continue;
				if (hideCompletely) { go.SetActive(false); continue; }
				foreach (var c in go.GetComponentsInChildren<Collider>(true))
				{
					l.companionColliders.Add((c, c.enabled));
					c.enabled = false;
				}
			}
		}

		private static void RestoreCompanions(Live l)
		{
			foreach (var (c, enabled) in l.companionColliders) if (c != null) c.enabled = enabled;
			foreach (var (go, active) in l.companions) if (go != null) go.SetActive(active);
			l.companionColliders.Clear();
			l.companions.Clear();
		}

		// ------------------------------------------------------------------
		// Schatten: Viele Objekte haben ihren Schatten nicht (nur) am eigenen Modell, sondern an einem separaten
		// „Shadows Only“-Ersatzmodell oder an einer Kopie in der Fern-Kachel (Far_…). Die liegen woanders in der
		// Hierarchie und blieben bisher stehen. Gesucht wird: Renderer außerhalb des Objekts, deren Mitte in den
		// Grenzen des Objekts liegt, nicht größer als das Objekt, und die entweder nur Schatten werfen oder
		// dasselbe Mesh benutzen.
		// ------------------------------------------------------------------
		// Räumlicher Index aller Renderer (Zellen 50 m, Weltkoordinaten). Früher: FindObjectsOfType<Renderer>() je
		// deaktiviertem Objekt alle 10 s – beim Nachladen einer Kachel kostete das je Objekt ~140 ms (Ruckler).
		// Jetzt: einmal komplett aufbauen, danach nur neu geladene Szenen hinzufügen.
		private const float IndexCell = 50f;
		private static Dictionary<Vector2Int, List<Renderer>>? s_index;
		private static Vector3 s_indexShift;
		private static bool s_indexHook;

		private static Vector2Int CellOf(Vector3 p) => new Vector2Int(Mathf.FloorToInt(p.x / IndexCell), Mathf.FloorToInt(p.z / IndexCell));

		private static void IndexAdd(Renderer r)
		{
			if (r == null) return;
			var k = CellOf(r.bounds.center);
			if (!s_index!.TryGetValue(k, out var list)) s_index[k] = list = new List<Renderer>();
			list.Add(r);
		}

		private static void EnsureIndex()
		{
			if (!s_indexHook)
			{
				s_indexHook = true;
				UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) =>
				{
					if (s_index == null || !scene.IsValid()) return;
					foreach (var root in scene.GetRootGameObjects())
						foreach (var r in root.GetComponentsInChildren<Renderer>(true)) IndexAdd(r);
				};
			}
			// Origin-Shift verschiebt die Welt → Index passt nicht mehr, neu aufbauen
			if (s_index != null && (GameAccess.OriginShift - s_indexShift).sqrMagnitude < 1f) return;
			s_index = new Dictionary<Vector2Int, List<Renderer>>();
			s_indexShift = GameAccess.OriginShift;
			foreach (var r in Object.FindObjectsOfType<Renderer>()) IndexAdd(r);
		}

		/// <summary>Renderer, deren Mitte (beim Eintragen) in der Nähe des Bereichs lag.</summary>
		private static IEnumerable<Renderer> RenderersNear(Bounds area)
		{
			EnsureIndex();
			var a = CellOf(area.min - Vector3.one * IndexCell * 0.5f);
			var b = CellOf(area.max + Vector3.one * IndexCell * 0.5f);
			var result = new List<Renderer>();
			for (int x = a.x; x <= b.x; x++)
				for (int y = a.y; y <= b.y; y++)
					if (s_index!.TryGetValue(new Vector2Int(x, y), out var list))
					{
						list.RemoveAll(r => r == null); // zerstörte (entladene Kacheln) aufräumen
						result.AddRange(list);
					}
			return result;
		}

		private static void FindTwins(Live l)
		{
			l.twins.Clear();
			var wb = WorldBounds(l.go);
			if (!wb.HasValue) return;
			Bounds area = wb.Value;
			area.Expand(0.5f);
			float maxSize = wb.Value.size.magnitude * 1.2f + 0.5f;
			var meshes = new HashSet<string>();
			foreach (var mf in l.go.GetComponentsInChildren<MeshFilter>(true))
			{
				var m = CloneHelper.OriginalMesh(mf);
				if (m != null) meshes.Add(m.name.Replace(" Instance", ""));
			}
			var rejected = new List<string>();
			foreach (var r in RenderersNear(area))
			{
				if (r == null || r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.Off) continue;
				// billige Prüfungen zuerst
				Bounds rb = r.bounds;
				if (!area.Contains(rb.center) || rb.size.magnitude > maxSize) continue;
				if (SkipRenderer(r) || r.transform.IsChildOf(l.go.transform)) continue;
				// Schattenwerfer echter Spielfunktionen (Drehscheibe, Gleise, Pitstops …) nie anfassen
				if (!l.go.transform.IsChildOf(r.transform) && CloneHelper.IsFunctional(r.transform) && !CloneHelper.IsFunctional(l.go.transform)) continue;
				// eigene Objekte (Klone, Schilder, Abdeckungen …) nie mitnehmen – ein Klon liegt oft genau auf dem Original
				if (r.GetComponentInParent<CloneBuilder.CloneMarker>() != null || r.name.StartsWith("[TP]") || IsOwnObject(r.transform)) continue;
				bool shadowOnly = r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
				bool sameMesh = false;
				var mf = r.GetComponent<MeshFilter>();
				if (!shadowOnly && mf != null)
				{
					var m = CloneHelper.OriginalMesh(mf);
					// nur ganze Kopien (Fern-Kachel), keine gleichen Kleinteile anderer Objekte im selben Bereich
					sameMesh = m != null && meshes.Contains(m.name.Replace(" Instance", "")) && rb.size.magnitude >= wb.Value.size.magnitude * 0.5f;
				}
				// Fern-Kachel (Far_…): vereinfachte Kopie des Gebäudes mit anderem Mesh-Namen – Größe ähnlich
				bool farCopy = !shadowOnly && !sameMesh && UnderFarTile(r.transform) && r.transform.root != l.go.transform.root
					&& rb.size.magnitude >= wb.Value.size.magnitude * 0.3f;
				// Kopie heißt: liegt an DERSELBEN Stelle. Ein gleiches Nachbarobjekt (z. B. die nächste Absperrung oder
				// Laterne daneben) hat seine Mitte versetzt – früher wurde dessen Modell mit ausgeblendet, sein Collider
				// blieb stehen → unsichtbares Hindernis.
				float off = Vector3.Distance(rb.center, wb.Value.center);
				bool samePlace = off <= Mathf.Max(0.3f, wb.Value.size.magnitude * 0.15f);
				if ((sameMesh || farCopy) && !samePlace) { sameMesh = false; farCopy = false; }
				if (shadowOnly || sameMesh || farCopy) l.twins.Add((r, r.shadowCastingMode, r.enabled));
				else if (rejected.Count < 12) rejected.Add($"{CloneHelper.PathOf(r.transform)} [{r.shadowCastingMode}, {rb.size.magnitude:0.0} m]");
			}
			if (l.twins.Count > 0) Main.Log($"Objekt {l.go.name}: {l.twins.Count} zusätzliche Schattenwerfer/Kopien mit ausgeblendet: " + string.Join(", ", l.twins.Select(t => t.r != null ? CloneHelper.PathOf(t.r.transform) : "?")));
			// Diagnose für hartnäckige Schatten: was liegt sonst noch im Bereich und wirft Schatten?
			if (rejected.Count > 0) Main.Log($"Objekt {l.go.name}: weitere Schattenwerfer im Bereich (nicht ausgeblendet): " + string.Join(" | ", rejected));
		}

		private static bool UnderFarTile(Transform t)
		{
			for (var p = t; p != null; p = p.parent)
				if (p.name.StartsWith("Far", StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}

		private static bool IsOwnObject(Transform t)
		{
			for (var p = t; p != null; p = p.parent)
				if (p.name.StartsWith("[TP]") || p.name.StartsWith("[TrackPlacer]")) return true;
			return false;
		}

		/// <summary>
		/// Klonen von deaktivierten Objekten: Halbtransparenz/ausgeschaltete Collider würden mitkopiert. Darum die
		/// betroffenen Einträge (Quelle selbst, ein Elternteil oder Teile darin) kurz zurücksetzen und danach wieder
		/// anwenden. Im Spielmodus (ausgeblendet) ist nichts nötig – inaktive Quellen werden normal kopiert.
		/// </summary>
		public static List<object> SuspendGhosts(GameObject source)
		{
			var list = new List<object>();
			foreach (var l in s_live.Values)
			{
				if (l.go == null || !l.ghost) continue;
				if (!source.transform.IsChildOf(l.go.transform) && !l.go.transform.IsChildOf(source.transform)) continue;
				RestoreGhostOnly(l);
				list.Add(l);
			}
			return list;
		}

		// ------------------------------------------------------------------
		// Klone: Zustand des Originals NICHT übernehmen
		// Eine Kopie entsteht immer aus dem aktuellen Szenenobjekt. Ist an der Quelle (oder an Teilen darin, z. B.
		// der Drehscheibe im Lokschuppen) gerade „Objekt deaktivieren“ angewendet – halbtransparent im
		// Bearbeitungsmodus, ausgeschaltet im Spielmodus –, landet dieser Zustand sonst in der Kopie. Nach dem
		// Laden passiert das zuverlässig, weil die Deaktivierungen vor den Klonen angewendet werden.
		// Darum wird nach dem Kopieren jedes betroffene Teil in der Kopie auf den gemerkten Originalzustand gesetzt.
		// ------------------------------------------------------------------

		/// <summary>Pfad von root zu t über Geschwister-Indizes (Namen sind nicht eindeutig).</summary>
		private static List<int>? IndexPath(Transform root, Transform t)
		{
			var path = new List<int>();
			for (var x = t; x != root; x = x.parent)
			{
				if (x == null) return null;
				path.Insert(0, x.GetSiblingIndex());
			}
			return path;
		}

		private static Transform? Follow(Transform root, List<int> path)
		{
			var x = root;
			foreach (int i in path)
			{
				if (i < 0 || i >= x.childCount) return null;
				x = x.GetChild(i);
			}
			return x;
		}

		/// <summary>Gegenstück einer Komponente der Quelle in der Kopie (gleicher Pfad, gleicher Index unter gleichem Typ).</summary>
		private static T? Counterpart<T>(Transform srcRoot, Transform cloneRoot, T comp) where T : Component
		{
			if (comp == null || !comp.transform.IsChildOf(srcRoot)) return null;
			var path = IndexPath(srcRoot, comp.transform);
			if (path == null) return null;
			var t = Follow(cloneRoot, path);
			if (t == null) return null;
			var srcAll = comp.GetComponents<T>();
			int idx = Array.IndexOf(srcAll, comp);
			var dst = t.GetComponents<T>();
			return idx >= 0 && idx < dst.Length ? dst[idx] : null;
		}

		/// <summary>Teile der Kopie, die nur wegen „Objekt deaktivieren“ an der Quelle aus/halbtransparent sind, zurücksetzen.</summary>
		public static int RestoreInClone(GameObject source, GameObject clone)
		{
			int n = 0;
			Transform sr = source.transform, cr = clone.transform;
			foreach (var l in s_live.Values)
			{
				if (l.go == null) continue;
				bool inside = l.go.transform.IsChildOf(sr);   // Quelle selbst oder ein Teil davon
				bool above = sr.IsChildOf(l.go.transform);    // ein Elternobjekt der Quelle ist deaktiviert
				if (inside && l.hidden && l.go.transform != sr)
				{
					var path = IndexPath(sr, l.go.transform);
					var t = path != null ? Follow(cr, path) : null;
					if (t != null && !t.gameObject.activeSelf) { t.gameObject.SetActive(true); n++; }
				}
				if (inside || above)
				{
					foreach (var (r, mats, shadows) in l.renderers)
					{
						var d = Counterpart(sr, cr, r);
						if (d != null) { d.sharedMaterials = mats; d.shadowCastingMode = shadows; n++; }
					}
					foreach (var (c, enabled) in l.colliders)
					{
						var d = Counterpart(sr, cr, c);
						if (d != null) { d.enabled = enabled; n++; }
					}
				}
				// Schattenwerfer/Collider-Gruppen dieses Eintrags, die zufällig IN der Quelle liegen
				foreach (var (r, shadows, enabled) in l.twins)
				{
					var d = Counterpart(sr, cr, r);
					if (d != null) { d.shadowCastingMode = shadows; d.enabled = enabled; n++; }
				}
				foreach (var (go, active) in l.companions)
				{
					if (go == null || !go.transform.IsChildOf(sr)) continue;
					var path = IndexPath(sr, go.transform);
					var t = path != null ? Follow(cr, path) : null;
					if (t != null) { t.gameObject.SetActive(active); n++; }
				}
				foreach (var (c, enabled) in l.companionColliders)
				{
					var d = Counterpart(sr, cr, c);
					if (d != null) { d.enabled = enabled; n++; }
				}
			}
			return n;
		}

		/// <summary>
		/// Zugehörige Collider-Gruppen einer deaktivierten Quelle, wie sie VOR dem Ausblenden gefunden wurden.
		/// Ausgeblendet (inaktiv bzw. Collider aus) liefert die geometrische Suche nichts mehr – der Klon stünde
		/// sonst nach dem Laden ohne seine Wände da.
		/// </summary>
		public static List<GameObject>? KnownCompanions(GameObject source)
		{
			foreach (var l in s_live.Values)
				if (l.go == source && l.companions.Count > 0)
					return l.companions.Where(x => x.go != null).Select(x => x.go).ToList();
			return null;
		}

		/// <summary>War diese Collider-Gruppe vor „Objekt deaktivieren“ aktiv? (für mitkopierte Nachbar-Collider)</summary>
		public static bool OriginalActive(GameObject go)
		{
			foreach (var l in s_live.Values)
				foreach (var (cgo, active) in l.companions)
					if (cgo == go) return active;
			return go.activeSelf;
		}

		public static void ResumeGhosts(List<object> suspended)
		{
			foreach (var o in suspended) if (o is Live l && l.go != null) { ApplyGhost(l); HideTwins(l, hideCompletely: false); }
		}

		private static void RestoreGhostOnly(Live l)
		{
			foreach (var (r, mats, shadows) in l.renderers)
				if (r != null) { r.sharedMaterials = mats; r.shadowCastingMode = shadows; }
			foreach (var (c, enabled) in l.colliders)
				if (c != null) c.enabled = enabled;
			l.renderers.Clear();
			l.colliders.Clear();
			l.ghost = false;
		}

		private static void HideTwins(Live l, bool hideCompletely)
		{
			foreach (var (r, shadows, _) in l.twins)
			{
				if (r == null) continue;
				r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
				// reine Schattenwerfer immer aus, sichtbare Kopien (Fern-Kachel) nur im Spielmodus
				if (hideCompletely || shadows == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly) r.enabled = false;
			}
		}

		private static void RestoreTwins(Live l)
		{
			foreach (var (r, shadows, enabled) in l.twins)
				if (r != null) { r.shadowCastingMode = shadows; r.enabled = enabled; }
			l.twins.Clear();
		}

		private static readonly Dictionary<string, Live> s_live = new Dictionary<string, Live>();
		private static readonly Dictionary<Material, Material> s_ghostMats = new Dictionary<Material, Material>();
		private static Material? s_ghostPlain;
		private static readonly Color GhostColor = new Color(0.55f, 0.85f, 1f, 0.35f);

		// ------------------------------------------------------------------
		// Auswahl
		// ------------------------------------------------------------------

		/// <summary>Darf das Objekt deaktiviert werden? Gleise, Bahnhöfe, Fahrzeuge, Gelände und eigene Objekte nicht.</summary>
		public static bool CanDisable(GameObject go, out string reason)
		{
			reason = "";
			if (go == null) { reason = Tr.T("nichts", "nothing"); return false; }
			if (HasNamed(go, "Terrain") || HasNamed(go, "TerrainCollider")) { reason = Tr.T("Gelände", "terrain"); return false; }
			if (go.GetComponentInChildren<RailTrack>(true) != null) { reason = Tr.T("enthält Gleise", "contains tracks"); return false; }
			if (go.GetComponentInChildren<StationController>(true) != null) { reason = Tr.T("Bahnhof", "station"); return false; }
			if (go.GetComponentInParent<TrainCar>() != null || go.GetComponentInChildren<TrainCar>(true) != null) { reason = Tr.T("Fahrzeug", "vehicle"); return false; }
			if (go.GetComponentInChildren<Camera>(true) != null) { reason = Tr.T("Kamera/Spieler", "camera/player"); return false; }
			if (go.name.StartsWith(TrackBuilder.NamePrefix) || go.GetComponentInParent<MachineBuilder.MachineMarker>() != null) { reason = Tr.T("eigenes Objekt – „Entfernen“ nutzen", "own object – use 'Remove'"); return false; }
			if (go.transform.parent == null) { reason = Tr.T("Szenen-Wurzel", "scene root"); return false; }
			return true;
		}

		/// <summary>Komponente per Typname suchen (ohne Verweis auf Terrain-/Partikel-Module).</summary>
		private static bool HasNamed(GameObject go, string typeName)
		{
			foreach (var c in go.GetComponentsInChildren<Component>(true))
				if (c != null && c.GetType().Name == typeName) return true;
			return false;
		}

		internal static bool SkipRenderer(Renderer r) => r is LineRenderer || r is TrailRenderer || r.GetType().Name == "ParticleSystemRenderer";

		/// <summary>Kandidaten vom getroffenen Objekt nach oben (Teil → Gruppe → Gebäude). Es gibt bewusst kein Größenlimit.</summary>
		/// <param name="skipColliderOnly">Klonen: reine Collider-Objekte (ohne sichtbares Modell) nicht anbieten – sie
		/// werden beim Kopieren des sichtbaren Objekts automatisch mitgenommen (CloneHelper.ColliderCompanions).</param>
		public static List<GameObject> CandidateChain(Transform hit, out int defaultIndex, float maxSize = float.PositiveInfinity, bool skipColliderOnly = false)
		{
			var list = new List<GameObject>();
			defaultIndex = 0;
			for (Transform? t = hit; t != null && t.parent != null; t = t.parent)
			{
				if (skipColliderOnly && (!HasVisible(t.gameObject) || IsHelperName(t.name)))
				{
					if (!CanDisable(t.gameObject, out _)) break;
					continue; // weiter nach oben, bis ein sichtbares Objekt kommt
				}
				float size = SizeOf(t.gameObject);
				if (size > maxSize) break;
				if (!CanDisable(t.gameObject, out _)) break;
				list.Add(t.gameObject);
				if (size <= 30f) defaultIndex = list.Count - 1; // größtes noch „handliches“ Objekt vorschlagen
			}
			return list;
		}

		/// <summary>Hat das Objekt (samt Kindern) ein sichtbares Modell?</summary>
		public static bool HasVisible(GameObject go)
		{
			foreach (var r in go.GetComponentsInChildren<Renderer>())
				if (r != null && r.enabled && !SkipRenderer(r)) return true;
			return false;
		}

		public static float SizeOf(GameObject go)
		{
			var b = WorldBounds(go);
			return b.HasValue ? Mathf.Max(b.Value.size.x, b.Value.size.y, b.Value.size.z) : 0f;
		}

		private static Bounds? WorldBounds(GameObject go)
		{
			Bounds? b = null;
			foreach (var r in go.GetComponentsInChildren<Renderer>())
			{
				if (SkipRenderer(r)) continue;
				if (b == null) b = r.bounds; else { var x = b.Value; x.Encapsulate(r.bounds); b = x; }
			}
			if (b == null)
				foreach (var c in go.GetComponentsInChildren<Collider>())
				{
					if (b == null) b = c.bounds; else { var x = b.Value; x.Encapsulate(c.bounds); b = x; }
				}
			return b;
		}

		/// <summary>Drahtgitter der Objektgrenzen (statisch) als eine Linie, zum Markieren.</summary>
		public static List<Vector3> WireBox(GameObject go)
		{
			// In der Ausrichtung des Objekts (gedrehte Box) statt achsparallel – passt eng um schräg stehende Objekte
			Bounds lb = CloneHelper.LocalBounds(go.transform);
			Quaternion rot = go.transform.rotation;
			Vector3 origin = go.transform.position - GameAccess.OriginShift;
			Vector3 mn = lb.min, mx = lb.max;
			Vector3 P(int x, int y, int z) => origin + rot * new Vector3(x == 0 ? mn.x : mx.x, y == 0 ? mn.y : mx.y, z == 0 ? mn.z : mx.z);
			return new List<Vector3>
			{
				P(0,0,0), P(1,0,0), P(1,0,1), P(0,0,1), P(0,0,0),
				P(0,1,0), P(1,1,0), P(1,0,0), P(1,1,0),
				P(1,1,1), P(1,0,1), P(1,1,1),
				P(0,1,1), P(0,0,1), P(0,1,1), P(0,1,0),
			};
		}

		public static List<Vector3> WireBox(Bounds sb)
		{
			Vector3 mn = sb.min, mx = sb.max;
			Vector3 P(int x, int y, int z) => new Vector3(x == 0 ? mn.x : mx.x, y == 0 ? mn.y : mx.y, z == 0 ? mn.z : mx.z);
			return new List<Vector3>
			{
				P(0,0,0), P(1,0,0), P(1,0,1), P(0,0,1), P(0,0,0),
				P(0,1,0), P(1,1,0), P(1,0,0), P(1,1,0),
				P(1,1,1), P(1,0,1), P(1,1,1),
				P(0,1,1), P(0,0,1), P(0,1,1), P(0,1,0),
			};
		}

		public static string Label(GameObject go) => $"{Short(go.name)} ({SizeOf(go):0} m)";
		private static string Short(string n) => n.Length > 22 ? n.Substring(0, 22) : n;

		/// <summary>Deaktiviertes Objekt im Bearbeitungsmodus, das der Laser durchquert (es hat keine Collider mehr).</summary>
		public static string? FindGhost(Ray worldRay, float maxDist, out float dist)
		{
			// Genauer als die Gesamtgrenzen: sonst „verschluckt“ ein großes deaktiviertes Gebäude alles, was darin liegt
			// (der Laser startet sogar innerhalb seiner Grenzen → Abstand 0). Daher je Einzelteil (Renderer) prüfen,
			// Teile überspringen, in denen der Laser beginnt, und bei gleichem Abstand das kleinere Teil nehmen.
			string? best = null;
			dist = maxDist;
			float bestSize = float.MaxValue;
			Vector3 shift = GameAccess.OriginShift;
			var staticRay = new Ray(worldRay.origin - shift, worldRay.direction);
			foreach (var kv in s_live)
			{
				var live = kv.Value;
				if (!live.ghost || live.go == null) continue;
				if (!live.staticBounds.IntersectRay(staticRay, out _)) continue; // grobe Vorauswahl
				foreach (var (r, _, _) in live.renderers)
				{
					if (r == null) continue;
					// Gedrehte Box des Meshes statt achsparalleler Weltgrenzen: sonst ragt die Box eines schräg
					// stehenden deaktivierten Objekts weit über das Nachbarobjekt und „schluckt“ den Laser.
					if (!RayHitsMeshBox(r, worldRay, out float d, out float size) || d > maxDist) continue;
					bool closer = d < dist - 0.05f;
					bool sameButSmaller = Mathf.Abs(d - dist) <= 0.05f && size < bestSize;
					if (best == null || closer || sameButSmaller) { dist = d; best = kv.Key; bestSize = size; }
				}
			}
			return best;
		}

		/// <summary>Laser gegen die gedrehte Mesh-Box eines Renderers (lokale Grenzen). Start innerhalb der Box zählt nicht.</summary>
		private static bool RayHitsMeshBox(Renderer r, Ray worldRay, out float dist, out float size)
		{
			dist = 0f; size = 0f;
			var mf = r.GetComponent<MeshFilter>();
			Mesh? mesh = mf != null ? CloneHelper.OriginalMesh(mf) : null;
			if (mesh == null)
			{
				Bounds rb = r.bounds;
				size = rb.size.sqrMagnitude;
				return !rb.Contains(worldRay.origin) && rb.IntersectRay(worldRay, out dist);
			}
			Transform t = r.transform;
			Bounds lb = mesh.bounds;
			var localRay = new Ray(t.InverseTransformPoint(worldRay.origin), t.InverseTransformDirection(worldRay.direction));
			size = Vector3.Scale(lb.size, t.lossyScale).sqrMagnitude;
			if (lb.Contains(localRay.origin) || !lb.IntersectRay(localRay, out float ld)) return false;
			dist = Vector3.Distance(worldRay.origin, t.TransformPoint(localRay.GetPoint(ld)));
			return true;
		}

		public static string GhostLabel(string id) => s_live.TryGetValue(id, out var l) && l.go != null ? Label(l.go) : "Objekt";
		public static List<Vector3> GhostWire(string id) => s_live.TryGetValue(id, out var l) ? (l.go != null ? WireBox(l.go) : WireBox(l.staticBounds)) : new List<Vector3>();

		// ------------------------------------------------------------------
		// Deaktivieren / Aktivieren
		// ------------------------------------------------------------------

		private static string PathOf(Transform? t) => t == null ? "" : (t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name);

		public static bool Disable(GameObject go, out string error)
		{
			error = "";
			if (!CanDisable(go, out error)) return false;
			var d = new DisabledObject
			{
				parentPath = PathOf(go.transform.parent),
				name = go.name,
				position = PlacedTrack.FromVec(go.transform.position - GameAccess.OriginShift),
			};
			LayoutStore.Layout.disabledObjects.Add(d);
			LayoutStore.SaveQuiet();
			var live = new Live { data = d, go = go };
			s_live[d.id] = live;
			ApplyMode(live);
			Main.Log($"Objekt deaktiviert: {d.parentPath}/{d.name}");
			return true;
		}

		public static void Enable(string id)
		{
			if (s_live.TryGetValue(id, out var live)) Restore(live);
			s_live.Remove(id);
			LayoutStore.Layout.disabledObjects.RemoveAll(x => x.id == id);
			LayoutStore.SaveQuiet();
		}

		/// <summary>Alle wieder einblenden und die Liste leeren.</summary>
		public static void EnableAll()
		{
			foreach (var l in s_live.Values) Restore(l);
			s_live.Clear();
			LayoutStore.Layout.disabledObjects.Clear();
			LayoutStore.SaveQuiet();
		}

		/// <summary>Modus gewechselt (Bearbeiten ↔ Spiel): alle neu anwenden.</summary>
		public static void ModeChanged()
		{
			foreach (var l in s_live.Values) { Restore(l); ApplyMode(l); }
		}

		private static void ApplyMode(Live l)
		{
			if (l.go == null) return;
			var b = WorldBounds(l.go);
			if (b.HasValue) l.staticBounds = new Bounds(b.Value.center - GameAccess.OriginShift, b.Value.size);
			FindTwins(l);
			FindCompanions(l); // vor dem Ausblenden – braucht die sichtbaren Grenzen des Objekts
			if (Main.Settings.editMode) { ApplyGhost(l); HideTwins(l, hideCompletely: false); HideCompanions(l, hideCompletely: false); }
			else { l.go.SetActive(false); l.hidden = true; HideTwins(l, hideCompletely: true); HideCompanions(l, hideCompletely: true); }
			if (l.companions.Count > 0) Main.Log($"Objekt {l.go.name}: {l.companions.Count} zugehörige Collider-Gruppe(n) mit ausgeschaltet");
		}

		private static void ApplyGhost(Live l)
		{
			l.renderers.Clear();
			l.colliders.Clear();
			foreach (var r in l.go.GetComponentsInChildren<Renderer>(true))
			{
				if (SkipRenderer(r)) continue;
				var mats = r.sharedMaterials;
				l.renderers.Add((r, mats, r.shadowCastingMode));
				r.sharedMaterials = mats.Select(Ghost).ToArray();
				r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			}
			foreach (var c in l.go.GetComponentsInChildren<Collider>(true))
			{
				l.colliders.Add((c, c.enabled));
				c.enabled = false;
			}
			l.ghost = true;
		}

		private static void Restore(Live l)
		{
			RestoreTwins(l);
			RestoreCompanions(l);
			if (l.go == null) return;
			if (l.hidden) { l.go.SetActive(true); l.hidden = false; }
			if (l.ghost)
			{
				foreach (var (r, mats, shadows) in l.renderers)
					if (r != null) { r.sharedMaterials = mats; r.shadowCastingMode = shadows; }
				foreach (var (c, enabled) in l.colliders)
					if (c != null) c.enabled = enabled;
				l.renderers.Clear();
				l.colliders.Clear();
				l.ghost = false;
			}
		}

		/// <summary>Halbtransparente Kopie (Textur bleibt erkennbar), je Original-Material einmal.</summary>
		internal static Material Ghost(Material? orig)
		{
			if (orig == null) return GhostPlain;
			if (s_ghostMats.TryGetValue(orig, out var g) && g != null) return g;
			var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Transparent");
			if (sh == null) return GhostPlain;
			g = new Material(sh) { name = "TP_Ghost_" + orig.name, color = GhostColor, renderQueue = 3000 };
			try { if (orig.HasProperty("_MainTex")) g.mainTexture = orig.mainTexture; } catch { /* egal */ }
			s_ghostMats[orig] = g;
			return g;
		}

		private static Material GhostPlain
		{
			get
			{
				if (s_ghostPlain != null) return s_ghostPlain;
				var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Hidden/Internal-Colored");
				s_ghostPlain = new Material(sh) { name = "TP_Ghost", color = GhostColor, renderQueue = 3000 };
				return s_ghostPlain;
			}
		}

		// ------------------------------------------------------------------
		// Wiederfinden (beim Laden und für nachgeladene Welt-Kacheln)
		// ------------------------------------------------------------------

		private static GameObject? Resolve(DisabledObject d)
		{
			Vector3 shift = GameAccess.OriginShift;
			IEnumerable<Transform> candidates;
			if (string.IsNullOrEmpty(d.parentPath))
			{
				var go = GameObject.Find("/" + d.name);
				candidates = go != null ? new[] { go.transform } : Array.Empty<Transform>();
			}
			else
			{
				var parent = GameObject.Find("/" + d.parentPath);
				if (parent == null) return null;
				candidates = parent.transform.Cast<Transform>().Where(c => c.name == d.name);
			}
			Transform? best = null;
			float bestD = 3f * 3f;
			foreach (var c in candidates)
			{
				float dd = ((c.position - shift) - d.Position).sqrMagnitude;
				if (dd < bestD) { bestD = dd; best = c; }
			}
			return best != null ? best.gameObject : null;
		}

		/// <summary>Fehlende/neu geladene Objekte suchen und anwenden. Billig: nur für gespeicherte Einträge.</summary>
		public static void Tick()
		{
			if (!GameAccess.TracksReady) return; // keine Welt geladen
			foreach (var d in LayoutStore.Layout.disabledObjects)
			{
				if (s_live.TryGetValue(d.id, out var l) && l.go != null) continue; // noch da
				var go = Resolve(d);
				if (go == null) continue;
				var live = new Live { data = d, go = go };
				s_live[d.id] = live;
				ApplyMode(live);
			}
		}

		/// <summary>Nur einen Eintrag prüfen; verhindert einen Block aus vielen GameObject.Find-Aufrufen.</summary>
		public static void TickOne(int index)
		{
			if (!GameAccess.TracksReady || index < 0 || index >= LayoutStore.Layout.disabledObjects.Count) return;
			var d = LayoutStore.Layout.disabledObjects[index];
			if (s_live.TryGetValue(d.id, out var l) && l.go != null) return;
			var go = Resolve(d);
			if (go == null) return;
			var live = new Live { data = d, go = go };
			s_live[d.id] = live;
			ApplyMode(live);
		}

		public static void OnWorldUnloaded() { s_live.Clear(); s_index = null; }

		/// <summary>Läufer für nachgeladene Weltobjekte. Teure Suchen werden auf einzelne Frames verteilt.</summary>
		private sealed class Runner : MonoBehaviour
		{
			private IEnumerator Start()
			{
				while (true)
				{
					// Erst die Welt erkennen – sonst würden Klone vor dem Zurücksetzen doppelt gebaut.
					try { if (GameAccess.TracksReady) TrackBuilder.EnsureWorld(); } catch (Exception e) { Main.Log("Welt: " + e.Message); }
					// Lade-Reihenfolge: erst Gleise, dann IDs/Lademaschinen, dann Objekte
					if (!LoadOrder.Objects) { yield return new WaitForSecondsRealtime(1f); continue; }
					int clones = LayoutStore.Layout.clones.Count;
					int disabled = LayoutStore.Layout.disabledObjects.Count;
					int modules = LayoutStore.Layout.pitstopModules.Count;
					for (int i = 0; i < clones; i++)
					{
						int ci = i;
						Timed("Klon " + (ci < LayoutStore.Layout.clones.Count ? LayoutStore.Layout.clones[ci].srcName : "?"), () => CloneBuilder.TickOne(ci));
						yield return null;
					}
					for (int i = 0; i < disabled; i++)
					{
						int di = i;
						Timed("Objekt " + (di < LayoutStore.Layout.disabledObjects.Count ? LayoutStore.Layout.disabledObjects[di].name : "?"), () => TickOne(di));
						yield return null;
					}
					for (int i = 0; i < modules; i++)
					{
						int pi = i;
						Timed("Pitstop-Modul", () => PitstopEditor.TickOne(pi));
						yield return null;
					}
					Timed("Fern-Gleise", FarTrackHider.Tick);
					Timed("Prellböcke", BufferBuilder.RetryMissing);
					yield return new WaitForSecondsRealtime(2f);
				}
			}
		}

		private static readonly Dictionary<string, int> s_slowLogged = new Dictionary<string, int>();

		/// <summary>Läufer-Schritt ausführen; dauert er spürbar (≥ 8 ms = Ruckler bei 100 FPS), ins Log (je Schritt max. 3×).</summary>
		private static void Timed(string what, Action step)
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			try { step(); } catch (Exception e) { Main.Log(what + ": " + e.Message); }
			sw.Stop();
			if (sw.Elapsed.TotalMilliseconds < 8.0) return;
			s_slowLogged.TryGetValue(what, out int n);
			if (n >= 3) return;
			s_slowLogged[what] = n + 1;
			Main.Log($"Leistung: {what} dauerte {sw.Elapsed.TotalMilliseconds:0} ms (Ruckler)");
		}

		private static Runner? s_runner;
		public static void StartRunner()
		{
			if (s_runner != null) return;
			var go = new GameObject("[TrackPlacer] ObjectDisabler");
			Object.DontDestroyOnLoad(go);
			s_runner = go.AddComponent<Runner>();
		}
	}
}
