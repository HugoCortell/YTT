using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace YangTransport;

internal readonly struct RailVehiclePlacementSpecification
{
	public readonly byte Gauge;
	public readonly bool IsStandardGauge;
	public readonly double Length;
	public readonly double FrontExtent;
	public readonly double RearExtent;
	public readonly double BodyOffsetForward;
	public readonly double BodyOffsetLateral;
	public readonly double BodyOffsetVertical;
	public readonly double FrontBogieOffset;
	public readonly double RearBogieOffset;
	public readonly double[] BogieOffsets;

	public RailVehiclePlacementSpecification(byte gauge, bool isStandardGauge, double length, double frontExtent, double rearExtent,
		double bodyOffsetForward, double bodyOffsetLateral, double bodyOffsetVertical,
		double frontBogieOffset, double rearBogieOffset, double[] bogieOffsets)
	{
		Gauge = gauge;
		IsStandardGauge = isStandardGauge;
		Length = Math.Max(0.1, length);
		FrontExtent = Math.Max(0, frontExtent);
		RearExtent = Math.Max(0, rearExtent);
		BodyOffsetForward = bodyOffsetForward;
		BodyOffsetLateral = bodyOffsetLateral;
		BodyOffsetVertical = bodyOffsetVertical;
		FrontBogieOffset = frontBogieOffset;
		RearBogieOffset = rearBogieOffset;
		BogieOffsets = bogieOffsets ?? Array.Empty<double>();
	}
}

internal readonly struct RailVehiclePreviewPose
{
	public readonly double X;
	public readonly double Y;
	public readonly double Z;
	public readonly float Yaw;
	public readonly float Roll;

	public RailVehiclePreviewPose(double x, double y, double z, float yaw, float roll = 0f)
	{
		X = x;
		Y = y;
		Z = z;
		Yaw = yaw;
		Roll = roll;
	}
}

internal enum RailVehiclePlacementPreviewState
{
	Invalid,
	Valid,
	Uncertain
}

internal static class RailVehiclePlacementPreviewUtil
{
	private static readonly double[] MinecartNoBogies = Array.Empty<double>();
	private static readonly double[] DefaultSGBogies = { 0.0, 4.0 };

	internal static bool TryGetRailTarget(IWorldAccessor worldAccessor, BlockSelection blockSelection, byte requiredGauge, out BlockPos railPosition, out TrackPieceSpec trackSpecification)
	{
		railPosition = null!;
		trackSpecification = null!;

		if (worldAccessor == null || blockSelection?.Position == null) return false;

		railPosition = blockSelection.Position;
		Block block = worldAccessor.BlockAccessor.GetBlock(railPosition);
		if (TrackSpecsDictionary.TryGet(block, out trackSpecification) && trackSpecification.Gauge == requiredGauge) return true;

		BlockPos blockBelow = railPosition.DownCopy();
		block = worldAccessor.BlockAccessor.GetBlock(blockBelow);
		if (TrackSpecsDictionary.TryGet(block, out trackSpecification) && trackSpecification.Gauge == requiredGauge) { railPosition = blockBelow; return true; }

		return false;
	}

	internal static bool TryGetVehicleSpecification(IWorldAccessor worldAccessor, AssetLocation itemCode, byte requiredGauge, out RailVehiclePlacementSpecification placementSpecification)
	{
		placementSpecification = default;
		if (worldAccessor == null || itemCode == null) return false;

		if (requiredGauge == 0) { placementSpecification = new RailVehiclePlacementSpecification(0, false, 1.0, 0.5, 0.5, 0, 0, 0, 0, 0, MinecartNoBogies); return true; }

		EntityProperties? entityProperties = worldAccessor.GetEntityType(itemCode);
		if (entityProperties == null) return false;

		if (!TryParseStandardGaugeSpecification(entityProperties, out placementSpecification)) return false;
		return placementSpecification.Gauge == requiredGauge;
	}

	internal static bool ServerCanPlaceVehicle(ICoreAPI coreAPI, EntityAgent byEntity, AssetLocation itemCode, BlockPos railPosition, byte requiredGauge, out bool overlaps, out bool insufficientRail)
	{
		overlaps = false;
		insufficientRail = false;

		if (coreAPI?.Side != EnumAppSide.Server || byEntity == null || railPosition == null) return true;
		if (!TryGetVehicleSpecification(coreAPI.World, itemCode, requiredGauge, out RailVehiclePlacementSpecification placementSpecification)) return true;

		RailGraphServerSystem? railGraphSystem = coreAPI.ModLoader.GetModSystem<RailGraphServerSystem>();
		RailTrainCollisionSystem? collisionSystem = coreAPI.ModLoader.GetModSystem<RailTrainCollisionSystem>();
		if (railGraphSystem == null || collisionSystem == null) return true;

		float yaw = byEntity.ServerPos.Yaw;
		Vec3d bindingPosition = new(railPosition.X + 0.5, railPosition.Y + TrackSpecsDictionary.DefaultY, railPosition.Z + 0.5);

		if (!TryBuildGraphPlacementCursor(railGraphSystem.Graph, bindingPosition, railPosition.dimension, placementSpecification.Gauge, yaw, out var cursor)) return true;
		if (!HasEnoughRailForPlacement(railGraphSystem.Graph, placementSpecification, in cursor)) { insufficientRail = true; return false; }

		overlaps = collisionSystem.WouldPlacementOverlap(railGraphSystem.Graph, placementSpecification.Gauge, in cursor, placementSpecification.RearExtent, placementSpecification.FrontExtent);
		return !overlaps;
	}

	internal static bool HasEnoughRailForPlacement(RailGraphLive railGraph, RailVehiclePlacementSpecification placementSpecification, in RailwayVehicleShared.RailCursor cursor, RailCollisionTrail? scratch = null)
	{
		if (!placementSpecification.IsStandardGauge) return true;
		if (railGraph == null || cursor.SegmentHash == 0 || cursor.PolyXYZ16 == null || cursor.PointCount < 2) return false;

		RailCollisionTrail collisionTrail = scratch ?? new RailCollisionTrail();
		collisionTrail.Clear();
		return collisionTrail.AddCurrentBodyFromCursor(railGraph, in cursor, centerTravel: 0, rearDistance: placementSpecification.RearExtent, frontDistance: placementSpecification.FrontExtent);
	}

	internal static void ServerPlayPlacementSound(ICoreAPI coreAPI, CollectibleObject? placedItem, Entity placedEntity)
	{
		if (coreAPI?.Side != EnumAppSide.Server || placedEntity == null) return;

		AssetLocation? placementSound = TryGetPlacementSound(placedItem?.Attributes);
		if (placementSound == null) return;

		double x = placedEntity.ServerPos.X;
		double y = placedEntity.ServerPos.Y + (placedEntity.SelectionBox?.Y2 ?? 1f) * 0.5f;
		double z = placedEntity.ServerPos.Z;

		coreAPI.World.PlaySoundAt(placementSound, x, y, z, null, true, 32f, 1f);
	}

