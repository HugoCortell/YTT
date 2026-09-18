using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;

namespace YangTransport;

internal delegate bool RailRouteAcceptance(RailAutomationRoute route);

#region Automation Path Index
/// Compressed, directed, runtime-only graph used by conductor-locust automation.
/// Built lazily from rail topology only. Stations are query-local virtual terminals and never invalidate this index.
internal sealed class RailPathIndex
{
	private readonly Dictionary<RailGraphLive.EndpointKey, int> NodeByEndpoint;
	private readonly List<RailPathNode> Nodes;
	private readonly List<RailPathArc> Arcs;
	private readonly List<RailPathArc>[] ArcsByNode;
	private readonly HashSet<DirectedTransitionKey> BlockedDirectedTransitions;
	private readonly HashSet<ulong>? IndexedEdgeHashes;
	private readonly HashSet<RailGraphLive.EndpointKey>? IndexedEndpoints;
	
	// Searches are serialized on the server thread. Reusing these workspaces removes the largest per-route allocation bursts without sharing any result-owned collections.
	private readonly BasicSearchWorkspace BasicSearchWorkspaceCache = new();
	private readonly AcceptedSearchWorkspace AcceptedSearchWorkspaceCache = new();
	private readonly List<ulong> ApproachEdgeScratch = new(16);
	private readonly StationPathTarget[] SingleTargetScratch = new StationPathTarget[1];
	private readonly Dictionary<StationTargetCacheKey, StationTargetGeometry> StationTargetCache = new();
	private const double CostEpsilon = 1e-7;

	private RailPathIndex
	(
		RailGraphLive graph, Dictionary<RailGraphLive.EndpointKey, int> nodeByEndpoint,
		List<RailPathNode> nodes, List<RailPathArc> arcs, HashSet<ulong>? indexedEdges = null, HashSet<RailGraphLive.EndpointKey>? indexedEndpoints = null
	)
	{
		this.NodeByEndpoint = nodeByEndpoint;
		this.Nodes = nodes;
		this.Arcs = arcs;
		this.IndexedEdgeHashes = indexedEdges;
		this.IndexedEndpoints = indexedEndpoints;

		ArcsByNode = new List<RailPathArc>[nodes.Count];
		for (int nodeIndex = 0; nodeIndex < ArcsByNode.Length; nodeIndex++) ArcsByNode[nodeIndex] = new List<RailPathArc>(2);
		for (int arcIndex = 0; arcIndex < arcs.Count; arcIndex++) { ArcsByNode[arcs[arcIndex].FromNode].Add(arcs[arcIndex]); }

		BlockedDirectedTransitions = PrecomputeBlockedDirectedTransitions(graph);
	}

	internal int NodeCount => Nodes.Count;
	internal int ArcCount => Arcs.Count;

	internal RailGraphLive.EndpointKey GetNodeEndpoint(int nodeID) => Nodes[nodeID].Endpoint;

	internal IReadOnlyCollection<ulong> IndexedEdges => IndexedEdgeHashes is null ? Array.Empty<ulong>() : IndexedEdgeHashes;
	internal bool IsComponentIndex => IndexedEdgeHashes != null;

	internal static RailPathIndex Build(RailGraphLive graph) { return BuildInternal(graph, null, null); }

	internal static bool TryBuildComponent(RailGraphLive graph, ulong seedEdgeHash, out RailPathIndex index)
	{
		index = null!;
		if (!TryCreateComponentBuildJob(graph, seedEdgeHash, out ComponentBuildJob? job) || job == null) return false;

		while (!job.IsComplete && !job.Failed) job.Step(16_384);
		if (!job.TryGetResult(out index)) return false;
		return true;
	}

	internal static bool TryCreateComponentBuildJob(RailGraphLive graph, ulong seedEdgeHash, out ComponentBuildJob? job)
	{
		job = null;
		if (graph == null || seedEdgeHash == 0 || !graph.TryGetEdgeEndpoints(seedEdgeHash, out _, out _, out byte gauge)) { return false; }

		job = new ComponentBuildJob(graph, seedEdgeHash, gauge);
		return true;
	}

	private static void CollectConnectedEdges(RailGraphLive graph, RailGraphLive.EndpointKey endpoint, byte gauge, HashSet<ulong> componentEdges, Queue<ulong> pending)
	{
		if (!graph.TryGetIncidentEdges(endpoint, out List<ulong> incidentEdges)) return;
		for (int incidentEdgeIndex = 0; incidentEdgeIndex < incidentEdges.Count; incidentEdgeIndex++)
		{
			ulong edgeHash = incidentEdges[incidentEdgeIndex];
			if (!graph.TryGetEdgeGauge(edgeHash, out byte edgeGauge) || edgeGauge != gauge) continue;
			if (componentEdges.Add(edgeHash)) pending.Enqueue(edgeHash);
		}
	}

	internal sealed class ComponentBuildJob
	{
		private enum BuildPhase : byte
		{
			Discover,
			Nodes,
			Arcs,
			Finalize,
			Complete,
			Failed
		}

		private readonly RailGraphLive Graph;
		private readonly byte Gauge;
		private readonly HashSet<ulong> ComponentEdges = new();
		private readonly HashSet<RailGraphLive.EndpointKey> ComponentEndpoints = new();
		private readonly Queue<ulong> PendingEdges = new();
		private readonly HashSet<ulong> PendingEdgeSet = new();
		private readonly List<ulong> NewlyDiscoveredEdges = new();
		private readonly Dictionary<RailGraphLive.EndpointKey, int> NodeMap = new();
		private readonly List<RailPathNode> Nodes = new();
		private readonly List<RailPathArc> Arcs = new();
		private readonly HashSet<ArcKey> EmittedArcs = new();
		private List<RailGraphLive.EndpointKey>? EndpointList;
		private int NodeEndpointIndex;
		private int ArcNodeIndex;
		private int ArcIncidentIndex;
		private ArcBuildState? ActiveArc;
		private RailPathIndex? Result;
		private BuildPhase Phase = BuildPhase.Discover;

		internal int GraphVersion { get; private set; }
		internal long SchedulerSequence { get; set; }
		internal bool IsComplete => Phase == BuildPhase.Complete;
		internal bool Failed => Phase == BuildPhase.Failed;
		internal bool DiscoveryComplete => Phase != BuildPhase.Discover;
		internal int BuildProgressRank => (int)Phase;
		internal IReadOnlyCollection<ulong> DiscoveredEdges => ComponentEdges;

		internal ComponentBuildJob(RailGraphLive graph, ulong seedEdgeHash, byte gauge)
		{
			this.Graph = graph;
			this.Gauge = gauge;
			GraphVersion = graph.BuildVersion;
			ComponentEdges.Add(seedEdgeHash);
			NewlyDiscoveredEdges.Add(seedEdgeHash);
			PendingEdges.Enqueue(seedEdgeHash);
			PendingEdgeSet.Add(seedEdgeHash);
		}

		/// Merges another discovery frontier after both jobs prove they belong to the same physical component.
		/// Once discovery has completed this job already owns the entire component, so no frontier transfer is necessary.
		internal void AbsorbDiscoveryFrom(ComponentBuildJob other)
		{
			if (other == null || ReferenceEquals(this, other) || Phase != BuildPhase.Discover) return;

			ComponentEndpoints.UnionWith(other.ComponentEndpoints);
			foreach (ulong edgeHash in other.ComponentEdges)
			{
				if (!ComponentEdges.Add(edgeHash)) continue;
				NewlyDiscoveredEdges.Add(edgeHash);

				// Only transfer work the winner has never seen. An overlapping edge already absent from this frontier has already been processed here.
				if (other.PendingEdgeSet.Contains(edgeHash) && PendingEdgeSet.Add(edgeHash)) PendingEdges.Enqueue(edgeHash);
			}
		}


		internal bool TryPromoteAcrossUnrelatedChange(RailGraphChangeSet change, int newGraphVersion)
		{
			if (change == null || change.GlobalInvalidation) return false;
			for (int touchedEdgeIndex = 0; touchedEdgeIndex < change.TouchedEdges.Count; touchedEdgeIndex++)
			{
				if (ComponentEdges.Contains(change.TouchedEdges[touchedEdgeIndex])) return false;
			}
			for (int touchedEndpointIndex = 0; touchedEndpointIndex < change.TouchedEndpoints.Count; touchedEndpointIndex++)
			{
				if (ComponentEndpoints.Contains(change.TouchedEndpoints[touchedEndpointIndex])) return false;
			}
			GraphVersion = newGraphVersion;
			return true;
		}

		internal void DrainNewlyDiscoveredEdges(List<ulong> destination)
		{
			if (destination == null || NewlyDiscoveredEdges.Count == 0) return;
			destination.AddRange(NewlyDiscoveredEdges);
			NewlyDiscoveredEdges.Clear();
		}

		internal void Cancel()
		{
			if (Phase == BuildPhase.Complete || Phase == BuildPhase.Failed) return;
			Phase = BuildPhase.Failed;
			Result = null;
		}

		internal int Step(int maxWork)
		{
			if (maxWork <= 0 || IsComplete || Failed) return 0;
			int used = 0;

			while (used < maxWork && !IsComplete && !Failed)
			{
				if (Graph.BuildVersion != GraphVersion) { Cancel(); break; }

				switch (Phase)
				{
					case BuildPhase.Discover:
						StepDiscover();
						used++;
					break;

					case BuildPhase.Nodes:
						StepNodes();
						used++;
					break;

					case BuildPhase.Arcs:
						StepArcs();
						used++;
					break;

					case BuildPhase.Finalize:
						Result = new RailPathIndex(Graph, NodeMap, Nodes, Arcs, ComponentEdges, ComponentEndpoints);
						Phase = BuildPhase.Complete;
						used++;
					break;
				}
			}

			return used;
		}

