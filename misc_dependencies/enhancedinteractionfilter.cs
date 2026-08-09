#nullable enable

using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace YttEnhancedInteractionFiltering;

public sealed class YttEnhancedInteractionFilteringSystem : ModSystem
{
	private const string HarmonyId = "yttenhancedinteractionfiltering";
	private const double SupplementSearchRadius = 16.0;
	private const string TargetEntityClass = "yangtransport.sglocomotive";

	private static YttEnhancedInteractionFilteringSystem? instance;

	private Harmony? harmony;
	private object? clientGameMain;
	private EntityPartitioning? partitioning;
	private AABBIntersectionTest? clientIntersectionTester;

	private IWorldIntersectionSupplier? cachedInnerSupplier;
	private SupplementingIntersectionSupplier? cachedWrapper;

	internal static YttEnhancedInteractionFilteringSystem? Instance => instance;

	public override bool ShouldLoad(EnumAppSide forSide) { return forSide == EnumAppSide.Client; }

	public override void StartClientSide(ICoreClientAPI api)
	{
		clientGameMain = api.World;
		partitioning = api.ModLoader.GetModSystem<EntityPartitioning>();

		if (partitioning == null)
		{
			api.Logger.Error("[yttenhancedinteractionfiltering] Vanilla EntityPartitioning can't be found. Aborting.");
			return;
		}

		Type? gameMainType = AccessTools.TypeByName("Vintagestory.Common.GameMain");
		if (gameMainType == null)
		{
			api.Logger.Error("[yttenhancedinteractionfiltering] The game does not exist somehow. Aborting.");
			return;
		}

		clientIntersectionTester = AccessTools.Property(gameMainType, "InteresectionTester")?.GetValue(clientGameMain) as AABBIntersectionTest;

		if (clientIntersectionTester == null)
		{
			api.Logger.Error("[yttenhancedinteractionfiltering] GameMain.InteresectionTester is missing. Aborting.");
			return;
		}

		var target = AccessTools.Method(
			gameMainType,
			"RayTraceForSelection",
			new[]
			{
				typeof(IWorldIntersectionSupplier),
				typeof(Ray),
				typeof(BlockSelection).MakeByRefType(),
				typeof(EntitySelection).MakeByRefType(),
				typeof(BlockFilter),
				typeof(EntityFilter)
			}
		);

		if (target == null)
		{
			api.Logger.Error("[yttenhancedinteractionfiltering] GameMain.RayTraceForSelection is missing? Aborting.");
			return;
		}

		try
		{
			instance = this;
			harmony = new Harmony(HarmonyId);

			var prefix = new HarmonyMethod(typeof(RayTraceForSelectionPatch), nameof(RayTraceForSelectionPatch.Prefix)) { priority = Priority.Last };
			var postfix = new HarmonyMethod(typeof(RayTraceForSelectionPatch), nameof(RayTraceForSelectionPatch.Postfix));

			harmony.Patch(target, prefix: prefix, postfix: postfix);
		}
		catch (Exception e)
		{
			instance = null;

			harmony?.UnpatchAll(HarmonyId);
			harmony = null;

			api.Logger.Error("[yttenhancedinteractionfiltering] Something went wrong: {0}", e);
		}
	}

	public override void Dispose()
	{
		if (ReferenceEquals(instance, this)) { instance = null; }

		harmony?.UnpatchAll(HarmonyId);
		harmony = null;

		cachedWrapper = null;
		cachedInnerSupplier = null;
		partitioning = null;
		clientIntersectionTester = null;
		clientGameMain = null;

		base.Dispose();
	}

	internal IWorldIntersectionSupplier? WrapClientSupplier(object gameMainInstance, ref IWorldIntersectionSupplier supplier)
	{
		EntityPartitioning? currentPartitioning = partitioning;
		AABBIntersectionTest? currentIntersectionTester = clientIntersectionTester;

		if (currentPartitioning == null || currentIntersectionTester == null || !ReferenceEquals(gameMainInstance, clientGameMain)) { return null; }

		IWorldIntersectionSupplier previousTesterSupplier = currentIntersectionTester.bsTester;

		if (supplier is SupplementingIntersectionSupplier) { return previousTesterSupplier; }

		if (!ReferenceEquals(supplier, cachedInnerSupplier) || cachedWrapper == null)
		{
			cachedInnerSupplier = supplier;
			cachedWrapper = new SupplementingIntersectionSupplier(supplier, currentPartitioning);
		}

		supplier = cachedWrapper;
		return previousTesterSupplier;
	}

