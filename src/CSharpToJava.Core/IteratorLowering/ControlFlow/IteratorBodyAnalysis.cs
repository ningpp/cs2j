using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.IteratorLowering.ControlFlow;

/// <summary>
/// 一个 foreach（词法上包含 yield）的降级信息：枚举器提升字段、
/// Current/MoveNext 形态与（若可 Dispose）关联的保护区域。
/// </summary>
internal sealed record ForeachLoweringInfo(
    bool IsArray,
    CaptureField? EnumeratorField,
    bool IsDisposable,
    bool IsEnumeratorValueType,
    string CurrentMemberName,
    string? GetEnumeratorMethodName,
    string ItemFieldName,
    RegionPlan? Region);

/// <summary>
/// 单个迭代器成员的完整词法分析：挂起状态编号、提升符号表、
/// foreach 降级表、保护区域表。Analyzer 与 Rewriter 共用。
/// </summary>
internal sealed class MethodLoweringInfo
{
    public required MethodDeclarationSyntax Method { get; init; }
    public required StateMachinePlan Plan { get; init; }

    /// <summary>按语法序的 yield return；其挂起状态号 = 下标 + 1。</summary>
    public required IReadOnlyList<YieldStatementSyntax> YieldReturns { get; init; }

    /// <summary>被提升的参数/局部符号 → 状态机字段。</summary>
    public required IReadOnlyDictionary<ISymbol, CaptureField> HoistedSymbols { get; init; }

    /// <summary>foreach 节点 → 降级信息（仅含词法上包裹 yield 的 foreach）。</summary>
    public required IReadOnlyDictionary<ForEachStatementSyntax, ForeachLoweringInfo> Foreaches { get; init; }

    /// <summary>活动保护区域节点（try-finally / 可释放 foreach 的合成 try）→ 区域。</summary>
    public required IReadOnlyDictionary<StatementSyntax, RegionPlan> RegionByNode { get; init; }

    /// <summary>foreach 节点 → 区域（与 <see cref="RegionByNode"/> 重复登记，便于查询）。</summary>
    public required IReadOnlyDictionary<ForEachStatementSyntax, RegionPlan> ForeachRegions { get; init; }
}

/// <summary>
/// 结构化扫描：沿词法结构做状态编号、基于段的活性提升判定、
/// foreach 枚举器提升与保护区域建树。不做任意基本块 flatten。
/// </summary>
internal static class IteratorBodyAnalysis
{
    public sealed record LegalityIssue(IteratorLoweringDiagnosticCode Code, string Message);

