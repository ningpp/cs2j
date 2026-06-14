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

        if (stmt.DescendantNodes().OfType<GotoStatementSyntax>()
            .Any(gotoStmt => gotoStmt.IsKind(SyntaxKind.GotoCaseStatement)
                || gotoStmt.IsKind(SyntaxKind.GotoDefaultStatement)))
        {
            var switchWithGoto = TransformSwitchWithGotoCase(stmt, expression, context).ToString("");
            if (preambleStatements.Count == 0)
                return new JavaStatementNode(switchWithGoto);

            return new JavaStatementNode(string.Join("\n", preambleStatements) + "\n" + switchWithGoto);
        }

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

    private JavaSyntaxNode TransformSwitchWithGotoCase(
        SwitchStatementSyntax stmt,
        string expression,
        ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var sectionStates = stmt.Sections
            .Select((section, index) => (section, state: index + 1))
            .ToDictionary(item => item.section, item => item.state);
        var stateByCaseValue = new Dictionary<string, int>(StringComparer.Ordinal);
        int? defaultState = null;

        foreach (var section in stmt.Sections)
        {
            foreach (var label in section.Labels)
            {
                if (label is CaseSwitchLabelSyntax caseLabel)
                {
                    stateByCaseValue[NormalizeSwitchCaseTarget(caseLabel.Value, context)] = sectionStates[section];
                }
                else if (label is DefaultSwitchLabelSyntax)
                {
                    defaultState = sectionStates[section];
                }
            }
        }

        var switchId = context.GenerateSyntheticName("_switch");
        var stateName = switchId + "State";
        var loopName = switchId + "Loop";
        var sb = new StringBuilder();

        // Drain any pending pre-statements BEFORE the switch statement.
        // Pre-statements like base segment declarations (MemorySegment __baseN = ptr)
        // must be placed before the switch, not inside a case, to avoid Java
        // "may not have been initialized" errors when referenced across cases.
        if (context.HasPendingPreStatements)
        {
            var pendingPre = context.DrainPreStatements();
            foreach (var pre in pendingPre)
                sb.AppendLine(pre.TrimEnd(';') + ";");
        }

        sb.AppendLine($"int {stateName} = 0;");
        sb.AppendLine($"switch ({expression}) {{");
        foreach (var section in stmt.Sections)
        {
            foreach (var label in section.Labels)
            {
                switch (label)
                {
                    case CaseSwitchLabelSyntax caseLabel:
                        sb.AppendLine($"    case {FormatSwitchCaseLabel(caseLabel.Value, context)}:");
                        break;
                    case DefaultSwitchLabelSyntax:
                        sb.AppendLine("    default:");
                        break;
                }
            }

            sb.AppendLine($"        {stateName} = {sectionStates[section]};");
            sb.AppendLine("        break;");
        }

        sb.AppendLine("}");
        sb.AppendLine($"{loopName}: while ({stateName} != -1) {{");
        sb.AppendLine($"    switch ({stateName}) {{");

        // Push the switch-goto-case context so nested goto case/default statements
        // can be properly transformed to state transitions.
        context.PushSwitchGotoCase(stmt, stateName, loopName, stateByCaseValue, defaultState);
        try
        {

        foreach (var section in stmt.Sections)
        {
            sb.AppendLine($"        case {sectionStates[section]}:");
            foreach (var statement in section.Statements)
            {
                if (statement is BreakStatementSyntax)
                {
                    sb.AppendLine($"            {stateName} = -1;");
                    sb.AppendLine($"            break {loopName};");
                }
                else if (statement is ContinueStatementSyntax)
                {
                    // C# continue inside a switch-with-goto-case should exit the while loop
                    // so the enclosing for/while loop can continue naturally
                    sb.AppendLine($"            {stateName} = -1;");
                    sb.AppendLine($"            break {loopName};");
                }
                else if (statement is GotoStatementSyntax gotoStmt
                    && TryTransformSwitchGoto(gotoStmt, stateName, loopName, stateByCaseValue, defaultState, context, out var gotoCode))
                {
                    AppendSwitchLine(sb, gotoCode);
                }
                else
                {
                    var transformed = Transform(statement, context).ToString("");
                    AppendSwitchLine(sb, transformed);
                }

                if (IsSwitchSectionTerminal(statement))
                {
                    break;
                }
            }

            if (!section.Statements.Any(StatementAlwaysTerminatesSwitchSection))
            {
                sb.AppendLine($"            {stateName} = -1;");
                sb.AppendLine($"            break {loopName};");
            }
        }

        }
        finally
        {
            context.PopSwitchGotoCase();
        }

        sb.AppendLine("        default:");
        sb.AppendLine($"            {stateName} = -1;");
        sb.AppendLine($"            break {loopName};");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        // Add a fallback return if the switch-with-goto-case is directly inside
        // a method body and the method has a non-void return type.
        // Java requires all paths to return a value, and the compiler can't verify
        // that the while loop always hits a return case (the default branch breaks out).
        // Only add when the switch is a direct child of the method body block
        // AND is the last statement in that block (otherwise code after the switch
        // handles the return, and a fallback return would be unreachable).
        if (stmt.Parent is BlockSyntax methodBlock && methodBlock.Parent is MethodDeclarationSyntax methodDecl
            && methodBlock.Statements.IndexOf(stmt) == methodBlock.Statements.Count - 1)
        {
            var returnType = methodDecl.ReturnType;
            if (returnType is PredefinedTypeSyntax pdt &&
                !pdt.Keyword.IsKind(SyntaxKind.VoidKeyword))
            {
                var keyword = pdt.Keyword.Text;
                var defaultReturn = keyword switch
                {
                    "int" or "long" or "short" or "byte" or "float" or "double" or "char" => "return 0;",
                    "bool" => "return false;",
                    _ => "return null;"
                };
                sb.AppendLine(defaultReturn);
            }
            else if (returnType is not PredefinedTypeSyntax)
            {
                // Non-predefined return type (e.g., String, custom class) - add return null as fallback.
                sb.AppendLine("return null;");
            }
        }

        return new JavaStatementNode(sb.ToString());

        string FormatSwitchCaseLabel(ExpressionSyntax caseValue, ConversionContext ctx)
        {
            var transformedLabel = exprTransformer.Transform(caseValue, ctx);
            if (ExpressionTransformerHelpers.TryFormatEnumMemberAccess(
                caseValue,
                ctx,
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

            return transformedLabel;
        }
    }

    private static string NormalizeSwitchCaseTarget(ExpressionSyntax expression, ConversionContext context)
    {
        return expression.NormalizeWhitespace().ToFullString();
    }

    private bool TryTransformSwitchGoto(
        GotoStatementSyntax gotoStmt,
        string stateName,
        string loopName,
        IReadOnlyDictionary<string, int> stateByCaseValue,
        int? defaultState,
        ConversionContext context,
        out string code)
    {
        if (gotoStmt.IsKind(SyntaxKind.GotoDefaultStatement))
        {
            if (defaultState.HasValue)
            {
                code = $"{stateName} = {defaultState.Value}; continue {loopName};";
                return true;
            }

            code = "/* TODO: goto default - default label not found */";
            return true;
        }

        if (gotoStmt.IsKind(SyntaxKind.GotoCaseStatement))
        {
            var target = gotoStmt.Expression != null
                ? NormalizeSwitchCaseTarget(gotoStmt.Expression, context)
                : "";
            if (stateByCaseValue.TryGetValue(target, out var state))
            {
                code = $"{stateName} = {state}; continue {loopName};";
                return true;
            }

            code = $"/* TODO: goto case {target} - case label not found */";
            return true;
        }

        code = "";
        return false;
    }

    private static void AppendSwitchLine(StringBuilder sb, string text)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                sb.AppendLine();
            }
            else
            {
                sb.Append("            ").AppendLine(line.TrimEnd());
            }
        }
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
                foreach (var identifier in sections[j].Statements
                    .SelectMany(s => s.DescendantNodesAndSelf())
                    .OfType<IdentifierNameSyntax>())
                {
                    var name = identifier.Identifier.Text;
                    if (declaredNames.Contains(name))
                        result.Add(name);
                }
            }
        }

        return result;
    }

    private JavaSyntaxNode TransformBreakStatement(BreakStatementSyntax? stmt, ConversionContext context)
    {
        if (stmt != null
            && context.TryGetSwitchGotoCaseInfo(
                out SwitchStatementSyntax switchStatement,
                out var stateName,
                out var loopName,
                out _,
                out _)
            && IsJumpTargetingSwitch(stmt, switchStatement))
        {
            return new JavaStatementNode($"{stateName} = -1; break {loopName};");
        }

        return new JavaStatementNode("break;");
    }

    private JavaSyntaxNode TransformContinueStatement(ContinueStatementSyntax? stmt, ConversionContext context)
    {
        if (stmt != null
            && context.TryGetSwitchGotoCaseInfo(
                out SwitchStatementSyntax switchStatement,
                out var stateName,
                out var loopName,
                out _,
                out _)
            && IsContinueTargetingEnclosingLoopThroughSwitch(stmt, switchStatement))
        {
            return new JavaStatementNode($"{stateName} = -1; break {loopName};");
        }

        return new JavaStatementNode("continue;");
    }

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
            case SyntaxKind.GotoStatement:
            case SyntaxKind.GotoCaseStatement:
            case SyntaxKind.GotoDefaultStatement:
                return true;
            case SyntaxKind.WhileStatement:
                // A while(true) loop that contains goto case/default is effectively terminal
                // because those gotos translate to "continue switchLoop" which exits the loop.
                return IsWhileTrueWithGotoCase(stmt);
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

    private static bool IsJumpTargetingSwitch(StatementSyntax jump, SwitchStatementSyntax switchStatement)
    {
        for (var parent = jump.Parent; parent != null; parent = parent.Parent)
        {
            if (ReferenceEquals(parent, switchStatement))
                return true;

            if (parent is SwitchStatementSyntax
                or WhileStatementSyntax
                or ForStatementSyntax
                or ForEachStatementSyntax
                or DoStatementSyntax)
                return false;
        }

        return false;
    }

    private static bool IsContinueTargetingEnclosingLoopThroughSwitch(ContinueStatementSyntax stmt, SwitchStatementSyntax switchStatement)
    {
        for (var parent = stmt.Parent; parent != null; parent = parent.Parent)
        {
            if (ReferenceEquals(parent, switchStatement))
                return true;

            if (parent is WhileStatementSyntax
                or ForStatementSyntax
                or ForEachStatementSyntax
                or DoStatementSyntax)
                return false;
        }

        return false;
    }

    private static bool IsWhileTrueWithGotoCase(StatementSyntax stmt)
    {
        if (stmt is not WhileStatementSyntax whileStmt)
            return false;

        // Check if condition is literal true
        if (whileStmt.Condition is not LiteralExpressionSyntax lit ||
            !lit.Token.IsKind(SyntaxKind.TrueKeyword))
            return false;

        // Check if the body contains any goto case/default
        return ContainsGotoCaseOrDefault(whileStmt.Statement);
    }

    private static bool ContainsGotoCaseOrDefault(StatementSyntax stmt)
    {
        return stmt.DescendantNodes().OfType<GotoStatementSyntax>().Any(g =>
            g.IsKind(SyntaxKind.GotoCaseStatement) || g.IsKind(SyntaxKind.GotoDefaultStatement));
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
            if (catchClause.Declaration != null)
            {
                var typeInfo = context.GetTypeInfo(catchClause.Declaration.Type);
                javaType = typeInfo.Type != null ? context.MapType(typeInfo.Type) : "Exception";
                var rawVarName = catchClause.Declaration.Identifier.ValueText;
                if (!string.IsNullOrWhiteSpace(rawVarName))
                    varName = ConversionContext.EscapeJavaKeyword(rawVarName);
            }

            var catchClauseIR = new Java.JavaCatchClause
            {
                ExceptionType = javaType,
                VariableName = varName
            };

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
                    new Java.JavaRawStatement($"if ({filter}) {innerBody.ToString("            ")}"));
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
        var body = currentStmt!.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{ {stmtTransformer.Transform(currentStmt.Statement, context).ToString("")} }}";

        return new JavaStatementNode($"try ({string.Join("; ", resources)}) {body}");
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
        return $"{javaType} {ConversionContext.EscapeJavaKeyword(variable.Identifier.Text)}{init}";
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

        return ExpressionTransformerHelpers.AdaptExpressionToTargetType(
            initializer,
            transformedInitializer,
            declaredResourceType,
            context);
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
                        var operandExpr = ExpressionTransformerFacade.Instance.Transform(addrOf.Operand, context);
                        var arrayType = FfmHelper.GetScratchArrayType(info.CSharpElementTypeName);
                        sb.AppendLine($"MemorySegment {varName} = MemorySegment.ofArray(new {arrayType}[] {{ {operandExpr} }});");
                    }
                }
                else
                {
                    initExpr = ExpressionTransformerFacade.Instance.Transform(initValue, context);
                    var initType = context.GetTypeInfo(initValue).Type;
                    isString = initType?.SpecialType == SpecialType.System_String;
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

        var body = stmt.Statement is BlockSyntax block
            ? TransformBlock(block, context)
            : Transform(stmt.Statement, context).ToString("");

        context.PopFixedScope();

        return new JavaStatementNode(sb.ToString() + body);
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
