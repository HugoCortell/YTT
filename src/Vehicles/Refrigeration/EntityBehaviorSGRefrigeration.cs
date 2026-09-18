using System;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace YangTransport;

public sealed class EntityBehaviorSGRefrigeration : EntityBehavior
{
	internal const int InventoryPacketOffset = 10;
	internal const string InventoryTreeAttribute = "sgrefrigerationInv";

	private const string CurrentUnitSecondsAttribute = "sgrefrigerationCurrentSeconds";
	private const string CapacitySecondsAttribute = "sgrefrigerationCapacitySeconds";
	private const double CoolingServiceIntervalSeconds = 1.0;
	private const double CargoTransitionIntervalSeconds = 45.0;
	internal const double TimeEpsilonSeconds = 1e-6;

	private InventoryGeneric RefrigerantInventory;
	private EntityBehaviorSGStorage Storage;
	private double CurrentUnitSecondsRemaining;
	private double CoolingServiceAccumulator;
	private double CargoTransitionAccumulator;
	private float PerishMultiplier = 0.4f;
	private int RefrigerantSlotCount = 4;
	private bool SuppressInventoryModified;
	private bool ForceRefrigeratedTransition;
	private bool TransitionDelegateAttached;

	private double ClientCapacitySeconds;
	private long ClientCapacitySyncMS;

	internal InventoryGeneric Inventory => RefrigerantInventory;
	internal float OfflineSimPerishMultiplier => PerishMultiplier;
	internal bool HasContentsForPickup => CurrentUnitSecondsRemaining > TimeEpsilonSeconds || (RefrigerantInventory != null && !RefrigerantInventory.Empty);

	public EntityBehaviorSGRefrigeration(Entity entity) : base(entity) { }

	public override string PropertyName() => "SGRefrigeration";

	public override void Initialize(EntityProperties properties, JsonObject typeAttributes)
	{
		base.Initialize(properties, typeAttributes);

		JsonObject configurationRoot = typeAttributes != null && typeAttributes.Exists &&
			typeAttributes["SGRefrigeration"].Exists ? typeAttributes : properties.Attributes;
		JsonObject configuration = configurationRoot?["SGRefrigeration"];

		if (configuration != null && configuration.Exists)
		{
			PerishMultiplier = Math.Max(0f, configuration["PerishMultiplier"].AsFloat(PerishMultiplier));
			RefrigerantSlotCount = Math.Clamp(configuration["RefrigerantSlots"].AsInt(RefrigerantSlotCount), 1, 64);
		}

		RefrigerantInventory = new InventoryGeneric
		(
			RefrigerantSlotCount,
			"sgrefrigeration-" + entity.EntityId,
			entity.Api,
			(slotID, inventory) => new ItemSlotRefrigerant(inventory)
		);

		LoadState();

		Storage = entity.GetBehavior<EntityBehaviorSGStorage>();

		if (entity.World.Side == EnumAppSide.Server) { RefrigerantInventory.SlotModified += OnRefrigerantSlotModified; }
		else
		{
			entity.WatchedAttributes.RegisterModifiedListener(InventoryTreeAttribute, LoadInventory);
			entity.WatchedAttributes.RegisterModifiedListener(CapacitySecondsAttribute, RefreshClientCapacityBaseline);
			RefreshClientCapacityBaseline();
		}
	}

	public override void AfterInitialized(bool onFirstSpawn)
	{
		base.AfterInitialized(onFirstSpawn);

		Storage ??= entity.GetBehavior<EntityBehaviorSGStorage>();
		if (Storage != null && !TransitionDelegateAttached)
		{
			Storage.AddTransitionSpeedDelegate(GetTransitionSpeedMultiplier);
			TransitionDelegateAttached = true;
		}

		if (entity.World.Side != EnumAppSide.Server) return;

		if (!IsServerRefrigerated && HasQueuedRefrigerant())
		{
			Storage?.ServerUpdateTransitions();
			TryStartNextUnit();
			StoreState(syncClient: true);
		}
		else { StoreState(syncClient: true); }
	}

