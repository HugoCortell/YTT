using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using InteractionRangeHack; // Hope to get rid of this some day

namespace YangTransport;

/// Standard gauge rolling stock (gauge 1). Locomotives, boxcars, passenger wagons, and future SG variants all share this entity.
/// Powered stock is identified by having EntityBehaviorSteamEngineCart, unpowered stock follows convoys only.
public sealed partial class EntityStandardGaugeLocomotive : Entity, ISeatInstSupplier, IRailwayConvoyVehicle, IEntityInteractionRangeProvider, IRailLivingCollisionSource
{
	public override double FrustumSphereRadius => Math.Max(base.FrustumSphereRadius, VehicleLength + 4.0);

	public float EntityInteractionRange => ConfiguredEntityInteractionRange; // Hack fix
	public override bool InRangeOf(Vec3d position, float horizontalRangeSQ, float verticalRange)
	{
		if (base.InRangeOf(position, horizontalRangeSQ, verticalRange)) return true;

		double paddedRange = Math.Sqrt(Math.Max(0f, horizontalRangeSQ)) + Math.Max(0.0, VehicleLength + 2.0);
		return Pos.InRangeOf(position, (float)(paddedRange * paddedRange), verticalRange + 2f);
	}

	private static readonly KinematicDrive.Parameters DefaultDriveParameters = new
	(
		HardMaxSpeed: 200,
		BaseResistance: 0.45,
		WeightResistance: 0.05,
		BrakeDeceleration: 12.0,
		StopEpsilon: 0.02
	);

	private const byte TrackGauge = 1;
	private const int BindRadiusBlocks = 256;
	private KinematicDrive.Parameters DriveParameters = DefaultDriveParameters;
	private float ReverseMaxSpeedMultiplier = 0.4f;

	private EntityBehaviorSteamPowered? SteamEngineBehaviour;
	private EntityBehaviorStandardGaugeLocomotiveStats? LocomotiveStatistics;
	private EntityBehaviorSeatable? SeatableBehaviour;
	private EntityBehaviorSGStorage? StorageBehaviour;
	private EntityBehaviorAttachable? ConductorAttachableBehaviour;

	private int SelfWeight = 5;
	private double VehicleLength = 6.0;
	private float ConfiguredEntityInteractionRange = 11.25f;
	private double BodyOffsetForward = 0.0;
	private const double BodyShapeForwardOffsetBlocks = 1.0;
	private double FrontBogieOffset = 0.0;
	private double RearBogieOffset = 4.0;
	private SGTrainEnd LeadEnd = SGTrainEnd.EndA;

	public const double CouplingDistance = 1.5; // Gap between carts

	private RailwayVehicleShared.RailCursor Cursor;
	private double Speed; // non-negative, sgLeadEnd chooses signed travel direction
	private double TravelledABS;
	private ConvoyRoute? PathTape;
	private readonly ConvoyRouteRecorder PathTapeRecorder = new();
	private double PathHeadDistance;
	private const double PathTapeSeedBaseDistance = 128.0;
	private const double AuthoritativeRouteReserveBlocks = RailTrainCollisionSystem.RouteHistoryRequiredBlocks;
	private const double ClientRouteLookaheadBaseBlocks = 32.0;
	private const double ClientRouteLookaheadSeconds = 2.5;
	private const double ClientRouteLookaheadMaxBlocks = 512.0;

	private readonly List<ulong> BindCandidates = new(256);
	private readonly List<IRailwayConvoyVehicle> ConvoyMemberScratch = new(16);
	private readonly List<StagedMaterialPose> MaterialPoseScratch = new(16);
	private readonly List<long> MaterialChunkScratch = new(4);
	private RailGraphServerSystem? RailSystem;
	private RailConvoySystem? ConvoySystem;
	private RailAutomationPathingSystem? AutomationSystem;
	private ConvoyRouteNetworkSystem? RouteNetworkSystem;
	private OffscreenConvoySimSystem? OffscreenSimulationSystem;

	private readonly struct StagedMaterialPose
	{
		public readonly EntityStandardGaugeLocomotive Vehicle;
		public readonly RailwayVehicleShared.RailCursor Cursor;
		public readonly double SourceDistance;
		public readonly double X, Y, Z;
		public readonly float Yaw;
		public readonly float Roll;
		public readonly long ChunkIndex;

		public StagedMaterialPose
		(
			EntityStandardGaugeLocomotive vehicle, RailwayVehicleShared.RailCursor cursor,
			double sourceDistance,
			double x, double y, double z,
			float yaw, float roll,
			long chunkIndex
		)
		{
			Vehicle = vehicle;
			Cursor = cursor;
			SourceDistance = sourceDistance;
			X = x; Y = y; Z = z;
			Yaw = yaw;
			Roll = roll;
			ChunkIndex = chunkIndex;
		}
	}

	private const string DerailedAttribute = "derailed";
	private const string SeedYawAttribute = "seedYaw";
	internal const string SGLeadEndAttribute = "sgLeadEnd";
	internal const string SGPathHeadDistanceAttribute = "sgPathHeadS";
	internal const string SGPathSourceDistanceAttribute = "sgPathSourceS";
	internal const string SGRouteEpochAttribute = "sgRouteEpoch";
	private bool Derailed;
	private readonly Vec3d DerailedVelocityBPS = new();
	private float DerailedYawVelocityRadians;
	private readonly Vec3d DerailedNewPositionScratch = new();
	private readonly BlockPos BelowPositionScratch = new();
	private readonly BlockPos DeadEndPositionScratch = new();

	private const double DerailedNudgeForward = 0.40, DerailedNudgeSide = 0.16;

	private long ConvoyHeadID; // 0 = free, else simulated head entity id
	private int ConvoyIndex;
	private long PreviousVehicleIDReadOnly;
	private long NextVehicleIDReadOnly;
	private double ConvoyDistanceBehindHeadBlocks;
	private EntityStandardGaugeLocomotive? ConvoyHeadReference;
	public long PrevVehicleID => PreviousVehicleIDReadOnly;
	internal long NextVehicleID => NextVehicleIDReadOnly;

	private const string ConvoyHeadIDAttribute = "convoyHeadId";
	private const string ConvoyIndexAttribute = "convoyIndex";
	private const string PreviousVehicleIDAttribute = "prevCartId"; // shared watched key for tether visuals
	private const string NextVehicleIDAttribute = "nextCartId";
	private const string ConvoyDistanceBehindHeadAttribute = "convoyDistanceBehindHead";
	private const string ConvoyWeightAttribute = "convoyWeight";
	private const string ConvoyCountAttribute = "convoyCount";
	private const string ConvoyTailDistanceAttribute = "convoyTailDistance";

	private struct ConvoyStaticStats { public int Count; public int Weight; public long TailID; public double TailDistance; }
	private double OccupancyRearDistanceCached;

	// Living collision (server head only)
	// SG vehicles are long, so we sweep a cheap 2-wide, 3-tall leading wall for each vehicle
	// instead of relying on the entitys useless 1x1 source position or doing expensive per-sample world queries.
	private const double LivingMinSpeedBPS			= 0.2;
	private const double LivingCheckTravelStep		= 0.75;
	private const double LivingMaxSweepBlocks		= 16.0;
	private const double LivingWallHalfWidth		= 1.0;
	private const double LivingWallHeight			= 3.0;
	private const double LivingWallBottomOffset		= -0.15;

	private double LastLivingCheckAbsoluteTravel;
	private double LastLivingCheckPathHeadDistance;
	private bool LivingCollisionSweepInitialized;
	private RailLivingCollisionSystem? LivingCollisionSystem;

	private const long MovingPersistenceIntervalMS = 1000;
	private long NextMovingPersistenceMS;
	private bool TrackStateDirty;
	private bool ConvoyBodyDirty = true;

	internal bool DebugHasTractionEngine => SteamEngineBehaviour != null;
	internal double DebugVehicleLength => VehicleLength;
	internal double DebugBodyOffsetForward => BodyOffsetForward;
	internal SGTrainEnd DebugLeadEnd => LeadEnd;
	internal long DebugOccupancyOwnerID => ConvoyHeadID != 0 ? ConvoyHeadID : EntityId;
	internal ulong DebugRailEdgeHash => Cursor.SegmentHash;

	internal bool DebugTryGetRailCursor(out RailwayVehicleShared.RailCursor cursor)
	{
		cursor = Cursor;
		return Cursor.SegmentHash != 0 && Cursor.PolyXYZ16 != null && Cursor.PointCount >= 2;
	}

	internal bool IsFollower => ConvoyHeadID != 0 && ConvoyHeadID != EntityId;
	internal bool IsConvoyHead => ConvoyHeadID != 0 && ConvoyHeadID == EntityId;

	internal long ClientRenderConvoyHeadID
	{
		get
		{
			long headID = WatchedAttributes.GetLong(ConvoyHeadIDAttribute, ConvoyHeadID);
			return headID != 0 ? headID : EntityId;
		}
	}

	internal double ClientRenderDistanceBehindHead => WatchedAttributes.GetDouble(ConvoyDistanceBehindHeadAttribute, ConvoyDistanceBehindHeadBlocks);
	internal int ClientRenderRouteEpoch => WatchedAttributes.GetInt(SGRouteEpochAttribute, 0);
	internal double ServerPathHeadDistance => PathHeadDistance;


	public override void Initialize(EntityProperties properties, ICoreAPI coreAPI, long chunkIndex3D)
	{
		base.Initialize(properties, coreAPI, chunkIndex3D);

		Cursor = default;
		Cursor.Gauge = TrackGauge;
		Cursor.Direction = 1;
		Cursor.NormalizedSegmentProgress = 0.5;

		SteamEngineBehaviour = GetBehavior<EntityBehaviorSteamPowered>();
		LocomotiveStatistics = GetBehavior<EntityBehaviorStandardGaugeLocomotiveStats>();
		SeatableBehaviour = GetBehavior<EntityBehaviorSeatable>();
		StorageBehaviour = GetBehavior<EntityBehaviorSGStorage>();
		ConductorAttachableBehaviour = GetBehavior<EntityBehaviorAttachable>();
		if (coreAPI.Side == EnumAppSide.Server) LivingCollisionSystem = coreAPI.ModLoader.GetModSystem<RailLivingCollisionSystem>();

		if (SeatableBehaviour != null) { SeatableBehaviour.CanSit += CanSitOnlyOnSeatSelection; }

		if (LocomotiveStatistics != null)
		{
			SelfWeight = LocomotiveStatistics.WeightSpecification.ComputeTotalWeight(ConductorAttachableBehaviour);
			VehicleLength = LocomotiveStatistics.VehicleLength;
			BodyOffsetForward = LocomotiveStatistics.BodyOffsetForward;
			FrontBogieOffset = LocomotiveStatistics.FrontBogieOffset;
			RearBogieOffset = LocomotiveStatistics.RearBogieOffset;
			DriveParameters = LocomotiveStatistics.DriveParameters;
			ReverseMaxSpeedMultiplier = LocomotiveStatistics.ReverseMaxSpeedMultiplier;
		}
		ConfiguredEntityInteractionRange = Math.Max(ConfiguredEntityInteractionRange, (float)(VehicleLength + 7));

		OccupancyRearDistanceCached = ComputeOccupancyRearExtentBlocks();

		Cursor.SegmentHash = unchecked((ulong)Attributes.GetLong("segHash", 0));
		Cursor.SegmentIndex = Attributes.GetInt("segIndex", 0);
		Cursor.NormalizedSegmentProgress = Attributes.GetDouble("segT01", 0.5);
		Cursor.Direction = Attributes.GetInt("dir", 1) >= 0 ? 1 : -1;
		LeadEnd = SGTrainEndUtil.FromInt(Attributes.GetInt(SGLeadEndAttribute, (int)SGTrainEnd.EndA));
		Speed = Math.Abs(Attributes.GetDouble("speed", 0));
		TravelledABS = Attributes.GetDouble("travelledAbs", 0);
		PathHeadDistance = Attributes.GetDouble(SGPathHeadDistanceAttribute, Math.Max(PathTapeSeedBaseDistance, TravelledABS));
		Cursor.BoundGraphVersion = -1;

		ConvoyHeadID = Attributes.GetLong(ConvoyHeadIDAttribute, 0);
		ConvoyIndex = Attributes.GetInt(ConvoyIndexAttribute, 0);
		PreviousVehicleIDReadOnly = Attributes.GetLong(PreviousVehicleIDAttribute, 0);
		NextVehicleIDReadOnly = Attributes.GetLong(NextVehicleIDAttribute, 0);
		ConvoyDistanceBehindHeadBlocks = Attributes.GetDouble(ConvoyDistanceBehindHeadAttribute, Math.Max(0, ConvoyIndex) * ConvoySpacingForLength(VehicleLength));

		if (coreAPI.Side == EnumAppSide.Server)
		{
			WatchedAttributes.SetInt(SGLeadEndAttribute, (int)LeadEnd);
			WatchedAttributes.MarkPathDirty(SGLeadEndAttribute);

			// Client tether/highlight visuals read prevCartId from WatchedAttributes.
			// Mirror persisted convoy state on load as well as during live convoy mutations.
			WatchedAttributes.SetLong(PreviousVehicleIDAttribute, PreviousVehicleIDReadOnly);
			WatchedAttributes.MarkPathDirty(PreviousVehicleIDAttribute);

			WatchedAttributes.SetLong(ConvoyHeadIDAttribute, ConvoyHeadID);
			WatchedAttributes.SetDouble(ConvoyDistanceBehindHeadAttribute, ConvoyDistanceBehindHeadBlocks);
			WatchedAttributes.MarkPathDirty(ConvoyHeadIDAttribute);
			WatchedAttributes.MarkPathDirty(ConvoyDistanceBehindHeadAttribute);

			// Client tether/highlight visuals read nextCartId from WatchedAttributes.
			WatchedAttributes.SetLong(NextVehicleIDAttribute, NextVehicleIDReadOnly);
			WatchedAttributes.MarkPathDirty(NextVehicleIDAttribute);
		}

		if (coreAPI.Side == EnumAppSide.Client)
		{
			PreviousVehicleIDReadOnly = WatchedAttributes.GetLong(PreviousVehicleIDAttribute, PreviousVehicleIDReadOnly);
			NextVehicleIDReadOnly = WatchedAttributes.GetLong(NextVehicleIDAttribute, NextVehicleIDReadOnly);
			LeadEnd = SGTrainEndUtil.FromInt(WatchedAttributes.GetInt(SGLeadEndAttribute, (int)LeadEnd));
			WatchedAttributes.RegisterModifiedListener(PreviousVehicleIDAttribute, () => PreviousVehicleIDReadOnly = WatchedAttributes.GetLong(PreviousVehicleIDAttribute, 0));
			WatchedAttributes.RegisterModifiedListener(NextVehicleIDAttribute, () => NextVehicleIDReadOnly = WatchedAttributes.GetLong(NextVehicleIDAttribute, 0));
			WatchedAttributes.RegisterModifiedListener(ConvoyHeadIDAttribute, () => ConvoyHeadID = WatchedAttributes.GetLong(ConvoyHeadIDAttribute, 0));
			WatchedAttributes.RegisterModifiedListener(ConvoyDistanceBehindHeadAttribute, () => ConvoyDistanceBehindHeadBlocks = WatchedAttributes.GetDouble(ConvoyDistanceBehindHeadAttribute, 0));
			WatchedAttributes.RegisterModifiedListener(SGLeadEndAttribute, () => LeadEnd = SGTrainEndUtil.FromInt(WatchedAttributes.GetInt(SGLeadEndAttribute, (int)SGTrainEnd.EndA)));
		}

		Derailed = Attributes.GetBool(DerailedAttribute, false);
		if (Derailed)
		{
			Cursor.ClearBinding();
			StopRailMotion();
			DerailedVelocityBPS.Set(0, 0, 0);
			DerailedYawVelocityRadians = 0f;
			ServerPos.Roll = 0f;
			ServerPos.Pitch = 0f;

			if (coreAPI.Side == EnumAppSide.Server)
			{
				ServerClearConvoyState();
				WatchedAttributes.SetBool(DerailedAttribute, true);
				WatchedAttributes.MarkPathDirty(DerailedAttribute);
			}

			return;
		}

		if (coreAPI.Side == EnumAppSide.Server)
		{
			RailSystem = coreAPI.ModLoader.GetModSystem<RailGraphServerSystem>();
			ConvoySystem = coreAPI.ModLoader.GetModSystem<RailConvoySystem>();
			RouteNetworkSystem = coreAPI.ModLoader.GetModSystem<ConvoyRouteNetworkSystem>();

			if (ConductorAttachableBehaviour != null) { WatchedAttributes.RegisterModifiedListener("wearablesInv", () => { RecalculateSelfWeight(); }); }

			TryEnsureBound(forceRebind: Cursor.SegmentHash == 0);
			if (!IsFollower) { WriteWorldPosFromTrack(); }
		}

	}

