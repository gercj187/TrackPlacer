using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace TrackPlacer.World
{
	/// <summary>
	/// Drehscheiben verbinden ihre Brücke dynamisch mit den Gleisen am Rand (TurntableRailTrack.trackEnds),
	/// abhängig von der Stellung. Die Liste wird vom Spiel nur einmal beim Start gesucht.
	/// Darum: eigene Gleise NIE fest mit der Brücke verbinden, sondern nach jedem Bauen/Entfernen die
	/// Liste neu suchen lassen. Zum Einrasten bieten wir einen beliebigen Punkt auf dem Drehscheibenrand an.
	/// </summary>
	internal static class TurntableSupport
	{
		private static readonly MethodInfo? MUpdateConnection =
			typeof(TurntableRailTrack).GetMethod("UpdateTrackConnection", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

		private static List<TurntableRailTrack>? s_tables;

		public static void OnWorldUnloaded() => s_tables = null;

		public static IReadOnlyList<TurntableRailTrack> All
		{
			get
			{
				if (s_tables == null || s_tables.Exists(t => t == null))
					s_tables = new List<TurntableRailTrack>(Object.FindObjectsOfType<TurntableRailTrack>());
				return s_tables;
			}
		}

		public static bool IsBridge(RailTrack t)
		{
			foreach (var tt in All) if (tt != null && tt.Track == t) return true;
			return false;
		}

		/// <summary>Mittelpunkt (statisch), Radius und Höhe der Brückenenden.</summary>
		public static bool Geometry(TurntableRailTrack tt, out Vector3 center, out float radius)
		{
			center = default; radius = 0f;
			if (tt == null) return false;
			RailTrack? bridge = tt.Track;
			if (bridge == null || bridge.curve == null || bridge.curve.pointCount < 2) return false;
			Vector3 a = bridge.curve[0].position, b = bridge.curve.Last().position;
			center = (a + b) * 0.5f - GameAccess.OriginShift;
			radius = tt.SearchRadius; // = halbe Brückenlänge, genau so sucht das Spiel
			return radius > 1f;
		}

		/// <summary>Liegt der Punkt (statisch) auf dem Rand einer Drehscheibe? Dann dort einrasten.</summary>
		public static bool FindRimSnap(Vector3 staticPos, float tolerance, out TrackEnd end)
		{
			end = default;
			foreach (var tt in All)
			{
				if (!Geometry(tt, out Vector3 c, out float r)) continue;
				Vector2 d = new Vector2(staticPos.x - c.x, staticPos.z - c.z);
				float dist = d.magnitude;
				if (dist < 0.1f || Mathf.Abs(dist - r) > tolerance || Mathf.Abs(staticPos.y - c.y) > 3f) continue;
				Vector2 dir = d / dist;
				end = new TrackEnd
				{
					position = new Vector3(c.x + dir.x * r, c.y, c.z + dir.y * r),
					outward = dir,
					turntable = tt,
				};
				return true;
			}
			return false;
		}

		/// <summary>Die beiden beweglichen Brückenenden als exakte Snap-Punkte anbieten.</summary>
		public static bool FindBridgeSnap(Vector3 staticPos, float tolerance, out TrackEnd end)
		{
			end = default;
			float best = tolerance * tolerance;
			bool found = false;
			Vector3 shift = GameAccess.OriginShift;
			foreach (var tt in All)
			{
				RailTrack? bridge = tt != null ? tt.Track : null;
				if (bridge == null || bridge.curve == null || bridge.curve.pointCount < 2) continue;
				Vector3 a = bridge.curve[0].position - shift;
				Vector3 b = bridge.curve.Last().position - shift;
				Vector3 center = (a + b) * 0.5f;
				foreach (var pair in new[] { (point: a, start: true), (point: b, start: false) })
				{
					if (Mathf.Abs(pair.point.y - staticPos.y) > 3f) continue;
					float d = new Vector2(pair.point.x - staticPos.x, pair.point.z - staticPos.z).sqrMagnitude;
					if (d >= best) continue;
					Vector2 outward = new Vector2(pair.point.x - center.x, pair.point.z - center.z).normalized;
					best = d;
					found = true;
					end = new TrackEnd
					{
						position = pair.point,
						outward = outward.sqrMagnitude > 0.5f ? outward : Vector2.up,
						isStart = pair.start,
						track = bridge,
						turntable = tt,
					};
				}
			}
			return found;
		}

		/// <summary>Liegt ein Gleisende am Rand einer Drehscheibe (dann nicht als normales freies Ende anbieten)?</summary>
		public static bool IsOnRim(Vector3 staticPos)
		{
			foreach (var tt in All)
			{
				if (!Geometry(tt, out Vector3 c, out float r)) continue;
				float dist = new Vector2(staticPos.x - c.x, staticPos.z - c.z).magnitude;
				if (Mathf.Abs(dist - r) <= TurntableRailTrack.SEARCH_RADIUS_ALLOWED_OFFSET + 0.2f && Mathf.Abs(staticPos.y - c.y) < 3f) return true;
			}
			return false;
		}

		/// <summary>Alle Drehscheiben ihre Anschlussgleise neu suchen lassen (nur beim Laden – teuer:
		/// FindTrackEnds durchsucht die ganze Szene nach Gleisen).</summary>
		public static void Refresh()
		{
			foreach (var tt in All) RefreshOne(tt);
		}

		private static void RefreshOne(TurntableRailTrack tt)
		{
			if (tt == null) return;
			try
			{
				tt.trackEnds = tt.FindTrackEnds();
				MUpdateConnection?.Invoke(tt, null);
			}
			catch (System.Exception e) { Main.Log($"Drehscheibe {tt.name}: Aktualisieren fehlgeschlagen: {e.Message}"); }
		}

		/// <summary>Statische Endpunkte von Gleisen (zum späteren RefreshNear, auch wenn das Gleis dann weg ist).</summary>
		public static List<Vector3> EndsOf(params RailTrack?[] tracks)
		{
			var pts = new List<Vector3>();
			Vector3 shift = GameAccess.OriginShift;
			foreach (var t in tracks)
			{
				if (t == null || t.curve == null || t.curve.pointCount < 2) continue;
				pts.Add(t.curve[0].position - shift);
				pts.Add(t.curve.Last().position - shift);
			}
			return pts;
		}

		/// <summary>Nur Drehscheiben neu verbinden, an deren Rand einer der Punkte liegt (sonst nichts tun).</summary>
		public static void RefreshNear(List<Vector3> staticPoints)
		{
			if (staticPoints.Count == 0) return;
			foreach (var tt in All)
			{
				if (!Geometry(tt, out Vector3 c, out float r)) continue;
				foreach (var p in staticPoints)
				{
					float dist = new Vector2(p.x - c.x, p.z - c.z).magnitude;
					if (Mathf.Abs(dist - r) < 1.5f && Mathf.Abs(p.y - c.y) < 3f) { RefreshOne(tt); break; }
				}
			}
		}

		public static void RefreshNear(params RailTrack?[] tracks) => RefreshNear(EndsOf(tracks));
	}
}
