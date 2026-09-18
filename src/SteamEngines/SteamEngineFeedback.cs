using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.API.Datastructures;

namespace YangTransport;

public interface ISteamEngineFeedbackSource
{
	// Return false when there's nothing worth emitting
	bool TryGetFeedback(out double temperatureC, out int fuelBurnTempC, out byte liquidKind);
	bool TryGetAudioState(out double temperatureC, out bool producingPower);

	// Cheap source-origin cull before resolving the exact SteamFX attachment point
	bool IsWithinAudioPreCull(double playerX, double playerY, double playerZ, double audioRange);

	// Cached vectors
	void GetEmissionBounds(out Vec3d minPos, out Vec3d maxPos);

	SteamFXProfile SteamEffectsProfile { get; }
}

public interface IExtendedSteamParticleRangeSource : ISteamEngineFeedbackSource
{
	bool UseExtendedParticleRange { get; }
	bool IsInExtendedParticleRange(double playerX, double playerY, double playerZ);
}

// Client-side steam engine audiovisual feedback. Audio and particles are independently scheduled.
public sealed class ModSystemSteamEngineFeedback : ModSystem
{
	private ICoreClientAPI ClientAPI;
	private readonly HashSet<ISteamEngineFeedbackSource> DefaultParticleRangeSources = new();
	private readonly HashSet<IExtendedSteamParticleRangeSource> ExtendedParticleRangeSources = new();
	private long ParticleListenerID;
	private long AudioListenerID;

	private readonly Dictionary<ISteamEngineFeedbackSource, EngineAudioState> AudioStates = new();

	private const int ParticleTickMS = 100;         // 10 Hz
	private const int AudioTickMS = 25;             // 40 Hz (needed for audible tempo scaling)

	public const float DefaultParticleMaxDistance = 40f;
	public const float DefaultParticleMaxDistanceSQ = DefaultParticleMaxDistance * DefaultParticleMaxDistance;

	// Audio tuning
	private const float EscapeMinVolume = 0.15f;  // ~ambient/early heat
	private const float EscapeMaxVolume = 0.50f;  // at 150c and above
	private const float EscapeMaxTempC = 150f;
	private const float PistonBaseVolume = 0.4f;
	private const float AudioRange = 32f;
	private const float AudioRangeSquared = AudioRange * AudioRange;
	private const float BasePistonIntervalSec = 0.2f; // 800c, scale=1

	// Piston pitch shaping
	private const float PistonPitchMinAt100C = 0.35f; // 100c to 800c
	private const float PistonPitchMaxAt1500C = 1.5f; // 800c to 1500c

	private static readonly AssetLocation SteamEscapeAsset		= new("yangtransport", "sounds/steam_escape.ogg");
	private static readonly AssetLocation SteamPiston0Asset		= new("yangtransport", "sounds/steam_piston_0.ogg");
	private static readonly AssetLocation SteamPiston1Asset		= new("yangtransport", "sounds/steam_piston_1.ogg");

	public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

	public override void StartClientSide(ICoreClientAPI clientAPI)
	{
		ClientAPI = clientAPI;
		ParticleListenerID = ClientAPI.Event.RegisterGameTickListener(OnParticleTick, ParticleTickMS);
		AudioListenerID = ClientAPI.Event.RegisterGameTickListener(OnAudioTick, AudioTickMS);
	}

	public override void Dispose()
	{
		if (ParticleListenerID != 0) ClientAPI?.Event.UnregisterGameTickListener(ParticleListenerID);
		if (AudioListenerID != 0) ClientAPI?.Event.UnregisterGameTickListener(AudioListenerID);
		DefaultParticleRangeSources.Clear();
		ExtendedParticleRangeSources.Clear();

		foreach (var audioStateEntry in AudioStates) audioStateEntry.Value.Dispose();
		AudioStates.Clear();
	}

	public void Register(ISteamEngineFeedbackSource source)
	{
		if (source == null) return;

		if (source is IExtendedSteamParticleRangeSource extendedRangeSource && extendedRangeSource.UseExtendedParticleRange)
		{
			ExtendedParticleRangeSources.Add(extendedRangeSource);
			return;
		}
		DefaultParticleRangeSources.Add(source);
	}

	public void Unregister(ISteamEngineFeedbackSource source)
	{
		if (source == null) return;

		DefaultParticleRangeSources.Remove(source);
		if (source is IExtendedSteamParticleRangeSource extextendedRangeSourcended) ExtendedParticleRangeSources.Remove(extextendedRangeSourcended);
		DeactivateAudioSource(source);
	}

