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
        _statistics.ConstraintDependentNullableValueSites += result.DependentNullableValueSites;
        _statistics.ConstraintLazySites += result.ConstraintsLazy;
        _statistics.ConstraintUnsafeSites += result.ConstraintsUnsafe;
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

        // 'System.Enum' is resolved through the symbol's constraint types, never by name/text.
        // Enum values are immutable value types: a boxed copy is observationally identical to a
        // Java enum reference, and a Java enum array element is immutable too. Boxing/array
        // dependencies are therefore excused only when the constraint actually names System.Enum.
        var includesEnum = valueType && parameter.ConstraintTypes.Any(IsSystemEnum);

        var dependents = construction
            ? result.Candidates.Match(parameter, includesEnum)
            : Dependents.Empty;
        if (!valueType)
        {
            // A 'new()'-only constraint guarantees reference-or-value construction, not
            // value semantics: only the construction/Activator association applies (D5b②).
            dependents = dependents.ConstructionOnly();
        }

        var safeSites = dependents.SafeBoxing + dependents.SafeArray + dependents.Nullable
            + dependents.SafeNullableValueOperations;
        var unsafeSites = dependents.Default
            + Math.Max(0, dependents.New - dependents.Activator)
            + (dependents.Boxing - dependents.SafeBoxing)
            + (dependents.Array - dependents.SafeArray)
            + dependents.NullableValueOperations
            + dependents.UnmanagedLayout;
        var judgment = valueType ? (unsafeSites == 0 ? "lazy" : "unsafe") : string.Empty;
        var rationale = valueType
            ? BuildRationale(kinds, dependents, unsafeSites)
            : string.Empty;

        foreach (var kind in kinds)
        {
            AddConstraintSite(
                model, parameter, declaringName, kind, dependents, includesEnum,
                safeSites, unsafeSites, judgment, rationale, result);
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

        if (valueType)
        {
            if (unsafeSites == 0)
            {
                result.ConstraintsLazy++;
            }
            else
            {
                result.ConstraintsUnsafe++;
            }

            result.DependentNullableValueSites += dependents.NullableValueOperations;
        }
    }

    private static bool IsSystemEnum(ITypeSymbol type)
        => type is INamedTypeSymbol named && named.SpecialType == SpecialType.System_Enum;

    /// <summary>
    /// Explains the value-semantics verdict for one struct/unmanaged constraint. A <c>lazy</c>
    /// verdict is only possible when every dependent bucket is either empty or excused
    /// (enum boxing/array, nullable type use): the constraint then only restricts callers, all of
    /// which are already-compiled C#, so Java erasure changes no observable behavior.
    /// </summary>
    private static string BuildRationale(
        IReadOnlyCollection<ConstraintKind> kinds,
        Dependents dependents,
        int unsafeSites)
    {
        var notes = new List<string>();
        if (dependents.Default > 0)
        {
            notes.Add($"{dependents.Default} default(T)/default site(s) expect a zero-value instance, which Java erasure renders as null");
        }

        var residualNew = Math.Max(0, dependents.New - dependents.Activator);
        if (residualNew > 0)
        {
            notes.Add($"{residualNew} residual new T() site(s) are not covered by E10");
        }

        if (dependents.SafeBoxing > 0 || dependents.SafeArray > 0)
        {
            notes.Add($"{dependents.SafeBoxing} boxing-site(s) and {dependents.SafeArray} array-site(s) are excused because the constraint names System.Enum (symbol-resolved): enum values are immutable value types, a boxed copy and a Java enum reference are observationally indistinguishable, and Java enum array elements are immutable");
        }

        var unsafeBoxing = dependents.Boxing - dependents.SafeBoxing;
        var unsafeArray = dependents.Array - dependents.SafeArray;
        if (unsafeBoxing > 0 || unsafeArray > 0)
        {
            notes.Add($"{unsafeBoxing} non-enum boxing-site(s) and {unsafeArray} non-enum array-site(s) still observe value identity");
        }

        if (dependents.Nullable > 0)
        {
            notes.Add($"{dependents.Nullable} nullable type-use site(s) are safe: the T? occurrence only names a parameter/return/field/local/base type, and under Java reference erasure null means 'no value' while a struct instance itself is never null");
        }

        if (dependents.SafeNullableValueOperations > 0)
        {
            notes.Add($"{dependents.SafeNullableValueOperations} nullable value-operation site(s) are safe: HasValue maps to a receiver != null check, ?? maps to a null ternary, a dominating same-receiver null guard makes .Value the post-guard unwrap, and zero-argument GetValueOrDefault() on T? maps to the receiver");
        }

        if (dependents.NullableValueOperations > 0)
        {
            notes.Add($"{dependents.NullableValueOperations} unguarded nullable value-operation site(s) (.Value without a dominating same-receiver HasValue/null guard, or an unproven GetValueOrDefault form) still rely on C#'s throw-on-null semantics");
        }

        if (kinds.Contains(ConstraintKind.Unmanaged))
        {
            notes.Add(dependents.UnmanagedLayout == 0
                ? "no pointer/sizeof/marshal/Unsafe dependent site: the constraint only restricts generic substitution"
                : $"{dependents.UnmanagedLayout} pointer/sizeof/marshal/Unsafe site(s) assume an unmanaged value layout");
        }

        if (notes.Count == 0)
        {
            notes.Add("no dependent site found");
        }

        var prefix = unsafeSites == 0
            ? "judged lazy: the constraint only restricts callers, and every caller is already-compiled C#; with no value-semantics dependent site, Java erasure changes no observable behavior"
            : $"judged unsafe: {unsafeSites} dependent site(s) still rely on value semantics";
        return prefix + " (" + string.Join("; ", notes) + ")";
    }

    private static void AddConstraintSite(
        SemanticModel model,
        ITypeParameterSymbol parameter,
        string declaringName,
        ConstraintKind kind,
        Dependents dependents,
        bool includesEnum,
        int safeSites,
        int unsafeSites,
        string judgment,
        string rationale,
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
            Column: (span?.Character ?? 0) + 1,
            IncludesEnum: includesEnum,
            DependentNullableValueSites: dependents.NullableValueOperations,
            SafeDependentSites: safeSites,
            UnsafeDependentSites: unsafeSites,
            Judgment: judgment,
            Rationale: rationale));

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
        public int DependentNullableValueSites;

        public int ConstraintsLazy;
        public int ConstraintsUnsafe;

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
        private readonly List<(ITypeSymbol Type, SyntaxNode Node)> _nullableArguments = new();
        private readonly List<(ITypeSymbol Type, SyntaxNode Node, bool Safe, string Rationale)> _nullableValueOperations = new();
        private readonly List<(ITypeSymbol Type, SyntaxNode Node)> _unmanagedLayout = new();
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
                    case InvocationExpressionSyntax invocation
                        when TryGetInteropTypeArgument(model, invocation, out var interopType):
                        candidates._unmanagedLayout.Add((interopType!, invocation));
                        break;
                    case PointerTypeSyntax pointer:
                        if (model.GetTypeInfo(pointer.ElementType).Type is { } pointee)
                        {
                            candidates._unmanagedLayout.Add((pointee, pointer));
                        }

                        break;
                    case SizeOfExpressionSyntax sizeOf:
                        if (model.GetTypeInfo(sizeOf.Type).Type is { } sized)
                        {
                            candidates._unmanagedLayout.Add((sized, sizeOf));
                        }

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
                    case MemberAccessExpressionSyntax member
                        when member.Name.Identifier.ValueText is "HasValue" or "Value" or "GetValueOrDefault":
                        AddNullableValueOperation(model, member.Expression, member, candidates);
                        break;
                    case BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression):
                        AddNullableValueOperation(model, coalesce.Left, coalesce, candidates);
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
                candidates._nullableArguments.Add((named.TypeArguments[0], node));
            }
        }

        /// <summary>
        /// Records a value-semantics use of <c>T?</c> (member access or <c>??</c>) only when the
        /// receiver/left operand is really <see cref="System.Nullable{T}"/>; <c>ByRef&lt;T?&gt;.Value</c>
        /// and other wrappers are not nullable unwrapping and must stay out of this bucket.
        /// Each site is classified here (where the semantic model is available) as safe or
        /// unsafe; unsafe sites keep the original Error treatment.
        /// </summary>
        private static void AddNullableValueOperation(
            SemanticModel model,
            SyntaxNode expression,
            SyntaxNode node,
            DependentCandidates candidates)
        {
            if (model.GetTypeInfo(expression).Type is not INamedTypeSymbol named
                || named.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
                || named.TypeArguments.Length != 1)
            {
                return;
            }

            var (safe, rationale) = ClassifyNullableValueOperation(model, node, named);
            candidates._nullableValueOperations.Add((named.TypeArguments[0], node, safe, rationale));
        }

        /// <summary>
        /// Java mapping of the nullable member surface (C22/README):
        /// <c>HasValue</c> → <c>receiver != null</c>; guarded <c>Value</c> → the post-guard unwrap
        /// (<c>io.github.ningpp.compat.Nullable.value</c> for primitives, the receiver itself for
        /// reference-mapped value types); zero-argument <c>GetValueOrDefault()</c> on <c>T?</c> →
        /// the receiver itself (null is <c>default(T)</c> under erasure); <c>??</c> → a
        /// <c>receiver != null ? receiver : fallback</c> ternary. An unguarded <c>.Value</c>
        /// (C# throws; Java primitive unbox throws, reference-mapped receiver does not) and any
        /// other <c>GetValueOrDefault</c> form stay unsafe.
        /// </summary>
        private static (bool Safe, string Rationale) ClassifyNullableValueOperation(
            SemanticModel model,
            SyntaxNode node,
            INamedTypeSymbol nullable)
        {
            switch (node)
            {
                case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "HasValue" }:
                    return (true,
                        "HasValue maps to a receiver != null null check; null/no-value equivalence holds");

                case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Value" } member:
                    if (HasDominatingNonNullGuard(model, member.Expression, member, out var evidence))
                    {
                        return (true,
                            $"{evidence}; the guarded .Value is the post-guard unwrap (compat Nullable.value "
                            + "for primitives, the receiver itself for reference-mapped value types)");
                    }

                    return (false,
                        "unguarded nullable .Value: no dominating same-receiver HasValue/null check proves "
                        + "the receiver non-null, so C#'s throw-on-null and the Java mapping are not equivalent");

                case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetValueOrDefault" } member:
                    if (IsZeroArgumentInvocation(member)
                        && nullable.TypeArguments[0].TypeKind == TypeKind.TypeParameter)
                    {
                        return (true,
                            "zero-argument GetValueOrDefault() on T? maps to the receiver itself; null is "
                            + "default(T) under reference erasure");
                    }

                    return (false,
                        "GetValueOrDefault form is not proven equivalent at this site; only the zero-argument "
                        + "T? form maps to the receiver (other forms need the C22 lift)");

                case BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression):
                    return (true,
                        "?? maps to a receiver != null ? receiver : fallback ternary, equivalent to "
                        + "HasValue ? Value : fallback");

                default:
                    return (false, "unclassified nullable value operation");
            }
        }

        private static bool IsZeroArgumentInvocation(MemberAccessExpressionSyntax member)
            => member.Parent is InvocationExpressionSyntax invocation
               && invocation.Expression == member
               && invocation.ArgumentList.Arguments.Count == 0;

        /// <summary>
        /// Symbol-level dominance check: the site must sit in a branch guarded by a null test on
        /// the <em>same</em> receiver symbol. Supported forms are the true branch of
        /// <c>if (x.HasValue)</c>/<c>if (x != null)</c>/<c>if (x is not null)</c>, the else branch of
        /// their negations, the corresponding conditional-expression arm, and the code after an
        /// early-exit guard (<c>if (x is null) return;</c>). Merely seeing <c>HasValue</c> for a
        /// different receiver, or anywhere outside a dominating position, does not qualify.
        /// A preceding statement that writes to the receiver ends the search.
        /// </summary>
        private static bool HasDominatingNonNullGuard(
            SemanticModel model,
            ExpressionSyntax receiver,
            SyntaxNode site,
            out string evidence)
        {
            evidence = string.Empty;
            var receiverSymbol = model.GetSymbolInfo(receiver).Symbol;
            if (receiverSymbol is null)
            {
                return false;
            }

            foreach (var ancestor in site.Ancestors())
            {
                switch (ancestor)
                {
                    case IfStatementSyntax ifStatement:
                        if (ifStatement.Statement.Span.Contains(site.Span)
                            && TryNonNullGuard(model, ifStatement.Condition, whenTrue: true, out var thenReceiver, out var thenForm)
                            && SymbolEqualityComparer.Default.Equals(receiverSymbol, thenReceiver))
                        {
                            evidence = $"dominated by the true branch of a same-receiver '{thenForm}' check";
                            return true;
                        }

                        if (ifStatement.Else is { Statement: var elseStatement }
                            && elseStatement.Span.Contains(site.Span)
                            && TryNonNullGuard(model, ifStatement.Condition, whenTrue: false, out var elseReceiver, out var elseForm)
                            && SymbolEqualityComparer.Default.Equals(receiverSymbol, elseReceiver))
                        {
                            evidence = $"dominated by the else branch of a same-receiver '{elseForm}' check";
                            return true;
                        }

                        break;

                    case ConditionalExpressionSyntax conditional:
                        if (conditional.WhenTrue.Span.Contains(site.Span)
                            && TryNonNullGuard(model, conditional.Condition, whenTrue: true, out var trueReceiver, out var trueForm)
                            && SymbolEqualityComparer.Default.Equals(receiverSymbol, trueReceiver))
                        {
                            evidence = $"dominated by the true arm of a same-receiver '{trueForm}' conditional";
                            return true;
                        }

                        if (conditional.WhenFalse.Span.Contains(site.Span)
                            && TryNonNullGuard(model, conditional.Condition, whenTrue: false, out var falseReceiver, out var falseForm)
                            && SymbolEqualityComparer.Default.Equals(receiverSymbol, falseReceiver))
                        {
                            evidence = $"dominated by the false arm of a same-receiver '{falseForm}' conditional";
                            return true;
                        }

                        break;

                    case BlockSyntax block:
                        var containing = block.Statements.FirstOrDefault(statement => statement.Span.Contains(site.Span));
                        if (containing is null)
                        {
                            break;
                        }

                        var index = block.Statements.IndexOf(containing);
                        for (var i = index - 1; i >= 0; i--)
                        {
                            var previous = block.Statements[i];
                            if (WritesTo(model, previous, receiverSymbol))
                            {
                                break;
                            }

                            if (previous is IfStatementSyntax { Else: null, Statement: var guardedBody } earlyExit
                                && AlwaysExits(guardedBody)
                                && TryNonNullGuard(model, earlyExit.Condition, whenTrue: false, out var exitReceiver, out var exitForm)
                                && SymbolEqualityComparer.Default.Equals(receiverSymbol, exitReceiver))
                            {
                                evidence = $"dominated after a same-receiver '{exitForm}' early-exit guard";
                                return true;
                            }
                        }

                        break;
                }
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="condition"/> being <paramref name="whenTrue"/> proves that the
        /// tested receiver is non-null. Only simple null predicates (and an AND conjunction in the
        /// true branch) qualify; disjunctions and unknown forms are rejected.
        /// </summary>
        private static bool TryNonNullGuard(
            SemanticModel model,
            ExpressionSyntax condition,
            bool whenTrue,
            out ISymbol? receiver,
            out string form)
        {
            receiver = null;
            form = string.Empty;

            switch (condition)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    return TryNonNullGuard(model, parenthesized.Expression, whenTrue, out receiver, out form);

                case PrefixUnaryExpressionSyntax not when not.IsKind(SyntaxKind.LogicalNotExpression):
                    return TryNonNullGuard(model, not.Operand, !whenTrue, out receiver, out form);

                case IsPatternExpressionSyntax isPattern:
                    if (!TryNullPattern(isPattern.Pattern, out var assertsNull) || assertsNull == whenTrue)
                    {
                        return false;
                    }

                    receiver = model.GetSymbolInfo(isPattern.Expression).Symbol;
                    form = assertsNull ? "is null" : "is not null";
                    return receiver != null;

                case BinaryExpressionSyntax conjunction
                    when conjunction.IsKind(SyntaxKind.LogicalAndExpression) && whenTrue:
                    if (TryNonNullGuard(model, conjunction.Left, whenTrue: true, out receiver, out form)
                        || TryNonNullGuard(model, conjunction.Right, whenTrue: true, out receiver, out form))
                    {
                        return true;
                    }

                    receiver = null;
                    form = string.Empty;
                    return false;

                case BinaryExpressionSyntax equality
                    when equality.IsKind(SyntaxKind.EqualsExpression) || equality.IsKind(SyntaxKind.NotEqualsExpression):
                    return TryEqualityNonNullGuard(model, equality, whenTrue, out receiver, out form);

                case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "HasValue" } member when whenTrue:
                    receiver = model.GetSymbolInfo(member.Expression).Symbol;
                    form = "HasValue";
                    return receiver != null;

                default:
                    return false;
            }
        }

        private static bool TryNullPattern(PatternSyntax pattern, out bool assertsNull)
        {
            assertsNull = false;
            switch (pattern)
            {
                case ConstantPatternSyntax { Expression: LiteralExpressionSyntax literal }
                    when literal.IsKind(SyntaxKind.NullLiteralExpression):
                    assertsNull = true;
                    return true;

                case UnaryPatternSyntax { Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax literal } }
                    when literal.IsKind(SyntaxKind.NullLiteralExpression):
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryEqualityNonNullGuard(
            SemanticModel model,
            BinaryExpressionSyntax equality,
            bool whenTrue,
            out ISymbol? receiver,
            out string form)
        {
            receiver = null;
            form = string.Empty;
            var isEquals = equality.IsKind(SyntaxKind.EqualsExpression);

            var other = IsNullLiteral(equality.Right) ? equality.Left
                : IsNullLiteral(equality.Left) ? equality.Right
                : null;
            if (other != null)
            {
                // 'x == null' proves non-null when false; 'x != null' proves it when true.
                if (isEquals == whenTrue)
                {
                    return false;
                }

                receiver = model.GetSymbolInfo(other).Symbol;
                form = isEquals ? "== null" : "!= null";
                return receiver != null;
            }

            if (TryMatchHasValueComparison(equality.Left, equality.Right, out var hasValue, out var expected)
                || TryMatchHasValueComparison(equality.Right, equality.Left, out hasValue, out expected))
            {
                var hasValueWhenTrue = isEquals ? expected : !expected;
                if (hasValueWhenTrue != whenTrue)
                {
                    return false;
                }

                receiver = model.GetSymbolInfo(hasValue!.Expression).Symbol;
                form = "HasValue";
                return receiver != null;
            }

            return false;
        }

        private static bool TryMatchHasValueComparison(
            ExpressionSyntax left,
            ExpressionSyntax right,
            out MemberAccessExpressionSyntax? hasValue,
            out bool expected)
        {
            hasValue = null;
            expected = false;
            if (left is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "HasValue" } member
                && right is LiteralExpressionSyntax literal
                && literal.Token.Value is bool value)
            {
                hasValue = member;
                expected = value;
                return true;
            }

            return false;
        }

        private static bool IsNullLiteral(ExpressionSyntax expression)
            => expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.NullLiteralExpression);

        /// <summary>
        /// A preceding statement invalidates an earlier guard when it can change the receiver:
        /// assignment, increment/decrement, or a ref/out argument.
        /// </summary>
        private static bool WritesTo(SemanticModel model, StatementSyntax statement, ISymbol receiver)
        {
            foreach (var node in statement.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case AssignmentExpressionSyntax assignment
                        when SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, receiver):
                        return true;

                    case PrefixUnaryExpressionSyntax prefix
                        when (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression))
                             && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(prefix.Operand).Symbol, receiver):
                        return true;

                    case PostfixUnaryExpressionSyntax postfix
                        when (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression))
                             && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(postfix.Operand).Symbol, receiver):
                        return true;

                    case ArgumentSyntax argument
                        when argument.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword
                             && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(argument.Expression).Symbol, receiver):
                        return true;
                }
            }

            return false;
        }

        private static bool AlwaysExits(StatementSyntax statement) => statement switch
        {
            ReturnStatementSyntax or ThrowStatementSyntax or ContinueStatementSyntax
                or BreakStatementSyntax or GotoStatementSyntax => true,
            BlockSyntax block => block.Statements.Count > 0 && AlwaysExits(block.Statements[^1]),
            IfStatementSyntax { Else: { } elseClause } ifStatement
                => AlwaysExits(ifStatement.Statement) && AlwaysExits(elseClause.Statement),
            _ => false,
        };

        /// <summary>
        /// Pointer/sizeof/marshal/Unsafe sites that assume an unmanaged layout. Only the type
        /// argument of the interop call is matched here; <c>sizeof(T)</c> and <c>T*</c> are
        /// collected by their own syntax cases.
        /// </summary>
        private static bool TryGetInteropTypeArgument(
            SemanticModel model,
            InvocationExpressionSyntax invocation,
            out ITypeSymbol? type)
        {
            type = null;
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
                || method.ContainingType?.ToDisplayString() is not (
                    "System.Runtime.InteropServices.Marshal"
                    or "System.Runtime.InteropServices.MemoryMarshal"
                    or "System.Runtime.CompilerServices.Unsafe"))
            {
                return false;
            }

            foreach (var argument in method.TypeArguments)
            {
                if (argument is ITypeParameterSymbol)
                {
                    type = argument;
                    return true;
                }
            }

            return false;
        }

        public Dependents Match(ITypeParameterSymbol parameter, bool includesEnum)
        {
            var diagnostics = new List<VarianceConstraintDependentSite>();
            var defaults = 0;
            var news = 0;
            var boxing = 0;
            var arrays = 0;
            var nullables = 0;
            var nullableValueOperations = 0;
            var safeNullableValueOperations = 0;
            var unmanagedLayout = 0;
            var activators = 0;

            foreach (var (type, node) in _defaults)
            {
                if (SymbolEqualityComparer.Default.Equals(type, parameter))
                {
                    defaults++;
                    diagnostics.Add(Dependent(
                        VarianceCodes.DefaultOnValueTypeConstraint, DefaultMessage(parameter), parameter, node,
                        rationale: "default(T) expects a zero-value instance; Java erasure renders it as null"));
                }
            }

            foreach (var (convertedType, node) in _defaultLiterals)
            {
                if (SymbolEqualityComparer.Default.Equals(convertedType, parameter))
                {
                    defaults++;
                    diagnostics.Add(Dependent(
                        VarianceCodes.DefaultOnValueTypeConstraint, DefaultMessage(parameter), parameter, node,
                        rationale: "default expects a zero-value instance; Java erasure renders it as null"));
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
                        ResidualNewMessage(parameter), parameter, creation,
                        rationale: "residual new T() form is not rewritten by E10"));
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

            foreach (var (argument, node) in _nullableArguments)
            {
                if (SymbolEqualityComparer.Default.Equals(argument, parameter))
                {
                    nullables++;
                    diagnostics.Add(Dependent(
                        VarianceCodes.ValueTypeDependentSite,
                        $"nullable type-use site on '{parameter.Name}?': the T? occurrence only names a parameter, "
                        + "return, field, local or base type; under Java reference erasure null means 'no value' and a "
                        + "non-null instance always has a value, so the type use itself is safe",
                        parameter, node,
                        safe: true,
                        rationale: "type-use-only T?; null/no-value equivalence under reference erasure"));
                }
            }

            foreach (var (type, node, safe, rationale) in _nullableValueOperations)
            {
                if (!SymbolEqualityComparer.Default.Equals(type, parameter))
                {
                    continue;
                }

                if (safe)
                {
                    safeNullableValueOperations++;
                    diagnostics.Add(Dependent(
                        VarianceCodes.StructConstraint,
                        $"nullable value operation '{OperationWord(node)}' on '{parameter.Name}?': {rationale}",
                        parameter, node,
                        safe: true,
                        rationale: rationale));
                    continue;
                }

                nullableValueOperations++;
                diagnostics.Add(Dependent(
                    VarianceCodes.StructConstraint,
                    $"nullable value operation '{OperationWord(node)}' on '{parameter.Name}?': {rationale}; "
                    + "Java reference erasure is not proven safe here",
                    parameter, node,
                    rationale: rationale));
            }

            foreach (var (type, node) in _unmanagedLayout)
            {
                if (SymbolEqualityComparer.Default.Equals(type, parameter))
                {
                    unmanagedLayout++;
                    diagnostics.Add(Dependent(
                        VarianceCodes.UnmanagedConstraint,
                        $"pointer/sizeof/marshal/Unsafe site on unmanaged-constrained '{parameter.Name}': the site "
                        + "assumes an unmanaged value layout that Java erasure does not preserve",
                        parameter, node,
                        rationale: "unmanaged layout dependency"));
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

            // Enum boxing/array sites are safe only when the constraint symbol actually names
            // System.Enum; every other struct constraint keeps the original (unsafe) treatment.
            var safeBoxing = includesEnum ? boxing : 0;
            var safeArray = includesEnum ? arrays : 0;

            return new Dependents(
                defaults, news, boxing, arrays, nullables, activators,
                safeBoxing, safeArray, nullableValueOperations, safeNullableValueOperations,
                unmanagedLayout, diagnostics);
        }

        private static string OperationWord(SyntaxNode node) => node switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            BinaryExpressionSyntax => "??",
            _ => "value operation",
        };

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
            SyntaxNode node,
            bool safe = false,
            string? rationale = null)
        {
            var span = node.GetLocation().GetLineSpan().StartLinePosition;
            return new VarianceConstraintDependentSite(
                Code: code,
                Message: message,
                TypeParameterName: parameter.Name,
                FilePath: node.SyntaxTree.FilePath,
                Line: span.Line + 1,
                Column: span.Character + 1,
                Safe: safe,
                Rationale: rationale);
        }
    }

    private readonly record struct Dependents(
        int Default,
        int New,
        int Boxing,
        int Array,
        int Nullable,
        int Activator,
        int SafeBoxing,
        int SafeArray,
        int NullableValueOperations,
        int SafeNullableValueOperations,
        int UnmanagedLayout,
        List<VarianceConstraintDependentSite> Diagnostics)
    {
        public static Dependents Empty { get; } = new(
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, new List<VarianceConstraintDependentSite>());

        /// <summary>Keeps only construction/Activator facts (drops value-semantics buckets) for 'new()'-only constraints.</summary>
        public Dependents ConstructionOnly() => new(
            0,
            New,
            0,
            0,
            0,
            Activator,
            0,
            0,
            0,
            0,
            0,
            Diagnostics.Where(d => d.Code == VarianceCodes.ResidualNewTSite).ToList());
    }
}
