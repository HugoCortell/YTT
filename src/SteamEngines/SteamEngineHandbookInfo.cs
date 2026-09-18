using System;
using System.Globalization;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace YangTransport;

// Appends dynamic handbook stats to locomotives, engines, and other stuff.
public static class SteamEngineHandbookInfo
{
	private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

	public static void AppendStationarySteamEngineInfo(StringBuilder description, Block block)
	{
		if (description == null || block == null) return;

		SteamEngineConfig configuration = SteamEngineConfig.FromBlock(block);
		double heatUnits = configuration.MaxTemperatureCelsius / 100.0;

		description.AppendLine(Lang.Get("yangtransport:handbook-steam-max-temperature", Format(configuration.MaxTemperatureCelsius)));
		description.AppendLine(Lang.Get("yangtransport:handbook-steam-fluid-capacity", Format(configuration.CapacityLitres)));
		description.AppendLine(Lang.Get("yangtransport:handbook-steam-water-consumption", Format(configuration.WorkingFluidConsumptionLitresPerHour)));
		description.AppendLine(Lang.Get("yangtransport:handbook-steam-max-power", Format(configuration.RawPowerNewtonsPer100Celsius * heatUnits)));
		description.AppendLine(Lang.Get("yangtransport:handbook-steam-max-speed", Format(configuration.AccelerationNewtonsPer100Celsius * heatUnits)));
	}

	public static void AppendRailVehicleInfo(ItemSlot inSlot, StringBuilder description, IWorldAccessor world, bool includeVehicleLength)
	{
		if (inSlot?.Itemstack?.Collectible?.Code == null || description == null || world == null) return;

		AssetLocation entityCode = inSlot.Itemstack.Collectible.Code;
		EntityProperties? entityType = world.GetEntityType(entityCode);
		JsonObject? entityAttributes = entityType?.Attributes;
		if (entityAttributes == null || !entityAttributes.Exists) return;

		JsonObject? steamEngineRoot = entityAttributes["SteamEngine"];
		bool hasSteamEngine = steamEngineRoot != null && steamEngineRoot.Exists;

		JsonObject? vehicleRoot = GetSGVehicleRoot(entityAttributes);
		bool hasVehicleStats = vehicleRoot != null && vehicleRoot.Exists;

		bool hasSeatStats = TryGetSeatCount(entityType, out int seatCount);
		bool hasStorageStats = TryGetStorageInfo(entityType, out StorageInfo storageInfo);
		bool hasRefrigerationStats = TryGetRefrigerationPerishMultiplier(entityAttributes, entityType, out double perishMultiplier);

		if (!hasSteamEngine && !hasVehicleStats && !IsMinecartCode(entityCode) && !hasSeatStats && !hasStorageStats && !hasRefrigerationStats) return;

		if (hasSteamEngine)
		{
			SteamEngineConfig engineConfiguration = SteamEngineConfig.FromJson(entityAttributes);
			double heatUnits = engineConfiguration.MaxTemperatureCelsius / 100.0;

			description.AppendLine(Lang.Get("yangtransport:handbook-steam-max-temperature", Format(engineConfiguration.MaxTemperatureCelsius)));
			description.AppendLine(Lang.Get("yangtransport:handbook-steam-fluid-capacity", Format(engineConfiguration.CapacityLitres)));
			description.AppendLine(Lang.Get("yangtransport:handbook-steam-water-consumption", Format(engineConfiguration.WorkingFluidConsumptionLitresPerHour)));
			description.AppendLine(Lang.Get("yangtransport:handbook-steam-max-power", Format(engineConfiguration.RawPowerNewtonsPer100Celsius * heatUnits)));
			description.AppendLine(Lang.Get("yangtransport:handbook-steam-max-speed", Format(engineConfiguration.AccelerationBlocksPerSecondSquaredPerHeatUnit * heatUnits)));
		}

		if (hasVehicleStats)
		{
			if (includeVehicleLength)
			{
				double vehicleLength = vehicleRoot!["VehicleLength"].AsDouble(6.0);
				description.AppendLine(Lang.Get("yangtransport:handbook-rail-vehicle-length", Format(vehicleLength)));
			}

			int selfWeight = RailVehicleWeightSpec.FromJson(vehicleRoot, defaultSelfWeight: 5, defaultAttachmentWeight: 0).SelfWeight;
			description.AppendLine(Lang.Get("yangtransport:handbook-rail-self-weight", selfWeight));
		}
		else if (IsMinecartCode(entityCode))
		{
			int selfWeight = RailVehicleWeightSpec.FromJson(entityAttributes["RailVehicle"], defaultSelfWeight: 1, defaultAttachmentWeight: 0).SelfWeight;
			description.AppendLine(Lang.Get("yangtransport:handbook-rail-self-weight", selfWeight));
		}

		if (hasSeatStats)
		{
			description.AppendLine(Lang.Get("yangtransport:handbook-rail-seats", seatCount));
		}

		if (hasStorageStats)
		{
			description.AppendLine(Lang.Get("yangtransport:handbook-rail-storage-slots", storageInfo.PointCount));
			description.AppendLine(Lang.Get("yangtransport:handbook-rail-storage-size", storageInfo.TotalQuantitySlots));
		}

		if (hasRefrigerationStats)
		{
			description.AppendLine(Lang.Get("yangtransport:handbook-rail-perish-speed-multiplier", Format(perishMultiplier)));
		}
	}

