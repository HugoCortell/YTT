using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.API.Util;
using System.Text;
using Vintagestory.API.Config;
using System.Runtime.CompilerServices;

namespace YangTransport;

// Kinematic minecart. Convoy followers are sampled from the shared rail tape.
public sealed partial class EntityMinecart : Entity, ISeatInstSupplier, IRailwayConvoyVehicle, IRailLivingCollisionSource
{
	// Core variables (speeds are in blocks per second / bps^2)
	internal const double CouplingDistance = 1.2; // Center-to-center spacing between carts in a convoy

	// Kinematic traction/resistance model (shared with other rolling stock)
	private static readonly KinematicDrive.Parameters DriveParameters = new
	(
		HardMaxSpeed: 200, // Safety limit
		BaseResistance: 0.35,
		WeightResistance: 0.08,
		BrakeDeceleration: 10.0,
		StopEpsilon: 0.02
	);
	private const byte TrackGauge = 0;

	// Shared rail cursor (server authoritative, simulated head only)
	private RailwayVehicleShared.RailCursor Cursor;

	private readonly List<ulong> BindCandidates = new();
	private RailGraphServerSystem? RailSystem;
	private RailConvoySystem? ConvoySystem;
	private RailAutomationPathingSystem? AutomationSystem;

	private bool Breaking;

	// Derailed Physics
	private const string DerailedAttributeKey = "derailed";
	private bool Derailed;
	private readonly Vec3d DerailedVelocityBPS = new(); // blocks per second
	private float DerailedYawVelocityRadians; // yaw-only spin, no pitch/roll (prevents ground clipping)

	// Make heavy rolling stock drop quickly once it leaves the rails.
	private const double DerailedGravityMultiplier = 6.0;

	// Horizontal dampening
	private const double DerailedLinearDrag = 0.28;	// 1/s (ground)
	private const double DerailedAirDrag = 0.06;	// 1/s (air)

	// Chaos spin/tumble (roll only)
	private const double DerailedYawDrag = 2.0;			// 1/s
	private const double DerailedImpactToYaw = 0.035;	// spin impulse per bps of impact

	private const double DerailedHorizontalRestitution = 0.28;	// bounce off walls (0-1)
	private const double DerailedStopEpsilon = 0.03;

	private readonly Vec3d DerailedNewPositionScratch = new();
	private readonly BlockPos BelowBlockPositionScratch = new();
	private const double DerailedNudgeForward = 0.25, DerailedNudgeSide = 0.10;

	private float ReverseMaxSpeedMultiplier = 0.4f;

	// Dynamic flatbed storage hitbox: shrink the storage selection box height when no attachment is present.
	// Implemented by swapping the per-entity selection box AttachmentPoint to a private clone with adjusted ParentElement bounds.
	private AttachmentPoint DynamicStorageAttachmentPoint;
	private ShapeElement DynamicStorageParentElement;
	private double DynamicStorageFromY;
	private double DynamicStorageFullToY;
	private double Speed; // signed movement in convoy rail-tape S coordinates.
	private double TravelledABS; // monotonic distance counter kept for HUD/debug and coarse living-collision throttling.
	private ConvoyRoute? RailTape;
	private readonly ConvoyRouteRecorder RailTapeRecorder = new();
	private double RailRootDistance;
	private const double RailTapeSeedBaseDistance = 64.0;
	private const double RailTapeMarginBlocks = RailTrainCollisionSystem.RouteHistoryRequiredBlocks;
	private readonly List<IRailwayConvoyVehicle> ConvoyMemberScratch = new(16);
	private const long MovingPersistenceIntervalMS = 1000;
	private long NextMovingPersistenceMS;
	private bool TrackStateDirty;

	// Convoy (server authoritative ordering, followers are rail-tape readers)
	private long ConvoyHeadID;	// 0 = free cart, else entityId of head cart
	private int ConvoyIndex;	// 0=head, 1/N-1 followers (ignored if ConvoyHeadId==0)
	private double ConvoyDistanceBehindHeadBlocks;
	private EntityMinecart? ConvoyHeadReference;
	private long PreviousCartIDReadOnly; // direct tether to the cart in front (0 means none)
	public long PreviousCartID => PreviousCartIDReadOnly;

	internal long ConvoyHeadEntityID => ConvoyHeadID;
	internal int ConvoyOrderIndex => ConvoyIndex;
	internal bool IsEngineCart => SteamEngineBehaviour != null;
	internal int SelfWeightCached => SelfWeight;

	private const string ConvoyHeadIDAttributeKey = "convoyHeadId";
	private const string ConvoyIndexAttributeKey = "convoyIndex";
	private const string ConvoyDistanceBehindHeadAttributeKey = "convoyDistanceBehindHead";
	private const string PreviousCartIDAttributeKey = "prevCartId"; // also mirrored to WatchedAttributes for client visuals

	// Diegetic controls
	// Keys + element names live in RailwayVehicleShared to avoid drift across systems.
	private const string ConvoyWeightAttributeKey = "convoyWeight";
	private const string ConvoyCountAttributeKey = "convoyCount";
	
	// Cached behaviors
	private EntityBehaviorSeatable? SeatableBehaviour;
	private EntityBehaviorAttachable? AttachableBehaviour;

	// Cached engine behavior (null if this cart is not an engine cart)
	private EntityBehaviorSteamPowered? SteamEngineBehaviour;

	// Cached weight
	private RailVehicleWeightSpec WeightSpecification = RailVehicleWeightSpec.MinecartDefault;
	private int SelfWeight = 1;

	// Cached convoy stats (resolved via RailConvoySystem)
	private struct ConvoyStaticStats { public int Count; public int Weight; public long TailID; public double TailDistance; }
	private const double OccupancyHalfLengthBlocks = 0.5; // True 1-block body, endpoint/probe sensors no longer create idle spacing.
	private double OccupancyRearDistanceCached = OccupancyHalfLengthBlocks;

	// Allow driving from a follower
	private const double MaxAttachDistance = 4.0;
	private const double MaxAttachDistanceSQ = MaxAttachDistance * MaxAttachDistance;


	// Living collision (leader only, server only)
	private const double LivingMinSpeedBPS = 0.2;
	private const double LivingCheckTravelStep = 0.25;
	private const double LivingMaxSweepBlocks = 16.0;
	private const double CartRadius = 0.55;

	private double LastLivingCheckTravelABS;
	private double LastLivingCheckRailRootDistance;
	private bool LivingCollisionSweepInitialized;
	private RailLivingCollisionSystem? LivingCollisionSystem;

	// Dead-end wall check scratch
	private readonly BlockPos DeadEndBlockPositionScratch = new();

	#region Init & De-Init
	public override void Initialize(EntityProperties properties, ICoreAPI coreAPI, long chunkIndex)
	{
		base.Initialize(properties, coreAPI, chunkIndex);

		Cursor = default;
		Cursor.Gauge = TrackGauge;
		Cursor.Direction = 1;
		Cursor.NormalizedSegmentProgress = 0.5;


		SeatableBehaviour = GetBehavior<EntityBehaviorSeatable>();
		AttachableBehaviour = GetBehavior<EntityBehaviorAttachable>();
		SteamEngineBehaviour = GetBehavior<EntityBehaviorSteamPowered>();
		WeightSpecification = RailVehicleWeightSpec.FromJson(properties.Attributes?["RailVehicle"], defaultSelfWeight: 1, defaultAttachmentWeight: 0);

		// Slow down maximum speed when running in reverse (lead is not simulated head)
		if (properties.Attributes != null)
		{
			ReverseMaxSpeedMultiplier = GameMath.Clamp(properties.Attributes["reverseMaxSpeedMul"].AsFloat(ReverseMaxSpeedMultiplier), 0.05f, 1f);
		}

		if (coreAPI.Side == EnumAppSide.Server) LivingCollisionSystem = coreAPI.ModLoader.GetModSystem<RailLivingCollisionSystem>();

		// If any storage/attachment is installed, the cart is no longer rideable
		if (SeatableBehaviour != null) { SeatableBehaviour.CanSit += CanSitOnlyWhenNoAttachments; }

		// Cached self-weight (recomputed when attachments change, authoritative recompute happens in OnEntityLoaded/OnEntitySpawn)
		SelfWeight = WeightSpecification.ComputeTotalWeight(AttachableBehaviour);

		// Attachable inventory is persisted/synced via WatchedAttributes ("wearablesInv"), which may be constructed after Initialize().
		// Always listen for changes, even if the inventory isn't ready yet.
		if (coreAPI.Side == EnumAppSide.Server && AttachableBehaviour != null)
		{
			WatchedAttributes.RegisterModifiedListener("wearablesInv", () => { RecalculateSelfWeight(); });
		}

		// Load persistent state (clean-slate convoy system)
		ConvoyHeadID = Attributes.GetLong(ConvoyHeadIDAttributeKey, 0);
		ConvoyIndex = Attributes.GetInt(ConvoyIndexAttributeKey, 0);
		ConvoyDistanceBehindHeadBlocks = Attributes.GetDouble(ConvoyDistanceBehindHeadAttributeKey, Math.Max(0, ConvoyIndex) * CouplingDistance);
		PreviousCartIDReadOnly = Attributes.GetLong(PreviousCartIDAttributeKey, 0);

		// Ensure clients get the tether info (WatchedAttributes are what get synced)
		if (coreAPI.Side == EnumAppSide.Server)
		{
			WatchedAttributes.SetLong(PreviousCartIDAttributeKey, PreviousCartIDReadOnly);
			WatchedAttributes.MarkPathDirty(PreviousCartIDAttributeKey);
		}

		// Keep cached fields in sync on the client (network sync uses WatchedAttributes)
		if (coreAPI.Side == EnumAppSide.Client)
		{
			// Grab the current synced value if already present
			PreviousCartIDReadOnly = WatchedAttributes.GetLong(PreviousCartIDAttributeKey, PreviousCartIDReadOnly);

			// Update when server changes it
			WatchedAttributes.RegisterModifiedListener
			(
				PreviousCartIDAttributeKey, () =>
				{ PreviousCartIDReadOnly = WatchedAttributes.GetLong(PreviousCartIDAttributeKey, 0); }
			);
		}

		Speed = Attributes.GetDouble("speed", 0.0);
		TravelledABS = Attributes.GetDouble("travelledAbs", 0.0);
		RailRootDistance = Attributes.GetDouble("railRootS", Math.Max(RailTapeSeedBaseDistance, TravelledABS));

		Cursor.SegmentHash = unchecked((ulong)Attributes.GetLong("segHash", 0));
		Cursor.SegmentIndex = Attributes.GetInt("segIndex", 0);
		Cursor.NormalizedSegmentProgress = Attributes.GetDouble("segT01", 0.5);
		Cursor.Direction = Attributes.GetInt("dir", 1) >= 0 ? 1 : -1;

		// Derailed state (off-rails physics)
		Derailed = Attributes.GetBool(DerailedAttributeKey, false);
		if (Derailed)
		{
			// Clear rail binding - we must NOT auto-rebind on load.
			Cursor.ClearBinding();
			RailwayVehicleShared.StopRailMotion(this, ref Speed);
			DerailedVelocityBPS.Set(0, 0, 0);
			DerailedYawVelocityRadians = 0f;
			ServerPos.Roll = 0f;
			ServerPos.Pitch = 0f;

			if (coreAPI.Side == EnumAppSide.Server)
			{
				WatchedAttributes.SetBool(DerailedAttributeKey, true);
				WatchedAttributes.MarkPathDirty(DerailedAttributeKey);
			}
		}

		// Server-side systems
		if (coreAPI.Side == EnumAppSide.Server)
		{
			ConvoySystem = coreAPI.ModLoader.GetModSystem<RailConvoySystem>();
			RailSystem = coreAPI.ModLoader.GetModSystem<RailGraphServerSystem>();

			// Derailed carts are always treated as free.
			if (Derailed)
			{
				ConvoyHeadID = 0;
				ConvoyIndex = 0;
				ConvoyDistanceBehindHeadBlocks = 0;
				PreviousCartIDReadOnly = 0;
				Attributes.SetLong(ConvoyHeadIDAttributeKey, 0);
				Attributes.SetInt(ConvoyIndexAttributeKey, 0);
				Attributes.SetDouble(ConvoyDistanceBehindHeadAttributeKey, 0);
				SetPreviousCartID(0);
			}
		}

		if (Derailed) return;

		if (coreAPI.Side == EnumAppSide.Server)
		{
			TryEnsureBound(forceRebind: false);
			if (IsSimulationHead) { WriteWorldPosFromTrack(); }
		}
	}

	public override void OnEntityLoaded()
	{
		base.OnEntityLoaded();
		if (Api.Side != EnumAppSide.Server) return;

		RecalculateSelfWeight(notifyConvoy: false);
	}