    public static MethodLoweringInfo Analyze(
        MethodDeclarationSyntax method,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        string stateMachineClassName)
    {
        var body = method.Body!;
        var compilation = semanticModel.Compilation;

        // ── 1. 状态编号：语法序 yield return 1..N ──
        var yieldReturns = body.DescendantNodes()
            .OfType<YieldStatementSyntax>()
            .Where(y => y.IsKind(SyntaxKind.YieldReturnStatement))
            .ToList();
        var hasYieldBreak = body.DescendantNodes()
            .OfType<YieldStatementSyntax>()
            .Any(y => y.IsKind(SyntaxKind.YieldBreakStatement));
        var yieldStateByNode = new Dictionary<YieldStatementSyntax, int>();
        for (var i = 0; i < yieldReturns.Count; i++)
        {
            yieldStateByNode[yieldReturns[i]] = i + 1;
        }

        // ── 2. 收集引用节点 → 符号 与 局部声明 ──
        var referenceSpans = new Dictionary<ISymbol, List<int>>(SymbolEqualityComparer.Default);
        void TrackReference(SyntaxNode node, ISymbol? symbol)
        {
            if (symbol is not (IParameterSymbol or ILocalSymbol)) return;
            if (!referenceSpans.TryGetValue(symbol, out var spans))
            {
                spans = new List<int>();
                referenceSpans[symbol] = spans;
            }
            spans.Add(node.SpanStart);
        }

        var declaredLocals = new List<(ILocalSymbol Symbol, int DeclSpan, SyntaxNode DeclNode)>();
        foreach (var node in body.DescendantNodes())
        {
            switch (node)
            {
                case IdentifierNameSyntax id:
                    TrackReference(id, semanticModel.GetSymbolInfo(id).Symbol);
                    break;
                case VariableDeclaratorSyntax declarator when declarator.Parent is VariableDeclarationSyntax { Parent: LocalDeclarationStatementSyntax or ForStatementSyntax or FixedStatementSyntax }:
                    if (semanticModel.GetDeclaredSymbol(declarator) is ILocalSymbol localFromDecl)
                    {
                        declaredLocals.Add((localFromDecl, declarator.SpanStart, declarator));
                    }
                    break;
            }
        }

        // ── 3. 活动 foreach 与枚举器提升 ──
        var idisposable = compilation.GetSpecialType(SpecialType.System_IDisposable);
        var activeForeachNodes = body.DescendantNodes()
            .OfType<ForEachStatementSyntax>()
            .Where(ContainsYieldReturn)
            .ToList();

        var hoisted = new Dictionary<ISymbol, CaptureField>(SymbolEqualityComparer.Default);
        var parameterFields = new List<CaptureField>();
        var localFields = new List<CaptureField>();
        var usedFieldNames = new HashSet<string>(StringComparer.Ordinal)
        {
            IteratorLoweringNaming.StateField,
            IteratorLoweringNaming.CurrentField,
            IteratorLoweringNaming.InitialThreadIdField,
            IteratorLoweringNaming.ThisField,
        };

        CaptureField AddField(ISymbol symbol, string originalName, ITypeSymbol type, string desiredName)
        {
            var fieldName = desiredName;
            if (!usedFieldNames.Add(fieldName))
            {
                var suffix = 2;
                while (!usedFieldNames.Add(fieldName + "_" + suffix)) suffix++;
                fieldName += "_" + suffix;
            }
            var field = new CaptureField(originalName, type.ToDisplayString(), fieldName);
            hoisted[symbol] = field;
            if (symbol is IParameterSymbol)
            {
                parameterFields.Add(field);
            }
            else
            {
                localFields.Add(field);
            }
            return field;
        }

        // 形参：体内有引用即提升
        foreach (var param in methodSymbol.Parameters)
        {
            if (referenceSpans.ContainsKey(param))
            {
                AddField(param, param.Name, param.Type, IteratorLoweringNaming.GetCaptureFieldName(param.Name));
            }
        }

        // 活动 try-finally 区域（区域建树用）
        var activeTryNodes = body.DescendantNodes()
            .OfType<TryStatementSyntax>()
            .Where(t => t.Finally != null
                && t.Block.DescendantNodes().OfType<YieldStatementSyntax>()
                    .Any(y => y.IsKind(SyntaxKind.YieldReturnStatement)))
            .ToList();

        // foreach 迭代变量提升
        var foreachInfos = new Dictionary<ForEachStatementSyntax, ForeachLoweringInfo>();
        var foreachRegions = new Dictionary<ForEachStatementSyntax, RegionPlan>();
        var enumeratorIndex = 0;
        foreach (var fe in activeForeachNodes)
        {
            var itemSymbol = semanticModel.GetDeclaredSymbol(fe);
            var feInfo = semanticModel.GetForEachStatementInfo(fe);
            var itemFieldName = IteratorLoweringNaming.GetLocalFieldName(fe.Identifier.ValueText);
            CaptureField? itemField = null;
            if (itemSymbol is ILocalSymbol itemLocal)
            {
                itemField = AddField(
                    itemLocal,
                    fe.Identifier.ValueText,
                    feInfo.ElementType ?? itemLocal.Type,
                    itemFieldName);
            }

            // 注意：部分 Roslyn 版本对数组 foreach 也会报告非泛型 IEnumerable 模式
            // （GetEnumeratorMethod 非空），因此数组判定以表达式类型为准。
            var expressionType = semanticModel.GetTypeInfo(fe.Expression).Type;
            var isArray = expressionType is IArrayTypeSymbol;
            CaptureField? enumField = null;
            var isDisposable = false;
            var isEnumeratorValueType = false;
            string? getEnumeratorName = null;
            var currentName = "Current";

            if (isArray)
            {
                var arrayType = feInfo.ElementType != null
                    ? compilation.CreateArrayTypeSymbol(feInfo.ElementType)
                    : semanticModel.GetTypeInfo(fe.Expression).Type;
                if (arrayType != null)
                {
                    enumField = new CaptureField(
                        "array" + enumeratorIndex,
                        arrayType.ToDisplayString(),
                        IteratorLoweringNaming.GetEnumeratorFieldName(enumeratorIndex));
                    enumeratorIndex++;
                }
            }
            else
            {
                var enumType = feInfo.GetEnumeratorMethod!.ReturnType;
                enumField = new CaptureField(
                    "enumerator" + enumeratorIndex,
                    enumType.ToDisplayString(),
                    IteratorLoweringNaming.GetEnumeratorFieldName(enumeratorIndex));
                enumeratorIndex++;
                isDisposable = compilation.ClassifyConversion(enumType, idisposable).IsImplicit;
                isEnumeratorValueType = enumType.IsValueType;
                getEnumeratorName = feInfo.GetEnumeratorMethod.Name;
                if (feInfo.CurrentProperty != null)
                {
                    currentName = feInfo.CurrentProperty.Name;
                }
            }

            foreachInfos[fe] = new ForeachLoweringInfo(
                isArray,
                enumField,
                isDisposable,
                isEnumeratorValueType,
                currentName,
                getEnumeratorName,
                itemField?.FieldName ?? itemFieldName,
                Region: null);
        }

        // ── 4. 局部提升：常规局部全部提升为字段。
        // MoveNext 体被扁平化为单层 goto 控制流，标签间跳转可能跨越原局部作用域，
        // 因此不做活性裁剪（foreach 迭代变量已在上方登记）。
        // M4 例外：ref struct/ref local/指针局部永远不能成为 class 字段
        // （CS4007/CS9217 族）；不跨 yield 边界的保留为 MoveNext 真局部
        // （analyzer 已保证跨边界成员整体 skip，此处再不济也不发射非法字段）。
        foreach (var (symbol, _, _) in declaredLocals)
        {
            if (hoisted.ContainsKey(symbol)) continue;
            if (IteratorLocalEscaping.IsRestricted(symbol)) continue;
            AddField(
                symbol,
                symbol.Name,
                symbol.Type,
                IteratorLoweringNaming.GetLocalFieldName(symbol.Name));
        }

        // ── 5. 保护区域建树（pre-order，父节点 = 最近活动区域） ──
        var regionByNode = new Dictionary<StatementSyntax, RegionPlan>();
        var regions = new List<RegionPlan>();
        var regionId = 0;

        int? FindEnclosingRegion(SyntaxNode node)
        {
            var cursor = node.Parent;
            while (cursor != null && cursor != body)
            {
                if (cursor is StatementSyntax s && regionByNode.TryGetValue(s, out var r))
                {
                    return r.Id;
                }
                cursor = cursor.Parent;
            }
            return null;
        }

        foreach (var tryNode in activeTryNodes)
        {
            var states = YieldStatesWithin(tryNode.Block, yieldStateByNode);
            var region = new RegionPlan(
                regionId++,
                FindEnclosingRegion(tryNode),
                RegionKind.UserFinally,
                states.First(),
                states.Last(),
                EnumeratorField: null);
            regions.Add(region);
            regionByNode[tryNode] = region;
        }

        foreach (var fe in activeForeachNodes)
        {
            var info = foreachInfos[fe];
            if (!info.IsArray && info.IsDisposable)
            {
                var states = YieldStatesWithin(fe.Statement, yieldStateByNode);
                var region = new RegionPlan(
                    regionId++,
                    FindEnclosingRegion(fe),
                    RegionKind.ForeachDispose,
                    states.First(),
                    states.Last(),
                    info.EnumeratorField);
                regions.Add(region);
                regionByNode[fe] = region;
                foreachRegions[fe] = region;
                foreachInfos[fe] = info with { Region = region };
            }
        }

        // ── 6. 组装 plan ──
        var declaringType = methodSymbol.ContainingType;
        var thisCaptureType = methodSymbol.IsStatic
            ? declaringType.ToDisplayString()
            : BuildThisCaptureType(declaringType);

        var plan = new StateMachinePlan(
            MemberName: methodSymbol.Name,
            StateMachineClassName: stateMachineClassName,
            ElementType: ResolveElementType(methodSymbol.ReturnType),
            IsIEnumerableReturn: IsIEnumerableReturn(methodSymbol.ReturnType),
            IsIEnumeratorReturn: IsIEnumeratorReturn(methodSymbol.ReturnType),
            IsNonGenericReturn: IsNonGenericIteratorReturn(methodSymbol.ReturnType),
            IsGenericMethod: methodSymbol.IsGenericMethod,
            DeclaringTypeName: declaringType.Name,
            ThisCaptureType: thisCaptureType,
            IsStatic: methodSymbol.IsStatic,
            Parameters: parameterFields,
            HoistedLocals: localFields,
            HoistedEnumerators: foreachInfos.Values
                .Where(i => i.EnumeratorField != null)
                .Select(i => i.EnumeratorField!)
                .ToList(),
            Regions: regions,
            YieldReturnCount: yieldReturns.Count,
            HasYieldBreak: hasYieldBreak);

        return new MethodLoweringInfo
        {
            Method = method,
            Plan = plan,
            YieldReturns = yieldReturns,
            HoistedSymbols = hoisted,
            Foreaches = foreachInfos,
            RegionByNode = regionByNode,
            ForeachRegions = foreachRegions,
        };
    }

