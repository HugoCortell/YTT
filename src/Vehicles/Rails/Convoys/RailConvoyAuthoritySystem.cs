using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace YangTransport;

internal enum RailEntityAuthorityMode : byte
{
	Material = 1,
	Virtual = 2,
	Deleted = 3
}

internal readonly record struct RailEntityAuthority(
	long HeadID,
	long Generation,
	RailEntityAuthorityMode Mode,
	int ConvoyIndex);

internal readonly record struct RailAuthorityTransitionMember(
	long EntityID,
	long Generation,
	int ConvoyIndex);

internal sealed class MaterialConvoyTopology
{
	internal readonly long HeadID;
	internal readonly long[] Members;

	internal MaterialConvoyTopology(long headID, long[] members)
	{
		HeadID = headID;
		Members = members;
	}
}

/// Global rail-entity identity and convoy-topology authority.
/// Physical entity state stays in vanilla chunks. This system stores only the small logical relationship that must survive independently saved member chunks.
/// Explicit material topology edits are additionally journaled until the next staged global authority snapshot.
internal sealed class RailConvoyAuthoritySystem : ModSystem
{
	private const string SaveKey = "yangtransport_convoyauthority";
	private const string AuthorityGenerationAttribute = "yangtransport:authorityGeneration";
	private const int MaxAuthorityRecords = 4_000_000;
	private const int MaxDeletedAuthorityRecords = 65_536;

	private ICoreServerAPI? ServerAPI;
	private readonly Dictionary<long, RailEntityAuthority> AuthorityByEntityID = new();
	private readonly Dictionary<long, MaterialConvoyTopology> MaterialTopologiesByHead = new();
	private readonly Queue<long> DeletedAuthorityOrder = new();
	private readonly HashSet<long> DeletedAuthorityIDs = new();
	private readonly HashSet<long> MaterializingEntityIDs = new();
	private readonly ConvoyTopologyRecoveryJournal RecoveryJournal = new();

	private bool Dirty;
	private long TopologySerial; // Journal transaction sequence, not a save-format version.

	public override double ExecuteOrder() => 0.095;
	public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

	public override void Start(ICoreAPI coreAPI)
	{
		ServerAPI = coreAPI as ICoreServerAPI;
		if (ServerAPI == null) return;

		ServerAPI.Event.SaveGameLoaded += OnSaveGameLoaded;
		ServerAPI.Event.GameWorldSave += OnGameWorldSave;
	}

	public override void Dispose()
	{
		if (ServerAPI != null)
		{
			Save(force: true);
			ServerAPI.Event.SaveGameLoaded -= OnSaveGameLoaded;
			
			// Keep the handler only during real server shutdown.
			// A soft exit will fire the final save and checkpoint the journal, HardExit fires no save event.
			if (!ServerAPI.Server.IsShuttingDown) ServerAPI.Event.GameWorldSave -= OnGameWorldSave;
		}

		RecoveryJournal.Dispose();
		base.Dispose();
	}

	internal bool TryGetAuthority(long entityID, out RailEntityAuthority authority) { return AuthorityByEntityID.TryGetValue(entityID, out authority); }

	internal bool TryGetMaterialTopology(long entityID, out MaterialConvoyTopology topology, out int convoyIndex)
	{
		topology = null!;
		convoyIndex = -1;

		if
		(
			!AuthorityByEntityID.TryGetValue(entityID, out RailEntityAuthority authority) ||
			authority.Mode != RailEntityAuthorityMode.Material ||
			!MaterialTopologiesByHead.TryGetValue(authority.HeadID, out topology)
		) { return false; }

		convoyIndex = authority.ConvoyIndex;
		return (uint)convoyIndex < (uint)topology.Members.Length && topology.Members[convoyIndex] == entityID;
	}

	internal bool IsLoadedMaterialAuthorityCurrent(Entity entity)
	{
		if (entity == null || entity.State == EnumEntityState.Despawned) return false;
		if (MaterializingEntityIDs.Contains(entity.EntityId)) return true;

		if (!AuthorityByEntityID.TryGetValue(entity.EntityId, out RailEntityAuthority authority)) return true;

		return authority.Mode == RailEntityAuthorityMode.Material && ReadEntityGeneration(entity) == authority.Generation;
	}


