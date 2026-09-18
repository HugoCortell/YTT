using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace YangTransport;

/// Incremental, always-live graph for the entire railway network.
/// Truth is derived from Atomic edges keyed by stable geometry hash (ulong), Endpoint adjacency (for local queries), and Heavy polyline points (xyz16) keyed by same hash
/// We cache maximal segments between nodes (degree != 2 endpoints) on demand, invalidate locally, and store them as ordered edge-hash chains without polyline duplication.
internal sealed class RailGraphLive
{
	private readonly Dictionary<ulong, LightweightEdge> EdgesByHash = new();
	private readonly Dictionary<EndpointKey, List<ulong>> EdgesByEndpoint = new();
	private readonly HashSet<HeavySegment> HeavySegments = new(HeavySegment.HashComparer.Instance);

	// Spatial index for atomic edges (XZ hashgrid, ignores Y)
	private readonly HashGrid2D EdgeGrid = new(cellSizeBlocks: 64);

	// Segment cache: (start node endpoint, outgoing edge) --> maximal segment chain
	private readonly Dictionary<SegmentKey, MaximalSegment> SegmentCache = new();

	// Occupancy zones are committed graph state. Zone IDs remain valid until a synchronous topology transaction reassigns the affected component.
	private readonly Dictionary<ulong, HashSet<ulong>> EdgesByZone = new();
	private readonly Dictionary<ulong, HashSet<long>> OccupantsByZone = new();
	private ulong NextZoneID = 1;

	// Signal endpoints (midpoints inside special signal track pieces). Signals are metadata only.
	private readonly Dictionary<EndpointKey, SignalEntry> Signals = new();
	internal Dictionary<EndpointKey, SignalEntry> SignalsUnsafe => Signals; // Used for signal lights

	// Reused transaction scratch. All graph mutations execute on the server thread.
	private readonly HashSet<ulong> ZoneSetScratch = new();
	private readonly HashSet<ulong> ZoneEdgeSetScratch = new();
	private readonly List<ulong> ZoneWork = new();
	private readonly Dictionary<ulong, ulong> PreviousZoneByEdgeScratch = new();
	private readonly Dictionary<ulong, int> PreviousZoneSizeScratch = new();
	private readonly List<ZoneComponent> ZoneComponentsScratch = new();

	// Scratch buffers for local rebuild (server-only dev tool).
	private readonly List<ulong> RebuildWork = new(1024);
	private readonly List<EndpointKey> RebuildSignalWork = new(256);

	private const int MaxSavedEdges							= 10_000_000;
	private const int MaxSavedSignals						= 1_000_000;
	private const int MaxSavedOccupiedZones					= 10_000_000;
	private const int MaxSavedPointsPerEdge					= 4096;
	private const long MaxSavedAggregatePoints				= 100_000_000;
	private const int MinSavedEdgeBytes						= 65;
	private const int SavedSignalBytes						= 42;
	private const int SavedSignalContributorBytes			= 21;
	private const int MaxSignalContributorsPerEndpoint		= 4096;

	internal readonly record struct SignalContributor(int OwnerX, int OwnerY, int OwnerZ, SignalKind Kind, ulong AllowedFromEdgeHash);

	internal struct SignalEntry
	{
		// Aggregate fields stay hot-path friendly. Contributors are only touched by topology mutation, persistence, and crash recovery.
		public SignalKind Kind;
		public int ReferenceCount;
		public ulong AllowedFromEdgeHash; // Only used for OneWay/Chain signals
		public int OwnerX, OwnerY, OwnerZ; // Representative (most recently added) contributor.
		public List<SignalContributor>? Contributors;
	}

	public int BuildVersion { get; private set; } = 1;
	public uint LoadedSpecificationsHash { get; private set; }

	public int NodeCount => EdgesByEndpoint.Count;
	public int ConnectionCount => EdgesByHash.Count;

	/// Lightweight view of an edge.
	public readonly struct ConnectionView
	{
		public readonly int ConnectionID;
		public readonly int NodeAID;
		public readonly int NodeBID;
		public readonly byte Gauge;
		public readonly bool Occupied;
		public readonly ulong Hash;

		public ConnectionView(int connectionID, int nodeAID, int nodeBID, byte gauge, bool occupied, ulong hash)
		{
			this.ConnectionID = connectionID; NodeAID = nodeAID; NodeBID = nodeBID; Gauge = gauge; Occupied = occupied; Hash = hash;
		}
	}

	public IEnumerable<ConnectionView> EnumerateConnections()
	{
		foreach (var edgePair in EdgesByHash)
		{
			var edge = edgePair.Value;
			yield return new ConnectionView(HashToIntegerID(edgePair.Key), 0, 0, edge.Gauge, edge.Occupied, edgePair.Key);
		}
	}

	public bool TryGetPolyline16(ulong hash, out int[] quantizedCoordinates)
	{
		quantizedCoordinates = Array.Empty<int>();
		if (!TryGetHeavy(hash, out var heavySegment)) return false;
		quantizedCoordinates = heavySegment.QuantizedCoordinates;
		return quantizedCoordinates != null && quantizedCoordinates.Length >= 6;
	}

	public int EdgeGridCellSize => EdgeGrid.CellSize;
	public int EdgeGridCellShift => EdgeGrid.CellShift;

	public int CollectEdgeCandidatesNear(Vec3d worldPosition, int dimension, int radiusBlocks, List<ulong> destination, bool clear = true)
	{
		int blockX = (int)Math.Floor(worldPosition.X);
		int blockZ = (int)Math.Floor(worldPosition.Z);
		return EdgeGrid.CollectNear(dimension, blockX, blockZ, radiusBlocks, destination, clear);
	}

	public bool TryGetEdgeGauge(ulong hash, out byte gauge)
	{
		if (!EdgesByHash.TryGetValue(hash, out var edge)) { gauge = 0; return false; }
		gauge = edge.Gauge;
		return true;
	}

	internal bool ContainsEdge(ulong hash) => hash != 0 && EdgesByHash.ContainsKey(hash);

	public bool TryGetNormalizedEdgeMaxSpeedFactor(ulong hash, out float normalizedSpeedFactor)
	{
		if (!EdgesByHash.TryGetValue(hash, out var edge)) { normalizedSpeedFactor = 1f; return false; }
		normalizedSpeedFactor = edge.QuantizedMaxSpeedFactor / 255f;
		return true;
	}

	public bool TryGetEdgeMaterialSpeedCapBPS(ulong hash, out ushort speedCapBPS)
	{
		if (!EdgesByHash.TryGetValue(hash, out var edge)) { speedCapBPS = 0; return false; }
		speedCapBPS = edge.MaterialSpeedCapBPS;
		return true;
	}

	internal bool TryGetEdgeOwner(ulong hash, out BlockPos ownerPosition, out int ownerBlockID, out byte gauge)
	{
		if (!EdgesByHash.TryGetValue(hash, out var edge) || edge.OwnerBlockID <= 0)
		{
			ownerPosition = null!;
			ownerBlockID = 0;
			gauge = 0;
			return false;
		}

		ownerPosition = new BlockPos(edge.OwnerX, edge.OwnerY, edge.OwnerZ, edge.FirstEndpoint.Dimension);
		ownerBlockID = edge.OwnerBlockID;
		gauge = edge.Gauge;
		return true;
	}

	/// Rebinds only the persisted/runtime owner block-id hint for an existing edge. Owner position, gauge, geometry, topology, occupancy, and BuildVersion are untouched.
	/// The caller must already have proven that the live track at expectedOwnerPos producesthis exact geometric edge.
	internal bool TryRebindEdgeOwnerBlockID(ulong hash, BlockPos expectedOwnerPosition, byte expectedGauge, int newBlockID, out bool changed)
	{
		changed = false;
		if (expectedOwnerPosition == null || newBlockID <= 0) return false;
		if (!EdgesByHash.TryGetValue(hash, out LightweightEdge edge)) return false;

		if 
		(
			edge.Gauge != expectedGauge ||
			edge.FirstEndpoint.Dimension != expectedOwnerPosition.dimension || edge.SecondEndpoint.Dimension != expectedOwnerPosition.dimension ||
			edge.OwnerX != expectedOwnerPosition.X || edge.OwnerY != expectedOwnerPosition.Y || edge.OwnerZ != expectedOwnerPosition.Z
		) { return false; }

		if (edge.OwnerBlockID == newBlockID) return true;

		edge.OwnerBlockID = newBlockID;
		EdgesByHash[hash] = edge;
		changed = true;
		return true;
	}

	internal bool IsEdgeClearanceBlocked(ulong hash) { return EdgesByHash.TryGetValue(hash, out var edge) && edge.ClearanceBlocked; }

	internal void SetEdgeClearanceBlocked(ulong hash, bool blocked)
	{
		if (!EdgesByHash.TryGetValue(hash, out var edge)) return;
		if (edge.ClearanceBlocked == blocked) return;
		edge.ClearanceBlocked = blocked;
		EdgesByHash[hash] = edge;
	}

	internal void ClearEdgeClearanceState()
	{
		if (EdgesByHash.Count == 0) return;
		var keys = new List<ulong>(EdgesByHash.Keys);
		for (int keyIndex = 0; keyIndex < keys.Count; keyIndex++)
		{
			ulong hash = keys[keyIndex];
			LightweightEdge edge = EdgesByHash[hash];
			if (!edge.ClearanceBlocked) continue;
			edge.ClearanceBlocked = false;
			EdgesByHash[hash] = edge;
		}
	}

	internal bool TryGetEdgeEndpoints(ulong hash, out EndpointKey firstEndpoint, out EndpointKey secondEndpoint, out byte gauge)
	{
		if (!EdgesByHash.TryGetValue(hash, out var edge)) { firstEndpoint = default; secondEndpoint = default; gauge = 0; return false; }
		firstEndpoint = edge.FirstEndpoint; secondEndpoint = edge.SecondEndpoint; gauge = edge.Gauge;
		return true;
	}


	public bool TryGetEdgeCell(int dimension, int cellX, int cellZ, out List<ulong> edgeIDs)
		=> EdgeGrid.TryGetCell(dimension, cellX, cellZ, out edgeIDs);

	public bool TryGetEdgeCellAtBlock(int dimension, int blockX, int blockZ, out List<ulong> edgeIDs)
		=> EdgeGrid.TryGetCellAtBlock(dimension, blockX, blockZ, out edgeIDs);

	internal bool ContainsSpecEdgeAt(BlockPos anchorPosition, TrackPieceSpec trackSpecification, ulong edgeHash = 0)
	{
		if (anchorPosition == null || trackSpecification == null || trackSpecification.Paths == null) return false;

		byte gauge = trackSpecification.Gauge;

		for (int pathIndex = 0; pathIndex < trackSpecification.Paths.Length; pathIndex++)
		{
			TrackPath path = trackSpecification.Paths[pathIndex];
			Vec3f[] points = path.LocalPoints;
			if (points == null || points.Length < 2) continue;

			int[] quantizedCoordinates = QuantizeWorldPolyline16(anchorPosition, points);
			ulong hash = HashUtility.HashSegment(gauge, quantizedCoordinates);

			if (edgeHash != 0 && hash != edgeHash) continue;
			if (!EdgesByHash.TryGetValue(hash, out var edge)) continue;
			if (edge.Gauge == gauge && edge.FirstEndpoint.Dimension == anchorPosition.dimension) return true;
		}

		return false;
	}

	internal int CollectSpecEdgeHashesAt(BlockPos anchorPosition, TrackPieceSpec trackSpecification, List<ulong> destination)
	{
		if (destination == null) throw new ArgumentNullException(nameof(destination));
		destination.Clear();
		if (anchorPosition == null || trackSpecification == null || trackSpecification.Paths == null) return 0;

		byte gauge = trackSpecification.Gauge;
		for (int pathIndex = 0; pathIndex < trackSpecification.Paths.Length; pathIndex++)
		{
			TrackPath path = trackSpecification.Paths[pathIndex];
			Vec3f[] points = path.LocalPoints;
			if (points == null || points.Length < 2) continue;

			int[] quantizedCoordinates = QuantizeWorldPolyline16(anchorPosition, points);
			ulong hash = HashUtility.HashSegment(gauge, quantizedCoordinates);
			if
			(
				!EdgesByHash.TryGetValue(hash, out LightweightEdge edge) || edge.Gauge != gauge ||
				edge.FirstEndpoint.Dimension != anchorPosition.dimension || destination.Contains(hash)
			) { continue; }

			destination.Add(hash);
		}

		return destination.Count;
	}

	public void Clear()
	{
		EdgesByHash.Clear();
		EdgesByEndpoint.Clear();
		HeavySegments.Clear();
		EdgeGrid.Clear();
		SegmentCache.Clear();
		EdgesByZone.Clear();
		OccupantsByZone.Clear();
		Signals.Clear();
		ZoneSetScratch.Clear();
		ZoneEdgeSetScratch.Clear();
		ZoneWork.Clear();
		PreviousZoneByEdgeScratch.Clear();
		PreviousZoneSizeScratch.Clear();
		ZoneComponentsScratch.Clear();
		NextZoneID = 1;
		BuildVersion = 1;
		LoadedSpecificationsHash = 0;
	}

	public void BumpVersion() { BuildVersion++; }