	public override void OnEntitySpawn()
	{
		base.OnEntitySpawn();
		if (Api.Side != EnumAppSide.Server) return;

		RecalculateSelfWeight(notifyConvoy: false);
		if (Derailed) return;

		TryEnsureBound(forceRebind: Cursor.SegmentHash == 0);

		if (!IsFollower && Cursor.SegmentHash != 0 && Attributes.HasAttribute("seedYaw"))
		{
			float seedYaw = Attributes.GetFloat("seedYaw", ServerPos.Yaw);
			ChooseForwardDirectionFromYaw(seedYaw);
			PersistTrackState();
		}

		if (!IsFollower) WriteWorldPosFromTrack();
	}

	public override void OnEntityDespawn(EntityDespawnData despawn)
	{
		if (Api?.Side == EnumAppSide.Server)
		{
			if (TrackStateDirty) PersistTrackState(force: true);
			if (!IsFollower) RemovePublishedCollisionBody();
			RailSystem?.ReleaseLoadedRouteInterest(EntityId);
			
			if (despawn?.Reason != EnumDespawnReason.Unload || Derailed) ServerVacateOccupancyZone(); // Only the simulated head owns occupancy
		}
		base.OnEntityDespawn(despawn);
	}

	public override void ToBytes(BinaryWriter writer, bool forClient)
	{
		if (!forClient && Api?.Side == EnumAppSide.Server && TrackStateDirty) { PersistTrackState(force: true); }
		base.ToBytes(writer, forClient);
	}


	public override string GetName()
	{
		string baseName = base.GetName();

		if (!(Api?.Side == EnumAppSide.Client ? WatchedAttributes.GetBool(DerailedAttributeKey, false) : Derailed)) return baseName;
		return $"{Lang.Get("yangtransport:locomotive-state-derailed")} {baseName}";
	}


	public override string GetInfoText()
	{
		string baseText = base.GetInfoText();
		StringBuilder informationTextBuilder = new StringBuilder(baseText);
		if (informationTextBuilder.Length > 0 && informationTextBuilder[informationTextBuilder.Length - 1] != '\n') informationTextBuilder.AppendLine();

		bool isFollower = ConvoyHeadID != 0 && ConvoyHeadID != EntityId;
		bool isSimulationHead = !isFollower;

		// Speed + weight are most meaningful for the simulated head, but we show them on followers too by referring to the head.
		double speed = 0;
		int weight = SelfWeight;

		if (Api.Side == EnumAppSide.Client)
		{
			EntityMinecart? head = isSimulationHead ? this : Api.World.GetEntityById(ConvoyHeadID) as EntityMinecart;
			speed = RailwayVehicleShared.GetMotionSpeedBPS(head ?? this);

			if (isSimulationHead)	{ weight = WatchedAttributes.GetInt(ConvoyWeightAttributeKey, SelfWeight); }
			else if (head != null)	{ weight = head.WatchedAttributes.GetInt(ConvoyWeightAttributeKey, SelfWeight); }
		}
		else
		{
			if (isSimulationHead)
			{
				speed = Math.Abs(Speed);
				weight = GetConvoyStaticStats().Weight;
			}
			else if (Api.World.GetEntityById(ConvoyHeadID) is EntityMinecart head)
			{
				speed = Math.Abs(head.Speed);
				weight = head.GetConvoyStaticStats().Weight;
			}
		}

		bool showEngineInformation = isSimulationHead && SteamEngineBehaviour != null;
		float trackPenalty = Api.Side == EnumAppSide.Client ? WatchedAttributes.GetFloat(RailwayVehicleShared.AttributeTrackPenalty, 1f) : Cursor.SegmentMaxSpeedFactor;

		RailwayVehicleShared.AppendLocomotiveHUDInfo(informationTextBuilder, this, SteamEngineBehaviour, speed, weight, showEngineInformation, ConvoyIndex, trackPenalty);
		if (RailSystemDebug.ClientAutomationDebugHUDEnabled) RailSystemDebug.AppendAutomationDebugHUDInfo(this, informationTextBuilder);
		return informationTextBuilder.ToString();
	}



	private void BreakIntoItem(bool breakTrain)
	{
		if (Breaking) return;
		Breaking = true;

		// Unmount passengers cleanly
		var seats = SeatableBehaviour?.Seats;
		if (seats != null) { for (int seatIndex = 0; seatIndex < seats.Length; seatIndex++) { if (seats[seatIndex]?.Passenger is EntityAgent agent) { agent.TryUnmount(); } } }

		DropAttachablesOnBreak(); // Drop attachments before we despawn.

		// Break the whole convoy if we're the head
		if (breakTrain && Api.Side == EnumAppSide.Server && IsConvoyHead)
		{
			if (ConvoySystem != null && ConvoySystem.TryGetOrderedMemberIDs(EntityId, out var memberIDs))
			{
				for (int memberIndex = 1; memberIndex < memberIDs.Count; memberIndex++)
				{
					if (Api.World.GetEntityById(memberIDs[memberIndex]) is EntityMinecart minecart) { minecart.BreakIntoItem(breakTrain: false); }
				}
			}
		}

		// Drop minecart item
		var item = Api.World.GetItem(new AssetLocation(Code.Domain, Code.Path));
		if (item != null)
		{
			var stack = new ItemStack(item, 1);
			Api.World.SpawnItemEntity(stack, ServerPos.XYZ, null);
		}

		Die(EnumDespawnReason.Removed, null);
	}


	/// Drops every attachable item currently installed on this cart.
	private void DropAttachablesOnBreak()
	{
		if (Api.Side != EnumAppSide.Server) return;

		var attachmentBehavior = AttachableBehaviour;
		var attachmentInventory = attachmentBehavior?.Inventory;
		if (attachmentInventory == null) return;

		var despawnData = new EntityDespawnData
		{
			Reason = EnumDespawnReason.Removed,
			DamageSourceForDeath = null
		};

		for (int inventoryIndex = 0; inventoryIndex < attachmentInventory.Count; inventoryIndex++)
		{
			var slot = attachmentInventory[inventoryIndex];
			if (slot.Empty) continue;

			// Let the attachment do its standard cleanup (closes GUIs, etc).
			slot.Itemstack?.Collectible.GetCollectibleInterface<IAttachedInteractions>()?.OnEntityDespawn(slot, inventoryIndex, this, despawnData);

			ItemStack dropStack = slot.TakeOutWhole();
			if (dropStack == null) continue;

			Api.World.SpawnItemEntity(dropStack, ServerPos.XYZ, null);
		}
	}
	#endregion

	#region Interaction
	private bool IsFollower => ConvoyHeadID != 0 && ConvoyHeadID != EntityId;
	bool IsSimulationHead => !IsFollower;
	bool IsConvoyHead => ConvoyHeadID == EntityId;
	public override void OnInteract(EntityAgent byEntity, ItemSlot itemSlot, Vec3d hitPosition, EnumInteractMode mode)
	{
		if (TryHandleConductorLocustInstall(byEntity, itemSlot, mode)) return;

		// Linkage pole coupler | Right-click: tether (2-click), Left-click: untether.
		if (itemSlot?.Itemstack?.Collectible is ItemLinkagePole)
		{
			if (mode == EnumInteractMode.Attack)
			{
				if (Api.Side == EnumAppSide.Server) { ConvoyTethering.HandleLinkagePoleUntetherClick(this, byEntity, itemSlot); }
				return;
			}

			if (mode == EnumInteractMode.Interact)
			{
				if (Api.Side == EnumAppSide.Server) { ConvoyTethering.HandleLinkagePoleTetherClick(this, byEntity, itemSlot); }
				return; // prevent seatable mounting when holding the linkage pole
			}
		}

		// Sneak + right-click with empty hand: pick up carts. Run on BOTH client + server to prevent client-side seat-mount prediction.
		if (RailwayVehicleShared.IsSneakEmptyHandPickupClick(byEntity, itemSlot, mode))
		{
			if (Api.Side == EnumAppSide.Server) { ServerTryPickup(byEntity); } return;
		}

		// Ctrl + right-click with empty hand: remove the storage attachment. If it contains items, reject - player must empty it first.
		if (mode == EnumInteractMode.Interact && byEntity.Controls.CtrlKey && (itemSlot == null || itemSlot.Empty))
		{
			int selectionBoxIndex = (byEntity as EntityPlayer)?.EntitySelection?.SelectionBoxIndex ?? -1;
			if (selectionBoxIndex > 0)
			{
				var attachmentInventory = AttachableBehaviour?.Inventory;
				if (attachmentInventory != null && attachmentInventory.Count > 0 && !attachmentInventory[0].Empty) { if (Api.Side == EnumAppSide.Server) { ServerDetachStorageIfEmpty(byEntity); } return; }
			}
		}

		base.OnInteract(byEntity, itemSlot, hitPosition, mode);
	}
	#endregion

	#region Life Cycle
	// The vanilla game keeps trying to create animal corpses for our locomotives,
	// so all this code is just to try and plug any path to corpse creation. Normal "deaths" should just result in derailment.
	// Boiler explosions and kill commands should immediately trigger a de-spawn. Never allow a fucking corpse to be made.
	public override void Die(EnumDespawnReason reason = EnumDespawnReason.Death, DamageSource damageSourceForDeath = null)
	{
		if (!Alive) return;

		// Client: accept server-driven state.
		if (Api.Side != EnumAppSide.Server) { base.Die(reason, damageSourceForDeath); return; }

		// Preserve unload semantics for offscreen simulation / streaming.
		if (reason == EnumDespawnReason.Unload) { base.Die(reason, damageSourceForDeath); return; }

		if (reason == EnumDespawnReason.Death)
		{
			if (RailwayVehicleShared.IsImmediateRemovalDeath(damageSourceForDeath))
			{
				// Delete this cart immediately. Any other carts that were attached should derail.
				ServerDerailConvoyMembers(excludeThis: true);
				ServerHardDespawn(EnumDespawnReason.Removed, damageSourceForDeath);
				return;
			}

			// All other deaths: never die, derail instead.
			ServerDerailConvoyMembers(excludeThis: false);
			return;
		}

		// Non-death reasons: despawn immediately to avoid a "dead tick" state.
		ServerHardDespawn(reason, damageSourceForDeath);
	}


	private void ServerHardDespawn(EnumDespawnReason reason, DamageSource deathSource)
	{
		if (RailwayVehicleShared.TryServerHardDespawn(this, reason, deathSource)) return;

		base.Die(reason, deathSource); // Should never happen in reality, but keep a safe fallback.
	}

	private void ServerDerailConvoyMembers(bool excludeThis)
	{
		if (Api.Side != EnumAppSide.Server) return;

		List<EntityMinecart> carts = new();

		long headID = 0;
		if (ConvoyHeadID != 0) { headID = IsConvoyHead ? EntityId : ConvoyHeadID; }

		if (headID != 0 && ConvoySystem != null && ConvoySystem.TryGetOrderedMemberIDs(headID, out var memberIDs))
		{
			for (int memberIndex = 0; memberIndex < memberIDs.Count; memberIndex++)
			{
				if (Api.World.GetEntityById(memberIDs[memberIndex]) is EntityMinecart cart)
				{
					if (excludeThis && cart.EntityId == EntityId) continue;
					carts.Add(cart);
				}
			}
		}
		else { if (!excludeThis) carts.Add(this); }

		if (carts.Count == 0) return;

		// Approximate derail direction from current facing (good enough, avoids deep damage-type branching).
		double yaw = ServerPos.Yaw;
		double forwardX = Math.Sin(yaw);
		double forwardZ = Math.Cos(yaw);
		double lengthSQ = forwardX * forwardX + forwardZ * forwardZ;
		if (lengthSQ < 1e-8) { forwardX = 0; forwardZ = 1; lengthSQ = 1; }
		double inverseLength = 1.0 / Math.Sqrt(lengthSQ);
		forwardX *= inverseLength;
		forwardZ *= inverseLength;

		double sideX = -forwardZ;
		double sideZ = forwardX;

		double impactSpeedABS = Math.Abs(Speed);
		Random random = Api.World.Rand;

		for (int cartIndex = 0; cartIndex < carts.Count; cartIndex++)
		{
			carts[cartIndex].ServerSetDerailedState(impactSpeedABS, forwardX, forwardZ, sideX, sideZ, cartIndex, carts.Count, random);
		}
	}

	public override void OnReceivedClientPacket(IServerPlayer player, int packetID, byte[] data)
	{
		if (packetID == SteamEnginePacketIds.SetDriveLeverPhase || packetID == SteamEnginePacketIds.SetTurnLeverPhase)
		{
			ServerTryApplyConvoyLeverInput(player, packetID, data);
			return;
		}

		base.OnReceivedClientPacket(player, packetID, data);
	}