	/// Commit the canonical topology already applied to the supplied loaded vehicles.
	/// This is called only after a real merge/split/free transaction succeeds.
	internal void CommitLoadedMaterialTopology(IReadOnlyList<IRailwayConvoyVehicle> touchedVehicles)
	{
		if (ServerAPI == null || touchedVehicles == null || touchedVehicles.Count == 0) return;

		var records = new List<KeyValuePair<long, RailEntityAuthority>>(touchedVehicles.Count);
		var seen = new HashSet<long>();

		for (int iteration = 0; iteration < touchedVehicles.Count; iteration++)
		{
			IRailwayConvoyVehicle vehicle = touchedVehicles[iteration];
			Entity entity = vehicle.Entity;
			long entityID = entity.EntityId;
			if (entityID == 0 || !seen.Add(entityID)) continue;

			long generation = ReadEntityGeneration(entity);
			if (AuthorityByEntityID.TryGetValue(entityID, out RailEntityAuthority current))
			{
				if (current.Mode == RailEntityAuthorityMode.Material)
				generation = Math.Max(generation, current.Generation);
			}
			long headID = vehicle.ConvoyHeadEntityID != 0 ? vehicle.ConvoyHeadEntityID : entityID;
			int index = vehicle.ConvoyHeadEntityID != 0 ? Math.Max(0, vehicle.ConvoyOrderIndex) : -1;

			records.Add(new KeyValuePair<long, RailEntityAuthority>(entityID, new RailEntityAuthority(headID, generation, RailEntityAuthorityMode.Material, index)));
		}

		if (records.Count == 0) return;

		long serial;
		try { serial = checked(TopologySerial + 1); }
		catch (OverflowException) { ServerAPI.Logger.Error("[yangtransport] Material convoy topology serial overflowed."); return; }

		if (!RecoveryJournal.Append(serial, records))
		{
			ServerAPI.Logger.Error("[yangtransport] Material convoy topology changed but its crash-recovery journal could not be written.");
		}

		var oldHeads = new HashSet<long>();
		for (int iteration = 0; iteration < records.Count; iteration++)
		{
			long entityID = records[iteration].Key;
			if (AuthorityByEntityID.TryGetValue(entityID, out RailEntityAuthority oldAuthority) && oldAuthority.Mode == RailEntityAuthorityMode.Material)
			{
				oldHeads.Add(oldAuthority.HeadID);
			}
		}
		foreach (long oldHead in oldHeads) MaterialTopologiesByHead.Remove(oldHead);

		for (int iteration = 0; iteration < records.Count; iteration++)
		{
			KeyValuePair<long, RailEntityAuthority> pair = records[iteration];
			UntrackDeletedAuthority(pair.Key);
			AuthorityByEntityID[pair.Key] = pair.Value;
		}

		RebuildTouchedMaterialTopologies(records);
		TopologySerial = serial;
		Dirty = true;
	}

	internal bool TryEnterVirtual(long headID, IReadOnlyList<RailAuthorityTransitionMember> members, out long[] generations)
	{
		generations = Array.Empty<long>();
		if (headID == 0 || members == null || members.Count == 0) return false;

		var seen = new HashSet<long>();
		generations = new long[members.Count];

		try
		{
			for (int iteration = 0; iteration < members.Count; iteration++)
			{
				RailAuthorityTransitionMember member = members[iteration];
				if (member.EntityID == 0 || !seen.Add(member.EntityID)) return false;

				long currentGeneration = Math.Max(0, member.Generation);
				if (AuthorityByEntityID.TryGetValue(member.EntityID, out RailEntityAuthority authority))
				{
					if (authority.Mode != RailEntityAuthorityMode.Material || authority.Generation != currentGeneration) { return false; }
					currentGeneration = authority.Generation;
				}

				generations[iteration] = checked(currentGeneration + 1);
			}
		}
		catch (OverflowException)
		{
			generations = Array.Empty<long>();
			return false;
		}

		for (int iteration = 0; iteration < members.Count; iteration++)
		{
			RailAuthorityTransitionMember member = members[iteration];
			UntrackDeletedAuthority(member.EntityID);
			AuthorityByEntityID[member.EntityID] = new RailEntityAuthority(headID, generations[iteration], RailEntityAuthorityMode.Virtual, iteration);
		}

		MaterialTopologiesByHead.Remove(headID);
		Dirty = true;
		return true;
	}

