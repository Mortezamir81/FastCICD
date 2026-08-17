using System.Text.RegularExpressions;
using System.Security.Cryptography;
using CICD_API.Models;
using Microsoft.Data.SqlClient;

namespace CICD_API.Migrations;

public sealed class MigrationManager(IConfiguration configuration, ILogger<MigrationManager> logger) : IMigrationManager
{
	private const int DefaultMaxScriptBytes = 50 * 1024 * 1024;
	private static readonly Regex GoSeparator = new("^\\s*GO(?:\\s+\\d+)?\\s*(?:--.*)?$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
	private static readonly Regex MigrationHistoryInsert = new("INSERT\\s+INTO\\s+\\[__EFMigrationsHistory\\][\\s\\S]*?VALUES\\s*\\(\\s*[Nn]'(?<id>[^']+)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	public async Task<MigrationStatusResponse> GetStatusAsync(string profileName, CancellationToken cancellationToken)
	{
		var (profile, migrations, scriptInfo) = await LoadProfileAndScriptAsync(profileName, cancellationToken);
		await using var connection = new SqlConnection(profile.ConnectionString);
		await connection.OpenAsync(cancellationToken);
		var applied = await GetAppliedMigrationIdsAsync(connection, profile.CommandTimeoutSeconds, cancellationToken);
		var configuredMigrationIds = migrations.Select(migration => migration.Id).ToHashSet(StringComparer.Ordinal);
		var status = migrations.Select(migration => new MigrationItemResponse(migration.Id, applied.Contains(migration.Id)))
			.Concat(applied.Where(id => !configuredMigrationIds.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).Select(id => new MigrationItemResponse(id, true)))
			.ToList();
		return new MigrationStatusResponse(profileName, scriptInfo.Sha256, scriptInfo.UploadedAt, status);
	}

	public async Task<MigrationScriptUploadResponse> UploadScriptAsync(string profileName, Stream scriptStream, string expectedSha256, CancellationToken cancellationToken)
	{
		_ = GetProfile(profileName);
		if (!IsSha256(expectedSha256))
			throw new ArgumentException("The migration script hash is invalid.", nameof(expectedSha256));
		var storageDirectory = GetStorageDirectory();
		Directory.CreateDirectory(storageDirectory);
		var temporaryPath = Path.Combine(storageDirectory, $"{Guid.NewGuid():N}.upload");
		try
		{
			await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
				await CopyWithLimitAsync(scriptStream, destination, GetMaxScriptBytes(), cancellationToken);
			var actualSha256 = await CalculateSha256Async(temporaryPath, cancellationToken);
			if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException("The uploaded migration script did not match its signed hash.");
			var script = await File.ReadAllTextAsync(temporaryPath, cancellationToken);
			var migrations = ParseMigrations(script);
			ValidateMigrations(migrations);
			var uploadedAt = DateTimeOffset.UtcNow;
			var destinationPath = GetStoredScriptPath(profileName);
			File.Move(temporaryPath, destinationPath, overwrite: true);
			await File.WriteAllTextAsync(GetStoredMetadataPath(profileName), $"{actualSha256}\n{uploadedAt:O}", cancellationToken);
			logger.LogInformation("Stored migration script for profile '{ProfileName}'. Hash: {ScriptHash}; migrations: {MigrationCount}.", profileName, actualSha256, migrations.Count);
			return new MigrationScriptUploadResponse(profileName, actualSha256, uploadedAt, migrations.Count);
		}
		finally
		{
			if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
		}
	}

	public async Task<ApplyMigrationsResponse> ApplyPendingAsync(string profileName, CancellationToken cancellationToken)
	{
		var (profile, migrations, _) = await LoadProfileAndScriptAsync(profileName, cancellationToken);
		await using var connection = new SqlConnection(profile.ConnectionString);
		await connection.OpenAsync(cancellationToken);
		await AcquireExecutionLockAsync(connection, profileName, profile.CommandTimeoutSeconds, cancellationToken);

		try
		{
			await EnsureHistoryTableAsync(connection, profile.CommandTimeoutSeconds, cancellationToken);
			var applied = await GetAppliedMigrationIdsAsync(connection, profile.CommandTimeoutSeconds, cancellationToken);
			var pending = migrations.Where(m => !applied.Contains(m.Id)).ToList();
			var executed = new List<string>();

			foreach (var migration in pending)
			{
				await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
				try
				{
					foreach (var batch in migration.Batches)
					{
						await ExecuteAsync(connection, transaction, batch, profile.CommandTimeoutSeconds, cancellationToken);
					}

					await transaction.CommitAsync(cancellationToken);
					executed.Add(migration.Id);
					logger.LogInformation("Applied migration '{MigrationId}' using profile '{ProfileName}'.", migration.Id, profileName);
				}
				catch
				{
					await transaction.RollbackAsync(CancellationToken.None);
					logger.LogError("Migration '{MigrationId}' failed using profile '{ProfileName}'.", migration.Id, profileName);
					throw;
				}
			}

			return new ApplyMigrationsResponse(profileName, executed);
		}
		finally
		{
			await ReleaseExecutionLockAsync(connection, profileName, profile.CommandTimeoutSeconds);
		}
	}

	private async Task<(MigrationProfileOptions Profile, IReadOnlyList<MigrationScriptBlock> Migrations, StoredScriptInfo ScriptInfo)> LoadProfileAndScriptAsync(string profileName, CancellationToken cancellationToken)
	{
		var profile = GetProfile(profileName);
		var scriptPath = GetStoredScriptPath(profileName);
		var info = new FileInfo(scriptPath);
		if (!info.Exists)
			throw new FileNotFoundException("No migration script has been uploaded for this profile.");
		if (info.Length > GetMaxScriptBytes())
			throw new InvalidOperationException("The configured migration script is too large.");

		var script = await File.ReadAllTextAsync(scriptPath, cancellationToken);
		var migrations = ParseMigrations(script);
		ValidateMigrations(migrations);
		var metadata = await ReadStoredScriptInfoAsync(profileName, scriptPath, cancellationToken);
		return (profile, migrations, metadata);
	}

	private MigrationProfileOptions GetProfile(string profileName)
	{
		if (string.IsNullOrWhiteSpace(profileName) || !Regex.IsMatch(profileName, "^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant))
			throw new ArgumentException("A valid migration profile is required.", nameof(profileName));
		var profile = configuration.GetSection("MigrationProfiles").GetSection(profileName).Get<MigrationProfileOptions>()
			?? throw new KeyNotFoundException("Migration profile is not configured.");
		if (string.IsNullOrWhiteSpace(profile.ConnectionString))
			throw new InvalidOperationException("Migration profile is incomplete.");
		return new MigrationProfileOptions { ConnectionString = profile.ConnectionString, CommandTimeoutSeconds = Math.Clamp(profile.CommandTimeoutSeconds, 30, 3600) };
	}

	private string GetStorageDirectory()
	{
		var directory = configuration["MigrationScriptStorageDirectory"];
		if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
			throw new InvalidOperationException("Migration script storage directory must be an absolute server path.");
		return Path.GetFullPath(directory);
	}

	private string GetStoredScriptPath(string profileName) => Path.Combine(GetStorageDirectory(), $"{profileName}.sql");
	private string GetStoredMetadataPath(string profileName) => Path.Combine(GetStorageDirectory(), $"{profileName}.meta");
	private int GetMaxScriptBytes() => Math.Clamp(configuration.GetValue<int?>("MigrationMaxScriptBytes") ?? DefaultMaxScriptBytes, 1024, DefaultMaxScriptBytes);

	private static List<MigrationScriptBlock> ParseMigrations(string script)
	{
		var migrations = new List<MigrationScriptBlock>();
		var pendingBatches = new List<string>();
		foreach (var batch in GoSeparator.Split(script))
		{
			var trimmed = batch.Trim();
			if (string.IsNullOrWhiteSpace(trimmed) || IsOuterTransactionBoundary(trimmed))
				continue;
			pendingBatches.Add(trimmed);
			var match = MigrationHistoryInsert.Match(trimmed);
			if (!match.Success)
				continue;
			migrations.Add(new MigrationScriptBlock(match.Groups["id"].Value, [.. pendingBatches]));
			pendingBatches.Clear();
		}
		return migrations;
	}

	private static void ValidateMigrations(IReadOnlyList<MigrationScriptBlock> migrations)
	{
		if (migrations.Count == 0 || migrations.Count > 10000 || migrations.Any(migration => migration.Id.Length > 150 || string.IsNullOrWhiteSpace(migration.Id)) || migrations.Select(migration => migration.Id).Distinct(StringComparer.Ordinal).Count() != migrations.Count)
			throw new InvalidOperationException("The uploaded script is not a valid EF Core migration script.");
	}

	private async Task<StoredScriptInfo> ReadStoredScriptInfoAsync(string profileName, string scriptPath, CancellationToken cancellationToken)
	{
		var metadataPath = GetStoredMetadataPath(profileName);
		if (File.Exists(metadataPath))
		{
			var values = (await File.ReadAllLinesAsync(metadataPath, cancellationToken)).Take(2).ToArray();
			if (values.Length == 2 && IsSha256(values[0]) && DateTimeOffset.TryParse(values[1], out var uploadedAt))
				return new StoredScriptInfo(values[0], uploadedAt);
		}
		return new StoredScriptInfo(await CalculateSha256Async(scriptPath, cancellationToken), File.GetLastWriteTimeUtc(scriptPath));
	}

	private static bool IsSha256(string value) => Regex.IsMatch(value, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant);
	private static async Task<string> CalculateSha256Async(string path, CancellationToken cancellationToken)
	{
		await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
		return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
	}

	private static async Task CopyWithLimitAsync(Stream source, Stream destination, int maximumBytes, CancellationToken cancellationToken)
	{
		var buffer = new byte[81920];
		long total = 0;
		while (true)
		{
			var bytesRead = await source.ReadAsync(buffer, cancellationToken);
			if (bytesRead == 0) return;
			total += bytesRead;
			if (total > maximumBytes)
				throw new InvalidOperationException("The migration script exceeds the server size limit.");
			await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
		}
	}

	private static bool IsOuterTransactionBoundary(string batch) =>
		batch.Equals("BEGIN TRANSACTION;", StringComparison.OrdinalIgnoreCase) ||
		batch.Equals("COMMIT;", StringComparison.OrdinalIgnoreCase);

	private static async Task<HashSet<string>> GetAppliedMigrationIdsAsync(SqlConnection connection, int timeout, CancellationToken cancellationToken)
	{
		const string sql = "IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL SELECT CAST(NULL AS nvarchar(150)) WHERE 1 = 0; ELSE SELECT [MigrationId] FROM [__EFMigrationsHistory];";
		await using var command = new SqlCommand(sql, connection) { CommandTimeout = timeout };
		await using var reader = await command.ExecuteReaderAsync(cancellationToken);
		var results = new HashSet<string>(StringComparer.Ordinal);
		while (await reader.ReadAsync(cancellationToken))
			results.Add(reader.GetString(0));
		return results;
	}

	private static Task EnsureHistoryTableAsync(SqlConnection connection, int timeout, CancellationToken cancellationToken) =>
		ExecuteAsync(connection, null, "IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL CREATE TABLE [__EFMigrationsHistory] ([MigrationId] nvarchar(150) NOT NULL, [ProductVersion] nvarchar(32) NOT NULL, CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId]));", timeout, cancellationToken);

	private static Task ExecuteAsync(SqlConnection connection, SqlTransaction? transaction, string sql, int timeout, CancellationToken cancellationToken)
	{
		var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = timeout };
		return DisposeAfterExecuteAsync(command, cancellationToken);
	}

	private static async Task DisposeAfterExecuteAsync(SqlCommand command, CancellationToken cancellationToken)
	{
		await using (command)
			await command.ExecuteNonQueryAsync(cancellationToken);
	}

	private static async Task AcquireExecutionLockAsync(SqlConnection connection, string profileName, int timeout, CancellationToken cancellationToken)
	{
		await using var command = new SqlCommand("DECLARE @result int; EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; IF @result < 0 THROW 50001, 'Another migration execution is already in progress.', 1;", connection) { CommandTimeout = timeout };
		command.Parameters.AddWithValue("@resource", $"FastCICD:Migration:{profileName}");
		await command.ExecuteNonQueryAsync(cancellationToken);
	}

	private static async Task ReleaseExecutionLockAsync(SqlConnection connection, string profileName, int timeout)
	{
		await using var command = new SqlCommand("EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';", connection) { CommandTimeout = timeout };
		command.Parameters.AddWithValue("@resource", $"FastCICD:Migration:{profileName}");
		await command.ExecuteNonQueryAsync();
	}

	private sealed record MigrationScriptBlock(string Id, IReadOnlyList<string> Batches);
	private sealed record StoredScriptInfo(string Sha256, DateTimeOffset UploadedAt);
}
