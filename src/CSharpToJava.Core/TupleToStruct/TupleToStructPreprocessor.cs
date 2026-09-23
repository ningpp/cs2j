using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using System.Text.RegularExpressions;

namespace CSharpToJava.Core.TupleToStruct;

/// <summary>
/// C# 值元组消除预处理器。把源码中所有 tuple 语法（元组字面量、元组类型、
/// 解构、ValueTuple 泛型/工厂）替换为对生成 readonly struct 的引用：
/// <list type="bullet">
/// <item>元组类型/字面量 → global::Cs2jTuples.TupleXxx(...)；</item>
/// <item>元素名访问 t.x → t.ItemN（8+ 元组经 Rest 链）；</item>
/// <item>解构声明/赋值/out var 解构/foreach 解构展开为显式语句；</item>
/// <item>形状在全部文件间按结构去重，生成单一 struct 集合。</item>
/// </list>
/// 输出前做语法校验，任何情况下不产出语法损坏的中间 C#。
/// </summary>
public sealed partial class TupleToStructPreprocessor
{
    private static readonly SyntaxAnnotation s_rewriteAnnotation = new("Cs2jTupleRewrite");
    private static readonly SyntaxAnnotation s_spliceAnnotation = new("Cs2jTupleSplice");

    private readonly TupleShapeRegistry _registry = new();
    private readonly TupleToStructOptions _options;

    public TupleToStructPreprocessor(TupleToStructOptions? options = null)
    {
        _options = options ?? new TupleToStructOptions();
    }

    /// <summary>生成 struct 集合文件内容；没有任何元组时返回 null。</summary>
    public string? GenerateStructFile()
        => _registry.Shapes.Count == 0
            ? null
            : TupleStructFileContent.Generate(_registry.Shapes.ToList(), _options.NamespaceName);

    public IReadOnlyCollection<string> StructNames => _registry.Shapes.Select(s => s.Name).ToArray();

    /// <summary>
    /// 每个被改写文件用到的形状键（含嵌套/尾部形状闭包）。按编译单元生成 struct 文件时
    /// 需要它：一个源文件可能被多个程序集分别编译（共享源码），而元素类型是 internal
    /// 时只能各自生成一份；只生成本编译单元真正需要的形状，才能避免引用到看不见的类型。
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ShapesByFile =>
        _shapesByFile.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.Select(shape => shape.Key).ToArray(),
            StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, HashSet<TupleShape>> _shapesByFile =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取指定形状集合的闭包（嵌套形状 + Rest 尾部），并保持注册顺序稳定。</summary>
    public IReadOnlyList<string> CloseShapeKeys(IEnumerable<string> shapeKeys)
    {
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(shapeKeys);
        while (pending.Count > 0)
        {
            var key = pending.Pop();
            if (!closure.Add(key))
            {
                continue;
            }

            var shape = _registry.FindByKey(key);
            if (shape == null)
            {
                continue;
            }

            foreach (var element in shape.Elements.Where(e => e.IsTuple))
            {
                pending.Push(element.NestedShape!.Key);
            }

            if (shape.Tail != null)
            {
                pending.Push(shape.Tail.Key);
            }
        }

        return _registry.Shapes
            .Where(shape => closure.Contains(shape.Key))
            .Select(shape => shape.Key)
            .ToArray();
    }

    /// <summary>该形状的元素类型是否全部 public（决定生成 public 还是 internal struct）。</summary>
    public bool IsShapePubliclyAccessible(string shapeKey) =>
        _registry.FindByKey(shapeKey)?.IsPubliclyAccessible ?? false;

    /// <summary>按形状键筛选生成 struct 文件内容；没有匹配形状时返回 null。</summary>
    public string? GenerateStructFile(IEnumerable<string> shapeKeys)
    {
        var keys = new HashSet<string>(shapeKeys, StringComparer.Ordinal);
        var shapes = _registry.Shapes.Where(shape => keys.Contains(shape.Key)).ToList();
        return shapes.Count == 0
            ? null
            : TupleStructFileContent.Generate(shapes, _options.NamespaceName);
    }

    public TupleToStructFileResult RewriteFile(
        string sourceCode,
        SemanticModel semanticModel,
        TupleToStructStatistics statistics,
        List<TupleToStructDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(diagnostics);

        statistics.FilesScanned++;

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();

        if (!IsTupleDirty(root))
        {
            statistics.FilesSkippedClean++;
            return new TupleToStructFileResult { OutputCode = sourceCode, Changed = false };
        }

        var rewriter = new TupleSyntaxRewriter(
            semanticModel, _registry, _options.NamespaceName, statistics, diagnostics,
            _shapesByFile, semanticModel.SyntaxTree.FilePath);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (!rewriter.Changed)
        {
            statistics.FilesSkippedClean++;
            return new TupleToStructFileResult { OutputCode = sourceCode, Changed = false };
        }

        newRoot = (CompilationUnitSyntax)new StatementSplicer(rewriter.Expansions).Visit(newRoot)!;

        var outputCode = FormatAndValidate(newRoot);
        if (outputCode == null)
        {
            statistics.FilesSkippedClean++;
            diagnostics.Add(new TupleToStructDiagnostic(
                TupleToStructSeverity.Error,
                "tuple rewrite produced invalid C# syntax; the original file was preserved."));
            return new TupleToStructFileResult { OutputCode = sourceCode, Changed = false };
        }

        statistics.FilesTransformed++;

        var residual = CountResidualTupleSyntax(outputCode);
        if (residual > 0)
        {
            diagnostics.Add(new TupleToStructDiagnostic(
                TupleToStructSeverity.Warning,
                $"{residual} tuple syntax node(s) could not be converted and were left " +
                "unchanged (see the warnings above for the reasons)."));
        }

        return new TupleToStructFileResult { OutputCode = outputCode, Changed = true };
    }

