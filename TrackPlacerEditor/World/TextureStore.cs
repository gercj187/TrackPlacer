using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace TrackPlacer.World
{
	/// <summary>
	/// Eigene Texturen: zuerst Mods/TrackPlacer/textures/&lt;name&gt;.png|.jpg (zum Austauschen), sonst aus der DLL.
	/// Einmal geladen, dann zwischengespeichert.
	/// </summary>
	internal static class TextureStore
	{
		private static readonly Dictionary<string, Texture2D?> s_cache = new Dictionary<string, Texture2D?>();

		/// <param name="linear">true für Datenkarten (Normal, Metallic/Glanz) – sonst würde Unity sie als Farbe (sRGB) umrechnen.</param>
		public static Texture2D? Get(string name, bool linear = false)
		{
			if (s_cache.TryGetValue(name, out var cached)) return cached;
			Texture2D? tex = null;
			try
			{
				byte[]? bytes = null;
				foreach (var ext in new[] { ".png", ".jpg" })
				{
					string file = Path.Combine(Main.Mod.Path, "textures", name + ext);
					if (File.Exists(file)) { bytes = File.ReadAllBytes(file); break; }
				}
				if (bytes == null)
					foreach (var ext in new[] { ".png", ".jpg" })
					{
						using var st = typeof(TextureStore).Assembly.GetManifestResourceStream("TrackPlacer.Textures." + name + ext);
						if (st == null) continue;
						using var ms = new MemoryStream();
						st.CopyTo(ms);
						bytes = ms.ToArray();
						break;
					}
				if (bytes != null)
				{
					tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = "TP_" + name, wrapMode = TextureWrapMode.Repeat, anisoLevel = 4 };
					if (!ImageConversion.LoadImage(tex, bytes)) tex = null;
				}
				if (tex == null) Main.Log($"Textur {name} nicht gefunden.");
			}
			catch (Exception e) { Main.Log($"Textur {name}: {e.Message}"); tex = null; }
			s_cache[name] = tex;
			return tex;
		}
	}
}
