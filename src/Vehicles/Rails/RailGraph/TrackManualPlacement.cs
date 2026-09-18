using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace YangTransport;

/// One-shot manual aligned-variant selection and transient toolmode icon generation.
internal static class TrackManualPlacement
{
	private static ConditionalWeakTable<IPlayer, PlayerManualModeState> PlayerModes = new();

	private static BlockTrackAligned? CachedClientFamily;
	private static SkillItem[]? CachedClientModes;
	private static SkillItem[]? RetiredClientModes;
	private static int CachedClientFacing = -1;
	private static bool ToolDialogWasOpen;

	internal static void OnTracksCompiled()
	{
		DisposeClientModes();
		PlayerModes = new();
	}

	internal static bool TryResolveManualVariant(BlockTrackAligned heldBlock, IPlayer? player, out BlockTrackAligned variant)
	{
		variant = heldBlock;
		BlockTrackAligned? family = GetFamily(heldBlock);
		if (family == null || !TryGetMode(player, family, out int mode)) return false;

		variant = family.GetManualPlacementVariant(mode - 1);
		return true;
	}

	internal static SkillItem[]? GetToolModes(BlockTrackAligned heldBlock, ICoreClientAPI clientAPI)
	{
		BlockTrackAligned? family = GetFamily(heldBlock);
		if (family == null) return null;

		bool toolDialogOpen = IsToolModeDialogOpen(clientAPI);
		if (!ReferenceEquals(CachedClientFamily, family) || CachedClientModes == null)
		{
			DisposeClientModes();
			CachedClientFamily = family;
			CachedClientFacing = GetPlayerFacing(clientAPI);
			CachedClientModes = BuildModes(clientAPI, family, CachedClientFacing);
		}
		else if (toolDialogOpen && !ToolDialogWasOpen)
		{
			int facing = GetPlayerFacing(clientAPI);
			if (facing != CachedClientFacing)
			{
				DisposeRetiredClientModes();
				RetiredClientModes = CachedClientModes;
				CachedClientModes = BuildModes(clientAPI, family, facing);
				CachedClientFacing = facing;
			}
		}
		else if (!toolDialogOpen && ToolDialogWasOpen) { DisposeRetiredClientModes(); }

		ToolDialogWasOpen = toolDialogOpen;
		return CachedClientModes;
	}

	internal static int GetToolMode(BlockTrackAligned heldBlock, IPlayer? player)
	{
		BlockTrackAligned? family = GetFamily(heldBlock);
		return family != null && TryGetMode(player, family, out int mode) ? mode : 0;
	}

	internal static void SetToolMode(BlockTrackAligned heldBlock, IPlayer? player, int mode)
	{
		BlockTrackAligned? family = GetFamily(heldBlock);
		if (player == null || family == null) return;

		mode = GameMath.Clamp(mode, 0, family.ManualPlacementVariantCount);
		if (mode == 0)
		{
			PlayerModes.Remove(player);
			return;
		}

		PlayerManualModeState state = PlayerModes.GetValue(player, static playerKey => new PlayerManualModeState());
		state.Family = family;
		state.Mode = mode;
	}

	internal static void Reset(IPlayer? player) { if (player != null) PlayerModes.Remove(player); }

	internal static void CloseToolModeDialog(ICoreClientAPI clientAPI)
	{
		List<GuiDialog> dialogs = clientAPI.Gui.LoadedGuis;
		for (int dialogIndex = 0; dialogIndex < dialogs.Count; dialogIndex++)
		{
			GuiDialog dialog = dialogs[dialogIndex];
			if (dialog.ToggleKeyCombinationCode == "toolmodeselect" && dialog.IsOpened()) { dialog.TryClose(); return; }
		}
	}

	/// Releases an outgoing hold-session cache without disposing a cache the vanilla hotbar
	/// may already have rebuilt for the newly selected family earlier in the same event.
	internal static void OnClientActiveSlotChanged(BlockTrackAligned? newlyHeldBlock)
	{
		DisposeRetiredClientModes();
		ToolDialogWasOpen = false;

		BlockTrackAligned? heldFamily = GetFamily(newlyHeldBlock);
		if (!ReferenceEquals(CachedClientFamily, heldFamily)) DisposeClientModes();
	}

	internal static void Dispose()
	{
		DisposeClientModes();
		PlayerModes = new();
	}

	internal static void DisposeClientModes()
	{
		DisposeModes(CachedClientModes);
		DisposeRetiredClientModes();
		CachedClientModes = null;
		CachedClientFamily = null;
		CachedClientFacing = -1;
		ToolDialogWasOpen = false;
	}

	private static void DisposeRetiredClientModes()
	{
		DisposeModes(RetiredClientModes);
		RetiredClientModes = null;
	}

	private static void DisposeModes(SkillItem[]? modes)
	{
		if (modes == null) return;
		for (int modeIndex = 0; modeIndex < modes.Length; modeIndex++) modes[modeIndex]?.Dispose();
	}

