using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.StructValueSemantics;

/// <summary>
/// Phase 2 shim 决策与合成（设计 §2 决策表/§4.4）：
/// <list type="bullet">
/// <item>零参、返回类型恰为本类型、实例且可从任意调用点访问（public）的 Clone() → 复用（SV010）。</item>
/// <item>Clone 名被占用但签名不符，或拷贝构造签名冲突 → plan Infeasible（SV050）。</item>
/// <item>字段式事件 / fixed 缓冲区 → plan Infeasible（SV051），无法逐字段复制。</item>
/// <item>其余：合成 private 拷贝构造（不存在时）+ public Clone()；残余可变 struct
/// 且有可用 Clone 成员的字段递归 Clone，自动属性后备字段经属性赋值（get-only 在 ctor 合法）。</item>
/// </list>
/// 本类只做符号判定与语法合成，不发射诊断（由 analyzer 按 plan 终态统一发射）。
/// </summary>
internal static class CloneShimGenerator
{
    public const string CloneMethodName = "Clone";

    /// <summary>对 plan 就地完成 shim 决策与合成；终态写入 plan.Outcome/ShimKind/Fail*。</summary>
    public static void ResolveAndGenerate(
        ClonePlan plan,
        IReadOnlyDictionary<INamedTypeSymbol, ClonePlan> plansByOriginalDefinition)
    {
        var symbol = plan.Symbol;

        // 1) 字段式事件（event Action E;）：后备委托无法在拷贝构造中逐字段安全复制 → SV051。
        if (HasFieldStyleEvent(symbol))
        {
            MarkInfeasible(plan, StructValueSemanticsDiagnosticCode.SV051,
                "struct declares a field-like event; a defensive Clone shim cannot copy the " +
                "event backing delegate field by value (add an explicit Clone() or skip the type).");
            return;
        }

        // 2) fixed 缓冲区（unsafe fixed size buffer）→ SV051。
        if (symbol.GetMembers().OfType<IFieldSymbol>().Any(f => f.IsFixedSizeBuffer))
        {
            MarkInfeasible(plan, StructValueSemanticsDiagnosticCode.SV051,
                "struct contains a fixed-size buffer; a defensive Clone shim cannot perform a " +
                "field-by-field value copy of the buffer (add an explicit Clone() or skip the type).");
            return;
        }

        // 3) 既有 Clone()/Clone 名占用裁决。
        var existingClone = FindReusableClone(symbol);
        var cloneNameOccupied = symbol.GetMembers(CloneMethodName).Any(m =>
            m.Kind == SymbolKind.Method || m.Kind == SymbolKind.Property
            || m.Kind == SymbolKind.Field || m.Kind == SymbolKind.Event);

        if (existingClone != null)
        {
            plan.ShimKind = ShimKind.ReuseExistingClone;
            return;
        }

        if (cloneNameOccupied)
        {
            MarkInfeasible(plan, StructValueSemanticsDiagnosticCode.SV050,
                "struct already declares a member named 'Clone' but it is not a reusable " +
                "parameterless public instance method returning the struct type itself.");
            return;
        }

        // 4) 拷贝构造现状：签名恰为 .ctor(S)（by value）即可被生成的 Clone 复用；
        //    其他单参 ctor（ref/in S 或异类型）不构成签名冲突，仍生成我们自己的拷贝构造。
        var copyCtorExists = FindCopyConstructor(symbol) != null;

        // 5) 合成成员。
        if (!copyCtorExists)
            plan.GeneratedCopyConstructor = BuildCopyConstructor(plan, plansByOriginalDefinition);
        plan.GeneratedCloneMethod = BuildCloneMethod(symbol);
        plan.ShimKind = ShimKind.GenerateCopyCtorAndClone;
    }

    /// <summary>
    /// 零参、非泛型、实例、返回类型恰为本类型且 public 的 Clone()。
    /// struct 隐式密封，protected/internal 无法从跨工程调用点访问，故只认 public。
    /// </summary>
    private static IMethodSymbol? FindReusableClone(INamedTypeSymbol symbol)
    {
        foreach (var member in symbol.GetMembers(CloneMethodName).OfType<IMethodSymbol>())
        {
            if (member.MethodKind != MethodKind.Ordinary) continue;
            if (member.IsStatic || member.IsGenericMethod) continue;
            if (member.Parameters.Length != 0) continue;
            if (member.DeclaredAccessibility != Accessibility.Public) continue;
            if (!SymbolEqualityComparer.Default.Equals(
                    member.ReturnType.OriginalDefinition, symbol.OriginalDefinition))
            {
                continue;
            }

            return member;
        }

        return null;
    }

