using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace YangTransport;

/// Shared block entity used by all signals to drive their light meshes.
/// Expected light groups:
///		- North-facing light: NFS_R, NFS_Y, NFS_G
///		- South-facing light: SFS_R, SFS_Y, SFS_G
/// Each group shows exactly one element and disables the other two during tessellation.
public sealed class BlockEntityRailSignal : BlockEntity
{
	private const string NorthFacingAspectAttributeKey = "nfsAspect";
	private const string SouthFacingAspectAttributeKey = "sfsAspect";
	private const string MeshCacheObjectKey = "yangtransport:railSignalMeshes";

	private SignalAspect CurrentNorthFacingAspect = SignalAspect.Green;
	private SignalAspect CurrentSouthFacingAspect = SignalAspect.Green;

	// Shared per-client-session mesh cache: key = (blockId << 4) | (nfs << 2) | sfs.
	// MeshData contains raw OpenGL atlas texture IDs, so it must never outlive the ICoreAPI/client world that created it.
	private Dictionary<long, MeshData>? MeshCache;

	public SignalAspect NorthFacingAspect => CurrentNorthFacingAspect;
	public SignalAspect SouthFacingAspect => CurrentSouthFacingAspect;

	public override void Initialize(ICoreAPI coreAPI)
	{
		base.Initialize(coreAPI);

		// Register as a loaded display target. The server system computes current aspects on demand.
		if (coreAPI.Side == EnumAppSide.Server) { coreAPI.ModLoader.GetModSystem<RailGraphServerSystem>()?.RegisterLoadedSignal(Pos); }
		
		// ObjectCache belongs to this client API instance, unlike a static field which survives world/client reloads.
		else {  MeshCache = ObjectCacheUtil.GetOrCreate(coreAPI, MeshCacheObjectKey, () => new Dictionary<long, MeshData>()); }
	}

	public override void OnBlockUnloaded()
	{
		if (Api?.Side == EnumAppSide.Server) { Api.ModLoader.GetModSystem<RailGraphServerSystem>()?.UnregisterLoadedSignal(Pos); }
		base.OnBlockUnloaded();
	}

	public override void OnBlockRemoved()
	{
		if (Api?.Side == EnumAppSide.Server) {Api.ModLoader.GetModSystem<RailGraphServerSystem>()?.UnregisterLoadedSignal(Pos); }
		base.OnBlockRemoved();
	}

	public void SetAspects(SignalAspect newNorthFacingAspect, SignalAspect newSouthFacingAspect, bool markDirty = true)
	{
		if (CurrentNorthFacingAspect == newNorthFacingAspect && CurrentSouthFacingAspect == newSouthFacingAspect) return;

		CurrentNorthFacingAspect = newNorthFacingAspect;
		CurrentSouthFacingAspect = newSouthFacingAspect;

		if (markDirty) MarkDirty(true);
	}

	public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
	{
		base.FromTreeAttributes(tree, worldAccessForResolve);

		SignalAspect newNorthFacingAspect = (SignalAspect)tree.GetInt(NorthFacingAspectAttributeKey, (int)SignalAspect.Green);
		SignalAspect newSouthFacingAspect = (SignalAspect)tree.GetInt(SouthFacingAspectAttributeKey, (int)SignalAspect.Green);

		if (newNorthFacingAspect != CurrentNorthFacingAspect || newSouthFacingAspect != CurrentSouthFacingAspect)
		{
			CurrentNorthFacingAspect = newNorthFacingAspect;
			CurrentSouthFacingAspect = newSouthFacingAspect;

			// Client: trigger a chunk remesh for the new aspects.
			if (Api?.Side == EnumAppSide.Client) { MarkDirty(true); }
		}
	}

	public override void ToTreeAttributes(ITreeAttribute tree)
	{
		base.ToTreeAttributes(tree);
		tree.SetInt(NorthFacingAspectAttributeKey, (int)CurrentNorthFacingAspect);
		tree.SetInt(SouthFacingAspectAttributeKey, (int)CurrentSouthFacingAspect);
	}

	public override void GetBlockInfo(IPlayer forPlayer, StringBuilder description) { base.GetBlockInfo(forPlayer, description); }

	public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
	{
		// Allow BE behaviors to add geometry (we still provide the full block mesh).
		for (int behaviorIndex = 0; behaviorIndex < Behaviors.Count; behaviorIndex++) { Behaviors[behaviorIndex].OnTesselation(mesher, tesselator); }

		Block block = Api.World.BlockAccessor.GetBlock(Pos);
		Dictionary<long, MeshData> cache = MeshCache ??= ObjectCacheUtil.GetOrCreate(Api, MeshCacheObjectKey, () => new Dictionary<long, MeshData>());
		MeshData mesh = GetOrCreateMesh(Api, tesselator, block, CurrentNorthFacingAspect, CurrentSouthFacingAspect, cache);
		mesher.AddMeshData(mesh);

		// Skip default mesh, we already provided the full mesh with our selective signal elements.
		return true;
	}

