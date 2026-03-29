using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.Context;

/// <summary>
/// 诊断收集器
/// </summary>
public class DiagnosticCollector
{
    private readonly List<DiagnosticMessage> _messages = new();

    public IReadOnlyList<DiagnosticMessage> Messages => _messages;

    public void Error(string message, Location? location = null)
    {
        _messages.Add(new DiagnosticMessage(DiagnosticSeverity.Error, message, location));
    }

    public void Warning(string message, Location? location = null)
    {
        _messages.Add(new DiagnosticMessage(DiagnosticSeverity.Warning, message, location));
    }

    public void Info(string message, Location? location = null)
    {
        _messages.Add(new DiagnosticMessage(DiagnosticSeverity.Info, message, location));
    }
}

/// <summary>
/// 诊断消息
/// </summary>
public record DiagnosticMessage(
    DiagnosticSeverity Severity,
    string Message,
    Location? Location
);

/// <summary>
/// 诊断严重程度
/// </summary>
public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}