    /// <summary>文件级快速脏检查：不含任何元组相关语法时直接原样拷贝。</summary>
    internal static bool IsTupleDirty(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodesAndSelf())
        {
            if (node is TupleTypeSyntax or TupleExpressionSyntax or ParenthesizedVariableDesignationSyntax)
            {
                return true;
            }

            if (node is GenericNameSyntax generic && generic.Identifier.ValueText == "ValueTuple")
            {
                return true;
            }

            if (node is IdentifierNameSyntax identifier)
            {
                var text = identifier.Identifier.ValueText;
                if (text is "ValueTuple" or "ITuple" or "TupleElementNames")
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static int CountResidualTupleSyntax(string code)
    {
        try
        {
            var parsed = CSharpSyntaxTree.ParseText(code);
            return CountResidualTupleSyntax(parsed.GetRoot());
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static int CountResidualTupleSyntax(SyntaxNode root) =>
        root.DescendantNodesAndSelf().Count(n =>
            n is TupleTypeSyntax or TupleExpressionSyntax
            || (n is GenericNameSyntax g && g.Identifier.ValueText == "ValueTuple"));

    /// <summary>
    /// 优先只格式化被改写的片段；候选依次为：局部格式化、整文件格式化、原始文本。
    /// 每个候选重新解析校验，返回 null 表示全部失败。
    /// </summary>
    private static string? FormatAndValidate(CompilationUnitSyntax root)
    {
        var candidates = new List<string>();
        using (var workspace = new AdhocWorkspace())
        {
            try
            {
                var spans = GetRewrittenSpans(root);
                if (spans.Count > 0)
                {
                    var spanFormatted = Formatter.Format(root, spans, workspace);
                    candidates.Add(spanFormatted.ToFullString());
                }
            }
            catch
            {
                // 局部格式化失败不致命，继续尝试整文件格式化。
            }

            try
            {
                candidates.Add(Formatter.Format(root, workspace).ToFullString());
            }
            catch
            {
                // 忽略：仍可用未格式化文本。
            }
        }

        candidates.Add(root.ToFullString());

        return candidates.FirstOrDefault(candidate =>
        {
            try
            {
                var reparsed = CSharpSyntaxTree.ParseText(candidate);
                return !reparsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error);
            }
            catch (Exception)
            {
                return false;
            }
        });
    }

    /// <summary>被标记节点的最外层 Span（嵌套标记只保留最外层）。</summary>
    internal static List<Microsoft.CodeAnalysis.Text.TextSpan> GetRewrittenSpans(SyntaxNode root)
    {
        var annotated = root
            .GetAnnotatedNodes(s_rewriteAnnotation.Kind)
            .Where(n => n is ExpressionSyntax or StatementSyntax)
            .ToList();
        if (annotated.Count == 0)
        {
            return new List<Microsoft.CodeAnalysis.Text.TextSpan>();
        }

        var annotatedSet = new HashSet<SyntaxNode>(annotated);
        return annotated
            .Where(n => !n.Ancestors().Any(annotatedSet.Contains))
            .Select(n => n.Span)
            .Distinct()
            .OrderBy(s => s.Start)
            .ToList();
    }

    /// <summary>把带拼接注解的语句替换为展开语句列表（Block/SwitchSection/顶层语句）。</summary>
    private sealed class StatementSplicer : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, IReadOnlyList<StatementSyntax>> _expansions;

        public StatementSplicer(Dictionary<string, IReadOnlyList<StatementSyntax>> expansions)
        {
            _expansions = expansions;
        }

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var updated = (BlockSyntax)base.VisitBlock(node)!;
            return updated.WithStatements(SyntaxFactory.List(Splice(updated.Statements)));
        }

        public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        {
            var updated = (SwitchSectionSyntax)base.VisitSwitchSection(node)!;
            return updated.WithStatements(SyntaxFactory.List(Splice(updated.Statements)));
        }

        public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
        {
            var updated = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;
            var members = new List<MemberDeclarationSyntax>();
            foreach (var member in updated.Members)
            {
                if (member is GlobalStatementSyntax { Statement: { } stmt } global
                    && TryGetExpansion(stmt, out var expanded))
                {
                    foreach (var statement in expanded)
                    {
                        members.Add(SyntaxFactory.GlobalStatement(statement));
                    }

                    continue;
                }

                members.Add(member);
            }

            return updated.WithMembers(SyntaxFactory.List(members));
        }

        private IEnumerable<StatementSyntax> Splice(SyntaxList<StatementSyntax> statements)
        {
            foreach (var statement in statements)
            {
                if (TryGetExpansion(statement, out var expanded))
                {
                    foreach (var expandedStatement in expanded)
                    {
                        yield return expandedStatement;
                    }
                }
                else
                {
                    yield return statement;
                }
            }
        }

        private bool TryGetExpansion(StatementSyntax statement, out IReadOnlyList<StatementSyntax> expanded)
        {
            expanded = Array.Empty<StatementSyntax>();
            var annotation = statement.GetAnnotations(s_spliceAnnotation.Kind).FirstOrDefault();
            if (annotation == null || annotation.Data == null)
            {
                return false;
            }

            return _expansions.TryGetValue(annotation.Data, out expanded!);
        }
    }
}

/// <summary>struct 字段访问路径计算（8+ 元组经 Rest 链）。</summary>
internal static class TupleModelAccess
{
    /// <summary>1-based 元素序号 → 从 struct 根出发的访问路径。</summary>
    public static IReadOnlyList<string> ItemAccessPath(TupleShape shape, int ordinal)
    {
        var path = new List<string>();
        var current = shape;
        var index = ordinal;
        while (index > 7)
        {
            if (current.Tail == null)
            {
                path.Add("Item" + index);
                return path;
            }

            path.Add("Rest");
            index -= 7;
            current = current.Tail;
        }

        path.Add("Item" + index);
        return path;
    }
}

/// <summary>单文件语法重写器（在原始语义模型上工作）。</summary>
internal sealed class TupleSyntaxRewriter : CSharpSyntaxRewriter
{
    private static readonly SyntaxAnnotation s_rewriteAnnotation = new("Cs2jTupleRewrite");
    private static readonly SyntaxAnnotation s_spliceAnnotation = new("Cs2jTupleSplice");

    private readonly SemanticModel _model;
    private readonly TupleShapeRegistry _registry;
    private readonly string _namespace;
    private readonly TupleToStructStatistics _stats;
    private readonly List<TupleToStructDiagnostic> _diagnostics;
    private readonly Dictionary<string, HashSet<TupleShape>> _shapesByFile;
    private readonly string _filePath;

    /// <summary>登记本文件用到的形状（生成按编译单元切分的 struct 文件时需要）。</summary>
    private TupleShape TrackShape(TupleShape shape)
    {
        if (!string.IsNullOrEmpty(_filePath))
        {
            if (!_shapesByFile.TryGetValue(_filePath, out var set))
            {
                set = new HashSet<TupleShape>();
                _shapesByFile[_filePath] = set;
            }

            set.Add(shape);
        }

        return shape;
    }
    private readonly Dictionary<string, IReadOnlyList<StatementSyntax>> _expansions = new();
    private readonly Dictionary<ISymbol?, (string Temp, IReadOnlyList<string> Path)> _identifierRemap =
        new(SymbolEqualityComparer.Default);
    private readonly Dictionary<SyntaxNode, ExpressionSyntax> _isPatternReplacements = new();
    private readonly HashSet<string> _warnedOnce = new(StringComparer.Ordinal);
    private int _tempCounter;

    public bool Changed { get; private set; }

    public Dictionary<string, IReadOnlyList<StatementSyntax>> Expansions => _expansions;

    public TupleSyntaxRewriter(
        SemanticModel model,
        TupleShapeRegistry registry,
        string ns,
        TupleToStructStatistics stats,
        List<TupleToStructDiagnostic> diagnostics,
        Dictionary<string, HashSet<TupleShape>> shapesByFile,
        string filePath)
    {
        _model = model;
        _registry = registry;
        _namespace = ns;
        _stats = stats;
        _diagnostics = diagnostics;
        _shapesByFile = shapesByFile;
        _filePath = filePath;
    }

    // ------------------------------------------------------------------
    // 元组类型与字面量
    // ------------------------------------------------------------------

    public override SyntaxNode? VisitTupleType(TupleTypeSyntax node)
    {
        if (GetTupleTypeSymbol(node) is { } symbol)
        {
            var shape = TrackShape(_registry.GetOrCreateShape(symbol, _diagnostics));
            _stats.TupleTypesReplaced++;
            MarkChanged();
            return ParseTypeNameInScope(shape, symbol)
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(s_rewriteAnnotation);
        }

        WarnOnce("tuple-type", "Could not resolve a tuple type; it was left unchanged.");
        return base.VisitTupleType(node);
    }

    public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
    {
        // System.ValueTuple<...> / A.B.ValueTuple<...>：只在限定链最外层替换整条链，
        // 避免内层返回非 SimpleName 造成类型转换失败。
        if (node.Parent is not QualifiedNameSyntax
            && FindValueTupleLeaf(node) is { } leaf
            && GetTupleTypeSymbol(leaf) is { } symbol)
        {
            var shape = TrackShape(_registry.GetOrCreateShape(symbol, _diagnostics));
            _stats.TupleTypesReplaced++;
            MarkChanged();
            return ParseTypeNameInScope(shape, symbol)
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(s_rewriteAnnotation);
        }

        return base.VisitQualifiedName(node);
    }

    private static GenericNameSyntax? FindValueTupleLeaf(QualifiedNameSyntax node)
    {
        NameSyntax? current = node;
        while (current != null)
        {
            if (current is GenericNameSyntax { Identifier.ValueText: "ValueTuple" } leaf)
            {
                return leaf;
            }

            current = current switch
            {
                QualifiedNameSyntax qualified => qualified.Right,
                AliasQualifiedNameSyntax alias => alias.Name,
                _ => null,
            };
        }

        return null;
    }

