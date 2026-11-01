using System;
using System.Collections.Generic;
using UnityEngine;

namespace TrackPlacer.Geometry
{
	/// <summary>Ein Bezier-Ankerpunkt wie bei DVs BezierPoint: Position plus Handles relativ zur Position.
	/// handle1 zeigt rückwärts (eingehend), handle2 vorwärts (ausgehend).</summary>
	public struct Anchor
	{
		public Vector3 position;
		public Vector3 handle1;
		public Vector3 handle2;

		public Anchor(Vector3 position, Vector3 forwardHandle)
		{
			this.position = position;
			handle2 = forwardHandle;
			handle1 = -forwardHandle;
		}
	}

	public sealed class ArcResult
	{
		public readonly List<Anchor> anchors = new List<Anchor>();
		/// <summary>Radius in Metern, float.PositiveInfinity bei Geraden.</summary>
		public float radius = float.PositiveInfinity;
		/// <summary>Gleislänge in Metern (horizontal).</summary>
		public float length;
		/// <summary>Gesamter Richtungswechsel in Grad.</summary>
		public float angleDeg;
		/// <summary>Tatsächlich verwendete (ggf. begrenzte) Pfeilhöhe, + = links.</summary>
		public float sagitta;
		/// <summary>Steigung in Promille (B gegenüber A).</summary>
		public float gradePermille;

		public bool IsStraight => float.IsInfinity(radius);
	}

	/// <summary>
	/// Baut zwischen A und B einen Kreisbogen mit konstantem Radius.
	/// Die Form wird nur durch die Pfeilhöhe bestimmt: der seitliche Abstand der Bogenmitte
	/// zur Sehne A–B. 0 = Gerade, positiv = Bogen nach links (in Blickrichtung A→B), negativ = rechts.
	/// Die Höhe verläuft mit konstanter Steigung von A nach B.
	/// </summary>
	public static class ArcBuilder
	{
		private const float MaxSegmentAngleDeg = 30f;   // pro Bezier-Segment, hält den Fehler weit unter 1 mm
		private const float StraightEpsilon = 0.01f;     // unterhalb von 1 cm Pfeilhöhe gilt es als gerade

		public static ArcResult Build(Vector3 a, Vector3 b, float sagitta)
		{
			var result = new ArcResult();

			Vector2 a2 = new Vector2(a.x, a.z);
			Vector2 b2 = new Vector2(b.x, b.z);
			Vector2 chord = b2 - a2;
			float c = chord.magnitude;
			if (c < 0.001f)
			{
				result.anchors.Add(new Anchor(a, Vector3.zero));
				return result;
			}

			float half = c * 0.5f;
			// Mehr als ein Halbkreis ergibt für Gleise keinen Sinn
			sagitta = Mathf.Clamp(sagitta, -half * 0.999f, half * 0.999f);
			result.sagitta = sagitta;

			float dy = b.y - a.y;
			Vector2 dir = chord / c;
			// Links von dir in Unitys (x,z)-Ebene: (-z, x)
			Vector2 left = new Vector2(-dir.y, dir.x);

			if (Mathf.Abs(sagitta) < StraightEpsilon)
			{
				result.sagitta = 0f;
				result.length = c;
				result.angleDeg = 0f;
				result.gradePermille = dy / c * 1000f;

				Vector3 handle = (b - a) / 3f;
				result.anchors.Add(new Anchor(a, handle));
				result.anchors.Add(new Anchor(b, handle));
				return result;
			}

			float h = Mathf.Abs(sagitta);
			float sign = Mathf.Sign(sagitta);
			float radius = (half * half + h * h) / (2f * h);
			float theta = 4f * Mathf.Atan(h / half);      // Gesamtwinkel im Bogenmaß

			Vector2 mid = (a2 + b2) * 0.5f;
			Vector2 apex = mid + left * sagitta;
			Vector2 center = apex - left * (sign * radius);

			// Drehsinn bestimmen: der Bogen muss durch den Scheitelpunkt laufen
			Vector2 ra = a2 - center;
			float errPlus = (center + Rotate(ra, theta * 0.5f) - apex).sqrMagnitude;
			float errMinus = (center + Rotate(ra, -theta * 0.5f) - apex).sqrMagnitude;
			float rot = errPlus <= errMinus ? 1f : -1f;

			int segments = Mathf.Max(1, Mathf.CeilToInt(theta * Mathf.Rad2Deg / MaxSegmentAngleDeg));
			float phi = theta / segments;
			float handleLen = 4f / 3f * Mathf.Tan(phi / 4f) * radius;
			float arcLen = radius * theta;
			float grade = dy / arcLen;

			for (int i = 0; i <= segments; i++)
			{
				float ang = rot * phi * i;
				Vector2 radial = Rotate(ra, ang);
				Vector2 p2 = center + radial;
				// Tangente = Radialvektor um 90° in Drehrichtung gedreht
				Vector2 tangent = Rotate(radial, rot * Mathf.PI * 0.5f).normalized;

				float y = Mathf.Lerp(a.y, b.y, (float)i / segments);
				Vector3 pos = new Vector3(p2.x, y, p2.y);
				if (i == 0) pos = a;
				if (i == segments) pos = b;

				Vector3 handle = new Vector3(tangent.x, grade, tangent.y) * handleLen;
				result.anchors.Add(new Anchor(pos, handle));
			}

			result.radius = radius;
			result.length = arcLen;
			result.angleDeg = theta * Mathf.Rad2Deg;
			result.gradePermille = grade * 1000f;
			return result;
		}

