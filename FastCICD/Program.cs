using System;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FastCICD;
using FastCICD.Models;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using Spectre.Console.Rendering;

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

var handler = new HmacDelegatingHandler(() => config["DeployerSettings:SecurityKey"] ?? "", new HttpClientHandler());
using var httpClient = new HttpClient(handler) { BaseAddress = new Uri(endpoint) };
httpClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
httpClient.Timeout = TimeSpan.FromMinutes(60);

while (true)
{
	// Pick up appsettings changes without restarting the client.
	settings = config.GetSection("DeployerSettings");
	endpoint = settings["ServerEndpoint"] ?? endpoint;
	projects = settings.GetSection("Projects").Get<List<ProjectConfig>>() ?? [];
	if (Uri.TryCreate(endpoint, UriKind.Absolute, out var refreshedEndpoint))
		httpClient.BaseAddress = refreshedEndpoint;
	var refreshedApiKey = settings["SecurityKey"];
	if (!string.IsNullOrWhiteSpace(refreshedApiKey))
	{
		httpClient.DefaultRequestHeaders.Remove("X-Api-Key");
		httpClient.DefaultRequestHeaders.Add("X-Api-Key", refreshedApiKey);
	}

	if (projects.Count == 0)
	{
		AnsiConsole.MarkupLine("[bold white on red] ❌ No deployer projects are configured. [/] ");
		await Task.Delay(1500);
		continue;
	}

	// UI Setup: Welcome and Project Selection
	AnsiConsole.Clear();
	AnsiConsole.Write(new FigletText("Auto Deployer").Centered().Color(Color.Cyan1));

	var project = SelectProject(projects);
	if (project == null)
	{
		AnsiConsole.MarkupLine("[yellow]Exiting Auto Deployer. Goodbye![/]");
		break;
	}

	await HandleProjectMenuAsync(httpClient, project);


}

// --- Helper Methods ---
static ProjectConfig? SelectProject(List<ProjectConfig> projects)
{
	var groups = projects
		.Where(project => !string.IsNullOrWhiteSpace(project.Group))
		.GroupBy(project => project.Group!.Trim(), StringComparer.OrdinalIgnoreCase)
		.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
		.ToList();
	var ungroupedProjects = projects
		.Where(project => string.IsNullOrWhiteSpace(project.Group))
		.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
		.ToList();

	if (groups.Count == 0)
		return SelectProjectFromList(projects, "[green]Select the project you want to deploy:[/]");

	var groupChoices = new Dictionary<string, List<ProjectConfig>>();
	var mainChoices = new List<string>();
	foreach (var group in groups)
	{
		var choice = $"[cyan]▸ {Markup.Escape(group.Key)}[/]";
		groupChoices.Add(choice, group.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase).ToList());
		mainChoices.Add(choice);
	}

	var projectChoices = new Dictionary<string, ProjectConfig>();
	foreach (var project in ungroupedProjects)
	{
		var choice = $"[white]{Markup.Escape(project.Name)}[/]";
		projectChoices.Add(choice, project);
		mainChoices.Add(choice);
	}

	const string exitChoice = "[red]Exit[/]";
	mainChoices.Add(exitChoice);
	var selected = AnsiConsole.Prompt(
		new SelectionPrompt<string>()
			.Title("[green]Select a site or project:[/]")
			.PageSize(12)
			.AddChoices(mainChoices));

	if (selected == exitChoice)
		return null;
	if (projectChoices.TryGetValue(selected, out var directProject))
		return directProject;

	var selectedGroup = groupChoices[selected];
	return SelectProjectFromList(selectedGroup, $"[green]Select an item from[/] [cyan]{Markup.Escape(selectedGroup[0].Group!)}[/]:", "[grey]← Back[/]")
		?? SelectProject(projects);
}

