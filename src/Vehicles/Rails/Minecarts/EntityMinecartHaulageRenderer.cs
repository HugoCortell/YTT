using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace YangTransport;

/// Shape renderer for minecart haulage engines. The normal VS entity animator still evaluates the authored wheels/rods/pistons.
/// This renderer only locks its "move" animation phase to the distance the interpolated client entity visibly travels.
public sealed class EntityMinecartHaulageRenderer : EntityShapeRenderer
{
	private const string MovementMetadataCode = "move";
	private const double MovementEpsilon = 1e-6;

	private RailMechanicalTravel MechanicalTravel;

	private readonly Vec3f SteamEffectLocalPosition = new();
	private bool HasSteamEffectLocalPosition;
	private bool HasSteamEffectWorldPosition;
	private double SteamEffectWorldX;
	private double SteamEffectWorldY;
	private double SteamEffectWorldZ;

	private AnimationMetaData? MovementAnimation;
	private IAnimator? CachedAnimator;
	private RunningAnimation? MovementAnimationState;
	private bool AnimatorPoseEvaluated;
	private bool ResyncTravelOnNextRender;
	private double DistancePerCycle = RailMechanicalAnimation.DefaultSmallWheelDistancePerCycle;
	private double AnimationDirection = 1.0;
	private double PhaseOffset;

	public EntityMinecartHaulageRenderer(Entity entity, ICoreClientAPI coreClientAPI) : base(entity, coreClientAPI) { }

	public override void OnEntityLoaded()
	{
		base.OnEntityLoaded();

		EntityBehaviorSteamPowered? steamEngine = entity.GetBehavior<EntityBehaviorSteamPowered>();
		if (steamEngine != null)
		{
			steamEngine.CopySteamFxLocalPosition(SteamEffectLocalPosition);
			HasSteamEffectLocalPosition = true;
		}

		if (entity.Properties?.Client?.AnimationsByMetaCode != null &&
			entity.Properties.Client.AnimationsByMetaCode.TryGetValue(MovementMetadataCode, out AnimationMetaData animationMetadata))
		{
			MovementAnimation = animationMetadata.Clone();
			// We set CurrentFrame directly from accumulated travel every render frame.
			// Keeping engine-side progression at zero removes integration drift entirely.
			MovementAnimation.AnimationSpeed = 0f;
			MovementAnimation.EaseInSpeed = 1000f;
			MovementAnimation.EaseOutSpeed = 1000f;
		}

		var animationConfiguration = entity.Properties?.Attributes?["MechanicalAnimation"];
		if (animationConfiguration != null && animationConfiguration.Exists)
		{
			DistancePerCycle = Math.Max(1e-6, animationConfiguration["DistancePerCycle"].AsDouble(DistancePerCycle));
			AnimationDirection = animationConfiguration["Direction"].AsDouble(AnimationDirection);
			PhaseOffset = animationConfiguration["PhaseOffset"].AsDouble(PhaseOffset);
		}

		MechanicalTravel.ResetWorld(entity.Pos.X, entity.Pos.InternalY, entity.Pos.Z);
	}

	/// Called by the client steam-engine behavior while this entity is outside the main render set.
	/// No timing heuristic is needed: the next visible frame simply adopts the already-interpolated world position as its new travel origin.
	internal void MarkCulled()
	{
		ResyncTravelOnNextRender = true;
		HasSteamEffectWorldPosition = false;
	}