	public override void OnEntityDespawn(EntityDespawnData despawn)
	{
		if (TransitionDelegateAttached)
		{
			Storage?.RemoveTransitionSpeedDelegate(GetTransitionSpeedMultiplier);
			TransitionDelegateAttached = false;
		}
		if (entity.World.Side == EnumAppSide.Server && RefrigerantInventory != null) { RefrigerantInventory.SlotModified -= OnRefrigerantSlotModified; }

		base.OnEntityDespawn(despawn);
	}

	public override void FromBytes(bool isSync)
	{
		LoadState();
		if (entity.World.Side == EnumAppSide.Client) RefreshClientCapacityBaseline();
	}

	public override void ToBytes(bool forClient)
	{
		StoreState(syncClient: false);
	}

	public override void OnGameTick(float deltaTime)
	{
		if (entity.World.Side != EnumAppSide.Server || RefrigerantInventory == null) return;

		if (!IsServerRefrigerated)
		{
			CoolingServiceAccumulator = 0;
			CargoTransitionAccumulator = 0;
			return;
		}

		double deltaSeconds = Math.Max(0, deltaTime);
		CoolingServiceAccumulator += deltaSeconds;

		if (CoolingServiceAccumulator >= CoolingServiceIntervalSeconds)
		{
			double elapsedSeconds = CoolingServiceAccumulator;
			CoolingServiceAccumulator = 0;
			AdvanceCoolingLoaded(elapsedSeconds);
		}

		// AdvanceCoolingLoaded() commits the transition boundary if the last refrigerant unit expires.
		if (!IsServerRefrigerated) { CargoTransitionAccumulator = 0; return; }

		CargoTransitionAccumulator += deltaSeconds;
		if (CargoTransitionAccumulator >= CargoTransitionIntervalSeconds)
		{
			CargoTransitionAccumulator %= CargoTransitionIntervalSeconds;
			Storage?.ServerUpdateTransitions();
		}
	}

	public override void OnReceivedClientPacket(IServerPlayer player, int packetID, byte[] data, ref EnumHandling handled)
	{
		if (packetID < InventoryPacketOffset + 7 || packetID > InventoryPacketOffset + 9) return;
		if (RefrigerantInventory == null || !RefrigerantInventory.HasOpened(player)) return;

		RefrigerantInventory.InvNetworkUtil.HandleClientPacket(player, packetID - InventoryPacketOffset, data);
		handled = EnumHandling.PreventSubsequent;
	}

	public override void GetInfoText(StringBuilder infotext)
	{
		base.GetInfoText(infotext);
		infotext.AppendLine(GetCapacityStatusText());

		bool isRefrigerated = entity.World.Side == EnumAppSide.Server
			? IsServerRefrigerated
			: GetDisplayCapacitySeconds() > TimeEpsilonSeconds;
		if (isRefrigerated) infotext.AppendLine(Lang.Get("yangtransport:refrigeration-perish-speed-multiplier", FormatMultiplier(PerishMultiplier)));
	}

	internal void OfflineSimPrepareForCapture()
	{
		if (entity.World.Side != EnumAppSide.Server) return;

		if (CoolingServiceAccumulator > TimeEpsilonSeconds)
		{
			double elapsedSeconds = CoolingServiceAccumulator;
			CoolingServiceAccumulator = 0;
			AdvanceCoolingLoaded(elapsedSeconds);
		}

		Storage?.ServerUpdateTransitions();
		Storage?.StoreInventoriesForSnapshot();
		StoreState(syncClient: false);
	}

	internal double OfflineSimEstimateCapacitySecondsRemaining() { return ComputeTotalCapacitySeconds(); }

