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
	/// Schotterende: das Schotterbett endet am Gleisende sonst offen (Profil sichtbar). Wir verlängern das
	/// Schotterprofil des Gleises ein Stück über das Ende hinaus und lassen es dabei rund auf den Boden auslaufen –
	/// mit Profil, Material und UV-Maßstab des Gleises.
	/// </summary>
	internal static class CapBuilder
	{
		private const string NamePrefix = "[TP]_cap_";

		private static readonly Dictionary<string, GameObject> s_built = new Dictionary<string, GameObject>();
		public static IReadOnlyDictionary<string, GameObject> Built => s_built;

		public static void OnWorldUnloaded() { s_built.Clear(); s_mats.Clear(); }

		/// <summary>Schotterprofil (x seitlich, y Höhe) der Gleisart, per Reflection wie beim Schienenprofil.</summary>
		private static Vector2[]? Profile(BaseType bt)
		{
			try
			{
				object? shape = bt.baseShape;
				MethodInfo? m = shape?.GetType().GetMethods().FirstOrDefault(x => x.Name == "GetPoints2D" && x.GetParameters().Length == 1);
				if (m != null && m.Invoke(shape, new object[] { 1f }) is Vector2[] pts && pts.Length >= 2) return pts;
			}
			catch (Exception e) { Main.Log("Schotterprofil nicht lesbar: " + e.Message); }
			return null;
		}

		private static float BaseOffsetY(BaseType bt)
		{
			object? o = typeof(BaseType).GetField("baseOffset")?.GetValue(bt);
			return o switch { float f => f, Vector2 v2 => v2.y, Vector3 v3 => v3.y, _ => 0f };
		}

		public static bool Build(PlacedCap placed, out string error)
		{
			error = "";
			if (s_built.ContainsKey(placed.id)) return true;
			if (!TrackBuilder.CheckReady(out error)) return false;

			// Zugehöriges Gleisende wiederfinden
			TrackEnd? found = null;
			float best = 0.5f * 0.5f;
			foreach (var e in TrackEnds.Raw)
			{
				if (e.track == null || e.IsJunctionIn) continue;
				float d = (e.position - placed.Position).sqrMagnitude;
				if (d < best) { best = d; found = e; }
			}
			if (found == null)
			{
				float near = float.MaxValue;
				foreach (var e in TrackEnds.Raw) if (e.track != null) near = Mathf.Min(near, (e.position - placed.Position).magnitude);
				error = near < float.MaxValue ? $"kein freies Gleisende hier (nächstes freies Ende {near:0.00} m entfernt – Gleisende inzwischen verbunden oder entfernt?)" : Tr.T("kein freies Gleisende hier", "no free track end here");
				return false;
			}
			RailTrack track = found.Value.track!;
			BaseType? bt = track.baseType;
			if (bt == null) { error = Tr.T("Gleis ohne Schotter", "Track without ballast"); return false; }

			if (TrackBuilder.Materials.TryGetValue(track, out var mm) && mm.ballast == TrackBuilder.Invisible) { error = Tr.T("Gleis ohne Schotter", "Track without ballast"); return false; }
			string age = AgeKey(bt, track);
			Material mat = CapMaterial(age);

			var profile = Profile(bt);
			if (profile == null) { error = Tr.T("Schotterprofil fehlt", "Ballast profile missing"); return false; }

			try
			{
				bool atStart = found.Value.isStart;
				BezierPoint bp = atStart ? track.curve[0] : track.curve.Last();
				Vector3 outward = atStart ? bp.position - bp.globalHandle2 : bp.position - bp.globalHandle1;
				if (outward.sqrMagnitude < 1e-6f) outward = new Vector3(found.Value.outward.x, 0f, found.Value.outward.y);
				outward.Normalize();

				Mesh mesh = BuildMesh(profile, BaseOffsetY(bt), TexAspect(age));
				var go = new GameObject(NamePrefix + placed.id);
				go.transform.SetPositionAndRotation(bp.position, Quaternion.LookRotation(outward, Vector3.up)); // Weltkoordinaten
				go.AddComponent<MeshFilter>().sharedMesh = mesh;
				var mr = go.AddComponent<MeshRenderer>();
				mr.sharedMaterial = mat;
				go.AddComponent<MeshCollider>().sharedMesh = mesh;
				go.transform.SetParent(GameAccess.Registry!.TrackRootParent, true);
				s_built[placed.id] = go;
				TrackEnds.MarkDirty();
				return true;
			}
			catch (Exception e)
			{
				error = e.GetType().Name + ": " + e.Message;
				Main.Log($"Schotterende {placed.id} fehlgeschlagen: {e}");
				return false;
			}
		}

		/// <summary>
		/// Runde Kuppe: die äußere Profilhälfte (Gleismitte → Schotterfuß) wird um die senkrechte Achse durch das
		/// Gleisende von −90° bis +90° gedreht. Bei ±90° entspricht sie genau dem Profil an den Seiten, dazwischen
		/// läuft der Schotter halbrund nach vorne aus. Textur nur aus dem Stein-/Grasbereich (u 0,70 … 1,0),
		/// damit der braune Streifen unter Schienen und Schwellen nicht auf der Kuppe erscheint.
		/// </summary>
		private static bool s_profileLogged;

		/// <summary>Obere Hüllkurve des Profils bei |x| = r (beide Hälften), d. h. die höchste Stelle der Profillinie dort.</summary>
		private static float Envelope(Vector2[] profile, float r)
		{
			float best = float.NegativeInfinity;
			foreach (float x in new[] { r, -r })
				for (int i = 1; i < profile.Length; i++)
				{
					Vector2 a = profile[i - 1], b = profile[i];
					float lo = Mathf.Min(a.x, b.x), hi = Mathf.Max(a.x, b.x);
					if (x < lo - 1e-4f || x > hi + 1e-4f) continue;
					float y = Mathf.Abs(b.x - a.x) < 1e-5f ? Mathf.Max(a.y, b.y) : Mathf.Lerp(a.y, b.y, (x - a.x) / (b.x - a.x));
					if (y > best) best = y;
				}
			return best;
		}

		// ------------------------------------------------------------------
		// Eigene Böschungs-Textur je Alter (neu/mittel/alt): oben Steine, unten Grasrand, waagerecht kachelbar.
		// Austauschbar über Mods/TrackPlacer/textures/cap_new|cap_med|cap_old.(png|jpg).
		// ------------------------------------------------------------------

		private static readonly Dictionary<string, Material> s_mats = new Dictionary<string, Material>();

		/// <summary>Alter des Gleises: Gravel_000 / _050 / _100 (eigene Gleise), sonst RailTrack.age (0 … 100).</summary>
		private static string AgeKey(BaseType bt, RailTrack track)
		{
			if (TrackBuilder.IsOwn(track))
			{
				string n = bt.name ?? "";
				if (n.Contains("100")) return "old";
				if (n.Contains("050")) return "med";
				if (n.Contains("000")) return "new";
			}
			float a = track.age;
			return a >= 75f ? "old" : a >= 25f ? "med" : "new";
		}

		private static float TexAspect(string age)
		{
			var t = TextureStore.Get("cap_" + age);
			return t != null && t.height > 0 ? (float)t.width / t.height : 3.2f;
		}

		private static Material CapMaterial(string age)
		{
			if (s_mats.TryGetValue(age, out var m) && m != null) return m;
			m = new Material(Shader.Find("Standard")) { name = "TP_BallastCap_" + age };
			var tex = TextureStore.Get("cap_" + age);
			if (tex != null) m.mainTexture = tex;
			else m.color = new Color(0.62f, 0.6f, 0.55f);
			if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.05f);
			if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
			s_mats[age] = m;
			return m;
		}

		/// <summary>
		/// Schotterende wie im Original: Die Oberkante läuft ein kurzes Stück flach weiter, dann fällt die Stirnseite mit
		/// derselben Böschung wie die Seiten ab; die beiden Ecken sind um die Schulterkante gerundet.
		/// Textur: eigene Böschungs-Textur (cap_new/med/old) – oben Schulter, unten Schürze; waagerecht entlang der
		/// Kante in Metern (unverzerrt). Linke und rechte Hälfte treffen sich vorne in der Mitte mit gleichem u.
		/// </summary>
		private static Mesh BuildMesh(Vector2[] profile, float yOffset, float aspect)
		{
			if (!s_profileLogged)
			{
				s_profileLogged = true;
				Main.Log("Schotterprofil: " + string.Join(" ", profile.Select(p => $"({p.x:0.00};{p.y:0.00})")));
			}
			float rMax = Mathf.Max(0.2f, profile.Max(p => Mathf.Abs(p.x)));
			float yMin = profile.Min(p => p.y);

			// Hüllkurve fein abtasten, nach außen nur fallend
			const int N = 200;
			var hs = new float[N + 1];
			for (int i = 0; i <= N; i++) { float y = Envelope(profile, rMax * i / N); hs[i] = float.IsNegativeInfinity(y) ? yMin : y; }
			for (int i = N - 1; i >= 0; i--) hs[i] = Mathf.Max(hs[i], hs[i + 1]);
			hs[N] = Mathf.Min(hs[N], yMin);
			float H(float r) { float f = Mathf.Clamp01(r / rMax) * N; int k = Mathf.Min(N - 1, (int)f); return Mathf.Lerp(hs[k], hs[k + 1], f - k); }
			float hMax = hs[0];
			float xs = 0f;
			for (int i = 0; i <= N; i++) if (hs[i] >= hMax - 0.01f) xs = rMax * i / N;
			xs = Mathf.Clamp(xs, 0.05f, Mathf.Max(0.05f, rMax - 0.3f));
			float run = rMax - xs;
			const float plateau = 0.2f;     // flache Oberkante über das Gleisende hinaus


			var verts = new List<Vector3>();
			var uvs = new List<Vector2>();
			var tris = new List<int>();

			void Quad(int a, int b, int c, int d, Vector3 want)
			{
				Vector3 n1 = Vector3.Cross(verts[c] - verts[a], verts[b] - verts[a]);
				Vector3 n2 = Vector3.Cross(verts[c] - verts[b], verts[d] - verts[b]);
				Vector3 nn = n1.sqrMagnitude > n2.sqrMagnitude ? n1 : n2;
				if (Vector3.Dot(nn, want) >= 0f) tris.AddRange(new[] { a, c, b, b, c, d });
				else tris.AddRange(new[] { a, b, c, b, d, c });
			}

			const int TSteps = 8;
			const int Arc = 12;
			int ring = TSteps + 2;

			// Eine Hälfte: gerade Seite (z 0→plateau), Ecke (90°), Stirn bis zur Mitte
			void Half(float side)
			{
				var path = new List<(Vector3 c, Vector3 n, float s, float phi)>();
				float sAcc = 0f, phiAcc = 0f;
				void Add(Vector3 c, Vector3 n)
				{
					if (path.Count > 0) sAcc += Vector3.Distance(path[path.Count - 1].c, c);
					path.Add((c, n, sAcc, phiAcc));
				}
				Vector3 corner = new Vector3(side * xs, 0f, plateau);
				Add(new Vector3(side * xs, 0f, 0f), new Vector3(side, 0f, 0f));
				Add(corner, new Vector3(side, 0f, 0f));
				for (int k = 1; k <= Arc; k++)
				{
					float ph = (float)k / Arc * Mathf.PI * 0.5f; // 0 = seitlich, 90° = nach vorn
					phiAcc = ph;
					Add(corner, new Vector3(side * Mathf.Cos(ph), 0f, Mathf.Sin(ph)));
				}
				int front = Mathf.Max(1, Mathf.CeilToInt(xs / 0.4f));
				for (int k = 1; k <= front; k++) Add(new Vector3(side * xs * (1f - (float)k / front), 0f, plateau), Vector3.forward);

				// Böschung + Schürze bekommen die ganze Bildhöhe: oben (v = 1) Schulter/Steine, unten (v = 0) Schürzenende/Gras.
				// Waagerecht: Bildbreite = aspect × Bildhöhe in Metern → Steine unverzerrt; u = Weglänge entlang der Kante.
				const float drop = 0.6f;
				float slope = Mathf.Max(0.35f, (H(rMax - run / TSteps) - H(rMax)) / Mathf.Max(1e-4f, run / TSteps)); // Höhe je Meter am Fuß
				float extra = drop / slope;
				var cum = new float[TSteps + 2];
				for (int i = 1; i <= TSteps; i++)
				{
					float t0 = run * (i - 1) / TSteps, t1 = run * i / TSteps;
					cum[i] = cum[i - 1] + Mathf.Sqrt((t1 - t0) * (t1 - t0) + Mathf.Pow(H(xs + t1) - H(xs + t0), 2f));
				}
				cum[TSteps + 1] = cum[TSteps] + Mathf.Sqrt(extra * extra + drop * drop);
				float total = Mathf.Max(0.1f, cum[TSteps + 1]);
				float uPerMeter = 1f / (aspect * total);

				int start = verts.Count;
				foreach (var (c, n, sPath, phi) in path)
				{
					for (int i = 0; i <= TSteps; i++)
					{
						float t = run * i / TSteps;
						verts.Add(c + n * t + Vector3.up * (H(xs + t) + yOffset));
						uvs.Add(new Vector2((sPath + phi * Mathf.Max(t, 0.25f * run)) * uPerMeter, 1f - cum[i] / total));
					}
					verts.Add(c + n * (run + extra) + Vector3.up * (H(rMax) - drop + yOffset)); // Schürze: Böschung läuft in den Boden
					uvs.Add(new Vector2((sPath + phi * (run + extra)) * uPerMeter, 0f));
				}
				for (int j = 0; j < path.Count - 1; j++)
					for (int i = 0; i < ring - 1; i++)
					{
						int a = start + j * ring + i, b = a + 1, c = a + ring, d = c + 1;
						Vector3 want = (path[j].n + path[j + 1].n).normalized + Vector3.up * 0.5f;
						Quad(a, b, c, d, want);
					}

				// Flache Oberkante dieser Hälfte (Schulterhöhe): oberer Steinbereich des Bildes
				float yTop = hMax + yOffset;
				float vIn = 1f - Mathf.Min(0.35f, xs / total * 0.5f);
				int p0 = verts.Count;
				verts.Add(new Vector3(side * xs, yTop, 0f)); verts.Add(new Vector3(0f, yTop, 0f));
				verts.Add(new Vector3(side * xs, yTop, plateau)); verts.Add(new Vector3(0f, yTop, plateau));
				uvs.Add(new Vector2(0f, 1f)); uvs.Add(new Vector2(0f, vIn));
				uvs.Add(new Vector2(plateau * uPerMeter, 1f)); uvs.Add(new Vector2(plateau * uPerMeter, vIn));
				Quad(p0, p0 + 1, p0 + 2, p0 + 3, Vector3.up);

				// Rückwand am Gleisende (unter den Schwellen, schließt die Mulde)
				int wallSteps = Mathf.Max(2, Mathf.CeilToInt(xs / 0.2f));
				int w0 = verts.Count;
				for (int k = 0; k <= wallSteps; k++)
				{
					float x = side * xs * (float)k / wallSteps;
					float yb = Envelope(profile, Mathf.Abs(x));
					if (float.IsNegativeInfinity(yb)) yb = hMax;
					float uu = Mathf.Abs(x) * uPerMeter;
					verts.Add(new Vector3(x, Mathf.Min(yb, hMax) + yOffset - 0.02f, 0f));
					verts.Add(new Vector3(x, yTop, 0f));
					uvs.Add(new Vector2(uu, 0.9f)); uvs.Add(new Vector2(uu, 1f));
				}
				for (int k = 0; k < wallSteps; k++)
				{
					int a = w0 + 2 * k, b = a + 1, c = a + 2, d = a + 3;
					Quad(a, b, c, d, Vector3.back);
				}
			}

			Half(-1f);
			Half(1f);

			var mesh = new Mesh { name = "TP_BallastCap" };
			mesh.SetVertices(verts);
			mesh.SetUVs(0, uvs);
			mesh.SetTriangles(tris, 0);
			mesh.RecalculateNormals();
			mesh.RecalculateBounds();
			return mesh;
		}

		public static bool Remove(string id)
		{
			if (s_built.TryGetValue(id, out var go) && go != null) Object.Destroy(go);
			s_built.Remove(id);
			TrackEnds.MarkDirty();
			return true;
		}

		public static int BuildAll()
		{
			int n = 0;
			foreach (var c in LayoutStore.Layout.caps)
			{
				if (s_built.ContainsKey(c.id)) continue;
				if (Build(c, out string e)) n++;
				else Main.Log($"Schotterende {c.id}: {e}");
			}
			return n;
		}
	}
}
