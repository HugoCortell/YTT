using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace YangTransport;

internal sealed class RailConvoySystem : ModSystem
{
	private const double MaxAttachDistance = 4.0;
	private const double MaxAttachDistanceSQ = MaxAttachDistance * MaxAttachDistance;

	private ICoreServerAPI? ServerAPI;
	private RailGraphServerSystem? RailGraphSystem;
	private RailConvoyAuthoritySystem? AuthoritySystem;

	private readonly Dictionary<long, Convoy> Convoys = new();
	private readonly Dictionary<long, IRailwayConvoyVehicle> LoadedVehicles = new();
	private readonly List<ulong> BindingCandidatesScratch = new(256);
	private readonly List<IRailwayConvoyVehicle> LoadedMembersScratch = new(16);
	private readonly List<Convoy> ConvoySnapshotScratch = new(32);
	private readonly HashSet<long> ReseededOwnerScratch = new();
	private readonly HashSet<long> RejectedConvoyHeads = new();
	private readonly HashSet<ulong> SafetyFootprintScratch = new();
	private readonly List<IRailwayConvoyVehicle> SingleVehicleScratch = new(1);

	public override double ExecuteOrder() => 0.10;
	public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

	public override void Start(ICoreAPI coreAPI)
	{
		ServerAPI = coreAPI as ICoreServerAPI;
		if (ServerAPI == null) return;

		RailGraphSystem = ServerAPI.ModLoader.GetModSystem<RailGraphServerSystem>();
		AuthoritySystem = ServerAPI.ModLoader.GetModSystem<RailConvoyAuthoritySystem>();

		// Track entities as they stream in/out
		ServerAPI.Event.OnEntityLoaded += OnEntityLoaded;
		ServerAPI.Event.OnEntitySpawn += OnEntityLoaded;
		ServerAPI.Event.OnEntityDespawn += OnEntityDespawned;

		// Ensure an authoritative convoy rebuild before the first world tick
		ServerAPI.Event.ServerRunPhase(EnumServerRunPhase.RunGame, RebuildAllLoadedConvoys);
	}

	public override void Dispose()
	{
		if (ServerAPI != null)
		{
			ServerAPI.Event.OnEntityLoaded -= OnEntityLoaded;
			ServerAPI.Event.OnEntitySpawn -= OnEntityLoaded;
			ServerAPI.Event.OnEntityDespawn -= OnEntityDespawned;
		}

		base.Dispose();
	}

	private void OnEntityLoaded(Entity entity)
	{
		if (entity.State == EnumEntityState.Despawned) return;
		if (entity is IRailwayConvoyVehicle vehicle) Register(vehicle);
	}

	private void OnEntityDespawned(Entity entity, EntityDespawnData reasonData)
	{
		// Some engine-side despawn paths (or other mods) may pass a null despawn data.
		// Treat it as Unload to avoid splitting convoys on an unknown/partial despawn.
		EnumDespawnReason reason = reasonData?.Reason ?? EnumDespawnReason.Unload;
		if (entity is not IRailwayConvoyVehicle vehicle) return;
		if (!LoadedVehicles.TryGetValue(entity.EntityId, out IRailwayConvoyVehicle? registered) || !ReferenceEquals(registered, vehicle))
		{
			// Authority-rejected stale shells were never registered and must not mutate the current loaded convoy when the offscreen system removes them.
			return;
		}

		long headID = vehicle.ConvoyHeadEntityID;
		if (AuthoritySystem?.TryGetMaterialTopology(entity.EntityId, out MaterialConvoyTopology topology, out _) == true && topology.Members.Length > 1)
		{
			headID = topology.HeadID;
		}

		Unregister(vehicle, reason);
		LoadedVehicles.Remove(entity.EntityId);
		if (headID != 0 && Convoys.TryGetValue(headID, out Convoy? convoy))
		{
			InvalidateLoadedMembers(convoy);
			if (convoy.AuthoritativeTopology && !HasAnyLoadedMember(convoy)) Convoys.Remove(headID);
		}
	}

	private void RebuildAllLoadedConvoys()
	{
		if (ServerAPI == null) return;

		Convoys.Clear();
		LoadedVehicles.Clear();
		AuthoritySystem ??= ServerAPI.ModLoader.GetModSystem<RailConvoyAuthoritySystem>();

		foreach (Entity entity in ServerAPI.World.LoadedEntities.Values)
		{
			if (entity.State == EnumEntityState.Despawned || entity is not IRailwayConvoyVehicle vehicle) continue;
			if (AuthoritySystem?.IsLoadedMaterialAuthorityCurrent(entity) == false) continue;

			LoadedVehicles[entity.EntityId] = vehicle;
		}

		foreach (IRailwayConvoyVehicle vehicle in LoadedVehicles.Values)
		{
			if (TryRegisterFromAuthority(vehicle)) continue;

			long headID = vehicle.ConvoyHeadEntityID;
			if (headID == 0) continue;

			Convoy convoy = GetOrCreate(headID);
			convoy.ExpectedMemberCount = Math.Max(convoy.ExpectedMemberCount, vehicle.ExpectedConvoyMemberCount);
			if (!convoy.Members.Contains(vehicle.Entity.EntityId)) convoy.Members.Add(vehicle.Entity.EntityId);
			convoy.ExpectedMemberCount = Math.Max(convoy.ExpectedMemberCount, convoy.Members.Count);
		}

		foreach (Convoy convoy in Convoys.Values)
		{
			if (!convoy.AuthoritativeTopology) RebuildOrderedFromPersisted(convoy);

			RefreshStatisticsAndWatch(convoy);
			SeedLoadedConvoyRailSafetyState(convoy);
		}
	}

	internal bool ReseedLoadedConvoyRailSafetyState()
	{
		// Snapshot first because a definitively invalid loaded consist is derailed, which may rebuild convoy membership.
		// Runtime readiness remains closed only while partially streamed convoys are still waiting for adjacent members to load.
		bool allConclusive = true;
		ConvoySnapshotScratch.Clear();
		ConvoySnapshotScratch.AddRange(Convoys.Values);
		ReseededOwnerScratch.Clear();

		for (int iteration = 0; iteration < ConvoySnapshotScratch.Count; iteration++)
		{
			Convoy convoy = ConvoySnapshotScratch[iteration];
			IReadOnlyList<IRailwayConvoyVehicle> loaded = EnsureLoadedMembers(convoy);
			if (loaded.Count == 0) continue;
			if (!convoy.FullyLoaded)
			{
				// A partially streamed consist has neither an exact occupancy footprint nor a complete train-collision body.
				// Stream one chunk-column halo around every loaded member.
				// Once all of those columns are present, allow one additional tick for their entities to register.
				// A consist that is still incomplete is a persisted membership mismatch, not an unresolved streaming dependency.
				bool requestedAny = false;
				for (int memberIndex = 0; memberIndex < loaded.Count; memberIndex++)
				{
					if (RailGraphSystem?.RequestIncompleteConvoyChunkHalo(loaded[memberIndex].Entity.ServerPos) == true) requestedAny = true;
				}

				if (requestedAny)
				{
					convoy.IncompleteLoadedHaloReadyTicks = 0;
					allConclusive = false;
					continue;
				}

				if (++convoy.IncompleteLoadedHaloReadyTicks <= 1)
				{
					allConclusive = false;
					continue;
				}

				DerailMismatchedConvoy(convoy, loaded);
				continue;
			}
			IRailwayConvoyVehicle head = loaded[0];
			ReseededOwnerScratch.Add(head.Entity.EntityId);
			if (head.Derailed || SeedLoadedConvoyRailSafetyState(convoy)) continue;

			ServerAPI?.Logger.Warning("[yangtransport] Loaded rail owner {0} could not bind to the railgraph. Derailing.", head.Entity.EntityId);
			DerailInvalidSafetyOwner(head);
		}

		// Free-standing vehicles are not represented in the convoy dictionary.
		foreach (IRailwayConvoyVehicle vehicle in LoadedVehicles.Values)
		{
			if (vehicle.Derailed || vehicle.ConvoyHeadEntityID != 0 || ReseededOwnerScratch.Contains(vehicle.Entity.EntityId)) continue;

			SingleVehicleScratch.Clear();
			SingleVehicleScratch.Add(vehicle);
			if (SeedAndPublishRailSafetyState(vehicle, SingleVehicleScratch, null)) continue;

			ServerAPI?.Logger.Warning("[yangtransport] Loaded rail vehicle {0} could not bind to the railgraph. Derailing.", vehicle.Entity.EntityId);
			DerailInvalidSafetyOwner(vehicle);
		}

		SingleVehicleScratch.Clear();
		ReseededOwnerScratch.Clear();
		ConvoySnapshotScratch.Clear();
		return allConclusive;
	}

