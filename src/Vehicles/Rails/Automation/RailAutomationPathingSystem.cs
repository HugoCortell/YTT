using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace YangTransport;

internal enum RailAutomationDriveMode : byte
{
	Hold,
	DriveForward,
	ServiceBrake
}

/// Vehicle-agnostic automation traction intent. Vehicle physics adapters own the representation of direction and braking.
internal readonly record struct RailAutomationDriveCommand(RailAutomationDriveMode Mode, double Traction)
{
	internal static RailAutomationDriveCommand Hold => new(RailAutomationDriveMode.Hold, 0);
	internal static RailAutomationDriveCommand ServiceBrake => new(RailAutomationDriveMode.ServiceBrake, 0);
	internal static RailAutomationDriveCommand DriveForward => new(RailAutomationDriveMode.DriveForward, 1);
}


/// Runtime-only handoff object for preserving an executable automation route across loaded/offscreen authority transfers
/// The route itself is immutable except for per-movement signal/path status in its turn plan.
internal readonly record struct RailAutomationRouteLease(
	RailAutomationRoute Route,
	int TargetRouteIndex,
	bool TargetIsStationGroup,
	bool RefreshRequested,
	bool PinnedTargetInvalidated
);

/// Server-only conductor-locust automation controller. Keeps pathing runtime-only and performs path searches only on demand.
internal sealed class RailAutomationPathingSystem : ModSystem
{
	private const int MaxPathSearchesPerServerTick = 4;
	private const int MaxRejectedChainRouteCandidates = 16;
	private const int ComponentBuildWorkPerTick = 4096;
	private const int ComponentBuildSlice = 512;

	private const long PathRetryBaseMS = 3_000;
	private const long PathIndexRetryBaseMS = 200;
	private const long RouteWorkBudgetRetryBaseMS = 75;
	private const long StationCriteriaCheckMS = 1_500;

	private ICoreServerAPI? ServerAPI;
	private RailGraphServerSystem? RailGraphSystem;
	private RailStationRegistrySystem? StationRegistry;
	private RailConvoySystem? ConvoySystem;

	private readonly Dictionary<long, ConvoyAutomationState> AutomationStates = new();
	private readonly Dictionary<ulong, HashSet<long>> RouteOwnersByEdge = new();
	private readonly Dictionary<RailStationRegistrySystem.StationBlockKey, HashSet<long>> RouteOwnersByStation = new();
	private readonly HashSet<long> AffectedRouteOwnersScratch = new();

	// Whole-network index is retained only for the explicitly synchronous minimap path.
	private RailPathIndex? GlobalPathIndex;
	private int GlobalIndexedGraphVersion = -1;

	// Normal automation indexes only the physical connected component containing the convoy. | Every edge in a component points at the same immutable cache entry.
	private readonly Dictionary<ulong, ComponentIndexEntry> ComponentIndexByEdge = new();
	private readonly List<ComponentIndexEntry> ComponentIndexes = new();
	private readonly Dictionary<ulong, RailPathIndex.ComponentBuildJob> ComponentBuildJobByEdge = new();
	private readonly List<RailPathIndex.ComponentBuildJob> ComponentBuildJobs = new();
	private readonly List<ulong> ComponentBuildEdgeScratch = new();
	private int ComponentBuildRoundRobin;
	private long NextComponentBuildSequence;
	private long ComponentBuildListenerID;

	private long BudgetTickKey;
	private int SearchesThisBudgetTick;

	public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

	public override void Start(ICoreAPI coreAPI)
	{
		ServerAPI = coreAPI as ICoreServerAPI;
		if (ServerAPI == null) return;

		RailGraphSystem = ServerAPI.ModLoader.GetModSystem<RailGraphServerSystem>();
		StationRegistry = ServerAPI.ModLoader.GetModSystem<RailStationRegistrySystem>();
		ConvoySystem = ServerAPI.ModLoader.GetModSystem<RailConvoySystem>();
		if (StationRegistry != null) StationRegistry.Changed += OnStationRegistryChanged;
		ComponentBuildListenerID = ServerAPI.Event.RegisterGameTickListener(ProcessComponentBuildJobs, 50);
	}

	public override void Dispose()
	{
		if (ServerAPI != null && ComponentBuildListenerID != 0) ServerAPI.Event.UnregisterGameTickListener(ComponentBuildListenerID);
		if (StationRegistry != null) StationRegistry.Changed -= OnStationRegistryChanged;
		CancelAllComponentBuildJobs();
		AutomationStates.Clear();
		RouteOwnersByEdge.Clear();
		RouteOwnersByStation.Clear();
		AffectedRouteOwnersScratch.Clear();
		ComponentIndexByEdge.Clear();
		ComponentIndexes.Clear();
		ComponentBuildJobByEdge.Clear();
		ComponentBuildJobs.Clear();
		GlobalPathIndex = null;
		base.Dispose();
	}

	internal void ReleaseConvoy(long headID)
	{
		if (headID == 0) return;
		RemoveState(headID);
		ClearActionForConvoy(headID);
	}


	internal bool TryExportRouteLease(long headID, out RailAutomationRouteLease lease)
	{
		lease = default;
		if (headID == 0 || !AutomationStates.TryGetValue(headID, out ConvoyAutomationState? state) || state.Route == null)
		{ return false; }

		lease = new RailAutomationRouteLease
		(
			state.Route,
			state.TargetRouteIndex,
			state.CurrentDestinationIsStationGroup,
			state.RefreshRequested,
			state.PinnedTargetInvalidated
		);
		return true;
	}

	internal bool AdoptRouteLease(long headID, Entity timetableOwner, in RailAutomationRouteLease lease)
	{
		if (headID == 0 || timetableOwner == null || lease.Route == null) return false;

		ConvoyAutomationState state = GetState(headID);
		int timetableSerial = ConductorTimetableStorage.GetChangeSerial(timetableOwner);
		state.CachedTimetable = ConductorTimetableStorage.Load(timetableOwner);
		state.CachedTimetableOwnerID = timetableOwner.EntityId;
		state.CachedTimetableChangeSerial = timetableSerial;

		SetRoute(state, lease.Route, lease.TargetRouteIndex, lease.Route.Target, lease.TargetIsStationGroup);

		state.RefreshRequested = lease.RefreshRequested;
		state.PinnedTargetInvalidated = lease.PinnedTargetInvalidated;
		state.NextPathRetryMS = 0;
		state.NextPathIndexRetryMS = 0;

		return true;
	}

	internal bool DebugTryGetRoute(long headID, out RailAutomationRoute? route, out string statusText)
	{
		route = null;
		statusText = "";

		if (headID == 0) return false;
		if (!AutomationStates.TryGetValue(headID, out ConvoyAutomationState state)) return false;

		route = state.Route;
		statusText = state.LastActionText ?? "";
		return route != null;
	}