		internal bool TryGetResult(out RailPathIndex index)
		{
			index = Result!;
			return IsComplete && Result != null;
		}

		private void StepDiscover()
		{
			if (PendingEdges.Count == 0)
			{
				EndpointList = new List<RailGraphLive.EndpointKey>(ComponentEndpoints);
				Phase = BuildPhase.Nodes;
				return;
			}

			ulong edgeHash = PendingEdges.Dequeue();
			PendingEdgeSet.Remove(edgeHash);
			if (!Graph.TryGetEdgeEndpoints(edgeHash, out RailGraphLive.EndpointKey firstEndpoint, out RailGraphLive.EndpointKey secondEndpoint, out byte edgeGauge) || edgeGauge != Gauge)
			{
				Cancel();
				return;
			}

			ComponentEndpoints.Add(firstEndpoint);
			ComponentEndpoints.Add(secondEndpoint);
			DiscoverAtEndpoint(firstEndpoint);
			DiscoverAtEndpoint(secondEndpoint);
		}

		private void DiscoverAtEndpoint(RailGraphLive.EndpointKey endpoint)
		{
			if (!Graph.TryGetIncidentEdges(endpoint, out List<ulong> incidentEdges)) return;
			for (int incidentEdgeIndex = 0; incidentEdgeIndex < incidentEdges.Count; incidentEdgeIndex++)
			{
				ulong edgeHash = incidentEdges[incidentEdgeIndex];
				if (!Graph.TryGetEdgeGauge(edgeHash, out byte edgeGauge) || edgeGauge != Gauge) continue;
				if (!ComponentEdges.Add(edgeHash)) continue;
				NewlyDiscoveredEdges.Add(edgeHash);
				if (PendingEdgeSet.Add(edgeHash)) PendingEdges.Enqueue(edgeHash);
			}
		}

		private void StepNodes()
		{
			if (EndpointList == null) { Cancel(); return; }
			if (NodeEndpointIndex >= EndpointList.Count)
			{
				// In case of a circle just pick whatever node and use it as a synthetic pathfinding node.
				if (Nodes.Count == 0 && EndpointList.Count > 0)
				{
					RailGraphLive.EndpointKey anchor = EndpointList[0];
					for (int endpointIndex = 1; endpointIndex < EndpointList.Count; endpointIndex++)
					{
						if (EndpointList[endpointIndex].CompareTo(anchor) < 0) anchor = EndpointList[endpointIndex];
					}
					AddNode(Graph, NodeMap, Nodes, anchor);
				}
				Phase = BuildPhase.Arcs;
				return;
			}

			RailGraphLive.EndpointKey endpoint = EndpointList[NodeEndpointIndex++];
			if (Graph.IsTopologyPathNode(endpoint)) AddNode(Graph, NodeMap, Nodes, endpoint);
		}

		private void StepArcs()
		{
			if (ActiveArc != null)
			{
				ArcStepResult stepResult = ActiveArc.Step(Graph, NodeMap, ComponentEdges, out RailPathArc arc);
				if (stepResult == ArcStepResult.Completed)		{ Arcs.Add(arc.WithIndex(Arcs.Count));	ActiveArc = null; }
				else if (stepResult == ArcStepResult.Failed)	{										ActiveArc = null; }
				return;
			}

			while (ArcNodeIndex < Nodes.Count)
			{
				RailPathNode node = Nodes[ArcNodeIndex];
				if (!Graph.TryGetIncidentEdges(node.Endpoint, out List<ulong> incidentEdges))
				{
					ArcNodeIndex++;
					ArcIncidentIndex = 0;
					continue;
				}

				while (ArcIncidentIndex < incidentEdges.Count)
				{
					ulong edgeHash = incidentEdges[ArcIncidentIndex++];
					if (!ComponentEdges.Contains(edgeHash) || !Graph.TryGetEdgeGauge(edgeHash, out byte edgeGauge) || edgeGauge != node.Endpoint.Gauge) { continue; }

					ArcKey key = new(ArcNodeIndex, edgeHash);
					if (!EmittedArcs.Add(key)) continue;
					ActiveArc = new ArcBuildState(node.Endpoint, ArcNodeIndex, edgeHash, edgeGauge, ComponentEdges.Count + 1);
					return;
				}

				ArcNodeIndex++;
				ArcIncidentIndex = 0;
			}

			Phase = BuildPhase.Finalize;
		}

		private enum ArcStepResult : byte
		{
			Continue,
			Completed,
			Failed
		}

		private sealed class ArcBuildState
		{
			private RailGraphLive.EndpointKey CurrentEndpoint;
			private ulong CurrentEdge;
			private readonly int FromNode;
			private readonly ulong FirstEdge;
			private readonly byte Gauge;
			private readonly int MaxEdges;
			private readonly List<ulong> EdgeChain = new(8);
			private double Length;

			internal ArcBuildState(RailGraphLive.EndpointKey startEndpoint, int fromNode, ulong firstEdge, byte gauge, int maxEdges)
			{
				CurrentEndpoint = startEndpoint;
				CurrentEdge = firstEdge;
				this.FromNode = fromNode;
				this.FirstEdge = firstEdge;
				this.Gauge = gauge;
				this.MaxEdges = Math.Max(1, maxEdges);
			}

			internal ArcStepResult Step(RailGraphLive graph, Dictionary<RailGraphLive.EndpointKey, int> nodeMap, HashSet<ulong> allowedEdges, out RailPathArc arc)
			{
				arc = default;
				if (EdgeChain.Count >= MaxEdges || !allowedEdges.Contains(CurrentEdge) || !graph.TryGetOtherEndpoint(CurrentEdge, CurrentEndpoint, out RailGraphLive.EndpointKey nextEndpoint))
				{
					return ArcStepResult.Failed;
				}

				EdgeChain.Add(CurrentEdge);
				Length += graph.GetEdgeLength(CurrentEdge);

				if (nodeMap.TryGetValue(nextEndpoint, out int toNode))
				{
					if (toNode == FromNode && EdgeChain.Count == 1) return ArcStepResult.Failed;
					graph.TryGetOccupancyZoneID(FirstEdge, out ulong zoneID);
					arc = new RailPathArc(-1, FromNode, toNode, Gauge, FirstEdge, CurrentEdge, zoneID, Length, EdgeChain.ToArray());
					return ArcStepResult.Completed;
				}

				if (!graph.TryGetIncidentEdges(nextEndpoint, out List<ulong> incidentEdges) || incidentEdges.Count != 2) return ArcStepResult.Failed;

				ulong nextEdge = incidentEdges[0] == CurrentEdge ? incidentEdges[1] : incidentEdges[0];
				if
				(
					!allowedEdges.Contains(nextEdge) || !graph.TryGetEdgeGauge(nextEdge, out byte nextGauge) || nextGauge != Gauge ||
					!RailTransitionRules.IsDirectedTransitionAllowedSameGauge( graph, nextEndpoint, Gauge, CurrentEdge, nextEdge)
				) { return ArcStepResult.Failed; }

				CurrentEndpoint = nextEndpoint;
				CurrentEdge = nextEdge;
				return ArcStepResult.Continue;
			}
		}
	}

	private static RailPathIndex BuildInternal(
		RailGraphLive graph,
		HashSet<ulong>? allowedEdges,
		HashSet<RailGraphLive.EndpointKey>? allowedEndpoints)
	{
		List<RailGraphLive.EndpointKey> endpoints;
		if (allowedEndpoints == null)
		{
			endpoints = new List<RailGraphLive.EndpointKey>(4096);
			graph.CollectEndpointKeys(endpoints);
		}
		else { endpoints = new List<RailGraphLive.EndpointKey>(allowedEndpoints); }

		Dictionary<RailGraphLive.EndpointKey, int> nodeMap = new();
		List<RailPathNode> nodes = new();

		for (int endpointIndex = 0; endpointIndex < endpoints.Count; endpointIndex++)
		{
			RailGraphLive.EndpointKey endpoint = endpoints[endpointIndex];
			if (!graph.IsTopologyPathNode(endpoint)) continue;
			AddNode(graph, nodeMap, nodes, endpoint);
		}

		AddTopologyFreeComponentAnchors(graph, endpoints, nodeMap, nodes, allowedEdges);

		List<RailPathArc> arcs = new();
		HashSet<ArcKey> emitted = new();

		for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
		{
			RailPathNode node = nodes[nodeIndex];
			if (!graph.TryGetIncidentEdges(node.Endpoint, out List<ulong> incidentEdges)) continue;

			for (int incidentEdgeIndex = 0; incidentEdgeIndex < incidentEdges.Count; incidentEdgeIndex++)
			{
				ulong edgeHash = incidentEdges[incidentEdgeIndex];
				if (allowedEdges != null && !allowedEdges.Contains(edgeHash)) continue;
				if (!graph.TryGetEdgeGauge(edgeHash, out byte gauge) || gauge != node.Endpoint.Gauge) continue;
				if (!TryBuildArc(graph, nodeMap, node.Endpoint, nodeIndex, edgeHash, allowedEdges, out RailPathArc arc)) continue;

				ArcKey key = new(arc.FromNode, arc.FirstEdgeHash);
				if (!emitted.Add(key)) continue;
				arcs.Add(arc.WithIndex(arcs.Count));
			}
		}

		return new RailPathIndex(graph, nodeMap, nodes, arcs, allowedEdges, allowedEndpoints);
	}