	private void OnParticleTick(float deltaTime)
	{
		EntityPlayer player = ClientAPI?.World?.Player?.Entity;
		if (player == null) return;

		EntityPos playerPosition = player.Pos;
		double playerX = playerPosition.X;
		double playerY = playerPosition.InternalY;
		double playerZ = playerPosition.Z;

		foreach (var source in DefaultParticleRangeSources)
		{
			// Do not resolve SteamFX/body transforms for an engine that emits no particles.
			if (!source.TryGetFeedback(out double tempC, out int fuelTempC, out byte liquidKind)) continue;

			source.GetEmissionBounds(out Vec3d minPos, out Vec3d maxPos);

			double cx = (minPos.X + maxPos.X) * 0.5;
			double cy = (minPos.Y + maxPos.Y) * 0.5;
			double cz = (minPos.Z + maxPos.Z) * 0.5;
			double dx = playerX - cx, dy = playerY - cy, dz = playerZ - cz;

			if (dx * dx + dy * dy + dz * dz <= DefaultParticleMaxDistanceSQ)
			{
				SteamEngineParticles.Emit(ClientAPI, minPos, maxPos, tempC, fuelTempC, liquidKind, source.SteamEffectsProfile);
			}
		}

		foreach (var source in ExtendedParticleRangeSources)
		{
			if (!source.IsInExtendedParticleRange(playerX, playerY, playerZ)) continue;
			if (!source.TryGetFeedback(out double tempC, out int fuelTempC, out byte liquidKind)) continue;

			source.GetEmissionBounds(out Vec3d minPos, out Vec3d maxPos);
			SteamEngineParticles.Emit(ClientAPI, minPos, maxPos, tempC, fuelTempC, liquidKind, source.SteamEffectsProfile);
		}
	}

	#region Audio Feedback
	private void OnAudioTick(float deltaTime)
	{
		EntityPlayer player = ClientAPI?.World?.Player?.Entity;
		if (player == null) return;

		EntityPos playerPosition = player.Pos;
		double playerX = playerPosition.X;
		double playerY = playerPosition.InternalY;
		double playerZ = playerPosition.Z;
		long nowMS = ClientAPI.World.ElapsedMilliseconds;

		foreach (var source in DefaultParticleRangeSources) TickAudioSource(source, playerX, playerY, playerZ, nowMS);
		foreach (var source in ExtendedParticleRangeSources) TickAudioSource(source, playerX, playerY, playerZ, nowMS);
	}

	private void TickAudioSource(ISteamEngineFeedbackSource source, double playerX, double playerY, double playerZ, long nowMS) 
	{
		// State and a cheap origin-distance test come before the potentially expensive SteamFX/body transform.
		if (!source.TryGetAudioState(out double audioTemperatureC, out bool producingPower) || !source.IsWithinAudioPreCull(playerX, playerY, playerZ, AudioRange))
		{
			DeactivateAudioSource(source);
			return;
		}

		source.GetEmissionBounds(out Vec3d minPos, out Vec3d maxPos);

		double cx = (minPos.X + maxPos.X) * 0.5;
		double cy = (minPos.Y + maxPos.Y) * 0.5;
		double cz = (minPos.Z + maxPos.Z) * 0.5;
		double dx = playerX - cx, dy = playerY - cy, dz = playerZ - cz;

		if (dx * dx + dy * dy + dz * dz > AudioRangeSquared)
		{
			DeactivateAudioSource(source);
			return;
		}

		if (!AudioStates.TryGetValue(source, out EngineAudioState audioState))
		{
			audioState = new EngineAudioState();
			AudioStates[source] = audioState;
		}

		float pitch = ComputePitchScale(audioTemperatureC);
		audioState.Update(ClientAPI, (float)cx, (float)cy, (float)cz, producingPower, pitch, audioTemperatureC, nowMS);
	}

	private void DeactivateAudioSource(ISteamEngineFeedbackSource source)
	{
		if (!AudioStates.Remove(source, out EngineAudioState audioState)) return;
		audioState.Dispose();
	}

	private static float ComputePitchScale(double temperatureC)
	{
		if (temperatureC <= 100.0) return PistonPitchMinAt100C;

		if (temperatureC < 800.0)
		{
			double interpolationFactor = GameMath.Clamp((temperatureC - 100.0) / 700.0, 0.0, 1.0);
			return (float)(PistonPitchMinAt100C + (1.0f - PistonPitchMinAt100C) * interpolationFactor);
		}

		if (temperatureC >= 1500.0) return PistonPitchMaxAt1500C;

		double highTemperatureInterpolationFactor = GameMath.Clamp((temperatureC - 800.0) / 700.0, 0.0, 1.0);
		return (float)(1.0f + (PistonPitchMaxAt1500C - 1.0f) * highTemperatureInterpolationFactor);
	}

	private sealed class EngineAudioState : IDisposable
	{
		private const long EscapeStartDelayMS = 250;
		private const long ProducingContinuityDelayMS = 500;
		private const float ParameterEpsilon = 0.001f;

		private ILoadedSound EscapeLoop;
		private ILoadedSound PistonSound0;
		private ILoadedSound PistonSound1;

		private bool EscapePlaying;
		private bool PlaySecondPistonNext;
		private bool WasProducingLatched;

		private long StandbySinceMS;
		private long ProducingGraceUntilMS;
		private long NextPistonAtMS;
		private long LastPistonAtMS;

		private SoundParameterCache EscapeParameterCache;
		private SoundParameterCache Piston0ParameterCache;
		private SoundParameterCache Piston1ParameterCache;
		private float LastEscapeVolume;
		private bool HasEscapeVolume;

