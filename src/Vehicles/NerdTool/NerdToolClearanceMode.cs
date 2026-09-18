using System;
using System.Collections.Generic;
using System.IO;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace YangTransport;

[ProtoContract]
public sealed class NerdToolClearanceResultPacket
{
	[ProtoMember(1)] public int X;
	[ProtoMember(2)] public int Y;
	[ProtoMember(3)] public int Z;
	[ProtoMember(4)] public int Dimension;
	[ProtoMember(5)] public byte[] BlockerOffsets = Array.Empty<byte>();
}

// Tool for validating clearance of rails
public sealed class NerdToolClearanceMode : ModSystem, INerdToolModeHandler
{
	private const string ChannelName = "yangtransport_nerdtool_clearance";
	private const int BlockerHighlightSlot = 934302;
	private const long ClearResultDurationMS = 5000;
	private const long BlockedResultDurationMS = 10000;
	private const int ClientTickMS = 250; // red/white toggles at 4 Hz = 2 full blinks/sec

	private static readonly int ClearColor							= NerdToolTrackHighlightRenderer.HighlightColor(80, 80, 255, 80);
	private static readonly int BlockedColor						= NerdToolTrackHighlightRenderer.HighlightColor(88, 255, 64, 64);
	private static readonly List<int> BlockerRed		= new()		{ NerdToolTrackHighlightRenderer.HighlightColor(104, 255, 48, 48) };
	private static readonly List<int> BlockerWhite		= new()		{ NerdToolTrackHighlightRenderer.HighlightColor(104, 255, 255, 255) };
	private static readonly List<BlockPos> EmptyBlocks	= new();
	private static readonly List<int> EmptyColors		= new();

	private ICoreServerAPI? ServerAPI;
	private IServerNetworkChannel? ServerChannel;
	private RailGraphServerSystem? RailGraphServer;
	private readonly List<BlockPos> ServerBlockers = new();

	private ICoreClientAPI? ClientAPI;
	private NerdToolTrackHighlightRenderer? TrackHighlightRenderer;
	private readonly List<BlockPos> BlockerBlocks = new();

	private BlockPos? ResultSourcePos;
	private long ResultExpiresMS;
	private bool LastBlinkWhite;
	private long ClientTickListenerID;

	private MeshRef? BlockerWireMesh;
	private BlockPos? BlockerWireOrigin;
	private readonly Matrixf MeshModelView = new();

	public override bool ShouldLoad(EnumAppSide forSide) => true;

	public override void Start(ICoreAPI coreAPI)
	{
		if (coreAPI is ICoreServerAPI serverAPI)
		{
			ServerAPI = serverAPI;
			RailGraphServer = coreAPI.ModLoader.GetModSystem<RailGraphServerSystem>();
			ServerChannel = serverAPI.Network.RegisterChannel(ChannelName).RegisterMessageType<NerdToolClearanceResultPacket>();
		}

		if (coreAPI is ICoreClientAPI clientAPI)
		{
			ClientAPI = clientAPI;
			TrackHighlightRenderer = new NerdToolTrackHighlightRenderer(clientAPI);

			IClientNetworkChannel clientChannel = clientAPI.Network.RegisterChannel(ChannelName).RegisterMessageType<NerdToolClearanceResultPacket>();
			clientChannel.SetMessageHandler<NerdToolClearanceResultPacket>(OnClientResult);
		}
	}