	private static void AddTopologyFreeComponentAnchors
	(
		RailGraphLive graph, IReadOnlyList<RailGraphLive.EndpointKey> endpoints,
		Dictionary<RailGraphLive.EndpointKey, int> nodeMap, List<RailPathNode> nodes, HashSet<ulong>? allowedEdges
	)
	{
		HashSet<RailGraphLive.EndpointKey> visited = new();
		Queue<RailGraphLive.EndpointKey> pending = new();

		for (int endpointIndex = 0; endpointIndex < endpoints.Count; endpointIndex++)
		{
			RailGraphLive.EndpointKey seed = endpoints[endpointIndex];
			if (!visited.Add(seed)) continue;

			pending.Clear();
			pending.Enqueue(seed);
			RailGraphLive.EndpointKey anchor = seed;
			bool hasTopologyNode = nodeMap.ContainsKey(seed);

			while (pending.Count > 0)
			{
				RailGraphLive.EndpointKey endpoint = pending.Dequeue();
				if (endpoint.CompareTo(anchor) < 0) anchor = endpoint;
				if (nodeMap.ContainsKey(endpoint)) hasTopologyNode = true;
				if (!graph.TryGetIncidentEdges(endpoint, out List<ulong> incidentEdges)) continue;

				for (int incidentEdgeIndex = 0; incidentEdgeIndex < incidentEdges.Count; incidentEdgeIndex++)
				{
					ulong edgeHash = incidentEdges[incidentEdgeIndex];
					if (allowedEdges != null && !allowedEdges.Contains(edgeHash)) continue;
					if (!graph.TryGetEdgeGauge(edgeHash, out byte gauge) || gauge != endpoint.Gauge) continue;
					if (!graph.TryGetOtherEndpoint(edgeHash, endpoint, out RailGraphLive.EndpointKey other)) continue;
					if (visited.Add(other)) pending.Enqueue(other);
				}
			}

			if (!hasTopologyNode) AddNode(graph, nodeMap, nodes, anchor);
		}
	}

	internal bool TouchesGraphChange(RailGraphChangeSet change)
	{
		if (change == null || !change.HasAnyChange) return false;
		if (change.GlobalInvalidation || IndexedEdgeHashes == null || IndexedEndpoints == null) return true;

		for (int touchedEdgeIndex = 0; touchedEdgeIndex < change.TouchedEdges.Count; touchedEdgeIndex++)
		{
			if (IndexedEdgeHashes.Contains(change.TouchedEdges[touchedEdgeIndex])) return true;
		}
		for (int touchedEndpointIndex = 0; touchedEndpointIndex < change.TouchedEndpoints.Count; touchedEndpointIndex++)
		{
			if (IndexedEndpoints.Contains(change.TouchedEndpoints[touchedEndpointIndex])) return true;
		}
		return false;
	}

	internal bool TryResolveStationTarget(RailGraphLive graph, RailStationRegistrySystem.RailStationEntry station, out StationPathTarget target)
	{
		target = default;

		if (station == null || !station.HasStopEndpoint) return false;

		RailGraphLive.EndpointKey stop = new(station.StopX16, station.StopY16, station.StopZ16, station.Key.Dimension, station.Gauge);
		if (!graph.TryGetIncidentEdges(stop, out List<ulong> incidentEdges) || incidentEdges.Count == 0) return false;

		ulong approach = ChooseApproachEdge(graph, stop, station.Gauge, station.RotationCode, incidentEdges);
		if (approach == 0) return false;
		if (IndexedEdgeHashes != null && !IndexedEdgeHashes.Contains(approach)) return false;

		ulong depart = 0;
		for (int incidentEdgeIndex = 0; incidentEdgeIndex < incidentEdges.Count; incidentEdgeIndex++)
		{
			ulong edgeHash = incidentEdges[incidentEdgeIndex];
			if (edgeHash == approach) continue;
			if (!graph.TryGetEdgeGauge(edgeHash, out byte edgeGauge) || edgeGauge != station.Gauge) continue;
			depart = edgeHash;
			break;
		}

		StationTargetCacheKey cacheKey = new(stop, approach, depart, station.Gauge);
		if (!StationTargetCache.TryGetValue(cacheKey, out StationTargetGeometry geometry))
		{
			if (!TryBuildStationTargetGeometry(graph, stop, approach, station.Gauge, out geometry)) return false;
			StationTargetCache[cacheKey] = geometry;
		}

		target = new StationPathTarget
		(
			station.Key, geometry.TerminalNodeID, stop, approach, depart, station.Gauge, station.DisplayName ?? "",
			geometry.TerminalEntryEdgeHash, geometry.TerminalEdges, geometry.TerminalLengthMeters, geometry.TerminalZoneID
		);
		return true;
	}

	private bool TryBuildStationTargetGeometry(RailGraphLive graph, RailGraphLive.EndpointKey stop, ulong approach, byte gauge, out StationTargetGeometry geometry)
	{
		geometry = default;

		// A station may coincide with a genuine topology node.
		// In that rare case the station is already a query terminal at the node and needs no synthetic corridor.
		if (NodeByEndpoint.TryGetValue(stop, out int stopNodeID))
		{
			graph.TryGetOccupancyZoneID(approach, out ulong stopZoneID);
			geometry = new StationTargetGeometry(stopNodeID, 0, Array.Empty<ulong>(), 0, stopZoneID);
			return true;
		}

		if (!graph.TryGetOtherEndpoint(approach, stop, out RailGraphLive.EndpointKey endpoint)) return false;

		List<ulong> reversedEdges = new(8) { approach };
		double lengthMeters = graph.GetEdgeLength(approach);
		ulong currentEdge = approach;

		for (int guard = 0; guard < 4096; guard++)
		{
			if (NodeByEndpoint.TryGetValue(endpoint, out int terminalNodeID))
			{
				reversedEdges.Reverse();
				ulong[] terminalEdges = reversedEdges.ToArray();
				ulong entryEdge = terminalEdges.Length == 0 ? 0 : terminalEdges[0];
				graph.TryGetOccupancyZoneID(entryEdge, out ulong zoneID);
				geometry = new StationTargetGeometry(terminalNodeID, entryEdge, terminalEdges, lengthMeters, zoneID);
				return true;
			}

			if (!graph.TryGetIncidentEdges(endpoint, out List<ulong> incidentEdges)) return false;

			ulong nextEdge = 0;
			int sameGaugeAlternatives = 0;
			for (int incidentEdgeIndex = 0; incidentEdgeIndex < incidentEdges.Count; incidentEdgeIndex++)
			{
				ulong candidate = incidentEdges[incidentEdgeIndex];
				if (candidate == currentEdge) continue;
				if (!graph.TryGetEdgeGauge(candidate, out byte candidateGauge) || candidateGauge != gauge) continue;
				nextEdge = candidate;
				sameGaugeAlternatives++;
			}

			// A non-topology point must have exactly one same-gauge continuation.
			if (sameGaugeAlternatives != 1 || nextEdge == 0) return false;
			if (IndexedEdgeHashes != null && !IndexedEdgeHashes.Contains(nextEdge)) return false;

			// We are walking outward from the station, opposite the intended travel direction. Validate the actual terminal to station transition order.
			if (!RailTransitionRules.IsDirectedTransitionAllowedSameGauge(graph, endpoint, gauge, nextEdge, currentEdge)) return false;
			if (!graph.TryGetOtherEndpoint(nextEdge, endpoint, out RailGraphLive.EndpointKey nextEndpoint)) return false;

			currentEdge = nextEdge;
			reversedEdges.Add(currentEdge);
			lengthMeters += graph.GetEdgeLength(currentEdge);
			endpoint = nextEndpoint;
		}

		return false;
	}

	internal int CollectStationTargets(RailGraphLive graph, IReadOnlyList<RailStationRegistrySystem.RailStationEntry> stations, List<StationPathTarget> destination)
	{
		if (graph == null || stations == null || destination == null) return 0;

		int start = destination.Count;
		for (int targetIndex = 0; targetIndex < stations.Count; targetIndex++)
		{
			if (!TryResolveStationTarget(graph, stations[targetIndex], out StationPathTarget target)) continue;
			if (ContainsPhysicalTarget(destination, 0, target)) continue;
			destination.Add(target);
		}

		return destination.Count - start;
	}

	private static bool ContainsPhysicalTarget(List<StationPathTarget> targets, int start, StationPathTarget target)
	{
		for (int terminalEdgeIndex = start; terminalEdgeIndex < targets.Count; terminalEdgeIndex++)
		{
			if (targets[terminalEdgeIndex].SamePhysicalStop(target)) return true;
		}

		return false;
	}

	internal bool TryFindRouteGreedy
	(
		RailGraphLive graph, in RailwayVehicleShared.RailCursor cursor, StationPathTarget target, ulong currentZoneID,
		out RailAutomationRoute route, RailRouteAcceptance? acceptRoute = null, int maxRejectedCandidates = 0
	)
	{
		SingleTargetScratch[0] = target;
		return TryFindRouteGreedy(graph, cursor, SingleTargetScratch, currentZoneID, out route, acceptRoute, maxRejectedCandidates);
	}

