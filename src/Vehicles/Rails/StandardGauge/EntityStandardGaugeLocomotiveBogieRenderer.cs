using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace YangTransport;

/// Client-only SG renderer. Bogies are resolved from a branch-correct convoy route and the normal interpolated entity source position. Movement history is not used.
public sealed class EntityStandardGaugeLocomotiveBogieRenderer : EntityShapeRenderer, ISGLocomotiveBodyPoseProvider, ISGLocomotiveBogiePoseProvider
{
	private const byte TrackGauge = 1;
	private const double BogieRotationSmoothRadius = 0.75;
	private const double BogieRotationSmoothMinChord = 0.12;
	private const int BogieAnimationSampleCount = 120;

	private double BodyOffsetForward;
	private double BodyOffsetLateral;
	private double BodyOffsetVertical;

	private RenderBogiePart[] BogieParts =
	{
		new RenderBogiePart(new SgLocomotiveBogieRenderSpec("locomotives/bogies/default", 0)),
		new RenderBogiePart(new SgLocomotiveBogieRenderSpec("locomotives/bogies/default", 4))
	};

	private double FrontBogieOffset;
	private double RearBogieOffset;
	private int FrontBogieIndex;
	private int RearBogieIndex;

	private static readonly Dictionary<string, BogieMeshCacheEntry> BogieMeshCache = new();
	// CPU-side palettes are tiny and retained for the client session even if every vehicle using a shape unloads.
	// This prevents expensive hierarchy re-bakes when trains stream back into range while still allowing GPU meshes to be released.
	private static readonly Dictionary<string, BogieAnimationClip> BogieAnimationCache = new();

	private readonly float[] BodyMatrix = Mat4f.Create();
	private readonly float[] TemporaryLocalModelViewMatrix = Mat4f.Create();

	private EntityStandardGaugeLocomotive? StandardGaugeEntity;
	private ConvoyRouteNetworkSystem? RouteNetwork;

	private ConvoyRouteProjectionHint SourceProjectionHint = new() { RunIndex = -1 };
	private long ProjectionConvoyID;
	private int ProjectionExpectedEpoch = int.MinValue;
	private long RequiredAnchorGeneration;
	private bool RouteTravelNeedsReset = true;
	private RailMechanicalTravel MechanicalTravel;

	private SGLocomotiveBodyPose LastBodyPose;
	private bool HasBodyPose;

	public EntityStandardGaugeLocomotiveBogieRenderer(Entity entity, ICoreClientAPI clientAPI) : base(entity, clientAPI) { }

	public override void OnEntityLoaded()
	{
		base.OnEntityLoaded();

		StandardGaugeEntity = entity as EntityStandardGaugeLocomotive;
		RouteNetwork = capi.ModLoader.GetModSystem<ConvoyRouteNetworkSystem>();

		var locomotiveStatistics = entity.GetBehavior<EntityBehaviorStandardGaugeLocomotiveStats>();
		if (locomotiveStatistics != null)
		{
			BodyOffsetForward = locomotiveStatistics.BodyOffsetForward;
			BodyOffsetLateral = locomotiveStatistics.BodyOffsetLateral;
			BodyOffsetVertical = locomotiveStatistics.BodyOffsetVertical;
			ConfigureBogies(locomotiveStatistics.Bogies);
		}
		else { ResolveFrontRearBogies(); }

		EnsureBogieMeshes();
	}

	private void ConfigureBogies(SgLocomotiveBogieRenderSpec[] bogieSpecifications)
	{
		if (bogieSpecifications == null || bogieSpecifications.Length == 0) return;

		ReleaseBogieMeshes();

		BogieParts = new RenderBogiePart[bogieSpecifications.Length];
		for (int bogieIndex = 0; bogieIndex < bogieSpecifications.Length; bogieIndex++)
		{
			BogieParts[bogieIndex] = new RenderBogiePart(bogieSpecifications[bogieIndex]?.Clone() ?? new SgLocomotiveBogieRenderSpec("locomotives/bogies/default", 0));
		}

		ResolveFrontRearBogies();
		SourceProjectionHint.Reset();
	}

	private void ResolveFrontRearBogies()
	{
		FrontBogieIndex = 0;
		RearBogieIndex = 0;
		FrontBogieOffset = BogieParts.Length == 0 ? 0 : BogieParts[0].BogieSpecification.OffsetForward;
		RearBogieOffset = FrontBogieOffset;

		for (int bogieIndex = 1; bogieIndex < BogieParts.Length; bogieIndex++)
		{
			double bogieOffset = BogieParts[bogieIndex].BogieSpecification.OffsetForward;
			if (bogieOffset < FrontBogieOffset)
			{
				FrontBogieOffset = bogieOffset;
				FrontBogieIndex = bogieIndex;
			}
			if (bogieOffset > RearBogieOffset)
			{
				RearBogieOffset = bogieOffset;
				RearBogieIndex = bogieIndex;
			}
		}

		if (BogieParts.Length == 1)
		{
			RearBogieIndex = FrontBogieIndex;
			RearBogieOffset = FrontBogieOffset;
		}
	}

