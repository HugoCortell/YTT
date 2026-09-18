using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.API.Config;

namespace YangTransport;

/// Inherent storage to SG, one inventory per numbered AP.
public class EntityBehaviorSGStorage : EntityBehavior
{
	private const string DefaultPrefix = "STORAGE_AP_";
	private const string DoorOpenWatchedAttribute = "SGStorageOpen";
	private const string DefaultDoorOpenAnimationCode = "open";

	private readonly List<StoragePoint> StoragePoints = new();
	private readonly Dictionary<string, StoragePoint> StoragePointsByAttachmentPoint = new(StringComparer.OrdinalIgnoreCase);
	private GUIDialogSGBoxcarStorage ClientDialog;
	private StoragePoint OpenedClientStoragePoint;
	private AnimationMetaData DoorOpenAnimation;
	private string DoorOpenAnimationCode = DefaultDoorOpenAnimationCode;
	private bool DropContentsOnDeath;
	private bool DoorAnimationOpen;
	private bool SuppressDoorStateRefresh;

	internal const string InventoryTreeAttribute = "sgstorageInv";
	public string InventoryClassName => InventoryTreeAttribute;
	public bool IsEmpty
	{
		get
		{
			for (int storagePointIndex = 0; storagePointIndex < StoragePoints.Count; storagePointIndex++)
			{
				if (!StoragePoints[storagePointIndex].Inventory.Empty) return false;
			}

			return true;
		}
	}

	public EntityBehaviorSGStorage(Entity entity) : base(entity) { }

	public override string PropertyName() => "SGStorage";

	public override void Initialize(EntityProperties properties, JsonObject typeAttributes)
	{
		DropContentsOnDeath = typeAttributes?["DropContentsOnDeath"].AsBool(false) ?? false;
		DoorOpenAnimationCode = typeAttributes?["InventoryOpenAnimation"].AsString(DefaultDoorOpenAnimationCode) ?? DefaultDoorOpenAnimationCode;

		List<StorageSpecification> storageSpecifications = BuildStorageSpecifications(typeAttributes);
		if (storageSpecifications.Count == 0)
		{
			storageSpecifications.Add(new StorageSpecification(DefaultPrefix + "0", 36));
			storageSpecifications.Add(new StorageSpecification(DefaultPrefix + "1", 36));
		}

		for (int storagePointIndex = 0; storagePointIndex < storageSpecifications.Count; storagePointIndex++)
		{
			StorageSpecification storageSpecification = storageSpecifications[storagePointIndex];
			int inventorySlotCount = Math.Max(1, storageSpecification.InventorySlotCount);
			string inventoryID = "sgstorage-" + entity.EntityId + "-" + storagePointIndex;

			StoragePoint storagePoint = new StoragePoint
			{
				Index = storagePointIndex,
				AttachmentPointCode = storageSpecification.AttachmentPointCode,
				TreeKey = "p" + storagePointIndex,
				Inventory = new InventoryGeneric(inventorySlotCount, inventoryID, entity.Api)
			};

			StoragePoints.Add(storagePoint);
			StoragePointsByAttachmentPoint[storagePoint.AttachmentPointCode] = storagePoint;
		}

		LoadInventories();

		if (entity.World.Side == EnumAppSide.Server)
		{
			for (int storagePointIndex = 0; storagePointIndex < StoragePoints.Count; storagePointIndex++)
			{
				StoragePoints[storagePointIndex].Inventory.SlotModified += OnInventorySlotModified;
				StoragePoints[storagePointIndex].Inventory.OnInventoryOpened += OnInventoryOpened;
				StoragePoints[storagePointIndex].Inventory.OnInventoryClosed += OnInventoryClosed;
			}

			RefreshDoorOpenWatchedState(forceUpdate: true);
		}
		else
		{
			ResolveDoorAnimation();
			entity.WatchedAttributes.RegisterModifiedListener(InventoryClassName, LoadInventories);
			entity.WatchedAttributes.RegisterModifiedListener(DoorOpenWatchedAttribute, RefreshDoorAnimationState);
			RefreshDoorAnimationState();
		}

		base.Initialize(properties, typeAttributes);
	}

