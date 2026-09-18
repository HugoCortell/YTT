using System;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using InteractionRangeHack;
using static YangTransport.RailwayVehicleShared;
using Vintagestory.GameContent;

namespace YangTransport;

public sealed class EntityBehaviorSteamPowered : EntityBehavior, ISteamEngineHost, IExtendedSteamParticleRangeSource, ICustomInteractionHelpPositioning
{
	private SteamEngineConfig Configuration;

	public SteamFXProfile SteamEffectsProfile { get; private set; } = SteamFXProfile.Default;
	private SteamEngineController EngineController = null!;

	private SteamEngineEntityCommonClientUI? ClientUI;

	// Client-only cached bounds (mutated in-place, no allocations per tick)
	private readonly Vec3d SteamEmissionMinimum = new();
	private readonly Vec3d SteamEmissionMaximum = new();
	private readonly Vec3f SteamEffectsLocalPosition = new(0.5f, 1f, 0.5f);
	private readonly Matrixf SteamEffectsTransformationMatrix = new();
	private EntityBehaviorStandardGaugeLocomotiveStats? StandardGaugeLocomotiveStats;
	private float SteamEffectsScale = 1f;
	private float SteamEffectsRotationY;
	private bool ExtendedParticleRangeEnabled;
	private double ExtendedParticleRangeSquared = ModSystemSteamEngineFeedback.DefaultParticleMaxDistanceSQ;
	private double AudioPreCullPadding = 4.0;

	// Persistence throttling (engine controller can request frequent syncs, we don't need to rewrite full state every time)
	private long LastPersistenceMS;

	// Diagetic 3-state lever controls (shared keys in RailwayVehicleShared)
	private int DriveLeverSelectionBoxIndex = 2; // Configured per-entity; matches EntitySelection.SelectionBoxIndex + 1

	// Turning lever (shared keys in RailwayVehicleShared)
	private int TurnLeverSelectionBoxIndex = -1; // Configured per-entity; -1 disables the lever

	// Selection boxes other behaviors should handle before this behavior opens the boiler UI.
	private int[] PassThroughSelectionBoxIndices = Array.Empty<int>();

	// Optional choo choo
	private WhistleSpec Whistle = WhistleSpec.Disabled;
	private long NextWhistleAllowedMS;

	public EntityBehaviorSteamPowered(Entity entity) : base(entity) { }

	#region Init & Interaction
	public override void Initialize(EntityProperties properties, JsonObject typeAttributes)
	{
		base.Initialize(properties, typeAttributes);

		// Steam particle tuning (defaults from props, can be overridden per-variant)
		SteamEffectsProfile = SteamFXProfile.FromAttributes(properties.Attributes, SteamFXProfile.Default);
		SteamEffectsProfile = SteamFXProfile.FromAttributes(typeAttributes, SteamEffectsProfile);

		StandardGaugeLocomotiveStats = entity.GetBehavior<EntityBehaviorStandardGaugeLocomotiveStats>();
		AudioPreCullPadding = StandardGaugeLocomotiveStats != null ? 16.0 : 4.0;

		ExtendedParticleRangeEnabled = entity is EntityStandardGaugeLocomotive;
		if (ExtendedParticleRangeEnabled)
		{
			double range = Math.Max(0.0, entity.SimulationRange); if (range <= 0.0) { range = ModSystemSteamEngineFeedback.DefaultParticleMaxDistance; }
			ExtendedParticleRangeSquared = range * range;
		}

		Configuration = LoadConfigForVariant(properties, typeAttributes);
		Whistle = WhistleSpec.FromAttributes(properties, typeAttributes, entity.Code?.Domain ?? GlobalConstants.DefaultDomain);
		EngineController = new SteamEngineController(this);

		// Load persisted full engine+inventory state
		var savedState = entity.Attributes.GetTreeAttribute("steamEngineState");
		EngineController.Initialize
		(
			Configuration,
			inventoryID: $"yangtransport:steamenginecart-{entity.EntityId}",
			inventoryPosition: null,
			serializedTree: savedState,
			worldAccessorForResolution: API.World,
			serializedTreeIncludesInventory: true
		);

		// Diegetic drive lever config/state
		DriveLeverSelectionBoxIndex = typeAttributes["DriveLeverSelectionBoxIndex"].AsInt(1);
		int persistedDriveLeverPhase = entity.Attributes.GetInt(AttributeDriveLeverPhase, DefaultDriveLeverPhase);
		entity.Attributes.SetInt(AttributeDriveLeverPhase, persistedDriveLeverPhase);
		entity.WatchedAttributes.SetInt(AttributeDriveLeverPhase, persistedDriveLeverPhase);

		// Diegetic turning lever config/state (optional)
		TurnLeverSelectionBoxIndex = typeAttributes["TurnLeverSelectionBoxIndex"].AsInt(DriveLeverSelectionBoxIndex + 1);
		PassThroughSelectionBoxIndices = typeAttributes["PassThroughSelectionBoxIndices"].AsArray<int>(Array.Empty<int>(), null);
		int persistedTurnPhase = entity.Attributes.GetInt(AttributeTurnLeverPhase, DefaultTurnLeverPhase);
		if (TurnLeverSelectionBoxIndex >= 0)
		{
			entity.Attributes.SetInt(AttributeTurnLeverPhase, persistedTurnPhase);
			entity.WatchedAttributes.SetInt(AttributeTurnLeverPhase, persistedTurnPhase);
		}

		if (API.Side == EnumAppSide.Client)
		{
			CompositeShape compositeShape = entity.Properties?.Client?.Shape;
			if (compositeShape != null)
			{
				SteamEffectsScale = entity.Properties.Client.Size;
				SteamEffectsRotationY = compositeShape.rotateY;

				string cacheKey = $"{entity.Code.ToShortString()}|{compositeShape.Base}";
				if (SteamFxLocator.TryGetLocalSteamFxPos((ICoreClientAPI)API, compositeShape, cacheKey, out Vec3f localSteamEffectsPosition))
				{
					SteamEffectsLocalPosition.Set(localSteamEffectsPosition);
				}
			}

			// UI
			ClientUI = new SteamEngineEntityCommonClientUI
			(
				entity,
				EngineController,
				canPlayerUse: player => CanPlayerUse(player),
				dialogTitleProvider: () =>
				{
					var name = entity.GetName();
					return string.IsNullOrEmpty(name) ? Lang.Get("yangtransport:enginecart-title") : name;
				},
				showLocomotiveControlsProvider: () => false
			);

			entity.WatchedAttributes.RegisterModifiedListener("steamEngineSync", OnWatchedSyncChanged);
			entity.WatchedAttributes.RegisterModifiedListener(AttributeDriveLeverPhase, entity.MarkShapeModified);
			if (TurnLeverSelectionBoxIndex >= 0) { entity.WatchedAttributes.RegisterModifiedListener(AttributeTurnLeverPhase, entity.MarkShapeModified); }
			(API as ICoreClientAPI)?.ModLoader.GetModSystem<ModSystemSteamEngineFeedback>()?.Register(this);
		}
	}

