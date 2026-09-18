using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace YangTransport;

/// Spawns the standard gauge test locomotive on standard gauge track blocks.
public sealed class ItemStandardGaugeLocomotive : Item
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

	public override void GetHeldItemInfo(ItemSlot inputSlot, StringBuilder description, IWorldAccessor world, bool withDebugInformation)
	{
		base.GetHeldItemInfo(inputSlot, description, world, withDebugInformation);
		SteamEngineHandbookInfo.AppendRailVehicleInfo(inputSlot, description, world, includeVehicleLength: true);
	}

	public override void OnHeldRenderOpaque(ItemSlot inputSlot, IClientPlayer byPlayer)
	{
		if (ClientAPI == null) return;

		PreviewRenderer ??= new RailVehiclePlacementPreviewRenderer(ClientAPI);
		PreviewRenderer.Render(inputSlot, byPlayer, requiredGauge: 1);
	}

	public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSelection, EntitySelection entitySelection,
		bool firstEvent, ref EnumHandHandling handHandling)
	{
		if (blockSelection == null) return;
		if (byEntity is not EntityPlayer entityPlayer) return;

		IWorldAccessor world = byEntity.World;
		IPlayer? player = world.PlayerByUid(entityPlayer.PlayerUID);
		if (player == null) return;

		if (!RailVehiclePlacementPreviewUtil.TryGetRailTarget(world, blockSelection, requiredGauge: 1, out BlockPos railPosition, out _)) return;

		// This is a rail-vehicle placement attempt.
		// The client only sends the interaction. The server remains authoritative for validation, spawning and inventory mutation.
		handHandling = EnumHandHandling.PreventDefault;

		bool isServer = world.Api.Side == EnumAppSide.Server;

		if (!world.Claims.TryAccess(player, railPosition, EnumBlockAccessFlags.BuildOrBreak)) { if (isServer) slot.MarkDirty(); return; }
		if (!isServer) return;

		if (!RailVehiclePlacementPreviewUtil.ServerCanPlaceVehicle(world.Api, byEntity, Code, railPosition, requiredGauge: 1, out bool overlaps, out bool insufficientRail))
		{
			if (player is IServerPlayer serverPlayer)
			{
				if (insufficientRail) serverPlayer.SendIngameError("yangtransport:locomotive-placement-space");
				else if (overlaps) serverPlayer.SendIngameError("yangtransport:locomotive-occupied");
			}

			slot.MarkDirty(); return;
		}

		// Spawn entity with same code as item
		AssetLocation asset = Code;
		EntityProperties? entityType = world.GetEntityType(asset);
		if (entityType == null)
		{
			world.Logger.Error("yangtransport: No such entity type {0}", asset);
			slot.MarkDirty();
			return;
		}

		Entity? entity = world.ClassRegistry.CreateEntity(entityType);
		if (entity is not EntityStandardGaugeLocomotive locomotive)
		{
			world.Logger.Error("yangtransport: Entity type {0} did not create a standard-gauge locomotive entity", asset);
			slot.MarkDirty();
			return;
		}

		locomotive.ServerPos.X = railPosition.X + 0.5f;
		locomotive.ServerPos.Y = railPosition.Y + TrackSpecsDictionary.DefaultY;
		locomotive.ServerPos.Z = railPosition.Z + 0.5f;
		locomotive.ServerPos.Yaw = byEntity.ServerPos.Yaw;

		locomotive.Pos.SetFrom(locomotive.ServerPos);

		// Seed preferred direction based on player yaw
		locomotive.Attributes.SetFloat("seedYaw", locomotive.ServerPos.Yaw);

		world.SpawnEntity(locomotive);
		RailVehiclePlacementPreviewUtil.ServerPlayPlacementSound(world.Api, this, locomotive);

		// Consume only after authoritative server validation and spawn succeeded.
		if (player.WorldData.CurrentGameMode != EnumGameMode.Creative) slot.TakeOut(1);
		slot.MarkDirty();
	}
}