	private void EnsureBogieMeshes()
	{
		for (int bogieIndex = 0; bogieIndex < BogieParts.Length; bogieIndex++)
		{
			RenderBogiePart bogiePart = BogieParts[bogieIndex];
			if (bogiePart.MeshReference != null) continue;

			AssetLocation shapeLocation = ResolveShapeAssetLocation(bogiePart.BogieSpecification.ShapePath);
			string animationCode = string.IsNullOrWhiteSpace(bogiePart.BogieSpecification.AnimationCode) ? "move" : bogiePart.BogieSpecification.AnimationCode;
			string cacheKey = shapeLocation + "|anim:" + animationCode.ToLowerInvariant();

			if (!BogieMeshCache.TryGetValue(cacheKey, out BogieMeshCacheEntry cachedEntry))
			{
				BogieMeshCacheEntry? createdEntry = CreateBogieCacheEntry(shapeLocation, animationCode);
				if (createdEntry == null) continue;

				cachedEntry = createdEntry;
				BogieMeshCache[cacheKey] = cachedEntry;
			}

			cachedEntry.Users++;
			bogiePart.CacheKey = cacheKey;
			bogiePart.MeshReference = cachedEntry.MeshReference;
			bogiePart.AnimationClip = cachedEntry.AnimationClip;
		}
	}

	private BogieMeshCacheEntry? CreateBogieCacheEntry(AssetLocation shapeLocation, string animationCode)
	{
		try
		{
			var shapeAsset = capi.Assets.TryGet(shapeLocation);
			if (shapeAsset == null) { capi.Logger.Warning("[YangTransport] Missing SG bogie shape: {0}", shapeLocation); return null; }

			var shape = shapeAsset.ToObject<Shape>();

			// Asset.ToObject<Shape>() only deserializes the JSON.
			// Unlike the normal Vintage Story shape-loading path, it has not yet populated each ShapeElement.ParentElement reference.
			// InitForAnimations() caches inverse bind transforms before it resolves references itself,
			// so nested animated parts would otherwise get inverse matrices as if they were root elements.
			// Resolve once up front so skinning pivots use the authored hierarchy.
			shape.ResolveReferences(capi.Logger, shapeLocation.ToString());

			var textureSource = entity.GetTextureSource();
			if (textureSource == null)
			{
				capi.Logger.Warning("[YangTransport] Entity texture source is null; cannot tesselate SG bogie mesh {0}.", shapeLocation);
				return null;
			}

			BogieAnimationClip? animationClip = null;
			Animation? selectedAnimation = FindFirstAnimation(shape, animationCode);
			if (selectedAnimation != null)
			{
				// Use the first clip in the event that there's duplicates for some reason in the shape file.
				shape.Animations = new[] { selectedAnimation };
				shape.InitForAnimations(capi.Logger, shapeLocation.ToString());

				string clipKey = shapeLocation + "|anim:" + animationCode.ToLowerInvariant();
				if (!BogieAnimationCache.TryGetValue(clipKey, out animationClip))
				{
					animationClip = BakeAnimationClip(shape, selectedAnimation, BogieAnimationSampleCount);
					if (animationClip != null) BogieAnimationCache[clipKey] = animationClip;
				}
			}

			var tesselationMetadata = new TesselationMetaData()
			{
				TexSource = textureSource,
				GeneralGlowLevel = entity.Properties?.Client?.GlowLevel ?? 0,
				TypeForLogging = "YangTransportSgBogie",
				// Entityanimated's vertex layout places damage-effect before joint ids.
				// Keep that slot present whenever the bogie is skinned, otherwise the joint-id stream shifts into the damage slot and the shader reads joint 0.
				WithJointIds = animationClip != null,
				WithDamageEffect = animationClip != null
			};

			capi.Tesselator.TesselateShape(tesselationMetadata, shape, out MeshData meshData);

			if (meshData != null && meshData.VerticesCount > 0) { return new BogieMeshCacheEntry(capi.Render.UploadMultiTextureMesh(meshData), animationClip); }
			capi.Logger.Warning("[YangTransport] Failed to tesselate SG bogie shape {0} (0 vertices).", shapeLocation);
		}
		catch (Exception exception) { capi.Logger.Error(exception); }

		return null;
	}

	private static Animation? FindFirstAnimation(Shape shape, string animationCode)
	{
		Animation[] animations = shape.Animations;
		if (animations == null) return null;

		for (int animationIndex = 0; animationIndex < animations.Length; animationIndex++)
		{
			Animation animation = animations[animationIndex];
			if (animation != null && string.Equals(animation.Code, animationCode, StringComparison.OrdinalIgnoreCase)) { return animation; }
		}

		return null;
	}

