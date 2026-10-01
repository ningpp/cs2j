using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// C# 12 InlineArray 盘点拒绝（设计 D11，§4 组件表）。struct 经
/// <see cref="SemanticModel"/> 识别到
/// <c>[System.Runtime.CompilerServices.InlineArray]</c> 特性（全名称，
/// 含 using 别名/限定形态）时发 UF051 Error；不自动改写——inline array
/// 依赖 ABI 内联长度与栈/固定缓冲布局，Java 无对应表达。
/// 本规则是 pass 基座（随 pass 生效，不挂里程碑开关）。
/// </summary>
internal sealed class InlineArrayDetector : CSharpSyntaxRewriter
{
    private const string InlineArrayAttributeName =
        "System.Runtime.CompilerServices.InlineArrayAttribute";

    private readonly SemanticModel _model;
    private readonly UnsafeFfmStatistics _statistics;
    private readonly OriginalSyntaxResolver _resolver;

    public InlineArrayDetector(
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
        var visited = (StructDeclarationSyntax)base.VisitStructDeclaration(node)!;

        var original = _resolver.Resolve(node);
        var symbol = original == null ? null : _model.GetDeclaredSymbol(original);
        if (symbol == null || !HasInlineArrayAttribute(symbol)) return visited;

        AddError(
            $"struct '{symbol.Name}' is annotated with [System.Runtime.CompilerServices.InlineArray], " +
            "which inlines a fixed-length sequence into the ABI layout; Java has no equivalent form. " +
            "Inline arrays are rejected (C# 12 inventory); rewrite it as a regular array field manually.",
            node);

        return visited;
    }

    private static bool HasInlineArrayAttribute(INamedTypeSymbol symbol)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == InlineArrayAttributeName)
            {
                return true;
            }
        }

        return false;
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
            UnsafeFfmDiagnosticCode.UF051,
            message,
            null, line, character));
        _statistics.Rejections++;
        _statistics.Uf051Rejections++;
    }
}
