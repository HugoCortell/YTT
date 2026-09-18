using System.Collections.Generic;
using Vintagestory.API.Common.Entities;

namespace YangTransport;

/// Thin convoy-facing contract for rail vehicles used only for rare on-demand operations.
public interface IRailwayConvoyVehicle
{
	Entity Entity { get; }

	byte TrackGauge { get; }
	int BindRadiusBlocks { get; }

	long ConvoyHeadEntityID { get; }
	int ConvoyOrderIndex { get; }
	int ExpectedConvoyMemberCount { get; }
	long PreviousVehicleID { get; }
	
	double ConvoyDistanceBehindHead { get; } // Canonical designed source distance behind the stable convoy head, not the current physical linkage gap.
	int SelfWeightCached { get; } // Physical rail extent behind the vehicle's authoritative sample point for signal/footprint release and train collision.
	double OccupancyRearExtentBlocks { get; } // Physical rail extent in front of the vehicle's authoritative sample point for train collision.
	double OccupancyFrontExtentBlocks { get; }
	bool HasTractionEngine { get; }
	bool HasConductorLocustInstalled { get; }
	bool Derailed { get; }

	/// Distance from the vehicle's authoritative/front sample point to the next vehicle's authoritative/front sample point.
	/// Minecarts use their old constant, SG stock reads this from JSON so vehicle length stays explicit and simple.
	double ConvoySpacingToNext { get; }
	EntityBehaviorSteamPowered? SteamEngineBehaviour { get; }

	void ServerSetConvoyState(long headID, int index, long previousVehicleID, double distanceBehindHead);
	void ServerClearConvoyState();
	void ServerInvalidateRailBinding(bool persist);
	void ServerSetConvoyStatsWatched(int count, int weight, double tailDistance, double occupancyRearDistance);
	bool ServerSeedConvoyRailStateFromOrderedConvoy(IReadOnlyList<IRailwayConvoyVehicle> ordered, IReadOnlyDictionary<long, double>? physicalDistanceFromHead = null);

	void GetCouplerWorldPositions(out double frontX, out double frontY, out double frontZ, out double rearX, out double rearY, out double rearZ);
}
