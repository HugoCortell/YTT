using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace YangTransport;

/// The clearance layer pre-computes whether a path is clear for passage, so that convoys don't need to do expensive lookups every segment they cross.
#region Clearance Layer
internal sealed class RailClearanceLayer
{
	private const int MaxPersistenceRecords					= 10_000_000;

	private const int ClearanceTickMS						= 500;
	private const int ClearanceCandidateRadiusBlocks		= 16;
	private const int MinecartClearanceHeightBlocks			= 1;
	private const int StandardGaugeClearanceHeightBlocks	= 3;
	private const int MaxEdgeRechecksPerBatch				= 256;
	private const int MaxBlockReadsPerBatch					= 2048;
	private const int ExplosionRescanRadiusExtraBlocks		= 4;

	private ICoreServerAPI ServerAPI = null!;
	private RailGraphLive RailGraph = null!;

	private readonly HashSet<ulong> BlockedEdges = new();
	private readonly HashSet<ulong> DirtyEdges = new();
	private readonly Queue<ulong> DirtyEdgeQueue = new();
	private readonly HashSet<ulong> QueuedDirtyEdges = new();
	private readonly Dictionary<ulong, ClearanceScanCursor> ScanCursorByEdge = new();

	// A scan stops at the first unavailable chunk and is removed from the runnable queue.
	// Chunk-column load notifications wake only the exact scans that can now continue, so unloaded regions consume no recurring clearance budget.
	private readonly Dictionary<ulong, ClearanceChunkWait> ChunkWaitByEdge = new();
	private readonly Dictionary<long, HashSet<ulong>> ChunkWaitEdgesByColumn = new();
	private readonly HashSet<long> LoadedChunkColumns = new();
	private readonly List<ulong> ChunkWaitEdgeScratch = new();

	private readonly Dictionary<int, RailClearanceFootprint> FootprintByBlockID = new();
	private readonly Dictionary<ulong, EdgeOwner> OwnersByEdge = new();

	private readonly List<ulong> CandidateEdgeScratch = new();
	private readonly List<ulong> EdgeSetPruneScratch = new();
	private readonly BlockPos TemporaryPosition = new();
	private readonly BlockPos TemporaryChunkProbePosition = new();

	private long NextProcessMS;
	private bool IsPersistenceDirty;
	internal event Action<ulong>? EdgeResolved;

	public void Start(ICoreServerAPI serverAPI, RailGraphLive railGraph)
	{
		this.ServerAPI = serverAPI;
		this.RailGraph = railGraph;
		NextProcessMS = serverAPI.World.ElapsedMilliseconds + ClearanceTickMS;
	}

	internal bool PersistenceDirty => IsPersistenceDirty;

	internal void ResetEmptyForLoadedGraph()
	{
		ResetClearanceSets();
		RailGraph.ClearEdgeClearanceState();
		IsPersistenceDirty = false;
	}

	internal byte[] SerializePersistence()
	{
		int initialCapacity = (int)Math.Min(64L * 1024 * 1024, Math.Max(64L, 64L + ((long)BlockedEdges.Count + DirtyEdges.Count) * 9L));
		using MemoryStream memoryStream = new(initialCapacity);
		using BinaryWriter binaryWriter = new(memoryStream);

		EdgeSetPruneScratch.Clear();
		foreach (ulong edgeHash in BlockedEdges)	{ if (RailGraph.ContainsEdge(edgeHash)) EdgeSetPruneScratch.Add(edgeHash); }
		foreach (ulong edgeHash in DirtyEdges)		{ if (!BlockedEdges.Contains(edgeHash) && RailGraph.ContainsEdge(edgeHash)) EdgeSetPruneScratch.Add(edgeHash); }

		binaryWriter.Write(EdgeSetPruneScratch.Count);
		foreach (ulong edgeHash in EdgeSetPruneScratch)
		{
			byte flags = 0;
			if (BlockedEdges.Contains(edgeHash)) flags |= 1;
			if (DirtyEdges.Contains(edgeHash)) flags |= 2;
			binaryWriter.Write(edgeHash);
			binaryWriter.Write(flags);
		}

		EdgeSetPruneScratch.Clear();
		binaryWriter.Flush();
		return memoryStream.ToArray();
	}

	internal bool TryLoadPersistence(byte[] payload)
	{
		try
		{
			using MemoryStream memoryStream = new(payload, writable: false);
			using BinaryReader binaryReader = new(memoryStream);
			int recordCount = binaryReader.ReadInt32();
			if (recordCount < 0 || recordCount > MaxPersistenceRecords) return false;
			if ((long)recordCount * 9L > memoryStream.Length - memoryStream.Position) return false;

			Dictionary<ulong, byte> staged = new(recordCount);
			for (int recordIndex = 0; recordIndex < recordCount; recordIndex++)
			{
				ulong edgeHash = binaryReader.ReadUInt64();
				byte flags = binaryReader.ReadByte();
				if ((flags & ~3) != 0 || flags == 0 || !RailGraph.ContainsEdge(edgeHash) || !staged.TryAdd(edgeHash, flags)) { return false; }
			}
			if (memoryStream.Position != memoryStream.Length) return false;

			ResetClearanceSets();
			RailGraph.ClearEdgeClearanceState();
			foreach (KeyValuePair<ulong, byte> stagedRecord in staged)
			{
				if ((stagedRecord.Value & 1) != 0) BlockedEdges.Add(stagedRecord.Key);
				if ((stagedRecord.Value & 2) != 0) DirtyEdges.Add(stagedRecord.Key);
				if ((stagedRecord.Value & 3) != 0) RailGraph.SetEdgeClearanceBlocked(stagedRecord.Key, true);
			}

			RefreshOwnerCacheFromGraph();
			RebuildDirtyQueueFromSet();
			IsPersistenceDirty = false;
			return true;
		}
		catch { return false; }
	}