	private static AssetLocation? TryGetPlacementSound(JsonObject? attributes)
	{
		if (attributes == null || !attributes.Exists) return null;

		string? soundPath = attributes["PlacementSound"].AsString(null);
		if (string.IsNullOrWhiteSpace(soundPath)) return null;

		return soundPath;
	}

	internal static bool TryBuildClientPreviewPose(BlockPos railPosition, TrackPieceSpec trackSpecification, float playerYaw, out RailVehiclePreviewPose pose)
	{
		pose = default;
		if (railPosition == null || trackSpecification?.Paths == null || trackSpecification.Paths.Length == 0) return false;

		Vec3d targetPosition = new(railPosition.X + 0.5, railPosition.Y + TrackSpecsDictionary.DefaultY, railPosition.Z + 0.5);
		double yawForwardX = Math.Sin(playerYaw);
		double yawForwardZ = Math.Cos(playerYaw);

		bool found = false;
		double bestScore = double.MaxValue;
		double bestX = targetPosition.X;
		double bestY = targetPosition.Y;
		double bestZ = targetPosition.Z;
		double bestDx = 0;
		double bestDz = 1;

		for (int pathIndex = 0; pathIndex < trackSpecification.Paths.Length; pathIndex++)
		{
			TrackPath path = trackSpecification.Paths[pathIndex];
			if (path == null || path.LocalPoints == null || path.LocalPoints.Length < 2) continue;

			Vec3f[] pathPoints = path.LocalPoints;

			for (int pointIndex = 0; pointIndex < pathPoints.Length - 1; pointIndex++)
			{
				Vec3f a = pathPoints[pointIndex];
				Vec3f b = pathPoints[pointIndex + 1];

				double ax = railPosition.X + a.X;
				double ay = railPosition.Y + a.Y;
				double az = railPosition.Z + a.Z;
				double bx = railPosition.X + b.X;
				double by = railPosition.Y + b.Y;
				double bz = railPosition.Z + b.Z;

				double d2 = RailwayVehicleShared.SquaredDistancePointToSegment(targetPosition, ax, ay, az, bx, by, bz, out double t);

				double sx = bx - ax;
				double sz = bz - az;
				double horizontalLength = Math.Sqrt(sx * sx + sz * sz);
				if (horizontalLength <= 1e-8) continue;

				double dot = (sx * yawForwardX + sz * yawForwardZ) / horizontalLength;
				double absoluteDot = Math.Abs(dot);
				double score = d2 - absoluteDot * 0.05;

				if (score >= bestScore) continue;

				double sign = dot < 0 ? -1.0 : 1.0;
				bestScore = score;
				bestX = ax + (bx - ax) * t;
				bestY = ay + (by - ay) * t;
				bestZ = az + (bz - az) * t;
				bestDx = (sx / horizontalLength) * sign;
				bestDz = (sz / horizontalLength) * sign;
				found = true;
			}
		}

		if (!found) return false;

		pose = new RailVehiclePreviewPose(bestX, bestY, bestZ, (float)Math.Atan2(bestDx, bestDz));
		return true;
	}

	internal static bool TryBuildGraphPlacementCursor(RailGraphLive railGraph, Vec3d bindingPosition, int dimension, byte gauge, float yaw, out RailwayVehicleShared.RailCursor cursor)
	{
		cursor = new RailwayVehicleShared.RailCursor
		{
			Gauge = gauge,
			Direction = 1,
			NormalizedSegmentProgress = 0.5
		};

		List<ulong> candidates = new(gauge == 1 ? 256 : 128);
		int radiusBlocks = gauge == 1 ? 256 : 128;

		bool bindingSucceeded = gauge == 1
			? RailwayVehicleShared.TryBindNearestEdgeWithYaw(railGraph, bindingPosition, dimension, radiusBlocks, yaw, ref cursor, candidates)
			: RailwayVehicleShared.TryBindNearestEdge(railGraph, bindingPosition, dimension, radiusBlocks, ref cursor, candidates);

		if (!bindingSucceeded) return false;

		RailwayVehicleShared.ChooseForwardDirectionFromYaw(yaw, ref cursor);
		cursor.BoundGraphVersion = railGraph.BuildVersion;
		return cursor.SegmentHash != 0 && cursor.PolyXYZ16 != null && cursor.PointCount >= 2;
	}

	internal static bool TryBuildPreviewPoseFromCursor(ref RailwayVehicleShared.RailCursor cursor, out RailVehiclePreviewPose pose)
	{
		pose = default;
		if (!RailwayVehicleShared.TryReadWorldPoseFromTrack(ref cursor, out double x, out double y, out double z, out float yaw, out float roll)) return false;

		pose = new RailVehiclePreviewPose(x, y, z, yaw, roll);
		return true;
	}

	private static bool TryParseStandardGaugeSpecification(EntityProperties entityProperties, out RailVehiclePlacementSpecification placementSpecification)
	{
		placementSpecification = default;

		JsonObject configurationRoot = entityProperties.Attributes;
		JsonObject standardGaugeConfiguration = configurationRoot?["SGLocomotive"];
		if (standardGaugeConfiguration == null || !standardGaugeConfiguration.Exists) return false;

		double vehicleLength = Math.Max(0.1, standardGaugeConfiguration["VehicleLength"].AsDouble(6.0));

		JsonObject renderConfiguration = standardGaugeConfiguration["Render"];
		double bodyOffsetForward = 0.0;
		double bodyOffsetLateral = 0.0;
		double bodyOffsetVertical = 0.0;
		double[] bogieOffsets = DefaultSGBogies;

		if (renderConfiguration.Exists)
		{
			bodyOffsetForward = renderConfiguration["BodyOffsetForward"].AsDouble(0.0);
			bodyOffsetLateral = renderConfiguration["BodyOffsetLateral"].AsDouble(0.0);
			bodyOffsetVertical = renderConfiguration["BodyOffsetVertical"].AsDouble(0.0);
			bogieOffsets = ParseBogieOffsets(renderConfiguration);
		}

		double frontBogieOffset = 0.0;
		double rearBogieOffset = 0.0;
		if (bogieOffsets.Length > 0)
		{
			frontBogieOffset = rearBogieOffset = bogieOffsets[0];
			for (int bogieIndex = 1; bogieIndex < bogieOffsets.Length; bogieIndex++)
			{
				if (bogieOffsets[bogieIndex] < frontBogieOffset) frontBogieOffset = bogieOffsets[bogieIndex];
				if (bogieOffsets[bogieIndex] > rearBogieOffset) rearBogieOffset = bogieOffsets[bogieIndex];
			}
		}

		double frontExtent = GameMath.Clamp(frontBogieOffset + bodyOffsetForward + 1.0, 0.0, vehicleLength);
		double rearExtent = Math.Max(0.0, vehicleLength - frontExtent);

		placementSpecification = new RailVehiclePlacementSpecification
		(
			1, true, vehicleLength, frontExtent, rearExtent, bodyOffsetForward,
			bodyOffsetLateral, bodyOffsetVertical, frontBogieOffset, rearBogieOffset, bogieOffsets
		);
		return true;
	}