	internal bool TrySeedLoadedConvoyRailSafetyState(long headID) { return Convoys.TryGetValue(headID, out var convoy) && SeedLoadedConvoyRailSafetyState(convoy); }

	private bool SeedLoadedConvoyRailSafetyState(Convoy convoy)
	{
		IReadOnlyList<IRailwayConvoyVehicle> loaded = EnsureLoadedMembers(convoy);
		if (!convoy.FullyLoaded || loaded.Count == 0) return false;
		if (loaded[0].Entity.EntityId != convoy.HeadID) return false;

		IReadOnlyDictionary<long, double>? physicalDistances = loaded.Count > 1 ? TryMeasurePhysicalDistances(loaded) : null;
		return SeedAndPublishRailSafetyState(loaded[0], loaded, physicalDistances);
	}

	private bool SeedAndPublishRailSafetyState
	(
		IRailwayConvoyVehicle owner,
		IReadOnlyList<IRailwayConvoyVehicle> ordered,
		IReadOnlyDictionary<long, double>? physicalDistances
	)
	{
		if (RailGraphSystem == null || owner == null || owner.Derailed) return false;
		if (!owner.ServerSeedConvoyRailStateFromOrderedConvoy(ordered, physicalDistances)) return false;

		return owner.Entity switch
		{
			EntityMinecart cart => cart.TryPublishRailOccupancyFootprint(RailGraphSystem, RailGraphSystem.Graph, force: true),
			EntityStandardGaugeLocomotive standardGaugeVehicle => standardGaugeVehicle.TryPublishRailOccupancyFootprint(RailGraphSystem, RailGraphSystem.Graph, force: true),
			_ => false
		};
	}


	private void DerailMismatchedConvoy(Convoy convoy, IReadOnlyList<IRailwayConvoyVehicle> loaded)
	{
		List<IRailwayConvoyVehicle> invalidMembers = new(loaded);
		ServerAPI?.Logger.Error
		(
			"[yangtransport] Convoy {0} remained incomplete after all adjacent chunk columns loaded " +
			"(loaded={1}, registered={2}, expected={3}). Removing the invalid convoy membership and derailing its loaded vehicles.",
			convoy.HeadID,
			invalidMembers.Count,
			convoy.Members.Count,
			convoy.ExpectedMemberCount
		);

		// Reject this persisted owner for the rest of the server session.
		// If a stale unloaded member streams in later, Register() clears and derails it instead of recreating the invalid logical convoy.
		RejectedConvoyHeads.Add(convoy.HeadID);

		// Delete the invalid logical convoy first so each surviving loaded vehicle can be derailed independently even when the persisted head itself is the missing member.
		Convoys.Remove(convoy.HeadID);
		RailGraphSystem?.ReleaseOccupancyOwner(convoy.HeadID);
		for (int iteration = 0; iteration < invalidMembers.Count; iteration++) invalidMembers[iteration].ServerClearConvoyState();
		for (int iteration = 0; iteration < invalidMembers.Count; iteration++) DerailInvalidSafetyOwner(invalidMembers[iteration]);
	}

	private static void DerailInvalidSafetyOwner(IRailwayConvoyVehicle owner)
	{
		switch (owner.Entity)
		{
			case EntityMinecart cart:
				cart.ServerDerailForInvalidTrack();
			break;

			case EntityStandardGaugeLocomotive standardGaugeVehicle:
				standardGaugeVehicle.ServerDerailForInvalidTrack();
			break;
		}
	}

	internal sealed class Convoy
	{
		public long HeadID;
		public readonly List<long> Members = new(8); // canonical ordered ids, loaded subset is cached below
		public bool AuthoritativeTopology;
		public bool AuthorityApplied;
		public int ExpectedMemberCount = 1;
		public int WeightSum;
		public long LeadID; // Vehicle that provides drive/turn inputs (engine cart/loco if present)
		public double TailDistance;
		public double OccupancyRearDistance;
		public bool HasConductorLocust;

		// Canonical loaded-member snapshot. MembershipRevision changes only when the ordered ID set or loaded entity registry changes.
		public long MembershipRevision = 1;
		public long LoadedMembersRevision;
		public readonly List<IRailwayConvoyVehicle> LoadedMembers = new(8);
		public bool FullyLoaded;
		public int IncompleteLoadedHaloReadyTicks;
	}

	public void Register(IRailwayConvoyVehicle vehicle)
	{
		if (ServerAPI == null || vehicle == null || vehicle.Entity == null || vehicle.Entity.State == EnumEntityState.Despawned) return;

		AuthoritySystem ??= ServerAPI.ModLoader.GetModSystem<RailConvoyAuthoritySystem>();
		if (AuthoritySystem?.IsLoadedMaterialAuthorityCurrent(vehicle.Entity) == false) return;

		long entityID = vehicle.Entity.EntityId;
		bool loadedRegistryChanged =
			!LoadedVehicles.TryGetValue(entityID, out IRailwayConvoyVehicle? registeredVehicle) ||
			!ReferenceEquals(registeredVehicle, vehicle);

		LoadedVehicles[entityID] = vehicle;

		if (TryRegisterFromAuthority(vehicle, loadedRegistryChanged)) return;

		long headID = vehicle.ConvoyHeadEntityID;
		if (headID == 0) return;
		if (RejectedConvoyHeads.Contains(headID))
		{
			ServerAPI.Logger.Error
			(
				"[yangtransport] Vehicle {0} loaded with rejected convoy owner {1}; clearing the stale membership and derailing it.",
				vehicle.Entity.EntityId, headID
			);
			vehicle.ServerClearConvoyState();
			DerailInvalidSafetyOwner(vehicle);
			return;
		}

		Convoy convoy = GetOrCreate(headID);
		convoy.ExpectedMemberCount = Math.Max(convoy.ExpectedMemberCount, vehicle.ExpectedConvoyMemberCount);
		if (!convoy.Members.Contains(vehicle.Entity.EntityId)) convoy.Members.Add(vehicle.Entity.EntityId);
		convoy.ExpectedMemberCount = Math.Max(convoy.ExpectedMemberCount, convoy.Members.Count);

		RebuildOrderedFromPersisted(convoy);
		RefreshStatisticsAndWatch(convoy);
		SeedLoadedConvoyRailSafetyState(convoy);
	}

