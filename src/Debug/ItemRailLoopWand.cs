using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using CollisionFlowFields;

namespace YangTransport;

// Debug tool for testing. Too inefficient for real use. When Yangfinding 2 releases I'll properly add rail building locusts.
public sealed class ItemRailLoopWand : Item
{
	private enum WandToolMode : byte
	{
		StandardGaugeLoop = 0,
		WagonwayLoop = 1,
		Connection = 2
	}

	private const int HighlightIDBase = 934260;
	private const int MaximumAStarVisits = 120000;
	private const int MaximumStartEndpoints = 8;
	private const int MaximumTargetEndpoints = 32;
	private const int ClearanceHeightBlocks = 4;

	// Adjust based on how far we can push the server's CPU prior to it cooking itself
	private const int SearchTickIntervalMS = 24;
	private const int SearchNodesPerTick = 512;

	private const int PlaceBlocksPerTick = 16;
	private const double PenaltyWagonwayReservedNeighbour = 80.0;
	private const double PenaltyWagonwayCurrentNeighbour = 35.0;
	private const double PenaltyWagonwayCurve = 4.0;
	private const double PenaltyWagonwaySnake = 6.0;
	private const double PenaltyWagonwayRepeatedCurve = 10.0;
	private const double PenaltyWagonwayVerticalPerBlock = 1.0;
	private const double MaximumWagonwayVerticalPenalty = 3.0;
	private const double PenaltyStandardGaugeNonStraight = 0.5;
	private const double PenaltyStandardGaugeSharpSlope = 28.0;
	private const double PenaltyStandardGaugeSwerve = 28.0;
	private const double PenaltyStandardGaugeSharpTurn = 14.0;
	private const double PenaltyStandardGaugeGentleSlope = 10.0;
	private const double PenaltyStandardGaugeWideTurn = 4.0;
	private const double PenaltyStandardGaugeRepeatedTurn = 12.0;
	private const double PenaltyStandardGaugeTurnBacktrack = 18.0;
	private const double PenaltyVerticalMotionPerBlock = 4.0;
	private const double PenaltyVerticalTowardGoalFactor = 0.20;
	private const double PenaltyVerticalNeutralGoalFactor = 0.75;
	private const double PenaltyVerticalAwayGoalFactor = 1.50;
	private const double PenaltyVerticalDirectionFlip = 20.0;

	// Greedy dials
	private const double SearchHeuristicWeightBase = 1.5;
	private const double MaximumRoughSearchHeuristicWeight = 6;
	private const double SearchHeuristicRoughnessScale = 6.0;

	// Affects fanning
	private const double PriorityPenaltyMovingAwayFromGoal = 3.0;
	private const double PriorityPenaltyLowGoalProgress = 0.5;
	private const double LowGoalProgressThreshold = 0.35;

	private const double PenaltySideOccupiedBlock = 1.0;
	private const double PenaltySideOwnTrack = 2.0;
	private const bool DebugVisualizeSearch = true;
	private const int DebugVisualVisitSamplingInterval = 8;
	private const int DebugVisualMaximumBuckets = 2048;
	private const int DebugVisualBucketSize = 4;
	private const int DebugVisualFlushMS = 250;
	private const int DebugVisitedHighlightSlot = HighlightIDBase + 10;
	private const int DebugBestPathHighlightSlot = HighlightIDBase + 11;
	private const int DebugReverseBestPathHighlightSlot = HighlightIDBase + 12;
	private const int DebugGoalHighlightSlot = HighlightIDBase + 13;
	private const byte DirNorth = 0, DirEast = 1, DirSouth = 2, DirWest = 3, DirNone = 255;

	private static readonly Dictionary<string, WandState> States = new();
	private static readonly Dictionary<string, SegmentCache> SegmentCaches = new();
	private static readonly Dictionary<string, IRailGenerationJob> ActiveJobs = new();
	private static ICoreServerAPI? ServerAPI;
	private static long ServerTickListenerID;

	private SkillItem[] ToolModes = Array.Empty<SkillItem>();

	public override void OnLoaded(ICoreAPI clientAPI)
	{
		base.OnLoaded(clientAPI);
		ICoreClientAPI capi = clientAPI as ICoreClientAPI;

		ToolModes = new[]
		{
			new SkillItem { Code = new AssetLocation("standardgauge"),	Name = "Standard gauge loop" },
			new SkillItem { Code = new AssetLocation("wagonway"),		Name = "Wagonway loop" },
			new SkillItem { Code = new AssetLocation("connection"),		Name = "Connect rails" }
		};

		if (capi != null)
		{
			ToolModes[0].WithIcon(capi, "wpHome");
			ToolModes[1].WithIcon(capi, "wpCave");
			ToolModes[2].WithIcon(capi, "wpBee");
		}

		if (clientAPI.Side == EnumAppSide.Server && clientAPI is ICoreServerAPI serverAPI) { ServerAPI = serverAPI; }
	}

	public override void OnUnloaded(ICoreAPI coreAPI)
	{
		if (coreAPI.Side == EnumAppSide.Server)
		{
			if (ServerTickListenerID != 0)
			{
				coreAPI.Event.UnregisterGameTickListener(ServerTickListenerID);
				ServerTickListenerID = 0;
			}

			foreach (IRailGenerationJob job in ActiveJobs.Values) job.ClearDebugVisuals();
			ServerAPI = null;
			ActiveJobs.Clear();
		}

		for (int index = 0; index < ToolModes.Length; index++) { ToolModes[index]?.Dispose(); }

		base.OnUnloaded(coreAPI);
	}

