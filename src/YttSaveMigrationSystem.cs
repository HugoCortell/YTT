using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace YangTransport;

/// Updates save files from older versions to current ones.
// Runs entirely during load time so the logic is >entirely< self contained here and never adds any cost or complexity to the rest of the mod.
// Works linearly, so if we are in version 1 and want to reach 4, we need to loop via 1 -> 2 -> 3 -> 4. Keeps things simple.
internal sealed class YttSaveMigrationSystem : ModSystem
{
	private const string RevisionSaveKey			= "yangtransport_saverevision";
	private const string RailStateSaveKey			= "yangtransport_railstate";
	private const string ConvoyAuthoritySaveKey		= "yangtransport_convoyauthority";
	private const string OffscreenStateSaveKey		= "yangtransport_offscreenstate";
	private const string StationStateSaveKey		= "yangtransport_stations";

	private const int CurrentSaveRevision			= 2;

	private const int RouteStartSentinel			= 0x53545259; // YRTS
	private const int RouteEndSentinel				= 0x45545259; // YRTE

	private const int MaxVirtualConvoys				= 1_000_000;
	private const int MaxVehiclesPerConvoy			= 4096;
	private const int MaxRouteEntries				= 256;
	private const int MaxRouteRuns					= 8192;
	private const int MaxEntityBytes				= 64 * 1024 * 1024;

	private const int FrozenPoseBytes	= sizeof(double) * 3 + sizeof(int) + sizeof(float) * 2;
	private const int RouteRunBytes		= sizeof(ulong) + sizeof(double) * 4 + sizeof(sbyte);

	public override double ExecuteOrder() => 0.01;
	public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

	public override void StartPre(ICoreAPI api)
	{
		if (api is not ICoreServerAPI serverAPI) return;

		try { Migrate(serverAPI); }
		catch (Exception exception)
		{
			serverAPI.Logger.Error("[yangtransport] Save migration failed before YangTransport initialization.");
			serverAPI.Logger.Error(exception); if (exception is FormatException) throw;
			throw new FormatException("YangTransport could not migrate this world's saved data.", exception);
		}
	}

	private static void Migrate(ICoreServerAPI serverAPI)
	{
		ISaveGame save = serverAPI.WorldManager.SaveGame;
		byte[]? revisionData = save.GetData(RevisionSaveKey);

		if (revisionData == null)
		{
			if (!HasPersistedYTTState(save))
			{
				MigrateR0ToR1Configs(serverAPI);
				WriteRevision(save, CurrentSaveRevision);
				return;
			}
		}

		int revision = revisionData == null ? 0 : ReadRevision(revisionData);
		if (revision > CurrentSaveRevision) throw new FormatException($"YangTransport save revision R{revision} is newer than this mod supports (R{CurrentSaveRevision}).");

		if (revision == CurrentSaveRevision) return;

		int startingRevision = revision;
		if (BackupWhenUpdatingSaves(serverAPI))
		{
			if (serverAPI.Server.Config.HostedMode)
			{
				serverAPI.Logger.Warning
				(
					"[yangtransport] Your server host has disabled the ability to create backups. " +
					"The save file will be migrated to the new version without first creating a backup." +
					"Hope you made your own manual backup before updating the mod and re-starting the server, best of luck!"
				);
			}
			else
			{
				try { BackupSave(serverAPI, startingRevision, CurrentSaveRevision); }
				catch (Exception exception)
				{
					serverAPI.Logger.Error("[yangtransport] Pre-migration backup failed! Continuing without a backup...");
					serverAPI.Logger.Error(exception);
				}
			}
		}
		else serverAPI.Logger.Notification("[yangtransport] Pre-migration save backup skipped by configuration.");

		byte[]? originalOffscreenState = save.GetData(OffscreenStateSaveKey);
		byte[]? offscreenState = originalOffscreenState;

		while (revision < CurrentSaveRevision)
		{
			switch (revision)
			{
				case 0:
					offscreenState = UpgradeR0ToR1(offscreenState);
					MigrateR0ToR1Configs(serverAPI);
				break;
				case 1:
					offscreenState = UpgradeR1ToR2(offscreenState, save.GetData(ConvoyAuthoritySaveKey), serverAPI.Logger);
				break;

				default: throw new InvalidDataException($"No YangTransport migration exists for save revision R{revision}.");
			}

			revision++;
		}

		if (!ReferenceEquals(offscreenState, originalOffscreenState) && offscreenState != null) save.StoreData(OffscreenStateSaveKey, offscreenState);

		WriteRevision(save, revision);
		serverAPI.Logger.Notification("[yangtransport] Save migrated from R{0} to R{1}.", startingRevision, revision);
	}

