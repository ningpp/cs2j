using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;

namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// Pure analysis pass: locates host types, determines static, collects captures,
/// runs the fixpoint propagation, and evaluates all skip gates.
/// Produces a list of LiftPlan objects for the rewriter.
/// </summary>
internal sealed class LocalFunctionLiftAnalyzer
{
    private readonly SemanticModel _model;
    private readonly LocalFunctionLiftOptions _options;
    private readonly LocalFunctionLiftStatistics _stats;
    private readonly List<LocalFunctionLiftDiagnostic> _diagnostics;
    private readonly Dictionary<string, HashSet<string>> _crossFileNameTable;
    /// <summary>条件编译区（本次分析编译未启用对应符号）里以函数式引用出现的标识符。</summary>
    private readonly HashSet<string> _namesReferencedInDisabledText = new(StringComparer.Ordinal);

    public LocalFunctionLiftAnalyzer(
        SemanticModel model,
        LocalFunctionLiftOptions options,
        LocalFunctionLiftStatistics stats,
        List<LocalFunctionLiftDiagnostic> diagnostics,
        Dictionary<string, HashSet<string>> crossFileNameTable)
    {
        _model = model;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _crossFileNameTable = crossFileNameTable;
    }

    /// <summary>
    /// Analyzes all local functions in the compilation unit and returns LiftPlans.
    /// </summary>
    public List<LiftPlan> Analyze(CompilationUnitSyntax root)
    {
        var localFns = root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().ToList();
        var plans = new List<LiftPlan>();

        CollectNamesReferencedInDisabledText(root);

        foreach (var localFn in localFns)
        {
            var plan = AnalyzeOne(root, localFn);
            plans.Add(plan);
        }

        PropagateCaptures(plans);

        var bySymbol = plans.Where(p => p.Symbol != null)
            .ToDictionary(p => p.Symbol!, p => p, SymbolEqualityComparer.Default);

        // 写入型捕获要进 lambda（方法组转换 / 已有 lambda 内引用）时不能是 ref 形参
        // （CS1628）。Holder 决策按变量符号全局生效：一旦某个变量改成 StrongBox，
        // 所有提升函数对它的捕获都必须统一成 StrongBox 形参。
        var holderSymbols = CollectHolderCaptureSymbols(plans, bySymbol);
        ApplyHolderCaptures(plans, holderSymbols);

        foreach (var plan in plans)
        {
            if (plan.Skipped)
                continue;
            CheckPostPropagationGates(plan, bySymbol);
        }

        // Gate #12: References skipped local functions.
        // If a non-skipped local function calls a skipped local function,
        // it cannot be lifted because the skipped function remains local.
        CheckReferencesToSkippedLocalFunctions(plans);
        CheckDuplicateNamesWithUnboundCallSites(root, plans);

        return plans;
    }

    /// <summary>
    /// 同一宿主类型内重名局部函数且调用点绑定失败时整组保守跳过。
    /// 重写器在符号缺失时按名字回退到唯一 plan；重名会让所有未绑定调用点
    /// 指向同一个 plan（真实 SDK：GetDefaultSimpleConverters 与
    /// GetDictionaryKeyConverters 都声明局部函数 Add，整树编译把
    /// regular-expressions 工程的 global using System.ComponentModel 泄漏进
    /// json 文件，使 new BooleanConverter() 二义 → Add(...) 绑定失败，18 个
    /// Dictionary 调用被错误改写成 ConcurrentDictionary 版本 → CS1503）。
    /// </summary>
    private void CheckDuplicateNamesWithUnboundCallSites(
        CompilationUnitSyntax root, List<LiftPlan> plans)
    {
        foreach (var group in plans
                     .Where(p => !p.Skipped)
                     .GroupBy(p => (Host: p.HostType, Name: p.Node.Identifier.Text)))
        {
            if (group.Count() < 2)
            {
                continue;
            }

            var name = group.Key.Name;
            var hasUnbound = root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(invocation => invocation.Expression is IdentifierNameSyntax id
                    && string.Equals(id.Identifier.Text, name, StringComparison.Ordinal)
                    && _model.GetSymbolInfo(invocation).Symbol == null);
            if (!hasUnbound)
            {
                continue;
            }

            foreach (var plan in group)
            {
                Skip(plan, LocalFunctionSkipReason.AmbiguousName,
                    "another local function with the same name exists in this type and a call site " +
                    "does not bind; the name-based fallback would target the wrong overload");
            }
        }
    }

