using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers;
using CSharpToJava.Core.Transformers.Expression;
using System.Text;
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

        // Java switch only supports byte, short, char, int, String, and enum types.
        // If the switch expression type is long, float, or double, convert to if-else.
        // Also convert if the type is a C# enum with long/ulong underlying type,
        // since such enums are converted to long constants (not Java enums).
        var switchType = context.GetTypeInfo(stmt.Expression).Type;
        bool needsIfElse = false;
        if (switchType != null)
        {
            if (switchType.SpecialType == SpecialType.System_Int64 ||
                switchType.SpecialType == SpecialType.System_UInt64 ||
                switchType.SpecialType == SpecialType.System_Single ||
                switchType.SpecialType == SpecialType.System_Double ||
                switchType.SpecialType == SpecialType.System_Decimal)
            {
                needsIfElse = true;
            }
            else if (switchType.TypeKind == TypeKind.Enum &&
                     switchType is INamedTypeSymbol enumType &&
                     enumType.EnumUnderlyingType != null)
            {
                var underlyingType = enumType.EnumUnderlyingType.SpecialType;
                if (underlyingType == SpecialType.System_Int64 ||
                    underlyingType == SpecialType.System_UInt64)
                {
                    needsIfElse = true;
                }
            }
        }
        if (needsIfElse)
            return TransformLongSwitchToIfElse(stmt, expression, context);

        // Java switch case labels must be compile-time constants.
        // C# const local variables are compile-time constants in C# but get converted
        // to non-final local variables in Java, so they can't be used as case labels.
        // If any case label references a local variable, convert to if-else.
        foreach (var section in stmt.Sections)
        {
            foreach (var label in section.Labels.OfType<CaseSwitchLabelSyntax>())
            {
                var labelSymbol = context.GetSymbolInfo(label.Value).Symbol;
                if (labelSymbol is ILocalSymbol)
                {
                    return TransformLongSwitchToIfElse(stmt, expression, context);
                }
            }
        }

        // Java enum switch requires all case labels to be enum constants.
        // If any case label is a const field of the enum type (not an enum member),
        // convert to if-else since Java doesn't allow static final fields as case labels.
        if (switchType?.TypeKind == TypeKind.Enum)
        {
            foreach (var section in stmt.Sections)
            {
                foreach (var label in section.Labels.OfType<CaseSwitchLabelSyntax>())
                {
                    var labelSymbol = context.GetSymbolInfo(label.Value).Symbol;
                    // If the case label is a const field of the enum type but NOT defined
                    // in that enum (e.g., a const field in another class), Java can't use
                    // it as a switch case label — only enum constants are allowed.
                    if (labelSymbol is IFieldSymbol field &&
                        field.Type?.TypeKind == TypeKind.Enum &&
                        !SymbolEqualityComparer.Default.Equals(field.ContainingType, field.Type))
                    {
                        return TransformPatternSwitchToIfElse(stmt, expression, context);
                    }
                }
            }
        }

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
            // Strip trailing break statements (they're implicit in if-else). A break may
            // hide inside a trailing block ({ ... break; }) — strip it from the tail too.
            while (statements.Count > 0)
            {
                var t = statements[statements.Count - 1].Trim();
                if (t == "break;")
                {
                    statements.RemoveAt(statements.Count - 1);
                }
                else if (t.StartsWith("{") && t.EndsWith("break;"))
                {
                    var idx = statements[statements.Count - 1].LastIndexOf("break;", StringComparison.Ordinal);
                    if (idx > 0)
                        statements[statements.Count - 1] = statements[statements.Count - 1].Remove(idx, 7).TrimEnd();
                    break;
                }
                else if (t.StartsWith("{") && t.EndsWith("}", StringComparison.Ordinal))
                {
                    // Trailing block ({ ... break;\n    }): strip the final break; when it
                    // is the block's last statement (nothing but whitespace/} after it).
                    var idx = statements[statements.Count - 1].LastIndexOf("break;", StringComparison.Ordinal);
                    if (idx > 0)
                    {
                        var tail = statements[statements.Count - 1].Substring(idx + 6).Trim();
                        if (tail is "" or "}")
                            statements[statements.Count - 1] = statements[statements.Count - 1].Remove(idx, 7).TrimEnd();
                    }
                    break;
                }
                else
                {
                    break;
                }
            }
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

    private JavaSyntaxNode TransformLongSwitchToIfElse(SwitchStatementSyntax stmt, string expression, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var sb = new System.Text.StringBuilder();
        bool first = true;

        foreach (var section in stmt.Sections)
        {
            var stmtTransformer = new StatementTransformer();
            var statements = section.Statements.Select(s =>
                stmtTransformer.Transform(s, context).ToString("")).ToList();
            // Strip trailing break statements (they're implicit in if-else). A break may
            // hide inside a trailing block ({ ... break; }) — strip it from the tail too.
            while (statements.Count > 0)
            {
                var t = statements[statements.Count - 1].Trim();
                if (t == "break;")
                {
                    statements.RemoveAt(statements.Count - 1);
                }
                else if (t.StartsWith("{") && t.EndsWith("break;"))
                {
                    var idx = statements[statements.Count - 1].LastIndexOf("break;", StringComparison.Ordinal);
                    if (idx > 0)
                        statements[statements.Count - 1] = statements[statements.Count - 1].Remove(idx, 7).TrimEnd();
                    break;
                }
                else if (t.StartsWith("{") && t.EndsWith("}", StringComparison.Ordinal))
                {
                    // Trailing block ({ ... break;\n    }): strip the final break; when it
                    // is the block's last statement (nothing but whitespace/} after it).
                    var idx = statements[statements.Count - 1].LastIndexOf("break;", StringComparison.Ordinal);
                    if (idx > 0)
                    {
                        var tail = statements[statements.Count - 1].Substring(idx + 6).Trim();
                        if (tail is "" or "}")
                            statements[statements.Count - 1] = statements[statements.Count - 1].Remove(idx, 7).TrimEnd();
                    }
                    break;
                }
                else
                {
                    break;
                }
            }
            var body = string.Join("\n        ", statements);

            foreach (var label in section.Labels)
            {
                if (label is DefaultSwitchLabelSyntax)
                {
                    sb.Append(" else {\n        ");
                    sb.Append(body);
                    sb.Append("\n    }");
                }
                else if (label is CaseSwitchLabelSyntax caseLabel)
                {
                    var transformedLabel = exprTransformer.Transform(caseLabel.Value, context);
                    var condition = $"(({expression}) == {transformedLabel})";

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
        var preambleStatements = DrainSwitchExpressionSideEffects(expression, context, out expression);

        // goto case/goto default must be desugared by GotoEliminator before Java conversion;
        // any goto remaining inside a section hits the goto throw arm in Transform.
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var sections = new List<string>();
        var declarationsUsedByLaterSections = FindSwitchDeclarationsUsedByLaterSections(stmt);

        foreach (var section in stmt.Sections)
        {
            var labels = new List<string>();

            foreach (var label in section.Labels)
            {
                switch (label)
                {
                    case CaseSwitchLabelSyntax caseLabel:
                        var transformedLabel = exprTransformer.Transform(caseLabel.Value, context);
                        if (transformedLabel.Contains(".get"))
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
            var statements = new List<string>();
            foreach (var s in section.Statements)
            {
                statements.Add(stmtTransformer.Transform(s, context).ToString(""));
                if (IsSwitchSectionTerminal(s))
                    break;
            }

            // Build case section header: combine multiple labels as "case X, Y:" or "default:"
            var sectionBody = string.Join("\n            ", statements);
            var localDeclarationNames = section.Statements
                .OfType<LocalDeclarationStatementSyntax>()
                .SelectMany(s => s.Declaration.Variables)
                .Select(v => v.Identifier.Text)
                .ToHashSet(StringComparer.Ordinal);
            var needsScopedBody = localDeclarationNames.Count > 0
                && !localDeclarationNames.Overlaps(declarationsUsedByLaterSections);
            if (needsScopedBody)
            {
                var indentedBody = string.Join("\n                ", statements);
                sectionBody = "{\n                " + indentedBody + "\n            }";
            }

            string sectionStr;
            var caseValues = labels.Where(l => l.StartsWith("case ")).Select(l => l.Substring(5)).ToList();
            var hasDefault = labels.Contains("default");
            if (hasDefault && caseValues.Count == 0)
            {
                sectionStr = "default:\n            " + sectionBody;
            }
            else if (hasDefault)
            {
                sectionStr = "case " + string.Join(", ", caseValues) + ":\n            default:\n            " +
                             sectionBody;
            }
            else
            {
                sectionStr = "case " + string.Join(", ", caseValues) + ":\n            " +
                             sectionBody;
            }

            sections.Add(sectionStr);
        }

        var bodyStr = string.Join("\n\n        ", sections);

        var switchText = $"switch ({expression}) {{\n        {bodyStr}\n    }}";
        if (preambleStatements.Count > 0)
            switchText = string.Join("\n", preambleStatements) + "\n" + switchText;

        return new JavaStatementNode(switchText);
    }

    private static List<string> DrainSwitchExpressionSideEffects(
        string expression,
        ConversionContext context,
        out string effectiveExpression)
    {
        var statements = new List<string>();
        effectiveExpression = expression;

        var hasPre = context.HasPendingPreStatements;
        var hasPost = context.HasPendingPostStatements;
        if (!hasPre && !hasPost)
            return statements;

        if (hasPre)
        {
            statements.AddRange(context.DrainPreStatements().Select(EnsureStatementSemicolon));
        }

        var switchExprName = context.GenerateSyntheticName("_switchExpr");
        statements.Add($"var {switchExprName} = {expression};");

        if (hasPost)
        {
            statements.AddRange(context.DrainPostStatements().Select(EnsureStatementSemicolon));
        }

        effectiveExpression = switchExprName;
        return statements;
    }

    private static string EnsureStatementSemicolon(string statement)
    {
        return statement.TrimEnd().TrimEnd(';') + ";";
    }

    private static HashSet<string> FindSwitchDeclarationsUsedByLaterSections(SwitchStatementSyntax stmt)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var sections = stmt.Sections;

        for (int i = 0; i < sections.Count; i++)
        {
            var declaredNames = sections[i].Statements
                .OfType<LocalDeclarationStatementSyntax>()
                .SelectMany(s => s.Declaration.Variables)
                .Select(v => v.Identifier.Text)
                .ToHashSet(StringComparer.Ordinal);

            if (declaredNames.Count == 0)
                continue;

            for (int j = i + 1; j < sections.Count; j++)
            {
                // Collect names declared in the later section itself — these are
                // self-references, not references to variables from section i.
                var laterDeclaredNames = sections[j].Statements
                    .OfType<LocalDeclarationStatementSyntax>()
                    .SelectMany(s => s.Declaration.Variables)
                    .Select(v => v.Identifier.Text)
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var identifier in sections[j].Statements
                    .SelectMany(s => s.DescendantNodesAndSelf())
                    .OfType<IdentifierNameSyntax>())
                {
                    var name = identifier.Identifier.Text;
                    if (declaredNames.Contains(name)
                        && !laterDeclaredNames.Contains(name))
                        result.Add(name);
                }
            }
        }

        return result;
    }

    private JavaSyntaxNode TransformBreakStatement(BreakStatementSyntax? stmt, ConversionContext context)
        => new JavaStatementNode("break;");

    private JavaSyntaxNode TransformContinueStatement(ContinueStatementSyntax? stmt, ConversionContext context)
        => new JavaStatementNode("continue;");

    private static bool IsSwitchSectionTerminal(StatementSyntax stmt) =>
        StatementAlwaysTerminatesSwitchSection(stmt);

    private static bool StatementAlwaysTerminatesSwitchSection(StatementSyntax stmt)
    {
        switch (stmt.Kind())
        {
            case SyntaxKind.ReturnStatement:
            case SyntaxKind.ThrowStatement:
            case SyntaxKind.BreakStatement:
            case SyntaxKind.ContinueStatement:
                return true;
            case SyntaxKind.Block:
                return stmt is BlockSyntax block
                    && block.Statements.Any()
                    && StatementAlwaysTerminatesSwitchSection(block.Statements.Last());
            case SyntaxKind.IfStatement:
                return stmt is IfStatementSyntax ifStmt
                    && ifStmt.Else != null
                    && StatementAlwaysTerminatesSwitchSection(ifStmt.Statement)
                    && StatementAlwaysTerminatesSwitchSection(ifStmt.Else.Statement);
            default:
                return false;
        }
    }

    private JavaSyntaxNode TransformTryStatement(TryStatementSyntax stmt, ConversionContext context)
    {
        var result = new Java.JavaTryCatchStatement();

        // Try body
        if (stmt.Block != null)
        {
            context.MethodState.PushScope();
            var tryStatements = TransformStatementsToIR(stmt.Block.Statements, context);
            context.MethodState.PopScope();
            foreach (var s in tryStatements)
                result.TryBody.Statements.Add(s);
        }

        // Catch clauses
        foreach (var catchClause in stmt.Catches)
        {
            string javaType = "Exception";
            string? varName = null;
            ITypeParameterSymbol? caughtTypeParam = null;
            if (catchClause.Declaration != null)
            {
                var typeInfo = context.GetTypeInfo(catchClause.Declaration.Type);
                if (typeInfo.Type is ITypeParameterSymbol tp)
                {
                    // Java cannot catch a type variable (catch (T ex)). C# allows it when T has
                    // an Exception constraint. The clause is rewritten to catch the constraint
                    // type plus a runtime isInstance guard (using the Class<T> token) so that
                    // non-matching exceptions continue to propagate, and a T-typed local that
                    // the body binds to (C# catch(T) semantics).
                    caughtTypeParam = tp;
                    var constraint = tp.ConstraintTypes.FirstOrDefault(t => t.TypeKind == TypeKind.Class);
                    javaType = constraint != null ? context.MapType(constraint) : "Exception";
                }
                else
                {
                    javaType = typeInfo.Type != null ? context.MapType(typeInfo.Type) : "Exception";
                }
                var rawVarName = catchClause.Declaration.Identifier.ValueText;
                if (!string.IsNullOrWhiteSpace(rawVarName))
                    varName = context.MethodState.GetEffectiveLocalJavaName(
                        context.GetDeclaredSymbol(catchClause.Declaration), ConversionContext.EscapeJavaKeyword(rawVarName));
            }

            var catchClauseIR = new Java.JavaCatchClause
            {
                ExceptionType = javaType,
                VariableName = varName
            };

            if (catchClause.Filter != null && string.IsNullOrWhiteSpace(varName))
            {
                // Java catch blocks always have a variable; use a synthetic one so a
                // false filter can rethrow the original exception.
                varName = "_cs2j_filterEx";
                catchClauseIR.VariableName = varName;
            }

            // For catch (T ...) with a type-parameter type, emit the whole clause raw:
            // catch (constraint _cs2j_xCatch) { guard; T x = (T) _cs2j_xCatch; body }
            bool typeParameterCatch = caughtTypeParam != null && catchClause.Declaration != null;
            if (typeParameterCatch)
            {
                var token = ConversionContext.GetClassLiteral(caughtTypeParam!, context);
                var rawCatchVarName = catchClause.Declaration.Identifier.ValueText;
                var syntheticCatchName = string.IsNullOrWhiteSpace(rawCatchVarName)
                    ? "_cs2j_catchEx"
                    : $"_cs2j_{rawCatchVarName}Catch";
                var catchText = new StringBuilder();
                catchText.Append(" catch (").Append(javaType).Append(' ').Append(syntheticCatchName).Append(") {\n");
                catchText.Append("    if (!").Append(token).Append(".isInstance(").Append(syntheticCatchName).Append(")) {\n");
                catchText.Append("        throw ").Append(syntheticCatchName).Append(";\n");
                catchText.Append("    }\n");
                if (!string.IsNullOrWhiteSpace(rawCatchVarName))
                {
                    catchText.Append("    ").Append(caughtTypeParam!.Name).Append(' ').Append(varName)
                        .Append(" = (").Append(caughtTypeParam.Name).Append(") ").Append(syntheticCatchName).Append(";\n");
                }

                var exprTransformer = ExpressionTransformerFacade.Instance;
                var filterExpr = catchClause.Filter?.FilterExpression;
                var filter = filterExpr != null ? exprTransformer.Transform(filterExpr, context) : null;
                context.MethodState.PushScope();
                var catchStatements = TransformStatementsToIR(catchClause.Block.Statements, context);
                context.MethodState.PopScope();
                var innerBody = new Java.JavaBlockStatement();
                foreach (var s in catchStatements)
                    innerBody.Statements.Add(s);
                var bodyText = innerBody.ToString("            ");
                if (filter != null)
                {
                    // Java doesn't support catch filters — wrap body in if statement
                    catchText.Append("    if (").Append(filter).Append(") ").Append(bodyText)
                        .Append(" else { throw ").Append(syntheticCatchName).Append("; }\n");
                }
                else
                {
                    catchText.Append(bodyText);
                }
                catchText.Append("    }");
                catchClauseIR.RawOverride = catchText.ToString();
                result.CatchClauses.Add(catchClauseIR);
                continue;
            }

            if (catchClause.Filter != null)
            {
                // Java doesn't support catch filters — wrap body in if statement
                var exprTransformer = ExpressionTransformerFacade.Instance;
                var filter = exprTransformer.Transform(catchClause.Filter.FilterExpression, context);
                context.MethodState.PushScope();
                var catchStatements = TransformStatementsToIR(catchClause.Block.Statements, context);
                context.MethodState.PopScope();
                var innerBody = new Java.JavaBlockStatement();
                foreach (var s in catchStatements)
                    innerBody.Statements.Add(s);
                // Wrap the inner body in a raw if-check statement
                catchClauseIR.Body.Statements.Add(
                    new Java.JavaRawStatement($"if ({filter}) {innerBody.ToString("            ")} else {{ throw {varName}; }}"));
            }
            else
            {
                context.MethodState.PushScope();
                var catchStatements = TransformStatementsToIR(catchClause.Block.Statements, context);
                context.MethodState.PopScope();
                foreach (var s in catchStatements)
                    catchClauseIR.Body.Statements.Add(s);
            }

            result.CatchClauses.Add(catchClauseIR);
        }

        // Finally body
        if (stmt.Finally != null && stmt.Finally.Block != null)
        {
            context.MethodState.PushScope();
            var finallyStatements = TransformStatementsToIR(stmt.Finally.Block.Statements, context);
            context.MethodState.PopScope();
            result.FinallyBody = new Java.JavaBlockStatement();
            foreach (var s in finallyStatements)
                result.FinallyBody.Statements.Add(s);
        }

        return result;
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
                    resources.Add(BuildUsingVariableResource(currentStmt.Declaration.Type, variable, context));
                }
            }
            else if (currentStmt.Expression != null)
            {
                resources.Add(BuildUsingExpressionResource(currentStmt.Expression, context));
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
        // Object initializers in resource expressions emit pre-statements
        // (var _objN = ...; _objN.setX(...);) that must run BEFORE the try.
        var resourcePre = context.DrainPreStatements().ToList();
        // The pre-statements may reference the first resource variable (e.g.
        // new JsonTextWriter(stringWriter)); hoist that declaration first.
        if (resourcePre.Count > 0 && resources.Count > 1)
        {
            var firstResource = resources[0];
            var firstEq = firstResource.IndexOf(" = ", System.StringComparison.Ordinal);
            if (firstEq > 0)
            {
                resourcePre.Insert(0, firstResource + ";");
                resources.RemoveAt(0);
            }
        }
        var body = currentStmt!.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{ {stmtTransformer.Transform(currentStmt.Statement, context).ToString("")} }}";

        var prefix = resourcePre.Count > 0
            ? string.Join("\n", resourcePre.Select(s => s.TrimEnd(';') + ";")) + "\n"
            : "";
        return new JavaStatementNode($"{prefix}try ({string.Join("; ", resources)}) {body}");
    }

    private static List<string> BuildUsingDeclarationResources(
        LocalDeclarationStatementSyntax stmt,
        ConversionContext context)
    {
        return stmt.Declaration.Variables
            .Select(variable => BuildUsingVariableResource(stmt.Declaration.Type, variable, context))
            .ToList();
    }

    private static string BuildUsingVariableResource(
        TypeSyntax declaredTypeSyntax,
        VariableDeclaratorSyntax variable,
        ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var declaredResourceType = GetDeclaredResourceType(declaredTypeSyntax, variable, context);
        var javaType = GetResourceJavaType(variable, declaredResourceType, context);
        var resourceInit = variable.Initializer != null
            ? AdaptResourceInitializer(
                variable.Initializer.Value,
                exprTransformer.Transform(variable.Initializer.Value, context),
                declaredResourceType,
                javaType,
                context)
            : string.Empty;
        var init = variable.Initializer != null ? $" = {resourceInit}" : "";
        var resourceVarName = context.MethodState.GetEffectiveLocalJavaName(
            context.GetDeclaredSymbol(variable), ConversionContext.EscapeJavaKeyword(variable.Identifier.Text));
        return $"{javaType} {resourceVarName}{init}";
    }

    private static string BuildUsingExpressionResource(ExpressionSyntax resourceExpression, ConversionContext context)
    {
        if (resourceExpression is IdentifierNameSyntax identifier)
            return ConversionContext.EscapeJavaKeyword(identifier.Identifier.Text);

        var exprTransformer = ExpressionTransformerFacade.Instance;
        var expressionType = context.GetTypeInfo(resourceExpression).Type;
        var javaType = GetExpressionResourceJavaType(resourceExpression, expressionType, context);
        var transformedExpression = exprTransformer.Transform(resourceExpression, context);
        var resourceInit = AdaptResourceInitializer(
            resourceExpression,
            transformedExpression,
            expressionType,
            javaType,
            context);

        var resourceName = context.GenerateSyntheticName("_usingResource");
        return $"{javaType} {resourceName} = {resourceInit}";
    }

    /// <summary>
    /// Determines the Java type for a using-statement resource variable.
    /// When the variable is initialized by a method call whose mapped Java return type
    /// differs from the C# type mapping, the Java return type is used.
    /// </summary>
    private static string GetResourceJavaType(
        VariableDeclaratorSyntax variable,
        ITypeSymbol? declaredResourceType,
        ConversionContext context)
    {
        if (IsSystemIoStream(declaredResourceType))
        {
            context.AddImport("io.github.ningpp.compat.StreamWrapper");
            return "StreamWrapper";
        }

        // When the initializer is a method call, resolve the Java return type of the
        // mapped method so the variable type matches what the expression actually produces.
        if (variable.Initializer?.Value is InvocationExpressionSyntax invocation)
        {
            var methodSymbol = context.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (methodSymbol != null)
            {
                var mappedType = ResolveMethodReturnJavaType(methodSymbol);
                if (mappedType != null)
                {
                    AddResourceTypeImport(mappedType, context);
                    return mappedType;
                }
            }
        }

        return declaredResourceType != null
            ? context.MapType(declaredResourceType)
            : "AutoCloseable";
    }

    private static string GetExpressionResourceJavaType(
        ExpressionSyntax expression,
        ITypeSymbol? expressionType,
        ConversionContext context)
    {
        if (IsSystemIoStream(expressionType))
        {
            if (IsFileOpenInvocation(expression, context))
            {
                context.AddImport("io.github.ningpp.compat.StreamWrapper");
                return "StreamWrapper";
            }
        }

        if (expression is InvocationExpressionSyntax invocation)
        {
            var methodSymbol = context.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (methodSymbol != null)
            {
                var mappedType = ResolveMethodReturnJavaType(methodSymbol);
                if (mappedType != null)
                {
                    AddResourceTypeImport(mappedType, context);
                    return mappedType;
                }
            }
        }

        if (expressionType != null && expressionType.TypeKind != TypeKind.Error)
            return context.MapType(expressionType);

        return "AutoCloseable";
    }

    private static ITypeSymbol? GetDeclaredResourceType(
        TypeSyntax declaredTypeSyntax,
        VariableDeclaratorSyntax variable,
        ConversionContext context)
    {
        if (context.GetDeclaredSymbol(variable) as ILocalSymbol is { } local
            && local.Type is { TypeKind: not TypeKind.Error })
        {
            return local.Type;
        }

        var typeInfo = context.GetTypeInfo(declaredTypeSyntax);
        return typeInfo.Type is { TypeKind: not TypeKind.Error } type ? type : null;
    }

    private static string AdaptResourceInitializer(
        ExpressionSyntax initializer,
        string transformedInitializer,
        ITypeSymbol? declaredResourceType,
        string javaResourceType,
        ConversionContext context)
    {
        if (javaResourceType == "StreamWrapper"
            && IsFileOpenInvocation(initializer, context))
        {
            return transformedInitializer;
        }

        return transformedInitializer;
    }

    private static string? ResolveMethodReturnJavaType(IMethodSymbol method)
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
                "Open" => "StreamWrapper",
                _ => null
            },
            _ => null
        };
    }

    private static bool IsSystemIoStream(ITypeSymbol? type)
        => type?.ToDisplayString() == "System.IO.Stream";

    private static bool IsFileOpenInvocation(ExpressionSyntax initializer, ConversionContext context)
    {
        if (initializer is not InvocationExpressionSyntax invocation)
            return false;

        return context.GetSymbolInfo(invocation).Symbol is IMethodSymbol method
            && method.ContainingType?.ToDisplayString() == "System.IO.File"
            && method.Name == "Open";
    }

    private static void AddResourceTypeImport(string javaType, ConversionContext context)
    {
        switch (javaType)
        {
            case "InputStream":
                context.AddImport("java.io.InputStream");
                break;
            case "OutputStream":
                context.AddImport("java.io.OutputStream");
                break;
            case "StreamWrapper":
                context.AddImport("io.github.ningpp.compat.StreamWrapper");
                break;
            case "TextReader":
                context.AddImport("io.github.ningpp.compat.TextReader");
                break;
        }
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
        var sb = new StringBuilder();
        var pointerInfos = new List<FixedPointerInfo>();

        var pointerType = stmt.Declaration.Type as PointerTypeSyntax;
        if (pointerType == null)
        {
            context.Diagnostics.Error("Fixed statement requires a pointer type declaration.", stmt.GetLocation());
            return new JavaStatementNode("/* TODO: Fixed statement - unsupported declaration */");
        }

        var elementTypeName = FfmHelper.GetPointerElementTypeName(pointerType.ElementType);

        foreach (var declarator in stmt.Declaration.Variables)
        {
            var varName = declarator.Identifier.Text;
            var info = FfmHelper.CreatePointerInfo(varName, elementTypeName);
            pointerInfos.Add(info);

            bool isNull = declarator.Initializer?.Value is LiteralExpressionSyntax lit && lit.Token.IsKind(SyntaxKind.NullKeyword);
            bool isString = false;
            string initExpr = "";

            if (!isNull && declarator.Initializer != null)
            {
                var initValue = declarator.Initializer.Value;

                // Handle address-of scalar: fixed (byte* p = &b)
                if (initValue is PrefixUnaryExpressionSyntax addrOf && addrOf.OperatorToken.IsKind(SyntaxKind.AmpersandToken))
                {
                    // Check for &arr[index] pattern (address of array element)
                    if (addrOf.Operand is ElementAccessExpressionSyntax elemAccess
                        && elemAccess.ArgumentList.Arguments.Count == 1)
                    {
                        var arrayExpr = ExpressionTransformerFacade.Instance.Transform(elemAccess.Expression, context);
                        var indexExpr = ExpressionTransformerFacade.Instance.Transform(elemAccess.ArgumentList.Arguments[0].Expression, context);
                        // If index is 0, wrap the entire array
                        if (indexExpr.Trim() == "0")
                        {
                            var baseVar = context.GenerateSyntheticName("__base");
                            sb.AppendLine($"MemorySegment {baseVar} = MemorySegment.ofArray({arrayExpr});");
                            sb.AppendLine($"MemorySegment {varName} = {baseVar};");
                            context.RegisterPointerBase(varName, baseVar);
                        }
                        else
                        {
                            // Non-zero index: wrap array and slice
                            var offsetCalc = info.ElementSize == 1
                                ? indexExpr
                                : $"(long)({indexExpr}) * {info.ElementSize}";
                            var baseVar = context.GenerateSyntheticName("__base");
                            sb.AppendLine($"MemorySegment {baseVar} = MemorySegment.ofArray({arrayExpr});");
                            sb.AppendLine($"MemorySegment {varName} = {baseVar}.asSlice({offsetCalc});");
                            context.RegisterPointerBase(varName, baseVar);
                        }
                    }
                    else
                    {
                        // &MemoryMarshal.GetReference(span) pins the span's memory:
                        // the compat span exposes boxed elements and cannot be pinned
                        // in place, so materialize the whole backing byte[]/char[] —
                        // the fixed pointer then covers the entire span, keeping
                        // pointer arithmetic (p + span.Length) in bounds.
                        if (addrOf.Operand is InvocationExpressionSyntax refInv
                            && refInv.Expression is MemberAccessExpressionSyntax refMember
                            && refMember.Name.Identifier.Text == "GetReference"
                            && refInv.ArgumentList.Arguments.Count == 1)
                        {
                            var spanExpr = ExpressionTransformerFacade.Instance.Transform(refInv.ArgumentList.Arguments[0].Expression, context);
                            var spanType = context.GetTypeInfo(refInv.ArgumentList.Arguments[0].Expression).Type;
                            var spanElement = (spanType as INamedTypeSymbol)?.TypeArguments.FirstOrDefault();
                            if (spanElement?.SpecialType == SpecialType.System_Byte)
                            {
                                sb.AppendLine($"MemorySegment {varName} = MemorySegment.ofArray({spanExpr}.toByteArray());");
                                context.RegisterPointerBase(varName, varName);
                                continue;
                            }
                            if (spanElement?.SpecialType == SpecialType.System_Char)
                            {
                                sb.AppendLine($"MemorySegment {varName} = MemorySegment.ofArray({spanExpr}.toCharArray());");
                                context.RegisterPointerBase(varName, varName);
                                continue;
                            }
                        }

                        var operandExpr = ExpressionTransformerFacade.Instance.Transform(addrOf.Operand, context);
                        var arrayType = FfmHelper.GetScratchArrayType(info.CSharpElementTypeName);
                        // ref element values (e.g. MemoryMarshal.getReference) surface as
                        // boxed Integers — narrow through Number (a direct (byte) cast of
                        // Integer is an incompatible type in Java).
                        if (arrayType is "byte" or "sbyte" or "short"
                            && (operandExpr.Contains("getReference(") || operandExpr.Contains(".get(")))
                            operandExpr = arrayType == "short"
                                ? $"((Number)({operandExpr})).shortValue()"
                                : $"((Number)({operandExpr})).byteValue()";
                        sb.AppendLine($"MemorySegment {varName} = MemorySegment.ofArray(new {arrayType}[] {{ {operandExpr} }});");
                    }
                }
                else
                {
                    initExpr = ExpressionTransformerFacade.Instance.Transform(initValue, context);
                    var initType = context.GetTypeInfo(initValue).Type;
                    isString = initType?.SpecialType == SpecialType.System_String;
                    // A Span/ReadOnlySpan source cannot be pinned directly —
                    // materialize the backing byte[]/char[] for MemorySegment.ofArray.
                    if (initType is INamedTypeSymbol initNamed
                        && initNamed.OriginalDefinition?.ToDisplayString() is "System.Span<T>" or "System.ReadOnlySpan<T>"
                        && initNamed.TypeArguments.Length == 1)
                    {
                        if (initNamed.TypeArguments[0].SpecialType == SpecialType.System_Byte)
                            initExpr += ".toByteArray()";
                        else if (initNamed.TypeArguments[0].SpecialType == SpecialType.System_Char)
                            initExpr += ".toCharArray()";
                    }
                    var baseVar = context.GenerateSyntheticName("__base");
                    sb.AppendLine(FfmHelper.GenerateMemorySegmentInit(varName, initExpr, info, isString, isNull, baseVar));
                    context.RegisterPointerBase(varName, baseVar);
                }
            }
            else
            {
                sb.AppendLine(FfmHelper.GenerateMemorySegmentInit(varName, initExpr, info, isString, isNull));
            }
        }

        var imports = FfmHelper.GetRequiredImports(false);
        foreach (var imp in imports)
            context.AddImport(imp);

        context.PushFixedScope(pointerInfos);

        // Build a structured JavaBlockStatement so the VariableNameDeduplicationRewriter
        // can properly track scope boundaries.  The variable declarations (MemorySegment __baseN / p)
        // go into the block's statement list, and the body statements are added as children.
        var blockResult = new Java.JavaBlockStatement();
        blockResult.Statements.Add(new Java.JavaRawStatement(sb.ToString().TrimEnd('\n', '\r')));

        if (stmt.Statement is BlockSyntax innerBlock)
        {
            context.MethodState.PushScope();
            var bodyStmts = TransformStatementsToIR(innerBlock.Statements, context);
            context.MethodState.PopScope();
            foreach (var s in bodyStmts)
                blockResult.Statements.Add(s);
        }
        else
        {
            var singleStmt = Transform(stmt.Statement, context);
            if (singleStmt is Java.JavaStatement javaStmt)
                blockResult.Statements.Add(javaStmt);
            else
                blockResult.Statements.Add(new Java.JavaRawStatement(singleStmt.ToString("")));
        }

        context.PopFixedScope();

        return blockResult;
    }

    private JavaSyntaxNode TransformUnsafeStatement(UnsafeStatementSyntax stmt, ConversionContext context)
    {
        return stmt.Block != null
            ? new JavaStatementNode(TransformBlock(stmt.Block, context))
            : new JavaStatementNode("");
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
            // Parenthesize: the when-clause may contain || which would otherwise bind
            // looser than the && joining it to the pattern condition, changing semantics
            // (e.g. `case Boundary when A || B` must not become `(case==B && A) || B`).
            condition = $"{condition} && ({whenCond})";
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
            DiscardDesignationSyntax => "_unused",
            _ => "_unused"
        };
        return $"({expr} instanceof {mappedType} {designation})";
    }

    private string BuildRecursiveCondition(string expr, RecursivePatternSyntax pattern, ConversionContext context)
    {
        string? typeName = null;
        if (pattern.Type != null)
        {
            var typeInfo = context.GetTypeInfo(pattern.Type);
            typeName = typeInfo.Type != null
                ? context.MapType(typeInfo.Type)
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
