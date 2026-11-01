using System.Globalization;
using UnityEngine;

namespace TrackPlacer.Geometry
{
	/// <summary>
	/// Ein Gleis in Bearbeitung. Je nachdem, welche Enden eingerastet sind, bedeutet das Mausrad etwas anderes:
	///  - Frei:        A und B fest, Mausrad = Pfeilhöhe (Kreisbogen durch A und B)
	///  - Tangente A:  A eingerastet, Richtung fest. Mausrad = Krümmung, Länge bleibt, B wandert mit
	///  - Tangente B:  wie oben, nur vom Ende B aus gerechnet
	///  - Beide:       A und B eingerastet. Mausrad = Handle-Länge (Übergangsbogen / S-Kurve)
	/// Unveränderlich: jede Mausrad-Raste erzeugt einen neuen Draft.
	/// </summary>
	internal sealed class TrackDraft
	{
		public enum Mode { Free, TangentA, TangentB, Both }

		public readonly Mode mode;
		public readonly Vector3 a, b;          // b = angeklickter Punkt (bei Tangente: Ziel für die Länge)
		public readonly Vector2 dirA, dirB;    // Fahrtrichtung in A bzw. B (nur wenn eingerastet)
		public readonly float param;           // Pfeilhöhe | Krümmung | Handle-Faktor
		public readonly float length;          // nur Tangenten-Modi
		public readonly ArcResult arc;

		private TrackDraft(Mode mode, Vector3 a, Vector3 b, Vector2 dirA, Vector2 dirB, float param, float length)
		{
			this.mode = mode; this.a = a; this.b = b; this.dirA = dirA; this.dirB = dirB;
			this.param = param; this.length = length;
			arc = Compute();
		}

		/// <summary>Startet einen Draft. Liefert null, wenn die Geometrie unmöglich ist (z. B. B liegt hinter dem Gleisende).</summary>
		public static TrackDraft? Create(Vector3 a, Vector2? snapDirA, Vector3 b, Vector2? snapDirB)
		{
			if (snapDirA.HasValue && snapDirB.HasValue)
				return new TrackDraft(Mode.Both, a, b, snapDirA.Value, snapDirB.Value, 1f / 3f, 0f);

			if (snapDirA.HasValue)
			{
				if (!ArcBuilder.SolveTangentArc(a, snapDirA.Value, b, out float k, out float len)) return null;
				return new TrackDraft(Mode.TangentA, a, b, snapDirA.Value, default, k, len);
			}

			if (snapDirB.HasValue)
			{
				// Vom Ende B rückwärts nach A rechnen
				Vector2 back = -snapDirB.Value;
				if (!ArcBuilder.SolveTangentArc(b, back, a, out float k, out float len)) return null;
				return new TrackDraft(Mode.TangentB, a, b, default, snapDirB.Value, k, len);
			}

			return new TrackDraft(Mode.Free, a, b, default, default, 0f, 0f);
		}

		private ArcResult Compute()
		{
			switch (mode)
			{
				case Mode.TangentA:
					return ArcBuilder.TangentArc(a, dirA, param, length, b.y);
				case Mode.TangentB:
					return ArcBuilder.Reverse(ArcBuilder.TangentArc(b, -dirB, param, length, a.y));
				case Mode.Both:
					return ArcBuilder.BetweenTangents(a, dirA, b, dirB, param);
				default:
					return ArcBuilder.Build(a, b, param);
			}
		}

		/// <summary>Eine Mausrad-Raste. step = Schrittweite in Metern (Pfeilhöhe-Äquivalent).</summary>
		public TrackDraft Scroll(float direction, float step)
		{
			switch (mode)
			{
				case Mode.TangentA:
				case Mode.TangentB:
				{
					// Pfeilhöhe ≈ k·L²/8 → gleiches Gefühl wie im freien Modus
					float dk = 8f * step / Mathf.Max(1f, length * length);
					// Bei Tangente B wird rückwärts gerechnet, also Drehsinn umkehren (Mausrad hoch = links in Fahrtrichtung A→B)
					float k = param + (mode == Mode.TangentB ? -direction : direction) * dk;
					if (Mathf.Sign(k) != Mathf.Sign(param) && param != 0f) k = 0f;
					// Nicht mehr als ~160° drehen
					float kMax = 2.8f / Mathf.Max(1f, length);
					return new TrackDraft(mode, a, b, dirA, dirB, Mathf.Clamp(k, -kMax, kMax), length);
				}
				case Mode.Both:
				{
					float f = Mathf.Clamp(param + direction * step * 0.2f, 0.05f, 0.9f); // 0,01 m ≙ 0,2 % Handle-Länge
					return new TrackDraft(mode, a, b, dirA, dirB, f, length);
				}
				default:
				{
					float s = param + direction * step;
					if (Mathf.Sign(s) != Mathf.Sign(param) && param != 0f) s = 0f;
					return new TrackDraft(mode, a, b, dirA, dirB, s, length);
				}
			}
		}

		public Vector3 Start => arc.anchors[0].position;
		public Vector3 End => arc.anchors[arc.anchors.Count - 1].position;

		public string Describe(float minRadius)
		{
			var de = De;
			string R(float r) => r.ToString("F0", de);
			string M(float v, int d = 1) => v.ToString("F" + d, de);

			string warn = !arc.IsStraight && arc.radius < minRadius ? " ZU ENG!" : "";
			string len = $"L {M(arc.length, 0)} m";

			switch (mode)
			{
				case Mode.Both:
					return $"Übergang, min. R {(float.IsInfinity(arc.radius) ? "∞" : R(arc.radius))} m{warn}\nHandle {M(param * 100f, 0)} %  {len}";
				case Mode.TangentA:
				case Mode.TangentB:
					if (arc.IsStraight) return $"Gerade (eingerastet)\n{len}";
					return $"R {R(arc.radius)} m {(param > 0 ^ mode == Mode.TangentB ? "links" : "rechts")}{warn}\n{len}  Winkel {M(arc.angleDeg)}°";
				default:
					if (arc.IsStraight) return $"Gerade  {M(arc.length)} m\nMausrad = biegen";
					return $"R {R(arc.radius)} m {(arc.sagitta > 0 ? "links" : "rechts")}{warn}\nPfeil {M(Mathf.Abs(arc.sagitta), 2)} m  {len}";
			}
		}

		public bool IsTight(float minRadius) => !arc.IsStraight && arc.radius < minRadius;

		private static CultureInfo? s_de;
		private static CultureInfo De
		{
			get
			{
				if (s_de != null) return s_de;
				try { s_de = CultureInfo.GetCultureInfo("de-DE"); } catch { s_de = CultureInfo.InvariantCulture; }
				return s_de;
			}
		}
	}
}
