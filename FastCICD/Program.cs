using System;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FastCICD;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var config = new ConfigurationBuilder()
	.SetBasePath(Directory.GetCurrentDirectory())
	.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
	.Build();

var settings = config.GetSection("DeployerSettings");
var endpoint = settings["ServerEndpoint"]!;
var apiKey = settings["SecurityKey"]!;
var projects = settings.GetSection("Projects").Get<List<ProjectConfig>>()!;

if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || projects == null || projects.Count == 0)
{
	AnsiConsole.MarkupLine("[bold white on red] ❌ CRITICAL ERROR: Invalid or missing configuration in appsettings.json. [/]");
	AnsiConsole.MarkupLine("[grey]Please ensure 'ServerEndpoint', 'SecurityKey', and at least one project are properly defined.[/]");
	Console.ReadKey();
	return;
}

var handler = new HmacDelegatingHandler(apiKey, new HttpClientHandler());
using var httpClient = new HttpClient(handler) { BaseAddress = new Uri(endpoint) };
httpClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
httpClient.Timeout = TimeSpan.FromMinutes(60);

while (true)
{
	// UI Setup: Welcome and Project Selection
	AnsiConsole.Clear();
	AnsiConsole.Write(new FigletText("Auto Deployer").Centered().Color(Color.Cyan1));

	var projectChoices = projects.Select(p => p.Name).ToList();
	projectChoices.Add("[red]Exit[/]");

	var selectedProjectName = AnsiConsole.Prompt(
		new SelectionPrompt<string>()
			.Title("[green]Select the project you want to deploy:[/]")
			.PageSize(10)
			.AddChoices(projectChoices)
	);

	if (selectedProjectName == "[red]Exit[/]")
	{
		AnsiConsole.MarkupLine("[yellow]Exiting Auto Deployer. Goodbye![/]");
		break;
	}

	var project = projects.First(p => p.Name == selectedProjectName);

	await HandleProjectMenuAsync(httpClient, project);


}

// --- Helper Methods ---
static async Task HandleProjectMenuAsync(HttpClient client, ProjectConfig project)
{
	bool backToMainMenu = false;

	while (!backToMainMenu)
	{
		AnsiConsole.Clear();
		AnsiConsole.Write(new Rule($"[cyan]Project:[/] [bold yellow]{project.Name}[/]").RuleStyle("grey").LeftJustified());

		AnsiConsole.WriteLine();

		var menuChoices = new List<string>
		{
			MenuOptions.Deploy,
			MenuOptions.CheckStatus,
			MenuOptions.CheckVersion,
			MenuOptions.StartServices,
			MenuOptions.StopServices
		};

		if (project.PreDeployCommands.Count > 0)
			menuChoices.Add(MenuOptions.RunPreDeploy);
		if (project.PostDeployCommands.Count > 0)
			menuChoices.Add(MenuOptions.RunPostDeploy);

		if (project.EnableRollback)
		{
			menuChoices.Add(MenuOptions.Rollback);
		}

		menuChoices.Add(MenuOptions.Back);

		var projectAction = AnsiConsole.Prompt(
			new SelectionPrompt<string>()
				.Title("[green]Select an action for this project:[/]")
				.AddChoices(menuChoices)
		);

		switch (projectAction)
		{
			case MenuOptions.Deploy:
				await ExecuteDeploymentPipelineAsync(client, project);
				break;

			case MenuOptions.CheckStatus:
				await CheckServicesStatusAsync(client, project.ServicesToManage);
				break;

			case MenuOptions.CheckVersion:
				await ShowCurrentVersionAsync(client, project.Name);
				break;

			case MenuOptions.StartServices:
				await ManageServicesAsync(client, project.ServicesToManage, "start");
				break;

			case MenuOptions.StopServices:
				await ManageServicesAsync(client, project.ServicesToManage, "stop");
				break;

			case MenuOptions.RunPreDeploy:
				await ManualExecuteCommandsAsync(client, project.Name, project.PreDeployCommands, "Pre-Deploy");
				break;

			case MenuOptions.RunPostDeploy:
				await ManualExecuteCommandsAsync(client, project.Name, project.PostDeployCommands, "Post-Deploy");
				break;

			case MenuOptions.Rollback:
				await HandleRollbackAsync(client, project);
				break;

			case MenuOptions.Back:
				backToMainMenu = true;
				break;
		}

		if (!backToMainMenu)
		{
			AnsiConsole.WriteLine();
			AnsiConsole.MarkupLine("[grey]Press any key to return to the project menu...[/]");
			Console.ReadKey(true);
		}
	}
}

