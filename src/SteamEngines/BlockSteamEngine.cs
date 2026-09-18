using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.GameContent;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace YangTransport;

/// Steam engines! Simple portable ones that directly turn heat into energy!! (So not a real steam engine lol)
public sealed class BlockSteamEngine : BlockLiquidContainerBase, IIgnitable, IMechanicalPowerBlock
{
	public override int ContainerSlotId => 1; // Slot 1 in the BE inventory is the "liquid content" slot.

	public override bool AllowHeldLiquidTransfer => true;
	public override bool IsTopOpened => true;

	private ICoreClientAPI? ClientAPI;
	private readonly Matrixf ModelMatrix = new();

	private BlockFacing PowerOutputFacing = BlockFacing.SOUTH;

	public override void OnLoaded(ICoreAPI coreAPI)
	{
		base.OnLoaded(coreAPI);
		ClientAPI = coreAPI as ICoreClientAPI; // null on server
		
		string variantSide = Variant?["side"];
		if (variantSide != null) { PowerOutputFacing = BlockFacing.FromCode(variantSide).Opposite; }
	}

	public override void GetHeldItemInfo(ItemSlot inventorySlot, StringBuilder description, IWorldAccessor world, bool withDebugInformation)
	{
		base.GetHeldItemInfo(inventorySlot, description, world, withDebugInformation);
		SteamEngineHandbookInfo.AppendStationarySteamEngineInfo(description, this);
	}