	#region Tessellation
	public override void OnTesselation(ref Shape entityShape, string shapePathForLogging)
	{
		base.OnTesselation(ref entityShape, shapePathForLogging);
		RailwayVehicleShared.ApplyLeverTessellation(ref entityShape, WatchedAttributes);
	}
	#endregion

	public override void OnEntityLoaded()
	{
		base.OnEntityLoaded();

		if (Api.Side != EnumAppSide.Server) return;
		RecalculateSelfWeight(notifyConvoy: false);
		ServerRequestConvoyBodyReapply();
	}

	public override void OnEntitySpawn()
	{
		base.OnEntitySpawn();

		if (Api.Side != EnumAppSide.Server) return;

		RecalculateSelfWeight(notifyConvoy: false);
		if (Derailed) return;

		RailSystem ??= Api.ModLoader.GetModSystem<RailGraphServerSystem>();
		ConvoySystem ??= Api.ModLoader.GetModSystem<RailConvoySystem>();

		TryEnsureBound(forceRebind: Cursor.SegmentHash == 0);

		if (!IsFollower && Cursor.SegmentHash != 0 && Attributes.HasAttribute(SeedYawAttribute))
		{
			float seedYaw = Attributes.GetFloat(SeedYawAttribute, ServerPos.Yaw);
			RailwayVehicleShared.ChooseForwardDirectionFromYaw(seedYaw, ref Cursor);
			Attributes.RemoveAttribute(SeedYawAttribute);
		}

		if (!IsFollower)
		{
			WriteWorldPosFromTrack();
			PersistTrackState();
		}
		ServerRequestConvoyBodyReapply();
	}

	public override void OnEntityDespawn(EntityDespawnData despawn)
	{
		if (Api?.Side == EnumAppSide.Server)
		{
			if (TrackStateDirty) PersistTrackState(force: true);
			if (!IsFollower) RemovePublishedCollisionBody();
			if (!IsFollower)
			{
				RouteNetworkSystem ??= Api.ModLoader.GetModSystem<ConvoyRouteNetworkSystem>();
				RouteNetworkSystem?.RemoveServerRoute(EntityId, this);
			}
			if (despawn?.Reason != EnumDespawnReason.Unload || Derailed) ServerVacateOccupancyZone();
		}
		base.OnEntityDespawn(despawn);
	}

	public override string GetName()
	{
		string baseName = base.GetName();

		if (!(Api?.Side == EnumAppSide.Client ? WatchedAttributes.GetBool(DerailedAttribute, false) : Derailed)) return baseName;
		return $"{Lang.Get("yangtransport:locomotive-state-derailed")} {baseName}";
	}

	public override string GetInfoText()
	{
		string baseText = base.GetInfoText();
		StringBuilder informationBuilder = new StringBuilder(baseText);
		if (informationBuilder.Length > 0 && informationBuilder[informationBuilder.Length - 1] != '\n') informationBuilder.AppendLine();

		EntityStandardGaugeLocomotive? speedSource = Api.Side == EnumAppSide.Client && IsFollower
			? Api.World.GetEntityById(ConvoyHeadID) as EntityStandardGaugeLocomotive : this;
		double speed = Api.Side == EnumAppSide.Client
			? RailwayVehicleShared.GetMotionSpeedBPS(speedSource ?? this) : Math.Abs(Speed);
		float trackPenalty = Api.Side == EnumAppSide.Client
			? WatchedAttributes.GetFloat(RailwayVehicleShared.AttributeTrackPenalty, 1f) : Cursor.SegmentMaxSpeedFactor;

		int count = Api.Side == EnumAppSide.Client ? WatchedAttributes.GetInt(ConvoyCountAttribute, 1) : GetConvoyStaticStats().Count;
		int weight = Api.Side == EnumAppSide.Client ? WatchedAttributes.GetInt(ConvoyWeightAttribute, SelfWeight) : GetConvoyStaticStats().Weight;

		bool showEngine = SteamEngineBehaviour != null && (!IsFollower || count <= 1);
		RailwayVehicleShared.AppendLocomotiveHUDInfo(informationBuilder, this, SteamEngineBehaviour, speed, weight, showEngine, ConvoyIndex, trackPenalty);
		if (RailSystemDebug.ClientAutomationDebugHUDEnabled) RailSystemDebug.AppendAutomationDebugHUDInfo(this, informationBuilder);
		return informationBuilder.ToString();
	}

	public override void Die(EnumDespawnReason reason = EnumDespawnReason.Death, DamageSource damageSourceForDeath = null)
	{
		if (!Alive) return;

		// Client: accept server-driven state.
		if (Api.Side != EnumAppSide.Server) { base.Die(reason, damageSourceForDeath); return; }

		// Preserve unload semantics for streaming.
		if (reason == EnumDespawnReason.Unload) { base.Die(reason, damageSourceForDeath); return; }

		if (reason == EnumDespawnReason.Death)
		{
			if (RailwayVehicleShared.IsImmediateRemovalDeath(damageSourceForDeath))
			{
				// Delete this vehicle immediately. Any other vehicles that were attached should derail.
				ServerDerailConvoyMembers(excludeThis: true);
				ServerHardDespawn(EnumDespawnReason.Removed, damageSourceForDeath);
				return;
			}

			// All other deaths | Never die, derail instead.
			ServerDerailConvoyMembers(excludeThis: false);
			return;
		}

		// Non-death reasons, despawn immediately to avoid unexpected and undesired state.
		ServerHardDespawn(reason, damageSourceForDeath);
	}

	private void ServerHardDespawn(EnumDespawnReason reason, DamageSource deathSource)
	{
		if (RailwayVehicleShared.TryServerHardDespawn(this, reason, deathSource)) return;

		// Should never happen on a real server, but keep a safe fallback.
		base.Die(reason, deathSource);
	}

	public override void OnInteract(EntityAgent byEntity, ItemSlot itemSlot, Vec3d hitPosition, EnumInteractMode mode)
	{
		if (TryHandleConductorLocustInstall(byEntity, itemSlot, mode)) return;

		if (itemSlot?.Itemstack?.Collectible is ItemLinkagePole)
		{
			if (Api.Side == EnumAppSide.Server)
			{
				if (mode == EnumInteractMode.Attack) ConvoyTethering.HandleLinkagePoleUntetherClick(this, byEntity, itemSlot);
				else if (mode == EnumInteractMode.Interact) ConvoyTethering.HandleLinkagePoleTetherClick(this, byEntity, itemSlot);
			}
			return;
		}

		if (itemSlot?.Itemstack?.Collectible is ItemWagonFlipStick)
		{
			if (Api.Side == EnumAppSide.Server && mode == EnumInteractMode.Interact) ItemWagonFlipStick.ServerTryFlip(this, byEntity);
			return;
		}

		if (RailwayVehicleShared.IsSneakEmptyHandPickupClick(byEntity, itemSlot, mode))
		{
			if (Api.Side == EnumAppSide.Server) { ServerTryPickup(byEntity); }
			return;
		}

		base.OnInteract(byEntity, itemSlot, hitPosition, mode);
	}

	private bool TryHandleConductorLocustInstall(EntityAgent byEntity, ItemSlot itemSlot, EnumInteractMode mode)
	{
		if (mode != EnumInteractMode.Interact) return false;
		if (itemSlot?.Itemstack?.Collectible is not ItemConductorLocust) return false;
		if (byEntity is not EntityPlayer entityPlayer) return false;

		if (!TryGetClickedSelectionBoxCode(entityPlayer, out string selectionBoxCode) ||
			!ConductorLocustPlacement.IsStandardGaugeConductorSeat(selectionBoxCode))
		{
			if (Api.Side == EnumAppSide.Server && Api.World.PlayerByUid(entityPlayer.PlayerUID) is IServerPlayer serverPlayer)
			{
				serverPlayer.SendIngameError("yangtransport:conductorlocust-sg-requires-conductor-seat");
			}

			return true;
		}

		if (Api.Side == EnumAppSide.Server)
		{
			CanAcceptConductorLocust(out string? errorCode);
			ConductorLocustPlacement.ServerTryInstallIntoAttachableSlot
			(
				this,
				byEntity,
				itemSlot,
				ConductorAttachableBehaviour,
				ConductorLocustPlacement.StandardGaugeAttachmentPointCode,
				errorCode
			);
		}

		return true;
	}

	internal bool CanAcceptConductorLocust(out string? errorCode)
	{
		return ConductorLocustPlacement.CanInstallOnStandardGauge
		(
			ConductorAttachableBehaviour,
			SeatableBehaviour,
			out errorCode
		);
	}

	internal bool HasConductorLocustInstalled()
	{
		return ConductorLocustPlacement.HasInstalledConductorLocust(ConductorAttachableBehaviour, ConductorLocustPlacement.StandardGaugeAttachmentPointCode);
	}

	private bool TryGetClickedSelectionBoxCode(EntityPlayer player, out string selectionBoxCode)
	{
		selectionBoxCode = string.Empty;

		int selectionBoxIndex = player.EntitySelection?.SelectionBoxIndex ?? -1;
		if (selectionBoxIndex <= 0) return false;

		EntityBehaviorSelectionBoxes selectionBoxBehavior = GetBehavior<EntityBehaviorSelectionBoxes>();
		AttachmentPointAndPose[] selectionBoxes = selectionBoxBehavior?.selectionBoxes;
		if ((selectionBoxes == null || selectionBoxes.Length == 0) && selectionBoxBehavior is EntityBehaviorSGBodySelectionBoxes standardGaugeSelectionBoxes)
		{
			selectionBoxes = standardGaugeSelectionBoxes.GetSelectionBoxesForDebug();
		}

		if (selectionBoxes == null || selectionBoxIndex > selectionBoxes.Length) return false;

		selectionBoxCode = selectionBoxes[selectionBoxIndex - 1]?.AttachPoint?.Code;
		return !string.IsNullOrEmpty(selectionBoxCode);
	}

	public override void OnGameTick(float deltaTime)
	{
		if (State == EnumEntityState.Despawned) return;
		base.OnGameTick(deltaTime);

		if (Api.Side != EnumAppSide.Server) return;

		if (Derailed)
		{
			RailwayVehicleShared.TickDerailed
			(
				this, deltaTime, DerailedVelocityBPS, ref DerailedYawVelocityRadians, DerailedNewPositionScratch,
				BelowPositionScratch, in RailwayVehicleShared.DerailedPhysicsParameters.MinecartDefault
			);
			return;
		}

		// SG convoys are simulated only by their stable owner/head.
		// Followers are sampled from the convoy path tape; they do not run independent traction/signaling logic.
		if (IsFollower) return;

		RailSystem ??= Api.ModLoader.GetModSystem<RailGraphServerSystem>();
		if (RailSystem?.IsRuntimeReady != true)
		{
			if (Math.Abs(Speed) > 1e-9)
			{
				StopRailMotion();
				MarkTrackStateDirty();
				PersistTrackState(force: false);
			}
			return;
		}

		ConvoySystem ??= Api.ModLoader.GetModSystem<RailConvoySystem>();
		if (IsConvoyHead && ConvoySystem != null && !ConvoySystem.IsConvoyFullyLoaded(EntityId))
		{
			if (Math.Abs(Speed) > 1e-9)
			{
				StopRailMotion();
				MarkTrackStateDirty();
				PersistTrackState(force: false);
			}
			return;
		}

		if (!TryEnsureBound(forceRebind: false)) { StopRailMotion(); return; }

		var convoyStatistics = GetConvoyStaticStats();
		var leadVehicle = ResolveConvoyLead();

		if (ServerConvoyHasConductorLocust() && leadVehicle.LeadEnd != SGTrainEnd.EndA)
		{
			leadVehicle.ServerSetLeadEnd(SGTrainEnd.EndA);
			StopRailMotion();
		}

		if (!ServerEnsureLoadedConvoyPathTape(forceRebuild: false) && !ServerRecoverPathTapeAfterFailure())
		{
			StopRailMotion();
			PersistTrackState();
			return;
		}
		if (State == EnumEntityState.Despawned) return;

		double rawThrottle = 0;
		double engineMaxSpeed = DriveParameters.HardMaxSpeed;
		double engineAcceleration = 0;

		TryComputeConvoyDrive(leadVehicle, convoyStatistics.Count, convoyStatistics.Weight, out rawThrottle, out engineAcceleration, out engineMaxSpeed);

		RailExactTurnPlan? exactTurnPlan = null;
		RailAutomationDriveCommand automationCommand = RailAutomationDriveCommand.Hold;
		bool automationActive = false;
		AutomationSystem ??= Api.ModLoader.GetModSystem<RailAutomationPathingSystem>();
		if (AutomationSystem != null)
		{
			var authorityCursor = Cursor;
			if (ServerTryGetAutomationRouteStart(allowRouteRepair: false, out var sampledAuthority)) authorityCursor = sampledAuthority;
			automationActive = AutomationSystem.TryApplyAutomation(this, leadVehicle, ref authorityCursor, out automationCommand, out exactTurnPlan);
		}

		bool brakingForLeadChange = false;
		if (!automationActive && Math.Abs(rawThrottle) > 0.1)
		{
			SGTrainEnd requestedLeadEnd = rawThrottle >= 0 ? SGTrainEnd.EndA : SGTrainEnd.EndB;
			if (leadVehicle.LeadEnd != requestedLeadEnd)
			{
				if (Speed <= DriveParameters.StopEpsilon) { leadVehicle.ServerSetLeadEnd(requestedLeadEnd); }
				else brakingForLeadChange = true;
			}
		}

		bool brake = automationActive
			? automationCommand.Mode != RailAutomationDriveMode.DriveForward
			: brakingForLeadChange || (leadVehicle.SteamEngineBehaviour != null && RailwayVehicleShared.IsDriveLeverBrakeEngaged(leadVehicle.WatchedAttributes));

		double throttle = automationActive
			? (automationCommand.Mode == RailAutomationDriveMode.DriveForward
			? GameMath.Clamp(automationCommand.Traction, 0, 1) : 0) : (brakingForLeadChange ? 0 : Math.Abs(rawThrottle));

		int travelSign = leadVehicle.LeadEnd == SGTrainEnd.EndA ? 1 : -1;
		var speedLimitCursor = Cursor;
		if (ServerTryGetAuthorityCursor(travelSign, convoyStatistics.TailDistance, out var sampledSpeedLimitCursor)) speedLimitCursor = sampledSpeedLimitCursor;

		double effectiveMaxSpeed = engineMaxSpeed;
		if (speedLimitCursor.SegmentMaterialSpeedCapBPS > 0) effectiveMaxSpeed = Math.Min(effectiveMaxSpeed, speedLimitCursor.SegmentMaterialSpeedCapBPS);
		if (!automationActive && leadVehicle.LeadEnd != SGTrainEnd.EndA) effectiveMaxSpeed *= leadVehicle.ReverseMaxSpeedMultiplier;

		double previousSpeed = Speed;
		Speed = Math.Max(0, KinematicDrive.Step(Speed, throttle, deltaTime, convoyStatistics.Weight, engineAcceleration, effectiveMaxSpeed, speedLimitCursor.SegmentMaxSpeedFactor, in DriveParameters, brake));
		if (Math.Abs(Speed - previousSpeed) > 1e-9) MarkTrackStateDirty();
		if (previousSpeed > 1e-9 && Speed <= 1e-9) StopRailMotion();

		if (!LivingCollisionSweepInitialized || LastLivingCheckAbsoluteTravel > TravelledABS)
		{
			LastLivingCheckAbsoluteTravel = TravelledABS;
			LastLivingCheckPathHeadDistance = PathHeadDistance;
			LivingCollisionSweepInitialized = true;
		}

		double signedDistance = Speed * deltaTime * travelSign;
		bool advanced = Math.Abs(signedDistance) > 1e-6;
		bool convoyBodyApplied = advanced
			? ServerAdvanceLoadedConvoyPathTape(signedDistance, leadVehicle, exactTurnPlan, automatedMovement: automationActive)
			: !ConvoyBodyDirty || ServerApplyPathTapeToLoadedConvoyMembers();

		if (State == EnumEntityState.Despawned) return;

		if (Derailed) { RailSystem?.ReleaseTransientSignalReservations(EntityId); return; }
		if (!convoyBodyApplied)
		{
			StopRailMotion();
			RailSystem?.ReleaseTransientSignalReservations(EntityId);
			ServerRecoverPathTapeAfterFailure();
			PersistTrackState();
			return;
		}

		CollisionTrail.Clear();

		if (!Derailed) ServerPublishOccupancy(RailSystem.Graph);

		ServerLivingCollisionCheck();

		RailwayVehicleShared.ServerSyncTrackPenaltyIfChanged(this, Cursor.SegmentMaxSpeedFactor);
		if (advanced) MarkTrackStateDirty();
		if (TrackStateDirty) PersistTrackState(force: false);
	}

