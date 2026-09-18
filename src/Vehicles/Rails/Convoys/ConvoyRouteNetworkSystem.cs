using System;
using System.Collections.Generic;
using System.IO;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace YangTransport;

[ProtoContract]
public sealed class ConvoyRouteRequestPacket
{
	[ProtoMember(1)] public long ConvoyID;
	[ProtoMember(2)] public int Epoch;
	[ProtoMember(3)] public uint Revision;
	[ProtoMember(4)] public bool ResetGeometry;
	[ProtoMember(5)] public ulong GeometryEpoch = 1;
}

public enum ConvoyRouteRecordKind : byte
{
	Snapshot = 1,
	Delta = 2,
	Anchor = 3
}

[ProtoContract]
public sealed class ConvoyRouteRecordPacket
{
	[ProtoMember(1)] public ConvoyRouteRecordKind Kind;
	[ProtoMember(2)] public long ConvoyID;
	[ProtoMember(3)] public int Epoch;
	[ProtoMember(4)] public uint BaseRevision;
	[ProtoMember(5)] public uint Revision;
	[ProtoMember(6)] public double HeadS;
	[ProtoMember(7)] public byte[] Payload = Array.Empty<byte>();
}

[ProtoContract]
public sealed class ConvoyRouteBatchPacket
{
	[ProtoMember(1)] public ulong GeometryEpoch = 1;
	[ProtoMember(2)] public ConvoyRouteRecordPacket[] Records = Array.Empty<ConvoyRouteRecordPacket>();
}

/// Convoy-scoped SG route replication. One shared route replica is maintained per observed convoy, independent of wagon count
/// Route topology is sent only when it changes, entity position interpolation remains Vintage Story's responsibility.
public sealed class ConvoyRouteNetworkSystem : ModSystem
{
	private const string ChannelName = "yangtransport_convoyroute";
	private const long SubscriptionLifetimeMS = 45000;
	private const long ClientRequestIntervalMS = 15000;
	private const long ClientMissingRouteRetryMS = 1000;
	private const long ClientAnchorRequestThrottleMS = 1000;
	private const long BoundaryRoutePublishIntervalMS = 250;
	private const long ServerFlushIntervalMS = 100;
	private const double RouteInterestDistanceBlocks = 512;
	private const double RouteInterestDistanceSquared = RouteInterestDistanceBlocks * RouteInterestDistanceBlocks;
	private const long CleanupIntervalMS = 5000;
	private const long ClientCacheLifetimeMS = 60000;
	private const int MaxKnownGeometryEdgesPerPlayer = 65536;
	private const int RouteBatchSoftBytes = 768 * 1024;
	private const int RouteBatchHardBytes = 2 * 1024 * 1024;
	private const int MaxRouteRecordsPerFlush = 512;
	private const int EstimatedRouteRecordOverheadBytes = 96;
	private const long OversizedRouteRetryMS = 5000;
	private const double OversizedRouteSnapshotWindowBlocks = RouteInterestDistanceBlocks * 2;

	private ICoreServerAPI? ServerAPI;
	private IServerNetworkChannel? ServerChannel;
	private RailGraphServerSystem? RailSystem;
	private readonly Dictionary<long, ServerRouteSource> ServerSources = new();
	private readonly Dictionary<ulong, HashSet<long>> ServerRouteOwnersByEdge = new();
	private readonly Dictionary<long, Dictionary<IServerPlayer, ServerSubscription>> ServerSubscriptions = new();
	private readonly List<long> ConvoyRemovalScratch = new();
	private readonly HashSet<long> AffectedRouteSourcesScratch = new();
	private readonly List<IServerPlayer> PlayerRemovalScratch = new();
	private readonly Dictionary<IServerPlayer, PlayerRouteOutbox> ServerOutboxes = new();
	private long ServerCleanupListenerID;
	private long ServerFlushListenerID;

	private ICoreClientAPI? ClientAPI;
	private IClientNetworkChannel? ClientChannel;
	private readonly Dictionary<long, ClientRouteEntry> ClientRoutes = new();
	private readonly Dictionary<ulong, RailEdgeGeometry> ClientGeometryCache = new();
	private ulong ClientGeometryEpoch = 1;
	private bool ClientGeometryResetPending;
	private long ClientCleanupListenerID;

	public override bool ShouldLoad(EnumAppSide forSide) => true;

	public override void Start(ICoreAPI coreAPI)
	{
		if (coreAPI is ICoreServerAPI serverAPI) StartServer(serverAPI);
		if (coreAPI is ICoreClientAPI clientAPI) StartClient(clientAPI);
	}

	private void StartServer(ICoreServerAPI serverAPI)
	{
		ServerAPI = serverAPI;
		RailSystem = serverAPI.ModLoader.GetModSystem<RailGraphServerSystem>();
		ServerChannel = serverAPI.Network.RegisterChannel(ChannelName)
			.RegisterMessageType<ConvoyRouteRequestPacket>()
			.RegisterMessageType<ConvoyRouteBatchPacket>();

		ServerChannel.SetMessageHandler<ConvoyRouteRequestPacket>(OnRouteRequest);
		serverAPI.Event.PlayerLeave += OnPlayerLeave;
		ServerFlushListenerID = serverAPI.Event.RegisterGameTickListener(OnServerFlush, (int)ServerFlushIntervalMS);
		ServerCleanupListenerID = serverAPI.Event.RegisterGameTickListener(OnServerCleanup, (int)CleanupIntervalMS);
	}

	private void StartClient(ICoreClientAPI clientAPI)
	{
		ClientAPI = clientAPI;
		ClientChannel = clientAPI.Network.RegisterChannel(ChannelName)
			.RegisterMessageType<ConvoyRouteRequestPacket>()
			.RegisterMessageType<ConvoyRouteBatchPacket>();

		ClientChannel.SetMessageHandler<ConvoyRouteBatchPacket>(OnBatch);
		ClientCleanupListenerID = clientAPI.Event.RegisterGameTickListener(OnClientCleanup, (int)CleanupIntervalMS);
	}

	public override void Dispose()
	{
		if (ServerAPI != null)
		{
			ServerAPI.Event.PlayerLeave -= OnPlayerLeave;
			if (ServerCleanupListenerID != 0) ServerAPI.Event.UnregisterGameTickListener(ServerCleanupListenerID);
			if (ServerFlushListenerID != 0) ServerAPI.Event.UnregisterGameTickListener(ServerFlushListenerID);
		}

		if (ClientAPI != null && ClientCleanupListenerID != 0) { ClientAPI.Event.UnregisterGameTickListener(ClientCleanupListenerID); }

		foreach (KeyValuePair<long, ServerRouteSource> pair in ServerSources) { pair.Value.Route.DisableChangeJournal(); RemoveSourceRouteIndex(pair.Key, pair.Value); }
		ServerSources.Clear();
		ServerRouteOwnersByEdge.Clear();
		ServerSubscriptions.Clear();
		ServerOutboxes.Clear();
		ClientRoutes.Clear();
		ClientGeometryCache.Clear();
		ClientGeometryEpoch = 1;
		ClientGeometryResetPending = false;
		base.Dispose();
	}