    private LiftPlan AnalyzeOne(CompilationUnitSyntax root, LocalFunctionStatementSyntax localFn)
    {
        var symbol = _model.GetDeclaredSymbol(localFn) as IMethodSymbol;
        var plan = new LiftPlan
        {
            Node = localFn,
            Symbol = symbol!,
            NewName = "",
            IsStatic = false,
            Skipped = false,
            SkipReason = LocalFunctionSkipReason.None,
        };

        // Gate #2: No body
        if (localFn.Body == null && localFn.ExpressionBody == null)
        {
            Skip(plan, LocalFunctionSkipReason.NoBody, "local function has no body");
            return plan;
        }

        // Gate #2b: 名字在条件编译区（如 #if DEBUG）里被引用。
        // 重写器只能重命名活动代码，禁用区是不透明文本（DisabledTextTrivia），
        // 其中的 nameof/调用会保留旧名（cs-text p05: RegexPrefixAnalyzer 的
        // `nameof(TryFindRawFixedSets)` 位于 #if DEBUG，而 generator 工程
        // 真实编译时 DEBUG 是定义的 → CS0103）。无法安全等价提升，按设计保留。
        if (_namesReferencedInDisabledText.Contains(localFn.Identifier.Text))
        {
            Skip(plan, LocalFunctionSkipReason.NameReferencedInDisabledText,
                $"local function name is referenced in a conditionally compiled region that this " +
                "analysis compilation excludes (#if); the reference cannot be renamed and the " +
                "region may compile in real builds",
                intentionallyRetained: true);
            return plan;
        }

        // Gate #1 (disabled): Iterator (yield return / yield break).
        // Iterator local functions can be lifted to class-level methods
        // that also use yield return. The hoisted method will be a valid
        // iterator method.
        // Note: iterator methods cannot have ref/out parameters, so if
        // captures require ref, the post-propagation gate will catch it.

        // Gate #3: No host type
        var hostType = FindHostType(localFn);
        if (hostType == null)
        {
            Skip(plan, LocalFunctionSkipReason.NoHostType, "no enclosing type declaration found");
            return plan;
        }
        if (IsInsideFieldOrPropertyInitializer(localFn))
        {
            Skip(plan, LocalFunctionSkipReason.NoHostType, "local function is inside a field/property initializer");
            return plan;
        }
        plan.HostType = hostType;

        // Gate #4: Interface host
        if (hostType is InterfaceDeclarationSyntax && !_options.AllowInterfaceHost)
        {
            Skip(plan, LocalFunctionSkipReason.InterfaceHost, "host type is an interface");
            return plan;
        }

        // Gate #6: checked/unchecked context
        if (IsInsideCheckedContext(localFn))
        {
            Skip(plan, LocalFunctionSkipReason.CheckedContext, "local function is inside a checked/unchecked block");
            return plan;
        }

        plan.IsStatic = DetermineStatic(localFn);
        var isExplicitStatic = IsExplicitStaticLocalFunction(localFn);

        // Gate #5a: Static local functions with the 'static' modifier cannot
        // capture enclosing locals. However, instead of skipping them, we
        // treat them as non-static so captures are collected properly.
        // The hoisted method will be static (since the enclosing method is
        // typically static) and captures will be passed as parameters.
        // Only skip if the enclosing method is an instance method — in that
        // case, the hoisted method would need 'this' which a static local
        // function doesn't have.
        if (isExplicitStatic && ReferencesEnclosingLocals(localFn, symbol!))
        {
            // Check if enclosing method is static. If so, we can safely
            // treat this as non-static for capture purposes.
            if (plan.IsStatic)
            {
                // Enclosing method is static, so the hoisted method will be
                // static too. Treat as non-static for capture collection.
                isExplicitStatic = false;
            }
            else
            {
                // Enclosing method is an instance method. A static local
                // function inside an instance method that references
                // enclosing locals cannot be safely lifted.
                Skip(plan, LocalFunctionSkipReason.UnsupportedCaptureType,
                    "static local function references enclosing local variables/parameters inside instance method");
                return plan;
            }
        }

        // Gate #5 (disabled): Method group / delegate conversion reference.
        // We now support this case — the rewriter will rename the method group
        // reference to the new method name. The delegate conversion still works
        // because the hoisted method has the same signature.

        // Collect captures
        var unresolvedCaptures = new List<ISymbol>();
        plan.Captures = CollectCaptures(
            localFn, symbol!, isExplicitStatic, hostType, unresolvedCaptures);

        // 捕获变量的类型在语义模型里是 ErrorType 时，捕获收集会丢弃它（无法拼出形参类型）。
        // 若继续提升，方法体仍按原名引用该变量，生成的方法作用域里没有这个名字（CS0103）。
        // 有意保留原局部函数：这是工具链无法解析类型，不是源码缺陷。
        if (unresolvedCaptures.Count > 0)
        {
            var names = string.Join(", ", unresolvedCaptures
                .Select(static s => s.Name)
                .Distinct(StringComparer.Ordinal));
            Skip(plan, LocalFunctionSkipReason.UnsupportedCaptureType,
                $"captured variable(s) {names} have unresolved types; the hoisted method " +
                "cannot declare a parameter for them and would reference undeclared names",
                intentionallyRetained: true);
            return plan;
        }

        // Gate #7: Unsupported capture type
        foreach (var cap in plan.Captures)
        {
            if (IsUnsupportedCaptureType(cap))
            {
                Skip(plan, LocalFunctionSkipReason.UnsupportedCaptureType,
                    $"capture variable '{cap.OriginalName}' has unsupported type: {cap.TypeDisplay}");
                return plan;
            }
        }

        // Assign name
        plan.NewName = AssignName(localFn, hostType, symbol!);

        // Gate #10: CapturesAtEnd with params/optional
        if (_options.CapturesAtEnd)
        {
            if (HasParamsOrOptional(localFn))
            {
                Skip(plan, LocalFunctionSkipReason.ParamsOrOptionalConflict,
                    "CapturesAtEnd=true but original parameters contain params or default values");
                return plan;
            }
        }

        // Gate #11 (removed): Named argument at call sites.
        // Named arguments still work after lifting because capture parameters
        // are prepended as positional arguments, and named arguments reference
        // the original parameter names which remain unchanged. C# allows
        // mixing positional and named arguments as long as positionals come first.

        // R1b：类型参数携带（own + 外层方法/局部函数约束闭包、宿主类级形参不重复声明）
        // 交给统一解析器 SyntheticTypeParameterResolver；同名遮蔽由 StableName 消歧。
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        var signatureTypes = new List<ITypeSymbol?>(localFn.ParameterList.Parameters.Count + 1);
        foreach (var parameter in localFn.ParameterList.Parameters)
            signatureTypes.Add((_model.GetDeclaredSymbol(parameter) as IParameterSymbol)?.Type);
        signatureTypes.Add(symbol!.ReturnType);

        var typeParameterPlan = SyntheticTypeParameterResolver.Resolve(
            _model, localFn,
            ownTypeParameterList: localFn.TypeParameterList,
            ownConstraintClauses: localFn.ConstraintClauses,
            body: body,
            signatureTypes: signatureTypes);

        // S-03：任一携带形参无法安全表达 → 保守跳过（不产出悬挂 where 子句）。
        if (typeParameterPlan.Parameters.Any(static p =>
                p.State == SyntheticTypeParameterState.NotExpressible))
        {
            var reasons = string.Join("; ", typeParameterPlan.Diagnostics
                .Where(static d => d.Code == SyntheticTypeParameterResolver.NotExpressibleCode)
                .Select(static d => d.Message));
            Skip(plan, LocalFunctionSkipReason.EnclosingTypeParameterConflict,
                $"type parameter constraints cannot be reproduced safely: {reasons}");
            return plan;
        }

        plan.TypeParameterPlan = typeParameterPlan;
        var carriedEnclosing = typeParameterPlan.Parameters
            .Where(static p => p.Origin != SyntheticTypeParameterOrigin.Own)
            .Select(static p => p.RenderName)
            .ToList();
        if (carriedEnclosing.Count > 0)
        {
            plan.EnclosingTypeParams = carriedEnclosing;
            plan.NeedsExplicitTypeArguments = true;
        }

        return plan;
    }

    private void Skip(
        LiftPlan plan, LocalFunctionSkipReason reason, string message,
        bool intentionallyRetained = false)
    {
        plan.Skipped = true;
        plan.SkipReason = reason;
        plan.IntentionallyRetained = intentionallyRetained;
        _stats.LocalFunctionsSkipped++;
        if (!_stats.SkippedByReason.TryAdd(reason, 1))
            _stats.SkippedByReason[reason]++;

        var lineSpan = plan.Node.GetLocation().GetLineSpan();
        var line = lineSpan.IsValid ? lineSpan.StartLinePosition.Line + 1 : 0;
        _diagnostics.Add(new LocalFunctionLiftDiagnostic(
            LocalFunctionLiftSeverity.Warning,
            $"Skipped local function '{plan.Node.Identifier.Text}': {message}",
            null, line, reason, intentionallyRetained));
    }