	internal bool TryFindRouteGreedy
	(
		RailGraphLive graph, in RailwayVehicleShared.RailCursor cursor, IReadOnlyList<StationPathTarget> targets,
		ulong currentZoneID, out RailAutomationRoute route, RailRouteAcceptance? acceptRoute = null, int maxRejectedCandidates = 0
	)
	{
		route = default!;
		if (graph == null || targets == null || targets.Count == 0) return false;

		byte gauge = targets[0].Gauge;
		List<ulong> approachEdges = ApproachEdgeScratch;
		approachEdges.Clear();

		// A train can already be inside the destination corridor. Resolve that without routing past the station to the next topology node.
		if (TryFindDirectForwardTarget(graph, cursor, targets, approachEdges, out StationPathTarget directTarget))
		{
			RailAutomationRoute directRoute = RailAutomationRoute.Empty(this, directTarget, new SearchState(-1, cursor.SegmentHash), approachEdges);

			if (acceptRoute == null || acceptRoute(directRoute)) { route = directRoute; return true; }
		}

		approachEdges.Clear();
		if (!TryResolveForwardStart(graph, cursor, approachEdges, out SearchState start)) return false;

		if (acceptRoute != null)
		{
			return TryFindAcceptedRouteGreedy(graph, targets, currentZoneID, gauge, start, approachEdges, acceptRoute, maxRejectedCandidates, out route);
		}

		return TryFindRouteFromStateGreedy(graph, targets, currentZoneID, gauge, start, approachEdges, out route);
	}

	internal bool TryFindRouteFromStationGreedy(RailGraphLive graph, StationPathTarget from, StationPathTarget target, ulong currentZoneID, out RailAutomationRoute route)
	{
		SingleTargetScratch[0] = target;
		return TryFindRouteFromStationGreedy(graph, from, SingleTargetScratch, currentZoneID, out route);
	}

	internal bool TryFindRouteFromStationGreedy(RailGraphLive graph, StationPathTarget from, IReadOnlyList<StationPathTarget> targets, ulong currentZoneID, out RailAutomationRoute route)
	{
		route = default!;

		if (graph == null || targets == null || targets.Count == 0) return false;
		if (from.ApproachEdgeHash == 0 || from.DepartEdgeHash == 0) return false;
		if (!RailTransitionRules.IsDirectedTransitionAllowedSameGauge(graph, from.StopEndpoint, from.Gauge, from.ApproachEdgeHash, from.DepartEdgeHash)) { return false; }

		List<ulong> approachEdges = ApproachEdgeScratch;
		approachEdges.Clear();

		if (TryFindDirectForwardTargetFromStation(graph, from, targets, approachEdges, out StationPathTarget directTarget))
		{
			route = RailAutomationRoute.Empty(this, directTarget, new SearchState(-1, from.DepartEdgeHash), approachEdges);
			return true;
		}

		approachEdges.Clear();
		if (!TryResolveForwardStartFromStation(graph, from, approachEdges, out SearchState start)) return false;
		return TryFindRouteFromStateGreedy(graph, targets, currentZoneID, from.Gauge, start, approachEdges, out route);
	}

	private bool TryFindRouteFromStateGreedy
	(
		RailGraphLive graph, IReadOnlyList<StationPathTarget> targets, ulong currentZoneID,
		byte gauge, SearchState start, List<ulong> approachEdges, out RailAutomationRoute route
	)
	{
		route = default!;

		BasicSearchWorkspace workspace = BasicSearchWorkspaceCache;
		workspace.Reset();
		PriorityQueue<SearchState, double> open = workspace.Open;
		Dictionary<SearchState, PathCame> came = workspace.Came;
		Dictionary<SearchState, double> bestCost = workspace.BestCost;
		HashSet<SearchState> closed = workspace.Closed;

		bestCost[start] = 0;
		open.Enqueue(start, HeuristicMetersToTargets(start.NodeID, targets));

		const int maxExpansions = 8192;
		const double blockedZonePenaltyMeters = 100000.0;

		int expansions = 0;
		SearchState found = default;
		StationPathTarget foundTarget = default;
		double foundCost = double.PositiveInfinity;
		bool hasFound = false;

		while (open.Count > 0 && expansions++ < maxExpansions)
		{
			SearchState currentState = open.Dequeue();
			if (!closed.Add(currentState)) continue;

			RailPathNode curNode = Nodes[currentState.NodeID];
			if (currentState.ArrivedViaEdgeHash != 0 && curNode.IsSignal && !graph.IsOneWayCrossingAllowed(curNode.Endpoint, currentState.ArrivedViaEdgeHash)) { continue; }

			double currentCost = bestCost[currentState];

			for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
			{
				StationPathTarget target = targets[targetIndex];
				if (!TryGetTerminalCost(graph, currentZoneID, currentState, target, blockedZonePenaltyMeters, out double terminalCost)) continue;

				double candidateCost = currentCost + terminalCost;
				if (candidateCost + CostEpsilon < foundCost)
				{
					found = currentState;
					foundTarget = target;
					foundCost = candidateCost;
					hasFound = true;
				}
			}

			List<RailPathArc> outgoing = ArcsByNode[currentState.NodeID];
			for (int arcIndex = 0; arcIndex < outgoing.Count; arcIndex++)
			{
				RailPathArc arc = outgoing[arcIndex];
				if (arc.Gauge != gauge) continue;
				if (currentState.ArrivedViaEdgeHash != 0)
				{
					if (arc.FirstEdgeHash == currentState.ArrivedViaEdgeHash) continue; // no reverse/U-turn pathing
					if (IsDirectedTransitionBlocked(currentState.NodeID, currentState.ArrivedViaEdgeHash, arc.FirstEdgeHash)) continue;
				}

				double penalty = 0;
				if (arc.ZoneID != 0 && arc.ZoneID != currentZoneID && graph.IsOccupancyZoneOccupied(arc.ZoneID))
				{
					// Do not prune occupied zones. Penalize them so a free route wins, but still return a best candidate path when every route is currently blocked.
					penalty = blockedZonePenaltyMeters;
				}

				SearchState next = new(arc.ToNode, arc.LastEdgeHash);
				if (closed.Contains(next)) continue;

				double nextCost = currentCost + Math.Max(0.1, arc.LengthMeters) + penalty;
				if (bestCost.TryGetValue(next, out double oldCost) && oldCost <= nextCost) continue;

				bestCost[next] = nextCost;
				came[next] = new PathCame(currentState, arc.Index);
				open.Enqueue(next, nextCost + HeuristicMetersToTargets(arc.ToNode, targets));
			}

			if (hasFound && (!open.TryPeek(out _, out double nextPriority) || nextPriority >= foundCost - CostEpsilon)) break;
		}

		if (!hasFound) return false;
		return TryBuildRouteFromCame(start, found, foundTarget, approachEdges, came, workspace.ReverseArcs, out route);
	}

	private bool TryFindAcceptedRouteGreedy
	(
		RailGraphLive graph, IReadOnlyList<StationPathTarget> targets, ulong currentZoneID, byte gauge, SearchState start,
		List<ulong> approachEdges, RailRouteAcceptance acceptRoute, int maxRejectedCandidates, out RailAutomationRoute route
	)
	{
		route = default!;

		AcceptedSearchWorkspace workspace = AcceptedSearchWorkspaceCache;
		workspace.Reset();
		PriorityQueue<int, double> open = workspace.Open;
		List<SearchVisit> visits = workspace.Visits;
		Dictionary<SearchState, List<SearchVisitVariant>> activeVariantsByState = workspace.ActiveVariantsByState;
		Dictionary<SearchState, int> expandedVariantsByState = workspace.ExpandedVariantsByState;
		HashSet<int> activeVisitIDs = workspace.ActiveVisitIDs;

		const int maxExpansions = 8192;
		const double blockedZonePenaltyMeters = 100000.0;
		const int maxPathVariantsPerState = 4;

		int rejectedCandidates = 0;
		int startVisitID = AddSearchVisit(visits, start, 0, -1, -1);
		TryRegisterSearchVisit(workspace, start, 0, startVisitID, maxPathVariantsPerState);
		open.Enqueue(startVisitID, HeuristicMetersToTargets(start.NodeID, targets));

		RailAutomationRoute? bestAcceptedRoute = null;
		double bestAcceptedCost = double.PositiveInfinity;

		int expansions = 0;
		while (open.Count > 0 && expansions++ < maxExpansions)
		{
			int visitID = open.Dequeue();
			if ((uint)visitID >= (uint)visits.Count) continue;
			if (!activeVisitIDs.Contains(visitID)) continue;

			SearchVisit visit = visits[visitID];
			SearchState currentState = visit.State;
			if (!TryRegisterStateExpansion(expandedVariantsByState, currentState, maxPathVariantsPerState)) continue;

			RailPathNode curNode = Nodes[currentState.NodeID];
			if (currentState.ArrivedViaEdgeHash != 0 && curNode.IsSignal && !graph.IsOneWayCrossingAllowed(curNode.Endpoint, currentState.ArrivedViaEdgeHash))
			{
				continue;
			}

			for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
			{
				StationPathTarget target = targets[targetIndex];
				if (!TryGetTerminalCost(graph, currentZoneID, currentState, target, blockedZonePenaltyMeters, out double terminalCost)) continue;

				if (!TryBuildRouteFromVisits(start, visitID, target, approachEdges, visits, workspace.ReverseArcs, out RailAutomationRoute candidate)) continue;

				if (acceptRoute(candidate))
				{
					double candidateCost = visit.Cost + terminalCost;
					if (candidateCost + CostEpsilon < bestAcceptedCost)
					{
						bestAcceptedRoute = candidate;
						bestAcceptedCost = candidateCost;
					}
				}
				else
				{
					rejectedCandidates++;
					if (maxRejectedCandidates > 0 && rejectedCandidates >= maxRejectedCandidates)
					{
						if (bestAcceptedRoute != null) { route = bestAcceptedRoute; return true; }
						return false;
					}
				}
			}

			List<RailPathArc> outgoing = ArcsByNode[currentState.NodeID];
			for (int arcIndex = 0; arcIndex < outgoing.Count; arcIndex++)
			{
				RailPathArc arc = outgoing[arcIndex];
				if (arc.Gauge != gauge) continue;
				if (currentState.ArrivedViaEdgeHash != 0)
				{
					if (arc.FirstEdgeHash == currentState.ArrivedViaEdgeHash) continue; // no reverse/U-turn pathing
					if (IsDirectedTransitionBlocked(currentState.NodeID, currentState.ArrivedViaEdgeHash, arc.FirstEdgeHash)) continue;
				}

				double penalty = 0;
				if (arc.ZoneID != 0 && arc.ZoneID != currentZoneID && graph.IsOccupancyZoneOccupied(arc.ZoneID)) { penalty = blockedZonePenaltyMeters; }

				SearchState next = new(arc.ToNode, arc.LastEdgeHash);
				double nextCost = visit.Cost + Math.Max(0.1, arc.LengthMeters) + penalty;

				int nextVisitID = visits.Count;
				if (!TryRegisterSearchVisit(workspace, next, nextCost, nextVisitID, maxPathVariantsPerState)) continue;

				visits.Add(new SearchVisit(next, nextCost, visitID, arc.Index));
				open.Enqueue(nextVisitID, nextCost + HeuristicMetersToTargets(arc.ToNode, targets));
			}

			if (bestAcceptedRoute != null && (!open.TryPeek(out _, out double nextPriority) || nextPriority >= bestAcceptedCost - CostEpsilon))
			{ 
				route = bestAcceptedRoute;
				return true;
			}
		}

		if (bestAcceptedRoute != null) { route = bestAcceptedRoute; return true; }

		return false;
	}