	/// Crash-recovery helper. Removes every persisted graph contribution produced by one of the supplied physical owner positions without requiring the old TrackSpec.
	/// We scan the graph once instead of maintaining another hot-path index that'd ruin performance.
	internal bool RemoveOwnerContributions(HashSet<RailOwnerKey> owners, RailGraphChangeSet change)
	{
		if (owners == null || owners.Count == 0) return false;

		bool changed = false;

		RebuildSignalWork.Clear();
		foreach (KeyValuePair<EndpointKey, SignalEntry> pair in Signals)
		{
			List<SignalContributor>? contributors = pair.Value.Contributors;
			if (contributors == null) continue;

			for (int contributorIndex = 0; contributorIndex < contributors.Count; contributorIndex++)
			{
				SignalContributor contributor = contributors[contributorIndex];
				if (!owners.Contains(new RailOwnerKey(contributor.OwnerX, contributor.OwnerY, contributor.OwnerZ))) continue;

				RebuildSignalWork.Add(pair.Key);
				break;
			}
		}

		for (int signalIndex = 0; signalIndex < RebuildSignalWork.Count; signalIndex++)
		{
			EndpointKey endpoint = RebuildSignalWork[signalIndex];
			if (!Signals.TryGetValue(endpoint, out SignalEntry signal) || signal.Contributors == null) continue;

			int removed = 0;
			for (int contributorIndex = signal.Contributors.Count - 1; contributorIndex >= 0; contributorIndex--)
			{
				SignalContributor contributor = signal.Contributors[contributorIndex];
				if (!owners.Contains(new RailOwnerKey(contributor.OwnerX, contributor.OwnerY, contributor.OwnerZ))) continue;

				signal.Contributors.RemoveAt(contributorIndex);
				removed++;
			}
			if (removed == 0) continue;

			TouchEndpointAndIncidentEdges(endpoint, change);
			change?.TouchSignal(endpoint);
			if (signal.Contributors.Count == 0)
			{
				Signals.Remove(endpoint);
				InvalidateAround(endpoint);
			}
			else
			{
				RebuildSignalAggregate(ref signal);
				Signals[endpoint] = signal;
			}
			changed = true;
		}

		RebuildWork.Clear();
		foreach (KeyValuePair<ulong, LightweightEdge> pair in EdgesByHash)
		{
			LightweightEdge edge = pair.Value;
			if (owners.Contains(new RailOwnerKey(edge.OwnerX, edge.OwnerY, edge.OwnerZ))) { RebuildWork.Add(pair.Key); }
		}

		for (int edgeIndex = 0; edgeIndex < RebuildWork.Count; edgeIndex++) { if (RemoveEdge(RebuildWork[edgeIndex], change)) changed = true; }

		return changed;
	}

	/// Incremental updates (track blocks)
	public bool AddTrackBlock(Block block, BlockPos position, RailGraphChangeSet? change = null)
	{
		if (!TrackSpecsDictionary.TryGet(block, out TrackPieceSpec trackSpecification)) return false;
		ushort materialSpeedCapBPS = TrackSpecsDictionary.GetMaterialSpeedCapBPS(block, trackSpecification.Gauge);
		return AddTrackSpecification(block, trackSpecification, position, materialSpeedCapBPS, change);
	}

	public bool RemoveTrackBlock(Block block, BlockPos position, RailGraphChangeSet? change = null)
	{
		if (!TrackSpecsDictionary.TryGet(block, out TrackPieceSpec trackSpecification)) return false;
		return RemoveTrackSpecification(trackSpecification, position, change);
	}


	/// Rebuild helpers. Devtool, repairs a local core box while scanning a padded anchor box.
	/// Large pieces (wide turns/forks/slopes) can intersect the core even when their anchor block is outside it,
	/// so the scan volume is expanded by the largest registered spec reach.

	// true if any edges/signals were added/updated
	internal bool RebuildArea(IBlockAccessor blockAccessor, BlockPos center, int radius, int verticalRange, RailGraphChangeSet? change = null)
	{
		if (blockAccessor == null) throw new ArgumentNullException(nameof(blockAccessor));

		bool changed = false;
		int dimension = center.dimension;

		TrackSpecsDictionary.GetMaxSpecReach(out int horizontalPadding, out int verticalPadding);
		int scanRadius = radius + horizontalPadding;
		int verticalScanRange = verticalRange + verticalPadding;

		// Discard stale graph data only inside the requested repair core.
		if (RemoveEdgesInBox(center, radius, verticalRange, change)) changed = true;

		var blockPosition = new BlockPos(0, 0, 0, dimension);
		for (int dy = -verticalScanRange; dy <= verticalScanRange; dy++)
		{
			int y = center.Y + dy;
			for (int dx = -scanRadius; dx <= scanRadius; dx++)
			{
				int x = center.X + dx;
				for (int dz = -scanRadius; dz <= scanRadius; dz++)
				{
					int z = center.Z + dz;
					blockPosition.Set(x, y, z);

					Block block = blockAccessor.GetBlock(blockPosition);
					if (!TrackSpecsDictionary.TryGet(block, out TrackPieceSpec trackSpecification)) continue;
					if (!TrackSpecificationIntersectsBox(trackSpecification, blockPosition, center, radius, verticalRange)) continue;

					// Reset the whole contributing piece, not just the core-intersecting edge.
					// This prevents signal refcount drift when the padded scan sees existing signals
					// whose midpoint is outside the core, and keeps multi-path pieces consistent.
					if (RemoveTrackSpecification(trackSpecification, blockPosition, change)) changed = true;
					ushort materialSpeedCapBPS = TrackSpecsDictionary.GetMaterialSpeedCapBPS(block, trackSpecification.Gauge);
					if (AddTrackSpecification(block, trackSpecification, blockPosition, materialSpeedCapBPS, change)) changed = true;
				}
			}
		}

		return changed;
	}

	private static bool TrackSpecificationIntersectsBox(TrackPieceSpec trackSpecification, BlockPos anchorPosition, BlockPos center, int radiusBlocks, int verticalRange)
	{
		int minX = center.X - radiusBlocks;
		int maxX = center.X + radiusBlocks;
		int minY = center.Y - verticalRange;
		int maxY = center.Y + verticalRange;
		int minZ = center.Z - radiusBlocks;
		int maxZ = center.Z + radiusBlocks;

		for (int pathIndex = 0; pathIndex < trackSpecification.Paths.Length; pathIndex++)
		{
			TrackPath path = trackSpecification.Paths[pathIndex];
			Vec3f[] points = path.LocalPoints;
			if (points == null || points.Length < 2) continue;

			int pathMinX = int.MaxValue, pathMinY = int.MaxValue, pathMinZ = int.MaxValue;
			int pathMaxX = int.MinValue, pathMaxY = int.MinValue, pathMaxZ = int.MinValue;

			for (int pointIndex = 0; pointIndex < points.Length; pointIndex++)
			{
				Vec3f point = points[pointIndex];
				int blockX = QuantizeLocalBlockCoordinate(anchorPosition.X, point.X);
				int blockY = QuantizeLocalBlockCoordinate(anchorPosition.Y, point.Y);
				int blockZ = QuantizeLocalBlockCoordinate(anchorPosition.Z, point.Z);

				if (blockX < pathMinX) pathMinX = blockX;
				if (blockX > pathMaxX) pathMaxX = blockX;
				if (blockY < pathMinY) pathMinY = blockY;
				if (blockY > pathMaxY) pathMaxY = blockY;
				if (blockZ < pathMinZ) pathMinZ = blockZ;
				if (blockZ > pathMaxZ) pathMaxZ = blockZ;
			}

			if (pathMaxX < minX || pathMinX > maxX) continue;
			if (pathMaxY < minY || pathMinY > maxY) continue;
			if (pathMaxZ < minZ || pathMinZ > maxZ) continue;
			return true;
		}

		return false;
	}

	private static int QuantizeLocalBlockCoordinate(int anchor, float localCoordinate) => (int)Math.Round((anchor + (double)localCoordinate) * 16.0) >> 4;

	/// Remove all atomic edges (and signal metadata) intersecting the rebuild volume. Used only by the dev rebuild command to avoid nuking the whole railgraph.
	private bool RemoveEdgesInBox(BlockPos center, int radiusBlocks, int yRange, RailGraphChangeSet? change = null)
	{
		int dimension = center.dimension;
		int minX = center.X - radiusBlocks;
		int maxX = center.X + radiusBlocks;
		int minY = center.Y - yRange;
		int maxY = center.Y + yRange;
		int minZ = center.Z - radiusBlocks;
		int maxZ = center.Z + radiusBlocks;

		// Collect candidate edges via hashgrid, then filter precisely by stored bounds.
		EdgeGrid.CollectNear(dimension, center.X, center.Z, radiusBlocks, RebuildWork, clear: true);

		bool changed = false;

		for (int edgeIndex = 0; edgeIndex < RebuildWork.Count; edgeIndex++)
		{
			ulong hash = RebuildWork[edgeIndex];
			if (!EdgesByHash.TryGetValue(hash, out var edge)) continue;

			if (edge.MaxX < minX || edge.MinX > maxX || edge.MaxZ < minZ || edge.MinZ > maxZ) continue;

			// Coarse Y gate (edgeGrid is XZ-only).
			int firstEndpointY = edge.FirstEndpoint.Y16 >> 4;
			int secondEndpointY = edge.SecondEndpoint.Y16 >> 4;
			int edgeMinY = firstEndpointY < secondEndpointY ? firstEndpointY : secondEndpointY;
			int edgeMaxY = firstEndpointY > secondEndpointY ? firstEndpointY : secondEndpointY;
			if (edgeMaxY < minY || edgeMinY > maxY) continue;

			if (RemoveEdge(hash, change)) changed = true;
		}

		RebuildWork.Clear();

		// Signals are metadata only. If they are inside the rebuild volume, drop them and let the rescan recreate them.
		RebuildSignalWork.Clear();
		foreach (var signalPair in Signals)
		{
			EndpointKey endpoint = signalPair.Key;
			if (endpoint.Dimension != dimension) continue;
			int x = endpoint.X16 >> 4;
			int y = endpoint.Y16 >> 4;
			int z = endpoint.Z16 >> 4;
			if (x < minX || x > maxX || y < minY || y > maxY || z < minZ || z > maxZ) continue;
			RebuildSignalWork.Add(endpoint);
		}

		for (int signalIndex = 0; signalIndex < RebuildSignalWork.Count; signalIndex++)
		{
			EndpointKey endpoint = RebuildSignalWork[signalIndex];
			TouchEndpointAndIncidentEdges(endpoint, change);
			Signals.Remove(endpoint);
			InvalidateAround(endpoint);
			changed = true;
		}

		RebuildSignalWork.Clear();

		return changed;
	}

	/// Persistence
	public byte[] Serialize(bool includePolylines, bool includeOccupancy = true)
	{
		using var memoryStream = new MemoryStream(64 * 1024);
		using var binaryWriter = new BinaryWriter(memoryStream);

		binaryWriter.Write(TrackSpecsDictionary.GetSpecsHash());
		binaryWriter.Write(BuildVersion);

		binaryWriter.Write(EdgesByHash.Count);
		foreach (var edgePair in EdgesByHash)
		{
			ulong hash = edgePair.Key;
			LightweightEdge edge = edgePair.Value;

			binaryWriter.Write(hash);
			binaryWriter.Write(edge.Gauge);
			binaryWriter.Write(includeOccupancy && edge.Occupied);
			binaryWriter.Write(edge.QuantizedMaxSpeedFactor);
			binaryWriter.Write(edge.MaterialSpeedCapBPS);

			binaryWriter.Write(edge.FirstEndpoint.X16); binaryWriter.Write(edge.FirstEndpoint.Y16); binaryWriter.Write(edge.FirstEndpoint.Z16); binaryWriter.Write(edge.FirstEndpoint.Dimension);
			binaryWriter.Write(edge.SecondEndpoint.X16); binaryWriter.Write(edge.SecondEndpoint.Y16); binaryWriter.Write(edge.SecondEndpoint.Z16); binaryWriter.Write(edge.SecondEndpoint.Dimension);

			binaryWriter.Write(edge.OwnerX);
			binaryWriter.Write(edge.OwnerY);
			binaryWriter.Write(edge.OwnerZ);
			binaryWriter.Write(edge.OwnerBlockID);

			if (!includePolylines || !TryGetHeavy(hash, out var heavySegment))
			{
				binaryWriter.Write(0);
				continue;
			}

			int pointCount = heavySegment.QuantizedCoordinates.Length / 3;
			binaryWriter.Write(pointCount);
			for (int coordinateIndex = 0; coordinateIndex < heavySegment.QuantizedCoordinates.Length; coordinateIndex++) binaryWriter.Write(heavySegment.QuantizedCoordinates[coordinateIndex]);
		}

		binaryWriter.Write(Signals.Count);
		foreach (var signalPair in Signals) { WriteSignal(binaryWriter, signalPair.Key, signalPair.Value); }

		if (!includeOccupancy) { binaryWriter.Write(0); }
		else
		{
			int occupiedCount = 0;
			foreach (var occupancyPair in OccupantsByZone)
			{
				if (occupancyPair.Value.Count > 0 && EdgesByZone.TryGetValue(occupancyPair.Key, out HashSet<ulong>? zoneEdges) && zoneEdges.Count > 0) occupiedCount++;
			}

			binaryWriter.Write(occupiedCount);
			foreach (var occupancyPair in OccupantsByZone)
			{
				if (occupancyPair.Value.Count == 0 || !EdgesByZone.TryGetValue(occupancyPair.Key, out HashSet<ulong>? zoneEdges) || zoneEdges.Count == 0) continue;
				using HashSet<ulong>.Enumerator enumerator = zoneEdges.GetEnumerator();
				enumerator.MoveNext();
				binaryWriter.Write(enumerator.Current);
			}
		}

		return memoryStream.ToArray();
	}


