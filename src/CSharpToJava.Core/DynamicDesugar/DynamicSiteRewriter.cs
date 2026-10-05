using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.DynamicDesugar;

/// <summary>
/// C25 M2 A1 重写器（设计 §4.5/D10/D14）：仅把 <see cref="ProvenDeclaration"/> 的
/// 声明类型语法（显式 <c>dynamic</c>、隐式 dynamic 的 <c>var</c>、foreach/catch 类型位）
/// 具体化为证明类型 T；使用点（成员/调用/运算/实参）一律不改——重编译时自动静态重绑，
/// 避免「声明改了用点没改」的半改写窗口（D10）。
/// 不生成新文件、不注入支撑库、不改任何签名（参数/返回/字段 DS011/DS012 永不自动重写）。
/// </summary>
internal static class DynamicSiteRewriter
{
    /// <summary>
    /// 仅内存节点标注（不落文本）：标出被具体化的类型语法，供改动区间格式化选取 span、
    /// 幂等识别与测试断言。等价 JK 的 <c>RewriteAnnotation</c>（其为 internal，
    /// 本 pass 自定义同形态标注，不跨 pass 复用）。
    /// </summary>
    public static SyntaxAnnotation RewriteAnnotation { get; } = new("Cs2jDynamicDesugarRewrite");

    private static readonly SymbolDisplayFormat MinimalConcreteTypeFormat =
        SymbolDisplayFormat.MinimallyQualifiedFormat
            .WithMiscellaneousOptions(
                SymbolDisplayFormat.MinimallyQualifiedFormat.MiscellaneousOptions
                | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly SymbolDisplayFormat FullyQualifiedConcreteTypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat
            .WithMiscellaneousOptions(
                SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
                | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>
    /// A1 重写结果：新根（未格式化）、被具体化的声明数（共享 VariableDeclaration 计 1）、
    /// 被具体化声明对应的声明节点集合（VariableDeclarator/ForEach/Catch 原节点引用）。
    /// </summary>
    public sealed class ApplyResult
    {
        public required CompilationUnitSyntax Root { get; init; }
        public required int DeclarationsRewritten { get; init; }
        public required HashSet<SyntaxNode> ConcretizedDeclarators { get; init; }
    }

    /// <summary>
    /// 对整棵编译单元执行 A1。<paramref name="proven"/> 必须是本文件语义树上的
    /// <see cref="ProvenDeclaration"/>（节点引用相等）。
    /// </summary>
    public static ApplyResult Apply(
        SemanticModel model,
        IReadOnlyList<ProvenDeclaration> proven)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(proven);

        var root = model.SyntaxTree.GetCompilationUnitRoot();
        var rewriter = new DeclarationConcretizer(model, proven);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;
        return new ApplyResult
        {
            Root = newRoot,
            DeclarationsRewritten = rewriter.Count,
            ConcretizedDeclarators = rewriter.Concretized,
        };
    }

    /// <summary>
    /// dirty 快检（设计 §4.5/D14 幂等）：文件内无 <c>dynamic</c> token 且语义上无
    /// LateBound 动态节点即 clean。<see cref="CandidateReason.LateBound"/> 是 Roslyn
    /// 实况信号（不存在 CandidateReason.Dynamic 成员，M1 已采用）。clean 文件字节复制。
    /// </summary>
    public static bool IsDynamicDirty(SyntaxNode root, SemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(model);

        // 语法面：dynamic 关键字（上下文关键字，token 种类仍是 IdentifierToken）。
        foreach (var token in root.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.IdentifierToken)
                && token.Text == "dynamic")
            {
                return true;
            }
        }