	private static BlockTrackAligned? GetFamily(BlockTrackAligned? block)
	{
		return block?.TrackConfigCompiled == true ? block.ManualPlacementFamilyRoot : null;
	}

	private static bool TryGetMode(IPlayer? player, BlockTrackAligned family, out int mode)
	{
		mode = 0;
		if (player == null || !PlayerModes.TryGetValue(player, out PlayerManualModeState? state)) return false;

		if (!ReferenceEquals(state.Family, family) || state.Mode <= 0 || state.Mode > family.ManualPlacementVariantCount)
		{
			PlayerModes.Remove(player);
			return false;
		}

		mode = state.Mode;
		return true;
	}

	private static bool IsToolModeDialogOpen(ICoreClientAPI clientAPI)
	{
		foreach (GuiDialog dialog in clientAPI.Gui.LoadedGuis) { if (dialog.ToggleKeyCombinationCode == "toolmodeselect" && dialog.IsOpened()) return true; }
		return false;
	}

	private static int GetPlayerFacing(ICoreClientAPI clientAPI)
	{
		float yaw = clientAPI.World.Player?.Entity?.Pos?.Yaw ?? 0f;
		return BlockFacing.HorizontalFromYaw(yaw).Index;
	}

	private static SkillItem[] BuildModes(ICoreClientAPI clientAPI, BlockTrackAligned family, int facing)
	{
		SkillItem[] modes = new SkillItem[family.ManualPlacementVariantCount + 1];

		SkillItem automatic = new()
		{
			Code = new AssetLocation("yangtransport:trackplacement-automatic"),
			Name = Lang.Get("yangtransport:manualplacement-automatic")
		};

		AssetLocation gearLocation = new("game:textures/icons/worldmap/gear.svg");
		IAsset? gearAsset = clientAPI.Assets.TryGet(gearLocation, loadAsset: true);
		if (gearAsset?.Data != null)
		{
			automatic.WithIcon(clientAPI, (drawingContext, x, y, width, height, colorComponents) =>
			{
				if (drawingContext.GetTarget() is not ImageSurface surface) return;
				clientAPI.Gui.DrawSvg(gearAsset, surface, drawingContext.Matrix, x, y, (int)width, (int)height, ColorUtil.ColorFromRgba(colorComponents));
			});
		}
		else { automatic.WithLetterIcon(clientAPI, "A"); }

		modes[0] = automatic;

		for (int variantIndex = 0; variantIndex < family.ManualPlacementVariantCount; variantIndex++)
		{
			BlockTrackAligned variant = family.GetManualPlacementVariant(variantIndex);
			string variantCode = family.AlignmentMode == TrackAlignmentMode.WorldAligned ? "axis" : "rot";
			string state = variant.Variant![variantCode];

			string langKey = "yangtransport:manualplacement-" + state.ToLowerInvariant();
			SkillItem mode = new()
			{
				Code = new AssetLocation("yangtransport", "trackplacement-" + state),
				Name = Lang.Get(langKey)
			};

			if (TrackSpecsDictionary.TryGet(variant, out TrackPieceSpec trackSpecification))
			{
				byte iconRotationSteps = variant.ManualPlacementIconRotationSteps;
				mode.WithIcon(clientAPI, (drawingContext, x, y, width, height, colorComponents) => DrawTrackIcon(drawingContext, x, y, width, height, colorComponents, trackSpecification, facing, iconRotationSteps));
			}
			else { mode.WithLetterIcon(clientAPI, mode.Name); }

			modes[variantIndex + 1] = mode;
		}

		return modes;
	}

