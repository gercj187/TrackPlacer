using System.Collections.Generic;
using CommsRadioAPI;
using DV;
using TrackPlacer.Data;
using TrackPlacer.World;
using UnityEngine;
using InputAction = CommsRadioAPI.InputAction;

namespace TrackPlacer.Radio
{
	/// <summary>Gemeinsamer Laser-Treffer mit Transform (für Objekt-Auswahl).</summary>
	internal static class RadioRay
	{
		public static Transform? HitTransform(Transform origin, out float dist)
		{
			dist = Main.Settings.maxDistance;
			var ray = new Ray(origin.position, origin.forward);
			var hits = Physics.RaycastAll(ray, dist, ~0, QueryTriggerInteraction.Ignore);
			System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
			foreach (var h in hits)
			{
				if (h.transform.IsChildOf(origin.root)) continue;
				if (h.collider.GetComponentInParent<TrainCar>() != null) continue;
				dist = h.distance;
				return h.transform;
			}
			return null;
		}
	}

	// ======================================================================
	// Modus "Gleisschild": Boden neben dem Gleis anzielen → Text aus der Gleis-ID → drehen → setzen
	// ======================================================================

	internal sealed class SignIdleState : AStateBehaviour
	{
		public SignIdleState(string? message = null) : base(new CommsRadioState(
			titleText: Tr.T("Gleisschild", "Track sign"),
			contentText: message ?? Tr.T("Schild neben ein Gleis\nmit Bahnhofs-ID setzen\nKlick = Start · Rad = Menü", "Place a sign next to a track\nwith its station ID\nClick = start · Wheel = menu"),
			actionText: Tr.Act("START"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{ }

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous) { SignBuilder.HidePreview(); PlacerSession.HideCursor(); }

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return PlacerMenu.Return(); // Mausrad = zurück ins Menü
			utility.PlaySound(VanillaSoundCommsRadio.ModeEnter);
			return new SignPickState(null, "", "");
		}
	}

	internal sealed class SignPickState : AStateBehaviour
	{
		private readonly TrackRef? track;
		private readonly string sub, trk;
		private readonly PlacedSign? existing;

		public SignPickState(TrackRef? track, string sub, string trk, PlacedSign? existing = null) : base(new CommsRadioState(
			titleText: Tr.T("Gleisschild", "Track sign"),
			contentText: existing != null ? Tr.T($"Schild {existing.sub} {existing.track}\nKlick = bearbeiten", $"Sign {existing.sub} {existing.track}\nClick = edit")
				: track == null ? Tr.T("Laser auf Boden neben\neinem Gleis (≤ 6 m)\noder auf ein Schild", "Aim at ground next to\na track (≤ 6 m)\nor at a sign")
				: trk == "" ? Tr.T("Gleis ohne Bahnhofs-ID\nerst „Gleis-ID“ vergeben", "Track has no station ID\nassign a 'Track ID' first")
				: Tr.T($"Schild {sub} {trk}\nKlick = drehen/setzen", $"Sign {sub} {trk}\nClick = rotate/place"),
			actionText: Tr.Act(existing != null ? "BEARBEITEN" : track != null && trk != "" ? "WEITER" : "ZIELEN"),
			buttonBehaviour: ButtonBehaviourType.Ignore))
		{
			this.track = track; this.sub = sub; this.trk = trk; this.existing = existing;
		}

