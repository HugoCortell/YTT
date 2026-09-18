using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace YangTransport;

internal readonly record struct RailRouteChunkColumn(int Dimension, int ChunkX, int ChunkY, int ChunkZ);

internal readonly struct ConvoyRouteRun
{
	public readonly ulong EdgeHash;
	public readonly double S0;
	public readonly double S1;
	public readonly double EdgeS0;
	public readonly double EdgeS1;
	public readonly int CursorDirectionForPositiveS;

	public ConvoyRouteRun(ulong edgeHash, double s0, double s1, double edgeS0, double edgeS1, int cursorDirectionForPositiveS)
	{
		EdgeHash = edgeHash;
		S0 = s0;
		S1 = s1;
		EdgeS0 = edgeS0;
		EdgeS1 = edgeS1;
		CursorDirectionForPositiveS = cursorDirectionForPositiveS >= 0 ? 1 : -1;
	}
}

internal readonly struct ConvoyRouteProjection
{
	public readonly bool Found;
	public readonly double PathS;
	public readonly double DistanceSquared;
	public readonly bool ClampedLow;
	public readonly bool ClampedHigh;

	public ConvoyRouteProjection(bool found, double pathS, double distanceSquared, bool clampedLow, bool clampedHigh)
	{
		Found = found;
		PathS = pathS;
		DistanceSquared = distanceSquared;
		ClampedLow = clampedLow;
		ClampedHigh = clampedHigh;
	}
}

internal struct ConvoyRouteProjectionHint
{
	public int RunIndex;
	public double LastPathS;
	public int Epoch;
	public uint Revision;
	public bool HasPathS;

	public void Reset()
	{
		RunIndex = -1;
		LastPathS = 0;
		Epoch = int.MinValue;
		Revision = uint.MaxValue;
		HasPathS = false;
	}
}

internal enum ConvoyRouteChangeKind : byte
{
	Append = 1,
	Prepend = 2,
	ReplaceHigh = 3,
	ReplaceLow = 4,
	TrimLow = 5,
	TrimHigh = 6
}

internal readonly struct ConvoyRouteChange
{
	public readonly uint BaseRevision;
	public readonly uint NewRevision;
	public readonly ConvoyRouteChangeKind Kind;
	public readonly ConvoyRouteRun Run;
	public readonly double BoundaryS;

	public ConvoyRouteChange(uint baseRevision, uint newRevision, ConvoyRouteChangeKind kind, in ConvoyRouteRun run, double boundaryS)
	{
		BaseRevision = baseRevision;
		NewRevision = newRevision;
		Kind = kind;
		Run = run;
		BoundaryS = boundaryS;
	}
}

/// Bounded, bidirectional graph route for a convoy.
/// The route is a deque: ordinary movement may only prepend, append, trim-low, or trim-high.
/// Stable path coordinates are independent of current travel direction and convoy order never reverses.
internal sealed class ConvoyRoute
{
	private const double Epsilon = 1e-7;
	private const double JoinTolerance = 1e-4;
	private const double OccupancyOverlapEpsilon = 1e-9;
	private const int MaxJournalChanges = 512;

	// Embedded route data is not length-prefixed, so these sentinels catch stream misalignment.
	private const int RouteDataStartSentinel = 0x53545259;	// YRTS
	private const int RouteDataEndSentinel = 0x45545259;	// YRTE

	private static int NextEpochCounter;

	private ConvoyRouteRun[] RouteRuns = new ConvoyRouteRun[32];
	private int FirstRunIndex;
	private int RunCount;
	private readonly ConvoyRouteChangeJournal ChangeJournal = new(MaxJournalChanges);
	private readonly List<ulong> GeometryEdgeScratch = new(32);
	private readonly HashSet<ulong> GeometryEdgeSeen = new();
	private Dictionary<ulong, RailEdgeGeometry>? ReplicaGeometry;
	private bool JournalEnabled;
	private uint JournalPublishedRevision;
	private bool AuthoritativeValidationRequired;

	private byte[]? CachedSnapshot;
	private int CachedSnapshotEpoch;
	private int CachedSnapshotGraphVersion;
	private uint CachedSnapshotRevision;
	private byte[]? CachedDelta;
	private int CachedDeltaEpoch;
	private int CachedDeltaGraphVersion;
	private uint CachedDeltaBaseRevision;
	private uint CachedDeltaRevision;

	public int Count => RunCount;
	public int GraphVersion { get; private set; } = -1;
	public byte Gauge { get; private set; }
	public double MinS { get; private set; }
	public double MaxS { get; private set; }
	public int Epoch { get; private set; }
	public uint Revision { get; private set; }
	public uint TopologyRevision { get; private set; }

	public void EnableChangeJournal()
	{
		if (JournalEnabled) return;
		ChangeJournal.Clear();
		JournalPublishedRevision = Revision;
		JournalEnabled = true;
	}

	public void DisableChangeJournal()
	{
		if (!JournalEnabled && ChangeJournal.Count == 0) return;
		JournalEnabled = false;
		JournalPublishedRevision = Revision;
		ChangeJournal.Clear();
	}

	public void MarkJournalPublished() { if (JournalEnabled) JournalPublishedRevision = Revision; }

	public ConvoyRoute() { Epoch = NextEpoch(); }
	private ConvoyRoute(int epoch) { Epoch = epoch > 0 ? epoch : NextEpoch(); ObserveEpoch(Epoch); }

	private static int NextEpoch()
	{
		int value = Interlocked.Increment(ref NextEpochCounter);
		return value == 0 ? Interlocked.Increment(ref NextEpochCounter) : value;
	}

	private static void ObserveEpoch(int epoch)
	{
		if (epoch <= 0) return;
		int observed = Volatile.Read(ref NextEpochCounter);
		while (observed < epoch)
		{
			int previous = Interlocked.CompareExchange(ref NextEpochCounter, epoch, observed);
			if (previous == observed) return;
			observed = previous;
		}
	}

	private int PhysicalIndex(int logicalIndex) { return (FirstRunIndex + logicalIndex) % RouteRuns.Length; }

	private ConvoyRouteRun GetRun(int logicalIndex) { return RouteRuns[PhysicalIndex(logicalIndex)]; }
	private void SetRun(int logicalIndex, in ConvoyRouteRun run) { RouteRuns[PhysicalIndex(logicalIndex)] = run; }

	private void EnsureCapacity(int required)
	{
		if (required <= RouteRuns.Length) return;

		int capacity = RouteRuns.Length;
		while (capacity < required) capacity *= 2;

		var replacement = new ConvoyRouteRun[capacity];
		for (int iteration = 0; iteration < RunCount; iteration++) replacement[iteration] = GetRun(iteration);
		RouteRuns = replacement;
		FirstRunIndex = 0;
	}

	private void AddFirstRaw(in ConvoyRouteRun run)
	{
		EnsureCapacity(RunCount + 1);
		FirstRunIndex = (FirstRunIndex - 1 + RouteRuns.Length) % RouteRuns.Length;
		RouteRuns[FirstRunIndex] = run;
		RunCount++;
	}

	private void AddLastRaw(in ConvoyRouteRun run)
	{
		EnsureCapacity(RunCount + 1);
		RouteRuns[PhysicalIndex(RunCount)] = run;
		RunCount++;
	}

	private void RemoveFirstRaw()
	{
		if (RunCount <= 0) return;
		RouteRuns[FirstRunIndex] = default;
		FirstRunIndex = (FirstRunIndex + 1) % RouteRuns.Length;
		RunCount--;
		if (RunCount == 0) FirstRunIndex = 0;
	}

	private void RemoveLastRaw()
	{
		if (RunCount <= 0) return;
		int physicalIndex = PhysicalIndex(RunCount - 1);
		RouteRuns[physicalIndex] = default;
		RunCount--;
		if (RunCount == 0) FirstRunIndex = 0;
	}

	private void ClearRaw()
	{
		Array.Clear(RouteRuns, 0, RouteRuns.Length);
		FirstRunIndex = 0;
		RunCount = 0;
		MinS = MaxS = 0;
	}

	public void Clear()
	{
		ClearRaw();
		GraphVersion = -1;
		Gauge = 0;
		Epoch = NextEpoch();
		Revision = 0;
		TopologyRevision = 0;
		AuthoritativeValidationRequired = false;
		ChangeJournal.Clear();
		ClearSerializationCache();
	}

	private void ClearSerializationCache()
	{
		CachedSnapshot = null;
		CachedDelta = null;
	}

	public ConvoyRoute Clone()
	{
		var clone = new ConvoyRoute(Epoch)
		{
			GraphVersion = GraphVersion,
			Gauge = Gauge,
			MinS = MinS,
			MaxS = MaxS,
			Revision = Revision,
			TopologyRevision = TopologyRevision,
			AuthoritativeValidationRequired = AuthoritativeValidationRequired
		};

		clone.EnsureCapacity(RunCount);
		for (int iteration = 0; iteration < RunCount; iteration++) clone.AddLastRaw(GetRun(iteration));

		if (ReplicaGeometry != null && ReplicaGeometry.Count > 0) { clone.ReplicaGeometry = new Dictionary<ulong, RailEdgeGeometry>(ReplicaGeometry); }

		return clone;
	}

	internal ConvoyRoute CloneSnapshotWindow(double keepMinS, double keepMaxS)
	{
		ConvoyRoute clone = Clone();
		if (clone.RunCount == 0) return clone;
		if (keepMinS > keepMaxS) (keepMinS, keepMaxS) = (keepMaxS, keepMinS);

		keepMinS = GameMath.Clamp(keepMinS, clone.MinS, clone.MaxS);
		keepMaxS = GameMath.Clamp(keepMaxS, clone.MinS, clone.MaxS);
		if (keepMaxS < keepMinS) keepMaxS = keepMinS;

		clone.TrimLowRaw(keepMinS);
		clone.TrimHighRaw(keepMaxS);
		if (clone.RunCount == 0) return clone;

		clone.RefreshBounds();
		clone.ChangeJournal.Clear();
		clone.ClearSerializationCache();
		return clone;
	}


	public bool ValidateAuthoritative(RailGraphLive graph, byte gauge)
	{
		if (graph == null || RunCount == 0 || Gauge != gauge) return false;

		// Graph edits explicitly invalidate only routes indexed on touched edges.
		// An unrelated global build revision is therefore an instant promotion, not a full route walk.
		if (!AuthoritativeValidationRequired)
		{
			if (GraphVersion != graph.BuildVersion)
			{
				GraphVersion = graph.BuildVersion;
				ClearSerializationCache();
			}
			return true;
		}

		for (int iteration = 0; iteration < RunCount; iteration++)
		{
			ConvoyRouteRun run = GetRun(iteration);
			if (run.EdgeHash == 0) return false;
			if (!RailEdgeGeometryCache.TryGet(graph, gauge, run.EdgeHash, out RailEdgeGeometry geometry)) return false;

			double minEdgeS = Math.Min(run.EdgeS0, run.EdgeS1);
			double maxEdgeS = Math.Max(run.EdgeS0, run.EdgeS1);
			if (minEdgeS < -1e-4 || maxEdgeS > geometry.TotalLength + 1e-4) return false;
		}

		AuthoritativeValidationRequired = false;
		GraphVersion = graph.BuildVersion;
		ClearSerializationCache();
		return true;
	}

	internal void RequireAuthoritativeValidation() { AuthoritativeValidationRequired = true; }

	public bool Covers(double minS, double maxS, double epsilon = 1e-4)
	{
		if (RunCount == 0) return false;
		if (minS > maxS) (minS, maxS) = (maxS, minS);
		return MinS <= minS + epsilon && MaxS >= maxS - epsilon;
	}

	internal void CollectUniqueEdgeHashes(HashSet<ulong> destination, bool clear = true)
	{
		if (destination == null) return;
		if (clear) destination.Clear();

		for (int iteration = 0; iteration < RunCount; iteration++)
		{
			ulong edgeHash = GetRun(iteration).EdgeHash;
			if (edgeHash != 0) destination.Add(edgeHash);
		}
	}

	public bool ContainsAnyChangedEdge(RailGraphChangeSet change)
	{
		if (change == null || RunCount == 0) return false;
		if (change.GlobalInvalidation) return true;

		int changedCount = change.TouchedEdges.Count;
		if (changedCount == 0) return false;

		// Tiny edits are the common case and avoid binary-search overhead.
		// Large edits iterate the bounded route once and query the already-canonicalized change set.
		if (changedCount <= 4)
		{
			for (int iteration = 0; iteration < changedCount; iteration++)
			{
				ulong edgeHash = change.TouchedEdges[iteration];
				for (int runIndex = 0; runIndex < RunCount; runIndex++) { if (GetRun(runIndex).EdgeHash == edgeHash) return true; }
			}
			return false;
		}

		for (int runIndex = 0; runIndex < RunCount; runIndex++) { if (change.ContainsEdge(GetRun(runIndex).EdgeHash)) return true; }
		return false;
	}

