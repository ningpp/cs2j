using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.StaticAbstractCompanion;

/// <summary>
/// Per-file companion rewrite. It runs on the original syntax tree with the
/// original semantic model (before any other expression-desugar rule), so every
/// symbol lookup is still valid:
/// <list type="bullet">
/// <item>the planned interface declarations drop their static abstract/virtual
/// members and receive the generated companion declarations;</item>
/// <item>member access / calls on a type parameter are redirected to the
/// companion dispatch instance;</item>
/// <item>operator and conversion applications become companion method calls;
/// event add/remove targets become companion instance events.</item>
/// </list>
/// </summary>
public static class StaticAbstractCompanionRewriter
{
    public static StaticAbstractCompanionFileResult Rewrite(
        CompilationUnitSyntax root,
        SemanticModel semanticModel,
        StaticAbstractCompanionPlan plan,
        string filePath)
    {
        if (plan.IsEmpty)
            return new StaticAbstractCompanionFileResult(root.ToFullString(), false, 0);

        var text = root.ToFullString();
        if (!plan.MayContainSites(text))
            return new StaticAbstractCompanionFileResult(text, false, 0);

        var rewriter = new CompanionSyntaxRewriter(semanticModel, plan, filePath);
        var rewritten = (CompilationUnitSyntax)rewriter.Visit(root)!;
        var changed = rewriter.Changed;
        return new StaticAbstractCompanionFileResult(
            changed ? rewritten.ToFullString() : text,
            changed,
            rewriter.CallSitesRewritten);
    }

    internal const string CompanionAnnotationKind = "cs2j.static-abstract-companion";

    private sealed class CompanionSyntaxRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _model;
        private readonly StaticAbstractCompanionPlan _plan;
        private readonly string _filePath;
        private readonly Dictionary<string, StaticAbstractCompanionInterfacePlan> _plansByAnnotation =
            new(StringComparer.Ordinal);

        public CompanionSyntaxRewriter(
            SemanticModel model,
            StaticAbstractCompanionPlan plan,
            string filePath)
        {
            _model = model;
            _plan = plan;
            _filePath = filePath;
            var index = 0;
            foreach (var interfacePlan in plan.ByInterface.Values)
            {
                _plansByAnnotation[index.ToString()] = interfacePlan;
                index++;
            }
        }

        public bool Changed { get; private set; }

        public int CallSitesRewritten { get; private set; }

        // ---- interface declarations ----

        public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            var symbol = _model.GetDeclaredSymbol(node) as INamedTypeSymbol;
            var plan = symbol == null
                ? null
                : _plan.ByInterface.GetValueOrDefault(symbol.OriginalDefinition);

            if (plan == null)
                return InsertCompanions(
                    (InterfaceDeclarationSyntax)base.VisitInterfaceDeclaration(node)!);

            var kept = new List<MemberDeclarationSyntax>(node.Members.Count);
            foreach (var member in node.Members)
            {
                var memberSymbol = _model.GetDeclaredSymbol(member);
                if (memberSymbol != null
                    && _plan.MemberDefinitions.Contains(memberSymbol.OriginalDefinition))
                {
                    Changed = true;
                    continue;
                }

                kept.Add((MemberDeclarationSyntax)Visit(member)!);
            }

            var visited = node.WithMembers(SyntaxFactory.List(kept));
            visited = visited.WithMembers(InsertCompanions(visited.Members));
            if (SamePath(plan.DeclaringFilePath, _filePath))
            {
                visited = visited.WithAdditionalAnnotations(
                    new SyntaxAnnotation(CompanionAnnotationKind, FindAnnotationIndex(plan)));
                Changed = true;
            }