	public NerdToolInteractionResult HandleInteract(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSelection, EntitySelection entitySelection)
	{
		if // It's MY code and I get to choose the indentation!
		(
			blockSelection?.Position == null || !NerdToolRailTarget.TryResolve
			(
				byEntity.World.BlockAccessor, blockSelection.Position,
				out BlockPos sourcePos, out Block sourceBlock,
				out TrackPieceSpec trackSpecification
			)
		) { return NerdToolInteractionResult.NotHandled; }

		if (byEntity.Api?.Side == EnumAppSide.Client)
		{
			return NerdToolInteractionResult.Success(sourcePos);
		}

		if (byEntity is not EntityPlayer entityPlayer || entityPlayer.Player is not IServerPlayer serverPlayer)
		{
			return NerdToolInteractionResult.HandledFailure(sourcePos);
		}

		return ServerMeasure(serverPlayer, sourcePos, sourceBlock, trackSpecification)
			? NerdToolInteractionResult.Success(sourcePos)
			: NerdToolInteractionResult.HandledFailure(sourcePos);
	}

	public void OnDeselected()
	{
		if (ClientAPI == null) return;

		ClearAllClientState();
		StopClientTick();
	}

	private bool ServerMeasure(IServerPlayer player, BlockPos sourcePos, Block sourceBlock, TrackPieceSpec trackSpec)
	{
		if (ServerChannel == null || player == null) return false;
		RailGraphServer ??= ServerAPI?.ModLoader.GetModSystem<RailGraphServerSystem>();
		if (RailGraphServer == null) return false;

		ServerBlockers.Clear();
		RailGraphServer.MeasureTrackClearanceNow(sourcePos, sourceBlock, trackSpec, ServerBlockers);

		ServerChannel.SendPacket(new NerdToolClearanceResultPacket
		{
			X = sourcePos.X, Y = sourcePos.Y, Z = sourcePos.Z,
			Dimension = sourcePos.dimension,
			BlockerOffsets = PackBlockerOffsets(sourcePos, ServerBlockers)
		}, player);

		return true;
	}

	public void ClientRender(ItemSlot slot, IClientPlayer player)
	{
		if (ClientAPI == null || player?.Entity == null || TrackHighlightRenderer == null) return;

		EnsureClientTick();
		TrackHighlightRenderer.UpdateHover(player, ResultSourcePos);
		TrackHighlightRenderer.Render(player);
		RenderBlockerWire(player);
	}

	private void OnClientResult(NerdToolClearanceResultPacket packet)
	{
		if (ClientAPI == null ||TrackHighlightRenderer == null || packet == null || packet.BlockerOffsets == null || packet.BlockerOffsets.Length % 6 != 0) { return; }


		BlockPos sourcePos = new(packet.X, packet.Y, packet.Z, packet.Dimension);
		Block sourceBlock = ClientAPI.World.BlockAccessor.GetBlock(sourcePos);
		if (!TrackSpecsDictionary.TryGet(sourceBlock, out _)) return;

		DecodeBlockerOffsets(sourcePos, packet.BlockerOffsets, BlockerBlocks);

		ResultSourcePos = sourcePos;
		ResultExpiresMS = ClientAPI.World.ElapsedMilliseconds +
			(BlockerBlocks.Count == 0 ? ClearResultDurationMS : BlockedResultDurationMS);
		LastBlinkWhite = false;

		TrackHighlightRenderer.SetResult(sourcePos, sourceBlock, BlockerBlocks.Count == 0 ? ClearColor : BlockedColor);
		TrackHighlightRenderer.InvalidateHoverSelection();

		if (BlockerBlocks.Count > 0)
		{
			ClientAPI.World.HighlightBlocks(ClientAPI.World.Player, BlockerHighlightSlot, BlockerBlocks, BlockerRed);
			BuildBlockerWireMesh();
		}
		else { ClearBlockers(); }

		EnsureClientTick();
	}