	private static SteamEngineConfig LoadConfigForVariant(EntityProperties properties, JsonObject typeAttributes)
	{
		JsonObject configAttributes = (typeAttributes != null && typeAttributes.Exists && typeAttributes["SteamEngine"].Exists) ? typeAttributes : properties.Attributes;
		return SteamEngineConfig.FromJson(configAttributes);
	}

	public override void OnInteract(EntityAgent byEntity, ItemSlot itemSlot, Vec3d hitPosition, EnumInteractMode mode, ref EnumHandling handled)
	{
		if (mode != EnumInteractMode.Interact) return;
		if (byEntity is not EntityPlayer playerEntity) return;

		// Shared diegetic whistle/horn (server owned)
		if (IsWhistleClick(playerEntity))
		{
			if (API.Side == EnumAppSide.Server) { ServerTryPlayWhistle(); }
			handled = EnumHandling.Handled;
			return;
		}

		// Diegetic drive lever (server owned)
		if (IsDriveLeverClick(playerEntity))
		{
			if (API.Side == EnumAppSide.Server) { ApplyDriveLeverInput(byEntity); }
			handled = EnumHandling.Handled;
			return;
		}

		// Diegetic turn lever (server owned)
		if (IsTurnLeverClick(playerEntity))
		{
			if (API.Side == EnumAppSide.Server) { ApplyTurnLeverInput(byEntity); }
			handled = EnumHandling.Handled;
			return;
		}

		if (ShouldPassThroughClick(playerEntity)) return;

		if (API.Side == EnumAppSide.Server)
		{
			if (SteamEngineEntityInteractions.TryHandleHeldInteractServer(entity, EngineController, playerEntity, itemSlot))
			{
				handled = EnumHandling.Handled;
				return;
			}
			return;
		}

		// Client: if this click is predicted to move fuel/water then don't open the UI
		if (SteamEngineEntityInteractions.WouldHandleHeldInteractClient(entity, EngineController, itemSlot))
		{
			handled = EnumHandling.Handled;
			return;
		}

		var clientAPI = API as ICoreClientAPI;
		if (clientAPI == null) return;

		ClientUI?.Toggle(clientAPI.World.Player);
		handled = EnumHandling.Handled;
	}


	public override WorldInteraction[] GetInteractionHelp(IClientWorldAccessor world, EntitySelection entitySelection, IClientPlayer player, ref EnumHandling handled)
	{
		int selectionBoxIndex = entitySelection?.SelectionBoxIndex ?? -1;

		if (Whistle.Enabled && selectionBoxIndex == Whistle.SelectionBoxIndex)
		{
			handled = EnumHandling.PreventSubsequent;
			return SimpleRightClickHelp("yangtransport:entityhelp-steamengine-whistle");
		}

		if (selectionBoxIndex == DriveLeverSelectionBoxIndex)
		{
			handled = EnumHandling.PreventSubsequent;
			return SimpleRightClickHelp("yangtransport:entityhelp-steamengine-drivelever");
		}

		if (TurnLeverSelectionBoxIndex >= 0 && selectionBoxIndex == TurnLeverSelectionBoxIndex)
		{
			handled = EnumHandling.PreventSubsequent;
			return SimpleRightClickHelp("yangtransport:entityhelp-steamengine-turnlever");
		}

		if (IsPassThroughSelectionBoxIndex(selectionBoxIndex))
		{
			return base.GetInteractionHelp(world, entitySelection, player, ref handled);
		}

		handled = EnumHandling.PreventSubsequent;
		return SteamEngineEntityInteractions.GetBodyInteractionHelp(world, entity, EngineController, player);
	}


