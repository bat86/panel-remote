using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace PanelRemote.Services;

public static class AppPaths
{
    public const string AppFolderName = "PanelRemote";
    public const string AppDisplayName = "Пульт доски";
    public const string ExeName = "PanelRemote.exe";

    public static string LocalAppData { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string DataDirectory => Path.Combine(LocalAppData, AppFolderName);
    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string InstallDirectory => Path.Combine(LocalAppData, "Programs", AppFolderName);
    public static string InstalledExePath => Path.Combine(InstallDirectory, ExeName);

    public static string CurrentExecutable =>
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? string.Empty;

    public static string Version =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";

    public static bool IsInstalledLocation => PathsEqual(CurrentExecutable, InstalledExePath);

    public static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void EnsureDataDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