	private double StableSourceOffsetFromFront()
	{
		double vehicleLength = Math.Max(0.1, VehicleLength);
		return GameMath.Clamp(FrontBogieOffset + BodyOffsetForward + BodyShapeForwardOffsetBlocks, 0.0, vehicleLength);
	}

	private double StableSourceRearExtentBlocks() { return Math.Max(0.0, Math.Max(0.1, VehicleLength) - StableSourceOffsetFromFront()); }

	internal double StableSourceFrontExtentBlocks() { return StableSourceOffsetFromFront(); }

	private double ComputeOccupancyRearExtentBlocks() { return StableSourceRearExtentBlocks(); }

	internal double LeadingBodyOffsetForLeadEnd(SGTrainEnd leadEnd)
	{
		return leadEnd == SGTrainEnd.EndA ? StableSourceFrontExtentBlocks() : -StableSourceRearExtentBlocks();
	}

	private bool TryComputeConvoyDrive(EntityStandardGaugeLocomotive leadVehicle, int convoyLength, int convoyWeight, out double normalizedThrottle, out double accelerationBPSPerSec, out double maxSpeedBPS)
	{
		normalizedThrottle = 0;
		accelerationBPSPerSec = 0;
		maxSpeedBPS = DriveParameters.HardMaxSpeed;

		if (IsFollower) return false;

		var steamEngine = leadVehicle.SteamEngineBehaviour;
		if (steamEngine == null) return false;
		if (!steamEngine.TryGetDrive(out var drive)) return false;

		int direction = drive.GetDriveDir();

		if (!drive.TryComputeLoadedKinematicLimits(convoyWeight, out maxSpeedBPS, out accelerationBPSPerSec)) return false;

		normalizedThrottle = GameMath.Clamp(direction, -1, 1);

		return true;
	}

	private EntityStandardGaugeLocomotive ResolveConvoyLead()
	{
		if (Api?.Side != EnumAppSide.Server) return this;
		if (ConvoyHeadID == 0 || ConvoySystem == null) return this;

		long headID = IsFollower ? ConvoyHeadID : EntityId;
		if (!ConvoySystem.TryGetLeadID(headID, out long leadID)) return this;
		return Api.World.GetEntityById(leadID) as EntityStandardGaugeLocomotive ?? this;
	}

	private bool ServerConvoyHasConductorLocust()
	{
		if (Api?.Side != EnumAppSide.Server) return false;

		long headID = ConvoyHeadID != 0 ? ConvoyHeadID : EntityId;
		ConvoySystem ??= Api.ModLoader.GetModSystem<RailConvoySystem>();
		if (ConvoySystem != null && ConvoySystem.TryGetConvoyHasConductorLocust(headID, out bool hasLocust)) return hasLocust;

		return HasConductorLocustInstalled();
	}

	private ConvoyStaticStats GetConvoyStaticStats()
	{
		if (IsFollower)
		{
			if (Api.World.GetEntityById(ConvoyHeadID) is EntityStandardGaugeLocomotive headVehicle) return headVehicle.GetConvoyStaticStats();
			return new ConvoyStaticStats { Count = 1, Weight = SelfWeight, TailID = EntityId, TailDistance = 0 };
		}

		int count = 1;
		int weight = SelfWeight;
		long tailID = EntityId;
		double tailDistance = 0;

		if (Api.Side == EnumAppSide.Server && IsConvoyHead && ConvoySystem != null && ConvoySystem.TryGetConvoyStats(EntityId, out int convoyCount, out int convoyWeight, out long convoyTailID, out double convoyTailDistance))
		{
			count = convoyCount;
			weight = convoyWeight;
			tailID = convoyTailID;
			tailDistance = convoyTailDistance;
		}

		return new ConvoyStaticStats { Count = count, Weight = weight, TailID = tailID, TailDistance = tailDistance };
	}

	private void RecalculateSelfWeight(bool notifyConvoy = true)
	{
		if (LocomotiveStatistics == null) return;

		int newWeight = LocomotiveStatistics.WeightSpecification.ComputeTotalWeight(ConductorAttachableBehaviour);
		if (newWeight == SelfWeight) return;

		SelfWeight = newWeight;
		if (!notifyConvoy) return;
		if (Api.Side != EnumAppSide.Server) return;

		if (ConvoyHeadID == 0) ServerSetWatchedConvoyStatistics(1, SelfWeight, 0, ComputeOccupancyRearExtentBlocks());
		else
		{
			ConvoySystem ??= Api.ModLoader.GetModSystem<RailConvoySystem>();
			ConvoySystem?.NotifyVehicleWeightChanged(this);
		}
	}

	#region Lead-End Control State
	/// Canonical SG reverse state. This is a control/travel-direction bit only.
	/// It must not change the entity source point, visual active end, convoy order, or rail tape.
	internal void ServerSetLeadEnd(SGTrainEnd leadEnd)
	{
		if (LeadEnd == leadEnd) return;
		LeadEnd = leadEnd;
		Attributes.SetInt(SGLeadEndAttribute, (int)leadEnd);
		MarkTrackStateDirty();

		if (Api?.Side == EnumAppSide.Server)
		{
			WatchedAttributes.SetInt(SGLeadEndAttribute, (int)leadEnd);
			WatchedAttributes.MarkPathDirty(SGLeadEndAttribute);
		}
	}
	#endregion

	#region Rail Binding
	internal bool ServerTryGetHorizontalFacing(out double facingX, out double facingZ)
	{
		double yaw = ServerPos.Yaw;
		facingX = Math.Sin(yaw);
		facingZ = Math.Cos(yaw);
		return facingX * facingX + facingZ * facingZ > 1e-8;
	}

	internal bool ServerTryPlanFacingFlip(RailGraphServerSystem railSystem, out RailwayVehicleShared.RailCursor flippedCursor)
	{
		flippedCursor = default;
		if (Api?.Side != EnumAppSide.Server || Derailed || railSystem == null) return false;

		var bound = Cursor;
		bound.Gauge = TrackGauge;

		if (!RailwayVehicleShared.TryEnsureBound
		(
			railSystem,
			Pos.AsBlockPos,
			ServerPos.XYZ,
			Pos.AsBlockPos.dimension,
			forceRebind: true,
			bindRadiusBlocks: BindRadiusBlocks,
			ref bound,
			BindCandidates,
			bindWithYaw: true,
			bindYaw: ServerPos.Yaw)
		) { return false; }

		double sourceShift = StableSourceFrontExtentBlocks() - StableSourceRearExtentBlocks();
		double expectedX = ServerPos.X + Math.Sin(ServerPos.Yaw) * sourceShift;
		double expectedY = ServerPos.Y;
		double expectedZ = ServerPos.Z + Math.Cos(ServerPos.Yaw) * sourceShift;

		return RailVehicleFacingAlignment.TryPlanInPlaceFacingFlip(railSystem.Graph, sourceShift, expectedX, expectedY, expectedZ, in bound, out flippedCursor);
	}

	internal void ServerApplyPlannedFacingFlip(RailwayVehicleShared.RailCursor flippedCursor, bool publishOccupancy)
	{
		if (Api?.Side != EnumAppSide.Server) return;

		Cursor = flippedCursor;
		Cursor.Gauge = TrackGauge;
		StopRailMotion();

		PathTape?.Clear();
		CollisionTrail.Clear();

		WriteWorldPosFromTrack();
		PersistTrackState();

		if (publishOccupancy)
		{
			RailSystem ??= Api.ModLoader.GetModSystem<RailGraphServerSystem>();
			if (RailSystem != null) ServerPublishOccupancy(RailSystem.Graph);
		}
	}

	private bool TryEnsureBound(bool forceRebind)
	{
		var railSystem = RailSystem ??= Api.ModLoader.GetModSystem<RailGraphServerSystem>();
		if (railSystem == null) return false;
		if (!RailwayVehicleShared.TryEnsureBound
		(
			railSystem,
			Pos.AsBlockPos,
			ServerPos.XYZ,
			Pos.AsBlockPos.dimension,
			forceRebind,
			BindRadiusBlocks,
			ref Cursor,
			BindCandidates,
			bindWithYaw: true,
			bindYaw: ServerPos.Yaw)
		) { return false; }

		RailwayVehicleShared.ServerSyncTrackPenaltyIfChanged(this, Cursor.SegmentMaxSpeedFactor);
		return true;
	}


	private void ServerPublishOccupancy(RailGraphLive graph)
	{
		if (Api?.Side != EnumAppSide.Server || IsFollower) return;
		if (!TryPublishRailOccupancyFootprint(RailSystem!, graph)) ServerVacateOccupancyZone();
	}

	private void ServerVacateOccupancyZone()
	{
		if (Api?.Side != EnumAppSide.Server) return;
		if (!IsFollower) RemovePublishedCollisionBody();
		PublishedOccupancyFootprintEdges.Clear();
		PublishedOccupancyGraphVersion = -1;
		RailSystem?.ReleaseOccupancyOwner(EntityId);
	}
	#endregion

	#region Movement Along Track & Dead Ends
	private bool ServerDeadEndWallImpact(double absoluteImpactSpeed, in RailwayVehicleShared.DeadEndInfo deadEnd)
	{
		if (Api.Side != EnumAppSide.Server || IsFollower || absoluteImpactSpeed < 0.01) return false;
		if (!deadEnd.TryGetHorizontalForward(out double forwardX, out double forwardZ)) return false;

		deadEnd.GetEndPoint(out double endX, out double endY, out double endZ);
		if (!RailwayVehicleShared.IsBlockingWallAhead(Api, ServerPos.Dimension, endX, endY, endZ, forwardX, forwardZ, DeadEndPositionScratch)) return false;

		RailwayVehicleShared.ServerShakeImpact(Api, ServerPos.XYZ, absoluteImpactSpeed);
		ServerApplyPassengerImpactToConvoy
		(
			absoluteImpactSpeed,
			RailwayVehicleShared.CollisionDamageMultiplier,
			RailwayVehicleShared.EndTrackCollisionDamageSpeedThreshold,
			double.PositiveInfinity
		);
		var leadVehicle = ResolveConvoyLead();
		leadVehicle.SteamEngineBehaviour?.ForceDriveLeverStop();
		leadVehicle.SteamEngineBehaviour?.ForceExtinguishBoiler();
		StopRailMotion();
		PersistTrackState();
		return true;
	}

	internal void ServerDerailForInvalidTrack()
	{
		if (Api?.Side != EnumAppSide.Server || Derailed || IsFollower) return;
		double yaw = ServerPos.Yaw;
		ServerDerailConvoy(Math.Abs(Speed), Math.Sin(yaw), Math.Cos(yaw), 1, ServerPos.X, ServerPos.Y, ServerPos.Z);
	}

	private void ServerDerailConvoy(double absoluteImpactSpeed, double directionX, double directionZ, int movementDirection, double endX, double endY, double endZ)
	{
		if (Api.Side != EnumAppSide.Server) return;

		RailwayVehicleShared.ServerShakeImpact(Api, ServerPos.XYZ, absoluteImpactSpeed);

		ServerPos.X = (float)endX;
		ServerPos.Y = (float)endY;
		ServerPos.Z = (float)endZ;
		Pos.SetFrom(ServerPos);

		double directionLengthSQ = directionX * directionX + directionZ * directionZ;
		if (directionLengthSQ < 1e-8)
		{
			double yaw = ServerPos.Yaw;
			directionX = Math.Sin(yaw);
			directionZ = Math.Cos(yaw);
			directionLengthSQ = directionX * directionX + directionZ * directionZ;
		}

		double inverseDirectionLength = 1.0 / Math.Sqrt(directionLengthSQ);
		double forwardX = directionX * inverseDirectionLength * (movementDirection >= 0 ? 1 : -1);
		double forwardZ = directionZ * inverseDirectionLength * (movementDirection >= 0 ? 1 : -1);
		double sideX = -forwardZ;
		double sideZ = forwardX;

		List<EntityStandardGaugeLocomotive> vehicles = new();

		long headID = IsConvoyHead ? EntityId : ConvoyHeadID;
		if (headID != 0 && ConvoySystem != null && ConvoySystem.TryGetOrderedMemberIDs(headID, out var memberIDs))
		{
			for (int memberIndex = 0; memberIndex < memberIDs.Count; memberIndex++)
			{
				if (Api.World.GetEntityById(memberIDs[memberIndex]) is EntityStandardGaugeLocomotive vehicle) vehicles.Add(vehicle);
			}
		}
		else vehicles.Add(this);

		Random random = Api.World.Rand;
		for (int vehicleIndex = 0; vehicleIndex < vehicles.Count; vehicleIndex++)
		{
			vehicles[vehicleIndex].ServerSetDerailedState(absoluteImpactSpeed, forwardX, forwardZ, sideX, sideZ, vehicleIndex, vehicles.Count, random);
		}
	}

