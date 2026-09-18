using System;
using System.Collections.Generic;
using System.IO;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace YangTransport;

[ProtoContract]
public sealed class NerdToolSignalResultPacket
{
	[ProtoMember(1)] public int X;
	[ProtoMember(2)] public int Y;
	[ProtoMember(3)] public int Z;
	[ProtoMember(4)] public int Dimension;
	[ProtoMember(5)] public byte ResultFlags;
	[ProtoMember(6)] public byte[] PackedData = Array.Empty<byte>();
}

/// Signal inspection mode, lets you visualize the state of a whole block and every signal in it (or just a signle signal)
public sealed class NerdToolSignalMode : ModSystem, INerdToolModeHandler
{
	private const string ChannelName = "yangtransport_nerdtool_signal";
	private const int MaxSignalRecords = 256;
	private const int MaxOccupancyOwnerRecords = 128;
	private const long ResultDurationMS = 12000;
	private const int ClientTickMS = 500;
	private const long BlinkIntervalMS = 500;

	private const byte PacketZoneQuery = 1 << 0;
	private const byte PacketZoneOccupied = 1 << 1;
	private const byte PacketTruncated = 1 << 2;
	private const byte PacketOwnersTruncated = 1 << 3;

	private const byte SignalAxisZ = 1 << 0;
	private const byte SignalEntryFromNegativeAllowed = 1 << 1;
	private const byte SignalEntryFromPositiveAllowed = 1 << 2;
	private const byte SignalExitFromClickedZoneAllowed = 1 << 3;
	private const byte SignalGaugeOne = 1 << 4;
	private const byte SignalNegativeEntryApplicable = 1 << 5;
	private const byte SignalPositiveEntryApplicable = 1 << 6;

	private static readonly int TrackGreenColor =			NerdToolTrackHighlightRenderer.HighlightColor(80, 80, 255, 80);
	private static readonly int TrackYellowColor =			NerdToolTrackHighlightRenderer.HighlightColor(88, 255, 220, 48);
	private static readonly int TrackRedColor =				NerdToolTrackHighlightRenderer.HighlightColor(88, 255, 64, 64);

	private static readonly int SignalGreenColor =			NerdToolTrackHighlightRenderer.HighlightColor(255, 80, 255, 80);
	private static readonly int SignalYellowColor =			NerdToolTrackHighlightRenderer.HighlightColor(255, 255, 220, 48);
	private static readonly int SignalRedColor =			NerdToolTrackHighlightRenderer.HighlightColor(255, 255, 64, 64);

	private static readonly int SignalGreenFillColor =		NerdToolTrackHighlightRenderer.HighlightColor(72, 80, 255, 80);
	private static readonly int SignalYellowFillColor =		NerdToolTrackHighlightRenderer.HighlightColor(80, 255, 220, 48);
	private static readonly int SignalRedFillColor =		NerdToolTrackHighlightRenderer.HighlightColor(80, 255, 64, 64);

	private static readonly int VehicleWireColor =			NerdToolTrackHighlightRenderer.HighlightColor(255, 255, 64, 255);
	private static readonly int VehicleFillColor =			NerdToolTrackHighlightRenderer.HighlightColor(80, 255, 64, 255);

	private static readonly int[] UnitCubeTriangleIndices =
	{
		0, 2, 1, 0, 3, 2,
		4, 5, 6, 4, 6, 7,
		0, 4, 7, 0, 7, 3,
		1, 2, 6, 1, 6, 5,
		0, 1, 5, 0, 5, 4,
		3, 7, 6, 3, 6, 2,
	};

	private static readonly int[] UnitCubeLineCornerIndices =
	{
		0, 1, 1, 5, 5, 4, 4, 0,
		3, 2, 2, 6, 6, 7, 7, 3,
		0, 3, 1, 2, 5, 6, 4, 7,
	};

	private ICoreServerAPI? ServerAPI;
	private IServerNetworkChannel? ServerChannel;
	private RailGraphServerSystem? RailGraphServer;

	private readonly List<ulong> ServerEdgeHashes = new(8);
	private readonly List<SignalSnapshotRecord> ServerSnapshots = new(MaxSignalRecords);
	private readonly List<long> ServerOccupancyOwnerIDs = new(MaxOccupancyOwnerRecords);

	// Tool-owned topology cache. This does not modify or depend on the server signal index.
	private int CachedGraphVersion = -1;
	private readonly List<SignalTopologyRecord> CachedSignals = new();
	private readonly Dictionary<ulong, List<int>> CachedSignalsByZoneID = new();

	private ICoreClientAPI? ClientAPI;
	private NerdToolTrackHighlightRenderer? TrackHighlightRenderer;
	private readonly List<ClientSignalRecord> ClientSignals = new(MaxSignalRecords);
	private readonly List<long> ClientOccupancyOwnerIDs = new(MaxOccupancyOwnerRecords);
	private readonly Dictionary<int, SignalBoxBounds> ClientSignalBoundsByBlockID = new();

	private BlockPos? ResultSourcePos;
	private bool ResultIsZoneQuery;
	private bool ResultZoneOccupied;
	private bool ResultTruncated;
	private long ResultStartedMS;
	private long ResultExpiresMS;
	private long ClientTickListenerID;

	private MeshRef? SignalFullMesh;
	private MeshRef? SignalSplitMesh;
	private MeshRef? SignalFullFillMesh;
	private MeshRef? SignalSplitFillMesh;
	private MeshRef? GuideMesh;
	private MeshData? GuideMeshData;
	private MeshRef? VehicleWireMesh;
	private MeshData? VehicleWireMeshData;
	private MeshRef? VehicleFillMesh;
	private MeshData? VehicleFillMeshData;
	private BlockPos? SignalMeshOrigin;
	private readonly Matrixf MeshModelView = new();

	public override bool ShouldLoad(EnumAppSide forSide) => true;

	public override void Start(ICoreAPI coreAPI)
	{
		if (coreAPI is ICoreServerAPI serverAPI)
		{
			ServerAPI = serverAPI;
			RailGraphServer = coreAPI.ModLoader.GetModSystem<RailGraphServerSystem>();
			ServerChannel = serverAPI.Network.RegisterChannel(ChannelName).RegisterMessageType<NerdToolSignalResultPacket>();
		}

		if (coreAPI is ICoreClientAPI clientAPI)
		{
			ClientAPI = clientAPI;
			TrackHighlightRenderer = new NerdToolTrackHighlightRenderer(clientAPI);

			IClientNetworkChannel clientChannel = clientAPI.Network.RegisterChannel(ChannelName).RegisterMessageType<NerdToolSignalResultPacket>();
			clientChannel.SetMessageHandler<NerdToolSignalResultPacket>(OnClientResult);
		}
	}

	public NerdToolInteractionResult HandleInteract(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSelection, EntitySelection entitySelection)
	{
		if
		(
			blockSelection?.Position == null || !NerdToolRailTarget.TryResolve
			( 
				byEntity.World.BlockAccessor, blockSelection.Position, out BlockPos sourcePos, out _, out TrackPieceSpec traclSpec
			)
		) { return NerdToolInteractionResult.NotHandled; }

		int durabilityCost = traclSpec.IsSignal ? 1 : 2;

		if (byEntity.Api?.Side == EnumAppSide.Client) { return NerdToolInteractionResult.Success(sourcePos, durabilityCost); }

		if (byEntity is not EntityPlayer entityPlayer || entityPlayer.Player is not IServerPlayer serverPlayer)
		{
			return NerdToolInteractionResult.HandledFailure(sourcePos);
		}

		return ServerInspect(serverPlayer, sourcePos, traclSpec)
			? NerdToolInteractionResult.Success(sourcePos, durabilityCost)
			: NerdToolInteractionResult.HandledFailure(sourcePos);
	}

	public void ClientRender(ItemSlot slot, IClientPlayer player)
	{
		if (ClientAPI == null || player?.Entity == null || TrackHighlightRenderer == null) return;

		TrackHighlightRenderer.UpdateHover(player, ResultSourcePos);
		TrackHighlightRenderer.Render(player);
		RenderSignalVisuals(player);
	}