	internal byte[] SerializeArea
	(
		BlockPos center,
		int radius,
		bool includePolylines,
		bool includeOccupancy,
		int maxEdges,
		int maxPoints,
		List<ulong>? selectedEdgeHashes,
		out bool truncated,
		out int serializedEdges,
		out int serializedPoints
	)
	{
		truncated = false;
		serializedEdges = 0;
		serializedPoints = 0;
		radius = Math.Max(1, radius);
		maxEdges = Math.Max(1, maxEdges);
		maxPoints = Math.Max(2, maxPoints);

		int minX16 = (center.X - radius) << 4;
		int maxX16 = (center.X + radius + 1) << 4;
		int minZ16 = (center.Z - radius) << 4;
		int maxZ16 = (center.Z + radius + 1) << 4;

		List<ulong> selectedEdges;
		if (selectedEdgeHashes == null) { selectedEdges = new List<ulong>(Math.Min(maxEdges, 4096)); }
		else
		{
			selectedEdgeHashes.Clear();
			selectedEdgeHashes.EnsureCapacity(Math.Min(maxEdges, 4096));
			selectedEdges = selectedEdgeHashes;
		}
		HashSet<ulong> selectedSet = new();
		foreach (KeyValuePair<ulong, LightweightEdge> pair in EdgesByHash)
		{
			LightweightEdge edge = pair.Value;
			if (edge.FirstEndpoint.Dimension != center.dimension) continue;

			bool near =
				(edge.FirstEndpoint.X16 >= minX16 && edge.FirstEndpoint.X16 <= maxX16 && edge.FirstEndpoint.Z16 >= minZ16 && edge.FirstEndpoint.Z16 <= maxZ16) ||
				(edge.SecondEndpoint.X16 >= minX16 && edge.SecondEndpoint.X16 <= maxX16 && edge.SecondEndpoint.Z16 >= minZ16 && edge.SecondEndpoint.Z16 <= maxZ16) ||
				(edge.OwnerX >= center.X - radius && edge.OwnerX <= center.X + radius &&
				 edge.OwnerZ >= center.Z - radius && edge.OwnerZ <= center.Z + radius);
			if (!near) continue;

			int pointCount = 0;
			if (includePolylines && TryGetHeavy(pair.Key, out HeavySegment segment)) pointCount = segment.QuantizedCoordinates.Length / 3;
			if (selectedEdges.Count >= maxEdges || (includePolylines && serializedPoints + pointCount > maxPoints)) { truncated = true; continue; }

			selectedEdges.Add(pair.Key);
			selectedSet.Add(pair.Key);
			serializedPoints += pointCount;
		}
		serializedEdges = selectedEdges.Count;

		List<KeyValuePair<EndpointKey, SignalEntry>> selectedSignals = new();
		foreach (KeyValuePair<EndpointKey, SignalEntry> pair in Signals)
		{
			EndpointKey endpoint = pair.Key;
			if (endpoint.Dimension != center.dimension || endpoint.X16 < minX16 || endpoint.X16 > maxX16 || endpoint.Z16 < minZ16 || endpoint.Z16 > maxZ16) { continue; }
			selectedSignals.Add(pair);
		}

		using MemoryStream memoryStream = new(64 * 1024);
		using BinaryWriter binaryWriter = new(memoryStream);
		binaryWriter.Write(TrackSpecsDictionary.GetSpecsHash());
		binaryWriter.Write(BuildVersion);

		binaryWriter.Write(selectedEdges.Count);
		for (int selectedIndex = 0; selectedIndex < selectedEdges.Count; selectedIndex++)
		{
			ulong hash = selectedEdges[selectedIndex];
			LightweightEdge edge = EdgesByHash[hash];

			binaryWriter.Write(hash);
			binaryWriter.Write(edge.Gauge);
			binaryWriter.Write(edge.Occupied);
			binaryWriter.Write(edge.QuantizedMaxSpeedFactor);
			binaryWriter.Write(edge.MaterialSpeedCapBPS);
			binaryWriter.Write(edge.FirstEndpoint.X16); binaryWriter.Write(edge.FirstEndpoint.Y16); binaryWriter.Write(edge.FirstEndpoint.Z16); binaryWriter.Write(edge.FirstEndpoint.Dimension);
			binaryWriter.Write(edge.SecondEndpoint.X16); binaryWriter.Write(edge.SecondEndpoint.Y16); binaryWriter.Write(edge.SecondEndpoint.Z16); binaryWriter.Write(edge.SecondEndpoint.Dimension);
			binaryWriter.Write(edge.OwnerX);
			binaryWriter.Write(edge.OwnerY);
			binaryWriter.Write(edge.OwnerZ);
			binaryWriter.Write(edge.OwnerBlockID);

			if (!includePolylines || !TryGetHeavy(hash, out HeavySegment heavy)) { binaryWriter.Write(0); continue; }

			int pointCount = heavy.QuantizedCoordinates.Length / 3;
			binaryWriter.Write(pointCount);
			for (int coordinateIndex = 0; coordinateIndex < heavy.QuantizedCoordinates.Length; coordinateIndex++) binaryWriter.Write(heavy.QuantizedCoordinates[coordinateIndex]);
		}

		binaryWriter.Write(selectedSignals.Count);
		for (int signalIndex = 0; signalIndex < selectedSignals.Count; signalIndex++) { WriteSignal(binaryWriter, selectedSignals[signalIndex].Key, selectedSignals[signalIndex].Value); }

		if (!includeOccupancy) { binaryWriter.Write(0); }
		else
		{
			List<ulong> occupiedRepresentatives = new();
			foreach (KeyValuePair<ulong, HashSet<long>> pair in OccupantsByZone)
			{
				if (pair.Value.Count == 0 || !EdgesByZone.TryGetValue(pair.Key, out HashSet<ulong>? zoneEdges)) continue;
				ulong representative = 0;
				foreach (ulong edgeHash in zoneEdges)
				{
					if (!selectedSet.Contains(edgeHash)) continue;
					representative = edgeHash;
					break;
				}
				if (representative != 0) occupiedRepresentatives.Add(representative);
			}

			binaryWriter.Write(occupiedRepresentatives.Count);
			for (int representativeIndex = 0; representativeIndex < occupiedRepresentatives.Count; representativeIndex++) binaryWriter.Write(occupiedRepresentatives[representativeIndex]);
		}

		return memoryStream.ToArray();
	}

	public bool TryLoad(byte[] serializedData)
	{
		try { LoadOrThrow(serializedData); return true; }
		catch { return false; }
	}

	private void LoadOrThrow(byte[] serializedData)
	{
		using var memoryStream = new MemoryStream(serializedData);
		using var binaryReader = new BinaryReader(memoryStream);

		uint persistedSpecificationsHash = binaryReader.ReadUInt32();
		int buildVersion = Math.Max(1, binaryReader.ReadInt32());

		int edgeCount = binaryReader.ReadInt32();
		if (edgeCount < 0 || edgeCount > MaxSavedEdges) throw new InvalidDataException("Invalid railgraph edge count.");
		if ((long)edgeCount * MinSavedEdgeBytes + sizeof(int) > memoryStream.Length - memoryStream.Position) throw new EndOfStreamException("Truncated railgraph edge data.");

		var newEdgesByHash = new Dictionary<ulong, LightweightEdge>(edgeCount);
		var newEdgesByEndpoint = new Dictionary<EndpointKey, List<ulong>>(Math.Min(MaxSavedEdges, checked(edgeCount * 2)));
		var newHeavySegments = new HashSet<HeavySegment>(HeavySegment.HashComparer.Instance);
		var newSignals = new Dictionary<EndpointKey, SignalEntry>();
		var newOccupiedEdges = new HashSet<ulong>();

		static void AddEndpointReference(Dictionary<EndpointKey, List<ulong>> endpointMap, EndpointKey key, ulong hash)
		{
			if (!endpointMap.TryGetValue(key, out var edgeHashes))
			{
				edgeHashes = new List<ulong>(2);
				endpointMap[key] = edgeHashes;
			}
			edgeHashes.Add(hash);
		}

		long aggregatePointCount = 0;
		for (int edgeIndex = 0; edgeIndex < edgeCount; edgeIndex++)
		{
			ulong hash = binaryReader.ReadUInt64();
			byte gauge = binaryReader.ReadByte();
			bool occupied = binaryReader.ReadBoolean();
			byte quantizedMaxSpeedFactor = binaryReader.ReadByte();
			ushort materialSpeedCapBPS = binaryReader.ReadUInt16();

			int firstEndpointX = binaryReader.ReadInt32(); int firstEndpointY = binaryReader.ReadInt32(); int firstEndpointZ = binaryReader.ReadInt32(); int firstEndpointDimension = binaryReader.ReadInt32();
			int secondEndpointX = binaryReader.ReadInt32(); int secondEndpointY = binaryReader.ReadInt32(); int secondEndpointZ = binaryReader.ReadInt32(); int secondEndpointDimension = binaryReader.ReadInt32();

			int ownerX = binaryReader.ReadInt32();
			int ownerY = binaryReader.ReadInt32();
			int ownerZ = binaryReader.ReadInt32();
			int ownerBlockID = binaryReader.ReadInt32();
			if (ownerBlockID <= 0) throw new InvalidDataException("Invalid railgraph edge owner block id.");

			EndpointKey firstEndpoint = new EndpointKey(firstEndpointX, firstEndpointY, firstEndpointZ, firstEndpointDimension, gauge);
			EndpointKey secondEndpoint = new EndpointKey(secondEndpointX, secondEndpointY, secondEndpointZ, secondEndpointDimension, gauge);

			int pointCount = binaryReader.ReadInt32();
			if (pointCount < 0 || pointCount > MaxSavedPointsPerEdge) throw new InvalidDataException("Invalid railgraph point count.");
			aggregatePointCount = checked(aggregatePointCount + pointCount);
			if (aggregatePointCount > MaxSavedAggregatePoints) throw new InvalidDataException("Railgraph contains too many polyline points.");

			int[] quantizedCoordinates = Array.Empty<int>();
			if (pointCount > 0)
			{
				int coordinateCount = checked(pointCount * 3);
				if ((long)coordinateCount * sizeof(int) > memoryStream.Length - memoryStream.Position) throw new EndOfStreamException("Truncated railgraph polyline.");
				quantizedCoordinates = new int[coordinateCount];
				for (int coordinateIndex = 0; coordinateIndex < quantizedCoordinates.Length; coordinateIndex++) quantizedCoordinates[coordinateIndex] = binaryReader.ReadInt32();
			}

			int minX, minZ, maxX, maxZ;
			if (quantizedCoordinates.Length > 0) ComputeHorizontalBoundsBlocks(quantizedCoordinates, out minX, out minZ, out maxX, out maxZ);
			else
			{
				minX = Math.Min(firstEndpoint.X16, secondEndpoint.X16) >> 4; maxX = Math.Max(firstEndpoint.X16, secondEndpoint.X16) >> 4;
				minZ = Math.Min(firstEndpoint.Z16, secondEndpoint.Z16) >> 4; maxZ = Math.Max(firstEndpoint.Z16, secondEndpoint.Z16) >> 4;
			}

			var edge = new LightweightEdge
			{
				Gauge = gauge,
				Occupied = occupied,
				QuantizedMaxSpeedFactor = quantizedMaxSpeedFactor,
				MaterialSpeedCapBPS = materialSpeedCapBPS,
				LengthBlocks = ComputePolylineLengthBlocks(quantizedCoordinates),
				FirstEndpoint = firstEndpoint, SecondEndpoint = secondEndpoint,
				MinX = minX, MinZ = minZ, MaxX = maxX, MaxZ = maxZ,
				OwnerX = ownerX, OwnerY = ownerY, OwnerZ = ownerZ, OwnerBlockID = ownerBlockID,
				ZoneID = 0
			};
			if (hash == 0 || !newEdgesByHash.TryAdd(hash, edge)) throw new InvalidDataException("Duplicate or invalid railgraph edge hash.");

			AddEndpointReference(newEdgesByEndpoint, firstEndpoint, hash);
			AddEndpointReference(newEdgesByEndpoint, secondEndpoint, hash);
			if (quantizedCoordinates.Length > 0) newHeavySegments.Add(new HeavySegment(hash, quantizedCoordinates));

		}

		int signalCount = binaryReader.ReadInt32();
		if (signalCount < 0 || signalCount > MaxSavedSignals) throw new InvalidDataException("Invalid railgraph signal count.");
		if ((long)signalCount * SavedSignalBytes + sizeof(int) > memoryStream.Length - memoryStream.Position) throw new EndOfStreamException("Truncated railgraph signal data.");

		for (int signalIndex = 0; signalIndex < signalCount; signalIndex++)
		{
			int x = binaryReader.ReadInt32(); int y = binaryReader.ReadInt32(); int z = binaryReader.ReadInt32(); int dimension = binaryReader.ReadInt32();
			byte gauge = binaryReader.ReadByte();
			int contributorCount = binaryReader.ReadInt32();
			if (contributorCount <= 0 || contributorCount > MaxSignalContributorsPerEndpoint) throw new InvalidDataException("Invalid railgraph signal contributor count.");
			if ((long)contributorCount * SavedSignalContributorBytes > memoryStream.Length - memoryStream.Position) throw new EndOfStreamException("Truncated railgraph signal contributors.");

			var contributors = new List<SignalContributor>(contributorCount);
			for (int contributorIndex = 0; contributorIndex < contributorCount; contributorIndex++)
			{
				int ownerX = binaryReader.ReadInt32();
				int ownerY = binaryReader.ReadInt32();
				int ownerZ = binaryReader.ReadInt32();
				SignalKind kind = (SignalKind)binaryReader.ReadByte();
				ulong allowedFromEdgeHash = binaryReader.ReadUInt64();

				if (!Enum.IsDefined(typeof(SignalKind), kind)) throw new InvalidDataException("Invalid railgraph signal kind.");

				for (int existingIndex = 0; existingIndex < contributors.Count; existingIndex++)
				{
					SignalContributor existing = contributors[existingIndex];
					if (existing.OwnerX == ownerX && existing.OwnerY == ownerY && existing.OwnerZ == ownerZ) throw new InvalidDataException("Duplicate railgraph signal contributor.");
				}

				contributors.Add(new SignalContributor(ownerX, ownerY, ownerZ, kind, allowedFromEdgeHash));
			}

			EndpointKey endpoint = new(x, y, z, dimension, gauge);
			if (newSignals.ContainsKey(endpoint)) throw new InvalidDataException("Duplicate railgraph signal endpoint.");

			var signal = new SignalEntry { Contributors = contributors };
			RebuildSignalAggregate(ref signal);
			newSignals[endpoint] = signal;
		}

		int occupiedZoneCount = binaryReader.ReadInt32();
		if (occupiedZoneCount < 0 || occupiedZoneCount > MaxSavedOccupiedZones) throw new InvalidDataException("Invalid railgraph occupancy count.");
		if ((long)occupiedZoneCount * sizeof(ulong) > memoryStream.Length - memoryStream.Position) throw new EndOfStreamException("Truncated railgraph occupancy data.");
		for (int occupiedZoneIndex = 0; occupiedZoneIndex < occupiedZoneCount; occupiedZoneIndex++)
		{
			ulong occupiedEdge = binaryReader.ReadUInt64();
			if (occupiedEdge == 0 || !newEdgesByHash.ContainsKey(occupiedEdge) || !newOccupiedEdges.Add(occupiedEdge)) throw new InvalidDataException("Duplicate, missing, or invalid occupied rail edge.");
		}
		if (memoryStream.Position != memoryStream.Length) throw new InvalidDataException("Trailing railgraph data.");

		Clear();
		LoadedSpecificationsHash = persistedSpecificationsHash;
		BuildVersion = buildVersion;

		foreach (var edgePair in newEdgesByHash)
		{
			EdgesByHash[edgePair.Key] = edgePair.Value;
			LightweightEdge edge = edgePair.Value;
			EdgeGrid.Add(edge.FirstEndpoint.Dimension, edge.MinX, edge.MinZ, edge.MaxX, edge.MaxZ, edgePair.Key);
		}
		foreach (var endpointPair in newEdgesByEndpoint) EdgesByEndpoint[endpointPair.Key] = endpointPair.Value;
		foreach (var heavySegment in newHeavySegments) HeavySegments.Add(heavySegment);
		foreach (var signalPair in newSignals) Signals[signalPair.Key] = signalPair.Value;
		RebuildOccupancyZonesNow();
		foreach (ulong edgeHash in newOccupiedEdges) { if (TryGetOccupancyZoneID(edgeHash, out ulong zoneID)) OccupantsByZone[zoneID] = new HashSet<long> { 0 }; }
	}


