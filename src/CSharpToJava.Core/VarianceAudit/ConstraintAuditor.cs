using System.Collections.Concurrent;
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
/// <remarks>
/// Performance: each tree is audited independently and in parallel. The dependent-site
/// scan used to re-walk the whole file once per constrained type parameter (K parameters
/// meant K full walks with K semantic queries per candidate); now one walk collects the
/// semantic shape of every candidate node once, and all parameters for that file match
/// against the same candidate set.
/// </remarks>
internal sealed class ConstraintAuditor
{
    private readonly Compilation _compilation;
    private readonly VarianceAuditStatistics _statistics;
    private readonly VarianceBaselineCache _baseline;

    public ConstraintAuditor(
        Compilation compilation,
        VarianceAuditStatistics statistics,
        VarianceBaselineCache baseline)
    {
        _compilation = compilation;
        _statistics = statistics;
        _baseline = baseline;
    }

    public void Audit(VarianceAuditAnalysis analysis)
    {
        var perTreeResults = new ConcurrentBag<TreeAuditResult>();
        Parallel.ForEach(
            _compilation.SyntaxTrees,
            () => new List<TreeAuditResult>(),
            (tree, _, local) =>
            {
                if (!tree.FilePath.EndsWith("__cs2j_global_usings.g.cs", StringComparison.Ordinal))
                {
                    local.Add(AuditTree(_compilation.GetSemanticModel(tree)));
                }

                return local;
            },
            local =>
            {
                foreach (var result in local)
                {
                    perTreeResults.Add(result);
                }
            });

        foreach (var result in perTreeResults)
        {
            Merge(result, analysis);
        }
    }

    private void Merge(TreeAuditResult result, VarianceAuditAnalysis analysis)
    {
        analysis.ConstraintSites.AddRange(result.ConstraintSites);
        analysis.ConstraintDependentSites.AddRange(result.DependentSites);

        _statistics.ConstraintsStruct += result.ConstraintsStruct;
        _statistics.ConstraintsUnmanaged += result.ConstraintsUnmanaged;
        _statistics.ConstraintsNew += result.ConstraintsNew;
        _statistics.ConstraintsNotNull += result.ConstraintsNotNull;
        _statistics.ConstraintsDefault += result.ConstraintsDefault;
        _statistics.ConstraintsClass += result.ConstraintsClass;
        _statistics.ConstraintDependentDefaultSites += result.DependentDefaults;
        _statistics.ConstraintDependentNewSites += result.DependentNews;
        _statistics.ConstraintDependentBoxingSites += result.DependentBoxing;
        _statistics.ConstraintDependentArraySites += result.DependentArrays;
        _statistics.ConstraintDependentNullableSites += result.DependentNullables;
    }

    private static TreeAuditResult AuditTree(SemanticModel model)
    {
        var result = new TreeAuditResult(model);
        foreach (var node in model.SyntaxTree.GetRoot().DescendantNodes())
        {
            switch (node)
            {
                case TypeDeclarationSyntax declaration when declaration.TypeParameterList != null:
                    AuditType(model, declaration, model.GetDeclaredSymbol(declaration), result);
                    break;
                case DelegateDeclarationSyntax declaration when declaration.TypeParameterList != null:
                    AuditType(model, declaration, model.GetDeclaredSymbol(declaration), result);
                    break;
                case MethodDeclarationSyntax declaration when declaration.TypeParameterList != null:
                    AuditMethod(model, declaration, model.GetDeclaredSymbol(declaration), result);
                    break;
                case LocalFunctionStatementSyntax declaration when declaration.TypeParameterList != null:
                    AuditMethod(model, declaration, model.GetDeclaredSymbol(declaration), result);
                    break;
            }
        }

        return result;
    }

    private static void AuditType(
        SemanticModel model,
        SyntaxNode declaration,
        INamedTypeSymbol? symbol,
        TreeAuditResult result)
    {
        if (symbol == null)
        {
            return;
        }

        var declaringName = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        foreach (var parameter in symbol.TypeParameters)
        {
            ProcessTypeParameter(model, declaration, parameter, declaringName, result);
        }
    }

    private static void AuditMethod(
        SemanticModel model,
        SyntaxNode declaration,
        IMethodSymbol? symbol,
        TreeAuditResult result)
    {
        if (symbol == null)
        {
            return;
        }

        var declaringName = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        foreach (var parameter in symbol.TypeParameters)
        {
            ProcessTypeParameter(model, declaration, parameter, declaringName, result);
        }
    }

