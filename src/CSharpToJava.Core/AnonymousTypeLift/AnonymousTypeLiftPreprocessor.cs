using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.AnonymousTypeLift;

/// <summary>
/// C# 匿名类型提升预处理器（C21）。把源码中所有匿名对象创建点
/// （<c>new { X = 1, … }</c>、投影缩写 <c>new { e.X }</c>）替换为对生成的
/// internal sealed 不可变类的限定构造（<c>new global::Cs2jAnonymous.Anon_X(…)</c>）；
/// 形状在全部方法/文件间按「属性序列 + 类型结构键 + 约束签名」去重。
/// 其余语法（var/lambda/隐式数组/成员访问/初始化器）一律不动，靠重编译重新绑定
/// （设计 D10/D11）。输出前做语法校验，任何情况下不产出语法损坏的中间 C#。
/// </summary>
public sealed partial class AnonymousTypeLiftPreprocessor
{
    private static readonly SyntaxAnnotation s_rewriteAnnotation = new("Cs2jAnonymousTypeLiftRewrite");

    private readonly AnonymousShapeRegistry _registry = new();
    private readonly AnonymousTypeLiftOptions _options;
    private readonly Dictionary<string, HashSet<AnonymousShape>> _shapesByFile =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本 preprocessor 生命周期内已发过 AL030 的形状键（每形状最多一条）。</summary>
    private readonly HashSet<string> _dynamicShapesAnnounced = new(StringComparer.Ordinal);

    public AnonymousTypeLiftPreprocessor(AnonymousTypeLiftOptions? options = null)
    {
        _options = options ?? new AnonymousTypeLiftOptions();
    }

    /// <summary>生成 class 集合文件内容；没有任何匿名形状时返回 null。内容按 Key 排序。</summary>
    public string? GenerateTypeFile()
        => _registry.Shapes.Count == 0
            ? null
            : AnonymousTypeFileContent.Generate(_registry.Shapes.ToList(), _options.NamespaceName);

    /// <summary>按形状键筛选生成 class 文件内容（按注册序过滤，生成时按 Key 排序）；无匹配返回 null。</summary>
    public string? GenerateTypeFile(IEnumerable<string> shapeKeys)
    {
        var keys = new HashSet<string>(shapeKeys, StringComparer.Ordinal);
        var shapes = _registry.Shapes.Where(shape => keys.Contains(shape.Key)).ToList();
        return shapes.Count == 0
            ? null
            : AnonymousTypeFileContent.Generate(shapes, _options.NamespaceName);
    }

    public IReadOnlyCollection<string> TypeNames => _registry.Shapes.Select(s => s.Name).ToArray();

    /// <summary>
    /// 每个被改写文件用到的形状键（不含嵌套闭包；需要时经 <see cref="CloseShapeKeys"/>
    /// 展开）。按编译单元生成 class 文件时需要它：一个源文件可能被多个程序集分别
    /// 编译（共享源码，D3），各编译单元各生成一份 internal 副本。
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ShapesByFile =>
        _shapesByFile.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.Select(shape => shape.Key).ToArray(),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>取指定形状集合的嵌套闭包，并保持注册顺序稳定。</summary>
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