    private static TypeDeclarationSyntax? FindHostType(LocalFunctionStatementSyntax localFn)
    {
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is TypeDeclarationSyntax typeDecl)
                return typeDecl;
            current = current.Parent;
        }
        return null;
    }

    private static bool IsInsideLambdaOrInitializer(LocalFunctionStatementSyntax localFn)
    {
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is AnonymousFunctionExpressionSyntax
                or SimpleLambdaExpressionSyntax
                or ParenthesizedLambdaExpressionSyntax)
                return true;
            if (current is VariableDeclaratorSyntax vd && vd.Initializer != null)
                return true;
            if (current is PropertyDeclarationSyntax pd
                && (pd.ExpressionBody != null || pd.Initializer != null))
                return true;
            if (current is TypeDeclarationSyntax)
                return false;
            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// Checks if the local function is inside a field or property initializer.
    /// Local functions inside lambdas are OK (they can be lifted to the class level),
    /// but local functions inside field/property initializers cannot be lifted
    /// because the initializer context is not a method body.
    /// </summary>
    private static bool IsInsideFieldOrPropertyInitializer(
        LocalFunctionStatementSyntax localFn)
    {
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            // Field initializer: variable declarator with initializer,
            // whose parent is a FieldDeclarationSyntax
            if (current is VariableDeclaratorSyntax vd
                && vd.Initializer != null
                && vd.Parent?.Parent is FieldDeclarationSyntax)
                return true;
            // Property initializer
            if (current is PropertyDeclarationSyntax pd
                && (pd.ExpressionBody != null || pd.Initializer != null))
                return true;
            if (current is TypeDeclarationSyntax)
                return false;
            current = current.Parent;
        }
        return false;
    }

    private static bool IsInsideCheckedContext(LocalFunctionStatementSyntax localFn)
    {
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is CheckedExpressionSyntax || current is CheckedStatementSyntax)
                return true;
            if (current is TypeDeclarationSyntax)
                return false;
            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// Returns true if the local function itself has the 'static' modifier
    /// (as opposed to merely being inside a static method).
    /// </summary>
    private static bool IsExplicitStaticLocalFunction(LocalFunctionStatementSyntax localFn)
    {
        return localFn.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
    }

    private static bool DetermineStatic(LocalFunctionStatementSyntax localFn)
    {
        if (localFn.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            return true;

        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is LocalFunctionStatementSyntax)
            {
                current = current.Parent;
                continue;
            }
            if (current is MemberDeclarationSyntax member)
            {
                return member.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
            }
            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// Checks if a static local function references local variables or
    /// parameters from the enclosing scope (which would require captures
    /// that a static hoisted method cannot have).
    /// </summary>
    private bool ReferencesEnclosingLocals(
        LocalFunctionStatementSyntax localFn,
        IMethodSymbol localFnSymbol)
    {
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body == null)
            return false;

        foreach (var identifier in body
            .DescendantNodes(d => d is not LocalFunctionStatementSyntax)
            .OfType<IdentifierNameSyntax>())
        {
            if (identifier.Parent is NameColonSyntax)
                continue;

            var symbol = _model.GetSymbolInfo(identifier).Symbol;
            if (symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol)
            {
                // Skip references to the local function's own parameters
                if (SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol, localFnSymbol))
                    continue;
                return true;
            }
        }
        return false;
    }

    private List<CapturePlan> CollectCaptures(
        LocalFunctionStatementSyntax localFn,
        IMethodSymbol localFnSymbol,
        bool isStatic,
        TypeDeclarationSyntax hostType,
        ICollection<ISymbol>? unresolvedSymbols = null)
    {
        if (isStatic)
            return new List<CapturePlan>();

        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body == null)
            return new List<CapturePlan>();

        // C32 §4.2: capture discovery is the single shared primitive.
        var capParamNames = new HashSet<string>(StringComparer.Ordinal);
        return CaptureClassification.CollectCapturedSymbols(
                _model,
                body,
                localFnSymbol,
                d => d is not LocalFunctionStatementSyntax,
                unresolvedSymbols)
            .Select(info =>
            {
                var isWritten = IsCapturedVariableWritten(localFn, info.Symbol);
                var capName = LocalFunctionLiftNaming.GetCaptureParameterName(
                    info.Name, capParamNames);
                return new CapturePlan(
                    info.Symbol,
                    info.Name,
                    capName,
                    RenderCaptureTypeDisplay(info.Type, hostType),
                    isWritten,
                    isWritten);
            })
            .ToList();
    }

    /// <summary>
    /// 捕获形参的类型显示。形参声明落在宿主类型的成员区，与捕获声明处同文件、
    /// 同命名空间、同类型作用域，因此优先用「该位置按语言规则可解析的最短形态」。
    ///
    /// 全树编译（ProjectPassRunner 把整棵树的源文件放进同一个 Compilation）里，
    /// 别的工程的源类型对本文件可见，<see cref="SymbolDisplayFormat.FullyQualifiedFormat"/>
    /// 会写出真实工程里并不存在的名字。cs-text p05 实例：
    /// System.Private.CoreLib 的 <c>dotnet.System.Text.StringBuilder</c> 位于本文件
    /// 命名空间 <c>dotnet.System.Text.RegularExpressions</c> 的祖先命名空间中，按简单名
    /// 查找会先命中它并遮蔽 <c>using System.Text;</c> 引入的 BCL
    /// <c>System.Text.StringBuilder</c>；生成的
    /// <c>global::dotnet.System.Text.StringBuilder</c> 形参在只引用 BCL 的 regex 工程里
    /// 是 CS0234。源声明类型退回最短形态后，名字按原代码的同一套 using/命名空间规则
    /// 重绑定，真实工程的解析结果与提升前一致。
    ///
    /// 元数据类型不受合并编译影响，仍用 <c>global::</c> 全限定形态：最短形态依赖的
    /// using 可能只来自别的工程的 global using（见 ProjectPassAdapter 的并集合成），
    /// 在真实工程里未必可见。
    /// </summary>
    private string RenderCaptureTypeDisplay(ITypeSymbol type, TypeDeclarationSyntax hostType)
    {
        if (!IsDeclaredInAnalysisSource(type))
        {
            return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        var position = hostType.OpenBraceToken.SpanStart;
        var minimalText = type.ToMinimalDisplayString(_model, position);
        var minimalType = SyntaxFactory.ParseTypeName(minimalText);
        if (!minimalType.ContainsDiagnostics
            && BindsToTypeAtPosition(minimalType, position, type))
        {
            return minimalText;
        }

        return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    /// <summary>
    /// 类型（含数组元素、指针目标、泛型实参）是否由本次分析编译的源文件声明。
    /// 合并编译里的源类型可能属于别的工程，真实工程编译它时不可见。
    /// </summary>
    private bool IsDeclaredInAnalysisSource(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return IsDeclaredInAnalysisSource(array.ElementType);
            case IPointerTypeSymbol pointer:
                return IsDeclaredInAnalysisSource(pointer.PointedAtType);
            case INamedTypeSymbol named:
                if (SymbolEqualityComparer.Default.Equals(
                        named.ContainingAssembly, _model.Compilation.Assembly))
                {
                    return true;
                }

                return named.TypeArguments.Any(IsDeclaredInAnalysisSource);
            default:
                return false;
        }
    }

    private bool BindsToTypeAtPosition(
        TypeSyntax typeSyntax, int position, ITypeSymbol expected)
    {
        try
        {
            return _model.GetSpeculativeSymbolInfo(
                    position, typeSyntax, SpeculativeBindingOption.BindAsTypeOrNamespace)
                .Symbol is ITypeSymbol resolved
                && SymbolEqualityComparer.Default.Equals(resolved, expected);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool IsCapturedVariableWritten(
        LocalFunctionStatementSyntax localFn,
        ISymbol symbol)
    {
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body == null)
            return false;

        // C32 §4.2: write-site discovery is the single shared primitive.
        return CaptureClassification.FindWriteSites(
                _model,
                symbol,
                body,
                d => d is not LocalFunctionStatementSyntax)
            .Count > 0;
    }

    private void PropagateCaptures(List<LiftPlan> plans)
    {
        var bySymbol = plans.Where(p => p.Symbol != null)
            .ToDictionary(p => p.Symbol!, p => p, SymbolEqualityComparer.Default);

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var plan in plans)
            {
                if (plan.Skipped)
                    continue;

                var body = (SyntaxNode?)plan.Node.Body ?? plan.Node.ExpressionBody;
                if (body == null)
                    continue;

                foreach (var identifier in body
                    .DescendantNodes(d => d is not LocalFunctionStatementSyntax)
                    .OfType<IdentifierNameSyntax>())
                {
                    if (identifier.Parent is NameColonSyntax)
                        continue;

                    var calleeSymbol = _model.GetSymbolInfo(identifier).Symbol;
                    if (calleeSymbol is not IMethodSymbol { MethodKind: MethodKind.LocalFunction } callee)
                        continue;
                    if (!bySymbol.TryGetValue(callee, out var calleePlan))
                        continue;
                    if (calleePlan.Skipped)
                        continue;

                    foreach (var capture in calleePlan.Captures)
                    {
                        // 嵌套局部函数捕获的变量可能就是 caller 自己的局部变量
                        // （例如 Compute 里的 charSetSolver 被它内部的
                        // MapCategoryCodeSetToCondition 捕获）。这不是 caller 的
                        // 外部捕获：caller 提升后，callee 调用点仍在 caller 方法体
                        // 内，直接引用该局部变量即可。若把它传播成 caller 的形参，
                        // caller 的外部调用点会被追加一个根本不在作用域内的实参
                        // （CS0103）。
                        if (CaptureClassification.IsDeclaredInside(capture.Symbol, body))
                            continue;

                        // caller 自己的形参同理：嵌套局部函数捕获它时，提升后的
                        // caller 方法里它就是普通形参，直接引用即可。若传播成
                        // caller 的捕获，会产生与形参同名的重复形参（CS0100），
                        // 或按名字把形参引用错误改写成捕获参数
                        // （gen/RegexGenerator.Emitter.cs 的 EmitAlternation）。
                        if (SymbolEqualityComparer.Default.Equals(
                                capture.Symbol.ContainingSymbol, plan.Symbol))
                            continue;

                        var existing = plan.Captures.FirstOrDefault(c =>
                            SymbolEqualityComparer.Default.Equals(c.Symbol, capture.Symbol));
                        if (existing == null)
                        {
                            // 两个不同符号可能同名（例如 caller 自有的 node 与 callee 参数 node），
                            // 各自独立分配后都得到 _cs2j_cap_node。提升后的形参名必须在
                            // caller 的提升方法作用域内唯一，否则出现重复形参（CS0100）。
                            plan.Captures.Add(capture with
                            {
                                ParameterName = AllocateCaptureParameterName(plan, capture.OriginalName),
                            });
                            changed = true;
                        }
                        else if (capture.IsWritten && !existing.IsWritten)
                        {
                            plan.Captures[plan.Captures.IndexOf(existing)] =
                                existing with { IsWritten = true, ByRef = true };
                            changed = true;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// 在 caller 提升后的方法作用域内为新捕获分配唯一形参名：
    /// 避开已有捕获形参、原方法参数名与类型参数名。
    /// </summary>
    private static string AllocateCaptureParameterName(LiftPlan plan, string originalName)
    {
        var usedNames = new HashSet<string>(
            plan.Captures.Select(c => c.ParameterName), StringComparer.Ordinal);
        if (plan.Node.ParameterList != null)
        {
            foreach (var parameter in plan.Node.ParameterList.Parameters)
                usedNames.Add(parameter.Identifier.Text);
        }
        if (plan.Node.TypeParameterList != null)
        {
            foreach (var typeParameter in plan.Node.TypeParameterList.Parameters)
                usedNames.Add(typeParameter.Identifier.Text);
        }
        return LocalFunctionLiftNaming.GetCaptureParameterName(originalName, usedNames);
    }

    private void CheckPostPropagationGates(
        LiftPlan plan, IReadOnlyDictionary<ISymbol, LiftPlan> bySymbol)
    {
        // 写入型捕获提升为 ref 形参后，调用点的实参必须确定赋值（CS0165）。
        // 原局部函数允许「局部声明时不初始化、只在其函数体内赋值」
        // （cs-text p05: RegexCharClass.renderRanges 的 ch1/ch2）。这类声明在重写器
        // 里补 `= default(T)`：原代码能通过确定赋值分析，说明读取前必经赋值，
        // 补默认值只影响确定赋值，不改变运行语义。只有声明形式无法原地补
        // 初始化器（using/fixed/ref 局部等）时才保留原局部函数。
        // holder 捕获按值传入 StrongBox，不存在确定赋值要求，不触发该闸门。
        var unassignedCapture = plan.Captures.FirstOrDefault(c =>
            c.IsWritten && !c.UseHolder && c.Symbol is ILocalSymbol local
            && !HasInitializer(c.Symbol) && !CanSeedDefaultInitializer(local));
        if (unassignedCapture != null)
        {
            var local = (ILocalSymbol)unassignedCapture.Symbol;
            Skip(plan, LocalFunctionSkipReason.RefCaptureNotDefinitelyAssigned,
                $"write capture '{unassignedCapture.OriginalName}' is declared without an " +
                "initializer and cannot be seeded with a default value " +
                $"({GetSeedingUnavailableReason(local)}); the lifted ref parameter would require " +
                "definite assignment at every call site (CS0165)",
                intentionallyRetained: true);
            return;
        }

        foreach (var cap in plan.Captures)
        {
            if (!cap.IsWritten || cap.UseHolder)
                continue;

            if (!CanUseRefCapture(plan, cap))
            {
                Skip(plan, LocalFunctionSkipReason.RefCaptureUnavailable,
                    $"write capture '{cap.OriginalName}' cannot use ref: " +
                    GetRefCaptureUnavailableReason(plan, cap));
                return;
            }
        }

        // 写入型捕获若必须以 ref 形参传递，就不能出现在 lambda 内（CS1628）。
        // 这类捕获已在上一步尽量改用 StrongBox holder；只有 holder 不可表达
        // （如 ref struct / 指针 / 参数捕获等）时才保留原局部函数。
        var offending = FindLambdaExposedWrittenCaptures(plan, bySymbol)
            .FirstOrDefault(static c => !c.UseHolder);
        if (offending != null)
        {
            var lineSpan = plan.Node.GetLocation().GetLineSpan();
            var line = lineSpan.IsValid ? lineSpan.StartLinePosition.Line + 1 : 0;
            Skip(plan, LocalFunctionSkipReason.RefCaptureInNestedLambda,
                $"write capture '{offending.OriginalName}' would be referenced from a lambda in the " +
                $"hoisted body (local function starts at line {line}); a ref parameter cannot be " +
                "used there (CS1628) and the capture type cannot be wrapped in a StrongBox holder " +
                $"({GetHolderUnavailableReason(offending)})",
                intentionallyRetained: true);
        }
    }

    /// <summary>
    /// 收集「提升后会落进 lambda、因而不能是 ref 形参」的写入型捕获，返回本 plan
    /// 自己的 CapturePlan 实例（决定 holder 标记用）：
    /// - 方法组引用（任何位置）：重写器会合成 `(args) =&gt; callee(caps, args)`，
    ///   该 lambda 会以 ref 形式携带 callee 的全部写入型捕获；
    /// - 直接调用，且调用点位于已有 lambda / 匿名方法内：重写器插入的 `ref cap`
    ///   实参落在 lambda 内；
    /// - lambda 内直接引用写入型捕获（读或写）。
    /// 与捕获收集一致，不下降进更深层的局部函数声明（它们各自有独立的 LiftPlan）。
    /// </summary>
    private List<CapturePlan> FindLambdaExposedWrittenCaptures(
        LiftPlan plan, IReadOnlyDictionary<ISymbol, LiftPlan> bySymbol)
    {
        var result = new List<CapturePlan>();
        var body = (SyntaxNode?)plan.Node.Body ?? plan.Node.ExpressionBody;
        if (body == null)
            return result;

        var writtenCaptures = plan.Captures.Where(static c => c.IsWritten).ToList();
        if (writtenCaptures.Count == 0)
            return result;

        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var identifier in body
            .DescendantNodes(d => d is not LocalFunctionStatementSyntax)
            .OfType<IdentifierNameSyntax>())
        {
            if (identifier.Parent is NameColonSyntax)
                continue;
            // nameof(f) 只改名，不追加捕获实参。
            if (CaptureClassification.IsInsideNameOf(identifier))
                continue;

            var insideLambda = IsInsideAnonymousFunction(identifier, body);
            var symbol = _model.GetSymbolInfo(identifier).Symbol;

            if (symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction } callee
                && bySymbol.TryGetValue(callee, out var calleePlan))
            {
                var calleeWritten = calleePlan.Captures
                    .Where(static c => c.IsWritten)
                    .ToList();
                if (calleeWritten.Count == 0)
                    continue;

                var isInvocationTarget = identifier.Parent is InvocationExpressionSyntax invocation
                    && invocation.Expression == identifier;
                if (isInvocationTarget && !insideLambda)
                    continue;

                // 只报告本 plan 确实以 ref 形参携带的捕获（捕获传播已合并 callee 的捕获）。
                foreach (var calleeCapture in calleeWritten)
                {
                    var propagated = writtenCaptures.FirstOrDefault(c =>
                        SymbolEqualityComparer.Default.Equals(c.Symbol, calleeCapture.Symbol));
                    if (propagated != null && seen.Add(propagated.Symbol))
                        result.Add(propagated);
                }
                continue;
            }

            if (!insideLambda)
                continue;

            var directCapture = writtenCaptures.FirstOrDefault(c =>
                SymbolEqualityComparer.Default.Equals(c.Symbol, symbol));
            if (directCapture != null && seen.Add(directCapture.Symbol))
                result.Add(directCapture);
        }
        return result;
    }

    private HashSet<ISymbol> CollectHolderCaptureSymbols(
        List<LiftPlan> plans, IReadOnlyDictionary<ISymbol, LiftPlan> bySymbol)
    {
        var holders = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var plan in plans)
        {
            if (plan.Skipped)
                continue;
            foreach (var capture in FindLambdaExposedWrittenCaptures(plan, bySymbol))
            {
                if (CanUseHolderCapture(capture))
                    holders.Add(capture.Symbol);
            }
        }
        return holders;
    }

    /// <summary>
    /// 把 holder 决策应用到所有 plan 的同符号捕获。变量本身变成 StrongBox 后，
    /// 即使某个提升函数只是读取它，也必须接收 StrongBox 形参。
    /// </summary>
    private void ApplyHolderCaptures(List<LiftPlan> plans, HashSet<ISymbol> holderSymbols)
    {
        if (holderSymbols.Count == 0)
            return;

        foreach (var plan in plans)
        {
            for (var i = 0; i < plan.Captures.Count; i++)
            {
                var capture = plan.Captures[i];
                if (capture.UseHolder || !holderSymbols.Contains(capture.Symbol))
                    continue;

                var type = GetCaptureType(capture.Symbol);
                if (type == null)
                    continue;

                plan.Captures[i] = capture with
                {
                    UseHolder = true,
                    ByRef = false,
                    HolderElementTypeDisplay = plan.HostType != null
                        ? RenderCaptureTypeDisplay(type, plan.HostType)
                        : type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                };
            }
        }
    }

    private static ITypeSymbol? GetCaptureType(ISymbol symbol)
        => symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            _ => null,
        };

    /// <summary>
    /// 该捕获能否安全改写为 StrongBox 闭包：
    /// - 必须是有普通局部声明语句覆盖的局部变量（参数/foreach/using/fixed/const 无法原地改写）；
    /// - 类型必须能作泛型实参（非 ref struct、非指针、非函数指针、非未绑定泛型）。
    /// </summary>
    private bool CanUseHolderCapture(CapturePlan capture)
    {
        if (capture.Symbol is not ILocalSymbol local || local.IsConst)
            return false;

        if (!IsValidStrongBoxElementType(local.Type))
            return false;

        var declarator = local.DeclaringSyntaxReferences
            .Select(static reference => reference.GetSyntax())
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault();
        if (declarator?.Parent is not VariableDeclarationSyntax declaration)
            return false;
        if (declaration.Variables.Count != 1)
            return false;
        if (declaration.Parent is not LocalDeclarationStatementSyntax statement)
            return false;
        if (!statement.UsingKeyword.IsKind(SyntaxKind.None)
            || !statement.AwaitKeyword.IsKind(SyntaxKind.None))
            return false;
        if (statement.Modifiers.Any(static m => m.IsKind(SyntaxKind.ConstKeyword)))
            return false;
        if (declaration.Type is RefTypeSyntax or ScopedTypeSyntax)
            return false;

        return true;
    }

    private static string GetHolderUnavailableReason(CapturePlan capture)
    {
        if (capture.Symbol is not ILocalSymbol local)
            return $"capture is a {capture.Symbol.Kind}, not a plain local variable";
        if (local.IsConst)
            return "capture is a const local";
        if (!IsValidStrongBoxElementType(local.Type))
            return $"type '{local.Type.ToDisplayString()}' cannot be a StrongBox type argument";
        return "the local declaration cannot be rewritten in place (foreach/using/fixed/multi-declarator)";
    }

    private static bool IsValidStrongBoxElementType(ITypeSymbol type)
    {
        switch (type)
        {
            case IPointerTypeSymbol:
            case IFunctionPointerTypeSymbol:
                return false;
            case IArrayTypeSymbol array:
                return IsValidStrongBoxElementType(array.ElementType);
        }

        if (type.IsRefLikeType)
            return false;
        if (type.TypeKind == TypeKind.Error)
            return false;

        if (type is INamedTypeSymbol named)
        {
            if (named.IsUnboundGenericType)
                return false;
            foreach (var argument in named.TypeArguments)
            {
                if (!IsValidStrongBoxElementType(argument))
                    return false;
            }
        }

        return true;
    }

    private static bool IsInsideAnonymousFunction(SyntaxNode node, SyntaxNode scope)
    {
        for (var current = node.Parent; current != null && current != scope; current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Gate #12: If a non-skipped local function references a skipped local
    /// function, it must also be skipped because the skipped function remains
    /// local and cannot be accessed from the class-level method.
    /// Uses iterative fixpoint propagation because skipping a function may
    /// cause other functions that reference it to also need skipping.
    /// </summary>
    private void CheckReferencesToSkippedLocalFunctions(List<LiftPlan> plans)
    {
        var skippedSymbols = new HashSet<ISymbol>(
            plans.Where(p => p.Skipped && p.Symbol != null)
                 .Select(p => p.Symbol!),
            SymbolEqualityComparer.Default);
        var bySymbol = plans.Where(p => p.Symbol != null)
            .ToDictionary(p => p.Symbol!, p => p, SymbolEqualityComparer.Default);

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var plan in plans)
            {
                if (plan.Skipped)
                    continue;

                var body = (SyntaxNode?)plan.Node.Body ?? plan.Node.ExpressionBody;
                if (body == null)
                    continue;

                foreach (var identifier in body
                    .DescendantNodes(d => d is not LocalFunctionStatementSyntax)
                    .OfType<IdentifierNameSyntax>())
                {
                    if (identifier.Parent is NameColonSyntax)
                        continue;

                    var symbol = _model.GetSymbolInfo(identifier).Symbol;
                    if (symbol != null && skippedSymbols.Contains(symbol))
                    {
                        // 调用者因为引用了“有意保留”的函数而级联保留，同样不算改写残留。
                        var intentionallyRetained = bySymbol.TryGetValue(symbol, out var callee)
                            && callee.IntentionallyRetained;
                        Skip(plan, LocalFunctionSkipReason.ReferencesSkippedLocalFunction,
                            $"references skipped local function '{identifier.Identifier.Text}'",
                            intentionallyRetained);
                        if (plan.Symbol != null)
                            skippedSymbols.Add(plan.Symbol);
                        changed = true;
                        break;
                    }
                }
            }
        }
    }

    private bool CanUseRefCapture(LiftPlan plan, CapturePlan cap)
    {
        if (plan.Node.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            return false;

        var sym = cap.Symbol;
        if (sym is ILocalSymbol)
        {
            var decl = FindVariableDeclarator(plan.Node, sym);
            if (decl != null)
            {
                if (IsForeachOrUsingOrFixedVariable(decl))
                    return false;
                if (decl.Initializer == null)
                    return false;
            }
        }
        if (sym is IParameterSymbol param)
        {
            if (param.RefKind is RefKind.In or RefKind.Out)
                return false;
        }

        return true;
    }

    private static string GetRefCaptureUnavailableReason(LiftPlan plan, CapturePlan cap)
    {
        if (plan.Node.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            return "local function is async";
        if (cap.Symbol is ILocalSymbol)
        {
            var decl = FindVariableDeclarator(plan.Node, cap.Symbol);
            if (decl != null)
            {
                if (IsForeachOrUsingOrFixedVariable(decl))
                    return $"variable '{cap.OriginalName}' is a foreach/using/fixed variable";
                if (decl.Initializer == null)
                    return $"variable '{cap.OriginalName}' has no initializer";
            }
        }
        if (cap.Symbol is IParameterSymbol param)
        {
            if (param.RefKind is RefKind.In)
                return $"parameter '{cap.OriginalName}' is 'in'";
            if (param.RefKind is RefKind.Out)
                return $"parameter '{cap.OriginalName}' is 'out'";
        }
        return "ref not available";
    }

    /// <summary>
    /// 被捕获的局部变量声明时是否带初始化器。带初始化器才能保证调用点确定赋值
    /// （ref 实参的前提）；模式变量/解构声明本身就是赋值，不算未赋值。
    /// </summary>
    private static bool HasInitializer(ISymbol symbol)
    {
        var declaring = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        return declaring is not VariableDeclaratorSyntax declarator
            || declarator.Initializer != null;
    }

    /// <summary>
    /// 无初始化器的局部能否在声明处补 `= default(T)` 以满足调用点的确定赋值：
    /// 只有普通变量声明（局部声明语句 / for 初始化声明，非 var、非 ref/using/
    /// fixed/const）可以原地补初始化器。
    /// </summary>
    private static bool CanSeedDefaultInitializer(ILocalSymbol local)
        => GetSeedingUnavailableReason(local) == null;

    private static string? GetSeedingUnavailableReason(ILocalSymbol local)
    {
        var declarator = local.DeclaringSyntaxReferences
            .Select(static reference => reference.GetSyntax())
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault();
        if (declarator?.Parent is not VariableDeclarationSyntax declaration)
            return "the local is declared by a pattern, deconstruction or foreach";
        if (declaration.Type is RefTypeSyntax)
            return "'ref' local";
        if (declaration.Type is IdentifierNameSyntax { Identifier.Text: "var" })
            return "'var' local without initializer";
        if (declaration.Parent is LocalDeclarationStatementSyntax statement
            && (!statement.UsingKeyword.IsKind(SyntaxKind.None)
                || !statement.AwaitKeyword.IsKind(SyntaxKind.None)
                || statement.Modifiers.Any(static m => m.IsKind(SyntaxKind.ConstKeyword))))
            return "declared in a using/const local statement";
        if (declaration.Parent is not LocalDeclarationStatementSyntax and not ForStatementSyntax)
            return "declared in a fixed/using statement";
        return null;
    }

    /// <summary>
    /// 收集条件编译区里以函数式引用（<c>name(...)</c> / <c>nameof(name)</c>）出现的
    /// 标识符。禁用区是 DisabledTextTrivia（不透明文本），重写器无法重命名其中的引用；
    /// 这些区域在真实工程里可能被编译（如 generator 工程定义了 DEBUG）。
    /// 只收函数式引用：同名局部变量/字段的普通出现不影响提升。
    /// </summary>
    private void CollectNamesReferencedInDisabledText(CompilationUnitSyntax root)
    {
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (!trivia.IsKind(SyntaxKind.DisabledTextTrivia))
                continue;

            var text = trivia.ToString();
            var index = 0;
            while (index < text.Length)
            {
                if (!SyntaxFacts.IsIdentifierStartCharacter(text[index]))
                {
                    index++;
                    continue;
                }

                var start = index;
                while (index < text.Length && SyntaxFacts.IsIdentifierPartCharacter(text[index]))
                    index++;

                var name = text.Substring(start, index - start);
                if (IsFunctionStyleReference(text, start, index))
                    _namesReferencedInDisabledText.Add(name);
            }
        }
    }

    private static bool IsFunctionStyleReference(string text, int start, int end)
    {
        // nameof(Name)：前一个非空白记号是 nameof(。
        var head = text.AsSpan(0, start).TrimEnd();
        if (!head.IsEmpty && head[^1] == '(')
        {
            var callee = head[..^1].TrimEnd();
            if (callee.EndsWith("nameof".AsSpan(), StringComparison.Ordinal))
            {
                var prefixLength = callee.Length - "nameof".Length;
                if (prefixLength == 0
                    || !SyntaxFacts.IsIdentifierPartCharacter(callee[prefixLength - 1]))
                {
                    return true;
                }
            }
        }

        // Name(...)：后一个非空白字符是 '('。
        var tail = text.AsSpan(end);
        var next = 0;
        while (next < tail.Length && char.IsWhiteSpace(tail[next]))
            next++;
        return next < tail.Length && tail[next] == '(';
    }

    private static VariableDeclaratorSyntax? FindVariableDeclarator(
        SyntaxNode scope, ISymbol symbol)
    {
        foreach (var decl in scope.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (decl.Identifier.Text == symbol.Name)
                return decl;
        }
        return null;
    }

    private static bool IsForeachOrUsingOrFixedVariable(VariableDeclaratorSyntax decl)
    {
        SyntaxNode? current = decl.Parent;
        while (current != null)
        {
            if (current is ForEachStatementSyntax
                or UsingStatementSyntax
                or FixedStatementSyntax)
                return true;
            if (current is MemberDeclarationSyntax)
                return false;
            current = current.Parent;
        }
        return false;
    }

    private bool HasMethodGroupReference(
        CompilationUnitSyntax root,
        IMethodSymbol symbol)
    {
        foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (id.Parent is NameColonSyntax)
                continue;
            var sym = _model.GetSymbolInfo(id).Symbol;
            if (!SymbolEqualityComparer.Default.Equals(sym, symbol))
                continue;
            if (id.Parent is InvocationExpressionSyntax invocation
                && invocation.Expression == id)
                continue;
            if (CaptureClassification.IsInsideNameOf(id))
                continue;
            return true;
        }
        return false;
    }

    private bool HasNamedArgumentCallSite(
        CompilationUnitSyntax root,
        IMethodSymbol symbol)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not IdentifierNameSyntax
                && invocation.Expression is not GenericNameSyntax)
                continue;

            var sym = _model.GetSymbolInfo(invocation).Symbol;
            if (!SymbolEqualityComparer.Default.Equals(sym, symbol))
                continue;

            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                if (arg.NameColon != null)
                    return true;
            }
        }
        return false;
    }

    private static bool IsUnsupportedCaptureType(CapturePlan cap)
        // C32 §4.2: unsupported-kind classification is the single shared primitive.
        => CaptureClassification.IsUnsupportedCaptureKind(cap.TypeDisplay);

    private static bool HasParamsOrOptional(LocalFunctionStatementSyntax localFn)
    {
        if (localFn.ParameterList == null)
            return false;
        foreach (var param in localFn.ParameterList.Parameters)
        {
            if (param.Default != null)
                return true;
            if (param.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword)))
                return true;
        }
        return false;
    }

    private static MethodDeclarationSyntax? FindEnclosingMethod(LocalFunctionStatementSyntax localFn)
    {
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is MethodDeclarationSyntax method)
                return method;
            if (current is TypeDeclarationSyntax)
                return null;
            current = current.Parent;
        }
        return null;
    }

    private static List<string> GetEnclosingMethodTypeParameters(
        LocalFunctionStatementSyntax localFn)
    {
        var result = new List<string>();
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is MethodDeclarationSyntax method
                && method.TypeParameterList != null)
            {
                foreach (var tp in method.TypeParameterList.Parameters)
                    result.Add(tp.Identifier.Text);
            }
            if (current is TypeDeclarationSyntax)
                break;
            current = current.Parent;
        }
        return result;
    }

    /// <summary>
    /// Gate #9b (strict): Checks if the local function uses ANY enclosing
    /// method type parameters in its body, parameter types, or return type.
    /// After lifting, the hoisted method becomes a generic method and type
    /// inference at call sites may fail (e.g. when arguments are default,
    /// null, or lambda expressions). To be safe, we skip all such functions.
    /// </summary>
    private static bool UsesEnclosingTypeParameters(
        LocalFunctionStatementSyntax localFn)
    {
        var enclosingParams = GetEnclosingMethodTypeParameters(localFn);
        if (enclosingParams.Count == 0)
            return false;

        // Type parameters declared on the local function itself
        var ownTypeParams = localFn.TypeParameterList?.Parameters
            .Select(p => p.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

        // Collect all identifier names used in parameter types and return type
        var typeIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        if (localFn.ParameterList != null)
        {
            foreach (var param in localFn.ParameterList.Parameters)
                CollectIdentifierNames(param.Type, typeIdentifiers);
        }
        CollectIdentifierNames(localFn.ReturnType, typeIdentifiers);

        // Collect all identifier names used in the body
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body != null)
        {
            foreach (var id in body.DescendantNodes().OfType<IdentifierNameSyntax>())
                typeIdentifiers.Add(id.Identifier.Text);
        }

        // Check if any enclosing type parameter (not in own type params) is used
        foreach (var tp in enclosingParams)
        {
            if (ownTypeParams.Contains(tp))
                continue;
            if (typeIdentifiers.Contains(tp))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Gate #9b (legacy): Checks if the local function references enclosing method
    /// type parameters that are NOT in its own type parameter list or
    /// parameter list. After lifting, call sites would need to explicitly
    /// specify these type parameters, which they don't.
    /// </summary>
    private static bool UsesEnclosingTypeParametersNotInParameters(
        LocalFunctionStatementSyntax localFn)
    {
        var enclosingParams = GetEnclosingMethodTypeParameters(localFn);
        if (enclosingParams.Count == 0)
            return false;

        // Type parameters declared on the local function itself
        var ownTypeParams = localFn.TypeParameterList?.Parameters
            .Select(p => p.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

        // Type parameters that appear in the local function's parameter types
        // and return type. Note: we must check the type node itself, not just
        // its descendants, because a simple type like "TDict" is itself an
        // IdentifierNameSyntax with no descendant nodes.
        var paramsInParameters = new HashSet<string>(StringComparer.Ordinal);
        if (localFn.ParameterList != null)
        {
            foreach (var param in localFn.ParameterList.Parameters)
            {
                CollectIdentifierNames(param.Type, paramsInParameters);
            }
        }

        // Also check the return type
        CollectIdentifierNames(localFn.ReturnType, paramsInParameters);

        // For each enclosing type parameter, check if it's used in the
        // local function body but not in its own type params or parameter types.
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body == null)
            return false;

        var bodyIdentifiers = body.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Select(id => id.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var tp in enclosingParams)
        {
            if (ownTypeParams.Contains(tp))
                continue;
            // If the type parameter is used in the body but not in parameters
            if (bodyIdentifiers.Contains(tp) && !paramsInParameters.Contains(tp))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Collects all IdentifierName text from a type syntax node, including
    /// the node itself if it is an IdentifierNameSyntax. This is needed
    /// because DescendantNodes() does not include the node itself.
    /// </summary>
    private static void CollectIdentifierNames(
        TypeSyntax? type, HashSet<string> names)
    {
        if (type == null)
            return;
        if (type is IdentifierNameSyntax id)
            names.Add(id.Identifier.Text);
        foreach (var descendant in type.DescendantNodes().OfType<IdentifierNameSyntax>())
            names.Add(descendant.Identifier.Text);
    }

    private string AssignName(
        LocalFunctionStatementSyntax localFn,
        TypeDeclarationSyntax hostType,
        IMethodSymbol symbol)
    {
        var baseName = LocalFunctionLiftNaming.GetBaseName(localFn.Identifier.Text);

        var typeSymbol = _model.GetDeclaredSymbol(hostType) as INamedTypeSymbol;
        var memberNames = new HashSet<string>(StringComparer.Ordinal);
        if (typeSymbol != null)
        {
            foreach (var member in typeSymbol.GetMembers())
                memberNames.Add(member.Name);
        }

        var typeKey = typeSymbol?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            ?? hostType.Identifier.Text;
        if (!_crossFileNameTable.TryGetValue(typeKey, out var usedNames))
        {
            usedNames = new HashSet<string>(StringComparer.Ordinal);
            _crossFileNameTable[typeKey] = usedNames;
        }

        var combined = new HashSet<string>(memberNames, StringComparer.Ordinal);
        foreach (var n in usedNames)
            combined.Add(n);

        // Collect local variable names in the enclosing method to avoid
        // naming conflicts when the hoisted method name shadows a local.
        foreach (var localName in GetEnclosingMethodLocalVariableNames(localFn))
            combined.Add(localName);

        var finalName = LocalFunctionLiftNaming.ResolveUniqueName(baseName, combined);
        usedNames.Add(finalName);

        return finalName;
    }

    /// <summary>
    /// Collects all local variable and local constant names declared in the
    /// enclosing method (between the method start and the local function),
    /// so that the hoisted method name doesn't shadow them.
    /// </summary>
    private static HashSet<string> GetEnclosingMethodLocalVariableNames(
        LocalFunctionStatementSyntax localFn)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is TypeDeclarationSyntax)
                break;
            if (current is BaseMethodDeclarationSyntax method)
            {
                CollectLocalVariableNames(method, names);
                break;
            }
            current = current.Parent;
        }
        return names;
    }

    private static void CollectLocalVariableNames(
        SyntaxNode node, HashSet<string> names)
    {
        foreach (var decl in node.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>())
        {
            names.Add(decl.Identifier.Text);
        }
        foreach (var param in node.DescendantNodes()
            .OfType<ParameterSyntax>())
        {
            names.Add(param.Identifier.Text);
        }
    }
}
