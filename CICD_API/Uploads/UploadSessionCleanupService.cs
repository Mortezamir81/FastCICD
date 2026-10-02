namespace CICD_API.Uploads;

/// <summary>Periodically removes upload sessions that have been inactive for too long.</summary>
public sealed class UploadSessionCleanupService(IConfiguration configuration, ILogger<UploadSessionCleanupService> logger) : BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var retentionHours = configuration.GetValue<double?>("UploadSessionRetentionHours") ?? 24;
		if (retentionHours > 0)
			UploadSessionStore.Retention = TimeSpan.FromHours(retentionHours);

		var sweepMinutes = configuration.GetValue<double?>("UploadSessionSweepMinutes") ?? 15;
		var interval = TimeSpan.FromMinutes(sweepMinutes > 0 ? sweepMinutes : 15);

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				var removed = UploadSessionStore.Sweep(logger);
				if (removed > 0)
					logger.LogInformation("Upload session cleanup removed {Count} inactive session(s).", removed);
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "Upload session cleanup failed; it will be retried.");
			}

			try { await Task.Delay(interval, stoppingToken); }
			catch (OperationCanceledException) { return; }
		}
	}
}