	private static double[] ParseBogieOffsets(JsonObject renderConfiguration)
	{
		JsonObject[] bogieObjects = renderConfiguration["Bogies"].AsArray();
		if (bogieObjects != null && bogieObjects.Length > 0)
		{
			double[] offsets = new double[bogieObjects.Length];
			int offsetCount = 0;

			for (int bogieIndex = 0; bogieIndex < bogieObjects.Length; bogieIndex++)
			{
				JsonObject bogieObject = bogieObjects[bogieIndex];
				if (bogieObject == null || !bogieObject.Exists) continue;

				double offset = bogieObject["OffsetForward"].AsDouble(double.NaN);
				if (double.IsNaN(offset)) offset = 0.0;
				offsets[offsetCount++] = offset;
			}

			if (offsetCount > 0)
			{
				if (offsetCount == offsets.Length) return offsets;

				double[] trimmed = new double[offsetCount];
				Array.Copy(offsets, trimmed, offsetCount);
				return trimmed;
			}
		}

		double bogieDistance = renderConfiguration["BogieDistance"].AsDouble(double.NaN);
		if (!double.IsNaN(bogieDistance)) return new[] { 0.0, bogieDistance };

		return DefaultSGBogies;
	}
}

internal sealed class RailVehiclePlacementPreviewRenderer : IDisposable
{
	private const int ProbeIntervalMS = 200;
	private const int GraphRequestIntervalMS = 1500;
	private const int GraphRequestCellShift = 4;
	private const int MaxVertices = 384;
	private const double MinecartOverlapHalfSize = 0.475;
	private const double SGBogiePreviewForwardCorrection = -0.5;

	private static readonly int ValidColor = MeshColor(255, 64, 255, 96);
	private static readonly int InvalidColor = MeshColor(255, 255, 64, 64);
	private static readonly int UncertainColor = MeshColor(255, 255, 224, 64);

	private readonly ICoreClientAPI ClientAPI;
	private readonly RailGraphClientSystem? ClientGraphSystem;
	private readonly MeshData PreviewMeshData;
	private MeshRef? PreviewMeshReference;

	private readonly Dictionary<string, RailVehiclePlacementSpecification> SpecificationCache = new();
	private readonly RailCollisionTrail PlacementCollisionTrail = new();
	private readonly RailCollisionTrail VehicleCollisionTrail = new();
	private readonly List<RailEndpointSensor> PlacementSensors = new(8);
	private readonly List<RailEndpointSensor> VehicleSensors = new(16);

	private long NextProbeMS;
	private RailVehiclePlacementPreviewState LastState = RailVehiclePlacementPreviewState.Uncertain;
	private RailVehiclePreviewPose LastPose;
	private RailVehiclePreviewPose LastSGBodyPose;
	private RailVehiclePreviewPose[] LastSGBogiePoses = Array.Empty<RailVehiclePreviewPose>();
	private int LastSGBogiePoseCount;
	private bool LastHasSGInverseKinematicsPose;
	private bool HasLastProbe;
	private bool HasLastPose;
	private int LastX, LastY, LastZ, LastDimension;
	private int LastYawBucket;
	private string? LastItemCode;

	private long NextGraphRequestMS;
	private bool HasLastGraphRequestCell;
	private int LastGraphRequestCellX, LastGraphRequestCellY, LastGraphRequestCellZ, LastGraphRequestDimension;
	private byte LastGraphRequestGauge;

	private static int MeshColor(int a, int r, int g, int b) { return ColorUtil.ToRgba(a, b, g, r); }

	public RailVehiclePlacementPreviewRenderer(ICoreClientAPI clientAPI)
	{
		this.ClientAPI = clientAPI;
		ClientGraphSystem = clientAPI.ModLoader.GetModSystem<RailGraphClientSystem>();

		PreviewMeshData = new MeshData(MaxVertices, MaxVertices, withNormals: false, withUv: false);
		PreviewMeshData.SetMode(EnumDrawMode.Lines);

		for (int vertexIndex = 0; vertexIndex < MaxVertices; vertexIndex++)
		{
			PreviewMeshData.AddVertexSkipTex(0, 0, 0, ColorUtil.WhiteArgb);
			PreviewMeshData.AddIndex(vertexIndex);
		}

		PreviewMeshReference = clientAPI.Render.UploadMesh(PreviewMeshData);
	}

	public void Dispose()
	{
		if (PreviewMeshReference != null)
		{
			ClientAPI.Render.DeleteMesh(PreviewMeshReference);
			PreviewMeshReference = null;
		}
	}

