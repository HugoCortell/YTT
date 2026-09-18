using System;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace YangTransport;

internal static class TransportDropTables // Handles item drops from deconstruction as well as destruction
{
	public const string DeconstructDropsTable = "deconstructDrops";
	public const string DestroyDropsTable = "DestroyDrops";

	public static bool HasEntityDropTable(Entity entity, string tableName)
	{
		if (entity == null || string.IsNullOrEmpty(tableName)) return false;
		return TryGetDropTable(entity.Properties?.Attributes, entity.Code, tableName, out _);
	}

	public static int SpawnEntityDrops(Entity entity, string tableName, Vec3d? pos = null, bool randomVelocity = false, bool verticalVelocityOnly = false)
	{
		if (entity?.World == null || string.IsNullOrEmpty(tableName)) return 0;

		Vec3d? spawnPos = pos ?? entity.ServerPos?.XYZ ?? entity.Pos?.XYZ;
		return SpawnDrops(entity.World, entity.Properties?.Attributes, entity.Code, tableName, spawnPos, randomVelocity, verticalVelocityOnly);
	}

	public static int SpawnEntityTypeDrops(IWorldAccessor world, AssetLocation entityCode, string tableName, Vec3d pos, bool randomVelocity = false)
	{
		if (world == null || entityCode == null || pos == null || string.IsNullOrEmpty(tableName)) return 0;

		EntityProperties? entityType = world.GetEntityType(entityCode);
		if (entityType == null) return 0;

		return SpawnDrops(world, entityType.Attributes, entityCode, tableName, pos, randomVelocity, verticalVelocityOnly: false);
	}

	public static bool HasBlockDropTable(Block block, string tableName)
	{
		if (block == null || string.IsNullOrEmpty(tableName)) return false;
		return TryGetDropTable(block.Attributes, block.Code, tableName, out _);
	}

	public static int SpawnBlockDrops(IWorldAccessor world, Block block, BlockPos blockPos, string tableName, bool randomVelocity = true)
	{
		if (world == null || block == null || blockPos == null || string.IsNullOrEmpty(tableName)) return 0;

		Vec3d spawnPos = blockPos.ToVec3d().Add(0.5, 0.5, 0.5);
		return SpawnDrops(world, block.Attributes, block.Code, tableName, spawnPos, randomVelocity, verticalVelocityOnly: false);
	}

	public static int SpawnDrops
	(
		IWorldAccessor world, JsonObject? attributes, AssetLocation? ownerCode,
		string tableName, Vec3d? pos, bool randomVelocity, bool verticalVelocityOnly = false
	)
	{
		if (world == null || pos == null) return 0;
		if (!TryGetDropTable(attributes, ownerCode, tableName, out JsonItemStack[] drops)) return 0;

		int spawnedDropCount = 0;
		string resolutionSource = $"{ownerCode ?? new AssetLocation("unknown", "unknown")} {tableName}";

		for (int dropIndex = 0; dropIndex < drops.Length; dropIndex++)
		{
			JsonItemStack dropSpecification = drops[dropIndex]; if (dropSpecification == null) continue;
			if (!dropSpecification.Resolve(world, resolutionSource, printWarningOnError: true)) continue;
			ItemStack dropStack = dropSpecification.ResolvedItemstack?.Clone(); if (dropStack == null || dropStack.StackSize <= 0) continue;

			world.SpawnItemEntity(dropStack, pos, DropVelocity(world, randomVelocity, verticalVelocityOnly));
			spawnedDropCount++;
		}

		return spawnedDropCount;
	}

	public static bool TryGetDropTable(JsonObject? attributes, AssetLocation? ownerCode, string tableName, out JsonItemStack[] drops)
	{
		drops = Array.Empty<JsonItemStack>();
		if (attributes == null || !attributes.Exists || string.IsNullOrEmpty(tableName)) return false;

		JsonObject directDropTable = attributes[tableName];
		if (TryReadDropArray(directDropTable, out drops)) return true;

		JsonObject dropsByType = attributes[tableName + "ByType"];
		if (TryReadByTypeTable(dropsByType, ownerCode, out drops)) return true;

		// Fallback just in case
		JsonObject attributesByType = attributes["attributesByType"];
		if (TryReadNestedAttributesByType(attributesByType, ownerCode, tableName, out drops)) return true;

		return false;
	}

