using System;
using System.Collections.Generic;
using CollisionFlowFields;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace YangTransport;

// Diagonostic tool for players, more optimized and immersive than debug tools
public sealed class ItemNerdTool : Item
{
	private const string ToolModeAttribute = "toolMode";

	private SkillItem[] ToolModes = Array.Empty<SkillItem>();
	private NerdToolClearanceMode? ClearanceMode;
	private NerdToolSignalMode? SignalMode;

	public override void OnLoaded(ICoreAPI coreAPI)
	{
		base.OnLoaded(coreAPI);

		ClearanceMode = coreAPI.ModLoader.GetModSystem<NerdToolClearanceMode>();
		SignalMode = coreAPI.ModLoader.GetModSystem<NerdToolSignalMode>();

		ToolModes = new[]
		{
			new SkillItem
			{
				Code = new AssetLocation("clearance"),
				Name = Lang.Get("yangtransport:nerdtool-mode-clearance")
			},
			new SkillItem
			{
				Code = new AssetLocation("signal"),
				Name = Lang.Get("yangtransport:nerdtool-mode-signal")
			}
		};

		if (coreAPI is ICoreClientAPI clientAPI)
		{
			LoadModeIcon(clientAPI, ToolModes[0], "textures/icons/nerdtool-clearance.svg");
			LoadModeIcon(clientAPI, ToolModes[1], "textures/icons/nerdtool-signal.svg");
		}
	}

	public override void OnUnloaded(ICoreAPI coreAPI)
	{
		for (int toolModeIndex = 0; toolModeIndex < ToolModes.Length; toolModeIndex++) { ToolModes[toolModeIndex]?.Dispose(); }

		ToolModes = Array.Empty<SkillItem>();
		ClearanceMode = null;
		SignalMode = null;

		base.OnUnloaded(coreAPI);
	}

	public override void OnHeldRenderOpaque(ItemSlot inSlot, IClientPlayer byPlayer)
	{
		GetModeHandler(GetSelectedMode(inSlot))?.ClientRender(inSlot, byPlayer);
	}

	public override void OnHeldInteractStart(
		ItemSlot slot,
		EntityAgent byEntity,
		BlockSelection blockSelection,
		EntitySelection entitySelection,
		bool firstEvent,
		ref EnumHandHandling handling)
	{
		if (!firstEvent)
		{
			base.OnHeldInteractStart(slot, byEntity, blockSelection, entitySelection, firstEvent, ref handling);
			return;
		}

		INerdToolModeHandler? selectedModeHandler = GetModeHandler(GetSelectedMode(slot));
		NerdToolInteractionResult interactionResult = selectedModeHandler?.HandleInteract(slot, byEntity, blockSelection, entitySelection)
			?? NerdToolInteractionResult.NotHandled;

		if (!interactionResult.Handled) { base.OnHeldInteractStart(slot, byEntity, blockSelection, entitySelection, firstEvent, ref handling); return; }

		handling = EnumHandHandling.PreventDefaultAction;
		if (!interactionResult.Successful) return;

		if (byEntity.Api?.Side == EnumAppSide.Client)
		{
			string hitAnimation = GetHeldTpHitAnimation(slot, byEntity);
			byEntity.AnimManager?.ResetAnimation(hitAnimation);
			byEntity.AnimManager?.StartAnimation(hitAnimation);
			return;
		}

		if (interactionResult.FeedbackPosition != null)
		{
			byEntity.World.PlaySoundAt
			(
				new AssetLocation("game:sounds/effect/anvilhit"),
				interactionResult.FeedbackPosition,
				0.125,
				null,
				randomizePitch: true,
				range: 16f, volume: 0.35f
			);
		}

		if (interactionResult.DurabilityCost > 0)
		{
			DamageItem(byEntity.World, byEntity, slot, interactionResult.DurabilityCost);
		}
	}

	public override SkillItem[] GetToolModes(ItemSlot slot, IClientPlayer forPlayer, BlockSelection blockSelection) { return ToolModes; }

	public override int GetToolMode(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSelection)
	{
		int toolMode = slot?.Itemstack?.Attributes.GetInt(ToolModeAttribute, 0) ?? 0;
		return GameMath.Clamp(toolMode, 0, Math.Max(0, ToolModes.Length - 1));
	}

	public override void SetToolMode(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSelection, int toolMode)
	{
		if (slot?.Itemstack == null) return;

		int maxMode = Math.Max(0, ToolModes.Length - 1);
		toolMode = GameMath.Clamp(toolMode, 0, maxMode);

		NerdToolMode previousMode = GetSelectedMode(slot);
		NerdToolMode nextMode = ToolModeFromIndex(toolMode);

		slot.Itemstack.Attributes.SetInt(ToolModeAttribute, toolMode);

		if (previousMode != nextMode) { GetModeHandler(previousMode)?.OnDeselected(); }
	}