	public bool TryPromoteAcrossUnrelatedChange(RailGraphChangeSet change, int graphVersion)
	{
		if (change == null) return false;
		if (change.GlobalInvalidation || ContainsAnyChangedEdge(change)) { RequireAuthoritativeValidation(); return false; }

		AuthoritativeValidationRequired = false;
		GraphVersion = graphVersion;
		ClearSerializationCache();
		return true;
	}

	public void AddSpan(int graphVersion, byte gauge, ulong edgeHash, double s0, double s1, double edgeS0, double edgeS1, int cursorDirectionForPositiveS, int traversedPathDirection = 0)
	{
		if (edgeHash == 0) return;
		if (Math.Abs(s1 - s0) <= Epsilon || Math.Abs(edgeS1 - edgeS0) <= Epsilon) return;

		if (s1 < s0)
		{
			(s0, s1) = (s1, s0);
			(edgeS0, edgeS1) = (edgeS1, edgeS0);
		}

		if (RunCount == 0)
		{
			GraphVersion = graphVersion;
			Gauge = gauge;
			AddHigh(new ConvoyRouteRun(edgeHash, s0, s1, edgeS0, edgeS1, cursorDirectionForPositiveS));
			return;
		}

		if (Gauge != gauge)
		{
			Clear();
			GraphVersion = graphVersion;
			Gauge = gauge;
			AddHigh(new ConvoyRouteRun(edgeHash, s0, s1, edgeS0, edgeS1, cursorDirectionForPositiveS));
			return;
		}

		GraphVersion = graphVersion;

		if (traversedPathDirection != 0)
		{
			ReconcileTraversedSpan(edgeHash, s0, s1, edgeS0, edgeS1, cursorDirectionForPositiveS, traversedPathDirection);
			if (RunCount == 0)
			{
				GraphVersion = graphVersion;
				Gauge = gauge;
				AddHigh(new ConvoyRouteRun(edgeHash, s0, s1, edgeS0, edgeS1, cursorDirectionForPositiveS));
				return;
			}
		}

		double originalS0 = s0;
		double originalS1 = s1;
		double originalEdgeS0 = edgeS0;
		double originalEdgeS1 = edgeS1;

		if (originalS0 < MinS - JoinTolerance)
		{
			double endS = Math.Min(originalS1, MinS);
			if (endS > originalS0 + Epsilon)
			{
				double alpha = (endS - originalS0) / Math.Max(Epsilon, originalS1 - originalS0);
				double endEdgeS = originalEdgeS0 + (originalEdgeS1 - originalEdgeS0) * alpha;
				AddLow(new ConvoyRouteRun(edgeHash, originalS0, endS, originalEdgeS0, endEdgeS, cursorDirectionForPositiveS));
			}
		}

		if (originalS1 > MaxS + JoinTolerance)
		{
			double startS = Math.Max(originalS0, MaxS);
			if (originalS1 > startS + Epsilon)
			{
				double alpha = (startS - originalS0) / Math.Max(Epsilon, originalS1 - originalS0);
				double startEdgeS = originalEdgeS0 + (originalEdgeS1 - originalEdgeS0) * alpha;
				AddHigh(new ConvoyRouteRun(edgeHash, startS, originalS1, startEdgeS, originalEdgeS1, cursorDirectionForPositiveS));
			}
		}
	}

	private void ReconcileTraversedSpan(ulong edgeHash, double s0, double s1, double edgeS0, double edgeS1, int cursorDirectionForPositiveS, int traversedPathDirection)
	{
		if (RunCount == 0 || s1 <= s0 + Epsilon) return;

		double overlapLow = Math.Max(s0, MinS);
		double overlapHigh = Math.Min(s1, MaxS);
		if (overlapHigh <= overlapLow + Epsilon) return;

		if (traversedPathDirection > 0)
		{
			double cursorS = overlapLow;
			int hint = -1;
			int index = FindRun(Math.Min(overlapHigh, cursorS + Math.Min(0.01, (overlapHigh - cursorS) * 0.5)), ref hint);
			if (index < 0) { Trim(MinS, cursorS); return; }

			while (index < RunCount && cursorS < overlapHigh - Epsilon)
			{
				ConvoyRouteRun existing = GetRun(index);
				if (existing.S0 > cursorS + JoinTolerance) { Trim(MinS, cursorS); return; }

				double segmentLow = Math.Max(cursorS, existing.S0);
				double segmentHigh = Math.Min(overlapHigh, existing.S1);
				if
				(
					segmentHigh <= segmentLow + Epsilon ||
					!SpanMappingMatches(existing, edgeHash, s0, s1, edgeS0, edgeS1, cursorDirectionForPositiveS, segmentLow, segmentHigh)
				) { Trim(MinS, segmentLow); return; }

				cursorS = segmentHigh;
				index++;
			}

			if (cursorS < overlapHigh - JoinTolerance) Trim(MinS, cursorS);
			return;
		}

		{
			double cursorS = overlapHigh;
			int hint = -1;
			int index = FindRun(Math.Max(overlapLow, cursorS - Math.Min(0.01, (cursorS - overlapLow) * 0.5)), ref hint);
			if (index < 0) { Trim(cursorS, MaxS); return; }

			while (index >= 0 && cursorS > overlapLow + Epsilon)
			{
				ConvoyRouteRun existing = GetRun(index);
				if (existing.S1 < cursorS - JoinTolerance) { Trim(cursorS, MaxS); return; }

				double segmentLow = Math.Max(overlapLow, existing.S0);
				double segmentHigh = Math.Min(cursorS, existing.S1);
				if
				(
					segmentHigh <= segmentLow + Epsilon ||
					!SpanMappingMatches(existing, edgeHash, s0, s1, edgeS0, edgeS1, cursorDirectionForPositiveS, segmentLow, segmentHigh)
				) { Trim(segmentHigh, MaxS); return; }

				cursorS = segmentLow;
				index--;
			}

			if (cursorS > overlapLow + JoinTolerance) Trim(cursorS, MaxS);
		}
	}

	private static bool SpanMappingMatches
	(
		in ConvoyRouteRun existing, ulong edgeHash, double spanS0, double spanS1, double spanEdgeS0,
		double spanEdgeS1, int cursorDirectionForPositiveS, double overlapS0, double overlapS1
	)
	{
		if (existing.EdgeHash != edgeHash) return false;
		if (existing.CursorDirectionForPositiveS != (cursorDirectionForPositiveS >= 0 ? 1 : -1)) return false;

		double spanLength = spanS1 - spanS0;
		double existingLength = existing.S1 - existing.S0;
		if (spanLength <= Epsilon || existingLength <= Epsilon) return false;

		double spanSlope = (spanEdgeS1 - spanEdgeS0) / spanLength;
		double existingSlope = (existing.EdgeS1 - existing.EdgeS0) / existingLength;
		double slopeScale = Math.Max(1.0, Math.Max(Math.Abs(spanSlope), Math.Abs(existingSlope)));
		if (Math.Abs(spanSlope - existingSlope) > 1e-6 * slopeScale) return false;

		double probeS = (overlapS0 + overlapS1) * 0.5;
		double expectedEdgeS = spanEdgeS0 + (probeS - spanS0) * spanSlope;
		double existingEdgeS = existing.EdgeS0 + (probeS - existing.S0) * existingSlope;
		return Math.Abs(existingEdgeS - expectedEdgeS) <= 1e-3;
	}

	private void AddLow(in ConvoyRouteRun run)
	{
		if (RunCount == 0)
		{
			AddFirstRaw(run);
			RefreshBounds();
			RecordChange(ConvoyRouteChangeKind.Prepend, run, 0);
			return;
		}

		ConvoyRouteRun current = GetRun(0);
		if (TryMerge(run, current, out ConvoyRouteRun merged))
		{
			SetRun(0, merged);
			RefreshBounds();
			RecordChange(ConvoyRouteChangeKind.ReplaceLow, merged, 0);
			return;
		}

		if (Math.Abs(run.S1 - current.S0) > JoinTolerance) return;
		AddFirstRaw(run);
		RefreshBounds();
		RecordChange(ConvoyRouteChangeKind.Prepend, run, 0);
	}

	private void AddHigh(in ConvoyRouteRun run)
	{
		if (RunCount == 0)
		{
			AddLastRaw(run);
			RefreshBounds();
			RecordChange(ConvoyRouteChangeKind.Append, run, 0);
			return;
		}

		ConvoyRouteRun current = GetRun(RunCount - 1);
		if (TryMerge(current, run, out ConvoyRouteRun merged))
		{
			SetRun(RunCount - 1, merged);
			RefreshBounds();
			RecordChange(ConvoyRouteChangeKind.ReplaceHigh, merged, 0);
			return;
		}

		if (Math.Abs(current.S1 - run.S0) > JoinTolerance) return;
		AddLastRaw(run);
		RefreshBounds();
		RecordChange(ConvoyRouteChangeKind.Append, run, 0);
	}

	private static bool TryMerge(in ConvoyRouteRun firstRun, in ConvoyRouteRun secondRun, out ConvoyRouteRun merged)
	{
		merged = default;
		if (firstRun.EdgeHash != secondRun.EdgeHash) return false;
		if (firstRun.CursorDirectionForPositiveS != secondRun.CursorDirectionForPositiveS) return false;
		if (Math.Abs(firstRun.S1 - secondRun.S0) > JoinTolerance) return false;
		if (Math.Abs(firstRun.EdgeS1 - secondRun.EdgeS0) > 1e-4) return false;

		double aPathLength = firstRun.S1 - firstRun.S0;
		double bPathLength = secondRun.S1 - secondRun.S0;
		if (aPathLength <= Epsilon || bPathLength <= Epsilon) return false;

		double slopeA = (firstRun.EdgeS1 - firstRun.EdgeS0) / aPathLength;
		double slopeB = (secondRun.EdgeS1 - secondRun.EdgeS0) / bPathLength;
		double scale = Math.Max(1.0, Math.Max(Math.Abs(slopeA), Math.Abs(slopeB)));
		if (Math.Abs(slopeA - slopeB) > 1e-6 * scale) return false;

		merged = new ConvoyRouteRun(firstRun.EdgeHash, firstRun.S0, secondRun.S1, firstRun.EdgeS0, secondRun.EdgeS1, firstRun.CursorDirectionForPositiveS);
		return true;
	}

	public void Trim(double keepMinS, double keepMaxS)
	{
		if (RunCount == 0) return;
		if (keepMinS > keepMaxS) (keepMinS, keepMaxS) = (keepMaxS, keepMinS);

		bool lowChanged = keepMinS > MinS + Epsilon;
		bool highChanged = keepMaxS < MaxS - Epsilon;
		if (!lowChanged && !highChanged) return;

		if (lowChanged) TrimLowRaw(keepMinS);
		if (highChanged && RunCount > 0) TrimHighRaw(keepMaxS);

		if (RunCount == 0) { Clear(); return; }

		RefreshBounds();
		if (lowChanged) RecordChange(ConvoyRouteChangeKind.TrimLow, default, MinS);
		if (highChanged) RecordChange(ConvoyRouteChangeKind.TrimHigh, default, MaxS);
	}

	private void TrimLowRaw(double keepMinS)
	{
		while (RunCount > 0 && GetRun(0).S1 <= keepMinS + Epsilon) RemoveFirstRaw();
		if (RunCount == 0) return;

		ConvoyRouteRun run = GetRun(0);
		if (keepMinS <= run.S0 + Epsilon) return;

		double alpha = (keepMinS - run.S0) / Math.Max(Epsilon, run.S1 - run.S0);
		double edgeS = run.EdgeS0 + (run.EdgeS1 - run.EdgeS0) * alpha;
		SetRun(0, new ConvoyRouteRun(run.EdgeHash, keepMinS, run.S1, edgeS, run.EdgeS1, run.CursorDirectionForPositiveS));
	}

	private void TrimHighRaw(double keepMaxS)
	{
		while (RunCount > 0 && GetRun(RunCount - 1).S0 >= keepMaxS - Epsilon) RemoveLastRaw();
		if (RunCount == 0) return;

		ConvoyRouteRun run = GetRun(RunCount - 1);
		if (keepMaxS >= run.S1 - Epsilon) return;

		double alpha = (keepMaxS - run.S0) / Math.Max(Epsilon, run.S1 - run.S0);
		double edgeS = run.EdgeS0 + (run.EdgeS1 - run.EdgeS0) * alpha;
		SetRun(RunCount - 1, new ConvoyRouteRun(run.EdgeHash, run.S0, keepMaxS, run.EdgeS0, edgeS, run.CursorDirectionForPositiveS));
	}

