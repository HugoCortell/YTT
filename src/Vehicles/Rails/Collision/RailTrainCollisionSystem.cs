using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace YangTransport;

#region System Core
/// Server-side graph-native train collision and repulsion.
/// Bodies are whole movable rail objects (free vehicle, loaded convoy head, or virtual convoy).
/// Collision spans are emitted per car, resolution commits one kinematic rail displacement per body.
internal sealed class RailTrainCollisionSystem : ModSystem
{
	private const int TickMS = 150;
	internal const double EndpointPaddingBlocks = 0.45;
	private const double SameEdgeOverlapToleranceBlocks = 0.02; // Ignore tiny interval overlap/jitter
	internal const double ProbeRadiusBlocks = 0.85;
	private const double ProbeCellSizeBlocks = 2.0;
	private const int MaxRelaxationPasses = 12;
	private const double MaxMovePerBodyPerPass = 2.0;
	private const double MinUsefulMoveBlocks = 0.01;
	private const double EscapeSearchStepBlocks = 0.75;
	private const double EscapeSearchMaxDistanceBlocks = 128.0;
	internal const double RouteHistoryRequiredBlocks = EscapeSearchMaxDistanceBlocks + 4.0;
	private const int MaxPooledIntegerBuckets = 1024;
	private const int MaxPooledIntegerBucketCapacity = 128;
	private const int MaxPooledPublishedBodies = 1024;
	internal const double StopCollisionSpeedBPS = 15.0;
	internal const double BoilerExplosionCollisionSpeedBPS = 28.0;
	internal const double MediumDerailCollisionSpeedBPS = StopCollisionSpeedBPS;

	private ICoreServerAPI? ServerAPI;
	private RailGraphServerSystem? RailGraphSystem;
	private RailConvoySystem? ConvoySystem;
	private OffscreenConvoySimSystem? OffscreenSimulationSystem;
	private long TickListenerID;

	// Persistent body footprints.
	// Moving heads publish only when their path coordinate or topology changes, collision ticks inspect only bodies affected by those publications.
	private readonly Dictionary<long, PublishedBody> PublishedBodies = new(128);
	private readonly Dictionary<EdgeKey, HashSet<long>> PublishedBodiesByEdge = new(512);
	private readonly Dictionary<ulong, HashSet<long>> PublishedBodiesByEdgeHash = new(512);
	private readonly Dictionary<EndpointKey, HashSet<long>> PublishedBodiesByEndpoint = new(256);
	private readonly Dictionary<ProbeKey, HashSet<long>> PublishedBodiesByProbeCell = new(256);
	private readonly Dictionary<long, IRailwayConvoyVehicle> PendingLoadedBodies = new(64);
	private readonly HashSet<long> DirtyPublishedBodies = new();
	private readonly HashSet<long> CandidateGroups = new();
	private readonly HashSet<PublishedPairKey> CandidatePairs = new();
	private readonly HashSet<PublishedPairKey> UnresolvedPairs = new();
	private readonly List<PublishedPairKey> UnresolvedPairRemovalScratch = new();
	private readonly List<long> GroupScratch = new(128);
	private PublishedBody? ActivePublication;
	private readonly Stack<PublishedBody> PublishedBodyPool = new();

	private readonly List<RailBody> Bodies = new(64);
	private readonly Dictionary<long, int> BodyIndexByGroup = new(64);
	private readonly List<RailBodySpan> Spans = new(512);
	private readonly Dictionary<EdgeKey, List<int>> SpansByEdge = new(256);

	private readonly List<EndpointCap> EndpointCaps = new(128);
	private readonly Dictionary<EndpointKey, List<int>> CapsByEndpoint = new(128);
	private readonly HashSet<EndpointDeduplicationKey> EndpointDeduplication = new();

	private readonly List<RailContactProbe> Probes = new(128);
	private readonly Dictionary<ProbeKey, List<int>> ProbesByCell = new(128);
	private readonly Stack<List<int>> IntegerBucketPool = new();

	private double[] RelaxationMove = Array.Empty<double>();
	private double[] RelaxationAwayX = Array.Empty<double>();
	private double[] RelaxationAwayY = Array.Empty<double>();
	private double[] RelaxationAwayZ = Array.Empty<double>();

	private readonly List<RailContact> Contacts = new(128);
	private readonly Dictionary<PairKey, int> ContactByPair = new(128);
	private readonly List<IRailwayConvoyVehicle> OrderedVehicleScratch = new(16);
	private readonly List<ulong> BindingScratch = new(256);
	private readonly RailCollisionTrail SpanScratch = new();

	private bool DebugCaptureEnabled;
	private readonly List<TrainOverlapDebugBox> DebugBoxes = new(512);

	public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

	public override void Start(ICoreAPI coreAPI)
	{
		ServerAPI = coreAPI as ICoreServerAPI;
		if (ServerAPI == null) return;

		RailGraphSystem = ServerAPI.ModLoader.GetModSystem<RailGraphServerSystem>();
		ConvoySystem = ServerAPI.ModLoader.GetModSystem<RailConvoySystem>();
		OffscreenSimulationSystem = ServerAPI.ModLoader.GetModSystem<OffscreenConvoySimSystem>();
		TickListenerID = ServerAPI.Event.RegisterGameTickListener(OnTick, TickMS);
	}

	public override void Dispose()
	{
		if (TickListenerID != 0) ServerAPI?.Event.UnregisterGameTickListener(TickListenerID);
		TickListenerID = 0;
		ServerAPI = null;
		RailGraphSystem = null;
		ConvoySystem = null;
		OffscreenSimulationSystem = null;
	}

	private void OnTick(float deltaTime)
	{
		if (ServerAPI == null || RailGraphSystem == null) return;

		RailGraphLive graph = RailGraphSystem.Graph;
		if (graph.ConnectionCount == 0) return;

		FlushPendingLoadedBodies(graph);
		CollectBodiesAndContacts(graph);

		if (Contacts.Count == 0) return;

		ApplyPassengerCrashEffectsForSevereContacts();

		if (HandleSevereContacts())
		{
			MarkCurrentContactBodiesDirty();
			FlushPendingLoadedBodies(graph);
			CollectBodiesAndContacts(graph);
			if (Contacts.Count == 0) return;
		}

		ResolveRepulsion(graph);
	}

	private void CollectBodiesAndContacts(RailGraphLive graph)
	{
		ClearScratch();
		CollectDirtyCandidatePairs();

		if (DebugCaptureEnabled) RefreshDebugBoxesFromPublished(graph);
		if (CandidatePairs.Count == 0) return;

		CandidateGroups.Clear();
		foreach (PublishedPairKey pair in CandidatePairs)
		{
			CandidateGroups.Add(pair.FirstGroupID);
			CandidateGroups.Add(pair.SecondGroupID);
		}

		foreach (long groupID in CandidateGroups) { if (PublishedBodies.TryGetValue(groupID, out PublishedBody? body)) AddPublishedBodyToWorkingSet(body); }

		BuildContacts();
		RefreshUnresolvedPairs();
	}

	private enum LoadedBodyPublicationResult
	{
		Published,
		Retry,
		Discarded
	}

	private void FlushPendingLoadedBodies(RailGraphLive graph)
	{
		if (PendingLoadedBodies.Count == 0) return;

		GroupScratch.Clear();
		GroupScratch.AddRange(PendingLoadedBodies.Keys);
		for (int pendingGroupIndex = 0; pendingGroupIndex < GroupScratch.Count; pendingGroupIndex++)
		{
			long groupID = GroupScratch[pendingGroupIndex];
			if (!PendingLoadedBodies.Remove(groupID, out IRailwayConvoyVehicle? head)) continue;

			try
			{
				LoadedBodyPublicationResult result = PublishLoadedBodyNow(graph, head);
				if (result == LoadedBodyPublicationResult.Retry && !PendingLoadedBodies.ContainsKey(groupID)) { PendingLoadedBodies[groupID] = head; }
			}
			catch
			{
				// Preserve the retry request even when an unexpected builder exception is allowed to propagate. The old complete body remains installed.
				if (!PendingLoadedBodies.ContainsKey(groupID)) PendingLoadedBodies[groupID] = head; throw;
			}
		}
		GroupScratch.Clear();
	}

	private void MarkCurrentContactBodiesDirty()
	{
		for (int contactIndex = 0; contactIndex < Contacts.Count; contactIndex++)
		{
			RailContact contact = Contacts[contactIndex];
			if ((uint)contact.FirstBodyIndex < Bodies.Count) DirtyPublishedBodies.Add(Bodies[contact.FirstBodyIndex].GroupID);
			if ((uint)contact.SecondBodyIndex < Bodies.Count) DirtyPublishedBodies.Add(Bodies[contact.SecondBodyIndex].GroupID);
		}
	}

	private void ApplyPassengerCrashEffectsForSevereContacts()
	{
		for (int contactIndex = 0; contactIndex < Contacts.Count; contactIndex++)
		{
			RailContact contact = Contacts[contactIndex];
			if (contact.ImpactSpeed < MediumDerailCollisionSpeedBPS) continue;
			if ((uint)contact.FirstBodyIndex >= Bodies.Count || (uint)contact.SecondBodyIndex >= Bodies.Count) continue;

			RailBody firstBody = Bodies[contact.FirstBodyIndex];
			RailBody secondBody = Bodies[contact.SecondBodyIndex];

			RailwayVehicleShared.ServerShakeImpact
			(
				ServerAPI!, new Vec3d
				(
					(firstBody.CenterX + secondBody.CenterX) * 0.5,
					(firstBody.CenterY + secondBody.CenterY) * 0.5,
					(firstBody.CenterZ + secondBody.CenterZ) * 0.5
				),
				contact.ImpactSpeed
			);

			ServerApplyPassengerTrainCrashImpact(contact.FirstBodyIndex, contact.ImpactSpeed, secondBody.HeadEntity);
			ServerApplyPassengerTrainCrashImpact(contact.SecondBodyIndex, contact.ImpactSpeed, firstBody.HeadEntity);
		}
	}

	private void ServerApplyPassengerTrainCrashImpact(int bodyIndex, double impactSpeedABS, Entity? causeEntity)
	{
		if ((uint)bodyIndex >= Bodies.Count) return;

		RailBody body = Bodies[bodyIndex];
		if (body.IsVirtual || body.HeadEntity == null) return;

		switch (body.HeadEntity)
		{
			case EntityMinecart cart:
				cart.ServerApplyPassengerCrashImpact(impactSpeedABS, causeEntity);
			break;

			case EntityStandardGaugeLocomotive standardGaugeLocomotive:
				standardGaugeLocomotive.ServerApplyPassengerCrashImpact(impactSpeedABS, causeEntity);
			break;
		}
	}

	private void ClearScratch()
	{
		Bodies.Clear();
		BodyIndexByGroup.Clear();

		Spans.Clear();
		ReleaseIntegerBuckets(SpansByEdge);

		EndpointCaps.Clear();
		ReleaseIntegerBuckets(CapsByEndpoint);
		EndpointDeduplication.Clear();

		Probes.Clear();
		ReleaseIntegerBuckets(ProbesByCell);

		Contacts.Clear();
		ContactByPair.Clear();

		if (DebugCaptureEnabled) DebugBoxes.Clear();
	}



	internal void RequestLoadedBodyUpdate(IRailwayConvoyVehicle head)
	{
		if (head == null) return;
		long groupID = head.ConvoyHeadEntityID != 0 ? head.ConvoyHeadEntityID : head.Entity.EntityId;
		if (groupID == 0) return;

		// Followers share the head's published body. A late follower callback must never remove/replace that body, convoy regrouping explicitly republishes new head.
		if (head.ConvoyHeadEntityID != 0 && head.ConvoyHeadEntityID != head.Entity.EntityId) return;
		if (head.Derailed) { RemovePublishedBody(groupID); return; }

		PendingLoadedBodies[groupID] = head;
	}

	internal bool HasPublishedLoadedBody(long groupID, Entity headEntity)
	{
		if (groupID == 0 || headEntity == null) return false;
		return PublishedBodies.TryGetValue(groupID, out PublishedBody? body) && !body.Body.IsVirtual && ReferenceEquals(body.Body.HeadEntity, headEntity);
	}

	internal bool HasPendingLoadedBodyUpdate(long groupID, Entity headEntity)
	{
		return groupID != 0 &&
			headEntity != null &&
			PendingLoadedBodies.TryGetValue(groupID, out IRailwayConvoyVehicle? pending) &&
			ReferenceEquals(pending.Entity, headEntity);
	}

	internal void CancelPendingLoadedBodyUpdate(long groupID, Entity headEntity)
	{
		if (groupID == 0 || headEntity == null) return;
		if (!PendingLoadedBodies.TryGetValue(groupID, out IRailwayConvoyVehicle? pending)) return;
		if (ReferenceEquals(pending.Entity, headEntity)) PendingLoadedBodies.Remove(groupID);
	}

	internal void RemovePublishedBody(long groupID)
	{
		if (groupID == 0) return;
		PendingLoadedBodies.Remove(groupID);
		if (!PublishedBodies.Remove(groupID, out PublishedBody? existingPublishedBody)) return;
		RemovePublishedBodyFromIndexes(existingPublishedBody);
		ReturnPublishedBody(existingPublishedBody);
		DirtyPublishedBodies.Add(groupID);
	}

	internal bool BeginVirtualBodyPublication(long groupID)
	{
		if (groupID == 0 || ActivePublication != null) return false;
		ActivePublication = RentPublishedBody(groupID);
		return true;
	}

	internal void EndVirtualBodyPublication(bool success) { EndBodyPublication(success); }

	private LoadedBodyPublicationResult PublishLoadedBodyNow(RailGraphLive graph, IRailwayConvoyVehicle head)
	{
		long groupID = head.ConvoyHeadEntityID != 0 ? head.ConvoyHeadEntityID : head.Entity.EntityId;
		if (groupID == 0 || head.Derailed ||
			(head.ConvoyHeadEntityID != 0 && head.ConvoyHeadEntityID != head.Entity.EntityId))
		{
			RemovePublishedBody(groupID);
			return LoadedBodyPublicationResult.Discarded;
		}

		if (ActivePublication != null) return LoadedBodyPublicationResult.Retry;
		ActivePublication = RentPublishedBody(groupID);
		bool success;
		try
		{
			double speedABS = head.Entity switch
			{
				EntityMinecart cart => Math.Abs(cart.ServerCollisionSpeed),
				EntityStandardGaugeLocomotive standardGaugeLocomotive => Math.Abs(standardGaugeLocomotive.ServerCollisionSpeed),
				_ => 0
			};
			success = TryBuildLoadedBodyFromHead(graph, head, speedABS);
		}
		catch
		{
			PublishedBody? failedBody = ActivePublication;
			ActivePublication = null;
			if (failedBody != null) ReturnPublishedBody(failedBody);
			throw;
		}

		if (!EndBodyPublication(success)) return LoadedBodyPublicationResult.Retry;

		AcknowledgeLoadedBodyPublication(head, groupID);
		return LoadedBodyPublicationResult.Published;
	}

	private static void AcknowledgeLoadedBodyPublication(IRailwayConvoyVehicle head, long groupID)
	{
		switch (head.Entity)
		{
			case EntityMinecart cart:
				cart.AcknowledgeCollisionBodyPublished(groupID);
			break;

			case EntityStandardGaugeLocomotive standardGaugeLocomotive:
				standardGaugeLocomotive.AcknowledgeCollisionBodyPublished(groupID);
			break;
		}
	}

	private bool EndBodyPublication(bool success)
	{
		PublishedBody? body = ActivePublication;
		ActivePublication = null;
		if (body == null) return false;

		if (!success || body.Spans.Count == 0)
		{
			// A replacement is prepared privately. Failure must leave the last complete body installed.
			// Removing it would make a still-present train disappear from collision queries until a later successful publication.
			ReturnPublishedBody(body);
			return false;
		}

		ReplacePublishedBody(body);
		return true;
	}

	private PublishedBody RentPublishedBody(long groupID)
	{
		PublishedBody body = PublishedBodyPool.Count > 0 ? PublishedBodyPool.Pop() : new PublishedBody();
		body.Reset(groupID);
		return body;
	}

