namespace CSharpToJava.Core.MemberDesugar;

internal interface IMemberDesugarRule
{
    MemberDesugarKind Kind { get; }
    int Order { get; }
    void Analyze(MemberDesugarAnalysisContext context, MemberDesugarPlanBuilder builder);
}

/// <summary>种类 + 同 Kind 内确定性顺序。规则经程序集扫描发现，禁止注册表热文件（设计 §4.5）。</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class MemberDesugarRuleAttribute(MemberDesugarKind kind, int order) : Attribute
{
    public MemberDesugarKind Kind { get; } = kind;
    public int Order { get; } = order;
}

internal static class MemberDesugarRuleDiscovery
{
    private static IReadOnlyList<IMemberDesugarRule>? _cached;

    public static IReadOnlyList<IMemberDesugarRule> Discover()
    {
        if (_cached != null) return _cached;
        _cached = typeof(MemberDesugarRuleDiscovery).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                && t.GetCustomAttributes(typeof(MemberDesugarRuleAttribute), inherit: false).Length == 1
                && typeof(IMemberDesugarRule).IsAssignableFrom(t)
                && t.GetConstructor(Type.EmptyTypes) != null)
            .Select(t => (IMemberDesugarRule)Activator.CreateInstance(t)!)
            .OrderBy(r => r.Kind).ThenBy(r => r.Order)
            .ToList();
        return _cached;
    }
}
