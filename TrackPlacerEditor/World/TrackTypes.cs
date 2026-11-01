using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TrackPlacer.World
{
	internal enum TrackStyle { Ballast, Sleepers, Rails }

	/// <summary>Ein Eintrag der Gleistyp-Auswahl: Alter (Material-Satz des Spiels) × Bauart, optional mit Abdeckung.</summary>
	internal sealed class TrackTypeOption
	{
		public RailType rail = null!;
		public BaseType baseType = null!;
		public float age;
		public string ageLabel = "";
		public TrackStyle style;
		public bool cover;

		public string RailName => rail != null ? rail.name : "?";
		public string BaseName => baseType != null ? baseType.name : "?";

		/// <summary>Alter übersetzt (intern bleibt „neu/mittel/alt“).</summary>
		private string Age => ageLabel switch { "neu" => Tr.T("neu", "new"), "mittel" => Tr.T("mittel", "medium"), "alt" => Tr.T("alt", "old"), _ => ageLabel };

		public string Label => (cover ? $"Asphalt {Age}"
			: style switch
			{
				TrackStyle.Sleepers => Tr.T($"Schwellen {Age}", $"Sleepers {Age}"),
				TrackStyle.Rails => Tr.T($"Nur Schienen {Age}", $"Rails only {Age}"),
				_ => Tr.T($"Schotter {Age}", $"Ballast {Age}"),
			}).Trim();

		public string Detail => cover ? Tr.T($"Schotter {Age} + Asphalt-Abdeckung", $"Ballast {Age} + asphalt cover")
			: style switch
			{
				TrackStyle.Sleepers => Tr.T("ohne Schotter", "without ballast"),
				TrackStyle.Rails => Tr.T("ohne Schotter/Schwellen", "without ballast/sleepers"),
				_ => Tr.T("komplett", "complete"),
			};

		public string StyleId => style switch { TrackStyle.Sleepers => "sleepers", TrackStyle.Rails => "rails", _ => "ballast" };

		public static TrackStyle ParseStyle(string s) => s == "sleepers" ? TrackStyle.Sleepers : s == "rails" ? TrackStyle.Rails : TrackStyle.Ballast;
	}

	/// <summary>
	/// Gleistypen aus den Material-Sätzen des Spiels (Globals.GetRailType: neu / mittel / alt) in drei Bauarten:
	/// Schotter (komplett), Schwellen (ohne Schotter), Nur Schienen (ohne Schotter und Schwellen), plus Bahnübergang.
	/// </summary>
	internal static class TrackTypes
	{
		private static List<TrackTypeOption>? s_options;
		public static int Selected; // Index der Auswahl im Funkgerät (bleibt zwischen Gleisen erhalten)

		public static void Reset() => s_options = null;

		public static IReadOnlyList<TrackTypeOption> All
		{
			get
			{
				if (s_options != null) return s_options;
				var list = new List<TrackTypeOption>();
				if (!GameAccess.TracksReady) return list;

				// Material-Sätze nach Alter abfragen, Duplikate entfernen
				var sets = new List<DV.Globals.TrackType>();
				try
				{
					foreach (float a in new[] { 0f, 25f, 50f, 75f, 100f })
					{
						DV.Globals.TrackType tt = DV.Globals.G.GetRailType(a);
						if (tt != null && tt.railType != null && tt.baseType != null && !sets.Any(x => x.railType == tt.railType && x.baseType == tt.baseType))
							sets.Add(tt);
					}
				}
				catch (System.Exception e) { Main.Log("Gleis-Material-Sätze nicht lesbar: " + e.Message); }
				sets = sets.OrderBy(t => t.age).ToList();

				if (sets.Count == 0)
				{
					// Notlösung: häufigstes Gleis der Welt
					var t = GameAccess.Registry?.OrderedRailtracks.FirstOrDefault(x => x != null && !x.isJunctionTrack && x.railType != null && x.baseType != null);
					if (t == null) return list;
					sets.Add(new DV.Globals.TrackType { age = t.age, railType = t.railType, baseType = t.baseType });
				}

				string AgeLabel(int i) => sets.Count == 1 ? "" : i == 0 ? "neu" : i == sets.Count - 1 ? "alt" : "mittel";

				foreach (TrackStyle style in new[] { TrackStyle.Ballast, TrackStyle.Sleepers, TrackStyle.Rails })
					for (int i = 0; i < sets.Count; i++)
						list.Add(new TrackTypeOption { rail = sets[i].railType, baseType = sets[i].baseType, age = sets[i].age, ageLabel = AgeLabel(i), style = style });

				// Asphalt für jeden vorhandenen Materialsatz anbieten, damit auch das alte Gleis darunter auswählbar ist.
				for (int i = 0; i < sets.Count; i++)
					list.Add(new TrackTypeOption { rail = sets[i].railType, baseType = sets[i].baseType, age = sets[i].age, ageLabel = AgeLabel(i), style = TrackStyle.Ballast, cover = true });

				foreach (var o in list) Main.Log($"Gleistyp: {o.Label} ({o.Detail}) | rail={o.RailName} base={o.BaseName} age={o.age}");
				s_options = list;
				return list;
			}
		}

		public static int OptionCount => All.Count;

		public static TrackTypeOption? Current
		{
			get
			{
				var all = All;
				if (all.Count == 0) return null;
				if (Selected < 0 || Selected >= all.Count) Selected = 0;
				return all[Selected];
			}
		}

		public static bool CoverSelected => Current?.cover == true;
		public static string CurrentLabel => Current?.Label ?? Tr.T("Gleis", "Track");

		/// <summary>Typ eines gespeicherten Gleises finden. Neue Einträge: Alter + Bauart, alte Einträge: Asset-Namen.</summary>
		public static TrackTypeOption? Resolve(string railName, string baseName, string style, float age)
		{
			var all = All;
			if (all.Count == 0) return null;
			var st = TrackTypeOption.ParseStyle(style);
			var candidates = all.Where(o => !o.cover && o.style == st).ToList();
			if (candidates.Count == 0) return all[0];

			if (age >= 0f) return candidates.OrderBy(o => Mathf.Abs(o.age - age)).First();
			var byName = candidates.FirstOrDefault(o => o.RailName == railName && o.BaseName == baseName)
			             ?? candidates.FirstOrDefault(o => o.BaseName == baseName);
			return byName ?? candidates[0];
		}
	}
}
