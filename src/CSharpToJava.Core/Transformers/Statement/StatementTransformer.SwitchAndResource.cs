using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression;
using CSharpToJava.Core.Transformers.Type;

namespace CSharpToJava.Core.Transformers.Statement;

public partial class StatementTransformer
{
    /// <summary>
    /// switch maps directly to a Java switch with compile-time constant labels only.
    /// Pattern cases, when clauses and switch types unsupported by Java switch
    /// (bool/long/float/double/decimal) must be lowered by a preprocessor.
    /// </summary>
    private JavaSyntaxNode TransformSwitchStatement(SwitchStatementSyntax stmt, ConversionContext context)
    {
        var exprTransformer = ExpressionTransformerFacade.Instance;
        var expression = exprTransformer.Transform(stmt.Expression, context);

        if (stmt.Sections.Any(s => s.Labels.Any(l => l is CasePatternSwitchLabelSyntax)))
        {
            throw new NotSupportedException(
                "CS2J normalization required: pattern switch labels (including `when` clauses) must be lowered by a preprocessor before Java conversion.");
        }

        var switchType = context.GetTypeInfo(stmt.Expression).Type;
        if (switchType != null)
        {
            switch (switchType.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_Decimal:
                    throw new NotSupportedException(
                        $"CS2J normalization required: switch on {switchType.ToDisplayString()} must be lowered to an if-chain by a preprocessor before Java conversion.");
            }
        }

        // A C# value enum (explicit values, including [Flags] combinations) has
        // no Java constant labels: `A | B` is a runtime expression in Java.
        // Drive the switch on the numeric value instead and render each label's
        // compile-time constant as an integer literal.
        // Int-constant metadata enums (BindingFlags/TypeAttributes/...) are plain
        // ints in Java: the switch drives on the int itself and case labels render
        // as compat int constants, so the value-enum getValue() rewrite must not fire.
        var valueEnumType = switchType is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType
            && !EnumTransformer.IsIntConstantMetadataEnum(enumType)
            && EnumTransformer.IsValueEnum(enumType)
                ? enumType
                : null;
        var valueEnumStorage = valueEnumType == null
            ? null
            : EnumTransformer.StorageTypeName(
                valueEnumType.EnumUnderlyingType?.SpecialType ?? SpecialType.System_Int32);
        var drivenExpression = valueEnumType == null
            ? expression
            : valueEnumStorage == "long"
                ? $"((int)({expression}).getValue())"
                : $"({expression}).getValue()";

        // ── 跨节共享局部提升 ──
        // C# 的 switch 各节共享一个声明作用域：前一节声明的局部，后一节可直接
        // 赋值复用（真实链 DocumentXmlWriter.CloseInnerContent：
        // InsertSiblingAfter 节声明 `XmlNode parent`，InsertSiblingBefore 节裸
        // 赋值复用）。Java 每个 case 块自成作用域（每节固定包 { }），后节的
        // 裸赋值落空（javac: 找不到符号 变量 parent）。把被后节引用的声明提升
        // 到 switch 之前，节内声明降级为纯赋值。
        var hoistPlans = new List<(LocalDeclarationStatementSyntax Stmt, string Name, string TypeText)>();
        var declaredBySection = new Dictionary<int, List<(string Name, LocalDeclarationStatementSyntax Stmt)>>();
        var switchSemanticModel = context.SemanticModel;
        for (var i = 0; i < stmt.Sections.Count; i++)
        {
            var declared = new List<(string Name, LocalDeclarationStatementSyntax Stmt)>();
            foreach (var s in stmt.Sections[i].Statements)
            {
                if (s is LocalDeclarationStatementSyntax { IsConst: false } lds)
                {
                    foreach (var v in lds.Declaration.Variables)
                    {
                        if (switchSemanticModel?.GetDeclaredSymbol(v) is ILocalSymbol)
                            declared.Add((v.Identifier.ValueText, lds));
                    }
                }
            }
            declaredBySection[i] = declared;
        }
        for (var i = 0; i < stmt.Sections.Count; i++)
        {
            foreach (var (name, hoistStmt) in declaredBySection[i])
            {
                var usedLater = stmt.Sections.Skip(i + 1).Any(section =>
                    section.Statements.Any(s => s.DescendantNodes().OfType<IdentifierNameSyntax>()
                        .Any(id => id.Identifier.ValueText == name)));
                if (!usedLater || hoistPlans.Any(p => p.Name == name))
                    continue;
                var localSymbol = hoistStmt.Declaration.Variables
                    .Select(v => switchSemanticModel?.GetDeclaredSymbol(v))
                    .OfType<ILocalSymbol>()
                    .FirstOrDefault(s => s.Name == name);
                var typeText = localSymbol != null ? context.MapType(localSymbol.Type) : "Object";
                hoistPlans.Add((hoistStmt, name, typeText));
            }
        }
        var hoistText = string.Join("", hoistPlans
            .DistinctBy(p => p.Name)
            .Select(p => $"{p.TypeText} {p.Name};\n        "));

        var sections = new List<string>();
        foreach (var section in stmt.Sections)
        {
            var labels = new List<string>();
            foreach (var label in section.Labels)
            {
                switch (label)
                {
                    case CaseSwitchLabelSyntax caseLabel:
                        labels.Add(valueEnumType == null
                            ? $"case {exprTransformer.Transform(caseLabel.Value, context)}:"
                            : $"case {RenderValueEnumCaseLabel(caseLabel, valueEnumStorage!, context)}:");
                        break;
                    case DefaultSwitchLabelSyntax:
                        labels.Add("default:");
                        break;
                    default:
                        throw new NotSupportedException(
                            $"CS2J normalization required: switch label '{label.Kind()}' must be lowered by a preprocessor before Java conversion.");
                }
            }

            var stmtTransformer = new StatementTransformer();

            string sectionBody;
            if (section.Statements.Count == 1 && section.Statements[0] is BlockSyntax explicitBlock)
            {
                // The C# source already scoped the section with its own braces;
                // emit that block directly (it also provides local variable scope).
                var blockText = stmtTransformer.Transform(explicitBlock, context).ToString("");
                if (!StatementAlwaysTerminatesSwitchSection(explicitBlock))
                    blockText += "\n            break;";
                sectionBody = $"{{\n            {blockText}\n            }}";
            }
            else
            {
                var statements = new List<string>();
                foreach (var s in section.Statements)
                {
                    // 被提升的节内声明降级为纯赋值（同名变量在提升表中时）。
                    if (s is LocalDeclarationStatementSyntax lds2
                        && hoistPlans.Any(p => p.Stmt == lds2))
                    {
                        foreach (var v in lds2.Declaration.Variables)
                        {
                            if (v.Initializer != null)
                                statements.Add($"{v.Identifier.ValueText} = {exprTransformer.Transform(v.Initializer.Value, context)};");
                        }
                    }
                    else
                    {
                        statements.Add(stmtTransformer.Transform(s, context).ToString(""));
                    }
                    if (StatementAlwaysTerminatesSwitchSection(s))
                        break;
                }
                if (!section.Statements.Any(StatementAlwaysTerminatesSwitchSection))
                    statements.Add("break;");
                sectionBody = $"{{\n            {string.Join("\n            ", statements)}\n            }}";
            }

            sections.Add($"{string.Join("\n            ", labels)}\n            {sectionBody}");
        }

        return new JavaStatementNode(
            $"{hoistText}switch ({drivenExpression}) {{\n        {string.Join("\n\n        ", sections)}\n    }}");
    }

