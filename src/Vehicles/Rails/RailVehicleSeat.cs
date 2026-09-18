using System;
using System.IO;
using CollisionFlowFields;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace YangTransport;

/// Lightweight shared seat implementation for rail vehicles.
/// Uses the vanilla EntityBehaviorSeatable pipeline, but keeps the seat itself free of vehicle-control logic.
internal sealed class RailVehicleSeat : EntitySeat
{
	internal const int RequestCollisionDismountPacketID = 1347;

	private const int CollisionCheckIntervalMS = 20;
	private const double CenteredSeatLateralEpsilon = 0.25;

	internal readonly struct CollisionDismountFrame
	{
		internal const int PacketSize = 56;

		internal readonly double SeatX;
		internal readonly double SeatY;
		internal readonly double SeatZ;
		internal readonly double CenterX;
		internal readonly double CenterZ;
		internal readonly float Yaw;
		internal readonly double RailBaseY;
		internal readonly int Dimension;

		internal CollisionDismountFrame(double seatX, double seatY, double seatZ, double centerX, double centerZ, float yaw, double railBaseY, int dimension)
		{
			SeatX = seatX;
			SeatY = seatY;
			SeatZ = seatZ;
			CenterX = centerX;
			CenterZ = centerZ;
			Yaw = yaw;
			RailBaseY = railBaseY;
			Dimension = dimension;
		}
	}

	private readonly EntityPos CachedSeatPosition = new();
	private readonly Vec3d CollisionProbePosition = new();
	private readonly Vec3f TemporaryOffset = new();
	private readonly Vec3f LocalEyePosition = new();

	private long CollisionCheckListenerID;
	private EntityAgent? CollisionCheckPassenger;
	private ICoreClientAPI? CollisionCheckClientAPI;
	private CollisionDismountFrame? PendingCollisionDismountFrame;

	private readonly Matrixf RenderTransformMatrix = new();
	private readonly string MountableClassName;
	private readonly string DefaultMountAnimation;
	private readonly bool UseConfiguredAngleMode;
	private readonly bool UseAttachmentPointAnchor;
	private readonly bool UseConfiguredEyeHeight;

	private const float SlopeRollThreshold = 0.02f; // radians (~1.1d)

	private float RenderTransformYaw;
	private float RenderTransformTilt;
	private bool RenderTransformValid;

	public RailVehicleSeat
	(
		IMountable mounted,
		string seatID,
		SeatConfig seatConfiguration,
		string mountableClassName,
		string defaultMountAnimation = "sitflooridle",
		bool useConfigAngleMode = false,
		bool useAttachmentPointAnchor = false,
		bool useConfigEyeHeight = false
	) : base(mounted, seatID, seatConfiguration)
	{
		this.MountableClassName = mountableClassName;
		this.DefaultMountAnimation = defaultMountAnimation;
		this.UseConfiguredAngleMode = useConfigAngleMode;
		this.UseAttachmentPointAnchor = useAttachmentPointAnchor;
		this.UseConfiguredEyeHeight = useConfigEyeHeight;
	}

	public override AnimationMetaData SuggestedAnimation
	{
		get
		{
			Entity passenger = Passenger;
			var animationsByMetaCode = passenger?.Properties?.Client?.AnimationsByMetaCode;
			if (animationsByMetaCode == null) return null;

			string metaCode = GetMountAnimationCode();
			return animationsByMetaCode.TryGetValue(metaCode, out AnimationMetaData animationMetadata) ? animationMetadata : null;
		}
	}

	public override EnumMountAngleMode AngleMode => UseConfiguredAngleMode ? (config?.AngleMode ?? base.AngleMode) : base.AngleMode;

	public override void MountableToTreeAttributes(TreeAttribute tree)
	{
		base.MountableToTreeAttributes(tree);
		tree.SetLong("entityIdMount", Entity.EntityId);
		tree.SetString("className", MountableClassName);
	}

	public override void DidMount(EntityAgent entityAgent)
	{
		base.DidMount(entityAgent);
		entityAgent?.AnimManager?.StartAnimation(GetMountAnimationCode());
		TryStartCollisionCheck(entityAgent);
		RailSeatDesyncRecovery.Start(this, entityAgent); // This should be removed once the vanilla game fixes the related bug
	}

