using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace YangTransport;

// Mechanical power producer behavior for BlockEntitySteamEngine. Acts as a heat engine more than a steam engine, which is an acceptable abstraction for simple engines.
public sealed class SteamMechanica : BEBehaviorMPBase
{
	private double CapableSpeed;
	private BlockFacing OutputFacing;
	private static readonly AssetLocation AxleShapeAsset = new("yangtransport", "engines/default_engine_axle");
	private const float BaseResistance = 0.01f;
	private const double AccelerationFactor = 0.05;

	private BlockEntitySteamEngine EngineBlockEntity => (BlockEntitySteamEngine)Blockentity;

	public SteamMechanica(BlockEntity blockEntity) : base(blockEntity) { }

	public override void SetOrientations()
	{
	    // Called by BEBehaviorMPBase.Initialize() BEFORE it attempts network discovery. Support both common horizontal variant keys.
	    string side = Block?.Variant?["side"] ?? Block?.Variant?["horizontalorientation"];
		
	    // Vanilla rotor convention: "side" = mounting face, output = opposite.
	    OutputFacing = side != null ? BlockFacing.FromCode(side).Opposite : BlockFacing.SOUTH;
	    OutFacingForNetworkDiscovery = OutputFacing;
		
	    AxisSign = OutputFacing.Axis == EnumAxis.X ? new[] { 1, 0, 0 } : new[] { 0, 0, 1 };
	}
	
	public override void Initialize(ICoreAPI coreAPI, JsonObject properties) { base.Initialize(coreAPI, properties); }

	public override float GetResistance() => BaseResistance;

	public override float GetTorque(long tick, float speed, out float resistance)
	{
		double tempC = EngineBlockEntity?.TemperatureC ?? 0;

		// No steam production below boiling
		if (tempC < 100.0)
		{
			resistance = 0;
			CapableSpeed = 0;
			return 0;
		}

		double heatUnits = tempC / 100.0; // We do math in per 100c because its easier for my feeble brain

		// Convert heat -> target MP values
		float targetSpeed = (float)(heatUnits * EngineBlockEntity.Config.AccelerationNewtonsPer100Celsius);
		float maximumTorqueAtTarget = (float)(heatUnits * EngineBlockEntity.Config.RawPowerNewtonsPer100Celsius);

		if (targetSpeed <= 0f || maximumTorqueAtTarget <= 0f)
		{
			resistance = 0;
			return 0;
		}

		// Spool capable speed toward target
		CapableSpeed += (targetSpeed - CapableSpeed) * AccelerationFactor;
		float capableRotationalSpeed = (float)CapableSpeed;

		const float epsilon = 1e-4f;
		float torqueFactor = maximumTorqueAtTarget / Math.Max(capableRotationalSpeed, epsilon);

		float outputDirectionSign = (propagationDir == OutFacingForNetworkDiscovery) ? 1f : -1f;

		float absoluteSpeed = Math.Abs(speed);
		float excessSpeed = absoluteSpeed - capableRotationalSpeed;
		bool wrongDirection = outputDirectionSign * speed < 0f;

		resistance =
			wrongDirection
				? (BaseResistance * torqueFactor * Math.Min(0.8f, absoluteSpeed * 400f))
				: (excessSpeed > 0f ? (BaseResistance * Math.Min(0.2f, excessSpeed * excessSpeed * 80f)) : 0f);

		float availableSpeed = capableRotationalSpeed - absoluteSpeed;
		if (wrongDirection) availableSpeed = capableRotationalSpeed;

		return Math.Max(0f, availableSpeed) * torqueFactor * outputDirectionSign;
	}

	
	public override MechPowerPath[] GetMechPowerExits(MechPowerPath fromExitTurnDir)
	{
	    // Producer endpoint (like BEBehaviorMPRotor) - do not pass network through this block.
	    return Array.Empty<MechPowerPath>();
	}

	#region Rendering
	protected override CompositeShape GetShape()
	{
		// Clone so we inherit shapebytype rotateX/Y/Z (north/east/south/west variants)
		CompositeShape shape = Block?.Shape?.Clone() ?? new CompositeShape();
		shape.Base = AxleShapeAsset;
		shape.Overlays = null;
		return shape;
	}

	protected override void updateShape(IWorldAccessor worldAccessorForResolution) { Shape = GetShape(); }

	public override bool OnTesselation(ITerrainMeshPool terrainMeshPool, ITesselatorAPI tesselator)
	{
	    // Keep MP-rendered axle correctly lit
	    lightRbs = Api.World.BlockAccessor.GetLightRGBs(Blockentity.Pos);

	    // CRITICAL: return false so the normal block JSON mesh (the engine body) is still rendered.
	    // BEBehaviorMPBase returns true by default, which skips the default mesh entirely.
	    return false;
	}
	#endregion
}
