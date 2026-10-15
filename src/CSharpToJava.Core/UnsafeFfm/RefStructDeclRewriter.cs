using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// R7：用户自定义 <c>ref struct</c> 声明处置（设计 D9，§4 组件表）。
/// <list type="bullet">
/// <item><c>ref struct</c> / <c>readonly ref struct</c> 声明经
/// <see cref="SemanticModel"/> 解析为 <see cref="INamedTypeSymbol"/>，
/// <c>IsRefLikeType=true</c> 且不在 BCL 白名单（System.Span&lt;T&gt;/
/// System.ReadOnlySpan&lt;T&gt;——其声明不可能出现在用户源码）时进入二分：</item>
/// <item><b>ref-only 语义承载</b>（存在 <c>ref</c> 字段、指针/函数指针字段或
/// ref-like（Span/ReadOnlySpan/其它 ref struct）实例字段——即去 <c>ref</c>
/// 后会成为非法普通 struct 的成员）→ 维持 UF050 Error，消息说明 Java 没有
/// 栈-only、不可装箱的类型；</item>
/// <item><b>纯数据型</b>（无上述成员，<c>ref</c> 仅是栈分配优化）→ 去掉
/// <c>ref</c> 修饰符降级为普通 struct（可装箱），发 UF052 Warning 说明语义
/// 等价性以引用语义为代价；</item>
/// <item>白名单类型的<b>使用点</b>原样不重写；签名位置（参数/返回）属
/// C01 已落地的 RefStructByRef&lt;T&gt; holder，R7 不处理；</item>
/// <item>using 站点由 R9 <see cref="RefStructUsingRewriter"/> 拥有。</item>
/// </list>
/// 本规则是 pass 基座（与 R1 同级语义）：随 pass 生效，不挂 EnableExtern。
/// 全部基于 ISymbol，禁止文本匹配；输出二次跑不再命中（去 <c>ref</c> 后符号
/// 不再是 ref-like），天然幂等。
/// </summary>
internal sealed class RefStructDeclRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly UnsafeFfmStatistics _statistics;
    private readonly OriginalSyntaxResolver _resolver;

    public RefStructDeclRewriter(
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

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        // 先递归：嵌套 ref struct 声明一并在本轮识别。
        var visited = (StructDeclarationSyntax)base.VisitStructDeclaration(node)!;

        var original = _resolver.Resolve(node);
        var symbol = original == null ? null : _model.GetDeclaredSymbol(original);
        if (symbol is not { IsRefLikeType: true }) return visited;
        if (IsWhitelistedSpan(symbol)) return visited;
        // C01 释放的 ref-out-in 支撑库（Cs2jByRef.RefStructByRef/ScopedByRef/
        // ReadOnlyScopedByRef）是有意随预构建 Java compat 发布的 holder，
        // TypeMappings 已逐个映射到 io.github.ningpp.compat.*，不是用户 ref struct。
        if (symbol.ContainingNamespace?.ToDisplayString() == "Cs2jByRef")
            return visited;

        // ref-only 语义承载：去 ref 会成为非法普通 struct（ref 字段/指针字段/
        // ref-like 字段），维持显式拒绝。
        if (HasRefOnlyMembers(symbol))
        {
            AddError(
                $"ref struct '{symbol.Name}' is a stack-only, non-boxable type; Java has no equivalent " +
                "(every Java type can be boxed to Object and no stack-only lifetime exists). It carries " +
                "ref-only members (ref/pointer/Span fields) that a plain struct cannot declare, so the " +
                "declaration is rejected. Replace it with a regular class/struct or route " +
                "Span<T>/ReadOnlySpan<T> use sites through the BCL whitelist; signature positions are " +
                "handled by the ref-out-in pass (RefStructByRef<T> holder).",
                node);
            return visited;
        }

        // 纯数据型 ref struct：ref 仅是栈分配优化，去 ref 降级为普通 struct
        // （可装箱，引用语义代价）。
        AddDowngrade(symbol, node);
        return RemoveRefModifier(visited);
    }

    /// <summary>
    /// ref-only 语义承载判定（全部基于 ISymbol，禁文本匹配）：任一字段
    /// （含自动实现属性的后备字段）是 <c>ref</c> 字段、指针/函数指针类型，
    /// 或 ref-like（Span/ReadOnlySpan/其它 ref struct）类型时，去 <c>ref</c>
    /// 后会成为非法普通 struct（CS8345 等），必须维持 UF050 Error。
    /// </summary>
    private static bool HasRefOnlyMembers(INamedTypeSymbol symbol)
    {
        foreach (var member in symbol.GetMembers())
        {
            if (member is not IFieldSymbol field) continue;
            if (field.RefKind != RefKind.None) return true;
            if (field.Type.IsRefLikeType) return true;
            if (field.Type.TypeKind is TypeKind.Pointer or TypeKind.FunctionPointer) return true;
        }
        return false;
    }

    /// <summary>
    /// 从修饰符表移除 <c>ref</c> keyword，其余修饰符顺序与 trivia 原始形态
    /// 不变：ref 的 trivia 顺延并入其后的保留修饰符前导（ref 为末位时并入
    /// 前一保留修饰符尾部）；ref 是唯一修饰符时并入 <c>struct</c> 关键字前导。
    /// </summary>
    private static StructDeclarationSyntax RemoveRefModifier(StructDeclarationSyntax node)
    {
        var kept = new List<SyntaxToken>();
        var pendingTrivia = new List<SyntaxTrivia>();

        foreach (var token in node.Modifiers)
        {
            if (token.IsKind(SyntaxKind.RefKeyword))
            {
                pendingTrivia.AddRange(token.LeadingTrivia);
                pendingTrivia.AddRange(token.TrailingTrivia);
                continue;
            }

            var keptToken = token;
            if (pendingTrivia.Count > 0)
            {
                keptToken = keptToken.WithLeadingTrivia(pendingTrivia.Concat(keptToken.LeadingTrivia));
                pendingTrivia.Clear();
            }

            kept.Add(keptToken);
        }

        if (pendingTrivia.Count > 0 && kept.Count > 0)
        {
            kept[^1] = kept[^1].WithTrailingTrivia(
                kept[^1].TrailingTrivia.Concat(pendingTrivia));
        }

        var result = node.WithModifiers(SyntaxFactory.TokenList(kept));
        if (kept.Count == 0 && pendingTrivia.Count > 0)
        {
            result = result.WithKeyword(result.Keyword.WithLeadingTrivia(
                pendingTrivia.Concat(result.Keyword.LeadingTrivia)));
        }

        return result;
    }

    /// <summary>BCL 白名单：System.Span&lt;T&gt;/System.ReadOnlySpan&lt;T&gt;。</summary>
    private static bool IsWhitelistedSpan(INamedTypeSymbol symbol)
    {
        if (!symbol.IsGenericType || symbol.TypeArguments.Length != 1) return false;
        var definition = symbol.OriginalDefinition.ToDisplayString();
        return definition is "System.Span<T>" or "System.ReadOnlySpan<T>";
    }

    /// <summary>诊断位置：经 resolver 映射回原始树取行/列，不可用时退回 (1,1)。</summary>
    private (int Line, int Character) ResolvePosition(SyntaxNode node)
    {
        var positionNode = _resolver.Resolve(node) ?? node;
        try
        {
            var span = positionNode.GetLocation().GetLineSpan();
            return (span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
        }
        catch
        {
            return (1, 1);
        }
    }

    private void AddError(string message, SyntaxNode node)
    {
        var (line, character) = ResolvePosition(node);
        Diagnostics.Add(new UnsafeFfmDiagnostic(
            UnsafeFfmSeverity.Error,
            UnsafeFfmDiagnosticCode.UF050,
            message,
            null, line, character));
        _statistics.Rejections++;
        _statistics.Uf050Rejections++;
    }

    /// <summary>
    /// 纯数据型 ref struct 降级告警：Java 无栈-only/不可装箱类型，已去
    /// <c>ref</c> 降级为普通 struct（可装箱），语义等价性以引用语义为代价。
    /// </summary>
    private void AddDowngrade(INamedTypeSymbol symbol, SyntaxNode node)
    {
        var (line, character) = ResolvePosition(node);
        Diagnostics.Add(new UnsafeFfmDiagnostic(
            UnsafeFfmSeverity.Warning,
            UnsafeFfmDiagnosticCode.UF052,
            $"ref struct '{symbol.Name}' is a stack-only, non-boxable type in C#; Java has no equivalent " +
            "(every Java type can be boxed to Object and no stack-only lifetime exists). It carries no " +
            "ref-only members (no ref/pointer/Span fields), so the 'ref' modifier is dropped and it is " +
            "lowered to a plain struct — boxable, trading ref-only allocation semantics for reference " +
            "semantics at use sites.",
            null, line, character));
        _statistics.Uf052Downgrades++;
    }
}
