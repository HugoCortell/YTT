using System;
using System.Collections.Generic;
using System.IO;
using Cairo;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace YangTransport;

[ProtoContract]
public sealed class TimetableMapRequestPacket
{
	[ProtoMember(1)] public long OwnerEntityID;
	[ProtoMember(2)] public TimetableRouteEntryPacket[] Route = Array.Empty<TimetableRouteEntryPacket>();
	[ProtoMember(3)] public int CurrentStationIndex = -1;
}

[ProtoContract]
public sealed class TimetableMapOpenPacket
{
	[ProtoMember(1)] public long OwnerEntityID;
	[ProtoMember(2)] public string ErrorText = "";
	[ProtoMember(3)] public TimetableMapLegendEntryPacket[] Legend = Array.Empty<TimetableMapLegendEntryPacket>();

	[ProtoMember(4)] public int Dimension;
	[ProtoMember(5)] public int CurrentX16;
	[ProtoMember(6)] public int CurrentZ16;
	[ProtoMember(7)] public int MinX16;
	[ProtoMember(8)] public int MinZ16;
	[ProtoMember(9)] public int MaxX16;
	[ProtoMember(10)] public int MaxZ16;

	[ProtoMember(11)] public byte[] Geometry = Array.Empty<byte>();
	[ProtoMember(12)] public int CurrentStationIndex = -1;
	[ProtoMember(13)] public bool IsWaitingAtCurrentStation;
	[ProtoMember(14)] public string CurrentStationName = "";
}

[ProtoContract]
public sealed class TimetableMapLegendEntryPacket
{
	[ProtoMember(1)] public int RouteIndex;
	[ProtoMember(2)] public string Name = "";
	[ProtoMember(3)] public bool Reachable;
}

internal static class RailStationMapBuilder
{
	private const int MaxRouteEntries	= 256;
	private const int MaxTotalPoints	= 16384;
	private const int MaxGeometryBytes	= 65536;

	internal static TimetableMapOpenPacket Build(ICoreServerAPI serverAPI, IServerPlayer player, TimetableMapRequestPacket request)
	{
		TimetableMapOpenPacket packet = new() { OwnerEntityID = request?.OwnerEntityID ?? 0 };
		if (serverAPI == null || request == null) return Error(packet, "Could not build station map!");

		TimetableRouteEntryPacket[] routeEntries = request.Route ?? Array.Empty<TimetableRouteEntryPacket>();
		if (routeEntries.Length == 0) return Error(packet, "The timetable has no stations to map out.");
		if (routeEntries.Length > MaxRouteEntries) Array.Resize(ref routeEntries, MaxRouteEntries);

		Entity owner = serverAPI.World.GetEntityById(request.OwnerEntityID);
		if (owner is not IRailwayConvoyVehicle ownerVehicle || !ownerVehicle.HasTractionEngine)
		{
			return Error(packet, "Could not build station map. Locomotive is no longer loaded.");
		}

		long headID = ownerVehicle.ConvoyHeadEntityID != 0 ? ownerVehicle.ConvoyHeadEntityID : ownerVehicle.Entity.EntityId;
		Entity headEntity = serverAPI.World.GetEntityById(headID) ?? ownerVehicle.Entity;
		if (headEntity is not IRailwayConvoyVehicle headVehicle)
		{
			return Error(packet, "Could not build station map. Convoy head is no longer loaded.");
		}

		if (!TryGetAutomationRouteStart(headVehicle.Entity, allowRouteRepair: true, out RailwayVehicleShared.RailCursor cursor))
		{
			return Error(packet, "Could not build station map. Train route is not initialized.");
		}

		RailGraphServerSystem? railSystem = serverAPI.ModLoader.GetModSystem<RailGraphServerSystem>();
		RailStationRegistrySystem? registry = serverAPI.ModLoader.GetModSystem<RailStationRegistrySystem>();
		RailAutomationPathingSystem? pathing = serverAPI.ModLoader.GetModSystem<RailAutomationPathingSystem>();
		if (railSystem == null || registry == null || pathing == null)
		{
			return Error(packet, "Could not build station map. Routing systems are unavailable.");
		}

		RailGraphLive graph = railSystem.Graph;
		if (!graph.TryGetEdgeEndpoints(cursor.SegmentHash, out RailGraphLive.EndpointKey currentA, out _, out _))
		{
			return Error(packet, "Could not build station map. Current rail edge is missing.");
		}

		if (!pathing.TryGetPathIndex(out RailPathIndex? pathIndex) || pathIndex == null)
		{
			return Error(packet, "Could not build station map. Path index is unavailable.");
		}

		int currentDimension = currentA.Dimension;
		byte gauge = cursor.Gauge;
		packet.Dimension = currentDimension;

		RouteGeometryBuilder geometry = new();
		TryGetCursorPoint(cursor, out packet.CurrentX16, out packet.CurrentZ16);
		geometry.Include(packet.CurrentX16, packet.CurrentZ16);

		ulong currentZoneID = 0;
		graph.TryGetOccupancyZoneID(cursor.SegmentHash, out currentZoneID);

		TimetableMapLegendEntryPacket[] legend = new TimetableMapLegendEntryPacket[routeEntries.Length];
		RouteStop[] stops = new RouteStop[routeEntries.Length];

		List<RailStationRegistrySystem.RailStationEntry> candidates = new(8);
		List<StationPathTarget> targets = new(8);

		for (int routeEntryIndex = 0; routeEntryIndex < routeEntries.Length; routeEntryIndex++)
		{
			TimetableRouteEntryPacket entry = Sanitize(routeEntries[routeEntryIndex]);
			string name = SafeStationName(entry);

			legend[routeEntryIndex] = new TimetableMapLegendEntryPacket { RouteIndex = routeEntryIndex, Name = name, Reachable = false };
			stops[routeEntryIndex] = new RouteStop(routeEntryIndex, name, Array.Empty<StationPathTarget>(), false);

			candidates.Clear();
			targets.Clear();

			if (!CollectStationCandidates(registry, entry, gauge, currentDimension, candidates)) continue;
			if (pathIndex.CollectStationTargets(graph, candidates, targets) <= 0) continue;

			stops[routeEntryIndex] = new RouteStop(routeEntryIndex, name, targets.ToArray(), true);
		}

		int startIndex = request.CurrentStationIndex >= 0 && request.CurrentStationIndex < routeEntries.Length ? request.CurrentStationIndex : 0;
		RailAutomationRoute? activeRouteForMap = null;
		int activeRouteIndexForMap = -1;
		bool activeRouteIsWaitingAtStation = false;

		if
		(
			pathing.TryGetActiveRouteStatus(headID, out RailAutomationRoute? activeRoute, out int activeRouteIndex, out bool activeRouteWaitingAtStation)
			&& activeRoute != null && activeRouteIndex >= 0 && activeRouteIndex < stops.Length
			&& registry.TryGet(activeRoute.Target.StationKey, out RailStationRegistrySystem.RailStationEntry activeStation)
			&& pathIndex.TryResolveStationTarget(graph, activeStation, out StationPathTarget activeLiveTarget)
			&& activeLiveTarget.SamePhysicalStop(activeRoute.Target)
		)
		{
			// A metadata-only rename does not redirect the current timetable leg.
			// Make the pinned live target visible to this map request even if the timetable's old name no longer resolves through the registry name index.
			RouteStop activeStop = stops[activeRouteIndex];
			if (!ContainsTarget(activeStop.Targets, activeLiveTarget))
			{
				StationPathTarget[] activeTargets = new StationPathTarget[activeStop.Targets.Length + 1];
				Array.Copy(activeStop.Targets, activeTargets, activeStop.Targets.Length);
				activeTargets[^1] = activeLiveTarget;
				stops[activeRouteIndex] = new RouteStop(activeStop.RouteIndex, activeStop.Name, activeTargets, true);
			}

			startIndex = activeRouteIndex;
			activeRouteForMap = activeRoute;
			activeRouteIndexForMap = activeRouteIndex;
			activeRouteIsWaitingAtStation = activeRouteWaitingAtStation;
		}

		packet.CurrentStationIndex = startIndex;
		packet.CurrentStationName = startIndex >= 0 && startIndex < stops.Length ? stops[startIndex].Name : "";
		packet.IsWaitingAtCurrentStation = activeRouteForMap != null && activeRouteIsWaitingAtStation;

		StationPathTarget previousTarget = default;
		StationPathTarget firstStationTarget = default;
		bool hasPreviousStation = false;
		bool pathChainValid = true;
		bool geometryEnabled = true;
		int previousRouteIndex = -1;
		List<RouteMapPoint> points = new(256);

		for (int step = 0; step < routeEntries.Length && pathChainValid; step++)
		{
			int routeIndex = (startIndex + step) % routeEntries.Length;
			RouteStop stop = stops[routeIndex];
			if (!stop.HasTargets) { pathChainValid = false; break; }

			RailAutomationRoute route = null!;
			bool found = false;
			bool geometryPrepared = false;
			bool hasGeometry = false;

			if (step == 0)
			{
				bool activeRouteCandidate = activeRouteForMap != null && activeRouteIndexForMap == routeIndex && ContainsTarget(stop.Targets, activeRouteForMap.Target);

				if (activeRouteCandidate && geometryEnabled)
				{
					points.Clear();
					if (AppendCurrentTrainRoute(graph, cursor, activeRouteForMap!, points))
					{
						route = activeRouteForMap!;
						found = true;
						geometryPrepared = true;
						hasGeometry = true;
					}
				}

				// Reuse is only an optimization. A missing or incompatible automation route must use the same on-demand path search as every other minimap leg.
				if (!found) { points.Clear(); found = pathIndex.TryFindRouteGreedy(graph, cursor, stop.Targets, currentZoneID, out route); }
			}
			else { found = pathIndex.TryFindRouteFromStationGreedy(graph, previousTarget, stop.Targets, currentZoneID, out route); }

			if (!found) { pathChainValid = false; break; }

			// Reachability is a pathfinding result. Geometry limits or malformed polylines should not turn a valid route into a false "(?)" legend entry.
			legend[routeIndex].Reachable = true;

			if (geometryEnabled)
			{
				if (!geometryPrepared)
				{
					points.Clear();
					hasGeometry = step == 0 ? AppendCurrentTrainRoute(graph, cursor, route, points) : AppendStationRoute(graph, previousTarget, route, points);
				}

				if (!hasGeometry) { geometryEnabled = false; }
				else
				{
					SimplifyPolyline(points);
					if (points.Count >= 2 && !geometry.TryAddLeg(previousRouteIndex, routeIndex, step == 0, false, points, MaxTotalPoints, MaxGeometryBytes))
					{
						geometryEnabled = false;
					}
				}
			}

			if (step == 0) firstStationTarget = route.Target;
			previousTarget = route.Target;
			hasPreviousStation = true;
			previousRouteIndex = routeIndex;
		}

		if (pathChainValid && geometryEnabled && hasPreviousStation && routeEntries.Length > 1)
		{
			if (pathIndex.TryFindRouteFromStationGreedy(graph, previousTarget, firstStationTarget, currentZoneID, out RailAutomationRoute loopRoute))
			{
				points.Clear();
				if (AppendStationRoute(graph, previousTarget, loopRoute, points))
				{
					SimplifyPolyline(points);
					geometry.TryAddLeg(previousRouteIndex, startIndex, false, true, points, MaxTotalPoints, MaxGeometryBytes);
				}
			}
		}

		packet.Legend = legend;
		geometry.ApplyBounds(packet);
		packet.Geometry = geometry.Encode(MaxGeometryBytes);
		return packet;
	}