    private static List<int> YieldStatesWithin(
        SyntaxNode scope,
        IReadOnlyDictionary<YieldStatementSyntax, int> yieldStateByNode)
    {
        var result = new List<int>();
        // 作用域节点自身也可能就是 yield（无花括号 foreach: foreach (...) yield return x;）。
        foreach (var descendant in scope.DescendantNodesAndSelf())
        {
            if (descendant is YieldStatementSyntax y
                && y.IsKind(SyntaxKind.YieldReturnStatement)
                && yieldStateByNode.TryGetValue(y, out var state))
            {
                result.Add(state);
            }
        }
        result.Sort();
        return result;
    }

    private static string BuildThisCaptureType(INamedTypeSymbol declaringType)
    {
        if (!declaringType.IsGenericType)
        {
            return declaringType.ToDisplayString();
        }

        // 状态机是声明类型的嵌套类，引用外层类型用其类型参数（C<T1, T2>）。
        var name = declaringType.Name;
        var args = string.Join(", ", declaringType.TypeParameters.Select(p => p.Name));
        return $"{name}<{args}>";
    }

    private static string ResolveElementType(ITypeSymbol returnType)
    {
        if (returnType is INamedTypeSymbol named
            && named.OriginalDefinition.ToDisplayString() is "System.Collections.Generic.IEnumerable<T>"
                or "System.Collections.Generic.IEnumerator<T>")
        {
            return named.TypeArguments[0].ToDisplayString();
        }
        return "object";
    }

    private static bool IsIEnumerableReturn(ITypeSymbol returnType) =>
        returnType.OriginalDefinition.ToDisplayString() is "System.Collections.Generic.IEnumerable<T>"
            or "System.Collections.IEnumerable";

    private static bool IsIEnumeratorReturn(ITypeSymbol returnType) =>
        returnType.OriginalDefinition.ToDisplayString() is "System.Collections.Generic.IEnumerator<T>"
            or "System.Collections.IEnumerator";

    private static bool IsNonGenericIteratorReturn(ITypeSymbol returnType) =>
        returnType.ToDisplayString() is "System.Collections.IEnumerable"
            or "System.Collections.IEnumerator";

    public static bool ContainsYieldReturn(SyntaxNode node)
        => node.DescendantNodes().Any(n => n.IsKind(SyntaxKind.YieldReturnStatement));
}
