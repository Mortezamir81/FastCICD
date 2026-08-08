namespace CICD_API.Models;

public record CommandRequest(string ProjectName, List<string> Commands);
public record RollbackRequest(string ProjectName, string BackupFileName);
public sealed record CompareRequest(string ProjectName, Dictionary<string, string> FileHashes, List<string>? IgnoredFiles, bool MirrorServerToLocal);
public sealed record CompareResponse(List<string> DeltaFiles, int ExtraFileCount, string? SyncManifestId);
public record ServiceRequest(List<string> Services, string Action);
