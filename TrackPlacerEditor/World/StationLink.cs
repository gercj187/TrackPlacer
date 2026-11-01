using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DV.Logic.Job;
using TrackPlacer.Data;
using UnityEngine;

namespace TrackPlacer.World
{
	/// <summary>
	/// Verbindet eigene Gleise mit Bahnhöfen:
	///  - setzt die Bahnhofsgleis-ID am Logik-Gleis (Track.OverrideTrackID), z. B. SM-B7S
	///  - trägt S/I/O-Gleise in die Gleislisten des Bahnhofs ein (Yard.StorageTracks / TransferIn / TransferOut).
	///    Der Job-Generator des Bahnhofs liest genau diese Listen, neue Einträge werden also mitgenutzt.
	///  - meldet das Gleis beim YardTracksOrganizer an (Platzreservierung für Jobs)
	/// L-Gleise gehören zu einer Lademaschine und werden über diese eingebunden.
	/// </summary>
	internal static class StationLink
	{
		public static readonly string[] Types = { "S", "I", "O", "L", "P" };
		public static string TypeName(string t) => t switch
		{
			"S" => "Storage", "I" => "Inbound", "O" => "Outbound", "L" => "Loading", "P" => "Parking", _ => Tr.T("ohne", "none"),
		};

		/// <summary>Alle Bahnhöfe der Welt, erst wenn ihre Logik (Start-Coroutine) fertig ist.</summary>
		public static bool StationsReady =>
			StationController.allStations != null && StationController.allStations.Count > 0 &&
			StationController.allStations.All(s => s == null || s.logicStation != null);

		public static List<StationController> Stations =>
			StationController.allStations?.Where(s => s != null && s.logicStation != null).OrderBy(YardOf).ToList() ?? new List<StationController>();

		public static string StationName(StationController s) => s.stationInfo?.Name ?? YardOf(s);

		public static string YardOf(StationController s) => s.stationInfo?.YardID ?? s.logicStation?.ID ?? "?";

		public static StationController? Nearest(Vector3 staticPos)
		{
			StationController? best = null;
			float bestD = float.MaxValue;
			Vector3 shift = GameAccess.OriginShift;
			foreach (var s in Stations)
			{
				float d = ((s.transform.position - shift) - staticPos).sqrMagnitude;
				if (d < bestD) { bestD = d; best = s; }
			}
			return best;
		}

		public static StationController? ByYard(string yard) => Stations.FirstOrDefault(s => string.Equals(YardOf(s), yard, StringComparison.OrdinalIgnoreCase));

		public static string FullId(PlacedTrack t) =>
			string.IsNullOrEmpty(t.yardId) ? "" : $"{t.yardId}-{t.subYard}{TrimNumber(t.number)}{t.trackType}";

		public static string FullId(VanillaTrackId t) =>
			string.IsNullOrEmpty(t.yardId) ? "" : $"{t.yardId}-{t.subYard}{TrimNumber(t.number)}{t.trackType}";

		private static string TrimNumber(string n) => int.TryParse(n, out int i) ? i.ToString() : n;