	/// Debug snapshot built from maximal segments between nodes (degree!=2 endpoints). Truth remains atomic edges, not what this view shows.
	public RailGraphSnapshot ExportSnapshot(BlockPos center, bool includePolylines)
	{
		var snapshot = new RailGraphSnapshot
		{
			Center = center.Copy(),
			Nodes = new List<RailNode>(64),
			Segments = new List<RailSegment>(64),
			BuildVersion = BuildVersion
		};

		if (EdgesByHash.Count == 0) return snapshot;

		// Find nodes per connected component. natural nodes == degree != 2. loop-only components (all degree==2) == force one node (first visited endpoint)
		var nodeSet = new HashSet<EndpointKey>();
		var visited = new HashSet<EndpointKey>();
		var endpointQueue = new Queue<EndpointKey>();

		foreach (var start in EdgesByEndpoint.Keys)
		{
			if (visited.Contains(start)) continue;

			bool hasNaturalNode = false;
			EndpointKey forced = start;
			ulong forcedMinEdge = ulong.MaxValue;

			visited.Add(start);
			endpointQueue.Enqueue(start);

			while (endpointQueue.Count > 0)
			{
				var endpoint = endpointQueue.Dequeue();
				if (IsNode(endpoint))
				{
					nodeSet.Add(endpoint);
					hasNaturalNode = true;
				}

				if (!EdgesByEndpoint.TryGetValue(endpoint, out var incidentEdges)) continue;
				for (int edgeIndex = 0; edgeIndex < incidentEdges.Count; edgeIndex++)
				{
					ulong edgeHash = incidentEdges[edgeIndex];
					// Loop-only components: choose a forced node that is incident to the minimal edge-hash in the component.
					// This keeps ExportSnapshot stable and ensures loops still emit at least one segment.
					if (edgeHash < forcedMinEdge) { forcedMinEdge = edgeHash; forced = endpoint; }

					var other = OtherEndpoint(edgeHash, endpoint);
					if (visited.Add(other)) endpointQueue.Enqueue(other);
				}
			}

			if (!hasNaturalNode) nodeSet.Add(forced);
		}

		// Stable ordering for debug readability
		var nodes = nodeSet
			.OrderBy(endpoint => endpoint.Dimension)
			.ThenBy(endpoint => endpoint.Gauge).ThenBy(endpoint => endpoint.X16).ThenBy(endpoint => endpoint.Y16).ThenBy(endpoint => endpoint.Z16)
			.ToList();

		var nodeIDs = new Dictionary<EndpointKey, int>(nodes.Count);
		for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
		{
			var endpoint = nodes[nodeIndex];
			int nodeID = nodeIndex + 1;
			nodeIDs[endpoint] = nodeID;

			Vec3d worldPosition = endpoint.ToWorld();
			bool isSignal = Signals.TryGetValue(endpoint, out var signalEntry) && signalEntry.ReferenceCount > 0;

			snapshot.Nodes.Add(new RailNode
			{
				ID = nodeID,
				Gauge = endpoint.Gauge,
				IsSignal = isSignal,
				SignalKind = isSignal ? (byte)signalEntry.Kind : (byte)0,
				OneWayAllowedFromEdgeHash = isSignal && (signalEntry.Kind == SignalKind.OneWay || signalEntry.Kind == SignalKind.Chain) ? signalEntry.AllowedFromEdgeHash : 0,
				WorldPosition = worldPosition,
				// For signal nodes, Pos must be the source block entity position, not the graph endpoint floor.
				// Signal endpoints are often internal/shared points inside a track shape and may floor into a neighbouring block.
				Position = isSignal
					? new BlockPos(signalEntry.OwnerX, signalEntry.OwnerY, signalEntry.OwnerZ, endpoint.Dimension)
					: new BlockPos((int)Math.Floor(worldPosition.X), (int)Math.Floor(worldPosition.Y), (int)Math.Floor(worldPosition.Z), endpoint.Dimension)
			});
		}

		// Build maximal segments starting from each node along each incident edge. Include each undirected segment only once (canonical by endpoint order / loop rule).
		for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
		{
			var startNode = nodes[nodeIndex];
			if (!EdgesByEndpoint.TryGetValue(startNode, out var incidentEdges)) continue;

			for (int edgeIndex = 0; edgeIndex < incidentEdges.Count; edgeIndex++)
			{
				ulong startEdge = incidentEdges[edgeIndex];
				var maximalSegment = GetOrBuildMaximalSegment(startNode, startEdge);

				if (!IsCanonical(startNode, maximalSegment.End, startEdge, maximalSegment.Edges)) continue;

				var railSegment = new RailSegment
				{
					ID = HashToIntegerID(maximalSegment.SegmentHash),
					Gauge = maximalSegment.Gauge,
					NodeA = nodeIDs.TryGetValue(startNode, out int firstNodeID) ? firstNodeID : 0,
					NodeB = nodeIDs.TryGetValue(maximalSegment.End, out int secondNodeID) ? secondNodeID : 0
				};
				if (maximalSegment.Edges != null) railSegment.EdgeHashes.AddRange(maximalSegment.Edges);

				// Occupancy zone id (signals define boundaries, forks do not).
				if (TryGetOccupancyZoneID(startEdge, out ulong zoneID)) railSegment.OccupancyZoneID = zoneID;
				railSegment.ClearanceBlocked = AnyEdgeClearanceBlocked(maximalSegment.Edges);

				if (includePolylines) { BuildSegmentPolyline(railSegment.Polyline, startNode, maximalSegment.Edges); }
				else
				{
					railSegment.Polyline.Add(startNode.ToWorld());
					railSegment.Polyline.Add(maximalSegment.End.ToWorld());
				}

				snapshot.Segments.Add(railSegment);

				if (railSegment.NodeA > 0) snapshot.Nodes[railSegment.NodeA - 1].SegmentIDs.Add(railSegment.ID);
				if (railSegment.NodeB > 0 && railSegment.NodeB != railSegment.NodeA) snapshot.Nodes[railSegment.NodeB - 1].SegmentIDs.Add(railSegment.ID);
			}
		}

		return snapshot;
	}

	#region Segment Cache & Construction
	internal bool TryGetEdgeWorldEndpoints(ulong edgeHash, out Vec3d firstWorldEndpoint, out Vec3d secondWorldEndpoint)
	{
		if (!EdgesByHash.TryGetValue(edgeHash, out var edge))
		{
			firstWorldEndpoint = new Vec3d();
			secondWorldEndpoint = new Vec3d();
			return false;
		}

		firstWorldEndpoint = edge.FirstEndpoint.ToWorld();
		secondWorldEndpoint = edge.SecondEndpoint.ToWorld();
		return true;
	}

	internal bool TryGetIncidentEdges(EndpointKey endpoint, out List<ulong> edges) // Returns incident edge hashes for an endpoint. Do not modify the returned list.
	{
		if (EdgesByEndpoint.TryGetValue(endpoint, out var incidentEdges)) { edges = incidentEdges; return true; }
		edges = null!;
		return false;
	}

	internal void CollectEndpointKeys(List<EndpointKey> destination)
	{
		if (destination == null) return;
		foreach (EndpointKey endpoint in EdgesByEndpoint.Keys) destination.Add(endpoint);
	}

	internal int GetIncidentEdgeCount(EndpointKey endpoint)
	{
		return EdgesByEndpoint.TryGetValue(endpoint, out var incidentEdges) ? incidentEdges.Count : 0;
	}

	internal bool IsTopologyPathNode(EndpointKey endpoint) => IsNode(endpoint);

	internal bool TryGetOtherEndpoint(ulong edgeHash, EndpointKey sourceEndpoint, out EndpointKey other)
	{
		other = default;
		if (!EdgesByHash.TryGetValue(edgeHash, out var edge)) return false;
		if (edge.FirstEndpoint.Equals(sourceEndpoint)) { other = edge.SecondEndpoint; return true; }
		if (edge.SecondEndpoint.Equals(sourceEndpoint)) { other = edge.FirstEndpoint; return true; }
		return false;
	}

	internal double GetEdgeLength(ulong edgeHash)
	{
		return EdgesByHash.TryGetValue(edgeHash, out LightweightEdge edge) ? edge.LengthBlocks : 0;
	}

	internal bool EdgeTouchesEndpoint(ulong edgeHash, EndpointKey endpoint)
	{
		return EdgesByHash.TryGetValue(edgeHash, out var edge) && (edge.FirstEndpoint.Equals(endpoint) || edge.SecondEndpoint.Equals(endpoint));
	}

	internal bool SetSignal(EndpointKey endpoint, SignalKind kind, bool adding, ulong allowedFromEdgeHash, BlockPos ownerPosition, RailGraphChangeSet? change = null)
	{
		TouchEndpointAndIncidentEdges(endpoint, change);
		int ownerX = ownerPosition.X;
		int ownerY = ownerPosition.Y;
		int ownerZ = ownerPosition.Z;

		if (adding)
		{
			var contributor = new SignalContributor(ownerX, ownerY, ownerZ, kind, allowedFromEdgeHash);
			if (Signals.TryGetValue(endpoint, out SignalEntry current))
			{
				if (current.Contributors == null || current.Contributors.Count == 0) throw new InvalidOperationException("Railgraph signal has no contributor state.");

				bool replaced = false;
				for (int contributorIndex = 0; contributorIndex < current.Contributors.Count; contributorIndex++)
				{
					SignalContributor existing = current.Contributors[contributorIndex];
					if (existing.OwnerX != ownerX || existing.OwnerY != ownerY || existing.OwnerZ != ownerZ) continue;
					current.Contributors[contributorIndex] = contributor;
					replaced = true;
					break;
				}
				if (!replaced) current.Contributors.Add(contributor);

				SignalKind previousKind = current.Kind;
				ulong previousAllowedFromEdgeHash = current.AllowedFromEdgeHash;
				int previousOwnerX = current.OwnerX;
				int previousOwnerY = current.OwnerY;
				int previousOwnerZ = current.OwnerZ;
				RebuildSignalAggregate(ref current);
				if
				(
					previousKind != current.Kind || previousAllowedFromEdgeHash != current.AllowedFromEdgeHash ||
					previousOwnerX != current.OwnerX || previousOwnerY != current.OwnerY || previousOwnerZ != current.OwnerZ
				) { change?.TouchSignal(endpoint); }

				Signals[endpoint] = current;
				return true;
			}

			change?.TouchSignal(endpoint);
			var newSignal = new SignalEntry { Contributors = new List<SignalContributor>(1) { contributor } };
			RebuildSignalAggregate(ref newSignal);
			Signals[endpoint] = newSignal;
			InvalidateAround(endpoint);
			return true;
		}

		if (!Signals.TryGetValue(endpoint, out SignalEntry existingSignal) || existingSignal.Contributors == null) { return false; }

		int removeIndex = -1;
		for (int contributorIndex = 0; contributorIndex < existingSignal.Contributors.Count; contributorIndex++)
		{
			SignalContributor contributor = existingSignal.Contributors[contributorIndex];
			if (contributor.OwnerX == ownerX && contributor.OwnerY == ownerY && contributor.OwnerZ == ownerZ) { removeIndex = contributorIndex; break; }
		}
		if (removeIndex < 0) return false;

		existingSignal.Contributors.RemoveAt(removeIndex);
		change?.TouchSignal(endpoint);
		if (existingSignal.Contributors.Count == 0)
		{
			Signals.Remove(endpoint);
			InvalidateAround(endpoint);
			return true;
		}

		RebuildSignalAggregate(ref existingSignal);
		Signals[endpoint] = existingSignal;
		return true;
	}

	private static void RebuildSignalAggregate(ref SignalEntry signal)
	{
		List<SignalContributor>? contributors = signal.Contributors;
		if (contributors == null || contributors.Count == 0) { signal = default; return; }

		SignalKind kind = SignalKind.None;
		ulong allowedFromEdgeHash = 0;
		for (int contributorIndex = 0; contributorIndex < contributors.Count; contributorIndex++)
		{
			SignalContributor contributor = contributors[contributorIndex];
			if ((byte)contributor.Kind > (byte)kind) kind = contributor.Kind;
			if ((contributor.Kind == SignalKind.OneWay || contributor.Kind == SignalKind.Chain) && contributor.AllowedFromEdgeHash != 0)
			{
				allowedFromEdgeHash = contributor.AllowedFromEdgeHash;
			}
		}

		SignalContributor representative = contributors[^1];
		signal.Kind = kind;
		signal.ReferenceCount = contributors.Count;
		signal.AllowedFromEdgeHash = allowedFromEdgeHash;
		signal.OwnerX = representative.OwnerX;
		signal.OwnerY = representative.OwnerY;
		signal.OwnerZ = representative.OwnerZ;
	}

