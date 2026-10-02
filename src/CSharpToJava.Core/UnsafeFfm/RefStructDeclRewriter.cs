using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// R7：用户自定义 <c>ref struct</c> 声明拒绝（设计 D9，§4 组件表）。
/// <list type="bullet">
/// <item><c>ref struct</c> / <c>readonly ref struct</c> 声明经
/// <see cref="SemanticModel"/> 解析为 <see cref="INamedTypeSymbol"/>，
/// <c>IsRefLikeType=true</c> 且不在 BCL 白名单（System.Span&lt;T&gt;/
/// System.ReadOnlySpan&lt;T&gt;——其声明不可能出现在用户源码）时发
/// UF050 Error，消息说明 Java 没有栈-only、不可装箱的类型；</item>
/// <item>白名单类型的<b>使用点</b>原样不重写；签名位置（参数/返回）属
/// C01 已落地的 RefStructByRef&lt;T&gt; holder，R7 不处理；</item>
/// <item>using 站点由 R9 <see cref="RefStructUsingRewriter"/> 拥有。</item>
/// </list>
/// 本规则是 pass 基座（与 R1 同级语义）：随 pass 生效，不挂 EnableExtern。
/// 全部基于 ISymbol，禁止文本匹配。
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

        AddError(
            $"ref struct '{symbol.Name}' is a stack-only, non-boxable type; Java has no equivalent " +
            "(every Java type can be boxed to Object and no stack-only lifetime exists). User-defined " +
            "ref struct declarations are therefore rejected. Replace it with a regular class/struct or " +
            "route Span<T>/ReadOnlySpan<T> use sites through the BCL whitelist; signature positions are " +
            "handled by the ref-out-in pass (RefStructByRef<T> holder).",
            node);

        return visited;
    }

    /// <summary>BCL 白名单：System.Span&lt;T&gt;/System.ReadOnlySpan&lt;T&gt;。</summary>
    private static bool IsWhitelistedSpan(INamedTypeSymbol symbol)
    {
        if (!symbol.IsGenericType || symbol.TypeArguments.Length != 1) return false;
        var definition = symbol.OriginalDefinition.ToDisplayString();
        return definition is "System.Span<T>" or "System.ReadOnlySpan<T>";
    }

    private void AddError(string message, SyntaxNode node)
    {
        var line = 1;
        var character = 1;
        var positionNode = _resolver.Resolve(node) ?? node;
        try
        {
            var span = positionNode.GetLocation().GetLineSpan();
            line = span.StartLinePosition.Line + 1;
            character = span.StartLinePosition.Character + 1;
        }
        catch
        {
            // 行号不可用时退回 (1,1)。
        }

        Diagnostics.Add(new UnsafeFfmDiagnostic(
            UnsafeFfmSeverity.Error,
            UnsafeFfmDiagnosticCode.UF050,
            message,
            null, line, character));
        _statistics.Rejections++;
        _statistics.Uf050Rejections++;
    }
}