		private static readonly Regex VanillaName = new Regex(@"\[Y\]_\[([a-z0-9]*)\]_\[([a-z0-9]+)-(\d+)-([a-z0-9]+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
		private static Dictionary<string, HashSet<int>>? s_vanillaIds;

		/// <summary>Vergebene Nummern der Spielgleise je "YARD|SUB" (aus den Gleisnamen, einmal pro Welt).</summary>
		private static Dictionary<string, HashSet<int>> VanillaIds
		{
			get
			{
				if (s_vanillaIds != null) return s_vanillaIds;
				s_vanillaIds = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
				var reg = GameAccess.Registry;
				if (reg == null) return s_vanillaIds;
				foreach (var t in reg.OrderedRailtracks)
				{
					if (t == null || TrackBuilder.IsOwn(t)) continue;
					var m = VanillaName.Match(t.name);
					if (!m.Success || !int.TryParse(m.Groups[3].Value, out int n)) continue;
					string key = (m.Groups[1].Value + "|" + m.Groups[2].Value).ToUpperInvariant();
					if (!s_vanillaIds.TryGetValue(key, out var set)) s_vanillaIds[key] = set = new HashSet<int>();
					set.Add(n);
				}
				return s_vanillaIds;
			}
		}

		/// <summary>Bereiche (Sub-Yards), die der Bahnhof im Spiel schon hat, z. B. A, B, C.</summary>
		public static List<string> VanillaSubYards(string yard) =>
			VanillaIds.Keys.Where(k => k.StartsWith(yard.ToUpperInvariant() + "|")).Select(k => k.Substring(k.IndexOf('|') + 1)).OrderBy(x => x).ToList();

		public static bool IsTaken(string yard, string sub, int number, string exceptId = "")
		{
			if (VanillaIds.TryGetValue((yard + "|" + sub).ToUpperInvariant(), out var set) && set.Contains(number)) return true;
			if (LayoutStore.Layout.vanillaTrackIds.Any(t => VanillaKey(t.name) != exceptId && string.Equals(t.yardId, yard, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(t.subYard, sub, StringComparison.OrdinalIgnoreCase) && int.TryParse(t.number, out int vn) && vn == number)) return true;
			return LayoutStore.Layout.tracks.Any(t => t.id != exceptId && string.Equals(t.yardId, yard, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(t.subYard, sub, StringComparison.OrdinalIgnoreCase) && int.TryParse(t.number, out int n) && n == number);
		}

		/// <summary>Nächste freie Nummer für Yard + Bereich (Spielgleise und eigene Gleise).</summary>
		public static int NextFreeNumber(string yard, string sub, string exceptId = "")
		{
			int n = 1;
			while (n < 99 && IsTaken(yard, sub, n, exceptId)) n++;
			return n;
		}

		// ------------------------------------------------------------------
		// Anwenden / Lösen
		// ------------------------------------------------------------------

		private static readonly Dictionary<string, (StationController station, string type, Track logic, RailTrack rail)> s_applied =
			new Dictionary<string, (StationController, string, Track, RailTrack)>();
		/// <summary>Ursprüngliche (generische) ID je Schlüssel, zum Zurücksetzen beim Löschen.</summary>
		private static readonly Dictionary<string, (Track logic, TrackID id)> s_original = new Dictionary<string, (Track, TrackID)>();

		public static void OnWorldUnloaded() { s_applied.Clear(); s_original.Clear(); s_vanillaIds = null; s_stationsDone = false; }

		private static bool s_stationsDone;
		/// <summary>Gleis-IDs/Lademaschinen für diese Welt schon angewendet?</summary>
		public static bool StationsDone => s_stationsDone;

		/// <summary>
		/// Gleis-IDs, Lademaschinen und das Lösen entfernter Spielgleise – einmal pro Welt, so früh wie möglich:
		/// spätestens VOR dem Laden der Jobs aus dem Spielstand. Der Job-Lader sucht Start-/Zielgleise über
		/// YardTracksOrganizer.yardTrackIdToTrack ("HB-D-06-S") und Lademaschinen über ihre ID; fehlt etwas, verwirft
		/// er die ganze Job-Kette – deren Wagen gelten dann als unbenutzt und verschwinden (leerer Bahnhof).
		/// </summary>
		public static bool ApplyStationsOnce(string when)
		{
			if (s_stationsDone) return true;
			if (!StationsReady || !GameAccess.TracksReady) return false;
			s_stationsDone = true;
			try
			{
				int ids = ApplyAll();
				int machines = MachineBuilder.BuildAll();
				int edits = MachineBuilder.ApplyVanillaEdits();
				int detached = VanillaRemover.DetachAllFromStations();
				Main.Log($"Gleis-IDs ({when}): {ids}, Lademaschinen: {machines}, geänderte Spielmaschinen: {edits}, entfernte Spielgleise aus Bahnhöfen gelöst: {detached}");
			}
			catch (Exception e) { Main.Log($"Gleis-IDs/Lademaschinen ({when}) fehlgeschlagen: {e}"); }
			return true;
		}

		/// <summary>Nachzügler (später gebaute Gleise/Maschinen), wenn die IDs schon früh angewendet wurden.</summary>
		public static int ApplyMissing()
		{
			int n = 0;
			foreach (var t in LayoutStore.Layout.tracks)
				if (!string.IsNullOrEmpty(t.yardId) && !s_applied.ContainsKey(t.id) && Apply(t, out _)) n++;
			foreach (var v in LayoutStore.Layout.vanillaTrackIds)
				if (!s_applied.ContainsKey(VanillaKey(v.name)) && Apply(v, out _)) n++;
			n += MachineBuilder.BuildAll();
			VanillaRemover.DetachAllFromStations();
			return n;
		}

		private static readonly System.Reflection.FieldInfo? FYtoIds =
			typeof(YardTracksOrganizer).GetField("yardTrackIdToTrack", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

		private static Dictionary<string, Track>? YardIdMap =>
			YardTracksOrganizer.Instance != null ? FYtoIds?.GetValue(YardTracksOrganizer.Instance) as Dictionary<string, Track> : null;

		/// <summary>Gleis unter seiner vollen ID im YardTracksOrganizer eintragen (dort sucht der Job-Lader).</summary>
		private static void RegisterYardId(Track logic)
		{
			var map = YardIdMap;
			if (map == null || logic.ID == null) return;
			foreach (var k in map.Where(kv => kv.Value == logic).Select(kv => kv.Key).ToList()) map.Remove(k);
			string full = logic.ID.FullID;
			if (map.TryGetValue(full, out var other) && other != logic && other != null)
				Main.Log($"Gleis-ID {full} war schon an ein anderes Gleis vergeben – wird auf das eigene Gleis umgestellt.");
			map[full] = logic;
		}

		private static void UnregisterYardId(Track logic)
		{
			var map = YardIdMap;
			if (map == null) return;
			foreach (var k in map.Where(kv => kv.Value == logic).Select(kv => kv.Key).ToList()) map.Remove(k);
		}

		public static string VanillaKey(string trackName) => "v:" + trackName;

		private static readonly System.Reflection.FieldInfo? FIdOverridden =
			typeof(Track).GetField("idOverridden", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

		/// <summary>Track.OverrideTrackID erlaubt nur EINEN Aufruf; für Änderungen den Merker vorher zurücksetzen.</summary>
		private static void SetTrackId(Track logic, TrackID id)
		{
			FIdOverridden?.SetValue(logic, false);
			logic.OverrideTrackID(id);
		}

		/// <summary>Alle IDs aus der JSON anwenden (nach dem Bauen, wenn die Bahnhöfe bereit sind).</summary>
		public static int ApplyAll()
		{
			int n = 0;
			foreach (var t in LayoutStore.Layout.tracks)
				if (!string.IsNullOrEmpty(t.yardId) && Apply(t, out _)) n++;
			foreach (var v in LayoutStore.Layout.vanillaTrackIds)
				if (Apply(v, out string e)) n++; else Main.Log($"Spielgleis-ID {v.name}: {e}");
			return n;
		}

		public static bool Apply(PlacedTrack placed, out string error)
		{
			error = "";
			Unapply(placed.id);
			if (string.IsNullOrEmpty(placed.yardId)) return true;
			if (!TrackBuilder.Built.TryGetValue(placed.id, out RailTrack rail) || rail == null) { error = "Gleis nicht gebaut"; return false; }
			return ApplyCore(placed.id, rail, placed.yardId, placed.subYard, placed.number, placed.trackType, out error);
		}

		public static bool Apply(VanillaTrackId v, out string error)
		{
			error = "";
			string key = VanillaKey(v.name);
			Unapply(key);
			RailTrack? rail = VanillaRemover.Find(v.name, v.Mid);
			if (rail == null) { error = "Spielgleis nicht gefunden"; return false; }
			return ApplyCore(key, rail, v.yardId, v.subYard, v.number, v.trackType, out error);
		}

		/// <summary>Logik-Gleis eines gebauten Gleises (eigenes oder Spielgleis).</summary>
		public static Track? LogicOf(RailTrack rail) =>
			rail != null && RailTrackRegistry.RailTrackToLogicTrack.TryGetValue(rail, out Track logic) ? logic : null;

		/// <summary>Darf die ID eines Spielgleises geändert werden? Nur generische (oder von uns vergebene).</summary>
		public static bool VanillaEditable(RailTrack rail, out string currentId)
		{
			var logic = LogicOf(rail);
			currentId = logic?.ID?.TrackPartOnly ?? "";
			if (logic == null) return false;
			if (LayoutStore.Layout.vanillaTrackIds.Exists(x => x.name == rail.name)) return true;
			return logic.ID == null || logic.ID.IsGeneric();
		}

		private static bool ApplyCore(string key, RailTrack rail, string yardId, string subYard, string numberS, string type, out string error)
		{
			error = "";
			Track? logic = LogicOf(rail);
			if (logic == null) { error = "kein Logik-Gleis"; return false; }
			var station = ByYard(yardId);
			if (station == null) { error = $"Bahnhof {yardId} fehlt"; return false; }

			try
			{
				if (!s_original.ContainsKey(key)) s_original[key] = (logic, logic.ID);
				string number = int.TryParse(numberS, out int num) ? num.ToString("D2") : numberS;
				SetTrackId(logic, new TrackID(yardId, subYard, number, type));

				var yard = station.logicStation.yard;
				List<Track>? list = type switch
				{
					"S" => yard.StorageTracks,
					"I" => yard.TransferInTracks,
					"O" => yard.TransferOutTracks,
					_ => null,
				};
				if (list != null && !list.Contains(logic)) list.Add(logic);

				if (type == "S" || type == "I" || type == "O" || type == "L")
				{
					var yto = YardTracksOrganizer.Instance;
					if (yto != null && !yto.IsTrackManagedByOrganizer(logic)) yto.InitializeYardTrack(logic);
					RegisterYardId(logic);
				}

				var all = station.AllStationTracks;
				if (all != null && !all.Contains(rail)) all.Add(rail);

				s_applied[key] = (station, type, logic, rail);
				Main.Log($"Gleis {key} → {yardId}-{subYard}{TrimNumber(numberS)}{type} ({TypeName(type)}) bei {station.stationInfo?.Name}");
				return true;
			}
			catch (Exception e)
			{
				error = e.GetType().Name + ": " + e.Message;
				Main.Log($"Gleis-ID für {key} fehlgeschlagen: {e}");
				return false;
			}
		}

		/// <summary>Gleis aus den Bahnhofslisten nehmen (vor Änderung oder Entfernen).</summary>
		public static void Unapply(string key)
		{
			if (!s_applied.TryGetValue(key, out var a)) return;
			try
			{
				var yard = a.station.logicStation?.yard;
				yard?.StorageTracks.Remove(a.logic);
				yard?.TransferInTracks.Remove(a.logic);
				yard?.TransferOutTracks.Remove(a.logic);
				if (a.rail != null) a.station.AllStationTracks?.Remove(a.rail);
				if (a.logic != null) UnregisterYardId(a.logic);
			}
			catch (Exception e) { Main.Log("Gleis-ID lösen: " + e.Message); }
			s_applied.Remove(key);
		}

		/// <summary>ID löschen: aus den Listen nehmen und die ursprüngliche (generische) ID zurückgeben.</summary>
		public static void Reset(string key)
		{
			Unapply(key);
			if (!s_original.TryGetValue(key, out var o)) return;
			try { SetTrackId(o.logic, o.id); } catch (Exception e) { Main.Log("Gleis-ID zurücksetzen: " + e.Message); }
			s_original.Remove(key);
		}
	}
}

namespace TrackPlacer.World
{
	/// <summary>Verweis auf ein Gleis: eigenes (aus der JSON) oder Spielgleis.</summary>
	internal sealed class TrackRef
	{
		public PlacedTrack? own;
		public RailTrack? vanilla;

		public string Key => own != null ? own.id : StationLink.VanillaKey(vanilla != null ? vanilla.name : "");
		public RailTrack? Rail => own != null ? (TrackBuilder.Built.TryGetValue(own.id, out RailTrack r) ? r : null) : vanilla;
		public VanillaTrackId? VanillaEntry => vanilla == null ? null : LayoutStore.Layout.vanillaTrackIds.Find(x => x.name == vanilla.name);

		/// <summary>Aktuelle Bahnhofs-ID oder "" (generisch).</summary>
		public string CurrentId
		{
			get
			{
				if (own != null) return StationLink.FullId(own);
				var e = VanillaEntry;
				if (e != null) return StationLink.FullId(e);
				var logic = vanilla != null ? StationLink.LogicOf(vanilla) : null;
				if (logic?.ID == null || logic.ID.IsGeneric()) return "";
				return logic.ID.FullDisplayID;
			}
		}

		/// <summary>Darf die ID geändert werden? Eigene immer, Spielgleise nur ohne Bahnhofs-ID.</summary>
		public bool Editable => own != null || (vanilla != null && StationLink.VanillaEditable(vanilla, out _));

		public string Label
		{
			get
			{
				string id = CurrentId;
				string what = own != null ? $"Gleis {own.length:0} m" : "Spielgleis";
				return what + "\n" + (id == "" ? "ohne ID" : id);
			}
		}

		public bool Same(TrackRef? o) => o != null && o.own == own && o.vanilla == vanilla;
	}
}