	public void Render(ItemSlot inputSlot, IClientPlayer byPlayer, byte requiredGauge)
	{
		if (PreviewMeshReference == null || inputSlot?.Itemstack?.Collectible?.Code == null || byPlayer?.Entity == null) return;

		AssetLocation itemCode = inputSlot.Itemstack.Collectible.Code;
		if (!TryGetCachedSpecification(itemCode, requiredGauge, out RailVehiclePlacementSpecification placementSpecification)) return;

		BlockSelection? rawSelection = byPlayer.CurrentBlockSelection;
		if (rawSelection == null) return;

		if (!RailVehiclePlacementPreviewUtil.TryGetRailTarget(ClientAPI.World, rawSelection, placementSpecification.Gauge, out BlockPos railPosition, out TrackPieceSpec trackSpecification)) return;

		float playerYaw = byPlayer.Entity.Pos.Yaw;
		double normalizedYaw = playerYaw % (Math.PI * 2.0);
		if (normalizedYaw < 0) normalizedYaw += Math.PI * 2.0;
		int yawBucket = (int)Math.Round(normalizedYaw * 64.0 / (Math.PI * 2.0));
		long nowMS = ClientAPI.World.ElapsedMilliseconds;
		string itemKey = itemCode.ToShortString();

		bool needsProbe = !HasLastProbe || !HasLastPose || nowMS >= NextProbeMS
			|| railPosition.X != LastX || railPosition.Y != LastY || railPosition.Z != LastZ
			|| railPosition.dimension != LastDimension || yawBucket != LastYawBucket || itemKey != LastItemCode;

		if (needsProbe)
		{
			bool hasGraphPose = TryBuildGraphPreviewPose(railPosition, trackSpecification, placementSpecification.Gauge, playerYaw, out RailVehiclePreviewPose pose, out RailGraphLive? railGraph, out var placementCursor);
			if (!hasGraphPose)
			{
				MaybeRequestDetailedGraph(railPosition, placementSpecification.Gauge);
				if (!RailVehiclePlacementPreviewUtil.TryBuildClientPreviewPose(railPosition, trackSpecification, playerYaw, out pose)) return;
			}

			LastPose = pose;
			HasLastPose = true;
			LastHasSGInverseKinematicsPose = placementSpecification.IsStandardGauge && hasGraphPose && railGraph != null && TryBuildSGInverseKinematicsPreview(railGraph, placementSpecification, in placementCursor, pose);
			LastState = EvaluatePreviewState(placementSpecification, pose, hasGraphPose, railGraph, in placementCursor);
			HasLastProbe = true;
			NextProbeMS = nowMS + ProbeIntervalMS;
			LastX = railPosition.X;
			LastY = railPosition.Y;
			LastZ = railPosition.Z;
			LastDimension = railPosition.dimension;
			LastYawBucket = yawBucket;
			LastItemCode = itemKey;
		}

		RailVehiclePreviewPose previewRenderPose = LastPose;
		Vec3d cameraPosition = byPlayer.Entity.CameraPos;
		int color = ColorForState(LastState);
		int vertexCount = 0;

		if (placementSpecification.IsStandardGauge) WriteStandardGaugePreview(ref vertexCount, placementSpecification, previewRenderPose, cameraPosition, color, LastHasSGInverseKinematicsPose);
		else WriteMinecartPreview(ref vertexCount, previewRenderPose, cameraPosition, color);

		if (vertexCount == 0) return;

		PreviewMeshData.VerticesCount = vertexCount;
		PreviewMeshData.IndicesCount = vertexCount;
		ClientAPI.Render.UpdateMesh(PreviewMeshReference, PreviewMeshData);

		IShaderProgram? previousShader = ClientAPI.Render.CurrentActiveShader;
		previousShader?.Stop();

		IShaderProgram shader = ClientAPI.Render.GetEngineShader(EnumShaderProgram.Autocamera);
		shader.Use();
		ClientAPI.Render.LineWidth = 2f;

		shader.UniformMatrix("projectionMatrix", ClientAPI.Render.CurrentProjectionMatrix);
		shader.UniformMatrix("modelViewMatrix", ClientAPI.Render.CameraMatrixOriginf);

		ClientAPI.Render.RenderMesh(PreviewMeshReference);
		ClientAPI.Render.LineWidth = 1f;

		shader.Stop();
		previousShader?.Use();
	}

	private bool TryBuildGraphPreviewPose
	(
		BlockPos railPosition, TrackPieceSpec trackSpecification, byte gauge, float playerYaw,
		out RailVehiclePreviewPose pose, out RailGraphLive? railGraph, out RailwayVehicleShared.RailCursor placementCursor
	)
	{
		pose = default;
		railGraph = null;
		placementCursor = default;

		if (ClientGraphSystem?.TryGetGraph(out RailGraphLive liveGraph, requirePolylines: true) != true) return false;

		Vec3d bindingPosition = new(railPosition.X + 0.5, railPosition.Y + TrackSpecsDictionary.DefaultY, railPosition.Z + 0.5);
		if (!RailVehiclePlacementPreviewUtil.TryBuildGraphPlacementCursor(liveGraph, bindingPosition, railPosition.dimension, gauge, playerYaw, out placementCursor)) return false;
		if (!liveGraph.ContainsSpecEdgeAt(railPosition, trackSpecification, placementCursor.SegmentHash)) return false;
		if (!RailVehiclePlacementPreviewUtil.TryBuildPreviewPoseFromCursor(ref placementCursor, out pose)) return false;

		railGraph = liveGraph;
		return true;
	}

	private bool TryBuildSGInverseKinematicsPreview
	(
		RailGraphLive railGraph, RailVehiclePlacementSpecification placementSpecification,
		in RailwayVehicleShared.RailCursor placementCursor, RailVehiclePreviewPose frontPose
	)
	{
		int bogieCount = placementSpecification.BogieOffsets?.Length ?? 0;
		if (bogieCount <= 0) return false;

		EnsureSGBogiePoseCapacity(bogieCount);

		int frontIndex = 0;
		int rearIndex = 0;
		double frontOffset = placementSpecification.BogieOffsets[0];
		double rearOffset = placementSpecification.BogieOffsets[0];

		for (int bogieIndex = 0; bogieIndex < bogieCount; bogieIndex++)
		{
			double offset = placementSpecification.BogieOffsets[bogieIndex];
			if (offset < frontOffset) { frontOffset = offset; frontIndex = bogieIndex; }
			if (offset > rearOffset) { rearOffset = offset; rearIndex = bogieIndex; }

			double behindFront = Math.Max(0.0, offset - placementSpecification.FrontBogieOffset);
			if (!TrySamplePoseBehind(railGraph, in placementCursor, behindFront, out RailVehiclePreviewPose bogiePose))
			{
				bogiePose = BuildRigidPoseBehind(frontPose, behindFront);
			}

			LastSGBogiePoses[bogieIndex] = bogiePose;
		}

		LastSGBogiePoseCount = bogieCount;

		RailVehiclePreviewPose front = LastSGBogiePoses[frontIndex];
		RailVehiclePreviewPose rear = LastSGBogiePoses[rearIndex];

		double centerDeltaX = front.X - rear.X;
		double centerDeltaY = front.Y - rear.Y;
		double centerDeltaZ = front.Z - rear.Z;

		if (Math.Abs(centerDeltaX) + Math.Abs(centerDeltaY) + Math.Abs(centerDeltaZ) < 1e-9)
		{
			SGTrackPoseUtil.ForwardVectorFromYawRoll(frontPose.Yaw, frontPose.Roll, out centerDeltaX, out centerDeltaY, out centerDeltaZ);
		}

		SGTrackPoseUtil.YawRollFromVector(centerDeltaX, centerDeltaY, centerDeltaZ, out float bodyYaw, out float bodyRoll);
		SGTrackPoseUtil.NormalizeSafe(centerDeltaX, centerDeltaY, centerDeltaZ, out double fx, out double fy, out double fz);

		double rightX = fz;
		double rightZ = -fx;
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

		double bodyAnchorForward = placementSpecification.FrontBogieOffset + placementSpecification.BodyOffsetForward;
		LastSGBodyPose = new RailVehiclePreviewPose
		(
			front.X + fx * bodyAnchorForward + rightX * placementSpecification.BodyOffsetLateral,
			front.Y + fy * bodyAnchorForward + placementSpecification.BodyOffsetVertical,
			front.Z + fz * bodyAnchorForward + rightZ * placementSpecification.BodyOffsetLateral,
			bodyYaw, bodyRoll
		);

		return true;
	}