	private bool TryGetTerminalCost(RailGraphLive graph, ulong currentZoneID, SearchState state, StationPathTarget target, double blockedZonePenaltyMeters, out double terminalCost)
	{
		terminalCost = 0;
		if (target.TerminalNodeID != state.NodeID) return false;

		ulong[] terminalEdges = target.TerminalEdges ?? Array.Empty<ulong>();
		if (terminalEdges.Length == 0) { return state.ArrivedViaEdgeHash != 0 && state.ArrivedViaEdgeHash == target.ApproachEdgeHash; }

		ulong entryEdge = target.TerminalEntryEdgeHash;
		if (entryEdge == 0) return false;
		if (state.ArrivedViaEdgeHash != 0)
		{
			if (entryEdge == state.ArrivedViaEdgeHash) return false;
			if (IsDirectedTransitionBlocked(state.NodeID, state.ArrivedViaEdgeHash, entryEdge)) return false;
		}

		double penalty = 0;
		if (target.TerminalZoneID != 0 && target.TerminalZoneID != currentZoneID && graph.IsOccupancyZoneOccupied(target.TerminalZoneID)) { penalty = blockedZonePenaltyMeters; }

		terminalCost = Math.Max(0.1, target.TerminalLengthMeters) + penalty;
		return true;
	}

	private static int AddSearchVisit(List<SearchVisit> visits, SearchState state, double cost, int previousVisitID, int arcIndex)
	{
		int visitID = visits.Count;
		visits.Add(new SearchVisit(state, cost, previousVisitID, arcIndex));
		return visitID;
	}

	private static bool TryRegisterSearchVisit(AcceptedSearchWorkspace workspace, SearchState state, double cost, int visitID, int maxVariantsPerState)
	{
		const double epsilon = 1e-7;
		if (maxVariantsPerState <= 0) maxVariantsPerState = 1;

		Dictionary<SearchState, List<SearchVisitVariant>> variantsByState = workspace.ActiveVariantsByState;
		HashSet<int> activeVisitIDs = workspace.ActiveVisitIDs;
		if (!variantsByState.TryGetValue(state, out List<SearchVisitVariant>? variants))
		{
			variants = workspace.RentVariantList(maxVariantsPerState);
			variantsByState[state] = variants;
		}

		if (variants.Count < maxVariantsPerState)
		{
			variants.Add(new SearchVisitVariant(cost, visitID));
			activeVisitIDs.Add(visitID);
			return true;
		}

		int worstIndex = 0;
		double worst = variants[0].Cost;
		for (int variantIndex = 1; variantIndex < variants.Count; variantIndex++)
		{
			if (variants[variantIndex].Cost > worst)
			{
				worst = variants[variantIndex].Cost;
				worstIndex = variantIndex;
			}
		}

		// The bucket is full (my god!). Keep the best bounded variants discovered thus far,
		// equal-cost variants are admitted until the bucket fills, but not beyond it.
		if (cost >= worst - epsilon) return false;

		activeVisitIDs.Remove(variants[worstIndex].VisitID);
		variants[worstIndex] = new SearchVisitVariant(cost, visitID);
		activeVisitIDs.Add(visitID);
		return true;
	}

	private static bool TryRegisterStateExpansion(Dictionary<SearchState, int> expandedVariantsByState, SearchState state, int maxVariantsPerState)
	{
		if (maxVariantsPerState <= 0) maxVariantsPerState = 1;

		if (!expandedVariantsByState.TryGetValue(state, out int count)) { expandedVariantsByState[state] = 1; return true; }

		if (count >= maxVariantsPerState) return false;
		expandedVariantsByState[state] = count + 1;
		return true;
	}

	private bool TryBuildRouteFromCame
	(
		SearchState start, SearchState found, StationPathTarget foundTarget, List<ulong> approachEdges,
		Dictionary<SearchState, PathCame> came, List<RailPathArc> reversedArcs, out RailAutomationRoute route
	)
	{
		route = default!;
		reversedArcs.Clear();
		SearchState currentState = found;
		while (!currentState.Equals(start))
		{
			if (!came.TryGetValue(currentState, out PathCame predecessor)) return false;
			reversedArcs.Add(Arcs[predecessor.ArcIndex]);
			currentState = predecessor.Previous;
		}

		reversedArcs.Reverse();

		route = RailAutomationRoute.FromArcs(this, foundTarget, start, approachEdges, reversedArcs);
		return true;
	}

	private bool TryBuildRouteFromVisits
	(
		SearchState start, int foundVisitID, StationPathTarget foundTarget, List<ulong> approachEdges,
		List<SearchVisit> visits, List<RailPathArc> reversedArcs, out RailAutomationRoute route
	)
	{
		route = default!;
		reversedArcs.Clear();
		int visitID = foundVisitID;
		for (int guard = 0; guard < visits.Count; guard++)
		{
			if ((uint)visitID >= (uint)visits.Count) return false;

			SearchVisit visit = visits[visitID];
			if (visit.State.Equals(start))
			{
				reversedArcs.Reverse();
				route = RailAutomationRoute.FromArcs(this, foundTarget, start, approachEdges, reversedArcs);
				return true;
			}

			if (visit.PreviousVisitID < 0 || visit.ArcIndex < 0) return false;
			reversedArcs.Add(Arcs[visit.ArcIndex]);
			visitID = visit.PreviousVisitID;
		}

		return false;
	}

	private bool TryFindDirectForwardTarget
	(
		RailGraphLive graph, in RailwayVehicleShared.RailCursor cursor, IReadOnlyList<StationPathTarget> targets,
		List<ulong> approachEdges, out StationPathTarget target
	)
	{
		target = default;
		approachEdges.Clear();

		if (cursor.SegmentHash == 0) return false;
		if (!graph.TryGetEdgeEndpoints(cursor.SegmentHash, out RailGraphLive.EndpointKey firstEndpoint, out RailGraphLive.EndpointKey secondEndpoint, out byte gauge) || gauge != cursor.Gauge)
		{
			return false;
		}

		ulong edge = cursor.SegmentHash;
		RailGraphLive.EndpointKey endpoint = cursor.Direction >= 0 ? secondEndpoint : firstEndpoint;
		approachEdges.Add(edge);

		for (int guard = 0; guard < 4096; guard++)
		{
			if (TryFindTargetAtEndpoint(targets, endpoint, edge, out target)) return true;
			if (NodeByEndpoint.ContainsKey(endpoint)) return false;

			if (!TryGetSingleForwardContinuation(graph, endpoint, gauge, edge, out ulong nextEdge)) return false;
			if (!RailTransitionRules.IsDirectedTransitionAllowedSameGauge(graph, endpoint, gauge, edge, nextEdge)) return false;
			if (!graph.TryGetOtherEndpoint(nextEdge, endpoint, out RailGraphLive.EndpointKey nextEndpoint)) return false;

			edge = nextEdge;
			approachEdges.Add(edge);
			endpoint = nextEndpoint;
		}

		return false;
	}