	private bool TryRegisterFromAuthority(IRailwayConvoyVehicle vehicle, bool loadedRegistryChanged = false)
	{
		long entityID = vehicle.Entity.EntityId;
		if (AuthoritySystem?.TryGetMaterialTopology(entityID, out MaterialConvoyTopology topology, out _) != true)
		{
			if
			(
				AuthoritySystem?.TryGetAuthority(entityID, out RailEntityAuthority authority) == true &&
				authority.Mode == RailEntityAuthorityMode.Material && authority.HeadID == entityID && authority.ConvoyIndex == -1
			)
			{
				if (vehicle.ConvoyHeadEntityID != 0) vehicle.ServerClearConvoyState();
				return true;
			}
			
			return false;
		}

		long headID = topology.HeadID;
		if (RejectedConvoyHeads.Contains(headID))
		{
			vehicle.ServerClearConvoyState();
			DerailInvalidSafetyOwner(vehicle);
			return true;
		}

		Convoy convoy = GetOrCreate(headID);
		convoy.AuthoritativeTopology = true;
		convoy.ExpectedMemberCount = topology.Members.Length;

		bool membershipChanged = convoy.Members.Count != topology.Members.Length;
		if (!membershipChanged)
		{
			for (int iteration = 0; iteration < topology.Members.Length; iteration++)
			{
				if (convoy.Members[iteration] != topology.Members[iteration]) { membershipChanged = true; break; }
			}
		}

		if (membershipChanged)
		{
			convoy.Members.Clear();
			convoy.Members.AddRange(topology.Members);
		}

		if (membershipChanged || loadedRegistryChanged)
		{
			InvalidateLoadedMembers(convoy);
		}

		IReadOnlyList<IRailwayConvoyVehicle> loaded = EnsureLoadedMembers(convoy);
		if (convoy.FullyLoaded && !convoy.AuthorityApplied)
		{
			var ordered = new List<IRailwayConvoyVehicle>(loaded);
			ApplyConvoy(ordered, ScanConductorLocust(ordered), seedSafetyState: true, seedPhysicalDistanceFromHead: TryMeasurePhysicalDistances(ordered));
			convoy.AuthorityApplied = true;
		}

		RefreshStatisticsAndWatch(convoy);
		SeedLoadedConvoyRailSafetyState(convoy);
		return true;
	}

	public void Unregister(IRailwayConvoyVehicle vehicle, EnumDespawnReason reason)
	{
		if (ServerAPI == null) return;

		long headID = vehicle.ConvoyHeadEntityID;
		if (AuthoritySystem?.TryGetMaterialTopology(vehicle.Entity.EntityId, out MaterialConvoyTopology topology, out _) == true && topology.Members.Length > 1)
		{
			headID = topology.HeadID;
		}
		if (headID == 0) return;

		if (!Convoys.TryGetValue(headID, out var convoy)) return;

		if (reason == EnumDespawnReason.Unload)
		{
			if (convoy.AuthoritativeTopology) { InvalidateLoadedMembers(convoy); return; }

			convoy.ExpectedMemberCount = Math.Max(convoy.ExpectedMemberCount, Math.Max(convoy.Members.Count, vehicle.ExpectedConvoyMemberCount));
			convoy.Members.Remove(vehicle.Entity.EntityId);
			InvalidateLoadedMembers(convoy);
			if (convoy.Members.Count == 0) Convoys.Remove(headID);
			return;
		}

		TryDetachVehicleTransactional(vehicle, keepDetachedOwner: false);
	}

	public void NotifyVehicleWeightChanged(IRailwayConvoyVehicle vehicle)
	{
		long headID = vehicle.ConvoyHeadEntityID;
		if (headID == 0) return;
		if (Convoys.TryGetValue(headID, out var convoy)) RefreshStatisticsAndWatch(convoy);
	}

	public void NotifyCartWeightChanged(EntityMinecart cart) => NotifyVehicleWeightChanged(cart);

	public bool TryGetConvoyStatistics(long headID, out int count, out int weight, out long tailID)
	{
		return TryGetConvoyStats(headID, out count, out weight, out tailID, out _);
	}

	public bool TryGetConvoyStats(long headID, out int count, out int weight, out long tailID, out double tailDistance)
	{
		count = 1; weight = 1; tailID = headID; tailDistance = 0;
		if (!Convoys.TryGetValue(headID, out var convoy)) return false;
		if (convoy.Members.Count == 0) return false;

		count = convoy.Members.Count;
		weight = convoy.WeightSum;
		tailID = convoy.Members[count - 1];
		tailDistance = convoy.TailDistance;
		return true;
	}

	public bool TryGetLeadID(long headID, out long leadID)
	{
		leadID = headID;
		if (!Convoys.TryGetValue(headID, out var convoy)) return false;
		if (convoy.Members.Count == 0) return false;

		leadID = convoy.LeadID != 0 ? convoy.LeadID : headID;
		return true;
	}

	public bool TryGetOrderedMemberIDs(long headID, out List<long> memberIDs)
	{
		if (Convoys.TryGetValue(headID, out var convoy) && convoy.Members.Count > 0) { memberIDs = convoy.Members; return true; }

		memberIDs = null!;
		return false;
	}

	internal bool TryGetLoadedVehicle(long entityID, out IRailwayConvoyVehicle vehicle) { return LoadedVehicles.TryGetValue(entityID, out vehicle); }

	internal bool TryGetLoadedOrderedMembers(long headID, List<IRailwayConvoyVehicle> destination)
	{
		if (destination == null) return false;
		destination.Clear();
		if (!Convoys.TryGetValue(headID, out Convoy? convoy)) return false;

		IReadOnlyList<IRailwayConvoyVehicle> loaded = EnsureLoadedMembers(convoy);
		for (int iteration = 0; iteration < loaded.Count; iteration++) destination.Add(loaded[iteration]);
		return destination.Count > 0;
	}

	internal bool TryGetLoadedOrderedMembers(long headID, out IReadOnlyList<IRailwayConvoyVehicle> members, bool requireFullyLoaded = false)
	{
		members = Array.Empty<IRailwayConvoyVehicle>();
		if (!Convoys.TryGetValue(headID, out Convoy? convoy)) return false;
		members = EnsureLoadedMembers(convoy);
		return members.Count > 0 && (!requireFullyLoaded || convoy.FullyLoaded);
	}

	internal bool IsConvoyFullyLoaded(long headID)
	{
		if (!Convoys.TryGetValue(headID, out Convoy? convoy)) return false;
		EnsureLoadedMembers(convoy);
		return convoy.FullyLoaded;
	}

	public bool TryGetConvoyHasConductorLocust(long headID, out bool hasConductorLocust)
	{
		hasConductorLocust = false;

		if (Convoys.TryGetValue(headID, out var convoy))			{ hasConductorLocust = convoy.HasConductorLocust; return true; }
		if (GetVehicle(headID) is IRailwayConvoyVehicle vehicle)	{ hasConductorLocust = vehicle.HasConductorLocustInstalled; return true; }

		return false;
	}

	public void NotifyConductorLocustInstalled(Entity attachedTo)
	{
		if (attachedTo is not IRailwayConvoyVehicle vehicle) return;

		long headID = vehicle.ConvoyHeadEntityID != 0 ? vehicle.ConvoyHeadEntityID : vehicle.Entity.EntityId;
		GetOrCreate(headID).HasConductorLocust = true;
	}

	public void NotifyConductorLocustRemoved(Entity removedFrom)
	{
		if (removedFrom is not IRailwayConvoyVehicle vehicle) return;

		long headID = vehicle.ConvoyHeadEntityID != 0 ? vehicle.ConvoyHeadEntityID : vehicle.Entity.EntityId;

		ServerAPI?.ModLoader.GetModSystem<RailAutomationPathingSystem>()?.ReleaseConvoy(headID);

		// SG locusts can only live on the head/conductor seat right now, so removal is a straight false.
		if (vehicle.TrackGauge == 1)
		{
			if (Convoys.TryGetValue(headID, out var standardGaugeConvoy)) standardGaugeConvoy.HasConductorLocust = false;
			return;
		}

		RefreshConductorLocustForConvoy(headID, excludeEntityID: vehicle.Entity.EntityId);
	} // Reverse movement is signed rail-tape motion, convoy order is never reversed for movement.

	public void ForceFree(IRailwayConvoyVehicle vehicle) // Force-detach a vehicle from its convoy (used for derail/break). The removed vehicle becomes free.
	{
		if (ServerAPI == null) return;
		TryDetachVehicleTransactional(vehicle);
	}

