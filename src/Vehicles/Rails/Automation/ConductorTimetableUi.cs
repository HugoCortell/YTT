using System;
using System.Collections.Generic;
using Cairo;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace YangTransport;

internal sealed class ConductorTimetableSystem : ModSystem
{
	internal const string ChannelName = "yangtransport_timetable";
	private const int MaxClientRouteEntries = 256;
	private const int MaxStationNameCharacters = 512;

	private ICoreServerAPI? ServerAPI;
	private ICoreClientAPI? ClientAPI;
	private IServerNetworkChannel? ServerNetworkChannel;
	private IClientNetworkChannel? ClientNetworkChannel;
	private TimetableRoutePlannerDialog? RoutePlannerDialog;
	private TimetableStationMapDialog? MapDialog;

	public override bool ShouldLoad(EnumAppSide forSide) => true;

	public override void Start(ICoreAPI coreAPI)
	{
		if (coreAPI is ICoreServerAPI server) StartServer(server);
		if (coreAPI is ICoreClientAPI client) StartClient(client);
	}

	private void StartServer(ICoreServerAPI serverAPI)
	{
		ServerAPI = serverAPI;

		ServerNetworkChannel = serverAPI.Network
			.RegisterChannel(ChannelName)
			.RegisterMessageType<TimetableOpenPacket>()
			.RegisterMessageType<TimetableSavePacket>()
			.RegisterMessageType<TimetableMapRequestPacket>()
			.RegisterMessageType<TimetableMapOpenPacket>();

		ServerNetworkChannel.SetMessageHandler<TimetableSavePacket>(OnSaveFromClient);
		ServerNetworkChannel.SetMessageHandler<TimetableMapRequestPacket>(OnMapRequestFromClient);
	}

	private void StartClient(ICoreClientAPI clientAPI)
	{
		ClientAPI = clientAPI;

		ClientNetworkChannel = clientAPI.Network
			.RegisterChannel(ChannelName)
			.RegisterMessageType<TimetableOpenPacket>()
			.RegisterMessageType<TimetableSavePacket>()
			.RegisterMessageType<TimetableMapRequestPacket>()
			.RegisterMessageType<TimetableMapOpenPacket>();

		ClientNetworkChannel.SetMessageHandler<TimetableOpenPacket>(OnOpenFromServer);
		ClientNetworkChannel.SetMessageHandler<TimetableMapOpenPacket>(OnMapFromServer);
	}

	internal void OpenTimetable(IServerPlayer player, Entity openedFromEntity)
	{
		if (ServerAPI == null || ServerNetworkChannel == null || player == null || openedFromEntity == null) return;

		if (!TryResolveTimetableOwner(openedFromEntity, out Entity owner, out byte ownerGauge, out string error))
		{
			player.SendMessage(0, error, EnumChatType.Notification);
			return;
		}

		RailStationRegistrySystem? registry = ServerAPI.ModLoader.GetModSystem<RailStationRegistrySystem>();
		RailStationListEntry[] stations = registry?.BorrowStationListEntries(ownerGauge) ?? Array.Empty<RailStationListEntry>();

		ConductorTimetableDocument timetableDocument = ConductorTimetableStorage.Load(owner);
		ServerNetworkChannel.SendPacket(new TimetableOpenPacket
		{
			OwnerEntityID = owner.EntityId,
			Stations = stations,
			Route = timetableDocument.Route.ToArray(),
			CurrentStationIndex = timetableDocument.CurrentStationIndex
		}, player);
	}

	private bool TryResolveTimetableOwner(Entity openedFromEntity, out Entity owner, out byte ownerGauge, out string error)
	{
		owner = null!; ownerGauge = 0;
		error = Lang.Get("yangtransport:locusterror-no-locomotive");

		if (ServerAPI == null) return false;
		if (openedFromEntity is not IRailwayConvoyVehicle vehicle) return false;

		RailConvoySystem? convoys = ServerAPI.ModLoader.GetModSystem<RailConvoySystem>();
		long headID = vehicle.ConvoyHeadEntityID != 0 ? vehicle.ConvoyHeadEntityID : vehicle.Entity.EntityId;

		if (convoys != null && convoys.TryGetLeadID(headID, out long leadID))
		{
			if (ServerAPI.World.GetEntityById(leadID) is IRailwayConvoyVehicle leadVehicle && leadVehicle.HasTractionEngine)
			{
				owner = leadVehicle.Entity;
				ownerGauge = leadVehicle.TrackGauge;
				return true;
			}
		}

		if (vehicle.HasTractionEngine)
		{
			owner = vehicle.Entity;
			ownerGauge = vehicle.TrackGauge;
			return true;
		}

		return false;
	}

	private void OnSaveFromClient(IServerPlayer player, TimetableSavePacket packet)
	{
		if (ServerAPI == null || packet == null) return;

		if (!TryValidateClientRoute(packet.Route, out string validationError))
		{
			player?.SendMessage(0, validationError, EnumChatType.Notification);
			return;
		}

		Entity owner = ServerAPI.World.GetEntityById(packet.OwnerEntityID);
		if (owner is not IRailwayConvoyVehicle vehicle || !vehicle.HasTractionEngine)
		{
			player?.SendMessage(0, Lang.Get("yangtransport:locusterror-no-locomotive"), EnumChatType.Notification);
			return;
		}

		ConductorTimetableStorage.Save(owner, packet.Route ?? Array.Empty<TimetableRouteEntryPacket>(), packet.CurrentStationIndex);
	}

	private void OnMapRequestFromClient(IServerPlayer player, TimetableMapRequestPacket packet)
	{
		if (ServerAPI == null || ServerNetworkChannel == null || player == null || packet == null) return;
		if (!TryValidateClientRoute(packet.Route, out string validationError))
		{
			player.SendMessage(0, validationError, EnumChatType.Notification);
			return;
		}

		TimetableMapOpenPacket response = RailStationMapBuilder.Build(ServerAPI, player, packet);
		ServerNetworkChannel.SendPacket(response, player);
	}