	/// Keeps bounded branch history at both physical ends. Direction changes therefore replay the recorded route instead of reconstructing it from switch heuristics.
	/// ReconcileTraversedSpan replaces only the side that actually diverges at a junction.
	public void TrimForMovement(double occupiedMinS, double occupiedMaxS, int movementSign, double historyRetention, double leadingLookahead = 0)
	{
		historyRetention = Math.Max(0, historyRetention);
		leadingLookahead = Math.Max(0, leadingLookahead);

		double lowReserve = historyRetention;
		double highReserve = historyRetention;
		if (movementSign >= 0) highReserve = Math.Max(highReserve, leadingLookahead);
		else lowReserve = Math.Max(lowReserve, leadingLookahead);

		TrimWholeRunsOutside(occupiedMinS - lowReserve, occupiedMaxS + highReserve);
	}

	/// Drops complete old runs while preserving the complete boundary runs that intersect the retained interval
	/// This keeps deterministic coverage to the end of the currently selected edge without repeatedly clipping and re-appending it every simulation tick.
	public void TrimWholeRunsOutside(double keepMinS, double keepMaxS)
	{
		if (RunCount == 0) return;
		if (keepMinS > keepMaxS) (keepMinS, keepMaxS) = (keepMaxS, keepMinS);

		// The retained interval should always intersect the occupied convoy route
		//  Keep the route intact on a bad caller range so recovery can reseed from the existing cursor.
		if (keepMaxS < MinS - JoinTolerance || keepMinS > MaxS + JoinTolerance) return;

		bool trimmedLow = false;
		bool trimmedHigh = false;

		while (RunCount > 1 && GetRun(0).S1 < keepMinS - JoinTolerance) { RemoveFirstRaw(); trimmedLow = true; }
		while (RunCount > 1 && GetRun(RunCount - 1).S0 > keepMaxS + JoinTolerance) { RemoveLastRaw(); trimmedHigh = true; }

		if (!trimmedLow && !trimmedHigh) return;

		RefreshBounds();
		if (trimmedLow) RecordChange(ConvoyRouteChangeKind.TrimLow, default, MinS);
		if (trimmedHigh) RecordChange(ConvoyRouteChangeKind.TrimHigh, default, MaxS);
	}

	private void RefreshBounds()
	{
		if (RunCount == 0) { MinS = MaxS = 0; return; }

		MinS = GetRun(0).S0;
		MaxS = GetRun(RunCount - 1).S1;
	}

	private void RecordChange(ConvoyRouteChangeKind kind, in ConvoyRouteRun run, double boundaryS)
	{
		bool topologyChanged = kind != ConvoyRouteChangeKind.ReplaceHigh && kind != ConvoyRouteChangeKind.ReplaceLow;
		
		// Keep only the newest unpublished end-run boundary. Once that revision is sent, MarkJournalPublished() starts a new delta boundary for subsequent movement.
		if (!topologyChanged && JournalEnabled && Revision != JournalPublishedRevision && ChangeJournal.Count > 0)
		{
			int lastIndex = ChangeJournal.Count - 1;
			ConvoyRouteChange last = ChangeJournal[lastIndex];
			if (last.Kind == kind && last.NewRevision == Revision)
			{
				ChangeJournal[lastIndex] = new ConvoyRouteChange(last.BaseRevision, last.NewRevision, kind, run, boundaryS);
				ClearSerializationCache();
				return;
			}
		}

		if (Revision == uint.MaxValue)
		{
			Epoch = NextEpoch();
			Revision = 0;
			TopologyRevision = 0;
			ChangeJournal.Clear();
			return;
		}

		uint baseRevision = Revision;
		Revision++;
		if (topologyChanged) TopologyRevision++;
		if (!JournalEnabled) return;

		ChangeJournal.Add(new ConvoyRouteChange(baseRevision, Revision, kind, run, boundaryS));
	}

	/// Extends only through unambiguous, same-gauge transitions.
	/// It never predicts a switch choice, so the extra client coverage cannot select a branch on behalf of manual controls or automation.
	public void ExtendDeterministicLookahead(RailGraphLive graph, byte gauge, int pathDirection, double leadingBoundaryS, double lookaheadBlocks)
	{
		if (graph == null || RunCount == 0 || Gauge != gauge || lookaheadBlocks <= Epsilon) return;
		pathDirection = pathDirection >= 0 ? 1 : -1;

		double targetS = leadingBoundaryS + pathDirection * lookaheadBlocks;
		int guard = 0;

		while (guard++ < 4096)
		{
			if (pathDirection > 0) { if (MaxS >= targetS - JoinTolerance) return; }
			else if (MinS <= targetS + JoinTolerance) return;

			ConvoyRouteRun current = pathDirection > 0 ? GetRun(RunCount - 1) : GetRun(0);
			if (!RailEdgeGeometryCache.TryGet(graph, gauge, current.EdgeHash, out RailEdgeGeometry currentGeometry)) return;
			if (!graph.TryGetEdgeEndpoints(current.EdgeHash, out RailGraphLive.EndpointKey currentA, out RailGraphLive.EndpointKey currentB, out byte currentGauge) || currentGauge != gauge) return;

			double boundaryEdgeS = pathDirection > 0 ? current.EdgeS1 : current.EdgeS0;
			RailGraphLive.EndpointKey lowEndpoint = currentA.CompareTo(currentB) <= 0 ? currentA : currentB;
			RailGraphLive.EndpointKey highEndpoint = currentA.CompareTo(currentB) <= 0 ? currentB : currentA;
			RailGraphLive.EndpointKey endpoint;
			if (Math.Abs(boundaryEdgeS) <= JoinTolerance) endpoint = lowEndpoint;
			else if (Math.Abs(boundaryEdgeS - currentGeometry.TotalLength) <= JoinTolerance) endpoint = highEndpoint;
			else return;

			if (!graph.TryGetIncidentEdges(endpoint, out List<ulong> incident) || incident == null) return;

			ulong nextEdgeHash = 0;
			int[]? nextPoints = null;
			RailGraphLive.EndpointKey nextA = default;
			RailGraphLive.EndpointKey nextB = default;
			int validCount = 0;

			for (int iteration = 0; iteration < incident.Count; iteration++)
			{
				ulong candidateHash = incident[iteration];
				if (candidateHash == 0 || candidateHash == current.EdgeHash) continue;
				if (!graph.TryGetEdgeGauge(candidateHash, out byte candidateGauge) || candidateGauge != gauge) continue;
				if (!graph.TryGetPolyline16(candidateHash, out int[] candidatePoints) || candidatePoints == null || candidatePoints.Length < 6) continue;
				if (!graph.TryGetEdgeEndpoints(candidateHash, out RailGraphLive.EndpointKey candidateA, out RailGraphLive.EndpointKey candidateB, out _)) continue;
				if (!endpoint.Equals(candidateA) && !endpoint.Equals(candidateB)) continue;
				if (!RailTransitionRules.IsDirectedTransitionAllowedSameGauge(endpoint, gauge, currentGeometry.PointCoordinates16, candidatePoints)) continue;

				validCount++;
				if (validCount > 1) return;
				nextEdgeHash = candidateHash;
				nextPoints = candidatePoints;
				nextA = candidateA;
				nextB = candidateB;
			}

			if (validCount != 1 || nextEdgeHash == 0 || nextPoints == null) return;
			if (!RailEdgeGeometryCache.TryGet(graph, gauge, nextEdgeHash, out RailEdgeGeometry nextGeometry)) return;

			int physicalDirectionFromEndpoint;
			if (endpoint.Equals(nextA)) physicalDirectionFromEndpoint = 1;
			else if (endpoint.Equals(nextB)) physicalDirectionFromEndpoint = -1;
			else return;

			RailGraphLive.EndpointKey nextLow = nextA.CompareTo(nextB) <= 0 ? nextA : nextB;
			double startCanonicalS = endpoint.Equals(nextLow) ? 0 : nextGeometry.TotalLength;
			double endCanonicalS = startCanonicalS <= JoinTolerance ? nextGeometry.TotalLength : 0;

			if (pathDirection > 0)
			{
				double startS = MaxS;
				AddSpan(graph.BuildVersion, gauge, nextEdgeHash, startS, startS + nextGeometry.TotalLength, startCanonicalS, endCanonicalS, physicalDirectionFromEndpoint);
			}
			else
			{
				double endS = MinS;
				AddSpan(graph.BuildVersion, gauge, nextEdgeHash, endS - nextGeometry.TotalLength, endS, endCanonicalS, startCanonicalS, -physicalDirectionFromEndpoint);
			}
		}
	}


	/// Returns the path distance from fromS to the next occupancy-zone boundary (or the retained route end).
	/// This lets virtual convoys schedule the next safety-relevant event instead of polling at a fixed rate.
	internal bool TryGetDistanceToNextZoneBoundary(RailGraphLive graph, double fromS, int pathDirection, out double distance)
	{
		distance = 0;
		if (graph == null || RunCount == 0) return false;
		pathDirection = pathDirection >= 0 ? 1 : -1;

		int hint = -1;
		int index = FindRun(fromS, ref hint);
		if (index < 0) return false;

		ConvoyRouteRun current = GetRun(index);
		if (!graph.TryGetOccupancyZoneID(current.EdgeHash, out ulong currentZone) || currentZone == 0) return false;

		if (pathDirection > 0)
		{
			for (int iteration = index; iteration < RunCount; iteration++)
			{
				ConvoyRouteRun run = GetRun(iteration);
				double boundaryS = run.S1;
				if (iteration + 1 >= RunCount) { distance = Math.Max(0, boundaryS - fromS); return true; }

				ConvoyRouteRun next = GetRun(iteration + 1);
				if (!graph.TryGetOccupancyZoneID(next.EdgeHash, out ulong nextZone) || nextZone == 0 || nextZone != currentZone)
				{
					distance = Math.Max(0, boundaryS - fromS);
					return true;
				}
				currentZone = nextZone;
			}
		}
		else
		{
			for (int iteration = index; iteration >= 0; iteration--)
			{
				ConvoyRouteRun run = GetRun(iteration);
				double boundaryS = run.S0;
				if (iteration == 0) { distance = Math.Max(0, fromS - boundaryS); return true; }

				ConvoyRouteRun next = GetRun(iteration - 1);
				if (!graph.TryGetOccupancyZoneID(next.EdgeHash, out ulong nextZone) || nextZone == 0 || nextZone != currentZone)
				{
					distance = Math.Max(0, fromS - boundaryS);
					return true;
				}
				currentZone = nextZone;
			}
		}

		return false;
	}



	internal bool CollectChunkColumnsAlongPath(RailGraphLive graph, double fromS, int pathDirection, double maxDistance, Dictionary<RailRouteChunkColumn, double> destination)
	{
		destination.Clear();
		if (graph == null || RunCount == 0 || maxDistance < 0) return false;

		pathDirection = pathDirection >= 0 ? 1 : -1;
		int hint = -1;
		int runIndex = FindRun(fromS, ref hint);
		if (runIndex < 0) return false;

		double targetS = pathDirection > 0
			? Math.Min(MaxS, fromS + maxDistance)
			: Math.Max(MinS, fromS - maxDistance);
		double currentS = fromS;

		while ((pathDirection > 0 && currentS <= targetS + JoinTolerance && runIndex < RunCount) || (pathDirection < 0 && currentS >= targetS - JoinTolerance && runIndex >= 0))
		{
			ConvoyRouteRun run = GetRun(runIndex);
			double runTargetS = pathDirection > 0 ? Math.Min(run.S1, targetS) : Math.Max(run.S0, targetS);

			if (!RailEdgeGeometryCache.TryGet(graph, Gauge, run.EdgeHash, out RailEdgeGeometry geometry) || !graph.TryGetEdgeEndpoints(run.EdgeHash, out RailGraphLive.EndpointKey endpoint, out _, out _))
			{
				return false;
			}

			double canonicalStart = PathSToCanonical(run, currentS);
			double canonicalEnd = PathSToCanonical(run, runTargetS);
			double distanceOffset = Math.Abs(currentS - fromS);
			CollectGeometryChunkColumns(geometry, canonicalStart, canonicalEnd, endpoint.Dimension, distanceOffset, destination);

			if (Math.Abs(runTargetS - targetS) <= JoinTolerance) break;
			currentS = runTargetS;
			runIndex += pathDirection;
		}

		return destination.Count != 0;
	}

	private static double PathSToCanonical(in ConvoyRouteRun run, double pathS)
	{
		double span = run.S1 - run.S0;
		if (Math.Abs(span) <= 1e-12) return run.EdgeS0;
		double progress = GameMath.Clamp((pathS - run.S0) / span, 0, 1);
		return run.EdgeS0 + (run.EdgeS1 - run.EdgeS0) * progress;
	}