	public bool TransparentCenter => entity.GetBehavior<EntityBehaviorSGBodySelectionBoxes>()?.TransparentCenter ?? false;

	public Vec3d GetInteractionHelpPosition()
	{
		EntityBehaviorSGBodySelectionBoxes standardGaugeBodySelectionBoxes = entity.GetBehavior<EntityBehaviorSGBodySelectionBoxes>();
		if (standardGaugeBodySelectionBoxes != null) return standardGaugeBodySelectionBoxes.GetInteractionHelpPosition();

		if (API is not ICoreClientAPI clientAPI) return null;

		EntitySelection selection = clientAPI.World.Player.CurrentEntitySelection;
		if (selection == null || selection.Entity != entity) return null;

		double selectionBoxOffsetX = entity.SelectionBox.X2 - entity.OriginSelectionBox.X2;
		double selectionBoxOffsetZ = entity.SelectionBox.Z2 - entity.OriginSelectionBox.Z2;

		return entity.SidedPos.XYZ.AddCopy(selectionBoxOffsetX, entity.SelectionBox.Y2 + 0.35, selectionBoxOffsetZ);
	}

	private static WorldInteraction[] SimpleRightClickHelp(string actionLanguageCode)
	{
		return new[]
		{
			new WorldInteraction
			{
				ActionLangCode = actionLanguageCode,
				MouseButton = EnumMouseButton.Right
			}
		};
	}

	private bool IsPassThroughSelectionBoxIndex(int selectionBoxIndex)
	{
		for (int passThroughIndex = 0; passThroughIndex < PassThroughSelectionBoxIndices.Length; passThroughIndex++)
		{
			if (selectionBoxIndex == PassThroughSelectionBoxIndices[passThroughIndex]) return true;
		}

		return false;
	}

	/// Helpers for the diagetic lever
	private bool IsDriveLeverClick(EntityPlayer playerEntity)
	{
		int selectedBoxIndex = playerEntity.EntitySelection?.SelectionBoxIndex ?? -1;
		return selectedBoxIndex == DriveLeverSelectionBoxIndex;
	}

	private bool IsTurnLeverClick(EntityPlayer playerEntity)
	{
		if (TurnLeverSelectionBoxIndex < 0) return false;
		int selectedBoxIndex = playerEntity.EntitySelection?.SelectionBoxIndex ?? -1;
		return selectedBoxIndex == TurnLeverSelectionBoxIndex;
	}

	private bool ShouldPassThroughClick(EntityPlayer playerEntity)
	{
		int selectedBoxIndex = playerEntity.EntitySelection?.SelectionBoxIndex ?? -1;
		for (int passThroughIndex = 0; passThroughIndex < PassThroughSelectionBoxIndices.Length; passThroughIndex++)
		{
			if (selectedBoxIndex == PassThroughSelectionBoxIndices[passThroughIndex]) return true;
		}
		return false;
	}

	private bool IsWhistleClick(EntityPlayer playerEntity)
	{
		if (!Whistle.Enabled || Whistle.SelectionBoxIndex < 0) return false;
		return (playerEntity.EntitySelection?.SelectionBoxIndex ?? -1) == Whistle.SelectionBoxIndex;
	}

	internal bool ServerTryPlayWhistle()
	{
		if (API?.Side != EnumAppSide.Server || !Whistle.Enabled || Whistle.Sound == null) return false;

		long currentTimeMS = API.World.ElapsedMilliseconds;
		if (currentTimeMS < NextWhistleAllowedMS) return false;
		NextWhistleAllowedMS = currentTimeMS + Whistle.CooldownMS;

		float pitch = 1f;
		if (Whistle.PitchRandomizationRange > 0f)
		{
			pitch += (float)((API.World.Rand.NextDouble() * 2.0 - 1.0) * Whistle.PitchRandomizationRange);
			pitch = GameMath.Clamp(pitch, 0.1f, 3f);
		}

		GetWhistleWorldPosition(out double worldX, out double worldY, out double worldZ);
		API.World.PlaySoundAt(Whistle.Sound, worldX, worldY, worldZ, null, pitch, Whistle.SoundRange, Whistle.Volume);
		return true;
	}

	private void GetWhistleWorldPosition(out double worldX, out double worldY, out double worldZ)
	{
		worldX = entity.ServerPos.X;
		worldY = entity.ServerPos.InternalY + (entity.SelectionBox?.Y2 ?? 1f) * 0.5f;
		worldZ = entity.ServerPos.Z;

		if (entity is EntityStandardGaugeLocomotive standardGaugeLocomotive && !string.IsNullOrEmpty(Whistle.AttachmentPointCode))
		{
			SGLocomotiveBodyTransform.TryGetAttachmentPointWorld
			(
				standardGaugeLocomotive, Whistle.AttachmentPointCode,
				new Vec3f(0f, 0f, 0f), out worldX, out worldY, out worldZ, out _, out _
			);
		}
	}

