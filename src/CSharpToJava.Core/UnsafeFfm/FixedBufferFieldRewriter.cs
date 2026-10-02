using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// R2：fixed 字段缓冲区改写（设计 D3，§4 组件表）。
/// <list type="bullet">
/// <item>结构体中的固定大小缓冲区字段（<c>fixed byte buf[256];</c>）归一为
/// 普通数组字段 <c>byte[] buf = new byte[256];</c>：元素类型与声明常量
/// 长度均取自原声明（语法实测：fixed 是 FieldDeclaration 的一个
/// FixedKeyword 修饰符，长度在 VariableDeclarator.ArgumentList
/// （BracketedArgumentList）中；符号为 IsFixedSizeBuffer 的 IFieldSymbol）。
/// 所有 <c>s.buf[i]</c> 用法语法不变、语义随数组天然成立，本规则不碰。</item>
/// <item>UF031（两条件缺一不可）：① 所在类型显式带
/// [StructLayout(LayoutKind.Sequential/Explicit)] 或字段带 [FieldOffset]；
/// ② 该类型自身含 extern/DllImport/LibraryImport 成员（互操作关联）。
/// 纯 Sequential 无 extern 必须正常改写。</item>
/// </list>
/// 判定全部基于 SemanticModel/ISymbol。产出的数组字段不含 fixed 形态，天然幂等。
/// </summary>
internal sealed class FixedBufferFieldRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly UnsafeFfmStatistics _statistics;
    private readonly OriginalSyntaxResolver _resolver;

    public FixedBufferFieldRewriter(
        SemanticModel model,
        UnsafeFfmStatistics statistics,
        OriginalSyntaxResolver resolver)
    {
        _model = model;
        _statistics = statistics;
        _resolver = resolver;
    }

    /// <summary>已改写的 fixed 缓冲区字段符号（供 R3 判定 fixed(byte* p=字段) 走数组路径）。</summary>
    public HashSet<IFieldSymbol> RewrittenFields { get; } = new();

    public List<UnsafeFfmDiagnostic> Diagnostics { get; } = new();

    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == UnsafeFfmSeverity.Error);

    /// <summary>本次运行中确实改写过 fixed 字段的 struct（需补显式无参 ctor，规避 CS8983）。</summary>
    private readonly HashSet<StructDeclarationSyntax> _structsWithRewrittenFields = new();

    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        if (!node.Modifiers.Any(SyntaxKind.FixedKeyword)) return node;

        var typeDecl = node.Parent as TypeDeclarationSyntax;
        var originalTypeDecl = typeDecl == null ? null : _resolver.Resolve(typeDecl);
        var typeSymbol = originalTypeDecl == null ? null : _model.GetDeclaredSymbol(originalTypeDecl);
        var layoutLocked = typeSymbol != null && HasLayoutLock(typeSymbol, node);
        var interopAssociated = typeSymbol != null && HasInteropMembers(typeSymbol);

        var newVariables = new List<VariableDeclaratorSyntax>();
        var anyRewritten = false;

        foreach (var variable in node.Declaration.Variables)
        {
            var fieldSymbol = (_resolver.Resolve(variable) is { } originalVariable
                ? _model.GetDeclaredSymbol(originalVariable)
                : null) as IFieldSymbol;

            if (layoutLocked && interopAssociated)
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF031,
                    $"fixed-size buffer field '{variable.Identifier.ValueText}' is in a type with an explicit " +
                    "StructLayout(Sequential/Explicit)/FieldOffset layout that is associated with extern/" +
                    "DllImport/LibraryImport interop members; the managed-array rewrite would break the " +
                    "marshalled layout (layout lock). Route it through the M4 FFM whitelist or rewrite manually.",
                    variable);
                // 拒绝优先：该声明保持原样（整文件随后回滚）。
                newVariables.Add(variable);
                continue;
            }

            var countExpression = variable.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
            if (countExpression == null)
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF010,
                    $"fixed-size buffer field '{variable.Identifier.ValueText}' has no recognizable constant " +
                    "length (internal rule gap).",
                    variable);
                newVariables.Add(variable);
                continue;
            }

            var elementType = node.Declaration.Type;
            var newInitializer = EqualsValueClause(
                ArrayCreationExpression(
                    ArrayType(ParseTypeName(elementType.ToString()))
                        .WithRankSpecifiers(SingletonList(ArrayRankSpecifier(
                            SingletonSeparatedList<ExpressionSyntax>(countExpression))))));

            newVariables.Add(variable
                .WithArgumentList(null)
                .WithInitializer(newInitializer));

            if (fieldSymbol != null) RewrittenFields.Add(fieldSymbol);
            _statistics.FixedBufferFieldsRewritten++;
            anyRewritten = true;
        }

        if (!anyRewritten) return node;

        // CS8983：struct 字段初始值设定项要求显式无参构造函数，稍后 VisitStructDeclaration 补。
        if (node.Parent is StructDeclarationSyntax structNode)
        {
            _structsWithRewrittenFields.Add(structNode);
        }

        var arrayFieldType = ArrayType(node.Declaration.Type)
            .WithRankSpecifiers(SingletonList(ArrayRankSpecifier(
                SingletonSeparatedList<ExpressionSyntax>(OmittedArraySizeExpression()))));

        return node
            .WithModifiers(RemoveFixedKeyword(node.Modifiers))
            .WithDeclaration(node.Declaration
                .WithType(arrayFieldType)
                .WithVariables(SeparatedList(newVariables)));
    }

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        var visited = (StructDeclarationSyntax)base.VisitStructDeclaration(node)!;
        if (!_structsWithRewrittenFields.Contains(node)) return visited;

        var hasParameterlessInstanceCtor = visited.Members
            .OfType<ConstructorDeclarationSyntax>()
            .Any(c => c.ParameterList.Parameters.Count == 0
                && !c.Modifiers.Any(SyntaxKind.StaticKeyword));
        if (hasParameterlessInstanceCtor) return visited;

        var constructor = ConstructorDeclaration(visited.Identifier)
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithParameterList(ParameterList())
            .WithBody(Block())
            .NormalizeWhitespace()
            // 追加在成员末尾：不改变任何原有成员的子节点索引，保证跨树路径映射有效。
            .WithLeadingTrivia(visited.Members.First().GetLeadingTrivia());

        return visited.WithMembers(visited.Members.Add(constructor));
    }

    /// <summary>
    /// 条件①：类型显式 StructLayout 且 LayoutKind 为 Sequential(0)/Explicit(2)
    /// （Auto(3) 不算；无参 StructLayout 即 Sequential），或本字段任一变量
    /// 的字段符号带 [FieldOffset]。
    /// </summary>
    private bool HasLayoutLock(INamedTypeSymbol typeSymbol, FieldDeclarationSyntax field)
    {
        foreach (var attribute in typeSymbol.GetAttributes())
        {
            if (attribute.AttributeClass?.Name != "StructLayoutAttribute") continue;
            if (attribute.ConstructorArguments.Length == 0) return true;
            if (attribute.ConstructorArguments[0].Value is int kind && kind is 0 or 2) return true;
        }

        foreach (var variable in field.Declaration.Variables)
        {
            if (_resolver.Resolve(variable) is not { } originalVariable
                || _model.GetDeclaredSymbol(originalVariable) is not IFieldSymbol fieldSymbol) continue;
            foreach (var attribute in fieldSymbol.GetAttributes())
            {
                if (attribute.AttributeClass?.Name == "FieldOffsetAttribute") return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 条件②：类型自身的方法成员带 DllImport/LibraryImport 属性，或为
    /// extern 方法（extern 经声明语法识别，IMethodSymbol 无 IsExtern 属性——
    /// 语法实测结论）。
    /// </summary>
    private static bool HasInteropMembers(INamedTypeSymbol typeSymbol)
    {
        foreach (var member in typeSymbol.GetMembers())
        {
            if (member is not IMethodSymbol method) continue;

            foreach (var attribute in method.GetAttributes())
            {
                var name = attribute.AttributeClass?.Name;
                if (name is "DllImportAttribute" or "LibraryImportAttribute") return true;
            }

            foreach (var reference in method.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is MethodDeclarationSyntax methodSyntax
                    && methodSyntax.Modifiers.Any(SyntaxKind.ExternKeyword))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 从修饰符表移除 fixed keyword，其 trivia 顺延并入下一个保留修饰符；
    /// fixed 为末位时并入前一修饰符尾部。
    /// </summary>
    private static SyntaxTokenList RemoveFixedKeyword(SyntaxTokenList modifiers)
    {
        var kept = new List<SyntaxToken>();
        var pendingTrivia = new List<SyntaxTrivia>();

        foreach (var token in modifiers)
        {
            if (token.IsKind(SyntaxKind.FixedKeyword))
            {
                pendingTrivia.AddRange(token.LeadingTrivia);
                pendingTrivia.AddRange(token.TrailingTrivia);
                continue;
            }

            var current = token;
            if (pendingTrivia.Count > 0)
            {
                current = current.WithLeadingTrivia(pendingTrivia.Concat(current.LeadingTrivia));
                pendingTrivia.Clear();
            }

            kept.Add(current);
        }

        if (pendingTrivia.Count > 0 && kept.Count > 0)
        {
            kept[^1] = kept[^1].WithTrailingTrivia(
                kept[^1].TrailingTrivia.Concat(pendingTrivia));
        }

        return TokenList(kept);
    }

    private void AddError(UnsafeFfmDiagnosticCode code, string message, SyntaxNode node)
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
            // 脱离语法树的节点无法计算行号时退回 (1,1)。
        }

        Diagnostics.Add(new UnsafeFfmDiagnostic(
            UnsafeFfmSeverity.Error, code, message, null, line, character));
        _statistics.Rejections++;
        if (code == UnsafeFfmDiagnosticCode.UF031) _statistics.Uf031Rejections++;
    }
}