	private static BogieAnimationClip? BakeAnimationClip(Shape shape, Animation animation, int sampleCount)
	{
		if (shape.JointsById == null || shape.JointsById.Count == 0 || animation.QuantityFrames <= 0) return null;

		var animator = new ClientAnimator(() => 1.0, new[] { animation }, shape.Elements, shape.JointsById);
		var animationMetadata = new AnimationMetaData
		{
			Code = animation.Code,
			Animation = animation.Code,
			AnimationSpeed = 1f,
			EaseInSpeed = 1000f,
			EaseOutSpeed = 1000f,
			BlendMode = EnumAnimationBlendMode.Add
		}.Init();

		var activeAnimations = new Dictionary<string, AnimationMetaData>(StringComparer.OrdinalIgnoreCase) { [animation.Code] = animationMetadata };

		// Activate once so ClientAnimator initializes weights/current animation state.
		animator.OnFrame(activeAnimations, 0f);
		RunningAnimation animationState = animator.GetAnimationState(animation.Code);
		if (animationState == null) return null;

		int matrixFloatCount = animator.MaxJointId * 16;
		if (matrixFloatCount <= 0) return null;

		var animationSamples = new float[sampleCount][];
		for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
		{
			// The authored last frame is intentionally the pose immediately before the completed loop (e.g. 354 degrees for a 60-frame revolution).
			// Sample the complete [0, QuantityFrames) cycle so the final half-step is represented instead of stretching frames 0-59 across the whole mechanical revolution.
			// Continuous rotating elements mark their final keyframe with rotShortestDistance*, allowing VS to interpolate that seam correctly.
			double normalizedSampleTime = sampleCount <= 1 ? 0 : (double)sampleIndex / sampleCount;
			animationState.CurrentFrame = (float)(normalizedSampleTime * animation.QuantityFrames);
			animationState.EasingFactor = 1f;
			animationState.BlendedWeight = 1f;
			animationState.Active = true;
			animationState.Running = true;
			animationState.Iterations = 0;

			animator.OnFrame(activeAnimations, 0f);

			float[] sample = new float[matrixFloatCount];
			Array.Copy(animator.Matrices, sample, matrixFloatCount);
			animationSamples[sampleIndex] = sample;
		}

		return new BogieAnimationClip(animationSamples, matrixFloatCount * sizeof(float));
	}

	private AssetLocation ResolveShapeAssetLocation(string shapePath)
	{
		string path = string.IsNullOrEmpty(shapePath) ? "locomotives/bogies/default" : shapePath;
		AssetLocation assetLocation = AssetLocation.Create(path, entity.Code?.Domain ?? "yangtransport");

		string assetPath = assetLocation.Path;
		if (!assetPath.StartsWith("shapes/", StringComparison.OrdinalIgnoreCase)) assetPath = "shapes/" + assetPath;
		if (!assetPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) assetPath += ".json";

		return new AssetLocation(assetLocation.Domain, assetPath);
	}

	public override void Dispose()
	{
		base.Dispose();
		ReleaseBogieMeshes();
	}

	private void ReleaseBogieMeshes()
	{
		for (int bogieIndex = 0; bogieIndex < BogieParts.Length; bogieIndex++)
		{
			RenderBogiePart bogiePart = BogieParts[bogieIndex];
			if (bogiePart.MeshReference == null || bogiePart.CacheKey == null) continue;

			if (BogieMeshCache.TryGetValue(bogiePart.CacheKey, out BogieMeshCacheEntry cachedEntry))
			{
				cachedEntry.Users--;
				if (cachedEntry.Users <= 0)
				{
					cachedEntry.MeshReference.Dispose();
					BogieMeshCache.Remove(bogiePart.CacheKey);
				}
			}

			bogiePart.MeshReference = null;
			bogiePart.CacheKey = null;
			bogiePart.AnimationClip = null;
		}
	}

	public override void BeforeRender(float deltaTime)
	{
		base.BeforeRender(deltaTime);

		if (!isSpectator) ComputePartPoses(deltaTime);
	}

	public override void DoRender3DOpaque(float deltaTime, bool isShadowPass)
	{
		if (!isSpectator)
		{
			if (!AllBogieMeshesReady()) EnsureBogieMeshes();
			BuildPartMatrices(deltaTime);
		}
	}

	private bool AllBogieMeshesReady()
	{
		for (int bogieIndex = 0; bogieIndex < BogieParts.Length; bogieIndex++) { if (BogieParts[bogieIndex].MeshReference == null) return false; }

		return true;
	}

	private void ComputePartPoses(float deltaTime)
	{
		if (BogieParts.Length == 0)
		{
			HasBodyPose = false;
			return;
		}

		double sourceX = entity.Pos.X;
		double sourceY = entity.Pos.InternalY;
		double sourceZ = entity.Pos.Z;

		float yaw = entity.Pos.Yaw;
		float roll = entity.Pos.Roll;

		if (!TryComputeRoutePoses(sourceX, sourceY, sourceZ, yaw, roll, deltaTime)) { ComputeRigidFallbackPoses(sourceX, sourceY, sourceZ, yaw, roll, deltaTime); }

		ComputeBodyPoseFromBogies(yaw, roll);
	}