	internal bool TryGetActiveRoute(long headID, out RailAutomationRoute? route, out int targetRouteIndex)
	{
		return TryGetActiveRouteStatus(headID, out route, out targetRouteIndex, out _);
	}

	internal bool TryGetActiveRouteStatus(long headID, out RailAutomationRoute? route, out int targetRouteIndex, out bool isWaitingAtStation)
	{
		route = null;
		targetRouteIndex = -1;
		isWaitingAtStation = false;

		if (headID == 0) return false;
		if (!AutomationStates.TryGetValue(headID, out ConvoyAutomationState state)) return false;
		if (state.Route == null) return false;

		route = state.Route;
		targetRouteIndex = state.TargetRouteIndex;
		isWaitingAtStation = state.ArrivedStationAtMS != 0 || state.LastActionCode == LocustActionCode.WaitingAtStation;
		return true;
	}

	internal void OnRailGraphChanged(RailGraphLive graph, RailGraphChangeSet change)
	{
		if (graph == null || change == null || !change.HasAnyChange) return;

		GlobalPathIndex = null;
		GlobalIndexedGraphVersion = -1;
		if (change.GlobalInvalidation) { CancelAllComponentBuildJobs(); }
		else { PromoteOrCancelComponentBuildJobs(change, graph.BuildVersion); }
		InvalidateTouchedComponentIndexes(change);

		// An active route is an executable lease. Topology cache churn never destroys it. Only routes whose own edges were touched are scheduled for replacement.
		AffectedRouteOwnersScratch.Clear();
		if (change.GlobalInvalidation) { foreach (long headID in AutomationStates.Keys) AffectedRouteOwnersScratch.Add(headID); }
		else
		{
			for (int touchedEdgeIndex = 0; touchedEdgeIndex < change.TouchedEdges.Count; touchedEdgeIndex++)
			{
				if (!RouteOwnersByEdge.TryGetValue(change.TouchedEdges[touchedEdgeIndex], out HashSet<long>? owners)) continue;
				foreach (long headID in owners) AffectedRouteOwnersScratch.Add(headID);
			}

			// Added edges are absent from every existing route. Find owners of route edges adjacent to the edited endpoint
			// so a new branch is incorporated before a train reaches what used to be an unambiguous degree-two continuation.
			for (int touchedEndpointIndex = 0; touchedEndpointIndex < change.TouchedEndpoints.Count; touchedEndpointIndex++)
			{
				if (!graph.TryGetIncidentEdges(change.TouchedEndpoints[touchedEndpointIndex], out List<ulong> incidentEdges)) continue;
				for (int incidentEdgeIndex = 0; incidentEdgeIndex < incidentEdges.Count; incidentEdgeIndex++)
				{
					if (!RouteOwnersByEdge.TryGetValue(incidentEdges[incidentEdgeIndex], out HashSet<long>? owners)) continue;
					foreach (long headID in owners) AffectedRouteOwnersScratch.Add(headID);
				}
			}
		}

		foreach (long headID in AffectedRouteOwnersScratch)
		{
			if (!AutomationStates.TryGetValue(headID, out ConvoyAutomationState? state) || state.Route == null) continue;
			if (!state.Route.TouchesGraphChange(graph, change)) continue;
			state.RefreshRequested = true;
			state.NextPathRetryMS = 0;
			state.NextPathIndexRetryMS = 0;
		}
		AffectedRouteOwnersScratch.Clear();
	}

	private void OnStationRegistryChanged(RailStationRegistrySystem.RegistryChange change)
	{
		if (change.Kind == RailStationRegistrySystem.RegistryChangeKind.Added) return;

		if (change.Kind == RailStationRegistrySystem.RegistryChangeKind.Reset)
		{
			foreach (ConvoyAutomationState state in AutomationStates.Values)
			{
				if (state.Route == null) continue;
				state.RefreshRequested = true;
				state.PinnedTargetInvalidated = true;
				state.NextPathRetryMS = 0;
				state.NextPathIndexRetryMS = 0;
			}
			return;
		}

		RailStationRegistrySystem.RailStationEntry? oldEntry = change.OldEntry;
		if (oldEntry == null) return;

		// Renames and other metadata-only updates do not affect a pinned physical stop.
		if (change.Kind == RailStationRegistrySystem.RegistryChangeKind.Updated && oldEntry.SamePhysicalStopAs(change.NewEntry)) { return; }

		if (!RouteOwnersByStation.TryGetValue(oldEntry.Key, out HashSet<long>? owners)) return;
		AffectedRouteOwnersScratch.Clear();
		foreach (long headID in owners) AffectedRouteOwnersScratch.Add(headID);

		foreach (long headID in AffectedRouteOwnersScratch)
		{
			if (!AutomationStates.TryGetValue(headID, out ConvoyAutomationState? state) || state.Route == null) continue;
			state.RefreshRequested = true;
			state.PinnedTargetInvalidated = true;
			state.NextPathRetryMS = 0;
			state.NextPathIndexRetryMS = 0;
		}
		AffectedRouteOwnersScratch.Clear();
	}

