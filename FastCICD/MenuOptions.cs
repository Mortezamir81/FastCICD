namespace FastCICD;

public static class MenuOptions
{
	public const string Deploy = "🚀 Deploy (Auto Compare & Sync)";
	public const string CheckStatus = "📊 Check Service Status";
	public const string CheckVersion = "ℹ️ Check Current Version";
	public const string StartServices = "⏯️ Start Services Manually";
	public const string StopServices = "⏹️ Stop Services Manually";
	public const string RunPreDeploy = "🚀 Run Pre-Deploy Commands";
	public const string RunPostDeploy = "🛠️ Run Post-Deploy Commands";
	public const string RunLocalPreDeploy = "💻 Run LOCAL Pre-Deploy Commands";
	public const string RunLocalPostDeploy = "💻 Run LOCAL Post-Deploy Commands";
	public const string Rollback = "⏪ Rollback to Previous Version";
	public const string Back = "[red]⬅️ Back to Main Menu[/]";
	public const string Exit = "[red]Exit[/]";
}
