using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// R3：fixed 语句改写（设计 D2/D2b/D2c，§4 组件表）。整个 fixed 块改写为
/// 可编译的纯托管普通块。表示约定（语法实测：FixedStatementSyntax.Declaration
/// 的 Type 是 PointerTypeSyntax，每个 VariableDeclarator 带 Initializer）：
/// 每个固定指针 p → 数组引用局部 + int 索引局部，索引局部沿用原名 p，
/// 故体内算术/比较/自增文本天然合法；仅解引用、下标、指针声明等被重写。
/// 五类初始式：数组、&amp;arr[k]、指针别名、null、string，外加 &amp;scalar 单槽。
/// </summary>
internal sealed class FixedStatementRewriter : CSharpSyntaxRewriter
{
    public static readonly SyntaxAnnotation FixedReplacedAnnotation = new("c06-fixed-replaced");

    private enum InitKind { Array, Offset, Alias, Null, String, Scalar, Error }

    private sealed record OffsetPayload(ExpressionSyntax Receiver, ExpressionSyntax Index);
    private sealed record AliasPayload(string SourceArray, ExpressionSyntax Index, bool ReadOnly);

    private sealed class PtrInfo
    {
        public required string ArrayName { get; set; }
        public bool ReadOnlyString { get; init; }
        public bool Resolved { get; set; } = true;
    }

    private sealed record FrameRecord(
        VariableDeclaratorSyntax Declarator,
        ILocalSymbol? Symbol,
        InitKind Kind,
        string ArrayName,
        object? Payload);

    private readonly SemanticModel _model;
    private readonly HashSet<IFieldSymbol> _rewrittenFields;
    private readonly UnsafeFfmStatistics _statistics;
    private readonly OriginalSyntaxResolver _resolver;

    private readonly Dictionary<ISymbol, PtrInfo> _infos = new();
    private readonly HashSet<string> _generatedNames = new();
    private readonly Dictionary<SyntaxNode, HashSet<string>> _scopeNameCache = new();

    public FixedStatementRewriter(
        SemanticModel model,
        HashSet<IFieldSymbol> rewrittenFields,
        UnsafeFfmStatistics statistics,
        OriginalSyntaxResolver resolver)
    {
        _model = model;
        _rewrittenFields = rewrittenFields;
        _statistics = statistics;
        _resolver = resolver;
    }

