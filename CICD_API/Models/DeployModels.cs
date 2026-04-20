namespace CICD_API.Models;

public record CommandRequest(string ProjectName, List<string> Commands);
public record RollbackRequest(string ProjectName, string BackupFileName);
public record CompareRequest(string ProjectName, Dictionary<string, string> FileHashes);
public record ServiceRequest(List<string> Services, string Action);
