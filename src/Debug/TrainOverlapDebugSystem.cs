using System;
using System.Collections.Generic;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Client.NoObf;

namespace YangTransport;

[ProtoContract] public sealed class TrainOverlapDebugToggle { [ProtoMember(1)] public bool Enabled; }
[ProtoContract] public sealed class TrainOverlapDebugUpdate { [ProtoMember(1)] public TrainOverlapDebugBox[] Boxes = Array.Empty<TrainOverlapDebugBox>(); }

[ProtoContract] public sealed class TrainOverlapDebugBox
{
	public const byte StateActive = 0;
	public const byte StateVirtual = 1;

	[ProtoMember(1)] public double X1;
	[ProtoMember(2)] public double Y1;
	[ProtoMember(3)] public double Z1;
	[ProtoMember(4)] public double X2;
	[ProtoMember(5)] public double Y2;
	[ProtoMember(6)] public double Z2;
	[ProtoMember(7)] public byte State;
}

// Collision preview
public sealed class TrainOverlapDebugSystem : ModSystem, IRenderer, IDisposable
{
	private const string ChannelName = "yangtransport_trainoverlapdebug";
	private const int UpdateTickMS = 250;
	private const double MaximumRenderDistanceSquared = 128.0;
	private const double MaxRenderDistanceSq = MaximumRenderDistanceSquared * MaximumRenderDistanceSquared;

	private ICoreServerAPI? ServerAPI;
	private IServerNetworkChannel? ServerNetworkChannel;
	private RailTrainCollisionSystem? CollisionSystem;
	private readonly HashSet<IServerPlayer> Subscribers = new();
	private long ServerTickListenerID;

	private ICoreClientAPI? ClientAPI;
	private IClientNetworkChannel? ClientNetworkChannel;
	private WireframeCube? BoxWireframe;
	private TrainOverlapDebugBox[] ClientBoxes = Array.Empty<TrainOverlapDebugBox>();
	private bool IsEnabled;

	private readonly Vec4f ActiveColor = new(1f, 0.48f, 0.08f, 1f);
	private readonly Vec4f VirtualColor = new(0.2f, 1f, 0.35f, 1f);

	public override bool ShouldLoad(EnumAppSide forSide) => true;

	public double RenderOrder => 1.1;
	public int RenderRange => (int)MaximumRenderDistanceSquared;

	public override void Start(ICoreAPI coreAPI)
	{
		if (coreAPI is ICoreServerAPI serverAPI) StartServer(serverAPI);
		if (coreAPI is ICoreClientAPI clientAPI) StartClient(clientAPI);
	}

	private void StartServer(ICoreServerAPI serverAPI)
	{
		ServerAPI = serverAPI;
		CollisionSystem = serverAPI.ModLoader.GetModSystem<RailTrainCollisionSystem>();

		ServerNetworkChannel = serverAPI.Network.RegisterChannel(ChannelName)
			.RegisterMessageType<TrainOverlapDebugToggle>()
			.RegisterMessageType<TrainOverlapDebugUpdate>();

		ServerNetworkChannel.SetMessageHandler<TrainOverlapDebugToggle>(OnToggleFromClient);
		serverAPI.Event.PlayerLeave += OnPlayerLeave;
	}

	private void StartClient(ICoreClientAPI clientAPI)
	{
		ClientAPI = clientAPI;

		ClientNetworkChannel = clientAPI.Network.RegisterChannel(ChannelName)
			.RegisterMessageType<TrainOverlapDebugToggle>()
			.RegisterMessageType<TrainOverlapDebugUpdate>();

		ClientNetworkChannel.SetMessageHandler<TrainOverlapDebugUpdate>(OnUpdateFromServer);
	}

	public override void Dispose()
	{
		if (ServerAPI != null)
		{
			ServerAPI.Event.PlayerLeave -= OnPlayerLeave;
			if (ServerTickListenerID != 0) ServerAPI.Event.UnregisterGameTickListener(ServerTickListenerID);
		}

		CollisionSystem?.SetDebugCaptureEnabled(false);

		DisableClientRenderer();
		base.Dispose();
	}

	private void OnPlayerLeave(IServerPlayer player) { if (Subscribers.Remove(player) && Subscribers.Count == 0) StopServerUpdates(); }

