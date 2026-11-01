using System;
using System.Collections.Generic;
using System.Linq;
using TrackPlacer.Data;
using UnityEngine;

namespace TrackPlacer.World
{
	/// <summary>
	/// Entfernt Spielgleise und Spielweichen (gespeichert in der JSON, bei jedem Laden erneut angewendet).
	/// Bewusst NICHT aus den Listen der Registry gelöscht: deren Reihenfolge nutzt das Spiel u. a. für
	/// Spielstände (Gleise und Weichenstellungen). Stattdessen: Verbindungen lösen, Mesh und Collider ausblenden.
	///  - normales Gleis: abkoppeln; hängt es am Anfang einer Weiche, wird dieser Anschluss frei
	///  - Gleis einer Weiche: die ganze Weiche wird entfernt (beide Weichengleise + Modell + Hebel)
	/// </summary>
	internal static class VanillaRemover
	{
		private static readonly HashSet<RailTrack> s_removed = new HashSet<RailTrack>();
		private static List<(RailTrack track, Bounds bounds, Vector3[] pts)>? s_index;

		public static bool IsRemoved(RailTrack t) => s_removed.Contains(t);

		public static void OnWorldUnloaded()
		{
			s_removed.Clear();
			s_index = null;
		}

		/// <summary>Alle Einträge aus der JSON anwenden (beim Laden).</summary>
		public static int ApplyAll()
		{
			int n = 0;
			foreach (var r in LayoutStore.Layout.removedVanilla)
			{
				RailTrack? t = Find(r.name, r.Mid);
				if (t == null) { Main.Log($"Entferntes Spielgleis '{r.name}' nicht gefunden (Spiel-Update?)"); continue; }
				if (!s_removed.Contains(t) && Remove(t, out _, refreshTurntables: false)) n++;
			}
			return n;
		}

		internal static RailTrack? Find(string name, Vector3 mid)
		{
			RailTrack? best = null;
			float bestD = 4f * 4f;
			foreach (var t in GameAccess.Registry!.OrderedRailtracks)
			{
				if (t == null || t.name != name) continue;
				float d = (Mid(t) - mid).sqrMagnitude;
				if (d < bestD) { bestD = d; best = t; }
			}
			return best;
		}

		public static Vector3 Mid(RailTrack t)
		{
			var ps = t.GetKinkedPointSet();
			if (ps?.points == null || ps.points.Length == 0) return t.transform.position - GameAccess.OriginShift;
			return (Vector3)ps.points[ps.points.Length / 2].position; // PointSet ist absolut = statisch
		}

		/// <summary>Die Weiche, zu der ein Weichengleis gehört (null bei normalem Gleis).</summary>
		public static Junction? OwningJunction(RailTrack t)
		{
			foreach (var j in new[] { t.inJunction, t.outJunction })
				if (j != null && j.outBranches != null && j.outBranches.Any(b => b?.track == t)) return j;
			return null;
		}

		private static List<RailTrack> SwitchTracks(Junction j) =>
			j.outBranches.Where(b => b?.track != null).Select(b => b.track).Distinct().ToList();

		public static bool CanRemove(RailTrack t, out string reason)
		{
			reason = "";
			if (t == null) { reason = Tr.T("kein Gleis", "no track"); return false; }
			if (TrackBuilder.IsOwn(t)) { reason = Tr.T("eigenes Gleis", "own track"); return false; }
			if (TurntableSupport.IsBridge(t)) { reason = Tr.T("Drehscheibe", "turntable"); return false; }
			var j = OwningJunction(t);
			var tracks = j != null ? SwitchTracks(j) : new List<RailTrack> { t };
			if (tracks.Any(TrackBuilder.HasVehicles)) { reason = Tr.T("Fahrzeug darauf", "vehicle on it"); return false; }
			return true;
		}

