using CICD_API.Models;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.Json;
using CICD_API.Uploads;

namespace CICD_API.Endpoints;

public static class DeployEndpoints
{
	private static bool IsIgnoredPath(string path, IEnumerable<string> ignoredFiles)
	{
		var normalizedPath = path.Replace('/', '\\');
		return ignoredFiles.Any(ignored =>
		{
			var normalizedIgnored = ignored.Replace('/', '\\').TrimEnd('\\');
			return normalizedPath.Equals(normalizedIgnored, StringComparison.OrdinalIgnoreCase) ||
				normalizedPath.StartsWith(normalizedIgnored + "\\", StringComparison.OrdinalIgnoreCase);
		});
	}

	private static string GetMetadataPath(string backupDirBase, string projectName)
		=> Path.Combine(backupDirBase, projectName, "project_metadata.json");

	private static string GetRequestId(HttpRequest request)
		=> request.Headers["X-Request-Id"].FirstOrDefault() ?? request.HttpContext.TraceIdentifier;

	public static void MapDeployEndpoints(this IEndpointRouteBuilder app)
	{
		MapResumableUploadEndpoints(app);

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
			var ignoredFiles = request.IgnoredFiles ?? [];

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

			var extraFiles = new List<string>();
			if (request.MirrorServerToLocal && Directory.Exists(baseDir))
			{
				foreach (var remoteFile in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
				{
					var relativePath = Path.GetRelativePath(baseDir, remoteFile);
					if (!IsIgnoredPath(relativePath, ignoredFiles) && !request.FileHashes.ContainsKey(relativePath))
						extraFiles.Add(relativePath);
				}
			}
			var syncManifestId = request.MirrorServerToLocal && (missingOrChanged.Count > 0 || extraFiles.Count > 0)
				? SyncManifestStore.Create(request.ProjectName, ignoredFiles, request.FileHashes.Keys)
				: null;

			logger.LogInformation("Comparison completed for project '{ProjectName}'. Found {ChangedCount} missing or changed files and {ExtraCount} extra files.", request.ProjectName, missingOrChanged.Count, extraFiles.Count);
			return Results.Ok(new CompareResponse(missingOrChanged.ToList(), extraFiles.Count, syncManifestId));
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

				// 🚀 ROBUST EXTRACTION: Handles Read-Only files (like .git objects) safely
				using (var archive = ZipFile.OpenRead(tempZipPath))
				{
					foreach (var entry in archive.Entries)
					{
						// Prevent Zip Slip vulnerability
						var destinationPath = Path.GetFullPath(Path.Combine(baseDir, entry.FullName));
						if (!destinationPath.StartsWith(baseDir))
							continue;

						if (string.IsNullOrEmpty(entry.Name))
						{
							// It's a directory
							Directory.CreateDirectory(destinationPath);
						}
						else
						{
							// It's a file
							Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

							if (File.Exists(destinationPath))
							{
								// Force remove Read-Only attribute before overwriting
								var attributes = File.GetAttributes(destinationPath);
								if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
								{
									File.SetAttributes(destinationPath, attributes & ~FileAttributes.ReadOnly);
								}
							}

							entry.ExtractToFile(destinationPath, overwrite: true);
						}
					}
				}

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
		app.MapPost("/api/execute", async ([FromBody] CommandRequest request, HttpResponse response, IConfiguration config, ILogger<Program> logger, CancellationToken cancellationToken) =>
		{
			logger.LogInformation("Executing {Count} remote CLI commands for project '{ProjectName}'.", request.Commands.Count, request.ProjectName);

			var allowedDirs = config.GetSection("AllowedDirectories").Get<Dictionary<string, string>>();
			if (allowedDirs == null || !allowedDirs.TryGetValue(request.ProjectName, out var baseDir))
			{
				logger.LogWarning("Execution failed: Project '{ProjectName}' is not defined.", request.ProjectName);
				return Results.BadRequest("Project not defined.");
			}

			response.ContentType = "application/x-ndjson";
			var writeLock = new SemaphoreSlim(1, 1);
			async Task WriteEventAsync(object value)
			{
				await writeLock.WaitAsync(cancellationToken);
				try
				{
					await JsonSerializer.SerializeAsync(response.Body, value, cancellationToken: cancellationToken);
					await response.Body.WriteAsync("\n"u8.ToArray(), cancellationToken);
					await response.Body.FlushAsync(cancellationToken);
				}
				finally { writeLock.Release(); }
			}

			foreach (var cmd in request.Commands)
			{
				try
				{
					// Use baseDir if it exists, otherwise fallback to Temp directory
					string safeWorkingDirectory = Directory.Exists(baseDir) ? baseDir : Path.GetTempPath();

					// Mask sensitive commands for logging purposes to prevent password leaks
					string loggableCmd = cmd;
					if (loggableCmd.Contains("Unlock-BitLocker", StringComparison.OrdinalIgnoreCase) ||
						loggableCmd.Contains("-Password", StringComparison.OrdinalIgnoreCase))
					{
						loggableCmd = "[REDACTED SECURE COMMAND]";
					}

					logger.LogInformation("Executing command: '{Command}' in directory '{BaseDirectory}'", loggableCmd, safeWorkingDirectory);
					await WriteEventAsync(new { Type = "started", Command = loggableCmd });
					bool isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);

					var processInfo = new System.Diagnostics.ProcessStartInfo
					{
						FileName = isWindows ? "cmd.exe" : "/bin/bash",
						// Ensure the REAL command is passed to the OS, not the redacted one
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
						using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
						using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
						var outputTask = StreamProcessOutputAsync(process.StandardOutput, "output", WriteEventAsync, linkedCts.Token);
						var errorTask = StreamProcessOutputAsync(process.StandardError, "error", WriteEventAsync, linkedCts.Token);

						try
						{
							await process.WaitForExitAsync(linkedCts.Token);
							await Task.WhenAll(outputTask, errorTask);
						}
						catch (TaskCanceledException)
						{
							logger.LogError("Command '{Command}' timed out after 2 minutes. Killing process.", loggableCmd);
							if (!process.HasExited) process.Kill(entireProcessTree: true);
							await WriteEventAsync(new { Type = "error", Message = $"Command '{loggableCmd}' timed out after 2 minutes." });
							return Results.Empty;
						}

						if (process.ExitCode != 0)
						{
							logger.LogError("Command '{Command}' failed with exit code {ExitCode}.", loggableCmd, process.ExitCode);
							await WriteEventAsync(new { Type = "error", Message = $"Command '{loggableCmd}' failed with exit code {process.ExitCode}." });
							return Results.Empty;
						}

						logger.LogInformation("Command '{Command}' executed successfully.", loggableCmd);
						await WriteEventAsync(new { Type = "completed", Command = loggableCmd });
					}
				}
				catch (Exception ex)
				{
					logger.LogError(ex, "Exception occurred while executing command: {Message}", ex.Message);
					await WriteEventAsync(new { Type = "error", Message = $"Failed to execute command: {ex.Message}" });
					return Results.Empty;
				}
			}

			await WriteEventAsync(new { Type = "finished" });
			return Results.Empty;
		});
	}

