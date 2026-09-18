using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace YangTransport;

public sealed class ItemMinecart : Item
{
	private ICoreClientAPI? ClientAPI;
	private RailVehiclePlacementPreviewRenderer? PreviewRenderer;

	public override void OnLoaded(ICoreAPI coreAPI)
	{
		base.OnLoaded(coreAPI);
		ClientAPI = coreAPI as ICoreClientAPI;
	}

	public override void OnUnloaded(ICoreAPI coreAPI)
	{
		PreviewRenderer?.Dispose();
		PreviewRenderer = null;
		ClientAPI = null;
		base.OnUnloaded(coreAPI);
	}

	public override void GetHeldItemInfo(ItemSlot inventorySlot, StringBuilder description, IWorldAccessor world, bool withDebugInformation)
	{
		base.GetHeldItemInfo(inventorySlot, description, world, withDebugInformation);
		SteamEngineHandbookInfo.AppendRailVehicleInfo(inventorySlot, description, world, includeVehicleLength: false);
	}

	public override void OnHeldRenderOpaque(ItemSlot inventorySlot, IClientPlayer byPlayer)
	{
		if (ClientAPI == null) return;

		PreviewRenderer ??= new RailVehiclePlacementPreviewRenderer(ClientAPI);
		PreviewRenderer.Render(inventorySlot, byPlayer, requiredGauge: 0);
	}

	public override void OnHeldInteractStart
	(
		ItemSlot slot, EntityAgent byEntity, BlockSelection blockSelection,
		EntitySelection entitySelection, bool firstEvent, ref EnumHandHandling handHandling
	)
	{
		if (blockSelection == null) return;
		if (byEntity is not EntityPlayer playerEntity) return;

		IWorldAccessor world = byEntity.World;
		IPlayer? player = world.PlayerByUid(playerEntity.PlayerUID);
		if (player == null) return;

		if (!RailVehiclePlacementPreviewUtil.TryGetRailTarget(world, blockSelection, requiredGauge: 0, out BlockPos railPosition, out _)) return;

		// This is a rail-vehicle placement attempt.
		// The client only sends the interaction. The server remains authoritative for validation, spawning and inventory mutation.
		handHandling = EnumHandHandling.PreventDefault;

		bool isServer = world.Api.Side == EnumAppSide.Server;

		if (!world.Claims.TryAccess(player, railPosition, EnumBlockAccessFlags.BuildOrBreak)) { if (isServer) slot.MarkDirty(); return; }

		if (!isServer) return;

		if (!RailVehiclePlacementPreviewUtil.ServerCanPlaceVehicle(world.Api, byEntity, Code, railPosition, requiredGauge: 0, out bool overlaps, out bool insufficientRail))
		{
			if (overlaps && player is IServerPlayer serverPlayer) serverPlayer.SendIngameError("yangtransport:locomotive-occupied");
			slot.MarkDirty();
			return;
		}

		// Spawn entity with same code as item
		AssetLocation asset = Code;
		EntityProperties? entityProperties = world.GetEntityType(asset);
		if (entityProperties == null)
		{
			world.Logger.Error("yangtransport: No such entity type {0}", asset);
			slot.MarkDirty();
			return;
		}

		Entity? createdEntity = world.ClassRegistry.CreateEntity(entityProperties);
		if (createdEntity is not EntityMinecart minecartEntity)
		{
			world.Logger.Error("yangtransport: Entity type {0} did not create a minecart entity", asset);
			slot.MarkDirty();
			return;
		}

		minecartEntity.ServerPos.X = railPosition.X + 0.5f;
		minecartEntity.ServerPos.Y = railPosition.Y + 0.125f;
		minecartEntity.ServerPos.Z = railPosition.Z + 0.5f;
		minecartEntity.ServerPos.Yaw = byEntity.ServerPos.Yaw;

		minecartEntity.Pos.SetFrom(minecartEntity.ServerPos);

		// seed preferred direction based on player yaw
		minecartEntity.Attributes.SetFloat("seedYaw", minecartEntity.ServerPos.Yaw);

		world.SpawnEntity(minecartEntity);
		RailVehiclePlacementPreviewUtil.ServerPlayPlacementSound(world.Api, this, minecartEntity);

		// Consume only after authoritative server validation and spawn succeeded.
		if (player.WorldData.CurrentGameMode != EnumGameMode.Creative) slot.TakeOut(1);
		slot.MarkDirty();
	}
}
