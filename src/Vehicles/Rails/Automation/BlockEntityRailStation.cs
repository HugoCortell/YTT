using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Cairo;
using System;
using System.Collections.Generic;
using System.IO;

namespace YangTransport;

public sealed class BlockEntityRailStation : BlockEntity
{
	private const int PacketOpenDialog = 1001;
	private const int PacketSaveText = 1002;

	private string TextContents = "";
	private int Colour = ColorUtil.WhiteArgb;
	private float FontSize;

	private RailStationTextRenderer? TextRenderer;
	private GuiDialogBlockEntityTextInput? EditDialogue;

	public string StationName => TextContents;

	public override void Initialize(ICoreAPI coreAPI)
	{
		base.Initialize(coreAPI);

		RailStationTextConfiguration textConfiguration = GetTextConfiguration();
		if (FontSize <= 0) FontSize = textConfiguration.FontSize;

		if (coreAPI is ICoreClientAPI clientAPI)
		{
			TextRenderer = new RailStationTextRenderer(Pos, clientAPI, textConfiguration);
			TextRenderer.FontSize = FontSize;
			TextRenderer.SetNewText(TextContents, Colour);
		}

		if (coreAPI.Side == EnumAppSide.Server) { ServerRegisterStation(redraw: string.IsNullOrWhiteSpace(TextContents)); }
	}

	public override void OnBlockPlaced(ItemStack byItemStack = null)
	{
		base.OnBlockPlaced(byItemStack);

		if (Api?.Side != EnumAppSide.Server) return;

		if (FontSize <= 0) { FontSize = GetTextConfiguration().FontSize; }
		ServerRegisterStation(redraw: true);
	}

	public bool OnRightClick(IPlayer byPlayer)
	{
		// Same interaction style as signs. Except no writing tool/item is needed for obtuse UX reasons and absolutely not because I forgot.
		if (byPlayer?.Entity?.Controls?.ShiftKey != true) return false; // Remove this guard for plain right-click editing.

		if (Api is ICoreServerAPI serverAPI && byPlayer is IServerPlayer serverPlayer) { serverAPI.Network.SendBlockEntityPacket(serverPlayer, Pos, PacketOpenDialog); }

		return true;
	}

	public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
	{
		base.FromTreeAttributes(tree, worldForResolving);

		TextContents = tree.GetString("text", "");
		Colour = tree.GetInt("color", Colour);

		RailStationTextConfiguration textConfiguration = GetTextConfiguration();
		FontSize = tree.GetFloat("fontSize", textConfiguration.FontSize);
		if (FontSize <= 0) { FontSize = textConfiguration.FontSize; }

		if (TextRenderer != null)
		{
			TextRenderer.FontSize = FontSize;
			TextRenderer.SetNewText(TextContents, Colour);
		}
	}

	public override void ToTreeAttributes(ITreeAttribute tree)
	{
		base.ToTreeAttributes(tree);

		tree.SetString("text", TextContents);
		tree.SetInt("color", Colour);
		tree.SetFloat("fontSize", FontSize);
	}

	public override void OnReceivedClientPacket(IPlayer player, int packetID, byte[] data)
	{
		if (packetID != PacketSaveText) return;

		if (!Api.World.Claims.TryAccess(player, Pos, EnumBlockAccessFlags.BuildOrBreak)) { return; }

		EditSignPacket packet = SerializerUtil.Deserialize<EditSignPacket>(data);

		TextContents = (packet.Text ?? "").Trim();
		if (TextContents.Length > 512) TextContents = TextContents[..512];

		FontSize = packet.FontSize;
		if (FontSize <= 0) FontSize = GetTextConfiguration().FontSize;

		ServerRegisterStation(redraw: true);
	}

