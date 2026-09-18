using System;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using System.Collections.Generic;

namespace YangTransport;

public enum TrackAlignmentMode : byte
{
	WorldAligned,
	DirectionAligned,
	DirectionAlignedMirroring
}

/// Shared placement, preview, graph, station, and track-information behavior.
public abstract class BlockTrackBase : Block
{
	private ICoreClientAPI? ClientAPI;
	private readonly Matrixf ModelMatrix = new();
	private bool IsStation;

	public byte TrackGauge { get; private set; }
	internal bool TrackConfigCompiled { get; private set; }

	internal void InvalidateTrackConfig()
	{
		TrackConfigCompiled = false;
	}

	internal void ConfigureTrack(byte gauge)
	{
		TrackGauge = gauge;
		TrackConfigCompiled = true;
	}

	public override void OnLoaded(ICoreAPI coreAPI)
	{
		base.OnLoaded(coreAPI);
		ClientAPI = coreAPI as ICoreClientAPI;
		IsStation = string.Equals(EntityClass, "YangTransportRailStation", StringComparison.Ordinal);

		Frostable = true;
		if (IsStation) PlacedPriorityInteract = true;
	}

	protected abstract bool TryResolvePlacedBlock(IWorldAccessor world, IPlayer? player, BlockSelection selection, out Block block);

	public override bool TryPlaceBlock(IWorldAccessor world, IPlayer player, ItemStack stack, BlockSelection selection, ref string failureCode)
	{
		if (!TryResolvePlacedBlock(world, player, selection, out Block placed)) { failureCode = "track-config-not-compiled"; return false; }

		bool placedSuccessfully = placed.CanPlaceBlock(world, player, selection, ref failureCode) && placed.DoPlaceBlock(world, player, selection, stack);
		if (placedSuccessfully) { TrackManualPlacement.Reset(player); }

		return placedSuccessfully;
	}

	public override bool CanPlaceBlock(IWorldAccessor world, IPlayer player, BlockSelection selection, ref string failureCode)
	{
		return base.CanPlaceBlock(world, player, selection, ref failureCode)
			&& CollisionFlowFieldPlacement.CanPlace(api, world.BlockAccessor, selection.Position, this, ref failureCode);
	}

	public override void OnBlockPlaced(IWorldAccessor world, BlockPos position, ItemStack byStack = null)
	{
		base.OnBlockPlaced(world, position, byStack);
		if (world.Side != EnumAppSide.Server) return;

		world.Api.ModLoader.GetModSystem<RailGraphServerSystem>()?.NotifyBlockPlaced(this, position);
		UpdateDynamicNeighbours(world, position);
	}

	public override void OnBlockRemoved(IWorldAccessor world, BlockPos position)
	{
		if (world.Side == EnumAppSide.Server) { world.Api.ModLoader.GetModSystem<RailGraphServerSystem>()?.NotifyBlockRemoved(this, position); }

		base.OnBlockRemoved(world, position);
		if (world.Side == EnumAppSide.Server) UpdateDynamicNeighbours(world, position);
	}

	private void UpdateDynamicNeighbours(IWorldAccessor world, BlockPos position)
	{
		if (TrackBlockCompiler.HasDynamicTracks(TrackGauge)) { DynamicTrackAutoUpdater.UpdateAround(world, position, TrackGauge); }
	}