		public static bool Remove(RailTrack t, out string error, bool refreshTurntables = true)
		{
			error = "";
			if (!CanRemove(t, out error)) return false;
			try
			{
				Junction? j = OwningJunction(t);
				var tracks = j != null ? SwitchTracks(j) : new List<RailTrack> { t };

				if (j != null)
				{
					VanillaSwitchEditor.OnRemoved(tracks); // Abdeckung/Änderungen dieser Spielweiche verwerfen
					// Zulaufendes Gleis vom Weichenanfang lösen
					RailTrack? inTrack = j.inBranch?.track;
					if (inTrack != null)
					{
						if (inTrack.inJunction == j) { inTrack.inJunction = null; inTrack.inBranch = new Junction.Branch(); }
						if (inTrack.outJunction == j) { inTrack.outJunction = null; inTrack.outBranch = new Junction.Branch(); }
						TrackBuilder.RefreshLogicNeighbors(inTrack);
					}
					j.inBranch = new Junction.Branch();
				}
				else
				{
					// Normales Gleis am Anfang einer Weiche: Weichenanfang wird frei
					foreach (var jj in new[] { t.inJunction, t.outJunction })
						if (jj != null && jj.inBranch?.track == t) jj.inBranch = new Junction.Branch();
					t.inJunction = null;
					t.outJunction = null;
				}

				foreach (var tr in tracks)
				{
					var neighbors = new[] { tr.inBranch?.track, tr.outBranch?.track };
					TrackBuilder.Unlink(tr);
					foreach (var n in neighbors) if (n != null && !tracks.Contains(n)) TrackBuilder.RefreshLogicNeighbors(n);
					DisconnectLogic(tr);
					TrackBuilder.RemoveMeshesOf(tr);
					LodSupport.HideVanilla(tr);
					s_removed.Add(tr);
					DetachFromStations(tr);
					RemoveFromDiscoveryRegistry(tr);
				}

				// Ausblenden: bei Weichen die ganze Gruppe (Modell, Hebel, Schilder), sonst nur das Gleis
				if (j != null)
				{
					Transform group = j.transform.parent != null ? j.transform.parent : j.transform;
					group.gameObject.SetActive(false);
				}
				else t.gameObject.SetActive(false);

				// Suchindex nur um die entfernten Gleise kürzen statt neu aufzubauen
				s_index?.RemoveAll(e => s_removed.Contains(e.track));
				TrackEnds.MarkDirty();
				FarTrackHider.MarkDirty();
				if (refreshTurntables) TurntableSupport.RefreshNear(tracks.ToArray());
				return true;
			}
			catch (Exception e)
			{
				error = e.GetType().Name + ": " + e.Message;
				Main.Log($"Spielgleis {t.name} entfernen fehlgeschlagen: {e}");
				return false;
			}
		}

		/// <summary>
		/// Aus AllTracks entfernen: der Spiel-Funkgerät-Spawner und andere Auswahlwerkzeuge lesen diese Liste direkt.
		/// OrderedRailtracks bleibt absichtlich unverändert, weil Wagen-Spielstände dessen stabile Indizes speichern.
		/// </summary>
		private static void RemoveFromDiscoveryRegistry(RailTrack track)
		{
			var registry = GameAccess.Registry;
			if (registry != null && GameAccess.RegAllTracks?.GetValue(registry) is RailTrack[] all && all.Contains(track))
				GameAccess.RegAllTracks.SetValue(registry, all.Where(t => t != track).ToArray());
			// Die Zuordnung RailTrack ↔ Logik-Gleis bleibt bewusst bestehen: gespeicherte Jobs/Wagen und Spielcode
			// greifen per Index-Zugriff darauf zu (fehlender Schlüssel = KeyNotFoundException beim Laden).
		}

		// ------------------------------------------------------------------
		// Entfernte Gleise für das Spiel unbrauchbar machen. Sie bleiben (unsichtbar) in der Registry, weil der
		// Spielstand Gleis-Indizes speichert – deshalb:
		//  - aus den Bahnhofslisten (Abstell-/Ein-/Ausfahrgleise) und dem YardTracksOrganizer nehmen → keine Jobs/Wagen
		//  - Harmony: RailTrack.GetClosestPoint findet sie nicht mehr (Aufgleisen, Wagen-Spawner, Teleport …),
		//    CarSpawner.SpawnCarTypesOnTrack* verweigert sie.
		// ------------------------------------------------------------------

