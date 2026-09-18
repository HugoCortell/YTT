using System;

namespace YangTransport;

/// Client-side cosmetic rail travel accumulator. It deliberately knows nothing about rail simulation,
/// callers feed it the position/path coordinate that is already being rendered, and mechanical animation derives phase from the accumulated signed distance.
internal struct RailMechanicalTravel
{
	private const double DefaultDiscontinuityDistance = 32.0;

	private bool Initialized;
	private double LastX, LastY, LastZ;
	private double LastScalar;
	private RailMechanicalTravelSource TravelSource;

	public double TotalSignedDistance { get; private set; }

	public void ResetWorld(double x, double y, double z)
	{
		LastX = x; LastY = y; LastZ = z;
		Initialized = true;
		TravelSource = RailMechanicalTravelSource.World;
	}

	public void ResetScalar(double value)
	{
		LastScalar = value;
		Initialized = true;
		TravelSource = RailMechanicalTravelSource.Scalar;
	}

	public double SampleWorld(double x, double y, double z, float yaw, double discontinuityDistance = DefaultDiscontinuityDistance)
	{
		if (!Initialized || TravelSource != RailMechanicalTravelSource.World) { ResetWorld(x, y, z); return 0; }

		double dx = x - LastX;
		double dy = y - LastY;
		double dz = z - LastZ;
		LastX = x; LastY = y; LastZ = z;

		double squaredDistance = dx * dx + dy * dy + dz * dz;
		if (squaredDistance <= 1e-12) return 0;

		double maxSquaredDistance = discontinuityDistance * discontinuityDistance;
		if (squaredDistance > maxSquaredDistance) return 0;

		double distance = Math.Sqrt(squaredDistance);

		// Entity forward in Vintage Story is (+sin(yaw), +cos(yaw)).
		// The horizontal projection supplies the sign while the full 3D distance preserves slope travel.
		double alongForward = dx * Math.Sin(yaw) + dz * Math.Cos(yaw);
		if (Math.Abs(alongForward) <= 1e-9)
		{
			// Vertical-only movement is not meaningful rail travel. Keep the sample in sync but don't arbitrarily advance the mechanism.
			return 0;
		}

		double signedDistance = alongForward < 0 ? -distance : distance;
		TotalSignedDistance += signedDistance;
		return signedDistance;
	}

	public double SampleScalar(double value, double discontinuityDistance = DefaultDiscontinuityDistance)
	{
		if (!Initialized || TravelSource != RailMechanicalTravelSource.Scalar) { ResetScalar(value); return 0; }

		double delta = value - LastScalar;
		LastScalar = value;

		if (Math.Abs(delta) > discontinuityDistance) return 0;

		TotalSignedDistance += delta; return delta;
	}
}

internal enum RailMechanicalTravelSource : byte { None = 0, World = 1, Scalar = 2 }

internal static class RailMechanicalAnimation
{
	public const double DefaultSmallWheelDistancePerCycle = Math.PI * 0.625; // 10 model units diameter.

	public static double WrapPhase(double signedDistance, double distancePerCycle, double direction = 1.0, double phaseOffset = 0.0)
	{
		if (distancePerCycle <= 1e-9) return 0;

		double phase = signedDistance / distancePerCycle * direction + phaseOffset;
		phase -= Math.Floor(phase);
		return phase;
	}

	// Callers pass the already wrapped [0, 1) result from WrapPhase().
	public static float PhaseToAnimationFrame(double phase, int quantityFrames)
	{
		if (quantityFrames <= 1) return 0;
		return (float)(phase * quantityFrames);
	}

	// Callers pass the already wrapped [0, 1) result from WrapPhase().
	public static int PhaseToSample(double phase, int sampleCount)
	{
		if (sampleCount <= 1) return 0;

		int index = (int)(phase * sampleCount);
		return index >= sampleCount ? sampleCount - 1 : index;
	}
}