	private static bool HasPersistedYTTState(ISaveGame save)
	{
		return	HasData(save.GetData(RailStateSaveKey))			|| HasData(save.GetData(ConvoyAuthoritySaveKey))	||
				HasData(save.GetData(OffscreenStateSaveKey))	|| HasData(save.GetData(StationStateSaveKey));
	}

	private static bool HasData(byte[]? data) => data != null && data.Length > 0;

	private static int ReadRevision(byte[] data)
	{
		if (data.Length != sizeof(int)) throw new FormatException("Invalid YangTransport save revision payload.");

		int revision = BitConverter.ToInt32(data, 0);
		if (revision < 0) throw new FormatException($"Invalid YangTransport save revision R{revision}.");
		return revision;
	}

	private static void WriteRevision(ISaveGame save, int revision) { save.StoreData(RevisionSaveKey, BitConverter.GetBytes(revision)); }

	private static bool BackupWhenUpdatingSaves(ICoreServerAPI serverAPI)
	{
		try
		{
			YangTransportServerConfig? configuration = serverAPI.LoadModConfig<YangTransportServerConfig>(YangTransportSettings.ConfigurationFilename);
			if (configuration != null) return configuration.BackupWhenUpdatingSaves;

			configuration = serverAPI.LoadModConfig<YangTransportServerConfig>("yangtransport.json");
			return configuration?.BackupWhenUpdatingSaves ?? true;
		}
		catch (Exception exception)
		{
			serverAPI.Logger.Warning("[yangtransport] Could not read BackupWhenUpdatingSaves. Assuming true and creating a safety backup.");
			serverAPI.Logger.Warning(exception);
			return true;
		}
	}