	private bool ServerTryApplyConvoyLeverInput(IServerPlayer player, int packetID, byte[] data)
	{
		if (Api.Side != EnumAppSide.Server) return false;
		if (player?.Entity?.MountedOn == null) return false;

		IMountableSeat seat = player.Entity.MountedOn;
		Entity? mountedEntity = seat.Entity ?? seat.MountSupplier?.OnEntity;
		if (mountedEntity?.EntityId != EntityId) return false;

		EntityMinecart lead = ResolveConvoyLead();
		Vec3d interactionPos = seat.SeatPosition.XYZ;
		return lead.SteamEngineBehaviour?.ServerTryApplyRemoteLeverInputFromConvoySeat(player, interactionPos, packetID, data) == true;
	}

	public override void OnGameTick(float deltaTime)
	{
		base.OnGameTick(deltaTime);
		if (Api.Side != EnumAppSide.Server) return;

		// Derailed, off-rails sliding physics (runs per-entity)
		if (Derailed)
		{
			RailSystem?.ReleaseLoadedRouteInterest(EntityId);
			TickDerailed(deltaTime);
			return;
		}

		// Followers are tape readers updated by the convoy root, they do not own route interest.
		if (IsFollower)
		{
			RailSystem?.ReleaseLoadedRouteInterest(EntityId);
			return;
		}

		RailSystem ??= Api.ModLoader.GetModSystem<RailGraphServerSystem>();
		RailSystem?.UpdateLoadedRouteInterest(EntityId, RailTape);

		if (!ServerCanMoveLoadedConvoy())
		{
			if (Math.Abs(Speed) > 1e-9)
			{
				RailwayVehicleShared.StopRailMotion(this, ref Speed);
				PersistTrackState();
			}
			return;
		}

		if (!TryEnsureBound(forceRebind: false)) { RailwayVehicleShared.StopRailMotion(this, ref Speed); return; }

		var convoyStatistics = GetConvoyStaticStats();
		var lead = ResolveConvoyLead();

		double rawThrottle = 0;
		double engineMaxSpeed = DriveParameters.HardMaxSpeed;
		double engineAcceleration = 0;

		TryComputeConvoyDrive(lead, convoyStatistics.Count, convoyStatistics.Weight, out rawThrottle, out engineAcceleration, out engineMaxSpeed);

		RailExactTurnPlan? exactTurns = null;
		RailAutomationDriveCommand automationCommand = RailAutomationDriveCommand.Hold;
		bool automationActive = false;
		AutomationSystem ??= Api.ModLoader.GetModSystem<RailAutomationPathingSystem>();
		if (AutomationSystem != null)
		{
			var automationCursor = Cursor;
			bool hasAutomationDriver =
				ConvoySystem != null &&
				ConvoySystem.TryGetConvoyHasConductorLocust(EntityId, out bool convoyHasLocust) ? convoyHasLocust : HasConductorLocustInstalled();

			if (hasAutomationDriver && ServerTryGetAutomationRouteStart(allowRouteRepair: false, out var sampledAuthorityCursor))
			{
				automationCursor = sampledAuthorityCursor;
			}

			automationActive = AutomationSystem.TryApplyAutomation(this, lead, ref automationCursor, out automationCommand, out exactTurns);
		}

		bool brake = automationActive ? automationCommand.Mode != RailAutomationDriveMode.DriveForward : lead.SteamEngineBehaviour != null && RailwayVehicleShared.IsDriveLeverBrakeEngaged(lead.WatchedAttributes);
		double driveThrottle = automationActive ? (automationCommand.Mode == RailAutomationDriveMode.DriveForward ? GameMath.Clamp(automationCommand.Traction, 0, 1) : 0) : rawThrottle;

		// Positive tape S is the convoy's stable ordered-forward direction. Reverse is signed speed, not head swapping.
		if (!automationActive && Math.Abs(Speed) < 0.01 && Math.Abs(rawThrottle) > 0.1)
		{
			ChooseForwardDirectionFromYaw(ServerPos.Yaw);
			PersistTrackState();
		}

		double effectiveMaxSpeed = engineMaxSpeed;
		if (Cursor.SegmentMaterialSpeedCapBPS > 0) effectiveMaxSpeed = Math.Min(effectiveMaxSpeed, Cursor.SegmentMaterialSpeedCapBPS);
		if (!automationActive && rawThrottle < -0.1) effectiveMaxSpeed *= lead.ReverseMaxSpeedMultiplier;

		double previousSpeed = Speed;
		Speed = KinematicDrive.Step(Speed, driveThrottle, deltaTime, convoyStatistics.Weight, engineAcceleration, effectiveMaxSpeed, Cursor.SegmentMaxSpeedFactor, in DriveParameters, brake);
		if (Math.Abs(Speed - previousSpeed) > 1e-9) MarkTrackStateDirty();

		double travelDistance = Speed * deltaTime;
		bool advanced = Math.Abs(travelDistance) > 1e-6;
		if (!ServerEnsureRailTape(forceRebuild: false) && !ServerRecoverRailTapeAfterFailure())
		{
			RailwayVehicleShared.StopRailMotion(this, ref Speed);
			return;
		}

		if (!LivingCollisionSweepInitialized || LastLivingCheckTravelABS > TravelledABS)
		{
			LastLivingCheckTravelABS = TravelledABS;
			LastLivingCheckRailRootDistance = RailRootDistance;
			LivingCollisionSweepInitialized = true;
		}

		if (advanced && !ServerAdvanceLoadedConvoyRailTape(travelDistance, lead, exactTurns, automatedMovement: automationActive))
		{
			RailwayVehicleShared.StopRailMotion(this, ref Speed);
			RailSystem?.ReleaseTransientSignalReservations(EntityId);
			ServerRecoverRailTapeAfterFailure();
		}
		else ServerApplyRailTapeToLoadedConvoyMembers();

		if (Derailed)
		{
			RailSystem?.ReleaseTransientSignalReservations(EntityId);
			return;
		}
		if (!Derailed) ServerPublishOccupancy(RailSystem.Graph);

		ServerLivingCollisionCheck();

		ServerSyncTrackPenaltyIfChanged();
		if (advanced) MarkTrackStateDirty();
		if (TrackStateDirty) PersistTrackState(force: false);
	}
	#endregion

	#region Seats
	public IMountableSeat CreateSeat(IMountable mountable, string seatID, SeatConfig configuration = null)
	{
		return new RailVehicleSeat(mountable, seatID, configuration, "yangtransport.minecartseat", "sitflooridle");
	}
	#endregion


	#region Convoy API (server)

	private void SetPreviousCartID(long previousCartID)
	{
		PreviousCartIDReadOnly = previousCartID;
		Attributes.SetLong(PreviousCartIDAttributeKey, previousCartID);

		// Client rendering uses WatchedAttributes, so mirror it when we're the authority.
		if (Api?.Side == EnumAppSide.Server)
		{
			WatchedAttributes.SetLong(PreviousCartIDAttributeKey, previousCartID);
			WatchedAttributes.MarkPathDirty(PreviousCartIDAttributeKey);
		}
	}


	internal void ServerSetConvoyState(long headID, int index, long previousCartID, double distanceBehindHead)
	{
		if (Api?.Side == EnumAppSide.Server) RemovePublishedCollisionBody();
		ConvoyHeadID = headID;
		ConvoyIndex = index;
		ConvoyDistanceBehindHeadBlocks = Math.Max(0, distanceBehindHead);

		Attributes.SetLong(ConvoyHeadIDAttributeKey, headID);
		Attributes.SetInt(ConvoyIndexAttributeKey, index);
		Attributes.SetDouble(ConvoyDistanceBehindHeadAttributeKey, ConvoyDistanceBehindHeadBlocks);

		SetPreviousCartID(previousCartID);
	}

	internal void ServerClearConvoyState()
	{
		if (Api?.Side == EnumAppSide.Server) RemovePublishedCollisionBody();
		ConvoyHeadID = 0;
		ConvoyIndex = 0;
		ConvoyDistanceBehindHeadBlocks = 0;
		Attributes.SetLong(ConvoyHeadIDAttributeKey, 0);
		Attributes.SetInt(ConvoyIndexAttributeKey, 0);
		Attributes.SetDouble(ConvoyDistanceBehindHeadAttributeKey, 0);
		SetPreviousCartID(0);
		ServerSetConvoyStatisticsWatched(1, SelfWeight, 0, OccupancyHalfLengthBlocks); // Reset cached HUD stats for free carts
		
		// Followers do not keep rail binding up to date. If we just became free,
		// the stored Cursor.SegHash may point to where we were last simulated (often the place we were first tethered), causing a 'snap back' teleport.
		ServerInvalidateRailBinding(persist: false);
	}

	/// Clears any cached rail binding so the cart will re-bind based on its current world position.
	/// Needed because followers don't update Cursor.SegHash/Cursor.SegIndex/Cursor.SegT01 while snaking.
	internal void ServerInvalidateRailBinding(bool persist)
	{
		if (Api?.Side != EnumAppSide.Server) return;

		Cursor.SegmentHash = 0;
		Cursor.PolyXYZ16 = null;
		Cursor.PointCount = 0;
		Cursor.BoundGraphVersion = 0;

		// Also reset cached orientation so we don't reuse stale span tangents after rebind.
		Cursor.OrientationCacheSegmentHash = 0;
		Cursor.OrientationCacheSegmentIndex = -1;
		Cursor.OrientationCacheDirection = 0;

		CollisionTrail.Clear();
		RailTape?.Clear();

		if (persist) PersistTrackState();
	}

	internal void ServerSetConvoyStatisticsWatched(int count, int weight, double tailDistance, double occupancyRearDistance)
	{
		if (Api.Side != EnumAppSide.Server) return;

		OccupancyRearDistanceCached = Math.Max(OccupancyHalfLengthBlocks, occupancyRearDistance);

		WatchedAttributes.SetInt(ConvoyWeightAttributeKey, weight);
		WatchedAttributes.SetInt(ConvoyCountAttributeKey, count);
		WatchedAttributes.MarkPathDirty(ConvoyWeightAttributeKey);
		WatchedAttributes.MarkPathDirty(ConvoyCountAttributeKey);
	}


	/// Seeds the convoy rail tape from the currently loaded ordered vehicles. The tape, not pose history, is canonical for follower placement.
	internal bool ServerSeedConvoyRailStateFromOrderedConvoy(IReadOnlyList<IRailwayConvoyVehicle> ordered, IReadOnlyDictionary<long, double>? physicalDistanceFromHead = null)
	{
		return ServerSeedRailTapeFromOrderedConvoy(ordered, force: true, physicalDistanceFromHead);
	}

	internal bool ServerTryGetAutomationRouteStart(bool allowRouteRepair, out RailwayVehicleShared.RailCursor cursor)
	{
		cursor = default;
		if (Api?.Side != EnumAppSide.Server || IsFollower || Derailed) return false;

		bool tapeReady = ServerEnsureRailTape(forceRebuild: false);
		if (!tapeReady && allowRouteRepair) { tapeReady = ServerRecoverRailTapeAfterFailure(); }

		return tapeReady
			&& RailTape != null && RailSystem != null
			&& RailTape.TrySampleAuthoritativeCursor
			(
				RailSystem.Graph,
				TrackGauge,
				RailRootDistance + OccupancyHalfLengthBlocks,
				out cursor
			);
	}

	private bool ServerRecoverRailTapeAfterFailure()
	{
		if (Api?.Side != EnumAppSide.Server || IsFollower || Derailed) return false;

		CollisionTrail.Clear();

		bool seeded = ServerEnsureRailTape(forceRebuild: true);
		if (seeded) return true;

		// Last-resort recovery for rail edits under/near a train, do not leave a stale cursor/tape combination that fails forever.
		// Clear the binding so the next tick retries a clean nearest-rail bind, and persist that non-poisoned state.
		Cursor.ClearBinding();
		PersistTrackState();
		return false;
	}

	internal void ServerPromoteRouteAcrossGraphChange(RailGraphLive graph, RailGraphChangeSet change)
	{
		if (Api?.Side != EnumAppSide.Server || Derailed || IsFollower || graph == null || change == null) return;
		if (RailTape == null || RailTape.Count == 0) return;
		RailTape.TryPromoteAcrossUnrelatedChange(change, graph.BuildVersion);
	}

	private bool ServerEnsureRailTape(bool forceRebuild)
	{
		if (Api?.Side != EnumAppSide.Server || IsFollower) return false;

		RailSystem ??= Api.ModLoader.GetModSystem<RailGraphServerSystem>();
		ConvoySystem ??= Api.ModLoader.GetModSystem<RailConvoySystem>();
		if (RailSystem == null) return false;

		if (!forceRebuild && RailTape != null && RailTape.ValidateAuthoritative(RailSystem.Graph, TrackGauge))
		{
			var convoyStatistics = GetConvoyStaticStats();
			double minDistance = RailRootDistance - convoyStatistics.TailDistance - OccupancyHalfLengthBlocks;
			double maxDistance = RailRootDistance + OccupancyHalfLengthBlocks;
			if (RailTape.Covers(minDistance, maxDistance)) return true;
		}

		List<IRailwayConvoyVehicle> ordered = ConvoyMemberScratch;
		ordered.Clear();

		if (IsConvoyHead && ConvoySystem != null) { if (!ConvoySystem.TryGetLoadedOrderedMembers(EntityId, ordered)) ordered.Add(this); }
		else ordered.Add(this);

		return ServerSeedRailTapeFromOrderedConvoy(ordered, force: true);
	}