static async Task ShowCurrentVersionAsync(HttpClient client, string projectName)
{
	try
	{
		var res = await client.GetAsync($"/api/version?projectName={projectName}");
		await res.EnsureSuccessWithDetailsAsync();

		var data = await res.Content.ReadFromJsonAsync<VersionResponse>();

		// Displaying the information in a clean panel
		var panel = new Panel(new Markup($"Current Version: [bold yellow]{data?.Version}[/]\nDeploy Date: [bold blue]{data?.DeployDate}[/]"))
		{
			Header = new PanelHeader("Deployment Metadata"),
			Border = BoxBorder.Rounded
		};
		AnsiConsole.Write(panel);
	}
	catch (Exception ex)
	{
		AnsiConsole.MarkupLine($"[red]Error fetching version:[/] {Markup.Escape(ex.Message)}");
	}
}

static async Task CheckServicesStatusAsync(HttpClient client, List<string> services)
{
	if (services.Count == 0)
	{
		AnsiConsole.MarkupLine("[yellow]No services configured for this project.[/]");
		return;
	}

	try
	{
		var res = await client.PostAsJsonAsync("/api/services", new { Services = services, Action = "status" });
		await res.EnsureSuccessWithDetailsAsync();

		var statuses = await res.Content.ReadFromJsonAsync<Dictionary<string, string>>();

		var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
		table.AddColumn("[cyan]Service Name[/]");
		table.AddColumn("[cyan]Status[/]");

		foreach (var stat in statuses!)
		{
			var statusColor = stat.Value == "Running" ? "green" : "red";
			table.AddRow($"[white]{stat.Key}[/]", $"[{statusColor}]{stat.Value}[/]");
		}

		AnsiConsole.Write(table);
	}
	catch (Exception ex)
	{
		AnsiConsole.MarkupLine($"[red]Error checking status:[/] {Markup.Escape(ex.Message)}");
	}
}

static async Task ManageServicesAsync(HttpClient client, List<string> services, string action)
{
	if (services.Count == 0)
	{
		AnsiConsole.MarkupLine("[yellow]No services configured for this project.[/]");
		return;
	}

	await AnsiConsole.Status().StartAsync($"[yellow]Executing '{action}' on services...[/]", async ctx =>
	{
		try
		{
			var res = await client.PostAsJsonAsync("/api/services", new { Services = services, Action = action });
			await res.EnsureSuccessWithDetailsAsync();
			AnsiConsole.MarkupLine($"[bold green]✓ Services successfully {action}ed.[/]");
		}
		catch (Exception ex)
		{
			AnsiConsole.MarkupLine($"[bold red]❌ Failed to {action} services:[/] {Markup.Escape(ex.Message)}");
		}
	});
}

static async Task HandleRollbackAsync(HttpClient client, ProjectConfig project)
{
	try
	{
		var res = await client.GetAsync($"/api/backups?projectName={project.Name}");
		await res.EnsureSuccessWithDetailsAsync();

		var backups = await res.Content.ReadFromJsonAsync<List<string>>();

		if (backups == null || backups.Count == 0)
		{
			AnsiConsole.MarkupLine("[yellow]No backups found for this project on the server.[/]");
			return;
		}

		backups.Add("[red]Cancel[/]");

		var selectedBackup = AnsiConsole.Prompt(
			new SelectionPrompt<string>()
				.Title("[green]Select a backup to restore:[/]")
				.PageSize(10)
				.AddChoices(backups)
		);

		if (selectedBackup == "[red]Cancel[/]")
			return;

		bool confirm = AnsiConsole.Confirm($"Are you sure you want to rollback to [bold yellow]{selectedBackup}[/]? This will overwrite current files.");
		if (!confirm)
			return;

		await AnsiConsole.Status().StartAsync("[yellow]Executing Rollback Pipeline...[/]", async ctx =>
		{
			if (project.ServicesToManage.Count != 0)
			{
				ctx.Status("[red]Stopping Services...[/]");
				await client.PostAsJsonAsync("/api/services", new { Services = project.ServicesToManage, Action = "stop" });
			}

			ctx.Status("[blue]Restoring Backup Files...[/]");
			var rollbackRes = await client.PostAsJsonAsync("/api/rollback", new { ProjectName = project.Name, BackupFileName = selectedBackup });
			await rollbackRes.EnsureSuccessWithDetailsAsync();

			if (project.ServicesToManage.Count != 0)
			{
				ctx.Status("[green]Restarting Services...[/]");
				await client.PostAsJsonAsync("/api/services", new { Services = project.ServicesToManage, Action = "start" });
			}

			AnsiConsole.WriteLine();
			AnsiConsole.MarkupLine("[bold green]⏪ Rollback Completed Successfully![/]");
		});
	}
	catch (Exception ex)
	{
		AnsiConsole.WriteLine();
		AnsiConsole.MarkupLine($"[bold red]❌ Rollback Failed:[/] {Markup.Escape(ex.Message)}");
	}
}

