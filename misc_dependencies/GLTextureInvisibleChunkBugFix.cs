using System;
using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace GLTextureInvisibleChunkBugFix;

public sealed class GLTextureInvisibleChunkBugFixSystem : ModSystem
{
	private static ICoreClientAPI? capi;
	private Harmony? harmony;

	public override void StartClientSide(ICoreClientAPI api)
	{
		capi = api;
		harmony = new Harmony("gltextureinvisiblechunkbugfix");
		harmony.PatchAll(typeof(GLTextureInvisibleChunkBugFixSystem).Assembly);
	}

	public override void Dispose()
	{
		harmony?.UnpatchAll("gltextureinvisiblechunkbugfix");
		harmony = null;
		capi = null;
		base.Dispose();
	}

	[HarmonyPatch(typeof(JsonTesselator), nameof(JsonTesselator.AddJsonModelDataToMesh), new Type[]{typeof(MeshData), typeof(int), typeof(TCTCache), typeof(IMeshPoolSupplier), typeof(float[]), typeof(IJsonTesselatorHooks), typeof(int)})]
	private static class InvalidTerrainTexturePatch
	{
		private static readonly HashSet<string> Warned = new();
		private static readonly object WarnLock = new();

		private static void Prefix(ref MeshData sourceMesh, TCTCache vars, IJsonTesselatorHooks hooks)
		{
			ICoreClientAPI? api = capi;
			if (api == null || sourceMesh == null || hooks != null || vars.IsDecorOnJson) return;

			int[] textureIds = sourceMesh.TextureIds;
			if (textureIds == null || textureIds.Length == 0) return;

			TextureAtlasPosition unknown = api.BlockTextureAtlas.UnknownTexturePosition;
			if (unknown == null) return;

			MeshData? repaired = null;
			bool repairedAny = false;

			for (int slot = 0; slot < textureIds.Length; slot++)
			{
				int badTextureId = textureIds[slot];
				if (IsCurrentBlockAtlasTexture(api, badTextureId)) { continue; }

				repaired ??= sourceMesh.Clone();
				if (!TryRemapTextureSlot(repaired, slot, unknown)) { continue; }

				repaired.TextureIds[slot] = unknown.atlasTextureId;

				repairedAny = true;
				WarnOnce(api, vars, badTextureId);
			}

			if (repairedAny) { sourceMesh = repaired!; }
		}

		private static bool IsCurrentBlockAtlasTexture(ICoreClientAPI api, int textureId)
		{
			List<LoadedTexture> atlases = api.BlockTextureAtlas.AtlasTextures;
			for (int i = 0; i < atlases.Count; i++) { if (atlases[i].TextureId == textureId) return true; }

			return false;
		}

		private static bool TryRemapTextureSlot(MeshData mesh, int slot, TextureAtlasPosition unknown)
		{
			byte[] textureIndices = mesh.TextureIndices;
			float[] uv = mesh.Uv;
			int verticesPerFace = mesh.VerticesPerFace;

			if (textureIndices == null || uv == null || verticesPerFace <= 0) return false;

			int faceCount = Math.Min(mesh.XyzFacesCount, mesh.TextureIndicesCount);
			bool used = false;

			// Validate every face first. Never partially repair a shared texture slot.
			for (int face = 0; face < faceCount; face++)
			{
				if (textureIndices[face] != slot) continue;

				used = true;
				int uvStart = face * verticesPerFace * 2;
				if (uvStart < 0 || uvStart + verticesPerFace * 2 > uv.Length) return false;
			}

			if (!used) return false;

			for (int face = 0; face < faceCount; face++)
			{
				if (textureIndices[face] != slot) continue;
				RemapFaceUv(mesh, face, unknown);
			}

			return true;
		}

		private static void RemapFaceUv(MeshData mesh, int face, TextureAtlasPosition target)
		{
			int verticesPerFace = mesh.VerticesPerFace;
			int start = face * verticesPerFace * 2;

			float minU = float.MaxValue;
			float maxU = float.MinValue;
			float minV = float.MaxValue;
			float maxV = float.MinValue;

			for (int vertex = 0; vertex < verticesPerFace; vertex++)
			{
				int index = start + vertex * 2;
				float u = mesh.Uv[index];
				float v = mesh.Uv[index + 1];

				minU = Math.Min(minU, u);
				maxU = Math.Max(maxU, u);
				minV = Math.Min(minV, v);
				maxV = Math.Max(maxV, v);
			}

			float rangeU = maxU - minU;
			float rangeV = maxV - minV;

			for (int vertex = 0; vertex < verticesPerFace; vertex++)
			{
				int index = start + vertex * 2;
				float normU = rangeU > 0.0001f ? (mesh.Uv[index] - minU) / rangeU : 0f;
				float normV = rangeV > 0.0001f ? (mesh.Uv[index + 1] - minV) / rangeV : 0f;

				normU = Math.Clamp(normU, 0f, 1f);
				normV = Math.Clamp(normV, 0f, 1f);

				mesh.Uv[index] = target.x1 + normU * (target.x2 - target.x1);
				mesh.Uv[index + 1] = target.y1 + normV * (target.y2 - target.y1);
			}
		}

		private static void WarnOnce(ICoreClientAPI api, TCTCache vars, int badTextureId)
		{
			string blockCode = vars.block?.Code?.ToString() ?? "<unknown>";
			string key = blockCode + "|" + badTextureId;

			lock (WarnLock) { if (!Warned.Add(key)) return; }
			api.Logger.Error("[GLTextureInvisibleChunkBugFix] Stale texture ID found: {0} on block '{1}' at ({2}, {3}, {4}). Replaced with the missing texture to prevent an infinite chunk tessellation retry loop.", badTextureId, blockCode, vars.posX, vars.posY, vars.posZ);
		}
	}
}