	private static void WriteSignal(BinaryWriter writer, EndpointKey endpoint, SignalEntry signal)
	{
		List<SignalContributor>? contributors = signal.Contributors;
		if (contributors == null || contributors.Count == 0) throw new InvalidDataException("Railgraph signal has no contributors.");

		writer.Write(endpoint.X16);
		writer.Write(endpoint.Y16);
		writer.Write(endpoint.Z16);
		writer.Write(endpoint.Dimension);
		writer.Write(endpoint.Gauge);
		writer.Write(contributors.Count);

		for (int contributorIndex = 0; contributorIndex < contributors.Count; contributorIndex++)
		{
			SignalContributor contributor = contributors[contributorIndex];
			writer.Write(contributor.OwnerX);
			writer.Write(contributor.OwnerY);
			writer.Write(contributor.OwnerZ);
			writer.Write((byte)contributor.Kind);
			writer.Write(contributor.AllowedFromEdgeHash);
		}
	}

	internal bool IsOccupancyZoneOccupied(ulong zoneID)
		=> zoneID != 0 && OccupantsByZone.TryGetValue(zoneID, out HashSet<long>? owners) && owners.Count > 0;

	internal int GetOccupancyZoneReferenceCount(ulong zoneID)
		=> zoneID != 0 && OccupantsByZone.TryGetValue(zoneID, out HashSet<long>? owners) ? owners.Count : 0;

	internal bool IsOccupancyZoneOccupiedByOther(ulong zoneID, long ownerID)
	{
		if (zoneID == 0 || !OccupantsByZone.TryGetValue(zoneID, out HashSet<long>? owners) || owners.Count == 0) return false;
		return ownerID == 0 || owners.Count > 1 || !owners.Contains(ownerID);
	}

	internal bool AddZoneOccupant(ulong zoneID, long ownerID)
	{
		if (zoneID == 0 || ownerID == 0 || !EdgesByZone.ContainsKey(zoneID)) return false;
		if (!OccupantsByZone.TryGetValue(zoneID, out HashSet<long>? owners))
		{
			owners = new HashSet<long>();
			OccupantsByZone[zoneID] = owners;
		}
		return owners.Add(ownerID);
	}

	internal bool RemoveZoneOccupant(ulong zoneID, long ownerID)
	{
		if (zoneID == 0 || ownerID == 0 || !OccupantsByZone.TryGetValue(zoneID, out HashSet<long>? owners)) return false;
		bool removed = owners.Remove(ownerID);
		if (owners.Count == 0) OccupantsByZone.Remove(zoneID);
		return removed;
	}

	internal void ReplaceZoneOccupantsSnapshot(ulong zoneID, HashSet<long>? owners)
	{
		if (zoneID == 0) return;
		if (owners == null || owners.Count == 0) { OccupantsByZone.Remove(zoneID); return; }
		OccupantsByZone[zoneID] = owners;
	}

	internal void ClearOccupiedZones() => OccupantsByZone.Clear();

	internal void RebuildOccupancyZonesNow()
	{
		EdgesByZone.Clear();
		OccupantsByZone.Clear();
		NextZoneID = 1;

		ZoneEdgeSetScratch.Clear();
		ZoneWork.Clear();
		foreach (ulong edgeHash in EdgesByHash.Keys) ZoneWork.Add(edgeHash);
		for (int edgeIndex = 0; edgeIndex < ZoneWork.Count; edgeIndex++)
		{
			ulong edgeHash = ZoneWork[edgeIndex];
			LightweightEdge edge = EdgesByHash[edgeHash];
			edge.ZoneID = 0;
			EdgesByHash[edgeHash] = edge;
		}

		ulong[] allEdges = ZoneWork.ToArray();
		for (int edgeIndex = 0; edgeIndex < allEdges.Length; edgeIndex++)
		{
			ulong edgeHash = allEdges[edgeIndex];
			if (ZoneEdgeSetScratch.Contains(edgeHash)) continue;
			ulong zoneID = AllocateZoneID();
			HashSet<ulong> zoneEdges = new();
			CollectZoneComponent(edgeHash, null, ZoneEdgeSetScratch, zoneEdges);
			AssignZone(zoneID, zoneEdges, null);
		}

		ZoneEdgeSetScratch.Clear();
	}

	internal void CommitOccupancyZones(RailGraphChangeSet change)
	{
		if (change == null) return;

		if (change.GlobalInvalidation || EdgesByZone.Count == 0)
		{
			RebuildOccupancyZonesNow();
			foreach (var zonePair in EdgesByZone)
			{
				change.TouchZone(zonePair.Key);
				foreach (ulong edgeHash in zonePair.Value) change.ReassignZoneEdge(edgeHash);
			}
			return;
		}

		if (!change.HasZoneTopologyChange)
		{
			CollectPostMutationZones(change);
			return;
		}

		// Pure additions cannot split an existing zone. Attach each connected group of new edges directly,
		// retaining the largest adjacent zone and reassigning only newly added edges plus smaller merged zones.
		// Ordinary track placement is therefore independent of the size of the existing network.
		if (change.AddedEdges.Count != 0 && change.RemovedEdges.Count == 0 && change.TouchedSignals.Count == 0)
		{
			CommitAddedEdgesOnly(change);
			CollectPostMutationZones(change);
			return;
		}

		ZoneSetScratch.Clear();
		for (int zoneIndex = 0; zoneIndex < change.TouchedOccupancyZones.Count; zoneIndex++)
		{
			ulong zoneID = change.TouchedOccupancyZones[zoneIndex];
			if (zoneID != 0) ZoneSetScratch.Add(zoneID);
		}

		for (int edgeIndex = 0; edgeIndex < change.TouchedEdges.Count; edgeIndex++)
		{
			if (TryGetOccupancyZoneID(change.TouchedEdges[edgeIndex], out ulong zoneID)) ZoneSetScratch.Add(zoneID);
		}
		for (int endpointIndex = 0; endpointIndex < change.TouchedEndpoints.Count; endpointIndex++)
		{
			if (!EdgesByEndpoint.TryGetValue(change.TouchedEndpoints[endpointIndex], out List<ulong>? incident)) continue;
			for (int incidentEdgeIndex = 0; incidentEdgeIndex < incident.Count; incidentEdgeIndex++)
			{
				if (TryGetOccupancyZoneID(incident[incidentEdgeIndex], out ulong zoneID)) ZoneSetScratch.Add(zoneID);
			}
		}

		ZoneEdgeSetScratch.Clear();
		for (int edgeIndex = 0; edgeIndex < change.AddedEdges.Count; edgeIndex++)
		{
			if (EdgesByHash.ContainsKey(change.AddedEdges[edgeIndex])) ZoneEdgeSetScratch.Add(change.AddedEdges[edgeIndex]);
		}
		AddZoneEdgesToCandidateSet(ZoneSetScratch, ZoneEdgeSetScratch);

		// Close over any previously untouched zone that the new topology now joins through a non-signal endpoint.
		// This makes the candidate set a complete set of components.
		bool expanded;
		do
		{
			expanded = false;
			ZoneWork.Clear();
			foreach (ulong edgeHash in ZoneEdgeSetScratch) ZoneWork.Add(edgeHash);
			for (int edgeIndex = 0; edgeIndex < ZoneWork.Count; edgeIndex++)
			{
				if (!EdgesByHash.TryGetValue(ZoneWork[edgeIndex], out LightweightEdge edge)) continue;
				ExpandCandidateAt(edge.FirstEndpoint, ref expanded);
				ExpandCandidateAt(edge.SecondEndpoint, ref expanded);
			}
			if (expanded) AddZoneEdgesToCandidateSet(ZoneSetScratch, ZoneEdgeSetScratch);
		}
		while (expanded);

		PreviousZoneByEdgeScratch.Clear();
		PreviousZoneSizeScratch.Clear();
		foreach (ulong edgeHash in ZoneEdgeSetScratch)
		{
			if (!EdgesByHash.TryGetValue(edgeHash, out LightweightEdge edge)) continue;
			ulong previousZoneID = edge.ZoneID;
			PreviousZoneByEdgeScratch[edgeHash] = previousZoneID;
			if (previousZoneID != 0)
			{
				PreviousZoneSizeScratch.TryGetValue(previousZoneID, out int previousCount);
				PreviousZoneSizeScratch[previousZoneID] = previousCount + 1;
				ZoneSetScratch.Add(previousZoneID);
			}
		}

		foreach (ulong zoneID in ZoneSetScratch)
		{
			EdgesByZone.Remove(zoneID);
			OccupantsByZone.Remove(zoneID);
			change.TouchZone(zoneID);
		}

		foreach (ulong edgeHash in ZoneEdgeSetScratch)
		{
			if (!EdgesByHash.TryGetValue(edgeHash, out LightweightEdge edge)) continue;
			edge.ZoneID = 0;
			EdgesByHash[edgeHash] = edge;
		}

		ZoneComponentsScratch.Clear();
		HashSet<ulong> visited = new();
		foreach (ulong edgeHash in ZoneEdgeSetScratch)
		{
			if (!EdgesByHash.ContainsKey(edgeHash) || visited.Contains(edgeHash)) continue;
			var component = new ZoneComponent();
			CollectZoneComponent(edgeHash, ZoneEdgeSetScratch, visited, component.Edges);
			for (int componentEdgeIndex = 0; componentEdgeIndex < component.Edges.Count; componentEdgeIndex++)
			{
				ulong componentEdgeHash = component.Edges[componentEdgeIndex];
				if (componentEdgeHash < component.MinEdge) component.MinEdge = componentEdgeHash;
				if (!PreviousZoneByEdgeScratch.TryGetValue(componentEdgeHash, out ulong previousZoneID) || previousZoneID == 0) continue;
				component.PreviousZoneCounts.TryGetValue(previousZoneID, out int count);
				component.PreviousZoneCounts[previousZoneID] = count + 1;
			}
			ZoneComponentsScratch.Add(component);
		}

		Dictionary<ulong, ZoneComponent> preferredComponentByPreviousZone = new();
		foreach (ZoneComponent component in ZoneComponentsScratch)
		{
			foreach (var zoneCountPair in component.PreviousZoneCounts)
			{
				if
				(
					!preferredComponentByPreviousZone.TryGetValue(zoneCountPair.Key, out ZoneComponent? current) ||
					IsBetterZoneSuccessor(component, zoneCountPair.Value, current, current.PreviousZoneCounts[zoneCountPair.Key])
				) { preferredComponentByPreviousZone[zoneCountPair.Key] = component; }
			}
		}

		HashSet<ulong> usedPreviousZones = new();
		foreach (ZoneComponent component in ZoneComponentsScratch)
		{
			ulong assignedZoneID = 0;
			int assignedPreviousSize = -1;
			foreach (var zoneCountPair in component.PreviousZoneCounts)
			{
				if (!ReferenceEquals(preferredComponentByPreviousZone[zoneCountPair.Key], component) || usedPreviousZones.Contains(zoneCountPair.Key)) continue;
				PreviousZoneSizeScratch.TryGetValue(zoneCountPair.Key, out int previousSize);
				if (previousSize > assignedPreviousSize || (previousSize == assignedPreviousSize && (assignedZoneID == 0 || zoneCountPair.Key < assignedZoneID)))
				{
					assignedZoneID = zoneCountPair.Key;
					assignedPreviousSize = previousSize;
				}
			}

			if (assignedZoneID == 0) assignedZoneID = AllocateZoneID();
			else usedPreviousZones.Add(assignedZoneID);

			HashSet<ulong> zoneEdges = new(component.Edges);
			AssignZone(assignedZoneID, zoneEdges, change);
		}

		ZoneSetScratch.Clear();
		ZoneEdgeSetScratch.Clear();
		ZoneWork.Clear();
		PreviousZoneByEdgeScratch.Clear();
		PreviousZoneSizeScratch.Clear();
		ZoneComponentsScratch.Clear();

		void ExpandCandidateAt(EndpointKey endpoint, ref bool didExpand)
		{
			if (Signals.ContainsKey(endpoint) || !EdgesByEndpoint.TryGetValue(endpoint, out List<ulong>? incident)) return;
			for (int edgeIndex = 0; edgeIndex < incident.Count; edgeIndex++)
			{
				ulong neighbor = incident[edgeIndex];
				if (ZoneEdgeSetScratch.Contains(neighbor)) continue;
				if (!EdgesByHash.TryGetValue(neighbor, out LightweightEdge edge)) continue;
				if (edge.ZoneID != 0 && ZoneSetScratch.Add(edge.ZoneID)) didExpand = true;
				else if (edge.ZoneID == 0 && ZoneEdgeSetScratch.Add(neighbor)) didExpand = true;
			}
		}
	}

	private void CommitAddedEdgesOnly(RailGraphChangeSet change)
	{
		ZoneEdgeSetScratch.Clear();
		for (int edgeIndex = 0; edgeIndex < change.AddedEdges.Count; edgeIndex++)
		{
			ulong edgeHash = change.AddedEdges[edgeIndex];
			if (EdgesByHash.TryGetValue(edgeHash, out LightweightEdge edge) && edge.ZoneID == 0) ZoneEdgeSetScratch.Add(edgeHash);
		}

		HashSet<ulong> visited = new();
		foreach (ulong startEdge in ZoneEdgeSetScratch)
		{
			if (!visited.Add(startEdge)) continue;

			var component = new HashSet<ulong>();
			ZoneWork.Clear();
			ZoneWork.Add(startEdge);
			ZoneSetScratch.Clear();

			for (int workIndex = 0; workIndex < ZoneWork.Count; workIndex++)
			{
				ulong edgeHash = ZoneWork[workIndex];
				component.Add(edgeHash);
				LightweightEdge edge = EdgesByHash[edgeHash];
				Explore(edge.FirstEndpoint);
				Explore(edge.SecondEndpoint);
			}

			ulong retainedZone = 0;
			int retainedSize = -1;
			foreach (ulong zoneID in ZoneSetScratch)
			{
				int size = EdgesByZone.TryGetValue(zoneID, out HashSet<ulong>? existingEdges) ? existingEdges.Count : 0;
				if (size > retainedSize || (size == retainedSize && (retainedZone == 0 || zoneID < retainedZone)))
				{
					retainedZone = zoneID;
					retainedSize = size;
				}
			}

			if (retainedZone == 0) { AssignZone(AllocateZoneID(), component, change); continue; }

			HashSet<ulong> retainedEdges = EdgesByZone[retainedZone];
			foreach (ulong edgeHash in component) Reassign(edgeHash, retainedZone, retainedEdges);

			foreach (ulong zoneID in ZoneSetScratch)
			{
				if (zoneID == retainedZone || !EdgesByZone.Remove(zoneID, out HashSet<ulong>? mergedEdges)) continue;
				OccupantsByZone.Remove(zoneID);
				change.TouchZone(zoneID);
				foreach (ulong edgeHash in mergedEdges) Reassign(edgeHash, retainedZone, retainedEdges);
			}
			change.TouchZone(retainedZone);

			void Explore(EndpointKey endpoint)
			{
				if (Signals.ContainsKey(endpoint) || !EdgesByEndpoint.TryGetValue(endpoint, out List<ulong>? incident)) return;
				for (int edgeIndex = 0; edgeIndex < incident.Count; edgeIndex++)
				{
					ulong neighbor = incident[edgeIndex];
					if (ZoneEdgeSetScratch.Contains(neighbor)) { if (visited.Add(neighbor)) ZoneWork.Add(neighbor); }
					else if (TryGetOccupancyZoneID(neighbor, out ulong zoneID)) { ZoneSetScratch.Add(zoneID); }
				}
			}
		}

		ZoneSetScratch.Clear();
		ZoneEdgeSetScratch.Clear();
		ZoneWork.Clear();

		void Reassign(ulong edgeHash, ulong zoneID, HashSet<ulong> destination)
		{
			if (!EdgesByHash.TryGetValue(edgeHash, out LightweightEdge edge)) return;
			edge.ZoneID = zoneID;
			EdgesByHash[edgeHash] = edge;
			destination.Add(edgeHash);
			change.ReassignZoneEdge(edgeHash);
		}
	}

