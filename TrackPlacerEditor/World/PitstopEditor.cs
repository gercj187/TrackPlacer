using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TrackPlacer.Data;
using UnityEngine;

namespace TrackPlacer.World
{
	/// <summary>
	/// Pitstops bearbeiten: jedes Service-Modul (LocoResourceModule: Diesel, Kohle, Wasser, Sand, Öl, Strom,
	/// Reparaturen …) lässt sich abschalten. Abgeschaltet = Modul ausgeblendet und aus der Modulliste des Pitstops
	/// (PitStopIndicators.resourceModules) genommen. So entsteht z. B. ein reiner Kohle-Pitstop.
	/// Neue Pitstops: mit dem Objekt-Kloner einen vorhandenen Pitstop kopieren und hier die Module wählen.
	/// </summary>
	internal static class PitstopEditor
	{
		private static readonly Dictionary<string, (LocoResourceModule module, PitStopIndicators? owner)> s_live =
			new Dictionary<string, (LocoResourceModule, PitStopIndicators?)>();
		private static readonly List<GameObject> s_knownVanillaRoots = new List<GameObject>();

		public static void OnWorldUnloaded() { s_live.Clear(); s_knownVanillaRoots.Clear(); }

		public static string ResourceName(LocoResourceModule m) => m.resourceType switch
		{
			DV.ThingTypes.ResourceType.Fuel => "Diesel",
			DV.ThingTypes.ResourceType.Coal => Tr.T("Kohle", "Coal"),
			DV.ThingTypes.ResourceType.Water => Tr.T("Wasser", "Water"),
			DV.ThingTypes.ResourceType.Sand => "Sand",
			DV.ThingTypes.ResourceType.Oil => "Öl",
			DV.ThingTypes.ResourceType.ElectricCharge => Tr.T("Strom", "Electricity"),
			DV.ThingTypes.ResourceType.Car_DMG => Tr.T("Reparatur Wagenkasten", "Repair body"),
			DV.ThingTypes.ResourceType.Wheels_DMG => Tr.T("Reparatur Räder", "Repair wheels"),
			DV.ThingTypes.ResourceType.MechanicalPowertrain_DMG => Tr.T("Reparatur Mechanik", "Repair mechanical"),
			DV.ThingTypes.ResourceType.ElectricalPowertrain_DMG => Tr.T("Reparatur Elektrik", "Repair electrical"),
			DV.ThingTypes.ResourceType.Cargo_DMG => Tr.T("Reparatur Ladung", "Repair cargo"),
			_ => m.resourceType.ToString(),
		};

		/// <summary>Service-Modul nahe dem Laser (aktive und von uns abgeschaltete).</summary>
		public static (LocoResourceModule? module, string? disabledId) FindNear(Vector3 staticPos, Transform? hit, float radius)
		{
			var direct = hit != null ? hit.GetComponentInParent<LocoResourceModule>() : null;
			if (direct != null) return (direct, IsDisabled(direct) ? IdOf(direct) : null);

			LocoResourceModule? best = null;
			float bestD = radius * radius;
			Vector3 shift = GameAccess.OriginShift;
			foreach (var m in UnityEngine.Object.FindObjectsOfType<LocoResourceModule>())
			{
				float d = ((m.transform.position - shift) - staticPos).sqrMagnitude;
				if (d < bestD) { bestD = d; best = m; }
			}
			foreach (var kv in s_live)
			{
				if (kv.Value.module == null) continue;
				float d = ((kv.Value.module.transform.position - shift) - staticPos).sqrMagnitude;
				if (d < bestD) { bestD = d; best = kv.Value.module; }
			}
			return (best, best != null && IsDisabled(best) ? IdOf(best) : null);
		}

		private static string? IdOf(LocoResourceModule m) => s_live.FirstOrDefault(kv => kv.Value.module == m).Key;

		public static PitstopModuleEdit? EditOf(LocoResourceModule m)
		{
			string? id = IdOf(m);
			return id != null ? LayoutStore.Layout.pitstopModules.Find(x => x.id == id) : null;
		}

		public static bool IsDisabled(LocoResourceModule m) => EditOf(m)?.disabled == true;

		private static PitstopModuleEdit NewEdit(LocoResourceModule m) => new PitstopModuleEdit
		{
			parentPath = CloneHelper.PathOf(m.transform.parent),
			name = m.name,
			position = PlacedTrack.FromVec(m.transform.position - GameAccess.OriginShift),
			resource = m.resourceType.ToString(),
		};