	internal void PublishServerRoute(long convoyID, EntityStandardGaugeLocomotive owner, ConvoyRoute route, double headS, bool forceSnapshot = false)
	{
		if (ServerAPI == null || ServerChannel == null || convoyID == 0 || owner == null || route == null || route.Count == 0) return;

		bool sourceReplaced = false;
		if (!ServerSources.TryGetValue(convoyID, out ServerRouteSource? source) || !ReferenceEquals(source.Owner, owner) || !ReferenceEquals(source.Route, route))
		{
			if (source != null)
			{
				source.Route.DisableChangeJournal();
				RemoveSourceRouteIndex(convoyID, source);
			}
			source = new ServerRouteSource(owner, route, headS);
			ServerSources[convoyID] = source;
			sourceReplaced = true;
		}
		else
		{
			source.HeadS = headS;
		}

		UpdateSourceRouteIndex(convoyID, source);

		bool graphChanged = source.LastObservedGraphVersion != route.GraphVersion;
		bool epochChanged = source.LastObservedEpoch != route.Epoch;
		bool revisionChanged = source.LastObservedRevision != route.Revision;
		bool structureChanged = source.LastObservedTopologyRevision != route.TopologyRevision;
		bool routeChanged = forceSnapshot || sourceReplaced || epochChanged || revisionChanged;
		if (!routeChanged)
		{
			// Stable edge hashes make an unrelated graph revision irrelevant to the replica.
			// The route validates lazily if it is sampled, no packet or full route walk is needed. Hopefully safe enough.
			if (graphChanged) source.LastObservedGraphVersion = route.GraphVersion;
			return;
		}

		if (!ServerSubscriptions.TryGetValue(convoyID, out Dictionary<IServerPlayer, ServerSubscription>? subscribers) || subscribers.Count == 0)
		{
			source.Observe(route);
			route.DisableChangeJournal();
			return;
		}

		route.EnableChangeJournal();
		long now = ServerAPI.World.ElapsedMilliseconds;
		
		// Same-run boundary growth is coalesced in ConvoyRoute. Cap its publication rate.
		// Edge-sequence changes, graph changes, and forced snapshots still publish immediately.
		if (!forceSnapshot && !sourceReplaced && !epochChanged && !structureChanged && now < source.LastPublishMS + BoundaryRoutePublishIntervalMS) { return; }

		source.Observe(route);
		source.LastPublishMS = now;
		PlayerRemovalScratch.Clear();

		foreach (KeyValuePair<IServerPlayer, ServerSubscription> pair in subscribers)
		{
			IServerPlayer player = pair.Key;
			ServerSubscription subscription = pair.Value;

			if (player == null || player.ConnectionState != EnumClientState.Playing || subscription.ExpiresAtMS < now) { PlayerRemovalScratch.Add(player); continue; }
			QueueRouteUpdate(player, convoyID, forceSnapshot || sourceReplaced);
		}

		for (int iteration = 0; iteration < PlayerRemovalScratch.Count; iteration++) subscribers.Remove(PlayerRemovalScratch[iteration]);
		if (subscribers.Count == 0)
		{
			ServerSubscriptions.Remove(convoyID);
			route.DisableChangeJournal();
		}
	}

	internal bool HasServerSubscribers(long convoyID)
	{
		return convoyID != 0 && ServerSubscriptions.TryGetValue(convoyID, out Dictionary<IServerPlayer, ServerSubscription>? subscribers) && subscribers.Count > 0;
	}

	internal void OnRailGraphChanged(RailGraphLive graph, RailGraphChangeSet change)
	{
		if (graph == null || change == null) return;
		RailEdgeGeometryCache.Invalidate(graph, change);

		AffectedRouteSourcesScratch.Clear();
		if (change.GlobalInvalidation) { foreach (long convoyID in ServerSources.Keys) AffectedRouteSourcesScratch.Add(convoyID); }
		else
		{
			for (int iteration = 0; iteration < change.TouchedEdges.Count; iteration++)
			{
				if (!ServerRouteOwnersByEdge.TryGetValue(change.TouchedEdges[iteration], out HashSet<long>? owners)) continue;
				foreach (long convoyID in owners) AffectedRouteSourcesScratch.Add(convoyID);
			}
		}

		foreach (long convoyID in AffectedRouteSourcesScratch)
		{
			if (!ServerSources.TryGetValue(convoyID, out ServerRouteSource? source)) continue;
			source.Route.RequireAuthoritativeValidation();
			source.Owner.ServerMarkConvoyBodyDirty();
		}
		AffectedRouteSourcesScratch.Clear();
	}


	internal void RemoveServerRoute(long convoyID, EntityStandardGaugeLocomotive owner)
	{
		if (!ServerSources.TryGetValue(convoyID, out ServerRouteSource? source)) return;
		if (!ReferenceEquals(source.Owner, owner)) return;
		source.Route.DisableChangeJournal();
		RemoveSourceRouteIndex(convoyID, source);
		ServerSources.Remove(convoyID);
		ServerSubscriptions.Remove(convoyID);
	}

