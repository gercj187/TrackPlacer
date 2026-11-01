using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TrackPlacer.World
{
	/// <summary>Gemeinsame Werkzeuge zum Klonen und Wiederfinden von Szenenobjekten.</summary>
	internal static class CloneHelper
	{
		/// <summary>Pfad von der Szenen-Wurzel (Namen mit '/').</summary>
		public static string PathOf(Transform? t) => t == null ? "" : (t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name);

		/// <summary>Objekt über Elternpfad + Name + Position (statisch, ±radius) wiederfinden. Nur aktive Eltern.</summary>
		public static GameObject? Find(string parentPath, string name, Vector3 staticPos, float radius = 3f)
		{
			Vector3 shift = GameAccess.OriginShift;
			IEnumerable<Transform> candidates;
			if (string.IsNullOrEmpty(parentPath))
			{
				var go = GameObject.Find("/" + name);
				candidates = go != null ? new[] { go.transform } : Array.Empty<Transform>();
			}
			else
			{
				var parent = GameObject.Find("/" + parentPath);
				if (parent == null) return null;
				candidates = parent.transform.Cast<Transform>().Where(c => c.name == name);
			}
			Transform? best = null;
			float bestD = radius * radius;
			foreach (var c in candidates)
			{
				float d = ((c.position - shift) - staticPos).sqrMagnitude;
				if (d < bestD) { bestD = d; best = c; }
			}
			return best != null ? best.gameObject : null;
		}

		/// <summary>Zusätzlich geladene Prefab-/Ressourcenwurzeln finden; dient vor allem weltweiten Pitstop-Vorlagen.</summary>
		private static readonly HashSet<string> FunctionalTypes = new HashSet<string>
		{
			"RailTrack", "TurntableRailTrack", "TurntableController", "Junction", "BufferStop", "TrainCar",
			"LocoResourceModule", "PitStopStation", "StationController", "WarehouseMachineController",
		};

		/// <summary>
		/// Gehört das Objekt (oder ein Elternteil) zu einer Spielfunktion – Gleis, Drehscheibe, Weiche, Prellbock,
		/// Pitstop, Bahnhof? Dann nie als „zugehörige Collider“ oder „Schatten-Kopie“ eines anderen Objekts behandeln.
		/// (Beispiel: Drehscheibe im Lokschuppen deaktiviert → die Brücke der echten Drehscheibe verlor ihre Collider.)
		/// </summary>
		public static bool IsFunctional(Transform t)
		{
			for (var p = t; p != null; p = p.parent)
			{
				if (p.name == "[railway]" || p.name == "origin_shift_parent") break;
				foreach (var c in p.GetComponents<Component>())
					if (c != null && FunctionalTypes.Contains(c.GetType().Name)) return true;
			}
			// auch darunter (Collider-Gruppe, die eine Drehscheibe enthält)
			foreach (var c in t.GetComponentsInChildren<Component>(true))
				if (c != null && FunctionalTypes.Contains(c.GetType().Name)) return true;
			return false;
		}

		/// <summary>Große Sammel-Wurzeln, unter denen viele fremde Objekte hängen.</summary>
		public static bool IsCollectorRoot(Transform t) => t.parent == null || t.name == "[railway]" || t.name == "origin_shift_parent";

		public static GameObject? FindResource(string parentPath, string name)
		{
			// Resources.FindObjectsOfTypeAll geht über ALLE Objekte samt Assets (~100 ms). Einmal pro Welt eine
			// Namensliste der szenenlosen Objekte bauen, danach nur noch nachschlagen.
			if (s_resByName == null)
			{
				s_resByName = new Dictionary<string, List<Transform>>();
				foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
				{
					if (t == null || t.gameObject.scene.IsValid()) continue;
					if (!s_resByName.TryGetValue(t.name, out var l)) s_resByName[t.name] = l = new List<Transform>();
					l.Add(t);
				}
			}
			if (!s_resByName.TryGetValue(name, out var list)) return null;
			foreach (var t in list)
				if (t != null && PathOf(t.parent) == parentPath) return t.gameObject;
			return null;
		}

		private static Dictionary<string, List<Transform>>? s_resByName;
		public static void ResetResourceCache() => s_resByName = null;

		/// <summary>
		/// Inaktive Kopie (kein Awake/OnEnable, die Quelle bleibt unberührt). Laufzeit-Komponenten der Quelle
		/// (Hebel-Implementierungen, deren Rigidbody/Gelenke, DVConvertToEntity) werden entfernt – sie entstehen beim
		/// Aktivieren neu. Ergebnis: inaktiv, ohne Eltern.
		/// </summary>
		/// <summary>
		/// Collider, die zum Objekt gehören, aber woanders in der Hierarchie hängen (z. B. „Colliders“-Gruppe neben dem
		/// Modell): reine Collider-Objekte (ohne sichtbares Modell) unter den Eltern/Großeltern der Quelle, nicht in der
		/// Quelle selbst, deren Mitte in den sichtbaren Grenzen der Quelle liegt und die nicht größer sind.
		/// Es wird jeweils der oberste solche Knoten genommen.
		/// </summary>
		public static List<GameObject> ColliderCompanions(GameObject source)
		{
			var result = new List<GameObject>();
			Bounds? vb = null;
			foreach (var r in source.GetComponentsInChildren<Renderer>())
			{
				if (r == null || ObjectDisabler.SkipRenderer(r)) continue;
				if (vb == null) vb = r.bounds; else { var x = vb.Value; x.Encapsulate(r.bounds); vb = x; }
			}
			if (vb == null) return result;
			float maxSize = vb.Value.size.magnitude * 1.2f + 0.5f;
			// Gedrehte Box des Objekts (lokal) statt achsparalleler Weltgrenzen: bei schräg stehenden Objekten reichte
			// die Weltbox bis über das Nachbarobjekt – dessen Collider wurde dann „mit ausgeschaltet“.
			Bounds lb = LocalBounds(source.transform);
			lb.Expand(0.6f);
			Quaternion inv = Quaternion.Inverse(source.transform.rotation);
			Vector3 srcPos = source.transform.position;
			Vector3 srcCenter = vb.Value.center;
			bool InsideSource(Vector3 world) => lb.Contains(inv * (world - srcPos));

			var scopes = new List<Transform>();
			// Nie in den großen Sammel-Wurzeln suchen ([railway], origin_shift_parent …): dort hängen Gleise,
			// Drehscheiben und Weichen anderer Objekte (ein Klon liegt unter [railway]!).
			foreach (var sc in new[] { source.transform.parent, source.transform.parent?.parent })
				if (sc != null && !IsCollectorRoot(sc)) scopes.Add(sc);
			var seen = new HashSet<Transform>();
			var visible = new Dictionary<Transform, bool>();
			bool Vis(Transform t)
			{
				if (!visible.TryGetValue(t, out bool v)) visible[t] = v = ObjectDisabler.HasVisible(t.gameObject);
				return v;
			}
			foreach (var scope in scopes)
			{
				// sichtbare Nachbarobjekte im selben Bereich: eine Collider-Gruppe gehört zum NÄCHSTEN Objekt
				var others = new List<Vector3>();
				foreach (Transform ch in scope)
					if (ch != source.transform && !source.transform.IsChildOf(ch) && Vis(ch) && !ObjectDisabler.IsHelperName(ch.name))
					{
						var wb = ObjectDisablerBounds(ch.gameObject);
						if (wb.HasValue) others.Add(wb.Value.center);
					}
				foreach (var col in scope.GetComponentsInChildren<Collider>())
				{
					if (col == null || col.isTrigger || col.transform.IsChildOf(source.transform)) continue;
					// obersten reinen Collider-Knoten unterhalb des Suchbereichs finden
					Transform node = col.transform;
					while (node.parent != null && node.parent != scope && !node.parent.IsChildOf(source.transform)
						&& source.transform.parent != node.parent && !Vis(node.parent))
						node = node.parent;
					if (seen.Contains(node) || (Vis(node) && !ObjectDisabler.IsHelperName(node.name)) || source.transform.IsChildOf(node)) continue;
					if (IsFunctional(node)) { seen.Add(node); continue; } // Drehscheibe, Gleis, Weiche … nie mitnehmen
					seen.Add(node);
					Bounds cb = col.bounds;
					foreach (var c2 in node.GetComponentsInChildren<Collider>()) if (c2 != null) cb.Encapsulate(c2.bounds);
					if (!InsideSource(cb.center) || cb.size.magnitude > maxSize) continue;
					float dSrc = (cb.center - srcCenter).sqrMagnitude;
					if (others.Exists(o => (cb.center - o).sqrMagnitude < dSrc)) continue; // liegt näher an einem anderen Objekt
					result.Add(node.gameObject);
				}
			}
			return result;
		}

		/// <summary>Weltgrenzen der sichtbaren Teile.</summary>
		private static Bounds? ObjectDisablerBounds(GameObject go)
		{
			Bounds? b = null;
			foreach (var r in go.GetComponentsInChildren<Renderer>())
			{
				if (r == null || ObjectDisabler.SkipRenderer(r)) continue;
				if (b == null) b = r.bounds; else { var x = b.Value; x.Encapsulate(r.bounds); b = x; }
			}
			return b;
		}

		public static GameObject InstantiateInactive(GameObject source, string name)
		{
			var holder = new GameObject("[TP]_holder");
			holder.SetActive(false);
			var clone = Object.Instantiate(source, holder.transform);
			clone.name = name;
			StripRuntimeComponents(clone);
			EnsurePluggablePhysics(clone);
			FixStaticBatched(source, clone);
			clone.SetActive(false);
			clone.transform.SetParent(null, true);
			clone.transform.localScale = source.transform.lossyScale; // Maßstab der Quelle samt Eltern übernehmen
			Object.Destroy(holder);
			return clone;
		}

		/// <summary>
		/// Geklonte Zapfpistolen übernehmen häufig den kinematischen Halter-Zustand der Vorlage. PluggableObject
		/// schaltet diesen kopierten Rigidbody beim bloßen Loslassen nicht zuverlässig zurück. Der Wächter macht ihn
		/// deshalb genau dann dynamisch, wenn kein Socket verbunden ist und keine Hand das Objekt hält.
		/// </summary>
		private static void EnsurePluggablePhysics(GameObject clone)
		{
			foreach (var plug in clone.GetComponentsInChildren<PluggableObject>(true))
				if (plug.GetComponent<PluggablePhysicsGuard>() == null)
					plug.gameObject.AddComponent<PluggablePhysicsGuard>();
		}

		private sealed class PluggablePhysicsGuard : MonoBehaviour
		{
			private PluggableObject? plug;
			private Rigidbody[] bodies = Array.Empty<Rigidbody>();
			private bool released;

			private void Awake()
			{
				plug = GetComponent<PluggableObject>();
				bodies = GetComponents<Rigidbody>();
				if (bodies.Length == 0) bodies = GetComponentsInChildren<Rigidbody>(true);
			}

			private void LateUpdate()
			{
				if (plug == null) return;
				bool free = plug.Socket == null && !plug.IsHeldInHand;
				if (!free) { released = false; return; }
				if (released) return;
				foreach (var rb in bodies)
				{
					if (rb == null) continue;
					rb.useGravity = true;
					rb.isKinematic = false;
					rb.WakeUp();
				}
				released = true;
			}
		}

		/// <summary>
		/// Laufzeit-Komponenten der Quelle entfernen, die das Spiel beim Aktivieren selbst neu anlegt – sonst gibt es sie
		/// doppelt („Can't add … already added“) und das Bedienelement ist tot. Woher bekannt: ControlsInstantiator.TypeMap
		/// ordnet jedem „Spec“ (im Prefab gespeichert) die Implementierung zu, die er in Awake hinzufügt.
		///  - nur diese Implementierung entfernen (nicht alles, was ControlImplBase ist)
		///  - Rigidbody/Gelenke nur bei Hebeln u. Ä. entfernen (deren Implementierung legt sie neu an);
		///    Gegenstände (ItemBase, z. B. Zapfpistole) behalten ihre fest verbauten Rigidbodies und Gelenke zum Schlauch.
		/// </summary>
		public static void StripRuntimeComponents(GameObject clone)
		{
			System.Collections.IDictionary? map = null;
			try
			{
				map = typeof(DV.CabControls.ControlsInstantiator).GetField("TypeMap", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
					?.GetValue(null) as System.Collections.IDictionary;
			}
			catch { /* egal */ }

			var doomed = new List<Component>();
			int removed = 0;
			foreach (var impl in clone.GetComponentsInChildren<DV.CabControls.ControlImplBase>(true))
			{
				if (impl == null) continue;
				var go = impl.gameObject;
				bool spawnedBySpec = map == null; // ohne Tabelle: wie bisher annehmen
				if (map != null)
					foreach (var comp in go.GetComponents<Component>())
					{
						if (comp == null || !map.Contains(comp.GetType())) continue;
						object? entry = map[comp.GetType()];
						foreach (var fn in new[] { "pc", "vr", "Item1", "Item2" })
						{
							var f = entry?.GetType().GetField(fn);
							if (f?.GetValue(entry) is Type t && t == impl.GetType()) spawnedBySpec = true;
						}
					}
				if (!spawnedBySpec) continue; // fest im Prefab → behalten

				// Zapfpistolen/Kabelstecker sind keine ItemBase, brauchen ihren Rigidbody aber genauso dauerhaft:
				// PluggableObject setzt ihn beim Ein-/Ausstecken selbst kinematisch bzw. dynamisch.
				bool isItem = go.GetComponent<PluggableObject>() != null;
				for (var bt = impl.GetType(); bt != null; bt = bt.BaseType) if (bt.Name == "ItemBase") { isItem = true; break; }
				removed++;
				// Alles, was die Implementierung beim Start auf ihrem Objekt anlegt (Audio, Hilfs-Skripte …)
				foreach (var c in go.GetComponents<Component>())
				{
					if (c == null || c is Transform) continue;
					string n = c.GetType().Name;
					if (ImplHelperTypes.Contains(n)) doomed.Add(c);
					else if (!isItem && (c is Joint || c is Rigidbody)) doomed.Add(c); // Hebel u. Ä. legen Physik neu an
				}
				doomed.Add(impl);
				if (isItem)
				{
					// Gegenstand behält seinen Rigidbody (Schlauch/Kabel hängen daran) – dann richtet ItemBase.Awake ihn
					// aber nicht neu ein. Die Kopie hat den Zustand der Quelle (z. B. kinematisch im Halter) → beim
					// Loslassen würde sie schweben. Also so einstellen wie ItemBase es bei einem neuen Rigidbody tut.
					var irb = go.GetComponent<Rigidbody>();
					if (irb != null) { irb.isKinematic = false; irb.useGravity = true; }
				}
			}

			// Überall in der Kopie: Griff-Handler (AGrabHandler.AddGrabHandler fügt bei jedem Start einen NEUEN hinzu –
			// die kopierten alten würden zuerst gefunden und hängen an der Quelle → nichts greifbar/drückbar) und
			// Gelenk-Hilfen, die nur zur Laufzeit entstehen (sonst findet LocoResourceModule.InitHJAF den alten
			// HingeJointAngleFix ohne Gelenk und der Hebel bleibt tot).
			foreach (var c in clone.GetComponentsInChildren<Component>(true))
			{
				if (c == null) continue;
				string n = c.GetType().Name;
				if (IsGrabHandler(c.GetType()) || AlwaysRuntimeTypes.Contains(n)) doomed.Add(c);
			}

			// In mehreren Runden löschen (RequireComponent-Abhängigkeiten: erst Abhängige, dann den Rest)
			int grabbers = doomed.Count(c => c != null && IsGrabHandler(c.GetType()));
			for (int pass = 0; pass < 4 && doomed.Count > 0; pass++)
			{
				var left = new List<Component>();
				foreach (var c in doomed.Distinct().OrderBy(x => x is Rigidbody ? 1 : 0))
				{
					if (c == null) continue;
					try { Object.DestroyImmediate(c); } catch { /* unten erneut */ }
					if (c != null) left.Add(c); // ließ sich (noch) nicht löschen (Abhängigkeit) → nächste Runde
				}
				doomed = left;
			}
			if (removed > 0 || grabbers > 0)
				Main.Log($"Kopie {clone.name}: {removed} Bedienelemente und {grabbers} Griff-Handler der Quelle entfernt (werden beim Start neu angelegt)");
		}

		private static bool IsGrabHandler(Type t)
		{
			for (var bt = t; bt != null; bt = bt.BaseType) if (bt.Name == "AGrabHandler") return true;
			return false;
		}

		/// <summary>Werden nur zur Laufzeit angelegt (nie im Prefab) – in einer Kopie immer veraltet.</summary>
		private static readonly HashSet<string> AlwaysRuntimeTypes = new HashSet<string>
		{
			"DVConvertToEntity", "HingeJointAngleFix", "RotaryAmplitudeChecker", "PluggableObjectKinematicVRFix",
			"VelocityParent", "CabItemRigidbody", "ItemReparentingNonVR", "NonAABBReflectionProbeSampler",
		};

		/// <summary>Legen Hebel-/Knopf-/Gegenstands-Implementierungen in Awake/Start auf ihrem Objekt an.</summary>
		private static readonly HashSet<string> ImplHelperTypes = new HashSet<string>
		{
			"SteppedJoint", "SteppedPuller", "LeverAudio", "RotaryAudio", "ToggleSwitchAudio", "PullerAudio",
			"CollisionSound", "ItemBuoyancy", "ItemTransparencyHandler", "PageBookTransparencyHandler", "SnappableItem",
			"RespawnOnDrop", "ItemScrollingNonVR", "ItemScrollingVR", "ItemPositionHandler",
		};

		/// <summary>Diagnose: Bedienelemente/Gegenstände der Quelle und der Kopie ins Log (bei nicht greifbaren Teilen).</summary>
		public static void LogInteractables(GameObject source, GameObject clone)
		{
			string Describe(GameObject root)
			{
				var lines = new List<string>();
				foreach (var t in root.GetComponentsInChildren<Transform>(true))
				{
					var comps = t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name).ToList();
					if (comps.Any(n => n.Contains("Item") || n.Contains("Plug") || n.Contains("Grab") || n.Contains("Lever") || n == "Rigidbody" || n.EndsWith("Joint")))
					{
						var rb = t.GetComponent<Rigidbody>();
						string phys = rb != null ? $" [kinematisch={rb.isKinematic}, Schwerkraft={rb.useGravity}, schläft={rb.IsSleeping()}]" : "";
						lines.Add($"  {t.name}{(t.gameObject.activeInHierarchy ? "" : " (inaktiv)")}: {string.Join(", ", comps)}{phys}");
					}
				}
				return string.Join("\n", lines);
			}
			Main.Log($"Bedienteile Quelle {source.name}:\n{Describe(source)}\nBedienteile Kopie {clone.name}:\n{Describe(clone)}");
			// nach dem Start der Kopie (Awake/Start/Coroutinen) noch einmal – zeigt, ob Gegenstände Physik haben
			var later = clone.AddComponent<DelayedLog>();
			later.describe = () => $"Bedienteile Kopie {clone.name} nach 5 s:\n{Describe(clone)}";
		}

		private sealed class DelayedLog : MonoBehaviour
		{
			public Func<string>? describe;
			private void Start() => Invoke(nameof(Run), 5f);
			private void Run()
			{
				try { if (describe != null) Main.Log(describe()); } catch { /* egal */ }
				Destroy(this);
			}
		}

		// ------------------------------------------------------------------
		// Statisches Batching: das Spiel fasst unbewegliche Objekte (Masten, Gebäude) zu einem gemeinsamen
		// „Combined Mesh“ zusammen. Eine Kopie davon zeichnet nichts bzw. hat riesige Grenzen. Wir suchen das
		// Original-Mesh über den Objektnamen und setzen es in der Kopie ein.
		// ------------------------------------------------------------------

		private static Dictionary<string, Mesh>? s_meshByName;

		public static bool IsBatched(MeshFilter mf)
		{
			var r = mf.GetComponent<MeshRenderer>();
			return (r != null && r.isPartOfStaticBatch) || (mf.sharedMesh != null && mf.sharedMesh.name.StartsWith("Combined Mesh"));
		}

		/// <summary>Original-Mesh zu einem gebatchten Objekt (Name des Objekts, ohne „ (1)“ / „_LOD0“), sonst null.</summary>
		public static Mesh? OriginalMesh(MeshFilter mf)
		{
			if (!IsBatched(mf)) return mf.sharedMesh;
			if (s_meshByName == null)
			{
				s_meshByName = new Dictionary<string, Mesh>();
				foreach (var m in Resources.FindObjectsOfTypeAll<Mesh>())
					if (m != null && !m.name.StartsWith("Combined Mesh") && !s_meshByName.ContainsKey(m.name)) s_meshByName[m.name] = m;
			}
			string n = mf.gameObject.name;
			foreach (var cand in new[] { n, System.Text.RegularExpressions.Regex.Replace(n, @"\s*\(\d+\)$", ""), System.Text.RegularExpressions.Regex.Replace(n, @"_LOD\d+$", "") })
				if (s_meshByName.TryGetValue(cand, out var mesh)) return mesh;
			// Kollisions-Mesh als Notlösung (oft identisch mit dem sichtbaren)
			var mc = mf.GetComponent<MeshCollider>();
			return mc != null && mc.sharedMesh != null && !mc.sharedMesh.name.StartsWith("Combined Mesh") ? mc.sharedMesh : null;
		}

		/// <summary>In einer Kopie die gebatchten Meshes durch die Originale ersetzen. Liefert (ersetzt, nicht gefunden).</summary>
		public static (int fixedCount, int missing) FixStaticBatched(GameObject source, GameObject clone)
		{
			var src = source.GetComponentsInChildren<MeshFilter>(true);
			var dst = clone.GetComponentsInChildren<MeshFilter>(true);
			int ok = 0, miss = 0;
			for (int i = 0; i < src.Length && i < dst.Length; i++)
			{
				if (!IsBatched(src[i])) continue;
				var mesh = OriginalMesh(src[i]);
				if (mesh != null) { dst[i].sharedMesh = mesh; ok++; }
				else
				{
					miss++;
					var r = dst[i].GetComponent<MeshRenderer>();
					if (r != null) r.enabled = false; // nichts Falsches zeichnen
				}
			}
			if (ok + miss > 0) Main.Log($"Kopie von {source.name}: {ok} gebatchte Meshes ersetzt, {miss} nicht gefunden");
			return (ok, miss);
		}

		/// <summary>Begrenzung aller sichtbaren Meshes relativ zur Wurzel in deren Drehung (Weltmaßstab).
		/// Gebatchte Meshes zählen mit ihrem Original-Mesh (oder gar nicht, wenn es fehlt).</summary>
		public static Bounds LocalBounds(Transform root)
		{
			Quaternion inv = Quaternion.Inverse(root.rotation);
			bool any = false;
			var b = new Bounds();
			foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
			{
				var mesh = OriginalMesh(mf);
				if (mesh == null) continue;
				var rend = mf.GetComponent<MeshRenderer>();
				if (rend != null && !rend.enabled) continue;
				Bounds mb = mesh.bounds;
				for (int i = 0; i < 8; i++)
				{
					Vector3 c = new Vector3((i & 1) == 0 ? mb.min.x : mb.max.x, (i & 2) == 0 ? mb.min.y : mb.max.y, (i & 4) == 0 ? mb.min.z : mb.max.z);
					Vector3 local = inv * (mf.transform.TransformPoint(c) - root.position);
					if (!any) { b = new Bounds(local, Vector3.zero); any = true; } else b.Encapsulate(local);
				}
			}
			return any ? b : new Bounds(new Vector3(0f, 0.5f, 0f), Vector3.one);
		}

		/// <summary>Höhe des Pivots über dem Boden darunter (Boden = großer Collider, nicht das Objekt selbst).</summary>
		public static float PivotAboveGround(Transform t, float fallback)
		{
			var hits = Physics.RaycastAll(t.position + Vector3.up * 0.5f, Vector3.down, 30f, ~0, QueryTriggerInteraction.Ignore);
			System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
			foreach (var h in hits)
			{
				if (h.transform.IsChildOf(t)) continue;
				var sz = h.collider.bounds.size;
				if (sz.x < 2f && sz.z < 2f) continue; // kleine Teile (Pfosten, Steine) überspringen
				return t.position.y - h.point.y;
			}
			return fallback;
		}

		/// <summary>Halbtransparente Vorschau (nur Meshes, feinste LOD-Stufe), Pivot = Pivot der Quelle.</summary>
		public static GameObject MakeGhost(GameObject source, string name)
		{
			var root = new GameObject(name);
			Transform tr = source.transform;
			Quaternion inv = Quaternion.Inverse(tr.rotation);
			var skip = new HashSet<Renderer>();
			foreach (var lg in source.GetComponentsInChildren<LODGroup>(true))
			{
				var lods = lg.GetLODs();
				for (int i = 1; i < lods.Length; i++) foreach (var r in lods[i].renderers) if (r != null) skip.Add(r);
			}
			foreach (var mr in source.GetComponentsInChildren<MeshRenderer>(true))
			{
				if (skip.Contains(mr) || !mr.enabled) continue;
				var mf = mr.GetComponent<MeshFilter>();
				var gmesh = mf != null ? OriginalMesh(mf) : null;
				if (gmesh == null) continue;
				var part = new GameObject(mr.name);
				part.transform.SetParent(root.transform, false);
				part.transform.localPosition = inv * (mr.transform.position - tr.position);
				part.transform.localRotation = inv * mr.transform.rotation;
				part.transform.localScale = mr.transform.lossyScale;
				part.AddComponent<MeshFilter>().sharedMesh = gmesh;
				var r2 = part.AddComponent<MeshRenderer>();
				r2.sharedMaterials = mr.sharedMaterials.Select(ObjectDisabler.Ghost).ToArray();
				r2.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
				r2.receiveShadows = false;
			}
			root.SetActive(false);
			return root;
		}
	}
}
