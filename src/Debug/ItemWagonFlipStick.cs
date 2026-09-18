using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace YangTransport; 

// Debug tool meant to test the flipping of wagons to ensure they can flip in place and make up for their non-centered offset
public sealed class ItemWagonFlipStick : Item
{
	internal static void ServerTryFlip(IRailwayConvoyVehicle vehicle, EntityAgent byEntity)
	{
		if (byEntity is not EntityPlayer playerEntity) return;
		if (vehicle.Entity.Api?.Side != EnumAppSide.Server) return;

		var serverPlayer = vehicle.Entity.Api.World.PlayerByUid(playerEntity.PlayerUID) as IServerPlayer;
		if (serverPlayer == null) return;

		if (RailVehicleFacingAlignment.TryFlipSingleVehicle(vehicle, out string error))
		{
			vehicle.Entity.World.PlaySoundAt
			(
				new AssetLocation("game:sounds/effect/bellows"), // we a little fruity with this one
				vehicle.Entity, null,
				randomizePitch: true,
				range: 20f, volume: 3f
			);
			return;
		}

		if (!string.IsNullOrEmpty(error)) serverPlayer.SendMessage(0, error, EnumChatType.Notification);
	}
}
