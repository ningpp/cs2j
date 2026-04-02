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
}
