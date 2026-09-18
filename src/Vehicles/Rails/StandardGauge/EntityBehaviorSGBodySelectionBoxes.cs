using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace YangTransport;

public sealed class EntityBehaviorSGBodySelectionBoxes : EntityBehaviorSelectionBoxes, ICustomInteractionHelpPositioning
{
	private const string ForwardingHitboxPrefix = "FWHB_";

	private static readonly Cuboidd StandardBox = new Cuboidd(0.0, 0.0, 0.0, 1.0, 1.0, 1.0);
	public bool TransparentCenter => false;

	private readonly Matrixf ModelViewMatrix = new();
	private readonly Matrixf InverseMatrix = new();
	private string[] SelectionBoxCodes = Array.Empty<string>();
	private AttachmentPointAndPose[] ForwardingBoxes = Array.Empty<AttachmentPointAndPose>();
	private Vec3d OrientedBoundingBoxHitPosition = new();
	private Vec3d AxisAlignedBoundingBoxHitPosition = new();
	private int SelectedForwardingBoxIndex = -1;
	private bool SelectionBoxLoadAttempted;
	private SGSelectionBoxHighlightRenderer HighlightRenderer;

	public EntityBehaviorSGBodySelectionBoxes(Entity entity) : base(entity) { }

	public override void Initialize(EntityProperties properties, JsonObject attributes)
	{
		entity.trickleDownRayIntersects = true;
		entity.requirePosesOnServer = true;

		SelectionBoxCodes = BuildSelectionBoxCodes(attributes);
		if (SelectionBoxCodes.Length == 0)
		{
			entity.Api?.World?.Logger.Warning("EntityBehaviorSgBodySelectionBoxes, missing selectionBoxes or selectionBoxGroups property on {0}.", entity.Code);
		}

		if (entity.Api is ICoreClientAPI clientAPI) { HighlightRenderer = new SGSelectionBoxHighlightRenderer(this, clientAPI); }
	}

	public override void OnTesselated()
	{
		LoadSelectionBoxes();
	}

	public override void OnGameTick(float deltaTime)
	{
		// Server-side interaction code also reads EntityBehaviorSelectionBoxes.selectionBoxes by index.
		// Loading is cheap and only attempted while the model APs have not been resolved yet.
		if (!SelectionBoxLoadAttempted || (selectionBoxes.Length == 0 && SelectionBoxCodes.Length > 0)) { LoadSelectionBoxes(); }
	}

	public override void OnEntityDespawn(EntityDespawnData despawn)
	{
		HighlightRenderer?.Dispose();
		HighlightRenderer = null;
		base.OnEntityDespawn(despawn);
	}

	public override bool IntersectsRay(Ray ray, AABBIntersectionTest intersectionTester, out double intersectionDistance, ref int selectionBoxIndex, ref EnumHandling handled)
	{
		if (!SelectionBoxLoadAttempted || (selectionBoxes.Length == 0 && SelectionBoxCodes.Length > 0)) LoadSelectionBoxes();

		Ray pickingRay = new Ray(ray.origin - entity.SidedPos.XYZ, ray.dir);
		if (!SGLocomotiveBodyTransform.TryGetBodyPose(entity, out SGLocomotiveBodyPose pose)) { intersectionDistance = double.MaxValue; return false; }

		// Functional AP boxes get first refusal so large forwarding boxes cannot swallow other deliberate selection targets like the controls.
		if (TryGetHitIndex(selectionBoxes, pickingRay, pose, out int index))
		{
			SelectedForwardingBoxIndex = -1;
			intersectionDistance = AxisAlignedBoundingBoxHitPosition.Length();
			intersectionTester.hitPosition = AxisAlignedBoundingBoxHitPosition.AddCopy(entity.SidedPos.XYZ);
			selectionBoxIndex = 1 + index;
			handled = EnumHandling.PreventDefault;
			return true;
		}

		if (TryGetHitIndex(ForwardingBoxes, pickingRay, pose, out int forwardingIndex))
		{
			SelectedForwardingBoxIndex = forwardingIndex;
			intersectionDistance = AxisAlignedBoundingBoxHitPosition.Length();
			intersectionTester.hitPosition = AxisAlignedBoundingBoxHitPosition.AddCopy(entity.SidedPos.XYZ);
			selectionBoxIndex = 0;
			handled = EnumHandling.PreventDefault;
			return true;
		}

		intersectionDistance = double.MaxValue;
		return false;
	}

