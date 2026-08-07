using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript.Rewriters;

/// <summary>
/// 泛型擦除降级 rewriter（Task 24，M3，spec §2.6.2）。
/// 语法树层改写（沿用 Task 23 模式）：IR 方法体为 raw 文本且不携带语义信息，
/// typeof(T)/default(T)/new T()/is T 的类型参数判定与调用点同步必须在语法树层完成，
/// 产物经共享 visitor 流入 raw 文本。
/// ① typeof(T)（类型参数）→ TypeToken.of("T")；typeof(T).Name → TypeToken.nameOf("T")；
/// ② default(T)/裸 default（ConvertedType 为类型参数，非 class 约束）→ csDefault(TypeToken.of("T"))；
/// ③ new T()（方法体上下文）→ _factory_{T}() 调用 + 方法追加必选参数 System.Func&lt;T&gt; _factory_{T}，
///    调用点追加工厂实参（具体类型 → () =&gt; new X()；类型参数透传；不可解析 → csDefault() 桩）；
/// ④ x is T → TypeToken.isInstance("T", x)；x is T t 声明模式 → 诊断 + 保留（已知限制）。
/// 类字面量（typeof(Foo)）、(T)x 强转、lambda 箭头、工厂参数类型改写由同文件 IR 层
/// TsGenericErasureIrRewriter 完成（SingleFileTsEmitPass.RunTsRewriters 装配）。
/// 已知限制（登记于计划 Task 24）：x is T t 声明模式；构造器体/字段初始化器 new T()；
/// typeof(T) 运行时仅知类型参数名；csDefault 对 struct 约束返回 null；
/// (List&lt;T&gt;)x 泛型实例化强转；泛型数组 new T[n] 运行时类参数机制；跨文件工厂调用点。
/// </summary>
public sealed class TsGenericErasureRewriter : CSharpSyntaxRewriter
{
    private sealed record FactoryNeed(string TypeParamName, string FactoryParamName);

    private readonly ConversionContext _context;
    private readonly SemanticModel _semanticModel;
    private readonly HashSet<string> _diagnosed = new(StringComparer.Ordinal);

    /// <summary>需要工厂参数的方法：OriginalDefinition → 各类型参数对应的工厂参数名。</summary>
    private readonly Dictionary<IMethodSymbol, List<FactoryNeed>> _factoryNeeds = new(SymbolEqualityComparer.Default);

    /// <summary>当前正在访问的方法（new T() 上下文判定）。</summary>
    private MethodDeclarationSyntax? _currentMethod;
    private IMethodSymbol? _currentMethodSymbol;

