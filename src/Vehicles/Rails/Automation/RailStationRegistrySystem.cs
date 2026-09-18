using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace YangTransport;

/// Server-owned station index used by automation, timetable UIs, and station-name generation.
/// Stations are registered by BlockEntityRailStation and persisted separately so unloaded stations remain visible to automation.
internal sealed class RailStationRegistrySystem : ModSystem // There's a bit of outdated code here, but nothing harmful, so not worth cleaning up.
{
	internal const string ChannelName = "yangtransport_stations";
	private const string SaveKey = "yangtransport_stations";

	private ICoreServerAPI? ServerAPI;

	private readonly Dictionary<StationBlockKey, RailStationEntry> StationsByBlock = new();
	private readonly Dictionary<ulong, List<StationBlockKey>> StationKeysByNameHash = new();
	private readonly Dictionary<RailGraphLive.EndpointKey, List<RailStationEntry>> StationsByStopEndpoint = new();
	// Published arrays and their DTOs are shared immutable snapshots. Borrowers must not mutate them.
	private readonly Dictionary<int, RailStationListEntry[]> StationListCache = new();

	private bool IsLoaded;
	private bool IsDirty;

	internal int Version { get; private set; } = 1;

	internal enum RegistryChangeKind : byte
	{
		Reset,
		Added,
		Removed,
		Updated
	}

	internal readonly record struct RegistryChange
	(
		RegistryChangeKind Kind,
		RailStationEntry? OldEntry,
		RailStationEntry? NewEntry
	);

	// Server-local, detailed registry mutation event. Consumers can invalidate only routes that actually depend on the changed physical station.
	internal event Action<RegistryChange>? Changed;

	public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

	public override void Start(ICoreAPI coreAPI)
	{
		ServerAPI = coreAPI as ICoreServerAPI;
		if (ServerAPI == null) return;
		ServerAPI.Event.SaveGameLoaded += OnSaveGameLoaded;
		ServerAPI.Event.GameWorldSave += Save;
	}

	public override void Dispose()
	{
		if (ServerAPI != null)
		{
			ServerAPI.Event.SaveGameLoaded -= OnSaveGameLoaded;
			ServerAPI.Event.GameWorldSave -= Save;
			Save();
		}

		base.Dispose();
	}

	private void OnSaveGameLoaded()
	{
		IsLoaded = true;
		StationsByBlock.Clear();
		StationKeysByNameHash.Clear();
		StationsByStopEndpoint.Clear();
		StationListCache.Clear();

		byte[]? data = ServerAPI?.WorldManager.SaveGame.GetData(SaveKey);
		if (data == null || data.Length == 0)
		{
			Version++;
			Changed?.Invoke(new RegistryChange(RegistryChangeKind.Reset, null, null));
			return;
		}

		try
		{
			using MemoryStream memoryStream = new(data);
			using BinaryReader binaryReader = new(memoryStream);

			int count = binaryReader.ReadInt32();
			if (count < 0 || count > 1_000_000) throw new InvalidDataException("Invalid station count.");

			bool changedDuringLoad = false;

			for (int stationIndex = 0; stationIndex < count; stationIndex++)
			{
				var stationBlockKey = new StationBlockKey(binaryReader.ReadInt32(), binaryReader.ReadInt32(), binaryReader.ReadInt32(), binaryReader.ReadInt32());

				string name = binaryReader.ReadString();
				byte gauge = binaryReader.ReadByte();
				bool hasStop = binaryReader.ReadBoolean();
				int stopX16 = binaryReader.ReadInt32();
				int stopY16 = binaryReader.ReadInt32();
				int stopZ16 = binaryReader.ReadInt32();
				string rotationCode = binaryReader.ReadString();

				if (string.IsNullOrWhiteSpace(name)) changedDuringLoad = true;
				string displayName = CleanOrGenerateName(name);

				var entry = new RailStationEntry
				{
					Key = stationBlockKey,
					DisplayName = displayName,
					NameHash = HashNormalizedName(NormalizeStationName(displayName)),
					Gauge = gauge,
					HasStopEndpoint = hasStop,
					StopX16 = stopX16,
					StopY16 = stopY16,
					StopZ16 = stopZ16,
					RotationCode = rotationCode
				};

				if (StationsByBlock.TryGetValue(stationBlockKey, out RailStationEntry duplicate))
				{
					Unindex(duplicate);
					changedDuringLoad = true;
				}
				StationsByBlock[stationBlockKey] = entry;
				Index(entry);
			}

			IsDirty = changedDuringLoad;
		}
		catch (Exception exception)
		{
			ServerAPI?.Logger.Error("[YangTransport] Failed to load station registry.");
			ServerAPI?.Logger.Error(exception);
			StationsByBlock.Clear();
			StationKeysByNameHash.Clear();
			StationsByStopEndpoint.Clear();
			StationListCache.Clear();
			IsDirty = true;
		}

		// Subscribers must observe one complete registry state, never the cleared intermediate state that exists while the save payload is being decoded.
		Version++;
		Changed?.Invoke(new RegistryChange(RegistryChangeKind.Reset, null, null));
	}