	public override void OnEntityDespawn(EntityDespawnData despawn)
	{
		if (entity.World.Side == EnumAppSide.Server)
		{
			for (int storagePointIndex = 0; storagePointIndex < StoragePoints.Count; storagePointIndex++)
			{
				StoragePoints[storagePointIndex].Inventory.SlotModified -= OnInventorySlotModified;
				StoragePoints[storagePointIndex].Inventory.OnInventoryOpened -= OnInventoryOpened;
				StoragePoints[storagePointIndex].Inventory.OnInventoryClosed -= OnInventoryClosed;
			}
		}
		else { CloseDoor(); }

		CloseClientDialog();

		base.OnEntityDespawn(despawn);
	}

	public override void OnEntitySpawn()
	{
		base.OnEntitySpawn();

		if (entity.World.Side == EnumAppSide.Client) { RefreshDoorAnimationState(); }
	}

	public override void OnEntityLoaded()
	{
		base.OnEntityLoaded();

		if (entity.World.Side == EnumAppSide.Client) { RefreshDoorAnimationState(); }
	}

	public override void OnEntityDeath(DamageSource damageSourceForDeath)
	{
		base.OnEntityDeath(damageSourceForDeath);
		if (!DropContentsOnDeath) return;

		for (int storagePointIndex = 0; storagePointIndex < StoragePoints.Count; storagePointIndex++)
		{
			StoragePoints[storagePointIndex].Inventory.DropAll(entity.Pos.XYZ);
		}
	}

	public override void FromBytes(bool isSync) { LoadInventories(); }
	public override void ToBytes(bool forClient) { StoreInventories(); }

	public override bool TryGiveItemStack(ItemStack itemStack, ref EnumHandling handling)
	{
		if (itemStack == null || StoragePoints.Count == 0) return false;

		ItemSlot dummySlot = new DummySlot(null) { Itemstack = itemStack.Clone() };
		ItemStackMoveOperation moveOperation = new ItemStackMoveOperation(entity.World, EnumMouseButton.Left, (EnumModifierKey)0, EnumMergePriority.AutoMerge, itemStack.StackSize);

		for (int storagePointIndex = 0; storagePointIndex < StoragePoints.Count && itemStack.StackSize > 0; storagePointIndex++)
		{
			InventoryGeneric inventory = StoragePoints[storagePointIndex].Inventory;
			WeightedSlot weightedSlot = inventory.GetBestSuitedSlot(dummySlot, null, new List<ItemSlot>());
			if (weightedSlot.weight <= 0f) continue;

			int previousMovedQuantity = moveOperation.MovedQuantity;
			dummySlot.TryPutInto(weightedSlot.slot, ref moveOperation);
			itemStack.StackSize -= moveOperation.MovedQuantity - previousMovedQuantity;
		}

		if (moveOperation.MovedQuantity > 0)
		{
			StoreInventories();
			handling = EnumHandling.PreventSubsequent;
			return true;
		}

		return false;
	}

	public override void OnInteract(EntityAgent byEntity, ItemSlot itemSlot, Vec3d hitPosition, EnumInteractMode mode, ref EnumHandling handled)
	{
		if (mode == EnumInteractMode.Attack) return;
		if (byEntity is not EntityPlayer entityPlayer) return;
		if (!TryGetClickedStoragePoint(entityPlayer, out StoragePoint storagePoint)) return;

		IPlayer player = entity.World.PlayerByUid(entityPlayer.PlayerUID);
		if (player == null) return;

		handled = EnumHandling.PreventSubsequent;

		EntityBehaviorSGRefrigeration refrigeration = entity.GetBehavior<EntityBehaviorSGRefrigeration>();

		if (entity.World.Side == EnumAppSide.Server)
		{
			if (refrigeration != null) ServerUpdateTransitions();
			SuppressDoorStateRefresh = true;

			try
			{
				CloseOtherStorageInventories(player, storagePoint);
				player.InventoryManager.OpenInventory(storagePoint.Inventory);
				if (refrigeration?.Inventory != null && !refrigeration.Inventory.HasOpened(player))
				{
					player.InventoryManager.OpenInventory(refrigeration.Inventory);
				}
			}
			finally
			{
				SuppressDoorStateRefresh = false;
				RefreshDoorOpenWatchedState();
			}

			return;
		}

		player.InventoryManager.OpenInventory(storagePoint.Inventory);
		if (refrigeration?.Inventory != null) player.InventoryManager.OpenInventory(refrigeration.Inventory);
		OpenClientDialog(player, storagePoint, refrigeration);
	}