	public void OnDeselected()
	{
		if (ClientAPI == null) return;

		ClearAllClientState();
		StopClientTick();
	}

	private bool ServerInspect(IServerPlayer player, BlockPos sourcePos, TrackPieceSpec trackSpec)
	{
		if (ServerChannel == null || player == null) return false;

		RailGraphServer ??= ServerAPI?.ModLoader.GetModSystem<RailGraphServerSystem>();
		if (RailGraphServer == null) return false;

		RailGraphLive graph = RailGraphServer.Graph;

		if (trackSpec.IsSignal) { return ServerInspectSingleSignal(player, graph, sourcePos, trackSpec); }
		return ServerInspectZone(player, graph, sourcePos, trackSpec);
	}

	private bool ServerInspectSingleSignal(IServerPlayer player, RailGraphLive graph, BlockPos sourcePos, TrackPieceSpec trackSpec)
	{
		if 
		(
			!TryFindOwnedSignalEndpoint ( graph, sourcePos, trackSpec, out RailGraphLive.EndpointKey signalEndpoint, out RailGraphLive.SignalEntry signalEntry)
			|| !TryBuildSignalTopology(graph, signalEndpoint, signalEntry, out SignalTopologyRecord topology)
		) { return false; }

		ServerSnapshots.Clear();
		ServerSnapshots.Add(BuildSnapshot(graph, topology, clickedZoneID: 0));

		ServerOccupancyOwnerIDs.Clear();
		ServerChannel!.SendPacket(new NerdToolSignalResultPacket
		{
			X = sourcePos.X, Y = sourcePos.Y, Z = sourcePos.Z, Dimension = sourcePos.dimension,
			ResultFlags = 0, PackedData = PackSnapshotPayload(sourcePos, ServerSnapshots, ServerOccupancyOwnerIDs)
		}, player);

		return true;
	}

	private bool ServerInspectZone(IServerPlayer player, RailGraphLive graph, BlockPos sourcePos, TrackPieceSpec trackSpec)
	{
		if (!TryGetSingleZoneForTrack(graph, sourcePos, trackSpec, out ulong zoneID)) { return false; }

		ServerOccupancyOwnerIDs.Clear();
		bool occupancyOwnersTruncated = false;
		if (RailGraphServer!.TryGetOccupancyZoneOwners(zoneID, out IReadOnlyCollection<long> occupancyOwnerIDs))
		{
			foreach (long ownerID in occupancyOwnerIDs)
			{
				if (ownerID <= 0) continue;
				if (ServerOccupancyOwnerIDs.Count >= MaxOccupancyOwnerRecords) { occupancyOwnersTruncated = true; break; }

				ServerOccupancyOwnerIDs.Add(ownerID);
			}
		}

		EnsureSignalTopologyCache(graph);

		ServerSnapshots.Clear();
		bool signalRecordsTruncated = false;

		if (CachedSignalsByZoneID.TryGetValue(zoneID, out List<int>? signalIndices))
		{
			int signalCount = Math.Min(signalIndices.Count, MaxSignalRecords);
			signalRecordsTruncated = signalIndices.Count > signalCount;

			for (int signalListIndex = 0; signalListIndex < signalCount; signalListIndex++)
			{
				int signalIndex = signalIndices[signalListIndex];
				if ((uint)signalIndex >= (uint)CachedSignals.Count) continue;

				ServerSnapshots.Add(BuildSnapshot(graph, CachedSignals[signalIndex], zoneID));
			}
		}

		byte resultFlags = PacketZoneQuery;
		if (graph.IsOccupancyZoneOccupied(zoneID)) resultFlags |= PacketZoneOccupied;
		if (signalRecordsTruncated) resultFlags |= PacketTruncated;
		if (occupancyOwnersTruncated) resultFlags |= PacketOwnersTruncated;

		ServerChannel!.SendPacket(new NerdToolSignalResultPacket
		{
			X = sourcePos.X, Y = sourcePos.Y, Z = sourcePos.Z, Dimension = sourcePos.dimension,
			ResultFlags = resultFlags, PackedData = PackSnapshotPayload(sourcePos, ServerSnapshots, ServerOccupancyOwnerIDs)
		}, player);

		return true;
	}

	private bool TryGetSingleZoneForTrack(RailGraphLive graph, BlockPos sourcePos, TrackPieceSpec trackSpec, out ulong zoneID)
	{
		zoneID = 0;

		if (graph.CollectSpecEdgeHashesAt(sourcePos, trackSpec, ServerEdgeHashes) == 0) { return false; }

		for (int edgeIndex = 0; edgeIndex < ServerEdgeHashes.Count; edgeIndex++)
		{
			if (!graph.TryGetOccupancyZoneID(ServerEdgeHashes[edgeIndex], out ulong edgeZone) || edgeZone == 0) { return false; }

			if (zoneID == 0) { zoneID = edgeZone; }
			else if (zoneID != edgeZone) { return false; } // A block selection cannot disambiguate (or discombobulate) which independent path the player meant.
		}

		return zoneID != 0;
	}

	private bool TryFindOwnedSignalEndpoint
	(
		RailGraphLive graph, BlockPos sourcePos, TrackPieceSpec trackSpec,
		out RailGraphLive.EndpointKey endpoint, out RailGraphLive.SignalEntry signalEntry
	)
	{
		endpoint = default;
		signalEntry = default;

		if (graph.CollectSpecEdgeHashesAt(sourcePos, trackSpec, ServerEdgeHashes) == 0) { return false; }

		for (int edgeIndex = 0; edgeIndex < ServerEdgeHashes.Count; edgeIndex++)
		{
			if (!graph.TryGetEdgeEndpoints(ServerEdgeHashes[edgeIndex], out RailGraphLive.EndpointKey endpointA, out RailGraphLive.EndpointKey endpointB, out _))
			{
				continue;
			}

			if (TryGetOwnedSignal(graph, sourcePos, endpointA, out signalEntry)) { endpoint = endpointA; return true; }
			if (TryGetOwnedSignal(graph, sourcePos, endpointB, out signalEntry)) { endpoint = endpointB; return true; }
		}

		return false;
	}

	private static bool TryGetOwnedSignal(RailGraphLive graph, BlockPos sourcePos, RailGraphLive.EndpointKey endpoint, out RailGraphLive.SignalEntry signalEntry)
	{
		if
		(
			graph.TryGetSignal(endpoint, out signalEntry) &&
			endpoint.Dimension == sourcePos.dimension && signalEntry.OwnerX == sourcePos.X &&
			signalEntry.OwnerY == sourcePos.Y && signalEntry.OwnerZ == sourcePos.Z
		)
		{ return true; }

		signalEntry = default;
		return false;
	}

	private void EnsureSignalTopologyCache(RailGraphLive railGraph)
	{
		if (CachedGraphVersion == railGraph.BuildVersion) return;

		CachedSignals.Clear();
		CachedSignalsByZoneID.Clear();

		foreach (KeyValuePair<RailGraphLive.EndpointKey, RailGraphLive.SignalEntry> signalPair in railGraph.SignalsUnsafe)
		{
			if (!TryBuildSignalTopology(railGraph, signalPair.Key, signalPair.Value, out SignalTopologyRecord topology)) { continue; }

			int signalIndex = CachedSignals.Count;
			CachedSignals.Add(topology);

			AddCachedSignalToZone(topology.NegativeZoneID, signalIndex);
			if (topology.PositiveZoneID != topology.NegativeZoneID) { AddCachedSignalToZone(topology.PositiveZoneID, signalIndex); }
		}

		CachedGraphVersion = railGraph.BuildVersion;
	}

	private void AddCachedSignalToZone(ulong zoneID, int signalIndex)
	{
		if (zoneID == 0) return;

		if (!CachedSignalsByZoneID.TryGetValue(zoneID, out List<int>? signalIndices))
		{
			signalIndices = new List<int>(2);
			CachedSignalsByZoneID[zoneID] = signalIndices;
		}

		signalIndices.Add(signalIndex);
	}