		/// <summary>Transformationseintrag für ein Modul anlegen/aktualisieren und seine aktuelle lokale Pose merken.</summary>
		public static PitstopModuleEdit SaveTransform(LocoResourceModule m)
		{
			var e = EditOf(m) ?? NewEdit(m);
			if (!LayoutStore.Layout.pitstopModules.Contains(e)) LayoutStore.Layout.pitstopModules.Add(e);
			e.hasTransform = true;
			e.localPosition = PlacedTrack.FromVec(m.transform.localPosition);
			e.localEuler = PlacedTrack.FromVec(m.transform.localEulerAngles);
			s_live[e.id] = (m, FindOwner(m));
			LayoutStore.SaveQuiet();
			return e;
		}

		/// <summary>Modul abschalten (gespeichert).</summary>
		public static void Disable(LocoResourceModule m)
		{
			if (IsDisabled(m)) return;
			var e = EditOf(m) ?? NewEdit(m);
			if (!LayoutStore.Layout.pitstopModules.Contains(e)) LayoutStore.Layout.pitstopModules.Add(e);
			e.disabled = true;
			LayoutStore.SaveQuiet();
			Apply(e, m);
		}

		/// <summary>Modul wieder einschalten.</summary>
		public static void Enable(string id)
		{
			var e = LayoutStore.Layout.pitstopModules.Find(x => x.id == id);
			if (e == null) return;
			e.disabled = false;
			if (s_live.TryGetValue(id, out var l) && l.module != null)
			{
				if (l.owner != null && l.owner.resourceModules != null && !l.owner.resourceModules.Contains(l.module))
					l.owner.resourceModules = l.owner.resourceModules.Concat(new[] { l.module }).ToArray();
				l.module.gameObject.SetActive(true);
			}
			if (!e.hasTransform)
			{
				s_live.Remove(id);
				LayoutStore.Layout.pitstopModules.Remove(e);
			}
			LayoutStore.SaveQuiet();
		}

		private static PitStopIndicators? FindOwner(LocoResourceModule m)
		{
			foreach (var ind in UnityEngine.Object.FindObjectsOfType<PitStopIndicators>())
				if (ind.resourceModules != null && ind.resourceModules.Contains(m)) return ind;
			var root = RootOf(m.transform);
			return root != null ? root.GetComponentsInChildren<PitStopIndicators>(true)
				.OrderBy(i => (i.transform.position - m.transform.position).sqrMagnitude).FirstOrDefault() : null;
		}

		private static void Apply(PitstopModuleEdit e, LocoResourceModule m)
		{
			if (e.hasTransform)
			{
				m.transform.localPosition = e.LocalPosition;
				m.transform.localEulerAngles = e.LocalEuler;
			}
			PitStopIndicators? owner = FindOwner(m);
			if (e.disabled)
			{
				if (owner != null && owner.resourceModules != null) owner.resourceModules = owner.resourceModules.Where(x => x != m).ToArray();
				m.gameObject.SetActive(false);
			}
			else
			{
				m.gameObject.SetActive(true);
				if (owner != null && owner.resourceModules != null && !owner.resourceModules.Contains(m))
					owner.resourceModules = owner.resourceModules.Concat(new[] { m }).ToArray();
			}
			s_live[e.id] = (m, owner);
			Main.Log($"Pitstop-Modul angewendet: {ResourceName(m)} ({(e.disabled ? "aus" : "an")}, Pose={(e.hasTransform ? "geändert" : "original")})");
		}

		// ------------------------------------------------------------------
		// Ganze Pitstops (GeneralServiceStation = Eltern des PitStopStation-Objekts)
		// ------------------------------------------------------------------

		/// <summary>Wurzel eines Pitstops zu einem getroffenen Teil (oder null).</summary>
		public static GameObject? RootOf(Transform? hit)
		{
			for (var t = hit; t != null && t.parent != null; t = t.parent)
			{
				var st = t.GetComponentInChildren<PitStopStation>(true);
				if (st == null) continue;
				var root = st.transform.parent != null ? st.transform.parent : st.transform;
				if (t == root || t.IsChildOf(root)) return root.gameObject;
				return null; // anderer Ast (z. B. ganze Bahnhofsgruppe)
			}
			return null;
		}

		/// <summary>Anzahl der Service-Module eines Pitstops (auch abgeschaltete).</summary>
		public static int ModuleCount(GameObject root) => root.GetComponentsInChildren<LocoResourceModule>(true).Length;