	public override void DidUnmount(EntityAgent entityAgent)
	{
		RailSeatDesyncRecovery.Stop(this, entityAgent); // This should be removed once the vanilla game fixes the related bug
		StopCollisionCheck();
		entityAgent?.AnimManager?.StopAnimation(GetMountAnimationCode());

		CollisionDismountFrame? collisionFrame = PendingCollisionDismountFrame;
		PendingCollisionDismountFrame = null;

		if (entityAgent?.World?.Side == EnumAppSide.Server && DoTeleportOnUnmount)
		{
			PlacePassengerForDismount(entityAgent, collisionFrame);
		}

		base.DidUnmount(entityAgent);

		// Avoid leaving player roll/renderer tilt behind when dismounting on sloped track.
		if (entityAgent == null) return;

		entityAgent.Pos.Roll = 0f;
		entityAgent.SidedPos.Roll = 0f;
		entityAgent.ServerPos.Roll = 0f;
		if (entityAgent.Properties?.Client?.Renderer is EntityShapeRenderer entityShapeRenderer) { entityShapeRenderer.xangle = 0f; entityShapeRenderer.yangle = 0f; entityShapeRenderer.zangle = 0f; }
	}

	internal static bool CollisionChecksEnabled(Entity entity) { return entity?.Properties?.Attributes?["CollisionCheckSeats"].AsBool(false) == true; }

	private void TryStartCollisionCheck(EntityAgent entityAgent)
	{
		StopCollisionCheck();

		if (entityAgent?.Api is not ICoreClientAPI clientAPI) return;
		if (clientAPI.World?.Player?.Entity?.EntityId != entityAgent.EntityId) return;
		if (!CollisionChecksEnabled(Entity)) return;

		// Subscribe only while the local player occupies an opted-in vehicle.
		CollisionCheckPassenger = entityAgent;
		CollisionCheckClientAPI = clientAPI;
		CollisionCheckListenerID = clientAPI.Event.RegisterGameTickListener(OnCollisionCheckTick, CollisionCheckIntervalMS);
	}

	private void StopCollisionCheck()
	{
		if (CollisionCheckListenerID != 0 && CollisionCheckClientAPI != null) { CollisionCheckClientAPI.Event.UnregisterGameTickListener(CollisionCheckListenerID); }

		CollisionCheckListenerID = 0;
		CollisionCheckPassenger = null;
		CollisionCheckClientAPI = null;
	}

	private void OnCollisionCheckTick(float deltaTime)
	{
		EntityAgent? passenger = CollisionCheckPassenger;
		ICoreClientAPI? clientAPI = CollisionCheckClientAPI;
		if (passenger == null || clientAPI == null) return;

		EntityPos currentSeatPosition = SeatPosition;
		Block collidingBlock = passenger.World.CollisionTester.GetCollidingBlock
		(
			passenger.World.BlockAccessor,
			passenger.CollisionBox,
			CollisionProbePosition.SetWithDimension(currentSeatPosition),
			alsoCheckTouch: false
		);

		// BPFFC proxy colliders are intentional railway geometry (not surrounding terrain).
		if (collidingBlock == null || collidingBlock is BlockFlowCollider) return;

		long entityID = Entity?.EntityId ?? 0;
		CollisionDismountFrame frame = CaptureCurrentDismountFrame(currentSeatPosition);
		byte[] payload = SerializeCollisionDismountFrame(frame);

		StopCollisionCheck();
		if (entityID != 0) clientAPI.Network.SendEntityPacket(entityID, RequestCollisionDismountPacketID, payload);
	}