    /// <summary>
    /// Renders a value-enum case label as the Java primitive constant matching
    /// the enum's storage type. Compound labels such as <c>A | B</c> and
    /// <c>A &amp; ~B</c> collapse to their C# compile-time constant value.
    /// </summary>
    private static string RenderValueEnumCaseLabel(
        CaseSwitchLabelSyntax label, string storage, ConversionContext context)
    {
        var semanticModel = context.SemanticModel;
        if (semanticModel == null)
        {
            throw new NotSupportedException(
                $"CS2J conversion error: no semantic model to resolve switch case '{label.Value}' on a value enum.");
        }

        var constant = semanticModel.GetConstantValue(label.Value);
        if (!constant.HasValue || constant.Value is null)
        {
            throw new NotSupportedException(
                $"CS2J conversion error: cannot resolve compile-time constant for switch case '{label.Value}' on a value enum.");
        }

        var signed = Convert.ToInt64(constant.Value);
        return storage switch
        {
            // C# byte is unsigned while Java byte is signed: narrow through sbyte
            // so the literal matches the value produced by getValue().
            "byte" => unchecked((sbyte)signed).ToString(),
            "short" => unchecked((short)signed).ToString(),
            "char" => unchecked((ushort)signed).ToString(),
            // Java cannot switch on long; the driven expression is cast to int.
            "long" => unchecked((int)signed).ToString(),
            _ => unchecked((int)signed).ToString(),
        };
    }

    private JavaSyntaxNode TransformBreakStatement(BreakStatementSyntax? stmt, ConversionContext context)
        => new JavaStatementNode("break;");