	private static bool ContainsTarget(IReadOnlyList<StationPathTarget> targets, StationPathTarget target)
	{
		if (targets == null) return false;

		for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++) { if (targets[targetIndex].SamePhysicalStop(target)) return true; }
		return false;
	}

	private static bool AppendCurrentTrainRoute( RailGraphLive graph, in RailwayVehicleShared.RailCursor cursor, RailAutomationRoute route, List<RouteMapPoint> points)
	{
		if (graph == null || route == null || cursor.SegmentHash == 0) return false;
		if (!graph.TryGetEdgeEndpoints(cursor.SegmentHash, out RailGraphLive.EndpointKey cursorA, out RailGraphLive.EndpointKey cursorB, out _)) return false;

		RailGraphLive.EndpointKey cursorExit = cursor.Direction >= 0 ? cursorB : cursorA;
		ulong[] approach = route.ApproachEdges ?? Array.Empty<ulong>();

		// Try each occurrence without flattening the route.
		// Strict endpoint continuity to the station proves both that the occurrence is directed correctly
		// and that the cached route can still be reused from the current authority cursor.
		for (int approachIndex = 0; approachIndex < approach.Length; approachIndex++)
		{
			if (approach[approachIndex] != cursor.SegmentHash) continue;

			points.Clear();
			if (!AppendCursorRemainder(cursor, points)) return false;

			RailGraphLive.EndpointKey currentEndpoint = cursorExit;
			bool valid = true;

			for (int approachEdgeIndex = approachIndex + 1; approachEdgeIndex < approach.Length; approachEdgeIndex++)
			{
				if (AppendEdgeFromEndpoint(graph, approach[approachEdgeIndex], ref currentEndpoint, points)) continue;
				valid = false; break;
			}

			if (valid) valid = AppendRouteArcs(graph, route, ref currentEndpoint, points);
			if (valid && route.Start.NodeID >= 0) valid = AppendTargetTerminal(graph, route.Target, ref currentEndpoint, points);
			if (valid && currentEndpoint.Equals(route.Target.StopEndpoint))
			{
				AppendPoint(points, route.Target.StopEndpoint.X16, route.Target.StopEndpoint.Z16);
				return points.Count > 0;
			}
		}

		RailPathArc[] arcs = route.Arcs ?? Array.Empty<RailPathArc>();
		for (int currentArcIndex = 0; currentArcIndex < arcs.Length; currentArcIndex++)
		{
			ulong[] edges = arcs[currentArcIndex].Edges ?? Array.Empty<ulong>();
			for (int edgeIndex = 0; edgeIndex < edges.Length; edgeIndex++)
			{
				if (edges[edgeIndex] != cursor.SegmentHash) continue;

				points.Clear();
				if (!AppendCursorRemainder(cursor, points)) return false;

				RailGraphLive.EndpointKey currentEndpoint = cursorExit;
				bool valid = true;

				for (int next = edgeIndex + 1; next < edges.Length; next++)
				{
					if (AppendEdgeFromEndpoint(graph, edges[next], ref currentEndpoint, points)) continue;
					valid = false; break;
				}

				for (int arcIndex = currentArcIndex + 1; valid && arcIndex < arcs.Length; arcIndex++)
				{
					ulong[] laterEdges = arcs[arcIndex].Edges ?? Array.Empty<ulong>();
					for (int next = 0; next < laterEdges.Length; next++)
					{
						if (AppendEdgeFromEndpoint(graph, laterEdges[next], ref currentEndpoint, points)) continue;
						valid = false; break;
					}
				}

				if (valid && route.Start.NodeID >= 0) valid = AppendTargetTerminal(graph, route.Target, ref currentEndpoint, points);
				if (valid && currentEndpoint.Equals(route.Target.StopEndpoint))
				{
					AppendPoint(points, route.Target.StopEndpoint.X16, route.Target.StopEndpoint.Z16);
					return points.Count > 0;
				}
			}
		}

		// Once the train has entered the destination terminal corridor it is no longer present in ApproachEdges/Arcs, so continue from the matching terminal edge.
		ulong[] terminal = route.Target.TerminalEdges ?? Array.Empty<ulong>();
		for (int terminalIndex = 0; terminalIndex < terminal.Length; terminalIndex++)
		{
			if (terminal[terminalIndex] != cursor.SegmentHash) continue;

			points.Clear();
			if (!AppendCursorRemainder(cursor, points)) return false;

			RailGraphLive.EndpointKey currentEndpoint = cursorExit;
			bool valid = true;
			for (int terminalEdgeIndex = terminalIndex + 1; terminalEdgeIndex < terminal.Length; terminalEdgeIndex++)
			{
				if (AppendEdgeFromEndpoint(graph, terminal[terminalEdgeIndex], ref currentEndpoint, points)) continue;
				valid = false; break;
			}

			if (valid && currentEndpoint.Equals(route.Target.StopEndpoint))
			{
				AppendPoint(points, route.Target.StopEndpoint.X16, route.Target.StopEndpoint.Z16);
				return points.Count > 0;
			}
		}

		points.Clear();
		return false;
	}

	private static bool AppendStationRoute(RailGraphLive graph, StationPathTarget source, RailAutomationRoute route, List<RouteMapPoint> points)
	{
		if (graph == null || route == null) return false;

		RailGraphLive.EndpointKey currentEndpoint = source.StopEndpoint;
		AppendPoint(points, currentEndpoint.X16, currentEndpoint.Z16);

		ulong[] approach = route.ApproachEdges ?? Array.Empty<ulong>();
		for (int approachIndex = 0; approachIndex < approach.Length; approachIndex++)
		{
			if (!AppendEdgeFromEndpoint(graph, approach[approachIndex], ref currentEndpoint, points)) return false;
		}

		if (!AppendRouteArcs(graph, route, ref currentEndpoint, points)) return false;
		if (route.Start.NodeID >= 0 && !AppendTargetTerminal(graph, route.Target, ref currentEndpoint, points)) return false;
		if (!currentEndpoint.Equals(route.Target.StopEndpoint)) return false;

		AppendPoint(points, route.Target.StopEndpoint.X16, route.Target.StopEndpoint.Z16);
		return points.Count > 0;
	}

	private static bool AppendRouteArcs(RailGraphLive graph, RailAutomationRoute route, ref RailGraphLive.EndpointKey currentEndpoint, List<RouteMapPoint> points)
	{
		RailPathArc[] arcs = route.Arcs ?? Array.Empty<RailPathArc>();
		for (int arcIndex = 0; arcIndex < arcs.Length; arcIndex++)
		{
			ulong[] edges = arcs[arcIndex].Edges ?? Array.Empty<ulong>();
			for (int edgeIndex = 0; edgeIndex < edges.Length; edgeIndex++)
			{
				if (!AppendEdgeFromEndpoint(graph, edges[edgeIndex], ref currentEndpoint, points)) return false;
			}
		}

		return true;
	}

	private static bool AppendTargetTerminal(RailGraphLive graph, StationPathTarget target, ref RailGraphLive.EndpointKey currentEndpoint, List<RouteMapPoint> points)
	{
		if (currentEndpoint.Equals(target.StopEndpoint)) return true;

		ulong[] terminal = target.TerminalEdges ?? Array.Empty<ulong>();
		for (int terminalIndex = 0; terminalIndex < terminal.Length; terminalIndex++)
		{
			if (!AppendEdgeFromEndpoint(graph, terminal[terminalIndex], ref currentEndpoint, points)) return false;
		}

		return currentEndpoint.Equals(target.StopEndpoint);
	}

	private static bool AppendCursorRemainder(in RailwayVehicleShared.RailCursor cursor, List<RouteMapPoint> points)
	{
		int[]? polylinePoints = cursor.PolyXYZ16;
		int count = cursor.PointCount;
		if (polylinePoints == null || count < 2 || polylinePoints.Length < count * 3) return false;

		int segmentIndex = GameMath.Clamp(cursor.SegmentIndex, 0, count - 2);
		double t = GameMath.Clamp(cursor.NormalizedSegmentProgress, 0.0, 1.0);

		int x0 = polylinePoints[segmentIndex * 3];
		int z0 = polylinePoints[segmentIndex * 3 + 2];
		int x1 = polylinePoints[(segmentIndex + 1) * 3];
		int z1 = polylinePoints[(segmentIndex + 1) * 3 + 2];

		int cursorX16 = (int)Math.Round(x0 + (x1 - x0) * t);
		int cursorZ16 = (int)Math.Round(z0 + (z1 - z0) * t);
		AppendPoint(points, cursorX16, cursorZ16);

		if (cursor.Direction >= 0)
		{
			for (int pointIndex = segmentIndex + 1; pointIndex < count; pointIndex++)
			{
				AppendPoint(points, polylinePoints[pointIndex * 3], polylinePoints[pointIndex * 3 + 2]);
			}
		}
		else
		{
			for (int pointIndex = segmentIndex; pointIndex >= 0; pointIndex--)
			{
				AppendPoint(points, polylinePoints[pointIndex * 3], polylinePoints[pointIndex * 3 + 2]);
			}
		}

		return points.Count > 0;
	}

	private static bool AppendEdgeFromEndpoint(RailGraphLive graph, ulong edgeHash, ref RailGraphLive.EndpointKey currentEndpoint, List<RouteMapPoint> points)
	{
		if (edgeHash == 0) return false;
		if (!graph.TryGetEdgeEndpoints(edgeHash, out RailGraphLive.EndpointKey firstEndpoint, out RailGraphLive.EndpointKey secondEndpoint, out _)) return false;
		if (!graph.TryGetPolyline16(edgeHash, out int[] xyz16) || xyz16.Length < 6) return false;

		int count = xyz16.Length / 3;
		if (currentEndpoint.Equals(firstEndpoint))
		{
			for (int pointIndex = 0; pointIndex < count; pointIndex++) AppendPoint(points, xyz16[pointIndex * 3], xyz16[pointIndex * 3 + 2]);
			currentEndpoint = secondEndpoint;
			return true;
		}

		if (currentEndpoint.Equals(secondEndpoint))
		{
			for (int pointIndex = count - 1; pointIndex >= 0; pointIndex--) AppendPoint(points, xyz16[pointIndex * 3], xyz16[pointIndex * 3 + 2]);
			currentEndpoint = firstEndpoint;
			return true;
		}

		return false;
	}

	private static void TryGetCursorPoint(in RailwayVehicleShared.RailCursor cursor, out int x16, out int z16)
	{
		x16 = 0;
		z16 = 0;

		int[]? polylinePoints = cursor.PolyXYZ16;
		int count = cursor.PointCount;
		if (polylinePoints == null || count < 2 || polylinePoints.Length < count * 3) return;

		int segmentIndex = GameMath.Clamp(cursor.SegmentIndex, 0, count - 2);
		double t = GameMath.Clamp(cursor.NormalizedSegmentProgress, 0.0, 1.0);

		int x0 = polylinePoints[segmentIndex * 3];
		int z0 = polylinePoints[segmentIndex * 3 + 2];
		int x1 = polylinePoints[(segmentIndex + 1) * 3];
		int z1 = polylinePoints[(segmentIndex + 1) * 3 + 2];

		x16 = (int)Math.Round(x0 + (x1 - x0) * t);
		z16 = (int)Math.Round(z0 + (z1 - z0) * t);
	}

	private static void AppendPoint(List<RouteMapPoint> points, int x16, int z16)
	{
		if (points.Count > 0)
		{
			RouteMapPoint lastPoint = points[points.Count - 1];
			if (lastPoint.X16 == x16 && lastPoint.Z16 == z16) return;
		}

		points.Add(new RouteMapPoint(x16, z16));
	}

	private static void SimplifyPolyline(List<RouteMapPoint> points)
	{
		if (points.Count <= 2) return;
		const double epsilon16 = 2.0; // 1/8 block, enough to cut duplicate straight points without visibly changing rail curves. | Honestly could be less.

		int write = 1;
		for (int read = 1; read < points.Count - 1; read++)
		{
			RouteMapPoint a = points[write - 1];
			RouteMapPoint b = points[read];
			RouteMapPoint c = points[read + 1];

			double acx = c.X16 - a.X16;
			double acz = c.Z16 - a.Z16;
			double lengthSQ = acx * acx + acz * acz;
			if (lengthSQ <= 1e-18) { points[write++] = b; continue; }

			double cross = (b.X16 - a.X16) * acz - (b.Z16 - a.Z16) * acx;
			double dot = (b.X16 - a.X16) * (c.X16 - b.X16) + (b.Z16 - a.Z16) * (c.Z16 - b.Z16);

			if (cross * cross > epsilon16 * epsilon16 * lengthSQ || dot < 0) { points[write++] = b; }
		}

		points[write++] = points[points.Count - 1];
		if (write < points.Count) points.RemoveRange(write, points.Count - write);
	}

	private static bool TryGetAutomationRouteStart(Entity entity, bool allowRouteRepair, out RailwayVehicleShared.RailCursor cursor)
	{
		cursor = default;
		if (entity is EntityStandardGaugeLocomotive standardGaugeLocomotive)
		{
			return standardGaugeLocomotive.ServerTryGetAutomationRouteStart(allowRouteRepair, out cursor);
		}

		if (entity is EntityMinecart minecart)
		{
			return minecart.ServerTryGetAutomationRouteStart(allowRouteRepair, out cursor);
		}

		return false;
	}

	private static bool CollectStationCandidates
	(
		RailStationRegistrySystem registry, TimetableRouteEntryPacket entry,
		byte gauge, int dimension, List<RailStationRegistrySystem.RailStationEntry> destination
	)
	{
		destination.Clear();
		if (entry == null) return false;

		if (entry.NameHash != 0)
		{
			registry.CollectStationsByNameHash(entry.NameHash, destination, gauge, dimension);
			if (destination.Count > 0) return true;
		}

		var stationKey = new RailStationRegistrySystem.StationBlockKey(entry.X, entry.Y, entry.Z, entry.Dimension);
		if (
			registry.TryGet(stationKey, out RailStationRegistrySystem.RailStationEntry station)
			&& station.HasStopEndpoint && station.Gauge == gauge && station.Key.Dimension == dimension
		) { destination.Add(station); return true; }

		return false;
	}

	private static TimetableRouteEntryPacket Sanitize(TimetableRouteEntryPacket? entry)
	{
		if (entry == null) return new TimetableRouteEntryPacket();
		TimetableRouteEntryPacket clean = entry.Clone();
		clean.StationName = (clean.StationName ?? "").Trim();
		if (clean.StationName.Length > 512) clean.StationName = clean.StationName[..512];
		return clean;
	}

	private static string SafeStationName(TimetableRouteEntryPacket entry)
	{
		string name = (entry.StationName ?? "").Trim();
		return name.Length == 0 ? "(Unknown Station)" : name;
	}

	private static TimetableMapOpenPacket Error(TimetableMapOpenPacket packet, string message)
	{
		packet.ErrorText = message ?? "Could not build station map.";
		return packet;
	}

	private readonly record struct RouteStop(int RouteIndex, string Name, StationPathTarget[] Targets, bool HasTargets);
}

