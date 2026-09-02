using System.IO;

namespace FlankNote;

/// <summary>
/// Central application identity. The English product name comes from the
/// assembly name in the project file, so runtime UI code has no copied name.
/// Technical identifiers derive from the same product name as the UI.
/// </summary>
static class AppIdentity
{
    public const string RepositoryUrl = "https://github.com/nostalgicz/FlankNote";
    public static string EnglishName => typeof(AppIdentity).Assembly.GetName().Name!;

    // Leave empty until a Chinese product name is chosen. Chinese UI then
    // falls back to the English product name instead of inventing a translation.
    public const string ChineseName = "";

    public static string DisplayName =>
        Loc.Chinese && !string.IsNullOrWhiteSpace(ChineseName) ? ChineseName : EnglishName;

    public static string DebugLogPath =>
        Path.Combine(Path.GetTempPath(), $"{EnglishName.ToLowerInvariant()}-debug.log");

    public static string DefaultExportStem => EnglishName.ToLowerInvariant();

    public static string StorageDirectoryName => EnglishName;
    public static string SingleInstanceId => $"{EnglishName}.SingleInstance";
    public static string StartupValueName => EnglishName;
}
