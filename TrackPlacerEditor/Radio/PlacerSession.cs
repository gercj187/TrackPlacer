using System.Collections.Generic;
using DV.OriginShift;
using TrackPlacer.Data;
using TrackPlacer.Geometry;
using TrackPlacer.Preview;
using TrackPlacer.World;
using UnityEngine;

namespace TrackPlacer.Radio
{
	/// <summary>
	/// Hält alles, was über mehrere Funkgerät-Zustände hinweg leben muss: Marker, Vorschau,
	/// letzten Laser-Treffer (inkl. Einrasten) und die Vorschau ungebauter Gleise.
	/// Die States selbst sind kurzlebig (das API erzeugt pro Aktion einen neuen).
	/// </summary>
	internal static class PlacerSession
	{
		public static readonly Color ColorCursor = new Color(1f, 0.6f, 0.1f);
		public static readonly Color ColorSnap = new Color(0.2f, 1f, 0.3f);
		public static readonly Color ColorEdit = new Color(1f, 0.85f, 0.1f);
		public static readonly Color ColorTight = new Color(1f, 0.2f, 0.15f);
		public static readonly Color ColorSaved = new Color(0.2f, 0.85f, 1f);
		public static readonly Color ColorRemove = new Color(1f, 0.1f, 0.1f);

		private static PointMarker? s_cursor;
		private static PointMarker? s_markerA;
		private static PointMarker? s_markerB;
		private static TrackPreview? s_edit;
		private static TrackPreview? s_highlight;
		private static TrackPreview? s_highlight2;
		private static TrackPreview? s_edit2;
		private static readonly List<TrackPreview> s_saved = new List<TrackPreview>();

		/// <summary>Letzter gültiger Treffer des Lasers in statischen Koordinaten, sonst null.</summary>
		public static Vector3? Hit { get; private set; }
		/// <summary>Laserpunkt auf dem Boden (statisch), OHNE Gleishöhen-Aufschlag – für Objekte wie Lademaschinen.</summary>
		public static Vector3? GroundHit { get; private set; }
		/// <summary>Freies Gleisende, an dem der Treffer eingerastet ist.</summary>
		public static TrackEnd? HitSnap { get; private set; }
		/// <summary>Beim Einrasten: Höhe des Gleisendes über dem Boden darunter (zum Kalibrieren des Höhen-Offsets).</summary>
		public static float? SnapHeightAboveGround { get; private set; }

		// ---------- Lebenszyklus ----------

		public static void OnWorldReady()
		{
			s_cursor = s_markerA = s_markerB = null;
			s_edit = s_edit2 = s_highlight = s_highlight2 = null;
			s_saved.Clear();
			TrackEnds.MarkDirty();
			RebuildSaved();
		}

		private static PointMarker Cursor => s_cursor != null ? s_cursor : (s_cursor = PointMarker.Create("Cursor", ColorCursor));
		private static PointMarker MarkerA => s_markerA != null ? s_markerA : (s_markerA = PointMarker.Create("Punkt A", ColorSnap));
		private static PointMarker MarkerB => s_markerB != null ? s_markerB : (s_markerB = PointMarker.Create("Punkt B", ColorSnap));
		private static TrackPreview Edit => s_edit != null ? s_edit : (s_edit = TrackPreview.Create("Bearbeitung", ColorEdit, 0.1f));
		private static TrackPreview Highlight => s_highlight != null ? s_highlight : (s_highlight = TrackPreview.Create("Markierung", ColorRemove, 0.14f));
		private static TrackPreview Highlight2 => s_highlight2 != null ? s_highlight2 : (s_highlight2 = TrackPreview.Create("Markierung 2", ColorRemove, 0.14f));
		private static TrackPreview Edit2 => s_edit2 != null ? s_edit2 : (s_edit2 = TrackPreview.Create("Bearbeitung 2", ColorEdit, 0.1f));

		// ---------- Laser ----------

