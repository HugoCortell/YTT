using System;
using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace YangTransport;

[ProtoContract] public sealed class CartDebugToggle { [ProtoMember(1)] public bool Enabled; }
[ProtoContract] public sealed class CartDebugUpdate { [ProtoMember(1)] public CartDebugMarker[] Markers = Array.Empty<CartDebugMarker>(); }

[ProtoContract] public sealed class CartDebugMarker
{
	public const byte StateActive = 0;
	public const byte StateVirtual = 1;

	[ProtoMember(1)] public double X;
	[ProtoMember(2)] public double Z;
	[ProtoMember(3)] public byte State;
}

public sealed class CartMapDebugSystem : ModSystem
{
	private const string ChannelName = "yangtransport_cartdebug";
	private const int UpdateTickMS = 1000;

	// Server
	private ICoreServerAPI? ServerAPI;
	private IServerNetworkChannel? ServerNetworkChannel;
	private RailGraphServerSystem? RailGraphSystem;
	private OffscreenConvoySimSystem? OffscreenSimulationSystem;

	private readonly HashSet<IServerPlayer> Subscribers = new();
	private readonly List<CartDebugMarker> MarkerScratchBuffer = new(256);
	private long ServerTickListenerID;

	// Client
	private ICoreClientAPI? ClientAPI;
	private IClientNetworkChannel? ClientNetworkChannel;
	private CartDebugMarker[] ClientMarkers = Array.Empty<CartDebugMarker>();

	private WorldMapManager? MapManager;
	private CartDebugMapLayer? MapLayer;

	public override bool ShouldLoad(EnumAppSide forSide) => true;

	public override void Start(ICoreAPI coreAPI)
	{
		if (coreAPI is ICoreServerAPI serverAPI) StartServer(serverAPI);
		if (coreAPI is ICoreClientAPI clientAPI) StartClient(clientAPI);
	}

	private void StartServer(ICoreServerAPI serverAPI)
	{
		ServerAPI = serverAPI;
		RailGraphSystem = serverAPI.ModLoader.GetModSystem<RailGraphServerSystem>();
		OffscreenSimulationSystem = serverAPI.ModLoader.GetModSystem<OffscreenConvoySimSystem>();

		ServerNetworkChannel = serverAPI.Network.RegisterChannel(ChannelName).RegisterMessageType<CartDebugToggle>().RegisterMessageType<CartDebugUpdate>();
		ServerNetworkChannel.SetMessageHandler<CartDebugToggle>(OnToggleFromClient);

		serverAPI.Event.PlayerLeave += OnPlayerLeave;
	}

	private void OnPlayerLeave(IServerPlayer player)
	{
		if (Subscribers.Remove(player)) { if (Subscribers.Count == 0) StopServerUpdates(); }
	}

	private void OnToggleFromClient(IServerPlayer fromPlayer, CartDebugToggle message)
	{
		if (ServerAPI == null || (message.Enabled && !fromPlayer.HasPrivilege(Privilege.controlserver))) return;

		if (message.Enabled) { if (Subscribers.Add(fromPlayer) && Subscribers.Count == 1) StartServerUpdates(); }
		else { if (Subscribers.Remove(fromPlayer) && Subscribers.Count == 0) StopServerUpdates(); }
	}

	private void StartServerUpdates()
	{
		if (ServerAPI == null || ServerTickListenerID != 0) return;
		ServerTickListenerID = ServerAPI.Event.RegisterGameTickListener(OnServerTick, UpdateTickMS);
	}

	private void StopServerUpdates()
	{
		if (ServerAPI == null || ServerTickListenerID == 0) return;
		ServerAPI.Event.UnregisterGameTickListener(ServerTickListenerID);
		ServerTickListenerID = 0;
	}

	private void OnServerTick(float dt)
	{
		if (ServerAPI == null || ServerNetworkChannel == null) return;
		if (Subscribers.Count == 0) return;

		var graph = RailGraphSystem?.Graph;
		if (graph == null) return;

		MarkerScratchBuffer.Clear();

		// Active (loaded) carts/locos
		foreach (var entity in ServerAPI.World.LoadedEntities.Values)
		{
			if (entity == null || !entity.Alive) continue;

			if (entity is EntityMinecart || entity is EntityStandardGaugeLocomotive)
			{
				MarkerScratchBuffer.Add(new CartDebugMarker
				{
					X = entity.ServerPos.X, Z = entity.ServerPos.Z,
					State = CartDebugMarker.StateActive
				});
			}
		}

		// Virtual (offscreen simulated) convoy heads
		OffscreenSimulationSystem?.DebugCollectVirtualMarkers(MarkerScratchBuffer, graph);

		var update = new CartDebugUpdate { Markers = MarkerScratchBuffer.ToArray() };

		foreach (var player in Subscribers) { if (player?.ConnectionState == EnumClientState.Playing) { ServerNetworkChannel.SendPacket(update, player); } }
	}

