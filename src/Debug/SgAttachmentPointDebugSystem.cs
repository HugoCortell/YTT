using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace YangTransport;

public sealed class StandardGaugeAttachmentPointDebugSystem : ModSystem, IRenderer, IDisposable
{
	private const double MaximumRenderDistance = 64.0;
	private const double MaximumRenderDistanceSquared = MaximumRenderDistance * MaximumRenderDistance;

	private ICoreClientAPI? ClientAPI;
	private WireframeCube? BoxWireframe;

	private readonly Matrixf ModelViewMatrix = new();
	private readonly Matrixf BoxTransformMatrix = new();

	private readonly Vec4f NormalColor = new(0.1f, 0.85f, 1f, 1f);
	private readonly Vec4f ForwardingColor = new(1f, 0.65f, 0.1f, 1f);

	private bool IsEnabled;

	public double RenderOrder => 1.0;
	public int RenderRange => (int)MaximumRenderDistance;

	public override void StartClientSide(ICoreClientAPI clientAPI) { ClientAPI = clientAPI; }

	public override void Dispose()
	{
		DisableRenderer();
		base.Dispose();
	}

	internal bool Enabled => IsEnabled;

	internal void SetEnabled(bool enable)
	{
		if (enable == IsEnabled || ClientAPI == null) return;

		IsEnabled = enable;
		if (IsEnabled)
		{
			BoxWireframe = WireframeCube.CreateUnitCube(ClientAPI, -1);
			ClientAPI.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "yangtransport:sgapdebug");
		}
		else { DisableRenderer(); }
	}

	private void DisableRenderer()
	{
		if (ClientAPI != null && BoxWireframe != null) { ClientAPI.Event.UnregisterRenderer(this, EnumRenderStage.Opaque); }

		BoxWireframe?.Dispose();
		BoxWireframe = null;
	}

	public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
	{
		if (!IsEnabled || ClientAPI == null || BoxWireframe == null || ClientAPI.HideGuis) return;

		EntityPlayer? player = ClientAPI.World.Player?.Entity;
		if (player == null) return;

		Vec3d cameraPosition = player.CameraPos;

		foreach (Entity entity in ClientAPI.World.LoadedEntities.Values)
		{
			if (entity is not EntityStandardGaugeLocomotive || !entity.Alive) continue;
			if ((entity.SidedPos.XYZ - cameraPosition).LengthSq() > MaximumRenderDistanceSquared) continue;

			RenderEntityAttachmentPoints(entity);
		}
	}

	private void RenderEntityAttachmentPoints(Entity entity)
	{
		EntityBehaviorSGBodySelectionBoxes? selectionBehavior = entity.GetBehavior<EntityBehaviorSGBodySelectionBoxes>(); if (selectionBehavior == null) return;

		if (!SGLocomotiveBodyTransform.TryGetBodyPose(entity, out SGLocomotiveBodyPose pose)) return;
		SGLocomotiveBodyTransform.GetRenderOrigin(ClientAPI!, entity, out double originX, out double originY, out double originZ);

		RenderBoxes(entity, originX, originY, originZ, pose, selectionBehavior.GetSelectionBoxesForDebug(), NormalColor);
		RenderBoxes(entity, originX, originY, originZ, pose, selectionBehavior.GetForwardingBoxesForDebug(), ForwardingColor);
	}

	private void RenderBoxes(Entity entity, double originX, double originY, double originZ, SGLocomotiveBodyPose pose, AttachmentPointAndPose[] boxes, Vec4f color)
	{
		for (int boxIndex = 0; boxIndex < boxes.Length; boxIndex++)
		{
			AttachmentPointAndPose attachmentPointAndPose = boxes[boxIndex];
			if (attachmentPointAndPose?.AttachPoint?.ParentElement == null || attachmentPointAndPose.AnimModelMatrix == null) continue;

			BoxTransformMatrix.Identity();
			SGLocomotiveBodyTransform.BuildBodySelectionBoxMatrix(BoxTransformMatrix, entity, pose, attachmentPointAndPose);

			ModelViewMatrix.Identity()
				.Set(ClientAPI!.Render.CameraMatrixOrigin)
				.Translate(entity.SidedPos.X - originX, entity.SidedPos.InternalY - originY, entity.SidedPos.Z - originZ)
				.Mul(BoxTransformMatrix);

			BoxWireframe!.Render(ClientAPI, ModelViewMatrix, 1.6f, color);
		}
	}
}