	private int GetDriveDirection() { return PhaseToDriveDirection(entity.WatchedAttributes.GetInt(AttributeDriveLeverPhase, DefaultDriveLeverPhase)); }
	private int GetTurnChoice() { return PhaseToTurnChoice(entity.WatchedAttributes.GetInt(AttributeTurnLeverPhase, DefaultTurnLeverPhase)); }

	private void ApplyDriveLeverInput(EntityAgent byEntity)
	{
		EntityControls controls = byEntity.MountedOn?.Controls ?? byEntity.Controls;
		int phase = entity.WatchedAttributes.GetInt(AttributeDriveLeverPhase, DefaultDriveLeverPhase);
		int driveDirection = PhaseToDriveDirection(phase);

		int newPhase;
		if (controls.ShiftKey && !controls.CtrlKey)
		{
			// Up one state: Backwards -> Stop -> Forwards (no wrap)
			driveDirection = Math.Min(1, driveDirection + 1);
			newPhase = DriveDirectionToPhase(driveDirection);
		}
		else if (controls.CtrlKey && !controls.ShiftKey)
		{
			// Down one state: Forwards -> Stop -> Backwards (no wrap)
			driveDirection = Math.Max(-1, driveDirection - 1);
			newPhase = DriveDirectionToPhase(driveDirection);
		}
		else
		{
			// Cycle: Backwards -> Stop -> Forwards -> Stop -> etc
			newPhase = (phase + 1) % 4;
		}

		SetDriveLeverPhase(newPhase);
	}

	private void SetDriveLeverPhase(int phase)
	{
		phase = GameMath.Clamp(phase, 0, 3);
		entity.Attributes.SetInt(AttributeDriveLeverPhase, phase);
		entity.WatchedAttributes.SetInt(AttributeDriveLeverPhase, phase);
	}

	private void ApplyTurnLeverInput(EntityAgent byEntity)
	{
		if (TurnLeverSelectionBoxIndex < 0) return;
		EntityControls controls = byEntity.MountedOn?.Controls ?? byEntity.Controls;
		int phase = entity.WatchedAttributes.GetInt(AttributeTurnLeverPhase, DefaultTurnLeverPhase);
		int turnChoice = PhaseToTurnChoice(phase);

		int newPhase;
		if (controls.ShiftKey && !controls.CtrlKey)
		{
			// Up one state: Left -> Straight -> Right (no wrap)
			turnChoice = Math.Min(1, turnChoice + 1);
			newPhase = TurnChoiceToPhase(turnChoice);
		}
		else if (controls.CtrlKey && !controls.ShiftKey)
		{
			// Down one state: Right -> Straight -> Left (no wrap)
			turnChoice = Math.Max(-1, turnChoice - 1);
			newPhase = TurnChoiceToPhase(turnChoice);
		}
		else
		{
			// Cycle: Left -> Straight -> Right -> Straight -> etc
			newPhase = (phase + 1) % 4;
		}

		SetTurnLeverPhase(newPhase);
	}

	private void SetTurnLeverPhase(int phase)
	{
		if (TurnLeverSelectionBoxIndex < 0) return;
		phase = GameMath.Clamp(phase, 0, 3);
		entity.Attributes.SetInt(AttributeTurnLeverPhase, phase);
		entity.WatchedAttributes.SetInt(AttributeTurnLeverPhase, phase);
	}
	#endregion

	#region LifeCycle
	public override void OnGameTick(float deltaTime)
	{
		EngineController.OnGameTick(deltaTime);

		if (API.Side == EnumAppSide.Client && entity is EntityMinecart && !entity.IsRendered)
		{
			// Tell the renderer explicitly that visible-distance sampling crossed a cull gap.
			// Vintage Story already skips animator work for entities that are neither normally rendered nor shadow-rendered,
			// so do not force CalculateMatrices off: a shadows-only haulage engine may still legitimately need its current pose.
			if (entity.Properties?.Client?.Renderer is EntityMinecartHaulageRenderer haulageRenderer) { haulageRenderer.MarkCulled(); }
		}
	}
	#endregion

	#region Death
	public override void OnEntityDespawn(EntityDespawnData despawnData)
	{
		if (API.Side == EnumAppSide.Client && API is ICoreClientAPI clientAPI)
		{
			ClientUI?.Close();
			clientAPI.ModLoader.GetModSystem<ModSystemSteamEngineFeedback>()?.Unregister(this);
		}
		base.OnEntityDespawn(despawnData);
	}
	#endregion

	public bool ComputePowerEngaged()
	{
		// For engine carts, interpret this as "we are requesting traction" (placeholder). // Haha, I guess not so much a placeholder anymore (todo)
		return EngineController.GetSteamPower01() > 0.01;
	}

	private void OnWatchedSyncChanged()
	{
		ClientUI?.OnWatchedSyncChanged();
	}

