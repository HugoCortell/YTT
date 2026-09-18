using System;
using System.IO;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace YangTransport;

public sealed class YangTransportServerConfig
{
	public bool BackupWhenUpdatingSaves						{ get; set; } = true;
	public float EngineFuelConsumptionMultiplier			{ get; set; } = 1;
	public float ContaminatedLiquidHeatingMultiplier		{ get; set; } = 0.25f;
	public float ContaminatedLiquidEvaporationMultiplier	{ get; set; } = 1.2f;
	public float FlammableLiquidHeatingMultiplier			{ get; set; } = 2.5f;
	public float FlammableLiquidEvaporationMultiplier		{ get; set; } = 4f;
}

internal static class YangTransportSettings
{
	internal static readonly string ConfigurationFilename = Path.Combine("yangtransport", "yangtransport.json");

	internal static float EngineFuelConsumptionMultiplier			{ get; private set; } = 1;
	internal static float EngineFuelDurationScale					{ get; private set; } = 1f;
	internal static float ContaminatedLiquidHeatingMultiplier		{ get; private set; } = 0.25f;
	internal static float ContaminatedLiquidEvaporationMultiplier	{ get; private set; } = 1.2f;
	internal static float FlammableLiquidHeatingMultiplier			{ get; private set; } = 2.5f;
	internal static float FlammableLiquidEvaporationMultiplier		{ get; private set; } = 4f;

	internal static void Load(ICoreServerAPI serverAPI)
	{
		YangTransportServerConfig configuration = new();
		bool storeConfiguration = false;

		try
		{
			JsonObject? rawConfiguration = serverAPI.LoadModConfig(ConfigurationFilename);
			if (rawConfiguration != null)
			{
				configuration = rawConfiguration.AsObject<YangTransportServerConfig>() ?? new YangTransportServerConfig();
				storeConfiguration = HasMissingSettings(rawConfiguration);
			}
			else storeConfiguration = true;
		}
		catch (Exception exception)
		{
			serverAPI.Logger.Error("[YangTransport] Failed to load server config '{0}'. Using defaults.", ConfigurationFilename);
			serverAPI.Logger.Error(exception);
		}

		EngineFuelConsumptionMultiplier = ClampSetting
		(
			serverAPI,
			nameof(configuration.EngineFuelConsumptionMultiplier),
			configuration.EngineFuelConsumptionMultiplier,
			0.05f,
			2.5f,
			1f,
			ref storeConfiguration
		);
		configuration.EngineFuelConsumptionMultiplier = EngineFuelConsumptionMultiplier;
		EngineFuelDurationScale = 1f / EngineFuelConsumptionMultiplier;

		ContaminatedLiquidHeatingMultiplier = ClampSetting
		(
			serverAPI,
			nameof(configuration.ContaminatedLiquidHeatingMultiplier),
			configuration.ContaminatedLiquidHeatingMultiplier,
			0.1f,
			1f,
			0.25f,
			ref storeConfiguration
		);
		configuration.ContaminatedLiquidHeatingMultiplier = ContaminatedLiquidHeatingMultiplier;

		ContaminatedLiquidEvaporationMultiplier = ClampSetting
		(
			serverAPI,
			nameof(configuration.ContaminatedLiquidEvaporationMultiplier),
			configuration.ContaminatedLiquidEvaporationMultiplier,
			1f,
			4f,
			1.2f,
			ref storeConfiguration
		);
		configuration.ContaminatedLiquidEvaporationMultiplier = ContaminatedLiquidEvaporationMultiplier;

		FlammableLiquidHeatingMultiplier = ClampSetting
		(
			serverAPI,
			nameof(configuration.FlammableLiquidHeatingMultiplier),
			configuration.FlammableLiquidHeatingMultiplier,
			1f,
			8f,
			2.5f,
			ref storeConfiguration
		);
		configuration.FlammableLiquidHeatingMultiplier = FlammableLiquidHeatingMultiplier;

		FlammableLiquidEvaporationMultiplier = ClampSetting
		(
			serverAPI,
			nameof(configuration.FlammableLiquidEvaporationMultiplier),
			configuration.FlammableLiquidEvaporationMultiplier,
			1f,
			8f,
			4f,
			ref storeConfiguration
		);
		configuration.FlammableLiquidEvaporationMultiplier = FlammableLiquidEvaporationMultiplier;

		if (!storeConfiguration) return;

		try { serverAPI.StoreModConfig(configuration, ConfigurationFilename); }
		catch (Exception exception)
		{
			serverAPI.Logger.Error("[YangTransport] Failed to store server config '{0}'.", ConfigurationFilename);
			serverAPI.Logger.Error(exception);
		}
	}

	private static bool HasMissingSettings(JsonObject rawConfiguration)
	{
		foreach (var property in typeof(YangTransportServerConfig).GetProperties()) { if (!rawConfiguration[property.Name].Exists) return true; }
		return false;
	}

	private static float ClampSetting(ICoreServerAPI serverAPI, string settingName, float configuredValue, float minimum, float maximum, float defaultValue, ref bool storeConfiguration)
	{
		float clampedValue = float.IsFinite(configuredValue) ? Math.Clamp(configuredValue, minimum, maximum) : defaultValue;
		if (configuredValue == clampedValue) return clampedValue;

		serverAPI.Logger.Warning("[YangTransport] {0} was set to {1}, Clamping to {2}.", settingName, configuredValue, clampedValue);
		storeConfiguration = true;
		return clampedValue;
	}
}
