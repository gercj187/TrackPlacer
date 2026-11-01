using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommsRadioAPI;
using DV;
using TrackPlacer.Data;
using TrackPlacer.World;
using UnityEngine;
using InputAction = CommsRadioAPI.InputAction;
using Object = UnityEngine.Object;

namespace TrackPlacer.Radio
{
	// ======================================================================
	// Bilder auf dem Funkgerät-Display: eine Fläche mit Textur über dem Inhaltsfeld, in der LCD-Farbe.
	// Bilder sind in die DLL eingebettet; eigene PNGs in Mods/TrackPlacer/icons/<name>.png haben Vorrang.
	// ======================================================================

	internal static class RadioIcon
	{
		private static GameObject? s_quad;
		private static Material? s_mat;
		private static Component? s_content;
		private static readonly Dictionary<string, Texture2D?> s_tex = new Dictionary<string, Texture2D?>();

		/// <summary>false, sobald das Display nicht gefunden wurde – dann zeigen die Menüs Text statt Bild.</summary>
		public static bool Works { get; private set; } = true;
		/// <summary>Bilder werden gezeigt (Display gefunden und in den Settings eingeschaltet).</summary>
		public static bool Enabled => Works && Main.Settings.showIcons;

		private static Texture2D? Load(string key)
		{
			if (s_tex.TryGetValue(key, out var t)) return t;
			byte[]? bytes = null;
			try
			{
				string file = Path.Combine(Main.Mod.Path, "icons", key + ".png");
				if (File.Exists(file)) bytes = File.ReadAllBytes(file);
				else
				{
					using var st = typeof(RadioIcon).Assembly.GetManifestResourceStream("TrackPlacer.Icons." + key + ".png");
					if (st != null) { using var ms = new MemoryStream(); st.CopyTo(ms); bytes = ms.ToArray(); }
				}
			}
			catch (Exception e) { Main.Log($"Bild '{key}': {e.Message}"); }
			Texture2D? tex = null;
			if (bytes != null)
			{
				tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "TP_Icon_" + key, wrapMode = TextureWrapMode.Clamp };
				if (!ImageConversion.LoadImage(tex, bytes)) tex = null;
			}
			s_tex[key] = tex;
			return tex;
		}