	public override void OnReceivedServerPacket(int packetID, byte[] data)
	{
		if (packetID != PacketOpenDialog) return;
		if (Api is not ICoreClientAPI clientAPI) return;

		if (EditDialogue != null && EditDialogue.IsOpened()) return;

		RailStationTextConfiguration textConfiguration = GetTextConfiguration(FontSize);

		EditDialogue = new GuiDialogBlockEntityTextInput(Lang.Get("yangtransport:automation-ui-edit-station-name"), Pos, TextContents, clientAPI, textConfiguration);
		EditDialogue.OnTextChanged = DidChangeTextClientSide;
		EditDialogue.OnCloseCancel = () =>
		{
			if (TextRenderer != null)
			{
				TextRenderer.FontSize = FontSize;
				TextRenderer.SetNewText(TextContents, Colour);
			}
		};

		EditDialogue.OnClosed += () =>
		{
			if (TextRenderer != null) { TextRenderer.ShowTextBounds = false; }
			EditDialogue = null;
		};

		if (EditDialogue.TryOpen() && TextRenderer != null) { TextRenderer.ShowTextBounds = true; }
	}

	private void DidChangeTextClientSide(string newText)
	{
		if (TextRenderer == null || EditDialogue == null) return;

		TextRenderer.FontSize = EditDialogue.FontSize;
		TextRenderer.SetNewText(newText, Colour);
	}

	private void ServerRegisterStation(bool redraw)
	{
		if (Api?.Side != EnumAppSide.Server) return;

		string registeredName = Api.ModLoader.GetModSystem<RailStationRegistrySystem>()?.RegisterOrUpdate(Pos, Block, TextContents) ?? TextContents.Trim();
		if (string.IsNullOrWhiteSpace(registeredName)) { registeredName = Api.ModLoader.GetModSystem<RailStationNameSystem>()?.GenerateName()?.Trim() ?? "Station"; }

		bool changed = !string.Equals(TextContents, registeredName, StringComparison.Ordinal);
		TextContents = registeredName;

		if (!redraw && !changed) return;

		MarkDirty(redrawOnClient: true);
		Api.World.BlockAccessor.GetChunkAtBlockPos(Pos)?.MarkModified();
	}

	private RailStationTextConfiguration GetTextConfiguration(float? fontSizeOverride = null)
	{
		RailStationTextConfiguration textConfiguration = new();

		JsonObject? attributes = Block?.Attributes;
		if (attributes != null && attributes["StationText"].Exists)
		{
			textConfiguration = attributes["StationText"].AsObject(new RailStationTextConfiguration()) ?? new RailStationTextConfiguration();
		}
		if (fontSizeOverride != null) { textConfiguration.FontSize = fontSizeOverride.Value; }

		textConfiguration.ResolveDynamicCanvasSize();
		return textConfiguration;
	}

	public override void OnBlockRemoved()
	{
		if (Api?.Side == EnumAppSide.Server) { Api.ModLoader.GetModSystem<RailStationRegistrySystem>()?.Unregister(Pos); }

		DisposeRenderer();
		base.OnBlockRemoved();
	}

	public override void OnBlockUnloaded()
	{
		DisposeRenderer();
		base.OnBlockUnloaded();
	}

	private void DisposeRenderer()
	{
		TextRenderer?.Dispose();
		TextRenderer = null;
	}
}

#region Text Renderer
public sealed class RailStationTextConfiguration : TextAreaConfig
{
	// Final block-local text center, after the block variant/shape has been resolved. | These are in block coordinates, not voxels.
	public float CenterX = 0.5f;
	public float CenterY = 0.135f;
	public float CenterZ = 0.5f;

	// Use if the text points 90/180 degrees wrong for a particular model.
	public float YawOffsetDegrees = 0f;
	public float ForwardOffset = 0f;
	public float RightOffset = 0f;

	// Used only when MaxWidth/MaxHeight are omitted or set to 0.
	public float TexturePixelsPerVoxel = 16f;

	public RailStationTextConfiguration()
	{
		MaxWidth = 0;
		MaxHeight = 0;
	}

