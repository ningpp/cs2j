using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.NumericBoundary;

/// <summary>
/// Semantic classification services for the numeric-boundary rewriter.
/// Everything type/operation related goes through the <see cref="SemanticModel"/>
/// (<c>GetTypeInfo</c>/<c>GetSymbolInfo</c>/<c>GetOperation</c>/<c>GetConstantValue</c>);
/// token text is never trusted (design hard invariant 4).
/// Also owns the checked/unchecked context stack and per-project
/// <c>CheckForOverflowUnderflow</c> parsing (design decision 15).
/// </summary>
internal sealed class NumericBoundaryAnalyzer
{
    private readonly SemanticModel? _model;
    private readonly Stack<bool> _checkedContextStack = new();

    public NumericBoundaryAnalyzer(SemanticModel? model, bool projectDefaultChecked = false)
    {
        _model = model;
        _checkedContextStack.Push(projectDefaultChecked);
    }

    /// <summary>True while rewriting inside an active checked context (checked shell/block or project /checked).</summary>
    public bool IsInCheckedContext => _checkedContextStack.Count > 0 && _checkedContextStack.Peek();

    public IDisposable PushContext(bool isChecked)
    {
        _checkedContextStack.Push(isChecked);
        return new ContextScope(this);
    }

    private sealed class ContextScope : IDisposable
    {
        private readonly NumericBoundaryAnalyzer _analyzer;
        public ContextScope(NumericBoundaryAnalyzer analyzer) => _analyzer = analyzer;
        public void Dispose() => _analyzer._checkedContextStack.Pop();
    }

    // ── shell inner classification (M1: checked expressions only) ──

    /// <summary>
    /// True when the inner expression binds to a user-defined <b>checked</b>
    /// operator variant (<c>op_CheckedAddition</c>, <c>op_CheckedExplicit</c>, …).
    /// Such calls are method invocations whose checked semantics live in the
    /// chosen variant itself; the checked shell is the only syntax context that
    /// selected it, so the rewriter must keep it for member-desugar
    /// (design decision 4 — the NB-13 exemption vouchers).
    /// Note: <c>System.Decimal.op_Addition</c> and friends also surface with a
    /// non-null <see cref="Microsoft.CodeAnalysis.Operations.IBinaryOperation.OperatorMethod"/>,
    /// but they are plain (non-checked) variants, so they are NOT retained here —
    /// <c>checked(decimal a + b)</c> strips per NB-9.
    /// </summary>
    public bool IsCheckedUserOperatorVariant(ExpressionSyntax inner)
    {
        if (_model?.GetOperation(inner) is not { } operation)
        {
            return false;
        }

        var method = GetOperatorMethod(operation);
        return method != null
            && method.Name.StartsWith("op_Checked", StringComparison.Ordinal);
    }

    private static Microsoft.CodeAnalysis.IMethodSymbol? GetOperatorMethod(
        Microsoft.CodeAnalysis.IOperation operation)
        => operation switch
        {
            Microsoft.CodeAnalysis.Operations.IBinaryOperation b => b.OperatorMethod,
            Microsoft.CodeAnalysis.Operations.IUnaryOperation u => u.OperatorMethod,
            Microsoft.CodeAnalysis.Operations.ICompoundAssignmentOperation c => c.OperatorMethod,
            Microsoft.CodeAnalysis.Operations.IConversionOperation v => v.OperatorMethod,
            _ => null,
        };

    /// <summary>
    /// True for <c>checked(e)</c> sites that M1 must NOT strip even though they are
    /// not built-in integer arithmetic: compound assignments and ++/-- (NB-7/8,
    /// M3), nullable lifted numeric operations (future CS2J5325) and dynamically
    /// bound sites (future CS2J5322). Retaining the shell keeps the code compilable
    /// and loses no overflow check; later milestones close each bucket loudly.
    /// </summary>
    public bool IsDeferredCheckedSite(ExpressionSyntax inner)
    {
        if (_model?.GetOperation(inner) is not { } operation)
        {
            return false;
        }

        switch (operation)
        {
            case Microsoft.CodeAnalysis.Operations.ICompoundAssignmentOperation:
            case Microsoft.CodeAnalysis.Operations.IIncrementOrDecrementOperation:
                return true;
            case Microsoft.CodeAnalysis.Operations.IBinaryOperation binary:
                return IsNullableNumeric(binary.Type) || IsDynamic(binary.Type);
            case Microsoft.CodeAnalysis.Operations.IUnaryOperation unary:
                return IsNullableNumeric(unary.Type) || IsDynamic(unary.Type);
            case Microsoft.CodeAnalysis.Operations.IConversionOperation conversion:
                return IsNullableNumeric(conversion.Type);
            default:
                return false;
        }
    }