static async Task ExecuteDeploymentPipelineAsync(HttpClient httpClient, ProjectConfig project)
{
	if (!Directory.Exists(project.LocalSourcePath))
	{
		AnsiConsole.MarkupLine($"[bold red]❌ Deployment Aborted:[/] The local directory [yellow]'{project.LocalSourcePath}'[/] was not found.");
		AnsiConsole.MarkupLine("[grey]Please check your appsettings.json and ensure 'LocalSourcePath' is correct.[/]");
		return;
	}

	string version = ""; // Default empty version for when rollback is disabled

	if (project.EnableRollback)
	{
		// 1. Fetch current version before starting the deployment
		string currentVersion = "Unknown";
		try
		{
			var versionRes = await httpClient.GetAsync($"/api/version?projectName={project.Name}");
			if (versionRes.IsSuccessStatusCode)
			{
				var data = await versionRes.Content.ReadFromJsonAsync<VersionResponse>();
				currentVersion = data?.Version ?? "Unknown";
			}
		}
		catch
		{
			// Ignore error if it's the first deployment or drive is locked
		}

		AnsiConsole.WriteLine();
		AnsiConsole.MarkupLine($"[cyan]Current Server Version:[/] [bold yellow]{currentVersion}[/]");

		// 2. Ask for the new version BEFORE starting the UI spinner
		version = AnsiConsole.Ask<string>("[white]Enter [green]NEW[/] version label (e.g. 1.0.2):[/]");
	}
	else
	{
		AnsiConsole.WriteLine();
		AnsiConsole.MarkupLine("[grey]Versioning and Rollback are disabled for this project. Proceeding directly to deployment...[/]");
	}

	// 3. Start the deployment pipeline
	await AnsiConsole.Status()
		.Spinner(Spinner.Known.BouncingBar)
		.SpinnerStyle(Style.Parse("green"))
		.StartAsync("Initializing...", async ctx =>
		{
			bool servicesWereStopped = false;
			bool wasDeltaUploadedSuccessfully = false;

			try
			{
				if (project.PreDeployCommands.Count != 0)
				{
					ctx.Status("[magenta]Executing Pre-Deploy CLI Commands on server...[/]");
					await ExecuteRemoteCommandsAsync(httpClient, project.Name, project.PreDeployCommands);
					AnsiConsole.MarkupLine("[grey]✓ Pre-Deploy commands executed successfully.[/]");
				}

				ctx.Status("[blue]Calculating local file hashes (Multi-core)...[/]");
				var localFiles = GetLocalFileHashes(project.LocalSourcePath, project.IgnoredFiles);
				AnsiConsole.MarkupLine($"[grey]Found {localFiles.Count} files locally.[/]");

				if (project.ServicesToManage.Count != 0)
				{
					ctx.Status("[red]Stopping Windows Services on remote server...[/]");
					var stopRes = await httpClient.PostAsJsonAsync("/api/services",
						new { Services = project.ServicesToManage, Action = "stop" });
					await stopRes.EnsureSuccessWithDetailsAsync();

					servicesWereStopped = true;
					AnsiConsole.MarkupLine("[grey]Services stopped successfully.[/]");
				}

				ctx.Status("[blue]Comparing with server state...[/]");

				var response = await httpClient.PostAsJsonAsync("/api/compare",
					new { ProjectName = project.Name, FileHashes = localFiles });
				await response.EnsureSuccessWithDetailsAsync();

				var deltaFiles = await response.Content.ReadFromJsonAsync<List<string>>();

				if (deltaFiles == null || deltaFiles.Count == 0)
				{
					AnsiConsole.MarkupLine("[bold green]✓ Everything is up to date! No deployment needed.[/]");
					return;
				}
				AnsiConsole.MarkupLine($"[grey]Delta identified: {deltaFiles.Count} files need to be updated.[/]");

				// This acts as an eraser and completely overwrites the old "Comparing..." text.
				ctx.Status("[yellow]Zipping and uploading delta files...[/]");

				// Print an empty line to push the cursor down
				AnsiConsole.WriteLine();

				var table = new Table()
					.Border(TableBorder.Rounded)
					.BorderColor(Color.Grey)
					.AddColumn(new TableColumn("[cyan]Directory[/]"))
					.AddColumn(new TableColumn("[green]File Name[/]"));

				int maxDisplay = 100;
				var displayFiles = deltaFiles.Take(maxDisplay).ToList();

				foreach (var file in displayFiles)
				{
					var dir = Path.GetDirectoryName(file);
					var name = Path.GetFileName(file);

					table.AddRow(
						string.IsNullOrEmpty(dir) ? "[grey]/ (Root)[/]" : $"[white]{Markup.Escape(dir)}[/]",
						$"[yellow]{Markup.Escape(name)}[/]"
					);
				}

				if (deltaFiles.Count > maxDisplay)
				{
					table.AddRow("[grey]...[/]", $"[grey]... and {deltaFiles.Count - maxDisplay} more files hidden for performance.[/]");
				}

				AnsiConsole.Clear();

				// Draw the table cleanly above the new spinner text
				AnsiConsole.Write(table);

				// Pass the version variable here
				await UploadDeltaZipAsync(httpClient, project, deltaFiles, version, ctx);
				AnsiConsole.MarkupLine("[grey]Files uploaded and extracted successfully.[/]");

				wasDeltaUploadedSuccessfully = true;

				AnsiConsole.WriteLine();
				AnsiConsole.MarkupLine("[bold green]🚀 Deployment Completed Successfully![/]");
			}
			catch (Exception ex)
			{
				AnsiConsole.WriteLine();
				AnsiConsole.MarkupLine($"[bold red]❌ Deployment Failed:[/] {Markup.Escape(ex.Message)}");
			}
			finally
			{
				// 1. Evaluate and execute Post-Deploy commands safely
				bool shouldRunPostDeploy = project.PostDeployCommands.Count != 0 &&
										   (project.AlwaysRunPostDeployCommands || wasDeltaUploadedSuccessfully);

				if (shouldRunPostDeploy)
				{
					ctx.Status("[magenta]Executing Post-Deploy CLI Commands on server...[/]");
					try
					{
						await ExecuteRemoteCommandsAsync(httpClient, project.Name, project.PostDeployCommands);
						AnsiConsole.MarkupLine("[grey]✓ Post-Deploy commands executed successfully.[/]");
					}
					catch (Exception postEx)
					{
						// Wrap in a try-catch so it doesn't crash and block the service restart (Safety Net)
						AnsiConsole.MarkupLine($"[bold red]❌ Post-Deploy commands failed:[/] {Markup.Escape(postEx.Message)}");
					}
				}

				// 2. Execute Safety Net: Restarting Windows Services
				if (servicesWereStopped)
				{
					ctx.Status("[green]Executing Safety Net: Restarting Windows Services...[/]");
					try
					{
						var startRes = await httpClient.PostAsJsonAsync("/api/services",
							new { Services = project.ServicesToManage, Action = "start" });
						await startRes.EnsureSuccessWithDetailsAsync();
						AnsiConsole.MarkupLine("[grey]Services safely restarted.[/]");
					}
					catch (Exception finalEx)
					{
						AnsiConsole.MarkupLine($"[bold white on red] CRITICAL ERROR: Could not restart services. Manual intervention required! [/] {Markup.Escape(finalEx.Message)}");
					}
				}
			}
		});
}