	public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos position, IPlayer forPlayer)
	{
		StringBuilder descriptionBuilder = new(base.GetPlacedBlockInfo(world, position, forPlayer));
		if (world.BlockAccessor.GetBlockEntity(position) is BlockEntitySteamEngine blockEntity) { blockEntity.GetBlockInfo(forPlayer, descriptionBuilder); }
		return descriptionBuilder.ToString();
	}


	public override bool CanPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSelection, ref string failureCode)
	{
		if (!base.CanPlaceBlock(world, byPlayer, blockSelection, ref failureCode)) return false;
		return CollisionFlowFieldPlacement.CanPlace(api, world.BlockAccessor, blockSelection.Position, this, ref failureCode);
	}

	public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos position) // This is what clears the inventory on placement
	{
	    ItemStack stack = base.OnPickBlock(world, position);
	    SetContents(stack, null);
	    return stack;
	}

	public override void OnHeldRenderOit(ItemSlot inventorySlot, IClientPlayer byPlayer)
	{
		if (ClientAPI == null) return;

		BlockSelection? rawSelection = byPlayer.CurrentBlockSelection;
		if (rawSelection == null) return;

		BlockSelection placementSelection = HeldGhostPreview.MakePlacementSelection(rawSelection, byPlayer, this);

		// Same orientation resolver as BlockBehaviorHorizontalOrientable
		string variantCode = Variant != null && Variant.ContainsKey("horizontalorientation") ? "horizontalorientation" : "side";
		BlockFacing[] suggestedOrientations = Block.SuggestedHVOrientation(byPlayer, placementSelection);

		Block placedBlock = ClientAPI.World.GetBlock(CodeWithVariant(variantCode, suggestedOrientations[0].Code)) ?? this;

		string failureCode = "";
		bool canPlaceHere = placedBlock.CanPlaceBlock(ClientAPI.World, byPlayer, placementSelection, ref failureCode);

		HeldGhostPreview.RenderOit(ClientAPI, byPlayer, placementSelection.Position, placedBlock, canPlaceHere, ModelMatrix);
	}

	// BlockLiquidContainerBase consumes right-click in-hand (held liquid transfer), which prevents placement unless the player crouches.
	public override void OnHeldInteractStart(ItemSlot itemSlot, EntityAgent byEntity, BlockSelection blockSelection, EntitySelection entitySelection, bool firstEvent, ref EnumHandHandling handHandling)
	{
		// Do nothing. Leave handHandling as NotHandled so placement proceeds.
	}

	#region Fuel Interactions
	public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSelection)
	{
		if (blockSelection?.Position == null) return false;
		if (!world.Claims.TryAccess(byPlayer, blockSelection.Position, EnumBlockAccessFlags.Use)) { return false; }

		// 1. Let vanilla liquid transfer happen (fill/empty with container).
		if (base.OnBlockInteractStart(world, byPlayer, blockSelection)) { return true; }

		// 2) Fuel handling.
		if (world.BlockAccessor.GetBlockEntity(blockSelection.Position) is not BlockEntitySteamEngine blockEntity) { return false; }

		ItemSlot hotbarSlot = byPlayer.InventoryManager.ActiveHotbarSlot;

		// Empty hand + Shift = take 1 fuel item
		if (hotbarSlot == null || hotbarSlot.Empty)
		{
			if (byPlayer.Entity.Controls.ShiftKey) { return blockEntity.TryTakeFuelToPlayer(byPlayer, quantity: 1); }
			blockEntity.OnPlayerRightClick(byPlayer);
			return true;
		}

		// Fuel insertion: Shift inserts 1, otherwise inserts as much as possible.
		if (blockEntity.IsValidFuel(hotbarSlot.Itemstack))
		{
			int quantity = byPlayer.Entity.Controls.ShiftKey ? 1 : hotbarSlot.StackSize;
			return blockEntity.TryPutFuelFromPlayer(byPlayer, hotbarSlot, quantity);
		}
		blockEntity.OnPlayerRightClick(byPlayer);
		return true;
	}
	#endregion

	#region Ignitability
	public EnumIgniteState OnTryIgniteBlock(EntityAgent byEntity, BlockPos position, float secondsIgniting)
	{
		if (byEntity?.World == null) return EnumIgniteState.NotIgnitable;
		if (byEntity.World.BlockAccessor.GetBlockEntity(position) is not BlockEntitySteamEngine blockEntity) return EnumIgniteState.NotIgnitable;
		
		return blockEntity.GetIgnitableState(secondsIgniting);
	}
	
	public void OnTryIgniteBlockOver(EntityAgent byEntity, BlockPos position, float secondsIgniting, ref EnumHandling handling)
	{
		handling = EnumHandling.PreventDefault;
		if (secondsIgniting < 3f) return;
		if (byEntity?.World?.Side != EnumAppSide.Server) return;
		
		if (byEntity.World.BlockAccessor.GetBlockEntity(position) is not BlockEntitySteamEngine blockEntity) return;
		
		IPlayer player = (byEntity as EntityPlayer)?.Player;
		if (player != null && !byEntity.World.Claims.TryAccess(player, position, EnumBlockAccessFlags.Use)) return;
		
		blockEntity.TryIgniteNow();
	}
	
	public EnumIgniteState OnTryIgniteStack(EntityAgent byEntity, BlockPos position, ItemSlot slot, float secondsIgniting) { return EnumIgniteState.NotIgnitable; }
	#endregion

	// ...Pneumatica Mechanica
	#region Mechanica
	public MechanicalNetwork GetNetwork(IWorldAccessor world, BlockPos position)
	{
		var blockEntity = world.BlockAccessor.GetBlockEntity(position);
		return blockEntity?.GetBehavior<BEBehaviorMPBase>()?.Network;
	}

	public bool HasMechPowerConnectorAt(IWorldAccessor world, BlockPos position, BlockFacing face, BlockMPBase forBlock) { return face == PowerOutputFacing; }

	public void DidConnectAt(IWorldAccessor world, BlockPos position, BlockFacing face)
	{
		// Important: Since we inherit from BlockLiquidContainerBase, we need to manually replicate the BlockMPBase behaviour for axles to work.
		if (world.Side != EnumAppSide.Server) return;

		var blockEntity = world.BlockAccessor.GetBlockEntity(position);
		var mechanicalPowerBehavior = blockEntity?.GetBehavior<BEBehaviorMPBase>();
		if (mechanicalPowerBehavior == null) return;

		// If we don't yet have a network, create/discover one now.
		// This mirrors what vanilla BlockMPBase/BEBehaviorMPBase ensures happens.
		if (mechanicalPowerBehavior.Network == null)
		{
			// Use the behavior's configured OutFacingForNetworkDiscovery (set in SteamMechanica.SetOrientations()) if available.
			var outputFacing = mechanicalPowerBehavior.OutFacingForNetworkDiscovery ?? face;
			mechanicalPowerBehavior.CreateJoinAndDiscoverNetwork(outputFacing);
		}
	}
	#endregion 
}