	private void Save()
	{
		if (ServerAPI == null || !IsLoaded || !IsDirty) return;

		try
		{
			using MemoryStream memoryStream = new();
			using BinaryWriter binaryWriter = new(memoryStream);

			binaryWriter.Write(StationsByBlock.Count);

			foreach (RailStationEntry stationEntry in StationsByBlock.Values)
			{
				binaryWriter.Write(stationEntry.Key.X);
				binaryWriter.Write(stationEntry.Key.Y);
				binaryWriter.Write(stationEntry.Key.Z);
				binaryWriter.Write(stationEntry.Key.Dimension);

				binaryWriter.Write(stationEntry.DisplayName ?? "");
				binaryWriter.Write(stationEntry.Gauge);
				binaryWriter.Write(stationEntry.HasStopEndpoint);
				binaryWriter.Write(stationEntry.StopX16);
				binaryWriter.Write(stationEntry.StopY16);
				binaryWriter.Write(stationEntry.StopZ16);
				binaryWriter.Write(stationEntry.RotationCode ?? "");
			}

			ServerAPI.WorldManager.SaveGame.StoreData(SaveKey, memoryStream.ToArray());
			IsDirty = false;
		}
		catch (Exception exception)
		{
			ServerAPI.Logger.Error("[YangTransport] Failed to save station registry.");
			ServerAPI.Logger.Error(exception);
		}
	}

	internal string RegisterOrUpdate(BlockPos position, Block? block, string displayName)
	{
		if (ServerAPI == null || position == null) return CleanOrGenerateName(displayName);

		var stationBlockKey = new StationBlockKey(position.X, position.Y, position.Z, position.dimension);
		string name = CleanOrGenerateName(displayName);
		ulong nameHash = HashNormalizedName(NormalizeStationName(name));

		var entry = new RailStationEntry
		{
			Key = stationBlockKey,
			DisplayName = name,
			NameHash = nameHash,
			RotationCode = GetBlockRotation(block)
		};

		if (TryResolveStationStop(position, block, out byte gauge, out RailGraphLive.EndpointKey stop))
		{
			entry.Gauge = gauge;
			entry.HasStopEndpoint = true;
			entry.StopX16 = stop.X16;
			entry.StopY16 = stop.Y16;
			entry.StopZ16 = stop.Z16;
		}

		RailStationEntry? previous = null;
		if (StationsByBlock.TryGetValue(stationBlockKey, out RailStationEntry existingEntry))
		{
			if (existingEntry.SameAs(entry)) return name;
			previous = existingEntry;
			Unindex(existingEntry);
		}

		StationsByBlock[stationBlockKey] = entry;
		Index(entry);
		IsDirty = true;
		StationListCache.Clear();
		Version++;
		Changed?.Invoke(new RegistryChange(previous == null ? RegistryChangeKind.Added : RegistryChangeKind.Updated, previous, entry));
		return name;
	}

	internal void Unregister(BlockPos position)
	{
		if (position == null) return;

		var stationBlockKey = new StationBlockKey(position.X, position.Y, position.Z, position.dimension);
		if (!StationsByBlock.TryGetValue(stationBlockKey, out RailStationEntry existingEntry)) return;

		Unindex(existingEntry);
		StationsByBlock.Remove(stationBlockKey);
		IsDirty = true;
		StationListCache.Clear();
		Version++;
		Changed?.Invoke(new RegistryChange(RegistryChangeKind.Removed, existingEntry, null));
	}

