using System.Collections.Generic;
using System.Linq;
using TrackPlacer.Data;
using UnityEngine;

namespace TrackPlacer.World
{
	/// <summary>Ein freies Gleisende. Position in statischen Koordinaten, outward zeigt vom Gleis weg
	/// (= Richtung, in die ein angeschlossenes neues Gleis weiterlaufen muss).</summary>
	internal struct TrackEnd
	{
		public Vector3 position;
		public Vector2 outward;
		public bool isStart;
		public RailTrack? track;      // gebautes Gleis (Vanilla, eigenes oder Weichengleis)
		public string? placedId;      // eigenes Gleis/Weiche aus der JSON
		public Junction? junction;    // gesetzt = dies ist der Anfang einer eigenen Weiche
		public TurntableRailTrack? turntable; // gesetzt = Punkt auf dem Rand einer Drehscheibe (nie fest verbinden)
		public bool IsVanilla => track != null && placedId == null;
		public bool IsJunctionIn => junction != null;
	}

	/// <summary>
	/// Sammelt alle freien Gleisenden: Vanilla-Gleise ohne Anschluss und Weiche (z. B. Stumpfgleise),
	/// die Enden der eigenen Gleise und die drei Enden eigener Weichen. Enden mit Prellbock zählen nicht.
	/// Wird bei Änderungen neu aufgebaut, nicht pro Frame.
	/// </summary>
	internal static class TrackEnds
	{
		private static readonly List<TrackEnd> s_ends = new List<TrackEnd>();
		/// <summary>Alle freien Enden, auch mit Prellbock/Schotterende (für Schotterenden und deren Wiederfinden).</summary>
		private static readonly List<TrackEnd> s_raw = new List<TrackEnd>();
		private static bool s_dirty = true;

		public static void MarkDirty() => s_dirty = true;

		public static IReadOnlyList<TrackEnd> All
		{
			get
			{
				if (s_dirty) Rebuild();
				return s_ends;
			}
		}

		public static IReadOnlyList<TrackEnd> Raw
		{
			get
			{
				if (s_dirty) Rebuild();
				return s_raw;
			}
		}

		private static void Rebuild()
		{
			s_ends.Clear();
			s_raw.Clear();
			if (!GameAccess.TracksReady) return; // Welt nicht bereit, später erneut
			s_dirty = false;

			Vector3 shift = GameAccess.OriginShift;

			// 1) Vanilla-Gleise mit freiem Ende – auch Weichengleise (deren weichenseitiges Ende hängt an der
			//    Junction und gilt nicht als frei; das ferne Ende wird frei, wenn das Anschlussgleis entfernt wurde)
			var vanillaJunctions = new HashSet<Junction>();
			foreach (RailTrack t in GameAccess.Registry!.OrderedRailtracks)
			{
				if (t == null || TrackBuilder.IsOwn(t) || VanillaRemover.IsRemoved(t) || !t.gameObject.activeInHierarchy || TurntableSupport.IsBridge(t)) continue;
				BezierCurve c = t.curve;
				if (c == null || c.pointCount < 2) continue;
				if (t.inJunction != null) vanillaJunctions.Add(t.inJunction);
				if (t.outJunction != null) vanillaJunctions.Add(t.outJunction);

				if (t.inJunction == null && t.inBranch?.track == null)
				{
					BezierPoint p = c[0];
					Add(new TrackEnd { position = p.position - shift, outward = Flat(p.position - p.globalHandle2), isStart = true, track = t });
				}
				if (t.outJunction == null && t.outBranch?.track == null)
				{
					BezierPoint p = c.Last();
					Add(new TrackEnd { position = p.position - shift, outward = Flat(p.position - p.globalHandle1), isStart = false, track = t });
				}
			}

			// 1b) Spielweichen, deren Anfang frei ist (zulaufendes Gleis entfernt)
			foreach (var j in vanillaJunctions)
			{
				if (j == null || !j.gameObject.activeInHierarchy || j.inBranch?.track != null || j.outBranches == null) continue;
				Junction.Branch? b = j.outBranches.FirstOrDefault(x => x?.track != null && !VanillaRemover.IsRemoved(x.track));
				if (b == null) continue;
				RailTrack bt = b.track;
				if (bt.curve == null || bt.curve.pointCount < 2) continue;
				BezierPoint p = b.first ? bt.curve[0] : bt.curve.Last();
				Vector3 outward = b.first ? p.position - p.globalHandle2 : p.position - p.globalHandle1;
				Add(new TrackEnd { position = p.position - shift, outward = Flat(outward), isStart = b.first, track = bt, junction = j });
			}

			// 2) Eigene Gleise (Daten aus der JSON, Verbindungsstatus aus dem gebauten Gleis falls vorhanden)
			foreach (var pt in LayoutStore.Layout.tracks)
			{
				if (pt.anchors.Count < 2) continue;
				var anchors = pt.ToAnchors();
				TrackBuilder.Built.TryGetValue(pt.id, out RailTrack built);

				var first = anchors[0];
				var last = anchors[anchors.Count - 1];
				bool startFree = built == null ? !OtherEndNear(pt.id, first.position) : (built.inBranch?.track == null && built.inJunction == null);
				bool endFree = built == null ? !OtherEndNear(pt.id, last.position) : (built.outBranch?.track == null && built.outJunction == null);

				if (startFree) Add(new TrackEnd { position = first.position, outward = Flat(-first.handle2), isStart = true, track = built, placedId = pt.id });
				if (endFree) Add(new TrackEnd { position = last.position, outward = Flat(last.handle2), isStart = false, track = built, placedId = pt.id });
			}

			// 3) Eigene Weichen: Anfang (in) und die Enden von Durchgangs- und Abzweiggleis
			foreach (var ps in LayoutStore.Layout.switches)
			{
				if (!JunctionBuilder.Built.TryGetValue(ps.id, out BuiltSwitch sw) || sw.junction == null) continue;
				if (sw.junction.inBranch?.track == null)
				{
					var e = JunctionBuilder.JunctionInEnd(sw.junction, ps.Position, ps.yaw);
					e.placedId = ps.id;
					e.track = sw.through;
					Add(e);
				}
				foreach (var t in new[] { sw.through, sw.diverge })
				{
					if (t == null) continue;
					bool farIsStart = sw.FarIsStart(t);
					if ((farIsStart ? t.inBranch?.track : t.outBranch?.track) != null) continue;
					BezierPoint p = farIsStart ? t.curve[0] : t.curve.Last();
					Vector3 outward = farIsStart ? p.position - p.globalHandle2 : p.position - p.globalHandle1;
					Add(new TrackEnd { position = p.position - shift, outward = Flat(outward), isStart = farIsStart, track = t, placedId = ps.id });
				}
			}
		}

