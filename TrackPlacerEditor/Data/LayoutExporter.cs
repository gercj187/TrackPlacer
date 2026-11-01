using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace TrackPlacer.Data
{
	/// <summary>Erzeugt aus dem aktuellen Editorstand eine installierbare, funkgerätfreie UMM-Karten-Mod.</summary>
	internal static class LayoutExporter
	{
		/// <summary>
		/// Szenen-Mod (nur Daten) schreiben: Info.json ohne DLL (UMM erlaubt Mods ohne AssemblyName/EntryMethod) mit
		/// Abhängigkeit „TrackPlacerPlay“, dazu layout.json und map_manifest.json. Track Placer Play findet alle
		/// installierten Szenen über map_manifest.json und lädt sie gemeinsam – auch von verschiedenen Autoren.
		/// </summary>
		public static bool Export(string displayName, string author, string editorModPath, out string zipPath, out string error)
		{
			zipPath = "";
			error = "";
			displayName = (displayName ?? "").Trim();
			author = string.IsNullOrWhiteSpace(author) ? "TrackPlacerExporter" : author.Trim();
			if (displayName.Length == 0) { error = Tr.T("Bitte zuerst einen Namen eingeben.", "Please enter a name first."); return false; }

			string fileName = SafeFileName(displayName);
			if (fileName.Length == 0) { error = Tr.T("Der Name enthält keine gültigen Zeichen.", "The name contains no valid characters."); return false; }

			try
			{
				string exports = Path.Combine(editorModPath, "Exports");
				Directory.CreateDirectory(exports);
				zipPath = Path.Combine(exports, fileName + ".zip");
				string tmp = zipPath + ".tmp";
				if (File.Exists(tmp)) File.Delete(tmp);

				string folder = fileName;
				string id = "TrackPlacerScene_" + SafeId(author + "_" + displayName);
				var info = new
				{
					Id = id,
					DisplayName = displayName,
					Author = author,
					Version = "1.0.0",
					ManagerVersion = "0.27.3",
					Requirements = new[] { PlayId },
				};
				var manifest = new
				{
					format = "TrackPlacerMap",
					version = 2,
					name = displayName,
					author,
					exportedUtc = DateTime.UtcNow.ToString("o"),
					sourceLayoutVersion = LayoutStore.Layout.version,
					requires = PlayId,
				};

				using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
				using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
				{
					AddText(zip, folder + "/Info.json", JsonConvert.SerializeObject(info, Formatting.Indented));
					AddText(zip, folder + "/layout.json", JsonConvert.SerializeObject(LayoutStore.Layout, Formatting.Indented));
					AddText(zip, folder + "/map_manifest.json", JsonConvert.SerializeObject(manifest, Formatting.Indented));
					AddText(zip, folder + "/README.txt",
						$"{displayName}\r\nvon {author}\r\n\r\nTrack-Placer-Szene (nur Daten).\r\n"
						+ "Benötigt die Mod „Track Placer Play“ (TrackPlacerPlay).\r\n"
						+ "Installation: ZIP mit dem Unity Mod Manager installieren.\r\n");
				}

				if (File.Exists(zipPath)) File.Delete(zipPath);
				File.Move(tmp, zipPath);
				return true;
			}
			catch (Exception e)
			{
				error = e.Message;
				return false;
			}
		}

		public const string PlayId = "TrackPlacerPlay";

		private static void AddText(ZipArchive zip, string name, string text)
		{
			var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
			using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
			writer.Write(text);
		}

		private static string SafeFileName(string value)
		{
			var invalid = Path.GetInvalidFileNameChars();
			string result = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
			return result.Length > 80 ? result.Substring(0, 80).Trim() : result;
		}

		private static string SafeId(string value)
		{
			string id = new string(value.Where(c => c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9').ToArray());
			if (id.Length == 0) id = "Map";
			if (id.Length > 40) id = id.Substring(0, 40);
			uint hash = 2166136261;
			foreach (char c in value) { hash ^= c; hash *= 16777619; }
			return id + "_" + hash.ToString("x8");
		}
	}
}