	internal void MarkPersistenceSaved() { IsPersistenceDirty = false; }

	private void ResetClearanceSets()
	{
		BlockedEdges.Clear();
		DirtyEdges.Clear();
		DirtyEdgeQueue.Clear();
		QueuedDirtyEdges.Clear();
		ScanCursorByEdge.Clear();
		ChunkWaitByEdge.Clear();
		ChunkWaitEdgesByColumn.Clear();
		LoadedChunkColumns.Clear();
		ChunkWaitEdgeScratch.Clear();
		OwnersByEdge.Clear();
	}

	private void MarkPersistenceDirty() { IsPersistenceDirty = true; } // Me (and MarkPersistenceSaved) when I love boilerplate

	public byte[] SerializeForClient()
	{
		using MemoryStream memoryStream = new();
		using BinaryWriter binaryWriter = new(memoryStream);

		EdgeSetPruneScratch.Clear();
		foreach (ulong edgeHash in BlockedEdges) { if (RailGraph.TryGetEdgeGauge(edgeHash, out _)) EdgeSetPruneScratch.Add(edgeHash); }
		foreach (ulong edgeHash in DirtyEdges) { if (RailGraph.TryGetEdgeGauge(edgeHash, out _) && !BlockedEdges.Contains(edgeHash)) EdgeSetPruneScratch.Add(edgeHash); }

		binaryWriter.Write(EdgeSetPruneScratch.Count);
		for (int edgeIndex = 0; edgeIndex < EdgeSetPruneScratch.Count; edgeIndex++)
		{
			ulong edgeHash = EdgeSetPruneScratch[edgeIndex];
			byte flags = 0;
			if (BlockedEdges.Contains(edgeHash)) flags |= 1;
			if (DirtyEdges.Contains(edgeHash)) flags |= 2;

			binaryWriter.Write(edgeHash);
			binaryWriter.Write(flags);
		}
		EdgeSetPruneScratch.Clear();

		return memoryStream.ToArray();
	}

	public static void ApplyClientState(RailGraphLive railGraph, byte[]? serializedState)
	{
		if (railGraph == null) return;

		railGraph.ClearEdgeClearanceState();
		if (serializedState == null || serializedState.Length == 0) return;

		try
		{
			using MemoryStream memoryStream = new(serializedState);
			using BinaryReader binaryReader = new(memoryStream);

			int recordCount = binaryReader.ReadInt32();
			if (recordCount < 0) return;

			for (int recordIndex = 0; recordIndex < recordCount; recordIndex++)
			{
				ulong edgeHash = binaryReader.ReadUInt64();
				byte flags = binaryReader.ReadByte();

				if ((flags & 3) != 0) railGraph.SetEdgeClearanceBlocked(edgeHash, true);
			}
		}
		catch { railGraph.ClearEdgeClearanceState(); }
	}

	public bool IsBlockedOrDirty(ulong edgeHash) { return BlockedEdges.Contains(edgeHash) || DirtyEdges.Contains(edgeHash); }

	/// Loaded authoritative movement may synchronously re-verify a completed BLOCKED certificate before obeying it.
	/// DIRTY edges remain exclusively owned by the normal asynchronous rebuild path.
	internal bool IsBlockedForLoadedMovement(ulong edgeHash, ref int remainingBlockReads, ref int remainingRepairAttempts)
	{
		if (DirtyEdges.Contains(edgeHash))		return true;
		if (!BlockedEdges.Contains(edgeHash))	return false;
		if (remainingRepairAttempts <= 0)		return true;

		remainingRepairAttempts--;
		ImmediateClearanceResult result = VerifyBlockedEdgeFresh(edgeHash, ref remainingBlockReads);
		if (result != ImmediateClearanceResult.Clear) return true;

		ClearStaleBlockedCertificate(edgeHash);
		return false;
	}

	/// Explicit player-requested measurement. Known-clear track returns from cache.
	/// Blocked, dirty, or uncached track is synchronously rescanned once and any owned graph edges are repaired to the measured state.
	internal void MeasureTrackNow(BlockPos sourcePos, Block sourceBlock, TrackPieceSpec trackSpecification, List<BlockPos> blockers)
	{
		blockers.Clear();

		int edgeCount = RailGraph.CollectSpecEdgeHashesAt(sourcePos, trackSpecification, CandidateEdgeScratch);
		if (edgeCount > 0)
		{
			bool needsScan = false;
			for (int edgeIndex = 0; edgeIndex < edgeCount; edgeIndex++)
			{
				ulong edgeHash = CandidateEdgeScratch[edgeIndex];
				if (BlockedEdges.Contains(edgeHash) || DirtyEdges.Contains(edgeHash)) { needsScan = true; break; }
			}

			if (!needsScan) return;
		}

		RailClearanceFootprint footprint = GetFootprint(sourceBlock.Id);
		int clearanceHeight = ClearanceHeightBlocksForGauge(trackSpecification.Gauge);

		for (int columnIndex = 0; columnIndex < footprint.Columns.Length; columnIndex++)
		{
			RailClearanceColumn clearanceColumn = footprint.Columns[columnIndex];
			int minY = sourcePos.Y + clearanceColumn.MinY;
			int maxY = sourcePos.Y + clearanceColumn.MaxY + clearanceHeight;

			for (int y = minY; y <= maxY; y++)
			{
				TemporaryPosition.Set(sourcePos.X + clearanceColumn.X, y, sourcePos.Z + clearanceColumn.Z);
				TemporaryPosition.dimension = sourcePos.dimension;

				if (TemporaryPosition.X == sourcePos.X && TemporaryPosition.Y == sourcePos.Y && TemporaryPosition.Z == sourcePos.Z) continue;

				Block block = ServerAPI.World.BlockAccessor.GetBlock(TemporaryPosition);
				if (IsActualBlocker(block, TemporaryPosition)) blockers.Add(TemporaryPosition.Copy());
			}
		}

		bool blocked = blockers.Count > 0;

		for (int edgeIndex = 0; edgeIndex < edgeCount; edgeIndex++)
		{
			ulong edgeHash = CandidateEdgeScratch[edgeIndex];
			bool wasBlocked = BlockedEdges.Contains(edgeHash);
			bool wasDirty = DirtyEdges.Remove(edgeHash);

			if (blocked) BlockedEdges.Add(edgeHash);
			else BlockedEdges.Remove(edgeHash);

			QueuedDirtyEdges.Remove(edgeHash);
			ScanCursorByEdge.Remove(edgeHash);
			UnregisterChunkWait(edgeHash);
			RailGraph.SetEdgeClearanceBlocked(edgeHash, blocked);

			if (wasDirty || wasBlocked != blocked)
			{
				MarkPersistenceDirty();
				EdgeResolved?.Invoke(edgeHash);
			}
		}
	}

