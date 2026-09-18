using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace YangTransport;

// Shared, reusable logic for any steam-engine-powered entity.
public sealed class SteamEngineEntityCommonClientUI
{
	private const int InventoryPacketIDOffset = 0;
	private const string SynchronizationTreeKey = "steamEngineSync";

	private readonly Entity PoweredEntity;
	private readonly SteamEngineController EngineController;
	private readonly global::System.Func<IPlayer, bool> CanPlayerUse;
	private readonly global::System.Func<string> DialogTitleProvider;
	private readonly global::System.Func<bool> ShowLocomotiveControlsProvider;

	private EngineControlUI? Dialog;
	private SyncedTreeAttribute? DialogTree;

	// We optionally delay UI compose until the first inventory update arrives, so the initial water gauge and derived texts can be correct from the get-go.
	private bool PendingOpen;
	private long PendingOpenCallbackID = -1;
	private IPlayer? PendingPlayer;

	public SteamEngineEntityCommonClientUI
	(
		Entity entity,
		SteamEngineController engineController,
		global::System.Func<IPlayer, bool> canPlayerUse,
		global::System.Func<string> dialogTitleProvider,
		global::System.Func<bool> showLocomotiveControlsProvider
	)
	{
		this.PoweredEntity = entity;
		this.EngineController = engineController;
		this.CanPlayerUse = canPlayerUse;
		this.DialogTitleProvider = dialogTitleProvider;
		this.ShowLocomotiveControlsProvider = showLocomotiveControlsProvider;
	}

	public bool IsOpen => Dialog != null || PendingOpen;

	public void Toggle(IPlayer player)
	{
		if (PoweredEntity.Api.Side != EnumAppSide.Client) return;
		var clientAPI = PoweredEntity.Api as ICoreClientAPI; if (clientAPI == null) return;

		if (Dialog != null) { Dialog.TryClose(); return; }

		if (PendingOpen) return;
		BeginOpen(player, clientAPI);
	}

	public void OnWatchedSyncChanged() // Call from the entity behavior's watched attribute modified listener.
	{
		if (PoweredEntity.Api.Side != EnumAppSide.Client) return;

		var synchronizationTree = PoweredEntity.WatchedAttributes.GetTreeAttribute(SynchronizationTreeKey);
		if (synchronizationTree == null) return;

		EngineController.FromTreeAttributes(synchronizationTree, PoweredEntity.Api.World);

		// Push new synced values into the GUI attribute tree. EngineControlUI listens to tree.OnModified and updates the UI.
		if (Dialog != null && DialogTree != null) { EngineController.WriteDialogValues(DialogTree); }
	}

	private void BeginOpen(IPlayer player, ICoreClientAPI clientAPI)
	{
		PendingOpen = true;
		PendingPlayer = player;

		// Prefer the first inventory update, but never leave the UI pending if it does not arrive.
		EngineController.Inventory.SlotModified -= OnInvSlotModified;
		EngineController.Inventory.SlotModified += OnInvSlotModified;
		PendingOpenCallbackID = clientAPI.Event.RegisterCallback(_ => { PendingOpenCallbackID = -1; if (PendingOpen) FinalizeOpen(); }, 100, permittedWhilePaused: true);

		// Register/open locally before asking the entity server-side to send its fresh inventory state.
		clientAPI.Network.SendPacketClient(clientAPI.World.Player.InventoryManager.OpenInventory(EngineController.Inventory));
		clientAPI.Network.SendEntityPacket(PoweredEntity.EntityId, SteamEnginePacketIds.Open, null);
	}

	private void OnInvSlotModified(int slotID) { if (PendingOpen) FinalizeOpen(); }

	private void FinalizeOpen()
	{
		if (!PendingOpen || PoweredEntity.Api is not ICoreClientAPI clientAPI) return;

		var player = PendingPlayer;
		ClearPendingOpen(clientAPI);

		if (player == null || !CanPlayerUse(player))
		{
			// BeginOpen already opened the inventory server-side. Undo that bookkeeping if the player moved out of range before the delayed compose completed.
			clientAPI.Network.SendEntityPacket(PoweredEntity.EntityId, SteamEnginePacketIds.Close, null);
			clientAPI.World.Player.InventoryManager.CloseInventoryAndSync(EngineController.Inventory);
			return;
		}

		OpenNow(player);
	}

