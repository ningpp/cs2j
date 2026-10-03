using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.VarianceAudit;

/// <summary>
/// Constraint inventory and dependent-site enumeration (design D5/D5a/D5b/D6). It walks
/// every source declaration that declares type parameters (class/struct/record/interface/
/// delegate and generic methods/local functions), classifies each constraint clause, and
/// — for value-type constraints (<c>struct</c>/<c>unmanaged</c>) — enumerates the sites
/// whose semantics depend on the erased constraint. It never rewrites anything.
/// </summary>
internal sealed class ConstraintAuditor
{
    private readonly Compilation _compilation;
    private readonly VarianceAuditStatistics _statistics;

    public ConstraintAuditor(Compilation compilation, VarianceAuditStatistics statistics)
    {
        _compilation = compilation;
        _statistics = statistics;
    }

    public void Audit(VarianceAuditAnalysis analysis)
    {
        foreach (var tree in _compilation.SyntaxTrees)
        {
            if (tree.FilePath.EndsWith("__cs2j_global_usings.g.cs", StringComparison.Ordinal))
            {
                continue;
            }

            AuditTree(_compilation.GetSemanticModel(tree), analysis);
        }
    }

    private void AuditTree(SemanticModel model, VarianceAuditAnalysis analysis)
    {
        foreach (var node in model.SyntaxTree.GetRoot().DescendantNodes())
        {
            switch (node)
            {
                case TypeDeclarationSyntax declaration when declaration.TypeParameterList != null:
                    AuditType(model, declaration, model.GetDeclaredSymbol(declaration), analysis);
                    break;
                case DelegateDeclarationSyntax declaration when declaration.TypeParameterList != null:
                    AuditType(model, declaration, model.GetDeclaredSymbol(declaration), analysis);
                    break;
                case MethodDeclarationSyntax declaration when declaration.TypeParameterList != null:
                    AuditMethod(model, declaration, model.GetDeclaredSymbol(declaration), analysis);
                    break;
                case LocalFunctionStatementSyntax declaration when declaration.TypeParameterList != null:
                    AuditMethod(model, declaration, model.GetDeclaredSymbol(declaration), analysis);
                    break;
            }
        }
    }

    private void AuditType(
        SemanticModel model,
        SyntaxNode declaration,
        INamedTypeSymbol? symbol,
        VarianceAuditAnalysis analysis)
    {
        if (symbol == null)
        {
            return;
        }

        var declaringName = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        foreach (var parameter in symbol.TypeParameters)
        {
            ProcessTypeParameter(model, declaration, parameter, declaringName, analysis);
        }
    }

    private void AuditMethod(
        SemanticModel model,
        SyntaxNode declaration,
        IMethodSymbol? symbol,
        VarianceAuditAnalysis analysis)
    {
        if (symbol == null)
        {
            return;
        }

        var declaringName = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        foreach (var parameter in symbol.TypeParameters)
        {
            ProcessTypeParameter(model, declaration, parameter, declaringName, analysis);
        }
    }

    private void ProcessTypeParameter(
        SemanticModel model,
        SyntaxNode declaration,
        ITypeParameterSymbol parameter,
        string declaringName,
        VarianceAuditAnalysis analysis)
    {
        var clause = ConstraintClauseOf(declaration, parameter.Name);
        var kinds = ClassifyConstraints(clause, parameter);
        if (kinds.Count == 0)
        {
            return;
        }

        var valueType = kinds.Contains(ConstraintKind.Struct) || kinds.Contains(ConstraintKind.Unmanaged);
        var construction = kinds.Contains(ConstraintKind.New) || valueType;
        var dependents = construction ? ScanDependents(model, parameter) : Dependents.Empty;
        if (!valueType)
        {
            // A 'new()'-only constraint guarantees reference-or-value construction, not
            // value semantics: only the construction/Activator association applies (D5b②).
            dependents = dependents.ConstructionOnly();
        }

        foreach (var kind in kinds)
        {
            AddConstraintSite(model, parameter, declaringName, kind, dependents, analysis);
        }

        if (valueType || kinds.Contains(ConstraintKind.New))
        {
            _statistics.ConstraintDependentDefaultSites += dependents.Default;
            _statistics.ConstraintDependentNewSites += dependents.New;
            _statistics.ConstraintDependentBoxingSites += dependents.Boxing;
            _statistics.ConstraintDependentArraySites += dependents.Array;
            _statistics.ConstraintDependentNullableSites += dependents.Nullable;
            analysis.ConstraintDependentSites.AddRange(dependents.Diagnostics);
        }
    }

