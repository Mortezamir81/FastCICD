public class ProjectConfig
{
	public string Name { get; set; } = "";
	public string LocalSourcePath { get; set; } = "";
	public List<string> ServicesToManage { get; set; } = [];
	public List<string> IgnoredFiles { get; set; } = [];

	public List<string> PreDeployCommands { get; set; } = [];
	public List<string> PostDeployCommands { get; set; } = [];
}