	public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inputSlot)
	{
		return new[]
		{
			new WorldInteraction
			{
				ActionLangCode = "Change tool mode",
				HotKeyCodes = new[] { "toolmodeselect" },
				MouseButton = EnumMouseButton.None
			}
		};
	}

	internal static NerdToolMode GetSelectedMode(ItemSlot? slot) { return ToolModeFromIndex(slot?.Itemstack?.Attributes.GetInt(ToolModeAttribute, 0) ?? 0); }

	private static NerdToolMode ToolModeFromIndex(int toolMode) { return toolMode == 1 ? NerdToolMode.Signal : NerdToolMode.Clearance; }

	private INerdToolModeHandler? GetModeHandler(NerdToolMode mode)
	{
		if (api == null) return null;

		switch (mode)
		{
			case NerdToolMode.Signal:
				SignalMode ??= api.ModLoader.GetModSystem<NerdToolSignalMode>();
			return SignalMode;

			default:
				ClearanceMode ??= api.ModLoader.GetModSystem<NerdToolClearanceMode>();
			return ClearanceMode;
		}
	}

	private void LoadModeIcon(ICoreClientAPI clientAPI, SkillItem toolMode, string iconPath)
	{
		AssetLocation iconAsset = new(Code.Domain, iconPath);

		// Keep the mode selector usable until the final SVG assets are added.
		if (!clientAPI.Assets.Exists(iconAsset))
		{
			toolMode.WithLetterIcon(clientAPI, toolMode.Name.Length > 0 ? toolMode.Name[0].ToString() : "?");
			return;
		}

		toolMode.WithIcon(clientAPI, clientAPI.Gui.LoadSvgWithPadding(iconAsset, 48, 48, 5, -1));
		toolMode.TexturePremultipliedAlpha = false;
	}
}

internal enum NerdToolMode : byte
{
	Clearance = 0,
	Signal = 1
}

public interface INerdToolModeHandler
{
	NerdToolInteractionResult HandleInteract
	(
		ItemSlot slot,
		EntityAgent byEntity,
		BlockSelection blockSelection,
		EntitySelection entitySelection
	);

	void ClientRender(ItemSlot slot, IClientPlayer player);
	void OnDeselected();
}

public readonly struct NerdToolInteractionResult
{
	internal static readonly NerdToolInteractionResult NotHandled = new(false, false, null, 0);

	internal bool Handled { get; }
	internal bool Successful { get; }
	internal BlockPos? FeedbackPosition { get; }
	internal int DurabilityCost { get; }

	private NerdToolInteractionResult(bool handled, bool successful, BlockPos? feedbackPosition, int durabilityCost)
	{
		Handled = handled;
		Successful = successful;
		FeedbackPosition = feedbackPosition;
		DurabilityCost = Math.Max(0, durabilityCost);
	}

	internal static NerdToolInteractionResult Success(BlockPos? feedbackPosition, int durabilityCost = 1)
	{
		return new NerdToolInteractionResult(true, true, feedbackPosition?.Copy(), durabilityCost);
	}

	internal static NerdToolInteractionResult HandledFailure(BlockPos? feedbackPosition)
	{
		return new NerdToolInteractionResult(true, false, feedbackPosition?.Copy(), 0);
	}
}

// Shared targeting code
internal static class NerdToolRailTarget
{
	private const int MaxFlowPointerSteps = 64;

	internal static bool TryResolve
	(
		IBlockAccessor blockAccessor,
		BlockPos selectedPos,
		out BlockPos sourcePos,
		out Block sourceBlock,
		out TrackPieceSpec trackSpecification)
	{
		sourcePos = selectedPos.Copy();
		sourceBlock = blockAccessor.GetBlock(sourcePos);

		if (TrackSpecsDictionary.TryGet(sourceBlock, out trackSpecification)) return true;
		if (sourceBlock is not BlockFlowCollider) { trackSpecification = default!; return false; }

		for (int pointerStepIndex = 0; pointerStepIndex < MaxFlowPointerSteps; pointerStepIndex++)
		{
			string? directionCode = sourceBlock.Variant?["dir"];
			BlockFacing? direction = string.IsNullOrEmpty(directionCode) ? null : BlockFacing.FromFirstLetter(directionCode) ?? BlockFacing.FromCode(directionCode);

			if (direction == null) { trackSpecification = default!; return false; }

			sourcePos.Add(direction);
			sourceBlock = blockAccessor.GetBlock(sourcePos);

			if (TrackSpecsDictionary.TryGet(sourceBlock, out trackSpecification)) return true;
			if (sourceBlock is not BlockFlowCollider) { trackSpecification = default!; return false; }
		}

		trackSpecification = default!;
		return false;
	}
}

