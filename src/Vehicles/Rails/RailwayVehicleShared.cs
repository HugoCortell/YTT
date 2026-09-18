using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Client;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace YangTransport;

/// Shared, performance-minded rail vehicle helpers. Lots o' Math.
internal static class RailwayVehicleShared
{
	#region Diegetic Controls
	public const string AttributeDriveLeverPhase = "driveLeverPhase";
	public const int DefaultDriveLeverPhase = 1;
	public const string AttributeTurnLeverPhase = "turnLeverPhase";
	public const int DefaultTurnLeverPhase = 1;

	// Mirrored to clients for HUD tooltip display.
	public const string AttributeTrackPenalty = "trackPenalty";
	public const string AttributeLocustActionText = "yangtransport.locustActionText";
	public const string AttributeLocustActionCode = "yangtransport.locustActionCode";

	// Lever element names
	public const string DriveLeverBackElementName = "DRL_BCK";
	public const string DriveLeverStopElementName = "DRL_STP";
	public const string DriveLeverForwardElementName  = "DRL_FRW";

	public const string TurnLeverLeftElementName     = "TNL_LFT";
	public const string TurnLeverStraightElementName = "TNL_STR";
	public const string TurnLeverRightElementName    = "TNL_RGT";

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static int PhaseToDriveDirection(int phase) => phase == 0 ? -1 : (phase == 2 ? 1 : 0);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static int DriveDirectionToPhase(int direction) => direction < 0 ? 0 : (direction > 0 ? 2 : 1);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsDriveLeverBrakeEngaged(ITreeAttribute watchedAttributes)
	{
		return watchedAttributes != null
			&& watchedAttributes.HasAttribute(AttributeDriveLeverPhase)
			&& PhaseToDriveDirection(watchedAttributes.GetInt(AttributeDriveLeverPhase, DefaultDriveLeverPhase)) == 0;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static int PhaseToTurnChoice(int phase) => phase == 0 ? -1 : (phase == 2 ? 1 : 0);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static int TurnChoiceToPhase(int direction) => direction < 0 ? 0 : (direction > 0 ? 2 : 1);

	/// Applies lever mesh variants to an entity shape, based on watched attributes.
	/// This is called during tessellation and runs rarely (only when the entity shape is re-tessellated), so cloning is fine
	public static void ApplyLeverTessellation(ref Shape entityShape, ITreeAttribute watchedAttributes)
	{
		bool cloned = false;
		Shape workShape = entityShape;

		if (watchedAttributes.HasAttribute(AttributeDriveLeverPhase))
		{
			if (!cloned) { workShape = workShape.Clone(); cloned = true; }
			int phase = watchedAttributes.GetInt(AttributeDriveLeverPhase, DefaultDriveLeverPhase);
			int state = (phase == 0) ? 0 : (phase == 2 ? 2 : 1); // visible state

			switch (state)
			{
				case 0: // Backwards
					workShape.RemoveElementByName(DriveLeverStopElementName, StringComparison.InvariantCultureIgnoreCase);
					workShape.RemoveElementByName(DriveLeverForwardElementName,  StringComparison.InvariantCultureIgnoreCase);
				break;

				case 2: // Forwards
					workShape.RemoveElementByName(DriveLeverBackElementName, StringComparison.InvariantCultureIgnoreCase);
					workShape.RemoveElementByName(DriveLeverStopElementName, StringComparison.InvariantCultureIgnoreCase);
				break;

				default: // Stop (phase 1 or 3)
					workShape.RemoveElementByName(DriveLeverBackElementName, StringComparison.InvariantCultureIgnoreCase);
					workShape.RemoveElementByName(DriveLeverForwardElementName,  StringComparison.InvariantCultureIgnoreCase);
				break;
			}
		}

		if (watchedAttributes.HasAttribute(AttributeTurnLeverPhase))
		{
			if (!cloned) { workShape = workShape.Clone(); cloned = true; }
			int phase = watchedAttributes.GetInt(AttributeTurnLeverPhase, DefaultTurnLeverPhase);
			int state = (phase == 0) ? 0 : (phase == 2 ? 2 : 1); // 0 == left, 1 == straight, 2 == right

			switch (state)
			{
				case 0: // Left
					workShape.RemoveElementByName(TurnLeverStraightElementName, StringComparison.InvariantCultureIgnoreCase);
					workShape.RemoveElementByName(TurnLeverRightElementName,    StringComparison.InvariantCultureIgnoreCase);
				break;

				case 2: // Right
					workShape.RemoveElementByName(TurnLeverLeftElementName,     StringComparison.InvariantCultureIgnoreCase);
					workShape.RemoveElementByName(TurnLeverStraightElementName, StringComparison.InvariantCultureIgnoreCase);
				break;

				default: // Straight (phase 1 or 3)
					workShape.RemoveElementByName(TurnLeverLeftElementName,  StringComparison.InvariantCultureIgnoreCase);
					workShape.RemoveElementByName(TurnLeverRightElementName, StringComparison.InvariantCultureIgnoreCase);
				break;
			}
		}

		if (cloned) entityShape = workShape;
	}

	/// Turn preference in travel space (left/right relative to motion). travelSign is positive or negative.
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static int GetWantTurnTravel(ITreeAttribute watchedAttributes, int travelSign)
	{
		int phase = watchedAttributes.GetInt(AttributeTurnLeverPhase, DefaultTurnLeverPhase);
		return PhaseToTurnChoice(phase) * (travelSign >= 0 ? 1 : -1);
	}
	#endregion

	#region Interaction Helpers
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsSneakEmptyHandPickupClick(EntityAgent byEntity, ItemSlot itemslot, EnumInteractMode mode)
	{
		return mode == EnumInteractMode.Interact && byEntity?.Controls?.Sneak == true && (itemslot == null || itemslot.Empty);
	}

	public static bool TryGetServerPlayer(ICoreAPI coreAPI, EntityAgent byEntity, out EntityPlayer entityPlayer, out IServerPlayer serverPlayer)
	{
		entityPlayer = byEntity as EntityPlayer;
		serverPlayer = null;

		if (coreAPI?.Side != EnumAppSide.Server || entityPlayer == null) return false;

		serverPlayer = coreAPI.World.PlayerByUid(entityPlayer.PlayerUID) as IServerPlayer;
		return serverPlayer != null;
	}

	public static bool HasMountedPassenger(EntityBehaviorSeatable seatable)
	{
		IMountableSeat[] seats = seatable?.Seats;
		if (seats == null) return false;

		for (int seatIndex = 0; seatIndex < seats.Length; seatIndex++) { if (seats[seatIndex]?.Passenger != null) return true; }
		return false;
	}

	public static bool IsSeatSelectionClick(Entity entity, EntityBehaviorSeatable seatable, EntityAgent byEntity)
	{
		if (entity == null || seatable == null || byEntity is not EntityPlayer entityPlayer) return false;

		int selectionBoxIndex = entityPlayer.EntitySelection?.SelectionBoxIndex ?? -1;
		if (selectionBoxIndex <= 0) return false;

		EntityBehaviorSelectionBoxes selectionBoxesBehavior = entity.GetBehavior<EntityBehaviorSelectionBoxes>();
		AttachmentPointAndPose[] boxes = selectionBoxesBehavior?.selectionBoxes;
		if (boxes == null || selectionBoxIndex > boxes.Length) return false;

		string attachmentPointName = boxes[selectionBoxIndex - 1]?.AttachPoint?.Code;
		if (string.IsNullOrEmpty(attachmentPointName)) return false;

		IMountableSeat[] seats = seatable.Seats;
		if (seats == null) return false;

		for (int seatIndex = 0; seatIndex < seats.Length; seatIndex++)
		{
			SeatConfig seatConfiguration = seats[seatIndex]?.Config;
			if (seatConfiguration == null) continue;
			if (seatConfiguration.APName == attachmentPointName || seatConfiguration.SelectionBox == attachmentPointName) return true;
		}

		return false;
	}
	#endregion

	#region HUD Helpers
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static double GetMotionSpeedBPS(Entity entity)
	{
		return entity == null ? 0 : entity.Pos.Motion.Length() * 60.0;
	}

	public static void AppendLocomotiveHUDInfo
	(
		StringBuilder descriptionBuilder, Entity entity, EntityBehaviorSteamPowered? steamEngineBehaviour,
		double speedBPS, int weight, bool showEngineInformation, int convoyIndex, float trackPenalty
	)
	{
		if (showEngineInformation && steamEngineBehaviour != null)
		{
			double power = 0;
			if (steamEngineBehaviour.TryGetDrive(out var engineDrive)) { power = engineDrive.RawPowerNPer100C * engineDrive.GetHeatUnitsPer100C(); }

			descriptionBuilder.AppendLine(Lang.Get("yangtransport:locomotive-hudinfo-speed", speedBPS));
			descriptionBuilder.AppendLine(Lang.Get("yangtransport:locomotive-hudinfo-weightratio", weight, power, power - weight));

			int phase = entity.WatchedAttributes.GetInt(AttributeDriveLeverPhase, DefaultDriveLeverPhase);
			string leverDirectionText = Lang.Get("yangtransport:sestate-" + (phase == 0 ? "backwards" : (phase == 2 ? "forwards" : "stop")));
			descriptionBuilder.AppendLine(Lang.Get("yangtransport:locomotive-hudinfo-leverstate", leverDirectionText));

			if (entity.WatchedAttributes.HasAttribute(AttributeTurnLeverPhase))
			{
				int turnPhase = entity.WatchedAttributes.GetInt(AttributeTurnLeverPhase, DefaultTurnLeverPhase);
				string turnDirectionText = Lang.Get("yangtransport:sestate-" + ((turnPhase == 0) ? "left" : (turnPhase == 2 ? "right" : "straight")));
				descriptionBuilder.AppendLine(Lang.Get("yangtransport:locomotive-hudinfo-direction", turnDirectionText));
			}

			if (trackPenalty < 0.99f) { descriptionBuilder.AppendLine(Lang.Get("yangtransport:locomotive-hudinfo-trackpenalty", trackPenalty)); }
		}
		else
		{
			descriptionBuilder.AppendLine(Lang.Get("yangtransport:locomotive-hudinfo-convoyindex", convoyIndex));
			descriptionBuilder.AppendLine(Lang.Get("yangtransport:locomotive-hudinfo-speed", speedBPS));
			descriptionBuilder.AppendLine(Lang.Get("yangtransport:locomotive-hudinfo-weightindividual", weight));
		}

		string locustAction = entity.WatchedAttributes.GetString(AttributeLocustActionText, "");
		if (!string.IsNullOrWhiteSpace(locustAction)) { descriptionBuilder.AppendLine("Locust: " + locustAction); }
	}

	public static void ServerSyncTrackPenaltyIfChanged(Entity entity, float newFactor)
	{
		// Tooltip runs client-side, so mirror the current track speed factor only when it changes.
		if (entity.Api?.Side != EnumAppSide.Server) return;

		float previousFactor = entity.WatchedAttributes.GetFloat(AttributeTrackPenalty, 1f);
		if (Math.Abs(previousFactor - newFactor) <= 0.0005f) return;

		entity.WatchedAttributes.SetFloat(AttributeTrackPenalty, newFactor);
		entity.WatchedAttributes.MarkPathDirty(AttributeTrackPenalty);
	}
	#endregion

	/// Minimal rail cursor for kinematic rail vehicles. Stored inside an entity and passed by ref for zero-alloc updates.
	internal struct RailCursor
	{
		public byte Gauge;

		// RailGraph binding
		public ulong SegmentHash;
		public int SegmentIndex;
		public double NormalizedSegmentProgress; // 0-1, within current span SegIndex --> SegIndex+1 (polyline order)
		public int Direction; // +1 = forward along polyline order (A-->B), -1 = opposite (B-->A)
		public int BoundGraphVersion;

		// Cached polyline (xyz16)
		public int[]? PolyXYZ16;
		public int PointCount;

		// Per-edge limits
		public float SegmentMaxSpeedFactor; // 01
		public ushort SegmentMaterialSpeedCapBPS;
		
		// Orientation cache
		public ulong OrientationCacheSegmentHash;
		public int OrientationCacheSegmentIndex;
		public int OrientationCacheDirection;
		public float OrientationCacheYaw;
		public float OrientationCacheRoll;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void ClearBinding()
		{
			SegmentHash = 0;
			SegmentIndex = 0;
			NormalizedSegmentProgress = 0.5;
			Direction = 1;
			BoundGraphVersion = 0;
			PolyXYZ16 = null;
			PointCount = 0;
			SegmentMaxSpeedFactor = 1f;
			SegmentMaterialSpeedCapBPS = 0;

			// Prevent rebinds from reusing stale span tangents
			OrientationCacheSegmentHash = 0;
			OrientationCacheSegmentIndex = -1;
			OrientationCacheDirection = 0;
			OrientationCacheYaw = 0f;
			OrientationCacheRoll = 0f;
		}
	}

	internal readonly struct DeadEndInfo
	{
		public readonly bool Hit;
		public readonly bool ClearanceBlocked;
		public readonly int MoveDirection;
		public readonly double Dx, Dy, Dz;
		public readonly double X0, Y0, Z0;
		public readonly double X1, Y1, Z1;

		public DeadEndInfo(bool hit, int moveDirection, double dx, double dy, double dz,
			double x0, double y0, double z0, double x1, double y1, double z1)
		{
			Hit = hit;
			ClearanceBlocked = false;
			MoveDirection = moveDirection;
			Dx = dx; Dy = dy; Dz = dz;
			X0 = x0; Y0 = y0; Z0 = z0;
			X1 = x1; Y1 = y1; Z1 = z1;
		}

		private DeadEndInfo(bool clearanceBlocked)
		{
			Hit = false;
			ClearanceBlocked = clearanceBlocked;
			MoveDirection = 0;
			Dx = Dy = Dz = 0;
			X0 = Y0 = Z0 = 0;
			X1 = Y1 = Z1 = 0;
		}

		public static DeadEndInfo ClearanceBlock() => new(true);

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void GetEndPoint(out double endpointX, out double endpointY, out double endpointZ)
		{
			if (MoveDirection > 0) { endpointX = X1; endpointY = Y1; endpointZ = Z1; }
			else { endpointX = X0; endpointY = Y0; endpointZ = Z0; }
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool TryGetHorizontalForward(out double forwardX, out double forwardZ)
		{
			double squaredLength = Dx * Dx + Dz * Dz;
			if (squaredLength < 1e-9)
			{
				forwardX = 0;
				forwardZ = 0;
				return false;
			}

			double inverseLength = 1.0 / Math.Sqrt(squaredLength);
			forwardX = Dx * inverseLength * MoveDirection;
			forwardZ = Dz * inverseLength * MoveDirection;
			return true;
		}
	}

	private enum RailStepResult : byte
	{
		Advanced,
		DeadEnd,
		SignalBlocked,
		ClearanceBlocked,
		RouteBlocked
	}

	internal readonly struct DerailedPhysicsParameters
	{
		public readonly double GravityMultiplier;
		public readonly double LinearDrag;
		public readonly double AirDrag;

		public readonly double YawDrag;
		public readonly double ImpactToYaw;

		public readonly double HorizontalRestitution;
		public readonly double StopEpsilon;

		public DerailedPhysicsParameters
		(
			double gravityMultiplier,
			double linearDrag,
			double airDrag,
			double yawDrag,
			double impactToYaw,
			double horizontalRestitution,
			double stopEpsilon
		)
		{
			GravityMultiplier = gravityMultiplier;
			LinearDrag = linearDrag;
			AirDrag = airDrag;
			YawDrag = yawDrag;
			ImpactToYaw = impactToYaw;
			HorizontalRestitution = horizontalRestitution;
			StopEpsilon = stopEpsilon;
		}

		public static readonly DerailedPhysicsParameters MinecartDefault = new
		(
			gravityMultiplier: 6.0,
			linearDrag: 0.28,
			airDrag: 0.06,
			yawDrag: 2.0,
			impactToYaw: 0.035,
			horizontalRestitution: 0.28,
			stopEpsilon: 0.03
		);
	}

	#region Rail Binding
	public static bool TryEnsureBound
	(
		RailGraphServerSystem railGraphSystem, BlockPos buildNearPosition, Vec3d bindingPosition, int dimension,
		bool forceRebind, int bindRadiusBlocks, ref RailCursor cursor, List<ulong> candidates, bool bindWithYaw = false, float bindYaw = 0f
	)
	{
		if (railGraphSystem == null) return false;
		int graphVersion = railGraphSystem.GraphBuildVersion;

		// Refresh polyline when graph rebuilds
		if (!forceRebind && cursor.SegmentHash != 0 && graphVersion != cursor.BoundGraphVersion)
		{
			if (!TryRefreshPolyline(railGraphSystem.Graph, ref cursor))
			{
				cursor.SegmentHash = 0;
				cursor.PolyXYZ16 = null;
				cursor.PointCount = 0;
			}
		}

		if (forceRebind || cursor.SegmentHash == 0 || cursor.PolyXYZ16 == null || cursor.PointCount < 2)
		{
			bool bindingSucceeded = bindWithYaw
				? TryBindNearestEdgeWithYaw(railGraphSystem.Graph, bindingPosition, dimension, bindRadiusBlocks, bindYaw, ref cursor, candidates)
				: TryBindNearestEdge(railGraphSystem.Graph, bindingPosition, dimension, bindRadiusBlocks, ref cursor, candidates);

			if (!bindingSucceeded) return false;
		}

		cursor.BoundGraphVersion = graphVersion;
		return true;
	}

	public static bool TryRefreshPolyline(RailGraphLive railGraph, ref RailCursor cursor)
	{
		if (cursor.SegmentHash == 0) return false;
		if (!railGraph.TryGetEdgeGauge(cursor.SegmentHash, out byte gauge) || gauge != cursor.Gauge) return false;
		if (!railGraph.TryGetPolyline16(cursor.SegmentHash, out var polylineCoordinates16)) return false;

		cursor.PolyXYZ16 = polylineCoordinates16;
		cursor.PointCount = polylineCoordinates16.Length / 3;
		if (cursor.PointCount < 2) return false;

		cursor.SegmentIndex = GameMath.Clamp(cursor.SegmentIndex, 0, cursor.PointCount - 2);
		cursor.NormalizedSegmentProgress = GameMath.Clamp(cursor.NormalizedSegmentProgress, 0.0, 1.0);

		RefreshSegmentLimits(railGraph, ref cursor);
		return true;
	}

	public static void RefreshSegmentLimits(RailGraphLive railGraph, ref RailCursor cursor)
	{
		cursor.SegmentMaxSpeedFactor = 1f;
		cursor.SegmentMaterialSpeedCapBPS = 0;

		if (cursor.SegmentHash != 0)
		{
			if (railGraph.TryGetNormalizedEdgeMaxSpeedFactor(cursor.SegmentHash, out float speedFactor)) cursor.SegmentMaxSpeedFactor = GameMath.Clamp(speedFactor, 0.05f, 1f);
			if (railGraph.TryGetEdgeMaterialSpeedCapBPS(cursor.SegmentHash, out ushort speedCapBPS)) cursor.SegmentMaterialSpeedCapBPS = speedCapBPS;
		}
	}

	public static bool TryBindNearestEdge(RailGraphLive railGraph, Vec3d position, int dimension, int radiusBlocks, ref RailCursor cursor, List<ulong> candidates)
	{
		return TryBindNearestEdge(railGraph, position, dimension, radiusBlocks, 0, useYaw: false, ref cursor, candidates);
	}

	public static bool TryBindNearestEdgeWithYaw(RailGraphLive railGraph, Vec3d position, int dimension, int radiusBlocks, float yaw, ref RailCursor cursor, List<ulong> candidates)
	{
		return TryBindNearestEdge(railGraph, position, dimension, radiusBlocks, yaw, useYaw: true, ref cursor, candidates);
	}

	private static bool TryBindNearestEdge(RailGraphLive railGraph, Vec3d position, int dimension, int radiusBlocks, float yaw, bool useYaw, ref RailCursor cursor, List<ulong> candidates)
	{
		railGraph.CollectEdgeCandidatesNear(position, dimension, radiusBlocks, candidates, clear: true);

		double fx = useYaw ? Math.Sin(yaw) : 0;
		double fz = useYaw ? Math.Cos(yaw) : 0;

		const double distanceEpsilon = 1e-9;
		const double orientationTieSquaredDistance = 1.0 / (16.0 * 16.0);

		double bestSquaredDistance = double.MaxValue;
		double bestAbsoluteDot = -1;
		double bestDot = 0;
		ulong bestHash = 0;
		int bestSegmentIndex = 0;
		double bestT = 0;
		int[]? bestPolylineCoordinates16 = null;
		int bestPointCount = 0;

		for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
		{
			ulong edgeHash = candidates[candidateIndex];

			if (!railGraph.TryGetEdgeGauge(edgeHash, out byte gauge) || gauge != cursor.Gauge) continue;
			if (!railGraph.TryGetPolyline16(edgeHash, out var polylineCoordinates16)) continue;

			int pointCount = polylineCoordinates16.Length / 3;
			if (pointCount < 2) continue;

			for (int segmentIndex = 0; segmentIndex < pointCount - 1; segmentIndex++)
			{
				int startCoordinateOffset = segmentIndex * 3;
				int endCoordinateOffset = (segmentIndex + 1) * 3;

				double ax = polylineCoordinates16[startCoordinateOffset + 0] * (1.0 / 16.0);
				double ay = polylineCoordinates16[startCoordinateOffset + 1] * (1.0 / 16.0);
				double az = polylineCoordinates16[startCoordinateOffset + 2] * (1.0 / 16.0);

				double bx = polylineCoordinates16[endCoordinateOffset + 0] * (1.0 / 16.0);
				double by = polylineCoordinates16[endCoordinateOffset + 1] * (1.0 / 16.0);
				double bz = polylineCoordinates16[endCoordinateOffset + 2] * (1.0 / 16.0);

				double d2 = SquaredDistancePointToSegment(position, ax, ay, az, bx, by, bz, out double t);
				double absoluteDot = -1;
				double dot = 0;

				if (useYaw)
				{
					double sx = bx - ax;
					double sz = bz - az;
					double squaredSegmentLength = sx * sx + sz * sz;
					dot = squaredSegmentLength > 1e-12 ? (sx * fx + sz * fz) / Math.Sqrt(squaredSegmentLength) : 0;
					absoluteDot = Math.Abs(dot);
				}

				if (d2 < bestSquaredDistance - distanceEpsilon || (useYaw && d2 <= bestSquaredDistance + orientationTieSquaredDistance && absoluteDot > bestAbsoluteDot + 1e-6))
				{
					bestSquaredDistance = d2;
					bestAbsoluteDot = absoluteDot;
					bestDot = dot;
					bestHash = edgeHash;
					bestSegmentIndex = segmentIndex;
					bestT = t;
					bestPolylineCoordinates16 = polylineCoordinates16;
					bestPointCount = pointCount;
				}
			}
		}

		if (bestHash == 0 || bestPolylineCoordinates16 == null) return false;

		cursor.SegmentHash = bestHash;
		cursor.PolyXYZ16 = bestPolylineCoordinates16;
		cursor.PointCount = bestPointCount;

		RefreshSegmentLimits(railGraph, ref cursor);

		cursor.SegmentIndex = GameMath.Clamp(bestSegmentIndex, 0, cursor.PointCount - 2);
		cursor.NormalizedSegmentProgress = GameMath.Clamp(bestT, 0, 1);
		cursor.Direction = useYaw && bestDot < 0 ? -1 : 1;

		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static double SquaredDistancePointToSegment(Vec3d point, double ax, double ay, double az, double bx, double by, double bz, out double normalizedSegmentProgress)
	{
		double vx = bx - ax;
		double vy = by - ay;
		double vz = bz - az;

		double wx = point.X - ax;
		double wy = point.Y - ay;
		double wz = point.Z - az;

		double c1 = wx * vx + wy * vy + wz * vz;
		if (c1 <= 0)
		{
			normalizedSegmentProgress = 0;
			double dx0 = point.X - ax, dy0 = point.Y - ay, dz0 = point.Z - az;
			return dx0 * dx0 + dy0 * dy0 + dz0 * dz0;
		}

		double c2 = vx * vx + vy * vy + vz * vz;
		if (c2 <= c1)
		{
			normalizedSegmentProgress = 1;
			double dx1 = point.X - bx, dy1 = point.Y - by, dz1 = point.Z - bz;
			return dx1 * dx1 + dy1 * dy1 + dz1 * dz1;
		}

		normalizedSegmentProgress = c1 / c2;

		double px = ax + normalizedSegmentProgress * vx;
		double py = ay + normalizedSegmentProgress * vy;
		double pz = az + normalizedSegmentProgress * vz;

		double dx = point.X - px;
		double dy = point.Y - py;
		double dz = point.Z - pz;

		return dx * dx + dy * dy + dz * dz;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static double SquaredDistancePointToSegmentXZ(double px, double pz, double ax, double az, double bx, double bz, out double normalizedSegmentProgress)
	{
		double vx = bx - ax;
		double vz = bz - az;

		double wx = px - ax;
		double wz = pz - az;

		double c1 = wx * vx + wz * vz;
		if (c1 <= 0)
		{
			normalizedSegmentProgress = 0;
			double dx0 = px - ax;
			double dz0 = pz - az;
			return dx0 * dx0 + dz0 * dz0;
		}

		double c2 = vx * vx + vz * vz;
		if (c2 <= c1)
		{
			normalizedSegmentProgress = 1;
			double dx1 = px - bx;
			double dz1 = pz - bz;
			return dx1 * dx1 + dz1 * dz1;
		}

		normalizedSegmentProgress = c1 / c2;

		double hx = ax + normalizedSegmentProgress * vx;
		double hz = az + normalizedSegmentProgress * vz;
		double dx = px - hx;
		double dz = pz - hz;

		return dx * dx + dz * dz;
	}
	#endregion

	#region Movement (+ Junction Stepping)
	private const int StaleBlockedSelfHealMaxBlockReads = 256;
	private const int StaleBlockedSelfHealMaxRepairAttempts = 8;

	private struct ClearanceMoveContext
	{
		public bool AllowSelfHeal;
		public int RemainingBlockReads;
		public int RemainingRepairAttempts;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static void GetPoint(int[] coordinates16, int pointIndex, out double x, out double y, out double z)
	{
		int coordinateOffset = pointIndex * 3;
		x = coordinates16[coordinateOffset + 0] * (1.0 / 16.0);
		y = coordinates16[coordinateOffset + 1] * (1.0 / 16.0);
		z = coordinates16[coordinateOffset + 2] * (1.0 / 16.0);
	}

	public static void ChooseForwardDirectionFromYaw(float yaw, ref RailCursor cursor)
	{
		if (cursor.PolyXYZ16 == null || cursor.PointCount < 2) return;

		int startPointIndex = cursor.SegmentIndex;
		int endPointIndex = startPointIndex + 1;
		GetPoint(cursor.PolyXYZ16, startPointIndex, out double ax, out _, out double az);
		GetPoint(cursor.PolyXYZ16, endPointIndex, out double bx, out _, out double bz);

		double dx = bx - ax;
		double dz = bz - az;

		double fx = Math.Sin(yaw);
		double fz = Math.Cos(yaw);

		cursor.Direction = (dx * fx + dz * fz) >= 0 ? 1 : -1;
	}

	/// Advance a cursor along its current polyline by a signed distance (blocks).
	/// Returns true when the full distance was consumed. False means we hit a dead-end.
	public static bool AdvanceAlongTrack
	(
		RailGraphLive railGraph, double distance, int wantTurn, RailGraphServerSystem? railGraphSystem, bool useSignalAuthority,
		ref RailCursor cursor, ref double speed, ref double absoluteDistanceTravelled, bool trackAbsoluteDistanceTravelled, out DeadEndInfo deadEnd,
		RailCollisionTrail? collisionTrail = null, long occupancyOwnerID = 0, ConvoyRouteRecorder? tapeRecorder = null
	)
	{
		return AdvanceAlongTrack
		(
			railGraph, distance, wantTurn, null, railGraphSystem, useSignalAuthority, ref cursor, ref speed,
			ref absoluteDistanceTravelled, trackAbsoluteDistanceTravelled, out deadEnd, collisionTrail, occupancyOwnerID, tapeRecorder
		);
	}

	public static bool AdvanceAlongTrack
	(
		RailGraphLive railGraph, double distance, int wantTurn, RailExactTurnPlan? exactTurns, RailGraphServerSystem? railGraphSystem, bool useSignalAuthority,
		ref RailCursor cursor, ref double speed, ref double absoluteDistanceTravelled, bool trackTravelledABS, out DeadEndInfo deadEnd,
		RailCollisionTrail? collisionTrail = null, long occupancyOwnerId = 0, ConvoyRouteRecorder? tapeRecorder = null
	)
	{
		return AdvanceAlongTrack
		(
			railGraph, distance, wantTurn, exactTurns, railGraphSystem, useSignalAuthority, false,
			ref cursor, ref speed, ref absoluteDistanceTravelled, trackTravelledABS, out deadEnd,
			collisionTrail, occupancyOwnerId, tapeRecorder
		);
	}

	public static bool AdvanceAlongTrack
	(
		RailGraphLive railGraph, double distance, int wantTurn, RailExactTurnPlan? exactTurns, RailGraphServerSystem? railGraphSystem,
		bool useSignalAuthority, bool automatedMovement,
		ref RailCursor cursor, ref double speed, ref double absoluteDistanceTravelled, bool trackTravelledABS, out DeadEndInfo deadEnd,
		RailCollisionTrail? collisionTrail = null, long occupancyOwnerID = 0, ConvoyRouteRecorder? tapeRecorder = null,
		bool allowStaleBlockedSelfHeal = false
	)
	{
		deadEnd = default;

		if (railGraphSystem != null && !railGraphSystem.IsRuntimeReady && (useSignalAuthority || automatedMovement)) { speed = 0; return true; }
		if (cursor.PolyXYZ16 == null || cursor.PointCount < 2) return false;

		int speedSign = distance >= 0 ? 1 : -1;
		double remaining = Math.Abs(distance);
		
		ClearanceMoveContext clearance = new() // Explicit opt-in keeps offscreen/speculative traversal cache-only by default.
		{
			AllowSelfHeal = allowStaleBlockedSelfHeal,
			RemainingBlockReads = StaleBlockedSelfHealMaxBlockReads,
			RemainingRepairAttempts = StaleBlockedSelfHealMaxRepairAttempts
		};

		int iterationGuard = 0;
		while (remaining > 1e-8 && iterationGuard++ < 2048)
		{
			if (IsClearanceBlocked(railGraphSystem, cursor.SegmentHash, ref clearance))
			{
				speed = 0;
				deadEnd = DeadEndInfo.ClearanceBlock();
				return true;
			}

			int moveDirection = cursor.Direction * speedSign;
			ulong spanHash = cursor.SegmentHash;
			int spanStartIndex = cursor.SegmentIndex;
			double spanStartT = cursor.NormalizedSegmentProgress;
			double spanStartTravel = absoluteDistanceTravelled;
			int startPointIndex = cursor.SegmentIndex;
			int endPointIndex = cursor.SegmentIndex + 1;

			GetPoint(cursor.PolyXYZ16, startPointIndex, out double x0, out double y0, out double z0);
			GetPoint(cursor.PolyXYZ16, endPointIndex, out double x1, out double y1, out double z1);

			double dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
			double segmentLength = Math.Sqrt(dx * dx + dy * dy + dz * dz);
			if (segmentLength < 1e-9)
			{
				RailStepResult stepResult = StepSpanOrEdge(railGraph, wantTurn, exactTurns, railGraphSystem, useSignalAuthority, automatedMovement, moveDirection, speedSign, occupancyOwnerID, ref clearance, ref cursor);
				if (stepResult == RailStepResult.Advanced) continue;

				speed = 0;
				if (stepResult == RailStepResult.ClearanceBlocked) deadEnd = DeadEndInfo.ClearanceBlock();
				if (stepResult == RailStepResult.SignalBlocked || stepResult == RailStepResult.ClearanceBlocked || stepResult == RailStepResult.RouteBlocked) { return true; }
				return false;
			}

			double remainingToEnd = moveDirection > 0 ? (1.0 - cursor.NormalizedSegmentProgress) * segmentLength : cursor.NormalizedSegmentProgress * segmentLength;

			if (remaining < remainingToEnd)
			{
				double normalizedTravel = remaining / segmentLength;
				cursor.NormalizedSegmentProgress += moveDirection > 0 ? normalizedTravel : -normalizedTravel;
				if (trackTravelledABS) { absoluteDistanceTravelled += remaining; }
				
				if (trackTravelledABS && collisionTrail != null)
				{ collisionTrail.RecordSpan(railGraph, in cursor, spanHash, spanStartIndex, spanStartT, cursor.SegmentIndex, cursor.NormalizedSegmentProgress, spanStartTravel, absoluteDistanceTravelled); }
				
				if (trackTravelledABS && tapeRecorder != null)
				{ tapeRecorder.RecordSpan(railGraph, in cursor, spanHash, spanStartIndex, spanStartT, cursor.SegmentIndex, cursor.NormalizedSegmentProgress, spanStartTravel, absoluteDistanceTravelled); }
				
				remaining = 0;
			}
			else
			{
				// Consume until the end of this span.
				if (trackTravelledABS) absoluteDistanceTravelled += remainingToEnd;
				remaining -= remainingToEnd;

				// Snap to the endpoint (important if stepping fails).
				cursor.NormalizedSegmentProgress = moveDirection > 0 ? 1.0 : 0.0;
				if (trackTravelledABS && collisionTrail != null) { collisionTrail.RecordSpan(railGraph, in cursor, spanHash, spanStartIndex, spanStartT, cursor.SegmentIndex, cursor.NormalizedSegmentProgress, spanStartTravel, absoluteDistanceTravelled); }
				if (trackTravelledABS && tapeRecorder != null) { tapeRecorder.RecordSpan(railGraph, in cursor, spanHash, spanStartIndex, spanStartT, cursor.SegmentIndex, cursor.NormalizedSegmentProgress, spanStartTravel, absoluteDistanceTravelled); }

				// Automation station stop
				// Stop exactly on the marker endpoint and do not continue through it in the same tick. This avoids early-brake stalls and route overshoot.
				if (exactTurns != null && railGraph.TryGetEdgeEndpoints(cursor.SegmentHash, out var edgeA, out var edgeB, out _))
				{
					var endpoint = moveDirection > 0 ? edgeB : edgeA;

					if (exactTurns.ShouldStopAt(cursor.SegmentHash, endpoint))
					{
						speed = 0;
						remaining = 0;
						return true;
					}

				}

				// If the requested travel ended exactly at this endpoint, that is a successful move.
				// Do not attempt to step onto the next edge. Endpoint seeds at dead-ends are valid rail tape boundaries, not movement failures.
				if (remaining <= 1e-8) { remaining = 0; break; }

				RailStepResult stepResult = StepSpanOrEdge(railGraph, wantTurn, exactTurns, railGraphSystem, useSignalAuthority, automatedMovement, moveDirection, speedSign, occupancyOwnerID, ref clearance, ref cursor);
				if (stepResult != RailStepResult.Advanced)
				{
					speed = 0;
					if (stepResult == RailStepResult.ClearanceBlocked) deadEnd = DeadEndInfo.ClearanceBlock();
					if (stepResult == RailStepResult.SignalBlocked || stepResult == RailStepResult.ClearanceBlocked || stepResult == RailStepResult.RouteBlocked) { return true; }

					deadEnd = new DeadEndInfo(true, moveDirection, dx, dy, dz, x0, y0, z0, x1, y1, z1);
					return false;
				}
			}
		}


		if (iterationGuard >= 2048) { speed = 0; }
		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool IsClearanceBlocked(RailGraphServerSystem? railGraphSystem, ulong edgeHash, ref ClearanceMoveContext clearanceContext)
	{
		if (railGraphSystem == null) return false;
		if (!clearanceContext.AllowSelfHeal) return railGraphSystem.IsEdgeClearanceBlockedOrDirty(edgeHash);

		return railGraphSystem.IsEdgeClearanceBlockedForLoadedMovement(edgeHash, ref clearanceContext.RemainingBlockReads, ref clearanceContext.RemainingRepairAttempts);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static RailStepResult StepSpanOrEdge
	(
		RailGraphLive railGraph, int wantTurn, RailExactTurnPlan? exactTurns, RailGraphServerSystem? railGraphSystem,
		bool useSignalAuthority, bool automatedMovement, int moveDirection, int speedSign, long occupancyOwnerID,
		ref ClearanceMoveContext clearance, ref RailCursor cursor
	)
	{
		if (moveDirection > 0)
		{
			if (cursor.SegmentIndex < cursor.PointCount - 2) { cursor.SegmentIndex++; cursor.NormalizedSegmentProgress = 0; return RailStepResult.Advanced; }
			return StepToNextEdgeDetailed(railGraph, wantTurn, exactTurns, railGraphSystem, useSignalAuthority, automatedMovement, moveDirection, speedSign, occupancyOwnerID, ref clearance, ref cursor);
		}
		else
		{
			if (cursor.SegmentIndex > 0) { cursor.SegmentIndex--; cursor.NormalizedSegmentProgress = 1; return RailStepResult.Advanced; }
			return StepToNextEdgeDetailed(railGraph, wantTurn, exactTurns, railGraphSystem, useSignalAuthority, automatedMovement, moveDirection, speedSign, occupancyOwnerID, ref clearance, ref cursor);
		}
	}

	/// Step from the current edge to the next incident edge at the endpoint.
	/// Branch selection prefers the "straightest" continuation, optionally biases to left/right if wantTurn != 0.
	/// wantTurn must be defined relative to the current travel direction (not vehicle facing).
	public static bool StepToNextEdge
	(
		RailGraphLive railGraph, int wantTurn, RailGraphServerSystem? railGraphSystem, bool useSignalAuthority,
		int moveDirection, int speedSign, ref RailCursor cursor
	)
	{
		ClearanceMoveContext clearance = default;
		return StepToNextEdgeDetailed(railGraph, wantTurn, null, railGraphSystem, useSignalAuthority, false, moveDirection, speedSign, 0, ref clearance, ref cursor) == RailStepResult.Advanced;
	}

	public static bool StepToNextEdge
	(
		RailGraphLive railGraph, int wantTurn, RailExactTurnPlan? exactTurns, RailGraphServerSystem? railGraphSystem, bool useSignalAuthority,
		int moveDirection, int speedSign, long occupancyOwnerID, ref RailCursor cursor
	)
	{
		ClearanceMoveContext clearance = default;
		return StepToNextEdgeDetailed(railGraph, wantTurn, exactTurns, railGraphSystem, useSignalAuthority, false, moveDirection, speedSign, occupancyOwnerID, ref clearance, ref cursor) == RailStepResult.Advanced;
	}

	private static RailStepResult StepToNextEdgeDetailed
	(
		RailGraphLive railGraph, int wantTurn, RailExactTurnPlan? exactTurns, RailGraphServerSystem? railGraphSystem,
		bool useSignalAuthority, bool automatedMovement, int moveDirection, int speedSign, long occupancyOwnerID,
		ref ClearanceMoveContext clearance, ref RailCursor cursor
	)
	{
		// Failures in the physical rail state remain real dead ends even for automated movement.
		// RouteBlocked is reserved for cases where track can continue but the automation lease cannot provide a valid continuation.
		if (!railGraph.TryGetEdgeEndpoints(cursor.SegmentHash, out var edgeA, out var edgeB, out byte gauge) || gauge != cursor.Gauge) return RailStepResult.DeadEnd;

		var endpoint = moveDirection > 0 ? edgeB : edgeA;
		bool crossingSignal = railGraph.IsSignalEndpoint(endpoint);
		if (!railGraph.TryGetIncidentEdges(endpoint, out var incidentEdges) || incidentEdges.Count < 2) return RailStepResult.DeadEnd;
		if (cursor.PolyXYZ16 == null || cursor.PointCount < 2) return RailStepResult.DeadEnd;

		if (exactTurns != null && exactTurns.TryGet(cursor.SegmentHash, endpoint, out ulong forcedNextEdgeHash))
		{
			if (forcedNextEdgeHash == cursor.SegmentHash) return RoutePlanFailure(railGraph, exactTurns, automatedMovement, endpoint, incidentEdges, in cursor);

			bool incident = false;
			for (int incidentEdgeIndex = 0; incidentEdgeIndex < incidentEdges.Count; incidentEdgeIndex++) { if (incidentEdges[incidentEdgeIndex] == forcedNextEdgeHash) { incident = true; break; } }
			if (!incident) return RoutePlanFailure(railGraph, exactTurns, automatedMovement, endpoint, incidentEdges, in cursor);

			if (!railGraph.TryGetEdgeGauge(forcedNextEdgeHash, out byte forcedGauge) || forcedGauge != cursor.Gauge) return RoutePlanFailure(railGraph, exactTurns, automatedMovement, endpoint, incidentEdges, in cursor);
			if (!railGraph.TryGetPolyline16(forcedNextEdgeHash, out int[] forcedPolylineCoordinates16)) return RoutePlanFailure(railGraph, exactTurns, automatedMovement, endpoint, incidentEdges, in cursor);
			if (!railGraph.TryGetEdgeEndpoints(forcedNextEdgeHash, out var forcedEdgeA, out var forcedEdgeB, out _)) return RoutePlanFailure(railGraph, exactTurns, automatedMovement, endpoint, incidentEdges, in cursor);

			int forcedPointCount = forcedPolylineCoordinates16.Length / 3;
			if (forcedPointCount < 2) return RoutePlanFailure(railGraph, exactTurns, automatedMovement, endpoint, incidentEdges, in cursor);
			if (!RailTransitionRules.IsDirectedTransitionAllowedSameGauge(endpoint, cursor.Gauge, cursor.PolyXYZ16, forcedPolylineCoordinates16)) return RoutePlanFailure(railGraph, exactTurns, automatedMovement, endpoint, incidentEdges, in cursor);

			int forcedPhysicalDirection;
			if (endpoint.Equals(forcedEdgeA)) forcedPhysicalDirection = 1;
			else if (endpoint.Equals(forcedEdgeB)) forcedPhysicalDirection = -1;
			else return RoutePlanFailure(railGraph, exactTurns, automatedMovement, endpoint, incidentEdges, in cursor);

			if (IsClearanceBlocked(railGraphSystem, forcedNextEdgeHash, ref clearance)) { return RailStepResult.ClearanceBlocked; }

			if (crossingSignal && automatedMovement && railGraphSystem != null)
			{
				SignalAuthorityResult authorityResult = railGraphSystem.CheckAndReserveSignalSectionAuthority(occupancyOwnerID, endpoint, cursor.SegmentHash, forcedNextEdgeHash, exactTurns);
				if (authorityResult != SignalAuthorityResult.Allowed)
				{
					exactTurns.MarkBoundaryBlocked(endpoint, cursor.SegmentHash, forcedNextEdgeHash, authorityResult);
					return RailStepResult.SignalBlocked;
				}
			}

			cursor.SegmentHash = forcedNextEdgeHash;
			cursor.PolyXYZ16 = forcedPolylineCoordinates16;
			cursor.PointCount = forcedPointCount;
			RefreshSegmentLimits(railGraph, ref cursor);

			if (forcedPhysicalDirection > 0) { cursor.SegmentIndex = 0; cursor.NormalizedSegmentProgress = 0; }
			else { cursor.SegmentIndex = cursor.PointCount - 2; cursor.NormalizedSegmentProgress = 1; }

			cursor.Direction = forcedPhysicalDirection * speedSign;

			return RailStepResult.Advanced;
		}

		// Automated movement never falls through into geometric switch selection.
		// A route plan must name the branch at a real junction, only an unambiguous degree-two continuation may be traversed without an explicit turn entry.
		if (automatedMovement)
		{
			ulong continuationHash = 0;
			int[]? continuationPolylineCoordinates16 = null;
			int continuationPointCount = 0;
			int continuationPhysicalDirection = 1;
			int continuationCount = 0;

			for (int incidentEdgeIndex = 0; incidentEdgeIndex < incidentEdges.Count; incidentEdgeIndex++)
			{
				ulong edgeHash = incidentEdges[incidentEdgeIndex];
				if (edgeHash == cursor.SegmentHash) continue;
				if (!railGraph.TryGetEdgeGauge(edgeHash, out byte edgeGauge) || edgeGauge != cursor.Gauge) continue;

				continuationCount++;
				if (continuationCount > 1) break;

				if 
				(
					!railGraph.TryGetPolyline16(edgeHash, out int[] continuationCoordinates16) ||
					!railGraph.TryGetEdgeEndpoints(edgeHash, out var continuationEdgeA, out var continuationEdgeB, out _)
				) { continue; }

				int lengthPC = continuationCoordinates16.Length / 3;
				if (lengthPC < 2) continue;

				int physicalDirection;
				if (endpoint.Equals(continuationEdgeA)) physicalDirection = 1;
				else if (endpoint.Equals(continuationEdgeB)) physicalDirection = -1;
				else continue;

				continuationHash = edgeHash;
				continuationPolylineCoordinates16 = continuationCoordinates16;
				continuationPointCount = lengthPC;
				continuationPhysicalDirection = physicalDirection;
			}

			if (continuationCount != 1) return RoutePlanFailure(railGraph, exactTurns, true, endpoint, incidentEdges, in cursor);

			// A degree-two endpoint needs no route decision.
			// If its sole physical continuation cannot actually be traversed, that is a dead end rather than missing automation guidance.
			if (continuationHash == 0 || continuationPolylineCoordinates16 == null) return RailStepResult.DeadEnd;

			if (!RailTransitionRules.IsDirectedTransitionAllowedSameGauge(endpoint, cursor.Gauge, cursor.PolyXYZ16, continuationPolylineCoordinates16)) return RailStepResult.DeadEnd;

			if (IsClearanceBlocked(railGraphSystem, continuationHash, ref clearance)) return RailStepResult.ClearanceBlocked;

			if (crossingSignal && railGraphSystem != null)
			{
				SignalAuthorityResult authorityResult = railGraphSystem.CheckAndReserveSignalSectionAuthority
				(
					occupancyOwnerID,
					endpoint,
					cursor.SegmentHash,
					continuationHash,
					exactTurns
				);
				if (authorityResult != SignalAuthorityResult.Allowed)
				{
					exactTurns?.MarkBoundaryBlocked(endpoint, cursor.SegmentHash, continuationHash, authorityResult);
					return RailStepResult.SignalBlocked;
				}
			}

			cursor.SegmentHash = continuationHash;
			cursor.PolyXYZ16 = continuationPolylineCoordinates16;
			cursor.PointCount = continuationPointCount;
			RefreshSegmentLimits(railGraph, ref cursor);

			if (continuationPhysicalDirection > 0) { cursor.SegmentIndex = 0; cursor.NormalizedSegmentProgress = 0; }
			else { cursor.SegmentIndex = cursor.PointCount - 2; cursor.NormalizedSegmentProgress = 1; }

			cursor.Direction = continuationPhysicalDirection * speedSign;
			return RailStepResult.Advanced;
		}

		// Current heading at endpoint in XZ plane (ignore Y so slopes/quantization don't bias switch choice)
		int pointCount = cursor.PointCount;
		int[] currentCoordinates16 = cursor.PolyXYZ16;

		int endIndex = moveDirection > 0 ? (pointCount - 1) : 0;
		int seekIndex = moveDirection > 0 ? (pointCount - 2) : 1;

		int endCoordinateOffset = endIndex * 3;
		int endX16 = currentCoordinates16[endCoordinateOffset + 0];
		int endZ16 = currentCoordinates16[endCoordinateOffset + 2];

		double currentHeadingX = 0, currentHeadingZ = 0;
		while (seekIndex >= 0 && seekIndex < pointCount)
		{
			int coordinateOffset = seekIndex * 3;
			double dx = endX16 - currentCoordinates16[coordinateOffset + 0];
			double dz = endZ16 - currentCoordinates16[coordinateOffset + 2];
			double squaredDistance = dx * dx + dz * dz;
			if (squaredDistance > 1e-6)
			{
				double inverseDistance = 1.0 / Math.Sqrt(squaredDistance);
				currentHeadingX = dx * inverseDistance;
				currentHeadingZ = dz * inverseDistance;
				break;
			}
			seekIndex += moveDirection > 0 ? -1 : 1;
		}
		if (currentHeadingX == 0 && currentHeadingZ == 0) return RailStepResult.DeadEnd;

		ulong bestHash = 0;
		double bestDot = -2;
		int bestPhysicalDirection = 1;
		int[]? bestPolylineCoordinates16 = null;
		int bestPointCount = 0;

		// Side-specific best (only used when wantTurn != 0)
		ulong bestSideHash = 0;
		double bestSideScore = -2;
		int bestSidePhysicalDirection = 1;
		int[]? bestSidePolylineCoordinates16 = null;
		int bestSidePointCount = 0;

		for (int incidentEdgeIndex = 0; incidentEdgeIndex < incidentEdges.Count; incidentEdgeIndex++)
		{
			ulong edgeHash = incidentEdges[incidentEdgeIndex];
			if (edgeHash == cursor.SegmentHash) continue;

			if (!railGraph.TryGetEdgeGauge(edgeHash, out byte edgeGauge) || edgeGauge != cursor.Gauge) continue;
			if (!railGraph.TryGetPolyline16(edgeHash, out var candidateCoordinates16)) continue;
			if (!railGraph.TryGetEdgeEndpoints(edgeHash, out var candidateEdgeA, out var candidateEdgeB, out _)) continue;

			int candidatePointCount = candidateCoordinates16.Length / 3;
			if (candidatePointCount < 2) continue;

			int physicalDirection;
			RailGraphLive.EndpointKey other;

			if (endpoint.Equals(candidateEdgeA)) { physicalDirection = 1; other = candidateEdgeB; }
			else if (endpoint.Equals(candidateEdgeB)) { physicalDirection = -1; other = candidateEdgeA; }
			else continue;

			double dx = other.X16 - endpoint.X16;
			double dz = other.Z16 - endpoint.Z16;
			double squaredDistance = dx * dx + dz * dz;
			if (squaredDistance <= 1e-6) continue;
			if (!RailTransitionRules.IsDirectedTransitionAllowedSameGauge(endpoint, cursor.Gauge, cursor.PolyXYZ16, candidateCoordinates16)) continue;

			double inverseDistance = 1.0 / Math.Sqrt(squaredDistance);
			double candidateHeadingX = dx * inverseDistance;
			double candidateHeadingZ = dz * inverseDistance;

			double dot = currentHeadingX * candidateHeadingX + currentHeadingZ * candidateHeadingZ; // Straight, defined in XZ plane
			if (dot > bestDot + 1e-6 || (Math.Abs(dot - bestDot) <= 1e-6 && (bestHash == 0 || edgeHash < bestHash)))
			{
				bestDot = dot;
				bestHash = edgeHash;
				bestPhysicalDirection = physicalDirection;
				bestPolylineCoordinates16 = candidateCoordinates16;
				bestPointCount = candidatePointCount;
			}

			if (wantTurn == 0) continue;

			// Prefer the branch on the requested side that stays as close to straight as possible.
			// Also, don't accept steering choices that effectively require turning back on ourselves.
			double sideScore = dot; // same as horizontal dot here
			if (sideScore < -0.25) continue;

			// Vintage Story XZ convention: with yaw=atan2(dx, dz), cross>0 corresponds to "right", cross<0 to "left".
			double cross = currentHeadingX * candidateHeadingZ - currentHeadingZ * candidateHeadingX;
			const double sideEpsilon = 1e-4;
			if (wantTurn < 0) { if (cross >= -sideEpsilon) continue; }	// Left  => cross < 0
			else { if (cross <= sideEpsilon) continue; }				// Right => cross > 0

			if (sideScore > bestSideScore + 1e-6 || (Math.Abs(sideScore - bestSideScore) <= 1e-6 && (bestSideHash == 0 || edgeHash < bestSideHash)))
			{
				bestSideScore = sideScore;
				bestSideHash = edgeHash;
				bestSidePhysicalDirection = physicalDirection;
				bestSidePolylineCoordinates16 = candidateCoordinates16;
				bestSidePointCount = candidatePointCount;
			}
		}

		ulong chosenHash = bestSideHash != 0 ? bestSideHash : bestHash;
		int chosenPhysicalDirection = bestSideHash != 0 ? bestSidePhysicalDirection : bestPhysicalDirection;
		int[]? chosenPolylineCoordinates16 = bestSideHash != 0 ? bestSidePolylineCoordinates16 : bestPolylineCoordinates16;
		int chosenPointCount = bestSideHash != 0 ? bestSidePointCount : bestPointCount;

		if (chosenHash == 0 || chosenPolylineCoordinates16 == null) return RailStepResult.DeadEnd;
		if (IsClearanceBlocked(railGraphSystem, chosenHash, ref clearance)) { return RailStepResult.ClearanceBlocked; }

		cursor.SegmentHash = chosenHash;
		cursor.PolyXYZ16 = chosenPolylineCoordinates16;
		cursor.PointCount = chosenPointCount;
		RefreshSegmentLimits(railGraph, ref cursor);

		if (chosenPhysicalDirection > 0) { cursor.SegmentIndex = 0; cursor.NormalizedSegmentProgress = 0; }
		else { cursor.SegmentIndex = cursor.PointCount - 2; cursor.NormalizedSegmentProgress = 1; }

		cursor.Direction = chosenPhysicalDirection * speedSign;

		return RailStepResult.Advanced;
	}
	private static RailStepResult RoutePlanFailure
	(
		RailGraphLive graph,
		RailExactTurnPlan? exactTurns,
		bool automatedMovement,
		RailGraphLive.EndpointKey endpoint,
		List<ulong> incidentEdges,
		in RailCursor cursor
	)
	{
		if (!automatedMovement) return RailStepResult.DeadEnd;

		// RouteBlocked means the physical railway still offers a traversable continuation,
		// but the current automation lease cannot name or validate the one it needs.
		// If there is no physically usable continuation at all, preserve real dead-end semantics so loaded movement can run the normal impact/derailment path.
		if (!HasTraversableContinuation(graph, endpoint, incidentEdges, in cursor)) return RailStepResult.DeadEnd;

		exactTurns?.MarkPathBlocked(endpoint, cursor.SegmentHash);
		return RailStepResult.RouteBlocked;
	}

	private static bool HasTraversableContinuation
	(
		RailGraphLive graph,
		RailGraphLive.EndpointKey endpoint,
		List<ulong> incidentEdges,
		in RailCursor cursor
	)
	{
		if (graph == null || incidentEdges == null || cursor.PolyXYZ16 == null) return false;

		for (int incidentEdgeIndex = 0; incidentEdgeIndex < incidentEdges.Count; incidentEdgeIndex++)
		{
			ulong edgeHash = incidentEdges[incidentEdgeIndex];
			if (edgeHash == cursor.SegmentHash) continue;
			if (!graph.TryGetEdgeGauge(edgeHash, out byte gauge) || gauge != cursor.Gauge) continue;
			if (!graph.TryGetPolyline16(edgeHash, out int[] candidateCoordinates16) || candidateCoordinates16.Length < 6) continue;
			if (!graph.TryGetEdgeEndpoints(edgeHash, out var edgeA, out var edgeB, out _)) continue;
			if (!endpoint.Equals(edgeA) && !endpoint.Equals(edgeB)) continue;
			if (!RailTransitionRules.IsDirectedTransitionAllowedSameGauge(endpoint, cursor.Gauge, cursor.PolyXYZ16, candidateCoordinates16)) continue;

			return true;
		}

		return false;
	}
	#endregion

	#region Pose
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static void StopRailMotion(Entity entity, ref double speed)
	{
		speed = 0;
		entity.ServerPos.Motion.Set(0, 0, 0);
		entity.Pos.Motion.Set(0, 0, 0);
	}

	public static void WriteWorldPositionFromTrack(EntityPos serverPosition, EntityPos clientPosition, ref RailCursor cursor, double motionSpeedBPS = 0)
	{
		if (!TryReadWorldPoseFromTrack(ref cursor, out double positionX, out double positionY, out double positionZ, out float yaw, out float roll)) return;
		WriteWorldPose(serverPosition, clientPosition, positionX, positionY, positionZ, yaw, roll, motionSpeedBPS);
	}

	public static void WriteWorldPose(EntityPos serverPosition, EntityPos clientPosition, double positionX, double positionY, double positionZ, float yaw, float roll, double motionSpeedBPS = 0)
	{
		double oldX = serverPosition.X;
		double oldY = serverPosition.Y;
		double oldZ = serverPosition.Z;

		serverPosition.X = (float)positionX;
		serverPosition.Y = (float)positionY;
		serverPosition.Z = (float)positionZ;
		serverPosition.Yaw = yaw;
		serverPosition.Roll = roll;
		serverPosition.Pitch = 0f;

		double dx = positionX - oldX;
		double dy = positionY - oldY;
		double dz = positionZ - oldZ;
		double squaredDisplacementLength = dx * dx + dy * dy + dz * dz;

		if (motionSpeedBPS <= 1e-9) { serverPosition.Motion.Set(0, 0, 0); }
		else if (squaredDisplacementLength > 1e-12)
		{
			double scale = Math.Abs(motionSpeedBPS) / (60.0 * Math.Sqrt(squaredDisplacementLength));
			serverPosition.Motion.Set(dx * scale, dy * scale, dz * scale);
		}
		else
		{
			double motionX = serverPosition.Motion.X;
			double motionY = serverPosition.Motion.Y;
			double motionZ = serverPosition.Motion.Z;
			double squaredMotionLength = motionX * motionX + motionY * motionY + motionZ * motionZ;
			if (squaredMotionLength > 1e-12)
			{
				double scale = Math.Abs(motionSpeedBPS) / (60.0 * Math.Sqrt(squaredMotionLength));
				serverPosition.Motion.Set(motionX * scale, motionY * scale, motionZ * scale);
			}
		}

		clientPosition.SetFrom(serverPosition);
	}

	public static bool TryReadWorldPoseFromTrack(ref RailCursor cursor, out double positionX, out double positionY, out double positionZ, out float yaw, out float roll)
	{
		positionX = positionY = positionZ = 0;
		yaw = roll = 0;

		if (cursor.PolyXYZ16 == null || cursor.PointCount < 2) return false;

		int startPointIndex = cursor.SegmentIndex;
		int endPointIndex = cursor.SegmentIndex + 1;

		GetPoint(cursor.PolyXYZ16, startPointIndex, out double ax, out double ay, out double az);
		GetPoint(cursor.PolyXYZ16, endPointIndex, out double bx, out double by, out double bz);

		positionX = ax + (bx - ax) * cursor.NormalizedSegmentProgress;
		positionY = ay + (by - ay) * cursor.NormalizedSegmentProgress;
		positionZ = az + (bz - az) * cursor.NormalizedSegmentProgress;

		// Orientation: match the current polyline span tangent (includes vertical component).
		int facingDirection = cursor.Direction; // might no longer be necessary to abstract c.dir

		if (cursor.OrientationCacheSegmentHash != cursor.SegmentHash || cursor.OrientationCacheSegmentIndex != cursor.SegmentIndex || cursor.OrientationCacheDirection != facingDirection)
		{
			cursor.OrientationCacheSegmentHash = cursor.SegmentHash;
			cursor.OrientationCacheSegmentIndex = cursor.SegmentIndex;
			cursor.OrientationCacheDirection = facingDirection;

			double forwardDeltaX = facingDirection > 0 ? (bx - ax) : (ax - bx);
			double forwardDeltaY = facingDirection > 0 ? (by - ay) : (ay - by);
			double forwardDeltaZ = facingDirection > 0 ? (bz - az) : (az - bz);

			cursor.OrientationCacheYaw = (float)Math.Atan2(forwardDeltaX, forwardDeltaZ);

			double horizontalLength = Math.Sqrt(forwardDeltaX * forwardDeltaX + forwardDeltaZ * forwardDeltaZ);
			// Tilt up/down along slopes. | NOTE: VS's default entity renderer applies EntityPos.Roll as a Z-rotation *after* yaw.
			cursor.OrientationCacheRoll = horizontalLength < 1e-8 ? 0f : (float)(-Math.Atan2(forwardDeltaY, horizontalLength));
		}

		yaw = cursor.OrientationCacheYaw;
		roll = cursor.OrientationCacheRoll;
		return true;
	}
	#endregion

	#region Dead End Check
	/// Returns true if there's a solid (colliding) non-track block immediately ahead of the given endpoint. Used to decide whether to stop or derail upon reaching an end.
	public static bool IsBlockingWallAhead(ICoreAPI coreAPI, int dimension, double endX, double endY, double endZ, double forwardX, double forwardZ, BlockPos temporaryPosition)
	{
		// Check a point slightly beyond the track endpoint.
		int blockX = (int)Math.Floor(endX + forwardX * 0.6);
		int blockY = (int)Math.Floor(endY);
		int blockZ = (int)Math.Floor(endZ + forwardZ * 0.6);

		temporaryPosition.Set(blockX, blockY, blockZ);
		temporaryPosition.dimension = dimension;

		Block block = coreAPI.World.BlockAccessor.GetBlock(temporaryPosition);
		if (block == null || block.Id == 0) return false;
		if (TrackSpecsDictionary.IsTrack(block)) return false;

		var collisionBoxes = block.CollisionBoxes;
		return collisionBoxes != null && collisionBoxes.Length > 0;
	}
	#endregion

	#region Damage Compute
	public const double CollisionDamageMultiplier = 0.25;
	public const double CrashDamageMultiplier = 0.50;
	public const double EndTrackCollisionDamageSpeedThreshold = 18.0;
	public const double AlongTrackCollisionDamageSpeedThreshold = 16.0;
	public const double AlongTrackCollisionEjectSpeedThreshold = 14.0;

	private const float ImpactShakeMinStrength = 0.12f;
	private const float ImpactShakeMaxStrength = 1.0f;
	private const double ImpactShakeFullStrengthBPS = 30.0;
	private const float ImpactShakeRangeBlocks = 64f;

	public static float ComputeTrainCrashDamageBars(double blocksPerSec)
	{
		double absoluteSpeed = Math.Abs(blocksPerSec);

		if (absoluteSpeed <= 0) return 0f;
		if (absoluteSpeed <= 8.0) return (float)(absoluteSpeed / 8.0);
		if (absoluteSpeed >= 33.0) return 18f;

		return (float)(1.0 + (absoluteSpeed - 8.0) * (17.0 / 25.0));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static float ComputePassengerImpactDamageBars(double absoluteImpactSpeed, double damagePerBPS)
	{
		double absoluteSpeed = Math.Abs(absoluteImpactSpeed);
		return absoluteSpeed <= 0 || damagePerBPS <= 0 ? 0f : (float)(absoluteSpeed * damagePerBPS);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static float ComputeImpactShakeStrength(double absoluteImpactSpeed)
	{
		float strength = (float)(Math.Abs(absoluteImpactSpeed) / ImpactShakeFullStrengthBPS);
		return GameMath.Clamp(strength, ImpactShakeMinStrength, ImpactShakeMaxStrength);
	}

	public static void ServerShakeImpact(ICoreAPI coreAPI, Vec3d position, double absoluteImpactSpeed)
	{
		if (coreAPI?.Side != EnumAppSide.Server || position == null) return;

		coreAPI.ModLoader.GetModSystem<ScreenshakeToClientModSystem>()?.ShakeScreen(position, ComputeImpactShakeStrength(absoluteImpactSpeed), ImpactShakeRangeBlocks);
	}

	public static void ServerApplyPassengerImpact
	(
		Entity vehicle, EntityBehaviorSeatable? seatable,
		double absoluteImpactSpeed, double damagePerBPS, double damageThresholdBPS, double ejectThresholdBPS,
		Entity? causeEntity = null
	)
	{
		if (vehicle?.Api?.Side != EnumAppSide.Server) return;

		double speed = Math.Abs(absoluteImpactSpeed);
		bool shouldEject = speed >= ejectThresholdBPS;
		float damage = speed >= damageThresholdBPS ? ComputePassengerImpactDamageBars(speed, damagePerBPS) : 0f;

		if (!shouldEject && damage <= 0.01f) return;

		var seats = seatable?.Seats;
		if (seats == null) return;

		for (int seatIndex = 0; seatIndex < seats.Length; seatIndex++)
		{
			if (seats[seatIndex]?.Passenger is not EntityAgent passenger) continue;
			if (shouldEject) passenger.TryUnmount();

			if (damage <= 0.01f || passenger is not EntityPlayer player) continue;

			player.ReceiveDamage
			(
				new DamageSource
				{
					Source = EnumDamageSource.Entity,
					Type = EnumDamageType.BluntAttack,
					SourceEntity = vehicle,
					CauseEntity = causeEntity ?? vehicle,
					KnockbackStrength = 0f
				},
				damage
			);
		}
	}

	public static float ComputeTrainCrashKnockbackStrength(double blocksPerSec)
	{
		const double referenceSpeedBPS = 2.0;
		const double referenceKnockbackFactor = 0.25;
		const double maxKnockbackFactor = 0.55;
		const double knockbackKneeSpeedBPS = referenceSpeedBPS * (maxKnockbackFactor / referenceKnockbackFactor - 1.0);

		double absoluteSpeed = Math.Abs(blocksPerSec);
		double factor = (absoluteSpeed <= 1e-6) ? 0.0 : (maxKnockbackFactor * (absoluteSpeed / (absoluteSpeed + knockbackKneeSpeedBPS)));
		return (float)(factor * 10.0);
	}

	private static TagSetFast TrainCrashDamageTags;
	private static TagSetFast TrainCrashNoDamageTags;

	public static void SetupTrainCrashTagSets(ICoreAPI coreAPI)
	{
		coreAPI.EntityTagRegistry.TryCreateTagSetAndLogIssues(out TrainCrashDamageTags, "rust-creature", "animal");
		coreAPI.EntityTagRegistry.TryCreateTagSetAndLogIssues(out TrainCrashNoDamageTags, "tamed", "trader");
	}

	public static bool ShouldTrainCrashDamage(EntityAgent agent)
	{
		if (agent is EntityPlayer) return true;

		if (agent.Tags.Overlaps(in TrainCrashNoDamageTags)) return false; // Pets, tamed animals, traders get pushed but not damaged
		return agent.Tags.Overlaps(in TrainCrashDamageTags); // Damage monsters and wildlife
	}

	public static void ApplyVanillaKnockbackOnly(Entity sourceEntity, EntityAgent agent, Vec3d sourcePosition, float knockbackStrength, float verticalKnockbackDivisor)
	{
		if (knockbackStrength <= 0.001f) return;

		double dx = agent.SidedPos.X - sourcePosition.X;
		double dy = agent.SidedPos.Y - sourcePosition.Y;
		double dz = agent.SidedPos.Z - sourcePosition.Z;

		double directionLength = Math.Sqrt(dx * dx + dy * dy + dz * dz);
		if (directionLength < 1e-9)
		{
			double yaw = sourceEntity == null ? 0 : sourceEntity.ServerPos.Yaw;
			dx = Math.Sin(yaw);
			dy = 0;
			dz = Math.Cos(yaw);
			directionLength = Math.Sqrt(dx * dx + dz * dz);
			if (directionLength < 1e-9) return;
		}

		double inverseLength = 1.0 / directionLength;
		dx *= inverseLength; dy *= inverseLength; dz *= inverseLength;

		dy = 0.7;
		dy /= verticalKnockbackDivisor;

		float factor = knockbackStrength * GameMath.Clamp((1 - agent.Properties.KnockbackResistance) / 10f, 0, 1);
		if (factor <= 1e-4f) return;

		agent.WatchedAttributes.SetFloat("onHurtDir", (float)Math.Atan2(dx, dz));
		agent.WatchedAttributes.SetDouble("kbdirX", dx * factor);
		agent.WatchedAttributes.SetDouble("kbdirY", dy * factor);
		agent.WatchedAttributes.SetDouble("kbdirZ", dz * factor);

		agent.WatchedAttributes.SetInt("onHurtCounter", agent.WatchedAttributes.GetInt("onHurtCounter") + 1);
		agent.WatchedAttributes.SetFloat("onHurt", 0.051f);
	}
	#endregion


	#region Death & Despawn
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsInternalExplosionDeath(DamageSource damageSource)
	{
		// Our own steam engine explosions mark the death as 'Machine'
		return damageSource != null && damageSource.Source == EnumDamageSource.Machine;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsServerKillCommandDeath(DamageSource damageSource)
	{
		return damageSource != null && (damageSource.Source == EnumDamageSource.Suicide || (damageSource.Source == EnumDamageSource.Player && damageSource.Type == EnumDamageType.Gravity));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsImmediateRemovalDeath(DamageSource damageSource)
	{
		return IsInternalExplosionDeath(damageSource) || IsServerKillCommandDeath(damageSource);
	}

	public static bool TryServerHardDespawn(Entity entity, EnumDespawnReason reason, DamageSource deathSource)
	{
		if (entity?.Api?.Side != EnumAppSide.Server) return false;
		if (entity.Api.World is not IServerWorldAccessor serverWorld) return false;

		var despawnData = new EntityDespawnData
		{
			Reason = reason,
			DamageSourceForDeath = deathSource
		};

		// ServerMain.DespawnEntity() queues entity.DespawnReason, so ensure it matches.
		entity.DespawnReason = despawnData;
		serverWorld.DespawnEntity(entity, despawnData);
		return true;
	}
	#endregion

	#region Derailed physics
	public static void ServerStartDerailed
	(
		Entity entity,
		ref bool derailedFlag,
		ref RailCursor cursor,
		ref double speed,
		Vec3d derailedVelocityBPS,
		ref float derailedYawVelocityRadians,
		double absoluteImpactSpeed,
		double forwardX, double forwardZ,
		double sideX, double sideZ,
		int orderIndex, int cartCount,
		Random random,
		double nudgeForward, double nudgeSide,
		string attributeDerailedKey = "derailed"
	)
	{
		if (entity.Api.Side != EnumAppSide.Server) return;

		// Switch to derailed state
		derailedFlag = true;
		entity.WatchedAttributes.SetBool(attributeDerailedKey, true);
		entity.WatchedAttributes.MarkPathDirty(attributeDerailedKey);

		entity.Attributes.SetBool(attributeDerailedKey, true);

		// Clear rail binding so we never auto-rebind.
		cursor.ClearBinding();
		speed = 0;

		entity.Attributes.SetLong("segHash", 0);
		entity.Attributes.SetDouble("speed", 0);

		// Slight spread within a convoy so they don't perfectly overlap.
		double spread = (cartCount <= 1) ? 0 : (orderIndex / (double)(cartCount - 1)) - 0.5; // -0.5 to 0.5

		// Nudge out of rail space immediately (forward + a touch of side).
		// If the side spread embeds us in terrain, the overlap resolver can selectively discard only that lateral component while preserving the forward nudge.
		double lateralOffsetX = sideX * spread * nudgeSide;
		double lateralOffsetZ = sideZ * spread * nudgeSide;
		entity.ServerPos.X += (float)(forwardX * nudgeForward + lateralOffsetX);
		entity.ServerPos.Z += (float)(forwardZ * nudgeForward + lateralOffsetZ);
		ServerResolveInitialTerrainOverlapUpward(entity, lateralOffsetX, lateralOffsetZ);
		entity.Pos.SetFrom(entity.ServerPos);

		// Initial slide velocity: forward + a bit of sideways chaos.
		double baseSpeed = absoluteImpactSpeed * (0.82 + random.NextDouble() * 0.20); // 0.82 - 1.02
		double lateral = (random.NextDouble() - 0.5) * 0.25 * Math.Max(1.0, absoluteImpactSpeed) + spread * 0.15 * Math.Max(1.0, absoluteImpactSpeed);

		derailedVelocityBPS.Set(forwardX * baseSpeed + sideX * lateral, 0, forwardZ * baseSpeed + sideZ * lateral);
		entity.ServerPos.Motion.Set(derailedVelocityBPS.X / 60.0, derailedVelocityBPS.Y / 60.0, derailedVelocityBPS.Z / 60.0);
		entity.Pos.SetFrom(entity.ServerPos);

		// Spin around vertical axis only (yaw).
		entity.ServerPos.Roll = 0f;
		entity.ServerPos.Pitch = 0f;
		derailedYawVelocityRadians = (float)((random.NextDouble() * 2.0 - 1.0) * (0.55 + 0.06 * Math.Min(25.0, absoluteImpactSpeed)));
	}

	public static void TickDerailed
	(
		Entity entity, float deltaTime,
		Vec3d derailedVelocityBPS,
		ref float derailedYawVelocityRadians,
		Vec3d temporaryNewPosition,
		BlockPos temporaryBelowPosition,
		in DerailedPhysicsParameters physicsParameters
	)
	{
		if (entity.Api.Side != EnumAppSide.Server || deltaTime <= 0) return;

		if // Once we've settled into a crash site, early out to avoid burning out the CPU (Classic Warband ragdoll physics trick, worked then and it works now)
		(
			entity.OnGround
			&& Math.Abs(derailedVelocityBPS.X) < physicsParameters.StopEpsilon
			&& Math.Abs(derailedVelocityBPS.Z) < physicsParameters.StopEpsilon
			&& Math.Abs(derailedVelocityBPS.Y) < 0.01
			&& Math.Abs(derailedYawVelocityRadians) < 0.002f
		)
		{
			derailedVelocityBPS.Set(0, 0, 0);
			derailedYawVelocityRadians = 0f;
			entity.ServerPos.Motion.Set(0, 0, 0);
			entity.Pos.Motion.Set(0, 0, 0);
			return;
		}

		double oldX = entity.ServerPos.X, oldY = entity.ServerPos.Y, oldZ = entity.ServerPos.Z;

		// Gravity
		derailedVelocityBPS.Y -= GlobalConstants.GravityPerSecond * physicsParameters.GravityMultiplier * deltaTime;

		// Yaw spin with damping (visual-only)
		if (Math.Abs(derailedYawVelocityRadians) > 0.0001f)
		{
			entity.ServerPos.Yaw += derailedYawVelocityRadians * deltaTime;
			derailedYawVelocityRadians = (float)(derailedYawVelocityRadians * Math.Exp(-physicsParameters.YawDrag * deltaTime));
		}

		double vx0 = derailedVelocityBPS.X, vy0 = derailedVelocityBPS.Y, vz0 = derailedVelocityBPS.Z;

		float deltaTimeFactor = 60f * deltaTime;
		entity.ServerPos.Motion.X = vx0 / 60.0;
		entity.ServerPos.Motion.Y = vy0 / 60.0;
		entity.ServerPos.Motion.Z = vz0 / 60.0;

		entity.CollidedHorizontally = false;
		entity.CollidedVertically = false;

		temporaryNewPosition.Set(oldX, oldY, oldZ);
		entity.Api.World.CollisionTester.ApplyTerrainCollision(entity, entity.ServerPos, deltaTimeFactor, ref temporaryNewPosition, stepHeight: 1.05f, yExtra: 1f);

		entity.ServerPos.SetPos(temporaryNewPosition);
		entity.Pos.SetFrom(entity.ServerPos);

		// Displacement this tick
		double actualDx = temporaryNewPosition.X - oldX;
		double actualDy = temporaryNewPosition.Y - oldY;
		double actualDz = temporaryNewPosition.Z - oldZ;

		// Intended displacement this tick (pre-collision)
		double intendedDeltaX = vx0 * deltaTime;
		double intendedDeltaY = vy0 * deltaTime;
		double intendedDeltaZ = vz0 * deltaTime;

		const double hitEpsilon = 0.0005; // > CollisionTester epsilon (0.0001)

		bool hitX = (intendedDeltaX > 0 && actualDx < intendedDeltaX - hitEpsilon) || (intendedDeltaX < 0 && actualDx > intendedDeltaX + hitEpsilon);
		bool hitY = (intendedDeltaY > 0 && actualDy < intendedDeltaY - hitEpsilon) || (intendedDeltaY < 0 && actualDy > intendedDeltaY + hitEpsilon);
		bool hitZ = (intendedDeltaZ > 0 && actualDz < intendedDeltaZ - hitEpsilon) || (intendedDeltaZ < 0 && actualDz > intendedDeltaZ + hitEpsilon);

		// Stable "resting on ground" behavior, if we were already grounded and didn't move vertically, keep it.
		entity.OnGround = (entity.CollidedVertically && vy0 <= 0) || (entity.OnGround && Math.Abs(actualDy) < 1e-6);

		// Velocity from actual movement
		double ax = actualDx / deltaTime;
		double ay = actualDy / deltaTime;
		double az = actualDz / deltaTime;

		derailedVelocityBPS.X = hitX ? (-vx0 * physicsParameters.HorizontalRestitution) : ax;
		derailedVelocityBPS.Z = hitZ ? (-vz0 * physicsParameters.HorizontalRestitution) : az;

		// No bounce/hop. When we hit the ground while falling, kill Y velocity.
		derailedVelocityBPS.Y = (entity.OnGround && hitY && vy0 < 0) ? 0 : ay;

		// Convert impacts into extra spin
		if (hitX || hitZ)
		{
			double impact = Math.Sqrt(vx0 * vx0 + vz0 * vz0);
			int impactSign = hitX ? Math.Sign(vx0) : (hitZ ? -Math.Sign(vz0) : 1);
			if (impactSign == 0) impactSign = 1;
			derailedYawVelocityRadians += (float)(impactSign * impact * physicsParameters.ImpactToYaw);
		}

		// Horizontal drag (optionally scale by below-block DragMultiplier)
		double groundMultiplier = 1.0;
		if (entity.OnGround)
		{
			temporaryBelowPosition.Set((int)Math.Floor(entity.ServerPos.X), (int)Math.Floor(entity.ServerPos.Y - 0.05), (int)Math.Floor(entity.ServerPos.Z));
			temporaryBelowPosition.dimension = entity.ServerPos.Dimension;
			groundMultiplier = GameMath.Clamp(entity.Api.World.BlockAccessor.GetBlock(temporaryBelowPosition)?.DragMultiplier ?? 1f, 0.02f, 1f);
		}

		double drag = Math.Exp(-((entity.OnGround ? (physicsParameters.LinearDrag * groundMultiplier) : physicsParameters.AirDrag) * deltaTime));
		derailedVelocityBPS.X *= drag;
		derailedVelocityBPS.Z *= drag;

		// Stop tiny motion for perf and to avoid infinite micro-sliding.
		if (Math.Abs(derailedVelocityBPS.X) < physicsParameters.StopEpsilon) derailedVelocityBPS.X = 0;
		if (Math.Abs(derailedVelocityBPS.Z) < physicsParameters.StopEpsilon) derailedVelocityBPS.Z = 0;
		if (entity.OnGround && Math.Abs(derailedVelocityBPS.Y) < 0.01) derailedVelocityBPS.Y = 0;
	}

	private static void ServerResolveInitialTerrainOverlapUpward(Entity entity, double lateralOffsetX, double lateralOffsetZ)
	{
		const double clearanceEpsilon = 0.001;
		const double minimumLiftStep = 1.0 / 16.0;
		const double maximumLift = 2.0;
		const int maximumPasses = 8;

		IWorldAccessor world = entity.Api.World;
		var candidate = new Vec3d();
		var obstacle = new Cuboidd();
		double startX = entity.ServerPos.X;
		double startY = entity.ServerPos.Y;
		double startZ = entity.ServerPos.Z;

		bool TryResolveAt(double x, double z, out double resolvedY)
		{
			candidate.Set(x, startY, z);

			for (int resolutionPass = 0; resolutionPass < maximumPasses; resolutionPass++)
			{
				if
				(
					!world.CollisionTester.GetCollidingCollisionBox
					(
						world.BlockAccessor, entity.CollisionBox,candidate,
						ref obstacle,alsoCheckTouch: false, dimension: entity.ServerPos.Dimension
					)
				) { resolvedY = candidate.Y; return true; }

				// Put the bottom of the entity immediately above this obstacle.
				double nextY = obstacle.Y2 - entity.CollisionBox.Y1 + clearanceEpsilon;

				// Protect against precision issues or unusual collision boxes.
				if (nextY <= candidate.Y + clearanceEpsilon) nextY = candidate.Y + minimumLiftStep;
				if (nextY - startY > maximumLift) break;
				candidate.Y = nextY;
			}

			resolvedY = startY;
			return false;
		}

		bool nudgedResolved = TryResolveAt(startX, startZ, out double nudgedY);
		if (nudgedResolved && nudgedY <= startY + clearanceEpsilon) return;

		bool hasLateralOffset = Math.Abs(lateralOffsetX) + Math.Abs(lateralOffsetZ) > 1e-9;
		if (hasLateralOffset)
		{
			double fallbackX = startX - lateralOffsetX;
			double fallbackZ = startZ - lateralOffsetZ;
			bool fallbackResolved = TryResolveAt(fallbackX, fallbackZ, out double fallbackY);

			// Keep the polished lateral spread unless dropping it produces a clearly safer result with less vertical displacement.
			if (fallbackResolved && (!nudgedResolved || fallbackY + clearanceEpsilon < nudgedY))
			{
				entity.ServerPos.X = fallbackX; entity.ServerPos.Y = fallbackY; entity.ServerPos.Z = fallbackZ;
				return;
			}
		}

		if (nudgedResolved) entity.ServerPos.Y = nudgedY;
	}
	#endregion
}

#region Trans Rulez!
/// Shared physical transition rules for moving from one atomic rail edge to another at an endpoint.
/// Gauge 0 minecarts may take tight 90-degree turns.
/// Standard-gauge vehicles require smoother transitions. Neither gauge should use multi-path rail pieces as tiny in-place switchbacks.
internal static class RailTransitionRules
{
	private const double MinTransitionDotMinecart = -0.05; // Allows 90 degree plus small quantization/tangent tolerance.
	private const double MinTransitionDotStandardGauge = 0.5; // SG, same as with automation rules: >= cos(60d).
	private const double DotEpsilon = 1e-6;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal static bool IsDirectedTransitionAllowedSameGauge
	(
		RailGraphLive graph,
		RailGraphLive.EndpointKey endpoint,
		byte gauge,
		ulong fromEdgeHash,
		ulong toEdgeHash
	)
	{
		if (fromEdgeHash == 0 || toEdgeHash == 0) return false;
		if (fromEdgeHash == toEdgeHash) return false;
		if (gauge != endpoint.Gauge) return false;

		if (!graph.TryGetPolyline16(fromEdgeHash, out int[] sourceCoordinates16)) return false;
		if (!graph.TryGetPolyline16(toEdgeHash, out int[] destinationCoordinates16)) return false;

		return IsDirectedTransitionAllowedSameGauge(endpoint, gauge, sourceCoordinates16, destinationCoordinates16);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal static bool IsDirectedTransitionAllowedSameGauge
	(
		RailGraphLive.EndpointKey endpoint,
		byte gauge,
		int[] sourceCoordinates16,
		int[] destinationCoordinates16
	)
	{
		if (gauge != endpoint.Gauge) return false;

		if (!TryGetDirectedEndpointTangentXZ(sourceCoordinates16, endpoint, intoEndpoint: true, out double inX, out double inZ)) return true;
		if (!TryGetDirectedEndpointTangentXZ(destinationCoordinates16, endpoint, intoEndpoint: false, out double outX, out double outZ)) return true;

		double dot = inX * outX + inZ * outZ;
		return dot + DotEpsilon >= MinTransitionDot(gauge);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static double MinTransitionDot(byte gauge) { return gauge == 0 ? MinTransitionDotMinecart : MinTransitionDotStandardGauge; }

	private static bool TryGetDirectedEndpointTangentXZ(int[] coordinates16, RailGraphLive.EndpointKey endpoint, bool intoEndpoint, out double x, out double z)
	{
		x = 0; z = 0;

		if (coordinates16 == null || coordinates16.Length < 6) return false;

		int lastOffset = coordinates16.Length - 3;
		bool endpointAtStart = coordinates16[0] == endpoint.X16 && coordinates16[1] == endpoint.Y16 && coordinates16[2] == endpoint.Z16;
		bool endpointAtEnd = coordinates16[lastOffset + 0] == endpoint.X16 && coordinates16[lastOffset + 1] == endpoint.Y16 && coordinates16[lastOffset + 2] == endpoint.Z16;
		if (!endpointAtStart && !endpointAtEnd) return false;

		int endpointOffset = endpointAtStart ? 0 : lastOffset;
		int step = endpointAtStart ? 3 : -3;
		int endpointX = coordinates16[endpointOffset + 0];
		int endpointZ = coordinates16[endpointOffset + 2];

		for (int coordinateOffset = endpointOffset + step; coordinateOffset >= 0 && coordinateOffset + 2 < coordinates16.Length; coordinateOffset += step)
		{
			double dx = coordinates16[coordinateOffset + 0] - endpointX;
			double dz = coordinates16[coordinateOffset + 2] - endpointZ;
			double squaredLength = dx * dx + dz * dz;
			if (squaredLength <= 1e-9) continue;

			if (intoEndpoint)
			{
				dx = -dx;
				dz = -dz;
			}

			double inverseLength = 1.0 / Math.Sqrt(squaredLength);
			x = dx * inverseLength;
			z = dz * inverseLength;
			return true;
		}

		return false;
	}
}
#endregion
