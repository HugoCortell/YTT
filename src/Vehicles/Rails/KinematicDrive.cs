using System;
using Vintagestory.API.MathTools;

namespace YangTransport;

internal static class KinematicDrive
{
	internal readonly record struct Parameters(double HardMaxSpeed, double BaseResistance, double WeightResistance, double BrakeDeceleration, double StopEpsilon); 

	private static double MoveTowards(double current, double target, double maxDelta)
	{
	    if (maxDelta <= 0) return current;
	    if (current < target) return Math.Min(current + maxDelta, target);
	    if (current > target) return Math.Max(current - maxDelta, target);
	    return current;
	}

	public static double Step
	(
		double speed, double throttle, float deltaTime, int weight, double accelerationBPSPerSec,
		double maxSpeedBPS, double trackMaxSpeedFactor, in Parameters driveParameters, bool brake = false
	)
	{
	    double rollingDeceleration = driveParameters.BaseResistance + driveParameters.WeightResistance * Math.Max(0, weight - 1);

	    double effectiveThrottle = Math.Abs(throttle) > 0.1 ? throttle : 0;

	    // Brake lever / commanded stop | Active braking, not passive coasting.
	    if (brake)
	    {
	        speed = MoveTowards(speed, 0, (driveParameters.BrakeDeceleration + rollingDeceleration) * deltaTime);
	        if (Math.Abs(speed) < driveParameters.StopEpsilon) speed = 0;
	        return speed;
	    }

	    // Apply track cap after engine + weight computed.
	    double speedCap = GameMath.Clamp(maxSpeedBPS * trackMaxSpeedFactor, 0, driveParameters.HardMaxSpeed);

	    // No throttle means coast to stop.
	    if (effectiveThrottle == 0)
	    {
	        speed = MoveTowards(speed, 0, rollingDeceleration * deltaTime);
	        if (Math.Abs(speed) < driveParameters.StopEpsilon) speed = 0;
	        return speed;
	    }

	    // If cap is basically zero, we can't pull this train.
	    if (speedCap <= 0.01)
	    {
	        speed = MoveTowards(speed, 0, rollingDeceleration * deltaTime);
	        if (Math.Abs(speed) < driveParameters.StopEpsilon) speed = 0;
	        return speed;
	    }

	    double targetSpeed = Math.Sign(effectiveThrottle) * speedCap;

	    // If we're over cap (eg entered a curve), brake down quickly. | While if trying to reverse while moving, brake toward 0 first.
	    if (Math.Sign(speed) == Math.Sign(targetSpeed) && Math.Abs(speed) > speedCap + 0.01)	{ speed = MoveTowards(speed, targetSpeed, (driveParameters.BrakeDeceleration + rollingDeceleration) * deltaTime); }
	    else if (Math.Sign(speed) != Math.Sign(targetSpeed) && Math.Abs(speed) > 0.01)			{ speed = MoveTowards(speed, 0, (driveParameters.BrakeDeceleration + rollingDeceleration) * deltaTime); }
	    else																					{ speed = MoveTowards(speed, targetSpeed, Math.Max(0, accelerationBPSPerSec) * deltaTime); }

	    // Do not deadband active powered acceleration here. Low power starts may need several ticks to accumulate above StopEpsilon.
		// Braking/coasting/no-power paths already apply the stop epsilon before returning.
	    if (speed > driveParameters.HardMaxSpeed) speed = driveParameters.HardMaxSpeed;
	    else if (speed < -driveParameters.HardMaxSpeed) speed = -driveParameters.HardMaxSpeed;
	    return speed;
	}
}