	public void ResolveDynamicCanvasSize()
	{
		float pixelsPerVoxel = GameMath.Clamp(TexturePixelsPerVoxel, 4f, 64f);

		if (MaxWidth <= 0)		{ MaxWidth = VoxelSizeToCanvasPixels(textVoxelWidth, pixelsPerVoxel); }
		if (MaxHeight <= 0)		{ MaxHeight = VoxelSizeToCanvasPixels(textVoxelHeight, pixelsPerVoxel); }
	}

	private static int VoxelSizeToCanvasPixels(float voxelSize, float pixelsPerVoxel)
	{
		return Math.Max(1, (int)Math.Ceiling(Math.Max(0.0625f, voxelSize) * pixelsPerVoxel));
	}
}

public sealed class RailStationTextRenderer : IRenderer, IDisposable
{
	private readonly ICoreClientAPI ClientAPI;
	private readonly BlockPos Position;
	private readonly RailStationTextConfiguration Configuration;
	private readonly MeshRef QuadModelReference;
	private readonly Matrixf ModelMatrix = new();

	private const float TextBoundsLift = 0.003f;
	private static readonly Vec4f TextBoundsColor = new(1f, 1f, 1f, 0.85f);
	private readonly MeshRef TextBoundsModelReference;
	private readonly Matrixf BoundsMatrix = new();

	private LoadedTexture? LoadedTexture;
	private string Text = "";
	private readonly CairoFont Font;

	private readonly int TextWidth;
	private readonly int TextHeight;
	private readonly float QuadWidth;
	private readonly float QuadHeight;
	private readonly float CenterX;
	private readonly float CenterZ;
	private readonly float YawRadians;
	private readonly float RollRadians;

	public float FontSize { get; set; }
	public bool ShowTextBounds { get; set; }

	public double RenderOrder => 1.103;// If all else fails, do BlockEntitySignRenderer.AfterSignRendererOrder + 0.001
	public int RenderRange => 24;

	public RailStationTextRenderer(BlockPos position, ICoreClientAPI clientAPI, RailStationTextConfiguration configuration)
	{
		this.ClientAPI = clientAPI;
		this.Position = position;
		this.Configuration = configuration ?? new RailStationTextConfiguration();
		this.Configuration.ResolveDynamicCanvasSize();

		FontSize = this.Configuration.FontSize;
		TextWidth = Math.Max(1, this.Configuration.MaxWidth);
		TextHeight = Math.Max(1, this.Configuration.MaxHeight);
		QuadWidth = this.Configuration.textVoxelWidth / 16f;
		QuadHeight = this.Configuration.textVoxelHeight / 16f;

		Font = new CairoFont(FontSize, this.Configuration.FontName, ColorUtil.ToRGBADoubles(ColorUtil.BlackArgb));
		if (this.Configuration.BoldFont) { Font.WithWeight((FontWeight)1); }
		Font.LineHeightMultiplier = 0.9;

		Block block = clientAPI.World.BlockAccessor.GetBlock(position);
		string rotationCode = "ne";
		if (block?.Variant != null && block.Variant.TryGetValue("rot", out string foundRotationCode)) { rotationCode = foundRotationCode; }

		YawRadians = ConvertRotationToYawRadians(rotationCode) + this.Configuration.YawOffsetDegrees * ((float)Math.PI / 180f);

		CenterX = this.Configuration.CenterX;
		CenterZ = this.Configuration.CenterZ;
		if (this.Configuration.ForwardOffset != 0 || this.Configuration.RightOffset != 0)
		{
			float sine = (float)Math.Sin(YawRadians);
			float cosine = (float)Math.Cos(YawRadians);

			CenterX += this.Configuration.RightOffset * cosine - this.Configuration.ForwardOffset * sine;
			CenterZ += -this.Configuration.ForwardOffset * cosine - this.Configuration.RightOffset * sine;
		}

		MeshData modelData = QuadMeshUtil.GetQuad();
		modelData.Uv = new float[8] { 1f, 0f, 0f, 0f, 0f, 1f, 1f, 1f }; // UV flip
		modelData.Rgba = new byte[16];
		modelData.Rgba.Fill(byte.MaxValue);

		QuadModelReference = clientAPI.Render.UploadMesh(modelData);

		// Bounds
		MeshData boundsModelData = LineMeshUtil.GetRectangle(ColorUtil.WhiteArgb);
		boundsModelData.Flags = new int[boundsModelData.VerticesCount];
		for (int flagIndex = 0; flagIndex < boundsModelData.Flags.Length; flagIndex++) { boundsModelData.Flags[flagIndex] = 1 << 8; }
		TextBoundsModelReference = clientAPI.Render.UploadMesh(boundsModelData);

		clientAPI.Event.RegisterRenderer(this, EnumRenderStage.Opaque, "yangtransport-stationtext");
	}