	internal bool TryApplyAutomation
	(
		IRailwayConvoyVehicle headVehicle,
		IRailwayConvoyVehicle leadVehicle,
		ref RailwayVehicleShared.RailCursor cursor,
		out RailAutomationDriveCommand driveCommand,
		out RailExactTurnPlan? exactTurns
	)
	{
		driveCommand = RailAutomationDriveCommand.Hold;
		exactTurns = null;

		if (ServerAPI == null || RailGraphSystem == null || StationRegistry == null || headVehicle == null || leadVehicle == null) return false;

		long headID = GetHeadID(headVehicle);
		if (!HasConductorLocust(headID, headVehicle))
		{
			if (RemoveState(headID)) { ClearActionForConvoy(headID, headVehicle.Entity); }
			return false;
		}

		long nowMS = ServerAPI.World.ElapsedMilliseconds;
		ConvoyAutomationState state = GetState(headID);

		Entity owner = leadVehicle.Entity ?? headVehicle.Entity;
		if (owner == null)
		{
			SetAction(state, headVehicle.Entity, headID, LocustActionCode.Stuck, "Stuck");
			driveCommand = RailAutomationDriveCommand.ServiceBrake;
			return true;
		}

		ConductorTimetableDocument timetableDocument = GetCachedTimetable(owner, state);
		if (timetableDocument.Route.Count == 0)
		{
			ResetRoute(state);
			SetAction(state, headVehicle.Entity, headID, LocustActionCode.NoTimetable, "No timetable");
			driveCommand = RailAutomationDriveCommand.ServiceBrake;
			return true;
		}

		int targetIndex = timetableDocument.CurrentStationIndex >= 0 && timetableDocument.CurrentStationIndex < timetableDocument.Route.Count ? timetableDocument.CurrentStationIndex : 0;
		TimetableRouteEntryPacket entry = timetableDocument.Route[targetIndex];

		if (!TryGetCursorDimension(RailGraphSystem.Graph, cursor, out int currentDimension))
		{
			// Cursor repair is owned by the vehicle. Preserve the route lease so a transient cursor outage cannot throw away navigation state.
			SetAction(state, headVehicle.Entity, headID, LocustActionCode.Stuck, "Stuck");
			driveCommand = RailAutomationDriveCommand.ServiceBrake;
			exactTurns = state.Route?.TurnPlan;
			return true;
		}

		bool timetableChanged = state.Route != null && (state.TargetRouteIndex != targetIndex || state.RouteTimetableChangeSerial != state.CachedTimetableChangeSerial);
		if (timetableChanged && !state.PinnedTargetInvalidated)
		{
			state.RefreshRequested = true;
			state.PinnedTargetInvalidated = true;
			state.NextPathRetryMS = 0;
			state.NextPathIndexRetryMS = 0;
		}

		if (state.Route?.TurnPlan.PathBlocked == true && !state.RefreshRequested)
		{
			state.RefreshRequested = true;
			state.NextPathRetryMS = 0;
			state.NextPathIndexRetryMS = 0;
		}

		RailAutomationRoute? activeRoute = state.Route;
		bool routeRefreshNeeded = activeRoute == null || state.RefreshRequested;

		// Chain-signal alternate routing is also demand-driven. Normal movement with a valid lease does not touch the path index or station registry at all.
		bool chainRerouteWanted = activeRoute != null && activeRoute.TurnPlan.BoundaryBlocked && activeRoute.TurnPlan.BoundaryBlockReason == SignalAuthorityResult.ChainDownstreamOccupied;

		if (chainRerouteWanted && !routeRefreshNeeded && activeRoute != null)
		{
			RailExactTurnPlan blockedPlan = activeRoute.TurnPlan;
			ulong serial = RailGraphSystem.GetSignalAuthorityRevision(blockedPlan.BlockedSignalEndpoint);
			if (state.LastChainRepathOccupancySerial == serial && state.LastChainRepathSignalEndpoint.Equals(blockedPlan.BlockedSignalEndpoint) && state.LastChainRepathFromEdgeHash == blockedPlan.BlockedFromEdgeHash)
			{
				chainRerouteWanted = false;
			}
		}

		bool routeWorkDeferred = routeRefreshNeeded && !chainRerouteWanted && state.NextPathRetryMS > nowMS;

		RailPathIndex? activePathIndex = null;
		bool havePathIndex = false;
		if (!routeWorkDeferred && (routeRefreshNeeded || chainRerouteWanted) && nowMS >= state.NextPathIndexRetryMS)
		{
			havePathIndex = TryGetPathIndexForEdge(cursor.SegmentHash, out activePathIndex, out _) && activePathIndex != null;
			if (havePathIndex)	{ state.NextPathIndexRetryMS = 0; }
			else				{ state.NextPathIndexRetryMS = nowMS + PathIndexRetryDelayMS(headID); }
		}

		if (havePathIndex && activePathIndex != null && (routeRefreshNeeded || chainRerouteWanted))
		{
			bool preservePinnedTarget = activeRoute != null && !timetableChanged && state.TargetRouteIndex == targetIndex;

			if (TryPrepareTargetCandidates(activePathIndex, state, entry, cursor.Gauge, currentDimension, preservePinnedTarget, chainRerouteWanted, out bool isStationGroup))
			{
				ulong currentZoneID = 0;
				if (cursor.SegmentHash != 0) RailGraphSystem.Graph.TryGetOccupancyZoneID(cursor.SegmentHash, out currentZoneID);

				if (chainRerouteWanted && activeRoute != null)
				{
					TryRerouteAtBlockedChainSignal(activePathIndex, state, headID, cursor, currentZoneID, targetIndex, isStationGroup, nowMS);
					activeRoute = state.Route;
				}

				if (state.Route == null || state.RefreshRequested)
				{
					bool retryDue = nowMS >= state.NextPathRetryMS;
					bool canSearchNow = retryDue && TrySpendPathSearchBudget(nowMS);
					if (canSearchNow)
					{
						state.NextPathRetryMS = nowMS + RetryDelayMS(headID);
						if (activePathIndex.TryFindRouteGreedy(RailGraphSystem.Graph, cursor, state.TargetCandidates, currentZoneID, out RailAutomationRoute replacement))
						{
							SetRoute(state, replacement, targetIndex, replacement.Target, isStationGroup);
							state.NextPathRetryMS = 0;
							activeRoute = replacement;
						}
					}

					// Search capacity is shared across all convoys. Stagger retries instead of rebuilding station overlays and polling the budget every movement tick.
					else if (retryDue) { state.NextPathRetryMS = nowMS + RouteWorkBudgetRetryDelayMS(headID); }
				}
			}
			else if (routeRefreshNeeded) { state.NextPathRetryMS = nowMS + RetryDelayMS(headID); }
		}

		activeRoute = state.Route;
		if (activeRoute == null)
		{
			string text = havePathIndex || routeWorkDeferred ? "Waiting for route" : "Path component unavailable";
			SetAction(state, headVehicle.Entity, headID, LocustActionCode.Stuck, text);
			driveCommand = RailAutomationDriveCommand.ServiceBrake;
			return true;
		}

		exactTurns = activeRoute.TurnPlan;

		// A target that was physically invalidated is not considered an arrival until a
		// replacement route has rebound the timetable leg to a live station.
		if (!state.PinnedTargetInvalidated && activeRoute.IsAtTarget(RailGraphSystem.Graph, cursor))
		{
			if (state.ArrivedStationAtMS == 0)
			{
				state.ArrivedStationAtMS = nowMS;
				if (entry.AnnounceOnArrival) TryBlowAutomationWhistle(leadVehicle);
			}

			SetAction(state, headVehicle.Entity, headID, LocustActionCode.WaitingAtStation, "Waiting at station");
			driveCommand = RailAutomationDriveCommand.Hold;

			if (nowMS >= state.NextStationCriteriaCheckMS)
			{
				state.NextStationCriteriaCheckMS = nowMS + StationCriteriaCheckMS;
				if (StationCriteriaSatisfied(entry, leadVehicle, nowMS - state.ArrivedStationAtMS))
				{
					if (entry.AnnounceOnDeparture) TryBlowAutomationWhistle(leadVehicle);
					int nextIndex = (targetIndex + 1) % timetableDocument.Route.Count;
					SaveNextRouteIndex(owner, timetableDocument, nextIndex);
					ResetRoute(state);
				}
			}

			return true;
		}

		bool wasBoundaryBlocked = activeRoute.TurnPlan.BoundaryBlocked;
		bool wasPathBlocked = activeRoute.TurnPlan.PathBlocked;
		activeRoute.TurnPlan.ResetSignalBoundaryStatus();

		if (wasBoundaryBlocked) { SetAction(state, headVehicle.Entity, headID, LocustActionCode.WaitingForSignal, "Waiting for signal"); }
		else if (wasPathBlocked && state.RefreshRequested) { SetAction(state, headVehicle.Entity, headID, LocustActionCode.Stuck, "Waiting for route"); }
		else { SetAction(state, headVehicle.Entity, headID, LocustActionCode.Going, "Going to " + activeRoute.Target.DisplayName); }

		// The active route remains executable while a replacement is being built.
		// If its changed portion is reached first, automated movement marks PathBlocked and holds at that boundary until a replacement is installed.
		// Signal waits deliberately keep probing so authority can be re-evaluated when the downstream revision changes.
		driveCommand = wasPathBlocked && state.RefreshRequested ? RailAutomationDriveCommand.Hold : RailAutomationDriveCommand.DriveForward;
		return true;
	}