	private void ReturnPublishedBody(PublishedBody body)
	{
		body.Clear();
		if (PublishedBodyPool.Count < MaxPooledPublishedBodies) PublishedBodyPool.Push(body);
	}

	private void ReplacePublishedBody(PublishedBody body)
	{
		if (PublishedBodies.Remove(body.GroupID, out PublishedBody? existingPublishedBody))
		{
			RemovePublishedBodyFromIndexes(existingPublishedBody);
			ReturnPublishedBody(existingPublishedBody);
		}
		PublishedBodies[body.GroupID] = body;
		AddPublishedBodyToIndexes(body);
		DirtyPublishedBodies.Add(body.GroupID);
	}

	private void AddPublishedBodyToIndexes(PublishedBody body)
	{
		for (int publishedSpanIndex = 0; publishedSpanIndex < body.Spans.Count; publishedSpanIndex++)
		{
			PublishedSpan span = body.Spans[publishedSpanIndex];
			AddPersistentBucket(PublishedBodiesByEdge, span.Key, body.GroupID);
			AddPersistentBucket(PublishedBodiesByEdgeHash, span.Key.EdgeHash, body.GroupID);
		}
		foreach (EndpointKey key in body.EndpointKeys) AddPersistentBucket(PublishedBodiesByEndpoint, key, body.GroupID);
		for (int publishedProbeIndex = 0; publishedProbeIndex < body.Probes.Count; publishedProbeIndex++)
		{
			PublishedProbe probe = body.Probes[publishedProbeIndex];
			AddPersistentBucket(PublishedBodiesByProbeCell, ProbeCellFor(probe), body.GroupID);
		}
	}

	private void RemovePublishedBodyFromIndexes(PublishedBody body)
	{
		for (int publishedSpanIndex = 0; publishedSpanIndex < body.Spans.Count; publishedSpanIndex++)
		{
			PublishedSpan span = body.Spans[publishedSpanIndex];
			RemovePersistentBucket(PublishedBodiesByEdge, span.Key, body.GroupID);
			RemovePersistentBucket(PublishedBodiesByEdgeHash, span.Key.EdgeHash, body.GroupID);
		}
		foreach (EndpointKey key in body.EndpointKeys) RemovePersistentBucket(PublishedBodiesByEndpoint, key, body.GroupID);
		for (int publishedProbeIndex = 0; publishedProbeIndex < body.Probes.Count; publishedProbeIndex++)
		{
			PublishedProbe probe = body.Probes[publishedProbeIndex];
			RemovePersistentBucket(PublishedBodiesByProbeCell, ProbeCellFor(probe), body.GroupID);
		}
	}

	private static void AddPersistentBucket<TKey>(Dictionary<TKey, HashSet<long>> index, TKey key, long groupID) where TKey : notnull
	{
		if (!index.TryGetValue(key, out HashSet<long>? bucket))
		{
			bucket = new HashSet<long>();
			index[key] = bucket;
		}
		bucket.Add(groupID);
	}

	private static void RemovePersistentBucket<TKey>(Dictionary<TKey, HashSet<long>> index, TKey key, long groupID) where TKey : notnull
	{
		if (!index.TryGetValue(key, out HashSet<long>? bucket)) return;
		bucket.Remove(groupID);
		if (bucket.Count == 0) index.Remove(key);
	}

	private static ProbeKey ProbeCellFor(in PublishedProbe probe)
	{
		return new ProbeKey(probe.Dimension, probe.Gauge, (int)Math.Floor(probe.X / ProbeCellSizeBlocks), (int)Math.Floor(probe.Z / ProbeCellSizeBlocks));
	}

	private void CollectDirtyCandidatePairs()
	{
		CandidatePairs.Clear();
		UnresolvedPairRemovalScratch.Clear();
		foreach (PublishedPairKey pair in UnresolvedPairs)
		{
			if (PublishedBodies.ContainsKey(pair.FirstGroupID) && PublishedBodies.ContainsKey(pair.SecondGroupID))	{ CandidatePairs.Add(pair); }
			else																									{ UnresolvedPairRemovalScratch.Add(pair); }
		}
		for (int unresolvedPairIndex = 0; unresolvedPairIndex < UnresolvedPairRemovalScratch.Count; unresolvedPairIndex++)
		{
			UnresolvedPairs.Remove(UnresolvedPairRemovalScratch[unresolvedPairIndex]);
		}
		UnresolvedPairRemovalScratch.Clear();

		if (DirtyPublishedBodies.Count == 0) return;

		GroupScratch.Clear();
		GroupScratch.AddRange(DirtyPublishedBodies);
		DirtyPublishedBodies.Clear();

		for (int dirtyGroupIndex = 0; dirtyGroupIndex < GroupScratch.Count; dirtyGroupIndex++)
		{
			long groupID = GroupScratch[dirtyGroupIndex];
			if (!PublishedBodies.TryGetValue(groupID, out PublishedBody? body)) continue;

			for (int publishedSpanIndex = 0; publishedSpanIndex < body.Spans.Count; publishedSpanIndex++)
			{
				if (PublishedBodiesByEdge.TryGetValue(body.Spans[publishedSpanIndex].Key, out HashSet<long>? peers)) AddCandidatePairs(groupID, peers);
			}

			foreach (EndpointKey endpoint in body.EndpointKeys)
			{
				if (PublishedBodiesByEndpoint.TryGetValue(endpoint, out HashSet<long>? peers)) AddCandidatePairs(groupID, peers);
			}

			for (int publishedProbeIndex = 0; publishedProbeIndex < body.Probes.Count; publishedProbeIndex++)
			{
				PublishedProbe probe = body.Probes[publishedProbeIndex];
				ProbeKey ownProbeCell = ProbeCellFor(probe);
				for (int dz = -1; dz <= 1; dz++)
				{
					for (int dx = -1; dx <= 1; dx++)
					{
						var key = new ProbeKey(ownProbeCell.Dimension, ownProbeCell.Gauge, ownProbeCell.X + dx, ownProbeCell.Z + dz);
						if (PublishedBodiesByProbeCell.TryGetValue(key, out HashSet<long>? peers)) AddCandidatePairs(groupID, peers);
					}
				}
			}
		}

		GroupScratch.Clear();
	}

	private void AddCandidatePairs(long groupID, HashSet<long> peers)
	{
		foreach (long other in peers)
		{
			if (other == groupID) continue;
			CandidatePairs.Add(PublishedPairKey.Create(groupID, other));
		}
	}

	private void RefreshUnresolvedPairs()
	{
		// Every candidate was fully rebuilt from the current published bodies.
		// Remove candidates that no longer overlap, then retain every repulsion contact independently of the dirty set.
		foreach (PublishedPairKey pair in CandidatePairs) UnresolvedPairs.Remove(pair);
		for (int contactIndex = 0; contactIndex < Contacts.Count; contactIndex++)
		{
			RailContact contact = Contacts[contactIndex];
			if (!IsRepulsionContact(contact) || (uint)contact.FirstBodyIndex >= Bodies.Count || (uint)contact.SecondBodyIndex >= Bodies.Count) { continue; }
			UnresolvedPairs.Add(PublishedPairKey.Create(Bodies[contact.FirstBodyIndex].GroupID, Bodies[contact.SecondBodyIndex].GroupID));
		}
	}

	private void AddPublishedBodyToWorkingSet(PublishedBody source)
	{
		int bodyIndex = Bodies.Count;
		RailBody body = source.Body;
		Bodies.Add(body);
		BodyIndexByGroup[body.GroupID] = bodyIndex;

		for (int publishedSpanIndex = 0; publishedSpanIndex < source.Spans.Count; publishedSpanIndex++)
		{
			PublishedSpan saved = source.Spans[publishedSpanIndex];
			int spanIndex = Spans.Count;
			Spans.Add(new RailBodySpan
			{
				BodyIndex = bodyIndex,
				CarIndex = saved.CarIndex,
				Key = saved.Key,
				CanonicalMin = saved.CanonicalMin,
				CanonicalMax = saved.CanonicalMax,
				EdgeLength = saved.EdgeLength,
				MiddleX = saved.MiddleX,
				MiddleY = saved.MiddleY,
				MiddleZ = saved.MiddleZ,
				TangentX = saved.TangentX,
				TangentY = saved.TangentY,
				TangentZ = saved.TangentZ,
				EdgeDirection = saved.EdgeDirection
			});

			if (!SpansByEdge.TryGetValue(saved.Key, out List<int>? bucket))
			{
				bucket = RentIntegerBucket();
				SpansByEdge[saved.Key] = bucket;
			}
			bucket.Add(spanIndex);
		}

		foreach (EndpointKey endpoint in source.EndpointKeys)
		{
			int endpointCapIndex = EndpointCaps.Count;
			double x = endpoint.Endpoint.X16 * (1.0 / 16.0);
			double y = endpoint.Endpoint.Y16 * (1.0 / 16.0);
			double z = endpoint.Endpoint.Z16 * (1.0 / 16.0);
			EndpointCaps.Add(new EndpointCap { Key = endpoint, BodyIndex = bodyIndex, X = x, Y = y, Z = z });

			if (!CapsByEndpoint.TryGetValue(endpoint, out List<int>? bucket))
			{
				bucket = RentIntegerBucket();
				CapsByEndpoint[endpoint] = bucket;
			}
			bucket.Add(endpointCapIndex);
		}

		for (int publishedProbeIndex = 0; publishedProbeIndex < source.Probes.Count; publishedProbeIndex++)
		{
			PublishedProbe probe = source.Probes[publishedProbeIndex];
			AddProbe(new RailContactProbe
			{
				BodyIndex = bodyIndex,
				Dimension = probe.Dimension,
				Gauge = probe.Gauge,
				X = probe.X, Y = probe.Y, Z = probe.Z,
				Radius = probe.Radius
			});
		}
	}

	private void RefreshDebugBoxesFromPublished(RailGraphLive graph)
	{
		DebugBoxes.Clear();
		foreach (PublishedBody body in PublishedBodies.Values)
		{
			for (int publishedSpanIndex = 0; publishedSpanIndex < body.Spans.Count; publishedSpanIndex++)
			{
				PublishedSpan span = body.Spans[publishedSpanIndex];
				AddDebugBoxesForInterval
				(
					graph, body.Body.Gauge, span.Key.EdgeHash, span.CanonicalMin, span.CanonicalMax,
					body.Body.IsVirtual ? TrainOverlapDebugBox.StateVirtual : TrainOverlapDebugBox.StateActive
				);
			}
		}
	}

	internal void OnRailGraphChanged(RailGraphChangeSet change)
	{
		if (change == null || !change.HasAnyChange) return;
		if (change.GlobalInvalidation)
		{
			GroupScratch.Clear();
			GroupScratch.AddRange(PublishedBodies.Keys);
		}
		else
		{
			CandidateGroups.Clear();
			for (int touchedEdgeIndex = 0; touchedEdgeIndex < change.TouchedEdges.Count; touchedEdgeIndex++)
			{
				if (!TryGetPublishedGroupsForEdge(change.TouchedEdges[touchedEdgeIndex], out HashSet<long>? groups)) continue;
				foreach (long groupID in groups) CandidateGroups.Add(groupID);
			}
			GroupScratch.Clear();
			GroupScratch.AddRange(CandidateGroups);
			CandidateGroups.Clear();
		}

		for (int groupIndex = 0; groupIndex < GroupScratch.Count; groupIndex++) RemovePublishedBody(GroupScratch[groupIndex]);
		GroupScratch.Clear();
	}

	private bool TryGetPublishedGroupsForEdge(ulong edgeHash, out HashSet<long>? groups)
	{
		return PublishedBodiesByEdgeHash.TryGetValue(edgeHash, out groups);
	}

	private List<int> RentIntegerBucket() { return IntegerBucketPool.Count > 0 ? IntegerBucketPool.Pop() : new List<int>(4); }

	private void ReleaseIntegerBuckets<TKey>(Dictionary<TKey, List<int>> buckets) where TKey : notnull
	{
		foreach (List<int> bucket in buckets.Values)
		{
			if (bucket.Capacity <= MaxPooledIntegerBucketCapacity && IntegerBucketPool.Count < MaxPooledIntegerBuckets)
			{
				bucket.Clear();
				IntegerBucketPool.Push(bucket);
			}
		}

		buckets.Clear();
	}

	internal void SetDebugCaptureEnabled(bool enabled)
	{
		DebugCaptureEnabled = enabled;
		if (!enabled) DebugBoxes.Clear();
	}

	internal TrainOverlapDebugBox[] GetDebugBoxesSnapshot() { return DebugBoxes.Count == 0 ? Array.Empty<TrainOverlapDebugBox>() : DebugBoxes.ToArray(); }

	internal bool WouldPlacementOverlap(RailGraphLive graph, byte gauge, in RailwayVehicleShared.RailCursor sourceCursor, double rearExtent, double frontExtent)
	{
		return WouldPlacementOverlap(graph, gauge, in sourceCursor, rearExtent, frontExtent, out _);
	}

	internal bool WouldPlacementOverlap(RailGraphLive graph, byte gauge, in RailwayVehicleShared.RailCursor sourceCursor, double rearExtent, double frontExtent, out bool hasNonBlockingContact)
	{
		hasNonBlockingContact = false;
		if (ServerAPI == null || graph == null || sourceCursor.SegmentHash == 0) return false;

		var cursor = sourceCursor;
		cursor.Gauge = gauge;

		if (!RailwayVehicleShared.TryRefreshPolyline(graph, ref cursor)) return false;
		if (!graph.TryGetEdgeEndpoints(cursor.SegmentHash, out var edgeA, out _, out byte edgeGauge) || edgeGauge != gauge) return false;
		if (!TrySampleCursorPose(in cursor, out double x, out double y, out double z, out _, out _)) return false;

		FlushPendingLoadedBodies(graph);
		ClearScratch();

		int probeBody = BeginPlacementProbeBody(edgeA.Dimension, gauge, x, y, z);
		if (probeBody < 0) return false;

		if (!AddCarSpansFromCursor(graph, probeBody, 0, in cursor, Math.Max(0.0, rearExtent), Math.Max(0.0, frontExtent))) return false;

		CollectPlacementCandidateGroups(probeBody);
		foreach (long groupID in CandidateGroups) { if (PublishedBodies.TryGetValue(groupID, out PublishedBody? body)) AddPublishedBodyToWorkingSet(body); }
		BuildContacts();

		for (int contactIndex = 0; contactIndex < Contacts.Count; contactIndex++)
		{
			RailContact contact = Contacts[contactIndex];
			if (contact.FirstBodyIndex != probeBody && contact.SecondBodyIndex != probeBody) continue;

			if (IsPlacementBlockingContact(contact)) return true;
			hasNonBlockingContact = true;
		}

		return false;
	}


	private void CollectPlacementCandidateGroups(int probeBody)
	{
		CandidateGroups.Clear();

		for (int spanIndex = 0; spanIndex < Spans.Count; spanIndex++)
		{
			RailBodySpan span = Spans[spanIndex];
			if (span.BodyIndex != probeBody) continue;
			if (!PublishedBodiesByEdge.TryGetValue(span.Key, out HashSet<long>? groups)) continue;
			CandidateGroups.UnionWith(groups);
		}

		for (int endpointCapIndex = 0; endpointCapIndex < EndpointCaps.Count; endpointCapIndex++)
		{
			EndpointCap endpointCap = EndpointCaps[endpointCapIndex];
			if (endpointCap.BodyIndex != probeBody) continue;
			if (!PublishedBodiesByEndpoint.TryGetValue(endpointCap.Key, out HashSet<long>? groups)) continue;
			CandidateGroups.UnionWith(groups);
		}

		for (int probeIndex = 0; probeIndex < Probes.Count; probeIndex++)
		{
			RailContactProbe probe = Probes[probeIndex];
			if (probe.BodyIndex != probeBody) continue;
			int probeCellX = (int)Math.Floor(probe.X / ProbeCellSizeBlocks);
			int probeCellZ = (int)Math.Floor(probe.Z / ProbeCellSizeBlocks);
			for (int dz = -1; dz <= 1; dz++)
			{
				for (int dx = -1; dx <= 1; dx++)
				{
					var key = new ProbeKey(probe.Dimension, probe.Gauge, probeCellX + dx, probeCellZ + dz);
					if (PublishedBodiesByProbeCell.TryGetValue(key, out HashSet<long>? groups)) CandidateGroups.UnionWith(groups);
				}
			}
		}
	}