	private static bool TryValidateClientRoute(TimetableRouteEntryPacket[]? route, out string error)
	{
		error = string.Empty;
		if (route == null) return true;
		if (route.Length > MaxClientRouteEntries)
		{
			error = $"Timetable contains too many stops ({route.Length}/{MaxClientRouteEntries}).";
			return false;
		}

		for (int routeIndex = 0; routeIndex < route.Length; routeIndex++)
		{
			string name = route[routeIndex]?.StationName ?? string.Empty;
			if (name.Length > MaxStationNameCharacters)
			{
				error = $"Timetable stop {routeIndex + 1} has a station name longer than {MaxStationNameCharacters} characters.";
				return false;
			}
		}

		return true;
	}

	private void OnMapFromServer(TimetableMapOpenPacket packet)
	{
		if (ClientAPI == null || packet == null) return;

		if (!string.IsNullOrWhiteSpace(packet.ErrorText)) { ClientAPI.ShowChatMessage(packet.ErrorText); return; }

		MapDialog?.TryClose();
		MapDialog = new TimetableStationMapDialog(ClientAPI, packet);
		MapDialog.OnClosed += () => MapDialog = null;
		MapDialog.TryOpen();
	}

	private void OnOpenFromServer(TimetableOpenPacket packet)
	{
		if (ClientAPI == null || ClientNetworkChannel == null || packet == null) return;

		RoutePlannerDialog?.TryClose();
		RoutePlannerDialog = new TimetableRoutePlannerDialog
		(
			ClientAPI,
			packet.OwnerEntityID,
			packet.Stations ?? Array.Empty<RailStationListEntry>(),
			packet.Route ?? Array.Empty<TimetableRouteEntryPacket>(),
			packet.CurrentStationIndex,
			save => ClientNetworkChannel.SendPacket(save),
			map => ClientNetworkChannel.SendPacket(map)
		);

		RoutePlannerDialog.OnClosed += () => RoutePlannerDialog = null;
		RoutePlannerDialog.TryOpen();
	}
}

[ProtoContract]
public sealed class RailStationListPacket
{
	[ProtoMember(1)] public RailStationListEntry[] Stations = Array.Empty<RailStationListEntry>();
}

[ProtoContract]
public sealed class RailStationListEntry
{
	[ProtoMember(1)] public string Name = "";
	[ProtoMember(2)] public ulong NameHash;

	[ProtoMember(3)] public int X;
	[ProtoMember(4)] public int Y;
	[ProtoMember(5)] public int Z;
	[ProtoMember(6)] public int Dimension;

	[ProtoMember(7)] public byte Gauge;

	[ProtoMember(8)] public bool HasStopEndpoint;
	[ProtoMember(9)] public int StopX16;
	[ProtoMember(10)] public int StopY16;
	[ProtoMember(11)] public int StopZ16;
	[ProtoMember(12)] public int PlatformCount = 1;

	internal bool SameBlockAs(TimetableRouteEntryPacket route) { return X == route.X && Y == route.Y && Z == route.Z && Dimension == route.Dimension; }
}

[ProtoContract]
public sealed class TimetableOpenPacket
{
	[ProtoMember(1)] public long OwnerEntityID;
	[ProtoMember(2)] public RailStationListEntry[] Stations = Array.Empty<RailStationListEntry>();
	[ProtoMember(3)] public TimetableRouteEntryPacket[] Route = Array.Empty<TimetableRouteEntryPacket>();
	[ProtoMember(4)] public int CurrentStationIndex = -1;
}

[ProtoContract]
public sealed class TimetableSavePacket
{
	[ProtoMember(1)] public long OwnerEntityID;
	[ProtoMember(2)] public TimetableRouteEntryPacket[] Route = Array.Empty<TimetableRouteEntryPacket>();
	[ProtoMember(3)] public int CurrentStationIndex = -1;
}

[ProtoContract]
public sealed class TimetableRouteEntryPacket
{
	public const int DefaultTimeElapsedSeconds = 15;
	public const int DefaultMileageLeftMinutes = 2;

	[ProtoMember(1)] public string StationName = "";
	[ProtoMember(2)] public ulong NameHash;

	[ProtoMember(3)] public int X;
	[ProtoMember(4)] public int Y;
	[ProtoMember(5)] public int Z;
	[ProtoMember(6)] public int Dimension;

	[ProtoMember(7)] public int TimeElapsedSeconds = DefaultTimeElapsedSeconds;
	[ProtoMember(8)] public int MileageLeftMinutes = DefaultMileageLeftMinutes;
	[ProtoMember(9)] public bool AnnounceOnArrival;
	[ProtoMember(10)] public bool AnnounceOnDeparture;

	internal static TimetableRouteEntryPacket FromStation(RailStationListEntry station)
	{
		return new TimetableRouteEntryPacket
		{
			StationName = station.Name ?? "",
			NameHash = station.NameHash,
			X = station.X, Y = station.Y, Z = station.Z,
			Dimension = station.Dimension,
			TimeElapsedSeconds = DefaultTimeElapsedSeconds,
			MileageLeftMinutes = DefaultMileageLeftMinutes,
			AnnounceOnArrival = false,
			AnnounceOnDeparture = false
		};
	}

	internal TimetableRouteEntryPacket Clone()
	{
		return new TimetableRouteEntryPacket
		{
			StationName = StationName ?? "",
			NameHash = NameHash,
			X = X, Y = Y, Z = Z,
			Dimension = Dimension,
			TimeElapsedSeconds = TimeElapsedSeconds,
			MileageLeftMinutes = MileageLeftMinutes,
			AnnounceOnArrival = AnnounceOnArrival,
			AnnounceOnDeparture = AnnounceOnDeparture
		};
	}
}