	private static bool TryReadDropArray(JsonObject? dropArrayToken, out JsonItemStack[] drops)
	{
		drops = Array.Empty<JsonItemStack>(); if (dropArrayToken == null || !dropArrayToken.Exists) return false;
		JsonItemStack[]? parsedDrops = dropArrayToken.AsObject<JsonItemStack[]>(null); if (parsedDrops == null) return false;

		drops = parsedDrops; return true;
	}

	private static bool TryReadByTypeTable(JsonObject? dropsByType, AssetLocation? ownerCode, out JsonItemStack[] drops)
	{
		drops = Array.Empty<JsonItemStack>();
		if (dropsByType == null || !dropsByType.Exists || dropsByType.Token is not JObject obj) return false;

		foreach (JProperty typePatternProperty in obj.Properties())
		{
			if (!PatternMatches(typePatternProperty.Name, ownerCode)) continue;
			if (TryReadDropArray(new JsonObject(typePatternProperty.Value), out drops)) return true;
		}

		return false;
	}

	private static bool TryReadNestedAttributesByType(JsonObject? attributesByType, AssetLocation? ownerCode, string tableName, out JsonItemStack[] drops)
	{
		drops = Array.Empty<JsonItemStack>();
		if (attributesByType == null || !attributesByType.Exists || attributesByType.Token is not JObject obj) return false;

		foreach (JProperty typePatternProperty in obj.Properties())
		{
			if (!PatternMatches(typePatternProperty.Name, ownerCode)) continue;

			JsonObject variantAttributes = new JsonObject(typePatternProperty.Value);
			if (TryReadDropArray(variantAttributes[tableName], out drops)) return true;
			if (TryReadByTypeTable(variantAttributes[tableName + "ByType"], ownerCode, out drops)) return true;
		}

		return false;
	}