	private bool TryComputeRoutePoses(double sourceX, double sourceY, double sourceZ, float fallbackYaw, float fallbackRoll, float deltaTime)
	{
		if (StandardGaugeEntity == null) return false;
		if (entity.WatchedAttributes.GetBool("derailed", false)) return false;

		if (RouteNetwork == null)
		{
			RouteNetwork = capi.ModLoader.GetModSystem<ConvoyRouteNetworkSystem>();
			if (RouteNetwork == null) return false;
		}

		long convoyID = StandardGaugeEntity.ClientRenderConvoyHeadID;
		int expectedEpoch = StandardGaugeEntity.ClientRenderRouteEpoch;
		if (ProjectionConvoyID != convoyID || ProjectionExpectedEpoch != expectedEpoch)
		{
			SourceProjectionHint.Reset();
			RequiredAnchorGeneration = 0;
			ProjectionConvoyID = convoyID;
			ProjectionExpectedEpoch = expectedEpoch;
			RouteTravelNeedsReset = true;
		}

		bool requireFreshAnchor = !SourceProjectionHint.HasPathS;
		if
		(
			!RouteNetwork.TryGetClientRoute
			(
				convoyID,
				expectedEpoch,
				requireFreshAnchor,
				ref RequiredAnchorGeneration,
				out ConvoyRoute route,
				out double headDistanceAnchor
			)
		) { return false; }

		double expectedSourceDistance = headDistanceAnchor - StandardGaugeEntity.ClientRenderDistanceBehindHead;
		if (!route.TryProjectClientWorldPoint(sourceX, sourceY, sourceZ, expectedSourceDistance, ref SourceProjectionHint, out ConvoyRouteProjection projection))
		{
			InvalidateRouteProjection(convoyID, expectedEpoch);
			return false;
		}

		// Route topology is reliable but entity positions are interpolated independently. If position has advanced beyond available route coverage,
		// use rigid poses until the route/anchor request catches up rather than pinning the vehicle to an endpoint.
		if (((projection.ClampedLow || projection.ClampedHigh) && projection.DistanceSquared > 0.01) || projection.DistanceSquared > 4.0)
		{
			InvalidateRouteProjection(convoyID, expectedEpoch);
			return false;
		}
		double sourceDistance = projection.PathS;

		if (RouteTravelNeedsReset)
		{
			MechanicalTravel.ResetScalar(sourceDistance);
			RouteTravelNeedsReset = false;
		}
		else { MechanicalTravel.SampleScalar(sourceDistance); }
		UpdateBogieAnimationSamples();

		bool anySampled = false;
		for (int bogieIndex = 0; bogieIndex < BogieParts.Length; bogieIndex++)
		{
			RenderBogiePart bogiePart = BogieParts[bogieIndex];
			double physicalOffsetBehindFront = bogiePart.BogieSpecification.OffsetForward - FrontBogieOffset;
			double sampleDistance = sourceDistance - physicalOffsetBehindFront;

			if (TryReadSmoothedRoutePose(route, sampleDistance, bogiePart, out SGTrackPose pose))
			{
				bogiePart.TargetPose = pose;
				bogiePart.RenderPose = bogiePart.PoseFilter.Update(bogiePart.TargetPose, deltaTime);
				anySampled = true;
				continue;
			}

			bogiePart.TargetPose = BuildRigidBogiePose(sourceX, sourceY, sourceZ, fallbackYaw, fallbackRoll, physicalOffsetBehindFront);
			bogiePart.RenderPose = bogiePart.PoseFilter.Update(bogiePart.TargetPose, deltaTime);
		}

		return anySampled;
	}

	private void InvalidateRouteProjection(long convoyID, int expectedEpoch)
	{
		SourceProjectionHint.Reset();
		RequiredAnchorGeneration = 0;
		ProjectionConvoyID = convoyID;
		ProjectionExpectedEpoch = expectedEpoch;
	}

	private static bool TryReadSmoothedRoutePose(ConvoyRoute route, double sampleDistance, RenderBogiePart bogiePart, out SGTrackPose pose)
	{
		pose = default;

		if (!route.TrySampleClientCursor(sampleDistance, ref bogiePart.SampleSpanHint, out var centerCursor)) { return false; }

		var poseCursor = centerCursor;
		if (!RailwayVehicleShared.TryReadWorldPoseFromTrack(ref poseCursor, out double poseX, out double poseY, out double poseZ, out float poseYaw, out float poseRoll))
		{
			return false;
		}

		// Keep the bogie center exactly on the branch-resolved convoy route, but derive orientation from a short centered chord.
		// This smooths low-resolution curve tangents without reintroducing movement-history dependence or server work.
		double backDistance = Math.Max(route.MinS, sampleDistance - BogieRotationSmoothRadius);
		double forwardDistance = Math.Min(route.MaxS, sampleDistance + BogieRotationSmoothRadius);

		if (forwardDistance - backDistance >= BogieRotationSmoothMinChord &&
			TryReadRoutePoint(route, backDistance, ref bogiePart.SmoothBackSpanHint, out double backX, out double backY, out double backZ) &&
			TryReadRoutePoint(route, forwardDistance, ref bogiePart.SmoothForwardSpanHint, out double forwardX, out double forwardY, out double forwardZ))
		{
			double directionX = forwardX - backX;
			double directionY = forwardY - backY;
			double directionZ = forwardZ - backZ;
			double chordLengthSQ = directionX * directionX + directionY * directionY + directionZ * directionZ;

			if (chordLengthSQ >= BogieRotationSmoothMinChord * BogieRotationSmoothMinChord)
			{
				SGTrackPoseUtil.YawRollFromVector(directionX, directionY, directionZ, out poseYaw, out poseRoll);
			}
		}

		pose = new SGTrackPose(poseX, poseY, poseZ, poseYaw, poseRoll);
		return true;
	}

