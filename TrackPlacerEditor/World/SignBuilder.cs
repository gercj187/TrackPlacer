using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV.Signs;
using TrackPlacer.Data;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TrackPlacer.World
{
	/// <summary>
	/// Gleisschilder („B ↙ 7S“): Klon eines Schilds aus dem Spiel (Komponente TrackSignHover mit den Textfeldern
	/// subYardID und trackID). Text aus der Gleis-ID (TrackID.SignIDSubYardPart / SignIDTrackPart).
	/// </summary>
	internal static class SignBuilder
	{
		private const string NamePrefix = "[TP]_sign_";
		private static readonly FieldInfo? FSub = typeof(TrackSignHover).GetField("subYardID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		private static readonly FieldInfo? FTrack = typeof(TrackSignHover).GetField("trackID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

		private static readonly Dictionary<string, GameObject> s_built = new Dictionary<string, GameObject>();
		private static GameObject? s_template;
		private static Bounds s_bounds;
		private static float s_lift; // > 0: Vorlage ist nur die Tafel, so hoch wird sie auf einen eigenen Pfosten gesetzt
		private static Material? s_poleMat;
		private const float DefaultLift = 2.2f;   // Unterkante der Tafel über dem Boden (Vorlage ohne Mast)
		private const float PoleWidth = 0.1f;     // Vierkantrohr
		private static GameObject? s_preview;

		public static IReadOnlyDictionary<string, GameObject> Built => s_built;

		public static void OnWorldUnloaded()
		{
			s_built.Clear();
			s_template = null;
			if (s_preview != null) Object.Destroy(s_preview);
			s_preview = null;
		}

		/// <summary>Ganzes Schild (Pfosten + Tafel): vom TrackSignHover nach oben, solange klein (≤ 4 m) und nur ein Schild.</summary>
		private static GameObject? Template
		{
			get
			{
				if (s_template != null) return s_template;
				foreach (var sh in Object.FindObjectsOfType<TrackSignHover>())
				{
					if (sh == null || sh.name.StartsWith(NamePrefix) || sh.GetComponentInParent<SignMarker>() != null) continue;
					Transform t = sh.transform;
					while (t.parent != null && t.parent.GetComponentsInChildren<TrackSignHover>(true).Length == 1 && ObjectDisabler.SizeOf(t.parent.gameObject) <= 4f)
						t = t.parent;
					s_template = t.gameObject;
					s_bounds = CloneHelper.LocalBounds(t);
					s_lift = s_bounds.size.y < 1.0f ? DefaultLift : 0f; // nur Tafel ohne Mast gefunden → eigener Pfosten
					if (s_lift > 0f) Main.Log("Schild-Vorlage ohne Mast – es wird ein einfacher Pfosten ergänzt.");
					Main.Log($"Schild-Vorlage: {CloneHelper.PathOf(t)}");
					break;
				}
				return s_template;
			}
		}

		internal sealed class SignMarker : MonoBehaviour { public string id = ""; public Vector3 baseScale; }

		/// <summary>Schildtext aus der Gleis-ID: (Bereich, Gleis), z. B. ("B", "7S"). Leer, wenn generisch.</summary>
		public static (string sub, string track) TextFor(TrackRef r)
		{
			var rail = r.Rail;
			var logic = rail != null ? StationLink.LogicOf(rail) : null;
			if (logic?.ID == null || logic.ID.IsGeneric()) return ("", "");
			try { return (logic.ID.SignIDSubYardPart, logic.ID.SignIDTrackPart); }
			catch { return ("", ""); }
		}

		private static void SetText(object? tmp, string text)
		{
			if (tmp == null) return;
			try { tmp.GetType().GetProperty("text")?.SetValue(tmp, text); } catch { /* egal */ }
		}

		public static bool Build(PlacedSign placed, out string error)
		{
			error = "";
			if (s_built.ContainsKey(placed.id)) return true;
			if (!TrackBuilder.CheckReady(out error)) return false;
			var template = Template;
			if (template == null) { error = Tr.T("Kein Gleisschild in der Welt gefunden", "No track sign found in the world"); return false; }

			// Text auffrischen, falls die Gleis-ID inzwischen geändert wurde
			var r = ResolveTrack(placed.trackKey);
			if (r != null)
			{
				var (sub, track) = TextFor(r);
				if (track != "") { placed.sub = sub; placed.track = track; }
			}

			// Aufbau: leerer Halter am Boden (Lage + Richtung) → Tafel (Kopie der Vorlage, skalierbar) + eigener Pfosten
			var root = new GameObject(NamePrefix + placed.id);
			root.SetActive(false);
			var marker = root.AddComponent<SignMarker>();
			marker.id = placed.id;
			var panel = CloneHelper.InstantiateInactive(template, "Tafel");
			var hover = panel.GetComponentInChildren<TrackSignHover>(true);
			if (hover != null)
			{
				SetText(FSub?.GetValue(hover), placed.sub);
				SetText(FTrack?.GetValue(hover), placed.track);
			}
			Vector3 baseScale = panel.transform.localScale;
			marker.baseScale = baseScale;
			panel.transform.SetParent(root.transform, false);
			panel.transform.localRotation = Quaternion.identity;
			panel.SetActive(true);
			root.transform.SetPositionAndRotation(placed.Position + GameAccess.OriginShift, Quaternion.Euler(0f, placed.yaw, 0f));
			ApplyPose(root, panel, baseScale, placed);
			root.SetActive(true);
			root.transform.SetParent(GameAccess.Registry!.TrackRootParent, true);
			s_built[placed.id] = root;
			return true;
		}

		private static float ScaleOf(PlacedSign p) => Mathf.Clamp(p.scale <= 0f ? 1f : p.scale, 0.2f, 1f); // nicht größer als im Spiel

		/// <summary>Tafel auf Höhe/Größe setzen und den Pfosten passend (neu) anlegen.</summary>
		private static void ApplyPose(GameObject root, GameObject panel, Vector3 baseScale, PlacedSign placed)
		{
			float sc = ScaleOf(placed);
			float lift = Mathf.Max(0f, s_lift + placed.height); // Unterkante der Tafel über Boden
			panel.transform.localScale = baseScale * sc;
			panel.transform.localPosition = new Vector3(0f, lift - s_bounds.min.y * sc, 0f);
			var old = root.transform.Find("TP_Pfosten");
			if (old != null) Object.DestroyImmediate(old.gameObject);
			if (!placed.hidePole && (s_lift > 0f || placed.height > 0.05f)) AddPole(root, lift, sc);
		}

		/// <summary>Höhe/Größe/Richtung eines gesetzten Schilds ändern (ohne Neuaufbau).</summary>
		public static void SetPose(PlacedSign placed)
		{
			if (!s_built.TryGetValue(placed.id, out var root) || root == null) return;
			var panel = root.transform.Find("Tafel");
			if (panel == null) return;
			// Grundmaßstab aus dem aktuellen Maßstab zurückrechnen (gespeichert ist nur der Faktor)
			var cur = root.GetComponent<SignMarker>();
			Vector3 baseScale = cur != null && cur.baseScale != Vector3.zero ? cur.baseScale : panel.localScale;
			if (cur != null && cur.baseScale == Vector3.zero) cur.baseScale = baseScale;
			root.transform.SetPositionAndRotation(placed.Position + GameAccess.OriginShift, Quaternion.Euler(0f, placed.yaw, 0f));
			ApplyPose(root, panel.gameObject, baseScale, placed);
		}

		/// <summary>Pfosten unter der Tafel: schlankes Vierkantrohr mit Rost-Textur, steckt 0,3 m im Boden.</summary>
		private static void AddPole(GameObject root, float lift, float sc)
		{
			var pole = GameObject.CreatePrimitive(PrimitiveType.Cube);
			pole.name = "TP_Pfosten";
			pole.transform.SetParent(root.transform, false);
			const float below = 0.3f;
			float into = Mathf.Min(0.25f, s_bounds.size.y * sc * 0.5f); // ein Stück hinter/in die Tafel
			float h = lift + below + into;
			float w = PoleWidth * Mathf.Clamp(sc, 0.75f, 2f);
			pole.transform.localPosition = new Vector3(s_bounds.center.x * sc, -below + h * 0.5f, s_bounds.center.z * sc);
			pole.transform.localRotation = Quaternion.identity;
			pole.transform.localScale = new Vector3(w, h, w); // Würfel = 1 Einheit
			var mr = pole.GetComponent<MeshRenderer>();
			mr.sharedMaterial = PoleMaterial();
			// Textur nicht strecken: 1 Bildbreite ≈ 0,16 m, Bild ist doppelt so hoch wie breit
			var mpb = new MaterialPropertyBlock();
			mpb.SetVector("_MainTex_ST", new Vector4(w / 0.16f, h / 0.32f, 0f, 0f));
			mr.SetPropertyBlock(mpb);
		}

		private static Material PoleMaterial()
		{
			if (s_poleMat != null) return s_poleMat;
			var sh = Shader.Find("Standard");
			s_poleMat = new Material(sh) { name = "TP_SignPole" };
			var tex = TextureStore.Get("sign_pole");
			if (tex != null) { s_poleMat.mainTexture = tex; s_poleMat.color = Color.white; }
			else s_poleMat.color = new Color(0.30f, 0.18f, 0.11f);
			// Optionale Karten wie beim Asphalt: sign_pole_n.png (Normal, Unity-Format oder normales RGB),
			// sign_pole_s.png (R = Metallic, G = AO, A = Glanz). Fehlen sie, feste Werte.
			var normal = TextureStore.Get("sign_pole_n", linear: true);
			if (normal != null) { s_poleMat.SetTexture("_BumpMap", normal); s_poleMat.EnableKeyword("_NORMALMAP"); }
			var spec = TextureStore.Get("sign_pole_s", linear: true);
			if (spec != null)
			{
				s_poleMat.SetTexture("_MetallicGlossMap", spec);
				s_poleMat.EnableKeyword("_METALLICGLOSSMAP");
				s_poleMat.SetFloat("_GlossMapScale", 1f);
				s_poleMat.SetTexture("_OcclusionMap", spec);
			}
			else
			{
				if (s_poleMat.HasProperty("_Glossiness")) s_poleMat.SetFloat("_Glossiness", 0.1f);
				if (s_poleMat.HasProperty("_Metallic")) s_poleMat.SetFloat("_Metallic", 0.3f);
			}
			Main.Log($"Schildmast-Material: Farbe {(tex != null ? "ja" : "nein")}, Normal {(normal != null ? "ja" : "nein")}, Spec {(spec != null ? "ja" : "nein")}");
			return s_poleMat;
		}

		private static TrackRef? ResolveTrack(string key)
		{
			if (string.IsNullOrEmpty(key)) return null;
			if (key.StartsWith("v:"))
			{
				string name = key.Substring(2);
				var reg = GameAccess.Registry;
				var t = reg?.OrderedRailtracks.FirstOrDefault(x => x != null && x.name == name);
				return t != null ? new TrackRef { vanilla = t } : null;
			}
			var own = LayoutStore.Find(key);
			return own != null ? new TrackRef { own = own } : null;
		}

		/// <summary>Gesetztes Schild unter dem Laser (Tafel oder Pfosten), sonst null.</summary>
		public static PlacedSign? SignAt(Transform? hit)
		{
			var m = hit != null ? hit.GetComponentInParent<SignMarker>() : null;
			return m != null ? LayoutStore.Layout.signs.Find(x => x.id == m.id) : null;
		}

		public static bool Remove(string id)
		{
			if (s_built.TryGetValue(id, out var go) && go != null) Object.Destroy(go);
			s_built.Remove(id);
			return true;
		}

		public static int BuildAll()
		{
			int n = 0;
			foreach (var s in LayoutStore.Layout.signs)
				if (!s_built.ContainsKey(s.id) && Build(s, out _)) n++;
			return n;
		}

		// ---------- Vorschau ----------

		public static void ShowPreview(Vector3 groundStatic, float yaw)
		{
			if (s_preview == null)
			{
				var t = Template;
				if (t == null) return;
				s_preview = CloneHelper.MakeGhost(t, "[TrackPlacer] Schild-Vorschau");
			}
			s_preview.transform.SetPositionAndRotation(groundStatic - Vector3.up * (s_bounds.min.y - s_lift) + GameAccess.OriginShift, Quaternion.Euler(0f, yaw, 0f));
			if (!s_preview.activeSelf) s_preview.SetActive(true);
		}

		public static void HidePreview()
		{
			if (s_preview != null) s_preview.SetActive(false);
		}
	}
}
