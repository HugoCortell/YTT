using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace YangTransport;

/// Rare-path helper for one-time rail-vehicle facing normalization, mostly when linking and stuff.
internal static class RailVehicleFacingAlignment
{
	private const double FlipDotThreshold = -0.05;
	private const double MinVectorLengthSQ = 1e-8;

	private readonly struct PlannedFlip
	{
		public readonly IRailwayConvoyVehicle Vehicle;
		public readonly RailwayVehicleShared.RailCursor Cursor;

		public PlannedFlip(IRailwayConvoyVehicle vehicle, RailwayVehicleShared.RailCursor cursor)
		{
			Vehicle = vehicle;
			Cursor = cursor;
		}
	}

	internal static bool TryAlignForMerge(
		RailGraphServerSystem railGraphSystem,
		IReadOnlyList<IRailwayConvoyVehicle> sourceMembers,
		IReadOnlyList<IRailwayConvoyVehicle> targetMembers,
		out string errorCode)
	{
		errorCode = "";

		if (railGraphSystem == null) { errorCode = "linkagepole-railgraph-unavailable"; return false; }

		if (sourceMembers == null || sourceMembers.Count == 0 || targetMembers == null || targetMembers.Count == 0)
		{
			errorCode = "linkagepole-missing-members";
			return false;
		}

		IRailwayConvoyVehicle? anchor = ChooseFacingAnchor(sourceMembers, targetMembers);
		if (anchor == null) return true;
		if (!TryGetHorizontalFacing(anchor, out double targetX, out double targetZ)) { errorCode = "linkagepole-facing-unreadable"; return false; }

		var plannedFlips = new List<PlannedFlip>(sourceMembers.Count + targetMembers.Count);
		var seenEntityIDs = new HashSet<long>();

		if (!TryPlanMemberFlips(railGraphSystem, sourceMembers, targetX, targetZ, plannedFlips, seenEntityIDs, out errorCode)) return false;
		if (!TryPlanMemberFlips(railGraphSystem, targetMembers, targetX, targetZ, plannedFlips, seenEntityIDs, out errorCode)) return false;

		for (int planIndex = 0; planIndex < plannedFlips.Count; planIndex++) { ApplyPlannedFlip(plannedFlips[planIndex], publishOccupancy: false); }

		return true;
	}

	internal static void PreferFacingOutwardHead(List<IRailwayConvoyVehicle> orderedVehicles)
	{
		if (orderedVehicles == null || orderedVehicles.Count < 2) return;
		if (!TryGetHorizontalFacing(orderedVehicles[0], out double fx, out double fz)) return;

		Entity head = orderedVehicles[0].Entity;
		Entity next = orderedVehicles[1].Entity;

		double dx = next.ServerPos.X - head.ServerPos.X;
		double dz = next.ServerPos.Z - head.ServerPos.Z;
		double directionLengthSQ = dx * dx + dz * dz;
		if (directionLengthSQ < MinVectorLengthSQ) return;

		double inverseLength = 1.0 / Math.Sqrt(directionLengthSQ);
		dx *= inverseLength;
		dz *= inverseLength;

		// Correct train head selection, the head's body-facing direction should point away from the next car.
		// If it points into the consist, reverse the rail-distance order.
		if (fx * dx + fz * dz > 0.05) orderedVehicles.Reverse();
	}

	internal static bool TryFlipSingleVehicle(IRailwayConvoyVehicle vehicle, out string error)
	{
		error = "";

		if (vehicle == null)															{ error = "No rail vehicle selected.";						return false; }
		Entity entity = vehicle.Entity; if (entity.Api?.Side != EnumAppSide.Server)		{ error = "Wagon Flip Stick only runs on the server.";		return false; }
		if (vehicle.Derailed)															{ error = "Cannot flip, vehicle derailed.";					return false; }

		var railGraphSystem = entity.Api.ModLoader.GetModSystem<RailGraphServerSystem>();
		if (railGraphSystem == null)													{ error = "Cannot flip, rail graph missing!";				return false; }
		
		if (vehicle.Entity is not (EntityMinecart or EntityStandardGaugeLocomotive))	{ error = "Cannot flip, not needed or not intended for.";	return false; }
		if (!TryPlanFlip(railGraphSystem, vehicle, out var cursor))						{ error = "Cannot flip, could not figure out how.";			return false; }

		ApplyPlannedFlip(new PlannedFlip(vehicle, cursor), publishOccupancy: false);

		long ownerID = vehicle.ConvoyHeadEntityID != 0 ? vehicle.ConvoyHeadEntityID : vehicle.Entity.EntityId;
		IRailwayConvoyVehicle? owner = entity.Api.World.GetEntityById(ownerID) as IRailwayConvoyVehicle ?? vehicle;

		if (vehicle.ConvoyHeadEntityID != 0)
		{
			var convoySystem = entity.Api.ModLoader.GetModSystem<RailConvoySystem>();
			if (convoySystem == null || !convoySystem.TrySeedLoadedConvoyRailSafetyState(ownerID))
			{
				error = "Flipped, but could not reseed linked convoy safety state.";
				return false;
			}
		}
		else
		{
			IRailwayConvoyVehicle[] singleVehicleConvoy = { vehicle };
			if (!vehicle.ServerSeedConvoyRailStateFromOrderedConvoy(singleVehicleConvoy))
			{
				error = "Flipped, but could not reseed vehicle safety state.";
				return false;
			}
		}
		if (!TryPublishOccupancy(railGraphSystem, owner)) { error = "Flipped, but could not refresh occupancy."; return false; }

		return true;
	}