	internal long NextMaterialGeneration(long headID, long entityID, long expectedVirtualGeneration)
	{
		if
		(
			!AuthorityByEntityID.TryGetValue(entityID, out RailEntityAuthority authority) || authority.Mode != RailEntityAuthorityMode.Virtual ||
			authority.HeadID != headID || authority.Generation != expectedVirtualGeneration
		) { throw new InvalidOperationException($"Virtual authority for rail entity {entityID} changed during materialization."); }

		return checked(authority.Generation + 1);
	}

	internal void CommitVirtualToMaterial(long virtualHeadID, IReadOnlyList<RailAuthorityTransitionMember> members, bool preserveConvoy)
	{
		if (members == null || members.Count == 0) throw new InvalidOperationException("Material authority commit has no members.");

		for (int iteration = 0; iteration < members.Count; iteration++)
		{
			RailAuthorityTransitionMember member = members[iteration];
			if
			(
				!AuthorityByEntityID.TryGetValue(member.EntityID, out RailEntityAuthority authority) || authority.Mode != RailEntityAuthorityMode.Virtual ||
				authority.HeadID != virtualHeadID || member.Generation <= authority.Generation
			) { throw new InvalidOperationException( $"Virtual authority for rail entity {member.EntityID} changed before materialization commit."); }
		}

		if (preserveConvoy)
		{
			long headID = members[0].EntityID;
			long[] ordered = new long[members.Count];

			for (int iteration = 0; iteration < members.Count; iteration++)
			{
				RailAuthorityTransitionMember member = members[iteration];
				UntrackDeletedAuthority(member.EntityID);
				AuthorityByEntityID[member.EntityID] = new RailEntityAuthority(headID, member.Generation, RailEntityAuthorityMode.Material, iteration);
				ordered[iteration] = member.EntityID;
			}

			MaterialTopologiesByHead.Remove(virtualHeadID);
			MaterialTopologiesByHead[headID] = new MaterialConvoyTopology(headID, ordered);
		}
		else
		{
			MaterialTopologiesByHead.Remove(virtualHeadID);
			for (int iteration = 0; iteration < members.Count; iteration++)
			{
				RailAuthorityTransitionMember member = members[iteration];
				UntrackDeletedAuthority(member.EntityID);
				AuthorityByEntityID[member.EntityID] = new RailEntityAuthority(member.EntityID, member.Generation, RailEntityAuthorityMode.Material, -1);
			}
		}

		Dirty = true;
	}

	internal void MarkTrackedEntityDeleted(long entityID)
	{
		if (!AuthorityByEntityID.TryGetValue(entityID, out RailEntityAuthority authority)) return;

		long nextGeneration;
		try { nextGeneration = checked(authority.Generation + 1); }
		catch (OverflowException) { nextGeneration = authority.Generation; }

		AuthorityByEntityID[entityID] = new RailEntityAuthority(authority.HeadID, nextGeneration, RailEntityAuthorityMode.Deleted, -1);
		MaterialTopologiesByHead.Remove(authority.HeadID);
		TrackDeletedAuthority(entityID);
		Dirty = true;
	}

	internal void RetireVirtualAuthority(long headID, IReadOnlyList<RailAuthorityTransitionMember> members)
	{
		for (int iteration = 0; iteration < members.Count; iteration++)
		{
			RailAuthorityTransitionMember member = members[iteration];
			long currentGeneration = member.Generation;
			if (AuthorityByEntityID.TryGetValue(member.EntityID, out RailEntityAuthority authority)) currentGeneration = Math.Max(currentGeneration, authority.Generation);

			long nextGeneration;
			try { nextGeneration = checked(currentGeneration + 1); }
			catch (OverflowException) { nextGeneration = currentGeneration; }

			AuthorityByEntityID[member.EntityID] = new RailEntityAuthority(headID, nextGeneration, RailEntityAuthorityMode.Deleted, -1);
			TrackDeletedAuthority(member.EntityID);
		}

		MaterialTopologiesByHead.Remove(headID);
		Dirty = true;
	}