	public bool OnServerTick(long nowMS)
	{
		if (nowMS < NextProcessMS) return false;
		NextProcessMS = nowMS + ClearanceTickMS;
		return ProcessDirtyBatch();
	}

	public void NotifyChunkColumnLoaded(Vec2i chunkCoordinates)
	{
		if (chunkCoordinates == null) return;
		LoadedChunkColumns.Add(PackChunkColumn(chunkCoordinates.X, chunkCoordinates.Y));
		// The API raises ChunkColumnLoaded immediately before installing the chunks into the live accessor.
		// Wake processing on the next server tick, after the column is queryable, rather than reading world state inside the callback.
		NextProcessMS = 0;
	}

	public void NotifyGraphChanged(RailGraphChangeSet change)
	{
		IsPersistenceDirty = true;

		if (change == null || change.GlobalInvalidation)
		{
			ScanCursorByEdge.Clear();
			ClearAllChunkWaits();
			PruneDeadEdges(BlockedEdges);
			PruneDeadEdges(DirtyEdges);
			OwnersByEdge.Clear();
			RefreshOwnerCacheFromGraph();

			foreach (RailGraphLive.ConnectionView connection in RailGraph.EnumerateConnections()) { QueueEdgeDirty(connection.Hash); }

			RebuildDirtyQueueFromSet();
			return;
		}

		foreach (ulong edgeHash in change.TouchedEdges)
		{
			if (!RailGraph.TryGetEdgeGauge(edgeHash, out _))
			{
				BlockedEdges.Remove(edgeHash);
				DirtyEdges.Remove(edgeHash);
				QueuedDirtyEdges.Remove(edgeHash);
				ScanCursorByEdge.Remove(edgeHash);
				UnregisterChunkWait(edgeHash);
				OwnersByEdge.Remove(edgeHash);
				RailGraph.SetEdgeClearanceBlocked(edgeHash, false);
				continue;
			}

			EnsureOwnerCachedFromGraph(edgeHash);
			QueueEdgeDirty(edgeHash);
		}
	}

	public void NotifyPotentialBlockChanged(BlockPos blockPosition, Block? oldBlock, Block? newBlock)
	{
		if (blockPosition == null) return;

		Vec3d center = new(blockPosition.X + 0.5, blockPosition.Y + 0.5, blockPosition.Z + 0.5);
		RailGraph.CollectEdgeCandidatesNear(center, blockPosition.dimension, ClearanceCandidateRadiusBlocks, CandidateEdgeScratch, clear: true);
		if (CandidateEdgeScratch.Count == 0) return;

		// Collision boxes and track/block checks are more expensive than hashgrid collection.
		// Only pay for them after we know the edit is near some rail edge.
		if (!IsPotentialBlocker(oldBlock, blockPosition) && !IsPotentialBlocker(newBlock, blockPosition)) return;

		for (int candidateIndex = 0; candidateIndex < CandidateEdgeScratch.Count; candidateIndex++)
		{
			ulong edgeHash = CandidateEdgeScratch[candidateIndex];
			if (ShouldDirtyEdgeForBlockChange(edgeHash, blockPosition)) { QueueEdgeDirty(edgeHash); }
		}
	}

	public void NotifyPotentialAreaChanged(BlockPos center, int radiusBlocks)
	{
		if (center == null) return;

		int radius = Math.Max(1, radiusBlocks + ClearanceCandidateRadiusBlocks + ExplosionRescanRadiusExtraBlocks);
		Vec3d worldCenter = new(center.X + 0.5, center.Y + 0.5, center.Z + 0.5);
		RailGraph.CollectEdgeCandidatesNear(worldCenter, center.dimension, radius, CandidateEdgeScratch, clear: true);

		for (int candidateIndex = 0; candidateIndex < CandidateEdgeScratch.Count; candidateIndex++) { QueueEdgeDirty(CandidateEdgeScratch[candidateIndex]); }
	}

	private void QueueEdgeDirty(ulong edgeHash)
	{
		if (!RailGraph.TryGetEdgeGauge(edgeHash, out _)) return;

		// Owner metadata is best-effort here. An edge must still be allowed to become DIRTY when its owner cannot currently be resolved.
		// The async scanner will fail closed instead of silently preserving a potentially stale CLEAR result.
		TryGetEdgeOwnerMetadata(edgeHash, out _);

		ScanCursorByEdge.Remove(edgeHash);
		UnregisterChunkWait(edgeHash);
		RailGraph.SetEdgeClearanceBlocked(edgeHash, true);

		if (DirtyEdges.Add(edgeHash)) MarkPersistenceDirty();
		EnqueueDirtyEdge(edgeHash);
	}

	private void EnqueueDirtyEdge(ulong edgeHash)
	{
		if (!DirtyEdges.Contains(edgeHash) || !QueuedDirtyEdges.Add(edgeHash)) return;
		DirtyEdgeQueue.Enqueue(edgeHash);
	}

