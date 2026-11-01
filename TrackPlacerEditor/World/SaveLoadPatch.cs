using System;
using System.Reflection;
using HarmonyLib;

namespace TrackPlacer.World
{
	/// <summary>
	/// Spielstand-Schutz für Wagen auf eigenen Gleisen (zwei gezielte Patches, kein PatchAll):
	///  - Prefix auf CarsSaveManager.Load: eigene Gleise vorher bauen, damit die gespeicherten Gleis-Indizes existieren.
	///  - Finalizer auf InstantiateCarFromSavegame: verweist ein Wagen trotzdem auf ein fehlendes Gleis, wird nur dieser
	///    Wagen übersprungen. Ohne das löscht das Spiel ALLE Wagen und Jobs („Deleting all cars/jobs“).
	/// </summary>
	internal static class SaveLoadPatch
	{
		private static Harmony? s_harmony;

		public static void Apply()
		{
			if (s_harmony != null) return;
			try
			{
				s_harmony = new Harmony("TrackPlacer.SaveLoad");
				const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
				Type? csm = Type.GetType("CarsSaveManager, Assembly-CSharp");
				MethodInfo? load = csm?.GetMethod("Load", all);
				MethodInfo? inst = csm?.GetMethod("InstantiateCarFromSavegame", all);
				if (load != null)
					s_harmony.Patch(load, prefix: new HarmonyMethod(typeof(SaveLoadPatch).GetMethod(nameof(LoadPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
				else Main.Log("CarsSaveManager.Load nicht gefunden – Wagen auf eigenen Gleisen werden evtl. nicht geladen.");
				if (inst != null)
					s_harmony.Patch(inst, finalizer: new HarmonyMethod(typeof(SaveLoadPatch).GetMethod(nameof(InstantiateFinalizer), BindingFlags.Static | BindingFlags.NonPublic)));
				else Main.Log("CarsSaveManager.InstantiateCarFromSavegame nicht gefunden.");

				// Jobs: Bahnhofs-IDs eigener Gleise müssen VOR dem Laden der Job-Ketten gesetzt sein
				MethodInfo? jobs = Type.GetType("JobSaveManager, Assembly-CSharp")?.GetMethod("LoadJobSaveGameData", all);
				if (jobs != null)
					s_harmony.Patch(jobs, prefix: new HarmonyMethod(typeof(SaveLoadPatch).GetMethod(nameof(JobsPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
				else Main.Log("JobSaveManager.LoadJobSaveGameData nicht gefunden – Jobs auf eigenen Bahnhofsgleisen gehen beim Laden evtl. verloren.");

				// Eigene Weichen: Schild-Hover beim ersten Start noch ohne Schild-Konfiguration → NullReference abfangen,
				// kurz danach erneut versuchen
				MethodInfo? hover = Type.GetType("JunctionSwitchRemoteControllable, Assembly-CSharp")?.GetMethod("UpdateSignHover", all);
				if (hover != null)
					s_harmony.Patch(hover, finalizer: new HarmonyMethod(typeof(SaveLoadPatch).GetMethod(nameof(SignHoverFinalizer), BindingFlags.Static | BindingFlags.NonPublic)));
			}
			catch (Exception e) { Main.Log("Spielstand-Patch fehlgeschlagen: " + e.Message); }
		}

		private static void LoadPrefix() => TrackBuilder.EarlyBuild();

		private static void JobsPrefix()
		{
			try
			{
				TrackBuilder.EnsureWorld();
				if (!StationLink.ApplyStationsOnce("vor dem Laden der Jobs") && !StationLink.StationsDone)
					Main.Log("Vor dem Laden der Jobs: Bahnhöfe nicht bereit – Jobs auf eigenen Gleisen können fehlen.");
				else StationLink.ApplyMissing();
			}
			catch (Exception e) { Main.Log("Gleis-IDs vor den Jobs fehlgeschlagen: " + e); }
		}

		private static readonly System.Collections.Generic.HashSet<int> s_hoverRetried = new System.Collections.Generic.HashSet<int>();

		private static Exception? SignHoverFinalizer(Exception? __exception, UnityEngine.MonoBehaviour __instance)
		{
			if (!(__exception is NullReferenceException) || __instance == null) return __exception;
			bool own = false;
			for (var t = __instance.transform; t != null && !own; t = t.parent) own = t.name.StartsWith("[TP]");
			if (!own) return __exception;
			int id = __instance.GetInstanceID();
			if (s_hoverRetried.Add(id))
			{
				var inst = __instance;
				var m = inst.GetType().GetMethod("UpdateSignHover", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
				WorldLoader.RunDelayed(3f, () => { if (inst != null) m?.Invoke(inst, null); });
			}
			return null; // eigene Weiche: Fehler schlucken, das Schild aktualisiert sich beim nächsten Umstellen ohnehin
		}

		private static Exception? InstantiateFinalizer(Exception? __exception, ref TrainCar? __result)
		{
			if (__exception is IndexOutOfRangeException || __exception is ArgumentOutOfRangeException || __exception is NullReferenceException)
			{
				Main.Log("Wagen aus dem Spielstand übersprungen (Gleis fehlt, z. B. eigenes Gleis gelöscht): " + __exception.Message);
				__result = null;
				return null;
			}
			return __exception;
		}
	}
}
