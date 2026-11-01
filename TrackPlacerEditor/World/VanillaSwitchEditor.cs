using System;
using System.Collections.Generic;
using System.Linq;
using TrackPlacer.Data;
using UnityEngine;

namespace TrackPlacer.World
{
	/// <summary>
	/// Weichen des Spiels ändern, ohne sie zu ersetzen: Alter (neu/mittel/alt – beide Weichengleise werden mit dem
	/// gewählten Gleistyp neu erzeugt, wie bei eigenen Weichen) und Asphalt-Abdeckung. Gespeichert über Name und
	/// Mittelpunkt des geraden Weichengleises (wie bei geänderten Spielgleisen).
	/// Hebelseite: nicht möglich (das Weichenmodell samt Hebel ist fest im Spiel) – dafür Weiche entfernen und eine
	/// eigene setzen.
	/// </summary>
	internal static class VanillaSwitchEditor
	{
		internal sealed class Info
		{
			public Junction junction = null!;
			public RailTrack through = null!;
			public RailTrack diverge = null!;
			public Vector3 toeStatic;
			public float yaw;
			public Vector3 throughEndLocal;
			public bool divergesLeft;
		}

		/// <summary>Spielweiche zu einem getroffenen Spielgleis (oder null).</summary>
		public static Info? Of(RailTrack? track)
		{
			if (track == null || TrackBuilder.IsOwn(track) || VanillaRemover.IsRemoved(track)) return null;
			var j = VanillaRemover.OwningJunction(track);
			if (j == null || j.outBranches == null || j.outBranches.Count < 2) return null;
			var bThrough = j.outBranches.FirstOrDefault(b => b?.track != null && b.track.name != RailTrack.JUNCTION_DIVERGING_TRACK_NAME);
			var bDiverge = j.outBranches.FirstOrDefault(b => b?.track != null && b.track.name == RailTrack.JUNCTION_DIVERGING_TRACK_NAME);
			if (bThrough == null || bDiverge == null) return null;
			var th = bThrough.track; var dv = bDiverge.track;
			if (th.curve == null || dv.curve == null || th.curve.pointCount < 2 || dv.curve.pointCount < 2) return null;
			Vector3 shift = GameAccess.OriginShift;
			Vector3 toe = (bThrough.first ? th.curve[0].position : th.curve.Last().position) - shift;
			Vector3 thFar = (bThrough.first ? th.curve.Last().position : th.curve[0].position) - shift;
			Vector3 dvFar = (bDiverge.first ? dv.curve.Last().position : dv.curve[0].position) - shift;
			Vector3 f = thFar - toe; f.y = 0f;
			float len = f.magnitude;
			if (len < 0.5f) return null;
			f /= len;
			Vector3 r = new Vector3(f.z, 0f, -f.x);
			return new Info
			{
				junction = j, through = th, diverge = dv, toeStatic = toe,
				yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg,
				throughEndLocal = new Vector3(0f, 0f, len),
				divergesLeft = Vector3.Dot(dvFar - toe, r) < 0f,
			};
		}

		public static VanillaSwitchEdit? Find(Info w)
		{
			Vector3 mid = VanillaRemover.Mid(w.through);
			return LayoutStore.Layout.vanillaSwitchEdits.Find(e => e.throughName == w.through.name && (e.Mid - mid).sqrMagnitude < 16f);
		}

		public static VanillaSwitchEdit GetOrCreate(Info w)
		{
			var e = Find(w);
			if (e != null) return e;
			e = new VanillaSwitchEdit
			{
				throughName = w.through.name,
				mid = PlacedTrack.FromVec(VanillaRemover.Mid(w.through)),
				origRail = w.through.railType != null ? w.through.railType.name : "",
				origBase = w.through.baseType != null ? w.through.baseType.name : "",
				origAge = w.through.age,
			};
			LayoutStore.Layout.vanillaSwitchEdits.Add(e);
			return e;
		}

		public static bool Apply(VanillaSwitchEdit e, Info w, out string error)
		{
			error = "";
			// Alter: −1 = Original (beim ersten Ändern gemerkt)
			TrackTypeOption? type = e.age >= 0f
				? TrackTypes.Resolve("", "", "ballast", e.age)
				: TrackTypes.Resolve(e.origRail, e.origBase, "ballast", e.origAge);
			if (type != null && (e.age >= 0f || e.ageApplied))
			{
				foreach (var t in new[] { w.through, w.diverge })
					if (!TrackBuilder.ApplyType(t, type, out error)) return false;
				e.ageApplied = e.age >= 0f;
			}
			SwitchCoverBuilder.BuildCore(e.id, e.covered, w.toeStatic, w.yaw, w.throughEndLocal, w.divergesLeft, w.through.railType);
			return true;
		}

		public static int ApplyAll()
		{
			int n = 0;
			foreach (var e in LayoutStore.Layout.vanillaSwitchEdits)
			{
				var t = VanillaRemover.Find(e.throughName, e.Mid);
				var w = Of(t);
				if (w == null) { Main.Log($"Geänderte Spielweiche '{e.throughName}' nicht gefunden"); continue; }
				if (Apply(e, w, out string err)) n++;
				else Main.Log($"Spielweiche {e.throughName}: {err}");
			}
			return n;
		}

		/// <summary>Spielweiche wurde entfernt: Abdeckung weg, Eintrag löschen.</summary>
		public static void OnRemoved(IEnumerable<RailTrack> tracks)
		{
			foreach (var t in tracks)
			{
				if (t == null) continue;
				Vector3 mid = VanillaRemover.Mid(t);
				var e = LayoutStore.Layout.vanillaSwitchEdits.Find(x => x.throughName == t.name && (x.Mid - mid).sqrMagnitude < 16f);
				if (e == null) continue;
				SwitchCoverBuilder.Remove(e.id);
				LayoutStore.Layout.vanillaSwitchEdits.Remove(e);
			}
		}

		/// <summary>Eintrag ohne Änderung wieder löschen (Original, keine Abdeckung).</summary>
		public static void CleanUp(VanillaSwitchEdit e)
		{
			if (e.age < 0f && !e.covered) LayoutStore.Layout.vanillaSwitchEdits.Remove(e);
		}
	}
}