	private bool ServerSeedRailTapeFromOrderedConvoy(IReadOnlyList<IRailwayConvoyVehicle> ordered, bool force, IReadOnlyDictionary<long, double>? physicalDistanceFromHead = null)
	{
		if (Api?.Side != EnumAppSide.Server || ordered == null || ordered.Count == 0) return false;
		if (ordered[0].Entity.EntityId != EntityId) return false;

		RailSystem ??= Api.ModLoader.GetModSystem<RailGraphServerSystem>();
		ConvoySystem ??= Api.ModLoader.GetModSystem<RailConvoySystem>();
		if (RailSystem == null) return false;

		if (physicalDistanceFromHead == null && ordered.Count > 1) { physicalDistanceFromHead = ConvoySystem?.TryMeasurePhysicalDistances(ordered); }

		ConvoyRoute? savedRailTape = RailTape;
		double savedRootDistance = RailRootDistance;
		double savedTravelledABS = TravelledABS;
		bool success = false;

		RailTape = new ConvoyRoute();

		try
		{
			double tailDistance = Math.Max(0, ordered[ordered.Count - 1].ConvoyDistanceBehindHead);
			RailRootDistance = Math.Max(RailTapeSeedBaseDistance, tailDistance + OccupancyHalfLengthBlocks * 2.0 + RailTapeMarginBlocks);
			TravelledABS = Math.Max(TravelledABS, tailDistance + 1.0);

			for (int orderedIndex = 0; orderedIndex < ordered.Count; orderedIndex++)
			{
				if (ordered[orderedIndex].Entity is not EntityMinecart cart) return false;
				if (!cart.TryEnsureBound(forceRebind: force || cart.Cursor.SegmentHash == 0)) return false;
			}

			if (ordered[ordered.Count - 1].Entity is not EntityMinecart tail) return false;
			double tailSourceDistance = RailRootDistance - ordered[ordered.Count - 1].ConvoyDistanceBehindHead;
			ServerAppendBestBoundarySeedGap(tail, tailSourceDistance, -OccupancyHalfLengthBlocks);

			for (int orderedIndex = ordered.Count - 1; orderedIndex > 0; orderedIndex--)
			{
				if (ordered[orderedIndex].Entity is not EntityMinecart behind) return false;
				if (ordered[orderedIndex - 1].Entity is not EntityMinecart ahead) return false;

				double behindDistance = RailRootDistance - ordered[orderedIndex].ConvoyDistanceBehindHead;
				double aheadDistance = RailRootDistance - ordered[orderedIndex - 1].ConvoyDistanceBehindHead;
				double canonicalGap = aheadDistance - behindDistance;
				if (canonicalGap <= 1e-6) continue;

				double physicalGap = canonicalGap;
				if 
				(
					physicalDistanceFromHead != null &&
					physicalDistanceFromHead.TryGetValue(ordered[orderedIndex].Entity.EntityId, out double behindPhysical) &&
					physicalDistanceFromHead.TryGetValue(ordered[orderedIndex - 1].Entity.EntityId, out double aheadPhysical)
				) { physicalGap = Math.Abs(behindPhysical - aheadPhysical); }

				if (!ServerAppendBestSeedGap(behind, ahead, behindDistance, canonicalGap, physicalGap)) return false;
			}

			ServerAppendBestBoundarySeedGap(this, RailRootDistance, OccupancyHalfLengthBlocks);
			if (RailTape == null || RailTape.Count == 0) return false;

			ServerTrimRailTapeToCurrentConvoy(ordered);
			success = ServerApplyRailTapeToLoadedConvoyMembers();

			if (success)
			{
				Attributes.SetDouble("railRootS", RailRootDistance);
				Attributes.SetDouble("travelledAbs", TravelledABS);
			}

			return success;
		}
		finally
		{
			if (!success)
			{
				RailTape = savedRailTape;
				RailRootDistance = savedRootDistance;
				TravelledABS = savedTravelledABS;
			}
		}
	}

	private bool ServerAppendBestSeedGap(EntityMinecart sourceMinecart, EntityMinecart destinationMinecart, double sourceDistance, double canonicalGap, double physicalGap)
	{
		if (RailTape == null || RailSystem == null) return false;

		double dx = destinationMinecart.ServerPos.X - sourceMinecart.ServerPos.X;
		double dz = destinationMinecart.ServerPos.Z - sourceMinecart.ServerPos.Z;

		var startCursor = sourceMinecart.Cursor;
		if (dx * dx + dz * dz > 1e-8) RailwayVehicleShared.ChooseForwardDirectionFromYaw((float)Math.Atan2(dx, dz), ref startCursor);

		ReadOnlySpan<int> attempts = stackalloc int[] { 0, -1, 1 };
		ConvoyRoute? bestTape = null;
		double bestDistanceSQ = double.MaxValue;

		for (int attemptIndex = 0; attemptIndex < attempts.Length; attemptIndex++)
		{
			var saved = RailTape;
			var candidate = saved?.Clone() ?? new ConvoyRoute();
			RailTape = candidate;

			if (ServerAppendTapeFromCursor(startCursor, physicalGap, sourceDistance, attempts[attemptIndex], null, requireFullDistance: true, pathDistance: canonicalGap)
				&& candidate.TrySampleAuthoritativeCursor(RailSystem.Graph, TrackGauge, sourceDistance + canonicalGap, out var probe))
			{
				var poseProbe = probe;
				if (RailwayVehicleShared.TryReadWorldPoseFromTrack(ref poseProbe, out double x, out double y, out double z, out _, out _))
				{
					double ex = destinationMinecart.ServerPos.X - x;
					double ey = destinationMinecart.ServerPos.Y - y;
					double ez = destinationMinecart.ServerPos.Z - z;
					double distanceSQ = ex * ex + ey * ey + ez * ez;
					if (distanceSQ < bestDistanceSQ)
					{
						bestDistanceSQ = distanceSQ;
						bestTape = candidate;
					}
				}
			}

			RailTape = saved;
		}

		if (bestTape == null) return false;

		double maxError = 0.35;
		if (bestDistanceSQ > maxError * maxError) return false;

		RailTape = bestTape;
		return true;
	}

	private bool ServerAppendBestBoundarySeedGap(EntityMinecart cart, double sourceDistance, double signedDistance)
	{
		if (RailTape == null || RailSystem == null) return false;
		double distanceABS = Math.Abs(signedDistance);
		if (distanceABS <= 1e-8) return true;

		double expectedX = cart.ServerPos.X + Math.Sin(cart.ServerPos.Yaw) * signedDistance;
		double expectedY = cart.ServerPos.Y;
		double expectedZ = cart.ServerPos.Z + Math.Cos(cart.ServerPos.Yaw) * signedDistance;

		var startCursor = cart.Cursor;
		RailwayVehicleShared.ChooseForwardDirectionFromYaw(cart.ServerPos.Yaw, ref startCursor);

		ReadOnlySpan<int> attempts = stackalloc int[] { 0, -1, 1 };
		ConvoyRoute? bestTape = null;
		double bestScore = double.MaxValue;

		for (int attemptIndex = 0; attemptIndex < attempts.Length; attemptIndex++)
		{
			var saved = RailTape;
			var candidate = saved?.Clone() ?? new ConvoyRoute();
			RailTape = candidate;

			if (ServerAppendTapeFromCursor(startCursor, signedDistance, sourceDistance, attempts[attemptIndex], null, requireFullDistance: true)
				&& candidate.TrySampleAuthoritativeCursor(RailSystem.Graph, TrackGauge, sourceDistance + signedDistance, out var probe))
			{
				var poseProbe = probe;
				if (RailwayVehicleShared.TryReadWorldPoseFromTrack(ref poseProbe, out double x, out double y, out double z, out float yaw, out _))
				{
					double ex = expectedX - x;
					double ey = expectedY - y;
					double ez = expectedZ - z;
					double posScore = ex * ex + ey * ey * 0.25 + ez * ez;

					// Tape samples always use the vehicle/body-positive orientation. signedDistance is only the temporary direction used while seeding a boundary.
					float expectedYaw = cart.ServerPos.Yaw;

					double yawError = Math.Abs(GameMath.AngleRadDistance(expectedYaw, yaw));
					double score = posScore + yawError * yawError * 0.25;
					if (score < bestScore)
					{
						bestScore = score;
						bestTape = candidate;
					}
				}
			}

			RailTape = saved;
		}

		if (bestTape == null) return false;

		double maxError = Math.Min(2.5, Math.Max(0.5, distanceABS * 0.5));
		if (bestScore > maxError * maxError + 1.0) return false;

		RailTape = bestTape;
		return true;
	}

	private bool ServerAppendTapeFromCursor
	(
		RailwayVehicleShared.RailCursor startCursor,
		double signedDistance,
		double startingDistance,
		int wantTurn,
		RailExactTurnPlan? exactTurns,
		bool requireFullDistance = false,
		double? pathDistance = null
	)
	{
		if (RailTape == null || RailSystem == null) return false;
		if (Math.Abs(signedDistance) <= 1e-8) return true;

		var cursor = startCursor;
		cursor.Gauge = TrackGauge;
		if (!RailwayVehicleShared.TryRefreshPolyline(RailSystem.Graph, ref cursor)) return false;

		double speed = 0;
		double travel = 0;
		double pathDelta = pathDistance ?? signedDistance;
		int pathSign = pathDelta >= 0 ? 1 : -1;
		double pathUnitsPerTravel = Math.Abs(pathDelta / signedDistance);

		RailTapeRecorder.Begin(RailTape, TrackGauge, pathSign, startingDistance, 0, pathUnitsPerTravel, recordSelectedEdgeRemainder: false);
		bool appendSucceeded = RailwayVehicleShared.AdvanceAlongTrack
		(
			RailSystem.Graph,
			signedDistance,
			wantTurn,
			exactTurns,
			RailSystem,
			useSignalAuthority: false,
			ref cursor,
			ref speed,
			ref travel,
			trackTravelledABS: true,
			out _,
			collisionTrail: null,
			occupancyOwnerId: EntityId,
			tapeRecorder: RailTapeRecorder
		);
		RailTapeRecorder.Clear();

		if (requireFullDistance) return appendSucceeded && Math.Abs(travel - Math.Abs(signedDistance)) <= Math.Max(0.01, Math.Abs(signedDistance) * 0.02);
		return appendSucceeded || travel > 1e-6;
	}

	private bool ServerCanMoveLoadedConvoy()
	{
		RailSystem ??= Api.ModLoader.GetModSystem<RailGraphServerSystem>();
		if (RailSystem?.IsRuntimeReady != true) return false;
		ConvoySystem ??= Api.ModLoader.GetModSystem<RailConvoySystem>();
		return !IsConvoyHead || ConvoySystem == null || ConvoySystem.IsConvoyFullyLoaded(EntityId);
	}

