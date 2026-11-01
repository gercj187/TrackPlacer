using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using TrackPlacer.Data;
using TrackPlacer.World;
using UnityEngine;
using UnityModManagerNet;

namespace TrackPlacer
{
	/// <summary>Nur die zwei Einstellungen, die der gemeinsame Kern benötigt.</summary>
	public sealed class RuntimeSettings
	{
		public bool editMode = false;
		public string coverMaterial = "";
	}

	/// <summary>
	/// Track Placer Play: lädt alle installierten Szenen-Mods (exportierte Umbauten, nur Daten) und baut sie in die
	/// Welt. Kein Funkgerät-Menü, keine Editor-Funktionen. Szenen verschiedener Autoren werden zusammengeführt.
	/// Eine Szenen-Mod erkennt Play an der Datei map_manifest.json (format "TrackPlacerMap") in ihrem Mod-Ordner.
	/// </summary>
	public static class Main
	{
		public static UnityModManager.ModEntry Mod = null!;
		public static RuntimeSettings Settings = new RuntimeSettings();
		private static PlayBootstrap? s_bootstrap;
		private static readonly List<(string id, string name, string author, int tracks, int clones)> s_scenes = new List<(string, string, string, int, int)>();
		private static string s_status = "";
		private const string EditorId = "TrackPlacer";

		public static void Log(string message) => Mod?.Logger.Log(message);

		public static bool Load(UnityModManager.ModEntry modEntry)
		{
			Mod = modEntry;
			modEntry.OnGUI = OnGUI;

			// Editor aktiv → Play bleibt passiv, sonst würden Editor und Szenen dieselben Gleise doppelt eintragen
			var editor = UnityModManager.FindMod(EditorId);
			if (editor != null && editor.Enabled)
			{
				s_status = Tr.T("Track Placer Editor ist aktiv – Szenen werden nicht geladen (Editor hat Vorrang). Zum Spielen den Editor deaktivieren.", "Track Placer Editor is active – scenes are not loaded (the editor takes precedence). Disable the editor to play.");
				Log(s_status);
				return true;
			}

			if (s_bootstrap == null)
			{
				MeshMaterialPatch.Apply();
				SaveLoadPatch.Apply();
				VanillaRemover.PatchGame();
				ObjectDisabler.StartRunner();
				var go = new GameObject("[TrackPlacer Play] Bootstrap");
				UnityEngine.Object.DontDestroyOnLoad(go);
				s_bootstrap = go.AddComponent<PlayBootstrap>();
			}
			return true;
		}