		// ------------------------------------------------------------------
		// Tangentialer Bogen: Start in P mit fester Richtung dir (z. B. Fortsetzung eines
		// bestehenden Gleisendes). curvature = 1/R, + = links. Länge L horizontal.
		// ------------------------------------------------------------------
		public static ArcResult TangentArc(Vector3 p, Vector2 dir, float curvature, float length, float endY)
		{
			var result = new ArcResult();
			dir = dir.normalized;
			Vector2 p2 = new Vector2(p.x, p.z);
			float grade = length > 0.01f ? (endY - p.y) / length : 0f;
			result.length = length;
			result.gradePermille = grade * 1000f;

			if (Mathf.Abs(curvature) < 1e-6f)
			{
				Vector2 e2 = p2 + dir * length;
				Vector3 e = new Vector3(e2.x, endY, e2.y);
				Vector3 h = (e - p) / 3f;
				result.anchors.Add(new Anchor(p, h));
				result.anchors.Add(new Anchor(e, h));
				return result;
			}

			float radius = 1f / Mathf.Abs(curvature);
			float rot = Mathf.Sign(curvature);              // links = gegen den Uhrzeigersinn
			Vector2 left = new Vector2(-dir.y, dir.x);
			Vector2 center = p2 + left * (rot * radius);
			Vector2 radial = p2 - center;
			float theta = length / radius;

			int segments = Mathf.Max(1, Mathf.CeilToInt(theta * Mathf.Rad2Deg / MaxSegmentAngleDeg));
			float phi = theta / segments;
			float handleLen = 4f / 3f * Mathf.Tan(phi / 4f) * radius;

			for (int i = 0; i <= segments; i++)
			{
				Vector2 r = Rotate(radial, rot * phi * i);
				Vector2 q = center + r;
				Vector2 t = Rotate(r, rot * Mathf.PI * 0.5f).normalized;
				float y = Mathf.Lerp(p.y, endY, (float)i / segments);
				Vector3 pos = i == 0 ? p : new Vector3(q.x, y, q.y);
				result.anchors.Add(new Anchor(pos, new Vector3(t.x, grade, t.y) * handleLen));
			}

			result.radius = radius;
			result.angleDeg = theta * Mathf.Rad2Deg;
			result.sagitta = rot * radius * (1f - Mathf.Cos(theta * 0.5f));
			return result;
		}

		/// <summary>Krümmung und Länge des Tangentialbogens, der von P in Richtung dir startet und durch B läuft.
		/// Liefert false, wenn B hinter P liegt oder der Bogen über ~160° drehen müsste.</summary>
		public static bool SolveTangentArc(Vector3 p, Vector2 dir, Vector3 b, out float curvature, out float length)
		{
			curvature = 0f; length = 0f;
			dir = dir.normalized;
			Vector2 v = new Vector2(b.x - p.x, b.z - p.z);
			float c = v.magnitude;
			if (c < 0.5f) return false;
			float phi = Mathf.Atan2(dir.x * v.y - dir.y * v.x, Vector2.Dot(dir, v)); // + = B liegt links
			if (Mathf.Abs(phi) > 80f * Mathf.Deg2Rad) return false;
			if (Mathf.Abs(phi) < 1e-4f) { length = c; return true; }
			curvature = 2f * Mathf.Sin(phi) / c;
			length = 2f * phi / curvature;
			return true;
		}