		/// <param name="allowSnap">Beim Setzen von A/B an freien Gleisenden einrasten.</param>
		public static void UpdateHit(Transform signalOrigin, bool allowSnap)
		{
			Hit = null;
			GroundHit = null;
			HitSnap = null;
			SnapHeightAboveGround = null;

			Ray ray = new Ray(signalOrigin.position, signalOrigin.forward);
			RaycastHit[] hits = Physics.RaycastAll(ray, Main.Settings.maxDistance, ~0, QueryTriggerInteraction.Ignore);
			if (hits.Length > 0)
			{
				System.Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
				Transform ownRoot = signalOrigin.root;
				foreach (var h in hits)
				{
					if (h.transform.IsChildOf(ownRoot)) continue;
					if (h.collider.GetComponentInParent<TrainCar>() != null) continue;
					Hit = h.point - WorldMover.currentMove;
					GroundHit = Hit;
					break;
				}
			}

			if (Hit.HasValue && allowSnap && Main.Settings.snapRadius > 0f
			    && TrackEnds.FindSnap(Hit.Value, Main.Settings.snapRadius, out TrackEnd end))
			{
				HitSnap = end;
				SnapHeightAboveGround = end.position.y - Hit.Value.y;
				Hit = end.position; // exakt auf das Gleisende, ohne Höhen-Offset
				Cursor.SetColor(ColorSnap);
				Cursor.Show(end.position);
				return;
			}

			if (Hit.HasValue)
			{
				Hit = Hit.Value + Vector3.up * Main.Settings.heightOffset;
				Cursor.SetColor(ColorCursor);
				Cursor.Show(Hit.Value);
			}
			else Cursor.Hide();
		}

		/// <summary>Cursor an eine Stelle setzen (grün = eingerastet).</summary>
		public static void ShowCursorAt(Vector3 staticPos, bool snapped)
		{
			Cursor.SetColor(snapped ? ColorSnap : ColorCursor);
			Cursor.Show(staticPos);
		}

		public static void HideCursor() { if (s_cursor != null) s_cursor.Hide(); }

		// ---------- Bearbeitung ----------

		public static void ShowA(Vector3 a)
		{
			MarkerA.Show(a);
			MarkerB.Hide();
		}

		public static void ShowStraight(Vector3 a, Vector3 b)
		{
			MarkerA.Show(a);
			Edit.SetColor(ColorEdit);
			Edit.SetStraight(a, b);
		}

		public static void ShowDraft(TrackDraft draft)
		{
			MarkerA.Show(draft.Start);
			MarkerB.Show(draft.End);
			Edit.SetColor(draft.IsTight(Main.Settings.minRadius) ? ColorTight : ColorEdit);
			Edit.SetAnchors(draft.arc.anchors);
		}

		public static void ClearEdit()
		{
			// Unity-Objekte nicht mit ?. prüfen (umgeht Unitys Null-Check bei zerstörten Objekten)
			if (s_edit != null) s_edit.Clear();
			if (s_edit2 != null) s_edit2.Clear();
			if (s_markerA != null) s_markerA.Hide();
			if (s_markerB != null) s_markerB.Hide();
		}

		// ---------- Entfernen: Gleis unter dem Laser finden ----------

		private static readonly Dictionary<string, List<Vector3>> s_samples = new Dictionary<string, List<Vector3>>();

		public enum TargetKind { Track, Switch, Buffer, Vanilla, Machine, Cap, Sign }

		public sealed class RemoveTarget
		{
			public TargetKind kind;
			public string id = "";
			public string label = "";
			public RailTrack? vanilla;
		}

