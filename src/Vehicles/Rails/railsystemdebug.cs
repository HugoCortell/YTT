using System;
using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using System.Text;

namespace YangTransport;

/// Debug visualization for the server-owned rail graph.
/// Controlled by the YangTransport debug dialog. The .railgraphrebuild command remains available for quick use.
public sealed class RailSystemDebug : ModSystem, IRenderer, IDisposable
{
	private ICoreClientAPI? ClientAPI;
	private ICoreServerAPI? ServerAPI;

	private IClientNetworkChannel? ClientAutomationNetwork;
	private IServerNetworkChannel? ServerAutomationNetwork;
	private RailGraphClientSystem? ClientRailGraphSystem;

	private RailGraphServerSystem? ServerRailGraphSystem;
	private RailAutomationPathingSystem? ServerAutomationSystem;

	private RailGraphSnapshot Snapshot = RailGraphSnapshot.Empty;

	private bool ShowDetailed;
	private bool ShowZones;
	private bool ShowGrid;
	private bool ShowClearance;
	private bool ShowAutomationDebug, RendererRegistered, ShowSGSizeTest;

	// Snapshot detail level we currently have cached from the server (polylines included or not).
	private bool SnapshotHasPolylines;

	// Per-overlay rebuild tracking.
	private int LastGraphBuildVersion = -1;

	private int LastZonesBuildVersion = -1;

	private int LastGridGraphBuildVersion = -1;
	private int LastClearanceBuildVersion = -1;
	private int LastGridCellX = int.MinValue;
	private int LastGridCellZ = int.MinValue;
	private int LastGridDimension = int.MinValue;
	private double LastGridY = double.NaN;
	private const int GridMaxDistanceDown = 256;

	private long NextRequestMS;

	private readonly Matrixf ModelViewMatrix = new();

	// We render in buckets, because the Wireframe shader uses a uniform color (colorIn), not per-vertex colors.
	// Bucket 0 is nodes (white), buckets 1..5 are rail colors, and the last bucket is signal overlays.
	private const int RailColorCount = 5;

	// Bucket layout:
	// 0						-> graph nodes (white)
	// 1..5						-> graph lines (5-color cycling)
	// 6..9						-> signal overlays (green/red/yellow/magenta)
	// 10..14					-> occupancy zone lines (same 5 colors, separate buckets)
	// 15..16					-> rail hashgrid overlay (white + red)
	// 17..22					-> automation debug locomotive/path colors
	// 23..24					-> clearance overlay (green clear, red blocked)
	private const int GraphNodeBucket			= 0;
	private const int GraphRailBucketStart		= 1;
	private const int SignalGreenBucket			= GraphRailBucketStart	+ RailColorCount;
	private const int SignalRedBucket			= SignalGreenBucket		+ 1;
	private const int SignalYellowBucket		= SignalRedBucket		+ 1;
	private const int SignalUnknownBucket		= SignalYellowBucket	+ 1;
	private const int ZoneRailBucketStart		= SignalUnknownBucket	+ 1;
	private const int GridBucketStart			= ZoneRailBucketStart	+ RailColorCount;
	private const int GridBucketWhite			= GridBucketStart		+ 0;
	private const int GridBucketRed				= GridBucketStart		+ 1;
	private const int AutomationBucketStart		= GridBucketStart		+ 2;
	private const int AutomationBucketCount		= 6;
	private const int ClearanceBucketStart		= AutomationBucketStart	+ AutomationBucketCount;
	private const int ClearanceGreenBucket		= ClearanceBucketStart	+ 0;
	private const int ClearanceRedBucket		= ClearanceBucketStart	+ 1;

	private const int BucketCount = ClearanceBucketStart + 2;

	private readonly List<Line>[] BucketLines = new List<Line>[BucketCount];
	private readonly List<Line>[] DynamicBucketLines = new List<Line>[BucketCount];

	private readonly MeshData?[] BucketMeshData = new MeshData?[BucketCount];
	private readonly MeshRef?[] BucketMeshReferences = new MeshRef?[BucketCount];
	private readonly int[] BucketMeshCapacityVertices = new int[BucketCount];

	private static readonly Vec4f[] BucketColors = new[]
	{
		new Vec4f(1f, 1f, 1f, 1f),				// 0 graph nodes: white

		new Vec4f(1f, 0.2f, 0f, 1f),			// 1 graph rail #FF3300
		new Vec4f(0.4f, 0.8f, 0.2f, 1f),		// 2 graph rail #66CC33
		new Vec4f(0.2f, 0.8f, 1f, 1f),			// 3 graph rail #33CCFF
		new Vec4f(0.8f, 0.2f, 1f, 1f),			// 4 graph rail #CC33FF
		new Vec4f(1f, 0.8f, 0f, 1f),			// 5 graph rail #FFCC00

		new Vec4f(0.2f, 1f, 0.2f, 1f),			// 6 signals: green (clear)
		new Vec4f(1f, 0.2f, 0.2f, 1f),			// 7 signals: red (blocked)
		new Vec4f(1f, 0.85f, 0.1f, 1f), 		// 8 signals: yellow (partially blocked)
		new Vec4f(1f, 0f, 1f, 1f),				// 9 signals: magenta (unknown/broken)

		new Vec4f(1f, 0.2f, 0f, 1f),			// 10 zone rail #FF3300
		new Vec4f(0.4f, 0.8f, 0.2f, 1f),		// 11 zone rail #66CC33
		new Vec4f(0.2f, 0.8f, 1f, 1f),			// 12 zone rail #33CCFF
		new Vec4f(0.8f, 0.2f, 1f, 1f),			// 13 zone rail #CC33FF
		new Vec4f(1f, 0.8f, 0f, 1f),			// 14 zone rail #FFCC00

		new Vec4f(1f, 1f, 1f, 1f),				// 15 grid: white
		new Vec4f(1f, 0.2f, 0f, 1f),			// 16 grid: red

		new Vec4f(1f, 0.4f, 0f, 1f),			// 17 automation: #FF6600 | Orange
		new Vec4f(0.4f, 0.6f, 0.2f, 1f),		// 18 automation: #669933 | Green
		new Vec4f(0.2f, 0.4f, 0.8f, 1f),		// 19 automation: #3366CC | Blue
		new Vec4f(1f, 0.9f, 0.05f, 1f),			// 20 automation: yellow | Yellow
		new Vec4f(0f, 0.8f, 0.6f, 1f),			// 21 automation: #00CC99 | Teal
		new Vec4f(0.8f, 0.2f, 0.4f, 1f),		// 22 automation: #CC3366 | Magenta

		new Vec4f(0.2f, 1f, 0.2f, 1f),			// 23 clearance: green clear
		new Vec4f(1f, 0.2f, 0.2f, 1f),			// 24 clearance: red blocked
	};

	private readonly Random RandomGenerator = new();
	private int GraphColorSeed;
	private int ZoneColorSeed;
	private readonly Dictionary<int, int> SegmentColorIndex = new(); // segmentId -> palette index (0..4)
	private readonly Dictionary<ulong, int> ZoneColorIndex = new(); // zoneId -> palette index (0..4)

	private const string AutomationDebugChannelName = "yangtransport_automationdebug";
	private const int AutomationDebugUpdateMS = 500;
	private const int AutomationDebugMaxLocomotives = 6;
	private const double AutomationDebugMaxDistanceBlocks = 256.0;
	private const int MaxAutomationDebugPathPointsPerLocomotive = 768;
	private const int MaxAutomationDebugPathEdgesPerLocomotive = 512;
	private const int MaxSignalOccupantLinesPerSignal = 4;
	private const int AutomationDebugPathBreak = int.MinValue;
	private const long MaxAutomationDebugPathJoinSquaredDistance16 = 2L * 2L; // adjacent rail-edge endpoints should match exactly; tolerate tiny rounding only
	private const long MaxAutomationDebugRenderSegmentSquaredDistance16 = 128L * 128L; // last-resort guard against accidental long jump lines

	private static readonly string[] AutomationSlotNames = { "Orange", "Green", "Blue", "Yellow", "Teal", "Magenta" };

	private readonly Dictionary<IServerPlayer, AutomationDebugFeatures> AutomationSubscribers = new();
	private readonly Dictionary<string, AutomationDebugSlotState> AutomationSlotStateByPlayerID = new();
	private readonly List<IRailwayConvoyVehicle> ServerLocomotivesScratch = new(64);
	private readonly Dictionary<long, IRailwayConvoyVehicle> ServerLocomotiveByIDScratch = new(64);
	private readonly Dictionary<long, double> ServerLocomotiveSquaredDistanceByIDScratch = new(64);
	private readonly HashSet<long> ServerAssignedLocomotiveIDsScratch = new();
	private readonly List<AutomationDebugLocomotive> ServerLocomotivePacketsScratch = new(AutomationDebugMaxLocomotives);
	private readonly List<AutomationDebugSignalEdgeState> ServerSignalEdgePacketsScratch = new(64);
	private readonly Dictionary<long, AutomationDebugOwnerPosition> ServerOwnerPositionByIDScratch = new();
	private readonly List<AutomationDebugOwnerPosition> ServerOwnerPositionPacketsScratch = new(32);
	private readonly List<int> ServerPathCoordinatesScratch = new(MaxAutomationDebugPathPointsPerLocomotive * 3);
	private readonly List<ulong> ServerPathEdgesScratch = new(MaxAutomationDebugPathEdgesPerLocomotive);
	private long AutomationServerTickListenerID;

	private AutomationDebugLocomotive[] AutomationLocomotives = Array.Empty<AutomationDebugLocomotive>();
	private readonly Dictionary<long, AutomationDebugLocomotive> AutomationLocomotiveByID = new();
	private readonly Dictionary<long, int> AutomationBucketMaskByOwnerID = new();
	private readonly List<ulong> TrackHeadsUpDisplayEdgeHashesScratch = new(4);
	private readonly List<ulong> TrackHeadsUpDisplayZoneIDsScratch = new(2);
	private readonly Dictionary<ulong, RailGraphDebugEdgeOccupancy> AuthoritativeOccupancyByEdge = new();
	private int AuthoritativeOccupancyGraphVersion = -1;
	private ulong AuthoritativeOccupancySerial;
	private readonly Dictionary<ulong, AutomationDebugSignalEdgeState> AutomationSignalStateByEdge = new();
	private int AutomationSignalGraphVersion = -1;
	private ulong AutomationSignalOccupancySerial;
	private readonly Dictionary<long, AutomationDebugOwnerPosition> AutomationOwnerPositions = new();
	private readonly Dictionary<SignalHeadsUpDisplayKey, string> SignalHeadsUpDisplayInformationByPosition = new();
	private bool AutomationStaticDirty;

	private static RailSystemDebug? ClientInstance;
	internal static bool ClientAutomationDebugHUDEnabled;
	internal static bool ClientSignalDebugHUDEnabled;
	internal static bool ClientTrackZoneDebugHUDEnabled => ClientAutomationDebugHUDEnabled && ClientSignalDebugHUDEnabled;


	public override void StartServerSide(ICoreServerAPI serverAPI)
	{
		ServerAPI = serverAPI;
		ServerRailGraphSystem = serverAPI.ModLoader.GetModSystem<RailGraphServerSystem>();
		ServerAutomationSystem = serverAPI.ModLoader.GetModSystem<RailAutomationPathingSystem>();

		ServerAutomationNetwork = serverAPI.Network
			.RegisterChannel(AutomationDebugChannelName)
			.RegisterMessageType<AutomationDebugToggle>()
			.RegisterMessageType<AutomationDebugUpdate>();

		ServerAutomationNetwork.SetMessageHandler<AutomationDebugToggle>(OnAutomationDebugToggleFromClient);

		serverAPI.Event.PlayerLeave += OnAutomationDebugPlayerLeave;
	}

	public override void StartClientSide(ICoreClientAPI clientAPI)
	{
		ClientAPI = clientAPI;
		ClientInstance = this;

		for (int bucketIndex = 0; bucketIndex < BucketCount; bucketIndex++)
		{
			BucketLines[bucketIndex] = new List<Line>(256);
			DynamicBucketLines[bucketIndex] = new List<Line>(16);
		}

		ClientRailGraphSystem = clientAPI.ModLoader.GetModSystem<RailGraphClientSystem>();
		if (ClientRailGraphSystem != null) ClientRailGraphSystem.GraphUpdated += OnGraphUpdated;

		ClientAutomationNetwork = clientAPI.Network
			.RegisterChannel(AutomationDebugChannelName)
			.RegisterMessageType<AutomationDebugToggle>()
			.RegisterMessageType<AutomationDebugUpdate>();

		ClientAutomationNetwork.SetMessageHandler<AutomationDebugUpdate>(OnAutomationDebugUpdate);

		clientAPI.ChatCommands
			.Create("railgraphrebuild")
			.RequiresPrivilege(Privilege.controlserver)
			.WithDescription("Trigger a re-build of the railgraph around you.")
			.HandleWith(OnCommandForceRebuild);

		if (RendererRegistered)
		{
			ClientAPI.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
			RendererRegistered = false;
		}
	}

	public override void Dispose()
	{
		if (ServerAPI != null)
		{
			ServerAPI.Event.PlayerLeave -= OnAutomationDebugPlayerLeave;
			StopAutomationServerUpdates();
		}

		if (ClientRailGraphSystem != null)
		{
			ClientRailGraphSystem.GraphUpdated -= OnGraphUpdated;
			ClientRailGraphSystem = null;
		}

		if (ClientInstance == this)
		{
			ClientInstance = null;
			ClientAutomationDebugHUDEnabled = false;
			ClientSignalDebugHUDEnabled = false;
		}

		if (ClientAPI != null)
		{
			if (RendererRegistered)
			{
				ClientAPI.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
				RendererRegistered = false;
			}

			for (int bucketIndex = 0; bucketIndex < BucketCount; bucketIndex++)
			{
				if (BucketMeshReferences[bucketIndex] != null)
				{
					ClientAPI.Render.DeleteMesh(BucketMeshReferences[bucketIndex]);
					BucketMeshReferences[bucketIndex] = null;
				}
			}
		}

		base.Dispose();
	}

	private bool HasAnyDebugOverlayEnabled()
	{
		return ShowDetailed || ShowZones || ShowGrid || ShowClearance || ShowAutomationDebug || ShowSGSizeTest;
	}

	private void UpdateRendererRegistration()
	{
		if (ClientAPI == null) return;

		bool shouldRender = HasAnyDebugOverlayEnabled();
		if (shouldRender == RendererRegistered) return;

		if (shouldRender) { ClientAPI.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "yangtransport:railgraphdebug"); }
		else { ClientAPI.Event.UnregisterRenderer(this, EnumRenderStage.Opaque); }

