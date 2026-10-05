using System.Runtime.CompilerServices;

namespace SampleAttributeNormalizeLib;

/// <summary>One captured caller-info quadruple.</summary>
public sealed record CallerRecord(
    string Member,
    string File,
    int Line,
    string Expr);

public interface ICallerLog
{
    void Notify(
        string msg,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0,
        [CallerArgumentExpression("msg")] string expr = "");
}

public sealed class CallerLog : ICallerLog
{
    public List<CallerRecord> Records { get; } = new();

    public void Notify(
        string msg,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0,
        [CallerArgumentExpression("msg")] string expr = "")
        => Records.Add(new CallerRecord(member, file, line, expr));
}