	private static void CollectGeometryChunkColumns(RailEdgeGeometry geometry, double canonicalStart, double canonicalEnd, int dimension, double distanceOffset, Dictionary<RailRouteChunkColumn, double> destination)
	{
		double storedStart	= geometry.StoredOrderIsCanonical ? canonicalStart : geometry.TotalLength - canonicalStart;
		double storedEnd	= geometry.StoredOrderIsCanonical ? canonicalEnd : geometry.TotalLength - canonicalEnd;
		storedStart = GameMath.Clamp(storedStart, 0, geometry.TotalLength);
		storedEnd = GameMath.Clamp(storedEnd, 0, geometry.TotalLength);

		if (!TryPointAtStoredDistance(geometry, storedStart, out double x, out double y, out double z, out int segmentIndex))
			return;

		double walked = 0;
		if (storedEnd >= storedStart)
		{
			for (int vertex = segmentIndex + 1; vertex < geometry.PointCount; vertex++)
			{
				double vertexS = geometry.CumulativeLengths[vertex];
				if (vertexS >= storedEnd - 1e-9) break;
				GetGeometryPoint(geometry, vertex, out double vx, out double vy, out double vz);
				double segmentLength = ThreeDimensionalDistance(x, y, z, vx, vy, vz);
				CollectLineChunkColumns(x, y, z, vx, vy, vz, dimension, distanceOffset + walked, segmentLength, destination);
				walked += segmentLength;
				x = vx; y = vy; z = vz;
			}
		}
		else
		{
			for (int vertex = segmentIndex; vertex >= 0; vertex--)
			{
				double vertexS = geometry.CumulativeLengths[vertex];
				if (vertexS <= storedEnd + 1e-9) break;
				GetGeometryPoint(geometry, vertex, out double vx, out double vy, out double vz);
				double segmentLength = ThreeDimensionalDistance(x, y, z, vx, vy, vz);
				CollectLineChunkColumns(x, y, z, vx, vy, vz, dimension, distanceOffset + walked, segmentLength, destination);
				walked += segmentLength;
				x = vx; y = vy; z = vz;
			}
		}

		if (TryPointAtStoredDistance(geometry, storedEnd, out double endX, out double endY, out double endZ, out _))
		{
			double segmentLength = ThreeDimensionalDistance(x, y, z, endX, endY, endZ);
			CollectLineChunkColumns(x, y, z, endX, endY, endZ, dimension, distanceOffset + walked, segmentLength, destination);
		}
	}

	private static bool TryPointAtStoredDistance(RailEdgeGeometry geometry, double storedS, out double x, out double y, out double z, out int segmentIndex)
	{
		x = y = z = 0;
		segmentIndex = 0;
		if (geometry.PointCount < 2) return false;

		storedS = GameMath.Clamp(storedS, 0, geometry.TotalLength);
		int lowIndex = 0;
		int highIndex = geometry.PointCount - 2;
		while (lowIndex <= highIndex)
		{
			int midpointIndex = (lowIndex + highIndex) >> 1;
			if (geometry.CumulativeLengths[midpointIndex + 1] < storedS) lowIndex = midpointIndex + 1;
			else if (geometry.CumulativeLengths[midpointIndex] > storedS) highIndex = midpointIndex - 1;
			else { segmentIndex = midpointIndex; break; }
		}
		if (lowIndex > highIndex) segmentIndex = Math.Max(0, Math.Min(geometry.PointCount - 2, lowIndex));

		double s0 = geometry.CumulativeLengths[segmentIndex];
		double s1 = geometry.CumulativeLengths[segmentIndex + 1];
		double progress = s1 - s0 <= 1e-12 ? 0 : GameMath.Clamp((storedS - s0) / (s1 - s0), 0, 1);
		GetGeometryPoint(geometry, segmentIndex, out double ax, out double ay, out double az);
		GetGeometryPoint(geometry, segmentIndex + 1, out double bx, out double by, out double bz);
		x = ax + (bx - ax) * progress;
		y = ay + (by - ay) * progress;
		z = az + (bz - az) * progress;
		return true;
	}

	private static void GetGeometryPoint(RailEdgeGeometry geometry, int pointIndex, out double x, out double y, out double z)
	{
		int offset = pointIndex * 3;
		x = geometry.PointCoordinates16[offset] * (1.0 / 16.0);
		y = geometry.PointCoordinates16[offset + 1] * (1.0 / 16.0);
		z = geometry.PointCoordinates16[offset + 2] * (1.0 / 16.0);
	}

	private static double ThreeDimensionalDistance(double ax, double ay, double az, double bx, double by, double bz)
	{
		double dx = bx - ax; double dy = by - ay; double dz = bz - az;
		return Math.Sqrt(dx * dx + dy * dy + dz * dz);
	}

	private static void CollectLineChunkColumns
	(
		double x0, double y0, double z0, double x1, double y1, double z1,
		int dimension, double pathDistanceOffset, double lineLength, Dictionary<RailRouteChunkColumn, double> destination
	)
	{
		double chunkSize = GlobalConstants.ChunkSize;
		int chunkX = (int)Math.Floor(x0 / chunkSize);
		int chunkY = (int)Math.Floor(y0 / chunkSize);
		int chunkZ = (int)Math.Floor(z0 / chunkSize);
		AddChunkCrossing(new RailRouteChunkColumn(dimension, chunkX, chunkY, chunkZ), pathDistanceOffset, destination);

		double dx = x1 - x0;
		double dz = z1 - z0;
		if (Math.Abs(dx) <= 1e-12 && Math.Abs(dz) <= 1e-12) return;

		int stepX = Math.Sign(dx);
		int stepZ = Math.Sign(dz);
		double progressDeltaX = stepX == 0 ? double.PositiveInfinity : chunkSize / Math.Abs(dx);
		double progressDeltaZ = stepZ == 0 ? double.PositiveInfinity : chunkSize / Math.Abs(dz);
		double nextBoundaryX = stepX > 0 ? (chunkX + 1) * chunkSize : chunkX * chunkSize;
		double nextBoundaryZ = stepZ > 0 ? (chunkZ + 1) * chunkSize : chunkZ * chunkSize;
		double progressMaxX = stepX == 0 ? double.PositiveInfinity : (nextBoundaryX - x0) / dx;
		double progressMaxZ = stepZ == 0 ? double.PositiveInfinity : (nextBoundaryZ - z0) / dz;
		progressMaxX = Math.Max(0, progressMaxX);
		progressMaxZ = Math.Max(0, progressMaxZ);

		int guard = 0;
		while (guard++ < 1_000_000)
		{
			double progress;
			if (Math.Abs(progressMaxX - progressMaxZ) <= 1e-12)
			{
				progress = progressMaxX;
				if (progress > 1 + 1e-12) break;
				chunkX += stepX;
				chunkZ += stepZ;
				progressMaxX += progressDeltaX;
				progressMaxZ += progressDeltaZ;
			}
			else if (progressMaxX < progressMaxZ)
			{
				progress = progressMaxX;
				if (progress > 1 + 1e-12) break;
				chunkX += stepX;
				progressMaxX += progressDeltaX;
			}
			else
			{
				progress = progressMaxZ;
				if (progress > 1 + 1e-12) break;
				chunkZ += stepZ;
				progressMaxZ += progressDeltaZ;
			}

			AddChunkCrossing
			(
				new RailRouteChunkColumn(dimension, chunkX, (int)Math.Floor((y0 + (y1 - y0) * Math.Max(0, Math.Min(1, progress))) / chunkSize), chunkZ),
				pathDistanceOffset + Math.Max(0, Math.Min(1, progress)) * lineLength, destination
			);
		}
	}

	private static void AddChunkCrossing(RailRouteChunkColumn column, double distance, Dictionary<RailRouteChunkColumn, double> destination)
	{
		if (!destination.TryGetValue(column, out double previous) || distance < previous) destination[column] = distance;
	}


	public bool TrySampleAuthoritativeCursor(RailGraphLive graph, byte gauge, double s, out RailwayVehicleShared.RailCursor cursor)
	{
		int hint = -1;
		return TrySampleAuthoritativeCursor(graph, gauge, s, ref hint, out cursor);
	}

	public bool TrySampleAuthoritativeCursor(RailGraphLive graph, byte gauge, double sampleS, ref int runHint, out RailwayVehicleShared.RailCursor cursor)
	{
		cursor = default;
		if (graph == null || RunCount == 0 || Gauge != gauge) return false;

		int index = FindRun(sampleS, ref runHint);
		if (index < 0) return false;
		return TrySampleAuthoritativeCursorAtRun(graph, gauge, sampleS, index, out cursor);
	}

	/// Samples a movement authority boundary from the occupied side of a route join.
	/// Starting on the speculative/ahead run would skip the graph endpoint transition and could bypass switch, signal, one-way, or clearance checks.
	public bool TrySampleAuthoritativeCursorForTravel(RailGraphLive graph, byte gauge, double sampleS, int pathDirection, out RailwayVehicleShared.RailCursor cursor)
	{
		cursor = default;
		if (graph == null || RunCount == 0 || Gauge != gauge) return false;
		pathDirection = pathDirection >= 0 ? 1 : -1;

		int hint = -1;
		int index = FindRun(sampleS, ref hint);
		if (index < 0) return false;

		if (pathDirection > 0)
		{
			while (index > 0)
			{
				ConvoyRouteRun current = GetRun(index);
				ConvoyRouteRun previous = GetRun(index - 1);
				if (sampleS > current.S0 + JoinTolerance || previous.S1 < sampleS - JoinTolerance) break;
				index--;
			}
		}
		else
		{
			while (index + 1 < RunCount)
			{
				ConvoyRouteRun current = GetRun(index);
				ConvoyRouteRun next = GetRun(index + 1);
				if (sampleS < current.S1 - JoinTolerance || next.S0 > sampleS + JoinTolerance) break;
				index++;
			}
		}

		return TrySampleAuthoritativeCursorAtRun(graph, gauge, sampleS, index, out cursor);
	}

	private bool TrySampleAuthoritativeCursorAtRun(RailGraphLive graph, byte gauge, double sampleS, int index, out RailwayVehicleShared.RailCursor cursor)
	{
		cursor = default;
		if ((uint)index >= RunCount) return false;

		ConvoyRouteRun run = GetRun(index);
		double alpha = (sampleS - run.S0) / Math.Max(Epsilon, run.S1 - run.S0);
		alpha = GameMath.Clamp(alpha, 0.0, 1.0);
		double edgeS = run.EdgeS0 + (run.EdgeS1 - run.EdgeS0) * alpha;

		return RailGeometryUtility.TryCursorFromCanonicalS(graph, gauge, run.EdgeHash, edgeS, run.CursorDirectionForPositiveS, out cursor);
	}

	private int FindRun(double sampleS, ref int hint)
	{
		if ((uint)hint < RunCount)
		{
			ConvoyRouteRun current = GetRun(hint);
			if (sampleS >= current.S0 - JoinTolerance && sampleS <= current.S1 + JoinTolerance) return hint;

			if (hint + 1 < RunCount)
			{
				ConvoyRouteRun next = GetRun(hint + 1);
				if (sampleS >= next.S0 - JoinTolerance && sampleS <= next.S1 + JoinTolerance) { hint++; return hint; }
			}

			if (hint > 0)
			{
				ConvoyRouteRun previous = GetRun(hint - 1);
				if (sampleS >= previous.S0 - JoinTolerance && sampleS <= previous.S1 + JoinTolerance) { hint--; return hint; }
			}
		}

		int low = 0;
		int high = RunCount - 1;
		while (low <= high)
		{
			int midpointIndex = (low + high) >> 1;
			ConvoyRouteRun run = GetRun(midpointIndex);
			if (sampleS < run.S0 - JoinTolerance) high = midpointIndex - 1;
			else if (sampleS > run.S1 + JoinTolerance) low = midpointIndex + 1;
			else { hint = midpointIndex; return midpointIndex; }
		}

		return -1;
	}

	public bool TrySampleClientCursor(double sampleS, ref int runHint, out RailwayVehicleShared.RailCursor cursor)
	{
		cursor = default;
		if (RunCount == 0 || ReplicaGeometry == null) return false;

		int index = FindRun(sampleS, ref runHint);
		if (index < 0) return false;

		ConvoyRouteRun run = GetRun(index);
		if (!ReplicaGeometry.TryGetValue(run.EdgeHash, out RailEdgeGeometry? geometry)) return false;

		double alpha = (sampleS - run.S0) / Math.Max(Epsilon, run.S1 - run.S0);
		alpha = GameMath.Clamp(alpha, 0.0, 1.0);
		double edgeS = run.EdgeS0 + (run.EdgeS1 - run.EdgeS0) * alpha;
		return RailGeometryUtility.TryCursorFromGeometry(Gauge, run.EdgeHash, geometry, edgeS, run.CursorDirectionForPositiveS, GraphVersion, out cursor);
	}