    public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
    {
        if (node.Identifier.ValueText == "ValueTuple"
            && node.Parent is not QualifiedNameSyntax
            && GetTupleTypeSymbol(node) is { } symbol)
        {
            // 显式 ValueTuple<...> 泛型写法（含 8+ 嵌套 TRest 形式）。
            var shape = TrackShape(_registry.GetOrCreateShape(symbol, _diagnostics));
            _stats.TupleTypesReplaced++;
            MarkChanged();
            return ParseTypeNameInScope(shape, symbol)
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(s_rewriteAnnotation);
        }

        return base.VisitGenericName(node);
    }

    public override SyntaxNode? VisitTupleExpression(TupleExpressionSyntax node)
    {
        if (node.Parent is AssignmentExpressionSyntax assignment && assignment.Left == node)
        {
            // 解构赋值左侧由语句层/Deconstruct 处理。
            return base.VisitTupleExpression(node);
        }

        var info = _model.GetTypeInfo(node);
        var symbol = info.ConvertedType is INamedTypeSymbol { IsTupleType: true } converted
            ? converted
            : info.Type as INamedTypeSymbol;
        if (symbol is not { IsTupleType: true })
        {
            WarnOnce("tuple-literal",
                "Could not infer the tuple type of a tuple literal; it was left unchanged.");
            return base.VisitTupleExpression(node);
        }

        var shape = TrackShape(_registry.GetOrCreateShape(symbol, _diagnostics));
        var arguments = node.Arguments
            .Select(a => (ExpressionSyntax)Visit(a.Expression)!)
            .ToList();
        _stats.TupleLiteralsRewritten++;
        MarkChanged();
        return BuildStructCreation(shape, symbol, arguments)
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia())
            .WithAdditionalAnnotations(s_rewriteAnnotation);
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (_model.GetSymbolInfo(node).Symbol is IMethodSymbol
            {
                Name: "Create",
                MethodKind: MethodKind.Ordinary,
                ContainingType:
                {
                    Name: "ValueTuple",
                    ContainingNamespace: { } ns,
                },
                ReturnType: INamedTypeSymbol { IsTupleType: true } tupleReturn,
            } method
            && ns.ToDisplayString() == "System"
            && node.ArgumentList.Arguments.Count == tupleReturn.TupleElements.Length)
        {
            var shape = TrackShape(_registry.GetOrCreateShape(tupleReturn, _diagnostics));
            var arguments = node.ArgumentList.Arguments
                .Select(a => (ExpressionSyntax)Visit(a.Expression)!)
                .ToList();
            _stats.ValueTupleCreatesRewritten++;
            MarkChanged();
            return BuildStructCreation(shape, tupleReturn, arguments)
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(s_rewriteAnnotation);
        }

        return base.VisitInvocationExpression(node);
    }

    public override SyntaxNode? VisitAttributeList(AttributeListSyntax node)
    {
        var updated = (AttributeListSyntax)base.VisitAttributeList(node)!;
        var kept = updated.Attributes
            .Where(a => !IsTupleElementNamesAttribute(a.Name))
            .ToArray();
        if (kept.Length == updated.Attributes.Count)
        {
            return updated;
        }

        MarkChanged();
        if (kept.Length == 0)
        {
            return null; // 移除整个 [TupleElementNames(...)] 特性列表
        }

        return updated.WithAttributes(SyntaxFactory.SeparatedList(kept))
            .WithAdditionalAnnotations(s_rewriteAnnotation);
    }

    private static bool IsTupleElementNamesAttribute(NameSyntax name)
    {
        var text = name.ToString().Trim();
        return text.EndsWith("TupleElementNames", StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 元组成员访问：t.x / t?.x / t.ItemN → ItemN（经 Rest 链）
    // ------------------------------------------------------------------

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        if (ResolveElementAccessPath(_model.GetSymbolInfo(node).Symbol) is { } path)
        {
            var visitedReceiver = (ExpressionSyntax)Visit(node.Expression)!;
            ExpressionSyntax rewritten = BuildMemberChain(visitedReceiver, path);
            _stats.MemberAccessesRewritten++;
            MarkChanged();
            return rewritten
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(s_rewriteAnnotation);
        }

        return base.VisitMemberAccessExpression(node);
    }

    public override SyntaxNode? VisitMemberBindingExpression(MemberBindingExpressionSyntax node)
    {
        if (ResolveElementAccessPath(_model.GetSymbolInfo(node).Symbol) is { } path
            && path.Count == 1
            && node.Name.ToString() != path[0])
        {
            // t?.x → t?.ItemN（单段路径；多段路径在条件访问层处理）
            _stats.MemberAccessesRewritten++;
            MarkChanged();
            return SyntaxFactory.MemberBindingExpression(
                    node.OperatorToken, SyntaxFactory.IdentifierName(path[0]))
                .WithAdditionalAnnotations(s_rewriteAnnotation);
        }

        return base.VisitMemberBindingExpression(node);
    }

    public override SyntaxNode? VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
    {
        // t?.Item8 → t?.Rest.Item1：多段路径需在条件访问之上重建成员链
        if (node.WhenNotNull is MemberBindingExpressionSyntax binding
            && ResolveElementAccessPath(_model.GetSymbolInfo(binding).Symbol) is { } path
            && path.Count > 1)
        {
            var visitedExpression = (ExpressionSyntax)Visit(node.Expression)!;
            var conditional = SyntaxFactory.ConditionalAccessExpression(
                visitedExpression,
                node.OperatorToken,
                SyntaxFactory.MemberBindingExpression(
                    binding.OperatorToken, SyntaxFactory.IdentifierName(path[0])));
            var chain = BuildMemberChain(conditional, path.Skip(1).ToArray());
            _stats.MemberAccessesRewritten++;
            MarkChanged();
            return chain
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(s_rewriteAnnotation);
        }

        return base.VisitConditionalAccessExpression(node);
    }

    public override SyntaxNode? VisitElementAccessExpression(ElementAccessExpressionSyntax node)
    {
        if (_model.GetTypeInfo(node.Expression).Type is INamedTypeSymbol { IsTupleType: true })
        {
            WarnOnce("ituple-indexer",
                "ITuple indexer usage on a tuple was left unchanged (unsupported).");
        }

        return base.VisitElementAccessExpression(node);
    }

    /// <summary>若成员符号是值元组元素，返回 struct 上的访问路径（Item8 → Rest, Item1）。</summary>
    private IReadOnlyList<string>? ResolveElementAccessPath(ISymbol? symbol)
    {
        if (symbol is IPropertySymbol
            {
                ContainingType.Name: "ITuple",
            })
        {
            WarnOnce("ituple-member",
                "ITuple member usage on a tuple was left unchanged (unsupported).");
            return null;
        }

        if (symbol is not IFieldSymbol field
            || field.ContainingType is not { IsTupleType: true } containingTuple)
        {
            return null;
        }

        var tupleField = field.CorrespondingTupleField ?? field;
        var match = Regex.Match(tupleField.Name, @"^Item(\d+)$");
        if (!match.Success)
        {
            return null; // Rest / 其他字段：struct 自带同名成员，原样保留
        }

        var ordinal = int.Parse(match.Groups[1].Value);
        var shape = TrackShape(_registry.GetOrCreateShape(containingTuple, _diagnostics));
        var path = TupleModelAccess.ItemAccessPath(shape, ordinal);
        return path.Count > 0 ? path : null;
    }

    private static ExpressionSyntax BuildMemberChain(
        ExpressionSyntax receiver,
        IReadOnlyList<string> path)
    {
        var result = receiver;
        foreach (var segment in path)
        {
            result = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                result,
                SyntaxFactory.IdentifierName(segment));
        }

        return result;
    }