	private void ServerDerailConvoyMembers(bool excludeThis)
	{
		if (Api.Side != EnumAppSide.Server) return;

		List<EntityStandardGaugeLocomotive> vehicles = new();
		long headID = IsConvoyHead ? EntityId : ConvoyHeadID;

		if (headID != 0 && ConvoySystem != null && ConvoySystem.TryGetOrderedMemberIDs(headID, out var memberIDs))
		{
			for (int memberIndex = 0; memberIndex < memberIDs.Count; memberIndex++)
			{
				if (Api.World.GetEntityById(memberIDs[memberIndex]) is EntityStandardGaugeLocomotive vehicle)
				{
					if (excludeThis && vehicle.EntityId == EntityId) continue;
					vehicles.Add(vehicle);
				}
			}
		}
		else if (!excludeThis) vehicles.Add(this);

		double yaw = ServerPos.Yaw;
		double forwardX = Math.Sin(yaw);
		double forwardZ = Math.Cos(yaw);
		double directionLengthSQ = forwardX * forwardX + forwardZ * forwardZ;
		if (directionLengthSQ < 1e-8) { forwardX = 0; forwardZ = 1; directionLengthSQ = 1; }
		double inverseDirectionLength = 1.0 / Math.Sqrt(directionLengthSQ);
		forwardX *= inverseDirectionLength;
		forwardZ *= inverseDirectionLength;
		double sideX = -forwardZ;
		double sideZ = forwardX;

		double absoluteImpactSpeed = Math.Abs(Speed);
		Random random = Api.World.Rand;

		for (int vehicleIndex = 0; vehicleIndex < vehicles.Count; vehicleIndex++)
		{
			vehicles[vehicleIndex].ServerSetDerailedState(absoluteImpactSpeed, forwardX, forwardZ, sideX, sideZ, vehicleIndex, vehicles.Count, random);
		}
	}

	private void ServerSetDerailedState(double absoluteImpactSpeed, double forwardX, double forwardZ, double sideX, double sideZ, int orderIndex, int cartCount, Random random)
	{
		ConvoySystem?.ForceFree(this);
		ServerClearConvoyState();
		ServerVacateOccupancyZone();

		RailwayVehicleShared.ServerStartDerailed
		(
			this,
			ref Derailed,
			ref Cursor,
			ref Speed,
			DerailedVelocityBPS,
			ref DerailedYawVelocityRadians,
			absoluteImpactSpeed,
			forwardX, forwardZ,
			sideX, sideZ,
			orderIndex, cartCount,
			random,
			nudgeForward: DerailedNudgeForward,
			nudgeSide: DerailedNudgeSide,
			attributeDerailedKey: DerailedAttribute
		);
	}

	private void WriteWorldPosFromTrack(double motionSpeedBPS = 0)
	{
		RailwayVehicleShared.WriteWorldPositionFromTrack(ServerPos, Pos, ref Cursor, motionSpeedBPS);
	}

	private void StopRailMotion()
	{
		RailwayVehicleShared.StopRailMotion(this, ref Speed);
		ConvoyBodyDirty = true;
	}
	#endregion

	#region Living Collision
	private void ServerLivingCollisionCheck()
	{
		if (Api.Side != EnumAppSide.Server || IsFollower || Derailed || PathTape == null || RailSystem == null) return;

		double absoluteSpeed = Math.Abs(Speed);
		if (absoluteSpeed < LivingMinSpeedBPS)
		{
			LastLivingCheckAbsoluteTravel = TravelledABS;
			LastLivingCheckPathHeadDistance = PathHeadDistance;
			LivingCollisionSweepInitialized = true;
			return;
		}

		if (!LivingCollisionSweepInitialized || LastLivingCheckAbsoluteTravel > TravelledABS)
		{
			LastLivingCheckAbsoluteTravel = TravelledABS;
			LastLivingCheckPathHeadDistance = PathHeadDistance;
			LivingCollisionSweepInitialized = true;
			return;
		}

		double sweepTravel = TravelledABS - LastLivingCheckAbsoluteTravel;
		double sweptPathDistance = PathHeadDistance - LastLivingCheckPathHeadDistance;
		if (sweepTravel < LivingCheckTravelStep) return;

		double previousPathHeadDistance = LastLivingCheckPathHeadDistance;
		LastLivingCheckAbsoluteTravel = TravelledABS;
		LastLivingCheckPathHeadDistance = PathHeadDistance;
		if (sweepTravel > LivingMaxSweepBlocks || Math.Abs(sweptPathDistance) > LivingMaxSweepBlocks) return;
		if (!PathTape.ValidateAuthoritative(RailSystem.Graph, TrackGauge)) return;

		LivingCollisionSystem ??= Api.ModLoader.GetModSystem<RailLivingCollisionSystem>();
		if (LivingCollisionSystem == null) return;

		SGTrainEnd sweepLeadEnd = ResolveConvoyLead().LeadEnd;
		List<IRailwayConvoyVehicle> ordered = ConvoyMemberScratch;
		ordered.Clear();
		if (!IsConvoyHead || ConvoySystem?.TryGetLoadedOrderedMembers(EntityId, ordered) != true || ordered.Count == 0) ordered.Add(this);

		for (int vehicleIndex = 0; vehicleIndex < ordered.Count; vehicleIndex++)
		{
			if (ordered[vehicleIndex].Entity is not EntityStandardGaugeLocomotive vehicle || vehicle.Derailed) continue;

			double offset = vehicle.ConvoyDistanceBehindHeadBlocks;
			double currentPathDistance = PathHeadDistance - offset;
			double previousPathDistance = previousPathHeadDistance - offset;
			if (!TrySamplePathRoutePose(currentPathDistance, out double currentX, out double currentY, out double currentZ, out float currentYaw, out _)) continue;
			if (!TrySamplePathRoutePose(previousPathDistance, out double previousX, out double previousY, out double previousZ, out float previousYaw, out _)) continue;

			double leadingOffset = vehicle.LeadingBodyOffsetForLeadEnd(sweepLeadEnd);
			double previousFrontX = previousX + Math.Sin(previousYaw) * leadingOffset;
			double previousFrontZ = previousZ + Math.Cos(previousYaw) * leadingOffset;
			double currentFrontX = currentX + Math.Sin(currentYaw) * leadingOffset;
			double currentFrontZ = currentZ + Math.Cos(currentYaw) * leadingOffset;

			LivingCollisionSystem.PublishSweep
			(
				this,
				absoluteSpeed,
				LivingWallHalfWidth,
				LivingWallBottomOffset,
				LivingWallHeight,
				previousFrontX, previousY, previousFrontZ,
				currentFrontX, currentY, currentFrontZ
			);
		}
		ordered.Clear();
	}

	private bool TrySamplePathRoutePose(double pathDistance, out double x, out double y, out double z, out float yaw, out float roll)
	{
		x = y = z = 0;
		yaw = roll = 0;
		if (PathTape == null || RailSystem == null || !PathTape.TrySampleAuthoritativeCursor(RailSystem.Graph, TrackGauge, pathDistance, out var cursor)) return false;
		return RailwayVehicleShared.TryReadWorldPoseFromTrack(ref cursor, out x, out y, out z, out yaw, out roll);
	}

	Entity IRailLivingCollisionSource.CollisionSourceEntity => this;
	long IRailLivingCollisionSource.CollisionGroupID => ConvoyHeadID != 0 ? ConvoyHeadID : EntityId;

	bool IRailLivingCollisionSource.IsMountedOnCollisionSource(EntityAgent agent)
	{
		IMountableSeat seat = agent.MountedOn;
		if (seat == null) return false;

		Entity mountEntity = seat.Entity;
		if (mountEntity is not EntityStandardGaugeLocomotive) mountEntity = seat.MountSupplier?.OnEntity;
		if (mountEntity is not EntityStandardGaugeLocomotive vehicle) return false;

		long localConvoyHeadID = ConvoyHeadID != 0 ? ConvoyHeadID : EntityId;
		long otherConvoyHeadID = vehicle.ConvoyHeadID != 0 ? vehicle.ConvoyHeadID : vehicle.EntityId;
		return localConvoyHeadID == otherConvoyHeadID;
	}

	void IRailLivingCollisionSource.ApplyLivingCollisionHit(EntityAgent agent, double hitX, double hitY, double hitZ, double absoluteSpeed)
	{
		if (agent.IsActivityRunning("invulnerable")) return;

		var sourcePosition = new Vec3d(hitX, hitY, hitZ);
		float damage = RailwayVehicleShared.ComputeTrainCrashDamageBars(absoluteSpeed);
		float knockback = RailwayVehicleShared.ComputeTrainCrashKnockbackStrength(absoluteSpeed);
		float verticalDivisor = 2.5f + (float)GameMath.Clamp(absoluteSpeed / 2.5, 0, 8);

		if (damage > 0.01f && RailwayVehicleShared.ShouldTrainCrashDamage(agent))
		{
			agent.ReceiveDamage(new DamageSource
			{
				Source = EnumDamageSource.Machine,
				Type = EnumDamageType.BluntAttack,
				SourceEntity = this,
				CauseEntity = this,
				SourcePos = sourcePosition,
				KnockbackStrength = knockback,
				YDirKnockbackDiv = verticalDivisor
			}, damage);
			return;
		}

		RailwayVehicleShared.ApplyVanillaKnockbackOnly(this, agent, sourcePosition, knockback, verticalDivisor);
	}


	private void ServerPublishConvoyRoute(bool forceSnapshot = false)
	{
		if (Api?.Side != EnumAppSide.Server || PathTape == null || PathTape.Count == 0 || IsFollower) return;
		RouteNetworkSystem ??= Api.ModLoader.GetModSystem<ConvoyRouteNetworkSystem>();
		RouteNetworkSystem?.PublishServerRoute(EntityId, this, PathTape, PathHeadDistance, forceSnapshot);
	}

	private double ServerDesiredClientRouteLookahead()
	{
		if (Api?.Side != EnumAppSide.Server || IsFollower) return 0;
		RouteNetworkSystem ??= Api.ModLoader.GetModSystem<ConvoyRouteNetworkSystem>();
		if (RouteNetworkSystem?.HasServerSubscribers(EntityId) != true) return 0;

		return GameMath.Clamp(ClientRouteLookaheadBaseBlocks + Speed * ClientRouteLookaheadSeconds, ClientRouteLookaheadBaseBlocks, ClientRouteLookaheadMaxBlocks);
	}

	internal bool ServerTryGetReplicatedConvoyRoute(out ConvoyRoute route, out double headDistance)
	{
		route = null!;
		headDistance = PathHeadDistance;
		if (Api?.Side != EnumAppSide.Server || IsFollower) return false;
		if (!ServerEnsureLoadedConvoyPathTape(forceRebuild: false) || PathTape == null || PathTape.Count == 0) return false;
		route = PathTape;
		headDistance = PathHeadDistance;
		return true;
	}


	#endregion

	#region Path Tape
	private bool ServerSeedConvoyRailStateFromOrderedConvoy(IReadOnlyList<IRailwayConvoyVehicle> ordered, IReadOnlyDictionary<long, double>? physicalDistanceFromHead = null)
	{
		return ServerSeedPathTapeFromOrderedConvoy(ordered, force: true, physicalDistanceFromHead);
	}

	internal bool ServerEnsureLoadedConvoyPathTape(bool forceRebuild)
	{
		if (Api?.Side != EnumAppSide.Server || Derailed || IsFollower) return false;

		RailSystem ??= Api.ModLoader.GetModSystem<RailGraphServerSystem>();
		ConvoySystem ??= Api.ModLoader.GetModSystem<RailConvoySystem>();
		if (RailSystem == null) return false;

		if (!forceRebuild && PathTape != null && PathTape.ValidateAuthoritative(RailSystem.Graph, TrackGauge))
		{
			// Tape validity is keyed to loaded vehicle source points. Physical front/rear  overhang spans are recorded when available,
			// but they should not make a train immobile after a topology edit near an end buffer or partially edited rail.
			var convoyStatistics = GetConvoyStaticStats();
			double minPathDistance = PathHeadDistance - convoyStatistics.TailDistance - GetTailRearExtentForCurrentConvoy();
			double maxPathDistance = PathHeadDistance + StableSourceFrontExtentBlocks();
			if (PathTape.Covers(minPathDistance, maxPathDistance)) return true;
		}

		return ServerSeedPathTapeFromCurrentOrderedConvoy(force: true);
	}

	private bool ServerRecoverPathTapeAfterFailure()
	{
		if (Api?.Side != EnumAppSide.Server || IsFollower || Derailed) return false;

		CollisionTrail.Clear();

		bool seeded = ServerEnsureLoadedConvoyPathTape(forceRebuild: true);
		if (seeded) return true;

		// Last-resort recovery for rail edits under/near a train: do not leave a stale cursor/tape combination that fails forever.
		// Clear the binding so the next tick retries a clean nearest-rail bind, and persist that non-poisoned state.
		Cursor.ClearBinding();
		PersistTrackState();
		return false;
	}

	internal bool ServerSeedPathTapeFromCurrentOrderedConvoy(bool force = true)
	{
		if (Api?.Side != EnumAppSide.Server || IsFollower) return false;
		ConvoySystem ??= Api.ModLoader.GetModSystem<RailConvoySystem>();

		List<IRailwayConvoyVehicle> ordered = ConvoyMemberScratch;
		ordered.Clear();

		if (IsConvoyHead && ConvoySystem != null) { if (!ConvoySystem.TryGetLoadedOrderedMembers(EntityId, ordered)) ordered.Add(this); }
		else ordered.Add(this);

		return ServerSeedPathTapeFromOrderedConvoy(ordered, force);
	}