	public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer player, BlockSelection selection)
	{
		if (IsStation && world.BlockAccessor.GetBlockEntity(selection.Position) is BlockEntityRailStation station && station.OnRightClick(player)) { return true; }

		return base.OnBlockInteractStart(world, player, selection);
	}

	public override string GetPlacedBlockInfo(IWorldAccessor world, BlockPos position, IPlayer player)
	{
		string information = base.GetPlacedBlockInfo(world, position, player);
		if (!TrackSpecsDictionary.TryGet(this, out TrackPieceSpec trackSpecification)) return information;

		if (trackSpecification.MaxSpeedFactor01 <= 0.99f)
		{
			string penalty = Lang.Get("yangtransport:locomotive-hudinfo-trackpenalty", trackSpecification.MaxSpeedFactor01);
			information = string.IsNullOrEmpty(information) ? penalty : information + "\n" + penalty;
		}

		if (!trackSpecification.IsSignal && RailSystemDebug.ClientTrackZoneDebugHUDEnabled)
		{
			information = RailSystemDebug.AppendTrackDebugHUDInfo(this, position, information);
		}
		else if (trackSpecification.IsSignal && RailSystemDebug.ClientSignalDebugHUDEnabled && RailSystemDebug.ClientAutomationDebugHUDEnabled)
		{
			var informationBuilder = new StringBuilder(information ?? string.Empty);
			RailSystemDebug.AppendSignalDebugHUDInfo(world.Api, position, informationBuilder);
			information = informationBuilder.ToString();
		}

		return information;
	}

	public override void OnHeldRenderOit(ItemSlot slot, IClientPlayer player)
	{
		if (ClientAPI == null || player.CurrentBlockSelection is not BlockSelection rawSelection) return;

		BlockSelection selection = HeldGhostPreview.MakePlacementSelection(rawSelection, player, this);
		if (!TryResolvePlacedBlock(ClientAPI.World, player, selection, out Block placed)) return;

		string failureCode = "";
		bool canPlace = placed.CanPlaceBlock(ClientAPI.World, player, selection, ref failureCode);
		HeldGhostPreview.RenderOit(ClientAPI, player, selection.Position, placed, canPlace, ModelMatrix);
	}
}

/// Places the held track without selecting another alignment variant.
public sealed class BlockTrackStatic : BlockTrackBase
{
	protected override bool TryResolvePlacedBlock(IWorldAccessor world, IPlayer? player, BlockSelection selection, out Block block)
	{
		block = this;
		return TrackConfigCompiled;
	}
}

/// Selects an axis, facing, or mirrored-facing variant compiled from TrackConfig.
public sealed class BlockTrackAligned : BlockTrackBase
{
	private readonly Block?[] NormalVariants = new Block?[4];
	private Block?[]? MirroredVariants;

	internal TrackAlignmentMode AlignmentMode { get; private set; }
	internal byte ManualPlacementIconRotationSteps { get; private set; }

	internal void ConfigureAlignment(TrackAlignmentMode mode, byte manualPlacementIconRotationSteps)
	{
		AlignmentMode = mode;
		ManualPlacementIconRotationSteps = manualPlacementIconRotationSteps;
	}

	internal BlockTrackAligned? ManualPlacementFamilyRoot => NormalVariants[0] as BlockTrackAligned;

	internal int ManualPlacementVariantCount => AlignmentMode switch
	{
		TrackAlignmentMode.WorldAligned => 2,
		TrackAlignmentMode.DirectionAlignedMirroring => 8,
		_ => 4
	};

	internal BlockTrackAligned GetManualPlacementVariant(int index)
	{
		return index < 4 ? (BlockTrackAligned)NormalVariants[index]! : (BlockTrackAligned)MirroredVariants![index - 4]!;
	}

	internal void CompileVariants(ICoreAPI coreAPI)
	{
		if (AlignmentMode == TrackAlignmentMode.WorldAligned)
		{
			Block northSouthTrack = TrackBlockCompiler.ResolveAlignedSibling(coreAPI, this, "axis", "ns");
			Block westEastTrack = TrackBlockCompiler.ResolveAlignedSibling(coreAPI, this, "axis", "we");
			NormalVariants[0] = NormalVariants[2] = northSouthTrack;
			NormalVariants[1] = NormalVariants[3] = westEastTrack;

			return;
		}

		for (int variantIndex = 0; variantIndex < 4; variantIndex++)
		{
			NormalVariants[variantIndex] = TrackBlockCompiler.ResolveAlignedSibling(coreAPI, this, "rot", TrackAlignmentResolver.Rotations[variantIndex]);
		}

		if (AlignmentMode != TrackAlignmentMode.DirectionAlignedMirroring) return;
		MirroredVariants = new Block?[4];
		for (int variantIndex = 0; variantIndex < 4; variantIndex++)
		{
			MirroredVariants[variantIndex] = TrackBlockCompiler.ResolveAlignedSibling(coreAPI, this, "rot", TrackAlignmentResolver.Rotations[variantIndex] + "_r");
		}
	}

