namespace FastCICD;

public sealed record CompareResponse(List<string> DeltaFiles, int ExtraFileCount, string? SyncManifestId);

public sealed record CreateUploadSessionRequest(
	string ProjectName,
	string Version,
	bool EnableBackup,
	bool MirrorServerToLocal,
	List<string> IgnoredFiles,
	List<string> SynchronizedFiles,
	string? SyncManifestId,
	long TotalBytes,
	int ChunkSize,
	string FileHash);

public sealed record UploadSessionResponse(string UploadId, int ChunkSize, int TotalChunks);

public sealed record UploadSessionStatusResponse(
	string UploadId,
	long TotalBytes,
	int ChunkSize,
	int TotalChunks,
	int[] UploadedChunks);

public sealed class PendingUploadManifest
{
	public string UploadId { get; set; } = "";
	public string ProjectName { get; set; } = "";
	public string Version { get; set; } = "";
	public bool EnableBackup { get; set; }
	public bool MirrorServerToLocal { get; set; }
	public string FileHash { get; set; } = "";
	public string ZipPath { get; set; } = "";
}
