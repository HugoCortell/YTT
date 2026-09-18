using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace YangTransport;

internal readonly struct OffscreenTrackState
{
	public readonly ulong SegmentHash;
	public readonly int SegmentIndex;
	public readonly double NormalizedSegmentProgress;
	public readonly int Direction;
	public readonly double Speed;
	public readonly double TravelledABS;
	public readonly int SGLeadEnd;

	public OffscreenTrackState
	(
		ulong segmentHash, int segmentIndex, double normalizedSegmentProgress, int direction,
		double speed, double absoluteDistanceTravelled, int standardGaugeLeadEnd = (int)SGTrainEnd.EndA
	)
	{
		SegmentHash = segmentHash;
		SegmentIndex = segmentIndex;
		NormalizedSegmentProgress = normalizedSegmentProgress;
		Direction = direction >= 0 ? 1 : -1;
		Speed = speed;
		TravelledABS = absoluteDistanceTravelled;
		SGLeadEnd = standardGaugeLeadEnd;
	}
}

/// Rare-path bridge between the generic offscreen convoy simulator and concrete rail vehicle classes.
/// Keeps the hot per-tick movement paths direct and avoids widening IRailwayConvoyVehicle with persistence-only hooks.
internal static class OffscreenRailVehicleAdapter
{
	public static bool TryGetSupported(Entity entity, out IRailwayConvoyVehicle vehicle)
	{
		switch (entity)
		{
			case EntityMinecart minecart:
				vehicle = minecart;
			return true;

			case EntityStandardGaugeLocomotive standardGaugeLocomotive:
				vehicle = standardGaugeLocomotive;
			return true;

			default:
				vehicle = null!;
			return false;
		}
	}

	public static void PersistNow(IRailwayConvoyVehicle vehicle)
	{
		switch (vehicle.Entity)
		{
			case EntityMinecart minecart:
				minecart.OfflineSimulationPersistNow();
			break;

			case EntityStandardGaugeLocomotive standardGaugeLocomotive:
				standardGaugeLocomotive.OfflineSimPersistNow();
			break;
		}
	}

	public static bool TryReadTrackState(IRailwayConvoyVehicle vehicle, out OffscreenTrackState trackState)
	{
		var attributes = vehicle.Entity.Attributes;
		ulong segmentHash = unchecked((ulong)attributes.GetLong("segHash", 0));
		if (segmentHash == 0)
		{
			trackState = default;
			return false;
		}

		double speed = attributes.GetDouble("speed", 0);
		int standardGaugeLeadEnd = (int)SGTrainEnd.EndA;
		if (vehicle.Entity is EntityStandardGaugeLocomotive)
		{
			SGTrainEnd leadEnd = SGTrainEndUtil.FromInt(attributes.GetInt(EntityStandardGaugeLocomotive.SGLeadEndAttribute, (int)SGTrainEnd.EndA));
			standardGaugeLeadEnd = (int)leadEnd;
			speed = Math.Abs(speed) * (leadEnd == SGTrainEnd.EndB ? -1 : 1);
		}

		trackState = new OffscreenTrackState
		(
			segmentHash,
			attributes.GetInt("segIndex", 0),
			attributes.GetDouble("segT01", 0.5),
			attributes.GetInt("dir", 1),
			speed,
			attributes.GetDouble("travelledAbs", 0),
			standardGaugeLeadEnd
		);

		return true;
	}

	public static void ApplyTrackState(IRailwayConvoyVehicle vehicle, in OffscreenTrackState trackState, bool resetHistory)
	{
		switch (vehicle.Entity)
		{
			case EntityMinecart minecart:
				minecart.OfflineSimApplyTrackState
				(
					trackState.SegmentHash, trackState.SegmentIndex, trackState.NormalizedSegmentProgress,
					trackState.Direction, trackState.Speed, trackState.TravelledABS, resetHistory
				);
			break;

			case EntityStandardGaugeLocomotive standardGaugeLocomotive:
				standardGaugeLocomotive.OfflineSimApplyTrackState
				(
					trackState.SegmentHash, trackState.SegmentIndex, trackState.NormalizedSegmentProgress, trackState.Direction,
					trackState.Speed, trackState.TravelledABS, resetHistory, SGTrainEndUtil.FromInt(trackState.SGLeadEnd)
				);
			break;
		}
	}

	public static bool TryImportPathTape(IRailwayConvoyVehicle convoyHead, ConvoyRoute pathTape, double pathHeadDistance)
	{
		switch (convoyHead.Entity)
		{
			case EntityMinecart minecart: return minecart.OfflineSimulationImportRailRoute(pathTape, pathHeadDistance);
			case EntityStandardGaugeLocomotive standardGaugeLocomotive: return standardGaugeLocomotive.OfflineSimImportPathTape(pathTape, pathHeadDistance);
			default: return false;
		}
	}

	public static void GetRollParams(IRailwayConvoyVehicle vehicle, out double rollingResistanceConstant, out double rollingResistanceSpeedCoefficient, out double stopEpsilon) // GetRollingResistanceParameters
	{
		switch (vehicle.Entity)
		{
			case EntityMinecart minecart:
				minecart.OfflineSimGetRollingResistanceParameters(out rollingResistanceConstant, out rollingResistanceSpeedCoefficient, out stopEpsilon);
			return;

			case EntityStandardGaugeLocomotive standardGaugeLocomotive:
				standardGaugeLocomotive.OfflineSimGetRollingResistParams(out rollingResistanceConstant, out rollingResistanceSpeedCoefficient, out stopEpsilon);
			return;

			default:
				rollingResistanceConstant = 0.35;
				rollingResistanceSpeedCoefficient = 0.08;
				stopEpsilon = 0.02;
			return;
		}
	}
}