	public override void GetInfoText(StringBuilder informationText)
	{
		if (!SelectionBoxLoadAttempted || (selectionBoxes.Length == 0 && SelectionBoxCodes.Length > 0)) LoadSelectionBoxes();

		int hitIndex = GetHitIndexFromPlayer();
		if (hitIndex < 0 || hitIndex >= selectionBoxes.Length) return;
		if (entity.Api is not Vintagestory.API.Client.ICoreClientAPI clientAPI) return;

		informationText.AppendLine(Lang.GetMatching(GetSelectionBoxLanguageKey(selectionBoxes[hitIndex].AttachPoint.Code), Array.Empty<object>()));
	}

	public override string PropertyName() { return "selectionboxes"; } // Functionally a const string at the moment

	public Vec3d GetInteractionHelpPosition() // Dirty fix with ICustomInteractionHelpPositioning and TransparentCenter for block overlay crash
	{
		if (entity.Api is not ICoreClientAPI clientAPI) return null;

		EntitySelection selection = clientAPI.World.Player.CurrentEntitySelection;
		if (selection == null || selection.Entity != entity || selection.HitPosition == null) return null;

		if (!SelectionBoxLoadAttempted || (selectionBoxes.Length == 0 && SelectionBoxCodes.Length > 0)) LoadSelectionBoxes();

		int selectionBoxIndex = selection.SelectionBoxIndex;
		if (selectionBoxIndex > 0 && TryGetBoxHelpPosition(clientAPI, selectionBoxes, selectionBoxIndex - 1, out Vec3d position)) { return position; }
		if (selectionBoxIndex == 0 && SelectedForwardingBoxIndex >= 0 && TryGetBoxHelpPosition(clientAPI, ForwardingBoxes, SelectedForwardingBoxIndex, out position)) { return position; }

		Vec3d fallbackPosition = selection.Position != null
			? selection.Position.AddCopy(selection.HitPosition)
			: entity.SidedPos.XYZ.AddCopy(selection.HitPosition);

		return ApplyRenderOriginOffsetForHeadsUpDisplay(clientAPI, fallbackPosition);
	}

	private bool TryGetBoxHelpPosition(ICoreClientAPI clientAPI, AttachmentPointAndPose[] selectionBoxes, int selectionBoxIndex, out Vec3d position)
	{
		position = null;

		if (selectionBoxes == null || selectionBoxIndex < 0 || selectionBoxIndex >= selectionBoxes.Length) return false;

		AttachmentPointAndPose attachmentPointAndPose = selectionBoxes[selectionBoxIndex];
		if (attachmentPointAndPose?.AttachPoint?.ParentElement == null || attachmentPointAndPose.AnimModelMatrix == null) return false;
		if (!SGLocomotiveBodyTransform.TryGetBodyPose(entity, out SGLocomotiveBodyPose pose)) return false;

		ModelViewMatrix.Identity();
		SGLocomotiveBodyTransform.BuildBodySelectionBoxMatrix(ModelViewMatrix, entity, pose, attachmentPointAndPose);

		Vec4d center = ModelViewMatrix.TransformVector(new Vec4d(0.5, 0.5, 0.5, 1.0));
		position = ApplyRenderOriginOffsetForHeadsUpDisplay(clientAPI, new Vec3d(entity.SidedPos.X + center.X, entity.SidedPos.InternalY + center.Y, entity.SidedPos.Z + center.Z));
		return true;
	}

