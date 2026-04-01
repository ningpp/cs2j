using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers;
using CSharpToJava.Core.Transformers.Expression;
using CSharpToJava.Core.Transformers.Expression.Utilities;

namespace CSharpToJava.Core.Transformers.Statement;

/// <summary>
/// 语句转换器
/// </summary>
public class StatementTransformer : IStatementTransformer
{
    public JavaSyntaxNode Transform(StatementSyntax node, ConversionContext context)
    {
        return node.Kind() switch
        {
            SyntaxKind.Block => new JavaStatementNode(TransformBlock(node as BlockSyntax, context)),
            SyntaxKind.ExpressionStatement => TransformExpressionStatement(node as ExpressionStatementSyntax, context),
            SyntaxKind.ReturnStatement => TransformReturnStatement(node as ReturnStatementSyntax, context),
            SyntaxKind.ThrowStatement => TransformThrowStatement(node as ThrowStatementSyntax, context),
            SyntaxKind.IfStatement => TransformIfStatement(node as IfStatementSyntax, context),
            SyntaxKind.WhileStatement => TransformWhileStatement(node as WhileStatementSyntax, context),
            SyntaxKind.ForStatement => TransformForStatement(node as ForStatementSyntax, context),
            SyntaxKind.ForEachStatement => TransformForEachStatement(node as ForEachStatementSyntax, context),
            SyntaxKind.DoStatement => TransformDoStatement(node as DoStatementSyntax, context),
            SyntaxKind.SwitchStatement => TransformSwitchStatement(node as SwitchStatementSyntax, context),
            SyntaxKind.TryStatement => TransformTryStatement(node as TryStatementSyntax, context),
            SyntaxKind.UsingStatement => TransformUsingStatement(node as UsingStatementSyntax, context),
            SyntaxKind.BreakStatement => new JavaStatementNode("break;"),
            SyntaxKind.ContinueStatement => new JavaStatementNode("continue;"),
            SyntaxKind.YieldReturnStatement => TransformYieldReturn(node as YieldStatementSyntax, context),
            SyntaxKind.YieldBreakStatement => TransformYieldBreak(node as YieldStatementSyntax, context),
            SyntaxKind.LockStatement => TransformLockStatement(node as LockStatementSyntax, context),
            SyntaxKind.FixedStatement => TransformFixedStatement(node as FixedStatementSyntax, context),
            SyntaxKind.UnsafeStatement => TransformUnsafeStatement(node as UnsafeStatementSyntax, context),
            SyntaxKind.EmptyStatement => new JavaStatementNode(""),
            SyntaxKind.LocalDeclarationStatement => TransformLocalDeclaration(node as LocalDeclarationStatementSyntax, context),
            SyntaxKind.CheckedStatement => TransformCheckedStatement(node as CheckedStatementSyntax, context),
            SyntaxKind.UncheckedStatement => TransformUncheckedStatement(node as CheckedStatementSyntax, context),
            _ => new JavaStatementNode($"/* TODO: {node.Kind()} - {node} */")
        };
    }

    public string TransformBlock(BlockSyntax block, ConversionContext context)
    {
        if (block == null) return "{}";

        var statements = TransformStatements(block.Statements, context);
        return string.Join("\n        ", statements);
    }

    /// <summary>
    /// 将块语法转换为结构化 JavaMethodBody（使用 JavaRawStatement 包装现有字符串输出）。
    /// 这是从字符串输出过渡到结构化 IR 的桥梁方法。
    /// </summary>
    public Java.JavaMethodBody TransformBlockToStructuredBody(BlockSyntax block, ConversionContext context)
    {
        var body = new Java.JavaMethodBody();
        if (block == null) return body;

        var statements = TransformStatements(block.Statements, context);
        foreach (var stmt in statements)
        {
            body.Statements.Add(new Java.JavaRawStatement(stmt));
        }
        return body;
    }

    public List<string> TransformStatements(SyntaxList<StatementSyntax> statements, ConversionContext context)
    {
        var results = new List<string>();

        foreach (var statement in statements)
        {
            var result = Transform(statement, context);
            if (result is JavaMemberCollection collection)
            {
                foreach (var member in collection.Members)
                {
                    var memberText = member.ToString("");
                    if (!string.IsNullOrWhiteSpace(memberText) &&
                        !memberText.TrimStart().StartsWith("#") &&
                        !memberText.TrimStart().StartsWith("/* TODO: UncheckedStatement"))
                    {
                        results.Add(AttachStatementComments(statement, memberText));
                    }
                }
            }
            else if (result is JavaStatementNode stmt)
            {
                var stmtText = stmt.ToString("");
                // 过滤掉空语句和 C# 预处理器指令残留
                if (!string.IsNullOrWhiteSpace(stmtText) &&
                    !stmtText.TrimStart().StartsWith("#") &&
                    !stmtText.TrimStart().StartsWith("/* TODO: UncheckedStatement"))
                {
                    results.Add(AttachStatementComments(statement, stmtText));
                }
            }
        }

        return results;
    }

    private static string AttachStatementComments(StatementSyntax statement, string statementText)
    {
        var leadingComments = CommentConversion.ExtractStatementLeadingComments(statement);
        var trailingComments = CommentConversion.ExtractStatementTrailingComments(statement);
        var result = statementText;

        if (!string.IsNullOrWhiteSpace(leadingComments))
            result = leadingComments + "\n" + result;

        if (!string.IsNullOrWhiteSpace(trailingComments))
        {
            result = trailingComments.Contains('\n')
                ? result + "\n" + trailingComments
                : result + " " + trailingComments;
        }

        return result;
    }

    private JavaSyntaxNode TransformExpressionStatement(ExpressionStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;

        // ConditionalAccessExpression (obj?.Method()) as a statement cannot be a ternary expression in Java.
        // Java only allows method calls, assignments, and new as expression statements.
        // Convert to: if (obj != null) { obj.Method(); }
        if (stmt.Expression is ConditionalAccessExpressionSyntax condAccess)
        {
            var objExpr = exprTransformer.Transform(condAccess.Expression, context);
            string innerCall;
            switch (condAccess.WhenNotNull)
            {
                case MemberBindingExpressionSyntax binding:
                    innerCall = $"{objExpr}.{ConversionContext.EscapeJavaKeyword(binding.Name.Identifier.Text)};";
                    break;
                case InvocationExpressionSyntax invocation when invocation.Expression is MemberBindingExpressionSyntax invokeBinding:
                    var args = string.Join(", ", invocation.ArgumentList.Arguments.Select(a => exprTransformer.Transform(a.Expression, context)));
                    innerCall = $"{objExpr}.{ConversionContext.EscapeJavaKeyword(invokeBinding.Name.Identifier.Text)}({args});";
                    break;
                default:
                    // General case: ?.a.b(...) — recursively substitute the member binding with objExpr
                    innerCall = exprTransformer.TransformWhenNotNull(condAccess.WhenNotNull, objExpr, context) + ";";
                    break;
            }
            return new JavaStatementNode($"if ({objExpr} != null) {{ {innerCall} }}");
        }

        // Special case: dict.TryGetValue(key, out var v) as a standalone statement → v = dict.get(key);
        // For value types (structs/primitives), use getOrDefault to avoid NPE since C# TryGetValue
        // default-initializes the out parameter when the key is not found.
        if (stmt.Expression is InvocationExpressionSyntax tvInvoc &&
            tvInvoc.Expression is MemberAccessExpressionSyntax tvMa &&
            tvMa.Name.Identifier.Text == "TryGetValue" &&
            tvInvoc.ArgumentList.Arguments.Count == 2)
        {
            var tvTarget = exprTransformer.Transform(tvMa.Expression, context);
            var tvKey = exprTransformer.Transform(tvInvoc.ArgumentList.Arguments[0].Expression, context);
            var tvArg2 = tvInvoc.ArgumentList.Arguments[1];
            if (tvArg2.Expression is DeclarationExpressionSyntax tvDecl2)
            {
                var declType = context.SemanticModel?.GetTypeInfo(tvDecl2.Type);
                var javaType = declType.HasValue && declType.Value.Type != null ? context.MapType(declType.Value.Type) : "var";
                var varName = tvDecl2.Designation is SingleVariableDesignationSyntax sv ? sv.Identifier.Text : "_outVar";
                var defaultVal = GetValueTypeDefault(declType?.Type, javaType);
                var getCall = defaultVal != null
                    ? $"{tvTarget}.getOrDefault({tvKey}, {defaultVal})"
                    : $"{tvTarget}.get({tvKey})";
                return new JavaStatementNode($"{javaType} {varName} = {getCall};");
            }
            else
            {
                var tvOut2 = exprTransformer.Transform(tvArg2.Expression, context);
                var outTypeInfo = context.SemanticModel?.GetTypeInfo(tvArg2.Expression);
                string? defaultVal = null;
                if (outTypeInfo?.Type is { IsValueType: true } outType)
                {
                    defaultVal = GetValueTypeDefault(outType, context.MapType(outType));
                }
                var getCall = defaultVal != null
                    ? $"{tvTarget}.getOrDefault({tvKey}, {defaultVal})"
                    : $"{tvTarget}.get({tvKey})";
                return new JavaStatementNode($"{tvOut2} = {getCall};");
            }
        }

        // Array.Sort(keys, items) overload: sort keys and reorder items accordingly.
        if (stmt.Expression is InvocationExpressionSyntax arrSortInv
            && arrSortInv.Expression is MemberAccessExpressionSyntax arrSortMa
            && arrSortMa.Name.Identifier.Text == "Sort"
            && arrSortInv.ArgumentList.Arguments.Count == 2)
        {
            var receiverText = arrSortMa.Expression.ToString();
            bool isArrayReceiver = receiverText is "Array" or "System.Array";
            if (isArrayReceiver && context.SemanticModel != null)
            {
                var arg0Expr = arrSortInv.ArgumentList.Arguments[0].Expression;
                var arg1Expr = arrSortInv.ArgumentList.Arguments[1].Expression;
                var keyArr = context.SemanticModel.GetTypeInfo(arg0Expr).Type as IArrayTypeSymbol;
                var itemArr = context.SemanticModel.GetTypeInfo(arg1Expr).Type as IArrayTypeSymbol;

                bool keyIsNumeric = keyArr?.ElementType.SpecialType is
                    SpecialType.System_Byte or SpecialType.System_SByte
                    or SpecialType.System_Int16 or SpecialType.System_UInt16
                    or SpecialType.System_Int32 or SpecialType.System_UInt32
                    or SpecialType.System_Int64 or SpecialType.System_UInt64
                    or SpecialType.System_Single or SpecialType.System_Double
                    or SpecialType.System_Decimal;

                if (keyArr != null && itemArr != null && keyIsNumeric)
                {
                    var keysExpr = exprTransformer.Transform(arg0Expr, context);
                    var itemsExpr = exprTransformer.Transform(arg1Expr, context);
                    var keyElemType = context.MapType(keyArr.ElementType);
                    var itemElemType = context.MapType(itemArr.ElementType);

                    context.AddImport("java.util.Arrays");
                    context.AddImport("java.util.Comparator");
                    context.AddImport("java.util.stream.IntStream");

                    var idxName = context.GenerateSyntheticName("_sortIdx");
                    var keyCopyName = context.GenerateSyntheticName("_keyCopy");
                    var itemCopyName = context.GenerateSyntheticName("_itemCopy");

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"Integer[] {idxName} = IntStream.range(0, {keysExpr}.length).boxed().toArray(Integer[]::new);");
                    sb.AppendLine($"Arrays.sort({idxName}, Comparator.comparingDouble(i -> (double){keysExpr}[i]));");
                    sb.AppendLine($"{keyElemType}[] {keyCopyName} = {keysExpr}.clone();");
                    sb.AppendLine($"{itemElemType}[] {itemCopyName} = {itemsExpr}.clone();");
                    sb.Append($"for (int i = 0; i < {idxName}.length; i++) {{ {keysExpr}[i] = {keyCopyName}[{idxName}[i]]; {itemsExpr}[i] = {itemCopyName}[{idxName}[i]]; }}");
                    return new JavaStatementNode(sb.ToString());
                }
            }
        }