	public void SetNewText(string newText, int color)
	{
		Text = newText ?? "";

		Font.Color = ColorUtil.ToRGBADoubles(color);

		LoadedTexture?.Dispose();
		LoadedTexture = null;
	}

	public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
	{
		bool hasText = !string.IsNullOrEmpty(Text);
		if (!hasText && !ShowTextBounds) return;

		if (hasText && LoadedTexture == null) { LoadedTexture = RenderText(); }
		if (!ClientAPI.Render.DefaultFrustumCuller.SphereInFrustum(Position.X + 0.5, Position.InternalY + 0.5, Position.Z + 0.5, 2.0)) { return; }

		IRenderAPI renderAPI = ClientAPI.Render;
		Vec3d cameraPos = ClientAPI.World.Player.Entity.CameraPos;

		renderAPI.GlToggleBlend(true, EnumBlendMode.PremultipliedAlpha);
		renderAPI.GlDisableCullFace();

		IStandardShaderProgram shaderProgram = renderAPI.PreparedStandardShader(Position.X, Position.InternalY, Position.Z);
		shaderProgram.ViewMatrix = renderAPI.CameraMatrixOriginf;
		shaderProgram.ProjectionMatrix = renderAPI.CurrentProjectionMatrix;
		shaderProgram.NormalShaded = 0;
		shaderProgram.ExtraGodray = 0f;
		shaderProgram.SsaoAttn = 0f;
		shaderProgram.AlphaTest = 0.05f;
		shaderProgram.OverlayOpacity = 0f;
		shaderProgram.RgbaLightIn = ClientAPI.World.BlockAccessor.GetLightRGBs(Position);

		float centerY = Configuration.CenterY;

		if (hasText && LoadedTexture != null)
		{
			shaderProgram.Tex2D = LoadedTexture.TextureId;

			shaderProgram.ModelMatrix = ModelMatrix
				.Identity()
				.Translate(Position.X - cameraPos.X + CenterX, Position.InternalY - cameraPos.Y + centerY, Position.Z - cameraPos.Z + CenterZ)
				.RotateY(YawRadians)
				.RotateX(-(float)Math.PI / 2f)
				.RotateZ(RollRadians)
				.Scale(0.5f * QuadWidth, 0.5f * QuadHeight, 0.5f)
			.Values;

			renderAPI.RenderMesh(QuadModelReference);
		}

		shaderProgram.Stop();
		if (ShowTextBounds) { RenderTextBounds(renderAPI, cameraPos, centerY); }
		renderAPI.GlToggleBlend(true);
	}

	private LoadedTexture RenderText()
	{
		Font.UnscaledFontsize = FontSize / RuntimeEnv.GUIScale;

		double verticalPadding = 0;
		if (Configuration.VerticalAlign == EnumVerticalAlign.Middle)
		{
			verticalPadding = TextHeight - ClientAPI.Gui.Text.GetMultilineTextHeight(Font, Text, TextWidth);
			verticalPadding = Math.Max(0, verticalPadding);
		}

		TextBackground textBackground = new() { VerPadding = (int)verticalPadding / 2 };

		return ClientAPI.Gui.TextTexture.GenTextTexture(Text, Font, TextWidth, TextHeight, textBackground, EnumTextOrientation.Center);
	}

