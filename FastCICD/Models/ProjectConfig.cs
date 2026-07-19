using FastCICD;

public class ProjectConfig
{
	public string Name { get; set; } = "";
	public string LocalSourcePath { get; set; } = "";
	public bool EnableRollback { get; set; } = true;
	public bool AlwaysRunPostDeployCommands { get; set; } = false;
	public List<string> ServicesToManage { get; set; } = [];
	public List<string> IgnoredFiles { get; set; } = [];

	public List<string> PreDeployCommands { get; set; } = [];
	public List<string> PostDeployCommands { get; set; } = [];

	public List<LocalCommandConfig> LocalPreDeployCommands { get; set; } = [];
	public List<LocalCommandConfig> LocalPostDeployCommands { get; set; } = [];
}