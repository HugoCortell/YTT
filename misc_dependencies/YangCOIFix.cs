using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace YangCOIFix;

public sealed class YangCOIFixSystem : ModSystem
{

	private Harmony? harmony;

	public override bool ShouldLoad(EnumAppSide forSide) { return forSide == EnumAppSide.Client; }

	public override void StartClientSide(ICoreClientAPI api)
	{
		harmony = new Harmony("YangCOIFix");
		harmony.PatchAll(Assembly.GetExecutingAssembly());
	}

	public override void Dispose()
	{
		harmony?.UnpatchAll("YangCOIFix");
		harmony = null;
	}
}

[HarmonyPatch]
internal static class YangCOIFixPatch
{
	private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> GameRef = AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");

	private static MethodBase TargetMethod()
	{
		return AccessTools.Method
		(
			typeof(SystemMouseInWorldInteractions),
			"TryBeginUseActiveSlotItem",
			new[] { typeof(BlockSelection), typeof(EntitySelection) }
		);
	}

	private static bool Prefix(SystemMouseInWorldInteractions __instance, BlockSelection __0, EntitySelection __1, ref bool __result) // Harmony is ugly
	{
		BlockSelection blockSel = __0;
		EntitySelection entitySel = __1;

		if (blockSel != null || entitySel?.Entity == null) { return true; }

		ClientMain game = GameRef(__instance);
		ItemSlot? handSlot = game?.Player?.InventoryManager?.ActiveHotbarSlot; if (handSlot == null) { return true; }

		// Only bother with doing this with liquid containers, the only thing relevant to YTT
		if (!IsDrinkableLiquidContainer(game.EntityPlayer, handSlot)) { return true; } // Everyone else can make their own, better programmed mod, or wait for 1.23
		if (TryEntityInteractClientFirst(game, entitySel, handSlot)) { __result = true; return false; } // Ignore self-use if the entity can handle the interaction

		return true; // Passthrough
	}

	private static bool IsDrinkableLiquidContainer(EntityAgent? player, ItemSlot? slot)
	{
		ItemStack? stack = slot?.Itemstack;
		if (player == null || stack == null) return false;

		if (stack.Collectible is not BlockLiquidContainerBase container) return false;
		if (!container.CanDrinkFrom) return false;

		return container.GetNutritionPropertiesPerLitre(player.World, stack, player) != null;
	}

	private static bool TryEntityInteractClientFirst(ClientMain game, EntitySelection esel, ItemSlot handSlot)
	{
		if (game?.EntityPlayer == null) return false;

		Entity entity = esel.Entity;
		EntitySidedProperties? sidedProperties = entity.SidedProperties; if (sidedProperties?.Behaviors == null) return false;

		EnumHandling handled = EnumHandling.PassThrough;
		bool anyHandled = false;
		foreach (EntityBehavior behavior in sidedProperties.Behaviors)
		{
			behavior.OnInteract(game.EntityPlayer, handSlot, esel.HitPosition, EnumInteractMode.Interact, ref handled);
			if (handled != EnumHandling.PassThrough) { anyHandled = true; }
			if (handled == EnumHandling.PreventSubsequent) { break; }
		}
		if (!anyHandled) { return false; }

		game.SendPacketClient(ClientPackets.EntityInteraction(1, esel.Entity.EntityId, esel.Face, esel.HitPosition, esel.SelectionBoxIndex));
		return true;
	}
}
