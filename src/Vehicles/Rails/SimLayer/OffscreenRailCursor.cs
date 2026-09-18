using System;
using Vintagestory.API.MathTools;

namespace YangTransport;

/// Rail-cursor helpers for offscreen travel. The branch/edge stepping delegates to RailwayVehicleShared so loaded and unloaded rail choice stay in sync.
internal static class OffscreenRailCursor
{
	private const double MaxStepBlocks = 256.0;

	internal enum AdvanceResult
	{
		NoMove,
		MovedAll,
		MovedPartial
	}

	internal struct Cursor
	{
		public ulong SegmentHash;
		public int SegmentIndex;
		public double NormalizedSegmentProgress;
		public int Direction;
	}

	public static bool TryGetWorldPose(RailGraphLive graph, in Cursor cursor, Vec3d position, out float yaw, out float roll)
	{
		yaw = 0f;
		roll = 0f;

		if (position == null) return false;
		position.Set(0, 0, 0);

		if (cursor.SegmentHash == 0) return false;
		if (!graph.TryGetPolyline16(cursor.SegmentHash, out var quantizedCoordinates)) return false;

		int pointCount = quantizedCoordinates.Length / 3;
		if (pointCount < 2) return false;

		int firstPointIndex = GameMath.Clamp(cursor.SegmentIndex, 0, pointCount - 2);
		int secondPointIndex = firstPointIndex + 1;

		RailwayVehicleShared.GetPoint(quantizedCoordinates, firstPointIndex, out double ax, out double ay, out double az);
		RailwayVehicleShared.GetPoint(quantizedCoordinates, secondPointIndex, out double bx, out double by, out double bz);

		double px = ax + (bx - ax) * GameMath.Clamp(cursor.NormalizedSegmentProgress, 0.0, 1.0);
		double py = ay + (by - ay) * GameMath.Clamp(cursor.NormalizedSegmentProgress, 0.0, 1.0);
		double pz = az + (bz - az) * GameMath.Clamp(cursor.NormalizedSegmentProgress, 0.0, 1.0);

		position.Set(px, py, pz);

		double forwardDeltaX = cursor.Direction >= 0 ? (bx - ax) : (ax - bx);
		double forwardDeltaY = cursor.Direction >= 0 ? (by - ay) : (ay - by);
		double forwardDeltaZ = cursor.Direction >= 0 ? (bz - az) : (az - bz);

		yaw = (float)Math.Atan2(forwardDeltaX, forwardDeltaZ);

		double horizontalLength = Math.Sqrt(forwardDeltaX * forwardDeltaX + forwardDeltaZ * forwardDeltaZ);
		roll = horizontalLength < 1e-8 ? 0f : (float)(-Math.Atan2(forwardDeltaY, horizontalLength));
		return true;
	}

	public static bool TryGetWorldPose(RailGraphLive graph, in Cursor cursor, out Vec3d position, out float yaw, out float roll)
	{
		position = new Vec3d();
		return TryGetWorldPose(graph, cursor, position, out yaw, out roll);
	}

	public static AdvanceResult AdvancePartial(RailGraphLive graph, byte gauge, ref Cursor cursor, double distance, int wantTurn, out double moved)
	{
		double absoluteDistanceTravelled = 0;
		return AdvancePartial(graph, gauge, ref cursor, distance, wantTurn, null, null, 0, false, ref absoluteDistanceTravelled, out moved);
	}

	public static AdvanceResult AdvancePartial
	(
		RailGraphLive graph, byte gauge, ref Cursor cursor, double distance, int wantTurn,
		RailExactTurnPlan? exactTurns, RailGraphServerSystem? railSystem, long occupancyOwnerID, bool automatedMovement,
		ref double absoluteDistanceTravelled, out double moved, ConvoyRouteRecorder? tapeRecorder = null)
	{
		moved = 0;

		if (Math.Abs(distance) <= 1e-8) return AdvanceResult.NoMove;
		if (cursor.SegmentHash == 0) return AdvanceResult.NoMove;

		var railCursor = new RailwayVehicleShared.RailCursor
		{
			Gauge = gauge,
			SegmentHash = cursor.SegmentHash,
			SegmentIndex = cursor.SegmentIndex,
			NormalizedSegmentProgress = cursor.NormalizedSegmentProgress,
			Direction = cursor.Direction >= 0 ? 1 : -1
		};

		if (!RailwayVehicleShared.TryRefreshPolyline(graph, ref railCursor)) return AdvanceResult.NoMove;

		double requestedAbsoluteDistance = Math.Abs(distance);
		double remainingAbsoluteDistance = requestedAbsoluteDistance;
		double absoluteDistanceMoved = 0;
		double movementSign = distance >= 0 ? 1.0 : -1.0;

		int guardIteration = 0;
		while (remainingAbsoluteDistance > 1e-8 && guardIteration++ < 512)
		{
			double absoluteStepDistance = Math.Min(remainingAbsoluteDistance, MaxStepBlocks);
			double step = movementSign * absoluteStepDistance;

			double dummySpeed = step;
			double previousDistanceTravelled = absoluteDistanceTravelled;

			bool completedFullStep = RailwayVehicleShared.AdvanceAlongTrack
			(
				graph,
				step,
				wantTurn,
				exactTurns,
				railSystem,
				railSystem != null,
				automatedMovement,
				ref railCursor,
				ref dummySpeed,
				ref absoluteDistanceTravelled,
				true,
				out _,
				null,
				occupancyOwnerID,
				tapeRecorder
			);

			double absoluteStepDistanceMoved = Math.Max(0, absoluteDistanceTravelled - previousDistanceTravelled);
			if (absoluteStepDistanceMoved <= 1e-8) { break; }

			absoluteDistanceMoved += absoluteStepDistanceMoved;
			remainingAbsoluteDistance -= absoluteStepDistanceMoved;

			if (!completedFullStep || absoluteStepDistanceMoved < absoluteStepDistance - 1e-6) { break; }
		}

		cursor.SegmentHash = railCursor.SegmentHash;
		cursor.SegmentIndex = railCursor.SegmentIndex;
		cursor.NormalizedSegmentProgress = railCursor.NormalizedSegmentProgress;
		cursor.Direction = railCursor.Direction >= 0 ? 1 : -1;

		if (absoluteDistanceMoved <= 1e-8) return AdvanceResult.NoMove;

		moved = movementSign * absoluteDistanceMoved;
		return absoluteDistanceMoved >= requestedAbsoluteDistance - 1e-6 ? AdvanceResult.MovedAll : AdvanceResult.MovedPartial;
	}

	public static bool TryAdvancePartial(RailGraphLive graph, byte gauge, ref Cursor cursor, double distance, int wantTurn, out double moved)
	{
		return AdvancePartial(graph, gauge, ref cursor, distance, wantTurn, out moved) != AdvanceResult.NoMove;
	}

	public static bool TryAdvance(RailGraphLive graph, byte gauge, ref Cursor cursor, double distance, int wantTurn)
	{
		return AdvancePartial(graph, gauge, ref cursor, distance, wantTurn, out double moved) == AdvanceResult.MovedAll && Math.Abs(moved - distance) <= 1e-6;
	}
}
