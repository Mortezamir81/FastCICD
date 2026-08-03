namespace FastCICD;

public sealed record CreateUploadSessionRequest(
	string ProjectName,
	string Version,
	bool EnableBackup,
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
	public string FileHash { get; set; } = "";
	public string ZipPath { get; set; } = "";
}
