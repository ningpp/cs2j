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

public partial class StatementTransformer
{
    private JavaSyntaxNode TransformExpressionStatement(ExpressionStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;

        // ConditionalAccessExpression (obj?.Method()) as a statement cannot be a ternary expression in Java.
        // Java only allows method calls, assignments, and new as expression statements.
        // Convert to: if (obj != null) { obj.Method(); }
        if (stmt.Expression is ConditionalAccessExpressionSyntax condAccess)
        {
            var objExpr = exprTransformer.Transform(condAccess.Expression, context);
            var innerCall = TransformConditionalAccessStatement(condAccess, objExpr, context);
            return new JavaStatementNode(innerCall);
        }

        // Special case: dict.TryGetValue(key, out var v) as a standalone statement.
        // For standard IDictionary implementations (mapped to Java Map), use containsKey() + get().
        // For custom dictionaries (e.g. LowLevelDictionary), use tryGetValue(key, ObjectHolder).
        // C# TryGetValue default-initializes the out parameter when the key is not found.
        if (stmt.Expression is InvocationExpressionSyntax tvInvoc &&
            tvInvoc.Expression is MemberAccessExpressionSyntax tvMa &&
            tvMa.Name.Identifier.Text == "TryGetValue" &&
            tvInvoc.ArgumentList.Arguments.Count == 2)
        {
            var tvTarget = exprTransformer.Transform(tvMa.Expression, context);
            var tvKey = exprTransformer.Transform(tvInvoc.ArgumentList.Arguments[0].Expression, context);
            var tvArg2 = tvInvoc.ArgumentList.Arguments[1];

            // Determine if the receiver is a standard IDictionary (mapped to Java Map with containsKey)
            // or a custom dictionary (needs tryGetValue with ObjectHolder)
            bool isStandardMap = false;
            if (context.SemanticModel != null)
            {
                var receiverTypeInfo = context.GetTypeInfo(tvMa.Expression);
                if (receiverTypeInfo.Type != null)
                {
                    isStandardMap = receiverTypeInfo.Type.AllInterfaces.Any(i =>
                        i.OriginalDefinition?.ToDisplayString() == "System.Collections.Generic.IDictionary<TKey, TValue>");
                    // Also check if the type itself is IDictionary
                    if (!isStandardMap && receiverTypeInfo.Type is INamedTypeSymbol named)
                    {
                        isStandardMap = named.OriginalDefinition?.ToDisplayString() == "System.Collections.Generic.IDictionary<TKey, TValue>";
                    }
                }
            }

            if (tvArg2.Expression is DeclarationExpressionSyntax tvDecl2)
            {
                var declType = context.GetTypeInfo(tvDecl2.Type);
                var javaType = declType.Type != null ? context.MapType(declType.Type) : "var";
                var varName = tvDecl2.Designation is SingleVariableDesignationSyntax sv ? sv.Identifier.Text : "_outVar";
                var defaultVal = GetValueTypeDefault(declType.Type, javaType);
                if (declType.Type?.SpecialType == SpecialType.System_Decimal)
                    context.AddImport("io.github.ningpp.compat.Decimal");

                if (isStandardMap)
                {
                    // Standard Java Map: use containsKey + get/getOrDefault
                    var getCall = defaultVal != null
                        ? $"{tvTarget}.getOrDefault({tvKey}, {defaultVal})"
                        : $"{tvTarget}.get({tvKey})";
                    return new JavaStatementNode($"{javaType} {varName} = {getCall};");
                }
                else
                {
                    // Custom dictionary (e.g. LowLevelDictionary): use tryGetValue(key, ObjectHolder)
                    const string ObjectHolderFqn = "io.github.ningpp.compat.ObjectHolder";
                    var holderName = $"_{varName}Holder";
                    var holderType = javaType == "var" ? "var" : $"{ObjectHolderFqn}<{javaType}>";
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"{holderType} {holderName} = new {ObjectHolderFqn}<>();");
                    sb.Append($"{tvTarget}.tryGetValue({tvKey}, {holderName});");
                    sb.Append($" {javaType} {varName} = {holderName}.value;");
                    return new JavaStatementNode(sb.ToString());
                }
            }
            else
            {
                var tvOut2 = exprTransformer.Transform(tvArg2.Expression, context);
                var outTypeInfo = context.GetTypeInfo(tvArg2.Expression);
                string? defaultVal = null;
                if (outTypeInfo.Type is { IsValueType: true } outType)
                {
                    defaultVal = GetValueTypeDefault(outType, context.MapType(outType));
                    if (outType.SpecialType == SpecialType.System_Decimal)
                        context.AddImport("io.github.ningpp.compat.Decimal");
                }

                if (isStandardMap)
                {
                    // Standard Java Map: use containsKey + get/getOrDefault
                    var getCall = defaultVal != null
                        ? $"{tvTarget}.getOrDefault({tvKey}, {defaultVal})"
                        : $"{tvTarget}.get({tvKey})";
                    return new JavaStatementNode($"{tvOut2} = {getCall};");
                }
                else
                {
                    // Custom dictionary (e.g. LowLevelDictionary): use tryGetValue(key, ObjectHolder)
                    const string ObjectHolderFqn = "io.github.ningpp.compat.ObjectHolder";
                    var outJavaType = outTypeInfo.Type != null ? context.MapType(outTypeInfo.Type) : "Object";
                    var holderName = context.GenerateSyntheticName("_outHolder");
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"{ObjectHolderFqn}<{outJavaType}> {holderName} = new {ObjectHolderFqn}<>();");
                    sb.Append($"{tvTarget}.tryGetValue({tvKey}, {holderName});");
                    sb.Append($" {tvOut2} = {holderName}.value;");
                    return new JavaStatementNode(sb.ToString());
                }
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
                var keyArr = context.GetTypeInfo(arg0Expr).Type as IArrayTypeSymbol;
                var itemArr = context.GetTypeInfo(arg1Expr).Type as IArrayTypeSymbol;

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

        // Debug.Assert / Trace.Assert / Contract.Requires / Contract.Assert
        // Debug.Assert is compiled out in C# Release builds ([Conditional("DEBUG")]).
        // Trace.Assert remains in Release. Contract.Assert/Requires require CONTRACTS_FULL symbol.
        // Java's 'assert' is runtime-gated (-ea), not equivalent to compile-time removal.
        // → strip Debug/Contract asserts as comments; keep Trace.Assert as `assert`.
        if (stmt.Expression is InvocationExpressionSyntax assertInvoc &&
            assertInvoc.Expression is MemberAccessExpressionSyntax assertMa &&
            assertInvoc.ArgumentList.Arguments.Count >= 1 &&
            assertMa.Name.Identifier.Text is "Assert" or "Requires")
        {
            bool isTrace = false;
            bool isDebugOrContract = false;
            if (context.SemanticModel != null &&
                context.GetSymbolInfo(assertInvoc).Symbol is IMethodSymbol assertSym)
            {
                var typeName = assertSym.ContainingType.ToDisplayString();
                isTrace = typeName == "System.Diagnostics.Trace";
                isDebugOrContract = typeName is "System.Diagnostics.Debug"
                                             or "System.Diagnostics.Contracts.Contract";
            }
            if (!isTrace && !isDebugOrContract)
            {
                // Syntactic fallback: semantic model absent or couldn't resolve the symbol
                var receiver = assertMa.Expression.ToString();
                isTrace = receiver is "Trace" or "System.Diagnostics.Trace";
                isDebugOrContract = receiver is "Debug" or "Contract"
                                             or "System.Diagnostics.Debug"
                                             or "System.Diagnostics.Contracts.Contract";
            }

            if (isTrace)
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

            if (isDebugOrContract)
            {
                var condition = exprTransformer.Transform(assertInvoc.ArgumentList.Arguments[0].Expression, context);
                string comment;
                if (assertInvoc.ArgumentList.Arguments.Count >= 2)
                {
                    var message = exprTransformer.Transform(assertInvoc.ArgumentList.Arguments[1].Expression, context);
                    comment = $"// Debug.Assert({condition}, {message});\n";
                }
                else
                {
                    comment = $"// Debug.Assert({condition});\n";
                }
                // Drain pre/post statements even for comments (they were emitted during transform)
                // but discard them since the assert is a no-op
                if (context.HasPendingPreStatements) context.DrainPreStatements();
                if (context.HasPendingPostStatements) context.DrainPostStatements();
                return new JavaStatementNode(comment);
            }
        }

        // Debug.WriteLineIf / Trace.WriteLineIf / Debug.WriteIf / Trace.WriteIf
        // Debug versions are [Conditional("DEBUG")]; Trace versions are [Conditional("TRACE")].
        // Convert Trace versions to guarded System.out.println/print; strip Debug versions
        // to match C# Release semantics. This must happen before the generic expression
        // transformer, which would otherwise treat the boolean condition as a format string
        // and emit String.format(boolean, String) — invalid Java.
        if (stmt.Expression is InvocationExpressionSyntax writeIfInvoc &&
            writeIfInvoc.Expression is MemberAccessExpressionSyntax writeIfMa &&
            writeIfInvoc.ArgumentList.Arguments.Count >= 2 &&
            writeIfMa.Name.Identifier.Text is "WriteLineIf" or "WriteIf")
        {
            bool isDebug = false;
            bool isTrace = false;
            if (context.SemanticModel != null &&
                context.GetSymbolInfo(writeIfInvoc).Symbol is IMethodSymbol writeIfSym)
            {
                var typeName = writeIfSym.ContainingType.ToDisplayString();
                isDebug = typeName == "System.Diagnostics.Debug";
                isTrace = typeName == "System.Diagnostics.Trace";
            }
            if (!isDebug && !isTrace)
            {
                var receiver = writeIfMa.Expression.ToString();
                isDebug = receiver is "Debug" or "System.Diagnostics.Debug";
                isTrace = receiver is "Trace" or "System.Diagnostics.Trace";
            }

            if (isDebug || isTrace)
            {
                var condition = exprTransformer.Transform(writeIfInvoc.ArgumentList.Arguments[0].Expression, context);
                var message = exprTransformer.Transform(writeIfInvoc.ArgumentList.Arguments[1].Expression, context);
                string writeIfStmt;
                if (isTrace)
                {
                    var printMethod = writeIfMa.Name.Identifier.Text == "WriteLineIf" ? "println" : "print";
                    writeIfStmt = $"if ({condition}) System.out.{printMethod}({message});";
                }
                else
                {
                    writeIfStmt = writeIfMa.Name.Identifier.Text == "WriteLineIf"
                        ? $"// Debug.WriteLineIf({condition}, {message});\n"
                        : $"// Debug.WriteIf({condition}, {message});\n";
                }

                if (context.HasPendingPreStatements || context.HasPendingPostStatements)
                {
                    var sb = new System.Text.StringBuilder();
                    if (context.HasPendingPreStatements)
                    {
                        var preStmts = context.DrainPreStatements();
                        sb.AppendLine(string.Join("\n", preStmts.Select(s => s.TrimEnd(';') + ";")));
                    }
                    sb.Append(writeIfStmt);
                    if (context.HasPendingPostStatements)
                    {
                        var postStmts = context.DrainPostStatements();
                        sb.Append("\n" + string.Join("\n", postStmts.Select(s => s.TrimEnd(';') + ";")));
                    }
                    return new JavaStatementNode(sb.ToString());
                }

                return new JavaStatementNode(writeIfStmt);
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
                string getter = $"_{i + 1}";
                if (i < names.Count - 1)
                    sbTuple.AppendLine($"var {ConversionContext.EscapeJavaKeyword(names[i])} = {tempVar}.{getter}();");
                else
                    sbTuple.Append($"var {ConversionContext.EscapeJavaKeyword(names[i])} = {tempVar}.{getter}();");
            }
            return new JavaStatementNode(sbTuple.ToString());
        }

        // Fix 4b: Typed tuple deconstruction — (Type a, Type b) = value;
        // ExpressionStatement > AssignmentExpression where LHS is TupleExpression with DeclarationExpression arguments
        if (stmt.Expression is AssignmentExpressionSyntax typedTupleAssign
            && typedTupleAssign.Left is TupleExpressionSyntax tupleExpr)
        {
            var declArgs = tupleExpr.Arguments
                .Select(a => a.Expression)
                .OfType<DeclarationExpressionSyntax>()
                .Where(d => d.Designation is SingleVariableDesignationSyntax)
                .ToList();
            if (declArgs.Count == tupleExpr.Arguments.Count && declArgs.Count > 0)
            {
                var rhsExpr = exprTransformer.Transform(typedTupleAssign.Right, context);
                var tempVar = "_t";
                var sbTuple = new System.Text.StringBuilder();
                sbTuple.AppendLine($"var {tempVar} = {rhsExpr};");
                for (int i = 0; i < declArgs.Count; i++)
                {
                    var svd = (SingleVariableDesignationSyntax)declArgs[i].Designation;
                    var varName = ConversionContext.EscapeJavaKeyword(svd.Identifier.Text);
                    string getter = $"_{i + 1}";
                    if (i < declArgs.Count - 1)
                        sbTuple.AppendLine($"var {varName} = {tempVar}.{getter}();");
                    else
                        sbTuple.Append($"var {varName} = {tempVar}.{getter}();");
                }
                return new JavaStatementNode(sbTuple.ToString());
            }
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
            bool isNonStatement = IsBareReadExpression(expr);
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

    /// <summary>
    /// Transforms a C# conditional access expression used as a statement into a Java
    /// <c>if (x != null) { ... }</c> chain. Nested conditional accesses become nested
    /// <c>if</c> statements so that the generated code is a valid Java statement.
    /// </summary>
    private static string TransformConditionalAccessStatement(
        ConditionalAccessExpressionSyntax condAccess,
        string objExpr,
        ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
        string innerCall;
        switch (condAccess.WhenNotNull)
        {
            case MemberBindingExpressionSyntax binding:
                innerCall = $"{objExpr}.{ConversionContext.EscapeJavaKeyword(binding.Name.Identifier.Text)};";
                break;
            case InvocationExpressionSyntax invocation when invocation.Expression is MemberBindingExpressionSyntax invokeBinding:
                var methodName = ConversionContext.EscapeJavaKeyword(invokeBinding.Name.Identifier.Text);
                // Delegate .Invoke() → SAM method: Invoke is not a valid Java method
                // on functional interfaces. Map to run/accept/get/apply based on usage.
                if (methodName == "Invoke")
                {
                    int paramCount = invocation.ArgumentList.Arguments.Count;
                    methodName = Transformers.Type.DelegateTransformer.InferSamMethodName(
                        returnsVoid: true, paramCount); // statement context is always void
                }
                var args = string.Join(", ", invocation.ArgumentList.Arguments.Select(a => exprTransformer.Transform(a.Expression, context)));
                innerCall = $"{objExpr}.{methodName}({args});";
                break;
            case ConditionalAccessExpressionSyntax nestedCondAccess:
                var nestedObjExpr = exprTransformer.TransformWhenNotNull(nestedCondAccess.Expression, objExpr, context);
                innerCall = TransformConditionalAccessStatement(nestedCondAccess, nestedObjExpr, context);
                break;
            default:
                // General case: ?.a.b(...) — recursively substitute the member binding with objExpr
                innerCall = exprTransformer.TransformWhenNotNull(condAccess.WhenNotNull, objExpr, context) + ";";
                break;
        }
        return $"if ({objExpr} != null) {{ {innerCall} }}";
    }

    private static bool IsBareReadExpression(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return false;

        var trimmed = expr.Trim();
        return trimmed.EndsWith(".value", StringComparison.Ordinal)
            || trimmed.Split('.').All(IsIdentifierLikeSegment);
    }

    private static bool IsIdentifierLikeSegment(string segment)
    {
        return segment.Length > 0
            && (char.IsLetter(segment[0]) || segment[0] == '_' || segment[0] == '$')
            && segment.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '$');
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
            if (context.IsInAsyncContext)
            {
                context.AddImport("java.util.concurrent.CompletableFuture");
                return new JavaStatementNode("return CompletableFuture.completedFuture(null);");
            }
            return new JavaStatementNode("return;");
        }

        var exprTransformer = ExpressionTransformerFacade.Instance;

        // Special case: return dict.TryGetValue(key, out T v) ? v : null;
        // The generic out-var path produces convoluted holder-based code; replace with
        // a clean dict.get(key) which is semantically equivalent (both return null when
        // the key is absent or the value is null).
        if (stmt.Expression is ConditionalExpressionSyntax ternary
            && ternary.WhenTrue is IdentifierNameSyntax trueIdent
            && ternary.WhenFalse.IsKind(SyntaxKind.NullLiteralExpression)
            && ternary.Condition is InvocationExpressionSyntax tryGetInvoke
            && tryGetInvoke.Expression is MemberAccessExpressionSyntax tryGetMa
            && tryGetMa.Name.Identifier.Text == "TryGetValue"
            && tryGetInvoke.ArgumentList.Arguments.Count == 2
            && tryGetInvoke.ArgumentList.Arguments[1].Expression is DeclarationExpressionSyntax outDecl
            && outDecl.Designation is SingleVariableDesignationSyntax svd
            && svd.Identifier.Text == trueIdent.Identifier.Text)
        {
            var dictExpr = exprTransformer.Transform(tryGetMa.Expression, context);
            var keyExpr = exprTransformer.Transform(tryGetInvoke.ArgumentList.Arguments[0].Expression, context);
            var typeInfo = context.GetTypeInfo(outDecl.Type);
            var javaType = typeInfo.Type != null
                ? context.MapType(typeInfo.Type) : "var";
            var varName = ConversionContext.EscapeJavaKeyword(svd.Identifier.Text);
            var defaultVal = GetValueTypeDefault(typeInfo.Type, javaType);
            if (typeInfo.Type?.SpecialType == SpecialType.System_Decimal)
                context.AddImport("io.github.ningpp.compat.Decimal");
            var getCall = defaultVal != null
                ? $"{dictExpr}.getOrDefault({keyExpr}, {defaultVal})"
                : $"{dictExpr}.get({keyExpr})";
            var retExpr2 = context.IsInAsyncContext
                ? $"CompletableFuture.completedFuture({varName})"
                : varName;
            if (context.IsInAsyncContext)
                context.AddImport("java.util.concurrent.CompletableFuture");
            return new JavaStatementNode($"{javaType} {varName} = {getCall};\nreturn {retExpr2};");
        }

        var expr = exprTransformer.Transform(stmt.Expression, context);
        var returnTargetType = ResolveReturnTargetType(stmt, context);

        // Detect when a C# array element (from jagged array) is returned where a List<T> is expected.
        // e.g., return outEdges[vertex]; where the method returns IList<TEdge> → List<TEdge> in Java
        // and outEdges is TEdge[][] — element is TEdge[] which doesn't implement List<TEdge> in Java.
        if (stmt.Expression != null && context.SemanticModel != null)
        {
            var exprType = context.GetTypeInfo(stmt.Expression).Type;
            if (exprType is IArrayTypeSymbol { Rank: 1 } arrayType)
            {
                // Check if the enclosing method's return type is IList<T>, ICollection<T>, or IEnumerable<T>
                var enclosingMethod = stmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                ITypeSymbol? enclosingRetSym = null;
                if (enclosingMethod != null)
                    enclosingRetSym = context.GetTypeInfo(enclosingMethod.ReturnType).Type;
                // Also check property accessor (return in a get { } block)
                if (enclosingRetSym == null)
                {
                    var propAccessor = stmt.Ancestors().OfType<AccessorDeclarationSyntax>().FirstOrDefault();
                    if (propAccessor != null)
                    {
                        var propDecl = propAccessor.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
                        if (propDecl != null)
                            enclosingRetSym = context.GetTypeInfo(propDecl.Type).Type;
                    }
                }
                if (enclosingRetSym is INamedTypeSymbol retNamed &&
                    retNamed.Name is "IList" or "ICollection" or "List" or "Collection"
                        or "IEnumerable" or "Iterable")
                {
                    // Primitive arrays (int[], double[], etc.) can't use ArrayHelper.toList() directly
                    // because ArrayHelper.toList(int[]) returns List<int[]>, not List<Integer>.
                    if (arrayType.ElementType.SpecialType is
                        SpecialType.System_Int32 or SpecialType.System_Int64 or
                        SpecialType.System_Double or SpecialType.System_Single or
                        SpecialType.System_Boolean or SpecialType.System_Byte or
                        SpecialType.System_Int16 or SpecialType.System_Char)
                    {
                        // Use CSharpList.toCSharpList() instead of toList()
                        // because C# List<T> maps to Java ArrayList<T> (concrete), and
                        // Collectors.toList() returns List<T> (interface) — type mismatch.
                        expr = $"Arrays.stream({expr}).boxed().collect(java.util.stream.Collectors.toCollection(() -> new java.util.ArrayList<>()))";
                        context.AddImport("java.util.Arrays");
                        context.AddImport("java.util.stream.Collectors");
                        context.AddImport("java.util.ArrayList"); context.AddImport("io.github.ningpp.compat.CSharpList");
                        if (context.ReturnsCSharpGenericIterable)
                        {
                            context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
                            expr = $"CSharpGenericIterable.from({expr})";
                        }
                    }
                    else
                    {
                        expr = ExpressionTransformerHelpers.BuildArrayToCollectionViewExpression(expr, arrayType, context);
                    }
                }
            }

            // Detect when a Stream expression is returned from a method that declares Iterable/IEnumerable.
            // C# LINQ expressions become Java Streams but IEnumerable<T> maps to Iterable<T>.
            // Stream<T> does not implement Iterable<T>, so we need .collect(CSharpList.toCSharpList()).
            var retExprType = context.GetTypeInfo(stmt.Expression).Type;
            bool isStreamReturn = retExprType is INamedTypeSymbol retNamed2 &&
                (retNamed2.Name is "IEnumerable" or "IOrderedEnumerable" or "IQueryable") &&
                retNamed2.ContainingNamespace?.ToDisplayString().StartsWith("System") == true;
            if (isStreamReturn && ExpressionTransformerHelpers.ContainsStreamMethodAtTopLevel(expr))
            {
                // Check if enclosing method returns Iterable
                var enclosing = stmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                ITypeSymbol? enclosingRetType = enclosing != null
                    ? context.GetTypeInfo(enclosing.ReturnType).Type
                    : null;
                // Also check property accessor (return inside a get { } block)
                if (enclosingRetType == null)
                {
                    var accessorDecl = stmt.Ancestors().OfType<AccessorDeclarationSyntax>().FirstOrDefault();
                    if (accessorDecl != null)
                    {
                        var propDecl2 = accessorDecl.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
                        if (propDecl2 != null)
                            enclosingRetType = context.GetTypeInfo(propDecl2.Type).Type;
                    }
                }
                bool enclosingReturnsIterable = enclosingRetType is INamedTypeSymbol mret &&
                    mret.Name is "IEnumerable" or "ICollection" or "IList";
                // Don't double-collect: if the expression already contains .collect() or ends with .toArray()
                // it's already materialized. Use Contains for .collect() since it may appear at any depth
                // with varying closing parentheses (e.g., Concat produces Stream.concat(...).collect(CSharpList.toCSharpList())).
                bool alreadyCollected = expr.Contains(".collect(CSharpList.toCSharpList())")
                    || expr.EndsWith(".toList())")
                    || expr.EndsWith("toList()))")
                    || expr.EndsWith(".toArray())")
                    || System.Text.RegularExpressions.Regex.IsMatch(expr, @"\.toArray\([^)]+\)\)$")
                    || System.Text.RegularExpressions.Regex.IsMatch(expr, @"\.toArray\([^)]+\)$");
                if (enclosingReturnsIterable && !alreadyCollected)
                {
                    context.AddImport("java.util.stream.Collectors");
                    context.AddImport("java.util.ArrayList"); context.AddImport("io.github.ningpp.compat.CSharpList");
                    expr = $"{expr}.collect(CSharpList.toCSharpList())";
                    if (context.ReturnsCSharpGenericIterable)
                    {
                        context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
                        expr = $"CSharpGenericIterable.from({expr})";
                    }
                }
            }

            // Detect when a Dictionary/Map is returned from a method that expects
            // IEnumerable<KeyValuePair<K,V>> (→ CSharpGenericIterable<CSharpKeyValuePair<K,V>>).
            // CSharpDictionary already implements CSharpGenericIterable<CSharpKeyValuePair<K,V>>,
            // so no .entrySet() is needed — the dictionary itself is directly assignable.
            // However, when the method returns IEnumerable<IGrouping<K,V>>, the Dictionary
            // needs .entrySet() to produce Map.Entry<K,List<V>> items for the IGrouping pattern.
            if (retExprType is INamedTypeSymbol dictRetType &&
                (dictRetType.Name is "Dictionary" or "SortedDictionary" or "IDictionary" ||
                 dictRetType.AllInterfaces.Any(i => i.Name is "IDictionary")))
            {
                var enclosing2 = stmt.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                ITypeSymbol? enclosingRetType2 = enclosing2 != null
                    ? context.GetTypeInfo(enclosing2.ReturnType).Type
                    : null;
                if (enclosingRetType2 is INamedTypeSymbol encRet2 &&
                    encRet2.Name is "IEnumerable" or "ICollection" or "IList" or "Iterable")
                {
                    // Check if this is an IGrouping return type — still needs .entrySet()
                    bool isIGroupingReturn = encRet2.IsGenericType && encRet2.TypeArguments.Length > 0
                        && encRet2.TypeArguments[0] is INamedTypeSymbol elemType
                        && elemType.Name == "IGrouping";
                    if (isIGroupingReturn)
                    {
                        context.AddImport("java.util.Map");
                        context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
                        expr = $"CSharpGenericIterable.from({expr}.entrySet())";
                    }
                    // Otherwise, CSharpDictionary is already CSharpGenericIterable<CSharpKeyValuePair<K,V>>
                }
            }
        }

        // Struct value copy: when returning a user-defined struct expression that is not a temporary,
        // clone it to preserve C# value-copy semantics (C# return always copies structs).
        // Skip inside property getters where the consumption site handles cloning.
        if (stmt.Expression != null && context.SemanticModel != null && !context.SuppressReturnClone)
        {
            var retExprTypeForClone = context.GetTypeInfo(stmt.Expression).Type;
            expr = StructCloneHelper.CloneStructValueIfNeeded(stmt.Expression, expr, retExprTypeForClone, context);
        }

        expr = ExpressionTransformerHelpers.AdaptExpressionToTargetType(
            stmt.Expression,
            expr,
            returnTargetType,
            context);

        // When the method returns CSharpGenericIterable<T> but the expression is
        // a Java Collection/List/Set/Iterable (not already a CSharpGenericIterable),
        // wrap it in CSharpGenericIterable.from() so the types are compatible.
        // Skip IDictionary because CSharpDictionary already implements CSharpGenericIterable.
        if (context.ReturnsCSharpGenericIterable && context.SemanticModel != null)
        {
            var retExprType = context.GetTypeInfo(stmt.Expression).Type;
            bool isAlreadyCSharpGenericIterable = expr.StartsWith("CSharpGenericIterable.from(", StringComparison.Ordinal);
            bool isIDictionary = retExprType is INamedTypeSymbol dictType
                && (dictType.Name is "Dictionary" or "SortedDictionary" or "IDictionary"
                    || dictType.AllInterfaces.Any(i => i.Name == "IDictionary"));
            bool isEnumerableInterfaceType = retExprType is INamedTypeSymbol ifaceNamed
                && ifaceNamed.Name is "IEnumerable" or "ICollection" or "IList"
                    or "IReadOnlyCollection" or "IReadOnlyList"
                && ifaceNamed.ContainingNamespace?.ToDisplayString().StartsWith("System") == true;
            bool isJavaCollectionLike = !isAlreadyCSharpGenericIterable && !isIDictionary
                && (isEnumerableInterfaceType
                    || (retExprType is INamedTypeSymbol collNamed
                        && collNamed.AllInterfaces.Any(i =>
                            i.OriginalDefinition.ToDisplayString() is
                            "System.Collections.Generic.IEnumerable<T>" or
                            "System.Collections.Generic.ICollection<T>" or
                            "System.Collections.Generic.IList<T>" or
                            "System.Collections.Generic.ISet<T>")));
            if (isJavaCollectionLike)
            {
                context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
                expr = $"CSharpGenericIterable.from({expr})";
            }
        }

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
                var asyncRet = context.IsInAsyncContext
                    ? "CompletableFuture.completedFuture(_ret)"
                    : "_ret";
                sb.Append($"\nreturn {asyncRet};");
            }
            else
            {
                var asyncExpr = context.IsInAsyncContext
                    ? $"CompletableFuture.completedFuture({expr})"
                    : expr;
                sb.Append($"return {asyncExpr};");
            }
            if (context.IsInAsyncContext)
                context.AddImport("java.util.concurrent.CompletableFuture");
            return new JavaStatementNode(sb.ToString());
        }

        if (context.IsInAsyncContext)
        {
            context.AddImport("java.util.concurrent.CompletableFuture");
            return new JavaStatementNode($"return CompletableFuture.completedFuture({expr});");
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
            MethodDeclarationSyntax method => context.GetTypeInfo(method.ReturnType).Type,
            AccessorDeclarationSyntax accessor => (context.GetDeclaredSymbol(accessor) as IMethodSymbol)?.ReturnType,
            LocalFunctionStatementSyntax localFunction => context.GetTypeInfo(localFunction.ReturnType).Type,
            _ => null
        };
    }
}