		/// <summary>Eigenes Objekt (Gleis, Weiche, Prellbock) nächst dem Laser-Treffer, horizontal, max. radius.</summary>
		public static RemoveTarget? FindOwnNear(Vector3 staticPos, float radius, bool allowVanilla = true)
		{
			RemoveTarget? best = null;
			float bestD = radius * radius;

			void Check(IEnumerable<Vector3> pts, TargetKind kind, string id, string label)
			{
				foreach (var p in pts)
				{
					if (Mathf.Abs(p.y - staticPos.y) > 4f) continue;
					float d = new Vector2(p.x - staticPos.x, p.z - staticPos.z).sqrMagnitude;
					if (d < bestD) { bestD = d; best = new RemoveTarget { kind = kind, id = id, label = label }; }
				}
			}

			foreach (var t in LayoutStore.Layout.tracks)
			{
				if (!s_samples.TryGetValue(t.id, out var pts))
					s_samples[t.id] = pts = ArcBuilder.Sample(t.ToAnchors(), 1f);
				Check(pts, TargetKind.Track, t.id, Tr.T($"Gleis {Fmt.M(t.length, 0)} m", $"Track {Fmt.M(t.length, 0)} m"));
			}
			foreach (var sw in LayoutStore.Layout.switches)
			{
				if (!s_samples.TryGetValue(sw.id, out var pts))
				{
					pts = new List<Vector3>();
					var v = FindVariant(sw.prefab);
					if (v != null)
					{
						pts.AddRange(ArcBuilder.Sample(v.Transform(v.through, sw.Position, sw.yaw), 1f));
						pts.AddRange(ArcBuilder.Sample(v.Transform(v.diverge, sw.Position, sw.yaw), 1f));
					}
					else pts.Add(sw.Position);
					s_samples[sw.id] = pts;
				}
				Check(pts, TargetKind.Switch, sw.id, Tr.T("Weiche", "Switch"));
			}
			// Prellböcke bevorzugen (kleines Objekt, sonst kaum zu treffen): innerhalb 2,5 m gewinnt der nächste
			float bufD = 2.5f * 2.5f;
			foreach (var b in LayoutStore.Layout.buffers)
			{
				Vector3 p = b.Position;
				float d = new Vector2(p.x - staticPos.x, p.z - staticPos.z).sqrMagnitude;
				if (Mathf.Abs(p.y - staticPos.y) <= 4f && d < bufD)
				{
					bufD = d;
					best = new RemoveTarget { kind = TargetKind.Buffer, id = b.id, label = Tr.T("Prellbock", "Buffer stop") };
				}
			}
			// Schotterenden (kleines Objekt, innerhalb 2 m bevorzugt)
			float capD = 2f * 2f;
			foreach (var c in LayoutStore.Layout.caps)
			{
				Vector3 p = c.Position;
				float d = new Vector2(p.x - staticPos.x, p.z - staticPos.z).sqrMagnitude;
				if (Mathf.Abs(p.y - staticPos.y) <= 4f && d < capD && d < bufD)
				{
					capD = d;
					best = new RemoveTarget { kind = TargetKind.Cap, id = c.id, label = Tr.T("Schotterende", "Ballast end") };
				}
			}
			// Gleisschilder (innerhalb 1,5 m)
			float signD = 1.5f * 1.5f;
			foreach (var sg in LayoutStore.Layout.signs)
			{
				Vector3 p = sg.Position;
				float d = new Vector2(p.x - staticPos.x, p.z - staticPos.z).sqrMagnitude;
				if (Mathf.Abs(p.y - staticPos.y) <= 4f && d < signD)
				{
					signD = d;
					best = new RemoveTarget { kind = TargetKind.Sign, id = sg.id, label = Tr.T($"Schild {sg.sub} {sg.track}", $"Sign {sg.sub} {sg.track}") };
				}
			}
			// Lademaschinen ebenso bevorzugen (innerhalb 3 m)
			float mD = 3f * 3f;
			foreach (var m in LayoutStore.Layout.machines)
			{
				Vector3 p = m.Position;
				float d = new Vector2(p.x - staticPos.x, p.z - staticPos.z).sqrMagnitude;
				if (Mathf.Abs(p.y - staticPos.y) <= 5f && d < mD)
				{
					mD = d;
					best = new RemoveTarget { kind = TargetKind.Machine, id = m.id, label = Tr.T("Lademaschine", "Loading machine") };
				}
			}
			// Nichts Eigenes getroffen: Spielgleis anbieten
			if (best == null && allowVanilla && Main.Settings.allowVanillaRemoval)
			{
				RailTrack? v = VanillaRemover.FindNear(staticPos, 2.5f);
				if (v != null)
				{
					bool ok = VanillaRemover.CanRemove(v, out string why);
					string what = VanillaRemover.OwningJunction(v) != null ? Tr.T("Spielweiche", "Game switch") : Tr.T("Spielgleis", "Game track");
					best = new RemoveTarget { kind = TargetKind.Vanilla, id = v.GetInstanceID().ToString(), vanilla = v,
						label = ok ? what + " " + Short(v.name) : what + " (" + why + ")" };
				}
			}
			return best;
		}