	private void ClearPendingOpen(ICoreClientAPI clientAPI)
	{
		PendingOpen = false;
		PendingPlayer = null;
		EngineController.Inventory.SlotModified -= OnInvSlotModified;

		if (PendingOpenCallbackID < 0) return;
		clientAPI.Event.UnregisterCallback(PendingOpenCallbackID);
		PendingOpenCallbackID = -1;
	}

	public void Close()
	{
		if (PoweredEntity.Api is not ICoreClientAPI clientAPI) return;
		if (Dialog != null) { Dialog.TryClose(); return; }
		if (!PendingOpen) return;

		ClearPendingOpen(clientAPI);
		clientAPI.Network.SendEntityPacket(PoweredEntity.EntityId, SteamEnginePacketIds.Close, null);
		clientAPI.World.Player.InventoryManager.CloseInventoryAndSync(EngineController.Inventory);
	}

	private void OpenNow(IPlayer player)
	{
		if (PoweredEntity.Api.Side != EnumAppSide.Client) return;
		var clientAPI = PoweredEntity.Api as ICoreClientAPI; if (clientAPI == null) return;

		DialogTree = new SyncedTreeAttribute();
		EngineController.WriteDialogValues(DialogTree);

		global::System.Action<object> sendInventoryPacket = packet => clientAPI.Network.SendEntityPacketWithOffset(PoweredEntity.EntityId, InventoryPacketIDOffset, packet); 
		global::System.Action<int, byte[]?> sendControlPacket = (packetID, data) => clientAPI.Network.SendEntityPacket(PoweredEntity.EntityId, packetID, data);

		Dialog = new EngineControlUI
		(
			dialogTitle: DialogTitleProvider(),
			dialogID: PoweredEntity.EntityId.ToString(),
			inventory: EngineController.Inventory,
			attributes: DialogTree,
			clientAPI: clientAPI,
			sendInventoryPacket: sendInventoryPacket,
			sendControlPacket: sendControlPacket,
			showLocomotiveControls: ShowLocomotiveControlsProvider(),
			owningEntity: PoweredEntity
		);

		Dialog.OnClosed += () =>
		{
			// Best-effort, tell server to reduce sync rate + close inventory
			clientAPI.Network.SendEntityPacket(PoweredEntity.EntityId, SteamEnginePacketIds.Close, null);
			clientAPI.World.Player.InventoryManager.CloseInventoryAndSync(EngineController.Inventory);
			Dialog = null;
			DialogTree = null;
		};

		Dialog.TryOpen();
	}
}

// Shared in-world interaction logic for steam engine entity inventories (fuel + working fluid).
// Mirrors BlockSteamEngine/BlockEntitySteamEngine behaviours as closely as I can manage.
public static class SteamEngineEntityInteractions
{
	public static bool WouldHandleHeldInteractClient(Entity entity, SteamEngineController engineController, ItemSlot handSlot)
	{
		if (handSlot == null) return false;

		// Empty hand + Shift = take one fuel item (matches BlockSteamEngine QoL)
		if (handSlot.Empty)
		{
			var clientAPI = entity.Api as ICoreClientAPI;
			bool isShiftPressed = clientAPI?.World?.Player?.Entity?.Controls?.ShiftKey == true;
			return isShiftPressed && !engineController.Inventory[0].Empty;
		}

		// Liquid interface (bucket/jug/etc)
		if (handSlot.Itemstack?.Collectible is ILiquidInterface) { return WouldLiquidTransferLikeBarrel(engineController, handSlot); }

		// Portion stack -> tank (waterportion etc)
		if (BlockLiquidContainerBase.GetContainableProps(handSlot.Itemstack) != null) { return TankFreeLitres(engineController) > 1e-4f; }

		// Fuel
		return engineController.Inventory[0].CanTakeFrom(handSlot);
	}