    private static bool IsNullableNumeric(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named || named.OriginalDefinition.SpecialType
                != SpecialType.System_Nullable_T)
        {
            return false;
        }

        var argument = named.TypeArguments.FirstOrDefault();
        return NumericBoundarySyntaxFacts.IsIntegralType(argument)
            || NumericBoundarySyntaxFacts.IsFloatingPointOrDecimalType(argument);
    }

    private static bool IsDynamic(ITypeSymbol? type) => type?.TypeKind == TypeKind.Dynamic;

    /// <summary>
    /// M1 policy for a <c>checked(e)</c> shell whose inner expression is not a
    /// compile-time constant and not a user-defined operator: built-in integer
    /// arithmetic/conversion sites keep the shell (M2/M3 closes them), everything
    /// else is stripped with the inner operation untouched.
    /// </summary>
    public bool IsBuiltInIntegerCheckedSite(ExpressionSyntax inner)
    {
        if (_model?.GetOperation(inner) is not { } operation)
        {
            return false;
        }

        switch (operation)
        {
            case Microsoft.CodeAnalysis.Operations.IBinaryOperation binary:
                return binary.OperatorMethod == null
                    && NumericBoundarySyntaxFacts.IsIntegralType(binary.Type)
                    && binary.OperatorKind is Microsoft.CodeAnalysis.Operations.BinaryOperatorKind.Add
                        or Microsoft.CodeAnalysis.Operations.BinaryOperatorKind.Subtract
                        or Microsoft.CodeAnalysis.Operations.BinaryOperatorKind.Multiply
                        or Microsoft.CodeAnalysis.Operations.BinaryOperatorKind.Divide
                        or Microsoft.CodeAnalysis.Operations.BinaryOperatorKind.Remainder;

            case Microsoft.CodeAnalysis.Operations.IUnaryOperation unary:
                return unary.OperatorMethod == null
                    && NumericBoundarySyntaxFacts.IsIntegralType(unary.Type)
                    && unary.OperatorKind == Microsoft.CodeAnalysis.Operations.UnaryOperatorKind.Minus;

            case Microsoft.CodeAnalysis.Operations.IConversionOperation conversion:
                // Enum conversions and identity conversions never overflow-check
                // (NB-11); everything else integer-targeted keeps the shell in M1 —
                // this also conservatively retains future error sites
                // (floating→integer CS2J5321 / lifted CS2J5325) so no check is lost.
                if (conversion.OperatorMethod != null || conversion.Conversion.IsIdentity)
                {
                    return false;
                }

                if (NumericBoundarySyntaxFacts.IsEnumType(conversion.Type)
                    || NumericBoundarySyntaxFacts.IsEnumType(conversion.Operand?.Type))
                {
                    return false;
                }

                return NumericBoundarySyntaxFacts.IsIntegralType(conversion.Type);

            default:
                return false;
        }
    }

    /// <summary>
    /// Materialize a constant inner expression into a target-correct literal.
    /// Handles conversion operations (value already folded by the compiler),
    /// folded binary/unary operations, and bare constant expressions whose
    /// converted type is numeric (e.g. <c>byte b = unchecked(256);</c>).
    /// </summary>
    public bool TryMaterializeConstant(ExpressionSyntax inner, out ExpressionSyntax literal)
    {
        literal = null!;
        if (_model == null)
        {
            return false;
        }

        ITypeSymbol? targetType = null;
        object? value = null;

        switch (_model.GetOperation(inner))
        {
            case Microsoft.CodeAnalysis.Operations.IConversionOperation { ConstantValue: { HasValue: true } cv1 } conversion:
                targetType = conversion.Type;
                value = cv1.Value;
                break;
            case Microsoft.CodeAnalysis.Operations.IBinaryOperation { ConstantValue: { HasValue: true } cv2 } binary:
                targetType = binary.Type;
                value = cv2.Value;
                break;
            case Microsoft.CodeAnalysis.Operations.IUnaryOperation { ConstantValue: { HasValue: true } cv3 } unary:
                targetType = unary.Type;
                value = cv3.Value;
                break;
        }

        if (targetType == null && _model.GetConstantValue(inner) is { HasValue: true } constant)
        {
            var typeInfo = _model.GetTypeInfo(inner);
            var converted = typeInfo.ConvertedType;
            if (converted != null
                && !converted.Equals(typeInfo.Type, SymbolEqualityComparer.Default)
                && IsNumericType(converted))
            {
                targetType = converted;
            }
            else
            {
                targetType = typeInfo.Type;
            }

            value = constant.Value;
        }

        if (targetType == null || value == null)
        {
            return false;
        }

        return NumericBoundarySyntaxFacts.TryMaterializeLiteral(targetType, value, out literal);
    }

    private static bool IsNumericType(ITypeSymbol type)
        => NumericBoundarySyntaxFacts.IsIntegralType(type)
            || NumericBoundarySyntaxFacts.IsFloatingPointOrDecimalType(type);

    /// <summary>
    /// True when <paramref name="node"/> is (or is inside) a lambda converted to
    /// <c>System.Linq.Expressions.Expression&lt;TDelegate&gt;</c> (CS2J5323 site).
    /// M1 keeps the plumbing; the loud failure lands with M3.
    /// </summary>
    public bool IsWithinExpressionTree(SyntaxNode node)
    {
        if (_model == null)
        {
            return false;
        }

        for (var current = node.Parent; current != null; current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax lambda
                && _model.GetTypeInfo(lambda).ConvertedType is INamedTypeSymbol converted
                && converted.IsGenericType
                && converted.OriginalDefinition.ContainingNamespace?.ToDisplayString()
                    == "System.Linq.Expressions"
                && converted.OriginalDefinition.Name == "Expression")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the node binds dynamically (CS2J5322 site; plumbing for M3).</summary>
    public bool IsDynamic(ExpressionSyntax node)
        => _model?.GetTypeInfo(node).Type?.TypeKind == TypeKind.Dynamic;

    // ── project-level /checked (decision 15) ──

    /// <summary>
    /// Parse every csproj for <c>&lt;CheckForOverflowUnderflow&gt;</c> and build a
    /// project-directory → default-context map (last setting wins, mirroring
    /// MSBuild's textual override order).
    /// </summary>
    public static IReadOnlyDictionary<string, bool> ParseCheckForOverflowUnderflow(
        IEnumerable<string> projectFiles)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectFile in projectFiles)
        {
            XDocument document;
            try
            {
                document = XDocument.Load(projectFile, LoadOptions.None);
            }
            catch (Exception)
            {
                continue;
            }

            var root = document.Root;
            if (root == null || !root.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? raw = null;
            foreach (var element in root.Descendants())
            {
                if (element.Name.LocalName == "CheckForOverflowUnderflow")
                {
                    raw = element.Value;
                }
            }

            if (raw != null && bool.TryParse(raw.Trim(), out var enabled))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(projectFile));
                if (directory != null)
                {
                    map[directory] = enabled;
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Resolve the default overflow context for a file from its owning project
    /// directory (longest prefix wins). Null means "no project setting" (unchecked
    /// default for non-constant expressions).
    /// </summary>
    public static bool? ResolveProjectDefaultChecked(
        string? sourceFilePath,
        IReadOnlyDictionary<string, bool>? map)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || map == null || map.Count == 0)
        {
            return null;
        }

        var fullPath = Path.GetFullPath(sourceFilePath);
        string? bestDirectory = null;
        foreach (var projectDirectory in map.Keys)
        {
            if (IsSameOrDescendant(fullPath, projectDirectory)
                && (bestDirectory == null || projectDirectory.Length > bestDirectory.Length))
            {
                bestDirectory = projectDirectory;
            }
        }

        return bestDirectory != null ? map[bestDirectory] : null;
    }

    private static bool IsSameOrDescendant(string filePath, string directory)
    {
        var normalizedFile = filePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedDir = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedFile.Equals(normalizedDir, StringComparison.OrdinalIgnoreCase)
            || normalizedFile.StartsWith(
                normalizedDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || normalizedFile.StartsWith(
                normalizedDir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
