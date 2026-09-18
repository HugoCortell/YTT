using System;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Client;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.GameContent.Mechanics;
using Vintagestory.API.Config;

namespace YangTransport;

public sealed class BlockEntitySteamEngine : BlockEntityContainer, ISteamEngineHost, ISteamEngineFeedbackSource
{
	private EngineControlUI? ControlDialog;

	private readonly SteamEngineController EngineController;
	private SteamEngineConfig SteamEngineConfiguration;

	// For performance reasons, only tick when engine says it needs sim
	private long SimulationTickListenerID;
	private bool RemovedOrExploded;

	// Client-only steam FX profile + emission bounds cache
	public SteamFXProfile SteamEffectsProfile { get; private set; } = SteamFXProfile.Default;
	private readonly Vec3d SteamEmissionMinimum = new();
	private readonly Vec3d SteamEmissionMaximum = new();
	private readonly Vec3d WorldPosition = new();

	public BlockEntitySteamEngine() { EngineController = new SteamEngineController(this); }

	// BlockEntityContainer glue
	public override InventoryBase Inventory		=> EngineController.Inventory;
	public override string InventoryClassName	=> "yangtransport:steamengine";

	// Public accessors (used by BlockSteamEngine + SteamMechanica)
	public SteamEngineController Engine		=> EngineController;
	public SteamEngineConfig Config			=> SteamEngineConfiguration;

	public ItemSlot FuelSlot				=> EngineController.FuelSlot;
	public ItemSlot WaterSlot				=> EngineController.WaterSlot;

	public double TemperatureC				=> EngineController.TemperatureC;
	public double LimitTemperatureC			=> EngineController.LimitTemperatureC;
	public bool CanIgniteFuel				=> EngineController.CanIgniteFuel;
	public bool PowerEngaged				=> EngineController.PowerEngaged;
	public bool IsBurning					=> EngineController.IsBurning;

	#region  Init | Save | sync
	public override void Initialize(ICoreAPI coreAPI)
	{
		base.Initialize(coreAPI);
		RemovedOrExploded = false;

		SteamEngineConfiguration = SteamEngineConfig.FromBlock(Block);

		// IMPORTANT: BlockEntityContainer.Initialize() already LateInitialize()'d Inventory. Here we only bind config + compute modifiers + reset sync gates.
		EngineController.Initialize
		(
			SteamEngineConfiguration,
			inventoryID: null,
			inventoryPosition: Pos,
			serializedTree: null,
			worldAccessorForResolution: Api.World,
			serializedTreeIncludesInventory: false,
			lateInitializeInventory: false
		);

		WorldPosition.Set(Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5);

		EnsureSimTickingIfNeeded();

		if (coreAPI.Side == EnumAppSide.Client && coreAPI is ICoreClientAPI clientAPI)
		{
			SteamEffectsProfile = SteamFXProfile.FromAttributes(Block?.Attributes, SteamFXProfile.Default);

			// Prefer artist-defined element named "SteamFX", fallback to top-center
			if (SteamFxLocator.TryGetLocalSteamFxPos(clientAPI, Block, out Vec3f localEffectsPosition))
			{
				SteamEmissionMinimum.Set(Pos.X + localEffectsPosition.X - 0.03, Pos.Y + localEffectsPosition.Y - 0.03, Pos.Z + localEffectsPosition.Z - 0.03);
				SteamEmissionMaximum.Set(Pos.X + localEffectsPosition.X + 0.03, Pos.Y + localEffectsPosition.Y + 0.03, Pos.Z + localEffectsPosition.Z + 0.03);
			}
			else
			{
				SteamEmissionMinimum.Set(Pos.X + 0.48, Pos.Y + 0.95, Pos.Z + 0.48);
				SteamEmissionMaximum.Set(Pos.X + 0.52, Pos.Y + 1.05, Pos.Z + 0.52);
			}

			clientAPI.ModLoader.GetModSystem<ModSystemSteamEngineFeedback>()?.Register(this);
		}
	}

	public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
	{
		base.FromTreeAttributes(tree, worldAccessForResolve); // Base handles inventory under key "inventory"
		EngineController.FromTreeAttributes(tree, worldAccessForResolve); // Engine state keys live in the same tree

		// Client: if UI open, push values into its attribute tree
		if (Api?.Side == EnumAppSide.Client && ControlDialog != null) { EngineController.WriteDialogValues(ControlDialog.Attributes); }
	}

	public override void ToTreeAttributes(ITreeAttribute tree)
	{
		base.ToTreeAttributes(tree); // Base writes inventory
		EngineController.ToTreeAttributes(tree); // Engine writes boiler state + convenience GUI keys
	}
	#endregion

