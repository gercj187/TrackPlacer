using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using TrackPlacer.Geometry;
using UnityEngine;

namespace TrackPlacer.Data
{
	/// <summary>Speicherformat. Vektoren als float[3], weil Newtonsoft Unitys Vector3 nicht sauber serialisiert.
	/// Alle Koordinaten sind statisch (Welt minus WorldMover.currentMove).</summary>
	public class LayoutFile
	{
		public int version = 3;
		public List<PlacedTrack> tracks = new List<PlacedTrack>();
		public List<PlacedSwitch> switches = new List<PlacedSwitch>();
		public List<PlacedBuffer> buffers = new List<PlacedBuffer>();
		public List<PlacedCap> caps = new List<PlacedCap>();
		public List<RemovedVanilla> removedVanilla = new List<RemovedVanilla>();
		public List<PlacedMachine> machines = new List<PlacedMachine>();
		public List<VanillaTrackId> vanillaTrackIds = new List<VanillaTrackId>();
		public List<VanillaTrackEdit> vanillaTrackEdits = new List<VanillaTrackEdit>();
		public List<VanillaSwitchEdit> vanillaSwitchEdits = new List<VanillaSwitchEdit>();
		public List<VanillaMachineEdit> vanillaMachines = new List<VanillaMachineEdit>();
		public List<DisabledObject> disabledObjects = new List<DisabledObject>();
		public List<PlacedSign> signs = new List<PlacedSign>();
		public List<PlacedClone> clones = new List<PlacedClone>();
		public List<PitstopModuleEdit> pitstopModules = new List<PitstopModuleEdit>();
		public List<PlacedVegetationMask> vegetationMasks = new List<PlacedVegetationMask>();
	}

	/// <summary>Fläche ohne Vegetation (Gras, Büsche, Bäume) – Quadrat um die Mitte, gedreht um yaw.</summary>
	public class PlacedVegetationMask
	{
		public string id = Guid.NewGuid().ToString("N");
		public float[] position = new float[3];
		public float radius = 3f;     // halbe Kantenlänge (m)
		public float yaw;
		[JsonIgnore] public Vector3 Position => PlacedTrack.ToVec(position);
	}

	/// <summary>Gleisschild (Klon eines Spiel-Schilds), Text aus der Gleis-ID.</summary>
	public class PlacedSign
	{
		public string id = Guid.NewGuid().ToString("N");
		public float[] position = new float[3];
		public float yaw;
		public string trackKey = "";   // eigenes Gleis (id) oder "v:<Name>" – zum Aktualisieren des Textes
		public string sub = "";        // z. B. "B"
		public string track = "";      // z. B. "7S"
		public float height;           // zusätzliche Höhe der Tafel (m), 0 = Standard
		public float scale = 1f;       // Größe der Tafel (1 = wie im Spiel, nur kleiner möglich)
		public bool hidePole;          // eigenen Mast ausblenden (z. B. Schild an Wand/Mast montiert)
		[JsonIgnore] public Vector3 Position => PlacedTrack.ToVec(position);
	}

	/// <summary>Klon eines Szenenobjekts: Quelle (zum Wiederfinden) + eigene Lage.</summary>
	public class PlacedClone
	{
		public string id = Guid.NewGuid().ToString("N");
		public string srcParentPath = "";
		public string srcName = "";
		public float[] srcPosition = new float[3];
		public float[] position = new float[3];   // Pivot des Klons (statisch)
		public float yaw;                          // Drehung um die Hochachse relativ zur Quelle
		public bool keepVegetation;                // veraltet (unter Klonen wird keine Vegetation mehr entfernt) – nur für alte Dateien
		public bool replacesSource;                // Quelle ausblenden (verschobener Spiel-Pitstop); Entfernen = Original zurück
		public bool noCompanions;                  // benachbarte Collider-Gruppen der Quelle NICHT mitkopieren
		public List<string> disabledColliders = new List<string>(); // ausgeschaltete Collider im Klon: "Pfad#Index"
		[JsonIgnore] public Vector3 SrcPosition => PlacedTrack.ToVec(srcPosition);
		[JsonIgnore] public Vector3 Position => PlacedTrack.ToVec(position);
	}