internal readonly record struct RouteMapPoint(int X16, int Z16);

internal sealed class RouteMapLeg
{
	public int FromRouteIndex;
	public int ToRouteIndex;
	public bool IsCurrentLeg;
	public bool IsLoopLeg;
	public RouteMapPoint[] Points = Array.Empty<RouteMapPoint>();
}

internal sealed class RouteGeometryBuilder
{
	private readonly List<RouteMapLeg> Legs = new(16);
	private int TotalPoints;
	private bool HasBounds;
	private int MinX16, MinZ16, MaxX16, MaxZ16;

	public void Include(int x16, int z16)
	{
		if (!HasBounds)
		{
			MinX16 = MaxX16 = x16;
			MinZ16 = MaxZ16 = z16;
			HasBounds = true;
			return;
		}

		if (x16 < MinX16) MinX16 = x16;
		if (x16 > MaxX16) MaxX16 = x16;
		if (z16 < MinZ16) MinZ16 = z16;
		if (z16 > MaxZ16) MaxZ16 = z16;
	}

	public bool TryAddLeg(int fromRouteIndex, int toRouteIndex, bool isCurrentLeg, bool isLoopLeg, List<RouteMapPoint> points, int maxTotalPoints, int maxBytes)
	{
		if (points == null || points.Count < 2) return false;
		if (TotalPoints + points.Count > maxTotalPoints) return false;
		if (16 + (TotalPoints + points.Count) * 12 + (Legs.Count + 1) * 8 > maxBytes) return false;

		for (int pointIndex = 0; pointIndex < points.Count; pointIndex++) Include(points[pointIndex].X16, points[pointIndex].Z16);

		Legs.Add(new RouteMapLeg
		{
			FromRouteIndex = fromRouteIndex, ToRouteIndex = toRouteIndex,
			IsCurrentLeg = isCurrentLeg, IsLoopLeg = isLoopLeg,
			Points = points.ToArray()
		});

		TotalPoints += points.Count;
		return true;
	}