	private bool TryFindDirectForwardTargetFromStation( RailGraphLive graph, StationPathTarget from, IReadOnlyList<StationPathTarget> targets, List<ulong> approachEdges, out StationPathTarget target)
	{
		target = default;
		approachEdges.Clear();

		ulong edge = from.DepartEdgeHash;
		if (edge == 0 || !graph.EdgeTouchesEndpoint(edge, from.StopEndpoint)) return false;
		if (!graph.TryGetEdgeGauge(edge, out byte gauge) || gauge != from.Gauge) return false;
		if (!graph.TryGetOtherEndpoint(edge, from.StopEndpoint, out RailGraphLive.EndpointKey endpoint)) return false;

		approachEdges.Add(edge);

		for (int guard = 0; guard < 4096; guard++)
		{
			if (TryFindTargetAtEndpoint(targets, endpoint, edge, out target)) return true;
			if (NodeByEndpoint.ContainsKey(endpoint)) return false;

			if (!TryGetSingleForwardContinuation(graph, endpoint, gauge, edge, out ulong nextEdge)) return false;
			if (!RailTransitionRules.IsDirectedTransitionAllowedSameGauge(graph, endpoint, gauge, edge, nextEdge)) return false;
			if (!graph.TryGetOtherEndpoint(nextEdge, endpoint, out RailGraphLive.EndpointKey nextEndpoint)) return false;

			edge = nextEdge;
			approachEdges.Add(edge);
			endpoint = nextEndpoint;
		}

		return false;
	}

	private static bool TryFindTargetAtEndpoint(IReadOnlyList<StationPathTarget> targets, RailGraphLive.EndpointKey endpoint, ulong arrivedViaEdge, out StationPathTarget target)
	{
		for (int terminalEdgeIndex = 0; terminalEdgeIndex < targets.Count; terminalEdgeIndex++)
		{
			StationPathTarget candidate = targets[terminalEdgeIndex];
			if (candidate.StopEndpoint.Equals(endpoint) && candidate.ApproachEdgeHash == arrivedViaEdge) { target = candidate; return true; }
		}

		target = default;
		return false;
	}

	private bool TryResolveForwardStart(RailGraphLive graph, in RailwayVehicleShared.RailCursor cursor, List<ulong> approachEdges, out SearchState state)
	{
		approachEdges.Clear();
		state = default;
		if (cursor.SegmentHash == 0) return false;
		if (!graph.TryGetEdgeEndpoints(cursor.SegmentHash, out RailGraphLive.EndpointKey firstEndpoint, out RailGraphLive.EndpointKey secondEndpoint, out byte gauge) || gauge != cursor.Gauge)
		{
			return false;
		}

		ulong edge = cursor.SegmentHash;
		approachEdges.Add(edge);
		RailGraphLive.EndpointKey endpoint = cursor.Direction >= 0 ? secondEndpoint : firstEndpoint;

		for (int guard = 0; guard < 4096; guard++)
		{
			if (NodeByEndpoint.TryGetValue(endpoint, out int nodeID))
			{
				state = new SearchState(nodeID, edge);
				return true;
			}

			if (!TryGetSingleForwardContinuation(graph, endpoint, gauge, edge, out ulong nextEdge)) return false;
			if (!RailTransitionRules.IsDirectedTransitionAllowedSameGauge(graph, endpoint, gauge, edge, nextEdge)) return false;
			if (!graph.TryGetOtherEndpoint(nextEdge, endpoint, out RailGraphLive.EndpointKey nextEndpoint)) return false;

			edge = nextEdge;
			approachEdges.Add(edge);
			endpoint = nextEndpoint;
		}

		return false;
	}

	private bool TryResolveForwardStartFromStation(RailGraphLive graph, StationPathTarget from, List<ulong> approachEdges, out SearchState state)
	{
		approachEdges.Clear();
		state = default;

		ulong edge = from.DepartEdgeHash;
		if (edge == 0 || !graph.EdgeTouchesEndpoint(edge, from.StopEndpoint)) return false;
		if (!graph.TryGetEdgeGauge(edge, out byte gauge) || gauge != from.Gauge) return false;
		if (!graph.TryGetOtherEndpoint(edge, from.StopEndpoint, out RailGraphLive.EndpointKey endpoint)) return false;

		approachEdges.Add(edge);

		for (int guard = 0; guard < 4096; guard++)
		{
			if (NodeByEndpoint.TryGetValue(endpoint, out int nodeID)) { state = new SearchState(nodeID, edge); return true; }

			if (!TryGetSingleForwardContinuation(graph, endpoint, gauge, edge, out ulong nextEdge)) return false;
			if (!RailTransitionRules.IsDirectedTransitionAllowedSameGauge(graph, endpoint, gauge, edge, nextEdge)) return false;
			if (!graph.TryGetOtherEndpoint(nextEdge, endpoint, out RailGraphLive.EndpointKey nextEndpoint)) return false;

			edge = nextEdge;
			approachEdges.Add(edge);
			endpoint = nextEndpoint;
		}

		return false;
	}

	private static bool TryGetSingleForwardContinuation(
		RailGraphLive graph,
		RailGraphLive.EndpointKey endpoint,
		byte gauge,
		ulong fromEdge,
		out ulong nextEdge)
	{
		nextEdge = 0;
		if (!graph.TryGetIncidentEdges(endpoint, out List<ulong> incidentEdges)) return false;

		int count = 0;
		for (int incidentEdgeIndex = 0; incidentEdgeIndex < incidentEdges.Count; incidentEdgeIndex++)
		{
			ulong edgeHash = incidentEdges[incidentEdgeIndex];
			if (edgeHash == fromEdge) continue;
			if (!graph.TryGetEdgeGauge(edgeHash, out byte edgeGauge) || edgeGauge != gauge) continue;
			nextEdge = edgeHash;
			count++;
		}

		return count == 1 && nextEdge != 0;
	}

	private static bool TryBuildArc
	(
		RailGraphLive graph, Dictionary<RailGraphLive.EndpointKey, int> nodeMap, RailGraphLive.EndpointKey start,
		int fromNode, ulong firstEdge, HashSet<ulong>? allowedEdges, out RailPathArc arc
	)
	{
		arc = default;

		if (allowedEdges != null && !allowedEdges.Contains(firstEdge)) return false;
		if (!graph.TryGetEdgeEndpoints(firstEdge, out _, out _, out byte gauge)) return false;

		RailGraphLive.EndpointKey currentEndpoint = start;
		ulong currentEdge = firstEdge;
		List<ulong> chain = new(8);
		double length = 0;

		for (int guard = 0; guard < 4096; guard++)
		{
			if (!graph.TryGetOtherEndpoint(currentEdge, currentEndpoint, out RailGraphLive.EndpointKey nextEndpoint)) return false;

			chain.Add(currentEdge);
			length += graph.GetEdgeLength(currentEdge);

			if (nodeMap.TryGetValue(nextEndpoint, out int toNode))
			{
				if (toNode == fromNode && chain.Count == 1) return false; // self-loop edge is not useful for automation for now
				ulong zoneID = 0;
				graph.TryGetOccupancyZoneID(firstEdge, out zoneID);

				arc = new RailPathArc(-1, fromNode, toNode, gauge, firstEdge, currentEdge, zoneID, length, chain.ToArray());
				return true;
			}

			if (!graph.TryGetIncidentEdges(nextEndpoint, out List<ulong> incident) || incident.Count != 2) return false;

			ulong nextEdge = incident[0] == currentEdge ? incident[1] : incident[0];
			if (allowedEdges != null && !allowedEdges.Contains(nextEdge)) return false;
			if (!graph.TryGetEdgeGauge(nextEdge, out byte nextEdgeGauge) || nextEdgeGauge != gauge) return false;
			if (!RailTransitionRules.IsDirectedTransitionAllowedSameGauge(graph, nextEndpoint, gauge, currentEdge, nextEdge)) return false;

			currentEndpoint = nextEndpoint;
			currentEdge = nextEdge;
		}

		return false;
	}

	private static int AddNode(RailGraphLive graph, Dictionary<RailGraphLive.EndpointKey, int> nodeMap, List<RailPathNode> nodes, RailGraphLive.EndpointKey endpoint)
	{
		if (nodeMap.TryGetValue(endpoint, out int nodeID)) return nodeID;

		nodeID = nodes.Count;
		nodeMap[endpoint] = nodeID;
		nodes.Add(new RailPathNode(nodeID, endpoint, graph.IsSignalEndpoint(endpoint)));
		return nodeID;
	}

	private static ulong ChooseApproachEdge(RailGraphLive graph, RailGraphLive.EndpointKey stop, byte gauge, string rotationCode, List<ulong> incident)
	{
		GetApproachVector(gauge, rotationCode, out double wantX, out double wantZ);

		ulong best = 0;
		double bestDot = double.NegativeInfinity;

		for (int incidentEdgeIndex = 0; incidentEdgeIndex < incident.Count; incidentEdgeIndex++)
		{
			ulong edge = incident[incidentEdgeIndex];
			if (!graph.TryGetEdgeGauge(edge, out byte candidateGauge) || candidateGauge != gauge) continue;
			if (!graph.TryGetOtherEndpoint(edge, stop, out RailGraphLive.EndpointKey other)) continue;

			double dx = other.X16 - stop.X16;
			double dz = other.Z16 - stop.Z16;
			double vectorLengthSQ = dx * dx + dz * dz;
			if (vectorLengthSQ <= 1e-6) continue;

			double inverseLength = 1.0 / Math.Sqrt(vectorLengthSQ);
			double dot = dx * inverseLength * wantX + dz * inverseLength * wantZ;
			if (dot > bestDot + 1e-9 || (Math.Abs(dot - bestDot) <= 1e-9 && (best == 0 || edge < best)))
			{
				bestDot = dot;
				best = edge;
			}
		}

		return best;
	}