	private static bool IsBetterZoneSuccessor(ZoneComponent candidate, int candidateOverlap, ZoneComponent current, int currentOverlap)
	{
		if (candidateOverlap != currentOverlap) return candidateOverlap > currentOverlap;
		if (candidate.Edges.Count != current.Edges.Count) return candidate.Edges.Count > current.Edges.Count;
		return candidate.MinEdge < current.MinEdge;
	}

	private void AddZoneEdgesToCandidateSet(HashSet<ulong> zones, HashSet<ulong> candidates)
	{
		foreach (ulong zoneID in zones)
		{
			if (!EdgesByZone.TryGetValue(zoneID, out HashSet<ulong>? zoneEdges)) continue;
			foreach (ulong edgeHash in zoneEdges) candidates.Add(edgeHash);
		}
	}

	private void CollectZoneComponent(ulong startEdge, HashSet<ulong>? allowedEdges, HashSet<ulong> visited, ICollection<ulong> destination)
	{
		if (!EdgesByHash.ContainsKey(startEdge) || !visited.Add(startEdge)) return;
		ZoneWork.Clear();
		ZoneWork.Add(startEdge);

		for (int edgeIndex = 0; edgeIndex < ZoneWork.Count; edgeIndex++)
		{
			ulong edgeHash = ZoneWork[edgeIndex];
			destination.Add(edgeHash);
			LightweightEdge edge = EdgesByHash[edgeHash];
			ExploreZoneEndpoint(edge.FirstEndpoint, edgeHash);
			ExploreZoneEndpoint(edge.SecondEndpoint, edgeHash);
		}

		void ExploreZoneEndpoint(EndpointKey endpoint, ulong sourceEdge)
		{
			if (Signals.ContainsKey(endpoint) || !EdgesByEndpoint.TryGetValue(endpoint, out List<ulong>? incident)) return;
			for (int edgeIndex = 0; edgeIndex < incident.Count; edgeIndex++)
			{
				ulong nextEdge = incident[edgeIndex];
				if (nextEdge == sourceEdge || !EdgesByHash.ContainsKey(nextEdge)) continue;
				if (allowedEdges != null && !allowedEdges.Contains(nextEdge)) continue;
				if (visited.Add(nextEdge)) ZoneWork.Add(nextEdge);
			}
		}
	}

	private void AssignZone(ulong zoneID, HashSet<ulong> zoneEdges, RailGraphChangeSet? change)
	{
		EdgesByZone[zoneID] = zoneEdges;
		foreach (ulong edgeHash in zoneEdges)
		{
			if (!EdgesByHash.TryGetValue(edgeHash, out LightweightEdge edge)) continue;
			edge.ZoneID = zoneID;
			EdgesByHash[edgeHash] = edge;
			change?.ReassignZoneEdge(edgeHash);
		}
		change?.TouchZone(zoneID);
	}

	private ulong AllocateZoneID()
	{
		while (NextZoneID == 0 || EdgesByZone.ContainsKey(NextZoneID)) NextZoneID++;
		return NextZoneID++;
	}

	internal bool IsSignalEndpoint(EndpointKey endpoint) => Signals.ContainsKey(endpoint);

	internal bool TryGetSignal(EndpointKey endpoint, out SignalEntry entry) => Signals.TryGetValue(endpoint, out entry);

	internal bool IsOneWayCrossingAllowed(EndpointKey signalEndpoint, ulong fromEdgeHash)
	{
		if (!Signals.TryGetValue(signalEndpoint, out var signal)) return true;
		if (signal.Kind != SignalKind.OneWay && signal.Kind != SignalKind.Chain) return true;
		return signal.AllowedFromEdgeHash == 0 || signal.AllowedFromEdgeHash == fromEdgeHash;
	}

	internal bool IsChainSignal(EndpointKey signalEndpoint)
	{
		return Signals.TryGetValue(signalEndpoint, out var signal) && signal.Kind == SignalKind.Chain;
	}

	internal bool TryGetOtherIncidentEdge(EndpointKey endpoint, ulong edgeHash, out ulong otherEdgeHash)
	{
		otherEdgeHash = 0;
		if (!EdgesByEndpoint.TryGetValue(endpoint, out var incidentEdges) || incidentEdges.Count == 0) return false;

		for (int edgeIndex = 0; edgeIndex < incidentEdges.Count; edgeIndex++)
		{
			ulong candidateEdgeHash = incidentEdges[edgeIndex];
			if (candidateEdgeHash != edgeHash) { otherEdgeHash = candidateEdgeHash; return true; }
		}

		return false;
	}

	// Pure committed-state lookup. Zone discovery is never performed from a movement or signal query.
	internal bool TryGetOccupancyZoneID(ulong edgeHash, out ulong zoneID)
	{
		if (EdgesByHash.TryGetValue(edgeHash, out LightweightEdge edge) && edge.ZoneID != 0)
		{
			zoneID = edge.ZoneID;
			return true;
		}

		zoneID = 0;
		return false;
	}

	// Get maximal segment when leaving a node endpoint along a specific outgoing edge.
	internal bool TryGetMaximalSegmentFromNodeEdge(EndpointKey node, ulong outgoingEdge, out MaximalSegment maximalSegment)
	{
		maximalSegment = default;
		if (!EdgesByHash.ContainsKey(outgoingEdge)) return false;
		if (!IsNode(node)) return false;
		if (!EdgesByEndpoint.TryGetValue(node, out var incidentEdges) || incidentEdges.Count == 0) return false;
		bool found = false;
		for (int edgeIndex = 0; edgeIndex < incidentEdges.Count; edgeIndex++) { if (incidentEdges[edgeIndex] == outgoingEdge) { found = true; break; } }
		if (!found) return false;

		maximalSegment = GetOrBuildMaximalSegment(node, outgoingEdge);
		return maximalSegment.Edges != null && maximalSegment.Edges.Length > 0;
	}

	// Get the maximal segment that contains a given atomic edge. The returned segment Start is a node boundary.
	internal bool TryGetMaximalSegmentContainingEdge(ulong edgeHash, out MaximalSegment maximalSegment)
	{
		maximalSegment = default;
		if (!EdgesByHash.TryGetValue(edgeHash, out var edge)) return false;

		// Walk from endpoint A towards its boundary node. The last edge used to enter that node is the outgoing edge to use when building the maximal segment.
		FindBoundaryStart(edge.FirstEndpoint, edgeHash, out EndpointKey node, out ulong outgoingEdge);

		if (!TryGetMaximalSegmentFromNodeEdge(node, outgoingEdge, out maximalSegment)) return false;
		return true;
	}

	// Set/clear occupancy for the maximal segment that contains the given atomic edge.
	internal bool TrySetOccupiedByEdge(ulong edgeHash, bool occupied, out ulong segmentHash)
	{
		segmentHash = 0;
		if (!TryGetMaximalSegmentContainingEdge(edgeHash, out var maximalSegment)) return false;
		segmentHash = maximalSegment.SegmentHash;
		
		// Occupancy is debug/test metadata only. Apply it to all edges in the containing max-segment.
		for (int edgeIndex = 0; edgeIndex < maximalSegment.Edges.Length; edgeIndex++)
		{
			ulong segmentEdgeHash = maximalSegment.Edges[edgeIndex];
			if (!EdgesByHash.TryGetValue(segmentEdgeHash, out var edge)) continue;
			if (edge.Occupied == occupied) continue;
			edge.Occupied = occupied;
			EdgesByHash[segmentEdgeHash] = edge;
		}

		return true;
	}

	private void FindBoundaryStart(EndpointKey start, ulong firstEdge, out EndpointKey boundaryNode, out ulong outgoingEdgeFromNode)
	{
		// If we're already at a node, the outgoing edge is the first edge.
		if (IsNode(start))
		{
			boundaryNode = start;
			outgoingEdgeFromNode = firstEdge;
			return;
		}

		EndpointKey currentEndpoint = start;
		ulong edge = firstEdge;
		int steps = 0;

		while (true)
		{
			if (!EdgesByHash.TryGetValue(edge, out var edgeData)) break;

			EndpointKey nextEndpoint = edgeData.FirstEndpoint.Equals(currentEndpoint) ? edgeData.SecondEndpoint : edgeData.FirstEndpoint;
			steps++;

			// Loop-only component fallback: treat start as boundary.
			if (nextEndpoint.Equals(start) && steps > 1)
			{
				boundaryNode = start;
				outgoingEdgeFromNode = firstEdge;
				return;
			}

			if (IsNode(nextEndpoint))
			{
				boundaryNode = nextEndpoint;
				outgoingEdgeFromNode = edge; // edge used to enter the node
				return;
			}

			if (!EdgesByEndpoint.TryGetValue(nextEndpoint, out var incidentEdges) || incidentEdges.Count < 2)
			{
				boundaryNode = nextEndpoint;
				outgoingEdgeFromNode = edge;
				return;
			}

			ulong nextEdge = incidentEdges[0] == edge ? incidentEdges[1] : incidentEdges[0];
			currentEndpoint = nextEndpoint;
			edge = nextEdge;
		}

		boundaryNode = start;
		outgoingEdgeFromNode = firstEdge;
	}


	private MaximalSegment GetOrBuildMaximalSegment(EndpointKey startNode, ulong startEdge)
	{
		var key = new SegmentKey(startNode, startEdge);
		if (SegmentCache.TryGetValue(key, out var maximalSegment)) return maximalSegment;

		maximalSegment = BuildMaximalSegment(startNode, startEdge);
		SegmentCache[key] = maximalSegment;
		return maximalSegment;
	}

	// Maximal segment chain when leaving startNode along startEdge.
	private MaximalSegment BuildMaximalSegment(EndpointKey startNode, ulong startEdge)
	{
		var edges = new List<ulong>(8);

		EndpointKey currentEndpoint = startNode;
		ulong edge = startEdge;

		byte gauge = 0;

		while (true)
		{
			if (!EdgesByHash.TryGetValue(edge, out var edgeData)) break;
			gauge = edgeData.Gauge;

			edges.Add(edge);

			EndpointKey nextEndpoint = edgeData.FirstEndpoint.Equals(currentEndpoint) ? edgeData.SecondEndpoint : edgeData.FirstEndpoint;

			// Loop-only component: come back to start.
			if (nextEndpoint.Equals(startNode) && edges.Count > 1)
			{
				return new MaximalSegment(gauge, startNode, startNode, edges.ToArray());
			}

			// Stop at node boundary (or dead end)
			if (IsNode(nextEndpoint) && !nextEndpoint.Equals(startNode))
			{
				return new MaximalSegment(gauge, startNode, nextEndpoint, edges.ToArray());
			}

			if (!EdgesByEndpoint.TryGetValue(nextEndpoint, out var incidentEdges) || incidentEdges.Count == 0)
			{
				return new MaximalSegment(gauge, startNode, nextEndpoint, edges.ToArray());
			}

			if (incidentEdges.Count == 1)
			{
				// dead-end-ish
				return new MaximalSegment(gauge, startNode, nextEndpoint, edges.ToArray());
			}

			// degree==2 continuation: pick the other edge
			ulong nextEdge = incidentEdges[0] == edge ? incidentEdges[1] : incidentEdges[0];

			currentEndpoint = nextEndpoint;
			edge = nextEdge;
		}

		return new MaximalSegment(gauge, startNode, currentEndpoint, edges.ToArray());
	}


	private bool AnyEdgeClearanceBlocked(ulong[] edgeChain)
	{
		if (edgeChain == null) return false;
		for (int edgeIndex = 0; edgeIndex < edgeChain.Length; edgeIndex++) { if (IsEdgeClearanceBlocked(edgeChain[edgeIndex])) return true; }
		return false;
	}

	private void BuildSegmentPolyline(List<Vec3d> outputPolyline, EndpointKey startNode, ulong[] edgeChain)
	{
		outputPolyline.Clear();

		EndpointKey currentEndpoint = startNode;

		for (int edgeIndex = 0; edgeIndex < edgeChain.Length; edgeIndex++)
		{
			ulong edgeHash = edgeChain[edgeIndex];
			if (!EdgesByHash.TryGetValue(edgeHash, out var edge)) break;
			if (!TryGetHeavy(edgeHash, out var heavySegment)) { currentEndpoint = edge.FirstEndpoint.Equals(currentEndpoint) ? edge.SecondEndpoint : edge.FirstEndpoint; continue; }

			bool forward = edge.FirstEndpoint.Equals(currentEndpoint);
			AppendQuantizedPolyline(outputPolyline, heavySegment.QuantizedCoordinates, forward);

			currentEndpoint = forward ? edge.SecondEndpoint : edge.FirstEndpoint;
		}
	}