// Shared highlight logic
internal sealed class NerdToolTrackHighlightRenderer : IDisposable
{
	private static readonly int[] UnitCubeTriangleIndices =
	{
		0, 2, 1, 0, 3, 2,
		4, 5, 6, 4, 6, 7,
		0, 4, 7, 0, 7, 3,
		1, 2, 6, 1, 6, 5,
		0, 1, 5, 0, 5, 4,
		3, 7, 6, 3, 6, 2,
	};

	internal static readonly int HoverColor = HighlightColor(48, 255, 255, 255);

	private readonly ICoreClientAPI ClientAPI;
	private readonly Dictionary<int, RailClearanceFootprint> ClearanceFootprints = new();
	private readonly Matrixf MeshModelView = new();

	private bool HasRawSelection;
	private int RawSelectionX;
	private int RawSelectionY;
	private int RawSelectionZ;
	private int RawSelectionDimension;

	private bool HasHoverSource;
	private BlockPos? HoverSourcePos;

	private MeshRef? HoverTrackMesh;
	private BlockPos? HoverTrackOrigin;
	private MeshRef? ResultTrackMesh;
	private BlockPos? ResultTrackOrigin;

	internal NerdToolTrackHighlightRenderer(ICoreClientAPI clientAPI) { this.ClientAPI = clientAPI; }

	internal void UpdateHover(IClientPlayer player, BlockPos? suppressedSourcePos)
	{
		BlockSelection? selection = player.CurrentBlockSelection;
		if (selection?.Position == null)
		{
			if (HasRawSelection || HasHoverSource) ClearHover();
			HasRawSelection = false;
			return;
		}

		BlockPos rawPos = selection.Position;
		if (HasRawSelection && rawPos.X == RawSelectionX && rawPos.Y == RawSelectionY && rawPos.Z == RawSelectionZ && rawPos.dimension == RawSelectionDimension) { return; }

		HasRawSelection = true;
		RawSelectionX = rawPos.X;
		RawSelectionY = rawPos.Y;
		RawSelectionZ = rawPos.Z;
		RawSelectionDimension = rawPos.dimension;

		if (!NerdToolRailTarget.TryResolve(ClientAPI.World.BlockAccessor, rawPos, out BlockPos sourcePos, out Block sourceBlock, out _)) { ClearHover(); return; }

		HasHoverSource = true;
		HoverSourcePos = sourcePos.Copy();

		if (suppressedSourcePos != null && suppressedSourcePos.Equals(sourcePos))
		{
			ClearHoverHighlightOnly();
			return;
		}

		BuildTrackHighlightMesh(sourcePos, sourceBlock, HoverColor, ref HoverTrackMesh, ref HoverTrackOrigin);
	}

	internal void SetResult(BlockPos sourcePos, Block sourceBlock, int color)
	{
		BuildTrackHighlightMesh(sourcePos, sourceBlock, color, ref ResultTrackMesh, ref ResultTrackOrigin);
	}

	internal void ClearResult()
	{
		DeleteMesh(ref ResultTrackMesh);
		ResultTrackOrigin = null;
	}

	internal void ClearAll()
	{
		HasRawSelection = false;
		ClearHover();
		ClearResult();
	}

	internal void InvalidateHoverSelection() { HasRawSelection = false; }

	internal void Render(IClientPlayer player)
	{
		if (HoverTrackMesh == null && ResultTrackMesh == null) return;

		Vec3d camPos = player.Entity.CameraPos;
		IShaderProgram? previousShader = ClientAPI.Render.CurrentActiveShader;
		previousShader?.Stop();

		IShaderProgram shader = ClientAPI.Render.GetEngineShader(EnumShaderProgram.Autocamera);
		shader.Use();
		shader.UniformMatrix("projectionMatrix", ClientAPI.Render.CurrentProjectionMatrix);

		try
		{
			ClientAPI.Render.GlDisableCullFace();
			ClientAPI.Render.GlToggleBlend(true);
			ClientAPI.Render.GLDepthMask(false);

			RenderTrackMesh(shader, HoverTrackMesh, HoverTrackOrigin, camPos);
			RenderTrackMesh(shader, ResultTrackMesh, ResultTrackOrigin, camPos);
		}
		finally
		{
			ClientAPI.Render.GLDepthMask(true);
			ClientAPI.Render.GlToggleBlend(false);
			ClientAPI.Render.GlEnableCullFace();
		}

		shader.Stop();
		previousShader?.Use();
	}