	public override WorldInteraction[] GetInteractionHelp(IClientWorldAccessor world, EntitySelection entitySelection, IClientPlayer player, ref EnumHandling handled)
	{
		if (TryGetStoragePointForSelectionIndex(entitySelection?.SelectionBoxIndex ?? -1, out _))
		{
			handled = EnumHandling.PreventSubsequent;
			return new[]
			{
				new WorldInteraction
				{
					ActionLangCode = "yangtransport:entityhelp-sgstorage-open",
					MouseButton = EnumMouseButton.Right
				}
			};
		}

		return base.GetInteractionHelp(world, entitySelection, player, ref handled);
	}

	public override void OnReceivedClientPacket(IServerPlayer player, int packetID, byte[] data, ref EnumHandling handled)
	{
		if (packetID < 7 || packetID > 9) return;

		for (int storagePointIndex = 0; storagePointIndex < StoragePoints.Count; storagePointIndex++)
		{
			InventoryGeneric inventory = StoragePoints[storagePointIndex].Inventory;
			if (!inventory.HasOpened(player)) continue;

			inventory.InvNetworkUtil.HandleClientPacket(player, packetID, data);
			handled = EnumHandling.PreventSubsequent;
			return;
		}
	}

	private static List<StorageSpecification> BuildStorageSpecifications(JsonObject attributes)
	{
		List<StorageSpecification> storageSpecifications = new();

		JsonObject[] configuredPoints = attributes?["Points"].AsArray();
		if (configuredPoints != null)
		{
			for (int configuredPointIndex = 0; configuredPointIndex < configuredPoints.Length; configuredPointIndex++)
			{
				JsonObject configuredPoint = configuredPoints[configuredPointIndex];
				string attachmentPointCode = configuredPoint["AP"].AsString(null);
				if (string.IsNullOrEmpty(attachmentPointCode)) continue;

				storageSpecifications.Add(new StorageSpecification(attachmentPointCode, configuredPoint["QuantitySlots"].AsInt(36)));
			}
		}

		int[] slotCountsByPoint = attributes?["QuantitySlotsByPoint"].AsArray<int>(Array.Empty<int>()) ?? Array.Empty<int>();
		if (slotCountsByPoint.Length > 0)
		{
			string attachmentPointPrefix = attributes["Prefix"].AsString(DefaultPrefix);
			for (int storagePointIndex = 0; storagePointIndex < slotCountsByPoint.Length; storagePointIndex++)
			{
				storageSpecifications.Add(new StorageSpecification(attachmentPointPrefix + storagePointIndex, slotCountsByPoint[storagePointIndex]));
			}
		}

		int storagePointCount = attributes?["Count"].AsInt(0) ?? 0;
		if (storagePointCount > 0)
		{
			string attachmentPointPrefix = attributes["Prefix"].AsString(DefaultPrefix);
			int inventorySlotCount = attributes["QuantitySlots"].AsInt(36);
			for (int storagePointIndex = 0; storagePointIndex < storagePointCount; storagePointIndex++)
			{
				storageSpecifications.Add(new StorageSpecification(attachmentPointPrefix + storagePointIndex, inventorySlotCount));
			}
		}

		return storageSpecifications;
	}

	private void OpenClientDialog(IPlayer player, StoragePoint storagePoint, EntityBehaviorSGRefrigeration refrigeration)
	{
		if (ClientDialog != null && OpenedClientStoragePoint == storagePoint && ClientDialog.IsOpened()) return;

		CloseClientDialog();

		GUIDialogSGBoxcarStorage newDialog = new GUIDialogSGBoxcarStorage(storagePoint.Inventory, refrigeration, entity, entity.Api as ICoreClientAPI, "sgstorage", "yangtransport:sgcargocontainertitle");
		newDialog.OnClosed += delegate
		{
			newDialog.Dispose();
			if (ClientDialog == newDialog)
			{
				ClientDialog = null;
				OpenedClientStoragePoint = null;
			}
		};

		ClientDialog = newDialog;
		OpenedClientStoragePoint = storagePoint;

		if (newDialog.TryOpen())
		{
			ICoreClientAPI clientAPI = entity.World.Api as ICoreClientAPI;
			clientAPI?.Network.SendPacketClient(storagePoint.Inventory.Open(player));
			if (refrigeration?.Inventory != null) clientAPI?.Network.SendPacketClient(refrigeration.Inventory.Open(player));
		}
		else { CloseClientDialog(); }
	}

