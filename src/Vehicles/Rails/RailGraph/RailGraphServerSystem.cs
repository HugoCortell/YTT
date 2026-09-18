using System;
using System.Collections.Generic;
using System.IO;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace YangTransport;

internal enum SignalAuthorityResult : byte
{
	Allowed = 0,
	WrongDirection = 1,
	OccupiedNextZone = 2,
	ChainDownstreamOccupied = 3,
	IncompleteRoute = 4
}

/// Server-owned, always-live rail graph with incremental track updates.
/// RailGraphLive.cs is the datastructure, this is the manager/operator. The rail graph is Marxist theory, this is Leninist praxis.
/// Generalized track blocks notify this system directly, block-change listeners feed the clearance layer only.
public sealed class RailGraphServerSystem : ModSystem
{
	private const int DebugGraphDefaultRadius		= 96;
	private const int DebugGraphMaxRadius			= 256;
	private const int DebugGraphMaxEdges			= 20_000;
	private const int DebugGraphMaxPoints			= 200_000;
	private const long DebugGraphRequestCooldownMS	= 1000;

	// Developer-only destructive rebuild bounds. Normal gameplay never rebuilds the authoritative graph from loaded chunks.
	private const int ManualRebuildDefaultRadius = 48;
	private const int ManualRebuildYRange = 6;

	private ICoreServerAPI ServerAPI = null!;
	private IServerNetworkChannel NetworkChannel = null!;
	private RailConvoySystem? ConvoySystem;
	private OffscreenConvoySimSystem? OffscreenSystem;

	private readonly RailGraphLive LiveGraph = new();
	private readonly RailClearanceLayer ClearanceLayer = new();
	private readonly RailGraphRecoveryJournal RecoveryJournal = new();

	private bool Loaded;
	private bool RuntimeReady;
	private readonly Dictionary<string, long> NextDebugGraphRequestByPlayer = new();

	private bool GraphDirty;
	private long ServerTickListenerID;
	private readonly HashSet<RailChunkColumnKey> RequestedRailSupportColumns = new();

	// Incremental signal topology and visuals
	// Occupancy zone id --> signals whose direct or chain authority depends on that zone.
	private readonly Dictionary<ulong, HashSet<RailGraphLive.EndpointKey>> ZoneToSignals = new();
	private readonly Dictionary<RailGraphLive.EndpointKey, SignalIndexEntry> SignalIndex = new();
	private readonly Dictionary<RailGraphLive.EndpointKey, HashSet<ulong>> ZonesBySignal = new();
	private readonly Dictionary<RailGraphLive.EndpointKey, ulong> SignalAuthorityRevision = new();
	private readonly Dictionary<SignalBlockKey, RailGraphLive.EndpointKey> SignalEndpointByBlock = new();
	private readonly HashSet<SignalBlockKey> LoadedSignalBlocks = new();
	private readonly HashSet<RailGraphLive.EndpointKey> SignalRefreshScratch = new();
	private readonly List<RailGraphLive.EndpointKey> SignalRefreshQueueScratch = new(64);
	private readonly HashSet<ChainWalkKey> ChainAuthorityVisitedScratch = new();
	private readonly HashSet<ChainWalkKey> ChainTopologyVisitedScratch = new();
	private readonly List<ChainWalkKey> ChainTopologyWorkScratch = new(64);
	private readonly HashSet<ChainWalkKey> ChainVisualActiveScratch = new();
	private readonly Dictionary<ChainWalkKey, ChainVisualResult> ChainVisualMemoScratch = new();
	private readonly List<ChainVisualFrame> ChainVisualFrameScratch = new(64);
	private ulong NextSignalAuthorityRevision = 1;

	// Canonical occupancy is owner --> exact edge footprint.
	// Per-zone ownership and the reverse edge index are deterministic projections maintained in the same server-thread transaction.
	private readonly Dictionary<long, OwnerOccupancyState> OccupancyByOwner = new();
	private readonly Dictionary<ulong, HashSet<long>> OwnersByOccupiedEdge = new();
	private readonly Dictionary<ulong, HashSet<long>> OwnersByZone = new();
	private readonly HashSet<ulong> OccupancyEdgeScratch = new();
	private readonly List<ulong> OccupancyEdgeRemovalScratch = new();
	private readonly Dictionary<long, HashSet<ulong>> TransientSignalReservationsByOwner = new();
	private readonly List<ulong> TransientSignalReservationRemovalScratch = new();
	private readonly HashSet<long> OccupancyOwnerScratch = new();
	private readonly HashSet<long> RemovedTrackOwnerScratch = new();
	private readonly HashSet<long> OccupancyRetainedOwnerScratch = new();
	private readonly HashSet<ulong> OccupancyChangedZoneScratch = new();
	private int OccupancyBatchDepth;

	// Loaded minecart route tapes are indexed separately from their shorter physical occupancy footprints.
	// This guarantees an edit in retained lookahead is marked before graph-version promotion can occur on the next movement tick.
	private readonly Dictionary<long, LoadedRouteInterest> LoadedRouteInterestByOwner = new();
	private readonly Dictionary<ulong, HashSet<long>> LoadedRouteOwnersByEdge = new();
	private readonly HashSet<long> LoadedRouteOwnerScratch = new();

	private ulong OccupancyChangeSerialNumber;

	public override double ExecuteOrder() => 0.09;
	public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

	internal RailGraphLive Graph => LiveGraph;
	internal bool IsRuntimeReady => Loaded && RuntimeReady;
	public int GraphBuildVersion => LiveGraph.BuildVersion;
	internal ulong OccupancyChangeSerial => OccupancyChangeSerialNumber;

	// Ready-only fetch of convoys occupying a given zone
	internal bool TryGetOccupancyZoneOwners(ulong zoneID, out IReadOnlyCollection<long> owners)
	{
		if (zoneID != 0 && OwnersByZone.TryGetValue(zoneID, out HashSet<long>? zoneOwners)) { owners = zoneOwners; return true; }
		owners = Array.Empty<long>(); return false;
	}

	internal event Action<RailGraphLive.EndpointKey>? SignalAuthorityChanged;
	internal event Action<ulong>? ClearanceEdgeResolved;

	private void OnClearanceEdgeResolved(ulong edgeHash) { ClearanceEdgeResolved?.Invoke(edgeHash); }

	internal bool IsEdgeClearanceBlockedOrDirty(ulong edgeHash) { return Loaded && ClearanceLayer.IsBlockedOrDirty(edgeHash); }

	internal bool IsEdgeClearanceBlockedForLoadedMovement(ulong edgeHash, ref int remainingBlockReads, ref int remainingRepairAttempts)
	{
		if (!Loaded) return false;
		return ClearanceLayer.IsBlockedForLoadedMovement(edgeHash, ref remainingBlockReads, ref remainingRepairAttempts);
	}


	internal void MeasureTrackClearanceNow(BlockPos sourcePosition, Block sourceBlock, TrackPieceSpec trackSpecification, List<BlockPos> blockers)
	{
		ClearanceLayer.MeasureTrackNow(sourcePosition, sourceBlock, trackSpecification, blockers);
	}

	public override void Start(ICoreAPI coreAPI) // We use Start instead of StartServerSide to avoid race conditions
	{
		ServerAPI = coreAPI as ICoreServerAPI;
		if (ServerAPI == null) return;

		NetworkChannel = ServerAPI.Network
			.RegisterChannel("yangtransport_railgraph")
			.RegisterMessageType<RailGraphRequest>()
			.RegisterMessageType<RailGraphResponse>();

		NetworkChannel.SetMessageHandler<RailGraphRequest>(OnRequest);

		ServerAPI.ChatCommands
			.Create("railgraphrebuild")
			.RequiresPrivilege(Privilege.controlserver)
			.WithDescription("Rebuild the YangTransport rail graph around the command caller.")
			.HandleWith(OnCommandRebuildLocalArea);

		ConvoySystem = ServerAPI.ModLoader.GetModSystem<RailConvoySystem>();
		OffscreenSystem = ServerAPI.ModLoader.GetModSystem<OffscreenConvoySimSystem>();
		ClearanceLayer.Start(ServerAPI, LiveGraph);
		ClearanceLayer.EdgeResolved += OnClearanceEdgeResolved;

		ServerAPI.Event.SaveGameLoaded += OnSaveGameLoaded;
		ServerAPI.Event.GameWorldSave += OnGameWorldSave;
		ServerAPI.Event.ServerRunPhase(EnumServerRunPhase.GameReady, RecoverJournaledRailOwners);
		ServerAPI.Event.DidPlaceBlock += OnDidPlaceBlock;
		ServerAPI.Event.DidBreakBlock += OnDidBreakBlock;
		ServerAPI.Event.ChunkColumnLoaded += OnChunkColumnLoaded;
		ServerAPI.Event.RegisterEventBusListener(OnExplosionEvent, priority: 0.5, filterByEventName: "onexplosion");

		ServerTickListenerID = ServerAPI.Event.RegisterGameTickListener(OnServerTick, 200);
	}

	public override void Dispose()
	{
		// Vintage Story disposes mod systems before issuing its final GameWorldSave event.
		// Flush while the graph, clearance layer, and savegame accessor are still alive.
		if (ServerAPI != null) SaveRailState(force: true);

		ClearanceLayer.EdgeResolved -= OnClearanceEdgeResolved;
		if (ServerAPI != null)
		{
			ServerAPI.Event.SaveGameLoaded -= OnSaveGameLoaded;

			// During shutdown, keep GameWorldSave subscribed until the engine's final soft-exit save.
			// HardExit emits no save event, so the journal survives. A runtime mod disposal is not a server shutdown and must unsubscribe.
			if (!ServerAPI.Server.IsShuttingDown) ServerAPI.Event.GameWorldSave -= OnGameWorldSave;
			ServerAPI.Event.DidPlaceBlock -= OnDidPlaceBlock;
			ServerAPI.Event.DidBreakBlock -= OnDidBreakBlock;
			ServerAPI.Event.ChunkColumnLoaded -= OnChunkColumnLoaded;
			ServerAPI.Event.UnregisterEventBusListener(OnExplosionEvent);
			if (ServerTickListenerID != 0) ServerAPI.Event.UnregisterGameTickListener(ServerTickListenerID);
		}

		RecoveryJournal.Dispose();
		base.Dispose();
	}

	private void OnChunkColumnLoaded(Vec2i chunkCoordinate, IWorldChunk[] worldChunks)
	{
		if (!Loaded) return;
		if (RequestedRailSupportColumns.Count > 0)
		{
			// The API event omits dimension. Clearing every matching request is safe the next exact-dimension availability check requeues any still-missing one.
			RequestedRailSupportColumns.RemoveWhere( key => key.X == chunkCoordinate.X && key.Z == chunkCoordinate.Y);
		}
		ClearanceLayer.NotifyChunkColumnLoaded(chunkCoordinate);
	}

	internal bool RequestIncompleteConvoyChunkHalo(EntityPos position)
	{
		if (!Loaded || position == null) return false;

		bool requestedAny = false;
		int chunkSize = GlobalConstants.ChunkSize;
		int centerChunkX = (int)Math.Floor(position.X / chunkSize);
		int centerChunkZ = (int)Math.Floor(position.Z / chunkSize);
		int blockY = (int)Math.Floor(position.Y);
		var probePosition = new BlockPos(0, blockY, 0, position.Dimension);

		// Expanding one column from every loaded member is enough to stream the next coupled member.
		// Repeating this as members load walks arbitrarily long consists without forcing a large square around the whole train.
		for (int chunkX = centerChunkX - 1; chunkX <= centerChunkX + 1; chunkX++)
		{
			for (int chunkZ = centerChunkZ - 1; chunkZ <= centerChunkZ + 1; chunkZ++)
			{
				probePosition.Set(chunkX * chunkSize, blockY, chunkZ * chunkSize);
				probePosition.dimension = position.Dimension;
				if (ServerAPI.World.BlockAccessor.GetChunkAtBlockPos(probePosition) != null) continue;

				requestedAny = true;
				RequestRailSupportColumn(position.Dimension, chunkX, chunkZ);
			}
		}

		return requestedAny;
	}