	public void ApplyBounds(TimetableMapOpenPacket packet)
	{
		if (!HasBounds)
		{
			packet.MinX16 = packet.MaxX16 = packet.CurrentX16;
			packet.MinZ16 = packet.MaxZ16 = packet.CurrentZ16;
			return;
		}

		packet.MinX16 = MinX16; packet.MinZ16 = MinZ16; packet.MaxX16 = MaxX16; packet.MaxZ16 = MaxZ16;
	}

	public byte[] Encode(int maxBytes)
	{
		byte[] encoded = RouteGeometryCodec.Encode(Legs, HasBounds ? MinX16 : 0, HasBounds ? MinZ16 : 0);
		return encoded.Length <= maxBytes ? encoded : Array.Empty<byte>();
	}

	private int EstimateBytes() { return 16 + TotalPoints * 12 + Legs.Count * 8; } // Varints are usually much smaller.
}

internal static class RouteGeometryCodec
{
	private const byte FlagCurrentLeg = 1;
	private const byte FlagLoopLeg = 2;

	internal static byte[] Encode(IReadOnlyList<RouteMapLeg> legs, int originX16, int originZ16)
	{
		if (legs == null || legs.Count == 0) return Array.Empty<byte>();

		using MemoryStream memoryStream = new();
		WriteZigZag(memoryStream, originX16);
		WriteZigZag(memoryStream, originZ16);
		WriteVariableUnsignedInteger(memoryStream, legs.Count);

		for (int legIndex = 0; legIndex < legs.Count; legIndex++)
		{
			RouteMapLeg leg = legs[legIndex];
			RouteMapPoint[] points = leg.Points ?? Array.Empty<RouteMapPoint>();

			WriteZigZag(memoryStream, leg.FromRouteIndex);
			WriteZigZag(memoryStream, leg.ToRouteIndex);

			byte flags = 0;
			if (leg.IsCurrentLeg) flags |= FlagCurrentLeg;
			if (leg.IsLoopLeg) flags |= FlagLoopLeg;
			memoryStream.WriteByte(flags);

			WriteVariableUnsignedInteger(memoryStream, points.Length);

			int previousX16 = originX16;
			int previousZ16 = originZ16;
			for (int pointIndex = 0; pointIndex < points.Length; pointIndex++)
			{
				WriteZigZag(memoryStream, points[pointIndex].X16 - previousX16);
				WriteZigZag(memoryStream, points[pointIndex].Z16 - previousZ16);
				previousX16 = points[pointIndex].X16;
				previousZ16 = points[pointIndex].Z16;
			}
		}

		return memoryStream.ToArray();
	}

	internal static RouteMapLeg[] Decode(byte[] data)
	{
		if (data == null || data.Length == 0) return Array.Empty<RouteMapLeg>();

		int offset = 0;
		int originX16 = ReadZigZag(data, ref offset);
		int originZ16 = ReadZigZag(data, ref offset);
		int legCount = GameMath.Clamp((int)ReadVariableUnsignedInteger(data, ref offset), 0, 512);

		RouteMapLeg[] legs = new RouteMapLeg[legCount];
		for (int legIndex = 0; legIndex < legCount; legIndex++)
		{
			int fromRouteIndex = ReadZigZag(data, ref offset);
			int toRouteIndex = ReadZigZag(data, ref offset);
			byte flags = ReadByte(data, ref offset);
			int pointCount = GameMath.Clamp((int)ReadVariableUnsignedInteger(data, ref offset), 0, 32768);

			RouteMapPoint[] points = new RouteMapPoint[pointCount];
			int previousX16 = originX16;
			int previousZ16 = originZ16;

			for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
			{
				previousX16 += ReadZigZag(data, ref offset);
				previousZ16 += ReadZigZag(data, ref offset);
				points[pointIndex] = new RouteMapPoint(previousX16, previousZ16);
			}

			legs[legIndex] = new RouteMapLeg
			{
				FromRouteIndex = fromRouteIndex,
				ToRouteIndex = toRouteIndex,
				IsCurrentLeg = (flags & FlagCurrentLeg) != 0,
				IsLoopLeg = (flags & FlagLoopLeg) != 0,
				Points = points
			};
		}

		return legs;
	}

	private static void WriteZigZag(Stream stream, int value) { WriteVariableUnsignedInteger(stream, (uint)((value << 1) ^ (value >> 31))); }
	private static int ReadZigZag(byte[] data, ref int offset)
	{
		uint value = ReadVariableUnsignedInteger(data, ref offset);
		return (int)((value >> 1) ^ (uint)-(int)(value & 1));
	}

	private static void WriteVariableUnsignedInteger(Stream stream, int value) => WriteVariableUnsignedInteger(stream, (uint)value);
	private static void WriteVariableUnsignedInteger(Stream stream, uint value)
	{
		while (value >= 0x80)
		{
			stream.WriteByte((byte)(value | 0x80));
			value >>= 7;
		}

		stream.WriteByte((byte)value);
	}

	private static uint ReadVariableUnsignedInteger(byte[] data, ref int offset)
	{
		uint value = 0;
		int shift = 0;

		while (shift < 35)
		{
			byte encodedByte = ReadByte(data, ref offset);
			value |= (uint)(encodedByte & 0x7F) << shift;
			if ((encodedByte & 0x80) == 0) return value;
			shift += 7;
		}

		throw new InvalidDataException("Bad route geometry varint.");
	}

	private static byte ReadByte(byte[] data, ref int offset)
	{
		if ((uint)offset >= (uint)data.Length) throw new EndOfStreamException();
		return data[offset++];
	}
}