	public override SkillItem[]? GetToolModes(ItemSlot slot, IClientPlayer forPlayer, BlockSelection blockSelection)
	{
		return api is ICoreClientAPI clientAPI ? TrackManualPlacement.GetToolModes(this, clientAPI) : null;
	}

	public override int GetToolMode(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSelection)
	{
		return TrackManualPlacement.GetToolMode(this, byPlayer);
	}

	public override void SetToolMode(ItemSlot slot, IPlayer byPlayer, BlockSelection blockSelection, int toolMode)
	{
		TrackManualPlacement.SetToolMode(this, byPlayer, toolMode);
	}

	protected override bool TryResolvePlacedBlock(IWorldAccessor world, IPlayer? player, BlockSelection selection, out Block block)
	{
		block = this;
		if (!TrackConfigCompiled) return false;
		if (TrackManualPlacement.TryResolveManualVariant(this, player, out BlockTrackAligned manual)) { block = manual; return true; }

		int facing = TrackAlignmentResolver.FacingIndex(player?.Entity?.Pos?.Yaw ?? 0f);
		Block? selected = NormalVariants[facing];

		if (MirroredVariants != null && TrackAlignmentResolver.ShouldMirror(world, selection, TrackGauge, facing)) { selected = MirroredVariants[facing]; }

		if (selected == null) return false;
		block = selected;
		return true;
	}
}

internal static class TrackAlignmentResolver
{
	internal static readonly string[] Rotations = { "ne", "es", "sw", "wn" };

	internal static int FacingIndex(float yaw)
	{
		BlockFacing facing = BlockFacing.HorizontalFromAngle(yaw);
		if (facing == BlockFacing.EAST) return 1;
		if (facing == BlockFacing.SOUTH) return 2;
		return facing == BlockFacing.WEST ? 3 : 0;
	}

	internal static bool ShouldMirror(IWorldAccessor world, BlockSelection selection, byte gauge, int facing)
	{
		TrackSpecsDictionary.GetMaxSpecReach(out int radius, out _);
		int hitBaseX = selection.Position.X;
		int hitBaseY = selection.Position.Y;
		int hitBaseZ = selection.Position.Z;
		if (selection.DidOffset)
		{
			Vec3i offset = selection.Face.Opposite.Normali;
			hitBaseX += offset.X;
			hitBaseY += offset.Y;
			hitBaseZ += offset.Z;
		}

		double hitX = hitBaseX + selection.HitPosition.X;
		double hitY = hitBaseY + selection.HitPosition.Y;
		double hitZ = hitBaseZ + selection.HitPosition.Z;
		double bestDistance = double.MaxValue;
		double bestX = 0, bestZ = 0, tangentX = 0, tangentZ = 0;
		bool found = false;

		// Mirroring is only meaningful when an existing TrackSpec endpoint actually overlaps one of the two authored entry nodes on the selected connection face.
		// Use the same 1/16-block quantization as the rail graph for endpoint identity.
		int entryMinX16 = selection.Position.X << 4;
		int entryMaxX16 = entryMinX16 + 16;
		int entryY16 = (selection.Position.Y << 4) + (int)Math.Round(TrackSpecsDictionary.DefaultY * 16.0);
		int entryMinZ16 = selection.Position.Z << 4;
		int entryMaxZ16 = entryMinZ16 + 16;

		BlockPos probe = new(selection.Position.dimension);
		SearchLayer(selection.Position.Y);

		// A slope (or sub-mod equivalent) may be anchored one block above or below while exposing its endpoint at the placement layer.
		// Only search those owner layers if the normal layer did not provide an actual overlapping connection endpoint.
		if (!found)
		{
			SearchLayer(selection.Position.Y - 1);
			SearchLayer(selection.Position.Y + 1);
		}

		if (!found) return false;
		double offsetX = selection.Position.X + 0.5 - bestX;
		double offsetZ = selection.Position.Z + 0.5 - bestZ;
		return tangentX * offsetZ - tangentZ * offsetX > 0;

		void SearchLayer(int anchorY)
		{
			for (int dx = -radius; dx <= radius; dx++)
			{
				for (int dz = -radius; dz <= radius; dz++)
				{
					probe.Set(selection.Position.X + dx, anchorY, selection.Position.Z + dz);
					Block candidate = world.BlockAccessor.GetBlock(probe);
					if (candidate is not BlockTrackBase track || track.TrackGauge != gauge || !TrackSpecsDictionary.TryGet(candidate, out TrackPieceSpec trackSpecification)) continue;

					foreach (TrackPath path in trackSpecification.Paths)
					{
						Vec3f[] points = path.LocalPoints;
						if (points.Length < 2) continue;
						Consider(probe, points[0], points[1]);
						Consider(probe, points[^1], points[^2]);
					}
				}
			}
		}

		void Consider(BlockPos anchor, Vec3f endpoint, Vec3f inside)
		{
			double nodeX = anchor.X + endpoint.X;
			double nodeY = anchor.Y + endpoint.Y;
			double nodeZ = anchor.Z + endpoint.Z;

			int nodeX16 = (int)Math.Round(nodeX * 16.0);
			int nodeY16 = (int)Math.Round(nodeY * 16.0);
			int nodeZ16 = (int)Math.Round(nodeZ * 16.0);
			if (nodeY16 != entryY16 || !OverlapsEntryFace(nodeX16, nodeZ16)) return;

			double dx = nodeX - hitX, dy = nodeY - hitY, dz = nodeZ - hitZ;
			double distance = dx * dx + dy * dy + dz * dz;
			if (distance >= bestDistance) return;

			double tx = inside.X - endpoint.X;
			double tz = inside.Z - endpoint.Z;
			if (tx == 0 && tz == 0) return;

			bestDistance = distance;
			bestX = nodeX;
			bestZ = nodeZ;
			tangentX = tx;
			tangentZ = tz;
			found = true;
		}

		bool OverlapsEntryFace(int nodeX16, int nodeZ16)
		{
			return facing switch
			{
				0 => nodeX16 == entryMinX16 && (nodeZ16 == entryMinZ16 || nodeZ16 == entryMaxZ16), // ne: west face
				1 => nodeZ16 == entryMinZ16 && (nodeX16 == entryMinX16 || nodeX16 == entryMaxX16), // es: north face
				2 => nodeX16 == entryMaxX16 && (nodeZ16 == entryMinZ16 || nodeZ16 == entryMaxZ16), // sw: east face
				3 => nodeZ16 == entryMaxZ16 && (nodeX16 == entryMinX16 || nodeX16 == entryMaxX16), // wn: south face
				_ => false
			};
		}
	}
}

