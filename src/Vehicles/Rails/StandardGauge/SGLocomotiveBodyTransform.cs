using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace YangTransport;

public readonly struct SGLocomotiveBodyPose
{
	public readonly double X;
	public readonly double Y;
	public readonly double Z;
	public readonly float Yaw;
	public readonly float Roll;

	public SGLocomotiveBodyPose(double x, double y, double z, float yaw, float roll)
	{
		X = x; Y = y; Z = z;
		Yaw = yaw; Roll = roll;
	}
}

public interface ISGLocomotiveBodyPoseProvider { bool TryGetBodyPose(out SGLocomotiveBodyPose pose); }

public interface ISGLocomotiveBogiePoseProvider
{
	bool TryGetBogieCenterWorldPositions
	(
		out double frontX, out double frontY, out double frontZ,
		out double rearX, out double rearY, out double rearZ
	);
}

internal static class SGLocomotiveBodyTransform
{
	internal const double RideHeight = 0.125;

	public static void GetRenderOrigin(ICoreClientAPI clientAPI, Entity entity, out double originX, out double originY, out double originZ)
	{
		EntityPlayer playerEntity = clientAPI?.World?.Player?.Entity;
		IMountableSeat seat = playerEntity?.MountedOn;

		if (seat != null && (seat.Entity == entity || seat.MountSupplier?.OnEntity == entity))
		{
			EntityPos seatPosition = seat.SeatPosition;
			originX = seatPosition.X;
			originY = seatPosition.InternalY;
			originZ = seatPosition.Z;
			return;
		}

		Vec3d cameraPosition = playerEntity?.CameraPos ?? entity.SidedPos.XYZ;
		originX = cameraPosition.X;
		originY = cameraPosition.Y;
		originZ = cameraPosition.Z;
	}

	public static bool TryGetBodyPose(Entity entity, out SGLocomotiveBodyPose pose)
	{
		if (entity == null) { pose = default; return false; }

		// Renderer-provided SG body poses are only refreshed while the entity is render-active.
		// If the locomotive was culled, the renderer may still report its last pose, which would freeze mounted passengers at the last rendered body position.
		// Fall back to entity state whenever the entity is not currently being rendered.
		if
		(
			entity.Api?.Side == EnumAppSide.Client &&
			entity.IsRendered &&
			entity.Properties?.Client?.Renderer is ISGLocomotiveBodyPoseProvider provider &&
			provider.TryGetBodyPose(out pose)
		) { return true; }

		return TryGetFallbackBodyPose(entity, out pose);
	}

	public static bool TryGetAttachmentPointWorld
	(
		Entity entity, string attachmentPointName, Vec3f mountOffset,
		out double x, out double y, out double z,
		out float yaw, out float roll
	)
	{
		x = y = z = 0;
		yaw = roll = 0;

		if (entity == null || string.IsNullOrEmpty(attachmentPointName)) return false;
		if (!TryGetBodyPose(entity, out SGLocomotiveBodyPose pose)) return false;

		AttachmentPointAndPose attachmentPointAndPose = entity.AnimManager?.Animator?.GetAttachmentPointPose(attachmentPointName);
		if (attachmentPointAndPose?.AttachPoint == null || attachmentPointAndPose.AnimModelMatrix == null || attachmentPointAndPose.AnimModelMatrix.Length < 16) return false;

		Matrixf bodyMatrix = new Matrixf();
		EntityPos origin = entity.SidedPos;
		BuildBodyModelMatrix(bodyMatrix, entity, pose, origin.X, origin.InternalY, origin.Z);

		Vec3f offsetWorld = null;
		if (mountOffset != null && (Math.Abs(mountOffset.X) > 1e-6f || Math.Abs(mountOffset.Y) > 1e-6f || Math.Abs(mountOffset.Z) > 1e-6f))
		{
			offsetWorld = TransformVector(bodyMatrix, mountOffset.X, mountOffset.Y, mountOffset.Z);
		}

		Matrixf attachmentPointMatrix = new Matrixf(bodyMatrix.Values);
		attachmentPointMatrix.Mul(attachmentPointAndPose.AnimModelMatrix);

		AttachmentPoint attachmentPoint = attachmentPointAndPose.AttachPoint;
		GetCenteredAttachmentPointPosition(attachmentPoint, out float attachmentPointX, out float attachmentPointY, out float attachmentPointZ);
		Vec3f relativePosition = TransformPoint(attachmentPointMatrix, attachmentPointX, attachmentPointY, attachmentPointZ);

		x = origin.X + relativePosition.X;
		y = origin.InternalY + relativePosition.Y;
		z = origin.Z + relativePosition.Z;

		if (offsetWorld != null)
		{
			x += offsetWorld.X;
			y += offsetWorld.Y;
			z += offsetWorld.Z;
		}

		yaw = pose.Yaw;
		roll = pose.Roll;
		return true;
	}