	private bool ServerAdvanceLoadedConvoyRailTape(double requestedSignedDistance, EntityMinecart lead, RailExactTurnPlan? exactTurns, bool automatedMovement = false)
	{
		if (!ServerCanMoveLoadedConvoy() || RailSystem == null || RailTape == null) return false;
		if (!ServerEnsureRailTape(forceRebuild: false)) return false;

		List<IRailwayConvoyVehicle> ordered = ConvoyMemberScratch;
		ordered.Clear();
		if (IsConvoyHead && ConvoySystem != null) { if (!ConvoySystem.TryGetLoadedOrderedMembers(EntityId, ordered)) ordered.Add(this); }
		else ordered.Add(this);

		int movementSign = requestedSignedDistance >= 0 ? 1 : -1;
		double requestedABS = Math.Abs(requestedSignedDistance);
		if (requestedABS <= 1e-8) return ServerApplyRailTapeToLoadedConvoyMembers();

		double tailDistance = ordered.Count > 0 ? Math.Max(0, ordered[ordered.Count - 1].ConvoyDistanceBehindHead) : 0;
		double occupiedMinDistance = RailRootDistance - tailDistance - OccupancyHalfLengthBlocks;
		double occupiedMaxDistance = RailRootDistance + OccupancyHalfLengthBlocks;
		double authorityDistance = movementSign > 0 ? occupiedMaxDistance : occupiedMinDistance;

		if (!RailTape.TrySampleAuthoritativeCursorForTravel(RailSystem.Graph, TrackGauge, authorityDistance, movementSign, out _))
		{
			if (!ServerSeedRailTapeFromOrderedConvoy(ordered, force: true)) return false;
		}

		RailTape.TrimForMovement(occupiedMinDistance, occupiedMaxDistance, movementSign, RailTapeMarginBlocks);
		if (!RailTape.TrySampleAuthoritativeCursorForTravel(RailSystem.Graph, TrackGauge, authorityDistance, movementSign, out var extensionCursor)) return false;

		int wantTurn = 0;
		if (lead.WatchedAttributes.HasAttribute(RailwayVehicleShared.AttributeTurnLeverPhase))
		{
			wantTurn = RailwayVehicleShared.GetWantTurnTravel(lead.WatchedAttributes, 1);
			if (movementSign < 0) wantTurn = -wantTurn;
		}

		double temporarySpeed = Speed;
		double extensionTravel = 0;
		RailwayVehicleShared.DeadEndInfo deadEnd = default;
		double impactSpeedABS = Math.Abs(Speed);

		RailTapeRecorder.Begin(RailTape, TrackGauge, movementSign, authorityDistance, 0);
		bool trackAdvanceSucceeded = RailwayVehicleShared.AdvanceAlongTrack
		(
			RailSystem.Graph,
			movementSign * requestedABS,
			wantTurn,
			exactTurns,
			RailSystem,
			Api.Side == EnumAppSide.Server && !IsFollower,
			automatedMovement,
			ref extensionCursor,
			ref temporarySpeed,
			ref extensionTravel,
			trackTravelledABS: true,
			out deadEnd,
			collisionTrail: null,
			occupancyOwnerID: EntityId,
			tapeRecorder: RailTapeRecorder,
			allowStaleBlockedSelfHeal: true
		);
		RailTapeRecorder.Clear();

		if (deadEnd.ClearanceBlocked)
		{
			temporarySpeed = 0;
			ServerStopAfterClearanceBlock(lead, impactSpeedABS);
		}

		Speed = temporarySpeed;
		if (extensionTravel > 1e-8)
		{
			TravelledABS += extensionTravel;
			RailRootDistance += movementSign * extensionTravel;
		}

		if (!trackAdvanceSucceeded && deadEnd.Hit)
		{
			PersistTrackState();
			if (!ServerDeadEndWallImpact(impactSpeedABS, in deadEnd))
			{
				deadEnd.GetEndPoint(out double ex, out double ey, out double ez);
				ServerDerailConvoy(impactSpeedABS, deadEnd.Dx, deadEnd.Dz, deadEnd.MoveDirection, ex, ey, ez);
			}
		}

		return ServerApplyRailTapeToLoadedConvoyMembers();
	}

	private void ServerStopAfterClearanceBlock(EntityMinecart lead, double impactSpeedABS)
	{
		if (Api?.Side != EnumAppSide.Server) return;
		RailwayVehicleShared.StopRailMotion(this, ref Speed);
		RailwayVehicleShared.ServerShakeImpact(Api, lead.ServerPos.XYZ, impactSpeedABS);
		ServerApplyPassengerImpactToConvoy
		(
			impactSpeedABS,
			RailwayVehicleShared.CollisionDamageMultiplier,
			RailwayVehicleShared.AlongTrackCollisionDamageSpeedThreshold,
			RailwayVehicleShared.AlongTrackCollisionEjectSpeedThreshold
		);
		lead.SteamEngineBehaviour?.ForceDriveLeverStop();
		lead.SteamEngineBehaviour?.ForceExtinguishBoiler();
		PersistTrackState();
	}

	private void ServerTrimRailTapeToCurrentConvoy(IReadOnlyList<IRailwayConvoyVehicle> ordered)
	{
		if (RailTape == null || ordered == null || ordered.Count == 0) return;
		double tailDistance = Math.Max(0, ordered[ordered.Count - 1].ConvoyDistanceBehindHead);
		RailTape.TrimWholeRunsOutside(RailRootDistance - tailDistance - OccupancyHalfLengthBlocks - RailTapeMarginBlocks, RailRootDistance + OccupancyHalfLengthBlocks + RailTapeMarginBlocks);
	}

	internal bool ServerApplyRailTapeToLoadedConvoyMembers()
	{
		if (Api?.Side != EnumAppSide.Server || Derailed || IsFollower) return false;
		if (RailSystem == null || RailTape == null) return false;

		List<IRailwayConvoyVehicle> ordered = ConvoyMemberScratch;
		ordered.Clear();
		if (IsConvoyHead && ConvoySystem != null) { if (!ConvoySystem.TryGetLoadedOrderedMembers(EntityId, ordered)) ordered.Add(this); }
		else ordered.Add(this);

		int hint = -1;
		for (int orderedIndex = ordered.Count - 1; orderedIndex >= 0; orderedIndex--)
		{
			if (ordered[orderedIndex].Entity is not EntityMinecart cart) return false;
			double sourceDistance = RailRootDistance - ordered[orderedIndex].ConvoyDistanceBehindHead;
			if (!RailTape.TrySampleAuthoritativeCursor(RailSystem.Graph, TrackGauge, sourceDistance, ref hint, out var sampledCursor)) return false;

			double newSpeed = cart.EntityId == EntityId ? Speed : 0;
			double newTravelledABS = Math.Max(0, TravelledABS - Math.Abs(ordered[orderedIndex].ConvoyDistanceBehindHead));
			bool trackStateChanged =
				cart.Cursor.SegmentHash != sampledCursor.SegmentHash ||
				cart.Cursor.SegmentIndex != sampledCursor.SegmentIndex ||
				Math.Abs(cart.Cursor.NormalizedSegmentProgress - sampledCursor.NormalizedSegmentProgress) > 1e-9 ||
				cart.Cursor.Direction != sampledCursor.Direction ||
				Math.Abs(cart.Speed - newSpeed) > 1e-9 ||
				Math.Abs(cart.TravelledABS - newTravelledABS) > 1e-9 ||
				Math.Abs(cart.RailRootDistance - RailRootDistance) > 1e-9;

			cart.Cursor = sampledCursor;
			cart.Cursor.Gauge = TrackGauge;
			cart.Speed = newSpeed;
			cart.TravelledABS = newTravelledABS;
			cart.RailRootDistance = RailRootDistance;
			cart.WriteWorldPosFromTrack(Math.Abs(Speed));
			if (trackStateChanged) cart.MarkTrackStateDirty();
			if (cart.TrackStateDirty) cart.PersistTrackState(force: false);
		}

		return true;
	}

	internal bool TryCollectRailTapeFootprintEdges(RailGraphLive graph, HashSet<ulong> destinationEdgeHashes)
	{
		if (graph == null || destinationEdgeHashes == null || !ServerEnsureRailTape(forceRebuild: false)) return false;
		var convoyStatistics = GetConvoyStaticStats();
		return RailTape != null && RailTape.CollectOccupiedEdgeHashes(graph, TrackGauge, RailRootDistance - convoyStatistics.TailDistance - OccupancyHalfLengthBlocks, RailRootDistance + OccupancyHalfLengthBlocks, destinationEdgeHashes);
	}


	internal bool TryEmitRailTapeCollisionFootprint(RailTrainCollisionSystem collector, RailGraphLive graph, int bodyIndex, IReadOnlyList<IRailwayConvoyVehicle> ordered)
	{
		if (collector == null || graph == null || ordered == null || ordered.Count == 0) return false;
		if (!ServerEnsureRailTape(forceRebuild: false) || RailTape == null) return false;

		bool any = false;
		int routeRunHint = -1;
		for (int orderedIndex = ordered.Count - 1; orderedIndex >= 0; orderedIndex--)
		{
			if (ordered[orderedIndex].Entity is not EntityMinecart) continue;

			double sourceDistance = RailRootDistance - ordered[orderedIndex].ConvoyDistanceBehindHead;
			double minDistance = sourceDistance - OccupancyHalfLengthBlocks;
			double maxDistance = sourceDistance + OccupancyHalfLengthBlocks;
			any |= RailTape.EmitRepulsionSpans(collector, graph, TrackGauge, bodyIndex, orderedIndex, minDistance, maxDistance, ref routeRunHint);
		}

		return any;
	}

	internal bool OfflineSimTryExportRailTape(out ConvoyRoute railRoute, out double rootDistance) // OfflineSimulationTryExportRailRoute
	{
		railRoute = null!;
		rootDistance = RailRootDistance;
		if (Api?.Side != EnumAppSide.Server || IsFollower) return false;
		if (!ServerEnsureRailTape(forceRebuild: false)) return false;
		if (RailTape == null || RailTape.Count == 0) return false;

		railRoute = RailTape.Clone();
		rootDistance = RailRootDistance;
		return true;
	}

	Entity IRailwayConvoyVehicle.Entity												=> this;
	byte IRailwayConvoyVehicle.TrackGauge											=> TrackGauge;
	int IRailwayConvoyVehicle.BindRadiusBlocks										=> 128;
	long IRailwayConvoyVehicle.ConvoyHeadEntityID									=> ConvoyHeadID;
	int IRailwayConvoyVehicle.ConvoyOrderIndex										=> ConvoyIndex;
	int IRailwayConvoyVehicle.ExpectedConvoyMemberCount								=> Math.Max(1, WatchedAttributes.GetInt(ConvoyCountAttributeKey, 1));
	long IRailwayConvoyVehicle.PreviousVehicleID									=> PreviousCartIDReadOnly;
	double IRailwayConvoyVehicle.ConvoyDistanceBehindHead							=> ConvoyDistanceBehindHeadBlocks;
	int IRailwayConvoyVehicle.SelfWeightCached										=> SelfWeight;
	double IRailwayConvoyVehicle.OccupancyRearExtentBlocks							=> OccupancyHalfLengthBlocks;
	double IRailwayConvoyVehicle.OccupancyFrontExtentBlocks							=> OccupancyHalfLengthBlocks;
	bool IRailwayConvoyVehicle.HasTractionEngine									=> SteamEngineBehaviour != null;
	bool IRailwayConvoyVehicle.HasConductorLocustInstalled							=> HasConductorLocustInstalled();
	bool IRailwayConvoyVehicle.Derailed												=> Derailed;
	double IRailwayConvoyVehicle.ConvoySpacingToNext								=> CouplingDistance;
	EntityBehaviorSteamPowered? IRailwayConvoyVehicle.SteamEngineBehaviour		=> SteamEngineBehaviour;

	void IRailwayConvoyVehicle.ServerSetConvoyState(long headID, int index, long previousVehicleID, double distanceBehindHead) => ServerSetConvoyState(headID, index, previousVehicleID, distanceBehindHead);
	void IRailwayConvoyVehicle.ServerClearConvoyState() => ServerClearConvoyState();
	void IRailwayConvoyVehicle.ServerInvalidateRailBinding(bool persist) => ServerInvalidateRailBinding(persist);
	void IRailwayConvoyVehicle.ServerSetConvoyStatsWatched(int count, int weight, double tailDistance, double occupancyRearDistance) => ServerSetConvoyStatisticsWatched(count, weight, tailDistance, occupancyRearDistance);
	bool IRailwayConvoyVehicle.ServerSeedConvoyRailStateFromOrderedConvoy(IReadOnlyList<IRailwayConvoyVehicle> ordered, IReadOnlyDictionary<long, double>? physicalDistanceFromHead) => ServerSeedConvoyRailStateFromOrderedConvoy(ordered, physicalDistanceFromHead);

	internal void GetCouplerWorldPositions(out double frontX, out double frontY, out double frontZ, out double rearX, out double rearY, out double rearZ)
	{
		double half = OccupancyHalfLengthBlocks;
		double yaw = Pos.Yaw;
		double forwardX = Math.Sin(yaw);
		double forwardZ = Math.Cos(yaw);
		double y = Pos.InternalY + 0.2;

		frontX = Pos.X + forwardX * half;
		frontY = y;
		frontZ = Pos.Z + forwardZ * half;

		rearX = Pos.X - forwardX * half;
		rearY = y;
		rearZ = Pos.Z - forwardZ * half;
	}

	void IRailwayConvoyVehicle.GetCouplerWorldPositions(out double frontX, out double frontY, out double frontZ, out double rearX, out double rearY, out double rearZ) => GetCouplerWorldPositions(out frontX, out frontY, out frontZ, out rearX, out rearY, out rearZ);
	#endregion

	#region Attachables
	private bool CanSitOnlyWhenNoAttachments(EntityAgent agent, out string errorMessage)
	{
		errorMessage = null;
		if (!HasAnyAttachments()) return true;

		if (Api is ICoreClientAPI clientAPI) // Manual workaround for the vanilla hardcoded error path
		{
			clientAPI.TriggerIngameError(this, "yangtransport:locomotive-occupied", Lang.Get("ingameerror-yangtransport:locomotive-occupied"));
		}

		return false;
	}

