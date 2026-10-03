using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Records;

/// <summary>
/// record 克隆形态解析（设计 §4.1 CloneCtorResolver / 决策 3/4）：
/// record class/struct 判定（本 Roslyn 版本无 IsRecordStruct，用 IsRecord+TypeKind）、
/// 复制 ctor 定位、继承链（抽象/密封/record 基类）、工厂形态与继承链同名定名（防撞）、
/// 编译内 + 引用程序集派生 record 枚举（树外派生洞 CS2J5221）。
/// 全部结论来自 SemanticModel/符号绑定，不做任何文本判定。
/// </summary>
internal static class CloneCtorResolver
{
    public const string FactoryBaseName = "Cs2jWithClone";
    private const string FactoryCollisionSuffix = "_cs2j";

    public static bool IsRecordClass(INamedTypeSymbol type)
        => type.IsRecord && type.TypeKind == TypeKind.Class;

    public static bool IsRecordStruct(INamedTypeSymbol type)
        => type.IsRecord && type.TypeKind == TypeKind.Struct;

    public static bool IsInSource(INamedTypeSymbol type)
        => type.Locations.Any(l => l.IsInSource);

    /// <summary>位置 record：任一声明部件带非空参数表（RC-3 reshape 前的 M1 闸门）。</summary>
    public static bool IsPositional(INamedTypeSymbol type)
    {
        foreach (var r in type.DeclaringSyntaxReferences)
        {
            if (r.GetSyntax() is RecordDeclarationSyntax rec
                && rec.ParameterList is { Parameters.Count: > 0 })
                return true;
        }
        return false;
    }

    /// <summary>定位复制构造函数：实例 ctor、单参、参数类型即本类型（构造泛型按
    /// OriginalDefinition 比较）；含 implicit 合成 ctor（protected/private 均可——
    /// 工厂位于类型内部）。</summary>
    public static IMethodSymbol? FindCopyConstructor(INamedTypeSymbol type)
        => type.InstanceConstructors.FirstOrDefault(c =>
            !c.IsStatic
            && c.Parameters.Length == 1
            && SymbolEqualityComparer.Default.Equals(
                c.Parameters[0].Type.OriginalDefinition, type.OriginalDefinition));

    /// <summary>沿 BaseType 链取 record 祖先（不含自身）。</summary>
    public static IEnumerable<INamedTypeSymbol> RecordAncestors(INamedTypeSymbol type)
    {
        for (var b = type.BaseType; b != null; b = b.BaseType)
        {
            if (b.IsRecord) yield return b;
        }
    }

