using System.Collections.Generic;
using CommsRadioAPI;
using DV;
using TrackPlacer.World;
using UnityEngine;
using InputAction = CommsRadioAPI.InputAction;

namespace TrackPlacer.Radio
{
	// ======================================================================
	// Modus "Objekte": anzielen → Mausrad wählt die Ebene (Teil … Gebäude) → Klick = deaktivieren.
	// Deaktivierte Objekte (Bearbeitungsmodus: halbtransparent) anzielen → Klick = wieder aktivieren.
	// ======================================================================

	internal sealed class ObjectIdleState : AStateBehaviour
	{
		public ObjectIdleState(string? message = null) : base(new CommsRadioState(
			titleText: Tr.T("Objekte", "Objects"),
			contentText: message ?? (Main.Settings.editMode ? Tr.T("Objekte deaktivieren\nKlick = Start · Rad = Menü", "Disable objects\nClick = start · Wheel = menu") : Tr.T("Spielmodus: deaktivierte\nObjekte sind ausgeblendet\nKlick = Start · Rad = Menü", "Play mode: disabled\nobjects are hidden\nClick = start · Wheel = menu")),
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
			return new ObjectPickState(null, null, 0, null);
		}
	}

	internal sealed class ObjectPickState : AStateBehaviour
	{
		private readonly Transform? hitTransform;   // getroffener Collider (Basis der Kette)
		private readonly List<GameObject>? chain;
		private readonly int level;
		private readonly string? ghostId;           // deaktiviertes Objekt unter dem Laser

		public ObjectPickState(Transform? hitTransform, List<GameObject>? chain, int level, string? ghostId) : base(new CommsRadioState(
			titleText: Tr.T("Objekte", "Objects"),
			contentText: Describe(chain, level, ghostId),
			actionText: Tr.Act(ghostId != null ? "AKTIVIEREN" : chain != null && chain.Count > 0 ? "DEAKTIVIEREN" : "ZIELEN"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.hitTransform = hitTransform;
			this.chain = chain;
			this.level = level;
			this.ghostId = ghostId;
		}

		private static string Describe(List<GameObject>? chain, int level, string? ghostId)
		{
			if (ghostId != null) return Tr.T($"Deaktiviert:\n{ObjectDisabler.GhostLabel(ghostId)}\nKlick = wieder aktivieren", $"Disabled:\n{ObjectDisabler.GhostLabel(ghostId)}\nClick = enable again");
			if (chain == null || chain.Count == 0) return Tr.T("Laser auf ein Objekt\nKlick ins Leere = Abbruch", "Aim at an object\nClick on nothing = cancel");
			var go = chain[Mathf.Clamp(level, 0, chain.Count - 1)];
			return Tr.T($"{ObjectDisabler.Label(go)}\nEbene {level + 1}/{chain.Count} (Mausrad)\nKlick = deaktivieren", $"{ObjectDisabler.Label(go)}\nLevel {level + 1}/{chain.Count} (wheel)\nClick = disable");
		}

		private GameObject? Current => chain != null && chain.Count > 0 ? chain[Mathf.Clamp(level, 0, chain.Count - 1)] : null;

		public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
		{
			Transform origin = utility.SignalOrigin;
			var ray = new Ray(origin.position, origin.forward);
			float max = Main.Settings.maxDistance;

			// Nächster echter Treffer (ohne eigenes Funkgerät/Spieler und ohne Fahrzeuge)
			Transform? hit = null;
			float hitDist = max;
			var hits = Physics.RaycastAll(ray, max, ~0, QueryTriggerInteraction.Ignore);
			System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
			foreach (var h in hits)
			{
				if (h.transform.IsChildOf(origin.root)) continue;
				if (h.collider.GetComponentInParent<TrainCar>() != null) continue;
				hit = h.transform;
				hitDist = h.distance;
				break;
			}

			// Deaktivierte Objekte haben keine Collider mehr – über ihre Grenzen prüfen
			string? ghost = ObjectDisabler.FindGhost(ray, hitDist, out _);
			if (ghost != null)
			{
				if (ghost == ghostId) return this;
				PlacerSession.HighlightOutline(ObjectDisabler.GhostWire(ghost));
				return new ObjectPickState(null, null, 0, ghost);
			}

			if (hit == hitTransform && ghostId == null) return this;
			if (hit == null)
			{
				PlacerSession.ShowHighlight(null);
				return new ObjectPickState(null, null, 0, null);
			}
			var newChain = ObjectDisabler.CandidateChain(hit, out int def, skipColliderOnly: true); // keine Collider/Cube einzeln
			if (newChain.Count > 0) PlacerSession.HighlightOutline(ObjectDisabler.WireBox(newChain[def]));
			else PlacerSession.ShowHighlight(null);
			return new ObjectPickState(hit, newChain, def, null);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up || action == InputAction.Down)
			{
				if (chain == null || chain.Count == 0) return this;
				int l = Mathf.Clamp(level + (action == InputAction.Up ? 1 : -1), 0, chain.Count - 1);
				PlacerSession.HighlightOutline(ObjectDisabler.WireBox(chain[l]));
				return new ObjectPickState(hitTransform, chain, l, null);
			}

			if (ghostId != null)
			{
				ObjectDisabler.Enable(ghostId);
				utility.PlaySound(VanillaSoundCommsRadio.Confirm);
				return new ObjectIdleState(Tr.T("Wieder aktiviert\nKlick = weiter", "Enabled again\nClick = next"));
			}
			var go = Current;
			if (go == null)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Cancel);
				return new ObjectIdleState();
			}
			if (!ObjectDisabler.Disable(go, out string error))
			{
				utility.PlaySound(VanillaSoundCommsRadio.Warning);
				return new ObjectIdleState(Tr.T("Nicht möglich:\n", "Not possible:\n") + ConfirmState.Trim(error));
			}
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new ObjectIdleState((Main.Settings.editMode ? Tr.T("Deaktiviert (halbtransparent)", "Disabled (semi-transparent)") : Tr.T("Ausgeblendet", "Hidden")) + Tr.T("\nKlick = weiter", "\nClick = next"));
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not ObjectPickState) PlacerSession.ShowHighlight(null);
		}
	}
}
