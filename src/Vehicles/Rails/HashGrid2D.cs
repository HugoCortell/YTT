using System;
using System.Collections.Generic;

namespace YangTransport;

/// Lightweight sparse 2D hashgrid (no Y axis). Buckets are unordered lists of ulongs (swap-remove).
/// This mutable index is owned by the game thread and is not thread-safe. CollectNear reuses instance scratch storage and is explicitly non-reentrant.
/// Accidental nested queries fail fast instead of silently corrupting the outer query's deduplication state.
internal sealed class HashGrid2D
{
	public readonly int CellSize;
	public readonly int CellShift;

	// dim --> (cellKey --> ids)
	private readonly Dictionary<int, Dictionary<long, List<ulong>>> CellsByDimension = new();
	private readonly HashSet<ulong> QuerySeenIDs = new();
	private bool QueryInProgress;

	public HashGrid2D(int cellSizeBlocks = 64)
	{
		if (cellSizeBlocks <= 0) throw new ArgumentOutOfRangeException(nameof(cellSizeBlocks));
		if ((cellSizeBlocks & (cellSizeBlocks - 1)) != 0) throw new ArgumentException("Cell size must be a power of two.", nameof(cellSizeBlocks));

		CellSize = cellSizeBlocks;
		CellShift = 0;
		int cellSizeValue = cellSizeBlocks;
		while ((cellSizeValue >>= 1) != 0) CellShift++;
	}

	public void Clear() => CellsByDimension.Clear();

	public int BlockToCell(int blockCoordinate) => blockCoordinate >> CellShift;

	public static long CellKey(int cellX, int cellZ) => ((long)cellX << 32) | (uint)cellZ;

	public bool TryGetCell(int dimension, int cellX, int cellZ, out List<ulong> entityIDs)
	{
		entityIDs = null!;
		return CellsByDimension.TryGetValue(dimension, out var cellsByKey) && cellsByKey.TryGetValue(CellKey(cellX, cellZ), out entityIDs) && entityIDs.Count > 0;
	}

	public bool TryGetCellAtBlock(int dimension, int blockX, int blockZ, out List<ulong> entityIDs)
		=> TryGetCell(dimension, BlockToCell(blockX), BlockToCell(blockZ), out entityIDs);

	public void Add(int dimension, int minX, int minZ, int maxX, int maxZ, ulong entityID)
	{
		if (minX > maxX) (minX, maxX) = (maxX, minX);
		if (minZ > maxZ) (minZ, maxZ) = (maxZ, minZ);

		int minCellX = minX >> CellShift;
		int maxCellX = maxX >> CellShift;
		int minCellZ = minZ >> CellShift;
		int maxCellZ = maxZ >> CellShift;

		if (!CellsByDimension.TryGetValue(dimension, out var cellsByKey))
		{
			cellsByKey = new Dictionary<long, List<ulong>>(256);
			CellsByDimension[dimension] = cellsByKey;
		}

		for (int cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
		{
			for (int cellX = minCellX; cellX <= maxCellX; cellX++)
			{
				long cellKey = CellKey(cellX, cellZ);
				if (!cellsByKey.TryGetValue(cellKey, out var bucket))
				{
					bucket = new List<ulong>(4);
					cellsByKey[cellKey] = bucket;
				}
				bucket.Add(entityID);
			}
		}
	}

	public void Remove(int dimension, int minX, int minZ, int maxX, int maxZ, ulong entityID)
	{
		if (!CellsByDimension.TryGetValue(dimension, out var cellsByKey)) return;

		if (minX > maxX) (minX, maxX) = (maxX, minX);
		if (minZ > maxZ) (minZ, maxZ) = (maxZ, minZ);

		int minCellX = minX >> CellShift;
		int maxCellX = maxX >> CellShift;
		int minCellZ = minZ >> CellShift;
		int maxCellZ = maxZ >> CellShift;

		for (int cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
		{
			for (int cellX = minCellX; cellX <= maxCellX; cellX++)
			{
				long cellKey = CellKey(cellX, cellZ);
				if (!cellsByKey.TryGetValue(cellKey, out var bucket)) continue;

				for (int bucketIndex = 0; bucketIndex < bucket.Count; bucketIndex++)
				{
					if (bucket[bucketIndex] != entityID) continue;
					int lastIndex = bucket.Count - 1;
					bucket[bucketIndex] = bucket[lastIndex];
					bucket.RemoveAt(lastIndex);
					break;
				}

				if (bucket.Count == 0) cellsByKey.Remove(cellKey);
			}
		}

		if (cellsByKey.Count == 0) CellsByDimension.Remove(dimension);
	}

	/// Collects unique candidate ids near a block position within radius blocks.
	/// Long edges may span several cells, so uniqueness is enforced inside the grid rather than making every caller allocate its own deduplication set.
	public int CollectNear(int dimension, int blockX, int blockZ, int radiusBlocks, List<ulong> collectedIDs, bool clear = true)
	{
		if (QueryInProgress) { throw new InvalidOperationException("HashGrid2D.CollectNear() is not reentrant."); }

		QueryInProgress = true;
		try
		{
			QuerySeenIDs.Clear();
			if (clear) { collectedIDs.Clear(); }
			else { for (int collectedIndex = 0; collectedIndex < collectedIDs.Count; collectedIndex++) QuerySeenIDs.Add(collectedIDs[collectedIndex]); }

			if (!CellsByDimension.TryGetValue(dimension, out var cellsByKey)) return collectedIDs.Count;

			int centerCellX = blockX >> CellShift;
			int centerCellZ = blockZ >> CellShift;
			int radiusCells = (radiusBlocks + (CellSize - 1)) >> CellShift;

			for (int cellZ = centerCellZ - radiusCells; cellZ <= centerCellZ + radiusCells; cellZ++)
			{
				for (int cellX = centerCellX - radiusCells; cellX <= centerCellX + radiusCells; cellX++)
				{
					if (!cellsByKey.TryGetValue(CellKey(cellX, cellZ), out var bucket) || bucket.Count == 0) continue;
					for (int bucketIndex = 0; bucketIndex < bucket.Count; bucketIndex++)
					{
						ulong entityID = bucket[bucketIndex];
						if (QuerySeenIDs.Add(entityID)) collectedIDs.Add(entityID);
					}
				}
			}

			return collectedIDs.Count;
		}
		finally
		{
			QuerySeenIDs.Clear();
			QueryInProgress = false;
		}
	}
}
