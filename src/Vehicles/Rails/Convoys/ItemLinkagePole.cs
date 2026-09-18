using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Server;

namespace YangTransport;

/// Linkage pole item used for convoy coupling/uncoupling interactions. Has lots of fancy highlights for UX.
public sealed class ItemLinkagePole : Item
{
	private ICoreClientAPI? ClientAPI;
	private ICoreServerAPI? ServerAPI;
	private LinkagePoleConvoyBoxRenderer? ConvoyBoxRenderer;
	private LinkagePoleSelectionHud? SelectionHUD;
	private long ClientSelectedVehicleID;
	private long ClientSelectedConvoyAnchorID;

	public override void OnLoaded(ICoreAPI coreAPI)
	{
		base.OnLoaded(coreAPI);
		ClientAPI = coreAPI as ICoreClientAPI;
		if (ClientAPI != null)
		{
			ClientAPI.Event.AfterActiveSlotChanged += OnClientActiveSlotChanged;

			SelectionHUD = new LinkagePoleSelectionHud(ClientAPI, this);
			SelectionHUD.TryOpen(false);
		}

		ServerAPI = coreAPI as ICoreServerAPI;
		if (ServerAPI != null) { ServerAPI.Event.AfterActiveSlotChanged += OnServerActiveSlotChanged; }
	}

	public override void OnUnloaded(ICoreAPI coreAPI)
	{
		if (ClientAPI != null) { ClientAPI.Event.AfterActiveSlotChanged -= OnClientActiveSlotChanged; }
		if (ServerAPI != null) { ServerAPI.Event.AfterActiveSlotChanged -= OnServerActiveSlotChanged; }


		ConvoyBoxRenderer?.Dispose();
		ConvoyBoxRenderer = null;
		SelectionHUD?.TryClose();
		SelectionHUD?.Dispose();
		SelectionHUD = null;
		ClientSelectedVehicleID = 0;
		ClientSelectedConvoyAnchorID = 0;
		ClientAPI = null; ServerAPI = null;
		base.OnUnloaded(coreAPI);
	}

	public override void OnHeldRenderOpaque(ItemSlot inventorySlot, IClientPlayer player)
	{
		if (ClientAPI == null) return;

		ConvoyBoxRenderer ??= new LinkagePoleConvoyBoxRenderer(ClientAPI);
		ConvoyBoxRenderer.Render(ClientSelectedVehicleID);
	}

	public override void OnHeldInteractStart
	(
		ItemSlot slot, EntityAgent byEntity, BlockSelection blockSelection,
		EntitySelection entitySelection, bool firstEvent, ref EnumHandHandling handling
	)
	{
		if (firstEvent && ClientAPI != null && entitySelection?.Entity is IRailwayConvoyVehicle clickedVehicle)
		{
			ClientHandleLinkagePoleTetherClick(clickedVehicle.Entity, byEntity);
		}

		base.OnHeldInteractStart(slot, byEntity, blockSelection, entitySelection, firstEvent, ref handling);
	}

	public override void OnHeldAttackStart
	(
		ItemSlot slot, EntityAgent byEntity, BlockSelection blockSelection,
		EntitySelection entitySelection, ref EnumHandHandling handling
	)
	{
		// Left-click cancels the pending selection
		if (byEntity.Api?.Side == EnumAppSide.Server) ConvoyTethering.ClearLinkagePoleSelection(byEntity);
		else ClearClientSelection();

		if (entitySelection?.Entity is IRailwayConvoyVehicle clickedVehicle)
		{
			// Vanilla left-click entity interaction validates against the entity's normal SelectionBox before sending an EntityInteraction packet.
			// SG forwarding hitboxes are meant to live outside that box, so linkage-pole untether gotta use the held-item path instead.
			if (byEntity.Api?.Side == EnumAppSide.Server) ConvoyTethering.HandleLinkagePoleUntetherClick(clickedVehicle, byEntity, slot);

			handling = EnumHandHandling.PreventDefaultAction; return;
		}

		base.OnHeldAttackStart(slot, byEntity, blockSelection, entitySelection, ref handling);
	}

	public override bool OnHeldAttackStep(float secondsPassed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSelection, EntitySelection entitySelection)
	{
		return entitySelection?.Entity is IRailwayConvoyVehicle ? false : base.OnHeldAttackStep(secondsPassed, slot, byEntity, blockSelection, entitySelection);
	}

	private void ClientHandleLinkagePoleTetherClick(Entity clickedVehicle, EntityAgent byEntity)
	{
		if (byEntity.Api?.Side != EnumAppSide.Client) return;

		long clickedConvoyAnchor = ResolveClientConvoyAnchor(clickedVehicle);

		// Mirror the server's double click linkage pole state using transient client fields only.
		if (ClientSelectedConvoyAnchorID == 0)
		{
			ClientSelectedConvoyAnchorID = clickedConvoyAnchor;
			ClientSelectedVehicleID = clickedVehicle.EntityId;
			SelectionHUD?.SetSelection(clickedVehicle);
			return;
		}

		if (byEntity.Controls.Sneak) { ClearClientSelection(); return; }

		// The server rejects a second click anywhere on the same convoy and keeps the first selection.
		if (ClientSelectedConvoyAnchorID == clickedConvoyAnchor) return;

		// A different convoy consumes the pending selection, whether or not the server-side merge succeeds.
		ClearClientSelection();
	}

	private void ClearClientSelection()
	{
		ClientSelectedVehicleID = 0;
		ClientSelectedConvoyAnchorID = 0;
		SelectionHUD?.SetSelection(null);
	}

