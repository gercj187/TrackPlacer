using System;
using CommsRadioAPI;
using TrackPlacer.Data;
using TrackPlacer.Radio;
using TrackPlacer.World;
using UnityEngine;
using UnityModManagerNet;

namespace TrackPlacer
{
	public class Settings : UnityModManager.ModSettings
	{
		public float heightOffset = 0.75f;   // Gleishöhe über dem angeklickten Boden
		public float stepNormal = 0.01f;      // Mausrad-Raste normal (m)
		public float stepShift = 0.10f;      // mit Shift
		public float stepCtrl = 1.00f;       // mit Strg
		public float minRadius = 150f;       // darunter wird der Bogen rot
		public float minLength = 5f;
		public float maxDistance = 300f;     // Laser-Reichweite
		public bool invertScroll = false;
		public float snapRadius = 2.5f;      // Einrasten an freien Gleisenden
		public bool showUnbuiltPreview = true; // nicht gebaute Gleise als blaue Linien
		public bool autoBuildOnLoad = true;
		public bool showInspector = true;    // Debug-Modus im Funkgerät
		public bool allowVanillaRemoval = true;
		/// <summary>true = Bearbeiten (deaktivierte Objekte halbtransparent, ohne Collider), false = Spielmodus (komplett weg).</summary>
		public bool editMode = true;
		// Bilder im Funkgerät-Menü (Größe relativ zum Inhaltsfeld, Versatz in Feldbreiten/-höhen)
		public bool showIcons = true;
		public float iconScale = 0.9f;
		public float iconOffsetX = 0f;
		public float iconOffsetY = 0f;
		public bool iconFlipX = false;
		public string coverMaterial = "";    // Material-Name für die Übergangs-Abdeckung (leer = automatisch, sucht "RailwayCrossing…")
		public float coverUvScale = 1f;
		/// <summary>Sprache: 0 = wie im Spiel, 1 = English, 2 = Deutsch.</summary>
		public int language = 0;

		public override void Save(UnityModManager.ModEntry modEntry) => Save(this, modEntry);
	}

	public static class Main
	{
		public static UnityModManager.ModEntry Mod = null!;
		public static Settings Settings = new Settings();
		private static bool s_registered;
		private static string s_exportName = "Meine Kartenänderung";
		private static string s_exportAuthor = "TrackPlacerExporter";
		private static string s_exportStatus = "";

		public static void Log(string msg) => Mod?.Logger.Log(msg);

		// Kein Harmony nötig: alles läuft über die CommsRadioAPI.
		public static bool Load(UnityModManager.ModEntry modEntry)
		{
			Mod = modEntry;
			try
			{
				Settings = UnityModManager.ModSettings.Load<Settings>(modEntry) ?? new Settings();
			}
			catch (Exception e)
			{
				Log("Einstellungen konnten nicht geladen werden, nutze Standard: " + e.Message);
				Settings = new Settings();
			}

			Tr.Override = Settings.language;
			MeshMaterialPatch.Apply();
			SaveLoadPatch.Apply();
			VanillaRemover.PatchGame();
			ObjectDisabler.StartRunner();
			LayoutStore.Init(modEntry.Path);
			LayoutStore.Changed += PlacerSession.RebuildSaved;

			modEntry.OnGUI = OnGUI;
			modEntry.OnSaveGUI = e => Settings.Save(e);

			if (!s_registered)
			{
				// Wird bei jedem Laden der Spielwelt ausgelöst, sobald das Funkgerät bereit ist
				ControllerAPI.Ready += OnRadioReady;
				s_registered = true;
			}
			return true;
		}

		private static void OnRadioReady()
		{
			try
			{
				TrackBuilder.EnsureWorld(); // nur bei neuer Welt zurücksetzen (früher Aufbau vor dem Wagen-Laden bleibt erhalten)
				RadioIcon.OnWorldUnloaded();
				// Ein einziger Modus „3D Editor“ mit Menü (Gleisbau, Weichen, Gleisobjekte, Entfernen, Objekte)
				CommsRadioMode.Create(new EditorRootState(), laserColor: PlacerSession.ColorCursor);
				if (Settings.showInspector)
					CommsRadioMode.Create(new InspectState(), laserColor: Color.cyan);
				PlacerSession.OnWorldReady();
				Log("Funkgerät-Modus „3D Editor“ registriert.");

				if (Settings.autoBuildOnLoad)
					new GameObject("[TrackPlacer] Loader").AddComponent<WorldLoader>();
				// Vegetation Studio wird später als die Gleis-Registry initialisiert. Masken deshalb gezielt
				// nach dessen InitDone registrieren, nicht über einen dauerhaft laufenden Zwei-Sekunden-Scan.
				VegetationBuilder.BuildSavedWhenReady();
			}
			catch (Exception e)
			{
				Log("Funkgerät-Modus konnte nicht erstellt werden: " + e);
			}
		}