	public bool TryMerge(long selectedAnchorEntityID, IRailwayConvoyVehicle targetClicked, IServerPlayer player)
	{
		if (RailGraphSystem == null || ServerAPI == null)
		{
			if (ServerAPI != null) { ServerAPI.Logger.Error("[YangTransport] Linkage pole merge failed because RailGraphServerSystem is unavailable."); }
			player.SendIngameError("yangtransport:linkagepole-railgraph-unavailable");
			return false;
		}

		if (ServerAPI.World.GetEntityById(selectedAnchorEntityID) is not IRailwayConvoyVehicle sourceAny)
		{
			player.SendIngameError("yangtransport:linkagepole-missing-members");
			return false;
		}

		if (sourceAny.TrackGauge != targetClicked.TrackGauge)
		{
			player.SendIngameError("yangtransport:linkagepole-gauge-mismatch");
			return false;
		}

		if (sourceAny.Derailed || targetClicked.Derailed)
		{
			player.SendIngameError("yangtransport:linkagepole-derailed");
			return false;
		}

		CollectConvoy(sourceAny, out var sourceHead, out var sourceMembers);
		CollectConvoy(targetClicked, out var destinationHead, out var destinationMembers);

		long oldSourceHeadID = sourceHead.ConvoyHeadEntityID;
		long oldDestinationHeadID = destinationHead.ConvoyHeadEntityID;

		if (sourceMembers.Count == 0 || destinationMembers.Count == 0) return false;
		if (sourceHead.Entity.EntityId == destinationHead.Entity.EntityId && sourceHead.ConvoyHeadEntityID != 0) return false;

		if 
		(
			!RailConvoyOrdering.TryResolveClosestExposedEnds
			(
				sourceMembers, destinationMembers,MaxAttachDistanceSQ,
				out RailConvoyOrdering.ConvoyEnd sourceAttachEnd, out RailConvoyOrdering.ConvoyEnd destinationAttachEnd
			)
		)
		{
			player.SendIngameError("yangtransport:linkagepole-too-far", null, MaxAttachDistance.ToString("0"));
			return false;
		}

		HashSet<long> entityIDs = new();
		List<IRailwayConvoyVehicle> members = new(sourceMembers.Count + destinationMembers.Count);
		RailConvoyOrdering.AddUnique(sourceMembers, entityIDs, members);
		int sourceMemberCount = members.Count;
		RailConvoyOrdering.AddUnique(destinationMembers, entityIDs, members);

		IRailwayConvoyVehicle? engine = null;
		bool engineIsInSourceConvoy = false;

		for (int iteration = 0; iteration < members.Count; iteration++)
		{
			if (!members[iteration].HasTractionEngine) continue;
			if (engine != null)
			{
				player.SendIngameError("yangtransport:linkagepole-engine-limit");
				return false;
			}

			engine = members[iteration];
			engineIsInSourceConvoy = iteration < sourceMemberCount;
		}

		// SG traction engines must remain the physical front of their consist. Minecarts retain bidirectional end linking.
		if (engine?.TrackGauge == 1)
		{
			RailConvoyOrdering.ConvoyEnd engineAttachEnd = engineIsInSourceConvoy ? sourceAttachEnd : destinationAttachEnd;

			if (engineAttachEnd == RailConvoyOrdering.ConvoyEnd.Front)
			{
				player.SendIngameError("yangtransport:linkagepole-wagons-behind-locomotive");
				return false;
			}
		}

		if (!RailVehicleFacingAlignment.TryAlignForMerge(RailGraphSystem, sourceMembers, destinationMembers, out string alignErrorCode))
		{
			player.SendIngameError("yangtransport:" + alignErrorCode);
			return false;
		}

		IRailwayConvoyVehicle finalHead = engine ?? destinationHead;

		if
		(
			!RailConvoyOrdering.TryOrderByRailDistance
			(
				RailGraphSystem.Graph, finalHead, members, BindingCandidatesScratch,
				out var ordered, out var rawDistanceFromHeadByEntityID, out string orderErrorCode
			)
		)
		{
			TryReseedLoadedMembers(sourceMembers);
			TryReseedLoadedMembers(destinationMembers);
			player.SendIngameError("yangtransport:" + orderErrorCode);
			return false;
		}

		if (engine == null) RailVehicleFacingAlignment.PreferFacingOutwardHead(ordered);
		Dictionary<long, double> physicalDistanceFromHeadByEntityID = NormalizePhysicalDistanceFromHead(ordered, rawDistanceFromHeadByEntityID);

		bool hadConductorLocust =
			ConvoyOrMembersHaveConductorLocust(oldSourceHeadID, sourceMembers) ||
			ConvoyOrMembersHaveConductorLocust(oldDestinationHeadID, destinationMembers);

		var oldOwners = new HashSet<long>();
		CollectOccupancyOwners(ordered, oldOwners);

		var restoreGroups = new List<ConvoyRestoreGroup>(2)
		{
			CaptureRestoreGroup(sourceMembers, oldSourceHeadID),
			CaptureRestoreGroup(destinationMembers, oldDestinationHeadID)
		};

		if
		(
			!ApplyConvoy
			(
				ordered, hadConductorLocust ? true : ScanConductorLocust(ordered), seedSafetyState: true,
				seedPhysicalDistanceFromHead: physicalDistanceFromHeadByEntityID
			)
		) { return false; }

		long newHeadID = ordered[0].Entity.EntityId;
		if (oldSourceHeadID != 0 && oldSourceHeadID != newHeadID) Convoys.Remove(oldSourceHeadID);
		if (oldDestinationHeadID != 0 && oldDestinationHeadID != newHeadID) Convoys.Remove(oldDestinationHeadID);
		if (!CommitOccupancyHandoff(oldOwners, ordered[0])) { RollbackConvoyMutation(restoreGroups, ordered); return false; }

		AuthoritySystem?.CommitLoadedMaterialTopology(ordered);
		MarkVehicleChunksModified(ordered);
		player?.Entity?.World?.PlaySoundFor(new AssetLocation("sounds/held/shieldblock-metal-light"), player, randomizePitch: true, range: 24f, volume: 1f);

		return true;
	}

	public bool TryUntether(IRailwayConvoyVehicle vehicle, IServerPlayer player)
	{
		if (ServerAPI == null)
		{
			player.SendIngameError("yangtransport:linkagepole-railgraph-unavailable");
			return false;
		}

		long headID = vehicle.ConvoyHeadEntityID;
		if (headID == 0)
		{
			player.SendIngameError("yangtransport:linkagepole-already-free");
			return false;
		}

		if (!Convoys.TryGetValue(headID, out var convoy) || convoy.Members.Count <= 1)
		{
			vehicle.ServerClearConvoyState();
			player.SendIngameError("yangtransport:linkagepole-already-free");
			return false;
		}

		if (!TryDetachVehicleTransactional(vehicle)) return false;
		player?.Entity?.World?.PlaySoundFor(new AssetLocation("sounds/effect/latch"), player, randomizePitch: true, range: 24f, volume: 1f);

		return true;
	}


	private sealed class ConvoyRestoreGroup
	{
		internal readonly List<IRailwayConvoyVehicle> Members;
		internal readonly bool HasConductorLocust;
		internal readonly IReadOnlyDictionary<long, double>? PhysicalDistanceFromHeadByEntityID;

		internal ConvoyRestoreGroup(List<IRailwayConvoyVehicle> members, bool hasConductorLocust, IReadOnlyDictionary<long, double>? physicalDistanceFromHeadByEntityID)
		{
			Members = members;
			HasConductorLocust = hasConductorLocust;
			PhysicalDistanceFromHeadByEntityID = physicalDistanceFromHeadByEntityID;
		}
	}