	private void OnRouteRequest(IServerPlayer fromPlayer, ConvoyRouteRequestPacket request)
	{
		if (ServerAPI == null || ServerChannel == null || fromPlayer == null || request == null || request.ConvoyID == 0) return;
		if (!TryResolveServerRoute(request.ConvoyID, out ServerRouteSource source) || !IsPlayerInterested(fromPlayer, source.Owner)) { return; }

		PlayerRouteOutbox outbox = GetOrCreateOutbox(fromPlayer);
		if (!outbox.SynchronizeGeometryEpoch( request.GeometryEpoch, request.ResetGeometry, out bool resetPendingGeometry, out bool forceGeometrySnapshot)) { return; }

		if (!ServerSubscriptions.TryGetValue(request.ConvoyID, out Dictionary<IServerPlayer, ServerSubscription>? subscribers))
		{
			subscribers = new Dictionary<IServerPlayer, ServerSubscription>();
			ServerSubscriptions[request.ConvoyID] = subscribers;
		}

		bool newSubscription = !subscribers.TryGetValue(fromPlayer, out ServerSubscription? subscription);
		if (newSubscription)
		{
			subscription = new ServerSubscription
			{
				Epoch = request.Epoch,
				Revision = request.Revision
			};
			subscribers[fromPlayer] = subscription;
		}

		subscription!.ExpiresAtMS = ServerAPI.World.ElapsedMilliseconds + SubscriptionLifetimeMS;
		source.Route.EnableChangeJournal();

		// An actual epoch transition invalidates the shared geometry baseline. Every already-pending record must therefore become self-contained.
		if (resetPendingGeometry)	{ outbox.PromotePendingToSnapshots(); }
		if (forceGeometrySnapshot)	{ QueueRouteUpdate(fromPlayer, request.ConvoyID, forceSnapshot: true); return; }

		if (newSubscription) { QueueRouteUpdate(fromPlayer, request.ConvoyID, forceSnapshot: true); return; }
		if (subscription.Epoch != request.Epoch || subscription.Revision != request.Revision)
		{
			subscription.Epoch = request.Epoch;
			subscription.Revision = request.Revision;
			QueueRouteUpdate(fromPlayer, request.ConvoyID, forceSnapshot: true);
			return;
		}

		QueueRouteUpdate(fromPlayer, request.ConvoyID, forceSnapshot: false);
	}

	private bool TryResolveServerRoute(long convoyID, out ServerRouteSource source)
	{
		source = null!;

		EntityStandardGaugeLocomotive? owner = null;
		if (ServerSources.TryGetValue(convoyID, out ServerRouteSource? existing) && existing.Owner.Alive) { owner = existing.Owner; }
		else { owner = ServerAPI?.World.GetEntityById(convoyID) as EntityStandardGaugeLocomotive; }

		if (owner != null && owner.ServerTryGetReplicatedConvoyRoute(out ConvoyRoute route, out double headS))
		{
			if (!ServerSources.TryGetValue(convoyID, out source) || !ReferenceEquals(source.Owner, owner) || !ReferenceEquals(source.Route, route))
			{
				if (source != null)
				{
					source.Route.DisableChangeJournal();
					RemoveSourceRouteIndex(convoyID, source);
				}
				source = new ServerRouteSource(owner, route, headS);
				ServerSources[convoyID] = source;
			}
			else { source.HeadS = headS; }
			UpdateSourceRouteIndex(convoyID, source);
			return true;
		}

		if (ServerSources.Remove(convoyID, out ServerRouteSource? staleSource))
		{
			staleSource.Route.DisableChangeJournal();
			RemoveSourceRouteIndex(convoyID, staleSource);
		}
		ServerSubscriptions.Remove(convoyID);
		return false;
	}

	private void QueueRouteUpdate(IServerPlayer player, long convoyID, bool forceSnapshot)
	{
		if (player == null || convoyID == 0 || player.ConnectionState != EnumClientState.Playing) return;
		if (!ServerSources.TryGetValue(convoyID, out ServerRouteSource? source) || !IsPlayerInterested(player, source.Owner)) return;

		GetOrCreateOutbox(player).Queue(convoyID, forceSnapshot);
	}

	private PlayerRouteOutbox GetOrCreateOutbox(IServerPlayer player)
	{
		if (!ServerOutboxes.TryGetValue(player, out PlayerRouteOutbox? outbox))
		{
			outbox = new PlayerRouteOutbox();
			ServerOutboxes[player] = outbox;
		}
		return outbox;
	}

	private void OnServerFlush(float deltaTime)
	{
		if (ServerAPI == null || ServerChannel == null || ServerOutboxes.Count == 0) return;

		long now = ServerAPI.World.ElapsedMilliseconds;
		PlayerRemovalScratch.Clear();
		foreach (KeyValuePair<IServerPlayer, PlayerRouteOutbox> pair in ServerOutboxes)
		{
			IServerPlayer player = pair.Key;
			PlayerRouteOutbox outbox = pair.Value;
			if (player == null || player.ConnectionState != EnumClientState.Playing) { PlayerRemovalScratch.Add(player); continue; }

			if (outbox.PendingCount == 0) continue;

			outbox.BeginFlush();
			try
			{
				int estimatedBatchBytes = EstimatedRouteRecordOverheadBytes;
				LinkedListNode<long>? node = outbox.PendingOrder.First;

				while (node != null && outbox.Prepared.Count < MaxRouteRecordsPerFlush)
				{
					LinkedListNode<long>? next = node.Next;
					long convoyID = node.Value;

					if (!outbox.Pending.TryGetValue(convoyID, out bool forceSnapshot)) { outbox.RemovePending(convoyID); node = next; continue; }
					if (outbox.IsOversizedRetryThrottled(convoyID, now)) { node = next; continue; }
					if 
					(
						!ServerSources.TryGetValue(convoyID, out ServerRouteSource? source) ||
						!ServerSubscriptions.TryGetValue(convoyID, out Dictionary<IServerPlayer, ServerSubscription>? subscribers) ||
						!subscribers.TryGetValue(player, out ServerSubscription? subscription) || !IsPlayerInterested(player, source.Owner)
					) { outbox.RemovePending(convoyID); node = next; continue; }

					if (!TryPrepareCurrentRoute(convoyID, source, subscription, forceSnapshot, outbox, out PreparedRouteDelivery delivery, out bool oversizedDeferred))
					{
						if (oversizedDeferred) { outbox.DeferOversized(convoyID, now + OversizedRouteRetryMS); source.Owner.ServerMarkConvoyBodyDirty(); }
						else { outbox.RemovePending(convoyID); }
						node = next;
						continue;
					}

					int nextEstimatedBytes = estimatedBatchBytes + delivery.EstimatedBytes;
					if (outbox.Prepared.Count > 0 && nextEstimatedBytes > RouteBatchSoftBytes) break;

					outbox.AcceptPrepared(delivery);
					estimatedBatchBytes = nextEstimatedBytes;
					node = next;

					if (estimatedBatchBytes >= RouteBatchSoftBytes) break;
				}

				if (outbox.Prepared.Count == 0) continue;

				int sendCount = outbox.Prepared.Count;
				ConvoyRouteBatchPacket packet;
				byte[] serialized;
				try
				{
					while (true)
					{
						packet = BuildBatchPacket(outbox.Prepared, sendCount, outbox.GeometryEpoch);
						serialized = SerializeBatchPacket(packet);
						if (serialized.Length <= RouteBatchHardBytes || sendCount == 1) break;
						sendCount = Math.Max(1, sendCount / 2);
					}
				}
				catch (Exception exception) { ServerAPI.Logger.Error(exception); continue; }

				if (serialized.Length > RouteBatchHardBytes)
				{
					long convoyID = outbox.Prepared[0].ConvoyID;
					ServerAPI.Logger.Error(
						"[YangTransport] Atomic convoy route update {0} is {1} bytes, above the {2}-byte hard limit. " +
						"The update was deferred and will retry as a smaller route window.",
						convoyID,
						serialized.Length,
						RouteBatchHardBytes);
					outbox.DeferOversized(convoyID, now + OversizedRouteRetryMS);
					if (ServerSources.TryGetValue(convoyID, out ServerRouteSource? oversizedSource)) oversizedSource.Owner.ServerMarkConvoyBodyDirty();
					continue;
				}

				if (outbox.WouldExceedGeometryBudget(sendCount))
				{
					outbox.PromotePendingToSnapshots();
					outbox.StartNextGeometryEpoch();
					continue;
				}

				try
				{
					// Normal Vintage Story custom channels use reliable ordered TCP. Supplying the measured bytes avoids a second protobuf serialization pass.
					ServerChannel.SendPacket(packet, serialized, player);
				}
				catch (Exception exception) { ServerAPI.Logger.Error(exception); continue; }

				outbox.CommitDeliveries(sendCount);
			}
			finally
			{
				outbox.CommitFlushGeometry();
				outbox.ClearPrepared();
			}
		}

		for (int iteration = 0; iteration < PlayerRemovalScratch.Count; iteration++) ServerOutboxes.Remove(PlayerRemovalScratch[iteration]);
		PlayerRemovalScratch.Clear();
	}

