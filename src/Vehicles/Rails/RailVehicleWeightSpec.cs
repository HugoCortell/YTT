using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace YangTransport;

/// Cached rail-vehicle weight rules. Parsed once from entity JSON, evaluated only when attachable inventory changes.
internal sealed class RailVehicleWeightSpec
{
	public static readonly RailVehicleWeightSpec MinecartDefault = new(1, 0, null, null);
	public static readonly RailVehicleWeightSpec StandardGaugeDefault = new(5, 0, null, null);

	private readonly int AttachmentDefaultWeight;
	private readonly Dictionary<string, int>? WeightByCategoryCode;
	private readonly Dictionary<AssetLocation, int>? WeightByCollectibleCode;

	public int SelfWeight { get; }

	private RailVehicleWeightSpec
	(
		int selfWeight,
		int attachmentDefaultWeight,
		Dictionary<string, int>? weightByCategoryCode,
		Dictionary<AssetLocation, int>? weightByCollectibleCode
	)
	{
		SelfWeight = Math.Max(1, selfWeight);
		this.AttachmentDefaultWeight = Math.Max(0, attachmentDefaultWeight);
		this.WeightByCategoryCode = weightByCategoryCode;
		this.WeightByCollectibleCode = weightByCollectibleCode;
	}

	public static RailVehicleWeightSpec FromJson(JsonObject? configurationRoot, int defaultSelfWeight, int defaultAttachmentWeight)
	{
		if (configurationRoot == null || !configurationRoot.Exists) return new RailVehicleWeightSpec(defaultSelfWeight, defaultAttachmentWeight, null, null);

		JsonObject attachmentRoot = configurationRoot["AttachmentWeight"];
		int selfWeight = Math.Max(1, configurationRoot["SelfWeight"].AsInt(defaultSelfWeight));

		int attachmentDefaultWeight = Math.Max(0, defaultAttachmentWeight);
		Dictionary<string, int>? weightsByCategoryCode = null;
		Dictionary<AssetLocation, int>? weightsByCollectibleCode = null;

		if (attachmentRoot != null && attachmentRoot.Exists)
		{
			attachmentDefaultWeight = Math.Max(0, attachmentRoot["Default"].AsInt(attachmentDefaultWeight));
			weightsByCategoryCode = ReadStringWeightMap(attachmentRoot["ByCategoryCode"]);
			weightsByCollectibleCode = ReadCollectibleCodeWeightMap(attachmentRoot["ByCode"]);
		}

		return new RailVehicleWeightSpec(selfWeight, attachmentDefaultWeight, weightsByCategoryCode, weightsByCollectibleCode);
	}

	public int ComputeTotalWeight(EntityBehaviorAttachable? attachable)
	{
		int weight = SelfWeight;
		InventoryBase? attachableInventory = attachable?.Inventory;
		if (attachableInventory == null) return weight;

		for (int slotIndex = 0; slotIndex < attachableInventory.Count; slotIndex++)
		{
			ItemSlot inventorySlot = attachableInventory[slotIndex]; if (inventorySlot.Empty) continue;
			ItemStack? itemStack = inventorySlot.Itemstack; if (itemStack == null) continue;

			weight += ResolveAttachmentWeight(itemStack);
		}

		return Math.Max(1, weight);
	}

	private int ResolveAttachmentWeight(ItemStack itemStack)
	{
		CollectibleObject? collectible = itemStack.Collectible;
		if (collectible == null) return AttachmentDefaultWeight;

		if (WeightByCollectibleCode != null && collectible.Code != null && WeightByCollectibleCode.TryGetValue(collectible.Code, out int exactWeight))
		{
			return exactWeight;
		}

		string? categoryCode = IAttachableToEntity.FromCollectible(collectible)?.GetCategoryCode(itemStack);
		if (categoryCode != null && WeightByCategoryCode != null && WeightByCategoryCode.TryGetValue(categoryCode, out int categoryWeight))
		{
			return categoryWeight;
		}

		return AttachmentDefaultWeight;
	}

	private static Dictionary<string, int>? ReadStringWeightMap(JsonObject? weightMapRoot)
	{
		if (weightMapRoot == null || !weightMapRoot.Exists || weightMapRoot.Token is not JObject weightMapObject) return null;

		Dictionary<string, int>? weightsByCode = null;
		foreach (JProperty weightProperty in weightMapObject.Properties())
		{
			weightsByCode ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			weightsByCode[weightProperty.Name] = ReadWeight(weightProperty.Value);
		}

		return weightsByCode;
	}

	private static Dictionary<AssetLocation, int>? ReadCollectibleCodeWeightMap(JsonObject? weightMapRoot)
	{
		if (weightMapRoot == null || !weightMapRoot.Exists || weightMapRoot.Token is not JObject weightMapObject) return null;

		Dictionary<AssetLocation, int>? weightsByCode = null;
		foreach (JProperty weightProperty in weightMapObject.Properties())
		{
			if (string.IsNullOrWhiteSpace(weightProperty.Name)) continue;

			weightsByCode ??= new Dictionary<AssetLocation, int>();
			weightsByCode[new AssetLocation(weightProperty.Name)] = ReadWeight(weightProperty.Value);
		}

		return weightsByCode;
	}

	private static int ReadWeight(JToken token) { return Math.Max(0, new JsonObject(token).AsInt(0)); }
}
