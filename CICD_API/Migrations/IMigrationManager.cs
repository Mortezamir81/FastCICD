using CICD_API.Models;

namespace CICD_API.Migrations;

public interface IMigrationManager
{
	Task<MigrationStatusResponse> GetStatusAsync(string profileName, CancellationToken cancellationToken);
	Task<MigrationScriptUploadResponse> UploadScriptAsync(string profileName, Stream scriptStream, string expectedSha256, CancellationToken cancellationToken);
	Task<ApplyMigrationsResponse> ApplyPendingAsync(string profileName, CancellationToken cancellationToken);
}
