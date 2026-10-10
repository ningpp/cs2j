using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ArrayMethodRewriter;

internal sealed class ArrayMethodSyntaxRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _semanticModel;
    private readonly ArrayMethodRewriterOptions _options;
    private readonly ArrayMethodRewriterStatistics _stats;
    private readonly List<ArrayMethodRewriterDiagnostic> _diagnostics;

    // 改写目标成员名集合（用于脏检查快速路径）
    private static readonly HashSet<string> TargetMemberNames = new(StringComparer.Ordinal)
    {
        "Clone", "CopyTo", "GetValue", "SetValue",
        "GetLength", "GetLongLength", "GetLowerBound", "GetUpperBound",
        "LongLength", "Rank", "Length",
        "Sort", "BinarySearch", "Clear", "Resize",
        "IndexOf", "LastIndexOf", "Reverse",
        "Find", "FindAll", "FindIndex", "FindLast", "FindLastIndex",
        "Exists", "TrueForAll", "ConvertAll", "ForEach",
        "Copy", "ConstrainedCopy",
    };

    // 实例成员名 → ArrayHelper 方法名映射
    private static readonly HashSet<string> InstanceMemberNames = new(StringComparer.Ordinal)
    {
        "Clone", "CopyTo", "GetValue", "SetValue",
        "GetLength", "GetLongLength", "GetLowerBound", "GetUpperBound",
        "LongLength", "Rank",
    };

    // 静态方法名集合（§3.3 全表）
    private static readonly HashSet<string> StaticArrayMethodNames = new(StringComparer.Ordinal)
    {
        "Sort", "BinarySearch", "Clear", "Resize",
        "IndexOf", "LastIndexOf", "Reverse",
        "Find", "FindAll", "FindIndex", "FindLast", "FindLastIndex",
        "Exists", "TrueForAll", "ConvertAll", "ForEach",
        "Copy", "ConstrainedCopy",
    };

    public ArrayMethodSyntaxRewriter(
        SemanticModel? semanticModel,
        ArrayMethodRewriterOptions options,
        ArrayMethodRewriterStatistics stats,
        List<ArrayMethodRewriterDiagnostic> diagnostics)
    {
        _semanticModel = semanticModel;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// 脏检查（纯语法快速路径）：文件 token 中命中改写目标成员名集合，
    /// 或含 foreach 语句（多维摊平候选）。未命中即 clean，字节级保真复制。
    /// </summary>
    internal static bool IsDirty(CompilationUnitSyntax root)
    {
        foreach (var token in root.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.IdentifierToken)
                && TargetMemberNames.Contains(token.ValueText))
            {
                return true;
            }
        }

        // foreach 摊平候选
        return root.DescendantNodes().Any(n => n is ForEachStatementSyntax);
    }

    // ---- 辅助：类型判定 ----

    private bool IsArrayType(ExpressionSyntax expression)
    {
        if (_semanticModel == null) return false;
        var type = _semanticModel.GetTypeInfo(expression).Type;
        return type is IArrayTypeSymbol;
    }

    private IArrayTypeSymbol? GetArrayTypeSymbol(ExpressionSyntax expression)
    {
        if (_semanticModel == null) return null;
        return _semanticModel.GetTypeInfo(expression).Type as IArrayTypeSymbol;
    }

    private bool IsSystemArrayType(ExpressionSyntax expression)
    {
        if (_semanticModel == null) return false;
        var type = _semanticModel.GetTypeInfo(expression).Type;
        return type is INamedTypeSymbol named
            && named.SpecialType == SpecialType.System_Array;
    }

    private bool IsSystemArraySymbol(ExpressionSyntax expression)
    {
        if (_semanticModel == null) return false;
        var symbol = _semanticModel.GetSymbolInfo(expression).Symbol;
        return symbol is INamedTypeSymbol named
            && named.SpecialType == SpecialType.System_Array;
    }

    // ---- 辅助：构造 Cs2jArray.ArrayHelper.XXX(...) 调用 ----

    private static ExpressionSyntax StaticCall(string methodName, params ExpressionSyntax[] args)
    {
        var memberAccess = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.IdentifierName("Cs2jArray"),
            SyntaxFactory.IdentifierName("ArrayHelper"));
        var memberAccess2 = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            memberAccess,
            SyntaxFactory.IdentifierName(methodName));
        return SyntaxFactory.InvocationExpression(memberAccess2,
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(args.Select(a => SyntaxFactory.Argument(a)))));
    }

    private static ExpressionSyntax ParenthesizeIfNeeded(ExpressionSyntax expr)
    {
        // 防止 receiver 中的低优先级运算符被成员访问吞掉
        return expr is IdentifierNameSyntax
            or MemberAccessExpressionSyntax
            or InvocationExpressionSyntax
            or ElementAccessExpressionSyntax
            or LiteralExpressionSyntax
            or ParenthesizedExpressionSyntax
            ? expr
            : SyntaxFactory.ParenthesizedExpression(expr);
    }

    private ExpressionSyntax VisitAndStripLeading(ExpressionSyntax expr, out SyntaxTriviaList leading)
    {
        var visited = (ExpressionSyntax)Visit(expr)!;
        leading = visited.GetLeadingTrivia();
        return leading.Count == 0 ? visited : visited.WithoutLeadingTrivia();
    }

    private static ExpressionSyntax WithHoistedLeading(ExpressionSyntax replacement, params SyntaxTriviaList[] leads)
    {
        var total = default(SyntaxTriviaList);
        foreach (var lead in leads)
        {
            total = total.AddRange(lead);
        }
        return total.Count == 0 ? replacement : replacement.WithLeadingTrivia(total);
    }

    // ---- 实例成员改写（§3.1）----

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // 先递归访问子节点
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

        if (_semanticModel == null || !_options.EnableInstanceRewrite)
        {
            // 静态调用在无语义模型时也跳过
            return visited;
        }

        // 实例成员调用：receiver.Method(args)
        if (visited.Expression is MemberAccessExpressionSyntax mae)
        {
            var methodName = mae.Name.Identifier.ValueText;

            // 只处理实例成员名单中的方法名
            if (!InstanceMemberNames.Contains(methodName))
            {
                // 静态调用（Array.XXX）在单独路径处理
                var staticReplacement = TryRewriteStaticArrayCall(node, visited);
                if (staticReplacement != null)
                {
                    return staticReplacement;
                }
                return visited;
            }

            // receiver 必须是数组类型（IArrayTypeSymbol），排除 System.Array 接收者
            // 注意：语义模型查询必须使用原始节点 node.Expression 的 MemberAccess
            var originalMae = (MemberAccessExpressionSyntax)node.Expression!;
            if (IsSystemArrayType(originalMae.Expression) || !IsArrayType(originalMae.Expression))
            {
                // 也可能是 System.Array 静态调用
                var staticReplacement = TryRewriteStaticArrayCall(node, visited);
                if (staticReplacement != null)
                {
                    return staticReplacement;
                }
                return visited;
            }

            return RewriteInstanceMemberCall(node, originalMae, methodName, visited);
        }

        // 静态调用：Array.XXX(...)
        var staticCall = TryRewriteStaticArrayCall(node, visited);
        if (staticCall != null)
        {
            return staticCall;
        }

        return visited;
    }

    private ExpressionSyntax RewriteInstanceMemberCall(
        InvocationExpressionSyntax originalNode,
        MemberAccessExpressionSyntax originalMae,
        string methodName,
        InvocationExpressionSyntax visited)
    {
        // 获取原始 receiver 的语义信息（在原始树上查）
        var arrayType = GetArrayTypeSymbol(originalMae.Expression);
        if (arrayType == null) return visited;

        var receiver = VisitAndStripLeading(originalMae.Expression, out var receiverLead);
        var parenReceiver = ParenthesizeIfNeeded(receiver);
        var visitedArgs = originalNode.ArgumentList.Arguments
            .Select(a => (ExpressionSyntax)Visit(a.Expression)!).ToArray();

        ExpressionSyntax? replacement = methodName switch
        {
            "Clone" => StaticCall("Clone", parenReceiver),
            "CopyTo" => StaticCall("CopyTo", parenReceiver, visitedArgs[0], visitedArgs[1]),
            "GetValue" => RewriteGetValue(parenReceiver, visitedArgs, arrayType),
            "SetValue" => RewriteSetValue(parenReceiver, visitedArgs, arrayType),
            "GetLength" when visitedArgs.Length == 1 => StaticCall("GetLength", parenReceiver, visitedArgs[0]),
            "GetLongLength" when visitedArgs.Length == 1 => StaticCall("GetLongLength", parenReceiver, visitedArgs[0]),
            "GetLowerBound" => StaticCall("GetLowerBound", parenReceiver, visitedArgs[0]),
            "GetUpperBound" => StaticCall("GetUpperBound", parenReceiver, visitedArgs[0]),
            _ => null,
        };

        if (replacement == null) return visited;

        _stats.InstanceMemberRewrites++;
        return ArrayRewriteAnnotations.Mark(WithHoistedLeading(replacement, receiverLead));
    }

    private ExpressionSyntax RewriteGetValue(
        ExpressionSyntax receiver, ExpressionSyntax[] args, IArrayTypeSymbol arrayType)
    {
        // 按 rank 选择特化重载：rank 1 → GetValue<T>(T[], int)
        // rank 2 → GetValue<T>(T[][], int, int)
        // rank 3 → GetValue<T>(T[][][], int, int, int)
        // rank > 3 或 params 形态 → GetValue(System.Array, params int[])
        int rank = arrayType.Rank;
        var allArgs = new[] { receiver }.Concat(args).ToArray();
        return StaticCall("GetValue", allArgs);
    }

    private ExpressionSyntax RewriteSetValue(
        ExpressionSyntax receiver, ExpressionSyntax[] args, IArrayTypeSymbol arrayType)
    {
        // SetValue(value, index) / SetValue(value, i, j) / SetValue(value, params int[])
        var allArgs = new[] { receiver }.Concat(args).ToArray();
        return StaticCall("SetValue", allArgs);
    }

    // ---- 实例属性改写（§3.1: LongLength / Rank / 多维 Length）----

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;

        if (_semanticModel == null || !_options.EnableInstanceRewrite)
        {
            return visited;
        }

        var memberName = visited.Name.Identifier.ValueText;

        // 只处理实例属性名单中的名字
        if (memberName != "LongLength" && memberName != "Rank" && memberName != "Length")
        {
            return visited;
        }

        // receiver 必须是数组类型，排除 System.Array 接收者和 string.Length
        // 注意：语义模型查询必须使用原始节点 node.Expression，而不是 visited.Expression
        if (IsSystemArrayType(node.Expression) || !IsArrayType(node.Expression))
        {
            return visited;
        }

        var arrayType = GetArrayTypeSymbol(node.Expression);
        if (arrayType == null) return visited;

        // 一维 arr.Length 白名单，不改写
        if (memberName == "Length" && arrayType.Rank == 1)
        {
            return visited;
        }

        var receiver = VisitAndStripLeading(node.Expression, out var receiverLead);
        var parenReceiver = ParenthesizeIfNeeded(receiver);

        ExpressionSyntax? replacement = memberName switch
        {
            "Length" when arrayType.Rank > 1 => StaticCall("GetLength", parenReceiver), // 多维 .Length → GetLength(arr)（元素总数）
            "LongLength" => StaticCall("GetLongLength", parenReceiver),
            "Rank" => SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(arrayType.Rank)), // 内联常量
            _ => null,
        };

        if (replacement == null) return visited;

        if (memberName == "Rank")
        {
            _stats.RankInlined++;
        }
        else if (memberName == "Length")
        {
            _stats.MultiDimLengthRewrites++;
        }
        else
        {
            _stats.InstanceMemberRewrites++;
        }

        return ArrayRewriteAnnotations.Mark(WithHoistedLeading(replacement, receiverLead));
    }

    // ---- System.Array 静态调用改写（§3.3）----

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        var visited = (ExpressionStatementSyntax)base.VisitExpressionStatement(node)!;

        // Resize 语句改写：Array.Resize(ref a, n); → a = Cs2jArray.ArrayHelper.Resize(a, n);
        if (_semanticModel != null && _options.EnableStaticRewrite
            && visited.Expression is InvocationExpressionSyntax inv
            && inv.Expression is MemberAccessExpressionSyntax mae
            && mae.Name.Identifier.ValueText == "Resize"
            && IsSystemArraySymbol(mae.Expression))
        {
            var args = inv.ArgumentList.Arguments;
            if (args.Count == 2 && args[0].RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword))
            {
                var target = args[0].Expression;
                if (IsSafeAssignmentTarget(target))
                {
                    var newSize = (ExpressionSyntax)Visit(args[1].Expression)!;
                    var visitedTarget = VisitAndStripLeading(target, out var targetLead);
                    var call = StaticCall("Resize", visitedTarget, newSize);
                    var assignment = SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        visitedTarget,
                        call);
                    var markedExpr = ArrayRewriteAnnotations.Mark(
                        WithHoistedLeading(assignment, targetLead));
                    _stats.ResizeStatements++;
                    return visited.WithExpression(markedExpr);
                }
                else
                {
                    // 不满足安全条件 → 保守跳过 + Warning
                    _diagnostics.Add(new ArrayMethodRewriterDiagnostic(
                        ArrayMethodSeverity.Warning,
                        $"Array.Resize target is not a safe assignment target; skipped."));
                    return visited;
                }
            }
        }

        return visited;
    }

    private bool IsSafeAssignmentTarget(ExpressionSyntax target)
    {
        if (_semanticModel == null) return false;
        return target switch
        {
            IdentifierNameSyntax identifier =>
                _semanticModel.GetSymbolInfo(identifier).Symbol is ILocalSymbol
                    or IParameterSymbol
                    or IFieldSymbol,
            MemberAccessExpressionSyntax memberAccess =>
                _semanticModel.GetSymbolInfo(memberAccess).Symbol is IFieldSymbol
                && IsSideEffectFreeReceiver(memberAccess.Expression),
            _ => false,
        };
    }

    private static bool IsSideEffectFreeReceiver(ExpressionSyntax receiver) => receiver switch
    {
        ThisExpressionSyntax => true,
        BaseExpressionSyntax => true,
        IdentifierNameSyntax => true,
        MemberAccessExpressionSyntax memberAccess => IsSideEffectFreeReceiver(memberAccess.Expression),
        _ => false,
    };

    /// <summary>
    /// 改写 Array.XXX(...) 静态调用为 Cs2jArray.ArrayHelper.XXX(...)。
    /// 在 VisitInvocationExpression 中检测 Array 标识符是否解析到 System.Array。
    /// </summary>
    private ExpressionSyntax? TryRewriteStaticArrayCall(
        InvocationExpressionSyntax originalNode,
        InvocationExpressionSyntax visited)
    {
        if (_semanticModel == null || !_options.EnableStaticRewrite)
        {
            return null;
        }

        if (visited.Expression is not MemberAccessExpressionSyntax mae)
        {
            return null;
        }

        var methodName = mae.Name.Identifier.ValueText;

        // 只处理静态方法名单中的名字
        if (!StaticArrayMethodNames.Contains(methodName))
        {
            return null;
        }

        // Array 标识符必须解析到 System.Array 类型符号
        if (!IsSystemArraySymbol(mae.Expression))
        {
            return null;
        }

        // Resize 作为语句在 VisitExpressionStatement 中处理；如果在表达式上下文中
        // 出现（如 x = Array.Resize(...)），保守跳过
        if (methodName == "Resize")
        {
            return null;
        }

        // 实参原样透传（已在 base.VisitInvocationExpression 中递归访问过）
        var args = originalNode.ArgumentList.Arguments
            .Select(a => (ExpressionSyntax)Visit(a.Expression)!).ToArray();

        var replacement = StaticCall(methodName, args);
        _stats.StaticArrayCalls++;
        return ArrayRewriteAnnotations.Mark(replacement);
    }

    // ---- 多维 foreach 摊平（§3.2）----

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;

        if (_semanticModel == null || !_options.EnableForeachFlatten)
        {
            return visited;
        }

        // collection 表达式的类型必须是数组且 Rank > 1
        var collectionType = _semanticModel.GetTypeInfo(node.Expression).Type as IArrayTypeSymbol;
        if (collectionType == null || collectionType.Rank <= 1)
        {
            return visited;
        }

        // 合成行变量名，带冲突检查
        var rowVarName = MakeUniqueRowVariable(node);
        var rowVar = SyntaxFactory.IdentifierName(rowVarName);

        // 外层 foreach: foreach (var __cs2j_row0 in collection)
        var outerForEach = SyntaxFactory.ForEachStatement(
            SyntaxFactory.IdentifierName("var"),
            SyntaxFactory.Identifier(rowVarName),
            node.Expression,
            // 内层 foreach: foreach (var x in __cs2j_row0) { ... }
            SyntaxFactory.Block(
                SyntaxFactory.ForEachStatement(
                    SyntaxFactory.IdentifierName("var"),
                    node.Identifier,
                    rowVar,
                    node.Statement)));

        _stats.ForeachFlattened++;
        return ArrayRewriteAnnotations.Mark(outerForEach);
    }

    private string MakeUniqueRowVariable(ForEachStatementSyntax node)
    {
        // 收集作用域内已有的标识符名
        var takenNames = new HashSet<string>(StringComparer.Ordinal);
        var scope = node.Parent;
        while (scope != null)
        {
            foreach (var ident in scope.DescendantTokens()
                .Where(t => t.IsKind(SyntaxKind.IdentifierToken))
                .Select(t => t.ValueText))
            {
                takenNames.Add(ident);
            }
            scope = scope.Parent;
        }

        int suffix = 0;
        string name;
        do
        {
            name = ArrayRewriteAnnotations.RowVariablePrefix + suffix;
            suffix++;
        }
        while (takenNames.Contains(name));

        return name;
    }
}
