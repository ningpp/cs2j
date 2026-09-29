using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar.Operators;

/// <summary>每个源内运算符/转换符号一 plan（设计 §4.2）；plan 是原子回退单位。</summary>
internal sealed class OperatorPlan : MemberDesugarPlan
{
    public required IMethodSymbol Operator { get; init; }
    public required bool IsConversion { get; init; }
    /// <summary>命名预留后的最终方法名（决策 8）。</summary>
    public required string GeneratedMethodName { get; init; }
    /// <summary>碰撞前基名（== GeneratedMethodName 时无消歧）。</summary>
    public required string BaseMethodName { get; init; }

    public override bool OwnsTouch(string filePath, string? typeName, string? memberName)
    {
        if (!TouchedFiles.Contains(filePath)) return false;
        if (!string.Equals(typeName, Operator.ContainingType?.Name, StringComparison.Ordinal)) return false;
        return string.Equals(memberName, GeneratedMethodName, StringComparison.Ordinal)
               || string.Equals(memberName, BaseMethodName, StringComparison.Ordinal)
               || string.Equals(memberName, Operator.Name, StringComparison.Ordinal);
    }

    public static string BuildId(IMethodSymbol op)
    {
        var span = op.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0;
        return $"MD:{op.ContainingType?.ToDisplayString()}::{op.Name}@{span}";
    }
}