	internal static long ResolveClientConvoyAnchor(Entity vehicleEntity)
	{
		Entity currentVehicle = vehicleEntity;
		if (!TryGetClientPreviousVehicleID(currentVehicle, out long previousVehicleID)) return currentVehicle.EntityId;

		const int maxSteps = 32;
		int traversalSteps = 0;

		while (previousVehicleID != 0 && traversalSteps++ < maxSteps)
		{
			if (previousVehicleID == currentVehicle.EntityId) break;

			Entity previousVehicle = currentVehicle.World.GetEntityById(previousVehicleID);
			if (previousVehicle == null || !TryGetClientPreviousVehicleID(previousVehicle, out long nextPreviousVehicleID)) break;

			currentVehicle = previousVehicle;
			previousVehicleID = nextPreviousVehicleID;
		}

		return currentVehicle.EntityId;
	}

	internal static bool TryGetClientPreviousVehicleID(Entity entity, out long previousVehicleID)
	{
		switch (entity)
		{
			case EntityMinecart minecart:
				previousVehicleID = minecart.PreviousCartID;
			return true;

			case EntityStandardGaugeLocomotive standardGaugeVehicle:
				previousVehicleID = standardGaugeVehicle.PrevVehicleID;
			return true;

			default:
				previousVehicleID = 0;
			return false;
		}
	}

	private void OnClientActiveSlotChanged(ActiveSlotChangeEventArgs eventArguments)
	{
		if (ClientAPI?.World.Player == null) return;

		IInventory hotbarInventory = ClientAPI.World.Player.InventoryManager.GetHotbarInventory();

		if (!IsThisPole(hotbarInventory, eventArguments.FromSlot)) return;
		if (IsThisPole(hotbarInventory, eventArguments.ToSlot)) return;

		ClearClientSelection();
	}

	private void OnServerActiveSlotChanged(IServerPlayer player, ActiveSlotChangeEventArgs eventArguments)
	{
		IInventory hotbarInventory = player.InventoryManager.GetHotbarInventory();

		if (!IsThisPole(hotbarInventory, eventArguments.FromSlot)) return;
		if (IsThisPole(hotbarInventory, eventArguments.ToSlot)) return;

		ConvoyTethering.ClearLinkagePoleSelection(player.Entity);
	}

	private bool IsThisPole(IInventory inventory, int slotID)
	{
		return slotID >= 0 && slotID < inventory.Count && inventory[slotID].Itemstack?.Collectible == this;
	}
}

// Small client-side HUD card showing the linkage pole's pending selection.
internal sealed class LinkagePoleSelectionHud : HudElement
{
	private const string TextKey = "selection";
	private const double MinimumFontSize = 8;
	private readonly ItemLinkagePole Owner;
	private readonly double DefaultFontSize;

	public override string ToggleKeyCombinationCode => null;
	public override bool Focusable => false;
	public override double DrawOrder => 0.11;

	public LinkagePoleSelectionHud(ICoreClientAPI coreClientAPI, ItemLinkagePole linkagePole) : base(coreClientAPI)
	{
		this.Owner = linkagePole;

		ElementBounds dialogBounds = ElementBounds.Fixed(EnumDialogArea.CenterBottom, 0, 0, 440, 34).WithFixedAlignmentOffset(0, -105);

		ElementBounds overlayBounds = ElementBounds.Fill.FlatCopy();
		ElementBounds textBounds = ElementBounds.Fixed(8, 5, 424, 24);
		CairoFont textFont = CairoFont.WhiteSmallishText().WithOrientation(EnumTextOrientation.Center);
		DefaultFontSize = textFont.UnscaledFontsize;

		SingleComposer = coreClientAPI.Gui
			.CreateCompo("yangtransport-linkagepole-selection-" + linkagePole.Id, dialogBounds)
			.AddGameOverlay(overlayBounds)
			.AddDynamicText(SelectionText(null), textFont, textBounds, TextKey)
			.Compose();
	}

	public void SetSelection(Entity? entity)
	{
		string text = SelectionText(entity);
		GuiElementDynamicText? textElement = SingleComposer?.GetDynamicText(TextKey);
		if (textElement == null) return;

		textElement.Font.UnscaledFontsize = DefaultFontSize;
		textElement.Font.AutoFontSize(text, textElement.Bounds);
		textElement.Font.UnscaledFontsize = Math.Max(MinimumFontSize, textElement.Font.UnscaledFontsize);
		textElement.SetNewText(text, forceRedraw: true);
	}

	private static string SelectionText(Entity? entity)
	{
		string? name = entity?.GetName();
		if (string.IsNullOrWhiteSpace(name))
		{
			name = entity == null
				? Lang.Get("yangtransport:linkagepole-selection-none")
				: entity.Code?.ToString() ?? Lang.Get("yangtransport:linkagepole-selection-unknown");
		}

		return Lang.Get("yangtransport:linkagepole-selection-current", name);
	}

	public override bool ShouldReceiveRenderEvents()
	{
		if (!IsOpened()) return false;

		EntityPlayer? player = capi.World.Player?.Entity;
		return IsOwnerPole(player?.RightHandItemSlot) || IsOwnerPole(player?.LeftHandItemSlot);
	}

	public override bool ShouldReceiveKeyboardEvents() => false;
	public override bool ShouldReceiveMouseEvents() => false;

	private bool IsOwnerPole(ItemSlot? slot) { return slot?.Itemstack?.Collectible == Owner; }
}

/// Client-only renderer for linkage pole, draws per-convoy colored wire boxes around nearby rail vehicles.
/// Runs only while the linkage pole is held (invoked from ItemLinkagePole.OnHeldRenderOpaque).
internal sealed class LinkagePoleConvoyBoxRenderer : IDisposable
{
	private const int RenderRange = 48;
	private const int MaxCarts = 128;

