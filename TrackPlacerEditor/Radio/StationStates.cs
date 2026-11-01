using System.Collections.Generic;
using System.Linq;
using CommsRadioAPI;
using DV;
using DV.ThingTypes;
using TrackPlacer.Data;
using TrackPlacer.World;
using UnityEngine;
using InputAction = CommsRadioAPI.InputAction;

namespace TrackPlacer.Radio
{
	// ======================================================================
	// Modus "Gleis-ID": Gleis anzielen (eigenes oder Spielgleis ohne ID) → Bahnhof → Bereich → Nummer → Typ → Speichern
	// ======================================================================

	internal sealed class TrackIdIdleState : AStateBehaviour
	{
		public TrackIdIdleState(string? message = null) : base(new CommsRadioState(
			titleText: Tr.T("Gleis-ID", "Track ID"),
			contentText: message ?? Tr.T("Gleis beim Bahnhof anmelden\nKlick = Start · Rad = Menü", "Register track at a station\nClick = start · Wheel = menu"),
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
			if (!StationLink.StationsReady)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Warning);
				return new TrackIdIdleState(Tr.T("Bahnhöfe noch nicht bereit", "Stations not ready yet"));
			}
			utility.PlaySound(VanillaSoundCommsRadio.ModeEnter);
			return new TrackIdPickState(null);
		}
	}

	internal sealed class TrackIdPickState : AStateBehaviour
	{
		private readonly TrackRef? target;

		public TrackIdPickState(TrackRef? target) : base(new CommsRadioState(
			titleText: Tr.T("Gleis-ID", "Track ID"),
			contentText: target == null
				? Tr.T("Laser auf Gleis\nKlick ins Leere = Abbruch", "Aim at a track\nClick on nothing = cancel")
				: target.Label + (target.Editable ? Tr.T("\nKlick = bearbeiten", "\nClick = edit") : Tr.T("\nID vom Spiel (fest)", "\nID from the game (fixed)")),
			actionText: Tr.Act(target == null ? "ZIELEN" : target.Editable ? "BEARBEITEN" : "–"),
			buttonBehaviour: ButtonBehaviourType.Ignore))
		{
			this.target = target;
		}

		public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
		{
			PlacerSession.UpdateHit(utility.SignalOrigin, allowSnap: false);
			var t = PlacerSession.Hit.HasValue ? PlacerSession.FindTrackRef(PlacerSession.Hit.Value) : null;
			if ((t == null && target == null) || (t != null && t.Same(target))) return this;
			PlacerSession.HighlightTrackRef(t);
			return new TrackIdPickState(t);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return new TrackIdPickState(target);
			if (target == null)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Cancel);
				return new TrackIdIdleState();
			}
			if (!target.Editable)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Warning);
				return this;
			}
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new TrackIdEditState(TrackIdDraft.From(target), TrackIdEditState.Field.Station, 0);
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not TrackIdPickState and not TrackIdEditState) PlacerSession.ShowHighlight(null);
			if (next is not TrackIdPickState) PlacerSession.HideCursor();
		}
	}

	/// <summary>Arbeitskopie der ID, wird erst beim Speichern übernommen.</summary>
	internal sealed class TrackIdDraft
	{
		public TrackRef target = null!;
		public int station;      // Index in StationLink.Stations
		public string sub = "A";
		public int number = 1;
		public string type = "S";

		public static readonly string[] SubYards = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".Select(c => c.ToString()).ToArray();

		public List<StationController> Stations => StationLink.Stations;
		public StationController? Station => Stations.Count == 0 ? null : Stations[Mathf.Clamp(station, 0, Stations.Count - 1)];
		public string Yard => Station != null ? StationLink.YardOf(Station) : "?";
		public string Key => target.Key;

		public static TrackIdDraft From(TrackRef r)
		{
			var d = new TrackIdDraft { target = r };
			string yard = "", sub = "", num = "", type = "";
			Vector3 refPos = Vector3.zero;
			if (r.own != null) { yard = r.own.yardId; sub = r.own.subYard; num = r.own.number; type = r.own.trackType; refPos = r.own.A; }
			else if (r.vanilla != null)
			{
				refPos = VanillaRemover.Mid(r.vanilla);
				var e = r.VanillaEntry;
				if (e != null) { yard = e.yardId; sub = e.subYard; num = e.number; type = e.trackType; }
			}

			var list = d.Stations;
			StationController? s = !string.IsNullOrEmpty(yard) ? StationLink.ByYard(yard) : StationLink.Nearest(refPos);
			d.station = s != null ? Mathf.Max(0, list.IndexOf(s)) : 0;
			if (!string.IsNullOrEmpty(yard))
			{
				d.sub = string.IsNullOrEmpty(sub) ? "A" : sub.ToUpperInvariant();
				d.number = int.TryParse(num, out int n) ? n : 1;
				d.type = string.IsNullOrEmpty(type) ? "S" : type.ToUpperInvariant();
			}
			else
			{
				// Vorschlag: erster Bereich des Bahnhofs, nächste freie Nummer
				var subs = StationLink.VanillaSubYards(d.Yard);
				d.sub = subs.Count > 0 ? subs[0] : "A";
				d.number = StationLink.NextFreeNumber(d.Yard, d.sub, d.Key);
			}
			return d;
		}

		public string FullId => $"{Yard}-{sub}{number}{type}";
		public bool Taken => StationLink.IsTaken(Yard, sub, number, Key);
	}

	internal sealed class TrackIdEditState : AStateBehaviour
	{
		public enum Field { Station, Sub, Number, Type, Confirm }
		private static readonly string[] ConfirmOptions = { "SPEICHERN", "ID LÖSCHEN", "ABBRECHEN" };

		private readonly TrackIdDraft d;
		private readonly Field field;
		private readonly int sel;

		public TrackIdEditState(TrackIdDraft d, Field field, int sel) : base(new CommsRadioState(
			titleText: field switch
			{
				Field.Station => Tr.T("Gleis-ID: Bahnhof", "Track ID: station"),
				Field.Sub => Tr.T("Gleis-ID: Bereich", "Track ID: area"),
				Field.Number => Tr.T("Gleis-ID: Nummer", "Track ID: number"),
				Field.Type => Tr.T("Gleis-ID: Typ", "Track ID: type"),
				_ => Tr.T("Gleis-ID: ", "Track ID: ") + d.FullId,
			},
			contentText: Describe(d, field),
			actionText: Tr.Act(field == Field.Confirm ? ConfirmOptions[sel] : "WEITER"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.d = d;
			this.field = field;
			this.sel = sel;
		}

		private static string Describe(TrackIdDraft d, Field f)
		{
			string Mark(Field x) => x == f ? "> " : "  ";
			string station = d.Station != null ? $"{d.Yard} {StationLink.StationName(d.Station)}" : "?";
			bool vanillaSub = StationLink.VanillaSubYards(d.Yard).Contains(d.sub);
			return Tr.T($"{Mark(Field.Station)}Bahnhof: {station}\n", $"{Mark(Field.Station)}Station: {station}\n") +
			       Tr.T($"{Mark(Field.Sub)}Bereich: {d.sub}{(vanillaSub ? " (vorhanden)" : "")}\n", $"{Mark(Field.Sub)}Area: {d.sub}{(vanillaSub ? " (exists)" : "")}\n") +
			       Tr.T($"{Mark(Field.Number)}Nummer: {d.number}{(d.Taken ? " BELEGT!" : "")}\n", $"{Mark(Field.Number)}Number: {d.number}{(d.Taken ? " TAKEN!" : "")}\n") +
			       Tr.T($"{Mark(Field.Type)}Typ: {d.type} {StationLink.TypeName(d.type)}", $"{Mark(Field.Type)}Type: {d.type} {StationLink.TypeName(d.type)}") +
			       (f == Field.Confirm ? Tr.T("\nMausrad = Auswahl", "\nWheel = select") : "");
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up || action == InputAction.Down)
			{
				int dir = action == InputAction.Down ? 1 : -1;
				Step(dir);
				return new TrackIdEditState(d, field, field == Field.Confirm ? (sel + dir + ConfirmOptions.Length) % ConfirmOptions.Length : sel);
			}

			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			if (field != Field.Confirm) return new TrackIdEditState(d, field + 1, 0);

			switch (sel)
			{
				case 0: return Save(utility);
				case 1:
					StationLink.Reset(d.Key);
					if (d.target.own != null) d.target.own.yardId = d.target.own.subYard = d.target.own.number = d.target.own.trackType = "";
					else if (d.target.vanilla != null) LayoutStore.Layout.vanillaTrackIds.RemoveAll(x => x.name == d.target.vanilla.name);
					LayoutStore.SaveQuiet();
					return new TrackIdIdleState(Tr.T("ID gelöscht", "ID deleted"));
				default:
					utility.PlaySound(VanillaSoundCommsRadio.Cancel);
					return new TrackIdIdleState();
			}
		}

		private void Step(int dir)
		{
			switch (field)
			{
				case Field.Station:
					int n = d.Stations.Count;
					if (n == 0) return;
					d.station = (d.station + dir + n) % n;
					var subs = StationLink.VanillaSubYards(d.Yard);
					d.sub = subs.Count > 0 ? subs[0] : "A";
					d.number = StationLink.NextFreeNumber(d.Yard, d.sub, d.Key);
					break;
				case Field.Sub:
					int i = System.Array.IndexOf(TrackIdDraft.SubYards, d.sub);
					d.sub = TrackIdDraft.SubYards[(Mathf.Max(0, i) + dir + TrackIdDraft.SubYards.Length) % TrackIdDraft.SubYards.Length];
					d.number = StationLink.NextFreeNumber(d.Yard, d.sub, d.Key);
					break;
				case Field.Number:
					int step = PlacerSession.NextStep() >= Main.Settings.stepShift - 0.0001f ? 10 : 1;
					d.number = Mathf.Clamp(d.number + dir * step, 1, 99);
					break;
				case Field.Type:
					int k = System.Array.IndexOf(StationLink.Types, d.type);
					d.type = StationLink.Types[(Mathf.Max(0, k) + dir + StationLink.Types.Length) % StationLink.Types.Length];
					break;
			}
		}

		private AStateBehaviour Save(CommsRadioUtility utility)
		{
			bool ok;
			string error, id;
			if (d.target.own != null)
			{
				var t = d.target.own;
				t.yardId = d.Yard; t.subYard = d.sub; t.number = d.number.ToString("D2"); t.trackType = d.type;
				ok = StationLink.Apply(t, out error);
				id = StationLink.FullId(t);
			}
			else
			{
				var v = d.target.vanilla!;
				var e = d.target.VanillaEntry;
				if (e == null)
				{
					e = new VanillaTrackId { name = v.name, mid = PlacedTrack.FromVec(VanillaRemover.Mid(v)) };
					LayoutStore.Layout.vanillaTrackIds.Add(e);
				}
				e.yardId = d.Yard; e.subYard = d.sub; e.number = d.number.ToString("D2"); e.trackType = d.type;
				ok = StationLink.Apply(e, out error);
				id = StationLink.FullId(e);
			}
			LayoutStore.SaveQuiet();
			if (!ok)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Warning);
				return new TrackIdIdleState(Tr.T("Gespeichert, aber:\n", "Saved, but:\n") + ConfirmState.Trim(error));
			}
			return new TrackIdIdleState(Tr.T($"{id} gespeichert", $"{id} saved") + (d.Taken ? Tr.T("\nACHTUNG: doppelt!", "\nWARNING: duplicate!") : "") + Tr.T("\nKlick = nächstes", "\nClick = next"));
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not TrackIdEditState) PlacerSession.ShowHighlight(null);
		}
	}

	// ======================================================================
	// Modus "Lademaschine": setzen → drehen → Ladung wählen → Gleis zuweisen → fertig
	// Eigene oder Spielmaschine anzielen = bearbeiten / entfernen
	// ======================================================================

	/// <summary>Eigene Maschine (JSON) oder Lademaschine des Spiels.</summary>
	internal sealed class MachineRef
	{
		public PlacedMachine? own;
		public WarehouseMachineController? vanilla;

		public bool Same(MachineRef? o) => o != null && o.own == own && o.vanilla == vanilla;
		public List<Vector3> Outline => own != null ? MachineBuilder.OutlineAt(own.Position, own.yaw) : MachineBuilder.OutlineOf(vanilla!);
		public List<string> Cargo => own != null ? own.cargo : MachineBuilder.CargoOf(vanilla!);

		public void ApplyCargo()
		{
			if (own != null) MachineBuilder.UpdateCargo(own);
			else if (vanilla != null) MachineBuilder.ApplyVanillaCargo(vanilla);
		}

		public string Summary
		{
			get
			{
				if (own != null) return MachineEditState.Summary(own);
				int count = vanilla?.supportedCargoTypes?.Count ?? 0;
				string track = vanilla?.warehouseMachine?.WarehouseTrack?.ID?.FullDisplayID ?? vanilla?.warehouseTrackName ?? "?";
				return Tr.T($"Spielmaschine · {count} Ladung(en)\nGleis {track}", $"Game machine · {count} cargo type(s)\nTrack {track}");
			}
		}
	}

	internal sealed class MachineIdleState : AStateBehaviour
	{
		public MachineIdleState(string? message = null) : base(new CommsRadioState(
			titleText: Tr.T("Lademaschine", "Loading machine"),
			contentText: message ?? Tr.T("Setzen oder bearbeiten\nKlick = Start · Rad = Menü", "Place or edit\nClick = start · Wheel = menu"),
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
			if (!StationLink.StationsReady)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Warning);
				return new MachineIdleState(Tr.T("Bahnhöfe noch nicht bereit", "Stations not ready yet"));
			}
			utility.PlaySound(VanillaSoundCommsRadio.ModeEnter);
			return new MachinePickState(null, false);
		}
	}

	/// <summary>Laser auf Maschine = bearbeiten, auf Boden = neue Maschine dort.</summary>
	internal sealed class MachinePickState : AStateBehaviour
	{
		private readonly MachineRef? target;
		private readonly bool ground;

		public MachinePickState(MachineRef? target, bool ground) : base(new CommsRadioState(
			titleText: Tr.T("Lademaschine", "Loading machine"),
			contentText: target != null
				? Tr.T($"{target.Summary}\nKlick = bearbeiten", $"{target.Summary}\nClick = edit")
				: ground ? Tr.T("Klick = neue Maschine hier", "Click = new machine here") : Tr.T("Laser auf Boden/Maschine\nKlick ins Leere = Abbruch", "Aim at ground/machine\nClick on nothing = cancel"),
			actionText: Tr.Act(target != null ? "BEARBEITEN" : ground ? "SETZEN" : "ZIELEN"),
			buttonBehaviour: ButtonBehaviourType.Ignore))
		{
			this.target = target;
			this.ground = ground;
		}

		public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
		{
			PlacerSession.UpdateHit(utility.SignalOrigin, allowSnap: false);
			MachineRef? m = null;
			if (PlacerSession.Hit.HasValue)
			{
				var own = PlacerSession.FindMachine(PlacerSession.Hit.Value, 2f);
				if (own != null) m = new MachineRef { own = own };
				else
				{
					var v = MachineBuilder.FindVanilla(PlacerSession.Hit.Value, 2f);
					if (v != null) m = new MachineRef { vanilla = v };
				}
			}
			bool g = PlacerSession.GroundHit.HasValue;
			// Vorschau der neuen Maschine folgt dem Laser (Display zum Spieler)
			if (m == null && PlacerSession.GroundHit.HasValue)
				MachineBuilder.ShowPreview(PlacerSession.GroundHit.Value, Mathf.Repeat(utility.SignalOrigin.eulerAngles.y + 180f, 360f));
			else MachineBuilder.HidePreview();
			if (((m == null && target == null) || (m != null && m.Same(target))) && g == ground) return this;
			if (m != null) PlacerSession.HighlightOutline(m.Outline);
			else PlacerSession.ShowHighlight(null);
			return new MachinePickState(m, g);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return new MachinePickState(target, ground);
			if (target != null)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Confirm);
				return target.own != null ? new MachineEditState(target.own, 0) : new VanillaMachineEditState(target, 0);
			}
			if (!PlacerSession.GroundHit.HasValue)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Cancel);
				return new MachineIdleState();
			}
			// Display zum Spieler drehen
			float yaw = utility.SignalOrigin.eulerAngles.y + 180f;
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new MachinePlaceState(PlacerSession.GroundHit.Value, Mathf.Repeat(yaw, 360f));
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not MachinePickState) PlacerSession.HideCursor();
			if (next is not MachinePickState and not MachinePlaceState) MachineBuilder.HidePreview();
			if (next is not MachinePickState and not MachineEditState and not VanillaMachineEditState) PlacerSession.ShowHighlight(null);
		}
	}

	/// <summary>Ausrichten vor dem Setzen (Mausrad dreht, Shift/Strg = größere Schritte).</summary>
	internal sealed class MachinePlaceState : AStateBehaviour
	{
		private readonly Vector3 pos;
		private readonly float yaw;

		public MachinePlaceState(Vector3 pos, float yaw) : base(new CommsRadioState(
			titleText: Tr.T("Lademaschine drehen", "Rotate loading machine"),
			contentText: Tr.T($"Richtung {Fmt.M(yaw, 0)}°\nMausrad = drehen\nKlick = setzen", $"Heading {Fmt.M(yaw, 0)}°\nWheel = rotate\nClick = place"),
			actionText: Tr.Act("SETZEN"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.pos = pos;
			this.yaw = yaw;
		}

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous)
		{
			PlacerSession.ShowHighlight(null);
			MachineBuilder.ShowPreview(pos, yaw);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up || action == InputAction.Down)
			{
				float step = PlacerSession.NextAngleStep() * (action == InputAction.Up ? 1f : -1f);
				return new MachinePlaceState(pos, Mathf.Repeat(yaw + step, 360f));
			}

			var placed = LayoutStore.AddMachine(pos, yaw);
			if (!MachineBuilder.Build(placed, out string error))
			{
				LayoutStore.Remove(placed.id);
				utility.PlaySound(VanillaSoundCommsRadio.Warning);
				return new MachineIdleState(Tr.T("Fehlgeschlagen:\n", "Failed:\n") + ConfirmState.Trim(error));
			}
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new MachineEditState(placed, 0);
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			PlacerSession.ShowHighlight(null);
			if (next is not MachinePlaceState) MachineBuilder.HidePreview();
		}
	}

	/// <summary>Menü einer eigenen Maschine.</summary>
	internal sealed class MachineEditState : AStateBehaviour
	{
		private static readonly string[] Options = { "LADUNG", "GLEIS ZUWEISEN", "DREHEN", "HÖHE", "ENTFERNEN", "FERTIG" };
		private readonly PlacedMachine m;
		private readonly int sel;

		public MachineEditState(PlacedMachine m, int sel, string? message = null) : base(new CommsRadioState(
			titleText: Tr.T("Lademaschine", "Loading machine"),
			contentText: (message != null ? message + "\n" : "") + Summary(m) + Tr.T("\nMausrad = Auswahl", "\nWheel = select"),
			actionText: Tr.Act(Options[sel]),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.m = m;
			this.sel = sel;
		}

		public static string Summary(PlacedMachine m)
		{
			string track;
			if (!string.IsNullOrEmpty(m.trackId))
			{
				var t = LayoutStore.Find(m.trackId);
				track = t == null ? Tr.T("kein Gleis", "no track") : (string.IsNullOrEmpty(t.yardId) ? Tr.T("Gleis ohne ID", "track without ID") : StationLink.FullId(t));
			}
			else if (!string.IsNullOrEmpty(m.vanillaTrack))
			{
				var rail = MachineBuilder.ResolveTrack(m);
				track = rail != null ? new TrackRef { vanilla = rail }.CurrentId : Tr.T("Spielgleis fehlt", "game track missing");
				if (track == "") track = Tr.T("Spielgleis ohne ID", "game track without ID");
			}
			else track = Tr.T("kein Gleis", "no track");
			return Tr.T($"{m.cargo.Count} Ladung(en) · {track}", $"{m.cargo.Count} cargo type(s) · {track}");
		}

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous) => PlacerSession.HighlightOutline(MachineBuilder.OutlineAt(m.Position, m.yaw));

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up) return new MachineEditState(m, (sel + Options.Length - 1) % Options.Length);
			if (action == InputAction.Down) return new MachineEditState(m, (sel + 1) % Options.Length);

			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			switch (sel)
			{
				case 0: return new MachineCargoState(new MachineRef { own = m }, 1);
				case 1:
					if (m.cargo.Count == 0) return new MachineEditState(m, 0, Tr.T("Erst Ladung wählen!", "Choose cargo first!"));
					return new MachineAssignState(m, null);
				case 2: return new MachineRotateState(m);
				case 3: return new MachineHeightState(m);
				case 4: return new MachineRemoveState(new MachineRef { own = m }, 1);
				default:
					LayoutStore.SaveQuiet();
					return new MachineIdleState(m.HasTrack ? Tr.T("Gespeichert", "Saved") : Tr.T("Gespeichert\n(noch ohne Gleis)", "Saved\n(no track yet)"));
			}
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not MachineEditState and not MachineCargoState and not MachineRotateState and not MachineHeightState and not MachineRemoveState) PlacerSession.ShowHighlight(null);
		}
	}

	/// <summary>Menü einer Lademaschine des Spiels: Ladung ändern oder entfernen.</summary>
	internal sealed class VanillaMachineEditState : AStateBehaviour
	{
		private static readonly string[] Options = { "LADUNG", "ENTFERNEN", "FERTIG" };
		private readonly MachineRef r;
		private readonly int sel;

		public VanillaMachineEditState(MachineRef r, int sel) : base(new CommsRadioState(
			titleText: Tr.T("Spiel-Lademaschine", "Game loading machine"),
			contentText: r.Summary + Tr.T("\nMausrad = Auswahl", "\nWheel = select"),
			actionText: Tr.Act(Options[sel]),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.r = r;
			this.sel = sel;
		}

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous) => PlacerSession.HighlightOutline(r.Outline);

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up) return new VanillaMachineEditState(r, (sel + Options.Length - 1) % Options.Length);
			if (action == InputAction.Down) return new VanillaMachineEditState(r, (sel + 1) % Options.Length);
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			switch (sel)
			{
				case 0: return new MachineCargoState(r, 1);
				case 1: return new MachineRemoveState(r, 1);
				default:
					LayoutStore.SaveQuiet();
					return new MachineIdleState(Tr.T("Gespeichert", "Saved"));
			}
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not VanillaMachineEditState and not MachineCargoState and not MachineRemoveState) PlacerSession.ShowHighlight(null);
		}
	}

	/// <summary>Ladungsarten durchblättern, Klick = hinzufügen/entfernen. Eintrag 0 = zurück.</summary>
	internal sealed class MachineCargoState : AStateBehaviour
	{
		private readonly MachineRef r;
		private readonly int index; // 0 = zurück, 1..n = CargoTypes[index-1]

		private static List<CargoType> AllCargo => MachineBuilder.CargoTypes;

		public MachineCargoState(MachineRef r, int index) : base(new CommsRadioState(
			titleText: Tr.T($"Ladung ({r.Cargo.Count} gewählt)", $"Cargo ({r.Cargo.Count} selected)"),
			contentText: Describe(r, index),
			actionText: Tr.Act(index == 0 ? "ZURÜCK" : Has(r, index) ? "ENTFERNEN" : "HINZUFÜGEN"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.r = r;
			this.index = index;
		}

		private static bool Has(MachineRef r, int index) => index > 0 && r.Cargo.Contains(AllCargo[index - 1].ToString());

		private static string Describe(MachineRef r, int index)
		{
			if (index == 0) return Tr.T("« zurück zum Menü\n", "« back to the menu\n") + string.Join(", ", r.Cargo.Take(4)) + (r.Cargo.Count > 4 ? " …" : "");
			var c = AllCargo[index - 1];
			return Tr.T($"{(Has(r, index) ? "[X] " : "[ ] ")}{MachineBuilder.DisplayName(c)}\n({index}/{AllCargo.Count})  Shift = 10er-Schritte", $"{(Has(r, index) ? "[X] " : "[ ] ")}{MachineBuilder.DisplayName(c)}\n({index}/{AllCargo.Count})  Shift = steps of 10");
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			int n = AllCargo.Count + 1;
			if (action == InputAction.Up || action == InputAction.Down)
			{
				int step = PlacerSession.NextStep() >= Main.Settings.stepShift - 0.0001f ? 10 : 1;
				int dir = action == InputAction.Down ? 1 : -1;
				return new MachineCargoState(r, ((index + dir * step) % n + n) % n);
			}

			if (index == 0)
				return r.own != null ? new MachineEditState(r.own, 1) : new VanillaMachineEditState(r, 2);

			var cargo = r.Cargo;
			string name = AllCargo[index - 1].ToString();
			if (cargo.Contains(name)) cargo.Remove(name); else cargo.Add(name);
			r.ApplyCargo();
			LayoutStore.SaveQuiet();
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new MachineCargoState(r, index);
		}
	}

	/// <summary>Entfernen mit Rückfrage (eigene Maschine: aus der JSON; Spielmaschine: ausblenden und abmelden).</summary>
	internal sealed class MachineRemoveState : AStateBehaviour
	{
		private static readonly string[] Options = { "ENTFERNEN", "ABBRECHEN" };
		private readonly MachineRef r;
		private readonly int sel;

		public MachineRemoveState(MachineRef r, int sel) : base(new CommsRadioState(
			titleText: Tr.T("Wirklich entfernen?", "Really remove?"),
			contentText: (r.own != null ? Tr.T("Eigene Lademaschine", "Own loading machine") : Tr.T("Spiel-Lademaschine\nOffene Lade-Jobs dort\nsind dann nicht machbar", "Game loading machine\nOpen loading jobs there\ncan no longer be done")) + Tr.T("\nMausrad = Auswahl", "\nWheel = select"),
			actionText: Tr.Act(Options[sel]),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.r = r;
			this.sel = sel;
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up || action == InputAction.Down) return new MachineRemoveState(r, 1 - sel);
			if (sel == 1)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Cancel);
				return r.own != null ? new MachineEditState(r.own, 4) : new VanillaMachineEditState(r, 1);
			}
			if (r.own != null)
			{
				MachineBuilder.Remove(r.own.id);
				LayoutStore.Remove(r.own.id);
			}
			else if (r.vanilla != null)
			{
				MachineBuilder.EditOf(r.vanilla, true)!.removed = true;
				MachineBuilder.HideVanilla(r.vanilla);
				LayoutStore.SaveQuiet();
			}
			PlacerSession.ShowHighlight(null);
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new MachineIdleState(Tr.T("Lademaschine entfernt", "Loading machine removed"));
		}
	}

	/// <summary>Laser auf Gleis (eigenes oder Spielgleis), Klick = zuweisen. Ins Leere = zurück.</summary>
	internal sealed class MachineAssignState : AStateBehaviour
	{
		private readonly PlacedMachine m;
		private readonly TrackRef? target;

		public MachineAssignState(PlacedMachine m, TrackRef? target) : base(new CommsRadioState(
			titleText: Tr.T("Gleis zuweisen", "Assign track"),
			contentText: target == null
				? Tr.T("Laser auf Gleis\nKlick ins Leere = zurück", "Aim at a track\nClick on nothing = back")
				: target.Label + (target.CurrentId == "" ? Tr.T(" → bekommt L-ID", " → gets an L ID") : "") + Tr.T("\nKlick = zuweisen", "\nClick = assign"),
			actionText: Tr.Act(target == null ? "ZIELEN" : "ZUWEISEN"),
			buttonBehaviour: ButtonBehaviourType.Ignore))
		{
			this.m = m;
			this.target = target;
		}

		public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
		{
			PlacerSession.UpdateHit(utility.SignalOrigin, allowSnap: false);
			var t = PlacerSession.Hit.HasValue ? PlacerSession.FindTrackRef(PlacerSession.Hit.Value) : null;
			if ((t == null && target == null) || (t != null && t.Same(target))) return this;
			PlacerSession.HighlightTrackRef(t);
			return new MachineAssignState(m, t);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return new MachineAssignState(m, target);
			if (target == null)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Cancel);
				return new MachineEditState(m, 1);
			}
			if (!MachineBuilder.AssignTrack(m, target, out string error))
			{
				utility.PlaySound(VanillaSoundCommsRadio.Warning);
				return new MachineEditState(m, 1, Tr.T("Fehler: ", "Error: ") + ConfirmState.Trim(error));
			}
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new MachineEditState(m, 5, Tr.T("Gleis ", "Track ") + target.CurrentId + Tr.T(" zugewiesen", " assigned"));
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not MachineAssignState) { PlacerSession.HideCursor(); PlacerSession.ShowHighlight(null); }
		}
	}

	internal sealed class MachineRotateState : AStateBehaviour
	{
		private readonly PlacedMachine m;

		public MachineRotateState(PlacedMachine m) : base(new CommsRadioState(
			titleText: Tr.T("Lademaschine drehen", "Rotate loading machine"),
			contentText: Tr.T($"Richtung {Fmt.M(m.yaw, 0)}°\nMausrad = drehen\nKlick = fertig", $"Heading {Fmt.M(m.yaw, 0)}°\nWheel = rotate\nClick = done"),
			actionText: Tr.Act("FERTIG"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.m = m;
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up || action == InputAction.Down)
			{
				float step = PlacerSession.NextAngleStep() * (action == InputAction.Up ? 1f : -1f);
				m.yaw = (float)System.Math.Round(Mathf.Repeat(m.yaw + step, 360f), 3);
				MachineBuilder.SetPose(m);
				PlacerSession.HighlightOutline(MachineBuilder.OutlineAt(m.Position, m.yaw));
				return new MachineRotateState(m);
			}
			LayoutStore.SaveQuiet();
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new MachineEditState(m, 5);
		}
	}

	/// <summary>Höhe der Maschine per Mausrad (normal/Shift/Strg = 0,01/0,10/1,00 m).</summary>
	internal sealed class MachineHeightState : AStateBehaviour
	{
		private readonly PlacedMachine m;

		public MachineHeightState(PlacedMachine m) : base(new CommsRadioState(
			titleText: Tr.T("Lademaschine Höhe", "Loading machine height"),
			contentText: Tr.T($"Höhe {Fmt.M(m.position[1], 2)} m\nMausrad = hoch/runter\nShift/Strg = größere Schritte\nKlick = fertig", $"Height {Fmt.M(m.position[1], 2)} m\nWheel = up/down\nShift/Ctrl = bigger steps\nClick = done"),
			actionText: Tr.Act("FERTIG"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.m = m;
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up || action == InputAction.Down)
			{
				// Richtung wie beim Gleisbau (Höhe): Mausrad nach oben = höher
				float step = PlacerSession.NextStep() * (action == InputAction.Up ? -1f : 1f);
				m.position[1] = (float)System.Math.Round(m.position[1] + step, 3);
				MachineBuilder.SetPose(m);
				PlacerSession.HighlightOutline(MachineBuilder.OutlineAt(m.Position, m.yaw));
				return new MachineHeightState(m);
			}
			LayoutStore.SaveQuiet();
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new MachineEditState(m, 5);
		}
	}
}