	private bool HasAnyAttachments()
	{
		var attachmentBehavior = AttachableBehaviour; if (attachmentBehavior == null) return false;
		var attachmentInventory = attachmentBehavior.Inventory; if (attachmentInventory == null) return false;

		for (int attachmentIndex = 0; attachmentIndex < attachmentInventory.Count; attachmentIndex++) { if (!attachmentInventory[attachmentIndex].Empty) return true; }

		return false;
	}

	private bool TryHandleConductorLocustInstall(EntityAgent byEntity, ItemSlot itemSlot, EnumInteractMode mode)
	{
		if (mode != EnumInteractMode.Interact) return false;
		if (itemSlot?.Itemstack?.Collectible is not ItemConductorLocust) return false;

		if (Api.Side == EnumAppSide.Server)
		{
			CanAcceptConductorLocust(out string? errorCode);
			ConductorLocustPlacement.ServerTryInstallIntoAttachableSlot(this, byEntity, itemSlot, AttachableBehaviour, ConductorLocustPlacement.MinecartAttachmentPointCode, errorCode);
		}

		return true;
	}

	internal bool CanAcceptConductorLocust(out string? errorCode)
	{
		return ConductorLocustPlacement.CanInstallOnMinecart
		(
			AttachableBehaviour,
			SteamEngineBehaviour != null,
			out errorCode
		);
	}

	internal bool HasConductorLocustInstalled()
	{
		return ConductorLocustPlacement.HasInstalledConductorLocust
		(
			AttachableBehaviour,
			ConductorLocustPlacement.MinecartAttachmentPointCode
		);
	}
	#endregion

	#region Movement & Rail Binding
	private int ComputeSelfWeight() { return WeightSpecification.ComputeTotalWeight(AttachableBehaviour); }

	private void RecalculateSelfWeight(bool notifyConvoy = true)
	{
		int newWeight = ComputeSelfWeight();
		if (newWeight == SelfWeight) return;

		SelfWeight = newWeight;
		if (!notifyConvoy) return;
		if (Api.Side != EnumAppSide.Server) return;

		if (ConvoyHeadID == 0) ServerSetConvoyStatisticsWatched(1, SelfWeight, 0, OccupancyHalfLengthBlocks);
		else ConvoySystem?.NotifyVehicleWeightChanged(this);
	}

	private ConvoyStaticStats GetConvoyStaticStats()
	{
		// Followers should not be queried for physics stats - defer to head.
		if (IsFollower)
		{
			if (Api.World.GetEntityById(ConvoyHeadID) is EntityMinecart head) return head.GetConvoyStaticStats();
			return new ConvoyStaticStats { Count = 1, Weight = SelfWeight, TailID = EntityId, TailDistance = 0 };
		}

		int count = 1;
		int weight = SelfWeight;
		long tailID = EntityId;
		double tailDistance = 0;

		if (Api.Side == EnumAppSide.Server)
		{
			if (IsConvoyHead && ConvoySystem != null && ConvoySystem.TryGetConvoyStats(EntityId, out int resolvedCount, out int resolvedWeight, out long resolvedTailID, out double resolvedTailDistance))
			{
				count = resolvedCount;
				weight = resolvedWeight;
				tailID = resolvedTailID;
				tailDistance = resolvedTailDistance;
			}
		}

		return new ConvoyStaticStats
		{
			Count = count,
			Weight = weight,
			TailID = tailID,
			TailDistance = tailDistance
		};
	}
	private double ComputeNormalizedConvoySlope(in ConvoyStaticStats convoyStatistics)
	{
		if (convoyStatistics.TailDistance <= 0.001) return 0;

		double tailY;
		if (RailTape != null && RailSystem != null && RailTape.TrySampleAuthoritativeCursor(RailSystem.Graph, TrackGauge, RailRootDistance - convoyStatistics.TailDistance, out var tailCursor))
		{
			var poseCursor = tailCursor;
			if (!RailwayVehicleShared.TryReadWorldPoseFromTrack(ref poseCursor, out _, out tailY, out _, out _, out _)) return 0;
		}
		else if (convoyStatistics.TailID != EntityId && Api.World.GetEntityById(convoyStatistics.TailID) is EntityMinecart tail) { tailY = tail.ServerPos.Y; }
		else return 0;

		// Positive --> leader higher than tail (uphill when moving toward increasing convoy S).
		return (ServerPos.Y - tailY) / convoyStatistics.TailDistance;
	}


	internal bool ServerTryGetFacingDirection(out double forwardX, out double forwardZ)
	{
		double yaw = ServerPos.Yaw;
		forwardX = Math.Sin(yaw);
		forwardZ = Math.Cos(yaw);
		return forwardX * forwardX + forwardZ * forwardZ > 1e-8;
	}

	internal bool ServerTryPlanFacingFlip(RailGraphServerSystem railSystem, out RailwayVehicleShared.RailCursor flippedCursor)
	{
		flippedCursor = default;
		if (Api?.Side != EnumAppSide.Server || Derailed || railSystem == null) return false;

		const int radiusBlocks = 128;

		var bound = Cursor;
		bound.Gauge = TrackGauge;

		if (!RailwayVehicleShared.TryEnsureBound
		(
			railSystem,
			Pos.AsBlockPos,
			ServerPos.XYZ,
			Pos.AsBlockPos.dimension,
			forceRebind: true,
			bindRadiusBlocks: radiusBlocks,
			ref bound,
			BindCandidates,
			bindWithYaw: true,
			bindYaw: ServerPos.Yaw))
		{ return false; }

		return RailVehicleFacingAlignment.TryPlanInPlaceFacingFlip
		(
			railSystem.Graph,
			sourceShift: 0,
			expectedX: ServerPos.X,
			expectedY: ServerPos.Y,
			expectedZ: ServerPos.Z,
			in bound,
			out flippedCursor
		);
	}

	internal void ServerApplyPlannedFacingFlip(
		RailwayVehicleShared.RailCursor flippedCursor,
		bool publishOccupancy)
	{
		if (Api?.Side != EnumAppSide.Server) return;

		Cursor = flippedCursor;
		Cursor.Gauge = TrackGauge;
		RailwayVehicleShared.StopRailMotion(this, ref Speed);

		RailTape?.Clear();
		CollisionTrail.Clear();

		WriteWorldPosFromTrack();
		PersistTrackState();

		if (!publishOccupancy) return;

		RailSystem ??= Api.ModLoader.GetModSystem<RailGraphServerSystem>();
		if (RailSystem != null) { ServerPublishOccupancy(RailSystem.Graph); }
	}

	internal bool DebugTryGetRailCursor(out RailwayVehicleShared.RailCursor cursor)
	{
		cursor = Cursor;
		return Cursor.SegmentHash != 0 && Cursor.PolyXYZ16 != null && Cursor.PointCount >= 2;
	}

	private bool TryEnsureBound(bool forceRebind)
	{
		var railSystem = RailSystem;
		if (railSystem == null) return false;

		// Bind radius matches previous minecart tuning.
		const int radiusBlocks = 128;

		int previousGraphVersion = Cursor.BoundGraphVersion;
		ulong previousSegmentHash = Cursor.SegmentHash;
		int previousSegmentIndex = Cursor.SegmentIndex;
		double previousSegmentInterpolation = Cursor.NormalizedSegmentProgress;
		int previousDirection = Cursor.Direction;

		bool bindingSucceeded = RailwayVehicleShared.TryEnsureBound
		(
			railSystem,
			Pos.AsBlockPos,
			ServerPos.XYZ,
			Pos.AsBlockPos.dimension,
			forceRebind,
			radiusBlocks,
			ref Cursor,
			BindCandidates,
			bindWithYaw: true,
			bindYaw: ServerPos.Yaw
		);
		if (!bindingSucceeded) return false;

		// Persist on (re)bind so offscreen-unload resumes smoothly.
		if (forceRebind || previousSegmentHash != Cursor.SegmentHash || previousSegmentIndex != Cursor.SegmentIndex || Math.Abs(previousSegmentInterpolation - Cursor.NormalizedSegmentProgress) > 1e-6 || previousDirection != Cursor.Direction)
		{
			PersistTrackState();
		}

		// Keep tooltip penalty synchronized (only when it changes).
		ServerSyncTrackPenaltyIfChanged();

		return true;
	}

	private bool TryRefreshPolyline(RailGraphServerSystem railSystem)
	{
		if (!RailwayVehicleShared.TryRefreshPolyline(railSystem.Graph, ref Cursor)) return false;

		RefreshSegmentLimits(railSystem.Graph);
		ServerPublishOccupancy(railSystem.Graph);
		return true;
	}

	private bool TryBindNearestEdge(RailGraphServerSystem railSystem)
	{
		const int radiusBlocks = 128;
		if (!RailwayVehicleShared.TryBindNearestEdgeWithYaw
		(
			railSystem.Graph,
			ServerPos.XYZ,
			Pos.AsBlockPos.dimension,
			radiusBlocks,
			ServerPos.Yaw,
			ref Cursor,
			BindCandidates
		)) return false;

		RefreshSegmentLimits(railSystem.Graph);
		ServerPublishOccupancy(railSystem.Graph);
		PersistTrackState();
		return true;
	}

	private static double DistanceSQFromPointToSegment(Vec3d point, double ax, double ay, double az, double bx, double by, double bz, out double interpolation)
	{
		return RailwayVehicleShared.SquaredDistancePointToSegment(point, ax, ay, az, bx, by, bz, out interpolation);
	}

	private void ChooseForwardDirectionFromYaw(float yaw) { RailwayVehicleShared.ChooseForwardDirectionFromYaw(yaw, ref Cursor); }

	private void AdvanceAlongTrack(double distance, RailExactTurnPlan? exactTurns)
	{
		if (Cursor.PolyXYZ16 == null || Cursor.PointCount < 2) return;
		if (RailSystem == null) return;

		int speedSign = distance >= 0 ? 1 : -1;

		// Turn preference from diegetic lever. Missing attr == straight.
		int wantTurn = 0; // -1 Left, 0 Straight, +1 Right
		var lead = ResolveConvoyLead();
		int turnPhase = lead.WatchedAttributes.GetInt(RailwayVehicleShared.AttributeTurnLeverPhase, RailwayVehicleShared.DefaultTurnLeverPhase);
		wantTurn = turnPhase == 0 ? -1 : (turnPhase == 2 ? 1 : 0);

		// Lever is defined relative to the engine/cart forward (Cursor.Dir), but the left/right test is relative to travel direction (speedSign).
		// Moving backwards swaps left/right. If the lead cart is not the simulated head (reverse mode), travel direction is opposite to lead facing --> swap again.
		int leverToTravel = speedSign;
		if (lead.EntityId != EntityId) leverToTravel = -speedSign;
		wantTurn *= leverToTravel;

		double impactSpeedABS = Math.Abs(Speed);

		bool advanceSucceeded = RailwayVehicleShared.AdvanceAlongTrack
		(
			RailSystem.Graph, distance, wantTurn, exactTurns, RailSystem, Api.Side == EnumAppSide.Server && !IsFollower,
			ref Cursor, ref Speed, ref TravelledABS, true, out var deadEnd, CollisionTrail, EntityId
		);

		// Always keep tooltip penalty synced after movement (shared code can change it when stepping edges).
		ServerSyncTrackPenaltyIfChanged();

		if (advanceSucceeded || !deadEnd.Hit) return;

		// Dead-end: snap to endpoint, stop, and (optionally) check for wall ahead.
		PersistTrackState();
		if (!ServerDeadEndWallImpact(impactSpeedABS, in deadEnd))
		{
			deadEnd.GetEndPoint(out double endX, out double endY, out double endZ);
			ServerDerailConvoy(impactSpeedABS, deadEnd.Dx, deadEnd.Dz, deadEnd.MoveDirection, endX, endY, endZ);
		}
	}

	private bool ServerDeadEndWallImpact(double impactSpeedABS, in RailwayVehicleShared.DeadEndInfo deadEnd)
	{
		// Rails occupy the block space, so we only need to check when the track ends.
		if (Api.Side != EnumAppSide.Server || IsFollower || impactSpeedABS < 0.01) return false;

		if (!deadEnd.TryGetHorizontalForward(out double forwardX, out double forwardZ)) return false;
		deadEnd.GetEndPoint(out double ex, out double ey, out double ez);

		if (!RailwayVehicleShared.IsBlockingWallAhead(Api, ServerPos.Dimension, ex, ey, ez, forwardX, forwardZ, DeadEndBlockPositionScratch)) return false;

		RailwayVehicleShared.ServerShakeImpact(Api, ServerPos.XYZ, impactSpeedABS);
		ServerApplyPassengerImpactToConvoy
		(
			impactSpeedABS,
			RailwayVehicleShared.CollisionDamageMultiplier,
			RailwayVehicleShared.EndTrackCollisionDamageSpeedThreshold,
			double.PositiveInfinity
		);

		// Stop/quiet the engine on a hard wall hit.
		EntityMinecart lead = ResolveConvoyLead();
		lead.SteamEngineBehaviour?.ForceDriveLeverStop();
		lead.SteamEngineBehaviour?.ForceExtinguishBoiler();

		return true;
	}


