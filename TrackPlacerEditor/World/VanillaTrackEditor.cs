using System;
using System.Linq;
using TrackPlacer.Data;
using UnityEngine;

namespace TrackPlacer.World
{
	/// <summary>Persistente optische Änderungen an normalen Originalgleisen.</summary>
	internal static class VanillaTrackEditor
	{
		public static void OnWorldUnloaded() { }

		public static bool CanEdit(RailTrack? track, out string reason)
		{
			reason = "";
			if (track == null) { reason = Tr.T("kein Gleis", "no track"); return false; }
			if (TrackBuilder.IsOwn(track)) { reason = Tr.T("eigenes Gleis", "own track"); return false; }
			if (VanillaRemover.IsRemoved(track)) { reason = Tr.T("entfernt", "removed"); return false; }
			if (track.isJunctionTrack || VanillaRemover.OwningJunction(track) != null) { reason = Tr.T("Spielweiche", "game switch"); return false; }
			if (TurntableSupport.IsBridge(track)) { reason = Tr.T("Drehscheibe", "turntable"); return false; }
			return true;
		}

		public static VanillaTrackEdit? Find(RailTrack? track)
		{
			if (track == null) return null;
			Vector3 mid = VanillaRemover.Mid(track);
			return LayoutStore.Layout.vanillaTrackEdits.Find(e => e.name == track.name && (e.Mid - mid).sqrMagnitude < 16f);
		}

		public static VanillaTrackEdit GetOrCreate(RailTrack track)
		{
			var e = Find(track);
			if (e != null) return e;
			e = new VanillaTrackEdit
			{
				name = track.name,
				mid = PlacedTrack.FromVec(VanillaRemover.Mid(track)),
				railType = track.railType != null ? track.railType.name : "",
				baseType = track.baseType != null ? track.baseType.name : "",
				style = "ballast",
				age = track.age,
			};
			LayoutStore.Layout.vanillaTrackEdits.Add(e);
			return e;
		}

		public static TrackTypeOption? CurrentOption(RailTrack track)
		{
			var e = Find(track);
			if (e != null)
			{
				if (!string.IsNullOrEmpty(e.overlay)) return TrackTypes.All.Where(o => o.cover).OrderBy(o => Mathf.Abs(o.age - e.age)).FirstOrDefault();
				return TrackTypes.Resolve(e.railType, e.baseType, e.style, e.age);
			}
			return TrackTypes.All.Where(o => !o.cover && o.style == TrackStyle.Ballast)
				.OrderBy(o => Mathf.Abs(o.age - track.age)).FirstOrDefault();
		}

		public static void SetType(VanillaTrackEdit e, TrackTypeOption type)
		{
			e.style = type.StyleId;
			e.age = type.age;
			e.railType = type.RailName;
			e.baseType = type.BaseName;
			e.overlay = type.cover ? "crossing" : "";
		}

		private static PlacedTrack Geometry(VanillaTrackEdit e, RailTrack track)
		{
			var p = new PlacedTrack
			{
				id = e.id,
				railType = e.railType,
				baseType = e.baseType,
				style = e.style,
				age = e.age,
				overlay = e.overlay,
			};
			if (track.curve == null) return p;
			Vector3 shift = GameAccess.OriginShift;
			for (int i = 0; i < track.curve.pointCount; i++)
			{
				BezierPoint bp = track.curve[i];
				p.anchors.Add(new PlacedAnchor
				{
					p = PlacedTrack.FromVec(bp.position - shift),
					h1 = PlacedTrack.FromVec(bp.globalHandle1 - bp.position),
					h2 = PlacedTrack.FromVec(bp.globalHandle2 - bp.position),
				});
			}
			return p;
		}

		public static bool Apply(VanillaTrackEdit e, RailTrack track, out string error)
		{
			error = "";
			if (!CanEdit(track, out error)) return false;
			var type = TrackTypes.Resolve(e.railType, e.baseType, e.style, e.age);
			if (type == null) { error = Tr.T("Gleistyp nicht gefunden", "Track type not found"); return false; }
			if (!TrackBuilder.ApplyType(track, type, out error)) return false;

			OverlayBuilder.Remove(e.id);
			if (!string.IsNullOrEmpty(e.overlay)) OverlayBuilder.Build(Geometry(e, track), track);
			if (!VegetationBuilder.RebuildVanilla(e, track))
			{
				error = "Vegetationsmaske konnte nicht gebaut werden";
				return false;
			}
			return true;
		}

		public static int ApplyAll()
		{
			int count = 0;
			foreach (var e in LayoutStore.Layout.vanillaTrackEdits)
			{
				var track = VanillaRemover.Find(e.name, e.Mid);
				if (track == null) { Main.Log($"Geändertes Spielgleis '{e.name}' nicht gefunden (Spiel-Update?)"); continue; }
				if (Apply(e, track, out string error)) count++;
				else Main.Log($"Spielgleis {e.name} ändern: {error}");
			}
			return count;
		}

		public static float Length(RailTrack track)
		{
			var ps = track.GetKinkedPointSet();
			if (ps?.points == null || ps.points.Length < 2) return 0f;
			float length = 0f;
			for (int i = 1; i < ps.points.Length; i++)
				length += Vector3.Distance((Vector3)ps.points[i - 1].position, (Vector3)ps.points[i].position);
			return length;
		}
	}
}