            foreach (var property in shape.Properties.Where(p => p.IsAnonymous))
            {
                pending.Push(property.NestedShape!.Key);
            }
        }

        return _registry.Shapes
            .Where(shape => closure.Contains(shape.Key))
            .Select(shape => shape.Key)
            .ToArray();
    }

    public AnonymousTypeLiftFileResult RewriteFile(
        string sourceCode,
        SemanticModel semanticModel,
        AnonymousTypeLiftStatistics statistics,
        List<AnonymousTypeLiftDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(diagnostics);

        statistics.FilesScanned++;

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();

        if (!IsAnonymousDirty(root))
        {
            statistics.FilesSkippedClean++;
            return new AnonymousTypeLiftFileResult { OutputCode = sourceCode, Changed = false };
        }

        // §2.4 扫描断言（改写前的原树）：匿名类型在 C# 源码中不能被具名书写，只有
        // 真正占据语法类型槽位（声明/cast/new/default/typeof/模式/is-as/基类列表等）
        // 的最外层 TypeSyntax 绑定了匿名符号才算命中 → AL020 聚合一条（每文件最多
        // 一条，消息含命中数）。表达式上下文的裸标识符（成员接收者、实参、查询范围
        // 变量等）不是类型位置，必须排除。原树不可能有 Cs2jAnonymous 显式名，无需
        // 排除 ObjectCreationExpression。
        var explicitTypeHits = CountExplicitAnonymousTypePositions(root, semanticModel);
        if (explicitTypeHits > 0)
        {
            diagnostics.Add(new AnonymousTypeLiftDiagnostic(
                "AL020",
                AnonymousTypeLiftSeverity.Warning,
                $"{explicitTypeHits} explicit type position(s) in this file are bound to " +
                "anonymous type symbols (anonymous types cannot be named in C# source; a " +
                "preceding pass may have materialized one). Review the pass chain ordering."));
        }

        var rewriter = new AnonymousSiteRewriter(
            semanticModel, _registry, _options.NamespaceName, statistics, diagnostics,
            _shapesByFile, semanticModel.SyntaxTree.FilePath, _dynamicShapesAnnounced);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        // D14 语义级残余闸门：只统计「语义模型确认为匿名类型但未被改写」的创建点
        // （目前只有表达式树跳过这一种；IErrorTypeSymbol 的 AL010 节点未绑定匿名类型，
        // 不计）。查询 transparent identifier 没有创建语法节点，天然不计（设计 §7.4）。
        var residual = rewriter.SemanticResidualCount;
        statistics.ResidualCreations += residual;

        if (!rewriter.Changed)
        {
            statistics.FilesSkippedClean++;
            if (residual > 0)
            {
                diagnostics.Add(new AnonymousTypeLiftDiagnostic(
                    "AL060",
                    AnonymousTypeLiftSeverity.Warning,
                    $"{residual} anonymous object creation node(s) could not be rewritten and were " +
                    "left unchanged (see the warnings above for the reasons)."));
            }

            return new AnonymousTypeLiftFileResult { OutputCode = sourceCode, Changed = false };
        }

        var outputCode = FormatAndValidate(newRoot);
        if (outputCode == null)
        {
            statistics.FilesSkippedClean++;
            diagnostics.Add(new AnonymousTypeLiftDiagnostic(
                "AL002",
                AnonymousTypeLiftSeverity.Warning,
                "anonymous type rewrite produced invalid C# syntax; the original file was preserved."));
            return new AnonymousTypeLiftFileResult { OutputCode = sourceCode, Changed = false };
        }

        statistics.FilesTransformed++;

        if (residual > 0)
        {
            diagnostics.Add(new AnonymousTypeLiftDiagnostic(
                "AL060",
                AnonymousTypeLiftSeverity.Warning,
                $"{residual} anonymous object creation node(s) could not be rewritten and were " +
                "left unchanged (see the warnings above for the reasons)."));
        }

        // 汇总统计从共享 registry 回填（跨文件累计值随每次改写刷新为当前全集）。
        statistics.ShapesRegistered = _registry.Shapes.Count;
        statistics.GenericShapes = _registry.Shapes.Count(s => s.GenericArity > 0);
        statistics.DynamicShapes = _registry.Shapes.Count(s => s.HasDynamic);
        statistics.NestedShapes = _registry.Shapes.Count(s => s.Properties.Any(p => p.IsAnonymous));
        statistics.TypesGenerated = _registry.Shapes.Count;

        // AL001：每个被改写文件一条 Info（非每站点），含本文件创建点数与形状名集合。
        var shapeNames = rewriter.FileShapeNames
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        diagnostics.Add(new AnonymousTypeLiftDiagnostic(
            "AL001",
            AnonymousTypeLiftSeverity.Info,
            $"{rewriter.RewrittenSiteCount} anonymous object creation site(s) in this file " +
            $"were lifted to {shapeNames.Length} named type(s): {string.Join(", ", shapeNames)}."));

        return new AnonymousTypeLiftFileResult { OutputCode = outputCode, Changed = true };
    }

    /// <summary>
    /// §2.4 扫描断言（前序 pass 物化防线，设计 §2.4/§6 AL020）：只统计真正处于
    /// 「语法类型位置根」（见 <see cref="IsTypePositionRoot"/>）且语义绑定（Type 或
    /// ConvertedType）到匿名类型的位置数，以类型位置根为单位计数（一条复合类型只在
    /// 最外层判定一次）。合法手写 C# 中匿名类型无法显式具名，正常源码该数恒为 0；
    /// 真阳性只可能来自前序 pass 的异常物化（如 global:: 限定名落到声明槽），因此
    /// 白名单刻意从简、宁可保守少报。
    ///
    /// 不能用 <c>root.DescendantNodes().OfType&lt;TypeSyntax&gt;()</c> 全收：Roslyn 中
    /// <see cref="TypeSyntax"/> 继承自 ExpressionSyntax，表达式上下文的裸
    /// IdentifierName/限定名（成员访问 <c>r.K</c> 中的 r、实参 a/b、查询范围变量、
    /// <c>Console.WriteLine(o)</c> 的 o）也会被收进来，其 GetTypeInfo().Type 同样可能
    /// 是匿名类型——那是表达式位置而非类型位置，必须靠语法槽位白名单剔除。
    /// </summary>
    internal static int CountExplicitAnonymousTypePositions(SyntaxNode root, SemanticModel model)
    {
        var hits = 0;
        foreach (var typeSyntax in root.DescendantNodes().OfType<TypeSyntax>())
        {
            if (!IsTypePositionRoot(typeSyntax))
            {
                continue;
            }

            // var 是合法的推断位置而非可具名类型位置（foreach/using/out var 同理）：
            // 其 GetTypeInfo 会返回推断出的匿名类型，必须排除。
            if (typeSyntax is IdentifierNameSyntax { IsVar: true })
            {
                continue;
            }

            var info = model.GetTypeInfo(typeSyntax);
            if (info.Type is INamedTypeSymbol { IsAnonymousType: true }
                || info.ConvertedType is INamedTypeSymbol { IsAnonymousType: true })
            {
                hits++;
            }
        }

        return hits;
    }

    /// <summary>
    /// 判断一个 TypeSyntax 是否为白名单内的「类型位置根」：它本身是一条类型表达式
    /// 的最外层节点（非复合类型的内部部件，见 <see cref="IsTypeInternalPart"/>），且
    /// 父节点把它放在类型语法槽中。复合类型（限定名、数组、指针、Nullable、元组、
    /// 泛型实参、scoped/ref 包装等）以最外层根统一判定，内部节点不单独计数。
    /// </summary>
    private static bool IsTypePositionRoot(TypeSyntax node)
    {
        if (node.Parent is not { } parent || IsTypeInternalPart(node, parent))
        {
            return false;
        }

        switch (parent)
        {
            // ---- 声明位置的显式类型 ----
            case VariableDeclarationSyntax variable when variable.Type == node:
            case PropertyDeclarationSyntax property when property.Type == node:
            case IndexerDeclarationSyntax indexer when indexer.Type == node:
            case EventDeclarationSyntax @event when @event.Type == node:
            case MethodDeclarationSyntax method when method.ReturnType == node:
            case LocalFunctionStatementSyntax localFunction when localFunction.ReturnType == node:
            case DelegateDeclarationSyntax @delegate when @delegate.ReturnType == node:
            case OperatorDeclarationSyntax @operator when @operator.ReturnType == node:
            case ConversionOperatorDeclarationSyntax conversion when conversion.Type == node:
            case ParameterSyntax parameter when parameter.Type == node:
            case CatchDeclarationSyntax @catch when @catch.Type == node:
            // ---- 表达式/语句中的类型位置 ----
            case CastExpressionSyntax cast when cast.Type == node:
            // 隐式 new()/new[] 没有 Type，天然不会到这里。
            case ObjectCreationExpressionSyntax creation when creation.Type == node:
            case DefaultExpressionSyntax @default when @default.Type == node:
            case TypeOfExpressionSyntax typeOf when typeOf.Type == node:
            case DeclarationExpressionSyntax declaration when declaration.Type == node:
            case StackAllocArrayCreationExpressionSyntax stackAlloc when stackAlloc.Type == node:
            // ---- 模式中的类型（var 模式没有 TypeSyntax 子节点，无需处理）----
            case DeclarationPatternSyntax declarationPattern when declarationPattern.Type == node:
            case RecursivePatternSyntax recursivePattern when recursivePattern.Type == node:
            // ---- 旧式 x is T / x as T（模式式 is 走上面的 pattern 槽）----
            case BaseTypeSyntax baseType when baseType.Type == node:
            case TypeConstraintSyntax typeConstraint when typeConstraint.Type == node:
                return true;
            case BinaryExpressionSyntax binary
                when binary.IsKind(SyntaxKind.AsExpression)
                    || binary.IsKind(SyntaxKind.IsExpression):
                return binary.Right == node;
            default:
                // 其余父槽（成员访问、实参、查询子句、插值、条件表达式、特性名等）
                // 都是表达式上下文，不是类型位置，一律不收。
                return false;
        }
    }

    /// <summary>
    /// 复合类型表达式的内部部件判定：节点嵌在另一个类型语法内部时不是独立根，整条
    /// 类型只在其最外层所属槽位判定一次（避免同一类型内部节点重复计数）。
    /// </summary>
    private static bool IsTypeInternalPart(TypeSyntax node, SyntaxNode parent)
    {
        switch (parent)
        {
            // 限定名 / global:: 别名限定名：左部、右部、别名都是内部部件。
            case QualifiedNameSyntax qualified when qualified.Left == node || qualified.Right == node:
            case AliasQualifiedNameSyntax aliasQualified
                when aliasQualified.Alias == node || aliasQualified.Name == node:
            // 数组 / 指针 / T? / scoped T / ref T 的元素（包装）类型。
            case ArrayTypeSyntax array when array.ElementType == node:
            case PointerTypeSyntax pointer when pointer.ElementType == node:
            case NullableTypeSyntax nullable when nullable.ElementType == node:
            case ScopedTypeSyntax scoped when scoped.Type == node:
            case RefTypeSyntax refType when refType.Type == node:
            // 元组元素类型（最外层 TupleType 占槽，元素不单独扫）。
            case TupleElementSyntax tupleElement when tupleElement.Type == node:
            // 函数指针参数类型（delegate*<...>）。
            case FunctionPointerParameterSyntax functionPointerParameter
                when functionPointerParameter.Type == node:
            // 泛型实参：整条泛型名（或其外层限定名/别名限定名）才是类型位置根，
            // 实参不单独计数；泛型方法调用 M<T>() 的实参同样在这里被排除（保守）。
            case TypeArgumentListSyntax typeArguments when typeArguments.Arguments.Contains(node):
                return true;
            default:
                return false;
        }
    }

    /// <summary>文件级快速脏检查（D15 幂等）：不含任何匿名对象创建语法时直接原样拷贝。</summary>
    internal static bool IsAnonymousDirty(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodesAndSelf())
        {
            if (node is AnonymousObjectCreationExpressionSyntax)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 优先只格式化被改写的片段；候选依次为：局部格式化、整文件格式化、原始文本。
    /// 每个候选重新解析校验，返回 null 表示全部失败。（复刻 E06 FormatAndValidate。）
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
}

