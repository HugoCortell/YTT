using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace YangTransport;

/// Small internal hooks used by the generic offscreen rail convoy simulator.
/// Kept separate so the loaded SG movement/render path stays focused and hot-path direct.
public sealed partial class EntityStandardGaugeLocomotive
{
	internal void OfflineSimPersistNow() { PersistTrackState(); }

	internal void OfflineSimGetRollingResistParams(out double rollingResistanceConstant, out double speedDependentRollingResistance, out double stopEpsilon)
	{
		rollingResistanceConstant = DriveParameters.BaseResistance;
		speedDependentRollingResistance = DriveParameters.WeightResistance;
		stopEpsilon = DriveParameters.StopEpsilon;
	}

	/// Prefer exact-edge refresh by stored segHash. Only fall back to nearest-edge binding when that edge no longer exists.
	/// This avoids random direction/edge flips on loops and junctions during offscreen materialization.
	private bool OfflineSimulationEnsureBoundFromStoredCursor()
	{
		if (Api?.Side != EnumAppSide.Server) return false;

		RailSystem ??= Api.ModLoader.GetModSystem<RailGraphServerSystem>();
		if (RailSystem == null) return false;

		int graphVersion = RailSystem.GraphBuildVersion;

		Cursor.Gauge = TrackGauge;

		bool bindingSucceeded;
		if (Cursor.SegmentHash != 0)
		{
			bindingSucceeded = RailwayVehicleShared.TryRefreshPolyline(RailSystem.Graph, ref Cursor);
			if (!bindingSucceeded)
			{
				bindingSucceeded = RailwayVehicleShared.TryEnsureBound
				(
					RailSystem,
					Pos.AsBlockPos,
					ServerPos.XYZ,
					Pos.AsBlockPos.dimension,
					forceRebind: true,
					BindRadiusBlocks,
					ref Cursor,
					BindCandidates
				);
			}
		}
		else
		{
			bindingSucceeded = RailwayVehicleShared.TryEnsureBound
			(
				RailSystem,
				Pos.AsBlockPos,
				ServerPos.XYZ,
				Pos.AsBlockPos.dimension,
				forceRebind: true,
				BindRadiusBlocks,
				ref Cursor,
				BindCandidates
			);
		}

		if (bindingSucceeded) Cursor.BoundGraphVersion = graphVersion;
		return bindingSucceeded;
	}

	/// Installs the already-authoritative virtual route during materialization. This avoids an unnecessary nearest-edge rebuild and preserves the exact switch path.
	internal bool OfflineSimImportPathTape(ConvoyRoute pathTape, double pathHeadDistance)
	{
		if (Api?.Side != EnumAppSide.Server || pathTape == null || Derailed || IsFollower) return false;

		RailSystem ??= Api.ModLoader.GetModSystem<RailGraphServerSystem>();
		ConvoySystem ??= Api.ModLoader.GetModSystem<RailConvoySystem>();
		if (RailSystem == null || !pathTape.ValidateAuthoritative(RailSystem.Graph, TrackGauge)) return false;

		PathTape = pathTape.Clone();
		PathHeadDistance = pathHeadDistance;
		CollisionTrail.Clear();

		if (!ServerApplyPathTapeToLoadedConvoyMembers()) return false;

		PersistTrackState();
		return true;
	}

	internal void OfflineSimApplyTrackState
	(
		ulong segmentHash, int segmentIndex, double normalizedSegmentProgress, int direction, double speed,
		double absoluteDistanceTravelled, bool resetHistory, SGTrainEnd? leadEndOverride = null
	)
	{
		Derailed = false;
		Attributes.SetBool(DerailedAttribute, false);
		if (Api?.Side == EnumAppSide.Server)
		{
			WatchedAttributes.SetBool(DerailedAttribute, false);
			WatchedAttributes.MarkPathDirty(DerailedAttribute);
		}

		Cursor.Gauge = TrackGauge;
		Cursor.SegmentHash = segmentHash;
		Cursor.SegmentIndex = segmentIndex;
		Cursor.NormalizedSegmentProgress = GameMath.Clamp(normalizedSegmentProgress, 0.0, 1.0);
		Cursor.Direction = direction >= 0 ? 1 : -1;

		Speed = Math.Abs(speed);
		LeadEnd = leadEndOverride ?? (speed < 0 ? SGTrainEnd.EndB : SGTrainEnd.EndA);
		TravelledABS = absoluteDistanceTravelled;
		CollisionTrail.Clear();

		Cursor.BoundGraphVersion = 0;
		Cursor.PolyXYZ16 = null;
		Cursor.PointCount = 0;
		Cursor.OrientationCacheSegmentHash = 0;
		Cursor.OrientationCacheSegmentIndex = -1;
		Cursor.OrientationCacheDirection = 0;

		PersistTrackState();
		if (Api?.Side == EnumAppSide.Server)
		{
			WatchedAttributes.SetInt(SGLeadEndAttribute, (int)LeadEnd);
			WatchedAttributes.MarkPathDirty(SGLeadEndAttribute);
		}

		if (Api?.Side != EnumAppSide.Server) return;

		OfflineSimulationEnsureBoundFromStoredCursor();
		WriteWorldPosFromTrack();
	}
}