		// ------------------------------------------------------------------
		// Übergang zwischen zwei festen Enden (beide eingerastet): kubische Bezier mit
		// Tangenten an beiden Enden. handleFactor = Handle-Länge relativ zur Sehne.
		// dirA = Fahrtrichtung in A, dirB = Fahrtrichtung in B.
		// ------------------------------------------------------------------
		public static ArcResult BetweenTangents(Vector3 a, Vector2 dirA, Vector3 b, Vector2 dirB, float handleFactor)
		{
			var result = new ArcResult();
			float c = new Vector2(b.x - a.x, b.z - a.z).magnitude;
			float hl = Mathf.Max(0.1f, c * handleFactor);
			float grade = c > 0.01f ? (b.y - a.y) / c : 0f;
			dirA = dirA.normalized; dirB = dirB.normalized;
			result.anchors.Add(new Anchor(a, new Vector3(dirA.x, grade, dirA.y) * hl));
			result.anchors.Add(new Anchor(b, new Vector3(dirB.x, grade, dirB.y) * hl));

			var pts = Sample(result.anchors, 1f);
			float len = 0f;
			for (int i = 1; i < pts.Count; i++)
				len += new Vector2(pts[i].x - pts[i - 1].x, pts[i].z - pts[i - 1].z).magnitude;
			result.length = len;
			result.gradePermille = len > 0.01f ? (b.y - a.y) / len * 1000f : 0f;
			result.radius = MinRadius(pts);
			return result;
		}

		/// <summary>Kehrt die Laufrichtung um (für Bögen, die vom Endpunkt B aus konstruiert wurden).</summary>
		public static ArcResult Reverse(ArcResult r)
		{
			r.anchors.Reverse();
			for (int i = 0; i < r.anchors.Count; i++)
			{
				var an = r.anchors[i];
				r.anchors[i] = new Anchor { position = an.position, handle1 = an.handle2, handle2 = an.handle1 };
			}
			r.sagitta = -r.sagitta;
			r.gradePermille = -r.gradePermille;
			return r;
		}

		/// <summary>Kleinster Radius entlang abgetasteter Punkte (Umkreis aus je drei Punkten, horizontal).</summary>
		public static float MinRadius(List<Vector3> pts, int stride = 4)
		{
			float min = float.PositiveInfinity;
			for (int i = stride; i + stride < pts.Count; i += Math.Max(1, stride / 2))
			{
				Vector2 p0 = new Vector2(pts[i - stride].x, pts[i - stride].z);
				Vector2 p1 = new Vector2(pts[i].x, pts[i].z);
				Vector2 p2 = new Vector2(pts[i + stride].x, pts[i + stride].z);
				float a = (p1 - p0).magnitude, b = (p2 - p1).magnitude, cc = (p2 - p0).magnitude;
				float cross = Mathf.Abs((p1.x - p0.x) * (p2.y - p0.y) - (p1.y - p0.y) * (p2.x - p0.x));
				if (cross < 1e-5f) continue;
				min = Mathf.Min(min, a * b * cc / (2f * cross));
			}
			return min;
		}

		private static Vector2 Rotate(Vector2 v, float radians)
		{
			float cs = Mathf.Cos(radians);
			float sn = Mathf.Sin(radians);
			return new Vector2(v.x * cs - v.y * sn, v.x * sn + v.y * cs);
		}

		/// <summary>Tastet die Bezier-Kette in etwa gleichen Abständen ab (für die Vorschau).</summary>
		public static List<Vector3> Sample(List<Anchor> anchors, float step = 1f, List<Vector3>? tangentsOut = null)
		{
			var pts = new List<Vector3>();
			tangentsOut?.Clear();
			if (anchors.Count == 0) return pts;
			if (anchors.Count == 1)
			{
				pts.Add(anchors[0].position);
				tangentsOut?.Add(Vector3.forward);
				return pts;
			}

			for (int s = 0; s < anchors.Count - 1; s++)
			{
				Vector3 p0 = anchors[s].position;
				Vector3 p1 = p0 + anchors[s].handle2;
				Vector3 p3 = anchors[s + 1].position;
				Vector3 p2 = p3 + anchors[s + 1].handle1;

				float approx = Vector3.Distance(p0, p1) + Vector3.Distance(p1, p2) + Vector3.Distance(p2, p3);
				int n = Math.Max(2, Mathf.CeilToInt(approx / Mathf.Max(0.1f, step)));
				for (int i = (s == 0 ? 0 : 1); i <= n; i++)
				{
					float t = (float)i / n;
					pts.Add(Cubic(p0, p1, p2, p3, t));
					tangentsOut?.Add(CubicDerivative(p0, p1, p2, p3, t).normalized);
				}
			}
			return pts;
		}

		private static Vector3 Cubic(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
		{
			float u = 1f - t;
			return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
		}

		private static Vector3 CubicDerivative(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
		{
			float u = 1f - t;
			return 3f * u * u * (p1 - p0) + 6f * u * t * (p2 - p1) + 3f * t * t * (p3 - p2);
		}
	}
}