	private bool TryPrepareTargetCandidates
	(
		RailPathIndex activePathIndex, ConvoyAutomationState state, TimetableRouteEntryPacket entry,
		byte gauge, int dimension, bool preservePinnedTarget, bool expandStationGroup, out bool isStationGroup
	)
	{
		state.StationCandidates.Clear();
		state.TargetCandidates.Clear();
		isStationGroup = false;

		if (activePathIndex == null || RailGraphSystem == null || StationRegistry == null) return false;

		bool havePinnedTarget = false;
		if
		(
			preservePinnedTarget && StationRegistry.TryGet(state.Target.StationKey, out RailStationRegistrySystem.RailStationEntry pinned) &&
			pinned.HasStopEndpoint && pinned.Gauge == gauge && pinned.Key.Dimension == dimension &&
			activePathIndex.TryResolveStationTarget(RailGraphSystem.Graph, pinned, out StationPathTarget pinnedTarget)
		)
		{
			state.TargetCandidates.Add(pinnedTarget);
			isStationGroup = state.CurrentDestinationIsStationGroup;
			havePinnedTarget = true;

			// A normal refresh keeps the timetable leg pinned to its already-selected physical stop.
			// A chain-signal reroute is the deliberate exception, it may choose another currently valid platform from the same station group.
			if (!expandStationGroup) return true;
		}

		if (!TryGetStationCandidates(entry, gauge, dimension, state.StationCandidates, out bool registryStationGroup)) return havePinnedTarget;

		isStationGroup |= registryStationGroup;
		activePathIndex.CollectStationTargets(RailGraphSystem.Graph, state.StationCandidates, state.TargetCandidates);
		if (state.TargetCandidates.Count > 1) isStationGroup = true;

		return state.TargetCandidates.Count > 0;
	}

	private void SetRoute(ConvoyAutomationState state, RailAutomationRoute route, int targetRouteIndex, StationPathTarget target, bool isStationGroup)
	{
		RemoveRouteIndex(state);
		state.SetRoute(route, targetRouteIndex, target, isStationGroup, state.CachedTimetableChangeSerial);
		route.CollectEdgeHashes(state.IndexedRouteEdges);

		foreach (ulong edgeHash in state.IndexedRouteEdges)
		{
			if (!RouteOwnersByEdge.TryGetValue(edgeHash, out HashSet<long>? owners))
			{
				owners = new HashSet<long>();
				RouteOwnersByEdge[edgeHash] = owners;
			}
			owners.Add(state.HeadID);
		}

		if (!RouteOwnersByStation.TryGetValue(target.StationKey, out HashSet<long>? stationOwners))
		{
			stationOwners = new HashSet<long>();
			RouteOwnersByStation[target.StationKey] = stationOwners;
		}
		stationOwners.Add(state.HeadID);
	}

	private void ResetRoute(ConvoyAutomationState state)
	{
		RemoveRouteIndex(state);
		state.ResetRoute();
	}

	private bool RemoveState(long headID)
	{
		if (!AutomationStates.Remove(headID, out ConvoyAutomationState? state)) return false;
		RemoveRouteIndex(state);
		return true;
	}

	private void RemoveRouteIndex(ConvoyAutomationState state)
	{
		foreach (ulong edgeHash in state.IndexedRouteEdges)
		{
			if (!RouteOwnersByEdge.TryGetValue(edgeHash, out HashSet<long>? owners)) continue;
			owners.Remove(state.HeadID);
			if (owners.Count == 0) RouteOwnersByEdge.Remove(edgeHash);
		}
		state.IndexedRouteEdges.Clear();

		if (state.Route != null && RouteOwnersByStation.TryGetValue(state.Target.StationKey, out HashSet<long>? stationOwners))
		{
			stationOwners.Remove(state.HeadID);
			if (stationOwners.Count == 0) RouteOwnersByStation.Remove(state.Target.StationKey);
		}
	}

	private ConvoyAutomationState GetState(long headID)
	{
		if (!AutomationStates.TryGetValue(headID, out ConvoyAutomationState? state))
		{
			state = new ConvoyAutomationState(headID);
			AutomationStates[headID] = state;
		}

		return state;
	}

	private static ConductorTimetableDocument GetCachedTimetable(Entity owner, ConvoyAutomationState state)
	{
		int changeSerial = ConductorTimetableStorage.GetChangeSerial(owner);
		long ownerID = owner.EntityId;

		if (state.CachedTimetable == null || state.CachedTimetableOwnerID != ownerID || state.CachedTimetableChangeSerial != changeSerial)
		{
			state.CachedTimetable = ConductorTimetableStorage.Load(owner);
			state.CachedTimetableOwnerID = ownerID;
			state.CachedTimetableChangeSerial = changeSerial;
		}

		return state.CachedTimetable;
	}

	private void EnsureGlobalPathIndex()
	{
		if (RailGraphSystem == null) return;

		int graphVersion = RailGraphSystem.GraphBuildVersion;
		if (GlobalPathIndex != null && GlobalIndexedGraphVersion == graphVersion) return;

		GlobalPathIndex = RailPathIndex.Build(RailGraphSystem.Graph);
		GlobalIndexedGraphVersion = graphVersion;
	}

	// Used by the rare, explicitly synchronous timetable minimap request.
	internal bool TryGetPathIndex(out RailPathIndex? index)
	{
		EnsureGlobalPathIndex();
		index = GlobalPathIndex;
		return index != null;
	}