    public TsGenericErasureRewriter(ConversionContext context)
    {
        _context = context;
        _semanticModel = context.SemanticModel!;
    }

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        CollectFactoryNeeds(node.Members);
        return base.VisitCompilationUnit(node);
    }

    // ─── 预扫描：登记需要工厂参数的方法（含透传闭包） ─────────────────

    private void CollectFactoryNeeds(SyntaxList<MemberDeclarationSyntax> members)
    {
        var methods = new List<MethodDeclarationSyntax>();
        CollectMethods(members, methods);

        // 第一轮：方法体直接含 new T()（T 为类型参数）
        foreach (var method in methods)
        {
            var symbol = _semanticModel.GetDeclaredSymbol(method);
            if (symbol == null)
            {
                continue;
            }
            var needs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var creation in method.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                // 实测：GetTypeInfo(creation.Type) 对 new T() 返回 null，须用整表达式。
                var created = _semanticModel.GetTypeInfo(creation).Type;
                if (created is ITypeParameterSymbol tp && !needs.ContainsKey(tp.Name))
                {
                    needs[tp.Name] = $"_{"factory"}_{tp.Name}";
                }
            }
            if (needs.Count > 0)
            {
                _factoryNeeds[symbol.OriginalDefinition] = needs
                    .Select(kv => new FactoryNeed(kv.Key, kv.Value))
                    .ToList();
            }
        }

        // 第二轮：透传闭包——调用工厂方法且以类型参数作类型实参的调用方也需工厂参数
        bool changed;
        do
        {
            changed = false;
            foreach (var method in methods)
            {
                var caller = _semanticModel.GetDeclaredSymbol(method);
                if (caller == null)
                {
                    continue;
                }
                foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (!TryResolveMethodSymbol(invocation, out var callee))
                    {
                        continue;
                    }
                    if (!_factoryNeeds.TryGetValue(callee.OriginalDefinition, out var calleeNeeds))
                    {
                        continue;
                    }
                    foreach (var need in calleeNeeds)
                    {
                        var typeArg = ResolveTypeArgSymbol(invocation, callee, need.TypeParamName);
                        if (typeArg is ITypeParameterSymbol tp && !HasFactory(caller, tp.Name))
                        {
                            AddFactory(caller, tp.Name);
                            changed = true;
                        }
                    }
                }
            }
        } while (changed);
    }

    private static void CollectMethods(SyntaxList<MemberDeclarationSyntax> members, List<MethodDeclarationSyntax> target)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax method:
                    target.Add(method);
                    break;
                case TypeDeclarationSyntax type:
                    CollectMethods(type.Members, target);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    CollectMethods(ns.Members, target);
                    break;
            }
        }
    }

    /// <summary>Resolve call-site method symbol; fall back to CandidateSymbols when the
    /// rewritten signature breaks binding (OverloadResolutionFailure).</summary>
    private bool TryResolveMethodSymbol(InvocationExpressionSyntax invocation, out IMethodSymbol method)
    {
        var info = _semanticModel.GetSymbolInfo(invocation);
        if (info.Symbol is IMethodSymbol resolved)
        {
            method = resolved;
            return true;
        }
        method = info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault()!;
        return method != null;
    }

    private ITypeSymbol? ResolveTypeArgSymbol(InvocationExpressionSyntax invocation, IMethodSymbol callee, string typeParamName)
    {
        var definition = callee.OriginalDefinition;
        for (var i = 0; i < definition.TypeParameters.Length; i++)
        {
            if (definition.TypeParameters[i].Name == typeParamName)
            {
                return callee.TypeArguments.Length > i ? callee.TypeArguments[i] : null;
            }
        }
        // 含类型类型参数（Box<T>.Make() 形态：T 属 Box<T>，callee.ContainingType 已构造）
        var containingDefinition = definition.ContainingType?.OriginalDefinition;
        if (containingDefinition != null && callee.ContainingType != null)
        {
            for (var i = 0; i < containingDefinition.TypeParameters.Length; i++)
            {
                if (containingDefinition.TypeParameters[i].Name == typeParamName)
                {
                    return callee.ContainingType.TypeArguments.Length > i
                        ? callee.ContainingType.TypeArguments[i]
                        : null;
                }
            }
        }
        return null;
    }

    private bool HasFactory(IMethodSymbol method, string typeParamName)
    {
        return _factoryNeeds.TryGetValue(method.OriginalDefinition, out var needs)
            && needs.Any(n => n.TypeParamName == typeParamName);
    }

    private void AddFactory(IMethodSymbol method, string typeParamName)
    {
        if (!_factoryNeeds.TryGetValue(method.OriginalDefinition, out var needs))
        {
            needs = new List<FactoryNeed>();
            _factoryNeeds[method.OriginalDefinition] = needs;
        }
        if (needs.Any(n => n.TypeParamName == typeParamName))
        {
            return;
        }
        needs.Add(new FactoryNeed(typeParamName, $"_{"factory"}_{typeParamName}"));
    }

    // ─── typeof(T) ──────────────────────────────────────────────────────

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        if (node.Expression is TypeOfExpressionSyntax typeofExpr
            && node.Name.Identifier.Text is "Name" or "FullName")
        {
            var type = _semanticModel.GetTypeInfo(typeofExpr.Type).Type;
            if (type is ITypeParameterSymbol tp)
            {
                UseCompat("TypeToken");
                Warn($"cs2j: typeof({tp.Name}).{node.Name.Identifier.Text} degraded to TypeToken.nameOf(\"{tp.Name}\") — runtime type info unavailable (spec §2.6.2)");
                return Invocation("TypeToken.nameOf", StringLiteral(tp.Name)).WithTriviaFrom(node);
            }
            if (type is IArrayTypeSymbol { ElementType: ITypeParameterSymbol arrayElement })
            {
                var display = $"{arrayElement.Name}[]";
                UseCompat("TypeToken");
                Warn($"cs2j: typeof({display}).{node.Name.Identifier.Text} degraded to TypeToken.nameOf(\"{display}\") — runtime type info unavailable (spec §2.6.2)");
                return Invocation("TypeToken.nameOf", StringLiteral(display)).WithTriviaFrom(node);
            }
        }
        return base.VisitMemberAccessExpression(node);
    }

    public override SyntaxNode? VisitTypeOfExpression(TypeOfExpressionSyntax node)
    {
        var type = _semanticModel.GetTypeInfo(node.Type).Type;
        if (type is ITypeParameterSymbol tp)
        {
            UseCompat("TypeToken");
            Warn($"cs2j: typeof({tp.Name}) degraded to TypeToken.of(\"{tp.Name}\") — runtime type info unavailable (spec §2.6.2)");
            return Invocation("TypeToken.of", StringLiteral(tp.Name)).WithTriviaFrom(node);
        }
        if (type is IArrayTypeSymbol { ElementType: ITypeParameterSymbol arrayElement })
        {
            var display = $"{arrayElement.Name}[]";
            UseCompat("TypeToken");
            Warn($"cs2j: typeof({display}) degraded to TypeToken.of(\"{display}\") — runtime type info unavailable (spec §2.6.2)");
            return Invocation("TypeToken.of", StringLiteral(display)).WithTriviaFrom(node);
        }
        return base.VisitTypeOfExpression(node);
    }

    // ─── default(T) / 裸 default ────────────────────────────────────────

    public override SyntaxNode? VisitDefaultExpression(DefaultExpressionSyntax node)
    {
        var type = _semanticModel.GetTypeInfo(node.Type).Type;
        if (type is ITypeParameterSymbol tp && !tp.HasReferenceTypeConstraint)
        {
            UseCompat("csDefault");
            UseCompat("TypeToken");
            Warn($"cs2j: default({tp.Name}) degraded to csDefault(TypeToken.of(\"{tp.Name}\")) — returns null as T (spec §2.6.2)");
            return Invocation("csDefault", Invocation("TypeToken.of", StringLiteral(tp.Name))).WithTriviaFrom(node);
        }
        return base.VisitDefaultExpression(node);
    }

    public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.DefaultLiteralExpression))
        {
            var type = _semanticModel.GetTypeInfo(node).ConvertedType;
            if (type is ITypeParameterSymbol tp && !tp.HasReferenceTypeConstraint)
            {
                UseCompat("csDefault");
                UseCompat("TypeToken");
                Warn($"cs2j: default degraded to csDefault(TypeToken.of(\"{tp.Name}\")) — returns null as T (spec §2.6.2)");
                return Invocation("csDefault", Invocation("TypeToken.of", StringLiteral(tp.Name))).WithTriviaFrom(node);
            }
        }
        return base.VisitLiteralExpression(node);
    }

    // ─── x is T ─────────────────────────────────────────────────────────

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.IsExpression))
        {
            var type = _semanticModel.GetTypeInfo(node.Right).Type;
            if (type is ITypeParameterSymbol tp)
            {
                UseCompat("TypeToken");
                Warn($"cs2j: x is {tp.Name} degraded to TypeToken.isInstance(\"{tp.Name}\", x) — cannot verify type parameter at runtime (spec §2.6.2)");
                var left = (ExpressionSyntax)base.Visit(node.Left)!;
                return Invocation("TypeToken.isInstance", StringLiteral(tp.Name), left).WithTriviaFrom(node);
            }
        }
        return base.VisitBinaryExpression(node);
    }

    public override SyntaxNode? VisitIsPatternExpression(IsPatternExpressionSyntax node)
    {
        if (node.Pattern is DeclarationPatternSyntax { Type: TypeSyntax declarationType })
        {
            var type = _semanticModel.GetTypeInfo(declarationType).Type;
            if (type is ITypeParameterSymbol tp)
            {
                Warn($"cs2j: 'x is {tp.Name} var' declaration pattern for type parameters is not supported (known limitation) — spec §2.6.2");
            }
        }
        return base.VisitIsPatternExpression(node);
    }

    // ─── new T() 工厂参数注入 ───────────────────────────────────────────

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);
        var previousMethod = _currentMethod;
        var previousSymbol = _currentMethodSymbol;
        _currentMethod = node;
        _currentMethodSymbol = symbol;
        try
        {
            var visited = (MethodDeclarationSyntax?)base.VisitMethodDeclaration(node);
            if (visited != null && symbol != null
                && _factoryNeeds.TryGetValue(symbol.OriginalDefinition, out var needs)
                && needs.Count > 0)
            {
                var parameters = visited.ParameterList.Parameters.ToList();
                foreach (var need in needs)
                {
                    var paramName = need.FactoryParamName;
                    while (parameters.Any(p => p.Identifier.Text == paramName))
                    {
                        paramName += "_";
                    }
                    parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName))
                        .WithType(SyntaxFactory.ParseTypeName($"System.Func<{need.TypeParamName}>")));
                }
                visited = visited.WithParameterList(
                    SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)));
            }
            return visited;
        }
        finally
        {
            _currentMethod = previousMethod;
            _currentMethodSymbol = previousSymbol;
        }
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node);
        // 实测：GetTypeInfo(node.Type) 对 new T() 返回 null，须用整表达式。
        var created = _semanticModel.GetTypeInfo(node).Type;
        if (created is ITypeParameterSymbol tp)
        {
            if (_currentMethod != null
                && _currentMethodSymbol != null
                && TryGetFactoryName(_currentMethodSymbol, tp.Name, out var factoryName))
            {
                Warn($"cs2j: new {tp.Name}() degraded to factory parameter injection ({factoryName}) — spec §2.6.2");
                return SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName(factoryName))
                    .WithTriviaFrom(visited);
            }
            Warn($"cs2j: new {tp.Name}() outside a method body cannot receive a factory parameter (known limitation) — spec §2.6.2");
        }
        return visited;
    }

    public override SyntaxNode? VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        var visited = (ImplicitObjectCreationExpressionSyntax)base.VisitImplicitObjectCreationExpression(node);
        var created = _semanticModel.GetTypeInfo(node).Type;
        if (created is ITypeParameterSymbol tp
            && _currentMethod != null
            && _currentMethodSymbol != null
            && TryGetFactoryName(_currentMethodSymbol, tp.Name, out var factoryName))
        {
            Warn($"cs2j: new {tp.Name}() degraded to factory parameter injection ({factoryName}) — spec §2.6.2");
            return SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName(factoryName))
                .WithTriviaFrom(visited);
        }
        return visited;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // 先访问子节点（嵌套调用/实参内的 new T() 等），再解析符号追加实参
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node);
        // 1c rewrite adds factory params: pre-rewrite call sites (0 args) fail overload
        // resolution against the rewritten signature (e.g. this.A<T>()), so fall back
        // to CandidateSymbols.
        if (!TryResolveMethodSymbol(node, out var callee))
        {
            return visited;
        }
        if (!_factoryNeeds.TryGetValue(callee.OriginalDefinition, out var needs))
        {
            return visited;
        }
        var appended = new List<ArgumentSyntax>();
        foreach (var need in needs)
        {
            appended.Add(SyntaxFactory.Argument(BuildFactoryArgument(visited, callee, need)));
        }
        return visited.WithArgumentList(visited.ArgumentList.AddArguments(appended.ToArray()));
    }

    private ExpressionSyntax BuildFactoryArgument(InvocationExpressionSyntax invocation, IMethodSymbol callee, FactoryNeed need)
    {
        var typeArg = ResolveTypeArgSymbol(invocation, callee, need.TypeParamName);
        if (typeArg is ITypeParameterSymbol tp)
        {
            // 类型实参本身是类型参数：调用方自带该工厂参数则透传，否则诊断 + csDefault() 桩
            if (_currentMethodSymbol != null
                && TryGetFactoryName(_currentMethodSymbol, tp.Name, out var callerFactory))
            {
                return SyntaxFactory.IdentifierName(callerFactory);
            }
            Warn($"cs2j: cannot synthesize factory for type parameter {tp.Name} at call site — degraded to csDefault() (spec §2.6.2)");
            UseCompat("csDefault");
            return SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName("csDefault"));
        }
        if (typeArg == null || typeArg.TypeKind == TypeKind.Error || typeArg.TypeKind == TypeKind.Dynamic)
        {
            Warn($"cs2j: cannot resolve type argument for factory parameter {need.TypeParamName} at call site — degraded to csDefault() (spec §2.6.2)");
            UseCompat("csDefault");
            return SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName("csDefault"));
        }
        // 具体类型实参 → () => new X()（优先复用实参语法以保留限定名）
        var typeSyntax = TryGetTypeArgumentSyntax(invocation, IndexOfTypeParam(callee, need.TypeParamName))
            ?? SyntaxFactory.IdentifierName(typeArg.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        return SyntaxFactory.ParenthesizedLambdaExpression(
            SyntaxFactory.ParameterList(),
            SyntaxFactory.ObjectCreationExpression(typeSyntax));
    }

    private static TypeSyntax? TryGetTypeArgumentSyntax(InvocationExpressionSyntax invocation, int index)
    {
        switch (invocation.Expression)
        {
            case GenericNameSyntax genericName:
                return genericName.TypeArgumentList.Arguments.Count > index
                    ? genericName.TypeArgumentList.Arguments[index]
                    : null;
            case MemberAccessExpressionSyntax memberAccess when memberAccess.Name is GenericNameSyntax memberGeneric:
                return memberGeneric.TypeArgumentList.Arguments.Count > index
                    ? memberGeneric.TypeArgumentList.Arguments[index]
                    : null;
            default:
                return null;
        }
    }

    private static int IndexOfTypeParam(IMethodSymbol callee, string typeParamName)
    {
        var definition = callee.OriginalDefinition;
        for (var i = 0; i < definition.TypeParameters.Length; i++)
        {
            if (definition.TypeParameters[i].Name == typeParamName)
            {
                return i;
            }
        }
        var containingDefinition = definition.ContainingType?.OriginalDefinition;
        if (containingDefinition != null)
        {
            for (var i = 0; i < containingDefinition.TypeParameters.Length; i++)
            {
                if (containingDefinition.TypeParameters[i].Name == typeParamName)
                {
                    return i;
                }
            }
        }
        return 0;
    }

    private bool TryGetFactoryName(IMethodSymbol method, string typeParamName, out string factoryName)
    {
        factoryName = string.Empty;
        return _factoryNeeds.TryGetValue(method.OriginalDefinition, out var needs)
            && needs.Any(n => n.TypeParamName == typeParamName)
            && (factoryName = needs.First(n => n.TypeParamName == typeParamName).FactoryParamName) != null;
    }

    // ─── 工具 ───────────────────────────────────────────────────────────

    private static InvocationExpressionSyntax Invocation(string name, params ExpressionSyntax[] args)
    {
        return SyntaxFactory.InvocationExpression(
            IdentifierPath(name),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(args.Select(SyntaxFactory.Argument))));
    }

    /// <summary>把 "TypeToken.of" 这类点分名建成结构化 MemberAccess 链（IdentifierName 含点
    /// 会被前端整体 camelCase 成 typeToken.of——实测坑）。</summary>
    private static ExpressionSyntax IdentifierPath(string name)
    {
        var parts = name.Split('.');
        ExpressionSyntax expression = SyntaxFactory.IdentifierName(parts[0]);
        for (var i = 1; i < parts.Length; i++)
        {
            expression = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                expression,
                SyntaxFactory.IdentifierName(parts[i]));
        }
        return expression;
    }

    private static LiteralExpressionSyntax StringLiteral(string value)
        => SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value));

    private void UseCompat(string symbol)
    {
        _context.TsCompatSymbols.Add(symbol);
    }

    private void Warn(string message)
    {
        if (_diagnosed.Add(message))
        {
            _context.Diagnostics.Warning(message);
        }
    }
}