	internal void OfflineSimFastForward(double elapsedSeconds, bool refrigerationBoundaryApplied)
	{
		if (entity.World.Side != EnumAppSide.Server) return;

		elapsedSeconds = Math.Max(0, elapsedSeconds);
		bool wasRefrigerated = IsServerRefrigerated || HasQueuedRefrigerant();

		AdvanceCoolingStateOnly(elapsedSeconds);

		if (wasRefrigerated && !IsServerRefrigerated && !HasQueuedRefrigerant() && !refrigerationBoundaryApplied)
		{
			// Fallback for an unexpectedly missed virtual boundary.
			ForceRefrigeratedTransition = true;
			Storage?.ServerUpdateTransitions();
			ForceRefrigeratedTransition = false;
		}

		Storage?.ServerUpdateTransitions();
		StoreState(syncClient: true);
	}

	internal string GetCapacityStatusText()
	{
		double capacitySeconds = GetDisplayCapacitySeconds();
		if (capacitySeconds <= TimeEpsilonSeconds) return Lang.Get("yangtransport:refrigeration-unrefrigerated");

		return Lang.Get("yangtransport:refrigeration-capacity-left", FormatDuration(capacitySeconds));
	}

	private float GetTransitionSpeedMultiplier(EnumTransitionType transitionType, ItemStack stack, float configuredMultiplier)
	{
		if (transitionType != EnumTransitionType.Perish) return 1f;

		return (ForceRefrigeratedTransition || (entity.World.Side == EnumAppSide.Server ? IsServerRefrigerated : GetDisplayCapacitySeconds() > TimeEpsilonSeconds)) ? PerishMultiplier : 1f;
	}

	private void OnRefrigerantSlotModified(int slotID)
	{
		if (SuppressInventoryModified || entity.World.Side != EnumAppSide.Server) return;

		if (!IsServerRefrigerated && HasQueuedRefrigerant())
		{
			Storage?.ServerUpdateTransitions();
			TryStartNextUnit();
		}

		StoreState(syncClient: true);
	}

	private void AdvanceCoolingLoaded(double elapsedSeconds)
	{
		if (elapsedSeconds <= TimeEpsilonSeconds) return;

		bool wasRefrigerated = IsServerRefrigerated;
		bool inventoryChanged = AdvanceCoolingStateOnly(elapsedSeconds);

		if (wasRefrigerated && !IsServerRefrigerated && !HasQueuedRefrigerant())
		{
			ForceRefrigeratedTransition = true;
			Storage?.ServerUpdateTransitions();
			ForceRefrigeratedTransition = false;
		}

		if (inventoryChanged || wasRefrigerated != IsServerRefrigerated) { StoreState(syncClient: true); }
	}

	private bool AdvanceCoolingStateOnly(double elapsedSeconds)
	{
		double remainingSeconds = Math.Max(0, elapsedSeconds);
		bool inventoryChanged = false;

		while (remainingSeconds > TimeEpsilonSeconds)
		{
			if (CurrentUnitSecondsRemaining <= TimeEpsilonSeconds)
			{
				if (!TryStartNextUnit()) { CurrentUnitSecondsRemaining = 0; break; }
				inventoryChanged = true;
			}

			if (remainingSeconds + TimeEpsilonSeconds < CurrentUnitSecondsRemaining)
			{
				CurrentUnitSecondsRemaining -= remainingSeconds;
				remainingSeconds = 0;
				break;
			}

			remainingSeconds -= CurrentUnitSecondsRemaining;
			CurrentUnitSecondsRemaining = 0;

			if (remainingSeconds <= TimeEpsilonSeconds) break;

			if (!TryStartNextUnit()) break;
			inventoryChanged = true;
		}

		if (CurrentUnitSecondsRemaining <= TimeEpsilonSeconds)
		{
			CurrentUnitSecondsRemaining = 0;
			if (TryStartNextUnit()) inventoryChanged = true;
		}

		return inventoryChanged;
	}

