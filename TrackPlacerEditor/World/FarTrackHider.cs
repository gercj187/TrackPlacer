using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TrackPlacer.World
{
	/// <summary>
	/// Fern-Ansicht entfernter Spielgleise. In der Ferne zeigt das Spiel keine echten Gleise, sondern fest
	/// eingebaute, vereinfachte Modelle in den Fern-Kacheln (Szenen-Wurzeln „Far__x12_z2_…“). Die wissen nichts
	/// von entfernten Gleisen.
	/// Lösung: Jede geladene Fern-Kachel wird einmal durchsucht. Ein Mesh, dessen Punkte entlang eines entfernten
	/// Gleises liegen, wird
	///  - ganz ausgeblendet, wenn es NUR aus dem entfernten Gleis besteht,
	///  - sonst als Kopie ohne die Dreiecke entlang des entfernten Gleises ersetzt (Dreiecke an erhaltenen
	///    Spielgleisen bleiben stehen).
	/// Was nicht bearbeitbar ist (Mesh nicht lesbar, statisch zusammengefasst), steht mit Pfad im Log.
	/// </summary>
	internal static class FarTrackHider
	{
		private const float Near = 2.4f;      // Abstand (waagerecht) zur Gleismitte, der noch „auf dem Gleis“ gilt
		private const float KeepNear = 1.6f;  // Dreiecke an erhaltenen Gleisen nie entfernen
		private const float Cell = 16f;

		private static readonly HashSet<int> s_done = new HashSet<int>();
		private static readonly List<(Renderer r, bool enabled)> s_hidden = new List<(Renderer, bool)>();
		private static readonly List<(MeshFilter mf, Mesh orig)> s_cut = new List<(MeshFilter, Mesh)>();
		private static Dictionary<Vector2Int, List<(Vector2 a, Vector2 b)>>? s_removedGrid, s_keptGrid;
		private static Rect s_removedRect;
		private static bool s_dirty = true;
		private static float s_firstTick = -1f;
		private static bool s_diagDone;

		public static void OnWorldUnloaded()
		{
			s_done.Clear(); s_hidden.Clear(); s_cut.Clear();
			s_removedGrid = null; s_keptGrid = null; s_dirty = true;
			s_firstTick = -1f; s_diagDone = false; s_doneFilters.Clear(); s_nextGlobal = 0f;
		}

		/// <summary>Neue Spielgleise entfernt → alle Fern-Kacheln erneut prüfen.</summary>
		public static void MarkDirty() => s_dirty = true;

		/// <summary>Läufer (alle 2 s): neu geladene Fern-Kacheln bearbeiten.</summary>
		public static void Tick()
		{
			if (!GameAccess.TracksReady || GameAccess.Registry == null) return;
			if (s_dirty)
			{
				s_dirty = false;
				s_done.Clear();
				s_doneFilters.Clear();
				s_nextGlobal = 0f;
				BuildGrids();
			}
			if (s_removedGrid == null || s_removedGrid.Count == 0) return;
			if (s_firstTick < 0f) s_firstTick = Time.realtimeSinceStartup;
			if (!s_diagDone && Time.realtimeSinceStartup - s_firstTick > 20f) { s_diagDone = true; Diagnose(); }
			if (Time.realtimeSinceStartup - s_firstTick > 20f)
				try { GlobalPass(); } catch (Exception e) { Main.Log("Schotter-LOD: " + e.Message); }
			for (int i = 0; i < SceneManager.sceneCount; i++)
			{
				var scene = SceneManager.GetSceneAt(i);
				if (!scene.isLoaded) continue;
				foreach (var root in scene.GetRootGameObjects())
				{
					if (root == null || !root.name.StartsWith("Far_", StringComparison.OrdinalIgnoreCase)) continue;
					if (!s_done.Add(root.GetInstanceID())) continue;
					try { Scan(root); } catch (Exception e) { Main.Log($"Fern-Kachel {root.name}: {e.Message}"); }
				}
			}
		}

		// ------------------------------------------------------------------
		// Gleis-Geometrie (statisch, Draufsicht)
		// ------------------------------------------------------------------

		private static void BuildGrids()
		{
			s_removedGrid = new Dictionary<Vector2Int, List<(Vector2, Vector2)>>();
			s_keptGrid = new Dictionary<Vector2Int, List<(Vector2, Vector2)>>();
			bool any = false;
			float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
			foreach (var t in GameAccess.Registry!.OrderedRailtracks)
			{
				if (t == null || TrackBuilder.IsOwn(t)) continue; // eigene Gleise haben keine Fern-Modelle
				bool removed = VanillaRemover.IsRemoved(t);
				var ps = t.GetKinkedPointSet();
				if (ps?.points == null || ps.points.Length < 2) continue;
				var grid = removed ? s_removedGrid : s_keptGrid;
				Vector2 prev = Flat((Vector3)ps.points[0].position);
				for (int i = 1; i < ps.points.Length; i++)
				{
					Vector2 cur = Flat((Vector3)ps.points[i].position);
					Add(grid, prev, cur);
					if (removed)
					{
						any = true;
						minX = Mathf.Min(minX, cur.x, prev.x); maxX = Mathf.Max(maxX, cur.x, prev.x);
						minY = Mathf.Min(minY, cur.y, prev.y); maxY = Mathf.Max(maxY, cur.y, prev.y);
					}
					prev = cur;
				}
			}
			if (!any) { s_removedGrid.Clear(); return; }
			s_removedRect = Rect.MinMaxRect(minX - 5f, minY - 5f, maxX + 5f, maxY + 5f);
		}

		private static Vector2 Flat(Vector3 v) => new Vector2(v.x, v.z);

		private static void Add(Dictionary<Vector2Int, List<(Vector2, Vector2)>> grid, Vector2 a, Vector2 b)
		{
			int x0 = Mathf.FloorToInt((Mathf.Min(a.x, b.x) - Near) / Cell), x1 = Mathf.FloorToInt((Mathf.Max(a.x, b.x) + Near) / Cell);
			int y0 = Mathf.FloorToInt((Mathf.Min(a.y, b.y) - Near) / Cell), y1 = Mathf.FloorToInt((Mathf.Max(a.y, b.y) + Near) / Cell);
			for (int x = x0; x <= x1; x++)
				for (int y = y0; y <= y1; y++)
				{
					var k = new Vector2Int(x, y);
					if (!grid.TryGetValue(k, out var list)) grid[k] = list = new List<(Vector2, Vector2)>();
					list.Add((a, b));
				}
		}

		private static float Dist(Dictionary<Vector2Int, List<(Vector2 a, Vector2 b)>> grid, Vector2 p)
		{
			if (!grid.TryGetValue(new Vector2Int(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.y / Cell)), out var list)) return float.MaxValue;
			float best = float.MaxValue;
			foreach (var (a, b) in list)
			{
				Vector2 ab = b - a;
				float l2 = ab.sqrMagnitude;
				float t = l2 > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2) : 0f;
				float d = (a + ab * t - p).sqrMagnitude;
				if (d < best) best = d;
			}
			return Mathf.Sqrt(best);
		}

		// ------------------------------------------------------------------
		// Fern-Kachel durchsuchen
		// ------------------------------------------------------------------

		private static void Scan(GameObject root) => Scan("Fern-Kachel " + root.name, root.GetComponentsInChildren<MeshFilter>(true));

		/// <summary>Materialnamen + Shadernamen eines Renderers (Hinweis „Gleis/Schotter“, Shader „DV/Ballast LOD“).</summary>
		private static string MatInfo(Renderer r) =>
			string.Join(",", r.sharedMaterials.Where(x => x != null).Select(x => x.name + (x.shader != null ? " (" + x.shader.name + ")" : "")));

		private static bool IsBallastLod(Renderer r) =>
			r.sharedMaterials.Any(x => x != null && x.shader != null && x.shader.name.IndexOf("Ballast LOD", StringComparison.OrdinalIgnoreCase) >= 0);

		private static readonly HashSet<int> s_doneFilters = new HashSet<int>();
		private static float s_nextGlobal;

		/// <summary>
		/// Fern-Gleise außerhalb der Fern-Kacheln: alle Renderer mit dem Shader „DV/Ballast LOD“ (Schotter-LOD,
		/// wird erst ab einer Entfernung gezeichnet – _DistanceClip). Alle 30 s nach neuen suchen.
		/// </summary>
		private static readonly List<UnityEngine.SceneManagement.Scene> s_newScenes = new List<UnityEngine.SceneManagement.Scene>();
		private static bool s_sceneHook;

		private static void GlobalPass()
		{
			// Ganze Welt nur EINMAL (bzw. nach neuen Entfernungen) durchsuchen – FindObjectsOfType kostet einen spürbaren
			// Frame. Danach nur noch die jeweils neu geladene Szene (Welt-Kacheln werden beim Fahren nachgeladen).
			if (!s_sceneHook)
			{
				s_sceneHook = true;
				SceneManager.sceneLoaded += (scene, mode) => s_newScenes.Add(scene);
			}
			var list = new List<MeshFilter>();
			if (Time.realtimeSinceStartup >= s_nextGlobal)
			{
				s_nextGlobal = float.MaxValue;
				s_newScenes.Clear();
				foreach (var r in Object.FindObjectsOfType<MeshRenderer>()) Collect(r, list);
			}
			else if (s_newScenes.Count > 0)
			{
				var scene = s_newScenes[0];
				s_newScenes.RemoveAt(0); // eine Szene pro Durchlauf
				if (scene.IsValid() && scene.isLoaded)
					foreach (var root in scene.GetRootGameObjects())
						foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true)) Collect(r, list);
			}
			if (list.Count > 0) Scan($"Schotter-LOD ({list.Count} neue Modelle)", list);
		}

		private static void Collect(MeshRenderer r, List<MeshFilter> list)
		{
			if (r == null || !IsBallastLod(r)) return;
			var mf = r.GetComponent<MeshFilter>();
			if (mf != null && s_doneFilters.Add(mf.GetInstanceID())) list.Add(mf);
		}

		private static void Scan(string label, IEnumerable<MeshFilter> filters)
		{
			int hidden = 0, cut = 0, overlapping = 0;
			var problems = new List<string>();
			foreach (var mf in filters)
			{
				var r = mf != null ? mf.GetComponent<Renderer>() : null;
				if (mf == null || r == null || mf.sharedMesh == null) continue;
				s_doneFilters.Add(mf.GetInstanceID());
				// Statisch zusammengefasst: sharedMesh ist das Sammel-Mesh – zum Prüfen das Original-Mesh nehmen,
				// bearbeitet werden kann dann nur das Ganze (ausblenden), nicht einzelne Dreiecke.
				bool batched = r.isPartOfStaticBatch || mf.sharedMesh.name.StartsWith("Combined Mesh");
				Mesh? mesh = batched ? CloneHelper.OriginalMesh(mf) : mf.sharedMesh;
				Matrix4x4 m = mf.transform.localToWorldMatrix;
				Bounds wb = mesh != null ? TransformBounds(m, mesh.bounds) : r.bounds;
				// Fern-Kacheln liegen evtl. außerhalb des verschobenen Ursprungs – beide Lagen prüfen
				Vector3? offset = null;
				foreach (var off in new[] { GameAccess.OriginShift, Vector3.zero })
				{
					var rect = Rect.MinMaxRect(wb.min.x - off.x, wb.min.z - off.z, wb.max.x - off.x, wb.max.z - off.z);
					if (rect.Overlaps(s_removedRect)) { offset = off; break; }
				}
				if (offset == null) continue;
				overlapping++;

				string path = CloneHelper.PathOf(mf.transform);
				string mats = MatInfo(r);
				bool hint = Hint(path) || Hint(mats) || IsBallastLod(r);

				if (mesh == null || !mesh.isReadable)
				{
					// Ohne lesbares Mesh (z. B. beim Bau statisch zusammengefasst – in AssetStudio dann nur
					// „Combined Mesh (root: scene)“): über die Grenzen prüfen. Ausblenden, wenn die entfernten Gleise
					// die Grenzen des Modells erklären und kein erhaltenes Gleis darin liegt.
					var rect = Rect.MinMaxRect(wb.min.x - offset.Value.x, wb.min.z - offset.Value.z, wb.max.x - offset.Value.x, wb.max.z - offset.Value.z);
					float cover = Coverage(s_removedGrid!, rect);
					bool kept = AnyInside(s_keptGrid!, rect, 2f);
					if (hint && cover >= 0.8f && !kept && wb.size.magnitude < 600f)
					{
						s_hidden.Add((r, r.enabled)); r.enabled = false; hidden++;
					}
					else if ((hint || cover > 0.3f) && problems.Count < 12)
						problems.Add($"{path} [{(batched ? "statisch zusammengefasst" : "Mesh nicht lesbar")}, Material {mats}, {wb.size.magnitude:0} m, Deckung {cover * 100f:0} %{(kept ? ", erhaltenes Gleis darin" : "")}]");
					continue;
				}

				var verts = mesh.vertices;
				var near = new bool[verts.Length];
				int nNear = 0;
				for (int i = 0; i < verts.Length; i++)
				{
					Vector2 p = Flat(m.MultiplyPoint3x4(verts[i]) - offset.Value);
					if (Dist(s_removedGrid!, p) <= Near && Dist(s_keptGrid!, p) > KeepNear) { near[i] = true; nNear++; }
				}
				if (nNear == 0) continue;
				if (nNear == verts.Length)
				{
					s_hidden.Add((r, r.enabled)); r.enabled = false; hidden++;
					continue;
				}
				if (batched)
				{
					if (problems.Count < 12) problems.Add($"{path} [statisch zusammengefasst, {100f * nNear / verts.Length:0} % auf entfernten Gleisen]");
					continue;
				}
				// Teilweise: Dreiecke entlang der entfernten Gleise herausnehmen
				var copy = Object.Instantiate(mesh);
				copy.name = mesh.name + " (TP ohne entfernte Gleise)";
				int removedTris = 0, keptTris = 0;
				for (int s = 0; s < copy.subMeshCount; s++)
				{
					if (copy.GetTopology(s) != MeshTopology.Triangles) continue;
					var tris = copy.GetTriangles(s);
					var keep = new List<int>(tris.Length);
					for (int i = 0; i + 2 < tris.Length; i += 3)
					{
						if (near[tris[i]] && near[tris[i + 1]] && near[tris[i + 2]]) { removedTris++; continue; }
						keep.Add(tris[i]); keep.Add(tris[i + 1]); keep.Add(tris[i + 2]);
						keptTris++;
					}
					copy.SetTriangles(keep, s, false);
				}
				if (removedTris == 0) { Object.Destroy(copy); continue; }
				if (keptTris == 0) { Object.Destroy(copy); s_hidden.Add((r, r.enabled)); r.enabled = false; hidden++; continue; }
				s_cut.Add((mf, mesh));
				mf.sharedMesh = copy;
				cut++;
			}
			if (overlapping > 0 && hidden == 0 && cut == 0 && problems.Count == 0)
				Main.Log($"{label}: {overlapping} Modelle im Bereich entfernter Gleise, aber keins liegt auf ihnen");
			if (hidden > 0 || cut > 0 || problems.Count > 0)
				Main.Log($"{label}: entfernte Spielgleise – {hidden} Modell(e) ausgeblendet, {cut} gekürzt"
					+ (problems.Count > 0 ? "; nicht bearbeitbar: " + string.Join(" | ", problems) : ""));
		}

		/// <summary>
		/// Einmal pro Welt: wo stecken die Fern-Gleise überhaupt? Alle aktiven Renderer mit einem Schotter-/Gleis-
		/// LOD-Material (z. B. Textur „BallastLODMed“) mit Pfad, Szene und Art ins Log.
		/// </summary>
		private static void Diagnose()
		{
			try
			{
				var found = new List<string>();
				int total = 0, nearRemoved = 0;
				foreach (var r in Object.FindObjectsOfType<Renderer>())
				{
					if (r == null || r.GetComponentInParent<RailTrack>() != null) continue;
					string mats = MatInfo(r);
					bool lodMat = IsBallastLod(r) || (mats.IndexOf("lod", StringComparison.OrdinalIgnoreCase) >= 0 && Hint(mats));
					if (!lodMat) continue;
					total++;
					Vector2 c = Flat(r.bounds.center - GameAccess.OriginShift);
					float d = Dist(s_removedGrid!, c);
					if (d < 30f) nearRemoved++;
					if (found.Count < 15 && (d < 30f || found.Count < 3))
					{
						var mf = r.GetComponent<MeshFilter>();
						string mesh = mf != null && mf.sharedMesh != null ? $"{mf.sharedMesh.name}{(mf.sharedMesh.isReadable ? "" : ", nicht lesbar")}" : "kein Mesh";
						found.Add($"{CloneHelper.PathOf(r.transform)} (Szene {r.gameObject.scene.name}, Material {mats}, Mesh {mesh}{(r.isPartOfStaticBatch ? ", statisch zusammengefasst" : "")}, {r.bounds.size.magnitude:0} m{(r.enabled ? "" : ", aus")})");
					}
				}
				Main.Log($"Fern-Gleise (Diagnose): {total} Renderer mit Gleis-/Schotter-LOD-Material, davon {nearRemoved} bei entfernten Gleisen" + (found.Count > 0 ? ": " + string.Join(" | ", found) : ""));
			}
			catch (Exception e) { Main.Log("Fern-Gleise (Diagnose): " + e.Message); }
		}

		private static bool Hint(string s) =>
			s.IndexOf("rail", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("track", StringComparison.OrdinalIgnoreCase) >= 0
			|| s.IndexOf("ballast", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("gravel", StringComparison.OrdinalIgnoreCase) >= 0;

		private static IEnumerable<(Vector2 a, Vector2 b)> SegmentsIn(Dictionary<Vector2Int, List<(Vector2 a, Vector2 b)>> grid, Rect r)
		{
			int x0 = Mathf.FloorToInt(r.xMin / Cell), x1 = Mathf.FloorToInt(r.xMax / Cell);
			int y0 = Mathf.FloorToInt(r.yMin / Cell), y1 = Mathf.FloorToInt(r.yMax / Cell);
			if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 4000) yield break; // riesige Modelle (Gelände) nicht prüfen
			for (int x = x0; x <= x1; x++)
				for (int y = y0; y <= y1; y++)
					if (grid.TryGetValue(new Vector2Int(x, y), out var list))
						foreach (var seg in list) yield return seg;
		}

		/// <summary>Wie weit erklären die entfernten Gleise die Ausdehnung des Rechtecks? (0..1, min. aus Breite/Tiefe)</summary>
		private static float Coverage(Dictionary<Vector2Int, List<(Vector2 a, Vector2 b)>> grid, Rect r)
		{
			var e = new Rect(r.xMin - Near, r.yMin - Near, r.width + 2 * Near, r.height + 2 * Near);
			bool any = false;
			float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
			foreach (var (a, b) in SegmentsIn(grid, e))
				for (int k = 0; k <= 4; k++)
				{
					Vector2 p = Vector2.Lerp(a, b, k / 4f);
					if (!e.Contains(p)) continue;
					any = true;
					minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
					minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
				}
			if (!any) return 0f;
			float cx = r.width < 2 * Near ? 1f : Mathf.Clamp01((maxX - minX + 2 * Near) / r.width);
			float cy = r.height < 2 * Near ? 1f : Mathf.Clamp01((maxY - minY + 2 * Near) / r.height);
			return Mathf.Min(cx, cy);
		}

		private static bool AnyInside(Dictionary<Vector2Int, List<(Vector2 a, Vector2 b)>> grid, Rect r, float shrink)
		{
			var inner = new Rect(r.xMin + shrink, r.yMin + shrink, Mathf.Max(0f, r.width - 2 * shrink), Mathf.Max(0f, r.height - 2 * shrink));
			foreach (var (a, b) in SegmentsIn(grid, inner))
				for (int k = 0; k <= 4; k++)
					if (inner.Contains(Vector2.Lerp(a, b, k / 4f))) return true;
			return false;
		}

		private static Bounds TransformBounds(Matrix4x4 m, Bounds b)
		{
			var r = new Bounds(m.MultiplyPoint3x4(b.center), Vector3.zero);
			Vector3 e = b.extents;
			for (int i = 0; i < 8; i++)
				r.Encapsulate(m.MultiplyPoint3x4(b.center + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z)));
			return r;
		}
	}
}