	private bool TryDetachVehicleTransactional(IRailwayConvoyVehicle vehicle, bool keepDetachedOwner = true)
	{
		if (RailGraphSystem == null) return false;

		long headID = vehicle.ConvoyHeadEntityID;
		if (headID == 0 || !Convoys.TryGetValue(headID, out var convoy)) return false;

		int detachedMemberIndex = convoy.Members.IndexOf(vehicle.Entity.EntityId);
		if (detachedMemberIndex < 0) return false;

		if (!TryResolveLoadedMembers(convoy.Members, out List<IRailwayConvoyVehicle> originalMembers)) return false;

		var oldOwners = new HashSet<long>();
		CollectOccupancyOwners(originalMembers, oldOwners);
		AddOccupancyOwner(vehicle, oldOwners);

		var restoreGroups = new List<ConvoyRestoreGroup>(1) { CaptureRestoreGroup(originalMembers, headID) };

		List<long> remainingIDs = new(convoy.Members);
		remainingIDs.RemoveAt(detachedMemberIndex);

		var segmentPlans = new List<List<IRailwayConvoyVehicle>>(2);
		if (!TryBuildSplitSegmentPlans(remainingIDs, detachedMemberIndex, segmentPlans)) return false;

		var newOwners = new List<IRailwayConvoyVehicle>(segmentPlans.Count + 1);
		if (keepDetachedOwner && !vehicle.Derailed) newOwners.Add(vehicle);
		for (int iteration = 0; iteration < segmentPlans.Count; iteration++) newOwners.Add(segmentPlans[iteration][0]);

		vehicle.ServerClearConvoyState();
		Convoys.Remove(headID);

		for (int iteration = 0; iteration < segmentPlans.Count; iteration++)
		{
			List<IRailwayConvoyVehicle> segment = segmentPlans[iteration];
			if (!ApplyConvoy(segment, ScanConductorLocust(segment))) { RollbackConvoyMutation(restoreGroups, originalMembers); return false; }
		}

		if (!CommitOccupancyHandoff(oldOwners, newOwners)) { RollbackConvoyMutation(restoreGroups, originalMembers); return false; }

		AuthoritySystem?.CommitLoadedMaterialTopology(originalMembers);
		MarkVehicleChunksModified(originalMembers);
		return true;
	}

	private bool TryBuildSplitSegmentPlans(IReadOnlyList<long> memberIDs, int gapIndex, List<List<IRailwayConvoyVehicle>> segments)
	{
		int memberCount = memberIDs.Count;
		if (memberCount <= 0) return true;

		gapIndex = GameMath.Clamp(gapIndex, 0, memberCount);

		if (memberCount == 1 || gapIndex <= 0 || gapIndex >= memberCount)
		{
			return TryResolveLoadedMembers(memberIDs, out List<IRailwayConvoyVehicle> segment) && AddNonEmptySegment(segment, segments);
		}

		return TryResolveLoadedMembers(memberIDs, 0, gapIndex, out List<IRailwayConvoyVehicle> left) && AddNonEmptySegment(left, segments) &&
			TryResolveLoadedMembers(memberIDs, gapIndex, memberCount - gapIndex, out List<IRailwayConvoyVehicle> right) && AddNonEmptySegment(right, segments);
	}

	private static bool AddNonEmptySegment(List<IRailwayConvoyVehicle> segment, List<List<IRailwayConvoyVehicle>> segments)
	{
		if (segment.Count == 0) return true;
		segments.Add(segment);
		return true;
	}

	private bool TryResolveLoadedMembers(IReadOnlyList<long> memberIDs, out List<IRailwayConvoyVehicle> members)
	{
		return TryResolveLoadedMembers(memberIDs, 0, memberIDs.Count, out members);
	}

	private bool TryResolveLoadedMembers(IReadOnlyList<long> memberIDs, int start, int count, out List<IRailwayConvoyVehicle> members)
	{
		members = new List<IRailwayConvoyVehicle>(count);
		for (int iteration = 0; iteration < count; iteration++)
		{
			if (GetVehicle(memberIDs[start + iteration]) is not IRailwayConvoyVehicle vehicle) return false;
			members.Add(vehicle);
		}

		return true;
	}

	private ConvoyRestoreGroup CaptureRestoreGroup(List<IRailwayConvoyVehicle> members, long headID)
	{
		return new ConvoyRestoreGroup(new List<IRailwayConvoyVehicle>(members), ConvoyOrMembersHaveConductorLocust(headID, members), TryMeasurePhysicalDistances(members));
	}

	private void RollbackConvoyMutation(IReadOnlyList<ConvoyRestoreGroup> restoreGroups, IReadOnlyList<IRailwayConvoyVehicle> touched)
	{
		for (int iteration = 0; iteration < touched.Count; iteration++)
		{
			IRailwayConvoyVehicle vehicle = touched[iteration];
			long currentHeadID = vehicle.ConvoyHeadEntityID;
			if (currentHeadID != 0) Convoys.Remove(currentHeadID);
			Convoys.Remove(vehicle.Entity.EntityId);
			vehicle.ServerClearConvoyState();
		}

		for (int iteration = 0; iteration < restoreGroups.Count; iteration++)
		{
			ConvoyRestoreGroup group = restoreGroups[iteration];
			ApplyConvoy(group.Members, group.HasConductorLocust, seedSafetyState: true, seedPhysicalDistanceFromHead: group.PhysicalDistanceFromHeadByEntityID);
		}
	}

	private bool ApplyConvoy
	(
		List<IRailwayConvoyVehicle> ordered, bool hasConductorLocust, bool seedSafetyState = true,
		IReadOnlyDictionary<long, double>? seedPhysicalDistanceFromHead = null
	)
	{
		if (ordered.Count == 0) return false;

		if (ordered.Count == 1)
		{
			ordered[0].ServerClearConvoyState();
			if (seedSafetyState) ordered[0].ServerSeedConvoyRailStateFromOrderedConvoy(ordered);
			return true;
		}

		IRailwayConvoyVehicle head = ordered[0];
		bool headWasFollower = head.ConvoyHeadEntityID != 0 && head.ConvoyHeadEntityID != head.Entity.EntityId;
		long headID = head.Entity.EntityId;

		var convoy = GetOrCreate(headID);
		convoy.HeadID = headID;
		convoy.AuthoritativeTopology = true;
		convoy.Members.Clear();
		convoy.ExpectedMemberCount = ordered.Count;

		int weight = 0;
		long leadID = headID;
		double canonicalDistanceBehindHead = 0;

		if (seedSafetyState && seedPhysicalDistanceFromHead == null) { seedPhysicalDistanceFromHead = TryMeasurePhysicalDistances(ordered); }

		for (int iteration = 0; iteration < ordered.Count; iteration++)
		{
			var vehicle = ordered[iteration];
			long previousVehicleID = (iteration == 0) ? 0 : ordered[iteration - 1].Entity.EntityId;
			long nextID = (iteration >= ordered.Count - 1) ? 0 : ordered[iteration + 1].Entity.EntityId;

			vehicle.ServerSetConvoyState(headID, iteration, previousVehicleID, canonicalDistanceBehindHead);
			if (vehicle.Entity is EntityStandardGaugeLocomotive standardGaugeVehicle) standardGaugeVehicle.ServerSetNextVehicleID(nextID);

			convoy.Members.Add(vehicle.Entity.EntityId);
			weight += vehicle.SelfWeightCached;

			if (vehicle.HasTractionEngine) leadID = vehicle.Entity.EntityId;

			if (iteration < ordered.Count - 1) { canonicalDistanceBehindHead += Math.Max(0.1, vehicle.ConvoySpacingToNext); }
		}

		InvalidateLoadedMembers(convoy);
		convoy.WeightSum = weight;
		convoy.LeadID = leadID;
		IRailwayConvoyVehicle tail = ordered[ordered.Count - 1];
		convoy.TailDistance = tail.ConvoyDistanceBehindHead;
		convoy.OccupancyRearDistance = convoy.TailDistance + tail.OccupancyRearExtentBlocks;
		convoy.HasConductorLocust = hasConductorLocust;
		SetStatisticsWatchedOnLoadedMembers(convoy);
		if (headWasFollower) { head.ServerInvalidateRailBinding(persist: false); }

		if (seedSafetyState) head.ServerSeedConvoyRailStateFromOrderedConvoy(ordered, seedPhysicalDistanceFromHead);
		return true;
	}