	public override void BeforeRender(float deltaTime)
	{
		base.BeforeRender(deltaTime);

		if (MovementAnimation == null || capi.IsGamePaused) return;

		IAnimator? animator = entity.AnimManager?.Animator;
		if (animator == null) return;

		if (!ReferenceEquals(animator, CachedAnimator))
		{
			CachedAnimator = animator;
			MovementAnimationState = animator.GetAnimationState(MovementAnimation.Animation);
			AnimatorPoseEvaluated = false;
		}
		RunningAnimation? animationState = MovementAnimationState;
		if (animationState == null) return;

		if (ResyncTravelOnNextRender)
		{
			MechanicalTravel.ResetWorld(entity.Pos.X, entity.Pos.InternalY, entity.Pos.Z);
			ResyncTravelOnNextRender = false;
		}

		double signedDelta = MechanicalTravel.SampleWorld(entity.Pos.X, entity.Pos.InternalY, entity.Pos.Z, entity.Pos.Yaw);
		double phase = RailMechanicalAnimation.WrapPhase(MechanicalTravel.TotalSignedDistance, DistancePerCycle, AnimationDirection, PhaseOffset);

		bool movingThisFrame = Math.Abs(signedDelta) > MovementEpsilon;
		float frame = RailMechanicalAnimation.PhaseToAnimationFrame(phase, animationState.Animation.QuantityFrames);
		if (!animationState.Active)
		{
			if (!movingThisFrame)
			{
				// GPU skinning can use identity matrices for the stationary base mesh, but attachment points depend on the animator's hierarchical AnimModelMatrix.
				// Give every newly-created animator exactly one base-pose evaluation before freezing it,
				// otherwise APs remain at their uninitialized local transform until the engine moves for the first time.
				animator.CalculateMatrices = !AnimatorPoseEvaluated;
				AnimatorPoseEvaluated = true;
				return;
			}

			// AnimNowActive consumes StartFrameOnce on the immediately following AnimManager.OnClientFrame(),
			// so the very first visible moving frame starts at the correct distance-derived phase rather than briefly flashing frame 0.
			MovementAnimation.StartFrameOnce = frame;
			entity.AnimManager.StartAnimation(MovementAnimation);
		}

		// AnimationSpeed is intentionally zero.
		// The generic animator still evaluates the authored animated joints, but phase is an absolute function of rendered distance.
		animationState.CurrentFrame = frame;
		animationState.EasingFactor = 1f;
		animationState.BlendedWeight = 1f;

		// A stopped visible engine should retain its exact wheel/rod pose without paying the generic recursive matrix traversal every render frame.
		// A replacement animator still gets one evaluation so attachment-point hierarchy data is initialized.
		animator.CalculateMatrices = movingThisFrame || !AnimatorPoseEvaluated;
		AnimatorPoseEvaluated = true;
	}

	public override void DoRender3DOpaque(float deltaTime, bool isShadowPass)
	{
		base.DoRender3DOpaque(deltaTime, isShadowPass);

		if (!isShadowPass && !isSpectator && HasSteamEffectLocalPosition) { CacheSteamEffectWorldPosition(); }
	}

	internal bool TryGetSteamEffectWorldPosition(out double x, out double y, out double z)
	{
		x = SteamEffectWorldX; y = SteamEffectWorldY; z = SteamEffectWorldZ;
		return HasSteamEffectWorldPosition;
	}

	private void CacheSteamEffectWorldPosition()
	{
		float[] modelMatrix = ModelMat;
		float localX = SteamEffectLocalPosition.X;
		float localY = SteamEffectLocalPosition.Y;
		float localZ = SteamEffectLocalPosition.Z;

		double relativeX = modelMatrix[0] * localX + modelMatrix[4] * localY + modelMatrix[8] * localZ + modelMatrix[12];
		double relativeY = modelMatrix[1] * localX + modelMatrix[5] * localY + modelMatrix[9] * localZ + modelMatrix[13];
		double relativeZ = modelMatrix[2] * localX + modelMatrix[6] * localY + modelMatrix[10] * localZ + modelMatrix[14];

		GetRenderOrigin(out double originX, out double originY, out double originZ);
		SteamEffectWorldX = originX + relativeX;
		SteamEffectWorldY = originY + relativeY;
		SteamEffectWorldZ = originZ + relativeZ;
		HasSteamEffectWorldPosition = true;
	}

	private void GetRenderOrigin(out double x, out double y, out double z)
	{
		EntityPlayer playerEntity = capi.World.Player.Entity;
		IMountableSeat seat = playerEntity.MountedOn;

		if (seat != null && (seat.Entity == entity || seat.MountSupplier?.OnEntity == entity))
		{
			EntityPos seatPos = seat.SeatPosition;
			x = seatPos.X; y = seatPos.InternalY; z = seatPos.Z;
			return;
		}

		Vec3d cameraPos = playerEntity.CameraPos;
		x = cameraPos.X; y = cameraPos.Y; z = cameraPos.Z;
	}
}
