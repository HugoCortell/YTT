using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace YangTransport;

public sealed class EngineControlUI : GuiDialogGeneric
{
	public InventoryBase Inventory { get; }
	private readonly string DialogID;
	private readonly Action<object> SendInventoryPacket;
	private readonly Action<int, byte[]?> SendControlPacket;
	private readonly bool ShowLocomotiveControls;
	private readonly BlockPos? BlockEntityPosition;
	private readonly Entity? OwningEntity;

	public const int PacketIDOpen					= SteamEnginePacketIds.Open;
	public const int PacketIDClose					= SteamEnginePacketIds.Close;
	public const int PacketIDVentExcess				= SteamEnginePacketIds.VentExcess;
	public const int PacketIDDumpFluid				= SteamEnginePacketIds.DumpFluid;
	public const int PacketIDSetTargetTemperature 	= SteamEnginePacketIds.SetTargetTemperature;
	public const int PacketIDIgniteFuel				= SteamEnginePacketIds.IgniteFuel;
	public const int PacketIDCutFuel				= SteamEnginePacketIds.CutFuel;

	private const int SliderMin = 0;
	private const int SliderMax = 1500;
	private const int SliderStep = 50;

	private bool SuppressSliderPacket;
	private int LastSliderValue = int.MinValue;
	private bool UIThermostatLocked;

	private int LastSentTemperatureValue = int.MinValue;

	public override double DrawOrder => 0.2;

	public EngineControlUI
	(
		string dialogTitle, string dialogID, InventoryBase inventory,
		SyncedTreeAttribute attributes, ICoreClientAPI clientAPI, Action<object> sendInventoryPacket,
		Action<int, byte[]?> sendControlPacket, bool showLocomotiveControls,
		BlockPos? blockEntityPosition = null, Entity? owningEntity = null
	) : base(dialogTitle, clientAPI)
	{
		DialogTitle = dialogTitle;
		this.DialogID = dialogID;
		Inventory = inventory;
		this.SendInventoryPacket = sendInventoryPacket;
		this.SendControlPacket = sendControlPacket;
		this.ShowLocomotiveControls = showLocomotiveControls;
		this.BlockEntityPosition = blockEntityPosition;
		this.OwningEntity = owningEntity;

		// Live-updated BE values (temperature, burn progress)
		attributes.OnModified.Add(new TreeModifiedListener { listener = OnAttributesModified });
		Attributes = attributes;

		SetupDialog();
		OnAttributesModified();
	}