	private void CloseClientDialog()
	{
		GUIDialogSGBoxcarStorage oldDialog = ClientDialog;
		if (oldDialog == null) { OpenedClientStoragePoint = null; return; }

		if (ClientDialog == oldDialog)
		{
			ClientDialog = null;
			OpenedClientStoragePoint = null;
		}

		if (oldDialog.IsOpened())	{ oldDialog.TryClose(); }
		else						{ oldDialog.Dispose(); }
	}

	private void CloseOtherStorageInventories(IPlayer player, StoragePoint retainedStoragePoint)
	{
		if (player?.InventoryManager == null) return;

		for (int storagePointIndex = 0; storagePointIndex < StoragePoints.Count; storagePointIndex++)
		{
			StoragePoint storagePoint = StoragePoints[storagePointIndex];
			if (storagePoint == retainedStoragePoint || !storagePoint.Inventory.HasOpened(player)) continue;

			player.InventoryManager.CloseInventory(storagePoint.Inventory);
		}
	}

	private bool TryGetClickedStoragePoint(EntityPlayer player, out StoragePoint storagePoint)
	{
		return TryGetStoragePointForSelectionIndex(player.EntitySelection?.SelectionBoxIndex ?? -1, out storagePoint);
	}

	private bool TryGetStoragePointForSelectionIndex(int selectionBoxIndex, out StoragePoint storagePoint)
	{
		storagePoint = null;
		if (selectionBoxIndex <= 0) return false;

		var selectionBoxBehavior = entity.GetBehavior<EntityBehaviorSelectionBoxes>();
		AttachmentPointAndPose[] selectionBoxes = selectionBoxBehavior?.selectionBoxes;

		if ((selectionBoxes == null || selectionBoxes.Length == 0) && selectionBoxBehavior is EntityBehaviorSGBodySelectionBoxes standardGaugeSelectionBoxes)
		{
			selectionBoxes = standardGaugeSelectionBoxes.GetSelectionBoxesForDebug();
		}

		if (selectionBoxes != null && selectionBoxIndex <= selectionBoxes.Length)
		{
			string attachmentPointName = selectionBoxes[selectionBoxIndex - 1]?.AttachPoint?.Code;
			if (!string.IsNullOrEmpty(attachmentPointName) && StoragePointsByAttachmentPoint.TryGetValue(attachmentPointName, out storagePoint)) { return true; }
		}

		// Fallback for very early server interactions before AP poses have resolved.
		int pointIndex = selectionBoxIndex - 1;
		if (pointIndex >= 0 && pointIndex < StoragePoints.Count) { storagePoint = StoragePoints[pointIndex]; return true; }

		return false;
	}

	private void ResolveDoorAnimation()
	{
		if (string.IsNullOrEmpty(DoorOpenAnimationCode)) return;

		if (entity.Properties?.Client?.AnimationsByMetaCode != null && entity.Properties.Client.AnimationsByMetaCode.TryGetValue(DoorOpenAnimationCode, out AnimationMetaData animationMetadata))
		{
			DoorOpenAnimation = animationMetadata.Clone();
		}
	}

	private void RefreshDoorAnimationState()
	{
		if (entity.World.Side != EnumAppSide.Client) return;

		if (entity.WatchedAttributes.GetBool(DoorOpenWatchedAttribute)) { OpenDoor(); }
		else { CloseDoor(); }
	}

	private void OpenDoor()
	{
		if (DoorAnimationOpen || DoorOpenAnimation == null || entity.AnimManager?.Animator == null) return;

		entity.AnimManager.StartAnimation(DoorOpenAnimation);
		DoorAnimationOpen = true;
	}

	private void CloseDoor()
	{
		if (!DoorAnimationOpen || DoorOpenAnimation == null || entity.AnimManager?.Animator == null) return;

		entity.AnimManager.StopAnimation(DoorOpenAnimation.Animation);
		DoorAnimationOpen = false;
	}