    // ------------------------------------------------------------------
    // 元组→不同形状元组的显式/隐式转换
    // ------------------------------------------------------------------

    /// <summary>
    /// 元组相等比较：C# 的 == / != 在生成 struct 上没有对应运算符，且 Java 里 == 是引用
    /// 比较，因此一律改写为 Equals 调用（Java 侧映射为 equals）。
    /// </summary>
    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.EqualsExpression) || node.IsKind(SyntaxKind.NotEqualsExpression))
        {
            var leftTuple = GetTupleTypeOfExpression(node.Left);
            var rightTuple = GetTupleTypeOfExpression(node.Right);
            var leftIsDefault = node.Left.IsKind(SyntaxKind.DefaultLiteralExpression);
            var rightIsDefault = node.Right.IsKind(SyntaxKind.DefaultLiteralExpression);
            if (leftTuple != null || rightTuple != null)
            {
                if (leftTuple != null && rightTuple != null
                    || leftTuple != null && rightIsDefault
                    || rightTuple != null && leftIsDefault)
                {
                    var left = (ExpressionSyntax)Visit(node.Left)!;
                    var right = (ExpressionSyntax)Visit(node.Right)!;
                    var equals = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            ParenthesizeForMemberAccess(left),
                            SyntaxFactory.IdentifierName("Equals")),
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(right))));
                    ExpressionSyntax replacement = node.IsKind(SyntaxKind.NotEqualsExpression)
                        ? SyntaxFactory.PrefixUnaryExpression(
                            SyntaxKind.LogicalNotExpression,
                            Parenthesize(equals))
                        : equals;
                    _stats.ComparisonsRewritten++;
                    MarkChanged();
                    return replacement
                        .WithLeadingTrivia(node.GetLeadingTrivia())
                        .WithTrailingTrivia(node.GetTrailingTrivia())
                        .WithAdditionalAnnotations(s_rewriteAnnotation);
                }
            }
        }

        return base.VisitBinaryExpression(node);
    }

    private static ExpressionSyntax ParenthesizeForMemberAccess(ExpressionSyntax expression)
        => expression is IdentifierNameSyntax or MemberAccessExpressionSyntax
            or InvocationExpressionSyntax or ElementAccessExpressionSyntax
            or ObjectCreationExpressionSyntax or BaseExpressionSyntax or ThisExpressionSyntax
            or LiteralExpressionSyntax
            ? expression
            : SyntaxFactory.ParenthesizedExpression(expression);

    private static ExpressionSyntax Parenthesize(ExpressionSyntax expression)
        => expression is PrefixUnaryExpressionSyntax
            ? SyntaxFactory.ParenthesizedExpression(expression)
            : expression;

    public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
    {
        if (GetTupleTypeSymbol(node.Type) is { } targetTuple)
        {
            var operandType = _model.GetTypeInfo(node.Expression).Type as INamedTypeSymbol;
            if (operandType is { IsTupleType: true } sourceTuple
                && !SymbolsSameShape(sourceTuple, targetTuple))
            {
                if (!IsSideEffectFree(node.Expression))
                {
                    WarnOnce("cast-nonsimple",
                        "A tuple-to-tuple cast applied to an expression with potential side " +
                        "effects was left unchanged.");
                    return base.VisitCastExpression(node);
                }

                var shape = TrackShape(_registry.GetOrCreateShape(targetTuple, _diagnostics));
                var visitedOperand = (ExpressionSyntax)Visit(node.Expression)!;
                _stats.CastsRewritten++;
                MarkChanged();
                return BuildStructCreationWithCasts(shape, targetTuple, visitedOperand)
                    .WithLeadingTrivia(node.GetLeadingTrivia())
                    .WithTrailingTrivia(node.GetTrailingTrivia())
                    .WithAdditionalAnnotations(s_rewriteAnnotation);
            }
        }

        return base.VisitCastExpression(node);
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        if (node.Left is TupleExpressionSyntax or DeclarationExpressionSyntax)
        {
            // 解构赋值/解构声明：嵌套在表达式里时保留语法，由生成的 Deconstruct 支撑。
            return base.VisitAssignmentExpression(node);
        }

        var conversion = TryRewriteImplicitConversion(node.Right);
        if (conversion != null)
        {
            MarkChanged();
            return node
                .WithLeft((ExpressionSyntax)Visit(node.Left)!)
                .WithRight(conversion)
                .WithAdditionalAnnotations(s_rewriteAnnotation);
        }

        return base.VisitAssignmentExpression(node);
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        var conversion = TryRewriteImplicitConversion(node.Expression);
        if (conversion != null)
        {
            MarkChanged();
            return node.WithExpression(conversion)
                .WithAdditionalAnnotations(s_rewriteAnnotation);
        }

        return base.VisitReturnStatement(node);
    }

    public override SyntaxNode? VisitYieldStatement(YieldStatementSyntax node)
    {
        var conversion = TryRewriteImplicitConversion(node.Expression);
        if (conversion != null)
        {
            MarkChanged();
            return node.WithExpression(conversion)
                .WithAdditionalAnnotations(s_rewriteAnnotation);
        }

        return base.VisitYieldStatement(node);
    }

    public override SyntaxNode? VisitArgument(ArgumentSyntax node)
    {
        var conversion = TryRewriteImplicitConversion(node.Expression);
        if (conversion != null)
        {
            MarkChanged();
            return node.WithExpression(conversion)
                .WithAdditionalAnnotations(s_rewriteAnnotation);
        }

        return base.VisitArgument(node);
    }

    public override SyntaxNode? VisitEqualsValueClause(EqualsValueClauseSyntax node)
    {
        var conversion = TryRewriteImplicitConversion(node.Value);
        if (conversion != null)
        {
            MarkChanged();
            return node.WithValue(conversion)
                .WithAdditionalAnnotations(s_rewriteAnnotation);
        }

        return base.VisitEqualsValueClause(node);
    }

    public override SyntaxNode? VisitArrowExpressionClause(ArrowExpressionClauseSyntax node)
    {
        var conversion = TryRewriteImplicitConversion(node.Expression);
        if (conversion != null)
        {
            MarkChanged();
            return node.WithExpression(conversion)
                .WithAdditionalAnnotations(s_rewriteAnnotation);
        }

        return base.VisitArrowExpressionClause(node);
    }

    public override SyntaxNode? VisitConditionalExpression(ConditionalExpressionSyntax node)
    {
        var whenTrueConversion = TryRewriteImplicitConversion(node.WhenTrue);
        var whenFalseConversion = TryRewriteImplicitConversion(node.WhenFalse);
        if (whenTrueConversion == null && whenFalseConversion == null)
        {
            return base.VisitConditionalExpression(node);
        }

        var updated = node
            .WithCondition((ExpressionSyntax)Visit(node.Condition)!)
            .WithWhenTrue(whenTrueConversion ?? (ExpressionSyntax)Visit(node.WhenTrue)!)
            .WithWhenFalse(whenFalseConversion ?? (ExpressionSyntax)Visit(node.WhenFalse)!);
        MarkChanged();
        return updated.WithAdditionalAnnotations(s_rewriteAnnotation);
    }

    public override SyntaxNode? VisitInitializerExpression(InitializerExpressionSyntax node)
    {
        if (node.Kind() is SyntaxKind.ArrayInitializerExpression
            or SyntaxKind.ComplexElementInitializerExpression)
        {
            var changed = false;
            var expressions = new List<ExpressionSyntax>();
            foreach (var expression in node.Expressions)
            {
                var conversion = TryRewriteImplicitConversion(expression);
                if (conversion != null)
                {
                    expressions.Add(conversion);
                    changed = true;
                }
                else
                {
                    expressions.Add((ExpressionSyntax)Visit(expression)!);
                }
            }

            if (changed)
            {
                MarkChanged();
                return node.WithExpressions(SyntaxFactory.SeparatedList(expressions))
                    .WithAdditionalAnnotations(s_rewriteAnnotation);
            }
        }

        return base.VisitInitializerExpression(node);
    }

    /// <summary>
    /// 非字面量元组表达式发生形状变化的隐式转换时，改写为显式 struct 构造（元素级
    /// 显式转换）。仅对无副作用的简单表达式启用，避免重复求值。
    /// </summary>
    private ExpressionSyntax? TryRewriteImplicitConversion(ExpressionSyntax? original)
    {
        if (original == null || original is TupleExpressionSyntax)
        {
            return null;
        }

        var info = _model.GetTypeInfo(original);
        if (info.Type is not INamedTypeSymbol { IsTupleType: true } natural
            || info.ConvertedType is not INamedTypeSymbol { IsTupleType: true } target
            || SymbolsSameShape(natural, target))
        {
            return null;
        }

        if (!IsSideEffectFree(original))
        {
            WarnOnce("conversion-nonsimple",
                "An implicit tuple conversion was applied to an expression with potential " +
                "side effects and was left unchanged.");
            return null;
        }

        var shape = TrackShape(_registry.GetOrCreateShape(target, _diagnostics));
        var visited = (ExpressionSyntax)Visit(original)!;
        _stats.ImplicitConversionsRewritten++;
        return BuildStructCreationWithCasts(shape, target, visited)
            .WithLeadingTrivia(original.GetLeadingTrivia())
            .WithTrailingTrivia(original.GetTrailingTrivia());
    }

    private static bool SymbolsSameShape(INamedTypeSymbol a, INamedTypeSymbol b)
    {
        var ae = a.TupleElements;
        var be = b.TupleElements;
        if (ae.Length != be.Length)
        {
            return false;
        }

        for (var i = 0; i < ae.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(ae[i].Type, be[i].Type))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSideEffectFree(ExpressionSyntax expression)
    {
        foreach (var node in expression.DescendantNodesAndSelf())
        {
            if (node is InvocationExpressionSyntax
                or ObjectCreationExpressionSyntax
                or SimpleLambdaExpressionSyntax
                or ParenthesizedLambdaExpressionSyntax
                or AwaitExpressionSyntax
                or AssignmentExpressionSyntax)
            {
                return false;
            }

            if (node is PrefixUnaryExpressionSyntax pre
                && (pre.IsKind(SyntaxKind.PreIncrementExpression)
                    || pre.IsKind(SyntaxKind.PreDecrementExpression)))
            {
                return false;
            }

            if (node is PostfixUnaryExpressionSyntax post
                && (post.IsKind(SyntaxKind.PostIncrementExpression)
                    || post.IsKind(SyntaxKind.PostDecrementExpression)))
            {
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------
    // ITuple 检测与标识符重映射
    // ------------------------------------------------------------------

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        if (_identifierRemap.Count > 0
            && _model.GetSymbolInfo(node).Symbol is { } symbol
            && _identifierRemap.TryGetValue(symbol, out var mapping))
        {
            if (IsWriteTarget(node))
            {
                WarnOnce("condition-mutation",
                    "A tuple deconstruction variable is mutated inside a loop condition; " +
                    "this pattern is not supported and was left unchanged.");
                return base.VisitIdentifierName(node);
            }

            var pathed = BuildMemberChain(SyntaxFactory.IdentifierName(mapping.Temp), mapping.Path);
            MarkChanged();
            return pathed
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(s_rewriteAnnotation);
        }

        if (node.Identifier.ValueText == "ITuple"
            && _model.GetSymbolInfo(node).Symbol is INamedTypeSymbol
            {
                Name: "ITuple",
                ContainingNamespace: { } ns,
            }
            && ns.ToDisplayString() == "System.Runtime.CompilerServices")
        {
            WarnOnce("ituple",
                "ITuple usage involving tuples was detected and left unchanged " +
                "(generated structs do not implement ITuple).");
        }

        return base.VisitIdentifierName(node);
    }

    public override SyntaxNode? VisitDeclarationExpression(DeclarationExpressionSyntax node)
    {
        if (node.Designation is ParenthesizedVariableDesignationSyntax
            && GetTupleTypeOfExpression(node) is not null)
        {
            WarnOnce("nested-designation",
                "A tuple deconstructing designation inside a nested expression was left " +
                "unchanged (it relies on the generated Deconstruct method).");
        }

        return base.VisitDeclarationExpression(node);
    }

    private static bool IsWriteTarget(ExpressionSyntax identifier)
    {
        var parent = identifier.Parent;
        return parent is AssignmentExpressionSyntax assignment && assignment.Left == identifier
            || parent is PrefixUnaryExpressionSyntax
            || parent is PostfixUnaryExpressionSyntax;
    }

    // ------------------------------------------------------------------
    // 语句层：解构展开与 out var 解构提升
    // ------------------------------------------------------------------



    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        // var (a, b) = e; —— 解构声明（解析为 Assignment{Left: DeclarationExpression}）
        if (node.Expression is AssignmentExpressionSyntax
            {
                Left: DeclarationExpressionSyntax
                {
                    Designation: ParenthesizedVariableDesignationSyntax declarationDesignation,
                },
            } declarationAssignment
            && GetTupleTypeOfExpression(declarationAssignment.Right) is { } declaredTuple)
        {
            var shape = TrackShape(_registry.GetOrCreateShape(declaredTuple, _diagnostics));
            var visitedValue = (ExpressionSyntax)Visit(declarationAssignment.Right)!;
            var statements = new List<StatementSyntax>();
            var temp = NextTemp();
            statements.Add(LocalDecl(temp, visitedValue));
            statements.AddRange(ExpandDesignation(shape, declarationDesignation, temp));
            return FinishExpansion(node, statements);
        }

        // (a, b) = e;  —— 解构赋值
        if (node.Expression is AssignmentExpressionSyntax
            {
                Left: TupleExpressionSyntax tupleLeft,
            } assignment)
        {
            var tupleType = _model.GetTypeInfo(assignment).Type as INamedTypeSymbol
                ?? _model.GetTypeInfo(assignment.Right).ConvertedType as INamedTypeSymbol;
            if (tupleType is { IsTupleType: true })
            {
                var shape = TrackShape(_registry.GetOrCreateShape(tupleType, _diagnostics));
                var visitedRight = (ExpressionSyntax)Visit(assignment.Right)!;
                var statements = new List<StatementSyntax>();
                var temp = NextTemp();
                statements.Add(LocalDecl(temp, visitedRight));
                statements.AddRange(ExpandTupleExpressionTargets(shape, tupleLeft, temp));
                return FinishExpansion(node, statements);
            }
        }

        return base.VisitExpressionStatement(node);
    }

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        // 条件中的 `is var (a, b)` → 提升为条件求值前置语句（deconstruct 展开）
        var varPatterns = CollectVarPatternDeconstructions(node.Condition);
        if (varPatterns.Count == 0)
        {
            return base.VisitIfStatement(node);
        }

        var statements = new List<StatementSyntax>();

        foreach (var (isExpression, pattern) in varPatterns)
        {
            if (_model.GetTypeInfo(isExpression.Expression).Type is not INamedTypeSymbol
                {
                    IsTupleType: true,
                } tupleType)
            {
                continue;
            }

            var shape = TrackShape(_registry.GetOrCreateShape(tupleType, _diagnostics));
            var temp = NextTemp();
            var visitedOperand = (ExpressionSyntax)Visit(isExpression.Expression)!;
            statements.Add(LocalDecl(temp, visitedOperand));
            statements.AddRange(ExpandDesignation(shape, pattern.Designation, temp));
            _isPatternReplacements[isExpression] = SyntaxFactory.LiteralExpression(
                SyntaxKind.TrueLiteralExpression);
        }

        var resultTemp = NextTemp();
        var visitedCondition = (ExpressionSyntax)Visit(node.Condition)!;
        _identifierRemap.Clear();
        statements.Add(LocalDecl(resultTemp, visitedCondition));

        var visitedStatement = (StatementSyntax)Visit(node.Statement)!;
        var ifStatement = node
            .WithCondition(SyntaxFactory.IdentifierName(resultTemp))
            .WithStatement(visitedStatement);
        if (node.Else != null)
        {
            ifStatement = ifStatement.WithElse(
                node.Else.WithStatement((StatementSyntax)Visit(node.Else.Statement)!));
        }

        statements.Add(ifStatement);
        return FinishExpansion(node, statements);
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        WarnIfConditionDeconstruction(node.Condition, "while");
        return base.VisitWhileStatement(node);
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
        WarnIfConditionDeconstruction(node.Condition, "do");
        return base.VisitDoStatement(node);
    }

    public override SyntaxNode? VisitLockStatement(LockStatementSyntax node)
    {
        return base.VisitLockStatement(node);
    }

    public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
    {
        return base.VisitSwitchStatement(node);
    }

    private List<(IsPatternExpressionSyntax IsExpression, VarPatternSyntax Pattern)>
        CollectVarPatternDeconstructions(ExpressionSyntax condition)
    {
        var result = new List<(IsPatternExpressionSyntax, VarPatternSyntax)>();
        foreach (var node in condition.DescendantNodesAndSelf())
        {
            if (node is VarPatternSyntax
                {
                    Designation: ParenthesizedVariableDesignationSyntax,
                } pattern
                && node.Parent is IsPatternExpressionSyntax isExpression
                && _model.GetTypeInfo(isExpression.Expression).Type is INamedTypeSymbol
                {
                    IsTupleType: true,
                })
            {
                result.Add((isExpression, pattern));
            }
        }

        return result;
    }

    /// <summary>循环条件中的 `is var (a, b)` 无法安全提升（每轮需重新声明），给出告警。</summary>
    private void WarnIfConditionDeconstruction(ExpressionSyntax condition, string construct)
    {
        if (CollectVarPatternDeconstructions(condition).Count > 0)
        {
            WarnOnce($"loop-condition-deconstruction-{construct}",
                $"A tuple 'is var (a, b)' pattern in a {construct} condition was left " +
                "unchanged (it relies on the generated Deconstruct method).");
        }
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        // for (var (i, j) = F(); ...) —— 初始化子解构（位于 Initializers[0]）→ 前置块包裹
        if (node.Declaration == null
            && node.Initializers.Count == 1
            && node.Initializers[0] is AssignmentExpressionSyntax
            {
                Left: DeclarationExpressionSyntax
                {
                    Designation: ParenthesizedVariableDesignationSyntax designation,
                },
            } initAssignment
            && GetTupleTypeOfExpression(initAssignment.Right) is { } initTuple)
        {
            var shape = TrackShape(_registry.GetOrCreateShape(initTuple, _diagnostics));
            var visitedValue = (ExpressionSyntax)Visit(initAssignment.Right)!;
            var preamble = new List<StatementSyntax>();
            var temp = NextTemp();
            preamble.Add(LocalDecl(temp, visitedValue));
            preamble.AddRange(ExpandDesignation(shape, designation, temp));

            MarkChanged();
            _stats.DeconstructionsExpanded++;
            var forStatement = node
                .WithInitializers(SyntaxFactory.SeparatedList<ExpressionSyntax>())
                .WithCondition(node.Condition == null
                    ? null
                    : (ExpressionSyntax)Visit(node.Condition)!)
                .WithIncrementors(SyntaxFactory.SeparatedList(
                    node.Incrementors.Select(i => (ExpressionSyntax)Visit(i)!)))
                .WithStatement((StatementSyntax)Visit(node.Statement)!);
            return SyntaxFactory.Block(preamble.Append(forStatement))
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(s_rewriteAnnotation);
        }

        WarnIfConditionDeconstruction(node.Condition, "for");
        return base.VisitForStatement(node);
    }

    public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
    {
        var elementType = _model.GetForEachStatementInfo(node).ElementType as INamedTypeSymbol;
        if (elementType is not { IsTupleType: true })
        {
            return base.VisitForEachVariableStatement(node);
        }

        var shape = TrackShape(_registry.GetOrCreateShape(elementType, _diagnostics));
        var temp = NextTemp();
        var declarations = new List<StatementSyntax>();
        StatementSyntax result;

        if (node.Variable is DeclarationExpressionSyntax
            {
                Designation: ParenthesizedVariableDesignationSyntax varDesignation,
            })
        {
            declarations.AddRange(ExpandDesignation(shape, varDesignation, temp));
        }
        else if (node.Variable is TupleExpressionSyntax tupleVariables)
        {
            declarations.AddRange(ExpandTupleExpressionTargets(shape, tupleVariables, temp));
        }
        else
        {
            return base.VisitForEachVariableStatement(node);
        }

        var visitedExpression = (ExpressionSyntax)Visit(node.Expression)!;
        var body = PrependToStatementBody(node.Statement, declarations);
        result = SyntaxFactory.ForEachStatement(
            node.AwaitKeyword,
            node.ForEachKeyword,
            node.OpenParenToken,
            SyntaxFactory.IdentifierName("var"),
            SyntaxFactory.Identifier(temp).WithLeadingTrivia(
                node.Variable.GetLeadingTrivia()),
            node.InKeyword,
            visitedExpression,
            node.CloseParenToken,
            body);

        _stats.DeconstructionsExpanded++;
        MarkChanged();
        return result
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia())
            .WithAdditionalAnnotations(s_rewriteAnnotation);
    }

    // ------------------------------------------------------------------
    // 模式
    // ------------------------------------------------------------------

    public override SyntaxNode? VisitIsPatternExpression(IsPatternExpressionSyntax node)
    {
        if (_isPatternReplacements.TryGetValue(node, out var replacement))
        {
            MarkChanged();
            return replacement;
        }

        return base.VisitIsPatternExpression(node);
    }

    public override SyntaxNode? VisitVarPattern(VarPatternSyntax node)
    {
        if (node.Designation is ParenthesizedVariableDesignationSyntax
            && node.Parent is IsPatternExpressionSyntax isExpression
            && _model.GetTypeInfo(isExpression.Expression).Type is INamedTypeSymbol { IsTupleType: true }
            && !_isPatternReplacements.ContainsKey(isExpression))
        {
            WarnOnce("var-pattern",
                "A 'is var (a, b)' pattern outside an if-condition was left unchanged " +
                "(it relies on the generated Deconstruct method).");
        }

        return base.VisitVarPattern(node);
    }

    public override SyntaxNode? VisitRecursivePattern(RecursivePatternSyntax node)
    {
        if (GetPatternInputType(node) is { IsTupleType: true })
        {
            WarnOnce("positional-pattern",
                "A positional tuple pattern was left unchanged (it relies on the " +
                "generated Deconstruct method).");
        }

        return base.VisitRecursivePattern(node);
    }

    public override SyntaxNode? VisitConstantPattern(ConstantPatternSyntax node)
    {
        if (node.Expression is TupleExpressionSyntax
            && GetPatternInputType(node) is { IsTupleType: true })
        {
            WarnOnce("constant-tuple-pattern",
                "A constant tuple pattern was left unchanged (it relies on the " +
                "generated Deconstruct method).");
        }

        return base.VisitConstantPattern(node);
    }

    private INamedTypeSymbol? GetPatternInputType(SyntaxNode pattern)
    {
        var isPattern = pattern.Ancestors()
            .OfType<IsPatternExpressionSyntax>()
            .FirstOrDefault();
        if (isPattern == null)
        {
            // switch case 模式：取 switch 表达式类型
            var section = pattern.Ancestors().OfType<SwitchSectionSyntax>().FirstOrDefault();
            var switchStatement = section?.Parent as SwitchStatementSyntax;
            if (switchStatement != null)
            {
                return _model.GetTypeInfo(switchStatement.Expression).Type as INamedTypeSymbol;
            }

            return null;
        }

        return _model.GetTypeInfo(isPattern.Expression).Type as INamedTypeSymbol;
    }

    // ------------------------------------------------------------------
    // out var 解构规划
    // ------------------------------------------------------------------

    private void RegisterIdentifierRemap(
        VariableDesignationSyntax designation,
        TupleShape shape,
        string temp)
    {
        var queue = new Queue<(VariableDesignationSyntax Node, IReadOnlyList<string> Path)>();
        queue.Enqueue((designation, Array.Empty<string>()));
        while (queue.Count > 0)
        {
            var (node, path) = queue.Dequeue();
            if (node is ParenthesizedVariableDesignationSyntax paren)
            {
                for (var i = 0; i < paren.Variables.Count; i++)
                {
                    var elementPath = TupleModelAccess.ItemAccessPath(shape, i + 1);
                    queue.Enqueue((paren.Variables[i], path.Concat(elementPath).ToArray()));
                }
            }
            else if (node is SingleVariableDesignationSyntax single
                && _model.GetDeclaredSymbol(single) is { } symbol)
            {
                _identifierRemap[symbol] = (temp, path);
            }
        }
    }

    // ------------------------------------------------------------------
    // 解构展开
    // ------------------------------------------------------------------

    private IEnumerable<StatementSyntax> ExpandDesignation(
        TupleShape shape,
        VariableDesignationSyntax designation,
        string temp)
    {
        if (designation is not ParenthesizedVariableDesignationSyntax paren)
        {
            yield break;
        }

        for (var i = 0; i < paren.Variables.Count; i++)
        {
            var access = PathAccess(temp, TupleModelAccess.ItemAccessPath(shape, i + 1));
            switch (paren.Variables[i])
            {
                case SingleVariableDesignationSyntax single:
                    yield return SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(
                            SyntaxFactory.IdentifierName("var"),
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(
                                    single.Identifier,
                                    null,
                                    SyntaxFactory.EqualsValueClause(access)))));
                    break;

                case DiscardDesignationSyntax:
                    break;

                case ParenthesizedVariableDesignationSyntax nested
                    when shape.Elements[i].NestedShape is { } nestedShape:
                    var nestedTemp = NextTemp();
                    yield return LocalDecl(nestedTemp, access);
                    foreach (var statement in ExpandDesignation(nestedShape, nested, nestedTemp))
                    {
                        yield return statement;
                    }

                    break;
            }
        }
    }

    private IEnumerable<StatementSyntax> ExpandTupleExpressionTargets(
        TupleShape shape,
        TupleExpressionSyntax tupleLeft,
        string temp)
    {
        for (var i = 0; i < tupleLeft.Arguments.Count; i++)
        {
            var access = PathAccess(temp, TupleModelAccess.ItemAccessPath(shape, i + 1));
            switch (tupleLeft.Arguments[i].Expression)
            {
                case DeclarationExpressionSyntax
                    {
                        Designation: SingleVariableDesignationSyntax single,
                        Type: { } explicitType,
                    }:
                    yield return SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(
                            (TypeSyntax)Visit(explicitType)!,
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(
                                    single.Identifier,
                                    null,
                                    SyntaxFactory.EqualsValueClause(access)))));
                    break;

                case DeclarationExpressionSyntax
                    {
                        Designation: ParenthesizedVariableDesignationSyntax nested,
                    } when shape.Elements[i].NestedShape is { } nestedShape:
                    var nestedTemp = NextTemp();
                    yield return LocalDecl(nestedTemp, access);
                    foreach (var statement in ExpandDesignation(nestedShape, nested, nestedTemp))
                    {
                        yield return statement;
                    }

                    break;

                case TupleExpressionSyntax nestedTuple
                    when shape.Elements[i].NestedShape is { } nestedShape2:
                    var nestedTemp2 = NextTemp();
                    yield return LocalDecl(nestedTemp2, access);
                    foreach (var statement in ExpandTupleExpressionTargets(nestedShape2, nestedTuple, nestedTemp2))
                    {
                        yield return statement;
                    }

                    break;

                default:
                    yield return SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            (ExpressionSyntax)Visit(tupleLeft.Arguments[i].Expression)!,
                            access));
                    break;
            }
        }
    }

    private static ExpressionSyntax PathAccess(string temp, IReadOnlyList<string> path)
        => BuildMemberChain(SyntaxFactory.IdentifierName(temp), path);

    // ------------------------------------------------------------------
    // 语句工具
    // ------------------------------------------------------------------

    private SyntaxNode FinishExpansion(
        StatementSyntax original,
        List<StatementSyntax> expanded)
    {
        expanded[0] = expanded[0].WithLeadingTrivia(original.GetLeadingTrivia());
        expanded[^1] = expanded[^1].WithTrailingTrivia(original.GetTrailingTrivia());
        _stats.DeconstructionsExpanded++;
        MarkChanged();
        _identifierRemap.Clear();

        if (original.Parent is BlockSyntax or SwitchSectionSyntax or GlobalStatementSyntax)
        {
            var id = $"s{_expansions.Count}";
            _expansions[id] = expanded
                .Select(s => s.WithAdditionalAnnotations(
                    new SyntaxAnnotation(s_spliceAnnotation.Kind, id),
                    s_rewriteAnnotation))
                .ToList();
            return original.WithAdditionalAnnotations(new SyntaxAnnotation(s_spliceAnnotation.Kind, id));
        }

        WarnOnce("embedded-wrap",
            "A tuple deconstruction inside a non-block embedded statement was wrapped in " +
            "a block; variables declared there are scoped to that block.");
        return SyntaxFactory.Block(
                expanded.Select(s => s.WithAdditionalAnnotations(s_rewriteAnnotation)))
            .WithAdditionalAnnotations(s_rewriteAnnotation);
    }

    private StatementSyntax PrependToStatementBody(
        StatementSyntax statement,
        IEnumerable<StatementSyntax> declarations)
    {
        var list = declarations
            .Select(d => d.WithAdditionalAnnotations(s_rewriteAnnotation))
            .ToList();
        if (list.Count == 0)
        {
            return (StatementSyntax)Visit(statement)!;
        }

        if (statement is BlockSyntax block)
        {
            var visited = (BlockSyntax)Visit(block)!;
            return visited.WithStatements(SyntaxFactory.List(list.Concat(visited.Statements)));
        }

        var visitedStatement = (StatementSyntax)Visit(statement)!;
        return SyntaxFactory.Block(list.Concat(new[] { visitedStatement }))
            .WithAdditionalAnnotations(s_rewriteAnnotation);
    }

    private static LocalDeclarationStatementSyntax LocalDecl(string name, ExpressionSyntax value)
        => SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier(name),
                        null,
                        SyntaxFactory.EqualsValueClause(value)))));

    private string NextTemp()
    {
        var name = $"__cs2jT{_tempCounter}";
        _tempCounter++;
        return name;
    }

    // ------------------------------------------------------------------
    // struct 构造
    // ------------------------------------------------------------------

    private TypeSyntax ParseTypeNameInScope(TupleShape shape, INamedTypeSymbol tupleSymbol)
    {
        var display = ShapeDisplayInScope(shape, tupleSymbol);
        return SyntaxFactory.ParseTypeName($"global::{_namespace}.{display}");
    }

    /// <summary>在当前作用域内渲染 struct 实例化：泛型参数用其源码名（T 等）。</summary>
    private string ShapeDisplayInScope(TupleShape shape, INamedTypeSymbol tupleSymbol)
    {
        if (shape.GenericArity == 0)
        {
            return shape.Name;
        }

        var parameters = new List<ITypeParameterSymbol>();
        foreach (var field in tupleSymbol.TupleElements)
        {
            TupleShapeRegistry.CollectParameterOrder(field.Type, parameters);
        }

        var args = parameters
            .Take(shape.GenericArity)
            .Select(p => TupleShapeRegistry.SanitizeIdentifier(p.Name));
        return shape.Name + "<" + string.Join(", ", args) + ">";
    }

    private ExpressionSyntax BuildStructCreation(
        TupleShape shape,
        INamedTypeSymbol tupleSymbol,
        IReadOnlyList<ExpressionSyntax> arguments)
    {
        var typeSyntax = ParseTypeNameInScope(shape, tupleSymbol);
        var args = shape.Arity > 7
            ? GroupTailArguments(shape, tupleSymbol, arguments)
            : arguments;
        return SyntaxFactory.ObjectCreationExpression(
            typeSyntax,
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(args.Select(SyntaxFactory.Argument))),
            null);
    }

    private ExpressionSyntax BuildStructCreationWithCasts(
        TupleShape shape,
        INamedTypeSymbol tupleSymbol,
        ExpressionSyntax operand)
    {
        var arguments = new List<ExpressionSyntax>();
        for (var i = 0; i < shape.Arity; i++)
        {
            var path = TupleModelAccess.ItemAccessPath(shape, i + 1);
            var memberAccess = BuildMemberChain(operand, path);
            var elementDisplay = DisplayTypeInScope(tupleSymbol.TupleElements[i].Type);
            arguments.Add(SyntaxFactory.CastExpression(
                SyntaxFactory.ParseTypeName(elementDisplay),
                memberAccess));
        }

        var typeSyntax = ParseTypeNameInScope(shape, tupleSymbol);
        var args = shape.Arity > 7
            ? GroupTailArguments(shape, tupleSymbol, arguments)
            : arguments;
        return SyntaxFactory.ObjectCreationExpression(
            typeSyntax,
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(args.Select(SyntaxFactory.Argument))),
            null);
    }

    /// <summary>作用域内类型文本（泛型参数用源码名），用于元素级显式转换。</summary>
    private string DisplayTypeInScope(ITypeSymbol type)
    {
        switch (type)
        {
            case ITypeParameterSymbol parameter:
                return TupleShapeRegistry.SanitizeIdentifier(parameter.Name);

            case IArrayTypeSymbol array:
                return DisplayTypeInScope(array.ElementType)
                    + "[" + new string(',', array.Rank - 1) + "]";

            case IDynamicTypeSymbol:
                return "global::System.Object";

            case INamedTypeSymbol named:
            {
                if (named.IsTupleType)
                {
                    var shape = TrackShape(_registry.GetOrCreateShape(named, _diagnostics));
                    return ShapeDisplayInScope(shape, named);
                }

                var definition = named.OriginalDefinition;
                var name = definition.MetadataName ?? definition.Name;
                var index = name.LastIndexOf('`');
                if (index >= 0)
                {
                    name = name[..index];
                }

                var prefix = TupleShapeRegistry.QualifiedMetadataPrefix(definition);
                if (named.TypeArguments.Length == 0)
                {
                    return prefix + name;
                }

                return prefix + name + "<"
                    + string.Join(", ", named.TypeArguments.Select(DisplayTypeInScope))
                    + ">";
            }

            default:
                return "global::System.Object";
        }
    }

    private List<ExpressionSyntax> GroupTailArguments(
        TupleShape shape,
        INamedTypeSymbol tupleSymbol,
        IReadOnlyList<ExpressionSyntax> flatArguments)
    {
        var head = flatArguments.Take(7).ToList();
        var tailShape = shape.Tail!;
        var tailArguments = flatArguments.Skip(7).ToList();
        var tailTypeSyntax = SyntaxFactory.ParseTypeName(
            "global::" + _namespace + "." + TailDisplayInScope(tailShape, tupleSymbol));
        var groupedTail = tailShape.Arity > 7
            ? GroupTailArgumentsRecursive(tailShape, tupleSymbol, tailArguments)
            : tailArguments;
        head.Add(SyntaxFactory.ObjectCreationExpression(
            tailTypeSyntax,
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(groupedTail.Select(SyntaxFactory.Argument))),
            null));
        return head;
    }

    private List<ExpressionSyntax> GroupTailArgumentsRecursive(
        TupleShape tailShape,
        INamedTypeSymbol outerTupleSymbol,
        IReadOnlyList<ExpressionSyntax> tailArguments)
    {
        var head = tailArguments.Take(7).ToList();
        var nestedTailShape = tailShape.Tail!;
        var nestedTailArguments = tailArguments.Skip(7).ToList();
        var tailTypeSyntax = SyntaxFactory.ParseTypeName(
            "global::" + _namespace + "." + TailDisplayInScope(nestedTailShape, outerTupleSymbol));
        head.Add(SyntaxFactory.ObjectCreationExpression(
            tailTypeSyntax,
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(
                    (nestedTailShape.Arity > 7
                        ? GroupTailArgumentsRecursive(nestedTailShape, outerTupleSymbol, nestedTailArguments)
                        : nestedTailArguments).Select(SyntaxFactory.Argument))),
            null));
        return head;
    }

    /// <summary>尾部 struct 在源码作用域内的实例化文本。</summary>
    private string TailDisplayInScope(TupleShape tailShape, INamedTypeSymbol outerTupleSymbol)
    {
        if (tailShape.GenericArity == 0)
        {
            return tailShape.Name;
        }

        var tailElementTypes = outerTupleSymbol.TupleElements.Skip(7).Select(f => f.Type);
        var parameters = new List<ITypeParameterSymbol>();
        foreach (var elementType in tailElementTypes)
        {
            TupleShapeRegistry.CollectParameterOrder(elementType, parameters);
        }

        var args = parameters
            .Take(tailShape.GenericArity)
            .Select(p => TupleShapeRegistry.SanitizeIdentifier(p.Name));
        return tailShape.Name + "<" + string.Join(", ", args) + ">";
    }

    // ------------------------------------------------------------------
    // 杂项
    // ------------------------------------------------------------------

    /// <summary>
    /// 表达式（含 out var 解构声明）的元组类型：优先 ConvertedType（out 参数类型/
    /// 赋值目标类型），否则自然类型。非元组（如 KeyValuePair Deconstruct）返回 null。
    /// </summary>
    private INamedTypeSymbol? GetTupleTypeOfExpression(ExpressionSyntax expression)
    {
        var info = _model.GetTypeInfo(expression);
        if (info.ConvertedType is INamedTypeSymbol { IsTupleType: true } converted)
        {
            return converted;
        }

        if (info.Type is INamedTypeSymbol { IsTupleType: true } natural)
        {
            return natural;
        }

        return null;
    }

    private INamedTypeSymbol? GetTupleTypeSymbol(TypeSyntax typeSyntax)
    {
        if (_model.GetTypeInfo(typeSyntax).Type is INamedTypeSymbol { IsTupleType: true } byInfo)
        {
            return byInfo;
        }

        if (_model.GetSymbolInfo(typeSyntax).Symbol is INamedTypeSymbol { IsTupleType: true } bySymbol)
        {
            return bySymbol;
        }

        return null;
    }

    private void MarkChanged() => Changed = true;

    private void WarnOnce(string key, string message)
    {
        if (_warnedOnce.Add(key))
        {
            _diagnostics.Add(new TupleToStructDiagnostic(TupleToStructSeverity.Warning, message));
        }
    }
}
