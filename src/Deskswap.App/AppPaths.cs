using Deskswap.Core;
using Deskswap.Platform;

namespace Deskswap.App;

/// <summary>Where Deskswap keeps its files (design §10).</summary>
internal static class AppPaths
{
    /// <summary>Gets the data folder, <c>%LOCALAPPDATA%\Deskswap</c>.</summary>
    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductInfo.DataFolderName);

    /// <summary>Gets the saved desktop layout.</summary>
    public static string Layout => Path.Combine(DataFolder, "layout.json");

    /// <summary>Gets the write-ahead journal of hidden windows.</summary>
    public static string Journal => HiddenWindowJournal.DefaultPath;

    /// <summary>Gets the log file.</summary>
    public static string Log => Path.Combine(DataFolder, "deskswap.log");
}