	public bool TryProjectClientWorldPoint(double x, double y, double z, double expectedPathS, ref ConvoyRouteProjectionHint hint, out ConvoyRouteProjection projection)
	{
		const double localSearchRadius = 64.0;
		const double acceptLocalDistanceSquared = 4.0;

		projection = default;
		if (RunCount == 0 || ReplicaGeometry == null || ReplicaGeometry.Count == 0) return false;

		if (hint.Epoch != Epoch)
		{
			hint.Reset();
			hint.Epoch = Epoch;
		}

		if (!hint.HasPathS)
		{
			hint.LastPathS = GameMath.Clamp(expectedPathS, MinS, MaxS);
			hint.HasPathS = true;
		}

		if (hint.Revision != Revision || (uint)hint.RunIndex >= RunCount)
		{
			int rebased = -1;
			double rebaseS = GameMath.Clamp(hint.LastPathS, MinS, MaxS);
			FindRun(rebaseS, ref rebased);
			hint.RunIndex = rebased;
			hint.Revision = Revision;
		}

		double referenceS = GameMath.Clamp(hint.LastPathS, MinS, MaxS);
		int anchor = hint.RunIndex;
		if ((uint)anchor >= RunCount)
		{
			anchor = -1;
			FindRun(referenceS, ref anchor);
		}
		if (anchor < 0) return false;

		double bestDistanceSquared = double.MaxValue;
		double bestS = 0;
		int bestIndex = -1;
		bool bestClampedLow = false;
		bool bestClampedHigh = false;

		// The normal render path remains immediate, the last logical occurrence and its two neighbours cover ordinary motion across an edge join.
		// Wider route-S and global searches are recovery paths only.
		ConvoyRouteRun current = GetRun(anchor);
		TryProjectClientRun(in current, anchor, x, y, z, ref bestDistanceSquared, ref bestS, ref bestIndex, ref bestClampedLow, ref bestClampedHigh);
		if (anchor > 0)
		{
			ConvoyRouteRun previous = GetRun(anchor - 1);
			TryProjectClientRun(in previous, anchor - 1, x, y, z, ref bestDistanceSquared, ref bestS, ref bestIndex, ref bestClampedLow, ref bestClampedHigh);
		}
		if (anchor + 1 < RunCount)
		{
			ConvoyRouteRun next = GetRun(anchor + 1);
			TryProjectClientRun(in next, anchor + 1, x, y, z, ref bestDistanceSquared, ref bestS, ref bestIndex, ref bestClampedLow, ref bestClampedHigh);
		}

		if (bestIndex < 0 || bestDistanceSquared > acceptLocalDistanceSquared)
		{
			for (int iteration = anchor - 2; iteration >= 0; iteration--)
			{
				ConvoyRouteRun run = GetRun(iteration);
				if (run.S1 < referenceS - localSearchRadius) break;
				TryProjectClientRun(in run, iteration, x, y, z, ref bestDistanceSquared, ref bestS, ref bestIndex, ref bestClampedLow, ref bestClampedHigh);
			}

			for (int iteration = anchor + 2; iteration < RunCount; iteration++)
			{
				ConvoyRouteRun run = GetRun(iteration);
				if (run.S0 > referenceS + localSearchRadius) break;
				TryProjectClientRun(in run, iteration, x, y, z, ref bestDistanceSquared, ref bestS, ref bestIndex, ref bestClampedLow, ref bestClampedHigh);
			}
		}

		// Global nearest projection is recovery only. Once continuity exists, a plausible
		// local occurrence wins even if another lap or parallel edge is slightly closer.
		if (bestIndex < 0 || bestDistanceSquared > acceptLocalDistanceSquared)
		{
			for (int iteration = 0; iteration < RunCount; iteration++)
			{
				ConvoyRouteRun run = GetRun(iteration);
				if (run.S1 >= referenceS - localSearchRadius && run.S0 <= referenceS + localSearchRadius) continue;
				TryProjectClientRun(in run, iteration, x, y, z, ref bestDistanceSquared, ref bestS, ref bestIndex, ref bestClampedLow, ref bestClampedHigh);
			}
		}

		if (bestIndex < 0) return false;

		hint.RunIndex = bestIndex;
		hint.LastPathS = bestS;
		hint.Epoch = Epoch;
		hint.Revision = Revision;
		hint.HasPathS = true;
		projection = new ConvoyRouteProjection(true, bestS, bestDistanceSquared, bestClampedLow, bestClampedHigh);
		return true;
	}

	private void TryProjectClientRun(
		in ConvoyRouteRun run, int runIndex, double x, double y, double z,
		ref double bestDistanceSquared, ref double bestPathS, ref int bestIndex, ref bool bestClampedLow, ref bool bestClampedHigh
	)
	{
		if 
		(
			ReplicaGeometry == null ||
			!ReplicaGeometry.TryGetValue(run.EdgeHash, out RailEdgeGeometry? geometry) ||
			!RailGeometryUtility.TryProjectWorldPointToGeometry(geometry, x, y, z, out double edgeS, out _)
		) { return; }

		double minEdgeS = Math.Min(run.EdgeS0, run.EdgeS1);
		double maxEdgeS = Math.Max(run.EdgeS0, run.EdgeS1);
		double clampedEdgeS = GameMath.Clamp(edgeS, minEdgeS, maxEdgeS);
		if (!RailGeometryUtility.TryGetWorldPointAtGeometry(geometry, clampedEdgeS, out double pointX, out double pointY, out double pointZ)) return;

		double dx = x - pointX;
		double dy = y - pointY;
		double dz = z - pointZ;
		double distanceSquared = dx * dx + dy * dy + dz * dz;
		if (distanceSquared >= bestDistanceSquared) return;

		double denominator = run.EdgeS1 - run.EdgeS0;
		double alpha = Math.Abs(denominator) < Epsilon ? 0 : (clampedEdgeS - run.EdgeS0) / denominator;
		alpha = GameMath.Clamp(alpha, 0.0, 1.0);
		double pathS = run.S0 + (run.S1 - run.S0) * alpha;

		bool edgeRangeClamped = Math.Abs(clampedEdgeS - edgeS) > 1e-6;
		bool displacedFromBoundary = distanceSquared > 0.01;
		bestDistanceSquared = distanceSquared;
		bestPathS = pathS;
		bestIndex = runIndex;
		bestClampedLow = runIndex == 0 && pathS <= MinS + JoinTolerance && (edgeRangeClamped || displacedFromBoundary);
		bestClampedHigh = runIndex == RunCount - 1 && pathS >= MaxS - JoinTolerance && (edgeRangeClamped || displacedFromBoundary);
	}

	private int FindFirstRunEndingAfter(double pathS)
	{
		int low = 0;
		int high = RunCount;

		while (low < high)
		{
			int midpointIndex = low + ((high - low) >> 1);
			if (GetRun(midpointIndex).S1 <= pathS) { low = midpointIndex + 1; }
			else { high = midpointIndex; }
		}

		return low;
	}

	public bool CollectOccupiedEdgeHashes(RailGraphLive graph, byte gauge, double minS, double maxS, HashSet<ulong> destination)
	{
		if (destination == null || !double.IsFinite(minS) || !double.IsFinite(maxS) || !ValidateAuthoritative(graph, gauge)) { return false; }

		if (minS > maxS) (minS, maxS) = (maxS, minS);
		if (maxS - minS <= OccupancyOverlapEpsilon) return false;

		int start = FindFirstRunEndingAfter(minS + OccupancyOverlapEpsilon);
		bool any = false;

		for (int iteration = start; iteration < RunCount; iteration++)
		{
			ConvoyRouteRun run = GetRun(iteration);
			if (run.S0 >= maxS - OccupancyOverlapEpsilon) break;

			// Route joins are zero-area boundaries, touching an adjacent run does not occupy it.
			double overlapLow = Math.Max(minS, run.S0);
			double overlapHigh = Math.Min(maxS, run.S1);
			if (overlapHigh - overlapLow <= OccupancyOverlapEpsilon) continue;

			if (run.EdgeHash != 0) { destination.Add(run.EdgeHash); any = true; }
		}

		return any;
	}

	public bool TryClampTravelBeforeBlockedEdge(RailGraphServerSystem railSystem, double fromS, double requestedSignedDistance, out double allowedSignedDistance, out ulong blockedEdge)
	{
		allowedSignedDistance = requestedSignedDistance;
		blockedEdge = 0;

		if (railSystem == null || RunCount == 0 || Math.Abs(requestedSignedDistance) <= Epsilon) return false;

		double targetS = fromS + requestedSignedDistance;
		if (requestedSignedDistance > 0)
		{
			for (int iteration = 0; iteration < RunCount; iteration++)
			{
				ConvoyRouteRun run = GetRun(iteration);
				if (run.S1 < fromS - Epsilon) continue;
				if (run.S0 > targetS + Epsilon) break;
				if (run.EdgeHash == 0 || !railSystem.IsEdgeClearanceBlockedOrDirty(run.EdgeHash)) continue;

				blockedEdge = run.EdgeHash;
				allowedSignedDistance = run.S0 <= fromS + Epsilon ? 0 : Math.Max(0, run.S0 - fromS);
				return true;
			}
			return false;
		}

		for (int iteration = RunCount - 1; iteration >= 0; iteration--)
		{
			ConvoyRouteRun run = GetRun(iteration);
			if (run.S0 > fromS + Epsilon) continue;
			if (run.S1 < targetS - Epsilon) break;
			if (run.EdgeHash == 0 || !railSystem.IsEdgeClearanceBlockedOrDirty(run.EdgeHash)) continue;

			blockedEdge = run.EdgeHash;
			allowedSignedDistance = run.S1 >= fromS - Epsilon ? 0 : -Math.Max(0, fromS - run.S1);
			return true;
		}

		return false;
	}

	public bool EmitRepulsionSpans(RailTrainCollisionSystem collector, RailGraphLive graph, byte gauge, int bodyIndex, int carIndex, double minS, double maxS)
	{
		int runHint = -1;
		return EmitRepulsionSpans(collector, graph, gauge, bodyIndex, carIndex, minS, maxS, ref runHint);
	}

	/// Emits one car interval while advancing a reusable route cursor.
	/// Callers should process cars from tail to head so successive intervals move toward larger S.
	public bool EmitRepulsionSpans(RailTrainCollisionSystem collector, RailGraphLive graph, byte gauge, int bodyIndex, int carIndex, double minS, double maxS, ref int runHint)
	{
		return EmitRepulsionSpansCore(collector, graph, gauge, bodyIndex, carIndex, minS, maxS, ref runHint, requireWholeRouteValid: true);
	}

	internal bool EmitExistingRepulsionSpans(RailTrainCollisionSystem collector, RailGraphLive graph, byte gauge, int bodyIndex, int carIndex, double minS, double maxS, ref int runHint)
	{
		return EmitRepulsionSpansCore(collector, graph, gauge, bodyIndex, carIndex, minS, maxS, ref runHint, requireWholeRouteValid: false);
	}

	private bool EmitRepulsionSpansCore(RailTrainCollisionSystem collector, RailGraphLive graph, byte gauge, int bodyIndex, int carIndex, double minS, double maxS, ref int runHint, bool requireWholeRouteValid)
	{
		if (collector == null || graph == null || Gauge != gauge || RunCount == 0) return false;
		if (requireWholeRouteValid && !ValidateAuthoritative(graph, gauge)) return false;
		if (minS > maxS) (minS, maxS) = (maxS, minS);

		int start = FindRun(minS, ref runHint);
		if (start < 0)
		{
			start = Math.Max(0, Math.Min(RunCount - 1, runHint));
			while (start > 0 && GetRun(start).S0 > minS + JoinTolerance) start--;
			while (start < RunCount && GetRun(start).S1 < minS - JoinTolerance) start++;
		}
		if (start >= RunCount) return false;

		bool any = false;
		int lastVisited = start;
		for (int iteration = start; iteration < RunCount; iteration++)
		{
			ConvoyRouteRun run = GetRun(iteration);
			if (run.S0 > maxS + JoinTolerance) break;
			lastVisited = iteration;
			if (!graph.TryGetEdgeGauge(run.EdgeHash, out byte edgeGauge) || edgeGauge != gauge) continue;

			double a = Math.Max(minS, run.S0);
			double b = Math.Min(maxS, run.S1);
			if (b <= a + 1e-5) continue;

			double alpha0 = (a - run.S0) / Math.Max(Epsilon, run.S1 - run.S0);
			double alpha1 = (b - run.S0) / Math.Max(Epsilon, run.S1 - run.S0);
			double edgeS0 = run.EdgeS0 + (run.EdgeS1 - run.EdgeS0) * alpha0;
			double edgeS1 = run.EdgeS0 + (run.EdgeS1 - run.EdgeS0) * alpha1;

			collector.AddRepulsionSpan(graph, bodyIndex, carIndex, gauge, run.EdgeHash, edgeS0, edgeS1, run.CursorDirectionForPositiveS);
			any = true;
		}

		runHint = lastVisited;
		return any;
	}