	private static bool IsPlacementBlockingContact(in RailContact contact)
	{
		// Endpoint caps and junction probes are SG junction sensors, not physical body overlap.
		// They are useful while moving, but they must not make static placement fail when the proposed rail interval does not overlap another body interval.
		return contact.Kind == ContactKind.SameEdgeInterval;
	}

	private int BeginPlacementProbeBody(int dimension, byte gauge, double x, double y, double z)
	{
		var body = new RailBody
		{
			GroupID = long.MinValue,
			HeadEntity = null,
			VirtualHeadKey = 0,
			Gauge = gauge,
			Dimension = dimension,
			SpeedABS = 0,
			MemberCount = 1,
			Weight = 1,
			CenterX = x,
			CenterY = y,
			CenterZ = z,
			IsVirtual = false
		};

		int index = Bodies.Count;
		Bodies.Add(body);
		return index;
	}

	internal bool TryBuildLoadedBodyFromHead(RailGraphLive graph, IRailwayConvoyVehicle head, double speedABS)
	{
		if (head == null || head.Derailed) return false;

		long groupID = head.ConvoyHeadEntityID != 0 ? head.ConvoyHeadEntityID : head.Entity.EntityId;
		if (groupID != head.Entity.EntityId && head.ConvoyHeadEntityID != 0) return false; // followers are represented by the loaded head body

		IReadOnlyList<IRailwayConvoyVehicle> ordered;
		if (head.ConvoyHeadEntityID == head.Entity.EntityId)
		{
			// A partial loaded consist must not replace the last complete collision body. The missing cars remain represented by the existing body/offscreen handoff.
			if (ConvoySystem?.TryGetLoadedOrderedMembers(head.Entity.EntityId, out ordered, requireFullyLoaded: true) != true) { return false; }
		}
		else
		{
			OrderedVehicleScratch.Clear();
			OrderedVehicleScratch.Add(head);
			ordered = OrderedVehicleScratch;
		}

		int weight = 0;
		for (int memberIndex = 0; memberIndex < ordered.Count; memberIndex++) weight += Math.Max(1, ordered[memberIndex].SelfWeightCached);

		int bodyIndex = BeginLoadedBody(groupID, head.Entity, head.TrackGauge, speedABS, ordered.Count, weight);
		if (bodyIndex < 0) return false;

		if (head.Entity is EntityStandardGaugeLocomotive standardGaugeHead) { return standardGaugeHead.TryEmitPathTapeCollisionFootprint(this, graph, bodyIndex, ordered); }
		if (head.Entity is EntityMinecart minecartHead) { return minecartHead.TryEmitRailTapeCollisionFootprint(this, graph, bodyIndex, ordered); }

		bool addedAnyVehicle = false;
		for (int memberIndex = 0; memberIndex < ordered.Count; memberIndex++) addedAnyVehicle |= AddLoadedVehicleCar(graph, bodyIndex, ordered, memberIndex);
		return addedAnyVehicle;
	}


	internal bool TryGetLoadedOrderedMembers(long headID, List<IRailwayConvoyVehicle> destination)
	{
		destination.Clear();
		return ConvoySystem?.TryGetLoadedOrderedMembers(headID, destination) == true;
	}

	internal int BeginLoadedBody(long groupID, Entity headEntity, byte gauge, double speedABS, int memberCount, int weight)
	{
		if (groupID == 0 || headEntity == null) return -1;
		if (ActivePublication != null)
		{
			if (ActivePublication.GroupID != groupID) return -1;
			ActivePublication.Body = new RailBody
			{
				GroupID = groupID,
				HeadEntity = headEntity,
				VirtualHeadKey = 0,
				Gauge = gauge,
				Dimension = headEntity.Pos.AsBlockPos.dimension,
				SpeedABS = Math.Abs(speedABS),
				MemberCount = Math.Max(1, memberCount),
				Weight = Math.Max(1, weight),
				CenterX = headEntity.ServerPos.X,
				CenterY = headEntity.ServerPos.Y,
				CenterZ = headEntity.ServerPos.Z,
				IsVirtual = false
			};
			return 0;
		}
		if (BodyIndexByGroup.TryGetValue(groupID, out int existing)) return existing;

		var body = new RailBody
		{
			GroupID = groupID,
			HeadEntity = headEntity,
			VirtualHeadKey = 0,
			Gauge = gauge,
			Dimension = headEntity.Pos.AsBlockPos.dimension,
			SpeedABS = Math.Abs(speedABS),
			MemberCount = Math.Max(1, memberCount),
			Weight = Math.Max(1, weight),
			CenterX = headEntity.ServerPos.X,
			CenterY = headEntity.ServerPos.Y,
			CenterZ = headEntity.ServerPos.Z,
			IsVirtual = false
		};

		int index = Bodies.Count;
		Bodies.Add(body);
		BodyIndexByGroup[groupID] = index;
		return index;
	}

	internal int GetOrCreateVirtualPathTapeBody(RailGraphLive graph, byte gauge, ConvoyRoute tape, double pathHeadS, long virtualHeadKey, double speedABS)
	{
		if (virtualHeadKey == 0 || tape == null || !tape.ValidateAuthoritative(graph, gauge)) return -1;
		if (ActivePublication == null && BodyIndexByGroup.TryGetValue(virtualHeadKey, out int existing)) return existing;

		if (!tape.TrySampleAuthoritativeCursor(graph, gauge, pathHeadS, out var railCursor)) return -1;
		if (!graph.TryGetEdgeEndpoints(railCursor.SegmentHash, out var firstEndpoint, out _, out byte edgeGauge) || edgeGauge != gauge) return -1;

		var offscreenCursor = new OffscreenRailCursor.Cursor
		{
			SegmentHash = railCursor.SegmentHash,
			SegmentIndex = railCursor.SegmentIndex,
			NormalizedSegmentProgress = railCursor.NormalizedSegmentProgress,
			Direction = railCursor.Direction >= 0 ? 1 : -1
		};

		Vec3d position = new();
		OffscreenRailCursor.TryGetWorldPose(graph, in offscreenCursor, position, out _, out _);

		var body = new RailBody
		{
			GroupID = virtualHeadKey,
			HeadEntity = null,
			VirtualHeadKey = virtualHeadKey,
			Gauge = gauge,
			Dimension = firstEndpoint.Dimension,
			SpeedABS = Math.Abs(speedABS),
			MemberCount = 0,
			Weight = 1,
			CenterX = position.X,
			CenterY = position.Y,
			CenterZ = position.Z,
			IsVirtual = true
		};

		if (ActivePublication != null)
		{
			if (ActivePublication.GroupID != virtualHeadKey) return -1;
			ActivePublication.Body = body;
			return 0;
		}

		int index = Bodies.Count;
		Bodies.Add(body);
		BodyIndexByGroup[virtualHeadKey] = index;
		return index;
	}

	internal void IncrementVirtualBodyMemberCount(int bodyIndex)
	{
		if (ActivePublication != null)
		{
			RailBody published = ActivePublication.Body;
			published.MemberCount++;
			ActivePublication.Body = published;
			return;
		}
		if ((uint)bodyIndex >= Bodies.Count) return;
		RailBody body = Bodies[bodyIndex];
		body.MemberCount++;
		Bodies[bodyIndex] = body;
	}

	internal bool AddLoadedVehicleCar(RailGraphLive graph, int bodyIndex, IReadOnlyList<IRailwayConvoyVehicle> ordered, int carIndex)
	{
		if ((ActivePublication == null && (uint)bodyIndex >= Bodies.Count) || ordered == null || (uint)carIndex >= ordered.Count) return false;

		IRailwayConvoyVehicle vehicle = ordered[carIndex];
		if (vehicle.Derailed) return false;

		if (!TryBindVehicleCursor(graph, vehicle, out var cursor)) return false;
		AimVehicleCursor(ref cursor, ordered, carIndex);

		double rear = Math.Max(0.0, vehicle.OccupancyRearExtentBlocks);
		double front = Math.Max(0.0, vehicle.OccupancyFrontExtentBlocks);

		return AddCarSpansFromCursor(graph, bodyIndex, carIndex, in cursor, rear, front);
	}

	private static void AimVehicleCursor(ref RailwayVehicleShared.RailCursor cursor, IReadOnlyList<IRailwayConvoyVehicle> ordered, int index)
	{
		Entity current = ordered[index].Entity;
		RailwayVehicleShared.ChooseForwardDirectionFromYaw(current.ServerPos.Yaw, ref cursor);

		if (ordered.Count <= 1) return;

		if (index == 0) AimCursor(ref cursor, ordered[1].Entity, current);
		else AimCursor(ref cursor, current, ordered[index - 1].Entity);
	}

	private static void AimCursor(ref RailwayVehicleShared.RailCursor cursor, Entity from, Entity to)
	{
		double dx = to.ServerPos.X - from.ServerPos.X;
		double dz = to.ServerPos.Z - from.ServerPos.Z;
		if (dx * dx + dz * dz > 1e-8) RailwayVehicleShared.ChooseForwardDirectionFromYaw((float)Math.Atan2(dx, dz), ref cursor);
	}

	private bool TryBindVehicleCursor(RailGraphLive graph, IRailwayConvoyVehicle vehicle, out RailwayVehicleShared.RailCursor cursor)
	{
		cursor = default;
		Entity entity = vehicle.Entity;
		int dimension = entity.Pos.AsBlockPos.dimension;
		if (!RailBindUtil.TryBindNearest(graph, entity.ServerPos.XYZ, dimension, vehicle.TrackGauge, vehicle.BindRadiusBlocks, BindingScratch, out var bind)) return false;

		cursor = new RailwayVehicleShared.RailCursor
		{
			Gauge = vehicle.TrackGauge,
			SegmentHash = bind.EdgeHash,
			SegmentIndex = bind.SegmentIndex,
			NormalizedSegmentProgress = bind.SegmentInterpolation,
			Direction = 1,
			PolyXYZ16 = bind.XYZ16,
			PointCount = bind.PointCount,
			BoundGraphVersion = graph.BuildVersion
		};
		return true;
	}

	private bool AddCarSpansFromCursor(RailGraphLive graph, int bodyIndex, int carIndex, in RailwayVehicleShared.RailCursor source, double rearDistance, double frontDistance)
	{
		if (source.SegmentHash == 0 || source.PolyXYZ16 == null || source.PointCount < 2) return false;

		var cursor = source;
		double temporarySpeed = 0;
		double temporaryTravel = 0;
		RailwayVehicleShared.AdvanceAlongTrack(graph, -Math.Max(0, rearDistance), 0, null, false, ref cursor, ref temporarySpeed, ref temporaryTravel, false, out _, null);

		SpanScratch.Clear();
		temporarySpeed = 0;
		temporaryTravel = 0;
		double distance = Math.Max(0, rearDistance) + Math.Max(0, frontDistance);
		if (distance <= 1e-6) return false;

		RailwayVehicleShared.AdvanceAlongTrack(graph, distance, 0, null, false, ref cursor, ref temporarySpeed, ref temporaryTravel, true, out _, SpanScratch);
		int before = Spans.Count;
		SpanScratch.AppendRepulsionSpans(this, graph, bodyIndex, carIndex);
		return Spans.Count > before;
	}

	internal void AddRepulsionSpan(RailGraphLive graph, int bodyIndex, int carIndex, byte gauge, ulong edgeHash, double canonicalStartDistance, double canonicalEndDistance, int edgeDirection)
	{
		if ((ActivePublication == null && (uint)bodyIndex >= Bodies.Count) || edgeHash == 0) return;
		if (!RailCollisionTrail.TryGetCurrentCanonicalDistanceForEdge(graph, edgeHash, gauge, out double edgeLength, out var canonicalStart, out var canonicalEnd)) return;

		double canonicalMin = GameMath.Clamp(Math.Min(canonicalStartDistance, canonicalEndDistance), 0, edgeLength);
		double canonicalMax = GameMath.Clamp(Math.Max(canonicalStartDistance, canonicalEndDistance), 0, edgeLength);
		if (canonicalMax <= canonicalMin + 1e-5) return;

		if (!TrySampleCanonicalPointTangent(graph, edgeHash, gauge, (canonicalMin + canonicalMax) * 0.5, out Vec3d middlePoint, out Vec3d tangent)) return;

		var key = new EdgeKey(canonicalStart.Dimension, gauge, edgeHash);
		if (ActivePublication != null)
		{
			ActivePublication.Spans.Add(new PublishedSpan
			{
				CarIndex = carIndex,
				Key = key,
				CanonicalMin = canonicalMin,
				CanonicalMax = canonicalMax,
				EdgeLength = edgeLength,
				MiddleX = middlePoint.X,
				MiddleY = middlePoint.Y,
				MiddleZ = middlePoint.Z,
				TangentX = tangent.X,
				TangentY = tangent.Y,
				TangentZ = tangent.Z,
				EdgeDirection = edgeDirection
			});
			if (gauge == 1)
			{
				if (canonicalMin <= EndpointPaddingBlocks) EmitEndpointCap(canonicalStart, gauge, bodyIndex);
				if (edgeLength - canonicalMax <= EndpointPaddingBlocks) EmitEndpointCap(canonicalEnd, gauge, bodyIndex);
			}
			return;
		}

		int spanIndex = Spans.Count;
		Spans.Add(new RailBodySpan
		{
			BodyIndex = bodyIndex,
			CarIndex = carIndex,
			Key = key,
			CanonicalMin = canonicalMin,
			CanonicalMax = canonicalMax,
			EdgeLength = edgeLength,
			MiddleX = middlePoint.X,
			MiddleY = middlePoint.Y,
			MiddleZ = middlePoint.Z,
			TangentX = tangent.X,
			TangentY = tangent.Y,
			TangentZ = tangent.Z,
			EdgeDirection = edgeDirection
		});

		if (!SpansByEdge.TryGetValue(key, out var bucket))
		{
			bucket = RentIntegerBucket();
			SpansByEdge[key] = bucket;
		}
		bucket.Add(spanIndex);

		if (DebugCaptureEnabled) AddDebugBoxesForInterval(graph, gauge, edgeHash, canonicalMin, canonicalMax, Bodies[bodyIndex].IsVirtual ? TrainOverlapDebugBox.StateVirtual : TrainOverlapDebugBox.StateActive);

		// Endpoint/probe contacts are SG junction sensors, not physical body overlap. Minecarts must be able to sit one per block; their repulsion is interval-only.
		if (gauge == 1)
		{
			if (canonicalMin <= EndpointPaddingBlocks) EmitEndpointCap(canonicalStart, gauge, bodyIndex);
			if (edgeLength - canonicalMax <= EndpointPaddingBlocks) EmitEndpointCap(canonicalEnd, gauge, bodyIndex);
		}
	}

	private void EmitEndpointCap(RailGraphLive.EndpointKey endpoint, byte gauge, int bodyIndex)
	{
		if (ActivePublication != null)
		{
			var publishedKey = new EndpointKey(endpoint.Dimension, gauge, endpoint);
			if (!ActivePublication.EndpointKeys.Add(publishedKey)) return;
			ActivePublication.Probes.Add(new PublishedProbe
			{
				Dimension = endpoint.Dimension,
				Gauge = gauge,
				Radius = ProbeRadiusBlocks,
				X = endpoint.X16 * (1.0 / 16.0),
				Y = endpoint.Y16 * (1.0 / 16.0),
				Z = endpoint.Z16 * (1.0 / 16.0)
			});
			return;
		}
		if ((uint)bodyIndex >= Bodies.Count) return;

		var deduplicationKey = new EndpointDeduplicationKey(bodyIndex, endpoint);
		if (!EndpointDeduplication.Add(deduplicationKey)) return;

		Vec3d position = new(endpoint.X16 / 16.0, endpoint.Y16 / 16.0, endpoint.Z16 / 16.0);
		var key = new EndpointKey(endpoint.Dimension, gauge, endpoint);
		int endpointCapIndex = EndpointCaps.Count;
		EndpointCaps.Add(new EndpointCap
		{
			Key = key,
			BodyIndex = bodyIndex,
			X = position.X,
			Y = position.Y,
			Z = position.Z
		});

		if (!CapsByEndpoint.TryGetValue(key, out var bucket))
		{
			bucket = RentIntegerBucket();
			CapsByEndpoint[key] = bucket;
		}
		bucket.Add(endpointCapIndex);

		AddProbe(new RailContactProbe
		{
			BodyIndex = bodyIndex,
			Gauge = gauge,
			Dimension = endpoint.Dimension,
			Radius = ProbeRadiusBlocks,
			X = position.X,
			Y = position.Y,
			Z = position.Z
		});
	}