		RendererRegistered = shouldRender;
	}

	internal bool DetailedGraphEnabled => ShowDetailed;
	internal bool GridEnabled => ShowGrid;
	internal bool SignalDebugEnabled => ShowZones;
	internal bool AutomationDebugEnabled => ShowAutomationDebug;
	internal bool ClearanceDebugEnabled => ShowClearance;
	internal bool SGSizeEstimateEnabled => ShowSGSizeTest;

	internal void SetDetailedGraphEnabled(bool enabled)
	{
		if (ShowDetailed == enabled) return;
		ShowDetailed = enabled;

		if (ShowDetailed) { GraphColorSeed = RandomGenerator.Next(); }
		else { ClearBuckets(GraphNodeBucket, 1 + RailColorCount); }

		UpdateRendererRegistration();
		Invalidate();
	}

	internal void SetSignalDebugEnabled(bool enabled)
	{
		if (ShowZones == enabled) return;
		ShowZones = enabled;
		ClientSignalDebugHUDEnabled = ShowZones;

		if (ShowZones) { ZoneColorSeed = RandomGenerator.Next(); }
		else
		{
			ClearBuckets(SignalGreenBucket, 4 + RailColorCount);
			SignalHeadsUpDisplayInformationByPosition.Clear();
			AuthoritativeOccupancyByEdge.Clear();
			AuthoritativeOccupancyGraphVersion = -1;
			AuthoritativeOccupancySerial = 0;
			AutomationSignalStateByEdge.Clear();
			AutomationSignalGraphVersion = -1;
			AutomationSignalOccupancySerial = 0;
			AutomationOwnerPositions.Clear();
		}

		SendAutomationDebugFeatures();
		UpdateRendererRegistration();
		Invalidate();
	}

	internal void SetAutomationDebugEnabled(bool enabled)
	{
		if (ShowAutomationDebug == enabled) return;
		ShowAutomationDebug = enabled;
		ClientAutomationDebugHUDEnabled = ShowAutomationDebug;

		if (!ShowAutomationDebug)
		{
			ClearBuckets(AutomationBucketStart, AutomationBucketCount);
			ClearDynamicBuckets(AutomationBucketStart, AutomationBucketCount);
			AutomationLocomotives = Array.Empty<AutomationDebugLocomotive>();
			AutomationLocomotiveByID.Clear();
			AutomationBucketMaskByOwnerID.Clear();
			AutomationSignalStateByEdge.Clear();
			AutomationSignalGraphVersion = -1;
			AutomationSignalOccupancySerial = 0;
			AutomationOwnerPositions.Clear();
			SignalHeadsUpDisplayInformationByPosition.Clear();
			LastZonesBuildVersion = -1;
		}

		if (ShowZones) LastZonesBuildVersion = -1;

		SendAutomationDebugFeatures();
		AutomationStaticDirty = true;
		UpdateRendererRegistration();
	}

	private void SendAutomationDebugFeatures()
	{
		AutomationDebugFeatures features = AutomationDebugFeatures.None;
		if (ShowAutomationDebug)
		{
			features |= AutomationDebugFeatures.Routes;
			if (ShowZones) features |= AutomationDebugFeatures.SignalOccupants;
		}

		ClientAutomationNetwork?.SendPacket(new AutomationDebugToggle { Features = features });
	}

	internal void SetSGSizeEstimateEnabled(bool enabled)
	{
		if (ShowSGSizeTest == enabled) return;
		ShowSGSizeTest = enabled;
		if (!ShowSGSizeTest) ClearDynamicBuckets(SignalUnknownBucket, 1);

		UpdateRendererRegistration();
		AutomationStaticDirty = true;
	}

	internal void SetGridEnabled(bool enabled)
	{
		if (ShowGrid == enabled) return;
		ShowGrid = enabled;
		if (!ShowGrid) ClearBuckets(GridBucketStart, 2);

		UpdateRendererRegistration();
		Invalidate();
	}

	internal void SetClearanceDebugEnabled(bool enabled)
	{
		if (ShowClearance == enabled) return;
		ShowClearance = enabled;
		if (!ShowClearance) ClearBuckets(ClearanceBucketStart, 2);

		UpdateRendererRegistration();
		Invalidate();
	}

	private TextCommandResult OnCommandForceRebuild(TextCommandCallingArgs arguments)
	{
		if (ClientAPI == null || ClientRailGraphSystem == null) return TextCommandResult.Error("Rail graph client is not initialized.");

		BlockPos? centerPosition = ClientAPI.World.Player?.Entity?.Pos?.AsBlockPos;
		if (centerPosition == null) return TextCommandResult.Error("No local player.");

		ClientRailGraphSystem.RequestLocalRebuild(centerPosition);
		return TextCommandResult.Success("Requested local rail graph rebuild.");
	}


	private void Invalidate()
	{
		LastGraphBuildVersion = -1;
		LastZonesBuildVersion = -1;

		LastGridGraphBuildVersion = -1;
		LastClearanceBuildVersion = -1;
		LastGridCellX = LastGridCellZ = LastGridDimension = int.MinValue;
		LastGridY = double.NaN;

		SegmentColorIndex.Clear();
		ZoneColorIndex.Clear();

		// Force a fresh request next frame.
		NextRequestMS = 0;
	}

	public double RenderOrder => 0.9;
	public int RenderRange => 9999;

	public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
	{
		if (ClientAPI == null || ClientRailGraphSystem == null) return;
		if (!ShowDetailed && !ShowZones && !ShowGrid && !ShowClearance && !ShowAutomationDebug && !ShowSGSizeTest) return;

		EntityPlayer? player = ClientAPI.World.Player?.Entity;
		if (player == null) return;

		BlockPos centerPosition = player.Pos.AsBlockPos;

		// Poll server (throttled); server responds only if version changed, unless we need to switch detailed/non-detailed payloads.
		bool wantGraphSnapshot = ShowDetailed || ShowZones || ShowGrid || ShowClearance;
		bool wantDetailedSnapshot = ShowDetailed || ShowZones || ShowClearance;
		long nowMS = ClientAPI.World.ElapsedMilliseconds;
		if (wantGraphSnapshot && nowMS >= NextRequestMS)
		{
			int lastVersion = (ShowZones || ShowClearance) ? -1 : ((wantDetailedSnapshot == SnapshotHasPolylines) ? Snapshot.BuildVersion : -1);
			SendRequest(centerPosition, detailed: wantDetailedSnapshot, lastVersion: lastVersion, includeDebugOccupancy: ShowZones);
			NextRequestMS = nowMS + 500;
		}

		bool anyRebuild = false;

		// Detailed graph overlay uses buckets 0..5.
		if (ShowDetailed)
		{
			bool needGraphRebuild = Snapshot.BuildVersion != LastGraphBuildVersion;

			if (needGraphRebuild)
			{
				anyRebuild = true;
				LastGraphBuildVersion = Snapshot.BuildVersion;

				ComputeSegmentColors(Snapshot, SegmentColorIndex, GraphColorSeed);
				ClearBuckets(GraphNodeBucket, 1 + RailColorCount);
				RebuildGraphBuckets(Snapshot, SegmentColorIndex);
			}
		}

		// Occupancy zones overlay uses buckets 6..11
		if (ShowZones)
		{
			bool needZonesRebuild = Snapshot.BuildVersion != LastZonesBuildVersion;

			if (needZonesRebuild)
			{
				anyRebuild = true;
				LastZonesBuildVersion = Snapshot.BuildVersion;

				if (!ShowAutomationDebug) ComputeZoneColors(Snapshot, ZoneColorIndex, ZoneColorSeed);
				ClearBuckets(SignalGreenBucket, 4 + RailColorCount);
				RebuildZoneBuckets(Snapshot);
			}
		}

		// Hashgrid overlay uses buckets 12..13
		double gridY = 0;
		if (ShowGrid)
		{
			int shift = ClientRailGraphSystem.Graph.EdgeGridCellShift;
			int gridCellX = centerPosition.X >> shift;
			int gridCellZ = centerPosition.Z >> shift;
			int dimension = centerPosition.dimension;

			gridY = GetProjectedGridY(player);

			bool gridMoved = gridCellX != LastGridCellX || gridCellZ != LastGridCellZ || dimension != LastGridDimension;
			bool gridHeightChanged = !double.IsFinite(LastGridY) || Math.Abs(gridY - LastGridY) > 0.01;

			bool needGridRebuild = Snapshot.BuildVersion != LastGridGraphBuildVersion || gridMoved || gridHeightChanged;

			if (needGridRebuild)
			{
				anyRebuild = true;
				LastGridGraphBuildVersion = Snapshot.BuildVersion;
				LastGridCellX = gridCellX;
				LastGridCellZ = gridCellZ;
				LastGridDimension = dimension;
				LastGridY = gridY;

				ClearBuckets(GridBucketStart, 2);
				AddGridOverlayLines(centerPosition, gridY);
			}
		}

		if (ShowClearance)
		{
			bool needClearanceRebuild = Snapshot.BuildVersion != LastClearanceBuildVersion;
			if (needClearanceRebuild)
			{
				anyRebuild = true;
				LastClearanceBuildVersion = Snapshot.BuildVersion;

				ClearBuckets(ClearanceBucketStart, 2);
				RebuildClearanceBuckets(Snapshot);
			}
		}

		if (ShowAutomationDebug) { RebuildDynamicAutomationBuckets(); }

		if (ShowSGSizeTest) { RebuildDynamicSGSizeTestBucket(player); }

		if (AutomationStaticDirty)
		{
			AutomationStaticDirty = false;
			anyRebuild = true;
		}

		if (anyRebuild || ShowAutomationDebug)
		{
			for (int bucketIndex = 0; bucketIndex < BucketCount; bucketIndex++) { EnsureMeshCapacity(bucketIndex, BucketLines[bucketIndex].Count + DynamicBucketLines[bucketIndex].Count); }
		}

		// Write vertices (camera-relative for precision) for each bucket mesh.
		Vec3d cameraPosition = player.CameraPos;

		for (int bucketIndex = 0; bucketIndex < BucketCount; bucketIndex++)
		{
			int lineCount = BucketLines[bucketIndex].Count + DynamicBucketLines[bucketIndex].Count;
			if (lineCount == 0 || BucketMeshReferences[bucketIndex] == null || BucketMeshData[bucketIndex] == null) continue;

			var meshData = BucketMeshData[bucketIndex]!;
			int usedVertices = 0;

			WriteLinesToMesh(BucketLines[bucketIndex], meshData, cameraPosition, ref usedVertices);
			WriteLinesToMesh(DynamicBucketLines[bucketIndex], meshData, cameraPosition, ref usedVertices);

			meshData.VerticesCount = usedVertices;
			meshData.IndicesCount = usedVertices;

			ClientAPI.Render.UpdateMesh(BucketMeshReferences[bucketIndex], meshData);
		}

		// Render buckets, one pass per color (engine Wireframe shader uses a uniform colorIn).
		ClientAPI.Render.GlDisableCullFace();
		ClientAPI.Render.GlToggleBlend(true);

		ModelViewMatrix.Identity().Set(ClientAPI.Render.CameraMatrixOrigin);

		var shader = ClientAPI.Render.GetEngineShader(EnumShaderProgram.Wireframe);
		shader.Use();
		shader.Uniform("origin", new Vec3f(0, 0, 0));
		shader.UniformMatrix("projectionMatrix", ClientAPI.Render.CurrentProjectionMatrix);
		shader.UniformMatrix("modelViewMatrix", ModelViewMatrix.Values);

		ClientAPI.Render.LineWidth = 2f;

		for (int bucketIndex = 0; bucketIndex < BucketCount; bucketIndex++)
		{
			if (BucketMeshReferences[bucketIndex] == null) continue;
			if (BucketLines[bucketIndex].Count == 0 && DynamicBucketLines[bucketIndex].Count == 0) continue;

			shader.Uniform("colorIn", BucketColors[bucketIndex]);
			ClientAPI.Render.RenderMesh(BucketMeshReferences[bucketIndex]);
		}

		shader.Stop();
		ClientAPI.Render.GlToggleBlend(false);
	}

	private void SendRequest(BlockPos centerPosition, bool detailed, int lastVersion, bool includeDebugOccupancy)
	{
		ClientRailGraphSystem?.RequestGraph(centerPosition, detailed, lastVersion, includeDebugOccupancy);
	}

	private void OnGraphUpdated(RailGraphLive railGraph, bool detailed)
	{
		if (ClientAPI == null || !HasAnyDebugOverlayEnabled()) return;

		EntityPlayer? player = ClientAPI.World.Player?.Entity;
		BlockPos centerPosition = player?.Pos?.AsBlockPos ?? new BlockPos(0, 0, 0);

		Snapshot = railGraph.ExportSnapshot(centerPosition, includePolylines: detailed);

		if (ShowZones && ClientRailGraphSystem.LastResponseIncludedDebugOccupancy)
		{
			AuthoritativeOccupancyByEdge.Clear();
			RailGraphDebugEdgeOccupancy[] occupancyRecords = ClientRailGraphSystem.LastDebugEdgeOccupancy;
			for (int recordIndex = 0; recordIndex < occupancyRecords.Length; recordIndex++)
			{
				RailGraphDebugEdgeOccupancy record = occupancyRecords[recordIndex];
				if (record.EdgeHash != 0 && record.ZoneID != 0) { AuthoritativeOccupancyByEdge[record.EdgeHash] = record; }
			}
			AuthoritativeOccupancyGraphVersion = ClientRailGraphSystem.LastDebugOccupancyGraphVersion;
			AuthoritativeOccupancySerial = ClientRailGraphSystem.LastDebugOccupancySerial;
		}
		else if (AuthoritativeOccupancyGraphVersion != Snapshot.BuildVersion)
		{
			AuthoritativeOccupancyByEdge.Clear();
			AuthoritativeOccupancyGraphVersion = -1;
			AuthoritativeOccupancySerial = 0;
		}

		if (AutomationSignalGraphVersion != -1 && AutomationSignalGraphVersion != Snapshot.BuildVersion)
		{
			AutomationSignalStateByEdge.Clear();
			AutomationSignalGraphVersion = -1;
			AutomationSignalOccupancySerial = 0;
			AutomationOwnerPositions.Clear();
		}

		// Track snapshot detail level and force overlay rebuild next frame
		SnapshotHasPolylines = detailed;
		LastGraphBuildVersion = -1;
		LastZonesBuildVersion = -1;
		LastGridGraphBuildVersion = -1;
		LastClearanceBuildVersion = -1;
	}

	private double GetProjectedGridY(EntityPlayer player)
	{
		if (ClientAPI == null) return player.Pos.Y + 0.05;

		Vec3d rayStart = player.CameraPos;
		Vec3d rayEnd = new Vec3d(rayStart.X, rayStart.Y - GridMaxDistanceDown, rayStart.Z);

		BlockSelection? blockSelection = null;
		EntitySelection? entitySelection = null;
		ClientAPI.World.RayTraceForSelection(rayStart, rayEnd, ref blockSelection, ref entitySelection);

		double y = (blockSelection != null) ? (blockSelection.Position.Y + blockSelection.HitPosition.Y) : rayEnd.Y;
		return y + 0.05;
	}

	private void AddGridOverlayLines(BlockPos centerPosition, double gridY)
	{
		// Draw a window of hashgrid cells around the player (XZ only).
		int dimension = centerPosition.dimension;
		int shift = ClientRailGraphSystem.Graph.EdgeGridCellShift;
		int cellSize = ClientRailGraphSystem.Graph.EdgeGridCellSize;

		int playerCellX = centerPosition.X >> shift;
		int playerCellZ = centerPosition.Z >> shift;

		const int radiusCells = 7; // 15x15 window

		int minCellX = playerCellX - radiusCells;
		int maxCellX = playerCellX + radiusCells;
		int minCellZ = playerCellZ - radiusCells;
		int maxCellZ = playerCellZ + radiusCells;

		double y0 = gridY;
		double y1 = gridY + 64; // Draw the height of our fake box 64 blocks up

		double xMin = (double)(minCellX << shift);
		double xMax = (double)((maxCellX + 1) << shift);
		double zMin = (double)(minCellZ << shift);
		double zMax = (double)((maxCellZ + 1) << shift);

		// Horizontal grid lines (bottom + top)
		for (int cellZ = minCellZ; cellZ <= maxCellZ + 1; cellZ++)
		{
			double z = (double)(cellZ << shift);
			BucketLines[GridBucketWhite].Add(new Line(new Vec3d(xMin, y0, z), new Vec3d(xMax, y0, z)));
			BucketLines[GridBucketWhite].Add(new Line(new Vec3d(xMin, y1, z), new Vec3d(xMax, y1, z)));
		}

		for (int cellX = minCellX; cellX <= maxCellX + 1; cellX++)
		{
			double x = (double)(cellX << shift);
			BucketLines[GridBucketWhite].Add(new Line(new Vec3d(x, y0, zMin), new Vec3d(x, y0, zMax)));
			BucketLines[GridBucketWhite].Add(new Line(new Vec3d(x, y1, zMin), new Vec3d(x, y1, zMax)));
		}

		// Vertical grid lines (connect the two planes to make a "voxel" lattice)
		for (int cellZ = minCellZ; cellZ <= maxCellZ + 1; cellZ++)
		{
			double z = (double)(cellZ << shift);
			for (int cellX = minCellX; cellX <= maxCellX + 1; cellX++)
			{
				double x = (double)(cellX << shift);
				BucketLines[GridBucketWhite].Add(new Line(new Vec3d(x, y0, z), new Vec3d(x, y1, z)));
			}
		}

		// Mark occupied cells with an X on the bottom plane (if we have enough graph data locally)
		for (int cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
		{
			for (int cellX = minCellX; cellX <= maxCellX; cellX++)
			{
				if (ClientRailGraphSystem.Graph.TryGetEdgeCell(dimension, cellX, cellZ, out var edgeIDs) && edgeIDs.Count > 0)
				{
					double x0 = (double)(cellX << shift);
					double z0 = (double)(cellZ << shift);
					double x1 = x0 + cellSize;
					double z1 = z0 + cellSize;

					BucketLines[GridBucketRed].Add(new Line(new Vec3d(x0, y0, z0), new Vec3d(x1, y0, z1))); // 1 is red, makes it clearer
					BucketLines[GridBucketRed].Add(new Line(new Vec3d(x1, y0, z0), new Vec3d(x0, y0, z1)));
				}
			}
		}

		// Highlight the current cell with a small cross at its center (bottom plane)
		double currentCellCenterX = (double)(playerCellX << shift) + cellSize * 0.5;
		double currentCellCenterZ = (double)(playerCellZ << shift) + cellSize * 0.5;
		const double crossHalfSize = 2.0;

		BucketLines[GridBucketWhite].Add(new Line(new Vec3d(currentCellCenterX - crossHalfSize, y0, currentCellCenterZ), new Vec3d(currentCellCenterX + crossHalfSize, y0, currentCellCenterZ)));
		BucketLines[GridBucketWhite].Add(new Line(new Vec3d(currentCellCenterX, y0, currentCellCenterZ - crossHalfSize), new Vec3d(currentCellCenterX, y0, currentCellCenterZ + crossHalfSize)));
	}

	private void RebuildClearanceBuckets(RailGraphSnapshot snapshot)
	{
		if (snapshot?.Segments == null || ClientRailGraphSystem == null) return;

		RailGraphLive railGraph = ClientRailGraphSystem.Graph;
		HashSet<ulong> drawnEdges = new();

		for (int segmentIndex = 0; segmentIndex < snapshot.Segments.Count; segmentIndex++)
		{
			RailSegment segment = snapshot.Segments[segmentIndex];

			for (int edgeIndex = 0; edgeIndex < segment.EdgeHashes.Count; edgeIndex++)
			{
				ulong edgeHash = segment.EdgeHashes[edgeIndex];
				if (!drawnEdges.Add(edgeHash)) continue;

				int bucket = railGraph.IsEdgeClearanceBlocked(edgeHash) ? ClearanceRedBucket : ClearanceGreenBucket;
				if (railGraph.TryGetPolyline16(edgeHash, out int[] polylineCoordinates16)) { AddEdgePolylineToBucket(bucket, polylineCoordinates16); }
				else if (railGraph.TryGetEdgeWorldEndpoints(edgeHash, out Vec3d a, out Vec3d b)) { BucketLines[bucket].Add(new Line(a, b)); }
			}
		}
	}

	private void AddEdgePolylineToBucket(int bucketIndex, int[] polylineCoordinates16)
	{
		if (polylineCoordinates16 == null || polylineCoordinates16.Length < 6) return;

		for (int coordinateOffset = 0; coordinateOffset + 5 < polylineCoordinates16.Length; coordinateOffset += 3)
		{
			Vec3d a = new(polylineCoordinates16[coordinateOffset] / 16.0, polylineCoordinates16[coordinateOffset + 1] / 16.0, polylineCoordinates16[coordinateOffset + 2] / 16.0);
			Vec3d b = new(polylineCoordinates16[coordinateOffset + 3] / 16.0, polylineCoordinates16[coordinateOffset + 4] / 16.0, polylineCoordinates16[coordinateOffset + 5] / 16.0);
			BucketLines[bucketIndex].Add(new Line(a, b));
		}
	}

	private void ClearBuckets(int startBucket, int bucketCount)
	{
		int endBucket = startBucket + bucketCount;
		if (startBucket < 0) startBucket = 0;
		if (endBucket > BucketCount) endBucket = BucketCount;

		for (int bucketIndex = startBucket; bucketIndex < endBucket; bucketIndex++) BucketLines[bucketIndex].Clear();
	}

	private void ClearDynamicBuckets(int startBucket, int bucketCount)
	{
		int endBucket = startBucket + bucketCount;
		if (startBucket < 0) startBucket = 0;
		if (endBucket > BucketCount) endBucket = BucketCount;

		for (int bucketIndex = startBucket; bucketIndex < endBucket; bucketIndex++) DynamicBucketLines[bucketIndex].Clear();
	}

	private void RebuildGraphBuckets(RailGraphSnapshot snapshot, Dictionary<int, int> segmentColors)
	{
		if (snapshot.Segments.Count == 0) return;

		if (snapshot.Nodes.Count > 0)
		{
			const double nodeMarkerHalfSize = 0.20;
			for (int nodeIndex = 0; nodeIndex < snapshot.Nodes.Count; nodeIndex++)
			{
				Vec3d nodePosition = snapshot.Nodes[nodeIndex].WorldPosition;

				BucketLines[GraphNodeBucket].Add(new Line(new Vec3d(nodePosition.X - nodeMarkerHalfSize, nodePosition.Y, nodePosition.Z), new Vec3d(nodePosition.X + nodeMarkerHalfSize, nodePosition.Y, nodePosition.Z)));
				BucketLines[GraphNodeBucket].Add(new Line(new Vec3d(nodePosition.X, nodePosition.Y - nodeMarkerHalfSize, nodePosition.Z), new Vec3d(nodePosition.X, nodePosition.Y + nodeMarkerHalfSize, nodePosition.Z)));
				BucketLines[GraphNodeBucket].Add(new Line(new Vec3d(nodePosition.X, nodePosition.Y, nodePosition.Z - nodeMarkerHalfSize), new Vec3d(nodePosition.X, nodePosition.Y, nodePosition.Z + nodeMarkerHalfSize)));
			}
		}

		for (int segmentIndex = 0; segmentIndex < snapshot.Segments.Count; segmentIndex++)
		{
			RailSegment segment = snapshot.Segments[segmentIndex];
			if (segment.Polyline == null || segment.Polyline.Count < 2) continue;

			int colorIndex = segmentColors.TryGetValue(segment.ID, out int assignedColorIndex) ? assignedColorIndex : 0;
			int bucketIndex = GraphRailBucketStart + colorIndex;

			for (int pointIndex = 0; pointIndex < segment.Polyline.Count - 1; pointIndex++)
			{
				BucketLines[bucketIndex].Add(new Line(segment.Polyline[pointIndex], segment.Polyline[pointIndex + 1]));
			}
		}
	}

	private bool TryGetAuthoritativeSegmentZone(RailGraphSnapshot snapshot, RailSegment segment, out ulong zoneID)
	{
		zoneID = 0;
		if (AuthoritativeOccupancyGraphVersion != snapshot.BuildVersion || segment.EdgeHashes.Count == 0) return false;

		for (int edgeIndex = 0; edgeIndex < segment.EdgeHashes.Count; edgeIndex++)
		{
			ulong edgeHash = segment.EdgeHashes[edgeIndex];
			if ( edgeHash == 0 || !AuthoritativeOccupancyByEdge.TryGetValue(edgeHash, out RailGraphDebugEdgeOccupancy occupancyState) || occupancyState.ZoneID == 0) { zoneID = 0; return false; }

			if (zoneID == 0) zoneID = occupancyState.ZoneID;
			else if (zoneID != occupancyState.ZoneID) { zoneID = 0; return false; }
		}

		return zoneID != 0;
	}

	private void RebuildZoneBuckets(RailGraphSnapshot snapshot)
	{
		if (snapshot.Segments.Count == 0) return;

		if (!ShowAutomationDebug)
		{
			// Occupancy-zone view: color each (max) segment by its occupancy zone id (separate bucket range).
			for (int segmentIndex = 0; segmentIndex < snapshot.Segments.Count; segmentIndex++)
			{
				RailSegment segment = snapshot.Segments[segmentIndex];
				if (segment.Polyline == null || segment.Polyline.Count < 2) continue;

				bool zoneKnown = TryGetAuthoritativeSegmentZone(snapshot, segment, out ulong zoneID);
				int colorIndex = 0;
				if (zoneKnown) ZoneColorIndex.TryGetValue(zoneID, out colorIndex);
				int bucketIndex = zoneKnown ? ZoneRailBucketStart + colorIndex : SignalUnknownBucket;

				for (int pointIndex = 0; pointIndex < segment.Polyline.Count - 1; pointIndex++)
				{
					Vec3d sourcePoint = segment.Polyline[pointIndex];
					Vec3d destinationPoint = segment.Polyline[pointIndex + 1];

					// Occupancy-zone lines are rendered at mid-block height (Y = floor(Y) + 0.5) so they do not overlap with the graph overlay's line height.
					Vec3d sourcePosition = new Vec3d(sourcePoint.X, Math.Floor(sourcePoint.Y) + 0.5, sourcePoint.Z);
					Vec3d destinationPosition = new Vec3d(destinationPoint.X, Math.Floor(destinationPoint.Y) + 0.5, destinationPoint.Z);

					BucketLines[bucketIndex].Add(new Line(sourcePosition, destinationPosition));
				}
			}
		}

		// Overlay signal markers (wire box + face X) so you can see where zone boundaries are.
		AddSignalOverlayLines(snapshot);
	}


	/// Greedy 5-coloring so segments meeting at a node tend to be visually distinct.
	private void ComputeSegmentColors(RailGraphSnapshot snapshot, Dictionary<int, int> outputColors, int seed)
	{
		outputColors.Clear();
		var random = new Random(seed);
		if (snapshot.Segments.Count == 0 || snapshot.Nodes.Count == 0) return;

		var nodesByID = new Dictionary<int, RailNode>(snapshot.Nodes.Count);
		for (int nodeIndex = 0; nodeIndex < snapshot.Nodes.Count; nodeIndex++) { nodesByID[snapshot.Nodes[nodeIndex].ID] = snapshot.Nodes[nodeIndex]; }

		int segmentCount = snapshot.Segments.Count;
		int[] order = new int[segmentCount];
		for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++) order[segmentIndex] = segmentIndex;

		for (int attempt = 0; attempt < 50; attempt++)
		{
			outputColors.Clear();

			var seedNode = snapshot.Nodes[random.Next(snapshot.Nodes.Count)];
			if (seedNode.SegmentIDs.Count > 0)
			{
				int seedSegmentID = seedNode.SegmentIDs[random.Next(seedNode.SegmentIDs.Count)];
				outputColors[seedSegmentID] = random.Next(5);
			}

			for (int shuffleIndex = segmentCount - 1; shuffleIndex > 0; shuffleIndex--)
			{
				int randomIndex = random.Next(shuffleIndex + 1);
				(order[shuffleIndex], order[randomIndex]) = (order[randomIndex], order[shuffleIndex]);
			}

			if (TryGreedyColor(snapshot, nodesByID, outputColors, order, random)) { return; }
		}

		for (int segmentIndex = 0; segmentIndex < snapshot.Segments.Count; segmentIndex++) { outputColors[snapshot.Segments[segmentIndex].ID] = 0; }
	}

	private bool TryGreedyColor(RailGraphSnapshot snapshot, Dictionary<int, RailNode> nodesByID, Dictionary<int, int> colors, int[] order, Random random)
	{
		bool[] usedColors = new bool[5];
		int[] availableColors = new int[5];

		for (int orderIndex = 0; orderIndex < order.Length; orderIndex++)
		{
			var segment = snapshot.Segments[order[orderIndex]];
			if (colors.ContainsKey(segment.ID)) continue;

			Array.Clear(usedColors, 0, usedColors.Length);

			MarkUsed(segment.NodeA);
			MarkUsed(segment.NodeB);

			int allowedCount = 0;
			for (int colorIndex = 0; colorIndex < usedColors.Length; colorIndex++) { if (!usedColors[colorIndex]) availableColors[allowedCount++] = colorIndex; }

			if (allowedCount == 0) return false;
			colors[segment.ID] = availableColors[random.Next(allowedCount)];
		}

		// Validate (no two segments touching a node share color)
		for (int nodeIndex = 0; nodeIndex < snapshot.Nodes.Count; nodeIndex++)
		{
			var node = snapshot.Nodes[nodeIndex];
			Array.Clear(usedColors, 0, usedColors.Length);

			for (int segmentIndex = 0; segmentIndex < node.SegmentIDs.Count; segmentIndex++)
			{
				int segmentID = node.SegmentIDs[segmentIndex];
				if (!colors.TryGetValue(segmentID, out int colorIndex)) continue;
				if (usedColors[colorIndex]) return false;
				usedColors[colorIndex] = true;
			}
		}

		return true;

		void MarkUsed(int nodeID)
		{
			if (!nodesByID.TryGetValue(nodeID, out var node)) return;
			for (int segmentIndex = 0; segmentIndex < node.SegmentIDs.Count; segmentIndex++)
			{
				int segmentID = node.SegmentIDs[segmentIndex];
				if (!colors.TryGetValue(segmentID, out int colorIndex)) continue;
				usedColors[colorIndex] = true;
			}
		}
	}


	/// Greedy coloring for occupancy zones (derived from signals). We only constrain zone colors at signal nodes.
	/// This keeps zone boundaries visually distinct without requiring a large palette.
	private void ComputeZoneColors(RailGraphSnapshot snapshot, Dictionary<ulong, int> outputColors, int seed)
	{
		outputColors.Clear();
		if (snapshot.Segments.Count == 0) return;

		// Build segmentId --> zoneId and collect unique zones.
		var zoneBySegmentID = new Dictionary<int, ulong>(snapshot.Segments.Count);
		var zones = new HashSet<ulong>();

		for (int segmentIndex = 0; segmentIndex < snapshot.Segments.Count; segmentIndex++)
		{
			RailSegment segment = snapshot.Segments[segmentIndex];
			if (!TryGetAuthoritativeSegmentZone(snapshot, segment, out ulong zoneID)) continue;

			zoneBySegmentID[segment.ID] = zoneID;
			zones.Add(zoneID);
		}

		if (zones.Count == 0) return;

		// Build zone neighbor lists from SIGNAL nodes only.
		var neighbors = new Dictionary<ulong, List<ulong>>(zones.Count);

		for (int nodeIndex = 0; nodeIndex < snapshot.Nodes.Count; nodeIndex++)
		{
			RailNode node = snapshot.Nodes[nodeIndex];
			if (!node.IsSignal) continue;

			Span<ulong> nodeZones = stackalloc ulong[8];
			int zoneCount = 0;

			for (int segmentIndex = 0; segmentIndex < node.SegmentIDs.Count && zoneCount < nodeZones.Length; segmentIndex++)
			{
				int segmentID = node.SegmentIDs[segmentIndex];
				if (!zoneBySegmentID.TryGetValue(segmentID, out ulong zoneID) || zoneID == 0) continue;

				bool duplicate = false;
				for (int zoneIndex = 0; zoneIndex < zoneCount; zoneIndex++) { if (nodeZones[zoneIndex] == zoneID) { duplicate = true; break; } }
				if (duplicate) continue;

				nodeZones[zoneCount++] = zoneID;
			}

			if (zoneCount < 2) continue;

			for (int sourceZoneIndex = 0; sourceZoneIndex < zoneCount; sourceZoneIndex++)
			{
				for (int destinationZoneIndex = 0; destinationZoneIndex < zoneCount; destinationZoneIndex++)
				{
					if (sourceZoneIndex == destinationZoneIndex) continue;

					ulong sourceZoneID = nodeZones[sourceZoneIndex];
					ulong destinationZoneID = nodeZones[destinationZoneIndex];

					if (!neighbors.TryGetValue(sourceZoneID, out var neighborList))
					{
						neighborList = new List<ulong>(4);
						neighbors[sourceZoneID] = neighborList;
					}

					neighborList.Add(destinationZoneID);
				}
			}
		}

		ulong[] order = new ulong[zones.Count];
		int orderIndex = 0;
		foreach (ulong zoneID in zones) order[orderIndex++] = zoneID;

		var random = new Random(seed);

		// A few attempts with different random orders; usually succeeds on the first attempt.
		for (int attempt = 0; attempt < 20; attempt++)
		{
			outputColors.Clear();

			for (int shuffleIndex = order.Length - 1; shuffleIndex > 0; shuffleIndex--)
			{
				int randomIndex = random.Next(shuffleIndex + 1);
				(order[shuffleIndex], order[randomIndex]) = (order[randomIndex], order[shuffleIndex]);
			}

			if (!TryGreedyColorZones(outputColors, neighbors, order, random)) continue;
			if (ValidateZoneColors(snapshot, zoneBySegmentID, outputColors)) return;
		}

		// Fallback, deterministic cycling | May clash on pathological graphs. Acceptable for debug view.
		int colorIndex = 0;
		foreach (ulong zoneID in zones)
		{
			outputColors[zoneID] = colorIndex;
			colorIndex = (colorIndex + 1) % RailColorCount;
		}
	}

	private bool TryGreedyColorZones(Dictionary<ulong, int> colors, Dictionary<ulong, List<ulong>> neighbors, ulong[] order, Random random)
	{
		bool[] usedColors = new bool[RailColorCount];
		int[] availableColors = new int[RailColorCount];

		for (int orderIndex = 0; orderIndex < order.Length; orderIndex++)
		{
			ulong zoneID = order[orderIndex];
			if (colors.ContainsKey(zoneID)) continue;

			Array.Clear(usedColors, 0, usedColors.Length);

			if (neighbors.TryGetValue(zoneID, out var adjacentZones))
			{
				for (int adjacentZoneIndex = 0; adjacentZoneIndex < adjacentZones.Count; adjacentZoneIndex++)
				{
					if (!colors.TryGetValue(adjacentZones[adjacentZoneIndex], out int colorIndex)) continue;
					usedColors[colorIndex] = true;
				}
			}

			int allowedCount = 0;
			for (int colorIndex = 0; colorIndex < usedColors.Length; colorIndex++) { if (!usedColors[colorIndex]) availableColors[allowedCount++] = colorIndex; }

			if (allowedCount == 0) return false;
			colors[zoneID] = availableColors[random.Next(allowedCount)];
		}

		return true;
	}

	private bool ValidateZoneColors(RailGraphSnapshot snapshot, Dictionary<int, ulong> zoneBySegmentID, Dictionary<ulong, int> colors)
	{
		bool[] usedColors = new bool[RailColorCount];

		for (int nodeIndex = 0; nodeIndex < snapshot.Nodes.Count; nodeIndex++)
		{
			RailNode node = snapshot.Nodes[nodeIndex];
			if (!node.IsSignal) continue;

			Array.Clear(usedColors, 0, usedColors.Length);

			Span<ulong> nodeZones = stackalloc ulong[8];
			int zoneCount = 0;

			for (int segmentIndex = 0; segmentIndex < node.SegmentIDs.Count && zoneCount < nodeZones.Length; segmentIndex++)
			{
				int segmentID = node.SegmentIDs[segmentIndex];
				if (!zoneBySegmentID.TryGetValue(segmentID, out ulong zoneID) || zoneID == 0) continue;

				bool duplicate = false;
				for (int zoneIndex = 0; zoneIndex < zoneCount; zoneIndex++) { if (nodeZones[zoneIndex] == zoneID) { duplicate = true; break; } }
				if (duplicate) continue;

				nodeZones[zoneCount++] = zoneID;
			}

			for (int zoneIndex = 0; zoneIndex < zoneCount; zoneIndex++)
			{
				if (!colors.TryGetValue(nodeZones[zoneIndex], out int colorIndex)) continue;
				if ((uint)colorIndex >= (uint)RailColorCount) continue;
				if (usedColors[colorIndex]) return false;
				usedColors[colorIndex] = true;
			}
		}

		return true;
	}

	private bool TryGetAuthoritativeSignalEdgeState(
		int graphBuildVersion,
		ulong edgeHash,
		out ulong zoneID,
		out bool occupied)
	{
		zoneID = 0;
		occupied = false;
		if (edgeHash == 0) return false;

		if
		(
			ShowAutomationDebug && AutomationSignalGraphVersion == graphBuildVersion &&
			AutomationSignalStateByEdge.TryGetValue(edgeHash, out AutomationDebugSignalEdgeState signalState) && signalState.ZoneID != 0
		)
		{
			zoneID = signalState.ZoneID;
			occupied = signalState.Occupied;
			return true;
		}

		if
		(
			AuthoritativeOccupancyGraphVersion == graphBuildVersion &&
			AuthoritativeOccupancyByEdge.TryGetValue(edgeHash, out RailGraphDebugEdgeOccupancy edgeState) && edgeState.ZoneID != 0
		)
		{
			zoneID = edgeState.ZoneID;
			occupied = edgeState.Occupied;
			return true;
		}

		return false;
	}

	private void AddSignalOverlayLines(RailGraphSnapshot snapshot)
	{
		// Build lookup for fallback node adjacency if atomic incident edges are unavailable.
		var segmentsByID = new Dictionary<int, RailSegment>(snapshot.Segments.Count);
		for (int segmentIndex = 0; segmentIndex < snapshot.Segments.Count; segmentIndex++)
		{
			RailSegment segment = snapshot.Segments[segmentIndex];
			segmentsByID[segment.ID] = segment;
		}

		const double boxEpsilon = 0.02;
		const double faceEpsilon = 0.01;

		SignalHeadsUpDisplayInformationByPosition.Clear();

		for (int nodeIndex = 0; nodeIndex < snapshot.Nodes.Count; nodeIndex++)
		{
			RailNode node = snapshot.Nodes[nodeIndex];
			if (!node.IsSignal) continue;

			Span<SignalDebugSide> sides = stackalloc SignalDebugSide[4];
			int sideCount = BuildSignalDebugSides(snapshot, segmentsByID, node, sides);

			int knownSides = 0;
			int occupiedSides = 0;
			for (int sideIndex = 0; sideIndex < sideCount; sideIndex++)
			{
				if (!sides[sideIndex].OccupancyKnown) continue;
				knownSides++;
				if (sides[sideIndex].Occupied) occupiedSides++;
			}

			int boxBucket = SignalUnknownBucket;

			if (sideCount > 0)
			{
				if ((node.SignalKind == (byte)SignalKind.OneWay || node.SignalKind == (byte)SignalKind.Chain) && sideCount == 2)
				{
					int entrySide = sides[0].EdgeHash == node.OneWayAllowedFromEdgeHash ? 0 : sides[1].EdgeHash == node.OneWayAllowedFromEdgeHash ? 1 : -1;
					if (entrySide >= 0)
					{
						SignalDebugSide destinationSide = sides[1 - entrySide];
						if (destinationSide.OccupancyKnown) { boxBucket = destinationSide.Occupied ? SignalRedBucket : SignalGreenBucket; }
					}
				}
				else if (knownSides == sideCount)
				{
					if (occupiedSides == 0) boxBucket = SignalGreenBucket;
					else if (occupiedSides >= sideCount) boxBucket = SignalRedBucket;
					else boxBucket = SignalYellowBucket;
				}
			}

			AddWireBox(node.Position, boxEpsilon, boxBucket);

			for (int sideIndex = 0; sideIndex < sideCount && sideIndex < 2; sideIndex++)
			{
				SignalDebugSide entrySide = sides[sideIndex];
				if ((node.SignalKind == (byte)SignalKind.OneWay || node.SignalKind == (byte)SignalKind.Chain) && entrySide.EdgeHash != node.OneWayAllowedFromEdgeHash) continue;

				int opposite = sideCount == 2 ? 1 - sideIndex : -1;
				if (opposite < 0) continue;

				SignalDebugSide destinationSide = sides[opposite];
				int faceBucket = !destinationSide.OccupancyKnown ? SignalUnknownBucket : destinationSide.Occupied ? SignalRedBucket : SignalGreenBucket;
				AddFaceX(node.Position, entrySide.FaceAxis, entrySide.FaceSign, faceEpsilon, faceBucket);
			}

			if (ShowAutomationDebug) { AddSignalAutomationOccupantDebug(node, sides, sideCount); }
		}
	}

	private int BuildSignalDebugSides(RailGraphSnapshot snapshot, Dictionary<int, RailSegment> segmentsByID, RailNode node, Span<SignalDebugSide> sides)
	{
		int sideCount = 0;

		var endpointKey = new RailGraphLive.EndpointKey
		(
			(int)Math.Round(node.WorldPosition.X * 16.0),
			(int)Math.Round(node.WorldPosition.Y * 16.0),
			(int)Math.Round(node.WorldPosition.Z * 16.0),
			node.Position.dimension,
			node.Gauge
		);

		if (ClientRailGraphSystem.Graph.TryGetIncidentEdges(endpointKey, out List<ulong> incidentEdgeHashes))
		{
			for (int edgeIndex = 0; edgeIndex < incidentEdgeHashes.Count && sideCount < sides.Length; edgeIndex++)
			{
				ulong edgeHash = incidentEdgeHashes[edgeIndex];
				if (!ClientRailGraphSystem.Graph.TryGetEdgeWorldEndpoints(edgeHash, out Vec3d sourceEndpoint, out Vec3d destinationEndpoint)) continue;

				Vec3d otherEndpoint = NearestOtherEndpoint(node.WorldPosition, sourceEndpoint, destinationEndpoint);
				Vec3d direction = new Vec3d(otherEndpoint.X - node.WorldPosition.X, otherEndpoint.Y - node.WorldPosition.Y, otherEndpoint.Z - node.WorldPosition.Z);
				if (!TryResolveFaceFromDirection(direction, out int axis, out int sign)) continue;

				bool occupancyKnown = TryGetAuthoritativeSignalEdgeState(snapshot.BuildVersion, edgeHash, out ulong zoneID, out bool occupied);

				AddSignalSideIfNew(sides, ref sideCount, edgeHash, zoneID, occupancyKnown, occupied, axis, sign);
			}
		}

		if (sideCount > 0) return sideCount;

		// Fallback, use maximal-segment endpoints. This is less exact on curves but avoids silent missing markers.
		for (int segmentIndex = 0; segmentIndex < node.SegmentIDs.Count && sideCount < sides.Length; segmentIndex++)
		{
			if (!segmentsByID.TryGetValue(node.SegmentIDs[segmentIndex], out RailSegment segment)) continue;

			int otherNodeID = segment.NodeA == node.ID ? segment.NodeB : segment.NodeA;
			if (otherNodeID <= 0 || otherNodeID > snapshot.Nodes.Count) continue;

			Vec3d otherPosition = snapshot.Nodes[otherNodeID - 1].WorldPosition;
			Vec3d direction = new Vec3d(otherPosition.X - node.WorldPosition.X, otherPosition.Y - node.WorldPosition.Y, otherPosition.Z - node.WorldPosition.Z);
			if (!TryResolveFaceFromDirection(direction, out int axis, out int sign)) continue;

			ulong edgeHash = 0;
			if (segment.EdgeHashes.Count > 0)
			{
				edgeHash = segment.NodeA == node.ID ? segment.EdgeHashes[0] : segment.EdgeHashes[segment.EdgeHashes.Count - 1];
			}

			bool occupancyKnown = TryGetAuthoritativeSignalEdgeState(snapshot.BuildVersion, edgeHash, out ulong zoneID, out bool occupied);
			AddSignalSideIfNew(sides, ref sideCount, edgeHash, zoneID, occupancyKnown, occupied, axis, sign);
		}

		return sideCount;
	}

	private static Vec3d NearestOtherEndpoint(Vec3d nodePosition, Vec3d endA, Vec3d endB)
	{
		double deltaFromEndpointAX = endA.X - nodePosition.X; double deltaFromEndpointAY = endA.Y - nodePosition.Y; double deltaFromEndpointAZ = endA.Z - nodePosition.Z;
		double deltaFromEndpointBX = endB.X - nodePosition.X; double deltaFromEndpointBY = endB.Y - nodePosition.Y; double deltaFromEndpointBZ = endB.Z - nodePosition.Z;
		double squaredDistanceA = deltaFromEndpointAX * deltaFromEndpointAX + deltaFromEndpointAY * deltaFromEndpointAY + deltaFromEndpointAZ * deltaFromEndpointAZ;
		double squaredDistanceB = deltaFromEndpointBX * deltaFromEndpointBX + deltaFromEndpointBY * deltaFromEndpointBY + deltaFromEndpointBZ * deltaFromEndpointBZ;
		return squaredDistanceA < squaredDistanceB ? endB : endA;
	}

	private static void AddSignalSideIfNew
	(
		Span<SignalDebugSide> sides, ref int sideCount, ulong edgeHash,
		ulong zoneID, bool occupancyKnown, bool occupied, int axis, int sign
	)
	{
		for (int sideIndex = 0; sideIndex < sideCount; sideIndex++)
		{
			if (sides[sideIndex].FaceAxis != axis || sides[sideIndex].FaceSign != sign) continue;

			SignalDebugSide existing = sides[sideIndex];
			if ((!existing.OccupancyKnown && occupancyKnown) || (existing.OccupancyKnown && occupancyKnown && !existing.Occupied && occupied))
			{
				sides[sideIndex] = new SignalDebugSide(edgeHash, zoneID, occupancyKnown, occupied, axis, sign);
			}
			return;
		}

		if (sideCount >= sides.Length) return;
		sides[sideCount++] = new SignalDebugSide(edgeHash, zoneID, occupancyKnown, occupied, axis, sign);
	}

	private void AppendOwnerIDWithBucket(StringBuilder textBuilder, long ownerID)
	{
		textBuilder.Append(ownerID);
		if (!AutomationBucketMaskByOwnerID.TryGetValue(ownerID, out int bucketMask) || bucketMask == 0) return;

		int colorCount = 0;
		for (int slot = 0; slot < AutomationBucketCount; slot++) { if ((bucketMask & (1 << slot)) != 0) colorCount++; }
		if (colorCount == 0) return;

		textBuilder.Append(" (");
		bool printed = false;
		for (int slot = 0; slot < AutomationBucketCount; slot++)
		{
			if ((bucketMask & (1 << slot)) == 0) continue;
			if (printed) textBuilder.Append(", ");
			textBuilder.Append(AutomationSlotNames[slot]);
			printed = true;
		}
		textBuilder.Append(")");
	}

	private static void AppendDebugSectionSeparator(StringBuilder textBuilder)
	{
		if (textBuilder.Length == 0) return;

		int trailingNewlines = 0;
		for (int characterIndex = textBuilder.Length - 1; characterIndex >= 0; characterIndex--)
		{
			char character = textBuilder[characterIndex];
			if (character == '\n') { trailingNewlines++; continue; }
			if (character == '\r') continue;
			break;
		}

		if (trailingNewlines == 0) textBuilder.Append('\n');
		if (trailingNewlines < 2) textBuilder.Append('\n');
	}

	private void AddSignalAutomationOccupantDebug(RailNode node, Span<SignalDebugSide> sides, int sideCount)
	{
		var signalKey = new SignalHeadsUpDisplayKey(node.Position.X, node.Position.Y, node.Position.Z, node.Position.dimension);
		StringBuilder? textBuilder = null;
		int shownEdges = 0;
		int occupantLines = 0;

		for (int sideIndex = 0; sideIndex < sideCount; sideIndex++)
		{
			SignalDebugSide side = sides[sideIndex];
			bool edgeHasOwnerLines = false;

			if (shownEdges > 0)
			{
				textBuilder ??= new StringBuilder(160);
				textBuilder.Append('\n');
			}

			textBuilder ??= new StringBuilder(160);
			textBuilder.Append("Edge ").Append((char)('A' + shownEdges)).Append(": ");

			if
			(
				side.EdgeHash != 0 && AutomationSignalGraphVersion == Snapshot.BuildVersion &&
				AutomationSignalStateByEdge.TryGetValue(side.EdgeHash, out AutomationDebugSignalEdgeState state)
			)
			{
				long[] owners = state.OwnerIDs ?? Array.Empty<long>();
				bool hasOwner = false;
				for (int ownerIndex = 0; ownerIndex < owners.Length; ownerIndex++) { if (owners[ownerIndex] != 0) { hasOwner = true; break; } }

				textBuilder.Append("Zone ").Append(state.ZoneID).Append(" (")
					.Append(state.Occupied ? "Occupied" : "Free")
					.Append(")");

				if (hasOwner)
				{
					textBuilder.Append('\n');
					for (int ownerIndex = 0; ownerIndex < owners.Length; ownerIndex++)
					{
						long ownerID = owners[ownerIndex];
						if (ownerID == 0) continue;

						textBuilder.Append("ID ");
						AppendOwnerIDWithBucket(textBuilder, ownerID);
						textBuilder.Append('\n');
						edgeHasOwnerLines = true;

						if (occupantLines < MaxSignalOccupantLinesPerSignal && AutomationOwnerPositions.TryGetValue(ownerID, out AutomationDebugOwnerPosition ownerPosition))
						{
							Vec3d lineStart = new(node.WorldPosition.X, node.WorldPosition.Y + 0.7, node.WorldPosition.Z);
							Vec3d lineEnd = new(ownerPosition.X, ownerPosition.Y + 1.5, ownerPosition.Z);
							BucketLines[SignalRedBucket].Add(new Line(lineStart, lineEnd));
							occupantLines++;
						}
					}
				}
				else if (state.Occupied)
				{
					textBuilder.Append('\n').Append("ID unavailable").Append('\n');
					edgeHasOwnerLines = true;
				}
			}
			else if (side.OccupancyKnown)
			{
				textBuilder.Append("Zone ").Append(side.ZoneID).Append(" (").Append(side.Occupied ? "Occupied" : "Free").Append(")");
				if (side.Occupied)
				{
					textBuilder.Append('\n').Append("ID unavailable").Append('\n');
					edgeHasOwnerLines = true;
				}
			}
			else { textBuilder.Append("Zone unknown (Unknown)"); }

			if (edgeHasOwnerLines && sideIndex + 1 < sideCount) { textBuilder.Append('\n'); }

			shownEdges++;
		}

		if (textBuilder != null && shownEdges > 0)
		{
			while (textBuilder.Length > 0 && (textBuilder[textBuilder.Length - 1] == '\n' || textBuilder[textBuilder.Length - 1] == '\r')) textBuilder.Length--;
			SignalHeadsUpDisplayInformationByPosition[signalKey] = textBuilder.ToString();
		}
	}

	private static bool TryResolveFaceFromDirection(Vec3d direction, out int axis, out int sign)
	{
		// Axis: 0 = X faces, 1 = Z faces | Sign: -1 = negative face, +1 = positive face.
		double absoluteX = Math.Abs(direction.X);
		double absoluteZ = Math.Abs(direction.Z);

		if (absoluteX < 1e-6 && absoluteZ < 1e-6) { axis = 0; sign = 0; return false; }

		if (absoluteX >= absoluteZ)
		{
			axis = 0;
			sign = direction.X >= 0 ? 1 : -1;
			return true;
		}

		axis = 1;
		sign = direction.Z >= 0 ? 1 : -1;
		return true;
	}

	private void AddWireBox(BlockPos position, double epsilon, int bucketIndex)
	{
		double x0 = position.X - epsilon;
		double y0 = position.Y - epsilon;
		double z0 = position.Z - epsilon;
		double x1 = position.X + 1 + epsilon;
		double y1 = position.Y + 1 + epsilon;
		double z1 = position.Z + 1 + epsilon;

		// bottom rectangle
		AddSignalLine(bucketIndex, x0, y0, z0, x1, y0, z0);
		AddSignalLine(bucketIndex, x1, y0, z0, x1, y0, z1);
		AddSignalLine(bucketIndex, x1, y0, z1, x0, y0, z1);
		AddSignalLine(bucketIndex, x0, y0, z1, x0, y0, z0);

		// top rectangle
		AddSignalLine(bucketIndex, x0, y1, z0, x1, y1, z0);
		AddSignalLine(bucketIndex, x1, y1, z0, x1, y1, z1);
		AddSignalLine(bucketIndex, x1, y1, z1, x0, y1, z1);
		AddSignalLine(bucketIndex, x0, y1, z1, x0, y1, z0);

		// verticals
		AddSignalLine(bucketIndex, x0, y0, z0, x0, y1, z0);
		AddSignalLine(bucketIndex, x1, y0, z0, x1, y1, z0);
		AddSignalLine(bucketIndex, x1, y0, z1, x1, y1, z1);
		AddSignalLine(bucketIndex, x0, y0, z1, x0, y1, z1);
	}

	private void AddFaceX(BlockPos position, int axis, int sign, double inset, int bucketIndex)
	{
		// Draw an X on the face that allows entry (axis 0 = X face, axis 1 = Z face).
		double x0 = position.X;
		double y0 = position.Y;
		double z0 = position.Z;

		double yA = y0 + inset;
		double yB = y0 + 1 - inset;

		const double outerEpsilon = 0.012; // nudge outward to avoid z-fighting

		if (axis == 0)
		{
			double xf = (sign > 0) ? (x0 + 1 + outerEpsilon) : (x0 - outerEpsilon);
			double zA = z0 + inset;
			double zB = z0 + 1 - inset;

			AddSignalLine(bucketIndex, xf, yA, zA, xf, yB, zB);
			AddSignalLine(bucketIndex, xf, yA, zB, xf, yB, zA);
		}
		else
		{
			double zf = (sign > 0) ? (z0 + 1 + outerEpsilon) : (z0 - outerEpsilon);
			double xA = x0 + inset;
			double xB = x0 + 1 - inset;

			AddSignalLine(bucketIndex, xA, yA, zf, xB, yB, zf);
			AddSignalLine(bucketIndex, xA, yB, zf, xB, yA, zf);
		}
	}

	private void AddSignalLine(int bucketIndex, double x0, double y0, double z0, double x1, double y1, double z1)
	{
		BucketLines[bucketIndex].Add(new Line(new Vec3d(x0, y0, z0), new Vec3d(x1, y1, z1)));
	}

	private void RebuildDynamicAutomationBuckets()
	{
		ClearDynamicBuckets(AutomationBucketStart, AutomationBucketCount);

		if (ClientAPI == null || AutomationLocomotives.Length == 0) return;

		for (int locomotiveIndex = 0; locomotiveIndex < AutomationLocomotives.Length; locomotiveIndex++)
		{
			AutomationDebugLocomotive locomotiveDebug = AutomationLocomotives[locomotiveIndex];
			int slot = locomotiveDebug.Slot;
			if ((uint)slot >= AutomationBucketCount) continue;

			double x = locomotiveDebug.X;
			double y = locomotiveDebug.Y;
			double z = locomotiveDebug.Z;
			double yaw = locomotiveDebug.Yaw;

			Entity entity = ClientAPI.World.GetEntityById(locomotiveDebug.EntityID);
			if (entity != null)
			{
				x = entity.Pos.X;
				y = entity.Pos.Y;
				z = entity.Pos.Z;
				yaw = entity.Pos.Yaw;
			}

			AddVehicleWireBox(DynamicBucketLines[AutomationBucketStart + slot], x, y, z, yaw,
				locomotiveDebug.VehicleLength, locomotiveDebug.BodyOffsetForward, locomotiveDebug.HalfWidth, locomotiveDebug.Height, locomotiveDebug.BottomOffset);
		}
	}

	private void RebuildDynamicSGSizeTestBucket(EntityPlayer player)
	{
		ClearDynamicBuckets(SignalUnknownBucket, 1);
		if (ClientAPI == null || player == null) return;

		EntityStandardGaugeLocomotive? nearest = null;
		double playerX = player.Pos.X;
		double playerY = player.Pos.Y;
		double playerZ = player.Pos.Z;
		double bestSquaredDistance = 64.0 * 64.0;

		foreach (Entity entity in ClientAPI.World.LoadedEntities.Values)
		{
			if (entity is not EntityStandardGaugeLocomotive standardGaugeLocomotive || !entity.Alive) continue;

			double dx = entity.Pos.X - playerX;
			double dy = entity.Pos.Y - playerY;
			double dz = entity.Pos.Z - playerZ;
			double squaredDistance = dx * dx + dy * dy + dz * dz;
			if (squaredDistance >= bestSquaredDistance) continue;

			bestSquaredDistance = squaredDistance;
			nearest = standardGaugeLocomotive;
		}

		if (nearest == null) return;

		AddVehicleWireBox
		(
			DynamicBucketLines[SignalUnknownBucket],
			nearest.Pos.X, nearest.Pos.Y, nearest.Pos.Z, nearest.Pos.Yaw,
			nearest.DebugVehicleLength, nearest.DebugBodyOffsetForward,
			1.0, 3.0, -0.15
		);
	}

	private static void AddVehicleWireBox
	(
		List<Line> lines, double x, double y, double z, double yaw,
		double vehicleLength, double bodyOffsetForward, double halfWidth, double height, double bottomOffset
	)
	{
		double adjustedVehicleLength = Math.Max(0.1, vehicleLength);
		halfWidth = Math.Max(0.05, halfWidth);
		height = Math.Max(0.05, height);
		double frontOffset = bodyOffsetForward;
		double rearOffset = bodyOffsetForward - adjustedVehicleLength;

		double fx = Math.Sin(yaw);
		double fz = Math.Cos(yaw);
		double rx = Math.Cos(yaw);
		double rz = -Math.Sin(yaw);

		Vec3d[] corners = new Vec3d[8];
		int cornerIndex = 0;

		for (int heightIndex = 0; heightIndex < 2; heightIndex++)
		{
			double cornerY = y + bottomOffset + heightIndex * height;
			for (int lengthIndex = 0; lengthIndex < 2; lengthIndex++)
			{
				double lengthOffset = lengthIndex == 0 ? rearOffset : frontOffset;
				for (int widthIndex = 0; widthIndex < 2; widthIndex++)
				{
					double widthOffset = widthIndex == 0 ? -halfWidth : halfWidth;
					corners[cornerIndex++] = new Vec3d(x + fx * lengthOffset + rx * widthOffset, cornerY, z + fz * lengthOffset + rz * widthOffset);
				}
			}
		}

		// bottom
		lines.Add(new Line(corners[0], corners[1]));
		lines.Add(new Line(corners[1], corners[3]));
		lines.Add(new Line(corners[3], corners[2]));
		lines.Add(new Line(corners[2], corners[0]));

		// top
		lines.Add(new Line(corners[4], corners[5]));
		lines.Add(new Line(corners[5], corners[7]));
		lines.Add(new Line(corners[7], corners[6]));
		lines.Add(new Line(corners[6], corners[4]));

		// verticals
		lines.Add(new Line(corners[0], corners[4]));
		lines.Add(new Line(corners[1], corners[5]));
		lines.Add(new Line(corners[2], corners[6]));
		lines.Add(new Line(corners[3], corners[7]));
	}

	private void RebuildAutomationPathBuckets()
	{
		ClearBuckets(AutomationBucketStart, AutomationBucketCount);

		for (int locomotiveIndex = 0; locomotiveIndex < AutomationLocomotives.Length; locomotiveIndex++)
		{
			AutomationDebugLocomotive locomotiveDebug = AutomationLocomotives[locomotiveIndex];
			int slot = locomotiveDebug.Slot;
			if ((uint)slot >= AutomationBucketCount) continue;
			if (!locomotiveDebug.HasRoute || locomotiveDebug.PathCoordinates16 == null || locomotiveDebug.PathCoordinates16.Length < 6) continue;

			double yOffset = (slot + 0.5) * 0.125;
			double sideOffset = GetAutomationSlotHorizontalOffset(slot);
			int bucketIndex = AutomationBucketStart + slot;
			int[] pathPoints = locomotiveDebug.PathCoordinates16;

			for (int pointIndex = 0; pointIndex + 5 < pathPoints.Length; pointIndex += 3)
			{
				if (IsAutomationPathBreak(pathPoints, pointIndex) || IsAutomationPathBreak(pathPoints, pointIndex + 3)) continue;
				if (SquaredDistance16(pathPoints[pointIndex], pathPoints[pointIndex + 1], pathPoints[pointIndex + 2], pathPoints[pointIndex + 3], pathPoints[pointIndex + 4], pathPoints[pointIndex + 5]) > MaxAutomationDebugRenderSegmentSquaredDistance16) continue;

				double ax = pathPoints[pointIndex] / 16.0;
				double ay = pathPoints[pointIndex + 1] / 16.0 + yOffset;
				double az = pathPoints[pointIndex + 2] / 16.0;
				double bx = pathPoints[pointIndex + 3] / 16.0;
				double by = pathPoints[pointIndex + 4] / 16.0 + yOffset;
				double bz = pathPoints[pointIndex + 5] / 16.0;

				double dx = bx - ax;
				double dz = bz - az;
				double horizontalLength = Math.Sqrt(dx * dx + dz * dz);
				if (horizontalLength > 1e-8)
				{
					double offsetX = -dz / horizontalLength * sideOffset;
					double offsetZ = dx / horizontalLength * sideOffset;
					ax += offsetX; az += offsetZ;
					bx += offsetX; bz += offsetZ;
				}

				BucketLines[bucketIndex].Add(new Line(new Vec3d(ax, ay, az), new Vec3d(bx, by, bz)));
			}
		}

		AutomationStaticDirty = true;
	}

	private static double GetAutomationSlotHorizontalOffset(int slot) { return AutomationBucketCount <= 1 ? 0.0 : -0.5 + slot * (1.0 / (AutomationBucketCount - 1)); }

	private static bool IsAutomationPathBreak(int[] pathPoints, int offset)
	{
		return pathPoints[offset] == AutomationDebugPathBreak && pathPoints[offset + 1] == AutomationDebugPathBreak && pathPoints[offset + 2] == AutomationDebugPathBreak;
	}

	private void OnAutomationDebugUpdate(AutomationDebugUpdate message)
	{
		if (!ShowAutomationDebug)
		{
			ClearBuckets(AutomationBucketStart, AutomationBucketCount);
			ClearDynamicBuckets(AutomationBucketStart, AutomationBucketCount);
			AutomationLocomotives = Array.Empty<AutomationDebugLocomotive>();
			AutomationLocomotiveByID.Clear();
			AutomationBucketMaskByOwnerID.Clear();
			AutomationSignalStateByEdge.Clear();
			AutomationSignalGraphVersion = -1;
			AutomationSignalOccupancySerial = 0;
			AutomationOwnerPositions.Clear();
			return;
		}

		AutomationLocomotives = message.Locomotives ?? Array.Empty<AutomationDebugLocomotive>();

		AutomationLocomotiveByID.Clear();
		AutomationBucketMaskByOwnerID.Clear();
		for (int locomotiveIndex = 0; locomotiveIndex < AutomationLocomotives.Length; locomotiveIndex++)
		{
			AutomationDebugLocomotive locomotive = AutomationLocomotives[locomotiveIndex];
			AutomationLocomotiveByID[locomotive.EntityID] = locomotive;

			long ownerID = locomotive.OccupancyOwnerID != 0 ? locomotive.OccupancyOwnerID : locomotive.EntityID;
			if (ownerID == 0 || (uint)locomotive.Slot >= AutomationBucketCount) continue;

			AutomationBucketMaskByOwnerID.TryGetValue(ownerID, out int bucketMask);
			AutomationBucketMaskByOwnerID[ownerID] = bucketMask | (1 << locomotive.Slot);
		}

		AutomationSignalStateByEdge.Clear();
		AutomationOwnerPositions.Clear();
		if (ShowZones && message.GraphBuildVersion == Snapshot.BuildVersion)
		{
			AutomationDebugSignalEdgeState[] signalEdges =
				message.SignalEdges ?? Array.Empty<AutomationDebugSignalEdgeState>();
			for (int signalEdgeIndex = 0; signalEdgeIndex < signalEdges.Length; signalEdgeIndex++)
			{
				AutomationDebugSignalEdgeState signalEdgeState = signalEdges[signalEdgeIndex];
				if (signalEdgeState.EdgeHash != 0 && signalEdgeState.ZoneID != 0) { AutomationSignalStateByEdge[signalEdgeState.EdgeHash] = signalEdgeState; }
			}

			AutomationDebugOwnerPosition[] positions =
				message.OwnerPositions ?? Array.Empty<AutomationDebugOwnerPosition>();
			for (int positionIndex = 0; positionIndex < positions.Length; positionIndex++)
			{
				if (positions[positionIndex].OwnerID != 0) AutomationOwnerPositions[positions[positionIndex].OwnerID] = positions[positionIndex];
			}

			AutomationSignalGraphVersion = message.GraphBuildVersion;
			AutomationSignalOccupancySerial = message.OccupancySerial;
			if (AuthoritativeOccupancyGraphVersion == message.GraphBuildVersion && AuthoritativeOccupancySerial != AutomationSignalOccupancySerial) { NextRequestMS = 0; }
		}
		else
		{
			AutomationSignalGraphVersion = -1;
			AutomationSignalOccupancySerial = 0;
			if (ShowZones)
			{
				AuthoritativeOccupancyByEdge.Clear();
				AuthoritativeOccupancyGraphVersion = -1;
				AuthoritativeOccupancySerial = 0;
				NextRequestMS = 0;
			}
		}

		RebuildAutomationPathBuckets();
		if (ShowZones) LastZonesBuildVersion = -1;
	}

	private void OnAutomationDebugToggleFromClient(IServerPlayer fromPlayer, AutomationDebugToggle message)
	{
		AutomationDebugFeatures features = message.Features;
		if (fromPlayer == null || (features != AutomationDebugFeatures.None && !fromPlayer.HasPrivilege(Privilege.controlserver))) { return; }

		if (features != AutomationDebugFeatures.None)
		{
			bool wasEmpty = AutomationSubscribers.Count == 0;
			AutomationSubscribers[fromPlayer] = features;
			if (wasEmpty) StartAutomationServerUpdates();
		}
		else
		{
			AutomationSlotStateByPlayerID.Remove(fromPlayer.PlayerUID);
			if (AutomationSubscribers.Remove(fromPlayer) && AutomationSubscribers.Count == 0) StopAutomationServerUpdates();
		}
	}

	private void OnAutomationDebugPlayerLeave(IServerPlayer player)
	{
		AutomationSlotStateByPlayerID.Remove(player.PlayerUID);
		if (AutomationSubscribers.Remove(player) && AutomationSubscribers.Count == 0) StopAutomationServerUpdates();
	}

	private void StartAutomationServerUpdates()
	{
		if (ServerAPI == null || AutomationServerTickListenerID != 0) return;
		AutomationServerTickListenerID = ServerAPI.Event.RegisterGameTickListener(OnAutomationServerTick, AutomationDebugUpdateMS);
	}

	private void StopAutomationServerUpdates()
	{
		if (ServerAPI != null && AutomationServerTickListenerID != 0) { ServerAPI.Event.UnregisterGameTickListener(AutomationServerTickListenerID); }
		AutomationServerTickListenerID = 0;
		AutomationSubscribers.Clear();
		AutomationSlotStateByPlayerID.Clear();
	}

	private void OnAutomationServerTick(float deltaTime)
	{
		if (ServerAPI == null || ServerAutomationNetwork == null || AutomationSubscribers.Count == 0) return;
		if (ServerRailGraphSystem == null) return;

		bool anySignalOccupantSubscriber = false;
		foreach (AutomationDebugFeatures features in AutomationSubscribers.Values)
		{
			if ((features & AutomationDebugFeatures.SignalOccupants) == 0) continue;
			anySignalOccupantSubscriber = true;
			break;
		}

		AutomationDebugOwnerPosition[] ownerPositions = Array.Empty<AutomationDebugOwnerPosition>();
		if (anySignalOccupantSubscriber)
		{
			BuildOwnerPositionPackets();
			ownerPositions = ServerOwnerPositionPacketsScratch.Count == 0 ? Array.Empty<AutomationDebugOwnerPosition>() : ServerOwnerPositionPacketsScratch.ToArray();
		}
		else
		{
			ServerOwnerPositionByIDScratch.Clear();
			ServerOwnerPositionPacketsScratch.Clear();
		}

		foreach (KeyValuePair<IServerPlayer, AutomationDebugFeatures> subscriber in AutomationSubscribers)
		{
			IServerPlayer player = subscriber.Key;
			AutomationDebugFeatures features = subscriber.Value;
			if (player == null || player.ConnectionState != EnumClientState.Playing) continue;

			AutomationDebugSignalEdgeState[] signalEdges = Array.Empty<AutomationDebugSignalEdgeState>();
			AutomationDebugOwnerPosition[] subscriberOwnerPositions = Array.Empty<AutomationDebugOwnerPosition>();
			if ((features & AutomationDebugFeatures.SignalOccupants) != 0 && player.Entity != null)
			{
				EntityPos playerPosition = player.Entity.ServerPos;
				var centerPosition = new BlockPos
				(
					(int)Math.Floor(playerPosition.X),
					(int)Math.Floor(playerPosition.Y),
					(int)Math.Floor(playerPosition.Z),
					playerPosition.Dimension
				);

				ServerRailGraphSystem.DebugCollectSignalEdgeStates(centerPosition, (int)Math.Ceiling(AutomationDebugMaxDistanceBlocks), ServerSignalEdgePacketsScratch);
				signalEdges = ServerSignalEdgePacketsScratch.Count == 0 ? Array.Empty<AutomationDebugSignalEdgeState>() : ServerSignalEdgePacketsScratch.ToArray();
				subscriberOwnerPositions = ownerPositions;
			}

			AutomationDebugUpdate update = BuildAutomationUpdateForPlayer(player, features, signalEdges, subscriberOwnerPositions);
			ServerAutomationNetwork.SendPacket(update, player);
		}
	}

	private AutomationDebugUpdate BuildAutomationUpdateForPlayer
	(
		IServerPlayer player,
		AutomationDebugFeatures features,
		AutomationDebugSignalEdgeState[] signalEdges,
		AutomationDebugOwnerPosition[] ownerPositions
	)
	{
		ServerLocomotivePacketsScratch.Clear();
		ServerLocomotivesScratch.Clear();
		ServerLocomotiveByIDScratch.Clear();
		ServerLocomotiveSquaredDistanceByIDScratch.Clear();
		ServerAssignedLocomotiveIDsScratch.Clear();

		if ((features & AutomationDebugFeatures.Routes) == 0)
		{
			return new AutomationDebugUpdate
			{
				SignalEdges = signalEdges,
				OwnerPositions = ownerPositions,
				OccupancySerial = ServerRailGraphSystem?.OccupancyChangeSerial ?? 0,
				GraphBuildVersion = ServerRailGraphSystem?.GraphBuildVersion ?? 0
			};
		}

		EntityPlayer? playerEntity = player.Entity;
		if (playerEntity == null)
		{
			return new AutomationDebugUpdate
			{
				SignalEdges = signalEdges,
				OwnerPositions = ownerPositions,
				OccupancySerial = ServerRailGraphSystem?.OccupancyChangeSerial ?? 0,
				GraphBuildVersion = ServerRailGraphSystem?.GraphBuildVersion ?? 0
			};
		}

		double playerX = playerEntity.ServerPos.X;
		double playerY = playerEntity.ServerPos.Y;
		double playerZ = playerEntity.ServerPos.Z;
		int dimension = playerEntity.ServerPos.Dimension;
		double maxSquaredDistance = AutomationDebugMaxDistanceBlocks * AutomationDebugMaxDistanceBlocks;

		foreach (Entity entity in ServerAPI!.World.LoadedEntities.Values)
		{
			if (entity is not IRailwayConvoyVehicle vehicle) continue;
			if (!entity.Alive || !vehicle.HasTractionEngine) continue;
			if (entity.ServerPos.Dimension != dimension) continue;

			double deltaX = entity.ServerPos.X - playerX;
			double deltaY = entity.ServerPos.Y - playerY;
			double deltaZ = entity.ServerPos.Z - playerZ;
			double squaredDistance = deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ;
			if (squaredDistance > maxSquaredDistance) continue;

			long entityID = entity.EntityId;
			ServerLocomotivesScratch.Add(vehicle);
			ServerLocomotiveByIDScratch[entityID] = vehicle;
			ServerLocomotiveSquaredDistanceByIDScratch[entityID] = squaredDistance;
		}

		ServerLocomotivesScratch.Sort((leftVehicle, rightVehicle) =>
			ServerLocomotiveSquaredDistanceByIDScratch[leftVehicle.Entity.EntityId].CompareTo(ServerLocomotiveSquaredDistanceByIDScratch[rightVehicle.Entity.EntityId]));

		if (!AutomationSlotStateByPlayerID.TryGetValue(player.PlayerUID, out AutomationDebugSlotState? slotState))
		{
			slotState = new AutomationDebugSlotState();
			AutomationSlotStateByPlayerID[player.PlayerUID] = slotState;
		}

		// Preserve existing color assignments first. Only clear slots for powered vehicles that are no longer valid/nearby,
		// then evict the farthest assigned vehicle when a closer unassigned vehicle appears. This keeps colors stable while still converging to the nearest 6.
		for (int slot = 0; slot < AutomationDebugMaxLocomotives; slot++)
		{
			long entityID = slotState.EntityIDs[slot];
			if (entityID == 0) continue;

			if (!ServerLocomotiveByIDScratch.ContainsKey(entityID) || !ServerAssignedLocomotiveIDsScratch.Add(entityID)) { slotState.EntityIDs[slot] = 0; }
		}

		for (int locomotiveIndex = 0; locomotiveIndex < ServerLocomotivesScratch.Count; locomotiveIndex++)
		{
			long entityID = ServerLocomotivesScratch[locomotiveIndex].Entity.EntityId;
			if (ServerAssignedLocomotiveIDsScratch.Contains(entityID)) continue;

			int slot = FindFreeAutomationSlot(slotState);
			if (slot < 0)
			{
				double candidateSquaredDistance = ServerLocomotiveSquaredDistanceByIDScratch[entityID];
				slot = FindFarthestAutomationSlot(slotState, ServerLocomotiveSquaredDistanceByIDScratch, out double farthestSquaredDistance);
				if (slot < 0 || candidateSquaredDistance >= farthestSquaredDistance) continue;

				ServerAssignedLocomotiveIDsScratch.Remove(slotState.EntityIDs[slot]);
			}

			slotState.EntityIDs[slot] = entityID;
			ServerAssignedLocomotiveIDsScratch.Add(entityID);
		}

		for (int slot = 0; slot < AutomationDebugMaxLocomotives; slot++)
		{
			long entityID = slotState.EntityIDs[slot];
			if (entityID == 0 || !ServerLocomotiveByIDScratch.TryGetValue(entityID, out IRailwayConvoyVehicle? vehicle)) continue;

			Entity entity = vehicle.Entity;
			long ownerID = GetAutomationDebugOwnerID(vehicle);

			RailAutomationRoute? route = null;
			string statusText = "";
			bool hasRoute = ServerAutomationSystem != null && ServerAutomationSystem.DebugTryGetRoute(ownerID, out route, out statusText);
			bool truncated = false;
			int[] pathCoordinates16 = Array.Empty<int>();

			if (hasRoute && route != null)
			{
				ServerPathCoordinatesScratch.Clear();
				if (TryGetAutomationDebugCursor(vehicle, out RailwayVehicleShared.RailCursor cursor))
				{
					BuildAutomationDebugPath(cursor, route, ServerPathCoordinatesScratch, out truncated);
				}

				if (ServerPathCoordinatesScratch.Count >= 6) pathCoordinates16 = ServerPathCoordinatesScratch.ToArray();
				else hasRoute = false;
			}
			else { statusText = ""; }

			GetAutomationDebugBox
			(
				vehicle,
				out double vehicleLength,
				out double bodyOffsetForward,
				out double halfWidth,
				out double height,
				out double bottomOffset
			);

			ServerLocomotivePacketsScratch.Add(new AutomationDebugLocomotive
			{
				EntityID = entity.EntityId,
				OccupancyOwnerID = ownerID,
				Slot = slot,
				X = entity.ServerPos.X,
				Y = entity.ServerPos.Y,
				Z = entity.ServerPos.Z,
				Yaw = entity.ServerPos.Yaw,
				VehicleLength = vehicleLength,
				BodyOffsetForward = bodyOffsetForward,
				HalfWidth = halfWidth,
				Height = height,
				BottomOffset = bottomOffset,
				HasRoute = hasRoute,
				PathTruncated = truncated,
				PathCoordinates16 = pathCoordinates16,
				StatusText = statusText ?? ""
			});
		}

		return new AutomationDebugUpdate
		{
			Locomotives = ServerLocomotivePacketsScratch.ToArray(),
			SignalEdges = signalEdges,
			OwnerPositions = ownerPositions,
			OccupancySerial = ServerRailGraphSystem?.OccupancyChangeSerial ?? 0,
			GraphBuildVersion = ServerRailGraphSystem?.GraphBuildVersion ?? 0
		};
	}

	private static int FindFreeAutomationSlot(AutomationDebugSlotState slotState)
	{
		for (int slot = 0; slot < AutomationDebugMaxLocomotives; slot++) { if (slotState.EntityIDs[slot] == 0) return slot; }
		return -1;
	}

	private static int FindFarthestAutomationSlot(AutomationDebugSlotState slotState, Dictionary<long, double> squaredDistanceByEntityID, out double farthestSquaredDistance)
	{
		int farthestSlot = -1;
		farthestSquaredDistance = double.MinValue;

		for (int slot = 0; slot < AutomationDebugMaxLocomotives; slot++)
		{
			long entityID = slotState.EntityIDs[slot];
			if (entityID == 0 || !squaredDistanceByEntityID.TryGetValue(entityID, out double squaredDistance)) continue;

			if (squaredDistance > farthestSquaredDistance)
			{
				farthestSquaredDistance = squaredDistance;
				farthestSlot = slot;
			}
		}

		return farthestSlot;
	}


	private void BuildOwnerPositionPackets()
	{
		ServerOwnerPositionByIDScratch.Clear();
		ServerOwnerPositionPacketsScratch.Clear();

		foreach (Entity entity in ServerAPI!.World.LoadedEntities.Values)
		{
			if (entity is not IRailwayConvoyVehicle vehicle) continue;
			if (!entity.Alive) continue;

			long ownerID = GetAutomationDebugOwnerID(vehicle);
			if (ownerID == 0) continue;

			bool preferEntityPosition = ownerID == entity.EntityId || !ServerOwnerPositionByIDScratch.ContainsKey(ownerID);
			if (!preferEntityPosition) continue;

			ServerOwnerPositionByIDScratch[ownerID] = new AutomationDebugOwnerPosition
			{
				OwnerID = ownerID,
				X = entity.ServerPos.X,
				Y = entity.ServerPos.Y,
				Z = entity.ServerPos.Z,
				Virtual = false
			};
		}

		foreach (var ownerPositionEntry in ServerOwnerPositionByIDScratch)
		{
			ServerOwnerPositionPacketsScratch.Add(ownerPositionEntry.Value);
		}
	}

	private static long GetAutomationDebugOwnerID(IRailwayConvoyVehicle vehicle)
	{
		long headID = vehicle.ConvoyHeadEntityID;
		return headID != 0 ? headID : vehicle.Entity.EntityId;
	}

	private static bool TryGetAutomationDebugCursor(IRailwayConvoyVehicle vehicle, out RailwayVehicleShared.RailCursor cursor)
	{
		switch (vehicle.Entity)
		{
			case EntityStandardGaugeLocomotive standardGaugeLocomotive: return standardGaugeLocomotive.DebugTryGetRailCursor(out cursor);
			case EntityMinecart minecart: return minecart.DebugTryGetRailCursor(out cursor);
			default: cursor = default; return false;
		}
	}

	private static void GetAutomationDebugBox
	(
		IRailwayConvoyVehicle vehicle,
		out double length,
		out double forwardOffset,
		out double halfWidth,
		out double height,
		out double bottomOffset
	)
	{
		if (vehicle.Entity is EntityStandardGaugeLocomotive standardGaugeLocomotive)
		{
			length = standardGaugeLocomotive.DebugVehicleLength;
			forwardOffset = standardGaugeLocomotive.DebugBodyOffsetForward;
			halfWidth = 1.0;
			height = 3.0;
			bottomOffset = -0.15;
			return;
		}

		length = 1.0;
		forwardOffset = 0.5;
		halfWidth = 0.5;
		height = 1.0;
		bottomOffset = -0.15;
	}

	private void BuildAutomationDebugPath(RailwayVehicleShared.RailCursor cursor, RailAutomationRoute route, List<int> destinationCoordinates, out bool truncated)
	{
		destinationCoordinates.Clear();
		truncated = false;

		RailGraphLive? railGraph = ServerRailGraphSystem?.Graph;
		if (railGraph == null || route == null) return;

		ServerPathEdgesScratch.Clear();
		AppendAutomationDebugRouteEdges(route.ApproachEdges, ServerPathEdgesScratch);

		for (int arcIndex = 0; arcIndex < route.Arcs.Length; arcIndex++) { AppendAutomationDebugRouteEdges(route.Arcs[arcIndex].Edges, ServerPathEdgesScratch); }

		AppendAutomationDebugRouteEdge(route.Target.ApproachEdgeHash, ServerPathEdgesScratch);

		int currentEdgeIndex = FindAutomationDebugRouteEdgeIndex(cursor.SegmentHash, ServerPathEdgesScratch);
		if (currentEdgeIndex < 0) return;

		ulong nextEdgeHash = currentEdgeIndex + 1 < ServerPathEdgesScratch.Count ? ServerPathEdgesScratch[currentEdgeIndex + 1] : 0;
		int movementDirection = ResolveAutomationDebugCurrentEdgeDirection(railGraph, cursor.SegmentHash, nextEdgeHash, route.Target.StopEndpoint, cursor.Direction);

		AppendCurrentCursorPolylineCoordinates16(cursor, movementDirection, destinationCoordinates, ref truncated);

		int edgeCount = 1;
		ulong lastEdgeHash = cursor.SegmentHash;
		for (int edgeIndex = currentEdgeIndex + 1; edgeIndex < ServerPathEdgesScratch.Count; edgeIndex++)
		{
			TryAddAutomationDebugEdge(railGraph, ServerPathEdgesScratch[edgeIndex], destinationCoordinates, ref edgeCount, ref lastEdgeHash, ref truncated);
		}
	}

	private static void AppendAutomationDebugRouteEdges(ulong[] routeEdges, List<ulong> destinationEdges)
	{
		if (routeEdges == null) return;

		for (int edgeIndex = 0; edgeIndex < routeEdges.Length; edgeIndex++) { AppendAutomationDebugRouteEdge(routeEdges[edgeIndex], destinationEdges); }
	}

	private static void AppendAutomationDebugRouteEdge(ulong edgeHash, List<ulong> destinationEdges)
	{
		if (edgeHash == 0) return;
		if (destinationEdges.Count != 0 && destinationEdges[destinationEdges.Count - 1] == edgeHash) return;
		if (destinationEdges.Count >= MaxAutomationDebugPathEdgesPerLocomotive) return;
		destinationEdges.Add(edgeHash);
	}

	private static int FindAutomationDebugRouteEdgeIndex(ulong currentEdgeHash, List<ulong> edges)
	{
		if (currentEdgeHash == 0) return -1;

		for (int edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++) { if (edges[edgeIndex] == currentEdgeHash) return edgeIndex; }

		return -1;
	}

	private static int ResolveAutomationDebugCurrentEdgeDirection(RailGraphLive railGraph, ulong currentEdgeHash, ulong nextEdgeHash, RailGraphLive.EndpointKey targetEndpoint, int fallbackDirection)
	{
		if (!railGraph.TryGetPolyline16(currentEdgeHash, out int[] polylineCoordinates16) || polylineCoordinates16.Length < 6)
		{
			return fallbackDirection >= 0 ? 1 : -1;
		}

		int lastPointOffset = polylineCoordinates16.Length - 3;

		if (nextEdgeHash != 0 && railGraph.TryGetPolyline16(nextEdgeHash, out int[] nextPolylineCoordinates16) && nextPolylineCoordinates16.Length >= 6)
		{
			long firstSquaredDistance = MinSquaredDistanceToEdgeEnds16(polylineCoordinates16[0], polylineCoordinates16[1], polylineCoordinates16[2], nextPolylineCoordinates16);
			long lastSquaredDistance = MinSquaredDistanceToEdgeEnds16(polylineCoordinates16[lastPointOffset], polylineCoordinates16[lastPointOffset + 1], polylineCoordinates16[lastPointOffset + 2], nextPolylineCoordinates16);
			if (firstSquaredDistance != lastSquaredDistance) return firstSquaredDistance < lastSquaredDistance ? -1 : 1;
		}

		long firstSquaredDistanceToTarget = SquaredDistance16(polylineCoordinates16[0], polylineCoordinates16[1], polylineCoordinates16[2], targetEndpoint.X16, targetEndpoint.Y16, targetEndpoint.Z16);
		long lastSquaredDistanceToTarget = SquaredDistance16(polylineCoordinates16[lastPointOffset], polylineCoordinates16[lastPointOffset + 1], polylineCoordinates16[lastPointOffset + 2], targetEndpoint.X16, targetEndpoint.Y16, targetEndpoint.Z16);
		if (firstSquaredDistanceToTarget != lastSquaredDistanceToTarget) return firstSquaredDistanceToTarget < lastSquaredDistanceToTarget ? -1 : 1;

		return fallbackDirection >= 0 ? 1 : -1;
	}

	private static long MinSquaredDistanceToEdgeEnds16(int x, int y, int z, int[] edgeCoordinates16)
	{
		int lastPointOffset = edgeCoordinates16.Length - 3;
		long squaredDistanceToFirstEndpoint = SquaredDistance16(x, y, z, edgeCoordinates16[0], edgeCoordinates16[1], edgeCoordinates16[2]);
		long squaredDistanceToLastEndpoint = SquaredDistance16(x, y, z, edgeCoordinates16[lastPointOffset], edgeCoordinates16[lastPointOffset + 1], edgeCoordinates16[lastPointOffset + 2]);
		return squaredDistanceToFirstEndpoint < squaredDistanceToLastEndpoint ? squaredDistanceToFirstEndpoint : squaredDistanceToLastEndpoint;
	}

	private static void AppendCurrentCursorPolylineCoordinates16(in RailwayVehicleShared.RailCursor cursor, int movementDirection, List<int> destinationCoordinates, ref bool truncated)
	{
		int[]? polylineCoordinates16 = cursor.PolyXYZ16;
		if (polylineCoordinates16 == null || cursor.PointCount < 2 || truncated) return;

		int pointCount = Math.Min(cursor.PointCount, polylineCoordinates16.Length / 3);
		if (pointCount < 2) return;

		int segmentIndex = cursor.SegmentIndex;
		if (segmentIndex < 0) segmentIndex = 0;
		else if (segmentIndex > pointCount - 2) segmentIndex = pointCount - 2;

		double segmentProgress = cursor.NormalizedSegmentProgress;
		if (segmentProgress < 0) segmentProgress = 0;
		else if (segmentProgress > 1) segmentProgress = 1;

		int sourcePointOffset = segmentIndex * 3;
		int destinationPointOffset = (segmentIndex + 1) * 3;
		int x = (int)Math.Round(polylineCoordinates16[sourcePointOffset] + (polylineCoordinates16[destinationPointOffset] - polylineCoordinates16[sourcePointOffset]) * segmentProgress);
		int y = (int)Math.Round(polylineCoordinates16[sourcePointOffset + 1] + (polylineCoordinates16[destinationPointOffset + 1] - polylineCoordinates16[sourcePointOffset + 1]) * segmentProgress);
		int z = (int)Math.Round(polylineCoordinates16[sourcePointOffset + 2] + (polylineCoordinates16[destinationPointOffset + 2] - polylineCoordinates16[sourcePointOffset + 2]) * segmentProgress);

		AppendPointCoordinates16(x, y, z, destinationCoordinates, ref truncated);
		if (truncated) return;

		if (movementDirection >= 0)
		{
			for (int pointIndex = segmentIndex + 1; pointIndex < pointCount; pointIndex++)
			{
				int coordinateOffset = pointIndex * 3;
				AppendPointCoordinates16(polylineCoordinates16[coordinateOffset], polylineCoordinates16[coordinateOffset + 1], polylineCoordinates16[coordinateOffset + 2], destinationCoordinates, ref truncated);
				if (truncated) return;
			}
		}
		else
		{
			for (int pointIndex = segmentIndex; pointIndex >= 0; pointIndex--)
			{
				int coordinateOffset = pointIndex * 3;
				AppendPointCoordinates16(polylineCoordinates16[coordinateOffset], polylineCoordinates16[coordinateOffset + 1], polylineCoordinates16[coordinateOffset + 2], destinationCoordinates, ref truncated);
				if (truncated) return;
			}
		}
	}

	private static void TryAddAutomationDebugEdge(RailGraphLive railGraph, ulong edgeHash, List<int> destinationCoordinates, ref int edgeCount, ref ulong lastEdgeHash, ref bool truncated)
	{
		if (truncated || edgeHash == 0 || edgeHash == lastEdgeHash) return;
		lastEdgeHash = edgeHash;

		if (++edgeCount > MaxAutomationDebugPathEdgesPerLocomotive)
		{
			truncated = true;
			return;
		}

		if (!railGraph.TryGetPolyline16(edgeHash, out int[] polylineCoordinates16)) return;
		AppendPolylineCoordinates16(polylineCoordinates16, destinationCoordinates, ref truncated);
	}

	private static void AppendPolylineCoordinates16(int[] polylineCoordinates16, List<int> destinationCoordinates, ref bool truncated)
	{
		if (polylineCoordinates16 == null || polylineCoordinates16.Length < 6 || truncated) return;

		int pointCount = polylineCoordinates16.Length / 3;
		bool hasPreviousPoint = destinationCoordinates.Count >= 3 && !IsAutomationPathBreak(destinationCoordinates);
		int previousX = 0, previousY = 0, previousZ = 0;

		if (hasPreviousPoint)
		{
			int previousPointOffset = destinationCoordinates.Count - 3;
			previousX = destinationCoordinates[previousPointOffset];
			previousY = destinationCoordinates[previousPointOffset + 1];
			previousZ = destinationCoordinates[previousPointOffset + 2];
		}

		int start = 0;
		int endPointIndex = pointCount - 1;
		int step = 1;

		if (hasPreviousPoint)
		{
			int firstX = polylineCoordinates16[0], firstY = polylineCoordinates16[1], firstZ = polylineCoordinates16[2];
			int lastPointOffset = (pointCount - 1) * 3;
			int lastX = polylineCoordinates16[lastPointOffset], lastY = polylineCoordinates16[lastPointOffset + 1], lastZ = polylineCoordinates16[lastPointOffset + 2];

			long squaredDistanceToFirstPoint = SquaredDistance16(previousX, previousY, previousZ, firstX, firstY, firstZ);
			long squaredDistanceToLastPoint = SquaredDistance16(previousX, previousY, previousZ, lastX, lastY, lastZ);

			if (squaredDistanceToLastPoint < squaredDistanceToFirstPoint)
			{
				start = pointCount - 1;
				endPointIndex = 0;
				step = -1;
			}

			int startPointOffset = start * 3;
			if (SquaredDistance16(previousX, previousY, previousZ, polylineCoordinates16[startPointOffset], polylineCoordinates16[startPointOffset + 1], polylineCoordinates16[startPointOffset + 2]) > MaxAutomationDebugPathJoinSquaredDistance16)
			{
				AppendAutomationPathBreak(destinationCoordinates, ref truncated);
				if (truncated) return;
			}
		}

		for (int pointIndex = start; step > 0 ? pointIndex <= endPointIndex : pointIndex >= endPointIndex; pointIndex += step)
		{
			int pointOffset = pointIndex * 3;
			AppendPointCoordinates16(polylineCoordinates16[pointOffset], polylineCoordinates16[pointOffset + 1], polylineCoordinates16[pointOffset + 2], destinationCoordinates, ref truncated);
			if (truncated) return;
		}
	}

	private static void AppendPointCoordinates16(int x, int y, int z, List<int> destinationCoordinates, ref bool truncated)
	{
		if (destinationCoordinates.Count >= 3 && !IsAutomationPathBreak(destinationCoordinates))
		{
			int previousPointOffset = destinationCoordinates.Count - 3;
			if (destinationCoordinates[previousPointOffset] == x && destinationCoordinates[previousPointOffset + 1] == y && destinationCoordinates[previousPointOffset + 2] == z) return;
		}

		if (destinationCoordinates.Count / 3 >= MaxAutomationDebugPathPointsPerLocomotive)
		{
			truncated = true;
			return;
		}

		destinationCoordinates.Add(x);
		destinationCoordinates.Add(y);
		destinationCoordinates.Add(z);
	}

	private static bool IsAutomationPathBreak(List<int> pathPoints)
	{
		int lastPointOffset = pathPoints.Count - 3;
		return lastPointOffset >= 0 && pathPoints[lastPointOffset] == AutomationDebugPathBreak && pathPoints[lastPointOffset + 1] == AutomationDebugPathBreak && pathPoints[lastPointOffset + 2] == AutomationDebugPathBreak;
	}

	private static void AppendAutomationPathBreak(List<int> destinationCoordinates, ref bool truncated)
	{
		if (destinationCoordinates.Count < 3 || IsAutomationPathBreak(destinationCoordinates)) return;
		if (destinationCoordinates.Count / 3 >= MaxAutomationDebugPathPointsPerLocomotive)
		{
			truncated = true;
			return;
		}

		destinationCoordinates.Add(AutomationDebugPathBreak);
		destinationCoordinates.Add(AutomationDebugPathBreak);
		destinationCoordinates.Add(AutomationDebugPathBreak);
	}

	private static long SquaredDistance16(int sourceX, int sourceY, int sourceZ, int destinationX, int destinationY, int destinationZ)
	{
		long deltaX = sourceX - destinationX;
		long deltaY = sourceY - destinationY;
		long deltaZ = sourceZ - destinationZ;
		return deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ;
	}

	internal static void AppendAutomationDebugHUDInfo(Entity vehicle, StringBuilder textBuilder)
	{
		if (!ClientAutomationDebugHUDEnabled || ClientInstance == null || vehicle == null || textBuilder == null) return;

		if (!ClientInstance.AutomationLocomotiveByID.TryGetValue(vehicle.EntityId, out AutomationDebugLocomotive locomotiveDebug)) return;

		if (textBuilder.Length > 0 && textBuilder[textBuilder.Length - 1] != '\n') textBuilder.AppendLine();
		textBuilder.Append("Automation debug: ID ").Append(locomotiveDebug.EntityID);

		if (locomotiveDebug.OccupancyOwnerID != 0 && locomotiveDebug.OccupancyOwnerID != locomotiveDebug.EntityID)
		{
			textBuilder.Append(", owner ").Append(locomotiveDebug.OccupancyOwnerID);
		}

		textBuilder.Append(", color ").Append(AutomationSlotNames[Math.Max(0, Math.Min(AutomationSlotNames.Length - 1, locomotiveDebug.Slot))]) .AppendLine();

		if (!string.IsNullOrEmpty(locomotiveDebug.StatusText))
		{
			textBuilder.Append("Automation status: ").Append(locomotiveDebug.StatusText).AppendLine();
		}
		else if (!locomotiveDebug.HasRoute)
		{
			textBuilder.AppendLine("Automation route: none");
		}

		if (locomotiveDebug.PathTruncated) textBuilder.AppendLine("Automation route: debug path truncated");
	}

	internal static string AppendTrackDebugHUDInfo(Block block, BlockPos position, string information)
	{
		if
		(
			!ClientTrackZoneDebugHUDEnabled || ClientInstance == null || block == null || position == null ||
			!TrackSpecsDictionary.TryGet(block, out TrackPieceSpec trackSpecification) || trackSpecification.IsSignal
		) { return information; }

		return ClientInstance.AppendTrackDebugHeadsUpDisplayInformation(position, trackSpecification, information);
	}

	private string AppendTrackDebugHeadsUpDisplayInformation(BlockPos position, TrackPieceSpec trackSpecification, string information)
	{
		var textBuilder = new StringBuilder((information?.Length ?? 0) + 160);
		if (!string.IsNullOrEmpty(information)) textBuilder.Append(information);
		AppendDebugSectionSeparator(textBuilder);

		if
		(
			ClientRailGraphSystem == null || AuthoritativeOccupancyGraphVersion != Snapshot.BuildVersion ||
			ClientRailGraphSystem.Graph.CollectSpecEdgeHashesAt(position, trackSpecification, TrackHeadsUpDisplayEdgeHashesScratch) == 0
		) { textBuilder.Append("Zone unknown"); return textBuilder.ToString(); }

		TrackHeadsUpDisplayZoneIDsScratch.Clear();
		for (int edgeIndex = 0; edgeIndex < TrackHeadsUpDisplayEdgeHashesScratch.Count; edgeIndex++)
		{
			ulong edgeHash = TrackHeadsUpDisplayEdgeHashesScratch[edgeIndex];
			if
			(
				!AuthoritativeOccupancyByEdge.TryGetValue(edgeHash, out RailGraphDebugEdgeOccupancy record) ||
				record.ZoneID == 0 || TrackHeadsUpDisplayZoneIDsScratch.Contains(record.ZoneID)
			) { continue; }
			TrackHeadsUpDisplayZoneIDsScratch.Add(record.ZoneID);
		}

		if (TrackHeadsUpDisplayZoneIDsScratch.Count == 0) { textBuilder.Append("Zone unknown"); return textBuilder.ToString(); }

		TrackHeadsUpDisplayZoneIDsScratch.Sort();
		for (int zoneIndex = 0; zoneIndex < TrackHeadsUpDisplayZoneIDsScratch.Count; zoneIndex++)
		{
			if (zoneIndex > 0) textBuilder.Append('\n');

			ulong zoneID = TrackHeadsUpDisplayZoneIDsScratch[zoneIndex];
			RailGraphDebugEdgeOccupancy? zoneRecord = null;
			for (int edgeIndex = 0; edgeIndex < TrackHeadsUpDisplayEdgeHashesScratch.Count; edgeIndex++)
			{
				if
				(
					AuthoritativeOccupancyByEdge.TryGetValue(TrackHeadsUpDisplayEdgeHashesScratch[edgeIndex], out RailGraphDebugEdgeOccupancy candidate) &&
					candidate.ZoneID == zoneID
				) { zoneRecord = candidate; break; }
			}

			if (zoneRecord == null)
			{
				textBuilder.Append("Zone ").Append(zoneID).Append(", Unknown");
				continue;
			}

			long[] owners = zoneRecord.OwnerIDs ?? Array.Empty<long>();
			bool hasOwner = false;
			for (int ownerIndex = 0; ownerIndex < owners.Length; ownerIndex++) { if (owners[ownerIndex] != 0) { hasOwner = true; break; } }

			if (hasOwner)
			{
				textBuilder.Append("Zone ").Append(zoneID).Append(", Occupied By:");
				for (int ownerIndex = 0; ownerIndex < owners.Length; ownerIndex++)
				{
					long ownerID = owners[ownerIndex];
					if (ownerID == 0) continue;
					textBuilder.Append('\n').Append("ID ");
					AppendOwnerIDWithBucket(textBuilder, ownerID);
				}
			}
			else
			{
				textBuilder.Append("Zone ").Append(zoneID).Append(", ");
				if (zoneRecord.Occupied) { textBuilder.Append("Occupied By:\nID unavailable"); }
				else { textBuilder.Append("Free"); }
			}
		}

		return textBuilder.ToString();
	}

	internal static void AppendSignalDebugHUDInfo(ICoreAPI coreAPI, BlockPos position, StringBuilder textBuilder)
	{
		if (!ClientSignalDebugHUDEnabled || !ClientAutomationDebugHUDEnabled || ClientInstance == null || position == null || textBuilder == null) return;

		var signalKey = new SignalHeadsUpDisplayKey(position.X, position.Y, position.Z, position.dimension);
		if (!ClientInstance.SignalHeadsUpDisplayInformationByPosition.TryGetValue(signalKey, out string signalInformation)) return;

		AppendDebugSectionSeparator(textBuilder);
		textBuilder.Append(signalInformation);
		if (textBuilder.Length > 0 && textBuilder[textBuilder.Length - 1] != '\n') textBuilder.Append('\n');
	}

	private static void WriteLinesToMesh(List<Line> lines, MeshData meshData, Vec3d cameraPosition, ref int usedVertices)
	{
		for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
		{
			Line line = lines[lineIndex];

			float x0 = (float)(line.A.X - cameraPosition.X);
			float y0 = (float)(line.A.Y - cameraPosition.Y);
			float z0 = (float)(line.A.Z - cameraPosition.Z);

			float x1 = (float)(line.B.X - cameraPosition.X);
			float y1 = (float)(line.B.Y - cameraPosition.Y);
			float z1 = (float)(line.B.Z - cameraPosition.Z);

			int coordinateOffset = usedVertices * 3;
			meshData.xyz[coordinateOffset + 0] = x0;
			meshData.xyz[coordinateOffset + 1] = y0;
			meshData.xyz[coordinateOffset + 2] = z0;
			meshData.xyz[coordinateOffset + 3] = x1;
			meshData.xyz[coordinateOffset + 4] = y1;
			meshData.xyz[coordinateOffset + 5] = z1;

			usedVertices += 2;
		}
	}

	private void EnsureMeshCapacity(int bucketIndex, int lineCount)
	{
		if (ClientAPI == null) return;

		int neededVertices = Math.Max(2, lineCount * 2);

		if (BucketMeshReferences[bucketIndex] != null && BucketMeshData[bucketIndex] != null && BucketMeshCapacityVertices[bucketIndex] >= neededVertices) { return; }

		if (BucketMeshReferences[bucketIndex] != null)
		{
			ClientAPI.Render.DeleteMesh(BucketMeshReferences[bucketIndex]);
			BucketMeshReferences[bucketIndex] = null;
		}

		var meshData = new MeshData(neededVertices, neededVertices, withNormals: false, withUv: false);
		meshData.SetMode(EnumDrawMode.Lines);

		for (int vertexIndex = 0; vertexIndex < neededVertices; vertexIndex++)
		{
			meshData.AddVertexSkipTex(0, 0, 0, ColorUtil.WhiteArgb);
			meshData.AddIndex(vertexIndex);
		}

		BucketMeshData[bucketIndex] = meshData;
		BucketMeshReferences[bucketIndex] = ClientAPI.Render.UploadMesh(meshData);
		BucketMeshCapacityVertices[bucketIndex] = neededVertices;
	}

	private sealed class AutomationDebugSlotState { public readonly long[] EntityIDs = new long[AutomationDebugMaxLocomotives]; }

	private readonly struct SignalDebugSide
	{
		public readonly ulong EdgeHash;
		public readonly ulong ZoneID;
		public readonly bool OccupancyKnown;
		public readonly bool Occupied;
		public readonly int FaceAxis;
		public readonly int FaceSign;

		public SignalDebugSide(ulong edgeHash, ulong zoneID, bool occupancyKnown, bool occupied, int faceAxis, int faceSign)
		{
			EdgeHash = edgeHash;
			ZoneID = zoneID;
			OccupancyKnown = occupancyKnown;
			Occupied = occupied;
			FaceAxis = faceAxis;
			FaceSign = faceSign;
		}
	}

	private readonly record struct SignalHeadsUpDisplayKey(int X, int Y, int Z, int Dimension);

	private readonly struct Line
	{
		public readonly Vec3d A;
		public readonly Vec3d B;

		public Line(Vec3d a, Vec3d b)
		{
			A = a;
			B = b;
		}
	}
}

