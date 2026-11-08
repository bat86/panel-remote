using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PanelRemote.Services;

/// <summary>
/// Установка в профиль пользователя: %LOCALAPPDATA%\Programs\PanelRemote.
/// Никакого UAC, никаких служб, только HKCU\...\Run и ярлыки.
/// </summary>
public static class InstallService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PanelRemote";
    private const string ShortcutName = "Пульт доски.lnk";

    public static bool IsInstalled => File.Exists(AppPaths.InstalledExePath);

    public static InstallResult Install(AppLog log, bool copyCurrentExecutable = true)
    {
        try
        {
            AppPaths.EnsureDataDirectories();
            Directory.CreateDirectory(AppPaths.InstallDirectory);

            var current = AppPaths.CurrentExecutable;
            if (string.IsNullOrWhiteSpace(current) || !File.Exists(current))
            {
                return new InstallResult(false, "Не найден исполняемый файл программы.");
            }

            if (copyCurrentExecutable && !AppPaths.PathsEqual(current, AppPaths.InstalledExePath))
            {
                File.Copy(current, AppPaths.InstalledExePath, overwrite: true);
                log.Info($"Программа скопирована в {AppPaths.InstalledExePath}");
            }

            var startupResult = EnsureAutostart(log);
            CreateShortcuts(log);
            log.Info($"Установка завершена. Автозапуск: {startupResult}");

            return new InstallResult(true, startupResult
                ? null
                : "Не удалось добавить программу в автозапуск.");
        }
        catch (Exception ex)
        {
            log.Error("Ошибка установки", ex);
            return new InstallResult(false, $"Не удалось установить программу: {ex.Message}");
        }
    }

    /// <summary>Первый запуск из любого места: тихо раскладываемся в профиль.</summary>
    public static bool TryAutoInstall(AppLog log, out string? error)
    {
        error = null;
        if (AppPaths.IsInstalledLocation)
        {
            return true;
        }

        log.Info("Первый запуск вне папки установки — выполняю тихую установку в профиль пользователя.");
        var result = Install(log);
        error = result.Error;
        return result.Success;
    }

    public static bool EnsureAutostart(AppLog log)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            var command = $"\"{AppPaths.InstalledExePath}\" --tray";
            key.SetValue(RunValueName, command, RegistryValueKind.String);
            log.Info($"Автозапуск прописан: {command}");
            return true;
        }
        catch (Exception ex)
        {
            log.Error("Не удалось прописать автозапуск в HKCU", ex);
            return false;
        }
    }

    public static bool Uninstall(AppLog log)
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            {
                key?.DeleteValue(RunValueName, throwOnMissingValue: false);
            }

            DeleteShortcut(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                ShortcutName));

            DeleteShortcut(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                ShortcutName));

            log.Info("Автозапуск и ярлыки удалены.");

            var installDirectory = Path.GetFullPath(AppPaths.InstallDirectory);
            var localRoot = Path.GetFullPath(AppPaths.LocalAppData)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (installDirectory.StartsWith(localRoot, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(installDirectory))
            {
                if (AppPaths.IsInstalledLocation)
                {
                    // Работающий exe нельзя удалить сразу — удаляем папку после выхода.
                    var arguments = $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{installDirectory}\"";
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    log.Info("Удаление папки программы отложено до выхода.");
                }
                else
                {
                    Directory.Delete(installDirectory, recursive: true);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Error("Ошибка удаления", ex);
            return false;
        }
    }

    private static void CreateShortcuts(AppLog log)
    {
        try
        {
            var desktop = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                ShortcutName);
            CreateShortcut(desktop, AppPaths.InstalledExePath);

            var startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                ShortcutName);
            CreateShortcut(startMenu, AppPaths.InstalledExePath);
        }
        catch (Exception ex)
        {
            log.Error("Не удалось создать ярлыки", ex);
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath)
    {
        var parent = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return;
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return;
            }

            // WScript.Shell.CreateShortcut — единственный простой способ создать .lnk
            // без установки COM-обёрток и без администратора.
            dynamic shellDynamic = shell;
            shortcut = shellDynamic.CreateShortcut(shortcutPath);
            if (shortcut is null)
            {
                return;
            }

            dynamic link = shortcut;
            link.TargetPath = targetPath;
            link.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? string.Empty;
            link.IconLocation = targetPath + ",0";
            link.Description = "Пульт доски";
            link.WindowStyle = 1;
            link.Save();
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static void DeleteShortcut(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // не критично
        }
    }
}

public sealed record InstallResult(bool Success, string? Error);