	private void EnsureSGBogiePoseCapacity(int requiredCapacity)
	{
		if (LastSGBogiePoses.Length >= requiredCapacity) return;
		Array.Resize(ref LastSGBogiePoses, requiredCapacity);
	}

	private static bool TrySamplePoseBehind(RailGraphLive railGraph, in RailwayVehicleShared.RailCursor sourceCursor, double behindFront, out RailVehiclePreviewPose pose)
	{
		pose = default;
		var cursor = sourceCursor;

		if (behindFront > 1e-6)
		{
			double speed = 0;
			double travelled = 0;
			if (!RailwayVehicleShared.AdvanceAlongTrack(railGraph, -behindFront, 0, null, false, ref cursor, ref speed, ref travelled, false, out _, null)) return false;
		}

		return RailVehiclePlacementPreviewUtil.TryBuildPreviewPoseFromCursor(ref cursor, out pose);
	}

	private static RailVehiclePreviewPose BuildRigidPoseBehind(RailVehiclePreviewPose frontPose, double behindFront)
	{
		SGTrackPoseUtil.ForwardVectorFromYawRoll(frontPose.Yaw, frontPose.Roll, out double fx, out double fy, out double fz);
		return new RailVehiclePreviewPose
		(
			frontPose.X - fx * behindFront,
			frontPose.Y - fy * behindFront,
			frontPose.Z - fz * behindFront,
			frontPose.Yaw, frontPose.Roll
		);
	}

	private void MaybeRequestDetailedGraph(BlockPos railPosition, byte gauge)
	{
		if (ClientGraphSystem == null || railPosition == null) return;

		long nowMS = ClientAPI.World.ElapsedMilliseconds;
		int cellX = railPosition.X >> GraphRequestCellShift;
		int cellY = railPosition.Y >> GraphRequestCellShift;
		int cellZ = railPosition.Z >> GraphRequestCellShift;

		bool cellChanged = !HasLastGraphRequestCell
			|| cellX != LastGraphRequestCellX || cellY != LastGraphRequestCellY || cellZ != LastGraphRequestCellZ
			|| railPosition.dimension != LastGraphRequestDimension || gauge != LastGraphRequestGauge;

		if (!cellChanged && nowMS < NextGraphRequestMS) return;

		ClientGraphSystem.RequestDetailedAround(railPosition);

		HasLastGraphRequestCell = true;
		LastGraphRequestCellX = cellX;
		LastGraphRequestCellY = cellY;
		LastGraphRequestCellZ = cellZ;
		LastGraphRequestDimension = railPosition.dimension;
		LastGraphRequestGauge = gauge;
		NextGraphRequestMS = nowMS + GraphRequestIntervalMS;
	}

	private RailVehiclePlacementPreviewState EvaluatePreviewState
	(
		RailVehiclePlacementSpecification placementSpecification, RailVehiclePreviewPose pose,
		bool hasGraphPose, RailGraphLive? railGraph, in RailwayVehicleShared.RailCursor placementCursor
	)
	{
		if (ClientWouldOverlap(placementSpecification, pose)) return RailVehiclePlacementPreviewState.Invalid;
		if (!hasGraphPose || railGraph == null) return RailVehiclePlacementPreviewState.Uncertain;
		if (!RailVehiclePlacementPreviewUtil.HasEnoughRailForPlacement(railGraph, placementSpecification, in placementCursor, PlacementCollisionTrail)) return RailVehiclePlacementPreviewState.Uncertain;
		if (ClientHasNonBlockingEndpointContact(railGraph, placementSpecification, in placementCursor, pose)) return RailVehiclePlacementPreviewState.Uncertain;
		return RailVehiclePlacementPreviewState.Valid;
	}

	private bool ClientHasNonBlockingEndpointContact
	(
		RailGraphLive railGraph, RailVehiclePlacementSpecification placementSpecification,
		in RailwayVehicleShared.RailCursor placementCursor, RailVehiclePreviewPose pose
	)
	{
		if (placementSpecification.Gauge != 1) return false;

		PlacementCollisionTrail.Clear();
		if (!PlacementCollisionTrail.AddCurrentBodyFromCursor(railGraph, in placementCursor, 0, placementSpecification.RearExtent, placementSpecification.FrontExtent)) return true;

		PlacementSensors.Clear();
		if (PlacementCollisionTrail.CollectEndpointSensors(railGraph, placementSpecification.Gauge, PlacementSensors) == 0) return false;

		const double checkRange = 96.0;
		double squaredCheckRange = checkRange * checkRange;

		foreach (var entityEntry in ClientAPI.World.LoadedEntities)
		{
			if (entityEntry.Value is not EntityStandardGaugeLocomotive standardGaugeVehicle || !standardGaugeVehicle.Alive) continue;

			double dx = standardGaugeVehicle.Pos.X - pose.X;
			double dy = standardGaugeVehicle.Pos.Y - pose.Y;
			double dz = standardGaugeVehicle.Pos.Z - pose.Z;
			if (dx * dx + dy * dy + dz * dz > squaredCheckRange) continue;

			Vec3d bindingPosition = new(standardGaugeVehicle.Pos.X, standardGaugeVehicle.Pos.Y, standardGaugeVehicle.Pos.Z);
			int dimension = standardGaugeVehicle.Pos.AsBlockPos.dimension;
			if (!RailVehiclePlacementPreviewUtil.TryBuildGraphPlacementCursor(railGraph, bindingPosition, dimension, placementSpecification.Gauge, standardGaugeVehicle.Pos.Yaw, out var vehicleCursor)) continue;

			double front = Math.Max(0.0, standardGaugeVehicle.DebugBodyOffsetForward + 1.0);
			double rear = Math.Max(0.0, Math.Max(0.1, standardGaugeVehicle.DebugVehicleLength) - front);

			VehicleCollisionTrail.Clear();
			if (!VehicleCollisionTrail.AddCurrentBodyFromCursor(railGraph, in vehicleCursor, 0, rear, front)) continue;

			VehicleSensors.Clear();
			if (VehicleCollisionTrail.CollectEndpointSensors(railGraph, placementSpecification.Gauge, VehicleSensors) == 0) continue;

			if (EndpointSensorsTouch(PlacementSensors, VehicleSensors)) return true;
		}

		return false;
	}