	private void RequestRailSupportColumn(int dimension, int chunkX, int chunkZ)
	{
		if (!RequestedRailSupportColumns.Add(new RailChunkColumnKey(dimension, chunkX, chunkZ))) return;
		if (dimension == 0) ServerAPI.WorldManager.LoadChunkColumnPriority(chunkX, chunkZ);
		else ServerAPI.WorldManager.LoadChunkColumnForDimension(chunkX, chunkZ, dimension);
	}

	private void OnDidPlaceBlock(IServerPlayer player, int oldBlockID, BlockSelection blockSelection, ItemStack withItemStack)
	{
		if (!Loaded || blockSelection?.Position == null) return;

		BlockPos position = blockSelection.Position;
		Block oldBlock = oldBlockID > 0 ? ServerAPI.World.GetBlock(oldBlockID) : null;
		Block newBlock = ServerAPI.World.BlockAccessor.GetBlock(position);
		ClearanceLayer.NotifyPotentialBlockChanged(position, oldBlock, newBlock);
	}

	private void OnDidBreakBlock(IServerPlayer player, int oldBlockID, BlockSelection blockSelection)
	{
		if (!Loaded || blockSelection?.Position == null) return;

		BlockPos position = blockSelection.Position;
		Block oldBlock = oldBlockID > 0 ? ServerAPI.World.GetBlock(oldBlockID) : null;
		Block newBlock = ServerAPI.World.BlockAccessor.GetBlock(position);
		ClearanceLayer.NotifyPotentialBlockChanged(position, oldBlock, newBlock);
	}

	private void OnExplosionEvent(string eventName, ref EnumHandling handling, IAttribute eventData)
	{
		if (!Loaded || eventData is not ITreeAttribute tree) return;

		BlockPos position = tree.GetBlockPos("pos");
		if (position == null) return;

		int radius = (int)Math.Ceiling(tree.GetDouble("destructionRadius", 0));
		ClearanceLayer.NotifyPotentialAreaChanged(position, Math.Max(1, radius));
	}

	public void NotifyBlocksMaybeChanged(BlockPos position, Block? oldBlock = null, Block? newBlock = null)
	{
		if (!Loaded || position == null) return;

		newBlock ??= ServerAPI.World.BlockAccessor.GetBlock(position);
		ClearanceLayer.NotifyPotentialBlockChanged(position, oldBlock, newBlock);
	}

	public void NotifyAreaMaybeChanged(BlockPos center, int radiusBlocks)
	{
		if (!Loaded || center == null) return;
		ClearanceLayer.NotifyPotentialAreaChanged(center, radiusBlocks);
	}

	public void NotifyBlockPlaced(Block placedBlock, BlockPos position) // Called directly by generalized track blocks.
	{
		if (!Loaded) return;

		RecoveryJournal.MarkSuspect(position);
		var change = new RailGraphChangeSet { OldBuildVersion = LiveGraph.BuildVersion };
		bool changed = LiveGraph.AddTrackBlock(placedBlock, position, change);
		if (!changed) return;

		FinalizeGraphChange(change);
		MarkGraphDirty();
	}

	public void NotifyBlockRemoved(Block removedBlock, BlockPos position) // Called directly by generalized track blocks.
	{
		if (!Loaded) return;

		RecoveryJournal.MarkSuspect(position);
		var change = new RailGraphChangeSet { OldBuildVersion = LiveGraph.BuildVersion };
		bool changed = LiveGraph.RemoveTrackBlock(removedBlock, position, change);
		if (!changed) return;

		FinalizeGraphChange(change, derailLoadedOwnersOnRemovedTrack: true);
		MarkGraphDirty();
	}

	/// Bulk replace helper for dynamic tracks that ExchangeBlock many variants. Does a remove + add for each replacement, then bumps version once if anything changed.
	public void NotifyBlocksReplaced(IReadOnlyList<RailBlockReplacement> replacements)
	{
		if (!Loaded) return;

		var change = new RailGraphChangeSet { OldBuildVersion = LiveGraph.BuildVersion };
		bool changedAny = false;
		for (int replacementIndex = 0; replacementIndex < replacements.Count; replacementIndex++)
		{
			var replacement = replacements[replacementIndex];
			RecoveryJournal.MarkSuspect(replacement.Position);
			if (LiveGraph.RemoveTrackBlock(replacement.OldBlock, replacement.Position, change)) changedAny = true;
			if (LiveGraph.AddTrackBlock(replacement.NewBlock, replacement.Position, change)) changedAny = true;
		}

		if (!changedAny) return;

		FinalizeGraphChange(change);
		MarkGraphDirty();
	}

	public readonly struct RailBlockReplacement
	{
		public readonly Block OldBlock;
		public readonly Block NewBlock;
		public readonly BlockPos Position;

		public RailBlockReplacement(Block oldBlock, Block newBlock, BlockPos position)
		{
			OldBlock = oldBlock;
			NewBlock = newBlock;
			Position = position;
		}
	}


	internal void MarkRailOwnerRecoverySuspect(BlockPos position)
	{
		if (!Loaded || position == null) return;
		RecoveryJournal.MarkSuspect(position);
	}

	private void RecoverJournaledRailOwners()
	{
		if (!Loaded || !RecoveryJournal.HasPending) return;

		var owners = new HashSet<RailOwnerKey>(RecoveryJournal.PendingOwners);
		var ownersByColumn = new Dictionary<long, List<RailOwnerKey>>();
		foreach (RailOwnerKey owner in owners)
		{
			int chunkX = FloorDivide(owner.X, GlobalConstants.ChunkSize);
			int chunkZ = FloorDivide(owner.Z, GlobalConstants.ChunkSize);
			long columnKey = ((long)chunkX << 32) ^ (uint)chunkZ;
			if (!ownersByColumn.TryGetValue(columnKey, out List<RailOwnerKey>? columnOwners))
			{
				columnOwners = new List<RailOwnerKey>();
				ownersByColumn[columnKey] = columnOwners;
			}
			columnOwners.Add(owner);
		}

		var physicalTracks = new List<(RailOwnerKey Owner, Block Block)>(owners.Count);

		foreach (List<RailOwnerKey> columnOwners in ownersByColumn.Values)
		{
			RailOwnerKey first = columnOwners[0];
			int chunkX = FloorDivide(first.X, GlobalConstants.ChunkSize);
			int chunkZ = FloorDivide(first.Z, GlobalConstants.ChunkSize);
			IServerChunk[]? chunks = ServerAPI.WorldManager.BlockingLoadChunkColumn(chunkX, chunkZ);

			try
			{
				if (chunks == null)
				{
					// A genuinely absent persisted column means these owners contain no saved track.
					// If its mapchunk exists, however, Vintage Story failed to load a complete column (for example a partial/corrupt column).
					// Never turn that read failure into "authoritative air" entries.
					if (ServerAPI.WorldManager.BlockingTestMapChunkExists(chunkX, chunkZ))
					{
						throw new InvalidDataException($"Rail crash recovery could not read persisted chunk column {chunkX}/{chunkZ}.");
					}
					continue;
				}

				for (int ownerIndex = 0; ownerIndex < columnOwners.Count; ownerIndex++)
				{
					RailOwnerKey owner = columnOwners[ownerIndex];
					int chunkY = FloorDivide(owner.Y, GlobalConstants.ChunkSize);
					if ((uint)chunkY >= (uint)chunks.Length || chunks[chunkY] == null)
					{
						throw new InvalidDataException($"Rail crash recovery column {chunkX}/{chunkZ} is missing chunk Y={chunkY}.");
					}

					var position = new BlockPos(owner.X, owner.Y, owner.Z);
					Block block = chunks[chunkY].GetLocalBlockAtBlockPos(ServerAPI.World, position);
					if (block != null && TrackSpecsDictionary.TryGet(block, out _)) { physicalTracks.Add((owner, block)); }
				}
			}
			finally
			{
				if (chunks != null) { for (int chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++) chunks[chunkIndex]?.Dispose(); }
			}
		}

		var change = new RailGraphChangeSet { OldBuildVersion = LiveGraph.BuildVersion };
		bool changed = LiveGraph.RemoveOwnerContributions(owners, change);

		for (int trackIndex = 0; trackIndex < physicalTracks.Count; trackIndex++)
		{
			(RailOwnerKey owner, Block block) = physicalTracks[trackIndex];
			var position = new BlockPos(owner.X, owner.Y, owner.Z);
			if (LiveGraph.AddTrackBlock(block, position, change)) changed = true;
		}

		if (changed)
		{
			FinalizeGraphChange(change);
			MarkGraphDirty();
		}

		ServerAPI.Logger.Warning
		(
			"[yangtransport] Rail crash recovery reconciled {0} owner(s) across {1} persisted chunk column(s); {2} owner(s) currently contain track.",
			owners.Count, ownersByColumn.Count, physicalTracks.Count
		);
	}

	private static int FloorDivide(int value, int divisor)
	{
		int quotient = value / divisor;
		int remainder = value % divisor;
		return remainder < 0 ? quotient - 1 : quotient;
	}

	private void OnSaveGameLoaded()
	{
		RecoveryJournal.OpenForWorld(ServerAPI);
		RequestedRailSupportColumns.Clear();
		LiveGraph.Clear();
		ClearanceLayer.ResetEmptyForLoadedGraph();

		Loaded = true;
		RuntimeReady = false;
		GraphDirty = false;

		byte[]? payload = ServerAPI.WorldManager.SaveGame.GetData(RailStateSerializer.SaveKey);
		if (payload is { Length: > 0 })
		{
			if (!RailStateSerializer.TryDeserialize(payload, LiveGraph, ClearanceLayer, out uint savedSpecificationsHash, out bool clearanceLoaded, out string error))
			{
				Loaded = false;
				ClearDynamicOccupancyForGraphReset();
				ServerAPI.Logger.Error("[yangtransport] Rail state could not be loaded ({0}). The railgraph was not rebuilt or overwritten.", error);
				return;
			}


			ServerAPI.Logger.Notification
			(
				"[yangtransport] Rail state loaded: nodes={0}, edges={1}, ver={2}.",
				LiveGraph.NodeCount, LiveGraph.ConnectionCount, LiveGraph.BuildVersion
			);

			if (TrackSpecsDictionary.IsCompiled)
			{
				uint currentSpecificationsHash = TrackSpecsDictionary.GetSpecsHash();
				if (savedSpecificationsHash != currentSpecificationsHash)
				{
					ServerAPI.Logger.Warning
					(
						"[yangtransport] Rail state was saved with TrackSpec hash {0}; current hash is {1}. Keeping persisted railgraph.",
						savedSpecificationsHash, currentSpecificationsHash
					);
				}
			}

			if (!clearanceLoaded)
			{
				ServerAPI.Logger.Warning("[yangtransport] Rail clearance state could not be loaded. The railgraph remains loaded.");
			}
		}
		else { ServerAPI.Logger.Notification("[yangtransport] Rail graph has no saved snapshot."); }

		// Occupancy belongs to live or virtual convoys and is reconstructed after all save-load handlers have completed. It is never persisted anonymously.
		ClearDynamicOccupancyForGraphReset();
		RebuildSignalIndexAll();
		RefreshAllLoadedSignalBlocks();
	}

	private void OnGameWorldSave() { if (SaveRailState()) RecoveryJournal.Checkpoint(); }

	private bool SaveRailState(bool force = false)
	{
		if (!Loaded) return false;
		if (!force && !GraphDirty && !ClearanceLayer.PersistenceDirty && !RecoveryJournal.HasPending) return true;

		try
		{
			byte[] payload = RailStateSerializer.Serialize(LiveGraph, ClearanceLayer);
			ServerAPI.WorldManager.SaveGame.StoreData(RailStateSerializer.SaveKey, payload);

			GraphDirty = false;
			ClearanceLayer.MarkPersistenceSaved();

			ServerAPI.Logger.Notification
			(
				"[yangtransport] Rail state saved: nodes={0}, edges={1}, ver={2}, bytes={3}.",
				LiveGraph.NodeCount, LiveGraph.ConnectionCount, LiveGraph.BuildVersion, payload.Length
			);
			return true;
		}
		catch (Exception exception)
		{
			ServerAPI.Logger.Error("[yangtransport] Failed to serialize rail state.");
			ServerAPI.Logger.Error(exception);
			return false;
		}
	}

