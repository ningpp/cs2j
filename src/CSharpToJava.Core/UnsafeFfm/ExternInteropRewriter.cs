using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Analysis;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// R8：extern 互操作显式拒绝（设计 D8，§4 组件表；M4 裁决：白名单
/// facade 归一作为独立 opt-in 子段后续交付，本里程碑不消费
/// ExternWhitelistPath，一切 extern 默认显式拒绝）。
/// <list type="bullet">
/// <item>extern-like 方法（<see cref="InteropBoundaryFacts.IsExternLikeMethod"/>：
/// extern 修饰符或 [DllImport]/[LibraryImport]）→ UF060 Error，消息含
/// 「平台隔离通道（C42）」指引；永不发静默 stub；</item>
/// <item>方法体内/站点对 Marshal.* / NativeLibrary.* 的调用与成员访问、
/// SafeHandle（含派生）/HandleRef 类型的使用（字段/局部/参数/构造站点）
/// → UF070 Error，指引与 CS2J3202 相同隔离去向；</item>
/// <item>UF061 布局判定仅保留常量与 <see cref="MarshalLayoutSkeleton"/>
/// 最小骨架，无白名单路径时 pass 不触发 UF061。</item>
/// </list>
/// 判定全部基于 SemanticModel/ISymbol。本 rewriter M4 只诊断不改写
/// （拒绝即整文回滚）；facade 时代在本文件加归一形态。
/// </summary>
internal sealed class ExternInteropRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly UnsafeFfmStatistics _statistics;
    private readonly OriginalSyntaxResolver _resolver;

    private readonly HashSet<string> _reported = new();

    public ExternInteropRewriter(
        SemanticModel model,
        UnsafeFfmStatistics statistics,
        OriginalSyntaxResolver resolver)
    {
        _model = model;
        _statistics = statistics;
        _resolver = resolver;
    }

    public List<UnsafeFfmDiagnostic> Diagnostics { get; } = new();

    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == UnsafeFfmSeverity.Error);

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        ScanType(node);
        return base.VisitClassDeclaration(node);
    }

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        ScanType(node);
        return base.VisitStructDeclaration(node);
    }

    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        ScanType(node);
        return base.VisitInterfaceDeclaration(node);
    }

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        ScanType(node);
        return base.VisitRecordDeclaration(node);
    }

    /// <summary>
    /// 扫描类型<b>直接成员</b>（嵌套类型自身被 Visit 单独扫描，避免重复）：
    /// extern-like 方法 → UF060；成员内互操作 API/类型站点 → UF070。
    /// </summary>
    private void ScanType(TypeDeclarationSyntax node)
    {
        var original = _resolver.Resolve(node);
        var originalType = original as TypeDeclarationSyntax;

        foreach (var member in node.Members)
        {
            if (member is TypeDeclarationSyntax) continue;

            var originalMember = _resolver.Resolve(member);
            if (originalMember == null)
            {
                // 无法映射回原始模型树时（极端插入形态）只做纯语法 extern
                // 判定，绝不把重建节点传给 SemanticModel（否则抛异常）。
                if (member is MethodDeclarationSyntax unresolvedMethod
                    && unresolvedMethod.Modifiers.Any(SyntaxKind.ExternKeyword))
                {
                    AddError(
                        UnsafeFfmDiagnosticCode.UF060,
                        $"extern-like method '{unresolvedMethod.Identifier.ValueText}' (extern modifier) " +
                        "is not on an FFM whitelist; all extern interop is rejected by default. Port it " +
                        "through the 平台隔离通道（C42） or the future opt-in FFM whitelist " +
                        "facade; no silent stubs are emitted.",
                        unresolvedMethod.Identifier);
                }

                continue;
            }

            if (member is MethodDeclarationSyntax method
                && originalMember is MethodDeclarationSyntax originalMethod
                && InteropBoundaryFacts.IsExternLikeMethod(_model, originalMethod))
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF060,
                    $"extern-like method '{method.Identifier.ValueText}' (extern modifier or " +
                    "[DllImport]/[LibraryImport] attribute) is not on an FFM whitelist; all extern " +
                    "interop is rejected by default. Port it through the 平台隔离通道（C42） " +
                    "or the future opt-in FFM whitelist facade; no silent stubs are emitted.",
                    originalMethod.Identifier);
            }

            ScanMemberSites(originalMember);
        }

        _ = originalType;
    }

    /// <summary>扫描单个成员范围内的 Marshal/NativeLibrary/SafeHandle/HandleRef 站点。</summary>
    private void ScanMemberSites(SyntaxNode member)
    {
        foreach (var invocation in member.DescendantNodesAndSelf()
                     .OfType<InvocationExpressionSyntax>())
        {
            var symbol = _model.GetSymbolInfo(invocation).Symbol;
            if (TryGetInteropContainer(symbol, out var container))
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF070,
                    $"use of '{container}.{symbol!.Name}' is rejected: System.Runtime.InteropServices " +
                    "Marshal/NativeLibrary APIs have no Java equivalent. Isolate the call behind the " +
                    "platform isolation channel (C42) (same destination as CS2J3202).",
                    invocation);
            }
        }

        // 方法组/属性形态（非调用）：父节点是 invocation 时由调用分支覆盖，不重复报。
        foreach (var memberAccess in member.DescendantNodesAndSelf()
                     .OfType<MemberAccessExpressionSyntax>()
                     .Where(m => m.Parent is not InvocationExpressionSyntax))
        {
            var symbol = _model.GetSymbolInfo(memberAccess).Symbol;
            if (TryGetInteropContainer(symbol, out var container))
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF070,
                    $"use of '{container}.{symbol!.Name}' is rejected; isolate it behind the platform " +
                    "isolation channel (C42) (same destination as CS2J3202).",
                    memberAccess);
            }
        }

        foreach (var creation in member.DescendantNodesAndSelf()
                     .OfType<ObjectCreationExpressionSyntax>())
        {
            var type = _model.GetTypeInfo(creation).Type;
            if (IsSafeHandleOrHandleRef(type))
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF070,
                    $"construction of '{type!.ToDisplayString()}' is rejected: SafeHandle/HandleRef are " +
                    "runtime interop ownership types with no Java equivalent. Isolate the site behind " +
                    "the platform isolation channel (C42).",
                    creation);
            }
        }

        // 类型站点（字段/局部/参数/返回）：对象创建表达式内的 Type 由创建分支覆盖。
        // 限定名/可空类型的内部名也是 TypeSyntax，只扫最外层节点；表达式
        // 上下文（参数/成员访问右侧/二元运算等）中的 NameSyntax 不属类型站点，
        // 避免同一符号在使用表达式处被重复计数。
        foreach (var typeSyntax in member.DescendantNodesAndSelf().OfType<TypeSyntax>())
        {
            if (typeSyntax.Parent is TypeSyntax) continue;
            if (IsExpressionContext(typeSyntax.Parent)) continue;
            if (IsInsideObjectCreationType(typeSyntax)) continue;
            var type = _model.GetTypeInfo(typeSyntax).Type;
            if (IsSafeHandleOrHandleRef(type))
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF070,
                    $"use of interop ownership type '{type!.ToDisplayString()}' is rejected: " +
                    "SafeHandle/HandleRef encode runtime interop lifetime. Isolate the site behind " +
                    "the platform isolation channel (C42) (same destination as CS2J3202).",
                    typeSyntax);
            }
        }
    }

    private static bool TryGetInteropContainer(ISymbol? symbol, out string container)
    {
        container = string.Empty;
        var name = symbol?.ContainingType?.ToDisplayString();
        if (name == "System.Runtime.InteropServices.Marshal")
        {
            container = "Marshal";
            return true;
        }

        if (name == "System.Runtime.InteropServices.NativeLibrary")
        {
            container = "NativeLibrary";
            return true;
        }

        return false;
    }

    /// <summary>SafeHandle（含派生类型链）或 HandleRef（精确）。</summary>
    private static bool IsSafeHandleOrHandleRef(ITypeSymbol? type)
    {
        if (type == null) return false;
        if (type.OriginalDefinition.ToDisplayString() == "System.Runtime.InteropServices.HandleRef")
        {
            return true;
        }

        var current = type;
        while (current != null)
        {
            if (current.OriginalDefinition.ToDisplayString() == "System.Runtime.InteropServices.SafeHandle")
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }

    /// <summary>
    /// 父节点是否把该 TypeSyntax 当作表达式使用（而非类型站点）。
    /// 默认未知上下文按类型站点处理（拒绝优先，宁多报不漏报）。
    /// </summary>
    private static bool IsExpressionContext(SyntaxNode? parent) => parent switch
    {
        ArgumentSyntax => true,
        AttributeArgumentSyntax => true,
        MemberAccessExpressionSyntax => true,
        MemberBindingExpressionSyntax => true,
        InvocationExpressionSyntax => true,
        BinaryExpressionSyntax => true,
        AssignmentExpressionSyntax => true,
        AwaitExpressionSyntax => true,
        ReturnStatementSyntax => true,
        ThrowExpressionSyntax => true,
        ThrowStatementSyntax => true,
        ArrowExpressionClauseSyntax => true,
        ExpressionStatementSyntax => true,
        ConditionalExpressionSyntax => true,
        ConditionalAccessExpressionSyntax => true,
        PostfixUnaryExpressionSyntax => true,
        PrefixUnaryExpressionSyntax => true,
        InterpolationSyntax => true,
        InitializerExpressionSyntax => true,
        LockStatementSyntax => true,
        YieldStatementSyntax => true,
        ElementAccessExpressionSyntax => true,
        ParenthesizedExpressionSyntax => true,
        EqualsValueClauseSyntax => true,
        SwitchExpressionSyntax => true,
        SwitchExpressionArmSyntax => true,
        WhileStatementSyntax => true,
        DoStatementSyntax => true,
        IfStatementSyntax => true,
        ForStatementSyntax => true,
        RefExpressionSyntax => true,
        TupleExpressionSyntax => true,
        ImplicitElementAccessSyntax => true,
        _ => false,
    };

    private static bool IsInsideObjectCreationType(TypeSyntax typeSyntax)
    {
        var creation = typeSyntax.Ancestors().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault();
        return creation != null && creation.Type.DescendantNodesAndSelf().Contains(typeSyntax);
    }

    private void AddError(UnsafeFfmDiagnosticCode code, string message, SyntaxNode node)
        => AddError(code, message, node.GetLocation());

    private void AddError(
        UnsafeFfmDiagnosticCode code, string message, SyntaxToken token)
        => AddError(code, message, token.GetLocation());

    private void AddError(
        UnsafeFfmDiagnosticCode code, string message, Location location)
    {
        var line = 1;
        var character = 1;
        try
        {
            var span = location.GetLineSpan();
            line = span.StartLinePosition.Line + 1;
            character = span.StartLinePosition.Character + 1;
        }
        catch
        {
            // 行号不可用时退回 (1,1)。
        }

        // 同位置同码去重（不同成员范围可能扫到嵌套节点）。
        if (!_reported.Add($"{code}:{line}:{character}")) return;

        Diagnostics.Add(new UnsafeFfmDiagnostic(
            UnsafeFfmSeverity.Error, code, message, null, line, character));
        _statistics.Rejections++;
        switch (code)
        {
            case UnsafeFfmDiagnosticCode.UF060:
                _statistics.Uf060Rejections++;
                break;
            case UnsafeFfmDiagnosticCode.UF070:
                _statistics.Uf070Rejections++;
                break;
        }
    }
}