	private static bool TryReadRoutePoint(ConvoyRoute route, double sampleDistance, ref int routeSpanHint, out double x, out double y, out double z)
	{
		x = y = z = 0;

		if (!route.TrySampleClientCursor(sampleDistance, ref routeSpanHint, out var cursor)) { return false; }

		var pointCursor = cursor;
		if (!RailwayVehicleShared.TryReadWorldPoseFromTrack(ref pointCursor, out x, out y, out z, out _, out _)) { return false; }

		return true;
	}

	private void ComputeRigidFallbackPoses(double sourceX, double sourceY, double sourceZ, float yaw, float roll, float deltaTime)
	{
		// Derailed vehicles and temporary route gaps still animate from the same interpolated world motion the player sees.
		// Returning to route sampling resynchronizes sourceS instead of counting the same gap twice.
		MechanicalTravel.SampleWorld(sourceX, sourceY, sourceZ, yaw);
		RouteTravelNeedsReset = true;
		UpdateBogieAnimationSamples();

		for (int bogieIndex = 0; bogieIndex < BogieParts.Length; bogieIndex++)
		{
			RenderBogiePart bogiePart = BogieParts[bogieIndex];
			double physicalOffsetBehindFront = bogiePart.BogieSpecification.OffsetForward - FrontBogieOffset;
			bogiePart.TargetPose = BuildRigidBogiePose(sourceX, sourceY, sourceZ, yaw, roll, physicalOffsetBehindFront);
			bogiePart.RenderPose = bogiePart.PoseFilter.Update(bogiePart.TargetPose, deltaTime);
		}
	}

	private SGTrackPose BuildRigidBogiePose(double sourceX, double sourceY, double sourceZ, float yaw, float roll, double physicalOffsetBehindFront)
	{
		SGTrackPoseUtil.ForwardVectorFromYawRoll(yaw, roll, out double forwardX, out double forwardY, out double forwardZ);

		return new SGTrackPose
		(
			sourceX - forwardX * physicalOffsetBehindFront,
			sourceY - forwardY * physicalOffsetBehindFront,
			sourceZ - forwardZ * physicalOffsetBehindFront,
			yaw, roll
		);
	}

	private void UpdateBogieAnimationSamples()
	{
		for (int bogieIndex = 0; bogieIndex < BogieParts.Length; bogieIndex++)
		{
			RenderBogiePart bogiePart = BogieParts[bogieIndex];
			BogieAnimationClip? animationClip = bogiePart.AnimationClip;
			if (animationClip == null) continue;

			double phase = RailMechanicalAnimation.WrapPhase
			(
				MechanicalTravel.TotalSignedDistance,
				bogiePart.BogieSpecification.DistancePerCycle,
				bogiePart.BogieSpecification.AnimationDirection,
				bogiePart.BogieSpecification.AnimationPhaseOffset
			);

			bogiePart.AnimationSampleIndex = RailMechanicalAnimation.PhaseToSample(phase, animationClip.SampleCount);
		}
	}

	private void ComputeBodyPoseFromBogies(float fallbackYaw, float fallbackRoll)
	{
		RenderBogiePart frontBogie = BogieParts[FrontBogieIndex];
		RenderBogiePart rearBogie = BogieParts[RearBogieIndex];

		double bogieDirectionX = frontBogie.RenderPose.X - rearBogie.RenderPose.X;
		double bogieDirectionY = frontBogie.RenderPose.Y - rearBogie.RenderPose.Y;
		double bogieDirectionZ = frontBogie.RenderPose.Z - rearBogie.RenderPose.Z;

		if (Math.Abs(bogieDirectionX) + Math.Abs(bogieDirectionY) + Math.Abs(bogieDirectionZ) < 1e-9)
		{
			SGTrackPoseUtil.ForwardVectorFromYawRoll(fallbackYaw, fallbackRoll, out bogieDirectionX, out bogieDirectionY, out bogieDirectionZ);
		}

		SGTrackPoseUtil.YawRollFromVector(bogieDirectionX, bogieDirectionY, bogieDirectionZ, out float bodyYaw, out float bodyRoll);
		SGTrackPoseUtil.NormalizeSafe(bogieDirectionX, bogieDirectionY, bogieDirectionZ, out double forwardX, out double forwardY, out double forwardZ);

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

		double bodyX = frontBogie.RenderPose.X + forwardX * (FrontBogieOffset + BodyOffsetForward) + rightX * BodyOffsetLateral;
		double bodyY = frontBogie.RenderPose.Y + forwardY * (FrontBogieOffset + BodyOffsetForward) + BodyOffsetVertical + SGLocomotiveBodyTransform.RideHeight;
		double bodyZ = frontBogie.RenderPose.Z + forwardZ * (FrontBogieOffset + BodyOffsetForward) + rightZ * BodyOffsetLateral;

		LastBodyPose = new SGLocomotiveBodyPose(bodyX, bodyY, bodyZ, bodyYaw, bodyRoll);
		HasBodyPose = true;
	}