	private bool TryPrepareCurrentRoute
	(
		long convoyID, ServerRouteSource source, ServerSubscription subscription, bool forceSnapshot,
		PlayerRouteOutbox outbox, out PreparedRouteDelivery delivery, out bool oversizedDeferred
	)
	{
		delivery = null!;
		oversizedDeferred = false;
		RailSystem ??= ServerAPI?.ModLoader.GetModSystem<RailGraphServerSystem>();
		RailGraphLive? graph = RailSystem?.Graph;
		if (graph == null) return false;

		ConvoyRoute route = source.Route;
		if (!route.ValidateAuthoritative(graph, route.Gauge)) { source.Owner.ServerMarkConvoyBodyDirty(); return false; }

		outbox.CandidateGeometryEdges.Clear();
		try
		{
			if (!forceSnapshot && subscription.Epoch == route.Epoch && subscription.Revision == route.Revision)
			{
				delivery = PreparedRouteDelivery.Create(
					new ConvoyRouteRecordPacket
					{
						Kind = ConvoyRouteRecordKind.Anchor,
						ConvoyID = convoyID,
						Epoch = route.Epoch,
						Revision = route.Revision,
						HeadS = source.HeadS
					},
					subscription,
					route,
					Array.Empty<ulong>());
				return true;
			}

			if
			(
				!forceSnapshot && subscription.Epoch == route.Epoch &&
				route.TrySerializeDelta(subscription.Revision, graph, outbox.TentativeGeometryEdges, out byte[] deltaPayload, outbox.CandidateGeometryEdges) &&
				deltaPayload.Length > 0
			)
			{
				if (deltaPayload.Length + EstimatedRouteRecordOverheadBytes > RouteBatchHardBytes)
				{
					return TryPrepareWindowSnapshot(convoyID, source, subscription, outbox, graph, out delivery, out oversizedDeferred);
				}

				delivery = PreparedRouteDelivery.Create
				(
					new ConvoyRouteRecordPacket
					{
						Kind = ConvoyRouteRecordKind.Delta,
						ConvoyID = convoyID,
						Epoch = route.Epoch,
						BaseRevision = subscription.Revision,
						Revision = route.Revision,
						Payload = deltaPayload,
						HeadS = source.HeadS
					},
					subscription, route, CopyGeometryEdges(outbox.CandidateGeometryEdges)
				);
			}
			else
			{
				byte[] snapshotPayload = route.SerializeSnapshot( graph, outbox.TentativeGeometryEdges, outbox.CandidateGeometryEdges);
				if (snapshotPayload.Length + EstimatedRouteRecordOverheadBytes > RouteBatchHardBytes)
				{
					return TryPrepareWindowSnapshot(convoyID, source, subscription, outbox, graph, out delivery, out oversizedDeferred);
				}

				delivery = PreparedRouteDelivery.Create
				(
					new ConvoyRouteRecordPacket
					{
						Kind = ConvoyRouteRecordKind.Snapshot,
						ConvoyID = convoyID,
						Epoch = route.Epoch,
						Revision = route.Revision,
						Payload = snapshotPayload,
						HeadS = source.HeadS
					},
					subscription, route, CopyGeometryEdges(outbox.CandidateGeometryEdges)
				);
			}

			return true;
		}
		catch (Exception exception)
		{
			ServerAPI?.Logger.Error(exception);
			source.Owner.ServerMarkConvoyBodyDirty();
			delivery = null!;
			return false;
		}
		finally { outbox.CandidateGeometryEdges.Clear(); }
	}

	private bool TryPrepareWindowSnapshot
	(
		long convoyID, ServerRouteSource source, ServerSubscription subscription, PlayerRouteOutbox outbox,
		RailGraphLive graph, out PreparedRouteDelivery delivery, out bool oversizedDeferred
	)
	{
		delivery = null!;
		oversizedDeferred = true;

		double windowBlocks = OversizedRouteSnapshotWindowBlocks;
		for (int attempt = 0; attempt < 5; attempt++, windowBlocks *= 0.5)
		{
			outbox.CandidateGeometryEdges.Clear();
			ConvoyRoute window = source.Route.CloneSnapshotWindow(source.HeadS - windowBlocks, source.HeadS + windowBlocks);
			if (window.Count == 0) return false;

			byte[] payload = window.SerializeSnapshot(graph, outbox.TentativeGeometryEdges, outbox.CandidateGeometryEdges);
			if (payload.Length + EstimatedRouteRecordOverheadBytes > RouteBatchHardBytes) continue;

			delivery = PreparedRouteDelivery.Create
			(
				new ConvoyRouteRecordPacket
				{
					Kind = ConvoyRouteRecordKind.Snapshot,
					ConvoyID = convoyID,
					Epoch = source.Route.Epoch,
					Revision = source.Route.Revision,
					Payload = payload,
					HeadS = source.HeadS
				},
				subscription, source.Route, CopyGeometryEdges(outbox.CandidateGeometryEdges)
			);
			oversizedDeferred = false;
			return true;
		}

		return false;
	}

	private static ulong[] CopyGeometryEdges(HashSet<ulong> source)
	{
		if (source.Count == 0) return Array.Empty<ulong>();
		var result = new ulong[source.Count];
		source.CopyTo(result);
		return result;
	}