	private bool ProcessDirtyBatch()
	{
		WakeLoadedChunkColumns();

		bool stateChanged = false;
		int blockReads = 0;
		int dequeueBudget = Math.Min(DirtyEdgeQueue.Count, MaxEdgeRechecksPerBatch);

		while (DirtyEdgeQueue.Count > 0 && dequeueBudget-- > 0)
		{
			ulong edgeHash = DirtyEdgeQueue.Dequeue();
			QueuedDirtyEdges.Remove(edgeHash);

			if (!DirtyEdges.Contains(edgeHash)) continue;

			if (!RailGraph.TryGetEdgeGauge(edgeHash, out _))
			{
				DirtyEdges.Remove(edgeHash);
				BlockedEdges.Remove(edgeHash);
				ScanCursorByEdge.Remove(edgeHash);
				UnregisterChunkWait(edgeHash);
				OwnersByEdge.Remove(edgeHash);
				RailGraph.SetEdgeClearanceBlocked(edgeHash, false);
				stateChanged = true;
				IsPersistenceDirty = true;
				continue;
			}

			bool wasBlocked = BlockedEdges.Contains(edgeHash);
			ClearanceScanStatus status = RecomputeEdgeBlocked(edgeHash, ref blockReads, out bool blocked, out ClearanceChunkWait chunkWait);

			if (status == ClearanceScanStatus.WaitingForChunk) { RegisterChunkWait(edgeHash, in chunkWait); continue; }

			if (status == ClearanceScanStatus.BudgetExhausted)
			{
				EnqueueDirtyEdge(edgeHash);
				if (blockReads >= MaxBlockReadsPerBatch) break;
				continue;
			}

			if (blocked) BlockedEdges.Add(edgeHash);
			else BlockedEdges.Remove(edgeHash);

			if (blocked != wasBlocked) stateChanged = true;
			RailGraph.SetEdgeClearanceBlocked(edgeHash, blocked);
			DirtyEdges.Remove(edgeHash);
			MarkPersistenceDirty(); // dirty-to-clean is persisted even when blocked did not change
			EdgeResolved?.Invoke(edgeHash);
		}

		return stateChanged;
	}

	private ClearanceScanStatus RecomputeEdgeBlocked(ulong edgeHash, ref int blockReads, out bool blocked, out ClearanceChunkWait chunkWait)
	{
		blocked = false;
		chunkWait = default;

		LiveOwnerResolveResult ownerResult = ResolveEdgeOwnerForScan(edgeHash, out EdgeOwner owner, out chunkWait);
		if (ownerResult == LiveOwnerResolveResult.ChunkUnavailable) { return ClearanceScanStatus.WaitingForChunk; }

		if (ownerResult == LiveOwnerResolveResult.Incompatible)
		{
			// Loaded physical state cannot prove that the persisted edge still belongs to its recorded owner.
			// This is genuine graph incompatibility, not a stale numeric BlockId, so resolve DIRTY fail-closed as BLOCKED.
			blocked = true;
			ScanCursorByEdge.Remove(edgeHash);
			return ClearanceScanStatus.Complete;
		}

		RailClearanceFootprint footprint = GetFootprint(owner.BlockID);
		int clearanceHeight = ClearanceHeightBlocksForGauge(owner.Gauge);
		// Large footprints may exceed one batch. Resume at the exact unread cell instead of restarting from column zero and permanently starving the tail of the scan.
		bool hasCursor = ScanCursorByEdge.TryGetValue(edgeHash, out ClearanceScanCursor cursor);
		int startColumn = hasCursor ? Math.Clamp(cursor.ColumnIndex, 0, footprint.Columns.Length) : 0;
		int lastChunkX = int.MinValue; int lastChunkY = int.MinValue; int lastChunkZ = int.MinValue;
		int chunkSize = GlobalConstants.ChunkSize;

		for (int columnIndex = startColumn; columnIndex < footprint.Columns.Length; columnIndex++)
		{
			RailClearanceColumn clearanceColumn = footprint.Columns[columnIndex];
			int minY = owner.Y + clearanceColumn.MinY;
			int maxY = owner.Y + clearanceColumn.MaxY + clearanceHeight;
			int startY = columnIndex == startColumn && hasCursor ? Math.Max(minY, cursor.NextY) : minY;

			for (int y = startY; y <= maxY; y++)
			{
				if (blockReads >= MaxBlockReadsPerBatch)
				{
					ScanCursorByEdge[edgeHash] = new ClearanceScanCursor(columnIndex, y);
					return ClearanceScanStatus.BudgetExhausted;
				}

				TemporaryPosition.Set(owner.X + clearanceColumn.X, y, owner.Z + clearanceColumn.Z);
				TemporaryPosition.dimension = owner.Dimension;

				int chunkX = (int)Math.Floor(TemporaryPosition.X / (double)chunkSize);
				int chunkY = (int)Math.Floor(TemporaryPosition.InternalY / (double)chunkSize);
				int chunkZ = (int)Math.Floor(TemporaryPosition.Z / (double)chunkSize);
				if (chunkX != lastChunkX || chunkY != lastChunkY || chunkZ != lastChunkZ)
				{
					if (ServerAPI.World.BlockAccessor.GetChunkAtBlockPos(TemporaryPosition) == null)
					{
						ScanCursorByEdge[edgeHash] = new ClearanceScanCursor(columnIndex, y);
						chunkWait = ClearanceChunkWait.ForPosition(TemporaryPosition, chunkX, chunkZ);
						return ClearanceScanStatus.WaitingForChunk;
					}
					lastChunkX = chunkX;
					lastChunkY = chunkY;
					lastChunkZ = chunkZ;
				}

				Block block = ServerAPI.World.BlockAccessor.GetBlock(TemporaryPosition);
				blockReads++;

				if (TemporaryPosition.X == owner.X && TemporaryPosition.Y == owner.Y && TemporaryPosition.Z == owner.Z && TemporaryPosition.dimension == owner.Dimension) continue;
				if (IsActualBlocker(block, TemporaryPosition))
				{
					ScanCursorByEdge.Remove(edgeHash);
					blocked = true;
					return ClearanceScanStatus.Complete;
				}
			}
		}

		ScanCursorByEdge.Remove(edgeHash);
		return ClearanceScanStatus.Complete;
	}

