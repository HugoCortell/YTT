using System;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Client;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace YangTransport;

/// Shared reusable steam engine simulation + inventory + UI packet handling. Used by both block entities and living entities.
public sealed class SteamEngineController
{
	// Host + config
	private readonly ISteamEngineHost Host;

	public SteamEngineConfig Configuration { get; private set; }

	// Inventory
	private readonly InventoryGeneric EngineInventory;

	public InventoryBase Inventory => EngineInventory;
	public ItemSlot FuelSlot => EngineInventory[0];
	public ItemSlot WaterSlot => EngineInventory[1];

	// Persisted / synced state
	public double TemperatureC { get; private set; } = AmbientTemperatureC;

	// Config multiplier for fuel burn
	private float FuelDurationScale = 1f;

	// Thermostat target (works in steps)
	public double LimitTemperatureC { get; private set; } = -1;

	// Ignite stuffs
	public bool CanIgniteFuel => FuelIgnitionEnabled;
	private bool FuelIgnitionEnabled;

	// Power stuffs
	public bool PowerEngaged { get; private set; }

	// Firepit-like fuel burn state (seconds)
	public float FuelBurnTime { get; private set; }
	public float MaximumFuelBurnTime { get; private set; }
	public int FuelBurnTemperatureC { get; private set; }

	public bool IsBurning => FuelBurnTime > 0f;

	// Liquid modifiers
	private float TemperatureIncreaseSpeedMultiplier = 1f; // <1 slower heating	|	>1 faster heating
	private float WaterConsumptionMultiplier = 1f;  // >1 more consumption
	private float SynchronizedWorkingFluidConsumptionLitresPerHour = float.NaN;
	private bool ThermostatMaximumLockEnabled;
	private double StoredLimitBeforeLock = -1;

	public bool ThermostatLockedToMaximum => ThermostatMaximumLockEnabled;

	public float WorkingFluidConsumptionLitresPerHour => float.IsFinite(SynchronizedWorkingFluidConsumptionLitresPerHour)
			? SynchronizedWorkingFluidConsumptionLitresPerHour
			: (float)(Configuration.WorkingFluidConsumptionLitresPerHour * WaterConsumptionMultiplier);

	// Working fluid consumption accumulator (litres). Persisted to avoid exploits.
	private double WorkingFluidRemainderLitres;

	// Working-fluid data. Cache them so the burn loop does not repeatedly I/O deserialize WaterTightContainableProps.
	private CollectibleObject CachedWorkingFluidCollectible;
	private WaterTightContainableProps CachedWorkingFluidProperties;
	private double CachedWorkingFluidItemsPerLitre = 1.0;
	private bool CachedWorkingFluidIsFlammable;

	// Inventory opt
	private int InventoryMutationDepth;
	private bool ForcedSynchronizationPending;

	// Client-synced byte used for particle color (0 none/black, 1 water/white, 2 other/gray)
	private byte FeedbackLiquidKind;

	// Simulation timing / sync gating
	private const double AmbientTemperatureC							= 20;
	private const int SimulationTickIntervalMS							= 500;		// 2 Hz is plenty for heat + UI
	private const int MinimumSynchronizationIntervalMSWithUIOpen		= 500;		// while a GUI is open
	private const int MinimumSynchronizationIntervalMSIdle				= 2000;		// when nobody is watching
	private const double SynchronizationTemperatureEpsilonCelsius		= 0.5;		// only sync notable changes
	private const double SecondsPerHour									= 3600.0;

	// UI open tracking (server-side best-effort)
	private int OpenClientDialogCount;

	// Simulation tick time gates
	private long NextSimulationCheckMS;
	private float SimulationAccumulatedSeconds;

	// Previous values for sync gating
	private double PreviousTemperatureForSynchronization = double.NaN;
	private float PreviousFuelBurnTimeForSynchronization = float.NaN;
	private bool PreviousPowerEngagedForSynchronization;
	private long LastSynchronizationMS;

	// Cached emission bounds (world space)
	private readonly Vec3d SteamEmissionMinimum = new();
	private readonly Vec3d SteamEmissionMaximum = new();

	private static readonly AssetLocation SteamVentAsset = new("yangtransport", "sounds/steam_vent");

	// Thermostat target clamping
	private const int TargetMinimumCelsius = 0;
	private const int TargetMaximumCelsius = 1500;
	private const int TargetStep = 50;

	// Locomotive tuning helpers (used by engine cart drive code)
	public const double TrainAccelerationScale = 0.10;
	public const double TrainSpeedScale = 0.12;

	public SteamEngineController(ISteamEngineHost host)
	{
		this.Host = host ?? throw new ArgumentNullException(nameof(host));

		// Note: We build inventory with null id/api and LateInitialize() it in Initialize().
		EngineInventory = new InventoryGeneric(2, null, null, (slotID, owningInventory) =>
		{
			return slotID switch
			{
				0 => new ItemSlotSteamEngineFuel(owningInventory),
				1 => new ItemSlotSteamEngineWater(owningInventory, capacityLitres: 1), // capacity set in Initialize()
				_ => new ItemSlot(owningInventory)
			};
		});

		EngineInventory.BaseWeight = 1f;
		EngineInventory.SlotModified += OnSlotModified;
	}