	private static void DrawTrackIcon(Context drawingContext, int x, int y, float width, float height, double[] colorComponents, TrackPieceSpec trackSpecification, int facing, byte iconRotationSteps)
	{
		const double lineWidth = 4.0;
		const double sourceRadius = 3.5;
		const double paintInset = sourceRadius;
		double iconAngle = -iconRotationSteps * Math.PI / 4.0;
		double iconRotationCos = Math.Cos(iconAngle);
		double iconRotationSin = Math.Sin(iconAngle);

		double minX = double.MaxValue, minZ = double.MaxValue;
		double maxX = double.MinValue, maxZ = double.MinValue;
		Vec3f sourcePoint = default;
		float sourceDistanceSQ = float.MaxValue;
		bool anyPoint = false;

		foreach (TrackPath path in trackSpecification.Paths)
		{
			Vec3f[] points = path.LocalPoints;
			for (int pointIndex = 0; pointIndex < points.Length; pointIndex++)
			{
				Vec3f point = points[pointIndex];
				TransformPoint(point.X, point.Z, facing, iconRotationCos, iconRotationSin, out double px, out double pz);
				minX = Math.Min(minX, px);
				maxX = Math.Max(maxX, px);
				minZ = Math.Min(minZ, pz);
				maxZ = Math.Max(maxZ, pz);

				float dx = point.X - 0.5f;
				float dz = point.Z - 0.5f;
				float distanceSQ = dx * dx + dz * dz;
				if (distanceSQ < sourceDistanceSQ)
				{
					sourceDistanceSQ = distanceSQ;
					sourcePoint = point;
				}

				anyPoint = true;
			}
		}

		if (!anyPoint) return;

		double fitX = x + paintInset;
		double fitY = y + paintInset;
		double fitWidth = Math.Max(1.0, width - paintInset * 2.0);
		double fitHeight = Math.Max(1.0, height - paintInset * 2.0);

		double spanX = maxX - minX;
		double spanZ = maxZ - minZ;
		double scaleX = spanX > 0.0001 ? fitWidth / spanX : double.MaxValue;
		double scaleZ = spanZ > 0.0001 ? fitHeight / spanZ : double.MaxValue;
		double scale = Math.Min(scaleX, scaleZ);
		if (scale == double.MaxValue) scale = 1.0;

		double usedWidth = spanX * scale;
		double usedHeight = spanZ * scale;
		double originX = fitX + (fitWidth - usedWidth) * 0.5 - minX * scale;
		double originY = fitY + (fitHeight - usedHeight) * 0.5 - minZ * scale;

		drawingContext.Save();
		drawingContext.SetSourceRGBA(colorComponents);
		drawingContext.LineWidth = lineWidth;
		drawingContext.LineCap = (LineCap)1;
		drawingContext.LineJoin = (LineJoin)1;

		foreach (TrackPath path in trackSpecification.Paths)
		{
			Vec3f[] points = path.LocalPoints;
			if (points.Length < 2) continue;

			drawingContext.NewPath();
			MoveTo(points[0]);

			if (points.Length == 2) { LineTo(points[1]); }
			else
			{
				for (int pointIndex = 0; pointIndex < points.Length - 1; pointIndex++)
				{
					Vec3f p0 = points[Math.Max(0, pointIndex - 1)];
					Vec3f p1 = points[pointIndex];
					Vec3f p2 = points[pointIndex + 1];
					Vec3f p3 = points[Math.Min(points.Length - 1, pointIndex + 2)];

					double c1x = p1.X + (p2.X - p0.X) / 6.0;
					double c1z = p1.Z + (p2.Z - p0.Z) / 6.0;
					double c2x = p2.X - (p3.X - p1.X) / 6.0;
					double c2z = p2.Z - (p3.Z - p1.Z) / 6.0;

					ToIcon(c1x, c1z, out double ic1x, out double ic1y);
					ToIcon(c2x, c2z, out double ic2x, out double ic2y);
					ToIcon(p2.X, p2.Z, out double ip2x, out double ip2y);
					drawingContext.CurveTo(ic1x, ic1y, ic2x, ic2y, ip2x, ip2y);
				}
			}

			drawingContext.Stroke();
		}

		ToIcon(sourcePoint.X, sourcePoint.Z, out double sourceX, out double sourceY);
		drawingContext.SetSourceRGBA(1.0, 0.0, 0.0, 1.0);
		drawingContext.NewPath();
		drawingContext.Arc(sourceX, sourceY, sourceRadius, 0, Math.PI * 2);
		drawingContext.Fill();
		drawingContext.Restore();

		void MoveTo(Vec3f point)
		{
			ToIcon(point.X, point.Z, out double px, out double py);
			drawingContext.MoveTo(px, py);
		}

		void LineTo(Vec3f point)
		{
			ToIcon(point.X, point.Z, out double px, out double py);
			drawingContext.LineTo(px, py);
		}

		void ToIcon(double pointX, double pointZ, out double iconX, out double iconY)
		{
			TransformPoint(pointX, pointZ, facing, iconRotationCos, iconRotationSin, out double transformedX, out double transformedZ);
			iconX = originX + transformedX * scale;
			iconY = originY + transformedZ * scale;
		}
	}

	private static void TransformPoint(double x, double z, int facing, double iconRotationCos, double iconRotationSin, out double transformedX, out double transformedZ)
	{
		double rotatedX = x * iconRotationCos - z * iconRotationSin;
		double rotatedZ = x * iconRotationSin + z * iconRotationCos;

		switch (facing)
		{
			case 1: // East: world east is icon-up
				transformedX = rotatedZ;
				transformedZ = -rotatedX;
			return;

			case 2: // South
				transformedX = -rotatedX;
				transformedZ = -rotatedZ;
			return;

			case 3: // West
				transformedX = -rotatedZ;
				transformedZ = rotatedX;
			return;

			default: // North
				transformedX = rotatedX;
				transformedZ = rotatedZ;
			return;
		}
	}

	private sealed class PlayerManualModeState
	{
		internal BlockTrackAligned? Family;
		internal int Mode;
	}
}