internal enum DynamicTrackCategory : byte
{
	Straight,
	Turn,
	Slope,
	ThreeWay,
	Crossing
}

/// Single-block cardinal track whose cat/rot variant follows same-gauge neighbors.
public sealed class BlockTrackDynamic : BlockTrackBase
{
	private readonly Block?[,] Variants = new Block?[5, 4];

	internal void CompileVariants(ICoreAPI coreAPI)
	{
		Compile(DynamicTrackCategory.Straight, 0);
		Compile(DynamicTrackCategory.Straight, 1);
		for (int rotation = 0; rotation < 4; rotation++)
		{
			Compile(DynamicTrackCategory.Turn, rotation);
			Compile(DynamicTrackCategory.Slope, rotation);
			Compile(DynamicTrackCategory.ThreeWay, rotation);
		}
		Compile(DynamicTrackCategory.Crossing, 1);

		void Compile(DynamicTrackCategory category, int rotation)
		{
			Variants[(int)category, rotation] = TrackBlockCompiler.ResolveDynamicSibling
			(
				coreAPI, this, DynamicTrackAutoUpdater.CategoryCode(category), DynamicTrackAutoUpdater.RotationCode(rotation)
			);
		}
	}

	internal Block? GetVariant(DynamicTrackCategory category, int rotation) { return (uint)rotation < 4 ? Variants[(int)category, rotation] : null; }

	protected override bool TryResolvePlacedBlock(IWorldAccessor world, IPlayer? player, BlockSelection selection, out Block block)
	{
		block = this;
		if (!TrackConfigCompiled) return false;

		BlockFacing hint = BlockFacing.HorizontalFromAngle(player?.Entity?.Pos?.Yaw ?? 0f);
		(DynamicTrackCategory category, int rotation) = DynamicTrackAutoUpdater.ComputeDesiredVariant(world.BlockAccessor, selection.Position, TrackGauge, hint);

		Block? selected = GetVariant(category, rotation);
		if (selected == null) return false;
		block = selected;
		return true;
	}
}

