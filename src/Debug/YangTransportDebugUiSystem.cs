using System;
using Cairo;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.CommandAbbr;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace YangTransport;

[ProtoContract] public sealed class YangTransportDangerousDebugLog				{ [ProtoMember(1)] public string Message = ""; }
[ProtoContract] public sealed class YangTransportForceMaterializeNearRequest	{ [ProtoMember(1)] public bool Requested = true; }
[ProtoContract] public sealed class YangTransportForceMaterializeNearResult
{
	[ProtoMember(1)] public long[] MaterializedHeadIDs		= Array.Empty<long>();
	[ProtoMember(2)] public double[] MaterializedPositions	= Array.Empty<double>();
	[ProtoMember(3)] public long[] FailedHeadIDs			= Array.Empty<long>();
	[ProtoMember(4)] public int Dimension;
	[ProtoMember(5)] public string Error = "";
}
public sealed class YangTransportDebugUISystem : ModSystem
{
	private const string ChannelName = "yangtransport_debugui";

	private ICoreClientAPI? ClientAPI;
	private IClientNetworkChannel? ClientNetworkChannel;
	private YangTransportDebugDialog? Dialog;
	private ForceMaterializeGuideRenderer? MaterializeGuideRenderer;

	internal bool DangerousEnabled { get; private set; }

	public override bool ShouldLoad(EnumAppSide forSide) => true;

	public override void StartServerSide(ICoreServerAPI serverAPI)
	{
		var channel = serverAPI.Network.RegisterChannel(ChannelName)
			.RegisterMessageType<YangTransportDangerousDebugLog>()
			.RegisterMessageType<YangTransportForceMaterializeNearRequest>()
			.RegisterMessageType<YangTransportForceMaterializeNearResult>();

		channel.SetMessageHandler<YangTransportDangerousDebugLog>((player, packet) =>
		{
			string message = $"[Yangtransport] {player.PlayerName} {packet.Message}";
			serverAPI.Logger.Notification(message);
			serverAPI.Logger.Audit(message);
		});
		channel.SetMessageHandler<YangTransportForceMaterializeNearRequest>((player, _) =>
		{
			var materializedHeadIDs = new System.Collections.Generic.List<long>();
			var materializedPositions = new System.Collections.Generic.List<Vec3d>();
			var failedHeadIDs = new System.Collections.Generic.List<long>();
			bool ready = serverAPI.ModLoader.GetModSystem<OffscreenConvoySimSystem>().DebugForceMaterializeNear
			(
				player.Entity.ServerPos, 4, materializedHeadIDs, materializedPositions, failedHeadIDs
			);

			double[] packedPositions = new double[materializedPositions.Count * 3];
			for (int positionIndex = 0; positionIndex < materializedPositions.Count; positionIndex++)
			{
				Vec3d position = materializedPositions[positionIndex];
				int offset = positionIndex * 3;
				packedPositions[offset] = position.X;
				packedPositions[offset + 1] = position.Y;
				packedPositions[offset + 2] = position.Z;
			}

			channel.SendPacket
			(
				new YangTransportForceMaterializeNearResult
				{
					MaterializedHeadIDs = materializedHeadIDs.ToArray(),
					MaterializedPositions = packedPositions,
					FailedHeadIDs = failedHeadIDs.ToArray(),
					Dimension = player.Entity.ServerPos.Dimension,
					Error = ready ? "" : "Force Materialize Near failed because the rail simulation is not ready."
				},
				player
			);
		});
	}

	public override void StartClientSide(ICoreClientAPI clientAPI)
	{
		ClientAPI = clientAPI;
		ClientNetworkChannel = clientAPI.Network.RegisterChannel(ChannelName)
			.RegisterMessageType<YangTransportDangerousDebugLog>()
			.RegisterMessageType<YangTransportForceMaterializeNearRequest>()
			.RegisterMessageType<YangTransportForceMaterializeNearResult>();

		ClientNetworkChannel.SetMessageHandler<YangTransportForceMaterializeNearResult>(OnForceMaterializeNearResult);

		clientAPI.ChatCommands
			.Create("yttdebug")
			.RequiresPrivilege(Privilege.controlserver)
			.WithDescription("Open the YangTransport debug functionality control panel.")
			.HandleWith(OnDebugUICommand);
	}

