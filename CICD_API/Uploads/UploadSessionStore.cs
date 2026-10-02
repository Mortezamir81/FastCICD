using System.Collections.Concurrent;
using System.Text.Json;
using CICD_API.Models;

namespace CICD_API.Uploads;

public sealed class UploadSession
{
	public UploadSessionMetadata Metadata { get; }
	public string PartFilePath { get; }
	public object SyncRoot { get; } = new();

	public UploadSession(UploadSessionMetadata metadata, string partFilePath)
	{
		Metadata = metadata;
		PartFilePath = partFilePath;
	}
}

public static class UploadSessionStore
{
	private static readonly ConcurrentDictionary<string, UploadSession> Sessions = new();
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	public static string RootDirectory { get; } = Path.Combine(Path.GetTempPath(), "FastCICD-UploadSessions");

	/// <summary>How long a session may stay inactive (no chunk received) before it is discarded.</summary>
	public static TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

	/// <summary>Number of sessions currently stored on disk.</summary>
	public static int CountActive()
		=> Directory.Exists(RootDirectory) ? Directory.EnumerateFiles(RootDirectory, "*.json").Count() : 0;

	/// <summary>Free bytes on the drive that holds the session files.</summary>
	public static long GetFreeBytes()
	{
		Directory.CreateDirectory(RootDirectory);
		return new DriveInfo(Path.GetPathRoot(RootDirectory)!).AvailableFreeSpace;
	}

	/// <summary>Deletes inactive sessions and orphaned part files. Returns how many sessions were removed.</summary>
	public static int Sweep(ILogger logger)
	{
		if (!Directory.Exists(RootDirectory))
			return 0;

		var removed = 0;
		var cutoff = DateTime.UtcNow - Retention;
		foreach (var metadataPath in Directory.EnumerateFiles(RootDirectory, "*.json").ToList())
		{
			var uploadId = Path.GetFileNameWithoutExtension(metadataPath);
			if (File.GetLastWriteTimeUtc(metadataPath) >= cutoff)
				continue;

			Sessions.TryRemove(uploadId, out _);
			TryDelete(metadataPath);
			TryDelete(GetPartFilePath(uploadId));
			removed++;
			logger.LogInformation("Removed inactive upload session {UploadId}.", uploadId);
		}

		// Part files whose metadata is gone (crash during create/delete) can never be resumed.
		foreach (var partPath in Directory.EnumerateFiles(RootDirectory, "*.part").ToList())
		{
			var uploadId = Path.GetFileNameWithoutExtension(partPath);
			if (File.Exists(GetMetadataPath(uploadId)) || File.GetLastWriteTimeUtc(partPath) >= cutoff)
				continue;

			TryDelete(partPath);
			logger.LogInformation("Removed orphaned upload file for {UploadId}.", uploadId);
		}

		return removed;
	}

	public static UploadSession Create(CreateUploadSessionRequest request)
	{
		Directory.CreateDirectory(RootDirectory);
		var uploadId = Guid.NewGuid().ToString("N");
		var metadata = new UploadSessionMetadata
		{
			UploadId = uploadId,
			ProjectName = request.ProjectName,
			Version = request.Version,
			EnableBackup = request.EnableBackup,
			MirrorServerToLocal = request.MirrorServerToLocal,
			IgnoredFiles = request.IgnoredFiles,
			SynchronizedFiles = request.SynchronizedFiles,
			TotalBytes = request.TotalBytes,
			ChunkSize = request.ChunkSize,
			FileHash = request.FileHash,
			CreatedUtc = DateTime.UtcNow
		};

		var session = new UploadSession(metadata, GetPartFilePath(uploadId));
		using (var file = new FileStream(session.PartFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous))
			file.SetLength(request.TotalBytes);

		Save(session);
		Sessions[uploadId] = session;
		return session;
	}

	public static UploadSession? Get(string uploadId)
	{
		if (!Guid.TryParseExact(uploadId, "N", out _))
			return null;

		if (Sessions.TryGetValue(uploadId, out var cached))
			return cached;

		var metadataPath = GetMetadataPath(uploadId);
		if (!File.Exists(metadataPath) || !File.Exists(GetPartFilePath(uploadId)))
			return null;

		try
		{
			var metadata = JsonSerializer.Deserialize<UploadSessionMetadata>(File.ReadAllText(metadataPath), JsonOptions);
			if (metadata == null)
				return null;
			if (File.GetLastWriteTimeUtc(metadataPath) < DateTime.UtcNow - Retention)
			{
				TryDelete(metadataPath);
				TryDelete(GetPartFilePath(uploadId));
				return null;
			}

			var session = new UploadSession(metadata, GetPartFilePath(uploadId));
			Sessions[uploadId] = session;
			return session;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	public static void Save(UploadSession session)
	{
		var metadataPath = GetMetadataPath(session.Metadata.UploadId);
		var temporaryPath = metadataPath + ".tmp";
		File.WriteAllText(temporaryPath, JsonSerializer.Serialize(session.Metadata, JsonOptions));
		File.Move(temporaryPath, metadataPath, overwrite: true);
	}

	public static void Delete(UploadSession session)
	{
		Sessions.TryRemove(session.Metadata.UploadId, out _);
		TryDelete(GetMetadataPath(session.Metadata.UploadId));
		TryDelete(session.PartFilePath);
	}

	private static string GetMetadataPath(string uploadId) => Path.Combine(RootDirectory, uploadId + ".json");
	private static string GetPartFilePath(string uploadId) => Path.Combine(RootDirectory, uploadId + ".part");

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch
		{
			// Cleanup is best effort; the session can be cleaned by a later maintenance pass.
		}
	}
}
