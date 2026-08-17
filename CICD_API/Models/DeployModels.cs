namespace CICD_API.Models;

public record CommandRequest(string ProjectName, List<string> Commands);
public record RollbackRequest(string ProjectName, string BackupFileName);
public sealed record CompareRequest(string ProjectName, Dictionary<string, string> FileHashes, List<string>? IgnoredFiles, bool MirrorServerToLocal);
public sealed record CompareResponse(List<string> DeltaFiles, int ExtraFileCount, string? SyncManifestId);
public record ServiceRequest(List<string> Services, string Action);

/// <summary>Describes a migration script configured on the deployment server.</summary>
public sealed record MigrationStatusResponse(string ProfileName, string ScriptSha256, DateTimeOffset ScriptUploadedAt, IReadOnlyList<MigrationItemResponse> Migrations);

/// <summary>Describes one migration and whether its ID is recorded by EF Core.</summary>
public sealed record MigrationItemResponse(string MigrationId, bool IsApplied);

/// <summary>Summarizes a server-side migration execution.</summary>
public sealed record ApplyMigrationsResponse(string ProfileName, IReadOnlyList<string> AppliedMigrationIds);

/// <summary>Identifies a migration script securely stored by the deployment server.</summary>
public sealed record MigrationScriptUploadResponse(string ProfileName, string ScriptSha256, DateTimeOffset UploadedAt, int MigrationCount);