        // Special case: Debug.Assert / Trace.Assert / Contract.Requires / Contract.Assert
        // Java's 'assert' is a statement keyword, not a callable method — emit it directly
        // so the call never reaches the generic name-lowering + EscapeJavaKeyword path that
        // would produce the wrong "System.assertValue(...)" output.
        if (stmt.Expression is InvocationExpressionSyntax assertInvoc &&
            assertInvoc.Expression is MemberAccessExpressionSyntax assertMa &&
            assertInvoc.ArgumentList.Arguments.Count >= 1 &&
            assertMa.Name.Identifier.Text is "Assert" or "Requires")
        {
            bool isAssertLike = false;
            if (context.SemanticModel != null &&
                context.SemanticModel.GetSymbolInfo(assertInvoc).Symbol is IMethodSymbol assertSym)
            {
                var typeName = assertSym.ContainingType.ToDisplayString();
                isAssertLike = typeName is "System.Diagnostics.Debug"
                                         or "System.Diagnostics.Trace"
                                         or "System.Diagnostics.Contracts.Contract";
            }
            if (!isAssertLike)
            {
                // Syntactic fallback: semantic model absent or couldn't resolve the symbol
                var receiver = assertMa.Expression.ToString();
                isAssertLike = receiver is "Debug" or "Trace" or "Contract"
                                         or "System.Diagnostics.Debug"
                                         or "System.Diagnostics.Trace"
                                         or "System.Diagnostics.Contracts.Contract";
            }

            if (isAssertLike)
            {
                var condition = exprTransformer.Transform(assertInvoc.ArgumentList.Arguments[0].Expression, context);
                string assertStmt;
                if (assertInvoc.ArgumentList.Arguments.Count >= 2)
                {
                    var message = exprTransformer.Transform(assertInvoc.ArgumentList.Arguments[1].Expression, context);
                    assertStmt = $"assert {condition} : {message};";
                }
                else
                {
                    assertStmt = $"assert {condition};";
                }

                // Drain any pre/post statements emitted during condition/message transformation
                // (e.g., ref/out Holder declarations must precede the assert statement)
                if (context.HasPendingPreStatements || context.HasPendingPostStatements)
                {
                    var sb = new System.Text.StringBuilder();
                    if (context.HasPendingPreStatements)
                    {
                        var preStmts = context.DrainPreStatements();
                        sb.AppendLine(string.Join("\n", preStmts.Select(s => s.TrimEnd(';') + ";")));
                    }
                    sb.Append(assertStmt);
                    if (context.HasPendingPostStatements)
                    {
                        var postStmts = context.DrainPostStatements();
                        sb.Append("\n" + string.Join("\n", postStmts.Select(s => s.TrimEnd(';') + ";")));
                    }
                    return new JavaStatementNode(sb.ToString());
                }

                return new JavaStatementNode(assertStmt);
            }
        }

        // Fix 4: Tuple deconstruction — var (first, second) = GetPair();
        // ExpressionStatement > AssignmentExpression where LHS is DeclarationExpression with ParenthesizedVariableDesignation
        if (stmt.Expression is AssignmentExpressionSyntax tupleAssign
            && tupleAssign.Left is DeclarationExpressionSyntax tupleDecl
            && tupleDecl.Designation is ParenthesizedVariableDesignationSyntax parenDesig)
        {
            var rhsExpr = exprTransformer.Transform(tupleAssign.Right, context);
            var tempVar = "_t";
            var names = parenDesig.Variables
                .OfType<SingleVariableDesignationSyntax>()
                .Select(svd => svd.Identifier.Text)
                .ToList();
            var sbTuple = new System.Text.StringBuilder();
            sbTuple.AppendLine($"var {tempVar} = {rhsExpr};");
            for (int i = 0; i < names.Count; i++)
            {
                string getter = i == 0 ? "getFirst" : i == 1 ? "getSecond" : $"getItem{i + 1}";
                if (i < names.Count - 1)
                    sbTuple.AppendLine($"var {ConversionContext.EscapeJavaKeyword(names[i])} = {tempVar}.{getter}();");
                else
                    sbTuple.Append($"var {ConversionContext.EscapeJavaKeyword(names[i])} = {tempVar}.{getter}();");
            }
            return new JavaStatementNode(sbTuple.ToString());
        }

        var expr = exprTransformer.Transform(stmt.Expression, context);

        // Drain any pre-statements emitted by the expression transformer
        // (e.g., chain property assignment: list.Add(obj.Prop = local = expr) splits into pre-stmts)
        // Also drain post-statements (e.g., out-param holder reads back into variables after the call)
        if (context.HasPendingPreStatements)
        {
            var preStmts = context.DrainPreStatements();
            var preStmtLines = string.Join("\n", preStmts.Select(s => s.TrimEnd(';') + ";"));
            // If the main expression is just a simple variable or an out-param holder field access
            // (from chain-assignment hoisting), skip it as a statement — e.g. "anchors.value;" is
            // not valid Java. Valid Java statements must be method calls, assignments, etc.
            bool isNonStatement = expr.All(c => char.IsLetterOrDigit(c) || c == '_')
                || expr.EndsWith(".value");  // out-param XHolder field access (e.g. anchors.value)
            string stmtBlock = isNonStatement ? preStmtLines : preStmtLines + "\n" + expr + ";";
            if (context.HasPendingPostStatements)
            {
                var postStmts = context.DrainPostStatements();
                stmtBlock += "\n" + string.Join("\n", postStmts.Select(s => s.TrimEnd(';') + ";"));
            }
            return new JavaStatementNode(stmtBlock);
        }

        if (context.HasPendingPostStatements)
        {
            var postStmts = context.DrainPostStatements();
            var postStmtLines = string.Join("\n", postStmts.Select(s => s + ";"));
            return new JavaStatementNode(expr + ";\n" + postStmtLines);
        }