	private void OnServerTick(float deltaTime)
	{
		if (!Loaded) return;

		if (!RuntimeReady)
		{
			// SaveGameLoaded handlers for entities and virtual convoys have all completed before the first game tick.
			// Reassert every canonical owner-edge footprint once, then publish readiness as a barrier. Signal entry is denied before this point.
			if (ConvoySystem?.ReseedLoadedConvoyRailSafetyState() == false) return;
			OffscreenSystem?.ReseedVirtualOccupancy();
			RuntimeReady = true;
			RefreshAllLoadedSignalBlocks();
		}

		ClearanceLayer.OnServerTick(ServerAPI.World.ElapsedMilliseconds);
	}

	private void MarkGraphDirty() { GraphDirty = true; }

	private void FinalizeGraphChange(RailGraphChangeSet change, long exemptRemovedTrackOwnerID = 0, bool derailLoadedOwnersOnRemovedTrack = false)
	{
		// The graph is mutated and committed synchronously on the server thread. Zone assignment, occupancy projection, signal dependencies,
		// and dependent systems are all repaired before the new build version becomes externally observable after this callback returns.
		LiveGraph.CommitOccupancyZones(change);
		LiveGraph.BumpVersion();
		change.NewBuildVersion = LiveGraph.BuildVersion;
		change.Canonicalize();

		OnGraphTopologyChanged(change, exemptRemovedTrackOwnerID, derailLoadedOwnersOnRemovedTrack);
	}

	private void OnGraphTopologyChanged(RailGraphChangeSet change, long exemptRemovedTrackOwnerID, bool derailLoadedOwnersOnRemovedTrack)
	{
		CollectAffectedOccupancyOwners(change);
		MarkAffectedLoadedRouteInterests(change);

		// Route replicas must learn that touched routes require validation before loaded owners rebuild their exact footprint. This callback does not publish authority.
		try { ServerAPI.ModLoader.GetModSystem<ConvoyRouteNetworkSystem>()?.OnRailGraphChanged(LiveGraph, change); }
		catch (Exception exception)
		{
			ServerAPI.Logger.Error("[YangTransport] Route-replica graph notification failed.");
			ServerAPI.Logger.Error(exception);
		}

		// Occupancy and signal dependencies are the safety-critical read model. Repair them completely before invoking cache-only consumers.
		// Occupancy notifications remain batched, so no signal can observe the zone table between removal and reprojection.
		BeginOccupancyBatch();
		try
		{
			if (derailLoadedOwnersOnRemovedTrack) { TransitionLoadedOwnersRemovedFromTrack(change, exemptRemovedTrackOwnerID); }

			ReprojectAffectedOccupancyAtomically(OccupancyOwnerScratch);
			foreach (long ownerID in OccupancyOwnerScratch) RepublishLoadedRailOccupancyOwner(ownerID, change);

			try { ServerAPI.ModLoader.GetModSystem<OffscreenConvoySimSystem>()?.OnRailGraphChanged(LiveGraph, change); }
			catch (Exception exception)
			{
				ServerAPI.Logger.Error("[YangTransport] Virtual convoy graph reconciliation failed.");
				ServerAPI.Logger.Error(exception);
			}

			RebuildAffectedSignalIndex(change);
		}
		finally
		{
			EndOccupancyBatch();
			OccupancyOwnerScratch.Clear();
		}

		// These systems are derived caches. A failure may delay their refresh, but cannot expose a clear signal or erase authoritative occupancy.
		try { ClearanceLayer.NotifyGraphChanged(change); }
		catch (Exception exception)
		{
			ServerAPI.Logger.Error("[YangTransport] Clearance graph notification failed.");
			ServerAPI.Logger.Error(exception);
		}

		try { ServerAPI.ModLoader.GetModSystem<RailTrainCollisionSystem>()?.OnRailGraphChanged(change); }
		catch (Exception exception)
		{
			ServerAPI.Logger.Error("[YangTransport] Collision graph notification failed.");
			ServerAPI.Logger.Error(exception);
		}

		try { ServerAPI.ModLoader.GetModSystem<RailAutomationPathingSystem>()?.OnRailGraphChanged(LiveGraph, change); }
		catch (Exception exception)
		{
			ServerAPI.Logger.Error("[YangTransport] Automation graph notification failed.");
			ServerAPI.Logger.Error(exception);
		}
	}

	private void CollectAffectedOccupancyOwners(RailGraphChangeSet change)
	{
		OccupancyOwnerScratch.Clear();
		CollectOwnersForEdges(change.RemovedEdges);
		CollectOwnersForEdges(change.ZoneReassignedEdges);
		CollectOwnersForEdges(change.TouchedEdges);
	}

