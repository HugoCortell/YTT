using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace YangTransport;

// Automation controller item. Can be attached to engines (haulage, locomotive, etc). Not yet placable as a block.
public sealed class ItemConductorLocust : Item, IAttachedInteractions
{
	public const string CategoryCode = ConductorLocustPlacement.CategoryCode;

	public override void OnHeldInteractStart
	(
		ItemSlot heldItemSlot, EntityAgent byEntity, BlockSelection blockSelection,
		EntitySelection entitySelection, bool firstEvent, ref EnumHandHandling handHandling
	)
	{
		// Let entity interactions handle minecarts/locomotives.
		if (entitySelection != null) return;

		// But do not silently allow "placing" on terrain.
		if (blockSelection != null)
		{
			if (byEntity.World.Side == EnumAppSide.Server && byEntity is EntityPlayer entityPlayer)
			{
				if (byEntity.World.PlayerByUid(entityPlayer.PlayerUID) is IServerPlayer serverPlayer)
				{
					serverPlayer.SendIngameError("yangtransport:conductorlocust-cannot-place-ground");
				}
			}

			handHandling = EnumHandHandling.PreventDefault;
		}
	}

	private static void ServerOpenTimetable(Entity onEntity, EntityAgent byEntity)
	{
		if (onEntity.Api?.Side != EnumAppSide.Server) return;
		if (byEntity is not EntityPlayer entityPlayer || onEntity.Api.World.PlayerByUid(entityPlayer.PlayerUID) is not IServerPlayer serverPlayer) return;

		ConductorTimetableSystem? timetable = onEntity.Api.ModLoader.GetModSystem<ConductorTimetableSystem>();
		if (timetable != null) { timetable.OpenTimetable(serverPlayer, onEntity); return; }

		serverPlayer.SendMessage(0, Lang.Get("yangtransport:conductorlocust-ui-stub"), EnumChatType.Notification);
	}

	public bool OnTryAttach(ItemSlot itemSlot, int slotIndex, Entity toEntity)
	{
		if (toEntity is EntityMinecart minecart) { return minecart.CanAcceptConductorLocust(out _); }
		if (toEntity is EntityStandardGaugeLocomotive locomotive) { return locomotive.CanAcceptConductorLocust(out _); }

		return false;
	}

	public bool OnTryDetach(ItemSlot itemSlot, int slotIndex, Entity toEntity) => true;

	public void OnInteract
	(
		ItemSlot itemSlot, int slotIndex, Entity onEntity, EntityAgent byEntity,
		Vec3d hitPosition, EnumInteractMode mode, ref EnumHandling handled, System.Action onRequireSave
	)
	{
		if (mode != EnumInteractMode.Interact) { return; }
		if (byEntity.Controls.CtrlKey) { handled = EnumHandling.PassThrough; return; }

		handled = EnumHandling.PreventDefault;
		ServerOpenTimetable(onEntity, byEntity);
	}

	public void OnAttached(ItemSlot itemSlot, int slotIndex, Entity toEntity, EntityAgent byEntity)
	{
		if (toEntity.Api?.Side != EnumAppSide.Server) return;
		toEntity.Api.ModLoader.GetModSystem<RailConvoySystem>()?.NotifyConductorLocustInstalled(toEntity);
	}

	public void OnDetached(ItemSlot itemSlot, int slotIndex, Entity fromEntity, EntityAgent byEntity)
	{
		if (fromEntity.Api?.Side != EnumAppSide.Server) return;
		fromEntity.Api.ModLoader.GetModSystem<RailConvoySystem>()?.NotifyConductorLocustRemoved(fromEntity);
	}

	public void OnEntityDespawn(ItemSlot itemSlot, int slotIndex, Entity onEntity, EntityDespawnData despawnData) { }

	public void OnEntityDeath(ItemSlot itemSlot, int slotIndex, Entity onEntity, DamageSource damageSourceForDeath) { }

	public void OnReceivedClientPacket
	(
		ItemSlot itemSlot, int slotIndex, Entity onEntity, IServerPlayer player,
		int packetID, byte[] data, ref EnumHandling handled, System.Action onRequireSave
	) {} // Looks stupid but its required and it works
}

/// Shared locust install/check helpers. Keeping mount rules here so the item, manual entity interaction, and attachable callbacks cannot drift.
internal static class ConductorLocustPlacement
{
	public const string CategoryCode = "conductorlocust";
	public const string MinecartAttachmentPointCode = "storage";
	public const string StandardGaugeAttachmentPointCode = "SEAT_MAIN_AP";

