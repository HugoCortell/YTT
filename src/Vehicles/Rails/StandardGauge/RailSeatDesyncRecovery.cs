using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

// THIS ENTIRE FILE SHOULD BE DELETED ONCE THE BASE GAME FIXES THE ISSUE.
namespace YangTransport;

/// Client-side workaround for rail riders becoming detached from their live seat after severe network disruption.
/// Repairs stale MountedOn seat references (and, if vanilla does not recover on its own, persistent local position divergence too).
internal static class RailSeatDesyncRecovery
{
	private const int CheckIntervalMS = 500;
	private const double PositionDivergenceThresholdSquared = 2.25;
	private const int PositionDivergenceChecksBeforeRecovery = 5; // First detection plus about two seconds for vanilla to try and recover on its own.
	private static RecoveryState? ActiveRecovery;

	internal static void Start(RailVehicleSeat seat, EntityAgent passenger)
	{
		if (passenger?.Api is not ICoreClientAPI clientAPI || !ReferenceEquals(clientAPI.World?.Player?.Entity, passenger)) return;

		StopActive();
		ActiveRecovery = new RecoveryState(seat, passenger, clientAPI);
		ActiveRecovery.ListenerID = clientAPI.Event.RegisterGameTickListener(OnCheck, CheckIntervalMS);
	}

	internal static void Stop(RailVehicleSeat seat, EntityAgent passenger)
	{
		RecoveryState? state = ActiveRecovery;
		if (state == null || !ReferenceEquals(state.Seat, seat) || !ReferenceEquals(state.Passenger, passenger)) return;
		StopActive();
	}

	private static void OnCheck(float deltaTime)
	{
		RecoveryState? state = ActiveRecovery; if (state == null) return;

		EntityAgent passenger = state.Passenger;
		RailVehicleSeat seat = state.Seat;
		ICoreClientAPI clientAPI = state.ClientAPI;

		if (!ReferenceEquals(clientAPI.World?.Player?.Entity, passenger) || !ReferenceEquals(passenger.MountedOn, seat)) { StopActive(); return; }

		Entity? seatOwner = seat.Entity; if (seatOwner == null) { state.PositionDivergenceChecks = 0; return; }
		Entity? liveOwner = clientAPI.World.GetEntityById(seatOwner.EntityId); if (liveOwner == null) { state.PositionDivergenceChecks = 0; return; }

		if (!ReferenceEquals(liveOwner, seatOwner))
		{
			state.PositionDivergenceChecks = 0;
			TryRecoverStaleSeatReference(state, liveOwner);
			return;
		}

		TryRecoverPersistentPositionDivergence(state, seat);
	}

	private static void TryRecoverStaleSeatReference(RecoveryState state, Entity liveOwner)
	{
		RailVehicleSeat staleSeat = state.Seat;
		EntityAgent passenger = state.Passenger;
		EntityBehaviorSeatable? seatable = liveOwner.GetBehavior<EntityBehaviorSeatable>();
		IMountableSeat[]? seats = seatable?.Seats; if (seats == null) return;

		for (int seatIndex = 0; seatIndex < seats.Length; seatIndex++)
		{
			if (seats[seatIndex] is not RailVehicleSeat liveSeat || liveSeat.SeatId != staleSeat.SeatId) continue;
			if (liveSeat.Passenger != null && !ReferenceEquals(liveSeat.Passenger, passenger)) return;

			if (passenger.TryMount(liveSeat))
			{
				state.ClientAPI.Logger.Notification
				(
					"[YangTransport] Recovered stale local rail seat reference for entity {0}, seat '{1}'.",
					liveOwner.EntityId, liveSeat.SeatId
				);
			}
			return;
		}
	}

	private static void TryRecoverPersistentPositionDivergence(RecoveryState state, RailVehicleSeat seat)
	{
		EntityAgent passenger = state.Passenger;
		EntityPos seatPosition = seat.SeatPosition;
		EntityPos passengerPosition = passenger.Pos;

		if
		(
			seatPosition.Dimension != passengerPosition.Dimension ||
			!double.IsFinite(seatPosition.X) || !double.IsFinite(seatPosition.Y) || !double.IsFinite(seatPosition.Z) ||
			!double.IsFinite(passengerPosition.X) || !double.IsFinite(passengerPosition.Y) || !double.IsFinite(passengerPosition.Z)
		) { state.PositionDivergenceChecks = 0; return; }

		double deltaX = passengerPosition.X - seatPosition.X;
		double deltaY = passengerPosition.Y - seatPosition.Y;
		double deltaZ = passengerPosition.Z - seatPosition.Z;
		double distanceSquared = deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ;

		if (distanceSquared <= PositionDivergenceThresholdSquared) { state.PositionDivergenceChecks = 0; return; }

		state.PositionDivergenceChecks++;
		if (state.PositionDivergenceChecks < PositionDivergenceChecksBeforeRecovery) return;

		// Match vanilla mounted-player physics, only repair the local position and leave server state/networking alone.
		passengerPosition.SetPos(seatPosition);
		passengerPosition.Motion.Set(0.0, 0.0, 0.0);
		state.PositionDivergenceChecks = 0;

		state.ClientAPI.Logger.Notification
		(
			"[YangTransport] Recovered a desynced seat position for {0}, at seat {1}.",
			seat.Entity?.EntityId ?? 0, seat.SeatId
		);
	}

	private static void StopActive()
	{
		RecoveryState? state = ActiveRecovery;
		ActiveRecovery = null;
		if (state?.ListenerID > 0) { state.ClientAPI.Event.UnregisterGameTickListener(state.ListenerID); }
	}

	private sealed class RecoveryState
	{
		internal readonly RailVehicleSeat Seat;
		internal readonly EntityAgent Passenger;
		internal readonly ICoreClientAPI ClientAPI;
		internal long ListenerID;
		internal int PositionDivergenceChecks;

		internal RecoveryState(RailVehicleSeat seat, EntityAgent passenger, ICoreClientAPI clientAPI)
		{
			Seat = seat;
			Passenger = passenger;
			ClientAPI = clientAPI;
		}
	}
}