	private bool BothBodiesVirtual(int firstBodyIndex, int secondBodyIndex)
	{
		return (uint)firstBodyIndex < Bodies.Count && (uint)secondBodyIndex < Bodies.Count && Bodies[firstBodyIndex].IsVirtual && Bodies[secondBodyIndex].IsVirtual;
	}


	private void AddProbe(RailContactProbe probe)
	{
		if (ActivePublication != null)
		{
			ActivePublication.Probes.Add(new PublishedProbe
			{
				Dimension = probe.Dimension,
				Gauge = probe.Gauge,
				X = probe.X,
				Y = probe.Y,
				Z = probe.Z,
				Radius = probe.Radius
			});
			return;
		}
		int probeCellX = (int)Math.Floor(probe.X / ProbeCellSizeBlocks);
		int probeCellZ = (int)Math.Floor(probe.Z / ProbeCellSizeBlocks);

		for (int dz = -1; dz <= 1; dz++)
		{
			for (int dx = -1; dx <= 1; dx++)
			{
				var key = new ProbeKey(probe.Dimension, probe.Gauge, probeCellX + dx, probeCellZ + dz);
				if (!ProbesByCell.TryGetValue(key, out var bucket)) continue;

				for (int bucketIndex = 0; bucketIndex < bucket.Count; bucketIndex++)
				{
					RailContactProbe other = Probes[bucket[bucketIndex]];
					if (other.BodyIndex == probe.BodyIndex) continue;
					if (BothBodiesVirtual(other.BodyIndex, probe.BodyIndex)) continue;

					double ox = probe.X - other.X;
					double oy = probe.Y - other.Y;
					double oz = probe.Z - other.Z;
					double combinedRadius = probe.Radius + other.Radius;
					double distanceSQ = ox * ox + oy * oy + oz * oz;
					if (distanceSQ >= combinedRadius * combinedRadius) continue;

					double distance = Math.Sqrt(Math.Max(1e-12, distanceSQ));
					AddContact(other.BodyIndex, probe.BodyIndex, combinedRadius - distance, ox / distance, oy / distance, oz / distance, ContactKind.JunctionProbe);
				}
			}
		}

		var ownKey = new ProbeKey(probe.Dimension, probe.Gauge, probeCellX, probeCellZ);
		if (!ProbesByCell.TryGetValue(ownKey, out var ownBucket))
		{
			ownBucket = RentIntegerBucket();
			ProbesByCell[ownKey] = ownBucket;
		}

		ownBucket.Add(Probes.Count);
		Probes.Add(probe);
	}

	private void BuildContacts()
	{
		foreach (var edgeBucketEntry in SpansByEdge)
		{
			List<int> bucket = edgeBucketEntry.Value;
			for (int firstSpanBucketIndex = 0; firstSpanBucketIndex < bucket.Count; firstSpanBucketIndex++)
			{
				RailBodySpan firstSpan = Spans[bucket[firstSpanBucketIndex]];
				for (int secondSpanBucketIndex = firstSpanBucketIndex + 1; secondSpanBucketIndex < bucket.Count; secondSpanBucketIndex++)
				{
					RailBodySpan secondSpan = Spans[bucket[secondSpanBucketIndex]];
					if (firstSpan.BodyIndex == secondSpan.BodyIndex) continue;
					if (firstSpan.CanonicalMin > secondSpan.CanonicalMax || secondSpan.CanonicalMin > firstSpan.CanonicalMax) continue;

					double overlap = Math.Min(firstSpan.CanonicalMax, secondSpan.CanonicalMax) - Math.Max(firstSpan.CanonicalMin, secondSpan.CanonicalMin);
					if (overlap <= SameEdgeOverlapToleranceBlocks) continue;

					ContactNormalFromSpans(firstSpan, secondSpan, out double nx, out double ny, out double nz);
					AddContact(firstSpan.BodyIndex, secondSpan.BodyIndex, overlap, nx, ny, nz, ContactKind.SameEdgeInterval);
				}
			}
		}

		foreach (var endpointBucketEntry in CapsByEndpoint)
		{
			List<int> bucket = endpointBucketEntry.Value;
			for (int firstEndpointCapBucketIndex = 0; firstEndpointCapBucketIndex < bucket.Count; firstEndpointCapBucketIndex++)
			{
				EndpointCap firstEndpointCap = EndpointCaps[bucket[firstEndpointCapBucketIndex]];
				for (int secondEndpointCapBucketIndex = firstEndpointCapBucketIndex + 1; secondEndpointCapBucketIndex < bucket.Count; secondEndpointCapBucketIndex++)
				{
					EndpointCap secondEndpointCap = EndpointCaps[bucket[secondEndpointCapBucketIndex]];
					if (firstEndpointCap.BodyIndex == secondEndpointCap.BodyIndex) continue;
					if (BothBodiesVirtual(firstEndpointCap.BodyIndex, secondEndpointCap.BodyIndex)) continue;

					ContactNormalFromBodies(firstEndpointCap.BodyIndex, secondEndpointCap.BodyIndex, out double nx, out double ny, out double nz);
					AddContact(firstEndpointCap.BodyIndex, secondEndpointCap.BodyIndex, EndpointPaddingBlocks, nx, ny, nz, ContactKind.EndpointCap);
				}
			}
		}
	}

	private void AddContact(int firstBodyIndex, int secondBodyIndex, double depth, double nx, double ny, double nz, ContactKind kind)
	{
		if ((uint)firstBodyIndex >= Bodies.Count || (uint)secondBodyIndex >= Bodies.Count || firstBodyIndex == secondBodyIndex) return;

		var pair = PairKey.Create(firstBodyIndex, secondBodyIndex);
		if (pair.FirstBodyIndex != firstBodyIndex)
		{
			nx = -nx;
			ny = -ny;
			nz = -nz;
		}

		double normalLength = Math.Sqrt(nx * nx + ny * ny + nz * nz);
		if (normalLength <= 1e-8) { ContactNormalFromBodies(pair.FirstBodyIndex, pair.SecondBodyIndex, out nx, out ny, out nz); }
		else
		{
			double inverseLength = 1.0 / normalLength;
			nx *= inverseLength;
			ny *= inverseLength;
			nz *= inverseLength;
		}

		double impact = Math.Max(Bodies[pair.FirstBodyIndex].SpeedABS, Bodies[pair.SecondBodyIndex].SpeedABS);
		if (ContactByPair.TryGetValue(pair, out int oldIndex))
		{
			RailContact existingContact = Contacts[oldIndex];
			existingContact.ImpactSpeed = Math.Max(existingContact.ImpactSpeed, impact);

			// Same-edge interval overlap is the only ordinary repulsion contact.
			// Do not let endpoint/probe sensors hide a real interval overlap for the same pair, and do not let large sensor radii become invisible body padding.
			bool oldRepulses = IsRepulsionContact(existingContact);
			bool newRepulses = kind == ContactKind.SameEdgeInterval;
			if (newRepulses || !oldRepulses)
			{
				if (newRepulses != oldRepulses || depth > existingContact.Depth)
				{
					existingContact.Depth = Math.Max(0, depth);
					existingContact.NormalX = nx;
					existingContact.NormalY = ny;
					existingContact.NormalZ = nz;
					existingContact.Kind = kind;
				}
			}

			Contacts[oldIndex] = existingContact;
			return;
		}

		ContactByPair[pair] = Contacts.Count;
		Contacts.Add(new RailContact
		{
			FirstBodyIndex = pair.FirstBodyIndex,
			SecondBodyIndex = pair.SecondBodyIndex,
			Depth = Math.Max(0, depth),
			ImpactSpeed = impact,
			NormalX = nx,
			NormalY = ny,
			NormalZ = nz,
			Kind = kind
		});
	}

	private static bool IsRepulsionContact(RailContact contact) { return contact.Kind == ContactKind.SameEdgeInterval; }

	private bool HasRepulsionContacts()
	{
		for (int contactIndex = 0; contactIndex < Contacts.Count; contactIndex++) { if (IsRepulsionContact(Contacts[contactIndex])) return true; }
		return false;
	}

	private bool HandleSevereContacts()
	{
		bool handled = false;
		for (int contactIndex = 0; contactIndex < Contacts.Count; contactIndex++)
		{
			RailContact contact = Contacts[contactIndex];
			if (contact.ImpactSpeed > BoilerExplosionCollisionSpeedBPS) { handled |= ResolveHighSpeedCollision(contact); }
			else if (contact.ImpactSpeed >= MediumDerailCollisionSpeedBPS)
			{
				bool derailed = HandleMediumDerail(contact.FirstBodyIndex, contact.ImpactSpeed);
				derailed |= HandleMediumDerail(contact.SecondBodyIndex, contact.ImpactSpeed);
				if (derailed) PlayDerailCollisionSound(contact);
				handled |= derailed;
			}
		}
		return handled;
	}

	private void ResolveRepulsion(RailGraphLive graph)
	{
		for (int pass = 0; pass < MaxRelaxationPasses; pass++)
		{
			if (!HasRepulsionContacts()) return;

			if (pass == 0) PlayStopCollisionSounds();
			StopContactBodies();

			PrepareRelaxationScratch(Bodies.Count);
			for (int contactIndex = 0; contactIndex < Contacts.Count; contactIndex++)
			{
				RailContact contact = Contacts[contactIndex];
				if (!IsRepulsionContact(contact)) continue;

				SplitContactDepthByWeight(contact.FirstBodyIndex, contact.SecondBodyIndex, Math.Max(0, contact.Depth), out double amountA, out double amountB);

				if (amountA > MinUsefulMoveBlocks)
				{
					AccumulateContactMove(graph, contact.FirstBodyIndex, -contact.NormalX, -contact.NormalY, -contact.NormalZ, amountA, RelaxationMove, RelaxationAwayX, RelaxationAwayY, RelaxationAwayZ);
				}

				if (amountB > MinUsefulMoveBlocks)
				{
					AccumulateContactMove(graph, contact.SecondBodyIndex,  contact.NormalX,  contact.NormalY,  contact.NormalZ, amountB, RelaxationMove, RelaxationAwayX, RelaxationAwayY, RelaxationAwayZ);
				}
			}

			MarkCurrentContactBodiesDirty();
			double movedTotal = ApplyAccumulatedMoves(graph, RelaxationMove, RelaxationAwayX, RelaxationAwayY, RelaxationAwayZ, Bodies.Count);
			FlushPendingLoadedBodies(graph);
			CollectBodiesAndContacts(graph);

			if (!HasRepulsionContacts() || movedTotal <= MinUsefulMoveBlocks) break;
		}

		if (HasRepulsionContacts()) EmergencyEscape(graph);
	}

	private void StopContactBodies()
	{
		for (int contactIndex = 0; contactIndex < Contacts.Count; contactIndex++)
		{
			if (!IsRepulsionContact(Contacts[contactIndex])) continue;
			ServerStopBody(Contacts[contactIndex].FirstBodyIndex);
			ServerStopBody(Contacts[contactIndex].SecondBodyIndex);
		}
	}


	private void PrepareRelaxationScratch(int count)
	{
		if (RelaxationMove.Length < count)
		{
			RelaxationMove = new double[count];
			RelaxationAwayX = new double[count];
			RelaxationAwayY = new double[count];
			RelaxationAwayZ = new double[count];
			return;
		}

		Array.Clear(RelaxationMove, 0, count);
		Array.Clear(RelaxationAwayX, 0, count);
		Array.Clear(RelaxationAwayY, 0, count);
		Array.Clear(RelaxationAwayZ, 0, count);
	}


	private void SplitContactDepthByWeight(int firstBodyIndex, int secondBodyIndex, double depth, out double moveA, out double moveB)
	{
		moveA = 0;
		moveB = 0;

		if (depth <= MinUsefulMoveBlocks) return;
		if ((uint)firstBodyIndex >= Bodies.Count || (uint)secondBodyIndex >= Bodies.Count) return;

		double firstBodyWeight = Math.Max(1, Bodies[firstBodyIndex].Weight);
		double secondBodyWeight = Math.Max(1, Bodies[secondBodyIndex].Weight);
		double total = firstBodyWeight + secondBodyWeight;

		if (total <= 1e-8)
		{
			moveA = depth * 0.5;
			moveB = depth * 0.5;
			return;
		}

		moveA = depth * (secondBodyWeight / total);
		moveB = depth * (firstBodyWeight / total);
	}

	private void AccumulateContactMove(RailGraphLive graph, int bodyIndex, double ax, double ay, double az, double amount,
		double[] move, double[] awayX, double[] awayY, double[] awayZ)
	{
		if ((uint)bodyIndex >= Bodies.Count) return;
		if (amount <= 0) return;

		int sign = ChooseMoveSign(graph, bodyIndex, ax, ay, az);
		move[bodyIndex] += sign * amount;
		awayX[bodyIndex] += ax;
		awayY[bodyIndex] += ay;
		awayZ[bodyIndex] += az;
	}

	private double ApplyAccumulatedMoves(RailGraphLive graph, double[] move, double[] awayX, double[] awayY, double[] awayZ, int count)
	{
		double movedTotal = 0;

		for (int bodyIndex = 0; bodyIndex < count; bodyIndex++)
		{
			double requested = GameMath.Clamp(move[bodyIndex], -MaxMovePerBodyPerPass, MaxMovePerBodyPerPass);
			if (Math.Abs(requested) <= MinUsefulMoveBlocks) continue;

			var away = NormalizeOrBodyForward(bodyIndex, awayX[bodyIndex], awayY[bodyIndex], awayZ[bodyIndex]);
			if (ServerMoveBody(graph, bodyIndex, requested, away, out double moved)) { movedTotal += Math.Abs(moved); }
		}

		return movedTotal;
	}

	private int ChooseMoveSign(RailGraphLive graph, int bodyIndex, double ax, double ay, double az)
	{
		Vec3d away = NormalizeOrBodyForward(bodyIndex, ax, ay, az);
		double probe = 0.75;

		double forwardScore = -double.MaxValue;
		double backwardScore = -double.MaxValue;

		if (ServerProbeBodyMove(graph, bodyIndex, probe, out Vec3d fwdDelta))	{ forwardScore = fwdDelta.X * away.X + fwdDelta.Y * away.Y + fwdDelta.Z * away.Z; }
		if (ServerProbeBodyMove(graph, bodyIndex, -probe, out Vec3d backDelta))	{ backwardScore = backDelta.X * away.X + backDelta.Y * away.Y + backDelta.Z * away.Z; }

		if (forwardScore == -double.MaxValue && backwardScore == -double.MaxValue) return 1;
		return forwardScore >= backwardScore ? 1 : -1;
	}

	private Vec3d NormalizeOrBodyForward(int bodyIndex, double x, double y, double z)
	{
		double vectorLength = Math.Sqrt(x * x + y * y + z * z);
		if (vectorLength > 1e-8) return new Vec3d(x / vectorLength, y / vectorLength, z / vectorLength);

		return new Vec3d(0, 0, 1);
	}