        // 语义面：晚绑定调用/成员/索引/运算节点（var 隐式 dynamic 残留由此兜出）。
        foreach (var node in root.DescendantNodes())
        {
            if (node is not (InvocationExpressionSyntax
                or MemberAccessExpressionSyntax
                or MemberBindingExpressionSyntax
                or ElementAccessExpressionSyntax
                or BinaryExpressionSyntax
                or AssignmentExpressionSyntax
                or PrefixUnaryExpressionSyntax
                or PostfixUnaryExpressionSyntax
                or AwaitExpressionSyntax))
            {
                continue;
            }

            if (model.GetSymbolInfo(node).CandidateReason == CandidateReason.LateBound)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 构造 T 的可替换类型语法：优先 <c>ToMinimalDisplayString(model, position)</c>
    /// （设计 §4.5，文件同位置可解析的最短形态，如 <c>SealedFoo</c>/<c>int</c>）；
    /// 解析或绑定失败时回退 FullyQualifiedFormat 的 <c>global::</c> 限定形态。
    /// 两种形态都要求零 parse error 且在原模型上绑定回 T；都失败则保留最短形态交
    /// 三候选编译校验兜底（DS040 fail-closed）。
    /// </summary>
    internal static TypeSyntax BuildConcreteTypeSyntax(
        SemanticModel model,
        ITypeSymbol type,
        int position)
    {
        var minimalText = type.ToMinimalDisplayString(model, position, MinimalConcreteTypeFormat);
        var minimal = SyntaxFactory.ParseTypeName(minimalText);
        if (BindsTo(model, minimal, position, type))
        {
            return minimal;
        }

        var fullyQualifiedText = type.ToDisplayString(FullyQualifiedConcreteTypeFormat);
        var fullyQualified = SyntaxFactory.ParseTypeName(fullyQualifiedText);
        if (BindsTo(model, fullyQualified, position, type))
        {
            return fullyQualified;
        }

        // 回退（如 using-alias 特殊上下文）：返回最短形态，编译校验会拦下任何问题。
        return minimal;
    }

    private static bool BindsTo(
        SemanticModel model,
        TypeSyntax typeSyntax,
        int position,
        ITypeSymbol expected)
    {
        if (typeSyntax.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return false;
        }

        // 在声明实际位置做推测绑定（using/命名空间可见性与真实替换位一致）。
        try
        {
            var symbol = model.GetSpeculativeSymbolInfo(
                    position, typeSyntax, SpeculativeBindingOption.BindAsTypeOrNamespace)
                .Symbol as ITypeSymbol;
            if (symbol != null
                && !SymbolEqualityComparer.Default.Equals(symbol, expected)
                && !SymbolEqualityComparer.IncludeNullability.Equals(symbol, expected))
            {
                return false;
            }
        }
        catch (ArgumentException)
        {
            // 个别边界位置不支持推测绑定：不当死刑，交由整文件编译校验终判。
        }

        return true;
    }

    /// <summary>
    /// 三候选选优（设计 D14，复刻 E06 TupleToStruct/JK 形态）：候选 1 仅格式化
    /// <see cref="RewriteAnnotation"/> 标注的改动区间；候选 2 整文件格式化；候选 3
    /// 未格式化原文（重写树 ToFullString，保留全部原 trivia）。每个候选重新 parse
    /// 并在同口径编译上重绑，取零新增 error 的第一个；全败返回 null（调用方保留原文件 + DS040）。
    /// </summary>
    public static string? FormatAndValidate(
        CompilationUnitSyntax root,
        SyntaxTree originalTree,
        CSharpCompilation compilation,
        DynamicRewriteValidator? validator = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(originalTree);
        ArgumentNullException.ThrowIfNull(compilation);

        validator ??= DynamicRewriteValidator.Default;
        var baseline = DynamicRewriteValidator.GetInFileErrorKeys(compilation, originalTree);
        var candidates = new List<DynamicRewriteValidator.Candidate>();

        using (var workspace = new AdhocWorkspace())
        {
            try
            {
                var spans = GetRewrittenSpans(root);
                if (spans.Count > 0)
                {
                    var spanFormatted = Formatter.Format(root, spans, workspace);
                    candidates.Add(new DynamicRewriteValidator.Candidate(
                        DynamicRewriteValidator.CandidateKind.ChangedSpanFormatted,
                        spanFormatted.ToFullString()));
                }
            }
            catch
            {
                // 区间格式化失败不致命，继续整文件格式化。
            }

            try
            {
                var wholeFormatted = Formatter.Format(root, workspace);
                candidates.Add(new DynamicRewriteValidator.Candidate(
                    DynamicRewriteValidator.CandidateKind.WholeFileFormatted,
                    wholeFormatted.ToFullString()));
            }
            catch
            {
                // 整文件格式化失败：仍有未格式化候选兜底。
            }
        }

        candidates.Add(new DynamicRewriteValidator.Candidate(
            DynamicRewriteValidator.CandidateKind.Unformatted,
            root.ToFullString()));

        foreach (var candidate in candidates)
        {
            if (validator.Accepts(candidate, originalTree, compilation, baseline))
            {
                return candidate.Text;
            }
        }

        return null;
    }

    /// <summary>标注类型语法的最外层 span（嵌套标注只取最外层；同 span 去重）。</summary>
    internal static List<TextSpan> GetRewrittenSpans(CompilationUnitSyntax root)
    {
        var annotated = root
            .GetAnnotatedNodes(RewriteAnnotation.Kind)
            .OfType<TypeSyntax>()
            .ToList();
        if (annotated.Count == 0)
        {
            return new List<TextSpan>();
        }

        var annotatedSet = new HashSet<SyntaxNode>(annotated);
        return annotated
            .Where(node => !node.Ancestors().Any(annotatedSet.Contains))
            .Select(node => node.Span)
            .Distinct()
            .OrderBy(span => span.Start)
            .ToList();
    }

    /// <summary>
    /// A1 声明具体化 CSharpSyntaxRewriter。节点替换规则：
    /// <list type="bullet">
    /// <item><see cref="VariableDeclarationSyntax"/>（局部声明显式 dynamic / var 推断
    /// dynamic）：同一声明下所有 dynamic 声明符都被证明且为同一 T 才替换共享类型语法；
    /// 否则整个声明不重写（保守：一个类型语法无法承载多个 T）；</item>
    /// <item><see cref="ForEachStatementSyntax"/>/<see cref="CatchDeclarationSyntax"/>：
    /// 直接替换类型位；</item>
    /// <item><see cref="SingleVariableDesignationSyntax"/>（解构声明/foreach 解构/out var
    /// 分量）：本里程碑不做语法替换（单个分量没有独立类型位可换，需要整体重塑解构形态，
    /// 留待后续）；这些站点保持 dynamic，后续 residual 闸门按残余站点 fail-closed。</item>
    /// </list>
    /// </summary>
    private sealed class DeclarationConcretizer : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _model;
        private readonly Dictionary<SyntaxNode, ITypeSymbol> _proven;

        public DeclarationConcretizer(SemanticModel model, IReadOnlyList<ProvenDeclaration> proven)
        {
            _model = model;
            _proven = new Dictionary<SyntaxNode, ITypeSymbol>(ReferenceEqualityComparer.Instance);
            foreach (var declaration in proven)
            {
                // 解构分量本里程碑不支持语法替换：不收进映射，Visit 时自然跳过。
                if (declaration.Declarator is not SingleVariableDesignationSyntax)
                {
                    _proven[declaration.Declarator] = declaration.Type;
                }
            }
        }

        public int Count { get; private set; }

        public HashSet<SyntaxNode> Concretized { get; } =
            new(ReferenceEqualityComparer.Instance);

        public override SyntaxNode? VisitVariableDeclaration(VariableDeclarationSyntax node)
        {
            // 字段/事件字段（DS012）与其它非局部形态不可能出现在 proven 映射中；
            // 保守起见仅在局部声明与 for 初始化器形态上操作。
            if (node.Parent is not (LocalDeclarationStatementSyntax or ForStatementSyntax))
            {
                return base.VisitVariableDeclaration(node);
            }

            // 多声明符共享一个类型语法（dynamic a = .., b = ..;）：只有该声明下所有
            // dynamic 声明符都被证明且为同一 T 才替换；任一未证明或异 T，整个声明
            // 不重写（异 T 已被 FlowTypeProver 的共享并集判 MultipleAssignmentTypes，
            // 此处是防御性二次确认）。
            ITypeSymbol? sharedType = null;
            var dynamicDeclarators = 0;
            foreach (var declarator in node.Variables)
            {
                if (_model.GetDeclaredSymbol(declarator) is not ILocalSymbol
                    {
                        Type.TypeKind: TypeKind.Dynamic,
                    })
                {
                    continue;
                }

                dynamicDeclarators++;
                if (!_proven.TryGetValue(declarator, out var provenType))
                {
                    return base.VisitVariableDeclaration(node);
                }

                if (sharedType != null
                    && !SymbolEqualityComparer.IncludeNullability.Equals(sharedType, provenType))
                {
                    return base.VisitVariableDeclaration(node);
                }

                sharedType = provenType;
            }

            if (dynamicDeclarators == 0 || sharedType == null)
            {
                return base.VisitVariableDeclaration(node);
            }

            var replacement = BuildConcreteTypeSyntax(_model, sharedType, node.Type.SpanStart)
                .WithTriviaFrom(node.Type)
                .WithAdditionalAnnotations(RewriteAnnotation);

            Count++;
            foreach (var declarator in node.Variables)
            {
                Concretized.Add(declarator);
            }

            return node.WithType(replacement);
        }

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            if (_proven.TryGetValue(node, out var provenType))
            {
                Count++;
                Concretized.Add(node);
                var replacement = BuildConcreteTypeSyntax(_model, provenType, node.Type.SpanStart)
                    .WithTriviaFrom(node.Type)
                    .WithAdditionalAnnotations(RewriteAnnotation);
                return node.WithType(replacement);
            }

            return base.VisitForEachStatement(node);
        }