		public void Update(ICoreClientAPI clientAPI, float x, float y, float z, bool producingPower, float pitch, double temperatureC, long nowMS)
		{
			// An active, audible engine always owns its escape source.
			// Piston sources are created together on first power delivery and retained until the engine deactivates/leaves range.
			EnsureEscapeSource(clientAPI, x, y, z, pitch, temperatureC);

			if (producingPower) ProducingGraceUntilMS = nowMS + ProducingContinuityDelayMS;
			bool producingLatched = producingPower || nowMS < ProducingGraceUntilMS;

			if (producingLatched)
			{
				StopEscapePlayback();
				EnsurePistonSources(clientAPI, x, y, z, pitch);

				if (!WasProducingLatched)
				{
					StandbySinceMS = 0;

					double startingIntervalSeconds = BasePistonIntervalSec / Math.Max(0.01f, pitch);
					long startingIntervalMS = Math.Max(1L, (long)Math.Round(startingIntervalSeconds * 1000.0));

					if (NextPistonAtMS <= 0) { NextPistonAtMS = nowMS; }
					else
					{
						long minimumNextPistonAtMS = LastPistonAtMS > 0 ? LastPistonAtMS + startingIntervalMS : nowMS;
						NextPistonAtMS = Math.Max(nowMS, minimumNextPistonAtMS);
					}
				}

				double pistonIntervalSeconds = BasePistonIntervalSec / Math.Max(0.01f, pitch);
				long pistonIntervalMS = Math.Max(1L, (long)Math.Round(pistonIntervalSeconds * 1000.0));

				if (nowMS >= NextPistonAtMS)
				{
					if (PlaySecondPistonNext)	{ PlayPersistentPiston(PistonSound1, ref Piston1ParameterCache, x, y, z, pitch); }
					else			{ PlayPersistentPiston(PistonSound0, ref Piston0ParameterCache, x, y, z, pitch); }

					PlaySecondPistonNext = !PlaySecondPistonNext;
					LastPistonAtMS = nowMS;
					NextPistonAtMS = nowMS + pistonIntervalMS;
				}

				WasProducingLatched = true;
				return;
			}

			if (WasProducingLatched)
			{
				WasProducingLatched = false;
				StandbySinceMS = nowMS;

				// Piston clips are retained, but stop any remaining tail before the escape loop can resume so the two modes never overlap.
				StopPistonPlayback();
			}
			else if (StandbySinceMS == 0) { StandbySinceMS = nowMS; }

			if (nowMS - StandbySinceMS < EscapeStartDelayMS) return;
			UpdateEscapeParameters(x, y, z, pitch, ComputeEscapeVolume(temperatureC));

			if (EscapeLoop != null && !EscapeLoop.IsDisposed && !EscapePlaying)
			{
				EscapeLoop.Start();
				EscapePlaying = true;
			}
		}

		private void EnsureEscapeSource(ICoreClientAPI clientAPI, float x, float y, float z, float pitch, double temperatureC)
		{
			if (EscapeLoop != null && !EscapeLoop.IsDisposed) return;

			float volume = ComputeEscapeVolume(temperatureC);
			EscapeLoop = clientAPI.World.LoadSound(new SoundParams
			{
				Location = SteamEscapeAsset,
				ShouldLoop = true,
				Position = new Vec3f(x, y, z),
				DisposeOnFinish = false,
				Pitch = pitch,
				Range = AudioRange,
				ReferenceDistance = 2f,
				Volume = volume
			});

			EscapePlaying = false;
			EscapeParameterCache.Set(x, y, z, pitch);
			LastEscapeVolume = volume;
			HasEscapeVolume = true;
		}

		private void EnsurePistonSources(ICoreClientAPI clientAPI, float x, float y, float z, float pitch)
		{
			if (PistonSound0 != null && !PistonSound0.IsDisposed && PistonSound1 != null && !PistonSound1.IsDisposed) return;

			DisposePistonSources();

			PistonSound0 = LoadPersistentPiston(clientAPI, SteamPiston0Asset, x, y, z, pitch);
			PistonSound1 = LoadPersistentPiston(clientAPI, SteamPiston1Asset, x, y, z, pitch);

			Piston0ParameterCache.Set(x, y, z, pitch);
			Piston1ParameterCache.Set(x, y, z, pitch);
		}

		private static ILoadedSound LoadPersistentPiston(ICoreClientAPI clientAPI, AssetLocation location, float x, float y, float z, float pitch)
		{
			return clientAPI.World.LoadSound(new SoundParams
			{
				Location = location,
				ShouldLoop = false,
				Position = new Vec3f(x, y, z),
				DisposeOnFinish = false,
				Pitch = pitch,
				Range = AudioRange,
				Volume = PistonBaseVolume
			});
		}

		private static void PlayPersistentPiston(ILoadedSound sound, ref SoundParameterCache cache, float x, float y, float z, float pitch)
		{
			if (sound == null || sound.IsDisposed) return;

			UpdateSpatialPitchParameters(sound, ref cache, x, y, z, pitch);
			sound.Start();
		}

		private void UpdateEscapeParameters(float x, float y, float z, float pitch, float volume)
		{
			if (EscapeLoop == null || EscapeLoop.IsDisposed) return;

			UpdateSpatialPitchParameters(EscapeLoop, ref EscapeParameterCache, x, y, z, pitch);

			if (!HasEscapeVolume || Math.Abs(volume - LastEscapeVolume) > ParameterEpsilon)
			{
				EscapeLoop.SetVolume(volume);
				LastEscapeVolume = volume;
				HasEscapeVolume = true;
			}
		}

		private static void UpdateSpatialPitchParameters(ILoadedSound sound, ref SoundParameterCache cache, float x, float y, float z, float pitch)
		{
			if (!cache.HasPosition || Math.Abs(x - cache.X) > ParameterEpsilon || Math.Abs(y - cache.Y) > ParameterEpsilon || Math.Abs(z - cache.Z) > ParameterEpsilon)
			{
				sound.SetPosition(x, y, z);
				cache.X = x; cache.Y = y; cache.Z = z;
				cache.HasPosition = true;
			}

			if (!cache.HasPitch || Math.Abs(pitch - cache.Pitch) > ParameterEpsilon)
			{
				sound.SetPitch(pitch);
				cache.Pitch = pitch;
				cache.HasPitch = true;
			}
		}