	private static ConvoyRouteBatchPacket BuildBatchPacket(List<PreparedRouteDelivery> deliveries, int count, ulong geometryEpoch)
	{
		count = Math.Min(count, deliveries.Count);
		var records = new ConvoyRouteRecordPacket[count];
		for (int iteration = 0; iteration < count; iteration++) records[iteration] = deliveries[iteration].Record;

		return new ConvoyRouteBatchPacket
		{
			GeometryEpoch = geometryEpoch,
			Records = records
		};
	}

	private static byte[] SerializeBatchPacket(ConvoyRouteBatchPacket packet)
	{
		using var stream = new MemoryStream();
		Serializer.Serialize(stream, packet);
		return stream.ToArray();
	}

	private static bool IsPlayerInterested(IServerPlayer player, EntityStandardGaugeLocomotive owner)
	{
		if (player?.Entity == null || owner == null || !owner.Alive) return false;
		if (player.Entity.Pos.AsBlockPos.dimension != owner.Pos.AsBlockPos.dimension) return false;
		double dx = player.Entity.Pos.X - owner.Pos.X;
		double dy = player.Entity.Pos.Y - owner.Pos.Y;
		double dz = player.Entity.Pos.Z - owner.Pos.Z;
		return dx * dx + dy * dy + dz * dz <= RouteInterestDistanceSquared;
	}

	private void OnPlayerLeave(IServerPlayer player)
	{
		ServerOutboxes.Remove(player);
		ConvoyRemovalScratch.Clear();

		foreach (KeyValuePair<long, Dictionary<IServerPlayer, ServerSubscription>> pair in ServerSubscriptions)
		{
			pair.Value.Remove(player);
			if (pair.Value.Count == 0) ConvoyRemovalScratch.Add(pair.Key);
		}

		for (int iteration = 0; iteration < ConvoyRemovalScratch.Count; iteration++)
		{
			long convoyID = ConvoyRemovalScratch[iteration];
			ServerSubscriptions.Remove(convoyID);
			DisableRouteJournal(convoyID);
		}
	}

	private void OnServerCleanup(float deltaTime)
	{
		if (ServerAPI == null) return;
		long now = ServerAPI.World.ElapsedMilliseconds;
		ConvoyRemovalScratch.Clear();

		foreach (KeyValuePair<long, Dictionary<IServerPlayer, ServerSubscription>> pair in ServerSubscriptions)
		{
			PlayerRemovalScratch.Clear();
			foreach (KeyValuePair<IServerPlayer, ServerSubscription> subscriber in pair.Value)
			{
				if (subscriber.Key == null || subscriber.Key.ConnectionState != EnumClientState.Playing || subscriber.Value.ExpiresAtMS < now)
				{
					PlayerRemovalScratch.Add(subscriber.Key);
				}
			}

			for (int iteration = 0; iteration < PlayerRemovalScratch.Count; iteration++) pair.Value.Remove(PlayerRemovalScratch[iteration]);
			if (pair.Value.Count == 0) ConvoyRemovalScratch.Add(pair.Key);
		}

		for (int iteration = 0; iteration < ConvoyRemovalScratch.Count; iteration++)
		{
			long convoyID = ConvoyRemovalScratch[iteration];
			ServerSubscriptions.Remove(convoyID);
			DisableRouteJournal(convoyID);
		}

		ConvoyRemovalScratch.Clear();
		foreach (KeyValuePair<long, ServerRouteSource> pair in ServerSources) { if (!pair.Value.Owner.Alive) ConvoyRemovalScratch.Add(pair.Key); }
		for (int iteration = 0; iteration < ConvoyRemovalScratch.Count; iteration++)
		{
			long convoyID = ConvoyRemovalScratch[iteration];
			if (ServerSources.Remove(convoyID, out ServerRouteSource? deadSource))
			{
				deadSource.Route.DisableChangeJournal();
				RemoveSourceRouteIndex(convoyID, deadSource);
			}
			ServerSubscriptions.Remove(convoyID);
		}
	}

	private void UpdateSourceRouteIndex(long convoyID, ServerRouteSource source)
	{
		if (source.IndexedTopologyRevision == source.Route.TopologyRevision) return;

		RemoveSourceRouteIndex(convoyID, source);
		source.Route.CollectUniqueEdgeHashes(source.IndexedEdges);

		foreach (ulong edgeHash in source.IndexedEdges)
		{
			if (!ServerRouteOwnersByEdge.TryGetValue(edgeHash, out HashSet<long>? owners))
			{
				owners = new HashSet<long>();
				ServerRouteOwnersByEdge[edgeHash] = owners;
			}
			owners.Add(convoyID);
		}

		source.IndexedTopologyRevision = source.Route.TopologyRevision;
	}

	private void RemoveSourceRouteIndex(long convoyID, ServerRouteSource source)
	{
		foreach (ulong edgeHash in source.IndexedEdges)
		{
			if (!ServerRouteOwnersByEdge.TryGetValue(edgeHash, out HashSet<long>? owners)) continue;
			owners.Remove(convoyID);
			if (owners.Count == 0) ServerRouteOwnersByEdge.Remove(edgeHash);
		}
		source.IndexedEdges.Clear();
		source.IndexedTopologyRevision = uint.MaxValue;
	}

	private void DisableRouteJournal(long convoyID)
	{
		if (ServerSources.TryGetValue(convoyID, out ServerRouteSource? source)) { source.Route.DisableChangeJournal(); }
	}

	internal bool TryGetClientRoute(long convoyID, int expectedEpoch, bool requireFreshAnchor, ref long requiredAnchorGeneration, out ConvoyRoute route, out double headSAnchor)
	{
		route = null!;
		headSAnchor = 0;
		if (ClientAPI == null || ClientChannel == null || convoyID == 0) return false;

		long now = ClientAPI.World.ElapsedMilliseconds;
		if (!ClientRoutes.TryGetValue(convoyID, out ClientRouteEntry? entry))
		{
			entry = new ClientRouteEntry();
			ClientRoutes[convoyID] = entry;
		}

		entry.LastUsedMS = now;

		bool usable = entry.Route != null && entry.Route.Count > 0 && (expectedEpoch <= 0 || entry.Route.Epoch == expectedEpoch);

		if (requireFreshAnchor && requiredAnchorGeneration == 0) { requiredAnchorGeneration = entry.AnchorGeneration + 1; }

		if (!usable)
		{
			RequestClientRoute(convoyID, expectedEpoch, entry, now, ClientMissingRouteRetryMS);
			return false;
		}

		if (entry.AnchorGeneration > 0 && (entry.HeadSAnchor < entry.Route!.MinS - 1 || entry.HeadSAnchor > entry.Route.MaxS + 1))
		{
			RequestClientRoute(convoyID, expectedEpoch, entry, now, ClientMissingRouteRetryMS, resetGeometry: true);
			return false;
		}

		if (requireFreshAnchor && entry.AnchorGeneration < requiredAnchorGeneration) { RequestClientAnchor(convoyID, expectedEpoch, entry, now); return false; }

		if (now >= entry.NextRequestMS) { RequestClientRoute(convoyID, expectedEpoch, entry, now, ClientRequestIntervalMS); }

		route = entry.Route!;
		headSAnchor = entry.HeadSAnchor;
		return true;
	}