static ProjectConfig? SelectProjectFromList(List<ProjectConfig> projects, string title, string cancelChoice = "[red]Exit[/]")
{
	var projectChoices = new Dictionary<string, ProjectConfig>();
	foreach (var project in projects.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase))
	{
		var choice = $"[white]{Markup.Escape(project.Name)}[/]";
		projectChoices.Add(choice, project);
	}

	projectChoices.Add(cancelChoice, null!);
	var selected = AnsiConsole.Prompt(
		new SelectionPrompt<string>()
			.Title(title)
			.PageSize(12)
			.AddChoices(projectChoices.Keys));

	return projectChoices[selected];
}

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

		if (project.LocalPreDeployCommands.Count > 0)
			menuChoices.Add(MenuOptions.RunLocalPreDeploy);
		if (project.LocalPostDeployCommands.Count > 0)
			menuChoices.Add(MenuOptions.RunLocalPostDeploy);

		if (project.EnableRollback)
		{
			menuChoices.Add(MenuOptions.Rollback);
		}
		if (!string.IsNullOrWhiteSpace(project.MigrationProfile))
			menuChoices.Add(MenuOptions.ManageMigrations);

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

			case MenuOptions.RunLocalPreDeploy:
				await ManualExecuteLocalCommandsAsync(project.LocalPreDeployCommands, "Local Pre-Deploy");
				break;

			case MenuOptions.RunLocalPostDeploy:
				await ManualExecuteLocalCommandsAsync(project.LocalPostDeployCommands, "Local Post-Deploy");
				break;

			case MenuOptions.Rollback:
				await HandleRollbackAsync(client, project);
				break;

			case MenuOptions.ManageMigrations:
				await HandleMigrationManagerAsync(client, project);
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
		var res = await client.GetAsync($"api/version?projectName={projectName}");
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
		var res = await client.PostAsJsonAsync("api/services", new { Services = services, Action = "status" });
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
			var res = await client.PostAsJsonAsync("api/services", new { Services = services, Action = action });
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
		var res = await client.GetAsync($"api/backups?projectName={project.Name}");
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
				await client.PostAsJsonAsync("api/services", new { Services = project.ServicesToManage, Action = "stop" });
			}

			ctx.Status("[blue]Restoring Backup Files...[/]");
			var rollbackRes = await client.PostAsJsonAsync("api/rollback", new { ProjectName = project.Name, BackupFileName = selectedBackup });
			await rollbackRes.EnsureSuccessWithDetailsAsync();

			if (project.ServicesToManage.Count != 0)
			{
				ctx.Status("[green]Restarting Services...[/]");
				await client.PostAsJsonAsync("api/services", new { Services = project.ServicesToManage, Action = "start" });
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
	string version = ""; // Default empty version for when rollback is disabled

	if (project.EnableRollback)
	{
		// 1. Fetch current version before starting the deployment
		string currentVersion = "Unknown";
		try
		{
			var versionRes = await httpClient.GetAsync($"api/version?projectName={project.Name}");
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
	var dashboard = new DeploymentDashboard();
	var finalResult = "[bold red]❌ Deployment ended before a result was recorded.[/]";
	await AnsiConsole.Live(dashboard.Render())
		.AutoClear(true)
		.StartAsync(async live =>
		{
			var renderLock = new Lock();
			using var refreshCts = new CancellationTokenSource();
			var refreshTask = Task.Run(async () =>
			{
				while (!refreshCts.IsCancellationRequested)
				{
					lock (renderLock)
						live.UpdateTarget(dashboard.Render(advanceSpinner: true));
					await Task.Delay(80, refreshCts.Token).ConfigureAwait(false);
				}
			});

			Action<string> status = text =>
			{
				lock (renderLock)
				{
					dashboard.SetStage(text);
					live.UpdateTarget(dashboard.Render());
				}
			};
			Action<string> commandOutput = text =>
			{
				lock (renderLock)
				{
					dashboard.AddCommandOutput(text);
					live.UpdateTarget(dashboard.Render());
				}
			};
			Action clearCommandOutput = () =>
			{
				lock (renderLock)
				{
					dashboard.ClearCommandOutput();
					live.UpdateTarget(dashboard.Render());
				}
			};
			bool servicesWereStopped = false;
			bool wasDeltaUploadedSuccessfully = false;

			try
			{
				if (project.LocalPreDeployCommands.Count != 0)
				{
					status("[magenta]Executing LOCAL Pre-Deploy commands...[/]");
					await ExecuteLocalCommandsAsync(project.LocalPreDeployCommands, "Local Pre-Deploy", status, commandOutput, clearCommandOutput);
					status("[grey]✓ Local Pre-Deploy commands executed successfully.[/]");
				}

				if (!Directory.Exists(project.LocalSourcePath))
				{
					throw new Exception($"The local directory '{project.LocalSourcePath}' was not found. Did your publish command output to the correct path?");
				}

				if (project.PreDeployCommands.Count != 0)
				{
					status("[magenta]Executing Pre-Deploy CLI Commands on server...[/]");
					await ExecuteRemoteCommandsAsync(httpClient, project.Name, project.PreDeployCommands, status);
					status("[grey]✓ Pre-Deploy commands executed successfully.[/]");
				}

				status("[blue]Calculating local file hashes (Multi-core)...[/]");
				var localFiles = GetLocalFileHashes(project.LocalSourcePath, project.IgnoredFiles);
				status($"[grey]Found {localFiles.Count} files locally.[/]");

				if (project.ServicesToManage.Count != 0)
				{
					status("[red]Stopping Windows Services on remote server...[/]");
					var stopRes = await httpClient.PostAsJsonAsync("api/services",
						new { Services = project.ServicesToManage, Action = "stop" });
					await stopRes.EnsureSuccessWithDetailsAsync();

					servicesWereStopped = true;
					status("[grey]Services stopped successfully.[/]");
				}

				status("[blue]Comparing with server state...[/]");

				var response = await httpClient.PostAsJsonAsync("api/compare",
					new { ProjectName = project.Name, FileHashes = localFiles, IgnoredFiles = project.IgnoredFiles, MirrorServerToLocal = project.MirrorServerToLocal });
				await response.EnsureSuccessWithDetailsAsync();

				var compareResult = await response.Content.ReadFromJsonAsync<CompareResponse>();
				var deltaFiles = compareResult?.DeltaFiles ?? [];
				var extraFileCount = compareResult?.ExtraFileCount ?? 0;

				if (deltaFiles.Count == 0 && extraFileCount == 0)
				{
					status("[bold green]✓ Everything is up to date! No deployment needed.[/]");
					finalResult = "[bold yellow]↔ No deployment was needed; the server is already up to date.[/]";
					return;
				}
				status($"[grey]Delta identified: {deltaFiles.Count} local files need uploading and {extraFileCount} extra server files will be deleted.[/]");

				status("[yellow]Zipping and uploading delta files...[/]");

				var deltaFileRows = deltaFiles
					.Select(file =>
					{
						var dir = Path.GetDirectoryName(file);
						var name = Path.GetFileName(file);
						return (
							Directory: string.IsNullOrEmpty(dir) ? "[grey]/ (Root)[/]" : $"[white]{Markup.Escape(dir)}[/]",
							FileName: $"[yellow]{Markup.Escape(name)}[/]"
						);
					})
					.ToList();

				lock (renderLock)
				{
					dashboard.SetDeltaFiles(deltaFileRows);
					live.UpdateTarget(dashboard.Render());
				}
				status($"[grey]Preparing {deltaFiles.Count} changed files for upload...[/]");

				await UploadDeltaZipAsync(httpClient, project, deltaFiles, compareResult?.SyncManifestId, version, status, (percent, text) =>
				{
					lock (renderLock)
					{
						dashboard.SetUploadProgress(percent, text);
						live.UpdateTarget(dashboard.Render());
					}
				});
				status("[grey]Files uploaded and extracted successfully.[/]");

				wasDeltaUploadedSuccessfully = true;

				status("[bold green]🚀 Deployment Completed Successfully![/]");
				finalResult = "[bold green]✓ Deployment completed successfully.[/]";
			}
			catch (Exception ex)
			{
				status($"[bold red]❌ Deployment Failed:[/] {Markup.Escape(ex.Message)}");
				finalResult = $"[bold red]❌ Deployment failed:[/] {Markup.Escape(ex.Message)}";
			}
			finally
			{
				try
				{
				// Local Post-Deploy commands
				bool shouldRunLocalPostDeploy = project.LocalPostDeployCommands.Count != 0 &&
												(project.AlwaysRunPostDeployCommands || wasDeltaUploadedSuccessfully);

				if (shouldRunLocalPostDeploy)
				{
					status("[magenta]Executing LOCAL Post-Deploy commands...[/]");
					try
					{
						await ExecuteLocalCommandsAsync(project.LocalPostDeployCommands, "Local Post-Deploy", status, commandOutput, clearCommandOutput);
						status("[grey]✓ Local Post-Deploy commands executed successfully.[/]");
					}
					catch (Exception localEx)
					{
						status($"[bold red]❌ Local Post-Deploy commands failed:[/] {Markup.Escape(localEx.Message)}");
					}
				}

				// Evaluate and execute Post-Deploy commands safely
				bool shouldRunPostDeploy = project.PostDeployCommands.Count != 0 &&
										   (project.AlwaysRunPostDeployCommands || wasDeltaUploadedSuccessfully);

				if (shouldRunPostDeploy)
				{
					status("[magenta]Executing Post-Deploy CLI Commands on server...[/]");
					try
					{
						await ExecuteRemoteCommandsAsync(httpClient, project.Name, project.PostDeployCommands, status);
						status("[grey]✓ Post-Deploy commands executed successfully.[/]");
					}
					catch (Exception postEx)
					{
						// Wrap in a try-catch so it doesn't crash and block the service restart (Safety Net)
						status($"[bold red]❌ Post-Deploy commands failed:[/] {Markup.Escape(postEx.Message)}");
					}
				}

				// 2. Execute Safety Net: Restarting Windows Services
				if (servicesWereStopped)
				{
					status("[green]Executing Safety Net: Restarting Windows Services...[/]");
					try
					{
						var startRes = await httpClient.PostAsJsonAsync("api/services",
							new { Services = project.ServicesToManage, Action = "start" });
						await startRes.EnsureSuccessWithDetailsAsync();
						status("[grey]Services safely restarted.[/]");
					}
					catch (Exception finalEx)
					{
						status($"[bold white on red] CRITICAL ERROR: Could not restart services. Manual intervention required! [/] {Markup.Escape(finalEx.Message)}");
						finalResult = $"[bold red]❌ Deployment requires manual intervention:[/] services could not be restarted. {Markup.Escape(finalEx.Message)}";
					}
				}
				}
			finally
			{
				refreshCts.Cancel();
				try
				{
					await refreshTask;
				}
				catch (OperationCanceledException)
				{
				}
			}
			}
		});

	AnsiConsole.Write(new Panel(new Markup(finalResult))
		.Header("[bold cyan]Deployment result[/]")
		.Border(BoxBorder.Rounded));
}

static async Task HandleMigrationManagerAsync(HttpClient client, ProjectConfig project)
{
	if (string.IsNullOrWhiteSpace(project.MigrationProfile) || string.IsNullOrWhiteSpace(project.MigrationExecutionKey) || string.IsNullOrWhiteSpace(project.MigrationScriptPath))
	{
		AnsiConsole.MarkupLine("[bold red]Migration Manager is not configured for this project.[/]");
		return;
	}
	if (!File.Exists(project.MigrationScriptPath))
	{
		AnsiConsole.MarkupLine($"[bold red]Migration script was not found:[/] {Markup.Escape(project.MigrationScriptPath)}");
		return;
	}

	try
	{
		var localHash = await ComputeFileHashAsync(project.MigrationScriptPath);
		MigrationStatusResponse? status = null;
		try { status = await GetMigrationStatusAsync(client, project); }
		catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound) { }

		if (status is null || !string.Equals(status.ScriptSha256, localHash, StringComparison.OrdinalIgnoreCase))
		{
			AnsiConsole.MarkupLine(status is null
				? "[yellow]No migration script is stored for this profile on the server.[/]"
				: "[yellow]The server has a different migration script. It will not be applied until you upload this local version.[/]");
			if (!AnsiConsole.Confirm("[yellow]Upload the local migration script to the remote server?[/]", false)) return;
			var uploadConfirmation = AnsiConsole.Ask<string>($"Type [bold yellow]{Markup.Escape(project.MigrationProfile)}[/] to authorize script upload:");
			if (!string.Equals(uploadConfirmation, project.MigrationProfile, StringComparison.Ordinal))
			{
				AnsiConsole.MarkupLine("[yellow]Migration script upload cancelled.[/]");
				return;
			}
			var upload = await UploadMigrationScriptAsync(client, project, localHash);
			AnsiConsole.MarkupLine($"[green]✓ Uploaded {upload.MigrationCount} migration(s). Hash: {upload.ScriptSha256[..12]}…[/]");
			status = await GetMigrationStatusAsync(client, project);
		}

		RenderMigrationStatus(status);
		var pending = status.Migrations.Where(migration => !migration.IsApplied).ToList();
		if (pending.Count == 0)
		{
			AnsiConsole.MarkupLine("[bold green]✓ The configured database is up to date.[/]");
			return;
		}

		if (!AnsiConsole.Confirm($"[yellow]Apply {pending.Count} pending migration(s) on the remote server?[/]", false))
			return;
		var confirmation = AnsiConsole.Ask<string>($"Type [bold yellow]{Markup.Escape(project.MigrationProfile)}[/] to confirm:");
		if (!string.Equals(confirmation, project.MigrationProfile, StringComparison.Ordinal))
		{
			AnsiConsole.MarkupLine("[yellow]Migration execution cancelled.[/]");
			return;
		}

		using var request = CreateMigrationRequest(HttpMethod.Post, $"api/migrations/{Uri.EscapeDataString(project.MigrationProfile)}/apply", project, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
		using var response = await client.SendAsync(request);
		await response.EnsureSuccessWithDetailsAsync();
		var result = await response.Content.ReadFromJsonAsync<ApplyMigrationsResponse>()
			?? throw new InvalidOperationException("The server returned an invalid migration result.");
		if (result.AppliedMigrationIds.Count == 0)
			AnsiConsole.MarkupLine("[green]No pending migrations remained.[/]");
		else
			AnsiConsole.MarkupLine($"[bold green]✓ Applied {result.AppliedMigrationIds.Count} migration(s) on the server.[/]");
	}
	catch (Exception exception)
	{
		AnsiConsole.MarkupLine($"[bold red]Migration Manager failed:[/] {Markup.Escape(exception.Message)}");
	}
}

static async Task<MigrationStatusResponse> GetMigrationStatusAsync(HttpClient client, ProjectConfig project)
{
	using var request = CreateMigrationRequest(HttpMethod.Get, $"api/migrations/{Uri.EscapeDataString(project.MigrationProfile!)}", project, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
	using var response = await client.SendAsync(request);
	await response.EnsureSuccessWithDetailsAsync();
	return await response.Content.ReadFromJsonAsync<MigrationStatusResponse>()
		?? throw new InvalidOperationException("The server returned an invalid migration status.");
}

static async Task<MigrationScriptUploadResponse> UploadMigrationScriptAsync(HttpClient client, ProjectConfig project, string scriptHash)
{
	await using var source = new FileStream(project.MigrationScriptPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
	using var request = CreateMigrationRequest(HttpMethod.Put, $"api/migrations/{Uri.EscapeDataString(project.MigrationProfile!)}/script", project, scriptHash);
	request.Content = new StreamContent(source);
	request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/sql");
	request.Content.Headers.ContentLength = source.Length;
	using var response = await client.SendAsync(request);
	await response.EnsureSuccessWithDetailsAsync();
	return await response.Content.ReadFromJsonAsync<MigrationScriptUploadResponse>()
		?? throw new InvalidOperationException("The server returned an invalid migration upload result.");
}

static HttpRequestMessage CreateMigrationRequest(HttpMethod method, string route, ProjectConfig project, string bodySha256)
{
	var request = new HttpRequestMessage(method, route);
	var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
	var nonce = Guid.NewGuid().ToString("D");
	var canonical = string.Join("\n", timestamp, nonce, method.Method, "/" + route.TrimStart('/'), bodySha256);
	using var hmac = new HMACSHA256(System.Text.Encoding.UTF8.GetBytes(project.MigrationExecutionKey!));
	var signature = Convert.ToBase64String(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canonical)));
	request.Headers.Add("X-Migration-Key", project.MigrationExecutionKey!);
	request.Headers.Add("X-Migration-Timestamp", timestamp);
	request.Headers.Add("X-Migration-Nonce", nonce);
	request.Headers.Add("X-Migration-Signature", signature);
	request.Headers.Add("X-Migration-Content-Sha256", bodySha256);
	return request;
}

static void RenderMigrationStatus(MigrationStatusResponse status)
{
	var table = new Table().Border(TableBorder.Rounded).Title($"[cyan]Migration profile: {Markup.Escape(status.ProfileName)}[/]");
	table.AddColumn("Migration ID");
	table.AddColumn("Status");
	foreach (var migration in status.Migrations)
		table.AddRow(Markup.Escape(migration.MigrationId), migration.IsApplied ? "[green]Applied[/]" : "[yellow]Pending[/]");
	AnsiConsole.Write(table);
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

static async Task UploadDeltaZipAsync(HttpClient client, ProjectConfig project, List<string> deltaFiles, string? syncManifestId, string version, Action<string> status, Action<int, string> uploadProgress)
{
	var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
	var zipPath = tempDir + ".zip";
	var preserveUploadArtifact = false;

	try
	{
		// 1. Update UI to show we are currently Zipping
		status("[yellow]Zipping delta files locally...[/]");
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
		status("[blue]Starting upload process...[/]");

		try
		{
			await UploadResumableAsync(client, project, syncManifestId, version, zipPath, status, uploadProgress);
		}
		catch
		{
			preserveUploadArtifact = true;
			throw;
		}
	}
	finally
	{
		if (preserveUploadArtifact)
		{
			status("[yellow]Upload paused. The local upload artifact is preserved for a future resume.[/]");
		}
		else if (Directory.Exists(tempDir))
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

		if (!preserveUploadArtifact && File.Exists(zipPath))
		{
			File.Delete(zipPath);
		}
	}
}

static async Task ExecuteRemoteCommandsAsync(HttpClient client, string projectName, List<string> commands, Action<string>? status = null)
{
	using var request = new HttpRequestMessage(HttpMethod.Post, "api/execute")
	{
		Content = JsonContent.Create(new { ProjectName = projectName, Commands = commands })
	};
	using var res = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

	await res.EnsureSuccessWithDetailsAsync();
	using var stream = await res.Content.ReadAsStreamAsync();
	using var reader = new StreamReader(stream);
	while (await reader.ReadLineAsync() is { } line)
	{
		if (string.IsNullOrWhiteSpace(line))
			continue;

		using var eventJson = JsonDocument.Parse(line);
		var root = eventJson.RootElement;
		var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : "";
		var message = root.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
		var command = root.TryGetProperty("command", out var commandValue) ? commandValue.GetString() : null;
		var display = type switch
		{
			"started" => $"Starting remote script: {command}",
			"output" => $"OUT: {message}",
			"error" => $"ERR: {message}",
			"completed" => $"Completed remote script: {command}",
			_ => message
		};
		if (!string.IsNullOrWhiteSpace(display))
		{
			if (status != null)
				status($"[magenta]{Markup.Escape(display!)}[/]");
			else
				AnsiConsole.MarkupLine($"[grey]{Markup.Escape(display!)}[/]");
		}
		if (type == "error")
			throw new Exception($"CLI command execution failed: {message}");
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
				await ExecuteRemoteCommandsAsync(client, projectName, commands, message => ctx.Status(message));
				AnsiConsole.MarkupLine($"[bold green]✓ {label} commands executed successfully on the server.[/]");
			}
			catch (Exception ex)
			{
				AnsiConsole.MarkupLine($"[bold red]❌ Manual execution of {label} commands failed:[/] {Markup.Escape(ex.Message)}");
			}
		});
}

static async Task ExecuteLocalCommandsAsync(List<LocalCommandConfig> commands, string label, Action<string>? status = null, Action<string>? commandOutput = null, Action? clearAfterCommand = null)
{
	int index = 0;
	foreach (var cmd in commands)
	{
		index++;
		var statusText = $"> {Markup.Escape(cmd.Command)}";
		status?.Invoke(statusText);
		if (status == null)
			AnsiConsole.MarkupLine($"[grey]→ Running:[/] [white]{Markup.Escape(cmd.Command)}[/]");

			var psi = new System.Diagnostics.ProcessStartInfo
			{
				FileName = "cmd.exe",
				Arguments = $"/c {cmd.Command}",
				WorkingDirectory = string.IsNullOrWhiteSpace(cmd.WorkingDirectory)
					? Directory.GetCurrentDirectory()
					: cmd.WorkingDirectory,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};

			using var process = System.Diagnostics.Process.Start(psi)
				?? throw new Exception($"Could not start local command: '{cmd.Command}'");

			var output = new List<string>();
			var errorOutput = new List<string>();
		var stdOutTask = ReadProcessOutputAsync(process.StandardOutput, line =>
			{
				output.Add(line);
				ShowLocalProcessLog(status, commandOutput, line);
			});
		var stdErrTask = ReadProcessOutputAsync(process.StandardError, line =>
			{
				errorOutput.Add(line);
				ShowLocalProcessLog(status, commandOutput, line, isError: true);
			});
			await Task.WhenAll(stdOutTask, stdErrTask, process.WaitForExitAsync());

			if (process.ExitCode != 0)
			{
				var details = errorOutput.Count == 0 ? output : errorOutput;
				var lastLines = string.Join("\n", details.TakeLast(15));
				throw new Exception($"Local command #{index} failed (ExitCode {process.ExitCode}): '{cmd.Command}'\n--- Output ---\n{lastLines}");
			}

			if (status == null)
				AnsiConsole.MarkupLine($"[grey]  ✓ ({index}/{commands.Count}) Done.[/]");
			clearAfterCommand?.Invoke();
		}
}

static void ShowLocalProcessLog(Action<string>? status, Action<string>? commandOutput, string line, bool isError = false)
{
	if (string.IsNullOrWhiteSpace(line))
		return;

	var text = line.Length > 400 ? line[..400] + "..." : line;
	var markup = Markup.Escape(text);
	if (commandOutput != null)
		commandOutput(markup);
	else if (status != null)
		status(markup);
	else
		AnsiConsole.MarkupLine(markup);
}

static async Task ManualExecuteLocalCommandsAsync(List<LocalCommandConfig> commands, string label)
{
	try
		{
		await ExecuteLocalCommandsAsync(commands, label);
		AnsiConsole.MarkupLine($"[bold green]✓ {label} commands executed successfully.[/]");
	}
	catch (Exception ex)
	{
		AnsiConsole.MarkupLine($"[bold red]❌ {label} commands failed:[/] {Markup.Escape(ex.Message)}");
	}
}

static async Task UploadResumableAsync(HttpClient client, ProjectConfig project, string? syncManifestId, string version, string zipPath, Action<string> uploadStatus, Action<int, string> uploadProgress)
{
	const int chunkSize = 512 * 1024;
	var totalBytes = new FileInfo(zipPath).Length;
	var fileHash = await ComputeFileHashAsync(zipPath);
	var manifestPath = GetPendingUploadManifestPath(project, version, fileHash, chunkSize);
	var uploadZipPath = zipPath;
	PendingUploadManifest? manifest = await LoadPendingUploadManifestAsync(manifestPath);
	UploadSessionResponse? session = null;

	if (manifest != null && File.Exists(manifest.ZipPath))
	{
	uploadStatus("[yellow]Resuming previous upload session...[/]");
		using var existingStatusResponse = await client.GetAsync($"api/upload/sessions/{manifest.UploadId}");
		if (existingStatusResponse.IsSuccessStatusCode)
		{
			var existingStatus = await existingStatusResponse.Content.ReadFromJsonAsync<UploadSessionStatusResponse>();
			if (existingStatus != null && existingStatus.TotalBytes == new FileInfo(manifest.ZipPath).Length &&
				string.Equals(await ComputeFileHashAsync(manifest.ZipPath), fileHash, StringComparison.OrdinalIgnoreCase))
			{
				session = new UploadSessionResponse(manifest.UploadId, existingStatus.ChunkSize, existingStatus.TotalChunks);
				uploadZipPath = manifest.ZipPath;
				uploadStatus($"[cyan]Resuming upload session:[/] [yellow]{existingStatus.UploadedChunks.Length}/{existingStatus.TotalChunks} chunks already uploaded.[/]");
			}
		}
	}

	if (session == null)
	{
		if (manifest != null)
			File.Delete(manifestPath);

		using var sessionResponse = await client.PostAsJsonAsync("api/upload/sessions", new CreateUploadSessionRequest(
			project.Name, version, project.EnableRollback, project.MirrorServerToLocal, [], [], syncManifestId, totalBytes, chunkSize, fileHash));
		LogUploadDiagnostic($"Session create response. Project={project.Name}; TotalBytes={totalBytes}; ChunkSize={chunkSize}; Status={(int)sessionResponse.StatusCode} {sessionResponse.StatusCode}");
		await sessionResponse.EnsureSuccessWithDetailsAsync();
		session = await sessionResponse.Content.ReadFromJsonAsync<UploadSessionResponse>()
			?? throw new InvalidOperationException("The server did not return an upload session.");
		manifest = new PendingUploadManifest
		{
			UploadId = session.UploadId,
			ProjectName = project.Name,
			Version = version,
			EnableBackup = project.EnableRollback,
			MirrorServerToLocal = project.MirrorServerToLocal,
			FileHash = fileHash,
			ZipPath = zipPath
		};
		await SavePendingUploadManifestAsync(manifestPath, manifest);
		uploadStatus($"[cyan]Started new upload session:[/] [yellow]0/{session.TotalChunks} chunks uploaded.[/]");
	}

	using var statusResponse = await client.GetAsync($"api/upload/sessions/{session.UploadId}");
	LogUploadDiagnostic($"Session status response. UploadId={session.UploadId}; Status={(int)statusResponse.StatusCode} {statusResponse.StatusCode}");
	await statusResponse.EnsureSuccessWithDetailsAsync();
	var status = await statusResponse.Content.ReadFromJsonAsync<UploadSessionStatusResponse>()
		?? throw new InvalidOperationException("The server did not return upload status.");

	var completedChunks = status.UploadedChunks.ToHashSet();
	long completedBytes = completedChunks.Sum(index => Math.Min((long)session.ChunkSize, totalBytes - (long)index * session.ChunkSize));
	var totalChunks = session.TotalChunks;
	var lastSpeedUpdate = Stopwatch.GetTimestamp();
	long lastSpeedBytes = completedBytes;
	double smoothedBytesPerSecond = 0;

	for (var chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
	{
		if (completedChunks.Contains(chunkIndex))
			continue;

		var offset = (long)chunkIndex * session.ChunkSize;
		var length = Math.Min((long)session.ChunkSize, totalBytes - offset);
		var uploadedBeforeChunk = completedBytes;
		var sent = false;

		for (var attempt = 1; attempt <= 5 && !sent; attempt++)
		{
			try
			{
				uploadStatus($"Uploading chunk {chunkIndex + 1}/{totalChunks} ({completedChunks.Count}/{totalChunks} completed)...");
				using var fileStream = new FileStream(uploadZipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
				fileStream.Position = offset;
				var lastProgressUpdate = Stopwatch.GetTimestamp();
				using var content = new StreamContent(new BoundedReadStream(fileStream, length, chunkRead =>
				{
					if (chunkRead != length && Stopwatch.GetElapsedTime(lastProgressUpdate) < TimeSpan.FromMilliseconds(250))
						return;
					lastProgressUpdate = Stopwatch.GetTimestamp();
					var uploaded = uploadedBeforeChunk + chunkRead;
					var percent = totalBytes > 0 ? (int)((double)uploaded / totalBytes * 100) : 0;
					var elapsed = Stopwatch.GetElapsedTime(lastSpeedUpdate).TotalSeconds;
					if (elapsed > 0)
					{
						var instantBytesPerSecond = (uploaded - lastSpeedBytes) / elapsed;
						smoothedBytesPerSecond = smoothedBytesPerSecond <= 0
							? instantBytesPerSecond
							: (smoothedBytesPerSecond * 0.7) + (instantBytesPerSecond * 0.3);
						lastSpeedBytes = uploaded;
						lastSpeedUpdate = Stopwatch.GetTimestamp();
					}

					var speedMegabytes = smoothedBytesPerSecond / 1048576d;
					var speedMegabits = smoothedBytesPerSecond * 8 / 1000000d;
					uploadProgress(percent, $"Uploading delta.zip... {percent}% ({uploaded / 1048576d:F2} MB / {totalBytes / 1048576d:F2} MB) {speedMegabytes:F2} MB/s ({speedMegabits:F2} Mbps)");
				}));
				content.Headers.ContentLength = length;

				using var request = new HttpRequestMessage(HttpMethod.Post, $"api/upload/sessions/{session.UploadId}/chunks/{chunkIndex}")
				{
					Content = content
				};
				var requestId = Guid.NewGuid().ToString("N");
				request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
				LogUploadDiagnostic($"Chunk sending. RequestId={requestId}; UploadId={session.UploadId}; ChunkIndex={chunkIndex}; Offset={offset}; Length={length}; Attempt={attempt}");
				using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
				var responseBody = await response.Content.ReadAsStringAsync();
				LogUploadDiagnostic($"Chunk response. RequestId={requestId}; UploadId={session.UploadId}; ChunkIndex={chunkIndex}; Attempt={attempt}; Status={(int)response.StatusCode} {response.StatusCode}; Headers={FormatDiagnosticResponseHeaders(response)}; Body={TruncateUploadDiagnostic(responseBody)}");
				if (!response.IsSuccessStatusCode)
					throw new Exception($"HTTP {(int)response.StatusCode}: {responseBody}");
				sent = true;
				completedBytes += length;
				completedChunks.Add(chunkIndex);
				uploadStatus($"Chunk {chunkIndex + 1}/{totalChunks} completed ({completedChunks.Count}/{totalChunks}).");
			}
			catch (Exception ex) when (attempt < 5)
			{
				LogUploadDiagnostic($"Chunk failed. UploadId={session.UploadId}; ChunkIndex={chunkIndex}; Attempt={attempt}; ExceptionType={ex.GetType().Name}; Message={TruncateUploadDiagnostic(ex.ToString())}");
				uploadStatus($"Chunk {chunkIndex + 1} failed: {Markup.Escape(ex.Message)}; retrying ({attempt}/4)...");
				await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))), CancellationToken.None);
				if (ex is OperationCanceledException)
					throw;
			}
		}

		if (!sent)
			throw new IOException($"Chunk {chunkIndex + 1} could not be uploaded after 5 attempts.");
	}

	uploadStatus("[yellow]Upload complete. Verifying and deploying on server...[/]");
	using var completeResponse = await client.PostAsync($"api/upload/sessions/{session.UploadId}/complete", content: null);
	LogUploadDiagnostic($"Upload complete response. UploadId={session.UploadId}; Status={(int)completeResponse.StatusCode} {completeResponse.StatusCode}; Body={TruncateUploadDiagnostic(await completeResponse.Content.ReadAsStringAsync())}");
	await completeResponse.EnsureSuccessWithDetailsAsync();
	await DeletePendingUploadManifestAsync(manifestPath);
	if (!string.Equals(uploadZipPath, zipPath, StringComparison.OrdinalIgnoreCase) && File.Exists(uploadZipPath))
		File.Delete(uploadZipPath);
}