internal sealed class TimetableStationMapDialog : GuiDialogGeneric
{
	private const double ContentWidth = 1040;
	private const double ContentHeight = 650;
	private const double PanelPadding = 10;

	private readonly TimetableMapOpenPacket MapPacket;
	private readonly RouteMapLeg[] RouteLegs;
	private readonly List<MapLayer> LocalLayers = new(2);
	private readonly HashSet<FastVec2i> VisibleChunks = new();
	private readonly WorldMapManager? WorldMapManager;
	private readonly ChunkMapLayer? TerrainLayer;
	private readonly TimetableRouteOverlayLayer RouteLayer;
	private readonly TimetableMapSink FallbackMapSink = new();

	private TimetableRouteMapElement? MapElement;

	public override double DrawOrder => 0.24;

	public TimetableStationMapDialog(ICoreClientAPI clientAPI, TimetableMapOpenPacket mapPacket) : base(Lang.Get("yangtransport:automation-minimap-title"), clientAPI)
	{
		this.MapPacket = mapPacket ?? new TimetableMapOpenPacket();

		try { RouteLegs = RouteGeometryCodec.Decode(this.MapPacket.Geometry); }
		catch { RouteLegs = Array.Empty<RouteMapLeg>(); }

		WorldMapManager = clientAPI.ModLoader.GetModSystem<WorldMapManager>();
		if (WorldMapManager != null)
		{
			for (int layerIndex = 0; layerIndex < WorldMapManager.MapLayers.Count; layerIndex++)
			{
				if (WorldMapManager.MapLayers[layerIndex] is ChunkMapLayer chunkLayer)
				{
					TerrainLayer = chunkLayer;
					LocalLayers.Add(chunkLayer);
					break;
				}
			}
		}

		RouteLayer = new TimetableRouteOverlayLayer(clientAPI, (IWorldMapManager?)WorldMapManager ?? FallbackMapSink, this.MapPacket, RouteLegs);
		LocalLayers.Add(RouteLayer);

		Compose();
	}

	private void Compose()
	{
		double titleBarHeight = GuiStyle.TitleBarHeight;

		ElementBounds backgroundBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
		backgroundBounds.BothSizing = ElementSizing.FitToChildren;

		ElementBounds panel = ElementBounds.Fixed(0, titleBarHeight, ContentWidth, ContentHeight).WithParent(backgroundBounds);
		backgroundBounds.WithChildren(panel);

		ElementBounds mapBounds = ElementBounds.Fixed(PanelPadding, PanelPadding, panel.fixedWidth - PanelPadding * 2, panel.fixedHeight - PanelPadding * 2).WithParent(panel);
		ElementBounds legendBounds = BuildLegendBounds(panel);

		MapElement = new TimetableRouteMapElement(LocalLayers, capi, mapBounds);
		MapElement.viewChanged = OnMapViewChanged;
		MapElement.viewChangedSync = (_, _, _, _) => { };

		SingleComposer?.Dispose();
		SingleComposer = capi.Gui
			.CreateCompo("yangtransport-timetable-station-map-" + MapPacket.OwnerEntityID.ToString(), ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle))
			.AddShadedDialogBG(backgroundBounds)
			.AddDialogTitleBar(Lang.Get("yangtransport:automation-minimap-title"), () => TryClose())
			.BeginChildElements(backgroundBounds)
				.AddInset(panel, brightness: 1f)
				.AddInteractiveElement(MapElement, "routeMap")
				.AddDynamicCustomDraw(legendBounds, DrawLegend, "legend")
			.EndChildElements()
			.Compose();

		MapElement.SetRouteView(MapPacket);
	}

	private ElementBounds BuildLegendBounds(ElementBounds panel)
	{
		int count = Math.Max(1, MapPacket.Legend?.Length ?? 0);
		const double rowHeight = 18;
		const double columnWidth = 270;
		const double headerAndPadding = 48;
		double maxHeight = Math.Max(120, panel.fixedHeight - (PanelPadding + 10) * 2);

		int columns = 1;
		while (columns < 3)
		{
			int rows = (int)Math.Ceiling(count / (double)columns);
			double height = headerAndPadding + rows * rowHeight;
			if (height <= maxHeight) break;
			columns++;
		}

		int finalRows = (int)Math.Ceiling(count / (double)columns);
		double width = columns * columnWidth + 16;
		double heightFinal = headerAndPadding + finalRows * rowHeight;
		return ElementBounds.Fixed(PanelPadding + 10, PanelPadding + 10, width, heightFinal).WithParent(panel);
	}

	private void OnMapViewChanged(List<FastVec2i> nowVisible, List<FastVec2i> nowHidden)
	{
		for (int chunkIndex = 0; chunkIndex < nowVisible.Count; chunkIndex++) VisibleChunks.Add(nowVisible[chunkIndex]);
		for (int chunkIndex = 0; chunkIndex < nowHidden.Count; chunkIndex++) VisibleChunks.Remove(nowHidden[chunkIndex]);

		for (int layerIndex = 0; layerIndex < LocalLayers.Count; layerIndex++) { LocalLayers[layerIndex].OnViewChangedClient(nowVisible, nowHidden); }
	}

	public override void OnGuiClosed()
	{
		base.OnGuiClosed();

		if (TerrainLayer != null && WorldMapManager?.IsOpened != true && VisibleChunks.Count > 0)
		{
			List<FastVec2i> hidden = new(VisibleChunks.Count);
			foreach (FastVec2i chunk in VisibleChunks) hidden.Add(chunk);
			TerrainLayer.OnViewChangedClient(new List<FastVec2i>(), hidden);
			VisibleChunks.Clear();
		}

		RouteLayer.Dispose();
	}

	private void DrawLegend(Context context, ImageSurface surface, ElementBounds currentBounds)
	{
		TimetableMapLegendEntryPacket[] legend = MapPacket.Legend ?? Array.Empty<TimetableMapLegendEntryPacket>();
		if (legend.Length == 0) return;

		double width = currentBounds.InnerWidth;
		double height = currentBounds.InnerHeight;

		const double rowHeight = 18;
		const double columnWidth = 270;
		int columns = Math.Max(1, Math.Min(3, (int)Math.Floor((width - 16) / columnWidth)));
		int rows = Math.Max(1, (int)Math.Ceiling(legend.Length / (double)columns));

		double boxWidth = Math.Min(width, columns * columnWidth + 16);
		double boxHeight = Math.Min(height, 48 + rows * rowHeight);

		context.Save();
		context.Rectangle(0, 0, boxWidth, boxHeight);
		context.SetSourceRGBA(0, 0, 0, 0.55);
		context.Fill();
		context.Restore();

		DrawLabel(context, Lang.Get("yangtransport:automation-minimap-timetable"), 10, 19, CairoFont.WhiteSmallishText().WithWeight(FontWeight.Bold));

		for (int legendIndex = 0; legendIndex < legend.Length; legendIndex++)
		{
			int columnIndex = legendIndex / rows;
			int row = legendIndex % rows;
			double x = 10 + columnIndex * columnWidth;
			double y = 42 + row * rowHeight;

			TimetableMapLegendEntryPacket legendEntry = legend[legendIndex];
			string currentPrefix = legendEntry.RouteIndex == MapPacket.CurrentStationIndex ? ">>> " : "";
			string text = legendEntry.Reachable ? currentPrefix + legendEntry.Name : "(?) " + currentPrefix + legendEntry.Name;
			CairoFont font = legendEntry.Reachable
				? CairoFont.WhiteSmallText()
				: CairoFont.WhiteSmallText().WithColor(new double[] { 1.0, 0.25, 0.25, 1.0 });

			DrawLabel(context, text, x, y, font);
		}
	}

	private static void DrawLabel(Context context, string text, double x, double y, CairoFont font)
	{
		if (string.IsNullOrEmpty(text)) return;

		context.Save();
		font.SetupContext(context);
		context.MoveTo(x, y + font.GetFontExtents().Ascent);
		context.ShowText(text);
		context.Restore();
	}
}

internal sealed class TimetableRouteMapElement : GuiElementMap
{
	public TimetableRouteMapElement(List<MapLayer> mapLayers, ICoreClientAPI clientAPI, ElementBounds bounds): base(mapLayers, clientAPI, null!, bounds, snapToPlayer: false) { }

	public override void PostRenderInteractiveElements(float deltaTime)
	{
		// GuiElementMap's default post-render behavior references GuiDialogWorldMap for focus and player-follow behavior.
		// This embedded timetable map is standalone and should only move when the player drags/zooms it.
	}

