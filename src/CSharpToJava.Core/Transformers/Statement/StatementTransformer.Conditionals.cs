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

                // When the out variable is itself a ref/out parameter of the enclosing method,
                // it has been converted to a Holder type — use containsKey pattern instead of null check
                bool isOutParam = context.SemanticModel?.GetSymbolInfo(tvIfIdent).Symbol is IParameterSymbol negOutP
                    && (negOutP.RefKind == RefKind.Out || negOutP.RefKind == RefKind.Ref)
                    && !context.IsReadOnlyRefStructParam(negOutP.Name);

                var tvStmtTransformer2 = new StatementTransformer();
                string thenBody;
                if (stmt.Statement is BlockSyntax tvIfThenBlock2)
                    thenBody = $"{{\n        {TransformBlock(tvIfThenBlock2, context)}\n    }}";
                else
                    thenBody = $"{{ {tvStmtTransformer2.Transform(stmt.Statement, context).ToString("")} }}";

                var ifSb2 = new System.Text.StringBuilder();
                if (isOutParam)
                {
                    // Null check on .value won't work for primitives; use containsKey instead
                    // C#: if (!dict.TryGetValue(key, out outParam)) { A } [else { B }]
                    // Java: if (!dict.containsKey(key)) { A } else { outParam.value = dict.get(key); [B] }
                    ifSb2.Append($"if (!{tvTarget}.containsKey({tvKey})) {thenBody}");
                    // Always emit else to assign the value when key exists
                    string elseInner = $"{existingVarName}.value = {tvTarget}.get({tvKey});";
                    if (stmt.Else != null)
                    {
                        string elseBody;
                        if (stmt.Else.Statement is BlockSyntax tvIfElseBlock)
                            elseBody = TransformBlock(tvIfElseBlock, context);
                        else
                            elseBody = tvStmtTransformer2.Transform(stmt.Else.Statement, context).ToString("");
                        ifSb2.Append($" else {{\n        {elseInner}\n        {elseBody}\n    }}");
                    }
                    else
                    {
                        ifSb2.Append($" else {{\n        {elseInner}\n    }}");
                    }
                }
                else
                {
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
                }

                return new JavaStatementNode(ifSb2.ToString());
            }

            // Case C: out complexTarget (e.g., Result[i], obj.Field — any non-identifier, non-declaration)
            // For value-type targets, assignment of null (from get on missing key) would NPE on unboxing.
            // Use containsKey approach: if (!dict.containsKey(key)) { thenBody } else { target = dict.get(key); }
            {
                var tvTarget = exprTransformer.Transform(tvIfMa.Expression, context);
                var tvKey = exprTransformer.Transform(tvIfInvoc.ArgumentList.Arguments[0].Expression, context);
                var outTarget = exprTransformer.Transform(tvOutArg.Expression, context);

                var tvStmtTransformer3 = new StatementTransformer();
                string thenBody;
                if (stmt.Statement is BlockSyntax tvIfThenBlock3)
                    thenBody = $"{{\n        {TransformBlock(tvIfThenBlock3, context)}\n    }}";
                else
                    thenBody = $"{{ {tvStmtTransformer3.Transform(stmt.Statement, context).ToString("")} }}";

                var ifSb3 = new System.Text.StringBuilder();
                ifSb3.Append($"if (!{tvTarget}.containsKey({tvKey})) {thenBody}");

                string elseAssign = $"{outTarget} = {tvTarget}.get({tvKey});";
                if (stmt.Else != null)
                {
                    string elseBody;
                    if (stmt.Else.Statement is BlockSyntax tvIfElseBlock3)
                        elseBody = TransformBlock(tvIfElseBlock3, context);
                    else
                        elseBody = tvStmtTransformer3.Transform(stmt.Else.Statement, context).ToString("");
                    ifSb3.Append($" else {{\n        {elseAssign}\n        {elseBody}\n    }}");
                }
                else
                {
                    ifSb3.Append($" else {{\n        {elseAssign}\n    }}");
                }

                return new JavaStatementNode(ifSb3.ToString());
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

                // When the out variable is itself a ref/out parameter of the enclosing method,
                // it has been converted to a Holder type — assignments must target .value
                var assignTarget = existingVarName;
                if (context.SemanticModel?.GetSymbolInfo(tvIfIdent2).Symbol is IParameterSymbol tvOutParam
                    && (tvOutParam.RefKind == RefKind.Out || tvOutParam.RefKind == RefKind.Ref)
                    && !context.IsReadOnlyRefStructParam(tvOutParam.Name))
                {
                    assignTarget = $"{existingVarName}.value";
                }

                var tvStmtTransformer4 = new StatementTransformer();

                string thenBody2;
                if (stmt.Statement is BlockSyntax tvIfThenBlock4)
                {
                    var bodyStr = TransformBlock(tvIfThenBlock4, context);
                    thenBody2 = $"{{\n        {bodyStr}\n    }}";
                }
                else
                {
                    var bodyStr = tvStmtTransformer4.Transform(stmt.Statement, context).ToString("");
                    thenBody2 = $"{{\n        {bodyStr}\n    }}";
                }

                var ifSb4 = new System.Text.StringBuilder();
                // When the out variable is a primitive holder (assignTarget ends with .value),
                // the get() must go inside the if block to avoid NPE from auto-unboxing null.
                // For reference-type variables, assign before the if for Java definite assignment.
                if (assignTarget.EndsWith(".value"))
                {
                    // Primitive holder: containsKey guards the get() so unboxing is safe.
                    // The holder already has default(T) from its initialization, so definite
                    // assignment after the if block is satisfied.
                    string thenBodyWithAssign;
                    if (stmt.Statement is BlockSyntax tvIfThenBlock4_2)
                    {
                        var bodyStr2 = TransformBlock(tvIfThenBlock4_2, context);
                        thenBodyWithAssign = $"{{\n        {assignTarget} = {tvTarget2}.get({tvKey2});\n        {bodyStr2}\n    }}";
                    }
                    else
                    {
                        var bodyStr2 = tvStmtTransformer4.Transform(stmt.Statement, context).ToString("");
                        thenBodyWithAssign = $"{{\n        {assignTarget} = {tvTarget2}.get({tvKey2});\n        {bodyStr2}\n    }}";
                    }
                    ifSb4.Append($"if ({tvTarget2}.containsKey({tvKey2})) {thenBodyWithAssign}");
                }
                else
                {
                    // Reference type: get() before if for definite assignment.
                    // get() may return null (no NPE for reference types), containsKey
                    // still guards the body to preserve semantics when map stores null.
                    ifSb4.Append($"{assignTarget} = {tvTarget2}.get({tvKey2});\nif ({tvTarget2}.containsKey({tvKey2})) {thenBody2}");
                }

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
}