		private static float ComputeEscapeVolume(double temperatureC)
		{
			double interpolationFactor = GameMath.Clamp((temperatureC - 20.0) / (EscapeMaxTempC - 20.0), 0.0, 1.0);
			return EscapeMinVolume + (float)interpolationFactor * (EscapeMaxVolume - EscapeMinVolume);
		}

		private void StopEscapePlayback()
		{
			if (!EscapePlaying || EscapeLoop == null || EscapeLoop.IsDisposed) return;
			EscapeLoop.Stop();
			EscapePlaying = false;
		}

		private void StopPistonPlayback()
		{
			if (PistonSound0 != null && !PistonSound0.IsDisposed) PistonSound0.Stop();
			if (PistonSound1 != null && !PistonSound1.IsDisposed) PistonSound1.Stop();
		}

		private void DisposePistonSources()
		{
			DisposeSound(ref PistonSound0);
			DisposeSound(ref PistonSound1);
			Piston0ParameterCache = default;
			Piston1ParameterCache = default;
		}

		private void DisposeEscapeSource()
		{
			DisposeSound(ref EscapeLoop);
			EscapePlaying = false;
			EscapeParameterCache = default;
			LastEscapeVolume = 0f;
			HasEscapeVolume = false;
		}

		private static void DisposeSound(ref ILoadedSound sound)
		{
			if (sound == null) return;

			if (!sound.IsDisposed)
			{
				sound.Stop();
				sound.Dispose();
			}

			sound = null;
		}

		public void Dispose()
		{
			DisposeEscapeSource();
			DisposePistonSources();

			StandbySinceMS = 0;
			ProducingGraceUntilMS = 0;
			NextPistonAtMS = 0;
			LastPistonAtMS = 0;
			WasProducingLatched = false;
			PlaySecondPistonNext = false;
		}

		private struct SoundParameterCache
		{
			public float X;
			public float Y;
			public float Z;
			public float Pitch;
			public bool HasPosition;
			public bool HasPitch;

			public void Set(float x, float y, float z, float pitch)
			{
				X = x; Y = y; Z = z;
				Pitch = pitch;
				HasPosition = true;
				HasPitch = true;
			}
		}
	}
}
#endregion

#region Particles VFX
public static class SteamEngineParticles
{
	private static readonly Vec3f minVelocity = new();
	private static readonly Vec3f maxVelocity = new();

