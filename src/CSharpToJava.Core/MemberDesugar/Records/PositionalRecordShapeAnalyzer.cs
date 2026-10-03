using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.MemberDesugar.Properties;

namespace CSharpToJava.Core.MemberDesugar.Records;

/// <summary>
/// 位置 record reshape 物料分析（RC-3/RC-4，设计决策 8a/8b/8c/6）。结论全部来自
/// SemanticModel/符号：位置参数 ↔ 合成位置属性清单、经 <see cref="BackingFieldNaming"/>
/// 算定的后备字段名、5220 闸门四类检测（①主 ctor 参数被字段初始化器/体内语句捕获；
/// ②用户自定义 Deconstruct；③用户自定义同形复制 ctor；④发射成员名与既有显式成员冲突）。
/// 本类只产分析快照（供 RecordRule plan / RecordDeclarationRewriter 发终形 /
/// PropertyRule 5123 认领共用同一物料），不做任何语法改写。
/// </summary>
internal sealed class PositionalShapeMember
{
    /// <summary>编译器为该位置参数合成的属性符号（手动实现同名属性时不走 reshape，闸门拦）。</summary>
    public required IPropertySymbol Property { get; init; }

    /// <summary>原始参数语法节点（含默认值/params/scoped 等，发主 ctor 时整体复用；
    /// 其 Type 节点在字段/get/set 签名处复用——绿节点不可变，可多处复用）。</summary>
    public required ParameterSyntax Parameter { get; init; }

    /// <summary>终形后备字段名（经 BackingFieldNaming 与 C11 契约逐字同名）。
    /// 基类转发参数（<see cref="IsInherited"/>）不发字段，此值为空串。</summary>
    public required string FieldName { get; init; }

    /// <summary>true=该参数在本类型不合成新属性（Roslyn 对与可继承位置属性同名的派生
    /// 位置参数复用基类属性）：不发字段/get/set，主 ctor 仅经 base(...) 转发；
    /// Deconstruct 仍以继承来的 get_X() 读它。</summary>
    public required bool IsInherited { get; init; }
}

/// <summary>一个可 reshape 的源内位置 record 的完整物料（终形发射所需的一切，重写期零语义查询）。</summary>
internal sealed class PositionalShape
{
    public required INamedTypeSymbol Type { get; init; }

    /// <summary>携带 ParameterList 的声明部件（partial 多部件中唯一；reshape 只在该部件执行）。</summary>
    public required RecordDeclarationSyntax Part { get; init; }

    /// <summary>true=record struct（RC-4：发无参 ctor，不发复制 ctor/工厂）。</summary>
    public required bool IsStruct { get; init; }

    /// <summary>位置成员（按参数序）。</summary>
    public required IReadOnlyList<PositionalShapeMember> Members { get; init; }

    /// <summary>派生位置 class 的主 ctor 基链实参（<c>: Point(X, 0)</c> 的整个基部件）；
    /// reshape 时从 BaseList 移入新发主 ctor 的 constructor initializer。</summary>
    public PrimaryConstructorBaseTypeSyntax? PrimaryBase { get; init; }

    /// <summary>是否补发 public 无参 ctor（class：基类无可访问无参 ctor 时不发；
    /// 用户已显式声明无参 ctor 时不发；struct：用户未显式声明时必发）。</summary>
    public required bool EmitParameterlessCtor { get; init; }
}

/// <summary>5220 闸门结论。</summary>
internal sealed record ShapeGate(MemberDesugarDiagnosticCode Code, string Reason);

