using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace TrackPlacer
{
	/// <summary>
	/// Zweisprachige Texte: Englisch (Standard) oder Deutsch, je nach Spracheinstellung im Spiel.
	/// Aufruf: Tr.T("Deutscher Text", "English text"). Die Spielsprache wird per Reflection gelesen
	/// (DV.Localization / I2 Localization) und kurz zwischengespeichert, damit ein Wechsel im Spiel greift.
	/// Log-Ausgaben bleiben deutsch.
	/// </summary>
	internal static class Tr
	{
		/// <summary>Manuelle Vorgabe aus den Mod-Einstellungen: 0 = automatisch, 1 = English, 2 = Deutsch.</summary>
		public static int Override { get; set; }

		private static bool s_german;
		private static float s_checked = -999f;
		private static Func<string?>? s_reader;
		private static bool s_searched, s_logged;

		public static bool German
		{
			get
			{
				if (Override == 1) return false;
				if (Override == 2) return true;
				float now = Time.realtimeSinceStartup;
				if (now - s_checked > 2f)
				{
					s_checked = now;
					string? lang = null;
					try { lang = Reader?.Invoke(); } catch { /* egal */ }
					s_german = IsGerman(lang);
					if (!s_logged && lang != null) { s_logged = true; Main.Log($"Spielsprache erkannt: {lang} → {(s_german ? "Deutsch" : "English")}"); }
				}
				return s_german;
			}
		}

		public static string T(string de, string en) => German ? de : en;

		/// <summary>
		/// Schaltflächen-Text des Funkgeräts. Intern bleiben die deutschen Wörter Schlüssel (Optionslisten, switch/case),
		/// übersetzt wird erst bei der Anzeige.
		/// </summary>
		public static string Act(string de) => German || de == null ? de! : (Actions.TryGetValue(de, out var en) ? en : de);

		private static readonly System.Collections.Generic.Dictionary<string, string> Actions = new System.Collections.Generic.Dictionary<string, string>
		{
			{ "START", "START" },
			{ "BEARBEITEN", "EDIT" },
			{ "WEITER", "NEXT" },
			{ "ZIELEN", "AIM" },
			{ "DREHEN", "ROTATE" },
			{ "HÖHE", "HEIGHT" },
			{ "GRÖSSE", "SIZE" },
			{ "MAST", "POLE" },
			{ "VERSCHIEBEN", "MOVE" },
			{ "ENTFERNEN", "REMOVE" },
			{ "FERTIG", "DONE" },
			{ "MAST EINBLENDEN", "SHOW POLE" },
			{ "MAST AUSBLENDEN", "HIDE POLE" },
			{ "ABSETZEN", "DROP" },
			{ "SETZEN", "PLACE" },
			{ "KOPIEREN", "COPY" },
			{ "COLLIDER", "COLLIDER" },
			{ "ZURÜCKSETZEN", "RESET" },
			{ "AUSWÄHLEN", "SELECT" },
			{ "ALLE WIEDER AN", "ALL BACK ON" },
			{ "NACHBARN", "NEIGHBOURS" },
			{ "ZURÜCK", "BACK" },
			{ "NACHBAR-COLLIDER AN", "NEIGHBOUR COLLIDERS ON" },
			{ "NACHBAR-COLLIDER AUS", "NEIGHBOUR COLLIDERS OFF" },
			{ "AUSSCHALTEN", "SWITCH OFF" },
			{ "MODUL AN", "MODULE ON" },
			{ "MODUL AUS", "MODULE OFF" },
			{ "MODUL DREHEN", "ROTATE MODULE" },
			{ "MODUL HÖHE", "MODULE HEIGHT" },
			{ "MODUL VERSCHIEBEN", "MOVE MODULE" },
			{ "PLATZIEREN", "PLACE" },
			{ "PRÜFEN", "CHECK" },
			{ "MENÜ", "MENU" },
			{ "ÖFFNEN", "OPEN" },
			{ "GLEISTYP", "TRACK TYPE" },
			{ "BEWUCHS ZULASSEN", "ALLOW GROWTH" },
			{ "FREISCHNEIDEN", "CLEAR VEGETATION" },
			{ "ÜBERNEHMEN", "APPLY" },
			{ "ÄNDERN", "CHANGE" },
			{ "ALTER", "AGE" },
			{ "ABDECKUNG", "COVER" },
			{ "HEBEL WECHSELN", "SWAP LEVER" },
			{ "AKTIVIEREN", "ENABLE" },
			{ "DEAKTIVIEREN", "DISABLE" },
			{ "A SETZEN", "SET A" },
			{ "B SETZEN", "SET B" },
			{ "BAUEN", "BUILD" },
			{ "BIEGEN", "BEND" },
			{ "VERWERFEN", "DISCARD" },
			{ "ABBRECHEN", "CANCEL" },
			{ "SPEICHERN", "SAVE" },
			{ "ID LÖSCHEN", "DELETE ID" },
			{ "LADUNG", "CARGO" },
			{ "GLEIS ZUWEISEN", "ASSIGN TRACK" },
			{ "HINZUFÜGEN", "ADD" },
			{ "ZUWEISEN", "ASSIGN" },
			{ "EXPORTIEREN", "EXPORT" },
		};

		private static bool IsGerman(string? lang)
		{
			if (string.IsNullOrEmpty(lang)) return false;
			string l = lang!.Trim().ToLowerInvariant();
			return l == "de" || l.StartsWith("de-") || l.StartsWith("de_") || l.Contains("german") || l.Contains("deutsch");
		}

		/// <summary>Sucht einmalig eine Quelle für die aktuelle Spielsprache.</summary>
		private static Func<string?>? Reader
		{
			get
			{
				if (s_searched) return s_reader;
				s_searched = true;
				const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
				foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
				{
					// 1. I2 Localization (Sprachcode, z. B. "de")
					Type? i2 = null;
					try { i2 = asm.GetType("I2.Loc.LocalizationManager", false); } catch { /* egal */ }
					if (i2 != null)
					{
						var p = i2.GetProperty("CurrentLanguageCode", S) ?? i2.GetProperty("CurrentLanguage", S);
						if (p != null) { s_reader = () => p.GetValue(null)?.ToString(); return s_reader; }
					}
				}
				foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
				{
					// 2. Derail Valley: statische Sprach-Eigenschaft im Namespace DV.Localization
					Type[] types;
					try { types = asm.GetTypes(); } catch { continue; }
					foreach (var t in types.Where(x => x.Namespace != null && x.Namespace.StartsWith("DV.Localization")))
					{
						var p = t.GetProperties(S).FirstOrDefault(x => x.Name.IndexOf("Language", StringComparison.OrdinalIgnoreCase) >= 0 && x.GetIndexParameters().Length == 0);
						if (p != null) { s_reader = () => p.GetValue(null)?.ToString(); return s_reader; }
						var f = t.GetFields(S).FirstOrDefault(x => x.Name.IndexOf("Language", StringComparison.OrdinalIgnoreCase) >= 0 && !x.IsLiteral);
						if (f != null) { s_reader = () => f.GetValue(null)?.ToString(); return s_reader; }
					}
				}
				Main.Log("Spielsprache nicht ermittelbar – Englisch (in den Mod-Einstellungen umstellbar).");
				return null;
			}
		}
	}
}