	public override void Dispose()
	{
		Dialog?.TryClose();
		Dialog = null;
		MaterializeGuideRenderer?.Dispose();
		MaterializeGuideRenderer = null;
		base.Dispose();
	}

	private TextCommandResult OnDebugUICommand(TextCommandCallingArgs commandArguments)
	{
		if (ClientAPI == null) return TextCommandResult.Error("Client API is not initialized.");

		if (Dialog != null)
		{
			return Dialog.TryClose()
				? TextCommandResult.Success()
				: TextCommandResult.Error("Unable to close the YangTransport debug controls.");
		}

		YangTransportDebugDialog openedDialog = new
		(
			ClientAPI,
			this,
			ClientAPI.ModLoader.GetModSystem<RailSystemDebug>(),
			ClientAPI.ModLoader.GetModSystem<CartMapDebugSystem>(),
			ClientAPI.ModLoader.GetModSystem<TrainOverlapDebugSystem>(),
			ClientAPI.ModLoader.GetModSystem<StandardGaugeAttachmentPointDebugSystem>(),
			ClientAPI.ModLoader.GetModSystem<RailGraphClientSystem>()
		);

		Dialog = openedDialog;
		openedDialog.OnClosed += () => { if (ReferenceEquals(Dialog, openedDialog)) Dialog = null; };

		if (!openedDialog.TryOpen())
		{
			Dialog = null;
			return TextCommandResult.Error("Unable to open the YangTransport debug controls.");
		}

		return TextCommandResult.Success();
	}

	private void OnForceMaterializeNearResult(YangTransportForceMaterializeNearResult packet)
	{
		if (ClientAPI == null || packet == null) return;

		MaterializeGuideRenderer?.Dispose();
		MaterializeGuideRenderer = null;
		if (!string.IsNullOrWhiteSpace(packet.Error)) { ClientAPI.ShowChatMessage(packet.Error); return; }

		long[] materializedHeadIDs = packet.MaterializedHeadIDs ?? Array.Empty<long>();
		double[] positions = packet.MaterializedPositions ?? Array.Empty<double>();
		long[] failedHeadIDs = packet.FailedHeadIDs ?? Array.Empty<long>();

		for (int convoyIndex = 0; convoyIndex < materializedHeadIDs.Length; convoyIndex++) // Compacted this a bit ugly but it's better than spanning unecessarily
		{ ClientAPI.ShowChatMessage($"Forced convoy {materializedHeadIDs[convoyIndex]} to materialize."); }
		for (int convoyIndex = 0; convoyIndex < failedHeadIDs.Length; convoyIndex++)
		{ ClientAPI.ShowChatMessage($"Failed to safely materialize convoy {failedHeadIDs[convoyIndex]}. Check logs."); }
		if (materializedHeadIDs.Length == 0 && failedHeadIDs.Length == 0)
		{ ClientAPI.ShowChatMessage("No virtual convoys found within 4 chunks."); return; }

		if (materializedHeadIDs.Length == 0 || positions.Length != materializedHeadIDs.Length * 3) return;

		Vec3d[] targets = new Vec3d[materializedHeadIDs.Length];
		for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
		{
			int offset = targetIndex * 3;
			targets[targetIndex] = new Vec3d(positions[offset], positions[offset + 1], positions[offset + 2]);
		}

		MaterializeGuideRenderer = new ForceMaterializeGuideRenderer(ClientAPI, targets, packet.Dimension);
	}

	private sealed class ForceMaterializeGuideRenderer : IRenderer, IDisposable
	{
		private const long DurationMS = 15000;
		private static readonly int GuideColor = NerdToolTrackHighlightRenderer.HighlightColor(255, 64, 255, 64);

		private readonly ICoreClientAPI ClientAPI;
		private readonly Vec3d Origin;
		private readonly MeshData GuideMeshData;
		private readonly MeshRef GuideMesh;
		private readonly Matrixf ModelView = new();
		private readonly int Dimension;
		private readonly long ExpiresMS;
		private bool Disposed;