/// <summary>单文件语法重写器（在原始语义模型上工作）。唯一改写节点：匿名对象创建。</summary>
internal sealed class AnonymousSiteRewriter : CSharpSyntaxRewriter
{
    private static readonly SyntaxAnnotation s_rewriteAnnotation = new("Cs2jAnonymousTypeLiftRewrite");

    private readonly SemanticModel _model;
    private readonly AnonymousShapeRegistry _registry;
    private readonly string _namespace;
    private readonly AnonymousTypeLiftStatistics _stats;
    private readonly List<AnonymousTypeLiftDiagnostic> _diagnostics;
    private readonly Dictionary<string, HashSet<AnonymousShape>> _shapesByFile;
    private readonly string _filePath;
    private readonly HashSet<string> _dynamicShapesAnnounced;

    /// <summary>每个 lambda 节点的表达式树判定缓存（同一 lambda 只问一次语义模型）。</summary>
    private readonly Dictionary<SyntaxNode, bool> _expressionTreeLambdaCache = new();

    /// <summary>本文件实际用到的形状（AL001 消息用，与 filePath 是否为空无关）。</summary>
    private readonly HashSet<AnonymousShape> _fileShapes = new();

    /// <summary>语义确认为匿名类型但未改写的创建点数（D14 语义残余闸门）。</summary>
    public int SemanticResidualCount { get; private set; }