	internal void ForgetUnrestoredVirtualAuthorities(ISet<long> retainedEntityIDs)
	{
		if (AuthorityByEntityID.Count == 0) return;

		var remove = new List<long>();
		foreach (KeyValuePair<long, RailEntityAuthority> pair in AuthorityByEntityID)
		{
			if (pair.Value.Mode == RailEntityAuthorityMode.Virtual && !retainedEntityIDs.Contains(pair.Key)) remove.Add(pair.Key);
		}

		// A rejected virtual snapshot must not suppress a usable entity still stored in a chunk.
		// Only remove virtual records, unrelated material topologies and real deletions remain authoritative.
		foreach (long entityID in remove) AuthorityByEntityID.Remove(entityID);
		if (remove.Count > 0) Dirty = true;
		if (remove.Count > 0) ServerAPI?.Logger.Warning("[yangtransport] Released {0} unrestored virtual vehicle authorities. Saved chunk entities may recover them.", remove.Count);
	}

	internal void BeginMaterialization(long entityID)	{ if (entityID != 0) MaterializingEntityIDs.Add(entityID); }
	internal void EndMaterialization(long entityID)		{ if (entityID != 0) MaterializingEntityIDs.Remove(entityID); }

	internal static long ReadEntityGeneration(Entity entity) { return Math.Max(0, entity.Attributes.GetLong(AuthorityGenerationAttribute, 0)); }
	internal static void WriteEntityGeneration(Entity entity, long generation) { entity.Attributes.SetLong(AuthorityGenerationAttribute, generation); }

	private void OnSaveGameLoaded()
	{
		if (ServerAPI == null) return;

		AuthorityByEntityID.Clear();
		MaterialTopologiesByHead.Clear();
		DeletedAuthorityOrder.Clear();
		DeletedAuthorityIDs.Clear();
		MaterializingEntityIDs.Clear();
		Dirty = false;
		TopologySerial = 0;

		byte[]? payload = ServerAPI.WorldManager.SaveGame.GetData(SaveKey);
		if (payload is { Length: > 0 })
		{
			try
			{
				using MemoryStream memoryStream = new(payload, writable: false);
				using BinaryReader binaryReader = new(memoryStream);

				TopologySerial = binaryReader.ReadInt64();
				if (TopologySerial < 0) { TopologySerial = 0; Dirty = true; }

				int count = binaryReader.ReadInt32();
				const int recordBytes = sizeof(long) * 3 + sizeof(byte) + sizeof(int); // Fixed width, bad count or truncated ones won't damage others.
				int availableCount = (int)((memoryStream.Length - memoryStream.Position) / recordBytes);
				if (count != availableCount || (memoryStream.Length - memoryStream.Position) % recordBytes != 0)
				{
					ServerAPI.Logger.Warning("[yangtransport] Damaged convoy-authority count/tail found. Reading {0} complete records.", availableCount);
					Dirty = true;
				}
				count = Math.Min(availableCount, MaxAuthorityRecords);

				for (int iteration = 0; iteration < count; iteration++)
				{
					long entityID = binaryReader.ReadInt64();
					long headID = binaryReader.ReadInt64();
					long generation = binaryReader.ReadInt64();
					RailEntityAuthorityMode mode = (RailEntityAuthorityMode)binaryReader.ReadByte();
					int index = binaryReader.ReadInt32();

					bool validIndex = mode switch
					{
						RailEntityAuthorityMode.Material => index >= 0 || (index == -1 && headID == entityID),
						RailEntityAuthorityMode.Virtual => index >= 0,
						RailEntityAuthorityMode.Deleted => index == -1,
						_ => false
					};
					if
					(
						entityID == 0 || headID == 0 || generation < 0 || !Enum.IsDefined(typeof(RailEntityAuthorityMode), mode) ||
						(mode != RailEntityAuthorityMode.Material && generation == 0) || !validIndex ||
						!AuthorityByEntityID.TryAdd(entityID, new RailEntityAuthority(headID, generation, mode, index))
					)
					{
						ServerAPI.Logger.Warning("[yangtransport] Ignoring invalid or duplicate convoy-authority record {0} (entity {1}).", iteration, entityID);
						Dirty = true;
						continue;
					}

					if (mode == RailEntityAuthorityMode.Deleted) TrackDeletedAuthority(entityID);
				}
			}
			catch (Exception exception)
			{
				ServerAPI.Logger.Error("[yangtransport] Convoy authority state could not be fully loaded. Retaining recovered records.");
				ServerAPI.Logger.Error(exception);
				Dirty = true;
			}
		}

		RebuildMaterialTopologyIndex();

		RecoveryJournal.OpenForWorld(ServerAPI);
		if (RecoveryJournal.ReplayNewerThan(TopologySerial, AuthorityByEntityID, out long recoveredSerial))
		{
			TopologySerial = recoveredSerial;
			RebuildMaterialTopologyIndex();
			Dirty = true;
			ServerAPI.Logger.Warning("[yangtransport] Replayed material convoy topology crash journal through serial {0}.", TopologySerial);
		}
	}