        public override SyntaxNode? VisitCatchDeclaration(CatchDeclarationSyntax node)
        {
            if (_proven.TryGetValue(node, out var provenType))
            {
                Count++;
                Concretized.Add(node);
                var replacement = BuildConcreteTypeSyntax(_model, provenType, node.Type!.SpanStart)
                    .WithTriviaFrom(node.Type!)
                    .WithAdditionalAnnotations(RewriteAnnotation);
                return node.WithType(replacement);
            }

            return base.VisitCatchDeclaration(node);
        }

        // 注意：不 override VisitSingleVariableDesignation——解构分量保持原文
        // （本里程碑不支持；residual 闸门对残余站点 fail-closed）。
    }
}

/// <summary>
/// 三候选校验器（设计 D14）：候选重新 <see cref="CSharpSyntaxTree.ParseText"/> 并在
/// 同口径编译（<see cref="CSharpCompilation.ReplaceSyntaxTree"/>）上取诊断；候选不得
/// 引入原文件基线之外的 error。internal+virtual 仅供 InternalsVisibleTo 测试注入
/// （全失败保护/候选兜底测试，仿 ExtensionMethodResolvePreprocessor 的 Func 钩子形态）。
/// </summary>
internal class DynamicRewriteValidator
{
    public static DynamicRewriteValidator Default { get; } = new();