	private static void AppendQuantizedPolyline(List<Vec3d> polyline, int[] quantizedCoordinates, bool forward)
	{
		if (quantizedCoordinates == null || quantizedCoordinates.Length < 6) return;

		if (polyline.Count == 0)
		{
			if (forward) { polyline.Add(new Vec3d(quantizedCoordinates[0] / 16.0, quantizedCoordinates[1] / 16.0, quantizedCoordinates[2] / 16.0)); }
			else
			{
				int coordinateOffset = quantizedCoordinates.Length - 3;
				polyline.Add(new Vec3d(quantizedCoordinates[coordinateOffset] / 16.0, quantizedCoordinates[coordinateOffset + 1] / 16.0, quantizedCoordinates[coordinateOffset + 2] / 16.0));
			}
		}

		if (forward) { for (int coordinateOffset = 3; coordinateOffset < quantizedCoordinates.Length; coordinateOffset += 3) AddPoint(polyline, quantizedCoordinates, coordinateOffset); }
		else { for (int coordinateOffset = quantizedCoordinates.Length - 6; coordinateOffset >= 0; coordinateOffset -= 3) AddPoint(polyline, quantizedCoordinates, coordinateOffset); }

		static void AddPoint(List<Vec3d> polyline, int[] quantizedCoordinates, int coordinateOffset)
		{
			double x = quantizedCoordinates[coordinateOffset] / 16.0;
			double y = quantizedCoordinates[coordinateOffset + 1] / 16.0;
			double z = quantizedCoordinates[coordinateOffset + 2] / 16.0;

			Vec3d last = polyline[polyline.Count - 1];
			double dx = last.X - x;
			double dy = last.Y - y;
			double dz = last.Z - z;
			if (dx * dx + dy * dy + dz * dz > 1e-10) polyline.Add(new Vec3d(x, y, z));
		}
	}

	private bool IsNode(EndpointKey endpoint)
	{
		if (Signals.ContainsKey(endpoint)) return true;
		return !EdgesByEndpoint.TryGetValue(endpoint, out var incidentEdges) || incidentEdges.Count != 2;
	}

	private EndpointKey OtherEndpoint(ulong edgeHash, EndpointKey sourceEndpoint)
	{
		var edge = EdgesByHash[edgeHash];
		return edge.FirstEndpoint.Equals(sourceEndpoint) ? edge.SecondEndpoint : edge.FirstEndpoint;
	}

	private static bool IsCanonical(EndpointKey firstEndpoint, EndpointKey secondEndpoint, ulong startEdge, ulong[] edgeChain)
	{
		int endpointComparison = firstEndpoint.CompareTo(secondEndpoint);
		if (endpointComparison < 0) return true;
		if (endpointComparison > 0) return false;

		// Important: this segment is only discovered by starting from node-incident edges.
		// The smallest edge hash in the loop may be somewhere in the middle of the circle,
		// so using the global minimum edge hash can reject both traversals and make the whole loop disappear from debug snapshots.
		// The two possible traversals from the same node have the same chain in opposite directions.
		// Therefore chain[0] and chain[^1] are swapped between them. Emit the traversal whose first edge is the smaller of those two.
		if (edgeChain == null || edgeChain.Length == 0) return false;
		return edgeChain[0] <= edgeChain[edgeChain.Length - 1];
	}

	//  Atomic edge add/remove 
	private bool AddTrackSpecification(Block block, TrackPieceSpec trackSpecification, BlockPos anchorPosition, ushort materialSpeedCapBPS, RailGraphChangeSet? change)
	{
		bool changed = false;

		byte gauge = trackSpecification.Gauge;
		byte quantizedSpeedFactor = QuantizeSpeedFactor(trackSpecification.MaxSpeedFactor01);

		// For "signal as track piece" force only the internal shared endpoints (usually the midpoint) as nodes.
		Dictionary<EndpointKey, int>? endpointCounts = trackSpecification.SignalKind != SignalKind.None ? new Dictionary<EndpointKey, int>() : null;

		ulong[]? pathHashes = (trackSpecification.SignalKind == SignalKind.OneWay || trackSpecification.SignalKind == SignalKind.Chain) ? new ulong[trackSpecification.Paths.Length] : null;

		for (int pathIndex = 0; pathIndex < trackSpecification.Paths.Length; pathIndex++)
		{
			TrackPath path = trackSpecification.Paths[pathIndex];
			Vec3f[] points = path.LocalPoints;
			if (points == null || points.Length < 2) continue;

			int[] quantizedCoordinates = QuantizeWorldPolyline16(anchorPosition, points);

			ulong hash = HashUtility.HashSegment(gauge, quantizedCoordinates);
			if (pathHashes != null && (uint)pathIndex < (uint)pathHashes.Length) pathHashes[pathIndex] = hash;

			EndpointKey firstEndpoint = EndpointKey.FromXYZ16(quantizedCoordinates, 0, anchorPosition.dimension, gauge);
			EndpointKey secondEndpoint = EndpointKey.FromXYZ16(quantizedCoordinates, quantizedCoordinates.Length - 3, anchorPosition.dimension, gauge);

			if (endpointCounts != null)
			{
				if (!endpointCounts.TryGetValue(firstEndpoint, out int firstEndpointCount))	firstEndpointCount = 0;
				endpointCounts[firstEndpoint] = firstEndpointCount + 1;
				
				if (!endpointCounts.TryGetValue(secondEndpoint, out int secondEndpointCount)) secondEndpointCount = 0;
				endpointCounts[secondEndpoint] = secondEndpointCount + 1;
			}

			if (AddEdge(hash, gauge, firstEndpoint, secondEndpoint, quantizedCoordinates, quantizedSpeedFactor, materialSpeedCapBPS, anchorPosition, block?.BlockId ?? 0, change)) changed = true;
		}

		if (endpointCounts != null)
		{
			foreach (var endpointCountPair in endpointCounts)
			{
				if (endpointCountPair.Value > 1)
				{
					ulong allowedFromEdgeHash = 0;
					if ((trackSpecification.SignalKind == SignalKind.OneWay || trackSpecification.SignalKind == SignalKind.Chain) && pathHashes != null)
					{
						int pathIndex = GameMath.Clamp((int)trackSpecification.OneWayAllowedFromPathIndex, 0, pathHashes.Length - 1);
						allowedFromEdgeHash = pathHashes[pathIndex];
					}
					if (SetSignal(endpointCountPair.Key, trackSpecification.SignalKind, adding: true, allowedFromEdgeHash: allowedFromEdgeHash, ownerPosition: anchorPosition, change: change)) changed = true;
				}
			}
		}

		return changed;
	}

	private static byte QuantizeSpeedFactor(float normalizedFactor) { int quantizedFactor = (int)Math.Round(GameMath.Clamp(normalizedFactor, 0f, 1f) * 255f); return (byte)GameMath.Clamp(quantizedFactor, 0, 255); }


	private bool RemoveTrackSpecification(TrackPieceSpec trackSpecification, BlockPos anchorPosition, RailGraphChangeSet? change)
	{
		bool changed = false;

		byte gauge = trackSpecification.Gauge;
		Dictionary<EndpointKey, int>? endpointCounts = trackSpecification.SignalKind != SignalKind.None ? new Dictionary<EndpointKey, int>() : null;

		ulong[]? pathHashes = (trackSpecification.SignalKind == SignalKind.OneWay || trackSpecification.SignalKind == SignalKind.Chain) ? new ulong[trackSpecification.Paths.Length] : null;

		for (int pathIndex = 0; pathIndex < trackSpecification.Paths.Length; pathIndex++)
		{
			TrackPath path = trackSpecification.Paths[pathIndex];
			Vec3f[] points = path.LocalPoints;
			if (points == null || points.Length < 2) continue;

			int[] quantizedCoordinates = QuantizeWorldPolyline16(anchorPosition, points);

			ulong hash = HashUtility.HashSegment(gauge, quantizedCoordinates);
			if (pathHashes != null && (uint)pathIndex < (uint)pathHashes.Length) pathHashes[pathIndex] = hash;

			if (endpointCounts != null)
			{
				EndpointKey firstEndpoint = EndpointKey.FromXYZ16(quantizedCoordinates, 0, anchorPosition.dimension, gauge);
				EndpointKey secondEndpoint = EndpointKey.FromXYZ16(quantizedCoordinates, quantizedCoordinates.Length - 3, anchorPosition.dimension, gauge);

				if (!endpointCounts.TryGetValue(firstEndpoint, out int firstEndpointCount)) firstEndpointCount = 0;
				endpointCounts[firstEndpoint] = firstEndpointCount + 1;
				if (!endpointCounts.TryGetValue(secondEndpoint, out int secondEndpointCount)) secondEndpointCount = 0;
				endpointCounts[secondEndpoint] = secondEndpointCount + 1;
			}

			if (RemoveEdge(hash, change)) changed = true;
		}

		if (endpointCounts != null)
		{
			foreach (var endpointCountPair in endpointCounts)
			{
				if (endpointCountPair.Value > 1)
				{
					if (SetSignal(endpointCountPair.Key, trackSpecification.SignalKind, adding: false, allowedFromEdgeHash: 0, ownerPosition: anchorPosition, change: change)) changed = true;
				}
			}
		}

		return changed;
	}



	internal void CollectPostMutationZones(RailGraphChangeSet change)
	{
		if (change == null) return;

		for (int edgeIndex = 0; edgeIndex < change.TouchedEdges.Count; edgeIndex++) { if (TryGetOccupancyZoneID(change.TouchedEdges[edgeIndex], out ulong zoneID)) change.TouchZone(zoneID); }
		for (int endpointIndex = 0; endpointIndex < change.TouchedEndpoints.Count; endpointIndex++)
		{
			if (!TryGetIncidentEdges(change.TouchedEndpoints[endpointIndex], out List<ulong> incident)) continue;
			for (int incidentEdgeIndex = 0; incidentEdgeIndex < incident.Count; incidentEdgeIndex++) { if (TryGetOccupancyZoneID(incident[incidentEdgeIndex], out ulong zoneID)) change.TouchZone(zoneID); }
		}

		change.Canonicalize();
	}

	private void TouchExistingEdge(ulong hash, RailGraphChangeSet? change)
	{
		if (change == null || hash == 0) return;

		change.TouchEdge(hash);

		if (!EdgesByHash.TryGetValue(hash, out var edge)) return;

		change.TouchEndpoint(edge.FirstEndpoint);
		change.TouchEndpoint(edge.SecondEndpoint);

		if (TryGetOccupancyZoneID(hash, out ulong zoneID)) change.TouchZone(zoneID);
	}

	private void TouchEndpointAndIncidentEdges(EndpointKey endpoint, RailGraphChangeSet? change)
	{
		if (change == null) return; change.TouchEndpoint(endpoint);

		if (!EdgesByEndpoint.TryGetValue(endpoint, out var incident)) return;
		for (int edgeIndex = 0; edgeIndex < incident.Count; edgeIndex++) TouchExistingEdge(incident[edgeIndex], change);
	}



	private bool AddEdge(ulong hash, byte gauge, EndpointKey firstEndpoint, EndpointKey secondEndpoint, int[] quantizedCoordinates, byte quantizedMaxSpeedFactor, ushort materialSpeedCapBPS, BlockPos ownerPosition, int ownerBlockID, RailGraphChangeSet? change)
	{
		if (EdgesByHash.TryGetValue(hash, out var existing))
		{
			// Same geometric edge (same hash), but metadata (material cap / spec speed factor) may change.
			if
			(
				existing.Gauge == gauge &&
				existing.QuantizedMaxSpeedFactor == quantizedMaxSpeedFactor &&
				existing.MaterialSpeedCapBPS == materialSpeedCapBPS &&
				existing.OwnerBlockID == ownerBlockID &&
				existing.OwnerX == ownerPosition.X && existing.OwnerY == ownerPosition.Y && existing.OwnerZ == ownerPosition.Z
			) return false;

			TouchExistingEdge(hash, change);
			EdgesByHash[hash] = new LightweightEdge
			{
				Gauge = existing.Gauge,
				Occupied = existing.Occupied,
				QuantizedMaxSpeedFactor = quantizedMaxSpeedFactor,
				MaterialSpeedCapBPS = materialSpeedCapBPS,
				LengthBlocks = existing.LengthBlocks,
				FirstEndpoint = existing.FirstEndpoint,
				SecondEndpoint = existing.SecondEndpoint,
				MinX = existing.MinX,
				ZoneID = existing.ZoneID,
				MinZ = existing.MinZ,
				MaxX = existing.MaxX,
				MaxZ = existing.MaxZ,
				OwnerX = ownerPosition.X,
				OwnerY = ownerPosition.Y,
				OwnerZ = ownerPosition.Z,
				OwnerBlockID = ownerBlockID,
				ClearanceBlocked = existing.ClearanceBlocked
			};
			return true;
		}

		ComputeHorizontalBoundsBlocks(quantizedCoordinates, out int minX, out int minZ, out int maxX, out int maxZ);

		change?.AddEdge(hash);
		change?.TouchEndpoint(firstEndpoint);
		change?.TouchEndpoint(secondEndpoint);
		EdgesByHash[hash] = new LightweightEdge
		{
			Gauge = gauge, Occupied = false, QuantizedMaxSpeedFactor = quantizedMaxSpeedFactor, MaterialSpeedCapBPS = materialSpeedCapBPS,
			LengthBlocks = ComputePolylineLengthBlocks(quantizedCoordinates),
			FirstEndpoint = firstEndpoint, SecondEndpoint = secondEndpoint, MinX = minX, MinZ = minZ, MaxX = maxX, MaxZ = maxZ,
			OwnerX = ownerPosition.X,
			OwnerY = ownerPosition.Y,
			OwnerZ = ownerPosition.Z,
			OwnerBlockID = ownerBlockID
		};

		EdgeGrid.Add(firstEndpoint.Dimension, minX, minZ, maxX, maxZ, hash);

		AddEndpointReference(firstEndpoint, hash);
		AddEndpointReference(secondEndpoint, hash);

		HeavySegments.Add(new HeavySegment(hash, quantizedCoordinates));

		InvalidateAround(firstEndpoint);
		InvalidateAround(secondEndpoint);

		return true;
	}