		// ---------------- Mod-Settings-Fenster ----------------

		private static void OnGUI(UnityModManager.ModEntry modEntry)
		{
			GUILayout.BeginHorizontal();
			GUILayout.Label(Tr.T("Sprache / Language:", "Language / Sprache:"), GUILayout.Width(160));
			int lang = GUILayout.Toolbar(Settings.language, new[] { Tr.T("Wie im Spiel", "Game setting"), "English", "Deutsch" }, GUILayout.Width(330));
			if (lang != Settings.language) { Settings.language = lang; Tr.Override = lang; }
			GUILayout.EndHorizontal();
			GUILayout.Label(Tr.T("<b>Gleisbau über das Funkgerät</b>", "<b>Track building with the comms radio</b>"));
			GUILayout.Label(Tr.T("Funkgerät → „Gleisbau“ → A setzen → B setzen → Mausrad biegen → Klick → BAUEN.  Grüner Cursor = rastet an freiem Gleisende ein.", "Radio → 'Track building' → set A → set B → bend with the wheel → click → BUILD.  Green cursor = snaps to a free track end."));

			Settings.heightOffset = FloatField(Tr.T("Gleishöhe über Boden (m)", "Track height above ground (m)"), Settings.heightOffset);
			Settings.stepNormal = FloatField(Tr.T("Mausrad-Schritt normal (m)", "Wheel step normal (m)"), Settings.stepNormal);
			Settings.stepShift = FloatField(Tr.T("Mausrad-Schritt mit Shift (m)", "Wheel step with Shift (m)"), Settings.stepShift);
			Settings.stepCtrl = FloatField(Tr.T("Mausrad-Schritt mit Strg (m)", "Wheel step with Ctrl (m)"), Settings.stepCtrl);
			Settings.minRadius = FloatField(Tr.T("Warnung unter Radius (m)", "Warn below radius (m)"), Settings.minRadius);
			Settings.minLength = FloatField(Tr.T("Mindestlänge (m)", "Minimum length (m)"), Settings.minLength);
			Settings.maxDistance = FloatField(Tr.T("Laser-Reichweite (m)", "Laser range (m)"), Settings.maxDistance);
			Settings.snapRadius = FloatField(Tr.T("Einrasten an Gleisenden bis (m)", "Snap to track ends up to (m)"), Settings.snapRadius);
			Settings.invertScroll = GUILayout.Toggle(Settings.invertScroll, Tr.T(" Mausrad umkehren", " Invert mouse wheel"));
			Settings.autoBuildOnLoad = GUILayout.Toggle(Settings.autoBuildOnLoad, Tr.T(" Gleise beim Laden automatisch bauen", " Build tracks automatically on load"));
			GUILayout.BeginHorizontal();
			GUILayout.Label(Tr.T("Abdeckung: Material-Name (leer = auto)", "Cover: material name (empty = auto)"), GUILayout.Width(260));
			Settings.coverMaterial = GUILayout.TextField(Settings.coverMaterial ?? "", GUILayout.Width(220));
			GUILayout.EndHorizontal();
			if (GUILayout.Button(Tr.T("Abdeckungen neu erzeugen (cover_uv.json neu lesen)", "Rebuild covers (re-read cover_uv.json)"), GUILayout.Width(360))) OverlayBuilder.ReloadUv();
			if (GUILayout.Button(Tr.T("Diagnose: Wagen und ihre Gleise ins Log (Umkreis 300 m)", "Diagnostics: cars and their tracks to log (300 m radius)"), GUILayout.Width(360))) TrackPlacer.World.JobDiagnostics.LogCarsNearPlayer();
			if (GUILayout.Button(Tr.T("Diagnose: feste Collider um mich herum ins Log (12 m)", "Diagnostics: solid colliders around me to log (12 m)"), GUILayout.Width(360))) TrackPlacer.World.JobDiagnostics.LogCollidersAroundPlayer();
			if (GUILayout.Button(Tr.T("Material-Kandidaten ins Log", "Material candidates to log"), GUILayout.Width(220))) OverlayBuilder.LogMaterialCandidates();
			GUILayout.Label(Tr.T("<b>Funkgerät-Bilder</b>", "<b>Radio pictures</b>"));
			Settings.showIcons = GUILayout.Toggle(Settings.showIcons, Tr.T(" Bilder im Menü anzeigen (aus = Textliste)", " Show pictures in the menu (off = text list)"));
			Settings.iconScale = FloatField(Tr.T("Bildgröße (Anteil des Displays)", "Picture size (share of the display)"), Settings.iconScale);
			Settings.iconOffsetX = FloatField(Tr.T("Bild-Versatz seitlich", "Picture offset sideways"), Settings.iconOffsetX);
			Settings.iconOffsetY = FloatField(Tr.T("Bild-Versatz Höhe", "Picture offset height"), Settings.iconOffsetY);
			bool flip = GUILayout.Toggle(Settings.iconFlipX, Tr.T(" Bild spiegeln (falls seitenverkehrt)", " Mirror picture (if reversed)"));
			if (flip != Settings.iconFlipX) Settings.iconFlipX = flip;
			if (GUILayout.Button(Tr.T("Bild-Lage übernehmen", "Apply picture position"), GUILayout.Width(200))) RadioIcon.Layout();
			bool edit = GUILayout.Toggle(Settings.editMode, Tr.T(" Bearbeitungsmodus: deaktivierte Objekte halbtransparent (aus = Spielmodus: komplett ausgeblendet)", " Edit mode: disabled objects semi-transparent (off = play mode: fully hidden)"));
			if (edit != Settings.editMode) { Settings.editMode = edit; ObjectDisabler.ModeChanged(); }
			if (GUILayout.Button(Tr.T($"Alle deaktivierten Objekte wieder aktivieren ({LayoutStore.Layout.disabledObjects.Count})", $"Re-enable all disabled objects ({LayoutStore.Layout.disabledObjects.Count})"), GUILayout.Width(360)))
				ObjectDisabler.EnableAll();
			Settings.allowVanillaRemoval = GUILayout.Toggle(Settings.allowVanillaRemoval, Tr.T(" Spielgleise mit „Entfernen“ entfernen erlauben", " Allow removing game tracks with 'Remove'"));
			Settings.showInspector = GUILayout.Toggle(Settings.showInspector, Tr.T(" Inspektor-Modus im Funkgerät (Debug, wirkt nach Neuladen)", " Inspector mode in the radio (debug, applies after reload)"));

			bool show = GUILayout.Toggle(Settings.showUnbuiltPreview, Tr.T(" Nicht gebaute Gleise anzeigen (blau)", " Show unbuilt tracks (blue)"));
			if (show != Settings.showUnbuiltPreview)
			{
				Settings.showUnbuiltPreview = show;
				PlacerSession.RebuildSaved();
			}

			GUILayout.Space(8);
			var L = LayoutStore.Layout;
			GUILayout.Label(Tr.T($"Gleise: {L.tracks.Count} (gebaut {TrackBuilder.Built.Count})   Weichen: {L.switches.Count} (gebaut {JunctionBuilder.Built.Count})   Prellböcke: {L.buffers.Count} (gebaut {BufferBuilder.Built.Count})", $"Tracks: {L.tracks.Count} (built {TrackBuilder.Built.Count})   Switches: {L.switches.Count} (built {JunctionBuilder.Built.Count})   Buffer stops: {L.buffers.Count} (built {BufferBuilder.Built.Count})"));
			GUILayout.Label(Tr.T($"Datei: {LayoutStore.FilePath}", $"File: {LayoutStore.FilePath}"));

			GUILayout.Space(10);
			GUILayout.Label(Tr.T("<b>Szene exportieren</b>", "<b>Export scene</b>"));
			GUILayout.Label(Tr.T("Enthält entfernte/geänderte Originalteile, Klone, Pitstops, Vegetationsflächen und alle verlegten Gleise. Die Szene enthält nur Daten und braucht zum Spielen die Mod „Track Placer Play“.", "Contains removed/changed original parts, clones, pitstops, vegetation areas and all laid tracks. The scene contains data only and needs the mod 'Track Placer Play' to be played."));
			GUILayout.BeginHorizontal();
			GUILayout.Label(Tr.T("Autor", "Author"), GUILayout.Width(260));
			s_exportAuthor = GUILayout.TextField(s_exportAuthor ?? "", GUILayout.Width(260));
			GUILayout.EndHorizontal();
			GUILayout.BeginHorizontal();
			GUILayout.Label(Tr.T("Name der Szene/ZIP-Datei", "Name of the scene/ZIP file"), GUILayout.Width(260));
			s_exportName = GUILayout.TextField(s_exportName ?? "", GUILayout.Width(260));
			if (GUILayout.Button(Tr.T("EXPORTIEREN", "EXPORT"), GUILayout.Width(150)))
			{
				if (LayoutExporter.Export(s_exportName, s_exportAuthor, modEntry.Path, out string path, out string error))
				{
					s_exportStatus = Tr.T("Export erstellt: ", "Export created: ") + path;
					Log(s_exportStatus);
				}
				else
				{
					s_exportStatus = Tr.T("Export fehlgeschlagen: ", "Export failed: ") + error;
					Log(s_exportStatus);
				}
			}
			GUILayout.EndHorizontal();
			if (!string.IsNullOrEmpty(s_exportStatus)) GUILayout.Label(s_exportStatus);

			GUILayout.BeginHorizontal();
			if (GUILayout.Button(Tr.T("Alle bauen", "Build all"), GUILayout.Width(200)))
			{
				TrackBuilder.BuildAll(out string msg);
				PlacerSession.RebuildSaved();
				if (StationLink.StationsReady)
					msg += Tr.T($", {VanillaTrackEditor.ApplyAll()} Spielgleise geändert, {VanillaSwitchEditor.ApplyAll()} Spielweichen geändert, {StationLink.ApplyAll()} Gleis-IDs, {MachineBuilder.BuildAll()} Lademaschinen, {MachineBuilder.ApplyVanillaEdits()} Spielmaschinen, {SignBuilder.BuildAll()} Schilder", $", {VanillaTrackEditor.ApplyAll()} game tracks changed, {VanillaSwitchEditor.ApplyAll()} game switches changed, {StationLink.ApplyAll()} track IDs, {MachineBuilder.BuildAll()} loading machines, {MachineBuilder.ApplyVanillaEdits()} game machines, {SignBuilder.BuildAll()} signs");
				Log(Tr.T("Alle bauen: ", "Build all: ") + msg);
			}
			if (GUILayout.Button(Tr.T("Letztes Gleis löschen", "Delete last track"), GUILayout.Width(200)))
			{
				var last = LayoutStore.Layout.tracks.Count > 0 ? LayoutStore.Layout.tracks[LayoutStore.Layout.tracks.Count - 1] : null;
				if (last != null)
				{
					if (TrackBuilder.Remove(last.id, out string err)) LayoutStore.Remove(last.id);
					else Log(Tr.T("Nicht entfernt: ", "Not removed: ") + err);
				}
			}
			if (GUILayout.Button(Tr.T($"Entfernte Spielgleise zurück ({LayoutStore.Layout.removedVanilla.Count})", $"Restore removed game tracks ({LayoutStore.Layout.removedVanilla.Count})"), GUILayout.Width(260)))
			{
				LayoutStore.ClearRemovedVanilla();
				Log(Tr.T("Liste entfernter Spielgleise geleert – wirksam nach dem Neuladen der Welt.", "List of removed game tracks cleared – takes effect after reloading the world."));
			}
			if (GUILayout.Button(Tr.T("Datei neu laden", "Reload file"), GUILayout.Width(200)))
				LayoutStore.Load();
			GUILayout.EndHorizontal();
		}

		// Eingabepuffer, damit man "0," tippen kann, ohne dass das Feld sofort umformatiert wird
		private static readonly System.Collections.Generic.Dictionary<string, string> s_buffers =
			new System.Collections.Generic.Dictionary<string, string>();

		private static float FloatField(string label, float value)
		{
			var inv = System.Globalization.CultureInfo.InvariantCulture;
			if (!s_buffers.TryGetValue(label, out string? text))
				text = value.ToString("0.###", inv);

			GUILayout.BeginHorizontal();
			GUILayout.Label(label, GUILayout.Width(260));
			text = GUILayout.TextField(text, GUILayout.Width(100));
			GUILayout.EndHorizontal();
			s_buffers[label] = text;

			return float.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float, inv, out float v) ? v : value;
		}
	}
}