	private static bool EndpointSensorsTouch(List<RailEndpointSensor> firstSensors, List<RailEndpointSensor> secondSensors)
	{
		double probeRange = RailTrainCollisionSystem.ProbeRadiusBlocks * 2.0;
		double squaredProbeRange = probeRange * probeRange;

		for (int firstSensorIndex = 0; firstSensorIndex < firstSensors.Count; firstSensorIndex++)
		{
			RailEndpointSensor firstSensor = firstSensors[firstSensorIndex];

			for (int secondSensorIndex = 0; secondSensorIndex < secondSensors.Count; secondSensorIndex++)
			{
				RailEndpointSensor secondSensor = secondSensors[secondSensorIndex];
				if (firstSensor.Dimension != secondSensor.Dimension || firstSensor.Gauge != secondSensor.Gauge) continue;
				if (firstSensor.Endpoint.Equals(secondSensor.Endpoint)) return true;

				double dx = firstSensor.X - secondSensor.X;
				double dy = firstSensor.Y - secondSensor.Y;
				double dz = firstSensor.Z - secondSensor.Z;
				if (dx * dx + dy * dy + dz * dz < squaredProbeRange) return true;
			}
		}

		return false;
	}

	private static int ColorForState(RailVehiclePlacementPreviewState state)
	{
		return state switch
		{
			RailVehiclePlacementPreviewState.Invalid => InvalidColor,
			RailVehiclePlacementPreviewState.Valid => ValidColor,
			_ => UncertainColor
		};
	}

	private bool TryGetCachedSpecification(AssetLocation itemCode, byte requiredGauge, out RailVehiclePlacementSpecification placementSpecification)
	{
		string key = itemCode.ToShortString();

		if (SpecificationCache.TryGetValue(key, out placementSpecification)) return placementSpecification.Gauge == requiredGauge;

		if (!RailVehiclePlacementPreviewUtil.TryGetVehicleSpecification(ClientAPI.World, itemCode, requiredGauge, out placementSpecification)) return false;

		SpecificationCache[key] = placementSpecification;
		return true;
	}

	private bool ClientWouldOverlap(RailVehiclePlacementSpecification placementSpecification, RailVehiclePreviewPose pose)
	{
		PreviewOrientedBoundingBox previewBounds = placementSpecification.IsStandardGauge
			? PreviewOrientedBoundingBox.FromRailPose(pose, -placementSpecification.RearExtent, placementSpecification.FrontExtent, 1.0, 1.0, 4.0)
			: PreviewOrientedBoundingBox.FromRailPose(pose, -MinecartOverlapHalfSize, MinecartOverlapHalfSize, MinecartOverlapHalfSize, 0.25, 0.75);

		const double checkRange = 96.0;
		double squaredCheckRange = checkRange * checkRange;

		foreach (var entityEntry in ClientAPI.World.LoadedEntities)
		{
			Entity entity = entityEntry.Value;
			if (entity == null || !entity.Alive) continue;

			double dx = entity.Pos.X - pose.X;
			double dy = entity.Pos.Y - pose.Y;
			double dz = entity.Pos.Z - pose.Z;
			if (dx * dx + dy * dy + dz * dz > squaredCheckRange) continue;

			if (placementSpecification.Gauge == 0 && entity is EntityMinecart minecart)
			{
				if (previewBounds.Overlaps(PreviewOrientedBoundingBox.FromRailPose(new RailVehiclePreviewPose(minecart.Pos.X, minecart.Pos.Y, minecart.Pos.Z, minecart.Pos.Yaw), -MinecartOverlapHalfSize, MinecartOverlapHalfSize, MinecartOverlapHalfSize, 0.25, 0.75))) return true;
			}
			else if (placementSpecification.Gauge == 1 && entity is EntityStandardGaugeLocomotive standardGaugeVehicle)
			{
				double vehicleLength = Math.Max(0.1, standardGaugeVehicle.DebugVehicleLength);
				double front = standardGaugeVehicle.DebugBodyOffsetForward + 1.0;
				double rear = front - vehicleLength;

				if (previewBounds.Overlaps(PreviewOrientedBoundingBox.FromRailPose(new RailVehiclePreviewPose(standardGaugeVehicle.Pos.X, standardGaugeVehicle.Pos.Y, standardGaugeVehicle.Pos.Z, standardGaugeVehicle.Pos.Yaw), rear, front, 1.0, 1.0, 4.0))) return true;
			}
		}

		return false;
	}

	private void WriteMinecartPreview(ref int vertexCount, RailVehiclePreviewPose pose, Vec3d cameraPosition, int color)
	{
		WriteOrientedBox(ref vertexCount, pose, cameraPosition, -0.5, 0.5, 0.5, 0.25, 0.75, color, diagonals: true);
	}

	private void WriteStandardGaugePreview(ref int vertexCount, RailVehiclePlacementSpecification placementSpecification, RailVehiclePreviewPose pose, Vec3d cameraPosition, int color, bool hasInverseKinematicsPose)
	{
		if (hasInverseKinematicsPose)
		{
			double bodyAnchorForward = placementSpecification.FrontBogieOffset + placementSpecification.BodyOffsetForward;
			// lastSgBodyPose already includes BodyOffsetVertical because it follows the real SG renderer's body anchor.
			// The wirebox's Y extents are ground-relative, so remove that anchor offset here or the body box floats above the bogies by the same amount.
			double bodyY0 = 1.0 - placementSpecification.BodyOffsetVertical;
			double bodyY1 = 4.0 - placementSpecification.BodyOffsetVertical;

			WriteOrientedBox3D
			(
				ref vertexCount, LastSGBodyPose, cameraPosition,
				-placementSpecification.RearExtent - bodyAnchorForward,
				placementSpecification.FrontExtent - bodyAnchorForward,
				1.0, bodyY0, bodyY1, color, diagonals: true
			);

			for (int bogieIndex = 0; bogieIndex < LastSGBogiePoseCount; bogieIndex++)
			{
				WriteOrientedBox3D
				(
					ref vertexCount, LastSGBogiePoses[bogieIndex], cameraPosition,
					SGBogiePreviewForwardCorrection - 1.0,
					SGBogiePreviewForwardCorrection + 1.0,
					1.0, 0.0, 1.0, color, diagonals: false
				);
			}

			return;
		}

		WriteOrientedBox(ref vertexCount, pose, cameraPosition, -placementSpecification.RearExtent, placementSpecification.FrontExtent, 1.0, 1.0, 4.0, color, diagonals: true);

		for (int bogieIndex = 0; bogieIndex < placementSpecification.BogieOffsets.Length; bogieIndex++)
		{
			double centerForward = -(placementSpecification.BogieOffsets[bogieIndex] - placementSpecification.FrontBogieOffset) + SGBogiePreviewForwardCorrection;
			WriteOrientedBox(ref vertexCount, pose, cameraPosition, centerForward - 1.0, centerForward + 1.0, 1.0, 0.0, 1.0, color, diagonals: false);
		}
	}