    /// <summary>编译内（源内）直接/间接派生自 <paramref name="type"/> 的 record。</summary>
    public static IEnumerable<INamedTypeSymbol> SourceRecordDescendants(
        INamedTypeSymbol type, IReadOnlyCollection<INamedTypeSymbol> sourceRecords)
    {
        foreach (var d in sourceRecords)
        {
            if (SymbolEqualityComparer.Default.Equals(d, type)) continue;
            for (var b = d.BaseType; b != null; b = b.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(
                        b.OriginalDefinition, type.OriginalDefinition))
                {
                    yield return d;
                    break;
                }
            }
        }
    }

    /// <summary>工厂形态（决策 3）：抽象→abstract；有 record 基类→override；
    /// 密封→非虚；其余→virtual。</summary>
    public static RecordFactoryForm ResolveFactoryForm(INamedTypeSymbol type)
    {
        if (type.IsAbstract) return RecordFactoryForm.Abstract;
        if (RecordAncestors(type).Any()) return RecordFactoryForm.Override;
        return type.IsSealed ? RecordFactoryForm.Sealed : RecordFactoryForm.Virtual;
    }

    /// <summary>枚举编译可见的全部 record 类型符号（源内 + 引用程序集/模块元数据）。
    /// 只有 record 能派生自 record（CS8865），故树外派生探测只需解 IsRecord 符号，
    /// 不解全量类型；仅在出现 with 站点时由 <see cref="RecordClaimIndex"/> 调用一次。</summary>
    public static IReadOnlyList<INamedTypeSymbol> EnumerateAllRecords(CSharpCompilation compilation)
    {
        var result = new List<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        void VisitType(INamedTypeSymbol type)
        {
            if (type.IsRecord && seen.Add(type)) result.Add(type);
            foreach (var nested in type.GetTypeMembers()) VisitType(nested);
        }

        void VisitNamespaceDeep(INamespaceSymbol ns)
        {
            foreach (var t in ns.GetTypeMembers()) VisitType(t);
            foreach (var child in ns.GetNamespaceMembers()) VisitNamespaceDeep(child);
        }

        VisitNamespaceDeep(compilation.SourceModule.GlobalNamespace);
        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not { } symbol) continue;
            switch (symbol)
            {
                case IAssemblySymbol assembly:
                    VisitNamespaceDeep(assembly.GlobalNamespace);
                    break;
                case IModuleSymbol module:
                    VisitNamespaceDeep(module.GlobalNamespace);
                    break;
            }
        }
        return result;
    }

    /// <summary>树外派生洞（决策 4）：枚举元数据 record 中继承链含
    /// <paramref name="type"/> 的类型。密封 record 无洞。返回树外派生 record 显示名。</summary>
    public static IReadOnlyList<string> FindMetadataDerivedRecords(
        INamedTypeSymbol type, IReadOnlyList<INamedTypeSymbol> allRecords)
    {
        if (type.IsSealed) return Array.Empty<string>();
        var result = new List<string>();
        foreach (var candidate in allRecords)
        {
            if (SymbolEqualityComparer.Default.Equals(candidate, type)) continue;
            if (IsInSource(candidate)) continue;
            for (var b = candidate.BaseType; b != null; b = b.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(
                        b.OriginalDefinition, type.OriginalDefinition))
                {
                    result.Add(candidate.ToDisplayString());
                    break;
                }
            }
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>继承链组件统一工厂定名（决策 2）：基名 Cs2jWithClone；组件内任一类型
    /// 已有用户同名成员（非本族合成签名）则让位 Cs2jWithClone_cs2j，再撞序号。
    /// 幂等：同签名零参返回本类型的既有工厂方法不算撞名（标 FactoryAlreadyExists）。</summary>
    public static (string Name, HashSet<string> AlreadyExistsAt) ResolveFactoryName(
        IReadOnlyCollection<INamedTypeSymbol> component)
    {
        var name = FactoryBaseName;
        while (component.Any(t => HasBlockingMember(t, name)))
        {
            if (name == FactoryBaseName)
                name = FactoryBaseName + FactoryCollisionSuffix;
            else
                name = FactoryBaseName + FactoryCollisionSuffix + NextSequence(name);
        }

        var already = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in component)
        {
            if (t.GetMembers(name).OfType<IMethodSymbol>().Any(m => IsOurFactorySignature(m, t)))
                already.Add(t.ToDisplayString());
        }
        return (name, already);
    }

    private static int NextSequence(string current)
    {
        // Cs2jWithClone_cs2j → 1；Cs2jWithClone_cs2jN → N+1
        var prefix = FactoryBaseName + FactoryCollisionSuffix;
        if (current == prefix) return 1;
        return int.TryParse(current[prefix.Length..], out var n) ? n + 1 : 1;
    }

    /// <summary>阻挡成员：同名用户成员（非 implicit 合成）且不是本族工厂签名。
    /// 注意 record 禁止名为 Clone 的成员（CS8859），Cs2jWithClone 无语言预留。</summary>
    private static bool HasBlockingMember(INamedTypeSymbol type, string name)
        => type.GetMembers(name).Any(m =>
            !m.IsImplicitlyDeclared
            && !(m is IMethodSymbol method && IsOurFactorySignature(method, type)));

    /// <summary>判定是否为本族工厂（幂等识别）：零参、名等、返回类型即本类型。</summary>
    public static bool IsOurFactorySignature(IMethodSymbol method, INamedTypeSymbol type)
        => method.Parameters.Length == 0
           && method.MethodKind == MethodKind.Ordinary
           && method.ReturnType is INamedTypeSymbol rt
           && SymbolEqualityComparer.Default.Equals(rt.OriginalDefinition, type.OriginalDefinition);

    /// <summary>
    /// with 站点可提升性（决策 10，照抄 C11 PD-8 语句位纪律）：剥括号后必须直接位于
    /// 表达式语句（块/switch 节/全局语句）、单变量局部声明、块/switch 节内 return。
    /// 空初始化器由调用方先行豁免（WR-3 纯内联）。
    /// </summary>
    public static bool IsLiftableSite(WithExpressionSyntax withExpression)
    {
        ExpressionSyntax node = withExpression;
        while (node.Parent is ParenthesizedExpressionSyntax)
            node = (ExpressionSyntax)node.Parent;

        switch (node.Parent)
        {
            case ExpressionStatementSyntax es when es.Parent is BlockSyntax
                or SwitchSectionSyntax or GlobalStatementSyntax:
                return true;

            case EqualsValueClauseSyntax eq when eq.Parent is VariableDeclaratorSyntax d
                && d.Parent is VariableDeclarationSyntax vd
                && vd.Variables.Count == 1
                && vd.Parent is LocalDeclarationStatementSyntax ld
                && ld.UsingKeyword.IsKind(SyntaxKind.None)
                && ld.AwaitKeyword.IsKind(SyntaxKind.None)
                && !ld.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)
                    || m.IsKind(SyntaxKind.StaticKeyword)):
                return true;

            case ReturnStatementSyntax r when r.Parent is BlockSyntax or SwitchSectionSyntax:
                return true;

            default:
                return false;
        }
    }
}
