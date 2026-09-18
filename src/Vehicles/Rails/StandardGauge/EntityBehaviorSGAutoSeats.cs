using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace YangTransport;

/// Data-driven seat generator for SG rolling stock variants. Registers numbered AP seats once during initialization, not used by the movement tick path.
public sealed class EntityBehaviorSGAutoSeats : EntityBehavior
{
	private string AttachmentPointPrefix = "SEAT_AP_";
	private int SeatCount;
	private string SeatIDPrefix = "seat";
	private bool SeatsControllable;
	private EnumMountAngleMode MountAngleMode = EnumMountAngleMode.PushYaw;
	private float SeatEyeHeight = 1.1f;
	private string SeatAnimationCode = "sitflooridle";
	private Vec3f SeatMountOffset = new Vec3f();
	private bool SeatsRegistered;

	public EntityBehaviorSGAutoSeats(Entity entity) : base(entity) { }

	public override string PropertyName() => "SGAutoSeats";

	public override void Initialize(EntityProperties properties, JsonObject attributes)
	{
		AttachmentPointPrefix = attributes["Prefix"].AsString(AttachmentPointPrefix);
		SeatCount = Math.Max(0, attributes["Count"].AsInt(0));
		SeatIDPrefix = attributes["SeatIDPrefix"].AsString(SeatIDPrefix);
		SeatsControllable = attributes["Controllable"].AsBool(false);
		SeatEyeHeight = attributes["EyeHeight"].AsFloat(SeatEyeHeight);
		SeatAnimationCode = attributes["Animation"].AsString(SeatAnimationCode);

		string angleModeCode = attributes["AngleMode"].AsString(null);
		if (!string.IsNullOrEmpty(angleModeCode) && Enum.TryParse(angleModeCode, ignoreCase: true, out EnumMountAngleMode parsedMode)) { MountAngleMode = parsedMode; }

		Vec3f configuredOffset = attributes["MountOffset"].AsObject<Vec3f>(null);
		if (configuredOffset != null) SeatMountOffset = configuredOffset;

		base.Initialize(properties, attributes);
		TryRegisterSeats();
	}

	public override void AfterInitialized(bool onFirstSpawn)
	{
		base.AfterInitialized(onFirstSpawn);
		TryRegisterSeats();
	}

	private void TryRegisterSeats()
	{
		if (SeatsRegistered || SeatCount <= 0) return;

		EntityBehaviorSeatable seatable = entity.GetBehavior<EntityBehaviorSeatable>();
		if (seatable == null) return;

		for (int seatIndex = 0; seatIndex < SeatCount; seatIndex++)
		{
			string attachmentPointCode = AttachmentPointPrefix + seatIndex;

			seatable.RegisterSeat(new SeatConfig
			{
				SeatId = SeatIDPrefix + seatIndex,
				APName = attachmentPointCode,
				SelectionBox = attachmentPointCode,
				Controllable = SeatsControllable,
				AngleMode = MountAngleMode,
				EyeHeight = SeatEyeHeight,
				Animation = SeatAnimationCode,
				MountOffset = new Vec3f(SeatMountOffset.X, SeatMountOffset.Y, SeatMountOffset.Z)
			});
		}

		SeatsRegistered = true;
	}
}