	private Vec3d ApplyRenderOriginOffsetForHeadsUpDisplay(ICoreClientAPI clientAPI, Vec3d worldPosition)
	{
		Vec3d cameraPosition = clientAPI.World.Player?.Entity?.CameraPos;
		if (cameraPosition == null) return worldPosition;

		SGLocomotiveBodyTransform.GetRenderOrigin(clientAPI, entity, out double originX, out double originY, out double originZ);

		worldPosition.X += cameraPosition.X - originX;
		worldPosition.Y += cameraPosition.Y - originY;
		worldPosition.Z += cameraPosition.Z - originZ;
		return worldPosition;
	}

	public AttachmentPointAndPose[] GetSelectionBoxesForDebug()
	{
		if (!SelectionBoxLoadAttempted || (selectionBoxes.Length == 0 && SelectionBoxCodes.Length > 0)) { LoadSelectionBoxes(); }
		return selectionBoxes ?? Array.Empty<AttachmentPointAndPose>();
	}

	public AttachmentPointAndPose[] GetForwardingBoxesForDebug()
	{
		if (!SelectionBoxLoadAttempted) { LoadSelectionBoxes(); }
		return ForwardingBoxes ?? Array.Empty<AttachmentPointAndPose>();
	}

	private string GetSelectionBoxLanguageKey(string attachmentPointCode) { return entity.Code.Domain + ":creature-" + entity.Code.Path + "-selectionbox-" + attachmentPointCode; }

	private static string[] BuildSelectionBoxCodes(JsonObject attributes)
	{
		List<string> selectionBoxCodes = new();

		string[] explicitCodes = attributes["SelectionBoxes"].AsArray<string>(Array.Empty<string>(), null);
		if (explicitCodes != null && explicitCodes.Length > 0) selectionBoxCodes.AddRange(explicitCodes);

		AddPrefixedCodes(selectionBoxCodes, attributes["SelectionBoxPrefix"].AsString(null), attributes["SelectionBoxCount"].AsInt(0));

		JsonObject[] selectionBoxGroups = attributes["SelectionBoxGroups"].AsArray();
		if (selectionBoxGroups != null)
		{
			for (int groupIndex = 0; groupIndex < selectionBoxGroups.Length; groupIndex++)
			{
				JsonObject selectionBoxGroup = selectionBoxGroups[groupIndex];
				string[] groupSelectionBoxCodes = selectionBoxGroup["SelectionBoxes"].AsArray<string>(Array.Empty<string>(), null);
				if (groupSelectionBoxCodes != null && groupSelectionBoxCodes.Length > 0) selectionBoxCodes.AddRange(groupSelectionBoxCodes);

				AddPrefixedCodes(selectionBoxCodes, selectionBoxGroup["Prefix"].AsString(null), selectionBoxGroup["Count"].AsInt(0));
			}
		}

		return selectionBoxCodes.ToArray();
	}

	private static void AddPrefixedCodes(List<string> selectionBoxCodes, string codePrefix, int codeCount)
	{
		if (string.IsNullOrEmpty(codePrefix) || codeCount <= 0) return;
		for (int codeIndex = 0; codeIndex < codeCount; codeIndex++) { selectionBoxCodes.Add(codePrefix + codeIndex); }
	}

	private void LoadSelectionBoxes()
	{
		var animator = entity.AnimManager?.Animator;
		if (animator == null) { SelectionBoxLoadAttempted = false; return; }

		SelectionBoxLoadAttempted = true;
		List<AttachmentPointAndPose> loadedSelectionBoxes = new(SelectionBoxCodes.Length);

		for (int selectionBoxIndex = 0; selectionBoxIndex < SelectionBoxCodes.Length; selectionBoxIndex++)
		{
			string attachmentPointCode = SelectionBoxCodes[selectionBoxIndex];
			AttachmentPointAndPose attachmentPointAndPose = animator.GetAttachmentPointPose(attachmentPointCode);
			if (attachmentPointAndPose == null) continue;

			loadedSelectionBoxes.Add(CopyAttachmentPointAndPose(attachmentPointAndPose));
		}

		selectionBoxes = loadedSelectionBoxes.ToArray();
		ForwardingBoxes = LoadAutomaticForwardingBoxes();
	}