	public bool TryGetDrive(out EngineCartDrive drive)
	{
		// Only provide drive output once we have usable steam pressure.
		if (EngineController.GetSteamPower01() <= 0.001)
		{
			drive = default;
			return false;
		}

		drive = new EngineCartDrive(this);
		return true;
	}

	public readonly struct EngineCartDrive
	{
		private readonly EntityBehaviorSteamPowered EngineBehaviour;
		public EngineCartDrive(EntityBehaviorSteamPowered BehaviourDrive) { this.EngineBehaviour = BehaviourDrive; }

		public int GetDriveDir()
		{
		    int driveDirection = EngineBehaviour.GetDriveDirection();
		    if (driveDirection == 0) return 0;
			if (!EngineBehaviour.ComputePowerEngaged()) return 0;
			
		    // Hard gate at boiling: no drive if cold.
		    return EngineBehaviour.EngineController.TemperatureC < 100 ? 0 : driveDirection;
		}

		public double GetHeatUnitsPer100C()
		{
		    double temperatureC = EngineBehaviour.EngineController.TemperatureC;
		    return temperatureC < 100 ? 0 : (temperatureC / 100.0);
		}

		public double RawPowerNPer100C => EngineBehaviour.Configuration.RawPowerNewtonsPer100Celsius;
		public double AccelerationNPer100C => EngineBehaviour.Configuration.AccelerationNewtonsPer100Celsius;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool TryComputeLoadedKinematicLimits(int convoyWeight, out double maxSpeedBPS, out double accelBPSSquared)
		{
			maxSpeedBPS = 0;
			accelBPSSquared = 0;

			double heat = GetHeatUnitsPer100C();
			if (heat <= 0) return false;

			SteamEngineConfig configuration = EngineBehaviour.Configuration;

			double weightedSpeed = configuration.RawPowerNewtonsPer100Celsius * heat - convoyWeight;
			maxSpeedBPS = weightedSpeed <= 0 ? 0 : Math.Min(weightedSpeed, configuration.SpeedLimitBlocksPerSecond);

			accelBPSSquared = Math.Min(configuration.AccelerationBlocksPerSecondSquaredPerHeatUnit * heat, configuration.AccelerationLimitBlocksPerSecondSquared);
			return true;
		}
	}

	/// ISteamEngineHost
	public ICoreAPI API => entity.Api;
	public Vec3d WorldPosition => entity.Pos.XYZ;
	public BlockPos BlockPosition => new BlockPos((int)entity.Pos.X, (int)entity.Pos.Y, (int)entity.Pos.Z);

	public bool ClientTryToggleBoilerUi(IPlayer player, bool showTooFarError = true)
	{
		if (API.Side != EnumAppSide.Client || ClientUI == null || player == null) return false;
		if (CanPlayerUse(player)) { ClientUI.Toggle(player); return true; }

		if (showTooFarError && API is ICoreClientAPI clientAPI)
		{
			clientAPI.TriggerIngameError(this, "yangtransport:locomotive-too-far", Lang.Get("ingameerror-yangtransport:locomotive-too-far"));
		}

		return true;
	}

	public bool CanPlayerUse(IPlayer player)
	{
		if (player?.Entity == null) return false;

		Vec3d eyePosition = player.Entity.Pos.XYZ.Add(player.Entity.LocalEyePos);
		float interactionRange = GetEffectiveEntityInteractionRange(player);

		return interactionRange > 0 && entity.InRangeOf(eyePosition, interactionRange * interactionRange, interactionRange);
	}

	private float GetEffectiveEntityInteractionRange(IPlayer player)
	{
		// InteractionRangeHack exposes per-entity ranges, but server-side UI packets do not reliably arrive with player.WorldData.PickingRange already expanded.
		// Use the entity provider value directly here, then still delegate shape/long-stock handling to Entity.InRangeOf().

		float interactionRange = player?.WorldData?.PickingRange ?? 0f;
		if (entity is IEntityInteractionRangeProvider rangeProvider) { interactionRange = Math.Max(interactionRange, rangeProvider.EntityInteractionRange); }

		return interactionRange;
	}

	public void OpenInventory(IServerPlayer player) => player.InventoryManager.OpenInventory(EngineController.Inventory);
	public void CloseInventory(IServerPlayer player) => player.InventoryManager.CloseInventory(EngineController.Inventory);

	public void RequestSync(bool forced)
	{
		if (API.Side != EnumAppSide.Server) return;

		long currentTimeMS = API.World.ElapsedMilliseconds;

		// Persist engine state (throttled). Inventory only needs to be rewritten when forced (fuel/fluid changes).
		var persistentState = entity.Attributes.GetOrAddTreeAttribute("steamEngineState");
		if (forced)
		{
			EngineController.ToTreeAttributesWithInventory(persistentState);
			LastPersistenceMS = currentTimeMS;
		}
		else if (currentTimeMS - LastPersistenceMS > 10_000)
		{
			EngineController.ToTreeAttributes(persistentState);
			LastPersistenceMS = currentTimeMS;
		}

		// Sync to clients (small tree)
		var synchronizedState = entity.WatchedAttributes.GetOrAddTreeAttribute("steamEngineSync");
		EngineController.ToTreeAttributes(synchronizedState);
		entity.WatchedAttributes.MarkPathDirty("steamEngineSync");
	}

