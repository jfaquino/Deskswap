using Deskswap.Core;

namespace Deskswap.Platform.Tests;

/// <summary>Design §8: the write-ahead journal survives restarts and never loses entries silently.</summary>
public sealed class HiddenWindowJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "deskswap-tests", Guid.NewGuid().ToString("N"));

    private string JournalPath => Path.Combine(_directory, "journal.json");

    [Fact]
    public void Record_writes_entries_that_a_new_instance_reads_back()
    {
        var journal = new HiddenWindowJournal(JournalPath);

        journal.Record([Entry(1, "Notepad"), Entry(2, "Chrome")]);

        var reloaded = new HiddenWindowJournal(JournalPath);
        Assert.Equal(journal.Windows.OrderBy(w => w.Handle), reloaded.Windows.OrderBy(w => w.Handle));
        Assert.Contains(reloaded.Entries, e => e.Title == "Chrome");
        Assert.False(File.Exists(JournalPath + ".tmp"));
    }

    [Fact]
    public void Forget_removes_entries_from_disk()
    {
        var journal = new HiddenWindowJournal(JournalPath);
        journal.Record([Entry(1), Entry(2)]);

        journal.Forget([Window(1)]);

        Assert.Equal([Window(2)], new HiddenWindowJournal(JournalPath).Windows);
    }

    [Fact]
    public void Recording_the_same_window_twice_keeps_one_entry()
    {
        var journal = new HiddenWindowJournal(JournalPath);

        journal.Record([Entry(1, "first")]);
        journal.Record([Entry(1, "second")]);

        Assert.Equal("first", Assert.Single(new HiddenWindowJournal(JournalPath).Entries).Title);
    }

    [Fact]
    public void Same_handle_from_another_process_is_a_separate_entry()
    {
        var journal = new HiddenWindowJournal(JournalPath);

        journal.Record([Entry(1), new JournalEntry(1, ProcessId: 99, ProcessStartTicks: 7, Title: null)]);

        Assert.Equal(2, new HiddenWindowJournal(JournalPath).Entries.Count);
    }

    [Fact]
    public void Clear_deletes_the_file()
    {
        var journal = new HiddenWindowJournal(JournalPath);
        journal.Record([Entry(1)]);

        journal.Clear();

        Assert.False(File.Exists(JournalPath));
        Assert.Empty(new HiddenWindowJournal(JournalPath).Entries);
    }

    [Fact]
    public void Corrupt_file_is_quarantined_not_deleted()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(JournalPath, "{ not json");

        var journal = new HiddenWindowJournal(JournalPath);

        Assert.Empty(journal.Entries);
        Assert.NotNull(journal.QuarantinedPath);
        Assert.Equal("{ not json", File.ReadAllText(journal.QuarantinedPath));
        Assert.False(File.Exists(JournalPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static WindowId Window(int n) => new(n, ProcessId: 1000 + n, ProcessStartTicks: 42);

    private static JournalEntry Entry(int n, string? title = null) => JournalEntry.For(Window(n), title);
}
