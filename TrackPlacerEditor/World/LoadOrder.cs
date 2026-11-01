using TrackPlacer.Data;
using UnityEngine;

namespace TrackPlacer.World
{
	/// <summary>
	/// Feste Lade-Reihenfolge pro Welt:
	///   1. eigene Gleise (+ Weichen, entfernte Spielgleise)
	///   2. Bahnhofs-IDs und Lademaschinen
	///   3. alles andere (Prellböcke, Schotterenden, Klone, deaktivierte Objekte, Pitstops, Vegetation, Fern-Gleise …)
	/// Schritt 3 wartet auf 1 und 2. Kommen die Bahnhöfe nicht (z. B. keine Bahnhofsdaten), geht es nach 60 s trotzdem weiter.
	/// </summary>
	internal static class LoadOrder
	{
		private static bool s_tracks;
		private static float s_tracksAt = -1f;
		private static bool s_logged;

		public static void OnWorldUnloaded() { s_tracks = false; s_tracksAt = -1f; s_logged = false; }

		/// <summary>Nach dem Bau der eigenen Gleise (früher Aufbau oder WorldLoader).</summary>
		public static void TracksBuilt()
		{
			if (s_tracks) return;
			s_tracks = true;
			s_tracksAt = Time.realtimeSinceStartup;
			Main.Log("Lade-Reihenfolge: 1. Gleise fertig");
		}

		public static bool Tracks => s_tracks;

		/// <summary>Braucht das Layout Bahnhöfe (IDs, Lademaschinen, entfernte Spielgleise)?</summary>
		private static bool NeedStations =>
			LayoutStore.Layout.machines.Count > 0 || LayoutStore.Layout.vanillaMachines.Count > 0
			|| LayoutStore.Layout.vanillaTrackIds.Count > 0 || LayoutStore.Layout.tracks.Exists(t => !string.IsNullOrEmpty(t.yardId));

		/// <summary>Schritt 3 erlaubt?</summary>
		public static bool Objects
		{
			get
			{
				// Layout ganz ohne Gleise/Weichen: Schritt 1 entfällt
				if (!s_tracks && GameAccess.TracksReady && LayoutStore.Layout.tracks.Count == 0 && LayoutStore.Layout.switches.Count == 0)
					TracksBuilt();
				if (!s_tracks) return false;
				bool stations = !NeedStations || StationLink.StationsDone;
				bool timeout = Time.realtimeSinceStartup - s_tracksAt > 60f;
				if (!stations && !timeout) return false;
				if (!s_logged)
				{
					s_logged = true;
					Main.Log(stations ? "Lade-Reihenfolge: 2. Gleis-IDs/Lademaschinen fertig → 3. Objekte"
						: "Lade-Reihenfolge: Bahnhöfe nach 60 s nicht bereit → 3. Objekte trotzdem");
				}
				return true;
			}
		}
	}
}