internal sealed class ConductorTimetableDocument
{
	public readonly List<TimetableRouteEntryPacket> Route = new();
	public int CurrentStationIndex = -1;
}

internal static class ConductorTimetableStorage
{
	private const string RootAttributeKey = "yangtransport.timetable";
	private const string ChangeSerialAttributeKey = "changeSerial";
	private const int MaxEntries = 256;

	internal static int GetChangeSerial(Entity owner)
	{
		ITreeAttribute? root = owner?.Attributes.GetTreeAttribute(RootAttributeKey);
		return root?.GetInt(ChangeSerialAttributeKey, 0) ?? 0;
	}

	internal static ConductorTimetableDocument Load(Entity owner)
	{
		ConductorTimetableDocument timetableDocument = new();
		ITreeAttribute? root = owner?.Attributes.GetTreeAttribute(RootAttributeKey);
		if (root == null) return timetableDocument;

		timetableDocument.CurrentStationIndex = root.GetInt("currentStationIndex", -1);

		int count = GameMath.Clamp(root.GetInt("count", 0), 0, MaxEntries);
		for (int entryIndex = 0; entryIndex < count; entryIndex++)
		{
			ITreeAttribute? entry = root.GetTreeAttribute("entry" + entryIndex.ToString());
			if (entry == null) continue;

			timetableDocument.Route.Add(new TimetableRouteEntryPacket
			{
				StationName = CleanName(entry.GetString("stationName", "")),
				NameHash = unchecked((ulong)entry.GetLong("nameHash", 0)),
				X = entry.GetInt("x", 0), Y = entry.GetInt("y", 0), Z = entry.GetInt("z", 0),
				Dimension = entry.GetInt("dim", 0),
				TimeElapsedSeconds = ClampSeconds(entry.GetInt("timeElapsedSeconds", TimetableRouteEntryPacket.DefaultTimeElapsedSeconds)),
				MileageLeftMinutes = ClampMinutes(entry.GetInt("mileageLeftMinutes", TimetableRouteEntryPacket.DefaultMileageLeftMinutes)),
				AnnounceOnArrival = entry.GetBool("announceOnArrival", false),
				AnnounceOnDeparture = entry.GetBool("announceOnDeparture", false)
			});
		}

		if (timetableDocument.CurrentStationIndex < 0 || timetableDocument.CurrentStationIndex >= timetableDocument.Route.Count) timetableDocument.CurrentStationIndex = -1;
		return timetableDocument;
	}

	internal static void Save(Entity owner, TimetableRouteEntryPacket[] route, int currentStationIndex)
	{
		if (owner == null) return;

		ITreeAttribute root = owner.Attributes.GetOrAddTreeAttribute(RootAttributeKey);
		int oldCount = root.GetInt("count", 0);
		int count = Math.Min(route?.Length ?? 0, MaxEntries);

		root.SetInt(ChangeSerialAttributeKey, root.GetInt(ChangeSerialAttributeKey, 0) + 1);
		root.SetInt("count", count);
		root.SetInt("currentStationIndex", currentStationIndex >= 0 && currentStationIndex < count ? currentStationIndex : -1);

		for (int entryIndex = 0; entryIndex < count; entryIndex++)
		{
			TimetableRouteEntryPacket sourceEntry = route![entryIndex] ?? new TimetableRouteEntryPacket();
			ITreeAttribute entry = root.GetOrAddTreeAttribute("entry" + entryIndex.ToString());

			entry.SetString("stationName", CleanName(sourceEntry.StationName));
			entry.SetLong("nameHash", unchecked((long)sourceEntry.NameHash));
			entry.SetInt("x", sourceEntry.X);
			entry.SetInt("y", sourceEntry.Y);
			entry.SetInt("z", sourceEntry.Z);
			entry.SetInt("dim", sourceEntry.Dimension);
			entry.SetInt("timeElapsedSeconds", ClampSeconds(sourceEntry.TimeElapsedSeconds));
			entry.SetInt("mileageLeftMinutes", ClampMinutes(sourceEntry.MileageLeftMinutes));
			entry.SetBool("announceOnArrival", sourceEntry.AnnounceOnArrival);
			entry.SetBool("announceOnDeparture", sourceEntry.AnnounceOnDeparture);
		}

		for (int entryIndex = count; entryIndex < oldCount; entryIndex++) { root.RemoveAttribute("entry" + entryIndex.ToString()); }
	}

	private static string CleanName(string name)
	{
		name = (name ?? "").Trim();
		return name.Length > 512 ? name[..512] : name;
	}

	private static int ClampSeconds(int value) => GameMath.Clamp(value, 0, 24 * 60 * 60);
	private static int ClampMinutes(int value) => GameMath.Clamp(value, 0, 24 * 60);
}

internal sealed class TimetableRoutePlannerDialog : GuiDialogGeneric
{
	private const double ContentWidth = 960;
	private const double ContentHeight = 620;
	private const double HorizontalGap = 14;
	private const double PanelPadding = 10;
	private const double ButtonHeight = 32;
	private const double ButtonGap = 6;
	private const double RowHeight = 32;

	private readonly long OwnerEntityID;
	private readonly RailStationListEntry[] AllStations;
	private readonly List<TimetableRouteEntryPacket> Route = new();
	private readonly Action<TimetableSavePacket> SendSavePacket;
	private readonly Action<TimetableMapRequestPacket> SendMapRequestPacket;

	private int SelectedIndex;
	private int CurrentStationIndex;
	private TimetableStationPickerDialog? StationPickerDialog;
	private bool SuppressTextPacket;
	private bool SuppressRouteScroll;
	private double RouteScrollY;
	private double LastRouteListHeight;
	private ElementBounds? RouteRowsBounds;

	public override double DrawOrder => 0.2;