/// <summary>
/// IR 层泛型擦除降级 rewriter（Task 24）：在语法层改写后的 Java IR 上做文本级收尾。
/// ① 类字面量：X.class.getName()/getTypeName() → mappedX.name；X.class → mappedX
///    （用户类/枚举 → 类引用；用户接口 → TypeToken.of("IX") + 诊断（Task 25 细化）；
///    BCL 表映射运行时构造器；未命中 → TypeToken.of("X") + 诊断）；
/// ② 类型参数强转 (T)(expr)/(T[])(expr) → (expr as T)；具体类/BCL 强转一并修复；
/// ③ instanceof T（类型参数）→ TypeToken.isInstance("T", expr)（结构化节点同步）；
/// ④ lambda 箭头 -> → =>（raw 文本，跳字符串/字符字面量）；
/// ⑤ 合成工厂参数：Supplier&lt;T&gt; → () =&gt; T；_factory_T.get() → _factory_T()。
/// 结构化 JavaMemberAccessExpression/JavaMethodCallExpression/JavaInstanceOfExpression
/// 同步改写；CompatSymbols 登记供发射器生成 @csharptypescript/compat 导入。
/// </summary>
public sealed class TsGenericErasureIrRewriter : JavaSyntaxRewriter
{
    private readonly ConversionContext _context;
    private readonly HashSet<string> _compatSymbols = new(StringComparer.Ordinal);
    private readonly HashSet<string> _diagnosed = new(StringComparer.Ordinal);