	public static bool TryTransformBodyLocalPointWorld(Entity entity, Vec3f localPosition, Matrixf bodyMatrix, out double x, out double y, out double z)
	{
		x = y = z = 0;
		if (entity == null || localPosition == null || bodyMatrix == null) return false;
		if (!TryGetBodyPose(entity, out SGLocomotiveBodyPose pose)) return false;

		EntityPos origin = entity.SidedPos;
		BuildBodyModelMatrix(bodyMatrix, entity, pose, origin.X, origin.InternalY, origin.Z);

		float[] matrixValues = bodyMatrix.Values;
		float localX = localPosition.X;
		float localY = localPosition.Y;
		float localZ = localPosition.Z;

		double relativeX = matrixValues[0] * localX + matrixValues[4] * localY + matrixValues[8] * localZ + matrixValues[12];
		double relativeY = matrixValues[1] * localX + matrixValues[5] * localY + matrixValues[9] * localZ + matrixValues[13];
		double relativeZ = matrixValues[2] * localX + matrixValues[6] * localY + matrixValues[10] * localZ + matrixValues[14];

		x = origin.X + relativeX;
		y = origin.InternalY + relativeY;
		z = origin.Z + relativeZ;

		return true;
	}

	public static void BuildBodySelectionBoxMatrix(Matrixf matrix, Entity entity, SGLocomotiveBodyPose pose, AttachmentPointAndPose attachmentPointAndPose)
	{
		BuildBodyModelMatrix(matrix, entity, pose, entity.SidedPos.X, entity.SidedPos.InternalY, entity.SidedPos.Z);
		matrix.Mul(attachmentPointAndPose.AnimModelMatrix);

		ShapeElement shapeElement = attachmentPointAndPose.AttachPoint.ParentElement;
		float scaleX = (float)(shapeElement.To[0] - shapeElement.From[0]) / 16f;
		float scaleY = (float)(shapeElement.To[1] - shapeElement.From[1]) / 16f;
		float scaleZ = (float)(shapeElement.To[2] - shapeElement.From[2]) / 16f;

		matrix.Scale(scaleX, scaleY, scaleZ);
	}

	private static bool TryGetFallbackBodyPose(Entity entity, out SGLocomotiveBodyPose pose)
	{
		EntityPos entityPosition = entity.Pos ?? entity.SidedPos;

		double bodyOffsetForward = 0;
		double bodyOffsetLateral = 0;
		double bodyOffsetVertical = 0;
		double frontBogieOffset = 0;
		double rearBogieOffset = 0;

		EntityBehaviorStandardGaugeLocomotiveStats locomotiveStatistics = entity.GetBehavior<EntityBehaviorStandardGaugeLocomotiveStats>();
		if (locomotiveStatistics != null)
		{
			bodyOffsetForward = locomotiveStatistics.BodyOffsetForward;
			bodyOffsetLateral = locomotiveStatistics.BodyOffsetLateral;
			bodyOffsetVertical = locomotiveStatistics.BodyOffsetVertical;
			frontBogieOffset = locomotiveStatistics.FrontBogieOffset;
			rearBogieOffset = locomotiveStatistics.RearBogieOffset;
		}

		ForwardVectorFromYawRoll(entityPosition.Yaw, entityPosition.Roll, out double fallbackForwardX, out double fallbackForwardY, out double fallbackForwardZ);

		double bogieSpan = Math.Abs(rearBogieOffset - frontBogieOffset);
		double frontX = entityPosition.X;
		double frontY = entityPosition.InternalY;
		double frontZ = entityPosition.Z;
		double rearX = entityPosition.X - fallbackForwardX * bogieSpan;
		double rearY = entityPosition.InternalY - fallbackForwardY * bogieSpan;
		double rearZ = entityPosition.Z - fallbackForwardZ * bogieSpan;

		double bogieDirectionX = frontX - rearX;
		double bogieDirectionY = frontY - rearY;
		double bogieDirectionZ = frontZ - rearZ;
		if (Math.Abs(bogieDirectionX) + Math.Abs(bogieDirectionY) + Math.Abs(bogieDirectionZ) < 1e-9)
		{
			bogieDirectionX = fallbackForwardX;
			bogieDirectionY = fallbackForwardY;
			bogieDirectionZ = fallbackForwardZ;
		}

		YawRollFromVector(bogieDirectionX, bogieDirectionY, bogieDirectionZ, out float bodyYaw, out float bodyRoll);
		NormalizeSafe(bogieDirectionX, bogieDirectionY, bogieDirectionZ, out double forwardX, out double forwardY, out double forwardZ);

		double rightX = forwardZ;
		double rightZ = -forwardX;
		double rightLength = Math.Sqrt(rightX * rightX + rightZ * rightZ);
		if (rightLength < 1e-9)
		{
			rightX = Math.Cos(bodyYaw);
			rightZ = -Math.Sin(bodyYaw);
		}
		else
		{
			rightX /= rightLength;
			rightZ /= rightLength;
		}

		double bodyX = frontX + forwardX * (frontBogieOffset + bodyOffsetForward) + rightX * bodyOffsetLateral;
		double bodyY = frontY + forwardY * (frontBogieOffset + bodyOffsetForward) + bodyOffsetVertical + RideHeight;
		double bodyZ = frontZ + forwardZ * (frontBogieOffset + bodyOffsetForward) + rightZ * bodyOffsetLateral;

		pose = new SGLocomotiveBodyPose(bodyX, bodyY, bodyZ, bodyYaw, bodyRoll);
		return true;
	}