            return visited;
        }

        private string FindAnnotationIndex(StaticAbstractCompanionInterfacePlan plan)
            => _plansByAnnotation.First(pair => ReferenceEquals(pair.Value, plan)).Key;

        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
            => InsertCompanions((ClassDeclarationSyntax)base.VisitClassDeclaration(node)!);

        public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
            => InsertCompanions((StructDeclarationSyntax)base.VisitStructDeclaration(node)!);

        public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
            => InsertCompanions((RecordDeclarationSyntax)base.VisitRecordDeclaration(node)!);

        public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
        {
            var visited = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;
            return visited.WithMembers(InsertCompanions(visited.Members));
        }

        public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            var visited = (NamespaceDeclarationSyntax)base.VisitNamespaceDeclaration(node)!;
            return visited.WithMembers(InsertCompanions(visited.Members));
        }

        public override SyntaxNode? VisitFileScopedNamespaceDeclaration(
            FileScopedNamespaceDeclarationSyntax node)
        {
            var visited = (FileScopedNamespaceDeclarationSyntax)base
                .VisitFileScopedNamespaceDeclaration(node)!;
            return visited.WithMembers(InsertCompanions(visited.Members));
        }

        private T InsertCompanions<T>(T node)
            where T : TypeDeclarationSyntax
            => (T)node.WithMembers(InsertCompanions(node.Members));

        private SyntaxList<MemberDeclarationSyntax> InsertCompanions(
            SyntaxList<MemberDeclarationSyntax> members)
        {
            var result = new List<MemberDeclarationSyntax>(members.Count);
            foreach (var member in members)
            {
                result.Add(member);
                var annotation = member.GetAnnotations(CompanionAnnotationKind).FirstOrDefault();
                if (annotation?.Data == null
                    || !_plansByAnnotation.TryGetValue(annotation.Data, out var plan))
                {
                    continue;
                }

                foreach (var generated in plan.GeneratedMembers)
                {
                    result.Add(generated
                        .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)
                        .WithAdditionalAnnotations(Microsoft.CodeAnalysis.Formatting.Formatter.Annotation));
                }
            }

            return SyntaxFactory.List(result);
        }

        // ---- call sites ----

        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var symbol = _model.GetSymbolInfo(node).Symbol
                ?? _model.GetSymbolInfo(node.Name).Symbol;

            if (symbol != null
                && _plan.MemberDefinitions.Contains(symbol.OriginalDefinition)
                && _model.GetSymbolInfo(node.Expression).Symbol is ITypeParameterSymbol typeParameter)
            {
                var interfaceDefinition = symbol.ContainingType?.OriginalDefinition;
                if (interfaceDefinition != null
                    && _plan.ByInterface.TryGetValue(interfaceDefinition, out var plan)
                    && TryBuildDispatchInstance(plan, typeParameter, out var dispatch))
                {
                    CallSitesRewritten++;
                    Changed = true;
                    return SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            dispatch,
                            node.Name.WithoutTrivia())
                        .WithTriviaFrom(node);
                }
            }

            return base.VisitMemberAccessExpression(node);
        }

        public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            var symbol = _model.GetSymbolInfo(node).Symbol as IMethodSymbol;
            var left = (ExpressionSyntax)Visit(node.Left)!;
            var right = (ExpressionSyntax)Visit(node.Right)!;

            if (symbol is { MethodKind: MethodKind.UserDefinedOperator }
                && TryRewriteOperator(node, symbol, new[] { left, right }) is { } rewritten)
            {
                return rewritten;
            }

            return node.WithLeft(left).WithRight(right);
        }

        public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
        {
            var symbol = _model.GetSymbolInfo(node).Symbol as IMethodSymbol;
            var operand = (ExpressionSyntax)Visit(node.Operand)!;

            if (node.Kind() is not (SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression)
                && symbol is { MethodKind: MethodKind.UserDefinedOperator }
                && TryRewriteUnaryOperator(node, symbol, operand) is { } rewritten)
            {
                return rewritten;
            }

            return node.WithOperand(operand);
        }

        public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
        {
            var symbol = _model.GetSymbolInfo(node).Symbol as IMethodSymbol;
            var operand = (ExpressionSyntax)Visit(node.Expression)!;

            if (symbol is { MethodKind: MethodKind.Conversion }
                && _plan.MemberDefinitions.Contains(symbol.OriginalDefinition)
                && symbol.ContainingType?.OriginalDefinition is { } interfaceDefinition
                && _plan.ByInterface.TryGetValue(interfaceDefinition, out var plan))
            {
                var typeParameter = InferTypeParameter(operand)
                    ?? _model.GetTypeInfo(node.Type).Type as ITypeParameterSymbol;
                if (typeParameter != null
                    && TryBuildDispatchInstance(plan, typeParameter, out var dispatch))
                {
                    CallSitesRewritten++;
                    Changed = true;
                    var invocation = Invocation(
                        dispatch,
                        symbol.Name,
                        new[] { operand.WithoutTrivia() });
                    return SyntaxFactory.CastExpression(
                            node.Type.WithoutTrivia(),
                            SyntaxFactory.ParenthesizedExpression(invocation))
                        .WithTriviaFrom(node);
                }
            }

            return node.WithExpression(operand);
        }

        private SyntaxNode? TryRewriteOperator(
            ExpressionSyntax node,
            IMethodSymbol symbol,
            IReadOnlyList<ExpressionSyntax> operands)
        {
            if (!_plan.MemberDefinitions.Contains(symbol.OriginalDefinition)
                || symbol.ContainingType?.OriginalDefinition is not { } interfaceDefinition
                || !_plan.ByInterface.TryGetValue(interfaceDefinition, out var plan))
            {
                return null;
            }

            var typeParameter = operands
                .Select(InferTypeParameter)
                .FirstOrDefault(static t => t != null);
            if (typeParameter == null
                || !TryBuildDispatchInstance(plan, typeParameter, out var dispatch))
            {
                return null;
            }

            CallSitesRewritten++;
            Changed = true;
            return Invocation(
                    dispatch,
                    symbol.Name,
                    operands.Select(static o => o.WithoutTrivia()))
                .WithTriviaFrom(node);
        }

        private SyntaxNode? TryRewriteUnaryOperator(
            ExpressionSyntax node,
            IMethodSymbol symbol,
            ExpressionSyntax operand)
        {
            if (!_plan.MemberDefinitions.Contains(symbol.OriginalDefinition)
                || symbol.ContainingType?.OriginalDefinition is not { } interfaceDefinition
                || !_plan.ByInterface.TryGetValue(interfaceDefinition, out var plan)
                || InferTypeParameter(operand) is not { } typeParameter
                || !TryBuildDispatchInstance(plan, typeParameter, out var dispatch))
            {
                return null;
            }

            CallSitesRewritten++;
            Changed = true;
            return Invocation(dispatch, symbol.Name, new[] { operand.WithoutTrivia() })
                .WithTriviaFrom(node);
        }

        private ITypeParameterSymbol? InferTypeParameter(ExpressionSyntax expression)
        {
            if (_model.GetSymbolInfo(expression).Symbol is ITypeParameterSymbol direct)
                return direct;

            return _model.GetTypeInfo(expression).Type as ITypeParameterSymbol;
        }

        private bool TryBuildDispatchInstance(
            StaticAbstractCompanionInterfacePlan plan,
            ITypeParameterSymbol typeParameter,
            out ExpressionSyntax dispatch)
        {
            dispatch = null!;
            var constructed = FindConstraintInterface(typeParameter, plan.Interface);
            if (constructed == null)
                return false;

            var arguments = constructed.TypeArguments
                .Select(static a => a.ToDisplayString(DisplayFormat))
                .ToList();
            arguments.Add(typeParameter.Name);

            var text = $"{plan.DispatchReference}<{string.Join(", ", arguments)}>.Instance";
            dispatch = SyntaxFactory.ParseExpression(text);
            return dispatch != null && !dispatch.ContainsDiagnostics && !dispatch.IsMissing;
        }

        private static INamedTypeSymbol? FindConstraintInterface(
            ITypeParameterSymbol typeParameter,
            INamedTypeSymbol interfaceDefinition)
        {
            foreach (var constraint in typeParameter.ConstraintTypes)
            {
                if (SymbolEqualityComparer.Default.Equals(
                        constraint.OriginalDefinition, interfaceDefinition))
                {
                    return constraint as INamedTypeSymbol;
                }

                foreach (var inherited in constraint.AllInterfaces)
                {
                    if (SymbolEqualityComparer.Default.Equals(
                            inherited.OriginalDefinition, interfaceDefinition))
                    {
                        return inherited;
                    }
                }
            }

            return null;
        }

        private static InvocationExpressionSyntax Invocation(
            ExpressionSyntax dispatch,
            string memberName,
            IEnumerable<ExpressionSyntax> arguments)
        {
            var access = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                dispatch,
                SyntaxFactory.IdentifierName(memberName));
            return SyntaxFactory.InvocationExpression(
                access,
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                    arguments.Select(static a => SyntaxFactory.Argument(a)))));
        }

        private static bool SamePath(string left, string right)
            => string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
    }

    private static readonly SymbolDisplayFormat DisplayFormat =
        SymbolDisplayFormat.FullyQualifiedFormat
            .WithMiscellaneousOptions(
                SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
                | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);
}
