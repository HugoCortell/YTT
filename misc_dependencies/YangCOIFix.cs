using System;
using System.Collections.Generic;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.GameContent;

namespace YangCOIFix;

public sealed class YangCOIFixSystem : ModSystem
{
	private static readonly Type[] OnInteractSignature =
	[
		typeof(EntityAgent),
		typeof(ItemSlot),
		typeof(Vec3d),
		typeof(EnumInteractMode)
	];

	private static readonly Dictionary<Type, bool> HasCustomOnInteractCache = [];

	private ICoreClientAPI? ClientApi;

	public override bool ShouldLoad(EnumAppSide forSide) { return forSide == EnumAppSide.Client; }

	public override void StartClientSide(ICoreClientAPI api)
	{
		ClientApi = api;
		api.Input.InWorldAction += OnInWorldAction;
	}

	public override void Dispose()
	{
		if (ClientApi != null)
		{
			ClientApi.Input.InWorldAction -= OnInWorldAction;
			ClientApi = null;
		}

		HasCustomOnInteractCache.Clear();
	}

	private void OnInWorldAction(EnumEntityAction action, bool on, ref EnumHandling handled)
	{
		if (!on || action != EnumEntityAction.InWorldRightMouseDown || handled != EnumHandling.PassThrough) { return; }

		ICoreClientAPI? api = ClientApi;
		if (api == null) return;

		IClientPlayer player = api.World.Player;
		EntitySelection? entitySel = player.CurrentEntitySelection;

		if (entitySel?.Entity == null || player.CurrentBlockSelection != null) { return; }

		ItemSlot? handSlot = player.InventoryManager.ActiveHotbarSlot;
		if (handSlot?.Itemstack?.Collectible is not ILiquidInterface) { return; }

		if (!TryInteractEntityFirst(player, handSlot, entitySel)) { return; }

		handled = EnumHandling.PreventDefault;
	}

	private bool TryInteractEntityFirst(IClientPlayer player, ItemSlot handSlot, EntitySelection entitySel)
	{
		Entity entity = entitySel.Entity;

		if (HasCustomOnInteract(entity.GetType()))
		{
			entity.OnInteract
			(
				player.Entity,
				handSlot,
				entitySel.HitPosition,
				EnumInteractMode.Interact
			);

			SendEntityInteraction(entitySel);
			return true;
		}

		var behaviors = entity.SidedProperties?.Behaviors;
		if (behaviors == null) return false;

		EnumHandling entityHandling = EnumHandling.PassThrough;

		foreach (EntityBehavior behavior in behaviors)
		{
			behavior.OnInteract
			(
				player.Entity,
				handSlot,
				entitySel.HitPosition,
				EnumInteractMode.Interact,
				ref entityHandling
			);

			if (entityHandling == EnumHandling.PreventSubsequent) { break; }
		}

		if (entityHandling == EnumHandling.PassThrough) { return false; }

		SendEntityInteraction(entitySel);
		return true;
	}

	private void SendEntityInteraction(EntitySelection entitySel)
	{
		ClientApi!.Network.SendPacketClient
		(
			ClientPackets.EntityInteraction
			(
				1,
				entitySel.Entity.EntityId,
				entitySel.Face,
				entitySel.HitPosition,
				entitySel.SelectionBoxIndex
			)
		);
	}

	private static bool HasCustomOnInteract(Type entityType)
	{
		if (HasCustomOnInteractCache.TryGetValue(entityType, out bool cached)) { return cached; }

		MethodInfo? method = entityType.GetMethod
		(
			nameof(Entity.OnInteract),
			BindingFlags.Instance | BindingFlags.Public,
			binder: null,
			types: OnInteractSignature,
			modifiers: null
		);
		bool hasCustomOnInteract = method == null|| (method.DeclaringType != typeof(Entity) && method.DeclaringType != typeof(EntityAgent));

		HasCustomOnInteractCache[entityType] = hasCustomOnInteract;
		return hasCustomOnInteract;
	}
}