	private void BuildPartMatrices(float deltaTime)
	{
		if (!HasBodyPose) ComputePartPoses(deltaTime);
		if (!HasBodyPose) return;

		GetRenderOrigin(out double originX, out double originY, out double originZ);

		for (int bogieIndex = 0; bogieIndex < BogieParts.Length; bogieIndex++)
		{
			RenderBogiePart bogiePart = BogieParts[bogieIndex];
			BuildEntityLikeModelMatrix
			(
				bogiePart.Matrix, bogiePart.RenderPose.X, bogiePart.RenderPose.Y + SGLocomotiveBodyTransform.RideHeight,
				bogiePart.RenderPose.Z, bogiePart.RenderPose.Yaw, bogiePart.RenderPose.Roll, bogiePart.BogieSpecification, originX, originY, originZ
			);
		}

		BuildEntityLikeModelMatrix(BodyMatrix, LastBodyPose.X, LastBodyPose.Y, LastBodyPose.Z, LastBodyPose.Yaw, LastBodyPose.Roll, null, originX, originY, originZ);
	}

	public bool TryGetBodyPose(out SGLocomotiveBodyPose pose) { pose = LastBodyPose; return HasBodyPose; }

	public bool TryGetBogieCenterWorldPositions(out double frontX, out double frontY, out double frontZ, out double rearX, out double rearY, out double rearZ)
	{
		if (!HasBodyPose || BogieParts.Length == 0) { frontX = frontY = frontZ = rearX = rearY = rearZ = 0; return false; }

		RenderBogiePart frontBogie = BogieParts[FrontBogieIndex];
		RenderBogiePart rearBogie = BogieParts[RearBogieIndex];
		double tetherYOffset = SGLocomotiveBodyTransform.RideHeight + BodyOffsetVertical - 0.25;

		frontX = frontBogie.RenderPose.X;
		frontY = frontBogie.RenderPose.Y + tetherYOffset;
		frontZ = frontBogie.RenderPose.Z;

		rearX = rearBogie.RenderPose.X;
		rearY = rearBogie.RenderPose.Y + tetherYOffset;
		rearZ = rearBogie.RenderPose.Z;

		return true;
	}

	private void GetRenderOrigin(out double originX, out double originY, out double originZ)
	{
		SGLocomotiveBodyTransform.GetRenderOrigin(capi, entity, out originX, out originY, out originZ);
	}

	private void BuildEntityLikeModelMatrix
	(
		float[] outputMatrix, double x, double y, double z, float yaw, float roll,
		SgLocomotiveBogieRenderSpec? bogieSpecification, double originX, double originY, double originZ
	)
	{
		Mat4f.Identity(outputMatrix);

		Mat4f.Translate(outputMatrix, outputMatrix, (float)(x - originX), (float)(y - originY), (float)(z - originZ));
		Mat4f.Translate(outputMatrix, outputMatrix, 0f, entity.SelectionBox.Y2 / 2f, 0f);

		float rotationX = entity.Properties.Client.Shape?.rotateX ?? 0f;
		float rotationY = entity.Properties.Client.Shape?.rotateY ?? 0f;
		float rotationZ = entity.Properties.Client.Shape?.rotateZ ?? 0f;

		float yawRadians = yaw + (rotationY + 90f) * GameMath.DEG2RAD;
		float pitchRadians = rotationX * GameMath.DEG2RAD;
		float rollRadians = roll + rotationZ * GameMath.DEG2RAD;

		Mat4f.RotateX(outputMatrix, outputMatrix, pitchRadians);
		Mat4f.RotateY(outputMatrix, outputMatrix, yawRadians);
		Mat4f.RotateZ(outputMatrix, outputMatrix, rollRadians);

		float scale = entity.Properties.Client.Size;
		Mat4f.Translate(outputMatrix, outputMatrix, 0f, (0f - entity.SelectionBox.Y2) / 2f, 0f);
		Mat4f.Scale(outputMatrix, outputMatrix, scale, scale, scale);

		if (bogieSpecification != null && (Math.Abs(bogieSpecification.ModelOffsetX) > 1e-6f || Math.Abs(bogieSpecification.ModelOffsetY) > 1e-6f || Math.Abs(bogieSpecification.ModelOffsetZ) > 1e-6f))
		{
			Mat4f.Translate(outputMatrix, outputMatrix, bogieSpecification.ModelOffsetX, bogieSpecification.ModelOffsetY, bogieSpecification.ModelOffsetZ);
		}

		Mat4f.Translate(outputMatrix, outputMatrix, -0.5f, 0f, -1f);
	}