	private const int MaxAutomaticStandardGaugeForwardingHitboxes = 32;
	private const int MaxForwardingHitboxBoxes = MaxCarts * MaxAutomaticStandardGaugeForwardingHitboxes + 1;
	private const int VerticesPerForwardingHitboxBox = 8;
	private const int IndicesPerForwardingHitboxBox = 36;
	private const int MaxForwardingHitboxVertices = MaxForwardingHitboxBoxes * VerticesPerForwardingHitboxBox;
	private const int MaxForwardingHitboxIndices = MaxForwardingHitboxBoxes * IndicesPerForwardingHitboxBox;
	private const long ForwardingHitboxFlashPeriodMS = 500;
	private const int ForwardingHitboxAlpha = 160;
	private const int SelectedVehicleAlpha = 88;
	private const float ForwardingHitboxDepthBiasFactor = -8f;
	private const float ForwardingHitboxDepthBiasUnits = -4096f;

	private const int EdgesPerBox = 12;
	private const int VerticesPerEdge = 2;
	private const int MaxVertices = MaxCarts * EdgesPerBox * VerticesPerEdge;

	private readonly ICoreClientAPI ClientAPI;
	private MeshRef? WireMeshReference;
	private readonly MeshData WireMeshData;

	private MeshRef? ForwardingHitboxMeshReference;
	private readonly MeshData ForwardingHitboxMeshData;
	private readonly Matrixf ForwardingHitboxBoxMatrix = new();

	private readonly Random RandomGenerator = new();

	// Convoy key is assigned color (ARGB int)
	private readonly Dictionary<long, int> ConvoyColors = new();

	// Palette assignment works by simply handing out unique palette colors until we run out.
	private int PaletteAssignedCount;
	private readonly int[] PaletteOrder;

	// High-contrast tertiary palette (ARGB)
	private static readonly int[] TertiaryPalette = new[]
	{
		ColorUtil.ToRgba(255, 255, 128, 0),		// orange
		ColorUtil.ToRgba(255, 255, 64, 160),	// pink-magenta
		ColorUtil.ToRgba(255, 160, 64, 255),	// violet
		ColorUtil.ToRgba(255, 64, 128, 255),	// blue
		ColorUtil.ToRgba(255, 64, 220, 255),	// cyan
		ColorUtil.ToRgba(255, 64, 255, 160),	// aquamarine
		ColorUtil.ToRgba(255, 64, 255, 64),		// green
		ColorUtil.ToRgba(255, 200, 255, 64),	// chartreuse
		ColorUtil.ToRgba(255, 255, 220, 64),	// yellow
		ColorUtil.ToRgba(255, 255, 64, 64),		// red
		ColorUtil.ToRgba(255, 255, 160, 64),	// orange-yellow
		ColorUtil.ToRgba(255, 64, 255, 220),	// teal
		ColorUtil.ToRgba(255, 220, 64, 255),	// purple
		ColorUtil.ToRgba(255, 64, 160, 255),	// azure (but not the evil SAAS one)
		ColorUtil.ToRgba(255, 160, 255, 64),	// lime
		ColorUtil.ToRgba(255, 64, 64, 255),		// deep blue
	};

	private static readonly int[] UnitCubeTriangleIndices =
	{
		0, 2, 1, 0, 3, 2,
		4, 5, 6, 4, 6, 7,
		0, 4, 7, 0, 7, 3,
		1, 2, 6, 1, 6, 5,
		0, 1, 5, 0, 5, 4,
		3, 7, 6, 3, 6, 2,
	};

	public LinkagePoleConvoyBoxRenderer(ICoreClientAPI coreClientAPI)
	{
		this.ClientAPI = coreClientAPI;

		// Pre-shuffle palette indices so we get random unique assignment quickly.
		PaletteOrder = new int[TertiaryPalette.Length];
		for (int paletteIndex = 0; paletteIndex < PaletteOrder.Length; paletteIndex++) PaletteOrder[paletteIndex] = paletteIndex;
		for (int shuffleIndex = PaletteOrder.Length - 1; shuffleIndex > 0; shuffleIndex--)
		{
			int swapIndex = RandomGenerator.Next(shuffleIndex + 1);
			(PaletteOrder[shuffleIndex], PaletteOrder[swapIndex]) = (PaletteOrder[swapIndex], PaletteOrder[shuffleIndex]);
		}

		// Static mesh, 2 verts per segment, line list indices [0-N)
		WireMeshData = new MeshData(MaxVertices, MaxVertices, withNormals: false, withUv: false);
		WireMeshData.SetMode(EnumDrawMode.Lines);

		for (int vertexIndex = 0; vertexIndex < MaxVertices; vertexIndex++)
		{
			WireMeshData.AddVertexSkipTex(0, 0, 0, ColorUtil.WhiteArgb);
			WireMeshData.AddIndex(vertexIndex);
		}

		WireMeshReference = coreClientAPI.Render.UploadMesh(WireMeshData);

		ForwardingHitboxMeshData = new MeshData(MaxForwardingHitboxVertices, MaxForwardingHitboxIndices, withNormals: false, withUv: false);
		ForwardingHitboxMeshData.SetMode(EnumDrawMode.Triangles);
		ForwardingHitboxMeshData.XyzStatic = false;
		ForwardingHitboxMeshData.RgbaStatic = false;
		ForwardingHitboxMeshData.IndicesStatic = true;
		ForwardingHitboxMeshData.FlagsStatic = true;
		BuildForwardingHitboxCubeIndices();

		// Upload the full reusable buffer once, each frame only changes the logical counts and vertex payload.
		ForwardingHitboxMeshData.VerticesCount = MaxForwardingHitboxVertices;
		ForwardingHitboxMeshData.IndicesCount = MaxForwardingHitboxIndices;
		ForwardingHitboxMeshReference = coreClientAPI.Render.UploadMesh(ForwardingHitboxMeshData);
	}

	public void Dispose()
	{
		if (WireMeshReference != null)
		{
			ClientAPI.Render.DeleteMesh(WireMeshReference);
			WireMeshReference = null;
		}

		if (ForwardingHitboxMeshReference != null)
		{
			ClientAPI.Render.DeleteMesh(ForwardingHitboxMeshReference);
			ForwardingHitboxMeshReference = null;
		}
	}

