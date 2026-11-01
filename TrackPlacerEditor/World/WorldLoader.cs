using System.Collections;
using TrackPlacer.Data;
#if !TRACKPLACER_RUNTIME
using TrackPlacer.Radio;
#endif
using UnityEngine;

namespace TrackPlacer.World
{
	/// <summary>
	/// Wartet nach dem Laden der Welt, bis Gleis-Registry und Mesh-Generator bereit sind,
	/// und baut dann alle gespeicherten Gleise. Zerstört sich danach selbst.
	/// </summary>
	internal class WorldLoader : MonoBehaviour
	{
		private sealed class Runner : MonoBehaviour { }
		private static Runner? s_runner;

		/// <summary>Aktion im nächsten Frame ausführen (z. B. nachdem Object.Destroy gegriffen hat).</summary>
		public static void RunNextFrame(System.Action action)
		{
			EnsureRunner();
			s_runner!.StartCoroutine(Next(action));
		}

		/// <summary>Aktion ohne periodischen Update-Läufer einmalig verzögert ausführen.</summary>
		public static void RunDelayed(float seconds, System.Action action)
		{
			EnsureRunner();
			s_runner!.StartCoroutine(Delayed(seconds, action));
		}

		/// <summary>Aktion nach einer festen Zahl Frames ausführen (Start/OnEnable darf vorher fertig werden).</summary>
		public static void RunAfterFrames(int frames, System.Action action)
		{
			EnsureRunner();
			s_runner!.StartCoroutine(AfterFrames(Mathf.Max(1, frames), action));
		}

		private static void EnsureRunner()
		{
			if (s_runner != null) return;
			var go = new GameObject("[TrackPlacer] Runner");
			DontDestroyOnLoad(go);
			s_runner = go.AddComponent<Runner>();
		}

		private static IEnumerator Next(System.Action action)
		{
			yield return null;
			try { action(); } catch (System.Exception e) { Main.Log("Verzögerte Aktion fehlgeschlagen: " + e.Message); }
		}

		private static IEnumerator Delayed(float seconds, System.Action action)
		{
			if (seconds > 0f) yield return new WaitForSecondsRealtime(seconds); else yield return null;
			try { action(); } catch (System.Exception e) { Main.Log("Verzögerte Aktion fehlgeschlagen: " + e.Message); }
		}

		private static IEnumerator AfterFrames(int frames, System.Action action)
		{
			for (int i = 0; i < frames; i++) yield return null;
			try { action(); } catch (System.Exception e) { Main.Log("Verzögerte Aktion fehlgeschlagen: " + e.Message); }
		}

		private IEnumerator Start()
		{
			float timeout = Time.realtimeSinceStartup + 180f;
			while (!GameAccess.WorldReady)
			{
				if (Time.realtimeSinceStartup > timeout)
				{
					Main.Log("Welt wurde nicht rechtzeitig bereit – Gleise nicht gebaut. Über die Mod-Settings „Alle bauen“ nachholen.");
					Destroy(gameObject);
					yield break;
				}
				yield return new WaitForSeconds(0.5f);
			}

			// Ein paar Frames Luft, damit der Mesh-Generator seinen ersten Durchlauf hinter sich hat
			for (int i = 0; i < 5; i++) yield return null;

			TrackBuilder.AfterMeshGeneratorStarted();
			TrackBuilder.BuildAll(out string msg);
			int vanillaEdits = VanillaTrackEditor.ApplyAll();
			if (vanillaEdits > 0) msg += $", {vanillaEdits} Spielgleise geändert";
			int vanillaSwitches = VanillaSwitchEditor.ApplyAll();
			if (vanillaSwitches > 0) msg += $", {vanillaSwitches} Spielweichen geändert";
#if !TRACKPLACER_RUNTIME
			PlacerSession.RebuildSaved();
#endif
			LodSupport.Flush();
			OverlayBuilder.BuildPending();
			Main.Log("Gespeicherte Gleise beim Laden: " + msg);

			// Gleis-IDs und Lademaschinen brauchen fertige Bahnhöfe (deren Logik entsteht in einer Start-Coroutine)
			bool needStations = LayoutStore.Layout.machines.Count > 0 || LayoutStore.Layout.vanillaMachines.Count > 0
				|| LayoutStore.Layout.vanillaTrackIds.Count > 0 || LayoutStore.Layout.removedVanilla.Count > 0 || LayoutStore.Layout.tracks.Exists(t => !string.IsNullOrEmpty(t.yardId));
			if (needStations)
			{
				float t2 = Time.realtimeSinceStartup + 120f;
				while (!StationLink.StationsReady && Time.realtimeSinceStartup < t2) yield return new WaitForSeconds(0.5f);
				if (StationLink.StationsDone)
				{
					int late = StationLink.ApplyMissing();
					if (late > 0) Main.Log($"Gleis-IDs/Lademaschinen nachgeholt: {late}");
				}
				else if (!StationLink.ApplyStationsOnce("nach dem Laden")) Main.Log("Bahnhöfe nicht bereit – Gleis-IDs/Lademaschinen übersprungen.");
			}
			int signs = SignBuilder.BuildAll();
			if (signs > 0) Main.Log($"Gleisschilder: {signs}");
			Destroy(gameObject);
		}
	}
}