		/// <summary>Enden mit eigenem Prellbock oder am Drehscheibenrand nicht anbieten (Drehscheibe verbindet selbst).</summary>
		private static void Add(TrackEnd e)
		{
			if (TurntableSupport.IsOnRim(e.position)) return;
			s_raw.Add(e);
			foreach (var b in LayoutStore.Layout.buffers)
				if ((b.Position - e.position).sqrMagnitude < 1.5f * 1.5f) return;
			foreach (var c in LayoutStore.Layout.caps)
				if ((c.Position - e.position).sqrMagnitude < 0.5f * 0.5f) return;
			s_ends.Add(e);
		}

		private static bool OtherEndNear(string id, Vector3 pos)
		{
			float tol2 = TrackBuilder.ConnectTolerance * TrackBuilder.ConnectTolerance;
			foreach (var pt in LayoutStore.Layout.tracks)
			{
				if (pt.id == id || pt.anchors.Count < 2) continue;
				if ((PlacedTrack.ToVec(pt.anchors[0].p) - pos).sqrMagnitude < tol2) return true;
				if ((PlacedTrack.ToVec(pt.anchors[pt.anchors.Count - 1].p) - pos).sqrMagnitude < tol2) return true;
			}
			return false;
		}

		private static Vector2 Flat(Vector3 v)
		{
			var f = new Vector2(v.x, v.z);
			return f.sqrMagnitude > 1e-8f ? f.normalized : Vector2.up;
		}

		/// <summary>Nächstes freies Ende zum Laser-Treffer (horizontal), für das Einrasten im Funkgerät.
		/// Ohne normales Ende in der Nähe: exaktes Brückenende oder Punkt auf dem Rand einer Drehscheibe.</summary>
		public static bool FindSnap(Vector3 staticPos, float radius, out TrackEnd end)
		{
			end = default;
			float best = radius * radius;
			bool found = false;
			foreach (var e in All)
			{
				if (Mathf.Abs(e.position.y - staticPos.y) > 3f) continue;
				float d = new Vector2(e.position.x - staticPos.x, e.position.z - staticPos.z).sqrMagnitude;
				if (d < best) { best = d; end = e; found = true; }
			}
			if (!found && TurntableSupport.FindBridgeSnap(staticPos, radius, out TrackEnd bridge)) { end = bridge; return true; }
			if (!found && TurntableSupport.FindRimSnap(staticPos, radius, out TrackEnd rim)) { end = rim; return true; }
			return found;
		}

		/// <summary>Freies Gleisende für einen Prellbock: auch mit Schotterende, aber ohne Prellbock und nicht am Weichenanfang.</summary>
		public static bool FindBufferSnap(Vector3 staticPos, float radius, out TrackEnd end)
		{
			end = default;
			float best = radius * radius;
			bool found = false;
			foreach (var e in Raw)
			{
				if (e.track == null || e.IsJunctionIn || Mathf.Abs(e.position.y - staticPos.y) > 3f) continue;
				if (LayoutStore.Layout.buffers.Exists(b => (b.Position - e.position).sqrMagnitude < 1.5f * 1.5f)) continue;
				float d = new Vector2(e.position.x - staticPos.x, e.position.z - staticPos.z).sqrMagnitude;
				if (d < best) { best = d; end = e; found = true; }
			}
			return found;
		}

		/// <summary>Freies Gleisende für ein Schotterende: auch mit Prellbock, aber ohne vorhandenes Schotterende und
		/// nicht am Weichenanfang.</summary>
		public static bool FindCapSnap(Vector3 staticPos, float radius, out TrackEnd end)
		{
			end = default;
			float best = radius * radius;
			bool found = false;
			foreach (var e in Raw)
			{
				if (e.track == null || e.IsJunctionIn || Mathf.Abs(e.position.y - staticPos.y) > 3f) continue;
				if (LayoutStore.Layout.caps.Exists(c => (c.Position - e.position).sqrMagnitude < 0.25f)) continue;
				float d = new Vector2(e.position.x - staticPos.x, e.position.z - staticPos.z).sqrMagnitude;
				if (d < best) { best = d; end = e; found = true; }
			}
			return found;
		}

		/// <summary>Gebautes freies Gleisende an genau dieser Position (beim Verbinden).</summary>
		public static bool FindFreeEndAt(Vector3 staticPos, float tolerance, RailTrack exclude, out TrackEnd end)
		{
			MarkDirty();
			foreach (var e in All)
			{
				if (e.track == null || e.track == exclude) continue;
				if ((e.position - staticPos).sqrMagnitude <= tolerance * tolerance) { end = e; return true; }
			}
			end = default;
			return false;
		}
	}
}