    private static void ProcessTypeParameter(
        SemanticModel model,
        SyntaxNode declaration,
        ITypeParameterSymbol parameter,
        string declaringName,
        TreeAuditResult result)
    {
        var clause = ConstraintClauseOf(declaration, parameter.Name);
        var kinds = ClassifyConstraints(clause, parameter);
        if (kinds.Count == 0)
        {
            return;
        }

        var valueType = kinds.Contains(ConstraintKind.Struct) || kinds.Contains(ConstraintKind.Unmanaged);
        var construction = kinds.Contains(ConstraintKind.New) || valueType;
        var dependents = construction
            ? result.Candidates.Match(parameter)
            : Dependents.Empty;
        if (!valueType)
        {
            // A 'new()'-only constraint guarantees reference-or-value construction, not
            // value semantics: only the construction/Activator association applies (D5b②).
            dependents = dependents.ConstructionOnly();
        }

        foreach (var kind in kinds)
        {
            AddConstraintSite(model, parameter, declaringName, kind, dependents, result);
        }

        if (valueType || kinds.Contains(ConstraintKind.New))
        {
            result.DependentDefaults += dependents.Default;
            result.DependentNews += dependents.New;
            result.DependentBoxing += dependents.Boxing;
            result.DependentArrays += dependents.Array;
            result.DependentNullables += dependents.Nullable;
            result.DependentSites.AddRange(dependents.Diagnostics);
        }
    }