	private void EmergencyEscape(RailGraphLive graph)
	{
		int guard = 0;
		while (HasRepulsionContacts() && guard++ < 16)
		{
			int bodyIndex = PickEmergencyBody();
			if (bodyIndex < 0) return;

			long groupID = Bodies[bodyIndex].GroupID;
			bool solved = false;
			double currentOffset = 0;
			int preferSign = ChooseEmergencySign(graph, bodyIndex);

			for (double radius = EscapeSearchStepBlocks; radius <= EscapeSearchMaxDistanceBlocks; radius += EscapeSearchStepBlocks)
			{
				bodyIndex = FindBodyIndexByGroup(groupID);
				if (bodyIndex < 0) break;

				double candidateOffset = ((int)(radius / EscapeSearchStepBlocks) & 1) == 1 ? preferSign * radius : -preferSign * radius;
				double delta = candidateOffset - currentOffset;
				Vec3d away = new(preferSign, 0, 0);

				if (!ServerMoveBody(graph, bodyIndex, delta, away, out double moved) || Math.Abs(moved) <= MinUsefulMoveBlocks) continue;

				currentOffset += moved;
				DirtyPublishedBodies.Add(groupID);
				FlushPendingLoadedBodies(graph);
				CollectBodiesAndContacts(graph);
				bodyIndex = FindBodyIndexByGroup(groupID);

				if (bodyIndex >= 0 && !BodyHasContact(bodyIndex)) { solved = true; break; }
			}

			if (!solved)
			{
				bodyIndex = FindBodyIndexByGroup(groupID);
				HandleMediumDerail(bodyIndex, 0);
				DirtyPublishedBodies.Add(groupID);
				FlushPendingLoadedBodies(graph);
				CollectBodiesAndContacts(graph);
			}
		}

		// The dirty work set is consumed at the start of each collision pass.
		// Preserve every unresolved pair for the next pass if the bounded emergency loop could not finish the pile-up this tick.
		if (HasRepulsionContacts()) MarkCurrentContactBodiesDirty();
	}

	private int PickEmergencyBody()
	{
		int bestBodyIndex = -1;
		for (int contactIndex = 0; contactIndex < Contacts.Count; contactIndex++)
		{
			if (!IsRepulsionContact(Contacts[contactIndex])) continue;
			ConsiderEmergencyBody(Contacts[contactIndex].FirstBodyIndex, ref bestBodyIndex);
			ConsiderEmergencyBody(Contacts[contactIndex].SecondBodyIndex, ref bestBodyIndex);
		}
		return bestBodyIndex;
	}

	private void ConsiderEmergencyBody(int bodyIndex, ref int bestBodyIndex)
	{
		if ((uint)bodyIndex >= Bodies.Count) return;
		if (bestBodyIndex < 0) { bestBodyIndex = bodyIndex; return; }

		RailBody candidateBody = Bodies[bodyIndex];
		RailBody currentBestBody = Bodies[bestBodyIndex];

		int comparisonResult = candidateBody.MemberCount.CompareTo(currentBestBody.MemberCount);
		if (comparisonResult == 0) comparisonResult = candidateBody.Weight.CompareTo(currentBestBody.Weight);
		if (comparisonResult == 0) comparisonResult = candidateBody.SpeedABS.CompareTo(currentBestBody.SpeedABS);
		if (comparisonResult == 0) comparisonResult = currentBestBody.GroupID.CompareTo(candidateBody.GroupID); // higher id loses ties
		if (comparisonResult < 0) bestBodyIndex = bodyIndex;
	}

	private int ChooseEmergencySign(RailGraphLive graph, int bodyIndex)
	{
		double ax = 0, ay = 0, az = 0;
		for (int contactIndex = 0; contactIndex < Contacts.Count; contactIndex++)
		{
			RailContact contact = Contacts[contactIndex];
			if (!IsRepulsionContact(contact)) continue;
			if (contact.FirstBodyIndex == bodyIndex) { ax -= contact.NormalX; ay -= contact.NormalY; az -= contact.NormalZ; }
			else if (contact.SecondBodyIndex == bodyIndex) { ax += contact.NormalX; ay += contact.NormalY; az += contact.NormalZ; }
		}
		return ChooseMoveSign(graph, bodyIndex, ax, ay, az);
	}

	private bool BodyHasContact(int bodyIndex)
	{
		for (int contactIndex = 0; contactIndex < Contacts.Count; contactIndex++)
		{
			if (!IsRepulsionContact(Contacts[contactIndex])) continue;
			if (Contacts[contactIndex].FirstBodyIndex == bodyIndex || Contacts[contactIndex].SecondBodyIndex == bodyIndex) return true;
		}
		return false;
	}

	private int FindBodyIndexByGroup(long groupID) { return BodyIndexByGroup.TryGetValue(groupID, out int index) ? index : -1; }

	private bool ResolveHighSpeedCollision(RailContact contact)
	{
		int firstBodyIndex = contact.FirstBodyIndex;
		int secondBodyIndex = contact.SecondBodyIndex;
		if ((uint)firstBodyIndex >= Bodies.Count || (uint)secondBodyIndex >= Bodies.Count) return false;

		const double tieEpsilon = 0.10;
		RailBody firstBody = Bodies[firstBodyIndex];
		RailBody secondBody = Bodies[secondBodyIndex];

		if (Math.Abs(firstBody.SpeedABS - secondBody.SpeedABS) <= tieEpsilon)
		{
			bool firstBodyExploded = HandleBoilerExplosion(firstBodyIndex);
			bool secondBodyExploded = HandleBoilerExplosion(secondBodyIndex);
			bool firstBodyDerailed = !firstBodyExploded && HandleMediumDerail(firstBodyIndex, Math.Max(firstBody.SpeedABS, secondBody.SpeedABS));
			bool secondBodyDerailed = !secondBodyExploded && HandleMediumDerail(secondBodyIndex, Math.Max(firstBody.SpeedABS, secondBody.SpeedABS));
			if ((firstBodyDerailed || secondBodyDerailed) && !firstBodyExploded && !secondBodyExploded) PlayDerailCollisionSound(contact);
			return firstBodyExploded || secondBodyExploded || firstBodyDerailed || secondBodyDerailed;
		}

		int faster = firstBody.SpeedABS > secondBody.SpeedABS ? firstBodyIndex : secondBodyIndex;
		int slower = firstBody.SpeedABS > secondBody.SpeedABS ? secondBodyIndex : firstBodyIndex;

		bool exploded = HandleBoilerExplosion(faster);
		bool derailed = !exploded && HandleMediumDerail(faster, Bodies[faster].SpeedABS);
		if (derailed) PlayDerailCollisionSound(contact);

		bool stopped = ServerStopBody(slower);
		return exploded || derailed || stopped;
	}

	private void PlayStopCollisionSounds()
	{
		for (int contactIndex = 0; contactIndex < Contacts.Count; contactIndex++)
		{
			RailContact contact = Contacts[contactIndex];
			if (IsRepulsionContact(contact) && contact.ImpactSpeed > 0.05 && contact.ImpactSpeed < MediumDerailCollisionSpeedBPS)
			{
				PlayCollisionSound(contact, new AssetLocation("sounds/held/shieldblock-metal-heavy"), 32f);
			}
		}
	}

	private void PlayDerailCollisionSound(RailContact contact) { PlayCollisionSound(contact, new AssetLocation("yangtransport", "sounds/derail-crash"), 64f); }

	private void PlayCollisionSound(RailContact contact, AssetLocation sound, float range)
	{
		if (ServerAPI == null) return;
		if ((uint)contact.FirstBodyIndex >= Bodies.Count || (uint)contact.SecondBodyIndex >= Bodies.Count) return;

		RailBody firstBody = Bodies[contact.FirstBodyIndex];
		RailBody secondBody = Bodies[contact.SecondBodyIndex];
		if (firstBody.IsVirtual && secondBody.IsVirtual) return;

		ServerAPI.World.PlaySoundAt(
			sound,
			(firstBody.CenterX + secondBody.CenterX) * 0.5,
			(firstBody.CenterY + secondBody.CenterY) * 0.5,
			(firstBody.CenterZ + secondBody.CenterZ) * 0.5,
			null, true, range, 1f
		);
	}

	private bool ServerStopBody(int bodyIndex)
	{
		if ((uint)bodyIndex >= Bodies.Count) return false;
		RailBody body = Bodies[bodyIndex];

		if (body.IsVirtual) return OffscreenSimulationSystem?.ServerStopVirtualConvoy(body.VirtualHeadKey) == true;

		bool stopped = body.HeadEntity switch
		{
			EntityMinecart cart => cart.ServerStopAfterTrainCollision(),
			EntityStandardGaugeLocomotive standardGaugeLocomotive => standardGaugeLocomotive.ServerStopAfterTrainCollision(),
			_ => false
		};
		if (stopped && body.HeadEntity is IRailwayConvoyVehicle vehicle) RequestLoadedBodyUpdate(vehicle);
		return stopped;
	}

	private bool ServerMoveBody(RailGraphLive graph, int bodyIndex, double signedDistance, Vec3d desiredAway, out double moved)
	{
		moved = 0;
		if ((uint)bodyIndex >= Bodies.Count) return false;
		RailBody body = Bodies[bodyIndex];

		if (body.IsVirtual)
		{
			return OffscreenSimulationSystem != null && OffscreenSimulationSystem.ServerTryMoveVirtualConvoyForRepulsion(body.VirtualHeadKey, signedDistance, desiredAway, out moved);
		}

		return body.HeadEntity switch
		{
			EntityMinecart cart => cart.ServerTryMoveRailRepulsionBody(graph, signedDistance, desiredAway, out moved),
			EntityStandardGaugeLocomotive standardGaugeLocomotive => standardGaugeLocomotive.ServerTryMoveRailRepulsionBody(graph, signedDistance, desiredAway, out moved),
			_ => false
		};
	}

	private bool ServerProbeBodyMove(RailGraphLive graph, int bodyIndex, double signedDistance, out Vec3d delta)
	{
		delta = new Vec3d();
		if ((uint)bodyIndex >= Bodies.Count) return false;
		RailBody body = Bodies[bodyIndex];

		if (body.IsVirtual) return OffscreenSimulationSystem != null && OffscreenSimulationSystem.ServerProbeVirtualConvoyMove(body.VirtualHeadKey, signedDistance, out delta);

		return body.HeadEntity switch
		{
			EntityMinecart cart => cart.ServerProbeRailRepulsionMove(graph, signedDistance, out delta),
			EntityStandardGaugeLocomotive standardGaugeLocomotive => standardGaugeLocomotive.ServerProbeRailRepulsionMove(graph, signedDistance, out delta),
			_ => false
		};
	}


	internal static bool TrySampleCursorPose(in RailwayVehicleShared.RailCursor cursor, out double x, out double y, out double z, out float yaw, out float roll)
	{
		x = y = z = 0;
		yaw = roll = 0;

		if (cursor.PolyXYZ16 == null || cursor.PointCount < 2) return false;

		int pointIndex = GameMath.Clamp(cursor.SegmentIndex, 0, cursor.PointCount - 2);
		int nextPointIndex = pointIndex + 1;
		RailwayVehicleShared.GetPoint(cursor.PolyXYZ16, pointIndex, out double ax, out double ay, out double az);
		RailwayVehicleShared.GetPoint(cursor.PolyXYZ16, nextPointIndex, out double bx, out double by, out double bz);

		double t = GameMath.Clamp(cursor.NormalizedSegmentProgress, 0.0, 1.0);
		x = ax + (bx - ax) * t;
		y = ay + (by - ay) * t;
		z = az + (bz - az) * t;

		int faceDirection = cursor.Direction;
		double forwardDeltaX = faceDirection >= 0 ? bx - ax : ax - bx;
		double forwardDeltaY = faceDirection >= 0 ? by - ay : ay - by;
		double forwardDeltaZ = faceDirection >= 0 ? bz - az : az - bz;

		yaw = (float)Math.Atan2(forwardDeltaX, forwardDeltaZ);
		double horizontalLength = Math.Sqrt(forwardDeltaX * forwardDeltaX + forwardDeltaZ * forwardDeltaZ);
		roll = horizontalLength < 1e-8 ? 0f : (float)(-Math.Atan2(forwardDeltaY, horizontalLength));
		return true;
	}

	private bool HandleMediumDerail(int bodyIndex, double impactSpeedABS)
	{
		if ((uint)bodyIndex >= Bodies.Count) return false;
		RailBody body = Bodies[bodyIndex];

		if (body.IsVirtual) return OffscreenSimulationSystem?.ServerStopVirtualConvoy(body.VirtualHeadKey) == true;

		return body.HeadEntity switch
		{
			EntityMinecart cart => cart.ServerDerailAfterTrainCollision(impactSpeedABS),
			EntityStandardGaugeLocomotive standardGaugeLocomotive => standardGaugeLocomotive.ServerDerailAfterTrainCollision(impactSpeedABS),
			_ => false
		};
	}

	private bool HandleBoilerExplosion(int bodyIndex)
	{
		if ((uint)bodyIndex >= Bodies.Count) return false;
		RailBody body = Bodies[bodyIndex];

		if (body.IsVirtual) return OffscreenSimulationSystem?.ServerMarkVirtualBoilerExplosion(body.VirtualHeadKey) == true;

		return body.HeadEntity switch
		{
			EntityMinecart cart => cart.ServerTryExplodeCollisionBoiler(),
			EntityStandardGaugeLocomotive standardGaugeLocomotive => standardGaugeLocomotive.ServerTryExplodeCollisionBoiler(),
			_ => false
		};
	}

	private static void ContactNormalFromSpans(RailBodySpan firstSpan, RailBodySpan secondSpan, out double nx, out double ny, out double nz)
	{
		nx = secondSpan.MiddleX - firstSpan.MiddleX;
		ny = secondSpan.MiddleY - firstSpan.MiddleY;
		nz = secondSpan.MiddleZ - firstSpan.MiddleZ;

		double normalLengthSQ = nx * nx + ny * ny + nz * nz;
		if (normalLengthSQ > 1e-8) return;

		double sign = secondSpan.CanonicalCenter >= firstSpan.CanonicalCenter ? 1 : -1;
		nx = firstSpan.TangentX * sign;
		ny = firstSpan.TangentY * sign;
		nz = firstSpan.TangentZ * sign;
	}

	private void ContactNormalFromBodies(int firstBodyIndex, int secondBodyIndex, out double nx, out double ny, out double nz)
	{
		RailBody firstBody = Bodies[firstBodyIndex];
		RailBody secondBody = Bodies[secondBodyIndex];
		nx = secondBody.CenterX - firstBody.CenterX;
		ny = secondBody.CenterY - firstBody.CenterY;
		nz = secondBody.CenterZ - firstBody.CenterZ;
		if (nx * nx + ny * ny + nz * nz > 1e-8) return;

		nx = 0; ny = 0; nz = 1;
	}

	private bool TrySampleCanonicalPointTangent(RailGraphLive graph, ulong edgeHash, byte gauge, double canonicalDistance, out Vec3d point, out Vec3d tangent)
	{
		point = new Vec3d();
		tangent = new Vec3d(0, 0, 1);

		if (!graph.TryGetPolyline16(edgeHash, out int[] xyz16)) return false;
		if (!graph.TryGetEdgeEndpoints(edgeHash, out var firstEndpoint, out var secondEndpoint, out byte edgeGauge) || edgeGauge != gauge) return false;

		int pointCount = xyz16.Length / 3;
		if (pointCount < 2) return false;

		double edgeLength = RailCollisionTrail.ComputeStoredLengthForDebug(xyz16, pointCount);
		if (edgeLength <= 1e-6) return false;

		double storedDistance = firstEndpoint.CompareTo(secondEndpoint) <= 0 ? canonicalDistance : edgeLength - canonicalDistance;
		storedDistance = GameMath.Clamp(storedDistance, 0, edgeLength);

		double walk = 0;
		for (int segmentIndex = 0; segmentIndex < pointCount - 1; segmentIndex++)
		{
			RailwayVehicleShared.GetPoint(xyz16, segmentIndex, out double ax, out double ay, out double az);
			RailwayVehicleShared.GetPoint(xyz16, segmentIndex + 1, out double bx, out double by, out double bz);

			double dx = bx - ax;
			double dy = by - ay;
			double dz = bz - az;
			double segmentLength = Math.Sqrt(dx * dx + dy * dy + dz * dz);
			if (segmentLength <= 1e-8) continue;

			if (walk + segmentLength >= storedDistance - 1e-8 || segmentIndex == pointCount - 2)
			{
				double t = GameMath.Clamp((storedDistance - walk) / segmentLength, 0, 1);
				point.Set(ax + dx * t, ay + dy * t, az + dz * t);
				double inverseLength = 1.0 / segmentLength;
				double sign = firstEndpoint.CompareTo(secondEndpoint) <= 0 ? 1 : -1;
				tangent.Set(dx * inverseLength * sign, dy * inverseLength * sign, dz * inverseLength * sign);
				return true;
			}

			walk += segmentLength;
		}

		return false;
	}

