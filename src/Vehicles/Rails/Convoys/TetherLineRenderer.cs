using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace YangTransport;

// Client-only coupler tether renderer. One batched, camera-facing, alpha-cut chain quad per visible linked cart pair.
public sealed class TetherBillboardRenderer : ModSystem, IRenderer, IDisposable
{
	private const int MaxLinks = 128;
	private const int VerticesPerLink = 4;
	private const int IndicesPerLink = 6;

	private const float ChainHalfWidth = 0.14f;
	private const float MinLinkLength = 0.05f;
	private const float MaxShortLinkLength = 4.0f;	// Minecart sanity guard for stale/desynced prevCartId visuals
	private const float MaxLongLinkLength = 12.0f;	// SG bogie-center links can legitimately approach/exceed 4 blocks

	private const float BaseMetalR = 0.72f;
	private const float BaseMetalG = 0.72f;
	private const float BaseMetalB = 0.70f;

	private static readonly AssetLocation ChainTextureLocation = new("yangtransport:convoytether.png");
	private static readonly AssetLocation LongChainTextureLocation = new("yangtransport:convoytether_long.png");
	private static readonly Vec3f FullBrightAmbient = new(1f, 1f, 1f);
	private static readonly Vec4f FullBrightLight = new(1f, 1f, 1f, 1f);
	private static readonly Vec4f WhiteTint = new(1f, 1f, 1f, 1f);

	private ICoreClientAPI CoreAPI = null!;
	private MeshRef? MeshReference;
	private MeshData MeshData = null!;
	private int ChainTextureID;
	private int LongChainTextureID;

	private readonly int[] DrawIndexStarts = new int[1];
	private readonly int[] DrawIndexSizes = new int[1];

	private readonly Matrixf ModelMatrix = new();

	public double RenderOrder => 0.9;
	public int RenderRange => 96;

	public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

	public override void StartClientSide(ICoreClientAPI coreClientAPI)
	{
		CoreAPI = coreClientAPI;
		CoreAPI.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "yangtransport:tethers");

		ChainTextureID = CoreAPI.Render.GetOrLoadTexture(ChainTextureLocation);
		LongChainTextureID = CoreAPI.Render.GetOrLoadTexture(LongChainTextureLocation);

		int maxVertices = MaxLinks * VerticesPerLink;
		int maxIndices = MaxLinks * IndicesPerLink;

		MeshData = new MeshData(maxVertices, maxIndices, withNormals: false, withUv: true, withRgba: true, withFlags: true);
		MeshData.SetMode(EnumDrawMode.Triangles);
		MeshData.XyzStatic = false;
		MeshData.UvStatic = false;
		MeshData.RgbaStatic = false;
		MeshData.FlagsStatic = true;
		MeshData.IndicesStatic = true;

		for (int linkIndex = 0; linkIndex < MaxLinks; linkIndex++)
		{
			int vertexIndex = linkIndex * VerticesPerLink;
			int indexOffset = linkIndex * IndicesPerLink;

			MeshData.Indices[indexOffset + 0] = vertexIndex + 0;
			MeshData.Indices[indexOffset + 1] = vertexIndex + 1;
			MeshData.Indices[indexOffset + 2] = vertexIndex + 2;
			MeshData.Indices[indexOffset + 3] = vertexIndex + 0;
			MeshData.Indices[indexOffset + 4] = vertexIndex + 2;
			MeshData.Indices[indexOffset + 5] = vertexIndex + 3;
		}

