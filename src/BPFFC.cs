using CollisionFlowFields;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace YangTransport;

internal static class CollisionFlowFieldPlacement // For anything related to the Back-Propagated Flow Field Collisions (BPFFC) dependency. Mostly block placement.
{
	// Returns false and sets notreplaceable as the failure code when the CollisionFlowFields mod blocks placement. If the mod isn't installed, always returns true.
	public static bool CanPlace(ICoreAPI coreAPI, IBlockAccessor blockAccessor, BlockPos position, Block block, ref string failureCode)
	{
		var collisionflowfields = coreAPI.ModLoader.GetModSystem<ModSystemCollisionFlowFields>();
		if (collisionflowfields != null && !collisionflowfields.CanPlace(blockAccessor, position, block))
		{
			failureCode = "notreplaceable"; // vanilla code, auto-translated
			return false;
		}

		return true;
	}
}

internal static class HeldGhostPreview // Shader rendering in 1.22 is totally fucked up! And EVIL!
{
	private static readonly Vec4f TintOk = new(1.15f, 1.15f, 1.15f, 0.50f);
	private static readonly Vec4f TintBad = new(1f, 0.5f, 0.5f, 0.65f);
	private static readonly Vec3f FullAmbient = new(1f, 1f, 1f);
	private static readonly Vec4f FullLight = new(1f, 1f, 1f, 1f);
	private static readonly Vec4f WhiteWash = new(1f, 1f, 1f, 0.15f);
	private static readonly int[] WhitePixel = { ColorUtil.ToRgba(255, 255, 255, 255) };

	private static AfterOitRenderer? Renderer;

	public static BlockSelection MakePlacementSelection(BlockSelection rawSelection, IClientPlayer byPlayer, Block heldBlock)
	{
		// BlockSelection.Clone() does NOT copy .Block, so copy it explicitly.
		BlockSelection placementSelection = rawSelection.Clone();
		placementSelection.Block = rawSelection.Block;

		if (!rawSelection.DidOffset)
		{
			// If the looked-at block isn't replaceable, placement is in front of it.
			Block lookedAtBlock = byPlayer.Entity.World.BlockAccessor.GetBlock(rawSelection.Position);
			if (!lookedAtBlock.IsReplacableBy(heldBlock))
			{
				placementSelection.Position.Add(rawSelection.Face.Normali);
				placementSelection.DidOffset = true;
			}
		}

		return placementSelection;
	}

	public static void RenderOit(ICoreClientAPI clientAPI, IClientPlayer byPlayer, BlockPos position, Block placedBlock, bool canPlaceHere, Matrixf modelMatrix)
	{
		// Use the engine's cached mesh ref (no custom caching, or per-frame uploads).
		MultiTextureMeshRef meshReference = clientAPI.TesselatorManager.GetDefaultBlockMeshRef(placedBlock);
		if (meshReference == null) return;

		if (Renderer == null || !Renderer.IsFor(clientAPI))
		{
			Renderer = new AfterOitRenderer(clientAPI);
			clientAPI.Event.RegisterRenderer(Renderer, EnumRenderStage.AfterFinalComposition, "yangtransport-heldghost-preview");
		}

		Renderer.Queue(meshReference, position, canPlaceHere);
	}

	private sealed class AfterOitRenderer : IRenderer
	{
		private readonly ICoreClientAPI ClientAPI;
		private readonly Matrixf ModelMatrix = new();

		private MultiTextureMeshRef? MeshReference;
		private BlockPos? Position;
		private bool CanPlaceHere;
		private LoadedTexture? WhiteTexture;

		public double RenderOrder => 0.91;
		public int RenderRange => 1;

		public AfterOitRenderer(ICoreClientAPI clientAPI) { this.ClientAPI = clientAPI; }
		public bool IsFor(ICoreClientAPI clientAPI) { return ReferenceEquals(this.ClientAPI, clientAPI); }