	private static void BackupSave(ICoreServerAPI serverAPI, int fromRevision, int toRevision)
	{
		// Call Vintage Story's own SQLite backup routine.
		string backupFilename = $"yangtransport-R{fromRevision}-to-R{toRevision}-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-{Guid.NewGuid():N}.vcdbs";

		object server = serverAPI.World;
		FieldInfo chunkThreadField = server.GetType().GetField("chunkThread", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(server.GetType().FullName, "chunkThread");
		object chunkThread = chunkThreadField.GetValue(server)
			?? throw new InvalidOperationException("Vintage Story chunk database thread is unavailable during YangTransport save migration.");

		FieldInfo gameDatabaseField = chunkThread.GetType().GetField("gameDatabase", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(chunkThread.GetType().FullName, "gameDatabase");
		object gameDatabase = gameDatabaseField.GetValue(chunkThread)
			?? throw new InvalidOperationException("Vintage Story save database is unavailable during YangTransport save migration.");

		MethodInfo createBackup = gameDatabase.GetType().GetMethod("CreateBackup", new[] { typeof(string) })
			?? throw new MissingMethodException(gameDatabase.GetType().FullName, "CreateBackup");

		try { createBackup.Invoke(gameDatabase, new object[] { backupFilename }); }
		catch (TargetInvocationException exception) when (exception.InnerException != null)
		{
			throw new IOException("Vintage Story failed to create the pre-migration save backup.", exception.InnerException);
		}

		string backupPath = Path.Combine(GamePaths.Backups, backupFilename);
		if (!File.Exists(backupPath) || new FileInfo(backupPath).Length == 0) throw new IOException("Vintage Story did not produce a valid pre-migration save backup.");

		serverAPI.Logger.Notification("[yangtransport] Created pre-migration save backup '{0}'.", backupPath);
	}

	private static void MigrateR0ToR1Configs(ICoreServerAPI serverAPI)
	{
		MigrateR0ToR1MainConfig(serverAPI);
		MigrateConfigFile(serverAPI, "station_names.json");
	}

	private static void MigrateR0ToR1MainConfig(ICoreServerAPI serverAPI)
	{
		const string legacyFilename = "yangtransport.json";
		string sourcePath = Path.Combine(GamePaths.ModConfig, legacyFilename);
		if (!File.Exists(sourcePath)) return;

		string targetPath = Path.Combine(GamePaths.ModConfig, YangTransportSettings.ConfigurationFilename);
		Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

		if (!File.Exists(targetPath)) { File.Move(sourcePath, targetPath); }
		else if (File.ReadAllBytes(sourcePath).AsSpan().SequenceEqual(File.ReadAllBytes(targetPath))) { File.Delete(sourcePath); }
		else
		{
			YangTransportServerConfig source = serverAPI.LoadModConfig<YangTransportServerConfig>(legacyFilename)
				?? throw new InvalidDataException($"Could not read legacy YangTransport config '{legacyFilename}'.");
			YangTransportServerConfig target = serverAPI.LoadModConfig<YangTransportServerConfig>(YangTransportSettings.ConfigurationFilename)
				?? throw new InvalidDataException($"Could not read YangTransport config '{YangTransportSettings.ConfigurationFilename}'.");

			target.EngineFuelConsumptionMultiplier = source.EngineFuelConsumptionMultiplier;
			target.ContaminatedLiquidHeatingMultiplier = source.ContaminatedLiquidHeatingMultiplier;
			target.ContaminatedLiquidEvaporationMultiplier = source.ContaminatedLiquidEvaporationMultiplier;
			target.FlammableLiquidHeatingMultiplier = source.FlammableLiquidHeatingMultiplier;
			target.FlammableLiquidEvaporationMultiplier = source.FlammableLiquidEvaporationMultiplier;

			serverAPI.StoreModConfig(target, YangTransportSettings.ConfigurationFilename);
			File.Delete(sourcePath);
		}

		serverAPI.Logger.Notification("[yangtransport] Moved config '{0}' to '{1}'.", legacyFilename, YangTransportSettings.ConfigurationFilename);
	}

	private static void MigrateConfigFile(ICoreServerAPI serverAPI, string filename)
	{
		string sourcePath = Path.Combine(GamePaths.ModConfig, filename);
		if (!File.Exists(sourcePath)) return;

		string targetPath = Path.Combine(GamePaths.ModConfig, "yangtransport", filename);
		Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

		if (File.Exists(targetPath))
		{
			if (!File.ReadAllBytes(sourcePath).AsSpan().SequenceEqual(File.ReadAllBytes(targetPath)))
				throw new IOException($"Cannot migrate YangTransport config '{filename}' because both the old and new locations contain different files.");

			File.Delete(sourcePath);
		}
		else File.Move(sourcePath, targetPath);

		serverAPI.Logger.Notification("[yangtransport] Moved config '{0}' to '{1}'.", filename, Path.Combine("yangtransport", filename));
	}

	private static byte[]? UpgradeR0ToR1(byte[]? payload)
	{
		if (payload == null || payload.Length == 0) return payload;

		var insertionOffsets = new List<int>();
		ScanOffscreenState(payload, hasRefrigerationField: false, insertionOffsets);

		if (insertionOffsets.Count == 0) return payload;

		byte[] upgraded = new byte[checked(payload.Length + insertionOffsets.Count)];
		int sourceOffset = 0;
		int destinationOffset = 0;

		for (int index = 0; index < insertionOffsets.Count; index++)
		{
			int insertionOffset = insertionOffsets[index];
			int copyLength = insertionOffset - sourceOffset;

			Buffer.BlockCopy(payload, sourceOffset, upgraded, destinationOffset, copyLength);
			destinationOffset += copyLength;
			upgraded[destinationOffset++] = 0; // HasRefrigeration = false
			sourceOffset = insertionOffset;
		}

		Buffer.BlockCopy(payload, sourceOffset, upgraded, destinationOffset, payload.Length - sourceOffset);
		ScanOffscreenState(upgraded, hasRefrigerationField: true, insertionOffsets: null);
		return upgraded;
	}

	private static byte[]? UpgradeR1ToR2(byte[]? payload, byte[]? authorityPayload, ILogger logger)
	{
		if (payload == null || payload.Length == 0) return payload;
		using var stream = new MemoryStream(payload, writable: false);
		using var reader = new BinaryReader(stream);
		var records = new List<(int Offset, int Length)>();
		int unreadOffset = sizeof(int);
		try
		{
			int count = reader.ReadInt32(); // Known record boundaries remain usable even when the global count is damaged.
			while (stream.Position < stream.Length)
			{
				int offset = checked((int)stream.Position);
				unreadOffset = offset;
				if (records.Count >= MaxVirtualConvoys) throw new InvalidDataException("Too many virtual convoys!");
				// Only locate boundaries here. Semantic validation belongs to the isolated R2 loader.
				SkipVirtualConvoy(reader, hasRefrigerationField: true, insertionOffsets: null);
				records.Add((offset, checked((int)stream.Position - offset)));
			}
			if (records.Count != count) logger.Warning("[yangtransport] Recovered {0} R1 virtual convoy records despite saved count {1}.", records.Count, count);
		}
		catch (Exception exception)
		{
			logger.Error("[yangtransport] Damaged R1 offscreen record at byte {0}. Scanning for remaining authority-matched convoys. {1}", unreadOffset, exception.Message);

			var authorities = new Dictionary<long, (long Head, int Index)>();
			if (authorityPayload != null)
			{
				const int authorityBytes = sizeof(long) * 3 + sizeof(byte) + sizeof(int);
				for (int offset = sizeof(long) + sizeof(int); offset <= authorityPayload.Length - authorityBytes; offset += authorityBytes)
				{
					ReadOnlySpan<byte> entry = authorityPayload.AsSpan(offset, authorityBytes);
					if (entry[24] != (byte)RailEntityAuthorityMode.Virtual) continue;
					authorities.TryAdd
					(
						BinaryPrimitives.ReadInt64LittleEndian(entry), (BinaryPrimitives.ReadInt64LittleEndian(entry[8..]),
						BinaryPrimitives.ReadInt32LittleEndian(entry[25..]))
					);
				}
			}
			int recoveredCount = 0;
			for (int offset = unreadOffset; offset <= payload.Length - 94 && records.Count < MaxVirtualConvoys; offset++)
			{
				long headID = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset));
				if (headID == 0 || payload[offset + 8] > 1 || !authorities.TryGetValue(headID, out var head) || head.Head != headID || head.Index != 0) continue;
				try
				{
					stream.Position = offset;
					SkipVirtualConvoy(reader, hasRefrigerationField: true, insertionOffsets: null, authorities);
					records.Add((offset, checked((int)stream.Position - offset)));
					recoveredCount++;
					offset = checked((int)stream.Position - 1);
				}
				catch (Exception) { } // Keep looking
			}
			logger.Warning("[yangtransport] Recovered {0} additional R1 virtual convoy records. Preserving {1} in total.", recoveredCount, records.Count);
		}
		return OffscreenConvoySimSystem.WriteSaveRecords(records.Count, (index, writer) => writer.Write(payload, records[index].Offset, records[index].Length));
	}