		public double RenderOrder => 0.9; public int RenderRange => 9999;
		internal ForceMaterializeGuideRenderer(ICoreClientAPI clientAPI, Vec3d[] targets, int dimension)
		{
			ClientAPI = clientAPI;
			Origin = targets[0];
			Dimension = dimension;
			ExpiresMS = clientAPI.World.ElapsedMilliseconds + DurationMS;

			GuideMeshData = new MeshData(targets.Length * 2, targets.Length * 2, withNormals: false, withUv: false);
			GuideMeshData.SetMode(EnumDrawMode.Lines);
			GuideMeshData.XyzStatic = false; GuideMeshData.RgbaStatic = true; GuideMeshData.IndicesStatic = true;

			for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
			{
				Vec3d target = targets[targetIndex];
				int vertex = GuideMeshData.VerticesCount;
				GuideMeshData.AddVertexSkipTex(0, 0, 0, GuideColor); GuideMeshData.AddIndex(vertex);
				GuideMeshData.AddVertexSkipTex((float)(target.X - Origin.X), (float)(target.Y - Origin.Y), (float)(target.Z - Origin.Z), GuideColor);
				GuideMeshData.AddIndex(vertex + 1);
			}

			GuideMesh = clientAPI.Render.UploadMesh(GuideMeshData);
			clientAPI.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "yangtransport:force-materialize-guide");
		}

		public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
		{
			if (Disposed) return;
			if (ClientAPI.World.ElapsedMilliseconds >= ExpiresMS) { Dispose(); return; }

			IClientPlayer? player = ClientAPI.World.Player;
			if (player?.Entity == null || player.Entity.Pos.Dimension != Dimension || ClientAPI.HideGuis) return;

			Vec3d cameraPosition = player.Entity.CameraPos;
			Vec3f cameraForward = EntityPos.GetViewVector(player.CameraPitch, player.CameraYaw);
			float startX = (float)(cameraPosition.X + cameraForward.X - Origin.X);
			float startY = (float)(cameraPosition.Y + cameraForward.Y - Origin.Y);
			float startZ = (float)(cameraPosition.Z + cameraForward.Z - Origin.Z);

			float[] coordinates = GuideMeshData.xyz;
			for (int lineIndex = 0; lineIndex < GuideMeshData.VerticesCount / 2; lineIndex++)
			{
				int offset = lineIndex * 6;
				coordinates[offset] = startX;
				coordinates[offset + 1] = startY;
				coordinates[offset + 2] = startZ;
			}
			ClientAPI.Render.UpdateMesh(GuideMesh, GuideMeshData);

			IShaderProgram? previousShader = ClientAPI.Render.CurrentActiveShader;
			previousShader?.Stop();

			IShaderProgram shader = ClientAPI.Render.GetEngineShader(EnumShaderProgram.Autocamera);
			shader.Use();
			shader.UniformMatrix("projectionMatrix", ClientAPI.Render.CurrentProjectionMatrix);
			shader.UniformMatrix
			(
				"modelViewMatrix",
				ModelView.Set(ClientAPI.Render.CameraMatrixOriginf)
					.Translate(Origin.X - cameraPosition.X, Origin.Y - cameraPosition.Y, Origin.Z - cameraPosition.Z).Values
			);

			try
			{
				ClientAPI.Render.GlDisableCullFace();
				ClientAPI.Render.GlToggleBlend(true);
				ClientAPI.Render.GLDepthMask(false);
				ClientAPI.Render.LineWidth = 2.5f;
				ClientAPI.Render.RenderMesh(GuideMesh);
			}
			finally
			{
				ClientAPI.Render.LineWidth = 1f;
				ClientAPI.Render.GLDepthMask(true);
				ClientAPI.Render.GlToggleBlend(false);
				ClientAPI.Render.GlEnableCullFace();
			}

			shader.Stop();
			previousShader?.Use();
		}

		public void Dispose()
		{
			if (Disposed) return;
			
			Disposed = true;
			ClientAPI.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
			ClientAPI.Render.DeleteMesh(GuideMesh);
		}
	}

	internal void SetDangerousEnabled(bool enabled)
	{
		if (enabled == DangerousEnabled) return;
		
		DangerousEnabled = enabled;
		LogDangerous($"toggled {(enabled ? "on" : "off")} the dangerous debug functionality button.");
	}

	internal void FireForceMaterializeNear()
	{
		if (!DangerousEnabled || ClientNetworkChannel == null) return;

		LogDangerous("fired the following dangerous debug function: \"Force Materialize Near\"");
		ClientNetworkChannel.SendPacket(new YangTransportForceMaterializeNearRequest());
	}

	internal void FireDeleteAllTransportEntities()
	{
		if (!DangerousEnabled || ClientAPI == null) return;

		LogDangerous("fired the following dangerous debug function: \"Delete All Transport Entities\"");
		ClientAPI.SendChatMessage("/entity remove e[type=yangtransport:*]");
	}

	private void LogDangerous(string message) { ClientNetworkChannel?.SendPacket(new YangTransportDangerousDebugLog { Message = message }); }
}