static Dictionary<string, string> GetLocalFileHashes(string basePath, List<string> ignoredPaths)
{
	var hashes = new ConcurrentDictionary<string, string>();
	var files = Directory.GetFiles(basePath, "*.*", SearchOption.AllDirectories);

	Parallel.ForEach(files, file =>
	{
		var relativePath = Path.GetRelativePath(basePath, file);
		var normalizedRelativePath = relativePath.Replace('/', '\\');

		bool isIgnored = ignoredPaths.Any(ignored =>
		{
			var normalizedIgnored = ignored.Replace('/', '\\');

			return normalizedRelativePath.Equals(normalizedIgnored, StringComparison.OrdinalIgnoreCase) ||
							   normalizedRelativePath.StartsWith(normalizedIgnored + "\\", StringComparison.OrdinalIgnoreCase) ||
							   normalizedRelativePath.Contains("\\" + normalizedIgnored + "\\", StringComparison.OrdinalIgnoreCase) ||
							   normalizedRelativePath.EndsWith("\\" + normalizedIgnored, StringComparison.OrdinalIgnoreCase);
		});

		if (isIgnored)
			return;

		using var sha256 = SHA256.Create();
		using var stream = File.OpenRead(file);
		var hash = Convert.ToHexStringLower(sha256.ComputeHash(stream));

		hashes.TryAdd(relativePath, hash);
	});

	return hashes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
}