	private void OnGameWorldSave() { if (Save()) RecoveryJournal.Checkpoint(TopologySerial); }

	private bool Save(bool force = false)
	{
		if (ServerAPI == null) return false;
		if (!force && !Dirty && !RecoveryJournal.HasPending) return true;

		try
		{
			TrimDeletedAuthorities();

			List<long> entityIDs = new(AuthorityByEntityID.Keys);
			entityIDs.Sort();
			if (entityIDs.Count > MaxAuthorityRecords) throw new InvalidDataException("Convoy authority exceeds the supported record count.");

			using MemoryStream memoryStream = new(4096);
			using BinaryWriter binaryWriter = new(memoryStream);
			binaryWriter.Write(TopologySerial);
			binaryWriter.Write(entityIDs.Count);

			for (int iteration = 0; iteration < entityIDs.Count; iteration++)
			{
				long entityID = entityIDs[iteration];
				RailEntityAuthority authority = AuthorityByEntityID[entityID];
				binaryWriter.Write(entityID);
				binaryWriter.Write(authority.HeadID);
				binaryWriter.Write(authority.Generation);
				binaryWriter.Write((byte)authority.Mode);
				binaryWriter.Write(authority.ConvoyIndex);
			}

			binaryWriter.Flush();
			ServerAPI.WorldManager.SaveGame.StoreData(SaveKey, memoryStream.ToArray());
			Dirty = false;
			return true;
		}
		catch (Exception exception)
		{
			ServerAPI.Logger.Error("[yangtransport] Failed to serialize convoy authority state.");
			ServerAPI.Logger.Error(exception);
			return false;
		}
	}

	private void RebuildTouchedMaterialTopologies(
		IReadOnlyList<KeyValuePair<long, RailEntityAuthority>> records)
	{
		var groups = new Dictionary<long, List<(int Index, long EntityId)>>();
		for (int iteration = 0; iteration < records.Count; iteration++)
		{
			KeyValuePair<long, RailEntityAuthority> pair = records[iteration];
			RailEntityAuthority authority = pair.Value;
			if (authority.Mode != RailEntityAuthorityMode.Material || authority.ConvoyIndex < 0) continue;

			if (!groups.TryGetValue(authority.HeadID, out List<(int Index, long EntityId)>? members))
			{
				members = new List<(int Index, long EntityId)>();
				groups[authority.HeadID] = members;
			}
			members.Add((authority.ConvoyIndex, pair.Key));
		}

		foreach (KeyValuePair<long, List<(int Index, long EntityId)>> pair in groups)
		{
			List<(int Index, long EntityId)> members = pair.Value;
			members.Sort(static (firstMember, secondMember) => firstMember.Index.CompareTo(secondMember.Index));

			if (members.Count <= 1) { MaterialTopologiesByHead.Remove(pair.Key); continue; }

			bool valid = members[0].Index == 0 && members[0].EntityId == pair.Key;
			for (int iteration = 0; valid && iteration < members.Count; iteration++) valid = members[iteration].Index == iteration;

			if (!valid) { MaterialTopologiesByHead.Remove(pair.Key); continue; }

			long[] ordered = new long[members.Count];
			for (int iteration = 0; iteration < members.Count; iteration++) ordered[iteration] = members[iteration].EntityId;
			MaterialTopologiesByHead[pair.Key] = new MaterialConvoyTopology(pair.Key, ordered);
		}
	}