	public void Render(long selectedVehicleID)
	{
		if (WireMeshReference == null) return;

		EntityPlayer? player = ClientAPI.World.Player?.Entity;
		if (player == null) return;

		Vec3d cameraPosition = player.CameraPos;

		double renderRangeSQ = RenderRange * RenderRange;
		int vertexCount = 0;
		int forwardingHitboxBoxCount = 0;
		int vehicleCount = 0;
		bool showForwardingHitboxFlash = ForwardingHitboxMeshReference != null && ((ClientAPI.World.ElapsedMilliseconds / ForwardingHitboxFlashPeriodMS) & 1L) == 0L;
		Entity? selectedVehicle = selectedVehicleID == 0 ? null : ClientAPI.World.GetEntityById(selectedVehicleID);

		// IMPORTANT: Avoid GetEntitiesAround(), it allocates at frame-rate.
		foreach (var entityEntry in ClientAPI.World.LoadedEntities)
		{
			if (vertexCount >= MaxVertices || vehicleCount >= MaxCarts) break;

			Entity vehicleEntity = entityEntry.Value;
			if (!ItemLinkagePole.TryGetClientPreviousVehicleID(vehicleEntity, out _)) continue;

			double dx = vehicleEntity.Pos.X - cameraPosition.X;
			double dy = vehicleEntity.Pos.Y - cameraPosition.Y;
			double dz = vehicleEntity.Pos.Z - cameraPosition.Z;
			if (dx * dx + dy * dy + dz * dz > renderRangeSQ) continue;

			// IMPORTANT: On the client, ConvoyHeadEntityId is not synced.
			// Instead, we derive the convoy "root" by walking the PrevCartID tether chain, which IS synced for visuals.
			long convoyKey = ItemLinkagePole.ResolveClientConvoyAnchor(vehicleEntity);

			int color = GetOrAssignColor(convoyKey);

			if (vehicleEntity is EntityStandardGaugeLocomotive standardGaugeVehicle)
			{
				WriteStandardGaugeVehicleBox(ref vertexCount, standardGaugeVehicle, cameraPosition, color);
				if (showForwardingHitboxFlash && forwardingHitboxBoxCount < MaxForwardingHitboxBoxes)
				{
					WriteStandardGaugeForwardingHitboxes(ref forwardingHitboxBoxCount, standardGaugeVehicle, cameraPosition, WithAlpha(color, ForwardingHitboxAlpha));
				}
			}
			else
			{
				Cuboidf box = vehicleEntity.SelectionBox;

				float x1 = (float)(vehicleEntity.Pos.X + box.X1 - cameraPosition.X);
				float y1 = (float)(vehicleEntity.Pos.Y + box.Y1 - cameraPosition.Y);
				float z1 = (float)(vehicleEntity.Pos.Z + box.Z1 - cameraPosition.Z);

				float x2 = (float)(vehicleEntity.Pos.X + box.X2 - cameraPosition.X);
				float y2 = (float)(vehicleEntity.Pos.Y + box.Y2 - cameraPosition.Y);
				float z2 = (float)(vehicleEntity.Pos.Z + box.Z2 - cameraPosition.Z);

				WriteBox(ref vertexCount, x1, y1, z1, x2, y2, z2, color);
			}

			vehicleCount++;
		}

		WriteSelectedVehicleSolidBox(ref forwardingHitboxBoxCount, selectedVehicle, cameraPosition, renderRangeSQ);

		if (vertexCount == 0 && forwardingHitboxBoxCount == 0) return;

		// Draw using engine shader that supports vertex colors (no need to bucket by convoy).
		IShaderProgram? previousShader = ClientAPI.Render.CurrentActiveShader;
		previousShader?.Stop();

		var shader = ClientAPI.Render.GetEngineShader(EnumShaderProgram.Autocamera);
		shader.Use();
		shader.UniformMatrix("projectionMatrix", ClientAPI.Render.CurrentProjectionMatrix);
		shader.UniformMatrix("modelViewMatrix", ClientAPI.Render.CameraMatrixOriginf);

		RenderForwardingHitboxMesh(forwardingHitboxBoxCount);
		RenderWireMesh(vertexCount);

		shader.Stop();
		previousShader?.Use();
	}

	private void WriteSelectedVehicleSolidBox(ref int forwardingHitboxBoxCount, Entity? selectedVehicle, Vec3d cameraPosition, double maxDistanceSQ)
	{
		if (selectedVehicle == null || forwardingHitboxBoxCount >= MaxForwardingHitboxBoxes) return;
		if (!ItemLinkagePole.TryGetClientPreviousVehicleID(selectedVehicle, out _)) return;

		double dx = selectedVehicle.Pos.X - cameraPosition.X;
		double dy = selectedVehicle.Pos.Y - cameraPosition.Y;
		double dz = selectedVehicle.Pos.Z - cameraPosition.Z;
		if (dx * dx + dy * dy + dz * dz > maxDistanceSQ) return;

		long convoyKey = ItemLinkagePole.ResolveClientConvoyAnchor(selectedVehicle);
		int color = WithAlpha(GetOrAssignColor(convoyKey), SelectedVehicleAlpha);

		if (selectedVehicle is EntityStandardGaugeLocomotive standardGaugeVehicle)
		{
			WriteStandardGaugeVehicleSolidBox(ref forwardingHitboxBoxCount, standardGaugeVehicle, cameraPosition, color);
			return;
		}

		Cuboidf box = selectedVehicle.SelectionBox;
		WriteAxisAlignedSolidBox
		(
			ref forwardingHitboxBoxCount,
			(float)(selectedVehicle.Pos.X + box.X1 - cameraPosition.X),
			(float)(selectedVehicle.Pos.Y + box.Y1 - cameraPosition.Y),
			(float)(selectedVehicle.Pos.Z + box.Z1 - cameraPosition.Z),
			(float)(selectedVehicle.Pos.X + box.X2 - cameraPosition.X),
			(float)(selectedVehicle.Pos.Y + box.Y2 - cameraPosition.Y),
			(float)(selectedVehicle.Pos.Z + box.Z2 - cameraPosition.Z),
			color
		);
	}