	internal bool TryGetPathIndexForEdge(ulong edgeHash, out RailPathIndex? index, out int graphVersion)
	{
		index = null;
		graphVersion = RailGraphSystem?.GraphBuildVersion ?? -1;
		if (RailGraphSystem == null || edgeHash == 0 || !RailGraphSystem.Graph.ContainsEdge(edgeHash)) return false;

		if (ComponentIndexByEdge.TryGetValue(edgeHash, out ComponentIndexEntry? cached)) { index = cached.Index; return true; }

		if (ComponentBuildJobByEdge.TryGetValue(edgeHash, out RailPathIndex.ComponentBuildJob? existingJob))
		{
			if (!existingJob.Failed) return false;
			RemoveComponentBuildJob(existingJob);
		}

		if (!RailPathIndex.TryCreateComponentBuildJob(RailGraphSystem.Graph, edgeHash, out RailPathIndex.ComponentBuildJob? created) || created == null)
		{
			return false;
		}

		created.SchedulerSequence = ++NextComponentBuildSequence;
		ComponentBuildJobs.Add(created);
		ComponentBuildJobByEdge[edgeHash] = created;
		return false;
	}

	private void ProcessComponentBuildJobs(float deltaTime)
	{
		if (RailGraphSystem == null || ComponentBuildJobs.Count == 0) return;
		int remaining = ComponentBuildWorkPerTick;
		int visitsRemaining = Math.Max(1, ComponentBuildJobs.Count * 2);

		while (remaining > 0 && ComponentBuildJobs.Count != 0 && visitsRemaining-- > 0)
		{
			if (ComponentBuildRoundRobin >= ComponentBuildJobs.Count) ComponentBuildRoundRobin = 0;
			RailPathIndex.ComponentBuildJob steppedJob = ComponentBuildJobs[ComponentBuildRoundRobin];
			int used = steppedJob.Step(Math.Min(ComponentBuildSlice, remaining));
			remaining -= Math.Max(1, used);

			RailPathIndex.ComponentBuildJob survivor = RegisterDiscoveredEdgesAndMerge(steppedJob);

			if (survivor.TryGetResult(out RailPathIndex built))
			{
				InstallComponentIndex(built);
				RemoveComponentBuildJob(survivor);
				continue;
			}
			if (survivor.Failed)
			{
				RemoveComponentBuildJob(survivor);
				continue;
			}

			int survivorIndex = ComponentBuildJobs.IndexOf(survivor);
			ComponentBuildRoundRobin = survivorIndex < 0 ? 0 : survivorIndex + 1;
		}

		ComponentBuildEdgeScratch.Clear();
	}

	private RailPathIndex.ComponentBuildJob RegisterDiscoveredEdgesAndMerge(RailPathIndex.ComponentBuildJob job)
	{
		ComponentBuildEdgeScratch.Clear();
		job.DrainNewlyDiscoveredEdges(ComponentBuildEdgeScratch);
		RailPathIndex.ComponentBuildJob survivor = job;

		for (int edgeIndex = 0; edgeIndex < ComponentBuildEdgeScratch.Count; edgeIndex++)
		{
			ulong edgeHash = ComponentBuildEdgeScratch[edgeIndex];
			if (ComponentBuildJobByEdge.TryGetValue( edgeHash, out RailPathIndex.ComponentBuildJob? existing) && !ReferenceEquals(existing, survivor))
			{
				survivor = MergeComponentBuildJobs(survivor, existing);
			}
			ComponentBuildJobByEdge[edgeHash] = survivor;
		}

		return survivor;
	}

	private RailPathIndex.ComponentBuildJob MergeComponentBuildJobs(RailPathIndex.ComponentBuildJob first, RailPathIndex.ComponentBuildJob second)
	{
		if (ReferenceEquals(first, second)) return first;

		RailPathIndex.ComponentBuildJob winner;
		RailPathIndex.ComponentBuildJob loser;
		int currentGraphVersion = RailGraphSystem?.GraphBuildVersion ?? -1;
		bool firstMatchesCurrentGraph = first.GraphVersion == currentGraphVersion;
		bool secondMatchesCurrentGraph = second.GraphVersion == currentGraphVersion;

		if (firstMatchesCurrentGraph != secondMatchesCurrentGraph)
		{
			winner = firstMatchesCurrentGraph ? first : second;
			loser = ReferenceEquals(winner, first) ? second : first;
		}
		else if (first.Failed != second.Failed)
		{
			winner = first.Failed ? second : first;
			loser = ReferenceEquals(winner, first) ? second : first;
		}
		else if (first.IsComplete != second.IsComplete)
		{
			winner = first.IsComplete ? first : second;
			loser = ReferenceEquals(winner, first) ? second : first;
		}
		else if (first.DiscoveryComplete != second.DiscoveryComplete)
		{
			winner = first.DiscoveryComplete ? first : second;
			loser = ReferenceEquals(winner, first) ? second : first;
		}
		else if (first.DiscoveryComplete && first.BuildProgressRank != second.BuildProgressRank)
		{
			winner = first.BuildProgressRank > second.BuildProgressRank ? first : second;
			loser = ReferenceEquals(winner, first) ? second : first;
		}
		else if (first.DiscoveredEdges.Count != second.DiscoveredEdges.Count)
		{
			// Keep the larger discovery state so merging copies the smaller set and minimizes frontier/hash-table work on very large connected networks.
			winner = first.DiscoveredEdges.Count > second.DiscoveredEdges.Count ? first : second;
			loser = ReferenceEquals(winner, first) ? second : first;
		}
		else
		{
			winner = first.SchedulerSequence <= second.SchedulerSequence ? first : second;
			loser = ReferenceEquals(winner, first) ? second : first;
		}

		winner.AbsorbDiscoveryFrom(loser);
		RemoveComponentBuildJob(loser);

		// Absorbed edges are claimed immediately, including the losing seed and frontier.
		// A request can therefore never start a third traversal while the surviving job continues through the shared component.
		foreach (ulong edgeHash in loser.DiscoveredEdges) ComponentBuildJobByEdge[edgeHash] = winner;

		return winner;
	}

	private void InstallComponentIndex(RailPathIndex built)
	{
		ComponentIndexEntry entry = new(built);
		ComponentIndexes.Add(entry);
		foreach (ulong indexedEdge in built.IndexedEdges)
		{
			if (ComponentIndexByEdge.TryGetValue(indexedEdge, out ComponentIndexEntry? existingIndexEntry) && !ReferenceEquals(existingIndexEntry, entry))
			{
				RemoveComponentIndex(existingIndexEntry);
			}
			ComponentIndexByEdge[indexedEdge] = entry;
		}
	}

	private void RemoveComponentBuildJob(RailPathIndex.ComponentBuildJob job)
	{
		int index = ComponentBuildJobs.IndexOf(job);
		if (index >= 0)
		{
			ComponentBuildJobs.RemoveAt(index);
			if (ComponentBuildRoundRobin > index) ComponentBuildRoundRobin--;
			if (ComponentBuildRoundRobin >= ComponentBuildJobs.Count) ComponentBuildRoundRobin = 0;
		}
		foreach (ulong edgeHash in job.DiscoveredEdges)
		{
			if (ComponentBuildJobByEdge.TryGetValue(edgeHash, out RailPathIndex.ComponentBuildJob? mapped) && ReferenceEquals(mapped, job))
			{
				ComponentBuildJobByEdge.Remove(edgeHash);
			}
		}
		job.Cancel();
	}