    public enum CandidateKind
    {
        ChangedSpanFormatted,
        WholeFileFormatted,
        Unformatted,
    }

    /// <summary>单个输出候选：种类 + 文本（BOM 已剥离，BOM 由调用方按输入保持）。</summary>
    public sealed record Candidate(CandidateKind Kind, string Text);

    /// <summary>候选是否可接受：parse 零 error 且同口径编译不引入基线外 error。</summary>
    public virtual bool Accepts(
        Candidate candidate,
        SyntaxTree originalTree,
        Compilation compilation,
        IReadOnlySet<string> baselineErrorKeys)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(originalTree);
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(baselineErrorKeys);

        var parseOptions = originalTree.Options as CSharpParseOptions
                           ?? new CSharpParseOptions(LanguageVersion.Latest);

        SyntaxTree candidateTree;
        try
        {
            candidateTree = CSharpSyntaxTree.ParseText(
                candidate.Text, parseOptions, originalTree.FilePath);
        }
        catch
        {
            return false;
        }

        if (candidateTree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return false;
        }

        if (compilation is not CSharpCompilation cSharpCompilation)
        {
            return true;
        }

        CSharpCompilation candidateCompilation;
        try
        {
            candidateCompilation = cSharpCompilation.ReplaceSyntaxTree(originalTree, candidateTree);
        }
        catch
        {
            return false;
        }

        foreach (var diagnostic in candidateCompilation.GetSemanticModel(candidateTree).GetDiagnostics())
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error)
            {
                continue;
            }

            // 原文件基线已有错误（如缺失符号的基线错误树）：不新增即接受。
            if (!baselineErrorKeys.Contains(ErrorKey(diagnostic)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>原文件（同口径编译）已有的 error 键集合；干净文件为空集 ⇒ 候选须零 error。</summary>
    public static IReadOnlySet<string> GetInFileErrorKeys(
        Compilation compilation,
        SyntaxTree tree)
    {
        if (compilation is not CSharpCompilation cSharpCompilation)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return cSharpCompilation.GetSemanticModel(tree).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(ErrorKey)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>错误去重键：码+消息（不绑行列——重写可能整体平移基线错误位置）。</summary>
    private static string ErrorKey(Diagnostic diagnostic)
        => $"{diagnostic.Id}|{diagnostic.GetMessage()}";
}
