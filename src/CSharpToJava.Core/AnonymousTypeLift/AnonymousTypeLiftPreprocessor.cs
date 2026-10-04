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

        var rewriter = new AnonymousSiteRewriter(
            semanticModel, _registry, _options.NamespaceName, statistics, diagnostics,
            _shapesByFile, semanticModel.SyntaxTree.FilePath);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (!rewriter.Changed)
        {
            statistics.FilesSkippedClean++;
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

        var residual = CountResidualAnonymousCreations(outputCode);
        statistics.ResidualCreations += residual;
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

        return new AnonymousTypeLiftFileResult { OutputCode = outputCode, Changed = true };
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

    internal static int CountResidualAnonymousCreations(string code)
    {
        try
        {
            var parsed = CSharpSyntaxTree.ParseText(code);
            return parsed.GetRoot()
                .DescendantNodesAndSelf()
                .Count(n => n is AnonymousObjectCreationExpressionSyntax);
        }
        catch (Exception)
        {
            return 0;
        }
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

    public bool Changed { get; private set; }

    public AnonymousSiteRewriter(
        SemanticModel model,
        AnonymousShapeRegistry registry,
        string ns,
        AnonymousTypeLiftStatistics stats,
        List<AnonymousTypeLiftDiagnostic> diagnostics,
        Dictionary<string, HashSet<AnonymousShape>> shapesByFile,
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

    public override SyntaxNode? VisitAnonymousObjectCreationExpression(AnonymousObjectCreationExpressionSyntax node)
    {
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
        Changed = true;
        return creation
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia())
            .WithAdditionalAnnotations(s_rewriteAnnotation);
    }

    /// <summary>登记本文件用到的形状（生成按编译单元切分的 class 文件时需要）。</summary>
    private AnonymousShape TrackShape(AnonymousShape shape)
    {
        if (!string.IsNullOrEmpty(_filePath))
        {
            if (!_shapesByFile.TryGetValue(_filePath, out var set))
            {
                set = new HashSet<AnonymousShape>();
                _shapesByFile[_filePath] = set;
            }

            set.Add(shape);
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