	private void PlacePassengerForDismount(EntityAgent passenger, CollisionDismountFrame? collisionFrame)
	{
		if (passenger?.World == null) return;

		// Collision-triggered dismounts use the client-captured contact frame so network delay does not move the search origin farther down the track.
		// Manual dismounts continue to use the vehicle's current server-side geometry.
		CollisionDismountFrame frame = collisionFrame ?? CaptureCurrentDismountFrame();

		double rightX = Math.Cos(frame.Yaw);
		double rightZ = -Math.Sin(frame.Yaw);
		double dx = frame.SeatX - frame.CenterX;
		double dz = frame.SeatZ - frame.CenterZ;
		double lateral = dx * rightX + dz * rightZ;

		bool centeredSeat = Math.Abs(lateral) < CenteredSeatLateralEpsilon;
		double outwardSign;
		if (centeredSeat)
		{
			double viewX = Math.Sin(passenger.Pos.Yaw);
			double viewZ = Math.Cos(passenger.Pos.Yaw);
			outwardSign = viewX * rightX + viewZ * rightZ >= 0 ? 1.0 : -1.0;
		}
		else { outwardSign = lateral > 0 ? 1.0 : -1.0; }

		if (TryPlaceToSide(passenger, frame, rightX * outwardSign, rightZ * outwardSign)) return;
		if (centeredSeat && TryPlaceToSide(passenger, frame, -rightX * outwardSign, -rightZ * outwardSign)) return;

		// Preserve the old "drop in place" behavior when the contact/current seat position itself is terrain-clear.
		if (!IsCollidingAt(passenger, frame.SeatX, frame.SeatY, frame.SeatZ, frame.Dimension))
		{
			SetDismountPosition(passenger, frame.SeatX, frame.SeatY, frame.SeatZ);
			return;
		}

		// Emergency fallback | Remove only the lateral component so the rider is moved back into the rail corridor,
		// but keep the longitudinal position from the same contact frame used for the rest of this dismount.
		double forwardX = Math.Sin(frame.Yaw);
		double forwardZ = Math.Cos(frame.Yaw);
		double alongForward = dx * forwardX + dz * forwardZ;
		double targetX = frame.CenterX + forwardX * alongForward;
		double targetZ = frame.CenterZ + forwardZ * alongForward;

		if (TryPlaceOnCenterline(passenger, targetX, targetZ, frame.RailBaseY, frame.Dimension)) return;

		// Last line of defense | The exact seat height may still be clear on the centerline even if unusual terrain invalidated the rail-level choices.
		if (!IsCollidingAt(passenger, targetX, frame.SeatY, targetZ, frame.Dimension)) { SetDismountPosition(passenger, targetX, frame.SeatY, targetZ); }
	}

	private bool TryPlaceToSide(EntityAgent passenger, CollisionDismountFrame frame, double sideX, double sideZ)
	{
		IWorldAccessor world = passenger.World;
		IBlockAccessor blockAccessor = world.BlockAccessor;

		double targetX = frame.SeatX + sideX;
		double targetZ = frame.SeatZ + sideZ;
		int blockX = (int)Math.Floor(targetX);
		int blockZ = (int)Math.Floor(targetZ);

		// Try ordinary ground first, then a one-block-higher station platform.
		for (int heightOffset = 0; heightOffset <= 1; heightOffset++)
		{
			double targetY = frame.RailBaseY + heightOffset + 0.01;
			int supportY = (int)Math.Floor(targetY - 0.1);
			BlockPos supportPosition = new(blockX, supportY, blockZ, frame.Dimension);
			Block support = blockAccessor.GetBlock(supportPosition, BlockLayersAccess.MostSolid);

			if (!support.SideSolid[BlockFacing.UP.Index]) continue;

			// Keep the precise vehicle-relative X/Z. Snapping to world block centers creates arbitrary longitudinal shifts, especially on curves/diagonals.
			if (IsCollidingAt(passenger, targetX, targetY, targetZ, frame.Dimension)) continue;

			SetDismountPosition(passenger, targetX, targetY, targetZ);
			return true;
		}

		return false;
	}

	private bool TryPlaceOnCenterline(EntityAgent passenger, double targetX, double targetZ, double railBaseY, int dimension)
	{
		// Standard-gauge clearance is ~three~ blocks high. Trying the rail level and the two levels above keeps this
		// fallback tiny while avoiding forced placement in terrain if the lowest standing height happens to be obstructed.
		for (int heightOffset = 0; heightOffset <= 2; heightOffset++)
		{
			double targetY = railBaseY + heightOffset + 0.01;
			if (IsCollidingAt(passenger, targetX, targetY, targetZ, dimension)) continue;

			SetDismountPosition(passenger, targetX, targetY, targetZ);
			return true;
		}

		return false;
	}