	private static JsonObject? GetSGVehicleRoot(JsonObject entityAttributes)
	{
		JsonObject? standardGaugeVehicleRoot = entityAttributes["SGLocomotive"];
		if (standardGaugeVehicleRoot != null && standardGaugeVehicleRoot.Exists) return standardGaugeVehicleRoot;

		return null;
	}

	private static bool TryGetSeatCount(EntityProperties? entityType, out int seatCount)
	{
		seatCount = 0;

		if (TryGetBehavior(entityType, "SGBodySelectionBoxes", out JsonObject? selectionBoxesBehavior))
		{
			seatCount = CountSeatSelectionBoxes(selectionBoxesBehavior!);
			if (seatCount > 0) return true;
		}

		if (TryGetBehavior(entityType, "SGAutoSeats", out JsonObject? automaticSeatsBehavior))
		{
			seatCount = Math.Max(0, automaticSeatsBehavior!["Count"].AsInt(0));
			return seatCount > 0;
		}

		return false;
	}

	private static int CountSeatSelectionBoxes(JsonObject selectionBoxBehavior)
	{
		int seatSelectionBoxCount = 0;

		seatSelectionBoxCount += CountSeatSelectionCodes(selectionBoxBehavior["SelectionBoxes"].AsArray<string>(Array.Empty<string>(), null));

		if (IsSeatPrefix(selectionBoxBehavior["SelectionBoxPrefix"].AsString(null))) { seatSelectionBoxCount += Math.Max(0, selectionBoxBehavior["SelectionBoxCount"].AsInt(0)); }

		JsonObject[] selectionBoxGroups = selectionBoxBehavior["SelectionBoxGroups"].AsArray();
		if (selectionBoxGroups != null)
		{
			for (int groupIndex = 0; groupIndex < selectionBoxGroups.Length; groupIndex++)
			{
				JsonObject selectionBoxGroup = selectionBoxGroups[groupIndex];
				seatSelectionBoxCount += CountSeatSelectionCodes(selectionBoxGroup["SelectionBoxes"].AsArray<string>(Array.Empty<string>(), null));

				if (IsSeatPrefix(selectionBoxGroup["Prefix"].AsString(null))) { seatSelectionBoxCount += Math.Max(0, selectionBoxGroup["Count"].AsInt(0)); }
			}
		}

		return seatSelectionBoxCount;
	}

	private static int CountSeatSelectionCodes(string[]? seatSelectionBoxCodes)
	{
		if (seatSelectionBoxCodes == null || seatSelectionBoxCodes.Length == 0) return 0;

		int seatSelectionBoxCount = 0;
		for (int BoxIndex = 0; BoxIndex < seatSelectionBoxCodes.Length; BoxIndex++) { if (seatSelectionBoxCodes[BoxIndex]?.StartsWith("SEAT_AP_", StringComparison.OrdinalIgnoreCase) == true) seatSelectionBoxCount++; }

		return seatSelectionBoxCount;
	}

	private static bool IsSeatPrefix(string? prefix) { return string.Equals(prefix, "SEAT_AP_", StringComparison.OrdinalIgnoreCase); }