	private static bool TryBuildSignalTopology
	(
		RailGraphLive graph, RailGraphLive.EndpointKey endpoint,
		RailGraphLive.SignalEntry signalEntry, out SignalTopologyRecord topology
	)
	{
		topology = default;

		if (!graph.TryGetIncidentEdges(endpoint, out List<ulong> incidentEdgeHashes) || incidentEdgeHashes.Count < 2) { return false; }

		ulong edge0 = 0;
		ulong edge1 = 0;

		for (int incidentEdgeIndex = 0; incidentEdgeIndex < incidentEdgeHashes.Count; incidentEdgeIndex++)
		{
			ulong edgeHash = incidentEdgeHashes[incidentEdgeIndex];
			if (edgeHash == 0 || edgeHash == edge0) continue;

			if (edge0 == 0)		{ edge0 = edgeHash; }
			else				{ edge1 = edgeHash; break; }
		}

		if 
		(
			edge0 == 0 || edge1 == 0 ||
			!TryClassifySignalEdge(graph, endpoint, edge0, out SignalAxis axis0, out int sign0) ||
			!TryClassifySignalEdge(graph, endpoint, edge1, out SignalAxis axis1, out int sign1) ||
			axis0 != axis1 || sign0 == sign1
		) { return false; }

		ulong negativeEdge = sign0 < 0 ? edge0 : edge1;
		ulong positiveEdge = sign0 > 0 ? edge0 : edge1;

		graph.TryGetOccupancyZoneID(negativeEdge, out ulong negativeZone);
		graph.TryGetOccupancyZoneID(positiveEdge, out ulong positiveZone);

		bool directional = signalEntry.Kind == SignalKind.OneWay || signalEntry.Kind == SignalKind.Chain;
		bool structuralNegative = !directional || signalEntry.AllowedFromEdgeHash == 0 || signalEntry.AllowedFromEdgeHash == negativeEdge;
		bool structuralPositive = !directional || signalEntry.AllowedFromEdgeHash == 0 || signalEntry.AllowedFromEdgeHash == positiveEdge;

		topology = new SignalTopologyRecord
		{
			X = signalEntry.OwnerX, Y = signalEntry.OwnerY, Z = signalEntry.OwnerZ,
			Gauge = endpoint.Gauge, Axis = axis0,
			NegativeZoneID = negativeZone, PositiveZoneID = positiveZone,
			StructuralNegativeEntryAllowed = structuralNegative, StructuralPositiveEntryAllowed = structuralPositive
		};

		return true;
	}

	private static bool TryClassifySignalEdge(RailGraphLive graph, RailGraphLive.EndpointKey signalEndpoint, ulong edgeHash, out SignalAxis axis, out int sign)
	{
		axis = default;
		sign = 0;

		if (!graph.TryGetEdgeEndpoints( edgeHash, out RailGraphLive.EndpointKey endpointA, out RailGraphLive.EndpointKey endpointB, out _)) { return false; }

		RailGraphLive.EndpointKey oppositeEndpoint;
		if			(endpointA.Equals(signalEndpoint))			{ oppositeEndpoint = endpointB; }
		else if		(endpointB.Equals(signalEndpoint))			{ oppositeEndpoint = endpointA; }
		else													{ return false; }

		int dx = oppositeEndpoint.X16 - signalEndpoint.X16;
		int dz = oppositeEndpoint.Z16 - signalEndpoint.Z16;

		if (dx == 0 && dz == 0) return false;

		if (Math.Abs(dx) >= Math.Abs(dz))	{ axis = SignalAxis.X; sign = dx < 0 ? -1 : 1; }
		else 								{ axis = SignalAxis.Z; sign = dz < 0 ? -1 : 1; }

		return true;
	}

	private static SignalSnapshotRecord BuildSnapshot(RailGraphLive graph, SignalTopologyRecord topology, ulong clickedZoneID)
	{
		bool negativeEntryAllowed = topology.StructuralNegativeEntryAllowed && topology.PositiveZoneID != 0 && !graph.IsOccupancyZoneOccupied(topology.PositiveZoneID);
		bool positiveEntryAllowed = topology.StructuralPositiveEntryAllowed && topology.NegativeZoneID != 0 && !graph.IsOccupancyZoneOccupied(topology.NegativeZoneID);

		bool exitFromClickedZoneAllowed =
			clickedZoneID != 0 && ((topology.NegativeZoneID == clickedZoneID &&
			negativeEntryAllowed) || (topology.PositiveZoneID == clickedZoneID && positiveEntryAllowed));

		byte snapshotFlags = 0;
		if (topology.Axis == SignalAxis.Z)					snapshotFlags |= SignalAxisZ;
		if (negativeEntryAllowed)							snapshotFlags |= SignalEntryFromNegativeAllowed;
		if (positiveEntryAllowed)							snapshotFlags |= SignalEntryFromPositiveAllowed;
		if (exitFromClickedZoneAllowed)						snapshotFlags |= SignalExitFromClickedZoneAllowed;
		if (topology.Gauge == 1)							snapshotFlags |= SignalGaugeOne;
		if (topology.StructuralNegativeEntryAllowed)		snapshotFlags |= SignalNegativeEntryApplicable;
		if (topology.StructuralPositiveEntryAllowed)		snapshotFlags |= SignalPositiveEntryApplicable;

		return new SignalSnapshotRecord { X = topology.X, Y = topology.Y, Z = topology.Z, Flags = snapshotFlags };
	}

	private void OnClientResult(NerdToolSignalResultPacket resultPacket)
	{
		if (ClientAPI == null || TrackHighlightRenderer == null || resultPacket == null || resultPacket.PackedData == null) { return; }
		BlockPos sourcePos = new(resultPacket.X, resultPacket.Y, resultPacket.Z, resultPacket.Dimension);

		ClearResult();
		TrackHighlightRenderer.InvalidateHoverSelection();
		if (!TryDecodeSnapshotPayload(sourcePos, resultPacket.PackedData, ClientSignals, ClientOccupancyOwnerIDs)) { return; }

		ResultSourcePos =		sourcePos;
		ResultIsZoneQuery =		(resultPacket.ResultFlags & PacketZoneQuery) != 0;
		ResultZoneOccupied =	(resultPacket.ResultFlags & PacketZoneOccupied) != 0;
		ResultTruncated = 		(resultPacket.ResultFlags & PacketTruncated) != 0;
		ResultStartedMS = 		ClientAPI.World.ElapsedMilliseconds;
		ResultExpiresMS = 		ResultStartedMS + ResultDurationMS;

		if (ResultIsZoneQuery)
		{
			Block sourceBlock = ClientAPI.World.BlockAccessor.GetBlock(sourcePos);
			if (TrackSpecsDictionary.TryGet(sourceBlock, out _))
			{
				int zoneTrackColor = DetermineZoneTrackColor();
				TrackHighlightRenderer.SetResult(sourcePos, sourceBlock, zoneTrackColor);
			}
		}
		else { TrackHighlightRenderer.ClearResult(); }

		TrackHighlightRenderer.InvalidateHoverSelection();
		BuildSignalMeshes();
		BuildVehicleMeshBuffers();
		BuildGuideMesh();
		EnsureClientTick();
	}

	private int DetermineZoneTrackColor()
	{
		if (!ResultZoneOccupied)	return TrackGreenColor;
		if (ResultTruncated)		return TrackYellowColor;
		for (int signalIndex = 0; signalIndex < ClientSignals.Count; signalIndex++)
		{
			if (ClientSignals[signalIndex].ExitFromClickedZoneAllowed) { return TrackYellowColor; }
		}

		return TrackRedColor;
	}