	private void RefreshDoorOpenWatchedState(bool forceUpdate = false)
	{
		if (entity.World.Side != EnumAppSide.Server || SuppressDoorStateRefresh) return;

		bool anyInventoryOpen = AnyStorageInventoryOpen();
		if (!forceUpdate && entity.WatchedAttributes.GetBool(DoorOpenWatchedAttribute) == anyInventoryOpen) return;

		entity.WatchedAttributes.SetBool(DoorOpenWatchedAttribute, anyInventoryOpen);
		entity.WatchedAttributes.MarkPathDirty(DoorOpenWatchedAttribute);
	}

	private bool AnyStorageInventoryOpen()
	{
		for (int storagePointIndex = 0; storagePointIndex < StoragePoints.Count; storagePointIndex++)
		{
			if (StoragePoints[storagePointIndex].Inventory.openedByPlayerGUIds?.Count > 0) return true;
		}

		return false;
	}

	internal void AddTransitionSpeedDelegate(CustomGetTransitionSpeedMulDelegate transitionSpeedDelegate)
	{
		if (transitionSpeedDelegate == null) return;
		for (int storagePointIndex = 0; storagePointIndex < StoragePoints.Count; storagePointIndex++)
		{
			StoragePoints[storagePointIndex].Inventory.OnAcquireTransitionSpeed += transitionSpeedDelegate;
		}
	}

	internal void RemoveTransitionSpeedDelegate(CustomGetTransitionSpeedMulDelegate transitionSpeedDelegate)
	{
		if (transitionSpeedDelegate == null) return;
		for (int storagePointIndex = 0; storagePointIndex < StoragePoints.Count; storagePointIndex++)
		{
			StoragePoints[storagePointIndex].Inventory.OnAcquireTransitionSpeed -= transitionSpeedDelegate;
		}
	}

	internal void ServerUpdateTransitions()
	{
		if (entity.World.Side != EnumAppSide.Server) return;

		for (int storagePointIndex = 0; storagePointIndex < StoragePoints.Count; storagePointIndex++)
		{
			InventoryGeneric inventory = StoragePoints[storagePointIndex].Inventory;
			for (int slotIndex = 0; slotIndex < inventory.Count; slotIndex++)
			{
				ItemSlot slot = inventory[slotIndex];
				if (!slot.Empty) slot.Itemstack.Collectible.UpdateAndGetTransitionStates(entity.World, slot);
			}
		}
	}

	internal void StoreInventoriesForSnapshot() { StoreInventories(); }

	private void OnInventorySlotModified(int slotID) { StoreInventories(); }

	private void OnInventoryOpened(IPlayer player) { RefreshDoorOpenWatchedState(); }
	private void OnInventoryClosed(IPlayer player)
	{
		StoreInventories();
		RefreshDoorOpenWatchedState();
	}

	private void LoadInventories()
	{
		TreeAttribute inventoryRoot = entity.WatchedAttributes[InventoryClassName] as TreeAttribute;
		bool hasNewStorageTree = inventoryRoot != null;

		for (int storagePointIndex = 0; storagePointIndex < StoragePoints.Count; storagePointIndex++)
		{
			StoragePoint storagePoint = StoragePoints[storagePointIndex];
			if (hasNewStorageTree)
			{
				TreeAttribute inventoryTree = inventoryRoot[storagePoint.TreeKey] as TreeAttribute;
				if (inventoryTree != null) storagePoint.Inventory.FromTreeAttributes(inventoryTree);
			}
		}
	}

	private void StoreInventories()
	{
		TreeAttribute inventoryRoot = new TreeAttribute();

		for (int storagePointIndex = 0; storagePointIndex < StoragePoints.Count; storagePointIndex++)
		{
			StoragePoint storagePoint = StoragePoints[storagePointIndex];
			TreeAttribute inventoryTree = new TreeAttribute();
			storagePoint.Inventory.ToTreeAttributes(inventoryTree);
			inventoryRoot[storagePoint.TreeKey] = inventoryTree;
		}

		entity.WatchedAttributes[InventoryClassName] = inventoryRoot;
		entity.WatchedAttributes.MarkPathDirty(InventoryClassName);
		entity.World.BlockAccessor.GetChunkAtBlockPos(entity.Pos.AsBlockPos)?.MarkModified();
	}

	private readonly struct StorageSpecification
	{
		public readonly string AttachmentPointCode;
		public readonly int InventorySlotCount;

		public StorageSpecification(string attachmentPointCode, int inventorySlotCount)
		{
			AttachmentPointCode = attachmentPointCode;
			InventorySlotCount = inventorySlotCount;
		}
	}