	public void Initialize // Must be called once after construction.
	(
		SteamEngineConfig configuration,
		string? inventoryID,
		BlockPos inventoryPosition = null,
		ITreeAttribute serializedTree = null,
		IWorldAccessor worldAccessorForResolution = null,
		bool serializedTreeIncludesInventory = false,
		bool lateInitializeInventory = true
	)
	{
		Configuration = configuration;
		if (Host.API.Side == EnumAppSide.Server) { FuelDurationScale = YangTransportSettings.EngineFuelDurationScale; }

		// Inventory can be hosted by a BlockEntityContainer which already LateInitialize()'d it.
		// For entities (and other non-container hosts), we LateInitialize here.
		if (lateInitializeInventory)
		{
			EngineInventory.LateInitialize(inventoryID, Host.API);
			EngineInventory.Pos = inventoryPosition;
		}
		else
		{
			// Best-effort. If a host forgot to LateInitialize, ensure API is set so InvNetworkUtil works.
			if (EngineInventory.Api == null)
			{
				EngineInventory.Api = Host.API;
				if (EngineInventory.InvNetworkUtil != null) EngineInventory.InvNetworkUtil.Api = Host.API;
			}
			if (inventoryPosition != null) EngineInventory.Pos = inventoryPosition;
		}

		// Keep the water slot capacity aligned to config
		if (EngineInventory[1] is ItemSlotLiquidOnly liquidSlot) { liquidSlot.CapacityLitres = (float)Configuration.CapacityLitres; }

		// Load inventory (optional)
		if (serializedTreeIncludesInventory && serializedTree != null)
		{
			ITreeAttribute inventoryTree = serializedTree.GetTreeAttribute("inventory");
			if (inventoryTree != null)
			{
				EngineInventory.FromTreeAttributes(inventoryTree);
				EngineInventory.ResolveBlocksOrItems();
			}
		}

		// Load engine state (optional)
		if (serializedTree != null) { FromTreeAttributes(serializedTree, worldAccessorForResolution ?? Host.API.World); }

		// Default target limit to rating unless loaded from save
		if (LimitTemperatureC < 0) { LimitTemperatureC = Configuration.MaxTemperatureCelsius; }

		LimitTemperatureC = ClampAndSnapTarget(LimitTemperatureC);

		// Ensure remainder isn't stale if slot is empty
		if (WaterSlot.Empty) { WorkingFluidRemainderLitres = 0; }

		// Server-side multipliers should match the current working fluid immediately
		if (Host.API.Side == EnumAppSide.Server) { RecomputeLiquidModifiers(applyThermostat: true); }

		FeedbackLiquidKind = (byte)ClassifyLiquidKindByte(WaterSlot?.Itemstack);

		// Reset sync gating baselines
		PreviousTemperatureForSynchronization = double.NaN;
		PreviousFuelBurnTimeForSynchronization = float.NaN;
		PreviousPowerEngagedForSynchronization = PowerEngaged;
		LastSynchronizationMS = 0;

		// Push initial state out (mirrors old BlockEntitySteamEngine.Initialize MarkDirty(false))
		if (Host.API.Side == EnumAppSide.Server)
		{
			PowerEngaged = Host.ComputePowerEngaged();
			PreviousPowerEngagedForSynchronization = PowerEngaged;
			RequestForcedSync();
		}
	}

	public double GetSteamPower01()
	{
		// Below boiling, we consider there to be no usable steam pressure
		if (TemperatureC <= 100) return 0;
		double temperatureRangeCelsius = Math.Max(1, Configuration.MaxTemperatureCelsius - 100);
		return GameMath.Clamp((TemperatureC - 100) / temperatureRangeCelsius, 0, 1);
	}

	// Server-side simulation tick. Internally steps the sim at ~2Hz.
	public void OnGameTick(float deltaTime)
	{
		var coreAPI = Host.API;
		if (coreAPI == null || coreAPI.Side != EnumAppSide.Server) return;

		if (!ShouldSimulate) { SimulationAccumulatedSeconds = 0; return; } // Keep sim accum from growing while idle

		SimulationAccumulatedSeconds += deltaTime;

		// Step in fixed 500ms slices for stability. Cap iterations to avoid spiral-of-death during extreme lag spikes.
		int remainingSimulationStepAllowance = 10;
		float simulationStepSeconds = SimulationTickIntervalMS / 1000f;
		while (SimulationAccumulatedSeconds >= simulationStepSeconds && remainingSimulationStepAllowance-- > 0)
		{
			SimulationAccumulatedSeconds -= simulationStepSeconds;
			StepSimulation(simulationStepSeconds);
		}
	}

	// Returns true when the engine should be simulated. Hosts can use this to register/unregister tick listeners if desired.
	public bool ShouldSimulate
	{
		// Same condition as the old EnsureSimTickingIfNeeded()
		get { return IsBurning || Math.Abs(TemperatureC - AmbientTemperatureC) > 0.1 || (FuelIgnitionEnabled && !FuelSlot.Empty); }
	}