	private bool ServerSeedPathTapeFromOrderedConvoy(IReadOnlyList<IRailwayConvoyVehicle> ordered, bool force, IReadOnlyDictionary<long, double>? physicalDistanceFromHead = null)
	{
		if (Api?.Side != EnumAppSide.Server || ordered == null || ordered.Count == 0) return false;
		if (ordered[0].Entity.EntityId != EntityId) return false;

		RailSystem ??= Api.ModLoader.GetModSystem<RailGraphServerSystem>();
		ConvoySystem ??= Api.ModLoader.GetModSystem<RailConvoySystem>();
		if (RailSystem == null) return false;

		if (physicalDistanceFromHead == null && ordered.Count > 1) { physicalDistanceFromHead = ConvoySystem?.TryMeasurePhysicalDistances(ordered); }

		ConvoyRoute? savedPathTape = PathTape;
		double savedHeadDistance = PathHeadDistance;
		double savedAbsoluteDistanceTravelled = TravelledABS;
		bool success = false;

		PathTape = new ConvoyRoute();
		try
		{
			double tailDistance = Math.Max(0, ordered[ordered.Count - 1].ConvoyDistanceBehindHead);
			double headFront = StableSourceFrontExtentBlocks();
			double tailRear = ordered[ordered.Count - 1].Entity is EntityStandardGaugeLocomotive standardGaugeTailVehicle
				? standardGaugeTailVehicle.StableSourceRearExtentBlocks()
				: ordered[ordered.Count - 1].OccupancyRearExtentBlocks;

			PathHeadDistance = Math.Max(PathTapeSeedBaseDistance, tailDistance + tailRear + headFront + AuthoritativeRouteReserveBlocks);
			TravelledABS = Math.Max(TravelledABS, tailDistance + 1.0);

			for (int vehicleIndex = 0; vehicleIndex < ordered.Count; vehicleIndex++)
			{
				if (ordered[vehicleIndex].Entity is not EntityStandardGaugeLocomotive vehicle) return false;
				if (!vehicle.TryEnsureBound(forceRebind: force || vehicle.Cursor.SegmentHash == 0)) return false;
			}

			if (ordered[ordered.Count - 1].Entity is EntityStandardGaugeLocomotive tailVehicle)
			{
				double tailSourceDistance = PathHeadDistance - ordered[ordered.Count - 1].ConvoyDistanceBehindHead;
				double tailRearExtent = Math.Max(0, tailVehicle.StableSourceRearExtentBlocks());
				if (tailRearExtent > 1e-6) ServerAppendBestBoundarySeedGap(tailVehicle, tailSourceDistance, -tailRearExtent);
			}

			for (int pairIndex = ordered.Count - 1; pairIndex > 0; pairIndex--)
			{
				if (ordered[pairIndex].Entity is not EntityStandardGaugeLocomotive behind) return false;
				if (ordered[pairIndex - 1].Entity is not EntityStandardGaugeLocomotive ahead) return false;

				double behindPathDistance = PathHeadDistance - ordered[pairIndex].ConvoyDistanceBehindHead;
				double aheadPathDistance = PathHeadDistance - ordered[pairIndex - 1].ConvoyDistanceBehindHead;
				double canonicalGap = aheadPathDistance - behindPathDistance;
				if (canonicalGap <= 1e-6) continue;

				double physicalGap = canonicalGap;
				if
				(
					physicalDistanceFromHead != null &&
					physicalDistanceFromHead.TryGetValue(ordered[pairIndex].Entity.EntityId, out double behindPhysical) &&
					physicalDistanceFromHead.TryGetValue(ordered[pairIndex - 1].Entity.EntityId, out double aheadPhysical)
				) { physicalGap = Math.Abs(behindPhysical - aheadPhysical); }

				if (!ServerAppendBestSeedGap(behind, ahead, behindPathDistance, canonicalGap, physicalGap)) return false;
			}

			double headFrontExtent = Math.Max(0, StableSourceFrontExtentBlocks());
			if (headFrontExtent > 1e-6) ServerAppendBestBoundarySeedGap(this, PathHeadDistance, headFrontExtent);
			if (PathTape == null || PathTape.Count == 0) return false;

			ServerTrimPathTapeToCurrentConvoy(ordered);
			success = ServerApplyPathTapeToLoadedConvoyMembers();

			if (success) { Attributes.SetDouble(SGPathHeadDistanceAttribute, PathHeadDistance); }

			return success;
		}
		finally
		{
			if (!success)
			{
				PathTape = savedPathTape;
				PathHeadDistance = savedHeadDistance;
				TravelledABS = savedAbsoluteDistanceTravelled;
			}
		}
	}

	private bool ServerAppendBestSeedGap(EntityStandardGaugeLocomotive sourceVehicle, EntityStandardGaugeLocomotive targetVehicle, double sourcePathDistance, double canonicalGap, double physicalGap)
	{
		double directionX = targetVehicle.ServerPos.X - sourceVehicle.ServerPos.X;
		double directionZ = targetVehicle.ServerPos.Z - sourceVehicle.ServerPos.Z;

		var startCursor = sourceVehicle.Cursor;
		if (directionX * directionX + directionZ * directionZ > 1e-8) RailwayVehicleShared.ChooseForwardDirectionFromYaw((float)Math.Atan2(directionX, directionZ), ref startCursor);

		ReadOnlySpan<int> attempts = stackalloc int[] { 0, -1, 1 };
		ConvoyRoute? bestPathTape = null;
		double bestDistanceSQ = double.MaxValue;

		for (int attemptIndex = 0; attemptIndex < attempts.Length; attemptIndex++)
		{
			var savedPathTape = PathTape;
			var candidatePathTape = ClonePathTape(savedPathTape);
			PathTape = candidatePathTape;

			if (ServerAppendTapeFromCursor(startCursor, physicalGap, sourcePathDistance, attempts[attemptIndex], null, requireFullDistance: true, pathDistance: canonicalGap))
			{
				if (candidatePathTape.TrySampleAuthoritativeCursor(RailSystem!.Graph, TrackGauge, sourcePathDistance + canonicalGap, out var routeProbeCursor))
				{
					var poseProbeCursor = routeProbeCursor;
					if (RailwayVehicleShared.TryReadWorldPoseFromTrack(ref poseProbeCursor, out double x, out double y, out double z, out _, out _))
					{
						double positionErrorX = targetVehicle.ServerPos.X - x;
						double positionErrorY = targetVehicle.ServerPos.Y - y;
						double positionErrorZ = targetVehicle.ServerPos.Z - z;
						double distanceSQ = positionErrorX * positionErrorX + positionErrorY * positionErrorY + positionErrorZ * positionErrorZ;
						if (distanceSQ < bestDistanceSQ)
						{
							bestDistanceSQ = distanceSQ;
							bestPathTape = candidatePathTape;
						}
					}
				}
			}

			PathTape = savedPathTape;
		}

		if (bestPathTape == null || bestDistanceSQ > 0.35 * 0.35) return false;

		PathTape = bestPathTape;
		return true;
	}


	private bool ServerAppendBestBoundarySeedGap(EntityStandardGaugeLocomotive vehicle, double sourcePathDistance, double signedDistance)
	{
		if (PathTape == null || RailSystem == null) return false;
		double absoluteDistance = Math.Abs(signedDistance);
		if (absoluteDistance <= 1e-8) return true;

		// The only known pose for a physical boundary is the vehicle source pose and its local tangent.
		// Use that as a sanity target so a boundary span crossing a switch does not silently take the straightest branch.
		double expectedX = vehicle.ServerPos.X + Math.Sin(vehicle.ServerPos.Yaw) * signedDistance;
		double expectedY = vehicle.ServerPos.Y;
		double expectedZ = vehicle.ServerPos.Z + Math.Cos(vehicle.ServerPos.Yaw) * signedDistance;

		var startCursor = vehicle.Cursor;
		RailwayVehicleShared.ChooseForwardDirectionFromYaw(vehicle.ServerPos.Yaw, ref startCursor);

		ReadOnlySpan<int> attempts = stackalloc int[] { 0, -1, 1 };
		ConvoyRoute? bestPathTape = null;
		double bestScore = double.MaxValue;

		for (int attemptIndex = 0; attemptIndex < attempts.Length; attemptIndex++)
		{
			var savedPathTape = PathTape;
			var candidatePathTape = ClonePathTape(savedPathTape);
			PathTape = candidatePathTape;

			if (ServerAppendTapeFromCursor(startCursor, signedDistance, sourcePathDistance, attempts[attemptIndex], null, requireFullDistance: true)
				&& candidatePathTape.TrySampleAuthoritativeCursor(RailSystem.Graph, TrackGauge, sourcePathDistance + signedDistance, out var routeProbeCursor))
			{
				var poseProbeCursor = routeProbeCursor;
				if (RailwayVehicleShared.TryReadWorldPoseFromTrack(ref poseProbeCursor, out double x, out double y, out double z, out float yaw, out _))
				{
					double positionErrorX = expectedX - x; double positionErrorY = expectedY - y; double positionErrorZ = expectedZ - z;
					double positionScore = positionErrorX * positionErrorX + positionErrorY * positionErrorY * 0.25 + positionErrorZ * positionErrorZ;

					// Tape samples always use the vehicle/body-positive orientation. signedDistance is only the temporary direction used while seeding a boundary.
					float expectedYaw = vehicle.ServerPos.Yaw;

					double yawError = Math.Abs(GameMath.AngleRadDistance(expectedYaw, yaw));
					double score = positionScore + yawError * yawError * 0.25;
					if (score < bestScore)
					{
						bestScore = score;
						bestPathTape = candidatePathTape;
					}
				}
			}

			PathTape = savedPathTape;
		}

		if (bestPathTape == null) return false;

		PathTape = bestPathTape;
		return true;
	}

	private static ConvoyRoute ClonePathTape(ConvoyRoute? source)
	{
		return source?.Clone() ?? new ConvoyRoute();
	}


	private bool ServerAppendTapeFromCursor
	(
		RailwayVehicleShared.RailCursor startCursor,
		double signedDistance,
		double startingPathDistance,
		int desiredTurn,
		RailExactTurnPlan? exactTurnPlan,
		bool requireFullDistance = false,
		double? pathDistance = null
	)
	{
		if (PathTape == null || RailSystem == null) return false;
		if (Math.Abs(signedDistance) <= 1e-8) return true;

		var cursor = startCursor;
		cursor.Gauge = TrackGauge;
		if (!RailwayVehicleShared.TryRefreshPolyline(RailSystem.Graph, ref cursor)) return false;

		double speed = 0;
		double travel = 0;
		double pathDelta = pathDistance ?? signedDistance;
		int pathSign = pathDelta >= 0 ? 1 : -1;
		double pathUnitsPerTravel = Math.Abs(pathDelta / signedDistance);

		PathTapeRecorder.Begin(PathTape, TrackGauge, pathSign, startingPathDistance, 0, pathUnitsPerTravel, recordSelectedEdgeRemainder: false);
		bool advanceSucceeded = RailwayVehicleShared.AdvanceAlongTrack
		(
			RailSystem.Graph,
			signedDistance,
			desiredTurn,
			exactTurnPlan,
			RailSystem,
			useSignalAuthority: false,
			ref cursor,
			ref speed,
			ref travel,
			trackTravelledABS: true,
			out _,
			collisionTrail: null,
			occupancyOwnerId: EntityId,
			tapeRecorder: PathTapeRecorder
		);
		PathTapeRecorder.Clear();

		if (requireFullDistance) { return advanceSucceeded && Math.Abs(travel - Math.Abs(signedDistance)) <= Math.Max(0.01, Math.Abs(signedDistance) * 0.02); }
		return advanceSucceeded || travel > 1e-6;
	}

	internal bool ServerTryGetAutomationRouteStart(bool allowRouteRepair, out RailwayVehicleShared.RailCursor cursor)
	{
		cursor = default;
		if (Api?.Side != EnumAppSide.Server || IsFollower || Derailed) return false;

		RailSystem ??= Api.ModLoader.GetModSystem<RailGraphServerSystem>();
		if (RailSystem == null) return false;

		// Conductor automation always normalizes standard-gauge travel toward EndA before it searches, so the minimap must use the same positive-tape authority point.
		if (ServerTryGetAuthorityCursor(1, 0, out cursor)) return true;
		if (!allowRouteRepair) return false;

		if (!ServerEnsureLoadedConvoyPathTape(forceRebuild: false) && !ServerRecoverPathTapeAfterFailure()) { return false; }

		if (ServerTryGetAuthorityCursor(1, 0, out cursor)) return true;
		if (!ServerRecoverPathTapeAfterFailure()) return false;
		return ServerTryGetAuthorityCursor(1, 0, out cursor);
	}

	private bool ServerTryGetAuthorityCursor(int movementSign, double tailDistance, out RailwayVehicleShared.RailCursor authorityCursor)
	{
		authorityCursor = default;
		if (PathTape == null || RailSystem == null) return false;

		double authorityPathDistance = movementSign >= 0
			? PathHeadDistance + StableSourceFrontExtentBlocks()
			: PathHeadDistance - tailDistance - GetTailRearExtentForCurrentConvoy();

		return PathTape.TrySampleAuthoritativeCursorForTravel(RailSystem.Graph, TrackGauge, authorityPathDistance, movementSign, out authorityCursor);
	}

	private double GetTailRearExtentForCurrentConvoy()
	{
		if (IsConvoyHead && ConvoySystem != null && ConvoySystem.TryGetLoadedOrderedMembers(EntityId, ConvoyMemberScratch) && ConvoyMemberScratch.Count > 0)
		{
			if (ConvoyMemberScratch[ConvoyMemberScratch.Count - 1].Entity is EntityStandardGaugeLocomotive tailVehicle) return tailVehicle.StableSourceRearExtentBlocks();
			return ConvoyMemberScratch[ConvoyMemberScratch.Count - 1].OccupancyRearExtentBlocks;
		}
		return StableSourceRearExtentBlocks();
	}

	private bool ServerAdvanceLoadedConvoyPathTape(double requestedSignedDistance, EntityStandardGaugeLocomotive leadVehicle, RailExactTurnPlan? exactTurnPlan, bool automatedMovement = false, bool useSignalAuthority = true)
	{
		if (RailSystem == null || PathTape == null) return false;
		if (!ServerEnsureLoadedConvoyPathTape(forceRebuild: false)) return false;
		if (State == EnumEntityState.Despawned) return true;

		List<IRailwayConvoyVehicle> ordered = ConvoyMemberScratch;
		ordered.Clear();
		if (IsConvoyHead && ConvoySystem != null) { if (!ConvoySystem.TryGetLoadedOrderedMembers(EntityId, ordered)) ordered.Add(this); }
		else ordered.Add(this);

		int movementSign = requestedSignedDistance >= 0 ? 1 : -1;
		double absoluteRequestedDistance = Math.Abs(requestedSignedDistance);
		if (absoluteRequestedDistance <= 1e-8) return true;

		double tailDistance = ordered.Count > 0 ? Math.Max(0, ordered[ordered.Count - 1].ConvoyDistanceBehindHead) : 0;
		double tailRear = ordered.Count > 0 && ordered[ordered.Count - 1].Entity is EntityStandardGaugeLocomotive tailVehicle
			? tailVehicle.StableSourceRearExtentBlocks()
			: StableSourceRearExtentBlocks();
		double occupiedMinPathDistance = PathHeadDistance - tailDistance - tailRear;
		double occupiedMaxPathDistance = PathHeadDistance + StableSourceFrontExtentBlocks();
		double authorityPathDistance = movementSign > 0 ? occupiedMaxPathDistance : occupiedMinPathDistance;

		if (!PathTape.TrySampleAuthoritativeCursorForTravel(RailSystem.Graph, TrackGauge, authorityPathDistance, movementSign, out _))
		{
			if (!ServerSeedPathTapeFromOrderedConvoy(ordered, force: true)) return false;
		}

		// Keep bounded branch history at both ends. The recorder reuses matching history and replaces only the side that actually diverges at a junction.
		double clientRouteLookahead = ServerDesiredClientRouteLookahead();
		PathTape.TrimForMovement(occupiedMinPathDistance, occupiedMaxPathDistance, movementSign, AuthoritativeRouteReserveBlocks, clientRouteLookahead);
		if (clientRouteLookahead > 0) { PathTape.ExtendDeterministicLookahead(RailSystem.Graph, TrackGauge, movementSign, authorityPathDistance, clientRouteLookahead); }
		if (!PathTape.TrySampleAuthoritativeCursorForTravel(RailSystem.Graph, TrackGauge, authorityPathDistance, movementSign, out var extensionCursor)) return false;

		int desiredTurn = 0;
		if (leadVehicle.WatchedAttributes.HasAttribute(RailwayVehicleShared.AttributeTurnLeverPhase))
		{
			desiredTurn = RailwayVehicleShared.GetWantTurnTravel(leadVehicle.WatchedAttributes, 1);
			if (leadVehicle.LeadEnd != SGTrainEnd.EndA) desiredTurn = -desiredTurn;
		}

		double temporarySpeed = Speed;
		double extensionTravel = 0;
		RailwayVehicleShared.DeadEndInfo deadEnd = default;
		double absoluteImpactSpeed = Math.Abs(Speed);

		PathTapeRecorder.Begin(PathTape, TrackGauge, movementSign, authorityPathDistance, 0);
		bool advanceSucceeded = RailwayVehicleShared.AdvanceAlongTrack
		(
			RailSystem.Graph,
			movementSign * absoluteRequestedDistance,
			desiredTurn,
			exactTurnPlan,
			RailSystem,
			useSignalAuthority,
			automatedMovement,
			ref extensionCursor,
			ref temporarySpeed,
			ref extensionTravel,
			trackTravelledABS: true,
			out deadEnd,
			collisionTrail: null,
			occupancyOwnerID: EntityId,
			tapeRecorder: PathTapeRecorder,
			allowStaleBlockedSelfHeal: true
		);
		PathTapeRecorder.Clear();

		if (extensionTravel <= 1e-8)
		{
			Speed = temporarySpeed;
			if (deadEnd.ClearanceBlocked) ServerStopAfterClearanceBlock(leadVehicle, absoluteImpactSpeed);
			else if (!advanceSucceeded && deadEnd.Hit) { HandlePathTapeDeadEnd(absoluteImpactSpeed, in deadEnd); }
			ServerPublishConvoyRoute();
			return true;
		}

		if (deadEnd.ClearanceBlocked)
		{
			temporarySpeed = 0;
			ServerStopAfterClearanceBlock(leadVehicle, absoluteImpactSpeed);
		}

		double proposedAbsoluteDistanceTravelled = TravelledABS + extensionTravel;
		double proposedPathHeadDistance = PathHeadDistance + movementSign * extensionTravel;

		if (clientRouteLookahead > 0)
		{
			double leadingBoundaryPathDistance = movementSign > 0 ? proposedPathHeadDistance + StableSourceFrontExtentBlocks() : proposedPathHeadDistance - tailDistance - tailRear;
			PathTape.ExtendDeterministicLookahead(RailSystem.Graph, TrackGauge, movementSign, leadingBoundaryPathDistance, clientRouteLookahead);
		}

		if (!ServerApplyPathTapeToLoadedConvoyMembers(ordered, proposedPathHeadDistance, proposedAbsoluteDistanceTravelled, temporarySpeed)) { return false; }

		if (State == EnumEntityState.Despawned) return true;

		if (!advanceSucceeded && deadEnd.Hit) { HandlePathTapeDeadEnd(absoluteImpactSpeed, in deadEnd); }

		return true;
	}