	private static bool PatternMatches(string typePattern, AssetLocation? ownerCode)
	{
		if (string.IsNullOrEmpty(typePattern) || ownerCode == null) return false;

		string path = ownerCode.Path ?? string.Empty;
		string shortCode = ownerCode.ToShortString();

		if (string.Equals(typePattern, path, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(typePattern, shortCode, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (typePattern.Length > 1 && typePattern[0] == '@')
		{
			string regex = typePattern.Substring(1);
			return Regex.IsMatch(path, regex, RegexOptions.IgnoreCase) || Regex.IsMatch(shortCode, regex, RegexOptions.IgnoreCase);
		}

		return WildcardUtil.Match(typePattern, path) || WildcardUtil.Match(typePattern, shortCode);
	}

	private static Vec3d? DropVelocity(IWorldAccessor world, bool randomVelocity, bool verticalVelocityOnly)
	{
		if (randomVelocity) return RandomVelocity(world);
		if (verticalVelocityOnly) return VerticalVelocity(world);
		return null;
	}

	private static Vec3d RandomVelocity(IWorldAccessor world)
	{
		Random random = world.Rand;
		return new Vec3d((random.NextDouble() - 0.5) * 0.15, 0.10 + random.NextDouble() * 0.15, (random.NextDouble() - 0.5) * 0.15);
	}

	private static Vec3d VerticalVelocity(IWorldAccessor world)
	{
		Random random = world.Rand;
		return new Vec3d(0, 0.10 + random.NextDouble() * 0.15, 0);
	}
}

// Wrench behavior for deconstructing vehicles. Mirrors vanilla entity deconstruction, but requires wrench main hand + hammer offhand instead because we're cooler.
public sealed class CollectibleBehaviorRailVehicleDeconstructTool : CollectibleBehavior
{
	private const float DeconstructSeconds = 6f;
	private const string DeconstructAnimation = "hammerandchisel";

	public CollectibleBehaviorRailVehicleDeconstructTool(CollectibleObject collectibleObject) : base(collectibleObject) { }

	public override void OnHeldInteractStart
	(
		ItemSlot slot, EntityAgent byEntity, BlockSelection blockSelection, EntitySelection entitySelection,
		bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling
	)
	{
		Entity? targetEntity = entitySelection?.Entity;
		if (!CanTargetBeDeconstructed(targetEntity)) return;

		handHandling = EnumHandHandling.PreventDefault;
		handling = EnumHandling.PreventDefault;

		if (!HasRequiredTools(slot, byEntity)) { NotifyMissingTools(byEntity); return; }

		byEntity.StartAnimation(DeconstructAnimation);
	}

	public override bool OnHeldInteractStep
	(
		float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSelection,
		EntitySelection entitySelection, ref EnumHandling handling
	)
	{
		handling = EnumHandling.PreventDefault;

		if (!CanTargetBeDeconstructed(entitySelection?.Entity) || !HasRequiredTools(slot, byEntity)) { byEntity?.StopAnimation(DeconstructAnimation); return false; }

		if (byEntity.World.Side == EnumAppSide.Server) return true;
		return secondsUsed < DeconstructSeconds;
	}

	public override void OnHeldInteractStop
	(
		float secondsUsed, ItemSlot slot, EntityAgent byEntity,
		BlockSelection blockSelection, EntitySelection entitySelection, ref EnumHandling handling
	) 
	{
		handling = EnumHandling.PreventDefault;
		byEntity?.StopAnimation(DeconstructAnimation);

		if (secondsUsed < DeconstructSeconds) return;

		Entity? target = entitySelection?.Entity;
		if (!CanTargetBeDeconstructed(target)) return;
		if (!HasRequiredTools(slot, byEntity)) { NotifyMissingTools(byEntity); return; }

		if (byEntity.World.Side == EnumAppSide.Server) { ServerTryDeconstructTarget(target, byEntity); }

		base.OnHeldInteractStop(secondsUsed, slot, byEntity, blockSelection, entitySelection, ref handling);
	}

	public override bool OnHeldInteractCancel
	(
		float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSelection,
		EntitySelection entitySelection, EnumItemUseCancelReason cancelReason, ref EnumHandling handled
	)
	{ byEntity?.StopAnimation(DeconstructAnimation); return true; }

	private static bool CanTargetBeDeconstructed(Entity? entity)
	{
		if (entity == null || !entity.Alive) return false;
		if (entity is not EntityMinecart && entity is not EntityStandardGaugeLocomotive) return false;

		return entity.Properties?.Attributes?.IsTrue("deconstructible") == true && TransportDropTables.HasEntityDropTable(entity, TransportDropTables.DeconstructDropsTable);
	}

	private static bool HasRequiredTools(ItemSlot mainHandSlot, EntityAgent byEntity)
	{
		if (mainHandSlot?.Itemstack?.Collectible?.GetTool(mainHandSlot) != EnumTool.Wrench) return false;

		ItemSlot? offhandSlot = byEntity?.LeftHandItemSlot;
		return offhandSlot?.Itemstack?.Collectible?.GetTool(offhandSlot) == EnumTool.Hammer;
	}

	private static bool ServerTryDeconstructTarget(Entity? targetEntity, EntityAgent byEntity)
	{
		if (targetEntity is EntityMinecart minecart) return minecart.ServerTryDeconstruct(byEntity);
		if (targetEntity is EntityStandardGaugeLocomotive SGVehicle) return SGVehicle.ServerTryDeconstruct(byEntity);

		return false;
	}

	private static void NotifyMissingTools(EntityAgent byEntity)
	{
		if (byEntity?.World?.Side != EnumAppSide.Server) return;
		if (byEntity is not EntityPlayer entityPlayer) return;
		if (byEntity.World.PlayerByUid(entityPlayer.PlayerUID) is IServerPlayer serverPlayer)
		{
			serverPlayer.SendIngameError("yangtransport:locomotive-deconstruct-tools");
		}
	}
}