internal static class DynamicTrackAutoUpdater
{
	private static readonly BlockFacing[] Horizontals = BlockFacing.HORIZONTALS;

	internal static void UpdateAround(IWorldAccessor world, BlockPos center, byte gauge)
	{
		if (world.Side != EnumAppSide.Server) return;

		IBlockAccessor blockAccessor = world.BlockAccessor;
		Queue<BlockPos> queue = new();
		HashSet<BlockPos> queued = new();
		List<RailGraphServerSystem.RailBlockReplacement> replacements = new();
		RailGraphServerSystem? railGraph = world.Api.ModLoader.GetModSystem<RailGraphServerSystem>();

		void Enqueue(BlockPos position)
		{
			BlockPos copy = Copy(position);
			if (queued.Add(copy)) queue.Enqueue(copy);
		}

		void EnqueueInfluence(BlockPos position)
		{
			Enqueue(position);
			foreach (BlockFacing facing in Horizontals)
			{
				int x = position.X + facing.Normali.X;
				int z = position.Z + facing.Normali.Z;
				Enqueue(NewPosition(position.dimension, x, position.Y, z));
				Enqueue(NewPosition(position.dimension, x, position.Y + 1, z));
				Enqueue(NewPosition(position.dimension, x, position.Y - 1, z));
			}
		}

		EnqueueInfluence(center);
		int steps = 0;

		while (queue.Count > 0 && steps++ < 256)
		{
			BlockPos position = queue.Dequeue();
			queued.Remove(position);

			Block current = blockAccessor.GetBlock(position);
			if (current is not BlockTrackDynamic dynamicTrack || !dynamicTrack.TrackConfigCompiled || dynamicTrack.TrackGauge != gauge) continue;

			int currentRotation = RotationIndex(current.Variant?["rot"]);
			(DynamicTrackCategory category, int rotation) = ComputeDesiredVariant(
				blockAccessor, position, gauge, RotationFacing(currentRotation)
			);

			Block? desired = dynamicTrack.GetVariant(category, rotation);
			if (desired == null || desired.BlockId == current.BlockId) continue;

			railGraph?.MarkRailOwnerRecoverySuspect(position);
			blockAccessor.ExchangeBlock(desired.BlockId, position);
			blockAccessor.MarkBlockDirty(position);
			replacements.Add(new RailGraphServerSystem.RailBlockReplacement(current, desired, Copy(position)));
			EnqueueInfluence(position);
		}

		if (queue.Count > 0)
		{
			world.Logger.Warning ("[YangTransport] Dynamic track update at {0}/{1}/{2} gauge {3} reached its safety cap.", center.X, center.Y, center.Z, gauge);
		}
		if (replacements.Count > 0) { railGraph?.NotifyBlocksReplaced(replacements); }
	}

	internal static (DynamicTrackCategory Category, int Rotation) ComputeDesiredVariant(IBlockAccessor blockAccessor, BlockPos position, byte gauge, BlockFacing hint)
	{
		bool north = HasConnection(blockAccessor, position, BlockFacing.NORTH, gauge);
		bool east = HasConnection(blockAccessor, position, BlockFacing.EAST, gauge);
		bool south = HasConnection(blockAccessor, position, BlockFacing.SOUTH, gauge);
		bool west = HasConnection(blockAccessor, position, BlockFacing.WEST, gauge);

		BlockFacing? uphill = FindUphill(blockAccessor, position, hint, gauge);
		if (uphill != null) return (DynamicTrackCategory.Slope, FacingRotation(uphill));

		int count = (north ? 1 : 0) + (east ? 1 : 0) + (south ? 1 : 0) + (west ? 1 : 0);
		if (count >= 4) return (DynamicTrackCategory.Crossing, 1);
		if (count == 3)
		{
			if (!north) return (DynamicTrackCategory.ThreeWay, 1);
			if (!east) return (DynamicTrackCategory.ThreeWay, 2);
			if (!south) return (DynamicTrackCategory.ThreeWay, 3);
			return (DynamicTrackCategory.ThreeWay, 0);
		}

		if (count == 2)
		{
			if (north && south) return (DynamicTrackCategory.Straight, 0);
			if (east && west) return (DynamicTrackCategory.Straight, 1);
			if (north && east) return (DynamicTrackCategory.Turn, 0);
			if (east && south) return (DynamicTrackCategory.Turn, 1);
			if (south && west) return (DynamicTrackCategory.Turn, 2);
			return (DynamicTrackCategory.Turn, 3);
		}

		if (count == 1) return (DynamicTrackCategory.Straight, north || south ? 0 : 1);
		return (DynamicTrackCategory.Straight, hint == BlockFacing.EAST || hint == BlockFacing.WEST ? 1 : 0);
	}

