using CommsRadioAPI;
using DV;
using TrackPlacer.Data;
using TrackPlacer.Geometry;
using TrackPlacer.World;
using UnityEngine;
using InputAction = CommsRadioAPI.InputAction;

namespace TrackPlacer.Radio
{
	// ======================================================================
	// Modus "Gleisbau": Typ → A → B → Höhe A → Höhe B → Bogen → Bauen
	// ======================================================================

	/// <summary>Startzustand im Menü. Mausrad wechselt hier noch normal die Modi.</summary>
	internal sealed class IdleState : AStateBehaviour
	{
		public IdleState(string? message = null) : base(new CommsRadioState(
			titleText: Tr.T("Gleisbau", "Track building"),
			contentText: message ?? Tr.T("Klick = Start · Rad = Menü", "Click = start · Wheel = menu"),
			actionText: Tr.Act("START"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{ }

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous)
		{
			PlacerSession.ClearEdit();
			PlacerSession.HideCursor();
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return PlacerMenu.Return(); // Mausrad = zurück ins Menü
			utility.PlaySound(VanillaSoundCommsRadio.ModeEnter);
			return TrackTypes.OptionCount > 1 ? (AStateBehaviour)new TrackTypeState() : new PickPointState(null, null, 0f);
		}
	}

	/// <summary>Gleistyp wählen (Schotter, feste Fahrbahn, nur Schienen …). Auswahl bleibt für die nächsten Gleise.</summary>
	internal sealed class TrackTypeState : AStateBehaviour
	{
		public TrackTypeState() : base(new CommsRadioState(
			titleText: Tr.T("Gleistyp", "Track type"),
			contentText: Describe(),
			actionText: Tr.Act("WEITER"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{ }

		private static string Describe()
		{
			var cur = TrackTypes.Current;
			if (cur == null) return Tr.T("Keine Typen gefunden", "No types found");
			return Tr.T($"{cur.Label}  ({TrackTypes.Selected + 1}/{TrackTypes.OptionCount})\n{cur.Detail}", $"{cur.Label}  ({TrackTypes.Selected + 1}/{TrackTypes.OptionCount})\n{cur.Detail}");
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			int n = TrackTypes.OptionCount;
			switch (action)
			{
				case InputAction.Up: TrackTypes.Selected = (TrackTypes.Selected + n - 1) % Mathf.Max(1, n); return new TrackTypeState();
				case InputAction.Down: TrackTypes.Selected = (TrackTypes.Selected + 1) % Mathf.Max(1, n); return new TrackTypeState();
				default:
					utility.PlaySound(VanillaSoundCommsRadio.Confirm);
					return new PickPointState(null, null, 0f);
			}
		}
	}

	/// <summary>Punkt A bzw. B setzen. Grüner Cursor = rastet an freiem Gleisende ein. Ins Leere klicken = Abbruch.</summary>
	internal sealed class PickPointState : AStateBehaviour
	{
		private readonly Vector3? a;
		private readonly TrackEnd? aSnap;
		private readonly float aGround;
		private float nextTextUpdate;

		public PickPointState(Vector3? a, TrackEnd? aSnap, float aGround, string? content = null) : base(new CommsRadioState(
			titleText: a == null ? Tr.T("Punkt A", "Point A") : Tr.T("Punkt B", "Point B"),
			contentText: content ?? (a == null ? Tr.T("Laser auf Boden/Gleisende\nKlick = A setzen", "Aim at ground/track end\nClick = set A") : Tr.T("Zum Ende laufen\nKlick = B setzen", "Walk to the end\nClick = set B")),
			actionText: Tr.Act(a == null ? "A SETZEN" : "B SETZEN"),
			buttonBehaviour: ButtonBehaviourType.Ignore))
		{
			this.a = a;
			this.aSnap = aSnap;
			this.aGround = aGround;
		}

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous)
		{
			nextTextUpdate = Time.unscaledTime + 0.25f;
			if (a.HasValue) PlacerSession.ShowA(a.Value);
		}

		public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
		{
			PlacerSession.UpdateHit(utility.SignalOrigin, allowSnap: true);

			if (a.HasValue && PlacerSession.Hit.HasValue)
			{
				var preview = MakeDraft(a.Value, aSnap, PlacerSession.Hit.Value, PlacerSession.HitSnap);
				if (preview != null) PlacerSession.ShowDraft(preview);
				else PlacerSession.ShowStraight(a.Value, PlacerSession.Hit.Value);
			}

			if (Time.unscaledTime < nextTextUpdate) return this;
			nextTextUpdate = Time.unscaledTime + 0.25f;
			string text = BuildText();
			return text == state.lcdContent ? this : new PickPointState(a, aSnap, aGround, text);
		}

		internal static TrackDraft? MakeDraft(Vector3 a, TrackEnd? aSnap, Vector3 b, TrackEnd? bSnap)
		{
			Vector2? dirA = aSnap?.outward;
			Vector2? dirB = bSnap.HasValue ? -bSnap.Value.outward : (Vector2?)null;
			return TrackDraft.Create(a, dirA, b, dirB);
		}

		private string BuildText()
		{
			if (!PlacerSession.Hit.HasValue) return Tr.T("Kein Ziel\nKlick = Abbruch", "No target\nClick = cancel");
			string snap = PlacerSession.HitSnap.HasValue
				? (PlacerSession.HitSnap.Value.turntable != null ? Tr.T("Rastet ein: Drehscheibe", "Snaps to: turntable")
					: PlacerSession.HitSnap.Value.IsVanilla ? Tr.T("Rastet ein: Spielgleis", "Snaps to: game track") : Tr.T("Rastet ein: eigenes Gleis", "Snaps to: own track"))
				: "";
			if (!a.HasValue)
			{
				if (snap == "") return Tr.T("Laser auf Boden/Gleisende\nKlick = A setzen", "Aim at ground/track end\nClick = set A");
				// Höhe über Boden anzeigen: hilft, „Gleishöhe über Boden“ in den Settings zu kalibrieren
				string h = PlacerSession.SnapHeightAboveGround.HasValue ? Tr.T($" ({Fmt.M(PlacerSession.SnapHeightAboveGround.Value, 2)} m ü. B.)", $" ({Fmt.M(PlacerSession.SnapHeightAboveGround.Value, 2)} m above ground)") : "";
				return snap + h + Tr.T("\nKlick = A setzen", "\nClick = set A");
			}

			var draft = MakeDraft(a.Value, aSnap, PlacerSession.Hit.Value, PlacerSession.HitSnap);
			if (draft == null) return Tr.T("Ziel liegt hinter dem\nGleisende – weiter vorne", "Target is behind the\ntrack end – aim further ahead");
			string first = snap != "" ? snap : Tr.T($"Länge {Fmt.M(draft.arc.length)} m", $"Length {Fmt.M(draft.arc.length)} m");
			return Tr.T($"{first}\nSteigung {Fmt.M(draft.arc.gradePermille, 0)} ‰", $"{first}\nGrade {Fmt.M(draft.arc.gradePermille, 0)} ‰");
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return new PickPointState(a, aSnap, aGround);

			if (!PlacerSession.Hit.HasValue)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Cancel);
				return new IdleState();
			}

			Vector3 hit = PlacerSession.Hit.Value;
			TrackEnd? snap = PlacerSession.HitSnap;
			float ground = snap.HasValue ? hit.y : hit.y - Main.Settings.heightOffset;

			if (!a.HasValue)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Confirm);
				return new PickPointState(hit, snap, ground);
			}

			float horiz = new Vector2(hit.x - a.Value.x, hit.z - a.Value.z).magnitude;
			if (horiz < Main.Settings.minLength)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Warning);
				return new PickPointState(a, aSnap, aGround, Tr.T($"Zu kurz (< {Fmt.M(Main.Settings.minLength, 0)} m)\nWeiter weg zielen", $"Too short (< {Fmt.M(Main.Settings.minLength, 0)} m)\nAim further away"));
			}
			if (MakeDraft(a.Value, aSnap, hit, snap) == null)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Warning);
				return new PickPointState(a, aSnap, aGround, Tr.T("Ziel liegt hinter dem\nGleisende – weiter vorne", "Target is behind the\ntrack end – aim further ahead"));
			}

			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			var p = new HeightState.Points(a.Value, aSnap, aGround, hit, snap, ground);
			return HeightState.Next(p, editingB: false);
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not PickPointState) PlacerSession.HideCursor();
		}
	}

	/// <summary>Höhe von A bzw. B per Mausrad (z. B. für einen Damm). Eingerastete Punkte werden übersprungen.</summary>
	internal sealed class HeightState : AStateBehaviour
	{
		internal sealed class Points
		{
			public readonly Vector3 a, b;
			public readonly TrackEnd? aSnap, bSnap;
			public readonly float aGround, bGround;
			public Points(Vector3 a, TrackEnd? aSnap, float aGround, Vector3 b, TrackEnd? bSnap, float bGround)
			{ this.a = a; this.aSnap = aSnap; this.aGround = aGround; this.b = b; this.bSnap = bSnap; this.bGround = bGround; }
			public Points WithA(Vector3 na) => new Points(na, aSnap, aGround, b, bSnap, bGround);
			public Points WithB(Vector3 nb) => new Points(a, aSnap, aGround, nb, bSnap, bGround);
		}

		private readonly Points p;
		private readonly bool editingB;
		private readonly TrackDraft? draft;

		/// <summary>Nächster sinnvoller Schritt: Höhe A (falls frei), Höhe B (falls frei), sonst direkt Bogen.</summary>
		public static AStateBehaviour Next(Points p, bool editingB)
		{
			if (!editingB && !p.aSnap.HasValue) return new HeightState(p, false);
			if (!p.bSnap.HasValue) return new HeightState(p, true);
			var d = PickPointState.MakeDraft(p.a, p.aSnap, p.b, p.bSnap);
			return d != null ? (AStateBehaviour)new BendState(d) : new IdleState(Tr.T("Geometrie ungültig", "Invalid geometry"));
		}

		private HeightState(Points p, bool editingB) : this(p, editingB, PickPointState.MakeDraft(p.a, p.aSnap, p.b, p.bSnap)) { }

		private HeightState(Points p, bool editingB, TrackDraft? draft) : base(new CommsRadioState(
			titleText: editingB ? Tr.T("Höhe B", "Height B") : Tr.T("Höhe A", "Height A"),
			contentText: Describe(p, editingB, draft),
			actionText: Tr.Act("WEITER"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.p = p;
			this.editingB = editingB;
			this.draft = draft;
		}

		private static string Describe(Points p, bool editingB, TrackDraft? d)
		{
			float above = editingB ? p.b.y - p.bGround : p.a.y - p.aGround;
			string sign = above >= 0 ? "+" : "";
			string grade = d != null ? Tr.T($"Steigung {Fmt.M(d.arc.gradePermille, 0)} ‰", $"Grade {Fmt.M(d.arc.gradePermille, 0)} ‰") : "";
			return Tr.T($"{sign}{Fmt.M(above, 2)} m über Boden\n{grade}", $"{sign}{Fmt.M(above, 2)} m above ground\n{grade}");
		}

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous)
		{
			PlacerSession.HideCursor();
			if (draft != null) PlacerSession.ShowDraft(draft);
			else PlacerSession.ShowStraight(p.a, p.b);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Activate)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Confirm);
				return editingB ? FinishBend() : Next(p, editingB: true);
			}

			// Richtung wie gewünscht: Mausrad hoch (vom Körper weg) = Punkt anheben
			float dir = (action == InputAction.Up ? -1f : 1f) * (Main.Settings.invertScroll ? -1f : 1f);
			Vector3 delta = Vector3.up * dir * PlacerSession.NextStep();
			var np = editingB ? p.WithB(p.b + delta) : p.WithA(p.a + delta);
			return new HeightState(np, editingB);
		}

		private AStateBehaviour FinishBend()
		{
			var d = PickPointState.MakeDraft(p.a, p.aSnap, p.b, p.bSnap);
			return d != null ? (AStateBehaviour)new BendState(d) : new IdleState(Tr.T("Geometrie ungültig", "Invalid geometry"));
		}
	}

	/// <summary>Bogen mit dem Mausrad einstellen. Klick = weiter zum Bestätigen.</summary>
	internal sealed class BendState : AStateBehaviour
	{
		private readonly TrackDraft draft;

		public BendState(TrackDraft draft) : base(new CommsRadioState(
			titleText: draft.mode == TrackDraft.Mode.Both ? Tr.T("Übergang", "Transition") : Tr.T("Bogen", "Curve"),
			contentText: draft.Describe(Main.Settings.minRadius),
			actionText: Tr.Act("FERTIG"),
			arrowState: draft.arc.IsStraight ? LCDArrowState.Off : (draft.arc.sagitta > 0 ? LCDArrowState.Left : LCDArrowState.Right),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.draft = draft;
		}

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous)
		{
			PlacerSession.HideCursor();
			PlacerSession.ShowDraft(draft);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			switch (action)
			{
				case InputAction.Activate:
					utility.PlaySound(VanillaSoundCommsRadio.Confirm);
					return new ConfirmState(draft, 0);
				case InputAction.Up:
				case InputAction.Down:
					float dir = (action == InputAction.Up ? 1f : -1f) * (Main.Settings.invertScroll ? -1f : 1f);
					return new BendState(draft.Scroll(dir, PlacerSession.NextStep()));
				default:
					return new BendState(draft);
			}
		}
	}

	/// <summary>Bestätigen: Bauen / Weiter biegen / Verwerfen (Mausrad wählt).</summary>
	internal sealed class ConfirmState : AStateBehaviour
	{
		private static readonly string[] Options = { "BAUEN", "BIEGEN", "VERWERFEN" };

		private readonly TrackDraft draft;
		private readonly int selected;

		public ConfirmState(TrackDraft draft, int selected) : base(new CommsRadioState(
			titleText: Tr.T("Gleis fertig?", "Track done?"),
			contentText: Tr.T($"{TrackTypes.CurrentLabel}, {Fmt.M(draft.arc.length, 0)} m\nMausrad = Auswahl", $"{TrackTypes.CurrentLabel}, {Fmt.M(draft.arc.length, 0)} m\nWheel = select"),
			actionText: Tr.Act(Options[selected]),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.draft = draft;
			this.selected = selected;
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			switch (action)
			{
				case InputAction.Up: return new ConfirmState(draft, (selected + Options.Length - 1) % Options.Length);
				case InputAction.Down: return new ConfirmState(draft, (selected + 1) % Options.Length);
			}

			switch (selected)
			{
				case 0:
					var type = TrackTypes.Current;
					var saved = LayoutStore.Add(draft.Start, draft.End, draft.arc, type?.RailName ?? "", type?.BaseName ?? "",
						TrackTypes.CoverSelected ? "crossing" : "", type?.StyleId ?? "ballast", type?.age ?? -1f);
					bool built = TrackBuilder.Build(saved, out string error);
					PlacerSession.RebuildSaved();
					if (built)
					{
						utility.PlaySound(VanillaSoundCommsRadio.Confirm);
						return new IdleState(Tr.T($"Gebaut, {Fmt.M(draft.arc.length, 0)} m\nKlick = nächstes Gleis", $"Built, {Fmt.M(draft.arc.length, 0)} m\nClick = next track"));
					}
					utility.PlaySound(VanillaSoundCommsRadio.Warning);
					return new IdleState(Tr.T("Gespeichert, Bau fehlgeschl.\n", "Saved, build failed\n") + Trim(error));
				case 1:
					return new BendState(draft);
				default:
					utility.PlaySound(VanillaSoundCommsRadio.Cancel);
					return new IdleState();
			}
		}

		internal static string Trim(string s) => s.Length > 26 ? s.Substring(0, 26) : s;
	}

	// ======================================================================
	// Modus "Weiche": Typ wählen → platzieren (Mausrad dreht, rastet an Gleisenden ein)
	// ======================================================================

	internal sealed class SwitchIdleState : AStateBehaviour
	{
		public SwitchIdleState(string? message = null) : base(new CommsRadioState(
			titleText: Tr.T("Weiche", "Switch"),
			contentText: message ?? Tr.T("Klick = Start · Rad = Menü", "Click = start · Wheel = menu"),
			actionText: Tr.Act("START"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{ }

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous)
		{
			PlacerSession.ClearSwitch();
			PlacerSession.HideCursor();
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return PlacerMenu.Return(); // Mausrad = zurück ins Menü
			if (JunctionBuilder.Variants.Count == 0)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Warning);
				return new SwitchIdleState(Tr.T("Keine Weichen-Vorlagen\nin der Welt gefunden", "No switch templates\nfound in the world"));
			}
			utility.PlaySound(VanillaSoundCommsRadio.ModeEnter);
			return new SwitchTypeState();
		}
	}

	internal sealed class SwitchTypeState : AStateBehaviour
	{
		public SwitchTypeState() : base(new CommsRadioState(
			titleText: JunctionBuilder.PlaceCovered ? Tr.T("Abgedeckte Weiche", "Covered switch") : Tr.T("Weichentyp", "Switch type"),
			contentText: Describe(),
			actionText: Tr.Act("WEITER"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{ }

		private static string Describe()
		{
			var v = JunctionBuilder.CurrentVariant;
			return v == null ? Tr.T("Keine Vorlagen", "No templates") : Tr.T($"{v.Label}\n{v.prefabName} ({JunctionBuilder.SelectedVariant + 1}/{JunctionBuilder.Variants.Count})", $"{v.Label}\n{v.prefabName} ({JunctionBuilder.SelectedVariant + 1}/{JunctionBuilder.Variants.Count})");
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			int n = Mathf.Max(1, JunctionBuilder.Variants.Count);
			switch (action)
			{
				case InputAction.Up: JunctionBuilder.SelectedVariant = (JunctionBuilder.SelectedVariant + n - 1) % n; return new SwitchTypeState();
				case InputAction.Down: JunctionBuilder.SelectedVariant = (JunctionBuilder.SelectedVariant + 1) % n; return new SwitchTypeState();
				default:
					utility.PlaySound(VanillaSoundCommsRadio.Confirm);
					Vector3 f = utility.SignalOrigin.forward;
					return new SwitchPlaceState(Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg);
			}
		}
	}

	internal sealed class SwitchPlaceState : AStateBehaviour
	{
		private static readonly string[] AttachNames = { Tr.T("Weichenanfang", "Switch start"), Tr.T("Ende gerade", "Through end"), Tr.T("Ende Abzweig", "Diverging end") };

		private readonly float yaw;
		private readonly int attach;   // welches Weichenende an das Gleisende rastet
		private float nextTextUpdate;

		public SwitchPlaceState(float yaw, int attach = 0, string? content = null) : base(new CommsRadioState(
			titleText: Tr.T("Weiche setzen", "Place switch"),
			contentText: content ?? Tr.T("Laser auf Boden/Gleisende\nMausrad = drehen", "Aim at ground/track end\nWheel = rotate"),
			actionText: Tr.Act("SETZEN"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.yaw = Mathf.Repeat(yaw, 360f);
			this.attach = attach;
		}

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous) => nextTextUpdate = Time.unscaledTime + 0.25f;

		private static float Angle(Vector2 v) => Mathf.Atan2(v.x, v.y) * Mathf.Rad2Deg;

		/// <summary>Weichen-Pose (Anfangspunkt + Fahrtrichtung). Eingerastet: das gewählte Weichenende sitzt exakt auf dem Gleisende.</summary>
		private bool Pose(SwitchVariant v, out Vector3 pos, out float y, out bool snapped)
		{
			pos = default; y = yaw; snapped = false;
			if (!PlacerSession.Hit.HasValue) return false;
			pos = PlacerSession.Hit.Value;
			if (!PlacerSession.HitSnap.HasValue) return true;

			TrackEnd end = PlacerSession.HitSnap.Value;
			snapped = true;
			// Zwei Weichenanfänge lassen sich nicht direkt verbinden: am Anfang einer anderen Weiche immer einen Ausgang ansetzen
			int at = attach == 0 && end.IsJunctionIn ? 1 : attach;
			if (at == 0)
			{
				// Weichenanfang ans Gleisende: Fahrtrichtung = vom Gleis weg
				y = Angle(end.outward);
				pos = end.position;
				return true;
			}

			// Ausgang ans Gleisende: Ausgangsrichtung der Weiche muss dem Gleis entgegen zeigen
			var list = at == 1 ? v.through : v.diverge;
			Anchor last = list[list.Count - 1];
			Vector2 dLocal = new Vector2(last.handle2.x, last.handle2.z);
			if (dLocal.sqrMagnitude < 1e-6f) dLocal = Vector2.up;
			y = Angle(-end.outward) - Angle(dLocal);
			pos = end.position - SwitchVariant.YawRotation(y) * last.position;
			return true;
		}

		public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
		{
			PlacerSession.UpdateHit(utility.SignalOrigin, allowSnap: true);
			var v = JunctionBuilder.CurrentVariant;
			if (v != null && Pose(v, out Vector3 pos, out float y, out bool snapped)) PlacerSession.ShowSwitch(v, pos, y, snapped);
			else PlacerSession.ClearSwitch();

			if (Time.unscaledTime < nextTextUpdate) return this;
			nextTextUpdate = Time.unscaledTime + 0.25f;
			string text = !PlacerSession.Hit.HasValue ? Tr.T("Kein Ziel\nKlick = Abbruch", "No target\nClick = cancel")
				: PlacerSession.HitSnap.HasValue ? Tr.T($"Rastet ein: {AttachNames[attach == 0 && PlacerSession.HitSnap.Value.IsJunctionIn ? 1 : attach]}{(PlacerSession.HitSnap.Value.IsJunctionIn ? "\nan Weichenanfang" : "")}\nMausrad = anderes Ende", $"Snaps to: {AttachNames[attach == 0 && PlacerSession.HitSnap.Value.IsJunctionIn ? 1 : attach]}{(PlacerSession.HitSnap.Value.IsJunctionIn ? "\nat switch start" : "")}\nWheel = other end")
				: Tr.T($"Richtung {Fmt.M(yaw, 0)}°\nMausrad = drehen", $"Heading {Fmt.M(yaw, 0)}°\nWheel = rotate");
			return text == state.lcdContent ? this : new SwitchPlaceState(yaw, attach, text);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up || action == InputAction.Down)
			{
				float dir = (action == InputAction.Up ? 1f : -1f) * (Main.Settings.invertScroll ? -1f : 1f);
				if (PlacerSession.HitSnap.HasValue)
				{
					// Eingerastet: Mausrad wählt, welches Weichenende an das Gleisende kommt
					int next = (attach + (dir > 0 ? 1 : 2)) % 3;
					return new SwitchPlaceState(yaw, next);
				}
				float deg = PlacerSession.NextAngleStep(); // 1° / Shift 10° / Strg 45°
				return new SwitchPlaceState(yaw + dir * deg, attach);
			}

			var v = JunctionBuilder.CurrentVariant;
			if (v == null || !Pose(v, out Vector3 pos, out float y, out _))
			{
				utility.PlaySound(VanillaSoundCommsRadio.Cancel);
				return new SwitchIdleState();
			}

			var placed = LayoutStore.AddSwitch(v.prefabName, pos, y, JunctionBuilder.PlaceCovered);
			if (JunctionBuilder.Build(placed, out string error))
			{
				PlacerSession.RebuildSaved();
				utility.PlaySound(VanillaSoundCommsRadio.Confirm);
				return new SwitchIdleState((placed.covered ? Tr.T("Abgedeckte Weiche", "Covered switch") : Tr.T("Weiche", "Switch")) + Tr.T(" gesetzt\nKlick = nächste", " placed\nClick = next"));
			}
			LayoutStore.Remove(placed.id);
			utility.PlaySound(VanillaSoundCommsRadio.Warning);
			return new SwitchIdleState(Tr.T("Fehlgeschlagen:\n", "Failed:\n") + ConfirmState.Trim(error));
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not SwitchPlaceState) { PlacerSession.HideCursor(); PlacerSession.ClearSwitch(); }
		}
	}

	// ======================================================================
	// Modus "Prellbock": auf freies Gleisende zielen, klicken
	// ======================================================================

	internal sealed class BufferIdleState : AStateBehaviour
	{
		public BufferIdleState(string? message = null) : base(new CommsRadioState(
			titleText: Tr.T("Prellbock", "Buffer stop"),
			contentText: message ?? Tr.T("Klick = Start · Rad = Menü", "Click = start · Wheel = menu"),
			actionText: Tr.Act("START"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{ }

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous) => PlacerSession.HideCursor();

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return PlacerMenu.Return(); // Mausrad = zurück ins Menü
			utility.PlaySound(VanillaSoundCommsRadio.ModeEnter);
			return new BufferPickState(null);
		}
	}

	internal sealed class BufferPickState : AStateBehaviour
	{
		private readonly bool? snapped;

		public BufferPickState(bool? snapped) : base(new CommsRadioState(
			titleText: Tr.T("Prellbock", "Buffer stop"),
			contentText: snapped == true ? Tr.T("Rastet an Gleisende ein\nKlick = setzen", "Snaps to track end\nClick = place") : Tr.T("Laser auf freies Gleisende\nKlick ins Leere = Abbruch", "Aim at a free track end\nClick on nothing = cancel"),
			actionText: Tr.Act("SETZEN"),
			buttonBehaviour: ButtonBehaviourType.Ignore))
		{
			this.snapped = snapped;
		}

		/// <summary>Auch Enden mit Schotterende anbieten (die normale Einrast-Suche schließt sie aus).</summary>
		private static bool Find(out TrackEnd end)
		{
			end = default;
			return PlacerSession.GroundHit.HasValue && TrackEnds.FindBufferSnap(PlacerSession.GroundHit.Value, Main.Settings.snapRadius, out end);
		}

		public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
		{
			PlacerSession.UpdateHit(utility.SignalOrigin, allowSnap: false);
			bool now = Find(out TrackEnd e);
			if (now) PlacerSession.ShowCursorAt(e.position, true);
			return snapped == now ? this : new BufferPickState(now);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return new BufferPickState(snapped);
			PlacerSession.UpdateHit(utility.SignalOrigin, allowSnap: false);
			if (!Find(out TrackEnd end))
			{
				utility.PlaySound(VanillaSoundCommsRadio.Cancel);
				return new BufferIdleState();
			}
			var placed = LayoutStore.AddBuffer(end.position, BufferBuilder.YawFor(end));
			if (BufferBuilder.Build(placed, out string error))
			{
				// Prellbock immer mit Schotterende (falls das Gleis Schotter hat und noch keins da ist)
				string extra = "";
				if (!LayoutStore.Layout.caps.Exists(c => (c.Position - end.position).sqrMagnitude < 0.25f))
				{
					var cap = LayoutStore.AddCap(end.position);
					if (CapBuilder.Build(cap, out _)) extra = Tr.T(" + Schotterende", " + ballast end");
					else LayoutStore.Remove(cap.id); // z. B. Gleis ohne Schotter
				}
				utility.PlaySound(VanillaSoundCommsRadio.Confirm);
				return new BufferIdleState(Tr.T("Prellbock", "Buffer stop") + extra + Tr.T(" gesetzt\nKlick = nächster", " placed\nClick = next"));
			}
			LayoutStore.Remove(placed.id);
			utility.PlaySound(VanillaSoundCommsRadio.Warning);
			return new BufferIdleState(Tr.T("Fehlgeschlagen:\n", "Failed:\n") + ConfirmState.Trim(error));
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not BufferPickState) PlacerSession.HideCursor();
		}
	}

	// ======================================================================
	// Modus "Schotterende": auf freies Gleisende zielen, klicken (auch an Enden mit Prellbock)
	// ======================================================================

	internal sealed class CapIdleState : AStateBehaviour
	{
		public CapIdleState(string? message = null) : base(new CommsRadioState(
			titleText: Tr.T("Schotterende", "Ballast end"),
			contentText: message ?? Tr.T("Schotter am Gleisende\nschließen\nKlick = Start · Rad = Menü", "Close the ballast\nat a track end\nClick = start · Wheel = menu"),
			actionText: Tr.Act("START"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{ }

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous) => PlacerSession.HideCursor();

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return PlacerMenu.Return(); // Mausrad = zurück ins Menü
			utility.PlaySound(VanillaSoundCommsRadio.ModeEnter);
			return new CapPickState(null);
		}
	}

	internal sealed class CapPickState : AStateBehaviour
	{
		private readonly bool? snapped;
		private TrackEnd? end;

		public CapPickState(bool? snapped) : base(new CommsRadioState(
			titleText: Tr.T("Schotterende", "Ballast end"),
			contentText: snapped == true ? Tr.T("Rastet an Gleisende ein\nKlick = setzen", "Snaps to track end\nClick = place") : Tr.T("Laser auf freies Gleisende\nKlick ins Leere = Abbruch", "Aim at a free track end\nClick on nothing = cancel"),
			actionText: Tr.Act("SETZEN"),
			buttonBehaviour: ButtonBehaviourType.Ignore))
		{
			this.snapped = snapped;
		}

		public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
		{
			PlacerSession.UpdateHit(utility.SignalOrigin, allowSnap: false);
			end = null;
			if (PlacerSession.GroundHit.HasValue && TrackEnds.FindCapSnap(PlacerSession.GroundHit.Value, Main.Settings.snapRadius, out TrackEnd e))
			{
				end = e;
				PlacerSession.ShowCursorAt(e.position, true);
			}
			bool now = end.HasValue;
			return snapped == now ? this : new CapPickState(now);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return this;
			PlacerSession.UpdateHit(utility.SignalOrigin, allowSnap: false);
			if (!PlacerSession.GroundHit.HasValue || !TrackEnds.FindCapSnap(PlacerSession.GroundHit.Value, Main.Settings.snapRadius, out TrackEnd e))
			{
				utility.PlaySound(VanillaSoundCommsRadio.Cancel);
				return new CapIdleState();
			}
			var placed = LayoutStore.AddCap(e.position);
			if (CapBuilder.Build(placed, out string error))
			{
				utility.PlaySound(VanillaSoundCommsRadio.Confirm);
				return new CapIdleState(Tr.T("Schotterende gesetzt\nKlick = nächstes", "Ballast end placed\nClick = next"));
			}
			LayoutStore.Remove(placed.id);
			utility.PlaySound(VanillaSoundCommsRadio.Warning);
			return new CapIdleState(Tr.T("Fehlgeschlagen:\n", "Failed:\n") + ConfirmState.Trim(error));
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not CapPickState) PlacerSession.HideCursor();
		}
	}

	// ======================================================================
	// Modus "Entfernen" (nur eigene Gleise, Weichen, Prellböcke)
	// ======================================================================

	internal sealed class RemoveIdleState : AStateBehaviour
	{
		public RemoveIdleState(string? message = null) : base(new CommsRadioState(
			titleText: Tr.T("Entfernen", "Remove"),
			contentText: message ?? Tr.T("Eigene Objekte + Spielgleise\nKlick = Start · Rad = Menü", "Own objects + game tracks\nClick = start · Wheel = menu"),
			actionText: Tr.Act("START"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{ }

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous)
		{
			PlacerSession.ShowHighlight(null);
			PlacerSession.HideCursor();
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return PlacerMenu.Return(); // Mausrad = zurück ins Menü
			utility.PlaySound(VanillaSoundCommsRadio.ModeEnter);
			return new RemovePickState(null);
		}
	}

	internal sealed class RemovePickState : AStateBehaviour
	{
		private readonly PlacerSession.RemoveTarget? target;

		public RemovePickState(PlacerSession.RemoveTarget? target) : base(new CommsRadioState(
			titleText: Tr.T("Entfernen", "Remove"),
			contentText: target == null ? Tr.T("Laser auf Gleis/Objekt\nKlick ins Leere = Abbruch", "Aim at track/object\nClick on nothing = cancel") : Tr.T($"{target.label}\nKlick = auswählen", $"{target.label}\nClick = select"),
			actionText: Tr.Act(target == null ? "ZIELEN" : "AUSWÄHLEN"),
			buttonBehaviour: ButtonBehaviourType.Ignore))
		{
			this.target = target;
		}

		public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
		{
			PlacerSession.UpdateHit(utility.SignalOrigin, allowSnap: false);
			var t = PlacerSession.Hit.HasValue ? PlacerSession.FindOwnNear(PlacerSession.Hit.Value, 3f) : null;
			if (t?.id == target?.id) return this;
			PlacerSession.ShowHighlight(t);
			return new RemovePickState(t);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return new RemovePickState(target);
			if (target == null)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Cancel);
				return new RemoveIdleState();
			}
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new RemoveConfirmState(target, 1);
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not RemovePickState) PlacerSession.HideCursor();
		}
	}

	internal sealed class RemoveConfirmState : AStateBehaviour
	{
		private static readonly string[] Options = { "ENTFERNEN", "ABBRECHEN" };
		private readonly PlacerSession.RemoveTarget target;
		private readonly int selected;

		public RemoveConfirmState(PlacerSession.RemoveTarget target, int selected) : base(new CommsRadioState(
			titleText: Tr.T("Wirklich entfernen?", "Really remove?"),
			contentText: Tr.T($"{target.label}\nMausrad = Auswahl", $"{target.label}\nWheel = select"),
			actionText: Tr.Act(Options[selected]),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.target = target;
			this.selected = selected;
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up || action == InputAction.Down)
				return new RemoveConfirmState(target, 1 - selected);

			if (selected == 1)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Cancel);
				return new RemoveIdleState();
			}

			string error = "";
			if (target.kind == PlacerSession.TargetKind.Vanilla)
			{
				RailTrack? v = target.vanilla;
				if (v == null || !VanillaRemover.Remove(v, out error))
				{
					utility.PlaySound(VanillaSoundCommsRadio.Warning);
					return new RemoveIdleState(Tr.T("Nicht entfernt:\n", "Not removed:\n") + ConfirmState.Trim(error));
				}
				LayoutStore.AddRemovedVanilla(v.name, VanillaRemover.Mid(v));
				StationLink.Reset(StationLink.VanillaKey(v.name));
				MachineBuilder.OnVanillaTrackRemoved(v.name);
				PlacerSession.RebuildSaved();
				utility.PlaySound(VanillaSoundCommsRadio.Confirm);
				return new RemoveIdleState(Tr.T("Spielgleis entfernt\nKlick = weiteres", "Game track removed\nClick = next"));
			}

			bool ok = target.kind switch
			{
				PlacerSession.TargetKind.Track => TrackBuilder.Remove(target.id, out error),
				PlacerSession.TargetKind.Switch => JunctionBuilder.Remove(target.id, out error),
				PlacerSession.TargetKind.Machine => MachineBuilder.Remove(target.id),
				PlacerSession.TargetKind.Cap => CapBuilder.Remove(target.id),
				PlacerSession.TargetKind.Sign => SignBuilder.Remove(target.id),
				_ => BufferBuilder.Remove(target.id),
			};
			if (!ok)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Warning);
				return new RemoveIdleState(Tr.T("Nicht entfernt:\n", "Not removed:\n") + ConfirmState.Trim(error));
			}
			LayoutStore.Remove(target.id);
			PlacerSession.RebuildSaved();
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new RemoveIdleState(Tr.T($"{target.label} entfernt\nKlick = weiteres", $"{target.label} removed\nClick = next"));
		}
	}
}