	private static void ScanOffscreenState(byte[] payload, bool hasRefrigerationField, List<int>? insertionOffsets)
	{
		using var stream = new MemoryStream(payload, writable: false);
		using var reader = new BinaryReader(stream);

		int convoyCount = ReadCount(reader, MaxVirtualConvoys, "virtual convoy");
		for (int convoyIndex = 0; convoyIndex < convoyCount; convoyIndex++) SkipVirtualConvoy(reader, hasRefrigerationField, insertionOffsets);

		if (stream.Position != stream.Length) throw new InvalidDataException("Trailing YangTransport offscreen-state data.");
	}

	private static void SkipVirtualConvoy
	(
		BinaryReader reader, bool hasRefrigerationField, List<int>? insertionOffsets,
		Dictionary<long, (long Head, int Index)>? expectedAuthorities = null
	)
	{
		// Head/gauge/motion state, lead state, max elapsed time and cursor.
		long headID = reader.ReadInt64();
		SkipBytes(reader, 85);

		if (reader.ReadBoolean())
		{
			SkipBytes(reader, sizeof(double)); // PathHeadDistance
			SkipConvoyRoute(reader);
		}

		// Distance simulation state, terminal flag and terminal elapsed time.
		SkipBytes(reader, sizeof(double) * 4 + sizeof(bool));

		if (reader.ReadBoolean()) SkipAutomation(reader);

		int vehicleCount = ReadCount(reader, MaxVehiclesPerConvoy, "virtual vehicle");
		if (expectedAuthorities != null && vehicleCount == 0) throw new InvalidDataException("Empty recovery candidate.");
		for (int vehicleIndex = 0; vehicleIndex < vehicleCount; vehicleIndex++)
		{
			long entityID = SkipVehicleSnapshot(reader, hasRefrigerationField, insertionOffsets);
			if 
			(
				expectedAuthorities != null && (!expectedAuthorities.TryGetValue(entityID, out var authority) ||
				authority.Head != headID || authority.Index != vehicleIndex)
			) { throw new InvalidDataException("Recovery candidate authority mismatch."); }
		}

		reader.ReadBoolean(); // Orphaned
		int poseCount = ReadCount(reader, MaxVehiclesPerConvoy, "frozen pose");

		SkipBytes(reader, checked((long)poseCount * FrozenPoseBytes));
	}

