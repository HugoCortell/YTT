using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using static YangTransport.RailwayVehicleShared;

namespace YangTransport;

/// Simulation layer (offscreen traversal), allows vehicles to keep simulating even when outside of the loaded world.
/// Uses a low fidelity approximation to keep it scalable. We only care about ensuring it can keep navigating and that the fuel/liquid is consumed.
/// * Capture supported rail vehicles/convoys when the server unloads them.
/// * Simulate one lightweight authority cursor / SG body path tape along the rail graph without chunk forcing.
/// * Materialize real entities once every target source chunk is loaded again.
internal sealed class OffscreenConvoySimSystem : ModSystem
{
	private const string SaveKey = "yangtransport_offscreenstate";
	private const int MaxSavedConvoys = 1_000_000;
	private const int SaveDirectoryEntryBytes = sizeof(int) * 2;

	private const int VirtualWakeTickMS						= 100;
	private const int MinActiveVirtualStepMS				= 25;
	private const int MovingVirtualTickMS					= 2000;
	private const int WaitingVirtualTickMS					= 10000;
	private const int PathBudgetRetryMS						= 10000;
	private const int MaxVirtualPositionAgeMS				= 30000;
	private const int VirtualFreshnessHeadroomMS			= 5000;
	private const int VirtualSchedulerBurstAllowance		= 8;
	private const int MaterializationRetryMS				= 1000;
	private const int PendingFinalizeTickMS					= 200;
	private const long PendingTimeoutMS						= 5000;

	private const double MinRollingDeceleration				= 0.05;
	private const double MaxPoweredOffscreenSeconds			= 20 * 60;
	private const double MaxCoastOffscreenSeconds			= 180;

	private const double AutomationCruiseEfficiency								= 0.80;
	private const double AutomationMinCruiseSpeedBPS							= 0.25;
	private const double AutomationMaxSliceSeconds								= 5.0;
	private const double MaxVirtualMovementStepSeconds							= 30.0;
	private const int AutomationMaxSlicesPerSimulate							= 6;
	private const int MaxRejectedChainRouteCandidates							= 16;
	private const double AutomationFuelSecondsPerMinute							= 60.0;
	private const double MovementEpsilon										= 1e-8;
	private const double ZoneBoundaryProbeBlocks								= 0.001;
	private const double DefaultVirtualRollingResistanceConstant				= 0.35;
	private const double DefaultVirtualRollingResistanceSpeedCoefficient		= 0.08;
	private const int MaxSerializedRouteEntries									= 256;

	private ICoreServerAPI? ServerAPI;
	private RailGraphServerSystem? RailSystem;
	private RailConvoySystem? ConvoySystem;
	private RailConvoyAuthoritySystem? AuthoritySystem;
	private RailAutomationPathingSystem? AutomationSystem;
	private RailStationRegistrySystem? StationRegistry;
	private RailTrainCollisionSystem? CollisionSystem;

	private readonly Dictionary<long, VirtualConvoy> VirtualConvoys = new();
	private readonly PriorityQueue<VirtualWake, long> VirtualWakeQueue = new();
	private readonly HashSet<long> ScheduledVirtuals = new();
	private readonly HashSet<long> AutomatedVirtuals = new();
	private readonly Dictionary<long, HashSet<long>> MaterializationOwnersByColumn = new();
	private readonly HashSet<long> PendingLoadedMaterializationColumns = new();
	private readonly HashSet<long> MaterializationCandidatesScratch = new();
	private readonly Dictionary<RailGraphLive.EndpointKey, HashSet<long>> SignalWaiters = new();
	private readonly Dictionary<ulong, HashSet<long>> VirtualRouteOwnersByEdge = new();
	private readonly Dictionary<RailStationRegistrySystem.StationBlockKey, HashSet<long>> VirtualRouteOwnersByStation = new();
	private readonly Dictionary<ulong, HashSet<long>> VirtualStationWaitersByNameHash = new();
	private readonly Dictionary<RailStationRegistrySystem.StationBlockKey, HashSet<long>> VirtualStationWaitersByBlock = new();
	private readonly HashSet<long> AffectedVirtualRoutesScratch = new();
	private readonly Dictionary<long, PendingConvoy> PendingConvoys = new();

	private readonly List<long> VirtualHeadKeysScratch = new(256);
	private readonly List<long> WakeHeadKeysScratch = new(64);
	private readonly Vec3d WorldPoseScratch = new();
	private bool VirtualPersistenceDirty;
	private List<VirtualConvoy>? PendingLoadedVirtualConvoys;
	private readonly HashSet<long> InternalDespawns = new();
	private long PendingFinalizeListenerID;
	private long VirtualWakeListenerID;

	public override double ExecuteOrder() => 0.11;
	public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

	public override void Start(ICoreAPI coreAPI)
	{
		ServerAPI = coreAPI as ICoreServerAPI;
		if (ServerAPI == null) return;

		RailSystem = ServerAPI.ModLoader.GetModSystem<RailGraphServerSystem>();
		ConvoySystem = ServerAPI.ModLoader.GetModSystem<RailConvoySystem>();
		AuthoritySystem = ServerAPI.ModLoader.GetModSystem<RailConvoyAuthoritySystem>();
		AutomationSystem = ServerAPI.ModLoader.GetModSystem<RailAutomationPathingSystem>();
		StationRegistry = ServerAPI.ModLoader.GetModSystem<RailStationRegistrySystem>();
		CollisionSystem = ServerAPI.ModLoader.GetModSystem<RailTrainCollisionSystem>();

		ServerAPI.Event.SaveGameLoaded += OnSaveGameLoaded;
		ServerAPI.Event.GameWorldSave += OnGameWorldSave;
		ServerAPI.Event.ServerRunPhase(EnumServerRunPhase.GameReady, FinalizeLoadedVirtualState);
		ServerAPI.Event.OnEntityDespawn += OnEntityDespawn;
		ServerAPI.Event.OnEntityLoaded += OnEntityLoaded;
		ServerAPI.Event.ChunkColumnLoaded += OnChunkColumnLoaded;
		if (RailSystem != null)
		{
			RailSystem.SignalAuthorityChanged += OnSignalAuthorityChanged;
			RailSystem.ClearanceEdgeResolved += OnClearanceEdgeResolved;
		}
		if (StationRegistry != null) StationRegistry.Changed += OnStationRegistryChanged;

		PendingFinalizeListenerID = ServerAPI.Event.RegisterGameTickListener(OnPendingFinalizeTick, PendingFinalizeTickMS);
		VirtualWakeListenerID = ServerAPI.Event.RegisterGameTickListener(OnVirtualWakeTick, VirtualWakeTickMS);
	}

	public override void Dispose()
	{
		if (ServerAPI != null) Save();

		if (ServerAPI != null)
		{
			ServerAPI.Event.SaveGameLoaded -= OnSaveGameLoaded;
			ServerAPI.Event.GameWorldSave -= OnGameWorldSave;
			ServerAPI.Event.OnEntityDespawn -= OnEntityDespawn;
			ServerAPI.Event.OnEntityLoaded -= OnEntityLoaded;
			ServerAPI.Event.ChunkColumnLoaded -= OnChunkColumnLoaded;
			if (PendingFinalizeListenerID != 0) ServerAPI.Event.UnregisterGameTickListener(PendingFinalizeListenerID);
			if (VirtualWakeListenerID != 0) ServerAPI.Event.UnregisterGameTickListener(VirtualWakeListenerID);
			PendingFinalizeListenerID = 0;
			VirtualWakeListenerID = 0;
		}
		if (RailSystem != null)
		{
			RailSystem.SignalAuthorityChanged -= OnSignalAuthorityChanged;
			RailSystem.ClearanceEdgeResolved -= OnClearanceEdgeResolved;
		}
		if (StationRegistry != null) StationRegistry.Changed -= OnStationRegistryChanged;

		ClearVirtualRuntimeIndexes();
		AffectedVirtualRoutesScratch.Clear();
		base.Dispose();
	}


	internal void ReseedVirtualOccupancy()
	{
		if (RailSystem == null) return;
		RailGraphLive graph = RailSystem.Graph;
		foreach (VirtualConvoy convoy in VirtualConvoys.Values)
		{
			if (convoy.Orphaned) { convoy.RetainValidOccupancy(RailSystem, graph); }
			else if (!convoy.EnsureOccupancyCurrent(RailSystem, graph))
			{
				convoy.BecomeOrphaned(RailSystem, graph);
				MarkVirtualDirty(convoy.HeadKey);
			}
		}
	}

	// Independent offsets and bounded readers prevent a damaged record from consuming its neighbours.
	internal static byte[] WriteSaveRecords(int count, Action<int, BinaryWriter> writeRecord)
	{
		if (count < 0 || count > MaxSavedConvoys) throw new InvalidDataException("Invalid virtual convoy count.");
		using var stream = new MemoryStream();
		using var writer = new BinaryWriter(stream);
		writer.Write(count);
		stream.SetLength(sizeof(int) + (long)count * SaveDirectoryEntryBytes);
		stream.Position = stream.Length;
		for (int index = 0; index < count; index++)
		{
			int start = checked((int)stream.Position);
			writeRecord(index, writer);
			int end = checked((int)stream.Position);
			stream.Position = sizeof(int) + (long)index * SaveDirectoryEntryBytes;
			writer.Write(start);
			writer.Write(end - start);
			stream.Position = end;
		}
		return stream.ToArray();
	}