	private void OnClientTick(float _)
	{
		if (ClientAPI == null) return;

		ItemSlot? activeSlot = ClientAPI.World.Player?.InventoryManager?.ActiveHotbarSlot;
		bool isToolHeld = activeSlot?.Itemstack?.Collectible is ItemNerdTool && ItemNerdTool.GetSelectedMode(activeSlot) == NerdToolMode.Clearance;

		if (!isToolHeld)
		{
			ClearAllClientState();
			StopClientTick();
			return;
		}

		if (ResultSourcePos == null) return;

		long now = ClientAPI.World.ElapsedMilliseconds;
		if (now >= ResultExpiresMS)
		{
			ClearResult();
			TrackHighlightRenderer?.InvalidateHoverSelection();
			return;
		}

		if (BlockerBlocks.Count == 0) return;

		bool blinkWhite = ((now / ClientTickMS) & 1L) != 0;
		if (blinkWhite == LastBlinkWhite) return;

		LastBlinkWhite = blinkWhite;
		ClientAPI.World.HighlightBlocks(ClientAPI.World.Player, BlockerHighlightSlot, BlockerBlocks, blinkWhite ? BlockerWhite : BlockerRed);
	}

	private void EnsureClientTick()
	{
		if (ClientAPI == null || ClientTickListenerID != 0) return;
		ClientTickListenerID = ClientAPI.Event.RegisterGameTickListener(OnClientTick, ClientTickMS);
	}

	private void StopClientTick()
	{
		if (ClientAPI == null || ClientTickListenerID == 0) return;
		ClientAPI.Event.UnregisterGameTickListener(ClientTickListenerID);
		ClientTickListenerID = 0;
	}

	private void ClearResult()
	{
		ResultSourcePos = null;
		ResultExpiresMS = 0;
		TrackHighlightRenderer?.ClearResult();
		ClearBlockers();
	}

	private void ClearBlockers()
	{
		if (ClientAPI != null)
		{
			BlockerBlocks.Clear();
			ClientAPI.World.HighlightBlocks(ClientAPI.World.Player, BlockerHighlightSlot, EmptyBlocks, EmptyColors);
			DeleteMesh(ref BlockerWireMesh);
		}

		BlockerWireOrigin = null;
	}

	private void ClearAllClientState()
	{
		if (ClientAPI == null) return;

		ResultSourcePos = null;
		ResultExpiresMS = 0;
		TrackHighlightRenderer?.ClearAll();
		ClearBlockers();
	}

	private void BuildBlockerWireMesh()
	{
		if (ClientAPI == null) return;

		DeleteMesh(ref BlockerWireMesh);

		if (BlockerBlocks.Count == 0) { BlockerWireOrigin = null; return; }

		BlockerWireOrigin = BlockerBlocks[0].Copy();
		int color = NerdToolTrackHighlightRenderer.HighlightColor(255, 255, 32, 32);

		MeshData mesh = new(BlockerBlocks.Count * 24, BlockerBlocks.Count * 24, withNormals: false, withUv: false);
		mesh.SetMode(EnumDrawMode.Lines);

		for (int blockerIndex = 0; blockerIndex < BlockerBlocks.Count; blockerIndex++)
		{
			BlockPos blockerPos = BlockerBlocks[blockerIndex];
			float x = blockerPos.X - BlockerWireOrigin.X;
			float y = blockerPos.InternalY - BlockerWireOrigin.InternalY;
			float z = blockerPos.Z - BlockerWireOrigin.Z;
			WriteWireBox(mesh, x, y, z, x + 1, y + 1, z + 1, color);
		}

		BlockerWireMesh = ClientAPI.Render.UploadMesh(mesh);
	}

	private void RenderBlockerWire(IClientPlayer player)
	{
		if (ClientAPI == null || BlockerWireMesh == null || BlockerWireOrigin == null || ResultSourcePos == null) { return; }

		Vec3d camPos = player.Entity.CameraPos;
		IShaderProgram? previousShader = ClientAPI.Render.CurrentActiveShader;
		previousShader?.Stop();

		IShaderProgram shader = ClientAPI.Render.GetEngineShader(EnumShaderProgram.Autocamera);
		shader.Use();
		shader.UniformMatrix("projectionMatrix", ClientAPI.Render.CurrentProjectionMatrix);
		shader.UniformMatrix
		(
			"modelViewMatrix",
			MeshModelView
				.Set(ClientAPI.Render.CameraMatrixOriginf)
				.Translate( BlockerWireOrigin.X - camPos.X, BlockerWireOrigin.InternalY - camPos.Y, BlockerWireOrigin.Z - camPos.Z)
			.Values
		);

		ClientAPI.Render.LineWidth = 2.5f;
		ClientAPI.Render.RenderMesh(BlockerWireMesh);
		ClientAPI.Render.LineWidth = 1f;

		shader.Stop();
		previousShader?.Use();
	}