	private void SetupDialog()
	{
		// Desired content size BELOW the title bar
		const double contentWidth = 650;
		const double contentHeight = 380;

		double titleBarHeight = GuiStyle.TitleBarHeight;
		double elementPadding = GuiElementItemSlotGridBase.unscaledSlotPadding;

		// Split: 65% left, 35% right, with a small gap
		const double horizontalGap = 10;
		double usableWidth = contentWidth - horizontalGap;
		double fuelDetailsWidth = Math.Floor(usableWidth * 0.65);
		double rightWidth = usableWidth - fuelDetailsWidth;

		ElementBounds backgroundBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
		backgroundBounds.BothSizing = ElementSizing.FitToChildren;

		ElementBounds leftPanelBounds = ElementBounds.Fixed(0, titleBarHeight, fuelDetailsWidth, contentHeight).WithParent(backgroundBounds);
		ElementBounds rightPanelBounds = ElementBounds.Fixed(fuelDetailsWidth + horizontalGap, titleBarHeight, rightWidth, contentHeight).WithParent(backgroundBounds);

		// Inner layout for later module/info content + button
		const double panelPadding = 6;
		const double verticalGap = 10;
		const double buttonHeight = 30;
		const double buttonsGap = 6;

		// LEFT PANEL | Water Gauge
		const double waterReliefWidth = 70;
		double waterReliefHeight = contentHeight - panelPadding * 2 - 90; // shorter so it always fits nicely
		if (waterReliefHeight < 180) waterReliefHeight = 180;

		ElementBounds waterReliefBounds = ElementBounds.Fixed(panelPadding, panelPadding + 10, waterReliefWidth, waterReliefHeight).WithParent(leftPanelBounds);

		// Draw area inside the inset (simple padding, avoids ForkBoundingParent quirks)
		ElementBounds waterMeterDrawBounds = ElementBounds.Fixed(4, 4, waterReliefWidth - 8, waterReliefHeight - 8).WithParent(waterReliefBounds);

		// Text in its own small inset under the meter (like barrel info readout)
		ElementBounds waterTextReliefBounds = ElementBounds.Fixed
		(
			panelPadding,
			waterReliefBounds.fixedY + waterReliefBounds.fixedHeight + 10,
			fuelDetailsWidth - panelPadding * 2,
			70
		).WithParent(leftPanelBounds);

		// Bottom-left relief | Split into fluid info (left) + temperature summary (right)
		double bottomInnerWidth = (fuelDetailsWidth - panelPadding * 2) - 12;
		const double bottomGap = 4;
		double waterInformationWidth = Math.Floor(bottomInnerWidth * 0.42);
		double temperatureInformationWidth = bottomInnerWidth - waterInformationWidth - bottomGap;

		ElementBounds waterTextBounds = ElementBounds.Fixed(6, 4, waterInformationWidth, waterTextReliefBounds.fixedHeight - 8).WithParent(waterTextReliefBounds);

		ElementBounds temperatureInformationTextBounds = ElementBounds.Fixed
		(
			6 + waterInformationWidth + bottomGap, 4,
			temperatureInformationWidth,
			waterTextReliefBounds.fixedHeight - 8
		).WithParent(waterTextReliefBounds);

		// Fuel relief to the right of the water gauge (top-left area)
		const double fuelReliefWidth = 90;
		const double fuelReliefHeight = 148;
		ElementBounds fuelReliefBounds = ElementBounds.Fixed
		(
			panelPadding + waterReliefWidth + 10,
			panelPadding + 10,
			fuelReliefWidth,
			fuelReliefHeight
		).WithParent(leftPanelBounds);

		// Fuel slot centered under the flame icon
		double slotSize = GuiElementPassiveItemSlot.unscaledSlotSize;
		double fuelSlotX = Math.Floor((fuelReliefWidth - slotSize) / 2);
		const double fuelIconW = 54;
		const double fuelIconH = 70;
		const double fuelIconY = 6;
		double fuelSlotY = fuelIconY + fuelIconH + 6;

		ElementBounds fuelSlotBounds = ElementBounds.Fixed(fuelSlotX, fuelSlotY, slotSize, slotSize).WithParent(fuelReliefBounds);

		// Flame icon ABOVE the slot, aligned to slot center
		double fuelIconX = fuelSlotX + Math.Floor((slotSize - fuelIconW) / 2);
		ElementBounds fuelIconBounds = ElementBounds.Fixed(fuelIconX, fuelIconY, fuelIconW, fuelIconH).WithParent(fuelReliefBounds);

		// Fuel info panel
		double fuelDetailsX = fuelReliefBounds.fixedX + fuelReliefWidth + 10;
		double fuelDetailsW = fuelDetailsWidth - fuelDetailsX - panelPadding;
		if (fuelDetailsW < 120) fuelDetailsW = 120;

		ElementBounds fuelDetailsReliefBounds = ElementBounds.Fixed
		(
			fuelDetailsX,
			fuelReliefBounds.fixedY,
			fuelDetailsW,
			fuelReliefHeight
		).WithParent(leftPanelBounds);

		ElementBounds fuelDetailsTextBounds = ElementBounds.Fixed
		(
			6, 4,
			fuelDetailsReliefBounds.fixedWidth - 12,
			fuelDetailsReliefBounds.fixedHeight - 8
		).WithParent(fuelDetailsReliefBounds);

		// Temperature slider goes on the left panel, sits just above the bottom text relief.
		double temperatureSliderX = fuelReliefBounds.fixedX;
		double temperatureSliderWidth = fuelDetailsWidth - temperatureSliderX - panelPadding;
		const double temperatureSliderReliefHeight = 98;
		double temperatureSliderY = waterTextReliefBounds.fixedY - 10 - temperatureSliderReliefHeight;
		
		double minimumSliderY = fuelReliefBounds.fixedY + fuelReliefBounds.fixedHeight + 10; // Safety clamp, never overlap the fuel relief
		if (temperatureSliderY < minimumSliderY) temperatureSliderY = minimumSliderY;

		ElementBounds temperatureSliderReliefBounds = ElementBounds.Fixed
		(
			temperatureSliderX,
			temperatureSliderY,
			temperatureSliderWidth,
			temperatureSliderReliefHeight
		).WithParent(leftPanelBounds);

		// Small overlay strip for the danger notch
		ElementBounds dangerNotchBounds = ElementBounds.Fixed(6, 6, temperatureSliderReliefBounds.fixedWidth - 12, 14).WithParent(temperatureSliderReliefBounds);

		ElementBounds temperatureSliderBounds = ElementBounds.Fixed(6, 22, temperatureSliderReliefBounds.fixedWidth - 12, 40).WithParent(temperatureSliderReliefBounds);

		// Status line under the slider
		ElementBounds outputStatusBounds = ElementBounds.Fixed(6, 66, temperatureSliderReliefBounds.fixedWidth - 12, 26).WithParent(temperatureSliderReliefBounds);

		// RIGHT PANEL with reserved space for operation buttons
		double buttonsTotalHeight = buttonHeight * 4 + buttonsGap * 3;
		ElementBounds rightInformationBounds = ElementBounds.Fixed
		(
			panelPadding,
			panelPadding,
			rightWidth - panelPadding * 2,
			contentHeight - panelPadding * 2 - buttonsTotalHeight - verticalGap
		).WithParent(rightPanelBounds);

		// Warning system
		const double warningTitleHeight = 32;
		const double warningDescriptionHeight = 120;
		const double warningGap = 6;

		ElementBounds warningTitleBounds = ElementBounds.Fixed(
			elementPadding,
			elementPadding,
			rightInformationBounds.fixedWidth - 2 * elementPadding,
			warningTitleHeight
		).WithParent(rightInformationBounds);

		ElementBounds warningDescriptionBounds = ElementBounds.Fixed(
			elementPadding,
			elementPadding + warningTitleHeight + warningGap,
			rightInformationBounds.fixedWidth - 2 * elementPadding,
			warningDescriptionHeight
		).WithParent(rightInformationBounds);

		double buttonsTopY = contentHeight - panelPadding - buttonsTotalHeight;
		ElementBounds ventExcessButtonBounds = ElementBounds.Fixed(
			panelPadding,
			buttonsTopY,
			rightWidth - panelPadding * 2,
			buttonHeight
		).WithParent(rightPanelBounds);

		ElementBounds cutFuelButtonBounds = ElementBounds.Fixed(
			panelPadding,
			buttonsTopY + (buttonHeight + buttonsGap) * 2,
			rightWidth - panelPadding * 2,
			buttonHeight
		).WithParent(rightPanelBounds);

		ElementBounds dumpFluidButtonBounds = ElementBounds.Fixed(
			panelPadding,
			buttonsTopY + (buttonHeight + buttonsGap) * 3,
			rightWidth - panelPadding * 2,
			buttonHeight
		).WithParent(rightPanelBounds);

		ElementBounds igniteFuelButtonBounds = ElementBounds.Fixed(
			panelPadding,
			buttonsTopY + (buttonHeight + buttonsGap) * 1,
			rightWidth - panelPadding * 2,
			buttonHeight
		).WithParent(rightPanelBounds);

		backgroundBounds.WithChildren(leftPanelBounds, rightPanelBounds);

		// Root dialog bounds
		ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle); // is autosized