	private void PromoteOrCancelComponentBuildJobs(RailGraphChangeSet change, int newGraphVersion)
	{
		for (int buildJobIndex = ComponentBuildJobs.Count - 1; buildJobIndex >= 0; buildJobIndex--)
		{
			RailPathIndex.ComponentBuildJob job = ComponentBuildJobs[buildJobIndex];
			if (!job.TryPromoteAcrossUnrelatedChange(change, newGraphVersion)) RemoveComponentBuildJob(job);
		}
	}

	private void CancelAllComponentBuildJobs()
	{
		for (int buildJobIndex = 0; buildJobIndex < ComponentBuildJobs.Count; buildJobIndex++) ComponentBuildJobs[buildJobIndex].Cancel();
		ComponentBuildJobs.Clear();
		ComponentBuildJobByEdge.Clear();
		ComponentBuildEdgeScratch.Clear();
		ComponentBuildRoundRobin = 0;
	}

	private void InvalidateTouchedComponentIndexes(RailGraphChangeSet change)
	{
		for (int componentIndex = ComponentIndexes.Count - 1; componentIndex >= 0; componentIndex--)
		{
			ComponentIndexEntry entry = ComponentIndexes[componentIndex];
			if (entry.Index.TouchesGraphChange(change)) RemoveComponentIndex(entry);
		}
	}

	private void RemoveComponentIndex(ComponentIndexEntry entry)
	{
		ComponentIndexes.Remove(entry);
		foreach (ulong edgeHash in entry.Index.IndexedEdges)
		{
			if (ComponentIndexByEdge.TryGetValue(edgeHash, out ComponentIndexEntry? mapped) && ReferenceEquals(mapped, entry)) { ComponentIndexByEdge.Remove(edgeHash); }
		}
	}


	private bool TryRerouteAtBlockedChainSignal(
		RailPathIndex activePathIndex, ConvoyAutomationState state, long headID,
		in RailwayVehicleShared.RailCursor cursor, ulong currentZoneID, int targetIndex, bool isStationGroup, long nowMS
	)
	{
		if (RailGraphSystem == null || activePathIndex == null || StationRegistry == null) return false;
		if (state.Route == null) return false;

		RailExactTurnPlan blockedPlan = state.Route.TurnPlan;
		if (!blockedPlan.BoundaryBlocked || blockedPlan.BoundaryBlockReason != SignalAuthorityResult.ChainDownstreamOccupied) return false;

		RailGraphLive.EndpointKey blockedEndpoint = blockedPlan.BlockedSignalEndpoint;
		ulong blockedFromEdge = blockedPlan.BlockedFromEdgeHash;

		ulong serial = RailGraphSystem.GetSignalAuthorityRevision(blockedEndpoint);
		if (state.LastChainRepathOccupancySerial == serial && state.LastChainRepathSignalEndpoint.Equals(blockedEndpoint) && state.LastChainRepathFromEdgeHash == blockedFromEdge)
		{
			return false;
		}

		if (!TrySpendPathSearchBudget(nowMS))
		{
			state.NextPathIndexRetryMS = nowMS + RouteWorkBudgetRetryDelayMS(headID);
			return false;
		}

		state.LastChainRepathOccupancySerial = serial;
		state.LastChainRepathSignalEndpoint = blockedEndpoint;
		state.LastChainRepathFromEdgeHash = blockedFromEdge;

		bool AcceptsBlockedChainCandidate(RailAutomationRoute candidate) { return CandidateCanPassBlockedChainSignal(headID, blockedEndpoint, blockedFromEdge, candidate); }

		if (!activePathIndex.TryFindRouteGreedy(RailGraphSystem.Graph, cursor, state.TargetCandidates, currentZoneID, out RailAutomationRoute candidate, AcceptsBlockedChainCandidate, MaxRejectedChainRouteCandidates))
		{
			return false;
		}

		SetRoute(state, candidate, targetIndex, candidate.Target, isStationGroup);
		return true;
	}

	private bool CandidateCanPassBlockedChainSignal(long headID, RailGraphLive.EndpointKey blockedEndpoint, ulong blockedFromEdge, RailAutomationRoute candidate)
	{
		if (RailGraphSystem == null || candidate == null) return false;
		if (!candidate.TurnPlan.TryGet(blockedFromEdge, blockedEndpoint, out ulong candidateToEdge)) return false;

		SignalAuthorityResult authority = RailGraphSystem.CheckSignalSectionAuthority(headID, blockedEndpoint, blockedFromEdge, candidateToEdge, candidate.TurnPlan);

		return authority == SignalAuthorityResult.Allowed;
	}

	internal bool TrySpendPathSearchBudget(long nowMS)
	{
		long tickKey = nowMS / 50;
		if (tickKey != BudgetTickKey)
		{
			BudgetTickKey = tickKey;
			SearchesThisBudgetTick = 0;
		}

		if (SearchesThisBudgetTick >= MaxPathSearchesPerServerTick) return false;
		SearchesThisBudgetTick++;
		return true;
	}

	private bool TryGetStationCandidates(TimetableRouteEntryPacket entry, byte gauge, int dimension, List<RailStationRegistrySystem.RailStationEntry> destination, out bool isStationGroup)
	{
		isStationGroup = false;
		if (destination == null) return false;

		destination.Clear();
		if (StationRegistry == null || entry == null) return false;

		if (entry.NameHash != 0)
		{
			StationRegistry.CollectStationsByNameHash(entry.NameHash, destination, gauge, dimension);
			if (destination.Count > 0) { isStationGroup = destination.Count > 1; return true; }
		}

		var key = new RailStationRegistrySystem.StationBlockKey(entry.X, entry.Y, entry.Z, entry.Dimension);
		if (StationRegistry.TryGet(key, out RailStationRegistrySystem.RailStationEntry station) && station.HasStopEndpoint && station.Gauge == gauge && station.Key.Dimension == dimension)
		{
			destination.Add(station);
			return true;
		}

		return false;
	}

	private static bool TryGetCursorDimension(RailGraphLive graph, in RailwayVehicleShared.RailCursor cursor, out int dimension)
	{
		dimension = 0;
		if (graph == null || cursor.SegmentHash == 0) return false;
		if (!graph.TryGetEdgeEndpoints(cursor.SegmentHash, out RailGraphLive.EndpointKey firstEndpoint, out _, out _)) return false;

		dimension = firstEndpoint.Dimension;
		return true;
	}

	private static void TryBlowAutomationWhistle(IRailwayConvoyVehicle leadVehicle) { leadVehicle?.SteamEngineBehaviour?.ServerTryPlayWhistle(); }