	private static readonly SimpleParticleProperties SteamParticleProperties = new() // Reused particle template
	{
		MinPos = new Vec3d(),
		ParticleModel = EnumParticleModel.Quad,
		WithTerrainCollision = false, // Note: Not adding terrain collision because it wasn't designed for the numbers I got in mind...

		// Steam should fade out and expand as it rises.
		OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, -90f),
	};

	public static void Emit(ICoreClientAPI clientAPI, Vec3d minPos, Vec3d maxPos, double temperatureC, int fuelBurnTempC, byte liquidKind, SteamFXProfile steamEffectsProfile)
	{
		if (clientAPI == null) return;

		SteamParticleProperties.MinPos.Set(minPos.X, minPos.Y, minPos.Z); // Note: this *has* to be defined.
		SteamParticleProperties.AddPos.Set(maxPos.X - minPos.X, maxPos.Y - minPos.Y, maxPos.Z - minPos.Z);
		float steam01 = (float)temperatureC / 100; if (steam01 < 1f) return;

		// Dynamic Values (steam01 = temperatureC / 100)
		float quantity = RemapClamped // This code is a nightmare and so is keeping track of all the damn variables
		(
			steam01, 
			steamEffectsProfile.QuantityMin_Range, steamEffectsProfile.QuantityMax_Range,
			steamEffectsProfile.QuantityMin_Target, steamEffectsProfile.QuantityMax_Target
		) - 1;

		float life = RemapClamped
		(
			steam01,
			steamEffectsProfile.LifeMin_Range, steamEffectsProfile.LifeMax_Range,
			steamEffectsProfile.LifeMin_Target, steamEffectsProfile.LifeMax_Target
		);

		float sideVelocity = RemapClamped
		(
			steam01,
			steamEffectsProfile.SideMin_Range, steamEffectsProfile.SideMax_Range,
			steamEffectsProfile.SideMin_Target, steamEffectsProfile.SideMax_Target
		);

		float UpwardsMin = RemapClamped
		(
			steam01,
			steamEffectsProfile.UpwardsMin_Range, steamEffectsProfile.UpwardsMax_Range,
			steamEffectsProfile.UpwardsMin_Low_Target, steamEffectsProfile.UpwardsMin_High_Target
		); minVelocity.Set( -sideVelocity, UpwardsMin, -sideVelocity);
		float UpwardsMax = RemapClamped
		(
			steam01,
			steamEffectsProfile.UpwardsMin_Range, steamEffectsProfile.UpwardsMax_Range,
			steamEffectsProfile.UpwardsMax_Low_Target, steamEffectsProfile.UpwardsMax_High_Target
		); maxVelocity.Set( sideVelocity, UpwardsMax,  sideVelocity);
		
		float minSize = RemapClamped
		(
			steam01, 
			steamEffectsProfile.SizeMin_Range, steamEffectsProfile.SizeMax_Range,
			steamEffectsProfile.MinSize_Low_Target, steamEffectsProfile.MinSize_High_Target
		);
		float maxSize = RemapClamped
		(
			steam01,
			steamEffectsProfile.SizeMin_Range, steamEffectsProfile.SizeMax_Range,
			steamEffectsProfile.MaxSize_Low_Target, steamEffectsProfile.MaxSize_High_Target
		);

		// Color rule:
		// 0 = no liquid => black | 1 = water => white | 2 = other/none => gray
		SteamParticleProperties.Color = liquidKind switch
		{
			1 => ColorUtil.ToRgba(80, 255, 255, 255),
			2 => ColorUtil.ToRgba(90, 160, 160, 160),
			_ => ColorUtil.ToRgba(90, 0,   0,   0)
		};

		SteamParticleProperties.WindAffectednes = RemapClamped
		(
			steam01,
			steamEffectsProfile.WindMin_Range, steamEffectsProfile.WindMax_Range,
			steamEffectsProfile.WindMin_Target, steamEffectsProfile.WindMax_Target
		); SteamParticleProperties.WindAffected = true;

		SteamParticleProperties.GravityEffect = steamEffectsProfile.GravityEffect;
		SteamParticleProperties.SizeEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEARINCREASE, steamEffectsProfile.GrowthFactor);
		SteamParticleProperties.MinVelocity.Set(minVelocity.X, minVelocity.Y, minVelocity.Z);
		SteamParticleProperties.AddVelocity.Set(maxVelocity.X - minVelocity.X, maxVelocity.Y - minVelocity.Y, maxVelocity.Z - minVelocity.Z);
		SteamParticleProperties.MinQuantity = 1;
		SteamParticleProperties.AddQuantity = Math.Max(0f, quantity);
		SteamParticleProperties.LifeLength = life;
		SteamParticleProperties.MinSize = minSize;
		SteamParticleProperties.MaxSize = maxSize;
		clientAPI.World.SpawnParticles(SteamParticleProperties);
	}

	public static void EmitVentingBurst(ICoreClientAPI clientAPI, Vec3d minPos, Vec3d maxPos, float amount, byte liquidKind)
	{
		if (clientAPI == null) return;

		// Shape intensity (0..1) just to scale velocity/life/size a bit.
		float intensity = GameMath.Clamp(amount / 60f, 0f, 1f);

		// Very strong upward impulse + wide dispersion.
		float side = 0.25f + 0.90f * intensity;
		float upMin = 0.90f + 1.80f * intensity;
		float upMax = 1.80f + 3.20f * intensity;

		minVelocity.Set(-side, upMin, -side);
		maxVelocity.Set( side, upMax,  side);

		int rgba = liquidKind switch
		{
			1 => ColorUtil.ToRgba(100, 255, 255, 255),
			2 => ColorUtil.ToRgba(140, 160, 160, 160),
			_ => ColorUtil.ToRgba(140, 0,   0,   0)
		};

		float life = 0.55f + 0.75f * intensity;
		float size = 0.12f + 0.10f * intensity;

		clientAPI.World.SpawnParticles
		(
			amount, rgba,
			minPos, maxPos,
			minVelocity, maxVelocity,
			life, 0f, size,
			EnumParticleModel.Quad,
			null
		);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	static float RemapClamped(float raw, float rawMin, float rawMax, float mapMin, float mapMax)
	{
		return mapMin + GameMath.Clamp((raw - rawMin) / (rawMax - rawMin), 0f, 1f) * (mapMax - mapMin);
	}
}
#endregion

#region Steam Locator
public static class SteamFxLocator
{
	private struct CacheEntry
	{
		public bool HasValue;
		public Vec3f LocalPosition;
	}

	private static readonly Dictionary<string, CacheEntry> Cache = new();

	public static bool TryGetLocalSteamFxPos(ICoreClientAPI clientAPI, Block block, out Vec3f localPosition) 
	{
		localPosition = default;
		return block != null && TryGetLocalSteamFxPos(clientAPI, block.Shape, block.Code.ToShortString(), out localPosition);
	}

	public static bool TryGetLocalSteamFxPos(ICoreClientAPI clientAPI, CompositeShape compositeShape, string cacheKey, out Vec3f localPosition)
	{
		localPosition = default;
		if (clientAPI == null || compositeShape?.Base == null || string.IsNullOrEmpty(cacheKey)) return false;

		if (Cache.TryGetValue(cacheKey, out var entry))
		{
			if (!entry.HasValue) return false;
			localPosition = entry.LocalPosition;
			return true;
		}

		if (!TryCompute(clientAPI, compositeShape, out localPosition))
		{
			Cache[cacheKey] = new CacheEntry { HasValue = false };
			return false;
		}

		Cache[cacheKey] = new CacheEntry { HasValue = true, LocalPosition = localPosition };
		return true;
	}

