using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using System.Text.RegularExpressions;

namespace CSharpToJava.Core.TupleToStruct;

/// <summary>
/// C# 值元组消除预处理器。把源码中所有 tuple 语法（元组字面量、元组类型、
/// 解构、ValueTuple 泛型/工厂）替换为对生成可变 struct 的引用：
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
    private readonly HashSet<int> _runtimeObservedArities = new();

    public TupleToStructPreprocessor(TupleToStructOptions? options = null)
    {
        _options = options ?? new TupleToStructOptions();
    }

    /// <summary>
    /// 记录通过 <c>typeof(ValueTuple&lt;...&gt;)</c> 观察值元组运行时身份的泛型元数。
    /// 该元数的形状保持原生 System.ValueTuple 语法：降级成生成的 struct 后运行时类型
    /// 不再是泛型 ValueTuple，<c>Type.IsGenericType</c> /
    /// <c>GetGenericTypeDefinition()</c> 驱动的反射消费者（如 System.Text.Json 的
    /// JsonConverterFactory.CanConvert）会静默失配（语料 Json 测试
    /// CustomConverterTests.NullableTypes）。
    /// 这是进程级的默认集合，供直接使用本类的单文件调用方保持旧行为；项目级调用方
    /// 应改为在 <see cref="RewriteFile"/> 按文件传入所属项目的元数集合，使观测只在该
    /// 项目内生效（跨项目互不污染）。
    /// </summary>
    public void ObserveRuntimeValueTupleArities(IEnumerable<int> arities)
    {
        ArgumentNullException.ThrowIfNull(arities);
        foreach (var arity in arities)
        {
            _runtimeObservedArities.Add(arity);
        }
    }

    /// <summary>
    /// 扫描语法树中 <c>typeof(ValueTuple&lt;...&gt;)</c>（含 <c>System.ValueTuple</c>
    /// 限定写法与 <c>ValueTuple&lt;,&gt;</c> 省略参数写法）出现的泛型元数。
    /// </summary>
    public static IReadOnlyCollection<int> FindRuntimeObservedTupleArities(SyntaxNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var arities = new HashSet<int>();
        foreach (var typeOf in root.DescendantNodes().OfType<TypeOfExpressionSyntax>())
        {
            if (FindValueTupleGenericName(typeOf.Type) is { } valueTuple)
            {
                arities.Add(valueTuple.TypeArgumentList.Arguments.Count);
            }
        }

        return arities;
    }

    private static GenericNameSyntax? FindValueTupleGenericName(SyntaxNode type) => type switch
    {
        GenericNameSyntax generic when generic.Identifier.ValueText == "ValueTuple" => generic,
        QualifiedNameSyntax qualified => FindValueTupleGenericName(qualified.Right),
        AliasQualifiedNameSyntax alias => FindValueTupleGenericName(alias.Name),
        _ => null,
    };

    /// <summary>
    /// 找出只存在于禁用条件编译分支（Roslyn 的 DisabledText）里的元组 arity。
    /// 本 pass 用固定符号集解析，`#if DEBUG` 之类的分支是 DisabledText，其中的
    /// 元组语法无法参与语义改写，只能原样保留。若同一 arity 的声明在活动区域被
    /// 降级成生成 struct，编译该分支的工程就会在原生 ValueTuple 与生成 struct
    /// 之间报 CS0030/CS0029（例如 CharSetSolver 的
    /// <c>foreach ((uint, uint) range in BDDRangeConverter.ToRanges(set))</c>，
    /// 而 ToRanges 的返回类型已被降级）。因此这些 arity 必须保持原生。
    ///
    /// 做法：用本文件条件指令里出现的符号重新解析整份文件（位置与原文一一对应），
    /// 只统计落在原解析禁用区域内的元组节点，避免把活动区域的元组误判成"不可改写"。
    /// </summary>
    public static IReadOnlyCollection<int> FindUnrewritableTupleArities(SyntaxNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.SyntaxTree is not CSharpSyntaxTree tree)
        {
            return Array.Empty<int>();
        }

        var disabledSpans = root
            .DescendantTrivia(descendIntoTrivia: true)
            .Where(trivia => trivia.IsKind(SyntaxKind.DisabledTextTrivia))
            .Select(trivia => trivia.Span)
            .Where(span => span.Length > 0)
            .ToList();
        if (disabledSpans.Count == 0)
        {
            return Array.Empty<int>();
        }

        var symbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.GetStructure() is not DirectiveTriviaSyntax directive)
            {
                continue;
            }

            if (directive is DefineDirectiveTriviaSyntax define)
            {
                symbols.Add(define.Name.ValueText);
                continue;
            }

            ExpressionSyntax? condition = directive switch
            {
                IfDirectiveTriviaSyntax ifDirective => ifDirective.Condition,
                ElifDirectiveTriviaSyntax elifDirective => elifDirective.Condition,
                _ => null,
            };

            if (condition == null)
            {
                continue;
            }

            foreach (var identifier in condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                symbols.Add(identifier.Identifier.ValueText);
            }
        }

        if (symbols.Count == 0)
        {
            return Array.Empty<int>();
        }

        var options = tree.Options.WithPreprocessorSymbols(
            tree.Options.PreprocessorSymbolNames.Concat(symbols).Distinct(StringComparer.Ordinal));
        var reparsed = CSharpSyntaxTree.ParseText(tree.GetText(), options, path: tree.FilePath);

        var arities = new HashSet<int>();
        foreach (var node in reparsed.GetRoot().DescendantNodes())
        {
            var arity = node switch
            {
                TupleTypeSyntax tupleType => tupleType.Elements.Count,
                TupleExpressionSyntax tupleExpression => tupleExpression.Arguments.Count,
                _ => 0,
            };
            if (arity > 0 && disabledSpans.Any(span => span.Contains(node.Span)))
            {
                arities.Add(arity);
            }
        }

        return arities;
    }

    /// <summary>逻辑元素个数 → ValueTuple 泛型元数（8 个以上元素经 TRest 嵌套，泛型元数为 8）。</summary>
    internal static int RuntimeGenericArity(int logicalArity) => logicalArity >= 8 ? 8 : logicalArity;

    /// <summary>
    /// True when lowering a tuple of the given shape would change the runtime type
    /// identity that <c>typeof(ValueTuple&lt;...&gt;)</c> checks observe for that
    /// generic arity.
    /// </summary>
    private bool RuntimeIdentityDenied(TupleShape shape)
        => RuntimeIdentityDenied(shape, _runtimeObservedArities);

    /// <summary>
    /// True when lowering this shape would change the runtime type identity that
    /// <c>typeof(ValueTuple&lt;...&gt;)</c> checks observe for that generic arity.
    /// Tail shapes that keep native syntax keep the enclosing shape native as well:
    /// the generated field would otherwise reference a struct that is never emitted.
    /// </summary>
    internal static bool RuntimeIdentityDenied(TupleShape shape, IReadOnlyCollection<int> observedArities)
    {
        if (observedArities.Contains(RuntimeGenericArity(shape.Arity)))
        {
            return true;
        }

        return shape.Tail != null && RuntimeIdentityDenied(shape.Tail, observedArities);
    }

    /// <summary>
    /// 站点级运行时身份判定：元组自身（及其 ValueTuple 尾部嵌套与元素里的嵌套元组）
    /// 的任一泛型元数被 <c>typeof(ValueTuple&lt;...&gt;)</c> 或不可改写的条件编译分支
    /// 观测到时，该站点必须保持原生 ValueTuple。
    /// </summary>
    internal static bool RuntimeIdentityDenied(
        INamedTypeSymbol tupleType,
        IReadOnlyCollection<int> observedArities)
    {
        var elements = tupleType.TupleElements;
        var remaining = elements.Length;
        while (remaining > 0)
        {
            if (observedArities.Contains(RuntimeGenericArity(remaining)))
            {
                return true;
            }

            if (remaining <= 7)
            {
                break;
            }

            remaining -= 7;
        }

        foreach (var element in elements)
        {
            if (element.Type is INamedTypeSymbol { IsTupleType: true } nested
                && RuntimeIdentityDenied(nested, observedArities))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 生成 struct 集合文件内容；没有任何元组时返回 null。
    /// <paramref name="runtimeObservedArities"/> 为该项目（或该编译单元）观测到的
    /// 运行时元数；为 null 时使用进程级默认集合。按项目生成时必须传入项目自己的
    /// 集合，否则别的项目的 typeof 观测会让本项目的形状既不降级也不生成（悬空引用）。
    /// </summary>
    public string? GenerateStructFile(IReadOnlyCollection<int>? runtimeObservedArities = null)
    {
        var observed = runtimeObservedArities ?? _runtimeObservedArities;

        // Shapes whose runtime ValueTuple identity is observed keep native syntax,
        // so they must not be emitted either. Element accessibility no longer
        // matters: generated fields are the struct's own generic parameters.
        var emittable = _registry.Shapes
            .Where(shape => !RuntimeIdentityDenied(shape, observed))
            .ToList();
        return emittable.Count == 0
            ? null
            : TupleStructFileContent.Generate(emittable, _options.NamespaceName);
    }

    public IReadOnlyCollection<string> StructNames =>
        _registry.Shapes
            .Where(shape => !RuntimeIdentityDenied(shape))
            .Select(shape => shape.Name)
            .ToArray();

    /// <summary>
    /// 每个被改写文件用到的形状键（含 Rest 尾部闭包，以及作为类型实参出现的嵌套元组
    /// 形状）。按编译单元生成 struct 文件时需要它：一个源文件可能被多个程序集分别编译，
    /// 只生成本编译单元真正需要的形状即可。
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ShapesByFile =>
        _shapesByFile.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.Select(shape => shape.Key).ToArray(),
            StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, HashSet<TupleShape>> _shapesByFile =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取指定形状集合的闭包（Rest 尾部形状），并保持注册顺序稳定。</summary>
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

    /// <summary>
    /// 该形状是否可以由引用它的其他程序集复用。生成 struct 的字段是自身泛型参数，
    /// 恒为 public，因此只要形状已登记即可跨程序集共享（分配逻辑据此选出唯一所有者）。
    /// </summary>
    public bool IsShapePubliclyAccessible(string shapeKey) =>
        _registry.FindByKey(shapeKey) != null;

    /// <summary>
    /// 按形状键筛选生成 struct 文件内容；没有匹配形状时返回 null。
    /// <paramref name="runtimeObservedArities"/> 语义同无键重载：按项目生成时传入
    /// 该项目自己的观测集合。
    /// </summary>
    public string? GenerateStructFile(
        IEnumerable<string> shapeKeys,
        IReadOnlyCollection<int>? runtimeObservedArities = null,
        bool internalAccessibility = false)
    {
        var observed = runtimeObservedArities ?? _runtimeObservedArities;
        var keys = new HashSet<string>(shapeKeys, StringComparer.Ordinal);
        var shapes = _registry.Shapes
            .Where(shape => keys.Contains(shape.Key) && !RuntimeIdentityDenied(shape, observed))
            .ToList();
        return shapes.Count == 0
            ? null
            : TupleStructFileContent.Generate(shapes, _options.NamespaceName, internalAccessibility);
    }

    /// <summary>
    /// 该形状能否在指定观测集合下真正生成（运行时要保持原生 ValueTuple 的 arity 不能
    /// 落地成 struct）。跨程序集分配定义者时必须用它筛选：定义者自身观测到该 arity 时
    /// 生成不出类型，会让其他使用方的引用悬空。
    /// </summary>
    public bool CanGenerateShape(string shapeKey, IReadOnlyCollection<int> runtimeObservedArities)
        => _registry.FindByKey(shapeKey) is { } shape
           && !RuntimeIdentityDenied(shape, runtimeObservedArities);

    /// <summary>
    /// 改写单个文件。<paramref name="runtimeObservedArities"/> 是该文件所属项目
    /// 观测到的运行时元数集合（null 表示沿用进程级默认集合）。项目级调用必须按文件
    /// 传入所属项目的集合，使 <c>typeof(ValueTuple&lt;...&gt;)</c> 的观测只在该项目内
    /// 生效；同一项目内所有文件拿到同一个集合，保证 arity 级一致（struct /
    /// ValueTuple 混用会 CS0029）。
    /// </summary>
    public TupleToStructFileResult RewriteFile(
        string sourceCode,
        SemanticModel semanticModel,
        TupleToStructStatistics statistics,
        List<TupleToStructDiagnostic> diagnostics,
        IReadOnlyCollection<int>? runtimeObservedArities = null)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var observedArities = runtimeObservedArities ?? _runtimeObservedArities;
        statistics.FilesScanned++;

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();

        // The syntax-only dirty check misses files that never spell tuple
        // syntax themselves but consume tuples declared elsewhere through
        // element access (named elements like arr[0].CharClass or plain ItemN).
        // The declaration file gets its tuple types replaced while such a
        // consumer file would be copied verbatim, leaving the old element name
        // against the generated struct (CS1061). The semantic gate catches them.
        if (!IsTupleDirty(root) && !HasTupleElementUse(semanticModel, root))
        {
            statistics.FilesSkippedClean++;
            return new TupleToStructFileResult { OutputCode = sourceCode, Changed = false };
        }

        var rewriter = new TupleSyntaxRewriter(
            semanticModel, _registry, _options.NamespaceName, statistics, diagnostics,
            _shapesByFile, semanticModel.SyntaxTree.FilePath, observedArities);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (rewriter.Changed)
        {
            newRoot = (CompilationUnitSyntax)new StatementSplicer(rewriter.Expansions).Visit(newRoot)!;
        }

        // 全部 tuple 改写完成后再做局部声明归一化：已经降级为 Cs2jTuples struct 的
        // 声明不再是 TupleTypeSyntax，只有刻意保留原生 ValueTuple 的局部声明会命中。
        // 该步骤独立于 rewriter.Changed —— 一个文件可能只含被保留的元组语法，
        // 不触发任何其它改写，却仍需消除局部声明里的 TupleTypeSyntax。
        var normalizer = new TupleLocalDeclarationNormalizer(statistics);
        newRoot = (CompilationUnitSyntax)normalizer.Visit(newRoot)!;

        if (!rewriter.Changed && !normalizer.Changed)
        {
            statistics.FilesSkippedClean++;
            return new TupleToStructFileResult { OutputCode = sourceCode, Changed = false };
        }

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

    /// <summary>
    /// 语义脏检查：文件自身不含 tuple 语法，但通过成员访问（具名元素 <c>arr[0].CharClass</c>
    /// 或 <c>t.Item1</c>、条件访问 <c>t?.X</c>）消费在别处声明的元组元素时仍需改写。
    /// 仅语法检查会让这种纯消费文件整体跳过，声明点换成生成 struct 后使用点残留旧名 → CS1061。
    /// </summary>
    internal static bool HasTupleElementUse(SemanticModel semanticModel, SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (node is not (MemberAccessExpressionSyntax or MemberBindingExpressionSyntax))
            {
                continue;
            }

            if (IsTupleElementField(semanticModel.GetSymbolInfo(node).Symbol))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>符号是否为值元组的 ItemN 元素字段（具名元素在语义模型中仍绑到 ItemN）。</summary>
    internal static bool IsTupleElementField(ISymbol? symbol)
    {
        if (symbol is not IFieldSymbol field
            || field.ContainingType is not { IsTupleType: true })
        {
            return false;
        }

        var tupleField = field.CorrespondingTupleField ?? field;
        return Regex.IsMatch(tupleField.Name, @"^Item(\d+)$");
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

    /// <summary>
    /// 消除局部声明中残存的 <see cref="TupleTypeSyntax"/>。Java 门禁
    /// (StatementTransformer.TransformLocalDeclaration) 只要局部声明类型是元组类型就
    /// 报 "deconstruction local declaration"，即便它只是一个普通元组类型变量。
    /// <c>(T1, T2) name = init;</c> 改写为 <c>var name = init;</c>；无初始化器时改写为
    /// <c>var name = default((T1, T2));</c>。推断出的类型仍是 System.ValueTuple，
    /// 语义与运行时反射身份（typeof(ValueTuple&lt;...&gt;)）不变。
    /// 只在所有其它 tuple 改写之后运行：已降级为 Cs2jTuples struct 的声明不再是
    /// TupleTypeSyntax，因此不会被命中。多变量声明与具名元素保持原样（拆分会改变
    /// 求值顺序/作用域；<c>default((int x, int y))</c> 不是合法表达式）。
    /// </summary>
    private sealed class TupleLocalDeclarationNormalizer : CSharpSyntaxRewriter
    {
        private readonly TupleToStructStatistics _stats;

        public TupleLocalDeclarationNormalizer(TupleToStructStatistics stats)
        {
            _stats = stats;
        }

        public bool Changed { get; private set; }

        public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            var updated = (LocalDeclarationStatementSyntax)base.VisitLocalDeclarationStatement(node)!;

            if (updated.Declaration.Type is not TupleTypeSyntax tupleType
                || updated.Declaration.Variables.Count != 1)
            {
                return updated;
            }

            // const 局部不能是元组类型；遇到时不改动（防御性，正常源码不会出现）。
            if (updated.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)))
            {
                return updated;
            }

            // 具名元素无法表示为 default((T1 a, T2 b))（不是合法表达式），必须原样保留。
            if (tupleType.Elements.Any(e => !e.Identifier.IsKind(SyntaxKind.None)))
            {
                return updated;
            }

            var declarator = updated.Declaration.Variables[0];
            if (declarator.Initializer == null)
            {
                declarator = declarator.WithInitializer(
                    SyntaxFactory.EqualsValueClause(BuildDefaultTupleExpression(tupleType)));
            }
            else if (declarator.Initializer.Value.IsKind(SyntaxKind.DefaultLiteralExpression))
            {
                // `var v = default;` 没有目标类型（CS8716）：把无类型 default 字面量
                // 换成带元组类型的 default((T1, T2))。
                declarator = declarator.WithInitializer(
                    SyntaxFactory.EqualsValueClause(
                        BuildDefaultTupleExpression(tupleType)
                            .WithTriviaFrom(declarator.Initializer.Value)));
            }

            var varType = SyntaxFactory.IdentifierName("var").WithTriviaFrom(tupleType);
            Changed = true;
            _stats.TupleLocalsNormalizedToVar++;
            return updated
                .WithDeclaration(updated.Declaration
                    .WithType(varType)
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(declarator)))
                .WithAdditionalAnnotations(s_rewriteAnnotation);
        }

        /// <summary>构造 <c>default((Element1, Element2, ...))</c>（元素名已保证不存在）。</summary>
        private static ExpressionSyntax BuildDefaultTupleExpression(TupleTypeSyntax tupleType)
            => SyntaxFactory.DefaultExpression(
                SyntaxFactory.TupleType(
                    SyntaxFactory.SeparatedList(
                        tupleType.Elements.Select(
                            element => SyntaxFactory.TupleElement(
                                element.Type.WithoutTrivia(), default)))));
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
    private readonly IReadOnlyCollection<int> _runtimeObservedArities;

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

    /// <summary>
    /// Register and return the shape for <paramref name="tupleType"/>. Whether the site
    /// may be lowered depends on the SITE's element types (not on the shared shape,
    /// which is one per logical arity): a tuple element that a top-level generated
    /// struct could not name, or an arity whose runtime System.ValueTuple identity is
    /// observed in the compilation (typeof(ValueTuple&lt;...&gt;)), keeps the native
    /// ValueTuple syntax. Returns null (warning once) for them.
    /// </summary>
    private TupleShape? GetEmittableShape(INamedTypeSymbol tupleType)
    {
        var shape = _registry.GetOrCreateShape(tupleType);
        if (!TupleShapeRegistry.IsEmittableFromTopLevel(tupleType))
        {
            WarnOnce("shape-not-emittable:" + shape.Key,
                "A tuple uses a private or protected type that is not accessible from " +
                "the generated top-level tuple struct; the site was kept as a native " +
                "System.ValueTuple.");
            return null;
        }

        if (KeepsRuntimeValueTupleIdentity(tupleType))
        {
            WarnOnce("shape-runtime-identity:" + shape.Key,
                $"{TupleToStructPreprocessor.RuntimeGenericArity(shape.Arity)}-element tuples must stay " +
                "native System.ValueTuple in this compilation (either typeof(ValueTuple<...>) observes " +
                "their runtime identity, or a conditional-compilation branch this pass cannot rewrite " +
                "contains tuple syntax); the site was kept as a native System.ValueTuple.");
            return null;
        }

        return TrackShape(shape);
    }

    /// <summary>
    /// True when lowering this site would change the runtime type identity that
    /// <c>typeof(ValueTuple&lt;...&gt;)</c> checks observe for that generic arity.
    /// </summary>
    private bool KeepsRuntimeValueTupleIdentity(INamedTypeSymbol tupleType) =>
        TupleToStructPreprocessor.RuntimeIdentityDenied(tupleType, _runtimeObservedArities);

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
        string filePath,
        IReadOnlyCollection<int> runtimeObservedArities)
    {
        _model = model;
        _registry = registry;
        _namespace = ns;
        _stats = stats;
        _diagnostics = diagnostics;
        _shapesByFile = shapesByFile;
        _filePath = filePath;
        _runtimeObservedArities = runtimeObservedArities;
    }

    // ------------------------------------------------------------------
    // 元组类型与字面量
    // ------------------------------------------------------------------

    public override SyntaxNode? VisitTupleType(TupleTypeSyntax node)
    {
        if (GetTupleTypeSymbol(node) is { } symbol)
        {
            // Shapes with inaccessible element types stay native; children may
            // still contain independently emittable tuples, so visit normally.
            if (GetEmittableShape(symbol) is not { } shape)
            {
                return base.VisitTupleType(node);
            }

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
            if (GetEmittableShape(symbol) is not { } shape)
            {
                return base.VisitQualifiedName(node);
            }

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
            if (GetEmittableShape(symbol) is not { } shape)
            {
                return base.VisitGenericName(node);
            }

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

        if (node.Parent is ForEachVariableStatementSyntax foreachStatement
            && foreachStatement.Variable == node)
        {
            // foreach 的解构声明列表不是值元组字面量：元素类型可能是带
            // Deconstruct 的非元组类型（如 JsonDerivedType），此时该节点只是
            // 解构语法。把它当元组字面量改写成 struct 构造会产出非法 foreach
            // 头（Type a 变成构造参数），整文件因此回滚。元组元素类型由
            // VisitForEachVariableStatement 的展开分支处理，不会走到这里。
            return base.VisitTupleExpression(node);
        }

        var info = _model.GetTypeInfo(node);
        var symbol = TupleLiteralType(info.ConvertedType) ?? TupleLiteralType(info.Type);
        if (symbol is not { IsTupleType: true })
        {
            WarnOnce("tuple-literal",
                "Could not infer the tuple type of a tuple literal; it was left unchanged.");
            return base.VisitTupleExpression(node);
        }

        if (GetEmittableShape(symbol) is not { } shape)
        {
            return base.VisitTupleExpression(node);
        }

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
            if (GetEmittableShape(tupleReturn) is not { } shape)
            {
                return base.VisitInvocationExpression(node);
            }

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

        // Native tuple signatures kept for inaccessible-element shapes still
        // need their [TupleElementNames] so named element access keeps binding;
        // stripping it would turn e.g. `result.CharRefEndPos` into CS1061.
        if (updated.Attributes.Any(a => IsTupleElementNamesAttribute(a.Name))
            && AttributeTargetHasDeniedTuple(node))
        {
            return updated;
        }

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

    /// <summary>
    /// True when the declaration carrying a <c>[TupleElementNames]</c> list uses a
    /// tuple shape that stays native (inaccessible elements). The attribute encodes
    /// element names on the raw signature and must be retained together with it.
    /// </summary>
    private bool AttributeTargetHasDeniedTuple(AttributeListSyntax attributeList)
    {
        IEnumerable<ITypeSymbol> typeSymbols = attributeList.Parent switch
        {
            BaseMethodDeclarationSyntax m => MethodSignatureTypes(_model.GetDeclaredSymbol(m)),
            LocalFunctionStatementSyntax l => MethodSignatureTypes(_model.GetDeclaredSymbol(l)),
            DelegateDeclarationSyntax d => DelegateSignatureTypes(_model.GetDeclaredSymbol(d)),
            ParameterSyntax p => new[] { _model.GetDeclaredSymbol(p)?.Type! },
            PropertyDeclarationSyntax or IndexerDeclarationSyntax
                => PropertySignatureTypes(_model.GetDeclaredSymbol((MemberDeclarationSyntax)attributeList.Parent!)),
            _ => Array.Empty<ITypeSymbol>(),
        };

        return typeSymbols.Any(t => t != null && ContainsDeniedTupleShape(t));
    }

    private static IEnumerable<ITypeSymbol> MethodSignatureTypes(ISymbol? symbol)
    {
        if (symbol is not IMethodSymbol method)
        {
            yield break;
        }

        yield return method.ReturnType;
        foreach (var parameter in method.Parameters)
        {
            yield return parameter.Type;
        }
    }

    private static IEnumerable<ITypeSymbol> DelegateSignatureTypes(ISymbol? symbol)
    {
        if (symbol is INamedTypeSymbol { DelegateInvokeMethod: { } invoke })
        {
            yield return invoke.ReturnType;
            foreach (var parameter in invoke.Parameters)
            {
                yield return parameter.Type;
            }
        }
    }

    private static IEnumerable<ITypeSymbol> PropertySignatureTypes(ISymbol? symbol)
    {
        switch (symbol)
        {
            case IPropertySymbol property:
                yield return property.Type;
                foreach (var parameter in property.Parameters)
                {
                    yield return parameter.Type;
                }

                break;
            case IEventSymbol @event:
                yield return @event.Type;
                break;
        }
    }

    /// <summary>Recursively locate a tuple type that cannot be emitted at top level.</summary>
    private bool ContainsDeniedTupleShape(ITypeSymbol type)
    {
        switch (type)
        {
            case INamedTypeSymbol { IsTupleType: true } tuple:
            {
                if (!TupleShapeRegistry.IsEmittableFromTopLevel(tuple)
                    || KeepsRuntimeValueTupleIdentity(tuple))
                {
                    return true;
                }

                foreach (var field in tuple.TupleElements)
                {
                    if (ContainsDeniedTupleShape(field.Type))
                    {
                        return true;
                    }
                }

                return false;
            }

            case INamedTypeSymbol named:
                return named.TypeArguments.Any(ContainsDeniedTupleShape);

            case IArrayTypeSymbol array:
                return ContainsDeniedTupleShape(array.ElementType);

            default:
                return false;
        }
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
            || field.ContainingType is not { IsTupleType: true } containingTuple
            || !TupleToStructPreprocessor.IsTupleElementField(symbol))
        {
            return null;
        }

        var tupleField = field.CorrespondingTupleField ?? field;
        var ordinal = int.Parse(Regex.Match(tupleField.Name, @"^Item(\d+)$").Groups[1].Value);
        // Shapes kept native (inaccessible elements or observed runtime ValueTuple
        // identity) keep their original .ItemN / named-element access — the warning
        // is emitted by the type site; here returning null silently preserves it.
        if (!TupleShapeRegistry.IsEmittableFromTopLevel(containingTuple)
            || KeepsRuntimeValueTupleIdentity(containingTuple))
        {
            return null;
        }

        var shape = TrackShape(_registry.GetOrCreateShape(containingTuple));
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

                if (GetEmittableShape(targetTuple) is not { } shape)
                {
                    return base.VisitCastExpression(node);
                }

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

        if (GetEmittableShape(target) is not { } shape)
        {
            return null;
        }

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
            && GetTupleDeconstructionSource(declarationAssignment.Right) is { } declaredTuple
            && GetEmittableShape(declaredTuple) is { } shape)
        {
            var visitedValue = (ExpressionSyntax)Visit(declarationAssignment.Right)!;
            var statements = new List<StatementSyntax>();
            var temp = NextTemp();
            statements.Add(LocalDecl(temp, visitedValue));
            statements.AddRange(ExpandDesignation(declaredTuple, shape, declarationDesignation, temp));
            return FinishExpansion(node, statements);
        }

        // (a, b) = e;  —— 解构赋值
        if (node.Expression is AssignmentExpressionSyntax
            {
                Left: TupleExpressionSyntax tupleLeft,
            } assignment)
        {
            if (GetTupleDeconstructionSource(assignment.Right) is { } tupleType
                && GetEmittableShape(tupleType) is { } assignmentShape)
            {
                var visitedRight = (ExpressionSyntax)Visit(assignment.Right)!;
                var statements = new List<StatementSyntax>();
                var temp = NextTemp();
                statements.Add(LocalDecl(temp, visitedRight));
                statements.AddRange(ExpandTupleExpressionTargets(tupleType, assignmentShape, tupleLeft, temp));
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

            // Denied shapes keep native tuple syntax — including the pattern —
            // so skip the lift for this occurrence.
            if (GetEmittableShape(tupleType) is not { } shape)
            {
                continue;
            }

            var temp = NextTemp();
            var visitedOperand = (ExpressionSyntax)Visit(isExpression.Expression)!;
            statements.Add(LocalDecl(temp, visitedOperand));
            statements.AddRange(ExpandDesignation(tupleType, shape, pattern.Designation, temp));
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
            && GetTupleDeconstructionSource(initAssignment.Right) is { } initTuple
            && GetEmittableShape(initTuple) is { } shape)
        {
            var visitedValue = (ExpressionSyntax)Visit(initAssignment.Right)!;
            var preamble = new List<StatementSyntax>();
            var temp = NextTemp();
            preamble.Add(LocalDecl(temp, visitedValue));
            preamble.AddRange(ExpandDesignation(initTuple, shape, designation, temp));

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

        // for (;;) 无条件：Condition 为 null，而 while/do 的条件在语法上必有。
        if (node.Condition != null)
        {
            WarnIfConditionDeconstruction(node.Condition, "for");
        }

        return base.VisitForStatement(node);
    }

    public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
    {
        var elementType = _model.GetForEachStatementInfo(node).ElementType as INamedTypeSymbol;
        if (elementType is not { IsTupleType: true }
            || GetEmittableShape(elementType) is not { } shape)
        {
            return base.VisitForEachVariableStatement(node);
        }

        var temp = NextTemp();
        var declarations = new List<StatementSyntax>();
        StatementSyntax result;

        if (node.Variable is DeclarationExpressionSyntax
            {
                Designation: ParenthesizedVariableDesignationSyntax varDesignation,
            })
        {
            declarations.AddRange(ExpandDesignation(elementType, shape, varDesignation, temp));
        }
        else if (node.Variable is TupleExpressionSyntax tupleVariables)
        {
            declarations.AddRange(
                ExpandTupleExpressionTargets(elementType, shape, tupleVariables, temp));
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
        INamedTypeSymbol tupleType,
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
                    when NestedTupleType(tupleType, i) is { } nestedTuple:
                    var nestedTemp = NextTemp();
                    yield return LocalDecl(nestedTemp, access);
                    var nestedShape = _registry.GetOrCreateShape(nestedTuple);
                    foreach (var statement in ExpandDesignation(nestedTuple, nestedShape, nested, nestedTemp))
                    {
                        yield return statement;
                    }

                    break;
            }
        }
    }

    private IEnumerable<StatementSyntax> ExpandTupleExpressionTargets(
        INamedTypeSymbol tupleType,
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
                    } when NestedTupleType(tupleType, i) is { } nestedTuple:
                    var nestedTemp = NextTemp();
                    yield return LocalDecl(nestedTemp, access);
                    var nestedShape = _registry.GetOrCreateShape(nestedTuple);
                    foreach (var statement in ExpandDesignation(nestedTuple, nestedShape, nested, nestedTemp))
                    {
                        yield return statement;
                    }

                    break;

                case TupleExpressionSyntax nestedTupleSyntax
                    when NestedTupleType(tupleType, i) is { } nestedTuple2:
                    var nestedTemp2 = NextTemp();
                    yield return LocalDecl(nestedTemp2, access);
                    var nestedShape2 = _registry.GetOrCreateShape(nestedTuple2);
                    foreach (var statement in ExpandTupleExpressionTargets(
                                 nestedTuple2, nestedShape2, nestedTupleSyntax, nestedTemp2))
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

    /// <summary>
    /// 解构目标第 <paramref name="index"/> 个元素自身是元组时的类型。生成的 struct 按
    /// 元数共享定义，嵌套结构只存在于站点类型实参里，所以嵌套判定必须回到解构源的
    /// 元组类型上，而不是形状上。
    /// </summary>
    private static INamedTypeSymbol? NestedTupleType(INamedTypeSymbol tupleType, int index)
        => index < tupleType.TupleElements.Length
            && tupleType.TupleElements[index].Type is INamedTypeSymbol { IsTupleType: true } nested
                ? nested
                : null;

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

    /// <summary>
    /// 在当前作用域内渲染 struct 实例化：按元素位置把实际元素类型作为类型实参传入。
    /// 同一元数的 struct 定义只有一个，开放形态（<c>(T1, T2)</c> → <c>T1, T2</c>）与
    /// 具体形态（<c>(string, int)</c> → <c>string, int</c>）因此得到同一个 C# 类型。
    /// </summary>
    private string ShapeDisplayInScope(TupleShape shape, INamedTypeSymbol tupleSymbol)
    {
        if (shape.GenericArity == 0)
        {
            return shape.Name;
        }

        var args = tupleSymbol.TupleElements
            .Take(shape.GenericArity)
            .Select(field => DisplayTypeInScope(field.Type));
        return shape.Name + "<" + string.Join(", ", args) + ">";
    }

    private ExpressionSyntax BuildStructCreation(
        TupleShape shape,
        INamedTypeSymbol tupleSymbol,
        IReadOnlyList<ExpressionSyntax> arguments)
    {
        // 元素类型在本 pass 的合并编译里无法解析（缺引用/命名空间歧义会让 out var、
        // lambda 参数等绑定失败）时，站点上的类型实参不可知；把 error type 物化成
        // object 会生成 TupleT0T1<object, object>，而真实编译里的同一定义是
        // TupleT0T1<TElement, TPriority>，两者是不同的 C# 类型（CS1503/CS0266）。
        // 改走生成的泛型工厂方法，由真正编译该文件的编译器按实参推断元素类型。
        if (shape.GenericArity > 0 && HasUnresolvedElements(tupleSymbol))
        {
            return BuildInferredStructCreation(shape, arguments);
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

    private static bool HasUnresolvedElements(INamedTypeSymbol tupleSymbol) =>
        tupleSymbol.TupleElements.Any(field => field.Type is IErrorTypeSymbol);

    /// <summary>
    /// 元素类型未知时的 struct 构造：调用按元数生成的泛型工厂
    /// （<c>TupleT0T1Factory.Create</c>），不给显式类型实参。工厂方法的类型参数由实参
    /// 推断，开放站点（<c>TElement</c>）与具体站点（<c>int</c>）因此都落到在真实编译
    /// 里唯一的那个 struct 实例化上。
    /// </summary>
    private ExpressionSyntax BuildInferredStructCreation(
        TupleShape shape,
        IReadOnlyList<ExpressionSyntax> arguments)
        => SyntaxFactory.InvocationExpression(
            SyntaxFactory.ParseExpression(
                $"global::{_namespace}.{shape.Name}Factory.Create"),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(arguments.Select(SyntaxFactory.Argument))));

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

    /// <summary>作用域内类型文本（泛型参数用源码名），用作 struct 的类型实参与元素级显式转换。</summary>
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

            // 无法解析的类型（缺少引用的编译单元）与匿名类型都不可能在生成代码里
            // 具名；物化为 object（与旧版形状字段的降级行为一致）。
            case IErrorTypeSymbol:
                return "global::System.Object";

            case INamedTypeSymbol named:
            {
                if (named.IsTupleType)
                {
                    var shape = TrackShape(_registry.GetOrCreateShape(named));
                    // 嵌套元组作为类型实参出现在任意站点作用域里，必须完整限定。
                    return "global::" + _namespace + "." + ShapeDisplayInScope(shape, named);
                }

                if (named.IsAnonymousType)
                {
                    return "global::System.Object";
                }

                var definition = named.OriginalDefinition;
                var name = definition.MetadataName ?? definition.Name;
                var index = name.LastIndexOf('`');
                if (index >= 0)
                {
                    name = name[..index];
                }

                var prefix = TupleShapeRegistry.QualifiedMetadataPrefix(named, DisplayTypeInScope);
                if (prefix == null)
                {
                    return "global::System.Object";
                }

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

    /// <summary>尾部 struct 在源码作用域内的实例化文本：尾部元素按位置作为类型实参。</summary>
    private string TailDisplayInScope(TupleShape tailShape, INamedTypeSymbol outerTupleSymbol)
    {
        if (tailShape.GenericArity == 0)
        {
            return tailShape.Name;
        }

        var args = outerTupleSymbol.TupleElements
            .Skip(7)
            .Take(tailShape.GenericArity)
            .Select(field => DisplayTypeInScope(field.Type));
        return tailShape.Name + "<" + string.Join(", ", args) + ">";
    }

    // ------------------------------------------------------------------
    // 杂项
    // ------------------------------------------------------------------

    /// <summary>
    /// 解构源表达式的值元组类型：只看自然类型。解构（<c>(int a, int b) = source</c> /
    /// <c>var (a, b) = source</c>）里 Roslyn 会把 source 的 ConvertedType 报成解构
    /// 目标形状（由 <c>Deconstruct</c> 的 out 参数推导），据此展开会访问 DateOnly /
    /// KeyValuePair 等非元组源上不存在的 ItemN（CS1061）。自然类型不是元组时说明
    /// 该解构走 Deconstruct 方法，必须保留原语法。
    /// </summary>
    private INamedTypeSymbol? GetTupleDeconstructionSource(ExpressionSyntax expression)
        => _model.GetTypeInfo(expression).Type is INamedTypeSymbol { IsTupleType: true } natural
            ? natural
            : null;

    /// <summary>
    /// 表达式（含 out var 解构声明）的元组类型：优先 ConvertedType（out 参数类型/
    /// 赋值目标类型），否则自然类型。注意 ConvertedType 在解构源上会被报成解构目标
    /// 形状，解构判断必须用 <see cref="GetTupleDeconstructionSource"/>。
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

    /// <summary>
    /// 元组字面量可用的元组类型。目标类型可能是可空值元组
    /// <c>Nullable&lt;ValueTuple&lt;...&gt;&gt;</c>（返回类型 / 赋值目标写成 <c>(T1, T2)?</c>）；
    /// 元素含 <c>null</c> 的元组字面量没有自然类型，只检查 ConvertedType 本身会漏掉这层
    /// 可空包装，导致字面量保留原生元组语法而内层字面量已被降级，产出
    /// <c>(x, new Cs2jTuples.Tuple...(...))</c> 无法转换到生成 struct（CS0029）。
    /// </summary>
    private static INamedTypeSymbol? TupleLiteralType(ITypeSymbol? type)
    {
        if (type is INamedTypeSymbol { IsTupleType: true } tuple)
        {
            return tuple;
        }

        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            && nullable.TypeArguments.Length == 1
            && nullable.TypeArguments[0] is INamedTypeSymbol { IsTupleType: true } underlying)
        {
            return underlying;
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