		private static readonly System.Reflection.FieldInfo? FYtoIds = typeof(YardTracksOrganizer).GetField("yardTrackIdToTrack", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
		private static readonly System.Reflection.FieldInfo? FYtoReserved = typeof(YardTracksOrganizer).GetField("trackToReservedLength", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
		private static readonly System.Reflection.BindingFlags LogicFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
		private static readonly System.Reflection.FieldInfo? FLogicIn = typeof(DV.Logic.Job.Track).GetField("<InTrack>k__BackingField", LogicFlags);
		private static readonly System.Reflection.FieldInfo? FLogicOut = typeof(DV.Logic.Job.Track).GetField("<OutTrack>k__BackingField", LogicFlags);
		private static readonly System.Reflection.FieldInfo? FLogicPossibleIn = typeof(DV.Logic.Job.Track).GetField("<PossibleInTracks>k__BackingField", LogicFlags);
		private static readonly System.Reflection.FieldInfo? FLogicPossibleOut = typeof(DV.Logic.Job.Track).GetField("<PossibleOutTracks>k__BackingField", LogicFlags);

		/// <summary>Entfernt das Logikgleis aus allen Nachbarschaften, ohne die RailTrack-Registry-Reihenfolge anzutasten.</summary>
		private static void DisconnectLogic(RailTrack rail)
		{
			if (!RailTrackRegistry.RailTrackToLogicTrack.TryGetValue(rail, out DV.Logic.Job.Track removed)) return;
			foreach (var logic in RailTrackRegistry.RailTrackToLogicTrack.Values.Distinct())
			{
				if (logic == null) continue;
				if (ReferenceEquals(FLogicIn?.GetValue(logic), removed)) FLogicIn?.SetValue(logic, null);
				if (ReferenceEquals(FLogicOut?.GetValue(logic), removed)) FLogicOut?.SetValue(logic, null);
				if (FLogicPossibleIn?.GetValue(logic) is HashSet<DV.Logic.Job.Track> pin) pin.Remove(removed);
				if (FLogicPossibleOut?.GetValue(logic) is HashSet<DV.Logic.Job.Track> pout) pout.Remove(removed);
			}
			FLogicIn?.SetValue(removed, null);
			FLogicOut?.SetValue(removed, null);
			if (FLogicPossibleIn?.GetValue(removed) is HashSet<DV.Logic.Job.Track> ownIn) ownIn.Clear();
			if (FLogicPossibleOut?.GetValue(removed) is HashSet<DV.Logic.Job.Track> ownOut) ownOut.Clear();
		}

		/// <summary>Logik-Gleis aus allen Bahnhöfen lösen. Gibt true zurück, wenn es irgendwo eingetragen war.</summary>
		public static bool DetachFromStations(RailTrack tr)
		{
			bool any = false;
			try
			{
				var logic = StationLink.LogicOf(tr);
				if (StationController.allStations != null)
					foreach (var st in StationController.allStations)
					{
						if (st == null) continue;
						var yard = st.logicStation?.yard;
						if (yard != null && logic != null)
						{
							any |= yard.StorageTracks?.Remove(logic) ?? false;
							any |= yard.TransferInTracks?.Remove(logic) ?? false;
							any |= yard.TransferOutTracks?.Remove(logic) ?? false;
						}
						any |= st.AllStationTracks?.Remove(tr) ?? false;
					}
				var yto = YardTracksOrganizer.Instance;
				if (yto != null && logic != null)
				{
					if (FYtoIds?.GetValue(yto) is Dictionary<string, DV.Logic.Job.Track> ids)
						foreach (var k in ids.Where(kv => kv.Value == logic).Select(kv => kv.Key).ToList()) { ids.Remove(k); any = true; }
					if (FYtoReserved?.GetValue(yto) is Dictionary<DV.Logic.Job.Track, float> res) any |= res.Remove(logic);
				}
				if (any) Main.Log($"Entferntes Spielgleis {tr.name} aus den Bahnhofslisten genommen (keine Jobs/Wagen mehr darauf).");
			}
			catch (Exception e) { Main.Log($"Gleis {tr.name} aus Bahnhof lösen: {e.Message}"); }
			return any;
		}

		/// <summary>Nach dem Laden (Bahnhöfe fertig): alle entfernten Gleise aus den Bahnhöfen lösen.</summary>
		public static int DetachAllFromStations()
		{
			int n = 0;
			foreach (var t in s_removed.ToList()) if (t != null && DetachFromStations(t)) n++;
			return n;
		}

		private static HarmonyLib.Harmony? s_harmony;

		/// <summary>Einmalig beim Mod-Start.</summary>
		public static void PatchGame()
		{
			if (s_harmony != null) return;
			try
			{
				s_harmony = new HarmonyLib.Harmony("TrackPlacer.RemovedTracks");
				const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
				var closest = typeof(RailTrack).GetMethod("GetClosestPoint", all);
				if (closest != null)
					s_harmony.Patch(closest, prefix: new HarmonyLib.HarmonyMethod(typeof(VanillaRemover).GetMethod(nameof(ClosestPointPrefix), all)));
				else Main.Log("RailTrack.GetClosestPoint nicht gefunden – entfernte Gleise sind evtl. noch auffindbar.");
				var withinRange = typeof(RailTrack).GetMethod("GetPointWithinRangeWithYOffset", all);
				if (withinRange != null)
					s_harmony.Patch(withinRange, prefix: new HarmonyLib.HarmonyMethod(typeof(VanillaRemover).GetMethod(nameof(PointWithinRangePrefix), all)));
				else Main.Log("RailTrack.GetPointWithinRangeWithYOffset nicht gefunden – Funkgerät-Spawner kann entfernte Gleise evtl. noch wählen.");
				var spawner = Type.GetType("CarSpawner, Assembly-CSharp");
				int n = 0;
				if (spawner != null)
					foreach (var m in spawner.GetMethods(all))
						if (m.Name.StartsWith("Spawn") && m.GetParameters().Any(p => p.ParameterType == typeof(RailTrack)))
						{
							s_harmony.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(VanillaRemover).GetMethod(nameof(SpawnOnTrackPrefix), all)));
							n++;
						}
				var closestTrack = spawner?.GetMethod("GetTrackClosestTo", all);
				if (closestTrack != null)
					s_harmony.Patch(closestTrack, postfix: new HarmonyLib.HarmonyMethod(typeof(VanillaRemover).GetMethod(nameof(ClosestTrackPostfix), all)));
				Main.Log($"Schutz für entfernte Spielgleise aktiv ({(closest != null ? 1 : 0)} Nächstpunkt + {(withinRange != null ? 1 : 0)} Bereichspunkt + {n} Spawn + {(closestTrack != null ? 1 : 0)} Nächstgleis).");
			}
			catch (Exception e) { Main.Log("Patch für entfernte Gleise fehlgeschlagen: " + e.Message); }
		}