	#region  Client UI open/close
	public void OnPlayerRightClick(IPlayer byPlayer)
	{
		if (Api?.Side != EnumAppSide.Client) return;
		ToggleControlDialogClient(byPlayer);
	}

	private void ToggleControlDialogClient(IPlayer byPlayer)
	{
		if (Api is not ICoreClientAPI clientAPI) return;

		if (ControlDialog == null)
		{
			string title = Block?.GetPlacedBlockName(Api.World, Pos) ?? Lang.Get("yangtransport:block-steamengine-standard-north");

			var dialogAttributes = new SyncedTreeAttribute();
			EngineController.WriteDialogValues(dialogAttributes);

			Action<object> sendInventoryPacket = packet => clientAPI.Network.SendBlockEntityPacket(Pos.X, Pos.InternalY, Pos.Z, packet);
			Action<int, byte[]?> sendControlPacket = (packetID, data) => clientAPI.Network.SendBlockEntityPacket(Pos, packetID, data);

			ControlDialog = new EngineControlUI
			(
				dialogTitle: title,
				dialogID: Pos.ToString(),
				inventory: Inventory,
				attributes: dialogAttributes,
				clientAPI: clientAPI,
				sendInventoryPacket: sendInventoryPacket,
				sendControlPacket: sendControlPacket,
				showLocomotiveControls: false,
				blockEntityPosition: Pos.Copy()
			);

			ControlDialog.OnClosed += () =>
			{
				ControlDialog = null;
				clientAPI.Network.SendBlockEntityPacket(Pos, EngineControlUI.PacketIDClose, null);
				clientAPI.Network.SendPacketClient(Inventory.Close(byPlayer));
			};

			ControlDialog.TryOpen();
			clientAPI.Network.SendPacketClient(Inventory.Open(byPlayer));
			clientAPI.Network.SendBlockEntityPacket(Pos, EngineControlUI.PacketIDOpen, null);
		}
		else
		{
			ControlDialog.TryClose();
		}
	}

	public override void OnBlockUnloaded()
	{
		RemovedOrExploded = true;
		base.OnBlockUnloaded();
		ControlDialog?.TryClose();
		ControlDialog?.Dispose();
		StopSimTicking();

		if (Api?.Side == EnumAppSide.Client && Api is ICoreClientAPI clientAPI) { clientAPI.ModLoader.GetModSystem<ModSystemSteamEngineFeedback>()?.Unregister(this); }
	}

	public override void OnBlockRemoved()
	{
		RemovedOrExploded = true;
		base.OnBlockRemoved();
		ControlDialog?.TryClose();
		ControlDialog?.Dispose();
		StopSimTicking();

		if (Api?.Side == EnumAppSide.Client && Api is ICoreClientAPI clientAPI) { clientAPI.ModLoader.GetModSystem<ModSystemSteamEngineFeedback>()?.Unregister(this); }
	}
	#endregion

	#region Networking & Tick Scheduling
	public override void OnReceivedClientPacket(IPlayer player, int packetID, byte[] data)
	{
		base.OnReceivedClientPacket(player, packetID, data);
		EngineController.OnReceivedClientPacket(player, packetID, data);
	}

	private void EnsureSimTickingIfNeeded()
	{
		if (Api?.Side != EnumAppSide.Server) return;

		if (RemovedOrExploded || !EngineController.ShouldSimulate) { StopSimTicking(); return; }
		if (SimulationTickListenerID == 0) { SimulationTickListenerID = RegisterGameTickListener(OnSimTick, 500); }
	}

	private void StopSimTicking()
	{
		if (SimulationTickListenerID != 0)
		{
			UnregisterGameTickListener(SimulationTickListenerID);
			SimulationTickListenerID = 0;
		}
	}

	private void OnSimTick(float dt)
	{
		if (Api?.Side != EnumAppSide.Server) return;
		if (RemovedOrExploded) { StopSimTicking(); return; }

		EngineController.OnGameTick(dt);

		if (RemovedOrExploded) return;
		EnsureSimTickingIfNeeded(); // Might have become idle after this sim step
	}
	#endregion

	#region Block Interactions Wrappers
	public bool IsValidFuel(ItemStack stack) => EngineController.IsValidFuel(stack);

	public bool TryPutFuelFromPlayer(IPlayer player, ItemSlot fromSlot, int requestedQuantity) => EngineController.TryPutFuelFromPlayer(player, fromSlot, requestedQuantity);
	
	public bool TryTakeFuelToPlayer(IPlayer player, int quantity) => EngineController.TryTakeFuelToPlayer(player, quantity);
	
	public EnumIgniteState GetIgnitableState(float secondsIgniting) => EngineController.GetIgnitableState(secondsIgniting);