	/// <summary>Abgeschaltetes Service-Modul eines Pitstops (Diesel, Kohle, Reparatur …).</summary>
	public class PitstopModuleEdit
	{
		public string id = Guid.NewGuid().ToString("N");
		public string parentPath = "";
		public string name = "";
		public float[] position = new float[3];
		public string resource = "";
		/// <summary>Alte Dateien enthielten ausschließlich abgeschaltete Module; deshalb ist true der kompatible Standard.</summary>
		public bool disabled = true;
		/// <summary>Optionale lokale Pose relativ zum direkten Elternobjekt des Moduls.</summary>
		public bool hasTransform;
		public float[] localPosition = new float[3];
		public float[] localEuler = new float[3];
		[JsonIgnore] public Vector3 Position => PlacedTrack.ToVec(position);
		[JsonIgnore] public Vector3 LocalPosition => PlacedTrack.ToVec(localPosition);
		[JsonIgnore] public Vector3 LocalEuler => PlacedTrack.ToVec(localEuler);
	}

	/// <summary>Deaktiviertes Szenenobjekt: Pfad des Elternobjekts + Name + Position (statisch) zum Wiederfinden.</summary>
	public class DisabledObject
	{
		public string id = Guid.NewGuid().ToString("N");
		public string parentPath = "";
		public string name = "";
		public float[] position = new float[3];
		[JsonIgnore] public Vector3 Position => PlacedTrack.ToVec(position);
	}

	/// <summary>Bahnhofs-ID für ein Spielgleis, das im Spiel keine hat (generisches Gleis).</summary>
	public class VanillaTrackId
	{
		public string name = "";
		public float[] mid = new float[3];
		public string yardId = "";
		public string subYard = "";
		public string number = "";
		public string trackType = "";
		[JsonIgnore] public Vector3 Mid => PlacedTrack.ToVec(mid);
	}

	/// <summary>Geänderte Spielweiche (Alter, Abdeckung), erkannt an Name und Mittelpunkt des geraden Weichengleises.</summary>
	public class VanillaSwitchEdit
	{
		public string id = Guid.NewGuid().ToString("N");
		public string throughName = "";
		public float[] mid = new float[3];
		public float age = -1f;        // −1 = Original
		public bool covered;
		public string origRail = "";
		public string origBase = "";
		public float origAge;
		[JsonIgnore] public bool ageApplied;
		[JsonIgnore] public Vector3 Mid => PlacedTrack.ToVec(mid);
	}

	/// <summary>Persistente Darstellung/Vegetationsmaske eines Originalgleises, erkannt an Name und Mittelpunkt.</summary>
	public class VanillaTrackEdit
	{
		public string id = Guid.NewGuid().ToString("N");
		public string name = "";
		public float[] mid = new float[3];
		public string railType = "";
		public string baseType = "";
		public string style = "ballast";
		public float age = -1f;
		public string overlay = "";
		public bool clearVegetation;
		[JsonIgnore] public Vector3 Mid => PlacedTrack.ToVec(mid);
	}

	/// <summary>Änderung an einer Lademaschine des Spiels, erkannt am Namen ihres Gleises.</summary>
	public class VanillaMachineEdit
	{
		public string trackName = "";
		public bool removed;
		/// <summary>null = Ladeliste des Spiels unverändert.</summary>
		public List<string>? cargo;
	}

	/// <summary>Lademaschine: Klon einer Maschine aus dem Spiel, mit Ladeliste und zugeordnetem Gleis.</summary>
	public class PlacedMachine
	{
		public string id = Guid.NewGuid().ToString("N");
		public float[] position = new float[3];
		public float yaw;
		public string yardId = "";                 // Bahnhof (leer = nächstgelegener)
		public string trackId = "";                // eigenes Gleis (PlacedTrack.id)
		public string vanillaTrack = "";           // oder: Spielgleis (Name)
		public float[] vanillaTrackMid = new float[3];
		public List<string> cargo = new List<string>(); // Namen aus DV.ThingTypes.CargoType
		[JsonIgnore] public Vector3 Position => PlacedTrack.ToVec(position);
		[JsonIgnore] public bool HasTrack => !string.IsNullOrEmpty(trackId) || !string.IsNullOrEmpty(vanillaTrack);
	}

