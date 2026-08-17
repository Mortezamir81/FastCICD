namespace FastCICD.Models;

public sealed record MigrationStatusResponse(string ProfileName, string ScriptSha256, DateTimeOffset ScriptUploadedAt, IReadOnlyList<MigrationItemResponse> Migrations);
public sealed record MigrationItemResponse(string MigrationId, bool IsApplied);
public sealed record ApplyMigrationsResponse(string ProfileName, IReadOnlyList<string> AppliedMigrationIds);
public sealed record MigrationScriptUploadResponse(string ProfileName, string ScriptSha256, DateTimeOffset UploadedAt, int MigrationCount);