	private static void WriteWireBox(MeshData mesh, float x1, float y1, float z1, float x2, float y2, float z2, int color)
	{
		AddLine(mesh, x1, y1, z1, x2, y1, z1, color);
		AddLine(mesh, x2, y1, z1, x2, y1, z2, color);
		AddLine(mesh, x2, y1, z2, x1, y1, z2, color);
		AddLine(mesh, x1, y1, z2, x1, y1, z1, color);

		AddLine(mesh, x1, y2, z1, x2, y2, z1, color);
		AddLine(mesh, x2, y2, z1, x2, y2, z2, color);
		AddLine(mesh, x2, y2, z2, x1, y2, z2, color);
		AddLine(mesh, x1, y2, z2, x1, y2, z1, color);

		AddLine(mesh, x1, y1, z1, x1, y2, z1, color);
		AddLine(mesh, x2, y1, z1, x2, y2, z1, color);
		AddLine(mesh, x2, y1, z2, x2, y2, z2, color);
		AddLine(mesh, x1, y1, z2, x1, y2, z2, color);
	}

	private static void AddLine(MeshData mesh, float x1, float y1, float z1, float x2, float y2, float z2, int color)
	{
		int index = mesh.VerticesCount;
		mesh.AddVertexSkipTex(x1, y1, z1, color);
		mesh.AddIndex(index);
		mesh.AddVertexSkipTex(x2, y2, z2, color);
		mesh.AddIndex(index + 1);
	}

	private void DeleteMesh(ref MeshRef? meshReference)
	{
		if (ClientAPI == null || meshReference == null) return;
		ClientAPI.Render.DeleteMesh(meshReference);
		meshReference = null;
	}

	private static byte[] PackBlockerOffsets(BlockPos source, List<BlockPos> blockers)
	{
		if (blockers.Count == 0) return Array.Empty<byte>();

		byte[] packedOffsets = new byte[blockers.Count * 6];
		using MemoryStream memoryStream = new(packedOffsets, writable: true);
		using BinaryWriter writer = new(memoryStream);

		for (int blockerIndex = 0; blockerIndex < blockers.Count; blockerIndex++)
		{
			BlockPos blockerPos = blockers[blockerIndex];
			writer.Write(checked((short)(blockerPos.X - source.X)));
			writer.Write(checked((short)(blockerPos.Y - source.Y)));
			writer.Write(checked((short)(blockerPos.Z - source.Z)));
		}

		return packedOffsets;
	}

	private static void DecodeBlockerOffsets(BlockPos source, byte[] packedOffsets, List<BlockPos> blockerPositions)
	{
		blockerPositions.Clear();
		if (packedOffsets.Length == 0) return;

		using MemoryStream memoryStream = new(packedOffsets, writable: false);
		using BinaryReader reader = new(memoryStream);

		while (memoryStream.Position < memoryStream.Length)
		{
			blockerPositions.Add(new BlockPos(source.X + reader.ReadInt16(), source.Y + reader.ReadInt16(), source.Z + reader.ReadInt16(), source.dimension));
		}
	}

	public override void Dispose()
	{
		if (ClientAPI != null)
		{
			StopClientTick();
			ClearAllClientState();
			TrackHighlightRenderer?.Dispose();
			TrackHighlightRenderer = null;
		}

		ServerBlockers.Clear();
		base.Dispose();
	}
}