	private AttachmentPointAndPose[] LoadAutomaticForwardingBoxes()
	{
		var animator = entity.AnimManager?.Animator;
		if (animator == null) return Array.Empty<AttachmentPointAndPose>();

		List<AttachmentPointAndPose> forwardingSelectionBoxes = new();

		for (int forwardingBoxIndex = 0; forwardingBoxIndex < 32; forwardingBoxIndex++)
		{
			AttachmentPointAndPose attachmentPointAndPose = animator.GetAttachmentPointPose(ForwardingHitboxPrefix + forwardingBoxIndex);
			if (attachmentPointAndPose == null) break;

			forwardingSelectionBoxes.Add(CopyAttachmentPointAndPose(attachmentPointAndPose));
		}

		return forwardingSelectionBoxes.ToArray();
	}

	private static AttachmentPointAndPose CopyAttachmentPointAndPose(AttachmentPointAndPose attachmentPointAndPose)
	{
		return new AttachmentPointAndPose
		{
			AnimModelMatrix = attachmentPointAndPose.AnimModelMatrix,
			AttachPoint = attachmentPointAndPose.AttachPoint,
			CachedPose = attachmentPointAndPose.CachedPose
		};
	}

	private int GetHitIndexFromPlayer()
	{
		if (entity.Api is not Vintagestory.API.Client.ICoreClientAPI clientAPI) return -1;

		EntityPlayer playerEntity = clientAPI.World.Player.Entity;
		Ray pickingRay = Ray.FromAngles
		(
			playerEntity.SidedPos.XYZ + playerEntity.LocalEyePos - entity.SidedPos.XYZ,
			playerEntity.SidedPos.Pitch, playerEntity.SidedPos.Yaw,
			clientAPI.World.Player.WorldData.PickingRange
		);

		if (!SGLocomotiveBodyTransform.TryGetBodyPose(entity, out SGLocomotiveBodyPose pose)) return -1;
		return TryGetHitIndex(selectionBoxes, pickingRay, pose, out int hitIndex) ? hitIndex : -1;
	}

	private bool TryGetHitIndex(AttachmentPointAndPose[] candidateBoxes, Ray pickingRay, SGLocomotiveBodyPose pose, out int hitIndex)
	{
		hitIndex = -1;
		if (candidateBoxes == null || candidateBoxes.Length == 0) return false;

		double foundDistance = double.MaxValue;
		double rayLengthSQ = pickingRay.Length * pickingRay.Length;

		for (int candidateBoxIndex = 0; candidateBoxIndex < candidateBoxes.Length; candidateBoxIndex++)
		{
			AttachmentPointAndPose attachmentPointAndPose = candidateBoxes[candidateBoxIndex];
			if (attachmentPointAndPose?.AttachPoint?.ParentElement == null || attachmentPointAndPose.AnimModelMatrix == null) continue;

			ModelViewMatrix.Identity();
			SGLocomotiveBodyTransform.BuildBodySelectionBoxMatrix(ModelViewMatrix, entity, pose, attachmentPointAndPose);

			InverseMatrix.Set(ModelViewMatrix.Values).Invert();
			Vec4d obbSpaceOrigin = InverseMatrix.TransformVector(new Vec4d(pickingRay.origin.X, pickingRay.origin.Y, pickingRay.origin.Z, 1.0));
			Vec4d obbSpaceDirection = InverseMatrix.TransformVector(new Vec4d(pickingRay.dir.X, pickingRay.dir.Y, pickingRay.dir.Z, 0.0));
			Ray obbSpaceRay = new Ray(obbSpaceOrigin.XYZ, obbSpaceDirection.XYZ);

			if (!TestIntersection(StandardBox, obbSpaceRay)) continue;

			Vec4d transformedHitPosition = ModelViewMatrix.TransformVector(new Vec4d(OrientedBoundingBoxHitPosition.X, OrientedBoundingBoxHitPosition.Y, OrientedBoundingBoxHitPosition.Z, 1.0));
			double hitDistanceSQ = (transformedHitPosition.XYZ - pickingRay.origin).LengthSq();

			if ((hitIndex < 0 || foundDistance >= hitDistanceSQ) && rayLengthSQ >= hitDistanceSQ)
			{
				AxisAlignedBoundingBoxHitPosition = transformedHitPosition.XYZ;
				foundDistance = hitDistanceSQ;
				hitIndex = candidateBoxIndex;
			}
		}

		return hitIndex >= 0;
	}