	public TimetableRoutePlannerDialog
	(
		ICoreClientAPI clientAPI,
		long ownerEntityID,
		RailStationListEntry[] stations,
		TimetableRouteEntryPacket[] initialRoute,
		int currentStationIndex,
		Action<TimetableSavePacket> sendSave,
		Action<TimetableMapRequestPacket> sendMapRequest
	) : base("Timetable Route Planner", clientAPI)
	{
		this.OwnerEntityID = ownerEntityID;
		AllStations = stations ?? Array.Empty<RailStationListEntry>();
		this.SendSavePacket = sendSave;
		this.SendMapRequestPacket = sendMapRequest;
		this.CurrentStationIndex = currentStationIndex;

		if (initialRoute != null) { for (int routeIndex = 0; routeIndex < initialRoute.Length; routeIndex++) Route.Add(Sanitize(initialRoute[routeIndex])); }

		RefreshRouteStationNames();
		SelectedIndex = Route.Count > 0 ? 0 : -1;
		if (this.CurrentStationIndex < 0 || this.CurrentStationIndex >= Route.Count) this.CurrentStationIndex = -1;

		Compose();
	}

	public override void OnGuiClosed()
	{
		StationPickerDialog?.TryClose();
		StationPickerDialog = null;
		base.OnGuiClosed();
	}

	private void Compose()
	{
		SelectedIndex = Route.Count == 0 ? -1 : GameMath.Clamp(SelectedIndex, 0, Route.Count - 1);

		double titleBarHeight = GuiStyle.TitleBarHeight;
		double usableWidth = ContentWidth - HorizontalGap;
		double leftWidth = Math.Floor(usableWidth * 0.40);
		double rightWidth = usableWidth - leftWidth;

		ElementBounds backgroundBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
		backgroundBounds.BothSizing = ElementSizing.FitToChildren;

		ElementBounds leftPanel = ElementBounds.Fixed(0, titleBarHeight, leftWidth, ContentHeight).WithParent(backgroundBounds);
		ElementBounds rightPanel = ElementBounds.Fixed(leftWidth + HorizontalGap, titleBarHeight, rightWidth, ContentHeight).WithParent(backgroundBounds);

		backgroundBounds.WithChildren(leftPanel, rightPanel);

		SingleComposer?.Dispose();
		GuiComposer composer = capi.Gui
			.CreateCompo("yangtransport-timetable-route-planner-" + OwnerEntityID.ToString(), ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle))
			.AddShadedDialogBG(backgroundBounds)
			.AddDialogTitleBar(Lang.Get("yangtransport:automation-ui-timetable-header"), () => TryClose())
			.BeginChildElements(backgroundBounds).AddInset(leftPanel).AddInset(rightPanel);

		AddLeftPanel(composer, leftPanel);
		AddRightPanel(composer, rightPanel);

		SingleComposer = composer.EndChildElements().Compose();