	private bool TryStartNextUnit()
	{
		if (RefrigerantInventory == null || CurrentUnitSecondsRemaining > TimeEpsilonSeconds) return CurrentUnitSecondsRemaining > TimeEpsilonSeconds;

		for (int slotIndex = 0; slotIndex < RefrigerantInventory.Count; slotIndex++)
		{
			ItemSlot slot = RefrigerantInventory[slotIndex];
			if (slot.Empty || !RefrigerantCatalog.TryGetDuration(slot.Itemstack, out int durationSeconds) || durationSeconds <= 0) continue;

			SuppressInventoryModified = true;
			try
			{
				int previousStackSize = slot.StackSize;
				slot.TakeOut(1);
				if (previousStackSize > 1) slot.MarkDirty();
			}
			finally { SuppressInventoryModified = false; }

			CurrentUnitSecondsRemaining = durationSeconds;
			return true;
		}

		return false;
	}

	private bool HasQueuedRefrigerant()
	{
		if (RefrigerantInventory == null) return false;

		for (int slotIndex = 0; slotIndex < RefrigerantInventory.Count; slotIndex++)
		{
			ItemSlot slot = RefrigerantInventory[slotIndex];
			if (!slot.Empty && RefrigerantCatalog.TryGetDuration(slot.Itemstack, out int durationSeconds) && durationSeconds > 0) return true;
		}

		return false;
	}

	private double ComputeTotalCapacitySeconds()
	{
		double seconds = Math.Max(0, CurrentUnitSecondsRemaining);
		if (RefrigerantInventory == null) return seconds;

		for (int slotIndex = 0; slotIndex < RefrigerantInventory.Count; slotIndex++)
		{
			ItemSlot slot = RefrigerantInventory[slotIndex];
			if (slot.Empty || !RefrigerantCatalog.TryGetDuration(slot.Itemstack, out int durationSeconds)) continue;
			seconds += (double)Math.Max(0, durationSeconds) * slot.StackSize;
		}

		return seconds;
	}

	private bool IsServerRefrigerated => CurrentUnitSecondsRemaining > TimeEpsilonSeconds;

	private double GetDisplayCapacitySeconds()
	{
		if (entity.World.Side == EnumAppSide.Server) return ComputeTotalCapacitySeconds();

		double elapsedSeconds = Math.Max(0, entity.World.ElapsedMilliseconds - ClientCapacitySyncMS) / 1000.0;
		return Math.Max(0, ClientCapacitySeconds - elapsedSeconds);
	}

	private void LoadState()
	{
		LoadInventory();
		CurrentUnitSecondsRemaining = Math.Max(0, entity.WatchedAttributes.GetDouble(CurrentUnitSecondsAttribute, 0));
	}

	private void LoadInventory()
	{
		if (RefrigerantInventory == null) return;
		if (entity.WatchedAttributes[InventoryTreeAttribute] is TreeAttribute inventoryTree) { RefrigerantInventory.FromTreeAttributes(inventoryTree); }
	}

	private void StoreState(bool syncClient)
	{
		if (RefrigerantInventory == null) return;

		var inventoryTree = new TreeAttribute();
		RefrigerantInventory.ToTreeAttributes(inventoryTree);

		entity.WatchedAttributes[InventoryTreeAttribute] = inventoryTree;
		entity.WatchedAttributes.SetDouble(CurrentUnitSecondsAttribute, Math.Max(0, CurrentUnitSecondsRemaining));
		entity.WatchedAttributes.SetDouble(CapacitySecondsAttribute, ComputeTotalCapacitySeconds());

		if (syncClient)
		{
			entity.WatchedAttributes.MarkPathDirty(InventoryTreeAttribute);
			entity.WatchedAttributes.MarkPathDirty(CurrentUnitSecondsAttribute);
			entity.WatchedAttributes.MarkPathDirty(CapacitySecondsAttribute);
		}

		if (entity.World.Side == EnumAppSide.Server)
		{
			entity.World.BlockAccessor.GetChunkAtBlockPos(entity.Pos.AsBlockPos)?.MarkModified();
		}
	}