	private void RenderForwardingHitboxMesh(int forwardingHitboxBoxCount)
	{
		if (ForwardingHitboxMeshReference == null || forwardingHitboxBoxCount <= 0) return;

		ForwardingHitboxMeshData.VerticesCount = forwardingHitboxBoxCount * VerticesPerForwardingHitboxBox;
		ForwardingHitboxMeshData.IndicesCount = forwardingHitboxBoxCount * IndicesPerForwardingHitboxBox;
		ClientAPI.Render.UpdateMesh(ForwardingHitboxMeshReference, ForwardingHitboxMeshData);

		try
		{
			ClientAPI.Render.GlDisableCullFace();
			ClientAPI.Render.GlToggleBlend(true);
			ClientAPI.Render.GLDepthMask(false);

			GL.Enable(EnableCap.PolygonOffsetFill);
			GL.PolygonOffset(ForwardingHitboxDepthBiasFactor, ForwardingHitboxDepthBiasUnits);

			ClientAPI.Render.RenderMesh(ForwardingHitboxMeshReference);
		}
		finally
		{
			GL.PolygonOffset(0f, 0f);
			GL.Disable(EnableCap.PolygonOffsetFill);
			ClientAPI.Render.GLDepthMask(true);
			ClientAPI.Render.GlToggleBlend(false);
			ClientAPI.Render.GlEnableCullFace();
		}
	}

	private void RenderWireMesh(int vertexCount)
	{
		if (WireMeshReference == null || vertexCount <= 0) return;

		WireMeshData.VerticesCount = vertexCount;
		WireMeshData.IndicesCount = vertexCount; // Lines use indices, so this MUST be set
		ClientAPI.Render.UpdateMesh(WireMeshReference, WireMeshData);

		ClientAPI.Render.LineWidth = 2f;
		ClientAPI.Render.RenderMesh(WireMeshReference);
		ClientAPI.Render.LineWidth = 1f;
	}

	private void BuildForwardingHitboxCubeIndices()
	{
		for (int boxIndex = 0; boxIndex < MaxForwardingHitboxBoxes; boxIndex++)
		{
			int vertexBaseIndex = boxIndex * VerticesPerForwardingHitboxBox;
			int indexBaseIndex = boxIndex * IndicesPerForwardingHitboxBox;

			for (int triangleIndex = 0; triangleIndex < UnitCubeTriangleIndices.Length; triangleIndex++)
			{
				ForwardingHitboxMeshData.Indices[indexBaseIndex + triangleIndex] = vertexBaseIndex + UnitCubeTriangleIndices[triangleIndex];
			}
		}
	}

	private void WriteStandardGaugeForwardingHitboxes(ref int forwardingHitboxBoxCount, EntityStandardGaugeLocomotive standardGaugeVehicle, Vec3d cameraPosition, int color)
	{
		EntityBehaviorSGBodySelectionBoxes selectionBoxBehavior = standardGaugeVehicle.GetBehavior<EntityBehaviorSGBodySelectionBoxes>();
		var forwardingHitboxes = selectionBoxBehavior?.GetForwardingBoxesForDebug();
		if (forwardingHitboxes == null || forwardingHitboxes.Length == 0) return;

		if (!SGLocomotiveBodyTransform.TryGetBodyPose(standardGaugeVehicle, out SGLocomotiveBodyPose pose)) return;

		double baseX = standardGaugeVehicle.SidedPos.X			- cameraPosition.X;
		double baseY = standardGaugeVehicle.SidedPos.InternalY	- cameraPosition.Y;
		double baseZ = standardGaugeVehicle.SidedPos.Z			- cameraPosition.Z;

		foreach (var attachmentPointAndPose in forwardingHitboxes)
		{
			if (forwardingHitboxBoxCount >= MaxForwardingHitboxBoxes) break;
			if (attachmentPointAndPose?.AttachPoint?.ParentElement == null || attachmentPointAndPose.AnimModelMatrix == null) continue;

			ForwardingHitboxBoxMatrix.Identity();
			SGLocomotiveBodyTransform.BuildBodySelectionBoxMatrix(ForwardingHitboxBoxMatrix, standardGaugeVehicle, pose, attachmentPointAndPose);

			WriteTransformedUnitCube(forwardingHitboxBoxCount++, baseX, baseY, baseZ, ForwardingHitboxBoxMatrix, color);
		}
	}

	private void WriteAxisAlignedSolidBox(ref int forwardingHitboxBoxCount, float x1, float y1, float z1, float x2, float y2, float z2, int color)
	{
		if (forwardingHitboxBoxCount >= MaxForwardingHitboxBoxes) return;

		int firstVertexIndex = forwardingHitboxBoxCount++ * VerticesPerForwardingHitboxBox;

		SetSolidBoxVertex(firstVertexIndex + 0, x1, y1, z1, color);
		SetSolidBoxVertex(firstVertexIndex + 1, x2, y1, z1, color);
		SetSolidBoxVertex(firstVertexIndex + 2, x2, y2, z1, color);
		SetSolidBoxVertex(firstVertexIndex + 3, x1, y2, z1, color);
		SetSolidBoxVertex(firstVertexIndex + 4, x1, y1, z2, color);
		SetSolidBoxVertex(firstVertexIndex + 5, x2, y1, z2, color);
		SetSolidBoxVertex(firstVertexIndex + 6, x2, y2, z2, color);
		SetSolidBoxVertex(firstVertexIndex + 7, x1, y2, z2, color);
	}

