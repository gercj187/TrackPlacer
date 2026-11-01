using System.Linq;
using System.Text;
using CommsRadioAPI;
using DV;
using UnityEngine;
using InputAction = CommsRadioAPI.InputAction;

namespace TrackPlacer.Radio
{
	/// <summary>
	/// Debug-Hilfe: schreibt ins Log, was unter dem Laser liegt (Objekt, Hierarchie, Komponenten, Meshes, Materialien)
	/// sowie alle Renderer, deren Bounds den Trefferpunkt enthalten – auch ohne Collider, z. B. Übergangs-Abdeckungen.
	/// </summary>
	internal static class Inspector
	{
		public static string Inspect(Transform signalOrigin)
		{
			var sb = new StringBuilder();
			Ray ray = new Ray(signalOrigin.position, signalOrigin.forward);
			var hits = Physics.RaycastAll(ray, Main.Settings.maxDistance, ~0, QueryTriggerInteraction.Collide)
				.Where(h => !h.transform.IsChildOf(signalOrigin.root))
				.OrderBy(h => h.distance)
				.Take(3)
				.ToArray();
			if (hits.Length == 0) return Tr.T("Kein Treffer", "No hit");

			Vector3 p = hits[0].point;
			sb.AppendLine($"=== Inspektor bei {p - WorldMover.currentMove} (statisch) ===");
			foreach (var h in hits)
			{
				sb.AppendLine($"Collider: {Path(h.transform)}  (Layer {LayerMask.LayerToName(h.collider.gameObject.layer)}, {h.distance:F1} m)");
				AppendObject(sb, h.transform, "  ");
			}

			sb.AppendLine("Renderer, deren Bounds den Punkt enthalten:");
			int n = 0;
			foreach (var r in Object.FindObjectsOfType<MeshRenderer>())
			{
				var b = r.bounds;
				b.Expand(0.3f);
				if (!b.Contains(p)) continue;
				var mf = r.GetComponent<MeshFilter>();
				string mats = string.Join(", ", r.sharedMaterials.Where(m => m != null).Select(m => $"{m.name} [{m.shader?.name}]"));
				sb.AppendLine($"  {Path(r.transform)} | Mesh {mf?.sharedMesh?.name ?? "-"} | {mats}");
				if (++n >= 25) { sb.AppendLine("  …"); break; }
			}

			Main.Log(sb.ToString());
			return hits[0].transform.name;
		}

		private static void AppendObject(StringBuilder sb, Transform t, string indent)
		{
			foreach (var c in t.GetComponents<Component>())
				if (c != null && !(c is Transform)) sb.AppendLine($"{indent}- {c.GetType().FullName}");
			var r = t.GetComponent<Renderer>();
			if (r != null)
				foreach (var m in r.sharedMaterials) if (m != null) sb.AppendLine($"{indent}  Material: {m.name} [{m.shader?.name}]");
		}

		private static string Path(Transform t)
		{
			var parts = new System.Collections.Generic.List<string>();
			for (var x = t; x != null && parts.Count < 8; x = x.parent) parts.Insert(0, x.name);
			return string.Join("/", parts);
		}
	}

	internal sealed class InspectState : AStateBehaviour
	{
		public InspectState(string? message = null) : base(new CommsRadioState(
			titleText: Tr.T("Inspektor", "Inspector"),
			contentText: message ?? Tr.T("Zielen + Klick =\nObjekt ins Log schreiben", "Aim + click =\nwrite object to log"),
			actionText: Tr.Act("PRÜFEN"),
			buttonBehaviour: ButtonBehaviourType.Regular))
		{ }

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			string name = Inspector.Inspect(utility.SignalOrigin);
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new InspectState(ConfirmState.Trim(name) + Tr.T("\nDetails im Player.log", "\nDetails in Player.log"));
		}
	}
}
