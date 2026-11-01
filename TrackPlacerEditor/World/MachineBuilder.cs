using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using DV.ThingTypes;
using TrackPlacer.Data;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TrackPlacer.World
{
	/// <summary>
	/// Lademaschinen: Klon einer WarehouseMachineController-Maschine aus dem Spiel.
	/// Die Maschine braucht beim Start ihr Gleis (sucht es per Name), darum bleibt der Controller
	/// deaktiviert, bis ein Gleis zugewiesen ist. Danach: Logik-Maschine beim Bahnhof eintragen
	/// (Yard.WarehouseMachines, StationController.warehouseMachineControllers), damit Lade-Jobs sie finden.
	/// </summary>
	internal static class MachineBuilder
	{
		private const string NamePrefix = "[TP]_machine_";
		private static readonly FieldInfo? FText = typeof(WarehouseMachineController).GetField("supportedCargoTypesText", BindingFlags.Instance | BindingFlags.NonPublic);
		private static readonly MethodInfo? MIdle = typeof(WarehouseMachineController).GetMethod("DisplayIdleText", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
		private static readonly FieldInfo? FInitialized = typeof(WarehouseMachineController).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic);
		private static readonly PropertyInfo? PCargo = typeof(DV.Logic.Job.WarehouseMachine).GetProperty("SupportedCargoTypes", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

		internal sealed class Built
		{
			public GameObject root = null!;
			public WarehouseMachineController controller = null!;
			public StationController? station;
			public bool registered;
		}

		private static readonly Dictionary<string, Built> s_built = new Dictionary<string, Built>();
		private static GameObject? s_template;
		private static Bounds s_templateBounds; // in Achsen der Vorlage (nur gedreht, nicht skaliert), Ursprung = Wurzel
		private static List<CargoType>? s_cargoTypes;
		private static readonly HashSet<WarehouseMachineController> s_hiddenVanilla = new HashSet<WarehouseMachineController>();

		public static IReadOnlyDictionary<string, Built> All => s_built;

		public static void OnWorldUnloaded()
		{
			s_built.Clear();
			s_template = null;
			s_hiddenVanilla.Clear();
			if (s_preview != null) Object.Destroy(s_preview);
			s_preview = null;
		}

		/// <summary>Alle Ladungsarten des Spiels (ohne „None“), alphabetisch.</summary>
		public static List<CargoType> CargoTypes
		{
			get
			{
				if (s_cargoTypes != null) return s_cargoTypes;
				s_cargoTypes = Enum.GetValues(typeof(CargoType)).Cast<CargoType>()
					.Where(c => c.ToString() != "None" && (int)c > 0)
					.OrderBy(c => DisplayName(c))
					.ToList();
				return s_cargoTypes;
			}
		}

		/// <summary>Lokalisierter Name wie im Spiel (Fallback: Enum-Name).</summary>
		public static string DisplayName(CargoType c)
		{
			try
			{
				var v2 = DV.ThingTypes.TransitionHelpers.TransitionHelpers.ToV2(c);
				if (v2 != null && !string.IsNullOrEmpty(v2.localizationKeyFull))
				{
					string l = Localize(v2.localizationKeyFull);
					if (!string.IsNullOrEmpty(l) && l != v2.localizationKeyFull) return l;
				}
			}
			catch { /* egal */ }
			return c.ToString();
		}

		private static MethodInfo? s_l;
		private static bool s_lSearched;
		private static string Localize(string key)
		{
			if (!s_lSearched)
			{
				s_lSearched = true;
				var asms = AppDomain.CurrentDomain.GetAssemblies();
				Type? t = null;
				foreach (var asm in asms)
				{
					try { t = asm.GetType("DV.Localization.LocalizationAPI", false); } catch { /* egal */ }
					if (t != null) break;
				}
				if (t == null)
					foreach (var asm in asms)
					{
						string an = asm.GetName().Name ?? "";
						if (!an.StartsWith("DV") && !an.StartsWith("Assembly-CSharp")) continue;
						try { t = asm.GetTypes().FirstOrDefault(x => x.Name == "LocalizationAPI"); } catch { /* egal */ }
						if (t != null) break;
					}
				s_l = t?.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault(m => m.Name == "L" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(string));
				if (s_l == null) Main.Log("LocalizationAPI.L nicht gefunden – Ladungsnamen als Enum-Namen.");
			}
			if (s_l == null) return key;
			var ps = s_l.GetParameters();
			var args = new object?[ps.Length];
			args[0] = key;
			for (int i = 1; i < ps.Length; i++)
				args[i] = ps[i].ParameterType.IsArray ? Array.CreateInstance(ps[i].ParameterType.GetElementType()!, 0)
					: ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
			try { return s_l.Invoke(null, args) as string ?? key; } catch { return key; }
		}

		public static bool TryParse(string name, out CargoType c) => Enum.TryParse(name, out c);

		/// <summary>
		/// Ganze Maschine als Vorlage: vom Controller aus nach oben, solange das Elternobjekt nur diese eine Maschine
		/// enthält, kein Bahnhof ist und kleiner als 25 m bleibt (Modell, Display, Hebel, Kollision).
		/// </summary>
		private static GameObject? Template
		{
			get
			{
				if (s_template != null) return s_template;
				foreach (var wmc in Object.FindObjectsOfType<WarehouseMachineController>())
				{
					if (wmc == null || wmc.GetComponentInParent<MachineMarker>() != null || s_hiddenVanilla.Contains(wmc)) continue;
					Transform t = MachineRoot(wmc);
					s_template = t.gameObject;
					s_templateBounds = CloneHelper.LocalBounds(t);
					Main.Log($"Lademaschinen-Vorlage: {Path(t)} (Controller {wmc.name}, Maße {s_templateBounds.size.x:0.00}×{s_templateBounds.size.y:0.00}×{s_templateBounds.size.z:0.00} m, Unterkante {s_templateBounds.min.y:0.00} m)");
					break;
				}
				return s_template;
			}
		}

		/// <summary>Oberstes Objekt einer Maschine: vom Controller nach oben, solange das Elternobjekt nur diese eine
		/// Maschine enthält, kein Bahnhof ist und kleiner als 25 m bleibt (Modell, Display, Hebel, Kollision).</summary>
		internal static Transform MachineRoot(WarehouseMachineController wmc)
		{
			Transform t = wmc.transform;
			while (t.parent != null)
			{
				Transform p = t.parent;
				if (p.GetComponent<StationController>() != null) break;
				if (p.GetComponentsInChildren<WarehouseMachineController>(true).Length != 1) break;
				if (p.GetComponentsInChildren<StationController>(true).Length > 0) break;
				if (SizeOf(p) > 25f) break;
				t = p;
			}
			return t;
		}

		/// <summary>Begrenzung aller Meshes relativ zur Wurzel, in deren Drehung (Maßstab der Welt).</summary>
		private static Bounds LocalBounds(Transform root)
		{
			Quaternion inv = Quaternion.Inverse(root.rotation);
			bool any = false;
			var b = new Bounds();
			foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
			{
				if (mf.sharedMesh == null) continue;
				Bounds mb = mf.sharedMesh.bounds;
				for (int i = 0; i < 8; i++)
				{
					Vector3 c = new Vector3((i & 1) == 0 ? mb.min.x : mb.max.x, (i & 2) == 0 ? mb.min.y : mb.max.y, (i & 4) == 0 ? mb.min.z : mb.max.z);
					Vector3 local = inv * (mf.transform.TransformPoint(c) - root.position);
					if (!any) { b = new Bounds(local, Vector3.zero); any = true; } else b.Encapsulate(local);
				}
			}
			return any ? b : new Bounds(new Vector3(0f, 1f, 0f), new Vector3(0.6f, 2f, 0.5f));
		}

		/// <summary>Umriss (geschlossenes Rechteck am Boden, statisch) einer Maschine an pos/yaw.</summary>
		public static List<Vector3> OutlineAt(Vector3 pos, float yaw)
		{
			_ = Template;
			Bounds b = s_template != null ? s_templateBounds : new Bounds(Vector3.zero, new Vector3(0.6f, 2f, 0.5f));
			Quaternion r = Quaternion.Euler(0f, yaw, 0f);
			float m = 0.15f;
			var pts = new List<Vector3>();
			foreach (var (x, z) in new[] { (b.min.x - m, b.min.z - m), (b.max.x + m, b.min.z - m), (b.max.x + m, b.max.z + m), (b.min.x - m, b.max.z + m), (b.min.x - m, b.min.z - m) })
				pts.Add(pos + r * new Vector3(x, 0f, z));
			return pts;
		}

		/// <summary>Umriss einer Spielmaschine aus ihren Render-Grenzen (statisch).</summary>
		public static List<Vector3> OutlineOf(WarehouseMachineController wmc)
		{
			Transform root = MachineRoot(wmc);
			var rs = root.GetComponentsInChildren<Renderer>();
			Vector3 shift = GameAccess.OriginShift;
			Bounds b = rs.Length > 0 ? rs[0].bounds : new Bounds(root.position, Vector3.one);
			foreach (var r in rs) b.Encapsulate(r.bounds);
			float y = b.min.y - shift.y, m = 0.15f;
			var pts = new List<Vector3>();
			foreach (var (x, z) in new[] { (b.min.x - m, b.min.z - m), (b.max.x + m, b.min.z - m), (b.max.x + m, b.max.z + m), (b.min.x - m, b.max.z + m), (b.min.x - m, b.min.z - m) })
				pts.Add(new Vector3(x - shift.x, y, z - shift.z));
			return pts;
		}

		private static float SizeOf(Transform t)
		{
			var rs = t.GetComponentsInChildren<Renderer>(true);
			if (rs.Length == 0) return 0f;
			var b = rs[0].bounds;
			foreach (var r in rs) b.Encapsulate(r.bounds);
			return Mathf.Max(b.size.x, b.size.z);
		}

		private static string Path(Transform t) => t.parent == null ? t.name : Path(t.parent) + "/" + t.name;

		/// <summary>
		/// Die Vorlage ist eine laufende Spielmaschine: ihr Hebel hat zur Laufzeit Komponenten bekommen
		/// (LeverNonVR, Rigidbody, Gelenk, DVConvertToEntity am Sound). Im Klon würden sie beim Aktivieren ein
		/// zweites Mal angelegt → Fehler, Hebel nicht bedienbar. Darum im Klon vorher entfernen.
		/// </summary>
		private static void StripRuntimeComponents(GameObject clone)
		{
			int n = 0;
			var ctrlObjects = new HashSet<GameObject>();
			foreach (var c in clone.GetComponentsInChildren<DV.CabControls.ControlImplBase>(true)) ctrlObjects.Add(c.gameObject);
			foreach (var go in ctrlObjects)
			{
				foreach (var c in go.GetComponents<DV.CabControls.ControlImplBase>()) { Object.DestroyImmediate(c); n++; }
				foreach (var j in go.GetComponents<Joint>()) { Object.DestroyImmediate(j); n++; }
				var rb = go.GetComponent<Rigidbody>();
				if (rb != null) { Object.DestroyImmediate(rb); n++; }
			}
			foreach (var c in clone.GetComponentsInChildren<Component>(true))
				if (c != null && c.GetType().Name == "DVConvertToEntity") { Object.DestroyImmediate(c); n++; }
			if (n > 0) Main.Log($"Lademaschine: {n} Laufzeit-Komponenten im Klon entfernt (werden beim Start neu angelegt)");
		}

		/// <summary>Markiert eigene Maschinen (damit sie nie als Vorlage oder Spielmaschine gelten).</summary>
		internal sealed class MachineMarker : MonoBehaviour { public string id = ""; }

		/// <summary>Eigene Maschine unter dem Laser (statische Koordinaten).</summary>
		public static string? FindNear(Vector3 staticPos, float radius)
		{
			string? best = null;
			float bestD = radius * radius;
			Vector3 shift = GameAccess.OriginShift;
			foreach (var kv in s_built)
			{
				if (kv.Value.root == null) continue;
				float d = ((kv.Value.root.transform.position - shift) - staticPos).sqrMagnitude;
				if (d < bestD) { bestD = d; best = kv.Key; }
			}
			return best;
		}

		/// <summary>Text auf ein TextMeshPro-Feld schreiben (ohne TMP-Referenz).</summary>
		private static void SetTmp(WarehouseMachineController c, string field, string text)
		{
			// Feld per Reflection lesen: so braucht die Mod keinen Verweis auf Unity.TextMeshPro
			object? tmp = typeof(WarehouseMachineController).GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(c);
			if (tmp == null) return;
			try { tmp.GetType().GetProperty("text")?.SetValue(tmp, text); } catch { /* egal */ }
		}

		// ------------------------------------------------------------------
		// Bauen
		// ------------------------------------------------------------------

		public static bool Build(PlacedMachine placed, out string error)
		{
			error = "";
			if (s_built.ContainsKey(placed.id)) return true;
			if (!TrackBuilder.CheckReady(out error)) return false;
			var template = Template;
			if (template == null) { error = Tr.T("Keine Lademaschine in der Welt gefunden", "No loading machine found in the world"); return false; }

			GameObject? clone = null;
			try
			{
				// Unter einem inaktiven Halter klonen: kein Awake/OnEnable, die Spielmaschine bleibt unberührt
				var holder = new GameObject("[TP]_holder");
				holder.SetActive(false);
				clone = Object.Instantiate(template, holder.transform);
				clone.name = NamePrefix + placed.id;
				clone.AddComponent<MachineMarker>().id = placed.id;

				// Noch unter dem inaktiven Halter konfigurieren: Controller aus, bis ein Gleis zugewiesen ist
				var wmc = clone.GetComponentInChildren<WarehouseMachineController>(true);
				wmc.supportedCargoTypes = ParseCargo(placed.cargo);
				wmc.warehouseTrack = null;
				wmc.warehouseTrackName = "";
				wmc.enabled = false;
				try { FInitialized?.SetValue(wmc, false); } catch { /* egal */ } // Hebel beim ersten Aktivieren einrichten

				CloneHelper.StripRuntimeComponents(clone);
				CloneHelper.FixStaticBatched(template, clone);

				clone.SetActive(false);
				clone.transform.SetParent(null, true);
				Object.Destroy(holder);
				// Unterkante der Maschine auf den angeklickten Boden
				clone.transform.SetPositionAndRotation(placed.Position - Vector3.up * s_templateBounds.min.y, Quaternion.Euler(0f, placed.yaw, 0f));
				clone.SetActive(true);
				TrackBuilder.MoveUnderRailwayRoot(clone.transform, GameAccess.Registry!);

				var b = new Built { root = clone, controller = wmc };
				s_built[placed.id] = b;
				if (placed.HasTrack) Activate(placed, b);
				if (!wmc.enabled) ShowOffline(placed, b);
				return true;
			}
			catch (Exception e)
			{
				error = e.GetType().Name + ": " + e.Message;
				Main.Log($"Lademaschine {placed.id} fehlgeschlagen: {e}");
				if (clone != null) Object.Destroy(clone);
				return false;
			}
		}

		private static List<CargoType> ParseCargo(List<string> names)
		{
			var list = new List<CargoType>();
			foreach (var n in names) if (TryParse(n, out var c) && !list.Contains(c)) list.Add(c);
			if (list.Count == 0) list.Add(CargoTypes.FirstOrDefault()); // die Maschine erwartet mind. einen Eintrag
			return list;
		}

		/// <summary>Gleis der Maschine: eigenes Gleis oder Spielgleis.</summary>
		public static RailTrack? ResolveTrack(PlacedMachine placed)
		{
			if (!string.IsNullOrEmpty(placed.trackId))
				return TrackBuilder.Built.TryGetValue(placed.trackId, out RailTrack r) ? r : null;
			if (!string.IsNullOrEmpty(placed.vanillaTrack))
				return VanillaRemover.Find(placed.vanillaTrack, PlacedTrack.ToVec(placed.vanillaTrackMid));
			return null;
		}

		/// <summary>Gleis setzen und Controller starten. Bahnhof-Eintrag erfolgt einen Frame später (nach Start).</summary>
		private static void Activate(PlacedMachine placed, Built b)
		{
			RailTrack? rail = ResolveTrack(placed);
			if (rail == null)
			{
				Main.Log($"Lademaschine {placed.id}: Gleis {placed.trackId}{placed.vanillaTrack} nicht gefunden");
				return;
			}
			var reg = GameAccess.Registry;
			if (reg != null && GameAccess.RegAllTracks?.GetValue(reg) is RailTrack[] all && all.Count(t => t != null && t.name == rail.name) > 1)
				Main.Log($"Lademaschine {placed.id}: Gleisname '{rail.name}' ist nicht eindeutig – die Maschine nimmt das erste Gleis dieses Namens.");
			b.controller.warehouseTrackName = rail.name;
			b.controller.enabled = true;
			string id = placed.id;
			WorldLoader.RunNextFrame(() => Register(id));
		}

		private static void Register(string id)
		{
			if (!s_built.TryGetValue(id, out var b) || b.registered) return;
			var placed = LayoutStore.Layout.machines.Find(m => m.id == id);
			var logic = b.controller.warehouseMachine;
			if (placed == null || logic == null) { Main.Log($"Lademaschine {id}: noch keine Logik – Gleis korrekt?"); return; }

			var station = !string.IsNullOrEmpty(placed.yardId) ? StationLink.ByYard(placed.yardId) : StationLink.Nearest(placed.Position);
			if (station == null) { Main.Log($"Lademaschine {id}: kein Bahnhof gefunden"); return; }
			// Feste ID wie bei Spielmaschinen („HB-0“): Lade-Jobs speichern sie (loadMachineId). Ohne ID wird null
			// gespeichert – beim Laden passt dann irgendeine Maschine ohne ID oder gar keine.
			if (string.IsNullOrEmpty(logic.ID))
			{
				string yard = StationLink.YardOf(station);
				logic.ID = $"{yard}-TP{id.Substring(0, Math.Min(6, id.Length)).ToUpperInvariant()}";
			}
			if (!station.logicStation.yard.WarehouseMachines.Contains(logic)) station.logicStation.yard.WarehouseMachines.Add(logic);
			if (!station.warehouseMachineControllers.Contains(b.controller)) station.warehouseMachineControllers.Add(b.controller);
			b.station = station;
			b.registered = true;
			Main.Log($"Lademaschine {id} ({logic.ID}) bei {station.stationInfo?.Name} angemeldet ({logic.SupportedCargoTypes.Count} Ladungen)");
		}

		private static void Unregister(Built b)
		{
			if (!b.registered || b.station == null) return;
			var logic = b.controller != null ? b.controller.warehouseMachine : null;
			if (logic != null) b.station.logicStation?.yard?.WarehouseMachines.Remove(logic);
			b.station.warehouseMachineControllers.Remove(b.controller);
			b.registered = false;
		}

		// ------------------------------------------------------------------
		// Bearbeiten
		// ------------------------------------------------------------------

		/// <summary>Ladeliste übernehmen und das Maschinen-Display sofort aktualisieren.</summary>
		public static void UpdateCargo(PlacedMachine placed)
		{
			if (!s_built.TryGetValue(placed.id, out var b)) return;
			SetCargo(b.controller, ParseCargo(placed.cargo));
			if (!b.controller.isActiveAndEnabled) ShowOffline(placed, b);
		}

		/// <summary>Ladeliste an Controller und Logik-Maschine setzen, Display wie in Start neu aufbauen.</summary>
		private static void SetCargo(WarehouseMachineController wmc, List<CargoType> list)
		{
			wmc.supportedCargoTypes = list;
			var logic = wmc.warehouseMachine;
			if (logic != null) { try { PCargo?.SetValue(logic, list); } catch (Exception e) { Main.Log("Ladeliste: " + e.Message); } }
			var sb = new StringBuilder();
			foreach (var c in list) sb.AppendLine(DisplayName(c));
			FText?.SetValue(wmc, sb.ToString());
			if (wmc.isActiveAndEnabled) { try { MIdle?.Invoke(wmc, null); } catch { /* egal */ } }
		}

		/// <summary>Display einer (noch) nicht gestarteten Maschine: Ladeliste + Hinweis auf fehlendes Gleis.</summary>
		private static void ShowOffline(PlacedMachine placed, Built b)
		{
			var sb = new StringBuilder();
			if (placed.cargo.Count == 0) sb.AppendLine("(keine Ladung)");
			foreach (var n in placed.cargo) sb.AppendLine(TryParse(n, out var c) ? DisplayName(c) : n);
			SetTmp(b.controller, "displayTitleText", "TrackPlacer");
			SetTmp(b.controller, "displayText", sb.ToString());
			SetTmp(b.controller, "trackIdText", placed.HasTrack ? "?" : "kein Gleis");
		}

		/// <summary>Gleis zuweisen (eigenes oder Spielgleis). Ohne Bahnhofs-ID bekommt das Gleis eine Loading-ID
		/// beim Bahnhof der Maschine; Spielgleise mit vorhandener ID behalten sie.</summary>
		public static bool AssignTrack(PlacedMachine placed, TrackRef target, out string error)
		{
			error = "";
			if (!s_built.ContainsKey(placed.id)) { error = Tr.T("Maschine nicht gebaut", "Machine not built"); return false; }
			RailTrack? rail = target.Rail;
			if (rail == null) { error = Tr.T("Gleis nicht gebaut", "Track not built"); return false; }

			var station = !string.IsNullOrEmpty(placed.yardId) ? StationLink.ByYard(placed.yardId) : StationLink.Nearest(placed.Position);
			if (station == null) { error = Tr.T("kein Bahnhof", "no station"); return false; }
			placed.yardId = StationLink.YardOf(station);

			if (target.own != null)
			{
				var track = target.own;
				if (string.IsNullOrEmpty(track.yardId) || track.trackType != "L")
				{
					track.yardId = placed.yardId;
					if (string.IsNullOrEmpty(track.subYard)) track.subYard = "W";
					track.trackType = "L";
					track.number = StationLink.NextFreeNumber(track.yardId, track.subYard, track.id).ToString("D2");
				}
				StationLink.Apply(track, out _);
				placed.trackId = track.id;
				placed.vanillaTrack = "";
			}
			else if (target.vanilla != null)
			{
				var v = target.vanilla;
				if (target.CurrentId == "" || target.VanillaEntry != null)
				{
					// Generisches Spielgleis: L-ID vergeben (gespeichert in vanillaTrackIds)
					var e = target.VanillaEntry;
					if (e == null)
					{
						e = new VanillaTrackId { name = v.name, mid = PlacedTrack.FromVec(VanillaRemover.Mid(v)) };
						LayoutStore.Layout.vanillaTrackIds.Add(e);
					}
					if (string.IsNullOrEmpty(e.yardId) || e.trackType != "L")
					{
						e.yardId = placed.yardId;
						e.subYard = "W";
						e.trackType = "L";
						e.number = StationLink.NextFreeNumber(e.yardId, e.subYard, StationLink.VanillaKey(v.name)).ToString("D2");
					}
					StationLink.Apply(e, out _);
				}
				placed.trackId = "";
				placed.vanillaTrack = v.name;
				placed.vanillaTrackMid = PlacedTrack.FromVec(VanillaRemover.Mid(v));
			}

			// Maschine neu aufbauen: der Controller liest Gleis und Ladeliste nur beim Start
			LayoutStore.SaveQuiet();
			Remove(placed.id);
			return Build(placed, out error);
		}

		public static bool Remove(string id)
		{
			if (s_built.TryGetValue(id, out var b))
			{
				Unregister(b);
				if (b.root != null) Object.Destroy(b.root);
			}
			s_built.Remove(id);
			return true;
		}

		/// <summary>Position und Drehung übernehmen (nach Drehen oder Höhe ändern).</summary>
		public static void SetPose(PlacedMachine placed)
		{
			if (!s_built.TryGetValue(placed.id, out var b) || b.root == null) return;
			b.root.transform.SetPositionAndRotation(
				placed.Position - Vector3.up * s_templateBounds.min.y + GameAccess.OriginShift,
				Quaternion.Euler(0f, placed.yaw, 0f));
		}

		// ------------------------------------------------------------------
		// Vorschau beim Setzen: halbtransparente Kopie der Maschine (nur Meshes)
		// ------------------------------------------------------------------

		private static GameObject? s_preview;

		private static GameObject? Preview
		{
			get
			{
				if (s_preview != null) return s_preview;
				var template = Template;
				if (template == null) return null;
				s_preview = CloneHelper.MakeGhost(template, "[TrackPlacer] Maschinen-Vorschau");
				return s_preview;
			}
		}

		/// <summary>Vorschau an pos (statisch, Boden) mit Drehung yaw zeigen.</summary>
		public static void ShowPreview(Vector3 groundStatic, float yaw)
		{
			var p = Preview;
			if (p == null) return;
			p.transform.SetPositionAndRotation(groundStatic - Vector3.up * s_templateBounds.min.y + GameAccess.OriginShift, Quaternion.Euler(0f, yaw, 0f));
			if (!p.activeSelf) p.SetActive(true);
		}

		public static void HidePreview()
		{
			if (s_preview != null) s_preview.SetActive(false);
		}

		/// <summary>Gleis einer Maschine wurde entfernt: Maschine abmelden und ohne Gleis neu aufbauen.</summary>
		public static void OnTrackRemoved(string trackId)
		{
			foreach (var m in LayoutStore.Layout.machines.Where(m => m.trackId == trackId).ToList())
			{
				m.trackId = "";
				Remove(m.id);
				Build(m, out _);
			}
		}

		/// <summary>Spielgleis entfernt: Maschinen darauf abmelden.</summary>
		public static void OnVanillaTrackRemoved(string trackName)
		{
			foreach (var m in LayoutStore.Layout.machines.Where(m => m.vanillaTrack == trackName).ToList())
			{
				m.vanillaTrack = "";
				Remove(m.id);
				Build(m, out _);
			}
		}

		// ------------------------------------------------------------------
		// Lademaschinen des Spiels: Ladung ändern, entfernen
		// ------------------------------------------------------------------

		public static bool IsOwn(WarehouseMachineController wmc) => wmc != null && wmc.GetComponentInParent<MachineMarker>() != null;

		/// <summary>Spielmaschine nahe dem Laser (statisch, waagerecht).</summary>
		public static WarehouseMachineController? FindVanilla(Vector3 staticPos, float radius)
		{
			WarehouseMachineController? best = null;
			float bestD = radius * radius;
			Vector3 shift = GameAccess.OriginShift;
			var list = WarehouseMachineController.allControllers;
			if (list == null) return null;
			foreach (var wmc in list)
			{
				if (wmc == null || IsOwn(wmc) || s_hiddenVanilla.Contains(wmc) || !wmc.gameObject.activeInHierarchy) continue;
				Vector3 p = wmc.transform.position - shift;
				if (Mathf.Abs(p.y - staticPos.y) > 5f) continue;
				float d = new Vector2(p.x - staticPos.x, p.z - staticPos.z).sqrMagnitude;
				if (d < bestD) { bestD = d; best = wmc; }
			}
			return best;
		}

		public static VanillaMachineEdit? EditOf(WarehouseMachineController wmc, bool create)
		{
			var e = LayoutStore.Layout.vanillaMachines.Find(x => x.trackName == wmc.warehouseTrackName);
			if (e == null && create)
			{
				e = new VanillaMachineEdit { trackName = wmc.warehouseTrackName };
				LayoutStore.Layout.vanillaMachines.Add(e);
			}
			return e;
		}

		/// <summary>Aktuelle Ladeliste einer Spielmaschine (als Namen, zum Bearbeiten).</summary>
		public static List<string> CargoOf(WarehouseMachineController wmc)
		{
			var e = EditOf(wmc, true)!;
			if (e.cargo == null) e.cargo = (wmc.supportedCargoTypes ?? new List<CargoType>()).Select(c => c.ToString()).ToList();
			return e.cargo;
		}

		public static void ApplyVanillaCargo(WarehouseMachineController wmc)
		{
			var e = EditOf(wmc, false);
			if (e?.cargo != null) SetCargo(wmc, ParseCargo(e.cargo));
		}

		/// <summary>Spielmaschine ausblenden und beim Bahnhof abmelden (keine neuen Lade-Jobs mehr).</summary>
		public static void HideVanilla(WarehouseMachineController wmc)
		{
			if (wmc == null || s_hiddenVanilla.Contains(wmc)) return;
			var logic = wmc.warehouseMachine;
			foreach (var st in StationLink.Stations)
			{
				if (!st.warehouseMachineControllers.Contains(wmc)) continue;
				st.warehouseMachineControllers.Remove(wmc);
				if (logic != null) st.logicStation?.yard?.WarehouseMachines.Remove(logic);
			}
			Transform root = MachineRoot(wmc);
			if (s_template == root.gameObject) s_template = null;
			s_hiddenVanilla.Add(wmc);
			root.gameObject.SetActive(false);
		}

		/// <summary>Gespeicherte Änderungen an Spielmaschinen anwenden (beim Laden).</summary>
		public static int ApplyVanillaEdits()
		{
			int n = 0;
			var list = WarehouseMachineController.allControllers?.ToList() ?? new List<WarehouseMachineController>();
			foreach (var e in LayoutStore.Layout.vanillaMachines)
			{
				var wmc = list.FirstOrDefault(w => w != null && !IsOwn(w) && w.warehouseTrackName == e.trackName);
				if (wmc == null) { Main.Log($"Spiel-Lademaschine für Gleis '{e.trackName}' nicht gefunden"); continue; }
				if (e.removed) HideVanilla(wmc);
				else if (e.cargo != null) SetCargo(wmc, ParseCargo(e.cargo));
				n++;
			}
			return n;
		}

		public static int BuildAll()
		{
			int n = 0;
			foreach (var m in LayoutStore.Layout.machines)
				if (!s_built.ContainsKey(m.id) && Build(m, out _)) n++;
			return n;
		}
	}
}