	public static bool TryHandleHeldInteractServer(Entity entity, SteamEngineController engineController, EntityPlayer entityPlayer, ItemSlot handSlot)
	{
		if (entity.Api?.Side != EnumAppSide.Server) return false;
		if (entityPlayer == null || handSlot == null) return false;

		IPlayer? player = entity.World.PlayerByUid(entityPlayer.PlayerUID);
		if (player == null) return false;

		// Empty hand + Shift = take 1 fuel item (same as BlockSteamEngine)
		if (handSlot.Empty)
		{
			if (entityPlayer.Controls.ShiftKey) { return engineController.TryTakeFuelToPlayer(player, quantity: 1); }
			return false;
		}

		// 1) Barrel-like ILiquidSource/ILiquidSink handling (fill + empty with buckets/jugs/etc)
		if (TryHandleLiquidTransferLikeBarrel(entity, engineController, entityPlayer, handSlot, player)) return true;

		// If it is a liquid interface item, never treat it as fuel
		if (handSlot.Itemstack?.Collectible is ILiquidInterface) return false;

		// 2) Liquid portion stacks (waterportion etc) -> tank
		var liquidProperties = BlockLiquidContainerBase.GetContainableProps(handSlot.Itemstack);
		if (liquidProperties != null)
		{
			float litres = liquidProperties.ItemsPerLitre > 0 ? handSlot.StackSize / liquidProperties.ItemsPerLitre : 0f;
			int movedItemCount = TryPutLiquidIntoTank(entity.Api.World, engineController, handSlot.Itemstack, litres);
			if (movedItemCount > 0)
			{
				SteamEngineController.TakeOutAndNotify(handSlot, movedItemCount);
				return true;
			}
			return false;
		}

		// 3) Fuel (match block: Shift inserts 1, otherwise insert as much as possible)
		if (handSlot.Itemstack != null && engineController.IsValidFuel(handSlot.Itemstack))
		{
			int quantity = entityPlayer.Controls.ShiftKey ? 1 : handSlot.StackSize;
			return engineController.TryPutFuelFromPlayer(player, handSlot, quantity);
		}

		return false;
	}


	public static WorldInteraction[] GetBodyInteractionHelp(IClientWorldAccessor world, Entity entity, SteamEngineController engineController, IClientPlayer player)
	{
		List<WorldInteraction> interactions = new();

		interactions.Add(new WorldInteraction
		{
			ActionLangCode = "yangtransport:entityhelp-steamengine-open",
			MouseButton = EnumMouseButton.Right,
			ShouldApply = (_, _, _) => !WouldHandleHeldInteractClient(entity, engineController, player?.InventoryManager?.ActiveHotbarSlot)
		});

		bool hasFuel = !engineController.FuelSlot.Empty;
		if (!hasFuel)
		{
			ItemStack[] fuelStacks = GetFuelInteractionStacks(world.Api);
			interactions.Add(new WorldInteraction
			{
				ActionLangCode = "game:blockhelp-boiler-addfuel",
				MouseButton = EnumMouseButton.Right,
				Itemstacks = fuelStacks,
				GetMatchingStacks = (_, _, _) => GetMatchingHeldOrAdvertisedFuelStacks(player, engineController, fuelStacks)
			});
		}
		else
		{
			interactions.Add(new WorldInteraction
			{
				ActionLangCode = "yangtransport:entityhelp-steamengine-takefuel",
				MouseButton = EnumMouseButton.Right,
				HotKeyCode = "shift",
				RequireFreeHand = true,
				ShouldApply = (_, _, _) =>
				{
					ItemSlot slot = player?.InventoryManager?.ActiveHotbarSlot;
					return slot != null && slot.Empty;
				}
			});
		}

		ItemStack[] liquidContainerStacks = GetLiquidContainerInteractionStacks(world.Api);
		InteractionStacksDelegate matchingLiquidStacks = (_, _, _) => GetMatchingHeldOrAdvertisedLiquidStacks(player, liquidContainerStacks);

		bool hasWorkingFluid = !engineController.WaterSlot.Empty;
		if (!hasWorkingFluid)
		{
			interactions.Add(new WorldInteraction
			{
				ActionLangCode = "blockhelp-bucket-rightclick",
				MouseButton = EnumMouseButton.Right,
				Itemstacks = liquidContainerStacks,
				GetMatchingStacks = matchingLiquidStacks
			});
		}
		else
		{
			interactions.Add(new WorldInteraction
			{
				ActionLangCode = "blockhelp-bucket-rightclick-sneak",
				MouseButton = EnumMouseButton.Right,
				HotKeyCode = "shift",
				Itemstacks = liquidContainerStacks,
				GetMatchingStacks = matchingLiquidStacks
			});
		}

		return interactions.ToArray();
	}

	private static ItemStack[] GetLiquidContainerInteractionStacks(ICoreAPI coreAPI)
	{
		return ObjectCacheUtil.GetOrCreate(coreAPI, "yangtransport:steamEngineLiquidContainerStacks", () =>
		{
			List<ItemStack> liquidContainerStacks = new();

			foreach (CollectibleObject collectible in coreAPI.World.Collectibles)
			{
				if (collectible is BlockLiquidContainerBase { IsTopOpened: not false, AllowHeldLiquidTransfer: not false })
				{
					liquidContainerStacks.Add(new ItemStack(collectible));
				}
			}

			return liquidContainerStacks.ToArray();
		});
	}