		/// <summary>Alle momentan geladenen Pitstops der Welt (auch inaktive), meiste Module zuerst.</summary>
		public static List<GameObject> VanillaRoots()
		{
			foreach (var st in Resources.FindObjectsOfTypeAll<PitStopStation>())
			{
				if (st == null || st.GetComponentInParent<CloneBuilder.CloneMarker>() != null) continue;
				var root = st.transform.parent != null ? st.transform.parent.gameObject : st.gameObject;
				if (!s_knownVanillaRoots.Contains(root)) s_knownVanillaRoots.Add(root);
			}
			s_knownVanillaRoots.RemoveAll(r => r == null);
			return s_knownVanillaRoots.OrderByDescending(ModuleCount).ThenBy(r => r.name).ToList();
		}

		/// <summary>Anzeigename: übergeordneter Name (z. B. „PitstopSteelMill“) statt „GeneralServiceStation“.</summary>
		public static string LabelOf(GameObject root)
		{
			var p = root.transform.parent;
			string n = p != null && root.name.StartsWith("GeneralServiceStation") ? p.name : root.name;
			return n.Length > 22 ? n.Substring(0, 22) : n;
		}

		/// <summary>
		/// Vor dem Aktivieren eines Klons: alle Service-Module einschalten und in die Modulliste eintragen (die Kopie
		/// übernimmt sonst abgeschaltete Module der Vorlage). Gespeicherte Abschaltungen setzt Tick danach wieder.
		/// </summary>
		public static int PrepareClone(GameObject clone)
		{
			var indicators = clone.GetComponentsInChildren<PitStopIndicators>(true);
			if (indicators.Length == 0) return 0;
			var modules = clone.GetComponentsInChildren<LocoResourceModule>(true);
			if (modules.Length == 0) return 0; // nur ein Teil kopiert – Verweise zeigen auf das Original, nichts ändern
			foreach (var m in modules) if (!m.gameObject.activeSelf) m.gameObject.SetActive(true);
			if (indicators.Length == 1) indicators[0].resourceModules = modules;
			else
				foreach (var ind in indicators)
				{
					// mehrere Stationen in einer Kopie: Module dem nächsten Anzeigeteil zuordnen
					ind.resourceModules = modules.Where(m => indicators.OrderBy(i => (i.transform.position - m.transform.position).sqrMagnitude).First() == ind).ToArray();
				}
			return modules.Length;
		}

		/// <summary>Abschaltungen eines Spiel-Pitstops auf seinen Ersatz-Klon übertragen.</summary>
		public static void MovePath(string oldPath, string newPath)
		{
			bool any = false;
			foreach (var e in LayoutStore.Layout.pitstopModules)
				if (e.parentPath == oldPath || e.parentPath.StartsWith(oldPath + "/", StringComparison.Ordinal))
				{
					e.parentPath = newPath + e.parentPath.Substring(oldPath.Length);
					s_live.Remove(e.id);
					any = true;
				}
			if (any) Tick();
		}

		/// <summary>Gespeicherte Abschaltungen anwenden (Läufer, alle 2 s – Pitstops können gestreamt sein).</summary>
		public static void Tick()
		{
			if (!GameAccess.TracksReady) return;
			foreach (var e in LayoutStore.Layout.pitstopModules)
			{
				if (s_live.TryGetValue(e.id, out var l) && l.module != null) continue;
				var go = CloneHelper.Find(e.parentPath, e.name, e.Position, 1.5f)
					?? CloneHelper.Find(e.parentPath, e.name, e.Position, 100000f); // verschobener Pitstop: nur über den Namen
				var m = go != null ? go.GetComponent<LocoResourceModule>() : null;
				if (m != null) Apply(e, m);
			}
		}

		/// <summary>Nur einen gespeicherten Moduleintrag prüfen; der Hintergrundläufer verteilt sie über Frames.</summary>
		public static void TickOne(int index)
		{
			if (!GameAccess.TracksReady || index < 0 || index >= LayoutStore.Layout.pitstopModules.Count) return;
			var e = LayoutStore.Layout.pitstopModules[index];
			if (s_live.TryGetValue(e.id, out var l) && l.module != null) return;
			var go = CloneHelper.Find(e.parentPath, e.name, e.Position, 1.5f)
				?? CloneHelper.Find(e.parentPath, e.name, e.Position, 100000f);
			var m = go != null ? go.GetComponent<LocoResourceModule>() : null;
			if (m != null) Apply(e, m);
		}
	}
}