	private static void SetDismountPosition(EntityAgent passenger, double x, double y, double z)
	{
		// Dismounts are nearby same-dimension relocations. EntityPlayer.TeleportTo() broadcasts vanilla teleport packet 1,
		// which clients can incorrectly apply to the mount while their mountedOn state is still catching up.
		passenger.Pos.SetPos(x, y, z);
		passenger.PreviousServerPos.SetPos(-99, -99, -99);
		passenger.PositionBeforeFalling.Set(x, y, z);
		passenger.Pos.Motion.Set(0.0, 0.0, 0.0);

		if (passenger is EntityPlayer player)
		{
			player.UpdatePartitioning();
			player.WatchedAttributes.GetIntAndIncrement("positionVersionNumber");
		}

		// TryUnmount removes mountedOn immediately after DidUnmount returns.
		// This update therefore carries both the unmounted state and the authoritative dismount position without using an unsafe teleport packet path.
		passenger.WatchedAttributes.MarkAllDirty();
	}

	private CollisionDismountFrame CaptureCurrentDismountFrame(EntityPos? currentSeatPosition = null)
	{
		EntityPos seatPosition = currentSeatPosition ?? SeatPosition;
		GetVehicleHorizontalFrame(out double centerX, out double centerZ, out float yaw);

		// Rail vehicle positions sit at track/standing level.
		// Passenger seat Y can be several blocks higher, so it is not suitable as the ground search origin.
		double railBaseY = Math.Floor((Entity?.Pos ?? mountedEntity.Position).Y + 1e-6);

		return new CollisionDismountFrame
		(
			seatPosition.X,
			seatPosition.Y,
			seatPosition.Z,
			centerX,
			centerZ,
			yaw,
			railBaseY,
			seatPosition.Dimension
		);
	}

	private static byte[] SerializeCollisionDismountFrame(in CollisionDismountFrame frame)
	{
		using MemoryStream memoryStream = new(CollisionDismountFrame.PacketSize);
		using BinaryWriter writer = new(memoryStream);
		writer.Write(frame.SeatX);
		writer.Write(frame.SeatY);
		writer.Write(frame.SeatZ);
		writer.Write(frame.CenterX);
		writer.Write(frame.CenterZ);
		writer.Write(frame.Yaw);
		writer.Write(frame.RailBaseY);
		writer.Write(frame.Dimension);
		return memoryStream.ToArray();
	}

	internal static bool TryDeserializeCollisionDismountFrame(byte[] serializedFrame, out CollisionDismountFrame frame)
	{
		frame = default;
		if (serializedFrame == null || serializedFrame.Length != CollisionDismountFrame.PacketSize) return false;

		try
		{
			using MemoryStream memoryStream = new(serializedFrame, writable: false);
			using BinaryReader reader = new(memoryStream);

			double seatX = reader.ReadDouble();
			double seatY = reader.ReadDouble();
			double seatZ = reader.ReadDouble();
			double centerX = reader.ReadDouble();
			double centerZ = reader.ReadDouble();
			float yaw = reader.ReadSingle();
			double railBaseY = reader.ReadDouble();
			int dimension = reader.ReadInt32();

			if
			(
				!double.IsFinite(seatX) || !double.IsFinite(seatY) || !double.IsFinite(seatZ) ||
				!double.IsFinite(centerX) || !double.IsFinite(centerZ) || !float.IsFinite(yaw) || !double.IsFinite(railBaseY)
			) { return false; }

			frame = new CollisionDismountFrame(seatX, seatY, seatZ, centerX, centerZ, yaw, railBaseY, dimension);
			return true;
		}
		catch (EndOfStreamException) { return false; }
	}

	internal void ServerSetCollisionDismountFrame(in CollisionDismountFrame frame) { PendingCollisionDismountFrame = frame; }
	internal void ServerClearCollisionDismountFrame() { PendingCollisionDismountFrame = null; }