        return new JavaStatementNode(expr + ";");
    }

    private JavaSyntaxNode TransformThrowStatement(ThrowStatementSyntax stmt, ConversionContext context)
    {
        if (stmt.Expression == null)
        {
            // Java does not support bare 'throw;' — rethrow the enclosing catch variable
            // Fix 2: Stop ancestor walk at lambda boundaries to avoid crossing exception scope
            var catchVar = stmt.Ancestors()
                .TakeWhile(a => a is not AnonymousFunctionExpressionSyntax)
                .OfType<CatchClauseSyntax>()
                .FirstOrDefault();
            if (catchVar == null)
                return new JavaStatementNode("throw; /* TODO: rethrow outside catch - manual conversion required */");
            var rethrowName = catchVar.Declaration?.Identifier.ValueText;
            if (string.IsNullOrWhiteSpace(rethrowName)) rethrowName = "_ex";
            return new JavaStatementNode($"throw {ConversionContext.EscapeJavaKeyword(rethrowName.Trim())};");
        }
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var expr = exprTransformer.Transform(stmt.Expression, context);
        return new JavaStatementNode($"throw {expr};");
    }

    private JavaSyntaxNode TransformReturnStatement(ReturnStatementSyntax stmt, ConversionContext context)
    {
        if (stmt.Expression == null)
        {
            return new JavaStatementNode("return;");
        }

        var exprTransformer = ExpressionTransformerFacade.Instance;
        var expr = exprTransformer.Transform(stmt.Expression, context);
        var returnTargetType = ResolveReturnTargetType(stmt, context);

        // Detect when a C# array element (from jagged array) is returned where a List<T> is expected.
        // e.g., return outEdges[vertex]; where the method returns IList<TEdge> → List<TEdge> in Java
        // and outEdges is TEdge[][] — element is TEdge[] which doesn't implement List<TEdge> in Java.
        if (stmt.Expression != null && context.SemanticModel != null)
        {
            var exprType = context.SemanticModel.GetTypeInfo(stmt.Expression).Type;
            if (exprType is IArrayTypeSymbol { Rank: 1 } arrayType)
            {
                // Check if the enclosing method's return type is IList<T>, ICollection<T>, or IEnumerable<T>
                var enclosingMethod = stmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                ITypeSymbol? enclosingRetSym = null;
                if (enclosingMethod != null)
                    enclosingRetSym = context.SemanticModel.GetTypeInfo(enclosingMethod.ReturnType).Type;
                // Also check property accessor (return in a get { } block)
                if (enclosingRetSym == null)
                {
                    var propAccessor = stmt.Ancestors().OfType<AccessorDeclarationSyntax>().FirstOrDefault();
                    if (propAccessor != null)
                    {
                        var propDecl = propAccessor.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
                        if (propDecl != null)
                            enclosingRetSym = context.SemanticModel.GetTypeInfo(propDecl.Type).Type;
                    }
                }
                if (enclosingRetSym is INamedTypeSymbol retNamed &&
                    retNamed.Name is "IList" or "ICollection" or "List" or "Collection"
                        or "IEnumerable" or "Iterable")
                {
                    // Primitive arrays (int[], double[], etc.) can't use Arrays.asList() directly
                    // because Arrays.asList(int[]) returns List<int[]>, not List<Integer>.
                    if (arrayType.ElementType.SpecialType is
                        SpecialType.System_Int32 or SpecialType.System_Int64 or
                        SpecialType.System_Double or SpecialType.System_Single or
                        SpecialType.System_Boolean or SpecialType.System_Byte or
                        SpecialType.System_Int16 or SpecialType.System_Char)
                    {
                        expr = $"Arrays.stream({expr}).boxed().collect(java.util.stream.Collectors.toList())";
                        context.AddImport("java.util.Arrays");
                        context.AddImport("java.util.stream.Collectors");
                    }
                    else
                    {
                        expr = $"Arrays.asList({expr})";
                        context.AddImport("java.util.Arrays");
                    }
                }
            }

            // Detect when a Stream expression is returned from a method that declares Iterable/IEnumerable.
            // C# LINQ expressions become Java Streams but IEnumerable<T> maps to Iterable<T>.
            // Stream<T> does not implement Iterable<T>, so we need .collect(Collectors.toCollection(ArrayList::new)).
            var retExprType = context.SemanticModel?.GetTypeInfo(stmt.Expression).Type;
            bool isStreamReturn = retExprType is INamedTypeSymbol retNamed2 &&
                (retNamed2.Name is "IEnumerable" or "IOrderedEnumerable" or "IQueryable") &&
                retNamed2.ContainingNamespace?.ToDisplayString().StartsWith("System") == true;
            if (isStreamReturn && (expr.Contains(".map(") || expr.Contains(".filter(") ||
                expr.Contains(".flatMap(") || expr.Contains(".select(") ||
                expr.Contains("stream(") || expr.Contains("Stream.concat") ||
                expr.Contains(".distinct(") || expr.Contains(".sorted(")))
            {
                // Check if enclosing method returns Iterable
                var enclosing = stmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                ITypeSymbol? enclosingRetType = enclosing != null
                    ? context.SemanticModel?.GetTypeInfo(enclosing.ReturnType).Type
                    : null;
                // Also check property accessor (return inside a get { } block)
                if (enclosingRetType == null)
                {
                    var accessorDecl = stmt.Ancestors().OfType<AccessorDeclarationSyntax>().FirstOrDefault();
                    if (accessorDecl != null)
                    {
                        var propDecl2 = accessorDecl.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
                        if (propDecl2 != null)
                            enclosingRetType = context.SemanticModel?.GetTypeInfo(propDecl2.Type).Type;
                    }
                }
                bool enclosingReturnsIterable = enclosingRetType is INamedTypeSymbol mret &&
                    mret.Name is "IEnumerable" or "ICollection" or "IList";
                // Don't double-collect: if the expression already ends with .toList() or ArrayList::new it's already a List
                bool alreadyCollected = expr.EndsWith(".toList())")
                    || expr.EndsWith("toList()))")
                    || expr.EndsWith("ArrayList::new))")
                    || expr.EndsWith("ArrayList::new)")
                    || expr.EndsWith(".toArray())")
                    || System.Text.RegularExpressions.Regex.IsMatch(expr, @"\.toArray\([^)]+\)\)$")
                    || System.Text.RegularExpressions.Regex.IsMatch(expr, @"\.toArray\([^)]+\)$");
                if (enclosingReturnsIterable && !alreadyCollected)
                {
                    context.AddImport("java.util.stream.Collectors");
                    context.AddImport("java.util.ArrayList");
                    expr = $"{expr}.collect(Collectors.toCollection(ArrayList::new))";
                }
            }
        }

        // Struct value copy: when returning a user-defined struct expression that is not a temporary,
        // clone it to preserve C# value-copy semantics (C# return always copies structs).
        // Skip inside property getters where the consumption site handles cloning.
        if (stmt.Expression != null && context.SemanticModel != null && !context.SuppressReturnClone)
        {
            var retExprTypeForClone = context.SemanticModel.GetTypeInfo(stmt.Expression).Type;
            expr = StructCloneHelper.CloneStructValueIfNeeded(stmt.Expression, expr, retExprTypeForClone, context);
        }

        expr = ExpressionTransformerHelpers.AdaptExpressionToTargetType(
            stmt.Expression,
            expr,
            returnTargetType,
            context);

        // Bug 3: drain any pre/post statements produced while transforming the return expression
        // (e.g., ref argument wrapping adds pre-statements for holder init and post-statements for write-back).
        if (context.HasPendingPreStatements || context.HasPendingPostStatements)
        {
            var sb = new System.Text.StringBuilder();
            if (context.HasPendingPreStatements)
            {
                var preStmts = context.DrainPreStatements();
                sb.Append(string.Join("\n", preStmts.Select(s => s.TrimEnd(';') + ";")));
                sb.Append("\n");
            }
            if (context.HasPendingPostStatements)
            {
                // Capture the return value in a temp variable, emit post-stmts, then return it.
                var postStmts = context.DrainPostStatements();
                sb.Append($"var _ret = {expr};\n");
                sb.Append(string.Join("\n", postStmts.Select(s => s.TrimEnd(';') + ";")));
                sb.Append("\nreturn _ret;");
            }
            else
            {
                sb.Append($"return {expr};");
            }
            return new JavaStatementNode(sb.ToString());
        }

        return new JavaStatementNode($"return {expr};");
    }

    private static ITypeSymbol? ResolveReturnTargetType(ReturnStatementSyntax stmt, ConversionContext context)
    {
        if (context.SemanticModel == null)
            return null;

        var returnScope = stmt.Ancestors().FirstOrDefault(ancestor => ancestor is MethodDeclarationSyntax
            or AccessorDeclarationSyntax
            or LocalFunctionStatementSyntax
            or AnonymousFunctionExpressionSyntax);

        return returnScope switch
        {
            MethodDeclarationSyntax method => context.SemanticModel.GetTypeInfo(method.ReturnType).Type,
            AccessorDeclarationSyntax accessor => (context.SemanticModel.GetDeclaredSymbol(accessor) as IMethodSymbol)?.ReturnType,
            LocalFunctionStatementSyntax localFunction => context.SemanticModel.GetTypeInfo(localFunction.ReturnType).Type,
            _ => null
        };
    }

    private JavaSyntaxNode TransformIfStatement(IfStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;

        // Fix: Handle !TryGetValue(key, out value) - negated TryGetValue in if condition
        // C#: if (!dict.TryGetValue(key, out value)) { dict[key] = value = compute(); }
        // Java: value = dict.get(key); if (value == null) { dict.put(key, value = compute()); }
        // The if body is responsible for initializing the value, so we don't generate new TC()
        if (stmt.Condition is PrefixUnaryExpressionSyntax unaryNot
            && unaryNot.OperatorToken.IsKind(SyntaxKind.ExclamationToken)
            && unaryNot.Operand is InvocationExpressionSyntax tvIfInvoc
            && tvIfInvoc.Expression is MemberAccessExpressionSyntax tvIfMa
            && tvIfMa.Name.Identifier.Text == "TryGetValue"
            && tvIfInvoc.ArgumentList.Arguments.Count == 2
            && tvIfInvoc.ArgumentList.Arguments[1].RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
        {
            var tvOutArg = tvIfInvoc.ArgumentList.Arguments[1];

            // Case A: out var v  (declaration)
            if (tvOutArg.Expression is DeclarationExpressionSyntax tvIfDeclExpr
                && tvIfDeclExpr.Designation is SingleVariableDesignationSyntax tvIfSvd)
            {
                var tvTarget = exprTransformer.Transform(tvIfMa.Expression, context);
                var tvKey = exprTransformer.Transform(tvIfInvoc.ArgumentList.Arguments[0].Expression, context);

                var tyInfo = context.SemanticModel?.GetTypeInfo(tvIfDeclExpr.Type);
                var outJavaType = tyInfo.HasValue && tyInfo.Value.Type != null ? context.MapType(tyInfo.Value.Type) : "Object";
                var outVarName = ConversionContext.EscapeJavaKeyword(tvIfSvd.Identifier.Text);

                var tvStmtTransformer = new StatementTransformer();
                string thenBody;
                if (stmt.Statement is BlockSyntax tvIfThenBlock)
                    thenBody = $"{{\n        {TransformBlock(tvIfThenBlock, context)}\n    }}";
                else
                    thenBody = $"{{ {tvStmtTransformer.Transform(stmt.Statement, context).ToString("")} }}";

                var ifSb = new System.Text.StringBuilder();
                ifSb.Append($"{outJavaType} {outVarName} = {tvTarget}.get({tvKey});\n");
                ifSb.Append($"if ({outVarName} == null) {thenBody}");

                if (stmt.Else != null)
                {
                    string elseBody;
                    if (stmt.Else.Statement is BlockSyntax tvIfElseBlock)
                        elseBody = $"{{\n        {TransformBlock(tvIfElseBlock, context)}\n    }}";
                    else
                        elseBody = $"{{ {tvStmtTransformer.Transform(stmt.Else.Statement, context).ToString("")} }}";
                    ifSb.Append($" else {elseBody}");
                }

                return new JavaStatementNode(ifSb.ToString());
            }

            // Case B: out existingVar  (assignment to already-declared variable)
            if (tvOutArg.Expression is IdentifierNameSyntax tvIfIdent)
            {
                var tvTarget = exprTransformer.Transform(tvIfMa.Expression, context);
                var tvKey = exprTransformer.Transform(tvIfInvoc.ArgumentList.Arguments[0].Expression, context);
                var existingVarName = ConversionContext.EscapeJavaKeyword(tvIfIdent.Identifier.Text);

                var tvStmtTransformer2 = new StatementTransformer();
                string thenBody;
                if (stmt.Statement is BlockSyntax tvIfThenBlock2)
                    thenBody = $"{{\n        {TransformBlock(tvIfThenBlock2, context)}\n    }}";
                else
                    thenBody = $"{{ {tvStmtTransformer2.Transform(stmt.Statement, context).ToString("")} }}";

                var ifSb2 = new System.Text.StringBuilder();
                ifSb2.Append($"{existingVarName} = {tvTarget}.get({tvKey});\n");
                ifSb2.Append($"if ({existingVarName} == null) {thenBody}");

                if (stmt.Else != null)
                {
                    string elseBody;
                    if (stmt.Else.Statement is BlockSyntax tvIfElseBlock)
                        elseBody = $"{{\n        {TransformBlock(tvIfElseBlock, context)}\n    }}";
                    else
                        elseBody = $"{{ {tvStmtTransformer2.Transform(stmt.Else.Statement, context).ToString("")} }}";
                    ifSb2.Append($" else {elseBody}");
                }

                return new JavaStatementNode(ifSb2.ToString());
            }
        }

        // Fix 5: Handle TryGetValue(key, out var value) or TryGetValue(key, out existingVar) in if condition
        // Java Map.get() returns null for missing keys; use containsKey + get + local declaration/assignment
        if (stmt.Condition is InvocationExpressionSyntax tvIfInvoc2
            && tvIfInvoc2.Expression is MemberAccessExpressionSyntax tvIfMa2
            && tvIfMa2.Name.Identifier.Text == "TryGetValue"
            && tvIfInvoc2.ArgumentList.Arguments.Count == 2
            && tvIfInvoc2.ArgumentList.Arguments[1].RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
        {
            var tvOutArg2 = tvIfInvoc2.ArgumentList.Arguments[1];

            // Case A: out var v  (declaration)
            if (tvOutArg2.Expression is DeclarationExpressionSyntax tvIfDeclExpr2
                && tvIfDeclExpr2.Designation is SingleVariableDesignationSyntax tvIfSvd2)
            {
                var tvTarget2 = exprTransformer.Transform(tvIfMa2.Expression, context);
                var tvKey2 = exprTransformer.Transform(tvIfInvoc2.ArgumentList.Arguments[0].Expression, context);

                var tyInfo = context.SemanticModel?.GetTypeInfo(tvIfDeclExpr2.Type);
                var outJavaType = tyInfo.HasValue && tyInfo.Value.Type != null ? context.MapType(tyInfo.Value.Type) : "Object";
                var outVarName = ConversionContext.EscapeJavaKeyword(tvIfSvd2.Identifier.Text);

                var tvStmtTransformer3 = new StatementTransformer();

                string thenBody;
                if (stmt.Statement is BlockSyntax tvIfThenBlock3)
                {
                    var bodyStr = TransformBlock(tvIfThenBlock3, context);
                    thenBody = $"{{\n        {outJavaType} {outVarName} = {tvTarget2}.get({tvKey2});\n        {bodyStr}\n    }}";
                }
                else
                {
                    var bodyStr = tvStmtTransformer3.Transform(stmt.Statement, context).ToString("");
                    thenBody = $"{{\n        {outJavaType} {outVarName} = {tvTarget2}.get({tvKey2});\n        {bodyStr}\n    }}";
                }

                var ifSb3 = new System.Text.StringBuilder();
                ifSb3.Append($"if ({tvTarget2}.containsKey({tvKey2})) {thenBody}");

                if (stmt.Else != null)
                {
                    string elseBody;
                    if (stmt.Else.Statement is BlockSyntax tvIfElseBlock3)
                        elseBody = $"{{\n        {TransformBlock(tvIfElseBlock3, context)}\n    }}";
                    else
                        elseBody = $"{{ {tvStmtTransformer3.Transform(stmt.Else.Statement, context).ToString("")} }}";
                    ifSb3.Append($" else {elseBody}");
                }

                return new JavaStatementNode(ifSb3.ToString());
            }

            // Case B: out existingVar  (assignment to already-declared variable)
            if (tvOutArg2.Expression is IdentifierNameSyntax tvIfIdent2)
            {
                var tvTarget2 = exprTransformer.Transform(tvIfMa2.Expression, context);
                var tvKey2 = exprTransformer.Transform(tvIfInvoc2.ArgumentList.Arguments[0].Expression, context);
                var existingVarName = ConversionContext.EscapeJavaKeyword(tvIfIdent2.Identifier.Text);

                var tvStmtTransformer4 = new StatementTransformer();

                string thenBody2;
                if (stmt.Statement is BlockSyntax tvIfThenBlock4)
                {
                    var bodyStr = TransformBlock(tvIfThenBlock4, context);
                    thenBody2 = $"{{\n        {existingVarName} = {tvTarget2}.get({tvKey2});\n        {bodyStr}\n    }}";
                }
                else
                {
                    var bodyStr = tvStmtTransformer4.Transform(stmt.Statement, context).ToString("");
                    thenBody2 = $"{{\n        {existingVarName} = {tvTarget2}.get({tvKey2});\n        {bodyStr}\n    }}";
                }

                var ifSb4 = new System.Text.StringBuilder();
                ifSb4.Append($"if ({tvTarget2}.containsKey({tvKey2})) {thenBody2}");

                if (stmt.Else != null)
                {
                    string elseBody2;
                    if (stmt.Else.Statement is BlockSyntax tvIfElseBlock4)
                        elseBody2 = $"{{\n        {TransformBlock(tvIfElseBlock4, context)}\n    }}";
                    else
                        elseBody2 = $"{{ {tvStmtTransformer4.Transform(stmt.Else.Statement, context).ToString("")} }}";
                    ifSb4.Append($" else {elseBody2}");
                }

                return new JavaStatementNode(ifSb4.ToString());
            }
        }

        var condition = exprTransformer.Transform(stmt.Condition, context);

        // Fix: out/ref parameters in the condition (e.g. if (TryParse(s, out var n))) emit
        // Holder declarations as pre-statements and value read-backs as post-statements.
        // Both must appear BEFORE the if — the out values must be available regardless
        // of whether the condition is true or false (C# guarantees out params are set).
        string condPreamble = "";
        string readBacks = "";
        string effectiveCondition = condition;
        if (context.HasPendingPreStatements)
        {
            var pre = context.DrainPreStatements();
            condPreamble = string.Join("\n", pre.Select(s => s.TrimEnd(';') + ";")) + "\n";
        }
        if (context.HasPendingPostStatements)
        {
            var post = context.DrainPostStatements();
            // Split post-statements:
            // - New variable declarations (e.g. "int n = _nHolder1.value") have a type before '='
            //   → these are out-var read-backs, scoped to the then-body
            // - Assignments to existing variables (e.g. "v = _vHolder1.value") have no type
            //   → these must be available after the if, so extract condition to temp var
            var bodyScoped = new List<string>();
            var preIfScoped = new List<string>();
            foreach (var s in post)
            {
                var eqIdx = s.IndexOf('=');
                if (eqIdx > 0 && s.Substring(0, eqIdx).Trim().Contains(' '))
                    bodyScoped.Add(s);
                else
                    preIfScoped.Add(s);
            }
            if (preIfScoped.Count > 0)
            {
                var condTemp = context.GenerateSyntheticName("_ifCond");
                condPreamble += $"var {condTemp} = {condition};\n"
                    + string.Join("\n", preIfScoped.Select(s => s.TrimEnd(';') + ";")) + "\n";
                effectiveCondition = condTemp;
            }
            if (bodyScoped.Count > 0)
            {
                readBacks = string.Join("\n        ", bodyScoped.Select(s => s.TrimEnd(';') + ";")) + "\n        ";
            }
        }

        var stmtTransformer = new StatementTransformer();

        string thenBlock;
        if (stmt.Statement is BlockSyntax block)
        {
            var bodyStr = TransformBlock(block, context);
            thenBlock = $"{{\n        {readBacks}{bodyStr}\n    }}";
        }
        else
        {
            var bodyStr = stmtTransformer.Transform(stmt.Statement, context).ToString("");
            thenBlock = $"{{\n        {readBacks}{bodyStr}\n    }}";
        }

        var result = new System.Text.StringBuilder();
        result.Append($"{condPreamble}if ({effectiveCondition}) {thenBlock}");

        if (stmt.Else != null)
        {
            var elseBlock = stmt.Else.Statement is BlockSyntax elseBlockSyntax
                ? $"{{\n        {TransformBlock(elseBlockSyntax, context)}\n    }}"
                : $"{{\n        {stmtTransformer.Transform(stmt.Else.Statement, context).ToString("")}\n    }}";
            result.Append($" else {elseBlock}");
        }

        return new JavaStatementNode(result.ToString());
    }

    private JavaSyntaxNode TransformWhileStatement(WhileStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var condition = exprTransformer.Transform(stmt.Condition, context);

        // Fix: out/ref parameters in while condition — Holder declarations go before the loop;
        // value read-backs go at the start of the body (re-read each iteration after the call).
        string whilePreamble = "";
        string whilePostInjection = "";
        string whilePostAfterLoop = "";
        if (context.HasPendingPreStatements)
        {
            var pre = context.DrainPreStatements();
            whilePreamble = string.Join("\n", pre.Select(s => s.TrimEnd(';') + ";")) + "\n";
        }
        if (context.HasPendingPostStatements)
        {
            var post = context.DrainPostStatements();
            whilePostInjection = string.Join("\n        ", post.Select(s => s.TrimEnd(';') + ";")) + "\n        ";
            whilePostAfterLoop = "\n" + string.Join("\n", post.Select(s => s.TrimEnd(';') + ";"));
        }

        var stmtTransformer = new StatementTransformer();
        string body;
        if (stmt.Statement is BlockSyntax block)
        {
            var bodyStr = TransformBlock(block, context);
            body = $"{{\n        {whilePostInjection}{bodyStr}\n    }}";
        }
        else
        {
            var bodyStr = stmtTransformer.Transform(stmt.Statement, context).ToString("");
            body = $"{{\n        {whilePostInjection}{bodyStr}\n    }}";
        }

        return new JavaStatementNode($"{whilePreamble}while ({condition}) {body}{whilePostAfterLoop}");
    }

    private JavaSyntaxNode TransformForStatement(ForStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;

        // 初始值
        var initializers = "";
        if (stmt.Declaration != null)
        {
            var typeInfo = context.SemanticModel?.GetTypeInfo(stmt.Declaration.Type);
            var declaredType = typeInfo.HasValue ? typeInfo.Value.Type : null;
            var javaType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "var";
            var vars = stmt.Declaration.Variables.Select(v => {
                var initExpr = v.Initializer != null
                    ? ExpressionTransformerHelpers.AdaptExpressionToTargetType(
                        v.Initializer.Value,
                        exprTransformer.Transform(v.Initializer.Value, context),
                        declaredType,
                        context)
                    : string.Empty;
                var init = v.Initializer != null ? $" = {initExpr}" : "";
                return $"{v.Identifier}{init}";
            });
            initializers = $"{javaType} {string.Join(", ", vars)}";
        }
        else if (stmt.Initializers.Any())
        {
            initializers = string.Join(", ", stmt.Initializers.Select(i => exprTransformer.Transform(i, context)));
        }

        // 条件
        var condition = stmt.Condition != null
            ? exprTransformer.Transform(stmt.Condition, context)
            : "true";

        // 增量
        var incrementors = string.Join(", ", stmt.Incrementors.Select(i =>
            exprTransformer.Transform(i, context)));

        // Fix: drain any Holder pre/post statements produced while transforming
        // the for-loop initializers, condition, or incrementors.
        string forPreamble = "";
        string forPostInjection = "";
        if (context.HasPendingPreStatements)
        {
            var pre = context.DrainPreStatements();
            forPreamble = string.Join("\n", pre.Select(s => s.TrimEnd(';') + ";")) + "\n";
        }
        if (context.HasPendingPostStatements)
        {
            var post = context.DrainPostStatements();
            forPostInjection = string.Join("\n        ", post.Select(s => s.TrimEnd(';') + ";")) + "\n        ";
        }

        var stmtTransformer = new StatementTransformer();
        string body;
        if (stmt.Statement is BlockSyntax block)
        {
            var bodyStr = TransformBlock(block, context);
            body = $"{{\n        {forPostInjection}{bodyStr}\n    }}";
        }
        else
        {
            var bodyStr = stmtTransformer.Transform(stmt.Statement, context).ToString("");
            body = $"{{\n        {forPostInjection}{bodyStr}\n    }}";
        }

        return new JavaStatementNode($"{forPreamble}for ({initializers}; {condition}; {incrementors}) {body}");
    }

    private JavaSyntaxNode TransformForEachStatement(ForEachStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;

        // Special case: foreach over a LINQ query with anonymous type select
        // e.g.: foreach (var pair in from A a in src from B b in tgt select new { aV = a, bV = b })
        // Transform as nested for loops, replacing pair.getaV() → a, pair.getbV() → b
        if (stmt.Expression is QueryExpressionSyntax queryExpr &&
            queryExpr.Body.SelectOrGroup is SelectClauseSyntax selectClause &&
            selectClause.Expression is AnonymousObjectCreationExpressionSyntax anonCreate)
        {
            return TransformForEachWithAnonymousQuery(stmt, queryExpr, anonCreate, context);
        }

        var typeInfo = context.SemanticModel?.GetTypeInfo(stmt.Type);
        var javaType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "var";
        var identifier = ConversionContext.EscapeJavaKeyword(stmt.Identifier.Text);
        var expression = exprTransformer.Transform(stmt.Expression, context);

        var stmtTransformer = new StatementTransformer();
        var body = stmt.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{ {stmtTransformer.Transform(stmt.Statement, context).ToString("")} }}";

        // Detect: iterating over a Dictionary/Map → need .entrySet() in Java
        var exprTypeInfo = context.SemanticModel?.GetTypeInfo(stmt.Expression).Type;
        if (exprTypeInfo is INamedTypeSymbol exprNamed &&
            (exprNamed.Name is "Dictionary" or "SortedDictionary" or "IDictionary" or
             "HashMap" or "TreeMap" or "LinkedHashMap" ||
             exprNamed.AllInterfaces.Any(i => i.Name is "IDictionary")))
        {
            // Map iteration: use entrySet()
            expression = $"{expression}.entrySet()";
            // javaType likely contains "AbstractMap.SimpleEntry" or "KeyValuePair" — normalize to "Map.Entry"
            if (javaType.Contains("SimpleEntry") || javaType.Contains("KeyValuePair") || javaType == "var")
            {
                // Try to get the proper Map.Entry type from the dictionary's type arguments
                if (exprNamed.IsGenericType && exprNamed.TypeArguments.Length >= 2)
                {
                    // Must use boxed types for Map.Entry generic args (primitives not allowed in generics)
                    static string BoxJavaType(string t) => t switch
                    {
                        "int" => "Integer", "long" => "Long", "double" => "Double",
                        "float" => "Float", "boolean" => "Boolean", "short" => "Short",
                        "byte" => "Byte", "char" => "Character", _ => t
                    };
                    var keyType = BoxJavaType(context.MapType(exprNamed.TypeArguments[0]));
                    var valType = BoxJavaType(context.MapType(exprNamed.TypeArguments[1]));
                    javaType = $"Map.Entry<{keyType}, {valType}>";
                    context.AddImport("java.util.Map");
                }
                else
                {
                    javaType = "Map.Entry<?, ?>";
                    context.AddImport("java.util.Map");
                }
            }
        }

        // Pre-process: StreamSupport.stream(...).toArray() used in foreach can't be iterated (Object[]).
        // Convert to .collect(Collectors.toCollection(ArrayList::new)) so the list is Iterable<T> and foreach works.
        {
            var trimExpr = expression.TrimEnd();
            if (trimExpr.EndsWith(".toArray()") && trimExpr.Contains("StreamSupport.stream("))
            {
                expression = trimExpr.Substring(0, trimExpr.Length - ".toArray()".Length)
                                 + ".collect(Collectors.toCollection(ArrayList::new))";
                context.AddImport("java.util.ArrayList");
            }
        }

        // Stream.concat(), StreamSupport.stream(), Arrays.stream(), etc.
        // Collect to List to allow break/continue/return in the loop body.
        // Use EndsWith check to avoid double-collecting an already-collected stream:
        // the expression may contain inner .collect() calls (e.g. spliterator wrapping)
        // but we only skip if the OUTERMOST call is already .collect(Collectors.toCollection(ArrayList::new)).
        bool isStream = !expression.TrimEnd().EndsWith(".collect(Collectors.toCollection(ArrayList::new))")
            && !System.Text.RegularExpressions.Regex.IsMatch(expression.TrimEnd(), @"\.collect\(.+\)$")
            && !expression.TrimEnd().EndsWith(".toArray()")
            && !System.Text.RegularExpressions.Regex.IsMatch(expression.TrimEnd(), @"\.toArray\([^)]+\)$")
            && (
            expression.Contains("Stream.concat(") || expression.Contains("StreamSupport.stream(") ||
            expression.Contains("Arrays.stream(") || expression.Contains("IntStream.range(") || expression.Contains(".stream()") ||
            expression.Contains(".map(") ||
            expression.Contains(".filter(") ||
            expression.Contains(".flatMap(") ||
            expression.Contains(".sorted(") ||
            expression.Contains(".distinct(") ||
            expression.Contains(".limit(") ||
            expression.Contains(".skip(") ||
            expression.Contains(".peek(") ||
            expression.Contains(".mapToInt(") ||
            expression.Contains(".mapToLong(") ||
            expression.Contains(".mapToDouble(") ||
            expression.Contains(".mapToObj("));

        // Also detect by semantic type: if the C# expression type is IOrderedEnumerable or IQueryable
        // (both are always yielded as Java Streams by the LINQ translator), force-collect.
        if (!isStream && exprTypeInfo is INamedTypeSymbol csForeachType)
        {
            bool isLinqResult = csForeachType.Name is "IOrderedEnumerable" or "IOrderedQueryable" or "IQueryable"
                || (csForeachType.ContainingNamespace?.ToDisplayString().StartsWith("System.Linq") == true
                    && csForeachType.Name != "IEnumerable" && csForeachType.Name != "ICollection");
            if (isLinqResult)
                isStream = true;
        }

        // Fallback: if the foreach expression is a locally-declared variable that was registered
        // as stream-typed in TransformLocalDeclaration, treat it as a stream here too.
        // This handles cases where the SemanticModel is unavailable or the type is not in System.Linq.
        //
        // Root cause of issue #10: StreamLocalVariables does not track scope — a variable name added
        // to the set in one block persists for the rest of the method. When the same variable name is
        // reused in a sibling scope with an array type (Point[]), the foreach must NOT collect it,
        // because arrays are directly iterable in Java. Fix: exclude IArrayTypeSymbol from stream treatment.
        if (!isStream && context.StreamLocalVariables.Contains(expression.Trim()))
        {
            bool exprIsCollectionLike =
                // Named collection/iterable interfaces are already collections, not streams
                (exprTypeInfo is INamedTypeSymbol exprNamedType &&
                    exprNamedType.Name is "IEnumerable" or "ICollection" or "IList" or "List" or "Collection" or "Iterable")
                // Arrays are directly iterable in Java (T[] supports for-each without stream wrapping)
                || exprTypeInfo is IArrayTypeSymbol;
            if (!exprIsCollectionLike)
                isStream = true;
        }

        if (isStream)
        {
            // Collect stream to list so that break/return/continue work in loop body
            context.AddImport("java.util.ArrayList");
            context.AddImport("java.util.stream.Collectors");
            expression = $"{expression}.collect(Collectors.toCollection(ArrayList::new))";
        }

        // Iterating a raw (non-generic) IEnumerable with a typed loop variable: in Java, iterating
        // a raw Iterable yields Object, which can't be assigned to a typed variable (compile error).
        // Cast the iterable to Iterable<ElementType> to make it compile (generates unchecked warning).
        if (!isStream && javaType != "Object" && javaType != "var"
            && exprTypeInfo is INamedTypeSymbol rawEnum
            && !rawEnum.IsGenericType
            && rawEnum.Name is "IEnumerable" or "ICollection")
        {
            expression = $"(Iterable<{javaType}>) ({expression})";
        }

        // Fix L: Downcast in foreach — C# allows implicitly downcasting the element type in foreach
        // (e.g., foreach (NetworkEdge e in IEnumerable<PolyIntEdge>)) but Java does not.
        // Detect via Roslyn's ForEachStatementInfo.ElementConversion.IsExplicit.
        if (!isStream && javaType != "var" && javaType != "Object" && context.SemanticModel != null)
        {
            try
            {
                var forEachInfo = context.SemanticModel.GetForEachStatementInfo(stmt);
                if (forEachInfo.ElementConversion.IsExplicit && !forEachInfo.ElementConversion.IsUserDefined
                    && forEachInfo.ElementType != null)
                {
                    // Wrap iterable with a double cast to suppress the type mismatch.
                    // At runtime the elements are already the correct subtype.
                    expression = $"(Iterable<{javaType}>)(Iterable<?>)({expression})";
                }
            }
            catch { /* SemanticModel may fail for some edge cases — skip silently */ }
        }

        return new JavaStatementNode($"for ({javaType} {identifier} : {expression}) {body}");
    }

    /// <summary>
    /// Handles: foreach (var pair in from A a in src from B b in tgt ... select new { aField = a, bField = b })
    /// by expanding to nested for-each loops, substituting the anonymous field accesses with the actual variables.
    /// </summary>
    private JavaSyntaxNode TransformForEachWithAnonymousQuery(
        ForEachStatementSyntax stmt,
        QueryExpressionSyntax queryExpr,
        AnonymousObjectCreationExpressionSyntax anonCreate,
        ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;

        // Collect all from-clauses (outer first, inner last)
        var froms = new List<(string varName, string javaType, string sourceExpr)>();

        // Outer from
        var outerFrom = queryExpr.FromClause;
        var outerSrc = exprTransformer.Transform(outerFrom.Expression, context);
        var outerSrcType = context.SemanticModel?.GetTypeInfo(outerFrom.Expression).Type;
        string outerIterType;
        if (outerFrom.Type is PredefinedTypeSyntax || outerFrom.Type?.IsKind(SyntaxKind.IdentifierName) == true)
        {
            var ti = context.SemanticModel?.GetTypeInfo(outerFrom.Type);
            outerIterType = ti.HasValue && ti.Value.Type != null ? context.MapType(ti.Value.Type) : "var";
        }
        else
        {
            outerIterType = "var";
        }
        froms.Add((outerFrom.Identifier.ValueText, outerIterType, outerSrc));

        // Inner from-clauses
        foreach (var clause in queryExpr.Body.Clauses)
        {
            if (clause is FromClauseSyntax innerFrom)
            {
                var innerSrc = exprTransformer.Transform(innerFrom.Expression, context);
                string innerIterType;
                if (innerFrom.Type is PredefinedTypeSyntax || innerFrom.Type?.IsKind(SyntaxKind.IdentifierName) == true)
                {
                    var ti2 = context.SemanticModel?.GetTypeInfo(innerFrom.Type);
                    innerIterType = ti2.HasValue && ti2.Value.Type != null ? context.MapType(ti2.Value.Type) : "var";
                }
                else
                {
                    innerIterType = "var";
                }
                froms.Add((innerFrom.Identifier.ValueText, innerIterType, innerSrc));
            }
            // Ignore orderby (TODO: sorting)
        }

        // Build a map from anonymous field name → actual variable name from select clause
        // e.g. new { sourceV = source, targetV = target } → sourceV→source, targetV→target
        var fieldToVar = new Dictionary<string, string>();
        foreach (var initializer in anonCreate.Initializers)
        {
            var fieldName = initializer.NameEquals?.Name.Identifier.ValueText
                            ?? (initializer.Expression is IdentifierNameSyntax id ? id.Identifier.ValueText : null);
            string? exprVarName = initializer.Expression is IdentifierNameSyntax id2 ? id2.Identifier.ValueText : null;
            if (fieldName != null && exprVarName != null)
                fieldToVar[fieldName] = exprVarName;
        }

        // The foreach identifier (e.g. "pair") is what downstream code accesses as pair.getXxx().
        // We need to transform the body and replace "pair.getSourceV()", "pair.getTargetV()" etc.
        // We do this by translating the body normally (which will emit pair.getFieldName())
        // and then doing string replacement of those accessor patterns.
        var forEachIdent = stmt.Identifier.Text;

        var stmtTransformer = new StatementTransformer();
        string body;
        if (stmt.Statement is BlockSyntax block)
        {
            body = $"{{\n        {TransformBlock(block, context)}\n    }}";
        }
        else
        {
            body = $"{{ {stmtTransformer.Transform(stmt.Statement, context).ToString("")} }}";
        }

        // Replace pair.getFieldName() with the actual variable name
        foreach (var (fieldName, varName) in fieldToVar)
        {
            // The getter follows JavaBean convention: capitalize first letter (e.g., sourceV -> getSourceV)
            var getterName = "get" + ToPascalCase(fieldName);
            body = body.Replace($"{forEachIdent}.{getterName}()", varName);
            body = body.Replace($"{forEachIdent}.{fieldName}", varName);
            body = body.Replace($"{forEachIdent}.{fieldName}()", varName);
        }

        // If body had helper aliases like "var source = pair.sourceV;" they become
        // "var source = source;" after replacement and should be dropped.
        body = System.Text.RegularExpressions.Regex.Replace(
            body,
            @"\bvar\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*\1\s*;\s*",
            string.Empty);
        body = System.Text.RegularExpressions.Regex.Replace(
            body,
            @"\bvar\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*\1\s*\(\s*\)\s*;\s*",
            string.Empty);

        // Build nested for loops from outermost to innermost
        var sb = new System.Text.StringBuilder();
        var indent = "";
        for (int i = 0; i < froms.Count; i++)
        {
            var (varName, javaType, srcExpr) = froms[i];
            // Wrap stream expressions in collect for the outer loops (inner loops can stay as-is if Iterable)
            bool needsCollect = srcExpr.Contains("StreamSupport.stream(") || srcExpr.Contains("Arrays.stream(") || srcExpr.Contains("IntStream.range(") ||
                (srcExpr.Contains(".map(") && !srcExpr.Contains(".collect(")) ||
                (srcExpr.Contains(".filter(") && !srcExpr.Contains(".collect("));
            if (needsCollect)
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.ArrayList");
                srcExpr = $"{srcExpr}.collect(Collectors.toCollection(ArrayList::new))";
            }
            if (i == froms.Count - 1)
            {
                // Innermost: attach the body
                sb.Append($"{indent}for ({javaType} {varName} : {srcExpr}) {body}");
            }
            else
            {
                sb.AppendLine($"{indent}for ({javaType} {varName} : {srcExpr}) {{");
                indent += "    ";
            }
        }
        // Close outer loops
        for (int i = froms.Count - 2; i >= 0; i--)
        {
            indent = indent.Length >= 4 ? indent.Substring(4) : "";
            sb.AppendLine();
            sb.Append($"{indent}}}");
        }

        return new JavaStatementNode(sb.ToString());
    }

    private JavaSyntaxNode TransformDoStatement(DoStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;

        var stmtTransformer = new StatementTransformer();
        var bodyBlock = stmt.Statement is BlockSyntax block
            ? TransformBlock(block, context)
            : stmtTransformer.Transform(stmt.Statement, context).ToString("");

        // Condition is evaluated at end of each iteration. Any Holder declarations it produces
        // must live outside the loop; value read-backs are injected into the body tail.
        // Strategy: transform body first so its own pre/post stmts are already drained,
        // then transform condition and hoist its pre-stmts before the do.
        var condition = exprTransformer.Transform(stmt.Condition, context);

        string doPreamble = "";
        string doBodyTail = "";
        if (context.HasPendingPreStatements)
        {
            var pre = context.DrainPreStatements();
            doPreamble = string.Join("\n", pre.Select(s => s.TrimEnd(';') + ";")) + "\n";
        }
        if (context.HasPendingPostStatements)
        {
            // Post-stmts after the condition would only run when the condition is false;
            // inject at the end of the body so they run every iteration before re-checking.
            var post = context.DrainPostStatements();
            doBodyTail = "\n        " + string.Join("\n        ", post.Select(s => s.TrimEnd(';') + ";"));
        }

        var body = $"{{\n        {bodyBlock}{doBodyTail}\n    }}";
        return new JavaStatementNode($"{doPreamble}do {body} while ({condition});");
    }

    private JavaSyntaxNode TransformSwitchStatement(SwitchStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var expression = exprTransformer.Transform(stmt.Expression, context);

        var sections = new List<string>();

        foreach (var section in stmt.Sections)
        {
            var labels = new List<string>();

            foreach (var label in section.Labels)
            {
                switch (label)
                {
                    case CaseSwitchLabelSyntax caseLabel:
                        var transformedLabel = exprTransformer.Transform(caseLabel.Value, context);
                        if (ExpressionTransformerHelpers.TryFormatEnumMemberAccess(
                            caseLabel.Value,
                            context,
                            useUnqualifiedRegularEnumInSwitchLabel: true,
                            out var formattedEnumLabel))
                        {
                            transformedLabel = formattedEnumLabel;
                        }
                        else if (transformedLabel.Contains(".get"))
                        {
                            var parts = transformedLabel.Split(new[] { ".get" }, StringSplitOptions.None);
                            transformedLabel = parts.Last().Replace("()", "");
                        }
                        labels.Add($"case {transformedLabel}");
                        break;
                    case DefaultSwitchLabelSyntax:
                        labels.Add("default");
                        break;
                }
            }

            var stmtTransformer = new StatementTransformer();
            var statements = section.Statements.Select(s =>
                stmtTransformer.Transform(s, context).ToString("")).ToList();

            // Build case section header: combine multiple labels as "case X, Y:" or "default:"
            string sectionStr;
            var caseValues = labels.Where(l => l.StartsWith("case ")).Select(l => l.Substring(5)).ToList();
            var hasDefault = labels.Contains("default");
            if (hasDefault && caseValues.Count == 0)
            {
                sectionStr = "default:\n            " + string.Join("\n            ", statements);
            }
            else if (hasDefault)
            {
                sectionStr = "case " + string.Join(", ", caseValues) + ":\n            default:\n            " +
                             string.Join("\n            ", statements);
            }
            else
            {
                sectionStr = "case " + string.Join(", ", caseValues) + ":\n            " +
                             string.Join("\n            ", statements);
            }

            sections.Add(sectionStr);
        }

        var bodyStr = string.Join("\n\n        ", sections);

        return new JavaStatementNode($"switch ({expression}) {{\n        {bodyStr}\n    }}");
    }

    private JavaSyntaxNode TransformTryStatement(TryStatementSyntax stmt, ConversionContext context)
    {
        var stmtTransformer = new StatementTransformer();
        var sb = new System.Text.StringBuilder();

        sb.Append("try ");
        sb.Append(stmt.Block is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : "{ }");

        // catch 块
        foreach (var catchClause in stmt.Catches)
        {
            string javaType = "Exception";
            string varName = "_ex";
            if (catchClause.Declaration != null)
            {
                var typeInfo = context.SemanticModel?.GetTypeInfo(catchClause.Declaration.Type);
                javaType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Exception";
                var rawVarName = catchClause.Declaration.Identifier.ValueText;
                if (!string.IsNullOrWhiteSpace(rawVarName))
                    varName = ConversionContext.EscapeJavaKeyword(rawVarName);
            }

            sb.Append($" catch ({javaType} {varName}) ");

            if (catchClause.Filter != null)
            {
                // Java 不支持 catch 过滤器，需要转换为内部 if
                var exprTransformer = ExpressionTransformerFacade.Instance;
                var filter = exprTransformer.Transform(catchClause.Filter.FilterExpression, context);
                sb.Append($"{{\n        if ({filter}) {{\n            {TransformBlock(catchClause.Block, context)}\n        }}\n    }}");
            }
            else
            {
                sb.Append(catchClause.Block is BlockSyntax catchBlock
                    ? $"{{\n        {TransformBlock(catchBlock, context)}\n    }}"
                    : "{ }");
            }
        }

        // finally 块
        if (stmt.Finally != null)
        {
            sb.Append(" finally ");
            sb.Append(stmt.Finally.Block is BlockSyntax finallyBlock
                ? $"{{\n        {TransformBlock(finallyBlock, context)}\n    }}"
                : "{ }");
        }

        return new JavaStatementNode(sb.ToString());
    }

    private JavaSyntaxNode TransformUsingStatement(UsingStatementSyntax stmt, ConversionContext context)
    {
        // Java 使用 try-with-resources
        var stmtTransformer = new StatementTransformer();
        var exprTransformer = ExpressionTransformerFacade.Instance;

        var resources = new List<string>();

        if (stmt.Declaration != null)
        {
            var typeInfo = context.SemanticModel?.GetTypeInfo(stmt.Declaration.Type);
            var resourceType = typeInfo.HasValue ? typeInfo.Value.Type : null;
            var javaType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "AutoCloseable";

            foreach (var variable in stmt.Declaration.Variables)
            {
                var resourceInit = variable.Initializer != null
                    ? ExpressionTransformerHelpers.AdaptExpressionToTargetType(
                        variable.Initializer.Value,
                        exprTransformer.Transform(variable.Initializer.Value, context),
                        resourceType,
                        context)
                    : string.Empty;
                var init = variable.Initializer != null
                    ? $" = {resourceInit}"
                    : "";
                resources.Add($"{javaType} {variable.Identifier}{init}");
            }
        }

        var body = stmt.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{ {stmtTransformer.Transform(stmt.Statement, context).ToString("")} }}";

        return new JavaStatementNode($"try ({string.Join("; ", resources)}) {body}");
    }

    private JavaSyntaxNode TransformLockStatement(LockStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var expression = exprTransformer.Transform(stmt.Expression, context);

        var stmtTransformer = new StatementTransformer();
        var body = stmt.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{ {stmtTransformer.Transform(stmt.Statement, context).ToString("")} }}";

        // Fix 6: Warn when lock expression is not a simple identifier; non-identifier lock targets
        // may evaluate to a different object on each synchronized block entry in Java
        string lockWarning = stmt.Expression is not IdentifierNameSyntax
            ? "// WARNING: lock expression is not a simple reference; verify lock identity\n"
            : "";
        return new JavaStatementNode($"{lockWarning}synchronized ({expression}) {body}");
    }

    private JavaSyntaxNode TransformFixedStatement(FixedStatementSyntax stmt, ConversionContext context)
    {
        context.Diagnostics.Error(
            "Java doesn't support fixed buffers. Manual conversion required.",
            stmt.GetLocation()
        );
        return new JavaStatementNode("/* TODO: Fixed statement - manual conversion required */");
    }

    private JavaSyntaxNode TransformUnsafeStatement(UnsafeStatementSyntax stmt, ConversionContext context)
    {
        context.Diagnostics.Error(
            "Java doesn't support unsafe code. Manual conversion required.",
            stmt.GetLocation()
        );
        return new JavaStatementNode("/* TODO: Unsafe statement - manual conversion required */");
    }

    private JavaSyntaxNode TransformCheckedStatement(CheckedStatementSyntax stmt, ConversionContext context)
    {
        // Fix 1: Java has no checked arithmetic; emit the inner block with an explanatory comment
        var body = TransformBlock(stmt.Block, context);
        return new JavaStatementNode("// C# checked block: use Math.*Exact() methods for overflow detection.\n" + body);
    }

    private JavaSyntaxNode TransformUncheckedStatement(CheckedStatementSyntax stmt, ConversionContext context)
    {
        // Fix 1: Java arithmetic is always unchecked; simply emit the inner block
        return new JavaStatementNode(TransformBlock(stmt.Block, context));
    }

    private JavaSyntaxNode TransformLocalDeclaration(LocalDeclarationStatementSyntax stmt, ConversionContext context)
    {
        var typeInfo = context.SemanticModel?.GetTypeInfo(stmt.Declaration.Type);
        string javaType;
        if (typeInfo.HasValue && typeInfo.Value.Type != null)
        {
            var resolvedType = typeInfo.Value.Type;
            // C# enumerator structs (e.g. Dictionary<K,V>.Enumerator, List<T>.Enumerator) have no Java equivalent.
            // The .iterator() call returns an Iterator<T>, so let Java infer the type with var.
            bool isEnumeratorStruct = resolvedType is INamedTypeSymbol nes
                && nes.Name == "Enumerator"
                && nes.ContainingType != null;
            // Also handle IEnumerator<T> mapped via GetEnumerator — use var so Java infers Iterator<T>
            bool isIEnumerator = resolvedType is INamedTypeSymbol ien
                && (ien.Name is "IEnumerator" or "IEnumerator`1");
            javaType = (isEnumeratorStruct || isIEnumerator) ? "var" : context.MapType(resolvedType);
            // Fallback: if MapType returns empty (e.g. unresolved error type), use var to let Java infer
            if (string.IsNullOrWhiteSpace(javaType))
                javaType = "var";
            // LINQ extension method generic type parameters (TSource, TResult, TKey, TElement) that
            // leak into the resolved type indicate an uninstantiated generic — use var instead.
            if (javaType.Contains("TSource") || javaType.Contains("TResult")
                || javaType.Contains("TKey") || javaType.Contains("TElement"))
                javaType = "var";
        }
        else
        {
            javaType = "var";
        }

        // When mapping C# IEnumerable<T>/ICollection<T> to Iterable<T> for a local variable,
        // use 'var' so Java infers the concrete return type (e.g. List<T>) from the initializer.
        // This prevents Collection<T> vs Iterable<T> compatibility issues (e.g. ArrayList.addAll).
        if (javaType == "Iterable" || javaType.StartsWith("Iterable<"))
            javaType = "var";

        // If the C# declaration used 'var' (implicit type) and had NO initializer, Java cannot infer the type.
        // We need to add a type + default initializer. Track whether the original C# type was implicit.
        bool wasImplicitVar = false;
        if (typeInfo.HasValue && typeInfo.Value.Type != null)
        {
            wasImplicitVar = stmt.Declaration.Type.IsVar;
        }
        else
        {
            wasImplicitVar = true;
        }

        // If C# used implicit 'var' with an initializer, prefer 'var' in Java.
        // This avoids incorrect/over-specific type annotations when C# resolves to ILookup,
        // IEnumerable, or uninstantiated generic types that don't cleanly map to Java equivalents.
        bool hasNoInitializer = stmt.Declaration.Variables.All(v => v.Initializer == null);
        if (wasImplicitVar && !hasNoInitializer)
        {
            // Exception: Java's var cannot infer lambda/method-reference types inside ternary
            // expressions, so keep the explicit type when the initializer is a conditional
            // expression containing lambdas or method references.
            bool hasTernaryWithLambda = stmt.Declaration.Variables.Any(v =>
                v.Initializer?.Value is ConditionalExpressionSyntax cond
                && (ContainsLambdaOrMethodRef(cond.WhenTrue) || ContainsLambdaOrMethodRef(cond.WhenFalse)));
            if (!hasTernaryWithLambda)
                javaType = "var";
        }
        bool wasConvertedFromVar = false;
        if (javaType == "var" && hasNoInitializer && context.SemanticModel != null)
        {
            foreach (var variable in stmt.Declaration.Variables)
            {
                var localSym = context.SemanticModel.GetDeclaredSymbol(variable) as ILocalSymbol;
                if (localSym?.Type != null && localSym.Type is not IErrorTypeSymbol)
                {
                    javaType = context.MapType(localSym.Type);
                    wasConvertedFromVar = true;
                    break;
                }
            }
        }

        // If this is a primitive-typed variable, check if it's used as an out-argument to TryGetValue.
        // In that case, Java can't compare the result of map.get() (Integer) to null when assigned to
        // a primitive (int). Use the boxed type (Integer, Long, etc.) instead.
        // This applies whether or not the variable has an initializer (e.g. "int x = 0" also needs boxing
        // when used as out-param to TryGetValue).
        {
            string? boxedType = javaType switch
            {
                "int" => "Integer", "long" => "Long", "double" => "Double",
                "float" => "Float", "boolean" => "Boolean", "short" => "Short",
                "byte" => "Byte", "char" => "Character", _ => null
            };
            if (boxedType != null && stmt.Parent is BlockSyntax parentBlock)
            {
                foreach (var variable in stmt.Declaration.Variables)
                {
                    var varName = variable.Identifier.Text;
                    bool usedAsTryGetValueOut = parentBlock.DescendantNodes()
                        .OfType<InvocationExpressionSyntax>()
                        .Any(inv => inv.Expression is MemberAccessExpressionSyntax ma2
                            && ma2.Name.Identifier.Text is "TryGetValue" or "TryGetComponent"
                            && inv.ArgumentList.Arguments.Any(a =>
                                a.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
                                && a.Expression is IdentifierNameSyntax id
                                && id.Identifier.Text == varName));
                    if (usedAsTryGetValueOut)
                    {
                        javaType = boxedType;
                        break;
                    }
                }
            }
        }

        // Consumer<T> → BiConsumer<Object, T> when the initializer is a 2-parameter lambda.
        // C# EventHandler<T>(object sender, T e) maps to Consumer<T> by default, but Java's
        // Consumer accepts only 1 arg; detect 2-param lambdas and upgrade to BiConsumer.
        if (javaType.StartsWith("Consumer<", StringComparison.Ordinal)
            && stmt.Declaration.Variables.Count == 1
            && stmt.Declaration.Variables[0].Initializer?.Value is ParenthesizedLambdaExpressionSyntax biLambda
            && biLambda.ParameterList.Parameters.Count == 2)
        {
            var innerType = javaType.Substring("Consumer<".Length, javaType.Length - "Consumer<".Length - 1);
            javaType = $"BiConsumer<Object, {innerType}>";
            context.AddImport("java.util.function.BiConsumer");
        }

        var exprTransformer = ExpressionTransformerFacade.Instance;

        // Special case: var x = target.Property = value
        // Property setters return void in Java — split into two statements: "Type x = value; target.setProperty(x);"
        if (stmt.Declaration.Variables.Count == 1)
        {
            var sv = stmt.Declaration.Variables[0];
            if (sv.Initializer?.Value is AssignmentExpressionSyntax assignInit
                && assignInit.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression))
            {
                // Check if the LHS of the initializer-assignment is a property setter
                bool lhsIsProp = false;
                if (assignInit.Left is MemberAccessExpressionSyntax maLhsCheck)
                {
                    var symCheck = context.SemanticModel?.GetSymbolInfo(maLhsCheck).Symbol;
                    lhsIsProp = symCheck is IPropertySymbol || (symCheck == null && char.IsUpper(maLhsCheck.Name.Identifier.Text[0]));
                }
                else if (assignInit.Left is IdentifierNameSyntax idLhsCheck)
                {
                    var symCheck = context.SemanticModel?.GetSymbolInfo(idLhsCheck).Symbol;
                    lhsIsProp = symCheck is IPropertySymbol;
                }
                if (lhsIsProp)
                {
                    var varName = ConversionContext.EscapeJavaKeyword(sv.Identifier.Text);
                    var rhsExpr = exprTransformer.Transform(assignInit.Right, context);
                    // Build setter call using varName as the value argument
                    string setterCode;
                    if (assignInit.Left is MemberAccessExpressionSyntax maLhsSet)
                    {
                        var maTarget = exprTransformer.Transform(maLhsSet.Expression, context);
                        var maPropName = maLhsSet.Name.Identifier.Text;
                        setterCode = $"{maTarget}.set{maPropName}({varName})";
                    }
                    else if (assignInit.Left is IdentifierNameSyntax idLhsSet)
                    {
                        setterCode = $"set{idLhsSet.Identifier.Text}({varName})";
                    }
                    else
                    {
                        setterCode = exprTransformer.Transform(assignInit, context);
                    }
                    var preStmts = context.DrainPreStatements();
                    var preCode = preStmts.Count > 0 ? string.Join("\n", preStmts.Select(s => s.TrimEnd(';') + ";")) + "\n" : "";
                    return new JavaMemberCollection(
                        new JavaStatementNode($"{preCode}{javaType} {varName} = {rhsExpr};"),
                        new JavaStatementNode($"{setterCode};"));
                }
            }
        }

        var declarations = string.Join(", ", stmt.Declaration.Variables.Select(v =>
        {
            string init;
            if (v.Initializer != null)
            {
                var localTargetType = context.SemanticModel?.GetDeclaredSymbol(v) switch
                {
                    ILocalSymbol localSymbol => localSymbol.Type,
                    _ => null
                };
                var initExpr = exprTransformer.Transform(v.Initializer.Value, context);
                // When the declared type is a primitive array (e.g., int[]) and the initializer is
                // a generic method whose original return type is T[] (type-parameter array),
                // Java generics substitute T with the boxed type (Integer[]) — we must unbox it.
                if (javaType is "int[]" or "long[]" or "double[]" or "float[]" or "boolean[]"
                    && v.Initializer.Value is InvocationExpressionSyntax unboxInvExpr
                    && context.SemanticModel != null)
                {
                    var invSym2 = context.SemanticModel.GetSymbolInfo(unboxInvExpr).Symbol as IMethodSymbol;
                    if (invSym2?.OriginalDefinition.ReturnType is IArrayTypeSymbol origRetArr2
                        && origRetArr2.ElementType is ITypeParameterSymbol
                        // Skip if already converted by TransformLinqToArray (contains mapToDouble/mapToInt etc.)
                        && !initExpr.Contains(".mapToDouble(") && !initExpr.Contains(".mapToInt(") && !initExpr.Contains(".mapToLong("))
                    {
                        // The method returns T[] in Java (boxed array, e.g. Integer[]); use Arrays.stream() to unbox.
                        // Java Arrays.stream only supports int[], long[], double[] for primitive arrays.
                        // float[] and boolean[] have no direct stream unboxing support; leave as-is.
                        if (javaType is "int[]" or "long[]" or "double[]")
                            context.AddImport("java.util.Arrays");
                        initExpr = javaType switch
                        {
                            "int[]" => $"Arrays.stream({initExpr}).mapToInt(Integer::intValue).toArray()",
                            "long[]" => $"Arrays.stream({initExpr}).mapToLong(Long::longValue).toArray()",
                            "double[]" => $"Arrays.stream({initExpr}).mapToDouble(Double::doubleValue).toArray()",
                            _ => initExpr
                        };
                    }
                }

                // Fix: C# arrays implement IEnumerable/ICollection/IList, so assigning an array directly
                // to IList<T>/ICollection<T> is valid C#. In Java, arrays are NOT Collection subtypes.
                // When the declared Java type is a collection interface and the initializer is an array,
                // wrap with Arrays.asList() (reference) or Arrays.stream().boxed().collect() (primitives).
                if (context.SemanticModel != null
                    && IsJavaCollectionOrListType(javaType)
                    && !initExpr.Contains("Arrays.asList(")
                    && !initExpr.Contains("Arrays.stream(")
                    && !initExpr.Contains("IntStream.range(")
                    && !initExpr.Contains(".collect("))
                {
                    var initTypeInfo = context.SemanticModel.GetTypeInfo(v.Initializer.Value);
                    if (initTypeInfo.Type is IArrayTypeSymbol arrayType)
                    {
                        initExpr = ObjectCreationTransformer.WrapArrayForCollectionArg(initExpr, arrayType, context);
                    }
                }

                // Fix K3: When the C# declared type is IEnumerable<T>/ICollection<T>/IList<T> (→ Java Iterable<T>)
                // but the initializer ends with .toArray(T[]::new), the assignment would fail because
                // T[] is NOT Iterable<T> in Java. Replace .toArray(T[]::new) with .collect(Collectors.toCollection(ArrayList::new)).
                if ((javaType.StartsWith("Iterable<") || javaType.StartsWith("List<") || javaType.StartsWith("Collection<"))
                    && System.Text.RegularExpressions.Regex.IsMatch(initExpr.TrimEnd(), @"\.toArray\([^)]+::new\)$"))
                {
                    initExpr = System.Text.RegularExpressions.Regex.Replace(
                        initExpr.TrimEnd(), @"\.toArray\([^)]+::new\)$", ".collect(Collectors.toCollection(ArrayList::new))");
                    context.AddImport("java.util.stream.Collectors");
                    context.AddImport("java.util.ArrayList");
                }

                // For locals mapped to Java "var" whose semantic type is IEnumerable/ICollection/IList,
                // Java would otherwise infer Stream<T> from LINQ chains. Materialize eagerly.
                // This applies both to implicit "var" and explicit IEnumerable<T> declarations,
                // because IEnumerable<T> is intentionally lowered to Java var in this transformer.
                if (javaType == "var" && context.SemanticModel != null)
                {
                    var localSym = context.SemanticModel.GetDeclaredSymbol(v) as ILocalSymbol;
                    bool semanticTypeIsEnumerableLike = localSym?.Type is INamedTypeSymbol localNamed
                        && localNamed.Name is "IEnumerable" or "IOrderedEnumerable" or "ICollection" or "IList";
                    bool looksLikeStreamExpr = !initExpr.Contains(".collect(Collectors.toCollection(ArrayList::new))")
                        && !initExpr.TrimEnd().EndsWith(".toArray()")
                        && !System.Text.RegularExpressions.Regex.IsMatch(initExpr.TrimEnd(), @"\.toArray\([^)]*\)$")
                        && (initExpr.Contains(".sorted(") || initExpr.Contains(".filter(") ||
                            initExpr.Contains(".map(") || initExpr.Contains(".flatMap(") ||
                            initExpr.Contains("StreamSupport.stream(") || initExpr.Contains("Arrays.stream(") ||
                            initExpr.Contains("IntStream.range(") ||
                            initExpr.Contains(".stream()") || initExpr.Contains("Stream.concat(") ||
                            initExpr.Contains(".distinct(") || initExpr.Contains(".limit(") ||
                            initExpr.Contains(".skip(") || initExpr.Contains(".peek("));

                    if (semanticTypeIsEnumerableLike && looksLikeStreamExpr)
                    {
                        initExpr = $"{initExpr}.collect(Collectors.toCollection(ArrayList::new))";
                        context.AddImport("java.util.stream.Collectors");
                        context.AddImport("java.util.ArrayList");
                    }

                    // C# arrays implement IEnumerable<T>/ICollection<T>/IList<T> implicitly.
                    // When the declared C# type was IEnumerable<T> (→ javaType was "Iterable<T>",
                    // then lowered to "var"), the initializer may be an array. Java arrays do NOT
                    // implement Iterable, so we must wrap with Arrays.asList() / Arrays.stream().
                    if (semanticTypeIsEnumerableLike
                        && !initExpr.Contains("Arrays.asList(")
                        && !initExpr.Contains("Arrays.stream(")
                        && !initExpr.Contains("IntStream.range(")
                        && !initExpr.Contains(".collect("))
                    {
                        var initExprTypeInfo = context.SemanticModel.GetTypeInfo(v.Initializer!.Value);
                        var arrayTypeSymbol = initExprTypeInfo.Type as IArrayTypeSymbol
                            ?? initExprTypeInfo.ConvertedType as IArrayTypeSymbol;
                        if (arrayTypeSymbol != null)
                            initExpr = ObjectCreationTransformer.WrapArrayForCollectionArg(initExpr, arrayTypeSymbol, context);
                    }
                }

                // Track stream-typed local variables for subsequent for-each statements.
                // When the initializer is a Java stream expression (not already collected), register
                // the variable name so TransformForEachStatement can detect it.
                // Guard: never register array-typed variables (T[]) as streams — arrays are
                // directly iterable in Java and must not be collected in for-each loops.
                {
                    bool initLooksLikeStream = !initExpr.Contains(".collect(Collectors.toCollection(ArrayList::new))")
                        // If it ends with .toArray(...), the stream was already terminated to an array —
                        // the variable is T[], not a stream, so do NOT register it as a stream variable.
                        && !initExpr.TrimEnd().EndsWith(".toArray()")
                        && !System.Text.RegularExpressions.Regex.IsMatch(initExpr.TrimEnd(), @"\.toArray\([^)]*\)$")
                        && (initExpr.Contains(".sorted(") || initExpr.Contains(".filter(") ||
                            initExpr.Contains(".map(") || initExpr.Contains(".flatMap(") ||
                            initExpr.Contains("StreamSupport.stream(") || initExpr.Contains("Arrays.stream(") ||
                            initExpr.Contains("IntStream.range(") ||
                            initExpr.Contains(".stream()") || initExpr.Contains("Stream.concat(") ||
                            initExpr.Contains(".distinct(") || initExpr.Contains(".limit(") ||
                            initExpr.Contains(".skip(") || initExpr.Contains(".peek("));
                    // Do NOT register if the variable's Java type is an array (e.g. Point[]).
                    // Arrays are directly iterable in Java; they are never Java streams.
                    bool isArrayJavaType = javaType.EndsWith("[]");
                    // Also check via semantic model: if the variable's C# type is an array, skip registration.
                    bool isSemanticArrayType = false;
                    if (context.SemanticModel != null)
                    {
                        var localSymForStream = context.SemanticModel.GetDeclaredSymbol(v) as ILocalSymbol;
                        isSemanticArrayType = localSymForStream?.Type is IArrayTypeSymbol;
                    }
                    if (initLooksLikeStream && !isArrayJavaType && !isSemanticArrayType)
                        context.StreamLocalVariables.Add(v.Identifier.Text);
                }
                // Struct value copy: In C# struct assignment copies the value; in Java it copies the reference.
                // Insert .clone() for user-defined struct initializers that are not fresh temporaries.
                if (context.SemanticModel != null && v.Initializer != null)
                {
                    var initValueType = context.SemanticModel.GetTypeInfo(v.Initializer.Value).Type;
                    initExpr = StructCloneHelper.CloneStructValueIfNeeded(v.Initializer.Value, initExpr, initValueType, context);
                }
                initExpr = ExpressionTransformerHelpers.AdaptExpressionToTargetType(
                    v.Initializer.Value,
                    initExpr,
                    localTargetType,
                    context);
                init = $" = {initExpr}";
            }
            else if (wasConvertedFromVar && javaType != "var" && javaType != "Object")
            {
                // The original C# used 'var' without initializer — Java needs a type with default.
                // Use type-appropriate defaults: primitives get their zero value, reference types get null.
                init = javaType switch
                {
                    "int" or "short" or "byte" or "long" or "char" => " = 0",
                    "double" or "float" => " = 0.0",
                    "boolean" => " = false",
                    _ => " = null"  // reference type
                };
            }
            else
            {
                init = "";
            }
            return $"{ConversionContext.EscapeJavaKeyword(v.Identifier.Text)}{init}";
        }));

        string localDeclPreCode = "";
        if (context.HasPendingPreStatements)
        {
            var pendingPre = context.DrainPreStatements();
            localDeclPreCode = string.Join("\n", pendingPre.Select(s => s.TrimEnd(';') + ";")) + "\n";
        }

        string localDeclPostCode = "";
        if (context.HasPendingPostStatements)
        {
            var pendingPost = context.DrainPostStatements();
            localDeclPostCode = "\n" + string.Join("\n", pendingPost.Select(s => s.TrimEnd(';') + ";"));
        }

        return new JavaStatementNode($"{localDeclPreCode}{javaType} {declarations};{localDeclPostCode}");
    }

    private JavaSyntaxNode TransformYieldReturn(YieldStatementSyntax? stmt, ConversionContext context)
    {
        if (stmt?.Expression == null)
            return new JavaStatementNode("// yield return (empty)");
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var expr = exprTransformer.Transform(stmt.Expression, context);
        return new JavaStatementNode($"_yieldResult.add({expr});");
    }

    private JavaSyntaxNode TransformYieldBreak(YieldStatementSyntax? stmt, ConversionContext context)
    {
        return new JavaStatementNode("return _yieldResult;");
    }

    /// <summary>
    /// Returns true when <paramref name="javaType"/> is a Java collection/list type whose
    /// constructor or assignment slot requires a <c>Collection</c>-compatible value.
    /// Excludes <c>Iterable</c> because that is already replaced by <c>var</c> earlier.
    /// </summary>
    private static bool IsJavaCollectionOrListType(string javaType)
    {
        // An array of collections (e.g. ArrayList<Integer>[]) is NOT itself a collection type.
        if (javaType.TrimEnd().EndsWith("[]"))
            return false;
        var bare = javaType.Contains('<') ? javaType[..javaType.IndexOf('<')] : javaType;
        return bare is "List" or "Collection" or "ArrayList" or "HashSet" or "TreeSet"
            or "LinkedList" or "LinkedHashSet" or "ArrayDeque" or "Stack" or "Vector"
            or "Set" or "Deque" or "Queue";
    }

    /// <summary>
    /// Checks whether an expression contains a lambda or method reference.
    /// Used to detect ternary expressions that Java var can't infer.
    /// </summary>
    private static bool ContainsLambdaOrMethodRef(ExpressionSyntax expr)
    {
        if (expr is ParenthesizedExpressionSyntax paren)
            return ContainsLambdaOrMethodRef(paren.Expression);
        if (expr is CastExpressionSyntax cast)
            return ContainsLambdaOrMethodRef(cast.Expression);
        return expr is SimpleLambdaExpressionSyntax
            or ParenthesizedLambdaExpressionSyntax
            or AnonymousMethodExpressionSyntax
            || (expr is MemberAccessExpressionSyntax && expr.Parent is ArgumentSyntax);
    }

    /// <summary>
    /// Returns a Java default-value expression for a C# value type, or null for reference types.
    /// Used to generate getOrDefault() calls for TryGetValue on value-type dictionary values.
    /// </summary>
    private static string? GetValueTypeDefault(ITypeSymbol? typeSymbol, string javaTypeName)
    {
        if (typeSymbol == null || !typeSymbol.IsValueType)
            return null;

        return javaTypeName switch
        {
            "int" => "0",
            "long" => "0L",
            "short" => "(short)0",
            "byte" => "(byte)0",
            "float" => "0.0f",
            "double" => "0.0",
            "boolean" => "false",
            "char" => "'\\0'",
            _ => $"new {javaTypeName}()"
        };
    }

    /// <summary>
    /// Converts a string to PascalCase by capitalizing the first letter.
    /// Used for generating JavaBean-compliant getter method names.
    /// </summary>
    private static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToUpper(name[0]) + name.Substring(1);
    }
}

/// <summary>
/// Java 语句节点
/// </summary>
internal class JavaStatementNode : JavaSyntaxNode
{
    private readonly string _statement;

    public JavaStatementNode(string statement)
    {
        _statement = statement;
    }

    public override string ToString(string indentation)
    {
        return _statement;
    }
}