	private bool ConvoyOrMembersHaveConductorLocust(long headID, IReadOnlyList<IRailwayConvoyVehicle> members)
	{
		if (headID != 0 && Convoys.TryGetValue(headID, out var convoy) && convoy.HasConductorLocust) return true;
		return ScanConductorLocust(members);
	}

	private bool ScanConductorLocust(IReadOnlyList<IRailwayConvoyVehicle> members, long excludeEntityID = 0)
	{
		for (int iteration = 0; iteration < members.Count; iteration++)
		{
			IRailwayConvoyVehicle vehicle = members[iteration];
			if (excludeEntityID != 0 && vehicle.Entity.EntityId == excludeEntityID) continue;
			if (vehicle.HasConductorLocustInstalled) return true;
		}

		return false;
	}

	private bool ScanConductorLocust(IReadOnlyList<long> memberIDs, long excludeEntityID = 0)
	{
		for (int iteration = 0; iteration < memberIDs.Count; iteration++)
		{
			long entityID = memberIDs[iteration];
			if (excludeEntityID != 0 && entityID == excludeEntityID) continue;
			if (GetVehicle(entityID) is IRailwayConvoyVehicle vehicle && vehicle.HasConductorLocustInstalled) return true;
		}

		return false;
	}

	private void RefreshConductorLocustForConvoy(long headID, long excludeEntityID = 0)
	{
		if (!Convoys.TryGetValue(headID, out var convoy)) return;
		convoy.HasConductorLocust = ScanConductorLocust(convoy.Members, excludeEntityID);
	}

	private static void CollectOccupancyOwners(IReadOnlyList<IRailwayConvoyVehicle> vehicles, HashSet<long> destination)
	{
		for (int iteration = 0; iteration < vehicles.Count; iteration++) AddOccupancyOwner(vehicles[iteration], destination);
	}

	private static void AddOccupancyOwner(IRailwayConvoyVehicle vehicle, HashSet<long> destination)
	{
		destination.Add(vehicle.Entity.EntityId);
		if (vehicle.ConvoyHeadEntityID != 0) destination.Add(vehicle.ConvoyHeadEntityID);
	}

	private static Dictionary<long, double> NormalizePhysicalDistanceFromHead(IReadOnlyList<IRailwayConvoyVehicle> ordered, IReadOnlyDictionary<long, double> rawDistanceFromHeadByEntityID)
	{
		Dictionary<long, double> normalized = new(ordered.Count);
		if (ordered.Count == 0) return normalized;

		long headID = ordered[0].Entity.EntityId;
		rawDistanceFromHeadByEntityID.TryGetValue(headID, out double headDistance);

		for (int iteration = 0; iteration < ordered.Count; iteration++)
		{
			long entityID = ordered[iteration].Entity.EntityId;
			double raw = rawDistanceFromHeadByEntityID.TryGetValue(entityID, out double measured) ? measured : 0;
			normalized[entityID] = Math.Max(0, Math.Abs(raw - headDistance));
		}

		normalized[headID] = 0;
		return normalized;
	}

	internal IReadOnlyDictionary<long, double>? TryMeasurePhysicalDistances(IReadOnlyList<IRailwayConvoyVehicle> ordered)
	{
		if (RailGraphSystem == null || ordered == null || ordered.Count == 0) return null;
		return RailConvoyOrdering.TryMeasureOrderedRailDistances(RailGraphSystem.Graph, ordered, BindingCandidatesScratch, out var measured) ? measured : null;
	}

	private bool CommitOccupancyHandoff(HashSet<long> oldOwners, IRailwayConvoyVehicle newOwner)
	{
		var owners = new List<IRailwayConvoyVehicle>(1) { newOwner };
		return CommitOccupancyHandoff(oldOwners, owners);
	}

	private bool CommitOccupancyHandoff(HashSet<long> oldOwners, IReadOnlyList<IRailwayConvoyVehicle> newOwners)
	{
		if (RailGraphSystem == null) return false;

		var footprints = new List<RailGraphServerSystem.OwnerEdgeFootprint>(newOwners.Count);
		for (int iteration = 0; iteration < newOwners.Count; iteration++)
		{
			var edges = new HashSet<ulong>();
			if (!TryCollectOccupancyFootprintEdges(newOwners[iteration], RailGraphSystem.Graph, edges)) return false;
			footprints.Add(new RailGraphServerSystem.OwnerEdgeFootprint(newOwners[iteration].Entity.EntityId, edges));
		}

		return RailGraphSystem.ReplaceOwnerEdgeFootprintsEager(footprints, oldOwners);
	}

	private static void MarkVehicleChunksModified(IReadOnlyList<IRailwayConvoyVehicle> vehicles)
	{
		for (int iteration = 0; iteration < vehicles.Count; iteration++)
		{
			Entity entity = vehicles[iteration].Entity;
			entity.World.BlockAccessor.GetChunkAtBlockPos(entity.ServerPos.AsBlockPos)?.MarkModified();
		}
	}

	private static bool TryCollectOccupancyFootprintEdges(IRailwayConvoyVehicle vehicle, RailGraphLive graph, HashSet<ulong> edges)
	{
		return vehicle.Entity switch
		{
			EntityMinecart cart => cart.TryCollectRailOccupancyFootprintEdges(graph, edges),
			EntityStandardGaugeLocomotive standardGaugeVehicle => standardGaugeVehicle.TryCollectRailOccupancyFootprintEdges(graph, edges),
			_ => false
		};
	}


	private void TryReseedLoadedMembers(IReadOnlyList<IRailwayConvoyVehicle> members)
	{
		if (members == null || members.Count == 0) return;
		members[0].ServerSeedConvoyRailStateFromOrderedConvoy(members, TryMeasurePhysicalDistances(members));
	}

	private List<IRailwayConvoyVehicle> GetLoadedMembers(IReadOnlyList<long> memberIDs)
	{
		List<IRailwayConvoyVehicle> members = new(memberIDs.Count);
		for (int iteration = 0; iteration < memberIDs.Count; iteration++) { if (GetVehicle(memberIDs[iteration]) is IRailwayConvoyVehicle vehicle) members.Add(vehicle); }
		return members;
	}

	private void InvalidateLoadedMembers(Convoy convoy)
	{
		if (convoy == null) return;
		convoy.MembershipRevision++;
		if (convoy.MembershipRevision == 0) convoy.MembershipRevision = 1;
		convoy.LoadedMembersRevision = 0;
		convoy.LoadedMembers.Clear();
		convoy.FullyLoaded = false;
		convoy.AuthorityApplied = false;
		convoy.IncompleteLoadedHaloReadyTicks = 0;
	}

	private IReadOnlyList<IRailwayConvoyVehicle> EnsureLoadedMembers(Convoy convoy)
	{
		if (convoy.LoadedMembersRevision == convoy.MembershipRevision) return convoy.LoadedMembers;

		convoy.LoadedMembers.Clear();
		for (int iteration = 0; iteration < convoy.Members.Count; iteration++)
		{
			if (LoadedVehicles.TryGetValue(convoy.Members[iteration], out IRailwayConvoyVehicle? vehicle)) { convoy.LoadedMembers.Add(vehicle); }
		}

		convoy.FullyLoaded = convoy.LoadedMembers.Count == convoy.Members.Count && convoy.LoadedMembers.Count >= Math.Max(1, convoy.ExpectedMemberCount);
		convoy.LoadedMembersRevision = convoy.MembershipRevision;
		return convoy.LoadedMembers;
	}

	private bool HasAnyLoadedMember(Convoy convoy)
	{
		for (int iteration = 0; iteration < convoy.Members.Count; iteration++) { if (LoadedVehicles.ContainsKey(convoy.Members[iteration])) return true; }
		return false;
	}

	private Convoy GetOrCreate(long headID)
	{
		if (!Convoys.TryGetValue(headID, out var convoy))
		{
			convoy = new Convoy { HeadID = headID };
			Convoys[headID] = convoy;
		}
		return convoy;
	}

