using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace YangTransport;

/// Small internal hooks used by the offscreen / unloaded minecart simulation. Kept in a partial to avoid further bloating the main minecart file.
public sealed partial class EntityMinecart
{
	/// Force persisted Attributes ("segHash", "speed", etc.) to match runtime fields right now.
	internal void OfflineSimulationPersistNow() { PersistTrackState(); }

	internal void OfflineSimGetRollingResistanceParameters(out double baseRollingResistance, out double rollingResistancePerWeight, out double stopEpsilon)
	{
		baseRollingResistance = DriveParameters.BaseResistance;
		rollingResistancePerWeight = DriveParameters.WeightResistance;
		stopEpsilon = DriveParameters.StopEpsilon;
	}

	/// Ensure we have a valid Cursor.PolyXYZ16 for the currently persisted Cursor.SegHash.
	/// IMPORTANT: In offscreen workflows we must avoid re-binding to the *nearest* edge whenever possible,
	/// because on loops/junctions multiple edges can be at equal distance and the choice can randomly flip,
	/// which in turn flips Cursor.Dir/travel direction on re-materialize.
	private bool OfflineSimEnsureBoundFromStoredCursor()
	{
		if (Api?.Side != EnumAppSide.Server) return false;
		if (RailSystem == null) return false;

		int graphBuildVersion = RailSystem.GraphBuildVersion;

		bool bindingSucceeded;
		if (Cursor.SegmentHash != 0)
		{
			// Prefer exact-edge refresh by hash (stable). Only fall back if the edge no longer exists.
			bindingSucceeded = TryRefreshPolyline(RailSystem);
			if (!bindingSucceeded) bindingSucceeded = TryBindNearestEdge(RailSystem);
		}
		else { bindingSucceeded = TryBindNearestEdge(RailSystem); }

		if (bindingSucceeded) Cursor.BoundGraphVersion = graphBuildVersion;
		return bindingSucceeded;
	}

	/// When we spawn from bytes in the offscreen system, we must call Initialize() first (to get Api/World),
	/// and only then call FromBytes(). EntityMinecart normally reads persistent state during Initialize(), so we need to re-apply it after FromBytes().
	internal void OfflineSimReapplyPersistedStateAfterFromBytes(bool bindAndPlace = true)
	{
		// Convoy + tether state
		ConvoyHeadID = Attributes.GetLong(ConvoyHeadIDAttributeKey, 0);
		ConvoyIndex = Attributes.GetInt(ConvoyIndexAttributeKey, 0);
		PreviousCartIDReadOnly = Attributes.GetLong(PreviousCartIDAttributeKey, 0);

		// Track + motion
		Speed = Attributes.GetDouble("speed", 0.0);
		TravelledABS = Attributes.GetDouble("travelledAbs", 0.0);
		RailRootDistance = Attributes.GetDouble("railRootS", Math.Max(RailTapeSeedBaseDistance, TravelledABS));
		RailTape?.Clear();

		Cursor.SegmentHash = unchecked((ulong)Attributes.GetLong("segHash", 0));
		Cursor.SegmentIndex = Attributes.GetInt("segIndex", 0);
		Cursor.NormalizedSegmentProgress = Attributes.GetDouble("segT01", 0.5);
		Cursor.Direction = Attributes.GetInt("dir", 1) >= 0 ? 1 : -1;
		CollisionTrail.Clear();

		Derailed = Attributes.GetBool(DerailedAttributeKey, false);
		if (Derailed)
		{
			// Mirror the same safety cleanup as Initialize().
			Cursor.SegmentHash = 0;
			Cursor.PolyXYZ16 = null;
			Cursor.PointCount = 0;
			Cursor.BoundGraphVersion = 0;
			Speed = 0;
			DerailedVelocityBPS.Set(0, 0, 0);
			DerailedYawVelocityRadians = 0f;
			ServerPos.Roll = 0f;
			ServerPos.Pitch = 0f;

			if (Api?.Side == EnumAppSide.Server)
			{
				WatchedAttributes.SetBool(DerailedAttributeKey, true);
				WatchedAttributes.MarkPathDirty(DerailedAttributeKey);
			}

			return;
		}

		// Ensure we are bound and positioned.
		if (bindAndPlace && Api?.Side == EnumAppSide.Server && Cursor.SegmentHash != 0)
		{
			// Refresh polyline/speed limits for the *persisted* Cursor.SegHash. Do NOT re-bind to the nearest edge here (can randomly flip on loops/junctions).
			Cursor.BoundGraphVersion = 0;
			Cursor.PolyXYZ16 = null;
			Cursor.PointCount = 0;
			OfflineSimEnsureBoundFromStoredCursor();
			WriteWorldPosFromTrack();
		}
	}

	/// Installs the already-authoritative virtual route during materialization. Avoids rebuilding the tape from nearest-edge binds, which is ambiguous at switches.
	internal bool OfflineSimulationImportRailRoute(ConvoyRoute railRoute, double rootDistance)
	{
		if (Api?.Side != EnumAppSide.Server || railRoute == null || Derailed || IsFollower) return false;

		RailSystem ??= Api.ModLoader.GetModSystem<RailGraphServerSystem>();
		ConvoySystem ??= Api.ModLoader.GetModSystem<RailConvoySystem>();
		if (RailSystem == null || !railRoute.ValidateAuthoritative(RailSystem.Graph, TrackGauge)) return false;

		RailTape = railRoute.Clone();
		RailRootDistance = rootDistance;
		CollisionTrail.Clear();

		if (!ServerApplyRailTapeToLoadedConvoyMembers()) return false;

		PersistTrackState();
		RailSystem.UpdateLoadedRouteInterest(EntityId, RailTape);
		return true;
	}

	/// Apply a new rail cursor + motion state (used when materializing an offscreen convoy). This mutates runtime fields, Attributes, and the current ServerPos.
	internal void OfflineSimApplyTrackState(ulong segmentHash, int segmentIndex, double segmentInterpolation, int direction, double speed, double travelledABS, bool resetHistory)
	{
		// Force "on-rails" mode.
		Derailed = false;
		Attributes.SetBool(DerailedAttributeKey, false);
		if (Api?.Side == EnumAppSide.Server)
		{
			WatchedAttributes.SetBool(DerailedAttributeKey, false);
			WatchedAttributes.MarkPathDirty(DerailedAttributeKey);
		}

		Cursor.SegmentHash = segmentHash;
		Cursor.SegmentIndex = segmentIndex;
		Cursor.NormalizedSegmentProgress = segmentInterpolation;
		Cursor.Direction = direction >= 0 ? 1 : -1;

		Speed = speed;
		TravelledABS = travelledABS;
		RailRootDistance = Math.Max(RailTapeSeedBaseDistance, travelledABS);
		RailTape?.Clear();
		CollisionTrail.Clear();

		// Force a rebind (polylines/speed caps) and write position.
		Cursor.BoundGraphVersion = 0;
		Cursor.PolyXYZ16 = null;
		Cursor.PointCount = 0;

		PersistTrackState();
		if (Api?.Side == EnumAppSide.Server)
		{
			OfflineSimEnsureBoundFromStoredCursor();
			WriteWorldPosFromTrack();
		}
	}


}