	private static ItemStack[] GetFuelInteractionStacks(ICoreAPI coreAPI)
	{
		return ObjectCacheUtil.GetOrCreate(coreAPI, "yangtransport:steamEngineFuelStacks", () =>
		{
			List<ItemStack> fuelStacks = new();

			foreach (CollectibleObject collectible in coreAPI.World.Collectibles)
			{
				ItemStack fuelStack = new(collectible);
				if (IsSteamEngineFuelStack(fuelStack)) { fuelStacks.Add(fuelStack); }
			}

			return fuelStacks.ToArray();
		});
	}

	private static ItemStack[] GetMatchingHeldOrAdvertisedLiquidStacks(IClientPlayer player, ItemStack[] advertisedStacks)
	{
		ItemSlot activeHotbarSlot = player?.InventoryManager?.ActiveHotbarSlot;
		if (activeHotbarSlot == null || activeHotbarSlot.Empty) return advertisedStacks;

		return activeHotbarSlot.Itemstack?.Collectible is ILiquidInterface ? new[] { activeHotbarSlot.Itemstack } : Array.Empty<ItemStack>();
	}

	private static ItemStack[] GetMatchingHeldOrAdvertisedFuelStacks(IClientPlayer player, SteamEngineController engineController, ItemStack[] advertisedStacks)
	{
		ItemSlot activeHotbarSlot = player?.InventoryManager?.ActiveHotbarSlot;
		if (activeHotbarSlot == null || activeHotbarSlot.Empty) return advertisedStacks;

		return engineController.IsValidFuel(activeHotbarSlot.Itemstack) ? new[] { activeHotbarSlot.Itemstack } : Array.Empty<ItemStack>();
	}

	private static bool IsSteamEngineFuelStack(ItemStack fuelStack)
	{
		if (fuelStack?.Collectible is BlockLiquidContainerBase) return false;
		if (BlockLiquidContainerBase.GetContainableProps(fuelStack) != null) return false;

		CombustibleProperties combustibleProperties = fuelStack?.Collectible?.CombustibleProps;
		return combustibleProperties != null && combustibleProperties.BurnTemperature > 0 && combustibleProperties.BurnDuration > 0f;
	}

	private static bool TryHandleLiquidTransferLikeBarrel(Entity entity, SteamEngineController engineController, EntityPlayer entityPlayer, ItemSlot handSlot, IPlayer player)
	{
		var collectible = handSlot.Itemstack?.Collectible;
		if (collectible is not ILiquidInterface) return false;

		bool takeSinglePortion = entityPlayer.Controls.ShiftKey;	// barrel semantics
		bool putSinglePortion = entityPlayer.Controls.CtrlKey;		// barrel semantics

		// mirror BlockLiquidContainerBase: allow held item to consume the interaction if it wants
		var attributes = collectible.Attributes;
		if (attributes != null && attributes.IsTrue("handleLiquidContainerInteract"))
		{
			EnumHandHandling handHandling = EnumHandHandling.NotHandled;
			collectible.OnHeldInteractStart(handSlot, entityPlayer, null, null, true, ref handHandling);
			if (handHandling == EnumHandHandling.Handled || handHandling == EnumHandHandling.PreventDefaultAction) return true;
		}

		// Pour from held container into tank (ILiquidSource)
		if (collectible is ILiquidSource liquidSource && !takeSinglePortion && liquidSource.AllowHeldLiquidTransfer)
		{
			ItemStack? liquidContentStack = liquidSource.GetContent(handSlot.Itemstack);
			if (liquidContentStack != null && TankFreeLitres(engineController) > 1e-4f)
			{
				float litres = putSinglePortion ? liquidSource.TransferSizeLitres : liquidSource.CapacityLitres;
				int moved = TryPutLiquidIntoTank(entity.Api.World, engineController, liquidContentStack, litres);
				if (moved > 0)
				{
					if (collectible is BlockLiquidContainerBase blockLiquidContainer)
					{
						blockLiquidContainer.SplitStackAndPerformAction(entityPlayer, handSlot, stack
							=> { liquidSource.TryTakeContent(stack, moved); return moved; });
						
						blockLiquidContainer.DoLiquidMovedEffects(player, liquidContentStack, moved, BlockLiquidContainerBase.EnumLiquidDirection.Pour);
					}
					else { liquidSource.TryTakeContent(handSlot.Itemstack, moved); handSlot.MarkDirty(); }
					return true;
				}
			}
		}

		// Fill held container from tank (ILiquidSink)
		if (collectible is ILiquidSink liquidSink && !putSinglePortion && liquidSink.AllowHeldLiquidTransfer)
		{
			ItemStack tankStack = engineController.Inventory[1].Itemstack;
			if (tankStack != null && TankCurrentLitres(engineController) > 1e-4f)
			{
				ItemStack particlesStack = tankStack.Clone();
				float transferLitres = takeSinglePortion ? liquidSink.TransferSizeLitres : liquidSink.CapacityLitres;

				int containerStack;
				if (collectible is BlockLiquidContainerBase blockLiquidContainer)
				{
					containerStack = blockLiquidContainer.SplitStackAndPerformAction(entityPlayer, handSlot, stack
						=> liquidSink.TryPutLiquid(stack, tankStack, transferLitres));
				}
				else { containerStack = liquidSink.TryPutLiquid(handSlot.Itemstack, tankStack, transferLitres); handSlot.MarkDirty(); }

				if (containerStack > 0)
				{
					TakeLiquidFromTank(engineController, containerStack);
					if (collectible is BlockLiquidContainerBase effectLiquidContainer)
					{
						effectLiquidContainer.DoLiquidMovedEffects(player, particlesStack, containerStack, BlockLiquidContainerBase.EnumLiquidDirection.Fill);
					}
					return true;
				}
			}
		}

		return false;
	}

