using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;

namespace YangTransport;

/// Rail binding helpers used for convoy ordering (event-only). Does not mutate entity state.
internal static class RailBindUtil
{
	internal readonly struct BindPoint
	{
		public readonly ulong EdgeHash;
		public readonly int Dimension;
		public readonly byte Gauge;

		public readonly int[] XYZ16;
		public readonly int PointCount;

		public readonly int SegmentIndex;
		public readonly double SegmentInterpolation;

		public readonly double EdgeLength;
		public readonly double SFromA; // Distance From Start Endpoint

		public readonly RailGraphLive.EndpointKey EndA;
		public readonly RailGraphLive.EndpointKey EndB;

		public BindPoint
		(
			ulong edgeHash, int dimension, byte gauge, int[] polylineCoordinates16, int pointCount, int segmentIndex, double segmentInterpolation,
			double edgeLength, double distanceFromStartEndpoint, RailGraphLive.EndpointKey startEndpoint, RailGraphLive.EndpointKey endEndpoint
		)
		{
			EdgeHash = edgeHash;
			Dimension = dimension;
			Gauge = gauge;
			XYZ16 = polylineCoordinates16;
			PointCount = pointCount;
			SegmentIndex = segmentIndex;
			SegmentInterpolation = segmentInterpolation;
			EdgeLength = edgeLength;
			SFromA = distanceFromStartEndpoint;
			EndA = startEndpoint;
			EndB = endEndpoint;
		}
	}

	public static bool TryBindNearest
	(
		RailGraphLive graph, Vec3d worldPosition, int dimension,
		byte gauge, int radiusBlocks, List<ulong> candidatesScratch, out BindPoint bindingPoint
	)
	{
		bindingPoint = default;

		candidatesScratch.Clear();
		graph.CollectEdgeCandidatesNear(worldPosition, dimension, radiusBlocks, candidatesScratch, clear: false);

		double bestDistanceSQ = double.MaxValue;
		ulong bestEdgeHash = 0;
		int bestSegmentIndex = 0;
		double bestSegmentInterpolation = 0;
		int[]? bestPolylineCoordinates16 = null;
		int bestPointCount = 0;

		double bestPrefixBefore = 0;
		double bestSegmentLength = 0;
		double bestEdgeLength = 0;

		for (int candidateIndex = 0; candidateIndex < candidatesScratch.Count; candidateIndex++)
		{
			ulong edgeHash = candidatesScratch[candidateIndex];

			if (!graph.TryGetEdgeGauge(edgeHash, out byte edgeGauge) || edgeGauge != gauge) continue;
			if (!graph.TryGetPolyline16(edgeHash, out var polylineCoordinates16)) continue;

			int pointCount = polylineCoordinates16.Length / 3;
			if (pointCount < 2) continue;

			double edgePrefixLength = 0;

			for (int pointIndex = 0; pointIndex < pointCount - 1; pointIndex++)
			{
				int currentPointOffset = pointIndex * 3;
				int nextPointOffset = (pointIndex + 1) * 3;

				double ax = polylineCoordinates16[currentPointOffset + 0] / 16.0;
				double ay = polylineCoordinates16[currentPointOffset + 1] / 16.0;
				double az = polylineCoordinates16[currentPointOffset + 2] / 16.0;

				double bx = polylineCoordinates16[nextPointOffset + 0] / 16.0;
				double by = polylineCoordinates16[nextPointOffset + 1] / 16.0;
				double bz = polylineCoordinates16[nextPointOffset + 2] / 16.0;

				double segmentLength = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az));
				if (segmentLength < 1e-9) continue;

				double distanceSQ = DistanceSQFromPointToSegment(worldPosition, ax, ay, az, bx, by, bz, out double segmentInterpolation);
				if (distanceSQ < bestDistanceSQ)
				{
					bestDistanceSQ = distanceSQ;
					bestEdgeHash = edgeHash;
					bestSegmentIndex = pointIndex;
					bestSegmentInterpolation = segmentInterpolation;
					bestPolylineCoordinates16 = polylineCoordinates16;
					bestPointCount = pointCount;
					bestPrefixBefore = edgePrefixLength;
					bestSegmentLength = segmentLength;
					// edge length filled after finishing this edge loop
				}

				edgePrefixLength += segmentLength;
			}

			if (bestEdgeHash == edgeHash) { bestEdgeLength = edgePrefixLength; }
		}

		if (bestEdgeHash == 0 || bestPolylineCoordinates16 == null) return false;

		double distanceFromStartEndpoint = bestPrefixBefore + bestSegmentLength * GameMath.Clamp(bestSegmentInterpolation, 0, 1);

		var startEndpoint = RailGraphLive.EndpointKey.FromXYZ16(bestPolylineCoordinates16, 0, dimension, gauge);
		var endEndpoint = RailGraphLive.EndpointKey.FromXYZ16(bestPolylineCoordinates16, (bestPointCount - 1) * 3, dimension, gauge);

		bindingPoint = new BindPoint
		(
			bestEdgeHash, dimension, gauge, bestPolylineCoordinates16, bestPointCount, bestSegmentIndex,
			bestSegmentInterpolation, bestEdgeLength, distanceFromStartEndpoint, startEndpoint, endEndpoint
		);
		return true;
	}

	public static double ComputePolylineLength(int[] polylineCoordinates16)
	{
		int pointCount = polylineCoordinates16.Length / 3;
		if (pointCount < 2) return 0;

		double totalLength = 0;
		for (int pointIndex = 0; pointIndex < pointCount - 1; pointIndex++)
		{
			int currentPointOffset = pointIndex * 3;
			int nextPointOffset = (pointIndex + 1) * 3;

			double ax = polylineCoordinates16[currentPointOffset + 0] / 16.0;
			double ay = polylineCoordinates16[currentPointOffset + 1] / 16.0;
			double az = polylineCoordinates16[currentPointOffset + 2] / 16.0;

			double bx = polylineCoordinates16[nextPointOffset + 0] / 16.0;
			double by = polylineCoordinates16[nextPointOffset + 1] / 16.0;
			double bz = polylineCoordinates16[nextPointOffset + 2] / 16.0;

			totalLength += Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az));
		}

		return totalLength;
	}

	private static double DistanceSQFromPointToSegment(Vec3d point, double ax, double ay, double az, double bx, double by, double bz, out double segmentInterpolation)
	{
		double abx = bx - ax, aby = by - ay, abz = bz - az;
		double apx = point.X - ax, apy = point.Y - ay, apz = point.Z - az;

		double segmentLengthSQ = abx * abx + aby * aby + abz * abz;
		if (segmentLengthSQ <= 1e-12) { segmentInterpolation = 0; return apx * apx + apy * apy + apz * apz; }

		double projectedInterpolation = (apx * abx + apy * aby + apz * abz) / segmentLengthSQ;
		if (projectedInterpolation <= 0) { segmentInterpolation = 0; return apx * apx + apy * apy + apz * apz; }
		if (projectedInterpolation >= 1)
		{
			segmentInterpolation = 1;
			double bpx = point.X - bx, bpy = point.Y - by, bpz = point.Z - bz;
			return bpx * bpx + bpy * bpy + bpz * bpz;
		}

		segmentInterpolation = projectedInterpolation;
		double cx = ax + abx * projectedInterpolation;
		double cy = ay + aby * projectedInterpolation;
		double cz = az + abz * projectedInterpolation;

		double dx = point.X - cx, dy = point.Y - cy, dz = point.Z - cz;
		return dx * dx + dy * dy + dz * dz;
	}
}