		public void Queue(MultiTextureMeshRef meshReference, BlockPos position, bool canPlaceHere)
		{
			this.MeshReference = meshReference;
			this.Position = position.Copy();
			this.CanPlaceHere = canPlaceHere;
		}

		private int WhiteTextureId()
		{
			LoadedTexture? texture = WhiteTexture;

			if (texture == null || texture.Disposed)
			{
				texture = new LoadedTexture(ClientAPI, 0, 1, 1);
				ClientAPI.Render.LoadOrUpdateTextureFromRgba(WhitePixel, false, 0, ref texture);
				WhiteTexture = texture;
			}

			return texture.TextureId;
		}

		public void OnRenderFrame(float deltaTime, EnumRenderStage renderStage)
		{
			MultiTextureMeshRef? meshReference = this.MeshReference;
			BlockPos? position = this.Position;

			this.MeshReference = null;
			this.Position = null;

			if (meshReference == null || position == null || meshReference.Disposed) return;

			IShaderProgram? previousShader = ClientAPI.Render.CurrentActiveShader;
			previousShader?.Stop();

			IStandardShaderProgram sstandardShaderd = ClientAPI.Render.PreparedStandardShader(position.X, position.InternalY, position.Z);

			sstandardShaderd.AlphaTest = 0.0001f;
			sstandardShaderd.RgbaTint = CanPlaceHere ? TintOk : TintBad;

			// Make the ghost read as a preview, not as a newly-lit world block
			sstandardShaderd.NormalShaded = 0;
			sstandardShaderd.RgbaAmbientIn = FullAmbient;
			sstandardShaderd.RgbaLightIn = FullLight;
			sstandardShaderd.SsaoAttn = 0f;

			// Pull very slightly toward the camera to avoid coplanar shimmer on rails and other flat faces
			sstandardShaderd.ExtraZOffset = -0.0008f;

			Vec3d cameraPosition = ClientAPI.World.Player.Entity.CameraPos;
			sstandardShaderd.ModelMatrix = ModelMatrix
				.Identity()
				.Translate(position.X - cameraPosition.X, position.InternalY - cameraPosition.Y, position.Z - cameraPosition.Z)
				.Values;

			sstandardShaderd.ViewMatrix = ClientAPI.Render.CameraMatrixOriginf;
			sstandardShaderd.ProjectionMatrix = ClientAPI.Render.CurrentProjectionMatrix;

			// Keep engine culling intact. Disabling culling makes transparent block meshes blend with their own back faces.
			ClientAPI.Render.GLEnableDepthTest();

			// Depth-only pre-pass. Write near faces to the buffer. Translucent passes then use Lequal, this gives translucent self-occlusion without touching OIT.
			ClientAPI.Render.GlToggleBlend(false);
			ClientAPI.Render.GLDepthMask(true);
			ClientAPI.Render.GlColorMask(false, false, false, false);
			ClientAPI.Render.RenderMultiTextureMesh(meshReference, "tex", 0);

			ClientAPI.Render.GlColorMask(true, true, true, true);
			ClientAPI.Render.GLDepthMask(false);
			ClientAPI.Render.GlToggleBlend(true);
			GL.DepthFunc(DepthFunction.Lequal);

			ClientAPI.Render.RenderMultiTextureMesh(meshReference, "tex", 0);

			if (CanPlaceHere)
			{
				sstandardShaderd.RgbaTint = WhiteWash;
				sstandardShaderd.Tex2D = WhiteTextureId();

				for (int meshIndex = 0; meshIndex < meshReference.meshrefs.Length; meshIndex++) { ClientAPI.Render.RenderMesh(meshReference.meshrefs[meshIndex]); }
			}

			GL.DepthFunc(DepthFunction.Less);
			ClientAPI.Render.GLDepthMask(true);
			ClientAPI.Render.GlToggleBlend(false);

			((IShaderProgram)sstandardShaderd).Stop();
			previousShader?.Use();
		}

		public void Dispose()
		{
			MeshReference = null;
			Position = null;
			WhiteTexture?.Dispose();
			WhiteTexture = null;
		}
	}
}