static void LogUploadDiagnostic(string message)
{
	try
	{
		var path = Path.Combine(Path.GetTempPath(), "FastCICD-Upload.log");
		File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
	}
	catch
	{
		// Diagnostic logging must never change deployment behavior.
	}
}

static string TruncateUploadDiagnostic(string value)
{
	const int maxLength = 2000;
	return value.Length <= maxLength ? value : value[..maxLength] + "...";
}

static string FormatDiagnosticResponseHeaders(HttpResponseMessage response)
{
	var headers = response.Headers
		.Concat(response.Content.Headers)
		.Where(header => !header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
		.Select(header => $"{header.Key}={string.Join(",", header.Value)}");
	return string.Join(" | ", headers);
}

static string GetPendingUploadManifestPath(ProjectConfig project, string version, string fileHash, int chunkSize)
{
	var key = $"{project.Name}|{version}|{project.EnableRollback}|{chunkSize}|{fileHash}";
	var keyHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
	var directory = Path.Combine(Path.GetTempPath(), "FastCICD-ClientUploadSessions");
	Directory.CreateDirectory(directory);
	return Path.Combine(directory, keyHash + ".json");
}

static async Task<PendingUploadManifest?> LoadPendingUploadManifestAsync(string path)
{
	if (!File.Exists(path))
		return null;

	try
	{
		await using var stream = File.OpenRead(path);
		return await System.Text.Json.JsonSerializer.DeserializeAsync<PendingUploadManifest>(stream);
	}
	catch (System.Text.Json.JsonException)
	{
		return null;
	}
}

static async Task ReadProcessOutputAsync(StreamReader reader, Action<string> onLine)
{
	while (await reader.ReadLineAsync() is { } line)
		onLine(line);
}


static async Task SavePendingUploadManifestAsync(string path, PendingUploadManifest manifest)
{
	var temporaryPath = path + ".tmp";
	await File.WriteAllTextAsync(temporaryPath, System.Text.Json.JsonSerializer.Serialize(manifest));
	File.Move(temporaryPath, path, overwrite: true);
}

static Task DeletePendingUploadManifestAsync(string path)
{
	if (File.Exists(path))
		File.Delete(path);
	return Task.CompletedTask;
}

static async Task<string> ComputeFileHashAsync(string path)
{
	await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
	using var sha256 = SHA256.Create();
	return Convert.ToHexStringLower(await sha256.ComputeHashAsync(stream));
}

sealed class DeploymentDashboard
{
	private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

	// Rows a Panel/Table adds beyond its own content, based on Spectre's Rounded border:
	//   Panel  -> top border+header line, ...content..., bottom border
	//   Table  -> top border, header row, header/body separator, ...data rows..., bottom border
	private const int PanelChromeRows = 2;
	private const int TableChromeRows = 4;
	private const int StageContentRows = 1;
	private const int UploadContentRows = 2;
	private const int TerminalHeightSafetyMargin = 2; // leaves room for the shell prompt line, avoids edge-of-buffer clipping
	private const int MinUsableHeight = 6;
	private const int MaxCommandOutputLines = 12;

	private readonly List<string> commandOutput = [];
	private string stage = "[grey]Initializing deployment pipeline...[/]";
	private string? uploadDetails;
	private int uploadPercent;
	private int spinnerFrame;

	// Raw rows for the delta-file table. Kept as data (not a pre-built Table) so
	// Render() can decide, every frame, how many rows actually fit on screen.
	private List<(string Directory, string FileName)> deltaFileRows = [];

	public void SetStage(string value)
	{
		stage = value;
	}

	public void SetDeltaFiles(List<(string Directory, string FileName)> rows)
	{
		deltaFileRows = rows;
	}

	public void SetUploadProgress(int percent, string details)
	{
		uploadPercent = Math.Clamp(percent, 0, 100);
		uploadDetails = Markup.Escape(details);
		stage = "[blue]Uploading deployment package...[/]";
	}

	public void AddCommandOutput(string line)
	{
		commandOutput.Add(line);
		while (commandOutput.Count > MaxCommandOutputLines)
			commandOutput.RemoveAt(0);
	}

	public void ClearCommandOutput()
	{
		commandOutput.Clear();
	}

	public IRenderable Render(bool advanceSpinner = false)
	{
		if (advanceSpinner)
			spinnerFrame = (spinnerFrame + 1) % SpinnerFrames.Length;

		// The "Deployment status" panel is always shown and always exactly
		// StageContentRows + PanelChromeRows tall, so it is guaranteed visible.
		var content = new List<IRenderable>
		{
			new Panel(new Markup($"[magenta]{SpinnerFrames[spinnerFrame]}[/] {stage}"))
				.Header("[bold cyan]Deployment status[/]")
				.Border(BoxBorder.Rounded)
		};
		var usedRows = StageContentRows + PanelChromeRows;

		if (uploadDetails != null)
		{
			const int width = 36;
			var filled = (int) Math.Round(width * (uploadPercent / 100d));
			var bar = new string('█', filled) + new string('░', width - filled);
			content.Add(new Panel(new Rows(
					new Markup($"[green]{bar}[/] [bold yellow]{uploadPercent}%[/]"),
					new Markup($"[grey]{uploadDetails}[/]")))
				.Header("[bold cyan]Upload progress[/]")
				.Border(BoxBorder.Rounded));
			usedRows += UploadContentRows + PanelChromeRows;
		}

		// Everything below this point is "nice to have" detail (file list, command
		// log). We size it to whatever vertical space is left in the *real* terminal
		// window so the total render never exceeds the visible area - no more manual
		// resizing/maximizing needed to see the status and upload panels above.
		var availableRows = Math.Max(MinUsableHeight, GetTerminalHeight() - TerminalHeightSafetyMargin);
		var remainingRows = Math.Max(0, availableRows - usedRows);

		var wantsTable = deltaFileRows.Count > 0;
		var wantsLog = commandOutput.Count > 0;

		var tableDataRows = 0;
		var logLines = 0;

		if (wantsTable && wantsLog)
		{
			var forData = Math.Max(0, remainingRows - TableChromeRows - PanelChromeRows);
			// The file list is the more important of the two "detail" panels, so it
			// gets the bigger share; the command log still gets a fair minimum.
			tableDataRows = (int) Math.Round(forData * 0.65);
			logLines = forData - tableDataRows;
		}
		else if (wantsTable)
		{
			tableDataRows = Math.Max(0, remainingRows - TableChromeRows);
		}
		else if (wantsLog)
		{
			logLines = Math.Max(0, remainingRows - PanelChromeRows);
		}

		if (wantsTable && tableDataRows > 0)
		{
			var table = new Table()
				.Border(TableBorder.Rounded)
				.BorderColor(Color.Grey)
				.AddColumn(new TableColumn("[cyan]Directory[/]"))
				.AddColumn(new TableColumn("[green]File Name[/]"));

			var totalFiles = deltaFileRows.Count;
			var needsSummaryRow = totalFiles > tableDataRows;
			var rowBudget = needsSummaryRow ? Math.Max(1, tableDataRows - 1) : tableDataRows;

			foreach (var (dir, name) in deltaFileRows.Take(rowBudget))
				table.AddRow(dir, name);

			if (needsSummaryRow)
			{
				var hidden = totalFiles - Math.Min(rowBudget, deltaFileRows.Count);
				table.AddRow("[grey]...[/]", $"[grey]... and {hidden} more file(s) not shown ({totalFiles} total; all of them still upload).[/]");
			}

			content.Add(table);
		}

		if (wantsLog && logLines > 0)
		{
			var visibleLines = commandOutput.TakeLast(logLines);
			content.Add(new Panel(new Rows(visibleLines.Select(line => new Markup(line))))
				.Header("[bold magenta]Live local command output[/]")
				.Border(BoxBorder.Rounded));
		}

		return new Rows(content);
	}

	private static int GetTerminalHeight()
	{
		try
		{
			var height = Console.WindowHeight;
			if (height > 0)
				return height;
		}
		catch
		{
			// Console.WindowHeight throws when output is redirected/not a real console;
			// fall through to the profile-based fallback below.
		}

		return AnsiConsole.Profile.Height > 0 ? AnsiConsole.Profile.Height : 40;
	}
}