		ConfigureRouteScrollbar();
		ConfigureRightPanelControls();
	}

	private void AddLeftPanel(GuiComposer composer, ElementBounds leftPanel)
	{
		double listTitleHeight = 28;
		double bottomButtonsTotalHeight = ButtonHeight;
		double listTop = PanelPadding + listTitleHeight;
		double listHeight = leftPanel.fixedHeight - PanelPadding * 3 - listTitleHeight - bottomButtonsTotalHeight;
		LastRouteListHeight = listHeight;
		RouteRowsBounds = null;

		ElementBounds titleBounds = ElementBounds.Fixed(PanelPadding, PanelPadding, leftPanel.fixedWidth - PanelPadding * 2, listTitleHeight).WithParent(leftPanel);
		composer.AddStaticText(Lang.Get("yangtransport:automation-ui-timetable-stationlist"), CairoFont.WhiteSmallishText().WithWeight(FontWeight.Bold), titleBounds);

		if (Route.Count == 0)
		{
			ElementBounds emptyBounds = ElementBounds.Fixed(PanelPadding + 4, listTop + 6, leftPanel.fixedWidth - PanelPadding * 2 - 8, 80).WithParent(leftPanel);
			composer.AddStaticText(Lang.Get("yangtransport:automation-ui-timetable-nostationswarning"), CairoFont.WhiteSmallText(), emptyBounds);
		}
		else
		{
			int visibleRows = Math.Max(1, (int)Math.Floor(listHeight / RowHeight));
			int maxStart = Math.Max(0, Route.Count - visibleRows);
			RouteScrollY = GameMath.Clamp(RouteScrollY, 0, maxStart * RowHeight);
			bool needsScrollbar = Route.Count > visibleRows;
			double scrollbarWidth = needsScrollbar ? GuiElementScrollbar.DefaultScrollbarWidth + 4 : 0;
			double listWidth = leftPanel.fixedWidth - PanelPadding * 2 - scrollbarWidth;
			double scrollContentHeight = listHeight + maxStart * RowHeight;

			ElementBounds clipBounds = ElementBounds.Fixed(PanelPadding, listTop, listWidth, listHeight).WithParent(leftPanel);
			ElementBounds rowsBounds = ElementBounds.Fixed(0, -RouteScrollY, listWidth, scrollContentHeight).WithParent(clipBounds);
			RouteRowsBounds = rowsBounds;

			composer.BeginClip(clipBounds).BeginChildElements(rowsBounds);

			for (int routeIteration = 0; routeIteration < Route.Count; routeIteration++)
			{
				int routeIndex = routeIteration;
				TimetableRouteEntryPacket entry = Route[routeIndex];

				string prefix = routeIndex == CurrentStationIndex ? ">>> " : "";
				string selectedPrefix = routeIndex == SelectedIndex ? "◊ " : "   "; // alt ●
				string label = selectedPrefix + prefix + SafeStationName(entry);

				ElementBounds rowBounds = ElementBounds.Fixed(0, routeIndex * RowHeight, listWidth, RowHeight - 3).WithParent(rowsBounds);

				composer.AddSmallButton(label, () => OnRouteRowClicked(routeIndex), rowBounds, EnumButtonStyle.Normal, "routeRow" + routeIndex.ToString());
			}

			composer.EndChildElements().EndClip();

			if (needsScrollbar)
			{
				ElementBounds scrollbarBounds = ElementBounds.Fixed
				(
					leftPanel.fixedWidth - PanelPadding - GuiElementScrollbar.DefaultScrollbarWidth,
					listTop, GuiElementScrollbar.DefaultScrollbarWidth, listHeight
				).WithParent(leftPanel);

				composer.AddVerticalScrollbar(OnRouteScrollChanged, scrollbarBounds, "routeScrollbar");
			}
		}

		double buttonsY = leftPanel.fixedHeight - PanelPadding - ButtonHeight;
		double buttonWidth = (leftPanel.fixedWidth - PanelPadding * 2 - ButtonGap * 3) / 4.0;

		composer
			.AddSmallButton("+", OnAddClicked, ElementBounds.Fixed(PanelPadding, buttonsY, buttonWidth, ButtonHeight).WithParent(leftPanel), EnumButtonStyle.Normal, "btnAdd")
			.AddSmallButton("─", OnRemoveClicked, ElementBounds.Fixed(PanelPadding + (buttonWidth + ButtonGap), buttonsY, buttonWidth, ButtonHeight).WithParent(leftPanel), EnumButtonStyle.Normal, "btnRemove")
			.AddSmallButton("▲", OnMoveUpClicked, ElementBounds.Fixed(PanelPadding + (buttonWidth + ButtonGap) * 2, buttonsY, buttonWidth, ButtonHeight).WithParent(leftPanel), EnumButtonStyle.Normal, "btnUp")
			.AddSmallButton("▼", OnMoveDownClicked, ElementBounds.Fixed(PanelPadding + (buttonWidth + ButtonGap) * 3, buttonsY, buttonWidth, ButtonHeight).WithParent(leftPanel), EnumButtonStyle.Normal, "btnDown");
	}

	private void ConfigureRouteScrollbar()
	{
		GuiElementScrollbar? scrollbar = SingleComposer?.GetScrollbar("routeScrollbar");
		if (scrollbar == null) return;

		SuppressRouteScroll = true;
		float visibleHeight = (float)Math.Max(RowHeight, LastRouteListHeight);
		int visibleRows = Math.Max(1, (int)Math.Floor(visibleHeight / RowHeight));
		int maxStart = Math.Max(0, Route.Count - visibleRows);
		float totalHeight = visibleHeight + (float)(maxStart * RowHeight);
		scrollbar.SetHeights(visibleHeight, totalHeight);
		scrollbar.CurrentYPosition = (float)RouteScrollY;
		scrollbar.RecomposeHandle();
		SuppressRouteScroll = false;
	}

	private void OnRouteScrollChanged(float value)
	{
		if (SuppressRouteScroll) return;
		RouteScrollY = value;
		if (RouteRowsBounds == null) return;

		RouteRowsBounds.fixedY = -value;
		RouteRowsBounds.CalcWorldBounds();
	}

	private void AddRightPanel(GuiComposer composer, ElementBounds rightPanel)
	{
		TimetableRouteEntryPacket? selected = SelectedIndex >= 0 && SelectedIndex < Route.Count ? Route[SelectedIndex] : null;

		ElementBounds titleBounds = ElementBounds.Fixed(PanelPadding, PanelPadding, rightPanel.fixedWidth - PanelPadding * 2, 42).WithParent(rightPanel);
		composer.AddStaticText(selected != null ? SafeStationName(selected) : Lang.Get("yangtransport:automation-ui-timetable-nostationselected"), CairoFont.WhiteMediumText().WithWeight(FontWeight.Bold), titleBounds);

		double announceTop = PanelPadding + 58;
		ElementBounds announceInset = ElementBounds.Fixed(PanelPadding, announceTop, rightPanel.fixedWidth - PanelPadding * 2, 44).WithParent(rightPanel);
		composer.AddInset(announceInset);

		composer.AddStaticText
		(
			Lang.Get("yangtransport:automation-ui-announceon"),
			CairoFont.WhiteSmallText(), ElementBounds.Fixed(10, 12, 115, 24).WithParent(announceInset)
		);
		if (selected != null)
		{
			composer
				.AddSwitch(OnAnnounceArrivalChanged, ElementBounds.Fixed(130, 9, 22, 22).WithParent(announceInset), "announceArrivalSwitch", 22, 4)
				.AddStaticText
				(
					Lang.Get("yangtransport:automation-ui-arrival"),
					CairoFont.WhiteSmallText(), ElementBounds.Fixed(158, 12, 75, 24).WithParent(announceInset)
				)
				.AddSwitch(OnAnnounceDepartureChanged, ElementBounds.Fixed(245, 9, 22, 22).WithParent(announceInset), "announceDepartureSwitch", 22, 4)
				.AddStaticText
				(
					Lang.Get("yangtransport:automation-ui-departure"),
					CairoFont.WhiteSmallText(), ElementBounds.Fixed(273, 12, 95, 24).WithParent(announceInset)
				);
		}

		double criteriaTop = announceTop + announceInset.fixedHeight + 10;
		ElementBounds criteriaInset = ElementBounds.Fixed(PanelPadding, criteriaTop, rightPanel.fixedWidth - PanelPadding * 2, 160).WithParent(rightPanel);
		composer.AddInset(criteriaInset);

		ElementBounds criteriaTitleBounds = ElementBounds.Fixed(8, 6, criteriaInset.fixedWidth - 16, 28).WithParent(criteriaInset);
		composer.AddStaticText(Lang.Get("yangtransport:automation-ui-timetable-leavecritera"), CairoFont.WhiteSmallishText().WithWeight(FontWeight.Bold), criteriaTitleBounds);

		if (selected == null)
		{
			ElementBounds noSelectionBounds = ElementBounds.Fixed(8, 44, criteriaInset.fixedWidth - 16, 80).WithParent(criteriaInset);
			composer.AddStaticText(Lang.Get("yangtransport:automation-ui-timetable-criterianostation"), CairoFont.WhiteSmallText(), noSelectionBounds);
		}
		else
		{
			AddCriterionRow
			(
				composer,
				criteriaInset,
				y: 44,
				label: Lang.Get("yangtransport:automation-ui-timetable-critera-time"),
				unit: Lang.Get("yangtransport:automation-ui-timetable-critera-time-unit"),
				inputKey: "timeElapsedInput",
				onChanged: OnTimeElapsedChanged
			);

			AddCriterionRow
			(
				composer,
				criteriaInset,
				y: 94,
				label: Lang.Get("yangtransport:automation-ui-timetable-critera-mileage"),
				unit: Lang.Get("yangtransport:automation-ui-timetable-critera-mileage-unit"),
				inputKey: "mileageLeftInput",
				onChanged: OnMileageLeftChanged
			);
		}

		// In the future here'd go some kind of warning system or extra info that may be of use. Cut from release.
		// double lowerTextTop = criteriaTop + criteriaInset.fixedHeight + 16;
		// ElementBounds noteBounds = ElementBounds.Fixed(PanelPad + 4, lowerTextTop, rightPanel.fixedWidth - PanelPad * 2 - 8, 80).WithParent(rightPanel);
		// composer.AddStaticText("Here goes some info that has not yet been filled...", CairoFont.WhiteDetailText(), noteBounds);

		double bottomY = rightPanel.fixedHeight - PanelPadding - ButtonHeight;
		double buttonW = (rightPanel.fixedWidth - PanelPadding * 2 - ButtonGap) / 2.0;

		composer
			.AddSmallButton(Lang.Get("yangtransport:automation-minimap-button"), OnOpenMapClicked, ElementBounds.Fixed(PanelPadding, bottomY, buttonW, ButtonHeight).WithParent(rightPanel), EnumButtonStyle.Normal, "btnOpenMap")
			.AddSmallButton(Lang.Get("game:Show Handbook"), OnHelpClicked, ElementBounds.Fixed(PanelPadding + buttonW + ButtonGap, bottomY, buttonW, ButtonHeight).WithParent(rightPanel), EnumButtonStyle.Normal, "btnHelp");
	}

	private void AddCriterionRow(GuiComposer composer, ElementBounds parent, double y, string label, string unit, string inputKey, Action<string> onChanged)
	{
		ElementBounds labelBounds = ElementBounds.Fixed(10, y + 5, 180, 28).WithParent(parent);
		ElementBounds inputBounds = ElementBounds.Fixed(205, y, 90, 30).WithParent(parent);
		ElementBounds unitBounds = ElementBounds.Fixed(305, y + 5, parent.fixedWidth - 315, 28).WithParent(parent);

		composer
			.AddStaticText(label, CairoFont.WhiteSmallText(), labelBounds)
			.AddTextInput(inputBounds, onChanged, CairoFont.SmallTextInput().WithColor(GuiStyle.DialogDefaultTextColor), inputKey)
			.AddStaticText(unit, CairoFont.WhiteSmallText(), unitBounds);
	}

	private void ConfigureRightPanelControls()
	{
		if (SingleComposer == null) return;

		SingleComposer.GetButton("btnOpenMap").Enabled = Route.Count > 0;

		if (SelectedIndex < 0 || SelectedIndex >= Route.Count) return;

		TimetableRouteEntryPacket selected = Route[SelectedIndex];

		GuiElementSwitch? arrivalSwitch = SingleComposer.GetSwitch("announceArrivalSwitch");
		arrivalSwitch?.SetValue(selected.AnnounceOnArrival);

		GuiElementSwitch? departureSwitch = SingleComposer.GetSwitch("announceDepartureSwitch");
		departureSwitch?.SetValue(selected.AnnounceOnDeparture);

		SuppressTextPacket = true;

		GuiElementTextInput? timeInput = SingleComposer.GetTextInput("timeElapsedInput");
		if (timeInput != null)
		{
			timeInput.SetMaxLength(5);
			timeInput.OnTryTextChangeText = IsUnsignedIntegerText;
			timeInput.SetValue(selected.TimeElapsedSeconds.ToString());
		}

		GuiElementTextInput? mileageInput = SingleComposer.GetTextInput("mileageLeftInput");
		if (mileageInput != null)
		{
			mileageInput.SetMaxLength(4);
			mileageInput.OnTryTextChangeText = IsUnsignedIntegerText;
			mileageInput.SetValue(selected.MileageLeftMinutes.ToString());
		}

		SuppressTextPacket = false;
	}

	private static bool IsUnsignedIntegerText(List<string> lines)
	{
		if (lines == null || lines.Count == 0) return true;
		string text = lines[0] ?? "";
		if (text.Length == 0) return true;
		for (int characterIndex = 0; characterIndex < text.Length; characterIndex++) { if (!char.IsDigit(text[characterIndex])) return false; }
		return true;
	}

	private bool OnRouteRowClicked(int routeIndex)
	{
		SelectedIndex = routeIndex;
		Compose();
		return true;
	}

	private bool OnAddClicked()
	{
		if (AllStations.Length == 0)
		{
			capi.ShowChatMessage(Lang.Get("yangtransport:automation-ui-timetable-nostationsregistered"));
			return true;
		}

		StationPickerDialog?.TryClose();
		StationPickerDialog = new TimetableStationPickerDialog(capi, AllStations, station =>
		{
			Route.Add(TimetableRouteEntryPacket.FromStation(station));
			SelectedIndex = Route.Count - 1;
			if (CurrentStationIndex < 0) CurrentStationIndex = 0;
			SendSave();
			Compose();
		});

		StationPickerDialog.OnClosed += () => StationPickerDialog = null;
		StationPickerDialog.TryOpen();
		return true;
	}

	private bool OnRemoveClicked()
	{
		if (SelectedIndex < 0 || SelectedIndex >= Route.Count) return true;

		int removed = SelectedIndex;
		Route.RemoveAt(removed);

		if (CurrentStationIndex == removed) CurrentStationIndex = -1;
		else if (CurrentStationIndex > removed) CurrentStationIndex--;

		SelectedIndex = Route.Count == 0 ? -1 : GameMath.Clamp(removed, 0, Route.Count - 1);
		SendSave();
		Compose();
		return true;
	}

	private bool OnMoveUpClicked()
	{
		if (SelectedIndex <= 0 || SelectedIndex >= Route.Count) return true;
		SwapRouteEntries(SelectedIndex, SelectedIndex - 1);
		SelectedIndex--;
		SendSave();
		Compose();
		return true;
	}

	private bool OnMoveDownClicked()
	{
		if (SelectedIndex < 0 || SelectedIndex >= Route.Count - 1) return true;
		SwapRouteEntries(SelectedIndex, SelectedIndex + 1);
		SelectedIndex++;
		SendSave();
		Compose();
		return true;
	}

	private bool OnOpenMapClicked()
	{
		if (Route.Count == 0) return true;

		SendMapRequestPacket(new TimetableMapRequestPacket
		{
			OwnerEntityID = OwnerEntityID,
			Route = CloneRoute(),
			CurrentStationIndex = CurrentStationIndex
		});
		return true;
	}

	private bool OnHelpClicked()
	{
		if (capi.LinkProtocols.TryGetValue("handbook", out Action<LinkTextComponent>? openHandbook))
		{
			openHandbook(new LinkTextComponent("handbook://yangtransport:railautomation"));
		}

		return true;
	}

	private void OnAnnounceArrivalChanged(bool enabled)
	{
		if (SelectedIndex < 0 || SelectedIndex >= Route.Count) return;
		Route[SelectedIndex].AnnounceOnArrival = enabled;
		SendSave();
	}

	private void OnAnnounceDepartureChanged(bool enabled)
	{
		if (SelectedIndex < 0 || SelectedIndex >= Route.Count) return;
		Route[SelectedIndex].AnnounceOnDeparture = enabled;
		SendSave();
	}

	private void OnTimeElapsedChanged(string text)
	{
		if (SuppressTextPacket || SelectedIndex < 0 || SelectedIndex >= Route.Count) return;
		if (!int.TryParse(text, out int value)) value = 0;
		Route[SelectedIndex].TimeElapsedSeconds = GameMath.Clamp(value, 0, 24 * 60 * 60);
		SendSave();
	}

	private void OnMileageLeftChanged(string text)
	{
		if (SuppressTextPacket || SelectedIndex < 0 || SelectedIndex >= Route.Count) return;
		if (!int.TryParse(text, out int value)) value = 0;
		Route[SelectedIndex].MileageLeftMinutes = GameMath.Clamp(value, 0, 24 * 60);
		SendSave();
	}

	private void SwapRouteEntries(int firstIndex, int secondIndex)
	{
		(Route[firstIndex], Route[secondIndex]) = (Route[secondIndex], Route[firstIndex]);

		if (CurrentStationIndex == firstIndex) CurrentStationIndex = secondIndex;
		else if (CurrentStationIndex == secondIndex) CurrentStationIndex = firstIndex;
	}

	private void SendSave()
	{
		SendSavePacket(new TimetableSavePacket
		{
			OwnerEntityID = OwnerEntityID,
			Route = CloneRoute(),
			CurrentStationIndex = CurrentStationIndex
		});
	}

	private TimetableRouteEntryPacket[] CloneRoute()
	{
		TimetableRouteEntryPacket[] clone = new TimetableRouteEntryPacket[Route.Count];
		for (int routeIndex = 0; routeIndex < Route.Count; routeIndex++) clone[routeIndex] = Sanitize(Route[routeIndex]);
		return clone;
	}

	private void RefreshRouteStationNames()
	{
		for (int routeIndex = 0; routeIndex < Route.Count; routeIndex++)
		{
			TimetableRouteEntryPacket entry = Route[routeIndex];

			for (int stationIndex = 0; stationIndex < AllStations.Length; stationIndex++)
			{
				RailStationListEntry station = AllStations[stationIndex];
				bool sameDestination = station.SameBlockAs(entry) || (entry.NameHash != 0 && station.NameHash == entry.NameHash && station.Dimension == entry.Dimension);

				if (!sameDestination) continue;

				entry.StationName = station.Name ?? entry.StationName;
				entry.NameHash = station.NameHash;
				break;
			}
		}
	}

	private static TimetableRouteEntryPacket Sanitize(TimetableRouteEntryPacket? entry)
	{
		entry ??= new TimetableRouteEntryPacket();

		TimetableRouteEntryPacket clean = entry.Clone();
		clean.StationName = (clean.StationName ?? "").Trim();
		if (clean.StationName.Length > 512) clean.StationName = clean.StationName[..512];
		clean.TimeElapsedSeconds = GameMath.Clamp(clean.TimeElapsedSeconds, 0, 24 * 60 * 60);
		clean.MileageLeftMinutes = GameMath.Clamp(clean.MileageLeftMinutes, 0, 24 * 60);
		return clean;
	}

	private static string SafeStationName(TimetableRouteEntryPacket entry)
	{
		string name = (entry.StationName ?? "").Trim();
		return name.Length == 0 ? "(Unknown Station)" : name;
	}
}