	/// <summary>Entferntes Spielgleis: Name + Mittelpunkt (statisch) zum Wiederfinden beim Laden.</summary>
	public class RemovedVanilla
	{
		public string id = Guid.NewGuid().ToString("N");
		public string name = "";
		public float[] mid = new float[3];
		[JsonIgnore] public Vector3 Mid => PlacedTrack.ToVec(mid);
	}

	public class PlacedTrack
	{
		public string id = Guid.NewGuid().ToString("N");
		public string created = DateTime.UtcNow.ToString("o");
		public float[] a = new float[3];
		public float[] b = new float[3];
		/// <summary>Pfeilhöhe in m, + = Bogen nach links (Blick A→B).</summary>
		public float sagitta;
		/// <summary>Nur zur Info, 0 = Gerade.</summary>
		public float radius;
		public float length;
		/// <summary>Gleistyp (Namen der Spiel-Assets). Leer = häufigster Streckentyp.</summary>
		public string railType = "";
		public string baseType = "";
		/// <summary>Bauart: "ballast" (komplett), "sleepers" (ohne Schotter), "rails" (nur Schienen).</summary>
		public string style = "ballast";
		/// <summary>Alter des Material-Satzes (0 = neu … 100 = alt). -1 = alter Eintrag, über Asset-Namen auflösen.</summary>
		public float age = -1f;
		/// <summary>"" = keine, "crossing" = Bahnübergang/Asphalt-Abdeckung über dem Gleis.</summary>
		public string overlay = "";
		public float overlayWidth;
		/// <summary>Vegetation Studio entlang des Gleises ausblenden (7 m breite Maske).</summary>
		public bool clearVegetation;
		/// <summary>Bahnhofsgleis-ID (leer = generisches Gleis). Beispiel: yard "SM", sub "B", number "07", type "S" → SM-B7S</summary>
		public string yardId = "";
		public string subYard = "";
		public string number = "";
		public string trackType = "";
		public List<PlacedAnchor> anchors = new List<PlacedAnchor>();

		[JsonIgnore] public Vector3 A => ToVec(a);
		[JsonIgnore] public Vector3 B => ToVec(b);

		public List<Anchor> ToAnchors()
		{
			var list = new List<Anchor>(anchors.Count);
			foreach (var p in anchors)
				list.Add(new Anchor { position = ToVec(p.p), handle1 = ToVec(p.h1), handle2 = ToVec(p.h2) });
			return list;
		}

		internal static float[] FromVec(Vector3 v) => new[] { Round(v.x), Round(v.y), Round(v.z) };
		internal static Vector3 ToVec(float[] f) => f != null && f.Length >= 3 ? new Vector3(f[0], f[1], f[2]) : Vector3.zero;
		private static float Round(float f) => (float)Math.Round(f, 4);
	}

	public class PlacedAnchor
	{
		public float[] p = new float[3];
		public float[] h1 = new float[3];
		public float[] h2 = new float[3];
	}

	/// <summary>Weiche: Klon eines Weichen-Prefabs aus dem Spiel. Position = Weichenanfang (Stoß), yaw = Fahrtrichtung dort.</summary>
	public class PlacedSwitch
	{
		public string id = Guid.NewGuid().ToString("N");
		public string prefab = "";
		public float[] position = new float[3];
		public float yaw;
		public int selectedBranch;
		/// <summary>Alter des Material-Satzes für Schienen/Schotter der Weiche (−1 = wie die Vorlage).</summary>
		public float age = -1f;
		/// <summary>Asphalt-/Übergangsabdeckung über die ganze Weiche (wie in Bahnhöfen).</summary>
		public bool covered;
		/// <summary>Angezeigte Weichen-ID wie bei Spielweichen, z. B. „S-9001-HB“ (Bahnhof = nächster Bahnhof). Wird beim
		/// ersten Bau vergeben und dann fest gespeichert.</summary>
		public string switchId = "";

		[JsonIgnore] public Vector3 Position => PlacedTrack.ToVec(position);
	}

	/// <summary>Prellbock am Ende eines Gleises. forward zeigt ins Gleis hinein.</summary>
	/// <summary>Schotterende: schließt das Schotterbett am freien Gleisende (Position = Gleisende, statisch).</summary>
	public class PlacedCap
	{
		public string id = Guid.NewGuid().ToString("N");
		public float[] position = new float[3];
		[JsonIgnore] public Vector3 Position => PlacedTrack.ToVec(position);
	}