	private void BuildTrackHighlightMesh(BlockPos sourcePos, Block sourceBlock, int color, ref MeshRef? meshReference, ref BlockPos? meshOrigin)
	{
		DeleteMesh(ref meshReference);
		meshOrigin = sourcePos.Copy();

		MeshData highlightMesh = new(64, 96, withNormals: false, withUv: false);
		highlightMesh.SetMode(EnumDrawMode.Triangles);

		AddSelectionBoxes(highlightMesh, sourcePos, sourceBlock, sourcePos, color);

		if (!ClearanceFootprints.TryGetValue(sourceBlock.Id, out RailClearanceFootprint? footprint))
		{
			footprint = RailClearanceFootprint.ForBlock(sourceBlock);
			ClearanceFootprints[sourceBlock.Id] = footprint;
		}

		for (int columnIndex = 0; columnIndex < footprint.Columns.Length; columnIndex++)
		{
			RailClearanceColumn clearanceColumn = footprint.Columns[columnIndex];
			for (int y = clearanceColumn.MinY; y <= clearanceColumn.MaxY; y++)
			{
				if (clearanceColumn.X == 0 && clearanceColumn.Z == 0 && y == 0) continue;

				BlockPos blockPos = new(sourcePos.X + clearanceColumn.X, sourcePos.Y + y, sourcePos.Z + clearanceColumn.Z, sourcePos.dimension);

				Block clearanceBlock = ClientAPI.World.BlockAccessor.GetBlock(blockPos);
				if (clearanceBlock.Code?.Domain == "collisionflowfields") { AddSelectionBoxes(highlightMesh, sourcePos, clearanceBlock, blockPos, color); }
			}
		}

		if (highlightMesh.VerticesCount > 0) { meshReference = ClientAPI.Render.UploadMesh(highlightMesh); }
	}

	private void AddSelectionBoxes(MeshData mesh, BlockPos origin, Block block, BlockPos blockPos, int color)
	{
		Cuboidf[] selectionBoxes = block.GetSelectionBoxes(ClientAPI.World.BlockAccessor, blockPos);
		if (selectionBoxes == null) return;

		float ox = blockPos.X - origin.X;
		float oy = blockPos.Y - origin.Y;
		float oz = blockPos.Z - origin.Z;

		for (int boxIndex = 0; boxIndex < selectionBoxes.Length; boxIndex++)
		{
			Cuboidf selectionBox = selectionBoxes[boxIndex];
			AddSolidBox(mesh, ox + selectionBox.X1, oy + selectionBox.Y1, oz + selectionBox.Z1, ox + selectionBox.X2, oy + selectionBox.Y2, oz + selectionBox.Z2, color);
		}
	}

	private static void AddSolidBox(MeshData mesh, float x1, float y1, float z1, float x2, float y2, float z2, int color)
	{
		const float Epsilon = 0.0015f;
		x1 -= Epsilon; y1 -= Epsilon; z1 -= Epsilon; x2 += Epsilon; y2 += Epsilon; z2 += Epsilon;

		int v = mesh.VerticesCount;
		mesh.AddVertexSkipTex(x1, y1, z1, color);
		mesh.AddVertexSkipTex(x2, y1, z1, color);
		mesh.AddVertexSkipTex(x2, y2, z1, color);
		mesh.AddVertexSkipTex(x1, y2, z1, color);
		mesh.AddVertexSkipTex(x1, y1, z2, color);
		mesh.AddVertexSkipTex(x2, y1, z2, color);
		mesh.AddVertexSkipTex(x2, y2, z2, color);
		mesh.AddVertexSkipTex(x1, y2, z2, color);

		for (int triangleIndex = 0; triangleIndex < UnitCubeTriangleIndices.Length; triangleIndex++) { mesh.AddIndex(v + UnitCubeTriangleIndices[triangleIndex]); }
	}

	private void RenderTrackMesh(IShaderProgram shader, MeshRef? meshReference, BlockPos? meshOrigin, Vec3d camPos)
	{
		if (meshReference == null || meshOrigin == null) return;

		shader.UniformMatrix
		(
			"modelViewMatrix", MeshModelView
				.Set(ClientAPI.Render.CameraMatrixOriginf)
				.Translate(meshOrigin.X - camPos.X, meshOrigin.InternalY - camPos.Y, meshOrigin.Z - camPos.Z)
			.Values
		);

		ClientAPI.Render.RenderMesh(meshReference);
	}

	private void ClearHover()
	{
		HasHoverSource = false;
		HoverSourcePos = null;
		ClearHoverHighlightOnly();
	}

	private void ClearHoverHighlightOnly()
	{
		DeleteMesh(ref HoverTrackMesh);
		HoverTrackOrigin = null;
	}

	private void DeleteMesh(ref MeshRef? meshReference)
	{
		if (meshReference == null) return;
		ClientAPI.Render.DeleteMesh(meshReference);
		meshReference = null;
	}

	internal static int HighlightColor(int a, int r, int g, int b) { return ColorUtil.ToRgba(a, b, g, r); }

	public void Dispose()
	{
		ClearAll();
		ClearanceFootprints.Clear();
	}
}
