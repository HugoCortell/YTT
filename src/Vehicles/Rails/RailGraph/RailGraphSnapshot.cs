using System.Collections.Generic;
using Vintagestory.API.MathTools;

namespace YangTransport;

public sealed class RailNode
{
	public int ID;
	public byte Gauge;
	public BlockPos Position = null!;
	public Vec3d WorldPosition = null!;
	public bool IsSignal;
	public byte SignalKind; // 0 = none, 1 = block signal, 2 = one-way, 3 = chain
	public ulong OneWayAllowedFromEdgeHash; // For one-way/chain signals, allowed-from edge hash (0 if unknown)
	public readonly List<int> SegmentIDs = new();
}

public sealed class RailGraphSnapshot
{
	public static readonly RailGraphSnapshot Empty = new()
	{
		Center = new BlockPos(0, 0, 0),
		Nodes = new List<RailNode>(0),
		Segments = new List<RailSegment>(0),
		BuildVersion = 0
	};

	public BlockPos Center = null!;
	public List<RailNode> Nodes = null!;
	public List<RailSegment> Segments = null!;
	public int BuildVersion;
}

/// Representation of a path between two nodes.
public sealed class RailSegment
{
	public int ID { get; internal set; }

	public byte Gauge { get; internal set; } // 0 == Tunnel Rails (Minecarts) | 1 == Standard Gauge (Big Locomotives)

	public int NodeA { get; internal set; }
	public int NodeB { get; internal set; }

	// Occupancy zone ID for this segment (derived from the live graph, signals define zone boundaries). zero if unknown.
	public ulong OccupancyZoneID { get; internal set; }

	// True when any atomic edge in this rendered segment is clearance blocked/dirty
	public bool ClearanceBlocked { get; internal set; }

	// Atomic edge hashes composing this rendered segment. Used by overlays that need edge-level state.
	public List<ulong> EdgeHashes { get; } = new();

	// World-space polyline points, in traversal order from A to B.
	public List<Vec3d> Polyline { get; } = new();

	// Source rail block positions used to build this segment (debug/diagnostics). Does not include the node positions (unless the node itself is a rail block).
	public List<BlockPos> RailBlocks { get; } = new();
}