	private void ServerStopAfterClearanceBlock(EntityStandardGaugeLocomotive leadVehicle, double absoluteImpactSpeed)
	{
		if (Api?.Side != EnumAppSide.Server) return;
		StopRailMotion();
		RailwayVehicleShared.ServerShakeImpact(Api, leadVehicle.ServerPos.XYZ, absoluteImpactSpeed);
		ServerApplyPassengerImpactToConvoy
		(
			absoluteImpactSpeed,
			RailwayVehicleShared.CollisionDamageMultiplier,
			RailwayVehicleShared.AlongTrackCollisionDamageSpeedThreshold,
			RailwayVehicleShared.AlongTrackCollisionEjectSpeedThreshold
		);
		leadVehicle.SteamEngineBehaviour?.ForceDriveLeverStop();
		leadVehicle.SteamEngineBehaviour?.ForceExtinguishBoiler();
		PersistTrackState();
	}

	private void HandlePathTapeDeadEnd(double absoluteImpactSpeed, in RailwayVehicleShared.DeadEndInfo deadEnd)
	{
		PersistTrackState();
		if (!ServerDeadEndWallImpact(absoluteImpactSpeed, in deadEnd))
		{
			deadEnd.GetEndPoint(out double endX, out double endY, out double endZ);
			ServerDerailConvoy(absoluteImpactSpeed, deadEnd.Dx, deadEnd.Dz, deadEnd.MoveDirection, endX, endY, endZ);
		}
	}

	private void ServerTrimPathTapeToCurrentConvoy(IReadOnlyList<IRailwayConvoyVehicle> ordered)
	{
		if (PathTape == null || ordered == null || ordered.Count == 0) return;

		double tailDistance = Math.Max(0, ordered[ordered.Count - 1].ConvoyDistanceBehindHead);
		double tailRear = ordered[ordered.Count - 1].Entity is EntityStandardGaugeLocomotive tailVehicle ? tailVehicle.StableSourceRearExtentBlocks() : ordered[ordered.Count - 1].OccupancyRearExtentBlocks;
		double minPathDistance = PathHeadDistance - tailDistance - tailRear - AuthoritativeRouteReserveBlocks;
		double maxPathDistance = PathHeadDistance + StableSourceFrontExtentBlocks() + AuthoritativeRouteReserveBlocks;
		PathTape.TrimWholeRunsOutside(minPathDistance, maxPathDistance);
	}

	internal bool ServerApplyPathTapeToLoadedConvoyMembers()
	{
		if (Api?.Side != EnumAppSide.Server || Derailed || IsFollower) return false;
		if (RailSystem == null || PathTape == null) return false;

		List<IRailwayConvoyVehicle> ordered = ConvoyMemberScratch;
		ordered.Clear();
		if (IsConvoyHead && ConvoySystem != null) { if (!ConvoySystem.TryGetLoadedOrderedMembers(EntityId, ordered)) ordered.Add(this); }
		else ordered.Add(this);

		return ServerApplyPathTapeToLoadedConvoyMembers(ordered, PathHeadDistance, TravelledABS, Speed);
	}

	private bool ServerApplyPathTapeToLoadedConvoyMembers(IReadOnlyList<IRailwayConvoyVehicle> ordered, double pathHeadDistance, double absoluteDistanceTravelled, double speed)
	{
		if (Api?.Side != EnumAppSide.Server || Derailed || IsFollower) return false;
		if (RailSystem == null || PathTape == null || ordered == null || ordered.Count == 0) return false;

		MaterialPoseScratch.Clear();
		MaterialChunkScratch.Clear();
		for (int vehicleIndex = 0; vehicleIndex < ordered.Count; vehicleIndex++) MaterialPoseScratch.Add(default);

		int routeSpanHint = -1;
		for (int vehicleIndex = ordered.Count - 1; vehicleIndex >= 0; vehicleIndex--)
		{
			if (ordered[vehicleIndex].Entity is not EntityStandardGaugeLocomotive vehicle) return false;
			double sourceDistance = pathHeadDistance - ordered[vehicleIndex].ConvoyDistanceBehindHead;

			if (!PathTape.TrySampleAuthoritativeCursor(RailSystem.Graph, TrackGauge, sourceDistance, ref routeSpanHint, out var sampledCursor)) return false;
			sampledCursor.Gauge = TrackGauge;

			if (!RailwayVehicleShared.TryReadWorldPoseFromTrack(ref sampledCursor, out double x, out double y, out double z, out float yaw, out float roll))
			{ return false; }

			// WriteWorldPose stores these coordinates at float precision.
			// Derive residency from those same values so an exact chunk boundary cannot disagree after the commit.
			long targetChunkIndex = ServerThreeDimensionalChunkIndex((float)x, (float)y, (float)z, vehicle.ServerPos.Dimension);

			// Stay lock-free while the source point remains in its current chunk.
			// Only a real chunk-boundary crossing needs the authoritative loaded-chunk dictionary probe.
			if (targetChunkIndex != vehicle.InChunkIndex3d && !MaterialChunkScratch.Contains(targetChunkIndex))
			{
				MaterialChunkScratch.Add(targetChunkIndex);
				if (((ICoreServerAPI)Api).WorldManager.GetChunk(targetChunkIndex) == null)
				{
					return ServerTryVirtualizeLoadedConvoy(ordered, pathHeadDistance, absoluteDistanceTravelled, speed);
				}
			}

			MaterialPoseScratch[vehicleIndex] = new StagedMaterialPose(vehicle, sampledCursor, sourceDistance, x, y, z, yaw, roll, targetChunkIndex);
		}

		Speed = speed;
		TravelledABS = absoluteDistanceTravelled;
		PathHeadDistance = pathHeadDistance;

		for (int vehicleIndex = 0; vehicleIndex < ordered.Count; vehicleIndex++)
		{
			StagedMaterialPose pose = MaterialPoseScratch[vehicleIndex];
			EntityStandardGaugeLocomotive vehicle = pose.Vehicle;

			vehicle.Cursor = pose.Cursor;
			vehicle.Speed = vehicle.EntityId == EntityId ? Speed : 0;
			vehicle.TravelledABS = Math.Max(0, TravelledABS - ordered[vehicleIndex].ConvoyDistanceBehindHead);
			vehicle.PathHeadDistance = PathHeadDistance;
			vehicle.Attributes.SetDouble(SGPathSourceDistanceAttribute, pose.SourceDistance);
			vehicle.Attributes.SetInt(SGRouteEpochAttribute, PathTape.Epoch);
			if (vehicle.WatchedAttributes.GetInt(SGRouteEpochAttribute, 0) != PathTape.Epoch)
			{
				vehicle.WatchedAttributes.SetInt(SGRouteEpochAttribute, PathTape.Epoch);
				vehicle.WatchedAttributes.MarkPathDirty(SGRouteEpochAttribute);
			}

			RailwayVehicleShared.WriteWorldPose(vehicle.ServerPos, vehicle.Pos, pose.X, pose.Y, pose.Z, pose.Yaw, pose.Roll, Math.Abs(Speed));

			if (pose.ChunkIndex != vehicle.InChunkIndex3d) { Api.World.UpdateEntityChunk(vehicle, pose.ChunkIndex); }

			vehicle.MarkTrackStateDirty();
			if (!ReferenceEquals(vehicle, this)) vehicle.PersistTrackState(force: false);
		}

		ConvoyBodyDirty = false;
		ServerPublishConvoyRoute();
		return true;
	}

	private long ServerThreeDimensionalChunkIndex(double x, double y, double z, int dimension)
	{
		int chunkX = (int)Math.Floor(x / GlobalConstants.ChunkSize);
		int chunkY = (int)Math.Floor(y / GlobalConstants.ChunkSize) + dimension * GlobalConstants.DimensionSizeInChunks;
		int chunkZ = (int)Math.Floor(z / GlobalConstants.ChunkSize);
		return ((ICoreServerAPI)Api).WorldManager.ChunkIndex3D(chunkX, chunkY, chunkZ);
	}

	private bool ServerTryVirtualizeLoadedConvoy(IReadOnlyList<IRailwayConvoyVehicle> ordered, double pathHeadS, double absoluteDistanceTravelled, double speed)
	{
		if (RailSystem == null || PathTape == null || !PathTape.TrySampleAuthoritativeCursor(RailSystem.Graph, TrackGauge, pathHeadS, out var proposedCursor))
		{
			return false;
		}

		// Explicit boundary virtualization begins at the first pose that cannot exist materially.
		// Only logical rail state is advanced here, world position remains in the last loaded chunk until the existing Unload handoff removes the material shell.
		double oldPathHeadDistance = PathHeadDistance;
		double oldAbsoluteDistanceTravelled = TravelledABS;
		double oldSpeed = Speed;
		RailwayVehicleShared.RailCursor oldCursor = Cursor;

		PathHeadDistance = pathHeadS;
		TravelledABS = absoluteDistanceTravelled;
		Speed = speed;
		Cursor = proposedCursor;

		OffscreenSimulationSystem ??= Api.ModLoader.GetModSystem<OffscreenConvoySimSystem>();
		bool virtualized = OffscreenSimulationSystem?.ServerVirtualizeLoadedConvoy(ordered) == true;
		if (virtualized) return true;

		// Preflight failures (for example, an occupied passenger seat) leave the material convoy authoritative.
		// Restore the exact logical state it had before the attempted handoff.
		PathHeadDistance = oldPathHeadDistance;
		TravelledABS = oldAbsoluteDistanceTravelled;
		Speed = oldSpeed;
		Cursor = oldCursor;
		if (State == EnumEntityState.Despawned) PersistTrackState(force: true);
		return false;
	}

	internal bool ServerTryMovePathTapeBySignedDistance(RailGraphLive graph, double signedDistance, out double moved)
	{
		moved = 0;
		if (Api?.Side != EnumAppSide.Server || IsFollower || Derailed) return false;
		ConvoySystem ??= Api.ModLoader.GetModSystem<RailConvoySystem>();
		if (IsConvoyHead && ConvoySystem != null && !ConvoySystem.IsConvoyFullyLoaded(EntityId)) return false;
		RailSystem ??= Api.ModLoader.GetModSystem<RailGraphServerSystem>();
		if (RailSystem?.IsRuntimeReady != true || graph == null) return false;
		if (!ServerEnsureLoadedConvoyPathTape(forceRebuild: false)) return false;

		double beforeTravel = TravelledABS;
		double beforePath = PathHeadDistance;
		bool movementSucceeded = ServerAdvanceLoadedConvoyPathTape(signedDistance, ResolveConvoyLead(), null, useSignalAuthority: false);
		if (State == EnumEntityState.Despawned) return movementSucceeded;

		moved = PathHeadDistance - beforePath;
		if (Math.Abs(moved) <= 1e-8)
		{
			double absoluteDistanceMoved = Math.Max(0, TravelledABS - beforeTravel);
			moved = Math.Sign(signedDistance) * absoluteDistanceMoved;
		}

		StopRailMotion();
		ServerPublishOccupancy(graph);
		PersistTrackState();
		return movementSucceeded || Math.Abs(moved) > 0.01;
	}

	internal bool TryCollectPathTapeFootprintEdges(RailGraphLive graph, HashSet<ulong> destinationEdges)
	{
		if (destinationEdges == null || graph == null) return false;
		if (!ServerEnsureLoadedConvoyPathTape(forceRebuild: false)) return false;

		List<IRailwayConvoyVehicle> ordered = ConvoyMemberScratch;
		if (IsConvoyHead && ConvoySystem != null) { if (!ConvoySystem.TryGetLoadedOrderedMembers(EntityId, ordered)) ordered.Add(this); }
		else
		{
			ordered.Clear();
			ordered.Add(this);
		}

		double tailDistance = ordered.Count > 0 ? Math.Max(0, ordered[ordered.Count - 1].ConvoyDistanceBehindHead) : 0;
		double tailRear = ordered.Count > 0 && ordered[ordered.Count - 1].Entity is EntityStandardGaugeLocomotive tailVehicle ? tailVehicle.StableSourceRearExtentBlocks() : StableSourceRearExtentBlocks();

		return PathTape != null && PathTape.CollectOccupiedEdgeHashes(graph, TrackGauge, PathHeadDistance - tailDistance - tailRear, PathHeadDistance + StableSourceFrontExtentBlocks(), destinationEdges);
	}