internal sealed class TimetableStationPickerDialog : GuiDialogGeneric
{
	private const double ContentWidth = 560;
	private const double ContentHeight = 520;
	private const double PanelPadding = 10;
	private const double RowHeight = 32;

	private readonly RailStationListEntry[] Stations;
	private readonly Action<RailStationListEntry> OnPicked;
	private bool SuppressStationScroll;
	private double StationScrollY;
	private double LastStationListHeight;
	private ElementBounds? StationRowsBounds;

	public override double DrawOrder => 0.25;

	public TimetableStationPickerDialog(ICoreClientAPI clientAPI, RailStationListEntry[] stations, Action<RailStationListEntry> onPicked) : base("Add Station", clientAPI)
	{
		this.Stations = stations ?? Array.Empty<RailStationListEntry>();
		this.OnPicked = onPicked;
		Compose();
	}

	private void Compose()
	{
		double titleBarHeight = GuiStyle.TitleBarHeight;

		ElementBounds backgroundBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
		backgroundBounds.BothSizing = ElementSizing.FitToChildren;

		ElementBounds panel = ElementBounds.Fixed(0, titleBarHeight, ContentWidth, ContentHeight).WithParent(backgroundBounds);
		backgroundBounds.WithChildren(panel);

		SingleComposer?.Dispose();

		GuiComposer composer = capi.Gui
			.CreateCompo("yangtransport-timetable-station-picker", ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle))
			.AddShadedDialogBG(backgroundBounds)
			.AddDialogTitleBar(Lang.Get("yangtransport:automation-ui-timetable-addstation"), () => TryClose())
			.BeginChildElements(backgroundBounds)
				.AddInset(panel);