internal static class PositionalRecordShapeAnalyzer
{
    /// <summary>
    /// 分析源内位置 record 的 reshape 可行性。
    /// 非位置 record（所有部件均无 ParameterList）→ (null, null)；
    /// 位置但命中闸门 → (null, gate)；可行 → (shape, null)。
    /// </summary>
    public static (PositionalShape? Shape, ShapeGate? Gate) Analyze(
        INamedTypeSymbol type, CSharpCompilation compilation)
    {
        var parts = type.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<RecordDeclarationSyntax>()
            .Where(r => r.ParameterList is { Parameters.Count: > 0 })
            .ToList();
        if (parts.Count == 0) return (null, null);
        if (parts.Count > 1)
            return Gate(type, "multiple declaring parts carry a positional parameter list "
                + "(partial positional records with multiple parameter lists cannot be reshaped mechanically)");

        var part = parts[0];
        var model = compilation.GetSemanticModel(part.SyntaxTree);
        var isStruct = CloneCtorResolver.IsRecordStruct(type);

        // readonly record struct：终形 set_X/字段直写在只读成员上无合法发射面。
        if (isStruct && type.IsReadOnly)
            return Gate(type, "the record struct is readonly, so its materialized set_X "
                + "methods and backing fields would themselves be readonly");

        // ---- 位置参数 → 合成/继承属性；手动实现/特性/非普通参数一律闸门 ----
        // 派生 record 与基类位置属性同名的参数不再合成新属性（Roslyn 复用基类属性），
        // 记为基类转发参数：本部件不为其发字段/get/set，只经 : base(...) 转发。
        var members = new List<(IPropertySymbol Prop, ParameterSyntax Param, bool IsInherited)>();
        var paramSymbols = new HashSet<IParameterSymbol>(SymbolEqualityComparer.Default);
        foreach (var param in part.ParameterList!.Parameters)
        {
            if (param.AttributeLists.Count > 0)
                return Gate(type, $"positional parameter '{param.Identifier.ValueText}' carries "
                    + "attributes (parameter-target attributes have no home in the reshaped "
                    + "field/get_X/set_X final form)");
            if (param.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)
                    || m.IsKind(SyntaxKind.OutKeyword) || m.IsKind(SyntaxKind.InKeyword)))
                return Gate(type, $"positional parameter '{param.Identifier.ValueText}' uses a "
                    + "ref/in/out modifier that cannot be represented as a record member");

            var prop = ResolvePositionalProperty(type, param.Identifier.ValueText);
            if (prop == null)
                return Gate(type, $"positional parameter '{param.Identifier.ValueText}' does not "
                    + "bind to a property of the record");
            if (!PropertyFacts.IsPositionalRecordProperty(prop))
                return Gate(type, $"positional member '{prop.Name}' is manually implemented "
                    + "(explicit property declaration); mixing manual positional members with "
                    + "synthesized field/get_X/set_X materialization is not reshaped in M2");
            var isInherited = !SymbolEqualityComparer.Default.Equals(
                prop.ContainingType?.OriginalDefinition, type.OriginalDefinition);
            members.Add((prop, param, isInherited));