	internal void RestoreClientSupplier(IWorldIntersectionSupplier? previousTesterSupplier)
	{
		if (previousTesterSupplier == null) { return; }

		AABBIntersectionTest? currentIntersectionTester = clientIntersectionTester;
		if (currentIntersectionTester != null) { currentIntersectionTester.bsTester = previousTesterSupplier; }
	}

	private sealed class SupplementingIntersectionSupplier : IWorldIntersectionSupplier
	{
		private readonly IWorldIntersectionSupplier inner;
		private readonly EntityPartitioning partitioning;

		private readonly List<Entity> missingEntities = new(2);
		private readonly ActionConsumable<Entity> visitCandidate;

		private Vec3d? queryPosition;
		private float queryHorizontalRangeSq;
		private float queryVerticalRange;
		private ActionConsumable<Entity>? queryMatches;
		private Entity[] queryVanillaEntities = Array.Empty<Entity>();
		private bool supplementing;

		public SupplementingIntersectionSupplier(IWorldIntersectionSupplier inner, EntityPartitioning partitioning)
		{
			this.inner = inner;
			this.partitioning = partitioning;
			visitCandidate = VisitCandidate;
		}

		public Vec3i MapSize => inner.MapSize;

		public IBlockAccessor blockAccessor => inner.blockAccessor;

		public Block GetBlock(BlockPos pos)							{ return inner.GetBlock(pos); }
		public Cuboidf[] GetBlockIntersectionBoxes(BlockPos pos)	{ return inner.GetBlockIntersectionBoxes(pos); }
		public bool IsValidPos(BlockPos pos)						{ return inner.IsValidPos(pos); }

		public Entity[] GetEntitiesAround(Vec3d position, float horRange, float vertRange, ActionConsumable<Entity>? matches = null)
		{
			Entity[] vanillaEntities = inner.GetEntitiesAround(position, horRange, vertRange, matches);

			if (supplementing || horRange >= SupplementSearchRadius || partitioning.Partitions.Count == 0) { return vanillaEntities; }

			supplementing = true;
			missingEntities.Clear();

			queryPosition = position;
			queryHorizontalRangeSq = horRange * horRange;
			queryVerticalRange = vertRange;
			queryMatches = matches;
			queryVanillaEntities = vanillaEntities;

			try
			{
				partitioning.WalkEntities(
					position.X, position.Y, position.Z,
					SupplementSearchRadius,
					visitCandidate,
					null,
					EnumEntitySearchType.Inanimate
				);
			}
			finally
			{
				queryPosition = null;
				queryMatches = null;
				queryVanillaEntities = Array.Empty<Entity>();
				supplementing = false;
			}

			int missingCount = missingEntities.Count;
			if (missingCount == 0) { return vanillaEntities; }

			Entity[] supplemented = new Entity[vanillaEntities.Length + missingCount];
			Array.Copy(vanillaEntities, supplemented, vanillaEntities.Length);
			missingEntities.CopyTo(supplemented, vanillaEntities.Length);
			missingEntities.Clear();

			return supplemented;
		}

		private bool VisitCandidate(Entity entity)
		{
			if (entity.Properties?.Class != TargetEntityClass) 																					{ return true; }
			if (entity.State == EnumEntityState.Despawned) 																						{ return true; }
			if (ContainsEntity(queryVanillaEntities, entity.EntityId)) 																			{ return true; }
			ActionConsumable<Entity>? matches = queryMatches; if (matches != null && !matches(entity)) 											{ return true; }
			Vec3d? position = queryPosition; if (position == null ||!entity.InRangeOf(position, queryHorizontalRangeSq, queryVerticalRange))	{ return true; }

			missingEntities.Add(entity);
			return true;
		}

		private static bool ContainsEntity(Entity[] entities, long entityId)
		{
			for (int i = 0; i < entities.Length; i++) { if (entities[i].EntityId == entityId) { return true; } }
			return false;
		}
	}

	private static class RayTraceForSelectionPatch
	{
		public static void Prefix(object __instance, ref IWorldIntersectionSupplier __0, out IWorldIntersectionSupplier? __state)
		{
			YttEnhancedInteractionFilteringSystem? system = Instance;
			__state = system?.WrapClientSupplier(__instance, ref __0);
		}

		public static void Postfix(IWorldIntersectionSupplier? __state) { Instance?.RestoreClientSupplier(__state); }
	}
}
