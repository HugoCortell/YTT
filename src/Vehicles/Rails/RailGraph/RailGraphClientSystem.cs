using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace YangTransport;

/// Client-owned mirror of the server rail graph. Rendering and debug systems use this instead of privately owning their own graph/channel copy.
public sealed class RailGraphClientSystem : ModSystem
{
	private const string ChannelName = "yangtransport_railgraph";
	private const int RequestThrottleMS = 500;

	private ICoreClientAPI? ClientAPI;
	private IClientNetworkChannel? NetworkChannel;

	private readonly RailGraphLive LiveGraph = new();

	private long NextDetailedRequestMS;
	private long NextAnyRequestMS;
	private bool GraphAvailable;
	private bool DetailedGraphAvailable;

	public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

	internal event Action<RailGraphLive, bool>? GraphUpdated;

	internal RailGraphLive Graph => LiveGraph;
	internal int BuildVersion => GraphAvailable ? LiveGraph.BuildVersion : -1;
	internal bool HasGraph => GraphAvailable;
	internal bool HasDetailedGraph => GraphAvailable && DetailedGraphAvailable;
	internal bool LastResponseIncludedDebugOccupancy { get; private set; }
	internal RailGraphDebugEdgeOccupancy[] LastDebugEdgeOccupancy { get; private set; } = Array.Empty<RailGraphDebugEdgeOccupancy>();
	internal ulong LastDebugOccupancySerial { get; private set; }
	internal int LastDebugOccupancyGraphVersion { get; private set; } = -1;

	public override void StartClientSide(ICoreClientAPI coreClientAPI)
	{
		ClientAPI = coreClientAPI;

		NetworkChannel = coreClientAPI.Network
			.RegisterChannel(ChannelName)
			.RegisterMessageType<RailGraphRequest>()
			.RegisterMessageType<RailGraphResponse>();

		NetworkChannel.SetMessageHandler<RailGraphResponse>(OnGraphResponse);
	}

	public override void Dispose()
	{
		GraphUpdated = null;
		base.Dispose();
	}

	internal bool TryGetGraph(out RailGraphLive liveGraph, bool requirePolylines = true)
	{
		liveGraph = LiveGraph;
		return GraphAvailable && (!requirePolylines || DetailedGraphAvailable);
	}

	internal void RequestDetailedAround(BlockPos center)
	{
		if (center == null) return;
		long now = ClientAPI?.World.ElapsedMilliseconds ?? 0;
		if (now < NextDetailedRequestMS) return;

		SendRequest(center, detailed: true, lastVersion: DetailedGraphAvailable ? LiveGraph.BuildVersion : -1, includeDebugOccupancy: false);
		NextDetailedRequestMS = now + RequestThrottleMS;
	}

	internal void RequestGraph(BlockPos center, bool detailed, int lastVersion, bool includeDebugOccupancy = false)
	{
		if (center == null) return;
		long now = ClientAPI?.World.ElapsedMilliseconds ?? 0;
		if (now < NextAnyRequestMS) return;

		SendRequest(center, detailed, lastVersion, includeDebugOccupancy);
		NextAnyRequestMS = now + RequestThrottleMS;
		if (detailed) NextDetailedRequestMS = Math.Max(NextDetailedRequestMS, now + RequestThrottleMS);
	}

	internal void RequestLocalRebuild(BlockPos center)
	{
		if (center == null || NetworkChannel == null) return;

		NetworkChannel.SendPacket(new RailGraphRequest
		{
			CenterX = center.X,
			CenterY = center.Y,
			CenterZ = center.Z,
			Dimension = center.dimension,
			ForceRebuild = true
		});
	}

	private void SendRequest(BlockPos center, bool detailed, int lastVersion, bool includeDebugOccupancy)
	{
		if (NetworkChannel == null) return;
		if (DetailedGraphAvailable) detailed = true;

		NetworkChannel.SendPacket(new RailGraphRequest
		{
			CenterX = center.X,
			CenterY = center.Y,
			CenterZ = center.Z,
			Dimension = center.dimension,

			Detailed = detailed,
			LastBuildVersion = lastVersion,
			ForceRebuild = false,
			Radius = detailed ? 96 : 0,
			IncludeDebugOccupancy = detailed && includeDebugOccupancy
		});
	}

	private void OnGraphResponse(RailGraphResponse graphResponse)
	{
		if (ClientAPI == null) return;
		if (graphResponse.GraphData == null || graphResponse.GraphData.Length == 0) return;
		if (graphResponse.Truncated)
		{
			ClientAPI.Logger.Warning
			(
				"[yangtransport] Detailed railgraph response was capped at {0} edges / {1} points.",
				graphResponse.SerializedEdges, graphResponse.SerializedPoints
			);
		}

		if (!LiveGraph.TryLoad(graphResponse.GraphData))
		{
			ClientAPI.World.Logger.Warning("[yangtransport] RailGraphResponse: failed to deserialize graph payload.");
			return;
		}

		RailClearanceLayer.ApplyClientState(LiveGraph, graphResponse.ClearanceData);

		LastResponseIncludedDebugOccupancy = graphResponse.DebugOccupancyIncluded;
		if (graphResponse.DebugOccupancyIncluded)
		{
			LastDebugEdgeOccupancy = graphResponse.DebugEdgeOccupancy ?? Array.Empty<RailGraphDebugEdgeOccupancy>();
			LastDebugOccupancySerial = graphResponse.DebugOccupancySerial;
			LastDebugOccupancyGraphVersion = graphResponse.BuildVersion;
		}

		GraphAvailable = true;
		DetailedGraphAvailable = graphResponse.Detailed;

		GraphUpdated?.Invoke(LiveGraph, DetailedGraphAvailable);
	}
}