	private void AddDebugBoxesForInterval(RailGraphLive graph, byte gauge, ulong edgeHash, double canonicalStartDistance, double canonicalEndDistance, byte state)
	{
		if (!graph.TryGetPolyline16(edgeHash, out int[] xyz16)) return;
		if (!graph.TryGetEdgeEndpoints(edgeHash, out var firstEndpoint, out var secondEndpoint, out byte edgeGauge) || edgeGauge != gauge) return;

		int pointCount = xyz16.Length / 3;
		if (pointCount < 2) return;

		double edgeLength = RailCollisionTrail.ComputeStoredLengthForDebug(xyz16, pointCount);
		if (edgeLength <= 1e-6) return;

		double canonicalMin = GameMath.Clamp(Math.Min(canonicalStartDistance, canonicalEndDistance), 0, edgeLength);
		double canonicalMax = GameMath.Clamp(Math.Max(canonicalStartDistance, canonicalEndDistance), 0, edgeLength);
		if (canonicalMax <= canonicalMin + 1e-6) return;

		bool canonicalMatchesStored = firstEndpoint.CompareTo(secondEndpoint) <= 0;
		double stored0 = canonicalMatchesStored ? canonicalMin : edgeLength - canonicalMax;
		double stored1 = canonicalMatchesStored ? canonicalMax : edgeLength - canonicalMin;

		double halfWidth = gauge == 1 ? 1.05 : 0.55;
		double bottomPadding = 0.15;
		double height = gauge == 1 ? 3.75 : 1.35;

		double walk = 0;
		for (int segmentIndex = 0; segmentIndex < pointCount - 1; segmentIndex++)
		{
			int startPointOffset = segmentIndex * 3;
			int endPointOffset = (segmentIndex + 1) * 3;

			double ax = xyz16[startPointOffset + 0] / 16.0;
			double ay = xyz16[startPointOffset + 1] / 16.0;
			double az = xyz16[startPointOffset + 2] / 16.0;

			double bx = xyz16[endPointOffset + 0] / 16.0;
			double by = xyz16[endPointOffset + 1] / 16.0;
			double bz = xyz16[endPointOffset + 2] / 16.0;

			double segmentLength = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az));
			if (segmentLength <= 1e-8) continue;

			double nextWalk = walk + segmentLength;
			double startClippedDistance = Math.Max(stored0, walk);
			double endClippedDistance = Math.Min(stored1, nextWalk);
			if (endClippedDistance > startClippedDistance + 1e-6)
			{
				double t0 = (startClippedDistance - walk) / segmentLength;
				double t1 = (endClippedDistance - walk) / segmentLength;

				double x0 = ax + (bx - ax) * t0;
				double y0 = ay + (by - ay) * t0;
				double z0 = az + (bz - az) * t0;

				double x1 = ax + (bx - ax) * t1;
				double y1 = ay + (by - ay) * t1;
				double z1 = az + (bz - az) * t1;

				double inverseHorizontalLength = 1.0 / Math.Max(1e-8, Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az)));
				double paddingX = Math.Abs(bz - az) * inverseHorizontalLength * halfWidth;
				double paddingZ = Math.Abs(bx - ax) * inverseHorizontalLength * halfWidth;

				DebugBoxes.Add(new TrainOverlapDebugBox
				{
					X1 = Math.Min(x0, x1) - paddingX,
					Y1 = Math.Min(y0, y1) - bottomPadding,
					Z1 = Math.Min(z0, z1) - paddingZ,
					X2 = Math.Max(x0, x1) + paddingX,
					Y2 = Math.Max(y0, y1) + height,
					Z2 = Math.Max(z0, z1) + paddingZ,
					State = state
				});
			}

			walk = nextWalk;
		}
	}



	private sealed class PublishedBody
	{
		internal long GroupID;
		internal RailBody Body;
		internal readonly List<PublishedSpan> Spans = new(16);
		internal readonly HashSet<EndpointKey> EndpointKeys = new();
		internal readonly List<PublishedProbe> Probes = new(8);

		internal void Reset(long groupID)
		{
			GroupID = groupID;
			Body = default;
		}

		internal void Clear()
		{
			GroupID = 0;
			Body = default;
			Spans.Clear();
			EndpointKeys.Clear();
			Probes.Clear();
		}
	}

	private struct PublishedSpan
	{
		internal int CarIndex;
		internal EdgeKey Key;
		internal double CanonicalMin;
		internal double CanonicalMax;
		internal double EdgeLength;
		internal double MiddleX, MiddleY, MiddleZ;
		internal double TangentX, TangentY, TangentZ;
		internal int EdgeDirection;
	}

	private struct PublishedProbe
	{
		internal int Dimension;
		internal byte Gauge;
		internal double X, Y, Z;
		internal double Radius;
	}

	private readonly record struct PublishedPairKey(long FirstGroupID, long SecondGroupID)
	{
		internal static PublishedPairKey Create(long firstGroupID, long secondGroupID) => firstGroupID < secondGroupID ? new PublishedPairKey(firstGroupID, secondGroupID) : new PublishedPairKey(secondGroupID, firstGroupID);
	}

	private readonly record struct EdgeKey(int Dimension, byte Gauge, ulong EdgeHash);
	private readonly record struct EndpointKey(int Dimension, byte Gauge, RailGraphLive.EndpointKey Endpoint);
	private readonly record struct EndpointDeduplicationKey(int BodyIndex, RailGraphLive.EndpointKey Endpoint);
	private readonly record struct ProbeKey(int Dimension, byte Gauge, int X, int Z);
	private readonly record struct PairKey(int FirstBodyIndex, int SecondBodyIndex)
	{
		public static PairKey Create(int firstBodyIndex, int secondBodyIndex) => firstBodyIndex < secondBodyIndex ? new PairKey(firstBodyIndex, secondBodyIndex) : new PairKey(secondBodyIndex, firstBodyIndex);
	}

	private enum ContactKind
	{
		SameEdgeInterval,
		EndpointCap,
		JunctionProbe
	}

	private struct RailBody
	{
		public long GroupID;
		public Entity? HeadEntity;
		public long VirtualHeadKey;
		public byte Gauge;
		public int Dimension;
		public double SpeedABS;
		public int MemberCount;
		public int Weight;
		public double CenterX;
		public double CenterY;
		public double CenterZ;
		public bool IsVirtual;
	}

	private struct RailBodySpan
	{
		public int BodyIndex;
		public int CarIndex;
		public EdgeKey Key;
		public double CanonicalMin;
		public double CanonicalMax;
		public double EdgeLength;
		public double CanonicalCenter => (CanonicalMin + CanonicalMax) * 0.5;
		public double MiddleX, MiddleY, MiddleZ;
		public double TangentX, TangentY, TangentZ;
		public int EdgeDirection;
	}

	private struct EndpointCap
	{
		public EndpointKey Key;
		public int BodyIndex;
		public double X, Y, Z;
	}

	private struct RailContactProbe
	{
		public int BodyIndex;
		public int Dimension;
		public byte Gauge;
		public double X, Y, Z;
		public double Radius;
	}

	private struct RailContact
	{
		public int FirstBodyIndex;
		public int SecondBodyIndex;
		public double Depth;
		public double ImpactSpeed;
		public double NormalX, NormalY, NormalZ;
		public ContactKind Kind;
	}
}
#endregion


internal readonly struct RailEndpointSensor
{
	public readonly int Dimension;
	public readonly byte Gauge;
	public readonly RailGraphLive.EndpointKey Endpoint;
	public readonly double X;
	public readonly double Y;
	public readonly double Z;

	public RailEndpointSensor(int dimension, byte gauge, RailGraphLive.EndpointKey endpoint)
	{
		Dimension = dimension;
		Gauge = gauge;
		Endpoint = endpoint;
		X = endpoint.X16 * (1.0 / 16.0);
		Y = endpoint.Y16 * (1.0 / 16.0);
		Z = endpoint.Z16 * (1.0 / 16.0);
	}
}

#region Trail
/// Per-simulated-head rail trail used by train-vs-train collision and signal occupancy.
/// Stores canonical edge-local spans from movement or for an eagerly seeded convoy body envelope.
internal sealed class RailCollisionTrail
{
	private const double Epsilon = 1e-7;

	internal struct Span
	{
		public int GraphVersion;
		public byte Gauge;
		public ulong EdgeHash;
		public double Travel0;
		public double Travel1;
		public double CanonicalStart;
		public double CanonicalEnd;
		public int EdgeVelocitySign;
	}

	private readonly List<Span> Spans = new();
	private int FirstUsableSpanIndex;
	private bool CurrentBodySeeded;
	private int CurrentBodySeedGraphVersion;

	public void Clear()
	{
		Spans.Clear();
		FirstUsableSpanIndex = 0;
		CurrentBodySeeded = false;
		CurrentBodySeedGraphVersion = 0;
	}

	public bool HasUsableSpans(int graphVersion) => Spans.Count > FirstUsableSpanIndex && Spans[Spans.Count - 1].GraphVersion == graphVersion;

	public bool HasCurrentBodySeed(int graphVersion) => CurrentBodySeeded && CurrentBodySeedGraphVersion == graphVersion && HasUsableSpans(graphVersion);

	public bool TryGetUsableTravelRange(int graphVersion, out double oldestTravel, out double newestTravel)
	{
		oldestTravel = 0;
		newestTravel = 0;

		bool found = false;
		for (int spanIndex = Spans.Count - 1; spanIndex >= FirstUsableSpanIndex; spanIndex--)
		{
			Span span = Spans[spanIndex];
			if (span.GraphVersion != graphVersion) { if (found) break; continue; }

			if (!found) { newestTravel = span.Travel1; found = true; }
			oldestTravel = span.Travel0;
		}

		return found;
	}

	public void RecordSpan
	(
		RailGraphLive graph, in RailwayVehicleShared.RailCursor cursor, ulong edgeHash,
		int startSegmentIndex, double startSegmentFraction, int endSegmentIndex, double endSegmentFraction, double travel0, double travel1
	)
	{
		if (edgeHash == 0 || cursor.PolyXYZ16 == null || cursor.PointCount < 2) return;
		if (travel1 <= travel0 + Epsilon) return;
		if (!graph.TryGetEdgeEndpoints(edgeHash, out var firstEndpoint, out var secondEndpoint, out byte gauge)) return;
		if (gauge != cursor.Gauge) return;

		double edgeLength;
		double canonicalStart = ComputeCanonicalDistance(cursor.PolyXYZ16, cursor.PointCount, startSegmentIndex, startSegmentFraction, firstEndpoint, secondEndpoint, out edgeLength);
		double canonicalEnd = ComputeCanonicalDistance(cursor.PolyXYZ16, cursor.PointCount, endSegmentIndex, endSegmentFraction, firstEndpoint, secondEndpoint, out _);

		int sign = Math.Sign(canonicalEnd - canonicalStart);
		if (sign == 0) return;

		canonicalStart = GameMath.Clamp(canonicalStart, 0, edgeLength);
		canonicalEnd = GameMath.Clamp(canonicalEnd, 0, edgeLength);

		AddSpan(new Span
		{
			GraphVersion = graph.BuildVersion,
			Gauge = gauge,
			EdgeHash = edgeHash,
			Travel0 = travel0,
			Travel1 = travel1,
			CanonicalStart = canonicalStart,
			CanonicalEnd = canonicalEnd,
			EdgeVelocitySign = sign
		});
	}

	internal bool SeedCurrentBodyFromOrderedConvoy(RailGraphLive graph, IReadOnlyList<IRailwayConvoyVehicle> ordered, double headTravelledABS, List<ulong> bindingScratch)
	{
		if (ordered == null || ordered.Count == 0) return false;

		var seed = new RailCollisionTrail();

		for (int memberIndex = ordered.Count - 1; memberIndex >= 0; memberIndex--)
		{
			IRailwayConvoyVehicle vehicle = ordered[memberIndex];
			if (!TryBindVehicleCursor(graph, vehicle, bindingScratch, out var cursor)) return false;

			AimBodyCursor(ref cursor, ordered, memberIndex);

			double travel = headTravelledABS - vehicle.ConvoyDistanceBehindHead;
			if (!seed.AddCurrentBodyFromCursor(graph, in cursor, travel, vehicle.OccupancyRearExtentBlocks, vehicle.OccupancyFrontExtentBlocks)) return false;
		}

		return ReplaceWith(seed);
	}

	internal bool SeedCurrentBodyFromVehicle(RailGraphLive graph, IRailwayConvoyVehicle vehicle, double travelledABS, List<ulong> bindScratch)
	{
		if (vehicle == null) return false;
		if (!TryBindVehicleCursor(graph, vehicle, bindScratch, out var cursor)) return false;

		RailwayVehicleShared.ChooseForwardDirectionFromYaw(vehicle.Entity.ServerPos.Yaw, ref cursor);

		Clear();
		if (!AddCurrentBodyFromCursor(graph, in cursor, travelledABS, vehicle.OccupancyRearExtentBlocks, vehicle.OccupancyFrontExtentBlocks)) return false;
		CurrentBodySeeded = true;
		CurrentBodySeedGraphVersion = graph.BuildVersion;
		return true;
	}

	internal bool AddCurrentBodyFromCursor(RailGraphLive graph, in RailwayVehicleShared.RailCursor source, double centerTravel, double rearDistance, double frontDistance)
	{
		if (source.SegmentHash == 0 || source.PolyXYZ16 == null || source.PointCount < 2) return false;

		var cursor = source;
		double rear = Math.Max(0, rearDistance);
		double rearMoved = 0;

		if (rear > Epsilon)
		{
			double speed = 0;
			if (!RailwayVehicleShared.AdvanceAlongTrack(graph, -rear, 0, null, false, ref cursor, ref speed, ref rearMoved, true, out _, null)) return false;
		}

		double distance = rearMoved + Math.Max(0, frontDistance);
		if (distance <= Epsilon) return false;

		int before = Spans.Count;
		double travel = centerTravel - rearMoved;
		double forwardSpeed = 0;
		if (!RailwayVehicleShared.AdvanceAlongTrack(graph, distance, 0, null, false, ref cursor, ref forwardSpeed, ref travel, true, out _, this)) return false;

		return Spans.Count > before;
	}

	private bool ReplaceWith(RailCollisionTrail seed)
	{
		if (seed.Spans.Count <= seed.FirstUsableSpanIndex) return false;

		Spans.Clear();
		Spans.AddRange(seed.Spans);
		FirstUsableSpanIndex = seed.FirstUsableSpanIndex;
		CurrentBodySeeded = true;
		CurrentBodySeedGraphVersion = seed.Spans[seed.Spans.Count - 1].GraphVersion;
		return true;
	}

	private static void AimBodyCursor(ref RailwayVehicleShared.RailCursor cursor, IReadOnlyList<IRailwayConvoyVehicle> ordered, int index)
	{
		Entity current = ordered[index].Entity;
		RailwayVehicleShared.ChooseForwardDirectionFromYaw(current.ServerPos.Yaw, ref cursor);

		if (ordered.Count == 1) return;

		if (index == 0) AimCursor(ref cursor, ordered[1].Entity, current);
		else AimCursor(ref cursor, current, ordered[index - 1].Entity);
	}