	internal static string CategoryCode(DynamicTrackCategory category) => category switch
	{
		DynamicTrackCategory.Straight => "straight",
		DynamicTrackCategory.Turn => "turn",
		DynamicTrackCategory.Slope => "slope",
		DynamicTrackCategory.ThreeWay => "tw",
		DynamicTrackCategory.Crossing => "crossing",
		_ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
	};

	internal static string RotationCode(int rotation) => rotation switch
	{
		0 => "ne",
		1 => "es",
		2 => "sw",
		3 => "wn",
		_ => throw new ArgumentOutOfRangeException(nameof(rotation), rotation, null)
	};

	private static bool HasConnection(IBlockAccessor blockAccessor, BlockPos position, BlockFacing facing, byte gauge)
	{
		int x = position.X + facing.Normali.X;
		int z = position.Z + facing.Normali.Z;
		BlockPos probe = new(position.dimension);

		probe.Set(x, position.Y, z);		if (IsCompatible(blockAccessor.GetBlock(probe), gauge)) return true;
		probe.Set(x, position.Y + 1, z);	if (IsCompatible(blockAccessor.GetBlock(probe), gauge)) return true;
		probe.Set(x, position.Y - 1, z);	return IsCompatible(blockAccessor.GetBlock(probe), gauge);
	}

	private static BlockFacing? FindUphill(IBlockAccessor blockAccessor, BlockPos position, BlockFacing hint, byte gauge)
	{
		if (HasDiagonalUp(blockAccessor, position, hint, gauge)) return hint;
		if (HasDiagonalUp(blockAccessor, position, BlockFacing.NORTH, gauge)) return BlockFacing.NORTH;
		if (HasDiagonalUp(blockAccessor, position, BlockFacing.EAST, gauge)) return BlockFacing.EAST;
		if (HasDiagonalUp(blockAccessor, position, BlockFacing.SOUTH, gauge)) return BlockFacing.SOUTH;
		if (HasDiagonalUp(blockAccessor, position, BlockFacing.WEST, gauge)) return BlockFacing.WEST;
		return null;
	}

	private static bool HasDiagonalUp(IBlockAccessor blockAccessor, BlockPos position, BlockFacing facing, byte gauge)
	{
		return IsCompatible(blockAccessor.GetBlock(NewPosition(position.dimension, position.X + facing.Normali.X, position.Y + 1, position.Z + facing.Normali.Z)), gauge);
	}

	private static bool IsCompatible(Block block, byte gauge)
	{
		return block is BlockTrackBase track && track.TrackConfigCompiled && track.TrackGauge == gauge;
	}

	private static int FacingRotation(BlockFacing facing)
	{
		if (facing == BlockFacing.EAST) return 1;
		if (facing == BlockFacing.SOUTH) return 2;
		return facing == BlockFacing.WEST ? 3 : 0;
	}

	private static int RotationIndex(string? rotation) => rotation switch { "es" => 1, "sw" => 2, "wn" => 3, _ => 0 };
	private static BlockFacing RotationFacing(int rotation) => rotation switch
	{
		1 => BlockFacing.EAST,
		2 => BlockFacing.SOUTH,
		3 => BlockFacing.WEST,
		_ => BlockFacing.NORTH
	};

	private static BlockPos Copy(BlockPos position) => NewPosition(position.dimension, position.X, position.Y, position.Z);
	private static BlockPos NewPosition(int dimension, int x, int y, int z) => new(dimension) { X = x, Y = y, Z = z };
}
