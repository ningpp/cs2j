using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.IteratorLowering;

/// <summary>
/// Rewrites iterator method bodies and synthesizes nested state machine classes
/// based on the <see cref="AnalyzerResult"/> produced by <see cref="IteratorLoweringAnalyzer"/>.
/// </summary>
internal sealed class IteratorStateMachineRewriter
{
    private static readonly SyntaxAnnotation Annotation = new("Cs2jIteratorLowering");

    private readonly SemanticModel _semanticModel;
    private readonly AnalyzerResult _analyzerResult;
    private readonly Dictionary<MethodDeclarationSyntax, StateMachinePlan> _planByMethod;

    public IteratorStateMachineRewriter(SemanticModel semanticModel, AnalyzerResult analyzerResult)
    {
        _semanticModel = semanticModel;
        _analyzerResult = analyzerResult;
        _planByMethod = new();

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>();
        var planIndex = 0;
        foreach (var method in methods)
        {
            if (method.Body == null) continue;
            if (!method.Body.DescendantNodes().Any(n => n.IsKind(SyntaxKind.YieldReturnStatement)
                || n.IsKind(SyntaxKind.YieldBreakStatement)))
                continue;

            if (planIndex < analyzerResult.Plans.Count
                && analyzerResult.Plans[planIndex].MemberName == method.Identifier.ValueText)
            {
                _planByMethod[method] = analyzerResult.Plans[planIndex];
                planIndex++;
            }
        }
    }

    public CompilationUnitSyntax Rewrite(CompilationUnitSyntax root)
    {
        if (_planByMethod.Count == 0)
            return root;

        var newRoot = root;
        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList())
        {
            var plansForType = _planByMethod
                .Where(kv => kv.Key.Parent == typeDecl)
                .ToList();
            if (plansForType.Count == 0) continue;

            var newMembers = new List<MemberDeclarationSyntax>();
            var nestedClasses = new List<ClassDeclarationSyntax>();

            foreach (var member in typeDecl.Members)
            {
                if (member is MethodDeclarationSyntax method && _planByMethod.TryGetValue(method, out var plan))
                {
                    var newMethod = RewriteMethodBody(method, plan);
                    newMembers.Add(newMethod);
                    nestedClasses.Add(BuildStateMachineClass(method, plan));
                }
                else
                {
                    newMembers.Add(member);
                }
            }

            newMembers.AddRange(nestedClasses);

            var newTypeDecl = typeDecl.WithMembers(List(newMembers));
            newRoot = newRoot.ReplaceNode(typeDecl, newTypeDecl);
        }