	private void WriteStandardGaugeVehicleSolidBox(ref int forwardingHitboxBoxCount, EntityStandardGaugeLocomotive standardGaugeVehicle, Vec3d cameraPosition, int color)
	{
		if (forwardingHitboxBoxCount >= MaxForwardingHitboxBoxes) return;

		const double halfWidth = 1.0; // 2 blocks wide
		const double height = 4.0;
		const double bottomOffset = -0.15;

		double vehicleLength = Math.Max(0.1, standardGaugeVehicle.DebugVehicleLength);
		double frontOffset = standardGaugeVehicle.DebugBodyOffsetForward + 1;
		double rearOffset = frontOffset - vehicleLength;

		double yaw = standardGaugeVehicle.Pos.Yaw;
		double fx = Math.Sin(yaw);
		double fz = Math.Cos(yaw);
		double rx = Math.Cos(yaw);
		double rz = -Math.Sin(yaw);

		double baseX = standardGaugeVehicle.Pos.X - cameraPosition.X;
		double baseY = standardGaugeVehicle.Pos.Y - cameraPosition.Y;
		double baseZ = standardGaugeVehicle.Pos.Z - cameraPosition.Z;

		double y0 = bottomOffset;
		double y1 = bottomOffset + height;

		int firstVertexIndex = forwardingHitboxBoxCount++ * VerticesPerForwardingHitboxBox;

		SetOrientedSolidBoxVertex(firstVertexIndex + 0, baseX, baseY, baseZ, fx, fz, rx, rz, rearOffset, -halfWidth, y0, color);
		SetOrientedSolidBoxVertex(firstVertexIndex + 1, baseX, baseY, baseZ, fx, fz, rx, rz, frontOffset, -halfWidth, y0, color);
		SetOrientedSolidBoxVertex(firstVertexIndex + 2, baseX, baseY, baseZ, fx, fz, rx, rz, frontOffset, -halfWidth, y1, color);
		SetOrientedSolidBoxVertex(firstVertexIndex + 3, baseX, baseY, baseZ, fx, fz, rx, rz, rearOffset, -halfWidth, y1, color);
		SetOrientedSolidBoxVertex(firstVertexIndex + 4, baseX, baseY, baseZ, fx, fz, rx, rz, rearOffset, halfWidth, y0, color);
		SetOrientedSolidBoxVertex(firstVertexIndex + 5, baseX, baseY, baseZ, fx, fz, rx, rz, frontOffset, halfWidth, y0, color);
		SetOrientedSolidBoxVertex(firstVertexIndex + 6, baseX, baseY, baseZ, fx, fz, rx, rz, frontOffset, halfWidth, y1, color);
		SetOrientedSolidBoxVertex(firstVertexIndex + 7, baseX, baseY, baseZ, fx, fz, rx, rz, rearOffset, halfWidth, y1, color);
	}

	private void SetOrientedSolidBoxVertex
	(
		int index, double baseX, double baseY, double baseZ,
		double fx, double fz, double rx, double rz,
		double length, double width, double y, int color
	)
	{ SetSolidBoxVertex(index, (float)(baseX + fx * length + rx * width), (float)(baseY + y), (float)(baseZ + fz * length + rz * width), color); }

	private void SetSolidBoxVertex(int index, float x, float y, float z, int color)
	{
		int coordinateOffset = index * 3;
		ForwardingHitboxMeshData.xyz[coordinateOffset + 0] = x;
		ForwardingHitboxMeshData.xyz[coordinateOffset + 1] = y;
		ForwardingHitboxMeshData.xyz[coordinateOffset + 2] = z;

		SetForwardingHitboxColor(index, color);
	}

	private void WriteTransformedUnitCube(int boxIndex, double baseX, double baseY, double baseZ, Matrixf transformationMatrix, int color)
	{
		int firstVertexIndex = boxIndex * VerticesPerForwardingHitboxBox;

		WriteTransformedCubeVertex(firstVertexIndex + 0, baseX, baseY, baseZ, transformationMatrix, 0f, 0f, 0f, color);
		WriteTransformedCubeVertex(firstVertexIndex + 1, baseX, baseY, baseZ, transformationMatrix, 1f, 0f, 0f, color);
		WriteTransformedCubeVertex(firstVertexIndex + 2, baseX, baseY, baseZ, transformationMatrix, 1f, 1f, 0f, color);
		WriteTransformedCubeVertex(firstVertexIndex + 3, baseX, baseY, baseZ, transformationMatrix, 0f, 1f, 0f, color);
		WriteTransformedCubeVertex(firstVertexIndex + 4, baseX, baseY, baseZ, transformationMatrix, 0f, 0f, 1f, color);
		WriteTransformedCubeVertex(firstVertexIndex + 5, baseX, baseY, baseZ, transformationMatrix, 1f, 0f, 1f, color);
		WriteTransformedCubeVertex(firstVertexIndex + 6, baseX, baseY, baseZ, transformationMatrix, 1f, 1f, 1f, color);
		WriteTransformedCubeVertex(firstVertexIndex + 7, baseX, baseY, baseZ, transformationMatrix, 0f, 1f, 1f, color);
	}

	private void WriteTransformedCubeVertex(int index, double baseX, double baseY, double baseZ, Matrixf transformationMatrix, float x, float y, float z, int color)
	{
		float[] matrixValues = transformationMatrix.Values;

		float tx = matrixValues[0] * x + matrixValues[4] * y + matrixValues[8] * z + matrixValues[12];
		float ty = matrixValues[1] * x + matrixValues[5] * y + matrixValues[9] * z + matrixValues[13];
		float tz = matrixValues[2] * x + matrixValues[6] * y + matrixValues[10] * z + matrixValues[14];

		int coordinateOffset = index * 3;
		ForwardingHitboxMeshData.xyz[coordinateOffset + 0] = (float)(baseX + tx);
		ForwardingHitboxMeshData.xyz[coordinateOffset + 1] = (float)(baseY + ty);
		ForwardingHitboxMeshData.xyz[coordinateOffset + 2] = (float)(baseZ + tz);

		SetForwardingHitboxColor(index, color);
	}