	private void WriteOrientedBox3D
	(
		ref int vertexCount, RailVehiclePreviewPose pose, Vec3d cameraPosition,
		double rear, double front, double halfWidth, double y0Ground, double y1Ground, int color, bool diagonals
	)
	{
		SGTrackPoseUtil.ForwardVectorFromYawRoll(pose.Yaw, pose.Roll, out double fx, out double fy, out double fz);

		double rx = fz;
		double ry = 0;
		double rz = -fx;
		double rightLength = Math.Sqrt(rx * rx + rz * rz);
		if (rightLength < 1e-9)
		{
			rx = Math.Cos(pose.Yaw);
			rz = -Math.Sin(pose.Yaw);
		}
		else
		{
			rx /= rightLength;
			rz /= rightLength;
		}

		double ux = fy * fx;
		double uy = fx * fx + fz * fz;
		double uz = -fy * fz;
		double upLength = Math.Sqrt(ux * ux + uy * uy + uz * uz);
		if (upLength < 1e-9)
		{
			ux = 0;
			uy = 1;
			uz = 0;
		}
		else
		{
			ux /= upLength;
			uy /= upLength;
			uz /= upLength;
		}

		double baseX = pose.X - cameraPosition.X;
		double baseY = pose.Y - TrackSpecsDictionary.DefaultY - cameraPosition.Y;
		double baseZ = pose.Z - cameraPosition.Z;

		AddBasisLine(ref vertexCount, baseX, baseY, baseZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, rear, -halfWidth, y0Ground, front, -halfWidth, y0Ground, color);
		AddBasisLine(ref vertexCount, baseX, baseY, baseZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, front, -halfWidth, y0Ground, front, halfWidth, y0Ground, color);
		AddBasisLine(ref vertexCount, baseX, baseY, baseZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, front, halfWidth, y0Ground, rear, halfWidth, y0Ground, color);
		AddBasisLine(ref vertexCount, baseX, baseY, baseZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, rear, halfWidth, y0Ground, rear, -halfWidth, y0Ground, color);

		AddBasisLine(ref vertexCount, baseX, baseY, baseZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, rear, -halfWidth, y1Ground, front, -halfWidth, y1Ground, color);
		AddBasisLine(ref vertexCount, baseX, baseY, baseZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, front, -halfWidth, y1Ground, front, halfWidth, y1Ground, color);
		AddBasisLine(ref vertexCount, baseX, baseY, baseZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, front, halfWidth, y1Ground, rear, halfWidth, y1Ground, color);
		AddBasisLine(ref vertexCount, baseX, baseY, baseZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, rear, halfWidth, y1Ground, rear, -halfWidth, y1Ground, color);

		AddBasisLine(ref vertexCount, baseX, baseY, baseZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, rear, -halfWidth, y0Ground, rear, -halfWidth, y1Ground, color);
		AddBasisLine(ref vertexCount, baseX, baseY, baseZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, front, -halfWidth, y0Ground, front, -halfWidth, y1Ground, color);
		AddBasisLine(ref vertexCount, baseX, baseY, baseZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, front, halfWidth, y0Ground, front, halfWidth, y1Ground, color);
		AddBasisLine(ref vertexCount, baseX, baseY, baseZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, rear, halfWidth, y0Ground, rear, halfWidth, y1Ground, color);

		if (!diagonals) return;

		AddBasisLine(ref vertexCount, baseX, baseY, baseZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, rear, -halfWidth, y1Ground, front, halfWidth, y1Ground, color);
		AddBasisLine(ref vertexCount, baseX, baseY, baseZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, rear, halfWidth, y1Ground, front, -halfWidth, y1Ground, color);
		AddBasisLine(ref vertexCount, baseX, baseY, baseZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, front, -halfWidth, y0Ground, front, halfWidth, y1Ground, color);
		AddBasisLine(ref vertexCount, baseX, baseY, baseZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, front, halfWidth, y0Ground, front, -halfWidth, y1Ground, color);
		AddBasisLine(ref vertexCount, baseX, baseY, baseZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, rear, -halfWidth, y0Ground, rear, halfWidth, y1Ground, color);
		AddBasisLine(ref vertexCount, baseX, baseY, baseZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, rear, halfWidth, y0Ground, rear, -halfWidth, y1Ground, color);
	}