	public static bool IsConductorLocust(ItemSlot? slot) { return slot?.Itemstack?.Collectible is ItemConductorLocust; }

	public static bool HasInstalledConductorLocust(EntityBehaviorAttachable? attachable, string attachmentPointCode)
	{
		return IsConductorLocust(attachable?.GetSlotConfigFromAPName(attachmentPointCode));
	}

	public static bool IsStandardGaugeConductorSeat(string? selectionBoxCode)
	{
		return string.Equals(selectionBoxCode, StandardGaugeAttachmentPointCode, System.StringComparison.OrdinalIgnoreCase);
	}

	public static bool CanInstallOnMinecart(EntityBehaviorAttachable? attachable, bool isEngineCart, out string? errorCode)
	{
		errorCode = null;
		if (isEngineCart) { errorCode = "yangtransport:conductorlocust-cannot-place-ground"; return false; }

		return CanInstallInSlot(attachable, MinecartAttachmentPointCode, out errorCode);
	}

	public static bool CanInstallOnStandardGauge(EntityBehaviorAttachable? attachable, EntityBehaviorSeatable? seatable, out string? errorCode)
	{
		errorCode = null;
		if (RailwayVehicleShared.HasMountedPassenger(seatable)) { errorCode = "yangtransport:locomotive-occupied"; return false; }

		return CanInstallInSlot(attachable, StandardGaugeAttachmentPointCode, out errorCode);
	}

	private static bool CanInstallInSlot(EntityBehaviorAttachable? attachable, string attachmentPointCode, out string? errorCode)
	{
		errorCode = null;

		ItemSlot? slot = attachable?.GetSlotConfigFromAPName(attachmentPointCode);
		if (slot == null) { errorCode = "yangtransport:conductorlocust-cannot-place-ground"; return false; }
		if (!slot.Empty) { errorCode = "yangtransport:locomotive-occupied"; return false; }

		return true;
	}

	public static bool ServerTryInstallIntoAttachableSlot
	(
		Entity target, EntityAgent byEntity, ItemSlot sourceSlot, EntityBehaviorAttachable? attachable,
		string attachmentPointCode, string? validationErrorCode
	)
	{
		if (target.Api?.Side != EnumAppSide.Server) return true;
		if (byEntity is not EntityPlayer entityPlayer || target.Api.World.PlayerByUid(entityPlayer.PlayerUID) is not IServerPlayer serverPlayer) return false;

		if (validationErrorCode != null) { serverPlayer.SendIngameError(validationErrorCode); return false; }
		if (sourceSlot?.Itemstack?.Collectible is not ItemConductorLocust) { return false; }

		ItemSlot? targetSlot = attachable?.GetSlotConfigFromAPName(attachmentPointCode);
		if (targetSlot == null) { serverPlayer.SendIngameError("yangtransport:conductorlocust-cannot-place-ground"); return false; }
		if (!targetSlot.Empty) { serverPlayer.SendIngameError("yangtransport:locomotive-occupied"); return false; }

		int slotIndex = 0;
		InventoryBase inventory = attachable!.Inventory;
		for (int inventorySlotIndex = 0; inventorySlotIndex < inventory.Count; inventorySlotIndex++) { if (ReferenceEquals(inventory[inventorySlotIndex], targetSlot)) { slotIndex = inventorySlotIndex; break; } }

		var attachedItemStack = sourceSlot.Itemstack.Clone();
		attachedItemStack.StackSize = 1;

		var attachedInteractions = attachedItemStack.Collectible.GetCollectibleInterface<IAttachedInteractions>();
		if (attachedInteractions != null && !attachedInteractions.OnTryAttach(sourceSlot, slotIndex, target)) { serverPlayer.SendIngameError("yangtransport:conductorlocust-cannot-place-ground"); return false; }

		targetSlot.Itemstack = attachedItemStack;
		attachedItemStack.Collectible.GetCollectibleInterface<IAttachedListener>()?.OnAttached(targetSlot, slotIndex, target, byEntity);

		if (serverPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative) { sourceSlot.TakeOut(1); sourceSlot.MarkDirty(); }

		targetSlot.MarkDirty();
		attachable.storeInv();
		target.MarkShapeModified();

		return true;
	}
}