	private void BuildSignalMeshes()
	{
		if (ClientAPI == null || ResultSourcePos == null) { return; }

		DeleteMesh(ref SignalFullMesh);
		DeleteMesh(ref SignalSplitMesh);
		DeleteMesh(ref SignalFullFillMesh);
		DeleteMesh(ref SignalSplitFillMesh);
		SignalMeshOrigin = ResultSourcePos.Copy();

		if (ClientSignals.Count == 0) { return; }

		MeshData fullMesh = new(Math.Max(24, ClientSignals.Count * 24), Math.Max(24, ClientSignals.Count * 24), withNormals: false, withUv: false);
		fullMesh.SetMode(EnumDrawMode.Lines);

		MeshData splitMesh = new(Math.Max(48, ClientSignals.Count * 48), Math.Max(48, ClientSignals.Count * 48), withNormals: false, withUv: false);
		splitMesh.SetMode(EnumDrawMode.Lines);

		MeshData fullFillMesh = new(Math.Max(8, ClientSignals.Count * 8), Math.Max(36, ClientSignals.Count * 36), withNormals: false, withUv: false);
		fullFillMesh.SetMode(EnumDrawMode.Triangles);

		MeshData splitFillMesh = new(Math.Max(16, ClientSignals.Count * 16), Math.Max(72, ClientSignals.Count * 72), withNormals: false, withUv: false);
		splitFillMesh.SetMode(EnumDrawMode.Triangles);

		for (int signalIndex = 0; signalIndex < ClientSignals.Count; signalIndex++)
		{
			ClientSignalRecord record = ClientSignals[signalIndex];
			BlockPos signalPos = new(record.X, record.Y, record.Z, ResultSourcePos.dimension);
			SignalBoxBounds bounds = GetSignalBounds(signalPos, record.Axis, record.Gauge);

			const float boxEpsilon = 0.015f;
			float x1 = signalPos.X - ResultSourcePos.X + bounds.X1 - boxEpsilon;
			float y1 = signalPos.InternalY - ResultSourcePos.InternalY + bounds.Y1 - boxEpsilon;
			float z1 = signalPos.Z - ResultSourcePos.Z + bounds.Z1 - boxEpsilon;
			float x2 = signalPos.X - ResultSourcePos.X + bounds.X2 + boxEpsilon;
			float y2 = signalPos.InternalY - ResultSourcePos.InternalY + bounds.Y2 + boxEpsilon;
			float z2 = signalPos.Z - ResultSourcePos.Z + bounds.Z2 + boxEpsilon;

			record.CenterX = signalPos.X + (bounds.X1 + bounds.X2) * 0.5;
			record.CenterY = signalPos.InternalY + (bounds.Y1 + bounds.Y2) * 0.5;
			record.CenterZ = signalPos.Z + (bounds.Z1 + bounds.Z2) * 0.5;
			ClientSignals[signalIndex] = record;

			SignalVisualState state = GetSignalVisualState(record);
			int overallWireColor = GetSignalWireColor(state);
			int overallFillColor = GetSignalFillColor(state);

			WriteWireBox(fullMesh, x1, y1, z1, x2, y2, z2, overallWireColor);
			WriteSolidBox(fullFillMesh, x1, y1, z1, x2, y2, z2, overallFillColor);

			// Directional signals only have one applicable entry side, so they resolve directly to green or red.
			if (state != SignalVisualState.Yellow)
			{
				WriteWireBox(splitMesh, x1, y1, z1, x2, y2, z2, overallWireColor);
				WriteSolidBox(splitFillMesh, x1, y1, z1, x2, y2, z2, overallFillColor);
				continue;
			}

			if (record.Axis == SignalAxis.X)
			{
				float midpointX = (x1 + x2) * 0.5f;
				WriteWireBox(splitMesh, x1, y1, z1, midpointX, y2, z2, record.NegativeEntryAllowed ? SignalGreenColor : SignalRedColor);
				WriteSolidBox(splitFillMesh, x1, y1, z1, midpointX, y2, z2, record.NegativeEntryAllowed ? SignalGreenFillColor : SignalRedFillColor);

				WriteWireBox(splitMesh, midpointX, y1, z1, x2, y2, z2, record.PositiveEntryAllowed ? SignalGreenColor : SignalRedColor);
				WriteSolidBox(splitFillMesh, midpointX, y1, z1, x2, y2, z2, record.PositiveEntryAllowed ? SignalGreenFillColor : SignalRedFillColor);
			}
			else
			{
				float midpointZ = (z1 + z2) * 0.5f;
				WriteWireBox(splitMesh, x1, y1, z1, x2, y2, midpointZ, record.NegativeEntryAllowed ? SignalGreenColor : SignalRedColor);
				WriteSolidBox(splitFillMesh, x1, y1, z1, x2, y2, midpointZ, record.NegativeEntryAllowed ? SignalGreenFillColor : SignalRedFillColor);

				WriteWireBox(splitMesh, x1, y1, midpointZ, x2, y2, z2, record.PositiveEntryAllowed ? SignalGreenColor : SignalRedColor);
				WriteSolidBox(splitFillMesh, x1, y1, midpointZ, x2, y2, z2, record.PositiveEntryAllowed ? SignalGreenFillColor : SignalRedFillColor);
			}
		}

		if (fullMesh.VerticesCount > 0)			{ SignalFullMesh = ClientAPI.Render.UploadMesh(fullMesh); }
		if (splitMesh.VerticesCount > 0)		{ SignalSplitMesh = ClientAPI.Render.UploadMesh(splitMesh); }
		if (fullFillMesh.VerticesCount > 0)		{ SignalFullFillMesh = ClientAPI.Render.UploadMesh(fullFillMesh); }
		if (splitFillMesh.VerticesCount > 0)	{ SignalSplitFillMesh = ClientAPI.Render.UploadMesh(splitFillMesh); }
	}

	private SignalBoxBounds GetSignalBounds(BlockPos signalPos, SignalAxis axis, byte gauge)
	{
		if (ClientAPI == null) return SignalBoxBounds.Unit;

		Block block = ClientAPI.World.BlockAccessor.GetBlock(signalPos);
		if (block != null && block.Id > 0 && TrackSpecsDictionary.TryGet(block, out TrackPieceSpec trackSpec) && trackSpec.IsSignal)
		{
			if (!ClientSignalBoundsByBlockID.TryGetValue(block.Id, out SignalBoxBounds bounds))
			{
				bounds = BuildSignalBounds(block);
				ClientSignalBoundsByBlockID[block.Id] = bounds;
			}
			return bounds;
		}

		// The graph can know about an unloaded signal when the client does not have its block.
		// Do not request chunks. Use a small axis/gauge-aware fallback until the snapshot expires.
		if (gauge == 1) { return axis == SignalAxis.X ? new SignalBoxBounds(0, 0, -1, 1, 3, 2) : new SignalBoxBounds(-1, 0, 0, 2, 3, 1); }

		return SignalBoxBounds.Unit;
	}

	private static SignalBoxBounds BuildSignalBounds(Block block)
	{
		float minX = 0; float minY = 0; float minZ = 0;
		float maxX = 1; float maxY = 1; float maxZ = 1;

		JsonObject? collisionFlowFieldAttribute = block.Attributes?["collisionFlowField"];
		JsonObject[]? flowFieldRows = SelectFlowFieldArray(collisionFlowFieldAttribute, block);

		if (flowFieldRows != null)
		{
			for (int rowIndex = 0; rowIndex < flowFieldRows.Length; rowIndex++)
			{
				int[]? flowFieldOffset = flowFieldRows[rowIndex]["pos"].AsArray<int>(null) ?? flowFieldRows[rowIndex]["offset"].AsArray<int>(null);
				if (flowFieldOffset == null || flowFieldOffset.Length < 3) continue;

				minX = Math.Min(minX, flowFieldOffset[0]);		minY = Math.Min(minY, flowFieldOffset[1]);		minZ = Math.Min(minZ, flowFieldOffset[2]);
				maxX = Math.Max(maxX, flowFieldOffset[0] + 1);	maxY = Math.Max(maxY, flowFieldOffset[1] + 1);	maxZ = Math.Max(maxZ, flowFieldOffset[2] + 1);
			}
		}

		return new SignalBoxBounds(minX, minY, minZ, maxX, maxY, maxZ);
	}

