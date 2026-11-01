using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TrackPlacer.Data;
using TrackPlacer.Geometry;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TrackPlacer.World
{
	/// <summary>
	/// Abdeckung für Bahnübergänge / Asphalt-Gleise. Im Spiel sind das normale Gleise, über die beim Bau der
	/// Welt eine Abdeckung gelegt wurde (RailCrossingTypeScriptableObject). Den Generator gibt es zur Laufzeit
	/// nicht, also erzeugen wir die Abdeckung selbst entlang der Kurve und verwenden das Original-Material.
	/// Profil: Innenplatte zwischen den Schienen, Außenplatten, Spurrillen an den Schienen, Abschrägung außen.
	/// </summary>
	internal static class OverlayBuilder
	{
		private const string NamePrefix = "[TP]_cover_";
		private const float FlangeGap = 0.075f;   // Spurrille
		private const float RailHeadHalf = 0.036f;

		private static readonly Dictionary<string, GameObject> s_built = new Dictionary<string, GameObject>();
		/// <summary>Vor dem Start der Welt (früher Aufbau) angefragte Abdeckungen – Materialien sind dann noch nicht geladen.</summary>
		private static readonly HashSet<string> s_pending = new HashSet<string>();
		private static Material? s_material;
		private static bool s_materialSearched;
		private static bool s_retryScheduled;
		private static int s_retryGeneration;
		private static int s_retryAttempt;
		private static readonly Dictionary<RailType, float> s_railTop = new Dictionary<RailType, float>();

		public static void OnWorldUnloaded()
		{
			s_built.Clear();
			s_pending.Clear();
			s_material = null;
			s_materialSearched = false;
			s_retryScheduled = false;
			s_retryGeneration++;
			s_retryAttempt = 0;
			s_railTop.Clear();
		}

		// ------------------------------------------------------------------
		// Material: bevorzugt das der Spiel-Übergänge
		// ------------------------------------------------------------------

		internal static Material? CoverMaterial
		{
			get
			{
				if (s_materialSearched) return s_material;
				s_materialSearched = true;

				// 0) Vom Nutzer in den Settings eingetragener Material-Name
				string wanted = Main.Settings.coverMaterial?.Trim() ?? "";
				if (wanted != "")
				{
					var m = Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(x => x != null && x.name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
					        ?? Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(x => x != null && x.name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0);
					if (m != null) { s_material = m; Main.Log($"Abdeckung: Material aus Settings '{m.name}'"); return m; }
					Main.Log($"Abdeckung: Material '{wanted}' nicht gefunden, suche automatisch.");
				}

				// 1) Mitgelieferter Übergangs-Atlas. So funktioniert Asphalt auch, wenn das entsprechende
				// Spiel-Asset in der aktuellen World-Streamer-Kachel noch nicht geladen wurde.
				var bundled = TextureStore.Get("asphalt");
				var bundledShader = Shader.Find("Standard");
				if (bundled != null && bundledShader != null)
				{
					var m = new Material(bundledShader) { name = "TP_Asphalt" };
					m.SetTexture("_MainTex", bundled);
					// Normal-Map (RailwayCrossingPlatform_01n): schon im Unity-Format X in Alpha, Y in Grün, R = 1 –
					// UnpackNormal liest das direkt, keine Umrechnung nötig.
					var normal = TextureStore.Get("asphalt_n", linear: true);
					if (normal != null)
					{
						m.SetTexture("_BumpMap", normal);
						m.SetFloat("_BumpScale", 1f);
						m.EnableKeyword("_NORMALMAP");
					}
					// Spec (RailwayCrossingPlatform_01s): R = Metallic, G = Verdeckung (AO), A = Glanz → Standard-Shader:
					// _MetallicGlossMap (R + A) und dieselbe Textur als _OcclusionMap (liest Grün).
					var spec = TextureStore.Get("asphalt_s", linear: true);
					if (spec != null)
					{
						m.SetTexture("_MetallicGlossMap", spec);
						m.EnableKeyword("_METALLICGLOSSMAP");
						m.SetFloat("_GlossMapScale", 1f);
						m.SetFloat("_SmoothnessTextureChannel", 0f); // Glanz aus Alpha der Metallic-Karte
						m.SetTexture("_OcclusionMap", spec);
						m.SetFloat("_OcclusionStrength", 1f);
					}
					else m.SetFloat("_Glossiness", 0.15f);
					s_material = m;
					Main.Log($"Abdeckung: mitgelieferte Texturen (Farbe{(normal != null ? " + Normal" : "")}{(spec != null ? " + Spec/AO" : "")})");
					return m;
				}

				// 2) Übergangstyp-Asset, falls es zur Laufzeit geladen ist
				var soType = Type.GetType("DV.WorldPrep.RailCrossingTypeScriptableObject, Assembly-CSharp");
				if (soType != null)
				{
					foreach (var so in Resources.FindObjectsOfTypeAll(soType))
					{
						var mat = soType.GetField("material")?.GetValue(so) as Material;
						if (mat != null) { s_material = mat; Main.Log($"Abdeckung: Material aus {so.name}: {mat.name}"); return mat; }
					}
				}

				// 3) Material der Spiel-Übergänge über den bekannten Namen ("RailwayCrossing…")
				var byName = Resources.FindObjectsOfTypeAll<Material>()
					.Where(x => x != null && x.name.IndexOf("RailwayCrossing", StringComparison.OrdinalIgnoreCase) >= 0)
					.OrderByDescending(x => x.name.IndexOf("Platform", StringComparison.OrdinalIgnoreCase) >= 0)
					.FirstOrDefault();
				if (byName != null) { s_material = byName; Main.Log($"Abdeckung: Material '{byName.name}'"); return byName; }

				// 4) Kein Material geladen, aber die Spieltexturen: eigenes Material daraus bauen
				var tex = Resources.FindObjectsOfTypeAll<Texture2D>();
				Texture2D? diff = tex.FirstOrDefault(t => t.name.Equals("RailwayCrossingPlatform_01d", StringComparison.OrdinalIgnoreCase));
				Texture2D? norm = tex.FirstOrDefault(t => t.name.Equals("RailwayCrossingPlatform_01n", StringComparison.OrdinalIgnoreCase));
				var std = Shader.Find("Standard");
				if (diff != null && std != null)
				{
					var m = new Material(std) { name = "TP_RailwayCrossingPlatform" };
					m.SetTexture("_MainTex", diff);
					if (norm != null) { m.EnableKeyword("_NORMALMAP"); m.SetTexture("_BumpMap", norm); }
					m.SetFloat("_Glossiness", 0.15f);
					s_material = m;
					Main.Log($"Abdeckung: Material aus Texturen gebaut ({diff.name}{(norm != null ? ", " + norm.name : "")})");
					return m;
				}

				// 5) Gebackene Übergangs-Abdeckung in der Szene: Objektname mit "crossing", aber keine Schienen/Gleise
				foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
				{
					var m = r.sharedMaterial;
					if (m == null) continue;
					string obj = r.name.ToLowerInvariant(), mn = m.name.ToLowerInvariant();
					if (mn.Contains("ballast") || mn.Contains("sleeper") || (mn.Contains("rail") && !mn.Contains("crossing"))) continue;
					if (obj.Contains("crossing") || mn.Contains("crossing"))
					{
						s_material = m;
						Main.Log($"Abdeckung: Material aus Szene ({r.name}): {m.name}");
						return m;
					}
				}

				// Beim frühen Savegame-Aufbau sind die Übergangs-Assets noch nicht geladen. Kein graues
				// Fallback dauerhaft an das Mesh hängen, sondern den Bau ereignisbasiert nachholen.
				return null;
			}
		}

		private static void QueuePending(string id)
		{
			s_pending.Add(id);
			if (s_retryScheduled) return;
			ScheduleRetry();
		}

		private static void ScheduleRetry()
		{
			// Nur drei gezielte Versuche während des Ladens. Kein sekündlicher Resources-Weltscan.
			float[] delays = { 2f, 8f, 30f };
			if (s_retryAttempt >= delays.Length)
			{
				Main.Log($"Abdeckung: Originaltextur noch nicht geladen; {s_pending.Count} Asphaltgleis(e) bleiben vorgemerkt. Material-Kandidaten im Mod-Menü protokollieren.");
				return;
			}
			s_retryScheduled = true;
			int generation = s_retryGeneration;
			float delay = delays[s_retryAttempt++];
			WorldLoader.RunDelayed(delay, () => RetryPending(generation));
		}

		private static void RetryPending(int generation)
		{
			s_retryScheduled = false;
			if (generation != s_retryGeneration || s_pending.Count == 0) return;
			// Während BuildPending darf QueuePending keinen zweiten Versuch parallel einplanen.
			s_retryScheduled = true;
			// Resources wächst während des Weltenladens. Jede begrenzte Wiederholung sucht in der neuen Menge.
			s_material = null;
			s_materialSearched = false;
			BuildPending();
			s_retryScheduled = false;
			if (s_pending.Count == 0) return;
			ScheduleRetry();
		}

		/// <summary>Alle geladenen Materialien mit passenden Namen ins Log (zum Eintragen in den Settings).</summary>
		public static void LogMaterialCandidates()
		{
			string[] keys = { "cross", "asphalt", "road", "plate", "concrete", "pave", "rubber", "level" };
			var names = Resources.FindObjectsOfTypeAll<Material>()
				.Where(m => m != null && keys.Any(k => m.name.ToLowerInvariant().Contains(k)))
				.Select(m => $"{m.name} [{(m.shader != null ? m.shader.name : "-")}]")
				.Distinct().OrderBy(n => n).ToList();
			Main.Log($"Material-Kandidaten ({names.Count}):\n  " + string.Join("\n  ", names));
			s_materialSearched = false; // neue Auswahl beim nächsten Bau
		}

		/// <summary>Oberkante Schiene relativ zur Kurve, aus dem Schienenprofil (per Reflection, das Shape-Assembly brauchen wir sonst nicht).</summary>
		internal static float RailTop(RailType rt)
		{
			if (rt == null) return 0.15f;
			if (s_railTop.TryGetValue(rt, out float v)) return v;
			v = 0.15f;
			try
			{
				object? shape = rt.railShape;
				MethodInfo? m = shape?.GetType().GetMethods().FirstOrDefault(x => x.Name == "GetPoints2D" && x.GetParameters().Length == 1);
				if (m != null && m.Invoke(shape, new object[] { 0.1f }) is Vector2[] pts && pts.Length > 0)
					v = pts.Max(p => p.y);
			}
			catch (Exception e) { Main.Log("Schienenprofil nicht lesbar: " + e.Message); }
			s_railTop[rt] = v;
			Main.Log($"Schienenoberkante für {rt.name}: {v:F3} m über der Kurve");
			return v;
		}

		// ------------------------------------------------------------------
		// Bauen
		// ------------------------------------------------------------------

		public static void Build(PlacedTrack placed, RailTrack track)
		{
			if (string.IsNullOrEmpty(placed.overlay) || s_built.ContainsKey(placed.id)) return;
			if (!GameAccess.WorldReady) { QueuePending(placed.id); return; } // Material-Suche erst, wenn die Welt geladen ist
			try
			{
				Material? material = CoverMaterial;
				if (material == null) { QueuePending(placed.id); return; }
				float top = RailTop(track.railType) - 0.008f; // knapp unter Schienenoberkante
				float halfGauge = track.railType != null ? track.railType.gauge * 0.5f : 0.7175f;
				float width = placed.overlayWidth > 0.5f ? placed.overlayWidth : 1.9f; // Plattenkante außen (ohne Bordstein)

				Mesh mesh = BuildModelMesh(placed.ToAnchors(), top, placed.anchors.Count > 0 ? PlacedTrack.ToVec(placed.anchors[0].p) : Vector3.zero);
				var go = new GameObject(NamePrefix + placed.id);
				go.transform.position = PlacedTrack.ToVec(placed.anchors[0].p); // absolute Koordinaten
				go.AddComponent<MeshFilter>().sharedMesh = mesh;
				var mr = go.AddComponent<MeshRenderer>();
				mr.sharedMaterial = material;
				go.AddComponent<MeshCollider>().sharedMesh = mesh; // begehbar

				TrackBuilder.MoveUnderRailwayRoot(go.transform, GameAccess.Registry!);
				s_built[placed.id] = go;
				s_pending.Remove(placed.id);
			}
			catch (Exception e)
			{
				Main.Log($"Abdeckung für {placed.id} fehlgeschlagen: {e}");
			}
		}

		/// <summary>Beim frühen Aufbau zurückgestellte Abdeckungen bauen (aus dem WorldLoader).</summary>
		public static void BuildPending()
		{
			if (s_material == null) s_materialSearched = false;
			foreach (var id in s_pending.ToList())
			{
				var placed = LayoutStore.Find(id);
				if (placed != null && TrackBuilder.Built.TryGetValue(id, out RailTrack t) && t != null) Build(placed, t);
			}
		}

		public static void Remove(string id)
		{
			s_pending.Remove(id);
			if (s_built.TryGetValue(id, out var go) && go != null) Object.Destroy(go);
			s_built.Remove(id);
		}

		// ------------------------------------------------------------------
		// Textur-Atlas: welche Bereiche der Übergangs-Textur auf welche Fläche kommen.
		// Liegt als cover_uv.json im Mod-Ordner und kann ohne Neukompilieren angepasst werden.
		// ------------------------------------------------------------------

		public class UvRegion
		{
			public float[] rect = { 0f, 0f, 1f, 1f };   // u0, v0, u1, v1 im Atlas
			public bool rotate;                          // true = Fahrtrichtung läuft entlang u (sonst entlang v)
			public float tile;                           // Meter je Wiederholung entlang des Gleises (0 = global)
		}

		public class CoverUvConfig
		{
			// Werte für den Atlas RailwayCrossingPlatform_01d (1024×512, UV-Ursprung unten links):
			//  Gitterplatte y 103–228 px, Rundstahl-Kante y 78–100 px,
			//  Bordstein oben y 230–262 px, Bordstein-Schräge y 262–320 px (Fugen alle 64 px),
			//  Stirnblech x 52–510 / y 322–448 px (oben Schraubenreihe, darunter Gitterschlitze).
			public const int CurrentVersion = 2;
			public int version = CurrentVersion;
			public float tileLength = 10.0f;
			public UvRegion plate = new UvRegion { rect = new[] { 0f, 0.555f, 1f, 0.799f }, rotate = true };
			public UvRegion bar = new UvRegion { rect = new[] { 0f, 0.805f, 1f, 0.848f }, rotate = true };
			public UvRegion face = new UvRegion { rect = new[] { 0.051f, 0.297f, 0.498f, 0.371f }, rotate = true, tile = 1.5f };
			public UvRegion groove = new UvRegion { rect = new[] { 0.6f, 0.05f, 0.95f, 0.3f }, rotate = true };
			/// <summary>Bordstein-Oberseite, quer: Plattenseite → außen (rechte Seite; links gespiegelt).</summary>
			public UvRegion curbTop = new UvRegion { rect = new[] { 0f, 0.549f, 1f, 0.488f }, rotate = true };
			/// <summary>Bordstein-Schräge, quer: oben → unten.</summary>
			public UvRegion curbSlope = new UvRegion { rect = new[] { 0f, 0.488f, 1f, 0.377f }, rotate = true };
			/// <summary>Stirnblech: u0, v unten, u1, v oben (oben = Schraubenreihe). tile = Meter quer je Wiederholung.</summary>
			public float[] endFace = { 0.051f, 0.125f, 0.498f, 0.371f };
			public float endFaceGrateTop = 0.297f; // v, ab hier nur Gitterschlitze (für tiefere Bereiche)
			public float endFaceTile = 0.9f;
			public float barWidth = 0.07f;    // Rundstahl-Kante oben (m)
			public float faceDepth = 0.15f;   // senkrechte Wand unter dem Schienenkopf (m)
			public float grooveDepth = 0.05f; // Spurrille (m)
			public float flangeGap = 0.065f;  // Spurrillenbreite innen (m)
			public float curbWidth = 0.2f;    // Bordstein oben flach (m)
			public float curbRun = 0.25f;     // Bordstein-Schräge waagerecht (m)
			public float curbDrop = 0.25f;    // Bordstein-Schräge senkrecht (m)
			public float skirt = 0.3f;        // Schürze unter dem Bordstein bis in den Boden (m)
		}

		private static CoverUvConfig? s_uv;

		private static CoverUvConfig Uv
		{
			get
			{
				if (s_uv != null) return s_uv;
				string path = System.IO.Path.Combine(Main.Mod.Path, "cover_uv.json");
				try
				{
					if (System.IO.File.Exists(path))
						s_uv = Newtonsoft.Json.JsonConvert.DeserializeObject<CoverUvConfig>(System.IO.File.ReadAllText(path));
					if (s_uv != null && s_uv.version < CoverUvConfig.CurrentVersion)
					{
						// Alte Datei (anderer Aufbau) beiseitelegen und neue Standardwerte schreiben
						try { System.IO.File.Copy(path, path + ".old", true); } catch { /* egal */ }
						Main.Log("cover_uv.json war veraltet – neue Standardwerte geschrieben (alte Datei: cover_uv.json.old).");
						s_uv = null;
					}
					if (s_uv == null)
					{
						s_uv = new CoverUvConfig();
						System.IO.File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(s_uv, Newtonsoft.Json.Formatting.Indented));
					}
				}
				catch (Exception e) { Main.Log("cover_uv.json fehlerhaft, nutze Standard: " + e.Message); s_uv = new CoverUvConfig(); }
				return s_uv;
			}
		}

		/// <summary>Nach Änderung der cover_uv.json alle Abdeckungen neu erzeugen.</summary>
		public static void ReloadUv()
		{
			s_uv = null;
			foreach (var kv in s_built.ToList())
			{
				var placed = LayoutStore.Find(kv.Key);
				if (placed == null || !TrackBuilder.Built.TryGetValue(kv.Key, out RailTrack t)) continue;
				Remove(kv.Key);
				Build(placed, t);
			}
		}

		/// <summary>Gleiche Region, quer gespiegelt (für die linke Seite).</summary>
		private static UvRegion Mirror(UvRegion r)
		{
			var q = r.rect != null && r.rect.Length >= 4 ? r.rect : new[] { 0f, 0f, 1f, 1f };
			return r.rotate
				? new UvRegion { rect = new[] { q[0], q[3], q[2], q[1] }, rotate = true, tile = r.tile }
				: new UvRegion { rect = new[] { q[2], q[1], q[0], q[3] }, rotate = false, tile = r.tile };
		}

		private static Vector2 MapUv(UvRegion r, float across, float along)
		{
			float a = Mathf.Clamp01(across), b = Mathf.Clamp01(along);
			if (r.rotate) (a, b) = (b, a);
			var q = r.rect != null && r.rect.Length >= 4 ? r.rect : new[] { 0f, 0f, 1f, 1f };
			return new Vector2(Mathf.Lerp(q[0], q[2], a), Mathf.Lerp(q[1], q[3], b));
		}

		/// <summary>
		/// Aufbau wie das Original: Gitterplatten bündig bis an die Schienen, Rundstahl-Kanten an beiden
		/// Seiten jeder Schiene und vorne/hinten, innen eine Spurrille mit Boden, außen und an den Stirnseiten
		/// senkrechte Stahlflächen. Querschnitt (x seitlich, y Höhe zur Kurve) wird entlang ausgezogen.
		/// </summary>
		// ------------------------------------------------------------------
		// Asphalt-/Übergangsgleis nach dem Referenzmodell (gleis.obj, SketchUp, Fuß → Meter):
		// 4,5 m breite Platte, Querschnitt mit leichter Wölbung, Rundstahl-Streifen über der Schiene,
		// Betonkante + Böschung außen, an beiden Enden 0,2 m Abschrägung und eine Stirnplatte.
		// UV direkt aus dem Modell (Atlas RailwayCrossingPlatform, 1024×512): v quer, u entlang (0,119 je Meter).
		// ------------------------------------------------------------------

		/// <summary>Halber Querschnitt von der Mitte nach außen: x (m), Höhe über Plattenfuß (m), v, UV-Insel.</summary>
		private static readonly (float x, float z, float v, int island)[] ModelHalf =
		{
			(0.00f, 0.238f, -0.006f, 0), // Mitte, Gitterplatte
			(0.59f, 0.226f, -0.147f, 0),
			(0.84f, 0.222f, -0.207f, 0), // 0,59 … 0,84 m: Rundstahl über der Schiene (Spur 0,7175 m)
			(0.84f, 0.222f, -0.238f, 1), // Außenplatte → Betonkante
			(1.28f, 0.213f, -0.338f, 1),
			(2.00f, 0.202f, -0.514f, 1),
			(2.00f, 0.200f,  0.485f, 2), // Böschung
			(2.25f, 0.000f,  0.409f, 2),
			(2.25f, -0.30f,  0.380f, 2), // Schürze in den Boden (nicht im Modell, gegen Lücken bei unebenem Gelände)
		};
		private const float ModelRailZ = 0.224f;     // Höhe der Oberfläche über der Schiene im Modell
		private const float ModelUPerMeter = 0.1192f; // u entlang des Gleises
		private const float ModelBevel = 0.2f;       // Abschrägung an den Enden
		private const float ModelEndTop = 0.202f;    // Oberkante an der Stirnseite
		internal const float ModelLower = 0.03f;      // im Spiel getestet: 3 cm tiefer, sonst verdeckt die Platte den Schienenkopf

		private static Mesh BuildModelMesh(List<Anchor> anchors, float top, Vector3 origin)
		{
			var tangents = new List<Vector3>();
			List<Vector3> pts = ArcBuilder.Sample(anchors, 0.5f, tangents);
			if (pts.Count < 2) throw new InvalidOperationException("Gleis zu kurz für eine Abdeckung");
			var dist = new float[pts.Count];
			for (int i = 1; i < pts.Count; i++) dist[i] = dist[i - 1] + Vector3.Distance(pts[i], pts[i - 1]);
			float L = dist[dist.Length - 1];
			float bevel = Mathf.Min(ModelBevel, L * 0.25f);
			float yOff = top - ModelRailZ - ModelLower; // Oberfläche am Schienenstreifen unter Schienenoberkante

			// Stationen entlang des Gleises: Enden fein (Abschrägung), dazwischen alle 0,5 m
			var stations = new List<float> { 0f, bevel * 0.5f, bevel };
			for (float s = bevel + 0.5f; s < L - bevel - 0.1f; s += 0.5f) stations.Add(s);
			stations.Add(L - bevel); stations.Add(L - bevel * 0.5f); stations.Add(L);
			stations = stations.Distinct().OrderBy(x => x).ToList();

			(Vector3 p, Vector3 t) At(float s)
			{
				int k = Array.BinarySearch(dist, s);
				if (k < 0) k = ~k;
				k = Mathf.Clamp(k, 1, pts.Count - 1);
				float f = Mathf.InverseLerp(dist[k - 1], dist[k], s);
				Vector3 t = Vector3.Lerp(tangents[k - 1], tangents[k], f);
				t.y = 0f;
				return (Vector3.Lerp(pts[k - 1], pts[k], f), t.sqrMagnitude > 1e-6f ? t.normalized : Vector3.forward);
			}

			// voller Querschnitt links → rechts (linke Hälfte gespiegelt, Mitte nur einmal)
			var prof = new List<(float x, float z, float v, int island)>();
			for (int i = ModelHalf.Length - 1; i >= 1; i--) { var h = ModelHalf[i]; prof.Add((-h.x, h.z, h.v, h.island + 10)); }
			prof.Add(ModelHalf[0]);
			for (int i = 1; i < ModelHalf.Length; i++) prof.Add(ModelHalf[i]);
			// Insel der linken Mitte (0) mit der rechten (0) zusammenführen: gleiche Mitte, gespiegelte UV
			for (int i = 0; i < prof.Count; i++) if (prof[i].island == 10) prof[i] = (prof[i].x, prof[i].z, prof[i].v, 0);

			var verts = new List<Vector3>();
			var uvs = new List<Vector2>();
			var tris = new List<int>();
			int n = prof.Count;

			float Z(int j, float s)
			{
				var q = prof[j];
				int isl = q.island % 10;
				if (isl == 2) return q.z; // Böschung/Schürze bleibt
				float k = bevel > 1e-4f ? Mathf.Clamp01(Mathf.Min(s, L - s) / bevel) : 1f;
				return Mathf.Lerp(ModelEndTop, q.z, k);
			}

			var ringPos = new List<(Vector3 p, Vector3 r, Vector3 t)>();
			foreach (float s in stations)
			{
				var (p, t) = At(s);
				Vector3 r = new Vector3(t.z, 0f, -t.x);
				ringPos.Add((p, r, t));
				for (int j = 0; j < n; j++)
				{
					verts.Add(p + r * prof[j].x + Vector3.up * (Z(j, s) + yOff) - origin);
					uvs.Add(new Vector2(2.4f + s * ModelUPerMeter, prof[j].v));
				}
			}

			void Tri(int a, int b, int c, Vector3 want)
			{
				Vector3 nn = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
				if (Vector3.Dot(nn, want) >= 0f) tris.AddRange(new[] { a, b, c }); else tris.AddRange(new[] { a, c, b });
			}

			for (int k = 0; k + 1 < stations.Count; k++)
				for (int j = 0; j + 1 < n; j++)
				{
					if (prof[j].island != prof[j + 1].island) continue; // UV-Naht: keine Fläche dazwischen
					float dx = prof[j + 1].x - prof[j].x, dz = prof[j + 1].z - prof[j].z;
					Vector3 want = ringPos[k].r * (-dz) + Vector3.up * dx;
					if (want.sqrMagnitude < 1e-8f) continue;
					int a = k * n + j, b = a + 1, c = a + n, d = c + 1;
					Tri(a, b, c, want); Tri(b, d, c, want);
				}

			// Stirnplatten an beiden Enden: von der Oberkante bis zur Unterkante der Schürze
			float baseZ = ModelHalf[ModelHalf.Length - 1].z;
			void EndFace(int ring, Vector3 outward)
			{
				var (p, r, _) = ringPos[ring];
				float s = stations[ring];
				var top2 = new List<(float x, float z)>();
				for (int j = 0; j < n; j++)
				{
					if (j > 0 && Mathf.Abs(prof[j].x - prof[j - 1].x) < 1e-4f) continue; // Inselnähte nur einmal
					if (prof[j].z < 0f) continue; // Schürzenpunkte nicht als Oberkante
					top2.Add((prof[j].x, Z(j, s)));
				}
				for (int j = 0; j + 1 < top2.Count; j++)
				{
					int b0 = verts.Count;
					foreach (var (x, z) in new[] { (top2[j].x, baseZ), (top2[j + 1].x, baseZ), (top2[j].x, top2[j].z), (top2[j + 1].x, top2[j + 1].z) })
					{
						verts.Add(p + r * x + Vector3.up * (z + yOff) - origin);
						uvs.Add(new Vector2(0.051f + Mathf.Min(Mathf.Abs(x), 1.28f) / 1.28f * 0.447f, 0.125f + Mathf.Clamp01((z - baseZ) / (ModelEndTop - baseZ)) * 0.246f));
					}
					Tri(b0, b0 + 1, b0 + 2, outward); Tri(b0 + 1, b0 + 3, b0 + 2, outward);
				}
			}
			EndFace(0, -ringPos[0].t);
			EndFace(stations.Count - 1, ringPos[stations.Count - 1].t);

			var mesh = new Mesh { name = "TP_Crossing" };
			if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
			mesh.SetVertices(verts);
			mesh.SetUVs(0, uvs);
			mesh.SetTriangles(tris, 0);
			mesh.RecalculateNormals();
			mesh.RecalculateTangents(); // für die Normal-Map
			mesh.RecalculateBounds();
			return mesh;
		}

		private static Mesh BuildMesh(List<Anchor> anchors, float top, float halfGauge, float width, Vector3 origin)
		{
			var cfg = Uv;
			float globalTile = Mathf.Max(0.1f, cfg.tileLength);

			var tangents = new List<Vector3>();
			List<Vector3> pts = ArcBuilder.Sample(anchors, 0.5f, tangents);
			var dist = new float[pts.Count];
			for (int i = 1; i < pts.Count; i++) dist[i] = dist[i - 1] + Vector3.Distance(pts[i], pts[i - 1]);

			float bw = cfg.barWidth, fd = cfg.faceDepth, gd = cfg.grooveDepth;
			float headIn = halfGauge - RailHeadHalf;          // Innenkante Schienenkopf
			float headOut = halfGauge + RailHeadHalf + 0.004f; // Außenkante Schienenkopf
			float innerEdge = headIn - cfg.flangeGap;          // Innenplatte endet hier (Spurrille dazwischen)
			float yb = top - gd;                               // Spurrillenboden
			float yf = top - fd;                               // Unterkante senkrechte Flächen
			float xc = width + cfg.curbWidth;                  // Bordstein: Beginn der Schräge
			float xo = xc + cfg.curbRun;                       // Bordstein: Fuß der Schräge
			float yc = top - cfg.curbDrop;                     // Höhe Fuß der Schräge
			float yBot = yc - cfg.skirt;                       // Unterkante Schürze/Stirnblech
			UvRegion curbTopL = Mirror(cfg.curbTop), curbSlopeL = Mirror(cfg.curbSlope);

			// Teilflächen quer, von links nach rechts (Draufsicht im Uhrzeigersinn = Oberseite)
			var parts = new List<(float x0, float y0, float x1, float y1, UvRegion r)>();
			// links außen: Schürze, Bordstein-Schräge, Bordstein oben, Außenplatte
			parts.Add((-xo, yBot, -xo, yc, curbSlopeL));
			parts.Add((-xo, yc, -xc, top, curbSlopeL));
			parts.Add((-xc, top, -width, top, curbTopL));
			parts.Add((-width, top, -headOut - bw, top, cfg.plate));        // Außenplatte
			parts.Add((-headOut - bw, top, -headOut, top, cfg.bar));        // Kante an der Schiene
			parts.Add((-headOut, top, -headOut, yf, cfg.face));             // Wand unter dem Schienenkopf (außen)
			// Spurrille links
			parts.Add((-headIn, yb, -innerEdge, yb, cfg.groove));          // Rillenboden
			parts.Add((-innerEdge, yb, -innerEdge, top, cfg.bar));          // Rillenwand
			// Innenplatte mit Kanten
			parts.Add((-innerEdge, top, -innerEdge + bw, top, cfg.bar));
			parts.Add((-innerEdge + bw, top, innerEdge - bw, top, cfg.plate));
			parts.Add((innerEdge - bw, top, innerEdge, top, cfg.bar));
			// Spurrille rechts
			parts.Add((innerEdge, top, innerEdge, yb, cfg.bar));
			parts.Add((innerEdge, yb, headIn, yb, cfg.groove));
			// rechts außen
			parts.Add((headOut, yf, headOut, top, cfg.face));
			parts.Add((headOut, top, headOut + bw, top, cfg.bar));
			parts.Add((headOut + bw, top, width, top, cfg.plate));
			parts.Add((width, top, xc, top, cfg.curbTop));
			parts.Add((xc, top, xo, yc, cfg.curbSlope));
			parts.Add((xo, yc, xo, yBot, cfg.curbSlope));

			var verts = new List<Vector3>();
			var uvs = new List<Vector2>();
			var tris = new List<int>();

			Vector3 Right(int i) { Vector3 t = tangents[i]; return new Vector3(t.z, 0f, -t.x).normalized; }
			Vector3 P(int i, float x, float y) => pts[i] + Right(i) * x + Vector3.up * y - origin;

			void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
			{
				// a–b erste Reihe, c–d zweite Reihe. Sichtbare Seite = cross(c-a, b-a):
				// bei Profilrichtung (dx,dy) zeigt die Fläche nach (-dy, dx), also z. B. links→rechts = nach oben.
				int n = verts.Count;
				verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
				uvs.Add(ua); uvs.Add(ub); uvs.Add(uc); uvs.Add(ud);
				tris.AddRange(new[] { n, n + 2, n + 1, n + 1, n + 2, n + 3 });
			}

			foreach (var (x0, y0, x1, y1, r) in parts)
			{
				float tile = r.tile > 0.01f ? r.tile : globalTile;
				for (int i = 0; i < pts.Count - 1; i++)
				{
					float s0 = dist[i], s1 = dist[i + 1];
					int k = Mathf.FloorToInt((s0 + 1e-4f) / tile);
					float v0 = (s0 - k * tile) / tile, v1 = (s1 - k * tile) / tile;
					Quad(P(i, x0, y0), P(i, x1, y1), P(i + 1, x0, y0), P(i + 1, x1, y1),
						MapUv(r, 0, v0), MapUv(r, 1, v0), MapUv(r, 0, v1), MapUv(r, 1, v1));
				}
			}

			// Viereck mit gewünschter Sichtseite (Wicklung wird passend gewählt)
			void QuadFacing(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, Vector3 outward)
			{
				int n = verts.Count;
				verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
				uvs.Add(ua); uvs.Add(ub); uvs.Add(uc); uvs.Add(ud);
				Vector3 nrm = Vector3.Cross(c - a, b - a);
				if (Vector3.Dot(nrm, outward) >= 0f) tris.AddRange(new[] { n, n + 2, n + 1, n + 1, n + 2, n + 3 });
				else tris.AddRange(new[] { n, n + 1, n + 2, n + 1, n + 3, n + 2 });
			}

			// Stirnseiten wie im Original: Rundstahl folgt dem ganzen Profil (Platte, Bordstein, Schräge),
			// darunter senkrechtes Stirnblech. An den Schienen bleibt eine Aussparung für Schiene und Spurkranz.
			var ef = cfg.endFace != null && cfg.endFace.Length >= 4 ? cfg.endFace : new[] { 0.051f, 0.125f, 0.498f, 0.371f };
			float efTile = Mathf.Max(0.1f, cfg.endFaceTile);
			float vTop = ef[3], vBotF = ef[1], vGrate = cfg.endFaceGrateTop;
			var barRect = cfg.bar.rect != null && cfg.bar.rect.Length >= 4 ? cfg.bar.rect : new[] { 0f, 0.805f, 1f, 0.848f };
			float railFoot = halfGauge + 0.085f; // bis hier reicht der Schienenfuß – darüber nur flaches Blech
			const float lift = 0.003f;

			float VFace(float y, float yCut)
			{
				if (y >= yCut)
				{
					float span = Mathf.Max(0.01f, (top - bw) - yCut);
					return Mathf.Lerp(vTop, vBotF, Mathf.Clamp01(((top - bw) - y) / span));
				}
				float span2 = Mathf.Max(0.01f, yCut - yBot);
				return Mathf.Lerp(vGrate, vBotF, Mathf.Clamp01((yCut - y) / span2));
			}

			int lastIdx = pts.Count - 1;
			foreach (var (i, sgn) in new[] { (0, -1f), (lastIdx, 1f) })
			{
				Vector3 t = tangents[i].normalized * sgn;   // zeigt vom Gleis weg
				Vector3 E(float x, float y) => P(i, x, y);
				Vector3 In(float x, float y) => P(i, x, y) - t * bw;

				var segs = new List<(float x0, float y0, float x1, float y1, float bottom)>
				{
					(-xo, yc, -xc, top, yBot),
					(-xc, top, -width, top, yBot),
					(-width, top, -railFoot, top, yBot),
					(-railFoot, top, -headOut, top, top - 0.12f),
					(-innerEdge, top, innerEdge, top, yBot),
					(headOut, top, railFoot, top, top - 0.12f),
					(railFoot, top, width, top, yBot),
					(width, top, xc, top, yBot),
					(xc, top, xo, yc, yBot),
				};

				foreach (var (x0, y0, x1, y1, bottom) in segs)
				{
					if (x1 - x0 < 0.001f) continue;
					float Y(float x) => Mathf.Lerp(y0, y1, (x - x0) / (x1 - x0));
					float UB(float x) => Mathf.Lerp(barRect[0], barRect[2], (x + xo) / globalTile);

					// Rundstahl: Oberseite (leicht angehoben, über der Platte) und Vorderkante
					Vector3 upN = new Vector3(0f, 1f, 0f);
					QuadFacing(In(x0, y0 + lift), In(x1, y1 + lift), E(x0, y0 + lift), E(x1, y1 + lift),
						new Vector2(UB(x0), barRect[3]), new Vector2(UB(x1), barRect[3]),
						new Vector2(UB(x0), (barRect[1] + barRect[3]) * 0.5f), new Vector2(UB(x1), (barRect[1] + barRect[3]) * 0.5f), upN);
					QuadFacing(E(x0, y0 + lift), E(x1, y1 + lift), E(x0, y0 - bw), E(x1, y1 - bw),
						new Vector2(UB(x0), (barRect[1] + barRect[3]) * 0.5f), new Vector2(UB(x1), (barRect[1] + barRect[3]) * 0.5f),
						new Vector2(UB(x0), barRect[1]), new Vector2(UB(x1), barRect[1]), t);

					// Stirnblech darunter, quer in Kacheln geteilt
					float yCut = yc; // Texturhöhe immer relativ zum Bordsteinfuß, flache Stücke werden nur abgeschnitten
					float xa = x0;
					while (xa < x1 - 0.0005f)
					{
						int k = Mathf.FloorToInt((xa + xo + 1e-4f) / efTile);
						float xb = Mathf.Min(x1, -xo + (k + 1) * efTile);
						float ua = Mathf.Lerp(ef[0], ef[2], (xa + xo) / efTile - k);
						float ub = Mathf.Lerp(ef[0], ef[2], (xb + xo) / efTile - k);
						FaceColumn(xa, xb, Y(xa) - bw, Y(xb) - bw, ua, ub, bottom, yCut, 0);
						xa = xb;
					}
				}

				// Senkrechte Spalte unter einem Profilstück: oben volles Blech bis yCut, darunter Gitterschlitze
				void FaceColumn(float xa, float xb, float yta, float ytb, float ua, float ub, float bottom, float yCut, int depth)
				{
					if (Mathf.Max(yta, ytb) <= bottom + 0.001f) return;
					bool aAbove = yta > yCut + 0.001f, bAbove = ytb > yCut + 0.001f;
					if (aAbove != bAbove && depth < 2 && Mathf.Abs(ytb - yta) > 1e-4f)
					{
						// Stück kreuzt die Trennhöhe (Schräge): dort teilen
						float f = (yCut - yta) / (ytb - yta);
						float xm = Mathf.Lerp(xa, xb, f), um = Mathf.Lerp(ua, ub, f);
						FaceColumn(xa, xm, yta, yCut, ua, um, bottom, yCut, depth + 1);
						FaceColumn(xm, xb, yCut, ytb, um, ub, bottom, yCut, depth + 1);
						return;
					}
					float ya = Mathf.Max(yta, bottom), yb2 = Mathf.Max(ytb, bottom);
					if (aAbove || bAbove)
					{
						float cut = Mathf.Max(yCut, bottom);
						QuadFacing(E(xa, ya), E(xb, yb2), E(xa, cut), E(xb, cut),
							new Vector2(ua, VFace(ya, yCut)), new Vector2(ub, VFace(yb2, yCut)),
							new Vector2(ua, VFace(cut, yCut)), new Vector2(ub, VFace(cut, yCut)), t);
						ya = yb2 = cut;
					}
					if (Mathf.Max(ya, yb2) > bottom + 0.001f)
						QuadFacing(E(xa, ya), E(xb, yb2), E(xa, bottom), E(xb, bottom),
							new Vector2(ua, VFace(ya - 0.0001f, yCut)), new Vector2(ub, VFace(yb2 - 0.0001f, yCut)),
							new Vector2(ua, VFace(bottom, yCut)), new Vector2(ub, VFace(bottom, yCut)), t);
				}
			}

			var mesh = new Mesh { name = "TP_Crossing" };
			if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
			mesh.SetVertices(verts);
			mesh.SetUVs(0, uvs);
			mesh.SetTriangles(tris, 0);
			mesh.RecalculateNormals();
			mesh.RecalculateTangents(); // für die Normal-Map
			mesh.RecalculateBounds();
			return mesh;
		}
	}
}