	public override void DoRender3DOpaqueBatched(float deltaTime, bool isShadowPass)
	{
		if (isSpectator) return;
		if (meshRefOpaque == null) return;

		IShaderProgram shaderProgram = capi.Render.CurrentActiveShader;
		UBORef animationUniformBufferObject = shaderProgram.UBOs["Animation"];
		BogieAnimationClip? uploadedClip = null;
		int uploadedSampleIndex = -1;

		if (isShadowPass)
		{
			animationUniformBufferObject.Update(entity.AnimManager.Animator.Matrices, 0, entity.AnimManager.Animator.MaxJointId * 16 * 4);

			Mat4f.Mul(TemporaryLocalModelViewMatrix, capi.Render.CurrentModelviewMatrix, BodyMatrix);
			shaderProgram.UniformMatrix("modelViewMatrix", TemporaryLocalModelViewMatrix);
			capi.Render.RenderMultiTextureMesh(meshRefOpaque, "entityTex", 0);

			for (int bogieIndex = 0; bogieIndex < BogieParts.Length; bogieIndex++)
			{
				RenderBogiePart bogiePart = BogieParts[bogieIndex];
				if (bogiePart.MeshReference == null) continue;

				Mat4f.Mul(TemporaryLocalModelViewMatrix, capi.Render.CurrentModelviewMatrix, bogiePart.Matrix);
				shaderProgram.UniformMatrix("modelViewMatrix", TemporaryLocalModelViewMatrix);
				UploadBogieAnimationPalette(animationUniformBufferObject, bogiePart, ref uploadedClip, ref uploadedSampleIndex);
				capi.Render.RenderMultiTextureMesh(bogiePart.MeshReference, "entityTex", 0);
			}

			return;
		}

		frostAlpha += (targetFrostAlpha - frostAlpha) * deltaTime / 32f; // Much higher value for a much slower lerping speed, making frost more gradual.
		float roundedFrostAlpha = (float)Math.Round(GameMath.Clamp(frostAlpha, 0f, 1f), 4);

		shaderProgram.Uniform("rgbaLightIn", lightrgbs);
		shaderProgram.Uniform("extraGlow", entity.Properties.Client.GlowLevel);
		shaderProgram.UniformMatrix("viewMatrix", capi.Render.CurrentModelviewMatrix);
		shaderProgram.Uniform("addRenderFlags", AddRenderFlags);
		shaderProgram.Uniform("windWaveIntensity", (float)WindWaveIntensity);
		shaderProgram.Uniform("entityId", (int)entity.EntityId);
		shaderProgram.Uniform("glitchFlicker", glitchFlicker ? 1 : 0);
		shaderProgram.Uniform("frostAlpha", roundedFrostAlpha);
		shaderProgram.Uniform("waterWaveCounter", capi.Render.ShaderUniforms.WaterWaveCounter);

		color.R = (float)((entity.RenderColor >> 16) & 0xFF) / 255f;
		color.G = (float)((entity.RenderColor >> 8) & 0xFF) / 255f;
		color.B = (float)(entity.RenderColor & 0xFF) / 255f;
		color.A = (float)((entity.RenderColor >> 24) & 0xFF) / 255f;
		shaderProgram.Uniform("renderColor", color);

		double entityTemporalStability = entity.WatchedAttributes.GetDouble("temporalStability", 1.0);
		double playerTemporalStability = capi.World.Player.Entity.WatchedAttributes.GetDouble("temporalStability", 1.0);
		double minTemporalStability = Math.Min(entityTemporalStability, playerTemporalStability);
		float strength = (float)(glitchAffected ? Math.Max(0.0, 1.0 - 2.5 * minTemporalStability) : 0.0);
		shaderProgram.Uniform("glitchEffectStrength", strength);

		animationUniformBufferObject.Update(entity.AnimManager.Animator.Matrices, 0, entity.AnimManager.Animator.MaxJointId * 16 * 4);

		shaderProgram.UniformMatrix("modelMatrix", BodyMatrix);
		capi.Render.RenderMultiTextureMesh(meshRefOpaque, "entityTex", 0);

		for (int bogieIndex = 0; bogieIndex < BogieParts.Length; bogieIndex++)
		{
			RenderBogiePart bogiePart = BogieParts[bogieIndex];
			if (bogiePart.MeshReference == null) continue;

			shaderProgram.UniformMatrix("modelMatrix", bogiePart.Matrix);
			UploadBogieAnimationPalette(animationUniformBufferObject, bogiePart, ref uploadedClip, ref uploadedSampleIndex);
			capi.Render.RenderMultiTextureMesh(bogiePart.MeshReference, "entityTex", 0);
		}
	}

	private static void UploadBogieAnimationPalette(UBORef animationUniformBufferObject, RenderBogiePart bogiePart, ref BogieAnimationClip? uploadedClip, ref int uploadedSampleIndex)
	{
		BogieAnimationClip? animationClip = bogiePart.AnimationClip; if (animationClip == null) return;

		int sampleIndex = GameMath.Clamp(bogiePart.AnimationSampleIndex, 0, animationClip.SampleCount - 1);
		if (ReferenceEquals(animationClip, uploadedClip) && sampleIndex == uploadedSampleIndex) return;

		animationUniformBufferObject.Update(animationClip.Samples[sampleIndex], 0, animationClip.MatrixByteCount);
		uploadedClip = animationClip;
		uploadedSampleIndex = sampleIndex;
	}