	public void SetRouteView(TimetableMapOpenPacket packet)
	{
		if (packet == null) return;

		Bounds.CalcWorldBounds();

		double minX = packet.MinX16 / 16.0;
		double maxX = packet.MaxX16 / 16.0;
		double minZ = packet.MinZ16 / 16.0;
		double maxZ = packet.MaxZ16 / 16.0;

		double width = Math.Max(32.0, maxX - minX);
		double height = Math.Max(32.0, maxZ - minZ);

		float zoomX = (float)(Bounds.InnerWidth / width);
		float zoomZ = (float)(Bounds.InnerHeight / height);
		ZoomLevel = GameMath.Clamp(Math.Min(zoomX, zoomZ) * 0.82f, 0.25f, 6f);

		int centerX = (int)Math.Round((minX + maxX) * 0.5);
		int centerZ = (int)Math.Round((minZ + maxZ) * 0.5);
		CenterMapTo(new BlockPos(centerX, 0, centerZ, packet.Dimension));
		EnsureMapFullyLoaded();
	}
}

internal sealed class TimetableRouteOverlayLayer : MapLayer
{
	private readonly ICoreClientAPI ClientAPI;
	private readonly TimetableMapOpenPacket MapPacket;
	private readonly RouteMapLeg[] RouteLegs;
	private readonly Vec3d WorldPosition = new();
	private Vec2f ViewPosition = new();
	private readonly Matrixf ModelViewMatrix = new();

	private const int RouteSegmentStep16 = 16;
	private const int RouteCoverageCell16 = 8;
	private const int MarkerOverlapThreshold16 = 24;

	private readonly int CurrentRouteColor = ColorUtil.ColorFromRgba(70, 255, 110, 235);
	private readonly int RouteColor = ColorUtil.ColorFromRgba(255, 235, 70, 235);

	private readonly Vec4f WhiteColor = ColorUtil.WhiteArgbVec;
	private readonly Vec4f MarkerTint = new(1f, 1f, 1f, 1f);

	private MeshRef? RouteMesh;
	private bool RouteMeshValid;
	private MeshRef? QuadMesh;
	private LoadedTexture? StationMarkerTexture;
	private LoadedTexture? CurrentMarkerTexture;

	private readonly List<RouteMapMarker> Markers = new();
	private double LastX1 = double.NaN;
	private double LastZ1 = double.NaN;
	private double LastX2 = double.NaN;
	private double LastZ2 = double.NaN;
	private double LastRenderX = double.NaN;
	private double LastRenderY = double.NaN;
	private double LastRenderWidth = double.NaN;
	private double LastRenderHeight = double.NaN;

	public override string Title => Lang.Get("yangtransport:automation-minimap-title");
	public override string LayerGroupCode => "yangtransport-timetable-route";
	public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
	public override bool RequireChunkLoaded => false;

	public TimetableRouteOverlayLayer(ICoreClientAPI clientAPI, IWorldMapManager mapSink, TimetableMapOpenPacket mapPacket, RouteMapLeg[] routeLegs) : base(clientAPI, mapSink)
	{
		ClientAPI = clientAPI;
		this.MapPacket = mapPacket ?? new TimetableMapOpenPacket();
		this.RouteLegs = routeLegs ?? Array.Empty<RouteMapLeg>();
		ZIndex = 5;

		CreateTextures();
		BuildMarkers();
	}

	public override void Render(GuiElementMap mapElement, float deltaTime)
	{
		if (!Active || mapElement == null) return;

		EnsureRouteMesh(mapElement);
		RenderRouteMesh(mapElement);
		RenderMarkersAndLabels(mapElement);
	}

	public override void Dispose()
	{
		RouteMesh?.Dispose();
		RouteMesh = null;
		RouteMeshValid = false;

		QuadMesh?.Dispose();
		QuadMesh = null;

		StationMarkerTexture?.Dispose();
		StationMarkerTexture = null;

		CurrentMarkerTexture?.Dispose();
		CurrentMarkerTexture = null;

		for (int markerIndex = 0; markerIndex < Markers.Count; markerIndex++)
		{
			Markers[markerIndex].LabelTexture?.Dispose();
			Markers[markerIndex].LabelTexture = null;
		}

		Markers.Clear();
		base.Dispose();
	}

	private void CreateTextures()
	{
		QuadMesh = ClientAPI.Render.UploadMesh(QuadMeshUtil.GetQuad());

		int markerSize = Math.Max(16, (int)Math.Ceiling(24 * RuntimeEnv.GUIScale));
		StationMarkerTexture = CreateMarkerTexture(markerSize, new double[] { 0.95, 0.72, 0.20, 1.0 }, new double[] { 0.12, 0.08, 0.02, 1.0 });
		CurrentMarkerTexture = CreateMarkerTexture(markerSize + 4, new double[] { 0.35, 1.0, 0.48, 1.0 }, new double[] { 0.02, 0.12, 0.04, 1.0 });
	}

	private LoadedTexture CreateMarkerTexture(int size, double[] fill, double[] stroke)
	{
		ImageSurface surface = new(Format.Argb32, size, size);
		Context context = new(surface);

		double cx = size * 0.5;
		double cy = size * 0.5;
		double r = size * 0.33;

		context.SetSourceRGBA(0, 0, 0, 0);
		context.Paint();

		context.Arc(cx + size * 0.07, cy + size * 0.09, r * 1.10, 0, Math.PI * 2);
		context.SetSourceRGBA(0, 0, 0, 0.45);
		context.Fill();

		context.Arc(cx, cy, r, 0, Math.PI * 2);
		context.SetSourceRGBA(fill);
		context.FillPreserve();

		context.LineWidth = Math.Max(2.0, size * 0.10);
		context.SetSourceRGBA(stroke);
		context.Stroke();

		context.Arc(cx - r * 0.28, cy - r * 0.28, r * 0.24, 0, Math.PI * 2);
		context.SetSourceRGBA(1, 1, 1, 0.70);
		context.Fill();

		LoadedTexture texture = new(ClientAPI)
		{
			TextureId = ClientAPI.Gui.LoadCairoTexture(surface, linearMag: true),
			Width = size,
			Height = size
		};

		context.Dispose();
		surface.Dispose();

		return texture;
	}

	private void BuildMarkers()
	{
		Markers.Clear();

		TimetableMapLegendEntryPacket[] legend = MapPacket.Legend ?? Array.Empty<TimetableMapLegendEntryPacket>();
		HashSet<int> addedRouteIndices = new();
		RouteMapPoint? waitingPoint = MapPacket.IsWaitingAtCurrentStation ? FindWaitingStationPoint() : null;

		if (!MapPacket.IsWaitingAtCurrentStation)
		{
			RouteMapMarker currentMarker = new()
			{
				Point = new RouteMapPoint(MapPacket.CurrentX16, MapPacket.CurrentZ16),
				IsCurrentLocation = true,
				Label = Lang.Get("yangtransport:automation-minimap-youarehere")
			};
			currentMarker.LabelTexture = CreateLabelTexture(currentMarker.Label, isCurrentLocation: true);
			Markers.Add(currentMarker);
		}

		for (int legIndex = 0; legIndex < RouteLegs.Length; legIndex++)
		{
			RouteMapLeg leg = RouteLegs[legIndex];
			if (leg.IsLoopLeg || leg.ToRouteIndex < 0 || !addedRouteIndices.Add(leg.ToRouteIndex)) continue;

			RouteMapPoint[] points = leg.Points ?? Array.Empty<RouteMapPoint>();
			if (points.Length == 0) continue;

			RouteMapPoint endPoint = points[points.Length - 1];
			if (MapPacket.IsWaitingAtCurrentStation)
			{
				if (leg.ToRouteIndex == MapPacket.CurrentStationIndex) continue;
				if (waitingPoint.HasValue && SameMarkerSpot(endPoint, waitingPoint.Value)) continue;
			}

			string label = GetStationName(legend, leg.ToRouteIndex);
			if (string.IsNullOrWhiteSpace(label)) label = "Unknown Station";

			RouteMapMarker marker = new()
			{
				Point = endPoint,
				RouteIndex = leg.ToRouteIndex,
				IsCurrentLocation = false,
				Label = label
			};

			marker.LabelTexture = CreateLabelTexture(label, isCurrentLocation: false);
			Markers.Add(marker);
		}

		if (MapPacket.IsWaitingAtCurrentStation)
		{
			string label = GetCurrentStationName(legend);
			if (string.IsNullOrWhiteSpace(label)) label = "Unknown Station";
			label = AppendCurrentLocationSuffix(label);

			RouteMapMarker marker = new()
			{
				Point = waitingPoint ?? new RouteMapPoint(MapPacket.CurrentX16, MapPacket.CurrentZ16),
				RouteIndex = MapPacket.CurrentStationIndex,
				IsCurrentLocation = true,
				Label = label
			};

			marker.LabelTexture = CreateLabelTexture(label, isCurrentLocation: true);
			Markers.Add(marker);
		}
	}