	internal static bool TryPlanInPlaceFacingFlip
	(
		RailGraphLive graph, double sourceShift, double expectedX, double expectedY, double expectedZ,
		in RailwayVehicleShared.RailCursor boundCursor, out RailwayVehicleShared.RailCursor flippedCursor
	)
	{
		flippedCursor = default;
		if (graph == null || boundCursor.SegmentHash == 0 || boundCursor.PolyXYZ16 == null || boundCursor.PointCount < 2) return false;

		RailwayVehicleShared.RailCursor best = boundCursor;
		double bestScore = double.MaxValue;

		if (Math.Abs(sourceShift) <= 1e-8) { bestScore = 0; }
		else
		{
			ReadOnlySpan<int> attempts = stackalloc int[] { 0, -1, 1 };

			for (int attemptIndex = 0; attemptIndex < attempts.Length; attemptIndex++)
			{
				var candidate = boundCursor;
				double speed = 0;
				double travel = 0;

				bool advancementSucceeded = RailwayVehicleShared.AdvanceAlongTrack
				(
					graph, sourceShift, attempts[attemptIndex], exactTurns: null, railGraphSystem: null, useSignalAuthority: false, ref candidate,
					ref speed, ref travel, trackTravelledABS: true, out _, collisionTrail: null, occupancyOwnerId: 0, tapeRecorder: null
				);

				if (!advancementSucceeded || Math.Abs(travel - Math.Abs(sourceShift)) > Math.Max(0.01, Math.Abs(sourceShift) * 0.02)) continue;

				var poseProbe = candidate;
				if (!RailwayVehicleShared.TryReadWorldPoseFromTrack(ref poseProbe, out double x, out double y, out double z, out _, out _)) continue;

				double dx = expectedX - x;
				double dy = expectedY - y;
				double dz = expectedZ - z;
				double score = dx * dx + dy * dy * 0.25 + dz * dz;

				if (score < bestScore)
				{
					bestScore = score;
					best = candidate;
				}
			}
		}

		if (double.IsInfinity(bestScore) || bestScore == double.MaxValue) return false;

		best.Direction = best.Direction >= 0 ? -1 : 1;
		ClearOrientationCache(ref best);
		flippedCursor = best;
		return true;
	}

	private static IRailwayConvoyVehicle? ChooseFacingAnchor(IReadOnlyList<IRailwayConvoyVehicle> sourceMembers, IReadOnlyList<IRailwayConvoyVehicle> targetMembers)
	{
		IRailwayConvoyVehicle? sourceEngine = FirstTractionEngine(sourceMembers);
		IRailwayConvoyVehicle? targetEngine = FirstTractionEngine(targetMembers);

		if (sourceEngine != null && targetEngine == null) return sourceEngine;
		if (targetEngine != null && sourceEngine == null) return targetEngine;

		return sourceMembers.Count > targetMembers.Count ? FirstFacingCapable(sourceMembers) : FirstFacingCapable(targetMembers);
	}

	private static IRailwayConvoyVehicle? FirstTractionEngine(IReadOnlyList<IRailwayConvoyVehicle> members)
	{
		for (int memberIndex = 0; memberIndex < members.Count; memberIndex++)
		{
			if (members[memberIndex].HasTractionEngine && TryGetHorizontalFacing(members[memberIndex], out _, out _)) return members[memberIndex];
		}

		return null;
	}