	private static bool IsCollidingAt(EntityAgent passenger, double x, double y, double z, int dimension)
	{
		// CollisionTester expects a dimension-aware Y coordinate, while TeleportTo() expects the ordinary dimension-local entity Y.
		Vec3d collisionPosition = new(x, y + dimension * 32768.0, z);
		return passenger.World.CollisionTester.IsColliding
		(
			passenger.World.BlockAccessor,
			passenger.CollisionBox,
			collisionPosition,
			alsoCheckTouch: false
		);
	}

	private void GetVehicleHorizontalFrame(out double centerX, out double centerZ, out float yaw)
	{
		if (Entity is EntityStandardGaugeLocomotive && SGLocomotiveBodyTransform.TryGetBodyPose(Entity, out SGLocomotiveBodyPose pose))
		{
			centerX = pose.X;
			centerZ = pose.Z;
			yaw = pose.Yaw;
			return;
		}

		EntityPos vehiclePosition = Entity?.Pos ?? mountedEntity.Position;
		centerX = vehiclePosition.X;
		centerZ = vehiclePosition.Z;
		yaw = vehiclePosition.Yaw;
	}

	public override Vec3f LocalEyePos
	{
		get
		{
			if (UseConfiguredEyeHeight && config != null)
			{
				LocalEyePosition.Set(config.EyeOffsetX, config.EyeHeight, 0f);
				return LocalEyePosition;
			}

			float mountY = config?.MountOffset?.Y ?? 0.45f;
			LocalEyePosition.Set(0f, mountY + 0.15f, 0f);
			return LocalEyePosition;
		}
	}

	public override EntityPos SeatPosition
	{
		get
		{
			Vec3f mountOffset = config?.MountOffset;
			if (mountOffset != null) TemporaryOffset.Set(mountOffset);
			else TemporaryOffset.Set(0, 0, 0);

			if
			(
				Entity is EntityStandardGaugeLocomotive && UseAttachmentPointAnchor &&
				SGLocomotiveBodyTransform.TryGetAttachmentPointWorld
				(
					Entity, config?.APName, TemporaryOffset, out double seatWorldX, out double seatWorldY,
					out double seatWorldZ, out float seatWorldYaw, out float seatWorldRoll
				)
			)
			{
				CachedSeatPosition.SetFrom(mountedEntity.Position);
				CachedSeatPosition.X = seatWorldX;
				CachedSeatPosition.Y = seatWorldY - CachedSeatPosition.DimensionYAdjustment;
				CachedSeatPosition.Z = seatWorldZ;
				CachedSeatPosition.Yaw = seatWorldYaw;
				CachedSeatPosition.Roll = seatWorldRoll;
				CachedSeatPosition.Pitch = 0f;
				return CachedSeatPosition;
			}

			float localOffsetX = 0f, localOffsetY = 0f, localOffsetZ = 0f;
			bool anchoredToAttachmentPoint = UseAttachmentPointAnchor && TryGetLocalAttachmentPoint(out localOffsetX, out localOffsetY, out localOffsetZ);

			localOffsetX += TemporaryOffset.X;
			localOffsetY += TemporaryOffset.Y;
			localOffsetZ += TemporaryOffset.Z;

			// Attachment points are authored in model space and use the vanilla +90 degree yaw convention.
			// Mount-offset seats are authored directly in vehicle-local coordinates.
			float yaw = Entity.Pos.Yaw;
			float localYaw = anchoredToAttachmentPoint ? yaw + GameMath.PIHALF : yaw;
			double yawCosine = Math.Cos(localYaw);
			double yawSine = Math.Sin(localYaw);

			double rotatedOffsetX = localOffsetX * yawCosine - localOffsetZ * yawSine;
			double rotatedOffsetZ = localOffsetX * yawSine + localOffsetZ * yawCosine;

			CachedSeatPosition.SetFrom(mountedEntity.Position);
			CachedSeatPosition.X += rotatedOffsetX;
			CachedSeatPosition.Y += localOffsetY;
			CachedSeatPosition.Z += rotatedOffsetZ;
			CachedSeatPosition.Yaw = yaw;
			CachedSeatPosition.Roll = 0f;
			CachedSeatPosition.Pitch = 0f;

			return CachedSeatPosition;
		}
	}

