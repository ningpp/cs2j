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
    private JavaSyntaxNode TransformSwitchStatement(SwitchStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var expression = exprTransformer.Transform(stmt.Expression, context);

        // Check if any section uses pattern-matching case labels — if so, convert the
        // entire switch to an if-else chain since Java switch doesn't support arbitrary patterns.
        bool hasPatternCases = stmt.Sections.Any(s =>
            s.Labels.Any(l => l is CasePatternSwitchLabelSyntax));

        if (hasPatternCases)
            return TransformPatternSwitchToIfElse(stmt, expression, context);

        return TransformPlainSwitch(stmt, expression, context);
    }

    private JavaSyntaxNode TransformPatternSwitchToIfElse(SwitchStatementSyntax stmt, string expression, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var sb = new System.Text.StringBuilder();
        bool first = true;

        foreach (var section in stmt.Sections)
        {
            var stmtTransformer = new StatementTransformer();
            var statements = section.Statements.Select(s =>
                stmtTransformer.Transform(s, context).ToString("")).ToList();
            // Strip trailing break statements (they're implicit in if-else)
            statements = statements.Where(s => s.Trim() != "break;").ToList();
            var body = string.Join("\n        ", statements);

            foreach (var label in section.Labels)
            {
                if (label is DefaultSwitchLabelSyntax)
                {
                    sb.Append(" else {\n        ");
                    sb.Append(body);
                    sb.Append("\n    }");
                }
                else
                {
                    string condition;
                    if (label is CasePatternSwitchLabelSyntax patternLabel)
                    {
                        condition = BuildCasePatternCondition(expression, patternLabel, context);
                    }
                    else if (label is CaseSwitchLabelSyntax caseLabel)
                    {
                        var transformedLabel = exprTransformer.Transform(caseLabel.Value, context);
                        condition = $"java.util.Objects.equals({expression}, {transformedLabel})";
                    }
                    else
                    {
                        continue;
                    }

                    if (first)
                    {
                        sb.Append($"if ({condition}) {{\n        ");
                        first = false;
                    }
                    else
                    {
                        sb.Append($" else if ({condition}) {{\n        ");
                    }
                    sb.Append(body);
                    sb.Append("\n    }");
                }
            }
        }

        return new JavaStatementNode(sb.ToString());
    }

    private JavaSyntaxNode TransformPlainSwitch(SwitchStatementSyntax stmt, string expression, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
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
        var exprTransformer = ExpressionTransformerFacade.Instance;

        // Walk stacked using statements to collect all resources into a single
        // try-with-resources block (C# stacked usings become nested UsingStatementSyntax).
        var resources = new List<string>();
        UsingStatementSyntax? currentStmt = stmt;
        while (currentStmt != null)
        {
            if (currentStmt.Declaration != null)
            {
                foreach (var variable in currentStmt.Declaration.Variables)
                {
                    var javaType = GetResourceJavaType(currentStmt, variable, context);
                    var resourceInit = variable.Initializer != null
                        ? ExpressionTransformerHelpers.AdaptExpressionToTargetType(
                            variable.Initializer.Value,
                            exprTransformer.Transform(variable.Initializer.Value, context),
                            context.SemanticModel?.GetTypeInfo(currentStmt.Declaration.Type).Type,
                            context)
                        : string.Empty;
                    var init = variable.Initializer != null ? $" = {resourceInit}" : "";
                    resources.Add($"{javaType} {variable.Identifier}{init}");
                }
            }

            if (currentStmt.Statement is UsingStatementSyntax innerUsing)
            {
                currentStmt = innerUsing;
            }
            else
            {
                break;
            }
        }

        // currentStmt is the innermost using; its Statement is the actual body.
        var stmtTransformer = new StatementTransformer();
        var body = currentStmt!.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{ {stmtTransformer.Transform(currentStmt.Statement, context).ToString("")} }}";

        return new JavaStatementNode($"try ({string.Join("; ", resources)}) {body}");
    }

    /// <summary>
    /// Determines the Java type for a using-statement resource variable.
    /// When the variable is initialized by a method call whose mapped Java return type
    /// differs from the C# type mapping, the Java return type is used.
    /// </summary>
    private static string GetResourceJavaType(
        UsingStatementSyntax stmt,
        VariableDeclaratorSyntax variable,
        ConversionContext context)
    {
        var typeInfo = context.SemanticModel?.GetTypeInfo(stmt.Declaration!.Type);

        // When the initializer is a method call, resolve the Java return type of the
        // mapped method so the variable type matches what the expression actually produces.
        if (variable.Initializer?.Value is InvocationExpressionSyntax invocation)
        {
            var methodSymbol = context.SemanticModel?.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (methodSymbol != null)
            {
                var mappedType = ResolveMethodReturnJavaType(methodSymbol, context);
                if (mappedType != null)
                    return mappedType;
            }
        }

        return typeInfo.HasValue && typeInfo.Value.Type != null
            ? context.MapType(typeInfo.Value.Type)
            : "AutoCloseable";
    }

    private static string? ResolveMethodReturnJavaType(IMethodSymbol method, ConversionContext context)
    {
        var containingType = method.ContainingType?.ToDisplayString();
        if (containingType == null)
            return null;

        return containingType switch
        {
            // File.Create() → FileHelper.create() → OutputStream
            "System.IO.File" => method.Name switch
            {
                "Create" => "OutputStream",
                "OpenRead" => "InputStream",
                "Open" => "InputStream",
                _ => null
            },
            _ => null
        };
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

    private string BuildCasePatternCondition(string expr, CasePatternSwitchLabelSyntax label, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var condition = BuildPatternCondition(expr, label.Pattern, context);

        // Handle `when` clause
        if (label.WhenClause != null)
        {
            var whenCond = facade.Transform(label.WhenClause.Condition, context);
            condition = $"{condition} && {whenCond}";
        }

        return condition;
    }

    private string BuildPatternCondition(string expr, PatternSyntax pattern, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        return pattern switch
        {
            ConstantPatternSyntax cp when cp.Expression.IsKind(SyntaxKind.NullLiteralExpression)
                => $"({expr} == null)",
            ConstantPatternSyntax cp
                => $"java.util.Objects.equals({expr}, {facade.Transform(cp.Expression, context)})",
            DeclarationPatternSyntax dp
                => BuildDeclPatternCondition(expr, dp, context),
            TypePatternSyntax tp
                => $"({expr} instanceof {context.MapTypeFromSyntax(tp.Type)})",
            DiscardPatternSyntax
                => "true",
            UnaryPatternSyntax np when np.OperatorToken.IsKind(SyntaxKind.NotKeyword)
                => $"!({BuildPatternCondition(expr, np.Pattern, context)})",
            RelationalPatternSyntax rel
                => $"({expr} {rel.OperatorToken.Text} {facade.Transform(rel.Expression, context)})",
            BinaryPatternSyntax bin when bin.IsKind(SyntaxKind.AndPattern)
                => $"({BuildPatternCondition(expr, bin.Left, context)} && {BuildPatternCondition(expr, bin.Right, context)})",
            BinaryPatternSyntax bin when bin.IsKind(SyntaxKind.OrPattern)
                => $"({BuildPatternCondition(expr, bin.Left, context)} || {BuildPatternCondition(expr, bin.Right, context)})",
            RecursivePatternSyntax recPattern
                => BuildRecursiveCondition(expr, recPattern, context),
            VarPatternSyntax varPattern
                => BuildVarCondition(expr, varPattern),
            ParenthesizedPatternSyntax parenPattern
                => BuildPatternCondition(expr, parenPattern.Pattern, context),
            _ => $"/* TODO: pattern {pattern.GetType().Name} */ true"
        };
    }

    private string BuildDeclPatternCondition(string expr, DeclarationPatternSyntax dp, ConversionContext context)
    {
        var mappedType = context.MapTypeFromSyntax(dp.Type);
        var designation = dp.Designation switch
        {
            SingleVariableDesignationSyntax sv => ConversionContext.EscapeJavaKeyword(sv.Identifier.Text),
            DiscardDesignationSyntax => "_",
            _ => "_unused"
        };
        return $"({expr} instanceof {mappedType} {designation})";
    }

    private string BuildRecursiveCondition(string expr, RecursivePatternSyntax pattern, ConversionContext context)
    {
        string? typeName = null;
        if (pattern.Type != null)
        {
            var typeInfo = context.SemanticModel?.GetTypeInfo(pattern.Type);
            typeName = (typeInfo.HasValue && typeInfo.Value.Type != null)
                ? context.MapType(typeInfo.Value.Type)
                : context.MapTypeFromSyntax(pattern.Type);
        }

        var conditions = new List<string>();
        if (typeName != null)
            conditions.Add($"{expr} instanceof {typeName}");

        if (pattern.PropertyPatternClause != null && typeName != null)
        {
            var cast = $"(({typeName}){expr})";
            foreach (var sub in pattern.PropertyPatternClause.Subpatterns)
            {
                string? propName = sub.NameColon?.Name.Identifier.Text
                    ?? (sub.ExpressionColon?.Expression is IdentifierNameSyntax idName ? idName.Identifier.Text : null);
                if (propName == null) continue;
                string getter = $"{cast}.get{char.ToUpperInvariant(propName[0])}{propName[1..]}()";
                string cond = BuildPatternCondition(getter, sub.Pattern, context);
                conditions.Add(cond);
            }
        }

        if (pattern.Designation is SingleVariableDesignationSyntax sv)
        {
            var varName = ConversionContext.EscapeJavaKeyword(sv.Identifier.Text);
            if (typeName != null)
                conditions.Add($"({varName} = ({typeName}){expr}) != null");
        }

        return conditions.Count > 0
            ? string.Join(" && ", conditions)
            : "true";
    }

    private string BuildVarCondition(string expr, VarPatternSyntax pattern)
    {
        var designation = pattern.Designation.ToString();
        if (designation != "_")
            return $"({ConversionContext.EscapeJavaKeyword(designation)} = {expr}) != null || true";
        return "true";
    }
}
