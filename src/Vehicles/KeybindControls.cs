using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

namespace YangTransport; 

public sealed class YangtransportKeybindSystem : ModSystem
{
	private ICoreClientAPI? ClientAPI;

	public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

	public override void StartClientSide(ICoreClientAPI coreAPI)
	{
		ClientAPI = coreAPI;

		RegisterHotkey
		(
			"yangtransport-locomotive-open-boiler", Lang.Get("yangtransport:keybind-open-boiler"),
			GlKeys.B, _ => ToggleBoilerUi()
		);

		// Locomotive drive
		RegisterHotkey
		(
			"yangtransport-locomotive-drive-forward", Lang.Get("yangtransport:keybind-drive-lever-forward"),
			GlKeys.Keypad8, _ => SendDrivePhase(2)
		);
		RegisterHotkey
		(
			"yangtransport-locomotive-drive-stop", Lang.Get("yangtransport:keybind-drive-lever-stop"),
			GlKeys.Keypad0, _ => SendDrivePhase(1)
		);
		RegisterHotkey
		(
			"yangtransport-locomotive-drive-reverse", Lang.Get("yangtransport:keybind-drive-lever-reverse"),
			GlKeys.Keypad2, _ => SendDrivePhase(0)
		);

		// Locomotive turn
		RegisterHotkey
		(
			"yangtransport-locomotive-turn-left", Lang.Get("yangtransport:keybind-turn-lever-left"),
			GlKeys.Keypad4, _ => SendTurnPhase(0)
		);
		RegisterHotkey
		(
			"yangtransport-locomotive-turn-straight", Lang.Get("yangtransport:keybind-turn-lever-straight"),
			GlKeys.Keypad5, _ => SendTurnPhase(1)
		);
		RegisterHotkey
		(
			"yangtransport-locomotive-turn-right", Lang.Get("yangtransport:keybind-turn-lever-right"),
			GlKeys.Keypad6, _ => SendTurnPhase(2)
		);
	}

	private void RegisterHotkey(string code, string name, GlKeys defaultKey, ActionConsumable<KeyCombination> handler)
	{
		if (ClientAPI == null) return;

		ClientAPI.Input.RegisterHotKey(code, name, defaultKey, HotkeyType.MovementControls);
		ClientAPI.Input.SetHotKeyHandler(code, handler);
	}

	#region Helpers
	private Entity? GetLocalMountedEntity()
	{
		EntityPlayer? player = ClientAPI?.World?.Player?.Entity;
		IMountableSeat? seat = player?.MountedOn;
		if (seat == null) return null;

		return seat.Entity ?? seat.MountSupplier?.OnEntity;
	}

	#endregion

	#region Boiler UI
	private bool ToggleBoilerUi()
	{
		if (ClientAPI == null) return false;

		IMountableSeat? seat = ClientAPI?.World?.Player?.Entity?.MountedOn;		if (seat == null) return false;
		Entity? mountEntity = seat.Entity ?? seat.MountSupplier?.OnEntity;	if (mountEntity == null) return false;

		Entity? boilerEntity = ResolveMountedBoilerTarget(mountEntity, seat);
		EntityBehaviorSteamPowered? boiler = boilerEntity?.GetBehavior<EntityBehaviorSteamPowered>();

		return boiler?.ClientTryToggleBoilerUi(ClientAPI.World.Player) == true;
	}

	private Entity? ResolveMountedBoilerTarget(Entity mountEntity, IMountableSeat seat)
	{
		if (mountEntity is EntityStandardGaugeLocomotive)
		{
			if (seat.SeatId != "conductor") return null;
			return mountEntity.GetBehavior<EntityBehaviorSteamPowered>() != null ? mountEntity : null;
		}

		return mountEntity is EntityMinecart minecart ? ResolveMinecartBoilerTarget(minecart) : null;
	}

	private Entity? ResolveMinecartBoilerTarget(EntityMinecart startingMinecart)
	{
		EntityMinecart? currentMinecart = startingMinecart;
		int maxSteps = Math.Clamp(startingMinecart.WatchedAttributes.GetInt("convoyCount", 1) + 2, 4, 64);

		for (int stepIndex = 0; stepIndex < maxSteps && currentMinecart != null; stepIndex++)
		{
			if (currentMinecart.GetBehavior<EntityBehaviorSteamPowered>() != null) return currentMinecart;

			long previousEntityID = currentMinecart.PreviousCartID;
			if (previousEntityID == 0 || previousEntityID == currentMinecart.EntityId) break;

			currentMinecart = ClientAPI?.World.GetEntityById(previousEntityID) as EntityMinecart;
		}

		return null;
	}
	#endregion

	#region Locomotive Control
	private bool SendDrivePhase(byte drivePhase) => SendLeverPhase(SteamEnginePacketIds.SetDriveLeverPhase, drivePhase);
	private bool SendTurnPhase(byte turnPhase) => SendLeverPhase(SteamEnginePacketIds.SetTurnLeverPhase, turnPhase);

	private bool SendLeverPhase(int packetID, byte leverPhase)
	{
		if (ClientAPI == null) return false;

		Entity? mountEntity = GetLocalMountedEntity();
		if (mountEntity == null) return false;

		bool canTryControl = mountEntity.GetBehavior<EntityBehaviorSteamPowered>() != null || mountEntity is EntityMinecart;
		if (!canTryControl) return false;
		
		ClientAPI.Network.SendEntityPacket(mountEntity.EntityId, packetID, new[] { leverPhase });
		return true;
	}
	#endregion
}