	private ImmediateClearanceResult VerifyBlockedEdgeFresh(ulong edgeHash, ref int remainingBlockReads)
	{
		if (!TryGetEdgeOwnerMetadata(edgeHash, out EdgeOwner owner))					return ImmediateClearanceResult.Unavailable;

		BlockPos ownerPosition = owner.ToBlockPos();
		if (ServerAPI.World.BlockAccessor.GetChunkAtBlockPos(ownerPosition) == null)	return ImmediateClearanceResult.Unavailable;

		if (remainingBlockReads <= 0)													return ImmediateClearanceResult.BudgetExceeded;
		Block ownerBlock = ServerAPI.World.BlockAccessor.GetBlock(ownerPosition);
		remainingBlockReads--;

		EdgeOwner cachedOwner = owner;
		LiveOwnerResolveResult ownerResult = ValidateLiveOwner(edgeHash, in cachedOwner, ownerBlock, out owner);
		if (ownerResult == LiveOwnerResolveResult.Incompatible)							return ImmediateClearanceResult.Unavailable;

		RailClearanceFootprint footprint = GetFootprint(owner.BlockID);
		int clearanceHeight = ClearanceHeightBlocksForGauge(owner.Gauge);

		long requiredReads = 0;
		for (int columnIndex = 0; columnIndex < footprint.Columns.Length; columnIndex++)
		{
			RailClearanceColumn clearanceColumn = footprint.Columns[columnIndex];
			requiredReads += (long)clearanceColumn.MaxY + clearanceHeight - clearanceColumn.MinY + 1L;
			if (requiredReads > remainingBlockReads) return ImmediateClearanceResult.BudgetExceeded;
		}

		int lastChunkX = int.MinValue;
		int lastChunkY = int.MinValue;
		int lastChunkZ = int.MinValue;
		int chunkSize = GlobalConstants.ChunkSize;

		for (int columnIndex = 0; columnIndex < footprint.Columns.Length; columnIndex++)
		{
			RailClearanceColumn clearanceColumn = footprint.Columns[columnIndex];
			int minY = owner.Y + clearanceColumn.MinY;
			int maxY = owner.Y + clearanceColumn.MaxY + clearanceHeight;

			for (int y = minY; y <= maxY; y++)
			{
				TemporaryPosition.Set(owner.X + clearanceColumn.X, y, owner.Z + clearanceColumn.Z);
				TemporaryPosition.dimension = owner.Dimension;

				int chunkX = (int)Math.Floor(TemporaryPosition.X / (double)chunkSize);
				int chunkY = (int)Math.Floor(TemporaryPosition.InternalY / (double)chunkSize);
				int chunkZ = (int)Math.Floor(TemporaryPosition.Z / (double)chunkSize);
				if (chunkX != lastChunkX || chunkY != lastChunkY || chunkZ != lastChunkZ)
				{
					if (ServerAPI.World.BlockAccessor.GetChunkAtBlockPos(TemporaryPosition) == null) return ImmediateClearanceResult.Unavailable;
					lastChunkX = chunkX; lastChunkY = chunkY; lastChunkZ = chunkZ;
				}

				Block block = ServerAPI.World.BlockAccessor.GetBlock(TemporaryPosition);
				remainingBlockReads--;

				if (TemporaryPosition.X == owner.X && TemporaryPosition.Y == owner.Y && TemporaryPosition.Z == owner.Z && TemporaryPosition.dimension == owner.Dimension) continue;
				if (IsActualBlocker(block, TemporaryPosition)) return ImmediateClearanceResult.Blocked;
			}
		}

		return ImmediateClearanceResult.Clear;
	}

	private void ClearStaleBlockedCertificate(ulong edgeHash)
	{
		if (!BlockedEdges.Remove(edgeHash)) return;

		RailGraph.SetEdgeClearanceBlocked(edgeHash, false);
		MarkPersistenceDirty();
		EdgeResolved?.Invoke(edgeHash);
	}

	private void RegisterChunkWait(ulong edgeHash, in ClearanceChunkWait chunkWait)
	{
		UnregisterChunkWait(edgeHash);
		ChunkWaitByEdge[edgeHash] = chunkWait;

		long columnKey = chunkWait.ColumnKey;
		if (!ChunkWaitEdgesByColumn.TryGetValue(columnKey, out HashSet<ulong>? waitingEdges))
		{
			waitingEdges = new HashSet<ulong>();
			ChunkWaitEdgesByColumn[columnKey] = waitingEdges;
		}
		waitingEdges.Add(edgeHash);
	}

	private void UnregisterChunkWait(ulong edgeHash)
	{
		if (!ChunkWaitByEdge.Remove(edgeHash, out ClearanceChunkWait chunkWait)) return;
		if (!ChunkWaitEdgesByColumn.TryGetValue(chunkWait.ColumnKey, out HashSet<ulong>? waitingEdges)) return;
		waitingEdges.Remove(edgeHash);
		if (waitingEdges.Count == 0) ChunkWaitEdgesByColumn.Remove(chunkWait.ColumnKey);
	}

	private void ClearAllChunkWaits()
	{
		ChunkWaitByEdge.Clear();
		ChunkWaitEdgesByColumn.Clear();
		LoadedChunkColumns.Clear();
		ChunkWaitEdgeScratch.Clear();
	}

