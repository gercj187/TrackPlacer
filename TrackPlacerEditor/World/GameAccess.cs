using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace TrackPlacer.World
{
	/// <summary>
	/// Alle Zugriffe auf private Felder des Spiels an einer Stelle, einmal aufgelöst und gecacht.
	/// Kein Harmony, keine AccessTools-Aufrufe in Schleifen.
	/// Namen per Dekompilierung von Assembly-CSharp / DV.RailTrack (Build vom Sept. 2025) geprüft.
	/// </summary>
	internal static class GameAccess
	{
		private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

		// RailTrackRegistryBase
		public static readonly FieldInfo? RegAllTracks = typeof(RailTrackRegistryBase).GetField("_allTracks", Inst);
		public static readonly FieldInfo? RegOrderedTracks = typeof(RailTrackRegistryBase).GetField("_orderedRailtracks", Inst);

		// RailwayMeshGenerator
		public static readonly FieldInfo? MeshSpatialHash = typeof(RailwayMeshGenerator).GetField("spatialHash", Inst);
		public static readonly FieldInfo? MeshActiveChunks = typeof(RailwayMeshGenerator).GetField("activeChunks", Inst);
		public static readonly FieldInfo? MeshPrevCell = typeof(RailwayMeshGenerator).GetField("prevCellId", Inst);
		/// <summary>Erzeugt Schotter/Schienen-Mesh für genau einen Chunk (optional, sonst Zellen-Neuaufbau).</summary>
		public static readonly MethodInfo? MeshScheduleChunk = typeof(RailwayMeshGenerator).GetMethod("ScheduleGenerateBaseAndRail", Inst);

		// TrackChunkSpatialHash
		public static readonly FieldInfo? HashDoneAdding = typeof(TrackChunkSpatialHash).GetField("doneAdding", Inst);
		public static readonly FieldInfo? HashLookupCells = typeof(TrackChunkSpatialHash).GetField("lookupCells", Inst);

		private static bool s_reported;

		public static bool Ok(out string missing)
		{
			var list = new List<string>();
			if (RegAllTracks == null) list.Add("RailTrackRegistryBase._allTracks");
			if (RegOrderedTracks == null) list.Add("RailTrackRegistryBase._orderedRailtracks");
			if (MeshSpatialHash == null) list.Add("RailwayMeshGenerator.spatialHash");
			if (MeshActiveChunks == null) list.Add("RailwayMeshGenerator.activeChunks");
			if (MeshPrevCell == null) list.Add("RailwayMeshGenerator.prevCellId");
			if (HashDoneAdding == null) list.Add("TrackChunkSpatialHash.doneAdding");
			if (HashLookupCells == null) list.Add("TrackChunkSpatialHash.lookupCells");
			missing = string.Join(", ", list);
			if (list.Count > 0 && !s_reported)
			{
				s_reported = true;
				Main.Log("Spielinterne Felder nicht gefunden (Spiel-Update?): " + missing);
			}
			return list.Count == 0;
		}

		private static RailwayMeshGenerator? s_meshGen;
		public static RailwayMeshGenerator? MeshGenerator
		{
			get
			{
				if (s_meshGen == null) s_meshGen = UnityEngine.Object.FindObjectOfType<RailwayMeshGenerator>();
				return s_meshGen;
			}
		}

		// SingletonBehaviour.Instance kann ein Objekt automatisch anlegen. Darum vorher per Suche prüfen,
		// ob die Registry der Spielwelt wirklich existiert.
		private static RailTrackRegistryBase? s_registry;
		public static RailTrackRegistryBase? Registry
		{
			get
			{
				if (s_registry == null) s_registry = UnityEngine.Object.FindObjectOfType<RailTrackRegistryBase>();
				return s_registry;
			}
		}

		/// <summary>True, sobald die Gleis-Registry der Welt gefüllt ist (reicht zum Bauen; Meshes kommen ggf. später).</summary>
		public static bool TracksReady
		{
			get
			{
				var reg = Registry;
				// Nicht OrderedRailtracks abfragen: der Getter cacht beim ersten Aufruf, zu früh wäre die Liste unvollständig.
				// _allTracks füllt das Spiel selbst, sobald die Logik-Gleise angelegt sind.
				if (reg == null || RegAllTracks == null) return false;
				return RegAllTracks.GetValue(reg) is RailTrack[] all && all.Length > 0;
			}
		}

		/// <summary>True, sobald der Mesh-Generator gestartet ist (Start hat alle aktiven Gleise eingetragen).</summary>
		public static bool MeshReady
		{
			get
			{
				var gen = MeshGenerator;
				return gen != null && MeshSpatialHash != null && MeshSpatialHash.GetValue(gen) != null;
			}
		}

		/// <summary>True, sobald Registry und Mesh-Generator fertig initialisiert sind.</summary>
		public static bool WorldReady
		{
			get
			{
				if (!TracksReady) return false;
				var gen = MeshGenerator;
				if (gen == null || MeshSpatialHash == null) return false;
				return MeshSpatialHash.GetValue(gen) != null;
			}
		}

		public static Vector3 OriginShift => DV.OriginShift.OriginShift.currentMove;
	}
}