    private readonly HashSet<string> _userClassNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _userEnumNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _userInterfaceNames = new(StringComparer.Ordinal);

    /// <summary>当前方法/类作用域的类型参数名（栈式累积）。</summary>
    private readonly Stack<HashSet<string>> _typeParamStack = new();
    /// <summary>当前方法的合成工厂参数名。</summary>
    private readonly Stack<HashSet<string>> _factoryParamStack = new();

    /// <summary>BCL Java 名 → TS 运行时值（类字面量位置）。</summary>
    private static readonly Dictionary<string, string> BclClassLiteralValues = new(StringComparer.Ordinal)
    {
        ["String"] = "String",
        ["Integer"] = "Number",
        ["Long"] = "BigInt",
        ["Boolean"] = "Boolean",
        ["Double"] = "Number",
        ["Float"] = "Number",
        ["Short"] = "Number",
        ["Byte"] = "Number",
        ["Character"] = "String",
        ["Object"] = "Object",
        ["CSharpList"] = "Array",
        ["CSharpArrayList"] = "Array",
        ["ArrayList"] = "Array",
        ["List"] = "Array",
    };

    /// <summary>BCL Java 名 → TS 类型（强转位置）。</summary>
    private static readonly Dictionary<string, string> BclCastTypes = new(StringComparer.Ordinal)
    {
        ["int"] = "number",
        ["long"] = "bigint",
        ["short"] = "number",
        ["byte"] = "number",
        ["float"] = "number",
        ["double"] = "number",
        ["boolean"] = "boolean",
        ["char"] = "string",
        ["String"] = "string",
        ["Integer"] = "number",
        ["Long"] = "bigint",
        ["Boolean"] = "boolean",
        ["Double"] = "number",
        ["Float"] = "number",
        ["Short"] = "number",
        ["Byte"] = "number",
        ["Character"] = "string",
        ["Object"] = "any",
    };

    public TsGenericErasureIrRewriter(ConversionContext context)
    {
        _context = context;
    }