	private void WakeLoadedChunkColumns()
	{
		if (LoadedChunkColumns.Count == 0 || ChunkWaitEdgesByColumn.Count == 0) { LoadedChunkColumns.Clear(); return; }

		foreach (long columnKey in LoadedChunkColumns)
		{
			if (!ChunkWaitEdgesByColumn.TryGetValue(columnKey, out HashSet<ulong>? waitingEdges) || waitingEdges.Count == 0) { continue; }

			ChunkWaitEdgeScratch.Clear();
			ChunkWaitEdgeScratch.AddRange(waitingEdges);
			for (int waitingEdgeIndex = 0; waitingEdgeIndex < ChunkWaitEdgeScratch.Count; waitingEdgeIndex++)
			{
				ulong edgeHash = ChunkWaitEdgeScratch[waitingEdgeIndex];
				if (!ChunkWaitByEdge.TryGetValue(edgeHash, out ClearanceChunkWait chunkWait) || !IsWaitChunkLoaded(in chunkWait)) { continue; }

				UnregisterChunkWait(edgeHash);
				EnqueueDirtyEdge(edgeHash);
			}
		}

		ChunkWaitEdgeScratch.Clear();
		LoadedChunkColumns.Clear();
	}

	private bool IsWaitChunkLoaded(in ClearanceChunkWait chunkWait)
	{
		TemporaryChunkProbePosition.Set(chunkWait.ProbeX, chunkWait.ProbeY, chunkWait.ProbeZ);
		TemporaryChunkProbePosition.dimension = chunkWait.Dimension;
		return ServerAPI.World.BlockAccessor.GetChunkAtBlockPos(TemporaryChunkProbePosition) != null;
	}

	private static long PackChunkColumn(int chunkX, int chunkZ) { return ((long)(uint)chunkX << 32) | (uint)chunkZ; }

	private bool ShouldDirtyEdgeForBlockChange(ulong edgeHash, BlockPos changedPosition)
	{
		// Only a positively resolved current footprint may reject an edit.
		// If ownership cannot be established, dirty the edge and let the existing asynchronous scanner resolve it safely.
		if (!TryGetEdgeOwnerMetadata(edgeHash, out EdgeOwner owner)) return true;

		if (!owner.LiveValidated)
		{
			BlockPos ownerPosition = owner.ToBlockPos();
			if (ServerAPI.World.BlockAccessor.GetChunkAtBlockPos(ownerPosition) == null) return true;

			Block liveBlock = ServerAPI.World.BlockAccessor.GetBlock(ownerPosition);
			EdgeOwner cachedOwner = owner;
			if (ValidateLiveOwner(edgeHash, in cachedOwner, liveBlock, out owner) == LiveOwnerResolveResult.Incompatible) { return true; }
		}

		RailClearanceFootprint footprint = GetFootprint(owner.BlockID);
		int relativeX = changedPosition.X - owner.X;
		int relativeZ = changedPosition.Z - owner.Z;
		int relativeY = changedPosition.Y - owner.Y;

		return footprint.TryGetColumn(relativeX, relativeZ, out RailClearanceColumn clearanceColumn) &&
			relativeY >= clearanceColumn.MinY &&
			relativeY <= clearanceColumn.MaxY + ClearanceHeightBlocksForGauge(owner.Gauge);
	}

	private bool TryGetEdgeOwnerMetadata(ulong edgeHash, out EdgeOwner owner)
	{
		if (OwnersByEdge.TryGetValue(edgeHash, out owner)) return true;
		return EnsureOwnerCachedFromGraph(edgeHash, out owner);
	}

	private bool EnsureOwnerCachedFromGraph(ulong edgeHash) { return EnsureOwnerCachedFromGraph(edgeHash, out _); }

	private bool EnsureOwnerCachedFromGraph(ulong edgeHash, out EdgeOwner owner)
	{
		if (!RailGraph.TryGetEdgeOwner(edgeHash, out BlockPos ownerPosition, out int ownerBlockID, out byte gauge)) { owner = default; return false; }

		// Persisted owner IDs are deliberately untrusted until this server session physically validates the live track at the recorded owner position.
		owner = new EdgeOwner(ownerPosition.X, ownerPosition.Y, ownerPosition.Z, ownerPosition.dimension, ownerBlockID, gauge, liveValidated: false);
		OwnersByEdge[edgeHash] = owner;
		return true;
	}

	private LiveOwnerResolveResult ResolveEdgeOwnerForScan(ulong edgeHash, out EdgeOwner owner, out ClearanceChunkWait chunkWait)
	{
		chunkWait = default;
		if (!TryGetEdgeOwnerMetadata(edgeHash, out owner)) return LiveOwnerResolveResult.Incompatible;

		BlockPos ownerPosition = owner.ToBlockPos();
		if (ServerAPI.World.BlockAccessor.GetChunkAtBlockPos(ownerPosition) == null)
		{
			int chunkSize = GlobalConstants.ChunkSize;
			int chunkX = (int)Math.Floor(ownerPosition.X / (double)chunkSize);
			int chunkZ = (int)Math.Floor(ownerPosition.Z / (double)chunkSize);
			chunkWait = ClearanceChunkWait.ForPosition(ownerPosition, chunkX, chunkZ);
			return LiveOwnerResolveResult.ChunkUnavailable;
		}

		Block liveBlock = ServerAPI.World.BlockAccessor.GetBlock(ownerPosition);
		EdgeOwner cachedOwner = owner;
		return ValidateLiveOwner(edgeHash, in cachedOwner, liveBlock, out owner);
	}

