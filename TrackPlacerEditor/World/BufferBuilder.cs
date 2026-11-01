using System.Linq;
using System;
using System.Collections.Generic;
using TrackPlacer.Data;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TrackPlacer.World
{
	/// <summary>
	/// Prellböcke: Klon eines Prellbocks aus der Spielwelt (Objekt mit BufferStop-Komponente, inkl. Kollision
	/// und Bruchverhalten), an ein freies Gleisende gesetzt.
	/// </summary>
	internal static class BufferBuilder
	{
		private const string NamePrefix = "[TP]_buffer_";
		private static readonly Dictionary<string, GameObject> s_built = new Dictionary<string, GameObject>();
		private static GameObject? s_template;

		public static IReadOnlyDictionary<string, GameObject> Built => s_built;

		public static void OnWorldUnloaded()
		{
			s_built.Clear();
			s_template = null;
		}

		/// <summary>Oberstes Objekt eines vorhandenen Prellbocks unterhalb des Gleis-Roots.</summary>
		private static GameObject? Template
		{
			get
			{
				if (s_template != null) return s_template;
				var root = GameAccess.Registry?.TrackRootParent;
				// Nur einen VOLLSTÄNDIG aktiven Prellbock als Vorlage nehmen: Das Spiel schaltet entfernte Prellböcke bzw.
				// deren Teile per Entfernungs-Optimierung ab. Eine Kopie davon bliebe unsichtbar (so in 0.11.21 passiert).
				// Ist gerade keiner aktiv, wird später erneut gesucht (RetryMissing).
				var all = Object.FindObjectsOfType<BufferStop>()
					.Where(b => b != null && b.gameObject.activeInHierarchy)
					.ToList();
				foreach (BufferStop bs in all)
				{
					Transform t = bs.transform;
					if (t.root.name.StartsWith(NamePrefix) || t.name.StartsWith(NamePrefix)) continue;
					// nach oben bis unter den Gleis-Root – aber nie in ein Gleis hinein (sonst würde ein ganzes Gleis kopiert)
					while (t.parent != null && t.parent != root && t.parent.GetComponent<RailTrack>() == null) t = t.parent;
					if (t.name.StartsWith(NamePrefix) || t.GetComponentInChildren<RailTrack>(true) != null) continue;
					if (!Complete(t.gameObject)) continue; // Teile abgeschaltet → nächster
					s_template = t.gameObject;
					Main.Log($"Prellbock-Vorlage: {CloneHelper.PathOf(s_template.transform)} (aktiv, vollständig)");
					break;
				}
				return s_template;
			}
		}

		private static float s_nextRetry;

		/// <summary>Objekt und alle Teile aktiv?</summary>
		private static bool Complete(GameObject go)
		{
			if (!go.activeInHierarchy) return false;
			foreach (var t in go.GetComponentsInChildren<Transform>(true)) if (!t.gameObject.activeSelf) return false;
			return true;
		}

		/// <summary>
		/// Läufer: fehlende Prellböcke nachbauen. Beim Laden ist oft noch kein Prellbock der Welt geladen (Kacheln werden
		/// gestreamt) – dann fehlt die Vorlage. Höchstens alle 10 s erneut suchen.
		/// </summary>
		public static void RetryMissing()
		{
			if (!GameAccess.TracksReady || Time.realtimeSinceStartup < s_nextRetry) return;
			// erst nach dem Aufbau der eigenen Gleise (vorher läuft noch das Laden des Spielstands)
			if (LayoutStore.Layout.tracks.Count > 0 && TrackBuilder.Built.Count == 0) return;
			var missing = LayoutStore.Layout.buffers.Where(b => !s_built.TryGetValue(b.id, out var go) || go == null).ToList();
			if (missing.Count == 0) return;
			if (s_template != null && !Complete(s_template)) s_template = null;
			s_nextRetry = Time.realtimeSinceStartup + 10f;
			int ok = 0;
			foreach (var b in missing)
			{
				s_built.Remove(b.id);
				if (Build(b, out _)) ok++;
				else if (s_template == null) break; // ohne Vorlage hat der Rest auch keinen Sinn
			}
			if (ok > 0) { Main.Log($"Prellböcke nachgebaut: {ok}/{missing.Count}"); CapBuilder.BuildAll(); }
		}

		public static bool Build(PlacedBuffer placed, out string error)
		{
			error = "";
			if (s_built.ContainsKey(placed.id)) return true;
			if (!TrackBuilder.CheckReady(out error)) return false;
			GameObject? template = Template;
			if (template == null) { error = Tr.T("Keine Prellbock-Vorlage in der Welt gefunden", "No buffer stop template found in the world"); return false; }

			GameObject? clone = null;
			try
			{
				bool wasActive = template.activeSelf;
				template.SetActive(false);
				clone = Object.Instantiate(template);
				template.SetActive(wasActive);
				clone.name = NamePrefix + placed.id;

				// Vorlage: Ursprung am Gleisende, forward zeigt ins Gleis (wie bei den Vanilla-Prellböcken)
				clone.transform.SetPositionAndRotation(placed.Position, Quaternion.Euler(0f, placed.yaw, 0f));
				clone.SetActive(true);
				TrackBuilder.MoveUnderRailwayRoot(clone.transform, GameAccess.Registry!);

				s_built[placed.id] = clone;
				TrackEnds.MarkDirty();
				return true;
			}
			catch (Exception e)
			{
				error = e.GetType().Name + ": " + e.Message;
				Main.Log($"Bau des Prellbocks {placed.id} fehlgeschlagen: {e}");
				if (clone != null) Object.Destroy(clone);
				return false;
			}
		}

		public static bool Remove(string id)
		{
			if (s_built.TryGetValue(id, out GameObject go) && go != null) Object.Destroy(go);
			s_built.Remove(id);
			TrackEnds.MarkDirty();
			return true;
		}

		/// <summary>yaw (Grad) so, dass forward vom Gleisende ins Gleis zeigt.</summary>
		public static float YawFor(TrackEnd end)
		{
			Vector2 into = -end.outward;
			return Mathf.Atan2(into.x, into.y) * Mathf.Rad2Deg;
		}
	}
}