	public override void OnHeldAttackStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSelection, EntitySelection entitySelection, ref EnumHandHandling handling) 
	{
		handling = EnumHandHandling.PreventDefault;
		if (byEntity is not EntityPlayer playerEntity) return;

		WandState state = GetState(byEntity.World, playerEntity.PlayerUID);
		ResetSelectionState(state);

		if (byEntity.World.Side == EnumAppSide.Server) { CancelActiveJob(playerEntity.PlayerUID); }

		Highlight(byEntity.World, playerEntity, state);
		SayClient(byEntity, "Rail wand selection cleared.");
	}

	public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSelection, EntitySelection entitySelection, bool firstEvent, ref EnumHandHandling handling)
	{
		handling = EnumHandHandling.PreventDefault;
		if (!firstEvent || blockSelection?.Position == null || byEntity is not EntityPlayer playerEntity) return;

		WandState state = GetState(byEntity.World, playerEntity.PlayerUID);
		WandToolMode selectedMode = GetSelectedMode(slot);
		if (state.Mode != selectedMode)
		{
			ResetSelectionState(state);
			if (byEntity.World.Side == EnumAppSide.Server) CancelActiveJob(playerEntity.PlayerUID);
		}
		state.Mode = selectedMode;

		if (selectedMode == WandToolMode.Connection) { HandleConnectionInteract(byEntity, playerEntity, blockSelection, state); return; }

		byte selectedGauge = LoopModeGauge(selectedMode);
		if (state.Gauge != selectedGauge)
		{
			ResetSelectionState(state);
			if (byEntity.World.Side == EnumAppSide.Server) CancelActiveJob(playerEntity.PlayerUID);
		}
		state.Gauge = selectedGauge;
		EnsureCache(byEntity.World, state.Gauge);

		BlockPos target = blockSelection.Position.UpCopy();
		if (state.Points.Count > 0 && state.Points[0].dimension != target.dimension) { state.Points.Clear(); }

		if (state.Points.Count >= 3 && SamePos(target, state.Points[state.Points.Count - 1]))
		{
			if (byEntity.World.Side == EnumAppSide.Server)
			{
				string error;
				if (StartLoopJob(byEntity.World, playerEntity, state, out error))
				{
					SayServer(byEntity, $"{GaugeName(state.Gauge)} rail loop search started: {state.Points.Count} points. Limited to {SearchNodesPerTick} nodes per tick.");
				}
				else { SayServer(byEntity, $"Rail loop failed: {error}"); }

				state.Points.Clear();
			}
			else { state.IsGenerating = true; }

			Highlight(byEntity.World, playerEntity, state);
			return;
		}

		state.IsGenerating = false;
		state.Points.Add(target.Copy());
		Highlight(byEntity.World, playerEntity, state);
		SayClient(byEntity, $"Rail Loop Wand point {state.Points.Count}: {target.X},{target.Y},{target.Z} ({GaugeName(state.Gauge)}). Right-click it again with at least three points to generate. Press [F] to change rail type.");
	}

	public override void OnHeldRenderOpaque(ItemSlot inSlot, IClientPlayer byPlayer)
	{
		if (api?.Side != EnumAppSide.Client) return;
		WandState state = GetState(api.World, byPlayer.PlayerUID);
		WandToolMode selectedMode = GetSelectedMode(inSlot);
		if (state.Mode != selectedMode) ResetSelectionState(state);
		state.Mode = selectedMode;

		if (selectedMode != WandToolMode.Connection)
		{
			state.Gauge = LoopModeGauge(selectedMode);
			EnsureCache(api.World, state.Gauge);
		}

		Highlight(api.World, byPlayer.Entity, state);
		if (api is not ICoreClientAPI clientAPI) return;
		if (selectedMode == WandToolMode.Connection) DrawConnectionLine(clientAPI, state);
		else DrawPointLoop(clientAPI, state);
	}

	public override SkillItem[] GetToolModes(ItemSlot slot, IClientPlayer forPlayer, BlockSelection blockSelection) { return ToolModes; }

	public override int GetToolMode(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSelection)
	{
		int toolMode = slot?.Itemstack?.Attributes.GetInt("toolMode", 0) ?? 0;
		return GameMath.Clamp(toolMode, 0, Math.Max(0, ToolModes.Length - 1));
	}

	public override void SetToolMode(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSelection, int toolMode)
	{
		toolMode = GameMath.Clamp(toolMode, 0, Math.Max(0, ToolModes.Length - 1));
		slot.Itemstack.Attributes.SetInt("toolMode", toolMode);

		if (byPlayer?.Entity?.World == null) return;

		WandState state = GetState(byPlayer.Entity.World, byPlayer.PlayerUID);
		state.Mode = ToolModeFromIndex(toolMode);
		if (state.Mode != WandToolMode.Connection) state.Gauge = LoopModeGauge(state.Mode);
		ResetSelectionState(state);

		if (byPlayer.Entity.World.Side == EnumAppSide.Server) { CancelActiveJob(byPlayer.PlayerUID); }

		Highlight(byPlayer.Entity.World, byPlayer.Entity, state);
	}

	public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot)
	{
		return new WorldInteraction[]
		{
			new WorldInteraction
			{
				ActionLangCode = "Change tool mode",
				HotKeyCodes = new[] { "toolmodeselect" },
				MouseButton = EnumMouseButton.None
			}
		};
	}

	private static void CancelActiveJob(string playerUID)
	{
		if (!ActiveJobs.TryGetValue(playerUID, out IRailGenerationJob? job)) return;
		job.ClearDebugVisuals();
		ActiveJobs.Remove(playerUID);
		UnregisterServerTickIfIdle();
	}

	private static bool StartLoopJob(IWorldAccessor world, EntityPlayer playerEntity, WandState state, out string error)
	{
		error = "";
		if (world.Side != EnumAppSide.Server) { error = "Client not allowed to act as server!"; return false; }
		if (state.Points.Count < 3) { error = "Need at least 3 points for a functional loop, add more!"; return false; }
		IPlayer? player = world.PlayerByUid(playerEntity.PlayerUID); if (player == null) { error = "Player somehow does not exist!!!"; return false; }

		List<BlockPos> points = new();
		for (int index = 0; index < state.Points.Count; index++) points.Add(state.Points[index].Copy());

		if (!LoopGenerationJob.TryCreate(world, playerEntity.PlayerUID, state.Gauge, points, out LoopGenerationJob? job, out error)) return false;

		CancelActiveJob(playerEntity.PlayerUID);
		ActiveJobs[playerEntity.PlayerUID] = job;
		EnsureServerTickRegistered();
		return true;
	}

	private static void EnsureServerTickRegistered()
	{
		if (ServerAPI == null || ServerTickListenerID != 0) return;
		ServerTickListenerID = ServerAPI.Event.RegisterGameTickListener(OnServerTick, SearchTickIntervalMS);
	}

	private static void UnregisterServerTickIfIdle()
	{
		if (ServerAPI == null || ServerTickListenerID == 0 || ActiveJobs.Count != 0) return;
		ServerAPI.Event.UnregisterGameTickListener(ServerTickListenerID);
		ServerTickListenerID = 0;
 	}

	private static void OnServerTick(float dt)
	{
		if (ServerAPI == null || ActiveJobs.Count == 0) return;

		string[] keys = ActiveJobs.Keys.ToArray();
		for (int index = 0; index < keys.Length; index++)
		{
			string playerUID = keys[index];
			if (!ActiveJobs.TryGetValue(playerUID, out IRailGenerationJob? job)) continue;

			job.Step(SearchNodesPerTick);

			if (!job.Done)
			{
				job.FlushDebugVisuals();
				if (job.TryGetProgressMessage(out string? progress) && ServerAPI.World.PlayerByUid(playerUID) is IServerPlayer progressPlayer) { progressPlayer.SendMessage(0, progress, EnumChatType.Notification); }
				continue;
			}

			job.ClearDebugVisuals();
			ActiveJobs.Remove(playerUID);
			if (ServerAPI.World.PlayerByUid(playerUID) is IServerPlayer serverPlayer) { serverPlayer.SendMessage(0, job.Message, EnumChatType.Notification); }
		}
		
		UnregisterServerTickIfIdle();
	}

	private interface IRailGenerationJob
	{
		bool Done { get; }
		string Message { get; }
		void Step(int nodeBudget);
		bool TryGetProgressMessage(out string? message);
		void FlushDebugVisuals();
		void ClearDebugVisuals();
	}

	private sealed class SearchDebugVisualization
	{
		private readonly HashSet<CellKey> ExploredBuckets = new();
		private readonly List<BlockPos> BucketBounds = new();
		private readonly List<int> BucketColors = new();
		private readonly List<BlockPos> BestPath = new();
		private readonly List<int> BestPathColors = new();
		private readonly List<BlockPos> ReverseBestPath = new();
		private readonly List<int> ReverseBestPathColors = new();
		private readonly List<BlockPos> GoalBlocks = new();
		private readonly List<int> GoalColors = new();
		private long NextFlushMS;
		private bool IsDirty;

		public void Visit(Endpoint endpoint, int visitCount)
		{
			if ((visitCount % DebugVisualVisitSamplingInterval) != 0) return;
			if (ExploredBuckets.Count >= DebugVisualMaximumBuckets) return;

			CellKey bucket = ToBucket(endpoint);
			if (ExploredBuckets.Add(bucket)) IsDirty = true;
		}

		public void SetBest(Route? forwardRoute, Route? reverseRoute)
		{
			BestPath.Clear();
			BestPathColors.Clear();
			ReverseBestPath.Clear();
			ReverseBestPathColors.Clear();

			AddRoute(BestPath, BestPathColors, forwardRoute, HighlightColor(160, 0, 255, 0));
			AddRoute(ReverseBestPath, ReverseBestPathColors, reverseRoute, HighlightColor(105, 70, 140, 255));

			IsDirty = true;
		}

		private static void AddRoute(List<BlockPos> blocks, List<int> colors, Route? route, int color)
		{
			if (route == null) return;

			if (route.Placements.Count > 0 && route.Placements[0].Segment.Gauge == 1)
			{
				HashSet<CellKey> seen = new();
				for (int index = 0; index < route.Placements.Count; index++)
				{
					Placement placement = route.Placements[index];
					foreach (CellKey cell in FootprintCells(placement.Segment, placement.Position))
					{
						if (!seen.Add(cell)) continue;
						blocks.Add(new BlockPos(cell.X, cell.Y, cell.Z, cell.Dimension));
						colors.Add(color);
					}
				}
				return;
			}

			for (int index = 0; index < route.DebugEndpoints.Count; index++)
			{
				blocks.Add(EndpointBlockPos(route.DebugEndpoints[index]));
				colors.Add(color);
			}
		}

		private static int HighlightColor(int a, int r, int g, int b) { return ColorUtil.ToRgba(a, b, g, r); }

		public void SetGoals(List<GoalSpec> goals)
		{
			GoalBlocks.Clear();
			GoalColors.Clear();

			for (int index = 0; index < goals.Count; index++)
			{
				GoalBlocks.Add(EndpointBlockPos(goals[index].Endpoint));
				GoalColors.Add(goals[index].RequiredArrivalDirection == DirNone ? HighlightColor(160, 255, 255, 0) : HighlightColor(220, 255, 80, 0));
			}

			IsDirty = true;
		}

		public bool WantsFlush(IWorldAccessor world) => IsDirty && world.ElapsedMilliseconds >= NextFlushMS;

		public void Flush(IWorldAccessor world, IPlayer player, bool force)
		{
			long now = world.ElapsedMilliseconds;
			if (!force && (!IsDirty || now < NextFlushMS)) return;
			NextFlushMS = now + DebugVisualFlushMS;
			IsDirty = false;

			BucketBounds.Clear();
			BucketColors.Clear();

			foreach (CellKey bucket in ExploredBuckets)
			{
				int x = bucket.X * DebugVisualBucketSize;
				int y = bucket.Y * DebugVisualBucketSize;
				int z = bucket.Z * DebugVisualBucketSize;
				BucketBounds.Add(new BlockPos(x, y, z, bucket.Dimension));
				BucketBounds.Add(new BlockPos(x + DebugVisualBucketSize, y + DebugVisualBucketSize, z + DebugVisualBucketSize, bucket.Dimension));
				BucketColors.Add(HighlightColor(55, 255, 170, 0));
			}

			world.HighlightBlocks(player, DebugVisitedHighlightSlot, BucketBounds, BucketColors, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cubes);
			world.HighlightBlocks(player, DebugBestPathHighlightSlot, BestPath, BestPathColors, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
			world.HighlightBlocks(player, DebugReverseBestPathHighlightSlot, ReverseBestPath, ReverseBestPathColors, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
			world.HighlightBlocks(player, DebugGoalHighlightSlot, GoalBlocks, GoalColors, EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube);
		}

		public void Clear(IWorldAccessor world, IPlayer player)
		{
			world.HighlightBlocks(player, DebugVisitedHighlightSlot, new List<BlockPos>(), new List<int>(), EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cubes);
			world.HighlightBlocks(player, DebugBestPathHighlightSlot, new List<BlockPos>(), new List<int>(), EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
			world.HighlightBlocks(player, DebugReverseBestPathHighlightSlot, new List<BlockPos>(), new List<int>(), EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Arbitrary);
			world.HighlightBlocks(player, DebugGoalHighlightSlot, new List<BlockPos>(), new List<int>(), EnumHighlightBlocksMode.Absolute, EnumHighlightShape.Cube);
		}

		private static CellKey ToBucket(Endpoint endpoint)
		{
			int bx = FloorDiv(FloorDiv(endpoint.X, 16), DebugVisualBucketSize);
			int by = FloorDiv(FloorDiv(endpoint.Y, 16), DebugVisualBucketSize);
			int bz = FloorDiv(FloorDiv(endpoint.Z, 16), DebugVisualBucketSize);
			return new CellKey(bx, by, bz, endpoint.Dimension);
		}
	}

	private sealed class LoopGenerationJob : IRailGenerationJob
	{
		private readonly IWorldAccessor World;
		private readonly string PlayerUID;
		private readonly byte Gauge;
		private readonly SegmentCache CachedSegments;
		private readonly SearchBounds SearchBounds;
		private readonly List<Endpoint>[] TargetEndpoints;
		private readonly int StartCandidateLimit;

		private int StartCandidateIndex;
		private int LegIndex;
		private bool IsClosingLoop;
		private Endpoint StartEndpoint;
		private NodeKey CurrentNode;
		private Route TotalRoute = new();
		private Dictionary<BlockPos, Block> ReservedBlocks = new();
		private Dictionary<CellKey, CellClaim> ReservedCells = new();
		private RouteSearch? ActiveSearch;
		private int TotalNodeVisits;
		private long NextProgressMS;
		private List<Placement>? PlannedPlacements;
		private int PlacementIndex;
		private int PlacedBlockCount;
		private readonly SearchDebugVisualization? DebugVisualization = DebugVisualizeSearch ? new SearchDebugVisualization() : null;

		public bool Done { get; private set; }
		public string Message { get; private set; } = "";

		private LoopGenerationJob(IWorldAccessor world, string playerUID, byte gauge, SegmentCache segmentCache, SearchBounds searchBounds, List<Endpoint>[] targetEndpoints, int startCandidateLimit)
		{
			this.World = world;
			this.PlayerUID = playerUID;
			this.Gauge = gauge;
			this.CachedSegments = segmentCache;
			this.SearchBounds = searchBounds;
			this.TargetEndpoints = targetEndpoints;
			this.StartCandidateLimit = startCandidateLimit;
		}

		public static bool TryCreate(IWorldAccessor world, string playerUID, byte gauge, List<BlockPos> points, out LoopGenerationJob? job, out string error)
		{
			job = null;
			error = "";

			SegmentCache segmentCache = EnsureCache(world, gauge);
			if (segmentCache.Segments.Count == 0) { error = $"No usable {GaugeName(gauge)} track segments found!"; return false; }

			SearchBounds searchBounds = SearchBounds.FromPoints(points, segmentCache);
			List<Endpoint>[] targetEndpoints = new List<Endpoint>[points.Count];

			for (int index = 0; index < targetEndpoints.Length; index++)
			{
				targetEndpoints[index] = CollectTargetEndpoints(world, points[index], segmentCache, searchBounds);
				if (targetEndpoints[index].Count == 0)
				{
					error = $"No valid rail endpoint near point {index + 1}.";
					return false;
				}
			}

			int startCandidateLimit = Math.Min(targetEndpoints[0].Count, MaximumStartEndpoints);
			if (startCandidateLimit <= 0) { error = "No valid start endpoints."; return false; }

			job = new LoopGenerationJob(world, playerUID, gauge, segmentCache, searchBounds, targetEndpoints, startCandidateLimit);
			job.BeginStartCandidate();
			return true;
		}

		public bool TryGetProgressMessage(out string? message)
		{
			message = null;
			long now = World.ElapsedMilliseconds;
			if (now < NextProgressMS) return false;
			NextProgressMS = now + 2000;

			if (PlannedPlacements != null)
			{
				message = $"Rail loop placing... {PlacementIndex}/{PlannedPlacements.Count} planned blocks placed in batches of {PlaceBlocksPerTick}.";
				return true;
			}

			string phase = IsClosingLoop ? "closing loop" : $"leg {LegIndex}/{TargetEndpoints.Length - 1}";
			message = $"Rail loop searching... start {Math.Min(StartCandidateIndex, StartCandidateLimit)}/{StartCandidateLimit}, {phase}, {TotalNodeVisits} node visits.";
			return true;
		}

		public void Step(int nodeBudget)
		{
			if (Done) return;
			if (PlannedPlacements != null) { StepPlacement(PlaceBlocksPerTick); return; }
			if (ActiveSearch == null) { BeginCurrentSearch(); if (Done) return; }

			SearchStatus status = ActiveSearch!.Step(nodeBudget);
			TotalNodeVisits += ActiveSearch.LastStepVisitCount;

			if (status == SearchStatus.Running) return;
			if (status == SearchStatus.Failed || ActiveSearch.Result == null) { BeginStartCandidate(); return; }

			Route leg = ActiveSearch.Result;
			ActiveSearch = null;
			if (!ReservePlacements(leg, ReservedBlocks, ReservedCells)) { 	BeginStartCandidate(); return; }

			TotalRoute.Append(leg);
			CurrentNode = leg.EndNode;

			if (!IsClosingLoop)
			{
				if (LegIndex < TargetEndpoints.Length - 1) { LegIndex++; return; }
				IsClosingLoop = true; return;
			}
			if (Gauge == 1 && TotalRoute.FirstStartDirection != DirNone && TotalRoute.LastEndDirection != DirNone && TotalRoute.FirstStartDirection != TotalRoute.LastEndDirection) { BeginStartCandidate(); return; }

			FinishWithRoute();
		}

		private void BeginStartCandidate()
		{
			ActiveSearch = null;
			IsClosingLoop = false;
			LegIndex = 1;
			TotalRoute = new Route();
			ReservedBlocks = new Dictionary<BlockPos, Block>();
			ReservedCells = new Dictionary<CellKey, CellClaim>();

			if (StartCandidateIndex >= StartCandidateLimit)
			{
				Done = true;
				Message = $"Rail loop failed: Could not find a closed route between all points after {TotalNodeVisits} node visits.";
				return;
			}

			StartEndpoint = TargetEndpoints[0][StartCandidateIndex++];
			CurrentNode = new NodeKey(StartEndpoint, DirNone);
		}

		private void BeginCurrentSearch()
		{
			if (Done) return;

			List<GoalSpec> goals;
			if (IsClosingLoop)
			{
				byte requiredArrivalDirection = Gauge == 1 ? TotalRoute.FirstStartDirection : DirNone;
				goals = new List<GoalSpec> { new GoalSpec(StartEndpoint, requiredArrivalDirection) };
			}
			else { goals = TargetEndpoints[LegIndex].Select(endpoint => new GoalSpec(endpoint, DirNone)).ToList(); }

			DebugVisualization?.SetGoals(goals);
			ActiveSearch = new RouteSearch(World, CachedSegments, SearchBounds, CurrentNode, goals, ReservedBlocks, ReservedCells, DebugVisualization);
		}

		private void FinishWithRoute()
		{
			if (!BuildPlan(TotalRoute, out List<Placement> plan, out string error))
			{
				Done = true;
				Message = $"Rail loop failed: {error}";
				return;
			}

			IPlayer? player = World.PlayerByUid(PlayerUID);
			if (player == null)
			{
				Done = true;
				Message = "Rail loop failed: Player somehow unavailable.";
				return;
			}

			IBlockAccessor blockAccessor = World.BlockAccessor;
			for (int index = 0; index < plan.Count; index++)
			{
				Placement placement = plan[index];
				Block currentBlock = blockAccessor.GetBlock(placement.Position);
				if (currentBlock.BlockId == placement.Block.BlockId) continue;

				string failure = "";
				if (!IsSegmentClear(World, placement.Segment, placement.Position))
				{ Done = true; Message = $"Rail loop failed: Blocked clearance or missing support at {placement.Position.X},{placement.Position.Y},{placement.Position.Z}."; return; }
				
				if (!CanPlaceBlockIgnoringFlora(World, player, placement, ref failure))
				{ Done = true; Message = $"Rail loop failed: {failure} at {placement.Position.X},{placement.Position.Y},{placement.Position.Z}."; return; }
			}

			PlannedPlacements = plan;
			PlacementIndex = 0;
			PlacedBlockCount = 0;
		}

		public void FlushDebugVisuals()
		{
			if (DebugVisualization == null || !DebugVisualization.WantsFlush(World)) return;
			if (ActiveSearch != null) DebugVisualization.SetBest(ActiveSearch.GetBestRoute(), ActiveSearch.GetBestReverseRoute());
			if (World.PlayerByUid(PlayerUID) is IPlayer player) { DebugVisualization.Flush(World, player, force: false); }
		}

		public void ClearDebugVisuals()
		{
			if (DebugVisualization == null) return;
			if (World.PlayerByUid(PlayerUID) is IPlayer player) { DebugVisualization.Clear(World, player); }
		}

		private void StepPlacement(int blockBudget)
		{
			if (PlannedPlacements == null) return;

			IPlayer? player = World.PlayerByUid(PlayerUID);
			if (player == null) { Done = true; Message = "Rail loop failed: Player somehow unavailable."; return; }

			IBlockAccessor blockAccessor = World.BlockAccessor;
			int worked = 0;

			while (PlacementIndex < PlannedPlacements.Count && worked < blockBudget)
			{
				Placement placement = PlannedPlacements[PlacementIndex++];
				if (blockAccessor.GetBlock(placement.Position).BlockId == placement.Block.BlockId) continue;

				string failure = "";
				RemoveIgnoredFloraForSegment(World, placement.Segment, placement.Position);
				BlockSelection blockSelection = MakeSelection(blockAccessor, placement.Position);

				if (!IsSegmentClear(World, placement.Segment, placement.Position))
				{
					Done = true;
					Message = $"Rail loop failed during placement after placing {PlacedBlockCount} blocks: blocked clearance or missing support at {placement.Position.X},{placement.Position.Y},{placement.Position.Z}.";
					return;
				}
				if (!CanPlaceBlockIgnoringFlora(World, player, placement, ref failure))
				{
					Done = true;
					Message = $"Rail loop failed during placement after placing {PlacedBlockCount} blocks: {failure} at {placement.Position.X},{placement.Position.Y},{placement.Position.Z}.";
					return;
				}

				if (!placement.Block.DoPlaceBlock(World, player, blockSelection, new ItemStack(placement.Block)))
				{
					Done = true;
					Message = $"Rail loop failed during placement after placing {PlacedBlockCount} blocks: DoPlaceBlock returned false; planned={DescribeBlock(placement.Block)} at {placement.Position.X},{placement.Position.Y},{placement.Position.Z}.";
					return;
				}

				SpawnFlowFieldColliders(World, placement.Segment, placement.Position);
				PlacedBlockCount++;
				worked++;
			}

			if (PlacementIndex < PlannedPlacements.Count) return;

			Done = true;
			Message = PlacedBlockCount > 0
				? $"Rail loop complete: placed {PlacedBlockCount} {GaugeName(Gauge)} blocks after {TotalNodeVisits} node visits."
				: "Rail loop failed: Route produced no new placements.";
		}

	}

	private enum SearchStatus
	{
		Running,
		Found,
		Failed
	}

	private sealed class RouteSearch
	{
		private readonly IWorldAccessor World;
		private readonly SegmentCache CachedSegments;
		private readonly SearchBounds SearchBounds;
		private readonly NodeKey StartNode;
		private readonly List<GoalSpec> GoalSpecifications;
		private readonly List<Endpoint> GoalEndpoints;
		private readonly List<Endpoint> ReverseGoalEndpoints;
		private readonly HashSet<Endpoint> GoalEndpointSet;
		private readonly Dictionary<BlockPos, Block>? ReservedBlocks;
		private readonly Dictionary<CellKey, CellClaim>? ReservedCells;
		private readonly PriorityQueue<QueuedNode, double> ForwardOpenQueue = new();
		private readonly PriorityQueue<QueuedNode, double> ReverseOpenQueue = new();
		private readonly Dictionary<NodeKey, double> ForwardCostByNode = new();
		private readonly Dictionary<NodeKey, double> ReverseCostByNode = new();
		private readonly Dictionary<NodeKey, CameFrom> ForwardPredecessors = new();
		private readonly Dictionary<NodeKey, CameFrom> ReversePredecessors = new();
		private readonly Dictionary<Endpoint, List<NodeKey>> ForwardNodesByEndpoint = new();
		private readonly Dictionary<Endpoint, List<NodeKey>> ReverseNodesByEndpoint = new();
		private readonly Dictionary<PlacementKey, bool> ClearanceByPlacement = new();
		private readonly Dictionary<Endpoint, bool> NearbySupportByEndpoint = new();
		private readonly SearchDebugVisualization? DebugVisualization;
		private readonly bool MustMatchDirections;
		private readonly bool AllowExistingIdenticalAnchors;
		private readonly double ForwardHeuristicWeight;
		private readonly double ReverseHeuristicWeight;
		private NodeKey BestForwardNode;
		private double BestForwardHeuristic;
		private NodeKey BestReverseNode;
		private double BestReverseHeuristic;
		private bool HasBestReverseRoute;

		private int VisitedNodeCount;

		public Route? Result { get; private set; }
		public int LastStepVisitCount { get; private set; }

		public RouteSearch
		(
			IWorldAccessor world,
			SegmentCache segmentCache,
			SearchBounds searchBounds,
			NodeKey startNode,
			List<GoalSpec> goalSpecifications,
			Dictionary<BlockPos, Block>? reservedBlocks,
			Dictionary<CellKey, CellClaim>? reservedCells,
			SearchDebugVisualization? debugVisualization,
			bool enforceDirectionsForAllGauges = false,
			bool allowExistingIdenticalAnchors = true
		)
		{
			this.World = world;
			this.CachedSegments = segmentCache;
			this.SearchBounds = searchBounds;
			this.StartNode = startNode;
			this.GoalSpecifications = goalSpecifications;
			this.GoalEndpoints = goalSpecifications.Select(goal => goal.Endpoint).ToList();
			this.ReverseGoalEndpoints = new List<Endpoint> { startNode.Endpoint };
			this.ReservedBlocks = reservedBlocks;
			this.ReservedCells = reservedCells;
			this.DebugVisualization = debugVisualization;
			MustMatchDirections = segmentCache.Gauge == 1 || enforceDirectionsForAllGauges;
			this.AllowExistingIdenticalAnchors = allowExistingIdenticalAnchors;
			GoalEndpointSet = new HashSet<Endpoint>(this.GoalEndpoints);
			ForwardHeuristicWeight = ComputeHeuristicWeight(startNode.Endpoint, this.GoalEndpoints);
			ReverseHeuristicWeight = ForwardHeuristicWeight;

			BestForwardNode = startNode;
			BestForwardHeuristic = Heuristic(startNode.Endpoint, this.GoalEndpoints);
			BestReverseNode = default;
			BestReverseHeuristic = double.MaxValue;

			ForwardCostByNode[startNode] = 0;
			IndexNode(ForwardNodesByEndpoint, startNode);
			ForwardOpenQueue.Enqueue(new QueuedNode(startNode, 0), BestForwardHeuristic * ForwardHeuristicWeight);

			for (int index = 0; index < goalSpecifications.Count; index++)
			{
				GoalSpec goal = goalSpecifications[index];
				NodeKey reverseStart = new(goal.Endpoint, goal.RequiredArrivalDirection);
				if (ReverseCostByNode.ContainsKey(reverseStart)) continue;

				double heuristic = Heuristic(goal.Endpoint, ReverseGoalEndpoints);
				ReverseCostByNode[reverseStart] = 0;
				IndexNode(ReverseNodesByEndpoint, reverseStart);
				ReverseOpenQueue.Enqueue(new QueuedNode(reverseStart, 0), heuristic * ReverseHeuristicWeight);
			}
		}

		public SearchStatus Step(int nodeBudget)
		{
			LastStepVisitCount = 0;
			int queuePops = 0;

			while ((ForwardOpenQueue.Count > 0 || ReverseOpenQueue.Count > 0) && VisitedNodeCount < MaximumAStarVisits && queuePops < nodeBudget)
			{
				bool expandForward = ForwardOpenQueue.Count > 0 && (ReverseOpenQueue.Count == 0 || (queuePops & 1) == 0);
				queuePops++;

				if (expandForward) { if (StepForwardOne()) return SearchStatus.Found; }
				else { if (StepReverseOne()) return SearchStatus.Found; }
			}

			return (ForwardOpenQueue.Count == 0 && ReverseOpenQueue.Count == 0) || VisitedNodeCount >= MaximumAStarVisits ? SearchStatus.Failed : SearchStatus.Running;
		}

		public Route? GetBestRoute()
		{
			if (BestForwardNode.Equals(StartNode)) return null;
			if (!ForwardCostByNode.TryGetValue(BestForwardNode, out double cost)) cost = 0;
			return Reconstruct(ForwardPredecessors, StartNode, BestForwardNode, cost);
		}

		public Route? GetBestReverseRoute()
		{
			if (!HasBestReverseRoute) return null;
			if (!ReverseCostByNode.TryGetValue(BestReverseNode, out double cost)) return null;
			return ReconstructReverse(ReversePredecessors, BestReverseNode, cost);
		}

		private bool StepForwardOne()
		{
			if (ForwardOpenQueue.Count == 0) return false;

			QueuedNode queued = ForwardOpenQueue.Dequeue();
			NodeKey currentNode = queued.Node;
			if (!ForwardCostByNode.TryGetValue(currentNode, out double currentCost)) return false;
			if (Math.Abs(currentCost - queued.Cost) > 0.000001) return false;

			VisitedNodeCount++;
			LastStepVisitCount++;

			Endpoint currentEndpoint = currentNode.Endpoint;
			DebugVisualization?.Visit(currentEndpoint, VisitedNodeCount);

			double heuristic = Heuristic(currentEndpoint, GoalEndpoints);
			if (heuristic < BestForwardHeuristic)
			{
				BestForwardHeuristic = heuristic;
				BestForwardNode = currentNode;
			}

			if (IsGoal(currentNode)) { Result = Reconstruct(ForwardPredecessors, StartNode, currentNode, currentCost); return true; }
			if (TryConnect(currentNode)) { return true; }

			for (int index = 0; index < CachedSegments.Segments.Count; index++)
			{
				Segment segment = CachedSegments.Segments[index];

				ConsiderForward(currentNode, currentEndpoint, currentCost, segment, forward: true);
				ConsiderForward(currentNode, currentEndpoint, currentCost, segment, forward: false);
			}

			return false;
		}

		private bool StepReverseOne()
		{
			if (ReverseOpenQueue.Count == 0) return false;

			QueuedNode queued = ReverseOpenQueue.Dequeue();
			NodeKey currentNode = queued.Node;
			if (!ReverseCostByNode.TryGetValue(currentNode, out double currentCost)) return false;
			if (Math.Abs(currentCost - queued.Cost) > 0.000001) return false;

			VisitedNodeCount++;
			LastStepVisitCount++;

			Endpoint currentEndpoint = currentNode.Endpoint;
			DebugVisualization?.Visit(currentEndpoint, VisitedNodeCount);

			double heuristic = Heuristic(currentEndpoint, ReverseGoalEndpoints);
			if (ReversePredecessors.ContainsKey(currentNode) && (!HasBestReverseRoute || heuristic < BestReverseHeuristic))
			{
				BestReverseHeuristic = heuristic;
				BestReverseNode = currentNode;
				HasBestReverseRoute = true;
			}

			if (TryConnect(currentNode)) { return true; }

			for (int index = 0; index < CachedSegments.Segments.Count; index++)
			{
				Segment segment = CachedSegments.Segments[index];

				ConsiderReverse(currentNode, currentEndpoint, currentCost, segment, backwardForward: true);
				ConsiderReverse(currentNode, currentEndpoint, currentCost, segment, backwardForward: false);
			}

			return false;
		}

		private void ConsiderForward(NodeKey currentNode, Endpoint currentEndpoint, double currentCost, Segment segment, bool forward)
		{
			byte startDirection = segment.StartDir(forward);
			if (MustMatchDirections && currentNode.ArrivalDirection != DirNone && currentNode.ArrivalDirection != startDirection) return;

			if (!TryStep(currentEndpoint, segment, forward, out Endpoint nextEndpoint, out BlockPos anchor)) return; 
			if (!SearchBounds.Contains(nextEndpoint) || !SearchBounds.Contains(anchor)) return;

			if (ConflictsWithReserved(ReservedBlocks, anchor, segment.Block)) return;
			if (ConflictsWithReservedCells(ReservedCells, segment, anchor)) return;
			if (ConflictsWithCurrentRoute(ForwardPredecessors, StartNode, currentNode, segment, anchor)) return;

			PlacementKey key = new(segment.Block.BlockId, anchor);
			if (!ClearanceByPlacement.TryGetValue(key, out bool clear)) { clear = IsSegmentClear(World, segment, anchor, AllowExistingIdenticalAnchors); ClearanceByPlacement[key] = clear; }
			if (!clear) return;

			byte endDirection = segment.EndDir(forward);
			int deltaY = segment.DeltaY(forward);
			NodeKey nextNode = new(nextEndpoint, endDirection);

			if (!IsGoal(nextNode) && !HasAnyBuildSupportNear(nextEndpoint)) return;

			double tentative = currentCost
				+ segment.Cost
				+ SegmentPenalty(segment)
				+ GoalAwareVerticalPenalty(currentEndpoint, nextEndpoint, segment, GoalEndpoints)
				+ TransitionPenalty(World, CachedSegments, ForwardPredecessors, currentNode, segment, anchor, startDirection, endDirection, deltaY, ReservedCells, StartNode.Endpoint, GoalEndpointSet);
			if (ForwardCostByNode.TryGetValue(nextNode, out double old) && tentative >= old) return;

			double currentHeuristic = Heuristic(currentEndpoint, GoalEndpoints);
			double nextHeuristic = Heuristic(nextEndpoint, GoalEndpoints);

			ForwardCostByNode[nextNode] = tentative;
			ForwardPredecessors[nextNode] = new CameFrom(currentNode, segment, anchor, startDirection, endDirection, deltaY);
			IndexNode(ForwardNodesByEndpoint, nextNode);
			ForwardOpenQueue.Enqueue(new QueuedNode(nextNode, tentative), tentative + nextHeuristic * ForwardHeuristicWeight + GoalProgressPriorityPenalty(currentHeuristic, nextHeuristic));
		}

		private void ConsiderReverse(NodeKey curNode, Endpoint currentNode, double currentEndpoint, Segment segmentv, bool backwardForward)
		{
			// Walk the segment backwards, but store the placement in the final forward direction.
			bool pathForward = !backwardForward;
			byte pathEndDirection = segmentv.EndDir(pathForward);
			if (MustMatchDirections && curNode.ArrivalDirection != DirNone && curNode.ArrivalDirection != pathEndDirection) return;

			if (!TryStep(currentNode, segmentv, backwardForward, out Endpoint nextEndpoint, out BlockPos anchor)) return; 
			if (!SearchBounds.Contains(nextEndpoint) || !SearchBounds.Contains(anchor)) return;

			if (ConflictsWithReserved(ReservedBlocks, anchor, segmentv.Block)) return;
			if (ConflictsWithReservedCells(ReservedCells, segmentv, anchor)) return;
			if (ConflictsWithCurrentRoute(ReversePredecessors, StartNode, curNode, segmentv, anchor)) return;

			PlacementKey key = new(segmentv.Block.BlockId, anchor);
			if (!ClearanceByPlacement.TryGetValue(key, out bool clear)) { clear = IsSegmentClear(World, segmentv, anchor, AllowExistingIdenticalAnchors); ClearanceByPlacement[key] = clear; }
			if (!clear) return;

			byte pathStartDirection = segmentv.StartDir(pathForward);
			int deltaY = segmentv.DeltaY(pathForward);
			NodeKey nextNode = new(nextEndpoint, pathStartDirection);

			if (!nextEndpoint.Equals(StartNode.Endpoint) && !HasAnyBuildSupportNear(nextEndpoint)) return;

			double tentative = currentEndpoint
				+ segmentv.Cost
				+ SegmentPenalty(segmentv)
				+ GoalAwareVerticalPenalty(currentNode, nextEndpoint, segmentv, ReverseGoalEndpoints)
				+ TransitionPenalty(World, CachedSegments, ReversePredecessors, curNode, segmentv, anchor, pathStartDirection, pathEndDirection, deltaY, ReservedCells, StartNode.Endpoint, GoalEndpointSet);
			if (ReverseCostByNode.TryGetValue(nextNode, out double old) && tentative >= old) return;

			double currentHeuristic = Heuristic(currentNode, ReverseGoalEndpoints);
			double nextHeuristic = Heuristic(nextEndpoint, ReverseGoalEndpoints);

			ReverseCostByNode[nextNode] = tentative;
			ReversePredecessors[nextNode] = new CameFrom(curNode, segmentv, anchor, pathStartDirection, pathEndDirection, deltaY);
			IndexNode(ReverseNodesByEndpoint, nextNode);
			ReverseOpenQueue.Enqueue(new QueuedNode(nextNode, tentative), tentative + nextHeuristic * ReverseHeuristicWeight + GoalProgressPriorityPenalty(currentHeuristic, nextHeuristic));
		}

		private bool TryConnect(NodeKey node)
		{
			if (ForwardCostByNode.ContainsKey(node)) { if (TryConnectFromForward(node)) return true; }
			if (ReverseCostByNode.ContainsKey(node)) { if (TryConnectFromReverse(node)) return true; }

			return false;
		}

		private bool TryConnectFromForward(NodeKey forwardNode)
		{
			if (!ReverseNodesByEndpoint.TryGetValue(forwardNode.Endpoint, out List<NodeKey>? reverseNodes)) return false;
			if (!ForwardCostByNode.TryGetValue(forwardNode, out double forwardCost)) return false;

			for (int index = 0; index < reverseNodes.Count; index++)
			{
				NodeKey reverseNode = reverseNodes[index];
				if (!CanJoin(forwardNode, reverseNode)) continue;
				if (!ReverseCostByNode.TryGetValue(reverseNode, out double reverseCost)) continue;

				if (TryBuildJoinedRoute(forwardNode, reverseNode, forwardCost, reverseCost, out Route? route)) { Result = route; return true; }
			}

			return false;
		}

		private bool TryConnectFromReverse(NodeKey reverseNode)
		{
			if (!ForwardNodesByEndpoint.TryGetValue(reverseNode.Endpoint, out List<NodeKey>? forwardNodes)) return false;
			if (!ReverseCostByNode.TryGetValue(reverseNode, out double reverseCost)) return false;

			for (int index = 0; index < forwardNodes.Count; index++)
			{
				NodeKey forwardNode = forwardNodes[index];
				if (!CanJoin(forwardNode, reverseNode)) continue;
				if (!ForwardCostByNode.TryGetValue(forwardNode, out double forwardCost)) continue;

				if (TryBuildJoinedRoute(forwardNode, reverseNode, forwardCost, reverseCost, out Route? route)) { Result = route; return true; }
			}

			return false;
		}

		private bool TryBuildJoinedRoute(NodeKey forwardNode, NodeKey reverseNode, double forwardCost, double reverseCost, out Route? route)
		{
			route = null;

			Route joined = new();
			joined.Append(Reconstruct(ForwardPredecessors, StartNode, forwardNode, forwardCost));
			joined.Append(ReconstructReverse(ReversePredecessors, reverseNode, reverseCost));

			Dictionary<BlockPos, Block> joinedReserved = new();
			Dictionary<CellKey, CellClaim> joinedReservedCells = new();
			if (!ReservePlacements(joined, joinedReserved, joinedReservedCells)) return false;

			route = joined;
			return true;
		}

		private bool CanJoin(NodeKey forwardNode, NodeKey reverseNode)
		{
			if (!forwardNode.Endpoint.Equals(reverseNode.Endpoint)) return false;
			if (!MustMatchDirections) return true;
			return forwardNode.ArrivalDirection == DirNone || reverseNode.ArrivalDirection == DirNone || forwardNode.ArrivalDirection == reverseNode.ArrivalDirection;
		}

		private void IndexNode(Dictionary<Endpoint, List<NodeKey>> index, NodeKey node)
		{
			if (!index.TryGetValue(node.Endpoint, out List<NodeKey>? nodes))
			{
				nodes = new List<NodeKey>();
				index[node.Endpoint] = nodes;
			}

			if (!nodes.Contains(node)) nodes.Add(node);
		}

		private bool IsGoal(NodeKey node)
		{
			for (int index = 0; index < GoalSpecifications.Count; index++) { if (GoalSpecifications[index].Matches(node)) return true; }
			return false;
		}

		private bool HasAnyBuildSupportNear(Endpoint endpoint)
		{
			if (NearbySupportByEndpoint.TryGetValue(endpoint, out bool cached)) return cached;

			int bx = FloorDiv(endpoint.X, 16);
			int by = FloorDiv(endpoint.Y, 16);
			int bz = FloorDiv(endpoint.Z, 16);
			int xzRadius = Math.Max(2, CachedSegments.HorizontalReach + 2);
			int yRadius = Math.Max(2, CachedSegments.VerticalReach + 2);

			for (int dx = -xzRadius; dx <= xzRadius; dx++)
			for (int dy = -yRadius; dy <= yRadius; dy++)
			for (int dz = -xzRadius; dz <= xzRadius; dz++)
			{
				BlockPos supportPosition = new(bx + dx, by + dy - 1, bz + dz, endpoint.Dimension);
				Block support = World.BlockAccessor.GetBlock(supportPosition);
				if (!IsIgnoredFlora(support) && support.SideSolid[BlockFacing.indexUP]) { NearbySupportByEndpoint[endpoint] = true; return true; }
			}

			NearbySupportByEndpoint[endpoint] = false;
			return false;
		}

	}

	private static bool TryStep(Endpoint currentEndpoint, Segment segment, bool forward, out Endpoint next, out BlockPos anchor) 
	{
		Endpoint from = forward ? segment.A : segment.B;
		Endpoint to = forward ? segment.B : segment.A;

		int ax16 = currentEndpoint.X - from.X;
		int ay16 = currentEndpoint.Y - from.Y;
		int az16 = currentEndpoint.Z - from.Z;

		if (ax16 % 16 != 0 || ay16 % 16 != 0 || az16 % 16 != 0)
		{
			next = default;
			anchor = default!;
			return false;
		}

		int ax = ax16 / 16;
		int ay = ay16 / 16;
		int az = az16 / 16;

		next = new Endpoint(ax16 + to.X, ay16 + to.Y, az16 + to.Z, currentEndpoint.Dimension);
		anchor = new BlockPos(ax, ay, az, currentEndpoint.Dimension);
		return true;
	}

	private static Route Reconstruct(Dictionary<NodeKey, CameFrom> predecessors, NodeKey start, NodeKey end, double cost)
	{
		List<Placement> reversedPlacements = new();
		List<Endpoint> reversedEndpoints = new() { end.Endpoint };
		NodeKey currentNode = end;

		while (!currentNode.Equals(start) && predecessors.TryGetValue(currentNode, out CameFrom previousStep))
		{
			reversedPlacements.Add(new Placement(previousStep.Anchor, previousStep.Segment.Block, previousStep.Segment, previousStep.StartDirection, previousStep.EndDirection));
			currentNode = previousStep.PreviousNode;
			reversedEndpoints.Add(currentNode.Endpoint);
		}

		reversedPlacements.Reverse();
		reversedEndpoints.Reverse();

		byte firstStartDirection = reversedPlacements.Count > 0 ? reversedPlacements[0].StartDirection : DirNone;
		byte lastEndDirection = reversedPlacements.Count > 0 ? reversedPlacements[reversedPlacements.Count - 1].EndDirection : DirNone;
		return new Route { EndNode = end, Cost = cost, Placements = reversedPlacements, DebugEndpoints = reversedEndpoints, FirstStartDirection = firstStartDirection, LastEndDirection = lastEndDirection };
	}

	private static Route ReconstructReverse(Dictionary<NodeKey, CameFrom> predecessors, NodeKey start, double cost)
	{
		List<Placement> placements = new();
		List<Endpoint> endpoints = new() { start.Endpoint };
		NodeKey currentNode = start;

		while (predecessors.TryGetValue(currentNode, out CameFrom previousStep))
		{
			placements.Add(new Placement(previousStep.Anchor, previousStep.Segment.Block, previousStep.Segment, previousStep.StartDirection, previousStep.EndDirection));
			currentNode = previousStep.PreviousNode;
			endpoints.Add(currentNode.Endpoint);
		}

		byte firstStartDirection = placements.Count > 0 ? placements[0].StartDirection : DirNone;
		byte lastEndDirection = placements.Count > 0 ? placements[placements.Count - 1].EndDirection : DirNone;
		return new Route { EndNode = currentNode, Cost = cost, Placements = placements, DebugEndpoints = endpoints, FirstStartDirection = firstStartDirection, LastEndDirection = lastEndDirection };
	}

	private static bool ConflictsWithReserved(Dictionary<BlockPos, Block>? reserved, BlockPos position, Block block)
	{
		if (reserved == null) return false;
		return reserved.TryGetValue(position, out Block existing) && existing.BlockId != block.BlockId;
	}

	private static bool ConflictsWithReservedCells(Dictionary<CellKey, CellClaim>? reservedCells, Segment segment, BlockPos anchor)
	{
		if (reservedCells == null) return false;

		foreach (CellKey cell in FootprintCells(segment, anchor))
		{
			if (!reservedCells.TryGetValue(cell, out CellClaim claim)) continue;
			if (claim.BlockID == segment.Block.BlockId && claim.Anchor.Equals(anchor)) continue;
			return true;
		}

		return false;
	}

	private static bool ConflictsWithCurrentRoute(Dictionary<NodeKey, CameFrom> predecessors, NodeKey start, NodeKey currentNode, Segment segment, BlockPos anchor)
	{
		NodeKey walk = currentNode;
		while (predecessors.TryGetValue(walk, out CameFrom previousStep))
		{
			if (previousStep.Anchor.Equals(anchor) && previousStep.Segment.Block.BlockId == segment.Block.BlockId) { walk = previousStep.PreviousNode; continue; }

			if (FootprintsOverlap(segment, anchor, previousStep.Segment, previousStep.Anchor)) return true;
			walk = previousStep.PreviousNode;
		}
		return false;
	}

	private static bool ReservePlacements(Route route, Dictionary<BlockPos, Block> reservedBlocks, Dictionary<CellKey, CellClaim> reservedCells)
	{
		for (int index = 0; index < route.Placements.Count; index++)
		{
			Placement placement = route.Placements[index];
			if (reservedBlocks.TryGetValue(placement.Position, out Block existing)) { if (existing.BlockId != placement.Block.BlockId) return false; }
		}

		for (int index = 0; index < route.Placements.Count; index++)
		{
			Placement placement = route.Placements[index];

			foreach (CellKey cell in FootprintCells(placement.Segment, placement.Position))
			{
				if (reservedCells.TryGetValue(cell, out CellClaim claim))
				{
					if (claim.BlockID == placement.Block.BlockId && claim.Anchor.Equals(placement.Position)) continue;
					return false;
				}
			}
		}

		for (int index = 0; index < route.Placements.Count; index++)
		{
			Placement placement = route.Placements[index];
			if (!reservedBlocks.ContainsKey(placement.Position)) reservedBlocks[placement.Position.Copy()] = placement.Block;

			foreach (CellKey cell in FootprintCells(placement.Segment, placement.Position)) { if (!reservedCells.ContainsKey(cell)) reservedCells[cell] = new CellClaim(placement.Position, placement.Block.BlockId, placement.Segment); }
		}

		return true;
	}

	private static bool BuildPlan(Route route, out List<Placement> placements, out string error)
	{
		error = "";
		placements = new List<Placement>();
		Dictionary<BlockPos, Block> blockByPosition = new();

		for (int index = 0; index < route.Placements.Count; index++)
		{
			Placement placement = route.Placements[index];
			BlockPos key = placement.Position.Copy();

			if (blockByPosition.TryGetValue(key, out Block existing))
			{
				if (existing.BlockId != placement.Block.BlockId)
				{
					error = $"Route self-overlaps at {key.X},{key.Y},{key.Z}: {existing.Code} vs {placement.Block.Code}.";
					return false;
				}
				continue;
			}

			blockByPosition[key] = placement.Block;
			placements.Add(new Placement(key, placement.Block, placement.Segment, placement.StartDirection, placement.EndDirection));
		}
		if (placements.Count == 0) { error = "Route failed to produce placements."; return false; }

		return true;
	}

	private static bool CanPlaceBlockIgnoringFlora(IWorldAccessor world, IPlayer player, Placement placement, ref string failure)
	{
		IBlockAccessor blockAccessor = world.BlockAccessor;
		Block existing = blockAccessor.GetBlock(placement.Position);

		if (placement.Segment.FlowCells.Count > 0) { return CanPlaceFlowFieldRailIgnoringClutter(world, player, placement, ref failure); }

		if (IsIgnoredFlora(existing))
		{
			failure = "";
			return true;
		}

		BlockSelection blockSelection = MakeSelection(blockAccessor, placement.Position);
		if (placement.Block.CanPlaceBlock(world, player, blockSelection, ref failure)) return true;

		failure = DescribePlacementFailure(failure, existing, placement);
		return false;
	}

	private static bool CanPlaceFlowFieldRailIgnoringClutter(IWorldAccessor world, IPlayer player, Placement placement, ref string failure)
	{
		IBlockAccessor blockAccessor = world.BlockAccessor;
		HashSet<CellKey> visited = new();

		foreach (FootprintCell footprintCell in LocalFootprintCells(placement.Segment))
		{
			CellKey key = new(placement.Position.X + footprintCell.X, placement.Position.Y + footprintCell.Y, placement.Position.Z + footprintCell.Z, placement.Position.dimension);
			if (!visited.Add(key)) continue;

			BlockPos position = new(key.X, key.Y, key.Z, key.Dimension);
			Block existing = blockAccessor.GetBlock(position);

			if (position.Equals(placement.Position))
			{
				if (CanReplaceAnchorForLoopWand(existing, placement.Block)) continue;

				failure = DescribePlacementFailure("notreplaceable", existing, placement);
				return false;
			}

			if (CanReplaceFootprintForLoopWand(existing, placement.Block)) continue;

			failure = DescribeFootprintPlacementFailure("notreplaceable", existing, placement, position, footprintCell);
			return false;
		}

		if (player != null && !world.Claims.TryAccess(player, placement.Position, EnumBlockAccessFlags.BuildOrBreak))
		{
			failure = DescribePlacementFailure("claimed", blockAccessor.GetBlock(placement.Position), placement);
			return false;
		}

		failure = "";
		return true;
	}

	private static bool CanReplaceAnchorForLoopWand(Block existing, Block planned)
	{
		return existing.BlockId == 0 || existing.BlockId == planned.BlockId || IsIgnoredFlora(existing) || existing.IsReplacableBy(planned);
	}

	private static bool CanReplaceFootprintForLoopWand(Block existing, Block planned)
	{
		return existing.BlockId == 0 || IsIgnoredFlora(existing) || existing.IsReplacableBy(planned);
	}

	private static string DescribePlacementFailure(string failure, Block existing, Placement placement)
	{
		string reason = string.IsNullOrWhiteSpace(failure) ? "CanPlaceBlock returned false" : failure;
		return $"{reason}; existing={DescribeBlock(existing)}; planned={DescribeBlock(placement.Block)}";
	}

	private static string DescribeBlock(Block block)
	{
		string code = block.Code?.ToString() ?? "<null-code>";
		return $"{code} (id {block.BlockId}, material {block.BlockMaterial}, replaceable {block.Replaceable})";
	}

	private static string DescribeFootprintPlacementFailure(string failure, Block existing, Placement placement, BlockPos position, FootprintCell cell)
	{
		string reason = string.IsNullOrWhiteSpace(failure) ? "CanPlaceBlock returned false" : failure;
		return $"{reason}; flowCell={position.X},{position.Y},{position.Z} offset={cell.X},{cell.Y},{cell.Z}; existing={DescribeBlock(existing)}; planned={DescribeBlock(placement.Block)}";
 	}

	private static void RemoveIgnoredFloraForSegment(IWorldAccessor world, Segment segment, BlockPos anchor)
	{
		if (world.Side != EnumAppSide.Server) return;

		IBlockAccessor blockAccessor = world.BlockAccessor;
		HashSet<CellKey> visited = new();

		foreach (FootprintCell footprintCell in LocalFootprintCells(segment))
		{
			int topOffset = footprintCell.H8 >= 8 ? 1 : 0;
			for (int dy = 0; dy <= ClearanceHeightBlocks + topOffset; dy++)
			{
				CellKey key = new(anchor.X + footprintCell.X, anchor.Y + footprintCell.Y + dy, anchor.Z + footprintCell.Z, anchor.dimension);
				if (!visited.Add(key)) continue;

				BlockPos position = new(key.X, key.Y, key.Z, key.Dimension);
				Block existing = blockAccessor.GetBlock(position);
				if (!IsIgnoredFlora(existing)) continue;

				if (existing.EntityClass != null) blockAccessor.RemoveBlockEntity(position);
				blockAccessor.SetBlock(0, position);
			}
		}
	}

	private static bool IsIgnoredFlora(Block block) // All this logic could be consolidated
	{
		if (block.BlockId == 0) return false;
		if (block.BlockMaterial == EnumBlockMaterial.Leaves) return true;

		string path = block.Code?.Path ?? "";

		if (IsIgnoredLooseSurfaceBlock(path)) return true;
		if (block.BlockMaterial != EnumBlockMaterial.Plant) return false;

		return IsIgnoredPlant(path);
	}

	private static bool IsIgnoredLooseSurfaceBlock(string path)
	{
		return path.StartsWith("loosestones-", StringComparison.OrdinalIgnoreCase)
			|| path.StartsWith("looseboulders-", StringComparison.OrdinalIgnoreCase)
			|| path.StartsWith("looseores-", StringComparison.OrdinalIgnoreCase)
			|| path.StartsWith("looseflints-", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsIgnoredPlant(string path)
	{
		return path.Contains("grass", StringComparison.OrdinalIgnoreCase)
			|| path.Contains("flower", StringComparison.OrdinalIgnoreCase)
			|| path.Contains("fern", StringComparison.OrdinalIgnoreCase)
			|| path.Contains("bush", StringComparison.OrdinalIgnoreCase)
			|| path.Contains("herb", StringComparison.OrdinalIgnoreCase)
			|| path.Contains("mushroom", StringComparison.OrdinalIgnoreCase)
			|| path.Contains("lichen", StringComparison.OrdinalIgnoreCase)
			|| path.Contains("vine", StringComparison.OrdinalIgnoreCase)
			|| path.Contains("sapling", StringComparison.OrdinalIgnoreCase)
			|| path.StartsWith("crop-", StringComparison.OrdinalIgnoreCase)
			|| path.StartsWith("tallplant-coopersreed-", StringComparison.OrdinalIgnoreCase)
			|| path.StartsWith("tallplant-tule-", StringComparison.OrdinalIgnoreCase);
	}

	private static void SpawnFlowFieldColliders(IWorldAccessor world, Segment segment, BlockPos anchor)
	{
		if (world.Side != EnumAppSide.Server || segment.FlowCells.Count == 0) return;

		IBlockAccessor blockAccessor = world.BlockAccessor;
		string prefix = IsFlowFieldPassThrough(segment.Block) ? "flowcolliderghost" : "flowcollider";

		for (int index = 0; index < segment.FlowCells.Count; index++)
		{
			FlowCell flowCell = segment.FlowCells[index];
			if (flowCell.X == 0 && flowCell.Y == 0 && flowCell.Z == 0) continue;

			string direction = FlowDirLetter(flowCell.Direction);
			if (direction.Length == 0) continue;

			int h8 = GameMath.Clamp(flowCell.H8, 1, 8);
			Block? flow = world.GetBlock(new AssetLocation("collisionflowfields", $"{prefix}-{direction}-{h8}"));
			if (flow == null || flow.BlockId == 0) continue;

			BlockPos cellPosition = new(anchor.X + flowCell.X, anchor.Y + flowCell.Y, anchor.Z + flowCell.Z, anchor.dimension);
			Block existing = blockAccessor.GetBlock(cellPosition);
			if (existing.BlockId == flow.BlockId) continue;

			if (existing.EntityClass != null) blockAccessor.RemoveBlockEntity(cellPosition);
			blockAccessor.SetBlock(flow.BlockId, cellPosition);
		}
	}

	private static bool IsFlowFieldPassThrough(Block block)
	{
		string mode = block.Attributes?["collisionFlowFieldMode"].AsString(null);
		if (!string.IsNullOrEmpty(mode)) return mode.Equals("passthrough", StringComparison.OrdinalIgnoreCase);
		return block.Attributes?["collisionFlowFieldPassThrough"].AsBool(false) == true;
	}

	private static string FlowDirLetter(int dir) => dir switch
	{
		0 => "n",
		1 => "e",
		2 => "s",
		3 => "w",
		4 => "u",
		5 => "d",
		_ => ""
	};

	private static bool IsSegmentClear(IWorldAccessor world, Segment segment, BlockPos anchor, bool allowExistingIdenticalAnchor = true)
	{
		IBlockAccessor blockAccessor = world.BlockAccessor;

		bool HasSupport(int x, int y, int z)
		{
			BlockPos below = new(anchor.X + x, anchor.Y + y - 1, anchor.Z + z, anchor.dimension);
			Block support = blockAccessor.GetBlock(below);
			return !IsIgnoredFlora(support) && support.SideSolid[BlockFacing.indexUP];
		}

		bool CheckCell(int x, int y, int z, int h8)
		{
			if (!HasSupport(x, y, z)) return false;

			int topOffset = h8 >= 8 ? 1 : 0;
			for (int dy = 0; dy <= ClearanceHeightBlocks + topOffset; dy++)
			{
				BlockPos blockPosition = new(anchor.X + x, anchor.Y + y + dy, anchor.Z + z, anchor.dimension);
				Block existing = blockAccessor.GetBlock(blockPosition);

				if (blockPosition.Equals(anchor) && !allowExistingIdenticalAnchor && existing.BlockId == segment.Block.BlockId) return false;
				if (blockPosition.Equals(anchor) && (existing.BlockId == 0 || IsIgnoredFlora(existing) || existing.IsReplacableBy(segment.Block) || existing.BlockId == segment.Block.BlockId)) continue;
				if (IsIgnoredFlora(existing)) continue;
				if (!existing.IsReplacableBy(segment.Block)) return false;
			}
			return true;
		}

		foreach (FootprintCell footprintCell in LocalFootprintCells(segment)) { if (!CheckCell(footprintCell.X, footprintCell.Y, footprintCell.Z, footprintCell.H8)) return false; }

		return true;
	}

	private static IEnumerable<FootprintCell> LocalFootprintCells(Segment segment)
	{
		yield return new FootprintCell(0, 0, 0, 1);

		for (int index = 0; index < segment.FlowCells.Count; index++)
		{
			FlowCell flowCell = segment.FlowCells[index];
			yield return new FootprintCell(flowCell.X, flowCell.Y, flowCell.Z, flowCell.H8);
		}
	}

	private static IEnumerable<CellKey> FootprintCells(Segment segment, BlockPos anchor)
	{
		foreach (FootprintCell footprintCell in LocalFootprintCells(segment)) { yield return new CellKey(anchor.X + footprintCell.X, anchor.Y + footprintCell.Y, anchor.Z + footprintCell.Z, anchor.dimension); }
	}

	private static bool FootprintsOverlap(Segment aSeg, BlockPos aAnchor, Segment bSeg, BlockPos bAnchor)
	{
		HashSet<CellKey> firstFootprint = new();
		foreach (CellKey cell in FootprintCells(aSeg, aAnchor)) firstFootprint.Add(cell);
		foreach (CellKey cell in FootprintCells(bSeg, bAnchor)) { if (firstFootprint.Contains(cell)) return true; }
		return false;
	}

	private static BlockSelection MakeSelection(IBlockAccessor blockAccessor, BlockPos position)
	{
		return new BlockSelection(position.Copy(), BlockFacing.UP, blockAccessor.GetBlock(position))
		{
			HitPosition = new Vec3d(0.5, 0.5, 0.5),
			DidOffset = false
		};
	}

	private static List<Endpoint> CollectTargetEndpoints(IWorldAccessor world, BlockPos target, SegmentCache segmentCache, SearchBounds searchBounds)
	{
		int radius = segmentCache.Gauge == 0 ? 2 : 8;
		int yRadius = segmentCache.Gauge == 0 ? 2 : 5;
		Endpoint targetCenter = new(Q16(target.X + 0.5), Q16(target.Y + TrackSpecsDictionary.DefaultY), Q16(target.Z + 0.5), target.dimension);

		Dictionary<Endpoint, double> best = new();
		Dictionary<PlacementKey, bool> clearanceByPlacement = new();

		for (int dx = -radius; dx <= radius; dx++)
		for (int dy = -yRadius; dy <= yRadius; dy++)
		for (int dz = -radius; dz <= radius; dz++)
		{
			int ax = target.X + dx;
			int ay = target.Y + dy;
			int az = target.Z + dz;

			for (int index = 0; index < segmentCache.Segments.Count; index++)
			{
				Segment segment = segmentCache.Segments[index];
				BlockPos anchor = new(ax, ay, az, target.dimension);
				PlacementKey key = new(segment.Block.BlockId, anchor);

				if (!clearanceByPlacement.TryGetValue(key, out bool clear))
				{
					clear = IsSegmentClear(world, segment, anchor);
					clearanceByPlacement[key] = clear;
				}
				if (!clear) continue;

				Add(segment.A);
				Add(segment.B);

				void Add(Endpoint local)
				{
					Endpoint endpoint = new(Q16(ax) + local.X, Q16(ay) + local.Y, Q16(az) + local.Z, target.dimension);
					if (!searchBounds.Contains(endpoint)) return;
					double distanceSquared = DistanceSquared(endpoint, targetCenter);
					double maximumDistanceSquared = radius * radius * 256.0;
					if (distanceSquared > maximumDistanceSquared) return;
					if (!best.TryGetValue(endpoint, out double old) || distanceSquared < old) best[endpoint] = distanceSquared;
				}
			}
		}

		return best.OrderBy(kv => kv.Value).Take(MaximumTargetEndpoints).Select(kv => kv.Key).ToList();
	}

	private static SegmentCache EnsureCache(IWorldAccessor world, byte gauge)
	{
		string key = world.Side + ":" + world.SavegameIdentifier + ":" + gauge;
		if (!SegmentCaches.TryGetValue(key, out SegmentCache? segmentCache))
		{
			segmentCache = SegmentCache.Build(world, gauge);
			SegmentCaches[key] = segmentCache;
		}
		return segmentCache;
	}

	private static WandState GetState(IWorldAccessor world, string playerUID)
	{
		string key = world.Side + ":" + playerUID;
		if (!States.TryGetValue(key, out WandState? state))
		{
			state = new WandState();
			States[key] = state;
		}
		return state;
	}

	private static void ResetSelectionState(WandState state)
	{
		state.IsGenerating = false;
		state.Points.Clear();
		state.FirstRail = null;
		state.SecondRail = null;
	}

	private static void Highlight(IWorldAccessor world, EntityPlayer playerEntity, WandState state)
	{
		if (world.Side != EnumAppSide.Client || world.Api is not ICoreClientAPI clientAPI) return;

		List<BlockPos> wagonway = new();
		List<BlockPos> standardGauge = new();

		if (state.Mode == WandToolMode.Connection)
		{
			List<BlockPos> selected = state.Gauge == 0 ? wagonway : standardGauge;
			if (state.FirstRail != null) selected.Add(state.FirstRail.Position);
			if (state.SecondRail != null) selected.Add(state.SecondRail.Position);
		}
		else { (state.Gauge == 0 ? wagonway : standardGauge).AddRange(state.Points); }

		clientAPI.World.HighlightBlocks(clientAPI.World.Player, HighlightIDBase, wagonway);
		clientAPI.World.HighlightBlocks(clientAPI.World.Player, HighlightIDBase + 1, standardGauge);
	}

	private static void DrawPointLoop(ICoreClientAPI clientAPI, WandState state)
	{
		if (state.Points.Count < 2) return;

		BlockPos origin = state.Points[0];
		int color = state.IsGenerating ? ColorUtil.ToRgba(255, 0, 255, 0) : ColorUtil.ToRgba(255, 180, 220, 255);
		int count = state.Points.Count >= 3 ? state.Points.Count : state.Points.Count - 1;

		for (int index = 0; index < count; index++)
		{
			BlockPos firstPosition = state.Points[index];
			BlockPos secondPosition = state.Points[(index + 1) % state.Points.Count];

			clientAPI.Render.RenderLine
			(
				origin,
				firstPosition.X - origin.X + 0.5f, firstPosition.Y - origin.Y + 0.15f, firstPosition.Z - origin.Z + 0.5f,
				secondPosition.X - origin.X + 0.5f, secondPosition.Y - origin.Y + 0.15f, secondPosition.Z - origin.Z + 0.5f,
				color
			);
		}
	}

	private static void DrawConnectionLine(ICoreClientAPI clientAPI, WandState state)
	{
		if (state.FirstRail == null || state.SecondRail == null) return;

		BlockPos firstPosition = state.FirstRail.Position;
		BlockPos secondPosition = state.SecondRail.Position;
		int color = state.IsGenerating ? ColorUtil.ToRgba(255, 0, 255, 0) : ColorUtil.ToRgba(255, 180, 220, 255);

		clientAPI.Render.RenderLine(
			firstPosition,
			0.5f, 0.15f, 0.5f,
			secondPosition.X - firstPosition.X + 0.5f, secondPosition.Y - firstPosition.Y + 0.15f, secondPosition.Z - firstPosition.Z + 0.5f,
			color
		);
	}

	private static void SayClient(EntityAgent entity, string message)
	{
		if (entity.World.Side == EnumAppSide.Client && entity.World.Api is ICoreClientAPI clientAPI) { clientAPI.ShowChatMessage(message); }
	}

	private static void SayServer(EntityAgent entity, string message)
	{
		IWorldAccessor world = entity.World;
		if (world.Side == EnumAppSide.Server && entity is EntityPlayer playerEntity && world.PlayerByUid(playerEntity.PlayerUID) is IServerPlayer serverPlayer) { serverPlayer.SendMessage(0, message, EnumChatType.Notification); }
	}

	private static WandToolMode GetSelectedMode(ItemSlot slot) => ToolModeFromIndex(slot?.Itemstack?.Attributes.GetInt("toolMode", 0) ?? 0);
	private static WandToolMode ToolModeFromIndex(int toolMode) => toolMode switch
	{
		1 => WandToolMode.WagonwayLoop,
		2 => WandToolMode.Connection,
		_ => WandToolMode.StandardGaugeLoop
	};
	private static byte LoopModeGauge(WandToolMode mode) => mode == WandToolMode.WagonwayLoop ? (byte)0 : (byte)1;
	private static double SegmentPenalty(Segment segment)
	{
		double penalty = 0;

		if (segment.Gauge == 0)
		{
			if (segment.ChangesDirection) penalty += PenaltyWagonwayCurve;
			return penalty;
		}

		if (segment.Gauge != 1) return penalty;

		if (!segment.IsStandardGaugeStraight) penalty += PenaltyStandardGaugeNonStraight;
		if (segment.IsStandardGaugeSwerve) penalty += PenaltyStandardGaugeSwerve;
		if (segment.IsStandardGaugeSharpTurn) penalty += PenaltyStandardGaugeSharpTurn;
		if (segment.IsStandardGaugeWideTurn) penalty += PenaltyStandardGaugeWideTurn;
		return penalty;
	}

	private static double GoalAwareVerticalPenalty(Endpoint currentEndpoint, Endpoint next, Segment segment, List<Endpoint> goals)
	{
		if (!segment.ChangesElevation || segment.VerticalBlocks <= 0) return 0;

		double before = VerticalDistanceToClosestGoal(currentEndpoint, goals);
		double after = VerticalDistanceToClosestGoal(next, goals);

		if (segment.Gauge == 0)
		{
			double wagonwayPenalty = Math.Min(MaximumWagonwayVerticalPenalty, segment.VerticalBlocks * PenaltyWagonwayVerticalPerBlock);
			double factor = after < before ? PenaltyVerticalTowardGoalFactor : after > before ? PenaltyVerticalAwayGoalFactor : PenaltyVerticalNeutralGoalFactor;
			return Math.Min(MaximumWagonwayVerticalPenalty, wagonwayPenalty * factor);
		}

		double penalty = segment.VerticalBlocks * PenaltyVerticalMotionPerBlock;
		if (segment.Gauge == 1)
		{
			if (segment.IsStandardGaugeSharpSlope) penalty += PenaltyStandardGaugeSharpSlope;
			if (segment.IsStandardGaugeGentleSlope) penalty += PenaltyStandardGaugeGentleSlope;
		}

		if (after < before) return penalty * PenaltyVerticalTowardGoalFactor;
		if (after > before) return penalty * PenaltyVerticalAwayGoalFactor;
		return penalty * PenaltyVerticalNeutralGoalFactor;
	}

	private static double VerticalDistanceToClosestGoal(Endpoint endpoint, List<Endpoint> goals)
	{
		double best = double.MaxValue;
		for (int index = 0; index < goals.Count; index++)
		{
			double dy = Math.Abs(endpoint.Y - goals[index].Y) / 16.0;
			if (dy < best) best = dy;
		}
		return best;
	}

	private static double ComputeHeuristicWeight(Endpoint start, List<Endpoint> goals)
	{
		double best = double.MaxValue;
		double bestHorizontal = 1.0;
		double bestVertical = 0.0;

		for (int index = 0; index < goals.Count; index++)
		{
			double dx = (goals[index].X - start.X) / 16.0;
			double dy = (goals[index].Y - start.Y) / 16.0;
			double dz = (goals[index].Z - start.Z) / 16.0;
			double distanceSquared = dx * dx + dy * dy + dz * dz;

			if (distanceSquared < best)
			{
				best = distanceSquared;
				bestHorizontal = Math.Sqrt(dx * dx + dz * dz);
				bestVertical = Math.Abs(dy);
			}
		}

		double roughness = bestVertical / Math.Max(1.0, bestHorizontal);
		double weight = SearchHeuristicWeightBase + roughness * SearchHeuristicRoughnessScale;
		return Math.Min(MaximumRoughSearchHeuristicWeight, Math.Max(SearchHeuristicWeightBase, weight));
	}

	private static double GoalProgressPriorityPenalty(double currentHeuristic, double nextHeuristic) 
	{
		double progress = currentHeuristic - nextHeuristic;

		if (progress < 0) { return -progress * PriorityPenaltyMovingAwayFromGoal; }
		if (progress < LowGoalProgressThreshold) { return PriorityPenaltyLowGoalProgress * (1.0 - progress / LowGoalProgressThreshold); }

		return 0;
	}

	private static double TransitionPenalty
	(
		IWorldAccessor world,
		SegmentCache segmentCache,
		Dictionary<NodeKey, CameFrom> predecessors,
		NodeKey currentNode,
		Segment nextSegment,
		BlockPos anchor,
		byte startDirection,
		byte endDirection,
		int deltaY,
		Dictionary<CellKey, CellClaim>? reservedCells,
		Endpoint searchStartEndpoint,
		HashSet<Endpoint> searchGoalEndpoints
	)
	{
		double penalty = SideOccupancyPenalty(world, nextSegment, anchor, startDirection, endDirection, reservedCells, predecessors, currentNode);

		if (predecessors.TryGetValue(currentNode, out CameFrom previousStep))
		{
			int prevSign = Math.Sign(previousStep.VerticalDelta);
			int nextSign = Math.Sign(deltaY);
			if (prevSign != 0 && nextSign != 0 && prevSign != nextSign)
			{
				penalty += PenaltyVerticalDirectionFlip;
			}

			if (segmentCache.Gauge == 1 && previousStep.Segment.ChangesDirection && nextSegment.ChangesDirection)
			{
				penalty += PenaltyStandardGaugeRepeatedTurn;
				if (previousStep.StartDirection == endDirection && previousStep.EndDirection == startDirection) { penalty += PenaltyStandardGaugeTurnBacktrack; }
			}

			if (segmentCache.Gauge == 0)
			{
				if (previousStep.Segment.ChangesDirection && nextSegment.ChangesDirection) penalty += PenaltyWagonwayRepeatedCurve;
				penalty += WagonwayCurrentNeighbourPenalty(predecessors, currentNode, anchor, previousStep.Anchor);
			}
		}

		if (segmentCache.Gauge == 0)
		{
			if (currentNode.ArrivalDirection != DirNone && currentNode.ArrivalDirection != startDirection) { penalty += PenaltyWagonwaySnake; }
			penalty += WagonwayReservedNeighbourPenalty(reservedCells, nextSegment, anchor, searchStartEndpoint, searchGoalEndpoints);
		}

		return penalty;
	}

	private static double SideOccupancyPenalty(IWorldAccessor world, Segment segment, BlockPos anchor, byte startDirection, byte endDirection, Dictionary<CellKey, CellClaim>? reservedCells, Dictionary<NodeKey, CameFrom> predecessors, NodeKey currentNode)
	{
		HashSet<CellKey> ownFootprint = new();
		foreach (CellKey cell in FootprintCells(segment, anchor)) ownFootprint.Add(cell);

		HashSet<CellKey> sideCells = new();
		foreach (FootprintCell footprintCell in LocalFootprintCells(segment))
		{
			AddSideCells(sideCells, anchor, footprintCell, startDirection);
			AddSideCells(sideCells, anchor, footprintCell, endDirection);
		}

		double penalty = 0;
		foreach (CellKey side in sideCells)
		{
			if (ownFootprint.Contains(side)) continue;

			bool ownTrack = (reservedCells != null && reservedCells.ContainsKey(side)) || CurrentRouteContainsCell(predecessors, currentNode, side);
			if (ownTrack) { penalty += PenaltySideOwnTrack; continue; }

			BlockPos position = new(side.X, side.Y, side.Z, side.Dimension);
			Block existing = world.BlockAccessor.GetBlock(position);
			if (existing.BlockId != 0 && !IsIgnoredFlora(existing) && !existing.IsReplacableBy(segment.Block)) { penalty += PenaltySideOccupiedBlock; }
		}

		return penalty;
	}

	private static bool CurrentRouteContainsCell(Dictionary<NodeKey, CameFrom> predecessors, NodeKey currentNode, CellKey wanted)
	{
		NodeKey walk = currentNode;
		while (predecessors.TryGetValue(walk, out CameFrom previousStep))
		{
			foreach (CellKey cell in FootprintCells(previousStep.Segment, previousStep.Anchor)) { if (cell.Equals(wanted)) return true; }
			walk = previousStep.PreviousNode;
		}

		return false;
	}

	private static void AddSideCells(HashSet<CellKey> sideCells, BlockPos anchor, FootprintCell cell, byte direction)
	{
		int x = anchor.X + cell.X;
		int y = anchor.Y + cell.Y;
		int z = anchor.Z + cell.Z;
		int dimension = anchor.dimension;

		if (direction == DirNorth || direction == DirSouth)
		{
			sideCells.Add(new CellKey(x - 1, y, z, dimension));
			sideCells.Add(new CellKey(x + 1, y, z, dimension));
			return;
		}

		if (direction == DirEast || direction == DirWest)
		{
			sideCells.Add(new CellKey(x, y, z - 1, dimension));
			sideCells.Add(new CellKey(x, y, z + 1, dimension));
		}
	}

	private static double WagonwayReservedNeighbourPenalty(
		Dictionary<CellKey, CellClaim>? reservedCells,
		Segment nextSegment,
		BlockPos anchor,
		Endpoint searchStartEndpoint,
		HashSet<Endpoint> searchGoalEndpoints
	)
	{
		if (reservedCells == null) return 0;

		double penalty = 0;
		HashSet<CellKey> countedAnchors = new();

		for (int index = 0; index < 4; index++)
		{
			int dx = index == 1 ? 1 : index == 3 ? -1 : 0;
			int dz = index == 0 ? -1 : index == 2 ? 1 : 0;

			for (int dy = -1; dy <= 1; dy++)
			{
				CellKey neighbour = new(anchor.X + dx, anchor.Y + dy, anchor.Z + dz, anchor.dimension); if (!reservedCells.TryGetValue(neighbour, out CellClaim claim)) continue;
				CellKey claimAnchorKey = new(claim.Anchor.X, claim.Anchor.Y, claim.Anchor.Z, claim.Anchor.dimension); if (!countedAnchors.Add(claimAnchorKey)) continue;

				if (TryGetSharedEndpoint(nextSegment, anchor, claim.Segment, claim.Anchor, out Endpoint sharedEndpoint) && IsAllowedSearchSeamEndpoint(sharedEndpoint, searchStartEndpoint, searchGoalEndpoints)) { continue; }
				
				penalty += PenaltyWagonwayReservedNeighbour;
			}
		}

		return penalty;
	}

	private static double WagonwayCurrentNeighbourPenalty(Dictionary<NodeKey, CameFrom> predecessors, NodeKey currentNode, BlockPos anchor, BlockPos allowedPreviousAnchor)
	{
		double penalty = 0;
		NodeKey walk = currentNode;

		while (predecessors.TryGetValue(walk, out CameFrom previousStep))
		{
			if (!previousStep.Anchor.Equals(allowedPreviousAnchor) && IsWagonwayNeighbour(anchor, previousStep.Anchor)) { penalty += PenaltyWagonwayCurrentNeighbour; }
			walk = previousStep.PreviousNode;
		}

		return penalty;
	}

	private static bool IsWagonwayNeighbour(BlockPos firstPosition, BlockPos secondPosition) 
	{
		if (firstPosition.dimension != secondPosition.dimension) return false;

		int dx = Math.Abs(firstPosition.X - secondPosition.X);
		int dz = Math.Abs(firstPosition.Z - secondPosition.Z);

		// Match DynamicTrackAutoUpdater neighbor detection, only cardinal neighbours can auto-connect, they can be on different levels.
		return dx + dz == 1 && Math.Abs(firstPosition.Y - secondPosition.Y) <= 1;
	}

	private static bool IsAllowedSearchSeamEndpoint(Endpoint endpoint, Endpoint searchStartEndpoint, HashSet<Endpoint> searchGoalEndpoints)
	{
		return endpoint.Equals(searchStartEndpoint) || searchGoalEndpoints.Contains(endpoint);
	}

	private static bool TryGetSharedEndpoint(Segment aSeg, BlockPos aAnchor, Segment bSeg, BlockPos bAnchor, out Endpoint shared)
	{
		Endpoint a0 = WorldEndpoint(aAnchor, aSeg.A);
		Endpoint a1 = WorldEndpoint(aAnchor, aSeg.B);
		Endpoint b0 = WorldEndpoint(bAnchor, bSeg.A);
		Endpoint b1 = WorldEndpoint(bAnchor, bSeg.B);

		if (a0.Equals(b0) || a0.Equals(b1)) { shared = a0; return true; }
		if (a1.Equals(b0) || a1.Equals(b1)) { shared = a1; return true; }

		shared = default;
		return false;
	}

	private static Endpoint WorldEndpoint(BlockPos anchor, Endpoint local) { return new Endpoint(Q16(anchor.X) + local.X, Q16(anchor.Y) + local.Y, Q16(anchor.Z) + local.Z, anchor.dimension); }
	private static BlockPos EndpointBlockPos(Endpoint EndpointBlockPosition) { return new BlockPos(FloorDiv(EndpointBlockPosition.X, 16), FloorDiv(EndpointBlockPosition.Y, 16), FloorDiv(EndpointBlockPosition.Z, 16), EndpointBlockPosition.Dimension); }

	private static bool SamePos(BlockPos a, BlockPos b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.dimension == b.dimension;
	private static string GaugeName(byte gauge) => gauge == 0 ? "wagonway" : "standard gauge";
	private static byte OppositeDirection(byte direction) => direction switch { DirNorth => DirSouth, DirEast => DirWest, DirSouth => DirNorth, DirWest => DirEast, _ => DirNone };
	private static byte CardinalDir(Vec3f from, Vec3f to)
	{
		float dx = to.X - from.X;
		float dz = to.Z - from.Z;
		if (Math.Abs(dx) >= Math.Abs(dz)) return dx >= 0 ? DirEast : DirWest;
		return dz >= 0 ? DirSouth : DirNorth;
	}
	private static int Q16(double v) => (int)Math.Round(v * 16.0);
	private static int FloorDiv(int value, int divisor)
	{
		int q = value / divisor;
		int r = value % divisor;
		return r != 0 && ((r < 0) != (divisor < 0)) ? q - 1 : q;
	}
	private static double Heuristic(Endpoint endpoint, List<Endpoint> goalEndpoint) => Math.Sqrt(goalEndpoint.Min(g => DistanceSquared(endpoint, g))) / 16.0;
	private static double DistanceSquared(Endpoint a, Endpoint b)
	{
		double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
		return dx * dx + dy * dy + dz * dz;
	}

	private sealed class WandState
	{
		public WandToolMode Mode = WandToolMode.StandardGaugeLoop;
		public byte Gauge = 1;
		public bool IsGenerating;
		public readonly List<BlockPos> Points = new();
		public SelectedRail? FirstRail;
		public SelectedRail? SecondRail;
	}

	private sealed class SegmentCache
	{
		public readonly byte Gauge;
		public readonly List<Segment> Segments;
		public readonly int HorizontalReach;
		public readonly int VerticalReach;

		private SegmentCache(byte gauge, List<Segment> segments, int horizontalReach, int verticalReach)
		{
			Gauge = gauge;
			Segments = segments;
			HorizontalReach = horizontalReach;
			VerticalReach = verticalReach;
		}

		public static SegmentCache Build(IWorldAccessor world, byte gauge)
		{
			TrackSpecsDictionary.GetMaxSpecReach(out int horizontalReach, out int verticalReach);
			List<Segment> segments = new();

			foreach (Block? block in world.Blocks)
			{
				if (block?.Code == null) continue;
				if (!TrackSpecsDictionary.TryGet(block, out TrackPieceSpec spec)) continue;
				if (spec.Gauge != gauge || spec.SignalKind != SignalKind.None || spec.Paths.Length != 1) continue;
				if (block.Variant != null && block.Variant.TryGetValue("mat", out string? mat) && mat != "metal") continue;

				string path = block.Code.Path;
				if (path.Contains("station", StringComparison.OrdinalIgnoreCase)) continue;

				TrackPath trackPath = spec.Paths[0];
				if (trackPath.LocalPoints == null || trackPath.LocalPoints.Length < 2) continue;

				segments.Add(new Segment(block, spec.Gauge, spec.MaxSpeedFactor01, trackPath.LocalPoints, ReadFlowCells(block)));
			}

			segments.Sort((a, b) => a.Cost.CompareTo(b.Cost));
			return new SegmentCache(gauge, segments, horizontalReach, verticalReach);
		}

		public static List<FlowCell> ReadFlowCells(Block block)
		{
			JsonObject? flowFieldAttributes = block.Attributes?["collisionFlowField"];
			JsonObject[]? rows = SelectFlowFieldArray(flowFieldAttributes, block);
			List<FlowCell> cells = new();

			if (rows == null) return cells;

			for (int index = 0; index < rows.Length; index++)
			{
				int[]? positionComponents = rows[index]["pos"].AsArray<int>(null) ?? rows[index]["offset"].AsArray<int>(null);
				if (positionComponents == null || positionComponents.Length < 3) continue;

				int direction = rows[index]["dir"].AsInt(-1);
				if ((uint)direction > 5u) continue;

				cells.Add(new FlowCell(positionComponents[0], positionComponents[1], positionComponents[2], direction, rows[index]["h8"].AsInt(8)));
			}

			return cells;
		}

		private static JsonObject[]? SelectFlowFieldArray(JsonObject? root, Block block)
		{
			if (root == null || !root.Exists) return null;
			if (root.IsArray()) return root.AsArray();

			JsonObject selected = root[block.Code.ToString()];
			if (!selected.Exists) selected = root[block.Code.Path];
			if (!selected.Exists) selected = root["*"];

			return selected.Exists && selected.IsArray() ? selected.AsArray() : null;
		}
	}
	private sealed class Segment
	{
		public readonly Block Block;
		public readonly byte Gauge;
		public readonly Vec3f[] Points;
		public readonly Endpoint A;
		public readonly Endpoint B;
		public readonly double Cost;
		public readonly double VerticalBlocks;
		public readonly List<FlowCell> FlowCells;
		public readonly bool ChangesDirection;
		public readonly bool ChangesElevation;
		public readonly bool IsStandardGaugeStraight;
		public readonly bool IsStandardGaugeSharpTurn;
		public readonly bool IsStandardGaugeWideTurn;
		public readonly bool IsStandardGaugeSwerve;
		public readonly bool IsStandardGaugeSharpSlope;
		public readonly bool IsStandardGaugeGentleSlope;
		private readonly byte ForwardStartDirection;
		private readonly byte ForwardEndDirection;

		public Segment(Block block, byte gauge, float maxSpeedFactor01, Vec3f[] points, List<FlowCell> flowCells)
		{
			Block = block;
			Gauge = gauge;
			Points = points;
			FlowCells = flowCells;
			A = ToEndpoint(points[0]);
			B = ToEndpoint(points[points.Length - 1]);
			ForwardStartDirection = CardinalDir(points[0], points[1]);
			ForwardEndDirection = CardinalDir(points[points.Length - 2], points[points.Length - 1]);
			ChangesDirection = ForwardStartDirection != ForwardEndDirection;
			ChangesElevation = A.Y != B.Y;
			VerticalBlocks = Math.Abs(B.Y - A.Y) / 16.0;

			string path = block.Code?.Path ?? "";
			IsStandardGaugeStraight = gauge == 1 && path.Contains("widerails_straight", StringComparison.OrdinalIgnoreCase);
			IsStandardGaugeSharpTurn = gauge == 1 && path.Contains("turn_sharp", StringComparison.OrdinalIgnoreCase);
			IsStandardGaugeWideTurn = gauge == 1 && path.Contains("turn_wide", StringComparison.OrdinalIgnoreCase);
			IsStandardGaugeSwerve = gauge == 1 && path.Contains("swerve", StringComparison.OrdinalIgnoreCase);
			IsStandardGaugeSharpSlope = gauge == 1 && path.Contains("slope_short", StringComparison.OrdinalIgnoreCase);
			IsStandardGaugeGentleSlope = gauge == 1 && path.Contains("slope_long", StringComparison.OrdinalIgnoreCase);

			double normalizedLength = Math.Max(1.0, Math.Max(Length(points), EndpointDistance(points[0], points[points.Length - 1])));
			double speedFactor = Math.Max(0.1, maxSpeedFactor01);
			Cost = normalizedLength / speedFactor;
		}

		public byte StartDir(bool forward) => forward ? ForwardStartDirection : OppositeDirection(ForwardEndDirection);
		public byte EndDir(bool forward) => forward ? ForwardEndDirection : OppositeDirection(ForwardStartDirection);
		public int DeltaY(bool forward) => forward ? B.Y - A.Y : A.Y - B.Y;

		private static Endpoint ToEndpoint(Vec3f point) => new(Q16(point.X), Q16(point.Y), Q16(point.Z));

		private static double EndpointDistance(Vec3f a, Vec3f b)
		{
			double dx = b.X - a.X;
			double dy = b.Y - a.Y;
			double dz = b.Z - a.Z;
			return Math.Sqrt(dx * dx + dy * dy + dz * dz);
		}

		private static double Length(Vec3f[] points)
		{
			double length = 0;
			for (int index = 1; index < points.Length; index++)
			{
				double dx = points[index].X - points[index - 1].X;
				double dy = points[index].Y - points[index - 1].Y;
				double dz = points[index].Z - points[index - 1].Z;
				length += Math.Sqrt(dx * dx + dy * dy + dz * dz);
			}
			return length;
		}
	}

	private readonly struct FlowCell
	{
		public readonly int X, Y, Z, Direction, H8;
		public FlowCell(int x, int y, int z, int direction, int h8) { X = x; Y = y; Z = z; Direction = direction; H8 = h8; }
	}

	private readonly struct FootprintCell
	{
		public readonly int X, Y, Z, H8;
		public FootprintCell(int x, int y, int z, int h8) { X = x; Y = y; Z = z; H8 = h8; }
	}

	private readonly struct Placement
	{
		public readonly BlockPos Position;
		public readonly Block Block;
		public readonly Segment Segment;
		public readonly byte StartDirection;
		public readonly byte EndDirection;

		public Placement(BlockPos position, Block block, Segment segment, byte startDirection, byte endDirection)
		{
			Position = position;
			Block = block;
			Segment = segment;
			StartDirection = startDirection;
			EndDirection = endDirection;
		}
	}

	private readonly struct CameFrom
	{
		public readonly NodeKey PreviousNode;
		public readonly Segment Segment;
		public readonly BlockPos Anchor;
		public readonly byte StartDirection;
		public readonly byte EndDirection;
		public readonly int VerticalDelta;

		public CameFrom(NodeKey previousNode, Segment segment, BlockPos anchor, byte startDirection, byte endDirection, int deltaY)
		{
			PreviousNode = previousNode;
			Segment = segment;
			Anchor = anchor;
			StartDirection = startDirection;
			EndDirection = endDirection;
			VerticalDelta = deltaY;
		}
	}

	private sealed class Route
	{
		public NodeKey EndNode;
		public double Cost;
		public byte FirstStartDirection = DirNone;
		public byte LastEndDirection = DirNone;
		public List<Placement> Placements = new();
		public List<Endpoint> DebugEndpoints = new();

		public void Append(Route other)
		{
			Cost += other.Cost;
			if (FirstStartDirection == DirNone) FirstStartDirection = other.FirstStartDirection;
			if (other.LastEndDirection != DirNone) LastEndDirection = other.LastEndDirection;
			Placements.AddRange(other.Placements);
			if (other.DebugEndpoints.Count > 0)
			{
				if (DebugEndpoints.Count == 0) { DebugEndpoints.AddRange(other.DebugEndpoints); }
				else { DebugEndpoints.AddRange(other.DebugEndpoints.Skip(1)); }
			}
			EndNode = other.EndNode;
		}
	}

	private readonly struct Endpoint : IEquatable<Endpoint>
	{
		public readonly int X, Y, Z, Dimension;
		public Endpoint(int x, int y, int z, int dimension = 0) { X = x; Y = y; Z = z; Dimension = dimension; }
		public bool Equals(Endpoint other) => X == other.X && Y == other.Y && Z == other.Z && Dimension == other.Dimension;
		public override bool Equals(object? obj) => obj is Endpoint other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(X, Y, Z, Dimension);
	}

	private readonly struct GoalSpec
	{
		public readonly Endpoint Endpoint;
		public readonly byte RequiredArrivalDirection;

		public GoalSpec(Endpoint endpoint, byte requiredArrivalDirection)
		{
			Endpoint = endpoint;
			RequiredArrivalDirection = requiredArrivalDirection;
		}

		public bool Matches(NodeKey node) { return Endpoint.Equals(node.Endpoint) && (RequiredArrivalDirection == DirNone || RequiredArrivalDirection == node.ArrivalDirection); }
	}

	private readonly struct NodeKey : IEquatable<NodeKey>
	{
		public readonly Endpoint Endpoint;
		public readonly byte ArrivalDirection;
		public NodeKey(Endpoint endpoint, byte arrivalDirection) { Endpoint = endpoint; ArrivalDirection = arrivalDirection; }
		public bool Equals(NodeKey other) => Endpoint.Equals(other.Endpoint) && ArrivalDirection == other.ArrivalDirection;
		public override bool Equals(object? obj) => obj is NodeKey other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(Endpoint, ArrivalDirection);
	}

	private readonly struct QueuedNode
	{
		public readonly NodeKey Node;
		public readonly double Cost;

		public QueuedNode(NodeKey node, double cost)
		{
			Node = node;
			Cost = cost;
		}
	}

	private readonly struct CellKey : IEquatable<CellKey>
	{
		public readonly int X, Y, Z, Dimension;
		public CellKey(int x, int y, int z, int dimension) { X = x; Y = y; Z = z; Dimension = dimension; }
		public bool Equals(CellKey other) => X == other.X && Y == other.Y && Z == other.Z && Dimension == other.Dimension;
		public override bool Equals(object? obj) => obj is CellKey other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(X, Y, Z, Dimension);
	}

	private readonly struct CellClaim
	{
		public readonly BlockPos Anchor;
		public readonly int BlockID;
		public readonly Segment Segment;

		public CellClaim(BlockPos anchor, int blockID, Segment segment)
		{
			Anchor = anchor.Copy();
			BlockID = blockID;
			Segment = segment;
		}
	}

	private readonly struct PlacementKey : IEquatable<PlacementKey>
	{
		private readonly int BlockID, X, Y, Z, Dimension;
		public PlacementKey(int blockID, BlockPos position) { this.BlockID = blockID; X = position.X; Y = position.Y; Z = position.Z; Dimension = position.dimension; }
		public bool Equals(PlacementKey other) => BlockID == other.BlockID && X == other.X && Y == other.Y && Z == other.Z && Dimension == other.Dimension;
		public override bool Equals(object? obj) => obj is PlacementKey other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(BlockID, X, Y, Z, Dimension);
	}

	private readonly struct SearchBounds
	{
		private readonly int MinX16, MaxX16, MinY16, MaxY16, MinZ16, MaxZ16, Dimension;

		private SearchBounds(int minX16, int maxX16, int minY16, int maxY16, int minZ16, int maxZ16, int dimension)
		{
			this.MinX16 = minX16; this.MaxX16 = maxX16;
			this.MinY16 = minY16; this.MaxY16 = maxY16;
			this.MinZ16 = minZ16; this.MaxZ16 = maxZ16;
			this.Dimension = dimension;
		}

		public static SearchBounds FromPoints(List<BlockPos> points, SegmentCache segmentCache)
		{
			int minX = points.Min(point => point.X), maxX = points.Max(point => point.X);
			int minY = points.Min(point => point.Y), maxY = points.Max(point => point.Y);
			int minZ = points.Min(point => point.Z), maxZ = points.Max(point => point.Z);
			int xz = 24 + segmentCache.HorizontalReach;
			int y = 8 + segmentCache.VerticalReach;
			return new SearchBounds(Q16(minX - xz), Q16(maxX + xz + 1), Q16(minY - y), Q16(maxY + y + 1), Q16(minZ - xz), Q16(maxZ + xz + 1), points[0].dimension);
		}

		public bool Contains(Endpoint endpoint) => endpoint.Dimension == Dimension && endpoint.X >= MinX16 && endpoint.X <= MaxX16 && endpoint.Y >= MinY16 && endpoint.Y <= MaxY16 && endpoint.Z >= MinZ16 && endpoint.Z <= MaxZ16;
		public bool Contains(BlockPos position) => position.dimension == Dimension && Q16(position.X) >= MinX16 && Q16(position.X) <= MaxX16 && Q16(position.Y) >= MinY16 && Q16(position.Y) <= MaxY16 && Q16(position.Z) >= MinZ16 && Q16(position.Z) <= MaxZ16;
	}

	#region Connection Mode
	private const int ProtectedRailBlockID = -1;

	private static void HandleConnectionInteract(EntityAgent byEntity, EntityPlayer playerEntity, BlockSelection blockSelection, WandState state)
	{
		IWorldAccessor world = byEntity.World;
		if (!TryResolveClickedRail(world, blockSelection.Position, out BlockPos position, out Block block, out TrackPieceSpec spec))
		{
			SayClient(byEntity, "Connection mode: select a rail block or any part of its collision footprint.");
			return;
		}

		if (state.FirstRail == null)
		{
			state.Gauge = spec.Gauge;
			state.FirstRail = new SelectedRail(position, block.BlockId, spec.Gauge);
			state.SecondRail = null;
			state.IsGenerating = false;
			Highlight(world, playerEntity, state);
			SayClient(byEntity, $"Connection source selected: {GaugeName(spec.Gauge)} rail at {position.X},{position.Y},{position.Z}. Select a second rail of the same gauge.");
			return;
		}

		if (state.FirstRail.Position.dimension != position.dimension)
		{
			SayClient(byEntity, "Connection mode: both rails must be in the same dimension.");
			return;
		}

		if (state.FirstRail.Gauge != spec.Gauge)
		{
			SayClient(byEntity, $"Connection mode: the target must be {GaugeName(state.FirstRail.Gauge)}.");
			return;
		}

		if (SamePos(state.FirstRail.Position, position))
		{
			SayClient(byEntity, "Connection mode: select a different target rail.");
			return;
		}

		state.Gauge = spec.Gauge;
		state.SecondRail = new SelectedRail(position, block.BlockId, spec.Gauge);

		if (world.Side == EnumAppSide.Server)
		{
			if (StartConnectionJob(world, playerEntity, state, out string startedMessage, out string error))
			{
				state.IsGenerating = true;
				SayServer(byEntity, startedMessage);
			}
			else
			{
				state.IsGenerating = false;
				state.SecondRail = null;
				SayServer(byEntity, $"Rail connection failed: {error}");
			}
		}
		else
		{
			state.IsGenerating = true;
		}

		Highlight(world, playerEntity, state);
	}


	private static bool TryResolveClickedRail
	(
		IWorldAccessor world,
		BlockPos clickedPosition,
		out BlockPos railPosition,
		out Block railBlock,
		out TrackPieceSpec railSpec
	)
	{
		IBlockAccessor blockAccessor = world.BlockAccessor;
		railPosition = clickedPosition.Copy();
		railBlock = blockAccessor.GetBlock(railPosition);
		railSpec = null!;

		if (TrackSpecsDictionary.TryGet(railBlock, out railSpec)) return true;
		if (railBlock is not BlockFlowCollider) return false;

		// Follow the BPFFC cardinal pointer instead of guessing from nearby rails or graph geometry.
		for (int index = 0; index < 4096; index++)
		{
			BlockFacing? flowDirection = GetFlowDirection(railBlock);
			if (flowDirection == null) return false;

			railPosition.Add(flowDirection);
			railBlock = blockAccessor.GetBlock(railPosition);

			if (railBlock is BlockFlowCollider) continue;
			return TrackSpecsDictionary.TryGet(railBlock, out railSpec);
		}

		return false;
	}

	private static BlockFacing? GetFlowDirection(Block block)
	{
		string? direction = block.Variant?["dir"];
		if (string.IsNullOrEmpty(direction)) return null;
		return BlockFacing.FromFirstLetter(direction) ?? BlockFacing.FromCode(direction);
	}

	private static bool StartConnectionJob(IWorldAccessor world, EntityPlayer playerEntity, WandState state, out string startedMessage, out string error)
	{
		startedMessage = "";
		error = "";

		if (world.Side != EnumAppSide.Server)
		{
			error = "Client not allowed to act as server.";
			return false;
		}

		if (state.FirstRail == null || state.SecondRail == null)
		{
			error = "Select two rails first.";
			return false;
		}

		if (world.PlayerByUid(playerEntity.PlayerUID) == null)
		{
			error = "Player somehow does not exist.";
			return false;
		}

		if (!ConnectionGenerationJob.TryCreate(world, playerEntity.PlayerUID, state.FirstRail, state.SecondRail, out ConnectionGenerationJob? job, out error)) { return false; }

		ConnectionGenerationJob createdJob = job!;
		CancelActiveJob(playerEntity.PlayerUID);
		ActiveJobs[playerEntity.PlayerUID] = createdJob;
		EnsureServerTickRegistered();

		startedMessage =
			$"Rail connection search started: {GaugeName(createdJob.Gauge)} " +
			$"{FormatEndpoint(createdJob.SourceExit.Endpoint)} -> {FormatEndpoint(createdJob.TargetExit.Endpoint)}. " +
			$"Limited to {SearchNodesPerTick} nodes per tick.";
		return true;
	}

	private sealed class ConnectionGenerationJob : IRailGenerationJob
	{
		private readonly IWorldAccessor World;
		private readonly string PlayerUID;
		private readonly SelectedRail SourceRail;
		private readonly SelectedRail TargetRail;
		private readonly SegmentCache CachedSegments;
		private readonly SearchBounds SearchBounds;
		private readonly Dictionary<CellKey, CellClaim> ProtectedCells;
		private readonly SearchDebugVisualization? DebugVisualization = DebugVisualizeSearch ? new SearchDebugVisualization() : null;

		private RouteSearch? ActiveSearch;
		private int TotalNodeVisits;
		private long NextProgressMS;
		private List<Placement>? PlannedPlacements;
		private int PlacementIndex;
		private int PlacedBlockCount;

		public byte Gauge => SourceRail.Gauge;
		public RailExit SourceExit { get; }
		public RailExit TargetExit { get; }
		public bool Done { get; private set; }
		public string Message { get; private set; } = "";

		private ConnectionGenerationJob
		(
			IWorldAccessor world,
			string playerUID,
			SelectedRail sourceRail,
			SelectedRail targetRail,
			SegmentCache segmentCache,
			SearchBounds searchBounds,
			RailExit sourceExit,
			RailExit targetExit,
			Dictionary<CellKey, CellClaim> protectedCells
		)
		{
			this.World = world;
			this.PlayerUID = playerUID;
			this.SourceRail = sourceRail;
			this.TargetRail = targetRail;
			this.CachedSegments = segmentCache;
			this.SearchBounds = searchBounds;
			this.ProtectedCells = protectedCells;
			SourceExit = sourceExit;
			TargetExit = targetExit;

			List<GoalSpec> goals = new() { new GoalSpec(targetExit.Endpoint, OppositeDirection(targetExit.OutwardDirection)) };
			DebugVisualization?.SetGoals(goals);

			ActiveSearch = new RouteSearch
			(
				world,
				segmentCache,
				searchBounds,
				new NodeKey(sourceExit.Endpoint, sourceExit.OutwardDirection),
				goals,
				reservedBlocks: null, reservedCells: protectedCells,
				debugVisualization: DebugVisualization,
				enforceDirectionsForAllGauges: true, allowExistingIdenticalAnchors: false
			);
		}

		public static bool TryCreate
		(
			IWorldAccessor world,
			string playerUID,
			SelectedRail sourceRail,
			SelectedRail targetRail,
			out ConnectionGenerationJob? job,
			out string error
		)
		{
			job = null;
			error = "";

			if (sourceRail.Position.dimension != targetRail.Position.dimension)
			{
				error = "Both rails must be in the same dimension.";
				return false;
			}

			if (sourceRail.Gauge != targetRail.Gauge)
			{
				error = "Both rails must use the same gauge.";
				return false;
			}

			if (!TryResolveSelectedRail(world, sourceRail, out Block sourceBlock, out TrackPieceSpec sourceSpec, out error) || !TryResolveSelectedRail(world, targetRail, out Block targetBlock, out TrackPieceSpec targetSpec, out error)) { return false; }

			SegmentCache segmentCache = EnsureCache(world, sourceRail.Gauge);
			if (segmentCache.Segments.Count == 0)
			{
				error = $"No usable {GaugeName(sourceRail.Gauge)} track segments found.";
				return false;
			}

			RailGraphServerSystem? railSystem = world.Api.ModLoader.GetModSystem<RailGraphServerSystem>();
			if (railSystem == null || !railSystem.IsRuntimeReady)
			{
				error = "The server rail graph is not ready.";
				return false;
			}

			RailGraphLive graph = railSystem.Graph;
			if (!TryCreateRepresentativeSegment(sourceBlock, sourceSpec, out Segment? sourceRepresentative) ||
				!TryCreateRepresentativeSegment(targetBlock, targetSpec, out Segment? targetRepresentative))
			{
				error = "One of the selected rails has no usable path.";
				return false;
			}

			HashSet<CellKey> selectedFootprint = new();
			AddFootprintCells(selectedFootprint, sourceRepresentative!, sourceRail.Position);
			AddFootprintCells(selectedFootprint, targetRepresentative!, targetRail.Position);

			if (!TryFindFirstOpenExit(world, graph, segmentCache,sourceRail.Position, sourceBlock, sourceSpec, selectedFootprint, "source", out RailExit? sourceExit, out error))	{ return false; }
			if (!TryFindFirstOpenExit(world, graph, segmentCache, targetRail.Position, targetBlock, targetSpec, selectedFootprint, "target", out RailExit? targetExit, out error ))	{ return false; }

			Dictionary<CellKey, CellClaim> protectedCells = new();
			AddProtectedRailClaims(protectedCells, sourceRail.Position, sourceExit!.RepresentativeSegment);
			AddProtectedRailClaims(protectedCells, targetRail.Position, targetExit!.RepresentativeSegment);

			List<BlockPos> points = new() { sourceRail.Position, targetRail.Position };
			SearchBounds searchBounds = SearchBounds.FromPoints(points, segmentCache);

			job = new ConnectionGenerationJob(world, playerUID, sourceRail.Copy(), targetRail.Copy(), segmentCache, searchBounds, sourceExit!, targetExit!, protectedCells);
			return true;
		}

		public bool TryGetProgressMessage(out string? message)
		{
			message = null;
			long now = World.ElapsedMilliseconds;
			if (now < NextProgressMS) return false;
			NextProgressMS = now + 2000;

			if (PlannedPlacements != null)
			{
				message = $"Rail connection placing... {PlacementIndex}/{PlannedPlacements.Count} planned blocks processed in batches of {PlaceBlocksPerTick}.";
				return true;
			}

			message = $"Rail connection searching... {FormatEndpoint(SourceExit.Endpoint)} -> {FormatEndpoint(TargetExit.Endpoint)}, {TotalNodeVisits} node visits.";
			return true;
		}

		public void Step(int nodeBudget)
		{
			if (Done) return;
			if (PlannedPlacements != null)
			{
				StepPlacement(PlaceBlocksPerTick);
				return;
			}

			if (ActiveSearch == null)
			{
				Done = true;
				Message = "Rail connection failed: Search state was unexpectedly unavailable.";
				return;
			}

			SearchStatus status = ActiveSearch.Step(nodeBudget);
			TotalNodeVisits += ActiveSearch.LastStepVisitCount;

			if (status == SearchStatus.Running) return;
			if (status == SearchStatus.Failed || ActiveSearch.Result == null)
			{
				Done = true;
				Message = $"Rail connection failed: No route from {FormatEndpoint(SourceExit.Endpoint)} to {FormatEndpoint(TargetExit.Endpoint)} after {TotalNodeVisits} node visits.";
				return;
			}

			Route route = ActiveSearch.Result;
			ActiveSearch = null;
			FinishWithRoute(route);
		}

		private void FinishWithRoute(Route route)
		{
			if (!ValidateSelectedRailsAndExits(out string validationError))
			{
				Done = true;
				Message = $"Rail connection failed: {validationError}";
				return;
			}

			if (!BuildPlan(route, out List<Placement> plan, out string error))
			{
				Done = true;
				Message = $"Rail connection failed: {error}";
				return;
			}

			IPlayer? player = World.PlayerByUid(PlayerUID);
			if (player == null)
			{
				Done = true;
				Message = "Rail connection failed: Player somehow unavailable.";
				return;
			}

			for (int index = 0; index < plan.Count; index++)
			{
				Placement placement = plan[index];
				string failure = "";

				if (!IsSegmentClear(World, placement.Segment, placement.Position, allowExistingIdenticalAnchor: false))
				{
					Done = true;
					Message =
						$"Rail connection failed: Blocked clearance, occupied rail, or missing support at " +
						$"{placement.Position.X},{placement.Position.Y},{placement.Position.Z}.";
					return;
				}

				if (!CanPlaceBlockIgnoringFlora(World, player, placement, ref failure))
				{
					Done = true;
					Message = $"Rail connection failed: {failure} at {placement.Position.X},{placement.Position.Y},{placement.Position.Z}.";
					return;
				}
			}

			PlannedPlacements = plan;
			PlacementIndex = 0;
			PlacedBlockCount = 0;
		}

		private bool ValidateSelectedRailsAndExits(out string error)
		{
			error = "";

			if (!TryResolveSelectedRail(World, SourceRail, out Block sourceBlock, out TrackPieceSpec sourceSpec, out error) || !TryResolveSelectedRail(World, TargetRail, out Block targetBlock, out TrackPieceSpec targetSpec, out error)) { return false; }

			RailGraphServerSystem? railSystem = World.Api.ModLoader.GetModSystem<RailGraphServerSystem>();
			if (railSystem == null || !railSystem.IsRuntimeReady)
			{
				error = "The server rail graph is no longer ready.";
				return false;
			}

			if (!IsSpecificExitOpen(railSystem.Graph, SourceRail.Position, sourceBlock, sourceSpec, SourceExit.GraphEndpoint, out error))
			{
				error = string.IsNullOrEmpty(error) ? "The source exit became connected during route generation." : error;
				return false;
			}

			if (!IsSpecificExitOpen(railSystem.Graph, TargetRail.Position, targetBlock, targetSpec, TargetExit.GraphEndpoint, out error))
			{
				error = string.IsNullOrEmpty(error) ? "The target exit became connected during route generation." : error;
				return false;
			}

			return true;
		}

		public void FlushDebugVisuals()
		{
			if (DebugVisualization == null || !DebugVisualization.WantsFlush(World)) return;
			if (ActiveSearch != null) DebugVisualization.SetBest(ActiveSearch.GetBestRoute(), ActiveSearch.GetBestReverseRoute());
			if (World.PlayerByUid(PlayerUID) is IPlayer player) DebugVisualization.Flush(World, player, force: false);
		}

		public void ClearDebugVisuals()
		{
			if (DebugVisualization == null) return;
			if (World.PlayerByUid(PlayerUID) is IPlayer player) DebugVisualization.Clear(World, player);
		}

		private void StepPlacement(int blockBudget)
		{
			if (PlannedPlacements == null) return;

			IPlayer? player = World.PlayerByUid(PlayerUID);
			if (player == null)
			{
				Done = true;
				Message = "Rail connection failed: Player somehow unavailable.";
				return;
			}

			IBlockAccessor blockAccessor = World.BlockAccessor;
			int worked = 0;

			while (PlacementIndex < PlannedPlacements.Count && worked < blockBudget)
			{
				Placement placement = PlannedPlacements[PlacementIndex++];
				string failure = "";

				RemoveIgnoredFloraForSegment(World, placement.Segment, placement.Position);
				BlockSelection selection = MakeSelection(blockAccessor, placement.Position);

				if (!IsSegmentClear(World, placement.Segment, placement.Position, allowExistingIdenticalAnchor: false))
				{
					Done = true;
					Message =
						$"Rail connection failed during placement after placing {PlacedBlockCount} blocks: " +
						$"blocked clearance, occupied rail, or missing support at " +
						$"{placement.Position.X},{placement.Position.Y},{placement.Position.Z}.";
					return;
				}

				if (!CanPlaceBlockIgnoringFlora(World, player, placement, ref failure))
				{
					Done = true;
					Message =
						$"Rail connection failed during placement after placing {PlacedBlockCount} blocks: " +
						$"{failure} at {placement.Position.X},{placement.Position.Y},{placement.Position.Z}.";
					return;
				}

				if (!placement.Block.DoPlaceBlock(World, player, selection, new ItemStack(placement.Block)))
				{
					Done = true;
					Message =
						$"Rail connection failed during placement after placing {PlacedBlockCount} blocks: " +
						$"DoPlaceBlock returned false; planned={DescribeBlock(placement.Block)} at " +
						$"{placement.Position.X},{placement.Position.Y},{placement.Position.Z}.";
					return;
				}

				SpawnFlowFieldColliders(World, placement.Segment, placement.Position);
				PlacedBlockCount++;
				worked++;
			}

			if (PlacementIndex < PlannedPlacements.Count) return;

			Done = true;
			Message = PlacedBlockCount > 0
				? $"Rail connection complete: placed {PlacedBlockCount} {GaugeName(Gauge)} blocks after {TotalNodeVisits} node visits."
				: "Rail connection failed: Route produced no new placements.";
		}
	}

	private static bool TryResolveSelectedRail
	(
		IWorldAccessor world,
		SelectedRail selected,
		out Block block,
		out TrackPieceSpec spec,
		out string error
	)
	{
		block = world.BlockAccessor.GetBlock(selected.Position);
		spec = null!;
		error = "";

		if (block.BlockId != selected.BlockID)
		{
			error = $"The selected rail at {selected.Position.X},{selected.Position.Y},{selected.Position.Z} changed.";
			return false;
		}

		if (!TrackSpecsDictionary.TryGet(block, out spec))
		{
			error = $"The selected block at {selected.Position.X},{selected.Position.Y},{selected.Position.Z} is no longer a rail.";
			return false;
		}

		if (spec.Gauge != selected.Gauge)
		{
			error = $"The selected rail at {selected.Position.X},{selected.Position.Y},{selected.Position.Z} changed gauge.";
			return false;
		}

		return true;
	}

	private static bool TryCreateRepresentativeSegment(Block block, TrackPieceSpec spec, out Segment? segment)
	{
		for (int index = 0; index < spec.Paths.Length; index++)
		{
			Vec3f[] points = spec.Paths[index].LocalPoints;
			if (points == null || points.Length < 2) continue;

			segment = new Segment(block, spec.Gauge, spec.MaxSpeedFactor01, points, SegmentCache.ReadFlowCells(block));
			return true;
		}

		segment = null;
		return false;
	}

	private static bool TryFindFirstOpenExit
	(
		IWorldAccessor world,
		RailGraphLive graph,
		SegmentCache segmentCache,
		BlockPos anchor,
		Block block,
		TrackPieceSpec spec,
		HashSet<CellKey> selectedFootprint,
		string label,
		out RailExit? exit,
		out string error
	)
	{
		exit = null;
		error = "";

		if (!TryBuildOwnerEndpointCounts(graph, anchor, block, spec, out Dictionary<RailGraphLive.EndpointKey, int> ownerCounts, out error)) { return false; }

		List<RailExit> candidates = BuildOrderedRailExits(anchor, block, spec);
		if (candidates.Count == 0)
		{
			error = $"The {label} rail has no usable endpoints.";
			return false;
		}

		for (int index = 0; index < candidates.Count; index++)
		{
			RailExit candidate = candidates[index];
			if (!ownerCounts.TryGetValue(candidate.GraphEndpoint, out int ownIncidentCount)) continue;

			int totalIncidentCount = graph.GetIncidentEdgeCount(candidate.GraphEndpoint);
			if (totalIncidentCount < ownIncidentCount)
			{
				error = $"The rail graph is incomplete at {FormatEndpoint(candidate.Endpoint)}.";
				return false;
			}

			if (totalIncidentCount != ownIncidentCount) continue;
			if (!CanDepartFromExit(world, segmentCache, candidate, selectedFootprint)) continue;

			exit = candidate;
			return true;
		}

		error = $"The {label} rail has no open, unobstructed exit.";
		return false;
	}

	private static bool TryBuildOwnerEndpointCounts
	(
		RailGraphLive graph,
		BlockPos anchor,
		Block block,
		TrackPieceSpec spec,
		out Dictionary<RailGraphLive.EndpointKey, int> ownerCounts,
		out string error
	)
	{
		ownerCounts = new Dictionary<RailGraphLive.EndpointKey, int>();
		error = "";

		List<ulong> edgeHashes = new();
		if (graph.CollectSpecEdgeHashesAt(anchor, spec, edgeHashes) == 0)
		{
			error = $"The selected rail at {anchor.X},{anchor.Y},{anchor.Z} is missing from the rail graph.";
			return false;
		}

		for (int index = 0; index < edgeHashes.Count; index++)
		{
			ulong hash = edgeHashes[index];
			if (!graph.TryGetEdgeOwner(hash, out BlockPos ownerPosition, out int ownerBlockID, out byte ownerGauge) ||  !SamePos(ownerPosition, anchor) || ownerBlockID != block.BlockId || ownerGauge != spec.Gauge)
			{
				error = $"The rail graph is stale at {anchor.X},{anchor.Y},{anchor.Z}.";
				return false;
			}

			if (!graph.TryGetEdgeEndpoints(hash, out RailGraphLive.EndpointKey a, out RailGraphLive.EndpointKey b, out byte gauge) || gauge != spec.Gauge)
			{
				error = $"The rail graph has invalid edge data at {anchor.X},{anchor.Y},{anchor.Z}.";
				return false;
			}

			Increment(ownerCounts, a);
			if (!a.Equals(b)) Increment(ownerCounts, b);
		}

		if (ownerCounts.Count == 0)
		{
			error = $"The selected rail at {anchor.X},{anchor.Y},{anchor.Z} has no graph endpoints.";
			return false;
		}

		return true;

		static void Increment(Dictionary<RailGraphLive.EndpointKey, int> counts, RailGraphLive.EndpointKey endpoint)
		{
			counts.TryGetValue(endpoint, out int count);
			counts[endpoint] = count + 1;
		}
	}

	private static bool IsSpecificExitOpen
	(
		RailGraphLive graph,
		BlockPos anchor,
		Block block,
		TrackPieceSpec spec,
		RailGraphLive.EndpointKey endpoint,
		out string error
	)
	{
		if (!TryBuildOwnerEndpointCounts(graph, anchor, block, spec, out Dictionary<RailGraphLive.EndpointKey, int> ownerCounts, out error)) { return false; }

		if (!ownerCounts.TryGetValue(endpoint, out int ownIncidentCount) || ownIncidentCount <= 0)
		{
			error = $"The chosen endpoint {FormatGraphEndpoint(endpoint)} no longer belongs to the selected rail.";
			return false;
		}

		int totalIncidentCount = graph.GetIncidentEdgeCount(endpoint);
		if (totalIncidentCount < ownIncidentCount)
		{
			error = $"The rail graph is incomplete at {FormatGraphEndpoint(endpoint)}.";
			return false;
		}

		return totalIncidentCount == ownIncidentCount;
	}

	private static List<RailExit> BuildOrderedRailExits(BlockPos anchor, Block block, TrackPieceSpec spec)
	{
		List<RailExit> exits = new();
		HashSet<RailExitKey> seen = new();
		List<FlowCell> flowCells = SegmentCache.ReadFlowCells(block);

		for (int pathIndex = 0; pathIndex < spec.Paths.Length; pathIndex++)
		{
			Vec3f[] points = spec.Paths[pathIndex].LocalPoints;
			if (points == null || points.Length < 2) continue;

			Segment segment = new(block, spec.Gauge, spec.MaxSpeedFactor01, points, flowCells);
			Add(segment.A, OppositeDirection(segment.StartDir(forward: true)), segment);
			Add(segment.B, segment.EndDir(forward: true), segment);
		}

		return exits;

		void Add(Endpoint localEndpoint, byte outwardDirection, Segment segment)
		{
			Endpoint worldEndpoint = WorldEndpoint(anchor, localEndpoint);
			RailGraphLive.EndpointKey graphEndpoint = new
			(
				worldEndpoint.X, worldEndpoint.Y, worldEndpoint.Z,
				worldEndpoint.Dimension,
				spec.Gauge
			);
			RailExitKey key = new(graphEndpoint, outwardDirection);
			if (!seen.Add(key)) return;

			exits.Add(new RailExit(worldEndpoint, graphEndpoint, outwardDirection, segment));
		}
	}

	private static bool CanDepartFromExit( IWorldAccessor world, SegmentCache segmentCache, RailExit exit, HashSet<CellKey> selectedFootprint)
	{
		for (int index = 0; index < segmentCache.Segments.Count; index++)
		{
			Segment segment = segmentCache.Segments[index];

			if (CanUse(segment, forward: true) || CanUse(segment, forward: false)) return true;

			bool CanUse(Segment candidate, bool forward)
			{
				if (candidate.StartDir(forward) != exit.OutwardDirection) return false;
				if (!TryStep(exit.Endpoint, candidate, forward, out _, out BlockPos anchor)) return false;

				foreach (CellKey cell in FootprintCells(candidate, anchor)) { if (selectedFootprint.Contains(cell)) return false; }
				return IsSegmentClear(world, candidate, anchor, allowExistingIdenticalAnchor: false);
			}
		}

		return false;
	}

	private static void AddFootprintCells(HashSet<CellKey> cells, Segment segment, BlockPos anchor) { foreach (CellKey cell in FootprintCells(segment, anchor)) cells.Add(cell); }
	private static void AddProtectedRailClaims(Dictionary<CellKey, CellClaim> claims, BlockPos anchor, Segment segment) { foreach (CellKey cell in FootprintCells(segment, anchor)) { if (!claims.ContainsKey(cell)) { claims[cell] = new CellClaim(anchor, ProtectedRailBlockID, segment); } } }

	private static string FormatEndpoint(Endpoint endpoint) { return $"{endpoint.X / 16.0:0.###},{endpoint.Y / 16.0:0.###},{endpoint.Z / 16.0:0.###}"; }
	private static string FormatGraphEndpoint(RailGraphLive.EndpointKey endpoint) { return $"{endpoint.X16 / 16.0:0.###},{endpoint.Y16 / 16.0:0.###},{endpoint.Z16 / 16.0:0.###}"; }

	private sealed class SelectedRail
	{
		public readonly BlockPos Position;
		public readonly int BlockID;
		public readonly byte Gauge;

		public SelectedRail(BlockPos position, int blockID, byte gauge)
		{
			Position = position.Copy();
			BlockID = blockID;
			Gauge = gauge;
		}

		public SelectedRail Copy() => new(Position, BlockID, Gauge);
	}

	private sealed class RailExit
	{
		public readonly Endpoint Endpoint;
		public readonly RailGraphLive.EndpointKey GraphEndpoint;
		public readonly byte OutwardDirection;
		public readonly Segment RepresentativeSegment;

		public RailExit(Endpoint endpoint, RailGraphLive.EndpointKey graphEndpoint, byte outwardDirection, Segment representativeSegment)
		{
			Endpoint = endpoint;
			GraphEndpoint = graphEndpoint;
			OutwardDirection = outwardDirection;
			RepresentativeSegment = representativeSegment;
		}
	}

	private readonly struct RailExitKey : IEquatable<RailExitKey>
	{
		private readonly RailGraphLive.EndpointKey Endpoint;
		private readonly byte OutwardDirection;

		public RailExitKey(RailGraphLive.EndpointKey endpoint, byte outwardDirection)
		{
			this.Endpoint = endpoint;
			this.OutwardDirection = outwardDirection;
		}

		public bool Equals(RailExitKey other) => Endpoint.Equals(other.Endpoint) && OutwardDirection == other.OutwardDirection;
		public override bool Equals(object? obj) => obj is RailExitKey other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(Endpoint, OutwardDirection);
	}
	#endregion
}