	private static bool TryBindVehicleCursor(RailGraphLive graph, IRailwayConvoyVehicle vehicle, List<ulong> bindingScratch, out RailwayVehicleShared.RailCursor cursor)
	{
		cursor = default;
		Entity entity = vehicle.Entity;
		int dimension = entity.Pos.AsBlockPos.dimension;
		if (!RailBindUtil.TryBindNearest(graph, entity.ServerPos.XYZ, dimension, vehicle.TrackGauge, vehicle.BindRadiusBlocks, bindingScratch, out var bind)) return false;

		cursor = new RailwayVehicleShared.RailCursor
		{
			Gauge = vehicle.TrackGauge,
			SegmentHash = bind.EdgeHash,
			SegmentIndex = bind.SegmentIndex,
			NormalizedSegmentProgress = bind.SegmentInterpolation,
			PolyXYZ16 = bind.XYZ16,
			PointCount = bind.PointCount,
			BoundGraphVersion = graph.BuildVersion
		};
		return true;
	}

	private static void AimCursor(ref RailwayVehicleShared.RailCursor cursor, Entity from, Entity to)
	{
		double dx = to.ServerPos.X - from.ServerPos.X;
		double dz = to.ServerPos.Z - from.ServerPos.Z;
		if (dx * dx + dz * dz > Epsilon) RailwayVehicleShared.ChooseForwardDirectionFromYaw((float)Math.Atan2(dx, dz), ref cursor);
	}

	private void AddSpan(Span span)
	{
		if (span.EdgeHash == 0 || span.Travel1 <= span.Travel0 + Epsilon) return;

		if (Spans.Count > FirstUsableSpanIndex)
		{
			int lastIndex = Spans.Count - 1;
			Span last = Spans[lastIndex];

			if (last.GraphVersion == span.GraphVersion
				&& last.Gauge == span.Gauge
				&& last.EdgeHash == span.EdgeHash
				&& last.EdgeVelocitySign == span.EdgeVelocitySign
				&& Math.Abs(last.Travel1 - span.Travel0) <= 1e-5
				&& Math.Abs(last.CanonicalEnd - span.CanonicalStart) <= 1e-4)
			{
				last.Travel1 = span.Travel1;
				last.CanonicalEnd = span.CanonicalEnd;
				Spans[lastIndex] = last;
				return;
			}
		}

		Spans.Add(span);
	}


	internal bool CollectFootprintEdges(RailGraphLive graph, in RailwayVehicleShared.RailCursor cursor, double travelledABS, double rearDistance, double frontOverhang, HashSet<ulong> destination)
	{
		destination.Clear();
		if (cursor.SegmentHash == 0 || cursor.PolyXYZ16 == null || cursor.PointCount < 2) return false;

		double rearBound = travelledABS - Math.Max(0, rearDistance);
		double frontBound = travelledABS + Math.Max(0, frontOverhang);
		PruneBefore(graph.BuildVersion, rearBound);
		CollectHistoricalEdges(graph.BuildVersion, rearBound, frontBound, destination);
		destination.Add(cursor.SegmentHash);
		return destination.Count > 0;
	}

	private void PruneBefore(int graphVersion, double travelFrom)
	{
		while (FirstUsableSpanIndex < Spans.Count)
		{
			Span span = Spans[FirstUsableSpanIndex];
			if (span.GraphVersion == graphVersion && span.Travel1 > travelFrom + Epsilon) break;
			FirstUsableSpanIndex++;
		}

		if (FirstUsableSpanIndex * 2 >= Spans.Count)
		{
			Spans.RemoveRange(0, FirstUsableSpanIndex);
			FirstUsableSpanIndex = 0;
		}
	}

	private void CollectHistoricalEdges(int graphVersion, double travelFrom, double travelTo, HashSet<ulong> destination)
	{
		if (Spans.Count <= FirstUsableSpanIndex || travelTo <= travelFrom + Epsilon) return;

		for (int spanIndex = FirstUsableSpanIndex; spanIndex < Spans.Count; spanIndex++)
		{
			Span span = Spans[spanIndex];

			if (span.GraphVersion != graphVersion) continue;
			if (span.Travel1 <= travelFrom + Epsilon || span.Travel0 >= travelTo - Epsilon) continue;
			destination.Add(span.EdgeHash);
		}
	}


	internal int CollectEndpointSensors(RailGraphLive graph, byte gauge, List<RailEndpointSensor> destination, bool clear = false)
	{
		if (destination == null || graph == null) return 0;
		if (clear) destination.Clear();

		int before = destination.Count;
		if (Spans.Count <= FirstUsableSpanIndex) return 0;

		for (int spanIndex = FirstUsableSpanIndex; spanIndex < Spans.Count; spanIndex++)
		{
			Span span = Spans[spanIndex];
			if (span.GraphVersion != graph.BuildVersion || span.Gauge != gauge) continue;
			if (!TryGetCurrentCanonicalDistanceForEdge(graph, span.EdgeHash, gauge, out double edgeLength, out var canonicalStart, out var canonicalEnd)) continue;

			double canonicalMin = GameMath.Clamp(Math.Min(span.CanonicalStart, span.CanonicalEnd), 0, edgeLength);
			double canonicalMax = GameMath.Clamp(Math.Max(span.CanonicalStart, span.CanonicalEnd), 0, edgeLength);
			if (canonicalMax <= canonicalMin + Epsilon) continue;

			if (canonicalMin <= RailTrainCollisionSystem.EndpointPaddingBlocks) destination.Add(new RailEndpointSensor(canonicalStart.Dimension, gauge, canonicalStart));
			if (edgeLength - canonicalMax <= RailTrainCollisionSystem.EndpointPaddingBlocks) destination.Add(new RailEndpointSensor(canonicalEnd.Dimension, gauge, canonicalEnd));
		}

		return destination.Count - before;
	}


	internal void AppendRepulsionSpans(RailTrainCollisionSystem collector, RailGraphLive graph, int bodyIndex, int carIndex)
	{
		if (Spans.Count <= FirstUsableSpanIndex) return;

		for (int spanIndex = FirstUsableSpanIndex; spanIndex < Spans.Count; spanIndex++)
		{
			Span span = Spans[spanIndex];
			if (span.GraphVersion != graph.BuildVersion) continue;
			collector.AddRepulsionSpan(graph, bodyIndex, carIndex, span.Gauge, span.EdgeHash, span.CanonicalStart, span.CanonicalEnd, span.EdgeVelocitySign);
		}
	}

	internal static bool TryGetCurrentCanonicalDistance(RailGraphLive graph, in RailwayVehicleShared.RailCursor cursor, out double canonicalDistance, out double edgeLength, out int positiveCursorDirectionCanonicalSign)
	{
		canonicalDistance = 0;
		edgeLength = 0;
		positiveCursorDirectionCanonicalSign = 0;

		if (cursor.SegmentHash == 0 || cursor.PolyXYZ16 == null || cursor.PointCount < 2) return false;
		if (!graph.TryGetEdgeEndpoints(cursor.SegmentHash, out var firstEndpoint, out var secondEndpoint, out byte gauge) || gauge != cursor.Gauge) return false;

		canonicalDistance = ComputeCanonicalDistance(cursor.PolyXYZ16, cursor.PointCount, cursor.SegmentIndex, cursor.NormalizedSegmentProgress, firstEndpoint, secondEndpoint, out edgeLength);
		canonicalDistance = GameMath.Clamp(canonicalDistance, 0, edgeLength);

		bool storedStartIsCanonical = firstEndpoint.CompareTo(secondEndpoint) <= 0;
		positiveCursorDirectionCanonicalSign = (storedStartIsCanonical ? 1 : -1) * (cursor.Direction >= 0 ? 1 : -1);
		return true;
	}

	internal static bool TryGetCurrentCanonicalDistanceForEdge(RailGraphLive graph, ulong edgeHash, byte gauge, out double edgeLength, out RailGraphLive.EndpointKey canonicalStart, out RailGraphLive.EndpointKey canonicalEnd)
	{
		edgeLength = 0;
		canonicalStart = default;
		canonicalEnd = default;

		if (!graph.TryGetEdgeEndpoints(edgeHash, out var firstEndpoint, out var secondEndpoint, out byte edgeGauge) || edgeGauge != gauge) return false;
		if (!graph.TryGetPolyline16(edgeHash, out int[] xyz16)) return false;

		int pointCount = xyz16.Length / 3;
		if (pointCount < 2) return false;

		edgeLength = ComputeStoredLengthForDebug(xyz16, pointCount);
		if (firstEndpoint.CompareTo(secondEndpoint) <= 0) { canonicalStart = firstEndpoint; canonicalEnd = secondEndpoint; }
		else { canonicalStart = secondEndpoint; canonicalEnd = firstEndpoint; }
		return edgeLength > Epsilon;
	}

	internal static double ComputeStoredLengthForDebug(int[] xyz16, int pointCount)
	{
		double total = 0;
		for (int segmentIndex = 0; segmentIndex < pointCount - 1; segmentIndex++)
		{
			int startPointOffset = segmentIndex * 3;
			int endPointOffset = (segmentIndex + 1) * 3;

			double dx = (xyz16[endPointOffset + 0] - xyz16[startPointOffset + 0]) * (1.0 / 16.0);
			double dy = (xyz16[endPointOffset + 1] - xyz16[startPointOffset + 1]) * (1.0 / 16.0);
			double dz = (xyz16[endPointOffset + 2] - xyz16[startPointOffset + 2]) * (1.0 / 16.0);
			total += Math.Sqrt(dx * dx + dy * dy + dz * dz);
		}

		return total;
	}

	private static double ComputeCanonicalDistance
	(
		int[] xyz16, int pointCount, int segmentIndex, double segmentFraction,
		RailGraphLive.EndpointKey firstEndpoint, RailGraphLive.EndpointKey secondEndpoint, out double edgeLength
	)
	{
		segmentIndex = GameMath.Clamp(segmentIndex, 0, Math.Max(0, pointCount - 2));
		segmentFraction = GameMath.Clamp(segmentFraction, 0.0, 1.0);

		double storedDistance = 0;
		double total = 0;

		for (int currentSegmentIndex = 0; currentSegmentIndex < pointCount - 1; currentSegmentIndex++)
		{
			int startPointOffset = currentSegmentIndex * 3;
			int endPointOffset = (currentSegmentIndex + 1) * 3;

			double dx = (xyz16[endPointOffset + 0] - xyz16[startPointOffset + 0]) * (1.0 / 16.0);
			double dy = (xyz16[endPointOffset + 1] - xyz16[startPointOffset + 1]) * (1.0 / 16.0);
			double dz = (xyz16[endPointOffset + 2] - xyz16[startPointOffset + 2]) * (1.0 / 16.0);
			double segmentLength = Math.Sqrt(dx * dx + dy * dy + dz * dz);

			if (currentSegmentIndex < segmentIndex) storedDistance += segmentLength;
			else if (currentSegmentIndex == segmentIndex) storedDistance += segmentLength * segmentFraction;

			total += segmentLength;
		}

		edgeLength = total;
		return firstEndpoint.CompareTo(secondEndpoint) <= 0 ? storedDistance : total - storedDistance;
	}
}
#endregion


#region Vehicle Defs
public sealed partial class EntityMinecart
{
	private readonly RailCollisionTrail CollisionTrail = new();
	private readonly HashSet<ulong> OccupancyFootprintEdges = new();
	private readonly HashSet<ulong> PublishedOccupancyFootprintEdges = new();
	private int PublishedOccupancyGraphVersion = -1;
	private RailTrainCollisionSystem? TrainCollisionSystem;
	private long PublishedCollisionGroupID;
	private double PublishedCollisionRootDistance = double.NaN;
	private uint PublishedCollisionTopologyRevision = uint.MaxValue;
	private int PublishedCollisionMemberCount = -1;
	private int PublishedCollisionWeight = -1;
	private double PublishedCollisionSpeedABS = double.NaN;

	internal double ServerCollisionSpeed => Speed;

	internal bool TryCollectRailOccupancyFootprintEdges(RailGraphLive graph, HashSet<ulong> destination)
	{
		if (Api?.Side != EnumAppSide.Server) return false;
		if (Derailed || IsFollower) return false;
		if (Cursor.SegmentHash == 0) { if (!TryEnsureBound(forceRebind: true)) return false; }
		else if ((Cursor.BoundGraphVersion != graph.BuildVersion || Cursor.PolyXYZ16 == null || Cursor.PointCount < 2) && !TryEnsureBound(forceRebind: false)) return false;

		destination.Clear();
		return TryCollectRailTapeFootprintEdges(graph, destination);
	}

	internal bool TryPublishRailOccupancyFootprint(RailGraphServerSystem railGraphSystem, RailGraphLive graph, bool force = false)
	{
		if (!TryCollectRailOccupancyFootprintEdges(graph, OccupancyFootprintEdges))
		{
			RemovePublishedCollisionBody();
			return false;
		}

		bool footprintChanged = force || PublishedOccupancyGraphVersion != graph.BuildVersion || !PublishedOccupancyFootprintEdges.SetEquals(OccupancyFootprintEdges);
		if (footprintChanged)
		{
			if (!railGraphSystem.ReportOwnerEdgeFootprint(EntityId, OccupancyFootprintEdges)) return false;
			PublishedOccupancyFootprintEdges.Clear();
			PublishedOccupancyFootprintEdges.UnionWith(OccupancyFootprintEdges);
			PublishedOccupancyGraphVersion = graph.BuildVersion;
		}

		RequestCollisionBodyUpdate(footprintChanged);
		return true;
	}

	private void RequestCollisionBodyUpdate(bool force)
	{
		if (Api?.Side != EnumAppSide.Server || Derailed || IsFollower) { RemovePublishedCollisionBody(); return; }

		long groupID = ConvoyHeadID != 0 ? ConvoyHeadID : EntityId;
		TrainCollisionSystem ??= Api.ModLoader.GetModSystem<RailTrainCollisionSystem>();
		if (PublishedCollisionGroupID != groupID)
		{
			if (PublishedCollisionGroupID != 0) TrainCollisionSystem?.RemovePublishedBody(PublishedCollisionGroupID);
			PublishedCollisionGroupID = groupID;
			ResetCollisionPublicationStamp();
		}

		bool bodyPublished = TrainCollisionSystem?.HasPublishedLoadedBody(groupID, this) == true;
		bool updatePending = TrainCollisionSystem?.HasPendingLoadedBodyUpdate(groupID, this) == true;
		uint topologyRevision = RailTape?.TopologyRevision ?? 0;
		ConvoyStaticStats convoyStatistics = GetConvoyStaticStats();
		int memberCount = convoyStatistics.Count;
		int weight = convoyStatistics.Weight;
		double speedABS = Math.Abs(Speed);
		bool publishedStateMatches =
			bodyPublished &&
			Math.Abs(PublishedCollisionRootDistance - RailRootDistance) <= 1e-4 &&
			PublishedCollisionTopologyRevision == topologyRevision &&
			PublishedCollisionMemberCount == memberCount &&
			PublishedCollisionWeight == weight &&
			Math.Abs(PublishedCollisionSpeedABS - speedABS) <= 0.1;

		if (!force && publishedStateMatches) return;
		if (updatePending) return;

		TrainCollisionSystem?.RequestLoadedBodyUpdate(this);
	}

	internal void AcknowledgeCollisionBodyPublished(long groupID)
	{
		if (Api?.Side != EnumAppSide.Server || Derailed || IsFollower) return;

		long currentGroupID = ConvoyHeadID != 0 ? ConvoyHeadID : EntityId;
		if (groupID == 0 || groupID != currentGroupID) return;

		ConvoyStaticStats convoyStatistics = GetConvoyStaticStats();
		PublishedCollisionGroupID = groupID;
		PublishedCollisionRootDistance = RailRootDistance;
		PublishedCollisionTopologyRevision = RailTape?.TopologyRevision ?? 0;
		PublishedCollisionMemberCount = convoyStatistics.Count;
		PublishedCollisionWeight = convoyStatistics.Weight;
		PublishedCollisionSpeedABS = Math.Abs(Speed);
	}

	private void ResetCollisionPublicationStamp()
	{
		PublishedCollisionRootDistance = double.NaN;
		PublishedCollisionTopologyRevision = uint.MaxValue;
		PublishedCollisionMemberCount = -1;
		PublishedCollisionWeight = -1;
		PublishedCollisionSpeedABS = double.NaN;
	}