	internal void RebuildMaterialTopologyIndex()
	{
		MaterialTopologiesByHead.Clear();

		var groups = new Dictionary<long, List<(int Index, long EntityId)>>();
		foreach (KeyValuePair<long, RailEntityAuthority> pair in AuthorityByEntityID)
		{
			RailEntityAuthority authority = pair.Value;
			if (authority.Mode != RailEntityAuthorityMode.Material || authority.ConvoyIndex < 0) continue;

			if (!groups.TryGetValue(authority.HeadID, out List<(int Index, long EntityId)>? members))
			{
				members = new List<(int Index, long EntityId)>();
				groups[authority.HeadID] = members;
			}
			members.Add((authority.ConvoyIndex, pair.Key));
		}

		foreach (KeyValuePair<long, List<(int Index, long EntityId)>> pair in groups)
		{
			long headID = pair.Key;
			List<(int Index, long EntityId)> members = pair.Value;
			members.Sort(static (firstMember, secondMember) =>
			{
				int indexComparison = firstMember.Index.CompareTo(secondMember.Index);
				return indexComparison != 0 ? indexComparison : firstMember.EntityId.CompareTo(secondMember.EntityId);
			});

			if (members.Count <= 1) continue;

			bool valid = members[0].Index == 0 && members[0].EntityId == headID;
			for (int iteration = 0; valid && iteration < members.Count; iteration++) valid = members[iteration].Index == iteration;

			if (!valid)
			{
				ServerAPI?.Logger.Warning ("[yangtransport] Ignoring invalid material convoy authority group {0}.", headID);
				continue;
			}

			long[] ordered = new long[members.Count];
			for (int iteration = 0; iteration < members.Count; iteration++) ordered[iteration] = members[iteration].EntityId;
			MaterialTopologiesByHead[headID] = new MaterialConvoyTopology(headID, ordered);
		}
	}

	private void TrackDeletedAuthority(long entityID)
	{
		if (entityID == 0 || !DeletedAuthorityIDs.Add(entityID)) return;
		DeletedAuthorityOrder.Enqueue(entityID);
		TrimDeletedAuthorities();
	}

	private void UntrackDeletedAuthority(long entityID) { if (entityID != 0) DeletedAuthorityIDs.Remove(entityID); }

	private void TrimDeletedAuthorities()
	{
		while (DeletedAuthorityIDs.Count > MaxDeletedAuthorityRecords &&
			DeletedAuthorityOrder.Count > 0)
		{
			long entityID = DeletedAuthorityOrder.Dequeue();
			if (!DeletedAuthorityIDs.Remove(entityID)) continue;

			if (AuthorityByEntityID.TryGetValue(entityID, out RailEntityAuthority authority) && authority.Mode == RailEntityAuthorityMode.Deleted)
			{
				AuthorityByEntityID.Remove(entityID);
				Dirty = true;
			}
		}

		while (DeletedAuthorityOrder.Count > DeletedAuthorityIDs.Count * 2 + 16 && DeletedAuthorityOrder.Count > 0)
		{
			long entityID = DeletedAuthorityOrder.Dequeue();
			if (!DeletedAuthorityIDs.Contains(entityID)) continue;
			DeletedAuthorityOrder.Enqueue(entityID);
			break;
		}
	}

	private sealed class ConvoyTopologyRecoveryJournal : IDisposable
	{
		private const int JournalFileSignature = 0x4A435459; // YTCJ | Fixed file-type signature. Not a format version.
		private const int MemberBytes = sizeof(long) * 3 + sizeof(int);
		private const int MaxMembersPerRecord = 100_000;

		private readonly List<JournalRecord> Records = new();

		private ICoreServerAPI? ServerAPI;
		private string? FilePath;
		private string SaveGameIdentifier = "";
		private FileStream? AppendStream;
		private BinaryWriter? AppendWriter;
		private bool WriteFailureLogged;

		internal bool HasPending => Records.Count != 0;