	private static IRailwayConvoyVehicle? FirstFacingCapable(IReadOnlyList<IRailwayConvoyVehicle> members)
	{
		for (int memberIndex = 0; memberIndex < members.Count; memberIndex++)
		{
			if (TryGetHorizontalFacing(members[memberIndex], out _, out _)) return members[memberIndex];
		}

		return null;
	}

	private static bool TryPlanMemberFlips
	(
		RailGraphServerSystem railGraphSystem, IReadOnlyList<IRailwayConvoyVehicle> members,
		double targetX, double targetZ, List<PlannedFlip> plannedFlips, HashSet<long> seenEntityIDs, out string errorCode
	)
	{
		errorCode = "";

		for (int memberIndex = 0; memberIndex < members.Count; memberIndex++)
		{
			IRailwayConvoyVehicle vehicle = members[memberIndex];
			if (!seenEntityIDs.Add(vehicle.Entity.EntityId)) continue;

			if (vehicle.Entity is not (EntityMinecart or EntityStandardGaugeLocomotive)) continue;
			if (!TryGetHorizontalFacing(vehicle, out double fx, out double fz)) { errorCode = "linkagepole-facing-unreadable"; return false; }

			double facingDotProduct = fx * targetX + fz * targetZ;
			if (facingDotProduct >= FlipDotThreshold) continue;

			if (!TryPlanFlip(railGraphSystem, vehicle, out var cursor)) { errorCode = "linkagepole-facing-alignment-failed"; return false; }

			plannedFlips.Add(new PlannedFlip(vehicle, cursor));
		}

		return true;
	}

	private static bool TryGetHorizontalFacing(IRailwayConvoyVehicle vehicle, out double fx, out double fz)
	{
		bool facingReadSucceeded;
		switch (vehicle.Entity)
		{
			case EntityMinecart cart:
				facingReadSucceeded = cart.ServerTryGetFacingDirection(out fx, out fz);
			break;

			case EntityStandardGaugeLocomotive standardGaugeLocomotive:
				facingReadSucceeded = standardGaugeLocomotive.ServerTryGetHorizontalFacing(out fx, out fz);
			break;

			default:
				fx = fz = 0;
			return false;
		}

		if (!facingReadSucceeded) return false;

		double facingLengthSQ = fx * fx + fz * fz;
		if (facingLengthSQ < MinVectorLengthSQ) return false;

		double inverseLength = 1.0 / Math.Sqrt(facingLengthSQ);
		fx *= inverseLength;
		fz *= inverseLength;
		return true;
	}

	private static bool TryPlanFlip(RailGraphServerSystem railGraphSystem, IRailwayConvoyVehicle vehicle, out RailwayVehicleShared.RailCursor cursor)
	{
		switch (vehicle.Entity)
		{
			case EntityMinecart cart: return cart.ServerTryPlanFacingFlip(railGraphSystem, out cursor);
			case EntityStandardGaugeLocomotive standardGaugeLocomotive: return standardGaugeLocomotive.ServerTryPlanFacingFlip(railGraphSystem, out cursor);
			default: cursor = default; return false;
		}
	}

	private static void ApplyPlannedFlip(PlannedFlip plan, bool publishOccupancy)
	{
		switch (plan.Vehicle.Entity)
		{
			case EntityMinecart cart:
				cart.ServerApplyPlannedFacingFlip(plan.Cursor, publishOccupancy);
			break;

			case EntityStandardGaugeLocomotive standardGaugeLocomotive:
				standardGaugeLocomotive.ServerApplyPlannedFacingFlip(plan.Cursor, publishOccupancy);
			break;
		}
	}

	private static bool TryPublishOccupancy(RailGraphServerSystem railGraphSystem, IRailwayConvoyVehicle owner)
	{
		return owner.Entity switch
		{
			EntityMinecart cart => cart.TryPublishRailOccupancyFootprint(railGraphSystem, railGraphSystem.Graph, force: true),
			EntityStandardGaugeLocomotive standardGaugeLocomotive => standardGaugeLocomotive.TryPublishRailOccupancyFootprint(railGraphSystem, railGraphSystem.Graph, force: true),
			_ => false
		};
	}

	private static void ClearOrientationCache(ref RailwayVehicleShared.RailCursor cursor)
	{
		cursor.OrientationCacheSegmentHash = 0;
		cursor.OrientationCacheSegmentIndex = -1;
		cursor.OrientationCacheDirection = 0;
		cursor.OrientationCacheYaw = 0f;
		cursor.OrientationCacheRoll = 0f;
	}
}
