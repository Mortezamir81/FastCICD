using CICD_API.Models;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.Json;

namespace CICD_API.Endpoints;

public static class DeployEndpoints
{
	private static string GetMetadataPath(string backupDirBase, string projectName)
		=> Path.Combine(backupDirBase, projectName, "project_metadata.json");

	public static void MapDeployEndpoints(this IEndpointRouteBuilder app)
	{
		// Compare local and remote file hashes
		app.MapPost("/api/compare", async ([FromBody] CompareRequest request, IConfiguration config, ILogger<Program> logger) =>
		{
			logger.LogInformation("Starting file comparison for project '{ProjectName}'.", request.ProjectName);

			var allowedDirs = config.GetSection("AllowedDirectories").Get<Dictionary<string, string>>();

			if (allowedDirs == null || !allowedDirs.TryGetValue(request.ProjectName, out var baseDir))
			{
				logger.LogWarning("Comparison failed: Project '{ProjectName}' is not defined in allowed directories.", request.ProjectName);
				return Results.BadRequest($"Project '{request.ProjectName}' is not defined on the server.");
			}

			var missingOrChanged = new ConcurrentBag<string>();

			Parallel.ForEach(request.FileHashes, file =>
			{
				if (file.Key.Contains("..") || Path.IsPathRooted(file.Key))
					return;

				var remoteFilePath = Path.GetFullPath(Path.Combine(baseDir, file.Key));

				if (!remoteFilePath.StartsWith(baseDir))
					return;

				if (!File.Exists(remoteFilePath))
				{
					missingOrChanged.Add(file.Key);
					return;
				}

				using var sha256 = SHA256.Create();
				using var stream = File.OpenRead(remoteFilePath);
				var remoteHash = Convert.ToHexStringLower(sha256.ComputeHash(stream));

				if (remoteHash != file.Value)
					missingOrChanged.Add(file.Key);
			});

			logger.LogInformation("Comparison completed for project '{ProjectName}'. Found {Count} missing or changed files.", request.ProjectName, missingOrChanged.Count);
			return Results.Ok(missingOrChanged.ToList());
		});

		// Manage Windows Services (Start, Stop, Status)
		app.MapPost("/api/services", ([FromBody] ServiceRequest request, IConfiguration config, ILogger<Program> logger) =>
		{
			logger.LogInformation("Executing '{Action}' action on {Count} services.", request.Action, request.Services.Count);

			var allowedServices = config.GetSection("AllowedServices").Get<List<string>>() ?? [];
			var statuses = new Dictionary<string, string>();

			foreach (var serviceName in request.Services)
			{
				if (!allowedServices.Contains(serviceName))
				{
					logger.LogWarning("Attempted to manage unauthorized service: '{ServiceName}'.", serviceName);
					return Results.Problem($"Service {serviceName} is not allowed to be managed.");
				}

				try
				{
					// Check if the service actually exists on the machine before interacting with it
					var serviceExists = ServiceController.GetServices().Any(s => s.ServiceName == serviceName);

					if (!serviceExists)
					{
						// If service is not found, record a placeholder status and move to the next service
						logger.LogWarning("Service '{ServiceName}' was not found on the machine. Skipping...", serviceName);

						if (request.Action == "status")
						{
							statuses.Add(serviceName, "Not Found");
						}
						continue;
					}

					using var sc = new ServiceController(serviceName);

					if (request.Action == "status")
					{
						statuses.Add(serviceName, sc.Status.ToString());
						continue;
					}

					if (request.Action == "stop" && sc.Status != ServiceControllerStatus.Stopped)
					{
						logger.LogInformation("Stopping service '{ServiceName}'...", serviceName);
						sc.Stop();
						sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
						logger.LogInformation("Service '{ServiceName}' stopped successfully.", serviceName);
					}
					else if (request.Action == "start" && sc.Status != ServiceControllerStatus.Running)
					{
						logger.LogInformation("Starting service '{ServiceName}'...", serviceName);
						sc.Start();
						sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
						logger.LogInformation("Service '{ServiceName}' started successfully.", serviceName);
					}
				}
				catch (Exception ex)
				{
					// Log the error but CONTINUE the loop instead of aborting the entire request
					logger.LogError(ex, "Failed to manage service '{ServiceName}'. Error: {Message}", serviceName, ex.Message);

					if (request.Action == "status")
					{
						statuses.Add(serviceName, "Error");
					}
					continue;
				}
			}

			// Return statuses if requested, otherwise return standard OK
			return request.Action == "status" ? Results.Ok(statuses) : Results.Ok();
		});

		// Receive version and project name (Added Finally for strict Temp cleanup)
		app.MapPost("/api/upload", async (HttpRequest request, [FromQuery] string projectName, [FromQuery] string version, [FromQuery] bool enableBackup, IConfiguration config, ILogger<Program> logger) =>
		{
			logger.LogInformation("Upload initiated for project '{ProjectName}', Version: '{Version}'.", projectName, version);

			string tempZipPath = null; // Defined outside to be accessible in finally block

			try
			{
				var allowedDirs = config.GetSection("AllowedDirectories").Get<Dictionary<string, string>>();
				var backupDirBase = config["BackupDirectory"];

				if (allowedDirs == null || !allowedDirs.TryGetValue(projectName, out var baseDir))
				{
					logger.LogWarning("Upload failed: Project '{ProjectName}' is not defined.", projectName);
					return Results.BadRequest("Project not defined.");
				}

				// 1. Take Backup
				if (enableBackup && !string.IsNullOrEmpty(backupDirBase))
				{
					var projectBackupDir = Path.Combine(backupDirBase, projectName);
					Directory.CreateDirectory(projectBackupDir);
					var backupFilePath = Path.Combine(projectBackupDir, $"backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

					if (Directory.Exists(baseDir) && Directory.EnumerateFileSystemEntries(baseDir).Any())
					{
						logger.LogInformation("Creating backup for '{ProjectName}' at '{BackupFilePath}'.", projectName, backupFilePath);
						ZipFile.CreateFromDirectory(baseDir, backupFilePath);
					}
				}

				// 2. Extract Files
				if (request.Form.Files.Count == 0)
				{
					return Results.BadRequest("No file uploaded.");
				}

				var file = request.Form.Files[0];
				tempZipPath = Path.GetTempFileName();
				logger.LogInformation("Receiving file '{FileName}', saving to temporary path '{TempPath}'.", file.FileName, tempZipPath);

				using (var stream = new FileStream(tempZipPath, FileMode.Create))
				{
					// If connection drops here, exception is thrown and it jumps to catch.
					await file.CopyToAsync(stream);
				}

				logger.LogInformation("Extracting uploaded zip to '{BaseDirectory}'.", baseDir);
				ZipFile.ExtractToDirectory(tempZipPath, baseDir, overwriteFiles: true);

				// 3. Save Version Info
				if (enableBackup && !string.IsNullOrEmpty(backupDirBase))
				{
					var metadataPath = GetMetadataPath(backupDirBase, projectName);
					var metadata = new
					{
						Version = version,
						DeployDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
						IsLockedStorage = true
					};
					await File.WriteAllTextAsync(metadataPath, System.Text.Json.JsonSerializer.Serialize(metadata));
				}

				logger.LogInformation("Deployment completed successfully for project '{ProjectName}'.", projectName);
				return Results.Ok();
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Critical error during upload or extraction: {Message}", ex.Message);
				return Results.Problem($"Server Error Details: {ex.Message}");
			}
			finally
			{
				// SAFETY NET: This will ALWAYS run. 
				// It guarantees that partial/failed uploads don't eat up server disk space.
				if (!string.IsNullOrEmpty(tempZipPath) && File.Exists(tempZipPath))
				{
					try
					{
						File.Delete(tempZipPath);
						logger.LogInformation("Cleaned up temporary file: '{TempPath}'", tempZipPath);
					}
					catch (Exception cleanupEx)
					{
						logger.LogWarning("Failed to clean up temp file '{TempPath}'. It may be locked. Error: {Message}", tempZipPath, cleanupEx.Message);
					}
				}
			}
		});

		// Get available backups for a project
		app.MapGet("/api/backups", ([FromQuery] string projectName, IConfiguration config, ILogger<Program> logger) =>
		{
			var backupDirBase = config["BackupDirectory"];
			if (string.IsNullOrEmpty(backupDirBase))
			{
				logger.LogWarning("Backup directory base is not configured.");
				return Results.Ok(new List<string>());
			}

			var projectBackupDir = Path.Combine(backupDirBase, projectName);
			if (!Directory.Exists(projectBackupDir))
			{
				logger.LogInformation("No backups found: Directory '{ProjectBackupDir}' does not exist.", projectBackupDir);
				return Results.Ok(new List<string>());
			}

			var backups = Directory.GetFiles(projectBackupDir, "*.zip")
								   .Select(Path.GetFileName)
								   .OrderByDescending(f => f) // Show newest first
								   .ToList();

			logger.LogInformation("Retrieved {Count} backups for project '{ProjectName}'.", backups.Count, projectName);
			return Results.Ok(backups);
		});

		// Execute Rollback
		app.MapPost("/api/rollback", async ([FromBody] RollbackRequest request, IConfiguration config, ILogger<Program> logger) =>
		{
			logger.LogInformation("Rollback requested for project '{ProjectName}' using backup '{BackupFileName}'.", request.ProjectName, request.BackupFileName);

			var allowedDirs = config.GetSection("AllowedDirectories").Get<Dictionary<string, string>>();
			var backupDirBase = config["BackupDirectory"];

			if (allowedDirs == null || !allowedDirs.TryGetValue(request.ProjectName, out var baseDir))
			{
				logger.LogWarning("Rollback failed: Project '{ProjectName}' is not defined.", request.ProjectName);
				return Results.BadRequest("Project not defined.");
			}

			var projectBackupDir = Path.Combine(backupDirBase, request.ProjectName);
			var backupFilePath = Path.Combine(projectBackupDir, request.BackupFileName);

			if (!File.Exists(backupFilePath))
			{
				logger.LogError("Rollback failed: Backup file '{BackupFilePath}' not found.", backupFilePath);
				return Results.NotFound("Backup file not found.");
			}

			// Clean existing directory before rollback to prevent orphaned files
			logger.LogInformation("Cleaning existing directory '{BaseDirectory}' before applying rollback.", baseDir);
			var dirInfo = new DirectoryInfo(baseDir);
			foreach (var file in dirInfo.GetFiles())
				file.Delete();
			foreach (var dir in dirInfo.GetDirectories())
				dir.Delete(true);

			// Extract the backup
			logger.LogInformation("Extracting backup '{BackupFileName}' to '{BaseDirectory}'.", request.BackupFileName, baseDir);
			ZipFile.ExtractToDirectory(backupFilePath, baseDir, overwriteFiles: true);

			logger.LogInformation("Rollback completed successfully for project '{ProjectName}'.", request.ProjectName);
			return Results.Ok();
		});

		// Get current version of a project
		app.MapGet("/api/version", async ([FromQuery] string projectName, IConfiguration config, ILogger<Program> logger) =>
		{
			var backupDirBase = config["BackupDirectory"];

			// Prevent ArgumentNullException if BackupDirectory is missing in appsettings
			if (string.IsNullOrEmpty(backupDirBase))
			{
				logger.LogWarning("Version check requested but BackupDirectory is not configured.");
				return Results.Ok(new { Version = "Not Configured", DeployDate = "-" });
			}

			var metadataPath = GetMetadataPath(backupDirBase, projectName);

			if (!File.Exists(metadataPath))
				return Results.Ok(new { Version = "No version info", DeployDate = "-" });

			var content = await File.ReadAllTextAsync(metadataPath);
			return Results.Content(content, "application/json");
		});

		// Execute Remote CLI Commands
		app.MapPost("/api/execute", async ([FromBody] CommandRequest request, IConfiguration config, ILogger<Program> logger) =>
		{
			logger.LogInformation("Executing {Count} remote CLI commands for project '{ProjectName}'.", request.Commands.Count, request.ProjectName);

			var allowedDirs = config.GetSection("AllowedDirectories").Get<Dictionary<string, string>>();
			if (allowedDirs == null || !allowedDirs.TryGetValue(request.ProjectName, out var baseDir))
			{
				logger.LogWarning("Execution failed: Project '{ProjectName}' is not defined.", request.ProjectName);
				return Results.BadRequest("Project not defined.");
			}

			var results = new List<object>();

			foreach (var cmd in request.Commands)
			{
				try
				{
					// Use baseDir if it exists, otherwise fallback to Temp directory
					string safeWorkingDirectory = Directory.Exists(baseDir) ? baseDir : Path.GetTempPath();

					logger.LogInformation("Executing command: '{Command}' in directory '{BaseDirectory}'", cmd, safeWorkingDirectory);
					bool isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

					var processInfo = new System.Diagnostics.ProcessStartInfo
					{
						FileName = isWindows ? "cmd.exe" : "/bin/bash",
						Arguments = isWindows ? $"/c {cmd}" : $"-c \"{cmd}\"",
						WorkingDirectory = safeWorkingDirectory,
						RedirectStandardOutput = true,
						RedirectStandardError = true,
						UseShellExecute = false,
						CreateNoWindow = true
					};

					using var process = System.Diagnostics.Process.Start(processInfo);
					if (process != null)
					{
						// Prevent server freeze by adding a strict 2-minute timeout
						using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

						try
						{
							await process.WaitForExitAsync(cts.Token);
						}
						catch (TaskCanceledException)
						{
							logger.LogError("Command '{Command}' timed out after 2 minutes. Killing process.", cmd);
							process.Kill();
							return Results.Problem($"Command '{cmd}' timed out after 2 minutes.");
						}

						var output = await process.StandardOutput.ReadToEndAsync();
						var error = await process.StandardError.ReadToEndAsync();

						if (process.ExitCode != 0)
						{
							logger.LogError("Command '{Command}' failed with exit code {ExitCode}. Error output: {Error}", cmd, process.ExitCode, error);
							return Results.Problem($"Command '{cmd}' failed with exit code {process.ExitCode}.\nError: {error}");
						}

						logger.LogInformation("Command '{Command}' executed successfully.", cmd);
						results.Add(new { Command = cmd, Output = output });
					}
				}
				catch (Exception ex)
				{
					logger.LogError(ex, "Exception occurred while executing command '{Command}': {Message}", cmd, ex.Message);
					return Results.Problem($"Failed to execute '{cmd}': {ex.Message}");
				}
			}

			return Results.Ok(results);
		});
	}
}