	private void RequestClientRoute(long convoyID, int expectedEpoch, ClientRouteEntry entry, long now, long retryIntervalMS, bool resetGeometry = false)
	{
		if (ClientChannel == null || !ClientChannel.Connected || now < entry.NextRequestMS) return;

		int epoch = entry.Route?.Epoch ?? expectedEpoch;
		uint revision = entry.Route?.Revision ?? 0;
		ClientChannel.SendPacket(new ConvoyRouteRequestPacket
		{
			ConvoyID = convoyID,
			Epoch = epoch,
			Revision = revision,
			ResetGeometry = resetGeometry || ClientGeometryResetPending,
			GeometryEpoch = ClientGeometryEpoch
		});

		entry.NextRequestMS = now + Math.Max(250, retryIntervalMS);
	}

	private void RequestClientAnchor(long convoyID, int expectedEpoch, ClientRouteEntry entry, long now)
	{
		if (ClientChannel == null || !ClientChannel.Connected || now < entry.NextAnchorRequestMS) return;

		ClientChannel.SendPacket(new ConvoyRouteRequestPacket
		{
			ConvoyID = convoyID,
			Epoch = entry.Route?.Epoch ?? expectedEpoch,
			Revision = entry.Route?.Revision ?? 0,
			ResetGeometry = ClientGeometryResetPending,
			GeometryEpoch = ClientGeometryEpoch
		});

		entry.NextAnchorRequestMS = now + ClientAnchorRequestThrottleMS;
	}

	private void OnSnapshotCore(ConvoyRouteRecordPacket packet, ulong geometryEpoch)
	{
		if (ClientAPI == null || packet == null || packet.ConvoyID == 0) return;
		long now = ClientAPI.World.ElapsedMilliseconds;

		if (ClientRoutes.TryGetValue(packet.ConvoyID, out ClientRouteEntry? existingEntry) && existingEntry.Route != null && existingEntry.Route.Epoch == packet.Epoch)
		{
			if (existingEntry.Route.Revision > packet.Revision) return;
			if (existingEntry.Route.Revision == packet.Revision && existingEntry.GeometryEpoch == geometryEpoch)
			{
				existingEntry.HeadSAnchor = packet.HeadS;
				existingEntry.AnchorGeneration++;
				existingEntry.LastUsedMS = now;
				existingEntry.NextRequestMS = now + ClientRequestIntervalMS;
				ClientGeometryResetPending = false;
				return;
			}
		}

		if (!ConvoyRoute.TryDeserializeSnapshot(packet.Payload, ClientGeometryCache, out ConvoyRoute route) || route.Epoch != packet.Epoch || route.Revision != packet.Revision)
		{
			if (!ClientRoutes.TryGetValue(packet.ConvoyID, out ClientRouteEntry? failedEntry))
			{
				failedEntry = new ClientRouteEntry();
				ClientRoutes[packet.ConvoyID] = failedEntry;
			}
			failedEntry.Route = null;
			failedEntry.LastUsedMS = now;
			BeginClientGeometryReset(packet.ConvoyID, packet.Epoch, failedEntry, now);
			return;
		}

		if (!ClientRoutes.TryGetValue(packet.ConvoyID, out ClientRouteEntry? entry))
		{
			entry = new ClientRouteEntry();
			ClientRoutes[packet.ConvoyID] = entry;
		}

		entry.Route = route;
		entry.GeometryEpoch = geometryEpoch;
		entry.HeadSAnchor = packet.HeadS;
		entry.AnchorGeneration++;
		entry.LastUsedMS = now;
		entry.NextRequestMS = now + ClientRequestIntervalMS;
		ClientGeometryResetPending = false;
	}

	private void OnDeltaCore(ConvoyRouteRecordPacket packet, ulong geometryEpoch)
	{
		if (ClientAPI == null || packet == null || packet.ConvoyID == 0) return;

		ClientRoutes.TryGetValue(packet.ConvoyID, out ClientRouteEntry? entry);
		if (entry?.Route != null && entry.Route.Epoch == packet.Epoch)
		{
			if (entry.Route.Revision > packet.Revision) return;
			if (entry.Route.Revision == packet.Revision && entry.GeometryEpoch == geometryEpoch)
			{
				long duplicateNow = ClientAPI.World.ElapsedMilliseconds;
				entry.HeadSAnchor = packet.HeadS;
				entry.LastUsedMS = duplicateNow;
				entry.AnchorGeneration++;
				entry.NextRequestMS = duplicateNow + ClientRequestIntervalMS;
				ClientGeometryResetPending = false;
				return;
			}
		}

		if
		(
			entry == null || entry.Route == null || entry.Route.Epoch != packet.Epoch || entry.Route.Revision != packet.BaseRevision ||
			!entry.Route.TryApplyDelta(packet.Payload, ClientGeometryCache) || entry.Route.Revision != packet.Revision
		)
		{
			entry ??= new ClientRouteEntry();
			ClientRoutes[packet.ConvoyID] = entry;
			long now = ClientAPI.World.ElapsedMilliseconds;
			entry.Route = null;
			entry.LastUsedMS = now;
			BeginClientGeometryReset(packet.ConvoyID, packet.Epoch, entry, now);
			return;
		}

		entry.GeometryEpoch = geometryEpoch;
		entry.HeadSAnchor = packet.HeadS;
		entry.LastUsedMS = ClientAPI.World.ElapsedMilliseconds;
		entry.AnchorGeneration++;
		entry.NextRequestMS = entry.LastUsedMS + ClientRequestIntervalMS;
		ClientGeometryResetPending = false;
	}

	private void OnAnchorCore(ConvoyRouteRecordPacket packet, ulong geometryEpoch)
	{
		if (ClientAPI == null || packet == null || packet.ConvoyID == 0) return;
		if
		(
			!ClientRoutes.TryGetValue(packet.ConvoyID, out ClientRouteEntry? entry) ||
			entry.Route == null || entry.Route.Epoch != packet.Epoch || entry.Route.Revision != packet.Revision
		) { return; }

		long now = ClientAPI.World.ElapsedMilliseconds;
		entry.GeometryEpoch = geometryEpoch;
		entry.HeadSAnchor = packet.HeadS;
		entry.AnchorGeneration++;
		entry.LastUsedMS = now;
		entry.NextRequestMS = now + ClientRequestIntervalMS;
		ClientGeometryResetPending = false;
	}