    private void AddConstraintSite(
        SemanticModel model,
        ITypeParameterSymbol parameter,
        string declaringName,
        ConstraintKind kind,
        Dependents dependents,
        VarianceAuditAnalysis analysis)
    {
        var location = parameter.Locations.FirstOrDefault(candidate => candidate.IsInSource);
        var span = location?.GetLineSpan().StartLinePosition;
        analysis.ConstraintSites.Add(new VarianceConstraintSite(
            Kind: KindWord(kind),
            TypeParameterName: parameter.Name,
            DeclaringName: declaringName,
            DependentDefaultSites: dependents.Default,
            DependentNewSites: dependents.New,
            DependentBoxingSites: dependents.Boxing,
            DependentArraySites: dependents.Array,
            DependentNullableSites: dependents.Nullable,
            ActivatorSites: dependents.Activator,
            FilePath: model.SyntaxTree.FilePath,
            Line: (span?.Line ?? 0) + 1,
            Column: (span?.Character ?? 0) + 1));

        switch (kind)
        {
            case ConstraintKind.Struct:
                _statistics.ConstraintsStruct++;
                break;
            case ConstraintKind.Unmanaged:
                _statistics.ConstraintsUnmanaged++;
                break;
            case ConstraintKind.New:
                _statistics.ConstraintsNew++;
                break;
            case ConstraintKind.NotNull:
                _statistics.ConstraintsNotNull++;
                break;
            case ConstraintKind.Default:
                _statistics.ConstraintsDefault++;
                break;
            case ConstraintKind.Class:
                _statistics.ConstraintsClass++;
                break;
        }
    }

    // ---- constraint classification (D5a) ----

    private static List<ConstraintKind> ClassifyConstraints(
        TypeParameterConstraintClauseSyntax? clause,
        ITypeParameterSymbol parameter)
    {
        // 'unmanaged' is a pseudo-type identifier and has no stable semantic flag across
        // Roslyn versions (design R6); it is detected from syntax and cross-checked with the
        // value-type constraint it implies.
        var hasUnmanaged = false;
        var hasStruct = parameter.HasValueTypeConstraint;
        var hasNew = parameter.HasConstructorConstraint;
        var hasClass = parameter.HasReferenceTypeConstraint;
        var hasNotNull = false;
        var hasDefault = false;

        if (clause != null)
        {
            foreach (var constraint in clause.Constraints)
            {
                switch (constraint)
                {
                    case ClassOrStructConstraintSyntax classOrStruct:
                        if (classOrStruct.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword))
                        {
                            hasStruct = true;
                        }
                        else if (classOrStruct.ClassOrStructKeyword.IsKind(SyntaxKind.ClassKeyword))
                        {
                            hasClass = true;
                        }

                        break;
                    case ConstructorConstraintSyntax:
                        hasNew = true;
                        break;
                    case DefaultConstraintSyntax:
                        hasDefault = true;
                        break;
                    case TypeConstraintSyntax type when type.Type is IdentifierNameSyntax identifier:
                        if (identifier.Identifier.ValueText == "unmanaged")
                        {
                            hasUnmanaged = true;
                        }
                        else if (identifier.Identifier.ValueText == "notnull")
                        {
                            hasNotNull = true;
                        }

                        break;
                }
            }
        }