		private static bool ClosestPointPrefix(RailTrack __0, ref ValueTuple<DV.PointSet.EquiPointSet.Point?, float> __result)
		{
			if (s_removed.Count == 0 || __0 == null || !s_removed.Contains(__0)) return true;
			__result = (null, float.PositiveInfinity);
			return false;
		}

		/// <summary>Wird vom eingebauten Funkgerät-Fahrzeugspawner für Vorschau und Platzierung verwendet.</summary>
		private static bool PointWithinRangePrefix(RailTrack __0, ref DV.PointSet.EquiPointSet.Point? __result)
		{
			if (s_removed.Count == 0 || __0 == null || !s_removed.Contains(__0)) return true;
			__result = null;
			return false;
		}

		private static bool SpawnOnTrackPrefix(object[] __args, System.Reflection.MethodBase __originalMethod)
		{
			if (s_removed.Count == 0) return true;
			foreach (var a in __args)
				if (a is RailTrack t && s_removed.Contains(t))
				{
					Main.Log($"{__originalMethod.Name}: Spawn auf entferntem Spielgleis {t.name} verhindert.");
					return false;
				}
			return true;
		}

		private static void ClosestTrackPostfix(ref RailTrack? __result, ref int __2)
		{
			if (__result == null || !s_removed.Contains(__result)) return;
			Main.Log($"Entferntes Spielgleis {__result.name} aus der Spawn-Nächstsuche verworfen.");
			__result = null;
			__2 = -1;
		}

		/// <summary>Nur dann Drehscheiben neu suchen lassen, wenn das Gleis an einer endet (spart einen teuren Szenen-Scan).</summary>
		private static bool NearTurntable(RailTrack t)
		{
			if (t.curve == null || t.curve.pointCount < 2) return false;
			Vector3 shift = GameAccess.OriginShift;
			return TurntableSupport.IsOnRim(t.curve[0].position - shift) || TurntableSupport.IsOnRim(t.curve.Last().position - shift);
		}

		/// <summary>Spielgleis nahe am Laser-Treffer (statisch), max. radius horizontal.</summary>
		public static RailTrack? FindNear(Vector3 staticPos, float radius)
		{
			if (s_index == null)
			{
				s_index = new List<(RailTrack, Bounds, Vector3[])>();
				foreach (var t in GameAccess.Registry!.OrderedRailtracks)
				{
					if (t == null || s_removed.Contains(t) || TrackBuilder.IsOwn(t)) continue;
					var ps = t.GetKinkedPointSet();
					if (ps?.points == null || ps.points.Length == 0) continue;
					int n = ps.points.Length;
					var pts = new Vector3[(n + 3) / 4 + 1];
					int k = 0;
					for (int i = 0; i < n; i += 4) pts[k++] = (Vector3)ps.points[i].position;
					pts[k++] = (Vector3)ps.points[n - 1].position;
					if (k < pts.Length) Array.Resize(ref pts, k);
					var b = new Bounds(pts[0], Vector3.zero);
					foreach (var p in pts) b.Encapsulate(p);
					b.Expand(radius * 2f + 2f);
					s_index.Add((t, b, pts));
				}
			}

			RailTrack? best = null;
			float bestD = radius * radius;
			foreach (var (t, b, pts) in s_index)
			{
				if (!b.Contains(staticPos)) continue;
				foreach (var p in pts)
				{
					if (Mathf.Abs(p.y - staticPos.y) > 4f) continue;
					float d = new Vector2(p.x - staticPos.x, p.z - staticPos.z).sqrMagnitude;
					if (d < bestD) { bestD = d; best = t; }
				}
			}
			return best;
		}
	}
}
