using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace YangTransport;

/// Runtime lookup table for rail piece geometry.
/// Track specs are compiled once from final registered block JSON attributes during AssetsFinalize, then accessed by block id.
public static class TrackSpecsDictionary
{
	public const float DefaultY = 0.125f;

	// Hot-path lookup | Every concrete block id, including material variants, points at its compiled spec.
	private static Dictionary<int, TrackPieceSpec> SpecificationsByBlockID = new();

	// Aggregate lookup: one material-neutral spec per geometry, used for deterministic hashing,
	// max-reach calculation, and conflict detection. This is not used by gameplay hot paths.
	private static Dictionary<string, TrackPieceSpec> UniqueSpecificationsByGeometryCode = new(StringComparer.OrdinalIgnoreCase);
	private static Dictionary<int, ushort> MaterialSpeedCapByBlockID = new();
	private static Dictionary<byte, Dictionary<string, ushort>> MaterialSpeedsByGauge = new();

	public static bool IsCompiled { get; private set; }
	public static int RegisteredBlockSpecificationCount => SpecificationsByBlockID.Count;
	public static int RegisteredUniqueSpecificationCount => UniqueSpecificationsByGeometryCode.Count;

	private static int CachedMaxHorizontalSpecificationReach;
	private static int CachedMaxSpecificationReachY;
	private static bool CachedMaxSpecificationReachValid;

	private static uint CachedSpecificationsHash;
	private static bool CachedSpecificationsHashValid;

	public static void RequireCompiled()
	{
		if (IsCompiled) return;
		throw new InvalidOperationException("[YangTransport] TrackSpecsDictionary was read before TrackSpecs were compiled from registered blocks!");
	}

	private static Dictionary<byte, Dictionary<string, ushort>> LoadMaterialSpeedConfiguration(ICoreAPI coreAPI)
	{
		if (coreAPI == null) throw new ArgumentNullException(nameof(coreAPI));

		try
		{
			IAsset? asset = coreAPI.Assets.TryGet(new AssetLocation("yangtransport:config/trackmaterialspeeds.json"));
			if (asset == null) { throw new InvalidOperationException("[YangTransport] trackmaterialspeeds.json missing. Aborting."); }

			Dictionary<string, Dictionary<string, int>>? rawConfiguration = asset.ToObject<Dictionary<string, Dictionary<string, int>>>();
			return CompileMaterialSpeedConfiguration(rawConfiguration);
		}
		catch (Exception exception) { coreAPI.Logger.Error("[YangTransport] Failed to load required track material speed config."); coreAPI.Logger.Error(exception); throw; }
	}

	private static Dictionary<byte, Dictionary<string, ushort>> CompileMaterialSpeedConfiguration(Dictionary<string, Dictionary<string, int>>? rawConfiguration)
	{
		if (rawConfiguration == null || rawConfiguration.Count == 0) { throw new InvalidOperationException("[YangTransport] trackmaterialspeeds.json is empty!"); }

		Dictionary<byte, Dictionary<string, ushort>> loaded = new();

		foreach (KeyValuePair<string, Dictionary<string, int>> gaugePair in rawConfiguration)
		{
			if (!byte.TryParse(gaugePair.Key, out byte gauge))
			{
				throw new InvalidOperationException($"[YangTransport] Gauge '{gaugePair.Key}' in trackmaterialspeeds.json is not a byte.");
			}

			if (loaded.ContainsKey(gauge))
			{
				throw new InvalidOperationException($"[YangTransport] trackmaterialspeeds.json defines gauge {gauge} more than once, a sub-mod applied broken patch!");
			}

			if (gaugePair.Value == null || gaugePair.Value.Count == 0)
			{
				throw new InvalidOperationException($"[YangTransport] trackmaterialspeeds.json gauge {gauge} must define at least one material, data missing.");
			}

			Dictionary<string, ushort> speeds = new(StringComparer.OrdinalIgnoreCase);

			foreach (KeyValuePair<string, int> speedPair in gaugePair.Value)
			{
				string material = (speedPair.Key ?? "").Trim();
				if (material.Length == 0) { throw new InvalidOperationException($"[YangTransport] trackmaterialspeeds.json gauge {gauge} contains empty material name."); }

				int speed = speedPair.Value;
				if (speed <= 0 || speed > ushort.MaxValue) // Probably overkill to bother checking for this. If a user does this, it's on them at this point.
				{
					throw new InvalidOperationException($"[YangTransport] trackmaterialspeeds.json gauge {gauge} material '{material}' has invalid speed cap {speed}.");
				}

				speeds[material] = (ushort)speed;
			}

			loaded[gauge] = speeds;
		}

		if (!loaded.ContainsKey(0) || !loaded.ContainsKey(1))
		{
			throw new InvalidOperationException("[YangTransport] trackmaterialspeeds.json is missing base gauge values! Must be included even if unused.");
		}

		return loaded;
	}