	private void CollectOwnersForEdges(IReadOnlyList<ulong> edges)
	{
		for (int edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
		{
			if (!OwnersByOccupiedEdge.TryGetValue(edges[edgeIndex], out HashSet<long>? owners)) continue;
			foreach (long ownerID in owners) OccupancyOwnerScratch.Add(ownerID);
		}
	}

	internal void UpdateLoadedRouteInterest(long ownerID, ConvoyRoute? route)
	{
		if (!Loaded || ownerID == 0) return;
		if (route == null || route.Count == 0) { ReleaseLoadedRouteInterest(ownerID); return; }

		if
		(
			LoadedRouteInterestByOwner.TryGetValue(ownerID, out LoadedRouteInterest? existing) &&
			ReferenceEquals(existing.Route, route) && existing.TopologyRevision == route.TopologyRevision
		) { return; }

		RemoveLoadedRouteInterestIndex(ownerID, existing);
		var interest = existing ?? new LoadedRouteInterest();
		interest.Route = route;
		interest.TopologyRevision = route.TopologyRevision;
		route.CollectUniqueEdgeHashes(interest.Edges);
		LoadedRouteInterestByOwner[ownerID] = interest;

		foreach (ulong edgeHash in interest.Edges)
		{
			if (!LoadedRouteOwnersByEdge.TryGetValue(edgeHash, out HashSet<long>? owners))
			{
				owners = new HashSet<long>();
				LoadedRouteOwnersByEdge[edgeHash] = owners;
			}
			owners.Add(ownerID);
		}
	}

	internal void ReleaseLoadedRouteInterest(long ownerID)
	{
		if (ownerID == 0 || !LoadedRouteInterestByOwner.Remove(ownerID, out LoadedRouteInterest? interest)) return;
		RemoveLoadedRouteInterestIndex(ownerID, interest);
	}

	private void RemoveLoadedRouteInterestIndex(long ownerID, LoadedRouteInterest? interest)
	{
		if (interest == null) return;
		foreach (ulong edgeHash in interest.Edges)
		{
			if (!LoadedRouteOwnersByEdge.TryGetValue(edgeHash, out HashSet<long>? owners)) continue;
			owners.Remove(ownerID);
			if (owners.Count == 0) LoadedRouteOwnersByEdge.Remove(edgeHash);
		}
		interest.Edges.Clear();
	}

	private void MarkAffectedLoadedRouteInterests(RailGraphChangeSet change)
	{
		LoadedRouteOwnerScratch.Clear();
		if (change.GlobalInvalidation) { foreach (long ownerID in LoadedRouteInterestByOwner.Keys) LoadedRouteOwnerScratch.Add(ownerID); }
		else
		{
			for (int edgeIndex = 0; edgeIndex < change.TouchedEdges.Count; edgeIndex++)
			{
				if (!LoadedRouteOwnersByEdge.TryGetValue(change.TouchedEdges[edgeIndex], out HashSet<long>? owners)) continue;
				foreach (long ownerID in owners) LoadedRouteOwnerScratch.Add(ownerID);
			}
		}

		foreach (long ownerID in LoadedRouteOwnerScratch)
		{
			if (LoadedRouteInterestByOwner.TryGetValue(ownerID, out LoadedRouteInterest? interest)) interest.Route.RequireAuthoritativeValidation();
		}
		LoadedRouteOwnerScratch.Clear();
	}

	internal void RepublishLoadedRailOccupancyOwner(long ownerID) { RepublishLoadedRailOccupancyOwner(ownerID, null); }

	private void RepublishLoadedRailOccupancyOwner(long ownerID, RailGraphChangeSet? change)
	{
		if (!Loaded || ownerID == 0) return;

		ConvoySystem ??= ServerAPI.ModLoader.GetModSystem<RailConvoySystem>();
		if (ConvoySystem?.TryGetLoadedVehicle(ownerID, out IRailwayConvoyVehicle vehicle) != true) return; // Virtual owners are reconciled by OffscreenConvoySimSystem.

		Entity entity = vehicle.Entity;
		bool publishedSuccessfully;
		if (entity is EntityMinecart minecart)
		{
			if (change != null) minecart.ServerPromoteRouteAcrossGraphChange(LiveGraph, change);
			publishedSuccessfully = minecart.TryPublishRailOccupancyFootprint(this, LiveGraph);
		}
		else if (entity is EntityStandardGaugeLocomotive locomotive) { publishedSuccessfully = locomotive.TryPublishRailOccupancyFootprint(this, LiveGraph); }
		else { return; }

		if (!publishedSuccessfully)
		{
			// Keep the last successfully projected footprint. A failed route/body rebuild must never make a physically present convoy disappear from signal authority.
			// Removed edges were already stripped by ReprojectOwnerOccupancy, movement will reject the stale route at its next exact transition.
			return;
		}
	}

	private void TransitionLoadedOwnersRemovedFromTrack(RailGraphChangeSet change, long exemptOwnerID)
	{
		if (change.RemovedEdges.Count == 0) return;

		RemovedTrackOwnerScratch.Clear();
		CollectOwnersForEdgesInto(change.RemovedEdges, RemovedTrackOwnerScratch);
		if (RemovedTrackOwnerScratch.Count == 0) return;

		ConvoySystem ??= ServerAPI.ModLoader.GetModSystem<RailConvoySystem>();
		foreach (long ownerID in RemovedTrackOwnerScratch)
		{
			if (ownerID == exemptOwnerID) continue;
			if (ConvoySystem?.TryGetLoadedVehicle(ownerID, out IRailwayConvoyVehicle vehicle) != true) continue;
			if (vehicle.Entity is EntityMinecart minecart) { minecart.ServerDerailForInvalidTrack(); }
			else if (vehicle.Entity is EntityStandardGaugeLocomotive locomotive) { locomotive.ServerDerailForInvalidTrack(); }
		}
		RemovedTrackOwnerScratch.Clear();
	}

	private void CollectOwnersForEdgesInto(IReadOnlyList<ulong> edges, HashSet<long> destination)
	{
		for (int edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
		{
			if (!OwnersByOccupiedEdge.TryGetValue(edges[edgeIndex], out HashSet<long>? owners)) continue;
			foreach (long ownerID in owners) destination.Add(ownerID);
		}
	}

	private void ReprojectAffectedOccupancyAtomically(IEnumerable<long> affectedOwners)
	{
		var ownerSet = new HashSet<long>();
		var projections = new List<OwnerOccupancyProjection>();
		var affectedZones = new HashSet<ulong>();

		foreach (long ownerID in affectedOwners)
		{
			if (!ownerSet.Add(ownerID) || !OccupancyByOwner.TryGetValue(ownerID, out OwnerOccupancyState? state)) { continue; }

			var projection = new OwnerOccupancyProjection(ownerID, state);
			foreach (ulong zoneID in state.EdgeCountByZone.Keys) affectedZones.Add(zoneID);

			foreach (ulong edgeHash in state.Edges)
			{
				if (!LiveGraph.TryGetOccupancyZoneID(edgeHash, out ulong zoneID)) { projection.RemovedEdges.Add(edgeHash); continue; }

				projection.EdgeCountByZone.TryGetValue(zoneID, out int count);
				projection.EdgeCountByZone[zoneID] = count + 1;
				affectedZones.Add(zoneID);
			}

			state.EdgeCountByZone.EnsureCapacity(projection.EdgeCountByZone.Count);
			projections.Add(projection);
		}

		if (projections.Count == 0) return;

		var zoneReplacements = new Dictionary<ulong, ZoneOccupancyReplacement>(affectedZones.Count);
		OwnersByZone.EnsureCapacity(OwnersByZone.Count + affectedZones.Count);
		foreach (ulong zoneID in affectedZones)
		{
			var serverOwners = OwnersByZone.TryGetValue(zoneID, out HashSet<long>? current) ? new HashSet<long>(current) : new HashSet<long>();
			serverOwners.ExceptWith(ownerSet);
			zoneReplacements[zoneID] = new ZoneOccupancyReplacement(zoneID, current, serverOwners);
		}

		for (int projectionIndex = 0; projectionIndex < projections.Count; projectionIndex++)
		{
			OwnerOccupancyProjection projection = projections[projectionIndex];
			foreach (ulong zoneID in projection.EdgeCountByZone.Keys) { zoneReplacements[zoneID].ServerOwners.Add(projection.OwnerID); }
		}
		foreach (ZoneOccupancyReplacement replacement in zoneReplacements.Values)
		{
			replacement.GraphOwners = replacement.ServerOwners.Count == 0 ? null : new HashSet<long>(replacement.ServerOwners);
		}

		// All allocations and validation are complete above. The following assignments form the single authoritative occupancy commit
		// and cannot expose a partially rebuilt read model because server movement cannot interleave on the game thread.
		for (int projectionIndex = 0; projectionIndex < projections.Count; projectionIndex++)
		{
			OwnerOccupancyProjection projection = projections[projectionIndex];
			OwnerOccupancyState state = projection.State;
			for (int removedEdgeIndex = 0; removedEdgeIndex < projection.RemovedEdges.Count; removedEdgeIndex++)
			{
				ulong edgeHash = projection.RemovedEdges[removedEdgeIndex];
				state.Edges.Remove(edgeHash);
				RemoveOwnerFromOccupiedEdge(edgeHash, projection.OwnerID);
			}

			state.EdgeCountByZone.Clear();
			foreach (KeyValuePair<ulong, int> pair in projection.EdgeCountByZone) state.EdgeCountByZone[pair.Key] = pair.Value;

			if (state.Edges.Count == 0) OccupancyByOwner.Remove(projection.OwnerID);
		}

		foreach (ZoneOccupancyReplacement replacement in zoneReplacements.Values)
		{
			bool changed = replacement.PreviousOwners == null ? replacement.ServerOwners.Count != 0 : !replacement.PreviousOwners.SetEquals(replacement.ServerOwners);

			if (replacement.ServerOwners.Count == 0)	OwnersByZone.Remove(replacement.ZoneID);
			else										OwnersByZone[replacement.ZoneID] = replacement.ServerOwners;

			// RailGraphLive keeps only a debug/network mirror. Give it a separate prepared set so neither side can mutate the other's authoritative collection.
			LiveGraph.ReplaceZoneOccupantsSnapshot(replacement.ZoneID, replacement.GraphOwners);
			if (changed) QueueOccupancyZoneChanged(replacement.ZoneID);
		}
	}


	private void ClearDynamicOccupancyForGraphReset()
	{
		LiveGraph.ClearOccupiedZones();
		OwnersByZone.Clear();
		OccupancyByOwner.Clear();
		OwnersByOccupiedEdge.Clear();
		TransientSignalReservationsByOwner.Clear();
		TransientSignalReservationRemovalScratch.Clear();
		LoadedRouteInterestByOwner.Clear();
		LoadedRouteOwnersByEdge.Clear();
		LoadedRouteOwnerScratch.Clear();
		OccupancyChangedZoneScratch.Clear();
		unchecked { OccupancyChangeSerialNumber++; }
	}



	internal bool RebuildLocalArea(BlockPos center)
	{
		if (!Loaded || center == null) return false;

		var change = new RailGraphChangeSet { OldBuildVersion = LiveGraph.BuildVersion };
		bool changed = LiveGraph.RebuildArea
		(
			ServerAPI.World.BlockAccessor,
			center,
			ManualRebuildDefaultRadius,
			ManualRebuildYRange,
			change
		);

		if (!changed) return false;

		FinalizeGraphChange(change);
		MarkGraphDirty();
		RuntimeReady = false;
		return true;
	}

	private TextCommandResult OnCommandRebuildLocalArea(TextCommandCallingArgs commandArguments)
	{
		if (!Loaded) return TextCommandResult.Error("The rail graph is not loaded!");

		Vec3d? callerPosition = commandArguments.Caller.Pos;
		if (callerPosition == null) return TextCommandResult.Error("The command caller has no position.");

		BlockPos center = callerPosition.AsBlockPos;
		center.dimension = commandArguments.Caller.Entity?.Pos.Dimension ?? 0;

		bool changed = RebuildLocalArea(center);
		return TextCommandResult.Success(changed ? "Rebuilt the local rail graph." : "The rail graph is already up to date in this area.");
	}

	private RailGraphDebugEdgeOccupancy[] BuildDebugEdgeOccupancy(List<ulong> edgeHashes)
	{
		if (edgeHashes.Count == 0) return Array.Empty<RailGraphDebugEdgeOccupancy>();

		var records = new List<RailGraphDebugEdgeOccupancy>(edgeHashes.Count);
		for (int edgeIndex = 0; edgeIndex < edgeHashes.Count; edgeIndex++)
		{
			ulong edgeHash = edgeHashes[edgeIndex];
			if (!LiveGraph.TryGetOccupancyZoneID(edgeHash, out ulong zoneID) || zoneID == 0) continue;

			long[] ownerIDs = Array.Empty<long>();
			bool occupied = OwnersByZone.TryGetValue(zoneID, out HashSet<long>? owners) && owners.Count > 0;
			if (occupied)
			{
				ownerIDs = new long[owners!.Count];
				owners.CopyTo(ownerIDs);
				Array.Sort(ownerIDs);
			}

			records.Add(new RailGraphDebugEdgeOccupancy
			{
				EdgeHash = edgeHash,
				ZoneID = zoneID,
				Occupied = occupied,
				OwnerIDs = ownerIDs
			});
		}

		return records.Count == 0 ? Array.Empty<RailGraphDebugEdgeOccupancy>() : records.ToArray();
	}

	private void OnRequest(IServerPlayer requestingPlayer, RailGraphRequest request)
	{
		if (!Loaded) return;

		var center = new BlockPos(request.CenterX, request.CenterY, request.CenterZ, request.Dimension);

		if (request.ForceRebuild)
		{
			if (!requestingPlayer.HasPrivilege(Privilege.controlserver)) return;
			RebuildLocalArea(center);
			return;
		}

		if (request.LastBuildVersion == LiveGraph.BuildVersion && !request.Detailed) return;

		byte[] graphData;
		bool truncated = false;
		int serializedEdges = 0;
		int serializedPoints = 0;
		bool debugOccupancyIncluded = request.Detailed && request.IncludeDebugOccupancy;
		RailGraphDebugEdgeOccupancy[] debugEdgeOccupancy = Array.Empty<RailGraphDebugEdgeOccupancy>();
		if (request.Detailed)
		{
			long nowMS = ServerAPI.World.ElapsedMilliseconds;
			string playerKey = requestingPlayer.PlayerUID ?? "";
			if (NextDebugGraphRequestByPlayer.TryGetValue(playerKey, out long nextAllowed) && nowMS < nextAllowed) return;
			NextDebugGraphRequestByPlayer[playerKey] = nowMS + DebugGraphRequestCooldownMS;

			int radius = GameMath.Clamp(request.Radius <= 0 ? DebugGraphDefaultRadius : request.Radius, 1, DebugGraphMaxRadius);
			List<ulong>? selectedEdgeHashes = debugOccupancyIncluded ? new List<ulong>(4096) : null;
			graphData = LiveGraph.SerializeArea
			(
				center,
				radius,
				includePolylines: true,
				includeOccupancy: true,
				DebugGraphMaxEdges,
				DebugGraphMaxPoints,
				selectedEdgeHashes,
				out truncated,
				out serializedEdges,
				out serializedPoints
			);
			if (selectedEdgeHashes != null) debugEdgeOccupancy = BuildDebugEdgeOccupancy(selectedEdgeHashes);
		}
		else
		{
			graphData = LiveGraph.Serialize(includePolylines: false, includeOccupancy: true);
			serializedEdges = LiveGraph.ConnectionCount;
		}

		NetworkChannel.SendPacket(new RailGraphResponse
		{
			BuildVersion = LiveGraph.BuildVersion,
			Detailed = request.Detailed,
			GraphData = graphData,
			ClearanceData = ClearanceLayer.SerializeForClient(),
			Truncated = truncated,
			SerializedEdges = serializedEdges,
			SerializedPoints = serializedPoints,
			DebugOccupancyIncluded = debugOccupancyIncluded,
			DebugOccupancySerial = debugOccupancyIncluded ? OccupancyChangeSerialNumber : 0,
			DebugEdgeOccupancy = debugEdgeOccupancy
		}, requestingPlayer);
	}


	/// Pure signal-boundary authority check for automated trains. Chain signals evaluate the chosen route through the throat,
	/// including cascading chain signals, so a train waits before the throat whenever its planned exit path is blocked.
	internal SignalAuthorityResult CheckSignalSectionAuthority
	(
		long ownerID,
		RailGraphLive.EndpointKey signalEndpoint,
		ulong fromEdgeHash,
		ulong toEdgeHash,
		RailExactTurnPlan? exactTurns
	)
	{
		if (!RuntimeReady) return SignalAuthorityResult.IncompleteRoute;

		ulong authorityRevision = GetSignalAuthorityRevision(signalEndpoint);
		if (exactTurns != null &&
			exactTurns.TryGetCachedAuthority(ownerID, signalEndpoint, fromEdgeHash, toEdgeHash, authorityRevision, out SignalAuthorityResult cached))
		{
			return cached;
		}

		SignalAuthorityResult result = ComputeSignalSectionAuthority(ownerID, signalEndpoint, fromEdgeHash, toEdgeHash, exactTurns);
		exactTurns?.StoreCachedAuthority(ownerID, signalEndpoint, fromEdgeHash, toEdgeHash, authorityRevision, result);
		return result;
	}

	/// Performs the live authority check and reserves the selected destination edge in the same server-thread operation.
	/// The reservation is ordinary owner-edge occupancy, so the following footprint publication naturally retains or removes it.
	internal SignalAuthorityResult CheckAndReserveSignalSectionAuthority
	(
		long ownerID, RailGraphLive.EndpointKey signalEndpoint,
		ulong fromEdgeHash, ulong toEdgeHash, RailExactTurnPlan? exactTurns
	)
	{
		SignalAuthorityResult result = CheckSignalSectionAuthority(ownerID, signalEndpoint, fromEdgeHash, toEdgeHash, exactTurns);
		if (result != SignalAuthorityResult.Allowed || ownerID == 0) return result;
		if (!LiveGraph.TryGetOccupancyZoneID(toEdgeHash, out ulong zoneID) || zoneID == 0) return SignalAuthorityResult.IncompleteRoute;

		BeginOccupancyBatch();
		try
		{
			if (!OccupancyByOwner.TryGetValue(ownerID, out OwnerOccupancyState? state))
			{
				state = new OwnerOccupancyState();
				OccupancyByOwner[ownerID] = state;
			}

			bool alreadyOwned = state.Edges.Contains(toEdgeHash);
			AddOwnerEdge(state, ownerID, toEdgeHash);
			if (!alreadyOwned && state.Edges.Contains(toEdgeHash)) TrackTransientSignalReservation(ownerID, toEdgeHash);
		}
		finally { EndOccupancyBatch(); }
		return SignalAuthorityResult.Allowed;
	}

	private SignalAuthorityResult ComputeSignalSectionAuthority
	(
		long ownerID, RailGraphLive.EndpointKey signalEndpoint,
		ulong fromEdgeHash, ulong toEdgeHash, RailExactTurnPlan? exactTurns
	)
	{
		if (!Loaded || toEdgeHash == 0 || !LiveGraph.EdgeTouchesEndpoint(toEdgeHash, signalEndpoint)) return SignalAuthorityResult.IncompleteRoute;
		if (!IsSignalEntryAllowed(signalEndpoint, fromEdgeHash)) return SignalAuthorityResult.WrongDirection;
		if (!LiveGraph.TryGetOccupancyZoneID(toEdgeHash, out ulong nextZoneID)) return SignalAuthorityResult.IncompleteRoute;

		if (fromEdgeHash != 0 && !LiveGraph.TryGetOccupancyZoneID(fromEdgeHash, out _)) return SignalAuthorityResult.IncompleteRoute;

		if (LiveGraph.IsChainSignal(signalEndpoint))
		{
			if (exactTurns == null) return SignalAuthorityResult.IncompleteRoute;
			return CheckChainRouteAuthority(ownerID, signalEndpoint, toEdgeHash, exactTurns);
		}

		if (IsZoneOccupiedByOther(nextZoneID, ownerID)) return SignalAuthorityResult.OccupiedNextZone;

		return SignalAuthorityResult.Allowed;
	}

	private SignalAuthorityResult CheckChainRouteAuthority(long ownerID, RailGraphLive.EndpointKey chainEndpoint, ulong firstEdgeHash, RailExactTurnPlan exactTurns)
	{
		RailGraphLive.EndpointKey endpoint = chainEndpoint;
		ulong edgeHash = firstEdgeHash;
		ChainAuthorityVisitedScratch.Clear();

		try
		{
			while (true)
			{
				var walkState = new ChainWalkKey(endpoint, edgeHash);
				if (!ChainAuthorityVisitedScratch.Add(walkState))													return SignalAuthorityResult.IncompleteRoute;
				if (!LiveGraph.TryGetOccupancyZoneID(edgeHash, out ulong zoneID))									return SignalAuthorityResult.IncompleteRoute;
				if (IsZoneOccupiedByOther(zoneID, ownerID))															return SignalAuthorityResult.ChainDownstreamOccupied;
				if (!LiveGraph.TryGetOtherEndpoint(edgeHash, endpoint, out RailGraphLive.EndpointKey nextEndpoint))	return SignalAuthorityResult.IncompleteRoute;
				if (exactTurns.ShouldStopAt(edgeHash, nextEndpoint))												return SignalAuthorityResult.Allowed;

				if (LiveGraph.IsSignalEndpoint(nextEndpoint))
				{
					if (nextEndpoint.Equals(chainEndpoint))															return SignalAuthorityResult.IncompleteRoute;
					if (!TryGetPlannedContinuation(exactTurns, edgeHash, nextEndpoint, out ulong beyondSignalEdge))	return SignalAuthorityResult.IncompleteRoute;
					if (!IsSignalEntryAllowed(nextEndpoint, edgeHash))												return SignalAuthorityResult.ChainDownstreamOccupied;
					if (!LiveGraph.TryGetOccupancyZoneID(beyondSignalEdge, out ulong beyondZoneID))					return SignalAuthorityResult.IncompleteRoute;
					if (IsZoneOccupiedByOther(beyondZoneID, ownerID))												return SignalAuthorityResult.ChainDownstreamOccupied;
					if (!LiveGraph.IsChainSignal(nextEndpoint))														return SignalAuthorityResult.Allowed;

					endpoint = nextEndpoint;
					edgeHash = beyondSignalEdge;
					continue;
				}

				if 
				(
					!TryGetPlannedContinuation(exactTurns, edgeHash, nextEndpoint, out ulong nextEdgeHash) &&
					!TryGetStraightContinuation(edgeHash, nextEndpoint, out nextEdgeHash)
				) { return SignalAuthorityResult.IncompleteRoute; }

				endpoint = nextEndpoint;
				edgeHash = nextEdgeHash;
			}
		}
		finally { ChainAuthorityVisitedScratch.Clear(); }
	}

	private bool TryGetPlannedContinuation(RailExactTurnPlan exactTurns, ulong fromEdgeHash, RailGraphLive.EndpointKey atEndpoint, out ulong nextEdgeHash)
	{
		nextEdgeHash = 0;
		if (!exactTurns.TryGet(fromEdgeHash, atEndpoint, out ulong forcedEdge) || forcedEdge == 0 || forcedEdge == fromEdgeHash) return false;
		if (!LiveGraph.EdgeTouchesEndpoint(forcedEdge, atEndpoint)) return false;

		nextEdgeHash = forcedEdge; return true;
	}

	private bool TryGetStraightContinuation(ulong fromEdgeHash, RailGraphLive.EndpointKey atEndpoint, out ulong nextEdgeHash)
	{
		nextEdgeHash = 0;
		if (!LiveGraph.TryGetIncidentEdges(atEndpoint, out List<ulong> incident) || incident.Count != 2) return false;

		if		(incident[0] == fromEdgeHash) nextEdgeHash = incident[1];
		else if	(incident[1] == fromEdgeHash) nextEdgeHash = incident[0];

		return nextEdgeHash != 0 && RailTransitionRules.IsDirectedTransitionAllowedSameGauge(LiveGraph, atEndpoint, atEndpoint.Gauge, fromEdgeHash, nextEdgeHash);
	}

	private bool IsSignalEntryAllowed(RailGraphLive.EndpointKey signalEndpoint, ulong fromEdgeHash)
	{
		return LiveGraph.IsOneWayCrossingAllowed(signalEndpoint, fromEdgeHash);
	}

	internal readonly struct OwnerEdgeFootprint
	{
		public readonly long OwnerID;
		public readonly IReadOnlyCollection<ulong> EdgeHashes;

		public OwnerEdgeFootprint(long ownerID, IReadOnlyCollection<ulong> edgeHashes)
		{
			OwnerID = ownerID;
			EdgeHashes = edgeHashes;
		}
	}

	private sealed class LoadedRouteInterest
	{
		internal ConvoyRoute Route = null!;
		internal uint TopologyRevision = uint.MaxValue;
		internal readonly HashSet<ulong> Edges = new();
	}

	private sealed class OwnerOccupancyProjection
	{
		internal readonly long OwnerID;
		internal readonly OwnerOccupancyState State;
		internal readonly Dictionary<ulong, int> EdgeCountByZone = new(4);
		internal readonly List<ulong> RemovedEdges = new(2);

		internal OwnerOccupancyProjection(long ownerID, OwnerOccupancyState state)
		{
			OwnerID = ownerID;
			State = state;
		}
	}

	private sealed class ZoneOccupancyReplacement
	{
		internal readonly ulong ZoneID;
		internal readonly HashSet<long>? PreviousOwners;
		internal readonly HashSet<long> ServerOwners;
		internal HashSet<long>? GraphOwners;

		internal ZoneOccupancyReplacement(ulong zoneID, HashSet<long>? previousOwners, HashSet<long> serverOwners)
		{
			ZoneID = zoneID;
			PreviousOwners = previousOwners;
			ServerOwners = serverOwners;
		}
	}

	private sealed class OwnerOccupancyState
	{
		internal readonly HashSet<ulong> Edges = new();
		internal readonly Dictionary<ulong, int> EdgeCountByZone = new(4);
	}

	internal void ReleaseOccupancyOwner(long ownerID)
	{
		if (!Loaded || ownerID == 0) return;
		BeginOccupancyBatch();
		try { ReleaseOwnerOccupancy(ownerID); }
		finally { EndOccupancyBatch(); }
	}

	internal void ReleaseTransientSignalReservations(long ownerID)
	{
		if (!Loaded || ownerID == 0) return;
		BeginOccupancyBatch();
		try { ReleaseTransientSignalReservationsNoBatch(ownerID); }
		finally { EndOccupancyBatch(); }
	}

	internal bool ReportOwnerEdgeFootprint(long ownerID, IReadOnlyCollection<ulong> edgeHashes)
	{
		if (!Loaded || ownerID == 0 || !ValidateOwnerEdgeFootprint(edgeHashes)) return false;
		BeginOccupancyBatch();
		try
		{
			SetOwnerEdgeFootprint(ownerID, edgeHashes);
			ClearTransientSignalReservations(ownerID);
			return true;
		}
		finally { EndOccupancyBatch(); }
	}

	internal bool ReplaceOwnerEdgeFootprintsEager(IReadOnlyList<OwnerEdgeFootprint> desiredOwners, IReadOnlyCollection<long> oldOwners)
	{
		if (!Loaded) return false;
		if (desiredOwners != null)
		{
			for (int ownerIndex = 0; ownerIndex < desiredOwners.Count; ownerIndex++)
			{
				OwnerEdgeFootprint desired = desiredOwners[ownerIndex];
				if (desired.OwnerID == 0 || !ValidateOwnerEdgeFootprint(desired.EdgeHashes)) return false;
			}
		}

		BeginOccupancyBatch();
		try
		{
			OccupancyRetainedOwnerScratch.Clear();
			if (desiredOwners != null)
			{
				for (int ownerIndex = 0; ownerIndex < desiredOwners.Count; ownerIndex++)
				{
					long ownerID = desiredOwners[ownerIndex].OwnerID;
					if (ownerID == 0) continue;
					OccupancyRetainedOwnerScratch.Add(ownerID);
					SetOwnerEdgeFootprint(ownerID, desiredOwners[ownerIndex].EdgeHashes);
					ClearTransientSignalReservations(ownerID);
				}
			}

			if (oldOwners != null)
			{
				foreach (long ownerID in oldOwners) { if (ownerID != 0 && !OccupancyRetainedOwnerScratch.Contains(ownerID)) ReleaseOwnerOccupancy(ownerID); }
			}

			return true;
		}
		finally
		{
			OccupancyRetainedOwnerScratch.Clear();
			EndOccupancyBatch();
		}
	}

	private bool ValidateOwnerEdgeFootprint(IReadOnlyCollection<ulong>? edgeHashes)
	{
		if (edgeHashes == null) return true;
		foreach (ulong edgeHash in edgeHashes)
		{
			if (edgeHash == 0 || !LiveGraph.TryGetOccupancyZoneID(edgeHash, out ulong zoneID) || zoneID == 0) return false;
		}
		return true;
	}

	private void SetOwnerEdgeFootprint(long ownerID, IReadOnlyCollection<ulong> edgeHashes)
	{
		OccupancyEdgeScratch.Clear();
		if (edgeHashes != null) { foreach (ulong edgeHash in edgeHashes) OccupancyEdgeScratch.Add(edgeHash); }

		if (!OccupancyByOwner.TryGetValue(ownerID, out OwnerOccupancyState? state))
		{
			if (OccupancyEdgeScratch.Count == 0) return;
			state = new OwnerOccupancyState();
			OccupancyByOwner[ownerID] = state;
		}

		OccupancyEdgeRemovalScratch.Clear();
		foreach (ulong edgeHash in state.Edges) { if (!OccupancyEdgeScratch.Contains(edgeHash)) OccupancyEdgeRemovalScratch.Add(edgeHash); }
		for (int edgeIndex = 0; edgeIndex < OccupancyEdgeRemovalScratch.Count; edgeIndex++) RemoveOwnerEdge(state, ownerID, OccupancyEdgeRemovalScratch[edgeIndex]);

		foreach (ulong edgeHash in OccupancyEdgeScratch) { if (!state.Edges.Contains(edgeHash)) AddOwnerEdge(state, ownerID, edgeHash); }

		if (state.Edges.Count == 0) OccupancyByOwner.Remove(ownerID);
		OccupancyEdgeScratch.Clear();
		OccupancyEdgeRemovalScratch.Clear();
	}

	private void TrackTransientSignalReservation(long ownerID, ulong edgeHash)
	{
		if (!TransientSignalReservationsByOwner.TryGetValue(ownerID, out HashSet<ulong>? edges))
		{
			edges = new HashSet<ulong>();
			TransientSignalReservationsByOwner[ownerID] = edges;
		}
		edges.Add(edgeHash);
	}

	private void ClearTransientSignalReservations(long ownerID)
	{
		if (TransientSignalReservationsByOwner.Remove(ownerID, out HashSet<ulong>? edges)) edges.Clear();
	}

	private void ReleaseTransientSignalReservationsNoBatch(long ownerID)
	{
		if (!TransientSignalReservationsByOwner.Remove(ownerID, out HashSet<ulong>? reservations)) return;
		if (!OccupancyByOwner.TryGetValue(ownerID, out OwnerOccupancyState? state)) { reservations.Clear(); return; }

		TransientSignalReservationRemovalScratch.Clear();
		foreach (ulong edgeHash in reservations) { if (state.Edges.Contains(edgeHash)) TransientSignalReservationRemovalScratch.Add(edgeHash); }
		for (int edgeIndex = 0; edgeIndex < TransientSignalReservationRemovalScratch.Count; edgeIndex++)
		{
			RemoveOwnerEdge(state, ownerID, TransientSignalReservationRemovalScratch[edgeIndex]);
		}

		if (state.Edges.Count == 0) OccupancyByOwner.Remove(ownerID);
		TransientSignalReservationRemovalScratch.Clear();
		reservations.Clear();
	}

	private void AddOwnerEdge(OwnerOccupancyState state, long ownerID, ulong edgeHash)
	{
		if (!LiveGraph.TryGetOccupancyZoneID(edgeHash, out ulong zoneID) || !state.Edges.Add(edgeHash)) return;

		if (!OwnersByOccupiedEdge.TryGetValue(edgeHash, out HashSet<long>? owners))
		{
			owners = new HashSet<long>();
			OwnersByOccupiedEdge[edgeHash] = owners;
		}
		owners.Add(ownerID);

		state.EdgeCountByZone.TryGetValue(zoneID, out int count);
		state.EdgeCountByZone[zoneID] = count + 1;
		if (count == 0 && AddZoneOccupant(zoneID, ownerID)) QueueOccupancyZoneChanged(zoneID);
	}

	private void RemoveOwnerEdge(OwnerOccupancyState state, long ownerID, ulong edgeHash)
	{
		if (!state.Edges.Remove(edgeHash)) return;
		RemoveOwnerFromOccupiedEdge(edgeHash, ownerID);

		if (!LiveGraph.TryGetOccupancyZoneID(edgeHash, out ulong zoneID) || !state.EdgeCountByZone.TryGetValue(zoneID, out int count)) return;

		if (count <= 1)
		{
			state.EdgeCountByZone.Remove(zoneID);
			if (RemoveZoneOccupant(zoneID, ownerID)) QueueOccupancyZoneChanged(zoneID);
		}
		else { state.EdgeCountByZone[zoneID] = count - 1; }
	}

	private void RemoveOwnerFromOccupiedEdge(ulong edgeHash, long ownerID)
	{
		if (!OwnersByOccupiedEdge.TryGetValue(edgeHash, out HashSet<long>? owners)) return;
		owners.Remove(ownerID);
		if (owners.Count == 0) OwnersByOccupiedEdge.Remove(edgeHash);
	}

	private void ReleaseOwnerOccupancy(long ownerID)
	{
		ClearTransientSignalReservations(ownerID);
		if (!OccupancyByOwner.Remove(ownerID, out OwnerOccupancyState? state)) return;

		foreach (ulong edgeHash in state.Edges) RemoveOwnerFromOccupiedEdge(edgeHash, ownerID);
		foreach (ulong zoneID in state.EdgeCountByZone.Keys) { if (RemoveZoneOccupant(zoneID, ownerID)) QueueOccupancyZoneChanged(zoneID); }
	}

	internal void DebugCollectSignalEdgeStates(BlockPos center, int radius, List<AutomationDebugSignalEdgeState> destination)
	{
		destination?.Clear();
		if (!Loaded || center == null || destination == null) return;

		radius = Math.Max(1, radius);
		var seenEdges = new HashSet<ulong>();

		foreach (SignalIndexEntry signal in SignalIndex.Values)
		{
			SignalBlockKey block = signal.BlockKey;
			if (block.Dimension != center.dimension || Math.Abs(block.X - center.X) > radius || Math.Abs(block.Z - center.Z) > radius) { continue; }

			AddEdge(signal.FirstEdge);
			AddEdge(signal.SecondEdge);
		}

		void AddEdge(ulong edgeHash)
		{
			if (edgeHash == 0 || !seenEdges.Add(edgeHash)) return;
			if (!LiveGraph.TryGetOccupancyZoneID(edgeHash, out ulong zoneID) || zoneID == 0) return;

			long[] ownerIDs = Array.Empty<long>();
			bool occupied = OwnersByZone.TryGetValue(zoneID, out HashSet<long>? owners) && owners.Count > 0;
			if (occupied)
			{
				ownerIDs = new long[owners!.Count];
				owners!.CopyTo(ownerIDs);
				Array.Sort(ownerIDs);
			}

			destination.Add(new AutomationDebugSignalEdgeState
			{
				EdgeHash = edgeHash,
				ZoneID = zoneID,
				Occupied = occupied,
				OwnerIDs = ownerIDs
			});
		}
	}

	internal bool IsZoneOccupiedByAnotherOwner(ulong zoneID, long ownerID) { return IsZoneOccupiedByOther(zoneID, ownerID); }

	private bool IsZoneOccupiedByOther(ulong zoneID, long ownerID)
	{
		if (zoneID == 0 || !OwnersByZone.TryGetValue(zoneID, out HashSet<long>? owners)) return false;
		foreach (long occupant in owners) { if (occupant != ownerID) return true; }
		return false;
	}

	private bool AddZoneOccupant(ulong zoneID, long ownerID)
	{
		if (zoneID == 0 || ownerID == 0) return false;
		if (!OwnersByZone.TryGetValue(zoneID, out HashSet<long>? owners))
		{
			owners = new HashSet<long>();
			OwnersByZone[zoneID] = owners;
		}
		bool changed = owners.Add(ownerID);
		if (changed) LiveGraph.AddZoneOccupant(zoneID, ownerID); // Debug/persistence mirror only
		return changed;
	}

	private bool RemoveZoneOccupant(ulong zoneID, long ownerID)
	{
		if (!OwnersByZone.TryGetValue(zoneID, out HashSet<long>? owners) || !owners.Remove(ownerID)) return false;
		if (owners.Count == 0) OwnersByZone.Remove(zoneID);
		LiveGraph.RemoveZoneOccupant(zoneID, ownerID); // Debug/persistence mirror only
		return true;
	}

	private void BeginOccupancyBatch() { OccupancyBatchDepth++; }

	private void EndOccupancyBatch()
	{
		if (OccupancyBatchDepth <= 0) return;
		OccupancyBatchDepth--;
		if (OccupancyBatchDepth != 0 || OccupancyChangedZoneScratch.Count == 0) return;

		unchecked { OccupancyChangeSerialNumber++; }
		RefreshSignalsForZones(OccupancyChangedZoneScratch);
		OccupancyChangedZoneScratch.Clear();
	}

	private void QueueOccupancyZoneChanged(ulong zoneID)
	{
		if (zoneID == 0) return;
		OccupancyChangedZoneScratch.Add(zoneID);
		if (OccupancyBatchDepth != 0) return;

		unchecked { OccupancyChangeSerialNumber++; }
		RefreshSignalsForZones(OccupancyChangedZoneScratch);
		OccupancyChangedZoneScratch.Clear();
	}

	internal void RequestSignalAspectRefresh(BlockPos position) => RefreshSignalBlock(position);

	internal void RegisterLoadedSignal(BlockPos position)
	{
		LoadedSignalBlocks.Add(new SignalBlockKey(position.X, position.Y, position.Z, position.dimension));
		if (Loaded) RefreshSignalBlock(position);
	}

	internal void UnregisterLoadedSignal(BlockPos position) 
	{
		LoadedSignalBlocks.Remove(new SignalBlockKey(position.X, position.Y, position.Z, position.dimension));
	}

	// Signal visuals internals
	private readonly record struct RailChunkColumnKey(int Dimension, int X, int Z);
	private readonly record struct SignalBlockKey(int X, int Y, int Z, int Dimension);
	private readonly record struct ChainWalkKey(RailGraphLive.EndpointKey FromEndpoint, ulong EdgeHash);

	private enum SignalSide : byte
	{
		Unknown = 0,
		North = 1,
		South = 2
	}

	private sealed class SignalIndexEntry
	{
		internal SignalKind Kind;
		internal SignalBlockKey BlockKey;
		internal ulong FirstEdge;
		internal ulong SecondEdge;
		internal ulong OneWayAllowedFromEdgeHash;
	}

	internal ulong GetSignalAuthorityRevision(RailGraphLive.EndpointKey endpoint)
	{
		return SignalAuthorityRevision.TryGetValue(endpoint, out ulong revision) ? revision : 0;
	}

	private void RebuildSignalIndexAll()
	{
		ZoneToSignals.Clear();
		SignalIndex.Clear();
		ZonesBySignal.Clear();
		SignalEndpointByBlock.Clear();
		SignalAuthorityRevision.Clear();

		foreach (RailGraphLive.EndpointKey endpoint in LiveGraph.SignalsUnsafe.Keys) { BuildSignalIndexEntry(endpoint); }
	}

	private void RebuildAffectedSignalIndex(RailGraphChangeSet change)
	{
		if (change.GlobalInvalidation || SignalIndex.Count == 0)
		{
			RebuildSignalIndexAll();
			RefreshAllLoadedSignalBlocks();
			return;
		}

		SignalRefreshScratch.Clear();

		for (int signalIndex = 0; signalIndex < change.TouchedSignals.Count; signalIndex++) SignalRefreshScratch.Add(change.TouchedSignals[signalIndex]);
		for (int endpointIndex = 0; endpointIndex < change.TouchedEndpoints.Count; endpointIndex++)
		{
			RailGraphLive.EndpointKey endpoint = change.TouchedEndpoints[endpointIndex];
			if (LiveGraph.IsSignalEndpoint(endpoint) || SignalIndex.ContainsKey(endpoint)) SignalRefreshScratch.Add(endpoint);
		}

		for (int zoneIndex = 0; zoneIndex < change.TouchedOccupancyZones.Count; zoneIndex++)
		{
			AddSignalsDependingOnZone(change.TouchedOccupancyZones[zoneIndex], SignalRefreshScratch);
		}

		// Reassigned edges expose the new adjacent zones.
		// Existing reverse dependencies expose every upstream chain signal whose old authority path crossed the edited section.
		for (int edgeIndex = 0; edgeIndex < change.ZoneReassignedEdges.Count; edgeIndex++)
		{
			if (LiveGraph.TryGetOccupancyZoneID(change.ZoneReassignedEdges[edgeIndex], out ulong zoneID)) AddSignalsDependingOnZone(zoneID, SignalRefreshScratch);
		}

		// Expand through old dependency zones before removing mappings.
		// This reaches upstream chain signals when a downstream chain record itself changes.
		SignalRefreshQueueScratch.Clear();
		SignalRefreshQueueScratch.AddRange(SignalRefreshScratch);
		for (int queueIndex = 0; queueIndex < SignalRefreshQueueScratch.Count; queueIndex++)
		{
			RailGraphLive.EndpointKey endpoint = SignalRefreshQueueScratch[queueIndex];
			if (!ZonesBySignal.TryGetValue(endpoint, out HashSet<ulong>? zones)) continue;
			foreach (ulong zoneID in zones)
			{
				if (!ZoneToSignals.TryGetValue(zoneID, out HashSet<RailGraphLive.EndpointKey>? dependentSignals)) continue;
				foreach (RailGraphLive.EndpointKey otherSignal in dependentSignals)
				{
					if (SignalRefreshScratch.Add(otherSignal)) SignalRefreshQueueScratch.Add(otherSignal);
				}
			}
		}

		foreach (RailGraphLive.EndpointKey endpoint in SignalRefreshScratch) RemoveSignalIndexEntry(endpoint);
		foreach (RailGraphLive.EndpointKey endpoint in SignalRefreshScratch) { if (LiveGraph.IsSignalEndpoint(endpoint)) BuildSignalIndexEntry(endpoint); }
		foreach (RailGraphLive.EndpointKey endpoint in SignalRefreshScratch) UpdateSignalAtEndpoint(endpoint);
		SignalRefreshScratch.Clear();
		SignalRefreshQueueScratch.Clear();
	}

	private void AddSignalsDependingOnZone(ulong zoneID, HashSet<RailGraphLive.EndpointKey> destination)
	{
		if (zoneID == 0 || !ZoneToSignals.TryGetValue(zoneID, out HashSet<RailGraphLive.EndpointKey>? signalsForZone)) return;
		foreach (RailGraphLive.EndpointKey endpoint in signalsForZone) destination.Add(endpoint);
	}

	private void RemoveSignalIndexEntry(RailGraphLive.EndpointKey endpoint)
	{
		if (ZonesBySignal.Remove(endpoint, out HashSet<ulong>? zones))
		{
			foreach (ulong zoneID in zones)
			{
				if (!ZoneToSignals.TryGetValue(zoneID, out HashSet<RailGraphLive.EndpointKey>? endpoints)) continue;
				endpoints.Remove(endpoint);
				if (endpoints.Count == 0) ZoneToSignals.Remove(zoneID);
			}
		}

		if (SignalIndex.Remove(endpoint, out SignalIndexEntry? entry)) { SignalEndpointByBlock.Remove(entry.BlockKey); }
		SignalAuthorityRevision.Remove(endpoint);
	}

	private void BuildSignalIndexEntry(RailGraphLive.EndpointKey endpoint)
	{
		if (!LiveGraph.TryGetSignal(endpoint, out RailGraphLive.SignalEntry entry)) return;

		var blockKey = new SignalBlockKey(entry.OwnerX, entry.OwnerY, entry.OwnerZ, endpoint.Dimension);
		ulong firstEdge = 0;
		ulong secondEdge = 0;
		if (LiveGraph.TryGetIncidentEdges(endpoint, out List<ulong>? incidentEdges))
		{
			for (int edgeIndex = 0; edgeIndex < incidentEdges.Count; edgeIndex++)
			{
				ulong edgeHash = incidentEdges[edgeIndex];
				if (edgeHash == 0 || edgeHash == firstEdge || edgeHash == secondEdge) continue;
				if (firstEdge == 0) firstEdge = edgeHash;
				else if (secondEdge == 0) secondEdge = edgeHash;
			}
		}

		var dependencies = new HashSet<ulong>();
		if (firstEdge != 0 && LiveGraph.TryGetOccupancyZoneID(firstEdge, out ulong firstZoneID)) dependencies.Add(firstZoneID);
		if (secondEdge != 0 && LiveGraph.TryGetOccupancyZoneID(secondEdge, out ulong secondZoneID)) dependencies.Add(secondZoneID);

		if (entry.Kind == SignalKind.Chain && TryGetOtherSignalEdge(endpoint, entry.AllowedFromEdgeHash, out ulong outgoingEdge))
		{
			CollectChainVisualZones(endpoint, outgoingEdge, dependencies);
		}

		SignalIndex[endpoint] = new SignalIndexEntry
		{
			Kind = entry.Kind,
			BlockKey = blockKey,
			FirstEdge = firstEdge,
			SecondEdge = secondEdge,
			OneWayAllowedFromEdgeHash = entry.AllowedFromEdgeHash
		};
		SignalEndpointByBlock[blockKey] = endpoint;
		ZonesBySignal[endpoint] = dependencies;

		foreach (ulong zoneID in dependencies)
		{
			if (!ZoneToSignals.TryGetValue(zoneID, out HashSet<RailGraphLive.EndpointKey>? endpoints))
			{
				endpoints = new HashSet<RailGraphLive.EndpointKey>();
				ZoneToSignals[zoneID] = endpoints;
			}
			endpoints.Add(endpoint);
		}

		SignalAuthorityRevision[endpoint] = NextSignalAuthorityRevision++;
	}

	private static void GetWorldDirectionForLocalNorth(Block? block, out int x, out int z)
	{
		float rotationY = block?.Shape?.rotateY ?? 0f;

		// Quantize to nearest 90 degrees to match how shapes are rotated in Json.
		int quarterTurns = (int)Math.Round(rotationY / 90f);
		quarterTurns %= 4;
		if (quarterTurns < 0) quarterTurns += 4;

		switch (quarterTurns)
		{
			case 0: x = 0; z = -1; break;	// local north -> world north
			case 1: x = -1; z = 0; break;	// local north -> world west
			case 2: x = 0; z = 1; break;	// local north -> world south
			default: x = 1; z = 0; break;	// local north -> world east
		}
	}

	private SignalSide ResolveOneWayAllowedSide(RailGraphLive.EndpointKey endpoint, ulong allowedFromEdgeHash, int northX, int northZ)
	{
		// Decide whether the allowed-from half-edge lies on the local north or local south side.
		if (!LiveGraph.TryGetEdgeEndpoints(allowedFromEdgeHash, out var firstEndpoint, out var secondEndpoint, out _)) return SignalSide.Unknown;

		RailGraphLive.EndpointKey otherEndpoint;
		if (firstEndpoint.Equals(endpoint)) otherEndpoint = secondEndpoint;
		else if (secondEndpoint.Equals(endpoint)) otherEndpoint = firstEndpoint;
		else return SignalSide.Unknown;

		int deltaX = otherEndpoint.X16 - endpoint.X16;
		int deltaZ = otherEndpoint.Z16 - endpoint.Z16;

		int northDotProduct = deltaX * northX + deltaZ * northZ;
		return northDotProduct >= 0 ? SignalSide.North : SignalSide.South;
	}

	private void ClassifySignalSides(RailGraphLive.EndpointKey endpoint, ulong firstEdge, ulong secondEdge, int northX, int northZ, out ulong northEdge, out ulong southEdge)
	{
		ulong bestNorthEdge = 0;
		ulong bestSouthEdge = 0;
		int bestNorth = int.MinValue;
		int bestSouth = int.MinValue;

		Consider(firstEdge);
		Consider(secondEdge);

		if (bestNorthEdge == bestSouthEdge && bestNorthEdge != 0)
		{
			if (firstEdge != 0 && firstEdge != bestNorthEdge) bestSouthEdge = firstEdge;
			else if (secondEdge != 0 && secondEdge != bestNorthEdge) bestSouthEdge = secondEdge;
			else bestSouthEdge = 0;
		}

		northEdge = bestNorthEdge;
		southEdge = bestSouthEdge;

		void Consider(ulong edgeHash)
		{
			if (edgeHash == 0) return;
			if (!LiveGraph.TryGetEdgeEndpoints(edgeHash, out var firstEndpoint, out var secondEndpoint, out _)) return;

			RailGraphLive.EndpointKey otherEndpoint;
			if (firstEndpoint.Equals(endpoint)) otherEndpoint = secondEndpoint;
			else if (secondEndpoint.Equals(endpoint)) otherEndpoint = firstEndpoint;
			else return;

			int deltaX = otherEndpoint.X16 - endpoint.X16;
			int deltaZ = otherEndpoint.Z16 - endpoint.Z16;
			int dotProduct = deltaX * northX + deltaZ * northZ;

			if (dotProduct > bestNorth || (dotProduct == bestNorth && (bestNorthEdge == 0 || edgeHash < bestNorthEdge)))
			{
				bestNorth = dotProduct;
				bestNorthEdge = edgeHash;
			}

			int dotSouth = -dotProduct;
			if (dotSouth > bestSouth || (dotSouth == bestSouth && (bestSouthEdge == 0 || edgeHash < bestSouthEdge)))
			{
				bestSouth = dotSouth;
				bestSouthEdge = edgeHash;
			}
		}
	}


	internal bool TryComputeSignalAspects(BlockPos position, out SignalAspect northFacingAspect, out SignalAspect southFacingAspect)
	{
		northFacingAspect = SignalAspect.Green;
		southFacingAspect = SignalAspect.Green;

		if (!Loaded) return false;


		var signalBlockKey = new SignalBlockKey(position.X, position.Y, position.Z, position.dimension);
		if (!SignalEndpointByBlock.TryGetValue(signalBlockKey, out var endpoint)) return false;
		if (!SignalIndex.TryGetValue(endpoint, out var signalInformation)) return false;

		Block block = ServerAPI.World.BlockAccessor.GetBlock(position);
		GetWorldDirectionForLocalNorth(block, out int northX, out int northZ);

		ClassifySignalSides(endpoint, signalInformation.FirstEdge, signalInformation.SecondEdge, northX, northZ, out ulong northEdge, out ulong southEdge);

		ulong northZoneID = 0;
		ulong southZoneID = 0;
		if (northEdge != 0) LiveGraph.TryGetOccupancyZoneID(northEdge, out northZoneID);
		if (southEdge != 0) LiveGraph.TryGetOccupancyZoneID(southEdge, out southZoneID);

		if ((signalInformation.Kind == SignalKind.OneWay || signalInformation.Kind == SignalKind.Chain) && signalInformation.OneWayAllowedFromEdgeHash != 0)
		{
			SignalSide allowedSide = ResolveOneWayAllowedSide(endpoint, signalInformation.OneWayAllowedFromEdgeHash, northX, northZ);
			SignalAspect allowedAspect = signalInformation.Kind == SignalKind.Chain && 
				TryGetOtherSignalEdge(endpoint, signalInformation.OneWayAllowedFromEdgeHash, out ulong outgoingEdge)
					? ChainVisualAspect(endpoint, outgoingEdge)
					: AspectFromZone(signalInformation.OneWayAllowedFromEdgeHash == northEdge ? southZoneID : northZoneID, signalInformation.Kind);

			if (allowedSide == SignalSide.North)
			{
				northFacingAspect = allowedAspect;
				southFacingAspect = SignalAspect.Red;
				return true;
			}

			if (allowedSide == SignalSide.South)
			{
				southFacingAspect = allowedAspect;
				northFacingAspect = SignalAspect.Red;
				return true;
			}
		}

		// Block signal fallback, each face checks the zone beyond the signal.
		northFacingAspect = AspectFromZone(southZoneID, signalInformation.Kind);
		southFacingAspect = AspectFromZone(northZoneID, signalInformation.Kind);
		return true;
	}

	private void RefreshSignalBlock(BlockPos position)
	{
		if (ServerAPI.World.BlockAccessor.GetBlockEntity(position) is not BlockEntityRailSignal signalBlockEntity) return;
		if (!TryComputeSignalAspects(position, out var northFacingAspect, out var southFacingAspect)) return;
		signalBlockEntity.SetAspects(northFacingAspect, southFacingAspect);
	}

	private void RefreshAllLoadedSignalBlocks()
	{
		if (LoadedSignalBlocks.Count == 0) return;


		foreach (var signalBlockKey in LoadedSignalBlocks)
		{
			var position = new BlockPos(signalBlockKey.X, signalBlockKey.Y, signalBlockKey.Z, signalBlockKey.Dimension);
			RefreshSignalBlock(position);
		}
	}

	private void RefreshSignalsForZones(IReadOnlyCollection<ulong> zones)
	{
		SignalRefreshScratch.Clear();
		foreach (ulong zoneID in zones)
		{
			if (!ZoneToSignals.TryGetValue(zoneID, out HashSet<RailGraphLive.EndpointKey>? endpoints)) continue;
			foreach (RailGraphLive.EndpointKey endpoint in endpoints) SignalRefreshScratch.Add(endpoint);
		}

		foreach (RailGraphLive.EndpointKey endpoint in SignalRefreshScratch)
		{
			SignalAuthorityRevision[endpoint] = NextSignalAuthorityRevision++;
			UpdateSignalAtEndpoint(endpoint);
			SignalAuthorityChanged?.Invoke(endpoint);
		}
		SignalRefreshScratch.Clear();
	}

	private void UpdateSignalAtEndpoint(RailGraphLive.EndpointKey endpoint)
	{
		if (!SignalIndex.TryGetValue(endpoint, out SignalIndexEntry? signalInformation)) return;
		SignalBlockKey signalBlockKey = signalInformation.BlockKey;
		if (!LoadedSignalBlocks.Contains(signalBlockKey)) return;
		RefreshSignalBlock(new BlockPos(signalBlockKey.X, signalBlockKey.Y, signalBlockKey.Z, signalBlockKey.Dimension));
	}

	private bool TryGetOtherSignalEdge(RailGraphLive.EndpointKey signalEndpoint, ulong fromEdgeHash, out ulong otherEdgeHash)
	{
		otherEdgeHash = 0;
		if (fromEdgeHash == 0) return false;
		if (!LiveGraph.EdgeTouchesEndpoint(fromEdgeHash, signalEndpoint)) return false;
		if (!LiveGraph.TryGetIncidentEdges(signalEndpoint, out List<ulong> incident)) return false;

		for (int edgeIndex = 0; edgeIndex < incident.Count; edgeIndex++)
		{
			ulong edgeHash = incident[edgeIndex];
			if (edgeHash != 0 && edgeHash != fromEdgeHash && IsVisualContinuationAllowed(signalEndpoint, fromEdgeHash, edgeHash))
			{
				otherEdgeHash = edgeHash;
				return true;
			}
		}

		return false;
	}

	private bool IsVisualContinuationAllowed(RailGraphLive.EndpointKey atEndpoint, ulong fromEdgeHash, ulong toEdgeHash)
	{
		if (fromEdgeHash == 0 || toEdgeHash == 0 || fromEdgeHash == toEdgeHash)		return false;
		if (!LiveGraph.TryGetEdgeGauge(fromEdgeHash, out byte gauge))				return false;
		if (gauge != atEndpoint.Gauge)												return false;

		return RailTransitionRules.IsDirectedTransitionAllowedSameGauge(LiveGraph, atEndpoint, gauge, fromEdgeHash, toEdgeHash);
	}

	private enum ChainVisualResult : byte
	{
		Clear,
		Blocked,
		Mixed
	}

	private SignalAspect ChainVisualAspect(RailGraphLive.EndpointKey signalEndpoint, ulong firstEdgeHash)
	{
		ChainVisualActiveScratch.Clear();
		ChainVisualMemoScratch.Clear();
		ChainVisualFrameScratch.Clear();

		var rootKey = new ChainWalkKey(signalEndpoint, firstEdgeHash);
		ChainVisualFrameScratch.Add(new ChainVisualFrame(rootKey));

		ChainVisualResult rootResult = ChainVisualResult.Blocked;
		while (ChainVisualFrameScratch.Count > 0)
		{
			int frameIndex = ChainVisualFrameScratch.Count - 1;
			ChainVisualFrame frame = ChainVisualFrameScratch[frameIndex];

			if (!frame.Initialized)
			{
				if (ChainVisualMemoScratch.TryGetValue(frame.Key, out ChainVisualResult memoized))
				{
					ChainVisualFrameScratch.RemoveAt(frameIndex);
					IncludeChildResult(memoized);
					continue;
				}

				if (!ChainVisualActiveScratch.Add(frame.Key))
				{
					ChainVisualFrameScratch.RemoveAt(frameIndex);
					IncludeChildResult(ChainVisualResult.Blocked);
					continue;
				}

				if (TryInitializeChainVisualFrame(ref frame, out ChainVisualResult terminal))
				{
					ChainVisualActiveScratch.Remove(frame.Key);
					ChainVisualMemoScratch[frame.Key] = terminal;
					ChainVisualFrameScratch.RemoveAt(frameIndex);
					if (ChainVisualFrameScratch.Count == 0) rootResult = terminal;
					else IncludeChildResult(terminal);
					continue;
				}

				frame.Initialized = true;
				ChainVisualFrameScratch[frameIndex] = frame;
			}

			bool pushedChild = false;
			while (frame.IncidentEdges != null && frame.NextIncidentIndex < frame.IncidentEdges.Count)
			{
				ulong nextEdge = frame.IncidentEdges[frame.NextIncidentIndex++];
				if (!IsVisualContinuationAllowed(frame.NextEndpoint, frame.Key.EdgeHash, nextEdge)) continue;

				frame.SawChild = true;
				var childKey = new ChainWalkKey(frame.NextEndpoint, nextEdge);
				if (ChainVisualMemoScratch.TryGetValue(childKey, out ChainVisualResult childMemo))	{ frame.Include(childMemo); continue; }
				if (ChainVisualActiveScratch.Contains(childKey))									{ frame.Include(ChainVisualResult.Blocked); continue; }

				ChainVisualFrameScratch[frameIndex] = frame;
				ChainVisualFrameScratch.Add(new ChainVisualFrame(childKey));
				pushedChild = true;
				break;
			}

			if (pushedChild) continue;

			ChainVisualResult result = frame.Result;
			ChainVisualActiveScratch.Remove(frame.Key);
			ChainVisualMemoScratch[frame.Key] = result;
			ChainVisualFrameScratch.RemoveAt(frameIndex);
			if (ChainVisualFrameScratch.Count == 0) rootResult = result;
			else IncludeChildResult(result);
		}

		ChainVisualActiveScratch.Clear();
		ChainVisualMemoScratch.Clear();
		ChainVisualFrameScratch.Clear();

		return rootResult switch
		{
			ChainVisualResult.Clear => SignalAspect.Green,
			ChainVisualResult.Blocked => SignalAspect.Red,
			_ => SignalAspect.Yellow
		};

		void IncludeChildResult(ChainVisualResult childResult)
		{
			if (ChainVisualFrameScratch.Count == 0) { rootResult = childResult; return; }

			int parentIndex = ChainVisualFrameScratch.Count - 1;
			ChainVisualFrame parent = ChainVisualFrameScratch[parentIndex];
			parent.Include(childResult);
			ChainVisualFrameScratch[parentIndex] = parent;
		}
	}

	/// Initializes one directed chain-visual state. Returns true when the state is terminal.
	/// Non-terminal states expose their graph adjacency directly and are consumed by the explicit DFS in ChainVisualAspect no railway length can consume call stack.
	private bool TryInitializeChainVisualFrame(ref ChainVisualFrame frame, out ChainVisualResult terminal)
	{
		terminal = ChainVisualResult.Blocked;

		if 
		(
			frame.Key.EdgeHash == 0 || !LiveGraph.TryGetOccupancyZoneID(frame.Key.EdgeHash, out ulong zoneID) || LiveGraph.IsOccupancyZoneOccupied(zoneID) ||
			!LiveGraph.TryGetOtherEndpoint(frame.Key.EdgeHash, frame.Key.FromEndpoint, out RailGraphLive.EndpointKey nextEndpoint)
		) { return true; }

		frame.NextEndpoint = nextEndpoint;
		if (LiveGraph.IsSignalEndpoint(nextEndpoint))
		{
			if (!IsSignalEntryAllowed(nextEndpoint, frame.Key.EdgeHash) || !LiveGraph.TryGetIncidentEdges(nextEndpoint, out List<ulong>? signalIncidentEdges))
			{
				return true;
			}

			if (!LiveGraph.IsChainSignal(nextEndpoint))
			{
				bool sawClear = false;
				bool sawBlocked = false;
				bool sawExit = false;

				for (int edgeIndex = 0; edgeIndex < signalIncidentEdges.Count; edgeIndex++)
				{
					ulong nextEdge = signalIncidentEdges[edgeIndex];
					if (!IsVisualContinuationAllowed(nextEndpoint, frame.Key.EdgeHash, nextEdge)) continue;
					sawExit = true;

					if (!LiveGraph.TryGetOccupancyZoneID(nextEdge, out ulong exitZone) || LiveGraph.IsOccupancyZoneOccupied(exitZone))	{ sawBlocked = true; }
					else																												{ sawClear = true; }
				}

				terminal = !sawExit || (sawBlocked && !sawClear) ? ChainVisualResult.Blocked : sawClear && sawBlocked ? ChainVisualResult.Mixed : ChainVisualResult.Clear;
				return true;
			}

			frame.IncidentEdges = signalIncidentEdges;
			frame.DefaultClear = false;
			return false;
		}

		if (!LiveGraph.TryGetIncidentEdges(nextEndpoint, out List<ulong>? incidentEdges))
		{
			terminal = ChainVisualResult.Blocked;
			return true;
		}

		frame.IncidentEdges = incidentEdges;
		frame.DefaultClear = false;
		return false;
	}

	private void CollectChainVisualZones(RailGraphLive.EndpointKey fromEndpoint, ulong edgeHash, HashSet<ulong> zones)
	{
		ChainTopologyVisitedScratch.Clear();
		ChainTopologyWorkScratch.Clear();
		if (edgeHash != 0) ChainTopologyWorkScratch.Add(new ChainWalkKey(fromEndpoint, edgeHash));

		for (int workIndex = 0; workIndex < ChainTopologyWorkScratch.Count; workIndex++)
		{
			ChainWalkKey walkState = ChainTopologyWorkScratch[workIndex];
			if (!ChainTopologyVisitedScratch.Add(walkState)) continue;

			if (LiveGraph.TryGetOccupancyZoneID(walkState.EdgeHash, out ulong zoneID)) zones.Add(zoneID);
			if (!LiveGraph.TryGetOtherEndpoint(walkState.EdgeHash, walkState.FromEndpoint, out RailGraphLive.EndpointKey nextEndpoint)) continue;

			if (LiveGraph.IsSignalEndpoint(nextEndpoint))
			{
				if (!IsSignalEntryAllowed(nextEndpoint, walkState.EdgeHash) || !LiveGraph.TryGetIncidentEdges(nextEndpoint, out List<ulong>? signalIncidentEdges)) continue;

				bool cascade = LiveGraph.IsChainSignal(nextEndpoint);
				for (int edgeIndex = 0; edgeIndex < signalIncidentEdges.Count; edgeIndex++)
				{
					ulong nextEdge = signalIncidentEdges[edgeIndex];
					if (!IsVisualContinuationAllowed(nextEndpoint, walkState.EdgeHash, nextEdge)) continue;
					if (cascade) ChainTopologyWorkScratch.Add(new ChainWalkKey(nextEndpoint, nextEdge));
					else if (LiveGraph.TryGetOccupancyZoneID(nextEdge, out ulong exitZone)) zones.Add(exitZone);
				}
				continue;
			}

			if (!LiveGraph.TryGetIncidentEdges(nextEndpoint, out List<ulong>? incidentEdges)) continue;
			for (int edgeIndex = 0; edgeIndex < incidentEdges.Count; edgeIndex++)
			{
				ulong nextEdge = incidentEdges[edgeIndex];
				if (IsVisualContinuationAllowed(nextEndpoint, walkState.EdgeHash, nextEdge)) ChainTopologyWorkScratch.Add(new ChainWalkKey(nextEndpoint, nextEdge));
			}
		}

		ChainTopologyVisitedScratch.Clear();
		ChainTopologyWorkScratch.Clear();
	}

	private struct ChainVisualFrame
	{
		internal readonly ChainWalkKey Key;
		internal RailGraphLive.EndpointKey NextEndpoint;
		internal List<ulong>? IncidentEdges;
		internal int NextIncidentIndex;
		internal bool Initialized;
		internal bool DefaultClear;
		internal bool SawChild;
		internal bool SawClear;
		internal bool SawBlocked;

		internal ChainVisualFrame(ChainWalkKey walkKey)
		{
			Key = walkKey;
			NextEndpoint = default;
			IncidentEdges = null;
			NextIncidentIndex = 0;
			Initialized = false;
			DefaultClear = false;
			SawChild = false;
			SawClear = false;
			SawBlocked = false;
		}

		internal void Include(ChainVisualResult result)
		{
			if (result == ChainVisualResult.Clear) SawClear = true;
			else if (result == ChainVisualResult.Blocked) SawBlocked = true;
			else { SawClear = true; SawBlocked = true; }
		}

		internal ChainVisualResult Result =>
			!SawChild
				? (DefaultClear ? ChainVisualResult.Clear : ChainVisualResult.Blocked)
				: SawClear && SawBlocked
					? ChainVisualResult.Mixed
					: SawClear
						? ChainVisualResult.Clear
						: ChainVisualResult.Blocked;
	}

	private SignalAspect AspectFromZone(ulong zoneID, SignalKind kind)
	{
		if (zoneID == 0 || LiveGraph.IsOccupancyZoneOccupied(zoneID)) return SignalAspect.Red;
		return SignalAspect.Green;
	}
}

[ProtoContract]
public sealed class RailGraphRequest : INetworkMessage
{
	[ProtoMember(1)] public int CenterX;
	[ProtoMember(2)] public int CenterY;
	[ProtoMember(3)] public int CenterZ;
	[ProtoMember(4)] public int Dimension;

	[ProtoMember(5)] public bool Detailed;
	[ProtoMember(6)] public int LastBuildVersion;
	[ProtoMember(7)] public bool ForceRebuild;
	[ProtoMember(8)] public int Radius;
	[ProtoMember(9)] public bool IncludeDebugOccupancy;
}

[ProtoContract]
public sealed class RailGraphResponse : INetworkMessage
{
	[ProtoMember(1)] public int BuildVersion;
	[ProtoMember(2)] public bool Detailed;

	[ProtoMember(3)] public byte[] GraphData = Array.Empty<byte>();
	[ProtoMember(4)] public byte[] ClearanceData = Array.Empty<byte>();
	[ProtoMember(5)] public bool Truncated;
	[ProtoMember(6)] public int SerializedEdges;
	[ProtoMember(7)] public int SerializedPoints;
	[ProtoMember(8)] public ulong DebugOccupancySerial;
	[ProtoMember(9)] public RailGraphDebugEdgeOccupancy[] DebugEdgeOccupancy = Array.Empty<RailGraphDebugEdgeOccupancy>();
	[ProtoMember(10)] public bool DebugOccupancyIncluded;
}

[ProtoContract]
public sealed class RailGraphDebugEdgeOccupancy
{
	[ProtoMember(1)] public ulong EdgeHash;
	[ProtoMember(2)] public ulong ZoneID;
	[ProtoMember(3)] public bool Occupied;
	[ProtoMember(4)] public long[] OwnerIDs = Array.Empty<long>();
}