	private sealed class StoragePoint
	{
		public int Index;
		public string AttachmentPointCode;
		public string TreeKey;
		public InventoryGeneric Inventory;
	}
}

/// Cargo inventory dialog for standard gauge storage points.
/// Keeps the vanilla creature-contents interaction semantics, but has a proper scaling window as well as more flexible interaction range.
public sealed class GUIDialogSGBoxcarStorage : GuiDialog
{
	private const int MinPreferredColumns = 4;
	private const int MaxColumns = 10;
	private const int MaxVisibleRows = 7;
	private const string DialogPositionCode = "smallblockgui";
	private const string CargoGridCode = "slots";
	private const string RefrigerantGridCode = "refrigerantSlots";
	private const string RefrigerantStatusCode = "refrigerantStatus";

	private readonly InventoryGeneric Inventory;
	private readonly EntityBehaviorSGRefrigeration Refrigeration;
	private readonly Entity OwningEntity;
	private readonly string DialogCode;
	private readonly string TitleLanguageCode;

	private EnumPosFlag ScreenPosition;
	private long NextRefrigerantTextUpdateMS;

	public int PacketIDOffset;

	public override string ToggleKeyCombinationCode => null;
	public override double DrawOrder => 0.2;
	public override bool UnregisterOnClose => true;
	public override bool PrefersUngrabbedMouse => false;

	private double FloatyDialogPosition => 0.6;
	private double FloatyDialogAlign => 0.8;

	public GUIDialogSGBoxcarStorage
	(
		InventoryGeneric inventory,
		EntityBehaviorSGRefrigeration refrigeration,
		Entity owningEntity,
		ICoreClientAPI clientAPI,
		string dialogCode,
		string titleLanguageCode = null
	) : base(clientAPI)
	{
		Inventory = inventory;
		Refrigeration = refrigeration;
		OwningEntity = owningEntity;
		DialogCode = dialogCode;
		TitleLanguageCode = titleLanguageCode;

		Compose();
	}