		SingleComposer?.Dispose();
		SingleComposer = capi.Gui
			.CreateCompo("yangtransport-enginecontrol-" + DialogID, dialogBounds)
			.AddShadedDialogBG(backgroundBounds)
			.AddDialogTitleBar(DialogTitle, OnTitleBarClose)
			.BeginChildElements(backgroundBounds)

			// Main panels
			.AddInset(rightPanelBounds)

			// Right | warning system
			.AddDynamicText("", CairoFont.WhiteSmallishText().WithWeight(FontWeight.Bold), warningTitleBounds, "warnTitle")
			.AddDynamicText("", CairoFont.WhiteDetailText(), warningDescriptionBounds, "warnDesc")

			// Left | water gauge relief + draw + text relief
			.AddInset(waterReliefBounds)
			.AddDynamicCustomDraw(waterMeterDrawBounds, OnWaterMeterDraw, "waterBar")
			.AddInset(waterTextReliefBounds)
			.AddDynamicText("", CairoFont.WhiteDetailText(), waterTextBounds, "waterText")
			.AddDynamicText("", CairoFont.WhiteDetailText(), temperatureInformationTextBounds, "tempInfoText")

			// Left | fuel relief with flame icon ABOVE the slot
			.AddInset(fuelReliefBounds)
			.AddDynamicCustomDraw(fuelIconBounds, OnFuelIconDraw, "fuelIcon")
			.AddItemSlotGrid(Inventory, SendInventoryPacket, 1, new[] { 0 }, fuelSlotBounds, "fuelSlotGrid")

			// Left | fuel details panel (right of fuel relief)
			.AddInset(fuelDetailsReliefBounds)
			.AddDynamicText("", CairoFont.WhiteDetailText(), fuelDetailsTextBounds, "fuelDetailsText")

			// Left | temperature slider (with red "danger" region past engine rating)
			.AddInset(temperatureSliderReliefBounds)
			.AddDynamicCustomDraw(dangerNotchBounds, OnDangerNotchDraw, "dangerNotch")
			.AddSlider(OnTargetTemperatureSliderChanged, temperatureSliderBounds, "tempSlider")
			.AddDynamicText("", CairoFont.WhiteSmallishText().WithWeight(FontWeight.Bold), outputStatusBounds, "outputStatus")
			.AddDynamicText("", CairoFont.WhiteSmallishText().WithWeight(FontWeight.Bold).WithColor(new double[] { 1, 0.25, 0.25, 1 }), outputStatusBounds, "outputStatusRed")

			// Bottom-right action button
			.AddSmallButton(Lang.Get("yangtransport:steamengineinfo-button-vent"), OnVentExcessClicked, ventExcessButtonBounds, EnumButtonStyle.Normal, "btnVentExcess")
			.AddSmallButton(Lang.Get("yangtransport:steamengineinfo-button-ignite"), OnIgniteFuelClicked, igniteFuelButtonBounds, EnumButtonStyle.Normal, "btnIgniteFuel")
			.AddSmallButton(Lang.Get("yangtransport:steamengineinfo-button-putout"), OnCutFuelClicked, cutFuelButtonBounds, EnumButtonStyle.Normal, "btnCutFuel")
			.AddSmallButton(Lang.Get("yangtransport:steamengineinfo-button-dumpfluid"), OnDumpFluidClicked, dumpFluidButtonBounds, EnumButtonStyle.Normal, "btnFluidDump")

			.EndChildElements()
			.Compose();

		var slider = SingleComposer.GetSlider("tempSlider");
		if (slider != null)
		{
			slider.ShowTextWhenResting = false;
			slider.OnSliderRestingText = null;
			slider.OnSliderTooltip = null;
		}

