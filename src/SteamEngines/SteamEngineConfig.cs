using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace YangTransport;

public readonly struct SteamEngineConfig
{
	public static readonly double DefaultSpeedLimitBlocksPerSecond = double.PositiveInfinity;
	public const double DefaultAccelerationLimitBlocksPerSecondSquared = 50.0;

	public readonly double MaxTemperatureCelsius;
	public readonly double CapacityLitres;
	public readonly double WorkingFluidConsumptionLitresPerHour;
	public readonly double RawPowerNewtonsPer100Celsius;
	public readonly double AccelerationNewtonsPer100Celsius;
	public readonly double AccelerationBlocksPerSecondSquaredPerHeatUnit;
	public readonly double SpeedLimitBlocksPerSecond;
	public readonly double AccelerationLimitBlocksPerSecondSquared;

	public SteamEngineConfig
	(
		double maxTemperatureCelsius,
		double capacityLitres,
		double workingFluidConsumptionLitresPerHour,
		double rawPowerNewtonsPer100Celsius,
		double accelerationNewtonsPer100Celsius,
		double speedLimitBlocksPerSecond,
		double accelerationLimitBlocksPerSecondSquared
	)
	{
		MaxTemperatureCelsius								= maxTemperatureCelsius;
		CapacityLitres										= capacityLitres;
		WorkingFluidConsumptionLitresPerHour				= workingFluidConsumptionLitresPerHour;
		RawPowerNewtonsPer100Celsius						= rawPowerNewtonsPer100Celsius;
		AccelerationNewtonsPer100Celsius					= accelerationNewtonsPer100Celsius;
		AccelerationBlocksPerSecondSquaredPerHeatUnit		= accelerationNewtonsPer100Celsius * 0.1;
		SpeedLimitBlocksPerSecond							= NormalizeSpeedLimit(speedLimitBlocksPerSecond);
		AccelerationLimitBlocksPerSecondSquared				= NormalizeAccelerationLimit(accelerationLimitBlocksPerSecondSquared);
	}

	public static SteamEngineConfig FromBlock(Block block)
	{
		double maxTemperatureCelsius = 800;
		double capacityLitres = 20;
		double workingFluidConsumptionLitresPerHour = 4;
		double rawPowerNewtonsPer100Celsius = 1;
		double accelerationNewtonsPer100Celsius = 1;
		double speedLimitBlocksPerSecond = DefaultSpeedLimitBlocksPerSecond;
		double accelerationLimitBlocksPerSecondSquared = DefaultAccelerationLimitBlocksPerSecondSquared;

		if (block is BlockLiquidContainerBase liquidContainerBlock) { capacityLitres = liquidContainerBlock.CapacityLitres; }

		JsonObject? steamEngineAttributes = block?.Attributes?["SteamEngine"];
		if (steamEngineAttributes != null && steamEngineAttributes.Exists)
		{
			maxTemperatureCelsius						= steamEngineAttributes["MaxTemperatureC"].AsDouble(maxTemperatureCelsius);
			workingFluidConsumptionLitresPerHour		= steamEngineAttributes["WaterConsumptionRate"].AsDouble(workingFluidConsumptionLitresPerHour);
			rawPowerNewtonsPer100Celsius				= steamEngineAttributes["RawPowerNPer100C"].AsDouble(rawPowerNewtonsPer100Celsius);
			accelerationNewtonsPer100Celsius			= steamEngineAttributes["AccelerationNPer100C"].AsDouble(accelerationNewtonsPer100Celsius);
			speedLimitBlocksPerSecond					= steamEngineAttributes["SpeedLimitBPS"].AsDouble(speedLimitBlocksPerSecond);
			accelerationLimitBlocksPerSecondSquared		= steamEngineAttributes["AccelerationLimitSquaredBPS"].AsDouble(accelerationLimitBlocksPerSecondSquared);
		}

		return new SteamEngineConfig(maxTemperatureCelsius, capacityLitres, workingFluidConsumptionLitresPerHour, rawPowerNewtonsPer100Celsius, accelerationNewtonsPer100Celsius, speedLimitBlocksPerSecond, accelerationLimitBlocksPerSecondSquared);
	}

	public static SteamEngineConfig FromJson(JsonObject? attributes)
	{
		// Same defaults as FromBlock
		double maxTemperatureCelsius = 800;
		double capacityLitres = 20;
		double workingFluidConsumptionLitresPerHour = 4;
		double rawPowerNewtonsPer100Celsius = 1;
		double accelerationNewtonsPer100Celsius = 1;
		double speedLimitBlocksPerSecond = DefaultSpeedLimitBlocksPerSecond;
		double accelerationLimitBlocksPerSecondSquared = DefaultAccelerationLimitBlocksPerSecondSquared;

		if (attributes != null && attributes.Exists)
		{
			capacityLitres = attributes["capacityLitres"].AsDouble(capacityLitres);

			JsonObject steamEngineAttributes = attributes["SteamEngine"];
			if (steamEngineAttributes != null && steamEngineAttributes.Exists)
			{
				maxTemperatureCelsius						= steamEngineAttributes["MaxTemperatureC"].AsDouble(maxTemperatureCelsius);
				workingFluidConsumptionLitresPerHour		= steamEngineAttributes["WaterConsumptionRate"].AsDouble(workingFluidConsumptionLitresPerHour);
				rawPowerNewtonsPer100Celsius				= steamEngineAttributes["RawPowerNPer100C"].AsDouble(rawPowerNewtonsPer100Celsius);
				accelerationNewtonsPer100Celsius			= steamEngineAttributes["AccelerationNPer100C"].AsDouble(accelerationNewtonsPer100Celsius);
				speedLimitBlocksPerSecond					= steamEngineAttributes["SpeedLimitBPS"].AsDouble(speedLimitBlocksPerSecond);
				accelerationLimitBlocksPerSecondSquared		= steamEngineAttributes["AccelerationLimitSquaredBPS"].AsDouble(accelerationLimitBlocksPerSecondSquared);
			}
		}

		return new SteamEngineConfig(maxTemperatureCelsius, capacityLitres, workingFluidConsumptionLitresPerHour, rawPowerNewtonsPer100Celsius, accelerationNewtonsPer100Celsius, speedLimitBlocksPerSecond, accelerationLimitBlocksPerSecondSquared);
	}

	private static double NormalizeSpeedLimit(double value)
	{
		return value > 0 && !double.IsNaN(value) ? value : DefaultSpeedLimitBlocksPerSecond;
	}

	private static double NormalizeAccelerationLimit(double value)
	{
		if (value <= 0 || double.IsNaN(value)) return DefaultAccelerationLimitBlocksPerSecondSquared;
		return Math.Min(value, DefaultAccelerationLimitBlocksPerSecondSquared);
	}
}