	private static JsonObject[]? SelectFlowFieldArray(JsonObject? flowFieldAttribute, Block? block)
	{
		if (flowFieldAttribute == null || !flowFieldAttribute.Exists || block == null || block.Code == null) return null;
		if (flowFieldAttribute.IsArray()) return flowFieldAttribute.AsArray();

		JsonObject selectedFlowField = flowFieldAttribute[block.Code.ToString()];
		if (!selectedFlowField.Exists) selectedFlowField = flowFieldAttribute[block.Code.Path];
		if (!selectedFlowField.Exists) selectedFlowField = flowFieldAttribute["*"];

		return selectedFlowField.Exists && selectedFlowField.IsArray() ? selectedFlowField.AsArray() : null;
	}

	private void BuildVehicleMeshBuffers()
	{
		if (ClientAPI == null) { return; }

		DeleteMesh(ref VehicleWireMesh);
		DeleteMesh(ref VehicleFillMesh);
		VehicleWireMeshData = null;
		VehicleFillMeshData = null;

		int boxCapacity = ClientOccupancyOwnerIDs.Count;
		if (boxCapacity == 0) { return; }

		const int wireVerticesPerBox = 24;
		const int fillVerticesPerBox = 8;
		const int fillIndicesPerBox = 36;

		MeshData wireMeshData = new(boxCapacity * wireVerticesPerBox, boxCapacity * wireVerticesPerBox, withNormals: false, withUv: false);
		wireMeshData.SetMode(EnumDrawMode.Lines);
		wireMeshData.XyzStatic = false;
		wireMeshData.RgbaStatic = true;
		wireMeshData.IndicesStatic = true;

		for (int wireVertexIndex = 0; wireVertexIndex < boxCapacity * wireVerticesPerBox; wireVertexIndex++)
		{
			wireMeshData.AddVertexSkipTex(0, 0, 0, VehicleWireColor);
			wireMeshData.AddIndex(wireVertexIndex);
		}

		MeshData fillMeshData = new(boxCapacity * fillVerticesPerBox, boxCapacity * fillIndicesPerBox, withNormals: false, withUv: false);
		fillMeshData.SetMode(EnumDrawMode.Triangles);
		fillMeshData.XyzStatic = false;
		fillMeshData.RgbaStatic = true;
		fillMeshData.IndicesStatic = true;

		for (int box = 0; box < boxCapacity; box++)
		{
			int vertex = box * fillVerticesPerBox;
			for (int cornerIndex = 0; cornerIndex < fillVerticesPerBox; cornerIndex++) { fillMeshData.AddVertexSkipTex(0, 0, 0, VehicleFillColor); }
			for (int triIndex = 0; triIndex < UnitCubeTriangleIndices.Length; triIndex++) { fillMeshData.AddIndex(vertex + UnitCubeTriangleIndices[triIndex]); }
		}

		VehicleWireMeshData = wireMeshData;
		VehicleFillMeshData = fillMeshData;
		VehicleWireMesh = ClientAPI.Render.UploadMesh(wireMeshData);
		VehicleFillMesh = ClientAPI.Render.UploadMesh(fillMeshData);
	}

	private void BuildGuideMesh()
	{
		if (ClientAPI == null || ResultSourcePos == null) { return; }

		DeleteMesh(ref GuideMesh);
		GuideMeshData = null;

		int lineCount = ClientSignals.Count + ClientOccupancyOwnerIDs.Count;
		if (lineCount == 0) { return; }

		MeshData guideMeshBuffer = new(lineCount * 2, lineCount * 2, withNormals: false, withUv: false);
		guideMeshBuffer.SetMode(EnumDrawMode.Lines);
		guideMeshBuffer.XyzStatic = false;
		guideMeshBuffer.RgbaStatic = true;
		guideMeshBuffer.IndicesStatic = true;

		for (int signalIndex = 0; signalIndex < ClientSignals.Count; signalIndex++)
		{
			ClientSignalRecord record = ClientSignals[signalIndex];
			int color = GetSignalWireColor(GetSignalVisualState(record));

			int vertex = guideMeshBuffer.VerticesCount;
			guideMeshBuffer.AddVertexSkipTex(0, 0, 0, color);
			guideMeshBuffer.AddIndex(vertex);
			guideMeshBuffer.AddVertexSkipTex
			(
				(float)(record.CenterX - ResultSourcePos.X),
				(float)(record.CenterY - ResultSourcePos.InternalY),
				(float)(record.CenterZ - ResultSourcePos.Z),
				color
			);
			guideMeshBuffer.AddIndex(vertex + 1);
		}

		for (int ownerIndex = 0; ownerIndex < ClientOccupancyOwnerIDs.Count; ownerIndex++)
		{
			int vertex = guideMeshBuffer.VerticesCount;
			guideMeshBuffer.AddVertexSkipTex(0, 0, 0, VehicleWireColor); guideMeshBuffer.AddIndex(vertex);
			guideMeshBuffer.AddVertexSkipTex(0, 0, 0, VehicleWireColor); guideMeshBuffer.AddIndex(vertex + 1);
		}

		GuideMeshData = guideMeshBuffer;
		GuideMesh = ClientAPI.Render.UploadMesh(guideMeshBuffer);
	}

	private int UpdateDynamicClientMeshes(IClientPlayer player)
	{
		if (ClientAPI == null || ResultSourcePos == null) { return 0; }

		Vec3d playerRenderPos = player.Entity.CameraPos;

		float startX = (float)(playerRenderPos.X - ResultSourcePos.X);
		float startY = (float)(playerRenderPos.Y + 1.0 - ResultSourcePos.InternalY);
		float startZ = (float)(playerRenderPos.Z - ResultSourcePos.Z);

		float[]? guideCoordinates = GuideMeshData?.xyz;
		if (guideCoordinates != null)
		{
			int lineCount = ClientSignals.Count + ClientOccupancyOwnerIDs.Count;
			for (int lineIndex = 0; lineIndex < lineCount; lineIndex++)
			{
				int lineStartOffset = lineIndex * 2 * 3;
				guideCoordinates[lineStartOffset] = startX;
				guideCoordinates[lineStartOffset + 1] = startY;
				guideCoordinates[lineStartOffset + 2] = startZ;
			}
		}

		int visibleVehicleBoxCount = 0;
		for (int ownerIndex = 0; ownerIndex < ClientOccupancyOwnerIDs.Count; ownerIndex++)
		{
			int guideLineIndex = ClientSignals.Count + ownerIndex;
			int guideLineEndOffset = guideLineIndex * 2 * 3 + 3;

			Entity? occupancyOwnerEntity = ClientAPI.World.GetEntityById(ClientOccupancyOwnerIDs[ownerIndex]);
			if (occupancyOwnerEntity == null ||
				!TryWriteVehicleBox(occupancyOwnerEntity, visibleVehicleBoxCount, out double centerX, out double centerY, out double centerZ))
			{
				if (guideCoordinates != null)
				{
					guideCoordinates[guideLineEndOffset] = startX;
					guideCoordinates[guideLineEndOffset + 1] = startY;
					guideCoordinates[guideLineEndOffset + 2] = startZ;
				}
				continue;
			}

			if (guideCoordinates != null)
			{
				guideCoordinates[guideLineEndOffset] = (float)(centerX - ResultSourcePos.X);
				guideCoordinates[guideLineEndOffset + 1] = (float)(centerY - ResultSourcePos.InternalY);
				guideCoordinates[guideLineEndOffset + 2] = (float)(centerZ - ResultSourcePos.Z);
			}

			visibleVehicleBoxCount++;
		}

		if (GuideMesh != null && GuideMeshData != null) { ClientAPI.Render.UpdateMesh(GuideMesh, GuideMeshData); }

		if (VehicleFillMesh != null && VehicleFillMeshData != null)
		{
			VehicleFillMeshData.VerticesCount = visibleVehicleBoxCount * 8;
			VehicleFillMeshData.IndicesCount = visibleVehicleBoxCount * 36;
			if (visibleVehicleBoxCount > 0) { ClientAPI.Render.UpdateMesh(VehicleFillMesh, VehicleFillMeshData); }
		}

		if (VehicleWireMesh != null && VehicleWireMeshData != null)
		{
			VehicleWireMeshData.VerticesCount = visibleVehicleBoxCount * 24;
			VehicleWireMeshData.IndicesCount = visibleVehicleBoxCount * 24;
			if (visibleVehicleBoxCount > 0) { ClientAPI.Render.UpdateMesh(VehicleWireMesh, VehicleWireMeshData); }
		}

		return visibleVehicleBoxCount;
	}