    public List<UnsafeFfmDiagnostic> Diagnostics { get; } = new();

    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == UnsafeFfmSeverity.Error);

    // ---- fixed 节点 ----

    public override SyntaxNode? VisitFixedStatement(FixedStatementSyntax node)
    {
        _statistics.FixedStatementsCount++;

        var records = new List<FrameRecord>();
        foreach (var declarator in node.Declaration.Variables)
        {
            var ptrSymbol = (_resolver.Resolve(declarator) is { } originalDeclarator
                ? _model.GetDeclaredSymbol(originalDeclarator)
                : null) as ILocalSymbol;
            var (kind, payload) = Classify(declarator.Initializer.Value);
            var suffix = kind == InitKind.Scalar ? "slot" : "array";
            var arrayName = MakeGeneratedName(declarator.Identifier.ValueText, suffix, node);
            records.Add(new FrameRecord(declarator, ptrSymbol, kind, arrayName, payload));

            if (kind != InitKind.Error)
            {
                CountKind(kind);
            }
        }

        // 先注册（含根符号=自身），再改写块体。
        foreach (var record in records)
        {
            if (record.Symbol == null || record.Kind == InitKind.Error) continue;
            _infos[record.Symbol] = new PtrInfo
            {
                ArrayName = record.ArrayName,
                ReadOnlyString = record.Kind == InitKind.String,
            };
        }

        var body = (StatementSyntax)Visit(node.Statement)!;

        var statements = new List<StatementSyntax>();
        var writebacks = new List<StatementSyntax>();

        var lineStart = LineStartTrivia(node);

        foreach (var record in records)
        {
            var elementType = ((PointerTypeSyntax)node.Declaration.Type).ElementType;
            var ptrName = record.Declarator.Identifier.ValueText;

            switch (record.Kind)
            {
                case InitKind.Array:
                    var arrayExpr = (ExpressionSyntax)record.Payload!;
                    statements.Add(ArrayLocal(record.ArrayName, elementType, arrayExpr, node));
                    statements.Add(IndexLocal(ptrName, ZeroLiteral(), lineStart));
                    break;

                case InitKind.Offset:
                    var offset = (OffsetPayload)record.Payload!;
                    statements.Add(ArrayLocal(record.ArrayName, elementType, offset.Receiver, node));
                    statements.Add(IndexLocal(ptrName, offset.Index, lineStart));
                    break;

                case InitKind.Alias:
                    var alias = (AliasPayload)record.Payload!;
                    statements.Add(ArrayLocal(record.ArrayName, elementType,
                        IdentifierName(alias.SourceArray), node));
                    statements.Add(IndexLocal(ptrName, alias.Index, lineStart));
                    break;

                case InitKind.Null:
                    statements.Add(ArrayLocal(record.ArrayName, elementType,
                        PostfixUnaryExpression(
                            SyntaxKind.SuppressNullableWarningExpression,
                            LiteralExpression(SyntaxKind.NullLiteralExpression)), node));
                    statements.Add(IndexLocal(ptrName, ZeroLiteral(), lineStart));
                    break;

                case InitKind.String:
                    var sourceExpr = (ExpressionSyntax)record.Payload!;
                    var charType = PredefinedType(Token(SyntaxKind.CharKeyword));
                    statements.Add(ArrayLocal(record.ArrayName, charType,
                        InvocationExpression(MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            sourceExpr,
                            IdentifierName("ToCharArray")))
                        .WithArgumentList(ArgumentList()), node));
                    statements.Add(IndexLocal(ptrName, ZeroLiteral(), lineStart));
                    break;

                case InitKind.Scalar:
                    var target = (ExpressionSyntax)record.Payload!;
                    statements.Add(ArrayLocal(record.ArrayName, elementType,
                        ArrayCreationExpression(
                            ArrayType(elementType)
                                .WithRankSpecifiers(SingletonList(ArrayRankSpecifier())))
                        .WithInitializer(InitializerExpression(
                            SyntaxKind.ArrayInitializerExpression,
                            SingletonSeparatedList(target))), node));
                    statements.Add(IndexLocal(ptrName, ZeroLiteral(), lineStart));
                    writebacks.Add(ExpressionStatement(AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        target,
                        ElementAccessExpression(IdentifierName(record.ArrayName))
                            .WithArgumentList(BracketedArgumentList(SingletonSeparatedList(
                                Argument(ZeroLiteral()))))))
                        .WithLeadingTrivia(lineStart));
                    break;

                case InitKind.Error:
                    // 诊断已记录；占位让遍历继续，文件最终回滚。
                    statements.Add(EmptyStatement());
                    break;
            }
        }

        if (body is BlockSyntax bodyBlock)
        {
            statements.AddRange(bodyBlock.Statements);
        }
        else
        {
            statements.Add(body);
        }

        statements.AddRange(writebacks);

        return Block(statements).WithAdditionalAnnotations(FixedReplacedAnnotation);
    }

    // ---- 初始式分类 ----

    private (InitKind Kind, object? Payload) Classify(ExpressionSyntax raw)
    {
        var e = UnwrapParenthesized(raw);

        if (e.IsKind(SyntaxKind.NullLiteralExpression))
            return (InitKind.Null, null);

        if (e is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.AddressOfExpression } address)
        {
            if (address.Operand is ElementAccessExpressionSyntax elementAccess)
            {
                var arg = elementAccess.ArgumentList.Arguments[0].Expression;

                if (TryResolvePointer(elementAccess.Expression,
                        out var trackedArray, out var receiverIndex, out var trackedReadOnly))
                {
                    var index = BinaryExpression(
                        SyntaxKind.AddExpression,
                        ParenthesizedExpression(receiverIndex),
                        ParenthesizedExpression(arg));
                    return (InitKind.Alias,
                        new AliasPayload(trackedArray, index, trackedReadOnly));
                }

                var receiverType = TypeInfoFor(elementAccess.Expression).Type;
                var receiverSymbol = GetIdentifierSymbol(elementAccess.Expression);
                if (receiverType is IArrayTypeSymbol
                    || (receiverSymbol is IFieldSymbol field && _rewrittenFields.Contains(field)))
                {
                    return (InitKind.Offset,
                        new OffsetPayload(elementAccess.Expression, arg));
                }

                AddError(
                    UnsafeFfmDiagnosticCode.UF020,
                    $"fixed pointer initializer '{raw}' takes the address of an unprovable expression; " +
                    "only addresses of managed array elements are supported in this form.",
                    raw);
                return (InitKind.Error, null);
            }

            // &scalar：局部/字段标量单槽。
            return (InitKind.Scalar, address.Operand);
        }

        // tracked pointer（标识符或纯指针算术表达式）→ 别名
        if (TryResolvePointer(e, out var aliasArray, out _, out var aliasReadOnly))
        {
            return (InitKind.Alias,
                new AliasPayload(aliasArray, e, aliasReadOnly));
        }

        // R2 改写过的 fixed 字段（模型仍为旧指针类型）→ 数组
        if (GetIdentifierSymbol(e) is IFieldSymbol rewrittenField
            && _rewrittenFields.Contains(rewrittenField))
        {
            return (InitKind.Array, e);
        }

        var type = TypeInfoFor(e).Type;
        if (type?.SpecialType == SpecialType.System_String)
            return (InitKind.String, e);
        if (type is IArrayTypeSymbol)
            return (InitKind.Array, e);

        AddError(
            UnsafeFfmDiagnosticCode.UF020,
            $"fixed pointer initializer '{raw}' does not originate from a provable managed array, " +
            "array element address, pointer alias, string or scalar slot; the original file was preserved.",
            raw);
        return (InitKind.Error, null);
    }

    // ---- 块体表达式 ----

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (node.Kind() == SyntaxKind.PointerIndirectionExpression)
        {
            if (TryResolvePointer(node.Operand, out var array, out var index, out var readOnly))
            {
                if (readOnly && IsWriteTarget(node))
                {
                    AddError(
                        UnsafeFfmDiagnosticCode.UF030,
                        $"write through fixed string pointer '{node}' is undefined and cannot be " +
                        "written back; the fixed string buffer is read-only.",
                        node);
                    return node;
                }

                return ElementAccessExpression(IdentifierName(array))
                    .WithArgumentList(BracketedArgumentList(SingletonSeparatedList(
                        Argument(index))));
            }

            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"cannot statically prove what pointer expression '{node}' points to; the original " +
                "file was preserved.",
                node);
            return node;
        }

        if (node.Kind() == SyntaxKind.AddressOfExpression)
        {
            // 被指针局部声明/赋值拦截时本节点不会被访问到；落到此处即逃逸。
            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"address-of expression '{node}' escapes the supported fixed forms (it must be a " +
                "fixed statement initializer); the original file was preserved.",
                node);
            return node;
        }

        return base.VisitPrefixUnaryExpression(node);
    }

    public override SyntaxNode? VisitElementAccessExpression(ElementAccessExpressionSyntax node)
    {
        var receiverType = TypeInfoFor(node.Expression).Type;
        if (receiverType is IPointerTypeSymbol)
        {
            // R2 已改写为数组字段的 fixed 缓冲区：buf[i] 语法对数组天然合法，保持原样。
            if (GetIdentifierSymbol(node.Expression) is IFieldSymbol rewrittenReceiver
                && _rewrittenFields.Contains(rewrittenReceiver))
            {
                return base.VisitElementAccessExpression(node);
            }

            if (node.ArgumentList.Arguments.Count != 1)
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF020,
                    $"pointer element access '{node}' has an unsupported argument list.",
                    node);
                return node;
            }

            if (TryResolvePointer(node.Expression, out var array, out var receiverIndex, out var readOnly))
            {
                var arg = node.ArgumentList.Arguments[0].Expression;

                if (readOnly && IsWriteTarget(node))
                {
                    AddError(
                        UnsafeFfmDiagnosticCode.UF030,
                        $"write through fixed string pointer '{node}' is undefined and cannot be " +
                        "written back; the fixed string buffer is read-only.",
                        node);
                    return node;
                }

                var index = BinaryExpression(
                    SyntaxKind.AddExpression,
                    ParenthesizedExpression(receiverIndex),
                    ParenthesizedExpression(arg));

                return ElementAccessExpression(IdentifierName(array))
                    .WithArgumentList(BracketedArgumentList(SingletonSeparatedList(
                        Argument(index))));
            }

            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"cannot statically prove what pointer expression '{node.Expression}' points to; " +
                "the original file was preserved.",
                node);
            return node;
        }

        return base.VisitElementAccessExpression(node);
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        if (node.Kind() == SyntaxKind.PointerMemberAccessExpression)
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"struct pointer member access '{node}' (p->m) cannot be normalized in the fixed " +
                "rewriter; the original file was preserved.",
                node);
            return base.VisitMemberAccessExpression(node);
        }

        return base.VisitMemberAccessExpression(node);
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        // 赋给已登记指针局部（含声明后赋值的别名）且 RHS 为指针形态。
        if (GetIdentifierSymbol(node.Left) is ILocalSymbol leftSymbol
            && _infos.TryGetValue(leftSymbol, out var info))
        {
            var rhs = UnwrapParenthesized(node.Right);
            var rhsType = TypeInfoFor(node.Right).Type;

            if (rhsType is IPointerTypeSymbol
                || rhs.IsKind(SyntaxKind.AddressOfExpression)
                || rhs.IsKind(SyntaxKind.NullLiteralExpression))
            {
                ExpressionSyntax newRhs;

                if (rhs.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    newRhs = ZeroLiteral();
                    info.ArrayName = MakeGeneratedName(leftSymbol.Name, "array", node);
                    info.Resolved = true;
                }
                else if (TryResolvePointer(rhs, out var sourceArray, out var indexExpression, out _))
                {
                    newRhs = indexExpression;
                    info.ArrayName = sourceArray;
                    info.Resolved = true;
                }
                else
                {
                    AddError(
                        UnsafeFfmDiagnosticCode.UF020,
                        $"cannot assign pointer value '{node.Right}' to local '{leftSymbol.Name}': " +
                        "its origin is not provable; the original file was preserved.",
                        node);
                    return base.VisitAssignmentExpression(node);
                }

                var left = (ExpressionSyntax)Visit(node.Left)!;
                return node.Update(left, node.OperatorToken, newRhs);
            }
        }

        var visitedLeft = (ExpressionSyntax)Visit(node.Left)!;
        var visitedRight = (ExpressionSyntax)Visit(node.Right)!;
        if (visitedLeft == node.Left && visitedRight == node.Right) return node;
        return node.Update(visitedLeft, node.OperatorToken, visitedRight);
    }

    public override SyntaxNode? VisitArgument(ArgumentSyntax node)
    {
        var expression = UnwrapParenthesized(node.Expression);
        if (TypeInfoFor(node.Expression).Type is IPointerTypeSymbol
            && !(expression is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.AddressOfExpression })
            // 可解析为「数组+int 索引」的 tracked 指针：归一后实参是 int，不是逃逸。
            && !TryResolvePointer(node.Expression, out _, out _, out _))
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"pointer expression '{node.Expression}' is passed as an argument and escapes the " +
                "fixed block; the original file was preserved.",
                node);
        }

        return base.VisitArgument(node);
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        if (node.Expression != null
            && TypeInfoFor(node.Expression).Type is IPointerTypeSymbol)
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"pointer expression '{node.Expression}' is returned and escapes the fixed block; " +
                "the original file was preserved.",
                node);
        }

        return base.VisitReturnStatement(node);
    }

    public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
    {
        if (TypeInfoFor(node.Expression).Type is IPointerTypeSymbol)
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"cast involving pointer expression '{node.Expression}' escapes the fixed model; " +
                "the original file was preserved.",
                node);
        }

        return base.VisitCastExpression(node);
    }

    // ---- 块内指针局部声明 ----

    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        var entries = node.Declaration.Variables
            .Select(v => (v, sym: (_resolver.Resolve(v) is { } originalV
                ? _model.GetDeclaredSymbol(originalV)
                : null) as ILocalSymbol))
            .ToList();

        if (!entries.Any(e => e.sym?.Type is IPointerTypeSymbol))
            return base.VisitLocalDeclarationStatement(node);

        TypeSyntax elementTypeSyntax = node.Declaration.Type is PointerTypeSyntax pointer
            ? pointer.ElementType
            : ParseTypeName(
                ((IPointerTypeSymbol)entries.First(e => e.sym!.Type is IPointerTypeSymbol).sym!.Type)
                    .PointedAtType.ToDisplayString());

        var newDeclarators = new List<VariableDeclaratorSyntax>();
        var extraStatements = new List<StatementSyntax>();

        foreach (var (declarator, symbol) in entries)
        {
            if (symbol?.Type is not IPointerTypeSymbol)
            {
                newDeclarators.Add((VariableDeclaratorSyntax)Visit(declarator)!);
                continue;
            }

            var rawInitializer = declarator.Initializer?.Value;
            string? arrayName = null;
            ExpressionSyntax? newInitializer = null;

            if (rawInitializer != null)
            {
                var initializer = UnwrapParenthesized(rawInitializer);

                if (initializer.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    arrayName = MakeGeneratedName(declarator.Identifier.ValueText, "array", node);
                    extraStatements.Add(ArrayLocal(arrayName, elementTypeSyntax,
                        PostfixUnaryExpression(
                            SyntaxKind.SuppressNullableWarningExpression,
                            LiteralExpression(SyntaxKind.NullLiteralExpression)),
                        node));
                    newInitializer = ZeroLiteral();
                }
                else if (TryResolvePointer(initializer,
                             out var sourceArray, out var indexExpression, out _))
                {
                    arrayName = sourceArray;
                    newInitializer = indexExpression;
                }
                else
                {
                    AddError(
                        UnsafeFfmDiagnosticCode.UF020,
                        $"pointer local '{declarator.Identifier.ValueText}' is not initialized from a " +
                        "provable array/slot; the original file was preserved.",
                        declarator);
                    newDeclarators.Add(declarator);
                    continue;
                }
            }

            var rebuilt = declarator.WithInitializer(
                newInitializer == null ? null : EqualsValueClause(newInitializer));
            newDeclarators.Add(rebuilt);

            _infos[symbol] = new PtrInfo
            {
                ArrayName = arrayName ?? string.Empty,
                Resolved = arrayName != null,
            };
        }

        StatementSyntax newStatement = node
            .WithDeclaration(node.Declaration
                .WithType(elementTypeSyntax)
                .WithVariables(SeparatedList(newDeclarators)));

        if (extraStatements.Count > 0)
        {
            return Block(List(extraStatements).Add(newStatement));
        }

        return newStatement;
    }

    // ---- lambda 捕获 ----

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        CheckLambdaCapture(node);
        return base.VisitSimpleLambdaExpression(node);
    }

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
    {
        CheckLambdaCapture(node);
        return base.VisitParenthesizedLambdaExpression(node);
    }

    public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
    {
        CheckLambdaCapture(node);
        return base.VisitAnonymousMethodExpression(node);
    }

    private void CheckLambdaCapture(SyntaxNode node)
    {
        foreach (var identifier in node.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var symbol = SymbolInfoFor(identifier).Symbol;
            if (symbol != null && _infos.ContainsKey(symbol))
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF020,
                    $"pointer symbol '{identifier.Identifier.ValueText}' originating from a fixed " +
                    "block is captured in a lambda/anonymous method (escape); the original file was preserved.",
                    identifier);
                return;
            }
        }
    }

    // ---- 指针对（数组 + 索引）解析 ----

    private bool TryResolvePointer(
        ExpressionSyntax raw,
        out string arrayName,
        out ExpressionSyntax indexExpression,
        out bool readOnly)
    {
        var e = UnwrapParenthesized(raw);
        arrayName = string.Empty;
        indexExpression = null!;
        readOnly = false;

        if (e is IdentifierNameSyntax identifier)
        {
            var symbol = SymbolInfoFor(identifier).Symbol;
            if (symbol != null
                && _infos.TryGetValue(symbol, out var info)
                && info.Resolved)
            {
                arrayName = info.ArrayName;
                indexExpression = e;
                readOnly = info.ReadOnlyString;
                return true;
            }

            return false;
        }

        if (e is BinaryExpressionSyntax binary
            && (binary.IsKind(SyntaxKind.AddExpression)
                || binary.IsKind(SyntaxKind.SubtractExpression)))
        {
            var leftType = TypeInfoFor(binary.Left).Type;
            var rightType = TypeInfoFor(binary.Right).Type;

            ExpressionSyntax pointerSide;
            if (leftType is IPointerTypeSymbol)
            {
                pointerSide = binary.Left;
            }
            else if (rightType is IPointerTypeSymbol && binary.IsKind(SyntaxKind.AddExpression))
            {
                pointerSide = binary.Right;
            }
            else
            {
                return false;
            }

            if (TryResolvePointer(pointerSide, out var nestedArray, out _, out var nestedReadOnly))
            {
                arrayName = nestedArray;
                indexExpression = e; // 原文即合法 int 算术
                readOnly = nestedReadOnly;
                return true;
            }

            return false;
        }

        if (e is ConditionalExpressionSyntax conditional
            && TryResolvePointer(conditional.WhenTrue, out var trueArray, out _, out var trueReadOnly)
            && TryResolvePointer(conditional.WhenFalse, out var falseArray, out _, out var falseReadOnly)
            && trueArray == falseArray)
        {
            arrayName = trueArray;
            indexExpression = e;
            readOnly = trueReadOnly || falseReadOnly;
            return true;
        }

        if (e is CheckedExpressionSyntax checkedExpression)
        {
            return TryResolvePointer(
                checkedExpression.Expression, out arrayName, out indexExpression, out readOnly);
        }

        if (e is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.AddressOfExpression } addr
            && addr.Operand is ElementAccessExpressionSyntax elementAccess
            && elementAccess.ArgumentList.Arguments.Count == 1)
        {
            var arg = elementAccess.ArgumentList.Arguments[0].Expression;

            if (TryResolvePointer(elementAccess.Expression,
                    out var receiverArray, out var receiverIndex, out var receiverReadOnly))
            {
                arrayName = receiverArray;
                indexExpression = BinaryExpression(
                    SyntaxKind.AddExpression,
                    ParenthesizedExpression(receiverIndex),
                    ParenthesizedExpression(arg));
                readOnly = receiverReadOnly;
                return true;
            }

            var directType = TypeInfoFor(elementAccess.Expression).Type;
            var directSymbol = GetIdentifierSymbol(elementAccess.Expression);
            if (directType is IArrayTypeSymbol
                || (directSymbol is IFieldSymbol directField
                    && _rewrittenFields.Contains(directField)))
            {
                arrayName = elementAccess.Expression.ToString();
                indexExpression = arg;
                return true;
            }
        }

        return false;
    }

    // ---- 构造辅助 ----

    private LocalDeclarationStatementSyntax ArrayLocal(
        string name, TypeSyntax elementType, ExpressionSyntax initializer, SyntaxNode anchor)
    {
        var arrayType = ArrayType(elementType)
            .WithRankSpecifiers(SingletonList(ArrayRankSpecifier()));
        var statement = LocalDeclarationStatement(VariableDeclaration(arrayType,
            SingletonSeparatedList(VariableDeclarator(name)
                .WithInitializer(EqualsValueClause(initializer)))));
        return statement.WithLeadingTrivia(anchor.GetLeadingTrivia());
    }

    private LocalDeclarationStatementSyntax IndexLocal(
        string name, ExpressionSyntax initializer, SyntaxTriviaList lineStart)
    {
        return LocalDeclarationStatement(VariableDeclaration(
            PredefinedType(Token(SyntaxKind.IntKeyword)),
            SingletonSeparatedList(VariableDeclarator(name)
                .WithInitializer(EqualsValueClause(initializer)))))
            .WithLeadingTrivia(lineStart);
    }

    private static LiteralExpressionSyntax ZeroLiteral() =>
        LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0));

    private static ExpressionSyntax UnwrapParenthesized(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private ISymbol? GetIdentifierSymbol(ExpressionSyntax expression)
    {
        expression = UnwrapParenthesized(expression);
        return expression switch
        {
            IdentifierNameSyntax id => SymbolInfoFor(id).Symbol,
            // f.buf 这类成员访问同样取其字段符号（R2 字段可能经实例限定）。
            MemberAccessExpressionSyntax memberAccess => SymbolInfoFor(memberAccess).Symbol,
            _ => null,
        };
    }

    // 语义查询统一经原始节点映射（重建红包装不属于旧模型树）。
    private TypeInfo TypeInfoFor(ExpressionSyntax expression)
        => _resolver.Resolve(expression) is { } originalTypeNode
            ? _model.GetTypeInfo(originalTypeNode)
            : default;

    private SymbolInfo SymbolInfoFor(ExpressionSyntax expression)
        => _resolver.Resolve(expression) is { } originalSymbolNode
            ? _model.GetSymbolInfo(originalSymbolNode)
            : default;

    private static bool IsWriteTarget(ExpressionSyntax node)
    {
        var parent = node.Parent;
        if (parent is AssignmentExpressionSyntax assignment && assignment.Left == node) return true;
        if (parent is PostfixUnaryExpressionSyntax) return true;
        if (parent is PrefixUnaryExpressionSyntax prefix
            && (prefix.Kind() == SyntaxKind.PreIncrementExpression
                || prefix.Kind() == SyntaxKind.PreDecrementExpression))
        {
            return true;
        }
        if (parent is ArgumentSyntax argument
            && (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)))
        {
            return true;
        }
        return false;
    }

    private SyntaxTriviaList LineStartTrivia(SyntaxNode node)
    {
        var indent = string.Empty;
        var leading = node.GetLeadingTrivia();
        if (leading.Count > 0)
        {
            var last = leading[^1];
            if (last.IsKind(SyntaxKind.WhitespaceTrivia))
            {
                indent = last.ToString();
            }
        }

        return TriviaList(EndOfLine(System.Environment.NewLine), Whitespace(indent));
    }

    private string MakeGeneratedName(string pointerName, string suffix, SyntaxNode node)
    {
        var member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault() ?? node;
        if (!_scopeNameCache.TryGetValue(member, out var names))
        {
            names = new HashSet<string>(
                member.DescendantTokens().Select(t => t.ValueText));
            _scopeNameCache[member] = names;
        }

        var candidate = "_" + pointerName + "_" + suffix;
        var counter = 2;
        while (names.Contains(candidate) || _generatedNames.Contains(candidate))
        {
            candidate = "_" + pointerName + "_" + suffix + "_" + counter;
            counter++;
        }

        names.Add(candidate);
        _generatedNames.Add(candidate);
        return candidate;
    }

    private void CountKind(InitKind kind)
    {
        switch (kind)
        {
            case InitKind.Array: _statistics.FixedVariablesArray++; break;
            case InitKind.Offset: _statistics.FixedVariablesOffset++; break;
            case InitKind.Alias: _statistics.FixedVariablesAlias++; break;
            case InitKind.Null: _statistics.FixedVariablesNull++; break;
            case InitKind.String: _statistics.FixedVariablesString++; break;
            case InitKind.Scalar: _statistics.FixedVariablesScalar++; break;
        }
    }

    private void AddError(UnsafeFfmDiagnosticCode code, string message, SyntaxNode node)
    {
        var line = 1;
        var character = 1;
        var positionNode = _resolver.Resolve(node) ?? node;
        try
        {
            var span = positionNode.GetLocation().GetLineSpan();
            line = span.StartLinePosition.Line + 1;
            character = span.StartLinePosition.Character + 1;
        }
        catch
        {
            // 脱离语法树的节点无法计算行号时退回 (1,1)。
        }

        Diagnostics.Add(new UnsafeFfmDiagnostic(
            UnsafeFfmSeverity.Error, code, message, null, line, character));
        _statistics.Rejections++;
        switch (code)
        {
            case UnsafeFfmDiagnosticCode.UF020: _statistics.Uf020Rejections++; break;
            case UnsafeFfmDiagnosticCode.UF030: _statistics.Uf030Rejections++; break;
        }
    }
}