	public class PlacedBuffer
	{
		public string id = Guid.NewGuid().ToString("N");
		public float[] position = new float[3];
		public float yaw;

		[JsonIgnore] public Vector3 Position => PlacedTrack.ToVec(position);
	}

	public static class LayoutStore
	{
		public static LayoutFile Layout { get; private set; } = new LayoutFile();
		public static string FilePath { get; private set; } = "";
		public static event Action? Changed;
		private static bool s_readOnly;

		public static void Init(string modPath)
		{
			InitFile(Path.Combine(modPath, "placed_tracks.json"));
		}

		/// <summary>Runtime-Exporte laden eine feste layout.json und verändern das verteilte Paket niemals.</summary>
		public static void InitFile(string filePath, bool readOnly = false)
		{
			FilePath = filePath;
			s_readOnly = readOnly;
			Load();
		}

		public static void Load()
		{
			try
			{
				Layout = File.Exists(FilePath)
					? JsonConvert.DeserializeObject<LayoutFile>(File.ReadAllText(FilePath)) ?? new LayoutFile()
					: new LayoutFile();
				Normalize(Layout);
			}
			catch (Exception e)
			{
				Main.Log($"placed_tracks.json konnte nicht gelesen werden, starte leer: {e.Message}");
				// Kaputte Datei nicht überschreiben, sondern beiseitelegen
				try { File.Copy(FilePath, FilePath + ".broken", true); } catch { /* egal */ }
				Layout = new LayoutFile();
			}
			Changed?.Invoke();
		}

		private static void Normalize(LayoutFile layout)
		{
			layout.tracks ??= new List<PlacedTrack>();
			layout.switches ??= new List<PlacedSwitch>();
			layout.buffers ??= new List<PlacedBuffer>();
			layout.caps ??= new List<PlacedCap>();
			layout.removedVanilla ??= new List<RemovedVanilla>();
			layout.machines ??= new List<PlacedMachine>();
			layout.vanillaTrackIds ??= new List<VanillaTrackId>();
			layout.vanillaTrackEdits ??= new List<VanillaTrackEdit>();
			layout.vanillaSwitchEdits ??= new List<VanillaSwitchEdit>();
			layout.vanillaMachines ??= new List<VanillaMachineEdit>();
			layout.disabledObjects ??= new List<DisabledObject>();
			layout.signs ??= new List<PlacedSign>();
			layout.clones ??= new List<PlacedClone>();
			layout.pitstopModules ??= new List<PitstopModuleEdit>();
			layout.vegetationMasks ??= new List<PlacedVegetationMask>();
		}

		/// <summary>Weitere installierte Kartenexporte in denselben Runtime-Aufbau aufnehmen.</summary>
		public static void MergeFile(string filePath)
		{
			var other = JsonConvert.DeserializeObject<LayoutFile>(File.ReadAllText(filePath)) ?? new LayoutFile();
			Normalize(other);
			Layout.version = Math.Max(Layout.version, other.version);
			Layout.tracks.AddRange(other.tracks);
			Layout.switches.AddRange(other.switches);
			Layout.buffers.AddRange(other.buffers);
			Layout.caps.AddRange(other.caps);
			Layout.removedVanilla.AddRange(other.removedVanilla);
			Layout.machines.AddRange(other.machines);
			Layout.vanillaTrackIds.AddRange(other.vanillaTrackIds);
			Layout.vanillaTrackEdits.AddRange(other.vanillaTrackEdits);
			Layout.vanillaSwitchEdits.AddRange(other.vanillaSwitchEdits);
			Layout.vanillaMachines.AddRange(other.vanillaMachines);
			Layout.disabledObjects.AddRange(other.disabledObjects);
			Layout.signs.AddRange(other.signs);
			Layout.clones.AddRange(other.clones);
			Layout.pitstopModules.AddRange(other.pitstopModules);
			Layout.vegetationMasks.AddRange(other.vegetationMasks);
			Changed?.Invoke();
		}

		// ---------- Gleise ----------