	private static void BuildBodyModelMatrix(Matrixf matrix, Entity entity, SGLocomotiveBodyPose pose, double originX, double originY, double originZ)
	{
		matrix.Identity();

		matrix.Translate((float)(pose.X - originX), (float)(pose.Y - originY), (float)(pose.Z - originZ));
		matrix.Translate(0f, entity.SelectionBox.Y2 / 2f, 0f);

		float rotationX = entity.Properties?.Client?.Shape?.rotateX ?? 0f;
		float rotationY = entity.Properties?.Client?.Shape?.rotateY ?? 0f;
		float rotationZ = entity.Properties?.Client?.Shape?.rotateZ ?? 0f;

		matrix.RotateX(rotationX * GameMath.DEG2RAD);
		matrix.RotateY(pose.Yaw + (rotationY + 90f) * GameMath.DEG2RAD);
		matrix.RotateZ(pose.Roll + rotationZ * GameMath.DEG2RAD);

		matrix.Translate(0f, -entity.SelectionBox.Y2 / 2f, 0f);

		float scale = entity.Properties?.Client?.Size ?? 1f;
		matrix.Scale(scale, scale, scale);

		// Standard-gauge locomotive bodies are rendered with this centering correction.
		// APs and hitboxes must use the same correction or they drift away from the visible mesh.
		matrix.Translate(-0.5f, 0f, -1f);
	}

	private static void GetCenteredAttachmentPointPosition(AttachmentPoint attachmentPoint, out float x, out float y, out float z)
	{
		x = (float)(attachmentPoint.PosX / 16.0);
		y = (float)(attachmentPoint.PosY / 16.0);
		z = (float)(attachmentPoint.PosZ / 16.0);

		ShapeElement shapeElement = attachmentPoint.ParentElement;
		if (shapeElement == null) return;

		// Our SG seat APs double as selection-box anchors. The AP itself is the cuboid pivot/corner, while the expected mount point is the center of that cuboid.
		x += (float)(shapeElement.To[0] - shapeElement.From[0]) / 32f;
		y += (float)(shapeElement.To[1] - shapeElement.From[1]) / 32f;
		z += (float)(shapeElement.To[2] - shapeElement.From[2]) / 32f;
	}

	private static Vec3f TransformPoint(Matrixf matrix, float x, float y, float z)
	{
		Vec4f inputVector = new Vec4f(x, y, z, 1f);
		Vec4f outputVector = new Vec4f();
		Mat4f.MulWithVec4(matrix.Values, inputVector, outputVector);
		return new Vec3f(outputVector.X, outputVector.Y, outputVector.Z);
	}

	private static Vec3f TransformVector(Matrixf matrix, float x, float y, float z)
	{
		Vec4f inputVector = new Vec4f(x, y, z, 0f);
		Vec4f outputVector = new Vec4f();
		Mat4f.MulWithVec4(matrix.Values, inputVector, outputVector);
		return new Vec3f(outputVector.X, outputVector.Y, outputVector.Z);
	}

	private static void YawRollFromVector(double directionX, double directionY, double directionZ, out float yaw, out float roll)
	{
		yaw = (float)Math.Atan2(directionX, directionZ);

		double horizontalLength = Math.Sqrt(directionX * directionX + directionZ * directionZ);
		roll = horizontalLength < 1e-8 ? 0f : (float)(-Math.Atan2(directionY, horizontalLength));
	}

	private static void NormalizeSafe(double directionX, double directionY, double directionZ, out double normalizedX, out double normalizedY, out double normalizedZ)
	{
		double length = Math.Sqrt(directionX * directionX + directionY * directionY + directionZ * directionZ);
		if (length < 1e-9)
		{
			normalizedX = 0;
			normalizedY = 0;
			normalizedZ = 1;
			return;
		}

		normalizedX = directionX / length;
		normalizedY = directionY / length;
		normalizedZ = directionZ / length;
	}

	private static void ForwardVectorFromYawRoll(float yaw, float roll, out double forwardX, out double forwardY, out double forwardZ)
	{
		double horizontalX = Math.Sin(yaw);
		double horizontalZ = Math.Cos(yaw);
		double verticalDirection = -Math.Tan(roll);

		double length = Math.Sqrt(horizontalX * horizontalX + verticalDirection * verticalDirection + horizontalZ * horizontalZ);
		if (length < 1e-9)
		{
			forwardX = 0;
			forwardY = 0;
			forwardZ = 1;
			return;
		}

		forwardX = horizontalX / length;
		forwardY = verticalDirection / length;
		forwardZ = horizontalZ / length;
	}
}