            if (model.GetDeclaredSymbol(param) is { } ps) paramSymbols.Add(ps);
        }

        // ---- 闸门①：主 ctor 参数被「非主 ctor 体、非 base(...) 白名单」处捕获 ----
        // 字段初始化器（跨全部部件）绑定位置参数时，reshape 后参数脱离作用域（CS0103）。
        // 方法体内 X 绑定的是属性（不是参数符号），天然不误伤；base(X, 0) 白名单豁免。
        foreach (var declRef in type.DeclaringSyntaxReferences)
        {
            if (declRef.GetSyntax() is not RecordDeclarationSyntax recPart) continue;
            var partModel = compilation.GetSemanticModel(recPart.SyntaxTree);
            foreach (var id in recPart.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (id.Ancestors().OfType<ParameterSyntax>().Any()) continue;
                if (id.Ancestors().OfType<PrimaryConstructorBaseTypeSyntax>().Any()) continue;
                if (partModel.GetSymbolInfo(id).Symbol is IParameterSymbol used
                    && paramSymbols.Contains(used))
                {
                    return Gate(type, $"primary constructor parameter '{id.Identifier.ValueText}' "
                        + "is captured outside of the base constructor argument list (e.g. by a "
                        + "field initializer or a member body); after the parameter list is "
                        + "removed the parameter is no longer in scope");
                }
            }
        }

        // ---- 闸门②：用户自定义 Deconstruct（任意形态，v1 不做合并）----
        if (type.GetMembers("Deconstruct").OfType<IMethodSymbol>()
            .Any(m => !m.IsImplicitlyDeclared))
        {
            return Gate(type, "a user-defined Deconstruct method already exists and cannot be "
                + "merged with the positional out-parameter materialization");
        }

        // ---- 闸门③（class）：用户自定义同形复制 ctor ----
        if (!isStruct
            && CloneCtorResolver.FindCopyConstructor(type) is { } copy
            && !copy.IsImplicitlyDeclared)
        {
            return Gate(type, "a user-defined single-parameter copy constructor already exists "
                + "and cannot be merged with the materialized protected copy constructor");
        }

        // ---- 闸门④：get_X/set_X 发射名与既有显式成员冲突（仅本类型新发的成员；
        // 基类转发参数的访问器由基类 reshape 发射，本类型不重复发、不参与撞名）----
        foreach (var (prop, _, isInherited) in members)
        {
            if (isInherited) continue;
            foreach (var accessorName in new[]
                         { AccessorsToMethods.GetterName(prop.Name),
                           AccessorsToMethods.SetterName(prop.Name) })
            {
                if (type.GetMembers(accessorName).Any(m => !m.IsImplicitlyDeclared))
                    return Gate(type, $"the record already declares an explicit member named "
                        + $"'{accessorName}' which collides with the materialized accessor method");
            }
        }

        // ---- 后备字段名（与 C11 同一确定性算法；只给本类型新发字段的自有属性算；
        // 撞名/value/reserved 由其消歧）。基类转发参数 FieldName 留空。----
        var ownMembers = members.Where(m => !m.IsInherited).ToList();
        var fieldNames = BackingFieldNaming.Assign(
            ownMembers.Select(m => (m.Prop, DeclName: m.Prop.Name)));
        var material = members
            .Select(m => new PositionalShapeMember
            {
                Property = m.Prop,
                Parameter = m.Param,
                FieldName = m.IsInherited ? "" : fieldNames[m.Prop],
                IsInherited = m.IsInherited,
            })
            .ToList();

        var primaryBase = part.BaseList?.Types
            .OfType<PrimaryConstructorBaseTypeSyntax>()
            .FirstOrDefault();

        var hasUserParameterless = type.InstanceConstructors.Any(c =>
            !c.IsStatic && c.Parameters.Length == 0 && !c.IsImplicitlyDeclared);
        bool emitParameterless;
        if (isStruct)
        {
            // 移除参数表后隐式无参 ctor 丢失：用户未显式声明就必须补发。
            emitParameterless = !hasUserParameterless;
        }
        else
        {
            emitParameterless = !hasUserParameterless && BaseHasAccessibleParameterless(type);
        }

        var shape = new PositionalShape
        {
            Type = type,
            Part = part,
            IsStruct = isStruct,
            Members = material,
            PrimaryBase = primaryBase,
            EmitParameterlessCtor = emitParameterless,
        };
        return (shape, null);
    }

    /// <summary>class 无参 ctor 可行性：基类存在从本类型可访问的无参 ctor
    /// （object 恒有；未 reshape 的位置基类只有主 ctor/复制 ctor → 不发）。</summary>
    private static bool BaseHasAccessibleParameterless(INamedTypeSymbol type)
    {
        var baseType = type.BaseType;
        if (baseType == null) return true;
        foreach (var c in baseType.InstanceConstructors)
        {
            if (c.Parameters.Length != 0) continue;
            switch (c.DeclaredAccessibility)
            {
                case Accessibility.Public:
                case Accessibility.Protected:
                case Accessibility.ProtectedOrInternal:
                    return true;
                case Accessibility.Internal:
                case Accessibility.ProtectedAndInternal:
                    if (SymbolEqualityComparer.Default.Equals(
                            c.ContainingAssembly, type.ContainingAssembly))
                        return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>位置参数 → 属性解析：先本类型自有，再沿基类链找同名实例属性
    /// （Roslyn 对派生 record 与可继承位置属性同名的参数不重复合成属性）。</summary>
    private static IPropertySymbol? ResolvePositionalProperty(INamedTypeSymbol type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var prop = t.GetMembers(name).OfType<IPropertySymbol>()
                .FirstOrDefault(p => !p.IsIndexer && !p.IsStatic);
            if (prop != null) return prop;
        }
        return null;
    }

    private static (PositionalShape?, ShapeGate) Gate(INamedTypeSymbol type, string reason)
        => (null, new ShapeGate(
            MemberDesugarDiagnosticCode.PositionalReshapeGate,
            RecordDiagnostics.PositionalReshapeBlocked(type.ToDisplayString(), reason)));
}