	private void RefreshSegmentLimits(RailGraphLive graph)
	{
		RailwayVehicleShared.RefreshSegmentLimits(graph, ref Cursor);
		ServerSyncTrackPenaltyIfChanged();
	}

	private void ServerSyncTrackPenaltyIfChanged()
	{
		if (IsFollower) return;
		RailwayVehicleShared.ServerSyncTrackPenaltyIfChanged(this, Cursor.SegmentMaxSpeedFactor);
	}

	private void ServerPublishOccupancy(RailGraphLive graph)
	{
		if (Api.Side != EnumAppSide.Server || IsFollower) return;
		RailSystem!.UpdateLoadedRouteInterest(EntityId, RailTape);
		if (!TryPublishRailOccupancyFootprint(RailSystem, graph)) ServerVacateOccupancyZone();
	}

	private void ServerVacateOccupancyZone()
	{
		if (Api?.Side != EnumAppSide.Server) return;
		if (!IsFollower) RemovePublishedCollisionBody();
		PublishedOccupancyFootprintEdges.Clear();
		PublishedOccupancyGraphVersion = -1;
		RailSystem?.ReleaseOccupancyOwner(EntityId);
	}

	private void WriteWorldPosFromTrack(double motionSpeedBPS = 0)
	{
		RailwayVehicleShared.WriteWorldPositionFromTrack(ServerPos, Pos, ref Cursor, motionSpeedBPS);
	}

	private void GetPoint(int pointIndex, out double x, out double y, out double z)
	{
		RailwayVehicleShared.GetPoint(Cursor.PolyXYZ16!, pointIndex, out x, out y, out z);
	}

	private void MarkTrackStateDirty() { if (Api?.Side == EnumAppSide.Server) TrackStateDirty = true; }

	private void PersistTrackState(bool force = true)
	{
		if (!force)
		{
			if (!TrackStateDirty) return;
			if (Api?.Side == EnumAppSide.Server)
			{
				long now = Api.World.ElapsedMilliseconds;
				if (now < NextMovingPersistenceMS) return;
				NextMovingPersistenceMS = now + MovingPersistenceIntervalMS;
			}
		}

		Attributes.SetLong("segHash", unchecked((long)Cursor.SegmentHash));
		Attributes.SetInt("segIndex", Cursor.SegmentIndex);
		Attributes.SetDouble("segT01", Cursor.NormalizedSegmentProgress);
		Attributes.SetInt("dir", Cursor.Direction);

		Attributes.SetDouble("speed", Speed);
		Attributes.SetDouble("travelledAbs", TravelledABS);
		Attributes.SetDouble("railRootS", RailRootDistance);
		TrackStateDirty = false;
	}


	private EntityMinecart ResolveConvoyLead()
	{
		// Lead = the cart providing drive/turn inputs (engine cart if present). May be a follower when reversing.
		if (Api?.Side != EnumAppSide.Server) return this;
		if (ConvoyHeadID == 0 || ConvoySystem == null) return this;

		long headID = ConvoyHeadID;
		if (!ConvoySystem.TryGetLeadID(headID, out long leadID)) return this;
		return Api.World.GetEntityById(leadID) as EntityMinecart ?? this;
	}

	private bool UsesAutomationAuthority() { return ResolveConvoyLead().HasConductorLocustInstalled(); }

	private bool TryComputeConvoyDrive(EntityMinecart lead, int convoyLength, int convoyWeight, out double normalizedThrottle, out double accelerationBlocksPerSecondSQ, out double maxSpeedBPS)
	{
		// Always assign outs up-front (so all return paths are valid)
		normalizedThrottle = 0;
		accelerationBlocksPerSecondSQ = 0;
		maxSpeedBPS = DriveParameters.HardMaxSpeed;

		// Only the simulated head should drive.
		if (IsFollower) return false;

		// Max 1 engine per convoy (enforced by tethering). The engine cart provides drive even if it's a follower.
		var engineBehavior = lead.SteamEngineBehaviour;
		if (engineBehavior == null) return false;

		if (!engineBehavior.TryGetDrive(out var drive)) return false;

		int direction = drive.GetDriveDir();

		if (!drive.TryComputeLoadedKinematicLimits(convoyWeight, out maxSpeedBPS, out accelerationBlocksPerSecondSQ)) return false;

		normalizedThrottle = GameMath.Clamp(direction, -1, 1);

		return true;
	}
	#endregion

	#region Passenger impact handling
	private void ServerApplyPassengerImpactToConvoy(double impactSpeedABS, double damagePerBPS, double damageThresholdBPS, double ejectThresholdBPS, Entity? causeEntity = null)
	{
		if (Api?.Side != EnumAppSide.Server || IsFollower) return;

		RailwayVehicleShared.ServerApplyPassengerImpact(this, SeatableBehaviour, impactSpeedABS, damagePerBPS, damageThresholdBPS, ejectThresholdBPS, causeEntity);

		if (!IsConvoyHead || ConvoySystem == null) return;
		if (!ConvoySystem.TryGetOrderedMemberIDs(EntityId, out var memberIDs)) return;

		for (int memberIndex = 1; memberIndex < memberIDs.Count; memberIndex++)
		{
			if (Api.World.GetEntityById(memberIDs[memberIndex]) is EntityMinecart cart)
			{
				RailwayVehicleShared.ServerApplyPassengerImpact(cart, cart.SeatableBehaviour, impactSpeedABS, damagePerBPS, damageThresholdBPS, ejectThresholdBPS, causeEntity);
			}
		}
	}

	internal void ServerApplyPassengerCrashImpact(double impactSpeedABS, Entity? causeEntity = null)
	{
		if (Api?.Side != EnumAppSide.Server || Derailed || IsFollower) return;
		ServerApplyPassengerImpactToConvoy(impactSpeedABS, RailwayVehicleShared.CrashDamageMultiplier, 0, 0, causeEntity);
	}
	#endregion


	#region Convoy Collision
	private void ServerLivingCollisionCheck()
	{
		if (Api.Side != EnumAppSide.Server || IsFollower || Derailed || RailTape == null || RailSystem == null) return;

		double speedABS = Math.Abs(Speed);
		if (speedABS < LivingMinSpeedBPS)
		{
			LastLivingCheckTravelABS = TravelledABS;
			LastLivingCheckRailRootDistance = RailRootDistance;
			LivingCollisionSweepInitialized = true;
			return;
		}

		if (!LivingCollisionSweepInitialized || LastLivingCheckTravelABS > TravelledABS)
		{
			LastLivingCheckTravelABS = TravelledABS;
			LastLivingCheckRailRootDistance = RailRootDistance;
			LivingCollisionSweepInitialized = true;
			return;
		}

		double sweepTravel = TravelledABS - LastLivingCheckTravelABS;
		double sweptPath = RailRootDistance - LastLivingCheckRailRootDistance;
		if (sweepTravel < LivingCheckTravelStep) return;

		double previousRootDistance = LastLivingCheckRailRootDistance;
		LastLivingCheckTravelABS = TravelledABS;
		LastLivingCheckRailRootDistance = RailRootDistance;
		if (sweepTravel > LivingMaxSweepBlocks || Math.Abs(sweptPath) > LivingMaxSweepBlocks) return;
		if (!RailTape.ValidateAuthoritative(RailSystem.Graph, TrackGauge)) return;

		LivingCollisionSystem ??= Api.ModLoader.GetModSystem<RailLivingCollisionSystem>();
		if (LivingCollisionSystem == null) return;

		List<IRailwayConvoyVehicle> ordered = ConvoyMemberScratch;
		ordered.Clear();
		if (!IsConvoyHead || ConvoySystem?.TryGetLoadedOrderedMembers(EntityId, ordered) != true || ordered.Count == 0) ordered.Add(this);

		int previousHint = -1;
		int currentHint = -1;
		for (int vehicleIndex = 0; vehicleIndex < ordered.Count; vehicleIndex++)
		{
			IRailwayConvoyVehicle vehicle = ordered[vehicleIndex];
			if (vehicle.Derailed) continue;

			double offset = vehicle.ConvoyDistanceBehindHead;
			if (!RailTape.TrySampleAuthoritativeCursor(RailSystem.Graph, TrackGauge, previousRootDistance - offset, ref previousHint, out var previous)) continue;
			if (!RailTape.TrySampleAuthoritativeCursor(RailSystem.Graph, TrackGauge, RailRootDistance - offset, ref currentHint, out var current)) continue;
			if (!RailwayVehicleShared.TryReadWorldPoseFromTrack(ref previous, out double px, out double py, out double pz, out _, out _)) continue;
			if (!RailwayVehicleShared.TryReadWorldPoseFromTrack(ref current, out double cx, out double cy, out double cz, out _, out _)) continue;

			LivingCollisionSystem.PublishSweep(this, speedABS, CartRadius, -0.5, 2.0, px, py, pz, cx, cy, cz);
		}
		ordered.Clear();
	}

	Entity IRailLivingCollisionSource.CollisionSourceEntity => this;
	long IRailLivingCollisionSource.CollisionGroupID => ConvoyHeadID != 0 ? ConvoyHeadID : EntityId;

	bool IRailLivingCollisionSource.IsMountedOnCollisionSource(EntityAgent agent)
	{
		IMountableSeat seat = agent.MountedOn;
		if (seat == null) return false;

		Entity mountEntity = seat.Entity;
		if (mountEntity is not EntityMinecart) mountEntity = seat.MountSupplier?.OnEntity;
		if (mountEntity is not EntityMinecart cart) return false;

		long currentConvoyHeadID = ConvoyHeadID != 0 ? ConvoyHeadID : EntityId;
		long otherConvoyHeadID = cart.ConvoyHeadID != 0 ? cart.ConvoyHeadID : cart.EntityId;
		return currentConvoyHeadID == otherConvoyHeadID;
	}