    private static IMethodSymbol? FindCopyConstructor(INamedTypeSymbol symbol)
    {
        foreach (var ctor in symbol.InstanceConstructors)
        {
            if (ctor.Parameters.Length != 1) continue;
            var p = ctor.Parameters[0];
            if (p.RefKind != RefKind.None) continue;
            if (SymbolEqualityComparer.Default.Equals(
                    p.Type.OriginalDefinition, symbol.OriginalDefinition))
            {
                return ctor;
            }
        }

        return null;
    }

    private static bool HasFieldStyleEvent(INamedTypeSymbol symbol)
    {
        foreach (var ev in symbol.GetMembers().OfType<IEventSymbol>())
        {
            // 字段式事件在源码中体现为 EventFieldDeclarationSyntax（符号的声明语法引用
            // 也可能落在其下的 VariableDeclaratorSyntax）；自定义 add/remove 事件没有后备字段。
            if (ev.DeclaringSyntaxReferences.Any(r =>
                    r.GetSyntax().AncestorsAndSelf().OfType<EventFieldDeclarationSyntax>().Any()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <c>private S(S other) { f = other.f; P = other.P; ... }</c>：
    /// 显式实例字段直接赋值；字段类型为「激活 plan 的残余可变 struct」时递归 Clone；
    /// 自动属性的隐式后备字段改经属性赋值（含 get-only，ctor 内合法）。
    /// </summary>
    private static ConstructorDeclarationSyntax BuildCopyConstructor(
        ClonePlan plan,
        IReadOnlyDictionary<INamedTypeSymbol, ClonePlan> plansByOriginalDefinition)
    {
        var symbol = plan.Symbol;
        var name = symbol.Name;
        var typeReference = BareTypeName(symbol);
        var body = new StringBuilder();
        body.AppendLine();

        foreach (var field in symbol.GetMembers().OfType<IFieldSymbol>()
                     .Where(f => !f.IsStatic && !f.IsConst)
                     .OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            // 字段式事件/fixed 已在裁决期 Infeasible，这里仅作防御性跳过。
            if (field.IsFixedSizeBuffer || field.AssociatedSymbol is IEventSymbol) continue;

            if (field.IsImplicitlyDeclared && field.AssociatedSymbol is IPropertySymbol property)
            {
                // 自动属性后备字段：经属性赋值（get-only/init-only 自动属性在拷贝构造内可写）。
                body.Append("            ")
                    .Append(property.Name)
                    .Append(" = other.")
                    .Append(property.Name);
                if (NeedsRecursiveClone(field.Type, plansByOriginalDefinition))
                    body.Append(".Clone()");
                body.AppendLine(";");
                continue;
            }

            if (field.IsImplicitlyDeclared) continue; // 枚举值等其他合成字段，忽略

            body.Append("            ")
                .Append(field.Name)
                .Append(" = other.")
                .Append(field.Name);
            if (NeedsRecursiveClone(field.Type, plansByOriginalDefinition))
                body.Append(".Clone()");
            body.AppendLine(";");
        }

        var requiredMembers = symbol.GetMembers()
            .Any(m => m is IFieldSymbol { IsRequired: true } or IPropertySymbol { IsRequired: true });
        var text = new StringBuilder();
        if (requiredMembers)
            text.AppendLine("[System.Diagnostics.CodeAnalysis.SetsRequiredMembers]");
        text.Append("private ").Append(name).Append("(").Append(typeReference).Append(" other)")
            .AppendLine(" {")
            .Append(body)
            .Append("        }");

        var ctor = (ConstructorDeclarationSyntax?)SyntaxFactory.ParseMemberDeclaration(
            text.ToString(), offset: 0, options: null);
        if (ctor == null)
            throw new InvalidOperationException(
                $"Failed to synthesize copy constructor for {plan.StructQualifiedName}.");

        return ctor.WithNewLineTrivia();
    }

    /// <summary>
    /// <c>public S Clone() =&gt; new S(this);</c>。泛型类型体内返回类型与对象创建表达式
    /// 都必须带类型实参（CS0305：裸名不绑定为当前构造类型），故生成 <c>Node&lt;T&gt;</c>。
    /// </summary>
    private static MethodDeclarationSyntax BuildCloneMethod(INamedTypeSymbol symbol)
    {
        var typeReference = BareTypeName(symbol);
        var text = $"public {typeReference} {CloneMethodName}() => new {typeReference}(this);";
        var method = (MethodDeclarationSyntax?)SyntaxFactory.ParseMemberDeclaration(
            text, offset: 0, options: null);
        if (method == null)
            throw new InvalidOperationException(
                $"Failed to synthesize Clone() for {symbol.ToDisplayString()}.");

        return method.WithNewLineTrivia();
    }

    /// <summary>
    /// 逐字段复制成员清单（与拷贝构造口径一致）：显式实例字段给出字段名；自动属性
    /// 后备字段给出属性名（构造函数内经属性赋值合法，get-only 亦然）；字段式事件后备/
    /// fixed 缓冲区已在裁决期 Infeasible（SV051），此处不返回；其他合成字段忽略。
    /// SV041 字段式展开复用本清单，保证 shim 拷贝构造与 this 赋值展开覆盖同一成员集。
    /// </summary>
    internal static IReadOnlyList<(string Name, bool IsAutoProperty)> EnumerateCopyMembers(
        INamedTypeSymbol symbol)
    {
        var result = new List<(string, bool)>();
        foreach (var field in symbol.GetMembers().OfType<IFieldSymbol>()
                     .Where(f => !f.IsStatic && !f.IsConst)
                     .OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            if (field.IsFixedSizeBuffer || field.AssociatedSymbol is IEventSymbol) continue;
            if (field.IsImplicitlyDeclared && field.AssociatedSymbol is IPropertySymbol property)
            {
                result.Add((property.Name, true));
                continue;
            }
            if (field.IsImplicitlyDeclared) continue;
            result.Add((field.Name, false));
        }
        return result;
    }

    /// <summary>
    /// 字段静态类型 ∈ 残余可变 struct 且其 plan 激活（Clone 成员必然可用）时递归深拷贝；
    /// 其余（基元/enum/string/引用/数组/readonly struct/跳过 plan）保持浅赋值（§4.4）。
    /// </summary>
    private static bool NeedsRecursiveClone(
        ITypeSymbol fieldType,
        IReadOnlyDictionary<INamedTypeSymbol, ClonePlan> plansByOriginalDefinition)
    {
        if (fieldType is not INamedTypeSymbol named) return false;
        return plansByOriginalDefinition.TryGetValue(named.OriginalDefinition, out var fieldPlan)
            && fieldPlan.IsActive;
    }

    private static void MarkInfeasible(
        ClonePlan plan, StructValueSemanticsDiagnosticCode code, string reason)
    {
        plan.Outcome = ClonePlanOutcome.Infeasible;
        plan.ShimKind = ShimKind.Infeasible;
        plan.FailCode = code;
        plan.FailReason = reason;
        var loc = plan.Symbol.Locations.FirstOrDefault(l => l.IsInSource);
        plan.FailLocation = loc;
    }

    /// <summary>
    /// 类型体内可引用自身的名字：非泛型为 <c>S</c>，泛型为 <c>S&lt;T1, T2&gt;</c>
    /// （类型形参名取自开放定义，体内引用合法）。
    /// </summary>
    private static string BareTypeName(INamedTypeSymbol symbol)
    {
        if (symbol.TypeParameters.Length == 0) return symbol.Name;
        return symbol.Name + "<" + string.Join(", ", symbol.TypeParameters.Select(p => p.Name)) + ">";
    }

    /// <summary>为合成成员挂统一的换行/缩进 trivia（FormatAndValidate 会再整文件格式化）。</summary>
    private static T WithNewLineTrivia<T>(this T member) where T : MemberDeclarationSyntax
    {
        var eol = SyntaxFactory.EndOfLine("\n");
        return member
            .WithLeadingTrivia(SyntaxFactory.Whitespace("        "), eol)
            .WithTrailingTrivia(eol, SyntaxFactory.Whitespace("    "));
    }
}