	private bool RemoveEdge(ulong hash, RailGraphChangeSet? change = null)
	{
		if (!EdgesByHash.TryGetValue(hash, out var edge)) return false;

		TouchExistingEdge(hash, change);
		change?.RemoveEdge(hash);
		
		// Invalidate cache while the edge is still present in adjacency, so cached entries keyed by this outgoing edge can be removed.
		InvalidateAround(edge.FirstEndpoint);
		InvalidateAround(edge.SecondEndpoint);

		EdgeGrid.Remove(edge.FirstEndpoint.Dimension, edge.MinX, edge.MinZ, edge.MaxX, edge.MaxZ, hash);

		EdgesByHash.Remove(hash);

		RemoveEndpointReference(edge.FirstEndpoint, hash);
		RemoveEndpointReference(edge.SecondEndpoint, hash);

		HeavySegments.Remove(new HeavySegment(hash, Array.Empty<int>()));

		return true;
	}

	private void AddEndpointReference(EndpointKey key, ulong hash)
	{
		if (!EdgesByEndpoint.TryGetValue(key, out var edgeHashes))
		{
			edgeHashes = new List<ulong>(2);
			EdgesByEndpoint[key] = edgeHashes;
		}
		edgeHashes.Add(hash);
	}

	private void RemoveEndpointReference(EndpointKey key, ulong hash)
	{
		if (!EdgesByEndpoint.TryGetValue(key, out var edgeHashes)) return;

		for (int edgeIndex = 0; edgeIndex < edgeHashes.Count; edgeIndex++)
		{
			if (edgeHashes[edgeIndex] != hash) continue;
			int lastIndex = edgeHashes.Count - 1;
			edgeHashes[edgeIndex] = edgeHashes[lastIndex];
			edgeHashes.RemoveAt(lastIndex);
			break;
		}

		if (edgeHashes.Count == 0) EdgesByEndpoint.Remove(key);
	}

	// Local invalidation | Remove cached segments that might have changed due to edits near ep
	private void InvalidateAround(EndpointKey endpoint)
	{
		if (SegmentCache.Count == 0) return;

		// small list, no need for hashset
		var nodes = new List<EndpointKey>(8);
		AddOnce(nodes, endpoint);

		if (EdgesByEndpoint.TryGetValue(endpoint, out var incidentEdges))
		{
			for (int edgeIndex = 0; edgeIndex < incidentEdges.Count; edgeIndex++)
			{
				EndpointKey boundaryNode = WalkToBoundaryNode(endpoint, incidentEdges[edgeIndex]);
				AddOnce(nodes, boundaryNode);
			}
		}

		for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
		{
			var node = nodes[nodeIndex];
			if (!EdgesByEndpoint.TryGetValue(node, out var outgoingEdges)) continue;

			for (int outgoingEdgeIndex = 0; outgoingEdgeIndex < outgoingEdges.Count; outgoingEdgeIndex++)
			{
				SegmentCache.Remove(new SegmentKey(node, outgoingEdges[outgoingEdgeIndex]));
			}
		}

		static void AddOnce(List<EndpointKey> endpoints, EndpointKey endpoint)
		{
			for (int endpointIndex = 0; endpointIndex < endpoints.Count; endpointIndex++) if (endpoints[endpointIndex].Equals(endpoint)) return;
			endpoints.Add(endpoint);
		}
	}

	private EndpointKey WalkToBoundaryNode(EndpointKey start, ulong firstEdge)
	{
		EndpointKey currentEndpoint = start;
		ulong edge = firstEdge;
		int steps = 0;

		while (true)
		{
			if (!EdgesByHash.TryGetValue(edge, out var edgeData)) return currentEndpoint;

			EndpointKey nextEndpoint = edgeData.FirstEndpoint.Equals(currentEndpoint) ? edgeData.SecondEndpoint : edgeData.FirstEndpoint;
			steps++;
			if (nextEndpoint.Equals(start) && steps > 1) return start;

			if (IsNode(nextEndpoint)) return nextEndpoint;

			if (!EdgesByEndpoint.TryGetValue(nextEndpoint, out var incidentEdges) || incidentEdges.Count < 2) return nextEndpoint;

			ulong nextEdge = incidentEdges[0] == edge ? incidentEdges[1] : incidentEdges[0];
			currentEndpoint = nextEndpoint;
			edge = nextEdge;
		}
	}
	#endregion

	#region Helpers
	private bool TryGetHeavy(ulong hash, out HeavySegment heavySegment) { return HeavySegments.TryGetValue(new HeavySegment(hash, Array.Empty<int>()), out heavySegment); }


	private static double ComputePolylineLengthBlocks(int[] quantizedCoordinates)
	{
		if (quantizedCoordinates == null || quantizedCoordinates.Length < 6) return 0;
		double length = 0;
		for (int offset = 0; offset + 5 < quantizedCoordinates.Length; offset += 3)
		{
			double dx = (quantizedCoordinates[offset + 3] - quantizedCoordinates[offset]) * (1.0 / 16.0);
			double dy = (quantizedCoordinates[offset + 4] - quantizedCoordinates[offset + 1]) * (1.0 / 16.0);
			double dz = (quantizedCoordinates[offset + 5] - quantizedCoordinates[offset + 2]) * (1.0 / 16.0);
			length += Math.Sqrt(dx * dx + dy * dy + dz * dz);
		}
		return length;
	}

	private static void ComputeHorizontalBoundsBlocks(int[] quantizedCoordinates, out int minX, out int minZ, out int maxX, out int maxZ)
	{
		minX = int.MaxValue; minZ = int.MaxValue;
		maxX = int.MinValue; maxZ = int.MinValue;

		for (int coordinateIndex = 0; coordinateIndex < quantizedCoordinates.Length; coordinateIndex += 3)
		{
			int blockX = quantizedCoordinates[coordinateIndex + 0] >> 4;
			int blockZ = quantizedCoordinates[coordinateIndex + 2] >> 4;

			if (blockX < minX) minX = blockX;
			if (blockX > maxX) maxX = blockX;
			if (blockZ < minZ) minZ = blockZ;
			if (blockZ > maxZ) maxZ = blockZ;
		}

		if (minX == int.MaxValue) { minX = maxX = 0; minZ = maxZ = 0; }
	}

	private static int[] QuantizeWorldPolyline16(BlockPos anchorPosition, Vec3f[] localPoints)
	{
		int[] quantizedCoordinates = new int[localPoints.Length * 3];

		for (int pointIndex = 0; pointIndex < localPoints.Length; pointIndex++)
		{
			Vec3f localPoint = localPoints[pointIndex];

			double worldX = anchorPosition.X + localPoint.X;
			double worldY = anchorPosition.Y + localPoint.Y;
			double worldZ = anchorPosition.Z + localPoint.Z;

			quantizedCoordinates[pointIndex * 3 + 0] = (int)Math.Round(worldX * 16.0);
			quantizedCoordinates[pointIndex * 3 + 1] = (int)Math.Round(worldY * 16.0);
			quantizedCoordinates[pointIndex * 3 + 2] = (int)Math.Round(worldZ * 16.0);
		}

		return quantizedCoordinates;
	}

	private static int HashToIntegerID(ulong hash)
	{
		int integerID = unchecked((int)(hash ^ (hash >> 32)));
		return integerID == 0 ? 1 : integerID;
	}

	private sealed class ZoneComponent
	{
		internal readonly List<ulong> Edges = new();
		internal readonly Dictionary<ulong, int> PreviousZoneCounts = new();
		internal ulong MinEdge = ulong.MaxValue;
	}

	private struct LightweightEdge
	{
		public byte Gauge;
		public bool Occupied;
		public byte QuantizedMaxSpeedFactor; // 0-255 to 0-1
		public ushort MaterialSpeedCapBPS; // 0 == unlimited
		public double LengthBlocks;
		public EndpointKey FirstEndpoint;
		public EndpointKey SecondEndpoint;

		// XZ bounds in block coords (for hashgrid add/remove)
		public int MinX;
		public int MinZ;
		public int MaxX;
		public int MaxZ;

		// Producing rail block. Position + gauge + geometric edge hash are the durable identity.
		// OwnerBlockId is a rebindable runtime/persisted hint because numeric block IDs may change across mod updates even when the physical rail and edge geometry do not.
		public int OwnerX;
		public int OwnerY;
		public int OwnerZ;
		public int OwnerBlockID;
		public bool ClearanceBlocked;

		// Committed occupancy-zone assignment.
		public ulong ZoneID;
	}

	internal readonly record struct EndpointKey(int X16, int Y16, int Z16, int Dimension, byte Gauge) : IComparable<EndpointKey>
	{
		public Vec3d ToWorld() => new Vec3d(X16 / 16.0, Y16 / 16.0, Z16 / 16.0);

		public static EndpointKey FromXYZ16(int[] quantizedCoordinates, int offset, int dimension, byte gauge)
		{
			return new EndpointKey(quantizedCoordinates[offset + 0], quantizedCoordinates[offset + 1], quantizedCoordinates[offset + 2], dimension, gauge);
		}

		public int CompareTo(EndpointKey other)
		{
			if (Dimension != other.Dimension) return Dimension.CompareTo(other.Dimension);
			if (Gauge != other.Gauge) return Gauge.CompareTo(other.Gauge);
			if (X16 != other.X16) return X16.CompareTo(other.X16);
			if (Y16 != other.Y16) return Y16.CompareTo(other.Y16);
			return Z16.CompareTo(other.Z16);
		}
	}

	private readonly struct HeavySegment
	{
		public readonly ulong Hash;
		public readonly int[] QuantizedCoordinates;

		public HeavySegment(ulong hash, int[] quantizedCoordinates)
		{
			Hash = hash;
			QuantizedCoordinates = quantizedCoordinates;
		}

		public sealed class HashComparer : IEqualityComparer<HeavySegment>
		{
			public static readonly HashComparer Instance = new();
			public bool Equals(HeavySegment firstSegment, HeavySegment secondSegment) => firstSegment.Hash == secondSegment.Hash;
			public int GetHashCode(HeavySegment segment) => segment.Hash.GetHashCode();
		}
	}

	private readonly struct SegmentKey : IEquatable<SegmentKey>
	{
		public readonly EndpointKey Start;
		public readonly ulong Edge;

		public SegmentKey(EndpointKey start, ulong edge) { Start = start; Edge = edge; }

		public bool Equals(SegmentKey other) => Start.Equals(other.Start) && Edge == other.Edge;
		public override bool Equals(object? otherObject) => otherObject is SegmentKey segmentKey && Equals(segmentKey);
		public override int GetHashCode() => HashCode.Combine(Start, Edge);
	}

	internal readonly struct MaximalSegment
	{
		public readonly byte Gauge;
		public readonly EndpointKey Start;
		public readonly EndpointKey End;
		public readonly ulong[] Edges;
		public readonly ulong SegmentHash;

		public MaximalSegment(byte gauge, EndpointKey start, EndpointKey end, ulong[] edges)
		{
			Gauge = gauge;
			Start = start;
			End = end;
			Edges = edges;
			SegmentHash = HashUtility.HashSegmentChain(gauge, edges, start, end);
		}
	}

	private static class HashUtility
	{
		// Edge hash (FNV-1a 64, direction-invariant by endpoint)
		public static ulong HashSegment(byte gauge, int[] quantizedCoordinates)
		{
			ulong hash = 1469598103934665603UL;

			Mix(ref hash, gauge);
			Mix(ref hash, (uint)(quantizedCoordinates?.Length ?? 0));

			if (quantizedCoordinates == null || quantizedCoordinates.Length < 6) return hash;

			bool reverse = ShouldReverse(quantizedCoordinates);

			if (!reverse)
			{
				for (int coordinateIndex = 0; coordinateIndex < quantizedCoordinates.Length; coordinateIndex++) Mix(ref hash, (uint)quantizedCoordinates[coordinateIndex]);
			}
			else
			{
				for (int coordinateIndex = quantizedCoordinates.Length - 3; coordinateIndex >= 0; coordinateIndex -= 3)
				{
					Mix(ref hash, (uint)quantizedCoordinates[coordinateIndex + 0]);
					Mix(ref hash, (uint)quantizedCoordinates[coordinateIndex + 1]);
					Mix(ref hash, (uint)quantizedCoordinates[coordinateIndex + 2]);
				}
			}

			return hash;
		}

		// Segment hash from edge chain + endpoints (direction-invariant)
		public static ulong HashSegmentChain(byte gauge, ulong[] edges, EndpointKey firstEndpoint, EndpointKey secondEndpoint)
		{
			ulong hash = 1469598103934665603UL;
			Mix(ref hash, gauge);

			bool reverse = firstEndpoint.CompareTo(secondEndpoint) > 0;
			if (!reverse) { for (int edgeIndex = 0; edgeIndex < edges.Length; edgeIndex++) Mix(ref hash, edges[edgeIndex]); }
			else { for (int edgeIndex = edges.Length - 1; edgeIndex >= 0; edgeIndex--) Mix(ref hash, edges[edgeIndex]); }

			return hash;
		}

		private static bool ShouldReverse(int[] quantizedCoordinates)
		{
			int firstPointX = quantizedCoordinates[0], firstPointY = quantizedCoordinates[1], firstPointZ = quantizedCoordinates[2];
			int secondPointOffset = quantizedCoordinates.Length - 3;
			int secondPointX = quantizedCoordinates[secondPointOffset + 0], secondPointY = quantizedCoordinates[secondPointOffset + 1], secondPointZ = quantizedCoordinates[secondPointOffset + 2];

			if (firstPointX != secondPointX) return firstPointX > secondPointX;
			if (firstPointY != secondPointY) return firstPointY > secondPointY;
			return firstPointZ > secondPointZ;
		}

		private static void Mix(ref ulong hash, byte value)		{ hash ^= value; hash *= 1099511628211UL; }
		private static void Mix(ref ulong hash, uint value)		{ hash ^= value; hash *= 1099511628211UL; }
		private static void Mix(ref ulong hash, ulong value)	{ hash ^= value; hash *= 1099511628211UL; }
	}
	#endregion
}