	public void Serialize(BinaryWriter writer)
	{
		writer.Write(RouteDataStartSentinel);
		writer.Write(GraphVersion);
		writer.Write(Gauge);
		writer.Write(Epoch);
		writer.Write(Revision);
		writer.Write(RunCount);

		for (int iteration = 0; iteration < RunCount; iteration++)
		{
			ConvoyRouteRun run = GetRun(iteration);
			WriteRun(writer, in run);
		}

		writer.Write(RouteDataEndSentinel);
	}

	// Startup validation of persisted virtual routes. Geometry checks remain in ValidateAuthoritative.
	internal bool HasValidSavedRuns()
	{
		// EdgeS uses canonical endpoint order | Cursor direction uses stored polyline order and can have the opposite sign.
		for (int index = 0; index < RunCount; index++)
		{
			ConvoyRouteRun run = GetRun(index);
			if 
			(
				run.EdgeHash == 0 || !double.IsFinite(run.S0) || !double.IsFinite(run.S1) ||
				!double.IsFinite(run.EdgeS0) || !double.IsFinite(run.EdgeS1) || run.S1 <= run.S0 ||
				run.EdgeS0 == run.EdgeS1 || (index > 0 && Math.Abs(GetRun(index - 1).S1 - run.S0) > JoinTolerance)
			) { return false; }
		}
		return RunCount > 0;
	}

	public static ConvoyRoute Deserialize(BinaryReader reader)
	{
		if (reader.ReadInt32() != RouteDataStartSentinel) throw new InvalidDataException("Invalid convoy route start marker.");

		int graphVersion = reader.ReadInt32();
		byte gauge = reader.ReadByte();
		int epoch = reader.ReadInt32();
		uint revision = reader.ReadUInt32();
		int runCount = reader.ReadInt32();
		if (runCount <= 0 || runCount > 8192) throw new InvalidDataException($"Invalid convoy route run count {runCount}.");

		var route = new ConvoyRoute(epoch)
		{
			GraphVersion = graphVersion,
			Gauge = gauge,
			Revision = revision,
			AuthoritativeValidationRequired = true
		};

		route.EnsureCapacity(runCount);
		for (int iteration = 0; iteration < runCount; iteration++) route.AddLastRaw(ReadRun(reader));
		if (reader.ReadInt32() != RouteDataEndSentinel) throw new InvalidDataException("Invalid convoy route end marker.");
		route.RefreshBounds();
		return route;
	}

	public byte[] SerializeSnapshot(RailGraphLive graph) { return SerializeSnapshot(graph, null, null); }

	internal byte[] SerializeSnapshot(RailGraphLive graph, ISet<ulong>? knownGeometryEdges, ISet<ulong>? newlyKnownGeometryEdges = null)
	{
		if (graph == null) throw new ArgumentNullException(nameof(graph));
		bool useSharedCache = knownGeometryEdges != null;
		if (!useSharedCache && CachedSnapshot != null && CachedSnapshotEpoch == Epoch && CachedSnapshotGraphVersion == GraphVersion && CachedSnapshotRevision == Revision)
		{
			return CachedSnapshot;
		}

		using var stream = new MemoryStream(Math.Max(256, RunCount * 80));
		using var writer = new BinaryWriter(stream);
		Serialize(writer);
		WriteReplicaGeometry(writer, graph, 0, RunCount, journalChanges: null, knownGeometryEdges, newlyKnownGeometryEdges);
		byte[] result = stream.ToArray();
		if (!useSharedCache)
		{
			CachedSnapshot = result;
			CachedSnapshotEpoch = Epoch;
			CachedSnapshotGraphVersion = GraphVersion;
			CachedSnapshotRevision = Revision;
		}
		return result;
	}

	public static bool TryDeserializeSnapshot(byte[] data, out ConvoyRoute route) { return TryDeserializeSnapshot(data, null, out route); }

	internal static bool TryDeserializeSnapshot(byte[] data, IDictionary<ulong, RailEdgeGeometry>? sharedGeometry, out ConvoyRoute route)
	{
		route = null!;
		if (data == null || data.Length < 32) return false;

		try
		{
			using var stream = new MemoryStream(data, writable: false);
			using var reader = new BinaryReader(stream);
			route = Deserialize(reader);
			route.ReadReplicaGeometry(reader, sharedGeometry);
			route.AttachSharedReplicaGeometry(sharedGeometry);
			if (stream.Position != stream.Length) return false;
			return route.Count > 0 && route.HasCompleteReplicaGeometry();
		}
		catch { route = null!; return false; }
	}

	public bool TrySerializeDelta(uint baseRevision, RailGraphLive graph, out byte[] data)
	{
		return TrySerializeDelta(baseRevision, graph, null, out data, null);
	}

	internal bool TrySerializeDelta(uint baseRevision, RailGraphLive graph, ISet<ulong>? knownGeometryEdges, out byte[] data, ISet<ulong>? newlyKnownGeometryEdges = null)
	{
		data = Array.Empty<byte>();
		if (graph == null || baseRevision == Revision) return graph != null;
		bool useSharedCache = knownGeometryEdges != null;
		if (!useSharedCache && CachedDelta != null && CachedDeltaEpoch == Epoch && CachedDeltaGraphVersion == GraphVersion && CachedDeltaBaseRevision == baseRevision && CachedDeltaRevision == Revision)
		{
			data = CachedDelta;
			return true;
		}
		if (ChangeJournal.Count == 0) return false;

		int firstChange = -1;
		for (int iteration = 0; iteration < ChangeJournal.Count; iteration++)
		{
			if (ChangeJournal[iteration].BaseRevision == baseRevision) { firstChange = iteration; break; }
		}

		if (firstChange < 0) return false;

		uint expected = baseRevision;
		int changeCount = 0;
		for (int iteration = firstChange; iteration < ChangeJournal.Count; iteration++)
		{
			ConvoyRouteChange change = ChangeJournal[iteration];
			if (change.BaseRevision != expected) return false;
			expected = change.NewRevision;
			changeCount++;
			if (expected == Revision) break;
		}

		if (expected != Revision) return false;

		using var stream = new MemoryStream(Math.Max(128, changeCount * 80));
		using var writer = new BinaryWriter(stream);
		writer.Write(Epoch);
		writer.Write(GraphVersion);
		writer.Write(Gauge);
		writer.Write(baseRevision);
		writer.Write(Revision);
		writer.Write(changeCount);

		for (int iteration = firstChange; iteration < firstChange + changeCount; iteration++)
		{
			ConvoyRouteChange change = ChangeJournal[iteration];
			writer.Write((byte)change.Kind);
			writer.Write(change.BaseRevision);
			writer.Write(change.NewRevision);

			if (change.Kind == ConvoyRouteChangeKind.TrimLow || change.Kind == ConvoyRouteChangeKind.TrimHigh) { writer.Write(change.BoundaryS); }
			else { WriteRun(writer, in change.Run); }
		}

		WriteReplicaGeometry(writer, graph, firstChange, changeCount, ChangeJournal, knownGeometryEdges, newlyKnownGeometryEdges);
		data = stream.ToArray();
		if (!useSharedCache)
		{
			CachedDelta = data;
			CachedDeltaEpoch = Epoch;
			CachedDeltaGraphVersion = GraphVersion;
			CachedDeltaBaseRevision = baseRevision;
			CachedDeltaRevision = Revision;
		}
		return true;
	}

	/// Applies a server delta in place. A failed application may leave this disposable client replica partially modified,
	/// the network owner must discard it and request a snapshot. Avoiding a full route/geometry clone keeps ordinary edge deltas cheap.
	public bool TryApplyDelta(byte[] data) { return TryApplyDelta(data, null); }

	internal bool TryApplyDelta(byte[] data, IDictionary<ulong, RailEdgeGeometry>? sharedGeometry)
	{
		if (data == null || data.Length < 32) return false;

		try
		{
			using var stream = new MemoryStream(data, writable: false);
			using var reader = new BinaryReader(stream);

			int epoch = reader.ReadInt32();
			int graphVersion = reader.ReadInt32();
			byte gauge = reader.ReadByte();
			uint baseRevision = reader.ReadUInt32();
			uint targetRevision = reader.ReadUInt32();
			int changeCount = reader.ReadInt32();
			if (changeCount < 0 || changeCount > 4096) return false;
			if (epoch != Epoch || gauge != Gauge || baseRevision != Revision) return false;

			GraphVersion = graphVersion;
			uint expected = baseRevision;
			for (int iteration = 0; iteration < changeCount; iteration++)
			{
				ConvoyRouteChangeKind kind = (ConvoyRouteChangeKind)reader.ReadByte();
				uint changeBase = reader.ReadUInt32();
				uint changeRevision = reader.ReadUInt32();
				if (changeBase != expected || changeRevision != changeBase + 1) return false;

				if (kind == ConvoyRouteChangeKind.TrimLow) { TrimLowRaw(reader.ReadDouble()); }
				else if (kind == ConvoyRouteChangeKind.TrimHigh) { TrimHighRaw(reader.ReadDouble()); }
				else { ConvoyRouteRun run = ReadRun(reader); if (!ApplyRunChangeRaw(kind, in run)) return false; }

				if (RunCount == 0) return false;
				expected = changeRevision;
			}

			if (expected != targetRevision) return false;
			RefreshBounds();
			ReadReplicaGeometry(reader, sharedGeometry);
			AttachSharedReplicaGeometry(sharedGeometry);
			if (stream.Position != stream.Length) return false;
			PruneReplicaGeometry();
			if (!HasCompleteReplicaGeometry()) return false;

			Revision = targetRevision;
			ChangeJournal.Clear();
			ClearSerializationCache();
			return true;
		}
		catch { return false; }
	}

	private void WriteReplicaGeometry(
		BinaryWriter writer, RailGraphLive graph, int firstIndex, int itemCount,
		IReadOnlyList<ConvoyRouteChange>? journalChanges, ISet<ulong>? knownGeometryEdges, ISet<ulong>? newlyKnownGeometryEdges
	)
	{
		GeometryEdgeScratch.Clear();
		GeometryEdgeSeen.Clear();

		for (int iteration = 0; iteration < itemCount; iteration++)
		{
			ulong edgeHash;
			if (journalChanges == null) { edgeHash = GetRun(firstIndex + iteration).EdgeHash; }
			else
			{
				ConvoyRouteChange change = journalChanges[firstIndex + iteration];
				if
				(
					change.Kind == ConvoyRouteChangeKind.TrimLow	|| change.Kind == ConvoyRouteChangeKind.TrimHigh ||
					change.Kind == ConvoyRouteChangeKind.ReplaceLow	|| change.Kind == ConvoyRouteChangeKind.ReplaceHigh
				) continue;
				edgeHash = change.Run.EdgeHash;
			}

			if 
			(
				edgeHash != 0 && (knownGeometryEdges == null || !knownGeometryEdges.Contains(edgeHash)) &&
				(newlyKnownGeometryEdges == null || !newlyKnownGeometryEdges.Contains(edgeHash)) && GeometryEdgeSeen.Add(edgeHash)
			) { GeometryEdgeScratch.Add(edgeHash); }
		}

		writer.Write(GeometryEdgeScratch.Count);
		for (int iteration = 0; iteration < GeometryEdgeScratch.Count; iteration++)
		{
			ulong edgeHash = GeometryEdgeScratch[iteration];
			if (!RailEdgeGeometryCache.TryGet(graph, Gauge, edgeHash, out RailEdgeGeometry geometry))
			{
				throw new InvalidDataException($"Missing geometry for route edge {edgeHash}.");
			}

			writer.Write(edgeHash);
			writer.Write(geometry.StoredOrderIsCanonical);
			writer.Write(geometry.PointCount);
			for (int pointCoordinateIndex = 0; pointCoordinateIndex < geometry.PointCoordinates16.Length; pointCoordinateIndex++) writer.Write(geometry.PointCoordinates16[pointCoordinateIndex]);
			if (newlyKnownGeometryEdges != null) newlyKnownGeometryEdges.Add(edgeHash);
			else knownGeometryEdges?.Add(edgeHash);
		}
		GeometryEdgeScratch.Clear();
		GeometryEdgeSeen.Clear();
	}