	private void RenderTextBounds(IRenderAPI renderAPI, Vec3d cameraPos, float centerY)
	{
		IShaderProgram shaderProgram = ClientAPI.Shader.GetProgram((int)EnumShaderProgram.Wireframe);
		shaderProgram.Use();

		renderAPI.LineWidth = 2f;
		renderAPI.GLEnableDepthTest();
		renderAPI.GLDepthMask(false);
		renderAPI.GlToggleBlend(true);

		shaderProgram.Uniform("origin", 0f, 0f, 0f);
		shaderProgram.UniformMatrix("projectionMatrix", renderAPI.CurrentProjectionMatrix);
		shaderProgram.UniformMatrix("modelViewMatrix", BoundsMatrix
			.Identity()
			.Set(renderAPI.CameraMatrixOrigin)
			.Translate(Position.X - cameraPos.X + CenterX, Position.InternalY - cameraPos.Y + centerY + TextBoundsLift, Position.Z - cameraPos.Z + CenterZ)
			.RotateY(YawRadians)
			.RotateX(-(float)Math.PI / 2f)
			.RotateZ(RollRadians)
			.Scale(0.5f * QuadWidth, 0.5f * QuadHeight, 0.5f)
			.Values
		);
		shaderProgram.Uniform("colorIn", TextBoundsColor);

		renderAPI.RenderMesh(TextBoundsModelReference);

		shaderProgram.Stop();

		renderAPI.GLDepthMask(true);
		renderAPI.LineWidth = 1.6f;
	}

	private static float ConvertRotationToYawRadians(string rotationCode)
	{
		// The horizontal quad's unrotated "top of text" points north.
		return rotationCode switch
		{
			"es" => -(float)Math.PI / 2f,
			"sw" => (float)Math.PI,
			"wn" => (float)Math.PI / 2f,
			_ => 0f
		};
	}

	public void Dispose()
	{
		ClientAPI.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
		LoadedTexture?.Dispose();
		QuadModelReference.Dispose();
		TextBoundsModelReference.Dispose();
	}
}
#endregion

#region Name Gen
public sealed class RailStationNameConfiguration
{
	public List<string> Prefix = new();
	public List<string> Start = new();
	public List<string> End = new();
	public List<string> Suffix = new();
	public List<string> Special = new();

	public void Normalize()
	{
		Prefix ??= new List<string>();
		Start ??= new List<string>();
		End ??= new List<string>();
		Suffix ??= new List<string>();
		Special ??= new List<string>();

		RemoveBlankEntries(Prefix);
		RemoveBlankEntries(Start);
		RemoveBlankEntries(End);
		RemoveBlankEntries(Suffix);
		RemoveBlankEntries(Special);
	}

	private static void RemoveBlankEntries(List<string> list)
	{
		list.RemoveAll(value => string.IsNullOrWhiteSpace(value));
	}
}

public sealed class RailStationNameSystem : ModSystem
{
	private static readonly string ConfigurationFilename = System.IO.Path.Combine("yangtransport", "station_names.json");
	private const double SpecialChance = 0.10;
	private const double PrefixChance = 0.30;
	private const double SuffixChance = 0.45;

	private static readonly AssetLocation DefaultConfigurationAsset = new("yangtransport:config/stationnames.json");

	private readonly Random FallbackRandom = new();
	private ICoreServerAPI? ServerAPI;
	private RailStationNameConfiguration Configuration = new();

	public override bool ShouldLoad(EnumAppSide forSide) { return forSide == EnumAppSide.Server; }

	public override void AssetsLoaded(ICoreAPI coreAPI)
	{
		if (coreAPI is not ICoreServerAPI serverAPI) return;

		ServerAPI = serverAPI;

		EnsureEditableConfigurationExists(serverAPI);
		Configuration = LoadConfiguration(serverAPI);
		Configuration.Normalize();
	}