	private bool StationCriteriaSatisfied(TimetableRouteEntryPacket entry, IRailwayConvoyVehicle leadVehicle, long waitedMS)
	{
		if (entry == null) return true;
		if (waitedMS < Math.Max(0, entry.TimeElapsedSeconds) * 1000L) return false;

		int minutes = Math.Max(0, entry.MileageLeftMinutes);
		if (minutes <= 0) return true;

		EntityBehaviorSteamPowered? engine = leadVehicle.SteamEngineBehaviour;
		if (engine == null) return false;

		double requiredSeconds = minutes * 60.0;
		return engine.OfflineSimEstimateFuelSecondsRemaining() >= requiredSeconds && engine.OfflineSimEstimateWaterSecondsRemaining() >= requiredSeconds;
	}

	private static void SaveNextRouteIndex(Entity owner, ConductorTimetableDocument timetableDocument, int nextIndex)
	{
		TimetableRouteEntryPacket[] route = new TimetableRouteEntryPacket[timetableDocument.Route.Count];
		for (int routeIndex = 0; routeIndex < route.Length; routeIndex++) route[routeIndex] = timetableDocument.Route[routeIndex].Clone();
		ConductorTimetableStorage.Save(owner, route, nextIndex);
	}

	private bool HasConductorLocust(long headID, IRailwayConvoyVehicle headVehicle)
	{
		if (ConvoySystem != null && ConvoySystem.TryGetConvoyHasConductorLocust(headID, out bool hasLocust)) return hasLocust;
		return headVehicle.HasConductorLocustInstalled;
	}

	private static long GetHeadID(IRailwayConvoyVehicle vehicle)	{ return vehicle.ConvoyHeadEntityID != 0 ? vehicle.ConvoyHeadEntityID : vehicle.Entity.EntityId; }

	private static long RetryDelayMS(long headID)					{ return PathRetryBaseMS + Math.Abs(headID % 1000); }
	private static long PathIndexRetryDelayMS(long headID)			{ return PathIndexRetryBaseMS + Math.Abs(headID % 101); }
	private static long RouteWorkBudgetRetryDelayMS(long headID)	{ return RouteWorkBudgetRetryBaseMS + Math.Abs(headID % 51); }

	private void SetAction(ConvoyAutomationState state, Entity entity, long headID, LocustActionCode code, string text)
	{
		if (state.LastActionCode == code && string.Equals(state.LastActionText, text, StringComparison.Ordinal)) return;

		state.LastActionCode = code;
		state.LastActionText = text ?? "";

		SetEntityAction(entity, code, text);

		if (ConvoySystem != null && ConvoySystem.TryGetOrderedMemberIDs(headID, out List<long> members) && ServerAPI != null)
		{
			for (int memberIndex = 0; memberIndex < members.Count; memberIndex++)
			{
				if (members[memberIndex] == entity.EntityId) continue;
				Entity other = ServerAPI.World.GetEntityById(members[memberIndex]);
				if (other != null) SetEntityAction(other, code, text);
			}
		}
	}

	private void ClearActionForConvoy(long headID, Entity? fallback = null)
	{
		if (ConvoySystem != null && ConvoySystem.TryGetOrderedMemberIDs(headID, out List<long> members) && ServerAPI != null)
		{
			for (int memberIndex = 0; memberIndex < members.Count; memberIndex++)
			{
				Entity other = ServerAPI.World.GetEntityById(members[memberIndex]);
				if (other != null) ClearEntityAction(other);
			}
			return;
		}

		if (fallback != null) ClearEntityAction(fallback);
		else if (ServerAPI?.World.GetEntityById(headID) is Entity head) ClearEntityAction(head);
	}

	private static void ClearEntityAction(Entity entity)
	{
		if (entity == null) return;
		entity.WatchedAttributes.SetString(RailwayVehicleShared.AttributeLocustActionText, "");
		entity.WatchedAttributes.SetInt(RailwayVehicleShared.AttributeLocustActionCode, 0);
		entity.WatchedAttributes.MarkPathDirty(RailwayVehicleShared.AttributeLocustActionText);
		entity.WatchedAttributes.MarkPathDirty(RailwayVehicleShared.AttributeLocustActionCode);
	}

	private static void SetEntityAction(Entity entity, LocustActionCode code, string text)
	{
		if (entity == null) return;

		string oldText = entity.WatchedAttributes.GetString(RailwayVehicleShared.AttributeLocustActionText, "");
		int oldCode = entity.WatchedAttributes.GetInt(RailwayVehicleShared.AttributeLocustActionCode, 0);
		int newCode = (int)code;

		if (oldCode == newCode && string.Equals(oldText, text, StringComparison.Ordinal)) return;

		entity.WatchedAttributes.SetString(RailwayVehicleShared.AttributeLocustActionText, text ?? "");
		entity.WatchedAttributes.SetInt(RailwayVehicleShared.AttributeLocustActionCode, newCode);
		entity.WatchedAttributes.MarkPathDirty(RailwayVehicleShared.AttributeLocustActionText);
		entity.WatchedAttributes.MarkPathDirty(RailwayVehicleShared.AttributeLocustActionCode);
	}

	private enum LocustActionCode
	{
		None = 0,
		Going = 1,
		WaitingAtStation = 2,
		Stuck = 3,
		WaitingForSignal = 4,
		NoTimetable = 5
	}



	private sealed class ComponentIndexEntry
	{
		internal readonly RailPathIndex Index;

		internal ComponentIndexEntry(RailPathIndex index) { Index = index; }
	}

	private sealed class ConvoyAutomationState
	{
		public readonly long HeadID;

		public int TargetRouteIndex = -1;
		public int RouteTimetableChangeSerial = -1;
		public StationPathTarget Target;
		public bool CurrentDestinationIsStationGroup;

		// Route refresh is orthogonal to route execution. A live Route remains the movement lease until a replacement is successfully installed.
		public bool RefreshRequested;
		public bool PinnedTargetInvalidated;

		public readonly List<RailStationRegistrySystem.RailStationEntry> StationCandidates = new(8);
		public readonly List<StationPathTarget> TargetCandidates = new(8);

		public RailAutomationRoute? Route;
		public readonly HashSet<ulong> IndexedRouteEdges = new();

		public long NextPathRetryMS;
		public long NextPathIndexRetryMS;
		public long NextStationCriteriaCheckMS;
		public long ArrivedStationAtMS;

		public ulong LastChainRepathOccupancySerial;
		public RailGraphLive.EndpointKey LastChainRepathSignalEndpoint;
		public ulong LastChainRepathFromEdgeHash;

		public long CachedTimetableOwnerID;
		public int CachedTimetableChangeSerial = -1;
		public ConductorTimetableDocument? CachedTimetable;

		public LocustActionCode LastActionCode = LocustActionCode.None;
		public string LastActionText = "";