	private static bool ReadSaveRecords(byte[] payload, Action<BinaryReader> readRecord, Action<int, Exception> rejectRecord)
	{
		using var directory = new MemoryStream(payload, writable: false);
		using var reader = new BinaryReader(directory);
		int count = reader.ReadInt32();
		int savedCount = count;
		if (payload.Length == sizeof(int)) count = 0;
		else if (payload.Length >= sizeof(int) + SaveDirectoryEntryBytes)
		{
			// The first record starts immediately after the directory.
			// Confirm its implied count against the last entry's end, so a damaged first offset cannot truncate the directory.
			int firstOffset = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(sizeof(int)));
			long directoryBytes = (long)firstOffset - sizeof(int);
			if
			(
				directoryBytes >= SaveDirectoryEntryBytes && directoryBytes % SaveDirectoryEntryBytes == 0 &&
				directoryBytes / SaveDirectoryEntryBytes <= MaxSavedConvoys && firstOffset <= payload.Length
			)
			{
				int lastOffset = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(firstOffset - SaveDirectoryEntryBytes));
				int lastLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(firstOffset - sizeof(int)));
				if (lastOffset >= firstOffset && lastLength > 0 && (long)lastOffset + lastLength == payload.Length)
				{
					count = (int)(directoryBytes / SaveDirectoryEntryBytes);
				}
			}
		}
		if (count < 0 || count > MaxSavedConvoys) throw new InvalidDataException("Invalid virtual convoy count.");
		long dataStart = sizeof(int) + (long)count * SaveDirectoryEntryBytes;
		if (dataStart > payload.Length) throw new InvalidDataException("Truncated virtual convoy directory.");
		for (int index = 0; index < count; index++)
		{
			int offset = reader.ReadInt32();
			int length = reader.ReadInt32();
			try
			{
				if (offset < dataStart || length <= 0 || (long)offset + length > payload.Length) throw new InvalidDataException("Invalid virtual convoy record bounds.");
				using var record = new MemoryStream(payload, offset, length, writable: false);
				using var recordReader = new BinaryReader(record);
				readRecord(recordReader);
			}
			catch (Exception exception) { rejectRecord(index, exception); }
		}
		return count != savedCount;
	}

	private void OnSaveGameLoaded()
	{
		if (ServerAPI == null) return;

		ResetVirtualLoadState();
		VirtualPersistenceDirty = false;

		byte[]? payload = ServerAPI.WorldManager.SaveGame.GetData(SaveKey);
		PendingLoadedVirtualConvoys = new();
		if (payload == null || payload.Length == 0) return;

		try
		{
			bool repairedCount = ReadSaveRecords(payload, binaryReader =>
			{
				VirtualConvoy convoy = VirtualConvoy.Deserialize(binaryReader);
				if (binaryReader.BaseStream.Position != binaryReader.BaseStream.Length) throw new InvalidDataException("Trailing virtual convoy data.");
				PendingLoadedVirtualConvoys.Add(convoy);
			}, (index, exception) =>
			{
				ServerAPI.Logger.Error("[yangtransport] Discarding unreadable virtual convoy record {0}: {1}", index, exception.Message);
				VirtualPersistenceDirty = true;
			});
			if (repairedCount)
			{
				ServerAPI.Logger.Warning("[yangtransport] Recovered virtual convoy count from the saved directory layout.");
				VirtualPersistenceDirty = true;
			}
		}
		catch (Exception exception)
		{
			ServerAPI.Logger.Error("[yangtransport] Offscreen convoy directory could not be read. Retaining all records recovered so far.");
			ServerAPI.Logger.Error(exception);
			VirtualPersistenceDirty = true;
		}
	}

	private void FinalizeLoadedVirtualState()
	{
		if (PendingLoadedVirtualConvoys == null) return;

		List<VirtualConvoy> convoys = PendingLoadedVirtualConvoys;
		PendingLoadedVirtualConvoys = null;

		InstallLoadedVirtuals(convoys);
	}

	private void ResetVirtualLoadState()
	{
		ClearVirtualRuntimeIndexes();
		ReleaseAllVirtualOccupancy();
		VirtualConvoys.Clear();
		VirtualRouteOwnersByEdge.Clear();
		PendingConvoys.Clear();
		PendingLoadedVirtualConvoys = null;
		InternalDespawns.Clear();
	}

	private void InstallLoadedVirtuals(List<VirtualConvoy> convoys)
	{
		if (ServerAPI == null || RailSystem == null)
			throw new InvalidOperationException("Rail graph was unavailable while loading virtual convoys.");

		RailGraphLive graph = RailSystem.Graph;
		long nowMS = ServerAPI.World.ElapsedMilliseconds;
		HashSet<long> retainedEntityIDs = new();

		for (int convoyIndex = 0; convoyIndex < convoys.Count; convoyIndex++)
		{
			VirtualConvoy convoy = convoys[convoyIndex];
			bool installing = false;
			try
			{
				ValidateLoadedConvoy(convoy);
				if (VirtualConvoys.ContainsKey(convoy.HeadKey)) throw new InvalidDataException("Duplicate virtual convoy head.");

				installing = true;
				// Validate fallback poses only when needed, a valid route reconstructs them during registration.
				if (convoy.Orphaned || convoy.PathTape == null || !convoy.PathTape.ValidateAuthoritative(graph, convoy.Gauge) || !convoy.EnsureOccupancyCurrent(RailSystem, graph))
				{
					if (convoy.FrozenPoses.Count != convoy.Vehicles.Count) throw new InvalidDataException("Incomplete fallback poses.");
					foreach (FrozenVehiclePose pose in convoy.FrozenPoses) RequireFinite(pose.X, pose.Y, pose.Z, pose.Yaw, pose.Roll);
					
					if (convoy.Orphaned) { convoy.RetainValidOccupancy(RailSystem, graph); } // Preserve the vehicles/cargo and use the existing derailed-materialization path.
					else
					{
						convoy.BecomeOrphaned(RailSystem, graph);
						VirtualPersistenceDirty = true;
					}
				}
				else
				{
					// The tape is authoritative. A damaged redundant cursor must not strand automation on the wrong component.
					if (!convoy.TrySampleVehicleCursor(graph, 0, out OffscreenRailCursor.Cursor cursor)) throw new InvalidDataException("Virtual route cannot locate its head vehicle.");
					if (!convoy.Cursor.Equals(cursor)) VirtualPersistenceDirty = true;
					convoy.Cursor = cursor;
				}

				VirtualConvoys.Add(convoy.HeadKey, convoy);
				RegisterVirtualRuntime(convoy, graph, nowMS);
				if (!convoy.MaterializationPoseValid) throw new InvalidDataException("Virtual convoy has no usable materialization poses.");
				foreach (VehicleSnapshot snapshot in convoy.Vehicles) retainedEntityIDs.Add(snapshot.EntityID);
			}
			catch (Exception exception)
			{
				// Never release by an unvalidated ID, a rejected duplicate can name a healthy convoy.
				if (installing)
				{
					convoy.ReleaseOccupancy(RailSystem);
					RemoveVirtualRuntime(convoy);
					VirtualConvoys.Remove(convoy.HeadKey);
				}
				ServerAPI.Logger.Error("[yangtransport] Discarding virtual convoy {0}: {1}", convoy.HeadKey, exception.Message);
				VirtualPersistenceDirty = true;
			}
		}
		// Reconcile by accepted entity IDs, not by IDs claimed by a corrupt record.
		// Runs at GameReady, before vanilla starts loading entities into the live world.
		AuthoritySystem?.ForgetUnrestoredVirtualAuthorities(retainedEntityIDs);
	}

	private void ValidateLoadedConvoy(VirtualConvoy convoy) // Startup only
	{
		if
		(
			convoy.HeadKey == 0 || convoy.Gauge > 1 || convoy.Vehicles.Count == 0 ||
			convoy.Vehicles[0].EntityID != convoy.HeadKey || (uint)convoy.LeadIndex >= (uint)convoy.Vehicles.Count
		) { throw new InvalidDataException("Invalid virtual convoy identity or lead vehicle."); }
			
		RequireFinite
		(
			convoy.InitialSpeed, convoy.DistanceSimulated, convoy.PathHeadDistance,
			convoy.RollingDeceleration, convoy.PoweredSeconds, convoy.StopSeconds,
			convoy.StartingAbsoluteDistanceTravelled, convoy.MaxElapsedSeconds, convoy.AbsoluteDistanceSimulated,
			convoy.ElapsedSimulationSec, convoy.TerminalElapsedSec
		);

		for (int index = 0; index < convoy.Vehicles.Count; index++)
		{
			VehicleSnapshot snapshot = convoy.Vehicles[index]; // Matching each canonical index also rejects duplicate members, without another identity set.
			if
			(
				snapshot.Gauge != convoy.Gauge || AuthoritySystem == null || !AuthoritySystem.TryGetAuthority(snapshot.EntityID, out RailEntityAuthority authority) ||
				authority.Mode != RailEntityAuthorityMode.Virtual || authority.HeadID != convoy.HeadKey ||
				authority.ConvoyIndex != index || authority.Generation < snapshot.AuthorityGeneration || authority.Generation == long.MaxValue
			) { throw new InvalidDataException($"Virtual authority does not match vehicle {snapshot.EntityID}."); }

			// A failed materialization can advance the authority generation while leaving the original snapshot intact.
			if (snapshot.AuthorityGeneration != authority.Generation)
			{
				snapshot.AuthorityGeneration = authority.Generation;
				VirtualPersistenceDirty = true;
			}

			RequireFinite
			(
				snapshot.DistanceBehindHead, snapshot.OccupancyRearExtent, snapshot.OccupancyFrontExtent,
				snapshot.RefrigerationPerishMultiplier, snapshot.RefrigerationCapacityAtCapture, snapshot.RefrigerationSecondsRemaining
			);
			snapshot.ValidateEntityForLoad(ServerAPI!);
		}
		if (convoy.PathTape != null && !convoy.PathTape.HasValidSavedRuns())
		{
			convoy.PathTape = null;
			VirtualPersistenceDirty = true;
		}
		if (convoy.Automation is VirtualAutomationState automation)
		{
			RequireFinite
			(
				automation.DriveMaxSpeedBPS, automation.FuelWaterSecondsAtCapture,
				automation.BurnConsumedSec, automation.CurrentSpeedBPS, automation.ArrivedStationElapsedSec
			);
		}
	}

	private static void RequireFinite(params ReadOnlySpan<double> values) { foreach (double value in values) if (!double.IsFinite(value)) throw new InvalidDataException("Non-finite virtual convoy state."); }

	private void OnGameWorldSave() { Save(); }

	private void Save()
	{
		if (ServerAPI == null) return;

		long nowMS = ServerAPI.World.ElapsedMilliseconds;
		foreach (VirtualConvoy virtualConvoy in VirtualConvoys.Values) { if (virtualConvoy.AdvanceRefrigeration(ServerAPI, nowMS)) VirtualPersistenceDirty = true; }

		if (!VirtualPersistenceDirty) return;

		try
		{
			List<long> convoyIDs = new(VirtualConvoys.Keys);
			convoyIDs.Sort();
		byte[] payload = WriteSaveRecords(convoyIDs.Count, (index, writer) => VirtualConvoys[convoyIDs[index]].Serialize(writer));
			ServerAPI.WorldManager.SaveGame.StoreData(SaveKey, payload);
			VirtualPersistenceDirty = false;
		}
		catch (Exception exception)
		{
			ServerAPI.Logger.Error("[yangtransport] Failed to serialize offscreen convoy state.");
			ServerAPI.Logger.Error(exception);
		}
	}

	private void MarkVirtualDirty(long headKey)
	{
		if (headKey == 0 || !VirtualConvoys.ContainsKey(headKey)) return;
		MarkVirtualPersistenceDirty();
	}

	private void MarkVirtualPersistenceDirty() { VirtualPersistenceDirty = true; }

	private void OnEntityLoaded(Entity entity)
	{
		if (ServerAPI == null || AuthoritySystem == null || !OffscreenRailVehicleAdapter.TryGetSupported(entity, out _)) return;
		if (AuthoritySystem.IsLoadedMaterialAuthorityCurrent(entity)) return;
		if (!AuthoritySystem.TryGetAuthority(entity.EntityId, out RailEntityAuthority authority)) return;

		long entityGeneration = RailConvoyAuthoritySystem.ReadEntityGeneration(entity);
		ServerAPI.Logger.Debug
		(
			"[yangtransport] Removing stale rail entity shell {0}: entity generation={1}, authority={2}/{3}.",
			entity.EntityId, entityGeneration, authority.Mode, authority.Generation
		);

		DiscardLoadedStaleShell(entity);
		RestoreAuthorityPublicationsAfterStaleShell(entity, authority);
	}

	private void RestoreAuthorityPublicationsAfterStaleShell(Entity staleEntity, RailEntityAuthority authority)
	{
		if (RailSystem == null) return;

		// A stale loaded head may have queued a loaded collision body before the authority handler discarded it.
		// Cancel only that exact entity request so an unrelated current head sharing either the persisted or authoritative group ID cannot be disturbed.
		long staleGroupID = staleEntity.EntityId;
		if (staleEntity is IRailwayConvoyVehicle staleVehicle && staleVehicle.ConvoyHeadEntityID != 0)
		{
			staleGroupID = staleVehicle.ConvoyHeadEntityID;
		}
		CollisionSystem?.CancelPendingLoadedBodyUpdate(staleGroupID, staleEntity);
		if (staleGroupID != authority.HeadID) CollisionSystem?.CancelPendingLoadedBodyUpdate(authority.HeadID, staleEntity);

		if (authority.Mode != RailEntityAuthorityMode.Virtual || !VirtualConvoys.TryGetValue(authority.HeadID, out VirtualConvoy? convoy)) { return; }

		RailGraphLive graph = RailSystem.Graph;
		if (!convoy.RestoreOccupancyPublication(RailSystem, graph))
		{
			ServerAPI?.Logger.Warning
			(
				"[yangtransport] Could not restore virtual convoy {0} occupancy after discarding stale entity {1}. Freezing it for safe materialization.",
				authority.HeadID, staleEntity.EntityId
			);
			convoy.BecomeOrphaned(RailSystem, graph);
			UpdateVirtualRouteIndex(convoy);
			UpdateWaitingSignalIndex(convoy);
			RefreshMaterializationIndex(convoy, graph);
			ScheduleVirtual(convoy, ServerAPI?.World.ElapsedMilliseconds ?? 0, replaceExisting: true);
			MarkVirtualDirty(convoy.HeadKey);
		}

		// The stale entity's OnEntityDespawn may remove the shared group body.
		// Reassert the virtual body synchronously after every stale-shell callback has completed.
		PublishVirtualCollisionBody(convoy, graph);
	}

	private void DiscardLoadedStaleShell(Entity entity)
	{
		if (ServerAPI == null || entity.State == EnumEntityState.Despawned) return;

		// ServerChunk's load loop iterates its packed entity array by index.
		// Removing the shell from that array inside OnEntityLoaded would shift the next entity left and make the engine skip it.
		// First unload the shell from live simulation without touching the chunk array, then remove the stored shell after the load loop.
		ICoreServerAPI serverAPI = ServerAPI;
		long entityID = entity.EntityId;
		long entityChunkIndex = entity.InChunkIndex3d;

		InternalDespawns.Add(entityID);
		try { serverAPI.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Unload, DamageSourceForDeath = null }); }
		finally { InternalDespawns.Remove(entityID); }

		serverAPI.Event.EnqueueMainThreadTask(() =>
		{
			IServerChunk? chunk = serverAPI.WorldManager.GetChunk(entityChunkIndex);
			if (chunk == null) return;

			Entity[] entities = chunk.Entities;
			int count = Math.Min(chunk.EntitiesCount, entities?.Length ?? 0);
			for (int entityIndex = 0; entityIndex < count; entityIndex++)
			{
				if (!ReferenceEquals(entities[entityIndex], entity)) continue;
				chunk.RemoveEntity(entityID);
				break;
			}
		}, "yangtransport-remove-stale-rail-shell");
	}

	private void DespawnInternally(Entity entity)
	{
		if (ServerAPI == null || entity.State == EnumEntityState.Despawned) return;

		InternalDespawns.Add(entity.EntityId);
		try { ServerAPI.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed, DamageSourceForDeath = null }); }
		finally { InternalDespawns.Remove(entity.EntityId); }
	}

	private static RailAuthorityTransitionMember[] BuildAuthorityMembers(IReadOnlyList<VehicleSnapshot> snapshots, IReadOnlyList<long>? generations = null)
	{
		var members = new RailAuthorityTransitionMember[snapshots.Count];
		for (int snapshotIndex = 0; snapshotIndex < snapshots.Count; snapshotIndex++)
		{
			VehicleSnapshot snapshot = snapshots[snapshotIndex];
			long generation = generations == null ? snapshot.AuthorityGeneration : generations[snapshotIndex];
			members[snapshotIndex] = new RailAuthorityTransitionMember(snapshot.EntityID, generation, snapshotIndex);
		}
		return members;
	}

	// Virtualization
	internal bool ServerVirtualizeLoadedConvoy(IReadOnlyList<IRailwayConvoyVehicle> orderedVehicles)
	{
		if (ServerAPI == null || RailSystem == null || AuthoritySystem == null || orderedVehicles == null || orderedVehicles.Count == 0) return false;
		if (ServerAPI.Server.IsShuttingDown) return false;

		long headKey = orderedVehicles[0].ConvoyHeadEntityID != 0 ? orderedVehicles[0].ConvoyHeadEntityID : orderedVehicles[0].Entity.EntityId;
		if (headKey == 0 || VirtualConvoys.ContainsKey(headKey) || PendingConvoys.ContainsKey(headKey)) return false;

		var entities = new Entity[orderedVehicles.Count];
		var sourceChunks = new IServerChunk[orderedVehicles.Count];
		byte gauge = orderedVehicles[0].TrackGauge;

		for (int vehicleIndex = 0; vehicleIndex < orderedVehicles.Count; vehicleIndex++)
		{
			IRailwayConvoyVehicle vehicle = orderedVehicles[vehicleIndex];
			Entity entity = vehicle.Entity;
			if (entity == null) return false;

			long memberHeadKey = vehicle.ConvoyHeadEntityID != 0 ? vehicle.ConvoyHeadEntityID : entity.EntityId;
			if 
			(
				entity.State == EnumEntityState.Despawned || vehicle.Derailed || memberHeadKey != headKey || vehicle.TrackGauge != gauge ||
				(orderedVehicles.Count > 1 && vehicle.ConvoyOrderIndex != vehicleIndex) || !AuthoritySystem.IsLoadedMaterialAuthorityCurrent(entity) ||
				RailConvoyAuthoritySystem.ReadEntityGeneration(entity) == long.MaxValue || !OffscreenRailVehicleAdapter.TryGetSupported(entity, out _)
			) { return false; }

			EntityBehaviorSeatable? seatable = entity.GetBehavior<EntityBehaviorSeatable>();
			IMountableSeat[]? seats = seatable?.Seats;
			if (seats != null) { for (int seatIndex = 0; seatIndex < seats.Length; seatIndex++) { if (seats[seatIndex]?.Passenger != null) return false; } }

			IServerChunk? sourceChunk = ServerAPI.WorldManager.GetChunk(entity.InChunkIndex3d);
			if (sourceChunk == null) return false;

			entities[vehicleIndex] = entity;
			sourceChunks[vehicleIndex] = sourceChunk;
		}

		// Despawn followers first and the head last.
		// The existing unload handler accumulates the complete convoy and finalizes it when the head snapshot supplies the route state.
		for (int entityIndex = entities.Length - 1; entityIndex >= 0; entityIndex--)
		{
			ServerAPI.World.DespawnEntity(entities[entityIndex], new EntityDespawnData { Reason = EnumDespawnReason.Unload, DamageSourceForDeath = null });
		}

		if (!VirtualConvoys.ContainsKey(headKey))
		{
			ServerAPI.Logger.Error("[yangtransport] Explicit material-to-virtual transfer for convoy {0} did not finalize. Retained chunk shells for recovery.", headKey);
			return false;
		}

		// EnumDespawnReason.Unload intentionally leaves entities in their source chunks so vanilla chunk persistence can save them.
		// Virtual authority is now the persistent owner, so remove those obsolete shells only after the virtual transfer committed.
		for (int entityIndex = 0; entityIndex < entities.Length; entityIndex++) { sourceChunks[entityIndex].RemoveEntity(entities[entityIndex].EntityId); }

		return true;
	}

	private void OnEntityDespawn(Entity entity, EntityDespawnData reason)
	{
		if (ServerAPI == null || RailSystem == null || reason == null) return;
		if (InternalDespawns.Contains(entity.EntityId)) return;
		if (!OffscreenRailVehicleAdapter.TryGetSupported(entity, out var vehicle)) return;

		if (reason.Reason != EnumDespawnReason.Unload) { if (!ServerAPI.Server.IsShuttingDown) AuthoritySystem?.MarkTrackedEntityDeleted(entity.EntityId); return; }

		if (ServerAPI.Server.IsShuttingDown || vehicle.Derailed) return;

		OffscreenRailVehicleAdapter.PersistNow(vehicle);

		long entityID = vehicle.Entity.EntityId;
		long headKey = vehicle.ConvoyHeadEntityID != 0 ? vehicle.ConvoyHeadEntityID : entityID;

		long nowMS = ServerAPI.World.ElapsedMilliseconds;

		if (!PendingConvoys.TryGetValue(headKey, out var pendingConvoy))
		{
			pendingConvoy = new PendingConvoy(headKey, nowMS);
			PendingConvoys[headKey] = pendingConvoy;
		}

		pendingConvoy.LastSeenMS = nowMS;

		var snapshot = VehicleSnapshot.Capture(vehicle);
		pendingConvoy.AddSnapshot(snapshot.ConvoyIndex, snapshot);
		pendingConvoy.HasConductorLocust |= snapshot.HasConductorLocust;
		if (pendingConvoy.HasConductorLocust) AutomationSystem?.ReleaseConvoy(headKey);

		if (pendingConvoy.Gauge == byte.MaxValue) pendingConvoy.Gauge = snapshot.Gauge;
		else if (pendingConvoy.Gauge != snapshot.Gauge) pendingConvoy.Invalid = true;

		if (entityID != headKey)
		{
			if (pendingConvoy.HasHead && pendingConvoy.ExpectedCount > 0 && pendingConvoy.CollectedCount == pendingConvoy.ExpectedCount)
			{
				TryFinalizePending(headKey, pendingConvoy, RailSystem.Graph, nowMS);
			}
			return;
		}

		pendingConvoy.HeadSeen = true;

		if (!OffscreenRailVehicleAdapter.TryReadTrackState(vehicle, out var trackState)) { pendingConvoy.Invalid = true; return; }

		OffscreenRailVehicleAdapter.GetRollParams(vehicle, out pendingConvoy.RollingResistanceConstant, out pendingConvoy.RollingResistanceSpeedCoefficient, out _);

		int expectedCount = vehicle.ConvoyHeadEntityID == 0 ? 1 : entity.WatchedAttributes.GetInt("convoyCount", 1);

		pendingConvoy.HasHead = true;
		pendingConvoy.ExpectedCount = Math.Max(1, expectedCount);
		if (ConvoySystem != null && ConvoySystem.TryGetConvoyHasConductorLocust(headKey, out bool hasLocust)) pendingConvoy.HasConductorLocust |= hasLocust;
		if (pendingConvoy.HasConductorLocust) AutomationSystem?.ReleaseConvoy(headKey);
		pendingConvoy.InitialSpeed = trackState.Speed;
		pendingConvoy.StartingAbsoluteDistanceTravelled = trackState.TravelledABS;
		pendingConvoy.StartingStandardGaugeLeadEnd = trackState.SGLeadEnd;
		pendingConvoy.StartCursor = new OffscreenRailCursor.Cursor
		{
			SegmentHash = trackState.SegmentHash,
			SegmentIndex = trackState.SegmentIndex,
			NormalizedSegmentProgress = trackState.NormalizedSegmentProgress,
			Direction = trackState.Direction
		};

		if (entity is EntityStandardGaugeLocomotive standardGaugeHead && standardGaugeHead.OfflineSimTryExportPathTape(out ConvoyRoute exportedTape, out double exportedPathHeadDistance))
		{
			pendingConvoy.StartPathTape = exportedTape;
			pendingConvoy.StartingPathHeadDistance = exportedPathHeadDistance;
		}
		else if (entity is EntityMinecart minecartHead && minecartHead.OfflineSimTryExportRailTape(out ConvoyRoute exportedMinecartTape, out double exportedMinecartRootDistance))
		{
			pendingConvoy.StartPathTape = exportedMinecartTape;
			pendingConvoy.StartingPathHeadDistance = exportedMinecartRootDistance;
		}

		if (pendingConvoy.ExpectedCount > 0 && pendingConvoy.CollectedCount == pendingConvoy.ExpectedCount) TryFinalizePending(headKey, pendingConvoy, RailSystem.Graph, nowMS);
	}

	private void DropPending(long headKey, PendingConvoy pendingConvoy, bool releaseHeadOccupancy)
	{
		if (releaseHeadOccupancy && pendingConvoy.HeadSeen) RailSystem?.ReleaseOccupancyOwner(headKey);
		PendingConvoys.Remove(headKey);
	}

	private void OnPendingFinalizeTick(float deltaTime)
	{
		if (ServerAPI == null || RailSystem == null || PendingConvoys.Count == 0) return;

		RailGraphLive graph = RailSystem.Graph;
		long nowMS = ServerAPI.World.ElapsedMilliseconds;
		VirtualHeadKeysScratch.Clear();
		VirtualHeadKeysScratch.AddRange(PendingConvoys.Keys);

		for (int pendingIndex = 0; pendingIndex < VirtualHeadKeysScratch.Count; pendingIndex++)
		{
			long headKey = VirtualHeadKeysScratch[pendingIndex];
			if (!PendingConvoys.TryGetValue(headKey, out PendingConvoy? pendingConvoy)) continue;
			if (nowMS - pendingConvoy.LastSeenMS < PendingFinalizeTickMS) continue;

			if (pendingConvoy.Invalid || !pendingConvoy.HasHead || pendingConvoy.ExpectedCount <= 0 || pendingConvoy.CollectedCount != pendingConvoy.ExpectedCount)
			{
				if (nowMS - pendingConvoy.CreatedMS > PendingTimeoutMS) DropPending(headKey, pendingConvoy, releaseHeadOccupancy: true);
				continue;
			}

			TryFinalizePending(headKey, pendingConvoy, graph, nowMS);
		}

	}

	private bool TryFinalizePending(long headKey, PendingConvoy pendingConvoy, RailGraphLive graph, long nowMS)
	{
		if (pendingConvoy.Invalid || !pendingConvoy.HasHead || pendingConvoy.ExpectedCount <= 0 || pendingConvoy.CollectedCount != pendingConvoy.ExpectedCount) return false;

		List<VehicleSnapshot>? orderedSnapshots = pendingConvoy.BuildOrderedSnapshots();
		if (orderedSnapshots == null) { DropPending(headKey, pendingConvoy, releaseHeadOccupancy: true); return true; }

		// A virtual authority already exists for this head. Any later chunk shells are stale and are removed by OnEntityLoaded.
		if (VirtualConvoys.ContainsKey(headKey)) {  PendingConvoys.Remove(headKey); return true; }

		for (int snapshotIndex = 0; snapshotIndex < orderedSnapshots.Count; snapshotIndex++)
		{
			if (ServerAPI?.World.GetEntityById(orderedSnapshots[snapshotIndex].EntityID) is Entity)
			{
				// The chunk reloaded before the pending convoy became virtual. Material remains authoritative and the incomplete transfer is abandoned.
				DropPending(headKey, pendingConvoy, releaseHeadOccupancy: false);
				return true;
			}
		}

		bool routeValid = pendingConvoy.StartPathTape != null && pendingConvoy.StartPathTape.ValidateAuthoritative(graph, pendingConvoy.Gauge);

		int leadIndex = 0;
		int convoyWeight = 0;
		for (int snapshotIndex = 0; snapshotIndex < orderedSnapshots.Count; snapshotIndex++)
		{
			VehicleSnapshot snapshot = orderedSnapshots[snapshotIndex];
			if (snapshot.Gauge != pendingConvoy.Gauge) { DropPending(headKey, pendingConvoy, releaseHeadOccupancy: true); return true; }
			if (snapshot.HasTractionEngine) leadIndex = snapshotIndex;
			convoyWeight += Math.Max(1, snapshot.SelfWeight);
		}

		convoyWeight = Math.Max(1, convoyWeight);
		VehicleSnapshot leadSnapshot = orderedSnapshots[leadIndex];

		VirtualAutomationState? automation = null;
		if (pendingConvoy.HasConductorLocust && leadSnapshot.HasTractionEngine && leadSnapshot.TimetableRoute.Length > 0)
		{
			double driveMaxSpeedBPS = Math.Min(Math.Max(0, leadSnapshot.DriveSpeedPotentialBPS - convoyWeight), leadSnapshot.DriveSpeedLimitBPS);
			automation = new VirtualAutomationState(leadSnapshot.TimetableRoute, leadSnapshot.TimetableCurrentIndex, driveMaxSpeedBPS, Math.Min(leadSnapshot.FuelSeconds, leadSnapshot.WaterSeconds));

			// Preserve the already-computed executable route across the loaded -> virtual authority transfer. Derived path-index state is not needed to keep following it.
			if (AutomationSystem != null && AutomationSystem.TryExportRouteLease(headKey, out RailAutomationRouteLease lease) && lease.TargetRouteIndex == automation.CurrentTargetIndex)
			{
				automation.AdoptRouteLease(in lease);
			}
		}

		double rollingDeceleration = Math.Max(MinRollingDeceleration, pendingConvoy.RollingResistanceConstant + pendingConvoy.RollingResistanceSpeedCoefficient * Math.Max(0, convoyWeight - 1));
		double stopSeconds = GameMath.Clamp(Math.Abs(pendingConvoy.InitialSpeed) / rollingDeceleration, 0, MaxCoastOffscreenSeconds);

		int effectiveDriveDirection = leadSnapshot.DriveDirection;
		if (leadIndex != 0 && effectiveDriveDirection != 0) effectiveDriveDirection = Math.Abs(effectiveDriveDirection);

		double poweredSeconds = 0;
		if (automation == null && leadSnapshot.HasTractionEngine && effectiveDriveDirection != 0 && Math.Sign(effectiveDriveDirection) == Math.Sign(pendingConvoy.InitialSpeed))
		{
			poweredSeconds = GameMath.Clamp(Math.Min(leadSnapshot.FuelSeconds, leadSnapshot.WaterSeconds), 0, MaxPoweredOffscreenSeconds);
		}

		int wantTurnTravel = PhaseToTurnChoice(leadSnapshot.TurnPhase) * (pendingConvoy.InitialSpeed >= 0 ? 1 : -1);
		if (leadIndex != 0) wantTurnTravel = -wantTurnTravel;

		var virtualConvoy = new VirtualConvoy
		(
			headKey: headKey,
			gauge: pendingConvoy.Gauge,
			wantTurnTravel: wantTurnTravel,
			initialSpeed: pendingConvoy.InitialSpeed,
			rollingDeceleration: rollingDeceleration,
			poweredSeconds: poweredSeconds,
			stopSeconds: stopSeconds,
			startingAbsoluteDistanceTravelled: pendingConvoy.StartingAbsoluteDistanceTravelled,
			startCursor: pendingConvoy.StartCursor,
			leadIndex: leadIndex,
			standardGaugeLeadEnd: pendingConvoy.StartingStandardGaugeLeadEnd,
			startingMS: nowMS,
			automationState: automation,
			pathTape: pendingConvoy.StartPathTape,
			pathHeadDistance: pendingConvoy.StartingPathHeadDistance
		);

		for (int snapshotIndex = 0; snapshotIndex < orderedSnapshots.Count; snapshotIndex++)
		{
			virtualConvoy.AddVehicleSnapshot(orderedSnapshots[snapshotIndex]);
			virtualConvoy.FrozenPoses.Add(orderedSnapshots[snapshotIndex].CapturedPose);
		}

		if (!routeValid) { virtualConvoy.BecomeOrphaned(RailSystem, graph); }
		else if (!virtualConvoy.EnsureOccupancyCurrent(RailSystem, graph)) { virtualConvoy.BecomeOrphaned(RailSystem, graph); }
		VirtualConvoys[headKey] = virtualConvoy;
		RegisterVirtualRuntime(virtualConvoy, graph, nowMS);

		RailAuthorityTransitionMember[] authorityMembers = BuildAuthorityMembers(orderedSnapshots);
		if (AuthoritySystem == null || !AuthoritySystem.TryEnterVirtual(headKey, authorityMembers, out long[] virtualGenerations))
		{
			RemoveVirtualRuntime(virtualConvoy);
			VirtualConvoys.Remove(headKey);
			DropPending(headKey, pendingConvoy, releaseHeadOccupancy: true);
			return true;
		}

		for (int snapshotIndex = 0; snapshotIndex < orderedSnapshots.Count; snapshotIndex++) orderedSnapshots[snapshotIndex].AuthorityGeneration = virtualGenerations[snapshotIndex];

		if (automation != null) AutomationSystem?.ReleaseConvoy(headKey);
		PendingConvoys.Remove(headKey);
		MarkVirtualDirty(headKey);
		return true;
	}

	private void SimulateVirtual(VirtualConvoy virtualConvoy, long nowMS, RailGraphLive graph)
	{
		if (ServerAPI != null) virtualConvoy.AdvanceRefrigeration(ServerAPI, nowMS);

		RailPathIndex? pathIndex = null;
		if (virtualConvoy.NeedsAutomationPathIndex && AutomationSystem != null && virtualConvoy.Cursor.SegmentHash != 0)
		{
			AutomationSystem.TryGetPathIndexForEdge(virtualConvoy.Cursor.SegmentHash, out pathIndex, out _);
		}

		virtualConvoy.SimulateTo(nowMS, graph, RailSystem, StationRegistry, pathIndex, AutomationSystem);
		UpdateVirtualRouteIndex(virtualConvoy);
		UpdateWaitingSignalIndex(virtualConvoy);
		RefreshMaterializationIndex(virtualConvoy, graph);
		PublishVirtualCollisionBody(virtualConvoy, graph);
		MarkVirtualDirty(virtualConvoy.HeadKey);
	}

	private void PublishVirtualCollisionBody(VirtualConvoy virtualConvoy, RailGraphLive graph)
	{
		if (CollisionSystem == null || virtualConvoy == null) return;
		if (!CollisionSystem.BeginVirtualBodyPublication(virtualConvoy.HeadKey)) return;

		bool success = false;
		try
		{
			double collisionSpeed = virtualConvoy.SpeedAt(virtualConvoy.ElapsedSimulationSec);
			success = virtualConvoy.TryEmitPathTapeCollisionFootprint(CollisionSystem, graph, collisionSpeed);
		}
		finally { CollisionSystem.EndVirtualBodyPublication(success); }
	}

	internal void OnRailGraphChanged(RailGraphLive graph, RailGraphChangeSet change)
	{
		if (graph == null || change == null || !change.HasAnyChange) return;

		long nowMS = ServerAPI?.World.ElapsedMilliseconds ?? 0;
		AffectedVirtualRoutesScratch.Clear();
		if (change.GlobalInvalidation) { foreach (long headKey in VirtualConvoys.Keys) AffectedVirtualRoutesScratch.Add(headKey); }
		else
		{
			for (int edgeIndex = 0; edgeIndex < change.TouchedEdges.Count; edgeIndex++)
			{
				if (!VirtualRouteOwnersByEdge.TryGetValue(change.TouchedEdges[edgeIndex], out HashSet<long>? owners)) continue;
				foreach (long headKey in owners) AffectedVirtualRoutesScratch.Add(headKey);
			}
		}

		// An added edge is not present in any existing route index, so also wake route owners adjacent to touched endpoints.
		// This targets pathless trains at the edited junction without globally waking every dormant convoy.
		for (int endpointIndex = 0; endpointIndex < change.TouchedEndpoints.Count; endpointIndex++)
		{
			if (!graph.TryGetIncidentEdges(change.TouchedEndpoints[endpointIndex], out List<ulong> incidentEdges)) continue;
			for (int incidentEdgeIndex = 0; incidentEdgeIndex < incidentEdges.Count; incidentEdgeIndex++)
			{
				if (!VirtualRouteOwnersByEdge.TryGetValue(incidentEdges[incidentEdgeIndex], out HashSet<long>? owners)) continue;
				foreach (long headKey in owners) AffectedVirtualRoutesScratch.Add(headKey);
			}
		}

		VirtualHeadKeysScratch.Clear();
		foreach (long headKey in AffectedVirtualRoutesScratch)
		{
			if (!VirtualConvoys.TryGetValue(headKey, out VirtualConvoy? virtualConvoy)) continue;
			string? invalidationReason = null;

			VirtualAutomationState? automationState = virtualConvoy.Automation;
			if (automationState?.ActiveRoute != null && automationState.ActiveRoute.TouchesGraphChange(graph, change)) { automationState.RefreshRequested = true; }

			if (!virtualConvoy.HasPathTape || virtualConvoy.PathTape == null) { invalidationReason = "missing route"; }
			else if (!virtualConvoy.PathTape.TryPromoteAcrossUnrelatedChange(change, graph.BuildVersion) && !virtualConvoy.PathTape.ValidateAuthoritative(graph, virtualConvoy.Gauge))
			{
				invalidationReason = "edited route is no longer valid";
			}

			if (invalidationReason == null && RailSystem != null && !virtualConvoy.EnsureOccupancyCurrent(RailSystem, graph))
			{
				invalidationReason = "occupancy footprint could not be rebuilt";
			}

			if (invalidationReason != null)
			{
				ServerAPI?.Logger.Warning
				(
					"[yangtransport] Freezing virtual convoy {0} after rail graph edit: {1}. It will materialize derailed from its last committed poses.",
					headKey, invalidationReason
				);
				virtualConvoy.BecomeOrphaned(RailSystem, graph);
				UpdateVirtualRouteIndex(virtualConvoy);
				UpdateWaitingSignalIndex(virtualConvoy);
				RefreshMaterializationIndex(virtualConvoy, graph);
				PublishVirtualCollisionBody(virtualConvoy, graph);
				ScheduleVirtual(virtualConvoy, nowMS, replaceExisting: true);
				MarkVirtualDirty(virtualConvoy.HeadKey);
			}
			else
			{
				UpdateVirtualRouteIndex(virtualConvoy);
				UpdateWaitingSignalIndex(virtualConvoy);
				RefreshMaterializationIndex(virtualConvoy, graph);
				PublishVirtualCollisionBody(virtualConvoy, graph);
				ScheduleVirtual(virtualConvoy, nowMS);
				MarkVirtualDirty(virtualConvoy.HeadKey);
			}
		}

		VirtualHeadKeysScratch.Clear();
		AffectedVirtualRoutesScratch.Clear();
	}

	private void UpdateVirtualRouteIndex(VirtualConvoy virtualConvoy)
	{
		UpdateVirtualStationWaitIndex(virtualConvoy);

		ConvoyRoute? pathTape = virtualConvoy.PathTape;
		uint tapeTopologyRevision = pathTape?.TopologyRevision ?? 0;
		RailAutomationRoute? automationRoute = virtualConvoy.Automation?.ActiveRoute;

		if
		(
			ReferenceEquals(virtualConvoy.IndexedTape, pathTape) &&
			virtualConvoy.IndexedTapeTopologyRevision == tapeTopologyRevision &&
			ReferenceEquals(virtualConvoy.IndexedAutomationRoute, automationRoute)
		) { return; }

		RemoveVirtualRouteIndex(virtualConvoy);
		pathTape?.CollectUniqueEdgeHashes(virtualConvoy.IndexedRouteEdges);
		automationRoute?.CollectEdgeHashes(virtualConvoy.IndexedRouteEdges, clear: false);

		foreach (ulong edgeHash in virtualConvoy.IndexedRouteEdges)
		{
			if (!VirtualRouteOwnersByEdge.TryGetValue(edgeHash, out HashSet<long>? owners))
			{
				owners = new HashSet<long>();
				VirtualRouteOwnersByEdge[edgeHash] = owners;
			}
			owners.Add(virtualConvoy.HeadKey);
		}

		if (automationRoute != null)
		{
			RailStationRegistrySystem.StationBlockKey stationKey = automationRoute.Target.StationKey;
			if (!VirtualRouteOwnersByStation.TryGetValue(stationKey, out HashSet<long>? stationOwners))
			{
				stationOwners = new HashSet<long>();
				VirtualRouteOwnersByStation[stationKey] = stationOwners;
			}
			stationOwners.Add(virtualConvoy.HeadKey);
		}

		virtualConvoy.IndexedTape = pathTape;
		virtualConvoy.IndexedTapeTopologyRevision = tapeTopologyRevision;
		virtualConvoy.IndexedAutomationRoute = automationRoute;
	}

	private void UpdateVirtualStationWaitIndex(VirtualConvoy virtualConvoy)
	{
		VirtualAutomationState? automation = virtualConvoy.Automation;
		bool awaitingTargetResolution = automation != null && (automation.ActiveRoute == null ||
			(automation.RefreshRequested && automation.PinnedTargetInvalidated)) && automation.Route.Length != 0;

		ulong stationNameHash = 0;
		RailStationRegistrySystem.StationBlockKey stationKey = default;
		if (awaitingTargetResolution)
		{
			int targetIndex = automation!.CurrentStationIndex >= 0 &&
				automation.CurrentStationIndex < automation.Route.Length
				? automation.CurrentStationIndex : 0;
			TimetableRouteEntryPacket routeEntry = automation.Route[targetIndex];
			stationNameHash = routeEntry.NameHash;
			stationKey = new RailStationRegistrySystem.StationBlockKey(routeEntry.X, routeEntry.Y, routeEntry.Z, routeEntry.Dimension);

			if
			(
				virtualConvoy.IndexedWaitingStationNameHash == stationNameHash &&
				virtualConvoy.HasIndexedWaitingStationKey && virtualConvoy.IndexedWaitingStationKey.Equals(stationKey)
			) { return; }
		}
		else if (virtualConvoy.IndexedWaitingStationNameHash == 0 && !virtualConvoy.HasIndexedWaitingStationKey) { return; }

		RemoveVirtualStationWaitIndex(virtualConvoy);
		if (!awaitingTargetResolution) return;

		if (stationNameHash != 0)
		{
			if (!VirtualStationWaitersByNameHash.TryGetValue(stationNameHash, out HashSet<long>? owners))
			{
				owners = new HashSet<long>();
				VirtualStationWaitersByNameHash[stationNameHash] = owners;
			}
			owners.Add(virtualConvoy.HeadKey);
			virtualConvoy.IndexedWaitingStationNameHash = stationNameHash;
		}

		if (!VirtualStationWaitersByBlock.TryGetValue(stationKey, out HashSet<long>? blockOwners))
		{
			blockOwners = new HashSet<long>();
			VirtualStationWaitersByBlock[stationKey] = blockOwners;
		}
		blockOwners.Add(virtualConvoy.HeadKey);
		virtualConvoy.IndexedWaitingStationKey = stationKey;
		virtualConvoy.HasIndexedWaitingStationKey = true;
	}

	private void RemoveVirtualStationWaitIndex(VirtualConvoy virtualConvoy)
	{
		if
		(
			virtualConvoy.IndexedWaitingStationNameHash != 0 &&
			VirtualStationWaitersByNameHash.TryGetValue(virtualConvoy.IndexedWaitingStationNameHash, out HashSet<long>? nameOwners)
		)
		{
			nameOwners.Remove(virtualConvoy.HeadKey);
			if (nameOwners.Count == 0) VirtualStationWaitersByNameHash.Remove(virtualConvoy.IndexedWaitingStationNameHash);
		}
		virtualConvoy.IndexedWaitingStationNameHash = 0;

		if
		(
			virtualConvoy.HasIndexedWaitingStationKey &&
			VirtualStationWaitersByBlock.TryGetValue(virtualConvoy.IndexedWaitingStationKey, out HashSet<long>? blockOwners)
		)
		{
			blockOwners.Remove(virtualConvoy.HeadKey);
			if (blockOwners.Count == 0) VirtualStationWaitersByBlock.Remove(virtualConvoy.IndexedWaitingStationKey);
		}
		virtualConvoy.HasIndexedWaitingStationKey = false;
		virtualConvoy.IndexedWaitingStationKey = default;
	}

	private void CollectVirtualStationWaiters(RailStationRegistrySystem.RailStationEntry? stationEntry)
	{
		if (stationEntry == null) return;

		if (stationEntry.NameHash != 0 && VirtualStationWaitersByNameHash.TryGetValue(stationEntry.NameHash, out HashSet<long>? nameOwners))
		{
			foreach (long headKey in nameOwners) AffectedVirtualRoutesScratch.Add(headKey);
		}

		if (VirtualStationWaitersByBlock.TryGetValue(stationEntry.Key, out HashSet<long>? blockOwners))
		{
			foreach (long headKey in blockOwners) AffectedVirtualRoutesScratch.Add(headKey);
		}
	}

	private void RemoveVirtualRouteIndex(VirtualConvoy virtualConvoy)
	{
		RailAutomationRoute? indexedAutomationRoute = virtualConvoy.IndexedAutomationRoute;
		if (indexedAutomationRoute != null && VirtualRouteOwnersByStation.TryGetValue(indexedAutomationRoute.Target.StationKey, out HashSet<long>? stationOwners))
		{
			stationOwners.Remove(virtualConvoy.HeadKey);
			if (stationOwners.Count == 0) VirtualRouteOwnersByStation.Remove(indexedAutomationRoute.Target.StationKey);
		}

		foreach (ulong edgeHash in virtualConvoy.IndexedRouteEdges)
		{
			if (!VirtualRouteOwnersByEdge.TryGetValue(edgeHash, out HashSet<long>? owners)) continue;
			owners.Remove(virtualConvoy.HeadKey);
			if (owners.Count == 0) VirtualRouteOwnersByEdge.Remove(edgeHash);
		}

		virtualConvoy.IndexedRouteEdges.Clear();
		virtualConvoy.IndexedTape = null;
		virtualConvoy.IndexedTapeTopologyRevision = uint.MaxValue;
		virtualConvoy.IndexedAutomationRoute = null;
	}

	// Virtual scheduling / materialization
	private readonly record struct VirtualWake(long HeadKey, long Generation);

	private void ReleaseAllVirtualOccupancy() { foreach (VirtualConvoy virtualConvoy in VirtualConvoys.Values) virtualConvoy.ReleaseOccupancy(RailSystem); }

	private void ClearVirtualRuntimeIndexes()
	{
		VirtualWakeQueue.Clear();
		ScheduledVirtuals.Clear();
		AutomatedVirtuals.Clear();
		MaterializationOwnersByColumn.Clear();
		PendingLoadedMaterializationColumns.Clear();
		MaterializationCandidatesScratch.Clear();
		SignalWaiters.Clear();
		VirtualRouteOwnersByEdge.Clear();
		VirtualRouteOwnersByStation.Clear();
		VirtualStationWaitersByNameHash.Clear();
		VirtualStationWaitersByBlock.Clear();

		foreach (VirtualConvoy virtualConvoy in VirtualConvoys.Values)
		{
			CollisionSystem?.RemovePublishedBody(virtualConvoy.HeadKey);
			virtualConvoy.NextWakeMS = long.MaxValue;
			virtualConvoy.WakeGeneration++;
			virtualConvoy.IndexedMaterializationColumns.Clear();
			virtualConvoy.DesiredMaterializationColumns.Clear();
			virtualConvoy.MaterializationPoseValid = false;
			virtualConvoy.HasIndexedWaitingSignal = false;
			virtualConvoy.IndexedWaitingSignal = default;
			virtualConvoy.IndexedWaitingStationNameHash = 0;
			virtualConvoy.HasIndexedWaitingStationKey = false;
			virtualConvoy.IndexedWaitingStationKey = default;
		}
	}

	private void RegisterVirtualRuntime(VirtualConvoy virtualConvoy, RailGraphLive graph, long nowMS)
	{
		virtualConvoy.EnsureVirtualClock(nowMS);
		if (virtualConvoy.HasAutomation) AutomatedVirtuals.Add(virtualConvoy.HeadKey);
		UpdateVirtualRouteIndex(virtualConvoy);
		UpdateWaitingSignalIndex(virtualConvoy);
		bool materializationReady = RefreshMaterializationIndex(virtualConvoy, graph);
		PublishVirtualCollisionBody(virtualConvoy, graph);
		if (materializationReady) ScheduleVirtual(virtualConvoy, nowMS, replaceExisting: true);
		else ScheduleNextVirtualWake(virtualConvoy, nowMS, graph);
	}

	private void RemoveVirtualRuntime(VirtualConvoy virtualConvoy, bool removeCollisionBody = true)
	{
		CancelVirtualWake(virtualConvoy);
		AutomatedVirtuals.Remove(virtualConvoy.HeadKey);
		RemoveWaitingSignalIndex(virtualConvoy);
		RemoveMaterializationIndex(virtualConvoy);
		RemoveVirtualStationWaitIndex(virtualConvoy);
		RemoveVirtualRouteIndex(virtualConvoy);
		if (removeCollisionBody) CollisionSystem?.RemovePublishedBody(virtualConvoy.HeadKey);
	}

	private void ScheduleVirtual(VirtualConvoy virtualConvoy, long dueMS, bool replaceExisting = false)
	{
		if (virtualConvoy == null || !VirtualConvoys.ContainsKey(virtualConvoy.HeadKey)) return;
		dueMS = Math.Max(0, dueMS);

		if (!replaceExisting && virtualConvoy.NextWakeMS <= dueMS) return;

		unchecked { virtualConvoy.WakeGeneration++; }
		virtualConvoy.NextWakeMS = dueMS;
		ScheduledVirtuals.Add(virtualConvoy.HeadKey);
		VirtualWakeQueue.Enqueue(new VirtualWake(virtualConvoy.HeadKey, virtualConvoy.WakeGeneration), dueMS);
	}

	private void CancelVirtualWake(VirtualConvoy virtualConvoy)
	{
		unchecked { virtualConvoy.WakeGeneration++; }
		virtualConvoy.NextWakeMS = long.MaxValue;
		ScheduledVirtuals.Remove(virtualConvoy.HeadKey);
	}

	private void ScheduleNextVirtualWake(VirtualConvoy virtualConvoy, long nowMS, RailGraphLive graph)
	{
		if (ServerAPI != null && virtualConvoy.AdvanceRefrigeration(ServerAPI, nowMS)) MarkVirtualDirty(virtualConvoy.HeadKey);

		long delayMS = virtualConvoy.GetNextWakeDelayMS(graph);
		if (delayMS == long.MaxValue) { CancelVirtualWake(virtualConvoy); return; }

		long dueMS = nowMS + Math.Max(1, delayMS);
		if (virtualConvoy.LastVirtualTickMS > 0)
		{
			long latestFreshnessDueMS = virtualConvoy.LastVirtualTickMS + MaxVirtualPositionAgeMS;
			if (latestFreshnessDueMS > nowMS) dueMS = Math.Min(dueMS, latestFreshnessDueMS);
			else dueMS = nowMS;
		}

		ScheduleVirtual(virtualConvoy, dueMS, replaceExisting: true);
	}

	private int ComputeVirtualTickBudget()
	{
		int activeVirtualCount = Math.Max(1, ScheduledVirtuals.Count);
		int freshnessWindowMS = Math.Max(VirtualWakeTickMS, MaxVirtualPositionAgeMS - VirtualFreshnessHeadroomMS);
		int ticksPerFreshnessWindow = Math.Max(1, freshnessWindowMS / VirtualWakeTickMS);

		return Math.Max(1, (activeVirtualCount + ticksPerFreshnessWindow - 1) / ticksPerFreshnessWindow + VirtualSchedulerBurstAllowance);
	}

	private void OnVirtualWakeTick(float deltaTime)
	{
		if (ServerAPI == null || RailSystem == null) return;

		long nowMS = ServerAPI.World.ElapsedMilliseconds;
		RailGraphLive graph = RailSystem.Graph;

		FlushPendingLoadedMaterializationColumns(nowMS, graph);

		if (VirtualWakeQueue.Count == 0) return;
		ProcessDueVirtuals(nowMS, graph, true, ComputeVirtualTickBudget());
	}

	private void ProcessDueVirtuals(long nowMS, RailGraphLive graph, bool allowMaterialize, int budget)
	{
		CompactWakeQueueIfNeeded();

		int processedCount = 0;
		int examinedCount = 0;
		int maxExaminedCount = Math.Max(1024, budget * 4);
		while (processedCount < budget && examinedCount++ < maxExaminedCount && VirtualWakeQueue.TryPeek(out VirtualWake wake, out long dueMS) && dueMS <= nowMS)
		{
			VirtualWakeQueue.Dequeue();

			if
			(
				!VirtualConvoys.TryGetValue(wake.HeadKey, out VirtualConvoy? virtualConvoy) ||
				virtualConvoy.WakeGeneration != wake.Generation || virtualConvoy.NextWakeMS != dueMS
			) { continue; }

			processedCount++;
			virtualConvoy.NextWakeMS = long.MaxValue;
			ScheduledVirtuals.Remove(virtualConvoy.HeadKey);

			if 
			(
				allowMaterialize && virtualConvoy.MaterializationPoseValid &&
				virtualConvoy.IndexedMaterializationColumns.Count == 0 && TryMaterializeIfReady(virtualConvoy, graph, nowMS)
			) { continue; }

			SimulateVirtual(virtualConvoy, nowMS, graph);

			if (allowMaterialize && TryMaterializeIfReady(virtualConvoy, graph, nowMS)) continue;
			if (virtualConvoy.NextWakeMS == long.MaxValue) ScheduleNextVirtualWake(virtualConvoy, nowMS, graph);
		}

		CompactWakeQueueIfNeeded();
	}

	private void CompactWakeQueueIfNeeded()
	{
		int liveVirtualCount = ScheduledVirtuals.Count;
		if (VirtualWakeQueue.Count <= liveVirtualCount * 4 + 1024) return;

		VirtualWakeQueue.Clear();
		foreach (long headKey in ScheduledVirtuals)
		{
			if (!VirtualConvoys.TryGetValue(headKey, out VirtualConvoy? virtualConvoy) || virtualConvoy.NextWakeMS == long.MaxValue) continue;
			VirtualWakeQueue.Enqueue(new VirtualWake(headKey, virtualConvoy.WakeGeneration), virtualConvoy.NextWakeMS);
		}
	}

	private void OnChunkColumnLoaded(Vec2i chunkCoordinate, IWorldChunk[] chunks)
	{
		// Vintage Story raises ChunkColumnLoaded before the column has been installed into the live chunk map.
		// Treat the event only as a wake, the actual world-state probe happens on the next offscreen tick.
		if (ServerAPI == null || RailSystem == null) return;

		long columnKey = PackChunkColumn(chunkCoordinate.X, chunkCoordinate.Y);
		if (!MaterializationOwnersByColumn.TryGetValue(columnKey, out HashSet<long>? currentOwners) || currentOwners.Count == 0) { return; }

		PendingLoadedMaterializationColumns.Add(columnKey);
	}

	private void FlushPendingLoadedMaterializationColumns(long nowMS, RailGraphLive graph)
	{
		if (PendingLoadedMaterializationColumns.Count == 0) return;

		MaterializationCandidatesScratch.Clear();
		foreach (long columnKey in PendingLoadedMaterializationColumns)
		{
			if (!MaterializationOwnersByColumn.TryGetValue(columnKey, out HashSet<long>? owners)) continue;
			foreach (long headKey in owners) { MaterializationCandidatesScratch.Add(headKey); }
		}
		PendingLoadedMaterializationColumns.Clear();

		foreach (long headKey in MaterializationCandidatesScratch)
		{
			if (VirtualConvoys.TryGetValue(headKey, out VirtualConvoy? virtualConvoy)) { TryMaterializeIfReady(virtualConvoy, graph, nowMS); }
		}
		MaterializationCandidatesScratch.Clear();
	}


	private void OnSignalAuthorityChanged(RailGraphLive.EndpointKey endpoint)
	{
		if (ServerAPI == null || !SignalWaiters.TryGetValue(endpoint, out HashSet<long>? owners) || owners.Count == 0) { return; }

		long nowMS = ServerAPI.World.ElapsedMilliseconds;
		WakeHeadKeysScratch.Clear();
		WakeHeadKeysScratch.AddRange(owners);
		for (int waiterIndex = 0; waiterIndex < WakeHeadKeysScratch.Count; waiterIndex++)
		{
			if (VirtualConvoys.TryGetValue(WakeHeadKeysScratch[waiterIndex], out VirtualConvoy? virtualConvoy)) ScheduleVirtual(virtualConvoy, nowMS);
		}
		WakeHeadKeysScratch.Clear();
	}

	private void OnClearanceEdgeResolved(ulong edgeHash)
	{
		// This is only an opportunistic wake for clearance changes on edges already in the retained body tape.
		// We deliberately do not index an untraversed blocked next edge, clearance can only be recomputed in loaded chunks,
		// and a relevant chunk load/materialization transfers authority to the loaded convoy simulation.
		if (ServerAPI == null || !VirtualRouteOwnersByEdge.TryGetValue(edgeHash, out HashSet<long>? owners) || owners.Count == 0) { return; }

		long nowMS = ServerAPI.World.ElapsedMilliseconds;
		WakeHeadKeysScratch.Clear();
		WakeHeadKeysScratch.AddRange(owners);
		for (int waiterIndex = 0; waiterIndex < WakeHeadKeysScratch.Count; waiterIndex++)
		{
			if (VirtualConvoys.TryGetValue(WakeHeadKeysScratch[waiterIndex], out VirtualConvoy? virtualConvoy)) ScheduleVirtual(virtualConvoy, nowMS);
		}
		WakeHeadKeysScratch.Clear();
	}

	private void OnStationRegistryChanged(RailStationRegistrySystem.RegistryChange change)
	{
		if (ServerAPI == null || AutomatedVirtuals.Count == 0) return;

		AffectedVirtualRoutesScratch.Clear();
		bool registryReset = change.Kind == RailStationRegistrySystem.RegistryChangeKind.Reset;
		RailStationRegistrySystem.RailStationEntry? previousStationEntry = change.OldEntry;
		RailStationRegistrySystem.RailStationEntry? newStationEntry = change.NewEntry;
		bool physicalTargetChanged =
			change.Kind == RailStationRegistrySystem.RegistryChangeKind.Removed || (change.Kind == RailStationRegistrySystem.RegistryChangeKind.Updated &&
			previousStationEntry != null && !previousStationEntry.SamePhysicalStopAs(newStationEntry));

		if (registryReset) { foreach (long headKey in AutomatedVirtuals) AffectedVirtualRoutesScratch.Add(headKey); }
		else
		{
			// Added/renamed/rebound stations only wake convoys that are actually waiting for that timetable selector. Healthy active routes remain pinned.
			if (change.Kind == RailStationRegistrySystem.RegistryChangeKind.Added ||
				change.Kind == RailStationRegistrySystem.RegistryChangeKind.Updated)
			{
				CollectVirtualStationWaiters(newStationEntry);
			}

			// Removal or movement of the actual pinned stop invalidates only its route owners.
			if (physicalTargetChanged && previousStationEntry != null && VirtualRouteOwnersByStation.TryGetValue(previousStationEntry.Key, out HashSet<long>? routeOwners))
			{
				foreach (long headKey in routeOwners) AffectedVirtualRoutesScratch.Add(headKey);
			}
		}

		if (AffectedVirtualRoutesScratch.Count == 0) return;

		long nowMS = ServerAPI.World.ElapsedMilliseconds;
		foreach (long headKey in AffectedVirtualRoutesScratch)
		{
			if (!VirtualConvoys.TryGetValue(headKey, out VirtualConvoy? virtualConvoy) || virtualConvoy.Automation == null) continue;

			VirtualAutomationState automation = virtualConvoy.Automation!;
			bool activeTargetInvalidated = registryReset && automation.ActiveRoute != null;
			if 
			(
				!activeTargetInvalidated && physicalTargetChanged && previousStationEntry != null &&
				automation.ActiveRoute != null && automation.ActiveRoute.Target.StationKey.Equals(previousStationEntry.Key)
			) { activeTargetInvalidated = true; }

			if (activeTargetInvalidated)
			{
				automation.RefreshRequested = true;
				automation.PinnedTargetInvalidated = true;
				UpdateVirtualStationWaitIndex(virtualConvoy);
			}

			ScheduleVirtual(virtualConvoy, nowMS, replaceExisting: true);
		}
		AffectedVirtualRoutesScratch.Clear();
	}

	private void UpdateWaitingSignalIndex(VirtualConvoy virtualConvoy)
	{
		RemoveWaitingSignalIndex(virtualConvoy);

		VirtualAutomationState? automationState = virtualConvoy.Automation;
		RailExactTurnPlan? turnPlan = automationState?.ActiveRoute?.TurnPlan;
		if (automationState?.Status != VirtualAutomationStatus.WaitingForSignal || turnPlan == null || !turnPlan.BoundaryBlocked) { return; }

		RailGraphLive.EndpointKey endpoint = turnPlan.BlockedSignalEndpoint;
		if (!SignalWaiters.TryGetValue(endpoint, out HashSet<long>? owners)) { owners = new HashSet<long>(); SignalWaiters[endpoint] = owners; }

		owners.Add(virtualConvoy.HeadKey);
		virtualConvoy.HasIndexedWaitingSignal = true;
		virtualConvoy.IndexedWaitingSignal = endpoint;
	}

	private void RemoveWaitingSignalIndex(VirtualConvoy virtualConvoy)
	{
		if (!virtualConvoy.HasIndexedWaitingSignal) return;

		if (SignalWaiters.TryGetValue(virtualConvoy.IndexedWaitingSignal, out HashSet<long>? owners))
		{
			owners.Remove(virtualConvoy.HeadKey);
			if (owners.Count == 0) SignalWaiters.Remove(virtualConvoy.IndexedWaitingSignal);
		}

		virtualConvoy.HasIndexedWaitingSignal = false;
		virtualConvoy.IndexedWaitingSignal = default;
	}

	private bool RefreshMaterializationIndex(VirtualConvoy virtualConvoy, RailGraphLive graph)
	{
		virtualConvoy.DesiredMaterializationColumns.Clear();
		bool materializationPoseValid = virtualConvoy.Vehicles.Count > 0;

		if (virtualConvoy.Orphaned)
		{
			materializationPoseValid = virtualConvoy.FrozenPoses.Count == virtualConvoy.Vehicles.Count && virtualConvoy.FrozenPoses.Count > 0;
			for (int poseIndex = 0; materializationPoseValid && poseIndex < virtualConvoy.FrozenPoses.Count; poseIndex++)
			{
				FrozenVehiclePose pose = virtualConvoy.FrozenPoses[poseIndex];
				if (IsChunkLoadedAt(pose.X, pose.Y, pose.Z, pose.Dimension)) continue;

				int chunkX = (int)Math.Floor((float)pose.X / GlobalConstants.ChunkSize);
				int chunkZ = (int)Math.Floor((float)pose.Z / GlobalConstants.ChunkSize);
				virtualConvoy.DesiredMaterializationColumns.Add(PackChunkColumn(chunkX, chunkZ));
			}
		}
		else
		{
			virtualConvoy.EnsureFrozenPoseCapacity();
			for (int vehicleIndex = 0; vehicleIndex < virtualConvoy.Vehicles.Count; vehicleIndex++)
			{
				if
				(
					!virtualConvoy.TrySampleVehicleCursor(graph, vehicleIndex, out OffscreenRailCursor.Cursor cursor) ||
					!OffscreenRailCursor.TryGetWorldPose(graph, cursor, WorldPoseScratch, out float yaw, out float roll)
				) { materializationPoseValid = false; break; }

				if (!TryGetCursorDimension(graph, cursor, out int dimension)) { materializationPoseValid = false; break; }

				virtualConvoy.FrozenPoses[vehicleIndex] = new FrozenVehiclePose(WorldPoseScratch.X, WorldPoseScratch.Y, WorldPoseScratch.Z, dimension, yaw, roll);
				if (IsChunkLoadedAt(WorldPoseScratch.X, WorldPoseScratch.Y, WorldPoseScratch.Z, dimension)) continue;

				int chunkX = (int)Math.Floor((float)WorldPoseScratch.X / GlobalConstants.ChunkSize);
				int chunkZ = (int)Math.Floor((float)WorldPoseScratch.Z / GlobalConstants.ChunkSize);
				virtualConvoy.DesiredMaterializationColumns.Add(PackChunkColumn(chunkX, chunkZ));
			}
		}

		if (!materializationPoseValid) virtualConvoy.DesiredMaterializationColumns.Clear();

		if
		(
			virtualConvoy.MaterializationPoseValid == materializationPoseValid &&
			virtualConvoy.IndexedMaterializationColumns.SetEquals(virtualConvoy.DesiredMaterializationColumns)
		) { return materializationPoseValid && virtualConvoy.DesiredMaterializationColumns.Count == 0; }

		RemoveMaterializationIndex(virtualConvoy);
		virtualConvoy.MaterializationPoseValid = materializationPoseValid;

		foreach (long columnKey in virtualConvoy.DesiredMaterializationColumns)
		{
			virtualConvoy.IndexedMaterializationColumns.Add(columnKey);
			if (!MaterializationOwnersByColumn.TryGetValue(columnKey, out HashSet<long>? owners))
			{
				owners = new HashSet<long>();
				MaterializationOwnersByColumn[columnKey] = owners;
			}
			owners.Add(virtualConvoy.HeadKey);
		}

		return materializationPoseValid && virtualConvoy.IndexedMaterializationColumns.Count == 0;
	}

	private void RemoveMaterializationIndex(VirtualConvoy virtualConvoy)
	{
		foreach (long columnKey in virtualConvoy.IndexedMaterializationColumns)
		{
			if (!MaterializationOwnersByColumn.TryGetValue(columnKey, out HashSet<long>? owners)) continue;
			owners.Remove(virtualConvoy.HeadKey);
			if (owners.Count == 0) MaterializationOwnersByColumn.Remove(columnKey);
		}
		virtualConvoy.IndexedMaterializationColumns.Clear();
	}

	private bool TryMaterializeIfReady(VirtualConvoy virtualConvoy, RailGraphLive graph, long nowMS)
	{
		if (!RefreshMaterializationIndex(virtualConvoy, graph)) return false;
		if (!Materialize(virtualConvoy, graph)) { ScheduleVirtual(virtualConvoy, nowMS + MaterializationRetryMS, replaceExisting: true); return false; }

		// Preserve collision continuity across the virtual-to-loaded handoff.
		RemoveVirtualRuntime(virtualConvoy, removeCollisionBody: virtualConvoy.Orphaned);
		VirtualConvoys.Remove(virtualConvoy.HeadKey);
		MarkVirtualPersistenceDirty();
		return true;
	}

	private static long PackChunkColumn(int chunkX, int chunkZ)
	{
		// Chunk-column load events expose only X/Z, so dimension sharing here is intentional. Exact readiness is filtered by the dimension-aware chunk-Y probe.
		return ((long)chunkX << 32) | (uint)chunkZ;
	}

	private static int InternalChunkY(double localY, int dimension)
	{
		return (int)Math.Floor(localY / GlobalConstants.ChunkSize) + dimension * GlobalConstants.DimensionSizeInChunks;
	}

	private static bool TryGetCursorDimension(RailGraphLive graph, in OffscreenRailCursor.Cursor cursor, out int dimension)
	{
		dimension = 0;
		if (!graph.TryGetEdgeEndpoints(cursor.SegmentHash, out RailGraphLive.EndpointKey endpoint, out _, out _)) return false;

		dimension = endpoint.Dimension;
		return true;
	}

	private bool IsChunkLoadedAt(double x, double y, double z, int dimension)
	{
		if (ServerAPI == null) return false;

		// Material rail poses are committed to EntityPos at float precision.
		// Use that same precision for virtual residency so both sides agree exactly at chunk boundaries.
		int chunkX = (int)Math.Floor((float)x / GlobalConstants.ChunkSize);
		int chunkY = InternalChunkY((float)y, dimension);
		int chunkZ = (int)Math.Floor((float)z / GlobalConstants.ChunkSize);

		return ServerAPI.WorldManager.GetChunk(chunkX, chunkY, chunkZ) != null;
	}

	private bool MaterializeOrphaned(VirtualConvoy virtualConvoy)
	{
		if (ServerAPI == null || virtualConvoy == null || virtualConvoy.FrozenPoses.Count != virtualConvoy.Vehicles.Count || virtualConvoy.Vehicles.Count == 0) return false;

		Entity[] entities = new Entity[virtualConvoy.Vehicles.Count];
		long[] chunkIndexes = new long[virtualConvoy.Vehicles.Count];
		long[] generations = new long[virtualConvoy.Vehicles.Count];
		bool materializationCommitted = false;

		try
		{
			for (int vehicleIndex = 0; vehicleIndex < virtualConvoy.Vehicles.Count; vehicleIndex++)
			{
				FrozenVehiclePose pose = virtualConvoy.FrozenPoses[vehicleIndex];
				int chunkX = (int)Math.Floor(pose.X / GlobalConstants.ChunkSize);
				int chunkY = InternalChunkY(pose.Y, pose.Dimension);
				int chunkZ = (int)Math.Floor(pose.Z / GlobalConstants.ChunkSize);
				if (ServerAPI.WorldManager.GetChunk(chunkX, chunkY, chunkZ) == null) return false;

				VehicleSnapshot snapshot = virtualConvoy.Vehicles[vehicleIndex];
				if (ServerAPI.World.GetEntityById(snapshot.EntityID) is Entity) return false;

				Entity entity = snapshot.CreateEntityFromBytes(ServerAPI);
				snapshot.ApplyVirtualOverrides(entity);
				if (!OffscreenRailVehicleAdapter.TryGetSupported(entity, out _)) return false;

				generations[vehicleIndex] = AuthoritySystem?.NextMaterialGeneration(virtualConvoy.HeadKey, snapshot.EntityID, snapshot.AuthorityGeneration)
					?? throw new InvalidOperationException("Convoy authority system unavailable during materialization.");
				RailConvoyAuthoritySystem.WriteEntityGeneration(entity, generations[vehicleIndex]);

				entity.ServerPos.X = (float)pose.X;
				entity.ServerPos.Y = (float)pose.Y;
				entity.ServerPos.Z = (float)pose.Z;
				entity.ServerPos.Dimension = pose.Dimension;
				entity.ServerPos.Yaw = pose.Yaw;
				entity.ServerPos.Roll = pose.Roll;
				entity.ServerPos.Pitch = 0;
				entity.Pos.SetFrom(entity.ServerPos);

				entity.Attributes.SetBool("derailed", true);
				entity.Attributes.SetLong("segHash", 0);
				entity.Attributes.SetDouble("speed", 0);
				entity.WatchedAttributes.SetBool("derailed", true);
				entity.WatchedAttributes.MarkPathDirty("derailed");

				entities[vehicleIndex] = entity;
				chunkIndexes[vehicleIndex] = ServerAPI.WorldManager.ChunkIndex3D(chunkX, chunkY, chunkZ);
				AuthoritySystem?.BeginMaterialization(entity.EntityId);
			}

			for (int entityIndex = 0; entityIndex < entities.Length; entityIndex++)
			{
				if (!ServerAPI.World.LoadEntity(entities[entityIndex], chunkIndexes[entityIndex])) return false;
				ServerAPI.World.UpdateEntityChunk(entities[entityIndex], chunkIndexes[entityIndex]);
			}

			for (int entityIndex = 0; entityIndex < entities.Length; entityIndex++)
			{
				virtualConvoy.Vehicles[entityIndex].ApplyRefrigerationAfterMaterialization(entities[entityIndex]);
			}

			for (int vehicleIndex = 0; vehicleIndex < entities.Length; vehicleIndex++)
			{
				if (!OffscreenRailVehicleAdapter.TryGetSupported(entities[vehicleIndex], out IRailwayConvoyVehicle vehicle)) return false;
				vehicle.ServerClearConvoyState();
				vehicle.ServerInvalidateRailBinding(persist: true);
			}

			AuthoritySystem?.CommitVirtualToMaterial(virtualConvoy.HeadKey, BuildAuthorityMembers(virtualConvoy.Vehicles, generations), preserveConvoy: false);
			virtualConvoy.ReleaseOccupancy(RailSystem);
			materializationCommitted = true;
			return true;
		}
		catch (Exception exception) { ServerAPI.Logger.Error(exception); return false; }
		finally
		{
			for (int entityIndex = 0; entityIndex < entities.Length; entityIndex++)
			{
				if (entities[entityIndex] != null) AuthoritySystem?.EndMaterialization(entities[entityIndex].EntityId);
			}

			if (!materializationCommitted)
			{
				for (int entityIndex = entities.Length - 1; entityIndex >= 0; entityIndex--)
				{
					Entity entity = entities[entityIndex];
					if (entity != null && ReferenceEquals(ServerAPI.World.GetEntityById(entity.EntityId), entity) && entity.State != EnumEntityState.Despawned)
					{
						DespawnInternally(entity);
					}
				}
			}
		}
	}


	private bool Materialize(VirtualConvoy virtualConvoy, RailGraphLive graph)
	{
		if (ServerAPI == null) return false;
		if (virtualConvoy.AdvanceRefrigeration(ServerAPI, ServerAPI.World.ElapsedMilliseconds)) MarkVirtualDirty(virtualConvoy.HeadKey);
		if (virtualConvoy.Orphaned) return MaterializeOrphaned(virtualConvoy);

		Entity[]? entities = null;
		long[]? materialGenerations = null;
		bool loadedAnyEntity = false;
		bool materializationCommitted = false;

		try
		{
			double elapsedSec = virtualConvoy.ElapsedSimulationSec;
			double burnSec = virtualConvoy.BurnSecondsToApply;

			if (!virtualConvoy.TrySampleVehicleCursor(graph, 0, out OffscreenRailCursor.Cursor headCursor)) return false;
			double absoluteHeadDistanceTravelled = virtualConvoy.StartingAbsoluteDistanceTravelled + virtualConvoy.AbsoluteDistanceSimulated;
			double headSpeedNow = virtualConvoy.SpeedAt(elapsedSec);

			int vehicleCount = virtualConvoy.Vehicles.Count;
			if (vehicleCount <= 0) { virtualConvoy.ReleaseOccupancy(RailSystem); return true; }

			if (!OffscreenRailCursor.TryGetWorldPose(graph, headCursor, WorldPoseScratch, out _, out _) || !TryGetCursorDimension(graph, headCursor, out int headDimension))
			{
				return false;
			}

			int headChunkX = (int)Math.Floor((float)WorldPoseScratch.X / GlobalConstants.ChunkSize);
			int headChunkY = InternalChunkY((float)WorldPoseScratch.Y, headDimension);
			int headChunkZ = (int)Math.Floor((float)WorldPoseScratch.Z / GlobalConstants.ChunkSize);
			if (ServerAPI.WorldManager.GetChunk(headChunkX, headChunkY, headChunkZ) == null) return false;

			bool isConvoy = vehicleCount > 1;
			int leadIndex = GameMath.Clamp(virtualConvoy.LeadIndex, 0, vehicleCount - 1);

			var vehicleCursors = new OffscreenRailCursor.Cursor[vehicleCount];
			var absoluteDistancesTravelled = new double[vehicleCount];
			var chunkIndexes = new long[vehicleCount];
			var spawnX = new double[vehicleCount];
			var spawnY = new double[vehicleCount];
			var spawnZ = new double[vehicleCount];
			var spawnDimension = new int[vehicleCount];

			var temporaryPosition = new Vec3d();
			if (RailSystem != null && !virtualConvoy.EnsureOccupancyCurrent(RailSystem, graph)) { virtualConvoy.ReleaseOccupancy(RailSystem); return false; }

			for (int vehicleIndex = 0; vehicleIndex < vehicleCount; vehicleIndex++)
			{
				double distanceBehindHead = Math.Max(0, virtualConvoy.Vehicles[vehicleIndex].DistanceBehindHead);
				if (!virtualConvoy.TrySampleVehicleCursor(graph, vehicleIndex, out OffscreenRailCursor.Cursor vehicleCursor)) return false;
				if (!OffscreenRailCursor.TryGetWorldPose(graph, vehicleCursor, temporaryPosition, out _, out _) || !TryGetCursorDimension(graph, vehicleCursor, out int dimension))
				{
					return false;
				}

				int chunkX = (int)Math.Floor((float)temporaryPosition.X / GlobalConstants.ChunkSize);
				int chunkY = InternalChunkY((float)temporaryPosition.Y, dimension);
				int chunkZ = (int)Math.Floor((float)temporaryPosition.Z / GlobalConstants.ChunkSize);

				if (ServerAPI.WorldManager.GetChunk(chunkX, chunkY, chunkZ) == null) return false;

				vehicleCursors[vehicleIndex] = vehicleCursor;
				absoluteDistancesTravelled[vehicleIndex] = Math.Max(0, absoluteHeadDistanceTravelled - distanceBehindHead);
				chunkIndexes[vehicleIndex] = ServerAPI.WorldManager.ChunkIndex3D(chunkX, chunkY, chunkZ);
				spawnX[vehicleIndex] = temporaryPosition.X;
				spawnY[vehicleIndex] = temporaryPosition.Y;
				spawnZ[vehicleIndex] = temporaryPosition.Z;
				spawnDimension[vehicleIndex] = dimension;
			}

			var convoyVehicles = new IRailwayConvoyVehicle[vehicleCount];
			entities = new Entity[vehicleCount];
			materialGenerations = new long[vehicleCount];

			// Preflight every snapshot before loading the first entity.
			// A live ID collision is never ours to replace, and treating it as a partial materialization can combine two independent convoy states.
			for (int vehicleIndex = 0; vehicleIndex < vehicleCount; vehicleIndex++)
			{
				VehicleSnapshot snapshot = virtualConvoy.Vehicles[vehicleIndex];
				if (ServerAPI.World.GetEntityById(snapshot.EntityID) is Entity) return false;

				Entity entity = snapshot.CreateEntityFromBytes(ServerAPI);
				snapshot.ApplyVirtualOverrides(entity);
				if (!OffscreenRailVehicleAdapter.TryGetSupported(entity, out IRailwayConvoyVehicle vehicle)) return false;

				materialGenerations[vehicleIndex] = AuthoritySystem?.NextMaterialGeneration(virtualConvoy.HeadKey, snapshot.EntityID, snapshot.AuthorityGeneration)
					?? throw new InvalidOperationException("Convoy authority system unavailable during materialization.");
				RailConvoyAuthoritySystem.WriteEntityGeneration(entity, materialGenerations[vehicleIndex]);
				AuthoritySystem?.BeginMaterialization(entity.EntityId);

				entity.ServerPos.X = (float)spawnX[vehicleIndex];
				entity.ServerPos.Y = (float)spawnY[vehicleIndex];
				entity.ServerPos.Z = (float)spawnZ[vehicleIndex];
				entity.ServerPos.Dimension = spawnDimension[vehicleIndex];
				entity.Pos.SetFrom(entity.ServerPos);

				entities[vehicleIndex] = entity;
				convoyVehicles[vehicleIndex] = vehicle;
			}

			for (int vehicleIndex = 0; vehicleIndex < vehicleCount; vehicleIndex++)
			{
				if (!ServerAPI.World.LoadEntity(entities[vehicleIndex], chunkIndexes[vehicleIndex])) return false;
				loadedAnyEntity = true;
				ServerAPI.World.UpdateEntityChunk(entities[vehicleIndex], chunkIndexes[vehicleIndex]);
			}

			for (int vehicleIndex = 0; vehicleIndex < vehicleCount; vehicleIndex++)
			{
				virtualConvoy.Vehicles[vehicleIndex].ApplyRefrigerationAfterMaterialization(entities[vehicleIndex]);
			}

			for (int vehicleIndex = 0; vehicleIndex < vehicleCount; vehicleIndex++)
			{
				IRailwayConvoyVehicle vehicle = convoyVehicles[vehicleIndex];
				OffscreenRailCursor.Cursor vehicleCursor = vehicleCursors[vehicleIndex];

				var trackState = new OffscreenTrackState
				(
					vehicleCursor.SegmentHash,
					vehicleCursor.SegmentIndex,
					vehicleCursor.NormalizedSegmentProgress,
					vehicleCursor.Direction,
					headSpeedNow,
					absoluteDistancesTravelled[vehicleIndex],
					(int)virtualConvoy.LeadEnd
				);

				OffscreenRailVehicleAdapter.ApplyTrackState(vehicle, in trackState, resetHistory: vehicleIndex == 0);

				Entity entity = vehicle.Entity;
				int chunkX = (int)Math.Floor(entity.ServerPos.X / GlobalConstants.ChunkSize);
				int chunkY = InternalChunkY(entity.ServerPos.Y, entity.ServerPos.Dimension);
				int chunkZ = (int)Math.Floor(entity.ServerPos.Z / GlobalConstants.ChunkSize);

				if (ServerAPI.WorldManager.GetChunk(chunkX, chunkY, chunkZ) == null) return false;
				long newChunkIndex = ServerAPI.WorldManager.ChunkIndex3D(chunkX, chunkY, chunkZ);
				ServerAPI.World.UpdateEntityChunk(entity, newChunkIndex);
			}

			if (isConvoy)
			{
				long headID = convoyVehicles[0].Entity.EntityId;
				for (int vehicleIndex = 0; vehicleIndex < convoyVehicles.Length; vehicleIndex++)
				{
					long previousVehicleID = vehicleIndex == 0 ? 0 : convoyVehicles[vehicleIndex - 1].Entity.EntityId;
					convoyVehicles[vehicleIndex].ServerSetConvoyState(headID, vehicleIndex, previousVehicleID, Math.Max(0, virtualConvoy.Vehicles[vehicleIndex].DistanceBehindHead));
					ConvoySystem?.Register(convoyVehicles[vehicleIndex]);
				}
			}

			// The virtual tape is already authoritative and was used to choose every spawn cursor above.
			// Import it directly instead of rebuilding it from nearest-edge binds, which can select a different branch at switches (especially for minecarts).
			if (virtualConvoy.PathTape == null || !OffscreenRailVehicleAdapter.TryImportPathTape(convoyVehicles[0], virtualConvoy.PathTape, virtualConvoy.PathHeadDistance))
			{
				return false;
			}

			RailAutomationRouteLease? routeLease = null;
			if (virtualConvoy.Automation != null)
			{
				ConductorTimetableStorage.Save(convoyVehicles[leadIndex].Entity, virtualConvoy.Automation.CloneRoute(), virtualConvoy.Automation.CurrentStationIndex);
				if (virtualConvoy.Automation.TryCreateRouteLease(out RailAutomationRouteLease lease)) routeLease = lease;
			}

			if (burnSec > 0.001) { convoyVehicles[leadIndex].SteamEngineBehaviour?.OfflineSimFastForwardBurn(burnSec); }

			CollisionSystem?.RequestLoadedBodyUpdate(convoyVehicles[0]);
			AuthoritySystem?.CommitVirtualToMaterial(virtualConvoy.HeadKey, BuildAuthorityMembers(virtualConvoy.Vehicles, materialGenerations!), preserveConvoy: isConvoy);
			materializationCommitted = true;

			if (routeLease.HasValue && AutomationSystem != null)
			{
				try { AutomationSystem.AdoptRouteLease(convoyVehicles[0].Entity.EntityId, convoyVehicles[leadIndex].Entity, routeLease.Value); }
				catch (Exception exception)
				{
					// Material authority is already committed.
					// Route adoption is an optimization, a failed handoff simply causes loaded automation to acquire a fresh route.
					ServerAPI.Logger.Error(exception);
				}
			}
			return true;
		}
		catch (Exception exception) { ServerAPI.Logger.Error(exception); return false; }
		finally
		{
			if (entities != null)
			{
				for (int entityIndex = 0; entityIndex < entities.Length; entityIndex++)
				{
					if (entities[entityIndex] != null) AuthoritySystem?.EndMaterialization(entities[entityIndex].EntityId);
				}
			}

			if (!materializationCommitted && entities != null)
			{
				// Reverse order minimizes convoy regrouping while the temporary entities are removed.
				// EnumDespawnReason.Removed deliberately bypasses re-virtualization.
				for (int entityIndex = entities.Length - 1; entityIndex >= 0; entityIndex--)
				{
					Entity entity = entities[entityIndex];
					if (entity != null && ReferenceEquals(ServerAPI.World.GetEntityById(entity.EntityId), entity) && entity.State != EnumEntityState.Despawned)
					{
						loadedAnyEntity = true;
						DespawnInternally(entity);
					}
				}

				if (loadedAnyEntity)
				{
					// Entity initialization may have replaced the virtual owner's occupancy and collision body before a later step failed.
					// Restore both publications so the failed materialization is externally indistinguishable from no attempt.
					virtualConvoy.ReleaseOccupancy(RailSystem);
					if (!virtualConvoy.EnsureOccupancyCurrent(RailSystem, graph))
					{
						ServerAPI.Logger.Error("[yangtransport] Failed to restore virtual convoy {0} occupancy after materialization rollback.", virtualConvoy.HeadKey);
					}
					PublishVirtualCollisionBody(virtualConvoy, graph);
				}
			}
		}
	}

	internal bool DebugForceMaterializeNear
	(
		EntityPos playerPosition, int radiusChunks, List<long> materializedHeadIDs,
		List<Vec3d> materializedPositions, List<long> failedHeadIDs
	)
	{
		if (ServerAPI == null || RailSystem == null || AuthoritySystem == null) return false;

		RailGraphLive graph = RailSystem.Graph;
		int playerChunkX = (int)Math.Floor((float)playerPosition.X / GlobalConstants.ChunkSize);
		int playerChunkZ = (int)Math.Floor((float)playerPosition.Z / GlobalConstants.ChunkSize);

		var nearbyHeadKeys = new List<long>();
		foreach (VirtualConvoy virtualConvoy in VirtualConvoys.Values)
		{
			int dimension;
			double x, z;

			if
			(
				virtualConvoy.TrySampleVehicleCursor(graph, 0, out OffscreenRailCursor.Cursor cursor) &&
				TryGetCursorDimension(graph, cursor, out dimension) &&
				OffscreenRailCursor.TryGetWorldPose(graph, cursor, WorldPoseScratch, out _, out _)
			) { x = WorldPoseScratch.X; z = WorldPoseScratch.Z; }
			else if (virtualConvoy.FrozenPoses.Count > 0)
			{
				FrozenVehiclePose pose = virtualConvoy.FrozenPoses[0];
				dimension = pose.Dimension;
				x = pose.X;
				z = pose.Z;
			}
			else continue;

			if (dimension != playerPosition.Dimension) continue;

			int chunkX = (int)Math.Floor((float)x / GlobalConstants.ChunkSize);
			int chunkZ = (int)Math.Floor((float)z / GlobalConstants.ChunkSize);
			if (Math.Abs(chunkX - playerChunkX) <= radiusChunks && Math.Abs(chunkZ - playerChunkZ) <= radiusChunks) nearbyHeadKeys.Add(virtualConvoy.HeadKey);
		}

		for (int headIndex = 0; headIndex < nearbyHeadKeys.Count; headIndex++)
		{
			long headKey = nearbyHeadKeys[headIndex];
			if (!VirtualConvoys.TryGetValue(headKey, out VirtualConvoy? virtualConvoy)) continue;
			if (!DebugRepairVirtualAuthority(virtualConvoy)) { failedHeadIDs.Add(headKey); continue; }

			bool ready = RefreshMaterializationIndex(virtualConvoy, graph);
			if (!ready && virtualConvoy.MaterializationPoseValid)
			{
				ServerAPI.Logger.Warning("[yangtransport] Force Materialize Near could not materialize convoy {0}: required chunks are not loaded.", headKey);
				failedHeadIDs.Add(headKey);
				continue;
			}

			bool success = ready && Materialize(virtualConvoy, graph);
			if (!success && !virtualConvoy.Orphaned)
			{
				virtualConvoy.BecomeOrphaned(RailSystem, graph);
				success = RefreshMaterializationIndex(virtualConvoy, graph) && Materialize(virtualConvoy, graph);
			}

			if (!success)
			{
				ServerAPI.Logger.Warning("[yangtransport] Force Materialize Near failed to safely materialize convoy {0}, including derailed recovery.", headKey);
				failedHeadIDs.Add(headKey);
				UpdateVirtualRouteIndex(virtualConvoy);
				UpdateWaitingSignalIndex(virtualConvoy);
				PublishVirtualCollisionBody(virtualConvoy, graph);
				ScheduleVirtual(virtualConvoy, ServerAPI.World.ElapsedMilliseconds, replaceExisting: true);
				MarkVirtualDirty(virtualConvoy.HeadKey);
				continue;
			}

			Entity? headEntity = ServerAPI.World.GetEntityById(headKey);
			FrozenVehiclePose fallbackPose = virtualConvoy.FrozenPoses[0];
			Vec3d targetPosition = headEntity != null
				? new Vec3d(headEntity.ServerPos.X, headEntity.ServerPos.Y + 1, headEntity.ServerPos.Z)
				: new Vec3d(fallbackPose.X, fallbackPose.Y + 1, fallbackPose.Z);

			RemoveVirtualRuntime(virtualConvoy, removeCollisionBody: virtualConvoy.Orphaned);
			VirtualConvoys.Remove(virtualConvoy.HeadKey);
			MarkVirtualPersistenceDirty();

			materializedHeadIDs.Add(headKey);
			materializedPositions.Add(targetPosition);
		}

		return true;
	}

	private bool DebugRepairVirtualAuthority(VirtualConvoy virtualConvoy)
	{
		if (ServerAPI == null || AuthoritySystem == null) return false;

		long[] generations = new long[virtualConvoy.Vehicles.Count];
		for (int vehicleIndex = 0; vehicleIndex < virtualConvoy.Vehicles.Count; vehicleIndex++)
		{
			VehicleSnapshot snapshot = virtualConvoy.Vehicles[vehicleIndex];
			if (ServerAPI.World.GetEntityById(snapshot.EntityID) is Entity)
			{
				ServerAPI.Logger.Warning("[yangtransport] Force Materialize Near refused convoy {0}: entity {1} is already live.", virtualConvoy.HeadKey, snapshot.EntityID);
				return false;
			}
			if (!AuthoritySystem.TryGetAuthority(snapshot.EntityID, out RailEntityAuthority authority))
			{
				ServerAPI.Logger.Warning("[yangtransport] Force Materialize Near refused convoy {0}: entity {1} has no authority record.", virtualConvoy.HeadKey, snapshot.EntityID);
				return false;
			}
			if
			(
				authority.Mode != RailEntityAuthorityMode.Virtual || authority.HeadID != virtualConvoy.HeadKey ||
				authority.ConvoyIndex != vehicleIndex || authority.Generation < snapshot.AuthorityGeneration
			)
			{
				ServerAPI.Logger.Warning
				(
					"[yangtransport] Force Materialize Near refused convoy {0}. Entity {1} authority is {2}/{3}, head {4}, index {5}. Snapshot generation is {6}.",
					virtualConvoy.HeadKey, snapshot.EntityID, authority.Mode, authority.Generation, authority.HeadID, authority.ConvoyIndex, snapshot.AuthorityGeneration
				);
				return false;
			}

			generations[vehicleIndex] = authority.Generation;
		}

		bool repaired = false;
		for (int vehicleIndex = 0; vehicleIndex < virtualConvoy.Vehicles.Count; vehicleIndex++)
		{
			VehicleSnapshot snapshot = virtualConvoy.Vehicles[vehicleIndex];
			if (snapshot.AuthorityGeneration == generations[vehicleIndex]) continue;

			snapshot.AuthorityGeneration = generations[vehicleIndex];
			repaired = true;
		}

		if (repaired)
		{
			ServerAPI.Logger.Notification("[yangtransport] Force Materialize Near repaired stale virtual authority for convoy {0}.", virtualConvoy.HeadKey);
			MarkVirtualDirty(virtualConvoy.HeadKey);
		}
		return true;
	}

	internal void DebugCollectVirtualMarkers(List<CartDebugMarker> destination, RailGraphLive graph)
	{
		foreach (var virtualConvoy in VirtualConvoys.Values)
		{
			if (!virtualConvoy.TrySampleVehicleCursor(graph, 0, out var markerCursor)) continue;
			if (!OffscreenRailCursor.TryGetWorldPose(graph, markerCursor, out var position, out _, out _)) continue;
			destination.Add(new CartDebugMarker { X = position.X, Z = position.Z, State = CartDebugMarker.StateVirtual });
		}
	}

	internal bool ServerStopVirtualConvoy(long headKey)
	{
		if (!VirtualConvoys.TryGetValue(headKey, out var virtualConvoy)) return false;

		virtualConvoy.StopNow();
		if (RailSystem != null) PublishVirtualCollisionBody(virtualConvoy, RailSystem.Graph);
		UpdateWaitingSignalIndex(virtualConvoy);
		ScheduleVirtual(virtualConvoy, ServerAPI?.World.ElapsedMilliseconds ?? 0);
		MarkVirtualDirty(virtualConvoy.HeadKey);
		return true;
	}

	internal bool ServerTryMoveVirtualConvoyForRepulsion(long headKey, double signedDistance, Vec3d desiredAwayDirection, out double distanceMoved)
	{
		distanceMoved = 0;
		if (!VirtualConvoys.TryGetValue(headKey, out var virtualConvoy)) return false;
		if (RailSystem == null) return false;

		RailGraphLive graph = RailSystem.Graph;
		double absoluteDistanceTravelled = virtualConvoy.StartingAbsoluteDistanceTravelled + virtualConvoy.AbsoluteDistanceSimulated;
		var advanceResult = virtualConvoy.AdvanceBodyTape(graph, RailSystem, signedDistance, 0, null, false, ref absoluteDistanceTravelled, out distanceMoved);
		if (advanceResult == OffscreenRailCursor.AdvanceResult.NoMove || Math.Abs(distanceMoved) <= MovementEpsilon) return false;

		virtualConvoy.StopNow();
		virtualConvoy.EnsureOccupancyCurrent(RailSystem, graph);
		UpdateVirtualRouteIndex(virtualConvoy);
		UpdateWaitingSignalIndex(virtualConvoy);
		RefreshMaterializationIndex(virtualConvoy, graph);
		PublishVirtualCollisionBody(virtualConvoy, graph);
		ScheduleVirtual(virtualConvoy, ServerAPI?.World.ElapsedMilliseconds ?? 0);

		MarkVirtualDirty(virtualConvoy.HeadKey);
		return true;
	}

	private static ConvoyRoute? CloneTapeForProbe(ConvoyRoute? pathTape) { return pathTape?.Clone(); }

	internal bool ServerProbeVirtualConvoyMove(long headKey, double signedDistance, out Vec3d delta)
	{
		delta = new Vec3d();
		if (!VirtualConvoys.TryGetValue(headKey, out var virtualConvoy)) return false;
		if (RailSystem == null) return false;

		RailGraphLive graph = RailSystem.Graph;
		if (!virtualConvoy.TrySampleVehicleCursor(graph, 0, out var cursor)) return false;

		if (!OffscreenRailCursor.TryGetWorldPose(graph, in cursor, out Vec3d positionBefore, out _, out _)) return false;

		ConvoyRoute? savedPathTape = CloneTapeForProbe(virtualConvoy.PathTape);
		double savedPathHeadDistance = virtualConvoy.PathHeadDistance;
		var savedCursor = virtualConvoy.Cursor;
		double savedDistance = virtualConvoy.DistanceSimulated;
		double savedAbsoluteDistance = virtualConvoy.AbsoluteDistanceSimulated;
		bool savedTerminalStopped = virtualConvoy.TerminalStopped;
		double savedTerminalElapsedSec = virtualConvoy.TerminalElapsedSec;
		double savedMaxElapsedSeconds = virtualConvoy.MaxElapsedSeconds;

		double absoluteDistanceTravelled = virtualConvoy.StartingAbsoluteDistanceTravelled + virtualConvoy.AbsoluteDistanceSimulated;
		var advanceResult = virtualConvoy.AdvanceBodyTape(graph, RailSystem, signedDistance, 0, null, false, ref absoluteDistanceTravelled, out double distanceMoved);
		if (advanceResult == OffscreenRailCursor.AdvanceResult.NoMove || Math.Abs(distanceMoved) <= MovementEpsilon)
		{
			virtualConvoy.PathTape = savedPathTape;
			virtualConvoy.PathHeadDistance = savedPathHeadDistance;
			virtualConvoy.Cursor = savedCursor;
			virtualConvoy.DistanceSimulated = savedDistance;
			virtualConvoy.AbsoluteDistanceSimulated = savedAbsoluteDistance;
			virtualConvoy.TerminalStopped = savedTerminalStopped;
			virtualConvoy.TerminalElapsedSec = savedTerminalElapsedSec;
			virtualConvoy.MaxElapsedSeconds = savedMaxElapsedSeconds;
			return false;
		}

		Vec3d positionAfter = null!;
		bool obtainedPositionAfter = virtualConvoy.TrySampleVehicleCursor(graph, 0, out cursor) && OffscreenRailCursor.TryGetWorldPose(graph, in cursor, out positionAfter, out _, out _);

		virtualConvoy.PathTape = savedPathTape;
		virtualConvoy.PathHeadDistance = savedPathHeadDistance;
		virtualConvoy.Cursor = savedCursor;
		virtualConvoy.DistanceSimulated = savedDistance;
		virtualConvoy.AbsoluteDistanceSimulated = savedAbsoluteDistance;
		virtualConvoy.TerminalStopped = savedTerminalStopped;
		virtualConvoy.TerminalElapsedSec = savedTerminalElapsedSec;
		virtualConvoy.MaxElapsedSeconds = savedMaxElapsedSeconds;

		if (!obtainedPositionAfter) return false;

		delta.Set(positionAfter.X - positionBefore.X, positionAfter.Y - positionBefore.Y, positionAfter.Z - positionBefore.Z);
		return true;
	}


	internal bool ServerMarkVirtualBoilerExplosion(long headKey)
	{
		if (!VirtualConvoys.TryGetValue(headKey, out var virtualConvoy)) return false;
		if (!virtualConvoy.HasCollisionBoiler) return false;

		virtualConvoy.ReleaseOccupancy(RailSystem);
		RemoveVirtualRuntime(virtualConvoy);

		VirtualConvoys.Remove(headKey);
		AuthoritySystem?.RetireVirtualAuthority(virtualConvoy.HeadKey, BuildAuthorityMembers(virtualConvoy.Vehicles));
		return true;
	}

	// Pending collection
	private static bool TryCollectStationCandidates(RailStationRegistrySystem? stationRegistry, TimetableRouteEntryPacket routeEntry, byte gauge, int dimension, List<RailStationRegistrySystem.RailStationEntry> destination, out bool isStationGroup)
	{
		isStationGroup = false;
		if (destination == null) return false;

		destination.Clear();
		if (stationRegistry == null || routeEntry == null) return false;

		if (routeEntry.NameHash != 0)
		{
			stationRegistry.CollectStationsByNameHash(routeEntry.NameHash, destination, gauge, dimension);
			if (destination.Count > 0) { isStationGroup = destination.Count > 1; return true; }
		}

		var stationKey = new RailStationRegistrySystem.StationBlockKey(routeEntry.X, routeEntry.Y, routeEntry.Z, routeEntry.Dimension);
		if
		(
			stationRegistry.TryGet(stationKey, out RailStationRegistrySystem.RailStationEntry stationEntry)
			&& stationEntry.HasStopEndpoint && stationEntry.Gauge == gauge && stationEntry.Key.Dimension == dimension
		) { destination.Add(stationEntry); return true; }

		return false;
	}

	private static bool TryGetCursorDimension(RailGraphLive graph, ulong edgeHash, out int dimension)
	{
		dimension = 0;
		if (graph == null || edgeHash == 0) return false;
		if (!graph.TryGetEdgeEndpoints(edgeHash, out RailGraphLive.EndpointKey endpoint, out _, out _)) return false;

		dimension = endpoint.Dimension;
		return true;
	}

	private static TimetableRouteEntryPacket[] CloneRoute(IReadOnlyList<TimetableRouteEntryPacket>? timetableRoute)
	{
		if (timetableRoute == null || timetableRoute.Count == 0) return Array.Empty<TimetableRouteEntryPacket>();

		TimetableRouteEntryPacket[] routeCopy = new TimetableRouteEntryPacket[timetableRoute.Count];
		for (int routeEntryIndex = 0; routeEntryIndex < routeCopy.Length; routeEntryIndex++)
		{
			routeCopy[routeEntryIndex] = timetableRoute[routeEntryIndex]?.Clone() ?? new TimetableRouteEntryPacket();
		}
		return routeCopy;
	}

	private static void WriteRouteEntry(BinaryWriter binaryWriter, TimetableRouteEntryPacket routeEntry)
	{
		binaryWriter.Write(routeEntry.StationName ?? "");
		binaryWriter.Write(routeEntry.NameHash);
		binaryWriter.Write(routeEntry.X);
		binaryWriter.Write(routeEntry.Y);
		binaryWriter.Write(routeEntry.Z);
		binaryWriter.Write(routeEntry.Dimension);
		binaryWriter.Write(routeEntry.TimeElapsedSeconds);
		binaryWriter.Write(routeEntry.MileageLeftMinutes);
		binaryWriter.Write(routeEntry.AnnounceOnArrival);
		binaryWriter.Write(routeEntry.AnnounceOnDeparture);
	}

	private static TimetableRouteEntryPacket ReadRouteEntry(BinaryReader binaryReader)
	{
		return new TimetableRouteEntryPacket
		{
			StationName = binaryReader.ReadString(),
			NameHash = binaryReader.ReadUInt64(),
			X = binaryReader.ReadInt32(),
			Y = binaryReader.ReadInt32(),
			Z = binaryReader.ReadInt32(),
			Dimension = binaryReader.ReadInt32(),
			TimeElapsedSeconds = binaryReader.ReadInt32(),
			MileageLeftMinutes = binaryReader.ReadInt32(),
			AnnounceOnArrival = binaryReader.ReadBoolean(),
			AnnounceOnDeparture = binaryReader.ReadBoolean()
		};
	}

	private sealed class PendingConvoy
	{
		public readonly long HeadKey;
		public readonly long CreatedMS;
		public long LastSeenMS;

		public bool Invalid;
		public bool HeadSeen;
		public bool HasHead;
		public bool HasConductorLocust;
		public int ExpectedCount;
		public byte Gauge = byte.MaxValue;

		public double InitialSpeed;
		public double RollingResistanceConstant = DefaultVirtualRollingResistanceConstant;
		public double RollingResistanceSpeedCoefficient = DefaultVirtualRollingResistanceSpeedCoefficient;
		public double StartingAbsoluteDistanceTravelled;
		public int StartingStandardGaugeLeadEnd = (int)SGTrainEnd.EndA;
		public double StartingPathHeadDistance;
		public ConvoyRoute? StartPathTape;

		public OffscreenRailCursor.Cursor StartCursor;

		private readonly List<VehicleSnapshot?> Snapshots = new();
		public int CollectedCount { get; private set; }

		public PendingConvoy(long headKey, long createdMS)
		{
			HeadKey = headKey;
			CreatedMS = createdMS;
			LastSeenMS = createdMS;
			ExpectedCount = 0;
		}

		public void AddSnapshot(int snapshotIndex, VehicleSnapshot snapshot)
		{
			if (snapshotIndex < 0) snapshotIndex = 0;
			while (Snapshots.Count <= snapshotIndex) Snapshots.Add(null);

			if (Snapshots[snapshotIndex] == null) CollectedCount++;
			Snapshots[snapshotIndex] = snapshot;
		}

		public List<VehicleSnapshot>? BuildOrderedSnapshots()
		{
			if (ExpectedCount <= 0) return null;
			if (Snapshots.Count < ExpectedCount) return null;

			var orderedSnapshots = new List<VehicleSnapshot>(ExpectedCount);
			for (int snapshotIndex = 0; snapshotIndex < ExpectedCount; snapshotIndex++)
			{
				var snapshot = Snapshots[snapshotIndex];
				if (snapshot == null) return null;
				orderedSnapshots.Add(snapshot);
			}
			return orderedSnapshots;
		}
	}

	// Virtual state stuffs
	private enum VirtualAutomationStatus
	{
		// No virtual polling wake.
		// A non-signal obstruction can only change while its chunks are loaded,
		// at which point chunk/materialization events hand the consist back to loaded simulation.
		None = 0,
		Going = 1,
		WaitingAtStation = 2,
		Stuck = 3,
		WaitingForSignal = 4,
		NoTimetable = 5,
		WaitingForPathBudget = 6
	}

	private sealed class VehicleSnapshot
	{
		public readonly long EntityID;
		public long AuthorityGeneration;
		public readonly string Code;
		public readonly byte[] Bytes;
		public readonly FrozenVehiclePose CapturedPose;

		public readonly byte Gauge;
		public readonly int ConvoyIndex;
		public readonly double DistanceBehindHead;
		public readonly double SpacingToNext;
		public readonly double OccupancyRearExtent;
		public readonly double OccupancyFrontExtent;
		public readonly int SelfWeight;
		public readonly bool HasTractionEngine;
		public readonly bool HasConductorLocust;

		public readonly int DriveDirection;
		public readonly int TurnPhase;
		public readonly double FuelSeconds;
		public readonly double WaterSeconds;
		public readonly double DriveSpeedPotentialBPS;
		public readonly double DriveSpeedLimitBPS;

		public readonly TimetableRouteEntryPacket[] TimetableRoute;
		public readonly int TimetableCurrentIndex;

		public readonly bool HasRefrigeration;
		public readonly float RefrigerationPerishMultiplier;
		public readonly double RefrigerationCapacityAtCapture;
		public double RefrigerationSecondsRemaining;
		public bool RefrigerationBoundaryApplied;
		public TreeAttribute? RefrigeratedCargoOverride;
		private VehicleSnapshot
		(
			long entityID,
			long authorityGeneration,
			string code,
			byte[] bytes,
			FrozenVehiclePose capturedPose,
			byte gauge,
			int convoyIndex,
			double distanceBehindHead,
			double spacingToNext,
			double occupancyRearExtent,
			double occupancyFrontExtent,
			int selfWeight,
			bool hasTractionEngine,
			bool hasConductorLocust,
			int driveDirection,
			int turnPhase,
			double fuelSeconds,
			double waterSeconds,
			double driveSpeedPotentialBPS,
			double driveSpeedLimitBPS,
			TimetableRouteEntryPacket[] timetableRoute,
			int timetableCurrentIndex,
			bool hasRefrigeration,
			float refrigerationPerishMultiplier,
			double refrigerationCapacityAtCapture,
			double refrigerationSecondsRemaining,
			bool refrigerationBoundaryApplied,
			TreeAttribute? refrigeratedCargoOverride
		)
		{
			EntityID = entityID;
			AuthorityGeneration = authorityGeneration;
			Code = code;
			Bytes = bytes;
			CapturedPose = capturedPose;
			Gauge = gauge;
			ConvoyIndex = convoyIndex;
			DistanceBehindHead = distanceBehindHead;
			SpacingToNext = spacingToNext;
			OccupancyRearExtent = occupancyRearExtent;
			OccupancyFrontExtent = occupancyFrontExtent;
			SelfWeight = selfWeight;
			HasTractionEngine = hasTractionEngine;
			HasConductorLocust = hasConductorLocust;
			DriveDirection = driveDirection;
			TurnPhase = turnPhase;
			FuelSeconds = fuelSeconds;
			WaterSeconds = waterSeconds;
			DriveSpeedPotentialBPS = driveSpeedPotentialBPS;
			DriveSpeedLimitBPS = driveSpeedLimitBPS;
			TimetableRoute = timetableRoute ?? Array.Empty<TimetableRouteEntryPacket>();
			TimetableCurrentIndex = timetableCurrentIndex;
			HasRefrigeration = hasRefrigeration;
			RefrigerationPerishMultiplier = Math.Max(0, refrigerationPerishMultiplier);
			RefrigerationCapacityAtCapture = Math.Max(0, refrigerationCapacityAtCapture);
			RefrigerationSecondsRemaining = Math.Max(0, refrigerationSecondsRemaining);
			RefrigerationBoundaryApplied = refrigerationBoundaryApplied;
			RefrigeratedCargoOverride = refrigeratedCargoOverride;
		}

		public static VehicleSnapshot Capture(IRailwayConvoyVehicle vehicle)
		{
			Entity entity = vehicle.Entity;

			EntityBehaviorSGRefrigeration? refrigeration = entity.GetBehavior<EntityBehaviorSGRefrigeration>();
			refrigeration?.OfflineSimPrepareForCapture();

			bool hasRefrigeration = refrigeration != null;
			float refrigerationPerishMultiplier = refrigeration?.OfflineSimPerishMultiplier ?? 1f;
			double refrigerationCapacity = refrigeration?.OfflineSimEstimateCapacitySecondsRemaining() ?? 0;

			using var memoryStream = new MemoryStream(512);
			using var binaryWriter = new BinaryWriter(memoryStream);
			entity.ToBytes(binaryWriter, forClient: false);

			int convoyIndex = Math.Max(0, vehicle.ConvoyOrderIndex);
			double distanceBehindHead = convoyIndex == 0 ? 0 : Math.Max(0, vehicle.ConvoyDistanceBehindHead);

			int driveDirection = 0;
			int turnPhase = entity.WatchedAttributes.GetInt(AttributeTurnLeverPhase, DefaultTurnLeverPhase);
			double fuelSeconds = 0;
			double waterSeconds = 0;
			double driveSpeedPotentialBPS = 0;
			double driveSpeedLimitBPS = 0;

			var steamEngineBehavior = vehicle.SteamEngineBehaviour;
			if (steamEngineBehavior != null)
			{
				steamEngineBehavior.OfflineSimTryGetDriveDir(out driveDirection);
				steamEngineBehavior.OfflineSimTryGetDrivePotential(out driveSpeedPotentialBPS, out driveSpeedLimitBPS);
				fuelSeconds = steamEngineBehavior.OfflineSimEstimateFuelSecondsRemaining();
				waterSeconds = steamEngineBehavior.OfflineSimEstimateWaterSecondsRemaining();
			}

			TimetableRouteEntryPacket[] timetableRoute = Array.Empty<TimetableRouteEntryPacket>();
			int timetableCurrentIndex = -1;
			if (vehicle.HasTractionEngine)
			{
				ConductorTimetableDocument timetableDocument = ConductorTimetableStorage.Load(entity);
				timetableRoute = CloneRoute(timetableDocument.Route);
				timetableCurrentIndex = timetableDocument.CurrentStationIndex;
			}

			return new VehicleSnapshot
			(
				entity.EntityId,
				RailConvoyAuthoritySystem.ReadEntityGeneration(entity),
				entity.Code.ToShortString(),
				memoryStream.ToArray(),
				new FrozenVehiclePose(entity.ServerPos.X, entity.ServerPos.Y, entity.ServerPos.Z, entity.ServerPos.Dimension, entity.ServerPos.Yaw, entity.ServerPos.Roll),
				vehicle.TrackGauge,
				convoyIndex,
				distanceBehindHead,
				Math.Max(0.1, vehicle.ConvoySpacingToNext),
				Math.Max(0, vehicle.OccupancyRearExtentBlocks),
				Math.Max(0, vehicle.OccupancyFrontExtentBlocks),
				Math.Max(1, vehicle.SelfWeightCached),
				vehicle.HasTractionEngine,
				vehicle.HasConductorLocustInstalled,
				driveDirection,
				turnPhase,
				Math.Max(0, fuelSeconds),
				Math.Max(0, waterSeconds),
				Math.Max(0, driveSpeedPotentialBPS),
				driveSpeedLimitBPS,
				timetableRoute,
				timetableCurrentIndex,
				hasRefrigeration,
				refrigerationPerishMultiplier,
				refrigerationCapacity,
				refrigerationCapacity,
				refrigerationBoundaryApplied: false,
				refrigeratedCargoOverride: null
			);
		}

		public void ApplyVirtualOverrides(Entity entity)
		{
			if (entity == null || RefrigeratedCargoOverride == null) return;
			entity.WatchedAttributes[EntityBehaviorSGStorage.InventoryTreeAttribute] = (IAttribute)RefrigeratedCargoOverride.Clone();
		}

		public void Serialize(BinaryWriter binaryWriter)
		{
			binaryWriter.Write(EntityID);
			binaryWriter.Write(AuthorityGeneration);
			binaryWriter.Write(Code);
			binaryWriter.Write(Bytes.Length);
			binaryWriter.Write(Bytes);
			CapturedPose.Serialize(binaryWriter);

			binaryWriter.Write(Gauge);
			binaryWriter.Write(ConvoyIndex);
			binaryWriter.Write(DistanceBehindHead);
			binaryWriter.Write(SpacingToNext);
			binaryWriter.Write(OccupancyRearExtent);
			binaryWriter.Write(OccupancyFrontExtent);
			binaryWriter.Write(SelfWeight);
			binaryWriter.Write(HasTractionEngine);
			binaryWriter.Write(HasConductorLocust);

			binaryWriter.Write(DriveDirection);
			binaryWriter.Write(TurnPhase);
			binaryWriter.Write(FuelSeconds);
			binaryWriter.Write(WaterSeconds);
			binaryWriter.Write(DriveSpeedPotentialBPS);
			binaryWriter.Write(DriveSpeedLimitBPS);

			binaryWriter.Write(TimetableCurrentIndex);
			binaryWriter.Write(TimetableRoute.Length);
			for (int routeEntryIndex = 0; routeEntryIndex < TimetableRoute.Length; routeEntryIndex++) WriteRouteEntry(binaryWriter, TimetableRoute[routeEntryIndex]);

			binaryWriter.Write(HasRefrigeration);
			if (HasRefrigeration)
			{
				binaryWriter.Write(RefrigerationPerishMultiplier);
				binaryWriter.Write(RefrigerationCapacityAtCapture);
				binaryWriter.Write(RefrigerationSecondsRemaining);
				binaryWriter.Write(RefrigerationBoundaryApplied);
				binaryWriter.Write(RefrigeratedCargoOverride != null);
				RefrigeratedCargoOverride?.ToBytes(binaryWriter);
			}
		}

		public static VehicleSnapshot Deserialize(BinaryReader binaryReader)
		{
			long entityID = binaryReader.ReadInt64();
			long authorityGeneration = binaryReader.ReadInt64();
			if (entityID == 0 || authorityGeneration < 0) throw new InvalidDataException("Invalid vehicle authority generation.");
			string code = binaryReader.ReadString();
			if (string.IsNullOrWhiteSpace(code) || code.Length > 512) throw new InvalidDataException("Invalid entity snapshot type code.");
			int byteLength = binaryReader.ReadInt32();
			if (byteLength < 0 || byteLength > 64 * 1024 * 1024) throw new InvalidDataException($"Invalid entity snapshot length {byteLength}.");
			byte[] bytes = binaryReader.ReadBytes(byteLength);
			if (bytes.Length != byteLength) throw new EndOfStreamException("Truncated entity snapshot.");
			FrozenVehiclePose capturedPose = FrozenVehiclePose.Deserialize(binaryReader);

			byte gauge = binaryReader.ReadByte();
			int convoyIndex = binaryReader.ReadInt32();
			double distanceBehindHead = binaryReader.ReadDouble();
			double spacingToNext = binaryReader.ReadDouble();
			double occupancyRearExtent = binaryReader.ReadDouble();
			double occupancyFrontExtent = binaryReader.ReadDouble();
			int selfWeight = binaryReader.ReadInt32();
			bool hasTractionEngine = binaryReader.ReadBoolean();
			bool hasConductorLocust = binaryReader.ReadBoolean();

			int driveDirection = binaryReader.ReadInt32();
			int turnPhase = binaryReader.ReadInt32();
			double fuelSeconds = binaryReader.ReadDouble();
			double waterSeconds = binaryReader.ReadDouble();
			double driveSpeedPotentialBPS = binaryReader.ReadDouble();
			double driveSpeedLimitBPS = binaryReader.ReadDouble();

			int timetableCurrentIndex = binaryReader.ReadInt32();
			int timetableCount = binaryReader.ReadInt32();
			if (timetableCount < 0 || timetableCount > MaxSerializedRouteEntries) throw new InvalidDataException($"Invalid timetable route count {timetableCount}.");
			TimetableRouteEntryPacket[] timetableRoute = new TimetableRouteEntryPacket[timetableCount];
			for (int routeEntryIndex = 0; routeEntryIndex < timetableRoute.Length; routeEntryIndex++) timetableRoute[routeEntryIndex] = ReadRouteEntry(binaryReader);

			bool hasRefrigeration = binaryReader.ReadBoolean();
			float refrigerationPerishMultiplier = 1f;
			double refrigerationCapacityAtCapture = 0;
			double refrigerationSecondsRemaining = 0;
			bool refrigerationBoundaryApplied = false;
			TreeAttribute? refrigeratedCargoOverride = null;

			if (hasRefrigeration)
			{
				refrigerationPerishMultiplier = Math.Max(0, binaryReader.ReadSingle());
				refrigerationCapacityAtCapture = Math.Max(0, binaryReader.ReadDouble());
				refrigerationSecondsRemaining = Math.Max(0, binaryReader.ReadDouble());
				refrigerationBoundaryApplied = binaryReader.ReadBoolean();

				if (binaryReader.ReadBoolean())
				{
					refrigeratedCargoOverride = new TreeAttribute();
					refrigeratedCargoOverride.FromBytes(binaryReader);
				}
			}

			return new VehicleSnapshot
			(
				entityID, authorityGeneration, code, bytes, capturedPose, gauge, convoyIndex, distanceBehindHead,
				spacingToNext, occupancyRearExtent, occupancyFrontExtent, selfWeight, hasTractionEngine, hasConductorLocust,
				driveDirection, turnPhase, fuelSeconds, waterSeconds, driveSpeedPotentialBPS, driveSpeedLimitBPS,
				timetableRoute, timetableCurrentIndex,
				hasRefrigeration, refrigerationPerishMultiplier, refrigerationCapacityAtCapture, refrigerationSecondsRemaining,
				refrigerationBoundaryApplied, refrigeratedCargoOverride
			);
		}

		internal void ValidateEntityForLoad(ICoreServerAPI serverAPI)
		{
			// Use the actual materialization reader, the temporary entity is never initialized or registered.
			Entity entity = CreateEntityFromBytes(serverAPI);
			if
			(
				entity.EntityId != EntityID || !new AssetLocation(Code).Equals(entity.Code) ||
				!OffscreenRailVehicleAdapter.TryGetSupported(entity, out IRailwayConvoyVehicle vehicle) || vehicle.TrackGauge != Gauge
			) { throw new InvalidDataException($"Entity snapshot does not match vehicle {EntityID}."); }
		}

		public Entity CreateEntityFromBytes(ICoreServerAPI serverAPI)
		{
			var entityType = serverAPI.World.GetEntityType(new AssetLocation(Code));
			if (entityType == null) throw new Exception("Unknown entity type: " + Code);

			Entity entity = serverAPI.World.ClassRegistry.CreateEntity(entityType);

			using var memoryStream = new MemoryStream(Bytes);
			using var binaryReader = new BinaryReader(memoryStream);
			entity.FromBytes(binaryReader, isSync: false, serversideRemaps: null);

			return entity;
		}

		public bool AdvanceRefrigeration(ICoreServerAPI serverAPI, double elapsedSeconds)
		{
			if (!HasRefrigeration || RefrigerationSecondsRemaining <= EntityBehaviorSGRefrigeration.TimeEpsilonSeconds || elapsedSeconds <= 0) return false;

			RefrigerationSecondsRemaining = Math.Max(0, RefrigerationSecondsRemaining - elapsedSeconds);
			if (RefrigerationSecondsRemaining > EntityBehaviorSGRefrigeration.TimeEpsilonSeconds || RefrigerationBoundaryApplied) return true;

			try { ApplyRefrigeratedCargoBoundary(serverAPI); }
			catch (Exception exception)
			{
				serverAPI.Logger.Error("[yangtransport] Failed to update refrigerated cargo at virtual refrigeration expiry for entity {0}.", EntityID);
				serverAPI.Logger.Error(exception);
			}

			RefrigerationBoundaryApplied = true;
			return true;
		}

		public long GetRefrigerationWakeDelayMS()
		{
			if (!HasRefrigeration || RefrigerationSecondsRemaining <= EntityBehaviorSGRefrigeration.TimeEpsilonSeconds) return long.MaxValue;
			return Math.Max(1, (long)Math.Ceiling(RefrigerationSecondsRemaining * 1000.0));
		}

		public void ApplyRefrigerationAfterMaterialization(Entity entity)
		{
			if (!HasRefrigeration || entity == null) return;

			EntityBehaviorSGRefrigeration? refrigeration = entity.GetBehavior<EntityBehaviorSGRefrigeration>();
			if (refrigeration == null) return;

			double consumedSeconds = Math.Max(0, RefrigerationCapacityAtCapture - RefrigerationSecondsRemaining);
			refrigeration.OfflineSimFastForward(consumedSeconds, RefrigerationBoundaryApplied);
		}

		private void ApplyRefrigeratedCargoBoundary(ICoreServerAPI serverAPI)
		{
			Entity snapshotEntity = CreateEntityFromBytes(serverAPI);
			TreeAttribute? sourceRoot = RefrigeratedCargoOverride ??
				snapshotEntity.WatchedAttributes[EntityBehaviorSGStorage.InventoryTreeAttribute] as TreeAttribute;
			if (sourceRoot == null) return;

			TreeAttribute updatedRoot = (TreeAttribute)sourceRoot.Clone();

			for (int keyIndex = 0; keyIndex < updatedRoot.Keys.Length; keyIndex++)
			{
				string key = updatedRoot.Keys[keyIndex];
				if (updatedRoot[key] is not TreeAttribute inventoryTree) continue;

				int slotCount = inventoryTree.GetInt("qslots", 0);
				if (slotCount <= 0 || slotCount > 4096) continue;

				var inventory = new InventoryGeneric(slotCount, $"virtual-refrigerated-{EntityID}-{key}", serverAPI);
				inventory.FromTreeAttributes(inventoryTree);
				inventory.OnAcquireTransitionSpeed += (transitionType, stack, configuredMultiplier) =>
					transitionType == EnumTransitionType.Perish ? RefrigerationPerishMultiplier : 1f;

				for (int slotIndex = 0; slotIndex < inventory.Count; slotIndex++)
				{
					ItemSlot slot = inventory[slotIndex];
					if (!slot.Empty) slot.Itemstack.Collectible.UpdateAndGetTransitionStates(serverAPI.World, slot);
				}

				var updatedInventoryTree = new TreeAttribute();
				inventory.ToTreeAttributes(updatedInventoryTree);
				updatedRoot[key] = updatedInventoryTree;
			}

			RefrigeratedCargoOverride = updatedRoot;
		}
	}

	private sealed class VirtualAutomationState
	{
		public readonly TimetableRouteEntryPacket[] Route;
		public int CurrentStationIndex;
		public readonly double DriveMaxSpeedBPS;
		public readonly double FuelWaterSecondsAtCapture;

		public RailAutomationRoute? ActiveRoute;
		public StationPathTarget Target;
		public bool TargetIsStationGroup;
		public int TargetRouteIndex = -1;

		// ActiveRoute is an executable lease. Rebuilds happen beside it and only replace it after a complete successor route has been found.
		public bool RefreshRequested;
		public bool PinnedTargetInvalidated;

		public ulong LastChainRepathOccupancySerial;
		public RailGraphLive.EndpointKey LastChainRepathSignalEndpoint;
		public ulong LastChainRepathFromEdgeHash;

		public double ArrivedStationElapsedSec = -1;
		public double BurnConsumedSec;
		public double CurrentSpeedBPS;
		public VirtualAutomationStatus Status = VirtualAutomationStatus.None;

		public VirtualAutomationState(TimetableRouteEntryPacket[] route, int currentStationIndex, double driveMaxSpeedBPS, double fuelWaterSecondsAtCapture)
		{
			Route = OffscreenConvoySimSystem.CloneRoute(route);
			CurrentStationIndex = currentStationIndex >= 0 && currentStationIndex < Route.Length ? currentStationIndex : -1;
			DriveMaxSpeedBPS = Math.Max(0, driveMaxSpeedBPS);
			FuelWaterSecondsAtCapture = Math.Max(0, fuelWaterSecondsAtCapture);
		}


		public int CurrentTargetIndex => Route.Length == 0 ? -1 : CurrentStationIndex >= 0 && CurrentStationIndex < Route.Length ? CurrentStationIndex : 0;

		public void AdoptRouteLease(in RailAutomationRouteLease lease)
		{
			if (lease.Route == null) return;
			ActiveRoute = lease.Route;
			Target = lease.Route.Target;
			TargetIsStationGroup = lease.TargetIsStationGroup;
			TargetRouteIndex = lease.TargetRouteIndex;
			RefreshRequested = lease.RefreshRequested;
			PinnedTargetInvalidated = lease.PinnedTargetInvalidated;
			LastChainRepathOccupancySerial = 0;
			LastChainRepathSignalEndpoint = default;
			LastChainRepathFromEdgeHash = 0;
		}

		public bool TryCreateRouteLease(out RailAutomationRouteLease lease)
		{
			lease = default;
			if (ActiveRoute == null) return false;

			lease = new RailAutomationRouteLease(ActiveRoute, TargetRouteIndex, TargetIsStationGroup, RefreshRequested, PinnedTargetInvalidated);
			return true;
		}

		public void SetRoute(RailAutomationRoute route, int targetRouteIndex, bool targetIsStationGroup)
		{
			ActiveRoute = route;
			Target = route.Target;
			TargetIsStationGroup = targetIsStationGroup;
			TargetRouteIndex = targetRouteIndex;
			RefreshRequested = false;
			PinnedTargetInvalidated = false;
			LastChainRepathOccupancySerial = 0;
			LastChainRepathSignalEndpoint = default;
			LastChainRepathFromEdgeHash = 0;
			ArrivedStationElapsedSec = -1;
		}

		public void ResetRoute()
		{
			ActiveRoute = null;
			Target = default;
			TargetIsStationGroup = false;
			TargetRouteIndex = -1;
			RefreshRequested = false;
			PinnedTargetInvalidated = false;
			LastChainRepathOccupancySerial = 0;
			LastChainRepathSignalEndpoint = default;
			LastChainRepathFromEdgeHash = 0;
			ArrivedStationElapsedSec = -1;
		}

		public TimetableRouteEntryPacket[] CloneRoute() => OffscreenConvoySimSystem.CloneRoute(Route);

		public void Serialize(BinaryWriter binaryWriter)
		{
			binaryWriter.Write(CurrentStationIndex);
			binaryWriter.Write(DriveMaxSpeedBPS);
			binaryWriter.Write(FuelWaterSecondsAtCapture);
			binaryWriter.Write(ArrivedStationElapsedSec);
			binaryWriter.Write(BurnConsumedSec);
			binaryWriter.Write(CurrentSpeedBPS);
			binaryWriter.Write((int)Status);

			binaryWriter.Write(Route.Length);
			for (int routeEntryIndex = 0; routeEntryIndex < Route.Length; routeEntryIndex++) WriteRouteEntry(binaryWriter, Route[routeEntryIndex]);
		}

		public static VirtualAutomationState Deserialize(BinaryReader binaryReader)
		{
			int currentStationIndex = binaryReader.ReadInt32();
			double driveMaxSpeedBPS = binaryReader.ReadDouble();
			double fuelWaterSeconds = binaryReader.ReadDouble();
			double arrivedStationElapsedSec = binaryReader.ReadDouble();
			double burnConsumedSec = binaryReader.ReadDouble();
			double currentSpeedBPS = binaryReader.ReadDouble();
			VirtualAutomationStatus status = (VirtualAutomationStatus)binaryReader.ReadInt32();

			int routeEntryCount = binaryReader.ReadInt32();
			if (routeEntryCount < 0 || routeEntryCount > MaxSerializedRouteEntries)
				throw new InvalidDataException($"Invalid automation route count {routeEntryCount}.");
			TimetableRouteEntryPacket[] timetableRoute = new TimetableRouteEntryPacket[routeEntryCount];
			for (int routeEntryIndex = 0; routeEntryIndex < timetableRoute.Length; routeEntryIndex++) timetableRoute[routeEntryIndex] = ReadRouteEntry(binaryReader);

			var automationState = new VirtualAutomationState(timetableRoute, currentStationIndex, driveMaxSpeedBPS, fuelWaterSeconds)
			{
				ArrivedStationElapsedSec = arrivedStationElapsedSec,
				BurnConsumedSec = burnConsumedSec,
				CurrentSpeedBPS = currentSpeedBPS,
				Status = status
			};

			return automationState;
		}
	}


	private readonly struct FrozenVehiclePose
	{
		internal readonly double X;
		internal readonly double Y;
		internal readonly double Z;
		internal readonly int Dimension;
		internal readonly float Yaw;
		internal readonly float Roll;

		internal FrozenVehiclePose(double x, double y, double z, int dimension, float yaw, float roll)
		{
			X = x; Y = y; Z = z;
			Dimension = dimension;
			Yaw = yaw;
			Roll = roll;
		}

		internal void Serialize(BinaryWriter writer)
		{
			writer.Write(X);
			writer.Write(Y);
			writer.Write(Z);
			writer.Write(Dimension);
			writer.Write(Yaw);
			writer.Write(Roll);
		}

		internal static FrozenVehiclePose Deserialize(BinaryReader reader)
		{
			return new FrozenVehiclePose
			(
				reader.ReadDouble(),
				reader.ReadDouble(),
				reader.ReadDouble(),
				reader.ReadInt32(),
				reader.ReadSingle(),
				reader.ReadSingle()
			);
		}
	}


	private sealed class VirtualConvoy
	{
		private const double PathTapeTrailingRetentionBlocks = RailTrainCollisionSystem.RouteHistoryRequiredBlocks;

		public readonly long HeadKey;
		public readonly byte Gauge;
		public readonly int WantTurnTravel;
		public readonly double InitialSpeed;
		public readonly double RollingDeceleration;
		public readonly double PoweredSeconds;
		public readonly double StopSeconds;
		public readonly double StartingAbsoluteDistanceTravelled;
		public readonly int LeadIndex;
		public readonly SGTrainEnd LeadEnd;
		public readonly VirtualAutomationState? Automation;

		public double MaxElapsedSeconds;

		public readonly List<VehicleSnapshot> Vehicles = new(8);
		public readonly List<FrozenVehiclePose> FrozenPoses = new(8);
		private List<VehicleSnapshot>? RefrigerationVehicles;
		public bool Orphaned;

		private readonly HashSet<ulong> DesiredEdges = new();
		private readonly HashSet<ulong> PublishedOccupancyEdges = new();
		private int PublishedOccupancyGraphVersion = -1;

		public OffscreenRailCursor.Cursor Cursor;
		public ConvoyRoute? PathTape;
		public double PathHeadDistance;
		private readonly ConvoyRouteRecorder TapeRecorder = new();

		// Runtime-only reverse-index state used to target graph mutations.
		public ConvoyRoute? IndexedTape;
		public uint IndexedTapeTopologyRevision = uint.MaxValue;
		public RailAutomationRoute? IndexedAutomationRoute;
		public readonly HashSet<ulong> IndexedRouteEdges = new();

		// Runtime-only scheduling and materialization indexes.
		public long NextWakeMS = long.MaxValue;
		public long WakeGeneration;
		public readonly HashSet<long> IndexedMaterializationColumns = new();
		public readonly HashSet<long> DesiredMaterializationColumns = new();
		public bool MaterializationPoseValid;
		public bool HasIndexedWaitingSignal;
		public RailGraphLive.EndpointKey IndexedWaitingSignal;
		public ulong IndexedWaitingStationNameHash;
		public bool HasIndexedWaitingStationKey;
		public RailStationRegistrySystem.StationBlockKey IndexedWaitingStationKey;

		public bool HasPathTape => PathTape != null;

		public void AddVehicleSnapshot(VehicleSnapshot snapshot)
		{
			Vehicles.Add(snapshot);
			if (snapshot.HasRefrigeration) (RefrigerationVehicles ??= new List<VehicleSnapshot>(1)).Add(snapshot);
		}

		public void EnsureFrozenPoseCapacity()
		{
			while (FrozenPoses.Count < Vehicles.Count) FrozenPoses.Add(default);
			if (FrozenPoses.Count > Vehicles.Count) FrozenPoses.RemoveRange(Vehicles.Count, FrozenPoses.Count - Vehicles.Count);
		}

		public void BecomeOrphaned(RailGraphServerSystem? railSystem, RailGraphLive graph)
		{
			Orphaned = true;
			TerminalStopped = true;
			TerminalElapsedSec = ElapsedSimulationSec;
			MaxElapsedSeconds = ElapsedSimulationSec;
			if (Automation != null)
			{
				Automation.ResetRoute();
				Automation.CurrentSpeedBPS = 0;
				Automation.Status = VirtualAutomationStatus.Stuck;
			}
			RetainValidOccupancy(railSystem, graph);
		}

		public void RetainValidOccupancy(RailGraphServerSystem? railSystem, RailGraphLive graph)
		{
			if (railSystem == null || graph == null) return;
			DesiredEdges.Clear();
			foreach (ulong edgeHash in PublishedOccupancyEdges)
			{
				if (!graph.ContainsEdge(edgeHash)) continue;
				if (!graph.TryGetOccupancyZoneID(edgeHash, out ulong zoneID) || zoneID == 0) continue;
				DesiredEdges.Add(edgeHash);
			}

			// A convoy captured without a usable tape still carries exact last-known vehicle poses.
			// Bind those poses independently so valid adjacent track remains occupied until the derailed entities materialize.
			if (DesiredEdges.Count == 0 && FrozenPoses.Count != 0)
			{
				List<ulong> edgeCandidates = new(32);
				for (int poseIndex = 0; poseIndex < FrozenPoses.Count; poseIndex++)
				{
					FrozenVehiclePose pose = FrozenPoses[poseIndex];
					RailwayVehicleShared.RailCursor cursor = default;
					Vec3d position = new(pose.X, pose.Y, pose.Z);
					if (RailwayVehicleShared.TryBindNearestEdgeWithYaw(graph, position, pose.Dimension, 2, pose.Yaw, ref cursor, edgeCandidates))
					{
						DesiredEdges.Add(cursor.SegmentHash);
					}
				}
			}

			if (railSystem.ReportOwnerEdgeFootprint(OccupancyOwnerID, DesiredEdges))
			{
				PublishedOccupancyEdges.Clear();
				PublishedOccupancyEdges.UnionWith(DesiredEdges);
				PublishedOccupancyGraphVersion = graph.BuildVersion;
			}
			DesiredEdges.Clear();
		}

		public double DistanceSimulated;
		public double AbsoluteDistanceSimulated;
		public double ElapsedSimulationSec;

		public bool TerminalStopped;
		public double TerminalElapsedSec;

		private long LastUpdateMS;
		private long LastRefrigerationUpdateMS;

		private readonly List<RailStationRegistrySystem.RailStationEntry> StationCandidates = new(8);
		private readonly List<StationPathTarget> TargetCandidates = new(8);

		public bool HasAutomation => Automation != null;
		public bool NeedsAutomationPathIndex
		{
			get
			{
				VirtualAutomationState? automationState = Automation;
				RailExactTurnPlan? turnPlan = automationState?.ActiveRoute?.TurnPlan;
				return automationState != null &&
					(automationState.ActiveRoute == null || automationState.RefreshRequested ||
					(turnPlan?.BoundaryBlocked == true && turnPlan.BoundaryBlockReason == SignalAuthorityResult.ChainDownstreamOccupied));
			}
		}
		public long OccupancyOwnerID => HeadKey;
		public double BurnSecondsToApply => Automation != null
			? Math.Min(Automation.BurnConsumedSec, Automation.FuelWaterSecondsAtCapture)
			: Math.Min(ElapsedSimulationSec, PoweredSeconds);

		public double CollisionRearExtentForVehicle(int vehicleIndex)
		{
			if (Vehicles.Count == 0) return 0.5;
			vehicleIndex = GameMath.Clamp(vehicleIndex, 0, Vehicles.Count - 1);
			return Math.Max(0.0, Vehicles[vehicleIndex].OccupancyRearExtent);
		}

		public double CollisionFrontExtentForVehicle(int vehicleIndex)
		{
			if (Vehicles.Count == 0) return 0.5;
			vehicleIndex = GameMath.Clamp(vehicleIndex, 0, Vehicles.Count - 1);
			return Math.Max(0.0, Vehicles[vehicleIndex].OccupancyFrontExtent);
		}

		public bool TryEmitPathTapeCollisionFootprint(RailTrainCollisionSystem collisionCollector, RailGraphLive graph, double speedBPS)
		{
			if (collisionCollector == null || graph == null || !HasPathTape || PathTape == null || Vehicles.Count == 0) return false;
			if (!Orphaned && !PathTape.ValidateAuthoritative(graph, Gauge)) return false;

			int collisionBodyIndex = collisionCollector.GetOrCreateVirtualPathTapeBody(graph, Gauge, PathTape, PathHeadDistance, HeadKey, Math.Abs(speedBPS));
			if (collisionBodyIndex < 0) return false;

			bool emittedAnyVehicle = false;
			int routeRunHint = -1;
			for (int vehicleIndex = Vehicles.Count - 1; vehicleIndex >= 0; vehicleIndex--)
			{
				VehicleSnapshot vehicleSnapshot = Vehicles[vehicleIndex];
				double sourceDistance = PathHeadDistance - Math.Max(0, vehicleSnapshot.DistanceBehindHead);
				double minimumDistance = sourceDistance - Math.Max(0, vehicleSnapshot.OccupancyRearExtent);
				double maximumDistance = sourceDistance + Math.Max(0, vehicleSnapshot.OccupancyFrontExtent);
				bool vehicleEmitted = Orphaned
					? PathTape.EmitExistingRepulsionSpans(collisionCollector, graph, Gauge, collisionBodyIndex, vehicleIndex, minimumDistance, maximumDistance, ref routeRunHint)
					: PathTape.EmitRepulsionSpans(collisionCollector, graph, Gauge, collisionBodyIndex, vehicleIndex, minimumDistance, maximumDistance, ref routeRunHint);
				if (vehicleEmitted) { collisionCollector.IncrementVirtualBodyMemberCount(collisionBodyIndex); emittedAnyVehicle = true; }
			}

			return emittedAnyVehicle;
		}

		public double TailOccupancyDistance
		{
			get
			{
				if (Vehicles.Count == 0) return 0;
				VehicleSnapshot tailVehicle = Vehicles[Vehicles.Count - 1];
				return Math.Max(0, tailVehicle.DistanceBehindHead + tailVehicle.OccupancyRearExtent);
			}
		}

		public bool HasCollisionBoiler
		{
			get
			{
				int leadVehicleIndex = GameMath.Clamp(LeadIndex, 0, Math.Max(0, Vehicles.Count - 1));
				return Vehicles.Count > 0 && Vehicles[leadVehicleIndex].HasTractionEngine;
			}
		}

		public long LastVirtualTickMS => LastUpdateMS;

		public void EnsureVirtualClock(long nowMS)
		{
			if (LastUpdateMS <= 0) LastUpdateMS = nowMS;
			if (LastRefrigerationUpdateMS <= 0) LastRefrigerationUpdateMS = nowMS;
		}

		public bool AdvanceRefrigeration(ICoreServerAPI serverAPI, long nowMS)
		{
			if (RefrigerationVehicles == null) return false;

			if (LastRefrigerationUpdateMS <= 0)
			{
				LastRefrigerationUpdateMS = nowMS;
				return false;
			}

			long elapsedMS = nowMS - LastRefrigerationUpdateMS;
			if (elapsedMS <= 0) return false;

			double elapsedSeconds = elapsedMS / 1000.0;
			bool changed = false;
			for (int vehicleIndex = 0; vehicleIndex < RefrigerationVehicles.Count; vehicleIndex++)
			{
				changed |= RefrigerationVehicles[vehicleIndex].AdvanceRefrigeration(serverAPI, elapsedSeconds);
			}

			LastRefrigerationUpdateMS = nowMS;
			return changed;
		}

		private long GetRefrigerationWakeDelayMS()
		{
			if (RefrigerationVehicles == null) return long.MaxValue;

			long delayMS = long.MaxValue;
			for (int vehicleIndex = 0; vehicleIndex < RefrigerationVehicles.Count; vehicleIndex++)
			{
				delayMS = Math.Min(delayMS, RefrigerationVehicles[vehicleIndex].GetRefrigerationWakeDelayMS());
			}
			return delayMS;
		}

		public void StopNow()
		{
			TerminalStopped = true;
			TerminalElapsedSec = ElapsedSimulationSec;
			MaxElapsedSeconds = ElapsedSimulationSec;
			if (Automation != null)
			{
				Automation.CurrentSpeedBPS = 0;
				Automation.Status = VirtualAutomationStatus.Stuck;
			}
		}

		public long GetNextWakeDelayMS(RailGraphLive graph)
		{
			long refrigerationDelayMS = GetRefrigerationWakeDelayMS();
			if (Orphaned || TerminalStopped) return refrigerationDelayMS;

			long movementDelayMS;
			if (TryGetActiveMotionHorizon(graph, out double absoluteSpeed, out _, out _, out double distance))
			{
				long boundaryDelayMS = distance <= ZoneBoundaryProbeBlocks ? 1 : GetDelayForTravelDistanceMS(distance, absoluteSpeed);
				movementDelayMS = Math.Min(MovingVirtualTickMS, boundaryDelayMS);
				return Math.Min(movementDelayMS, refrigerationDelayMS);
			}

			if (Automation == null)
			{
				movementDelayMS = Math.Abs(SpeedAt(ElapsedSimulationSec)) > MovementEpsilon ? MovingVirtualTickMS : long.MaxValue;
				return Math.Min(movementDelayMS, refrigerationDelayMS);
			}

			switch (Automation.Status)
			{
				case VirtualAutomationStatus.None:
				case VirtualAutomationStatus.Going:
					movementDelayMS = MovingVirtualTickMS;
				break;

				case VirtualAutomationStatus.WaitingForPathBudget:
					movementDelayMS = PathBudgetRetryMS;
				break;

				case VirtualAutomationStatus.WaitingAtStation: {
					int targetIndex = CurrentTargetIndex();
					if (targetIndex < 0 || targetIndex >= Automation.Route.Length) movementDelayMS = long.MaxValue;
					else
					{
						TimetableRouteEntryPacket routeEntry = Automation.Route[targetIndex];
						double arrivalElapsedSec = Automation.ArrivedStationElapsedSec < 0 ? ElapsedSimulationSec : Automation.ArrivedStationElapsedSec;
						double waitedSec = Math.Max(0, ElapsedSimulationSec - arrivalElapsedSec);
						double remainingWaitSec = Math.Max(0, routeEntry.TimeElapsedSeconds - waitedSec);
						movementDelayMS = remainingWaitSec > MovementEpsilon
							? Math.Min(WaitingVirtualTickMS, Math.Max(1, (long)Math.Ceiling(remainingWaitSec * 1000.0)))
							: (FuelCriteriaSatisfied(routeEntry) ? 1 : WaitingVirtualTickMS);
					}
				break; }

				case VirtualAutomationStatus.WaitingForSignal:
					movementDelayMS = WaitingVirtualTickMS;
				break;

				case VirtualAutomationStatus.Stuck:
				case VirtualAutomationStatus.NoTimetable:
				default:
					movementDelayMS = long.MaxValue;
				break;
			}

			return Math.Min(movementDelayMS, refrigerationDelayMS);
		}

		public long GetDelayForTravelDistanceMS(double distance, double absoluteSpeed)
		{
			if (distance <= ZoneBoundaryProbeBlocks) return 1;
			if (absoluteSpeed <= MovementEpsilon) return long.MaxValue;
			if (Automation != null) return Math.Max(1, (long)Math.Ceiling(distance / absoluteSpeed * 1000.0));

			double startDistance = Math.Abs(DistanceAt(ElapsedSimulationSec));
			double maximumDistance = Math.Abs(DistanceAt(MaxElapsedSeconds));
			if (startDistance + distance > maximumDistance + MovementEpsilon) return long.MaxValue;

			double lowerElapsedBound = ElapsedSimulationSec;
			double upperElapsedBound = MaxElapsedSeconds;
			for (int iteration = 0; iteration < 48; iteration++)
			{
				double midpointElapsed = (lowerElapsedBound + upperElapsedBound) * 0.5;
				double distanceTravelled = Math.Abs(DistanceAt(midpointElapsed)) - startDistance;
				if (distanceTravelled >= distance) upperElapsedBound = midpointElapsed;
				else lowerElapsedBound = midpointElapsed;
			}
			return Math.Max(1, (long)Math.Ceiling((upperElapsedBound - ElapsedSimulationSec) * 1000.0));
		}

		public bool TryGetActiveMotionHorizon(RailGraphLive graph, out double absoluteSpeed, out int pathDirection, out double authorityPathDistance, out double distance)
		{
			absoluteSpeed = 0;
			pathDirection = 1;
			authorityPathDistance = PathHeadDistance;
			distance = 0;
			if (Orphaned || TerminalStopped || graph == null || PathTape == null) return false;

			if (Automation == null)
			{
				double signedSpeed = SpeedAt(ElapsedSimulationSec);
				absoluteSpeed = Math.Abs(signedSpeed);
				pathDirection = signedSpeed >= 0 ? 1 : -1;
				if (absoluteSpeed <= MovementEpsilon || ElapsedSimulationSec >= MaxElapsedSeconds - MovementEpsilon) return false;
			}
			else
			{
				if (Automation.Status != VirtualAutomationStatus.None && Automation.Status != VirtualAutomationStatus.Going) return false;
				absoluteSpeed = Math.Abs(Automation.CurrentSpeedBPS);
				pathDirection = 1;
				if (absoluteSpeed <= MovementEpsilon) return false;
			}

			if (pathDirection >= 0)
			{
				double frontOccupancyExtent = Vehicles.Count > 0 ? Math.Max(0, Vehicles[0].OccupancyFrontExtent) : 0;
				authorityPathDistance = PathHeadDistance + frontOccupancyExtent;
			}
			else { authorityPathDistance = PathHeadDistance - TailOccupancyDistance; }

			if (!PathTape.TryGetDistanceToNextZoneBoundary(graph, authorityPathDistance, pathDirection, out distance)) return false;

			if (Automation == null)
			{
				double remainingTravel = Math.Max( 0, Math.Abs(DistanceAt(MaxElapsedSeconds)) - Math.Abs(DistanceAt(ElapsedSimulationSec)));
				distance = Math.Min(distance, remainingTravel);
			}
			return true;
		}

		public VirtualConvoy
		(
			long headKey,
			byte gauge,
			int wantTurnTravel,
			double initialSpeed,
			double rollingDeceleration,
			double poweredSeconds,
			double stopSeconds,
			double startingAbsoluteDistanceTravelled,
			OffscreenRailCursor.Cursor startCursor,
			int leadIndex,
			int standardGaugeLeadEnd,
			long startingMS,
			VirtualAutomationState? automationState,
			ConvoyRoute? pathTape,
			double pathHeadDistance
		)
		{
			HeadKey = headKey;
			Gauge = gauge;
			WantTurnTravel = wantTurnTravel;
			InitialSpeed = initialSpeed;
			RollingDeceleration = Math.Max(MinRollingDeceleration, rollingDeceleration);
			PoweredSeconds = Math.Max(0, poweredSeconds);
			StopSeconds = Math.Max(0, stopSeconds);
			StartingAbsoluteDistanceTravelled = startingAbsoluteDistanceTravelled;
			LeadIndex = Math.Max(0, leadIndex);
			LeadEnd = SGTrainEndUtil.FromInt(standardGaugeLeadEnd);
			Automation = automationState;

			Cursor = startCursor;
			PathTape = pathTape;
			PathHeadDistance = pathHeadDistance;

			DistanceSimulated = 0;
			AbsoluteDistanceSimulated = 0;
			ElapsedSimulationSec = 0;

			TerminalStopped = false;
			TerminalElapsedSec = 0;

			MaxElapsedSeconds = automationState == null ? PoweredSeconds + StopSeconds + 5 : double.MaxValue;
			LastUpdateMS = startingMS;
			LastRefrigerationUpdateMS = startingMS;
		}

		private static OffscreenRailCursor.Cursor ToOffscreenCursor(in RailwayVehicleShared.RailCursor railCursor)
		{
			return new OffscreenRailCursor.Cursor
			{
				SegmentHash = railCursor.SegmentHash,
				SegmentIndex = railCursor.SegmentIndex,
				NormalizedSegmentProgress = railCursor.NormalizedSegmentProgress,
				Direction = railCursor.Direction >= 0 ? 1 : -1
			};
		}

		public bool TrySampleVehicleCursor(RailGraphLive graph, int vehicleIndex, out OffscreenRailCursor.Cursor cursor)
		{
			cursor = default;
			if (Vehicles.Count == 0) return false;
			vehicleIndex = GameMath.Clamp(vehicleIndex, 0, Vehicles.Count - 1);

			if (!HasPathTape || PathTape == null) return false;
			if (!PathTape.ValidateAuthoritative(graph, Gauge)) return false;

			double sourceDistance = PathHeadDistance - Math.Max(0, Vehicles[vehicleIndex].DistanceBehindHead);
			if (!PathTape.TrySampleAuthoritativeCursor(graph, Gauge, sourceDistance, out var railCursor)) return false;
			cursor = ToOffscreenCursor(in railCursor);
			return true;
		}

		internal bool TrySampleAuthorityCursor(RailGraphLive graph, int movementSign, out OffscreenRailCursor.Cursor cursor, out double authorityPathDistance)
		{
			cursor = default;
			authorityPathDistance = PathHeadDistance;

			if (!HasPathTape || PathTape == null || Vehicles.Count == 0) return false;
			if (!PathTape.ValidateAuthoritative(graph, Gauge)) return false;

			authorityPathDistance = movementSign >= 0 ? PathHeadDistance + Math.Max(0, Vehicles[0].OccupancyFrontExtent) : PathHeadDistance - TailOccupancyDistance;

			if (!PathTape.TrySampleAuthoritativeCursorForTravel(graph, Gauge, authorityPathDistance, movementSign, out var railCursor)) return false;
			cursor = ToOffscreenCursor(in railCursor);
			return true;
		}

		private bool SyncHeadCursorFromPathTape(RailGraphLive graph)
		{
			if (!HasPathTape || PathTape == null) return false;
			if (!PathTape.TrySampleAuthoritativeCursor(graph, Gauge, PathHeadDistance, out var railCursor)) return false;
			Cursor = ToOffscreenCursor(in railCursor);
			return true;
		}

		public OffscreenRailCursor.AdvanceResult AdvanceBodyTape
		(
			RailGraphLive graph,
			RailGraphServerSystem? railSystem,
			double signedDistance,
			int wantTurn,
			RailExactTurnPlan? exactTurnPlan,
			bool automatedMovement,
			ref double absoluteDistanceTravelled,
			out double distanceMoved)
		{
			distanceMoved = 0;
			if (Math.Abs(signedDistance) <= MovementEpsilon) return OffscreenRailCursor.AdvanceResult.NoMove;

			if (!HasPathTape || PathTape == null || Vehicles.Count == 0) { StopNow(); return OffscreenRailCursor.AdvanceResult.NoMove; }

			int movementSign = signedDistance >= 0 ? 1 : -1;
			double minimumOccupiedDistance = PathHeadDistance - TailOccupancyDistance;
			double maximumOccupiedDistance = PathHeadDistance + Math.Max(0, Vehicles[0].OccupancyFrontExtent);
			double authorityPathDistance = movementSign > 0 ? maximumOccupiedDistance : minimumOccupiedDistance;

			PathTape.TrimForMovement(minimumOccupiedDistance, maximumOccupiedDistance, movementSign, PathTapeTrailingRetentionBlocks);
			if (!PathTape.TrySampleAuthoritativeCursorForTravel(graph, Gauge, authorityPathDistance, movementSign, out var extensionRailCursor))
			{
				StopNow();
				return OffscreenRailCursor.AdvanceResult.NoMove;
			}

			var extensionCursor = ToOffscreenCursor(in extensionRailCursor);
			double workingAbsoluteDistanceTravelled = absoluteDistanceTravelled;
			TapeRecorder.Begin(PathTape, Gauge, movementSign, authorityPathDistance, workingAbsoluteDistanceTravelled);
			var finalAdvanceResult = OffscreenRailCursor.AdvancePartial
			(
				graph,
				Gauge,
				ref extensionCursor,
				signedDistance,
				wantTurn,
				exactTurnPlan,
				railSystem,
				OccupancyOwnerID,
				automatedMovement,
				ref workingAbsoluteDistanceTravelled,
				out double extensionDistanceMoved,
				TapeRecorder
			);
			TapeRecorder.Clear();

			double absoluteDistanceMoved = Math.Abs(extensionDistanceMoved);
			if (absoluteDistanceMoved > MovementEpsilon)
			{
				absoluteDistanceTravelled = workingAbsoluteDistanceTravelled;
				distanceMoved = extensionDistanceMoved;
				PathHeadDistance += distanceMoved;
				DistanceSimulated += distanceMoved;
				AbsoluteDistanceSimulated += absoluteDistanceMoved;
				SyncHeadCursorFromPathTape(graph);
			}

			return finalAdvanceResult;
		}

		public bool RestoreOccupancyPublication(RailGraphServerSystem? railSystem, RailGraphLive graph)
		{
			if (railSystem == null) return true;

			// Remove any footprint published by the discarded shell before restoring the virtual authority.
			// This makes failure safe, an invalid cached footprint cannot leave the stale material location registered.
			railSystem.ReleaseOccupancyOwner(OccupancyOwnerID);

			// The virtual convoy already owns an exact local footprint cache. Re-report it directly when topology is unchanged instead of traversing the route again.
			if (PublishedOccupancyGraphVersion == graph.BuildVersion)
			{
				if (railSystem.ReportOwnerEdgeFootprint(OccupancyOwnerID, PublishedOccupancyEdges)) return true;
				PublishedOccupancyGraphVersion = -1;
				return false;
			}

			PublishedOccupancyGraphVersion = -1;
			return EnsureOccupancyCurrent(railSystem, graph);
		}

		public bool EnsureOccupancyCurrent(RailGraphServerSystem? railSystem, RailGraphLive graph)
		{
			if (railSystem == null) return true;
			if (!CollectDesiredOccupancyEdges(graph, DesiredEdges)) return false;

			if (PublishedOccupancyGraphVersion == graph.BuildVersion && PublishedOccupancyEdges.SetEquals(DesiredEdges)) return true;

			if (!railSystem.ReportOwnerEdgeFootprint(OccupancyOwnerID, DesiredEdges)) return false;

			PublishedOccupancyEdges.Clear();
			PublishedOccupancyEdges.UnionWith(DesiredEdges);
			PublishedOccupancyGraphVersion = graph.BuildVersion;
			return true;
		}

		public void ReleaseOccupancy(RailGraphServerSystem? railSystem)
		{
			PublishedOccupancyEdges.Clear();
			PublishedOccupancyGraphVersion = -1;
			railSystem?.ReleaseOccupancyOwner(OccupancyOwnerID);
		}

		private bool CollectDesiredOccupancyEdges(RailGraphLive graph, HashSet<ulong> destination)
		{
			destination.Clear();
			if (!HasPathTape || PathTape == null) return false;

			double minimumDistance = PathHeadDistance - TailOccupancyDistance;
			double maximumDistance = PathHeadDistance + (Vehicles.Count > 0 ? Math.Max(0, Vehicles[0].OccupancyFrontExtent) : 0);
			return PathTape.CollectOccupiedEdgeHashes(graph, Gauge, minimumDistance, maximumDistance, destination);
		}

		public void SimulateTo(long nowMS, RailGraphLive graph, RailGraphServerSystem? railSystem, RailStationRegistrySystem? stationRegistry, RailPathIndex? pathIndex, RailAutomationPathingSystem? pathingSystem)
		{
			if (Orphaned) { LastUpdateMS = nowMS; return; }
			if (Automation != null) { SimulateAutomationTo(nowMS, graph, railSystem, stationRegistry, pathIndex, pathingSystem); return; }

			SimulateBallisticTo(nowMS, graph, railSystem);
		}

		private void SimulateBallisticTo(long nowMS, RailGraphLive graph, RailGraphServerSystem? railSystem)
		{
			if (!EnsureOccupancyCurrent(railSystem, graph)) StopNow();

			if (TerminalStopped) { LastUpdateMS = nowMS; return; }
			if (LastUpdateMS == 0) { LastUpdateMS = nowMS; return; }

			long deltaTimeMS = nowMS - LastUpdateMS;
			if (deltaTimeMS <= 0) return;

			double deltaTimeSec = Math.Min(deltaTimeMS / 1000.0, MaxVirtualMovementStepSeconds);
			double previousElapsedSec = ElapsedSimulationSec;
			double desiredElapsedSec = Math.Min(MaxElapsedSeconds, previousElapsedSec + deltaTimeSec);

			if (desiredElapsedSec <= previousElapsedSec + 1e-6) { LastUpdateMS = nowMS; return; }

			double previousDistance = DistanceAt(previousElapsedSec);
			double desiredDistance = DistanceAt(desiredElapsedSec);

			double distanceDelta = desiredDistance - previousDistance;
			if (Math.Abs(distanceDelta) > 1e-6)
			{
				double absoluteDistanceTravelled = StartingAbsoluteDistanceTravelled + AbsoluteDistanceSimulated;
				var advanceResult = AdvanceBodyTape(graph, railSystem, distanceDelta, WantTurnTravel, null, false, ref absoluteDistanceTravelled, out double distanceMoved);

				if (advanceResult != OffscreenRailCursor.AdvanceResult.MovedAll)
				{
					double movementCompletionRatio = Math.Abs(distanceDelta) <= 1e-8 ? 0 : GameMath.Clamp(Math.Abs(distanceMoved) / Math.Abs(distanceDelta), 0.0, 1.0);
					ElapsedSimulationSec = previousElapsedSec + (desiredElapsedSec - previousElapsedSec) * movementCompletionRatio;
					TerminalStopped = true;
					TerminalElapsedSec = ElapsedSimulationSec;
					MaxElapsedSeconds = ElapsedSimulationSec;
					EnsureOccupancyCurrent(railSystem, graph);
					LastUpdateMS = nowMS;
					return;
				}
			}

			ElapsedSimulationSec = desiredElapsedSec;
			if (!EnsureOccupancyCurrent(railSystem, graph)) StopNow();
			LastUpdateMS = nowMS;
		}

		private void SimulateAutomationTo(long nowMS, RailGraphLive graph, RailGraphServerSystem? railSystem, RailStationRegistrySystem? stationRegistry, RailPathIndex? pathIndex, RailAutomationPathingSystem? pathingSystem)
		{
			if (!EnsureOccupancyCurrent(railSystem, graph)) StopNow();
			if (TerminalStopped) { LastUpdateMS = nowMS; return; }
			if (Automation == null) return;
			if (LastUpdateMS == 0) { LastUpdateMS = nowMS; return; }

			if 
			(
				(Automation.Status == VirtualAutomationStatus.WaitingForSignal ||
				Automation.Status == VirtualAutomationStatus.WaitingForPathBudget) && nowMS - LastUpdateMS > MinActiveVirtualStepMS
			)
			{
				// Time spent blocked was stationary time.
				// A wake that notices the blocker is gone must only resume motion from now, not replay the whole waiting interval as hidden travel.
				LastUpdateMS = nowMS - MinActiveVirtualStepMS;
			}

			long deltaTimeMS = nowMS - LastUpdateMS; if (deltaTimeMS <= 0) return;

			double remainingSec = Math.Min(deltaTimeMS / 1000.0, MaxVirtualMovementStepSeconds);
			int sliceIteration = 0;

			while (remainingSec > MovementEpsilon && sliceIteration < AutomationMaxSlicesPerSimulate)
			{
				sliceIteration++;
				if (Automation.Route.Length == 0)
				{
					Automation.Status = VirtualAutomationStatus.NoTimetable;
					Automation.CurrentSpeedBPS = 0;
					break;
				}

				double remainingPoweredSeconds = Automation.FuelWaterSecondsAtCapture - Automation.BurnConsumedSec;
				if (remainingPoweredSeconds <= MovementEpsilon) { StopNow(); break; } // Keeping sim cheap by cutting it early

				if (!TryEnsureAutomationRoute(railSystem, graph, stationRegistry, pathIndex, pathingSystem, nowMS))
				{
					Automation.CurrentSpeedBPS = 0;
					break;
				}

				if (!TryBuildRailCursor(graph, out RailwayVehicleShared.RailCursor railCursor))
				{
					Automation.Status = VirtualAutomationStatus.Stuck;
					Automation.CurrentSpeedBPS = 0;
					break;
				}

				RailAutomationRoute? automationRoute = Automation.ActiveRoute;
				if (automationRoute == null)
				{
					Automation.Status = VirtualAutomationStatus.Stuck;
					Automation.CurrentSpeedBPS = 0;
					break;
				}

				if (!Automation.PinnedTargetInvalidated && automationRoute.IsAtTarget(graph, railCursor))
				{
					if (!SimulateStationWait(remainingSec, out double consumedWaitSec))
					{
						ElapsedSimulationSec += consumedWaitSec;
						remainingSec -= consumedWaitSec;
						break;
					}

					ElapsedSimulationSec += consumedWaitSec;
					remainingSec -= consumedWaitSec;
					AdvanceAutomationStationIndex();
					if (Automation.Route.Length <= 1) break;
					continue;
				}

				double cruiseSpeedBPS = ComputeAutomationCruiseSpeed(graph);
				if (cruiseSpeedBPS < AutomationMinCruiseSpeedBPS)
				{
					Automation.Status = VirtualAutomationStatus.Stuck;
					Automation.CurrentSpeedBPS = 0;
					break;
				}

				double simulationSliceSec = Math.Min(Math.Min(remainingSec, AutomationMaxSliceSeconds), remainingPoweredSeconds);
				double distance = cruiseSpeedBPS * simulationSliceSec;
				double absoluteDistanceTravelled = StartingAbsoluteDistanceTravelled + AbsoluteDistanceSimulated;

				automationRoute.TurnPlan.ResetSignalBoundaryStatus();

				var advanceResult = AdvanceBodyTape
				(
					graph,
					railSystem,
					distance,
					0,
					automationRoute.TurnPlan,
					true,
					ref absoluteDistanceTravelled,
					out double distanceMoved
				);

				double absoluteDistanceMoved = Math.Abs(distanceMoved);
				if (absoluteDistanceMoved <= MovementEpsilon)
				{
					// Signal waits and stale-route boundaries are both recoverable without materializing the consist.
					// Clearance/physical obstructions still use the existing loaded-world recovery policy.
					if (automationRoute.TurnPlan.BoundaryBlocked) { Automation.Status = VirtualAutomationStatus.WaitingForSignal; }
					else if (automationRoute.TurnPlan.PathBlocked)
					{
						Automation.RefreshRequested = true;
						Automation.Status = VirtualAutomationStatus.WaitingForPathBudget;
					}
					else { Automation.Status = VirtualAutomationStatus.Stuck; }
					
					Automation.CurrentSpeedBPS = 0;
					break;
				}

				double usedTravelSec = absoluteDistanceMoved / cruiseSpeedBPS;
				ElapsedSimulationSec += usedTravelSec;
				remainingSec -= usedTravelSec;
				Automation.BurnConsumedSec = Math.Min(Automation.FuelWaterSecondsAtCapture, Automation.BurnConsumedSec + usedTravelSec);
				Automation.CurrentSpeedBPS = cruiseSpeedBPS;
				Automation.Status = VirtualAutomationStatus.Going;

				if (Automation.FuelWaterSecondsAtCapture - Automation.BurnConsumedSec <= MovementEpsilon) { StopNow(); break; }

				if (automationRoute.TurnPlan.BoundaryBlocked)
				{
					Automation.Status = VirtualAutomationStatus.WaitingForSignal;
					Automation.CurrentSpeedBPS = 0;
					break;
				}

				if (automationRoute.TurnPlan.PathBlocked)
				{
					Automation.RefreshRequested = true;
					Automation.Status = VirtualAutomationStatus.WaitingForPathBudget;
					Automation.CurrentSpeedBPS = 0;
					break;
				}

				if (advanceResult != OffscreenRailCursor.AdvanceResult.MovedAll) continue;
			}

			if (!EnsureOccupancyCurrent(railSystem, graph)) StopNow();

			// Any unconsumed interval is discarded.
			// A delayed virtual tick means the offscreen train effectively waited, it is never time debt to replay later.
			LastUpdateMS = nowMS;
		}

		private bool SimulateStationWait(double remainingSec, out double consumedSec)
		{
			consumedSec = 0;
			if (Automation == null || Automation.Route.Length == 0) return false;

			int targetIndex = CurrentTargetIndex();
			TimetableRouteEntryPacket routeEntry = Automation.Route[targetIndex];

			if (Automation.ArrivedStationElapsedSec < 0) Automation.ArrivedStationElapsedSec = ElapsedSimulationSec;

			Automation.Status = VirtualAutomationStatus.WaitingAtStation;
			Automation.CurrentSpeedBPS = 0;

			double waitedSec = Math.Max(0, ElapsedSimulationSec - Automation.ArrivedStationElapsedSec);
			double requiredWaitSec = Math.Max(0, routeEntry.TimeElapsedSeconds);
			if (waitedSec < requiredWaitSec)
			{
				consumedSec = Math.Min(remainingSec, requiredWaitSec - waitedSec);
				return waitedSec + consumedSec >= requiredWaitSec - MovementEpsilon && FuelCriteriaSatisfied(routeEntry);
			}

			return FuelCriteriaSatisfied(routeEntry);
		}

		private bool FuelCriteriaSatisfied(TimetableRouteEntryPacket routeEntry)
		{
			if (Automation == null) return false;

			int minutes = Math.Max(0, routeEntry.MileageLeftMinutes);
			if (minutes <= 0) return true;

			double requiredSeconds = minutes * AutomationFuelSecondsPerMinute;
			double remainingSeconds = Math.Max(0, Automation.FuelWaterSecondsAtCapture - Automation.BurnConsumedSec);
			return remainingSeconds >= requiredSeconds;
		}

		private void AdvanceAutomationStationIndex()
		{
			if (Automation == null || Automation.Route.Length == 0) return;

			int targetIndex = CurrentTargetIndex();
			Automation.CurrentStationIndex = (targetIndex + 1) % Automation.Route.Length;
			Automation.ResetRoute();
		}

		private int CurrentTargetIndex()
		{
			if (Automation == null || Automation.Route.Length == 0) return -1;
			return Automation.CurrentStationIndex >= 0 && Automation.CurrentStationIndex < Automation.Route.Length ? Automation.CurrentStationIndex : 0;
		}

		private bool TryEnsureAutomationRoute
		(
			RailGraphServerSystem? railSystem,
			RailGraphLive graph,
			RailStationRegistrySystem? stationRegistry,
			RailPathIndex? pathIndex,
			RailAutomationPathingSystem? pathingSystem,
			long nowMS
		)
		{
			if (Automation == null) return false;

			int targetIndex = CurrentTargetIndex();
			if (targetIndex < 0) { Automation.Status = VirtualAutomationStatus.NoTimetable; return false; }

			RailAutomationRoute? activeRoute = Automation.ActiveRoute;
			if (activeRoute?.TurnPlan.PathBlocked == true) { Automation.RefreshRequested = true; }

			bool rerouteBlockedChain = railSystem != null
				&& activeRoute != null && activeRoute.TurnPlan.BoundaryBlocked
				&& activeRoute.TurnPlan.BoundaryBlockReason == SignalAuthorityResult.ChainDownstreamOccupied;

			bool routeRefreshNeeded = activeRoute == null || Automation.RefreshRequested;
			if (!routeRefreshNeeded && !rerouteBlockedChain) { Automation.Status = VirtualAutomationStatus.Going; return true; }

			// A chain reroute is revision-driven. If nothing downstream changed since the last rejected search,
			// remain on the existing route without touching station resolution or consuming another search budget slot.
			// A real route refresh still proceeds even while the signal revision is unchanged.
			if (rerouteBlockedChain && !routeRefreshNeeded && railSystem != null && activeRoute != null)
			{
				RailExactTurnPlan blockedPlan = activeRoute.TurnPlan;
				ulong signalAuthorityRevision = railSystem.GetSignalAuthorityRevision(blockedPlan.BlockedSignalEndpoint);
				if 
				(
					Automation.LastChainRepathOccupancySerial == signalAuthorityRevision &&
					Automation.LastChainRepathSignalEndpoint.Equals(blockedPlan.BlockedSignalEndpoint) &&
					Automation.LastChainRepathFromEdgeHash == blockedPlan.BlockedFromEdgeHash
				) { Automation.Status = VirtualAutomationStatus.WaitingForSignal; return true; }
			}

			// Path-index construction is derived state.
			// An existing route keeps executing while its replacement is built, only a convoy with no route must wait.
			if (pathIndex == null || stationRegistry == null || pathingSystem == null)
			{
				if (activeRoute == null) { Automation.Status = VirtualAutomationStatus.WaitingForPathBudget; return false; }

				Automation.Status = rerouteBlockedChain
					? VirtualAutomationStatus.WaitingForSignal : activeRoute.TurnPlan.PathBlocked
					? VirtualAutomationStatus.WaitingForPathBudget : VirtualAutomationStatus.Going;
				return true;
			}

			TimetableRouteEntryPacket routeEntry = Automation.Route[targetIndex];
			if 
			(
				!TrySampleAuthorityCursor(graph, 1, out var dimensionAuthorityCursor, out _) ||
				!TryGetCursorDimension(graph, dimensionAuthorityCursor.SegmentHash, out int currentDimension)
			) { Automation.Status = VirtualAutomationStatus.Stuck; return activeRoute != null; }

			TargetCandidates.Clear();
			bool isStationGroup = Automation.TargetIsStationGroup;
			bool preservePinnedTarget = activeRoute != null && Automation.TargetRouteIndex == targetIndex;
			bool havePinnedTarget = false;

			if
			(
				preservePinnedTarget && stationRegistry.TryGet(Automation.Target.StationKey, out RailStationRegistrySystem.RailStationEntry pinnedStation)
				&& pinnedStation.HasStopEndpoint && pinnedStation.Gauge == Gauge && pinnedStation.Key.Dimension == currentDimension
				&& pathIndex.TryResolveStationTarget(graph, pinnedStation, out StationPathTarget pinnedTarget)
			)
			{
				TargetCandidates.Add(pinnedTarget);
				havePinnedTarget = true;
			}

			// Preserve the current physical destination during ordinary refreshes,
			// but expand the candidate set while blocked at a chain signal so another free platform in the same station group can be selected.
			if (!havePinnedTarget || rerouteBlockedChain)
			{
				StationCandidates.Clear();
				if (TryCollectStationCandidates(stationRegistry, routeEntry, Gauge, currentDimension, StationCandidates, out bool registryStationGroup))
				{
					isStationGroup |= registryStationGroup;
					pathIndex.CollectStationTargets(graph, StationCandidates, TargetCandidates);
					if (TargetCandidates.Count > 1) isStationGroup = true;
				}
				else if (!havePinnedTarget)
				{
					Automation.Status = activeRoute?.TurnPlan.PathBlocked == true
						? VirtualAutomationStatus.WaitingForPathBudget
						: activeRoute != null ? VirtualAutomationStatus.Going : VirtualAutomationStatus.Stuck;
					return activeRoute != null;
				}
			}

			if (TargetCandidates.Count == 0)
			{
				Automation.Status = activeRoute?.TurnPlan.PathBlocked == true
					? VirtualAutomationStatus.WaitingForPathBudget
					: activeRoute != null ? VirtualAutomationStatus.Going : VirtualAutomationStatus.Stuck;
				return activeRoute != null;
			}

			if (!TryBuildRailCursor(graph, out RailwayVehicleShared.RailCursor railCursor))
			{
				Automation.Status = VirtualAutomationStatus.Stuck;
				return activeRoute != null;
			}

			ulong currentZoneID = 0;
			if (railCursor.SegmentHash != 0) graph.TryGetOccupancyZoneID(railCursor.SegmentHash, out currentZoneID);

			RailRouteAcceptance? routeAcceptance = null;
			int maxRejectedCandidates = 0;
			ulong chainAuthoritySerial = 0;
			RailGraphLive.EndpointKey chainEndpoint = default;
			ulong chainSourceEdgeHash = 0;

			if (rerouteBlockedChain && activeRoute != null && railSystem != null)
			{
				RailGraphServerSystem signalRailSystem = railSystem;
				RailExactTurnPlan blockedPlan = activeRoute.TurnPlan;
				chainEndpoint = blockedPlan.BlockedSignalEndpoint;
				chainSourceEdgeHash = blockedPlan.BlockedFromEdgeHash;
				chainAuthoritySerial = signalRailSystem.GetSignalAuthorityRevision(chainEndpoint);

				routeAcceptance = candidate =>
				{
					if (!candidate.TurnPlan.TryGet(chainSourceEdgeHash, chainEndpoint, out ulong candidateToEdge)) return false;

					SignalAuthorityResult signalAuthorityResult = signalRailSystem.CheckSignalSectionAuthority
					(
						HeadKey,
						chainEndpoint,
						chainSourceEdgeHash,
						candidateToEdge,
						candidate.TurnPlan
					);

					return signalAuthorityResult == SignalAuthorityResult.Allowed;
				};

				maxRejectedCandidates = MaxRejectedChainRouteCandidates;
			}

			if (!pathingSystem.TrySpendPathSearchBudget(nowMS))
			{
				Automation.Status = rerouteBlockedChain
					? VirtualAutomationStatus.WaitingForSignal : activeRoute?.TurnPlan.PathBlocked == true
					? VirtualAutomationStatus.WaitingForPathBudget : activeRoute != null ? VirtualAutomationStatus.Going : VirtualAutomationStatus.WaitingForPathBudget;
				return activeRoute != null;
			}

			if (rerouteBlockedChain)
			{
				Automation.LastChainRepathOccupancySerial = chainAuthoritySerial;
				Automation.LastChainRepathSignalEndpoint = chainEndpoint;
				Automation.LastChainRepathFromEdgeHash = chainSourceEdgeHash;
			}

			if (!pathIndex.TryFindRouteGreedy
			(
				graph,
				railCursor,
				TargetCandidates,
				currentZoneID,
				out RailAutomationRoute replacement,
				routeAcceptance,
				maxRejectedCandidates)
			)
			{
				if (rerouteBlockedChain) { Automation.Status = VirtualAutomationStatus.WaitingForSignal; return activeRoute != null; }

				Automation.Status = activeRoute?.TurnPlan.PathBlocked == true
					? VirtualAutomationStatus.WaitingForPathBudget
					: activeRoute != null ? VirtualAutomationStatus.Going : VirtualAutomationStatus.Stuck;
				return activeRoute != null;
			}

			Automation.SetRoute(replacement, targetIndex, isStationGroup);
			Automation.Status = VirtualAutomationStatus.Going;
			return true;
		}

		private bool TryBuildRailCursor(RailGraphLive graph, out RailwayVehicleShared.RailCursor railCursor)
		{
			railCursor = default;
			if (!TrySampleAuthorityCursor(graph, 1, out var sourceCursor, out _)) return false;

			railCursor = new RailwayVehicleShared.RailCursor
			{
				Gauge = Gauge,
				SegmentHash = sourceCursor.SegmentHash,
				SegmentIndex = sourceCursor.SegmentIndex,
				NormalizedSegmentProgress = sourceCursor.NormalizedSegmentProgress,
				Direction = sourceCursor.Direction >= 0 ? 1 : -1
			};

			return RailwayVehicleShared.TryRefreshPolyline(graph, ref railCursor);
		}

		private double ComputeAutomationCruiseSpeed(RailGraphLive graph)
		{
			if (Automation == null) return 0;

			double speedBPS = Automation.DriveMaxSpeedBPS;
			if (speedBPS <= MovementEpsilon) return 0;

			if (!TrySampleAuthorityCursor(graph, 1, out var authorityCursor, out _)) return 0;
			ulong speedEdgeHash = authorityCursor.SegmentHash;

			if (graph.TryGetEdgeMaterialSpeedCapBPS(speedEdgeHash, out ushort speedCapBPS) && speedCapBPS > 0) speedBPS = Math.Min(speedBPS, speedCapBPS);
			if (graph.TryGetNormalizedEdgeMaxSpeedFactor(speedEdgeHash, out float speedFactor)) speedBPS *= GameMath.Clamp(speedFactor, 0f, 1f);

			return Math.Max(0, speedBPS * AutomationCruiseEfficiency);
		}

		public double SpeedAt(double elapsedSec)
		{
			if (Automation != null) return TerminalStopped ? 0 : Automation.CurrentSpeedBPS;
			if (TerminalStopped) return 0;
			if (elapsedSec < 0) return InitialSpeed;

			double movementSign = InitialSpeed >= 0 ? 1 : -1;
			double initialAbsoluteSpeed = Math.Abs(InitialSpeed);

			if (elapsedSec <= PoweredSeconds) return movementSign * initialAbsoluteSpeed;

			double coastingElapsedSec = elapsedSec - PoweredSeconds;
			if (coastingElapsedSec >= StopSeconds) return 0;

			double absoluteSpeed = Math.Max(0, initialAbsoluteSpeed - RollingDeceleration * coastingElapsedSec);
			return movementSign * absoluteSpeed;
		}

		public double DistanceAt(double elapsedSec)
		{
			if (elapsedSec <= 0) return 0;

			double movementSign = InitialSpeed >= 0 ? 1 : -1;
			double initialAbsoluteSpeed = Math.Abs(InitialSpeed);

			double poweredElapsedSec = Math.Min(elapsedSec, PoweredSeconds);
			double distance = initialAbsoluteSpeed * poweredElapsedSec;

			double rollingElapsedSec = Math.Max(0, elapsedSec - PoweredSeconds);
			rollingElapsedSec = Math.Min(rollingElapsedSec, StopSeconds);

			distance += initialAbsoluteSpeed * rollingElapsedSec - 0.5 * RollingDeceleration * rollingElapsedSec * rollingElapsedSec;

			return movementSign * distance;
		}

		public void Serialize(BinaryWriter binaryWriter)
		{
			binaryWriter.Write(HeadKey);
			binaryWriter.Write(Gauge);

			binaryWriter.Write(WantTurnTravel);
			binaryWriter.Write(InitialSpeed);
			binaryWriter.Write(RollingDeceleration);
			binaryWriter.Write(PoweredSeconds);
			binaryWriter.Write(StopSeconds);

			binaryWriter.Write(StartingAbsoluteDistanceTravelled);
			binaryWriter.Write(LeadIndex);
			binaryWriter.Write((int)LeadEnd);

			binaryWriter.Write(MaxElapsedSeconds);

			binaryWriter.Write(Cursor.SegmentHash);
			binaryWriter.Write(Cursor.SegmentIndex);
			binaryWriter.Write(Cursor.NormalizedSegmentProgress);
			binaryWriter.Write(Cursor.Direction);

			binaryWriter.Write(PathTape != null);
			if (PathTape != null)
			{
				binaryWriter.Write(PathHeadDistance);
				PathTape.Serialize(binaryWriter);
			}

			binaryWriter.Write(DistanceSimulated);
			binaryWriter.Write(AbsoluteDistanceSimulated);
			binaryWriter.Write(ElapsedSimulationSec);

			binaryWriter.Write(TerminalStopped);
			binaryWriter.Write(TerminalElapsedSec);

			binaryWriter.Write(Automation != null);
			Automation?.Serialize(binaryWriter);

			binaryWriter.Write(Vehicles.Count);
			for (int vehicleIndex = 0; vehicleIndex < Vehicles.Count; vehicleIndex++) { Vehicles[vehicleIndex].Serialize(binaryWriter); }

			binaryWriter.Write(Orphaned);
			binaryWriter.Write(FrozenPoses.Count);
			for (int poseIndex = 0; poseIndex < FrozenPoses.Count; poseIndex++) FrozenPoses[poseIndex].Serialize(binaryWriter);
		}

		public static VirtualConvoy Deserialize(BinaryReader binaryReader)
		{
			long headKey = binaryReader.ReadInt64();
			byte gauge = binaryReader.ReadByte();

			int wantTurn = binaryReader.ReadInt32();
			double initialSpeed = binaryReader.ReadDouble();
			double rollingDeceleration = binaryReader.ReadDouble();
			double poweredSeconds = binaryReader.ReadDouble();
			double stopSeconds = binaryReader.ReadDouble();

			double startingAbsoluteDistanceTravelled = binaryReader.ReadDouble();
			int leadIndex = binaryReader.ReadInt32();
			int standardGaugeLeadEnd = binaryReader.ReadInt32();

			double maxElapsedSeconds = binaryReader.ReadDouble();

			var cursor = new OffscreenRailCursor.Cursor
			{
				SegmentHash = binaryReader.ReadUInt64(),
				SegmentIndex = binaryReader.ReadInt32(),
				NormalizedSegmentProgress = binaryReader.ReadDouble(),
				Direction = binaryReader.ReadInt32()
			};
			cursor.Direction = cursor.Direction >= 0 ? 1 : -1;

			ConvoyRoute? pathTape = null;
			double pathHeadDistance = 0;
			if (binaryReader.ReadBoolean())
			{
				pathHeadDistance = binaryReader.ReadDouble();
				pathTape = ConvoyRoute.Deserialize(binaryReader);
			}

			double distanceSimulated = binaryReader.ReadDouble();
			double absoluteDistanceSimulated = binaryReader.ReadDouble();
			double elapsedSimulationSec = binaryReader.ReadDouble();

			bool terminalStopped = binaryReader.ReadBoolean();
			double terminalElapsed = binaryReader.ReadDouble();

			VirtualAutomationState? automation = binaryReader.ReadBoolean() ? VirtualAutomationState.Deserialize(binaryReader) : null;

			var virtualConvoy = new VirtualConvoy
			(
				headKey, gauge, wantTurn, initialSpeed, rollingDeceleration, poweredSeconds, stopSeconds,
				startingAbsoluteDistanceTravelled, cursor, leadIndex, standardGaugeLeadEnd, startingMS: 0,
				automationState: automation, pathTape: pathTape, pathHeadDistance: pathHeadDistance
			)
			{
				MaxElapsedSeconds = maxElapsedSeconds,
				Cursor = cursor,
				DistanceSimulated = distanceSimulated,
				AbsoluteDistanceSimulated = absoluteDistanceSimulated,
				ElapsedSimulationSec = elapsedSimulationSec,
				TerminalStopped = terminalStopped,
				TerminalElapsedSec = terminalElapsed,
				LastUpdateMS = 0,
				LastRefrigerationUpdateMS = 0
			};

			int vehicleCount = binaryReader.ReadInt32();
			if (vehicleCount < 0 || vehicleCount > 4096) throw new InvalidDataException($"Invalid virtual vehicle count {vehicleCount}.");
			for (int vehicleIndex = 0; vehicleIndex < vehicleCount; vehicleIndex++)
			{
				virtualConvoy.AddVehicleSnapshot(VehicleSnapshot.Deserialize(binaryReader));
			}

			virtualConvoy.Orphaned = binaryReader.ReadBoolean();
			int poseCount = binaryReader.ReadInt32();
			if (poseCount < 0 || poseCount > 4096) throw new InvalidDataException($"Invalid frozen pose count {poseCount}.");
			for (int poseIndex = 0; poseIndex < poseCount; poseIndex++) virtualConvoy.FrozenPoses.Add(FrozenVehiclePose.Deserialize(binaryReader));
			if (virtualConvoy.Orphaned && virtualConvoy.FrozenPoses.Count != virtualConvoy.Vehicles.Count) throw new InvalidDataException("Orphaned virtual convoy has an incomplete frozen pose set.");

			return virtualConvoy;
		}
	}
}