	private bool TryWriteVehicleBox(Entity entity, int boxIndex, out double centerX, out double centerY, out double centerZ)
	{
		centerX = centerY = centerZ = 0;

		if (ResultSourcePos == null || VehicleFillMeshData == null || VehicleWireMeshData == null || !ItemLinkagePole.TryGetClientPreviousVehicleID(entity, out _)) return false;

		if (entity is EntityStandardGaugeLocomotive standardGaugeVehicle) { WriteStandardGaugeVehicleBox(standardGaugeVehicle, boxIndex, out centerX, out centerY, out centerZ); }
		else
		{
			Cuboidf selectionBox = entity.SelectionBox;

			float x1 = (float)(entity.Pos.X + selectionBox.X1 - ResultSourcePos.X);
			float y1 = (float)(entity.Pos.Y + selectionBox.Y1 - ResultSourcePos.InternalY);
			float z1 = (float)(entity.Pos.Z + selectionBox.Z1 - ResultSourcePos.Z);
			float x2 = (float)(entity.Pos.X + selectionBox.X2 - ResultSourcePos.X);
			float y2 = (float)(entity.Pos.Y + selectionBox.Y2 - ResultSourcePos.InternalY);
			float z2 = (float)(entity.Pos.Z + selectionBox.Z2 - ResultSourcePos.Z);

			SetVehicleFillVertex(boxIndex, 0, x1, y1, z1);
			SetVehicleFillVertex(boxIndex, 1, x2, y1, z1);
			SetVehicleFillVertex(boxIndex, 2, x2, y2, z1);
			SetVehicleFillVertex(boxIndex, 3, x1, y2, z1);
			SetVehicleFillVertex(boxIndex, 4, x1, y1, z2);
			SetVehicleFillVertex(boxIndex, 5, x2, y1, z2);
			SetVehicleFillVertex(boxIndex, 6, x2, y2, z2);
			SetVehicleFillVertex(boxIndex, 7, x1, y2, z2);

			centerX = entity.Pos.X + (selectionBox.X1 + selectionBox.X2) * 0.5;
			centerY = entity.Pos.Y + (selectionBox.Y1 + selectionBox.Y2) * 0.5;
			centerZ = entity.Pos.Z + (selectionBox.Z1 + selectionBox.Z2) * 0.5;
		}

		CopyVehicleWireBoxFromFill(boxIndex);
		return true;
	}

	private void WriteStandardGaugeVehicleBox(EntityStandardGaugeLocomotive standardGaugeVehicle, int boxIndex, out double centerX, out double centerY, out double centerZ)
	{
		const double halfWidth = 1.0; // Parity with linkage-pole selection highlighting
		const double height = 4.0;
		const double bottomOffset = -0.15;

		double vehicleLength = Math.Max(0.1, standardGaugeVehicle.DebugVehicleLength);
		double frontOffset = standardGaugeVehicle.DebugBodyOffsetForward + 1;
		double rearOffset = frontOffset - vehicleLength;

		double yaw = standardGaugeVehicle.Pos.Yaw;
		double forwardX = Math.Sin(yaw);
		double forwardZ = Math.Cos(yaw);
		double rightX = Math.Cos(yaw);
		double rightZ = -Math.Sin(yaw);

		double baseX = standardGaugeVehicle.Pos.X - ResultSourcePos!.X;
		double baseY = standardGaugeVehicle.Pos.Y - ResultSourcePos.InternalY;
		double baseZ = standardGaugeVehicle.Pos.Z - ResultSourcePos.Z;
		double y0 = bottomOffset;
		double y1 = bottomOffset + height;

		SetOrientedVehicleFillVertex(boxIndex, 0, baseX, baseY, baseZ, forwardX, forwardZ, rightX, rightZ, rearOffset, -halfWidth, y0);
		SetOrientedVehicleFillVertex(boxIndex, 1, baseX, baseY, baseZ, forwardX, forwardZ, rightX, rightZ, frontOffset, -halfWidth, y0);
		SetOrientedVehicleFillVertex(boxIndex, 2, baseX, baseY, baseZ, forwardX, forwardZ, rightX, rightZ, frontOffset, -halfWidth, y1);
		SetOrientedVehicleFillVertex(boxIndex, 3, baseX, baseY, baseZ, forwardX, forwardZ, rightX, rightZ, rearOffset, -halfWidth, y1);
		SetOrientedVehicleFillVertex(boxIndex, 4, baseX, baseY, baseZ, forwardX, forwardZ, rightX, rightZ, rearOffset, halfWidth, y0);
		SetOrientedVehicleFillVertex(boxIndex, 5, baseX, baseY, baseZ, forwardX, forwardZ, rightX, rightZ, frontOffset, halfWidth, y0);
		SetOrientedVehicleFillVertex(boxIndex, 6, baseX, baseY, baseZ, forwardX, forwardZ, rightX, rightZ, frontOffset, halfWidth, y1);
		SetOrientedVehicleFillVertex(boxIndex, 7, baseX, baseY, baseZ, forwardX, forwardZ, rightX, rightZ, rearOffset, halfWidth, y1);

		double centerOffset = (frontOffset + rearOffset) * 0.5;
		centerX = standardGaugeVehicle.Pos.X + forwardX * centerOffset;
		centerY = standardGaugeVehicle.Pos.Y + bottomOffset + height * 0.5;
		centerZ = standardGaugeVehicle.Pos.Z + forwardZ * centerOffset;
	}

	private void SetOrientedVehicleFillVertex
	(
		int boxIndex, int cornerIndex,
		double baseX, double baseY, double baseZ,
		double forwardX, double forwardZ, double rightX, double rightZ,
		double length, double width, double verticalOffset
	)
	{
		SetVehicleFillVertex
		(
			boxIndex, cornerIndex,
			(float)(baseX + forwardX * length + rightX * width),
			(float)(baseY + verticalOffset),
			(float)(baseZ + forwardZ * length + rightZ * width)
		);
	}

	private void SetVehicleFillVertex(int boxIndex, int cornerIndex, float x, float y, float z)
	{
		if (VehicleFillMeshData == null) return;

		int coordinateOffset = (boxIndex * 8 + cornerIndex) * 3;
		VehicleFillMeshData.xyz[coordinateOffset] = x;
		VehicleFillMeshData.xyz[coordinateOffset + 1] = y;
		VehicleFillMeshData.xyz[coordinateOffset + 2] = z;
	}

	private void CopyVehicleWireBoxFromFill(int boxIndex)
	{
		if (VehicleFillMeshData == null || VehicleWireMeshData == null) return;

		float[] fillCoordinates = VehicleFillMeshData.xyz;
		float[] wireCoordinates = VehicleWireMeshData.xyz;
		int fillBase = boxIndex * 8;
		int wireBase = boxIndex * UnitCubeLineCornerIndices.Length;

		for (int lineCornerIndex = 0; lineCornerIndex < UnitCubeLineCornerIndices.Length; lineCornerIndex++)
		{
			int sourceOffset = (fillBase + UnitCubeLineCornerIndices[lineCornerIndex]) * 3;
			int destinationOffset = (wireBase + lineCornerIndex) * 3;
			wireCoordinates[destinationOffset] = fillCoordinates[sourceOffset];
			wireCoordinates[destinationOffset + 1] = fillCoordinates[sourceOffset + 1];
			wireCoordinates[destinationOffset + 2] = fillCoordinates[sourceOffset + 2];
		}
	}