	private static bool WouldLiquidTransferLikeBarrel(SteamEngineController engineController, ItemSlot handSlot)
	{
		if (handSlot?.Itemstack?.Collectible is not BlockLiquidContainerBase liquidContainer) return false;

		float currentTankLitres = TankCurrentLitres(engineController);
		float freeTankLitres = TankFreeLitres(engineController);
		float currentHeldContainerLitres = liquidContainer.GetCurrentLitres(handSlot.Itemstack);
		float heldContainerCapacityLitres = liquidContainer.CapacityLitres;

		// Can draw or pour water
		if (currentHeldContainerLitres > 1e-4f && freeTankLitres > 1e-4f) return true;
		if (currentHeldContainerLitres < heldContainerCapacityLitres - 1e-4f && currentTankLitres > 1e-4f) return true;

		return false;
	}

	private static float TankCurrentLitres(SteamEngineController engineController)
	{
		return (float)engineController.GetWorkingFluidLitres();
	}

	private static float TankFreeLitres(SteamEngineController engineController)
	{
		var waterSlot = engineController.Inventory[1] as ItemSlotLiquidOnly;
		if (waterSlot == null) return 0f;
		return waterSlot.CapacityLitres - TankCurrentLitres(engineController);
	}

	private static int TryPutLiquidIntoTank(IWorldAccessor world, SteamEngineController engineController, ItemStack liquidStack, float litres)
	{
		var liquidProperties = BlockLiquidContainerBase.GetContainableProps(liquidStack);
		if (liquidProperties == null || liquidProperties.ItemsPerLitre <= 0) return 0;

		var waterSlot = engineController.Inventory[1];
		int desiredItemCount = Math.Min(liquidStack.StackSize, (int)Math.Round(litres * liquidProperties.ItemsPerLitre));
		int movedItemCount = Math.Min(desiredItemCount, waterSlot.GetRemainingSlotSpace(liquidStack));
		if (movedItemCount <= 0) return 0;

		if (waterSlot.Empty)
		{
			waterSlot.Itemstack = liquidStack.Clone();
			waterSlot.Itemstack.StackSize = movedItemCount;
		}
		else
		{
			if (!waterSlot.Itemstack.Equals(world, liquidStack, GlobalConstants.IgnoredStackAttributes)) return 0;
			waterSlot.Itemstack.StackSize += movedItemCount;
		}

		waterSlot.MarkDirty();
		return movedItemCount;
	}

	private static void TakeLiquidFromTank(SteamEngineController engineController, int movedItemCount)
	{
		var waterSlot = engineController.Inventory[1];
		SteamEngineController.TakeOutAndNotify(waterSlot, movedItemCount);
	}
}