		/// <summary>Inhaltsfeld des Displays suchen und die Bildfläche darüberlegen.</summary>
		private static bool Attach()
		{
			if (s_quad != null && s_content != null) return true;
			var ctrl = Object.FindObjectOfType<CommsRadioController>();
			if (ctrl == null) return false;
			var disp = ctrl.GetComponentsInChildren<Component>(true).FirstOrDefault(c => c != null && c.GetType().Name == "CommsRadioDisplay");
			s_content = disp?.GetType().GetField("content")?.GetValue(disp) as Component;
			if (s_content == null) { Works = false; Main.Log("Funkgerät-Display nicht gefunden – Menü ohne Bilder."); return false; }

			var mesh = new Mesh { name = "TP_IconQuad" };
			mesh.SetVertices(new List<Vector3> { new Vector3(-0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(0.5f, -0.5f, 0f) });
			mesh.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) });
			mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
			mesh.RecalculateBounds();

			s_quad = new GameObject("[TrackPlacer] Funkgerät-Bild");
			s_quad.transform.SetParent(s_content.transform, false);
			s_quad.AddComponent<MeshFilter>().sharedMesh = mesh;
			var mr = s_quad.AddComponent<MeshRenderer>();
			mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			mr.receiveShadows = false;
			var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Transparent");
			s_mat = new Material(sh) { name = "TP_IconMat", renderQueue = 3100 };
			mr.sharedMaterial = s_mat;
			s_quad.layer = s_content.gameObject.layer;
			Layout();
			s_quad.SetActive(false);
			return true;
		}

		/// <summary>Größe/Lage aus dem Rechteck des Inhaltsfelds (eigene Einstellungen in den Settings).</summary>
		public static void Layout()
		{
			if (s_quad == null || s_content == null) return;
			Rect r = s_content.transform is RectTransform rt ? rt.rect : new Rect(-0.5f, -0.5f, 1f, 1f);
			float size = Mathf.Max(0.0001f, Mathf.Min(r.width, r.height)) * Main.Settings.iconScale;
			s_quad.transform.localPosition = new Vector3(r.center.x + Main.Settings.iconOffsetX * r.width, r.center.y + Main.Settings.iconOffsetY * r.height, -0.01f * size);
			s_quad.transform.localRotation = Quaternion.identity;
			s_quad.transform.localScale = new Vector3(Main.Settings.iconFlipX ? -size : size, size, size);
		}

		public static void Show(string key)
		{
			if (!Enabled || !Attach()) { Hide(); return; }
			var tex = Load(key);
			if (tex == null || s_mat == null || s_quad == null) { Hide(); return; }
			s_mat.mainTexture = tex;
			Color col = new Color(0.75f, 0.9f, 0.7f, 1f);
			try { if (s_content!.GetType().GetProperty("color")?.GetValue(s_content) is Color c) col = c; } catch { /* egal */ }
			s_mat.color = col;
			if (!s_quad.activeSelf) s_quad.SetActive(true);
		}

		public static void Hide()
		{
			if (s_quad != null && s_quad.activeSelf) s_quad.SetActive(false);
		}

		public static void OnWorldUnloaded() { s_quad = null; s_content = null; Works = true; }
	}

	// ======================================================================
	// Menübaum „3D Editor“
	// ======================================================================

	internal sealed class MenuNode
	{
		public string label = "";
		public string icon = "";
		public MenuNode? parent;
		public List<MenuNode>? children;
		public Func<AStateBehaviour>? start;

		public MenuNode Add(MenuNode child) { child.parent = this; (children ??= new List<MenuNode>()).Add(child); return this; }
		public static MenuNode Sub(string label, string icon, params MenuNode[] kids)
		{
			var n = new MenuNode { label = label, icon = icon };
			foreach (var k in kids) n.Add(k);
			return n;
		}
		public static MenuNode Leaf(string label, string icon, Func<AStateBehaviour> start) => new MenuNode { label = label, icon = icon, start = start };
	}

	internal static class PlacerMenu
	{
		private static MenuNode? s_root;
		private static MenuNode? s_lastNode;
		private static int s_lastIndex = 1;

		private static bool s_rootGerman;
		/// <summary>Menübaum; bei Sprachwechsel im Spiel neu aufbauen.</summary>
		public static MenuNode Root
		{
			get
			{
				if (s_root == null || s_rootGerman != Tr.German) { s_rootGerman = Tr.German; s_root = Build(); s_lastNode = null; }
				return s_root;
			}
		}

		/// <summary>Zurück ins Menü an die Stelle, von der aus das Werkzeug gestartet wurde (Mausrad in den Werkzeugen).</summary>
		public static AStateBehaviour Return() => s_lastNode != null ? new MenuState(s_lastNode, s_lastIndex) : (AStateBehaviour)new EditorRootState();

		public static void Remember(MenuNode node, int index) { s_lastNode = node; s_lastIndex = index; }

		private static AStateBehaviour StartSwitch(bool covered)
		{
			JunctionBuilder.PlaceCovered = covered;
			return JunctionBuilder.Variants.Count > 0
				? new SwitchTypeState() : (AStateBehaviour)new MenuState(s_lastNode ?? Root, s_lastIndex, Tr.T("Keine Weichen-Vorlagen", "No switch templates"));
		}

		private static AStateBehaviour NeedStations(Func<AStateBehaviour> f, string title) =>
			StationLink.StationsReady ? f() : new MenuState(s_lastNode ?? Root, s_lastIndex, title + Tr.T(": Bahnhöfe\nnoch nicht bereit", ": stations\nnot ready yet"));

		private static MenuNode Build()
		{
			var root = MenuNode.Sub(Tr.T("3D Editor", "3D Editor"), "editor",
				MenuNode.Sub(Tr.T("Gleisbau", "Track building"), "tracks",
					MenuNode.Sub(Tr.T("Gleise verlegen", "Lay tracks"), "track_lay",
						MenuNode.Leaf(Tr.T("Schotter", "Ballast"), "ballast", () => new TrackAgeState(TrackStyle.Ballast, false, 0)),
						MenuNode.Leaf(Tr.T("Schwellen", "Sleepers"), "sleepers", () => new TrackAgeState(TrackStyle.Sleepers, false, 0)),
						MenuNode.Leaf(Tr.T("Nur Schienen", "Rails only"), "rails", () => new TrackAgeState(TrackStyle.Rails, false, 0)),
						MenuNode.Leaf(Tr.T("Asphalt", "Asphalt"), "asphalt", () => new TrackAgeState(TrackStyle.Ballast, true, 0))),
					MenuNode.Leaf(Tr.T("Gleise ändern", "Change tracks"), "track_change", () => new TrackChangePickState(null)),
					MenuNode.Leaf(Tr.T("Gleis-ID", "Track ID"), "trackid", () => NeedStations(() => new TrackIdPickState(null), Tr.T("Gleis-ID", "Track ID")))),
				MenuNode.Sub(Tr.T("Weichen", "Switches"), "switch",
					MenuNode.Leaf(Tr.T("Weichen verlegen", "Lay switches"), "switch_lay", () => StartSwitch(false)),
					MenuNode.Leaf(Tr.T("Abgedeckte Weichen", "Covered switches"), "switch_asphalt", () => StartSwitch(true)),
					MenuNode.Leaf(Tr.T("Weichen ändern", "Change switches"), "switch_change", () => new SwitchChangePickState(null))),
				MenuNode.Sub(Tr.T("Gleisobjekte", "Track objects"), "track_objects",
					MenuNode.Leaf(Tr.T("Prellbock", "Buffer stop"), "buffer", () => new BufferPickState(null)),
					MenuNode.Leaf(Tr.T("Schilder", "Signs"), "sign", () => new SignPickState(null, "", ""))),
				MenuNode.Leaf(Tr.T("Entfernen", "Remove"), "remove", () => new RemovePickState(null)),
				MenuNode.Sub(Tr.T("Objekte", "Objects"), "objects",
					MenuNode.Leaf(Tr.T("Objekte deaktivieren", "Disable objects"), "disable", () => new ObjectPickState(null, null, 0, null)),
					MenuNode.Leaf(Tr.T("Vegetation entfernen", "Remove vegetation"), "vegetation", () => new VegetationPickState(null, false)),
					MenuNode.Sub(Tr.T("Objekte platzieren", "Place objects"), "place",
						MenuNode.Leaf(Tr.T("Klonen", "Clone"), "clone", () => new ClonePickState(null, null, 0, null)),
						MenuNode.Leaf(Tr.T("Lademaschine", "Loading machine"), "machine", () => NeedStations(() => new MachinePickState(null, false), Tr.T("Lademaschine", "Loading machine"))),
						MenuNode.Sub(Tr.T("Pitstop", "Pitstop"), "pitstop",
							MenuNode.Leaf(Tr.T("Pitstop bearbeiten", "Edit pitstop"), "pitstop", () => new PitstopPickState(null, null, null)),
							MenuNode.Leaf(Tr.T("Pitstop neu", "New pitstop"), "place", () => new PitstopNewState(0))))));
			return root;
		}
	}

	/// <summary>Einstieg im Funkgerät. Mausrad wechselt hier normal die Funkgerät-Modi.</summary>
	internal sealed class EditorRootState : AStateBehaviour
	{
		public EditorRootState() : base(new CommsRadioState(
			titleText: Tr.T("3D Editor", "3D Editor"),
			contentText: RadioIcon.Enabled ? "" : Tr.T("Klick = Menü", "Click = menu"),
			actionText: Tr.Act("MENÜ"),
			buttonBehaviour: ButtonBehaviourType.Regular))
		{ }

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous)
		{
			PlacerSession.ClearEdit();
			PlacerSession.ShowHighlight(null);
			PlacerSession.HideCursor();
			RadioIcon.Show("editor");
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return this;
			utility.PlaySound(VanillaSoundCommsRadio.ModeEnter);
			return new MenuState(PlacerMenu.Root, 1);
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not MenuState) RadioIcon.Hide();
		}
	}

	/// <summary>Ein Menü: Eintrag 0 = zurück, dann die Unterpunkte. Mausrad blättert, Klick öffnet/startet.</summary>
	internal sealed class MenuState : AStateBehaviour
	{
		private readonly MenuNode node;
		private readonly int index;

		private static int Count(MenuNode n) => (n.children?.Count ?? 0) + 1;
		private static MenuNode? Item(MenuNode n, int i) => i <= 0 || n.children == null ? null : n.children[i - 1];

		public MenuState(MenuNode node, int index, string? message = null) : base(new CommsRadioState(
			titleText: Item(node, Clamp(node, index))?.label ?? (node.parent == null ? Tr.T("Schließen", "Close") : Tr.T("Zurück", "Back")),
			contentText: message ?? (RadioIcon.Enabled ? "" : Listing(node, Clamp(node, index))),
			actionText: Tr.Act(Item(node, Clamp(node, index)) is MenuNode it ? (it.children != null ? "ÖFFNEN" : "START") : "ZURÜCK"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.node = node;
			this.index = Clamp(node, index);
		}

		private static int Clamp(MenuNode n, int i) { int c = Count(n); return ((i % c) + c) % c; }

		/// <summary>Textliste als Ersatz, falls keine Bilder angezeigt werden können.</summary>
		private static string Listing(MenuNode n, int sel)
		{
			var lines = new List<string>();
			for (int i = Mathf.Max(0, sel - 1); i < Mathf.Min(Count(n), sel + 2); i++)
				lines.Add((i == sel ? "> " : "  ") + (Item(n, i)?.label ?? Tr.T("« zurück", "« back")));
			return string.Join("\n", lines);
		}

		public override void OnEnter(CommsRadioUtility utility, AStateBehaviour? previous)
		{
			PlacerSession.ClearEdit();
			PlacerSession.ShowHighlight(null);
			PlacerSession.HideCursor();
			RadioIcon.Show(Item(node, index)?.icon ?? "back");
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up) return new MenuState(node, index - 1);
			if (action == InputAction.Down) return new MenuState(node, index + 1);

			var item = Item(node, index);
			if (item == null)
			{
				utility.PlaySound(VanillaSoundCommsRadio.Cancel);
				if (node.parent == null) return new EditorRootState();
				return new MenuState(node.parent, node.parent.children!.IndexOf(node) + 1);
			}
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			if (item.children != null) return new MenuState(item, 1);
			PlacerMenu.Remember(node, index);
			return item.start!();
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not MenuState and not EditorRootState) RadioIcon.Hide();
		}
	}

	// ======================================================================
	// Gleise verlegen: Alter wählen (neu/mittel/alt) für die gewählte Bauart
	// ======================================================================

	internal sealed class TrackAgeState : AStateBehaviour
	{
		private readonly TrackStyle style;
		private readonly bool cover;
		private readonly int sel;

		private static List<TrackTypeOption> Options(TrackStyle style, bool cover) =>
			TrackTypes.All.Where(o => o.cover == cover && (cover || o.style == style)).ToList();

		public TrackAgeState(TrackStyle style, bool cover, int sel) : base(new CommsRadioState(
			titleText: cover ? Tr.T("Asphalt-Gleis", "Asphalt track") : Tr.T("Gleis: Alter", "Track: age"),
			contentText: Describe(style, cover, sel),
			actionText: Tr.Act("WEITER"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.style = style; this.cover = cover; this.sel = sel;
		}

		private static string Describe(TrackStyle style, bool cover, int sel)
		{
			var opts = Options(style, cover);
			if (opts.Count == 0) return Tr.T("Keine Gleistypen gefunden", "No track types found");
			var o = opts[((sel % opts.Count) + opts.Count) % opts.Count];
			return Tr.T($"{o.Label}\n{o.Detail}\n({((sel % opts.Count) + opts.Count) % opts.Count + 1}/{opts.Count}) Mausrad", $"{o.Label}\n{o.Detail}\n({((sel % opts.Count) + opts.Count) % opts.Count + 1}/{opts.Count}) wheel");
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			var opts = Options(style, cover);
			if (opts.Count == 0) return PlacerMenu.Return();
			if (action == InputAction.Up) return new TrackAgeState(style, cover, sel - 1);
			if (action == InputAction.Down) return new TrackAgeState(style, cover, sel + 1);
			var o = opts[((sel % opts.Count) + opts.Count) % opts.Count];
			TrackTypes.Selected = TrackTypes.All.ToList().IndexOf(o);
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new PickPointState(null, null, 0f);
		}
	}

	// ======================================================================
	// Gleise ändern: eigenes oder normales Originalgleis anzielen → Typ ändern / freischneiden
	// ======================================================================

	internal sealed class TrackChangePickState : AStateBehaviour
	{
		private readonly TrackRef? target;

		private static string Content(TrackRef? target)
		{
			if (target == null) return Tr.T("Laser auf eigenes oder\nOriginalgleis\nMausrad = Menü", "Aim at an own or\noriginal track\nWheel = menu");
			float length = target.own != null ? target.own.length : target.vanilla != null ? VanillaTrackEditor.Length(target.vanilla) : 0f;
			string kind = target.own != null ? Tr.T("Eigenes Gleis", "Own track") : Tr.T("Originalgleis", "Original track");
			if (target.vanilla != null && !VanillaTrackEditor.CanEdit(target.vanilla, out string why)) return Tr.T($"{kind} {Fmt.M(length, 0)} m\nNicht änderbar: {why}", $"{kind} {Fmt.M(length, 0)} m\nCannot be changed: {why}");
			return Tr.T($"{kind} {Fmt.M(length, 0)} m\nKlick = bearbeiten", $"{kind} {Fmt.M(length, 0)} m\nClick = edit");
		}

		public TrackChangePickState(TrackRef? target) : base(new CommsRadioState(
			titleText: Tr.T("Gleis ändern", "Change track"),
			contentText: Content(target),
			actionText: Tr.Act(target == null ? "ZIELEN" : "BEARBEITEN"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.target = target;
		}

		public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
		{
			PlacerSession.UpdateHit(utility.SignalOrigin, allowSnap: false);
			Vector3? pos = PlacerSession.GroundHit ?? PlacerSession.Hit;
			var t = pos.HasValue ? PlacerSession.FindTrackRef(pos.Value) : null;
			if (t != null && t.Same(target) || t == null && target == null) return this;
			PlacerSession.HighlightTrackRef(t);
			return new TrackChangePickState(t);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return PlacerMenu.Return();
			if (target == null) { utility.PlaySound(VanillaSoundCommsRadio.Cancel); return PlacerMenu.Return(); }
			if (target.vanilla != null && !VanillaTrackEditor.CanEdit(target.vanilla, out _))
			{
				utility.PlaySound(VanillaSoundCommsRadio.Warning);
				return this;
			}
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			return new TrackEditState(target, 0);
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not TrackChangePickState and not TrackEditState and not TrackChangeState) PlacerSession.ShowHighlight(null);
			PlacerSession.HideCursor();
		}
	}

	internal sealed class TrackEditState : AStateBehaviour
	{
		private readonly TrackRef target;
		private readonly int sel;

		private static bool Cleared(TrackRef r) => r.own != null ? r.own.clearVegetation : VanillaTrackEditor.Find(r.vanilla)?.clearVegetation == true;

		private static string Action(TrackRef r, int sel) => sel switch
		{
			0 => "GLEISTYP",
			1 => Cleared(r) ? "BEWUCHS ZULASSEN" : "FREISCHNEIDEN",
			_ => "FERTIG",
		};

		private static string Content(TrackRef r)
		{
			float length = r.own != null ? r.own.length : r.vanilla != null ? VanillaTrackEditor.Length(r.vanilla) : 0f;
			string kind = r.own != null ? Tr.T("Eigenes", "Own") : Tr.T("Original", "Original");
			return Tr.T($"{kind} · {Fmt.M(length, 0)} m\nBewuchs: {(Cleared(r) ? "freigeschnitten (7 m)" : "bleibt")}\nMausrad = Auswahl", $"{kind} · {Fmt.M(length, 0)} m\nVegetation: {(Cleared(r) ? "cleared (7 m)" : "stays")}\nWheel = select");
		}

		public TrackEditState(TrackRef target, int sel) : base(new CommsRadioState(
			titleText: Tr.T("Gleis ändern", "Change track"),
			contentText: Content(target),
			actionText: Tr.Act(Action(target, sel)),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.target = target; this.sel = sel;
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up) return new TrackEditState(target, (sel + 2) % 3);
			if (action == InputAction.Down) return new TrackEditState(target, (sel + 1) % 3);
			if (sel == 0)
			{
				var all = TrackTypes.All.ToList();
				TrackTypeOption? cur;
				if (target.own != null)
					cur = string.IsNullOrEmpty(target.own.overlay) ? TrackTypes.Resolve(target.own.railType, target.own.baseType, target.own.style, target.own.age)
						: all.Where(o => o.cover).OrderBy(o => Mathf.Abs(o.age - target.own.age)).FirstOrDefault();
				else cur = target.vanilla != null ? VanillaTrackEditor.CurrentOption(target.vanilla) : null;
				utility.PlaySound(VanillaSoundCommsRadio.Confirm);
				return new TrackChangeState(target, Mathf.Max(0, cur != null ? all.IndexOf(cur) : 0));
			}
			if (sel == 1)
			{
				bool ok;
				if (target.own != null)
				{
					target.own.clearVegetation = !target.own.clearVegetation;
					ok = VegetationBuilder.RebuildTrack(target.own);
					if (!ok && target.own.clearVegetation) target.own.clearVegetation = false;
				}
				else if (target.vanilla != null)
				{
					var e = VanillaTrackEditor.GetOrCreate(target.vanilla);
					e.clearVegetation = !e.clearVegetation;
					ok = VegetationBuilder.RebuildVanilla(e, target.vanilla);
					if (!ok && e.clearVegetation) e.clearVegetation = false;
				}
				else ok = false;
				LayoutStore.SaveQuiet();
				utility.PlaySound(ok ? VanillaSoundCommsRadio.Confirm : VanillaSoundCommsRadio.Warning);
				return new TrackEditState(target, 1);
			}
			LayoutStore.SaveQuiet();
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			PlacerSession.ShowHighlight(null);
			return new TrackChangePickState(null);
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not TrackEditState and not TrackChangeState and not TrackChangePickState) PlacerSession.ShowHighlight(null);
		}
	}

	internal sealed class TrackChangeState : AStateBehaviour
	{
		private readonly TrackRef target;
		private readonly int sel;

		public TrackChangeState(TrackRef target, int sel) : base(new CommsRadioState(
			titleText: Tr.T("Neuer Gleistyp", "New track type"),
			contentText: Describe(sel),
			actionText: Tr.Act("ÜBERNEHMEN"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.target = target; this.sel = sel;
		}

		private static string Describe(int sel)
		{
			var all = TrackTypes.All;
			if (all.Count == 0) return Tr.T("Keine Gleistypen", "No track types");
			var o = all[((sel % all.Count) + all.Count) % all.Count];
			return Tr.T($"{o.Label}\n{o.Detail}\nMausrad = Typ", $"{o.Label}\n{o.Detail}\nWheel = type");
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			var all = TrackTypes.All;
			if (all.Count == 0) return PlacerMenu.Return();
			if (action == InputAction.Up) return new TrackChangeState(target, sel - 1);
			if (action == InputAction.Down) return new TrackChangeState(target, sel + 1);

			var o = all[((sel % all.Count) + all.Count) % all.Count];
			RailTrack? rail = target.Rail;
			if (rail != null && TrackBuilder.HasVehicles(rail))
			{
				utility.PlaySound(VanillaSoundCommsRadio.Warning);
				return new TrackEditState(target, 0);
			}

			bool ok;
			string error;
			if (target.own != null)
			{
				var t = target.own;
				// Lademaschinen auf diesem Gleis merken (Entfernen löst sie sonst vom Gleis)
				var machines = LayoutStore.Layout.machines.Where(m => m.trackId == t.id).ToList();
				TrackBuilder.Remove(t.id, out _);
				t.style = o.StyleId;
				t.age = o.age;
				t.railType = o.RailName;
				t.baseType = o.BaseName;
				t.overlay = o.cover ? "crossing" : "";
				ok = TrackBuilder.Build(t, out error);
				if (ok && !string.IsNullOrEmpty(t.yardId)) StationLink.Apply(t, out _);
				foreach (var m in machines) { m.trackId = t.id; MachineBuilder.Remove(m.id); MachineBuilder.Build(m, out _); }
			}
			else if (target.vanilla != null)
			{
				var e = VanillaTrackEditor.GetOrCreate(target.vanilla);
				VanillaTrackEditor.SetType(e, o);
				ok = VanillaTrackEditor.Apply(e, target.vanilla, out error);
			}
			else { ok = false; error = Tr.T("Gleis fehlt", "Track missing"); }
			LayoutStore.SaveQuiet();
			PlacerSession.RebuildSaved();
			utility.PlaySound(ok ? VanillaSoundCommsRadio.Confirm : VanillaSoundCommsRadio.Warning);
			return ok ? new TrackEditState(target, 0) : (AStateBehaviour)new MenuState(PlacerMenu.Root, 1, Tr.T("Fehlgeschlagen:\n", "Failed:\n") + ConfirmState.Trim(error));
		}
	}

	// ======================================================================
	// Weichen ändern: eigene Weiche anzielen → Hebelseite wechseln / Alter wählen → neu bauen
	// ======================================================================

	internal sealed class SwitchChangePickState : AStateBehaviour
	{
		private readonly PlacedSwitch? target;
		private readonly VanillaSwitchEditor.Info? vanilla;

		private static string Text(PlacedSwitch? target, VanillaSwitchEditor.Info? vanilla)
		{
			if (target != null) return Tr.T($"{JunctionBuilder.Variants.FirstOrDefault(v => v.prefabName == target.prefab)?.Label}\nKlick = ändern", $"{JunctionBuilder.Variants.FirstOrDefault(v => v.prefabName == target.prefab)?.Label}\nClick = change");
			if (vanilla != null) return Tr.T($"Spielweiche {(vanilla.divergesLeft ? "links" : "rechts")}\nKlick = ändern", $"Game switch {(vanilla.divergesLeft ? "left" : "right")}\nClick = change");
			return Tr.T("Laser auf eine Weiche\n(eigene oder Spielweiche)\nMausrad = Menü", "Aim at a switch\n(own or game switch)\nWheel = menu");
		}

		public SwitchChangePickState(PlacedSwitch? target, VanillaSwitchEditor.Info? vanilla = null) : base(new CommsRadioState(
			titleText: Tr.T("Weiche ändern", "Change switch"),
			contentText: Text(target, vanilla),
			actionText: Tr.Act(target == null && vanilla == null ? "ZIELEN" : "ÄNDERN"),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.target = target; this.vanilla = vanilla;
		}

		public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
		{
			PlacerSession.UpdateHit(utility.SignalOrigin, allowSnap: false);
			var r = PlacerSession.Hit.HasValue ? PlacerSession.FindOwnNear(PlacerSession.Hit.Value, 3f, allowVanilla: false) : null;
			var s = r?.kind == PlacerSession.TargetKind.Switch ? LayoutStore.Layout.switches.Find(x => x.id == r.id) : null;
			VanillaSwitchEditor.Info? w = null;
			if (s == null && PlacerSession.Hit.HasValue)
				w = VanillaSwitchEditor.Of(VanillaRemover.FindNear(PlacerSession.Hit.Value, 3f));
			if (s?.id == target?.id && w?.junction == vanilla?.junction) return this;
			if (s != null) PlacerSession.ShowHighlight(r);
			else if (w != null) PlacerSession.HighlightTrackRef(new TrackRef { vanilla = w.through });
			else PlacerSession.ShowHighlight(null);
			return new SwitchChangePickState(s, w);
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action != InputAction.Activate) return PlacerMenu.Return();
			if (target == null && vanilla == null) { utility.PlaySound(VanillaSoundCommsRadio.Cancel); return PlacerMenu.Return(); }
			utility.PlaySound(VanillaSoundCommsRadio.Confirm);
			if (target != null) return new SwitchChangeState(target, 0);
			return new VanillaSwitchChangeState(vanilla!, 0);
		}

		public override void OnLeave(CommsRadioUtility utility, AStateBehaviour? next)
		{
			if (next is not SwitchChangePickState and not SwitchChangeState and not VanillaSwitchChangeState) PlacerSession.ShowHighlight(null);
			PlacerSession.HideCursor();
		}
	}

	/// <summary>Spielweiche: Alter und Abdeckung (Hebelseite nur bei eigenen Weichen).</summary>
	internal sealed class VanillaSwitchChangeState : AStateBehaviour
	{
		private static readonly string[] Options = { "ALTER", "ABDECKUNG", "FERTIG" };
		private readonly VanillaSwitchEditor.Info w;
		private readonly int sel;

		private static List<TrackTypeOption> Ages => TrackTypes.All.Where(o => !o.cover && o.style == TrackStyle.Ballast).ToList();

		private static string AgeLabel(VanillaSwitchEdit? e)
		{
			if (e == null || e.age < 0f) return Tr.T("Original", "Original");
			return Ages.OrderBy(o => Mathf.Abs(o.age - e.age)).FirstOrDefault()?.ageLabel ?? "?";
		}

		public VanillaSwitchChangeState(VanillaSwitchEditor.Info w, int sel, string? message = null) : base(new CommsRadioState(
			titleText: Tr.T("Spielweiche ändern", "Change game switch"),
			contentText: (message != null ? message + "\n" : "") +
				Tr.T($"Alter: {AgeLabel(VanillaSwitchEditor.Find(w))} · Abdeckung: {(VanillaSwitchEditor.Find(w)?.covered == true ? "ja" : "nein")}\nHebelseite: nur eigene Weichen\nMausrad = Auswahl", $"Age: {AgeLabel(VanillaSwitchEditor.Find(w))} · Cover: {(VanillaSwitchEditor.Find(w)?.covered == true ? "yes" : "no")}\nLever side: own switches only\nWheel = select"),
			actionText: Tr.Act(Options[sel]),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.w = w; this.sel = sel;
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up) return new VanillaSwitchChangeState(w, (sel + Options.Length - 1) % Options.Length);
			if (action == InputAction.Down) return new VanillaSwitchChangeState(w, (sel + 1) % Options.Length);
			if (sel == 2)
			{
				PlacerSession.ShowHighlight(null);
				return new SwitchChangePickState(null);
			}
			var e = VanillaSwitchEditor.GetOrCreate(w);
			if (sel == 0)
			{
				// Original → neu → mittel → alt → Original
				var ages = Ages;
				int i = e.age < 0f ? -1 : ages.FindIndex(o => Mathf.Abs(o.age - e.age) < 0.5f);
				e.age = i + 1 < ages.Count ? ages[i + 1].age : -1f;
				e.ageApplied = true; // auch „Original“ muss neu aufgebaut werden, wenn vorher geändert
			}
			else e.covered = !e.covered;
			bool ok = VanillaSwitchEditor.Apply(e, w, out string err);
			VanillaSwitchEditor.CleanUp(e);
			LayoutStore.SaveQuiet();
			utility.PlaySound(ok ? VanillaSoundCommsRadio.Confirm : VanillaSoundCommsRadio.Warning);
			return new VanillaSwitchChangeState(w, sel, ok ? null : Tr.T("Fehler: ", "Error: ") + ConfirmState.Trim(err));
		}
	}

	internal sealed class SwitchChangeState : AStateBehaviour
	{
		private static readonly string[] Options = { "HEBEL WECHSELN", "ALTER", "ABDECKUNG", "FERTIG" };
		private readonly PlacedSwitch s;
		private readonly int sel;

		/// <summary>Alters-Stufen (neu/mittel/alt) aus den Gleistypen.</summary>
		private static List<TrackTypeOption> Ages => TrackTypes.All.Where(o => !o.cover && o.style == TrackStyle.Ballast).ToList();

		private static string AgeLabel(PlacedSwitch s)
		{
			if (s.age < 0f) return Tr.T("wie Vorlage", "as template");
			var a = Ages.OrderBy(o => Mathf.Abs(o.age - s.age)).FirstOrDefault();
			return a?.ageLabel ?? "?";
		}

		public SwitchChangeState(PlacedSwitch s, int sel, string? message = null) : base(new CommsRadioState(
			titleText: Tr.T("Weiche ändern", "Change switch"),
			contentText: (message != null ? message + "\n" : "") +
				Tr.T($"{JunctionBuilder.Variants.FirstOrDefault(v => v.prefabName == s.prefab)?.Label}\nAlter: {AgeLabel(s)} · Abdeckung: {(s.covered ? "ja" : "nein")}\nMausrad = Auswahl", $"{JunctionBuilder.Variants.FirstOrDefault(v => v.prefabName == s.prefab)?.Label}\nAge: {AgeLabel(s)} · Cover: {(s.covered ? "yes" : "no")}\nWheel = select"),
			actionText: Tr.Act(Options[sel]),
			buttonBehaviour: ButtonBehaviourType.Override))
		{
			this.s = s; this.sel = sel;
		}

		public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
		{
			if (action == InputAction.Up) return new SwitchChangeState(s, (sel + Options.Length - 1) % Options.Length);
			if (action == InputAction.Down) return new SwitchChangeState(s, (sel + 1) % Options.Length);

			if (sel == 3)
			{
				PlacerSession.ShowHighlight(null);
				return new SwitchChangePickState(null);
			}
			if (sel == 2)
			{
				// Abdeckung an/aus: nur die Abdeckung neu bauen, die Weiche bleibt stehen
				s.covered = !s.covered;
				var variant = JunctionBuilder.Variants.FirstOrDefault(v => v.prefabName == s.prefab);
				if (JunctionBuilder.Built.TryGetValue(s.id, out var bs) && bs != null && variant != null) SwitchCoverBuilder.Build(s, variant, bs.through);
				LayoutStore.SaveQuiet();
				utility.PlaySound(VanillaSoundCommsRadio.Confirm);
				return new SwitchChangeState(s, sel);
			}

			var cur = JunctionBuilder.Variants.FirstOrDefault(v => v.prefabName == s.prefab);
			string oldPrefab = s.prefab;
			float oldAge = s.age;
			if (sel == 0)
			{
				var other = cur == null ? null : JunctionBuilder.Variants.FirstOrDefault(v => v.divergesLeft == cur.divergesLeft && v.leverLeft != cur.leverLeft);
				if (other == null) { utility.PlaySound(VanillaSoundCommsRadio.Warning); return new SwitchChangeState(s, sel, Tr.T("Keine Variante mit\nanderer Hebelseite", "No variant with\nthe other lever side")); }
				s.prefab = other.prefabName;
			}
			else
			{
				// Alter weiterschalten: wie Vorlage → neu → mittel → alt → wie Vorlage
				var ages = Ages;
				int i = s.age < 0f ? -1 : ages.FindIndex(o => Mathf.Abs(o.age - s.age) < 0.5f);
				s.age = i + 1 < ages.Count ? ages[i + 1].age : -1f;
			}

			if (!JunctionBuilder.Remove(s.id, out string err))
			{
				s.prefab = oldPrefab; s.age = oldAge;
				utility.PlaySound(VanillaSoundCommsRadio.Warning);
				return new SwitchChangeState(s, sel, ConfirmState.Trim(err));
			}
			bool ok = JunctionBuilder.Build(s, out err);
			LayoutStore.SaveQuiet();
			PlacerSession.RebuildSaved();
			utility.PlaySound(ok ? VanillaSoundCommsRadio.Confirm : VanillaSoundCommsRadio.Warning);
			return new SwitchChangeState(s, sel, ok ? null : Tr.T("Fehler: ", "Error: ") + ConfirmState.Trim(err));
		}
	}
}