    private static void AddConstraintSite(
        SemanticModel model,
        ITypeParameterSymbol parameter,
        string declaringName,
        ConstraintKind kind,
        Dependents dependents,
        TreeAuditResult result)
    {
        var location = parameter.Locations.FirstOrDefault(candidate => candidate.IsInSource);
        var span = location?.GetLineSpan().StartLinePosition;
        result.ConstraintSites.Add(new VarianceConstraintSite(
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
                result.ConstraintsStruct++;
                break;
            case ConstraintKind.Unmanaged:
                result.ConstraintsUnmanaged++;
                break;
            case ConstraintKind.New:
                result.ConstraintsNew++;
                break;
            case ConstraintKind.NotNull:
                result.ConstraintsNotNull++;
                break;
            case ConstraintKind.Default:
                result.ConstraintsDefault++;
                break;
            case ConstraintKind.Class:
                result.ConstraintsClass++;
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

    /// <summary>Per-tree accumulator; owns the lazily-built dependent-site candidates.</summary>
    private sealed class TreeAuditResult
    {
        private DependentCandidates? _candidates;

        public TreeAuditResult(SemanticModel model)
        {
            Model = model;
        }

        public SemanticModel Model { get; }
        public List<VarianceConstraintSite> ConstraintSites { get; } = new();
        public List<VarianceConstraintDependentSite> DependentSites { get; } = new();

        public int ConstraintsStruct;
        public int ConstraintsUnmanaged;
        public int ConstraintsNew;
        public int ConstraintsNotNull;
        public int ConstraintsDefault;
        public int ConstraintsClass;

        public int DependentDefaults;
        public int DependentNews;
        public int DependentBoxing;
        public int DependentArrays;
        public int DependentNullables;

        public DependentCandidates Candidates => _candidates ??= DependentCandidates.Build(Model);
    }

    /// <summary>
    /// Semantic shape, resolved once, of every node that could be a value-constraint
    /// dependent site. Each constrained type parameter then matches its own symbol
    /// against the candidate types without triggering any further semantic query.
    /// </summary>
    private sealed class DependentCandidates
    {
        private readonly List<(ITypeSymbol Type, SyntaxNode Node)> _defaults = new();
        private readonly List<(ITypeSymbol ConvertedType, SyntaxNode Node)> _defaultLiterals = new();
        private readonly List<(ITypeSymbol Type, ObjectCreationExpressionSyntax Creation)> _news = new();
        private readonly List<ITypeSymbol> _activators = new();
        private readonly List<ITypeSymbol> _arrayElements = new();
        private readonly List<ITypeSymbol> _nullableArguments = new();
        private readonly List<ITypeSymbol> _boxing = new();

        // One entry per ==/!= node (never two: matching either operand counted once).
        private readonly List<(ITypeSymbol? Left, ITypeSymbol? Right)> _equalityPairs = new();

        private DependentCandidates()
        {
        }

        public static DependentCandidates Build(SemanticModel model)
        {
            var candidates = new DependentCandidates();
            foreach (var node in model.SyntaxTree.GetRoot().DescendantNodes())
            {
                switch (node)
                {
                    case DefaultExpressionSyntax expression:
                        var defaultInfo = model.GetTypeInfo(expression);
                        if (defaultInfo.Type != null)
                        {
                            candidates._defaults.Add((defaultInfo.Type, expression));
                        }

                        break;
                    case LiteralExpressionSyntax literal
                        when literal.IsKind(SyntaxKind.DefaultLiteralExpression):
                        var literalInfo = model.GetTypeInfo(literal);
                        if (literalInfo.ConvertedType != null)
                        {
                            candidates._defaultLiterals.Add((literalInfo.ConvertedType, literal));
                        }

                        break;
                    case ObjectCreationExpressionSyntax creation:
                        if (model.GetTypeInfo(creation).Type is { } createdType)
                        {
                            candidates._news.Add((createdType, creation));
                        }

                        break;
                    case InvocationExpressionSyntax invocation when IsActivatorCreateInstance(model, invocation, out var typeArgument):
                        candidates._activators.Add(typeArgument!);
                        break;
                    case ArrayTypeSyntax array:
                        if (model.GetTypeInfo(array).Type is IArrayTypeSymbol arraySymbol)
                        {
                            candidates._arrayElements.Add(arraySymbol.ElementType);
                        }

                        break;
                    case NullableTypeSyntax nullable:
                        AddNullableCandidate(model, nullable, candidates);
                        break;
                    case GenericNameSyntax generic when generic.Identifier.ValueText == "Nullable":
                        AddNullableCandidate(model, generic, candidates);
                        break;
                    case CastExpressionSyntax cast
                        when TypeMatches(model, cast.Expression, out var expressionType)
                             && IsObjectType(model, cast.Type):
                        candidates._boxing.Add(expressionType!);
                        break;
                    case LockStatementSyntax lockStatement
                        when TypeMatches(model, lockStatement.Expression, out var lockType):
                        candidates._boxing.Add(lockType!);
                        break;
                    case BinaryExpressionSyntax binary
                        when (binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression)):
                        candidates._equalityPairs.Add((
                            model.GetTypeInfo(binary.Left).Type,
                            model.GetTypeInfo(binary.Right).Type));
                        break;
                    case BinaryExpressionSyntax binary
                        when (binary.IsKind(SyntaxKind.IsExpression) || binary.IsKind(SyntaxKind.AsExpression))
                             && TypeMatches(model, binary.Left, out var isAsType)
                             && IsObjectType(model, binary.Right):
                        candidates._boxing.Add(isAsType!);
                        break;
                }
            }

            return candidates;
        }

        private static void AddNullableCandidate(SemanticModel model, SyntaxNode node, DependentCandidates candidates)
        {
            if (model.GetTypeInfo(node).Type is INamedTypeSymbol named
                && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                && named.TypeArguments.Length == 1)
            {
                candidates._nullableArguments.Add(named.TypeArguments[0]);
            }
        }

        public Dependents Match(ITypeParameterSymbol parameter)
        {
            var diagnostics = new List<VarianceConstraintDependentSite>();
            var defaults = 0;
            var news = 0;
            var boxing = 0;
            var arrays = 0;
            var nullables = 0;
            var activators = 0;

            foreach (var (type, node) in _defaults)
            {
                if (SymbolEqualityComparer.Default.Equals(type, parameter))
                {
                    defaults++;
                    diagnostics.Add(Dependent(VarianceCodes.DefaultOnValueTypeConstraint, DefaultMessage(parameter), parameter, node));
                }
            }

            foreach (var (convertedType, node) in _defaultLiterals)
            {
                if (SymbolEqualityComparer.Default.Equals(convertedType, parameter))
                {
                    defaults++;
                    diagnostics.Add(Dependent(VarianceCodes.DefaultOnValueTypeConstraint, DefaultMessage(parameter), parameter, node));
                }
            }

            foreach (var (type, creation) in _news)
            {
                if (!SymbolEqualityComparer.Default.Equals(type, parameter))
                {
                    continue;
                }

                news++;
                if (creation.ArgumentList?.Arguments.Count > 0 || creation.Initializer != null)
                {
                    diagnostics.Add(Dependent(
                        VarianceCodes.ResidualNewTSite,
                        ResidualNewMessage(parameter), parameter, creation));
                }
            }

            foreach (var typeArgument in _activators)
            {
                if (SymbolEqualityComparer.Default.Equals(typeArgument, parameter))
                {
                    activators++;
                    news++;
                }
            }

            foreach (var element in _arrayElements)
            {
                if (SymbolEqualityComparer.Default.Equals(element, parameter))
                {
                    arrays++;
                }
            }

            foreach (var argument in _nullableArguments)
            {
                if (SymbolEqualityComparer.Default.Equals(argument, parameter))
                {
                    nullables++;
                }
            }

            foreach (var type in _boxing)
            {
                if (SymbolEqualityComparer.Default.Equals(type, parameter))
                {
                    boxing++;
                }
            }

            foreach (var (left, right) in _equalityPairs)
            {
                if (SymbolEqualityComparer.Default.Equals(left, parameter)
                    || SymbolEqualityComparer.Default.Equals(right, parameter))
                {
                    boxing++;
                }
            }

            return new Dependents(defaults, news, boxing, arrays, nullables, activators, diagnostics);
        }

        private static bool TypeMatches(SemanticModel model, SyntaxNode node, out ITypeSymbol? type)
        {
            type = model.GetTypeInfo(node).Type;
            return type != null;
        }

        private static bool IsObjectType(SemanticModel model, SyntaxNode node)
            => model.GetTypeInfo(node).Type?.SpecialType == SpecialType.System_Object;

        private static bool IsActivatorCreateInstance(
            SemanticModel model,
            InvocationExpressionSyntax invocation,
            out ITypeSymbol? typeArgument)
        {
            typeArgument = null;
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
                || method.Name != "CreateInstance"
                || method.ContainingType?.ToDisplayString() != "System.Activator"
                || method.TypeArguments.Length != 1)
            {
                return false;
            }

            typeArgument = method.TypeArguments[0];
            return true;
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
    }

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