    /// <summary>本文件被改写的创建点数（AL001 消息用；区别于跨文件累计的 stats）。</summary>
    public int RewrittenSiteCount { get; private set; }

    /// <summary>本文件涉及的形状名集合（已按形状去重）。</summary>
    public IReadOnlyCollection<string> FileShapeNames =>
        _fileShapes.Select(shape => shape.Name).ToArray();

    public bool Changed { get; private set; }

    public AnonymousSiteRewriter(
        SemanticModel model,
        AnonymousShapeRegistry registry,
        string ns,
        AnonymousTypeLiftStatistics stats,
        List<AnonymousTypeLiftDiagnostic> diagnostics,
        Dictionary<string, HashSet<AnonymousShape>> shapesByFile,
        string filePath,
        HashSet<string> dynamicShapesAnnounced)
    {
        _model = model;
        _registry = registry;
        _namespace = ns;
        _stats = stats;
        _diagnostics = diagnostics;
        _shapesByFile = shapesByFile;
        _filePath = filePath;
        _dynamicShapesAnnounced = dynamicShapesAnnounced;
    }

    public override SyntaxNode? VisitAnonymousObjectCreationExpression(AnonymousObjectCreationExpressionSyntax node)
    {
        // 0. 表达式树跳过（AL020）：找最近的 lambda 祖先（含匿名方法，罕见），命中
        //    ExpressionTreeFacts 共享判定时整子树原样保留（不 Visit 内部），计语义残余。
        var lambda = node.Ancestors().OfType<LambdaExpressionSyntax>().FirstOrDefault();
        if (lambda != null && IsExpressionTreeLambdaCached(lambda))
        {
            _diagnostics.Add(new AnonymousTypeLiftDiagnostic(
                "AL020",
                AnonymousTypeLiftSeverity.Warning,
                "An anonymous object creation inside an expression-tree lambda was skipped " +
                "and left unchanged (expression trees cannot reference the lifted class)."));
            SemanticResidualCount++;
            return node;
        }

        // 1. 语义取符号：Type 优先，回退 ConvertedType；都不是匿名类型 → AL010 原样。
        var info = _model.GetTypeInfo(node);
        var symbol = info.Type as INamedTypeSymbol;
        if (symbol is not { IsAnonymousType: true })
        {
            symbol = info.ConvertedType as INamedTypeSymbol;
        }

        if (symbol is not { IsAnonymousType: true })
        {
            _diagnostics.Add(new AnonymousTypeLiftDiagnostic(
                "AL010",
                AnonymousTypeLiftSeverity.Warning,
                "An anonymous object creation could not be bound to an anonymous type symbol " +
                "(incomplete semantic information, e.g. missing reference); it was left unchanged."));
            return base.VisitAnonymousObjectCreationExpression(node);
        }

        // 2. 先 Visit 各 initializer 的 Expression：保证嵌套创建先注册、先改写；
        //    initializer 顺序与属性声明顺序 1:1（投影缩写 new { e.X } 的实参即 e.X）。
        var arguments = node.Initializers
            .Select(initializer => (ExpressionSyntax)Visit(initializer.Expression)!)
            .ToList();

        // 3. 登记形状 + TrackShape（记入本文件形状集合）。
        var shape = TrackShape(_registry.GetOrCreate(symbol, _diagnostics));

        // 4. 构造限定 ObjectCreationExpression，泛型实参用源码作用域内的形参名。
        var typeSyntax = SyntaxFactory.ParseTypeName(
            $"global::{_namespace}.{ShapeDisplayInScope(shape, symbol)}");
        var creation = SyntaxFactory.ObjectCreationExpression(
            typeSyntax,
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(arguments.Select(SyntaxFactory.Argument))),
            null);