	private void ReadReplicaGeometry(BinaryReader reader, IDictionary<ulong, RailEdgeGeometry>? sharedGeometry)
	{
		int geometryCount = reader.ReadInt32();
		if (geometryCount < 0 || geometryCount > 8192) throw new InvalidDataException($"Invalid route geometry count {geometryCount}.");

		ReplicaGeometry ??= new Dictionary<ulong, RailEdgeGeometry>(geometryCount);
		int totalPoints = 0;

		for (int iteration = 0; iteration < geometryCount; iteration++)
		{
			ulong edgeHash = reader.ReadUInt64();
			bool storedOrderIsCanonical = reader.ReadBoolean();
			int pointCount = reader.ReadInt32();
			if (edgeHash == 0 || pointCount < 2 || pointCount > 65536) throw new InvalidDataException("Invalid route edge geometry.");

			totalPoints += pointCount;
			if (totalPoints > 1_000_000) throw new InvalidDataException("Route geometry payload is too large.");

			var points = new int[pointCount * 3];
			for (int pointCoordinateIndex = 0; pointCoordinateIndex < points.Length; pointCoordinateIndex++) points[pointCoordinateIndex] = reader.ReadInt32();
			if (!RailEdgeGeometry.TryCreate(points, storedOrderIsCanonical, out RailEdgeGeometry geometry)) { throw new InvalidDataException("Degenerate route edge geometry."); }
			ReplicaGeometry[edgeHash] = geometry;
			if (sharedGeometry != null) sharedGeometry[edgeHash] = geometry;
		}
	}

	private void AttachSharedReplicaGeometry(IDictionary<ulong, RailEdgeGeometry>? sharedGeometry)
	{
		if (sharedGeometry == null || sharedGeometry.Count == 0) return;
		ReplicaGeometry ??= new Dictionary<ulong, RailEdgeGeometry>();
		for (int iteration = 0; iteration < RunCount; iteration++)
		{
			ulong edgeHash = GetRun(iteration).EdgeHash;
			if (!ReplicaGeometry.ContainsKey(edgeHash) && sharedGeometry.TryGetValue(edgeHash, out RailEdgeGeometry geometry)) { ReplicaGeometry[edgeHash] = geometry; }
		}
	}

	private bool HasCompleteReplicaGeometry()
	{
		if (ReplicaGeometry == null) return false;
		for (int iteration = 0; iteration < RunCount; iteration++) { if (!ReplicaGeometry.ContainsKey(GetRun(iteration).EdgeHash)) return false; }
		return true;
	}

	private void PruneReplicaGeometry()
	{
		if (ReplicaGeometry == null || ReplicaGeometry.Count <= RunCount * 2 + 16) return;
		var used = new HashSet<ulong>();
		for (int iteration = 0; iteration < RunCount; iteration++) used.Add(GetRun(iteration).EdgeHash);

		var remove = new List<ulong>();
		foreach (ulong edgeHash in ReplicaGeometry.Keys) { if (!used.Contains(edgeHash)) remove.Add(edgeHash); }
		for (int iteration = 0; iteration < remove.Count; iteration++) ReplicaGeometry.Remove(remove[iteration]);
	}

	private bool ApplyRunChangeRaw(ConvoyRouteChangeKind kind, in ConvoyRouteRun run)
	{
		switch (kind)
		{
			case ConvoyRouteChangeKind.Append:
				if (RunCount > 0 && Math.Abs(GetRun(RunCount - 1).S1 - run.S0) > JoinTolerance) return false;
				AddLastRaw(run);
			return true;

			case ConvoyRouteChangeKind.Prepend:
				if (RunCount > 0 && Math.Abs(run.S1 - GetRun(0).S0) > JoinTolerance) return false;
				AddFirstRaw(run);
			return true;

			case ConvoyRouteChangeKind.ReplaceHigh:
				if (RunCount <= 0) return false;
				SetRun(RunCount - 1, run);
			return true;

			case ConvoyRouteChangeKind.ReplaceLow:
				if (RunCount <= 0) return false;
				SetRun(0, run);
			return true;

			default: return false;
		}
	}

	private static void WriteRun(BinaryWriter writer, in ConvoyRouteRun run)
	{
		writer.Write(run.EdgeHash);
		writer.Write(run.S0);
		writer.Write(run.S1);
		writer.Write(run.EdgeS0);
		writer.Write(run.EdgeS1);
		writer.Write((sbyte)run.CursorDirectionForPositiveS);
	}

	private static ConvoyRouteRun ReadRun(BinaryReader reader)
	{
		return new ConvoyRouteRun
		(
			reader.ReadUInt64(),
			reader.ReadDouble(),
			reader.ReadDouble(),
			reader.ReadDouble(),
			reader.ReadDouble(),
			reader.ReadSByte()
		);
	}

	/// Fixed-capacity chronological journal.
	/// Appending at capacity overwrites the oldest revision on the spot, avoiding the front-shift performed by List.RemoveRange().
	private sealed class ConvoyRouteChangeJournal : IReadOnlyList<ConvoyRouteChange>
	{
		private readonly ConvoyRouteChange[] Entries;
		private int FirstEntryIndex;
		private int EntryCount;

		public ConvoyRouteChangeJournal(int capacity) { Entries = new ConvoyRouteChange[Math.Max(1, capacity)]; }

		public int Count => EntryCount;

		public ConvoyRouteChange this[int index]
		{
			get
			{
				if ((uint)index >= (uint)EntryCount) throw new ArgumentOutOfRangeException(nameof(index));
				return Entries[(FirstEntryIndex + index) % Entries.Length];
			}
			set
			{
				if ((uint)index >= (uint)EntryCount) throw new ArgumentOutOfRangeException(nameof(index));
				Entries[(FirstEntryIndex + index) % Entries.Length] = value;
			}
		}

		public void Add(in ConvoyRouteChange change)
		{
			if (EntryCount < Entries.Length)
			{
				Entries[(FirstEntryIndex + EntryCount) % Entries.Length] = change;
				EntryCount++;
				return;
			}

			Entries[FirstEntryIndex] = change;
			FirstEntryIndex = (FirstEntryIndex + 1) % Entries.Length;
		}

		public void Clear()
		{
			if (EntryCount > 0) Array.Clear(Entries, 0, Entries.Length);
			FirstEntryIndex = 0;
			EntryCount = 0;
		}

		public IEnumerator<ConvoyRouteChange> GetEnumerator() { for (int iteration = 0; iteration < EntryCount; iteration++) yield return this[iteration]; }

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}
}

/// Records graph traversal into a convoy route. For ordinary 1:1 movement, the remainder of the already-selected edge is recorded immediately.
/// This gives clients deterministic forward coverage without speculative junction choices.
internal sealed class ConvoyRouteRecorder
{
	private ConvoyRoute? Route;
	private int DirectionSign;
	private double PathDistanceAtTravelStart;
	private double TravelStart;
	private double PathUnitsPerTravel = 1.0;
	private byte Gauge;
	private bool RecordSelectedEdgeRemainder;

	public void Begin(ConvoyRoute targetRoute, byte trackGauge, int sign, double sAtTravel0, double travel0, double pathUnitsPerTravel = 1.0, bool recordSelectedEdgeRemainder = true)
	{
		Route = targetRoute;
		Gauge = trackGauge;
		this.DirectionSign = sign >= 0 ? 1 : -1;
		this.PathDistanceAtTravelStart = sAtTravel0;
		this.TravelStart = travel0;
		this.PathUnitsPerTravel = Math.Max(1e-9, Math.Abs(pathUnitsPerTravel));
		this.RecordSelectedEdgeRemainder = recordSelectedEdgeRemainder && Math.Abs(this.PathUnitsPerTravel - 1.0) <= 1e-7;
	}

	public void Clear()
	{
		Route = null;
		DirectionSign = 1;
		PathDistanceAtTravelStart = 0;
		TravelStart = 0;
		PathUnitsPerTravel = 1.0;
		Gauge = 0;
		RecordSelectedEdgeRemainder = false;
	}

	public void RecordSpan
	(
		RailGraphLive graph,
		in RailwayVehicleShared.RailCursor cursor,
		ulong edgeHash,
		int startSegmentIndex,
		double startSegmentInterpolation,
		int endSegmentIndex,
		double endSegmentInterpolation,
		double spanTravel0,
		double spanTravel1
	)
	{
		if (Route == null || graph == null || edgeHash == 0) return;
		if (cursor.PolyXYZ16 == null || cursor.PointCount < 2) return;
		if (spanTravel1 <= spanTravel0 + 1e-9) return;

		if (!RailGeometryUtility.TryCanonicalSFromCursor(graph, edgeHash, Gauge, cursor.PolyXYZ16, cursor.PointCount, startSegmentIndex, startSegmentInterpolation, out double edgeS0, out double edgeLength)) return;
		if (!RailGeometryUtility.TryCanonicalSFromCursor(graph, edgeHash, Gauge, cursor.PolyXYZ16, cursor.PointCount, endSegmentIndex, endSegmentInterpolation, out double edgeS1, out _)) return;

		double path0 = PathDistanceAtTravelStart + DirectionSign * (spanTravel0 - TravelStart) * PathUnitsPerTravel;
		double path1 = PathDistanceAtTravelStart + DirectionSign * (spanTravel1 - TravelStart) * PathUnitsPerTravel;
		int positivePathDirection = cursor.Direction >= 0 ? 1 : -1;

		if (RecordSelectedEdgeRemainder)
		{
			double edgeDirection = Math.Sign(edgeS1 - edgeS0);
			if (edgeDirection != 0)
			{
				double endpointEdgeS = edgeDirection > 0 ? edgeLength : 0;
				double remaining = Math.Abs(endpointEdgeS - edgeS1);
				if (remaining > 1e-7)
				{
					path1 += DirectionSign * remaining;
					edgeS1 = endpointEdgeS;
				}
			}
		}

		Route.AddSpan
		(
			graph.BuildVersion,
			Gauge,
			edgeHash,
			path0,
			path1,
			edgeS0,
			edgeS1,
			positivePathDirection,
			RecordSelectedEdgeRemainder ? DirectionSign : 0
		);
	}
}

internal sealed class RailEdgeGeometry
{
	public readonly int[] PointCoordinates16;
	public readonly double[] CumulativeLengths;
	public readonly double TotalLength;
	public readonly bool StoredOrderIsCanonical;

	public int PointCount => PointCoordinates16.Length / 3;

	private RailEdgeGeometry(int[] pointCoordinates16, double[] cumulativeLengths, double totalLength, bool storedOrderIsCanonical)
	{
		PointCoordinates16 = pointCoordinates16;
		CumulativeLengths = cumulativeLengths;
		TotalLength = totalLength;
		StoredOrderIsCanonical = storedOrderIsCanonical;
	}

	public static bool TryCreate(int[] pointCoordinates16, bool storedOrderIsCanonical, out RailEdgeGeometry geometry)
	{
		geometry = null!;
		if (pointCoordinates16 == null || pointCoordinates16.Length < 6 || pointCoordinates16.Length % 3 != 0) return false;

		int pointCount = pointCoordinates16.Length / 3;
		var cumulative = new double[pointCount];
		double total = 0;
		for (int iteration = 0; iteration < pointCount - 1; iteration++)
		{
			int a = iteration * 3;
			int b = a + 3;
			double dx = (pointCoordinates16[b] - pointCoordinates16[a]) * (1.0 / 16.0);
			double dy = (pointCoordinates16[b + 1] - pointCoordinates16[a + 1]) * (1.0 / 16.0);
			double dz = (pointCoordinates16[b + 2] - pointCoordinates16[a + 2]) * (1.0 / 16.0);
			total += Math.Sqrt(dx * dx + dy * dy + dz * dz);
			cumulative[iteration + 1] = total;
		}

		if (total <= 1e-9) return false;
		geometry = new RailEdgeGeometry(pointCoordinates16, cumulative, total, storedOrderIsCanonical);
		return true;
	}
}

internal static class RailEdgeGeometryCache
{
	// Two-way bounded cache, cheaper and less compute heavy than using a data map
	private const int SetCount = 1 << 13;
	private const int SetMask = SetCount - 1;
	private const int Ways = 2;

	private struct CacheSlot
	{
		public ulong EdgeHash;
		public byte Gauge;
		public uint LastUse;
		public RailEdgeGeometry? Geometry;
	}

	private sealed class PerGraph
	{
		public readonly CacheSlot[] Slots = new CacheSlot[SetCount * Ways];
		public uint Clock;
	}

	private static readonly ConditionalWeakTable<RailGraphLive, PerGraph> Caches = new();