	public void ExplodeAndRemove()
	{
		if (API.Side != EnumAppSide.Server) return;
		if (API.World is IServerWorldAccessor serverWorld) { serverWorld.CreateExplosion(BlockPosition, EnumBlastType.RockBlast, 2, 1); }

		TransportDropTables.SpawnEntityDrops(entity, TransportDropTables.DestroyDropsTable, entity.ServerPos.XYZ, randomVelocity: true);
		entity.Die(EnumDespawnReason.Death, new DamageSource
		{
			Source = EnumDamageSource.Machine,
			Type = EnumDamageType.Heat,
			SourceEntity = entity,
			CauseEntity = entity,
			SourcePos = entity.ServerPos.XYZ
		});
	}

	public override void OnReceivedClientPacket(IServerPlayer player, int packetID, byte[] data, ref EnumHandling handled)
	{
		if (packetID == SteamEnginePacketIds.SetDriveLeverPhase || packetID == SteamEnginePacketIds.SetTurnLeverPhase)
		{
			ApplyRemoteLeverInput(player, packetID, data);
			handled = EnumHandling.Handled;
			return;
		}

		EngineController.OnReceivedClientPacket(player, packetID, data);
		handled = EnumHandling.Handled;
	}

	private void ApplyRemoteLeverInput(IServerPlayer player, int packetID, byte[] data)
	{
		if (!CanAcceptRemoteLeverInput(player)) return;
		ServerTryApplyRemoteLeverInput(packetID, data);
	}

	internal bool ServerTryApplyRemoteLeverInput(int packetID, byte[] data)
	{
		if (API.Side != EnumAppSide.Server || data == null || data.Length < 1) return false;

		int phase = GameMath.Clamp((int)data[0], 0, 2);
		if (packetID == SteamEnginePacketIds.SetDriveLeverPhase) { SetDriveLeverPhase(phase); return true; }
		if (packetID == SteamEnginePacketIds.SetTurnLeverPhase) { SetTurnLeverPhase(phase); return true; }

		return false;
	}

	internal bool ServerTryApplyRemoteLeverInputFromConvoySeat(IServerPlayer player, Vec3d interactionPosition, int packetID, byte[] data)
	{
		if (API.Side != EnumAppSide.Server || player == null || interactionPosition == null) return false;
		if (!CanPlayerUseFromConvoySeat(player, interactionPosition))
		{
			player.SendIngameError("yangtransport:locomotive-too-far");
			return false;
		}

		return ServerTryApplyRemoteLeverInput(packetID, data);
	}

	private bool CanPlayerUseFromConvoySeat(IPlayer player, Vec3d interactionPosition)
	{
		if (player?.Entity == null) return false;

		float interactionRange = GetEffectiveEntityInteractionRange(player);
		if (interactionRange <= 0) return false;

		Vec3d eyePosition = interactionPosition.AddCopy(player.Entity.LocalEyePos.X, player.Entity.LocalEyePos.Y, player.Entity.LocalEyePos.Z);
		return entity.InRangeOf(eyePosition, interactionRange * interactionRange, interactionRange);
	}

	private bool CanAcceptRemoteLeverInput(IServerPlayer player)
	{
		if (player?.Entity?.MountedOn == null) return false;

		IMountableSeat seat = player.Entity.MountedOn;
		Entity? mountedEntity = seat.Entity ?? seat.MountSupplier?.OnEntity;
		if (mountedEntity?.EntityId != entity.EntityId) return false;

		if (entity is EntityStandardGaugeLocomotive) { return seat.SeatId == "conductor"; }
		return true;
	}

	public override string PropertyName() => "SteamPowered";

	public void ForceExtinguishBoiler() { if (API.Side != EnumAppSide.Server) return; EngineController.ForceExtinguishBoiler(); }

	public void ForceDriveLeverStop()
	{
		if (API.Side != EnumAppSide.Server) return;
		SetDriveLeverPhase(DefaultDriveLeverPhase);
	}

	// Fast check used by pickup logic. True if the engine still contains any working fluid (water/boiling water/etc).
	public bool HasAnyFluidForPickupReject() { return EngineController != null && !EngineController.WaterSlot.Empty && EngineController.WaterSlot.StackSize > 0; }

	// Fast check used by pickup logic. True if the boiler is currently burning fuel.
	public bool IsBurningForPickupReject() { return EngineController != null && EngineController.IsBurning; }


	public ItemStack? TakeRemainingFuelForPickupDrop()
	{
		if (API.Side != EnumAppSide.Server) return null;

		// Ensure we don't keep simulating a burning boiler after the cart is gone.
		EngineController.ForceExtinguishBoiler();

		if (EngineController.FuelSlot.Empty) return null;
		return SteamEngineController.TakeOutAndNotify(EngineController.FuelSlot, EngineController.FuelSlot.StackSize);
	}