	private void RenderSignalVisuals(IClientPlayer player)
	{
		if (ClientAPI == null || ResultSourcePos == null || SignalMeshOrigin == null) return;

		int visibleVehicleBoxes = UpdateDynamicClientMeshes(player);

		long nowMS = ClientAPI.World.ElapsedMilliseconds;
		bool splitPhase = (((nowMS - ResultStartedMS) / BlinkIntervalMS) & 1L) != 0;
		MeshRef? signalMesh = splitPhase ? SignalSplitMesh : SignalFullMesh;
		MeshRef? signalFillMesh = splitPhase ? SignalSplitFillMesh : SignalFullFillMesh;

		if (signalMesh == null && signalFillMesh == null && GuideMesh == null && visibleVehicleBoxes == 0) { return; }

		Vec3d camPos = player.Entity.CameraPos;
		IShaderProgram? previousShader = ClientAPI.Render.CurrentActiveShader;
		previousShader?.Stop();

		IShaderProgram shader = ClientAPI.Render.GetEngineShader(EnumShaderProgram.Autocamera);
		shader.Use();
		shader.UniformMatrix("projectionMatrix", ClientAPI.Render.CurrentProjectionMatrix);
		shader.UniformMatrix
		(
			"modelViewMatrix", MeshModelView
				.Set(ClientAPI.Render.CameraMatrixOriginf)
				.Translate( SignalMeshOrigin.X - camPos.X, SignalMeshOrigin.InternalY - camPos.Y, SignalMeshOrigin.Z - camPos.Z)
			.Values
		);

		try
		{
			ClientAPI.Render.GlDisableCullFace();
			ClientAPI.Render.GlToggleBlend(true);
			ClientAPI.Render.GLDepthMask(false);

			// Draw translucent volumes first, then wireframes so their edges remain crisp.
			if (signalFillMesh != null)									{ ClientAPI.Render.RenderMesh(signalFillMesh); }
			if (visibleVehicleBoxes > 0 && VehicleFillMesh != null)		{ ClientAPI.Render.RenderMesh(VehicleFillMesh); }
			if (signalMesh != null)										{ ClientAPI.Render.LineWidth = 3f; ClientAPI.Render.RenderMesh(signalMesh); }
			if (visibleVehicleBoxes > 0 && VehicleWireMesh != null)		{ ClientAPI.Render.LineWidth = 2.5f; ClientAPI.Render.RenderMesh(VehicleWireMesh); }
			if (GuideMesh != null)										{ ClientAPI.Render.LineWidth = 1.75f; ClientAPI.Render.RenderMesh(GuideMesh); }
		}
		finally
		{
			ClientAPI.Render.LineWidth = 1f;
			ClientAPI.Render.GLDepthMask(true);
			ClientAPI.Render.GlToggleBlend(false);
			ClientAPI.Render.GlEnableCullFace();
		}

		shader.Stop();
		previousShader?.Use();
	}


	private void OnClientTick(float _)
	{
		if (ClientAPI == null) return;

		ItemSlot? activeSlot = ClientAPI.World.Player?.InventoryManager?.ActiveHotbarSlot;
		bool held = activeSlot?.Itemstack?.Collectible is ItemNerdTool && ItemNerdTool.GetSelectedMode(activeSlot) == NerdToolMode.Signal;

		if (!held)
		{
			ClearAllClientState();
			StopClientTick();
			return;
		}

		if (ResultSourcePos == null) return;

		if (ClientAPI.World.ElapsedMilliseconds >= ResultExpiresMS) { ClearResult(); TrackHighlightRenderer?.InvalidateHoverSelection(); }
	}

	private void EnsureClientTick()
	{
		if (ClientAPI == null || ClientTickListenerID != 0) return;
		ClientTickListenerID = ClientAPI.Event.RegisterGameTickListener(OnClientTick, ClientTickMS);
	}

	private void StopClientTick()
	{
		if (ClientAPI == null || ClientTickListenerID == 0) return;
		ClientAPI.Event.UnregisterGameTickListener(ClientTickListenerID);
		ClientTickListenerID = 0;
	}

	private void ClearResult()
	{
		ResultSourcePos = null;
		ResultIsZoneQuery = false;
		ResultZoneOccupied = false;
		ResultTruncated = false;
		ResultStartedMS = 0;
		ResultExpiresMS = 0;

		TrackHighlightRenderer?.ClearResult();

		ClientSignals.Clear();
		ClientOccupancyOwnerIDs.Clear();
		SignalMeshOrigin = null;
		GuideMeshData = null;
		VehicleWireMeshData = null;
		VehicleFillMeshData = null;
		DeleteMesh(ref SignalFullMesh);
		DeleteMesh(ref SignalSplitMesh);
		DeleteMesh(ref SignalFullFillMesh);
		DeleteMesh(ref SignalSplitFillMesh);
		DeleteMesh(ref VehicleWireMesh);
		DeleteMesh(ref VehicleFillMesh);
		DeleteMesh(ref GuideMesh);
	}

	private void ClearAllClientState()
	{
		ClearResult();
		TrackHighlightRenderer?.ClearAll();
	}

	private void DeleteMesh(ref MeshRef? meshReference)
	{
		if (ClientAPI == null || meshReference == null) return;
		ClientAPI.Render.DeleteMesh(meshReference);
		meshReference = null;
	}

	private static SignalVisualState GetSignalVisualState(ClientSignalRecord signalRecord)
	{
		int applicableCount = 0;
		int allowedCount = 0;

		if (signalRecord.NegativeEntryApplicable) { applicableCount++; if (signalRecord.NegativeEntryAllowed) allowedCount++; }
		if (signalRecord.PositiveEntryApplicable) { applicableCount++; if (signalRecord.PositiveEntryAllowed) allowedCount++; }

		// A malformed directional record with no usable entry is safest to present as closed.
		if (applicableCount == 0 || allowedCount == 0)	return SignalVisualState.Red;
		if (allowedCount == applicableCount)			return SignalVisualState.Green;
														return SignalVisualState.Yellow;
	}

	private static int GetSignalWireColor(SignalVisualState state)
	{
		return state switch
		{
			SignalVisualState.Green => SignalGreenColor,
			SignalVisualState.Yellow => SignalYellowColor,
			_ => SignalRedColor
		};
	}

	private static int GetSignalFillColor(SignalVisualState state)
	{
		return state switch
		{
			SignalVisualState.Green => SignalGreenFillColor,
			SignalVisualState.Yellow => SignalYellowFillColor,
			_ => SignalRedFillColor
		};
	}

	private static void WriteSolidBox(MeshData mesh, float x1, float y1, float z1, float x2, float y2, float z2, int color)
	{
		int vertex = mesh.VerticesCount;
		mesh.AddVertexSkipTex(x1, y1, z1, color);
		mesh.AddVertexSkipTex(x2, y1, z1, color);
		mesh.AddVertexSkipTex(x2, y2, z1, color);
		mesh.AddVertexSkipTex(x1, y2, z1, color);
		mesh.AddVertexSkipTex(x1, y1, z2, color);
		mesh.AddVertexSkipTex(x2, y1, z2, color);
		mesh.AddVertexSkipTex(x2, y2, z2, color);
		mesh.AddVertexSkipTex(x1, y2, z2, color);

		for (int triangleIndex = 0; triangleIndex < UnitCubeTriangleIndices.Length; triangleIndex++) { mesh.AddIndex(vertex + UnitCubeTriangleIndices[triangleIndex]); }
	}

	private static void WriteWireBox(MeshData mesh, float x1, float y1, float z1, float x2, float y2, float z2, int color)
	{
		AddLine(mesh, x1, y1, z1, x2, y1, z1, color);
		AddLine(mesh, x2, y1, z1, x2, y1, z2, color);
		AddLine(mesh, x2, y1, z2, x1, y1, z2, color);
		AddLine(mesh, x1, y1, z2, x1, y1, z1, color);

		AddLine(mesh, x1, y2, z1, x2, y2, z1, color);
		AddLine(mesh, x2, y2, z1, x2, y2, z2, color);
		AddLine(mesh, x2, y2, z2, x1, y2, z2, color);
		AddLine(mesh, x1, y2, z2, x1, y2, z1, color);

		AddLine(mesh, x1, y1, z1, x1, y2, z1, color);
		AddLine(mesh, x2, y1, z1, x2, y2, z1, color);
		AddLine(mesh, x2, y1, z2, x2, y2, z2, color);
		AddLine(mesh, x1, y1, z2, x1, y2, z2, color);
	}

