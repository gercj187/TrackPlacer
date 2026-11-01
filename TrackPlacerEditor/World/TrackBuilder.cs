using System;
using System.Collections.Generic;
using System.Linq;
using DV.Logic.Job;
using DV.PointSet;
using TrackPlacer.Data;
using TrackPlacer.Geometry;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TrackPlacer.World
{
	/// <summary>
	/// Baut aus einem gespeicherten Gleis ein echtes, befahrbares RailTrack zur Laufzeit.
	///
	/// Ablauf (an die Initialisierung des Spiels angelehnt):
	///  1. GameObject inaktiv an der statischen Position anlegen, BezierCurve + RailTrack anhängen
	///  2. Enden mit freien Nachbarenden verbinden (Gleise und Weichen)
	///  3. Aktivieren → RailTrack.Awake → Init (PointSet + Collider), Punkte in absoluten Koordinaten
	///  4. Unter den [railway]-Root hängen (Origin-Shift korrekt übernehmen)
	///  5. In Registry, Logik-Gleise und den Mesh-Generator eintragen
	/// </summary>
	internal static class TrackBuilder
	{
		public const float ConnectTolerance = 0.05f;
		/// <summary>Nach dem Laden: freie Enden eigener Gleise/Weichen, die so nah beieinander liegen, werden verbunden.</summary>
		public const float AutoConnectTolerance = 0.2f;
		public const string NamePrefix = "[TP]_";

		private static readonly Dictionary<string, RailTrack> s_built = new Dictionary<string, RailTrack>();

		/// <summary>Materialien pro eigenem Gleis (der Mesh-Generator des Spiels kennt nur ein globales Material).</summary>
		internal static readonly Dictionary<RailTrack, (Material ballast, Material rail)> Materials = new Dictionary<RailTrack, (Material, Material)>();
		private static readonly HashSet<RailTrack> s_noSleepers = new HashSet<RailTrack>();
		private static Material? s_invisible;

		/// <summary>Spielgleise in der Original-Reihenfolge (vor dem ersten eigenen Gleis festgehalten).</summary>
		private static RailTrack[]? s_vanillaOrdered;
		/// <summary>Alle registrierten eigenen Gleise (auch Weichengleise), in Registrierungs-Reihenfolge.</summary>
		private static readonly List<RailTrack> s_registered = new List<RailTrack>();
		private static RailTrackRegistryBase? s_world;
		private static bool s_builtBeforeMeshGen;

		/// <summary>Material, das nichts zeichnet – für ausgeblendeten Schotter (die Geometrie erzeugt das Spiel trotzdem).</summary>
		internal static Material Invisible
		{
			get
			{
				if (s_invisible != null) return s_invisible;
				var sh = Shader.Find("Hidden/Internal-Colored");
				s_invisible = new Material(sh) { name = "TP_Invisible", color = new Color(0, 0, 0, 0) };
				s_invisible.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
				s_invisible.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
				s_invisible.SetInt("_ZWrite", 0);
				s_invisible.SetInt("_Cull", 0);
				return s_invisible;
			}
		}

		public static IReadOnlyDictionary<string, RailTrack> Built => s_built;
		public static bool IsOwn(RailTrack t) => t != null && (t.name.StartsWith(NamePrefix) || JunctionBuilder.IsOwnJunctionTrack(t));

		/// <summary>Neue Welt erkannt (Registry-Objekt gewechselt)? Dann alle Zustände der alten Welt verwerfen.
		/// Ersetzt das frühere Zurücksetzen beim Funkgerät-Start, das nach einem frühen Aufbau alles vergessen hätte.</summary>
		public static void EnsureWorld()
		{
			var reg = GameAccess.Registry;
			if (reg == null || reg == s_world) return;
			OnWorldUnloaded();
			s_world = reg;
		}

		public static void OnWorldUnloaded()
		{
			s_vanillaOrdered = null;
			s_registered.Clear();
			s_builtBeforeMeshGen = false;
			s_built.Clear();
			Materials.Clear();
			s_noSleepers.Clear();
			TrackTypes.Reset();
			JunctionBuilder.OnWorldUnloaded();
			OverlayBuilder.OnWorldUnloaded();
			TurntableSupport.OnWorldUnloaded();
			VanillaRemover.OnWorldUnloaded();
			BufferBuilder.OnWorldUnloaded();
			StationLink.OnWorldUnloaded();
			MachineBuilder.OnWorldUnloaded();
			ObjectDisabler.OnWorldUnloaded();
			LodSupport.OnWorldUnloaded();
			LoadOrder.OnWorldUnloaded();
			FarTrackHider.OnWorldUnloaded();
			CapBuilder.OnWorldUnloaded();
			SignBuilder.OnWorldUnloaded();
			CloneBuilder.OnWorldUnloaded();
			PitstopEditor.OnWorldUnloaded();
			VegetationBuilder.OnWorldUnloaded();
			VanillaTrackEditor.OnWorldUnloaded();
			SwitchCoverBuilder.OnWorldUnloaded();
		}

		// ------------------------------------------------------------------
		// Bauen
		// ------------------------------------------------------------------

		/// <summary>Baut alles aus der Datei: erst Weichen (damit Gleise an sie anschließen können), dann Gleise, dann Prellböcke.</summary>
		public static bool BuildAll(out string message)
		{
			int ok = 0, fail = 0;
			foreach (var s in LayoutStore.Layout.switches)
			{
				if (JunctionBuilder.Built.ContainsKey(s.id)) continue;
				if (JunctionBuilder.Build(s, out string e1)) ok++; else { fail++; Main.Log($"Weiche {s.id} nicht gebaut: {e1}"); }
			}
			foreach (var t in LayoutStore.Layout.tracks)
			{
				if (s_built.ContainsKey(t.id)) continue;
				if (Build(t, out string e2)) ok++; else { fail++; Main.Log($"Gleis {t.id} nicht gebaut: {e2}"); }
			}
			// Prellböcke/Schotterenden gehören zu Schritt 3 der Lade-Reihenfolge (nach Gleis-IDs und Lademaschinen)
			if (LoadOrder.Objects)
				foreach (var b in LayoutStore.Layout.buffers)
				{
					if (BufferBuilder.Built.ContainsKey(b.id)) continue;
					if (BufferBuilder.Build(b, out string e3)) ok++; else { fail++; Main.Log($"Prellbock {b.id} nicht gebaut: {e3}"); }
				}
			int removed = VanillaRemover.ApplyAll();
			if (LoadOrder.Objects) CapBuilder.BuildAll();
			TurntableSupport.Refresh();
			ApplyOrder();
			int auto = AutoConnectAll(AutoConnectTolerance);
			if (auto > 0) Main.Log($"{auto} Gleisende(n) automatisch verbunden (Abstand ≤ {AutoConnectTolerance:0.00} m).");
			// Logik-Nachbarn aller eigenen Gleise samt Nachbarn einmal vollständig nachziehen (nach dem Auto-Verbinden)
			foreach (var t in s_registered.ToList()) if (t != null) RefreshLogicNeighbors(t);
			int bad = VerifyAll();
			if (GameAccess.TracksReady) LoadOrder.TracksBuilt();
			if (bad > 0) Main.Log($"ACHTUNG: {bad} fehlerhafte Verbindung(en) gefunden und gelöst – siehe Zeilen oben.");
			message = $"{ok} gebaut" + (removed > 0 ? $", {removed} Spielgleise entfernt" : "") + (fail > 0 ? $", {fail} fehlgeschlagen" : "");
			return fail == 0;
		}

		public static bool Build(PlacedTrack placed, out string error)
		{
			error = "";
			if (s_built.ContainsKey(placed.id)) return true;
			if (!CheckReady(out error)) return false;

			TrackTypeOption? type = TrackTypes.Resolve(placed.railType, placed.baseType, placed.style, placed.age);
			if (type == null) { error = Tr.T("Kein Vorlagengleis gefunden", "No template track found"); return false; }

			List<Anchor> anchors = placed.ToAnchors();
			if (anchors.Count < 2) { error = "Gleis hat weniger als 2 Punkte"; return false; }

			GameObject? go = null;
			RailTrack? track = null;
			try
			{
				var registry = GameAccess.Registry!;
				var sw = System.Diagnostics.Stopwatch.StartNew();
				FreezeHashes(registry);

				go = new GameObject(NamePrefix + placed.id);
				go.SetActive(false);
				go.transform.position = anchors[0].position; // statische = absolute Koordinaten

				var curve = go.AddComponent<BezierCurve>();
				curve.resolution = RailTrack.DEFAULT_BEZIER_RESOLUTION;
				foreach (var an in anchors)
				{
					BezierPoint bp = curve.AddPointAt(an.position);
					bp.handleStyle = BezierPoint.HandleStyle.Connected;
					bp.globalHandle2 = an.position + an.handle2;
					bp.globalHandle1 = an.position + an.handle1;
				}

				track = go.AddComponent<RailTrack>();
				track.railType = type.rail;
				track.baseType = type.baseType;
				track.age = type.age;
				track.generateMeshes = true;
				// Nur-Schienen-Gleise ohne Schotter-Collider, sonst stolpert man über unsichtbaren Schotter
				track.generateColliders = type.style != TrackStyle.Rails;
				Materials[track] = (type.style == TrackStyle.Ballast ? type.baseType.baseMaterial : Invisible, type.rail.railMaterial);
				if (type.style == TrackStyle.Rails) s_noSleepers.Add(track);
				track.inBranch = new Junction.Branch();
				track.outBranch = new Junction.Branch();

				ConnectEnds(track, anchors[0].position, anchors[anchors.Count - 1].position);
				long tConnect = sw.ElapsedMilliseconds;

				// Awake → Init: PointSet und Collider entstehen jetzt in absoluten Koordinaten
				go.SetActive(true);
				MoveUnderRailwayRoot(go.transform, registry);
				long tInit = sw.ElapsedMilliseconds;

				RegisterTrack(track);
				long tReg = sw.ElapsedMilliseconds;

				s_built[placed.id] = track;
				ApplyOrder();
				VerifyConnections(track);
				OverlayBuilder.Build(placed, track);
				VegetationBuilder.BuildTrack(placed);
				long tCover = sw.ElapsedMilliseconds;
				TrackEnds.MarkDirty();
				TurntableSupport.RefreshNear(track);
				long tAll = sw.ElapsedMilliseconds;
				Main.Log($"Gleis {placed.id} gebaut ({type.Label}), in={Describe(track, true)} out={Describe(track, false)} – " +
				         $"{tAll} ms (Anschluss {tConnect}, Init {tInit - tConnect}, Registrieren {tReg - tInit}, Abdeckung {tCover - tReg}, Drehscheibe {tAll - tCover})");
				return true;
			}
			catch (Exception e)
			{
				error = e.GetType().Name + ": " + e.Message;
				Main.Log($"Bau von {placed.id} fehlgeschlagen: {e}");
				try { if (track != null) { Unlink(track); UnregisterTrack(track); Materials.Remove(track); s_noSleepers.Remove(track); } }
				catch (Exception cleanup) { Main.Log("Aufräumen nach Fehler: " + cleanup.Message); }
				if (go != null) Object.Destroy(go);
				return false;
			}
		}

		internal static bool CheckReady(out string error)
		{
			error = "";
			EnsureWorld();
			if (!GameAccess.TracksReady) { error = Tr.T("Welt noch nicht bereit", "World not ready yet"); return false; }
			if (!GameAccess.Ok(out string missing)) { error = "Spiel-Update? Fehlend: " + missing; return false; }
			return true;
		}

		/// <summary>Darstellung eines vorhandenen Spielgleises austauschen, ohne Registry oder Logik anzufassen.</summary>
		internal static bool ApplyType(RailTrack track, TrackTypeOption type, out string error)
		{
			error = "";
			if (track == null || type == null) { error = Tr.T("Gleis oder Gleistyp fehlt", "Track or track type missing"); return false; }
			if (!CheckReady(out error)) return false;
			try
			{
				RemoveFromMeshGenerator(track);
				track.railType = type.rail;
				track.baseType = type.baseType;
				track.age = type.age;
				track.generateMeshes = true;
				Materials[track] = (type.style == TrackStyle.Ballast ? type.baseType.baseMaterial : Invisible, type.rail.railMaterial);
				if (type.style == TrackStyle.Rails) s_noSleepers.Add(track); else s_noSleepers.Remove(track);
				AddToMeshGenerator(track, track.GetKinkedPointSet());
				return true;
			}
			catch (Exception e)
			{
				error = e.GetType().Name + ": " + e.Message;
				Main.Log($"Gleistyp von Spielgleis {track.name} konnte nicht geändert werden: {e}");
				return false;
			}
		}

		/// <summary>Hashes einfrieren, bevor wir etwas hinzufügen: Spielstände bleiben kompatibel.</summary>
		internal static void FreezeHashes(RailTrackRegistryBase registry)
		{
			_ = registry.TracksHash;
			_ = registry.JunctionsHash;
		}

		/// <summary>Objekt wurde an absoluten Koordinaten aufgebaut → um den Origin-Shift versetzen und einhängen.</summary>
		internal static void MoveUnderRailwayRoot(Transform t, RailTrackRegistryBase registry)
		{
			t.position += GameAccess.OriginShift;
			t.SetParent(registry.TrackRootParent, true);
		}

		private static string Describe(RailTrack t, bool start)
		{
			Junction? j = start ? t.inJunction : t.outJunction;
			if (j != null) return "Weiche";
			Junction.Branch? b = start ? t.inBranch : t.outBranch;
			return b?.track == null ? "frei" : b.track.name;
		}

		// ------------------------------------------------------------------
		// Verbinden
		// ------------------------------------------------------------------

		private static void ConnectEnds(RailTrack track, Vector3 startStatic, Vector3 endStatic)
		{
			if (TrackEnds.FindFreeEndAt(startStatic, ConnectTolerance, track, out TrackEnd n1)) Connect(track, true, n1);
			if (TrackEnds.FindFreeEndAt(endStatic, ConnectTolerance, track, out TrackEnd n2)) Connect(track, false, n2);
		}

		private static bool IsFree(RailTrack t, bool atStart) =>
			atStart ? t.inJunction == null && t.inBranch?.track == null : t.outJunction == null && t.outBranch?.track == null;

		/// <summary>
		/// Freie Enden eigener Gleise und Weichen mit dem nächsten freien Ende (Gleis, Weichenausgang oder
		/// Weichenanfang, auch des Spiels) verbinden, wenn es höchstens <paramref name="tolerance"/> entfernt ist.
		/// Behebt nach einem Neustart fehlende Anschlüsse durch Rundungen (Weichengeometrie vs. gespeicherte Gleise).
		/// Drehscheiben-Ränder werden nie fest verbunden.
		/// </summary>
		internal static int AutoConnectAll(float tolerance)
		{
			int n = 0;
			TrackEnds.MarkDirty();
			var ends = TrackEnds.All.Where(e => e.track != null && e.turntable == null).ToList();
			float tol2 = tolerance * tolerance;
			foreach (var e in ends)
			{
				if (e.IsJunctionIn) continue;                       // Weichenanfänge sind nur Ziel
				var t = e.track!;
				if (!IsOwn(t) || !IsFree(t, e.isStart)) continue;
				TrackEnd? best = null;
				float bestD = tol2;
				foreach (var f in ends)
				{
					if (f.track == t && !f.IsJunctionIn) continue;
					if (f.IsJunctionIn ? f.junction!.inBranch?.track != null : !IsFree(f.track!, f.isStart)) continue;
					if (f.IsJunctionIn && (t.inJunction == f.junction || t.outJunction == f.junction)) continue;
					float d = (f.position - e.position).sqrMagnitude;
					if (d <= bestD) { bestD = d; best = f; }
				}
				if (best == null) continue;
				Connect(t, e.isStart, best.Value);
				RefreshLogicNeighbors(t);
				if (best.Value.track != null && !best.Value.IsJunctionIn) RefreshLogicNeighbors(best.Value.track);
				Main.Log($"Automatisch verbunden: {t.name} ({(e.isStart ? "Anfang" : "Ende")}) → {(best.Value.IsJunctionIn ? "Weichenanfang" : best.Value.track!.name)} ({Mathf.Sqrt(bestD):0.000} m)");
				n++;
			}
			if (n > 0) TrackEnds.MarkDirty();
			return n;
		}

		/// <summary>Verbindet ein Ende von <paramref name="t"/> mit einem freien Ende (Gleis oder Weichenanfang).</summary>
		internal static void Connect(RailTrack t, bool tAtStart, TrackEnd end)
		{
			if (end.junction != null)
			{
				// Anschluss an den Weichenanfang: das Gleis fährt in die Weiche hinein
				if (tAtStart) { t.inJunction = end.junction; t.inBranch = new Junction.Branch(); }
				else { t.outJunction = end.junction; t.outBranch = new Junction.Branch(); }
				end.junction.inBranch = new Junction.Branch(t, tAtStart);
				return;
			}
			if (end.track != null) Link(t, tAtStart, end.track, end.isStart);
		}

		/// <summary>Verbindet zwei Gleisenden in beide Richtungen.</summary>
		internal static void Link(RailTrack a, bool aAtStart, RailTrack b, bool bAtStart)
		{
			// Branch.first = true, wenn das verbundene Gleis an seinem Anfang angeschlossen ist
			var toB = new Junction.Branch(b, bAtStart);
			var toA = new Junction.Branch(a, aAtStart);
			if (aAtStart) a.inBranch = toB; else a.outBranch = toB;
			if (bAtStart) b.inBranch = toA; else b.outBranch = toA;
		}

		/// <summary>Löst alle Verbindungen eines Gleises, auch zu Weichen.</summary>
		internal static void Unlink(RailTrack removed)
		{
			foreach (var b in new[] { removed.inBranch, removed.outBranch })
			{
				RailTrack? n = b?.track;
				if (n == null) continue;
				if (n.inBranch?.track == removed) n.inBranch = new Junction.Branch();
				if (n.outBranch?.track == removed) n.outBranch = new Junction.Branch();
			}
			foreach (var j in new[] { removed.inJunction, removed.outJunction })
			{
				if (j != null && j.inBranch?.track == removed) j.inBranch = new Junction.Branch();
			}
			removed.inBranch = new Junction.Branch();
			removed.outBranch = new Junction.Branch();
		}

		// ------------------------------------------------------------------
		// Registrieren (auch für die Gleise in Weichen)
		// ------------------------------------------------------------------

		internal static void RegisterTrack(RailTrack track)
		{
			var registry = GameAccess.Registry!;
			EquiPointSet ps = track.GetKinkedPointSet();

			if (s_vanillaOrdered == null) s_vanillaOrdered = registry.OrderedRailtracks.Where(t => !IsOwn(t)).ToArray();
			if (!s_registered.Contains(track)) s_registered.Add(track);
			ApplyOrder();
			if (GameAccess.RegAllTracks!.GetValue(registry) is RailTrack[] all && !all.Contains(track))
				GameAccess.RegAllTracks.SetValue(registry, Append(all, track));
			if (!GameAccess.MeshReady) s_builtBeforeMeshGen = true;

			var logic = new Track(ps.span, IdGenerator.Instance.GenerateGenericTrackID());
			RailTrackRegistry.RailTrackToLogicTrack[track] = logic;
			RailTrackRegistry.LogicToRailTrack[logic] = track;

			if (track.generateMeshes && track.baseType != null) AddToMeshGenerator(track, ps);

			// Wie RailTrackRegistry.CreateLogicTracks: Komponente, die Logik-Verbindungen nachführt
			// (Drehscheiben und Weichen rufen sie auf jedem Gleis auf)
			LodSupport.CreateFor(track); // Fern-Ansicht

			if (!track.TryGetComponent(out RailTrackLogicTrackSwitching sw)) sw = track.gameObject.AddComponent<RailTrackLogicTrackSwitching>();
			try { sw.Init(); } catch (Exception e) { Main.Log($"Logik-Verbindung {track.name}: {e.Message}"); }
			RefreshLogicNeighbors(track);
		}

		/// <summary>Logik-Verbindungen eines Gleises und seiner Nachbarn neu berechnen (nach Verbinden/Lösen).</summary>
		internal static void RefreshLogicNeighbors(RailTrack track)
		{
			// Auch die Gleise der Weichen an beiden Enden: hängt ein Gleis am Weichenanfang, kennen die beiden
			// Weichengleise es sonst nicht als möglichen Nachbarn (PossibleIn/OutTracks). Dann meldet der TrainsOptimizer
			// des Spiels tausendfach „Can't detect connection …“ (Debug.LogError = teure Ruckler).
			var list = new List<RailTrack?> { track, track.inBranch?.track, track.outBranch?.track };
			foreach (var j in new[] { track.inJunction, track.outJunction })
			{
				if (j == null) continue;
				list.Add(j.inBranch?.track);
				if (j.outBranches != null) foreach (var b in j.outBranches) list.Add(b?.track);
			}
			foreach (var t in list.Distinct())
			{
				if (t == null || !t.TryGetComponent(out RailTrackLogicTrackSwitching sw)) continue;
				if (!RailTrackRegistry.RailTrackToLogicTrack.ContainsKey(t)) continue; // Weichengleis noch ohne Logik (vor dem Spiel-Init)
				try { sw.UpdateLogicInTrackConnection(); sw.UpdateLogicOutTrackConnection(); }
				catch (Exception e) { Main.Log($"Logik-Verbindung {t.name}: {e.Message}"); }
			}
		}

		/// <summary>Hängt ein Gleis an einer eigenen Weiche (Gleise in Weichen nie entfernen o. Ä.)?</summary>
		internal static bool TouchesJunction(RailTrack t) => t.inJunction != null || t.outJunction != null;

		internal static void UnregisterTrack(RailTrack track)
		{
			var registry = GameAccess.Registry;
			s_registered.Remove(track);
			if (registry != null)
			{
				if (s_vanillaOrdered != null) ApplyOrder(exclude: track);
				else if (GameAccess.RegOrderedTracks!.GetValue(registry) is RailTrack[] ordered)
					GameAccess.RegOrderedTracks.SetValue(registry, ordered.Where(t => t != track).ToArray());
				if (GameAccess.RegAllTracks!.GetValue(registry) is RailTrack[] all)
					GameAccess.RegAllTracks.SetValue(registry, all.Where(t => t != track).ToArray());
			}
			if (RailTrackRegistry.RailTrackToLogicTrack.TryGetValue(track, out Track logic))
			{
				RailTrackRegistry.RailTrackToLogicTrack.Remove(track);
				RailTrackRegistry.LogicToRailTrack.Remove(logic);
			}
			RemoveFromMeshGenerator(track);
			LodSupport.RemoveFor(track);
		}

		private static T[] Append<T>(T[] arr, T item)
		{
			var res = new T[arr.Length + 1];
			Array.Copy(arr, res, arr.Length);
			res[arr.Length] = item;
			return res;
		}

		/// <summary>True, wenn ein Fahrzeug auf dem Gleis steht.</summary>
		internal static bool HasVehicles(RailTrack t) =>
			t != null && t.TryGetComponent(out RailTrackBogiesOnTrack onTrack) && onTrack.bogiesOnTrack.Count > 0;

		// ------------------------------------------------------------------
		// Reihenfolge in der Registry (Spielstand-Kompatibilität)
		// ------------------------------------------------------------------

		/// <summary>
		/// Der Spielstand speichert für jeden Wagen den INDEX seines Gleises in OrderedRailtracks. Damit Wagen auf
		/// eigenen Gleisen nach dem Laden wieder auf demselben Gleis stehen, hat die Liste immer dieselbe Form:
		/// Spielgleise (Original-Reihenfolge), dann Weichengleise in JSON-Reihenfolge, dann Gleise in JSON-Reihenfolge.
		/// </summary>
		internal static void ApplyOrder(RailTrack? exclude = null)
		{
			var registry = GameAccess.Registry;
			if (registry == null || s_vanillaOrdered == null || GameAccess.RegOrderedTracks == null) return;
			var own = new List<RailTrack>();
			var regSet = new HashSet<RailTrack>(s_registered);
			void Take(RailTrack? t) { if (t != null && t != exclude && regSet.Remove(t)) own.Add(t); }
			foreach (var sw in LayoutStore.Layout.switches)
				if (JunctionBuilder.Built.TryGetValue(sw.id, out BuiltSwitch b)) { Take(b.through); Take(b.diverge); }
			foreach (var pt in LayoutStore.Layout.tracks)
				if (s_built.TryGetValue(pt.id, out RailTrack r)) Take(r);
			foreach (var t in s_registered) if (regSet.Contains(t) && t != exclude && t != null) own.Add(t); // noch nicht in der JSON
			var arr = new RailTrack[s_vanillaOrdered.Length + own.Count];
			Array.Copy(s_vanillaOrdered, arr, s_vanillaOrdered.Length);
			for (int i = 0; i < own.Count; i++) arr[s_vanillaOrdered.Length + i] = own[i];
			GameAccess.RegOrderedTracks.SetValue(registry, arr);
		}

		/// <summary>
		/// Vor dem Laden der Wagen aus dem Spielstand (Harmony-Prefix auf CarsSaveManager.Load): alle eigenen Gleise
		/// schon jetzt bauen, damit die gespeicherten Gleis-Indizes existieren. Meshes trägt der Mesh-Generator bei
		/// seinem Start selbst ein.
		/// </summary>
		public static void EarlyBuild()
		{
			try
			{
				EnsureWorld();
				if (!GameAccess.TracksReady) { Main.Log("Früher Aufbau: Gleis-Registry noch leer – übersprungen."); return; }
				if (LayoutStore.Layout.tracks.Count == 0 && LayoutStore.Layout.switches.Count == 0) return;
				BuildAll(out string msg);
				Main.Log("Früher Aufbau vor dem Laden der Wagen: " + msg);
				// Bahnhofs-IDs gleich mit, damit Jobs auf eigenen Gleisen beim Laden ihr Gleis finden
				if (!StationLink.ApplyStationsOnce("früh, vor den Wagen")) Main.Log("Früher Aufbau: Bahnhöfe noch nicht bereit – Gleis-IDs folgen vor dem Laden der Jobs.");
			}
			catch (Exception e) { Main.Log("Früher Aufbau fehlgeschlagen: " + e); }
		}

		/// <summary>Nach dem Start des Mesh-Generators: Schwellen bei „Nur Schienen“-Gleisen wieder entfernen
		/// (der Generator hat beim Start alle aktiven Gleise mit Schwellen eingetragen).</summary>
		public static void AfterMeshGeneratorStarted()
		{
			if (!s_builtBeforeMeshGen || !GameAccess.MeshReady) return;
			s_builtBeforeMeshGen = false;
			if (s_noSleepers.Count == 0) return;
			var gen = GameAccess.MeshGenerator!;
			if (!(GameAccess.MeshSpatialHash!.GetValue(gen) is TrackChunkSpatialHash hash)) return;
			int n = 0;
			if (GameAccess.HashLookupCells!.GetValue(hash) is Dictionary<Vector2Int, List<TrackChunk>> cells)
				foreach (var list in cells.Values) n += list.RemoveAll(c => c.isSleepers && c.track != null && s_noSleepers.Contains(c.track));
			if (GameAccess.MeshActiveChunks!.GetValue(gen) is Dictionary<Vector2Int, List<TrackChunk>> active)
				foreach (var list in active.Values) list.RemoveAll(c => c.isSleepers && c.track != null && s_noSleepers.Contains(c.track));
			GameAccess.MeshPrevCell!.SetValue(gen, new Vector2Int(int.MinValue, int.MinValue));
			Main.Log($"Schwellen von {s_noSleepers.Count} „Nur Schienen“-Gleisen entfernt ({n} Abschnitte).");
		}

		// ------------------------------------------------------------------
		// Verbindungen prüfen (Sicherheitsnetz gegen „Teleportieren“ von Zügen)
		// ------------------------------------------------------------------

		/// <summary>
		/// Prüft beide Enden: zeigt eine Verbindung auf ein Gleisende, das nicht an derselben Stelle liegt, würde ein
		/// Drehgestell beim Übergang springen (Zug fliegt weg). Solche Verbindungen werden gelöst und geloggt;
		/// fehlende Rückverbindungen werden ergänzt.
		/// </summary>
		internal static int VerifyConnections(RailTrack t)
		{
			int bad = 0;
			if (t == null || t.curve == null || t.curve.pointCount < 2) return 0;
			foreach (bool atStart in new[] { true, false })
			{
				Vector3 my = atStart ? t.curve[0].position : t.curve.Last().position;
				Junction.Branch? br = atStart ? t.inBranch : t.outBranch;
				if (br?.track != null && br.track.curve != null && br.track.curve.pointCount >= 2)
				{
					Vector3 other = br.first ? br.track.curve[0].position : br.track.curve.Last().position;
					float d = Vector3.Distance(my, other);
					if (d > 0.3f)
					{
						bad++;
						Main.Log($"FEHLER Verbindung {t.name} ({(atStart ? "Anfang" : "Ende")}) → {br.track.name} ({(br.first ? "Anfang" : "Ende")}): Enden {d:0.00} m auseinander – gelöst.");
						var n = br.track;
						if (n.inBranch?.track == t) n.inBranch = new Junction.Branch();
						if (n.outBranch?.track == t) n.outBranch = new Junction.Branch();
						if (atStart) t.inBranch = new Junction.Branch(); else t.outBranch = new Junction.Branch();
						RefreshLogicNeighbors(t); RefreshLogicNeighbors(n);
					}
					else
					{
						Junction.Branch? back = br.first ? br.track.inBranch : br.track.outBranch;
						bool backJunction = br.first ? br.track.inJunction != null : br.track.outJunction != null;
						if (!backJunction && (back?.track != t || back.first != atStart))
						{
							Main.Log($"Verbindung {br.track.name} → {t.name}: Rückverbindung fehlte/falsch – ergänzt.");
							if (br.first) br.track.inBranch = new Junction.Branch(t, atStart); else br.track.outBranch = new Junction.Branch(t, atStart);
							RefreshLogicNeighbors(br.track);
						}
					}
				}
				Junction? j = atStart ? t.inJunction : t.outJunction;
				if (j != null && j.inBranch?.track == t && j.outBranches != null)
				{
					// Weichenanfang muss dort liegen, wo die Weichengleise beginnen
					var ob = j.outBranches.FirstOrDefault(x => x?.track != null && x.track.curve != null && x.track.curve.pointCount >= 2);
					if (ob != null)
					{
						Vector3 jp = ob.first ? ob.track.curve[0].position : ob.track.curve.Last().position;
						float dj = Vector3.Distance(my, jp);
						if (dj > 0.3f)
						{
							bad++;
							Main.Log($"FEHLER Weichenanschluss {t.name} ({(atStart ? "Anfang" : "Ende")}) → {j.name}: {dj:0.00} m entfernt – gelöst.");
							j.inBranch = new Junction.Branch();
							if (atStart) { t.inJunction = null; t.inBranch = new Junction.Branch(); }
							else { t.outJunction = null; t.outBranch = new Junction.Branch(); }
							RefreshLogicNeighbors(t);
							continue;
						}
					}
				}
				if (j != null && j.inBranch?.track == t && j.inBranch.first != atStart)
				{
					bad++;
					Main.Log($"FEHLER Weichenanfang an {t.name}: falsches Ende eingetragen – korrigiert.");
					j.inBranch = new Junction.Branch(t, atStart);
				}
			}
			return bad;
		}

		/// <summary>Alle eigenen Gleise prüfen (nach dem Laden).</summary>
		internal static int VerifyAll()
		{
			int bad = 0;
			foreach (var t in s_registered.ToList()) bad += VerifyConnections(t);
			return bad;
		}

		// ------------------------------------------------------------------
		// Mesh-Generator: Schienen/Schotter und Schwellen, genau wie RailwayMeshGenerator.Start
		// ------------------------------------------------------------------

		/// <summary>
		/// Fügt die Chunks eines Gleises in den Mesh-Generator ein und erzeugt NUR deren Meshes.
		/// Bereits sichtbare Zellen bekommen die neuen Chunks direkt (keine Neuerzeugung der Spielgleise dort);
		/// neue Zellen und der Schwellen-Puffer werden vom Generator beim nächsten Update ergänzt.
		/// </summary>
		private static void AddToMeshGenerator(RailTrack track, EquiPointSet ps)
		{
			if (!GameAccess.MeshReady) return; // Generator startet noch und trägt alle aktiven Gleise selbst ein
			var gen = GameAccess.MeshGenerator!;
			var hash = (TrackChunkSpatialHash)GameAccess.MeshSpatialHash!.GetValue(gen);

			var added = new Dictionary<TrackChunk, Vector2Int>();
			void Put(EquiPointSet set, EquiPointSet.Point p, bool sleepers)
			{
				TrackChunk c = hash.Add(set, p);
				c.isSleepers = sleepers;
				c.track = track;
				if (!added.ContainsKey(c)) added[c] = hash.GetCellID((Vector3)p.position);
			}

			float sd = track.baseType.sleeperDistance;
			GameAccess.HashDoneAdding!.SetValue(hash, false);
			foreach (var p in ps.points) Put(ps, p, false);
			if (sd > 0.01f && !s_noSleepers.Contains(track))
			{
				EquiPointSet sleepers = EquiPointSet.ResampleEquidistant(ps, sd, sd * 0.5f, true, true);
				foreach (var p in sleepers.points) Put(sleepers, p, true);
			}
			hash.DoneAdding(); // führt neue Zellen mit den bestehenden zusammen

			bool direct = GameAccess.MeshScheduleChunk != null;
			if (direct && GameAccess.MeshActiveChunks!.GetValue(gen) is Dictionary<Vector2Int, List<TrackChunk>> active)
			{
				foreach (var kv in added)
				{
					if (!active.TryGetValue(kv.Value, out var list)) continue; // Zelle (noch) nicht sichtbar → Generator macht das
					list.Add(kv.Key);
					if (kv.Key.isSleepers) continue;
					try { GameAccess.MeshScheduleChunk!.Invoke(gen, new object[] { kv.Key }); }
					catch (Exception e) { Main.Log("Chunk-Mesh: " + (e.InnerException ?? e).Message); }
				}
			}
			else if (!direct)
			{
				ForceMeshRefresh(gen, CellsOf(hash, track)); // Fallback: ganze Zellen neu
				return;
			}
			// Generator neu suchen lassen: neue Zellen + Schwellen-Puffer (wie beim normalen Zellwechsel)
			GameAccess.MeshPrevCell!.SetValue(gen, new Vector2Int(int.MinValue, int.MinValue));
		}

		/// <summary>Rasterzellen des Mesh-Generators, die ein Gleis berührt (inkl. Nachbarzellen).</summary>
		private static HashSet<Vector2Int> CellsOf(TrackChunkSpatialHash hash, RailTrack track)
		{
			var cells = new HashSet<Vector2Int>();
			var ps = track.GetKinkedPointSet();
			if (ps?.points == null) return cells;
			for (int i = 0; i < ps.points.Length; i += 4)
			{
				Vector2Int c = hash.GetCellID((Vector3)ps.points[i].position);
				for (int dx = -1; dx <= 1; dx++)
					for (int dy = -1; dy <= 1; dy++)
						cells.Add(new Vector2Int(c.x + dx, c.y + dy));
			}
			if (ps.points.Length > 0) cells.Add(hash.GetCellID((Vector3)ps.points[ps.points.Length - 1].position));
			return cells;
		}

		internal static void RemoveMeshesOf(RailTrack track) => RemoveFromMeshGenerator(track);

		/// <summary>Nur die Chunks dieses Gleises freigeben – die übrigen Gleise der Zellen bleiben, wie sie sind.</summary>
		private static void RemoveFromMeshGenerator(RailTrack track)
		{
			var gen = GameAccess.MeshGenerator;
			if (gen == null) return;
			var hash = GameAccess.MeshSpatialHash!.GetValue(gen) as TrackChunkSpatialHash;
			if (hash == null) return;
			var touched = CellsOf(hash, track);
			if (GameAccess.HashLookupCells!.GetValue(hash) is Dictionary<Vector2Int, List<TrackChunk>> cells)
				foreach (var id in touched)
					if (cells.TryGetValue(id, out var list)) list.RemoveAll(c => c.track == track);
			if (GameAccess.MeshActiveChunks!.GetValue(gen) is Dictionary<Vector2Int, List<TrackChunk>> active)
			{
				foreach (var id in touched)
				{
					if (!active.TryGetValue(id, out var list)) continue;
					foreach (var c in list)
						if (c.track == track)
							try { c.ReleasePoolObjects(); } catch (Exception e) { Main.Log("Chunk freigeben: " + e.Message); }
					list.RemoveAll(c => c.track == track);
				}
			}
			// Schwellen-Puffer neu füllen lassen
			GameAccess.MeshPrevCell!.SetValue(gen, new Vector2Int(int.MinValue, int.MinValue));
		}

		/// <summary>Fallback: aktive Chunks ganzer Zellen freigeben, der Generator baut sie neu.</summary>
		private static void ForceMeshRefresh(RailwayMeshGenerator gen, HashSet<Vector2Int> cells)
		{
			if (GameAccess.MeshActiveChunks!.GetValue(gen) is Dictionary<Vector2Int, List<TrackChunk>> active)
			{
				foreach (var id in cells)
				{
					if (!active.TryGetValue(id, out var list)) continue;
					foreach (var c in list) c.ReleasePoolObjects();
					active.Remove(id);
				}
			}
			GameAccess.MeshPrevCell!.SetValue(gen, new Vector2Int(int.MinValue, int.MinValue));
		}

		// ------------------------------------------------------------------
		// Entfernen
		// ------------------------------------------------------------------

		public static bool Remove(string id, out string error)
		{
			error = "";
			if (s_built.TryGetValue(id, out RailTrack t0) && t0 != null && HasVehicles(t0)) { error = Tr.T("Fahrzeug auf dem Gleis", "Vehicle on the track"); return false; }
			OverlayBuilder.Remove(id);
			VegetationBuilder.RemoveTrack(id);
			if (!s_built.TryGetValue(id, out RailTrack track) || track == null)
			{
				s_built.Remove(id);
				return true; // nicht gebaut → nur aus der Datei löschen
			}
			if (HasVehicles(track)) { error = Tr.T("Fahrzeug auf dem Gleis", "Vehicle on the track"); return false; }

			var swr = System.Diagnostics.Stopwatch.StartNew();
			try
			{
				StationLink.Unapply(id);
				MachineBuilder.OnTrackRemoved(id);
				var neighbors = new[] { track.inBranch?.track, track.outBranch?.track };
				Unlink(track);
				UnregisterTrack(track);
				foreach (var n in neighbors) if (n != null) RefreshLogicNeighbors(n);
				Materials.Remove(track);
				s_noSleepers.Remove(track);
				var ends = TurntableSupport.EndsOf(track);
				Object.Destroy(track.gameObject); // OnDestroy entfernt das PointSet aus RailTrack.pointSets
				s_built.Remove(id);
				TrackEnds.MarkDirty();
				// Drehscheiben (nur die betroffenen) erst im nächsten Frame neu suchen lassen, das Gleis ist dann weg
				WorldLoader.RunNextFrame(() => TurntableSupport.RefreshNear(ends));
				Main.Log($"Gleis {id} entfernt – {swr.ElapsedMilliseconds} ms");
				return true;
			}
			catch (Exception e)
			{
				error = e.GetType().Name + ": " + e.Message;
				Main.Log($"Entfernen von {id} fehlgeschlagen: {e}");
				return false;
			}
		}
	}
}