[Flags]
public enum AutomationDebugFeatures : byte
{
	None = 0,
	Routes = 1,
	SignalOccupants = 2
}

[ProtoContract]
public sealed class AutomationDebugToggle
{
	[ProtoMember(1)] public AutomationDebugFeatures Features;
}

[ProtoContract]
public sealed class AutomationDebugUpdate
{
	[ProtoMember(1)] public AutomationDebugLocomotive[] Locomotives = Array.Empty<AutomationDebugLocomotive>();
	[ProtoMember(2)] public AutomationDebugSignalEdgeState[] SignalEdges = Array.Empty<AutomationDebugSignalEdgeState>();
	[ProtoMember(3)] public AutomationDebugOwnerPosition[] OwnerPositions = Array.Empty<AutomationDebugOwnerPosition>();
	[ProtoMember(4)] public ulong OccupancySerial;
	[ProtoMember(5)] public int GraphBuildVersion;
}

[ProtoContract]
public sealed class AutomationDebugLocomotive
{
	[ProtoMember(1)] public long EntityID;
	[ProtoMember(2)] public long OccupancyOwnerID;
	[ProtoMember(3)] public int Slot;

	[ProtoMember(4)] public double X;
	[ProtoMember(5)] public double Y;
	[ProtoMember(6)] public double Z;
	[ProtoMember(7)] public float Yaw;

	[ProtoMember(8)] public double VehicleLength;
	[ProtoMember(9)] public double BodyOffsetForward;

	[ProtoMember(10)] public bool HasRoute;
	[ProtoMember(11)] public bool PathTruncated;
	[ProtoMember(12)] public int[] PathCoordinates16 = Array.Empty<int>();

	[ProtoMember(13)] public string StatusText = "";

	[ProtoMember(14)] public double HalfWidth = 1.0;
	[ProtoMember(15)] public double Height = 3.0;
	[ProtoMember(16)] public double BottomOffset = -0.15;
}

[ProtoContract]
public sealed class AutomationDebugSignalEdgeState
{
	[ProtoMember(1)] public ulong EdgeHash;
	[ProtoMember(2)] public ulong ZoneID;
	[ProtoMember(3)] public bool Occupied;
	[ProtoMember(4)] public long[] OwnerIDs = Array.Empty<long>();
}

[ProtoContract]
public sealed class AutomationDebugOwnerPosition
{
	[ProtoMember(1)] public long OwnerID;
	[ProtoMember(2)] public double X;
	[ProtoMember(3)] public double Y;
	[ProtoMember(4)] public double Z;
	[ProtoMember(5)] public bool Virtual;
}

