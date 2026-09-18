using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace YangTransport;

public interface IRailLivingCollisionSource
{
	Entity CollisionSourceEntity { get; }
	long CollisionGroupID { get; }
	bool IsMountedOnCollisionSource(EntityAgent agent);
	void ApplyLivingCollisionHit(EntityAgent agent, double hitX, double hitY, double hitZ, double speedABS);
}

/// Batches all train-vs-living sweeps into chunk buckets. Each touched chunk is scanned once, and each agent is tested only against sweeps that intersect that chunk.
internal sealed class RailLivingCollisionSystem : ModSystem
{
	private const int TickMS = 50;
	private const int MaxPooledBuckets = 512;
	private const int MaxPooledBucketCapacity = 128;
	private const double BroadEntityPadding = 2.0;

	private ICoreServerAPI? ServerAPI;
	private long TickListenerID;
	private readonly List<LivingSweep> Sweeps = new(128);
	private readonly Dictionary<ChunkKey, List<int>> SweepsByChunk = new(128);
	private readonly Stack<List<int>> BucketPool = new();
	private readonly HashSet<HitKey> HitsThisPass = new();

	public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

	public override void Start(ICoreAPI coreAPI)
	{
		ServerAPI = coreAPI as ICoreServerAPI;
		if (ServerAPI != null) TickListenerID = ServerAPI.Event.RegisterGameTickListener(OnTick, TickMS);
	}

	public override void Dispose()
	{
		if (TickListenerID != 0) ServerAPI?.Event.UnregisterGameTickListener(TickListenerID);
		TickListenerID = 0;
		ServerAPI = null;
	}