	private void SetForwardingHitboxColor(int index, int color)
	{
		// MeshData expects little-endian BGRA in the byte buffer (same as AddVertexSkipTex does internally).
		int colorOffset = index * 4;
		ForwardingHitboxMeshData.Rgba[colorOffset + 0] = (byte)(color & 0xFF);			// B
		ForwardingHitboxMeshData.Rgba[colorOffset + 1] = (byte)((color >> 8) & 0xFF);	// G
		ForwardingHitboxMeshData.Rgba[colorOffset + 2] = (byte)((color >> 16) & 0xFF);	// R
		ForwardingHitboxMeshData.Rgba[colorOffset + 3] = (byte)((color >> 24) & 0xFF);	// A
	}

	private static int WithAlpha(int color, int alpha)
	{
		return ColorUtil.ToRgba(alpha, ColorUtil.ColorR(color), ColorUtil.ColorG(color), ColorUtil.ColorB(color));
	}


	private void WriteBox(ref int vertexCount, float x1, float y1, float z1, float x2, float y2, float z2, int color)
	{
		// Bottom rectangle
		AddLine(ref vertexCount, x1, y1, z1, x2, y1, z1, color);
		AddLine(ref vertexCount, x2, y1, z1, x2, y1, z2, color);
		AddLine(ref vertexCount, x2, y1, z2, x1, y1, z2, color);
		AddLine(ref vertexCount, x1, y1, z2, x1, y1, z1, color);

		// Top rectangle
		AddLine(ref vertexCount, x1, y2, z1, x2, y2, z1, color);
		AddLine(ref vertexCount, x2, y2, z1, x2, y2, z2, color);
		AddLine(ref vertexCount, x2, y2, z2, x1, y2, z2, color);
		AddLine(ref vertexCount, x1, y2, z2, x1, y2, z1, color);

		// Vertical edges
		AddLine(ref vertexCount, x1, y1, z1, x1, y2, z1, color);
		AddLine(ref vertexCount, x2, y1, z1, x2, y2, z1, color);
		AddLine(ref vertexCount, x2, y1, z2, x2, y2, z2, color);
		AddLine(ref vertexCount, x1, y1, z2, x1, y2, z2, color);
	}

	private void WriteStandardGaugeVehicleBox(ref int vertexCount, EntityStandardGaugeLocomotive standardGaugeVehicle, Vec3d cameraPosition, int color)
	{
		const double halfWidth = 1.0; // 2 blocks wide
		const double height = 4.0;
		const double bottomOffset = -0.15;

		double vehicleLength = Math.Max(0.1, standardGaugeVehicle.DebugVehicleLength);
		double frontOffset = standardGaugeVehicle.DebugBodyOffsetForward + 1;
		double rearOffset = frontOffset - vehicleLength;

		double yaw = standardGaugeVehicle.Pos.Yaw;
		double fx = Math.Sin(yaw);
		double fz = Math.Cos(yaw);
		double rx = Math.Cos(yaw);
		double rz = -Math.Sin(yaw);

		double baseX = standardGaugeVehicle.Pos.X - cameraPosition.X;
		double baseY = standardGaugeVehicle.Pos.Y - cameraPosition.Y;
		double baseZ = standardGaugeVehicle.Pos.Z - cameraPosition.Z;

		double y0 = bottomOffset;
		double y1 = bottomOffset + height;

		// Bottom rectangle
		AddOrientedBoxLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, rearOffset, -halfWidth, y0, frontOffset, -halfWidth, y0, color);
		AddOrientedBoxLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, frontOffset, -halfWidth, y0, frontOffset, halfWidth, y0, color);
		AddOrientedBoxLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, frontOffset, halfWidth, y0, rearOffset, halfWidth, y0, color);
		AddOrientedBoxLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, rearOffset, halfWidth, y0, rearOffset, -halfWidth, y0, color);

		// Top rectangle
		AddOrientedBoxLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, rearOffset, -halfWidth, y1, frontOffset, -halfWidth, y1, color);
		AddOrientedBoxLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, frontOffset, -halfWidth, y1, frontOffset, halfWidth, y1, color);
		AddOrientedBoxLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, frontOffset, halfWidth, y1, rearOffset, halfWidth, y1, color);
		AddOrientedBoxLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, rearOffset, halfWidth, y1, rearOffset, -halfWidth, y1, color);

		// Vertical edges
		AddOrientedBoxLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, rearOffset, -halfWidth, y0, rearOffset, -halfWidth, y1, color);
		AddOrientedBoxLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, frontOffset, -halfWidth, y0, frontOffset, -halfWidth, y1, color);
		AddOrientedBoxLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, frontOffset, halfWidth, y0, frontOffset, halfWidth, y1, color);
		AddOrientedBoxLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, rearOffset, halfWidth, y0, rearOffset, halfWidth, y1, color);
	}

	private void AddOrientedBoxLine
	(
		ref int vertexCount,
		double baseX, double baseY, double baseZ,
		double fx, double fz,
		double rx, double rz,
		double length0, double width0, double y0,
		double length1, double width1, double y1,
		int color)
	{
		AddLine
		(
			ref vertexCount,
			(float)(baseX + fx * length0 + rx * width0),
			(float)(baseY + y0),
			(float)(baseZ + fz * length0 + rz * width0),
			(float)(baseX + fx * length1 + rx * width1),
			(float)(baseY + y1),
			(float)(baseZ + fz * length1 + rz * width1),
			color
		);
	}

	private void AddLine(ref int vertexCount, float ax, float ay, float az, float bx, float by, float bz, int color)
	{
		if (vertexCount + 2 > MaxVertices) return;

		SetVertex(vertexCount++, ax, ay, az, color);
		SetVertex(vertexCount++, bx, by, bz, color);
	}

	private void SetVertex(int index, float x, float y, float z, int color)
	{
		int coordinateOffset = index * 3;
		WireMeshData.xyz[coordinateOffset + 0] = x;
		WireMeshData.xyz[coordinateOffset + 1] = y;
		WireMeshData.xyz[coordinateOffset + 2] = z;

		// MeshData expects little-endian BGRA in the byte buffer (same as AddVertexSkipTex does internally).
		int colorOffset = index * 4;
		WireMeshData.Rgba[colorOffset + 0] = (byte)(color & 0xFF);			// B
		WireMeshData.Rgba[colorOffset + 1] = (byte)((color >> 8) & 0xFF);	// G
		WireMeshData.Rgba[colorOffset + 2] = (byte)((color >> 16) & 0xFF);	// R
		WireMeshData.Rgba[colorOffset + 3] = (byte)((color >> 24) & 0xFF);	// A
	}

	private int GetOrAssignColor(long convoyKey)
	{
		if (ConvoyColors.TryGetValue(convoyKey, out int existingColor)) return existingColor;
		int color;

		// Unique palette colors until we run out, then random (no uniqueness check).
		if (PaletteAssignedCount < TertiaryPalette.Length)
		{
			color = TertiaryPalette[PaletteOrder[PaletteAssignedCount]];
			PaletteAssignedCount++;
		}
		else
		{
			// Bright-ish random (avoid very dark colors).
			int r = RandomGenerator.Next(48, 256);
			int g = RandomGenerator.Next(48, 256);
			int b = RandomGenerator.Next(48, 256);
			color = ColorUtil.ToRgba(255, r, g, b);
		}

		ConvoyColors[convoyKey] = color;
		return color;
	}
}