	private static bool TryCompute(ICoreClientAPI clientAPI, CompositeShape compositeShape, out Vec3f localPosition)
	{
		localPosition = default;

		AssetLocation shapePath = compositeShape.Base.CopyWithPathPrefixAndAppendixOnce("shapes/", ".json");
		Shape shape = Shape.TryGet(clientAPI, shapePath);
		if (shape?.Elements == null) return false;

		// Find element local pos in shape space (0..1)
		var root = new Matrixf().Identity();
		if (!TryFindInElements(shape.Elements, root, out Vec3f shapeSpacePosition)) return false;

		// Apply whole-mesh transforms (matches vanilla tessellator order)
		var meshMatrix = new Matrixf().Identity();

		if (Math.Abs(compositeShape.Scale - 1f) > 1e-6f)
		{
			meshMatrix.Translate(0.5f, 0f, 0.5f).Scale(compositeShape.Scale, compositeShape.Scale, compositeShape.Scale).Translate(-0.5f, 0f, -0.5f);
		}

		if (compositeShape.rotateX != 0f || compositeShape.rotateY != 0f || compositeShape.rotateZ != 0f)
		{
			meshMatrix.Translate(0.5f, 0.5f, 0.5f)
				.RotateX(compositeShape.rotateX * GameMath.DEG2RAD)
				.RotateY(compositeShape.rotateY * GameMath.DEG2RAD)
				.RotateZ(compositeShape.rotateZ * GameMath.DEG2RAD)
				.Translate(-0.5f, -0.5f, -0.5f);
		}

		if (compositeShape.offsetX != 0f || compositeShape.offsetY != 0f || compositeShape.offsetZ != 0f)
		{
			meshMatrix.Translate(compositeShape.offsetX, compositeShape.offsetY, compositeShape.offsetZ);
		}

		Vec4f v = new Vec4f(shapeSpacePosition.X, shapeSpacePosition.Y, shapeSpacePosition.Z, 1f);
		Vec4f o = new Vec4f();
		Mat4f.MulWithVec4(meshMatrix.Values, v, o);
		localPosition = new Vec3f(o.X, o.Y, o.Z);
		return true;
	}

	private static bool TryFindInElements(ShapeElement[] elements, Matrixf parentMatrix, out Vec3f shapeSpacePosition) 
	{
		shapeSpacePosition = default;
		if (elements == null) return false;

		for (int elementIndex = 0; elementIndex < elements.Length; elementIndex++)
		{
			ShapeElement element = elements[elementIndex];
			if (element?.From == null || element.To == null || element.From.Length != 3 || element.To.Length != 3) continue;

			var elementMatrix = new Matrixf(parentMatrix.Values);
			ApplyElementTransform(elementMatrix, element);

			// 1) AttachmentPoint code "SteamFX"
			if (element.AttachmentPoints != null)
			{
				for (int attachmentPointIndex = 0; attachmentPointIndex < element.AttachmentPoints.Length; attachmentPointIndex++)
				{
					var attachmentPoint = element.AttachmentPoints[attachmentPointIndex];
					if (attachmentPoint?.Code != null && attachmentPoint.Code.Equals("SteamFX", StringComparison.OrdinalIgnoreCase))
					{
						shapeSpacePosition = TransformPoint(elementMatrix, (float)(attachmentPoint.PosX / 16.0), (float)(attachmentPoint.PosY / 16.0), (float)(attachmentPoint.PosZ / 16.0));
						return true;
					}
				}
			}

			// 2) Element named "SteamFX" => emit from its center
			if (!string.IsNullOrEmpty(element.Name) && element.Name.Equals("SteamFX", StringComparison.OrdinalIgnoreCase))
			{
				float cx = (float)((element.To[0] - element.From[0]) / 32.0);
				float cy = (float)((element.To[1] - element.From[1]) / 32.0);
				float cz = (float)((element.To[2] - element.From[2]) / 32.0);

				shapeSpacePosition = TransformPoint(elementMatrix, cx, cy, cz);
				return true;
			}

			if (element.Children != null && TryFindInElements(element.Children, elementMatrix, out shapeSpacePosition)) return true;
		}

		return false;
	}

	private static void ApplyElementTransform(Matrixf transformMatrix, ShapeElement element)
	{
		double rotationOriginX = 0, rotationOriginY = 0, rotationOriginZ = 0;
		if (element.RotationOrigin != null && element.RotationOrigin.Length == 3)
		{
			rotationOriginX = element.RotationOrigin[0];
			rotationOriginY = element.RotationOrigin[1];
			rotationOriginZ = element.RotationOrigin[2];
			transformMatrix.Translate((float)(rotationOriginX / 16.0), (float)(rotationOriginY / 16.0), (float)(rotationOriginZ / 16.0));
		}

		if (element.RotationX != 0) transformMatrix.RotateX((float)(element.RotationX * GameMath.DEG2RAD));
		if (element.RotationY != 0) transformMatrix.RotateY((float)(element.RotationY * GameMath.DEG2RAD));
		if (element.RotationZ != 0) transformMatrix.RotateZ((float)(element.RotationZ * GameMath.DEG2RAD));

		if (element.ScaleX != 1 || element.ScaleY != 1 || element.ScaleZ != 1)
		{
			transformMatrix.Scale((float)element.ScaleX, (float)element.ScaleY, (float)element.ScaleZ);
		}

		transformMatrix.Translate(
			(float)((element.From[0] - rotationOriginX) / 16.0),
			(float)((element.From[1] - rotationOriginY) / 16.0),
			(float)((element.From[2] - rotationOriginZ) / 16.0)
		);
	}