		ElementBounds titleBounds = ElementBounds.Fixed(PanelPadding, PanelPadding, panel.fixedWidth - PanelPadding * 2, 28).WithParent(panel);
		composer.AddStaticText(Lang.Get("yangtransport:automation-ui-timetable-picknewstation"), CairoFont.WhiteSmallishText(), titleBounds);

		if (Stations.Length == 0)
		{
			ElementBounds emptyBounds = ElementBounds.Fixed(PanelPadding, 50, panel.fixedWidth - PanelPadding * 2, 80).WithParent(panel);
			composer.AddStaticText(Lang.Get("yangtransport:automation-ui-timetable-nostationsregistered"), CairoFont.WhiteSmallText(), emptyBounds);
		}
		else
		{
			double listHeight = panel.fixedHeight - 60;
			LastStationListHeight = listHeight;
			StationRowsBounds = null;

			int visibleRows = Math.Max(1, (int)Math.Floor(listHeight / RowHeight));
			int maxStart = Math.Max(0, Stations.Length - visibleRows);
			StationScrollY = GameMath.Clamp(StationScrollY, 0, maxStart * RowHeight);
			bool needsScrollbar = Stations.Length > visibleRows;
			double scrollbarWidth = needsScrollbar ? GuiElementScrollbar.DefaultScrollbarWidth + 4 : 0;
			double listWidth = panel.fixedWidth - PanelPadding * 2 - scrollbarWidth;
			double scrollContentHeight = listHeight + maxStart * RowHeight;

			ElementBounds clipBounds = ElementBounds.Fixed(PanelPadding, 50, listWidth, listHeight).WithParent(panel);
			ElementBounds rowsBounds = ElementBounds.Fixed(0, -StationScrollY, listWidth, scrollContentHeight).WithParent(clipBounds);
			StationRowsBounds = rowsBounds;

			composer.BeginClip(clipBounds).BeginChildElements(rowsBounds);

			for (int stationIteration = 0; stationIteration < Stations.Length; stationIteration++)
			{
				int stationIndex = stationIteration;
				RailStationListEntry station = Stations[stationIndex];

				string label = station.Name;
				if (station.PlatformCount > 1) label += " <" + station.PlatformCount.ToString() + ">";
				if (station.Dimension != 0) label += " (dim " + station.Dimension.ToString() + ")";
				// label += "  [" + station.X.ToString() + ", " + station.Y.ToString() + ", " + station.Z.ToString() + "]"; // Disabled to prevent spoilers

				ElementBounds rowBounds = ElementBounds.Fixed
				(
					0,
					stationIndex * RowHeight,
					listWidth,
					RowHeight - 3
				).WithParent(rowsBounds);

				composer.AddSmallButton(label, () => Pick(stationIndex), rowBounds, EnumButtonStyle.Normal, "stationRow" + stationIndex.ToString());
			}

			composer.EndChildElements().EndClip();

			if (needsScrollbar)
			{
				ElementBounds scrollbarBounds = ElementBounds.Fixed
				(
					panel.fixedWidth - PanelPadding - GuiElementScrollbar.DefaultScrollbarWidth,
					50,
					GuiElementScrollbar.DefaultScrollbarWidth,
					listHeight
				).WithParent(panel);

				composer.AddVerticalScrollbar(OnStationScrollChanged, scrollbarBounds, "stationScrollbar");
			}
		}