	public override Matrixf RenderTransform
	{
		get
		{
			// Apply rail/body slope tilt in RenderTransform (pre-player-yaw), so it stays correct even when the player looks sideways.
			float tilt = mountedEntity.Position.Roll;
			float yaw = mountedEntity.Position.Yaw + GameMath.PIHALF;

			if (Entity is EntityStandardGaugeLocomotive && SGLocomotiveBodyTransform.TryGetBodyPose(Entity, out SGLocomotiveBodyPose pose))
			{
				tilt = pose.Roll;
				yaw = pose.Yaw + GameMath.PIHALF;
			}

			if (Math.Abs(tilt) <= SlopeRollThreshold) return null;

			if (!RenderTransformValid || Math.Abs(yaw - RenderTransformYaw) > 1e-5f || Math.Abs(tilt - RenderTransformTilt) > 1e-5f)
			{
				RenderTransformValid = true;
				RenderTransformYaw = yaw;
				RenderTransformTilt = tilt;
				RenderTransformMatrix.Identity();

				RenderTransformMatrix.RotateY(yaw);
				RenderTransformMatrix.RotateZ(tilt);
				RenderTransformMatrix.RotateY(-yaw);
			}

			return RenderTransformMatrix;
		}
	}

	public override float FpHandPitchFollow => 0.2f;

	public static IMountableSeat GetMountable(IWorldAccessor worldAccessor, TreeAttribute treeAttributes)
	{
		if (treeAttributes == null) return null;

		long entityID = treeAttributes.GetLong("entityIdMount", 0); if (entityID == 0) return null;
		string seatID = treeAttributes.GetString("seatId", null); if (seatID == null) return null;

		var mountedEntity = worldAccessor.GetEntityById(entityID);
		var seatableBehavior = mountedEntity?.GetBehavior<EntityBehaviorSeatable>();
		var mountableSeats = seatableBehavior?.Seats; if (mountableSeats == null) return null;
		for (int seatIndex = 0; seatIndex < mountableSeats.Length; seatIndex++) { if (mountableSeats[seatIndex]?.SeatId == seatID) return mountableSeats[seatIndex]; }

		return null;
	}

	private bool TryGetLocalAttachmentPoint(out float x, out float y, out float z)
	{
		x = y = z = 0f;

		string attachmentPointName = config?.APName;
		if (string.IsNullOrEmpty(attachmentPointName)) return false;

		var attachmentPointPose = Entity?.AnimManager?.Animator?.GetAttachmentPointPose(attachmentPointName);
		if (attachmentPointPose == null) return false;

		float[] animationModelMatrix = attachmentPointPose.AnimModelMatrix;
		if (animationModelMatrix == null || animationModelMatrix.Length < 16 || attachmentPointPose.AttachPoint == null) return false;

		var attachmentPoint = attachmentPointPose.AttachPoint;
		float attachmentPointX = (float)attachmentPoint.PosX / 16f;
		float attachmentPointY = (float)attachmentPoint.PosY / 16f;
		float attachmentPointZ = (float)attachmentPoint.PosZ / 16f;

		// Position of the attachment point in model space, including the parent element transform.
		float transformedX = animationModelMatrix[0] * attachmentPointX + animationModelMatrix[4] * attachmentPointY + animationModelMatrix[8] * attachmentPointZ + animationModelMatrix[12];
		float transformedY = animationModelMatrix[1] * attachmentPointX + animationModelMatrix[5] * attachmentPointY + animationModelMatrix[9] * attachmentPointZ + animationModelMatrix[13];
		float transformedZ = animationModelMatrix[2] * attachmentPointX + animationModelMatrix[6] * attachmentPointY + animationModelMatrix[10] * attachmentPointZ + animationModelMatrix[14];

		float entityScale = Entity?.Properties?.Client?.Size ?? 1f;

		// Match vanilla entity model centering. This intentionally mirrors EntityBehaviorSelectionBoxes, so seated players line up with the same AP box players click.
		x = (transformedX - 0.5f) * entityScale; y = transformedY * entityScale; z = (transformedZ - 0.5f) * entityScale;
		return true;
	}

	private string GetMountAnimationCode()
	{
		string metaCode = config?.Animation;
		return string.IsNullOrEmpty(metaCode) ? DefaultMountAnimation : metaCode;
	}
}