	internal bool TryEmitPathTapeCollisionFootprint(RailTrainCollisionSystem collector, RailGraphLive graph, int bodyIndex, IReadOnlyList<IRailwayConvoyVehicle> ordered)
	{
		if (collector == null || graph == null || PathTape == null || ordered == null || ordered.Count == 0) return false;
		if (!ServerEnsureLoadedConvoyPathTape(forceRebuild: false)) return false;

		bool anySpansEmitted = false;
		int routeRunHint = -1;
		for (int vehicleIndex = ordered.Count - 1; vehicleIndex >= 0; vehicleIndex--)
		{
			if (ordered[vehicleIndex].Entity is not EntityStandardGaugeLocomotive vehicle) continue;

			double sourceDistance = PathHeadDistance - ordered[vehicleIndex].ConvoyDistanceBehindHead;
			double minPathDistance = sourceDistance - vehicle.StableSourceRearExtentBlocks();
			double maxPathDistance = sourceDistance + vehicle.StableSourceFrontExtentBlocks();
			anySpansEmitted |= PathTape.EmitRepulsionSpans(collector, graph, TrackGauge, bodyIndex, vehicleIndex, minPathDistance, maxPathDistance, ref routeRunHint);
		}

		return anySpansEmitted;
	}

	internal bool OfflineSimTryExportPathTape(out ConvoyRoute pathTape, out double pathHeadDistance)
	{
		pathTape = null!;
		pathHeadDistance = PathHeadDistance;
		if (Api?.Side != EnumAppSide.Server || IsFollower) return false;
		if (!ServerEnsureLoadedConvoyPathTape(forceRebuild: false)) return false;
		if (PathTape == null) return false;

		pathTape = ClonePathTape(PathTape);
		pathHeadDistance = PathHeadDistance;
		return true;
	}

	#endregion

	public override void OnReceivedClientPacket(IServerPlayer player, int packetID, byte[] data)
	{
		if (packetID == RailVehicleSeat.RequestCollisionDismountPacketID)
		{
			if (Api?.Side == EnumAppSide.Server &&
				RailVehicleSeat.CollisionChecksEnabled(this) &&
				player?.Entity?.MountedOn is RailVehicleSeat seat &&
				RailVehicleSeat.TryDeserializeCollisionDismountFrame(data, out RailVehicleSeat.CollisionDismountFrame frame))
			{
				Entity mountedEntity = seat.Entity ?? seat.MountSupplier?.OnEntity;
				if (mountedEntity?.EntityId == EntityId && frame.Dimension == Pos.Dimension)
				{
					seat.ServerSetCollisionDismountFrame(frame);
					try { player.Entity.TryUnmount(); }
					finally { seat.ServerClearCollisionDismountFrame(); }
				}
			}
			return;
		}

		base.OnReceivedClientPacket(player, packetID, data);
	}

	#region Seats / pickup
	public IMountableSeat CreateSeat(IMountable mountable, string seatID, SeatConfig config = null)
	{
		return new RailVehicleSeat
		(
			mountable, seatID, config, "yangtransport.railvehicleseat", "sitflooridle",
			useConfigAngleMode: true, useAttachmentPointAnchor: true, useConfigEyeHeight: true
		);
	}

	private bool CanSitOnlyOnSeatSelection(EntityAgent agent, out string errorMessage)
	{
		errorMessage = null;
		return RailwayVehicleShared.IsSeatSelectionClick(this, SeatableBehaviour, agent);
	}

	private void ServerApplyPassengerImpactToConvoy
	(
		double absoluteImpactSpeed, double damagePerBPS,
		double damageThresholdBPS, double ejectThresholdBPS, Entity? causeEntity = null
	)
	{
		if (Api?.Side != EnumAppSide.Server || IsFollower) return;

		RailwayVehicleShared.ServerApplyPassengerImpact(this, SeatableBehaviour, absoluteImpactSpeed, damagePerBPS, damageThresholdBPS, ejectThresholdBPS, causeEntity);

		long headID = IsConvoyHead ? EntityId : ConvoyHeadID;
		if (headID == 0 || ConvoySystem == null) return;
		if (!ConvoySystem.TryGetOrderedMemberIDs(headID, out var memberIDs)) return;

		for (int memberIndex = 0; memberIndex < memberIDs.Count; memberIndex++)
		{
			if (memberIDs[memberIndex] == EntityId) continue;
			if (Api.World.GetEntityById(memberIDs[memberIndex]) is EntityStandardGaugeLocomotive vehicle)
			{
				RailwayVehicleShared.ServerApplyPassengerImpact
				(
					vehicle, vehicle.SeatableBehaviour, absoluteImpactSpeed, damagePerBPS,
					damageThresholdBPS, ejectThresholdBPS, causeEntity
				);
			}
		}
	}

	internal void ServerApplyPassengerCrashImpact(double absoluteImpactSpeed, Entity? causeEntity = null)
	{
		if (Api?.Side != EnumAppSide.Server || Derailed || IsFollower) return;
		ServerApplyPassengerImpactToConvoy(absoluteImpactSpeed, RailwayVehicleShared.CrashDamageMultiplier, 0, 0, causeEntity);
	}

	private void ServerTryPickup(EntityAgent byEntity)
	{
		if (!ServerCanManuallyRemove(byEntity, out IServerPlayer serverPlayer)) return;

		ServerDropFuelAndAttachablesForRemoval();

		var item = Api.World.GetItem(new AssetLocation(Code.Domain, Code.Path));
		if (item != null)
		{
			var stack = new ItemStack(item, 1);
			if (!serverPlayer.InventoryManager.TryGiveItemstack(stack, slotNotifyEffect: true)) { Api.World.SpawnItemEntity(stack, serverPlayer.Entity.ServerPos.XYZ, null); }
		}

		Die(EnumDespawnReason.Removed, null);
	}

	internal bool ServerTryDeconstruct(EntityAgent byEntity)
	{
		if (!ServerCanManuallyRemove(byEntity, out IServerPlayer serverPlayer)) return false;
		if (!TransportDropTables.HasEntityDropTable(this, TransportDropTables.DeconstructDropsTable)) { return false; }

		ServerDropFuelAndAttachablesForRemoval();
		TransportDropTables.SpawnEntityDrops(this, TransportDropTables.DeconstructDropsTable, ServerPos.XYZ, verticalVelocityOnly: true);

		Die(EnumDespawnReason.Removed, null);
		return true;
	}

	private bool ServerCanManuallyRemove(EntityAgent byEntity, out IServerPlayer serverPlayer)
	{
		serverPlayer = null;
		if (!RailwayVehicleShared.TryGetServerPlayer(Api, byEntity, out EntityPlayer entityPlayer, out serverPlayer)) return false;

		if (RailwayVehicleShared.HasMountedPassenger(SeatableBehaviour)) { serverPlayer.SendIngameError("yangtransport:locomotive-pickup-seated"); return false; }
		if (!Derailed && ConvoyHeadID != 0) { serverPlayer.SendIngameError("yangtransport:locomotive-pickup-attachment"); return false; }

		if (SteamEngineBehaviour != null)
		{
			if (SteamEngineBehaviour.IsBurningForPickupReject()) { serverPlayer.SendIngameError("yangtransport:locomotive-pickup-running"); return false; }
			if (SteamEngineBehaviour.HasAnyFluidForPickupReject()) { serverPlayer.SendIngameError("yangtransport:locomotive-pickup-contents"); return false; }
		}

		if (StorageBehaviour != null && !StorageBehaviour.IsEmpty) { serverPlayer.SendIngameError("yangtransport:locomotive-pickup-cargo"); return false; }
		if (GetBehavior<EntityBehaviorSGRefrigeration>()?.HasContentsForPickup == true) { serverPlayer.SendIngameError("yangtransport:locomotive-pickup-contents"); return false; }
		if (!Derailed && Math.Abs(Speed) > DriveParameters.StopEpsilon) { serverPlayer.SendIngameError("yangtransport:locomotive-pickup-moving"); return false; }

		return true;
	}

	private void ServerDropFuelAndAttachablesForRemoval()
	{
		ItemStack? fuelDrop = SteamEngineBehaviour?.TakeRemainingFuelForPickupDrop();
		if (fuelDrop != null) { Api.World.SpawnItemEntity(fuelDrop, ServerPos.XYZ, null); }

		DropConductorAttachablesOnBreak();
	}

	private void DropConductorAttachablesOnBreak()
	{
		if (Api.Side != EnumAppSide.Server) return;

		var attach = ConductorAttachableBehaviour;
		var inventory = attach?.Inventory;
		if (inventory == null) return;

		var closeData = new EntityDespawnData
		{
			Reason = EnumDespawnReason.Removed,
			DamageSourceForDeath = null
		};

		for (int slotIndex = 0; slotIndex < inventory.Count; slotIndex++)
		{
			var slot = inventory[slotIndex];
			if (slot.Empty) continue;

			slot.Itemstack?.Collectible.GetCollectibleInterface<IAttachedInteractions>()?.OnEntityDespawn(slot, slotIndex, this, closeData);

			ItemStack dropStack = slot.TakeOutWhole();
			if (dropStack == null) continue;

			Api.World.SpawnItemEntity(dropStack, ServerPos.XYZ, null);
		}
	}
	#endregion

	#region Convoy API
	private void SetPreviousVehicleID(long previousVehicleID)
	{
		PreviousVehicleIDReadOnly = previousVehicleID;
		Attributes.SetLong(PreviousVehicleIDAttribute, previousVehicleID);

		if (Api?.Side == EnumAppSide.Server)
		{
			WatchedAttributes.SetLong(PreviousVehicleIDAttribute, previousVehicleID);
			WatchedAttributes.MarkPathDirty(PreviousVehicleIDAttribute);
		}
	}

	internal void ServerSetNextVehicleID(long nextVehicleID)
	{
		NextVehicleIDReadOnly = nextVehicleID;
		Attributes.SetLong(NextVehicleIDAttribute, nextVehicleID);

		if (Api?.Side == EnumAppSide.Server)
		{
			WatchedAttributes.SetLong(NextVehicleIDAttribute, nextVehicleID);
			WatchedAttributes.MarkPathDirty(NextVehicleIDAttribute);
		}
	}

	internal void ServerMarkConvoyBodyDirty() { ServerRequestConvoyBodyReapply(); }

	private void ServerRequestConvoyBodyReapply(long explicitHeadID = 0)
	{
		if (Api?.Side != EnumAppSide.Server) return;

		long headID = explicitHeadID != 0 ? explicitHeadID : (ConvoyHeadID != 0 ? ConvoyHeadID : EntityId);

		if (headID == EntityId) { if (!IsFollower) ConvoyBodyDirty = true; return; }

		if (Api.World.GetEntityById(headID) is EntityStandardGaugeLocomotive headVehicle && !headVehicle.IsFollower) { headVehicle.ConvoyBodyDirty = true; }
	}

	internal void ServerSetConvoyState(long headID, int index, long previousVehicleID, double distanceBehindHead)
	{
		if (Api?.Side == EnumAppSide.Server) RemovePublishedCollisionBody();
		long oldHeadID = ConvoyHeadID != 0 ? ConvoyHeadID : EntityId;
		bool wasReplicatedHead = Api?.Side == EnumAppSide.Server && (ConvoyHeadID == 0 || ConvoyHeadID == EntityId);
		ConvoyHeadID = headID;
		ConvoyIndex = index;
		ConvoyDistanceBehindHeadBlocks = distanceBehindHead;

		Attributes.SetLong(ConvoyHeadIDAttribute, headID);
		Attributes.SetInt(ConvoyIndexAttribute, index);
		Attributes.SetDouble(ConvoyDistanceBehindHeadAttribute, distanceBehindHead);

		SetPreviousVehicleID(previousVehicleID);

		if (Api?.Side == EnumAppSide.Server)
		{
			if (wasReplicatedHead && headID != EntityId)
			{
				RouteNetworkSystem ??= Api.ModLoader.GetModSystem<ConvoyRouteNetworkSystem>();
				RouteNetworkSystem?.RemoveServerRoute(EntityId, this);
			}
			WatchedAttributes.SetLong(ConvoyHeadIDAttribute, headID);
			WatchedAttributes.SetDouble(ConvoyDistanceBehindHeadAttribute, distanceBehindHead);
			WatchedAttributes.MarkPathDirty(ConvoyHeadIDAttribute);
			WatchedAttributes.MarkPathDirty(ConvoyDistanceBehindHeadAttribute);
			ServerRequestConvoyBodyReapply(oldHeadID);
			ServerRequestConvoyBodyReapply(headID);
		}
	}

	internal void ServerClearConvoyState()
	{
		if (Api?.Side == EnumAppSide.Server) RemovePublishedCollisionBody();
		long oldHeadID = ConvoyHeadID != 0 ? ConvoyHeadID : EntityId;
		bool wasReplicatedHead = Api?.Side == EnumAppSide.Server && (ConvoyHeadID == 0 || ConvoyHeadID == EntityId);
		ConvoyHeadID = 0;
		ConvoyIndex = 0;
		ConvoyDistanceBehindHeadBlocks = 0;

		Attributes.SetLong(ConvoyHeadIDAttribute, 0);
		Attributes.SetInt(ConvoyIndexAttribute, 0);
		Attributes.SetDouble(ConvoyDistanceBehindHeadAttribute, 0);

		SetPreviousVehicleID(0);
		ServerSetNextVehicleID(0);
		if (Api?.Side == EnumAppSide.Server)
		{
			if (wasReplicatedHead)
			{
				RouteNetworkSystem ??= Api.ModLoader.GetModSystem<ConvoyRouteNetworkSystem>();
				RouteNetworkSystem?.RemoveServerRoute(EntityId, this);
			}
			WatchedAttributes.SetLong(ConvoyHeadIDAttribute, 0);
			WatchedAttributes.SetDouble(ConvoyDistanceBehindHeadAttribute, 0);
			WatchedAttributes.MarkPathDirty(ConvoyHeadIDAttribute);
			WatchedAttributes.MarkPathDirty(ConvoyDistanceBehindHeadAttribute);
		}
		ServerSetWatchedConvoyStatistics(1, SelfWeight, 0, ComputeOccupancyRearExtentBlocks());
		ServerRequestConvoyBodyReapply(oldHeadID);
		ConvoyBodyDirty = true;
		ServerInvalidateRailBinding(persist: false);
	}

	internal void ServerInvalidateRailBinding(bool persist)
	{
		if (Api?.Side != EnumAppSide.Server) return;

		Cursor.SegmentHash = 0;
		Cursor.PolyXYZ16 = null;
		Cursor.PointCount = 0;
		Cursor.BoundGraphVersion = 0;
		Cursor.OrientationCacheSegmentHash = 0;
		Cursor.OrientationCacheSegmentIndex = -1;
		Cursor.OrientationCacheDirection = 0;

		CollisionTrail.Clear();
		PathTape?.Clear();
		ConvoyBodyDirty = true;
		MarkTrackStateDirty();

		if (persist) PersistTrackState(force: true);
	}

	internal void ServerSetWatchedConvoyStatistics(int count, int weight, double tailDistance, double occupancyRearDistance)
	{
		if (Api.Side != EnumAppSide.Server) return;

		OccupancyRearDistanceCached = Math.Max(ComputeOccupancyRearExtentBlocks(), occupancyRearDistance);

		WatchedAttributes.SetInt(ConvoyWeightAttribute, weight);
		WatchedAttributes.SetInt(ConvoyCountAttribute, count);
		WatchedAttributes.SetDouble(ConvoyTailDistanceAttribute, tailDistance);
		WatchedAttributes.MarkPathDirty(ConvoyWeightAttribute);
		WatchedAttributes.MarkPathDirty(ConvoyCountAttribute);
		WatchedAttributes.MarkPathDirty(ConvoyTailDistanceAttribute);
	}