	private void RemovePublishedCollisionBody()
	{
		ResetCollisionPublicationStamp();
		long groupID = PublishedCollisionGroupID;
		PublishedCollisionGroupID = 0;
		if (groupID == 0) return;
		TrainCollisionSystem ??= Api?.ModLoader.GetModSystem<RailTrainCollisionSystem>();
		TrainCollisionSystem?.RemovePublishedBody(groupID);
	}


	internal bool ServerStopAfterTrainCollision()
	{
		if (Api?.Side != EnumAppSide.Server) return false;
		if (Derailed || IsFollower) return false;

		Speed = 0;

		EntityMinecart lead = ResolveConvoyLead();
		lead.SteamEngineBehaviour?.ForceDriveLeverStop();
		lead.SteamEngineBehaviour?.ForceExtinguishBoiler();

		PersistTrackState();
		return true;
	}

	internal bool ServerTryMoveRailRepulsionBody(RailGraphLive graph, double signedHeadDistance, Vec3d desiredAwayDirection, out double moved)
	{
		moved = 0;
		if (Api?.Side != EnumAppSide.Server) return false;
		if (Derailed || IsFollower || RailSystem == null || !ServerCanMoveLoadedConvoy()) return false;
		if (Math.Abs(signedHeadDistance) <= 1e-6) return false;

		double rootBeforeMovement = RailRootDistance;
		bool movementSucceeded = ServerAdvanceLoadedConvoyRailTape(signedHeadDistance, ResolveConvoyLead(), null);
		moved = RailRootDistance - rootBeforeMovement;

		Speed = 0;
		if (Math.Abs(moved) <= 1e-8) return false;

		ServerPublishOccupancy(graph);
		PersistTrackState();
		return movementSucceeded || Math.Abs(moved) > 0.01;
	}

	internal bool ServerProbeRailRepulsionMove(RailGraphLive graph, double signedDistance, out Vec3d delta)
	{
		delta = new Vec3d();
		if (Api?.Side != EnumAppSide.Server) return false;
		if (Derailed || IsFollower || RailSystem == null || !ServerCanMoveLoadedConvoy()) return false;
		if ((Cursor.BoundGraphVersion != graph.BuildVersion || Cursor.PolyXYZ16 == null || Cursor.PointCount < 2) && !TryEnsureBound(forceRebind: false)) return false;
		var probeCursor = Cursor;

		if (!TryGetCursorWorldPosition(in probeCursor, out Vec3d before)) return false;

		double speed = 0;
		double travel = 0;
		bool movementSucceeded = RailwayVehicleShared.AdvanceAlongTrack(graph, signedDistance, 0, null, false, ref probeCursor, ref speed, ref travel, false, out _, null);
		if (!movementSucceeded || !TryGetCursorWorldPosition(in probeCursor, out Vec3d after)) return false;

		delta.Set(after.X - before.X, after.Y - before.Y, after.Z - before.Z);
		return true;
	}

	private static bool TryGetCursorWorldPosition(in RailwayVehicleShared.RailCursor cursor, out Vec3d position)
	{
		position = new Vec3d();
		if (cursor.PolyXYZ16 == null || cursor.PointCount < 2) return false;

		int pointIndex = GameMath.Clamp(cursor.SegmentIndex, 0, cursor.PointCount - 2);
		int nextPointIndex = pointIndex + 1;
		RailwayVehicleShared.GetPoint(cursor.PolyXYZ16, pointIndex, out double ax, out double ay, out double az);
		RailwayVehicleShared.GetPoint(cursor.PolyXYZ16, nextPointIndex, out double bx, out double by, out double bz);

		double segmentProgress = GameMath.Clamp(cursor.NormalizedSegmentProgress, 0.0, 1.0);
		position.Set(ax + (bx - ax) * segmentProgress, ay + (by - ay) * segmentProgress, az + (bz - az) * segmentProgress);
		return true;
	}

	internal bool ServerDerailAfterTrainCollision(double impactSpeedABS)
	{
		if (Api?.Side != EnumAppSide.Server) return false;
		if (Derailed || IsFollower) return false;

		impactSpeedABS = Math.Max(Math.Abs(Speed), Math.Abs(impactSpeedABS));
		if (impactSpeedABS > 0.01 && Math.Abs(Speed) < impactSpeedABS) { Speed = Math.Sign(Speed == 0 ? 1 : Speed) * impactSpeedABS; }

		ServerDerailConvoyMembers(excludeThis: false);
		return true;
	}

	internal bool ServerTryExplodeCollisionBoiler()
	{
		if (Api?.Side != EnumAppSide.Server) return false;
		if (Derailed || IsFollower) return false;

		EntityMinecart lead = ResolveConvoyLead();
		if (lead.SteamEngineBehaviour == null) return false;

		lead.SteamEngineBehaviour.ExplodeAndRemove();
		return true;
	}
}

public sealed partial class EntityStandardGaugeLocomotive
{
	private readonly RailCollisionTrail CollisionTrail = new();
	private readonly HashSet<ulong> OccupancyFootprintEdges = new();
	private readonly HashSet<ulong> PublishedOccupancyFootprintEdges = new();
	private int PublishedOccupancyGraphVersion = -1;
	private RailTrainCollisionSystem? TrainCollisionSystem;
	private long PublishedCollisionGroupID;
	private double PublishedCollisionPathHeadDistance = double.NaN;
	private uint PublishedCollisionTopologyRevision = uint.MaxValue;
	private int PublishedCollisionMemberCount = -1;
	private int PublishedCollisionWeight = -1;
	private double PublishedCollisionSpeedABS = double.NaN;

	internal double ServerCollisionSpeed => Speed;

	internal bool TryCollectRailOccupancyFootprintEdges(RailGraphLive graph, HashSet<ulong> destination)
	{
		if (Api?.Side != EnumAppSide.Server) return false;
		if (Derailed || IsFollower) return false;
		if (Cursor.SegmentHash == 0) { if (!TryEnsureBound(forceRebind: true)) return false; }
		else if ((Cursor.BoundGraphVersion != graph.BuildVersion || Cursor.PolyXYZ16 == null || Cursor.PointCount < 2) && !TryEnsureBound(forceRebind: false)) return false;

		destination.Clear();
		return TryCollectPathTapeFootprintEdges(graph, destination);
	}

	internal bool TryPublishRailOccupancyFootprint(RailGraphServerSystem railGraphSystem, RailGraphLive graph, bool force = false)
	{
		if (!TryCollectRailOccupancyFootprintEdges(graph, OccupancyFootprintEdges)) { RemovePublishedCollisionBody(); return false; }

		bool footprintChanged = force || PublishedOccupancyGraphVersion != graph.BuildVersion || !PublishedOccupancyFootprintEdges.SetEquals(OccupancyFootprintEdges);
		if (footprintChanged)
		{
			if (!railGraphSystem.ReportOwnerEdgeFootprint(EntityId, OccupancyFootprintEdges)) return false;
			PublishedOccupancyFootprintEdges.Clear();
			PublishedOccupancyFootprintEdges.UnionWith(OccupancyFootprintEdges);
			PublishedOccupancyGraphVersion = graph.BuildVersion;
		}

		RequestCollisionBodyUpdate(footprintChanged);
		return true;
	}

	private void RequestCollisionBodyUpdate(bool force)
	{
		if (Api?.Side != EnumAppSide.Server || Derailed || IsFollower) { RemovePublishedCollisionBody(); return; }

		long groupID = ConvoyHeadID != 0 ? ConvoyHeadID : EntityId;
		TrainCollisionSystem ??= Api.ModLoader.GetModSystem<RailTrainCollisionSystem>();
		if (PublishedCollisionGroupID != groupID)
		{
			if (PublishedCollisionGroupID != 0) TrainCollisionSystem?.RemovePublishedBody(PublishedCollisionGroupID);
			PublishedCollisionGroupID = groupID;
			ResetCollisionPublicationStamp();
		}

		bool bodyPublished = TrainCollisionSystem?.HasPublishedLoadedBody(groupID, this) == true;
		bool updatePending = TrainCollisionSystem?.HasPendingLoadedBodyUpdate(groupID, this) == true;
		uint topologyRevision = PathTape?.TopologyRevision ?? 0;
		ConvoyStaticStats convoyStatistics = GetConvoyStaticStats();
		int memberCount = convoyStatistics.Count;
		int weight = convoyStatistics.Weight;
		double speedABS = Math.Abs(Speed);
		bool publishedStateMatches =
			bodyPublished &&
			Math.Abs(PublishedCollisionPathHeadDistance - PathHeadDistance) <= 1e-4 &&
			PublishedCollisionTopologyRevision == topologyRevision &&
			PublishedCollisionMemberCount == memberCount &&
			PublishedCollisionWeight == weight &&
			Math.Abs(PublishedCollisionSpeedABS - speedABS) <= 0.1;

		if (!force && publishedStateMatches) return;
		if (updatePending) return;

		TrainCollisionSystem?.RequestLoadedBodyUpdate(this);
	}

	internal void AcknowledgeCollisionBodyPublished(long groupID)
	{
		if (Api?.Side != EnumAppSide.Server || Derailed || IsFollower) return;

		long currentGroupID = ConvoyHeadID != 0 ? ConvoyHeadID : EntityId;
		if (groupID == 0 || groupID != currentGroupID) return;

		ConvoyStaticStats convoyStatistics = GetConvoyStaticStats();
		PublishedCollisionGroupID = groupID;
		PublishedCollisionPathHeadDistance = PathHeadDistance;
		PublishedCollisionTopologyRevision = PathTape?.TopologyRevision ?? 0;
		PublishedCollisionMemberCount = convoyStatistics.Count;
		PublishedCollisionWeight = convoyStatistics.Weight;
		PublishedCollisionSpeedABS = Math.Abs(Speed);
	}

	private void ResetCollisionPublicationStamp()
	{
		PublishedCollisionPathHeadDistance = double.NaN;
		PublishedCollisionTopologyRevision = uint.MaxValue;
		PublishedCollisionMemberCount = -1;
		PublishedCollisionWeight = -1;
		PublishedCollisionSpeedABS = double.NaN;
	}

	private void RemovePublishedCollisionBody()
	{
		ResetCollisionPublicationStamp();
		long groupID = PublishedCollisionGroupID;
		PublishedCollisionGroupID = 0;
		if (groupID == 0) return;
		TrainCollisionSystem ??= Api?.ModLoader.GetModSystem<RailTrainCollisionSystem>();
		TrainCollisionSystem?.RemovePublishedBody(groupID);
	}


	internal bool ServerStopAfterTrainCollision()
	{
		if (Api?.Side != EnumAppSide.Server) return false;
		if (Derailed || IsFollower) return false;

		Speed = 0;

		EntityStandardGaugeLocomotive lead = ResolveConvoyLead();
		lead.SteamEngineBehaviour?.ForceDriveLeverStop();
		lead.SteamEngineBehaviour?.ForceExtinguishBoiler();

		PersistTrackState();
		return true;
	}

	internal bool ServerTryMoveRailRepulsionBody(RailGraphLive graph, double signedHeadDistance, Vec3d desiredAwayDirection, out double moved)
	{
		moved = 0;
		if (Api?.Side != EnumAppSide.Server) return false;
		if (Derailed || IsFollower || RailSystem == null) return false;
		if (Math.Abs(signedHeadDistance) <= 1e-6) return false;

		if ((Cursor.BoundGraphVersion != graph.BuildVersion || Cursor.PolyXYZ16 == null || Cursor.PointCount < 2) && !TryEnsureBound(forceRebind: false)) return false;

		bool movementSucceeded = ServerTryMovePathTapeBySignedDistance(graph, signedHeadDistance, out moved);
		return movementSucceeded;
	}
	internal bool ServerProbeRailRepulsionMove(RailGraphLive graph, double signedDistance, out Vec3d delta)
	{
		delta = new Vec3d();
		if (Api?.Side != EnumAppSide.Server) return false;
		if (Derailed || IsFollower || RailSystem == null) return false;
		if (Math.Abs(signedDistance) <= 1e-6) return false;
		if (!ServerEnsureLoadedConvoyPathTape(forceRebuild: false) || PathTape == null) return false;

		int movementSign = signedDistance >= 0 ? 1 : -1;
		var convoyStatistics = GetConvoyStaticStats();
		double authorityDistance = movementSign >= 0
			? PathHeadDistance + StableSourceFrontExtentBlocks()
			: PathHeadDistance - convoyStatistics.TailDistance - GetTailRearExtentForCurrentConvoy();

		if (!PathTape.TrySampleAuthoritativeCursorForTravel(graph, TrackGauge, authorityDistance, movementSign, out var beforeCursor)) return false;
		if (!TryGetCursorWorldPosition(in beforeCursor, out Vec3d before)) return false;

		double requestedABS = Math.Abs(signedDistance);
		double availableRecordedABS = movementSign > 0
			? Math.Max(0, PathTape.MaxS - authorityDistance)
			: Math.Max(0, authorityDistance - PathTape.MinS);

		double consumedABS = Math.Min(requestedABS, availableRecordedABS);
		double remainingABS = requestedABS - consumedABS;
		double afterDistance = authorityDistance + movementSign * consumedABS;

		RailwayVehicleShared.RailCursor afterCursor;
		if (remainingABS <= 1e-6) { if (!PathTape.TrySampleAuthoritativeCursor(graph, TrackGauge, afterDistance, out afterCursor)) return false; }
		else
		{
			if (!PathTape.TrySampleAuthoritativeCursorForTravel(graph, TrackGauge, afterDistance, movementSign, out afterCursor)) return false;

			double speed = 0;
			double travel = 0;
			bool movementSucceeded = RailwayVehicleShared.AdvanceAlongTrack(
				graph,
				movementSign * remainingABS,
				0,
				null,
				false,
				ref afterCursor,
				ref speed,
				ref travel,
				false,
				out _,
				null
			);
			if (!movementSucceeded && travel <= 1e-8) return false;
		}

		if (!TryGetCursorWorldPosition(in afterCursor, out Vec3d after)) return false;

		delta.Set(after.X - before.X, after.Y - before.Y, after.Z - before.Z);
		return true;
	}

	private static bool TryGetCursorWorldPosition(in RailwayVehicleShared.RailCursor cursor, out Vec3d position)
	{
		position = new Vec3d();
		if (cursor.PolyXYZ16 == null || cursor.PointCount < 2) return false;

		int pointIndex = GameMath.Clamp(cursor.SegmentIndex, 0, cursor.PointCount - 2);
		int nextPointIndex = pointIndex + 1;
		RailwayVehicleShared.GetPoint(cursor.PolyXYZ16, pointIndex, out double ax, out double ay, out double az);
		RailwayVehicleShared.GetPoint(cursor.PolyXYZ16, nextPointIndex, out double bx, out double by, out double bz);

		double segmentProgress = GameMath.Clamp(cursor.NormalizedSegmentProgress, 0.0, 1.0);
		position.Set(ax + (bx - ax) * segmentProgress, ay + (by - ay) * segmentProgress, az + (bz - az) * segmentProgress);
		return true;
	}

	internal bool ServerDerailAfterTrainCollision(double impactSpeedABS)
	{
		if (Api?.Side != EnumAppSide.Server) return false;
		if (Derailed || IsFollower) return false;

		impactSpeedABS = Math.Max(Math.Abs(Speed), Math.Abs(impactSpeedABS));
		if (impactSpeedABS > 0.01 && Math.Abs(Speed) < impactSpeedABS) { Speed = Math.Sign(Speed == 0 ? 1 : Speed) * impactSpeedABS; }

		ServerDerailConvoyMembers(excludeThis: false);
		return true;
	}

	internal bool ServerTryExplodeCollisionBoiler()
	{
		if (Api?.Side != EnumAppSide.Server) return false;
		if (Derailed || IsFollower) return false;

		EntityStandardGaugeLocomotive lead = ResolveConvoyLead();
		if (lead.SteamEngineBehaviour == null) return false;

		lead.SteamEngineBehaviour.ExplodeAndRemove();
		return true;
	}
}
#endregion