static async Task UploadDeltaZipAsync(HttpClient client, ProjectConfig project, List<string> deltaFiles, string version, StatusContext ctx)
{
	var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
	var zipPath = tempDir + ".zip";

	try
	{
		// 1. Update UI to show we are currently Zipping
		ctx.Status("[yellow]Zipping delta files locally...[/]");
		Directory.CreateDirectory(tempDir);

		foreach (var file in deltaFiles)
		{
			var sourceFile = Path.Combine(project.LocalSourcePath, file);
			var destFile = Path.Combine(tempDir, file);
			Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
			File.Copy(sourceFile, destFile);
		}

		ZipFile.CreateFromDirectory(tempDir, zipPath);

		// 2. Update UI to show we are switching to Upload mode
		ctx.Status("[blue]Starting upload process...[/]");

		using (var form = new MultipartFormDataContent())
		{
			using (var fileStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				// Wrap the stream with our custom progress tracker
				var progressContent = new ProgressStreamContent(fileStream, (uploaded, total) =>
				{
					int percent = total > 0 ? (int) ((double) uploaded / total * 100) : 0;

					// Convert bytes to Megabytes for better UX
					double uploadedMb = Math.Round((double) uploaded / 1048576, 2);
					double totalMb = Math.Round((double) total / 1048576, 2);

					// Dynamically update the spinner text with live progress
					ctx.Status($"[yellow]Uploading delta.zip... [bold green]{percent}%[/] ([blue]{uploadedMb} MB[/] / [blue]{totalMb} MB[/])[/]");
				});

				form.Add(progressContent, "file", "delta.zip");

				var response = await client.PostAsync($"/api/upload?projectName={Uri.EscapeDataString(project.Name)}&version={Uri.EscapeDataString(version)}&enableBackup={project.EnableRollback}", form);
				await response.EnsureSuccessWithDetailsAsync();
			}
		}
	}
	finally
	{
		if (Directory.Exists(tempDir))
		{
			try
			{
				// Remove ReadOnly attributes so Directory.Delete doesn't crash and mask real upload errors
				var di = new DirectoryInfo(tempDir);
				foreach (var info in di.GetFileSystemInfos("*", SearchOption.AllDirectories))
				{
					info.Attributes &= ~FileAttributes.ReadOnly;
				}
				Directory.Delete(tempDir, true);
			}
			catch
			{
				// We silently ignore cleanup errors so the REAL upload error is thrown to the UI
			}
		}

		if (File.Exists(zipPath))
		{
			File.Delete(zipPath);
		}
	}
}

static async Task ExecuteRemoteCommandsAsync(HttpClient client, string projectName, List<string> commands)
{
	var res = await client.PostAsJsonAsync("/api/execute", new { ProjectName = projectName, Commands = commands });

	if (!res.IsSuccessStatusCode)
	{
		var errorContent = await res.Content.ReadAsStringAsync();
		throw new Exception($"CLI Command execution failed: {errorContent}");
	}
}

static async Task ManualExecuteCommandsAsync(HttpClient client, string projectName, List<string> commands, string label)
{
	await AnsiConsole.Status()
		.Spinner(Spinner.Known.Dots)
		.SpinnerStyle(Style.Parse("magenta"))
		.StartAsync($"[yellow]Executing {label} commands manually...[/]", async ctx =>
		{
			try
			{
				await ExecuteRemoteCommandsAsync(client, projectName, commands);
				AnsiConsole.MarkupLine($"[bold green]✓ {label} commands executed successfully on the server.[/]");
			}
			catch (Exception ex)
			{
				AnsiConsole.MarkupLine($"[bold red]❌ Manual execution of {label} commands failed:[/] {Markup.Escape(ex.Message)}");
			}
		});
}
