using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace YangTransport;

internal readonly record struct RailOwnerKey(int X, int Y, int Z);

/// Tiny crash-recovery intent log for physical rail owners modified since the last staged railgraph snapshot. It deliberately stores no graph state.
internal sealed class RailGraphRecoveryJournal : IDisposable
{
	// Fixed file-type signature. Not a format version.
	private const int JournalFileSignature = 0x4A525459; // YTRJ
	private const int RecordBytes = sizeof(int) * 3;

	private readonly HashSet<RailOwnerKey> PendingOwnerKeys = new();

	private ICoreServerAPI? ServerAPI;
	private string? FilePath;
	private string SavegameIdentifier = "";
	private FileStream? AppendStream;
	private BinaryWriter? AppendWriter;
	private bool WriteFailureLogged;

	internal int PendingCount => PendingOwnerKeys.Count;
	internal bool HasPending => PendingOwnerKeys.Count != 0;
	internal IReadOnlyCollection<RailOwnerKey> PendingOwners => PendingOwnerKeys;

	internal void OpenForWorld(ICoreServerAPI coreServerAPI)
	{
		DisposeWriter();
		PendingOwnerKeys.Clear();

		ServerAPI = coreServerAPI ?? throw new ArgumentNullException(nameof(coreServerAPI));
		SavegameIdentifier = coreServerAPI.World.SavegameIdentifier ?? "";
		FilePath = coreServerAPI.WorldManager.CurrentWorldFilepath + ".yangtransport-railjournal";
		WriteFailureLogged = false;

		if (!File.Exists(FilePath)) return;

		try
		{
			using FileStream stream = new(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
			using BinaryReader reader = new(stream);

			if (stream.Length < sizeof(int) || reader.ReadInt32() != JournalFileSignature) { ResetInvalidJournal("invalid file signature"); return; }

			string journalWorld = reader.ReadString();
			if (!string.Equals(journalWorld, SavegameIdentifier, StringComparison.Ordinal)) { ResetInvalidJournal("savegame identifier mismatch"); return; }

			long completeLength = stream.Position;
			while (stream.Length - stream.Position >= RecordBytes)
			{
				PendingOwnerKeys.Add(new RailOwnerKey(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()));
				completeLength = stream.Position;
			}

			if (completeLength != stream.Length)
			{
				stream.SetLength(completeLength);
				coreServerAPI.Logger.Warning("[yangtransport] Truncated incomplete tail from rail recovery journal '{0}'.", Path.GetFileName(FilePath));
			}

			if (PendingOwnerKeys.Count > 0)
			{
				coreServerAPI.Logger.Warning("[yangtransport] Found {0} rail owner(s) requiring crash recovery before gameplay starts.", PendingOwnerKeys.Count);
			}
		}
		catch (Exception exception)
		{
			coreServerAPI.Logger.Error("[yangtransport] Could not read the rail recovery journal; resetting it.");
			coreServerAPI.Logger.Error(exception);
			ResetInvalidJournal("read failure");
		}
	}

	internal void MarkSuspect(BlockPos position)
	{
		if (position == null) return;

		RailOwnerKey key = new(position.X, position.Y, position.Z);
		if (!PendingOwnerKeys.Add(key)) return;

		long recordStart = -1;
		try
		{
			EnsureWriter();
			recordStart = AppendStream!.Position;
			AppendWriter!.Write(key.X);
			AppendWriter.Write(key.Y);
			AppendWriter.Write(key.Z);
			AppendWriter.Flush();
			AppendStream.Flush(flushToDisk: false);
		}
		catch (Exception exception)
		{
			// Never leave a partial fixed-size record in front of later appends.
			if (recordStart >= 0 && AppendStream != null) { try { AppendStream.SetLength(recordStart); } catch { } }

			// The record never became durable enough to trust. Remove it from the in-memory dedupe set so a later mutation can retry the append.
			PendingOwnerKeys.Remove(key);

			if (!WriteFailureLogged)
			{
				WriteFailureLogged = true;
				ServerAPI?.Logger.Error
				(
					"[yangtransport] Failed to append the rail recovery journal. Railgraph crash reconciliation is unavailable until later successful journal writes.");
					ServerAPI?.Logger.Error(exception
				);
			}

			DisposeWriter();
		}
	}

	/// The corresponding graph snapshot has been staged into SaveGame.ModData.
	/// There's an async race condition in vanilla code if I recall right. Entries before this point no longer need recovery.
	internal void Checkpoint()
	{
		DisposeWriter();
		PendingOwnerKeys.Clear();

		if (FilePath == null || !File.Exists(FilePath)) return;

		try { File.Delete(FilePath); }
		catch (Exception exception)
		{
			// Leaving an old journal is safe, recovery is idempotent and will merely re-read the already-matching physical owners on the next boot.
			ServerAPI?.Logger.Warning
			(
				"[yangtransport] Could not delete checkpointed rail recovery journal '{0}'. It may cause harmless extra reconciliation on the next startup: {1}",
				Path.GetFileName(FilePath), exception.Message
			);
		}
	}

	public void Dispose()
	{
		DisposeWriter();
	}

	private void EnsureWriter()
	{
		if (AppendWriter != null) return;
		if (FilePath == null) throw new InvalidOperationException("Rail recovery journal has not been opened for a world.");

		string? directory = Path.GetDirectoryName(FilePath);
		if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
		bool newFile = !File.Exists(FilePath) || new FileInfo(FilePath).Length == 0;

		AppendStream = new FileStream
		(
			FilePath,
			FileMode.OpenOrCreate,
			FileAccess.ReadWrite,
			FileShare.Read,
			bufferSize: 4096,
			FileOptions.SequentialScan
		);

		AppendStream.Position = AppendStream.Length;
		AppendWriter = new BinaryWriter(AppendStream);

		if (!newFile) return;

		AppendWriter.Write(JournalFileSignature);
		AppendWriter.Write(SavegameIdentifier);
		AppendWriter.Flush();
		AppendStream.Flush(flushToDisk: false);
	}

	private void ResetInvalidJournal(string reason)
	{
		DisposeWriter();
		PendingOwnerKeys.Clear();

		if (FilePath == null) return;

		try
		{
			if (File.Exists(FilePath)) File.Delete(FilePath);
			ServerAPI?.Logger.Warning("[yangtransport] Reset rail recovery journal '{0}' ({1}).", Path.GetFileName(FilePath), reason);
		}
		catch (Exception exception)
		{
			ServerAPI?.Logger.Error("[yangtransport] Could not reset invalid rail recovery journal '{0}'.", Path.GetFileName(FilePath));
			ServerAPI?.Logger.Error(exception);
		}
	}

	private void DisposeWriter()
	{
		try { AppendWriter?.Dispose(); }
		finally
		{
			AppendWriter = null;
			AppendStream = null;
		}
	}
}