	internal void PublishSweep
	(
		IRailLivingCollisionSource source, double speedABS, double halfWidth, double bottomOffset, double height,
		double ax, double ay, double az, double bx, double by, double bz
	)
	{
		if (source == null || speedABS <= 0 || height <= 0 || halfWidth < 0) return;
		Entity sourceEntity = source.CollisionSourceEntity;
		if (sourceEntity == null || !sourceEntity.Alive) return;

		int sweepIndex = Sweeps.Count;
		Sweeps.Add(new LivingSweep
		{
			Source = source,
			GroupID = source.CollisionGroupID,
			Dimension = sourceEntity.ServerPos.Dimension,
			SpeedABS = speedABS,
			HalfWidth = halfWidth,
			BottomOffset = bottomOffset,
			Height = height,
			Ax = ax, Ay = ay, Az = az,
			Bx = bx, By = by, Bz = bz
		});

		double broadPhasePadding = halfWidth + BroadEntityPadding;
		int chunkSize = GlobalConstants.ChunkSize;
		int minChunkX = FloorDivide((int)Math.Floor(Math.Min(ax, bx) - broadPhasePadding), chunkSize);
		int maxChunkX = FloorDivide((int)Math.Floor(Math.Max(ax, bx) + broadPhasePadding), chunkSize);
		int minChunkZ = FloorDivide((int)Math.Floor(Math.Min(az, bz) - broadPhasePadding), chunkSize);
		int maxChunkZ = FloorDivide((int)Math.Floor(Math.Max(az, bz) + broadPhasePadding), chunkSize);

		double minY = Math.Min(ay, by) + bottomOffset - BroadEntityPadding;
		double maxY = Math.Max(ay, by) + bottomOffset + height + BroadEntityPadding;
		int dimensionOffset = sourceEntity.ServerPos.Dimension * BlockPos.DimensionBoundary;
		int minChunkY = FloorDivide((int)Math.Floor(minY) + dimensionOffset, chunkSize);
		int maxChunkY = FloorDivide((int)Math.Floor(maxY) + dimensionOffset, chunkSize);

		for (int chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
		{
			for (int chunkY = minChunkY; chunkY <= maxChunkY; chunkY++)
			{
				for (int chunkZ = minChunkZ; chunkZ <= maxChunkZ; chunkZ++)
				{
					var key = new ChunkKey(chunkX, chunkY, chunkZ);
					if (!SweepsByChunk.TryGetValue(key, out List<int>? bucket))
					{
						bucket = BucketPool.Count > 0 ? BucketPool.Pop() : new List<int>(4);
						SweepsByChunk[key] = bucket;
					}
					bucket.Add(sweepIndex);
				}
			}
		}
	}

	private void OnTick(float deltaTime)
	{
		if (ServerAPI == null || Sweeps.Count == 0) return;

		try
		{
			HitsThisPass.Clear();
			IBlockAccessor blockAccessor = ServerAPI.World.BlockAccessor;

			foreach (KeyValuePair<ChunkKey, List<int>> pair in SweepsByChunk)
			{
				IWorldChunk chunk = blockAccessor.GetChunk(pair.Key.X, pair.Key.Y, pair.Key.Z);
				if (chunk?.Entities == null) continue;

				List<int> bucket = pair.Value;
				for (int entityIndex = 0; entityIndex < chunk.EntitiesCount; entityIndex++)
				{
					Entity entity = chunk.Entities[entityIndex];
					if (entity is not EntityAgent agent || entity.State == EnumEntityState.Despawned) continue;

					for (int bucketIndex = 0; bucketIndex < bucket.Count; bucketIndex++)
					{
						LivingSweep sweep = Sweeps[bucket[bucketIndex]];
						if (agent.ServerPos.Dimension != sweep.Dimension) continue;
						if (!sweep.Source.CollisionSourceEntity.Alive) continue;

						var hitKey = new HitKey(sweep.GroupID, agent.EntityId);
						if (HitsThisPass.Contains(hitKey)) continue;
						if (sweep.Source.IsMountedOnCollisionSource(agent)) continue;
						if (!TryIntersect(in sweep, agent, out double hitX, out double hitY, out double hitZ)) continue;

						HitsThisPass.Add(hitKey);
						sweep.Source.ApplyLivingCollisionHit(agent, hitX, hitY, hitZ, sweep.SpeedABS);
					}
				}
			}
		}
		finally { ClearPending(); }
	}

	private static bool TryIntersect(in LivingSweep sweep, EntityAgent agent, out double hitX, out double hitY, out double hitZ)
	{
		hitX = hitY = hitZ = 0;

		Cuboidf collisionBox = agent.OriginCollisionBox;
		double ex = agent.ServerPos.X;
		double ey = agent.ServerPos.Y;
		double ez = agent.ServerPos.Z;
		double entityMinY = ey + collisionBox.Y1;
		double entityMaxY = ey + collisionBox.Y2;
		double entityRadius = Math.Max(collisionBox.X2 - collisionBox.X1, collisionBox.Z2 - collisionBox.Z1) * 0.5;
		double radius = sweep.HalfWidth + entityRadius;

		double distanceSQ = RailwayVehicleShared.SquaredDistancePointToSegmentXZ(ex, ez, sweep.Ax, sweep.Az, sweep.Bx, sweep.Bz, out double t);
		if (distanceSQ > radius * radius) return false;

		hitY = sweep.Ay + (sweep.By - sweep.Ay) * t;
		if (entityMaxY < hitY + sweep.BottomOffset || entityMinY > hitY + sweep.BottomOffset + sweep.Height) return false;

		hitX = sweep.Ax + (sweep.Bx - sweep.Ax) * t;
		hitZ = sweep.Az + (sweep.Bz - sweep.Az) * t;
		return true;
	}

	private void ClearPending()
	{
		Sweeps.Clear();
		foreach (List<int> bucket in SweepsByChunk.Values)
		{
			if (bucket.Capacity <= MaxPooledBucketCapacity && BucketPool.Count < MaxPooledBuckets)
			{
				bucket.Clear();
				BucketPool.Push(bucket);
			}
		}
		SweepsByChunk.Clear();
		HitsThisPass.Clear();
	}

	private static int FloorDivide(int value, int divisor)
	{
		int quotient = value / divisor;
		int remainder = value % divisor;
		return remainder != 0 && ((remainder < 0) != (divisor < 0)) ? quotient - 1 : quotient;
	}

	private readonly record struct ChunkKey(int X, int Y, int Z);
	private readonly record struct HitKey(long GroupID, long EntityID);

	private struct LivingSweep
	{
		internal IRailLivingCollisionSource Source;
		internal long GroupID;
		internal int Dimension;
		internal double SpeedABS;
		internal double HalfWidth;
		internal double BottomOffset;
		internal double Height;
		internal double Ax, Ay, Az;
		internal double Bx, By, Bz;
	}
}

// Note, in the future if we add momentum transfer/standing on vehicles, we can distinguish between
// a collision with a vehicle and just standing on it based on the difference in momentum + buffer.
// And maybe probably some extra collision hitboxes.