	private bool TestIntersection(Cuboidd boundingBox, Ray ray)
	{
		double width = boundingBox.X2 - boundingBox.X1;
		double height = boundingBox.Y2 - boundingBox.Y1;
		double length = boundingBox.Z2 - boundingBox.Z1;

		for (int faceIndex = 0; faceIndex < 6; faceIndex++)
		{
			BlockFacing blockSideFacing = BlockFacing.ALLFACES[faceIndex];
			Vec3i planeNormal = blockSideFacing.Normali;
			double rayPlaneDenominator = planeNormal.X * ray.dir.X + planeNormal.Y * ray.dir.Y + planeNormal.Z * ray.dir.Z;
			if (rayPlaneDenominator >= -1E-05) continue;

			Vec3d planeCenterPosition = blockSideFacing.PlaneCenter.ToVec3d().Mul(width, height, length).Add(boundingBox.X1, boundingBox.Y1, boundingBox.Z1);
			Vec3d planeOffset = Vec3d.Sub(planeCenterPosition, ray.origin);
			double intersectionParameter = (planeOffset.X * planeNormal.X + planeOffset.Y * planeNormal.Y + planeOffset.Z * planeNormal.Z) / rayPlaneDenominator;
			if (intersectionParameter < 0.0) continue;

			OrientedBoundingBoxHitPosition = new Vec3d(ray.origin.X + ray.dir.X * intersectionParameter, ray.origin.Y + ray.dir.Y * intersectionParameter, ray.origin.Z + ray.dir.Z * intersectionParameter);
			Vec3d lastExitedBlockFacePosition = Vec3d.Sub(OrientedBoundingBoxHitPosition, planeCenterPosition);

			if
			(	
				Math.Abs(lastExitedBlockFacePosition.X) <= width / 2.0 &&
				Math.Abs(lastExitedBlockFacePosition.Y) <= height / 2.0 &&
				Math.Abs(lastExitedBlockFacePosition.Z) <= length / 2.0
			) { return true; }
		}

		return false;
	}
	
	private sealed class SGSelectionBoxHighlightRenderer : IRenderer
	{
		private readonly EntityBehaviorSGBodySelectionBoxes SelectionBoxBehavior;
		private readonly ICoreClientAPI ClientAPI;
		private readonly WireframeCube BoxWireframe;
		private readonly Matrixf ModelViewMatrix = new();
		private readonly Matrixf BoxMatrix = new();

		private float RefreshAccumulatorSec;
		private bool DebugEnabled;
		private bool Disposed;

		public double RenderOrder => 1.0;
		public int RenderRange => 24;

		public SGSelectionBoxHighlightRenderer(EntityBehaviorSGBodySelectionBoxes selectionBoxBehavior, ICoreClientAPI clientAPI)
		{
			this.SelectionBoxBehavior = selectionBoxBehavior;
			this.ClientAPI = clientAPI;

			DebugEnabled = clientAPI.Settings.Bool["debugEntitySelectionBoxes"];
			BoxWireframe = WireframeCube.CreateUnitCube(clientAPI, -1);
			clientAPI.Event.RegisterRenderer(this, EnumRenderStage.AfterFinalComposition, "yangtransport:sgselectionboxes");
		}