		/// <summary>
		/// Alle aktiven Szenen-Mods einsammeln und zusammenführen (nach dem Laden aller Mods, vor der ersten Spielwelt).
		/// Reihenfolge fest nach Mod-Id – die Gleisreihenfolge bestimmt die im Spielstand gespeicherten Gleis-Indizes.
		/// </summary>
		internal static void LoadAllScenes()
		{
			s_scenes.Clear();
			var found = new List<(string id, string path, string name, string author)>();
			foreach (var e in UnityModManager.modEntries)
			{
				if (e == null || !e.Enabled || string.IsNullOrEmpty(e.Path)) continue;
				string manifest = Path.Combine(e.Path, "map_manifest.json");
				string layout = Path.Combine(e.Path, "layout.json");
				if (!File.Exists(manifest) || !File.Exists(layout)) continue;
				try
				{
					var m = JObject.Parse(File.ReadAllText(manifest));
					if ((string?)m["format"] != "TrackPlacerMap") continue;
				}
				catch (Exception ex) { Log($"Szene {e.Info.Id}: map_manifest.json unlesbar ({ex.Message})"); continue; }
				if (!string.IsNullOrEmpty(e.Info.AssemblyName))
				{
					Log($"Szene {e.Info.Id} ist ein alter Export mit eigener DLL – bitte mit dem aktuellen Editor neu exportieren. Übersprungen.");
					continue;
				}
				found.Add((e.Info.Id, layout, e.Info.DisplayName ?? e.Info.Id, e.Info.Author ?? ""));
			}
			found = found.OrderBy(f => f.id, StringComparer.Ordinal).ToList();
			if (found.Count == 0) { s_status = Tr.T("Keine Szenen installiert.", "No scenes installed."); Log(s_status); return; }

			bool first = true;
			foreach (var f in found)
			{
				int t0 = first ? 0 : LayoutStore.Layout.tracks.Count, c0 = first ? 0 : LayoutStore.Layout.clones.Count;
				if (first) { LayoutStore.InitFile(f.path, readOnly: true); first = false; }
				else LayoutStore.MergeFile(f.path);
				s_scenes.Add((f.id, f.name, f.author, LayoutStore.Layout.tracks.Count - t0, LayoutStore.Layout.clones.Count - c0));
			}
			s_status = Tr.T($"{found.Count} Szene(n) geladen: {LayoutStore.Layout.tracks.Count} Gleise, {LayoutStore.Layout.switches.Count} Weichen, {LayoutStore.Layout.clones.Count} Klone, {LayoutStore.Layout.removedVanilla.Count} entfernte Originalgleise.", $"{found.Count} scene(s) loaded: {LayoutStore.Layout.tracks.Count} tracks, {LayoutStore.Layout.switches.Count} switches, {LayoutStore.Layout.clones.Count} clones, {LayoutStore.Layout.removedVanilla.Count} removed original tracks.");
			Log(s_status + " (" + string.Join(", ", found.Select(f => f.id)) + ")");
		}

		private static void OnGUI(UnityModManager.ModEntry entry)
		{
			GUILayout.Label(s_status);
			foreach (var s in s_scenes)
				GUILayout.Label(Tr.T($"• {s.name}{(s.author.Length > 0 ? " von " + s.author : "")} – {s.tracks} Gleise, {s.clones} Klone", $"• {s.name}{(s.author.Length > 0 ? " by " + s.author : "")} – {s.tracks} tracks, {s.clones} clones"));
			GUILayout.Label(Tr.T("Szenen werden beim Spielstart geladen; neu installierte Szenen nach einem Neustart.", "Scenes are loaded when the game starts; newly installed scenes after a restart."));
			if (GUILayout.Button(Tr.T("Diagnose: Wagen und ihre Gleise ins Log (Umkreis 300 m)", "Diagnostics: cars and their tracks to log (300 m radius)"), GUILayout.Width(360))) TrackPlacer.World.JobDiagnostics.LogCarsNearPlayer();
			if (GUILayout.Button(Tr.T("Diagnose: feste Collider um mich herum ins Log (12 m)", "Diagnostics: solid colliders around me to log (12 m)"), GUILayout.Width(360))) TrackPlacer.World.JobDiagnostics.LogCollidersAroundPlayer();
		}
	}

	/// <summary>Ersetzt das Funkgerät-Ready-Ereignis: erkennt jede neue Spielwelt und startet einmal den Aufbau.</summary>
	internal sealed class PlayBootstrap : MonoBehaviour
	{
		private int worldId;

		private IEnumerator Start()
		{
			// Ein Frame nach dem Laden aller Mods: jetzt sind alle Szenen-Mods bekannt
			yield return null;
			try { Main.LoadAllScenes(); } catch (Exception e) { Main.Log("Szenen laden fehlgeschlagen: " + e); }
			while (true)
			{
				try
				{
					var registry = GameAccess.Registry;
					if (GameAccess.TracksReady && registry != null && registry.GetInstanceID() != worldId)
					{
						worldId = registry.GetInstanceID();
						TrackBuilder.EnsureWorld();
						new GameObject("[TrackPlacer Play] WorldLoader").AddComponent<WorldLoader>();
						VegetationBuilder.BuildSavedWhenReady();
					}
				}
				catch (Exception e) { Main.Log("Weltstart fehlgeschlagen: " + e); }
				yield return new WaitForSecondsRealtime(0.5f);
			}
		}
	}
}