	private bool TryAcceptClientGeometryEpoch(ulong packetEpoch)
	{
		if (packetEpoch == 0 || packetEpoch < ClientGeometryEpoch) return false;

		// A reset request is a server-owned epoch transition.
		// Ignore every packet from the old epoch until the server publishes the next one.
		// this makes concurrent route requests and already queued packets harmless by design.
		if (ClientGeometryResetPending && packetEpoch == ClientGeometryEpoch) return false;

		if (packetEpoch == ClientGeometryEpoch) return true;

		// Active routes own their currently referenced geometry, so changing the shared cross-route cache epoch cannot invalidate a rendered replica.
		ClientGeometryEpoch = packetEpoch;
		ClientGeometryCache.Clear();
		ClientGeometryResetPending = false;
		return true;
	}

	private void OnBatch(ConvoyRouteBatchPacket packet)
	{
		if (packet == null || !TryAcceptClientGeometryEpoch(packet.GeometryEpoch)) return;

		ConvoyRouteRecordPacket[] records = packet.Records ?? Array.Empty<ConvoyRouteRecordPacket>();
		if (records.Length > MaxRouteRecordsPerFlush) return;
		for (int iteration = 0; iteration < records.Length; iteration++)
		{
			ConvoyRouteRecordPacket record = records[iteration];
			if (record == null || record.ConvoyID == 0) continue;
			record.Payload ??= Array.Empty<byte>();

			switch (record.Kind)
			{
				case ConvoyRouteRecordKind.Snapshot:
					OnSnapshotCore(record, packet.GeometryEpoch);
				break;

				case ConvoyRouteRecordKind.Delta:
					OnDeltaCore(record, packet.GeometryEpoch);
				break;

				case ConvoyRouteRecordKind.Anchor:
					OnAnchorCore(record, packet.GeometryEpoch);
				break;

				default: continue;
			}

			// Snapshot/delta failure clears shared geometry and requests a new epoch.
			// Later records may depend on geometry introduced earlier in this packet, so none of them are safe to apply after that reset.
			if (ClientGeometryResetPending) return;
		}
	}

	private void BeginClientGeometryReset(long convoyID, int expectedRouteEpoch, ClientRouteEntry entry, long now)
	{
		ClientGeometryCache.Clear();
		ClientGeometryResetPending = true;
		entry.NextRequestMS = 0;

		if (convoyID != 0) { RequestClientRoute(convoyID, expectedRouteEpoch, entry, now, ClientMissingRouteRetryMS, resetGeometry: true); }
	}

	private void OnClientCleanup(float deltaTime)
	{
		if (ClientAPI == null) return;
		long now = ClientAPI.World.ElapsedMilliseconds;
		ConvoyRemovalScratch.Clear();

		foreach (KeyValuePair<long, ClientRouteEntry> pair in ClientRoutes) { if (pair.Value.LastUsedMS + ClientCacheLifetimeMS < now) ConvoyRemovalScratch.Add(pair.Key); }
		for (int iteration = 0; iteration < ConvoyRemovalScratch.Count; iteration++) ClientRoutes.Remove(ConvoyRemovalScratch[iteration]);

		if (ClientRoutes.Count == 0 && ClientGeometryCache.Count != 0)
		{
			ClientGeometryCache.Clear();
			ClientGeometryResetPending = true;
		}
	}

	private sealed class PreparedRouteDelivery
	{
		internal readonly long ConvoyID;
		internal readonly ConvoyRouteRecordPacket Record;
		internal readonly ServerSubscription Subscription;
		internal readonly ConvoyRoute Route;
		internal readonly int Epoch;
		internal readonly uint Revision;
		internal readonly ulong[] NewGeometryEdges;
		internal readonly int EstimatedBytes;

		private PreparedRouteDelivery(ConvoyRouteRecordPacket record, ServerSubscription subscription, ConvoyRoute route, ulong[] newGeometryEdges)
		{
			ConvoyID = record.ConvoyID;
			Record = record;
			Subscription = subscription;
			Route = route;
			Epoch = route.Epoch;
			Revision = route.Revision;
			NewGeometryEdges = newGeometryEdges;
			EstimatedBytes = EstimatedRouteRecordOverheadBytes + Math.Max(0, record.Payload?.Length ?? 0);
		}

		internal static PreparedRouteDelivery Create(ConvoyRouteRecordPacket record, ServerSubscription subscription, ConvoyRoute route, ulong[] newGeometryEdges)
		{
			return new PreparedRouteDelivery(record, subscription, route, newGeometryEdges);
		}
	}

	private sealed class PlayerRouteOutbox
	{
		internal readonly Dictionary<long, bool> Pending = new();
		internal readonly LinkedList<long> PendingOrder = new();
		internal readonly List<PreparedRouteDelivery> Prepared = new();
		internal readonly HashSet<ulong> KnownGeometryEdges = new();
		internal readonly HashSet<ulong> TentativeGeometryEdges = new();
		internal readonly HashSet<ulong> CandidateGeometryEdges = new();

		private readonly Dictionary<long, LinkedListNode<long>> PendingNodes = new();
		private readonly Dictionary<long, long> OversizedRetryAfterMS = new();
		private readonly HashSet<ulong> DeliveredGeometryEdges = new();

		internal int PendingCount => Pending.Count;
		internal ulong GeometryEpoch { get; private set; } = 1;

		internal void Queue(long convoyID, bool forceSnapshot)
		{
			OversizedRetryAfterMS.Remove(convoyID);
			if (Pending.TryGetValue(convoyID, out bool existing)) { Pending[convoyID] = existing || forceSnapshot; return; }

			Pending[convoyID] = forceSnapshot;
			PendingNodes[convoyID] = PendingOrder.AddLast(convoyID);
		}

		internal bool IsOversizedRetryThrottled(long convoyID, long nowMS)
		{
			if (!OversizedRetryAfterMS.TryGetValue(convoyID, out long retryAfterMS)) return false;
			if (nowMS < retryAfterMS) return true;
			OversizedRetryAfterMS.Remove(convoyID);
			return false;
		}

		internal void DeferOversized(long convoyID, long retryAfterMS)
		{
			Pending[convoyID] = true;
			OversizedRetryAfterMS[convoyID] = retryAfterMS;
			if (!PendingNodes.ContainsKey(convoyID)) PendingNodes[convoyID] = PendingOrder.AddLast(convoyID);
		}