	private static long SkipVehicleSnapshot(BinaryReader reader, bool hasRefrigerationField, List<int>? insertionOffsets)
	{
		long entityID = reader.ReadInt64();
		SkipBytes(reader, sizeof(long)); // Authority generation
		reader.ReadString(); // Entity code

		int entityByteLength = reader.ReadInt32();
		if (entityByteLength < 0 || entityByteLength > MaxEntityBytes) throw new InvalidDataException($"Invalid virtual vehicle snapshot length {entityByteLength}.");

		SkipBytes(reader, entityByteLength);

		// Captured pose, gauge/convoy geometry/weight/flags, then drive and fuel/water state.
		SkipBytes(reader, FrozenPoseBytes + 43 + 40);

		reader.ReadInt32(); // TimetableCurrentIndex
		int timetableCount = ReadCount(reader, MaxRouteEntries, "vehicle timetable route");
		for (int routeIndex = 0; routeIndex < timetableCount; routeIndex++) SkipRouteEntry(reader);

		if (!hasRefrigerationField) { insertionOffsets?.Add(checked((int)reader.BaseStream.Position)); return entityID; }

		if (reader.ReadBoolean())
		{
			SkipBytes(reader, sizeof(float) + sizeof(double) * 2 + sizeof(bool));
			if (reader.ReadBoolean()) new TreeAttribute().FromBytes(reader);
		}
		return entityID;
	}

	private static void SkipAutomation(BinaryReader reader)
	{
		// CurrentStationIndex, five doubles and Status.
		SkipBytes(reader, sizeof(int) * 2 + sizeof(double) * 5);

		int routeCount = ReadCount(reader, MaxRouteEntries, "automation route");
		for (int routeIndex = 0; routeIndex < routeCount; routeIndex++) SkipRouteEntry(reader);
	}

	private static void SkipRouteEntry(BinaryReader reader)
	{
		reader.ReadString();
		SkipBytes(reader, sizeof(ulong) + sizeof(int) * 6 + sizeof(bool) * 2);
	}

	private static void SkipConvoyRoute(BinaryReader reader)
	{
		if (reader.ReadInt32() != RouteStartSentinel) throw new InvalidDataException("Invalid virtual convoy route start marker.");

		// GraphVersion, Gauge, Epoch and Revision.
		SkipBytes(reader, sizeof(int) * 2 + sizeof(byte) + sizeof(uint));

		int runCount = reader.ReadInt32();
		if (runCount <= 0 || runCount > MaxRouteRuns) throw new InvalidDataException($"Invalid virtual convoy route run count {runCount}.");

		SkipBytes(reader, checked((long)runCount * RouteRunBytes));

		if (reader.ReadInt32() != RouteEndSentinel) throw new InvalidDataException("Invalid virtual convoy route end marker.");
	}

	private static int ReadCount(BinaryReader reader, int maximum, string label)
	{
		int count = reader.ReadInt32();
		if (count < 0 || count > maximum) throw new InvalidDataException($"Invalid {label} count {count}.");
		
		return count;
	}

	private static void SkipBytes(BinaryReader reader, long byteCount)
	{
		Stream stream = reader.BaseStream;
		if (byteCount < 0 || byteCount > stream.Length - stream.Position) throw new EndOfStreamException("Truncated YangTransport offscreen-state data.");

		stream.Position += byteCount;
	}
}