	private static MeshData GetOrCreateMesh(ICoreAPI coreAPI, ITesselatorAPI tesselator, Block block, SignalAspect northFacingAspect, SignalAspect southFacingAspect, Dictionary<long, MeshData> meshCache)
	{
		if (block?.Shape?.Base == null) return new MeshData();

		long meshCacheKey = ((long)block.Id << 4) | ((byte)northFacingAspect << 2) | (byte)southFacingAspect;
		if (meshCache.TryGetValue(meshCacheKey, out MeshData cachedMesh)) return cachedMesh;

		// Load the resolved Shape from assets.
		AssetLocation shapeLocation = block.Shape.Base.Clone().WithPathPrefixOnce("shapes/");
		Shape shape = Shape.TryGet(coreAPI, shapeLocation.ToString() + ".json");
		if (shape == null) return new MeshData();

		// Clone so we can disable elements without mutating the shared cached Shape instance.
		Shape filteredShape = new Shape
		{
			Textures = shape.Textures,
			Elements = shape.CloneElements(),
			Animations = shape.Animations,
			TextureWidth = shape.TextureWidth,
			TextureHeight = shape.TextureHeight,
			TextureSizes = shape.TextureSizes
		};

		ApplyLightGroup(filteredShape.Elements, prefix: "NFS_", northFacingAspect);
		ApplyLightGroup(filteredShape.Elements, prefix: "SFS_", southFacingAspect);

		// Source textures from the mesh itself. Rotation matches the CompositeShape rotate fields.
		Vec3f rotation = new Vec3f(block.Shape.rotateX, block.Shape.rotateY, block.Shape.rotateZ);
		tesselator.TesselateShape(block, filteredShape, out MeshData mesh, rotation);

		meshCache[meshCacheKey] = mesh;
		return mesh;
	}

	private static void ApplyLightGroup(ShapeElement[] elements, string prefix, SignalAspect aspect)
	{
		// Yellow is optional, mostly used by complex signals like the Chain signal.
		string yellowElementName	= prefix + "Y";
		if (aspect == SignalAspect.Yellow && !HasElementByName(elements, yellowElementName)) { aspect = SignalAspect.Green; }

		string targetElementName	= prefix + (aspect == SignalAspect.Red ? "R" : aspect == SignalAspect.Green ? "G" : "Y");

		string redElementName		= prefix + "R";
		string greenElementName		= prefix + "G";

		if (!string.Equals(targetElementName, redElementName, StringComparison.OrdinalIgnoreCase)) DisableElementByName(elements, redElementName);
		if (!string.Equals(targetElementName, yellowElementName, StringComparison.OrdinalIgnoreCase)) DisableElementByName(elements, yellowElementName);
		if (!string.Equals(targetElementName, greenElementName, StringComparison.OrdinalIgnoreCase)) DisableElementByName(elements, greenElementName);
	}

	private static bool HasElementByName(ShapeElement[] elements, string name)
	{
		if (elements == null) return false;

		for (int elementIndex = 0; elementIndex < elements.Length; elementIndex++)
		{
			ShapeElement element = elements[elementIndex];
			if (element == null) continue;

			if (string.Equals(element.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
			if (element.Children != null && element.Children.Length > 0 && HasElementByName(element.Children, name)) return true;
		}

		return false;
	}

	private static void DisableElementByName(ShapeElement[] elements, string name)
	{
		if (elements == null) return;

		for (int elementIndex = 0; elementIndex < elements.Length; elementIndex++)
		{
			ShapeElement element = elements[elementIndex];
			if (element == null) continue;

			// The VS tessellator still renders child elements even if the parent has no faces, so disabling a signal aspect must hide the entire named subtree.
			if (string.Equals(element.Name, name, StringComparison.OrdinalIgnoreCase)) {  DisableElementTree(element); continue; }

			if (element.Children != null && element.Children.Length > 0) { DisableElementByName(element.Children, name); }
		}
	}

	private static void DisableElementTree(ShapeElement element)
	{
		if (element == null) return;

		// For tessellation, a face is considered disabled when FacesResolved[faceIndex] == null.
		// ShapeElement.Clone() clones FacesResolved arrays, so nulling entries here is safe.
		var resolvedFaces = element.FacesResolved;
		if (resolvedFaces != null) { for (int faceIndex = 0; faceIndex < resolvedFaces.Length; faceIndex++) { resolvedFaces[faceIndex] = null; } }

		if (element.Children == null) return;
		for (int childIndex = 0; childIndex < element.Children.Length; childIndex++) { DisableElementTree(element.Children[childIndex]); }
	}
}

public enum SignalAspect : byte { Red = 0, Yellow = 1, Green = 2 } // Visual aspect shown by a rail signal light.