	private void RefreshClientCapacityBaseline()
	{
		ClientCapacitySeconds = Math.Max(0, entity.WatchedAttributes.GetDouble(CapacitySecondsAttribute, 0));
		ClientCapacitySyncMS = entity.World?.ElapsedMilliseconds ?? 0;
	}

	private static string FormatDuration(double seconds)
	{
		long totalSeconds = Math.Max(0, (long)Math.Ceiling(seconds));
		long hours = totalSeconds / 3600;
		long minutes = totalSeconds / 60 % 60;
		long remainingSeconds = totalSeconds % 60;

		return hours > 0 ? $"{hours}:{minutes:00}:{remainingSeconds:00}" : $"{minutes}:{remainingSeconds:00}";
	}

	private static string FormatMultiplier(double multiplier) { return multiplier.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); }
}

// Eventually the refrigeration code ought to be moved to a more generalized location once more vehicles/systems have use for it.
public static class RefrigerantCatalog
{
	private static readonly Dictionary<AssetLocation, int> Fallback = new()
	{
		[new("game:lakeice")] = 40,
		[new("game:glacierice")] = 120,
		[new("game:ice-glacier")] = 120,
		[new("game:packedglacierice")] = 120,
		[new("game:ice-packedglacier")] = 120
	};

	private static Dictionary<AssetLocation, int> DurationByCode = Fallback;

	public static void Compile(ICoreAPI coreAPI)
	{
		if (coreAPI == null) throw new ArgumentNullException(nameof(coreAPI));

		try
		{
			Dictionary<string, int>? rawConfiguration = coreAPI.Assets.TryGet(new AssetLocation("yangtransport:config/refrigerants.json"))?.ToObject<Dictionary<string, int>>();
			if (rawConfiguration == null || rawConfiguration.Count == 0) throw new InvalidOperationException("refrigerants.json is missing or empty.");

			Dictionary<AssetLocation, int> compiled = new();
			foreach (KeyValuePair<string, int> pair in rawConfiguration)
			{
				string code = (pair.Key ?? "").Trim();
				AssetLocation? location = code.Length > 0 ? new(code) : null;
				if (location == null || !location.Valid || location.IsWildCard || pair.Value <= 0 || !compiled.TryAdd(location, pair.Value))
				{
					coreAPI.Logger.Warning($"[YangTransport] Ignoring invalid or duplicate refrigerant '{code}' ({pair.Value}s).");
				}
			}

			if (compiled.Count == 0) throw new InvalidOperationException("refrigerants.json contains no valid refrigerants.");
			DurationByCode = compiled;
		}
		catch (Exception exception)
		{
			coreAPI.Logger.Error("[YangTransport] Failed to load refrigerant config. Falling back on defaults.");
			coreAPI.Logger.Error(exception);
			DurationByCode = Fallback;
		}
	}

	public static bool TryGetDuration(ItemStack itemStack, out int durationSeconds)
	{
		durationSeconds = 0;
		AssetLocation? code = itemStack?.Collectible?.Code;
		return code != null && DurationByCode.TryGetValue(code, out durationSeconds);
	}
}

public sealed class ItemSlotRefrigerant : ItemSlot
{
	public ItemSlotRefrigerant(InventoryBase inventory) : base(inventory) { }

	public override bool CanTakeFrom(ItemSlot sourceSlot, EnumMergePriority priority = EnumMergePriority.AutoMerge)
	{
		if (inventory?.PutLocked == true) return false;

		ItemStack sourceStack = sourceSlot?.Itemstack;
		if (!RefrigerantCatalog.TryGetDuration(sourceStack, out _)) return false;

		if (itemstack == null) return GetRemainingSlotSpace(sourceStack) > 0;

		return itemstack.Collectible.GetMergableQuantity(itemstack, sourceStack, priority) > 0 && GetRemainingSlotSpace(sourceStack) > 0;
	}

	public override bool CanHold(ItemSlot sourceSlot) { return CanTakeFrom(sourceSlot); }
}