internal sealed class YangTransportDebugDialog : GuiDialogGeneric
{
	private const double ContentWidth = 340;
	private const double ViewportHeight = 440;
	private const double ButtonHeight = 38;
	private const double RowStride = 44;
	private const double ScrollbarGap = 6;

	private readonly YangTransportDebugUISystem DebugUISystem;
	private readonly DebugControl[] Controls;
	private double ScrollPositionY;
	private bool SuppressScrollEvents;
	private bool RecomposeAfterScrollbarDrag;
	private bool CloseQueued;

	public override double DrawOrder => 0.2;

	internal YangTransportDebugDialog
	(
		ICoreClientAPI clientAPI,
		YangTransportDebugUISystem debugUISystem,
		RailSystemDebug railDebug,
		CartMapDebugSystem cartMapDebug,
		TrainOverlapDebugSystem overlapDebug,
		StandardGaugeAttachmentPointDebugSystem attachmentPointDebug,
		RailGraphClientSystem railGraphClient
	) : base("YangTransport Debug Functionality", clientAPI)
	{
		this.DebugUISystem = debugUISystem;

		Controls = new[]
		{
			DebugControl.Toggle("Show Rail Graph", () => railDebug.DetailedGraphEnabled, railDebug.SetDetailedGraphEnabled),
			DebugControl.Toggle("Show Rail Grid", () => railDebug.GridEnabled, railDebug.SetGridEnabled),
			DebugControl.Action("Rebuild Local Graph Area", () => RebuildLocalGraph(railGraphClient)),
			DebugControl.Toggle("Enable Cart Map", () => cartMapDebug.Enabled, enabled => cartMapDebug.SetEnabled(enabled)),
			DebugControl.Toggle("Show Signals", () => railDebug.SignalDebugEnabled, railDebug.SetSignalDebugEnabled),
			DebugControl.Toggle("Show Automation", () => railDebug.AutomationDebugEnabled, railDebug.SetAutomationDebugEnabled),
			DebugControl.Toggle("Show Collisions", () => overlapDebug.Enabled, overlapDebug.SetEnabled),
			DebugControl.Toggle("Show Clearance", () => railDebug.ClearanceDebugEnabled, railDebug.SetClearanceDebugEnabled),
			DebugControl.Toggle("SG Size Estimates", () => railDebug.SGSizeEstimateEnabled, railDebug.SetSGSizeEstimateEnabled),
			DebugControl.Toggle("Show Custom AP", () => attachmentPointDebug.Enabled, attachmentPointDebug.SetEnabled),
			DebugControl.Toggle("Show Chunk Boundaries", GetServerChunkWireframe, SetServerChunkWireframe),
			DebugControl.Toggle("Entity Wireframes", GetEntityWireframes, SetEntityWireframes),
			DebugControl.Toggle("Entity SelectionBoxes", GetEntitySelectionBoxes, SetEntitySelectionBoxes)
		};
	}

	private int DisplayedControlCount => Controls.Length + 1 + (DebugUISystem.DangerousEnabled ? 2 : 0);
	private int VisibleRowCapacity => (int)Math.Floor(ViewportHeight / RowStride);

	public override void OnGuiOpened()
	{
		Compose();
		base.OnGuiOpened();
	}

	private void Compose()
	{
		double titleBarHeight = GuiStyle.TitleBarHeight;
		double scrollbarWidth = GuiElementScrollbar.DefaultScrollbarWidth;
		double buttonWidth = ContentWidth - scrollbarWidth - ScrollbarGap;
		int displayedControlCount = DisplayedControlCount;

		ElementBounds backgroundBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
		backgroundBounds.BothSizing = ElementSizing.FitToChildren;

		ElementBounds contentBounds = ElementBounds
			.Fixed(0, titleBarHeight, ContentWidth, ViewportHeight)
			.WithParent(backgroundBounds);

		backgroundBounds.WithChildren(contentBounds);

		SingleComposer?.Dispose();
		GuiComposer composer = capi.Gui
			.CreateCompo("yangtransport-debug-controls", ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle))
			.AddShadedDialogBG(backgroundBounds)
			.AddDialogTitleBar("YangTransport Debug Functionality", () => TryClose())
			.BeginChildElements(backgroundBounds);

