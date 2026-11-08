using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PanelRemote.Services;

/// <summary>
/// Простой потокобезопасный журнал в %LOCALAPPDATA%\PanelRemote\logs.
/// Никаких внешних зависимостей: поддержка может открыть файл блокнотом.
/// </summary>
public sealed class AppLog : IDisposable
{
    private readonly object _gate = new();
    private readonly string _filePath;
    private bool _disposed;

    public AppLog()
    {
        AppPaths.EnsureDataDirectories();
        _filePath = Path.Combine(
            AppPaths.LogsDirectory,
            $"panel-{DateTime.Now:yyyyMMdd}.log");

        CleanupOldLogs();
    }

    public string FilePath => _filePath;

    public void Info(string message) => Write("INFO", message, null);
    public void Warn(string message) => Write("WARN", message, null);
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var builder = new StringBuilder(256);
            builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            builder.Append(" [").Append(level).Append("] ").Append(message);
            if (exception is not null)
            {
                builder.AppendLine();
                foreach (var line in exception.ToString().Split('\n'))
                {
                    builder.Append("       ").AppendLine(line.TrimEnd());
                }
            }

            lock (_gate)
            {
                File.AppendAllText(_filePath, builder.ToString() + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Журнал не должен ломать приложение.
        }
    }

    private static void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-30);
            foreach (var file in Directory.EnumerateFiles(AppPaths.LogsDirectory, "panel-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }

            // На всякий случай не копим больше 60 файлов, даже если даты странные.
            var files = Directory.EnumerateFiles(AppPaths.LogsDirectory, "panel-*.log")
                .OrderByDescending(File.GetLastWriteTime)
                .Skip(60)
                .ToArray();
            foreach (var file in files)
            {
                File.Delete(file);
            }
        }
        catch
        {
            // не критично
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