	internal string GenerateUniqueStationName()
	{
		RailStationNameSystem? stationNameSystem = ServerAPI?.ModLoader.GetModSystem<RailStationNameSystem>();

		for (int attemptIndex = 0; attemptIndex < 64; attemptIndex++)
		{
			string name = stationNameSystem?.GenerateName() ?? "";
			if (!string.IsNullOrWhiteSpace(name) && !ContainsName(name)) return name.Trim();
		}

		if (!ContainsName("Station")) return "Station";

		for (int stationNumber = 2; stationNumber < 10_000; stationNumber++)
		{
			string name = "Station " + stationNumber.ToString();
			if (!ContainsName(name)) return name;
		}

		return "Station " + (ServerAPI?.World.Rand.Next(10_000, 99_999).ToString() ?? "99999");
	}

	internal bool ContainsName(string displayName)
	{
		string normalized = NormalizeStationName(displayName);
		return normalized.Length > 0 && StationKeysByNameHash.ContainsKey(HashNormalizedName(normalized));
	}

	internal bool TryGetStationsByNameHash(ulong nameHash, out List<StationBlockKey> stations)
	{
		if (StationKeysByNameHash.TryGetValue(nameHash, out List<StationBlockKey>? list)) { stations = list; return true; }
		stations = null!; return false;
	}

	internal int CollectStationsByNameHash(ulong nameHash, List<RailStationEntry> destination, byte? gauge = null, int? dimension = null)
	{
		if (destination == null || nameHash == 0) return 0;
		if (!StationKeysByNameHash.TryGetValue(nameHash, out List<StationBlockKey>? keys)) return 0;

		int start = destination.Count;
		for (int stationKeyIndex = 0; stationKeyIndex < keys.Count; stationKeyIndex++)
		{
			if (!StationsByBlock.TryGetValue(keys[stationKeyIndex], out RailStationEntry? entry)) continue;
			if (!entry.HasStopEndpoint) continue;
			if (gauge.HasValue && entry.Gauge != gauge.Value) continue;
			if (dimension.HasValue && entry.Key.Dimension != dimension.Value) continue;

			destination.Add(entry);
		}

		return destination.Count - start;
	}

	internal bool TryGet(StationBlockKey key, out RailStationEntry entry)
	{
		if (StationsByBlock.TryGetValue(key, out RailStationEntry? found)) { entry = found; return true; }

		entry = null!; return false;
	}

	/// Borrows a cached station-list snapshot for immediate read-only use.
	/// The returned array and its mutable DTO instances are shared cache state and must not be sorted, edited, or retained for mutation by the caller.
	internal RailStationListEntry[] BorrowStationListEntries(byte? gauge = null)
	{
		int cacheKey = gauge.HasValue ? gauge.Value : -1;
		if (StationListCache.TryGetValue(cacheKey, out RailStationListEntry[]? cached)) return cached;

		List<RailStationListEntry> stations = new(StationsByBlock.Count);
		Dictionary<StationListGroupKey, int> indexByGroup = new();

		foreach (RailStationEntry stationEntry in StationsByBlock.Values)
		{
			if (gauge.HasValue && stationEntry.Gauge != gauge.Value) { continue; }

			StationListGroupKey groupKey = new(stationEntry.NameHash, stationEntry.Key.Dimension, stationEntry.Gauge);
			if (indexByGroup.TryGetValue(groupKey, out int existingIndex))
			{
				stations[existingIndex].PlatformCount++;
				continue;
			}

			indexByGroup[groupKey] = stations.Count;
			stations.Add(new RailStationListEntry
			{
				Name = stationEntry.DisplayName,
				NameHash = stationEntry.NameHash,
				X = stationEntry.Key.X, Y = stationEntry.Key.Y, Z = stationEntry.Key.Z,
				Dimension = stationEntry.Key.Dimension,
				Gauge = stationEntry.Gauge,
				HasStopEndpoint = stationEntry.HasStopEndpoint,
				StopX16 = stationEntry.StopX16,
				StopY16 = stationEntry.StopY16,
				StopZ16 = stationEntry.StopZ16,
				PlatformCount = 1
			});
		}

		RailStationListEntry[] result = stations.ToArray();
		Array.Sort(result, CompareListEntries);
		StationListCache[cacheKey] = result;
		return result;
	}