	private void WriteOrientedBox
	(
		ref int vertexCount, RailVehiclePreviewPose pose, Vec3d cameraPosition,
		double rear, double front, double halfWidth, double y0Ground, double y1Ground, int color, bool diagonals
	)
	{
		double yaw = pose.Yaw;
		double fx = Math.Sin(yaw);
		double fz = Math.Cos(yaw);
		double rx = Math.Cos(yaw);
		double rz = -Math.Sin(yaw);

		double groundY = pose.Y - TrackSpecsDictionary.DefaultY;

		double baseX = pose.X - cameraPosition.X;
		double baseY = groundY - cameraPosition.Y;
		double baseZ = pose.Z - cameraPosition.Z;

		// Bottom rectangle
		AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, rear, -halfWidth, y0Ground, front, -halfWidth, y0Ground, color);
		AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, front, -halfWidth, y0Ground, front, halfWidth, y0Ground, color);
		AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, front, halfWidth, y0Ground, rear, halfWidth, y0Ground, color);
		AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, rear, halfWidth, y0Ground, rear, -halfWidth, y0Ground, color);

		// Top rectangle
		AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, rear, -halfWidth, y1Ground, front, -halfWidth, y1Ground, color);
		AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, front, -halfWidth, y1Ground, front, halfWidth, y1Ground, color);
		AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, front, halfWidth, y1Ground, rear, halfWidth, y1Ground, color);
		AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, rear, halfWidth, y1Ground, rear, -halfWidth, y1Ground, color);

		// Vertical edges
		AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, rear, -halfWidth, y0Ground, rear, -halfWidth, y1Ground, color);
		AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, front, -halfWidth, y0Ground, front, -halfWidth, y1Ground, color);
		AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, front, halfWidth, y0Ground, front, halfWidth, y1Ground, color);
		AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, rear, halfWidth, y0Ground, rear, halfWidth, y1Ground, color);

		if (!diagonals) return;

		double midForward = (rear + front) * 0.5;
		double midYGround = (y0Ground + y1Ground) * 0.5;

		// Top face always gets an X. Bottom never does.
		AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, rear, -halfWidth, y1Ground, front, halfWidth, y1Ground, color);
		AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, rear, halfWidth, y1Ground, front, -halfWidth, y1Ground, color);

		// Front and rear faces
		if (FaceFacesCamera(baseX + fx * front, baseY + midYGround, baseZ + fz * front, fx, 0, fz))
		{
			AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, front, -halfWidth, y0Ground, front, halfWidth, y1Ground, color);
			AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, front, halfWidth, y0Ground, front, -halfWidth, y1Ground, color);
		}

		if (FaceFacesCamera(baseX + fx * rear, baseY + midYGround, baseZ + fz * rear, -fx, 0, -fz))
		{
			AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, rear, -halfWidth, y0Ground, rear, halfWidth, y1Ground, color);
			AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, rear, halfWidth, y0Ground, rear, -halfWidth, y1Ground, color);
		}

		// Side faces
		if (FaceFacesCamera(baseX - rx * halfWidth + fx * midForward, baseY + midYGround, baseZ - rz * halfWidth + fz * midForward, -rx, 0, -rz))
		{
			AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, rear, -halfWidth, y0Ground, front, -halfWidth, y1Ground, color);
			AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, front, -halfWidth, y0Ground, rear, -halfWidth, y1Ground, color);
		}

		if (FaceFacesCamera(baseX + rx * halfWidth + fx * midForward, baseY + midYGround, baseZ + rz * halfWidth + fz * midForward, rx, 0, rz))
		{
			AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, rear, halfWidth, y0Ground, front, halfWidth, y1Ground, color);
			AddOrientedLine(ref vertexCount, baseX, baseY, baseZ, fx, fz, rx, rz, front, halfWidth, y0Ground, rear, halfWidth, y1Ground, color);
		}
	}

	private static bool FaceFacesCamera(double faceCenterRelX, double faceCenterRelY, double faceCenterRelZ, double normalX, double normalY, double normalZ)
	{
		return normalX * -faceCenterRelX + normalY * -faceCenterRelY + normalZ * -faceCenterRelZ > 0.02;
	}

	private void AddBasisLine
	(
		ref int vertexCount,
		double baseX, double baseY, double baseZ,
		double fx, double fy, double fz,
		double rx, double ry, double rz,
		double ux, double uy, double uz,
		double f0, double w0, double y0,
		double f1, double w1, double y1,
		int color
	)
	{
		AddLine
		(
			ref vertexCount,
			(float)(baseX + fx * f0 + rx * w0 + ux * y0),
			(float)(baseY + fy * f0 + ry * w0 + uy * y0),
			(float)(baseZ + fz * f0 + rz * w0 + uz * y0),
			(float)(baseX + fx * f1 + rx * w1 + ux * y1),
			(float)(baseY + fy * f1 + ry * w1 + uy * y1),
			(float)(baseZ + fz * f1 + rz * w1 + uz * y1),
			color
		);
	}

	private void AddOrientedLine
	(
		ref int vertexCount,
		double baseX, double baseY, double baseZ,
		double fx, double fz, double rx, double rz,
		double f0, double w0, double y0,
		double f1, double w1, double y1,
		int color
	)
	{
		AddLine
		(
			ref vertexCount,
			(float)(baseX + fx * f0 + rx * w0),
			(float)(baseY + y0),
			(float)(baseZ + fz * f0 + rz * w0),
			(float)(baseX + fx * f1 + rx * w1),
			(float)(baseY + y1),
			(float)(baseZ + fz * f1 + rz * w1),
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
		int positionOffset = index * 3;
		PreviewMeshData.xyz[positionOffset + 0] = x;
		PreviewMeshData.xyz[positionOffset + 1] = y;
		PreviewMeshData.xyz[positionOffset + 2] = z;

		int colorOffset = index * 4;
		PreviewMeshData.Rgba[colorOffset + 0] = (byte)(color & 0xFF);
		PreviewMeshData.Rgba[colorOffset + 1] = (byte)((color >> 8) & 0xFF);
		PreviewMeshData.Rgba[colorOffset + 2] = (byte)((color >> 16) & 0xFF);
		PreviewMeshData.Rgba[colorOffset + 3] = (byte)((color >> 24) & 0xFF);
	}

	private readonly struct PreviewOrientedBoundingBox
	{
		private readonly double CenterX, CenterZ;
		private readonly double ForwardX, ForwardZ;
		private readonly double RightX, RightZ;
		private readonly double HalfForward;
		private readonly double HalfWidth;
		private readonly double MinY, MaxY;

		private PreviewOrientedBoundingBox(double centerX, double centerZ, double forwardX, double forwardZ, double rightX, double rightZ, double halfForward, double halfWidth, double minY, double maxY)
		{
			this.CenterX = centerX;
			this.CenterZ = centerZ;
			this.ForwardX = forwardX;
			this.ForwardZ = forwardZ;
			this.RightX = rightX;
			this.RightZ = rightZ;
			this.HalfForward = halfForward;
			this.HalfWidth = halfWidth;
			this.MinY = minY;
			this.MaxY = maxY;
		}

		public static PreviewOrientedBoundingBox FromRailPose(RailVehiclePreviewPose pose, double rear, double front, double halfWidth, double y0Ground, double y1Ground)
		{
			double yaw = pose.Yaw;
			double fx = Math.Sin(yaw);
			double fz = Math.Cos(yaw);
			double rx = Math.Cos(yaw);
			double rz = -Math.Sin(yaw);

			double centerForward = (rear + front) * 0.5;
			double groundY = pose.Y - TrackSpecsDictionary.DefaultY;

			return new PreviewOrientedBoundingBox(
				pose.X + fx * centerForward,
				pose.Z + fz * centerForward,
				fx, fz, rx, rz,
				Math.Max(0.001, (front - rear) * 0.5),
				Math.Max(0.001, halfWidth),
				groundY + y0Ground,
				groundY + y1Ground
			);
		}

		public bool Overlaps(in PreviewOrientedBoundingBox other)
		{
			if (MaxY < other.MinY || other.MaxY < MinY) return false;

			return OverlapsOnAxis(other, ForwardX, ForwardZ) && OverlapsOnAxis(other, RightX, RightZ) && OverlapsOnAxis(other, other.ForwardX, other.ForwardZ) && OverlapsOnAxis(other, other.RightX, other.RightZ);
		}

		private bool OverlapsOnAxis(in PreviewOrientedBoundingBox other, double axisX, double axisZ)
		{
			double centerProjection = CenterX * axisX + CenterZ * axisZ;
			double otherCenterProjection = other.CenterX * axisX + other.CenterZ * axisZ;
			double projectionRadius = HalfForward * Math.Abs(ForwardX * axisX + ForwardZ * axisZ) + HalfWidth * Math.Abs(RightX * axisX + RightZ * axisZ);
			double otherProjectionRadius = other.HalfForward * Math.Abs(other.ForwardX * axisX + other.ForwardZ * axisZ) + other.HalfWidth * Math.Abs(other.RightX * axisX + other.RightZ * axisZ);

			return Math.Abs(centerProjection - otherCenterProjection) <= projectionRadius + otherProjectionRadius;
		}
	}
}