	/// Proves physical ownership geometrically. Numeric BlockId equality is deliberately not part of identity,
	/// registry IDs may shift between mod versions while owner position, gauge, and edge geometry remain unchanged.
	private LiveOwnerResolveResult ValidateLiveOwner(ulong edgeHash, in EdgeOwner cachedOwner, Block liveBlock, out EdgeOwner resolvedOwner)
	{
		resolvedOwner = cachedOwner;
		if (!TrackSpecsDictionary.TryGet(liveBlock, out TrackPieceSpec trackSpecification) || trackSpecification.Gauge != cachedOwner.Gauge)
		{
			return LiveOwnerResolveResult.Incompatible;
		}

		BlockPos ownerPosition = cachedOwner.ToBlockPos();
		if (!RailGraph.ContainsSpecEdgeAt(ownerPosition, trackSpecification, edgeHash)) return LiveOwnerResolveResult.Incompatible;

		int liveBlockID = liveBlock.BlockId;
		if (liveBlockID <= 0) return LiveOwnerResolveResult.Incompatible;

		if (!RailGraph.TryRebindEdgeOwnerBlockID(edgeHash, ownerPosition, cachedOwner.Gauge, liveBlockID, out bool graphMetadataChanged))
		{
			return LiveOwnerResolveResult.Incompatible;
		}

		if (graphMetadataChanged) MarkPersistenceDirty();

		resolvedOwner = new EdgeOwner(cachedOwner.X, cachedOwner.Y, cachedOwner.Z, cachedOwner.Dimension, liveBlockID, cachedOwner.Gauge, liveValidated: true);

		OwnersByEdge[edgeHash] = resolvedOwner;
		return LiveOwnerResolveResult.Valid;
	}

	private RailClearanceFootprint GetFootprint(int blockID)
	{
		if (blockID <= 0) return RailClearanceFootprint.AnchorOnly;
		if (!FootprintByBlockID.TryGetValue(blockID, out RailClearanceFootprint footprint))
		{
			footprint = RailClearanceFootprint.ForBlock(ServerAPI.World.GetBlock(blockID));
			FootprintByBlockID[blockID] = footprint;
		}

		return footprint;
	}

	private static int ClearanceHeightBlocksForGauge(byte gauge) { return gauge == 0 ? MinecartClearanceHeightBlocks : StandardGaugeClearanceHeightBlocks; }

	private bool IsPotentialBlocker(Block? block, BlockPos blockPosition)
	{
		if (block == null || block.BlockId == 0) return false;
		if (TrackSpecsDictionary.IsTrack(block)) return false;
		if (block.Code?.Domain == "collisionflowfields") return false;

		Cuboidf[] boxes = block.GetCollisionBoxes(ServerAPI.World.BlockAccessor, blockPosition);
		return boxes != null && boxes.Length > 0;
	}

	private bool IsActualBlocker(Block block, BlockPos blockPosition) { return IsPotentialBlocker(block, blockPosition); }

	private void PruneDeadEdges(HashSet<ulong> edgeSet)
	{
		if (edgeSet.Count == 0) return;

		EdgeSetPruneScratch.Clear();
		foreach (ulong edgeHash in edgeSet) { if (!RailGraph.TryGetEdgeGauge(edgeHash, out _)) EdgeSetPruneScratch.Add(edgeHash); }

		for (int edgeIndex = 0; edgeIndex < EdgeSetPruneScratch.Count; edgeIndex++) edgeSet.Remove(EdgeSetPruneScratch[edgeIndex]);
		EdgeSetPruneScratch.Clear();
	}

	private void RefreshOwnerCacheFromGraph()
	{
		foreach (RailGraphLive.ConnectionView connection in RailGraph.EnumerateConnections())
		{
			if (!OwnersByEdge.ContainsKey(connection.Hash)) EnsureOwnerCachedFromGraph(connection.Hash);
		}
	}

	private void RebuildDirtyQueueFromSet()
	{
		DirtyEdgeQueue.Clear();
		QueuedDirtyEdges.Clear();
		foreach (ulong edgeHash in DirtyEdges) { if (RailGraph.TryGetEdgeGauge(edgeHash, out _)) EnqueueDirtyEdge(edgeHash); }
	}

	private enum ClearanceScanStatus : byte
	{
		Complete,
		BudgetExhausted,
		WaitingForChunk
	}

	private enum LiveOwnerResolveResult : byte
	{
		Valid,
		ChunkUnavailable,
		Incompatible
	}

	private enum ImmediateClearanceResult : byte
	{
		Clear,
		Blocked,
		Unavailable,
		BudgetExceeded
	}

	private readonly struct ClearanceChunkWait
	{
		public readonly int Dimension;
		public readonly int ChunkX, ChunkZ;
		public readonly int ProbeX, ProbeY, ProbeZ;
		private readonly bool ValidState;

		public bool IsValid => ValidState;
		public long ColumnKey => PackChunkColumn(ChunkX, ChunkZ);

		private ClearanceChunkWait(int dimension, int chunkX, int chunkZ, int probeX, int probeY, int probeZ)
		{
			Dimension = dimension;
			ChunkX = chunkX; ChunkZ = chunkZ;
			ProbeX = probeX; ProbeY = probeY; ProbeZ = probeZ;
			ValidState = true;
		}

		public static ClearanceChunkWait ForPosition(BlockPos blockPosition, int chunkX, int chunkZ)
		{
			return new ClearanceChunkWait(blockPosition.dimension, chunkX, chunkZ, blockPosition.X, blockPosition.Y, blockPosition.Z);
		}
	}

	private readonly struct ClearanceScanCursor
	{
		public readonly int ColumnIndex;
		public readonly int NextY;

		public ClearanceScanCursor(int columnIndex, int nextY)
		{
			ColumnIndex = columnIndex;
			NextY = nextY;
		}
	}

