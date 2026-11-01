using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TrackPlacer.Data;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TrackPlacer.World
{
	/// <summary>
	/// Abgedeckte Weiche: das Asphalt-/Übergangsmodell einer rechten Weiche (Models/switch_cover_right.obj, SketchUp,
	/// Fuß → Meter) wird auf die gebaute Weiche gelegt; für Linksweichen gespiegelt.
	///
	/// Ausrichtung: im Modell liegt das gerade Gleis leicht schräg. Seine Mittellinie (Scheitel der Wölbung) läuft
	/// von T (Weichenanfang, schmale Seite) nach E (Ende gerade). T kommt auf den Weichenanfang, T→E auf die Richtung
	/// des geraden Weichengleises. Höhe wie beim Asphaltgleis: Schienenstreifen knapp unter Schienenoberkante.
	/// </summary>
	internal static class SwitchCoverBuilder
	{
		private const string NamePrefix = "[TP]_switchcover_";
		private const float Feet = 0.3048f;
		private const float ModelRailZ = -0.031f; // Oberfläche über der Schiene im Modell (Basis −0,255 m)
		private const float ModelToeEdgeY = 12.085f; // Stoßkante (Weichenanfang) im Modell, Meter
		private const float ExtraLower = 0.01f;     // im Spiel getestet: 3 cm tiefer war zu viel, 2 cm wieder angehoben → 1 cm tiefer als das Asphaltgleis

		// Mittellinie des geraden Gleises im Modell (Meter, Draufsicht x/y)
		private static readonly Vector2 ModelToe = new Vector2(0.51f, 11.99f);
		private static readonly Vector2 ModelThroughEnd = new Vector2(-0.51f, -12.11f);

		private sealed class ObjMesh
		{
			public readonly List<Vector3> v = new List<Vector3>();   // Meter, z = oben
			public readonly List<Vector2> vt = new List<Vector2>();
			public readonly List<Vector3> vn = new List<Vector3>();
			public readonly List<(int v, int t, int n)[]> faces = new List<(int, int, int)[]>();
			public float maxY;
		}

		private static ObjMesh? s_model;
		private static bool s_loadFailed;
		private static readonly Dictionary<string, GameObject> s_built = new Dictionary<string, GameObject>();

		public static void OnWorldUnloaded() => s_built.Clear();

		private static ObjMesh? Model
		{
			get
			{
				if (s_model != null || s_loadFailed) return s_model;
				try
				{
					string text;
					string file = Path.Combine(Main.Mod.Path, "models", "switch_cover_right.obj");
					if (File.Exists(file)) text = File.ReadAllText(file);
					else
					{
						using var st = typeof(SwitchCoverBuilder).Assembly.GetManifestResourceStream("TrackPlacer.Models.switch_cover_right.obj");
						if (st == null) throw new FileNotFoundException("switch_cover_right.obj nicht eingebettet");
						using var r = new StreamReader(st);
						text = r.ReadToEnd();
					}
					s_model = Parse(text);
					Main.Log($"Weichen-Abdeckung geladen: {s_model.v.Count} Punkte, {s_model.faces.Count} Flächen");
				}
				catch (Exception e) { s_loadFailed = true; Main.Log("Weichen-Abdeckung nicht ladbar: " + e.Message); }
				return s_model;
			}
		}

		private static ObjMesh Parse(string text)
		{
			var m = new ObjMesh();
			var ci = CultureInfo.InvariantCulture;
			foreach (var raw in text.Split('\n'))
			{
				var p = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
				if (p.Length == 0) continue;
				switch (p[0])
				{
					case "v": m.v.Add(new Vector3(float.Parse(p[1], ci), float.Parse(p[2], ci), float.Parse(p[3], ci)) * Feet); break;
					case "vt": m.vt.Add(new Vector2(float.Parse(p[1], ci), float.Parse(p[2], ci))); break;
					case "vn": m.vn.Add(new Vector3(float.Parse(p[1], ci), float.Parse(p[2], ci), float.Parse(p[3], ci))); break;
					case "f":
						var f = new (int, int, int)[p.Length - 1];
						for (int i = 1; i < p.Length; i++)
						{
							var q = p[i].Split('/');
							int vi = int.Parse(q[0], ci);
							int ti = q.Length > 1 && q[1].Length > 0 ? int.Parse(q[1], ci) : 0;
							int ni = q.Length > 2 && q[2].Length > 0 ? int.Parse(q[2], ci) : 0;
							// negative Indizes = relativ zum Ende
							f[i - 1] = (vi < 0 ? m.v.Count + vi + 1 : vi, ti < 0 ? m.vt.Count + ti + 1 : ti, ni < 0 ? m.vn.Count + ni + 1 : ni);
						}
						m.faces.Add(f);
						break;
				}
			}
			m.maxY = m.v.Count > 0 ? m.v.Max(x => x.y) : 0f;
			return m;
		}

		/// <summary>Abdeckung auf eine gebaute Weiche legen (placed.covered).</summary>
		public static void Build(PlacedSwitch placed, SwitchVariant v, RailTrack through) =>
			BuildCore(placed.id, placed.covered, placed.Position, placed.yaw, v.through[v.through.Count - 1].position, v.divergesLeft, through.railType);

		/// <summary>
		/// Gemeinsamer Kern (eigene und Spielweichen): key = Name im Speicher, toeStatic = Weichenanfang (statisch),
		/// yawDeg = Fahrtrichtung, throughEndLocal = Ende des geraden Gleises im Weichen-Rahmen (+Z vorwärts, +X rechts).
		/// </summary>
		public static void BuildCore(string key, bool covered, Vector3 toeStatic, float yawDeg, Vector3 throughEndLocal, bool divergesLeft, RailType railType)
		{
			Remove(key);
			if (!covered) return;
			var model = Model;
			var mat = OverlayBuilder.CoverMaterial;
			if (model == null || mat == null) { Main.Log($"Weiche {key}: Abdeckung nicht möglich (Modell/Material fehlt)"); return; }
			try
			{
				bool mirror = divergesLeft; // Modell ist eine Rechtsweiche
				float sx = mirror ? -1f : 1f;
				Vector2 toe = new Vector2(ModelToe.x * sx, ModelToe.y), end = new Vector2(ModelThroughEnd.x * sx, ModelThroughEnd.y);
				Vector2 fm = (end - toe).normalized;
				// Weichenanfang = schmale Kante des Modells (größtes y) auf der Mittellinie
				// Weichenanfang = Stoßkante des ersten Modells (y = 12,085 m). Fest, nicht die Modellkante: Verlängerungen
				// im Modell (z. B. 13 cm vor dem Stoß) bleiben dort, wo sie modelliert wurden.
				if (Mathf.Abs(fm.y) > 1e-4f) toe += fm * ((ModelToeEdgeY - toe.y) / fm.y);
				Vector2 rm = new Vector2(fm.y, -fm.x); // rechts von fm (Draufsicht, x rechts / y oben)

				// Weichen-Rahmen (lokal, +Z vorwärts, +X rechts) → Richtung des geraden Gleises
				Vector3 thEnd = throughEndLocal;
				Vector3 fl = new Vector3(thEnd.x, 0f, thEnd.z).normalized;
				if (fl.sqrMagnitude < 0.5f) fl = Vector3.forward;
				Vector3 rl = new Vector3(fl.z, 0f, -fl.x);
				Quaternion yaw = SwitchVariant.YawRotation(yawDeg);
				Vector3 fw = yaw * fl, rw = yaw * rl;

				float modelLen = Vector2.Dot(new Vector2(end.x, end.y) - toe, fm); // Stoß → Ende gerade (Mittellinie)
				float trackLen = new Vector2(thEnd.x, thEnd.z).magnitude;
				if (Mathf.Abs(modelLen - trackLen) > 0.5f)
					Main.Log($"Weiche {key}: Abdeckung {modelLen:0.00} m, Weiche {trackLen:0.00} m – Längen weichen ab (Modell passt evtl. nicht zu dieser Vorlage)");

				float top = OverlayBuilder.RailTop(railType) - 0.008f;
				float yOff = top - ModelRailZ - OverlayBuilder.ModelLower - ExtraLower;

				Vector3 Map(Vector3 p)
				{
					Vector2 d = new Vector2(p.x * sx, p.y) - toe;
					return rw * Vector2.Dot(d, rm) + fw * Vector2.Dot(d, fm) + Vector3.up * (p.z + yOff);
				}
				Vector3 MapN(Vector3 n)
				{
					Vector2 d = new Vector2(n.x * sx, n.y);
					return rw * Vector2.Dot(d, rm) + fw * Vector2.Dot(d, fm) + Vector3.up * n.z;
				}

				var verts = new List<Vector3>();
				var uvs = new List<Vector2>();
				var tris = new List<int>();
				foreach (var f in model.faces)
				{
					if (f.Length < 3) continue;
					int b0 = verts.Count;
					Vector3 want = Vector3.zero;
					foreach (var (vi, ti, ni) in f)
					{
						verts.Add(Map(model.v[vi - 1]));
						uvs.Add(ti > 0 ? model.vt[ti - 1] : Vector2.zero);
						if (ni > 0) want += MapN(model.vn[ni - 1]);
					}
					for (int k = 1; k + 1 < f.Length; k++)
					{
						int a = b0, b = b0 + k, c = b0 + k + 1;
						Vector3 nn = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
						// Unity: Vorderseite = cross(b−a, c−a). Ohne Normale aus der Datei: nach oben zeigen lassen.
						Vector3 w = want.sqrMagnitude > 1e-6f ? want : Vector3.up;
						if (Vector3.Dot(nn, w) >= 0f) tris.AddRange(new[] { a, b, c }); else tris.AddRange(new[] { a, c, b });
					}
				}

				var mesh = new Mesh { name = "TP_SwitchCover" };
				if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
				mesh.SetVertices(verts);
				mesh.SetUVs(0, uvs);
				mesh.SetTriangles(tris, 0);
				mesh.RecalculateNormals();
				mesh.RecalculateTangents();
				mesh.RecalculateBounds();

				var go = new GameObject(NamePrefix + key);
				go.transform.position = toeStatic; // statisch, MoveUnderRailwayRoot verschiebt in die Welt
				go.AddComponent<MeshFilter>().sharedMesh = mesh;
				go.AddComponent<MeshRenderer>().sharedMaterial = mat;
				go.AddComponent<MeshCollider>().sharedMesh = mesh;
				TrackBuilder.MoveUnderRailwayRoot(go.transform, GameAccess.Registry!);
				s_built[key] = go;
			}
			catch (Exception e) { Main.Log($"Weichen-Abdeckung {key} fehlgeschlagen: {e}"); }
		}

		public static void Remove(string id)
		{
			if (s_built.TryGetValue(id, out var go) && go != null) Object.Destroy(go);
			s_built.Remove(id);
		}
	}
}