		// Update water gauge/text when the client inventory sync changes slot one
		Inventory.SlotModified += OnInventorySlotModified;
		UpdateWaterUI();
		UpdateActionButtons();
		UpdateWarningUI();
		RefreshThermostatLockUI(force: true);
	}

	public override void OnGuiClosed()
	{
		Inventory.SlotModified -= OnInventorySlotModified;
		(SingleComposer?.GetElement("fuelSlotGrid") as GuiElementItemSlotGridBase)?.OnGuiClosed(capi);
		base.OnGuiClosed();
	}

	public override void OnFinalizeFrame(float deltaTime)
	{
		base.OnFinalizeFrame(deltaTime);
		if (!IsOpened()) { return; }

		if (!IsInInteractionRange()) { capi.Event.EnqueueMainThreadTask(() => TryClose(), "closedlg"); }
	}

	private bool IsInInteractionRange()
	{
		if (OwningEntity != null)
		{
			// Same range pattern as GUIDialogSgBoxcarStorage
			EntityPlayer playerEntity = capi.World.Player.Entity;
			Vec3d eyePosition = playerEntity.Pos.XYZ.Add(playerEntity.LocalEyePos);
			float interactionRange = capi.World.Player.WorldData.PickingRange;

			return OwningEntity.InRangeOf(eyePosition, interactionRange * interactionRange, interactionRange);
		}

		if (BlockEntityPosition != null) { return IsInRangeOfBlock(BlockEntityPosition); }
		return true;
	}

	private void OnTitleBarClose() => TryClose();

	private bool OnVentExcessClicked()	{ SendControlPacket(SteamEnginePacketIds.VentExcess, null); return true; }

	private bool OnIgniteFuelClicked()	{ SendControlPacket(SteamEnginePacketIds.IgniteFuel, null); return true; }

	private bool OnCutFuelClicked()		{ SendControlPacket(SteamEnginePacketIds.CutFuel, null); return true; }

	private bool OnDumpFluidClicked()	{ SendControlPacket(SteamEnginePacketIds.DumpFluid, null); return true; }

	private bool OnTargetTemperatureSliderChanged(int value)
	{
		if (UIThermostatLocked) return true;
		if (SuppressSliderPacket) return true;

		// GuiElementSlider already snaps to step values. Just send what it gives us.
		if (value == LastSentTemperatureValue) { return true; } LastSentTemperatureValue = value;

		// Immediate local feedback
		UpdateOutputStatus(value, Attributes.GetFloat("engineMaxTemperatureC", 0f));
		SendControlPacket(PacketIDSetTargetTemperature, BitConverter.GetBytes(value));

		return true;
	}

	private void OnAttributesModified()
	{
		if (SingleComposer == null) return;

		float temperatureC						= Attributes.GetFloat("temperatureC", 20f);
		float targetTemperatureC				= Attributes.GetFloat("limitTemperatureC", 0f);
		float remainingFuelBurnTimeSeconds		= Attributes.GetFloat("fuelBurnTime", 0f);
		float maximumFuelBurnTimeSeconds		= Attributes.GetFloat("maxFuelBurnTime", 0f);
		int fuelBurnTemperatureC				= Attributes.GetInt("fuelBurnTemperatureC", 0);
		float engineMaxTemperatureC				= Attributes.GetFloat("engineMaxTemperatureC", 0f);

		// Update flame icon fill + move the fuel info readout into the bottom-left panel.
		(SingleComposer.GetElement("fuelIcon") as GuiElementCustomDraw)?.Redraw();
		(SingleComposer.GetElement("dangerNotch") as GuiElementCustomDraw)?.Redraw();

		UpdateFuelDetailsUI();
		UpdateTempSummaryUI();
		UpdateWarningUI();
		
		UpdateWaterUI();
		RefreshThermostatLockUI();

		// Update slider (value + danger zone) only when changed
		var slider = SingleComposer.GetSlider("tempSlider");
		if (slider != null)
		{
			int newValue = (int)Math.Round(targetTemperatureC);
			if (newValue < SliderMin) newValue = SliderMin;
			if (newValue > SliderMax) newValue = SliderMax;

			// Do not let server sync fight the user while they're using the slider
			if (!slider.HasFocus && newValue != LastSliderValue)
			{
				LastSliderValue = newValue;
				SuppressSliderPacket = true;
				slider.SetValues(newValue, SliderMin, SliderMax, SliderStep, "°C");
				SuppressSliderPacket = false;
			}
		}

		UpdateOutputStatus((int)Math.Round(targetTemperatureC), engineMaxTemperatureC);
		UpdateActionButtons();
	}

	private void UpdateFuelDetailsUI()
	{
		if (SingleComposer == null) return;
		var fuelDetailsTextElement = SingleComposer.GetElement("fuelDetailsText") as GuiElementDynamicText;
		if (fuelDetailsTextElement == null) return;

		ItemSlot fuelSlot = Inventory?[0];
		float remainingFuelBurnTimeSeconds = Attributes.GetFloat("fuelBurnTime", 0f);
		float maximumFuelBurnTimeSeconds = Attributes.GetFloat("maxFuelBurnTime", 0f);
		int fuelBurnTemperatureAttributeC = Attributes.GetInt("fuelBurnTemperatureC", 0);
		bool canIgniteFuel = Attributes.GetBool("canIgniteFuel", false);

		if (fuelSlot == null || fuelSlot.Empty) { fuelDetailsTextElement.SetNewText(Lang.Get("yangtransport:steamengineinfo-fueltype", Lang.Get("game:Empty")), autoHeight: true); return; }

		string itemName = fuelSlot.Itemstack.GetName();
		var combustionProperties = fuelSlot.Itemstack.Collectible?.CombustibleProps;
		float burnTimePerPieceSeconds = (maximumFuelBurnTimeSeconds > 0f) ? maximumFuelBurnTimeSeconds : (combustionProperties?.BurnDuration ?? 0f) * Attributes.GetFloat("fuelDurationScale", 1f);
		int burnTemperatureC = (fuelBurnTemperatureAttributeC > 0) ? fuelBurnTemperatureAttributeC : (combustionProperties?.BurnTemperature ?? 0);

		// Current fuel time remaining = burn seconds left (0 if not actively burning)
		float currentBurnTimeRemainingSeconds = (remainingFuelBurnTimeSeconds > 0f && maximumFuelBurnTimeSeconds > 0f) ? remainingFuelBurnTimeSeconds : 0f;
		bool isBurning = currentBurnTimeRemainingSeconds > 0f;

		// Total remaining seconds (current partial piece + remaining stack pieces)
		int stackSize = fuelSlot.StackSize;
		double totalBurnTimeRemainingSeconds = 0;
		if (burnTimePerPieceSeconds > 0f) { totalBurnTimeRemainingSeconds = currentBurnTimeRemainingSeconds + stackSize * burnTimePerPieceSeconds; }

		int maximumStackSize = fuelSlot.Itemstack.Collectible?.MaxStackSize ?? 0;
		double stackPercentage = (maximumStackSize > 0) ? (stackSize / (double)maximumStackSize) * 100.0 : 0.0;

		string fuelTypeLine = Lang.Get("yangtransport:steamengineinfo-fueltype", itemName);
		if (isBurning) fuelTypeLine += ", " + Lang.Get("yangtransport:steamengineinfo-fuelburntime", FormatDuration(currentBurnTimeRemainingSeconds));

		string text = fuelTypeLine + "\n";
		if (!isBurning) text += (canIgniteFuel ? Lang.Get("yangtransport:steamengineinfo-fuellit") : Lang.Get("yangtransport:steamengineinfo-fuelunlit")) + "\n";
		text += Lang.Get("yangtransport:steamengineinfo-totalfuelleft", FormatDuration(totalBurnTimeRemainingSeconds), stackPercentage) + "\n" +
			Lang.Get("yangtransport:steamengineinfo-fuelburntemperature", burnTemperatureC);

		fuelDetailsTextElement.SetNewText(text, autoHeight: true);
	}

	private void UpdateTempSummaryUI()
	{
		if (SingleComposer == null) return;
		var temperatureInformationTextElement = SingleComposer.GetElement("tempInfoText") as GuiElementDynamicText;
		if (temperatureInformationTextElement == null) return;

		float temperatureC = Attributes.GetFloat("temperatureC", 20f);
		float targetTemperatureC = Attributes.GetFloat("limitTemperatureC", 20f);
		float redlineTemperatureC = Attributes.GetFloat("engineMaxTemperatureC", 0f);

		string text =
			Lang.Get("Temperature: {0:0}°C", temperatureC) + "\n" +
			Lang.Get("Target Temperature: {0:0}°C", targetTemperatureC) + "\n" +
			Lang.Get("Red Line Temperature: {0:0}°C", redlineTemperatureC);

		temperatureInformationTextElement.SetNewText(text, autoHeight: true);
	}

	private void UpdateOutputStatus(int targetTemperatureC, float engineMaxTemperatureC)
	{
		if (SingleComposer == null) return;

		var normalStatusElement = SingleComposer.GetElement("outputStatus") as GuiElementDynamicText;
		var overheatStatusElement = SingleComposer.GetElement("outputStatusRed") as GuiElementDynamicText;
		if (normalStatusElement == null || overheatStatusElement == null) return;

		normalStatusElement.SetNewText("");
		overheatStatusElement.SetNewText("");
		if (engineMaxTemperatureC <= 0f) return;

		double ratedMaxTemperatureC = engineMaxTemperatureC;
		const double comparisonTolerance = 0.01;

		if (UIThermostatLocked) { overheatStatusElement.SetNewText(Lang.Get("yangtransport:steamengineinfo-target-aflame")); return; }
		if (targetTemperatureC > ratedMaxTemperatureC + comparisonTolerance) { overheatStatusElement.SetNewText(Lang.Get("yangtransport:steamengineinfo-target-high")); return; }
		if (targetTemperatureC < ratedMaxTemperatureC - comparisonTolerance) { normalStatusElement.SetNewText(Lang.Get("yangtransport:steamengineinfo-target-low")); }
		else { normalStatusElement.SetNewText(Lang.Get("yangtransport:steamengineinfo-target-optimal")); }
	}

	// DANGER ZONE TRIANGLE. LONG LIVE THE INTERNATIONAL BRIGADES.
	private void OnDangerNotchDraw(Context context, ImageSurface surface, ElementBounds currentBounds)
	{
		float ratedMaxTemperatureC = Attributes.GetFloat("engineMaxTemperatureC", 0f);
		if (ratedMaxTemperatureC <= 0f) return;

		double relativePosition = (ratedMaxTemperatureC - SliderMin) / (double)(SliderMax - SliderMin);
		relativePosition = GameMath.Clamp(relativePosition, 0.0, 1.0);

		double x = relativePosition * currentBounds.InnerWidth;
		double h = currentBounds.InnerHeight;

		// Triangle pointing down (this is because it is sad that they broke through the Valencia barricades)
		double size = Math.Min(12.0, h);
		double baseY = 0.5;
		double tipY = Math.Min(h - 0.5, size);

		context.Save();
		context.SetSourceRGBA(1, 0.25, 0.25, 0.95);
		context.MoveTo(x - size / 2.0, baseY);
		context.LineTo(x + size / 2.0, baseY);
		context.LineTo(x, tipY);
		context.ClosePath();
		context.Fill();
		context.Restore();
	}

	private void OnInventorySlotModified(int slotID)
	{
		// Water changes arrive via inventory sync. Update gauge/text without rebuilding the whole dialog.
		capi.Event.EnqueueMainThreadTask(UpdateWaterUI, "steamengine-waterui");
		capi.Event.EnqueueMainThreadTask(UpdateActionButtons, "steamengine-actionsui");
		capi.Event.EnqueueMainThreadTask(UpdateFuelDetailsUI, "steamengine-fuelui");
		capi.Event.EnqueueMainThreadTask(UpdateTempSummaryUI, "steamengine-tempsummaryui");
		capi.Event.EnqueueMainThreadTask(UpdateWarningUI, "steamengine-warningui");
		if (slotID == 1) { capi.Event.EnqueueMainThreadTask(() => RefreshThermostatLockUI(), "steamengine-thermolockui"); }
	}

	#region Warning System
	private void UpdateWarningUI() // System that warns the player in order of priority about potential issues with the engine
	{
		if (SingleComposer == null) return;

		var titleElement = SingleComposer.GetElement("warnTitle") as GuiElementDynamicText;
		var dptionElement  = SingleComposer.GetElement("warnDesc") as GuiElementDynamicText;
		if (titleElement == null || dptionElement == null) return;

		// Read synced values
		float temperatureC						= Attributes.GetFloat("temperatureC", 20f);
		float targetTemperatureC				= Attributes.GetFloat("limitTemperatureC", 20f);
		float remainingFuelBurnTimeSeconds		= Attributes.GetFloat("fuelBurnTime", 0f);
		float maximumFuelBurnTimeSeconds		= Attributes.GetFloat("maxFuelBurnTime", 0f);
		bool isBurning							= remainingFuelBurnTimeSeconds > 0f && maximumFuelBurnTimeSeconds > 0f;
		bool canIgniteFuel						= Attributes.GetBool("canIgniteFuel", false);
		float engineMaxTemperatureC				= Attributes.GetFloat("engineMaxTemperatureC", 0f);

		bool hasFuel  = Inventory != null && Inventory.Count > 0 && !Inventory[0].Empty;
		bool hasFluid = Inventory != null && Inventory.Count > 1 && !Inventory[1].Empty;
		ItemStack? workingLiquid = Inventory?[1]?.Itemstack;

		/// CHECKS ///

		// Default State (no warnings) | From here we overwrite from low to high prio
		string title	= Lang.Get("yangtransport:steamenginewarn-normal-title");
		string desc		= Lang.Get("yangtransport:steamenginewarn-normal-desc");

		if (!isBurning) // Engine ready for operation, but not lit
		{
			title	= Lang.Get("yangtransport:steamenginewarn-unlit-title");
			desc	= Lang.Get("yangtransport:steamenginewarn-unlit-desc");
		}

		if (!hasFluid) // No liquid
		{
			title	= Lang.Get("yangtransport:steamenginewarn-missingfluid-title");
			desc	= Lang.Get("yangtransport:steamenginewarn-missingfluid-desc");
		}

		if (!hasFuel) // No fuel
		{
			title	= Lang.Get("yangtransport:steamenginewarn-missingfuel-title");
			desc	= Lang.Get("yangtransport:steamenginewarn-missingfuel-desc");
		}

		if (hasFluid && !SteamEngineController.IsValidWorkingLiquid(workingLiquid)) // Liquid is not fresh water
		{
			title	= Lang.Get("yangtransport:steamenginewarn-badwater-title");
			desc	= Lang.Get("yangtransport:steamenginewarn-badwater-desc", workingLiquid.GetName());

			if (SteamEngineController.IsLiquidFlammable(workingLiquid)) // Liquid is flamable
			{

				if (isBurning) // THE WATER IS ON FIRE!!
				{
					title	= Lang.Get("yangtransport:steamenginewarn-fluidfire-title");
					desc	= Lang.Get("yangtransport:steamenginewarn-fluidfire-desc");
				}
				else // Hold on maybe don't use flammable liquid as working fluid
				{
					title	= Lang.Get("yangtransport:steamenginewarn-explosivewater-title");
					desc	= Lang.Get("yangtransport:steamenginewarn-explosivewater-desc", workingLiquid.GetName());
				}
			}
		}

		// Past redline (ignore warning if we've lost control)
		if (!UIThermostatLocked && targetTemperatureC > engineMaxTemperatureC + 1f)
		{
			title	= Lang.Get("yangtransport:steamenginewarn-redline-title");
			desc	= Lang.Get("yangtransport:steamenginewarn-redline-desc");
		}

		// WE ARE ABOUT TO EXPLODE!
		if (temperatureC > engineMaxTemperatureC)
		{
			title	= Lang.Get("yangtransport:steamenginewarn-overload-title");
			desc	= Lang.Get("yangtransport:steamenginewarn-overload-desc", ((temperatureC - engineMaxTemperatureC) / 100));
		}

		// Push to UI
		titleElement.SetNewText(title, autoHeight: true);
		dptionElement.SetNewText(desc, autoHeight: true);
	}
	#endregion
	
	private void UpdateWaterUI()
	{
		if (SingleComposer == null) return;
		(SingleComposer.GetElement("waterBar") as GuiElementCustomDraw)?.Redraw();
		UpdateWaterText();
	}

	private void UpdateActionButtons()
	{
		if (SingleComposer == null) return;

		var ventExcessButton					= SingleComposer.GetButton("btnVentExcess");
		var igniteFuelButton					= SingleComposer.GetButton("btnIgniteFuel");
		var cutFuelButton						= SingleComposer.GetButton("btnCutFuel");

		float temperatureC						= Attributes.GetFloat("temperatureC", 20f);
		float targetTemperatureC				= Attributes.GetFloat("limitTemperatureC", 20f);
		float remainingFuelBurnTimeSeconds		= Attributes.GetFloat("fuelBurnTime", 0f);
		bool isBurning							= remainingFuelBurnTimeSeconds > 0f;
		bool canIgniteFuel						= Attributes.GetBool("canIgniteFuel", false);

		// Vent Excess, only if excess to vent
		if (ventExcessButton != null) ventExcessButton.Enabled = temperatureC > targetTemperatureC + 0.5f;

		// Ignite Fuel. Survival needs some shit, in creative you can bypass it and get free ligma coal.
		if (igniteFuelButton != null)
		{
			bool hasFuel = !Inventory[0].Empty;
			bool hasWater = !Inventory[1].Empty;
			var player = capi.World.Player;
			bool isCreative = player?.WorldData?.CurrentGameMode == EnumGameMode.Creative;
			ItemStack? mainHand = player?.InventoryManager?.ActiveHotbarSlot?.Itemstack;
			ItemStack? offHand  = player?.Entity?.LeftHandItemSlot?.Itemstack;
			bool hasIgnitionSource = SteamEngineController.IsIgnitionSource(mainHand) || SteamEngineController.IsIgnitionSource(offHand);
			bool canCreativePrime = isCreative && !hasFuel && !hasWater;
			igniteFuelButton.Enabled = !canIgniteFuel && !isBurning && ((hasFuel && (hasIgnitionSource || isCreative)) || canCreativePrime);
		}

		// Cut Fuel. Only while burning, and only if enough liquid is present to pay the cost
		if (cutFuelButton != null) { cutFuelButton.Enabled = isBurning && HasEnoughLiquidForCut(temperatureC); }
	}

	private bool HasEnoughLiquidForCut(float temperatureC)
	{
		if (temperatureC <= 0f) return true;
		float requiredLitres = temperatureC / 100f;

		ItemSlot liquidSlot = Inventory[1];
		if (liquidSlot == null || liquidSlot.Empty) return false;

		WaterTightContainableProps containableProperties = BlockLiquidContainerBase.GetContainableProps(liquidSlot.Itemstack);
		if (containableProperties == null) return false;

		float itemsPerLitre = containableProperties.ItemsPerLitre <= 0 ? 1f : containableProperties.ItemsPerLitre;
		float availableLitres = liquidSlot.StackSize / itemsPerLitre;

		return availableLitres + 1e-4f >= requiredLitres;
	}
	
	private void UpdateWaterText()
	{
		if (SingleComposer == null) return;
		
		ItemSlot liquidOnlySlot = Inventory[1];
		float capacityLitres = 0f;
		if (liquidOnlySlot is ItemSlotLiquidOnly liq) capacityLitres = liq.CapacityLitres;
		
		float litres = 0f;
		string liquidName = Lang.Get("game:Empty");
		double timeRemainingSeconds = 0;
		
		if (!liquidOnlySlot.Empty)
		{
			WaterTightContainableProps containableProperties = BlockLiquidContainerBase.GetContainableProps(liquidOnlySlot.Itemstack);
			float itemsPerLitre = containableProperties?.ItemsPerLitre ?? 1f;
			litres = liquidOnlySlot.StackSize / itemsPerLitre;
			liquidName = liquidOnlySlot.Itemstack.GetName();

			float consumptionLitresPerHour = Attributes.GetFloat("workingFluidConsumptionLPerHour");
			if (consumptionLitresPerHour > 0f) timeRemainingSeconds = litres / consumptionLitresPerHour * 3600.0;
		}
		
		string text =
			Lang.Get("yangtransport:steamengineinfo-literage", litres, capacityLitres, liquidName) + "\n" +
			Lang.Get("yangtransport:steamengineinfo-evaporationrate", Attributes.GetFloat("workingFluidConsumptionLPerHour")) + "\n" +
			Lang.Get("yangtransport:steamengineinfo-evaporationtimeleft", FormatDuration(timeRemainingSeconds));
			
		(SingleComposer.GetElement("waterText") as GuiElementDynamicText)?.SetNewText(text, autoHeight: true);
	}

	private static string FormatDuration(double seconds)
	{
		long totalSeconds = Math.Max(0, (long)Math.Ceiling(seconds));
		long hours = totalSeconds / 3600;
		long minutes = totalSeconds / 60 % 60;
		long remainingSeconds = totalSeconds % 60;

		return hours > 0 ? $"{hours}:{minutes:00}:{remainingSeconds:00}" : $"{minutes}:{remainingSeconds:00}";
	}

	/// Barrel-style liquid fill meter
	private void OnWaterMeterDraw(Context context, ImageSurface surface, ElementBounds currentBounds)
	{
		ItemSlot liquidSlot = Inventory[1];
		if (liquidSlot.Empty) return;
		
		WaterTightContainableProps containableProperties = BlockLiquidContainerBase.GetContainableProps(liquidSlot.Itemstack);
		if (containableProperties == null) return;
		
		float itemsPerLitre = containableProperties.ItemsPerLitre <= 0 ? 1f : containableProperties.ItemsPerLitre;
		
		float capacityLitres = 0f;
		if (liquidSlot is ItemSlotLiquidOnly liquidOnlySlot) capacityLitres = liquidOnlySlot.CapacityLitres;
		if (capacityLitres <= 0f) capacityLitres = containableProperties.MaxStackSize / itemsPerLitre;
		if (capacityLitres <= 0f) return;
		
		float litres = liquidSlot.StackSize / itemsPerLitre;
		float fullnessRelative = GameMath.Clamp(litres / capacityLitres, 0f, 1f);
		
		double verticalOffset = (1f - fullnessRelative) * currentBounds.InnerHeight;
		context.Rectangle(0.0, verticalOffset, currentBounds.InnerWidth, currentBounds.InnerHeight - verticalOffset);
		
		object textureObject = containableProperties.Texture;
		if (textureObject == null)
		{
			JsonObject attributes = liquidSlot.Itemstack.Collectible.Attributes;
			textureObject = attributes?["inContainerTexture"].AsObject<CompositeTexture>(null, liquidSlot.Itemstack.Collectible.Code.Domain);
		}
		
		CompositeTexture texture = textureObject as CompositeTexture;
		if (texture == null) return;
		
		context.Save();
		Matrix transformationMatrix = context.Matrix;
		transformationMatrix.Scale(GuiElement.scaled(3.0), GuiElement.scaled(3.0));
		context.Matrix = transformationMatrix;
		
		AssetLocation textureLocation = texture.Base.Clone().WithPathAppendixOnce(".png");
		GuiElement.fillWithPattern(capi, context, textureLocation, true, false, texture.Alpha, 1f);
		
		context.Restore();
	}

	// Copied from vanilla firepit OnBgDraw() (fuel flame part) but adapted to fit
	private void OnFuelIconDraw(Context context, ImageSurface surface, ElementBounds currentBounds)
	{
		float remainingFuelBurnTimeSeconds = Attributes.GetFloat("fuelBurnTime", 0f);
		float maximumFuelBurnTimeSeconds = Attributes.GetFloat("maxFuelBurnTime", 0f);

		// Avoid div-by-zero - Show empty flame if not burning.
		float fuelRemainingFraction = (remainingFuelBurnTimeSeconds > 0 && maximumFuelBurnTimeSeconds > 0) ? remainingFuelBurnTimeSeconds / maximumFuelBurnTimeSeconds : 0f;

		context.Save();

		// IMPORTANT: CustomDraw ctx is already in element space. Draw from (0,0). Fit the 200x210 flame art into our bounds and center it.
		double w = currentBounds.InnerWidth;
		double h = currentBounds.InnerHeight;
		double scale = Math.Min(w / 200.0, h / 210.0);
		double ox = (w - 200.0 * scale) / 2.0;
		double oy = (h - 210.0 * scale) / 2.0;
		Matrix m = context.Matrix;
		m.Translate(ox, oy);
		m.Scale(scale, scale);
		context.Matrix = m;

		// Outline
		capi.Gui.Icons.DrawFlame(context, 3.0, true, true);

		// Fill proportional to remaining fuel
		double dy = 210f - 210f * fuelRemainingFraction;
		context.Rectangle(0.0, dy, 200.0, 210.0 - dy);
		context.Clip();

		LinearGradient gradient = new LinearGradient(0.0, 250.0, 0.0, 0.0);
		gradient.AddColorStop(0.0, new Color(1.0, 1.0, 0.0, 1.0));
		gradient.AddColorStop(1.0, new Color(1.0, 0.0, 0.0, 1.0));
		context.SetSource(gradient);

		capi.Gui.Icons.DrawFlame(context, 0.0, false, false);

		gradient.Dispose();
		context.Restore();
	}

	#region Temperature Governor Limiter
	private bool ComputeThermostatLocked()
	{
		ItemStack stack = Inventory?[1]?.Itemstack;
		return stack != null && SteamEngineController.IsLiquidFlammable(stack);
	}

	private void RefreshThermostatLockUI(bool force = false)
	{
		if (SingleComposer == null) return;
		var slider = SingleComposer.GetSlider("tempSlider");
		if (slider == null) return;

		bool lockedNow = ComputeThermostatLocked();
		if (!force && lockedNow == UIThermostatLocked) return;

		UIThermostatLocked = lockedNow;

		// Disable/enable interaction like buttons do
		slider.Enabled = !lockedNow;

		// Only on transition into locked: snap to max once (no packet spam)
		if (lockedNow)
		{
			SuppressSliderPacket = true;
			slider.SetValues(SliderMax, SliderMin, SliderMax, SliderStep, "°C");
			SuppressSliderPacket = false;
			LastSliderValue = SliderMax;
		}
	}
	#endregion
}