	internal void GetCouplerWorldPositions(out double frontX, out double frontY, out double frontZ, out double rearX, out double rearY, out double rearZ)
	{
		if (!SGLocomotiveBodyTransform.TryGetBodyPose(this, out var bodyPose)) { frontX = frontY = frontZ = rearX = rearY = rearZ = 0; return; }

		double forwardX = Math.Sin(bodyPose.Yaw);
		double forwardZ = Math.Cos(bodyPose.Yaw);
		double couplerY = bodyPose.Y + Math.Max(0.25, LocomotiveStatistics?.BodyOffsetVertical ?? 0);

		frontX = bodyPose.X;
		frontY = couplerY;
		frontZ = bodyPose.Z;

		double vehicleLength = Math.Max(0.1, VehicleLength);
		rearX = bodyPose.X - forwardX * vehicleLength;
		rearY = couplerY;
		rearZ = bodyPose.Z - forwardZ * vehicleLength;
	}

	Entity IRailwayConvoyVehicle.Entity => this;
	byte IRailwayConvoyVehicle.TrackGauge => TrackGauge;
	int IRailwayConvoyVehicle.BindRadiusBlocks => BindRadiusBlocks;
	long IRailwayConvoyVehicle.ConvoyHeadEntityID => ConvoyHeadID;
	int IRailwayConvoyVehicle.ConvoyOrderIndex => ConvoyIndex;
	int IRailwayConvoyVehicle.ExpectedConvoyMemberCount => Math.Max(1, WatchedAttributes.GetInt(ConvoyCountAttribute, 1));
	long IRailwayConvoyVehicle.PreviousVehicleID => PreviousVehicleIDReadOnly;
	double IRailwayConvoyVehicle.ConvoyDistanceBehindHead => ConvoyDistanceBehindHeadBlocks;
	int IRailwayConvoyVehicle.SelfWeightCached => SelfWeight;
	double IRailwayConvoyVehicle.OccupancyRearExtentBlocks => ComputeOccupancyRearExtentBlocks();
	double IRailwayConvoyVehicle.OccupancyFrontExtentBlocks => StableSourceFrontExtentBlocks();
	bool IRailwayConvoyVehicle.HasTractionEngine => SteamEngineBehaviour != null;
	bool IRailwayConvoyVehicle.HasConductorLocustInstalled => HasConductorLocustInstalled();
	bool IRailwayConvoyVehicle.Derailed => Derailed;
	double IRailwayConvoyVehicle.ConvoySpacingToNext => ConvoySpacingForLength(VehicleLength);
	EntityBehaviorSteamPowered? IRailwayConvoyVehicle.SteamEngineBehaviour => SteamEngineBehaviour;

	void IRailwayConvoyVehicle.ServerSetConvoyState(long headID, int index, long previousVehicleID, double distanceBehindHead) => ServerSetConvoyState(headID, index, previousVehicleID, distanceBehindHead);
	void IRailwayConvoyVehicle.ServerClearConvoyState() => ServerClearConvoyState();
	private static double ConvoySpacingForLength(double vehicleLength) { return Math.Max(0.1, vehicleLength) + CouplingDistance; }
	void IRailwayConvoyVehicle.ServerInvalidateRailBinding(bool persist) => ServerInvalidateRailBinding(persist);
	void IRailwayConvoyVehicle.ServerSetConvoyStatsWatched(int count, int weight, double tailDistance, double occupancyRearDistance) => ServerSetWatchedConvoyStatistics(count, weight, tailDistance, occupancyRearDistance);
	bool IRailwayConvoyVehicle.ServerSeedConvoyRailStateFromOrderedConvoy(IReadOnlyList<IRailwayConvoyVehicle> ordered, IReadOnlyDictionary<long, double>? physicalDistanceFromHead) => ServerSeedConvoyRailStateFromOrderedConvoy(ordered, physicalDistanceFromHead);
	void IRailwayConvoyVehicle.GetCouplerWorldPositions(out double frontX, out double frontY, out double frontZ, out double rearX, out double rearY, out double rearZ) => GetCouplerWorldPositions(out frontX, out frontY, out frontZ, out rearX, out rearY, out rearZ);
	#endregion

	#region Persistence
	public override void ToBytes(BinaryWriter writer, bool forClient)
	{
		if (!forClient && Api?.Side == EnumAppSide.Server && TrackStateDirty) { PersistTrackState(force: true); }
		base.ToBytes(writer, forClient);
	}

	private void MarkTrackStateDirty() { if (Api?.Side == EnumAppSide.Server) TrackStateDirty = true; }

	private void PersistTrackState(bool force = true)
	{
		if (!force)
		{
			if (!TrackStateDirty) return;
			if (Api?.Side == EnumAppSide.Server)
			{
				long nowMS = Api.World.ElapsedMilliseconds;
				if (nowMS < NextMovingPersistenceMS) return;
				NextMovingPersistenceMS = nowMS + MovingPersistenceIntervalMS;
			}
		}

		Attributes.SetLong("segHash", unchecked((long)Cursor.SegmentHash));
		Attributes.SetInt("segIndex", Cursor.SegmentIndex);
		Attributes.SetDouble("segT01", Cursor.NormalizedSegmentProgress);
		Attributes.SetInt("dir", Cursor.Direction);
		Attributes.SetInt(SGLeadEndAttribute, (int)LeadEnd);
		Attributes.SetDouble("speed", Speed);
		Attributes.SetDouble("travelledAbs", TravelledABS);
		Attributes.SetDouble(SGPathHeadDistanceAttribute, PathHeadDistance);
		Attributes.SetDouble(SGPathSourceDistanceAttribute, PathHeadDistance - ConvoyDistanceBehindHeadBlocks);
		if (PathTape != null) Attributes.SetInt(SGRouteEpochAttribute, PathTape.Epoch);

		if (Attributes.HasAttribute(SeedYawAttribute)) Attributes.RemoveAttribute(SeedYawAttribute);
		TrackStateDirty = false;
	}
	#endregion
}


public sealed class EntityBehaviorStandardGaugeLocomotiveStats : EntityBehavior
{
	private static readonly SgLocomotiveBogieRenderSpec[] DefaultBogies =
	{
		new SgLocomotiveBogieRenderSpec("locomotives/bogies/default", 0),
		new SgLocomotiveBogieRenderSpec("locomotives/bogies/default", 4)
	};

	public int SelfWeight { get; private set; } = 5;
	internal RailVehicleWeightSpec WeightSpecification { get; private set; } = RailVehicleWeightSpec.StandardGaugeDefault;
	public double VehicleLength { get; private set; } = 6.0;

	// Renderer tuning. Body offsets are in blocks, in locomotive-local space: +Forward = ahead of the front/driver bogie, +Lateral = right side, +Vertical = up.
	public double BodyOffsetForward					{ get; private set; } = 0.0;
	public double BodyOffsetLateral					{ get; private set; } = 0.0;
	public double BodyOffsetVertical				{ get; private set; } = 0.0;
	public SgLocomotiveBogieRenderSpec[] Bogies		{ get; private set; } = CloneBogies(DefaultBogies);
	public double FrontBogieOffset					{ get; private set; } = 0.0;
	public double RearBogieOffset					{ get; private set; } = 4.0;


	internal KinematicDrive.Parameters DriveParameters { get; private set; } = new(
		HardMaxSpeed: 200,
		BaseResistance: 0.45,
		WeightResistance: 0.05,
		BrakeDeceleration: 12.0,
		StopEpsilon: 0.02
	);

	public float ReverseMaxSpeedMultiplier { get; private set; } = 0.4f;

	public EntityBehaviorStandardGaugeLocomotiveStats(Entity entity) : base(entity) { }

	public override string PropertyName() => "sglocostats";

	public override void Initialize(EntityProperties properties, JsonObject typeAttributes)
	{
		base.Initialize(properties, typeAttributes);

		JsonObject configurationRoot = typeAttributes != null && typeAttributes.Exists && typeAttributes["SGLocomotive"].Exists
			? typeAttributes
			: properties.Attributes;

		JsonObject locomotiveConfiguration = configurationRoot?["SGLocomotive"];
		if (locomotiveConfiguration == null || !locomotiveConfiguration.Exists) return;

		WeightSpecification = RailVehicleWeightSpec.FromJson(locomotiveConfiguration, defaultSelfWeight: 5, defaultAttachmentWeight: 0);
		SelfWeight = WeightSpecification.SelfWeight;
		VehicleLength = Math.Max(0.1, locomotiveConfiguration["VehicleLength"].AsDouble(VehicleLength));

		var driveConfiguration = locomotiveConfiguration["Drive"];
		if (driveConfiguration.Exists)
		{
			DriveParameters = new KinematicDrive.Parameters
			(
				HardMaxSpeed:		driveConfiguration["HardMaxSpeed"].AsDouble(DriveParameters.HardMaxSpeed),
				BaseResistance:		driveConfiguration["Roll0"].AsDouble(DriveParameters.BaseResistance),
				WeightResistance:	driveConfiguration["RollW"].AsDouble(DriveParameters.WeightResistance),
				BrakeDeceleration:	driveConfiguration["BrakeDecel"].AsDouble(DriveParameters.BrakeDeceleration),
				StopEpsilon:		driveConfiguration["StopEpsilon"].AsDouble(DriveParameters.StopEpsilon)
			);

			ReverseMaxSpeedMultiplier = GameMath.Clamp(driveConfiguration["ReverseMaxSpeedMul"].AsFloat(ReverseMaxSpeedMultiplier), 0.05f, 1f );
		}

		var renderConfiguration = locomotiveConfiguration["Render"];
		if (renderConfiguration.Exists)
		{
			BodyOffsetForward = renderConfiguration["BodyOffsetForward"].AsDouble(BodyOffsetForward);
			BodyOffsetLateral = renderConfiguration["BodyOffsetLateral"].AsDouble(BodyOffsetLateral);
			BodyOffsetVertical = renderConfiguration["BodyOffsetVertical"].AsDouble(BodyOffsetVertical);

			JsonObject[] bogieObjects = renderConfiguration["Bogies"].AsArray();
			if (bogieObjects != null && bogieObjects.Length > 0)
			{
				var parsedBogies = new List<SgLocomotiveBogieRenderSpec>(bogieObjects.Length);

				foreach (JsonObject bogieObject in bogieObjects)
				{
					if (bogieObject == null || !bogieObject.Exists) continue;

					string shapePath =
						bogieObject["Shape"].AsString("locomotives/bogies/default");

					double offsetForward = bogieObject["OffsetForward"].AsDouble(double.NaN);
					if (double.IsNaN(offsetForward)) offsetForward = 0.0;

					var bogieSpecification = new SgLocomotiveBogieRenderSpec(shapePath, offsetForward)
					{
						ModelOffsetX = (float)bogieObject["ModelOffsetX"].AsDouble(0.0),
						ModelOffsetY = (float)bogieObject["ModelOffsetY"].AsDouble(0.0),
						ModelOffsetZ = (float)bogieObject["ModelOffsetZ"].AsDouble(0.0)
					};

					JsonObject animationConfiguration = bogieObject["Animation"];
					if (animationConfiguration.Exists)
					{
						bogieSpecification.AnimationCode = animationConfiguration["Code"].AsString(bogieSpecification.AnimationCode);
						bogieSpecification.DistancePerCycle = Math.Max(1e-6, animationConfiguration["DistancePerCycle"].AsDouble(bogieSpecification.DistancePerCycle));
						bogieSpecification.AnimationDirection = animationConfiguration["Direction"].AsFloat(bogieSpecification.AnimationDirection);
						bogieSpecification.AnimationPhaseOffset = animationConfiguration["PhaseOffset"].AsFloat(bogieSpecification.AnimationPhaseOffset);
					}

					parsedBogies.Add(bogieSpecification);
				}

				if (parsedBogies.Count > 0) Bogies = parsedBogies.ToArray();
			}
			else
			{
				string bogieShape = renderConfiguration["BogieShape"].AsString(null);

				double bogieDistance = renderConfiguration["BogieDistance"].AsDouble(double.NaN);
				if (!double.IsNaN(bogieDistance) && !string.IsNullOrEmpty(bogieShape))
				{
					Bogies = new[]
					{
						new SgLocomotiveBogieRenderSpec(bogieShape, 0),
						new SgLocomotiveBogieRenderSpec(bogieShape, bogieDistance)
					};
				}
			}
		}

		RefreshBogieExtents();
	}

	private void RefreshBogieExtents()
	{
		if (Bogies == null || Bogies.Length == 0)
		{
			FrontBogieOffset = 0;
			RearBogieOffset = 0;
			return;
		}

		FrontBogieOffset = RearBogieOffset = Bogies[0].OffsetForward;
		for (int bogieIndex = 1; bogieIndex < Bogies.Length; bogieIndex++)
		{
			double offset = Bogies[bogieIndex].OffsetForward;
			if (offset < FrontBogieOffset) FrontBogieOffset = offset;
			if (offset > RearBogieOffset) RearBogieOffset = offset;
		}
	}

	private static SgLocomotiveBogieRenderSpec[] CloneBogies(SgLocomotiveBogieRenderSpec[] source)
	{
		var clone = new SgLocomotiveBogieRenderSpec[source.Length];
		for (int bogieIndex = 0; bogieIndex < source.Length; bogieIndex++) clone[bogieIndex] = source[bogieIndex].Clone();
		return clone;
	}
}

public sealed class SgLocomotiveBogieRenderSpec
{
	public string ShapePath { get; set; } = "locomotives/bogies/default";

	/// Distance in blocks from the front/driver bogie toward the rear of the vehicle. The foremost bogie is the driver and stays at the entity position.
	public double OffsetForward { get; set; }

	public float ModelOffsetX { get; set; }
	public float ModelOffsetY { get; set; }
	public float ModelOffsetZ { get; set; }

	// Cosmetic, client-only mechanical animation. DistancePerCycle describes how many blocks of rail travel the complete authored animation clip represents.
	public string AnimationCode { get; set; } = "move";
	public double DistancePerCycle { get; set; } = RailMechanicalAnimation.DefaultSmallWheelDistancePerCycle;
	public float AnimationDirection { get; set; } = 1f;
	public float AnimationPhaseOffset { get; set; }

	public SgLocomotiveBogieRenderSpec() { }

	public SgLocomotiveBogieRenderSpec(string shapePath, double offsetForward)
	{
		ShapePath = string.IsNullOrEmpty(shapePath) ? "locomotives/bogies/default" : shapePath;
		OffsetForward = offsetForward;
	}

	public SgLocomotiveBogieRenderSpec Clone()
	{
		return new SgLocomotiveBogieRenderSpec(ShapePath, OffsetForward)
		{
			ModelOffsetX = ModelOffsetX,
			ModelOffsetY = ModelOffsetY,
			ModelOffsetZ = ModelOffsetZ,
			AnimationCode = AnimationCode,
			DistancePerCycle = DistancePerCycle,
			AnimationDirection = AnimationDirection,
			AnimationPhaseOffset = AnimationPhaseOffset
		};
	}
}

internal enum SGTrainEnd { EndA = 0, EndB = 1 } // Bit stupid but needed

internal static class SGTrainEndUtil
{
	public static SGTrainEnd FromInt(int value) => value == (int)SGTrainEnd.EndB ? SGTrainEnd.EndB : SGTrainEnd.EndA;
	public static SGTrainEnd Opposite(SGTrainEnd end) => end == SGTrainEnd.EndA ? SGTrainEnd.EndB : SGTrainEnd.EndA;
}
