using System;
using System.IO;

namespace YangTransport;

/// One save-time snapshot for the authoritative rail graph and its clearance certificates.
/// Current-format snapshot, not a transactional journal or a backwards-compatible format.
internal static class RailStateSerializer
{
	internal const string SaveKey = "yangtransport_railstate";
	private const int MaxSectionBytes = 1024 * 1024 * 1024;

	internal static byte[] Serialize(RailGraphLive graph, RailClearanceLayer clearance)
	{
		byte[] graphPayload = graph.Serialize(includePolylines: true, includeOccupancy: false);
		byte[] clearancePayload = clearance.SerializePersistence();
		if (graphPayload.Length > MaxSectionBytes || clearancePayload.Length > MaxSectionBytes) throw new InvalidDataException("Rail-state section exceeds max size!");

		int capacity = checked(12 + graphPayload.Length + clearancePayload.Length);
		using MemoryStream memoryStream = new(capacity);
		using BinaryWriter binaryWriter = new(memoryStream);
		binaryWriter.Write(TrackSpecsDictionary.GetSpecsHash());
		binaryWriter.Write(graphPayload.Length);
		binaryWriter.Write(graphPayload);
		binaryWriter.Write(clearancePayload.Length);
		binaryWriter.Write(clearancePayload);
		binaryWriter.Flush();
		return memoryStream.ToArray();
	}

	internal static bool TryDeserialize
	(
		byte[] payload,
		RailGraphLive graph,
		RailClearanceLayer clearance,
		out uint savedSpecificationsHash,
		out bool clearanceLoaded,
		out string error
	)
	{
		savedSpecificationsHash = 0;
		clearanceLoaded = false;
		error = string.Empty;

		try
		{
			using MemoryStream memoryStream = new(payload, writable: false);
			using BinaryReader binaryReader = new(memoryStream);

			savedSpecificationsHash = binaryReader.ReadUInt32();

			if (!TryReadSection(binaryReader, memoryStream, out byte[] graphPayload)) { error = "invalid graph section"; return false; }
			
			if (!graph.TryLoad(graphPayload)) { error = "invalid graph payload"; return false; }

			if (TryReadSection(binaryReader, memoryStream, out byte[] clearancePayload) && memoryStream.Position == memoryStream.Length)
			{
				clearanceLoaded = clearance.TryLoadPersistence(clearancePayload);
			}

			if (!clearanceLoaded) { clearance.ResetEmptyForLoadedGraph(); }

			return true;
		}
		catch (Exception exception) { error = exception.Message; return false; }
	}

	private static bool TryReadSection(BinaryReader binaryReader, MemoryStream memoryStream, out byte[] payload)
	{
		payload = Array.Empty<byte>();
		if (memoryStream.Length - memoryStream.Position < sizeof(int)) return false;

		int length = binaryReader.ReadInt32();
		if (length < 0 || length > MaxSectionBytes || length > memoryStream.Length - memoryStream.Position) return false;

		payload = binaryReader.ReadBytes(length);
		return payload.Length == length;
	}
}