	public string GenerateName()
	{
		Random random = ServerAPI?.World?.Rand ?? FallbackRandom;

		if (HasEntries(Configuration.Special) && random.NextDouble() < SpecialChance) { return Pick(Configuration.Special, random); }

		// If generated names are impossible, fall back to specials if available.
		if (!HasEntries(Configuration.Start) || !HasEntries(Configuration.End)) { return HasEntries(Configuration.Special) ? Pick(Configuration.Special, random) : ""; }

		string prefix = HasEntries(Configuration.Prefix) && random.NextDouble() < PrefixChance ? Pick(Configuration.Prefix, random) : "";
		string stationNameCore = Pick(Configuration.Start, random) + Pick(Configuration.End, random);
		string suffix = HasEntries(Configuration.Suffix) && random.NextDouble() < SuffixChance ? Pick(Configuration.Suffix, random) : "";

		return JoinStationName(prefix, stationNameCore, suffix);
	}

	private void EnsureEditableConfigurationExists(ICoreServerAPI serverAPI)
	{
		string configurationDirectory = serverAPI.GetOrCreateDataPath("ModConfig");
		string configurationPath = System.IO.Path.Combine(configurationDirectory, ConfigurationFilename);
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(configurationPath)!);

		if (File.Exists(configurationPath)) return;

		IAsset? defaultAsset = serverAPI.Assets.TryGet(DefaultConfigurationAsset);
		if (defaultAsset == null)
		{
			serverAPI.Logger.Warning("[YangTransport] Default station name config asset '{0}' missing! Creating an empty config instead.", DefaultConfigurationAsset);
			serverAPI.StoreModConfig(new RailStationNameConfiguration(), ConfigurationFilename);
			return;
		}

		try
		{
			File.WriteAllText(configurationPath, defaultAsset.ToText());
			serverAPI.Logger.Notification("[YangTransport] Created a station name config file at {0}", configurationPath);
		}
		catch (Exception exception)
		{
			serverAPI.Logger.Error("[YangTransport] Failed to copy default station name config to ModConfig.");
			serverAPI.Logger.Error(exception);
		}
	}

	private RailStationNameConfiguration LoadConfiguration(ICoreServerAPI serverAPI)
	{
		try
		{
			RailStationNameConfiguration? loadedConfiguration = serverAPI.LoadModConfig<RailStationNameConfiguration>(ConfigurationFilename);
			if (loadedConfiguration != null) return loadedConfiguration;
		}
		catch (Exception exception)
		{
			serverAPI.Logger.Error("[YangTransport] Failed to load station name config '{0}'. Falling back to default asset.", ConfigurationFilename);
			serverAPI.Logger.Error(exception);
		}

		return LoadDefaultConfiguration(serverAPI);
	}

	private RailStationNameConfiguration LoadDefaultConfiguration(ICoreServerAPI serverAPI)
	{
		try
		{
			IAsset? defaultAsset = serverAPI.Assets.TryGet(DefaultConfigurationAsset);
			RailStationNameConfiguration? loadedConfiguration = defaultAsset?.ToObject<RailStationNameConfiguration>();
			return loadedConfiguration ?? new RailStationNameConfiguration();
		}
		catch (Exception exception)
		{
			serverAPI.Logger.Error("[YangTransport] Failed to load default station name config asset '{0}'.", DefaultConfigurationAsset);
			serverAPI.Logger.Error(exception);
			return new RailStationNameConfiguration();
		}
	}

	private static bool HasEntries(List<string> list) { return list.Count > 0; }

	private static string Pick(List<string> list, Random random) { return list[random.Next(list.Count)]; }

	private static string JoinStationName(string prefix, string stationNameCore, string suffix)
	{
		List<string> stationNameParts = new(3);

		if (!string.IsNullOrWhiteSpace(prefix)) stationNameParts.Add(prefix.Trim());
		if (!string.IsNullOrWhiteSpace(stationNameCore)) stationNameParts.Add(stationNameCore.Trim());
		if (!string.IsNullOrWhiteSpace(suffix)) stationNameParts.Add(suffix.Trim());

		return string.Join(" ", stationNameParts);
	}
}
#endregion