	private readonly struct EdgeOwner
	{
		public readonly int X;
		public readonly int Y;
		public readonly int Z;
		public readonly int Dimension;
		public readonly int BlockID;
		public readonly byte Gauge;
		public readonly bool LiveValidated;

		public EdgeOwner(int x, int y, int z, int dimension, int blockID, byte gauge, bool liveValidated)
		{
			X = x; Y = y; Z = z;
			Dimension = dimension;
			BlockID = blockID; Gauge = gauge;
			LiveValidated = liveValidated;
		}

		public BlockPos ToBlockPos() { return new BlockPos(X, Y, Z, Dimension); }
	}
}
#endregion

#region Footprint Data
internal readonly struct RailClearanceColumn
{
	public readonly int X;
	public readonly int Z;
	public readonly int MinY;
	public readonly int MaxY;

	public RailClearanceColumn(int x, int z, int minY, int maxY)
	{
		X = x;
		Z = z;
		MinY = minY;
		MaxY = maxY;
	}
}

/// Compact X/Z-column view of a collision flow field footprint. The clearance layer only needs coarse vertical columns, not direction data or exact h8 granularity.
internal sealed class RailClearanceFootprint
{
	public static readonly RailClearanceFootprint AnchorOnly = new(new[] { new RailClearanceColumn(0, 0, 0, 0) });

	private readonly Dictionary<long, RailClearanceColumn> ColumnByXZ;

	public RailClearanceColumn[] Columns { get; }

	private RailClearanceFootprint(RailClearanceColumn[] columns)
	{
		Columns = columns ?? Array.Empty<RailClearanceColumn>();
		ColumnByXZ = new Dictionary<long, RailClearanceColumn>(Columns.Length);
		for (int columnIndex = 0; columnIndex < Columns.Length; columnIndex++)
		{
			RailClearanceColumn clearanceColumn = Columns[columnIndex];
			ColumnByXZ[CoordinateKey(clearanceColumn.X, clearanceColumn.Z)] = clearanceColumn;
		}
	}

	public bool TryGetColumn(int relativeX, int relativeZ, out RailClearanceColumn column)
	{
		return ColumnByXZ.TryGetValue(CoordinateKey(relativeX, relativeZ), out column);
	}

	public static RailClearanceFootprint ForBlock(Block block)
	{
		JsonObject? flowFieldAttribute = block?.Attributes?["collisionFlowField"];
		JsonObject[]? flowFieldRows = SelectFlowFieldArray(flowFieldAttribute, block);
		JsonObject? exclusionAttribute = block?.Attributes?["ClearanceExclude"];
		JsonObject[]? exclusions = exclusionAttribute != null && exclusionAttribute.Exists && exclusionAttribute.IsArray() ? exclusionAttribute.AsArray() : null;

		if ((flowFieldRows == null || flowFieldRows.Length == 0) && (exclusions == null || exclusions.Length == 0)) { return AnchorOnly; }
		Dictionary<long, MutableColumn> columnsByCoordinate = new();
		AddCell(columnsByCoordinate, 0, 0, 0);

		if (flowFieldRows != null)
		{
			for (int rowIndex = 0; rowIndex < flowFieldRows.Length; rowIndex++)
			{
				int[]? cellPosition = flowFieldRows[rowIndex]["pos"].AsArray<int>(null) ?? flowFieldRows[rowIndex]["offset"].AsArray<int>(null);
				if (cellPosition == null || cellPosition.Length < 3) continue;
				AddCell(columnsByCoordinate, cellPosition[0], cellPosition[1], cellPosition[2]);
			}
		}

		if (exclusions != null)
		{
			for (int exclusionIndex = 0; exclusionIndex < exclusions.Length; exclusionIndex++)
			{
				JsonObject[]? excludedCoordinates = exclusions[exclusionIndex].AsArray();
				if (excludedCoordinates == null || excludedCoordinates.Length != 2) continue;

				int x = excludedCoordinates[0].AsInt(int.MinValue);
				int z = excludedCoordinates[1].AsInt(int.MinValue);
				if (x == int.MinValue || z == int.MinValue) continue;

				columnsByCoordinate.Remove(CoordinateKey(x, z));
			}
		}

		RailClearanceColumn[] compactColumns = new RailClearanceColumn[columnsByCoordinate.Count];
		int columnIndex = 0;
		foreach (MutableColumn mutableColumn in columnsByCoordinate.Values)
		{
			compactColumns[columnIndex++] = new RailClearanceColumn(mutableColumn.X, mutableColumn.Z, mutableColumn.MinY, mutableColumn.MaxY);
		}

		return new RailClearanceFootprint(compactColumns);
	}

	private static void AddCell(Dictionary<long, MutableColumn> columnsByCoordinate, int x, int y, int z)
	{
		long coordinateKey = CoordinateKey(x, z);
		if (columnsByCoordinate.TryGetValue(coordinateKey, out MutableColumn column))
		{
			if (y < column.MinY) column.MinY = y;
			if (y > column.MaxY) column.MaxY = y;
			columnsByCoordinate[coordinateKey] = column;
			return;
		}

		columnsByCoordinate[coordinateKey] = new MutableColumn { X = x, Z = z, MinY = y, MaxY = y };
	}

	private static JsonObject[]? SelectFlowFieldArray(JsonObject? flowFieldRoot, Block? block)
	{
		if (flowFieldRoot == null || !flowFieldRoot.Exists || block == null || block.Code == null) return null;
		if (flowFieldRoot.IsArray()) return flowFieldRoot.AsArray();

		JsonObject selected = flowFieldRoot[block.Code.ToString()];
		if (!selected.Exists) selected = flowFieldRoot[block.Code.Path];
		if (!selected.Exists) selected = flowFieldRoot["*"];

		return selected.Exists && selected.IsArray() ? selected.AsArray() : null;
	}

	private static long CoordinateKey(int x, int z) { return ((long)x << 32) ^ (uint)z; }

	private struct MutableColumn
	{
		public int X;
		public int Z;
		public int MinY;
		public int MaxY;
	}
}
#endregion