	private void StartClient(ICoreClientAPI clientAPI)
	{
		ClientAPI = clientAPI;

		ClientNetworkChannel = clientAPI.Network.RegisterChannel(ChannelName) .RegisterMessageType<CartDebugToggle>() .RegisterMessageType<CartDebugUpdate>();
		ClientNetworkChannel.SetMessageHandler<CartDebugUpdate>(OnUpdateFromServer);

		MapManager = clientAPI.ModLoader.GetModSystem<WorldMapManager>();

	}

	internal bool Enabled => MapLayer != null;

	internal bool SetEnabled(bool enable)
	{
		if (enable == Enabled) return Enabled;
		if (ClientAPI == null || ClientNetworkChannel == null) return false;

		if (enable)
		{
			if (MapManager == null) return false;

			MapLayer = new CartDebugMapLayer(ClientAPI, MapManager, this);
			MapManager.MapLayers.Add(MapLayer);
			MapLayer.OnLoaded();
		}
		else { RemoveMapLayer(); }

		ClientNetworkChannel.SendPacket(new CartDebugToggle { Enabled = Enabled });
		return Enabled;
	}

	private void RemoveMapLayer()
	{
		if (MapLayer == null) return;

		MapManager?.MapLayers.Remove(MapLayer);
		MapLayer.OnShutDown();
		MapLayer.Dispose();
		MapLayer = null;
		ClientMarkers = Array.Empty<CartDebugMarker>();
	}

	public override void Dispose()
	{
		if (ServerAPI != null)
		{
			ServerAPI.Event.PlayerLeave -= OnPlayerLeave;
			StopServerUpdates();
			Subscribers.Clear();
		}

		RemoveMapLayer();
		base.Dispose();
	}

	private void OnUpdateFromServer(CartDebugUpdate message)
	{
		if (!Enabled) return;
		ClientMarkers = message.Markers ?? Array.Empty<CartDebugMarker>();
	}

	internal CartDebugMarker[] GetClientMarkers() => ClientMarkers;
}

internal sealed class CartDebugMapLayer : MapLayer // Clientside
{
	private readonly ICoreClientAPI ClientAPI;
	private readonly CartMapDebugSystem DebugSystem;

	private Vec2f ViewPosition = new();
	private readonly Vec3d WorldPosition = new();

	private readonly int ColorActive = ColorUtil.ColorFromRgba(40, 120, 255, 255);
	private readonly int ColorVirtual = ColorUtil.ColorFromRgba(60, 220, 120, 255);

	public override string Title => "Carts (debug)";
	public override string LayerGroupCode => "entities";
	public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
	public override bool RequireChunkLoaded => false;

	public CartDebugMapLayer(ICoreClientAPI clientAPI, IWorldMapManager mapSink, CartMapDebugSystem debugSystem) : base(clientAPI, mapSink)
	{
		ClientAPI = clientAPI;
		this.DebugSystem = debugSystem;
		ZIndex = 3;
	}

	public override void Render(GuiElementMap mapElement, float dt)
	{
		if (!Active) return;

		var debugMarkers = DebugSystem.GetClientMarkers();
		if (debugMarkers == null || debugMarkers.Length == 0) return;

		float markerSize = 4f;

		for (int markerIndex = 0; markerIndex < debugMarkers.Length; markerIndex++)
		{
			ref readonly var marker = ref debugMarkers[markerIndex];

			WorldPosition.Set(marker.X, 0, marker.Z);
			mapElement.TranslateWorldPosToViewPos(WorldPosition, ref ViewPosition);

			float x = (float)(((GuiElement)mapElement).Bounds.renderX + ViewPosition.X);
			float y = (float)(((GuiElement)mapElement).Bounds.renderY + ViewPosition.Y);

			int color = marker.State == CartDebugMarker.StateVirtual ? ColorVirtual : ColorActive; 
			ClientAPI.Render.RenderRectangle(x - markerSize * 0.5f, y - markerSize * 0.5f, 70f, markerSize, markerSize, color);
		}
	}
}
