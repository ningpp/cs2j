using CSharpToJava.Core.IteratorLowering.ControlFlow;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.IteratorLowering;

/// <summary>
/// Rewrites iterator method bodies and synthesizes nested state machine classes
/// based on the <see cref="MethodLoweringInfo"/> produced by
/// <see cref="IteratorLoweringAnalyzer"/>.
/// </summary>
internal sealed class IteratorStateMachineRewriter
{
    private static readonly SyntaxAnnotation Annotation = new("Cs2jIteratorLowering");
    private const string RunningStateResetLocal = "_cs2jTmpDisposeState";

    private readonly SemanticModel _semanticModel;
    private readonly Dictionary<MethodDeclarationSyntax, MethodLoweringInfo> _infoByMethod;

    public IteratorStateMachineRewriter(SemanticModel semanticModel, AnalyzerResult analyzerResult)
    {
        _semanticModel = semanticModel;
        _infoByMethod = new();

        foreach (var info in analyzerResult.Infos)
        {
            _infoByMethod[info.Method] = info;
        }
    }

    public CompilationUnitSyntax Rewrite(CompilationUnitSyntax root)
    {
        if (_infoByMethod.Count == 0)
            return root;

        var newRoot = root;
        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList())
        {
            var plansForType = _infoByMethod
                .Where(kv => kv.Key.Parent == typeDecl)
                .ToList();
            if (plansForType.Count == 0) continue;

            var newMembers = new List<MemberDeclarationSyntax>();
            var nestedClasses = new List<MemberDeclarationSyntax>();

            foreach (var member in typeDecl.Members)
            {
                if (member is MethodDeclarationSyntax method
                    && _infoByMethod.TryGetValue(method, out var info))
                {
                    newMembers.Add(RewriteMethodBody(method, info.Plan));
                    nestedClasses.Add(BuildStateMachineClass(method, info));
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

    private static MethodDeclarationSyntax RewriteMethodBody(
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

        return method
            .WithBody(Block(ReturnStatement(objectCreation)))
            .WithExpressionBody(null)
            .WithSemicolonToken(Token(SyntaxKind.None))
            .WithAdditionalAnnotations(Annotation);
    }

    private ClassDeclarationSyntax BuildStateMachineClass(
        MethodDeclarationSyntax method, MethodLoweringInfo info)
    {
        var plan = info.Plan;
        var fields = new List<MemberDeclarationSyntax>();

        fields.Add(Field(PredefinedType(Token(SyntaxKind.IntKeyword)), IteratorLoweringNaming.StateField));
        fields.Add(Field(ParseTypeName(plan.ElementType), IteratorLoweringNaming.CurrentField));
        if (plan.Regions.Count > 0)
        {
            fields.Add(Field(PredefinedType(Token(SyntaxKind.IntKeyword)),
                IteratorLoweringNaming.ActiveRegionField));
        }
        if (plan.IsIEnumerableReturn)
        {
            fields.Add(Field(PredefinedType(Token(SyntaxKind.IntKeyword)),
                IteratorLoweringNaming.InitialThreadIdField));
        }
        if (!plan.IsStatic)
        {
            fields.Add(Field(ParseTypeName(plan.ThisCaptureType), IteratorLoweringNaming.ThisField,
                Token(SyntaxKind.ReadOnlyKeyword)));
        }
        foreach (var f in plan.Parameters)
            fields.Add(Field(ParseTypeName(f.Type), f.FieldName));
        foreach (var f in plan.HoistedLocals)
            fields.Add(Field(ParseTypeName(f.Type), f.FieldName));
        foreach (var f in plan.HoistedEnumerators)
            fields.Add(Field(ParseTypeName(f.Type), f.FieldName));
        // foreach 数组形态的下标临时量
        foreach (var fe in info.Foreaches.Values.Where(v => v.IsArray))
        {
            var idxField = GetArrayIndexFieldName(fe);
            fields.Add(Field(PredefinedType(Token(SyntaxKind.IntKeyword)), idxField));
        }

        var ctor = BuildConstructor(plan);
        var moveNext = BuildMoveNextMethod(method, info);
        var dispose = BuildDisposeMethod(info);
        var reset = BuildResetMethod();

        var members = new List<MemberDeclarationSyntax>();
        members.AddRange(fields);
        members.Add(ctor);
        members.AddRange(BuildCurrentProperties(plan));
        members.Add(moveNext);
        members.Add(dispose);
        members.Add(reset);

        if (plan.IsIEnumerableReturn)
        {
            members.Add(BuildGetEnumeratorMethod(plan, method));
            members.Add(BuildExplicitGetEnumeratorMethod(plan));
        }

        var baseTypes = new List<BaseTypeSyntax>();
        if (plan.IsNonGenericReturn)
        {
            baseTypes.Add(SimpleBaseType(ParseTypeName("System.Collections.IEnumerable")));
            baseTypes.Add(SimpleBaseType(ParseTypeName("System.Collections.IEnumerator")));
        }
        else if (plan.IsIEnumerableReturn)
        {
            baseTypes.Add(SimpleBaseType(ParseTypeName(
                $"System.Collections.Generic.IEnumerable<{plan.ElementType}>")));
            baseTypes.Add(SimpleBaseType(ParseTypeName(
                $"System.Collections.Generic.IEnumerator<{plan.ElementType}>")));
            baseTypes.Add(SimpleBaseType(ParseTypeName("System.Collections.IEnumerator")));
        }
        else
        {
            baseTypes.Add(SimpleBaseType(ParseTypeName(
                $"System.Collections.Generic.IEnumerator<{plan.ElementType}>")));
            baseTypes.Add(SimpleBaseType(ParseTypeName("System.Collections.IEnumerator")));
        }
        baseTypes.Add(SimpleBaseType(ParseTypeName("System.IDisposable")));

        var classDecl = ClassDeclaration(Identifier(plan.StateMachineClassName))
            .WithModifiers(TokenList(
                Token(SyntaxKind.PrivateKeyword),
                Token(SyntaxKind.SealedKeyword)));

        if (plan.IsGenericMethod)
        {
            classDecl = classDecl
                .WithTypeParameterList(method.TypeParameterList)
                .WithConstraintClauses(method.ConstraintClauses);
        }

        return classDecl
            .WithAttributeLists(SingletonList(AttributeList(SingletonSeparatedList(
                Attribute(ParseName("System.Runtime.CompilerServices.CompilerGeneratedAttribute"))))))
            .WithBaseList(BaseList(SeparatedList(baseTypes)))
            .WithMembers(List(members))
            .WithAdditionalAnnotations(Annotation);
    }

    private static FieldDeclarationSyntax Field(
        TypeSyntax type, string name, params SyntaxToken[] additionalModifiers)
    {
        var modifiers = new List<SyntaxToken> { Token(SyntaxKind.PrivateKeyword) };
        modifiers.AddRange(additionalModifiers);
        return FieldDeclaration(VariableDeclaration(type)
                .WithVariables(SingletonSeparatedList(VariableDeclarator(name))))
            .WithModifiers(TokenList(modifiers));
    }

    private static ConstructorDeclarationSyntax BuildConstructor(StateMachinePlan plan)
    {
        var ctorParams = new List<ParameterSyntax>();
        if (!plan.IsStatic)
            ctorParams.Add(Parameter(Identifier("@this")).WithType(ParseTypeName(plan.ThisCaptureType)));
        foreach (var param in plan.Parameters)
            ctorParams.Add(Parameter(Identifier(param.OriginalName)).WithType(ParseTypeName(param.Type)));
        ctorParams.Add(Parameter(Identifier("state")).WithType(PredefinedType(Token(SyntaxKind.IntKeyword))));

        var ctorStatements = new List<StatementSyntax>();
        if (!plan.IsStatic)
            ctorStatements.Add(Assign(IteratorLoweringNaming.ThisField, IdentifierName("@this")));
        foreach (var param in plan.Parameters)
            ctorStatements.Add(Assign(param.FieldName, IdentifierName(param.OriginalName)));
        ctorStatements.Add(Assign(IteratorLoweringNaming.StateField, IdentifierName("state")));
        if (plan.IsIEnumerableReturn)
        {
            ctorStatements.Add(Assign(
                IteratorLoweringNaming.InitialThreadIdField,
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    ParseName("System.Environment"),
                    IdentifierName("CurrentManagedThreadId"))));
        }

        return ConstructorDeclaration(Identifier(plan.StateMachineClassName))
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithParameterList(ParameterList(SeparatedList(ctorParams)))
            .WithBody(Block(ctorStatements));
    }

    private static IEnumerable<MemberDeclarationSyntax> BuildCurrentProperties(StateMachinePlan plan)
    {
        // 块体 get 访问器（而非表达式体）：与 Roslyn 自身状态机 lowering 的发射形态
        // 一致，也避开下游 member-desugar 对“显式接口实现 + 表达式体属性”的错误改写
        // （会退化成 get_Current 方法触发 CS0683）。
        if (plan.IsNonGenericReturn)
        {
            yield return CurrentProperty(
                PredefinedType(Token(SyntaxKind.ObjectKeyword)),
                explicitInterface: "System.Collections.IEnumerator");
            yield break;
        }

        yield return CurrentProperty(ParseTypeName(plan.ElementType), isPublic: true);

        yield return CurrentProperty(
            PredefinedType(Token(SyntaxKind.ObjectKeyword)),
            explicitInterface: "System.Collections.IEnumerator");
    }

    private static PropertyDeclarationSyntax CurrentProperty(
        TypeSyntax type,
        bool isPublic = false,
        string? explicitInterface = null)
    {
        var property = PropertyDeclaration(type, Identifier("Current"))
            .AddAccessorListAccessors(
                AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithBody(Block(ReturnStatement(
                        IdentifierName(IteratorLoweringNaming.CurrentField)))));

        if (isPublic)
        {
            property = property.WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)));
        }