	private static void AddLine(MeshData mesh, float x1, float y1, float z1, float x2, float y2, float z2, int color)
	{
		int vertex = mesh.VerticesCount;
		mesh.AddVertexSkipTex(x1, y1, z1, color);
		mesh.AddIndex(vertex);
		mesh.AddVertexSkipTex(x2, y2, z2, color);
		mesh.AddIndex(vertex + 1);
	}

	private static byte[] PackSnapshotPayload(
		BlockPos sourcePos,
		List<SignalSnapshotRecord> signalRecords,
		List<long> occupancyOwnerIDs)
	{
		using MemoryStream stream = new(Math.Max(8, signalRecords.Count * 6 + occupancyOwnerIDs.Count * 3));
		WriteVarUInt(stream, (ulong)signalRecords.Count);

		for (int signalIndex = 0; signalIndex < signalRecords.Count; signalIndex++)
		{
			SignalSnapshotRecord signalRecord = signalRecords[signalIndex];
			WriteVarInt(stream, (long)signalRecord.X - sourcePos.X);
			WriteVarInt(stream, (long)signalRecord.Y - sourcePos.Y);
			WriteVarInt(stream, (long)signalRecord.Z - sourcePos.Z);
			stream.WriteByte(signalRecord.Flags);
		}

		WriteVarUInt(stream, (ulong)occupancyOwnerIDs.Count);
		for (int ownerIndex = 0; ownerIndex < occupancyOwnerIDs.Count; ownerIndex++) { WriteVarUInt(stream, (ulong)occupancyOwnerIDs[ownerIndex]); }

		return stream.ToArray();
	}

	private static bool TryDecodeSnapshotPayload( BlockPos sourcePosition, byte[] packedData, List<ClientSignalRecord> signalDestination, List<long> ownerDestination)
	{
		signalDestination.Clear();
		ownerDestination.Clear();

		using MemoryStream stream = new(packedData, writable: false);
		if (!TryReadVarUInt(stream, out ulong rawSignalCount) || rawSignalCount > MaxSignalRecords) { return false; }

		int signalCount = (int)rawSignalCount;
		for (int signalIndex = 0; signalIndex < signalCount; signalIndex++)
		{
			if (!TryReadVarInt(stream, out long dx) || !TryReadVarInt(stream, out long dy) || !TryReadVarInt(stream, out long dz))
			{
				signalDestination.Clear();
				return false;
			}

			int rawFlags = stream.ReadByte();
			if (rawFlags < 0)
			{
				signalDestination.Clear();
				return false;
			}

			long x = (long)sourcePosition.X + dx;
			long y = (long)sourcePosition.Y + dy;
			long z = (long)sourcePosition.Z + dz;
			if (x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue || z < int.MinValue || z > int.MaxValue)
			{
				signalDestination.Clear();
				return false;
			}

			byte snapshotFlags = (byte)rawFlags;
			signalDestination.Add
			(
				new ClientSignalRecord
				{
					X = (int)x, Y = (int)y, Z = (int)z,
					Axis = (snapshotFlags & SignalAxisZ) != 0 ? SignalAxis.Z : SignalAxis.X,
					Gauge = (byte)((snapshotFlags & SignalGaugeOne) != 0 ? 1 : 0),
					NegativeEntryAllowed = (snapshotFlags & SignalEntryFromNegativeAllowed) != 0,
					PositiveEntryAllowed = (snapshotFlags & SignalEntryFromPositiveAllowed) != 0,
					NegativeEntryApplicable = (snapshotFlags & SignalNegativeEntryApplicable) != 0,
					PositiveEntryApplicable = (snapshotFlags & SignalPositiveEntryApplicable) != 0,
					ExitFromClickedZoneAllowed = (snapshotFlags & SignalExitFromClickedZoneAllowed) != 0
				}
			);
		}

		if (!TryReadVarUInt(stream, out ulong rawOwnerCount) ||
			rawOwnerCount > MaxOccupancyOwnerRecords)
		{
			signalDestination.Clear();
			return false;
		}

		int ownerCount = (int)rawOwnerCount;
		for (int ownerIndex = 0; ownerIndex < ownerCount; ownerIndex++)
		{
			if (!TryReadVarUInt(stream, out ulong rawOwnerID) || rawOwnerID == 0 || rawOwnerID > long.MaxValue)
			{
				signalDestination.Clear();
				ownerDestination.Clear();
				return false;
			}

			ownerDestination.Add((long)rawOwnerID);
		}

		if (stream.Position != stream.Length)
		{
			signalDestination.Clear();
			ownerDestination.Clear();
			return false;
		}

		return true;
	}

	private static void WriteVarUInt(Stream stream, ulong value)
	{
		while (value >= 0x80)
		{
			stream.WriteByte((byte)((value & 0x7F) | 0x80));
			value >>= 7;
		}

		stream.WriteByte((byte)value);
	}

	private static void WriteVarInt(Stream stream, long value)
	{
		ulong zigzag = unchecked((ulong)((value << 1) ^ (value >> 63)));
		WriteVarUInt(stream, zigzag);
	}

	private static bool TryReadVarUInt(Stream stream, out ulong value)
	{
		value = 0;

		for (int shift = 0; shift <= 63; shift += 7)
		{
			int rawByte = stream.ReadByte();
			if (rawByte < 0) return false;

			byte currentByte = (byte)rawByte;
			if (shift == 63 && (currentByte & 0xFE) != 0) return false;

			value |= (ulong)(currentByte & 0x7F) << shift;
			if ((currentByte & 0x80) == 0) return true;
		}

		return false;
	}

	private static bool TryReadVarInt(Stream stream, out long value)
	{
		value = 0;
		if (!TryReadVarUInt(stream, out ulong zigzag)) return false;

		value = (long)(zigzag >> 1) ^ -((long)zigzag & 1L);
		return true;
	}

	public override void Dispose()
	{
		if (ClientAPI != null)
		{
			StopClientTick();
			ClearAllClientState();
			TrackHighlightRenderer?.Dispose();
			TrackHighlightRenderer = null;
			ClientSignalBoundsByBlockID.Clear();
		}

		ServerEdgeHashes.Clear();
		ServerSnapshots.Clear();
		ServerOccupancyOwnerIDs.Clear();
		ClientOccupancyOwnerIDs.Clear();
		CachedSignals.Clear();
		CachedSignalsByZoneID.Clear();
		CachedGraphVersion = -1;

		base.Dispose();
	}

	private enum SignalVisualState : byte
	{
		Green = 0,
		Yellow = 1,
		Red = 2
	}

	private enum SignalAxis : byte
	{
		X = 0,
		Z = 1
	}

	private struct SignalTopologyRecord
	{
		public int X;
		public int Y;
		public int Z;
		public byte Gauge;
		public SignalAxis Axis;
		public ulong NegativeZoneID;
		public ulong PositiveZoneID;
		public bool StructuralNegativeEntryAllowed;
		public bool StructuralPositiveEntryAllowed;
	}

	private struct SignalSnapshotRecord
	{
		public int X;
		public int Y;
		public int Z;
		public byte Flags;
	}

	private struct ClientSignalRecord
	{
		public int X;
		public int Y;
		public int Z;
		public byte Gauge;
		public SignalAxis Axis;
		public bool NegativeEntryAllowed;
		public bool PositiveEntryAllowed;
		public bool NegativeEntryApplicable;
		public bool PositiveEntryApplicable;
		public bool ExitFromClickedZoneAllowed;
		public double CenterX;
		public double CenterY;
		public double CenterZ;
	}

	private readonly struct SignalBoxBounds
	{
		internal static readonly SignalBoxBounds Unit = new(0, 0, 0, 1, 1, 1);

		internal readonly float X1;
		internal readonly float Y1;
		internal readonly float Z1;
		internal readonly float X2;
		internal readonly float Y2;
		internal readonly float Z2;

		internal SignalBoxBounds(float x1, float y1, float z1, float x2, float y2, float z2)
		{
			X1 = x1;
			Y1 = y1;
			Z1 = z1;
			X2 = x2;
			Y2 = y2;
			Z2 = z2;
		}
	}
}