		public static PlacedTrack Add(Vector3 a, Vector3 b, ArcResult arc, string railType = "", string baseType = "", string overlay = "", string style = "ballast", float age = -1f)
		{
			var t = new PlacedTrack
			{
				a = PlacedTrack.FromVec(a),
				b = PlacedTrack.FromVec(b),
				sagitta = arc.sagitta,
				radius = arc.IsStraight || float.IsInfinity(arc.radius) ? 0f : arc.radius,
				length = arc.length,
				railType = railType,
				baseType = baseType,
				overlay = overlay,
				style = style,
				age = age,
			};
			foreach (var anc in arc.anchors)
				t.anchors.Add(new PlacedAnchor
				{
					p = PlacedTrack.FromVec(anc.position),
					h1 = PlacedTrack.FromVec(anc.handle1),
					h2 = PlacedTrack.FromVec(anc.handle2),
				});
			Layout.tracks.Add(t);
			Save();
			return t;
		}

		public static PlacedTrack? Find(string id) => Layout.tracks.Find(t => t.id == id);

		public static bool Remove(string id)
		{
			int n = Layout.tracks.RemoveAll(t => t.id == id)
			      + Layout.machines.RemoveAll(m => m.id == id)
			      + Layout.switches.RemoveAll(s => s.id == id)
			      + Layout.buffers.RemoveAll(b => b.id == id)
			      + Layout.caps.RemoveAll(c => c.id == id)
			      + Layout.signs.RemoveAll(x => x.id == id)
			      + Layout.clones.RemoveAll(x => x.id == id)
			      + Layout.vegetationMasks.RemoveAll(x => x.id == id);
			if (n > 0) Save();
			return n > 0;
		}

		// ---------- Weichen / Prellböcke ----------

		public static PlacedSwitch AddSwitch(string prefab, Vector3 pos, float yaw, bool covered = false)
		{
			var s = new PlacedSwitch { prefab = prefab, position = PlacedTrack.FromVec(pos), yaw = (float)Math.Round(yaw, 3), covered = covered };
			Layout.switches.Add(s);
			Save();
			return s;
		}

		public static PlacedBuffer AddBuffer(Vector3 pos, float yaw)
		{
			var b = new PlacedBuffer { position = PlacedTrack.FromVec(pos), yaw = (float)Math.Round(yaw, 3) };
			Layout.buffers.Add(b);
			Save();
			return b;
		}

		public static PlacedCap AddCap(Vector3 pos)
		{
			var c = new PlacedCap { position = PlacedTrack.FromVec(pos) };
			Layout.caps.Add(c);
			Save();
			return c;
		}

		public static RemovedVanilla AddRemovedVanilla(string name, Vector3 mid)
		{
			var r = new RemovedVanilla { name = name, mid = PlacedTrack.FromVec(mid) };
			Layout.removedVanilla.Add(r);
			Save();
			return r;
		}

		public static void ClearRemovedVanilla()
		{
			Layout.removedVanilla.Clear();
			Layout.vanillaMachines.RemoveAll(m => m.removed && m.cargo == null);
			foreach (var m in Layout.vanillaMachines) m.removed = false;
			Save();
		}

		public static PlacedMachine AddMachine(Vector3 pos, float yaw)
		{
			var m = new PlacedMachine { position = PlacedTrack.FromVec(pos), yaw = (float)Math.Round(yaw, 3) };
			Layout.machines.Add(m);
			Save();
			return m;
		}

		/// <summary>Änderungen an Gleis-IDs oder Maschinen speichern, ohne Vorschauen neu aufzubauen.</summary>
		public static void SaveQuiet() => Save(notify: false);

		/// <summary>Weichenstellung merken, ohne Vorschauen neu aufzubauen.</summary>
		public static void SetSwitchState(string id, int branch)
		{
			var s = Layout.switches.Find(x => x.id == id);
			if (s == null || s.selectedBranch == branch) return;
			s.selectedBranch = branch;
			Save(notify: false);
		}

		private static void Save(bool notify = true)
		{
			if (s_readOnly) { if (notify) Changed?.Invoke(); return; }
			try
			{
				string tmp = FilePath + ".tmp";
				File.WriteAllText(tmp, JsonConvert.SerializeObject(Layout, Formatting.Indented));
				if (File.Exists(FilePath)) File.Delete(FilePath);
				File.Move(tmp, FilePath);
			}
			catch (Exception e)
			{
				Main.Log($"Speichern fehlgeschlagen: {e.Message}");
			}
			if (notify) Changed?.Invoke();
		}
	}
}