	private static Vec3f TransformPoint(Matrixf mtransformMatrixt, float x, float y, float z)
	{
		Vec4f v = new Vec4f(x, y, z, 1f);
		Vec4f o = new Vec4f();
		Mat4f.MulWithVec4(mtransformMatrixt.Values, v, o);
		return new Vec3f(o.X, o.Y, o.Z);
	}
}
#endregion

#region Steam FX Profiles
public readonly struct SteamFXProfile // Please, cast away your sight from the horrors of this struct
{
	public readonly float 
	QuantityMin_Range, QuantityMax_Range, QuantityMin_Target, QuantityMax_Target,
	LifeMin_Range, LifeMax_Range, LifeMin_Target, LifeMax_Target,
	UpwardsMin_Range, UpwardsMax_Range, UpwardsMin_Low_Target, UpwardsMin_High_Target, UpwardsMax_Low_Target, UpwardsMax_High_Target,
	SideMin_Range, SideMax_Range, SideMin_Target, SideMax_Target,
	SizeMin_Range, SizeMax_Range, MinSize_Low_Target, MinSize_High_Target, MaxSize_Low_Target, MaxSize_High_Target,
	WindMin_Range, WindMax_Range, WindMin_Target, WindMax_Target,
	GravityEffect, GrowthFactor;

	public SteamFXProfile
	(
		float Default_QuantityMin_Range,		float Default_QuantityMax_Range,
		float Default_QuantityMin_Target,		float Default_QuantityMax_Target,
		float Default_LifeMin_Range,			float Default_LifeMax_Range,
		float Default_LifeMin_Target,			float Default_LifeMax_Target,
		float Default_UpwardsMin_Range,			float Default_UpwardsMax_Range,
		float Default_UpwardsMin_Low_Target,	float Default_UpwardsMin_High_Target,
		float Default_UpwardsMax_Low_Target,	float Default_UpwardsMax_High_Target,
		float Default_SideMin_Range,			float Default_SideMax_Range,
		float Default_SideMin_Target,			float Default_SideMax_Target,
		float Default_SizeMin_Range, 			float Default_SizeMax_Range,
		float Default_MinSize_Low_Target,		float Default_MinSize_High_Target,
		float Default_MaxSize_Low_Target,		float Default_MaxSize_High_Target,
		float Default_WindMin_Range,			float Default_WindMax_Range,
		float Default_WindMin_Target,			float Default_WindMax_Target,
		float Default_GravityEffect,			float Default_GrowthFactor
	)
	{
		QuantityMin_Range = Default_QuantityMin_Range;				QuantityMax_Range = Default_QuantityMax_Range;
		QuantityMin_Target = Default_QuantityMin_Target;			QuantityMax_Target = Default_QuantityMax_Target;
		LifeMin_Range = Default_LifeMin_Range;						LifeMax_Range = Default_LifeMax_Range;
		LifeMin_Target = Default_LifeMin_Target;					LifeMax_Target = Default_LifeMax_Target;
		UpwardsMin_Range = Default_UpwardsMin_Range;				UpwardsMax_Range = Default_UpwardsMax_Range;
		UpwardsMin_Low_Target = Default_UpwardsMin_Low_Target;		UpwardsMin_High_Target = Default_UpwardsMin_High_Target;
		UpwardsMax_Low_Target = Default_UpwardsMax_Low_Target;		UpwardsMax_High_Target = Default_UpwardsMax_High_Target;
		SideMin_Range = Default_SideMin_Range;						SideMax_Range = Default_SideMax_Range;
		SideMin_Target = Default_SideMin_Target;					SideMax_Target = Default_SideMax_Target;
		SizeMin_Range = Default_SizeMin_Range;						SizeMax_Range = Default_SizeMax_Range;
		MinSize_Low_Target = Default_MinSize_Low_Target;			MinSize_High_Target = Default_MinSize_High_Target;
		MaxSize_Low_Target = Default_MaxSize_Low_Target;			MaxSize_High_Target = Default_MaxSize_High_Target;
		WindMin_Range = Default_WindMin_Range;						WindMax_Range = Default_WindMax_Range;
		WindMin_Target = Default_WindMin_Target;					WindMax_Target = Default_WindMax_Target;
		GravityEffect = Default_GravityEffect;						GrowthFactor = Default_GrowthFactor;
	}

	// Default profile that tries to fit every engine as a generic
	public static readonly SteamFXProfile Default = new SteamFXProfile // Remember: none-target are just the steam01 raws
	(
		Default_QuantityMin_Range: 1f,		Default_QuantityMax_Range: 12f,			Default_QuantityMin_Target: 5f,			Default_QuantityMax_Target: 1f,
		Default_LifeMin_Range: 4f,			Default_LifeMax_Range: 12f,				Default_LifeMin_Target: 0.2f,			Default_LifeMax_Target: 2.5f,
		Default_UpwardsMin_Range: 1f,		Default_UpwardsMax_Range: 12f,			Default_UpwardsMin_Low_Target: 0.5f,	Default_UpwardsMin_High_Target: 1f,
		Default_UpwardsMax_Low_Target: 1f,	Default_UpwardsMax_High_Target: 4f,		Default_SizeMin_Range: 8,				Default_SizeMax_Range: 12f,
		Default_SideMin_Range: 1f,			Default_SideMax_Range: 12f,				Default_SideMin_Target: 0f,				Default_SideMax_Target: 1f,
		Default_MinSize_Low_Target: 0.2f,	Default_MinSize_High_Target: 1f,		Default_MaxSize_Low_Target: 0.6f,		Default_MaxSize_High_Target: 2f,
		Default_WindMin_Range: 0f,			Default_WindMax_Range: 12f,				Default_WindMin_Target: 1f,				Default_WindMax_Target: 0f,
		Default_GravityEffect: 0.005f,		Default_GrowthFactor: 0.75f
	);
	[MethodImpl(MethodImplOptions.AggressiveInlining)] private static float SteamFXValueFromJson(JsonObject fx, string key, float def) => fx[key].AsFloat(def);

	// Reads overrides from attributes. Supports either attributes.steamFx or attributes.steamEngine.steamFx.
	public static SteamFXProfile FromAttributes(JsonObject? attributes, SteamFXProfile fallback)
	{
		if (attributes == null || !attributes.Exists) return fallback;

		JsonObject fx = attributes["SteamFX"];
		if (fx == null || !fx.Exists) { JsonObject steamEngineAttributes = attributes["SteamEngine"];
		if (steamEngineAttributes != null && steamEngineAttributes.Exists) { fx = steamEngineAttributes["SteamFX"]; } }
		if (fx == null || !fx.Exists) return fallback;

		return new SteamFXProfile
		(
			Default_QuantityMin_Range: 			SteamFXValueFromJson(fx, "QuantityMin_Range", fallback.QuantityMin_Range),
			Default_QuantityMax_Range: 			SteamFXValueFromJson(fx, "QuantityMax_Range", fallback.QuantityMax_Range),
			Default_QuantityMin_Target: 		SteamFXValueFromJson(fx, "QuantityMin_Target", fallback.QuantityMin_Target),
			Default_QuantityMax_Target: 		SteamFXValueFromJson(fx, "QuantityMax_Target", fallback.QuantityMax_Target),
			Default_LifeMin_Range: 				SteamFXValueFromJson(fx, "LifeMin_Range", fallback.LifeMin_Range),
			Default_LifeMax_Range: 				SteamFXValueFromJson(fx, "LifeMax_Range", fallback.LifeMax_Range),
			Default_LifeMin_Target: 			SteamFXValueFromJson(fx, "LifeMin_Target", fallback.LifeMin_Target),
			Default_LifeMax_Target: 			SteamFXValueFromJson(fx, "LifeMax_Target", fallback.LifeMax_Target),
			Default_UpwardsMin_Range: 			SteamFXValueFromJson(fx, "UpwardsMin_Range", fallback.UpwardsMin_Range),
			Default_UpwardsMax_Range: 			SteamFXValueFromJson(fx, "UpwardsMax_Range", fallback.UpwardsMax_Range),
			Default_UpwardsMin_Low_Target: 		SteamFXValueFromJson(fx, "UpwardsMin_Low_Target", fallback.UpwardsMin_Low_Target),
			Default_UpwardsMin_High_Target: 	SteamFXValueFromJson(fx, "UpwardsMin_High_Target", fallback.UpwardsMin_High_Target),
			Default_UpwardsMax_Low_Target: 		SteamFXValueFromJson(fx, "UpwardsMax_Low_Target", fallback.UpwardsMax_Low_Target),
			Default_UpwardsMax_High_Target: 	SteamFXValueFromJson(fx, "UpwardsMax_High_Target", fallback.UpwardsMax_High_Target),
			Default_SideMin_Range: 				SteamFXValueFromJson(fx, "SideMin_Range", fallback.SideMin_Range),
			Default_SideMax_Range: 				SteamFXValueFromJson(fx, "SideMax_Range", fallback.SideMax_Range),
			Default_SideMin_Target: 			SteamFXValueFromJson(fx, "SideMin_Target", fallback.SideMin_Target),
			Default_SideMax_Target: 			SteamFXValueFromJson(fx, "SideMax_Target", fallback.SideMax_Target),
			Default_SizeMin_Range: 				SteamFXValueFromJson(fx, "SizeMin_Range", fallback.SizeMin_Range),
			Default_SizeMax_Range: 				SteamFXValueFromJson(fx, "SizeMax_Range", fallback.SizeMax_Range),
			Default_MinSize_Low_Target: 		SteamFXValueFromJson(fx, "MinSize_Low_Target", fallback.MinSize_Low_Target),
			Default_MinSize_High_Target: 		SteamFXValueFromJson(fx, "MinSize_High_Target", fallback.MinSize_High_Target),
			Default_MaxSize_Low_Target: 		SteamFXValueFromJson(fx, "MaxSize_Low_Target", fallback.MaxSize_Low_Target),
			Default_MaxSize_High_Target: 		SteamFXValueFromJson(fx, "MaxSize_High_Target", fallback.MaxSize_High_Target),
			Default_GrowthFactor: 				SteamFXValueFromJson(fx, "GrowthFactor", fallback.GrowthFactor),
			Default_WindMin_Range: 				SteamFXValueFromJson(fx, "WindMin_Range", fallback.WindMin_Range),
			Default_WindMax_Range: 				SteamFXValueFromJson(fx, "WindMax_Range", fallback.WindMax_Range),
			Default_WindMin_Target: 			SteamFXValueFromJson(fx, "WindMin_Target", fallback.WindMin_Target),
			Default_WindMax_Target: 			SteamFXValueFromJson(fx, "WindMax_Target", fallback.WindMax_Target),
			Default_GravityEffect: 				SteamFXValueFromJson(fx, "GravityEffect", fallback.GravityEffect)
		);
	}
}
#endregion
