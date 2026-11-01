using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TrackPlacer.World
{
	/// <summary>
	/// Fern-Ansicht der Gleise. Das Spiel erzeugt beim Start EINMAL ein vereinfachtes Mesh je Gleis
	/// (RailwayLodGenerator → Objekt „Railway LOD“). Neue Gleise haben darum keins (aus der Ferne unsichtbar),
	/// entfernte Spielgleise behalten ihres. Hier:
	///  - eigene Gleise: Mesh mit dem Generator des Spiels erzeugen (gleiches Profil, gleiches Material)
	///  - entfernte Spielgleise: ihr LOD-Mesh über die Geometrie finden und ausblenden
	/// Die LOD-Objekte haben keinen Namen (new GameObject()), darum Zuordnung über Mittelpunkt und Größe.
	/// </summary>
	internal static class LodSupport
	{
		private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
		private static readonly MethodInfo? MSimplify = typeof(RailwayLodGenerator).GetMethod("SimplifiedPoints", Inst);
		private static readonly MethodInfo? MGenerate = typeof(RailwayLodGenerator).GetMethod("GenerateMesh", Inst);
		private static readonly MethodInfo? MMake = typeof(RailwayLodGenerator).GetMethod("MakeGameObject", Inst);

		private static Transform? s_root;
		private static bool s_searched;
		private static RailwayLodGenerator? s_gen;
		private static readonly Dictionary<RailTrack, GameObject> s_own = new Dictionary<RailTrack, GameObject>();
		private static readonly Dictionary<RailTrack, GameObject> s_hidden = new Dictionary<RailTrack, GameObject>();
		private static readonly HashSet<RailTrack> s_pendingCreate = new HashSet<RailTrack>();
		private static readonly HashSet<RailTrack> s_pendingHide = new HashSet<RailTrack>();
		private static readonly HashSet<GameObject> s_claimed = new HashSet<GameObject>();
		private static List<(GameObject go, Vector3 center, Vector3 size)>? s_index;

		public static void OnWorldUnloaded()
		{
			s_root = null; s_gen = null; s_index = null; s_searched = false;
			s_own.Clear(); s_hidden.Clear(); s_pendingCreate.Clear(); s_pendingHide.Clear(); s_claimed.Clear();
		}

		private static bool Ready
		{
			get
			{
				// Nur einmal pro Welt suchen: fehlt der Generator (aktuelles Spiel), kostete das früher bei jedem Gleis
				// zwei komplette Szenensuchen (~40 ms je Gleis beim Laden).
				if (!s_searched)
				{
					s_searched = true;
					if (s_root == null) { var go = GameObject.Find("Railway LOD"); s_root = go != null ? go.transform : null; }
					if (s_gen == null) s_gen = Object.FindObjectOfType<RailwayLodGenerator>();
				}
				return s_root != null && s_gen != null && MSimplify != null && MGenerate != null && MMake != null;
			}
		}

		/// <summary>Grenzen eines Gleises (statisch) aus seinem PointSet.</summary>
		private static Bounds? TrackBounds(RailTrack t)
		{
			var ps = t.GetKinkedPointSet();
			if (ps?.points == null || ps.points.Length == 0) return null;
			var b = new Bounds((Vector3)ps.points[0].position, Vector3.zero);
			foreach (var p in ps.points) b.Encapsulate((Vector3)p.position);
			return b;
		}

		/// <summary>LOD-Objekt des Spiels, das zu diesem Gleis passt (Mittelpunkt ≤ 1,5 m, ähnliche Ausdehnung).</summary>
		private static GameObject? FindMatch(RailTrack t)
		{
			var tb = TrackBounds(t);
			if (tb == null || s_root == null) return null;
			if (s_index == null)
			{
				s_index = new List<(GameObject, Vector3, Vector3)>();
				Vector3 shift = GameAccess.OriginShift;
				foreach (Transform c in s_root)
				{
					var mf = c.GetComponent<MeshFilter>();
					if (mf == null || mf.sharedMesh == null) continue;
					s_index.Add((c.gameObject, c.position - shift + mf.sharedMesh.bounds.center, mf.sharedMesh.bounds.size));
				}
			}
			GameObject? best = null;
			float bestD = 1.5f;
			foreach (var (go, center, size) in s_index)
			{
				if (go == null || s_claimed.Contains(go)) continue;
				float d = Vector3.Distance(center, tb.Value.center);
				if (d >= bestD) continue;
				// LOD-Profil ist breiter als die Gleismittellinie: waagerecht bis ~6 m Unterschied zulassen
				if (Mathf.Abs(size.x - tb.Value.size.x) > 6f || Mathf.Abs(size.z - tb.Value.size.z) > 6f) continue;
				bestD = d; best = go;
			}
			return best;
		}

		/// <summary>Fern-Mesh für ein eigenes Gleis anlegen (oder ein vom Spiel bereits erzeugtes übernehmen).</summary>
		public static void CreateFor(RailTrack t)
		{
			if (t == null || s_own.ContainsKey(t)) return;
			if (!Ready) { s_pendingCreate.Add(t); return; }
			try
			{
				// Lief der Generator erst nach einem frühen Aufbau, hat er unser Gleis schon erfasst
				var existing = FindMatch(t);
				if (existing != null) { s_claimed.Add(existing); s_own[t] = existing; existing.name = "[TP]_lod_" + t.name; return; }

				object? ps = MSimplify!.Invoke(s_gen, new object[] { t });
				if (!(MGenerate!.Invoke(s_gen, new[] { ps }) is Mesh mesh)) return;
				if (!(MMake!.Invoke(s_gen, new object[] { mesh }) is GameObject go)) return;
				go.name = "[TP]_lod_" + t.name;
				go.transform.position += GameAccess.OriginShift; // Mesh liegt in statischen Koordinaten
				go.transform.SetParent(s_root, true);
				s_own[t] = go;
				s_claimed.Add(go);
			}
			catch (Exception e) { Main.Log($"Fern-Mesh für {t.name}: {(e.InnerException ?? e).Message}"); }
		}

		public static void RemoveFor(RailTrack t)
		{
			s_pendingCreate.Remove(t);
			if (t != null && s_own.TryGetValue(t, out var go))
			{
				s_claimed.Remove(go);
				if (go != null) Object.Destroy(go);
				s_own.Remove(t);
			}
		}

		/// <summary>Fern-Mesh eines entfernten Spielgleises ausblenden.</summary>
		public static void HideVanilla(RailTrack t)
		{
			if (t == null || s_hidden.ContainsKey(t)) return;
			if (!Ready) { s_pendingHide.Add(t); return; }
			var go = FindMatch(t);
			if (go == null) { Main.Log($"Fern-Mesh zu Spielgleis {t.name} nicht gefunden"); return; }
			s_claimed.Add(go);
			go.SetActive(false);
			s_hidden[t] = go;
		}

		/// <summary>Nachholen, was vor dem Start des LOD-Generators angefragt wurde (aus dem WorldLoader).</summary>
		public static void Flush()
		{
			s_searched = false; // nach dem Laden noch einmal nachsehen
			if (!Ready) { Main.Log("„Railway LOD“ nicht gefunden – Fern-Ansicht eigener Gleise fehlt."); return; }
			var hide = new List<RailTrack>(s_pendingHide); s_pendingHide.Clear();
			foreach (var t in hide) HideVanilla(t);
			var create = new List<RailTrack>(s_pendingCreate); s_pendingCreate.Clear();
			foreach (var t in create) if (t != null) CreateFor(t);
			Main.Log($"Fern-Ansicht: {s_own.Count} eigene Gleise, {s_hidden.Count} Spielgleise ausgeblendet");
		}
	}
}
