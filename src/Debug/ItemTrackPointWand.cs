using System;
using System.Collections.Generic;
using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace YangTransport;

public class ItemTrackPointWand : Item
{
	private const int HighlightID = 934201;
	private static BlockPos? SelectedTrackPosition;

	public override void OnHeldAttackStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSelection, EntitySelection entitySelection, ref EnumHandHandling handling)
	{
		handling = EnumHandHandling.PreventDefault;
		if (api.Side != EnumAppSide.Client || blockSelection?.Position == null) return;
		var clientAPI = api as ICoreClientAPI; if (clientAPI == null) return;

		var block = api.World.BlockAccessor.GetBlock(blockSelection.Position);
		if (block is not BlockTrackBase) { clientAPI.ShowChatMessage($"Not a track: {block.Code}"); return; }

		SelectedTrackPosition = blockSelection.Position.Copy();
		clientAPI.World.HighlightBlocks(clientAPI.World.Player, HighlightID, new List<BlockPos> { SelectedTrackPosition });
		clientAPI.ShowChatMessage($"Selected {block.Code} @ {SelectedTrackPosition.X},{SelectedTrackPosition.Y},{SelectedTrackPosition.Z}");
	}

	public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSelection, EntitySelection entitySelection, bool firstEvent, ref EnumHandHandling handling)
	{
		handling = EnumHandHandling.PreventDefault;
		if (!firstEvent || api.Side != EnumAppSide.Client) return;
		var clientAPI = api as ICoreClientAPI; if (clientAPI == null) return;
		if (SelectedTrackPosition == null) { clientAPI.ShowChatMessage("No selected track. Left click a track block first."); return; }
		if (blockSelection?.Position == null) return;

		var hitPosition = new Vec3d(blockSelection.Position.X + blockSelection.HitPosition.X, blockSelection.Position.Y + blockSelection.HitPosition.Y, blockSelection.Position.Z + blockSelection.HitPosition.Z);
		var localHitPosition = hitPosition.SubCopy(SelectedTrackPosition.X, SelectedTrackPosition.Y, SelectedTrackPosition.Z);
		float qx = Q16(localHitPosition.X), qy = Q16(localHitPosition.Y), qz = Q16(localHitPosition.Z);
		clientAPI.ShowChatMessage($"P({F(qx)}f, {F(qz)}f, {F(qy)}f)");

		clientAPI.World.SpawnParticles(new SimpleParticleProperties(1f, 1f, ColorUtil.ToRgba(255, 255, 0, 255), hitPosition, hitPosition, new Vec3f(), new Vec3f(), 1f, 0f, 0.15f, 0.15f, EnumParticleModel.Quad));
	}

	private static float Q16(double value) => (float)(Math.Round(value * 16.0) / 16.0); 
	private static string F(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