		internal void OpenForWorld(ICoreServerAPI serverAPI)
		{
			DisposeWriter();
			Records.Clear();

			ServerAPI = serverAPI;
			SaveGameIdentifier = serverAPI.World.SavegameIdentifier ?? "";
			FilePath = serverAPI.WorldManager.CurrentWorldFilepath + ".yangtransport-convoyjournal";
			WriteFailureLogged = false;

			if (!File.Exists(FilePath)) return;

			try
			{
				using FileStream stream = new(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
				using BinaryReader reader = new(stream);

				if (stream.Length < sizeof(int) || reader.ReadInt32() != JournalFileSignature) { ResetInvalidJournal("invalid file signature"); return; }

				string journalWorld = reader.ReadString();
				if (!string.Equals(journalWorld, SaveGameIdentifier, StringComparison.Ordinal)) { ResetInvalidJournal("savegame identifier mismatch"); return; }

				long completeLength = stream.Position;
				while (stream.Length - stream.Position >= sizeof(long) + sizeof(int))
				{
					long recordStart = stream.Position;
					long serial = reader.ReadInt64();
					int count = reader.ReadInt32();
					if (serial <= 0 || count <= 0 || count > MaxMembersPerRecord) { stream.SetLength(recordStart); break; }

					long requiredBytes = (long)count * MemberBytes;
					if (stream.Length - stream.Position < requiredBytes) { stream.SetLength(recordStart); break; }

					var members = new KeyValuePair<long, RailEntityAuthority>[count];
					for (int iteration = 0; iteration < count; iteration++)
					{
						long entityID = reader.ReadInt64();
						long headID = reader.ReadInt64();
						long generation = reader.ReadInt64();
						int index = reader.ReadInt32();

						if (entityID == 0 || headID == 0 || generation < 0 || (index < 0 && !(index == -1 && headID == entityID)))
						{
							stream.SetLength(recordStart);
							return;
						}

						members[iteration] = new KeyValuePair<long, RailEntityAuthority>
						(
							entityID, new RailEntityAuthority( headID, generation,
							RailEntityAuthorityMode.Material, index)
						);
					}

					Records.Add(new JournalRecord(serial, members));
					completeLength = stream.Position;
				}

				if (stream.Length != completeLength)
				stream.SetLength(completeLength);
			}
			catch (Exception exception)
			{
				serverAPI.Logger.Error("[yangtransport] Could not read convoy topology recovery journal, resetting it.");
				serverAPI.Logger.Error(exception);
				ResetInvalidJournal("read failure");
			}
		}

		internal bool ReplayNewerThan(long savedSerial, Dictionary<long, RailEntityAuthority> authority, out long recoveredSerial)
		{
			recoveredSerial = savedSerial;
			bool replayed = false;

			Records.Sort(static (firstRecord, secondRecord) => firstRecord.Serial.CompareTo(secondRecord.Serial));
			for (int iteration = 0; iteration < Records.Count; iteration++)
			{
				JournalRecord record = Records[iteration];
				if (record.Serial <= recoveredSerial) continue;

				// The topology journal repairs membership inside an already-material generation.
				// It must never commit an otherwise-unsaved Material/Virtual handoff.
				// If the persisted global authority is Virtual/Deleted, or its generation differs, retain that older complete representation instead.
				bool canApply = true;
				for (int memberIndex = 0; memberIndex < record.Members.Length; memberIndex++)
				{
					KeyValuePair<long, RailEntityAuthority> pair = record.Members[memberIndex];
					if (authority.TryGetValue(pair.Key, out RailEntityAuthority current))
					{
						if (current.Mode != RailEntityAuthorityMode.Material || current.Generation != pair.Value.Generation) { canApply = false; break; }
					}

					// An untracked generation > 0 implies an authority handoff newer than the persisted global snapshot.
					// Roll it back rather than risking rejection of an older generation still stored in the chunk.
					else if (pair.Value.Generation != 0) { canApply = false; break; }
				}

				if (canApply)
				{
					for (int memberIndex = 0; memberIndex < record.Members.Length; memberIndex++)
					{
						KeyValuePair<long, RailEntityAuthority> pair = record.Members[memberIndex];
						authority[pair.Key] = pair.Value;
					}
				}

				// Whether applied or intentionally rolled back, this journal transaction has now been resolved against the persisted authority snapshot.
				recoveredSerial = record.Serial;
				replayed = true;
			}

			return replayed;
		}

		internal bool Append(long serial, IReadOnlyList<KeyValuePair<long, RailEntityAuthority>> members)
		{
			if (serial <= 0 || members == null || members.Count == 0) return false;

			long recordStart = -1;
			try
			{
				EnsureWriter();
				recordStart = AppendStream!.Position;
				AppendWriter!.Write(serial);
				AppendWriter.Write(members.Count);

				var stored = new KeyValuePair<long, RailEntityAuthority>[members.Count];
				for (int iteration = 0; iteration < members.Count; iteration++)
				{
					KeyValuePair<long, RailEntityAuthority> pair = members[iteration];
					RailEntityAuthority authority = pair.Value;
					AppendWriter.Write(pair.Key);
					AppendWriter.Write(authority.HeadID);
					AppendWriter.Write(authority.Generation);
					AppendWriter.Write(authority.ConvoyIndex);
					stored[iteration] = pair;
				}

				AppendWriter.Flush();
				AppendStream!.Flush(flushToDisk: false);
				Records.Add(new JournalRecord(serial, stored));
				return true;
			}
			catch (Exception exception)
			{
				if (recordStart >= 0 && AppendStream != null) { try { AppendStream.SetLength(recordStart); } catch { } }

				if (!WriteFailureLogged)
				{
					WriteFailureLogged = true;
					ServerAPI?.Logger.Error("[yangtransport] Failed to append convoy topology recovery journal.");
					ServerAPI?.Logger.Error(exception);
				}
				DisposeWriter();
				return false;
			}
		}

		internal void Checkpoint(long savedSerial)
		{
			if (Records.Count == 0) return;

			bool allSaved = true;
			for (int iteration = 0; iteration < Records.Count; iteration++)
			{
				if (Records[iteration].Serial > savedSerial) { allSaved = false; break; }
			}
			if (!allSaved) return;

			DisposeWriter();
			Records.Clear();

			if (FilePath == null || !File.Exists(FilePath)) return;
			try
			{
				File.Delete(FilePath);
			}
			catch (Exception exception)
			{
				ServerAPI?.Logger.Warning
				(
					"[yangtransport] Could not delete checkpointed convoy topology journal '{0}'. It may cause harmless replay checks on the next startup: {1}",
					Path.GetFileName(FilePath), exception.Message
				);
			}
		}

		public void Dispose() { DisposeWriter(); }

		private void EnsureWriter()
		{
			if (AppendWriter != null) return;
			if (FilePath == null) throw new InvalidOperationException("Convoy topology journal has not been opened for a world.");

			string? directory = Path.GetDirectoryName(FilePath);
			if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
			bool newFile = !File.Exists(FilePath) || new FileInfo(FilePath).Length == 0;

			AppendStream = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 4096, FileOptions.SequentialScan);
			AppendStream.Position = AppendStream.Length;
			AppendWriter = new BinaryWriter(AppendStream);

			if (!newFile) return;

			AppendWriter.Write(JournalFileSignature);
			AppendWriter.Write(SaveGameIdentifier);
			AppendWriter.Flush();
			AppendStream.Flush(flushToDisk: false);
		}

		private void ResetInvalidJournal(string reason)
		{
			DisposeWriter();
			Records.Clear();
			if (FilePath == null) return;

			try
			{
				if (File.Exists(FilePath)) File.Delete(FilePath);
				ServerAPI?.Logger.Warning ("[yangtransport] Reset convoy topology recovery journal '{0}' ({1}).", Path.GetFileName(FilePath), reason);
			}
			catch (Exception exception)
			{
				ServerAPI?.Logger.Error("[yangtransport] Could not reset invalid convoy topology recovery journal '{0}'.", Path.GetFileName(FilePath));
				ServerAPI?.Logger.Error(exception);
			}
		}

		private void DisposeWriter()
		{
			try { AppendWriter?.Dispose(); }
			finally { AppendWriter = null; AppendStream = null; }
		}

		private readonly record struct JournalRecord(long Serial, KeyValuePair<long, RailEntityAuthority>[] Members);
	}
}
