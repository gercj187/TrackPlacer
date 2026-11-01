using System.Collections.Generic;
using TrackPlacer.Geometry;
using DV.OriginShift;
using UnityEngine;

namespace TrackPlacer.Preview
{
	/// <summary>
	/// Zeichnet ein Gleis als zwei Linien im Spurabstand. Alle Koordinaten sind "statisch"
	/// (Weltposition minus WorldMover.currentMove). Das Objekt folgt dem Ursprungs-Shift selbst,
	/// damit die Vorschau nach einem Origin-Shift nicht verrutscht.
	/// </summary>
	public class TrackPreview : MonoBehaviour
	{
		// Normalspur 1435 mm, Linien auf Schienenkopf-Mitte
		private const float HalfGauge = 0.7175f + 0.035f;

		private LineRenderer left = null!;
		private LineRenderer right = null!;
		private readonly List<Vector3> samples = new List<Vector3>();
		private readonly List<Vector3> tangents = new List<Vector3>();

		private static Material? s_material;

		public static TrackPreview Create(string name, Color color, float width = 0.08f)
		{
			var go = new GameObject("[TrackPlacer] " + name);
			var preview = go.AddComponent<TrackPreview>();
			preview.left = MakeLine(go.transform, "left", color, width);
			preview.right = MakeLine(go.transform, "right", color, width);
			return preview;
		}

		private static LineRenderer MakeLine(Transform parent, string name, Color color, float width)
		{
			var go = new GameObject(name);
			go.transform.SetParent(parent, false);
			var lr = go.AddComponent<LineRenderer>();
			lr.useWorldSpace = false;
			lr.widthMultiplier = width;
			lr.positionCount = 0;
			lr.numCapVertices = 2;
			lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			lr.receiveShadows = false;
			lr.sharedMaterial = GetMaterial();
			lr.startColor = color;
			lr.endColor = color;
			return lr;
		}

		internal static Material GetMaterial()
		{
			if (s_material != null) return s_material;
			Shader? shader = Shader.Find("Sprites/Default")
				?? Shader.Find("Hidden/Internal-Colored")
				?? Shader.Find("Unlit/Color");
			s_material = new Material(shader);
			s_material.renderQueue = 4000; // über dem Terrain zeichnen
			return s_material;
		}

		public void SetColor(Color color)
		{
			left.startColor = left.endColor = color;
			right.startColor = right.endColor = color;
		}

		public void SetAnchors(List<Anchor> anchors)
		{
			samples.Clear();
			samples.AddRange(ArcBuilder.Sample(anchors, 1f, tangents));
			Apply();
		}

		public void SetStraight(Vector3 a, Vector3 b)
		{
			samples.Clear();
			tangents.Clear();
			Vector3 t = (b - a).normalized;
			int n = Mathf.Max(2, Mathf.CeilToInt(Vector3.Distance(a, b)));
			for (int i = 0; i <= n; i++)
			{
				samples.Add(Vector3.Lerp(a, b, (float)i / n));
				tangents.Add(t);
			}
			Apply();
		}

		/// <summary>Beliebige Punktfolge (statisch) als Gleis zeichnen, Tangenten aus den Nachbarpunkten.</summary>
		public void SetPolyline(List<Vector3> pts)
		{
			samples.Clear();
			tangents.Clear();
			for (int i = 0; i < pts.Count; i++)
			{
				Vector3 a = pts[Mathf.Max(0, i - 1)], b = pts[Mathf.Min(pts.Count - 1, i + 1)];
				samples.Add(pts[i]);
				tangents.Add((b - a).normalized);
			}
			Apply();
		}

		/// <summary>Einfache Linie durch die Punkte (statisch), ohne Spurabstand – z. B. Umriss eines Objekts.</summary>
		public void SetLine(List<Vector3> pts)
		{
			samples.Clear();
			tangents.Clear();
			var l = new Vector3[pts.Count];
			for (int i = 0; i < pts.Count; i++) l[i] = pts[i] + Vector3.up * 0.05f;
			left.positionCount = l.Length;
			left.SetPositions(l);
			right.positionCount = 0;
		}

		public void Clear()
		{
			samples.Clear();
			tangents.Clear();
			Apply();
		}

		private void Apply()
		{
			int n = samples.Count;
			var l = new Vector3[n];
			var r = new Vector3[n];
			for (int i = 0; i < n; i++)
			{
				Vector3 t = tangents[i];
				Vector3 side = new Vector3(t.z, 0f, -t.x); // rechts der Fahrtrichtung
				if (side.sqrMagnitude < 1e-6f) side = Vector3.right;
				side.Normalize();
				// minimal anheben, damit die Linien nicht im Boden flackern
				Vector3 p = samples[i] + Vector3.up * 0.05f;
				l[i] = p - side * HalfGauge;
				r[i] = p + side * HalfGauge;
			}
			left.positionCount = n;
			right.positionCount = n;
			left.SetPositions(l);
			right.SetPositions(r);
		}

		private void LateUpdate()
		{
			transform.position = WorldMover.currentMove;
		}
	}

	/// <summary>Kleiner senkrechter Pfahl als Markierung für Punkt A, Punkt B und den Laser-Treffer.</summary>
	public class PointMarker : MonoBehaviour
	{
		private LineRenderer line = null!;
		private Vector3 staticPos;

		public static PointMarker Create(string name, Color color)
		{
			var go = new GameObject("[TrackPlacer] " + name);
			var m = go.AddComponent<PointMarker>();
			m.line = go.AddComponent<LineRenderer>();
			m.line.useWorldSpace = false;
			m.line.widthMultiplier = 0.15f;
			m.line.positionCount = 2;
			m.line.sharedMaterial = TrackPreview.GetMaterial();
			m.line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			m.SetColor(color);
			go.SetActive(false);
			return m;
		}

		public void SetColor(Color color)
		{
			line.startColor = color;
			line.endColor = color;
		}

		public void Show(Vector3 staticPosition)
		{
			staticPos = staticPosition;
			line.SetPosition(0, staticPos);
			line.SetPosition(1, staticPos + Vector3.up * 2.5f);
			gameObject.SetActive(true);
		}

		public void Hide() => gameObject.SetActive(false);

		private void LateUpdate()
		{
			transform.position = WorldMover.currentMove;
		}
	}
}