	private void RebuildOrderedFromPersisted(Convoy convoy)
	{
		if (ServerAPI == null) return;

		List<IRailwayConvoyVehicle> vehicles = new(convoy.Members.Count);
		for (int iteration = 0; iteration < convoy.Members.Count; iteration++)
		{
			if (GetVehicle(convoy.Members[iteration]) is IRailwayConvoyVehicle vehicle) vehicles.Add(vehicle);
		}

		if (vehicles.Count > 1)
		{
			vehicles.Sort((firstVehicle, secondVehicle) =>
			{
				int orderComparison = firstVehicle.ConvoyOrderIndex.CompareTo(secondVehicle.ConvoyOrderIndex);
				if (orderComparison != 0) return orderComparison;
				return firstVehicle.Entity.EntityId.CompareTo(secondVehicle.Entity.EntityId);
			});

			convoy.Members.Clear();
			for (int iteration = 0; iteration < vehicles.Count; iteration++) convoy.Members.Add(vehicles[iteration].Entity.EntityId);
		}

		InvalidateLoadedMembers(convoy);
	}

	private void RefreshStatisticsAndWatch(Convoy convoy)
	{
		int weight = 0;
		long leadID = convoy.HeadID;
		double tailDistance = 0;
		double tailRearExtent = 0;

		for (int iteration = 0; iteration < convoy.Members.Count; iteration++)
		{
			if (GetVehicle(convoy.Members[iteration]) is not IRailwayConvoyVehicle vehicle) continue;
			weight += vehicle.SelfWeightCached;
			if (vehicle.HasTractionEngine) leadID = vehicle.Entity.EntityId;

			double vehicleDistance = vehicle.ConvoyDistanceBehindHead;
			if (vehicleDistance >= tailDistance)
			{
				tailDistance = vehicleDistance;
				tailRearExtent = vehicle.OccupancyRearExtentBlocks;
			}
		}

		convoy.WeightSum = weight;
		convoy.LeadID = leadID;
		convoy.TailDistance = tailDistance;
		convoy.OccupancyRearDistance = tailDistance + tailRearExtent;
		convoy.HasConductorLocust = ScanConductorLocust(convoy.Members);

		RefreshStandardGaugeVisualLinks(convoy);
		SetStatisticsWatchedOnLoadedMembers(convoy);
	}

	private void RefreshStandardGaugeVisualLinks(Convoy convoy)
	{
		for (int iteration = 0; iteration < convoy.Members.Count; iteration++)
		{
			if (GetVehicle(convoy.Members[iteration])?.Entity is not EntityStandardGaugeLocomotive standardGaugeVehicle) continue;

			long nextID = iteration < convoy.Members.Count - 1 ? convoy.Members[iteration + 1] : 0;
			standardGaugeVehicle.ServerSetNextVehicleID(nextID);
		}
	}

	private void SetStatisticsWatchedOnLoadedMembers(Convoy convoy)
	{
		for (int iteration = 0; iteration < convoy.Members.Count; iteration++)
		{
			if (GetVehicle(convoy.Members[iteration]) is IRailwayConvoyVehicle vehicle)
			{
				vehicle.ServerSetConvoyStatsWatched
				(
					Math.Max(convoy.Members.Count, convoy.ExpectedMemberCount),
					convoy.WeightSum, convoy.TailDistance, convoy.OccupancyRearDistance
				);
			}
		}
	}

	private void CollectConvoy(IRailwayConvoyVehicle any, out IRailwayConvoyVehicle head, out List<IRailwayConvoyVehicle> membersOrdered)
	{
		long headID = any.ConvoyHeadEntityID;
		if (headID == 0)
		{
			head = any;
			membersOrdered = new List<IRailwayConvoyVehicle>(1) { any };
			return;
		}

		head = GetVehicle(headID) ?? any;

		if (Convoys.TryGetValue(headID, out var convoy) && convoy.Members.Count > 0)
		{
			membersOrdered = new List<IRailwayConvoyVehicle>(convoy.Members.Count);
			for (int iteration = 0; iteration < convoy.Members.Count; iteration++)
			{
				if (GetVehicle(convoy.Members[iteration]) is IRailwayConvoyVehicle vehicle) membersOrdered.Add(vehicle);
			}
			if (membersOrdered.Count == 0) membersOrdered.Add(any);
			return;
		}

		membersOrdered = new List<IRailwayConvoyVehicle>(1) { any };
	}

	private IRailwayConvoyVehicle? GetVehicle(long entityID) => LoadedVehicles.TryGetValue(entityID, out var vehicle) ? vehicle : null;
}

// Convoy ordering helpers are intentionally nuzzled with the convoy system, they are used only during convoy rebuild/link operations.
/// Railgraph-based ordering utilities for convoys. Operates on loaded IRailwayConvoyVehicle instances and RailGraphLive, and is only called during ops.
internal static class RailConvoyOrdering
{
	internal enum ConvoyEnd : byte { Front, Rear }

	// Only the first vehicle's front coupler and the last vehicle's rear coupler are externally available once a convoy has an established order.
	public static bool TryResolveClosestExposedEnds
	(
		IReadOnlyList<IRailwayConvoyVehicle> firstConvoy,IReadOnlyList<IRailwayConvoyVehicle> secondConvoy,
		double maxAttachDistanceSQ,out ConvoyEnd firstConvoyEnd,out ConvoyEnd secondConvoyEnd
	)
	{
		firstConvoyEnd = ConvoyEnd.Front; secondConvoyEnd = ConvoyEnd.Front;
		if (firstConvoy.Count == 0 || secondConvoy.Count == 0) return false;

		GetExposedCouplers(firstConvoy,out double afx, out double afy, out double afz,out double arx, out double ary, out double arz);
		GetExposedCouplers(secondConvoy,out double bfx, out double bfy, out double bfz,out double brx, out double bry, out double brz);

		double best = DistanceSQ(afx, afy, afz, bfx, bfy, bfz);
		Consider(DistanceSQ(afx, afy, afz, brx, bry, brz), ConvoyEnd.Front, ConvoyEnd.Rear, ref best, ref firstConvoyEnd, ref secondConvoyEnd);
		Consider(DistanceSQ(arx, ary, arz, bfx, bfy, bfz), ConvoyEnd.Rear, ConvoyEnd.Front, ref best, ref firstConvoyEnd, ref secondConvoyEnd);
		Consider(DistanceSQ(arx, ary, arz, brx, bry, brz), ConvoyEnd.Rear, ConvoyEnd.Rear, ref best, ref firstConvoyEnd, ref secondConvoyEnd);

		return best <= maxAttachDistanceSQ;
	}

	private static void GetExposedCouplers
	(
		IReadOnlyList<IRailwayConvoyVehicle> members,
		out double frontX, out double frontY, out double frontZ, out double rearX, out double rearY, out double rearZ
	)
	{
		members[0].GetCouplerWorldPositions(out frontX, out frontY, out frontZ,out double firstRearX, out double firstRearY, out double firstRearZ);
		if (members.Count == 1) { rearX = firstRearX; rearY = firstRearY; rearZ = firstRearZ; return; }

		members[members.Count - 1].GetCouplerWorldPositions(out _, out _, out _,out rearX, out rearY, out rearZ);
	}

	private static void Consider(double distanceSQ, ConvoyEnd candidateA, ConvoyEnd candidateB, ref double best, ref ConvoyEnd firstConvoyEnd, ref ConvoyEnd secondConvoyEnd)
	{
		if (distanceSQ >= best) return;
		best = distanceSQ; firstConvoyEnd = candidateA; secondConvoyEnd = candidateB;
	}

	private static double DistanceSQ(double ax, double ay, double az, double bx, double by, double bz)
	{
		double dx = ax - bx, dy = ay - by, dz = az - bz;
		return dx * dx + dy * dy + dz * dz;
	}

	public static void AddUnique(IReadOnlyList<IRailwayConvoyVehicle> sourceVehicles, HashSet<long> entityIDs, List<IRailwayConvoyVehicle> destinationVehicles)
	{
		for (int iteration = 0; iteration < sourceVehicles.Count; iteration++)
		{
			var vehicle = sourceVehicles[iteration];
			if (entityIDs.Add(vehicle.Entity.EntityId)) destinationVehicles.Add(vehicle);
		}
	}