	public void TryIgniteNow()
	{
		if (RemovedOrExploded) return;

		EngineController.TryIgniteNow();
		EnsureSimTickingIfNeeded();
	}


	public override void GetBlockInfo(IPlayer forPlayer, StringBuilder descriptionBuilder)
	{
		double heatUnits = TemperatureC >= 100.0 ? TemperatureC / 100.0 : 0.0;
		double estimatedTorque = heatUnits * Config.RawPowerNewtonsPer100Celsius;
		double estimatedSpeed = heatUnits * Config.AccelerationNewtonsPer100Celsius;

		descriptionBuilder.AppendLine(Lang.Get("yangtransport:steamengine-blockinfo-torque", estimatedTorque.ToString("0.###")));
		descriptionBuilder.AppendLine(Lang.Get("yangtransport:steamengine-blockinfo-speed", estimatedSpeed.ToString("0.###")));
	}
	#endregion

	#region  ISteamEngineHost
	ICoreAPI ISteamEngineHost.API => Api;
	Vec3d ISteamEngineHost.WorldPosition => WorldPosition;

	bool ISteamEngineHost.CanPlayerUse(IPlayer player)
	{
		if (Api?.World == null || player == null) return false;
		return Api.World.Claims.TryAccess(player, Pos, EnumBlockAccessFlags.Use);
	}

	bool ISteamEngineHost.ComputePowerEngaged()
	{
		var mechanicalPowerBehavior = GetBehavior<BEBehaviorMPBase>();
		if (mechanicalPowerBehavior == null || mechanicalPowerBehavior.disconnected) return false;

		var mechanicalNetwork = mechanicalPowerBehavior.Network;
		if (mechanicalNetwork == null) return false;

		return mechanicalNetwork.nodes != null && mechanicalNetwork.nodes.Count > 1; // Treat individual networks as not engaged
	}

	void ISteamEngineHost.RequestSync(bool force)
	{
		if (RemovedOrExploded) return;

		// Controller already does sync gating, so we can cheap out
		EnsureSimTickingIfNeeded();
		MarkDirty(false);
	}

	void ISteamEngineHost.ExplodeAndRemove()
	{
		if (Api?.Side != EnumAppSide.Server || RemovedOrExploded) return;

		RemovedOrExploded = true;
		StopSimTicking();

		double explosionTemperatureC = TemperatureC; // Save the temp prior to explosion, there's some memory fuckery reasons for this
		Block oldBlock = Block;
		Api.World.BlockAccessor.SetBlock(0, Pos); // Remove engine block prior to explosion (mirrors vanilla bombs)

		if (Api.World is IServerWorldAccessor serverWorld) { serverWorld.CreateExplosion(Pos, EnumBlastType.EntityBlast, 0, (explosionTemperatureC / 100), 1f, null); }
		TransportDropTables.SpawnBlockDrops(Api.World, oldBlock, Pos, TransportDropTables.DestroyDropsTable, randomVelocity: true);
	}

	void ISteamEngineHost.GetSteamEmissionBounds(Vec3d min, Vec3d max)
	{
		// Client: use cached artist-defined bounds
		if (Api?.Side == EnumAppSide.Client)
		{
			min.Set(SteamEmissionMinimum);
			max.Set(SteamEmissionMaximum);
			return;
		}

		// Server: bounds only used for client burst effects (currently). Provide a some reasonable default.
		min.Set(Pos.X + 0.48, Pos.Y + 0.95, Pos.Z + 0.48);
		max.Set(Pos.X + 0.52, Pos.Y + 1.05, Pos.Z + 0.52);
	}
	#endregion

	#region  ISteamEngineFeedbackSource (forward to controller)
	public bool TryGetFeedback(out double temperatureC, out int fuelBurnTemperatureC, out byte liquidKind) 
		=> EngineController.TryGetFeedback(out temperatureC, out fuelBurnTemperatureC, out liquidKind);

	public bool TryGetAudioState(out double temperatureC, out bool producingPower)
		=> EngineController.TryGetAudioState(out temperatureC, out producingPower);

	public bool IsWithinAudioPreCull(double playerX, double playerY, double playerZ, double range)
	{
		double maxDistance = range + 2.0;
		double dx = playerX - WorldPosition.X;
		double dy = playerY - WorldPosition.Y;
		double dz = playerZ - WorldPosition.Z;
		return dx * dx + dy * dy + dz * dz <= maxDistance * maxDistance;
	}

	public void GetEmissionBounds(out Vec3d minimumPosition, out Vec3d maximumPosition) 
	{
		minimumPosition = SteamEmissionMinimum;
		maximumPosition = SteamEmissionMaximum;
	}
	#endregion
}