	public static void CompileFromRegisteredBlocks(ICoreAPI coreAPI)
	{
		if (coreAPI == null) throw new ArgumentNullException(nameof(coreAPI));

		Dictionary<byte, Dictionary<string, ushort>> materialSpeeds = LoadMaterialSpeedConfiguration(coreAPI);
		Dictionary<int, TrackPieceSpec> specificationsByBlockID = new();
		Dictionary<string, TrackPieceSpec> uniqueSpecificationsByGeometryCode = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<int, ushort> materialSpeedCapByBlockID = new();
		Dictionary<byte, int> specificationsByGauge = new();

		int trackBlockVariants = 0;
		int trackSpecificationAttributes = 0;
		int signalSpecifications = 0;
		int skippedTrackVariants = 0;
		HashSet<string> skippedTrackDomains = new(StringComparer.OrdinalIgnoreCase);

		foreach (Block block in coreAPI.World.Blocks)
		{
			if (block == null || block.Code == null) continue;

			bool hasTrackSpecification = HasTrackSpecification(block);
			if (block is not BlockTrackBase track)
			{
				if (hasTrackSpecification)
				{
					coreAPI.Logger.Error("[YangTransport] Block '{0}' defines TrackSpec but is not a YangTransport track block class. Ignoring.", block.Code);
				}
				continue;
			}

			trackBlockVariants++;
			if (!track.TrackConfigCompiled)
			{
				skippedTrackVariants++;
				skippedTrackDomains.Add(block.Code.Domain);
				continue;
			}

			if (!hasTrackSpecification)
			{
				coreAPI.Logger.Error("[YangTransport] Track block '{0}' is missing a TrackSpec and can not be registered as rail. Ignoring.", block.Code);
				skippedTrackVariants++;
				skippedTrackDomains.Add(block.Code.Domain);
				continue;
			}

			trackSpecificationAttributes++;
			if (!TryCompileFromBlock(block, track.TrackGauge, coreAPI.Logger, out TrackPieceSpec? trackSpecification))
			{
			    skippedTrackVariants++;
			    skippedTrackDomains.Add(block.Code.Domain);
			    continue;
			}

			if (!TryResolveMaterialSpeedCapBPS(block, trackSpecification!.Gauge, materialSpeeds, out ushort materialCap))
			{
				coreAPI.Logger.Error
				(
					"[YangTransport] Track block '{0}' uses gauge {1}, but trackmaterialspeeds.json defines no material speeds for that gauge. Ignoring.",
					block.Code, trackSpecification.Gauge
				);
				skippedTrackVariants++;
				continue;
			}

			if (!RegisterCompiled(block, trackSpecification, coreAPI.Logger, specificationsByBlockID, uniqueSpecificationsByGeometryCode))
			{
				skippedTrackVariants++;
				continue;
			}

			materialSpeedCapByBlockID[block.Id] = materialCap;
			specificationsByGauge.TryGetValue(trackSpecification.Gauge, out int gaugeCount);
			specificationsByGauge[trackSpecification.Gauge] = gaugeCount + 1;
			if (trackSpecification.IsSignal) signalSpecifications++;
		}

		if (specificationsByBlockID.Count == 0) { throw new InvalidOperationException("[YangTransport] No rail TrackSpecs compiled from registered blocks."); }

		ComputeMaxSpecificationReach(uniqueSpecificationsByGeometryCode, out int maxHorizontalReach, out int maxReachY);
		uint specificationsHash = ComputeSpecificationsHash(uniqueSpecificationsByGeometryCode);

		SpecificationsByBlockID = specificationsByBlockID;
		UniqueSpecificationsByGeometryCode = uniqueSpecificationsByGeometryCode;
		MaterialSpeedCapByBlockID = materialSpeedCapByBlockID;
		MaterialSpeedsByGauge = materialSpeeds;

		CachedMaxHorizontalSpecificationReach = maxHorizontalReach;
		CachedMaxSpecificationReachY = maxReachY;
		CachedMaxSpecificationReachValid = true;
		CachedSpecificationsHash = specificationsHash;
		CachedSpecificationsHashValid = true;
		IsCompiled = true;

		if (coreAPI.Side == EnumAppSide.Server && maxHorizontalReach > 12) { coreAPI.Logger.Warning("[YangTransport] Track searh radius set to {0} blocks in order to accomodate for the largest known track. Values higher than 12 will have a negative impact on performance! Consider removing whichever sub-mod adds oversized tracks.", maxHorizontalReach ); }

		string gaugeSummary = string.Join (", ", specificationsByGauge.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"));

		if (skippedTrackVariants > 0)
		{
			coreAPI.Logger.Warning
			(
				"[YangTransport] Skipped {0} malformed track blocks and variants from: {1}. Please report these errors to the relevant mod authors!",
				skippedTrackVariants,
				(string.Join(", ", skippedTrackDomains.OrderBy(domain => domain)))
			);
		}

		coreAPI.Logger.Notification(
			"[YangTransport] TrackSpecs compiled from registered blocks: "
			+ "trackBlocks={0}, trackSpecAttributes={1}, skipped={2}, blockIds={3}, uniqueGeometry={4}, gauges=[{5}], signals={6}, hash={7}.",
			trackBlockVariants,
			trackSpecificationAttributes,
			skippedTrackVariants,
			SpecificationsByBlockID.Count,
			UniqueSpecificationsByGeometryCode.Count,
			gaugeSummary,
			signalSpecifications,
			specificationsHash
		);
	}

	private static bool HasTrackSpecification(Block block)
	{
		JsonObject? root = block.Attributes?["TrackSpec"];
		return root != null && root.Exists;
	}

	private static bool TryCompileFromBlock(Block block, byte configuredGauge, ILogger logger, out TrackPieceSpec? trackSpecification)
	{
		trackSpecification = null;
		JsonObject? root = block.Attributes?["TrackSpec"];
		if (root == null || !root.Exists) return false;

		return TrackSpecificationDataCompiler.TryCompile(root, block, configuredGauge, logger, out trackSpecification);
	}

	private static bool RegisterCompiled
	(
		Block block, TrackPieceSpec trackSpecification, ILogger logger,
		Dictionary<int, TrackPieceSpec> specificationsByBlockID,
		Dictionary<string, TrackPieceSpec> uniqueSpecificationsByGeometryCode
	)
	{
		string geometryCode = GetGeometryCode(block);

		if (uniqueSpecificationsByGeometryCode.TryGetValue(geometryCode, out TrackPieceSpec? existing))
		{
			if (!SpecificationsEqual(existing, trackSpecification))
			{
				logger.Error
				(
					"[YangTransport] Conflicting TrackSpec for '{0}' under geometry key '{1}'. This block will not be registered as rail track.",
					block.Code, geometryCode
				);
				return false;
			}

			specificationsByBlockID[block.Id] = existing;
			return true;
		}

		uniqueSpecificationsByGeometryCode[geometryCode] = trackSpecification;
		specificationsByBlockID[block.Id] = trackSpecification;
		return true;
	}

	public static bool IsTrack(Block? block) { return TryGet(block, out _); }

	// Resolve a piece spec for the given block. Hot path would be integer dictionary lookup only.
	public static bool TryGet(Block? block, out TrackPieceSpec trackSpecification)
	{
		trackSpecification = default!;
		return block != null && SpecificationsByBlockID.TryGetValue(block.Id, out trackSpecification);
	}

	// Returns the largest anchor-to-geometry reach of all registered specs, rounded up to whole blocks plus one safety block.
	// Used by mirroring placement searches and explicit developer rebuilds where the owning block may lie outside the target area.
	public static void GetMaxSpecReach(out int horizontalPadding, out int verticalPadding)
	{
		if (!CachedMaxSpecificationReachValid)
		{
			ComputeMaxSpecificationReach(UniqueSpecificationsByGeometryCode, out CachedMaxHorizontalSpecificationReach, out CachedMaxSpecificationReachY);
			CachedMaxSpecificationReachValid = true;
		}

		horizontalPadding = CachedMaxHorizontalSpecificationReach;
		verticalPadding = CachedMaxSpecificationReachY;
	}

	private static void ComputeMaxSpecificationReach(Dictionary<string, TrackPieceSpec> uniqueSpecificationsByGeometryCode, out int horizontalPadding, out int verticalPadding)
	{
		float maxHorizontalCoordinate = 0f;
		float maxY = 0f;

		foreach (TrackPieceSpec trackSpecification in uniqueSpecificationsByGeometryCode.Values)
		{
			TrackPath[] paths = trackSpecification.Paths;
			if (paths == null) continue;

			for (int pathIndex = 0; pathIndex < paths.Length; pathIndex++)
			{
				Vec3f[] points = paths[pathIndex]?.LocalPoints;
				if (points == null) continue;

				for (int pointIndex = 0; pointIndex < points.Length; pointIndex++)
				{
					Vec3f point = points[pointIndex];
					float ax = Math.Abs(point.X);
					float az = Math.Abs(point.Z);
					float ay = Math.Abs(point.Y);

					if (ax > maxHorizontalCoordinate) maxHorizontalCoordinate = ax;
					if (az > maxHorizontalCoordinate) maxHorizontalCoordinate = az;
					if (ay > maxY) maxY = ay;
				}
			}
		}

		horizontalPadding = (int)Math.Ceiling(maxHorizontalCoordinate) + 1;
		verticalPadding = (int)Math.Ceiling(maxY) + 1;
	}

	public static ushort GetMaterialSpeedCapBPS(Block block, byte gauge)
	{
		RequireCompiled();
		if (MaterialSpeedCapByBlockID.TryGetValue(block.Id, out ushort cap)) return cap;
		return ResolveMaterialSpeedCapBPS(block, gauge, MaterialSpeedsByGauge);
	}

	private static bool TryResolveMaterialSpeedCapBPS(Block block,byte gauge, Dictionary<byte, Dictionary<string, ushort>> materialSpeedsByGauge, out ushort cap)
	{
		cap = 0;

		string? materialCode = null;
		block.Variant?.TryGetValue("mat", out materialCode);
		if (string.IsNullOrEmpty(materialCode)) materialCode = block.Attributes?["TrackMat"]?.AsString(null);

		if (!materialSpeedsByGauge.TryGetValue(gauge, out Dictionary<string, ushort>? materialSpeeds) || materialSpeeds.Count == 0) { return false; }

		if (materialCode != null && materialSpeeds.TryGetValue(materialCode, out cap)) return true;
		cap = LowestSpeed(materialSpeeds);
		return true;
	}

	private static ushort ResolveMaterialSpeedCapBPS(Block block, byte gauge, Dictionary<byte, Dictionary<string, ushort>> materialSpeedsByGauge)
	{
		if (TryResolveMaterialSpeedCapBPS(block, gauge, materialSpeedsByGauge, out ushort cap)) return cap;
		throw new InvalidOperationException($"[YangTransport] trackmaterialspeeds.json gauge {gauge} does not define any material speeds.");
	}

	// Returns a small, fast hash representing the current TrackSpecsDictionary contents.
	// Saved with railgraph persistence as diagnostic provenance, it is not a load-validity gate.
	public static uint GetSpecsHash()
	{
		RequireCompiled();

		if (CachedSpecificationsHashValid) return CachedSpecificationsHash;

		CachedSpecificationsHash = ComputeSpecificationsHash(UniqueSpecificationsByGeometryCode);
		CachedSpecificationsHashValid = true;
		return CachedSpecificationsHash;
	}

	private static uint ComputeSpecificationsHash(Dictionary<string, TrackPieceSpec> uniqueSpecificationsByGeometryCode)
	{
		uint hash = 2166136261u;

		foreach (KeyValuePair<string, TrackPieceSpec> specificationPair in uniqueSpecificationsByGeometryCode.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
		{
			MixString(ref hash, specificationPair.Key);

			TrackPieceSpec trackSpecification = specificationPair.Value;
			MixByte(ref hash, trackSpecification.Gauge);
			MixByte(ref hash, (byte)trackSpecification.SignalKind);
			MixByte(ref hash, trackSpecification.OneWayAllowedFromPathIndex);
			MixByte(ref hash, QuantizeFactorTo8Bit(trackSpecification.MaxSpeedFactor01));

			TrackPath[]? paths = trackSpecification.Paths;
			MixInt(ref hash, paths?.Length ?? 0);

			if (paths == null) continue;

			for (int pathIndex = 0; pathIndex < paths.Length; pathIndex++)
			{
				Vec3f[]? points = paths[pathIndex]?.LocalPoints;
				MixInt(ref hash, points?.Length ?? 0);

				if (points == null) continue;

				for (int pointIndex = 0; pointIndex < points.Length; pointIndex++)
				{
					MixInt(ref hash, QuantizeCoordinateTo256ths(points[pointIndex].X));
					MixInt(ref hash, QuantizeCoordinateTo256ths(points[pointIndex].Y));
					MixInt(ref hash, QuantizeCoordinateTo256ths(points[pointIndex].Z));
				}
			}
		}

		return hash;
	}

	private static ushort LowestSpeed(Dictionary<string, ushort> speeds) // Also makes up a speed if none
	{
		if (speeds.Count == 0) throw new InvalidOperationException("[YangTransport] Track material speed dictionary has no entries. Something has gone wrong.");

		ushort minimumSpeed = ushort.MaxValue;
		foreach (ushort speed in speeds.Values) { if (speed < minimumSpeed) minimumSpeed = speed; }
		return minimumSpeed;
	}

	private static string GetGeometryCode(Block block)
	{
		string code = block.Code.ToString();
		if (block.Variant?.TryGetValue("mat", out string materialCode) == true && !string.IsNullOrEmpty(materialCode))
		{
			string suffix = "-" + materialCode;
			if (code.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { return code.Substring(0, code.Length - suffix.Length); }
		}

		return code;
	}

	private static bool SpecificationsEqual(TrackPieceSpec firstSpecification, TrackPieceSpec secondSpecification)
	{
		if
		(
			firstSpecification.Gauge != secondSpecification.Gauge ||
			firstSpecification.SignalKind != secondSpecification.SignalKind ||
			firstSpecification.OneWayAllowedFromPathIndex != secondSpecification.OneWayAllowedFromPathIndex ||
			QuantizeFactorTo8Bit(firstSpecification.MaxSpeedFactor01) != QuantizeFactorTo8Bit(secondSpecification.MaxSpeedFactor01)
		) { return false; }

		TrackPath[] firstPaths = firstSpecification.Paths ?? Array.Empty<TrackPath>();
		TrackPath[] secondPaths = secondSpecification.Paths ?? Array.Empty<TrackPath>();
		if (firstPaths.Length != secondPaths.Length) return false;

		for (int pathIndex = 0; pathIndex < firstPaths.Length; pathIndex++)
		{
			Vec3f[] firstPoints = firstPaths[pathIndex]?.LocalPoints ?? Array.Empty<Vec3f>();
			Vec3f[] secondPoints = secondPaths[pathIndex]?.LocalPoints ?? Array.Empty<Vec3f>();
			if (firstPoints.Length != secondPoints.Length) return false;

			for (int pointIndex = 0; pointIndex < firstPoints.Length; pointIndex++)
			{
				if
				(
					QuantizeCoordinateTo256ths(firstPoints[pointIndex].X) != QuantizeCoordinateTo256ths(secondPoints[pointIndex].X) ||
					QuantizeCoordinateTo256ths(firstPoints[pointIndex].Y) != QuantizeCoordinateTo256ths(secondPoints[pointIndex].Y) ||
					QuantizeCoordinateTo256ths(firstPoints[pointIndex].Z) != QuantizeCoordinateTo256ths(secondPoints[pointIndex].Z)
				)
				{ return false; }
			}
		}

		return true;
	}

	private static byte QuantizeFactorTo8Bit(float normalizedFactor) { return (byte)GameMath.Clamp((int)Math.Round(GameMath.Clamp(normalizedFactor, 0f, 1f) * 255f), 0, 255); }
	private static int QuantizeCoordinateTo256ths(float value) => (int)Math.Round(value * 256f);

	private static void MixByte(ref uint hash, byte value) { hash ^= value; hash *= 16777619u; }
	private static void MixInt(ref uint hash, int value) { unchecked { MixUInt(ref hash, (uint)value); } }
	private static void MixUInt(ref uint hash, uint value)
	{
		MixByte(ref hash, (byte)(value));
		MixByte(ref hash, (byte)(value >> 8));
		MixByte(ref hash, (byte)(value >> 16));
		MixByte(ref hash, (byte)(value >> 24));
	}

	private static void MixString(ref uint hash, string? value)
	{
		if (string.IsNullOrEmpty(value)) { MixByte(ref hash, 0); return; }

		for (int characterIndex = 0; characterIndex < value.Length; characterIndex++)
		{
			char character = value[characterIndex];
			MixByte(ref hash, (byte)character);
			MixByte(ref hash, (byte)(character >> 8));
		}

		MixByte(ref hash, 0);
	}
}

internal static class TrackSpecificationDataCompiler
{
	private const float MaxABSCoordinate = 128f;

	public static bool TryCompile(JsonObject configurationRoot, Block block, byte configuredGauge, ILogger logger, out TrackPieceSpec? trackSpecification)
	{
		trackSpecification = null;

		if (configurationRoot["Gauge"].Exists)
		{
			LogTrackSpecificationError(logger, block, "Gauge is no longer valid inside TrackSpec; define it once in attributes.TrackConfig.");
			return false;
		}

		byte gauge = configuredGauge;

		JsonObject speedRoot = configurationRoot["SpeedFactor"].Exists ? configurationRoot["SpeedFactor"] : configurationRoot["MaxSpeedFactor01"];
		double rawSpeed = speedRoot.AsDouble(1.0);
		float speedFactor = (float)rawSpeed;
		if (double.IsNaN(rawSpeed) || double.IsInfinity(rawSpeed) || rawSpeed < float.MinValue || rawSpeed > float.MaxValue)
		{
			LogTrackSpecificationError(logger, block, "SpeedFactor value outside of valid range.");
			return false;
		}

		if (speedFactor < 0f || speedFactor > 1f)
		{
			float clamped = GameMath.Clamp(speedFactor, 0f, 1f);
			logger.Warning("[YangTransport] Clamped Speed Factor for '{0}' from {1} to {2}.", block.Code, speedFactor, clamped);
			speedFactor = clamped;
		}

		if (!TryParseSignalKind(configurationRoot["SignalKind"].AsString("none"), out SignalKind signalKind))
		{
			LogTrackSpecificationError(logger, block, "Invalid defined SignalKind. Sub-mod probably wrote it wrong, double check spelling."); return false;
		}

		int oneWayIndex = configurationRoot["OneWayAllowedFromPathIndex"].AsInt(0);
		if (oneWayIndex < byte.MinValue || oneWayIndex > byte.MaxValue) { LogTrackSpecificationError(logger, block, "OneWayAllowedFromPathIndex value too big."); return false; }

		JsonObject pathsRoot = configurationRoot["Paths"];
		bool singlePath = false;
		if (!pathsRoot.Exists) pathsRoot = configurationRoot["TrackPaths"]; if (!pathsRoot.Exists) { pathsRoot = configurationRoot["TrackPath"]; singlePath = pathsRoot.Exists; }

		if (!pathsRoot.Exists || !pathsRoot.IsArray()) { LogTrackSpecificationError(logger, block, "Paths must be a non-empty array."); return false; }

		TrackPath[] paths;
		if (singlePath)
		{
			if (!TryParsePath(pathsRoot, block, 0, logger, out TrackPath? path)) return false;
			paths = new[] { path! };
		}
		else
		{
			JsonObject[]? pathObjects = pathsRoot.AsArray();
			if (pathObjects == null || pathObjects.Length == 0) { LogTrackSpecificationError(logger, block, "Paths must contain at least one path."); return false; }

			paths = new TrackPath[pathObjects.Length];
			for (int pathIndex = 0; pathIndex < pathObjects.Length; pathIndex++)
			{
				if (!TryParsePath(pathObjects[pathIndex], block, pathIndex, logger, out TrackPath? path)) return false;
				paths[pathIndex] = path!;
			}
		}

		if ((signalKind == SignalKind.OneWay || signalKind == SignalKind.Chain) && oneWayIndex >= paths.Length)
		{
			LogTrackSpecificationError(logger, block, "OneWayAllowedFromPathIndex references a path that does not exist!");
			return false;
		}

		trackSpecification = new TrackPieceSpec(gauge, signalKind, (byte)oneWayIndex, speedFactor, paths);
		return true;
	}

	private static bool TryParsePath(JsonObject pathRoot, Block block, int pathIndex, ILogger logger, out TrackPath? path)
	{
		path = null;

		JsonObject[]? pointObjects = pathRoot.AsArray();
		if (pointObjects == null || pointObjects.Length < 2) { LogTrackSpecificationError(logger, block, $"Paths[{pathIndex}] must contain at least two points."); return false; }

		Vec3f[] points = new Vec3f[pointObjects.Length];
		for (int pointIndex = 0; pointIndex < pointObjects.Length; pointIndex++)
		{
			if (!TryParsePoint(pointObjects[pointIndex], block, pathIndex, pointIndex, logger, out Vec3f point)) return false;
			points[pointIndex] = point;

			if (pointIndex > 0 && IsZeroLength(points[pointIndex - 1], points[pointIndex]))
			{
				LogTrackSpecificationError(logger, block, $"Paths[{pathIndex}][{pointIndex - 1}] and Paths[{pathIndex}][{pointIndex}] create a zero-length segment.");
				return false;
			}
		}

		path = new TrackPath(points);
		return true;
	}

	private static bool TryParsePoint(JsonObject pointRoot, Block block, int pathIndex, int pointIndex, ILogger logger, out Vec3f point)
	{
		point = default;

		JsonObject[]? coordinates = pointRoot.AsArray();
		if (coordinates == null || (coordinates.Length != 2 && coordinates.Length != 3))
		{
			LogTrackSpecificationError(logger, block, $"Paths[{pathIndex}][{pointIndex}] must be [x, z] or [x, y, z].");
			return false;
		}

		float x = (float)coordinates[0].AsDouble(double.NaN);
		float y, z;

		if (coordinates.Length == 2)	{ y = TrackSpecsDictionary.DefaultY;			z = (float)coordinates[1].AsDouble(double.NaN); }
		else					{ y = (float)coordinates[1].AsDouble(double.NaN);	z = (float)coordinates[2].AsDouble(double.NaN); }

		if (!IsValidCoordinate(x) || !IsValidCoordinate(y) || !IsValidCoordinate(z))
		{
			LogTrackSpecificationError(logger, block, $"Paths[{pathIndex}][{pointIndex}] contains an invalid coordinate.");
			return false;
		}

		point = new Vec3f(x, y, z);
		return true;
	}

	private static bool IsValidCoordinate(float value) { return !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= MaxABSCoordinate; }
	private static bool IsZeroLength(Vec3f a, Vec3f b) { return a.X == b.X && a.Y == b.Y && a.Z == b.Z; }

	private static bool TryParseSignalKind(string? value, out SignalKind kind)
	{
		switch ((value ?? "none").Trim().ToLowerInvariant())
		{
			case "":
			case "none":
				kind = SignalKind.None;
			return true;

			case "block":
				kind = SignalKind.Block;
			return true;

			case "oneway":
			case "one-way":
			case "one_way":
				kind = SignalKind.OneWay;
			return true;

			case "chain":
				kind = SignalKind.Chain;
			return true;

			default:
				kind = SignalKind.None;
			return false;
		}
	}

	private static void LogTrackSpecificationError(ILogger logger, Block block, string message)
	{
		logger.Error("[YangTransport] Invalid TrackSpec for '{0}': {1}", block.Code, message);
	}
}

public enum SignalKind : byte
{
	None = 0,
	Block = 1,
	OneWay = 2,
	Chain = 3
}

public sealed class TrackPieceSpec // Junctions and forks can contain multiple independent paths
{
	public byte Gauge { get; }
	public SignalKind SignalKind { get; }
	public byte OneWayAllowedFromPathIndex { get; }
	public float MaxSpeedFactor01 { get; }
	public TrackPath[] Paths { get; }

	public bool IsSignal => SignalKind != SignalKind.None;

	public TrackPieceSpec(params TrackPath[] paths) : this(1, SignalKind.None, oneWayAllowedFromPathIndex: 0, maxSpeedFactor01: 1f, paths) { }
	public TrackPieceSpec(byte gauge, params TrackPath[] paths) : this(gauge, SignalKind.None, oneWayAllowedFromPathIndex: 0, maxSpeedFactor01: 1f, paths) { }
	public TrackPieceSpec(byte gauge, float maxSpeedFactor01, params TrackPath[] paths) : this(gauge, SignalKind.None, oneWayAllowedFromPathIndex: 0, maxSpeedFactor01: maxSpeedFactor01, paths) { }

	public TrackPieceSpec(byte gauge, SignalKind signalKind, float maxSpeedFactor01, params TrackPath[] paths)
		: this(gauge, signalKind, oneWayAllowedFromPathIndex: 0, maxSpeedFactor01: maxSpeedFactor01, paths) { }

	public TrackPieceSpec(byte gauge, SignalKind signalKind, byte oneWayAllowedFromPathIndex, float maxSpeedFactor01, params TrackPath[] paths)
	{
		Gauge = gauge;
		SignalKind = signalKind;
		OneWayAllowedFromPathIndex = oneWayAllowedFromPathIndex;
		MaxSpeedFactor01 = GameMath.Clamp(maxSpeedFactor01, 0f, 1f);
		Paths = paths ?? Array.Empty<TrackPath>();
	}
}

/// A single traversable path inside a piece: a polyline in local block space. Endpoints are LocalPoints[0] and LocalPoints[^1].
public sealed class TrackPath
{
	public Vec3f[] LocalPoints { get; }
	public TrackPath(params Vec3f[] localPoints) { LocalPoints = localPoints ?? Array.Empty<Vec3f>(); }
}