    private JavaSyntaxNode TransformContinueStatement(ContinueStatementSyntax? stmt, ConversionContext context)
        => new JavaStatementNode("continue;");

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
            case SyntaxKind.SwitchStatement:
                // A switch that can never complete normally: it covers every
                // input (has a default label) and every section ends in a
                // control-transfer statement. Both C# and javac apply this
                // reachability rule; appending a `break;` after such a switch
                // makes javac report an unreachable statement. This shape is
                // emitted by iterator-lowering's forwarding switches inside
                // the eliminate-goto state dispatch. Conservative on
                // purpose: anything short of this keeps the trailing break.
                return stmt is SwitchStatementSyntax nestedSwitch
                    && nestedSwitch.Sections.Count > 0
                    && nestedSwitch.Sections.Any(s => s.Labels.Any(l => l.IsKind(SyntaxKind.DefaultSwitchLabel)))
                    && nestedSwitch.Sections.All(s =>
                        s.Statements.Count > 0
                        && StatementAlwaysTerminatesSwitchSection(s.Statements.Last()));
            default:
                return false;
        }
    }

    /// <summary>
    /// try/catch/finally maps directly to Java. Catch filters and catches on type
    /// parameters must be lowered by a preprocessor.
    /// </summary>
    private JavaSyntaxNode TransformTryStatement(TryStatementSyntax stmt, ConversionContext context)
    {
        var result = new JavaTryCatchStatement();

        context.MethodState.PushScope();
        var tryStatements = TransformStatementsToIR(stmt.Block.Statements, context);
        context.MethodState.PopScope();
        foreach (var s in tryStatements)
            result.TryBody.Statements.Add(s);

        foreach (var catchClause in stmt.Catches)
        {
            if (catchClause.Filter != null)
            {
                throw new NotSupportedException(
                    "CS2J normalization required: catch filters (`catch (...) when (...)`) must be lowered by a preprocessor before Java conversion.");
            }

            string javaType;
            string? varName;
            if (catchClause.Declaration != null)
            {
                var caughtType = context.GetTypeInfo(catchClause.Declaration.Type).Type;
                if (caughtType is ITypeParameterSymbol)
                {
                    throw new NotSupportedException(
                        "CS2J normalization required: catch clauses over a type parameter must be lowered by a preprocessor before Java conversion.");
                }
                javaType = caughtType is { TypeKind: not TypeKind.Error } ? context.MapType(caughtType) : "Exception";
                var rawVarName = catchClause.Declaration.Identifier.ValueText;
                varName = string.IsNullOrWhiteSpace(rawVarName)
                    ? context.GenerateSyntheticName("_cs2jEx")
                    : context.MethodState.GetEffectiveLocalJavaName(
                        context.GetDeclaredSymbol(catchClause.Declaration), ConversionContext.EscapeJavaKeyword(rawVarName));
            }
            else
            {
                javaType = "Exception";
                varName = context.GenerateSyntheticName("_cs2jEx");
            }

            var catchClauseIR = new JavaCatchClause
            {
                ExceptionType = javaType,
                VariableName = varName
            };

            context.MethodState.PushScope();
            // Register the catch variable so an empty `throw;` inside the block
            // resolves to the exact Java name (synthetic for identifier-less C#
            // catches such as `catch (Exception) { throw; }`).
            context.MethodState.PushCatchVariable(varName);
            List<JavaStatement> catchStmts;
            try
            {
                catchStmts = TransformStatementsToIR(catchClause.Block.Statements, context);
            }
            finally
            {
                context.MethodState.PopCatchVariable();
            }
            context.MethodState.PopScope();
            foreach (var s in catchStmts)
                catchClauseIR.Body.Statements.Add(s);

            result.CatchClauses.Add(catchClauseIR);
        }

        if (stmt.Finally?.Block != null)
        {
            context.MethodState.PushScope();
            var finallyStatements = TransformStatementsToIR(stmt.Finally.Block.Statements, context);
            context.MethodState.PopScope();
            result.FinallyBody = new JavaBlockStatement();
            foreach (var s in finallyStatements)
                result.FinallyBody.Statements.Add(s);
        }

        return result;
    }

    /// <summary>
    /// using maps directly to Java try-with-resources. Stacked C# usings are
    /// collected into a single resource list.
    /// </summary>
    private JavaSyntaxNode TransformUsingStatement(UsingStatementSyntax stmt, ConversionContext context)
    {
        var resources = new List<string>();
        UsingStatementSyntax? currentStmt = stmt;
        while (currentStmt != null)
        {
            if (currentStmt.Declaration != null)
            {
                foreach (var variable in currentStmt.Declaration.Variables)
                    resources.Add(BuildUsingVariableResource(currentStmt.Declaration.Type, variable, context));
            }
            else if (currentStmt.Expression != null)
            {
                resources.Add(BuildUsingExpressionResource(currentStmt.Expression, context));
            }

            currentStmt = currentStmt.Statement as UsingStatementSyntax;
        }

        // Walk back to the innermost using to locate its real body.
        var innermost = stmt;
        while (innermost.Statement is UsingStatementSyntax innerUsing)
            innermost = innerUsing;

        var prefix = "";
        if (context.HasPendingPreStatements)
        {
            var pre = context.DrainPreStatements();
            prefix = string.Concat(pre.Select(s => s.TrimEnd(';') + ";\n"));
        }
        if (context.HasPendingPostStatements)
        {
            context.DrainPostStatements();
            throw new NotSupportedException(
                "CS2J normalization required: ref/out arguments producing read-backs in a using resource expression must be lowered by a preprocessor before Java conversion.");
        }

        var body = innermost.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{ {new StatementTransformer().Transform(innermost.Statement, context)} }}";

        return new JavaStatementNode($"{prefix}try ({string.Join("; ", resources)}) {body}");
    }

    internal static List<string> BuildUsingDeclarationResources(
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
        var javaType = (context.GetDeclaredSymbol(variable) as ILocalSymbol)?.Type is { TypeKind: not TypeKind.Error } localType
            ? context.MapType(localType)
            : context.MapTypeFromSyntax(declaredTypeSyntax);
        var resourceVarName = context.MethodState.GetEffectiveLocalJavaName(
            context.GetDeclaredSymbol(variable), ConversionContext.EscapeJavaKeyword(variable.Identifier.Text));
        var init = variable.Initializer != null
            ? $" = {exprTransformer.Transform(variable.Initializer.Value, context)}"
            : "";
        return $"{javaType} {resourceVarName}{init}";
    }

    private static string BuildUsingExpressionResource(ExpressionSyntax resourceExpression, ConversionContext context)
    {
        if (resourceExpression is IdentifierNameSyntax identifier)
            return ConversionContext.EscapeJavaKeyword(identifier.Identifier.Text);

        var exprTransformer = ExpressionTransformerFacade.Instance;
        var expressionType = context.GetTypeInfo(resourceExpression).Type;
        var javaType = expressionType is { TypeKind: not TypeKind.Error }
            ? context.MapType(expressionType)
            : "AutoCloseable";
        var resourceName = context.GenerateSyntheticName("_cs2jResource");
        return $"{javaType} {resourceName} = {exprTransformer.Transform(resourceExpression, context)}";
    }

    private JavaSyntaxNode TransformLockStatement(LockStatementSyntax stmt, ConversionContext context)
    {
        var expression = ExpressionTransformerFacade.Instance.Transform(stmt.Expression, context);
        var body = stmt.Statement is BlockSyntax block
            ? $"{{\n        {TransformBlock(block, context)}\n    }}"
            : $"{{ {new StatementTransformer().Transform(stmt.Statement, context)} }}";

        // C29 lock 分型：System.Threading.Lock（.NET 9 专用锁对象）不能发成
        // synchronized(expr)——那锁的是 wrapper 实例的 intrinsic monitor，与其
        // Enter/Exit 委托的 ReentrantLock 是两把锁；改发 enter(); try/finally exit();。
        if (context.GetTypeInfo(stmt.Expression).Type is INamedTypeSymbol lockType
            && lockType.OriginalDefinition.ToDisplayString() == "System.Threading.Lock")
        {
            var javaLockType = context.MapType(lockType);
            var lockLocal = context.GenerateSyntheticName("_cs2jLock");
            return new JavaStatementNode(
                $"{{\n    {javaLockType} {lockLocal} = {expression};\n"
                + $"    {lockLocal}.enter();\n"
                + $"    try {body} finally {{\n        {lockLocal}.exit();\n    }}\n}}");
        }

        return new JavaStatementNode($"synchronized ({expression}) {body}");
    }

    private JavaSyntaxNode TransformFixedStatement(FixedStatementSyntax stmt, ConversionContext context)
        => throw new NotSupportedException(
            "CS2J normalization required: fixed/unsafe pointer statements must be lowered by a preprocessor before Java conversion.");

    private JavaSyntaxNode TransformUnsafeStatement(UnsafeStatementSyntax stmt, ConversionContext context)
        => throw new NotSupportedException(
            "CS2J normalization required: unsafe blocks must be lowered by a preprocessor before Java conversion.");

    private JavaSyntaxNode TransformCheckedStatement(CheckedStatementSyntax stmt, ConversionContext context)
        => throw new NotSupportedException(
            "CS2J normalization required: checked blocks must be lowered (e.g. to Math.*Exact) by a preprocessor before Java conversion.");

    private JavaSyntaxNode TransformUncheckedStatement(CheckedStatementSyntax stmt, ConversionContext context)
        => throw new NotSupportedException(
            "CS2J normalization required: unchecked blocks must be stripped by a preprocessor before Java conversion.");
}