	private RouteMapPoint FindWaitingStationPoint()
	{
		for (int legIndex = 0; legIndex < RouteLegs.Length; legIndex++)
		{
			RouteMapLeg leg = RouteLegs[legIndex];
			if (leg.ToRouteIndex != MapPacket.CurrentStationIndex) continue;

			RouteMapPoint[] points = leg.Points ?? Array.Empty<RouteMapPoint>();
			if (points.Length > 0) return points[points.Length - 1];
		}

		return new RouteMapPoint(MapPacket.CurrentX16, MapPacket.CurrentZ16);
	}

	private string GetCurrentStationName(TimetableMapLegendEntryPacket[] legend)
	{
		string name = (MapPacket.CurrentStationName ?? "").Trim();
		return name.Length > 0 ? name : GetStationName(legend, MapPacket.CurrentStationIndex);
	}

	private static bool SameMarkerSpot(RouteMapPoint firstPoint, RouteMapPoint secondPoint)
	{
		int dx = firstPoint.X16 - secondPoint.X16;
		int dz = firstPoint.Z16 - secondPoint.Z16;
		return dx * dx + dz * dz <= MarkerOverlapThreshold16 * MarkerOverlapThreshold16;
	}

	private static string AppendCurrentLocationSuffix(string label)
	{
		string currentLocationText = "(" + Lang.Get("yangtransport:automation-minimap-youarehere") + ")";
		return label.Contains(currentLocationText, StringComparison.Ordinal) ? label : label + " " + currentLocationText;
	}