	public static bool TryGet(RailGraphLive graph, byte gauge, ulong edgeHash, out RailEdgeGeometry geometry)
	{
		geometry = null!;
		if (graph == null || edgeHash == 0) return false;
		if (!graph.TryGetPolyline16(edgeHash, out int[] points) || points == null || points.Length < 6) return false;

		PerGraph cache = Caches.GetValue(graph, static _ => new PerGraph());
		uint use = ++cache.Clock;
		int setStart = (unchecked((int)(edgeHash ^ (edgeHash >> 32))) & SetMask) * Ways;
		int replacementIndex = setStart;
		uint oldestUse = uint.MaxValue;

		for (int way = 0; way < Ways; way++)
		{
			int index = setStart + way;
			ref CacheSlot slot = ref cache.Slots[index];
			RailEdgeGeometry? cached = slot.Geometry;
			if (slot.EdgeHash == edgeHash && slot.Gauge == gauge && cached != null && ReferenceEquals(cached.PointCoordinates16, points))
			{
				slot.LastUse = use;
				geometry = cached;
				return true;
			}

			if (cached == null)
			{
				replacementIndex = index;
				oldestUse = 0;
				break;
			}
			if (slot.LastUse < oldestUse)
			{
				oldestUse = slot.LastUse;
				replacementIndex = index;
			}
		}

		if (!graph.TryGetEdgeEndpoints(edgeHash, out var endpointA, out var endpointB, out byte edgeGauge) || edgeGauge != gauge) return false;
		if (!RailEdgeGeometry.TryCreate(points, endpointA.CompareTo(endpointB) <= 0, out geometry)) return false;

		ref CacheSlot replacement = ref cache.Slots[replacementIndex];
		replacement.EdgeHash = edgeHash;
		replacement.Gauge = gauge;
		replacement.LastUse = use;
		replacement.Geometry = geometry;
		return true;
	}

	public static void Invalidate(RailGraphLive graph, RailGraphChangeSet change)
	{
		if (graph == null || change == null) return;
		if (change.GlobalInvalidation) { Caches.Remove(graph); return; }
		if (!Caches.TryGetValue(graph, out PerGraph? cache)) return;

		for (int iteration = 0; iteration < change.TouchedEdges.Count; iteration++)
		{
			ulong edgeHash = change.TouchedEdges[iteration];
			int setStart = (unchecked((int)(edgeHash ^ (edgeHash >> 32))) & SetMask) * Ways;
			for (int way = 0; way < Ways; way++)
			{
				int index = setStart + way;
				if (cache.Slots[index].EdgeHash == edgeHash) cache.Slots[index] = default;
			}
		}
	}
}

internal static class RailGeometryUtility
{
	private const double Epsilon = 1e-9;

	public static double PolylineLength(int[] pointCoordinates16, int pointCount)
	{
		if (pointCoordinates16 == null || pointCount < 2) return 0;
		double total = 0;
		for (int iteration = 0; iteration < pointCount - 1; iteration++)
		{
			SegmentDelta(pointCoordinates16, iteration, out double dx, out double dy, out double dz);
			total += Math.Sqrt(dx * dx + dy * dy + dz * dz);
		}
		return total;
	}

	public static bool TryCanonicalSFromCursor
	(
		RailGraphLive graph, ulong edgeHash, byte gauge, int[] pointCoordinates16,
		int pointCount, int segmentIndex, double segmentInterpolation, out double canonicalS, out double edgeLength
	)
	{
		canonicalS = 0;
		edgeLength = 0;
		if (!RailEdgeGeometryCache.TryGet(graph, gauge, edgeHash, out RailEdgeGeometry geometry)) return false;
		if (!ReferenceEquals(geometry.PointCoordinates16, pointCoordinates16) || geometry.PointCount != pointCount) return false;

		segmentIndex = GameMath.Clamp(segmentIndex, 0, Math.Max(0, pointCount - 2));
		segmentInterpolation = GameMath.Clamp(segmentInterpolation, 0, 1);

		double segmentStart = geometry.CumulativeLengths[segmentIndex];
		double segmentLength = geometry.CumulativeLengths[segmentIndex + 1] - segmentStart;
		double storedS = segmentStart + segmentLength * segmentInterpolation;

		edgeLength = geometry.TotalLength;
		canonicalS = geometry.StoredOrderIsCanonical ? storedS : edgeLength - storedS;
		canonicalS = GameMath.Clamp(canonicalS, 0, edgeLength);
		return true;
	}

	public static bool TryCursorFromCanonicalS(RailGraphLive graph, byte gauge, ulong edgeHash, double canonicalS, int cursorDirectionForPositiveS, out RailwayVehicleShared.RailCursor cursor)
	{
		cursor = default;
		if (!RailEdgeGeometryCache.TryGet(graph, gauge, edgeHash, out RailEdgeGeometry geometry)) return false;
		if (!TryCursorFromGeometry(gauge, edgeHash, geometry, canonicalS, cursorDirectionForPositiveS, graph.BuildVersion, out cursor)) return false;

		RailwayVehicleShared.RefreshSegmentLimits(graph, ref cursor);
		return true;
	}

	public static bool TryCursorFromGeometry(byte gauge, ulong edgeHash, RailEdgeGeometry geometry, double canonicalS, int cursorDirectionForPositiveS, int graphVersion, out RailwayVehicleShared.RailCursor cursor)
	{
		cursor = default;
		if (geometry == null || geometry.PointCount < 2) return false;

		canonicalS = GameMath.Clamp(canonicalS, 0, geometry.TotalLength);
		double storedS = geometry.StoredOrderIsCanonical ? canonicalS : geometry.TotalLength - canonicalS;
		if (!TryResolveStoredS(geometry, storedS, out int segmentIndex, out double segmentT)) return false;

		cursor = new RailwayVehicleShared.RailCursor
		{
			Gauge = gauge,
			SegmentHash = edgeHash,
			SegmentIndex = segmentIndex,
			NormalizedSegmentProgress = segmentT,
			Direction = cursorDirectionForPositiveS >= 0 ? 1 : -1,
			BoundGraphVersion = graphVersion,
			PolyXYZ16 = geometry.PointCoordinates16,
			PointCount = geometry.PointCount,
			SegmentMaxSpeedFactor = 1f,
			OrientationCacheSegmentHash = 0,
			OrientationCacheSegmentIndex = -1,
			OrientationCacheDirection = 0
		};
		return true;
	}

	public static bool TryReadWorldPoseAtCanonicalS
	(
		RailGraphLive graph, byte gauge, ulong edgeHash, double canonicalS, int cursorDirectionForPositiveS,
		out double x, out double y, out double z, out float yaw, out float roll
	)
	{
		x = y = z = 0;
		yaw = roll = 0;
		if (!TryCursorFromCanonicalS(graph, gauge, edgeHash, canonicalS, cursorDirectionForPositiveS, out RailwayVehicleShared.RailCursor cursor)) return false;
		return RailwayVehicleShared.TryReadWorldPoseFromTrack(ref cursor, out x, out y, out z, out yaw, out roll);
	}

	public static bool TryGetWorldPointAtGeometry( RailEdgeGeometry geometry, double canonicalS, out double x, out double y, out double z)
	{
		x = y = z = 0;
		if (!TryCursorFromGeometry(0, 0, geometry, canonicalS, 1, 0, out RailwayVehicleShared.RailCursor cursor)) return false;

		int a = GameMath.Clamp(cursor.SegmentIndex, 0, cursor.PointCount - 2);
		int b = a + 1;
		RailwayVehicleShared.GetPoint(cursor.PolyXYZ16, a, out double ax, out double ay, out double az);
		RailwayVehicleShared.GetPoint(cursor.PolyXYZ16, b, out double bx, out double by, out double bz);
		double t = GameMath.Clamp(cursor.NormalizedSegmentProgress, 0, 1);
		x = ax + (bx - ax) * t;
		y = ay + (by - ay) * t;
		z = az + (bz - az) * t;
		return true;
	}

	public static bool TryGetWorldPointAtCanonicalS(RailGraphLive graph, byte gauge, ulong edgeHash, double canonicalS, out double x, out double y, out double z)
	{
		x = y = z = 0;
		if (!TryCursorFromCanonicalS(graph, gauge, edgeHash, canonicalS, 1, out RailwayVehicleShared.RailCursor cursor)) return false;

		int a = GameMath.Clamp(cursor.SegmentIndex, 0, cursor.PointCount - 2);
		int b = a + 1;
		RailwayVehicleShared.GetPoint(cursor.PolyXYZ16, a, out double ax, out double ay, out double az);
		RailwayVehicleShared.GetPoint(cursor.PolyXYZ16, b, out double bx, out double by, out double bz);
		double t = GameMath.Clamp(cursor.NormalizedSegmentProgress, 0, 1);
		x = ax + (bx - ax) * t;
		y = ay + (by - ay) * t;
		z = az + (bz - az) * t;
		return true;
	}

	public static bool TryProjectWorldPointToEdge(RailGraphLive graph, byte gauge, ulong edgeHash, double x, double y, double z, out double canonicalS, out double distanceSquared)
	{
		canonicalS = 0;
		distanceSquared = double.MaxValue;
		if (!RailEdgeGeometryCache.TryGet(graph, gauge, edgeHash, out RailEdgeGeometry geometry)) return false;
		return TryProjectWorldPointToGeometry(geometry, x, y, z, out canonicalS, out distanceSquared);
	}

	public static bool TryProjectWorldPointToGeometry(RailEdgeGeometry geometry, double x, double y, double z, out double canonicalS, out double distanceSquared)
	{
		canonicalS = 0;
		distanceSquared = double.MaxValue;
		if (geometry == null || geometry.PointCount < 2) return false;

		double bestStoredS = 0;
		bool any = false;
		int[] points = geometry.PointCoordinates16;

		for (int iteration = 0; iteration < geometry.PointCount - 1; iteration++)
		{
			int a = iteration * 3;
			int b = (iteration + 1) * 3;

			double ax = points[a] * (1.0 / 16.0);
			double ay = points[a + 1] * (1.0 / 16.0);
			double az = points[a + 2] * (1.0 / 16.0);
			double bx = points[b] * (1.0 / 16.0);
			double by = points[b + 1] * (1.0 / 16.0);
			double bz = points[b + 2] * (1.0 / 16.0);

			double sx = bx - ax;
			double sy = by - ay;
			double sz = bz - az;
			double lengthSquared = sx * sx + sy * sy + sz * sz;
			if (lengthSquared <= Epsilon * Epsilon) continue;

			double t = ((x - ax) * sx + (y - ay) * sy + (z - az) * sz) / lengthSquared;
			t = GameMath.Clamp(t, 0, 1);

			double px = ax + sx * t;
			double py = ay + sy * t;
			double pz = az + sz * t;
			double dx = x - px;
			double dy = y - py;
			double dz = z - pz;
			double candidateDistanceSquared = dx * dx + dy * dy + dz * dz;

			if (candidateDistanceSquared < distanceSquared)
			{
				double segmentLength = geometry.CumulativeLengths[iteration + 1] - geometry.CumulativeLengths[iteration];
				distanceSquared = candidateDistanceSquared;
				bestStoredS = geometry.CumulativeLengths[iteration] + segmentLength * t;
				any = true;
			}
		}

		if (!any) return false;
		canonicalS = geometry.StoredOrderIsCanonical ? bestStoredS : geometry.TotalLength - bestStoredS;
		canonicalS = GameMath.Clamp(canonicalS, 0, geometry.TotalLength);
		return true;
	}

	private static bool TryResolveStoredS(RailEdgeGeometry geometry, double storedS, out int segmentIndex, out double segmentT)
	{
		segmentIndex = 0;
		segmentT = 0;
		storedS = GameMath.Clamp(storedS, 0, geometry.TotalLength);

		double[] cumulative = geometry.CumulativeLengths;
		int low = 0;
		int high = cumulative.Length - 2;
		while (low <= high)
		{
			int midpointIndex = (low + high) >> 1;
			double start = cumulative[midpointIndex];
			double end = cumulative[midpointIndex + 1];

			if (storedS < start - Epsilon) high = midpointIndex - 1;
			else if (storedS > end + Epsilon) low = midpointIndex + 1;
			else
			{
				double length = end - start;
				if (length <= Epsilon) { low = midpointIndex + 1; continue; }

				segmentIndex = midpointIndex;
				segmentT = GameMath.Clamp((storedS - start) / length, 0, 1);
				return true;
			}
		}

		for (int iteration = 0; iteration < cumulative.Length - 1; iteration++)
		{
			double length = cumulative[iteration + 1] - cumulative[iteration];
			if (length <= Epsilon) continue;
			segmentIndex = iteration;
			segmentT = storedS <= cumulative[iteration] ? 0 : 1;
			return true;
		}

		return false;
	}

	private static void SegmentDelta(int[] pointCoordinates16, int segmentIndex, out double dx, out double dy, out double dz)
	{
		int a = segmentIndex * 3;
		int b = (segmentIndex + 1) * 3;
		dx = (pointCoordinates16[b] - pointCoordinates16[a]) * (1.0 / 16.0);
		dy = (pointCoordinates16[b + 1] - pointCoordinates16[a + 1]) * (1.0 / 16.0);
		dz = (pointCoordinates16[b + 2] - pointCoordinates16[a + 2]) * (1.0 / 16.0);
	}
}