	void IRailLivingCollisionSource.ApplyLivingCollisionHit(EntityAgent agent, double hitX, double hitY, double hitZ, double speedABS)
	{
		if (agent.IsActivityRunning("invulnerable")) return;

		var sourcePosition = new Vec3d(hitX, hitY, hitZ);
		float damage = RailwayVehicleShared.ComputeTrainCrashDamageBars(speedABS);
		float knockback = RailwayVehicleShared.ComputeTrainCrashKnockbackStrength(speedABS);
		float verticalKnockbackDivisor = 2.5f + (float)GameMath.Clamp(speedABS / 2.5, 0, 8);

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
				YDirKnockbackDiv = verticalKnockbackDivisor
			}, damage);
			return;
		}

		ApplyVanillaKnockbackOnly(agent, sourcePosition, knockback, verticalKnockbackDivisor);
	}


	private void ApplyVanillaKnockbackOnly(EntityAgent agent, Vec3d sourcePosition, float knockbackStrength, float verticalKnockbackDivisor)
	{
		if (knockbackStrength <= 0.001f) return;

		// Match vanilla ReceiveDamage() knockback direction shaping.
		double dx = agent.SidedPos.X - sourcePosition.X;
		double dy = agent.SidedPos.Y - sourcePosition.Y;
		double dz = agent.SidedPos.Z - sourcePosition.Z;

		double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
		if (length < 1e-9)
		{
			// Degenerate, fall back to a deterministic forward direction.
			dx = Math.Sin(ServerPos.Yaw);
			dy = 0;
			dz = Math.Cos(ServerPos.Yaw);
			length = Math.Sqrt(dx * dx + dz * dz);
			if (length < 1e-9) return;
		}

		double inverseLength = 1.0 / length;
		dx *= inverseLength; dy *= inverseLength; dz *= inverseLength;

		// Vanilla defaults to an upward-ish knock so you don't just "pin" along the ground.
		dy = 0.7;
		dy /= verticalKnockbackDivisor;

		float factor = knockbackStrength * GameMath.Clamp((1 - agent.Properties.KnockbackResistance) / 10f, 0, 1);
		if (factor <= 1e-4f) return;

		agent.WatchedAttributes.SetFloat("onHurtDir", (float)Math.Atan2(dx, dz));
		agent.WatchedAttributes.SetDouble("kbdirX", dx * factor);
		agent.WatchedAttributes.SetDouble("kbdirY", dy * factor);
		agent.WatchedAttributes.SetDouble("kbdirZ", dz * factor);

		// Trigger vanilla knockback application via PModuleKnockback (same mechanism used by ReceiveDamage()).
		agent.WatchedAttributes.SetInt("onHurtCounter", agent.WatchedAttributes.GetInt("onHurtCounter") + 1);
		agent.WatchedAttributes.SetFloat("onHurt", 0.051f);
	}

	#endregion

	#region Derailment/Crash
	internal void ServerDerailForInvalidTrack()
	{
		if (Api?.Side != EnumAppSide.Server || Derailed || IsFollower) return;
		double yaw = ServerPos.Yaw;
		double dx = Math.Sin(yaw);
		double dz = Math.Cos(yaw);
		ServerDerailConvoy(Math.Abs(Speed), dx, dz, 1, ServerPos.X, ServerPos.Y, ServerPos.Z);
	}

	// Called on server when we run out of track with no wall in front.
	private void ServerDerailConvoy(double impactSpeedABS, double dx, double dz, int movementDirection, double endX, double endY, double endZ)
	{
		// Derailed physics runs per-entity, but derailment is triggered by the simulated head.
		if (Api.Side != EnumAppSide.Server || IsFollower) return;

		RailwayVehicleShared.ServerShakeImpact(Api, ServerPos.XYZ, impactSpeedABS);

		double lengthSQ = dx * dx + dz * dz;
		if (lengthSQ < 1e-8) return;

		double inverseLength = 1.0 / Math.Sqrt(lengthSQ);
		double forwardX = dx * inverseLength * movementDirection;
		double forwardZ = dz * inverseLength * movementDirection;

		// Right-hand perpendicular in XZ
		double sideX = -forwardZ;
		double sideZ = forwardX;

		// IMPORTANT: snap head to the real track endpoint BEFORE clearing rail binding, otherwise we stay at the previous tick pos and can start inside rail collision.
		ServerPos.X = (float)endX;
		ServerPos.Y = (float)endY;
		ServerPos.Z = (float)endZ;
		Pos.SetFrom(ServerPos);

		List<EntityMinecart> carts = new(8) { this };

		if (IsConvoyHead)
		{
			if (ConvoySystem != null && ConvoySystem.TryGetOrderedMemberIDs(EntityId, out var memberIDs))
			{
				for (int memberIndex = 1; memberIndex < memberIDs.Count; memberIndex++)
				{
					if (Api.World.GetEntityById(memberIDs[memberIndex]) is EntityMinecart minecart) carts.Add(minecart);
				}
			}
		}

		Random random = Api.World.Rand;
		for (int cartIndex = 0; cartIndex < carts.Count; cartIndex++)
		{
			carts[cartIndex].ServerSetDerailedState(impactSpeedABS, forwardX, forwardZ, sideX, sideZ, cartIndex, carts.Count, random);
		}
	}


	private void ServerSetDerailedState(double impactSpeedABS, double forwardX, double forwardZ, double sideX, double sideZ, int orderIndex, int cartCount, Random random)
	{
		// Break convoy links (everyone becomes independent)
		if (Api.Side == EnumAppSide.Server) { ConvoySystem?.ForceFree(this); }
		ServerClearConvoyState();

		// Leaving the rails frees our occupancy zone.
		ServerVacateOccupancyZone();

		RailwayVehicleShared.ServerStartDerailed
		(
			this,
			ref Derailed,
			ref Cursor,
			ref Speed,
			DerailedVelocityBPS,
			ref DerailedYawVelocityRadians,
			impactSpeedABS,
			forwardX, forwardZ,
			sideX, sideZ,
			orderIndex, cartCount,
			random,
			nudgeForward: DerailedNudgeForward,
			nudgeSide: DerailedNudgeSide,
			attributeDerailedKey: DerailedAttributeKey
		);
	}

	private void TickDerailed(float deltaTime)
	{
		RailwayVehicleShared.TickDerailed
		(
			this, deltaTime, DerailedVelocityBPS, ref DerailedYawVelocityRadians, DerailedNewPositionScratch, BelowBlockPositionScratch,
			in RailwayVehicleShared.DerailedPhysicsParameters.MinecartDefault
		);
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
		if (!RailwayVehicleShared.TryGetServerPlayer(Api, byEntity, out EntityPlayer playerEntity, out serverPlayer)) return false;

		// Can't remove if a player is sitting on it.
		if (RailwayVehicleShared.HasMountedPassenger(SeatableBehaviour)) { serverPlayer.SendIngameError("yangtransport:locomotive-pickup-seated"); return false; }

		// Engine carts prevent removal while running or containing fluid.
		if (SteamEngineBehaviour != null)
		{
			if (SteamEngineBehaviour.IsBurningForPickupReject()) { serverPlayer.SendIngameError("yangtransport:locomotive-pickup-running"); return false; }
			if (SteamEngineBehaviour.HasAnyFluidForPickupReject()) { serverPlayer.SendIngameError("yangtransport:locomotive-pickup-contents"); return false; }
		}

		// Railed: only allowed while fully stopped and not part of a convoy.
		// Derailed: residual physics velocity does not prevent manual removal.
		if (!Derailed)
		{
			if (Math.Abs(Speed) > DriveParameters.StopEpsilon)	{ serverPlayer.SendIngameError("yangtransport:locomotive-pickup-moving"); return false; }
			if (ConvoyHeadID != 0)		{ serverPlayer.SendIngameError("yangtransport:locomotive-pickup-attachment"); return false; }
		}

		// Storage attachments reject removal if any attached container still has contents since we can't seemingly reliably dump its contents.
		var attachmentInventory = AttachableBehaviour?.Inventory;
		if (attachmentInventory != null)
		{
			for (int attachmentIndex = 0; attachmentIndex < attachmentInventory.Count; attachmentIndex++)
			{
				ItemSlot slot = attachmentInventory[attachmentIndex];
				if (slot.Empty) continue;

				var attachedInteractions = slot.Itemstack?.Collectible.GetCollectibleInterface<IAttachedInteractions>();
				if (attachedInteractions != null && !attachedInteractions.OnTryDetach(slot, attachmentIndex, this)) { serverPlayer.SendIngameError("yangtransport:locomotive-pickup-contents"); return false; }
			}
		}

		return true;
	}

	private void ServerDropFuelAndAttachablesForRemoval()
	{
		// Engine carts drop remaining fuel so it does not vanish.
		ItemStack? fuelDrop = SteamEngineBehaviour?.TakeRemainingFuelForPickupDrop();
		if (fuelDrop != null) { Api.World.SpawnItemEntity(fuelDrop, ServerPos.XYZ, null); }

		// Drop attachments before we despawn.
		DropAttachablesOnBreak();
	}


	/// Placement/Removal of attachments
	private void ServerDetachStorageIfEmpty(EntityAgent byEntity)
	{
		if (Api.Side != EnumAppSide.Server || byEntity is not EntityPlayer playerEntity) return;
		IServerPlayer? serverPlayer = Api.World.PlayerByUid(playerEntity.PlayerUID) as IServerPlayer;
		if (serverPlayer == null) return;

		var attachmentBehavior = AttachableBehaviour;
		var attachmentInventory = attachmentBehavior?.Inventory;
		if (attachmentInventory == null || attachmentInventory.Count == 0) return;

		int selectionBoxIndex = playerEntity.EntitySelection?.SelectionBoxIndex ?? -1;
		if (selectionBoxIndex <= 0) return;

		// Don't allow removal if someone is still mounted.
		if (RailwayVehicleShared.HasMountedPassenger(SeatableBehaviour)) { serverPlayer.SendIngameError("yangtransport:locomotive-pickup-seated"); return; }

		// Determine the slot for this selection box (selection boxes are 1-based, 0 = entity).
		ItemSlot slot = attachmentBehavior.GetSlotFromSelectionBoxIndex(selectionBoxIndex - 1);
		if (slot == null || slot.Empty) return;

		int slotIndex = 0;
		for (int inventoryIndex = 0; inventoryIndex < attachmentInventory.Count; inventoryIndex++) { if (ReferenceEquals(attachmentInventory[inventoryIndex], slot)) { slotIndex = inventoryIndex; break; } }

		// Ask the attachment whether it is allowed to be detached. For held-bag style storage, this returns false while it still contains items.
		var attachedInteractions = slot.Itemstack?.Collectible.GetCollectibleInterface<IAttachedInteractions>();
		if (attachedInteractions != null && !attachedInteractions.OnTryDetach(slot, slotIndex, this)) { serverPlayer.SendIngameError("yangtransport:locomotive-pickup-contents"); return; }

		ItemStack dropStack = slot.Itemstack;
		if (dropStack == null) return;

		// Notify attachment listeners (closes GUIs, etc) before we remove it.
		dropStack.Collectible.GetCollectibleInterface<IAttachedListener>()?.OnDetached(slot, slotIndex, this, byEntity);

		// Give to player, otherwise drop at player.
		if (!serverPlayer.InventoryManager.TryGiveItemstack(dropStack, slotNotifyEffect: true))
		{
			Api.World.SpawnItemEntity(dropStack, serverPlayer.Entity.ServerPos.XYZ, null);
		}

		slot.Itemstack = null;
		attachmentBehavior.storeInv();
		MarkShapeModified();
	}
	#endregion

	#region Tessellation
	public override void OnTesselation(ref Shape entityShape, string shapePathForLogging)
	{
		base.OnTesselation(ref entityShape, shapePathForLogging);
		RailwayVehicleShared.ApplyLeverTessellation(ref entityShape, WatchedAttributes);
	}

	public override void OnTesselated()
	{
		base.OnTesselated();
		ApplyDynamicFlatbedHitbox();
	}

	private void ApplyDynamicFlatbedHitbox()
	{
		// Reduce the "storage" selection box height when there's no storage attachment, so players can click across the cart more easily. Good UX.
		var attachable = AttachableBehaviour ??= GetBehavior<EntityBehaviorAttachable>();
		bool hasStorageAttachment = attachable != null && attachable.Inventory != null && attachable.Inventory.Count > 0 && !attachable.Inventory[0].Empty;

		var selectionBoxesBehavior = GetBehavior<EntityBehaviorSelectionBoxes>();
		if (selectionBoxesBehavior == null || selectionBoxesBehavior.selectionBoxes == null || selectionBoxesBehavior.selectionBoxes.Length == 0) return;

		for (int selectionBoxIndex = 0; selectionBoxIndex < selectionBoxesBehavior.selectionBoxes.Length; selectionBoxIndex++)
		{
			var selectionBoxAttachmentPointAndPose = selectionBoxesBehavior.selectionBoxes[selectionBoxIndex];
			var sourceAttachmentPoint = selectionBoxAttachmentPointAndPose?.AttachPoint;
			if (sourceAttachmentPoint == null || sourceAttachmentPoint.Code == null) continue;
			if (!sourceAttachmentPoint.Code.Equals("storage", StringComparison.OrdinalIgnoreCase)) continue;

			// Ensure our per-entity clone exists (Shape.Clone/ShapeElement.Clone intentionally shallow-clone AttachmentPoint objects).
			// If we mutate srcAp.ParentElement, it may affect all carts. So we swap in a private AttachmentPoint clone.
			if (DynamicStorageAttachmentPoint == null || DynamicStorageParentElement == null)
			{
				var parentElement = sourceAttachmentPoint.ParentElement;
				if (parentElement == null || parentElement.From == null || parentElement.To == null || parentElement.From.Length < 2 || parentElement.To.Length < 2) return;

				DynamicStorageParentElement = parentElement.Clone();
				DynamicStorageFromY = DynamicStorageParentElement.From[1];
				DynamicStorageFullToY = DynamicStorageParentElement.To[1];

				DynamicStorageAttachmentPoint = new AttachmentPoint()
				{
					Code = sourceAttachmentPoint.Code,
					PosX = sourceAttachmentPoint.PosX, PosY = sourceAttachmentPoint.PosY, PosZ = sourceAttachmentPoint.PosZ,
					RotationX = sourceAttachmentPoint.RotationX, RotationY = sourceAttachmentPoint.RotationY, RotationZ = sourceAttachmentPoint.RotationZ,
					ParentElement = DynamicStorageParentElement
				};
			}
			else
			{
				// Keep transform values in sync, in case something about the base shape changed.
				DynamicStorageAttachmentPoint.PosX = sourceAttachmentPoint.PosX; DynamicStorageAttachmentPoint.PosY = sourceAttachmentPoint.PosY; DynamicStorageAttachmentPoint.PosZ = sourceAttachmentPoint.PosZ;
				DynamicStorageAttachmentPoint.RotationX = sourceAttachmentPoint.RotationX; DynamicStorageAttachmentPoint.RotationY = sourceAttachmentPoint.RotationY; DynamicStorageAttachmentPoint.RotationZ = sourceAttachmentPoint.RotationZ;
			}

			double desiredToY = hasStorageAttachment ? DynamicStorageFullToY : DynamicStorageFromY + 1; // This final value is the exact height we want it as
			if (DynamicStorageParentElement != null && DynamicStorageParentElement.To != null && DynamicStorageParentElement.To.Length >= 2)
			{
				DynamicStorageParentElement.To[1] = desiredToY;
			}

			// Swap the selection box to our per-entity attach point clone (prevents global effects).
			selectionBoxAttachmentPointAndPose.AttachPoint = DynamicStorageAttachmentPoint;

			return;
		}
	}
	#endregion
}