		SingleComposer = composer
			.EndChildElements()
			.Compose();

		ConfigureStationScrollbar();
	}

	private void ConfigureStationScrollbar()
	{
		GuiElementScrollbar? scrollbar = SingleComposer?.GetScrollbar("stationScrollbar");
		if (scrollbar == null) return;

		SuppressStationScroll = true;
		float visibleHeight = (float)Math.Max(RowHeight, LastStationListHeight);
		int visibleRows = Math.Max(1, (int)Math.Floor(visibleHeight / RowHeight));
		int maxStart = Math.Max(0, Stations.Length - visibleRows);
		float totalHeight = visibleHeight + (float)(maxStart * RowHeight);
		scrollbar.SetHeights(visibleHeight, totalHeight);
		scrollbar.CurrentYPosition = (float)StationScrollY;
		scrollbar.RecomposeHandle();
		SuppressStationScroll = false;
	}

	private void OnStationScrollChanged(float value)
	{
		if (SuppressStationScroll) return;
		StationScrollY = value;
		if (StationRowsBounds == null) return;

		StationRowsBounds.fixedY = -value;
		StationRowsBounds.CalcWorldBounds();
	}

	private bool Pick(int stationIndex)
	{
		if (stationIndex >= 0 && stationIndex < Stations.Length) OnPicked(Stations[stationIndex]);
		TryClose();
		return true;
	}
}
