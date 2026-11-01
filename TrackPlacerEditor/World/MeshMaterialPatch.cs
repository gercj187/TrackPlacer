using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TrackPlacer.World
{
	/// <summary>
	/// Der Mesh-Generator des Spiels setzt für alle Gleise dasselbe Schotter- und Schienenmaterial.
	/// Ein einziger, gezielter Postfix tauscht für unsere Gleise danach die Materialien aus
	/// (neu/alt, Schotter ausgeblendet). Kein PatchAll, keine Reflection pro Aufruf.
	/// </summary>
	internal static class MeshMaterialPatch
	{
		private static Harmony? s_harmony;

		public static void Apply()
		{
			if (s_harmony != null) return;
			try
			{
				MethodInfo? original = typeof(RailwayMeshGenerator).GetMethod("ScheduleGenerateBaseAndRail",
					BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
				if (original == null) { Main.Log("ScheduleGenerateBaseAndRail nicht gefunden – Gleisarten werden gleich aussehen."); return; }

				s_harmony = new Harmony("TrackPlacer.MeshMaterial");
				s_harmony.Patch(original, postfix: new HarmonyMethod(typeof(MeshMaterialPatch).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic)));
			}
			catch (Exception e) { Main.Log("Material-Patch fehlgeschlagen: " + e.Message); }
		}

		private static void Postfix(TrackChunk chunk)
		{
			if (chunk?.track == null) return;
			if (!TrackBuilder.Materials.TryGetValue(chunk.track, out var m)) return;
			var po = chunk.poolObjects;
			if (po == null || po.Length < 3) return;
			if (m.ballast != null) po[0]?.SetMaterial(m.ballast);
			if (m.rail != null) { po[1]?.SetMaterial(m.rail); po[2]?.SetMaterial(m.rail); }
		}
	}
}