		public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
		{
			if (Disposed || ClientAPI.HideGuis || BoxWireframe == null) return;

			if ((RefreshAccumulatorSec += deltaTime) >= 1f)
			{
				RefreshAccumulatorSec = 0f;
				DebugEnabled = ClientAPI.Settings.Bool["debugEntitySelectionBoxes"];
			}

			if (!SelectionBoxBehavior.SelectionBoxLoadAttempted || (SelectionBoxBehavior.selectionBoxes.Length == 0 && SelectionBoxBehavior.SelectionBoxCodes.Length > 0))
			{
				SelectionBoxBehavior.LoadSelectionBoxes();
			}

			Entity entity = SelectionBoxBehavior.entity;
			EntityPlayer playerEntity = ClientAPI.World.Player?.Entity;
			if (playerEntity == null || !entity.Alive) return;

			int hitIndex = GetSelectedFunctionalIndex(entity);
			if (!DebugEnabled && hitIndex < 0) return;

			if (!SGLocomotiveBodyTransform.TryGetBodyPose(entity, out SGLocomotiveBodyPose pose)) return;
			SGLocomotiveBodyTransform.GetRenderOrigin(ClientAPI, entity, out double originX, out double originY, out double originZ);

			if (DebugEnabled)
			{
				for (int selectionBoxIndex = 0; selectionBoxIndex < SelectionBoxBehavior.selectionBoxes.Length; selectionBoxIndex++)
				{
					if (selectionBoxIndex == hitIndex) continue;
					Render(entity, originX, originY, originZ, pose, selectionBoxIndex, ColorUtil.WhiteArgbVec);
				}
				if (hitIndex >= 0) { Render(entity, originX, originY, originZ, pose, hitIndex, new Vec4f(1f, 0f, 0f, 1f)); }

				return;
			}

			Render(entity, originX, originY, originZ, pose, hitIndex, new Vec4f(0f, 0f, 0f, 0.5f));
		}

		private int GetSelectedFunctionalIndex(Entity entity)
		{
			EntitySelection selection = ClientAPI.World.Player.CurrentEntitySelection;
			if (selection == null || selection.Entity != entity) return -1;

			int index = selection.SelectionBoxIndex - 1;
			return index >= 0 && index < SelectionBoxBehavior.selectionBoxes.Length ? index : -1;
		}

		private void Render(Entity entity, double originX, double originY, double originZ, SGLocomotiveBodyPose pose, int selectionBoxIndex, Vec4f color)
		{
			if (selectionBoxIndex < 0 || selectionBoxIndex >= SelectionBoxBehavior.selectionBoxes.Length) return;

			AttachmentPointAndPose attachmentPointAndPose = SelectionBoxBehavior.selectionBoxes[selectionBoxIndex];
			if (attachmentPointAndPose?.AttachPoint?.ParentElement == null || attachmentPointAndPose.AnimModelMatrix == null) return;

			BoxMatrix.Identity();
			SGLocomotiveBodyTransform.BuildBodySelectionBoxMatrix(BoxMatrix, entity, pose, attachmentPointAndPose);

			ModelViewMatrix.Identity()
				.Set(ClientAPI.Render.CameraMatrixOrigin)
				.Translate(entity.SidedPos.X - originX, entity.SidedPos.InternalY - originY, entity.SidedPos.Z - originZ)
				.Mul(BoxMatrix);

			BoxWireframe.Render(ClientAPI, ModelViewMatrix, 1.6f, color);
		}

		public void Dispose()
		{
			if (Disposed) return;
			Disposed = true;

			ClientAPI.Event.UnregisterRenderer(this, EnumRenderStage.AfterFinalComposition);
			BoxWireframe?.Dispose();
		}
	}

}
