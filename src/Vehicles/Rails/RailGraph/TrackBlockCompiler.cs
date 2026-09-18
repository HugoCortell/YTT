using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace YangTransport;

/// Compiles TrackConfig and direct variant lookups after asset variants are finalized.
internal static class TrackBlockCompiler
{
	private static readonly HashSet<byte> DynamicGauges = new();

	internal static bool HasDynamicTracks(byte gauge) => DynamicGauges.Contains(gauge);

	internal static void CompileAndValidate(ICoreAPI coreAPI)
	{
		DynamicGauges.Clear();
		List<BlockTrackBase> tracks = new();
		int skippedTrackVariants = 0;

		foreach (Block block in coreAPI.World.Blocks)
		{
			if (block is not BlockTrackBase track || block.Code == null) continue;

			track.InvalidateTrackConfig();

			try
			{
				JsonObject configuration = Required(block, block.Attributes?["TrackConfig"], "attributes.TrackConfig");
				int rawGauge = Required(block, configuration["Gauge"], "TrackConfig.Gauge").AsInt(-1);
				if (rawGauge < byte.MinValue || rawGauge > byte.MaxValue) throw new TrackConfigurationException(block, "Gauge must be a byte.");

				bool hasAlignment = configuration["Alignment"].Exists;
				bool hasManualIconRotation = configuration["ManualPlacementIconRotation"].Exists;
				int manualIconRotation = hasManualIconRotation ? configuration["ManualPlacementIconRotation"].AsInt(0) : 0;

				switch (track)
				{
					case BlockTrackStatic:
						RejectUnused(block, hasAlignment, hasManualIconRotation);
					break;

					case BlockTrackDynamic:
						RejectUnused(block, hasAlignment, hasManualIconRotation);
					break;

					case BlockTrackAligned alignedTrack:
						if (!hasAlignment) throw new TrackConfigurationException(block, "TrackAligned requires Alignment.");
						string name = configuration["Alignment"].AsString("");
						if (!Enum.TryParse(name, true, out TrackAlignmentMode mode) || !Enum.IsDefined(mode))
						{ 
							throw new TrackConfigurationException(block, $"unknown Alignment '{name}'.");
						}

						if (manualIconRotation < 0 || manualIconRotation >= 360 || manualIconRotation % 45 != 0)
						{
							throw new TrackConfigurationException(block, "ManualPlacementIconRotation must be 0 to 315 degrees in 45 degree increments.");
						}

						alignedTrack.ConfigureAlignment(mode, (byte)(manualIconRotation / 45));
					break;

					default: throw new TrackConfigurationException(block, $"unsupported track class '{track.GetType().FullName}'.");
				}

				track.ConfigureTrack((byte)rawGauge);
				tracks.Add(track);
			}
			catch (TrackConfigurationException error)
			{
				skippedTrackVariants++;
				coreAPI.Logger.Error("{0} Ignoring this track block.", error.Message);
			}
		}

		bool invalidatedAny;
		do
		{
			invalidatedAny = false;

			foreach (BlockTrackBase track in tracks)
			{
				if (!track.TrackConfigCompiled) continue;

				try
				{
					if (track is BlockTrackAligned aligned) aligned.CompileVariants(coreAPI);
					else if (track is BlockTrackDynamic dynamicTrack) dynamicTrack.CompileVariants(coreAPI);
				}
				catch (TrackConfigurationException error)
				{
					track.InvalidateTrackConfig();
					skippedTrackVariants++;
					invalidatedAny = true;
					coreAPI.Logger.Error("{0} Ignoring this track block.", error.Message);
				}
			}
		}
		while (invalidatedAny);

		int compiledTrackVariants = 0;
		foreach (BlockTrackBase track in tracks)
		{
			if (!track.TrackConfigCompiled) continue;

			compiledTrackVariants++;
			if (track is BlockTrackDynamic) DynamicGauges.Add(track.TrackGauge);
		}

		if (compiledTrackVariants == 0)
		{
			throw new InvalidOperationException("[YangTransport] No valid generalized track blocks were registered.");
		}

		if (skippedTrackVariants > 0)
		{
			coreAPI.Logger.Warning
			(
				"[YangTransport] Skipped {0} malformed TrackConfig track blocks or variants. Please fix these before releasing your sub-mod.",
				skippedTrackVariants
			);
		}

		coreAPI.Logger.Notification
		(
			"[YangTransport] Compiled {0} generalized track variants, skipped {1}, across {2} dynamic gauge(s).",
			compiledTrackVariants, skippedTrackVariants, DynamicGauges.Count
		);
	}

	internal static Block ResolveAlignedSibling(ICoreAPI coreAPI, BlockTrackAligned source, string key, string value)
	{
		if (source.Variant == null || !source.Variant.ContainsKey(key)) throw new TrackConfigurationException(source, $"alignment requires variant '{key}'.");

		AssetLocation code = source.CodeWithVariant(key, value);
		Block? block = coreAPI.World.GetBlock(code);
		if (block is not BlockTrackAligned alignedTrack) throw new TrackConfigurationException(source, $"required sibling '{code}' is missing or not TrackAligned.");
		ValidateSibling(source, alignedTrack, code);
		if (alignedTrack.AlignmentMode != source.AlignmentMode) throw new TrackConfigurationException(source, $"sibling '{code}' has a different Alignment.");
		return alignedTrack;
	}

	internal static Block ResolveDynamicSibling(ICoreAPI coreAPI, BlockTrackDynamic source, string category, string rotation)
	{
		if (source.Variant == null || !source.Variant.ContainsKey("cat") || !source.Variant.ContainsKey("rot"))
		{
			throw new TrackConfigurationException(source, "TrackDynamic requires 'cat' and 'rot' variants.");
		}

		AssetLocation code = source.CodeWithVariants(new Dictionary<string, string> { ["cat"] = category, ["rot"] = rotation });
		Block? block = coreAPI.World.GetBlock(code);
		if (block is not BlockTrackDynamic dynamicTrack) throw new TrackConfigurationException(source, $"required sibling '{code}' is missing or not TrackDynamic.");
		ValidateSibling(source, dynamicTrack, code);

		return dynamicTrack;
	}

	private static void ValidateSibling(BlockTrackBase source, BlockTrackBase sibling, AssetLocation code)
	{
		if (!sibling.TrackConfigCompiled) throw new TrackConfigurationException(source, $"sibling '{code}' was not compiled.");
		if (sibling.TrackGauge != source.TrackGauge) throw new TrackConfigurationException(source, $"sibling '{code}' has a different Gauge.");
	}

	private static void RejectUnused(Block block, bool hasAlignment, bool hasManualIconRotation)
	{
		if (hasAlignment) throw new TrackConfigurationException(block, $"{block.GetType().Name} must not define Alignment.");
		if (hasManualIconRotation) throw new TrackConfigurationException(block, $"{block.GetType().Name} must not define ManualPlacementIconRotation.");
	}

	private static JsonObject Required(Block block, JsonObject? value, string name)
	{
		if (value == null || !value.Exists) throw new TrackConfigurationException(block, $"missing required {name}.");
		return value!;
	}

	private sealed class TrackConfigurationException : InvalidOperationException
	{
		internal TrackConfigurationException(Block block, string message) : base($"[YangTransport] Invalid TrackConfig for '{block.Code}': {message}") { }
	}
}