	private static void GetApproachVector(byte gauge, string rotationCode, out double x, out double z)
	{
		rotationCode = (rotationCode ?? "").ToLowerInvariant();

		// Signals generally have the same vectors as one-way signals.
		if (gauge == 0)
		{
			switch (rotationCode)
			{
				case "ne": x = 0;	 z = 1; return;
				case "sw": x = 0;	 z = -1; return;
				case "es": x = -1;	 z = 0; return;
				case "wn": x = 1;	 z = 0; return;
			}
		}
		else
		{
			switch (rotationCode)
			{
				case "ne": x = -1;	 z = 0; return;
				case "sw": x = 1;	 z = 0; return;
				case "es": x = 0;	 z = -1; return;
				case "wn": x = 0;	 z = 1; return;
			}
		}

		x = 1;
		z = 0;
	}

	private double HeuristicMetersToTargets(int nodeID, IReadOnlyList<StationPathTarget> targets)
	{
		Vec3d from = Nodes[nodeID].Endpoint.ToWorld();
		double best = double.PositiveInfinity;
		for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
		{
			Vec3d to = targets[targetIndex].StopEndpoint.ToWorld();
			double dx = from.X - to.X;
			double dz = from.Z - to.Z;
			double distance = Math.Sqrt(dx * dx + dz * dz);
			if (distance < best) best = distance;
		}

		return double.IsPositiveInfinity(best) ? 0 : best;
	}

	// Prevent automation from jumping from one diverging branch directly onto another at a node.
	// This is precomputed once per RailPathIndex so pathfinding expansion only pays a quick hash lookup.
	// The gauge-specific angle limits live in RailTransitionRules so routing and movement agree.
	private HashSet<DirectedTransitionKey> PrecomputeBlockedDirectedTransitions(RailGraphLive graph)
	{
		HashSet<DirectedTransitionKey> blocked = new();

		for (int nodeID = 0; nodeID < Nodes.Count; nodeID++)
		{
			RailPathNode node = Nodes[nodeID];

			List<RailPathArc> outgoing = ArcsByNode[nodeID];
			if (outgoing.Count == 0) continue;
			if (!graph.TryGetIncidentEdges(node.Endpoint, out List<ulong> incidentEdges) || incidentEdges.Count < 2) continue;

			for (int incidentEdgeIndex = 0; incidentEdgeIndex < incidentEdges.Count; incidentEdgeIndex++)
			{
				ulong fromEdgeHash = incidentEdges[incidentEdgeIndex];
				if (!graph.TryGetEdgeGauge(fromEdgeHash, out byte fromGauge) || fromGauge != node.Endpoint.Gauge) continue;

				for (int outgoingArcIndex = 0; outgoingArcIndex < outgoing.Count; outgoingArcIndex++)
				{
					RailPathArc arc = outgoing[outgoingArcIndex];
					if (arc.Gauge != node.Endpoint.Gauge) continue;

					ulong toEdgeHash = arc.FirstEdgeHash;
					if (fromEdgeHash == toEdgeHash) continue;

					if (!RailTransitionRules.IsDirectedTransitionAllowedSameGauge(graph, node.Endpoint, fromGauge, fromEdgeHash, toEdgeHash))
					{
						blocked.Add(new DirectedTransitionKey(nodeID, fromEdgeHash, toEdgeHash));
					}
				}
			}
		}

		return blocked;
	}

	private bool IsDirectedTransitionBlocked(int nodeID, ulong fromEdgeHash, ulong toEdgeHash)
	{
		return fromEdgeHash != 0
			&& toEdgeHash != 0
			&& BlockedDirectedTransitions.Count != 0
			&& BlockedDirectedTransitions.Contains(new DirectedTransitionKey(nodeID, fromEdgeHash, toEdgeHash));
	}

	private sealed class BasicSearchWorkspace
	{
		internal readonly PriorityQueue<SearchState, double> Open = new();
		internal readonly Dictionary<SearchState, PathCame> Came = new();
		internal readonly Dictionary<SearchState, double> BestCost = new();
		internal readonly HashSet<SearchState> Closed = new();
		internal readonly List<RailPathArc> ReverseArcs = new(32);

		internal void Reset()
		{
			Open.Clear();
			Came.Clear();
			BestCost.Clear();
			Closed.Clear();
			ReverseArcs.Clear();
		}
	}

	private sealed class AcceptedSearchWorkspace
	{
		internal readonly PriorityQueue<int, double> Open = new();
		internal readonly List<SearchVisit> Visits = new(256);
		internal readonly Dictionary<SearchState, List<SearchVisitVariant>> ActiveVariantsByState = new();
		internal readonly Dictionary<SearchState, int> ExpandedVariantsByState = new();
		internal readonly HashSet<int> ActiveVisitIDs = new();
		internal readonly List<RailPathArc> ReverseArcs = new(32);
		private readonly Stack<List<SearchVisitVariant>> VariantListPool = new();

		internal List<SearchVisitVariant> RentVariantList(int capacity)
		{
			if (VariantListPool.Count == 0) return new List<SearchVisitVariant>(capacity);
			List<SearchVisitVariant> list = VariantListPool.Pop();
			if (list.Capacity < capacity) list.Capacity = capacity;
			return list;
		}

		internal void Reset()
		{
			Open.Clear();
			Visits.Clear();
			foreach (List<SearchVisitVariant> variants in ActiveVariantsByState.Values)
			{
				variants.Clear();
				VariantListPool.Push(variants);
			}
			ActiveVariantsByState.Clear();
			ExpandedVariantsByState.Clear();
			ActiveVisitIDs.Clear();
			ReverseArcs.Clear();
		}
	}

	private readonly record struct ArcKey(int FromNode, ulong FirstEdgeHash);
	private readonly record struct DirectedTransitionKey(int NodeID, ulong FromEdgeHash, ulong ToEdgeHash);
	private readonly record struct StationTargetCacheKey(RailGraphLive.EndpointKey StopEndpoint, ulong ApproachEdgeHash, ulong DepartEdgeHash, byte Gauge);
	private readonly record struct StationTargetGeometry(int TerminalNodeID, ulong TerminalEntryEdgeHash, ulong[] TerminalEdges, double TerminalLengthMeters, ulong TerminalZoneID);
	private readonly record struct PathCame(SearchState Previous, int ArcIndex);
	private readonly record struct SearchVisit(SearchState State, double Cost, int PreviousVisitID, int ArcIndex);
	private readonly record struct SearchVisitVariant(double Cost, int VisitID);
	internal readonly record struct SearchState(int NodeID, ulong ArrivedViaEdgeHash);

	internal readonly record struct RailPathNode(int NodeID, RailGraphLive.EndpointKey Endpoint, bool IsSignal);
}

internal readonly record struct StationPathTarget
(
	RailStationRegistrySystem.StationBlockKey StationKey, int TerminalNodeID, RailGraphLive.EndpointKey StopEndpoint, ulong ApproachEdgeHash,
	ulong DepartEdgeHash, byte Gauge, string DisplayName, ulong TerminalEntryEdgeHash, ulong[] TerminalEdges, double TerminalLengthMeters, ulong TerminalZoneID
)
{
	internal bool SamePhysicalStop(StationPathTarget other)
	{
		return StationKey.Equals(other.StationKey)
			&& StopEndpoint.Equals(other.StopEndpoint)
			&& ApproachEdgeHash == other.ApproachEdgeHash
			&& DepartEdgeHash == other.DepartEdgeHash
			&& Gauge == other.Gauge;
	}
}

internal readonly record struct RailPathArc
{
	public readonly int Index;
	public readonly int FromNode;
	public readonly int ToNode;
	public readonly byte Gauge;
	public readonly ulong FirstEdgeHash;
	public readonly ulong LastEdgeHash;
	public readonly ulong ZoneID;
	public readonly double LengthMeters;
	public readonly ulong[] Edges;

	public RailPathArc(int index, int fromNode, int toNode, byte gauge, ulong firstEdgeHash, ulong lastEdgeHash, ulong zoneID, double lengthMeters, ulong[] edges)
	{
		Index = index;
		FromNode = fromNode;
		ToNode = toNode;
		Gauge = gauge;
		FirstEdgeHash = firstEdgeHash;
		LastEdgeHash = lastEdgeHash;
		ZoneID = zoneID;
		LengthMeters = lengthMeters;
		Edges = edges ?? Array.Empty<ulong>();
	}

	public RailPathArc WithIndex(int index) => new(index, FromNode, ToNode, Gauge, FirstEdgeHash, LastEdgeHash, ZoneID, LengthMeters, Edges);
}

internal sealed class RailAutomationRoute
{
	internal readonly StationPathTarget Target;
	internal readonly RailPathIndex.SearchState Start;
	internal readonly ulong[] ApproachEdges;
	internal readonly RailPathArc[] Arcs;
	internal readonly RailExactTurnPlan TurnPlan;

	private RailAutomationRoute(RailPathIndex index, StationPathTarget target, RailPathIndex.SearchState start, List<ulong> approachEdges, RailPathArc[] arcs)
	{
		Target = target;
		Start = start;
		ApproachEdges = approachEdges?.ToArray() ?? Array.Empty<ulong>();
		Arcs = arcs;
		TurnPlan = BuildTurnPlan(index, target, start, arcs);
	}

	internal static RailAutomationRoute Empty(RailPathIndex index, StationPathTarget target, RailPathIndex.SearchState start, List<ulong> approachEdges)
	{
		return new RailAutomationRoute(index, target, start, approachEdges, Array.Empty<RailPathArc>());
	}

	internal static RailAutomationRoute FromArcs(RailPathIndex index, StationPathTarget target, RailPathIndex.SearchState start, List<ulong> approachEdges, List<RailPathArc> source)
	{
		return new RailAutomationRoute(index, target, start, approachEdges, source.ToArray());
	}


