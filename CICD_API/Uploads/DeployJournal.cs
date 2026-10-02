namespace CICD_API.Uploads;

/// <summary>
/// Records every change a deployment makes to the live directory so a failed deployment
/// can be undone, even when full ZIP backups are disabled. Only touched files are copied.
/// </summary>
public sealed class DeployJournal : IDisposable
{
	private readonly string _baseDir;
	private readonly string _journalDir;
	private readonly Dictionary<string, string> _savedFiles = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _createdFiles = new(StringComparer.OrdinalIgnoreCase);
	private readonly List<string> _deletedDirectories = [];
	private bool _finished;

	public DeployJournal(string baseDir)
	{
		_baseDir = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		_journalDir = Path.Combine(Path.GetTempPath(), "FastCICD-Journal", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_journalDir);
	}

	/// <summary>Call before a file is created or overwritten.</summary>
	public void BeforeWrite(string fullPath)
	{
		if (File.Exists(fullPath))
			Save(fullPath);
		else
			_createdFiles.Add(fullPath);
	}

	/// <summary>Call before a file is deleted.</summary>
	public void BeforeDelete(string fullPath)
	{
		if (File.Exists(fullPath))
			Save(fullPath);
	}

	/// <summary>Call before an empty directory is deleted.</summary>
	public void BeforeDeleteDirectory(string fullPath) => _deletedDirectories.Add(fullPath);

	/// <summary>Undoes everything recorded so far. Returns the paths that could not be restored.</summary>
	public List<string> Rollback()
	{
		var failures = new List<string>();

		foreach (var created in _createdFiles)
		{
			try
			{
				if (File.Exists(created))
				{
					File.SetAttributes(created, File.GetAttributes(created) & ~FileAttributes.ReadOnly);
					File.Delete(created);
				}
				RemoveEmptyParents(created);
			}
			catch (Exception)
			{
				failures.Add(Path.GetRelativePath(_baseDir, created));
			}
		}

		foreach (var directory in _deletedDirectories)
		{
			try { Directory.CreateDirectory(directory); }
			catch (Exception) { failures.Add(Path.GetRelativePath(_baseDir, directory)); }
		}

		foreach (var (original, saved) in _savedFiles)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(original)!);
				if (File.Exists(original))
					File.SetAttributes(original, File.GetAttributes(original) & ~FileAttributes.ReadOnly);
				File.Copy(saved, original, overwrite: true);
			}
			catch (Exception)
			{
				failures.Add(Path.GetRelativePath(_baseDir, original));
			}
		}

		Finish();
		return failures;
	}

	/// <summary>Discards the journal after a successful deployment.</summary>
	public void Commit() => Finish();

	public void Dispose() => Finish();

	private void Save(string fullPath)
	{
		if (_savedFiles.ContainsKey(fullPath))
			return;

		var copyPath = Path.Combine(_journalDir, _savedFiles.Count.ToString());
		File.Copy(fullPath, copyPath, overwrite: true);
		// File.Copy keeps the read-only flag; clear it so the journal can be removed later.
		File.SetAttributes(copyPath, FileAttributes.Normal);
		_savedFiles[fullPath] = copyPath;
	}

	private void RemoveEmptyParents(string fullPath)
	{
		var directory = Path.GetDirectoryName(fullPath);
		while (!string.IsNullOrEmpty(directory) &&
			   directory.Length >= _baseDir.Length &&
			   directory.StartsWith(_baseDir, StringComparison.OrdinalIgnoreCase) &&
			   Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
		{
			Directory.Delete(directory);
			directory = Path.GetDirectoryName(directory);
		}
	}

	private void Finish()
	{
		if (_finished)
			return;
		_finished = true;
		try { Directory.Delete(_journalDir, recursive: true); }
		catch (Exception) { /* Best effort; the system temp folder is cleaned eventually. */ }
	}
}