/// Linkage pole interaction gloop for the convoy/tether system...
///		- Right-click:	Two-step tether (select convoy, then merge)
///		- Left-click:	Untether the clicked rail vehicle (split convoy)
internal static class ConvoyTethering
{
	private const string SelectedConvoyHeadAttributeKey = "yangtransport:selectedConvoyHeadId";

	private const int TetherDurabilityCost = 10;
	private const int UntetherDurabilityCost = 5;

	internal static void HandleLinkagePoleTetherClick(IRailwayConvoyVehicle clickedVehicle, EntityAgent byEntity, ItemSlot toolSlot)
	{
		if (byEntity is not EntityPlayer playerEntity) return;

		if (clickedVehicle.Entity.Api.Side != EnumAppSide.Server) return;

		var serverPlayer = clickedVehicle.Entity.Api.World.PlayerByUid(playerEntity.PlayerUID) as IServerPlayer;
		if (serverPlayer == null) return;

		var convoySystem = clickedVehicle.Entity.Api.ModLoader.GetModSystem<RailConvoySystem>();
		long pendingConvoyHeadID = playerEntity.Attributes.GetLong(SelectedConvoyHeadAttributeKey, 0);
		long clickedConvoyAnchor = clickedVehicle.ConvoyHeadEntityID != 0 ? clickedVehicle.ConvoyHeadEntityID : clickedVehicle.Entity.EntityId;

		// First click, select convoy (non-specific)
		if (pendingConvoyHeadID == 0) { playerEntity.Attributes.SetLong(SelectedConvoyHeadAttributeKey, clickedConvoyAnchor); return; }

		// Sneak + right-click, clear selection
		if (playerEntity.Controls.Sneak) { playerEntity.Attributes.SetLong(SelectedConvoyHeadAttributeKey, 0); return; }

		// Second click, must be a different convoy
		if (pendingConvoyHeadID == clickedConvoyAnchor) { serverPlayer.SendIngameError("yangtransport:linkagepole-sameconvoy"); return; }

		// Merge
		playerEntity.Attributes.SetLong(SelectedConvoyHeadAttributeKey, 0);
		bool actionComplete = convoySystem.TryMerge(pendingConvoyHeadID, clickedVehicle, serverPlayer);
		if (actionComplete) TryDamageTool(toolSlot, serverPlayer, TetherDurabilityCost);
	}

	internal static void ClearLinkagePoleSelection(EntityAgent byEntity)
	{
		if (byEntity is EntityPlayer playerEntity && byEntity.Api?.Side == EnumAppSide.Server) { playerEntity.Attributes.SetLong(SelectedConvoyHeadAttributeKey, 0); }
	}

	internal static void HandleLinkagePoleUntetherClick(IRailwayConvoyVehicle clickedVehicle, EntityAgent byEntity, ItemSlot toolSlot)
	{
		if (byEntity is not EntityPlayer playerEntity) return;
		if (clickedVehicle.Entity.Api.Side != EnumAppSide.Server) return;

		var serverPlayer = clickedVehicle.Entity.Api.World.PlayerByUid(playerEntity.PlayerUID) as IServerPlayer;
		if (serverPlayer == null) return;

		// Untether should not keep a half-finished tether selection around.
		ClearLinkagePoleSelection(byEntity);

		var convoySystem = clickedVehicle.Entity.Api.ModLoader.GetModSystem<RailConvoySystem>();
		bool actionComplete = convoySystem.TryUntether(clickedVehicle, serverPlayer);
		if (actionComplete) TryDamageTool(toolSlot, serverPlayer, UntetherDurabilityCost);
 	}

	private static void TryDamageTool(ItemSlot toolSlot, IServerPlayer player, int durabilityAmount)
	{
		if (toolSlot == null || toolSlot.Empty) return;
		if (player.WorldData?.CurrentGameMode == EnumGameMode.Creative) return;

		ItemStack stack = toolSlot.Itemstack;
		stack?.Collectible?.DamageItem(player.Entity.World, player.Entity, toolSlot, durabilityAmount);
		toolSlot.MarkDirty();
	}
}