	internal void CollectEdgeHashes(HashSet<ulong> destination, bool clear = true)
	{
		if (destination == null) return;
		if (clear) destination.Clear();

		if (Target.ApproachEdgeHash != 0) destination.Add(Target.ApproachEdgeHash);
		if (Start.NodeID >= 0)
		{
			for (int terminalEdgeIndex = 0; terminalEdgeIndex < Target.TerminalEdges.Length; terminalEdgeIndex++)
			{
				if (Target.TerminalEdges[terminalEdgeIndex] != 0) destination.Add(Target.TerminalEdges[terminalEdgeIndex]);
			}
		}

		for (int approachEdgeIndex = 0; approachEdgeIndex < ApproachEdges.Length; approachEdgeIndex++)
		{
			if (ApproachEdges[approachEdgeIndex] != 0) destination.Add(ApproachEdges[approachEdgeIndex]);
		}

		for (int arcIndex = 0; arcIndex < Arcs.Length; arcIndex++)
		{
			ulong[] edges = Arcs[arcIndex].Edges;
			for (int edgeIndex = 0; edgeIndex < edges.Length; edgeIndex++)
			{
				if (edges[edgeIndex] != 0) destination.Add(edges[edgeIndex]);
			}
		}
	}

	internal bool TouchesGraphChange(RailGraphLive graph, RailGraphChangeSet change)
	{
		if (change == null || !change.HasAnyChange) return false;
		if (change.GlobalInvalidation) return true;

		if (Target.ApproachEdgeHash != 0 && change.ContainsEdge(Target.ApproachEdgeHash)) return true;
		if (change.ContainsEndpoint(Target.StopEndpoint)) return true;
		if (Start.NodeID >= 0)
		{
			if (AnyEdgeTouched(Target.TerminalEdges, change)) return true;
			if (AnyRouteEdgeEndpointTouched(graph, Target.TerminalEdges, change)) return true;
		}

		if (AnyEdgeTouched(ApproachEdges, change)) return true;
		if (AnyRouteEdgeEndpointTouched(graph, ApproachEdges, change)) return true;

		for (int arcIndex = 0; arcIndex < Arcs.Length; arcIndex++)
		{
			RailPathArc arc = Arcs[arcIndex];

			if (change.ContainsEdge(arc.FirstEdgeHash)) return true;
			if (change.ContainsEdge(arc.LastEdgeHash)) return true;

			if (AnyEdgeTouched(arc.Edges, change)) return true;
			if (AnyRouteEdgeEndpointTouched(graph, arc.Edges, change)) return true;
		}

		return false;
	}

	private static bool AnyEdgeTouched(ulong[] edges, RailGraphChangeSet change)
	{
		if (edges == null) return false;

		for (int edgeIndex = 0; edgeIndex < edges.Length; edgeIndex++) { if (change.ContainsEdge(edges[edgeIndex])) return true; }
		return false;
	}

	private static bool AnyRouteEdgeEndpointTouched(RailGraphLive graph, ulong[] edges, RailGraphChangeSet change)
	{
		if (edges == null || change.TouchedEndpoints.Count == 0) return false;

		for (int edgeIndex = 0; edgeIndex < edges.Length; edgeIndex++)
		{
			ulong edge = edges[edgeIndex];
			if (edge == 0) continue;

			for (int endpointIndex = 0; endpointIndex < change.TouchedEndpoints.Count; endpointIndex++)
			{
				if (graph.EdgeTouchesEndpoint(edge, change.TouchedEndpoints[endpointIndex])) return true;
			}
		}

		return false;
	}


	internal bool IsAtTarget(RailGraphLive graph, in RailwayVehicleShared.RailCursor cursor)
	{
		if (cursor.SegmentHash != Target.ApproachEdgeHash) return false;
		if (cursor.PolyXYZ16 == null || cursor.PointCount < 2) return false;
		if (!graph.TryGetEdgeEndpoints(cursor.SegmentHash, out var firstEndpoint, out var secondEndpoint, out _)) return false;

		if (Target.StopEndpoint.Equals(firstEndpoint)) return cursor.SegmentIndex == 0 && cursor.NormalizedSegmentProgress <= 0.18;
		if (Target.StopEndpoint.Equals(secondEndpoint)) return cursor.SegmentIndex >= cursor.PointCount - 2 && cursor.NormalizedSegmentProgress >= 0.82;
		return false;
	}

	private static RailExactTurnPlan BuildTurnPlan(RailPathIndex index, StationPathTarget target, RailPathIndex.SearchState start, RailPathArc[] arcs)
	{
		RailExactTurnPlan plan = new();
		ulong fromEdge = start.ArrivedViaEdgeHash;

		for (int arcIndex = 0; arcIndex < arcs.Length; arcIndex++)
		{
			RailPathArc arc = arcs[arcIndex];
			if (fromEdge != 0 && fromEdge != arc.FirstEdgeHash) { plan.SetTurn(fromEdge, index.GetNodeEndpoint(arc.FromNode), arc.FirstEdgeHash); }

			fromEdge = arc.LastEdgeHash;
		}

		// For a virtual terminal, the final route segment branches from the last real topology node onto the station corridor.
		// Direct-in-corridor routes use a synthetic start state and need no junction decision here~
		if (start.NodeID >= 0 &&
			target.TerminalEdges.Length > 0 &&
			fromEdge != 0 &&
			fromEdge != target.TerminalEntryEdgeHash)
		{
			plan.SetTurn(fromEdge, index.GetNodeEndpoint(target.TerminalNodeID), target.TerminalEntryEdgeHash);
		}

		// The target station is a hard stop because I can't figure out how brake physics work.
		// Do not let normal edge stepping continue through the marker. AdvanceAlongTrack() will stop exactly at this endpoint (for now).
		plan.SetStop(target.ApproachEdgeHash, target.StopEndpoint);
		return plan;
	}

}
#endregion

#region Invalidation Optimization
internal sealed class RailGraphChangeSet /// Used to invalidate only cached automation routes that actually overlap the edit.
{
	internal int OldBuildVersion;
	internal int NewBuildVersion;

	internal bool GlobalInvalidation;

	internal readonly List<ulong> TouchedEdges = new(16);
	internal readonly List<ulong> AddedEdges = new(8);
	internal readonly List<ulong> RemovedEdges = new(8);
	internal readonly List<ulong> ZoneReassignedEdges = new(16);
	internal readonly List<RailGraphLive.EndpointKey> TouchedEndpoints = new(16);
	internal readonly List<RailGraphLive.EndpointKey> TouchedSignals = new(8);
	internal readonly List<ulong> TouchedOccupancyZones = new(8);

	internal bool HasZoneTopologyChange =>
		GlobalInvalidation ||
		AddedEdges.Count != 0 ||
		RemovedEdges.Count != 0 ||
		TouchedSignals.Count != 0;

	internal bool HasAnyChange =>
		GlobalInvalidation ||
		TouchedEdges.Count != 0 ||
		TouchedEndpoints.Count != 0 ||
		TouchedOccupancyZones.Count != 0 ||
		TouchedSignals.Count != 0;

	internal void TouchEdge(ulong edgeHash) { if (edgeHash != 0) TouchedEdges.Add(edgeHash); }

	internal void AddEdge(ulong edgeHash)
	{
		if (edgeHash == 0) return;
		AddedEdges.Add(edgeHash);
		TouchedEdges.Add(edgeHash);
	}

	internal void RemoveEdge(ulong edgeHash)
	{
		if (edgeHash == 0) return;
		RemovedEdges.Add(edgeHash);
		TouchedEdges.Add(edgeHash);
	}

	internal void ReassignZoneEdge(ulong edgeHash) { if (edgeHash != 0) ZoneReassignedEdges.Add(edgeHash); }

	internal void TouchEndpoint(RailGraphLive.EndpointKey endpoint) { TouchedEndpoints.Add(endpoint); }

	internal void TouchSignal(RailGraphLive.EndpointKey endpoint)
	{
		TouchedSignals.Add(endpoint);
		TouchedEndpoints.Add(endpoint);
	}

	internal void TouchZone(ulong zoneID) { if (zoneID != 0) TouchedOccupancyZones.Add(zoneID); }

	internal void Canonicalize()
	{
		SortUnique(TouchedEdges);
		SortUnique(AddedEdges);
		SortUnique(RemovedEdges);
		SortUnique(ZoneReassignedEdges);

		SortUnique(TouchedEndpoints);
		SortUnique(TouchedSignals);
		SortUnique(TouchedOccupancyZones);
	}

	internal bool ContainsEdge(ulong edgeHash) { return edgeHash != 0 && TouchedEdges.BinarySearch(edgeHash) >= 0; }

	internal bool ContainsEndpoint(RailGraphLive.EndpointKey endpoint) { return TouchedEndpoints.BinarySearch(endpoint) >= 0; }

	internal bool ContainsZone(ulong zoneID) { return zoneID != 0 && TouchedOccupancyZones.BinarySearch(zoneID) >= 0; }

	private static void SortUnique(List<ulong> list)
	{
		list.Sort();
		for (int duplicateIndex = list.Count - 1; duplicateIndex > 0; duplicateIndex--)
		{
			if (list[duplicateIndex] == list[duplicateIndex - 1]) list.RemoveAt(duplicateIndex);
		}
	}

	private static void SortUnique(List<RailGraphLive.EndpointKey> list)
	{
		list.Sort();
		for (int duplicateIndex = list.Count - 1; duplicateIndex > 0; duplicateIndex--)
		{
			if (list[duplicateIndex].Equals(list[duplicateIndex - 1])) list.RemoveAt(duplicateIndex);
		}
	}
}
#endregion
