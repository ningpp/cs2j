using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar.Events;

/// <summary>统计分类（CS2J5150 分项）。</summary>
internal enum EventPlanKind
{
    FieldLike,
    Custom,
    AbstractOrInterface,
    ExplicitInterface,
}

/// <summary>一个源内事件符号一个 plan（D21）：同名字段（field-like）+ add_X/remove_X 两方法为原子改写单位。</summary>
internal sealed class EventPlan : MemberDesugarPlan
{
    public required IEventSymbol Event { get; init; }
    /// <summary>声明标识符文本（显式接口实现的 <see cref="ISymbol.Name"/> 是 "IFoo.X" 形态，
    /// 字段名/方法名必须用声明标识符）。</summary>
    public required string DeclName { get; init; }
    public required string AdderName { get; init; }
    public required string RemoverName { get; init; }
    /// <summary>是否生成后备字段（field-like 且非 abstract/接口成员）。</summary>
    public required bool HasField { get; init; }
    /// <summary>生成访问器是否使用 CAS 模式（仅 field-like；struct 强制 Simple，D6）。</summary>
    public required bool UseCas { get; init; }
    public required EventPlanKind PlanKind { get; init; }

    public override bool OwnsTouch(string filePath, string? typeName, string? memberName)
    {
        if (!TouchedFiles.Contains(filePath)) return false;
        if (!string.Equals(typeName, Event.ContainingType?.Name, StringComparison.Ordinal)) return false;
        return string.Equals(memberName, DeclName, StringComparison.Ordinal)
            || string.Equals(memberName, AdderName, StringComparison.Ordinal)
            || string.Equals(memberName, RemoverName, StringComparison.Ordinal);
    }

    public static string BuildId(IEventSymbol evt)
    {
        var span = evt.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0;
        return $"MD:{evt.ContainingType?.ToDisplayString()}::{evt.Name}@{span}";
    }
}