        // 5. 保留 trivia、挂改写注解。
        _stats.CreationsRewritten++;
        RewrittenSiteCount++;
        Changed = true;
        return creation
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia())
            .WithAdditionalAnnotations(s_rewriteAnnotation);
    }

    /// <summary>
    /// AL040 反射/身份契约盘点（D8，保守启发式、只 Warn 不改写）：
    /// ① 接收者静态类型为匿名类型的 <c>object.GetType()</c> 无参虚调用；
    /// ② 名为 Serialize/SerializeObject 的方法且任一实参静态类型是匿名类型。
    /// 每节点最多一条；无论是否命中都继续正常 Visit，不影响改写。
    /// </summary>
    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        AnalyzeReflectionAndSerialization(node);
        return base.VisitInvocationExpression(node);
    }

    private void AnalyzeReflectionAndSerialization(InvocationExpressionSyntax node)
    {
        if (_model.GetSymbolInfo(node).Symbol is not IMethodSymbol method)
        {
            return;
        }

        var definition = method.OriginalDefinition;
        if (definition.Name == "GetType"
            && definition.Parameters.Length == 0
            && definition.ContainingType?.SpecialType == SpecialType.System_Object
            && node.Expression is MemberAccessExpressionSyntax memberAccess
            && _model.GetTypeInfo(memberAccess.Expression).Type
                is INamedTypeSymbol { IsAnonymousType: true })
        {
            _diagnostics.Add(new AnonymousTypeLiftDiagnostic(
                "AL040",
                AnonymousTypeLiftSeverity.Warning,
                $"A GetType() identity-reflection call on an anonymous-typed receiver " +
                $"('{memberAccess.Expression}') was found; the lifted class name differs " +
                "from the compiler-generated anonymous type name, so identity/reflection " +
                "contracts are not equivalent."));
            return;
        }

        if ((string.Equals(definition.Name, "Serialize", StringComparison.Ordinal)
                || string.Equals(definition.Name, "SerializeObject", StringComparison.Ordinal))
            && node.ArgumentList.Arguments.Any(arg =>
                _model.GetTypeInfo(arg.Expression).Type is INamedTypeSymbol { IsAnonymousType: true }))
        {
            _diagnostics.Add(new AnonymousTypeLiftDiagnostic(
                "AL040",
                AnonymousTypeLiftSeverity.Warning,
                $"A '{definition.Name}' call receives an anonymous-typed argument; serialization " +
                "based on the runtime type identity was inventoried (property names/order are preserved)."));
        }
    }

    /// <summary>表达式树判定（每 lambda 节点缓存一次结果）。</summary>
    private bool IsExpressionTreeLambdaCached(LambdaExpressionSyntax lambda)
    {
        if (!_expressionTreeLambdaCache.TryGetValue(lambda, out var isExpressionTree))
        {
            isExpressionTree = ExpressionTreeFacts.IsExpressionTreeLambda(_model, lambda);
            _expressionTreeLambdaCache[lambda] = isExpressionTree;
        }

        return isExpressionTree;
    }

    /// <summary>
    /// 登记本文件用到的形状（生成按编译单元切分的 class 文件时需要）；同时维护
    /// AL001 用的本文件形状集合，并对含 dynamic 的形状在整个 preprocessor 生命周期
    /// 内首发一次 AL030 Info（D9，按形状键去重）。
    /// </summary>
    private AnonymousShape TrackShape(AnonymousShape shape)
    {
        _fileShapes.Add(shape);

        if (!string.IsNullOrEmpty(_filePath))
        {
            if (!_shapesByFile.TryGetValue(_filePath, out var set))
            {
                set = new HashSet<AnonymousShape>();
                _shapesByFile[_filePath] = set;
            }

            set.Add(shape);
        }

        if (shape.HasDynamic && _dynamicShapesAnnounced.Add(shape.Key))
        {
            _diagnostics.Add(new AnonymousTypeLiftDiagnostic(
                "AL030",
                AnonymousTypeLiftSeverity.Info,
                $"Shape '{shape.Name}' contains a dynamic property: it is lifted faithfully " +
                "on the C# side, but the Java side has no dynamic support (uncovered by C25)."));
        }

        return shape;
    }

    /// <summary>
    /// 在当前作用域内渲染 class 实例化：GenericArity=0 用 Name；否则对匿名符号各
    /// 属性类型做 CollectParameterOrder 去重取前 GenericArity 个类型形参，用各自
    /// 源码名（T/U 等，不是 __P）渲染成形参实参文本（仿 E06 ShapeDisplayInScope）。
    /// </summary>
    private static string ShapeDisplayInScope(AnonymousShape shape, INamedTypeSymbol anonymousSymbol)
    {
        if (shape.GenericArity == 0)
        {
            return shape.Name;
        }

        var parameters = new List<ITypeParameterSymbol>();
        foreach (var property in anonymousSymbol.GetMembers()
                     .OfType<IPropertySymbol>()
                     .Where(p => p.Parameters.Length == 0))
        {
            AnonymousShapeRegistry.CollectParameterOrder(property.Type, parameters);
        }

        var args = parameters
            .Take(shape.GenericArity)
            .Select(p => AnonymousShapeRegistry.SanitizeIdentifier(p.Name));
        return shape.Name + "<" + string.Join(", ", args) + ">";
    }
}