        var kinds = new List<ConstraintKind>();
        if (hasUnmanaged)
        {
            kinds.Add(ConstraintKind.Unmanaged);
        }
        else if (hasStruct)
        {
            kinds.Add(ConstraintKind.Struct);
        }

        if (hasNew)
        {
            kinds.Add(ConstraintKind.New);
        }

        if (hasNotNull)
        {
            kinds.Add(ConstraintKind.NotNull);
        }

        if (hasDefault)
        {
            kinds.Add(ConstraintKind.Default);
        }

        if (hasClass)
        {
            kinds.Add(ConstraintKind.Class);
        }

        return kinds;
    }

    private static TypeParameterConstraintClauseSyntax? ConstraintClauseOf(SyntaxNode declaration, string name)
    {
        var clauses = declaration switch
        {
            TypeDeclarationSyntax type => type.ConstraintClauses,
            DelegateDeclarationSyntax del => del.ConstraintClauses,
            MethodDeclarationSyntax method => method.ConstraintClauses,
            LocalFunctionStatementSyntax local => local.ConstraintClauses,
            _ => default,
        };

        foreach (var clause in clauses)
        {
            if (clause.Name.Identifier.ValueText == name)
            {
                return clause;
            }
        }

        return null;
    }

    // ---- D5b dependent sites ----

    private static Dependents ScanDependents(SemanticModel model, ITypeParameterSymbol parameter)
    {
        var diagnostics = new List<VarianceConstraintDependentSite>();
        var defaults = 0;
        var news = 0;
        var boxing = 0;
        var arrays = 0;
        var nullables = 0;
        var activators = 0;

        foreach (var node in model.SyntaxTree.GetRoot().DescendantNodes())
        {
            switch (node)
            {
                case DefaultExpressionSyntax expression when TypeMatches(model, expression, parameter):
                    defaults++;
                    diagnostics.Add(Dependent(
                        VarianceCodes.DefaultOnValueTypeConstraint,
                        DefaultMessage(parameter), parameter, expression));
                    break;
                case LiteralExpressionSyntax literal
                    when literal.IsKind(SyntaxKind.DefaultLiteralExpression)
                         && ConvertedTypeMatches(model, literal, parameter):
                    defaults++;
                    diagnostics.Add(Dependent(
                        VarianceCodes.DefaultOnValueTypeConstraint,
                        DefaultMessage(parameter), parameter, literal));
                    break;
                case ObjectCreationExpressionSyntax creation when TypeMatches(model, creation, parameter):
                    news++;
                    if (creation.ArgumentList?.Arguments.Count > 0 || creation.Initializer != null)
                    {
                        diagnostics.Add(Dependent(
                            VarianceCodes.ResidualNewTSite,
                            ResidualNewMessage(parameter), parameter, creation));
                    }

                    break;
                case InvocationExpressionSyntax invocation when IsActivatorCreateInstance(model, invocation, parameter):
                    activators++;
                    news++;
                    break;
                case ArrayTypeSyntax array when ArrayElementMatches(model, array, parameter):
                    arrays++;
                    break;
                case NullableTypeSyntax nullable when NullableArgumentMatches(model, nullable, parameter):
                    nullables++;
                    break;
                case GenericNameSyntax generic
                    when generic.Identifier.ValueText == "Nullable"
                         && NullableArgumentMatches(model, generic, parameter):
                    nullables++;
                    break;
                case CastExpressionSyntax cast
                    when ExpressionMatches(model, cast.Expression, parameter)
                         && IsObjectType(model, cast.Type):
                    boxing++;
                    break;
                case LockStatementSyntax lockStatement when ExpressionMatches(model, lockStatement.Expression, parameter):
                    boxing++;
                    break;
                case BinaryExpressionSyntax binary
                    when (binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression))
                         && (ExpressionMatches(model, binary.Left, parameter)
                             || ExpressionMatches(model, binary.Right, parameter)):
                    boxing++;
                    break;
                case BinaryExpressionSyntax binary
                    when (binary.IsKind(SyntaxKind.IsExpression) || binary.IsKind(SyntaxKind.AsExpression))
                         && ExpressionMatches(model, binary.Left, parameter)
                         && IsObjectType(model, binary.Right):
                    boxing++;
                    break;
            }
        }

        return new Dependents(defaults, news, boxing, arrays, nullables, activators, diagnostics);
    }

    private static bool TypeMatches(SemanticModel model, SyntaxNode node, ITypeParameterSymbol parameter)
        => SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(node).Type, parameter);

    private static bool ConvertedTypeMatches(SemanticModel model, SyntaxNode node, ITypeParameterSymbol parameter)
        => SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(node).ConvertedType, parameter);

    private static bool ExpressionMatches(SemanticModel model, SyntaxNode node, ITypeParameterSymbol parameter)
        => SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(node).Type, parameter);

    private static bool ArrayElementMatches(SemanticModel model, TypeSyntax type, ITypeParameterSymbol parameter)
        => model.GetTypeInfo(type).Type is IArrayTypeSymbol array
           && SymbolEqualityComparer.Default.Equals(array.ElementType, parameter);

    private static bool NullableArgumentMatches(SemanticModel model, SyntaxNode node, ITypeParameterSymbol parameter)
        => model.GetTypeInfo(node).Type is INamedTypeSymbol named
           && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
           && named.TypeArguments.Length == 1
           && SymbolEqualityComparer.Default.Equals(named.TypeArguments[0], parameter);

    private static bool IsObjectType(SemanticModel model, SyntaxNode node)
        => model.GetTypeInfo(node).Type?.SpecialType == SpecialType.System_Object;

    private static bool IsActivatorCreateInstance(SemanticModel model, InvocationExpressionSyntax invocation, ITypeParameterSymbol parameter)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
            || method.Name != "CreateInstance"
            || method.ContainingType?.ToDisplayString() != "System.Activator"
            || method.TypeArguments.Length != 1)
        {
            return false;
        }

        return SymbolEqualityComparer.Default.Equals(method.TypeArguments[0], parameter);
    }

    private static VarianceConstraintDependentSite Dependent(
        string code,
        string message,
        ITypeParameterSymbol parameter,
        SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan().StartLinePosition;
        return new VarianceConstraintDependentSite(
            Code: code,
            Message: message,
            TypeParameterName: parameter.Name,
            FilePath: node.SyntaxTree.FilePath,
            Line: span.Line + 1,
            Column: span.Character + 1);
    }

    private static string KindWord(ConstraintKind kind) => kind switch
    {
        ConstraintKind.Struct => "struct",
        ConstraintKind.Unmanaged => "unmanaged",
        ConstraintKind.New => "new",
        ConstraintKind.NotNull => "notnull",
        ConstraintKind.Default => "default",
        _ => "class",
    };

    private static string DefaultMessage(ITypeParameterSymbol parameter)
        => $"default(T) on value-type-constrained parameter '{parameter.Name}' at this site; Java falls back to "
           + "'null', which is a definite semantic error (a zero-value instance is expected); redesign the default usage";

    private static string ResidualNewMessage(ITypeParameterSymbol parameter)
        => $"residual 'new T()' with an object initializer or arguments at this site (T='{parameter.Name}'); "
           + "E10 does not rewrite this form and the direct conversion behavior is undefined; "
           + "rewrite the construction explicitly";

    private readonly record struct Dependents(
        int Default,
        int New,
        int Boxing,
        int Array,
        int Nullable,
        int Activator,
        List<VarianceConstraintDependentSite> Diagnostics)
    {
        public static Dependents Empty { get; } = new(0, 0, 0, 0, 0, 0, new List<VarianceConstraintDependentSite>());

        /// <summary>Keeps only construction/Activator facts (drops value-semantics buckets) for 'new()'-only constraints.</summary>
        public Dependents ConstructionOnly() => new(
            0,
            New,
            0,
            0,
            0,
            Activator,
            Diagnostics.Where(d => d.Code == VarianceCodes.ResidualNewTSite).ToList());
    }
}