		int maximumStartIndex = Math.Max(0, displayedControlCount - VisibleRowCapacity);
		int firstVisibleIndex = GameMath.Clamp((int)(ScrollPositionY / RowStride), 0, maximumStartIndex);
		int visibleRowCount = Math.Min(displayedControlCount - firstVisibleIndex, (int)Math.Ceiling(ViewportHeight / RowStride));

		for (int rowIndex = 0; rowIndex < visibleRowCount; rowIndex++)
		{
			int controlIndex = firstVisibleIndex + rowIndex;
			ElementBounds buttonBounds = ElementBounds.Fixed(0, rowIndex * RowStride, buttonWidth, ButtonHeight).WithParent(contentBounds);

			if (controlIndex < Controls.Length)
			{
				DebugControl control = Controls[controlIndex];
				int capturedIndex = controlIndex;
				composer.AddToggleButton
				(
					control.Label,
					CairoFont.WhiteSmallishText(),
					enabled => OnControlSelected(capturedIndex, enabled),
					buttonBounds,
					ControlKey(controlIndex)
				);
			}
			else if (controlIndex == Controls.Length) { AddDangerousArmButton(composer, buttonBounds); }
			else
			{
				bool forceMaterialize = controlIndex == Controls.Length + 1;
				composer.AddToggleButton
				(
					forceMaterialize ? "Force Materialize Near" : "Delete All Transport Entities",
					CairoFont.WhiteSmallishText(),
					_ => { if (forceMaterialize) OnForceMaterializeNear(); else OnDeleteAllTransportEntities(); },
					buttonBounds
				);
			}
		}

		ElementBounds scrollbarBounds = ElementBounds
			.Fixed(buttonWidth + ScrollbarGap, 0, scrollbarWidth, ViewportHeight)
			.WithParent(contentBounds);
		composer.AddVerticalScrollbar(OnScrollChanged, scrollbarBounds, "debugScrollbar");

		SingleComposer = composer.EndChildElements().Compose(focusFirstElement: false);

