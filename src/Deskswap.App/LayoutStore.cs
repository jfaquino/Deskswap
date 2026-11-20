using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deskswap.Core;

namespace Deskswap.App;

/// <summary>
/// Saves and loads the desktop layout (design §5.3, §10): atomic writes, no write when nothing changed, and a
/// corrupt file is kept as a backup rather than overwritten.
/// </summary>
/// <param name="path">The layout file.</param>
internal sealed class LayoutStore(string path)
{
    /// <summary>The current file format version.</summary>
    public const int SchemaVersion = 1;

    private string? _lastWritten;

    /// <summary>Gets the backup path if the file was corrupt when loaded, otherwise <see langword="null"/>.</summary>
    public string? QuarantinedPath { get; private set; }

    /// <summary>Loads the saved layout.</summary>
    /// <returns>The snapshot, or <see langword="null"/> if there is none or it was unreadable.</returns>
    public PoolSnapshot? Load()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize(json, AppJsonContext.Default.LayoutFile);
            _lastWritten = json;
            return file?.Pool;
        }
        catch (JsonException)
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            QuarantinedPath = $"{path}.corrupt-{stamp}";
            File.Move(path, QuarantinedPath, overwrite: true);
            return null;
        }
    }

    /// <summary>Saves the layout unless it is identical to what is already on disk.</summary>
    /// <param name="snapshot">The layout to save.</param>
    public void SaveIfChanged(PoolSnapshot snapshot)
    {
        string json = JsonSerializer.Serialize(new LayoutFile(SchemaVersion, snapshot), AppJsonContext.Default.LayoutFile);
        if (json == _lastWritten)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
        _lastWritten = json;
    }
}

/// <summary>The on-disk format of <c>layout.json</c>.</summary>
/// <param name="SchemaVersion">The file format version.</param>
/// <param name="Pool">The saved pool.</param>
internal sealed record LayoutFile(int SchemaVersion, PoolSnapshot Pool);

/// <summary>Source-generated JSON metadata for App files.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LayoutFile))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
