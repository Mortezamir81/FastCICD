namespace CICD_API.Migrations;

public sealed class MigrationProfileOptions
{
	public string ConnectionString { get; init; } = string.Empty;
	public int CommandTimeoutSeconds { get; init; } = 300;
}