        if (explicitInterface != null)
        {
            property = property.WithExplicitInterfaceSpecifier(
                ExplicitInterfaceSpecifier(ParseName(explicitInterface)));
        }

        return property;
    }

    private MethodDeclarationSyntax BuildMoveNextMethod(
        MethodDeclarationSyntax method, MethodLoweringInfo info)
    {
        var plan = info.Plan;
        var tokenRewriter = new TokenRewriter(_semanticModel, info);
        var flattener = new BodyFlattener(_semanticModel, info, tokenRewriter);
        var flattened = flattener.FlattenBody(method.Body!);

        // MoveNext 体是单层 goto 控制流：所有 resume 标签与 goto 同处一个块作用域，
        // C# 不允许 goto 跳入嵌套块，因此控制构造（if/loop/try/foreach）全部展平。
        var inner = new List<StatementSyntax>();

        // 已结束枚举后再次 MoveNext 必须直接返回 false。
        inner.Add(IfStatement(
            BinaryExpression(SyntaxKind.EqualsExpression,
                IdentifierName(IteratorLoweringNaming.StateField),
                PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression,
                    LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(1)))),
            Block(ReturnStatement(LiteralExpression(SyntaxKind.FalseLiteralExpression)))));

        // 挂起点恢复：有保护区域时先重建「当前活动区域」，再 goto 到同块作用域的 resume 标签。
        var hasRegions = plan.Regions.Count > 0;
        for (var state = 1; state <= plan.YieldReturnCount; state++)
        {
            StatementSyntax dispatch;
            var resume = GotoStatement(SyntaxKind.GotoStatement,
                IdentifierName(IteratorLoweringNaming.GetResumeLabel(state)));
            if (hasRegions)
            {
                var activeRegionId = FindInnermostRegionId(plan.Regions, state) ?? -1;
                dispatch = Block(
                    Assign(IteratorLoweringNaming.ActiveRegionField, IntLiteral(activeRegionId)),
                    resume);
            }
            else
            {
                dispatch = Block(resume);
            }
            inner.Add(IfStatement(
                BinaryExpression(SyntaxKind.EqualsExpression,
                    IdentifierName(IteratorLoweringNaming.StateField),
                    LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(state))),
                Block(dispatch)));
        }

        if (hasRegions)
        {
            inner.Add(Assign(IteratorLoweringNaming.ActiveRegionField, IntLiteral(-1)));
        }
        inner.AddRange(flattened);
        inner.Add(Assign(IteratorLoweringNaming.StateField, IntLiteral(-1)));
        inner.Add(ReturnStatement(LiteralExpression(SyntaxKind.FalseLiteralExpression)));

        // 唯一的 try：异常路径按活动区域链（内层→外层）补跑清理。
        StatementSyntax moveNextStatement;
        if (plan.Regions.Count > 0)
        {
            moveNextStatement = TryStatement()
                .WithBlock(Block(inner))
                .WithCatches(List(new[] { BuildPendingCleanupCatch(info, tokenRewriter) }));
        }
        else
        {
            moveNextStatement = Block(inner);
        }

        var moveNextMethod = MethodDeclaration(PredefinedType(Token(SyntaxKind.BoolKeyword)), Identifier("MoveNext"))
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithBody(Block(moveNextStatement));
        // 生成代码在 yield break 等终止边之后会保留线性清理副本（不可达），抑制 CS0162。
        var warningCode = LiteralExpression(
            SyntaxKind.NumericLiteralExpression, Literal(162));
        var disable = Trivia(PragmaWarningDirectiveTrivia(
            Token(SyntaxKind.DisableKeyword),
            SingletonSeparatedList<ExpressionSyntax>(warningCode),
            isActive: true));
        var restore = Trivia(PragmaWarningDirectiveTrivia(
            Token(SyntaxKind.RestoreKeyword),
            SingletonSeparatedList<ExpressionSyntax>(
                LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(162))),
            isActive: true));
        moveNextMethod = moveNextMethod.WithLeadingTrivia(
            moveNextMethod.GetLeadingTrivia().Add(disable).Add(EndOfLine("\n")));
        var lastToken = moveNextMethod.GetLastToken();
        return moveNextMethod.ReplaceToken(
            lastToken,
            lastToken.WithTrailingTrivia(lastToken.TrailingTrivia
                .Add(EndOfLine("\n")).Add(restore).Add(EndOfLine("\n"))));
    }

    private static int? FindInnermostRegionId(IReadOnlyList<RegionPlan> regions, int state)
    {
        RegionPlan? match = null;
        foreach (var region in regions)
        {
            if (region.ContainsState(state)
                && (match == null || region.FirstState > match.FirstState))
            {
                match = region;
            }
        }
        return match?.Id;
    }

    private CatchClauseSyntax BuildPendingCleanupCatch(
        MethodLoweringInfo info, TokenRewriter tokenRewriter)
    {
        const string activeLocal = "_cs2jCatchActive";
        var regionById = info.Plan.Regions.ToDictionary(r => r.Id);

        var dispatch = new List<StatementSyntax>();
        foreach (var region in info.Plan.Regions)
        {
            // 活动区域固定时，父链在发射期即可静态确定。
            var chain = new List<RegionPlan>();
            var cursor = region;
            while (true)
            {
                chain.Add(cursor);
                if (cursor.ParentId is not int parentId) break;
                cursor = regionById[parentId];
            }
            dispatch.Add(IfStatement(
                BinaryExpression(SyntaxKind.EqualsExpression,
                    IdentifierName(activeLocal),
                    LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(region.Id))),
                Block(BuildNestedCleanupChain(chain, info, tokenRewriter))));
        }

        var catchStatements = new List<StatementSyntax>
        {
            LocalDeclarationStatement(VariableDeclaration(PredefinedType(Token(SyntaxKind.IntKeyword)))
                .WithVariables(SingletonSeparatedList(VariableDeclarator(activeLocal)
                    .WithInitializer(EqualsValueClause(
                        IdentifierName(IteratorLoweringNaming.ActiveRegionField)))))),
        };
        catchStatements.AddRange(dispatch);
        catchStatements.Add(Assign(IteratorLoweringNaming.StateField, IntLiteral(-1)));
        catchStatements.Add(ThrowStatement());
        var catchBody = Block(catchStatements);

        return CatchClause()
            .WithDeclaration(CatchDeclaration(ParseTypeName("System.Exception")))
            .WithBlock(catchBody);
    }

    /// <summary>
    /// 内层清理抛异常时外层 finally 仍须执行：构造嵌套 try/finally 链（内层→外层）。
    /// </summary>
    private static StatementSyntax BuildNestedCleanupChain(
        IReadOnlyList<RegionPlan> innerToOuter,
        MethodLoweringInfo info,
        TokenRewriter tokenRewriter)
    {
        StatementSyntax chain = Block(BuildCleanupStatements(
            innerToOuter[^1], info, tokenRewriter));
        for (var i = innerToOuter.Count - 2; i >= 0; i--)
        {
            var cleanup = BuildCleanupStatements(innerToOuter[i], info, tokenRewriter);
            chain = TryStatement()
                .WithBlock(Block(cleanup))
                .WithFinally(FinallyClause(Block(chain)));
        }
        return chain;
    }

    private static LiteralExpressionSyntax IntLiteral(int value)
        => LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(value));

    private MethodDeclarationSyntax BuildDisposeMethod(MethodLoweringInfo info)
    {
        var plan = info.Plan;
        var statements = new List<StatementSyntax>
        {
            LocalDeclarationStatement(VariableDeclaration(PredefinedType(Token(SyntaxKind.IntKeyword)))
                .WithVariables(SingletonSeparatedList(
                    VariableDeclarator(RunningStateResetLocal)
                        .WithInitializer(EqualsValueClause(
                            IdentifierName(IteratorLoweringNaming.StateField))))))
        };

        if (plan.Regions.Count > 0)
        {
            var bodyRewriter = new TokenRewriter(_semanticModel, info);
            var orderedRegions = plan.Regions
                .OrderByDescending(r => r.FirstState)
                .ThenBy(r => r.LastState)
                .ToList();

            foreach (var region in orderedRegions)
            {
                var inRange = BinaryExpression(SyntaxKind.LogicalAndExpression,
                    BinaryExpression(SyntaxKind.GreaterThanOrEqualExpression,
                        IdentifierName(RunningStateResetLocal),
                        LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(region.FirstState))),
                    BinaryExpression(SyntaxKind.LessThanOrEqualExpression,
                        IdentifierName(RunningStateResetLocal),
                        LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(region.LastState))));

                var cleanup = BuildCleanupStatements(region, info, bodyRewriter);
                statements.Add(IfStatement(inRange, Block(cleanup)));
            }
        }

        statements.Add(Assign(
            IteratorLoweringNaming.StateField,
            PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression,
                LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(1)))));

        return MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), Identifier("Dispose"))
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithBody(Block(statements));
    }

    private static List<StatementSyntax> BuildCleanupStatements(
        RegionPlan region,
        MethodLoweringInfo info,
        TokenRewriter bodyRewriter)
    {
        switch (region.Kind)
        {
            case RegionKind.ForeachDispose:
            {
                var fieldName = region.EnumeratorField!.FieldName;
                var isValueType = info.Foreaches.Values
                    .Any(f => f.EnumeratorField == region.EnumeratorField && f.IsEnumeratorValueType);
                var dispose = ExpressionStatement(InvocationExpression(
                    MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                        ParenthesizedExpression(CastExpression(
                            ParseTypeName("System.IDisposable"),
                            IdentifierName(fieldName))),
                        IdentifierName("Dispose"))));
                // 值类型枚举器（如 List<T>.Enumerator）永不为 null；装箱转换即可 Dispose。
                if (isValueType)
                {
                    return new List<StatementSyntax> { dispose };
                }
                var notNull = BinaryExpression(SyntaxKind.NotEqualsExpression,
                    IdentifierName(fieldName),
                    LiteralExpression(SyntaxKind.NullLiteralExpression));
                return new List<StatementSyntax> { IfStatement(notNull, Block(dispose)) };
            }
            case RegionKind.UserFinally:
            {
                var tryNode = (TryStatementSyntax)info.RegionByNode
                    .First(kv => kv.Value == region).Key;
                var transformed = (BlockSyntax)bodyRewriter.Visit(tryNode.Finally!.Block)!;
                return transformed.Statements.ToList();
            }
            default:
                throw new InvalidOperationException($"Unknown region kind {region.Kind}.");
        }
    }

    private static MethodDeclarationSyntax BuildResetMethod()
        => MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), Identifier("Reset"))
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithBody(Block(
                ThrowStatement(ObjectCreationExpression(ParseTypeName("System.NotSupportedException"))
                    .WithArgumentList(ArgumentList()))));

    private MethodDeclarationSyntax BuildGetEnumeratorMethod(
        StateMachinePlan plan, MethodDeclarationSyntax method)
    {
        var cloneArgs = new List<ArgumentSyntax>();
        if (!plan.IsStatic)
            cloneArgs.Add(Argument(IdentifierName(IteratorLoweringNaming.ThisField)));
        foreach (var param in plan.Parameters)
            cloneArgs.Add(Argument(IdentifierName(param.FieldName)));
        cloneArgs.Add(Argument(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(-2))));

        var genericTypeName = plan.StateMachineClassName + GetGenericTypeArgs(method);
        var cloneCreation = ObjectCreationExpression(ParseTypeName(genericTypeName))
            .WithArgumentList(ArgumentList(SeparatedList(cloneArgs)));

        var condition = BinaryExpression(SyntaxKind.LogicalAndExpression,
            BinaryExpression(SyntaxKind.EqualsExpression,
                IdentifierName(IteratorLoweringNaming.StateField),
                PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression,
                    LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(2)))),
            BinaryExpression(SyntaxKind.EqualsExpression,
                IdentifierName(IteratorLoweringNaming.InitialThreadIdField),
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    ParseName("System.Environment"),
                    IdentifierName("CurrentManagedThreadId"))));

        var reuse = Block(
            Assign(IteratorLoweringNaming.StateField,
                LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0))),
            ReturnStatement(ThisExpression()));

        var clone = Block(
            LocalDeclarationStatement(VariableDeclaration(IdentifierName("var"))
                .WithVariables(SingletonSeparatedList(
                    VariableDeclarator("e").WithInitializer(EqualsValueClause(cloneCreation))))),
            Assign(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                IdentifierName("e"), IdentifierName(IteratorLoweringNaming.StateField)),
                LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0))),
            ReturnStatement(IdentifierName("e")));

        var ifStmt = IfStatement(condition, reuse).WithElse(ElseClause(clone));

        var returnType = plan.IsNonGenericReturn
            ? ParseTypeName("System.Collections.IEnumerator")
            : GenericName("IEnumerator").WithTypeArgumentList(
                TypeArgumentList(SingletonSeparatedList(ParseTypeName(plan.ElementType))));

        return MethodDeclaration(returnType, Identifier("GetEnumerator"))
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

    private static ExpressionStatementSyntax Assign(string name, ExpressionSyntax value)
        => Assign(IdentifierName(name), value);

    private static ExpressionStatementSyntax Assign(ExpressionSyntax target, ExpressionSyntax value)
        => ExpressionStatement(AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            target, value));

    private static string GetGenericTypeArgs(MethodDeclarationSyntax method)
    {
        if (method.TypeParameterList == null) return "";
        var args = string.Join(", ", method.TypeParameterList.Parameters.Select(p => p.Identifier.ValueText));
        return $"<{args}>";
    }

    internal static string GetArrayIndexFieldName(ForeachLoweringInfo info)
        => info.EnumeratorField != null
            ? info.EnumeratorField.FieldName + "_index"
            : "_cs2jEnum_array_index";

    private static CompilationUnitSyntax EnsureUsings(CompilationUnitSyntax root)
    {
        var usings = root.Usings.Select(u => u.Name?.ToString() ?? "").ToHashSet();
        var needed = new[]
        {
            "System",
            "System.Collections",
            "System.Collections.Generic",
        };
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

    /// <summary>
    /// 纯记号替换：提升符号 → 字段、裸实例成员 → _cs2jThis.X、
    /// 全量提升的局部声明 → 字段赋值。不改变控制结构。
    /// </summary>
    internal sealed class TokenRewriter : CSharpSyntaxRewriter
    {
        internal static readonly SyntaxAnnotation WrapperAnnotation = new("Cs2jIteratorStatementWrapper");

        private readonly SemanticModel _semanticModel;
        private readonly MethodLoweringInfo _info;
        private readonly StateMachinePlan _plan;
        private readonly IMethodSymbol _methodSymbol;

        public TokenRewriter(SemanticModel semanticModel, MethodLoweringInfo info)
        {
            _semanticModel = semanticModel;
            _info = info;
            _plan = info.Plan;
            _methodSymbol = semanticModel.GetDeclaredSymbol(info.Method)!;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            var symbol = _semanticModel.GetSymbolInfo(node).Symbol;
            if (symbol != null && _info.HoistedSymbols.TryGetValue(symbol, out var field))
            {
                if (!IsInsideNameof(node))
                {
                    return IdentifierName(field.FieldName);
                }
            }

            if (!_plan.IsStatic && IsBareOriginalInstanceMember(symbol))
            {
                return MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName(IteratorLoweringNaming.ThisField),
                    IdentifierName(node.Identifier));
            }

            return base.VisitIdentifierName(node);
        }

        public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
        {
            var symbol = _semanticModel.GetSymbolInfo(node).Symbol;
            if (!_plan.IsStatic && IsBareOriginalInstanceMember(symbol))
            {
                var visited = (GenericNameSyntax)base.VisitGenericName(node)!;
                return MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName(IteratorLoweringNaming.ThisField),
                    visited);
            }
            return base.VisitGenericName(node);
        }

        public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node)
            => _plan.IsStatic
                ? base.VisitThisExpression(node)
                : IdentifierName(IteratorLoweringNaming.ThisField);

        public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            var replacements = new List<StatementSyntax>();
            foreach (var variable in node.Declaration.Variables)
            {
                if (_semanticModel.GetDeclaredSymbol(variable) is not { } local
                    || !_info.HoistedSymbols.TryGetValue(local, out var field))
                {
                    continue;
                }
                if (variable.Initializer != null)
                {
                    var initializer = (ExpressionSyntax)Visit(variable.Initializer.Value)!;
                    replacements.Add(ExpressionStatement(
                        AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                            IdentifierName(field.FieldName), initializer)));
                }
            }
            return Block(replacements).WithAdditionalAnnotations(WrapperAnnotation);
        }

        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
        {
            if (node.Declaration == null)
            {
                return base.VisitForStatement(node);
            }

            var hoistedInit = new List<StatementSyntax>();
            foreach (var variable in node.Declaration.Variables)
            {
                if (_semanticModel.GetDeclaredSymbol(variable) is { } local
                    && _info.HoistedSymbols.TryGetValue(local, out var field)
                    && variable.Initializer != null)
                {
                    var initializer = (ExpressionSyntax)Visit(variable.Initializer.Value)!;
                    hoistedInit.Add(ExpressionStatement(
                        AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                            IdentifierName(field.FieldName), initializer)));
                }
            }

            var newFor = node.WithDeclaration(null)
                .WithCondition((ExpressionSyntax?)Visit(node.Condition))
                .WithInitializers(SeparatedList(node.Initializers.Select(i => (ExpressionSyntax)Visit(i)!)))
                .WithIncrementors(SeparatedList(node.Incrementors.Select(i => (ExpressionSyntax)Visit(i)!)))
                .WithStatement((StatementSyntax)Visit(node.Statement)!);

            if (hoistedInit.Count == 0)
            {
                return newFor;
            }
            return Block(hoistedInit.Concat(new[] { newFor }).ToList())
                .WithAdditionalAnnotations(WrapperAnnotation);
        }

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var visited = (BlockSyntax)base.VisitBlock(node)!;
            return StatementWrapperFlattener.FlattenBlock(visited);
        }

        public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        {
            var visited = (SwitchSectionSyntax)base.VisitSwitchSection(node)!;
            var flat = new List<StatementSyntax>();
            StatementWrapperFlattener.FlattenInto(visited.Statements, flat);
            return visited.WithStatements(List(flat));
        }

        private bool IsBareOriginalInstanceMember(ISymbol? symbol)
        {
            if (symbol is not (IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol))
                return false;
            if (symbol.IsStatic) return false;
            if (symbol is IMethodSymbol { MethodKind: not (MethodKind.Ordinary
                    or MethodKind.ExplicitInterfaceImplementation) })
                return false;

            var owner = symbol.ContainingType;
            if (owner == null) return false;

            var cursor = _methodSymbol.ContainingType;
            while (cursor != null)
            {
                if (SymbolEqualityComparer.Default.Equals(cursor, owner))
                    return true;
                cursor = cursor.BaseType;
            }
            return false;
        }

        private static bool IsInsideNameof(SyntaxNode node)
        {
            var argument = node.FirstAncestorOrSelf<ArgumentSyntax>();
            if (argument?.Parent is ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation })
            {
                return invocation.Expression is IdentifierNameSyntax id
                    && id.Identifier.Text == "nameof"
                    || invocation.Expression is GenericNameSyntax gn
                    && gn.Identifier.Text == "nameof";
            }
            return false;
        }
    }

    /// <summary>
    /// 把迭代器方法体扁平化为单层 goto 控制流：resume 标签与所有 goto 同处
    /// MoveNext 顶层块作用域（C# 禁止 goto 跳入嵌套块）。yield 走挂起协议；
    /// 离开保护区域的每条边（正常/yield break/break/continue/异常）显式发射清理。
    /// </summary>
    private sealed class BodyFlattener
    {
        private abstract record Frame;
        private sealed record LoopFrame(string BeginLabel, string EndLabel) : Frame;
        // ForeachDispose 区域记录其所属循环的 begin 标签：continue 回到该循环时
        // 枚举器跨迭代存活，不能作为跨越区域被释放。
        private sealed record RegionFrame(RegionPlan Region, string? OwningLoopBeginLabel = null) : Frame;

        private readonly SemanticModel _semanticModel;
        private readonly MethodLoweringInfo _info;
        private readonly TokenRewriter _token;
        private readonly List<StatementSyntax> _out = new();
        private readonly List<Frame> _stack = new();
        private readonly Dictionary<YieldStatementSyntax, int> _yieldStates = new();
        private int _labelCounter;

        public BodyFlattener(
            SemanticModel semanticModel,
            MethodLoweringInfo info,
            TokenRewriter token)
        {
            _semanticModel = semanticModel;
            _info = info;
            _token = token;
            for (var i = 0; i < info.YieldReturns.Count; i++)
            {
                _yieldStates[info.YieldReturns[i]] = i + 1;
            }
        }

        public List<StatementSyntax> FlattenBody(BlockSyntax body)
        {
            foreach (var statement in body.Statements)
            {
                FlattenStatement(statement);
            }
            return _out;
        }

        private ExpressionSyntax Tx(ExpressionSyntax node)
            => (ExpressionSyntax)_token.Visit(node)!;

        private StatementSyntax Ts(StatementSyntax node)
            => (StatementSyntax)_token.Visit(node)!;

        private string NewLabel() => "_cs2jL_" + (++_labelCounter);

        private void EmitLabel(string label)
            => _out.Add(LabeledStatement(
                Identifier(label), Token(SyntaxKind.ColonToken), EmptyStatement()));

        private static GotoStatementSyntax Goto(string label)
            => GotoStatement(SyntaxKind.GotoStatement, IdentifierName(label));

        private static ExpressionSyntax Not(ExpressionSyntax condition)
            => PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                ParenthesizedExpression(condition));

        private void EmitActive(int regionId)
            => _out.Add(Assign(IteratorLoweringNaming.ActiveRegionField, IntLiteral(regionId)));

        private void EmitCleanup(RegionPlan region)
        {
            _out.AddRange(BuildCleanupStatements(region, _info, _token));
        }

        private void FlattenStatement(StatementSyntax node)
        {
            switch (node)
            {
                case BlockSyntax block:
                    foreach (var s in block.Statements) FlattenStatement(s);
                    return;

                case YieldStatementSyntax yield:
                    FlattenYield(yield);
                    return;

                case LocalDeclarationStatementSyntax:
                    // 全部局部已提升：记号层把声明转为包装块字段赋值。
                    var visited = Ts(node);
                    if (visited is BlockSyntax wrapper
                        && wrapper.HasAnnotation(TokenRewriter.WrapperAnnotation))
                    {
                        _out.AddRange(wrapper.Statements);
                    }
                    else
                    {
                        _out.Add(visited);
                    }
                    return;

                case BreakStatementSyntax:
                    EmitLoopEdge(continueTarget: false);
                    return;

                case ContinueStatementSyntax:
                    EmitLoopEdge(continueTarget: true);
                    return;

                case IfStatementSyntax ifStatement:
                    FlattenIf(ifStatement);
                    return;

                case WhileStatementSyntax whileStatement:
                    FlattenWhile(whileStatement);
                    return;

                case DoStatementSyntax doStatement:
                    FlattenDo(doStatement);
                    return;

                case ForStatementSyntax forStatement:
                    FlattenFor(forStatement);
                    return;

                case ForEachStatementSyntax forEach
                    when _info.Foreaches.ContainsKey(forEach):
                    FlattenForEach(forEach);
                    return;

                case TryStatementSyntax tryStatement
                    when _info.RegionByNode.TryGetValue(tryStatement, out var tryRegion):
                    EmitActive(tryRegion.Id);
                    _stack.Add(new RegionFrame(tryRegion));
                    foreach (var s in tryStatement.Block.Statements) FlattenStatement(s);
                    _stack.RemoveAt(_stack.Count - 1);
                    // 先把活动区域恢复为外层，再跑清理：清理自身抛异常时
                    // 外层 catch 不会重复本区域，但仍会补跑外层链。
                    EmitActive(tryRegion.ParentId ?? -1);
                    EmitCleanup(tryRegion);
                    return;

                default:
                    // 不含 yield 的透传构造（无 yield try/catch、switch 等已由闸门保证）。
                    _out.Add(Ts(node));
                    return;
            }
        }

        private void FlattenYield(YieldStatementSyntax node)
        {
            if (node.IsKind(SyntaxKind.YieldReturnStatement) && node.Expression != null)
            {
                var state = _yieldStates[node];
                _out.Add(Assign(IteratorLoweringNaming.CurrentField, Tx(node.Expression)));
                _out.Add(Assign(IteratorLoweringNaming.StateField, IntLiteral(state)));
                _out.Add(ReturnStatement(LiteralExpression(SyntaxKind.TrueLiteralExpression)));
                EmitLabel(IteratorLoweringNaming.GetResumeLabel(state));
                // resume 后立即清 0：表示区域内运行中（异常路径改由活动区域字段判定）。
                _out.Add(Assign(IteratorLoweringNaming.StateField, IntLiteral(0)));
                return;
            }

            // yield break：逐层清理当前活动区域（内层→外层），结束枚举。
            var crossed = _stack.OfType<RegionFrame>().Select(f => f.Region).Reverse().ToList();
            if (crossed.Count > 0)
            {
                EmitActive(-1);
                _out.Add(BuildNestedCleanupChain(crossed));
            }
            _out.Add(Assign(IteratorLoweringNaming.StateField, IntLiteral(-1)));
            _out.Add(ReturnStatement(LiteralExpression(SyntaxKind.FalseLiteralExpression)));
        }

        /// <summary>
        /// 内层清理抛异常时外层 finally 仍须执行：构造嵌套 try/finally 链。
        /// </summary>
        private StatementSyntax BuildNestedCleanupChain(IReadOnlyList<RegionPlan> innerToOuter)
            => IteratorStateMachineRewriter.BuildNestedCleanupChain(innerToOuter, _info, _token);

        private void EmitLoopEdge(bool continueTarget)
        {
            var loopIndex = -1;
            for (var i = _stack.Count - 1; i >= 0; i--)
            {
                if (_stack[i] is LoopFrame) { loopIndex = i; break; }
            }
            if (loopIndex < 0)
            {
                // 非法用户代码（break/continue 不在循环内），透传让编译器报错。
                _out.Add(continueTarget ? ContinueStatement() : BreakStatement());
                return;
            }

            var loop = (LoopFrame)_stack[loopIndex];
            var crossedFrames = _stack.Skip(loopIndex + 1)
                .OfType<RegionFrame>()
                .Reverse()
                .ToList(); // 内层 → 外层
            if (continueTarget)
            {
                // 目标循环自身的 ForeachDispose 区域（枚举器在整个循环期间存活）
                // 及其外层帧不在 continue 清理范围内：只清理严格位于其内侧的区域。
                var ownIndex = -1;
                for (var i = 0; i < crossedFrames.Count; i++)
                {
                    if (crossedFrames[i].OwningLoopBeginLabel == loop.BeginLabel)
                    {
                        ownIndex = i;
                        break;
                    }
                }
                if (ownIndex >= 0)
                {
                    crossedFrames = crossedFrames.Take(ownIndex).ToList();
                }
            }
            var crossed = crossedFrames.Select(f => f.Region).ToList();
            if (crossed.Count > 0)
            {
                var remaining = crossed[^1].ParentId ?? -1;
                EmitActive(remaining);
                _out.Add(BuildNestedCleanupChain(crossed));
            }
            _out.Add(Goto(continueTarget ? loop.BeginLabel : loop.EndLabel));
        }

        private void FlattenIf(IfStatementSyntax node)
        {
            var endLabel = NewLabel();
            var condition = Tx(node.Condition);

            if (node.Else == null)
            {
                _out.Add(IfStatement(Not(condition), Block(Goto(endLabel))));
                FlattenStatement(node.Statement);
                EmitLabel(endLabel);
                return;
            }

            var elseLabel = NewLabel();
            _out.Add(IfStatement(Not(condition), Block(Goto(elseLabel))));
            var thenStart = _out.Count;
            FlattenStatement(node.Statement);
            // then 分支自身终结（return/throw/yield break 发射序列）时，尾 goto 不可达，省略。
            if (!SegmentTerminates(_out, thenStart))
            {
                _out.Add(Goto(endLabel));
            }
            EmitLabel(elseLabel);
            FlattenStatement(node.Else.Statement);
            EmitLabel(endLabel);
        }

        /// <summary>
        /// 展平后的分支是否必然终结：末语句为 return/throw（保守判定，仅用于消除不可达代码）。
        /// </summary>
        private static bool SegmentTerminates(List<StatementSyntax> statements, int start)
        {
            for (var i = statements.Count - 1; i >= start; i--)
            {
                var s = statements[i];
                if (s is ReturnStatementSyntax or ThrowStatementSyntax)
                    return true;
                if (s is BlockSyntax b && b.Statements.Count > 0
                    && b.Statements.Last() is ReturnStatementSyntax or ThrowStatementSyntax)
                    return true;
                return false;
            }
            return false;
        }

        private static bool LoopBodyHasDirectBreak(StatementSyntax body, SyntaxNode loop)
        {
            foreach (var breakStmt in body.DescendantNodesAndSelf()
                         .OfType<BreakStatementSyntax>())
            {
                var cursor = breakStmt.Parent;
                while (cursor != null && cursor != loop)
                {
                    if (cursor is CommonForEachStatementSyntax
                        or ForStatementSyntax
                        or WhileStatementSyntax
                        or DoStatementSyntax
                        or SwitchStatementSyntax)
                    {
                        break;
                    }
                    cursor = cursor.Parent;
                }
                if (cursor == loop)
                {
                    return true;
                }
            }
            return false;
        }

        private void FlattenWhile(WhileStatementSyntax node)
        {
            var beginLabel = NewLabel();
            var endLabel = NewLabel();
            EmitLabel(beginLabel);
            _out.Add(IfStatement(Not(Tx(node.Condition)), Block(Goto(endLabel))));
            _stack.Add(new LoopFrame(beginLabel, endLabel));
            FlattenStatement(node.Statement);
            _stack.RemoveAt(_stack.Count - 1);
            _out.Add(Goto(beginLabel));
            EmitLabel(endLabel);
        }

        private void FlattenDo(DoStatementSyntax node)
        {
            var beginLabel = NewLabel();
            var endLabel = NewLabel();
            var hasBreak = LoopBodyHasDirectBreak(node.Statement, node);
            EmitLabel(beginLabel);
            _stack.Add(new LoopFrame(beginLabel, endLabel));
            FlattenStatement(node.Statement);
            _stack.RemoveAt(_stack.Count - 1);
            _out.Add(IfStatement(Tx(node.Condition), Block(Goto(beginLabel))));
            if (hasBreak)
            {
                EmitLabel(endLabel);
            }
        }

        private void FlattenFor(ForStatementSyntax node)
        {
            // 声明变量全部提升：初始化器变字段赋值。
            if (node.Declaration != null)
            {
                foreach (var variable in node.Declaration.Variables)
                {
                    if (_semanticModel.GetDeclaredSymbol(variable) is { } local
                        && _info.HoistedSymbols.TryGetValue(local, out var field)
                        && variable.Initializer != null)
                    {
                        _out.Add(Assign(field.FieldName, Tx(variable.Initializer!.Value)));
                    }
                }
            }
            foreach (var init in node.Initializers)
            {
                _out.Add(ExpressionStatement(Tx(init)));
            }

            var beginLabel = NewLabel();
            var endLabel = NewLabel();
            EmitLabel(beginLabel);
            if (node.Condition != null)
            {
                _out.Add(IfStatement(Not(Tx(node.Condition)), Block(Goto(endLabel))));
            }
            _stack.Add(new LoopFrame(beginLabel, endLabel));
            FlattenStatement(node.Statement);
            _stack.RemoveAt(_stack.Count - 1);
            foreach (var incrementor in node.Incrementors)
            {
                _out.Add(ExpressionStatement(Tx(incrementor)));
            }
            _out.Add(Goto(beginLabel));
            EmitLabel(endLabel);
        }

        private void FlattenForEach(ForEachStatementSyntax node)
        {
            var fe = _info.Foreaches[node];
            var beginLabel = NewLabel();
            var endLabel = NewLabel();
            var body = node.Statement;

            if (fe.IsArray)
            {
                var arrayField = fe.EnumeratorField!;
                var idxField = GetArrayIndexFieldName(fe);
                _out.Add(Assign(arrayField.FieldName, Tx(node.Expression)));
                _out.Add(Assign(idxField, IntLiteral(0)));
                EmitLabel(beginLabel);
                _out.Add(IfStatement(
                    Not(BinaryExpression(SyntaxKind.LessThanExpression,
                        IdentifierName(idxField),
                        MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                            IdentifierName(arrayField.FieldName), IdentifierName("Length")))),
                    Block(Goto(endLabel))));
                _out.Add(ExpressionStatement(AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    IdentifierName(fe.ItemFieldName),
                    ElementAccessExpression(IdentifierName(arrayField.FieldName))
                        .WithArgumentList(BracketedArgumentList(SingletonSeparatedList(
                            Argument(IdentifierName(idxField))))))));
                _stack.Add(new LoopFrame(beginLabel, endLabel));
                FlattenStatement(body);
                _stack.RemoveAt(_stack.Count - 1);
                _out.Add(ExpressionStatement(PostfixUnaryExpression(
                    SyntaxKind.PostIncrementExpression, IdentifierName(idxField))));
                _out.Add(Goto(beginLabel));
                EmitLabel(endLabel);
                return;
            }

            var enumField = fe.EnumeratorField!;
            _out.Add(ExpressionStatement(AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                IdentifierName(enumField.FieldName),
                InvocationExpression(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    Tx(node.Expression), IdentifierName(fe.GetEnumeratorMethodName!))))));

            var region = fe.Region;
            var hasBreak = LoopBodyHasDirectBreak(body, node);
            EmitLabel(beginLabel);
            var moveNextCall = InvocationExpression(MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                IdentifierName(enumField.FieldName), IdentifierName("MoveNext")));
            if (region != null)
            {
                // MoveNext 抛异常也须 Dispose 枚举器（foreach 语义）。
                EmitActive(region.Id);
            }
            // 可释放形态的正常耗尽走清理标签；无清理时直接到循环末标签。
            var exhaustedTarget = region != null ? NewLabel() : endLabel;
            _out.Add(IfStatement(Not(moveNextCall), Block(Goto(exhaustedTarget))));

            ExpressionSyntax currentAccess = MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                IdentifierName(enumField.FieldName), IdentifierName(fe.CurrentMemberName));
            var feInfo = _semanticModel.GetForEachStatementInfo(node);
            if (feInfo.CurrentProperty != null
                && feInfo.ElementType != null
                && !_semanticModel.Compilation.ClassifyConversion(
                        feInfo.CurrentProperty.Type, feInfo.ElementType).IsIdentity)
            {
                currentAccess = CastExpression(
                    ParseTypeName(feInfo.ElementType.ToDisplayString()), currentAccess);
            }
            _out.Add(Assign(fe.ItemFieldName, currentAccess));

            _stack.Add(new LoopFrame(beginLabel, endLabel));
            if (region != null)
            {
                _stack.Add(new RegionFrame(region, beginLabel));
            }
            FlattenStatement(body);
            if (region != null)
            {
                _stack.RemoveAt(_stack.Count - 1);
            }
            _stack.RemoveAt(_stack.Count - 1);
            _out.Add(Goto(beginLabel));

            if (region != null)
            {
                // 正常耗尽：恢复外层活动区域后 Dispose；Dispose 抛异常由外层 catch 补外层链。
                EmitLabel(exhaustedTarget);
                EmitActive(region.ParentId ?? -1);
                EmitCleanup(region);
            }
            // 可释放形态的末标签仅 break 边引用；数组/不可释放形态由条件耗尽 goto 引用。
            if (region == null || hasBreak)
            {
                EmitLabel(endLabel);
            }
        }
    }

    /// <summary>
    /// Lifts statements out of wrapper blocks emitted at statement-list positions
    /// (declaration split, hoisted for-init).
    /// </summary>
    private static class StatementWrapperFlattener
    {
        public static BlockSyntax FlattenBlock(BlockSyntax block)
        {
            var statements = new List<StatementSyntax>();
            FlattenInto(block.Statements, statements);
            return block.WithStatements(List(statements));
        }

        public static void FlattenInto(
            IReadOnlyList<StatementSyntax> source, List<StatementSyntax> target)
        {
            foreach (var statement in source)
            {
                if (statement is BlockSyntax wrapper
                    && wrapper.HasAnnotation(TokenRewriter.WrapperAnnotation))
                {
                    FlattenInto(wrapper.Statements.ToList(), target);
                }
                else
                {
                    target.Add(statement);
                }
            }
        }
    }
}
