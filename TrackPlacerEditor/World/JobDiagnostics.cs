using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DV.Logic.Job;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TrackPlacer.World
{
	/// <summary>
	/// Diagnose für „Zug steht doch auf dem Gleis, Auftrag lässt sich nicht beenden“: Für alle Wagen in der Nähe
	/// ins Log, auf welchem Logik-Gleis das Spiel sie sieht (aktuell / vorderes / hinteres Drehgestell), samt
	/// RailTrack-Name und ob es ein eigenes oder entferntes Gleis ist. Das Spiel zählt einen Wagen erst dann als
	/// „auf dem Gleis“, wenn BEIDE Drehgestelle auf demselben Gleis stehen.
	/// </summary>
	internal static class JobDiagnostics
	{
		private static string Describe(Track? t)
		{
			if (t == null) return "–";
			string id = t.ID != null ? t.ID.FullID : "?";
			if (!RailTrackRegistry.LogicToRailTrack.TryGetValue(t, out var rail) || rail == null) return id + " (kein RailTrack)";
			string flags = (TrackBuilder.IsOwn(rail) ? " [eigen]" : "") + (VanillaRemover.IsRemoved(rail) ? " [ENTFERNT]" : "");
			return $"{id} ({rail.name}{flags})";
		}

		public static void LogCarsNearPlayer(float radius = 300f)
		{
			try
			{
				var cam = Camera.main;
				Vector3 p = cam != null ? cam.transform.position : Vector3.zero;
				var sb = new StringBuilder($"Diagnose Wagen/Gleise (Umkreis {radius:0} m):");
				int n = 0;
				foreach (var car in Object.FindObjectsOfType<TrainCar>().OrderBy(c => (c.transform.position - p).sqrMagnitude))
				{
					if ((car.transform.position - p).sqrMagnitude > radius * radius) continue;
					var lc = car.logicCar;
					if (lc == null) continue;
					sb.Append($"\n  {lc.ID}: auf {Describe(lc.CurrentTrack)} | vorn {Describe(lc.FrontBogieTrack)} | hinten {Describe(lc.RearBogieTrack)}");
					if (++n >= 60) break;
				}
				if (n == 0) sb.Append(" keine Wagen in der Nähe");

				// Bahnhofs-ID-Tabelle des Spiels prüfen (dort sucht es Gleise per ID)
				var map = typeof(YardTracksOrganizer).GetField("yardTrackIdToTrack", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
					?.GetValue(YardTracksOrganizer.Instance) as Dictionary<string, Track>;
				if (map != null)
				{
					var wrong = map.Where(kv => kv.Value == null || kv.Value.ID == null || kv.Value.ID.FullID != kv.Key).Take(20).ToList();
					sb.Append($"\n  Bahnhofs-ID-Tabelle: {map.Count} Einträge, {wrong.Count} unstimmig");
					foreach (var kv in wrong) sb.Append($"\n    {kv.Key} → {Describe(kv.Value)}");
				}
				Main.Log(sb.ToString());
			}
			catch (Exception e) { Main.Log("Diagnose Wagen/Gleise fehlgeschlagen: " + e); }
		}
		/// <summary>
		/// „Gegen etwas Unsichtbares gefahren“: alle festen Collider im Umkreis um den Spieler ins Log – mit Pfad,
		/// Größe und ob in der Nähe ein sichtbares Modell dazu existiert. Unsichtbare stehen oben.
		/// </summary>
		public static void LogCollidersAroundPlayer(float radius = 12f)
		{
			try
			{
				var cam = Camera.main;
				if (cam == null) { Main.Log("Diagnose Collider: keine Kamera"); return; }
				Vector3 p = cam.transform.position + Vector3.down * 1f;
				var hits = Physics.OverlapSphere(p, radius, ~0, QueryTriggerInteraction.Ignore);
				var rows = new List<(bool visible, string text)>();
				foreach (var c in hits)
				{
					if (c == null || c.GetComponentInParent<TrainCar>() != null) continue;
					if (c.transform.root == cam.transform.root) continue; // Spieler selbst
					if (c.GetType().Name == "TerrainCollider") continue;
					bool visible = false;
					for (var t = c.transform; t != null && !visible; t = t.parent)
						foreach (var r in t.GetComponents<Renderer>())
							if (r != null && r.enabled && r.gameObject.activeInHierarchy && !ObjectDisabler.SkipRenderer(r)) { visible = true; break; }
					if (!visible)
						foreach (var r in c.GetComponentsInChildren<Renderer>())
							if (r != null && r.enabled) { visible = true; break; }
					Vector3 sp = c.bounds.center - GameAccess.OriginShift;
					string own = c.GetComponentInParent<CloneBuilder.CloneMarker>() != null ? " [KLON]" : (c.transform.root.name.StartsWith("[TP]") || c.name.StartsWith("[TP]") ? " [eigen]" : "");
					rows.Add((visible, $"{(visible ? "sichtbar  " : "UNSICHTBAR")} {CloneHelper.PathOf(c.transform)}{own} – {c.GetType().Name}, {c.bounds.size.x:0.0}×{c.bounds.size.y:0.0}×{c.bounds.size.z:0.0} m, Mitte {sp.x:0.0}/{sp.y:0.0}/{sp.z:0.0}, Ebene {LayerMask.LayerToName(c.gameObject.layer)}, Szene {c.gameObject.scene.name}"));
				}
				var sb = new StringBuilder($"Diagnose Collider (Umkreis {radius:0} m um {p - GameAccess.OriginShift}): {rows.Count}");
				foreach (var r in rows.OrderBy(x => x.visible)) sb.Append("\n  ").Append(r.text);
				Main.Log(sb.ToString());
			}
			catch (Exception e) { Main.Log("Diagnose Collider fehlgeschlagen: " + e); }
		}
	}
}