	internal void CollectEntries(List<RailStationEntry> destination)
	{
		if (destination == null) return;
		foreach (RailStationEntry stationEntry in StationsByBlock.Values) destination.Add(stationEntry);
	}

	internal void CollectEntriesAtEndpoint(RailGraphLive.EndpointKey endpoint, List<RailStationEntry> destination)
	{
		if (destination == null || !StationsByStopEndpoint.TryGetValue(endpoint, out List<RailStationEntry>? entries)) { return; }
		for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++) destination.Add(entries[entryIndex]);
	}

	internal void CollectEntriesAtEndpoints(
		IEnumerable<RailGraphLive.EndpointKey> endpoints,
		List<RailStationEntry> destination)
	{
		if (endpoints == null || destination == null) return;
		foreach (RailGraphLive.EndpointKey endpoint in endpoints)
		{
			if (!StationsByStopEndpoint.TryGetValue(endpoint, out List<RailStationEntry>? entries)) continue;
			for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++) destination.Add(entries[entryIndex]);
		}
	}

	private static int CompareListEntries(RailStationListEntry firstEntry, RailStationListEntry secondEntry)
	{
		int comparisonResult = string.Compare(firstEntry.Name, secondEntry.Name, StringComparison.OrdinalIgnoreCase);
		if (comparisonResult != 0) return comparisonResult;
		comparisonResult = firstEntry.Dimension.CompareTo(secondEntry.Dimension); if (comparisonResult != 0) return comparisonResult;
		comparisonResult = firstEntry.X.CompareTo(secondEntry.X); if (comparisonResult != 0) return comparisonResult;
		comparisonResult = firstEntry.Y.CompareTo(secondEntry.Y); if (comparisonResult != 0) return comparisonResult;
		return firstEntry.Z.CompareTo(secondEntry.Z);
	}

	private string CleanOrGenerateName(string name)
	{
		name = (name ?? "").Trim();
		if (name.Length > 512) name = name[..512];
		return name.Length == 0 ? GenerateUniqueStationName() : name;
	}

	private void Index(RailStationEntry entry)
	{
		if (!StationKeysByNameHash.TryGetValue(entry.NameHash, out List<StationBlockKey>? list))
		{
			list = new List<StationBlockKey>(2);
			StationKeysByNameHash[entry.NameHash] = list;
		}

		list.Add(entry.Key);

		if (entry.HasStopEndpoint)
		{
			RailGraphLive.EndpointKey endpoint = new(entry.StopX16, entry.StopY16, entry.StopZ16, entry.Key.Dimension, entry.Gauge);
			if (!StationsByStopEndpoint.TryGetValue(endpoint, out List<RailStationEntry>? endpointEntries))
			{
				endpointEntries = new List<RailStationEntry>(1);
				StationsByStopEndpoint[endpoint] = endpointEntries;
			}
			endpointEntries.Add(entry);
		}
	}

	private void Unindex(RailStationEntry entry)
	{
		if (StationKeysByNameHash.TryGetValue(entry.NameHash, out List<StationBlockKey>? list))
		{
			list.Remove(entry.Key);
			if (list.Count == 0) StationKeysByNameHash.Remove(entry.NameHash);
		}

		if (!entry.HasStopEndpoint) return;
		RailGraphLive.EndpointKey endpoint = new(entry.StopX16, entry.StopY16, entry.StopZ16, entry.Key.Dimension, entry.Gauge);
		if (!StationsByStopEndpoint.TryGetValue(endpoint, out List<RailStationEntry>? endpointEntries)) return;
		endpointEntries.Remove(entry);
		if (endpointEntries.Count == 0) StationsByStopEndpoint.Remove(endpoint);
	}

	internal static string NormalizeStationName(string name)
	{
		if (string.IsNullOrWhiteSpace(name)) return "";

		StringBuilder stringBuilder = new(name.Length);
		bool space = false;

		for (int characterIndex = 0; characterIndex < name.Length; characterIndex++)
		{
			char character = name[characterIndex];

			if (char.IsWhiteSpace(character)) { if (stringBuilder.Length > 0) space = true; continue; }
			if (space) { stringBuilder.Append(' '); space = false; }

			stringBuilder.Append(char.ToLowerInvariant(character));
		}

		return stringBuilder.ToString();
	}

	internal static ulong HashNormalizedName(string normalizedName)
	{
		const ulong offset = 14695981039346656037UL;
		const ulong prime = 1099511628211UL;

		ulong hash = offset;
		for (int characterIndex = 0; characterIndex < normalizedName.Length; characterIndex++)
		{
			hash ^= normalizedName[characterIndex];
			hash *= prime;
		}

		return hash == 0 ? 1UL : hash;
	}

	private static string GetBlockRotation(Block? block)
	{
		if (block?.Variant != null && block.Variant.TryGetValue("rot", out string? rotationCode)) return rotationCode ?? "";
		return "";
	}

	private static bool TryResolveStationStop(BlockPos position, Block? block, out byte gauge, out RailGraphLive.EndpointKey stop)
	{
		gauge = 0;
		stop = default;

		if (!TrackSpecsDictionary.TryGet(block, out TrackPieceSpec trackPieceSpecification)) return false;
		gauge = trackPieceSpecification.Gauge;

		List<RailGraphLive.EndpointKey> endpoints = new(4);
		RailGraphLive.EndpointKey foundStop = default;

		for (int pathIndex = 0; pathIndex < trackPieceSpecification.Paths.Length; pathIndex++)
		{
			Vec3f[] localPoints = trackPieceSpecification.Paths[pathIndex].LocalPoints;
			if (localPoints == null || localPoints.Length < 2) continue;

			if (TryAddOrFindDuplicate(QuantizeEndpoint(position, localPoints[0], gauge))) { stop = foundStop; return true; }
			if (TryAddOrFindDuplicate(QuantizeEndpoint(position, localPoints[localPoints.Length - 1], gauge))) { stop = foundStop; return true; }
		}

		return false;

		bool TryAddOrFindDuplicate(RailGraphLive.EndpointKey endpoint)
		{
			for (int endpointIndex = 0; endpointIndex < endpoints.Count; endpointIndex++)
			{
				if (!endpoints[endpointIndex].Equals(endpoint)) continue;
				foundStop = endpoint;
				return true;
			}

			endpoints.Add(endpoint);
			return false;
		}
	}

	private static RailGraphLive.EndpointKey QuantizeEndpoint(BlockPos position, Vec3f local, byte gauge)
	{
		return new RailGraphLive.EndpointKey
		(
			(int)Math.Round((position.X + local.X) * 16.0),
			(int)Math.Round((position.Y + local.Y) * 16.0),
			(int)Math.Round((position.Z + local.Z) * 16.0),
			position.dimension,
			gauge
		);
	}

	internal readonly record struct StationBlockKey(int X, int Y, int Z, int Dimension);
	private readonly record struct StationListGroupKey(ulong NameHash, int Dimension, byte Gauge);

	internal sealed class RailStationEntry
	{
		public StationBlockKey Key;
		public string DisplayName = "";
		public ulong NameHash;

		public byte Gauge;
		public bool HasStopEndpoint;
		public int StopX16;
		public int StopY16;
		public int StopZ16;
		public string RotationCode = "";

		internal bool SamePhysicalStopAs(RailStationEntry? other)
		{
			return other != null
				&& Key.Equals(other.Key)
				&& Gauge == other.Gauge
				&& HasStopEndpoint == other.HasStopEndpoint
				&& StopX16 == other.StopX16
				&& StopY16 == other.StopY16
				&& StopZ16 == other.StopZ16
				&& string.Equals(RotationCode ?? "", other.RotationCode ?? "", StringComparison.OrdinalIgnoreCase);
		}

		public bool SameAs(RailStationEntry other)
		{
			return Key.Equals(other.Key)
				&& string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal)
				&& NameHash == other.NameHash
				&& Gauge == other.Gauge
				&& HasStopEndpoint == other.HasStopEndpoint
				&& StopX16 == other.StopX16
				&& StopY16 == other.StopY16
				&& StopZ16 == other.StopZ16
				&& string.Equals(RotationCode, other.RotationCode, StringComparison.Ordinal);
		}
	}
}