	private static bool TryGetStorageInfo(EntityProperties? entityType, out StorageInfo storage)
	{
		storage = default;

		if (!TryGetBehavior(entityType, "SGStorage", out JsonObject? storageBehavior)) return false;

		int storagePointCount = 0;
		int totalQuantitySlots = 0;

		JsonObject[] explicitStoragePoints = storageBehavior!["Points"].AsArray();
		if (explicitStoragePoints != null)
		{
			for (int pointIndex = 0; pointIndex < explicitStoragePoints.Length; pointIndex++)
			{
				JsonObject storagePoint = explicitStoragePoints[pointIndex];
				string attachmentPointCode = storagePoint["AP"].AsString(null);
				if (string.IsNullOrEmpty(attachmentPointCode)) continue;

				storagePointCount++;
				totalQuantitySlots += Math.Max(1, storagePoint["QuantitySlots"].AsInt(36));
			}
		}

		int[] quantitySlotsByPoint = storageBehavior["QuantitySlotsByPoint"].AsArray<int>(Array.Empty<int>()) ?? Array.Empty<int>();
		if (quantitySlotsByPoint.Length > 0)
		{
			storagePointCount += quantitySlotsByPoint.Length;

			for (int pointIndex = 0; pointIndex < quantitySlotsByPoint.Length; pointIndex++) { totalQuantitySlots += Math.Max(1, quantitySlotsByPoint[pointIndex]); }
		}

		int generatedStoragePointCount = Math.Max(0, storageBehavior["Count"].AsInt(0));
		if (generatedStoragePointCount > 0)
		{
			storagePointCount += generatedStoragePointCount;
			totalQuantitySlots += generatedStoragePointCount * Math.Max(1, storageBehavior["QuantitySlots"].AsInt(36));
		}

		if (storagePointCount <= 0) return false;

		storage = new StorageInfo(storagePointCount, totalQuantitySlots);
		return true;
	}

	private static bool TryGetRefrigerationPerishMultiplier(JsonObject entityAttributes, EntityProperties? entityType, out double perishMultiplier)
	{
		perishMultiplier = 1.0;

		if (!TryGetBehavior(entityType, "SGRefrigeration", out _)) return false;

		JsonObject refrigerationConfiguration = entityAttributes["SGRefrigeration"];
		if (refrigerationConfiguration == null || !refrigerationConfiguration.Exists) return false;

		perishMultiplier = Math.Max(0, refrigerationConfiguration["PerishMultiplier"].AsDouble(0.4));
		return true;
	}

	private static bool TryGetBehavior(EntityProperties? entityType, string behaviorCode, out JsonObject? behavior)
	{
		if (TryGetBehavior(entityType?.Server, behaviorCode, out behavior)) return true;
		if (TryGetBehavior(entityType?.Client, behaviorCode, out behavior)) return true;

		behavior = null;
		return false;
	}

	private static bool TryGetBehavior(EntitySidedProperties? entitySideProperties, string behaviorCode, out JsonObject? behavior)
	{
		JsonObject[]? behaviorConfigurations = entitySideProperties?.BehaviorsAsJsonObj;
		if (behaviorConfigurations != null)
		{
			for (int behaviorIndex = 0; behaviorIndex < behaviorConfigurations.Length; behaviorIndex++)
			{
				JsonObject candidateBehavior = behaviorConfigurations[behaviorIndex];
				if (string.Equals(candidateBehavior["code"].AsString(null), behaviorCode, StringComparison.OrdinalIgnoreCase))
				{
					behavior = candidateBehavior;
					return true;
				}
			}
		}

		behavior = null;
		return false;
	}

	private static bool IsMinecartCode(AssetLocation entityCode) { return entityCode.Path == "minecart" || entityCode.Path.StartsWith("enginecart-", StringComparison.Ordinal); }
	private static string Format(double value) { return value.ToString("0.###", Invariant); }

	private readonly struct StorageInfo
	{
		public readonly int PointCount;
		public readonly int TotalQuantitySlots;

		public StorageInfo(int pointCount, int totalQuantitySlots)
		{
			PointCount = pointCount;
			TotalQuantitySlots = totalQuantitySlots;
		}
	}
}
