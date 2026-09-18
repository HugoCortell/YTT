using System;
using YangTransport;
using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace YangTransport
{
	public class YangtransportCore : ModSystem
	{
		public static ICoreAPI ?CoreAPI { get; private set; }
		public static ICoreServerAPI ?ServerAPI { get; private set; }

		private ICoreClientAPI? ClientAPIForEvents;
		private ICoreServerAPI? ServerAPIForEvents;

		public override void Start(ICoreAPI coreAPI)
		{
			CoreAPI = coreAPI;
			if (coreAPI.Side == EnumAppSide.Server) ServerAPI = coreAPI as ICoreServerAPI;

			// Register Steam Engines
			coreAPI.RegisterBlockClass("yangtransport.steamengine", typeof(BlockSteamEngine));
			coreAPI.RegisterBlockEntityClass("YangTransportSteamEngine", typeof(BlockEntitySteamEngine));

			// Tools
			coreAPI.RegisterItemClass("yangtransport.linkagepole", typeof(ItemLinkagePole));
			coreAPI.RegisterItemClass("yangtransport.nerdtool", typeof(ItemNerdTool));
			coreAPI.RegisterItemClass("yangtransport.conductorlocust", typeof(ItemConductorLocust));
			coreAPI.RegisterCollectibleBehaviorClass("MechanicalVehicleDeconstructTool", typeof(CollectibleBehaviorRailVehicleDeconstructTool));

			// Register generic JSON-driven track blocks
			coreAPI.RegisterBlockClass("yangtransport.trackstatic", typeof(BlockTrackStatic));
			coreAPI.RegisterBlockClass("yangtransport.trackdynamic", typeof(BlockTrackDynamic));
			coreAPI.RegisterBlockClass("yangtransport.trackaligned", typeof(BlockTrackAligned));

			// Register Minecarts & Wagonways
			coreAPI.RegisterItemClass("yangtransport.minecart", typeof(ItemMinecart));
			coreAPI.RegisterEntity("yangtransport.minecart", typeof(EntityMinecart));
			coreAPI.RegisterMountable("yangtransport.minecartseat", RailVehicleSeat.GetMountable);
			coreAPI.RegisterMountable("yangtransport.railvehicleseat", RailVehicleSeat.GetMountable);
			coreAPI.RegisterEntityBehaviorClass("SteamPowered", typeof(EntityBehaviorSteamPowered));

			// Register Locomotives & Standard Gauge
			coreAPI.RegisterItemClass("yangtransport.sglocomotive", typeof(ItemStandardGaugeLocomotive));
			coreAPI.RegisterEntity("yangtransport.sglocomotive", typeof(EntityStandardGaugeLocomotive));
			coreAPI.RegisterEntityBehaviorClass("sglocostats", typeof(EntityBehaviorStandardGaugeLocomotiveStats));
			coreAPI.RegisterEntityBehaviorClass("SGBodySelectionBoxes", typeof(EntityBehaviorSGBodySelectionBoxes));
			coreAPI.RegisterEntityBehaviorClass("SGAutoSeats", typeof(EntityBehaviorSGAutoSeats));
			coreAPI.RegisterEntityBehaviorClass("SGStorage", typeof(EntityBehaviorSGStorage));
			coreAPI.RegisterEntityBehaviorClass("SGRefrigeration", typeof(EntityBehaviorSGRefrigeration));

			// Register Systems & Debug
			coreAPI.RegisterBlockEntityClass("YangTransportRailSignal", typeof(BlockEntityRailSignal));
			coreAPI.RegisterBlockEntityClass("YangTransportRailStation", typeof(BlockEntityRailStation));
			coreAPI.RegisterItemClass("yangtransport.trackpointwand", typeof(ItemTrackPointWand));
			coreAPI.RegisterItemClass("yangtransport.railloopwand", typeof(ItemRailLoopWand));
			coreAPI.RegisterItemClass("yangtransport.wagonflipstick", typeof(ItemWagonFlipStick));
			coreAPI.RegisterBlockEntityBehaviorClass("SteamMechanicaRotor", typeof(SteamMechanica));
		}

		public override void AssetsFinalize(ICoreAPI coreAPI)
		{
			TrackBlockCompiler.CompileAndValidate(coreAPI);
			TrackSpecsDictionary.CompileFromRegisteredBlocks(coreAPI);
			TrackManualPlacement.OnTracksCompiled();
			RefrigerantCatalog.Compile(coreAPI);

			if (coreAPI.Side == EnumAppSide.Server) { RailwayVehicleShared.SetupTrainCrashTagSets(coreAPI); }
		}

		public override void StartClientSide(ICoreClientAPI clientAPI)
		{
			ClientAPIForEvents = clientAPI;
			clientAPI.Event.AfterActiveSlotChanged += OnClientActiveSlotChanged; // This is ugly but also the correct and intended way to do it.

			// Client-only renderers.
			clientAPI.RegisterEntityRendererClass("yangtransport.minecarthaulage", typeof(EntityMinecartHaulageRenderer));
			clientAPI.RegisterEntityRendererClass("yangtransport.sglocomotivebogies", typeof(EntityStandardGaugeLocomotiveBogieRenderer));
		}

		public override void StartServerSide(ICoreServerAPI serverAPI)
		{
			ServerAPIForEvents = serverAPI;
			YangTransportSettings.Load(serverAPI);
			serverAPI.Event.AfterActiveSlotChanged += OnServerActiveSlotChanged;
		}

		public override void Dispose()
		{
			if (ClientAPIForEvents != null) ClientAPIForEvents.Event.AfterActiveSlotChanged -= OnClientActiveSlotChanged;
			if (ServerAPIForEvents != null) ServerAPIForEvents.Event.AfterActiveSlotChanged -= OnServerActiveSlotChanged;

			TrackManualPlacement.Dispose();
			ClientAPIForEvents = null;
			ServerAPIForEvents = null;
		}

		#region Events
		private void OnClientActiveSlotChanged(ActiveSlotChangeEventArgs eventArguments)
		{
			if (ClientAPIForEvents == null) return;

			TrackManualPlacement.CloseToolModeDialog(ClientAPIForEvents);
			TrackManualPlacement.Reset(ClientAPIForEvents.World.Player);

			BlockTrackAligned? heldBlock = ClientAPIForEvents.World.Player?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible as BlockTrackAligned;
			TrackManualPlacement.OnClientActiveSlotChanged(heldBlock);
		}

		private static void OnServerActiveSlotChanged(IServerPlayer player, ActiveSlotChangeEventArgs eventArguments)
		{
			TrackManualPlacement.Reset(player);
		}
		#endregion
	}
}