	private readonly struct WhistleSpec
	{
		public static readonly WhistleSpec Disabled = new(null, -1, null, 0.05f, 1200, 96f, 1f);

		public readonly AssetLocation? Sound;
		public readonly int SelectionBoxIndex;
		public readonly string? AttachmentPointCode;
		public readonly float PitchRandomizationRange;
		public readonly int CooldownMS;
		public readonly float SoundRange;
		public readonly float Volume;

		public bool Enabled => Sound != null;

		private WhistleSpec(AssetLocation? sound, int selectionBoxIndex, string? attachmentPointCode, float pitchRandomizationRange, int cooldownMS, float range, float volume)
		{
			Sound = sound;
			SelectionBoxIndex = selectionBoxIndex;
			AttachmentPointCode = attachmentPointCode;
			PitchRandomizationRange = pitchRandomizationRange;
			CooldownMS = cooldownMS;
			SoundRange = range;
			Volume = volume;
		}

		public static WhistleSpec FromAttributes(EntityProperties properties, JsonObject typeAttributes, string defaultDomain)
		{
			JsonObject sourceAttributes = (typeAttributes != null && typeAttributes.Exists && typeAttributes["WhistleHorn"].Exists) ? typeAttributes : properties.Attributes;
			if (sourceAttributes == null) return Disabled;

			JsonObject whistleAttributes = sourceAttributes["WhistleHorn"];
			if (whistleAttributes == null || !whistleAttributes.Exists) return Disabled;

			AssetLocation? sound = NormalizeSoundLocation(whistleAttributes["Sound"].AsString(null), defaultDomain);
			if (sound == null) return Disabled;

			int selectionBoxIndex = whistleAttributes["SelectionBoxIndex"].AsInt(-1);
			string? attachmentPoint = whistleAttributes["AttachmentPoint"].AsString(null);
			float pitchRange = GameMath.Clamp(whistleAttributes["PitchRandomizationRange"].AsFloat(0.05f), 0f, 1f);

			int cooldownMS = whistleAttributes["CooldownMs"].AsInt(1200);
			float range = Math.Max(0f, whistleAttributes["Range"].AsFloat(96f));
			float volume = Math.Max(0f, whistleAttributes["Volume"].AsFloat(1f));

			return new WhistleSpec(sound, selectionBoxIndex, attachmentPoint, pitchRange, Math.Max(0, cooldownMS), range, volume);
		}

		private static AssetLocation? NormalizeSoundLocation(string? soundPath, string defaultDomain)
		{
			if (string.IsNullOrWhiteSpace(soundPath)) return null;

			string trimmed = soundPath.Trim();
			bool hasDomain = trimmed.IndexOf(AssetLocation.LocationSeparator) >= 0;
			AssetLocation soundLocation = new AssetLocation(trimmed);

			string domain = hasDomain ? soundLocation.Domain : defaultDomain;
			if (string.IsNullOrEmpty(domain)) domain = GlobalConstants.DefaultDomain;

			string path = soundLocation.Path ?? string.Empty;
			if (!path.StartsWith("sounds/", StringComparison.OrdinalIgnoreCase)) path = "sounds/" + path;

			return new AssetLocation(domain.ToLowerInvariant(), path.ToLowerInvariant());
		}
	}

	internal void CopySteamFxLocalPosition(Vec3f target) { target.Set(SteamEffectsLocalPosition); }

	#region Effects
	public void GetSteamEmissionBounds(Vec3d min, Vec3d max)
	{
		if 
		(
			StandardGaugeLocomotiveStats != null && SGLocomotiveBodyTransform.TryTransformBodyLocalPointWorld
			(
				entity, SteamEffectsLocalPosition, SteamEffectsTransformationMatrix,
				out double standardGaugeWorldX, out double standardGaugeWorldY, out double standardGaugeWorldZ
			)
		)
		{
			min.Set(standardGaugeWorldX - 0.03, standardGaugeWorldY - 0.03, standardGaugeWorldZ - 0.03);
			max.Set(standardGaugeWorldX + 0.03, standardGaugeWorldY + 0.03, standardGaugeWorldZ + 0.03);
			return;
		}

		if
		(
			StandardGaugeLocomotiveStats == null && entity.IsRendered &&
			entity.Properties?.Client?.Renderer is EntityMinecartHaulageRenderer haulageRenderer &&
			haulageRenderer.TryGetSteamEffectWorldPosition(out double haulageX, out double haulageY, out double haulageZ
		)
		)
		{
			min.Set(haulageX - 0.03, haulageY - 0.03, haulageZ - 0.03);
			max.Set(haulageX + 0.03, haulageY + 0.03, haulageZ + 0.03);
			return;
		}

		double yaw = entity.Pos.Yaw + (SteamEffectsRotationY + 90f) * GameMath.DEG2RAD;
		double cos = Math.Cos(yaw);
		double sin = Math.Sin(yaw);

		double ox = (SteamEffectsLocalPosition.X - 0.5) * SteamEffectsScale;
		double oy = SteamEffectsLocalPosition.Y * SteamEffectsScale;
		double oz = (SteamEffectsLocalPosition.Z - (StandardGaugeLocomotiveStats == null ? 0.5 : 1.0)) * SteamEffectsScale;

		double rx = ox * cos - oz * sin;
		double rz = ox * sin + oz * cos;

		double wx = entity.Pos.X + rx;
		double wy = entity.Pos.InternalY + oy;
		double wz = entity.Pos.Z + rz;

		if (StandardGaugeLocomotiveStats != null)
		{
			double forwardOffset = StandardGaugeLocomotiveStats.BodyOffsetForward + StandardGaugeLocomotiveStats.FrontBogieOffset;
			double locomotiveYaw = entity.Pos.Yaw;
			double forwardX = Math.Sin(locomotiveYaw);
			double forwardZ = Math.Cos(locomotiveYaw);
			double rightX = forwardZ;
			double rightZ = -forwardX;

			wx += forwardX * forwardOffset + rightX * StandardGaugeLocomotiveStats.BodyOffsetLateral;
			wy += StandardGaugeLocomotiveStats.BodyOffsetVertical;
			wz += forwardZ * forwardOffset + rightZ * StandardGaugeLocomotiveStats.BodyOffsetLateral;
		}

		min.Set(wx - 0.03, wy - 0.03, wz - 0.03);
		max.Set(wx + 0.03, wy + 0.03, wz + 0.03);
	}

