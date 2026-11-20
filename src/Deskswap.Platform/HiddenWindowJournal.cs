using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deskswap.Core;

namespace Deskswap.Platform;

/// <summary>A window recorded in the journal before Deskswap hid it.</summary>
/// <param name="Handle">The window handle.</param>
/// <param name="ProcessId">The owning process id.</param>
/// <param name="ProcessStartTicks">The owning process's UTC start time, in ticks.</param>
/// <param name="Title">The window title when it was hidden, for diagnostics.</param>
public sealed record JournalEntry(long Handle, int ProcessId, long ProcessStartTicks, string? Title)
{
    /// <summary>Gets the window identity.</summary>
    [JsonIgnore]
    public WindowId Window => new(Handle, ProcessId, ProcessStartTicks);

    /// <summary>Creates an entry for a window.</summary>
    /// <param name="window">The window.</param>
    /// <param name="title">Its title.</param>
    /// <returns>The entry.</returns>
    public static JournalEntry For(WindowId window, string? title) => new(window.Handle, window.ProcessId, window.ProcessStartTicks, title);
}

/// <summary>
/// Write-ahead journal of windows Deskswap hid (design §8). A window is recorded and flushed to disk
/// <em>before</em> it is hidden and forgotten after it is shown again, so that if Deskswap dies the watchdog or
/// the next start can show every window again.
/// </summary>
public sealed class HiddenWindowJournal
{
    /// <summary>The current file format version.</summary>
    public const int SchemaVersion = 1;

    private readonly string _path;
    private readonly Dictionary<WindowId, JournalEntry> _entries = [];

    /// <summary>Initializes a new instance of the <see cref="HiddenWindowJournal"/> class and loads any entries left on disk.</summary>
    /// <param name="path">The journal file path.</param>
    public HiddenWindowJournal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        Load();
    }

    /// <summary>Gets the default journal path, <c>%LOCALAPPDATA%\Deskswap\journal.json</c>.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductInfo.DataFolderName, "journal.json");

    /// <summary>Gets the recorded windows.</summary>
    public IReadOnlyCollection<JournalEntry> Entries => _entries.Values;

    /// <summary>Gets the recorded window identities.</summary>
    public IEnumerable<WindowId> Windows => _entries.Keys;

    /// <summary>Gets the path of the quarantined copy if the file was corrupt when loaded, otherwise <see langword="null"/>.</summary>
    public string? QuarantinedPath { get; private set; }

    /// <summary>Records windows and flushes the journal to disk. Call before hiding them.</summary>
    /// <param name="entries">The windows about to be hidden.</param>
    public void Record(IEnumerable<JournalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        bool changed = false;
        foreach (var entry in entries)
        {
            changed |= _entries.TryAdd(entry.Window, entry);
        }

        if (changed)
        {
            Write();
        }
    }

    /// <summary>Forgets windows that are visible again and writes the journal if anything changed.</summary>
    /// <param name="windows">The windows that were shown.</param>
    public void Forget(IEnumerable<WindowId> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        bool changed = false;
        foreach (var window in windows)
        {
            changed |= _entries.Remove(window);
        }

        if (changed)
        {
            Write();
        }
    }

    /// <summary>Forgets every window and deletes the file.</summary>
    public void Clear()
    {
        _entries.Clear();
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(_path);
            var file = JsonSerializer.Deserialize(stream, PlatformJsonContext.Default.JournalFile);
            foreach (var entry in file?.Windows ?? [])
            {
                _entries.TryAdd(entry.Window, entry);
            }
        }
        catch (JsonException)
        {
            Quarantine();
        }
    }

    private void Quarantine()
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        QuarantinedPath = $"{_path}.corrupt-{stamp}";
        File.Move(_path, QuarantinedPath, overwrite: true);
    }

    /// <summary>Writes atomically: a flushed temp file replaces the journal.</summary>
    private void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, new JournalFile(SchemaVersion, [.. _entries.Values]), PlatformJsonContext.Default.JournalFile);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, _path, overwrite: true);
    }
}

/// <summary>The on-disk format of the journal.</summary>
/// <param name="SchemaVersion">The file format version.</param>
/// <param name="Windows">The hidden windows.</param>
internal sealed record JournalFile(int SchemaVersion, IReadOnlyList<JournalEntry> Windows);

/// <summary>Source-generated JSON metadata for Platform files.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JournalFile))]
internal sealed partial class PlatformJsonContext : JsonSerializerContext;
