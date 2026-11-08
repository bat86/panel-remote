using System.Collections.Generic;

namespace PanelRemote.Models;

public enum DiagnosticSeverity
{
    Ok,
    Info,
    Warning,
    Error
}

public sealed record DiagnosticItem(DiagnosticSeverity Severity, string Text)
{
    public string Icon => Severity switch
    {
        DiagnosticSeverity.Ok => "✓",
        DiagnosticSeverity.Warning => "!",
        DiagnosticSeverity.Error => "×",
        _ => "i"
    };

    public string Prefix => Severity switch
    {
        DiagnosticSeverity.Ok => "ВСЁ ХОРОШО",
        DiagnosticSeverity.Warning => "ВНИМАНИЕ",
        DiagnosticSeverity.Error => "ПРОБЛЕМА",
        _ => "ПРОВЕРКА"
    };
}

public sealed record DiagnosticReport(
    string Headline,
    string Explanation,
    IReadOnlyList<DiagnosticItem> Items,
    bool HasProblems,
    string FixLabel,
    string? LogPath);
