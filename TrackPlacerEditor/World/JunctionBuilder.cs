using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DV.CabControls;
using TrackPlacer.Data;
using TrackPlacer.Geometry;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TrackPlacer.World
{
	/// <summary>Eine Weichenvorlage aus der Spielwelt, mit ihrer Geometrie relativ zum Weichenanfang.</summary>
	internal sealed class SwitchVariant
	{
		public string prefabName = "";
		public GameObject template = null!;
		public bool divergesLeft;
		public bool leverLeft;
		/// <summary>Liegt der Weichenstoß am Anfang (curve[0]) des jeweiligen Weichengleises? Sonst am Ende.</summary>
		public bool throughJunctionAtStart = true;
		public bool divergeJunctionAtStart = true;
		/// <summary>Geometrie im lokalen Raum des Weichenanfangs (Ursprung = Stoß, +Z = Fahrtrichtung).</summary>
		public List<Anchor> through = new List<Anchor>();
		public List<Anchor> diverge = new List<Anchor>();
		/// <summary>Pose der Vorlage relativ zum Weichenanfang (zum Platzieren des Klons).</summary>
		public Vector3 rootPosLocal;
		public Quaternion rootRotLocal;

		public string Label => Tr.T($"Weiche {(divergesLeft ? "links" : "rechts")}, Hebel {(leverLeft ? "links" : "rechts")}", $"Switch {(divergesLeft ? "left" : "right")}, lever {(leverLeft ? "left" : "right")}");

		public static Quaternion YawRotation(float yawDeg) => Quaternion.Euler(0f, yawDeg, 0f);

		/// <summary>Geometrie in statischen Weltkoordinaten für eine Platzierung.</summary>
		public List<Anchor> Transform(List<Anchor> local, Vector3 origin, float yawDeg)
		{
			Quaternion r = YawRotation(yawDeg);
			return local.Select(a => new Anchor { position = origin + r * a.position, handle1 = r * a.handle1, handle2 = r * a.handle2 }).ToList();
		}
	}

	internal sealed class BuiltSwitch
	{
		public GameObject root = null!;
		public Junction junction = null!;
		public RailTrack through = null!;
		public RailTrack diverge = null!;
		public bool throughJunctionAtStart = true;
		public bool divergeJunctionAtStart = true;

		/// <summary>Freies (abgewandtes) Ende des Gleises: ist es dessen Anfang?</summary>
		public bool FarIsStart(RailTrack t) => t == through ? !throughJunctionAtStart : !divergeJunctionAtStart;
	}

	/// <summary>
	/// Weichen sind feste Modelle im Spiel. Wir klonen sie: inaktiv duplizieren, an die gewünschte Stelle drehen,
	/// Verbindungen und Junction-Daten neu setzen, dann aktivieren. Hebel, Animation und Weichenlogik bringt
	/// das Original mit (VisualSwitch + Junction). Das Auffahren einer falsch gestellten Weiche stellt das Spiel selbst um.
	/// Unsere Weichen kommen NICHT in die Weichenliste der Registry, damit die Weichen-Speicherdaten des
	/// Spielstands nicht verrutschen. Die Stellung speichern wir selbst in der JSON.
	/// </summary>
	internal static class JunctionBuilder
	{
		private const string NamePrefix = "[TP]_junc_";
		private static readonly FieldInfo? FTrackInitialized = typeof(RailTrack).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic);
		private static readonly FieldInfo? FTrackPointSet = typeof(RailTrack).GetField("pointSet", BindingFlags.Instance | BindingFlags.NonPublic);
		private static readonly FieldInfo? FSwitchButtonGO = typeof(VisualSwitch).GetField("switchButtonGO", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

		private static readonly Dictionary<string, BuiltSwitch> s_built = new Dictionary<string, BuiltSwitch>();
		private static readonly HashSet<RailTrack> s_ownTracks = new HashSet<RailTrack>();
		private static List<SwitchVariant>? s_variants;
		private static int s_counter;

		public static IReadOnlyDictionary<string, BuiltSwitch> Built => s_built;
		public static bool IsOwnJunctionTrack(RailTrack t) => s_ownTracks.Contains(t);
		public static int SelectedVariant;

		public static void OnWorldUnloaded()
		{
			s_built.Clear();
			s_ownTracks.Clear();
			s_variants = null;
			s_takenIds = null;
		}

		// ------------------------------------------------------------------
		// Weichen-ID wie bei Spielweichen („S-0406-HB“): S-<Nummer>-<Bahnhof>
		// ------------------------------------------------------------------

		private const float StationRange = 2000f;
		private static HashSet<string>? s_takenIds;
		private static readonly List<(Vector3 pos, string id)> s_vanillaJunctions = new List<(Vector3, string)>();
		private static readonly Regex SwitchIdPattern = new Regex(@"^([SW])-\d+(?:-([A-Z0-9]+))?$", RegexOptions.IgnoreCase);
		private static bool s_saveQueued;

		private static HashSet<string> TakenIds()
		{
			if (s_takenIds != null) return s_takenIds;
			s_takenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			s_vanillaJunctions.Clear();
			foreach (var j in Object.FindObjectsOfType<Junction>())
				try
				{
					string? jid = j != null ? j.junctionData.junctionIdLong : null;
					if (!string.IsNullOrEmpty(jid))
					{
						s_takenIds.Add(jid!);
						if (!j!.name.StartsWith(NamePrefix) && (j.transform.parent == null || !j.transform.parent.name.StartsWith(NamePrefix)))
							s_vanillaJunctions.Add((j.transform.position - GameAccess.OriginShift, jid!));
					}
				}
				catch (NullReferenceException) { /* Weiche ohne Daten */ }
			foreach (var sw in LayoutStore.Layout.switches)
				if (!string.IsNullOrEmpty(sw.switchId)) s_takenIds.Add(sw.switchId);
			return s_takenIds;
		}

		/// <summary>Kürzel des nächsten Bahnhofs (≤ range), sonst "".</summary>
		private static string NearestYard(Vector3 staticPos, float range = StationRange)
		{
			string best = "";
			float bestD = range * range;
			Vector3 world = staticPos + GameAccess.OriginShift;
			if (StationController.allStations == null) return best;
			foreach (var st in StationController.allStations)
			{
				string? yard = st != null ? st.stationInfo?.YardID : null;
				if (string.IsNullOrEmpty(yard)) continue;
				float d = (st!.transform.position - world).sqrMagnitude;
				if (d < bestD) { bestD = d; best = yard!; }
			}
			return best;
		}

		/// <summary>ID der eigenen Weiche; beim ersten Bau vergeben (nächster Bahnhof, freie Nummer ab 9001) und gespeichert.</summary>
		/// <summary>
		/// Art der Weiche wie im Spiel: im Bahnhof „S-0406-HB“, auf freier Strecke „W-0416“. Entscheidend ist die
		/// nächste Spielweiche (≤ 400 m): S mit Bahnhof → S-…-gleicher Bahnhof, W → W. Ohne Spielweiche in der Nähe:
		/// Bahnhof ≤ 1 km → S, sonst W.
		/// </summary>
		private static (string kind, string yard) KindFor(Vector3 staticPos)
		{
			float best = 400f * 400f;
			string? nearId = null;
			foreach (var (pos, id) in s_vanillaJunctions)
			{
				float d = (pos - staticPos).sqrMagnitude;
				if (d < best) { best = d; nearId = id; }
			}
			if (nearId != null)
			{
				var m = SwitchIdPattern.Match(nearId);
				if (m.Success)
				{
					string kind = m.Groups[1].Value.ToUpperInvariant();
					if (kind == "S" && m.Groups[2].Success) return ("S", m.Groups[2].Value.ToUpperInvariant());
					if (kind == "W") return ("W", "");
				}
			}
			string yard = NearestYard(staticPos, 1000f);
			return yard.Length > 0 ? ("S", yard) : ("W", "");
		}

		/// <summary>ID der eigenen Weiche; beim ersten Bau vergeben (freie Nummer ab 9001) und gespeichert.</summary>
		public static string SwitchIdFor(PlacedSwitch placed)
		{
			// gespeicherte ID behalten – außer dem alten Format aus 0.11.11 ohne Bahnhof („S-9001“)
			if (!string.IsNullOrEmpty(placed.switchId) && !Regex.IsMatch(placed.switchId, @"^S-\d+$")) return placed.switchId;
			// Bahnhöfe noch nicht da (sehr früh)? Dann vorläufige ID, nichts festschreiben.
			if (StationController.allStations == null || StationController.allStations.Count == 0)
				return "TP-" + placed.id.Substring(0, Math.Min(6, placed.id.Length)).ToUpperInvariant();
			var taken = TakenIds();
			if (!string.IsNullOrEmpty(placed.switchId)) taken.Remove(placed.switchId);
			var (kind, yard) = KindFor(placed.Position);
			for (int n = 9001; n <= 9999; n++)
			{
				string id = kind == "S" ? $"S-{n:D4}-{yard}" : $"W-{n:D4}";
				if (taken.Contains(id)) continue;
				taken.Add(id);
				placed.switchId = id;
				if (!s_saveQueued)
				{
					s_saveQueued = true;
					WorldLoader.RunNextFrame(() => { s_saveQueued = false; LayoutStore.SaveQuiet(); });
				}
				return id;
			}
			return "TP-" + placed.id.Substring(0, Math.Min(6, placed.id.Length)).ToUpperInvariant();
		}

		// ------------------------------------------------------------------
		// Vorlagen finden
		// ------------------------------------------------------------------

		public static IReadOnlyList<SwitchVariant> Variants
		{
			get
			{
				if (s_variants != null) return s_variants;
				var list = new List<SwitchVariant>();
				if (!GameAccess.TracksReady) return list;

				var seen = new HashSet<string>();
				foreach (Junction j in Object.FindObjectsOfType<Junction>())
				{
					if (j == null || j.name.StartsWith(NamePrefix)) continue;
					Transform group = j.transform.parent != null ? j.transform.parent : j.transform;
					if (group.name.StartsWith(NamePrefix)) continue;
					string baseName = BaseName(group.name);
					if (seen.Contains(baseName)) continue;

					var v = Analyse(group, baseName);
					if (v == null) continue;
					seen.Add(baseName);
					list.Add(v);
					Main.Log($"Weichen-Vorlage: {baseName} → {v.Label}");
				}
				s_variants = list.OrderBy(v => v.divergesLeft ? 0 : 1).ThenBy(v => v.leverLeft ? 0 : 1).ThenBy(v => v.prefabName).ToList();
				return s_variants;
			}
		}

		public static SwitchVariant? CurrentVariant
		{
			get
			{
				var all = Variants;
				if (all.Count == 0) return null;
				if (SelectedVariant < 0 || SelectedVariant >= all.Count) SelectedVariant = 0;
				return all[SelectedVariant];
			}
		}

		/// <summary>"junc-left (12)" → "junc-left"</summary>
		private static string BaseName(string n) => Regex.Replace(n, @"\s*\(\d+\)$", "").Trim();

		private static (RailTrack? through, RailTrack? diverge) FindTracks(Transform group)
		{
			RailTrack? th = null, dv = null;
			foreach (var t in group.GetComponentsInChildren<RailTrack>(true))
			{
				if (t.name == RailTrack.JUNCTION_THROUGH_TRACK_NAME) th = t;
				else if (t.name == RailTrack.JUNCTION_DIVERGING_TRACK_NAME) dv = t;
			}
			return (th, dv);
		}

		private static SwitchVariant? Analyse(Transform group, string baseName)
		{
			var (th, dv) = FindTracks(group);
			if (th == null || dv == null || th.curve == null || dv.curve == null || th.curve.pointCount < 2 || dv.curve.pointCount < 2) return null;

			// Der Weichenstoß ist der Punkt, den beide Weichengleise gemeinsam haben. Je nach Vorlage
			// ist das der Anfang oder das Ende der Kurve – nicht davon ausgehen, sondern messen.
			Vector3 t0 = th.curve[0].position, tN = th.curve.Last().position;
			Vector3 d0 = dv.curve[0].position, dN = dv.curve.Last().position;
			var pairs = new (bool thStart, bool dvStart, float dist)[]
			{
				(true, true, (t0 - d0).sqrMagnitude), (true, false, (t0 - dN).sqrMagnitude),
				(false, true, (tN - d0).sqrMagnitude), (false, false, (tN - dN).sqrMagnitude),
			};
			var best = pairs.OrderBy(p => p.dist).First();
			bool thAtStart = best.thStart, dvAtStart = best.dvStart;

			BezierPoint jp = thAtStart ? th.curve[0] : th.curve.Last();
			Vector3 origin = jp.position;
			// Richtung vom Stoß ins Gleis hinein
			Vector3 fwd = thAtStart ? jp.globalHandle2 - jp.position : jp.globalHandle1 - jp.position;
			fwd.y = 0f;
			if (fwd.sqrMagnitude < 1e-6f) return null;
			Quaternion frame = Quaternion.LookRotation(fwd.normalized, Vector3.up);
			Quaternion inv = Quaternion.Inverse(frame);

			// Ankerpunkte immer vom Stoß weg geordnet (handle2 = vom Stoß weg)
			List<Anchor> ToLocal(BezierCurve c, bool atStart)
			{
				var res = new List<Anchor>();
				for (int k = 0; k < c.pointCount; k++)
				{
					BezierPoint p = c[atStart ? k : c.pointCount - 1 - k];
					Vector3 h1 = p.globalHandle1 - p.position, h2 = p.globalHandle2 - p.position;
					if (!atStart) (h1, h2) = (h2, h1);
					res.Add(new Anchor { position = inv * (p.position - origin), handle1 = inv * h1, handle2 = inv * h2 });
				}
				return res;
			}

			var v = new SwitchVariant
			{
				prefabName = baseName,
				template = group.gameObject,
				through = ToLocal(th.curve, thAtStart),
				diverge = ToLocal(dv.curve, dvAtStart),
				rootPosLocal = inv * (group.position - origin),
				rootRotLocal = inv * group.rotation,
				throughJunctionAtStart = thAtStart,
				divergeJunctionAtStart = dvAtStart,
			};
			v.divergesLeft = v.diverge[v.diverge.Count - 1].position.x < 0f;

			Transform? lever = FindRecursive(group, "switch_lever");
			if (lever == null)
			{
				var bb = group.GetComponentsInChildren<ButtonBase>(true).FirstOrDefault();
				lever = bb != null ? bb.transform : null;
			}
			v.leverLeft = lever != null && (inv * (lever.position - origin)).x < 0f;
			Main.Log($"  {baseName}: Stoß am {(thAtStart ? "Anfang" : "Ende")} (gerade) / {(dvAtStart ? "Anfang" : "Ende")} (Abzweig)");
			return v;
		}

		private static Transform? FindRecursive(Transform t, string name)
		{
			if (t.name == name) return t;
			for (int i = 0; i < t.childCount; i++)
			{
				var r = FindRecursive(t.GetChild(i), name);
				if (r != null) return r;
			}
			return null;
		}

		// ------------------------------------------------------------------
		// Bauen
		// ------------------------------------------------------------------

		/// <summary>Nächste gesetzte Weiche bekommt die Asphalt-Abdeckung (Menü „Abgedeckte Weichen“).</summary>
		public static bool PlaceCovered { get; set; }

		public static bool Build(PlacedSwitch placed, out string error)
		{
			error = "";
			if (s_built.ContainsKey(placed.id)) return true;
			if (!TrackBuilder.CheckReady(out error)) return false;
			if (FTrackInitialized == null || FTrackPointSet == null) { error = "RailTrack-Felder fehlen (Spiel-Update?)"; return false; }

			SwitchVariant? v = Variants.FirstOrDefault(x => x.prefabName == placed.prefab);
			if (v == null) { error = $"Weichenvorlage '{placed.prefab}' fehlt"; return false; }

			GameObject? clone = null;
			var tracks = new List<RailTrack>();
			try
			{
				var registry = GameAccess.Registry!;
				TrackBuilder.FreezeHashes(registry);

				// Inaktiv klonen, damit Awake erst nach dem Umbau läuft
				bool wasActive = v.template.activeSelf;
				v.template.SetActive(false);
				clone = Object.Instantiate(v.template);
				v.template.SetActive(wasActive);
				clone.name = NamePrefix + placed.id;

				// Pose in absoluten Koordinaten: Weichenanfang auf placed.Position, Fahrtrichtung yaw
				Quaternion r = SwitchVariant.YawRotation(placed.yaw);
				clone.transform.SetPositionAndRotation(placed.Position + r * v.rootPosLocal, r * v.rootRotLocal);

				Junction junction = clone.GetComponentInChildren<Junction>(true);
				var (th, dv) = FindTracks(clone.transform);
				if (junction == null || th == null || dv == null) throw new Exception("Weichen-Klon unvollständig");
				tracks.Add(th); tracks.Add(dv);

				foreach (var t in tracks)
				{
					bool atStart = t == th ? v.throughJunctionAtStart : v.divergeJunctionAtStart;
					FTrackInitialized.SetValue(t, false);
					FTrackPointSet.SetValue(t, null);
					// Stoß-Seite hängt an der Junction (wie im Original), abgewandte Seite ist frei
					if (atStart) { t.inJunction = junction; t.inBranch = new Junction.Branch(); t.outJunction = null; t.outBranch = new Junction.Branch(); }
					else { t.outJunction = junction; t.outBranch = new Junction.Branch(); t.inJunction = null; t.inBranch = new Junction.Branch(); }
					t.generateColliders = true;
					// Alter (neu/mittel/alt): Schienen- und Schottertyp + Materialien wie bei normalen Gleisen
					if (placed.age >= 0f)
					{
						var opt = TrackTypes.Resolve("", "", "ballast", placed.age);
						if (opt != null)
						{
							t.railType = opt.rail;
							t.baseType = opt.baseType;
							t.age = opt.age;
							TrackBuilder.Materials[t] = (opt.baseType.baseMaterial, opt.rail.railMaterial);
						}
					}
				}

				// Ausgänge auf die geklonten Gleise zeigen lassen, Reihenfolge wie im Original
				Junction.Branch Out(RailTrack t) => new Junction.Branch(t, t == th ? v.throughJunctionAtStart : v.divergeJunctionAtStart);
				var outs = new List<Junction.Branch>();
				foreach (var b in junction.outBranches)
				{
					string n = b?.track != null ? b.track.name : "";
					outs.Add(Out(n == RailTrack.JUNCTION_DIVERGING_TRACK_NAME ? dv : th));
				}
				if (outs.Count != 2) outs = new List<Junction.Branch> { Out(th), Out(dv) };
				junction.outBranches = outs;
				junction.inBranch = new Junction.Branch();
				junction.defaultSelectedBranch = (short)Mathf.Clamp(placed.selectedBranch, 0, 1);

				int idx = 900000 + (++s_counter);
				junction.junctionData = new Junction.JunctionData
				{
					junctionIndex = idx,
					junctionId = idx,
					junctionIdLong = SwitchIdFor(placed),
					position = placed.Position,
				};

				PrepareLever(clone, junction);

				// Mit freien Gleisenden an Weichenanfang und den beiden Ausgängen verbinden
				var throughEnd = v.Transform(v.through, placed.Position, placed.yaw);
				var divergeEnd = v.Transform(v.diverge, placed.Position, placed.yaw);
				if (TrackEnds.FindFreeEndAt(placed.Position, TrackBuilder.ConnectTolerance, th, out TrackEnd inN) && inN.track != null && !inN.IsJunctionIn)
					TrackBuilder.Connect(inN.track, inN.isStart, JunctionInEnd(junction, placed.Position, placed.yaw));
				// Ausgänge: an Gleisende ODER an den Anfang einer anderen Weiche (Weichenstraße)
				if (TrackEnds.FindFreeEndAt(throughEnd[throughEnd.Count - 1].position, TrackBuilder.ConnectTolerance, th, out TrackEnd tN) && tN.track != null)
					TrackBuilder.Connect(th, !v.throughJunctionAtStart, tN);
				if (TrackEnds.FindFreeEndAt(divergeEnd[divergeEnd.Count - 1].position, TrackBuilder.ConnectTolerance, dv, out TrackEnd dN) && dN.track != null)
					TrackBuilder.Connect(dv, !v.divergeJunctionAtStart, dN);

				// RailTrack.Init prüft den Abstand zum Weichenanfang und stürzt bei freiem Anfang ab (Branch ohne Gleis).
				// Für die Aktivierung vorübergehend auf das eigene Durchgangsgleis zeigen lassen.
				bool inFree = junction.inBranch?.track == null;
				if (inFree) junction.inBranch = new Junction.Branch(th, v.throughJunctionAtStart);
				clone.SetActive(true); // Junction.Awake, RailTrack.Init, VisualSwitch.Start
				if (inFree) junction.inBranch = new Junction.Branch();
				TrackBuilder.MoveUnderRailwayRoot(clone.transform, registry);

				foreach (var t in tracks)
				{
					TrackBuilder.RegisterTrack(t);
					s_ownTracks.Add(t);
				}

				string id = placed.id;
				junction.Switched += (mode, branch) => LayoutStore.SetSwitchState(id, branch);

				TurntableSupport.RefreshNear(tracks.ToArray());
				s_built[placed.id] = new BuiltSwitch
				{
					root = clone, junction = junction, through = th, diverge = dv,
					throughJunctionAtStart = v.throughJunctionAtStart, divergeJunctionAtStart = v.divergeJunctionAtStart,
				};
				TrackBuilder.ApplyOrder();
				foreach (var t in tracks) TrackBuilder.VerifyConnections(t);
				TrackEnds.MarkDirty();
				SwitchCoverBuilder.Build(placed, v, th);
				Main.Log($"Weiche {placed.id} gebaut ({v.Label}{(placed.covered ? ", abgedeckt" : "")}), Anfang {(junction.inBranch?.track != null ? "verbunden" : "frei")}");
				return true;
			}
			catch (Exception e)
			{
				error = e.GetType().Name + ": " + e.Message;
				Main.Log($"Bau der Weiche {placed.id} fehlgeschlagen: {e}");
				try
				{
					foreach (var t in tracks) { TrackBuilder.Unlink(t); TrackBuilder.UnregisterTrack(t); s_ownTracks.Remove(t); }
				}
				catch (Exception cleanup) { Main.Log("Aufräumen nach Fehler: " + cleanup.Message); }
				if (clone != null) Object.Destroy(clone);
				return false;
			}
		}

		/// <summary>
		/// Wie bei DV-DoubleTrack: die vom Original zur Laufzeit erzeugten Hebel-Komponenten entfernen, damit der Klon
		/// sie beim Aktivieren sauber neu anlegt, und VisualSwitch auf den Hebel des Klons zeigen lassen.
		/// </summary>
		private static void PrepareLever(GameObject clone, Junction junction)
		{
			VisualSwitch vs = clone.GetComponentInChildren<VisualSwitch>(true);
			if (vs == null) { Main.Log("Weiche ohne VisualSwitch – Hebel wird nicht funktionieren."); return; }

			vs.junction = junction;
			if (vs.animator == null) vs.animator = clone.GetComponentInChildren<Animator>(true);

			ButtonBase? button = clone.GetComponentsInChildren<ButtonBase>(true).FirstOrDefault();
			if (button == null) return;
			GameObject lever = button.gameObject;
			FSwitchButtonGO?.SetValue(vs, lever);

			foreach (var h in lever.GetComponents<DV.Interaction.GrabHandlerButton>()) Object.DestroyImmediate(h);
			foreach (var b in lever.GetComponents<ButtonBase>()) Object.DestroyImmediate(b);
		}

		/// <summary>Freies Ende "Weichenanfang" für das Verbinden.</summary>
		internal static TrackEnd JunctionInEnd(Junction j, Vector3 pos, float yaw)
		{
			Vector3 fwd = SwitchVariant.YawRotation(yaw) * Vector3.forward;
			return new TrackEnd { position = pos, outward = -new Vector2(fwd.x, fwd.z), junction = j, isStart = true };
		}

		// ------------------------------------------------------------------
		// Entfernen
		// ------------------------------------------------------------------

		public static bool Remove(string id, out string error)
		{
			error = "";
			if (!s_built.TryGetValue(id, out BuiltSwitch sw) || sw.root == null)
			{
				s_built.Remove(id);
				return true;
			}
			if (TrackBuilder.HasVehicles(sw.through) || TrackBuilder.HasVehicles(sw.diverge)) { error = Tr.T("Fahrzeug auf der Weiche", "Vehicle on the switch"); return false; }

			try
			{
				// Zulaufendes Gleis lösen
				RailTrack? inTrack = sw.junction.inBranch?.track;
				if (inTrack != null)
				{
					if (inTrack.inJunction == sw.junction) { inTrack.inJunction = null; inTrack.inBranch = new Junction.Branch(); }
					if (inTrack.outJunction == sw.junction) { inTrack.outJunction = null; inTrack.outBranch = new Junction.Branch(); }
				}
				foreach (var t in new[] { sw.through, sw.diverge })
				{
					TrackBuilder.Unlink(t);
					TrackBuilder.UnregisterTrack(t);
					TrackBuilder.Materials.Remove(t);
					s_ownTracks.Remove(t);
				}
				Object.Destroy(sw.root);
				SwitchCoverBuilder.Remove(id);
				s_built.Remove(id);
				TrackEnds.MarkDirty();
				return true;
			}
			catch (Exception e)
			{
				error = e.GetType().Name + ": " + e.Message;
				Main.Log($"Entfernen der Weiche {id} fehlgeschlagen: {e}");
				return false;
			}
		}
	}
}
