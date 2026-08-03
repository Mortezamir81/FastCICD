namespace CICD_API.Models;

public sealed record CreateUploadSessionRequest(
	string ProjectName,
	string Version,
	bool EnableBackup,
	long TotalBytes,
	int ChunkSize,
	string FileHash);

public sealed class UploadSessionMetadata
{
	public string UploadId { get; set; } = "";
	public string ProjectName { get; set; } = "";
	public string Version { get; set; } = "";
	public bool EnableBackup { get; set; }
	public long TotalBytes { get; set; }
	public int ChunkSize { get; set; }
	public string FileHash { get; set; } = "";
	public List<int> UploadedChunks { get; set; } = [];
	public DateTime CreatedUtc { get; set; }
}