		// Upload full capacity once, per-frame updates only change logical counts and vertex payloads.
		MeshData.VerticesCount = maxVertices;
		MeshData.IndicesCount = maxIndices;
		MeshReference = CoreAPI.Render.UploadMesh(MeshData);
	}

	public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
	{
		if (MeshReference == null || ChainTextureID == 0) return;

		IClientPlayer? clientPlayer = CoreAPI.World.Player;
		EntityPlayer? player = clientPlayer?.Entity;
		if (player == null) return;

		Vec3d cameraPosition = player.CameraPos;
		Vec3f cameraForward = EntityPos.GetViewVector(clientPlayer.CameraPitch, clientPlayer.CameraYaw);
		double renderRangeSQ = RenderRange * RenderRange;
		int shortLinkCount = 0;
		int longLinkCount = 0;

		// Avoid GetEntitiesAround(), this runs every frame and should stay allocation-free.
		foreach (var entityEntry in CoreAPI.World.LoadedEntities)
		{
			if (shortLinkCount + longLinkCount >= MaxLinks) break;
			Entity cartEntity = entityEntry.Value;
			if (!TryGetPreviousVehicleID(cartEntity, out long previousVehicleID)) continue;

			double dx = cartEntity.Pos.X - cameraPosition.X;
			double dy = cartEntity.Pos.InternalY - cameraPosition.Y;
			double dz = cartEntity.Pos.Z - cameraPosition.Z;
			if (dx * dx + dy * dy + dz * dz > renderRangeSQ) continue;

			if (previousVehicleID == 0) continue;
			Entity previousEntity = CoreAPI.World.GetEntityById(previousVehicleID); if (previousEntity == null) continue;

			bool useLongTexture = cartEntity is EntityStandardGaugeLocomotive || previousEntity is EntityStandardGaugeLocomotive;
			float maxLinkLength = useLongTexture ? MaxLongLinkLength : MaxShortLinkLength;
			int linkIndex = useLongTexture ? MaxLinks - longLinkCount - 1 : shortLinkCount;

			if (WriteChainQuad(linkIndex, previousEntity, cartEntity, cameraPosition, cameraForward, maxLinkLength))
			{
				if (useLongTexture) { longLinkCount++; }
				else { shortLinkCount++; }
			}
		}

		if (shortLinkCount == 0 && longLinkCount == 0) return;

		MeshData.VerticesCount = longLinkCount > 0 ? MaxLinks * VerticesPerLink : shortLinkCount * VerticesPerLink;
		MeshData.IndicesCount = longLinkCount > 0 ? MaxLinks * IndicesPerLink : shortLinkCount * IndicesPerLink;
		CoreAPI.Render.UpdateMesh(MeshReference, MeshData);

		IRenderAPI renderAPI = CoreAPI.Render;
		IStandardShaderProgram shader = renderAPI.PreparedStandardShader((int)cameraPosition.X, (int)cameraPosition.Y, (int)cameraPosition.Z);

		renderAPI.GlDisableCullFace();
		renderAPI.GlToggleBlend(false);
		shader.RgbaTint = WhiteTint;

		// Lighting is baked into vertex colors per tether, so keep the Standard shader's light term neutral.
		// This preserves fog/depth/alpha-test behavior without making the chain fullbright at night.
		shader.RgbaAmbientIn = FullBrightAmbient;
		shader.RgbaLightIn = FullBrightLight;
		shader.NormalShaded = 0;
		shader.ExtraGlow = 0;
		shader.ExtraGodray = 0f;
		shader.SsaoAttn = 0f;
		shader.AlphaTest = 0.28f;
		shader.OverlayOpacity = 0f;
		shader.DamageEffect = 0f;
		shader.DontWarpVertices = 1;
		shader.AddRenderFlags = 0;
		shader.ModelMatrix = ModelMatrix.Identity().Values;
		shader.ViewMatrix = renderAPI.CameraMatrixOriginf;
		shader.ProjectionMatrix = renderAPI.CurrentProjectionMatrix;

		if (shortLinkCount > 0)
		{
			shader.Tex2D = ChainTextureID;
			DrawIndexStarts[0] = 0;
			DrawIndexSizes[0] = shortLinkCount * IndicesPerLink;
			renderAPI.RenderMesh(MeshReference, DrawIndexStarts, DrawIndexSizes, 1);
		}

		if (longLinkCount > 0)
		{
			shader.Tex2D = LongChainTextureID;
			DrawIndexStarts[0] = (MaxLinks - longLinkCount) * IndicesPerLink * MeshData.IndexSize;
			DrawIndexSizes[0] = longLinkCount * IndicesPerLink;
			renderAPI.RenderMesh(MeshReference, DrawIndexStarts, DrawIndexSizes, 1);
		}

		shader.Stop();
		renderAPI.GlEnableCullFace();
	}

	private bool WriteChainQuad(int linkIndex, Entity frontCart, Entity rearCart, Vec3d cameraPosition, Vec3f cameraForward, float maxLinkLength)
	{
		if (!TryGetTetherEndpoints(frontCart, rearCart, preferRenderedStandardGaugePose: true, out double ax, out double ay, out double az, out double bx, out double by, out double bz)) return false;

		double dx = bx - ax;
		double dy = by - ay;
		double dz = bz - az;
		double linkLength = Math.Sqrt(dx * dx + dy * dy + dz * dz);

		if ((linkLength < MinLinkLength || linkLength > maxLinkLength) && (frontCart is EntityStandardGaugeLocomotive || rearCart is EntityStandardGaugeLocomotive))
		{
			// Rendered SG bogie poses can be unavailable or stale for a frame when entity rendering/culling and the global tether renderer run out of phase.
			// Fall back before deciding the link is invalid.
			if (!TryGetTetherEndpoints(frontCart, rearCart, preferRenderedStandardGaugePose: false, out ax, out ay, out az, out bx, out by, out bz)) return false;

			dx = bx - ax;
			dy = by - ay;
			dz = bz - az;
			linkLength = Math.Sqrt(dx * dx + dy * dy + dz * dz);
		}

		if (linkLength < MinLinkLength || linkLength > maxLinkLength) return false;

		dx /= linkLength;
		dy /= linkLength;
		dz /= linkLength;

		double mx = (ax + bx) * 0.5;
		double my = (ay + by) * 0.5;
		double mz = (az + bz) * 0.5;

		double vx = cameraForward.X;
		double vy = cameraForward.Y;
		double vz = cameraForward.Z;

		// Screen-facing line billboard | Use camera rotation, not camera position, so the strip follows view yaw/pitch correctly.
		// side = normalize(cross(cameraForward, segmentDirection))
		double sx = vy * dz - vz * dy;
		double sy = vz * dx - vx * dz;
		double sz = vx * dy - vy * dx;
		double sideVectorLength = Math.Sqrt(sx * sx + sy * sy + sz * sz);

		if (sideVectorLength < 1e-6)
		{
			// Link is almost parallel to the camera forward vector. | Fall back to a cheap camera-right-ish horizontal axis.
			sx = -vz;
			sy = 0;
			sz = vx;
			sideVectorLength = Math.Sqrt(sx * sx + sz * sz);
			if (sideVectorLength < 1e-6) { sx = 1; sy = 0; sz = 0; sideVectorLength = 1; }
		}

		sx = sx / sideVectorLength * ChainHalfWidth;
		sy = sy / sideVectorLength * ChainHalfWidth;
		sz = sz / sideVectorLength * ChainHalfWidth;

		int color = GetChainLightColor(mx, my, mz);

		int firstVertexIndex = linkIndex * VerticesPerLink;
		WriteVertex(firstVertexIndex + 0, ax - sx, ay - sy, az - sz, cameraPosition, 0f, 0f, color);
		WriteVertex(firstVertexIndex + 1, ax + sx, ay + sy, az + sz, cameraPosition, 1f, 0f, color);
		WriteVertex(firstVertexIndex + 2, bx + sx, by + sy, bz + sz, cameraPosition, 1f, 1f, color);
		WriteVertex(firstVertexIndex + 3, bx - sx, by - sy, bz - sz, cameraPosition, 0f, 1f, color);

		return true;
	}

	private void WriteVertex(int vertexIndex, double x, double y, double z, Vec3d cameraPosition, float u, float v, int color)
	{
		int coordinateOffset = vertexIndex * 3;
		MeshData.xyz[coordinateOffset + 0] = (float)(x - cameraPosition.X);
		MeshData.xyz[coordinateOffset + 1] = (float)(y - cameraPosition.Y);
		MeshData.xyz[coordinateOffset + 2] = (float)(z - cameraPosition.Z);

		int textureCoordinateOffset = vertexIndex * 2;
		MeshData.Uv[textureCoordinateOffset + 0] = u;
		MeshData.Uv[textureCoordinateOffset + 1] = v;

		MeshData.Flags[vertexIndex] = 0;

		int colorOffset = vertexIndex * 4;
		MeshData.Rgba[colorOffset + 0] = ColorUtil.ColorR(color);
		MeshData.Rgba[colorOffset + 1] = ColorUtil.ColorG(color);
		MeshData.Rgba[colorOffset + 2] = ColorUtil.ColorB(color);
		MeshData.Rgba[colorOffset + 3] = ColorUtil.ColorA(color);
	}

	private int GetChainLightColor(double x, double y, double z)
	{
		Vec4f light = CoreAPI.World.BlockAccessor.GetLightRGBs((int)x, (int)y, (int)z);
		Vec3f ambient = CoreAPI.Render.AmbientColor;

		// Approximate the Standard shader's block/sun mix on the CPU so every tether in the batch can have its own local brightness without requiring one draw call per link.
		float r = Math.Max(light.R, light.A * ambient.R);
		float g = Math.Max(light.G, light.A * ambient.G);
		float b = Math.Max(light.B, light.A * ambient.B);

		return ColorUtil.ToRgba(255, ToByte(BaseMetalR * Clamp01(r)), ToByte(BaseMetalG * Clamp01(g)), ToByte(BaseMetalB * Clamp01(b)));
	}

	private static byte ToByte(float value) { return (byte)(Clamp01(value) * 255f + 0.5f); }

	private static float Clamp01(float value)
	{
		if (value <= 0f) return 0f;
		if (value >= 1f) return 1f;
		return value;
	}

	private static bool TryGetPreviousVehicleID(Entity entity, out long previousVehicleID)
	{
		switch (entity)
		{
			case EntityMinecart minecart:
				previousVehicleID = minecart.PreviousCartID;
			return true;

			case EntityStandardGaugeLocomotive standardGaugeVehicle:
				previousVehicleID = standardGaugeVehicle.PrevVehicleID;
			return true;

			default:
				previousVehicleID = 0;
			return false;
		}
	}

	private static bool TryGetRenderedStandardGaugeBogieCenters
	(
		EntityStandardGaugeLocomotive standardGaugeVehicle,
		out double frontX, out double frontY, out double frontZ,
		out double rearX, out double rearY, out double rearZ
	)
	{
		if 
		(
			standardGaugeVehicle.Properties?.Client?.Renderer is ISGLocomotiveBogiePoseProvider provider &&
			provider.TryGetBogieCenterWorldPositions(out frontX, out frontY, out frontZ, out rearX, out rearY, out rearZ)
		) { return true; }

		frontX = frontY = frontZ = rearX = rearY = rearZ = 0; return false;
	}

	private static bool TryGetTetherEndpoints(Entity frontCart, Entity rearCart, bool preferRenderedStandardGaugePose, out double ax, out double ay, out double az, out double bx, out double by, out double bz)
	{
		if (frontCart is EntityMinecart && rearCart is EntityMinecart)
		{
			ax = frontCart.Pos.X;
			ay = frontCart.Pos.InternalY + 0.2;
			az = frontCart.Pos.Z;

			bx = rearCart.Pos.X;
			by = rearCart.Pos.InternalY + 0.2;
			bz = rearCart.Pos.Z;
			return true;
		}

		if (!TryGetCouplers(frontCart, preferRenderedStandardGaugePose, out _, out _, out _, out double frontRearX, out double frontRearY, out double frontRearZ))
		{
			ax = ay = az = bx = by = bz = 0;
			return false;
		}

		if (!TryGetCouplers(rearCart, preferRenderedStandardGaugePose, out double rearFrontX, out double rearFrontY, out double rearFrontZ, out _, out _, out _))
		{
			ax = ay = az = bx = by = bz = 0;
			return false;
		}

		// We have a stable physical order, tether between adjacent ordered elements always connects rear coupler -> front coupler.
		ax = frontRearX;
		ay = frontRearY;
		az = frontRearZ;

		bx = rearFrontX;
		by = rearFrontY;
		bz = rearFrontZ;

		return true;
	}


	private static bool TryGetCouplers(Entity entity, bool preferRenderedStandardGaugePose, out double frontX, out double frontY, out double frontZ, out double rearX, out double rearY, out double rearZ)
	{
		switch (entity)
		{
			case EntityMinecart minecart:
				minecart.GetCouplerWorldPositions(out frontX, out frontY, out frontZ, out rearX, out rearY, out rearZ);
			return true;

			case EntityStandardGaugeLocomotive standardGaugeVehicle:
				if (preferRenderedStandardGaugePose && TryGetRenderedStandardGaugeBogieCenters(standardGaugeVehicle, out frontX, out frontY, out frontZ, out rearX, out rearY, out rearZ)) { return true; }
				standardGaugeVehicle.GetCouplerWorldPositions(out frontX, out frontY, out frontZ, out rearX, out rearY, out rearZ);
			return true;

			default:
				frontX = frontY = frontZ = rearX = rearY = rearZ = 0;
			return false;
		}
	}

	public override void Dispose()
	{
		if (CoreAPI != null) { CoreAPI.Event.UnregisterRenderer(this, EnumRenderStage.Opaque); }

		MeshReference?.Dispose();
		MeshReference = null;
	}
}