        newRoot = EnsureUsings(newRoot);
        return newRoot;
    }

    private MethodDeclarationSyntax RewriteMethodBody(
        MethodDeclarationSyntax method, StateMachinePlan plan)
    {
        var stateValue = plan.IsIEnumerableReturn ? -2 : 0;

        var args = new List<ArgumentSyntax>();

        if (!plan.IsStatic)
            args.Add(Argument(ThisExpression()));

        foreach (var param in plan.Parameters)
            args.Add(Argument(IdentifierName(param.OriginalName)));

        args.Add(Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression,
            Literal(stateValue))));

        var typeName = plan.StateMachineClassName
            + (plan.IsGenericMethod ? GetGenericTypeArgs(method) : "");

        var objectCreation = ObjectCreationExpression(ParseTypeName(typeName))
            .WithArgumentList(ArgumentList(SeparatedList(args)));

        var returnStmt = ReturnStatement(objectCreation);
        var newBody = Block(returnStmt);

        return method
            .WithBody(newBody)
            .WithExpressionBody(null)
            .WithSemicolonToken(Token(SyntaxKind.None))
            .WithAdditionalAnnotations(Annotation);
    }

    private ClassDeclarationSyntax BuildStateMachineClass(
        MethodDeclarationSyntax method, StateMachinePlan plan)
    {
        var fields = new List<MemberDeclarationSyntax>();

        fields.Add(FieldDeclaration(VariableDeclaration(PredefinedType(Token(SyntaxKind.IntKeyword)))
            .WithVariables(SingletonSeparatedList(VariableDeclarator(IteratorLoweringNaming.StateField))))
            .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword))));

        fields.Add(FieldDeclaration(VariableDeclaration(ParseTypeName(plan.ElementType))
            .WithVariables(SingletonSeparatedList(VariableDeclarator(IteratorLoweringNaming.CurrentField))))
            .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword))));

        if (plan.IsIEnumerableReturn)
        {
            fields.Add(FieldDeclaration(VariableDeclaration(PredefinedType(Token(SyntaxKind.IntKeyword)))
                .WithVariables(SingletonSeparatedList(VariableDeclarator(IteratorLoweringNaming.InitialThreadIdField))))
                .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword))));
        }

        if (!plan.IsStatic)
        {
            fields.Add(FieldDeclaration(VariableDeclaration(ParseTypeName(plan.DeclaringTypeName))
                .WithVariables(SingletonSeparatedList(VariableDeclarator(IteratorLoweringNaming.ThisField))))
                .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword))));
        }

        foreach (var param in plan.Parameters)
        {
            fields.Add(FieldDeclaration(VariableDeclaration(ParseTypeName(param.Type))
                .WithVariables(SingletonSeparatedList(VariableDeclarator(param.FieldName))))
                .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword))));
        }

        foreach (var local in plan.HoistedLocals)
        {
            fields.Add(FieldDeclaration(VariableDeclaration(ParseTypeName(local.Type))
                .WithVariables(SingletonSeparatedList(VariableDeclarator(local.FieldName))))
                .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword))));
        }

        // Constructor
        var ctorParams = new List<ParameterSyntax>();
        if (!plan.IsStatic)
            ctorParams.Add(Parameter(Identifier("@this")).WithType(ParseTypeName(plan.DeclaringTypeName)));
        foreach (var param in plan.Parameters)
            ctorParams.Add(Parameter(Identifier(param.OriginalName)).WithType(ParseTypeName(param.Type)));
        ctorParams.Add(Parameter(Identifier("state")).WithType(PredefinedType(Token(SyntaxKind.IntKeyword))));

        var ctorStatements = new List<StatementSyntax>();
        if (!plan.IsStatic)
            ctorStatements.Add(ExpressionStatement(AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                IdentifierName(IteratorLoweringNaming.ThisField),
                IdentifierName("@this"))));
        foreach (var param in plan.Parameters)
        {
            ctorStatements.Add(ExpressionStatement(AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                IdentifierName(param.FieldName),
                IdentifierName(param.OriginalName))));
        }
        ctorStatements.Add(ExpressionStatement(AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            IdentifierName(IteratorLoweringNaming.StateField),
            IdentifierName("state"))));
        if (plan.IsIEnumerableReturn)
        {
            ctorStatements.Add(ExpressionStatement(AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                IdentifierName(IteratorLoweringNaming.InitialThreadIdField),
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName("Environment"),
                    IdentifierName("CurrentManagedThreadId")))));
        }

        var ctor = ConstructorDeclaration(Identifier(plan.StateMachineClassName))
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithParameterList(ParameterList(SeparatedList(ctorParams)))
            .WithBody(Block(ctorStatements));

        // Current properties
        var currentProp = PropertyDeclaration(ParseTypeName(plan.ElementType), Identifier("Current"))
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithExpressionBody(ArrowExpressionClause(
                IdentifierName(IteratorLoweringNaming.CurrentField)))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        var explicitCurrentProp = PropertyDeclaration(
            PredefinedType(Token(SyntaxKind.ObjectKeyword)), Identifier("Current"))
            .WithExplicitInterfaceSpecifier(ExplicitInterfaceSpecifier(
                ParseName("System.Collections.IEnumerator")))
            .WithExpressionBody(ArrowExpressionClause(
                IdentifierName(IteratorLoweringNaming.CurrentField)))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        var moveNext = BuildMoveNextMethod(method, plan);

        var dispose = MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), Identifier("Dispose"))
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithBody(Block(
                ExpressionStatement(AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    IdentifierName(IteratorLoweringNaming.StateField),
                    PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression,
                        LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(1)))))));

        var reset = MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), Identifier("Reset"))
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithBody(Block(
                ThrowStatement(ObjectCreationExpression(ParseTypeName("System.NotSupportedException"))
                    .WithArgumentList(ArgumentList()))));

        var members = new List<MemberDeclarationSyntax>();
        members.AddRange(fields);
        members.Add(ctor);
        members.Add(currentProp);
        members.Add(explicitCurrentProp);
        members.Add(moveNext);
        members.Add(dispose);
        members.Add(reset);

        if (plan.IsIEnumerableReturn)
        {
            members.Add(BuildGetEnumeratorMethod(plan));
            members.Add(BuildExplicitGetEnumeratorMethod(plan));
        }

        var baseTypes = new List<BaseTypeSyntax>();
        if (plan.IsIEnumerableReturn)
        {
            baseTypes.Add(SimpleBaseType(ParseTypeName(
                $"System.Collections.Generic.IEnumerable<{plan.ElementType}>")));
            baseTypes.Add(SimpleBaseType(ParseTypeName(
                $"System.Collections.Generic.IEnumerator<{plan.ElementType}>")));
        }
        else
        {
            baseTypes.Add(SimpleBaseType(ParseTypeName(
                $"System.Collections.Generic.IEnumerator<{plan.ElementType}>")));
        }
        baseTypes.Add(SimpleBaseType(ParseTypeName("System.IDisposable")));

        // Add type parameters if generic method
        var classDecl = ClassDeclaration(Identifier(plan.StateMachineClassName))
            .WithModifiers(TokenList(
                Token(SyntaxKind.PrivateKeyword),
                Token(SyntaxKind.SealedKeyword)));

        if (plan.IsGenericMethod && method.TypeParameterList != null)
        {
            classDecl = classDecl.WithTypeParameterList(method.TypeParameterList);
        }

        return classDecl
            .WithAttributeLists(SingletonList(AttributeList(SingletonSeparatedList(
                Attribute(ParseName("System.Runtime.CompilerServices.CompilerGeneratedAttribute"))))))
            .WithBaseList(BaseList(SeparatedList(baseTypes)))
            .WithMembers(List(members))
            .WithAdditionalAnnotations(Annotation);
    }

    private MethodDeclarationSyntax BuildMoveNextMethod(
        MethodDeclarationSyntax method, StateMachinePlan plan)
    {
        var switchCases = new List<SwitchSectionSyntax>();
        for (int i = 0; i < plan.YieldReturnCount; i++)
        {
            var state = i + 1;
            var section = SwitchSection(
                default(SyntaxList<SwitchLabelSyntax>),
                SingletonList<StatementSyntax>(
                    GotoStatement(
                        SyntaxKind.GotoStatement,
                        IdentifierName(IteratorLoweringNaming.GetResumeLabel(i)))
                        .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))))
                .WithLabels(SingletonList<SwitchLabelSyntax>(
                    CaseSwitchLabel(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(state)))));
            switchCases.Add(section);
        }

        var switchStmt = SwitchStatement(
            IdentifierName(IteratorLoweringNaming.StateField))
            .WithSections(List(switchCases));

        var bodyStatements = new List<StatementSyntax>();

        // Label for resume point 0
        bodyStatements.Add(LabeledStatement(
            Identifier(IteratorLoweringNaming.GetResumeLabel(0)),
            Token(SyntaxKind.ColonToken),
            EmptyStatement()));

        // Switch for dispatching to resume points
        bodyStatements.Add(switchStmt);

        // Transform the original body
        var transformedBody = TransformBody(method.Body!, plan);
        bodyStatements.AddRange(transformedBody);

        // End: state = -1; return false;
        bodyStatements.Add(ExpressionStatement(AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            IdentifierName(IteratorLoweringNaming.StateField),
            PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression,
                LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(1))))));
        bodyStatements.Add(ReturnStatement(LiteralExpression(SyntaxKind.FalseLiteralExpression)));

        return MethodDeclaration(PredefinedType(Token(SyntaxKind.BoolKeyword)), Identifier("MoveNext"))
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithBody(Block(bodyStatements));
    }

    private List<StatementSyntax> TransformBody(BlockSyntax originalBody, StateMachinePlan plan)
    {
        var rewriter = new BodyTransformRewriter(plan);
        var transformed = (BlockSyntax)rewriter.Visit(originalBody)!;
        return transformed.Statements.ToList();
    }

    private MethodDeclarationSyntax BuildGetEnumeratorMethod(StateMachinePlan plan)
    {
        var cloneArgs = new List<ArgumentSyntax>();
        if (!plan.IsStatic)
            cloneArgs.Add(Argument(IdentifierName(IteratorLoweringNaming.ThisField)));
        foreach (var param in plan.Parameters)
            cloneArgs.Add(Argument(IdentifierName(param.FieldName)));
        cloneArgs.Add(Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(-2))));

        var cloneCreation = ObjectCreationExpression(ParseTypeName(plan.StateMachineClassName))
            .WithArgumentList(ArgumentList(SeparatedList(cloneArgs)));

        var cloneAssign = ExpressionStatement(AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                IdentifierName("e"), IdentifierName(IteratorLoweringNaming.StateField)),
            LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0))));

        var condition = BinaryExpression(SyntaxKind.LogicalAndExpression,
            BinaryExpression(SyntaxKind.EqualsExpression,
                IdentifierName(IteratorLoweringNaming.StateField),
                PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression,
                    LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(2)))),
            BinaryExpression(SyntaxKind.EqualsExpression,
                IdentifierName(IteratorLoweringNaming.InitialThreadIdField),
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName("Environment"),
                    IdentifierName("CurrentManagedThreadId"))));

        var ifStmt = IfStatement(condition,
            Block(
                ExpressionStatement(AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    IdentifierName(IteratorLoweringNaming.StateField),
                    LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0)))),
                ReturnStatement(ThisExpression())));
        var elseStmt = ElseClause(Block(
            LocalDeclarationStatement(VariableDeclaration(
                IdentifierName("var")).WithVariables(SingletonSeparatedList(
                    VariableDeclarator(Identifier("e"))
                        .WithInitializer(EqualsValueClause(cloneCreation))))),
            cloneAssign,
            ReturnStatement(IdentifierName("e"))));

        ifStmt = ifStmt.WithElse(elseStmt);

        return MethodDeclaration(
            GenericName(Identifier("IEnumerator"))
                .WithTypeArgumentList(TypeArgumentList(SingletonSeparatedList(
                    ParseTypeName(plan.ElementType)))),
            Identifier("GetEnumerator"))
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithBody(Block(ifStmt));
    }

    private MethodDeclarationSyntax BuildExplicitGetEnumeratorMethod(StateMachinePlan plan)
    {
        var inner = InvocationExpression(IdentifierName("GetEnumerator"));
        return MethodDeclaration(
            ParseTypeName("System.Collections.IEnumerator"),
            Identifier("GetEnumerator"))
            .WithExplicitInterfaceSpecifier(ExplicitInterfaceSpecifier(
                ParseName("System.Collections.IEnumerable")))
            .WithExpressionBody(ArrowExpressionClause(inner))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
    }

    private static string GetGenericTypeArgs(MethodDeclarationSyntax method)
    {
        if (method.TypeParameterList == null) return "";
        var args = string.Join(", ", method.TypeParameterList.Parameters.Select(p => p.Identifier.ValueText));
        return $"<{args}>";
    }

    private static CompilationUnitSyntax EnsureUsings(CompilationUnitSyntax root)
    {
        var usings = root.Usings.Select(u => u.Name?.ToString() ?? "").ToHashSet();
        var needed = new[] { "System", "System.Collections", "System.Collections.Generic" };
        var newUsings = root.Usings.ToList();
        foreach (var ns in needed)
        {
            if (!usings.Contains(ns))
            {
                newUsings.Add(UsingDirective(ParseName(ns)));
            }
        }
        return root.WithUsings(List(newUsings));
    }

    private sealed class BodyTransformRewriter : CSharpSyntaxRewriter
    {
        private readonly StateMachinePlan _plan;
        private int _yieldIndex;

        public BodyTransformRewriter(StateMachinePlan plan) => _plan = plan;

        public override SyntaxNode? VisitYieldStatement(YieldStatementSyntax node)
        {
            if (node.IsKind(SyntaxKind.YieldReturnStatement) && node.Expression != null)
            {
                var state = ++_yieldIndex;
                var statements = new List<StatementSyntax>
                {
                    ExpressionStatement(AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        IdentifierName(IteratorLoweringNaming.CurrentField),
                        node.Expression)),
                    ExpressionStatement(AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        IdentifierName(IteratorLoweringNaming.StateField),
                        LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(state)))),
                    ReturnStatement(LiteralExpression(SyntaxKind.TrueLiteralExpression)),
                    LabeledStatement(
                        Identifier(IteratorLoweringNaming.GetResumeLabel(state - 1)),
                        Token(SyntaxKind.ColonToken),
                        EmptyStatement()),
                };
                return Block(statements);
            }
            else if (node.IsKind(SyntaxKind.YieldBreakStatement))
            {
                return Block(
                    ExpressionStatement(AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        IdentifierName(IteratorLoweringNaming.StateField),
                        PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression,
                            LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(1))))),
                    ReturnStatement(LiteralExpression(SyntaxKind.FalseLiteralExpression)));
            }
            return base.VisitYieldStatement(node);
        }
    }
}