	public static bool TryOrderByRailDistance
	(
		RailGraphLive graph,
		IRailwayConvoyVehicle head,
		IReadOnlyList<IRailwayConvoyVehicle> members,
		List<ulong> bindingCandidatesScratch,
		out List<IRailwayConvoyVehicle> ordered,
		out Dictionary<long, double> distanceFromHeadByEntityID,
		out string errorCode
	)
	{
		ordered = new List<IRailwayConvoyVehicle>(members.Count);
		distanceFromHeadByEntityID = new Dictionary<long, double>(members.Count);
		errorCode = "linkagepole-vehicles-not-on-rails";

		byte gauge = head.TrackGauge;
		int bindRadiusBlocks = head.BindRadiusBlocks;
		int dimension = head.Entity.Pos.AsBlockPos.dimension;

		if (!RailBindUtil.TryBindNearest(graph, head.Entity.ServerPos.XYZ, dimension, gauge, bindRadiusBlocks, bindingCandidatesScratch, out var headBind))
		{
			errorCode = "linkagepole-head-not-on-rails";
			return false;
		}

		Dictionary<long, RailBindUtil.BindPoint> bindingsByEntityID = new(members.Count);
		double expectedTrainLength = 0;

		for (int iteration = 0; iteration < members.Count; iteration++)
		{
			var vehicle = members[iteration];

			if (vehicle.TrackGauge != gauge) { errorCode = "linkagepole-gauge-mismatch"; return false; }

			if (vehicle.Entity.Pos.AsBlockPos.dimension != dimension) { errorCode = "linkagepole-dimension-mismatch"; return false; }

			if (!RailBindUtil.TryBindNearest(graph, vehicle.Entity.ServerPos.XYZ, dimension, gauge, vehicle.BindRadiusBlocks, bindingCandidatesScratch, out var bindingPoint))
			{
				errorCode = "linkagepole-vehicle-not-on-rails";
				return false;
			}

			bindingsByEntityID[vehicle.Entity.EntityId] = bindingPoint;
			expectedTrainLength += Math.Max(0.1, vehicle.ConvoySpacingToNext);
		}

		// Bounded search so this stays cheap even as the network grows. Slack allows imperfect spacing / slight detours near junctions.
		double maxSearchDistance = Math.Max(16.0, expectedTrainLength + 16.0);

		var distancesByEndpoint = new Dictionary<RailGraphLive.EndpointKey, double>(128);
		var endpointQueue = new PriorityQueue<RailGraphLive.EndpointKey, double>();

		// Seed both endpoints of the head edge.
		distancesByEndpoint[headBind.EndA] = headBind.SFromA;
		distancesByEndpoint[headBind.EndB] = headBind.EdgeLength - headBind.SFromA;
		endpointQueue.Enqueue(headBind.EndA, headBind.SFromA);
		endpointQueue.Enqueue(headBind.EndB, headBind.EdgeLength - headBind.SFromA);

		var edgeLengthCache = new Dictionary<ulong, double>(128);

		while (endpointQueue.Count > 0)
		{
			endpointQueue.TryDequeue(out var endpoint, out double currentDistance);

			if (currentDistance > maxSearchDistance) continue;
			if (distancesByEndpoint.TryGetValue(endpoint, out double dKnown) && currentDistance > dKnown + 1e-9) continue;

			if (!graph.TryGetIncidentEdges(endpoint, out var adjacentEdges)) continue;

			for (int iteration = 0; iteration < adjacentEdges.Count; iteration++)
			{
				ulong edgeHash = adjacentEdges[iteration];

				if (!graph.TryGetEdgeEndpoints(edgeHash, out var startEndpoint, out var endEndpoint, out byte edgeGauge) || edgeGauge != gauge) continue;

				RailGraphLive.EndpointKey other;
				if (startEndpoint.Equals(endpoint)) other = endEndpoint;
				else if (endEndpoint.Equals(endpoint)) other = startEndpoint;
				else continue;

				if (!edgeLengthCache.TryGetValue(edgeHash, out double edgeLength))
				{
					if (!graph.TryGetPolyline16(edgeHash, out var polylineCoordinates16)) continue;
					edgeLength = RailBindUtil.ComputePolylineLength(polylineCoordinates16);
					edgeLengthCache[edgeHash] = edgeLength;
				}

				double nextDistance = currentDistance + edgeLength;
				if (nextDistance > maxSearchDistance) continue;

				if (!distancesByEndpoint.TryGetValue(other, out double existingDistance) || nextDistance < existingDistance)
				{
					distancesByEndpoint[other] = nextDistance;
					endpointQueue.Enqueue(other, nextDistance);
				}
			}
		}

		var scored = new List<(IRailwayConvoyVehicle vehicle, double d)>(members.Count); // The double d can not be renamed, thanks Roslyn.

		for (int iteration = 0; iteration < members.Count; iteration++)
		{
			var vehicle = members[iteration];
			var bindingPoint = bindingsByEntityID[vehicle.Entity.EntityId];

			double best = double.MaxValue;

			if (bindingPoint.EdgeHash == headBind.EdgeHash) best = Math.Min(best, Math.Abs(bindingPoint.SFromA - headBind.SFromA));
			if (distancesByEndpoint.TryGetValue(bindingPoint.EndA, out double distanceViaStartEndpoint)) best = Math.Min(best, distanceViaStartEndpoint + bindingPoint.SFromA);
			if (distancesByEndpoint.TryGetValue(bindingPoint.EndB, out double distanceViaEndEndpoint)) best = Math.Min(best, distanceViaEndEndpoint + (bindingPoint.EdgeLength - bindingPoint.SFromA));

			if (vehicle.Entity.EntityId == head.Entity.EntityId) best = 0;

			if (double.IsInfinity(best) || best == double.MaxValue) { errorCode = "linkagepole-rails-not-connected"; return false; }

			scored.Add((vehicle, best));
		}

		scored.Sort((firstScoredVehicle, secondScoredVehicle) =>
		{
			int distanceComparison = firstScoredVehicle.d.CompareTo(secondScoredVehicle.d);
			if (distanceComparison != 0) return distanceComparison;
			return firstScoredVehicle.vehicle.Entity.EntityId.CompareTo(secondScoredVehicle.vehicle.Entity.EntityId);
		});

		for (int iteration = 0; iteration < scored.Count; iteration++)
		{
			if (iteration > 0 && Math.Abs(scored[iteration].d - scored[iteration - 1].d) < 0.05) { errorCode = "linkagepole-order-ambiguous"; return false; }

			ordered.Add(scored[iteration].vehicle);
			distanceFromHeadByEntityID[scored[iteration].vehicle.Entity.EntityId] = Math.Max(0, scored[iteration].d);
		}

		return true;
	}

	public static bool TryMeasureOrderedRailDistances
	(
		RailGraphLive graph, IReadOnlyList<IRailwayConvoyVehicle> expectedOrder,
		List<ulong> bindingCandidatesScratch, out Dictionary<long, double> distanceFromHeadByEntityID
	)
	{
		distanceFromHeadByEntityID = new Dictionary<long, double>(expectedOrder?.Count ?? 0);
		if (expectedOrder == null || expectedOrder.Count == 0) { return false; }

		if 
		(
			!TryOrderByRailDistance
			(
				graph, expectedOrder[0], expectedOrder, bindingCandidatesScratch,
				out var measuredOrder, out var measuredDistances, out _
			)
		) { return false; }

		if (measuredOrder.Count != expectedOrder.Count) { return false; }
		for (int iteration = 0; iteration < expectedOrder.Count; iteration++) { if (measuredOrder[iteration].Entity.EntityId != expectedOrder[iteration].Entity.EntityId) { return false; } }

		distanceFromHeadByEntityID = measuredDistances;
		return true;
	}
}