	private sealed class RenderBogiePart
	{
		public readonly SgLocomotiveBogieRenderSpec BogieSpecification;
		public readonly float[] Matrix = Mat4f.Create();

		public MultiTextureMeshRef? MeshReference;
		public string? CacheKey;
		public BogieAnimationClip? AnimationClip;
		public int AnimationSampleIndex;

		public int SampleSpanHint = -1;
		public int SmoothBackSpanHint = -1;
		public int SmoothForwardSpanHint = -1;
		public SGTrackPose TargetPose;
		public SGTrackPose RenderPose;
		public SGVisualPoseFilter PoseFilter;

		public RenderBogiePart(SgLocomotiveBogieRenderSpec bogieSpecification) { BogieSpecification = bogieSpecification; }

		public void ResetRouteHints()
		{
			SampleSpanHint = -1;
			SmoothBackSpanHint = -1;
			SmoothForwardSpanHint = -1;
		}
	}

	private sealed class BogieMeshCacheEntry
	{
		public readonly MultiTextureMeshRef MeshReference;
		public readonly BogieAnimationClip? AnimationClip;
		public int Users;

		public BogieMeshCacheEntry(MultiTextureMeshRef meshReference, BogieAnimationClip? animationClip)
		{
			MeshReference = meshReference;
			AnimationClip = animationClip;
		}
	}

	private sealed class BogieAnimationClip
	{
		public readonly float[][] Samples;
		public readonly int MatrixByteCount;
		public int SampleCount => Samples.Length;

		public BogieAnimationClip(float[][] samples, int matrixByteCount)
		{
			Samples = samples;
			MatrixByteCount = matrixByteCount;
		}
	}
}

internal struct SGTrackPose
{
	public double X;
	public double Y;
	public double Z;
	public float Yaw;
	public float Roll;

	public SGTrackPose(double x, double y, double z, float yaw, float roll)
	{
		X = x; Y = y; Z = z;
		Yaw = yaw; Roll = roll;
	}
}

internal struct SGVisualPoseFilter
{
	private bool Initialized;
	private SGTrackPose FilteredPose;

	public bool HasPose => Initialized;
	public SGTrackPose Pose => FilteredPose;

	public void Reset(in SGTrackPose target)
	{
		FilteredPose = target;
		Initialized = true;
	}

	public SGTrackPose Update(in SGTrackPose target, float deltaTime, double snapDistance = 2.5, double positionTimeConstantSec = 0.055, double rotationTimeConstantSec = 0.055)
	{
		if (!Initialized) { Reset(target); return FilteredPose; }

		double positionDeltaX = target.X - FilteredPose.X;
		double positionDeltaY = target.Y - FilteredPose.Y;
		double positionDeltaZ = target.Z - FilteredPose.Z;
		double distanceSQ = positionDeltaX * positionDeltaX + positionDeltaY * positionDeltaY + positionDeltaZ * positionDeltaZ;

		if (distanceSQ > snapDistance * snapDistance || deltaTime <= 0) { Reset(target); return FilteredPose; }

		float positionAlpha = ExponentialAlpha(deltaTime, positionTimeConstantSec);
		float rotationAlpha = rotationTimeConstantSec == positionTimeConstantSec ? positionAlpha : ExponentialAlpha(deltaTime, rotationTimeConstantSec);

		FilteredPose.X += positionDeltaX * positionAlpha;
		FilteredPose.Y += positionDeltaY * positionAlpha;
		FilteredPose.Z += positionDeltaZ * positionAlpha;
		FilteredPose.Yaw += GameMath.AngleRadDistance(FilteredPose.Yaw, target.Yaw) * rotationAlpha;
		FilteredPose.Roll += GameMath.AngleRadDistance(FilteredPose.Roll, target.Roll) * rotationAlpha;

		return FilteredPose;
	}

	private static float ExponentialAlpha(float deltaTime, double timeConstantSec)
	{
		if (timeConstantSec <= 1e-6) return 1f;
		return GameMath.Clamp((float)(1.0 - Math.Exp(-deltaTime / timeConstantSec)), 0f, 1f);
	}
}

internal static class SGTrackPoseUtil
{
	public static void ForwardVectorFromYawRoll(float yaw, float roll, out double forwardX, out double forwardY, out double forwardZ)
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

	public static void YawRollFromVector(double directionX, double directionY, double directionZ, out float yaw, out float roll)
	{
		yaw = (float)Math.Atan2(directionX, directionZ);

		double horizontalLength = Math.Sqrt(directionX * directionX + directionZ * directionZ);
		roll = horizontalLength < 1e-8 ? 0f : (float)(-Math.Atan2(directionY, horizontalLength));
	}

	public static void NormalizeSafe(double directionX, double directionY, double directionZ, out double normalizedX, out double normalizedY, out double normalizedZ)
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
}
