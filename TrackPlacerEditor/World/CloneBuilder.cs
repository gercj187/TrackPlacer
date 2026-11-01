using System;
using System.Collections.Generic;
using TrackPlacer.Data;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TrackPlacer.World
{
	/// <summary>
	/// Objekt-Kloner: Kopie eines Szenenobjekts (Gebäude, Zaun, Pitstop …) an eigener Stelle, gedreht und in der Höhe
	/// angepasst. Gespeichert wird die Quelle (Elternpfad, Name, Position) und die eigene Lage. Quellen in gestreamten
	/// Welt-Kacheln sind evtl. erst später geladen – der Läufer (alle 2 s) baut fehlende Klone nach.
	/// </summary>
	internal static class CloneBuilder
	{
		private const string NamePrefix = "[TP]_clone_";

		internal sealed class CloneMarker : MonoBehaviour { public string id = ""; }

		private static readonly Dictionary<string, GameObject> s_built = new Dictionary<string, GameObject>();
		private static readonly HashSet<string> s_failed = new HashSet<string>();
		private static GameObject? s_preview;
		private static GameObject? s_previewSource;

		public static IReadOnlyDictionary<string, GameObject> Built => s_built;

		// Nachholen fehlender Quellen: nicht bei jedem Läufer-Durchlauf suchen (Ruckler!), sondern erst wieder, wenn
		// eine Welt-Kachel geladen wurde – spätestens nach 15 s.
		private static readonly Dictionary<string, float> s_nextTry = new Dictionary<string, float>();
		private static readonly Dictionary<string, GameObject?> s_resourceCache = new Dictionary<string, GameObject?>();
		private static Dictionary<string, GameObject>? s_markers;
		private static bool s_sceneHook;

		private static void HookScenes()
		{
			if (s_sceneHook) return;
			s_sceneHook = true;
			UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) => s_nextTry.Clear();
		}

		/// <summary>Vorhandene Klon-Objekte (z. B. nach Zustands-Reset) – EINMAL pro Welt suchen, nicht pro Versuch.</summary>
		private static GameObject? ExistingMarker(string id)
		{
			if (s_markers == null)
			{
				s_markers = new Dictionary<string, GameObject>();
				foreach (var m in Resources.FindObjectsOfTypeAll<CloneMarker>())
					if (m != null && m.gameObject.scene.IsValid() && !string.IsNullOrEmpty(m.id)) s_markers[m.id] = m.gameObject;
			}
			return s_markers.TryGetValue(id, out var go) && go != null ? go : null;
		}

		public static void OnWorldUnloaded()
		{
			s_built.Clear();
			s_failed.Clear();
			s_nextTry.Clear();
			s_resourceCache.Clear();
			s_markers = null;
			CloneHelper.ResetResourceCache();
			HidePreview(true);
		}

		/// <summary>Rotation des Klons: Drehung um die Hochachse relativ zur Quelle (Neigung der Quelle bleibt).</summary>
		private static Quaternion RotationFor(PlacedClone c, Transform src) => Quaternion.Euler(0f, c.yaw, 0f) * src.rotation;

		public static GameObject? Source(PlacedClone c)
		{
			var go = CloneHelper.Find(c.srcParentPath, c.srcName, c.SrcPosition);
			if (go != null) return go;
			// Ressourcen-Vorlagen (z. B. weltweite Pitstops) sind ab Spielstart geladen: nur EINMAL pro Welt suchen,
			// Resources.FindObjectsOfTypeAll geht über alle Objekte samt Assets und kostet einen ganzen Frame.
			if (s_resourceCache.TryGetValue(c.id, out var cached)) return cached != null ? cached : null;
			var res = CloneHelper.FindResource(c.srcParentPath, c.srcName);
			s_resourceCache[c.id] = res;
			return res;
		}

		/// <summary>Spiel-Objekt (z. B. Pitstop) „verschieben“: Klon an derselben Stelle, Original wird ausgeblendet.</summary>
		public static PlacedClone? ReplaceVanilla(GameObject root, out string error)
		{
			error = "";
			var existing = LayoutStore.Layout.clones.Find(x => x.replacesSource && x.srcName == root.name
				&& x.srcParentPath == CloneHelper.PathOf(root.transform.parent));
			if (existing != null) return existing;
			Vector3 pos = root.transform.position - GameAccess.OriginShift;
			var c = new PlacedClone
			{
				srcParentPath = CloneHelper.PathOf(root.transform.parent),
				srcName = root.name,
				srcPosition = PlacedTrack.FromVec(pos),
				position = PlacedTrack.FromVec(pos),
				yaw = 0f,
				replacesSource = true,
			};
			LayoutStore.Layout.clones.Add(c);
			// Abgeschaltete Module des Originals gelten künftig für den Klon
			string oldPath = CloneHelper.PathOf(root.transform);
			if (!Build(c, out error)) { LayoutStore.Layout.clones.Remove(c); return null; }
			string newPath = CloneHelper.PathOf(s_built[c.id].transform);
			PitstopEditor.MovePath(oldPath, newPath);
			LayoutStore.SaveQuiet();
			return c;
		}

		public static bool Build(PlacedClone c, out string error)
		{
			error = "";
			if (s_built.TryGetValue(c.id, out var existing) && existing != null) return true;
			if (!GameAccess.TracksReady) { error = Tr.T("Welt noch nicht bereit", "World not ready yet"); return false; }
			// Schon vorhanden (z. B. Zustand zurückgesetzt, Objekt aber noch da)? Übernehmen statt doppelt bauen.
			var existing2 = ExistingMarker(c.id);
			if (existing2 != null)
			{
				s_built[c.id] = existing2;
				HideSource(c);
				return true;
			}
			HookScenes();
			if (s_nextTry.TryGetValue(c.id, out float next) && Time.realtimeSinceStartup < next) { error = Tr.T("Quelle (noch) nicht geladen", "Source not loaded (yet)"); return false; }
			var src = Source(c);
			if (src == null) s_nextTry[c.id] = Time.realtimeSinceStartup + 15f;
			if (src == null) { error = Tr.T("Quelle (noch) nicht geladen", "Source not loaded (yet)"); return false; }
			try
			{
				GameObject clone = CloneHelper.InstantiateInactive(src, NamePrefix + c.id);
				// Deaktivierte Quelle (oder deaktivierte Teile darin): die Kopie bekommt den Originalzustand
				int restored = ObjectDisabler.RestoreInClone(src, clone);
				if (restored > 0) Main.Log($"Klon {c.srcName}: {restored} deaktivierte Teile der Quelle in der Kopie wieder normal");
				clone.AddComponent<CloneMarker>().id = c.id;
				// Zugehörige Collider aus der Nachbarschaft mitnehmen (gleiche Lage relativ zur Quelle)
				clone.transform.SetPositionAndRotation(src.transform.position, src.transform.rotation);
				int companions = 0;
				foreach (var comp in c.noCompanions ? new List<GameObject>() : (ObjectDisabler.KnownCompanions(src) ?? CloneHelper.ColliderCompanions(src)))
				{
					var cc = Object.Instantiate(comp, comp.transform.position, comp.transform.rotation);
					cc.name = comp.name;
					cc.transform.localScale = comp.transform.lossyScale;
					cc.transform.SetParent(clone.transform, true);
					// war das Original deaktiviert (Gruppe aus, Collider aus), soll die Kopie trotzdem fest sein
					cc.SetActive(ObjectDisabler.OriginalActive(comp));
					foreach (var col in cc.GetComponentsInChildren<Collider>(true)) col.enabled = true;
					companions++;
				}
				if (companions > 0) Main.Log($"Klon {c.srcName}: {companions} zugehörige Collider-Gruppe(n) mitkopiert");
				clone.transform.SetPositionAndRotation(c.Position + GameAccess.OriginShift, RotationFor(c, src.transform));
				ApplyColliderEdits(c, clone);
				int modules = PitstopEditor.PrepareClone(clone);
				clone.SetActive(true);
				clone.transform.SetParent(GameAccess.Registry!.TrackRootParent, true);
				if (clone.GetComponentInChildren<PluggableObject>(true) != null) CloneHelper.LogInteractables(src, clone);
				s_built[c.id] = clone;
				s_failed.Remove(c.id);
				HideSource(c, src);
				if (modules > 0) Main.Log($"Pitstop-Klon {c.id}: {modules} Service-Module");
				return true;
			}
			catch (Exception e)
			{
				error = e.GetType().Name + ": " + e.Message;
				Main.Log($"Klon {c.id} fehlgeschlagen: {e}");
				return false;
			}
		}

		// ------------------------------------------------------------------
		// Einzelne Collider im Klon ausschalten (z. B. unsichtbare Wände, die mitten auf einem Gleis stehen)
		// ------------------------------------------------------------------

		/// <summary>Schlüssel eines Colliders im Klon: relativer Pfad + Index unter den Collidern dieses Objekts.</summary>
		public static string ColliderKey(GameObject cloneRoot, Collider col)
		{
			var parts = new List<string>();
			for (var t = col.transform; t != null && t != cloneRoot.transform; t = t.parent) parts.Insert(0, t.name);
			int idx = Array.IndexOf(col.GetComponents<Collider>(), col);
			return (parts.Count == 0 ? "." : string.Join("/", parts)) + "#" + idx;
		}

		private static Collider? FindCollider(GameObject cloneRoot, string key)
		{
			int hash = key.LastIndexOf('#');
			if (hash < 0 || !int.TryParse(key.Substring(hash + 1), out int idx)) return null;
			string path = key.Substring(0, hash);
			Transform? t = path == "." ? cloneRoot.transform : cloneRoot.transform.Find(path);
			var cols = t != null ? t.GetComponents<Collider>() : Array.Empty<Collider>();
			return idx >= 0 && idx < cols.Length ? cols[idx] : null;
		}

		public static void ApplyColliderEdits(PlacedClone c, GameObject cloneRoot)
		{
			foreach (var key in c.disabledColliders)
			{
				var col = FindCollider(cloneRoot, key);
				if (col != null) col.enabled = false;
			}
		}

		/// <summary>Collider im Klon ausschalten und merken.</summary>
		public static void DisableCollider(PlacedClone c, Collider col)
		{
			if (!s_built.TryGetValue(c.id, out var root) || root == null) return;
			string key = ColliderKey(root, col);
			if (!c.disabledColliders.Contains(key)) c.disabledColliders.Add(key);
			col.enabled = false;
			LayoutStore.SaveQuiet();
		}

		/// <summary>Alle ausgeschalteten Collider wieder einschalten.</summary>
		public static int EnableAllColliders(PlacedClone c)
		{
			int n = 0;
			if (s_built.TryGetValue(c.id, out var root) && root != null)
				foreach (var key in c.disabledColliders)
				{
					var col = FindCollider(root, key);
					if (col != null) { col.enabled = true; n++; }
				}
			c.disabledColliders.Clear();
			LayoutStore.SaveQuiet();
			return n;
		}

		/// <summary>Klon neu bauen (z. B. nach Ändern von „Collider-Gruppen mitkopieren“).</summary>
		public static bool Rebuild(PlacedClone c, out string error)
		{
			if (s_built.TryGetValue(c.id, out var go) && go != null) Object.DestroyImmediate(go); // sofort – Build würde ihn sonst wieder übernehmen
			s_built.Remove(c.id);
			return Build(c, out error);
		}

		/// <summary>Lage eines gebauten Klons aktualisieren (Drehen, Höhe, Verschieben).</summary>
		public static void SetPose(PlacedClone c)
		{
			if (!s_built.TryGetValue(c.id, out var go) || go == null) return;
			var src = Source(c);
			Quaternion rot = src != null ? RotationFor(c, src.transform) : Quaternion.Euler(0f, c.yaw, 0f);
			go.transform.SetPositionAndRotation(c.Position + GameAccess.OriginShift, rot);
		}

		/// <summary>
		/// Klon löschen (Funkgerät): ersetzt er ein Spiel-Objekt, kommt das Original zurück und abgeschaltete
		/// Pitstop-Module gelten wieder dafür; sonst werden seine Modul-Einträge verworfen. true = Original wiederhergestellt.
		/// </summary>
		public static bool Delete(PlacedClone c)
		{
			string clonePath = s_built.TryGetValue(c.id, out var go) && go != null ? CloneHelper.PathOf(go.transform) : "";
			string srcPath = (c.srcParentPath.Length > 0 ? c.srcParentPath + "/" : "") + c.srcName;
			bool reset = c.replacesSource;
			Remove(c.id);
			LayoutStore.Remove(c.id);
			if (clonePath != "")
			{
				if (reset) PitstopEditor.MovePath(clonePath, srcPath);
				else LayoutStore.Layout.pitstopModules.RemoveAll(e => e.parentPath == clonePath || e.parentPath.StartsWith(clonePath + "/", StringComparison.Ordinal));
			}
			LayoutStore.SaveQuiet();
			return reset;
		}

		public static bool Remove(string id)
		{
			VegetationBuilder.RemoveClone(id); // Maske liegt nicht mehr unter dem Klon (statische Wurzel)
			if (s_built.TryGetValue(id, out var go) && go != null) Object.Destroy(go);
			s_built.Remove(id);
			// Ersetzte Quelle (verschobener Spiel-Pitstop) wieder einblenden
			var c = LayoutStore.Layout.clones.Find(x => x.id == id);
			if (c != null && c.replacesSource)
			{
				var src = Source(c);
				if (src != null) src.SetActive(true);
			}
			return true;
		}

		/// <summary>Quelle ausblenden, wenn der Klon sie ersetzt (gestreamte Kacheln laden sie evtl. neu → im Läufer wiederholt).</summary>
		private static void HideSource(PlacedClone c, GameObject? src = null)
		{
			if (!c.replacesSource) return;
			src ??= Source(c);
			if (src != null && src.activeSelf) src.SetActive(false);
		}

		/// <summary>Fehlende Klone nachbauen (vom Läufer alle zwei Sekunden aufgerufen).</summary>
		public static void Tick()
		{
			if (!GameAccess.TracksReady) return;
			foreach (var c in LayoutStore.Layout.clones)
			{
				if (s_built.TryGetValue(c.id, out var go) && go != null) { HideSource(c); continue; }
				if (!Build(c, out string e) && !s_failed.Contains(c.id))
				{
					s_failed.Add(c.id); // nur einmal loggen
					Main.Log($"Klon {c.srcName}: {e} – wird nachgeholt, sobald die Quelle geladen ist.");
				}
			}
		}

		/// <summary>Nur einen Klon prüfen, damit gestreamte Objekte keine periodische Frame-Spitze erzeugen.</summary>
		public static void TickOne(int index)
		{
			if (!GameAccess.TracksReady || index < 0 || index >= LayoutStore.Layout.clones.Count) return;
			var c = LayoutStore.Layout.clones[index];
			if (s_built.TryGetValue(c.id, out var go) && go != null) { HideSource(c); return; }
			if (!Build(c, out string e) && !s_failed.Contains(c.id))
			{
				s_failed.Add(c.id);
				Main.Log($"Klon {c.srcName}: {e} – wird nachgeholt, sobald die Quelle geladen ist.");
			}
		}

		/// <summary>Eigener Klon zu einem getroffenen Objekt (oder null).</summary>
		public static PlacedClone? CloneOf(Transform hit)
		{
			var m = hit.GetComponentInParent<CloneMarker>();
			return m != null ? LayoutStore.Layout.clones.Find(x => x.id == m.id) : null;
		}

		// ---------- Vorschau ----------

		/// <summary>Halbtransparente Kopie der Quelle zeigen: Pivot auf pivotStatic, gedreht um yaw relativ zur Quelle.</summary>
		public static void ShowPreview(GameObject source, Vector3 pivotStatic, float yaw)
		{
			if (s_preview == null || s_previewSource != source)
			{
				HidePreview(true);
				s_preview = CloneHelper.MakeGhost(source, "[TrackPlacer] Klon-Vorschau");
				s_previewSource = source;
			}
			s_preview.transform.SetPositionAndRotation(pivotStatic + GameAccess.OriginShift, Quaternion.Euler(0f, yaw, 0f) * source.transform.rotation);
			if (!s_preview.activeSelf) s_preview.SetActive(true);
		}

		public static void HidePreview(bool destroy = false)
		{
			if (s_preview == null) return;
			if (destroy) { Object.Destroy(s_preview); s_preview = null; s_previewSource = null; }
			else s_preview.SetActive(false);
		}

		/// <summary>Abstand Pivot → Unterkante der Quelle (für das Absetzen auf den Boden).</summary>
		public static float PivotAboveBottom(GameObject source)
		{
			var b = CloneHelper.LocalBounds(source.transform);
			return -b.min.y;
		}
	}
}