	public bool TryGetFeedback(out double temperatureC, out int fuelBurnTemperatureC, out byte liquidKind)
		=> EngineController.TryGetFeedback(out temperatureC, out fuelBurnTemperatureC, out liquidKind);

	public bool TryGetAudioState(out double temperatureC, out bool producingPower)
		=> EngineController.TryGetAudioState(out temperatureC, out producingPower);

	public bool IsWithinAudioPreCull(double playerX, double playerY, double playerZ, double range)
	{
		double maxDistance = range + AudioPreCullPadding;
		double dx = playerX - entity.Pos.X;
		double dy = playerY - entity.Pos.InternalY;
		double dz = playerZ - entity.Pos.Z;
		return dx * dx + dy * dy + dz * dz <= maxDistance * maxDistance;
	}

	public void GetEmissionBounds(out Vec3d minimumPosition, out Vec3d maximumPosition)
	{
		GetSteamEmissionBounds(SteamEmissionMinimum, SteamEmissionMaximum);
		minimumPosition = SteamEmissionMinimum;
		maximumPosition = SteamEmissionMaximum;
	}

	public bool UseExtendedParticleRange => ExtendedParticleRangeEnabled;

	public bool IsInExtendedParticleRange(double playerX, double playerY, double playerZ)
	{
		double dx = playerX - entity.Pos.X;
		double dy = playerY - entity.Pos.InternalY;
		double dz = playerZ - entity.Pos.Z;
		return dx * dx + dy * dy + dz * dz <= ExtendedParticleRangeSquared;
	}
	#endregion

	#region Offscreen Simulation
	private const double MinimumOfflineDriveSpeedBlocksPerSecond = 0.01;

	internal SteamEngineController Controller => EngineController;

	internal bool OfflineSimTryGetDriveDir(out int driveDirection) // True if this cart currently provides traction in some direction.
	{
		driveDirection = 0;
		if (!TryGetDrive(out var engineDrive)) return false;
		driveDirection = engineDrive.GetDriveDir();
		return driveDirection != 0;
	}

	internal bool OfflineSimTryGetDrivePotential(out double speedPotentialBPS, out double speedLimitBPS)
	{
		speedPotentialBPS = 0;
		speedLimitBPS = 0;

		if (!TryGetDrive(out var engineDrive)) return false;

		double heat = engineDrive.GetHeatUnitsPer100C();
		if (heat <= 0) return false;

		SteamEngineConfig configuration = EngineController.Configuration;
		speedPotentialBPS = configuration.RawPowerNewtonsPer100Celsius * heat;
		speedLimitBPS = configuration.SpeedLimitBlocksPerSecond;
		return speedPotentialBPS > MinimumOfflineDriveSpeedBlocksPerSecond;
	}

	internal double OfflineSimEstimateFuelSecondsRemaining()
	{
		// FuelBurnTime counts down in effective seconds, and queued pieces use the same globally-scaled duration.
		double secondsRemaining = Math.Max(0, EngineController.FuelBurnTime);

		var stack = EngineController.FuelSlot.Itemstack;
		var combustionProperties = stack?.Collectible?.CombustibleProps;
		if (combustionProperties == null || combustionProperties.BurnDuration <= 0) return secondsRemaining;

		secondsRemaining += EngineController.FuelSlot.StackSize * EngineController.GetEffectiveFuelBurnDuration(combustionProperties.BurnDuration);
		return secondsRemaining;
	}

	internal double OfflineSimEstimateWaterSecondsRemaining()
	{
		return EngineController.EstimateWorkingFluidSecondsRemaining();
	}

	// Approximiation of remaining fuel. No heat emulation, only remove fuel + working fluid and advance internal burn timers.
	internal void OfflineSimFastForwardBurn(double burnSeconds)
	{
		if (API.Side != EnumAppSide.Server) return;
		if (burnSeconds <= 0.001) return;

		EngineController.FastForwardUnloadedBurn((float)burnSeconds);
	}
	#endregion
}