	private void Compose()
	{
		double slotPadding = GuiElementItemSlotGridBase.unscaledSlotPadding;
		int columns = GetColumnCount(Inventory.Count);
		int rows = (int)Math.Ceiling(Inventory.Count / (float)columns);
		int visibleRows = Math.Min(rows, MaxVisibleRows);
		bool needsScrollbar = visibleRows < rows;

		ElementBounds cargoGridBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, slotPadding, slotPadding, columns, visibleRows);
		ElementBounds fullCargoGridBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 0, 0, columns, rows);
		ElementBounds cargoInsetBounds = cargoGridBounds.ForkBoundingParent(6, 6, 6, 6);

		cargoInsetBounds.fixedY += Math.Max(0, GuiStyle.TitleBarHeight - GuiStyle.ElementToDialogPadding + GuiStyle.HalfPadding);

		double contentWidth = cargoInsetBounds.fixedX + cargoInsetBounds.fixedWidth;
		double contentHeight = cargoInsetBounds.fixedY + cargoInsetBounds.fixedHeight;

		ElementBounds refrigerantLabelBounds = null;
		ElementBounds refrigerantGridBounds = null;
		ElementBounds refrigerantStatusBounds = null;

		if (Refrigeration?.Inventory != null)
		{
			int refrigerantColumns = Math.Min(4, Math.Max(1, Refrigeration.Inventory.Count));
			int refrigerantRows = (int)Math.Ceiling(Refrigeration.Inventory.Count / (double)refrigerantColumns);

			double sectionTop = contentHeight + 12;
			refrigerantLabelBounds = ElementBounds.Fixed(6, sectionTop, Math.Max(120, contentWidth - 12), 20);
			refrigerantGridBounds = ElementStdBounds.SlotGrid(EnumDialogArea.None, 6, sectionTop + 20, refrigerantColumns, refrigerantRows);
			refrigerantStatusBounds = ElementBounds.Fixed
			(
				6,
				refrigerantGridBounds.fixedY + refrigerantGridBounds.fixedHeight + 6,
				Math.Max(contentWidth - 12, refrigerantGridBounds.fixedWidth),
				22
			);

			contentWidth = Math.Max(contentWidth, refrigerantGridBounds.fixedWidth + 12);
			contentHeight = refrigerantStatusBounds.fixedY + refrigerantStatusBounds.fixedHeight;
		}

		ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
		bgBounds.BothSizing = ElementSizing.FitToChildren;
		bgBounds.WithChild(cargoInsetBounds);
		if (refrigerantLabelBounds != null) bgBounds.WithChildren(refrigerantLabelBounds, refrigerantGridBounds, refrigerantStatusBounds);

		ScreenPosition = GetFreePos(DialogPositionCode);

		ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
			.WithFixedAlignmentOffset(IsRight(ScreenPosition) ? -GuiStyle.DialogToScreenPadding : GuiStyle.DialogToScreenPadding, 0)
			.WithAlignment(IsRight(ScreenPosition) ? EnumDialogArea.RightMiddle : EnumDialogArea.LeftMiddle);

		if (!capi.Settings.Bool["immersiveMouseMode"])
		{
			dialogBounds.fixedOffsetY += (contentHeight + 40) * YOffsetMul(ScreenPosition);
			dialogBounds.fixedOffsetX += (contentWidth + 10) * XOffsetMul(ScreenPosition);
		}

		string dialogTitle = Lang.Get(TitleLanguageCode ?? DialogCode);

		GuiComposer composer = capi.Gui
			.CreateCompo(DialogCode + OwningEntity.EntityId, dialogBounds)
			.AddShadedDialogBG(bgBounds)
			.AddDialogTitleBar(dialogTitle, OnTitleBarClose)
			.BeginChildElements(bgBounds)
			.AddInset(cargoInsetBounds);

		if (needsScrollbar)
		{
			ElementBounds clippingBounds = cargoGridBounds.CopyOffsetedSibling();
			clippingBounds.fixedHeight -= 3;
			ElementBounds scrollbarBounds = ElementStdBounds.VerticalScrollbar(cargoInsetBounds);

			composer
				.AddVerticalScrollbar(OnNewScrollbarValue, scrollbarBounds, "scrollbar")
				.BeginClip(clippingBounds)
				.AddItemSlotGrid(Inventory, DoSendPacket, columns, fullCargoGridBounds, CargoGridCode)
				.EndClip();
		}
		else
		{
			composer.AddItemSlotGrid(Inventory, DoSendPacket, columns, cargoGridBounds, CargoGridCode);
		}

		if (Refrigeration?.Inventory != null)
		{
			int refrigerantColumns = Math.Min(4, Math.Max(1, Refrigeration.Inventory.Count));

			composer
				.AddStaticText(Lang.Get("yangtransport:refrigeration-refrigerant"), CairoFont.WhiteDetailText().WithWeight(FontWeight.Bold), refrigerantLabelBounds)
				.AddItemSlotGrid(Refrigeration.Inventory, DoSendRefrigerantPacket, refrigerantColumns, refrigerantGridBounds, RefrigerantGridCode)
				.AddDynamicText(Refrigeration.GetCapacityStatusText(), CairoFont.WhiteDetailText(), refrigerantStatusBounds, RefrigerantStatusCode);
		}

		SingleComposer = composer.EndChildElements().Compose();

		if (needsScrollbar)
		{
			SingleComposer.GetScrollbar("scrollbar").SetHeights
			(
				(float)cargoGridBounds.fixedHeight,
				(float)(fullCargoGridBounds.fixedHeight + slotPadding)
			);
		}

		SingleComposer.UnfocusOwnElements();
	}

	private static int GetColumnCount(int slotCount)
	{
		if (slotCount <= 0) return 1;

		int maxColumns = Math.Min(MaxColumns, slotCount);
		int minColumns = Math.Min(MinPreferredColumns, maxColumns);
		int bestColumns = minColumns;
		int bestScore = int.MaxValue;

		for (int columns = minColumns; columns <= maxColumns; columns++)
		{
			if (slotCount % columns != 0) continue;

			int rows = slotCount / columns;
			int score = Math.Abs(columns - rows) * 100 - columns;
			if (score >= bestScore) continue;

			bestScore = score;
			bestColumns = columns;
		}

		if (bestScore != int.MaxValue) return bestColumns;

		for (int columns = minColumns; columns <= maxColumns; columns++)
		{
			int rows = (int)Math.Ceiling(slotCount / (float)columns);
			int emptyCells = columns * rows - slotCount;
			int score = emptyCells * 100 + Math.Abs(columns - rows) * 10 - columns;
			if (score >= bestScore) continue;

			bestScore = score;
			bestColumns = columns;
		}

		return bestColumns;
	}

	private void DoSendPacket(object packet)
	{
		capi.Network.SendEntityPacketWithOffset(OwningEntity.EntityId, PacketIDOffset, packet);
	}

	private void DoSendRefrigerantPacket(object packet)
	{
		capi.Network.SendEntityPacketWithOffset(OwningEntity.EntityId, EntityBehaviorSGRefrigeration.InventoryPacketOffset, packet);
	}

	private void OnNewScrollbarValue(float value)
	{
		ElementBounds bounds = SingleComposer.GetSlotGrid(CargoGridCode).Bounds;
		bounds.fixedY = 10 - GuiElementItemSlotGridBase.unscaledSlotPadding - value;
		bounds.CalcWorldBounds();
	}

	private void OnTitleBarClose() { TryClose(); }

	public override void OnGuiOpened()
	{
		base.OnGuiOpened();

		if (capi.Gui.GetDialogPosition(SingleComposer.DialogName) == null) { OccupyPos(DialogPositionCode, ScreenPosition); }
	}

	public override void OnGuiClosed()
	{
		base.OnGuiClosed();

		capi.World.Player.InventoryManager.CloseInventoryAndSync(Inventory);
		SingleComposer.GetSlotGrid(CargoGridCode).OnGuiClosed(capi);

		if (Refrigeration?.Inventory != null)
		{
			capi.World.Player.InventoryManager.CloseInventoryAndSync(Refrigeration.Inventory);
			SingleComposer.GetSlotGrid(RefrigerantGridCode)?.OnGuiClosed(capi);
		}

		FreePos(DialogPositionCode, ScreenPosition);
	}

	public override void OnRenderGUI(float deltaTime)
	{
		if (Refrigeration != null && capi.ElapsedMilliseconds >= NextRefrigerantTextUpdateMS)
		{
			NextRefrigerantTextUpdateMS = capi.ElapsedMilliseconds + 1000;
			SingleComposer.GetDynamicText(RefrigerantStatusCode)?.SetNewText(Refrigeration.GetCapacityStatusText());
		}

		if (capi.Settings.Bool["immersiveMouseMode"])
		{
			double entityOffsetX = OwningEntity.SelectionBox.X2 - OwningEntity.OriginSelectionBox.X2;
			double entityOffsetZ = OwningEntity.SelectionBox.Z2 - OwningEntity.OriginSelectionBox.Z2;
			Vec3d aboveHeadPosition = new Vec3d(OwningEntity.Pos.X + entityOffsetX, OwningEntity.Pos.Y + FloatyDialogPosition, OwningEntity.Pos.Z + entityOffsetZ);
			Vec3d projectedPosition = MatrixToolsd.Project(aboveHeadPosition, capi.Render.PerspectiveProjectionMat, capi.Render.PerspectiveViewMat, capi.Render.FrameWidth, capi.Render.FrameHeight);

			if (projectedPosition.Z < 0) return;

			SingleComposer.Bounds.Alignment = EnumDialogArea.None;
			SingleComposer.Bounds.fixedOffsetX = 0;
			SingleComposer.Bounds.fixedOffsetY = 0;
			SingleComposer.Bounds.absFixedX = projectedPosition.X - SingleComposer.Bounds.OuterWidth / 2;
			SingleComposer.Bounds.absFixedY = capi.Render.FrameHeight - projectedPosition.Y - SingleComposer.Bounds.OuterHeight * FloatyDialogAlign;
			SingleComposer.Bounds.absMarginX = 0;
			SingleComposer.Bounds.absMarginY = 0;
		}

		base.OnRenderGUI(deltaTime);
	}

	public override void OnFinalizeFrame(float deltaTime)
	{
		base.OnFinalizeFrame(deltaTime);

		if (!IsInInteractionRange()) { capi.Event.EnqueueMainThreadTask(delegate { TryClose(); }, "closedlg"); }
	}

	private bool IsInInteractionRange()
	{
		EntityPlayer playerEntity = capi.World.Player.Entity;
		Vec3d eyePosition = playerEntity.Pos.XYZ.Add(playerEntity.LocalEyePos);
		float range = capi.World.Player.WorldData.PickingRange;

		return OwningEntity.InRangeOf(eyePosition, range * range, range);
	}
}