	private static async Task StreamProcessOutputAsync(
		StreamReader reader,
		string streamName,
		Func<object, Task> writeEventAsync,
		CancellationToken cancellationToken)
	{
		while (await reader.ReadLineAsync(cancellationToken) is { } line)
			await writeEventAsync(new { Type = streamName, Message = line });
	}

	private static void MapResumableUploadEndpoints(IEndpointRouteBuilder app)
	{
		app.MapPost("/api/upload/sessions", (CreateUploadSessionRequest request, HttpRequest httpRequest, IConfiguration config, ILogger<Program> logger) =>
		{
			var requestId = GetRequestId(httpRequest);
			var allowedDirs = config.GetSection("AllowedDirectories").Get<Dictionary<string, string>>();
			var maxUploadBytes = config.GetValue<long?>("MaxUploadBytes") ?? 10L * 1024 * 1024 * 1024;
			var maxChunkSize = config.GetValue<int?>("UploadChunkSizeBytes") ?? 8 * 1024 * 1024;

			if (allowedDirs == null || !allowedDirs.ContainsKey(request.ProjectName))
			{
				logger.LogWarning("Upload session rejected. RequestId: {RequestId}; Reason: Project not defined; Project: {ProjectName}", requestId, request.ProjectName);
				return Results.BadRequest("Project not defined.");
			}
			if (request.TotalBytes <= 0 || request.TotalBytes > maxUploadBytes)
			{
				logger.LogWarning("Upload session rejected. RequestId: {RequestId}; Reason: Invalid total size; Project: {ProjectName}; TotalBytes: {TotalBytes}; MaxUploadBytes: {MaxUploadBytes}", requestId, request.ProjectName, request.TotalBytes, maxUploadBytes);
				return Results.BadRequest("Upload size is invalid or exceeds the server limit.");
			}
			if (request.ChunkSize <= 0)
			{
				logger.LogWarning("Upload session rejected. RequestId: {RequestId}; Reason: Invalid chunk size; Project: {ProjectName}; ChunkSize: {ChunkSize}", requestId, request.ProjectName, request.ChunkSize);
				return Results.BadRequest("Chunk size must be positive.");
			}
			if (string.IsNullOrWhiteSpace(request.FileHash) || request.FileHash.Length != 64)
			{
				logger.LogWarning("Upload session rejected. RequestId: {RequestId}; Reason: Invalid file hash length; Project: {ProjectName}; HashLength: {HashLength}", requestId, request.ProjectName, request.FileHash?.Length ?? 0);
				return Results.BadRequest("A SHA-256 file hash is required.");
			}

			var normalizedRequest = request with { ChunkSize = Math.Min(request.ChunkSize, maxChunkSize) };
			if (request.MirrorServerToLocal)
			{
				if (string.IsNullOrWhiteSpace(request.SyncManifestId))
					return Results.Conflict("A synchronization manifest is required for mirror deployment. Please compare again.");

				var manifest = SyncManifestStore.Take(request.SyncManifestId, request.ProjectName);
				if (manifest == null)
					return Results.Conflict("The synchronization manifest expired or is no longer available. Please compare again.");

				normalizedRequest = normalizedRequest with
				{
					IgnoredFiles = manifest.IgnoredFiles,
					SynchronizedFiles = manifest.SynchronizedFiles
				};
			}
			var session = UploadSessionStore.Create(normalizedRequest);
			logger.LogInformation("Created resumable upload session. RequestId: {RequestId}; UploadId: {UploadId}; Project: {ProjectName}; TotalBytes: {TotalBytes}; ChunkSize: {ChunkSize}; TotalChunks: {TotalChunks}", requestId, session.Metadata.UploadId, request.ProjectName, session.Metadata.TotalBytes, session.Metadata.ChunkSize, GetTotalChunks(session.Metadata));
			return Results.Ok(new
			{
				UploadId = session.Metadata.UploadId,
				ChunkSize = session.Metadata.ChunkSize,
				TotalChunks = GetTotalChunks(session.Metadata)
			});
		});

		app.MapGet("/api/upload/sessions/{uploadId}", (string uploadId, HttpRequest httpRequest, ILogger<Program> logger) =>
		{
			var requestId = GetRequestId(httpRequest);
			var session = UploadSessionStore.Get(uploadId);
			if (session == null)
			{
				logger.LogWarning("Upload session lookup failed. RequestId: {RequestId}; UploadId: {UploadId}; Reason: Session not found", requestId, uploadId);
				return Results.NotFound("Upload session not found.");
			}

			lock (session.SyncRoot)
			{
				return Results.Ok(new
				{
					UploadId = session.Metadata.UploadId,
					TotalBytes = session.Metadata.TotalBytes,
					ChunkSize = session.Metadata.ChunkSize,
					TotalChunks = GetTotalChunks(session.Metadata),
					UploadedChunks = session.Metadata.UploadedChunks.Order().ToArray()
				});
			}
		});

		app.MapMethods("/api/upload/sessions/{uploadId}/chunks/{chunkIndex:int}", new[] { HttpMethods.Put, HttpMethods.Post }, async (string uploadId, int chunkIndex, HttpRequest request, ILogger<Program> logger) =>
		{
			var requestId = GetRequestId(request);
			var stopwatch = Stopwatch.StartNew();
			var requestContentLength = request.ContentLength;
			var session = UploadSessionStore.Get(uploadId);
			if (session == null)
			{
				logger.LogWarning("Chunk rejected. RequestId: {RequestId}; UploadId: {UploadId}; ChunkIndex: {ChunkIndex}; Status: 404; Reason: Session not found; ContentLength: {ContentLength}", requestId, uploadId, chunkIndex, requestContentLength);
				return Results.NotFound("Upload session not found.");
			}

			var metadata = session.Metadata;
			var totalChunks = GetTotalChunks(metadata);
			logger.LogInformation("Chunk received. RequestId: {RequestId}; UploadId: {UploadId}; Project: {ProjectName}; ChunkIndex: {ChunkIndex}; TotalChunks: {TotalChunks}; ExpectedLength: {ExpectedLength}; ContentLength: {ContentLength}; TransferEncoding: {TransferEncoding}", requestId, uploadId, metadata.ProjectName, chunkIndex, totalChunks, GetChunkLength(metadata, Math.Clamp(chunkIndex, 0, Math.Max(0, totalChunks - 1))), requestContentLength, request.Headers["Transfer-Encoding"].ToString());
			if (chunkIndex < 0 || chunkIndex >= totalChunks)
			{
				logger.LogWarning("Chunk rejected. RequestId: {RequestId}; UploadId: {UploadId}; ChunkIndex: {ChunkIndex}; TotalChunks: {TotalChunks}; Status: 400; Reason: Chunk index outside range", requestId, uploadId, chunkIndex, totalChunks);
				return Results.BadRequest("Chunk index is outside the upload range.");
			}

			var expectedLength = GetChunkLength(metadata, chunkIndex);
			if (requestContentLength != expectedLength)
			{
				logger.LogWarning("Chunk rejected. RequestId: {RequestId}; UploadId: {UploadId}; ChunkIndex: {ChunkIndex}; Status: 400; Reason: Content length mismatch; ExpectedLength: {ExpectedLength}; ContentLength: {ContentLength}", requestId, uploadId, chunkIndex, expectedLength, requestContentLength);
				return Results.BadRequest($"Expected chunk length {expectedLength}, received {requestContentLength ?? 0}.");
			}

			lock (session.SyncRoot)
			{
				if (metadata.UploadedChunks.Contains(chunkIndex))
				{
					logger.LogInformation("Chunk already uploaded. RequestId: {RequestId}; UploadId: {UploadId}; ChunkIndex: {ChunkIndex}", requestId, uploadId, chunkIndex);
					return Results.Ok(new { ChunkIndex = chunkIndex, AlreadyUploaded = true });
				}
			}

			try
			{
				long offset = (long)chunkIndex * metadata.ChunkSize;
				await using var stream = new FileStream(session.PartFilePath, FileMode.Open, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
				stream.Position = offset;
				await request.Body.CopyToAsync(stream, request.HttpContext.RequestAborted);
				await stream.FlushAsync(request.HttpContext.RequestAborted);

				lock (session.SyncRoot)
				{
					if (!metadata.UploadedChunks.Contains(chunkIndex))
						metadata.UploadedChunks.Add(chunkIndex);
					UploadSessionStore.Save(session);
				}
				stopwatch.Stop();
				logger.LogInformation("Chunk saved. RequestId: {RequestId}; UploadId: {UploadId}; ChunkIndex: {ChunkIndex}; Bytes: {Bytes}; ElapsedMs: {ElapsedMs}; UploadedChunks: {UploadedChunks}/{TotalChunks}", requestId, uploadId, chunkIndex, expectedLength, stopwatch.ElapsedMilliseconds, metadata.UploadedChunks.Count, totalChunks);

				return Results.Ok(new { ChunkIndex = chunkIndex, AlreadyUploaded = false });
			}
			catch (OperationCanceledException)
			{
				logger.LogWarning("Chunk canceled. RequestId: {RequestId}; UploadId: {UploadId}; ChunkIndex: {ChunkIndex}; ElapsedMs: {ElapsedMs}; RequestAborted: {RequestAborted}", requestId, uploadId, chunkIndex, stopwatch.ElapsedMilliseconds, request.HttpContext.RequestAborted.IsCancellationRequested);
				return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Chunk save failed. RequestId: {RequestId}; UploadId: {UploadId}; ChunkIndex: {ChunkIndex}; ElapsedMs: {ElapsedMs}; ContentLength: {ContentLength}", requestId, uploadId, chunkIndex, stopwatch.ElapsedMilliseconds, requestContentLength);
				return Results.Problem("The upload chunk could not be saved.");
			}
		});

		app.MapPost("/api/upload/sessions/{uploadId}/complete", async (string uploadId, HttpRequest request, IConfiguration config, ILogger<Program> logger) =>
		{
			var requestId = GetRequestId(request);
			var stopwatch = Stopwatch.StartNew();
			var session = UploadSessionStore.Get(uploadId);
			if (session == null)
			{
				logger.LogWarning("Upload completion rejected. RequestId: {RequestId}; UploadId: {UploadId}; Status: 404; Reason: Session not found", requestId, uploadId);
				return Results.NotFound("Upload session not found.");
			}

			lock (session.SyncRoot)
			{
				if (session.Metadata.UploadedChunks.Count != GetTotalChunks(session.Metadata))
				{
					logger.LogWarning("Upload completion rejected. RequestId: {RequestId}; UploadId: {UploadId}; Status: 409; Reason: Incomplete upload; UploadedChunks: {UploadedChunks}; TotalChunks: {TotalChunks}", requestId, uploadId, session.Metadata.UploadedChunks.Count, GetTotalChunks(session.Metadata));
					return Results.Conflict("The upload is incomplete.");
				}
			}

			try
			{
				await using (var stream = new FileStream(session.PartFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
				{
					using var sha256 = SHA256.Create();
					var actualHash = Convert.ToHexStringLower(await sha256.ComputeHashAsync(stream));
					if (!actualHash.Equals(session.Metadata.FileHash, StringComparison.OrdinalIgnoreCase))
					{
						logger.LogWarning("Upload integrity check failed. RequestId: {RequestId}; UploadId: {UploadId}; ExpectedHashPrefix: {ExpectedHashPrefix}; ActualHashPrefix: {ActualHashPrefix}", requestId, uploadId, session.Metadata.FileHash[..Math.Min(12, session.Metadata.FileHash.Length)], actualHash[..12]);
						return Results.Problem("Upload integrity verification failed.", statusCode: StatusCodes.Status422UnprocessableEntity);
					}
				}

				await ProcessUploadedZipAsync(session.PartFilePath, session.Metadata.ProjectName, session.Metadata.Version, session.Metadata.EnableBackup, session.Metadata.MirrorServerToLocal, session.Metadata.IgnoredFiles, session.Metadata.SynchronizedFiles, config, logger);
				UploadSessionStore.Delete(session);
				stopwatch.Stop();
				logger.LogInformation("Upload completed and processed. RequestId: {RequestId}; UploadId: {UploadId}; Project: {ProjectName}; ElapsedMs: {ElapsedMs}", requestId, uploadId, session.Metadata.ProjectName, stopwatch.ElapsedMilliseconds);
				return Results.Ok();
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Upload completion failed. RequestId: {RequestId}; UploadId: {UploadId}; ElapsedMs: {ElapsedMs}", requestId, uploadId, stopwatch.ElapsedMilliseconds);
				return Results.Problem($"Server Error Details: {ex.Message}");
			}
		});
	}

	private static int GetTotalChunks(UploadSessionMetadata metadata)
		=> checked((int)((metadata.TotalBytes + metadata.ChunkSize - 1) / metadata.ChunkSize));

	private static long GetChunkLength(UploadSessionMetadata metadata, int chunkIndex)
	{
		var offset = (long)chunkIndex * metadata.ChunkSize;
		return Math.Min(metadata.ChunkSize, metadata.TotalBytes - offset);
	}

	private static async Task ProcessUploadedZipAsync(string zipPath, string projectName, string version, bool enableBackup, bool mirrorServerToLocal, List<string> ignoredFiles, List<string> synchronizedFiles, IConfiguration config, ILogger<Program> logger)
	{
		var allowedDirs = config.GetSection("AllowedDirectories").Get<Dictionary<string, string>>();
		var backupDirBase = config["BackupDirectory"];
		if (allowedDirs == null || !allowedDirs.TryGetValue(projectName, out var baseDir))
			throw new InvalidOperationException("Project not defined.");

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

		logger.LogInformation("Extracting uploaded zip to '{BaseDirectory}'.", baseDir);
		using var archive = ZipFile.OpenRead(zipPath);
		var normalizedBaseDir = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		foreach (var entry in archive.Entries)
		{
			var destinationPath = Path.GetFullPath(Path.Combine(normalizedBaseDir, entry.FullName));
			if (!destinationPath.StartsWith(normalizedBaseDir, StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("The upload contains an invalid path.");

			if (string.IsNullOrEmpty(entry.Name))
				Directory.CreateDirectory(destinationPath);
			else
			{
				Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
				if (File.Exists(destinationPath))
					File.SetAttributes(destinationPath, File.GetAttributes(destinationPath) & ~FileAttributes.ReadOnly);
				entry.ExtractToFile(destinationPath, overwrite: true);
			}
		}

		if (mirrorServerToLocal)
		{
			var synchronizedPaths = synchronizedFiles
				.Select(path => path.Replace('/', '\\'))
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			foreach (var existingFile in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories).ToList())
			{
				var relativePath = Path.GetRelativePath(baseDir, existingFile);
				if (!IsIgnoredPath(relativePath, ignoredFiles) && !synchronizedPaths.Contains(relativePath.Replace('/', '\\')))
				{
					File.SetAttributes(existingFile, File.GetAttributes(existingFile) & ~FileAttributes.ReadOnly);
					File.Delete(existingFile);
					logger.LogInformation("Mirror sync deleted extra server file '{FilePath}'.", relativePath);
				}
			}
			foreach (var existingDirectory in Directory.EnumerateDirectories(baseDir, "*", SearchOption.AllDirectories)
				.OrderByDescending(path => path.Length).ToList())
			{
				var relativePath = Path.GetRelativePath(baseDir, existingDirectory);
				if (!IsIgnoredPath(relativePath, ignoredFiles) && !Directory.EnumerateFileSystemEntries(existingDirectory).Any())
				{
					Directory.Delete(existingDirectory);
					logger.LogInformation("Mirror sync deleted extra server directory '{DirectoryPath}'.", relativePath);
				}
			}
		}

		if (enableBackup && !string.IsNullOrEmpty(backupDirBase))
		{
			var metadataPath = GetMetadataPath(backupDirBase, projectName);
			var metadata = new { Version = version, DeployDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), IsLockedStorage = true };
			await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata));
		}
	}
}