	private LoadedTexture CreateLabelTexture(string label, bool isCurrentLocation)
	{
		return ClientAPI.Gui.TextTexture.GenTextTexture
		(
			label,
			CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold),
			new TextBackground
			{
				FillColor = new double[] { 0, 0, 0, 0.58 },
				BorderColor = isCurrentLocation ? new double[] { 0.35, 1.0, 0.48, 0.85 } : new double[] { 0.95, 0.72, 0.20, 0.80 },
				BorderWidth = 1,
				Radius = 4,
				Padding = 4
			}
		);
	}

	private static string GetStationName(TimetableMapLegendEntryPacket[] legend, int routeIndex)
	{
		for (int legendIndex = 0; legendIndex < legend.Length; legendIndex++)
		{
			if (legend[legendIndex].RouteIndex == routeIndex) return legend[legendIndex].Name ?? "";
		}

		return "";
	}

	private void EnsureRouteMesh(GuiElementMap mapElement)
	{
		if (!ViewChanged(mapElement) && RouteMeshValid) return;

		RouteMesh?.Dispose();
		RouteMesh = null;
		RouteMeshValid = false;

		MeshData mesh = BuildRouteMesh(mapElement);
		if (mesh.VerticesCount > 0 && mesh.IndicesCount > 0) { RouteMesh = ClientAPI.Render.UploadMesh(mesh); }

		RouteMeshValid = true;
		SaveViewState(mapElement);
	}

	private bool ViewChanged(GuiElementMap mapElement)
	{
		return !RouteMeshValid
			|| Math.Abs(LastX1 - mapElement.CurrentBlockViewBounds.X1) > 0.001
			|| Math.Abs(LastZ1 - mapElement.CurrentBlockViewBounds.Z1) > 0.001
			|| Math.Abs(LastX2 - mapElement.CurrentBlockViewBounds.X2) > 0.001
			|| Math.Abs(LastZ2 - mapElement.CurrentBlockViewBounds.Z2) > 0.001
			|| Math.Abs(LastRenderX - mapElement.Bounds.renderX) > 0.001
			|| Math.Abs(LastRenderY - mapElement.Bounds.renderY) > 0.001
			|| Math.Abs(LastRenderWidth - mapElement.Bounds.InnerWidth) > 0.001
			|| Math.Abs(LastRenderHeight - mapElement.Bounds.InnerHeight) > 0.001;
	}

	private void SaveViewState(GuiElementMap mapElement)
	{
		LastX1 = mapElement.CurrentBlockViewBounds.X1;
		LastZ1 = mapElement.CurrentBlockViewBounds.Z1;
		LastX2 = mapElement.CurrentBlockViewBounds.X2;
		LastZ2 = mapElement.CurrentBlockViewBounds.Z2;
		LastRenderX = mapElement.Bounds.renderX;
		LastRenderY = mapElement.Bounds.renderY;
		LastRenderWidth = mapElement.Bounds.InnerWidth;
		LastRenderHeight = mapElement.Bounds.InnerHeight;
	}

	private MeshData BuildRouteMesh(GuiElementMap mapElement)
	{
		int segmentCapacity = CountRenderPieces();
		// Keep the UV buffer allocated even though this mesh is untextured.
		// UploadMesh binds attributes sequentially, while the GUI shader expects RGBA after the UV slot when applyColor is enabled.
		MeshData mesh = new(Math.Max(4, segmentCapacity * 4), Math.Max(6, segmentCapacity * 6), withNormals: false, withUv: true, withRgba: true, withFlags: false);
		mesh.SetMode(EnumDrawMode.Triangles);

		RoutePolylineCoverage coverage = new();
		List<RouteRenderSegment> currentSegments = new(256);

		for (int legIndex = 0; legIndex < RouteLegs.Length; legIndex++)
		{
			RouteMapLeg leg = RouteLegs[legIndex];
			if (!leg.IsCurrentLeg) continue;
			CollectLegSegments(leg, CurrentRouteColor, 7.0f, coverage, currentSegments);
		}

		for (int legIndex = 0; legIndex < RouteLegs.Length; legIndex++)
		{
			RouteMapLeg leg = RouteLegs[legIndex];
			if (leg.IsCurrentLeg) continue;
			AddLegSegmentsToMesh(mesh, mapElement, leg, RouteColor, 5.5f, coverage);
		}

		// Append the green current path last so it visually wins even where a later route crosses it.
		for (int segmentIndex = 0; segmentIndex < currentSegments.Count; segmentIndex++) { AddSegmentToMesh(mesh, mapElement, currentSegments[segmentIndex]); }

		return mesh;
	}

	private int CountRenderPieces()
	{
		int count = 0;
		for (int legIndex = 0; legIndex < RouteLegs.Length; legIndex++)
		{
			RouteMapPoint[] points = RouteLegs[legIndex].Points ?? Array.Empty<RouteMapPoint>();
			for (int pointIndex = 1; pointIndex < points.Length; pointIndex++) { count += CountPieces(points[pointIndex - 1], points[pointIndex]); }
		}

		return count;
	}

	private static int CountPieces(RouteMapPoint from, RouteMapPoint to)
	{
		double dx = to.X16 - from.X16;
		double dz = to.Z16 - from.Z16;
		double distance = Math.Sqrt(dx * dx + dz * dz);
		return Math.Max(1, (int)Math.Ceiling(distance / RouteSegmentStep16));
	}

	private static RouteMapPoint Interpolate(RouteMapPoint from, RouteMapPoint to, int step, int steps)
	{
		if (step <= 0) return from;
		if (step >= steps) return to;

		double t = step / (double)steps;
		return new RouteMapPoint
		(
			(int)Math.Round(from.X16 + (to.X16 - from.X16) * t),
			(int)Math.Round(from.Z16 + (to.Z16 - from.Z16) * t)
		);
	}

	private static bool IsDegenerate(RouteMapPoint from, RouteMapPoint to)
	{
		return from.X16 == to.X16 && from.Z16 == to.Z16;
	}

	private static void CollectLegSegments(RouteMapLeg leg, int color, float width, RoutePolylineCoverage coverage, List<RouteRenderSegment> destination)
	{
		RouteMapPoint[] points = leg.Points ?? Array.Empty<RouteMapPoint>();
		if (points.Length < 2) return;

		for (int pointIndex = 1; pointIndex < points.Length; pointIndex++)
		{
			RouteMapPoint from = points[pointIndex - 1];
			RouteMapPoint to = points[pointIndex];
			int steps = CountPieces(from, to);

			for (int stepIndex = 0; stepIndex < steps; stepIndex++)
			{
				RouteMapPoint segmentStart = Interpolate(from, to, stepIndex, steps);
				RouteMapPoint segmentEnd = Interpolate(from, to, stepIndex + 1, steps);
				if (IsDegenerate(segmentStart, segmentEnd)) continue;
				if (coverage.TryReserve(segmentStart, segmentEnd)) destination.Add(new RouteRenderSegment(segmentStart, segmentEnd, width, color));
			}
		}
	}

	private void AddLegSegmentsToMesh(MeshData mesh, GuiElementMap mapElement, RouteMapLeg leg, int color, float width, RoutePolylineCoverage coverage)
	{
		RouteMapPoint[] points = leg.Points ?? Array.Empty<RouteMapPoint>();
		if (points.Length < 2) return;

		for (int pointIndex = 1; pointIndex < points.Length; pointIndex++)
		{
			RouteMapPoint from = points[pointIndex - 1];
			RouteMapPoint to = points[pointIndex];
			int steps = CountPieces(from, to);

			for (int stepIndex = 0; stepIndex < steps; stepIndex++)
			{
				RouteMapPoint segmentStart = Interpolate(from, to, stepIndex, steps);
				RouteMapPoint segmentEnd = Interpolate(from, to, stepIndex + 1, steps);
				if (IsDegenerate(segmentStart, segmentEnd)) continue;
				if (coverage.TryReserve(segmentStart, segmentEnd)) AddSegmentToMesh(mesh, mapElement, new RouteRenderSegment(segmentStart, segmentEnd, width, color));
			}
		}
	}

	private void AddSegmentToMesh(MeshData mesh, GuiElementMap mapElement, RouteRenderSegment segment)
	{
		Project(mapElement, segment.From, out float x0, out float y0);
		Project(mapElement, segment.To, out float x1, out float y1);
		AddSegmentQuad(mesh, x0, y0, x1, y1, segment.Width, segment.Color);
	}

	private static void AddSegmentQuad(MeshData mesh, float x0, float y0, float x1, float y1, float width, int color)
	{
		float dx = x1 - x0;
		float dy = y1 - y0;
		float length = (float)Math.Sqrt(dx * dx + dy * dy);
		if (length < 0.001f) return;

		float half = width * 0.5f;
		float nx = -dy / length * half;
		float ny = dx / length * half;
		float ex = dx / length * half;
		float ey = dy / length * half;

		int start = mesh.VerticesCount;

		mesh.AddVertexSkipTex(x0 - ex + nx, y0 - ey + ny, 78f, color);
		mesh.AddVertexSkipTex(x0 - ex - nx, y0 - ey - ny, 78f, color);
		mesh.AddVertexSkipTex(x1 + ex - nx, y1 + ey - ny, 78f, color);
		mesh.AddVertexSkipTex(x1 + ex + nx, y1 + ey + ny, 78f, color);
		mesh.AddQuadIndices(start);
	}

	private void RenderRouteMesh(GuiElementMap mapElement)
	{
		if (RouteMesh == null) return;

		ClientAPI.Render.GlToggleBlend(blend: true);

		IShaderProgram shaderProgram = ClientAPI.Render.GetEngineShader(EnumShaderProgram.Gui);
		shaderProgram.UniformMatrix("projectionMatrix", ClientAPI.Render.CurrentProjectionMatrix);
		shaderProgram.UniformMatrix("modelViewMatrix", ClientAPI.Render.CurrentModelviewMatrix);
		shaderProgram.Uniform("rgbaIn", WhiteColor);
		shaderProgram.Uniform("applyColor", 1);
		shaderProgram.Uniform("extraGlow", 0);
		shaderProgram.Uniform("noTexture", 1f);

		ClientAPI.Render.RenderMesh(RouteMesh);
	}

	private void RenderMarkersAndLabels(GuiElementMap mapElement)
	{
		if (QuadMesh == null) return;

		ClientAPI.Render.GlToggleBlend(blend: true);

		IShaderProgram shaderProgram = ClientAPI.Render.GetEngineShader(EnumShaderProgram.Gui);
		shaderProgram.UniformMatrix("projectionMatrix", ClientAPI.Render.CurrentProjectionMatrix);
		shaderProgram.Uniform("applyColor", 0);
		shaderProgram.Uniform("extraGlow", 0);
		shaderProgram.Uniform("noTexture", 0f);
		shaderProgram.Uniform("rgbaIn", MarkerTint);

		for (int markerIndex = 0; markerIndex < Markers.Count; markerIndex++)
		{
			RouteMapMarker marker = Markers[markerIndex];
			Project(mapElement, marker.Point, out float x, out float y);

			if
			(
				x < mapElement.Bounds.renderX - 64f || y < mapElement.Bounds.renderY - 64f ||
				x > mapElement.Bounds.renderX + mapElement.Bounds.InnerWidth + 64f ||
				y > mapElement.Bounds.renderY + mapElement.Bounds.InnerHeight + 64f
			) continue;

			LoadedTexture? markerTexture = marker.IsCurrentLocation ? CurrentMarkerTexture : StationMarkerTexture;
			if (markerTexture != null)
			{
				shaderProgram.BindTexture2D("tex2d", markerTexture.TextureId, 0);
				ModelViewMatrix.Set(ClientAPI.Render.CurrentModelviewMatrix)
					.Translate(x, y, 86f)
					.Scale(markerTexture.Width * 0.5f, markerTexture.Height * 0.5f, 0f);
				shaderProgram.UniformMatrix("modelViewMatrix", ModelViewMatrix.Values);
				ClientAPI.Render.RenderMesh(QuadMesh);
			}

			if (marker.LabelTexture != null)
			{
				LoadedTexture labelTexture = marker.LabelTexture;
				shaderProgram.BindTexture2D("tex2d", labelTexture.TextureId, 0);

				float labelOffset = marker.IsCurrentLocation ? 16f : 14f;
				ModelViewMatrix.Set(ClientAPI.Render.CurrentModelviewMatrix)
					.Translate(x + labelOffset * RuntimeEnv.GUIScale + labelTexture.Width * 0.5f, y - labelTexture.Height * 0.5f, 87f)
					.Scale(labelTexture.Width * 0.5f, labelTexture.Height * 0.5f, 0f);

				shaderProgram.UniformMatrix("modelViewMatrix", ModelViewMatrix.Values);
				ClientAPI.Render.RenderMesh(QuadMesh);
			}
		}
	}

	private void Project(GuiElementMap mapElement, RouteMapPoint point, out float x, out float y)
	{
		WorldPosition.Set(point.X16 / 16.0, 0, point.Z16 / 16.0);
		mapElement.TranslateWorldPosToViewPos(WorldPosition, ref ViewPosition);

		x = (float)(mapElement.Bounds.renderX + ViewPosition.X);
		y = (float)(mapElement.Bounds.renderY + ViewPosition.Y);
	}

	private readonly record struct RouteRenderSegment(RouteMapPoint From, RouteMapPoint To, float Width, int Color);

	private readonly record struct RouteSegmentKey(int MiddleX, int MiddleZ, int DirectionBucket);

	private sealed class RoutePolylineCoverage
	{
		private readonly HashSet<RouteSegmentKey> OccupiedSegments = new();

		public bool TryReserve(RouteMapPoint from, RouteMapPoint to)
		{
			if (from.X16 == to.X16 && from.Z16 == to.Z16) return false;
			return OccupiedSegments.Add(MakeKey(from, to));
		}

		private static RouteSegmentKey MakeKey(RouteMapPoint from, RouteMapPoint to)
		{
			int middleX = Quantize((from.X16 + to.X16) * 0.5);
			int middleZ = Quantize((from.Z16 + to.Z16) * 0.5);

			double dx = to.X16 - from.X16;
			double dz = to.Z16 - from.Z16;
			double angle = Math.Atan2(dz, dx);

			// Direction is an orientation, not travel direction, so opposite-way travel over the same rail maps to the same key.
			if (angle < 0) angle += Math.PI;
			if (angle >= Math.PI) angle -= Math.PI;

			int directionBin = ((int)Math.Round(angle / (Math.PI / 16.0))) & 15;
			return new RouteSegmentKey(middleX, middleZ, directionBin);
		}

		private static int Quantize(double value) { return (int)Math.Round(value / RouteCoverageCell16); }
	}

	private sealed class RouteMapMarker
	{
		public RouteMapPoint Point;
		public int RouteIndex = -1;
		public bool IsCurrentLocation;
		public string Label = "";
		public LoadedTexture? LabelTexture;
	}
}

internal sealed class TimetableMapSink : IWorldMapManager
{
	public bool IsShuttingDown => false;
	public bool IsOpened => true;

	public void TranslateWorldPosToViewPos(Vec3d worldPos, ref Vec2f viewPos) { viewPos.X = 0; viewPos.Y = 0; }

	public void SendMapDataToClient(MapLayer forMapLayer, IServerPlayer forPlayer, byte[] data) { }
	public void SendMapDataToServer(MapLayer forMapLayer, byte[] data) { }
}