		/// <summary>Nur eigene Gleise (für Gleis-ID und Maschinen-Zuweisung).</summary>
		public static PlacedTrack? FindOwnTrack(Vector3 staticPos, float radius)
		{
			PlacedTrack? best = null;
			float bestD = radius * radius;
			foreach (var t in LayoutStore.Layout.tracks)
			{
				if (!s_samples.TryGetValue(t.id, out var pts))
					s_samples[t.id] = pts = ArcBuilder.Sample(t.ToAnchors(), 1f);
				foreach (var p in pts)
				{
					if (Mathf.Abs(p.y - staticPos.y) > 4f) continue;
					float d = new Vector2(p.x - staticPos.x, p.z - staticPos.z).sqrMagnitude;
					if (d < bestD) { bestD = d; best = t; }
				}
			}
			return best;
		}

		/// <summary>Eigene Lademaschine nahe dem Laser.</summary>
		public static PlacedMachine? FindMachine(Vector3 staticPos, float radius)
		{
			PlacedMachine? best = null;
			float bestD = radius * radius;
			foreach (var m in LayoutStore.Layout.machines)
			{
				Vector3 p = m.Position;
				if (Mathf.Abs(p.y - staticPos.y) > 5f) continue;
				float d = new Vector2(p.x - staticPos.x, p.z - staticPos.z).sqrMagnitude;
				if (d < bestD) { bestD = d; best = m; }
			}
			return best;
		}

		public static void HighlightTrack(PlacedTrack? t)
		{
			if (s_highlight != null) s_highlight.Clear();
			if (s_highlight2 != null) s_highlight2.Clear();
			if (t != null) Highlight.SetAnchors(t.ToAnchors());
		}

		/// <summary>Umriss (z. B. Lademaschine) als einfache Linie markieren.</summary>
		public static void HighlightOutline(List<Vector3> pts)
		{
			if (s_highlight2 != null) s_highlight2.Clear();
			Highlight.SetLine(pts);
		}

		/// <summary>Gleis unter dem Laser: eigenes Gleis bevorzugt, sonst Spielgleis.</summary>
		public static TrackRef? FindTrackRef(Vector3 staticPos)
		{
			var own = FindOwnTrack(staticPos, 3f);
			if (own != null) return new TrackRef { own = own };
			var v = VanillaRemover.FindNear(staticPos, 2.5f);
			return v != null ? new TrackRef { vanilla = v } : null;
		}

		/// <summary>Gleis markieren (eigenes über die Anker, Spielgleis über seine Punkte).</summary>
		public static void HighlightTrackRef(TrackRef? r)
		{
			if (r?.own != null) { HighlightTrack(r.own); return; }
			HighlightTrack(null);
			if (r?.vanilla != null) Highlight.SetPolyline(PointsOf(r.vanilla));
		}

		private static string Short(string n) => n.Length > 16 ? n.Substring(0, 16) : n;

		private static List<Vector3> PointsOf(RailTrack t)
		{
			var pts = new List<Vector3>();
			var ps = t.GetKinkedPointSet();
			if (ps?.points == null) return pts;
			for (int i = 0; i < ps.points.Length; i += 2) pts.Add((Vector3)ps.points[i].position);
			if (ps.points.Length > 0) pts.Add((Vector3)ps.points[ps.points.Length - 1].position);
			return pts;
		}

		private static SwitchVariant? FindVariant(string prefab)
		{
			foreach (var v in JunctionBuilder.Variants) if (v.prefabName == prefab) return v;
			return null;
		}