	#region Serialization helpers
	public void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessorForResolution)
	{
		// Load engine state (NOT inventory) from a tree. Key names mirror the old BlockEntitySteamEngine for painless refactors.
		TemperatureC = tree.GetDouble("tempC", AmbientTemperatureC);
		LimitTemperatureC = tree.GetDouble("limitTempC", -1);
		FuelIgnitionEnabled = tree.GetBool("canIgniteFuel", false);

		FuelBurnTime = tree.GetFloat("fuelBurnTime", 0f);
		MaximumFuelBurnTime = tree.GetFloat("maxFuelBurnTime", 0f);
		FuelBurnTemperatureC = tree.GetInt("fuelBurnTemperatureC", 0);
		PowerEngaged = tree.GetBool("powerEngaged", PowerEngaged);

		float serializedFuelDurationScale = tree.GetFloat("fuelDurationScale", 1f);
		if (!float.IsFinite(serializedFuelDurationScale) || serializedFuelDurationScale <= 0f) serializedFuelDurationScale = 1f;

		if (worldAccessorForResolution?.Side == EnumAppSide.Server)
		{
			float currentFuelDurationScale = YangTransportSettings.EngineFuelDurationScale;
			if (Math.Abs(serializedFuelDurationScale - currentFuelDurationScale) > 0.000001f)
			{
				float scaleRatio = currentFuelDurationScale / serializedFuelDurationScale;
				FuelBurnTime *= scaleRatio;
				MaximumFuelBurnTime *= scaleRatio;
			}
			FuelDurationScale = currentFuelDurationScale;
		}
		else if (worldAccessorForResolution?.Side == EnumAppSide.Client)
		{
			FuelDurationScale = serializedFuelDurationScale;
			SynchronizedWorkingFluidConsumptionLitresPerHour = tree.GetFloat("workingFluidConsumptionLPerHour", float.NaN);
		}

		FeedbackLiquidKind = (byte)tree.GetInt("steamLiqKind", FeedbackLiquidKind);
		WorkingFluidRemainderLitres = tree.GetDouble("wfRemainderL", 0.0);
		StoredLimitBeforeLock = tree.GetDouble("storedLimitBeforeLock", StoredLimitBeforeLock);
	}

	// Write engine state (NOT inventory) to a tree. Includes convenience keys for UI.
	public void ToTreeAttributes(ITreeAttribute tree)
	{
		tree.SetDouble("tempC", TemperatureC);
		tree.SetDouble("limitTempC", LimitTemperatureC);
		tree.SetBool("canIgniteFuel", FuelIgnitionEnabled);
		tree.SetFloat("workingFluidConsumptionLPerHour", WorkingFluidConsumptionLitresPerHour);
		tree.SetDouble("storedLimitBeforeLock", StoredLimitBeforeLock);

		// Convenience keys for GUI (UI reads these directly)
		tree.SetFloat("temperatureC", (float)TemperatureC);
		tree.SetFloat("limitTemperatureC", (float)LimitTemperatureC);

		tree.SetFloat("fuelBurnTime", FuelBurnTime);
		tree.SetFloat("maxFuelBurnTime", MaximumFuelBurnTime);
		tree.SetInt("fuelBurnTemperatureC", FuelBurnTemperatureC);
		tree.SetFloat("fuelDurationScale", FuelDurationScale);

		tree.SetInt("steamLiqKind", ClassifyLiquidKindByte(WaterSlot?.Itemstack));
		tree.SetBool("powerEngaged", PowerEngaged);
		tree.SetDouble("wfRemainderL", WorkingFluidRemainderLitres);

		tree.SetFloat("engineMaxTemperatureC", (float)Configuration.MaxTemperatureCelsius);
		tree.SetBool("canIgniteFuel", FuelIgnitionEnabled);
	}

	// >Write full state including inventory under key 'inventory'
	public void ToTreeAttributesWithInventory(ITreeAttribute tree)
	{
		ToTreeAttributes(tree);

		ITreeAttribute inventoryTree = tree.GetOrAddTreeAttribute("inventory");
		EngineInventory.ToTreeAttributes(inventoryTree);
	}

	// Load full state including inventory from key
	public void FromTreeAttributesWithInventory(ITreeAttribute tree, IWorldAccessor worldAccessorForResolution)
	{
		ITreeAttribute inventoryTree = tree.GetTreeAttribute("inventory");
		if (inventoryTree != null)
		{
			EngineInventory.FromTreeAttributes(inventoryTree);
			EngineInventory.ResolveBlocksOrItems();
		}

		FromTreeAttributes(tree, worldAccessorForResolution);
	}

	// Convenience for UI trees, sets only values the GUI expects to be present.
	public void WriteDialogValues(ITreeAttribute tree)
	{
		tree.SetFloat("temperatureC", (float)TemperatureC);
		tree.SetFloat("limitTemperatureC", (float)LimitTemperatureC);
		tree.SetFloat("fuelBurnTime", FuelBurnTime);
		tree.SetFloat("maxFuelBurnTime", MaximumFuelBurnTime);
		tree.SetInt("fuelBurnTemperatureC", FuelBurnTemperatureC);
		tree.SetFloat("fuelDurationScale", FuelDurationScale);
		tree.SetFloat("engineMaxTemperatureC", (float)Configuration.MaxTemperatureCelsius);
		tree.SetBool("canIgniteFuel", FuelIgnitionEnabled);
		tree.SetFloat("workingFluidConsumptionLPerHour", WorkingFluidConsumptionLitresPerHour);
		tree.SetBool("powerEngaged", PowerEngaged);
		tree.SetInt("steamLiqKind", FeedbackLiquidKind);
	}
	#endregion

	#region  Packet handling (server side)
	public void OnReceivedClientPacket(IPlayer player, int packetID, byte[] data)
	{
		var coreAPI = Host.API;
		if (coreAPI == null || coreAPI.Side != EnumAppSide.Server) return;
		if (player == null) return;

		// Always accept close packets.
		// Range-checking them can leave the server-side inventory/dialog bookkeeping stuck open precisely when the client auto-closes because the player moved away.
		if (packetID == SteamEnginePacketIds.Close)
		{
			player.InventoryManager?.CloseInventory((IInventory)Inventory);
			OpenClientDialogCount = Math.Max(0, OpenClientDialogCount - 1);
			return;
		}

		if (!Host.CanPlayerUse(player)) return;

		if (packetID >= 7 && packetID <= 9) // Vanilla inventory network ops
		{
			EngineInventory.InvNetworkUtil.HandleClientPacket(player, packetID, data);
			RequestForcedSync();
			return;
		}

		if (packetID == SteamEnginePacketIds.Open)
		{
			player.InventoryManager?.OpenInventory((IInventory)Inventory);
			OpenClientDialogCount++;

			// Ensure the client receives a full inventory snapshot immediately.
			// Without this, a freshly loaded world can have a non-dirty inventory that never transmits its contents, creating weird UIs.
			for (int slotIndex = 0; slotIndex < EngineInventory.Count; slotIndex++) EngineInventory.MarkSlotDirty(slotIndex);

			RequestForcedSync();
			return;
		}

		if (packetID == SteamEnginePacketIds.VentExcess)
		{
			if (ThermostatMaximumLockEnabled) return;

			double targetTemperatureCelsius = LimitTemperatureC;
			if (targetTemperatureCelsius < AmbientTemperatureC) targetTemperatureCelsius = AmbientTemperatureC;

			if (TemperatureC > targetTemperatureCelsius)
			{
				VentSteam(targetTemperatureCelsius);
				RequestForcedSync();
			}

			return;
		}

		if (packetID == SteamEnginePacketIds.DumpFluid)
		{
			if (!WaterSlot.Empty) { TakeOutAndNotify(WaterSlot, WaterSlot.StackSize); }
			return;
		}

		if (packetID == SteamEnginePacketIds.IgniteFuel)
		{
			bool isCreative = player.WorldData?.CurrentGameMode == EnumGameMode.Creative;

			if (!isCreative)
			{
				// Survival requires an ignition source.
				ItemStack? mainHandItemStack = player.InventoryManager?.ActiveHotbarSlot?.Itemstack;
				ItemStack? offHandItemStack  = player.Entity?.LeftHandItemSlot?.Itemstack;
				if (!IsIgnitionSource(mainHandItemStack) && !IsIgnitionSource(offHandItemStack)) return;
			}
			else if (FuelSlot.Empty && WaterSlot.Empty) { TryPrimeCreativeTestResources(); }

			TryIgniteNow();
			return;
		}

		if (packetID == SteamEnginePacketIds.CutFuel)
		{
			if (!IsBurning) return;

			BeginInventoryMutation();
			try
			{
				// Consume 1L of liquid per 100c of current temperature (proportional).
				double requiredLitres = TemperatureC <= 0 ? 0 : (TemperatureC / 100.0);

				if (requiredLitres > 0)
				{
					if (WaterSlot.Empty) return;
					RefreshWorkingFluidCacheIfNeeded();
					if (CachedWorkingFluidProperties == null) return;

					int requiredItemCount = (int)Math.Ceiling(requiredLitres * CachedWorkingFluidItemsPerLitre);
					if (requiredItemCount > WaterSlot.StackSize) return;

					TakeOutAndNotify(WaterSlot, requiredItemCount);
				}

				// Stop the current burn immediately and suppress auto-ignition until re-ignited manually.
				FuelBurnTime = 0f;
				MaximumFuelBurnTime = 0f;
				FuelBurnTemperatureC = 0;
				FuelIgnitionEnabled = false;

				// Vent everything (sets TemperatureC very low, sim will warm back toward ambient).
				VentSteam(0);

				RequestForcedSync();
			}
			finally { EndInventoryMutation(); }
			return;
		}

		if (packetID == SteamEnginePacketIds.SetTargetTemperature)
		{
			if (data == null || data.Length < 4) return;

			int requestedTemperatureCelsius = BitConverter.ToInt32(data, 0);

			// If locked, ignore (UI disables it anyway)
			if (!ThermostatMaximumLockEnabled)
			{
				LimitTemperatureC = ClampAndSnapTarget(requestedTemperatureCelsius);
				RequestForcedSync();
			}

			return;
		}
	}
	#endregion

	#region Fluid modifiers
	private void RefreshWorkingFluidCacheIfNeeded()
	{
		ItemStack workingFluidStack = WaterSlot?.Itemstack;
		CollectibleObject currentCollectible = workingFluidStack?.Collectible;
		if (ReferenceEquals(currentCollectible, CachedWorkingFluidCollectible)) return;

		CachedWorkingFluidCollectible = currentCollectible;
		CachedWorkingFluidProperties = currentCollectible == null ? null : BlockLiquidContainerBase.GetContainableProps(workingFluidStack);
		CachedWorkingFluidItemsPerLitre = CachedWorkingFluidProperties != null && CachedWorkingFluidProperties.ItemsPerLitre > 0 ? CachedWorkingFluidProperties.ItemsPerLitre : 1.0;
		CachedWorkingFluidIsFlammable = CachedWorkingFluidProperties?.NutritionPropsPerLitre != null && CachedWorkingFluidProperties.NutritionPropsPerLitre.Intoxication > 0f;
	}

	internal double GetWorkingFluidLitres()
	{
		if (WaterSlot.Empty) return 0;
		RefreshWorkingFluidCacheIfNeeded();
		if (CachedWorkingFluidProperties == null) return 0;
		return WaterSlot.StackSize / CachedWorkingFluidItemsPerLitre;
	}

	internal double EstimateWorkingFluidSecondsRemaining()
	{
		double litres = GetWorkingFluidLitres();
		if (litres <= 0) return 0;

		double litresPerHour = Math.Max(0.0001, WorkingFluidConsumptionLitresPerHour);
		return (litres / litresPerHour) * SecondsPerHour;
	}

	private void RecomputeLiquidModifiers(bool applyThermostat)
	{
		float newTemperatureIncreaseSpeedMultiplier = 1f;
		float newWaterConsumptionMultiplier = 1f;
		bool lockThermostat = false;

		RefreshWorkingFluidCacheIfNeeded();

		if (!WaterSlot.Empty)
		{
			if (CachedWorkingFluidIsFlammable)
			{
				// Flammable fluids
				newTemperatureIncreaseSpeedMultiplier = YangTransportSettings.FlammableLiquidHeatingMultiplier;
				newWaterConsumptionMultiplier = YangTransportSettings.FlammableLiquidEvaporationMultiplier;
				lockThermostat = true;
			}
			else if (!IsValidWorkingLiquid(WaterSlot.Itemstack))
			{
				// Contaminated fluids (anything non-fresh-water and non-flammable)
				newTemperatureIncreaseSpeedMultiplier = YangTransportSettings.ContaminatedLiquidHeatingMultiplier;
				newWaterConsumptionMultiplier = YangTransportSettings.ContaminatedLiquidEvaporationMultiplier;
			}
		}

		TemperatureIncreaseSpeedMultiplier = newTemperatureIncreaseSpeedMultiplier;
		WaterConsumptionMultiplier = newWaterConsumptionMultiplier;

		if (!applyThermostat) return;

		if (lockThermostat)
		{
			// Enter / stay locked
			if (!ThermostatMaximumLockEnabled) { if (StoredLimitBeforeLock < 0) StoredLimitBeforeLock = LimitTemperatureC; }

			ThermostatMaximumLockEnabled = true;
			LimitTemperatureC = ClampAndSnapTarget(TargetMaximumCelsius);
		}
		else
		{
			// Exit lock: restore previous user target if we saved one
			if (ThermostatMaximumLockEnabled)
			{
				ThermostatMaximumLockEnabled = false;
				if (StoredLimitBeforeLock >= 0)
				{
					LimitTemperatureC = ClampAndSnapTarget(StoredLimitBeforeLock);
					StoredLimitBeforeLock = -1;
				}
			}
			else { ThermostatMaximumLockEnabled = false; }
		}
	}

	// Fresh water is the only valid working fluid (free from contaminants and minerals)
	public static bool IsValidWorkingLiquid(ItemStack liquidStack)
	{
		return liquidStack?.Collectible?.Code?.Domain == "game" && liquidStack.Collectible.Code.Path == "waterportion";
	}

	// Flammability is guessed through intoxication, which usually means alcohol.
	public static bool IsLiquidFlammable(ItemStack liquidProperties)
	{
		if (liquidProperties?.Collectible == null) return false;
		var props = BlockLiquidContainerBase.GetContainableProps(liquidProperties);
		return props?.NutritionPropsPerLitre != null && props.NutritionPropsPerLitre.Intoxication > 0f;
	}
	#endregion

	#region Fuel helpers
	public bool IsValidFuel(ItemStack fuelStack) => ItemSlotSteamEngineFuel.IsValidFuel(fuelStack);

	public EnumIgniteState GetIgnitableState(float secondsIgniting)
	{
		if (FuelSlot.Empty) return EnumIgniteState.NotIgnitablePreventDefault;
		if (IsBurning) return EnumIgniteState.NotIgnitablePreventDefault;
		return secondsIgniting > 3f ? EnumIgniteState.IgniteNow : EnumIgniteState.Ignitable;
	}

	public void TryIgniteNow()
	{
		var hostAPI = Host.API;
		if (hostAPI == null || hostAPI.Side != EnumAppSide.Server) return;
		if (FuelSlot.Empty || IsBurning) return;

		BeginInventoryMutation();
		try
		{
			if (!TryStartBurningFromFuelSlot()) return;
			FuelIgnitionEnabled = true;
			RequestForcedSync();
		}
		finally { EndInventoryMutation(); }
	}

	public static bool IsIgnitionSource(ItemStack ignitionSourceStack)
	{
		if (ignitionSourceStack?.Collectible == null) return false;
		if (ignitionSourceStack.Collectible is ItemFirestarter) return true;
		return ignitionSourceStack.Collectible.HasBehavior<BlockBehaviorCanIgnite>(false);
	}

	private void TryPrimeCreativeTestResources()
	{
		var hostAPI = Host.API;
		if (hostAPI == null || hostAPI.Side != EnumAppSide.Server) return;
		if (!FuelSlot.Empty || !WaterSlot.Empty) return;

		Item fuelItem = hostAPI.World.GetItem(new AssetLocation("yangtransport", "ligmanite"));
		Item waterItem = hostAPI.World.GetItem(new AssetLocation("game", "waterportion"));
		if (fuelItem == null || waterItem == null) return;

		var creativeFuelStack = new ItemStack(fuelItem, 16);
		var creativeWaterStack = new ItemStack(waterItem, 1);

		if (!IsValidFuel(creativeFuelStack)) return;

		int waterItemCount = WaterSlot.GetRemainingSlotSpace(creativeWaterStack);
		if (waterItemCount <= 0) return;

		creativeWaterStack.StackSize = waterItemCount;

		BeginInventoryMutation();
		try
		{
			FuelSlot.Itemstack = creativeFuelStack;
			FuelSlot.MarkDirty();

			WorkingFluidRemainderLitres = 0;
			WaterSlot.Itemstack = creativeWaterStack;
			WaterSlot.MarkDirty();

			RequestForcedSync();
		}
		finally { EndInventoryMutation(); }
	}

	public bool TryPutFuelFromPlayer(IPlayer player, ItemSlot sourceSlot, int requestedQuantity)
	{
		if (Host.API == null || player == null || sourceSlot == null || sourceSlot.Empty) return false;
		if (!IsValidFuel(sourceSlot.Itemstack)) return false;

		var moveOperation = new ItemStackMoveOperation
		(
			Host.API.World,
			EnumMouseButton.Right,
			player.Entity.Controls.ShiftKey ? EnumModifierKey.SHIFT : 0,
			EnumMergePriority.DirectMerge,
			requestedQuantity
		);
		sourceSlot.TryPutInto(FuelSlot, ref moveOperation);
		return moveOperation.MovedQuantity > 0;
	}

	public bool TryTakeFuelToPlayer(IPlayer player, int quantity)
	{
		if (Host.API == null || player == null) return false;
		if (FuelSlot.Empty) return false;

		ItemSlot destinationSlot = player.InventoryManager.ActiveHotbarSlot;
		if (destinationSlot == null) return false;

		var moveOperation = new ItemStackMoveOperation(Host.API.World, EnumMouseButton.Right, EnumModifierKey.SHIFT, EnumMergePriority.DirectMerge, quantity);
		FuelSlot.TryPutInto(destinationSlot, ref moveOperation);
		return moveOperation.MovedQuantity > 0;
	}
	#endregion

	#region Feedback helpers
	public bool TryGetFeedback(out double temperatureCelsius, out int fuelBurnTemperatureCelsius, out byte feedbackLiquidKind)
	{
		temperatureCelsius = TemperatureC;
		fuelBurnTemperatureCelsius = FuelBurnTemperatureC;
		feedbackLiquidKind = FeedbackLiquidKind;

		if (FuelBurnTime <= 0f) return false;
		if (fuelBurnTemperatureCelsius <= 0) return false;
		if (fuelBurnTemperatureCelsius - temperatureCelsius <= 1) return false;
		return true;
	}

	public bool TryGetAudioState(out double temperatureCelsius, out bool producingPower)
	{
		temperatureCelsius = TemperatureC;

		if (!IsBurning && !FuelIgnitionEnabled) { producingPower = false; return false; }
		producingPower = PowerEngaged && temperatureCelsius >= 100.0; return true;
	}
	#endregion

	#region Simulation
	private void StepSimulation(float deltaTime)
	{
		// Once-per-second checks
		long nowMS = Host.API.World.ElapsedMilliseconds;
		if (nowMS > NextSimulationCheckMS)
		{
			NextSimulationCheckMS = nowMS + 1000;

			// Overpressure / kablooey chance
			if (TemperatureC > Configuration.MaxTemperatureCelsius && ((TemperatureC - Configuration.MaxTemperatureCelsius) / 100) > Host.API.World.Rand.Next(0, 100))
			{
				Host.ExplodeAndRemove();
				return;
			}
		}

		// Consume time only while currently burning.
		if (IsBurning)
		{
			FuelBurnTime -= deltaTime;
			if (FuelBurnTime <= 0f)
			{
				FuelBurnTime = 0f;
				MaximumFuelBurnTime = 0f;
				FuelBurnTemperatureC = 0;
			}

			ConsumeWorkingFluid(deltaTime);
		}

		// Only start next fuel when not burning.
		if (!IsBurning && FuelIgnitionEnabled && !FuelSlot.Empty)
		{
			if (!TryStartBurningFromFuelSlot() && Math.Abs(TemperatureC - AmbientTemperatureC) <= 0.1) // Don't tick forever
			{
				FuelIgnitionEnabled = false;
				RequestForcedSync();
				return;
			}
		}

		// Flame goes out when fuel is gone and we are no longer burning anything.
		if (!IsBurning && FuelSlot.Empty) { FuelIgnitionEnabled = false; }

		// Heat up / cool down using the firepit temperature curve. Which honestly was a shit choice and I probably should have made my own backend for this.
		if (IsBurning)
		{
			double targetTemperatureCelsius = GetCurrentTargetTemperature();
			double fuelTemperature = FuelBurnTemperatureC > 0 ? FuelBurnTemperatureC : AmbientTemperatureC; // Heat rate governed by heat source burn temp
			double speedReferenceTemperature = targetTemperatureCelsius > TemperatureC ? fuelTemperature : targetTemperatureCelsius; // Apply the fuel-based speed reference while heating up.

			float adjustedDeltaTime = deltaTime; if (targetTemperatureCelsius > TemperatureC) adjustedDeltaTime *= TemperatureIncreaseSpeedMultiplier;

			TemperatureC = ChangeTemperature(TemperatureC, targetTemperatureCelsius, speedReferenceTemperature, adjustedDeltaTime);
		}
		else { TemperatureC = ChangeTemperature(TemperatureC, AmbientTemperatureC, AmbientTemperatureC, deltaTime); }

		// Power engaged is host-defined
		PowerEngaged = Host.ComputePowerEngaged();

		MaybeSyncToClients();
	}

	private void ConsumeWorkingFluid(float deltaTime)
	{
		// Real-time consumption while fuel is lit independent of temperature.
		if (WaterSlot.Empty) return;

		double consumedLitres = (Configuration.WorkingFluidConsumptionLitresPerHour * WaterConsumptionMultiplier) * (deltaTime / SecondsPerHour);
		if (consumedLitres <= 0) return;

		double totalLitres = WorkingFluidRemainderLitres + consumedLitres;
		RefreshWorkingFluidCacheIfNeeded();
		if (CachedWorkingFluidProperties == null) return;

		double itemsPerLitre = CachedWorkingFluidItemsPerLitre;

		// Convert litres to whole portion-items.
		int dueItemCount = (int)Math.Floor(totalLitres * itemsPerLitre + 1e-9);
		if (dueItemCount <= 0) { WorkingFluidRemainderLitres = totalLitres; return; }

		int takenItemCount = Math.Min(dueItemCount, WaterSlot.StackSize);
		if (takenItemCount <= 0) return;

		BeginInventoryMutation();
		try
		{
			TakeOutAndNotify(WaterSlot, takenItemCount);

			double removedLitres = takenItemCount / itemsPerLitre;
			WorkingFluidRemainderLitres = totalLitres - removedLitres;

			if (WaterSlot.Empty) WorkingFluidRemainderLitres = 0; // Avoid drain issues on refill after running out
			FeedbackLiquidKind = (byte)ClassifyLiquidKindByte(WaterSlot?.Itemstack);

			RequestForcedSync();
		}
		finally { EndInventoryMutation(); }
	}
	#endregion

	private void OnSlotModified(int slotID)
	{
		var hostAPI = Host.API;
		if (hostAPI == null || hostAPI.Side != EnumAppSide.Server) return;

		// Inserting fuel does not start burning unless already lit in manual ignition
		if (slotID == 0 && FuelIgnitionEnabled && !IsBurning && !FuelSlot.Empty) { TryStartBurningFromFuelSlot(); }

		if (slotID == 1)
		{
			if (WaterSlot.Empty) WorkingFluidRemainderLitres = 0;
			RecomputeLiquidModifiers(applyThermostat: true);
			FeedbackLiquidKind = (byte)ClassifyLiquidKindByte(WaterSlot?.Itemstack);
		}

		RequestForcedSync();
	}

	private void BeginInventoryMutation() { InventoryMutationDepth++; }

	private void EndInventoryMutation()
	{
		InventoryMutationDepth--;
		if (InventoryMutationDepth > 0 || !ForcedSynchronizationPending) return;

		ForcedSynchronizationPending = false;
		RequestForcedSync();
	}

	private void RequestForcedSync()
	{
		if (InventoryMutationDepth > 0) { ForcedSynchronizationPending = true; return; }
		Host.RequestSync(force: true);
	}

	internal static ItemStack? TakeOutAndNotify(ItemSlot slot, int quantity)
	{
		if (slot == null || slot.Empty || quantity <= 0) return null;

		bool removesWholeStack = quantity >= slot.StackSize;
		ItemStack? removedStack = slot.TakeOut(quantity);

		// TakeOutWhole() already calls OnItemSlotModified(), partial TakeOut() does not.
		if (!removesWholeStack && removedStack != null) slot.MarkDirty();
		return removedStack;
	}

	#region Core Simultion Helpers
	private void VentSteam(double target)
	{
		// Burst effect client-side
		if (Host.API.Side == EnumAppSide.Client && Host.API is ICoreClientAPI clientAPI)
		{
			Host.GetSteamEmissionBounds(SteamEmissionMinimum, SteamEmissionMaximum);
			SteamEngineParticles.EmitVentingBurst (clientAPI, SteamEmissionMinimum, SteamEmissionMaximum, (float)GameMath.Clamp(((TemperatureC - target) / 30), 1, 200), FeedbackLiquidKind);
		}

		// Play sound for everyone
		Host.API.World.PlaySoundAt(SteamVentAsset, Host.WorldPosition.X, Host.WorldPosition.Y, Host.WorldPosition.Z, null, false, 32f, 1f);

		// At the end so as not to mess with our calc for TemperatureC - target in the particles
		TemperatureC = target;
	}

	private double GetCurrentTargetTemperature()
	{
		// Fuel wants to reach its burn temperature
		double fuelTemperature = FuelBurnTemperatureC > 0 ? FuelBurnTemperatureC : AmbientTemperatureC;

		// Target set by slider
		double targetTemperatureLimitCelsius = LimitTemperatureC;
		if (targetTemperatureLimitCelsius < AmbientTemperatureC) targetTemperatureLimitCelsius = AmbientTemperatureC;

		return Math.Min(fuelTemperature, targetTemperatureLimitCelsius);
	}

	private static double ClampAndSnapTarget(double value)
	{
		int roundedValue = (int)Math.Round(value);
		roundedValue = GameMath.Clamp(roundedValue, TargetMinimumCelsius, TargetMaximumCelsius);
		roundedValue = (int)Math.Round(roundedValue / (double)TargetStep) * TargetStep;
		roundedValue = GameMath.Clamp(roundedValue, TargetMinimumCelsius, TargetMaximumCelsius);
		return roundedValue;
	}

	public float GetEffectiveFuelBurnDuration(float baseBurnDuration) { return baseBurnDuration > 0f ? baseBurnDuration * FuelDurationScale : 0f; }

	private bool TryStartBurningFromFuelSlot()
	{
		if (Host.API.Side != EnumAppSide.Server || IsBurning || FuelSlot.Empty) return false;

		ItemStack fuelStack = FuelSlot.Itemstack;
		var combustibleProperties = fuelStack?.Collectible?.CombustibleProps;

		if (combustibleProperties == null || combustibleProperties.BurnDuration <= 0f || combustibleProperties.BurnTemperature <= 0) return false;

		float effectiveBurnDuration = GetEffectiveFuelBurnDuration(combustibleProperties.BurnDuration);

		// IMPORTANT: set burn state BEFORE marking the slot dirty, otherwise SlotModified fires while IsBurning is still false and we consume the whole damn stack.
		MaximumFuelBurnTime = effectiveBurnDuration;
		FuelBurnTime = effectiveBurnDuration;
		FuelBurnTemperatureC = combustibleProperties.BurnTemperature;

		// Consume exactly one fuel item
		TakeOutAndNotify(FuelSlot, 1);
		return true;
	}

	private static double ChangeTemperature(double currentTemperatureCelsius, double targetTemperatureCelsius, double speedReferenceTemperatureCelsius, float deltaTime)
	{
		// Step size based on distance to the speed reference (prevents thermostat from changing warmup speed).
		double speedDifferenceCelsius = Math.Abs(currentTemperatureCelsius - speedReferenceTemperatureCelsius);
		double adjustedDeltaTime = deltaTime + deltaTime * (speedDifferenceCelsius / 28.0);

		// But clamp/stop based on the actual target, to avoid overshoot.
		double targetDifferenceCelsius = Math.Abs(currentTemperatureCelsius - targetTemperatureCelsius);
		if (targetDifferenceCelsius < 1.0) return targetTemperatureCelsius;
		if (targetDifferenceCelsius < adjustedDeltaTime) return targetTemperatureCelsius;

		if (currentTemperatureCelsius > targetTemperatureCelsius) adjustedDeltaTime = -adjustedDeltaTime;
		return currentTemperatureCelsius + adjustedDeltaTime;
	}

	private void MaybeSyncToClients()
	{
		long nowMS = Host.API.World.ElapsedMilliseconds;

		int minimumSynchronizationIntervalMS = OpenClientDialogCount > 0 ? MinimumSynchronizationIntervalMSWithUIOpen : MinimumSynchronizationIntervalMSIdle;
		if (nowMS - LastSynchronizationMS < minimumSynchronizationIntervalMS) return;

		float burnEpsilon = OpenClientDialogCount > 0 ? 0.25f : 1.0f;
		double temperatureEpsilonCelsius = OpenClientDialogCount > 0 ? SynchronizationTemperatureEpsilonCelsius : 2.0;

		bool temperatureChanged = double.IsNaN(PreviousTemperatureForSynchronization)	|| Math.Abs(PreviousTemperatureForSynchronization - TemperatureC) >= temperatureEpsilonCelsius;
		bool burnChanged = float.IsNaN(PreviousFuelBurnTimeForSynchronization)			|| Math.Abs(PreviousFuelBurnTimeForSynchronization - FuelBurnTime) >= burnEpsilon;
		bool engagedChanged = PowerEngaged != PreviousPowerEngagedForSynchronization;

		if (temperatureChanged || burnChanged || engagedChanged)
		{
			PreviousTemperatureForSynchronization = TemperatureC;
			PreviousFuelBurnTimeForSynchronization = FuelBurnTime;
			PreviousPowerEngagedForSynchronization = PowerEngaged;
			LastSynchronizationMS = nowMS;

			Host.RequestSync(force: false);
		}
	}
	#endregion

	#region  Slot Types
	private sealed class ItemSlotSteamEngineFuel : ItemSlot
	{
		public ItemSlotSteamEngineFuel(InventoryBase inventory) : base(inventory) { }

		public static bool IsValidFuel(ItemStack fuelStack)
		{
			// Never treat liquid containers or liquid portions as fuel | This also prevents buckets/jugs etc being shoved into the fuel slot.
			if (fuelStack?.Collectible is BlockLiquidContainerBase) return false;
			if (BlockLiquidContainerBase.GetContainableProps(fuelStack) != null) return false;

			// Anything that burns is fuel babyyy
			var combustibleProperties = fuelStack?.Collectible?.CombustibleProps;
			return combustibleProperties != null && combustibleProperties.BurnTemperature > 0 && combustibleProperties.BurnDuration > 0f;
		}

		public override bool CanHold(ItemSlot sourceSlot) { return IsValidFuel(sourceSlot?.Itemstack); }

		// Most VS move operations (including GUI and ItemSlot.TryPutInto) check CanTakeFrom(), not CanHold(). This needs to be overwritten too.
		public override bool CanTakeFrom(ItemSlot sourceSlot, EnumMergePriority priority = EnumMergePriority.AutoMerge)
		{
			return IsValidFuel(sourceSlot?.Itemstack) && base.CanTakeFrom(sourceSlot, priority);
		}
	}

	private sealed class ItemSlotSteamEngineWater : ItemSlotLiquidOnly
	{
		public ItemSlotSteamEngineWater(InventoryBase inventory, float capacityLitres) : base(inventory, capacityLitres) { }

		// Liquid tanks are capacity-bound, not collectible stack-size-bound.
		public override int GetRemainingSlotSpace(ItemStack itemStack)
		{
			var pliquidPropertiesops = BlockLiquidContainerBase.GetContainableProps(itemStack); if (pliquidPropertiesops == null) return 0;

			int maximumItemCount = (int)Math.Floor(CapacityLitres * pliquidPropertiesops.ItemsPerLitre + 1e-5f);
			return Math.Max(0, maximumItemCount - StackSize);
		}
	}
	#endregion

	#region Misc
	private static int ClassifyLiquidKindByte(ItemStack liquidStack)
	{
		// 0 = none, 1 = water, 2 = other
		string liquidCodePath = liquidStack?.Collectible?.Code?.Path;
		if (string.IsNullOrEmpty(liquidCodePath)) return 0;
		if (liquidCodePath == "waterportion" || liquidCodePath == "boilingwaterportion") return 1;
		return 2;
	}

	public void ForceExtinguishBoiler()
	{
		var hostAPI = Host.API;
		if (hostAPI == null || hostAPI.Side != EnumAppSide.Server) return;

		// Stop burn immediately and suppress auto-ignition until manually re-ignited.
		FuelBurnTime = 0f;
		MaximumFuelBurnTime = 0f;
		FuelBurnTemperatureC = 0;
		FuelIgnitionEnabled = false;

		// Dump steam pressure instantly (also kills traction) | Not quite as clean as I'd like it but honestly good enough for release
		double targetTemperatureCelsius = AmbientTemperatureC;
		if (TemperatureC > targetTemperatureCelsius + 0.01) VentSteam(targetTemperatureCelsius);
		else TemperatureC = targetTemperatureCelsius;

		RequestForcedSync();
	}

	// Simulation layer helper, fast-forward fuel + working-fluid consumption.
	public void FastForwardUnloadedBurn(float seconds)
	{
		var hostAPI = Host.API;
		if (hostAPI == null || hostAPI.Side != EnumAppSide.Server) return;
		if (seconds <= 0.001f) return;

		BeginInventoryMutation();
		try
		{
			// Keep invariants consistent.
			if (WaterSlot.Empty) WorkingFluidRemainderLitres = 0;

			float remainingSeconds = seconds;
			int iterationGuard = 0;

			// We advance only burn timers + water consumption. No actual temperature simulation here.
			while (remainingSeconds > 0.0001f && iterationGuard++ < 100000)
			{
				if (!IsBurning) // If not currently burning, try to ignite the next fuel item (if allowed).
				{
					if (!FuelIgnitionEnabled || FuelSlot.Empty) break;
					if (!TryStartBurningFromFuelSlot()) break;
				}

				float deltaTime = FuelBurnTime > 0 ? MathF.Min(remainingSeconds, FuelBurnTime) : remainingSeconds;
				if (deltaTime <= 0.0001f) break;

				FuelBurnTime -= deltaTime;
				if (FuelBurnTime <= 0f)
				{
					FuelBurnTime = 0f;
					MaximumFuelBurnTime = 0f;
					FuelBurnTemperatureC = 0;
				}

				ConsumeWorkingFluid(deltaTime);
				remainingSeconds -= deltaTime;
			}

			if (WaterSlot.Empty) WorkingFluidRemainderLitres = 0;

			RequestForcedSync();
		}
		finally { EndInventoryMutation(); }
	}
	#endregion
}

/// Host contract for SteamEngineController
public interface ISteamEngineHost
{
	ICoreAPI API { get; }

	Vec3d WorldPosition { get; }			// For particle/audio use
	bool CanPlayerUse(IPlayer player);		// Perm/validity check
	bool ComputePowerEngaged();
	void RequestSync(bool force);
	void ExplodeAndRemove();
	void GetSteamEmissionBounds(Vec3d min, Vec3d max);
}

public static class SteamEnginePacketIds
{
	public const int Open  = 1000;
	public const int Close = 1001;

	public const int VentExcess					= 1337;
	public const int DumpFluid					= 1338;
	public const int SetTargetTemperature		= 1339;
	public const int IgniteFuel					= 1340;
	public const int CutFuel					= 1341;

	// Keyboard/remote lever controls (single byte payload)
	public const int SetDriveLeverPhase			= 1345;
	public const int SetTurnLeverPhase			= 1346;

}
