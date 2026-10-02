using CICD_API.Uploads;

namespace FastCICD.Tests;

public sealed class DeployJournalTests : IDisposable
{
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "FastCICD-Tests", Guid.NewGuid().ToString("N"));

	public DeployJournalTests() => Directory.CreateDirectory(_dir);

	public void Dispose()
	{
		try { Directory.Delete(_dir, recursive: true); }
		catch (Exception) { /* best effort */ }
	}

	private string P(string name) => Path.Combine(_dir, name);

	[Fact]
	public void Rollback_restores_overwritten_files()
	{
		File.WriteAllText(P("a.txt"), "old");
		using var journal = new DeployJournal(_dir);

		journal.BeforeWrite(P("a.txt"));
		File.WriteAllText(P("a.txt"), "new");
		var failures = journal.Rollback();

		Assert.Empty(failures);
		Assert.Equal("old", File.ReadAllText(P("a.txt")));
	}

	[Fact]
	public void Rollback_removes_created_files_and_their_new_folders()
	{
		using var journal = new DeployJournal(_dir);

		var created = P(Path.Combine("newdir", "deep", "c.txt"));
		Directory.CreateDirectory(Path.GetDirectoryName(created)!);
		journal.BeforeWrite(created);
		File.WriteAllText(created, "x");
		journal.Rollback();

		Assert.False(File.Exists(created));
		Assert.False(Directory.Exists(P("newdir")));
		Assert.True(Directory.Exists(_dir));
	}

	[Fact]
	public void Rollback_restores_deleted_files_and_directories()
	{
		Directory.CreateDirectory(P("emptydir"));
		File.WriteAllText(P("gone.txt"), "keep me");
		using var journal = new DeployJournal(_dir);

		journal.BeforeDelete(P("gone.txt"));
		File.Delete(P("gone.txt"));
		journal.BeforeDeleteDirectory(P("emptydir"));
		Directory.Delete(P("emptydir"));
		journal.Rollback();

		Assert.Equal("keep me", File.ReadAllText(P("gone.txt")));
		Assert.True(Directory.Exists(P("emptydir")));
	}

	[Fact]
	public void Rollback_restores_read_only_files()
	{
		File.WriteAllText(P("ro.txt"), "old");
		File.SetAttributes(P("ro.txt"), FileAttributes.ReadOnly);
		using var journal = new DeployJournal(_dir);

		journal.BeforeWrite(P("ro.txt"));
		File.SetAttributes(P("ro.txt"), FileAttributes.Normal);
		File.WriteAllText(P("ro.txt"), "new");
		Assert.Empty(journal.Rollback());

		Assert.Equal("old", File.ReadAllText(P("ro.txt")));
	}

	[Fact]
	public void Writing_the_same_file_twice_keeps_the_original()
	{
		File.WriteAllText(P("a.txt"), "original");
		using var journal = new DeployJournal(_dir);

		journal.BeforeWrite(P("a.txt"));
		File.WriteAllText(P("a.txt"), "first change");
		journal.BeforeWrite(P("a.txt"));
		File.WriteAllText(P("a.txt"), "second change");
		journal.Rollback();

		Assert.Equal("original", File.ReadAllText(P("a.txt")));
	}

	[Fact]
	public void Commit_keeps_the_new_state()
	{
		File.WriteAllText(P("a.txt"), "old");
		var journal = new DeployJournal(_dir);

		journal.BeforeWrite(P("a.txt"));
		File.WriteAllText(P("a.txt"), "new");
		journal.Commit();
		journal.Dispose();

		Assert.Equal("new", File.ReadAllText(P("a.txt")));
	}

	[Fact]
	public void Rollback_reports_files_it_could_not_restore()
	{
		File.WriteAllText(P("locked.txt"), "old");
		using var journal = new DeployJournal(_dir);
		journal.BeforeWrite(P("locked.txt"));
		File.WriteAllText(P("locked.txt"), "new");

		List<string> failures;
		using (new FileStream(P("locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
			failures = journal.Rollback();

		Assert.Contains("locked.txt", failures);
	}
}