		internal void RemovePending(long convoyID)
		{
			Pending.Remove(convoyID);
			OversizedRetryAfterMS.Remove(convoyID);
			if (!PendingNodes.Remove(convoyID, out LinkedListNode<long>? node)) return;
			PendingOrder.Remove(node);
		}

		internal void BeginFlush()
		{
			DeliveredGeometryEdges.Clear();
			ClearPrepared();
			TentativeGeometryEdges.Clear();
			TentativeGeometryEdges.UnionWith(KnownGeometryEdges);
		}

		internal void AcceptPrepared(PreparedRouteDelivery delivery)
		{
			Prepared.Add(delivery);
			for (int iteration = 0; iteration < delivery.NewGeometryEdges.Length; iteration++)
				TentativeGeometryEdges.Add(delivery.NewGeometryEdges[iteration]);
		}

		internal void CommitDeliveries(int count)
		{
			count = Math.Min(count, Prepared.Count);
			for (int iteration = 0; iteration < count; iteration++)
			{
				PreparedRouteDelivery delivery = Prepared[iteration];
				delivery.Subscription.Epoch = delivery.Epoch;
				delivery.Subscription.Revision = delivery.Revision;
				delivery.Route.MarkJournalPublished();

				for (int edgeIndex = 0; edgeIndex < delivery.NewGeometryEdges.Length; edgeIndex++) { DeliveredGeometryEdges.Add(delivery.NewGeometryEdges[edgeIndex]); }

				RemovePending(delivery.ConvoyID);
			}

			// Any prepared-but-unsent records remain pending and are rebuilt from current authoritative state on the next batch/flush.
			ClearPrepared();
		}

		internal bool WouldExceedGeometryBudget(int deliveryCount)
		{
			deliveryCount = Math.Min(deliveryCount, Prepared.Count);
			CandidateGeometryEdges.Clear();
			try
			{
				for (int iteration = 0; iteration < deliveryCount; iteration++)
				{
					ulong[] edges = Prepared[iteration].NewGeometryEdges;
					for (int edgeIndex = 0; edgeIndex < edges.Length; edgeIndex++)
					{
						ulong edgeHash = edges[edgeIndex];
						if (KnownGeometryEdges.Contains(edgeHash) || DeliveredGeometryEdges.Contains(edgeHash)) { continue; }
						CandidateGeometryEdges.Add(edgeHash);
					}
				}

				return KnownGeometryEdges.Count + DeliveredGeometryEdges.Count + CandidateGeometryEdges.Count > MaxKnownGeometryEdgesPerPlayer;
			}
			finally { CandidateGeometryEdges.Clear(); }
		}

		internal void PromotePendingToSnapshots()
		{
			for (LinkedListNode<long>? node = PendingOrder.First; node != null; node = node.Next) { Pending[node.Value] = true; }
		}

		internal void StartNextGeometryEpoch() { AdvanceGeometryEpoch(); }

		internal void CommitFlushGeometry()
		{
			if (DeliveredGeometryEdges.Count == 0) return;
			KnownGeometryEdges.UnionWith(DeliveredGeometryEdges);
			DeliveredGeometryEdges.Clear();
		}

		internal bool SynchronizeGeometryEpoch(
			ulong requestedEpoch,
			bool resetKnowledge,
			out bool resetPendingGeometry,
			out bool forceRequestedSnapshot)
		{
			resetPendingGeometry = false;
			forceRequestedSnapshot = false;
			if (requestedEpoch == 0) return false;

			if (requestedEpoch > GeometryEpoch)
			{
				// A reconnecting client can already know a later epoch than this new outbox. Adopt that monotonic baseline before publishing any route.
				GeometryEpoch = requestedEpoch;
				if (resetKnowledge) AdvanceGeometryEpoch();
				else ResetGeometryKnowledge();
				resetPendingGeometry = true;
				forceRequestedSnapshot = true;
				return true;
			}

			if (requestedEpoch < GeometryEpoch)
			{
				// This is an old request crossing the server's already-published epoch transition.
				// Never clear current-epoch knowledge - ordered server traffic is the proof that every recorded edge precedes this forced snapshot.
				forceRequestedSnapshot = resetKnowledge;
				return true;
			}

			if (resetKnowledge)
			{
				// Epoch creation is server-owned. Repeated requests from the preceding epoch take the stale branch above and cannot invalidate this epoch.
				AdvanceGeometryEpoch();
				resetPendingGeometry = true;
				forceRequestedSnapshot = true;
			}
			return true;
		}

		private void AdvanceGeometryEpoch()
		{
			GeometryEpoch++;
			ResetGeometryKnowledge();
		}

		private void ResetGeometryKnowledge()
		{
			KnownGeometryEdges.Clear();
			TentativeGeometryEdges.Clear();
			CandidateGeometryEdges.Clear();
			DeliveredGeometryEdges.Clear();
			ClearPrepared();
		}

		internal void ClearPrepared()
		{
			Prepared.Clear();
			CandidateGeometryEdges.Clear();
		}
	}

	private sealed class ServerRouteSource
	{
		public readonly EntityStandardGaugeLocomotive Owner;
		public readonly ConvoyRoute Route;
		public double HeadS;
		public int LastObservedEpoch;
		public int LastObservedGraphVersion;
		public uint LastObservedRevision;
		public uint LastObservedTopologyRevision;
		public uint IndexedTopologyRevision = uint.MaxValue;
		public readonly HashSet<ulong> IndexedEdges = new();
		public long LastPublishMS;

		public ServerRouteSource(EntityStandardGaugeLocomotive owner, ConvoyRoute route, double headS)
		{
			Owner = owner;
			Route = route;
			HeadS = headS;
			LastObservedEpoch = int.MinValue;
			LastObservedGraphVersion = int.MinValue;
			LastObservedRevision = uint.MaxValue;
			LastObservedTopologyRevision = uint.MaxValue;
		}

		public void Observe(ConvoyRoute route)
		{
			LastObservedEpoch = route.Epoch;
			LastObservedGraphVersion = route.GraphVersion;
			LastObservedRevision = route.Revision;
			LastObservedTopologyRevision = route.TopologyRevision;
		}
	}

	private sealed class ServerSubscription
	{
		public int Epoch;
		public uint Revision;
		public long ExpiresAtMS;
	}

	private sealed class ClientRouteEntry
	{
		public ConvoyRoute? Route;
		public ulong GeometryEpoch;
		public double HeadSAnchor;
		public long AnchorGeneration;
		public long LastUsedMS;
		public long NextRequestMS;
		public long NextAnchorRequestMS;
	}
}