	private void OnToggleFromClient(IServerPlayer fromPlayer, TrainOverlapDebugToggle message)
	{
		if (message.Enabled && !fromPlayer.HasPrivilege(Privilege.controlserver)) return;

		if (message.Enabled)	{ if (Subscribers.Add(fromPlayer) && Subscribers.Count == 1) StartServerUpdates(); }
		else					{ if (Subscribers.Remove(fromPlayer) && Subscribers.Count == 0) StopServerUpdates(); }
	}

	private void StartServerUpdates()
	{
		if (ServerAPI == null || ServerTickListenerID != 0) return;

		CollisionSystem?.SetDebugCaptureEnabled(true);
		ServerTickListenerID = ServerAPI.Event.RegisterGameTickListener(OnServerTick, UpdateTickMS);
	}

	private void StopServerUpdates()
	{
		if (ServerAPI == null) return;

		if (ServerTickListenerID != 0)
		{
			ServerAPI.Event.UnregisterGameTickListener(ServerTickListenerID);
			ServerTickListenerID = 0;
		}

		CollisionSystem?.SetDebugCaptureEnabled(false);
	}

	private void OnServerTick(float dt)
	{
		if (ServerNetworkChannel == null || Subscribers.Count == 0 || CollisionSystem == null) return;

		var update = new TrainOverlapDebugUpdate { Boxes = CollisionSystem.GetDebugBoxesSnapshot() };
		foreach (var player in Subscribers) { if (player?.ConnectionState == EnumClientState.Playing) { ServerNetworkChannel.SendPacket(update, player); } }
	}

	internal bool Enabled => IsEnabled;

	internal void SetEnabled(bool enable)
	{
		if (enable == IsEnabled || ClientNetworkChannel == null || ClientAPI == null) return;

		IsEnabled = enable;
		if (IsEnabled)
		{
			BoxWireframe = WireframeCube.CreateUnitCube(ClientAPI, -1);
			ClientAPI.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "yangtransport:trainoverlapdebug");
		}
		else
		{
			ClientBoxes = Array.Empty<TrainOverlapDebugBox>();
			DisableClientRenderer();
		}

		ClientNetworkChannel.SendPacket(new TrainOverlapDebugToggle { Enabled = IsEnabled });
	}

	private void DisableClientRenderer()
	{
		if (ClientAPI != null && BoxWireframe != null) { ClientAPI.Event.UnregisterRenderer(this, EnumRenderStage.Opaque); }

		BoxWireframe?.Dispose();
		BoxWireframe = null;
	}

	private void OnUpdateFromServer(TrainOverlapDebugUpdate message)
	{
		if (!IsEnabled) return;
		ClientBoxes = message.Boxes ?? Array.Empty<TrainOverlapDebugBox>();
	}

	public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
	{
		if (!IsEnabled || ClientAPI == null || BoxWireframe == null || ClientAPI.HideGuis) return;
		if (ClientBoxes.Length == 0) return;

		EntityPlayer? player = ClientAPI.World.Player?.Entity;
		if (player == null) return;

		Vec3d cameraPosition = player.CameraPos;

		for (int boxIndex = 0; boxIndex < ClientBoxes.Length; boxIndex++)
		{
			var box = ClientBoxes[boxIndex];

			double sx = Math.Max(0.01, box.X2 - box.X1);
			double sy = Math.Max(0.01, box.Y2 - box.Y1);
			double sz = Math.Max(0.01, box.Z2 - box.Z1);

			double cx = box.X1 + sx * 0.5;
			double cy = box.Y1 + sy * 0.5;
			double cz = box.Z1 + sz * 0.5;

			double dx = cx - cameraPosition.X;
			double dy = cy - cameraPosition.Y;
			double dz = cz - cameraPosition.Z;
			if (dx * dx + dy * dy + dz * dz > MaxRenderDistanceSq) continue;

			BoxWireframe.Render
			(
				ClientAPI,
				box.X1, box.Y1, box.Z1,
				(float)sx, (float)sy, (float)sz,
				1.8f,
				box.State == TrainOverlapDebugBox.StateVirtual ? VirtualColor : ActiveColor
			);
		}
	}
}