    public IReadOnlySet<string> CompatSymbols => _compatSymbols;

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        CollectUserTypeNames(node.TypeDeclarations);
        return base.VisitCompilationUnit(node);
    }

    private void CollectUserTypeNames(IEnumerable<JavaTypeDeclaration> types)
    {
        foreach (var type in types)
        {
            switch (type)
            {
                case JavaClassDeclaration cls:
                    _userClassNames.Add(cls.Name);
                    CollectUserTypeNames(cls.NestedTypes);
                    break;
                case JavaInterfaceDeclaration iface:
                    _userInterfaceNames.Add(iface.Name);
                    CollectUserTypeNames(iface.NestedTypes);
                    break;
                case JavaEnumDeclaration en:
                    _userEnumNames.Add(en.Name);
                    break;
            }
        }
    }

    public override JavaClassDeclaration VisitClassDeclaration(JavaClassDeclaration node)
    {
        var classTypeParams = node.TypeParameters.Select(tp => tp.Name).ToHashSet(StringComparer.Ordinal);
        _typeParamStack.Push(classTypeParams);
        try
        {
            var visited = base.VisitClassDeclaration(node);
            foreach (var block in node.StaticInitializers)
            {
                for (var i = 0; i < block.Statements.Count; i++)
                {
                    block.Statements[i] = RewriteBodyText(block.Statements[i]);
                }
            }
            return visited;
        }
        finally
        {
            _typeParamStack.Pop();
        }
    }

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        var methodTypeParams = node.TypeParameters.Select(tp => tp.Name).ToHashSet(StringComparer.Ordinal);
        var factoryParams = node.Parameters
            .Where(p => p.Name.StartsWith("_factory_", StringComparison.Ordinal))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        _typeParamStack.Push(methodTypeParams);
        _factoryParamStack.Push(factoryParams);
        try
        {
            var visited = base.VisitMethodDeclaration(node);
            foreach (var parameter in visited.Parameters)
            {
                if (parameter.Name.StartsWith("_factory_", StringComparison.Ordinal)
                    && parameter.Type.StartsWith("Supplier<", StringComparison.Ordinal)
                    && parameter.Type.EndsWith(">", StringComparison.Ordinal))
                {
                    // Supplier<T> → () => T（TS 函数类型）
                    parameter.Type = $"() => {parameter.Type["Supplier<".Length..^1]}";
                }
            }
            if (!string.IsNullOrEmpty(visited.Body))
            {
                visited.Body = RewriteBodyText(visited.Body);
            }
            return visited;
        }
        finally
        {
            _typeParamStack.Pop();
            _factoryParamStack.Pop();
        }
    }

    public override JavaConstructorDeclaration VisitConstructorDeclaration(JavaConstructorDeclaration node)
    {
        _factoryParamStack.Push(new HashSet<string>(StringComparer.Ordinal));
        try
        {
            var visited = base.VisitConstructorDeclaration(node);
            if (!string.IsNullOrEmpty(visited.Body))
            {
                visited.Body = RewriteBodyText(visited.Body);
            }
            return visited;
        }
        finally
        {
            _factoryParamStack.Pop();
        }
    }

    public override JavaFieldDeclaration VisitFieldDeclaration(JavaFieldDeclaration node)
    {
        if (!string.IsNullOrEmpty(node.Initializer))
        {
            node.Initializer = RewriteBodyText(node.Initializer);
        }
        return node;
    }

    public override JavaRawStatement VisitRawStatement(JavaRawStatement node)
    {
        node.Code = RewriteBodyText(node.Code);
        return node;
    }

    public override JavaRawExpression VisitRawExpression(JavaRawExpression node)
    {
        node.Code = RewriteBodyText(node.Code);
        return node;
    }

    public override JavaExpression VisitExpression(JavaExpression node)
    {
        // 类字面量/类字面量方法调用/类型参数 instanceof 需要整节点替换（返回类型不定），
        // 统一在此分发；其余节点走基类默认遍历。
        return node switch
        {
            JavaMemberAccessExpression access => VisitMemberAccess(access),
            JavaMethodCallExpression call => VisitMethodCall(call),
            JavaInstanceOfExpression instanceOf => VisitInstanceOf(instanceOf),
            _ => base.VisitExpression(node),
        };
    }

    private JavaExpression VisitMemberAccess(JavaMemberAccessExpression node)
    {
        node.Target = VisitExpression(node.Target);
        if (node.MemberName == "class" && node.Target is JavaIdentifierExpression id)
        {
            return BuildClassLiteral(id.Name);
        }
        return node;
    }

    private JavaExpression VisitMethodCall(JavaMethodCallExpression node)
    {
        // X.class.getName()/getTypeName() → X.name（先于目标访问，避免目标先被改写为类引用）
        if (node.MethodName is "getName" or "getTypeName"
            && node.Target is JavaMemberAccessExpression { MemberName: "class" } classAccess
            && classAccess.Target is JavaIdentifierExpression classId)
        {
            var mapped = MapClassLiteralReference(classId.Name);
            return new JavaMemberAccessExpression
            {
                Target = new JavaIdentifierExpression(mapped),
                MemberName = "name",
            };
        }
        if (node.Target != null)
        {
            node.Target = VisitExpression(node.Target);
        }
        for (var i = 0; i < node.Arguments.Count; i++)
        {
            node.Arguments[i] = VisitExpression(node.Arguments[i]);
        }
        // 工厂参数调用 _factory_T.get() → _factory_T()
        if (node.MethodName == "get"
            && node.Target is JavaIdentifierExpression factoryId
            && node.Arguments.Count == 0
            && CurrentFactoryParams.Contains(factoryId.Name))
        {
            return new JavaRawExpression($"{factoryId.Name}()");
        }
        return node;
    }

    private JavaExpression VisitInstanceOf(JavaInstanceOfExpression node)
    {
        node.Expression = VisitExpression(node.Expression);
        if (CurrentTypeParams.Contains(node.Type))
        {
            RegisterCompat("TypeToken");
            return new JavaMethodCallExpression
            {
                Target = new JavaMemberAccessExpression
                {
                    Target = new JavaIdentifierExpression("TypeToken"),
                    MemberName = "isInstance",
                },
                Arguments =
                {
                    new JavaLiteralExpression($"\"{node.Type}\""),
                    node.Expression,
                },
            };
        }
        return node;
    }

    // ─── 类字面量 ───────────────────────────────────────────────────────

    private JavaExpression BuildClassLiteral(string javaName)
    {
        var mapped = MapClassLiteralReference(javaName);
        var isUserType = _userClassNames.Contains(javaName) || _userEnumNames.Contains(javaName);
        if (!isUserType && mapped == javaName)
        {
            // 未命中/接口分支：MapClassLiteralReference 已诊断并返回 TypeToken.of("X") 文本
            return new JavaRawExpression(mapped);
        }
        return new JavaIdentifierExpression(mapped);
    }

    /// <summary>返回 Java 类名对应的 TS 运行时引用；接口/未命中发诊断并返回 TypeToken.of("X") 文本。</summary>
    private string MapClassLiteralReference(string javaName)
    {
        if (_userClassNames.Contains(javaName) || _userEnumNames.Contains(javaName))
        {
            return TsNaming.SafeClassName(javaName);
        }
        if (_userInterfaceNames.Contains(javaName))
        {
            Warn($"cs2j: typeof({javaName}) — interface has no runtime type; degraded to TypeToken.of(\"{javaName}\") (Task 25 refines) — spec §2.6.2");
            return $"TypeToken.of(\"{javaName}\")";
        }
        if (BclClassLiteralValues.TryGetValue(javaName, out var mapped))
        {
            return mapped;
        }
        Warn($"cs2j: typeof({javaName}) — unmapped class literal; degraded to TypeToken.of(\"{javaName}\") — spec §2.6.2");
        return $"TypeToken.of(\"{javaName}\")";
    }

    // ─── raw 文本改写引擎 ───────────────────────────────────────────────

    private string RewriteBodyText(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (ch == '"' || ch == '\'')
            {
                var start = i;
                i = SkipQuotedLiteral(text, i);
                sb.Append(text, start, i - start);
                continue;
            }
            if (TryMatchClassGetName(text, i, out var mappedName, out var consumed))
            {
                sb.Append(mappedName).Append(".name");
                i += consumed;
                continue;
            }
            if (TryMatchClassLiteral(text, i, out var mappedRef, out var consumed2))
            {
                sb.Append(mappedRef);
                i += consumed2;
                continue;
            }
            if (TryMatchInstanceOfTypeParam(text, i, out var instanceExpr, out var typeParam, out var consumed3))
            {
                RegisterCompat("TypeToken");
                sb.Append($"TypeToken.isInstance(\"{typeParam}\", {instanceExpr})");
                i += consumed3;
                continue;
            }
            if (TryMatchTypeParamCast(text, i, out var castInner, out var castType, out var consumed4))
            {
                sb.Append($"({castInner} as {castType})");
                i += consumed4;
                continue;
            }
            if (ch == '-' && i + 1 < text.Length && text[i + 1] == '>')
            {
                sb.Append("=>");
                i += 2;
                continue;
            }
            if (TryStripSupplierLambdaCast(text, i, out var consumed6))
            {
                // 合成工厂实参 (Supplier<X>) () -> ... 的目标类型强转是 Java 重载消歧产物，
                // TS 函数类型参数不需要；仅去掉强转前缀，保留其后 lambda。
                i += consumed6;
                continue;
            }
            if (TryMatchFactoryGet(text, i, out var factoryName, out var consumed5))
            {
                sb.Append(factoryName).Append("()");
                i += consumed5;
                continue;
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>匹配 {qualifiedName}.class.getName() / .getTypeName()，返回映射后的引用名与消费长度。</summary>
    private bool TryMatchClassGetName(string text, int i, out string mappedName, out int consumed)
    {
        mappedName = string.Empty;
        consumed = 0;
        if (!TryMatchQualifiedName(text, i, out var name, out var nameLength)
            || i + nameLength + ".class.".Length > text.Length)
        {
            return false;
        }
        var afterName = i + nameLength;
        if (!text.AsSpan(afterName).StartsWith(".class", StringComparison.Ordinal))
        {
            return false;
        }
        var methodStart = afterName + ".class".Length;
        if (methodStart >= text.Length || text[methodStart] != '.')
        {
            return false;
        }
        var methodName = ReadIdentifier(text, methodStart + 1);
        if (methodName is not ("getName" or "getTypeName"))
        {
            return false;
        }
        var open = methodStart + 1 + methodName.Length;
        if (open >= text.Length || text[open] != '(')
        {
            return false;
        }
        var close = FindMatchingCloseParen(text, open);
        if (close < 0 || close != open + 1)
        {
            return false;
        }
        mappedName = MapClassLiteralReference(name);
        consumed = close + 1 - i;
        return true;
    }

    /// <summary>匹配 {qualifiedName}.class（非 .getName 前缀），返回映射后的引用与消费长度。</summary>
    private bool TryMatchClassLiteral(string text, int i, out string mappedRef, out int consumed)
    {
        mappedRef = string.Empty;
        consumed = 0;
        if (!TryMatchQualifiedName(text, i, out var name, out var nameLength))
        {
            return false;
        }
        var afterName = i + nameLength;
        if (afterName + ".class".Length > text.Length
            || !text.AsSpan(afterName).StartsWith(".class", StringComparison.Ordinal))
        {
            return false;
        }
        // .class 后不得紧跟标识符（getName 等已在上方优先处理；此处防 X.classFoo 之类误匹配）
        var end = afterName + ".class".Length;
        if (end < text.Length && IsIdentifierChar(text[end]))
        {
            return false;
        }
        mappedRef = MapClassLiteralReference(name);
        consumed = end - i;
        return true;
    }

    /// <summary>匹配 {expr} instanceof {T}（T 为当前作用域类型参数，无声明模式变量），返回表达式文本。</summary>
    private bool TryMatchInstanceOfTypeParam(string text, int i, out string instanceExpr, out string typeParam, out int consumed)
    {
        instanceExpr = string.Empty;
        typeParam = string.Empty;
        consumed = 0;
        if (i + "instanceof ".Length > text.Length
            || !text.AsSpan(i).StartsWith("instanceof", StringComparison.Ordinal))
        {
            return false;
        }
        if (i > 0 && IsIdentifierChar(text[i - 1]))
        {
            return false;
        }
        var nameStart = i + "instanceof".Length;
        if (nameStart >= text.Length || text[nameStart] != ' ')
        {
            return false;
        }
        nameStart++;
        var typeName = ReadIdentifier(text, nameStart);
        if (typeName.Length == 0 || !CurrentTypeParams.Contains(typeName))
        {
            return false;
        }
        var afterName = nameStart + typeName.Length;
        // 声明模式变量（x is T t）不支持：不改写（语法层已诊断）
        if (afterName < text.Length && IsIdentifierChar(text[afterName]))
        {
            return false;
        }
        // 反向提取表达式：从 instanceof 前回扫
        var exprEnd = i;
        var exprStart = exprEnd - 1;
        while (exprStart >= 0 && char.IsWhiteSpace(text[exprStart]))
        {
            exprStart--;
        }
        exprStart = BacktrackExpressionStart(text, exprStart);
        if (exprStart < 0 || exprStart >= exprEnd)
        {
            return false;
        }
        instanceExpr = text[exprStart..exprEnd].Trim();
        typeParam = typeName;
        consumed = afterName - exprStart;
        return true;
    }

    /// <summary>从表达式末尾回扫起始位置（标识符/限定名/括号组），失败返回 -1。</summary>
    internal static int BacktrackExpressionStart(string text, int end)
    {
        var pos = end;
        while (pos >= 0)
        {
            if (IsIdentifierChar(text[pos]))
            {
                while (pos >= 0 && IsIdentifierChar(text[pos]))
                {
                    pos--;
                }
                if (pos < 0 || text[pos] != '.')
                {
                    return pos + 1;
                }
                pos--;
                continue;
            }
            if (text[pos] == ')')
            {
                var open = FindMatchingOpenParen(text, pos);
                if (open < 0)
                {
                    return -1;
                }
                // 括号组前可接限定名（如 (Foo)(x)）或标识符（如 f(x)）
                var before = open - 1;
                while (before >= 0 && char.IsWhiteSpace(text[before]))
                {
                    before--;
                }
                if (before >= 0 && IsIdentifierChar(text[before]))
                {
                    while (before >= 0 && IsIdentifierChar(text[before]))
                    {
                        before--;
                    }
                    if (before < 0 || text[before] != '.')
                    {
                        return before + 1;
                    }
                    pos = before - 1;
                    continue;
                }
                return open;
            }
            return -1;
        }
        return -1;
    }

    /// <summary>匹配 ({type})({expr}) 强转（类型为类型参数/单元内类型/BCL 表名，可带 []），返回表达式与 TS 类型。</summary>
    private bool TryMatchTypeParamCast(string text, int i, out string castInner, out string castType, out int consumed)
    {
        castInner = string.Empty;
        castType = string.Empty;
        consumed = 0;
        if (text[i] != '(')
        {
            return false;
        }
        var nameStart = i + 1;
        var name = ReadIdentifier(text, nameStart);
        if (name.Length == 0)
        {
            return false;
        }
        var afterName = nameStart + name.Length;
        var suffixLength = 0;
        while (afterName + 2 <= text.Length && text.AsSpan(afterName).StartsWith("[]", StringComparison.Ordinal))
        {
            afterName += 2;
            suffixLength += 2;
        }
        if (afterName >= text.Length || text[afterName] != ')')
        {
            return false;
        }
        var mapped = MapCastType(name);
        if (mapped == null)
        {
            return false;
        }
        var operandStart = afterName + 1;
        while (operandStart < text.Length && char.IsWhiteSpace(text[operandStart]))
        {
            operandStart++;
        }
        if (operandStart >= text.Length)
        {
            return false;
        }
        if (text[operandStart] == '(')
        {
            // ({type})({expr}) 形态：整表达式组括号
            var innerClose = FindMatchingCloseParen(text, operandStart);
            if (innerClose < 0)
            {
                return false;
            }
            castInner = text[(operandStart + 1)..innerClose].Trim();
            consumed = innerClose + 1 - i;
        }
        else
        {
            // ({type})expr 简单操作数形态（Task 27 模式匹配：((T)x).P 属性链与
            // switch 臂模式变量强转产物无第二层括号）；操作数为标识符/成员链，
            // 可带调用实参或下标（(T)o.m() / (T)arr[0]）。
            if (!TryReadSimpleCastOperand(text, operandStart, out var operand, out var operandLength))
            {
                return false;
            }
            castInner = operand;
            consumed = operandStart + operandLength - i;
        }
        castType = mapped + new string('[', suffixLength / 2) + new string(']', suffixLength / 2);
        return true;
    }

    /// <summary>
    /// 读取强转的简单操作数：标识符（可带 .成员 / (实参) / [下标] 链）；起始处非
    /// 标识符或链中途括号/下标不匹配时返回 false。
    /// </summary>
    private static bool TryReadSimpleCastOperand(string text, int start, out string operand, out int length)
    {
        operand = string.Empty;
        length = 0;
        var pos = start;
        var name = ReadIdentifier(text, pos);
        if (name.Length == 0)
        {
            return false;
        }
        pos += name.Length;
        while (pos < text.Length)
        {
            var c = text[pos];
            if (c == '.' && pos + 1 < text.Length && IsIdentifierChar(text[pos + 1]))
            {
                var member = ReadIdentifier(text, pos + 1);
                pos += 1 + member.Length;
                continue;
            }
            if (c == '(')
            {
                var close = FindMatchingCloseParen(text, pos);
                if (close < 0)
                {
                    return false;
                }
                pos = close + 1;
                continue;
            }
            if (c == '[')
            {
                var close = FindMatchingCloseBracket(text, pos);
                if (close < 0)
                {
                    return false;
                }
                pos = close + 1;
                continue;
            }
            break;
        }
        operand = text[start..pos];
        length = pos - start;
        return true;
    }

    /// <summary>自 open 起的 '[' 找匹配的 ']'（跳过字符串/字符字面量）；无匹配返回 -1。</summary>
    internal static int FindMatchingCloseBracket(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"' || ch == '\'')
            {
                i = SkipQuotedLiteral(text, i) - 1;
                continue;
            }
            if (ch == '[')
            {
                depth++;
            }
            else if (ch == ']')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }
        return -1;
    }

    private string? MapCastType(string javaName)
    {
        if (CurrentTypeParams.Contains(javaName))
        {
            return javaName;
        }
        if (_userClassNames.Contains(javaName) || _userEnumNames.Contains(javaName))
        {
            return TsNaming.SafeClassName(javaName);
        }
        return BclCastTypes.GetValueOrDefault(javaName);
    }

    /// <summary>匹配合成工厂参数调用 _factory_T.get()。</summary>
    /// <summary>剥离 (Supplier&lt;X&gt;) 目标类型强转前缀（后随 lambda 才剥离，避免误伤普通调用）。</summary>
    private bool TryStripSupplierLambdaCast(string text, int i, out int consumed)
    {
        consumed = 0;
        if (i + "(Supplier<".Length > text.Length
            || !text.AsSpan(i).StartsWith("(Supplier<", StringComparison.Ordinal))
        {
            return false;
        }
        var depth = 0;
        var pos = i + "(Supplier<".Length - 1;
        for (; pos < text.Length; pos++)
        {
            if (text[pos] == '<')
            {
                depth++;
            }
            else if (text[pos] == '>')
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }
            }
        }
        if (pos >= text.Length || pos + 1 >= text.Length || text[pos + 1] != ')')
        {
            return false;
        }
        pos += 2;
        while (pos < text.Length && char.IsWhiteSpace(text[pos]))
        {
            pos++;
        }
        // 后随 lambda：() -> ... 或 标识符 -> ...（Java 形态，-> 尚未改写为 =>；
        // 括号组与箭头之间可能有空白，需跳过）
        if (pos < text.Length && text[pos] == '(')
        {
            var close = FindMatchingCloseParen(text, pos);
            if (close < 0)
            {
                return false;
            }
            var afterClose = close + 1;
            while (afterClose < text.Length && char.IsWhiteSpace(text[afterClose]))
            {
                afterClose++;
            }
            if (afterClose + 2 > text.Length || !text.AsSpan(afterClose).StartsWith("->", StringComparison.Ordinal))
            {
                return false;
            }
            consumed = pos - i;
            return true;
        }
        var name = ReadIdentifier(text, pos);
        if (name.Length > 0)
        {
            var afterName = pos + name.Length;
            while (afterName < text.Length && char.IsWhiteSpace(text[afterName]))
            {
                afterName++;
            }
            if (afterName + 2 <= text.Length && text.AsSpan(afterName).StartsWith("->", StringComparison.Ordinal))
            {
                consumed = pos - i;
                return true;
            }
        }
        return false;
    }

    private bool TryMatchFactoryGet(string text, int i, out string factoryName, out int consumed)
    {
        factoryName = string.Empty;
        consumed = 0;
        var name = ReadIdentifier(text, i);
        if (name.Length == 0 || !CurrentFactoryParams.Contains(name))
        {
            return false;
        }
        var afterName = i + name.Length;
        if (afterName + ".get()".Length > text.Length
            || !text.AsSpan(afterName).StartsWith(".get()", StringComparison.Ordinal))
        {
            return false;
        }
        factoryName = name;
        consumed = afterName + ".get()".Length - i;
        return true;
    }

    /// <summary>从位置起读取标识符（含 $/_），返回读到的文本（可为空）。</summary>
    internal static string ReadIdentifier(string text, int start)
    {
        if (start < 0 || start >= text.Length || !IsIdentifierChar(text[start]))
        {
            return string.Empty;
        }
        var end = start;
        while (end < text.Length && IsIdentifierChar(text[end]))
        {
            end++;
        }
        return text[start..end];
    }

    /// <summary>匹配 {ident}(.{ident})* 限定名；要求前边界非标识符/点。</summary>
    private static bool TryMatchQualifiedName(string text, int i, out string name, out int length)
    {
        name = string.Empty;
        length = 0;
        if (i > 0 && (IsIdentifierChar(text[i - 1]) || text[i - 1] == '.'))
        {
            return false;
        }
        var pos = i;
        while (pos < text.Length)
        {
            var part = ReadIdentifier(text, pos);
            if (part.Length == 0)
            {
                return false;
            }
            pos += part.Length;
            if (pos < text.Length && text[pos] == '.')
            {
                // 限定名内部点：要求点后是标识符（X.class 的 .class 除外——点后是 class 关键字）
                var next = ReadIdentifier(text, pos + 1);
                if (next.Length == 0 || next == "class")
                {
                    break;
                }
                pos++;
                continue;
            }
            break;
        }
        if (pos == i)
        {
            return false;
        }
        name = text[i..pos];
        length = pos - i;
        return true;
    }

    internal static bool IsIdentifierChar(char c)
        => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    internal static int SkipQuotedLiteral(string text, int start)
    {
        var quote = text[start];
        var i = start + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }
            if (text[i] == quote)
            {
                return i + 1;
            }
            i++;
        }
        return text.Length;
    }

    internal static int FindMatchingCloseParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"' || ch == '\'')
            {
                i = SkipQuotedLiteral(text, i) - 1;
                continue;
            }
            switch (ch)
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
            }
            if (depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    private static int FindMatchingOpenParen(string text, int close)
    {
        var depth = 0;
        for (var i = close; i >= 0; i--)
        {
            var ch = text[i];
            if (ch == '"' || ch == '\'')
            {
                i -= 1; // 简化回扫：字面量内括号不影响配对（下轮继续回扫）
                continue;
            }
            switch (ch)
            {
                case ')':
                    depth++;
                    break;
                case '(':
                    depth--;
                    break;
            }
            if (depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    private HashSet<string> CurrentTypeParams
        => _typeParamStack.Count > 0 ? _typeParamStack.Peek() : s_emptySet;

    private HashSet<string> CurrentFactoryParams
        => _factoryParamStack.Count > 0 && _factoryParamStack.Peek() != null ? _factoryParamStack.Peek() : s_emptySet;

    private static readonly HashSet<string> s_emptySet = new(StringComparer.Ordinal);

    private void RegisterCompat(string symbol)
    {
        _compatSymbols.Add(symbol);
    }

    private void Warn(string message)
    {
        if (_diagnosed.Add(message))
        {
            _context.Diagnostics.Warning(message);
        }
    }
}