		public static void ShowHighlight(RemoveTarget? target)
		{
			if (s_highlight != null) s_highlight.Clear();
			if (s_highlight2 != null) s_highlight2.Clear();
			if (target == null) return;
			switch (target.kind)
			{
				case TargetKind.Track:
					var t = LayoutStore.Find(target.id);
					if (t != null) Highlight.SetAnchors(t.ToAnchors());
					break;
				case TargetKind.Switch:
					var sw = LayoutStore.Layout.switches.Find(x => x.id == target.id);
					var v = sw != null ? FindVariant(sw.prefab) : null;
					if (sw != null && v != null)
					{
						Highlight.SetAnchors(v.Transform(v.through, sw.Position, sw.yaw));
						Highlight2.SetAnchors(v.Transform(v.diverge, sw.Position, sw.yaw));
					}
					break;
				case TargetKind.Vanilla:
					if (target.vanilla != null)
					{
						var j = VanillaRemover.OwningJunction(target.vanilla);
						if (j != null)
						{
							var others = new List<RailTrack>();
							foreach (var br in j.outBranches) if (br?.track != null && br.track != target.vanilla) others.Add(br.track);
							if (others.Count > 0) Highlight2.SetPolyline(PointsOf(others[0]));
						}
						var ps = target.vanilla.GetKinkedPointSet();
						if (ps?.points != null && ps.points.Length > 1)
						{
							var pts = new List<Vector3>();
							for (int i = 0; i < ps.points.Length; i += 2) pts.Add((Vector3)ps.points[i].position);
							pts.Add((Vector3)ps.points[ps.points.Length - 1].position);
							Highlight.SetPolyline(pts);
						}
					}
					break;
				case TargetKind.Sign:
					var sgn = LayoutStore.Layout.signs.Find(x => x.id == target.id);
					if (sgn != null) Highlight.SetLine(new List<Vector3> { sgn.Position, sgn.Position + Vector3.up * 2.5f });
					break;
				case TargetKind.Cap:
					var cp = LayoutStore.Layout.caps.Find(x => x.id == target.id);
					if (cp != null) Highlight.SetLine(new List<Vector3> { cp.Position + new Vector3(-1f, 0f, -1f), cp.Position + new Vector3(1f, 0f, 1f), cp.Position, cp.Position + new Vector3(-1f, 0f, 1f), cp.Position + new Vector3(1f, 0f, -1f) });
					break;
				case TargetKind.Machine:
					var mm = LayoutStore.Layout.machines.Find(x => x.id == target.id);
					if (mm != null) Highlight.SetLine(MachineBuilder.OutlineAt(mm.Position, mm.yaw));
					break;
				case TargetKind.Buffer:
					var b = LayoutStore.Layout.buffers.Find(x => x.id == target.id);
					if (b != null)
					{
						Vector3 f = Quaternion.Euler(0f, b.yaw, 0f) * Vector3.forward;
						Highlight.SetStraight(b.Position - f * 1.5f, b.Position + f * 3f);
					}
					break;
			}
		}

		// ---------- Weichen-Vorschau ----------

		public static void ShowSwitch(SwitchVariant v, Vector3 origin, float yaw, bool snapped)
		{
			Edit.SetColor(snapped ? ColorSnap : ColorEdit);
			Edit2.SetColor(snapped ? ColorSnap : ColorEdit);
			Edit.SetAnchors(v.Transform(v.through, origin, yaw));
			Edit2.SetAnchors(v.Transform(v.diverge, origin, yaw));
		}

		public static void ClearSwitch()
		{
			if (s_edit != null) s_edit.Clear();
			if (s_edit2 != null) s_edit2.Clear();
		}

		// ---------- Ungebaute Gleise als Vorschau ----------

		public static void RebuildSaved()
		{
			s_samples.Clear();
			TrackEnds.MarkDirty();
			foreach (var p in s_saved) if (p != null) Object.Destroy(p.gameObject);
			s_saved.Clear();
			if (!Main.Settings.showUnbuiltPreview) return;

			foreach (var t in LayoutStore.Layout.tracks)
			{
				if (TrackBuilder.Built.ContainsKey(t.id)) continue; // echtes Gleis ist sichtbar
				var p = TrackPreview.Create(Tr.T("Ungebaut ", "Unbuilt ") + t.id, ColorSaved, 0.08f);
				p.SetAnchors(t.ToAnchors());
				s_saved.Add(p);
			}
		}

		// ---------- Mausrad-Schrittweite ----------
		// Normal = fein, Shift = grob, Strg = sehr grob (feste Schritte, keine Beschleunigung)

		public static float NextStep()
		{
			bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
			bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
			if (ctrl) return Mathf.Max(0.001f, Main.Settings.stepCtrl);
			if (shift) return Mathf.Max(0.001f, Main.Settings.stepShift);
			return Mathf.Max(0.001f, Main.Settings.stepNormal);
		}

		/// <summary>Drehschritt in Grad: normal 1°, Shift 10°, Strg 45° (Verhältnis wie bei den Metern).</summary>
		public static float NextAngleStep()
		{
			float m = NextStep() / Mathf.Max(0.001f, Main.Settings.stepNormal);
			return Mathf.Min(m, 45f);
		}
	}
}