		public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
		{
			// Vorhandenes Schild angezielt → bearbeiten
			var ex = SignBuilder.SignAt(RadioRay.HitTransform(utility.SignalOrigin, out _));
			if (ex != null)
			{
				SignBuilder.HidePreview();
				PlacerSession.HideCursor();
				if (ex == existing) return this;
				if (SignBuilder.Built.TryGetValue(ex.id, out var sgo) && sgo != null) PlacerSession.HighlightOutline(ObjectDisabler.WireBox(sgo));
				return new SignPickState(null, "", "", ex);
			}

			PlacerSession.UpdateHit(utility.SignalOrigin, allowSnap: false);
			PlacerSession.HideCursor();
			TrackRef? r = null;
			if (PlacerSession.GroundHit.HasValue)
			{
				var g = PlacerSession.GroundHit.Value;
				var own = PlacerSession.FindOwnTrack(g, 6f);
				if (own != null) r = new TrackRef { own = own };
				else { var v = VanillaRemover.FindNear(g, 6f); if (v != null) r = new TrackRef { vanilla = v }; }
				SignBuilder.ShowPreview(g, Mathf.Repeat(utility.SignalOrigin.eulerAngles.y + 180f, 360f));
			}
			else SignBuilder.HidePreview();
			var (s, t) = r != null ? SignBuilder.TextFor(r) : ("", "");
			if (existing == null && ((r == null && track == null) || (r != null && r.Same(track))) && t == trk) return this;
			if (r != null) PlacerSession.HighlightTrackRef(r); else PlacerSession.ShowHighlight(null);
			return new SignPickState(r, s, t);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return this;
			if (existing != null)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Confirm);
				return new SignEditState(existing, 0);
			}
			if (!PlacerSession.GroundHit.HasValue) { utility.PlaySound(VanillaSoundCommsRadio.Cancel); return new SignIdleState(); }
			if (track == null || trk == "") { utility.PlaySound(VanillaSoundCommsRadio.Warning); return this; }
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new SignRotateState(PlacerSession.GroundHit.Value, Mathf.Repeat(utility.SignalOrigin.eulerAngles.y + 180f, 360f), track.Key, sub, trk);
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not SignPickState) PlacerSession.ShowHighlight(null);
			if (next is not SignPickState and not SignRotateState) SignBuilder.HidePreview();
		}
	}

	/// <summary>Gesetztes Schild bearbeiten: drehen, Höhe, Größe, entfernen.</summary>
	internal sealed class SignEditState : AStateBehaviour
	{
		public const int Done = 6; // Index von FERTIG
		private static readonly string[] Options = { "DREHEN", "HÖHE", "GRÖSSE", "MAST", "VERSCHIEBEN", "ENTFERNEN", "FERTIG" };
		private static string Opt(PlacedSign sg, int sel) => sel == 3 ? (sg.hidePole ? "MAST EINBLENDEN" : "MAST AUSBLENDEN") : Options[sel];
		private readonly PlacedSign sg;
		private readonly int sel;

		public SignEditState(PlacedSign sg, int sel) : base(new CommsRadioState(
			titleText: Tr.T($"Schild {sg.sub} {sg.track}", $"Sign {sg.sub} {sg.track}"),
			contentText: Tr.T($"Richtung {Fmt.M(sg.yaw, 0)}° · Höhe {Fmt.M(sg.height, 2)} m\nGröße {Fmt.M((sg.scale <= 0f ? 1f : sg.scale) * 100f, 0)} % · Mast {(sg.hidePole ? "aus" : "an")}\nMausrad = Auswahl", $"Heading {Fmt.M(sg.yaw, 0)}° · Height {Fmt.M(sg.height, 2)} m\nSize {Fmt.M((sg.scale <= 0f ? 1f : sg.scale) * 100f, 0)} % · Pole {(sg.hidePole ? "off" : "on")}\nWheel = select"),
			actionText: Tr.Act(Opt(sg, sel)),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.sg = sg; this.sel = sel;
		}

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous)
		{
			if (SignBuilder.Built.TryGetValue(sg.id, out var go) && go != null) PlacerSession.HighlightOutline(ObjectDisabler.WireBox(go));
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up) return new SignEditState(sg, (sel + Options.Length - 1) % Options.Length);
			if (action == InputAction.Down) return new SignEditState(sg, (sel + 1) % Options.Length);
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			switch (sel)
			{
				case 0: case 1: case 2: return new SignAdjustState(sg, sel);
				case 3:
					sg.hidePole = !sg.hidePole;
					SignBuilder.SetPose(sg);
					LayoutStore.SaveQuiet();
					return new SignEditState(sg, 3);
				case 4: return new SignMoveState(sg);
				case 5:
					SignBuilder.Remove(sg.id);
					LayoutStore.Remove(sg.id);
					PlacerSession.ShowHighlight(null);
					return new SignIdleState(Tr.T("Schild entfernt", "Sign removed"));
				default:
					LayoutStore.SaveQuiet();
					PlacerSession.ShowHighlight(null);
					return new SignIdleState(Tr.T("Gespeichert", "Saved"));
			}
		}
	}

	/// <summary>Schild drehen (°), Höhe (m) oder Größe (%) per Mausrad (Shift/Strg = grob).</summary>
	internal sealed class SignAdjustState : AStateBehaviour
	{
		private readonly PlacedSign sg;
		private readonly int mode; // 0 drehen, 1 Höhe, 2 Größe

		private static string Value(PlacedSign sg, int mode) => mode switch
		{
			0 => Tr.T($"Richtung {Fmt.M(sg.yaw, 0)}°", $"Heading {Fmt.M(sg.yaw, 0)}°"),
			1 => Tr.T($"Höhe {(sg.height >= 0f ? "+" : "")}{Fmt.M(sg.height, 2)} m", $"Height {(sg.height >= 0f ? "+" : "")}{Fmt.M(sg.height, 2)} m"),
			_ => Tr.T($"Größe {Fmt.M((sg.scale <= 0f ? 1f : sg.scale) * 100f, 0)} %", $"Size {Fmt.M((sg.scale <= 0f ? 1f : sg.scale) * 100f, 0)} %"),
		};

		public SignAdjustState(PlacedSign sg, int mode) : base(new CommsRadioState(
			titleText: mode == 0 ? Tr.T("Schild drehen", "Rotate sign") : mode == 1 ? Tr.T("Schild Höhe", "Sign height") : Tr.T("Schild Größe", "Sign size"),
			contentText: Value(sg, mode) + Tr.T("\nMausrad (Shift/Strg = grob)\nKlick = fertig", "\nWheel (Shift/Ctrl = coarse)\nClick = done"),
			actionText: Tr.Act("FERTIG"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.sg = sg; this.mode = mode;
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up || action == InputAction.Down)
			{
				// Mausrad nach oben = mehr (InputAction.Up kommt beim Runterscrollen, wie beim Gleisbau umgedreht)
				float dir = action == InputAction.Up ? -1f : 1f;
				switch (mode)
				{
					case 0: sg.yaw = (float)System.Math.Round(Mathf.Repeat(sg.yaw - dir * PlacerSession.NextAngleStep(), 360f), 2); break;
					case 1: sg.height = (float)System.Math.Round(Mathf.Clamp(sg.height + dir * PlacerSession.NextStep(), -2f, 10f), 3); break;
					default:
						float sc = sg.scale <= 0f ? 1f : sg.scale;
						sg.scale = (float)System.Math.Round(Mathf.Clamp(sc + dir * PlacerSession.NextStep(), 0.2f, 1f), 3); // max. Originalgröße
						break;
				}
				SignBuilder.SetPose(sg);
				if (SignBuilder.Built.TryGetValue(sg.id, out var go) && go != null) PlacerSession.HighlightOutline(ObjectDisabler.WireBox(go));
				return new SignAdjustState(sg, mode);
			}
			LayoutStore.SaveQuiet();
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new SignEditState(sg, SignEditState.Done);
		}
	}

	/// <summary>Gesetztes Schild verschieben: folgt dem Laser auf dem Boden, Mausrad dreht, Klick = absetzen.</summary>
	internal sealed class SignMoveState : AStateBehaviour
	{
		private readonly PlacedSign sg;

		public SignMoveState(PlacedSign sg) : base(new CommsRadioState(
			titleText: Tr.T($"Schild {sg.sub} {sg.track} verschieben", $"Move sign {sg.sub} {sg.track}"),
			contentText: Tr.T($"Laser auf neuen Standort\nMausrad = drehen ({Fmt.M(sg.yaw, 0)}°)\nKlick = absetzen", $"Aim at the new spot\nWheel = rotate ({Fmt.M(sg.yaw, 0)}°)\nClick = drop"),
			actionText: Tr.Act("ABSETZEN"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.sg = sg;
		}

		// Während des Verschiebens trifft der Laser sonst das Schild selbst → Collider kurz aus
		private void SetColliders(bool on)
		{
			if (SignBuilder.Built.TryGetValue(sg.id, out var go) && go != null)
				foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = on;
		}

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous) => SetColliders(false);

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not SignMoveState) SetColliders(true);
		}

		public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
		{
			PlacerSession.UpdateHit(utility.SignalOrigin, allowSnap: false);
			PlacerSession.HideCursor();
			if (PlacerSession.GroundHit.HasValue)
			{
				Vector3 g = PlacerSession.GroundHit.Value;
				if ((g - sg.Position).sqrMagnitude > 1e-6f)
				{
					sg.position = PlacedTrack.FromVec(g);
					SignBuilder.SetPose(sg);
					SetColliders(false); // der Mast wird dabei neu angelegt (mit Collider)
				}
			}
			return this;
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up || action == InputAction.Down)
			{
				float step = PlacerSession.NextAngleStep() * (action == InputAction.Up ? 1f : -1f);
				sg.yaw = (float)System.Math.Round(Mathf.Repeat(sg.yaw + step, 360f), 2);
				SignBuilder.SetPose(sg);
				SetColliders(false);
				return new SignMoveState(sg);
			}
			LayoutStore.SaveQuiet();
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new SignEditState(sg, SignEditState.Done);
		}
	}

	internal sealed class SignRotateState : AStateBehaviour
	{
		private readonly Vector3 pos; private readonly float yaw; private readonly string key, sub, trk;

		public SignRotateState(Vector3 pos, float yaw, string key, string sub, string trk) : base(new CommsRadioState(
			titleText: Tr.T($"Schild {sub} {trk}", $"Sign {sub} {trk}"),
			contentText: Tr.T($"Richtung {Fmt.M(yaw, 0)}°\nMausrad = drehen\nKlick = setzen", $"Heading {Fmt.M(yaw, 0)}°\nWheel = rotate\nClick = place"),
			actionText: Tr.Act("SETZEN"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.pos = pos; this.yaw = yaw; this.key = key; this.sub = sub; this.trk = trk;
		}

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous) => SignBuilder.ShowPreview(pos, yaw);

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up || action == InputAction.Down)
			{
				float step = PlacerSession.NextAngleStep() * (action == InputAction.Up ? 1f : -1f);
				return new SignRotateState(pos, Mathf.Repeat(yaw + step, 360f), key, sub, trk);
			}
			var placed = new PlacedSign { position = PlacedTrack.FromVec(pos), yaw = (float)System.Math.Round(yaw, 2), trackKey = key, sub = sub, track = trk };
			LayoutStore.Layout.signs.Add(placed);
			if (!SignBuilder.Build(placed, out string error))
			{
				LayoutStore.Layout.signs.Remove(placed);
				utility.PlaySound(VanillaSoundCommsRadio.Warning);
				return new SignIdleState(Tr.T("Fehlgeschlagen:\n", "Failed:\n") + ConfirmState.Trim(error));
			}
			LayoutStore.SaveQuiet();
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new SignIdleState(Tr.T($"Schild {sub} {trk} gesetzt\nKlick = nächstes", $"Sign {sub} {trk} placed\nClick = next"));
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not SignRotateState) SignBuilder.HidePreview();
		}
	}

	// ======================================================================
	// Modus "Objekt klonen": Objekt anzielen (Mausrad = Ebene) → Klick → Kopie folgt dem Laser, Mausrad dreht →
	// Klick = setzen → Menü (Drehen, Höhe, Verschieben, Entfernen). Eigenen Klon anzielen = Menü.
	// ======================================================================

	internal sealed class CloneIdleState : AStateBehaviour
	{
		public CloneIdleState(string? message = null) : base(new CommsRadioState(
			titleText: Tr.T("Objekt klonen", "Clone object"),
			contentText: message ?? Tr.T("Objekt anzielen und kopieren\nKlick = Start · Rad = Menü", "Aim at an object and copy it\nClick = start · Wheel = menu"),
			actionText: Tr.Act("START"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{ }

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous)
		{
			PlacerSession.ShowHighlight(null);
			PlacerSession.HideCursor();
			CloneBuilder.HidePreview();
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return PlacerMenu.Return(); // Mausrad = zurück ins Menü
			utility.PlaySound(VanillaSoundCommsRadio.ModeEnter);
			return new ClonePickState(null, null, 0, null);
		}
	}

	internal sealed class ClonePickState : AStateBehaviour
	{
		private readonly Transform? hit;
		private readonly List<GameObject>? chain;
		private readonly int level;
		private readonly PlacedClone? own;

		public ClonePickState(Transform? hit, List<GameObject>? chain, int level, PlacedClone? own) : base(new CommsRadioState(
			titleText: Tr.T("Objekt klonen", "Clone object"),
			contentText: own != null ? Tr.T($"Eigener Klon: {own.srcName}\nKlick = bearbeiten", $"Own clone: {own.srcName}\nClick = edit")
				: chain == null || chain.Count == 0 ? Tr.T("Laser auf ein Objekt\nKlick ins Leere = Abbruch", "Aim at an object\nClick on nothing = cancel")
				: Tr.T($"{ObjectDisabler.Label(chain[Mathf.Clamp(level, 0, chain.Count - 1)])}\nEbene {level + 1}/{chain.Count} (Mausrad)\nKlick = kopieren", $"{ObjectDisabler.Label(chain[Mathf.Clamp(level, 0, chain.Count - 1)])}\nLevel {level + 1}/{chain.Count} (wheel)\nClick = copy"),
			actionText: Tr.Act(own != null ? "BEARBEITEN" : chain != null && chain.Count > 0 ? "KOPIEREN" : "ZIELEN"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.hit = hit; this.chain = chain; this.level = level; this.own = own;
		}

		public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
		{
			var h = RadioRay.HitTransform(utility.SignalOrigin, out _);
			if (h == hit) return this;
			if (h == null) { PlacerSession.ShowHighlight(null); return new ClonePickState(null, null, 0, null); }
			var c = CloneBuilder.CloneOf(h);
			if (c != null)
			{
				if (CloneBuilder.Built.TryGetValue(c.id, out var go) && go != null) PlacerSession.HighlightOutline(ObjectDisabler.WireBox(go));
				return new ClonePickState(h, null, 0, c);
			}
			var newChain = ObjectDisabler.CandidateChain(h, out int def, skipColliderOnly: true); // Klonen: ohne Größenlimit, keine reinen Collider
			if (newChain.Count > 0) PlacerSession.HighlightOutline(ObjectDisabler.WireBox(newChain[def]));
			else PlacerSession.ShowHighlight(null);
			return new ClonePickState(h, newChain, def, null);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up || action == InputAction.Down)
			{
				if (chain == null || chain.Count == 0) return this;
				int l = Mathf.Clamp(level + (action == InputAction.Up ? 1 : -1), 0, chain.Count - 1);
				PlacerSession.HighlightOutline(ObjectDisabler.WireBox(chain[l]));
				return new ClonePickState(hit, chain, l, null);
			}
			if (own != null) { utility.PlaySound(VanillaSoundCommsRadio.Confirm); return new CloneEditState(own, 0); }
			if (chain == null || chain.Count == 0) { utility.PlaySound(VanillaSoundCommsRadio.Cancel); return new CloneIdleState(); }
			// Sofortige 1:1-Kopie am Quell-Pivot. Dadurch stimmen Position und Ausrichtung exakt und
			// das Bearbeitungsmenü ist ohne einen zweiten Platzierungsschritt direkt offen.
			var source = chain[Mathf.Clamp(level, 0, chain.Count - 1)];
			var c = new PlacedClone
			{
				srcParentPath = CloneHelper.PathOf(source.transform.parent),
				srcName = source.name,
				srcPosition = PlacedTrack.FromVec(source.transform.position - GameAccess.OriginShift),
				position = PlacedTrack.FromVec(source.transform.position - GameAccess.OriginShift),
				yaw = 0f,
			};
			LayoutStore.Layout.clones.Add(c);
			if (!CloneBuilder.Build(c, out string error))
			{
				LayoutStore.Layout.clones.Remove(c);
				utility.PlaySound(VanillaSoundCommsRadio.Warning);
				return new CloneIdleState(Tr.T("Fehlgeschlagen:\n", "Failed:\n") + ConfirmState.Trim(error));
			}
			LayoutStore.SaveQuiet();
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new CloneEditState(c, 0);
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not ClonePickState) PlacerSession.ShowHighlight(null);
		}
	}

	/// <summary>Kopie folgt dem Laser (Unterkante auf dem Boden), Mausrad dreht. existing = Verschieben eines Klons.</summary>
	internal sealed class ClonePlaceState : AStateBehaviour
	{
		private readonly GameObject source;
		private readonly float yaw;
		private readonly PlacedClone? existing;
		private readonly float lift;

		public ClonePlaceState(GameObject source, float yaw, PlacedClone? existing, float lift = float.NaN) : base(new CommsRadioState(
			titleText: existing != null ? Tr.T("Klon verschieben", "Move clone") : Tr.T("Klon setzen", "Place clone"),
			contentText: Tr.T($"Laser auf Zielort\nMausrad = drehen ({Fmt.M(yaw, 0)}°)\nKlick = setzen", $"Aim at the target spot\nWheel = rotate ({Fmt.M(yaw, 0)}°)\nClick = place"),
			actionText: Tr.Act("SETZEN"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.source = source; this.yaw = yaw; this.existing = existing;
			this.lift = float.IsNaN(lift) ? CloneBuilder.PivotAboveBottom(source) : lift;
		}

		private Vector3? Pivot => PlacerSession.GroundHit.HasValue ? PlacerSession.GroundHit.Value + Vector3.up * lift : (Vector3?)null;

		public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
		{
			PlacerSession.UpdateHit(utility.SignalOrigin, allowSnap: false);
			PlacerSession.HideCursor();
			var p = Pivot;
			if (p.HasValue && source != null) CloneBuilder.ShowPreview(source, p.Value, yaw);
			else CloneBuilder.HidePreview();
			return this;
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up || action == InputAction.Down)
			{
				float step = PlacerSession.NextAngleStep() * (action == InputAction.Up ? 1f : -1f);
				return new ClonePlaceState(source, Mathf.Repeat(yaw + step, 360f), existing, lift);
			}
			var pivot = Pivot;
			if (!pivot.HasValue || source == null) { utility.PlaySound(VanillaSoundCommsRadio.Cancel); return new CloneIdleState(); }

			if (existing != null)
			{
				existing.position = PlacedTrack.FromVec(pivot.Value);
				existing.yaw = (float)System.Math.Round(yaw, 2);
				CloneBuilder.SetPose(existing);
				LayoutStore.SaveQuiet();
				utility.PlaySound(VanillaSoundCommsRadio.Confirm);
				return new CloneEditState(existing, 0);
			}

			var c = new PlacedClone
			{
				srcParentPath = CloneHelper.PathOf(source.transform.parent),
				srcName = source.name,
				srcPosition = PlacedTrack.FromVec(source.transform.position - GameAccess.OriginShift),
				position = PlacedTrack.FromVec(pivot.Value),
				yaw = (float)System.Math.Round(yaw, 2),
			};
			LayoutStore.Layout.clones.Add(c);
			if (!CloneBuilder.Build(c, out string error))
			{
				LayoutStore.Layout.clones.Remove(c);
				utility.PlaySound(VanillaSoundCommsRadio.Warning);
				return new CloneIdleState(Tr.T("Fehlgeschlagen:\n", "Failed:\n") + ConfirmState.Trim(error));
			}
			LayoutStore.SaveQuiet();
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new CloneEditState(c, CloneEditState.Done);
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not ClonePlaceState) CloneBuilder.HidePreview();
		}
	}

	internal sealed class CloneEditState : AStateBehaviour
	{
		public const int Done = 5; // Index von FERTIG
		private static readonly string[] Options = { "DREHEN", "HÖHE", "VERSCHIEBEN", "COLLIDER", "ENTFERNEN", "FERTIG" };
		private static string Opt(PlacedClone c, int sel) => sel == 4 && c.replacesSource ? "ZURÜCKSETZEN" : Options[sel];
		private readonly PlacedClone c;
		private readonly int sel;

		public CloneEditState(PlacedClone c, int sel) : base(new CommsRadioState(
			titleText: Tr.T("Klon: ", "Clone: ") + (c.srcName.Length > 16 ? c.srcName.Substring(0, 16) : c.srcName),
			contentText: Tr.T($"Richtung {Fmt.M(c.yaw, 0)}° · Höhe {Fmt.M(c.position[1], 2)} m\nMausrad = Auswahl", $"Heading {Fmt.M(c.yaw, 0)}° · Height {Fmt.M(c.position[1], 2)} m\nWheel = select"),
			actionText: Tr.Act(Opt(c, sel)),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.c = c; this.sel = sel;
		}

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous)
		{
			if (CloneBuilder.Built.TryGetValue(c.id, out var go) && go != null) PlacerSession.HighlightOutline(ObjectDisabler.WireBox(go));
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up) return new CloneEditState(c, (sel + Options.Length - 1) % Options.Length);
			if (action == InputAction.Down) return new CloneEditState(c, (sel + 1) % Options.Length);
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			switch (sel)
			{
				case 0: return new CloneAdjustState(c, false);
				case 1: return new CloneAdjustState(c, true);
				case 2:
					var src = CloneBuilder.Source(c);
					if (src == null) return new CloneIdleState(Tr.T("Quelle nicht geladen", "Source not loaded"));
					// Pivot-Abstand zum Boden beibehalten: aktuelle Höhe über der Unterkante der Quelle
					return new ClonePlaceState(src, c.yaw, c);
				case 3: return new CloneColliderMenuState(c, 0);
				case 4:
					bool restored = CloneBuilder.Delete(c);
					PlacerSession.ShowHighlight(null);
					return new CloneIdleState(restored ? Tr.T("Original wiederhergestellt", "Original restored") : Tr.T("Klon entfernt", "Clone removed"));
				default:
					LayoutStore.SaveQuiet();
					PlacerSession.ShowHighlight(null);
					return new CloneIdleState(Tr.T("Gespeichert", "Saved"));
			}
		}
	}

	/// <summary>Collider eines Klons: einzeln ausschalten, alle wieder an, mitkopierte Nachbar-Collider an/aus.</summary>
	internal sealed class CloneColliderMenuState : AStateBehaviour
	{
		private static readonly string[] Options = { "AUSWÄHLEN", "ALLE WIEDER AN", "NACHBARN", "ZURÜCK" };
		private readonly PlacedClone c;
		private readonly int sel;

		private static string Opt(PlacedClone c, int sel) => sel == 2 ? (c.noCompanions ? "NACHBAR-COLLIDER AN" : "NACHBAR-COLLIDER AUS") : Options[sel];

		public CloneColliderMenuState(PlacedClone c, int sel, string? message = null) : base(new CommsRadioState(
			titleText: Tr.T("Klon-Collider", "Clone colliders"),
			contentText: (message != null ? message + "\n" : "") + Tr.T($"{c.disabledColliders.Count} ausgeschaltet · Nachbar-Collider {(c.noCompanions ? "nicht kopiert" : "kopiert")}\nMausrad = Auswahl", $"{c.disabledColliders.Count} switched off · neighbour colliders {(c.noCompanions ? "not copied" : "copied")}\nWheel = select"),
			actionText: Tr.Act(Opt(c, sel)),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.c = c; this.sel = sel;
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up) return new CloneColliderMenuState(c, (sel + Options.Length - 1) % Options.Length);
			if (action == InputAction.Down) return new CloneColliderMenuState(c, (sel + 1) % Options.Length);
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			switch (sel)
			{
				case 0: return new CloneColliderPickState(c, null);
				case 1:
					int n = CloneBuilder.EnableAllColliders(c);
					return new CloneColliderMenuState(c, sel, Tr.T($"{n} Collider wieder an", $"{n} collider(s) back on"));
				case 2:
					c.noCompanions = !c.noCompanions;
					bool ok = CloneBuilder.Rebuild(c, out string err);
					LayoutStore.SaveQuiet();
					return new CloneColliderMenuState(c, sel, ok ? Tr.T("Klon neu gebaut", "Clone rebuilt") : Tr.T("Fehler: ", "Error: ") + ConfirmState.Trim(err));
				default:
					return new CloneEditState(c, 3);
			}
		}
	}

	/// <summary>Collider im Klon anzielen (auch unsichtbare) → Klick = ausschalten. Mausrad = zurück.</summary>
	internal sealed class CloneColliderPickState : AStateBehaviour
	{
		private readonly PlacedClone c;
		private readonly Collider? target;

		public CloneColliderPickState(PlacedClone c, Collider? target) : base(new CommsRadioState(
			titleText: Tr.T("Collider ausschalten", "Switch off collider"),
			contentText: target == null ? Tr.T("Laser auf einen Collider\ndieses Klons (auch unsichtbar)\nMausrad = zurück", "Aim at a collider\nof this clone (also invisible)\nWheel = back")
				: Tr.T($"{(target.name.Length > 22 ? target.name.Substring(0, 22) : target.name)}\n{target.GetType().Name} · {Fmt.M(target.bounds.size.magnitude, 1)} m\nKlick = ausschalten", $"{(target.name.Length > 22 ? target.name.Substring(0, 22) : target.name)}\n{target.GetType().Name} · {Fmt.M(target.bounds.size.magnitude, 1)} m\nClick = switch off"),
			actionText: Tr.Act(target == null ? "ZIELEN" : "AUSSCHALTEN"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.c = c; this.target = target;
		}

		public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
		{
			Collider? hit = null;
			if (CloneBuilder.Built.TryGetValue(c.id, out var root) && root != null)
			{
				var origin = utility.SignalOrigin;
				var hits = Physics.RaycastAll(new Ray(origin.position, origin.forward), Main.Settings.maxDistance, ~0, QueryTriggerInteraction.Ignore);
				System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
				foreach (var h in hits)
				{
					if (h.transform.IsChildOf(origin.root)) continue;
					if (h.collider.transform.IsChildOf(root.transform)) hit = h.collider;
					break; // nur den vordersten Treffer – liegt etwas anderes davor, nichts auswählen
				}
			}
			if (hit == target) return this;
			if (hit != null) PlacerSession.HighlightOutline(ObjectDisabler.WireBox(new Bounds(hit.bounds.center - GameAccess.OriginShift, hit.bounds.size)));
			else PlacerSession.ShowHighlight(null);
			return new CloneColliderPickState(c, hit);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) { PlacerSession.ShowHighlight(null); return new CloneColliderMenuState(c, 0); }
			if (target == null) { utility.PlaySound(VanillaSoundCommsRadio.Cancel); return this; }
			CloneBuilder.DisableCollider(c, target);
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			PlacerSession.ShowHighlight(null);
			return new CloneColliderPickState(c, null);
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not CloneColliderPickState) PlacerSession.ShowHighlight(null);
		}
	}

	/// <summary>Drehen (Grad) oder Höhe (m) per Mausrad, normal/Shift/Strg = fein/grob/sehr grob.</summary>
	internal sealed class CloneAdjustState : AStateBehaviour
	{
		private readonly PlacedClone c;
		private readonly bool height;

		public CloneAdjustState(PlacedClone c, bool height) : base(new CommsRadioState(
			titleText: height ? Tr.T("Klon Höhe", "Clone height") : Tr.T("Klon drehen", "Rotate clone"),
			contentText: (height ? Tr.T($"Höhe {Fmt.M(c.position[1], 2)} m", $"Height {Fmt.M(c.position[1], 2)} m") : Tr.T($"Richtung {Fmt.M(c.yaw, 0)}°", $"Heading {Fmt.M(c.yaw, 0)}°")) + Tr.T("\nMausrad (Shift/Strg = grob)\nKlick = fertig", "\nWheel (Shift/Ctrl = coarse)\nClick = done"),
			actionText: Tr.Act("FERTIG"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.c = c; this.height = height;
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up || action == InputAction.Down)
			{
				float dir = action == InputAction.Up ? 1f : -1f;
				// Höhe: Mausrad nach oben = höher (InputAction.Up kommt beim Runterscrollen, wie beim Gleisbau umgedreht)
				if (height) c.position[1] = (float)System.Math.Round(c.position[1] - dir * PlacerSession.NextStep(), 3);
				else c.yaw = (float)System.Math.Round(Mathf.Repeat(c.yaw + dir * PlacerSession.NextAngleStep(), 360f), 2);
				CloneBuilder.SetPose(c);
				if (CloneBuilder.Built.TryGetValue(c.id, out var go) && go != null) PlacerSession.HighlightOutline(ObjectDisabler.WireBox(go));
				return new CloneAdjustState(c, height);
			}
			LayoutStore.SaveQuiet();
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new CloneEditState(c, CloneEditState.Done);
		}
	}

	// ======================================================================
	// Modus "Pitstop": Service-Modul anzielen (Diesel, Kohle, Reparatur …) → Klick = ab-/einschalten
	// ======================================================================

	internal sealed class PitstopIdleState : AStateBehaviour
	{
		public PitstopIdleState(string? message = null) : base(new CommsRadioState(
			titleText: Tr.T("Pitstop", "Pitstop"),
			contentText: message ?? Tr.T("Pitstop bearbeiten: Module,\ndrehen, Höhe, verschieben\nKlick = Start · Rad = Menü", "Edit pitstop: modules,\nrotate, height, move\nClick = start · Wheel = menu"),
			actionText: Tr.Act("START"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{ }

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous) { PlacerSession.ShowHighlight(null); PlacerSession.HideCursor(); }

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return PlacerMenu.Return(); // Mausrad = zurück ins Menü
			utility.PlaySound(VanillaSoundCommsRadio.ModeEnter);
			return new PitstopPickState(null, null, null);
		}
	}

	/// <summary>Pitstop oder eines seiner Service-Module anzielen → Klick = Bearbeiten-Menü.</summary>
	internal sealed class PitstopPickState : AStateBehaviour
	{
		private readonly LocoResourceModule? module;
		private readonly string? disabledId;
		private readonly GameObject? root;
		private float nextScan;

		private static string Text(LocoResourceModule? module, string? disabledId, GameObject? root)
		{
			if (module == null && root == null) return Tr.T("Laser auf einen Pitstop\noder ein Service-Modul\nKlick ins Leere = Abbruch", "Aim at a pitstop\nor a service module\nClick on nothing = cancel");
			string head = root != null ? PitstopEditor.LabelOf(root) + (CloneBuilder.CloneOf(root.transform) != null ? Tr.T(" (eigen)", " (own)") : "") : Tr.T("Pitstop", "Pitstop");
			string mod = module != null ? Tr.T($"\n{PitstopEditor.ResourceName(module)}: {(disabledId != null ? "AUS" : "an")}", $"\n{PitstopEditor.ResourceName(module)}: {(disabledId != null ? "OFF" : "on")}") : "";
			return head + mod + Tr.T("\nKlick = bearbeiten", "\nClick = edit");
		}

		public PitstopPickState(LocoResourceModule? module, string? disabledId, GameObject? root) : base(new CommsRadioState(
			titleText: Tr.T("Pitstop", "Pitstop"),
			contentText: Text(module, disabledId, root),
			actionText: Tr.Act(module == null && root == null ? "ZIELEN" : "BEARBEITEN"),
			buttonBehaviour: ButtonBehaviourType.Ignore))
		{
			this.module = module; this.disabledId = disabledId; this.root = root;
		}

		public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
		{
			if (Time.unscaledTime < nextScan) return this;
			nextScan = Time.unscaledTime + 0.25f; // Suche nicht jeden Frame
			var h = RadioRay.HitTransform(utility.SignalOrigin, out float dist);
			LocoResourceModule? m = null; string? id = null; GameObject? r = null;
			if (h != null)
			{
				Vector3 p = utility.SignalOrigin.position + utility.SignalOrigin.forward * dist - GameAccess.OriginShift;
				(m, id) = PitstopEditor.FindNear(p, h, 2.5f);
				r = PitstopEditor.RootOf(h) ?? (m != null ? PitstopEditor.RootOf(m.transform) : null);
			}
			if (m == module && id == disabledId && r == root) return this;
			if (m != null && m.gameObject.activeInHierarchy) PlacerSession.HighlightOutline(ObjectDisabler.WireBox(m.gameObject));
			else if (r != null) PlacerSession.HighlightOutline(ObjectDisabler.WireBox(r));
			else PlacerSession.ShowHighlight(null);
			return new PitstopPickState(m, id, r);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return this;
			if (module == null && root == null) { utility.PlaySound(VanillaSoundCommsRadio.Cancel); return new PitstopIdleState(); }
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new PitstopEditState(module, disabledId, root, 0);
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not PitstopPickState && next is not PitstopEditState) PlacerSession.ShowHighlight(null);
		}
	}

	/// <summary>Menü: Modul an/aus, Drehen, Höhe, Verschieben, Entfernen/Zurücksetzen, Fertig.</summary>
	internal sealed class PitstopEditState : AStateBehaviour
	{
		private readonly LocoResourceModule? module;
		private readonly string? disabledId;
		private readonly GameObject? root;
		private readonly int sel;
		private readonly List<string> options;

		private static List<string> OptionsFor(LocoResourceModule? module, string? disabledId, GameObject? root)
		{
			var o = new List<string>();
			if (module != null)
			{
				o.Add(disabledId != null ? "MODUL AN" : "MODUL AUS");
				// Modul-Pose nur an generierten/verschobenen Pitstops ändern; Vanilla-Pitstops bleiben unangetastet.
				if (root != null && CloneBuilder.CloneOf(root.transform) != null)
				{
					o.Add("MODUL DREHEN"); o.Add("MODUL HÖHE"); o.Add("MODUL VERSCHIEBEN");
				}
			}
			if (root != null)
			{
				o.Add("DREHEN"); o.Add("HÖHE"); o.Add("VERSCHIEBEN");
				var own = CloneBuilder.CloneOf(root.transform);
				if (own != null) o.Add(own.replacesSource ? "ZURÜCKSETZEN" : "ENTFERNEN");
			}
			o.Add("FERTIG");
			return o;
		}

		public PitstopEditState(LocoResourceModule? module, string? disabledId, GameObject? root, int sel) : base(new CommsRadioState(
			titleText: root != null ? PitstopEditor.LabelOf(root) : Tr.T("Pitstop", "Pitstop"),
			contentText: (module != null ? Tr.T($"{PitstopEditor.ResourceName(module)}: {(disabledId != null ? "AUS" : "an")}\n", $"{PitstopEditor.ResourceName(module)}: {(disabledId != null ? "OFF" : "on")}\n") : "")
				+ (root != null && CloneBuilder.CloneOf(root.transform) is PlacedClone c ? Tr.T($"Richtung {Fmt.M(c.yaw, 0)}° · Höhe {Fmt.M(c.position[1], 2)} m\n", $"Heading {Fmt.M(c.yaw, 0)}° · Height {Fmt.M(c.position[1], 2)} m\n") : "")
				+ Tr.T("Mausrad = Auswahl", "Wheel = select"),
			actionText: Tr.Act(OptionsFor(module, disabledId, root)[Mathf.Clamp(sel, 0, OptionsFor(module, disabledId, root).Count - 1)]),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.module = module; this.disabledId = disabledId; this.root = root;
			options = OptionsFor(module, disabledId, root);
			this.sel = Mathf.Clamp(sel, 0, options.Count - 1);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up) return new PitstopEditState(module, disabledId, root, (sel + options.Count - 1) % options.Count);
			if (action == InputAction.Down) return new PitstopEditState(module, disabledId, root, (sel + 1) % options.Count);
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			string opt = options[sel];
			switch (opt)
			{
				case "MODUL AN":
				case "MODUL AUS":
				{
					string name = PitstopEditor.ResourceName(module!);
					if (disabledId != null) PitstopEditor.Enable(disabledId); else PitstopEditor.Disable(module!);
					return new PitstopIdleState(Tr.T($"{name} {(disabledId != null ? "eingeschaltet" : "abgeschaltet")}\nKlick = weiter", $"{name} {(disabledId != null ? "switched on" : "switched off")}\nClick = next"));
				}
				case "MODUL DREHEN": return new PitstopModuleAdjustState(module!, false, root!);
				case "MODUL HÖHE": return new PitstopModuleAdjustState(module!, true, root!);
				case "MODUL VERSCHIEBEN": return new PitstopModuleMoveState(module!, root!);
				case "DREHEN":
				case "HÖHE":
				case "VERSCHIEBEN":
				{
					// Spiel-Pitstop: wird durch eine Kopie an derselben Stelle ersetzt (Original ausgeblendet),
					// denn das Original ist statisch zusammengefasst und lässt sich nicht bewegen.
					var c = CloneBuilder.CloneOf(root!.transform) ?? CloneBuilder.ReplaceVanilla(root, out string err);
					if (c == null) { utility.PlaySound(VanillaSoundCommsRadio.Warning); return new PitstopIdleState(Tr.T("Fehlgeschlagen", "Failed")); }
					if (opt == "DREHEN") return new CloneAdjustState(c, false);
					if (opt == "HÖHE") return new CloneAdjustState(c, true);
					var src = CloneBuilder.Source(c);
					if (src == null) return new PitstopIdleState(Tr.T("Vorlage nicht geladen", "Template not loaded"));
					return new ClonePlaceState(src, c.yaw, c);
				}
				case "ENTFERNEN":
				case "ZURÜCKSETZEN":
				{
					var c = CloneBuilder.CloneOf(root!.transform);
					if (c == null) return new PitstopIdleState();
					bool reset = CloneBuilder.Delete(c);
					PlacerSession.ShowHighlight(null);
					return new PitstopIdleState(reset ? Tr.T("Original-Pitstop\nwiederhergestellt", "Original pitstop\nrestored") : Tr.T("Pitstop entfernt", "Pitstop removed"));
				}
				default:
					PlacerSession.ShowHighlight(null);
					return new PitstopIdleState(Tr.T("Gespeichert", "Saved"));
			}
		}
	}

	/// <summary>Drehung oder lokale Höhe eines einzelnen Moduls eines generierten Pitstops.</summary>
	internal sealed class PitstopModuleAdjustState : AStateBehaviour
	{
		private readonly LocoResourceModule module;
		private readonly bool height;
		private readonly GameObject root;

		public PitstopModuleAdjustState(LocoResourceModule module, bool height, GameObject root) : base(new CommsRadioState(
			titleText: height ? Tr.T("Modul Höhe", "Module height") : Tr.T("Modul drehen", "Rotate module"),
			contentText: (height ? Tr.T($"Lokale Höhe {Fmt.M(module.transform.localPosition.y, 2)} m", $"Local height {Fmt.M(module.transform.localPosition.y, 2)} m") : Tr.T($"Drehung {Fmt.M(module.transform.localEulerAngles.y, 0)}°", $"Rotation {Fmt.M(module.transform.localEulerAngles.y, 0)}°"))
				+ Tr.T("\nMausrad (Shift/Strg = grob)\nKlick = fertig", "\nWheel (Shift/Ctrl = coarse)\nClick = done"),
			actionText: Tr.Act("FERTIG"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.module = module; this.height = height; this.root = root;
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up || action == InputAction.Down)
			{
				float dir = action == InputAction.Up ? 1f : -1f;
				if (height)
				{
					var p = module.transform.localPosition;
					p.y = (float)System.Math.Round(p.y - dir * PlacerSession.NextStep(), 3);
					module.transform.localPosition = p;
				}
				else
				{
					var e = module.transform.localEulerAngles;
					e.y = (float)System.Math.Round(Mathf.Repeat(e.y + dir * PlacerSession.NextAngleStep(), 360f), 2);
					module.transform.localEulerAngles = e;
				}
				PitstopEditor.SaveTransform(module);
				PlacerSession.HighlightOutline(ObjectDisabler.WireBox(module.gameObject));
				return new PitstopModuleAdjustState(module, height, root);
			}
			PitstopEditor.SaveTransform(module);
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new PitstopEditState(module, PitstopEditor.IsDisabled(module) ? PitstopEditor.EditOf(module)?.id : null, root, 0);
		}
	}

	/// <summary>Ein einzelnes Pitstop-Modul an einen neuen Bodenpunkt verschieben.</summary>
	internal sealed class PitstopModuleMoveState : AStateBehaviour
	{
		private readonly LocoResourceModule module;
		private readonly GameObject root;
		private readonly Vector3 originalLocal;
		private readonly float lift;
		private bool committed;

		public PitstopModuleMoveState(LocoResourceModule module, GameObject root) : base(new CommsRadioState(
			titleText: Tr.T("Modul verschieben", "Move module"),
			contentText: Tr.T("Laser auf Zielort\nKlick = Modul setzen", "Aim at the target spot\nClick = place module"),
			actionText: Tr.Act("SETZEN"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.module = module; this.root = root;
			originalLocal = module.transform.localPosition;
			lift = CloneHelper.PivotAboveGround(module.transform, 0f);
			// Identität noch an der Originalposition sichern, bevor die Live-Vorschau das Modul bewegt.
			PitstopEditor.SaveTransform(module);
		}

		public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
		{
			PlacerSession.UpdateHit(utility.SignalOrigin, allowSnap: false);
			PlacerSession.HideCursor();
			if (PlacerSession.GroundHit.HasValue)
			{
				module.transform.position = PlacerSession.GroundHit.Value + GameAccess.OriginShift + Vector3.up * lift;
				PlacerSession.HighlightOutline(ObjectDisabler.WireBox(module.gameObject));
			}
			return this;
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate || !PlacerSession.GroundHit.HasValue) return this;
			committed = true;
			PitstopEditor.SaveTransform(module);
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new PitstopEditState(module, PitstopEditor.IsDisabled(module) ? PitstopEditor.EditOf(module)?.id : null, root, 0);
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (!committed) module.transform.localPosition = originalLocal;
		}
	}

	/// <summary>Neuer Pitstop: eine der weltweit bereits geladenen Vorlagen wählen → Kopie mit allen Modulen setzen.</summary>
	internal sealed class PitstopNewState : AStateBehaviour
	{
		private static List<GameObject> s_roots = new List<GameObject>();
		private readonly int index;

		private static string Text(int i)
		{
			if (s_roots.Count == 0) return Tr.T("Noch keine Pitstop-Vorlage\naus der Welt geladen\nKlick = zurück", "No pitstop template\nloaded from the world yet\nClick = back");
			if (i >= s_roots.Count) return Tr.T("Zurück zum Menü\nKlick = zurück", "Back to the menu\nClick = back");
			var r = s_roots[i];
			return Tr.T($"Vorlage {i + 1}/{s_roots.Count}: {PitstopEditor.LabelOf(r)}\n{PitstopEditor.ModuleCount(r)} Module (alle an)\nMausrad = andere · Klick = setzen", $"Template {i + 1}/{s_roots.Count}: {PitstopEditor.LabelOf(r)}\n{PitstopEditor.ModuleCount(r)} modules (all on)\nWheel = other · Click = place");
		}

		public PitstopNewState(int index, bool refresh = true) : base(new CommsRadioState(
			titleText: Tr.T("Neuer Pitstop", "New pitstop"),
			contentText: Text(Prepare(index, refresh)),
			actionText: Tr.Act(s_roots.Count == 0 || index >= s_roots.Count ? "ZURÜCK" : "PLATZIEREN"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.index = index;
		}

		private static int Prepare(int index, bool refresh)
		{
			if (refresh || s_roots.Count == 0) s_roots = PitstopEditor.VanillaRoots();
			s_roots.RemoveAll(r => r == null);
			return index;
		}

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous)
		{
			if (index < s_roots.Count && s_roots[index] != null && s_roots[index].scene.IsValid()) PlacerSession.HighlightOutline(ObjectDisabler.WireBox(s_roots[index]));
			else PlacerSession.ShowHighlight(null);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			int n = s_roots.Count + 1; // + „Zurück“
			if (action == InputAction.Up) return new PitstopNewState((index + n - 1) % n, false);
			if (action == InputAction.Down) return new PitstopNewState((index + 1) % n, false);
			if (s_roots.Count == 0 || index >= s_roots.Count || s_roots[index] == null) { PlacerSession.ShowHighlight(null); return PlacerMenu.Return(); }
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			PlacerSession.ShowHighlight(null);
			return new ClonePlaceState(s_roots[index], 0f, null);
		}
	}

	// ======================================================================
	// Modus "Vegetation entfernen": Boden anzielen → Quadrat (Mausrad = Größe) → Klick = Gras/Büsche/Bäume weg.
	// Eigene Fläche anzielen → Klick = Vegetation zurück.
	// ======================================================================

	internal sealed class VegetationPickState : AStateBehaviour
	{
		private static float s_radius = 4f;
		private readonly PlacedVegetationMask? existing;
		private readonly bool hasHit;

		public VegetationPickState(PlacedVegetationMask? existing, bool hasHit, string? message = null) : base(new CommsRadioState(
			titleText: Tr.T("Vegetation entfernen", "Remove vegetation"),
			contentText: message ?? (!VegetationBuilder.Available ? Tr.T("Vegetation Studio nicht gefunden", "Vegetation Studio not found")
				: existing != null ? Tr.T($"Eigene Fläche {Fmt.M(existing.radius * 2f, 1)} m\nKlick = Vegetation zurück", $"Own area {Fmt.M(existing.radius * 2f, 1)} m\nClick = restore vegetation")
				: hasHit ? Tr.T($"Fläche {Fmt.M(s_radius * 2f, 1)} × {Fmt.M(s_radius * 2f, 1)} m\nMausrad = Größe (Shift/Strg grob)\nKlick = entfernen", $"Area {Fmt.M(s_radius * 2f, 1)} × {Fmt.M(s_radius * 2f, 1)} m\nWheel = size (Shift/Ctrl coarse)\nClick = remove")
				: Tr.T("Laser auf den Boden\nKlick ins Leere = Menü", "Aim at the ground\nClick on nothing = menu")),
			actionText: Tr.Act(existing != null ? "ZURÜCK" : hasHit ? "ENTFERNEN" : "ZIELEN"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.existing = existing; this.hasHit = hasHit;
		}

		private static float Step()
		{
			bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
			bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
			return ctrl ? 5f : shift ? 1f : 0.25f;
		}

		public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
		{
			PlacerSession.UpdateHit(utility.SignalOrigin, allowSnap: false);
			PlacerSession.HideCursor();
			var g = PlacerSession.GroundHit;
			var ex = g.HasValue ? VegetationBuilder.At(g.Value) : null;
			if (ex != null) PlacerSession.HighlightOutline(VegetationBuilder.Outline(ex.Position, ex.radius, ex.yaw));
			else if (g.HasValue) PlacerSession.HighlightOutline(VegetationBuilder.Outline(g.Value, s_radius, utility.SignalOrigin.eulerAngles.y));
			else PlacerSession.ShowHighlight(null);
			if (ex == existing && g.HasValue == hasHit) return this;
			return new VegetationPickState(ex, g.HasValue);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up || action == InputAction.Down)
			{
				if (existing != null) return this;
				// Mausrad nach oben = größer (InputAction.Up kommt beim Runterscrollen)
				s_radius = Mathf.Clamp(s_radius + (action == InputAction.Up ? -1f : 1f) * Step(), 0.5f, 100f);
				return new VegetationPickState(null, hasHit);
			}
			if (!VegetationBuilder.Available) return PlacerMenu.Return();
			if (existing != null)
			{
				VegetationBuilder.Remove(existing.id);
				LayoutStore.Remove(existing.id);
				utility.PlaySound(VanillaSoundCommsRadio.Confirm);
				return new VegetationPickState(null, hasHit, Tr.T("Vegetation wiederhergestellt\n(kann einen Moment dauern)", "Vegetation restored\n(may take a moment)"));
			}
			var g = PlacerSession.GroundHit;
			if (!g.HasValue) { PlacerSession.ShowHighlight(null); return PlacerMenu.Return(); }
			var m = new PlacedVegetationMask
			{
				position = PlacedTrack.FromVec(g.Value),
				radius = (float)System.Math.Round(s_radius, 2),
				yaw = (float)System.Math.Round(utility.SignalOrigin.eulerAngles.y, 1),
			};
			LayoutStore.Layout.vegetationMasks.Add(m);
			if (!VegetationBuilder.Build(m))
			{
				LayoutStore.Layout.vegetationMasks.Remove(m);
				utility.PlaySound(VanillaSoundCommsRadio.Warning);
				return new VegetationPickState(null, hasHit, Tr.T("Fehlgeschlagen (siehe Log)", "Failed (see log)"));
			}
			LayoutStore.SaveQuiet();
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new VegetationPickState(m, true);
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not VegetationPickState) PlacerSession.ShowHighlight(null);
		}
	}
}