		ConfigureScrollbar();
		RefreshToggleStates(firstVisibleIndex, visibleRowCount);
	}

	private void ConfigureScrollbar()
	{
		GuiElementScrollbar? scrollbar = SingleComposer?.GetScrollbar("debugScrollbar");
		if (scrollbar == null) return;

		SuppressScrollEvents = true;
		scrollbar.SetHeights((float)ViewportHeight, (float)(DisplayedControlCount * RowStride));
		scrollbar.CurrentYPosition = (float)ScrollPositionY;
		scrollbar.RecomposeHandle();
		SuppressScrollEvents = false;
	}

	private void RefreshToggleStates(int firstVisibleIndex, int visibleRowCount)
	{
		if (SingleComposer == null) return;

		for (int rowIndex = 0; rowIndex < visibleRowCount; rowIndex++)
		{
			int controlIndex = firstVisibleIndex + rowIndex;
			if (controlIndex >= Controls.Length) continue;

			DebugControl control = Controls[controlIndex];
			SingleComposer
				.GetToggleButton(ControlKey(controlIndex))
				.SetValue(control.IsToggle && control.Get!());
		}
	}

	private void OnControlSelected(int controlIndex, bool enabled)
	{
		DebugControl control = Controls[controlIndex];

		if (control.IsToggle)	{ control.Set!(enabled); }
		else					{ control.Run!(); }

		QueueClose();
	}

	private void AddDangerousArmButton(GuiComposer composer, ElementBounds bounds)
	{
		var button = new DangerousToggleButton
		(
			capi,
			DebugUISystem.DangerousEnabled ? "Disarm Dangerous Functions" : "Enable Dangerous Functions",
			CairoFont.WhiteSmallishText(),
			OnDangerousArmSelected,
			bounds
		);
		button.SetValue(DebugUISystem.DangerousEnabled);
		composer.AddInteractiveElement(button);
	}

	private void OnDangerousArmSelected(bool enabled)
	{
		DebugUISystem.SetDangerousEnabled(enabled);
		double maximumScrollPosition = Math.Max(0, (DisplayedControlCount - VisibleRowCapacity) * RowStride);
		ScrollPositionY = enabled ? maximumScrollPosition : Math.Min(ScrollPositionY, maximumScrollPosition);
		capi.Event.EnqueueMainThreadTask(() => { if (IsOpened()) Compose(); }, "yangtransport-debug-dangerous-toggle");
	}

	private void OnForceMaterializeNear()
	{
		DebugUISystem.FireForceMaterializeNear();
		QueueClose();
	}

	private void OnDeleteAllTransportEntities()
	{
		DebugUISystem.FireDeleteAllTransportEntities();
		QueueClose();
	}

	private void QueueClose()
	{
		if (CloseQueued) return;

		CloseQueued = true;
		capi.Event.EnqueueMainThreadTask
		(
			() =>
			{
				CloseQueued = false;
				if (IsOpened()) { TryClose(); }
			},
			"yangtransport-debug-dialog-close"
		);
	}

	private void OnScrollChanged(float scrollPosition)
	{
		if (SuppressScrollEvents) return;
		ScrollPositionY = scrollPosition;

		if (IsScrollbarDragging())
		{
			RecomposeAfterScrollbarDrag = true;
			return;
		}

		Compose();
	}

	public override void OnMouseUp(MouseEvent mouseEvent)
	{
		base.OnMouseUp(mouseEvent);

		if (!RecomposeAfterScrollbarDrag || !IsOpened()) return;

		RecomposeAfterScrollbarDrag = false;
		Compose();
	}

	private bool IsScrollbarDragging() { return SingleComposer?.GetScrollbar("debugScrollbar")?.mouseDownOnScrollbarHandle == true; }

	private bool RebuildLocalGraph(RailGraphClientSystem railGraphClient)
	{
		BlockPos? rebuildCenterPosition = capi.World.Player?.Entity?.Pos?.AsBlockPos;
		if (rebuildCenterPosition == null) return false;

		railGraphClient.RequestLocalRebuild(rebuildCenterPosition);
		capi.ShowChatMessage("Requested local rail graph rebuild.");
		return true;
	}

	private bool GetServerChunkWireframe() => capi.Render.WireframeDebugRender.ServerChunk;
	private void SetServerChunkWireframe(bool enabled) { capi.Render.WireframeDebugRender.ServerChunk = enabled; }
	private bool GetEntityWireframes() => capi.Render.WireframeDebugRender.Entity;
	private void SetEntityWireframes(bool enabled) { capi.Render.WireframeDebugRender.Entity = enabled; }
	private bool GetEntitySelectionBoxes() { return capi.Settings.Bool.Get("debugEntitySelectionBoxes", false); }
	private void SetEntitySelectionBoxes(bool enabled) { capi.Settings.Bool["debugEntitySelectionBoxes"] = enabled; }
	private static string ControlKey(int index) => "debugControl" + index;

	private sealed class DangerousToggleButton : GuiElementToggleButton
	{
		private static readonly double[] Background = { 0.32, 0.035, 0.035, 1 };

		internal DangerousToggleButton(ICoreClientAPI clientAPI, string text, CairoFont font, Action<bool> toggleHandler, ElementBounds bounds) : base(clientAPI, "", text, font, toggleHandler, bounds, true) { }

		public override void ComposeElements(Context context, ImageSurface surface)
		{
			double[] previousBackgroundColor = GuiStyle.DialogDefaultBgColor;
			GuiStyle.DialogDefaultBgColor = Background;
			try { base.ComposeElements(context, surface); }
			finally { GuiStyle.DialogDefaultBgColor = previousBackgroundColor; }
		}
	}

	private sealed class DebugControl
	{
		internal string Label { get; }
		internal Func<bool>? Get { get; }
		internal Action<bool>? Set { get; }
		internal Func<bool>? Run { get; }
		internal bool IsToggle => Get != null;

		private DebugControl(string label, Func<bool>? getState, Action<bool>? setState, Func<bool>? runAction)
		{
			Label = label;
			Get = getState;
			Set = setState;
			Run = runAction;
		}

		internal static DebugControl Toggle(string label, Func<bool> getState, Action<bool> setState) { return new DebugControl(label, getState, setState, null); }
		internal static DebugControl Action(string label, Func<bool> runAction) { return new DebugControl(label, null, null, runAction); }
	}
}