		public ConvoyAutomationState(long headID) { HeadID = headID; }

		public void SetRoute(RailAutomationRoute route, int targetRouteIndex, StationPathTarget target, bool isStationGroup, int timetableChangeSerial)
		{
			Route = route;
			TargetRouteIndex = targetRouteIndex;
			RouteTimetableChangeSerial = timetableChangeSerial;
			Target = target;
			CurrentDestinationIsStationGroup = isStationGroup;
			RefreshRequested = false;
			PinnedTargetInvalidated = false;
			ArrivedStationAtMS = 0;
			NextPathIndexRetryMS = 0;
			LastChainRepathOccupancySerial = 0;
			LastChainRepathSignalEndpoint = default;
			LastChainRepathFromEdgeHash = 0;
		}

		public void ResetRoute()
		{
			Route = null;
			TargetRouteIndex = -1;
			RouteTimetableChangeSerial = -1;
			Target = default;
			CurrentDestinationIsStationGroup = false;
			RefreshRequested = false;
			PinnedTargetInvalidated = false;
			ArrivedStationAtMS = 0;
			NextPathRetryMS = 0;
			NextPathIndexRetryMS = 0;
			LastChainRepathOccupancySerial = 0;
			LastChainRepathSignalEndpoint = default;
			LastChainRepathFromEdgeHash = 0;
		}
	}
}

/// Exact edge-hash turn decisions for automated convoys. | Hot-path lookup only, plans are rebuilt on-demand by RailAutomationPathingSystem.
internal sealed class RailExactTurnPlan
{
	private readonly Dictionary<TurnKey, ulong> Turns = new();
	private readonly HashSet<TurnKey> Stops = new();

	private bool HasAuthorityCache;
	private long CachedAuthorityOwnerID;
	private RailGraphLive.EndpointKey CachedAuthoritySignal;
	private ulong CachedAuthorityFromEdge;
	private ulong CachedAuthorityToEdge;
	private ulong CachedAuthorityRevision;
	private SignalAuthorityResult CachedAuthorityResult;

	internal bool BoundaryBlocked { get; private set; }
	internal SignalAuthorityResult BoundaryBlockReason { get; private set; }
	internal RailGraphLive.EndpointKey BlockedSignalEndpoint { get; private set; }
	internal ulong BlockedFromEdgeHash { get; private set; }
	internal ulong BlockedToEdgeHash { get; private set; }

	internal bool PathBlocked { get; private set; }
	internal RailGraphLive.EndpointKey BlockedPathEndpoint { get; private set; }
	internal ulong BlockedPathFromEdgeHash { get; private set; }

	internal int Count => Turns.Count + Stops.Count;

	internal void Clear()
	{
		Turns.Clear();
		Stops.Clear();
		ClearAuthorityCache();
		ResetSignalBoundaryStatus();
		PathBlocked = false;
		BlockedPathEndpoint = default;
		BlockedPathFromEdgeHash = 0;
	}

	internal void ResetSignalBoundaryStatus()
	{
		BoundaryBlocked = false;
		BoundaryBlockReason = SignalAuthorityResult.Allowed;
		BlockedSignalEndpoint = default;
		BlockedFromEdgeHash = 0;
		BlockedToEdgeHash = 0;
	}

	internal void MarkPathBlocked(RailGraphLive.EndpointKey endpoint, ulong fromEdgeHash)
	{
		PathBlocked = true;
		BlockedPathEndpoint = endpoint;
		BlockedPathFromEdgeHash = fromEdgeHash;
	}

	internal void MarkBoundaryBlocked(RailGraphLive.EndpointKey signalEndpoint, ulong fromEdgeHash, ulong toEdgeHash, SignalAuthorityResult reason)
	{
		BoundaryBlocked = true;
		BoundaryBlockReason = reason;
		BlockedSignalEndpoint = signalEndpoint;
		BlockedFromEdgeHash = fromEdgeHash;
		BlockedToEdgeHash = toEdgeHash;
	}

	internal void SetTurn(ulong fromEdgeHash, RailGraphLive.EndpointKey atEndpoint, ulong nextEdgeHash)
	{
		if (fromEdgeHash == 0 || nextEdgeHash == 0) return;
		Turns[new TurnKey(fromEdgeHash, atEndpoint)] = nextEdgeHash;
		ClearAuthorityCache();
	}

	internal void SetStop(ulong fromEdgeHash, RailGraphLive.EndpointKey atEndpoint)
	{
		if (fromEdgeHash == 0) return;
		Stops.Add(new TurnKey(fromEdgeHash, atEndpoint));
		ClearAuthorityCache();
	}

	internal bool ShouldStopAt(ulong fromEdgeHash, RailGraphLive.EndpointKey atEndpoint) { return Stops.Contains(new TurnKey(fromEdgeHash, atEndpoint)); }

	internal bool TryGet(ulong fromEdgeHash, RailGraphLive.EndpointKey atEndpoint, out ulong nextEdgeHash) { return Turns.TryGetValue(new TurnKey(fromEdgeHash, atEndpoint), out nextEdgeHash); }

	internal bool TryGetCachedAuthority(long ownerID, RailGraphLive.EndpointKey signalEndpoint, ulong fromEdgeHash, ulong toEdgeHash, ulong authorityRevision, out SignalAuthorityResult result)
	{
		if
		(
			HasAuthorityCache && CachedAuthorityOwnerID == ownerID && CachedAuthoritySignal.Equals(signalEndpoint) &&
			CachedAuthorityFromEdge == fromEdgeHash && CachedAuthorityToEdge == toEdgeHash && CachedAuthorityRevision == authorityRevision
		)
		{
			result = CachedAuthorityResult;
			return true;
		}

		result = SignalAuthorityResult.IncompleteRoute;
		return false;
	}

	internal void StoreCachedAuthority(long ownerID, RailGraphLive.EndpointKey signalEndpoint, ulong fromEdgeHash, ulong toEdgeHash, ulong authorityRevision, SignalAuthorityResult result)
	{
		HasAuthorityCache = true;
		CachedAuthorityOwnerID = ownerID;
		CachedAuthoritySignal = signalEndpoint;
		CachedAuthorityFromEdge = fromEdgeHash;
		CachedAuthorityToEdge = toEdgeHash;
		CachedAuthorityRevision = authorityRevision;
		CachedAuthorityResult = result;
	}

	private void ClearAuthorityCache()
	{
		HasAuthorityCache = false;
		CachedAuthorityOwnerID = 0;
		CachedAuthoritySignal = default;
		CachedAuthorityFromEdge = 0;
		CachedAuthorityToEdge = 0;
		CachedAuthorityRevision = 0;
		CachedAuthorityResult = SignalAuthorityResult.IncompleteRoute;
	}

	private readonly record struct TurnKey(ulong FromEdgeHash, RailGraphLive.EndpointKey Endpoint);
}

