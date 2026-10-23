using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

/// <summary>
/// Statement-level rewriter for external writes to target readonly structs:
///   obj.Field = v   -> obj = obj.WithField(v)
///   obj.Field op= v -> obj = obj.WithField(obj.Field op v)
///   obj.Field++ / -- (statement level) -> obj = obj.WithField(obj.Field +/- 1)
/// For L3 (DataContainer) structs the write is instead reconstructed through
/// their full constructor:
///   obj.Prop = v    -> obj = new S(p1: obj.P1, ..., p: v, ...)
/// All other nodes are returned unchanged. Target confirmation is semantic.
/// Anything more complex (tuple assignments, chained assignments, embedded
/// assignment values, unary mutations nested in expressions, object initializers,
/// read getter rewrites, casts) is not handled here and must be normalized by
/// earlier preprocessing passes.
/// </summary>
public sealed class AssignmentRewriter : CSharpSyntaxRewriter
{
    private readonly Dictionary<string, HashSet<string>> _structFields;
    private readonly HashSet<string> _targetFieldNames = new();
    private readonly SemanticModel? _semanticModel;
    private readonly Dictionary<string, string> _variableTypes = new();
    private readonly Dictionary<string, HashSet<string>> _qualifiedNames = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, (HashSet<string> Props, List<string> CtorParams)> _l3Structs;

    public AssignmentRewriter(
        Dictionary<string, HashSet<string>> structFields,
        SemanticModel? semanticModel,
        IReadOnlySet<string>? propertyAccessStructs = null,
        IReadOnlySet<string>? withChainStructs = null,
        IReadOnlyDictionary<string, (HashSet<string> Props, List<string> CtorParams)>? l3Structs = null,
        IReadOnlyDictionary<string, HashSet<string>>? qualifiedNames = null)
    {
        _structFields = structFields;
        _semanticModel = semanticModel;
        _l3Structs = l3Structs
            ?? new Dictionary<string, (HashSet<string> Props, List<string> CtorParams)>();
        if (qualifiedNames != null)
        {
            foreach (var kvp in qualifiedNames)
                _qualifiedNames[kvp.Key] = new HashSet<string>(kvp.Value, StringComparer.Ordinal);
        }
        // propertyAccessStructs / withChainStructs are accepted for source
        // compatibility with existing call sites; L3 structs additionally expose
        // their full constructor as the reconstruction target.
        foreach (var fields in structFields.Values)
        foreach (var field in fields)
            _targetFieldNames.Add(field);
    }

    /// <summary>
    /// Builds a map of variable names to their declared types. Must be called
    /// before the tree is visited; used only as a fallback when the semantic
    /// model cannot resolve a receiver.
    /// </summary>
    public void BuildVariableTypeMap(SyntaxNode root)
    {
        _variableTypes.Clear();
        foreach (var node in root.DescendantNodes())
        {
            if (node is LocalDeclarationStatementSyntax localDecl
                && localDecl.Declaration.Variables.Count == 1)
            {
                var variable = localDecl.Declaration.Variables[0];
                var varTypeName = GetTypeName(localDecl.Declaration.Type);
                if (!string.IsNullOrEmpty(varTypeName))
                    _variableTypes[variable.Identifier.Text] = varTypeName;
                else if (variable.Initializer?.Value is ObjectCreationExpressionSyntax objCreate)
                    _variableTypes[variable.Identifier.Text] = GetTypeName(objCreate.Type);
            }
            else if (node is FieldDeclarationSyntax fieldDecl
                     && fieldDecl.Declaration.Variables.Count == 1)
            {
                var variable = fieldDecl.Declaration.Variables[0];
                var varTypeName = GetTypeName(fieldDecl.Declaration.Type);
                if (!string.IsNullOrEmpty(varTypeName))
                    _variableTypes[variable.Identifier.Text] = varTypeName;
            }
        }
    }

    private int _hoistCounter;

    /// <summary>Deterministic temp name for hoisted value-position reads.</summary>
    internal string NextHoistTempName(string fieldName)
        => "__cs2jOld" + char.ToUpperInvariant(fieldName[0]) + fieldName.Substring(1) + "_" + (_hoistCounter++);

    /// <summary>
    /// 语义解析目标字段访问：<c>receiver.F</c>（F ∈ 目标字段，receiver 为可赋值
    /// 左值且类型命中目标 struct）。命中返回成员访问与 struct 名；否则 null。
    /// 供语句级重写与值位提升共用同一判定。
    /// </summary>
    internal MemberAccessExpressionSyntax? TryResolveTargetAccess(
        ExpressionSyntax expression, out string? structName)
    {
        structName = null;
        if (expression is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Name is not IdentifierNameSyntax fieldName
            || !_targetFieldNames.Contains(fieldName.Identifier.Text)
            || !IsModifiableLValue(memberAccess.Expression))
        {
            return null;
        }

        var resolved = TryGetTargetStructName(memberAccess.Expression);
        if (resolved == null)
            return null;

        // Writes inside the struct's own instance members manage themselves
        // (WithXxx bodies, constructors, property accessors).
        if (memberAccess.Expression is not IdentifierNameSyntax
            && InsideOwnInstanceMember(expression, resolved))
        {
            return null;
        }
        if (memberAccess.Expression is ThisExpressionSyntax
            && !InsideTargetType(expression, resolved))
        {
            return null;
        }

        structName = resolved;
        return memberAccess;
    }

    /// <summary>Builds the statement-level write-back for a resolved target access.</summary>
    internal AssignmentExpressionSyntax BuildWriteForAccess(
        MemberAccessExpressionSyntax access, string structName, ExpressionSyntax value, SyntaxNode triviaFrom)
        => BuildWriteAssignment(structName, access.Expression, access.Name.Identifier.Text, value, triviaFrom);

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        // Bottom-up: statement-level writes already rewrote; what remains in
        // value positions (declaration initializers, arguments, assignment RHS)
        // is hoisted here so external readonly-struct writes never survive raw.
        var visited = (BlockSyntax)base.VisitBlock(node)!;

        var newStatements = new List<StatementSyntax>();
        var changed = false;
        foreach (var statement in visited.Statements)
        {
            var hoister = new ValuePositionWriteHoister(this);
            var rewrittenStatement = (StatementSyntax)hoister.Visit(statement)!;
            if (!ReferenceEquals(rewrittenStatement, statement) || hoister.Hoisted.Count > 0)
                changed = true;
            newStatements.AddRange(hoister.Hoisted);
            newStatements.Add(rewrittenStatement);
        }

        return changed
            ? visited.WithStatements(SyntaxFactory.List(newStatements))
            : visited;
    }

    /// <summary>
    /// 值位写提升：声明初值/实参/赋值 RHS 中的 <c>r.F++</c>/<c>--r.F</c>/
    /// <c>r.F = v</c>（含连环赋值内层）→「temp 读 + With 写回」两条语句前置，
    /// 表达式替换为 temp（后缀）或 temp±1（前缀）或值本身（赋值）。求值顺序
    /// 与 C# 一致（内层连环写先于外层写，写先于所在语句的后续副作用）。
    /// </summary>
    private sealed class ValuePositionWriteHoister : CSharpSyntaxRewriter
    {
        private readonly AssignmentRewriter _owner;
        internal readonly List<StatementSyntax> Hoisted = new();

        public ValuePositionWriteHoister(AssignmentRewriter owner)
        {
            _owner = owner;
        }

        public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
        {
            if (node.Parent is ExpressionStatementSyntax)
                return base.VisitPostfixUnaryExpression(node);

            if ((node.IsKind(SyntaxKind.PostIncrementExpression)
                 || node.IsKind(SyntaxKind.PostDecrementExpression))
                && _owner.TryResolveTargetAccess(node.Operand, out var structName) is { } access)
            {
                return HoistUnary(access, structName!, node,
                    node.IsKind(SyntaxKind.PostIncrementExpression), isPrefix: false);
            }

            return base.VisitPostfixUnaryExpression(node);
        }

        public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
        {
            if (node.Parent is ExpressionStatementSyntax)
                return base.VisitPrefixUnaryExpression(node);

            if ((node.IsKind(SyntaxKind.PreIncrementExpression)
                 || node.IsKind(SyntaxKind.PreDecrementExpression))
                && _owner.TryResolveTargetAccess(node.Operand, out var structName) is { } access)
            {
                return HoistUnary(access, structName!, node,
                    node.IsKind(SyntaxKind.PreIncrementExpression), isPrefix: true);
            }

            return base.VisitPrefixUnaryExpression(node);
        }

        public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
        {
            // 语句根形态由外层 VisitExpressionStatement/VisitAssignmentExpression
            // 处理；值位（实参/嵌套赋值/声明初值）在此提升。
            if (node.Parent is ExpressionStatementSyntax
                || !node.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                return base.VisitAssignmentExpression(node);
            }

            if (_owner.TryResolveTargetAccess(node.Left, out var structName) is { } access)
            {
                var value = (ExpressionSyntax)Visit(node.Right)!;
                // 非平凡 RHS（new/调用等有副作用的形态）先落 temp：写回与
                // 表达式替换共用同一次求值，引用类型别名不复制（与
                // struct-value-semantics 链式赋值展开的 temp 约定一致）。
                if (!IsTrivialValue(value))
                {
                    var tempName = _owner.NextHoistTempName(access.Name.Identifier.Text);
                    Hoisted.Add(SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(tempName)
                                    .WithInitializer(SyntaxFactory.EqualsValueClause(value))))));
                    value = SyntaxFactory.IdentifierName(tempName);
                }

                Hoisted.Add(SyntaxFactory.ExpressionStatement(
                    _owner.BuildWriteForAccess(access, structName!, value, node)));
                return value.WithTriviaFrom(node);
            }

            return base.VisitAssignmentExpression(node);
        }

        private static bool IsTrivialValue(ExpressionSyntax expression)
            => expression is LiteralExpressionSyntax
                or IdentifierNameSyntax
                or ThisExpressionSyntax
                or ParenthesizedExpressionSyntax { Expression: LiteralExpressionSyntax or IdentifierNameSyntax };

        private SyntaxNode HoistUnary(
            MemberAccessExpressionSyntax access, string structName,
            ExpressionSyntax original, bool isIncrement, bool isPrefix)
        {
            var fieldName = access.Name.Identifier.Text;
            var tempName = _owner.NextHoistTempName(fieldName);

            Hoisted.Add(SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(tempName)
                            .WithInitializer(SyntaxFactory.EqualsValueClause(
                                access.WithoutTrivia()))))));

            var incremented = SyntaxFactory.BinaryExpression(
                isIncrement ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression,
                SyntaxFactory.IdentifierName(tempName),
                SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1)));
            Hoisted.Add(SyntaxFactory.ExpressionStatement(
                _owner.BuildWriteForAccess(access, structName, incremented, original)));

            if (isPrefix)
            {
                return SyntaxFactory.ParenthesizedExpression(incremented).WithTriviaFrom(original);
            }

            return SyntaxFactory.IdentifierName(tempName).WithTriviaFrom(original);
        }

        /// <summary>
        /// switch 语句的提升边界：整棵 switch 子树里命中的值位写必须落在其
        /// <b>所属 section 的语句表内</b>（在原语句之前），否则 temp 声明会越出
        /// section 作用域（真实链：StringConcat 状态机 case 5 的连环赋值被提升到
        /// case 0，使用点 CS0103/声明点 CS0841）。
        /// </summary>
        public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
        {
            var expression = (ExpressionSyntax)Visit(node.Expression)!;
            var newSections = new List<SwitchSectionSyntax>();
            var changed = false;

            foreach (var section in node.Sections)
            {
                var newStatements = new List<StatementSyntax>();
                var sectionChanged = false;
                foreach (var statement in section.Statements)
                {
                    var inner = new ValuePositionWriteHoister(_owner);
                    var visitedStatement = (StatementSyntax)inner.Visit(statement)!;
                    if (!ReferenceEquals(visitedStatement, statement) || inner.Hoisted.Count > 0)
                        sectionChanged = true;
                    newStatements.AddRange(inner.Hoisted);
                    newStatements.Add(visitedStatement);
                }

                newSections.Add(sectionChanged
                    ? section.WithStatements(SyntaxFactory.List(newStatements))
                    : section);
                changed |= sectionChanged;
            }

            return changed
                ? node.Update(node.SwitchKeyword, node.OpenParenToken, expression,
                    node.CloseParenToken, node.OpenBraceToken,
                    SyntaxFactory.List(newSections), node.CloseBraceToken)
                : node;
        }
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        // Statement-level member writes only.
        if (node.Parent is not ExpressionStatementSyntax)
            return base.VisitAssignmentExpression(node);
        if (node.Left is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Name is not IdentifierNameSyntax fieldName
            || !_targetFieldNames.Contains(fieldName.Identifier.Text)
            || !IsModifiableLValue(memberAccess.Expression))
        {
            return base.VisitAssignmentExpression(node);
        }

        var fieldNameText = fieldName.Identifier.Text;
        var structName = TryGetTargetStructName(memberAccess.Expression);
        if (structName == null)
            return base.VisitAssignmentExpression(node);

        // Writes inside the struct's own instance members manage themselves
        // (WithXxx bodies, constructors, property accessors).
        if (memberAccess.Expression is not IdentifierNameSyntax
            && InsideOwnInstanceMember(node, structName))
        {
            return base.VisitAssignmentExpression(node);
        }
        if (memberAccess.Expression is ThisExpressionSyntax
            && !InsideTargetType(node, structName))
        {
            return base.VisitAssignmentExpression(node);
        }

        var receiver = memberAccess.Expression;
        ExpressionSyntax newValue;
        switch (node.Kind())
        {
            case SyntaxKind.SimpleAssignmentExpression:
                newValue = (ExpressionSyntax)Visit(node.Right)!;
                break;
            case SyntaxKind.AddAssignmentExpression:
            case SyntaxKind.SubtractAssignmentExpression:
            case SyntaxKind.MultiplyAssignmentExpression:
            case SyntaxKind.DivideAssignmentExpression:
            case SyntaxKind.ModuloAssignmentExpression:
            case SyntaxKind.AndAssignmentExpression:
            case SyntaxKind.ExclusiveOrAssignmentExpression:
            case SyntaxKind.OrAssignmentExpression:
            case SyntaxKind.LeftShiftAssignmentExpression:
            case SyntaxKind.RightShiftAssignmentExpression:
                var readAccess = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    receiver.WithoutTrivia(),
                    SyntaxFactory.IdentifierName(fieldNameText));
                var rhs = ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!);
                newValue = SyntaxFactory.BinaryExpression(
                    GetBinaryKind(node.Kind()), readAccess, rhs);
                break;
            default:
                return base.VisitAssignmentExpression(node);
        }

        return BuildWriteAssignment(structName, receiver, fieldNameText, newValue, node);
    }

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (node.Parent is ExpressionStatementSyntax
            && node.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression
            && node.Operand is MemberAccessExpressionSyntax memberAccess)
        {
            return RewriteStatementLevelUnary(memberAccess, node,
                node.Kind() == SyntaxKind.PreIncrementExpression)
                ?? base.VisitPrefixUnaryExpression(node);
        }
        return base.VisitPrefixUnaryExpression(node);
    }

    public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        if (node.Parent is ExpressionStatementSyntax
            && node.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression
            && node.Operand is MemberAccessExpressionSyntax memberAccess)
        {
            return RewriteStatementLevelUnary(memberAccess, node,
                node.Kind() == SyntaxKind.PostIncrementExpression)
                ?? base.VisitPostfixUnaryExpression(node);
        }
        return base.VisitPostfixUnaryExpression(node);
    }

    private SyntaxNode? RewriteStatementLevelUnary(
        MemberAccessExpressionSyntax memberAccess, ExpressionSyntax original, bool isIncrement)
    {
        if (memberAccess.Name is not IdentifierNameSyntax fieldName
            || !_targetFieldNames.Contains(fieldName.Identifier.Text)
            || !IsModifiableLValue(memberAccess.Expression))
        {
            return null;
        }

        var fieldNameText = fieldName.Identifier.Text;
        var structName = TryGetTargetStructName(memberAccess.Expression);
        if (structName == null)
            return null;
        if (memberAccess.Expression is not IdentifierNameSyntax
            && InsideOwnInstanceMember(original, structName))
        {
            return null;
        }
        if (memberAccess.Expression is ThisExpressionSyntax
            && !InsideTargetType(original, structName))
        {
            return null;
        }

        var receiver = memberAccess.Expression;
        var readAccess = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            receiver.WithoutTrivia(),
            SyntaxFactory.IdentifierName(fieldNameText));
        var newValue = SyntaxFactory.BinaryExpression(
            isIncrement ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression,
            readAccess,
            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1)));

        return BuildWriteAssignment(structName, receiver, fieldNameText, newValue, original);
    }

    /// <summary>
    /// Dispatches the write to the L3 constructor reconstruction when the target
    /// is a DataContainer struct, otherwise to the WithXxx chain shape.
    /// </summary>
    private AssignmentExpressionSyntax BuildWriteAssignment(
        string structName, ExpressionSyntax receiver, string memberName,
        ExpressionSyntax value, SyntaxNode triviaFrom)
        => _l3Structs.ContainsKey(structName)
            ? BuildL3ConstructorAssignment(structName, receiver, memberName, value, triviaFrom)
            : BuildWithWriteAssignment(receiver, memberName, value, triviaFrom);

    /// <summary>
    /// receiver.Member = value  ->  receiver = new S(p1: receiver.P1, ..., pm: value, ...)
    /// Every constructor parameter maps to its state property (case-insensitive,
    /// underscores stripped); the written member carries the new value and all
    /// other members are copied from the receiver. Non-state parameters
    /// (e.g. the <c>__cs2jTag</c> discriminator) receive <c>0</c>.
    /// </summary>
    private AssignmentExpressionSyntax BuildL3ConstructorAssignment(
        string structName, ExpressionSyntax receiver, string memberName,
        ExpressionSyntax value, SyntaxNode triviaFrom)
    {
        var arguments = new List<ArgumentSyntax>();
        var (_, ctorParams) = _l3Structs[structName];
        foreach (var parameterName in ctorParams)
        {
            ExpressionSyntax argumentValue;
            var mappedProperty = GetL3PropertyForParameter(structName, parameterName);
            if (mappedProperty == null)
            {
                argumentValue = SyntaxFactory.LiteralExpression(
                    SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0));
            }
            else if (mappedProperty == memberName)
            {
                argumentValue = value;
            }
            else
            {
                argumentValue = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    receiver.WithoutTrivia(),
                    SyntaxFactory.IdentifierName(mappedProperty));
            }

            arguments.Add(SyntaxFactory.Argument(argumentValue)
                .WithNameColon(SyntaxFactory.NameColon(
                    SyntaxFactory.IdentifierName(parameterName))));
        }

        // The cross-file call-site passes do not run the whitespace formatter, so
        // the keyword needs explicit trailing trivia: `newS()` lexes as a single
        // identifier, while `new S()` is an object creation.
        var newKeyword = SyntaxFactory.Token(SyntaxKind.NewKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);
        var creation = SyntaxFactory.ObjectCreationExpression(
            newKeyword,
            BuildReconstructedTypeSyntax(structName, receiver, triviaFrom),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)),
            initializer: null);

        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            receiver.WithoutTrivia(),
            creation).WithTriviaFrom(triviaFrom);
    }

    /// <summary>
    /// Type syntax for an L3 constructor reconstruction site. L3 structs may be
    /// declared in another namespace than the write site (e.g. the generated
    /// <c>global::Cs2jTuples.*</c> shapes), where the bare struct name does not
    /// bind (CS0246). Emit a fully qualified <c>global::</c> name when the
    /// struct's declaration container is not an enclosing lexical container of
    /// the site; keep the simple name otherwise. A using directive that would
    /// bring the name into scope is ignored — over-qualifying still compiles.
    /// </summary>
    private TypeSyntax BuildReconstructedTypeSyntax(
        string structName, ExpressionSyntax receiver, SyntaxNode site)
    {
        if (_semanticModel != null)
        {
            try
            {
                if (_semanticModel.GetTypeInfo(receiver).Type is INamedTypeSymbol namedType
                    && !IsDeclaredInEnclosingContainer(namedType, site))
                {
                    return SyntaxFactory.ParseTypeName(
                        namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                }
            }
            catch (ArgumentException)
            {
                // Synthesized node not present in the modeled tree — fall through
                // to the simple name.
            }
        }

        return SyntaxFactory.IdentifierName(structName);
    }

    /// <summary>
    /// True when <paramref name="type"/> is declared in one of the lexical
    /// containers enclosing <paramref name="site"/> (its namespace or an
    /// enclosing type), in which case its simple name binds at the site.
    /// </summary>
    private static bool IsDeclaredInEnclosingContainer(INamedTypeSymbol type, SyntaxNode site)
    {
        var symbolParts = GetQualifiedTypeName(type)
            .Split('.', StringSplitOptions.RemoveEmptyEntries);
        var containerLength = symbolParts.Length - 1; // drop the type's own name
        if (containerLength == 0)
            return true; // global-namespace types are visible from every scope

        var chain = new List<string>();
        for (var node = site.Parent; node != null; node = node.Parent)
        {
            switch (node)
            {
                case TypeDeclarationSyntax t:
                    chain.Add(t.Identifier.Text);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    chain.AddRange(ns.Name.ToString()
                        .Split('.', StringSplitOptions.RemoveEmptyEntries));
                    break;
            }
        }
        chain.Reverse();

        if (containerLength > chain.Count)
            return false;
        for (var i = 0; i < containerLength; i++)
        {
            if (!string.Equals(chain[i], symbolParts[i], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private string? GetL3PropertyForParameter(string structName, string parameterName)
    {
        var props = _l3Structs[structName].Props;
        var normalizedParameter = parameterName.TrimStart('_');
        return props.FirstOrDefault(p =>
            string.Equals(p.TrimStart('_'), normalizedParameter, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// receiver.Member = value  ->  receiver = receiver.WithMember(value)
    /// </summary>
    private static AssignmentExpressionSyntax BuildWithWriteAssignment(
        ExpressionSyntax receiver, string memberName, ExpressionSyntax value, SyntaxNode triviaFrom)
    {
        var withCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver.WithoutTrivia(),
                SyntaxFactory.IdentifierName("With" + memberName)),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(value))));

        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            receiver.WithoutTrivia(),
            withCall).WithTriviaFrom(triviaFrom);
    }

    private static SyntaxKind GetBinaryKind(SyntaxKind compoundKind)
        => compoundKind switch
        {
            SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
            SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
            SyntaxKind.MultiplyAssignmentExpression => SyntaxKind.MultiplyExpression,
            SyntaxKind.DivideAssignmentExpression => SyntaxKind.DivideExpression,
            SyntaxKind.ModuloAssignmentExpression => SyntaxKind.ModuloExpression,
            SyntaxKind.AndAssignmentExpression => SyntaxKind.BitwiseAndExpression,
            SyntaxKind.ExclusiveOrAssignmentExpression => SyntaxKind.ExclusiveOrExpression,
            SyntaxKind.OrAssignmentExpression => SyntaxKind.BitwiseOrExpression,
            SyntaxKind.LeftShiftAssignmentExpression => SyntaxKind.LeftShiftExpression,
            SyntaxKind.RightShiftAssignmentExpression => SyntaxKind.RightShiftExpression,
            _ => throw new ArgumentOutOfRangeException(nameof(compoundKind))
        };

    /// <summary>
    /// Parenthesize a binary RHS so compound-assignment precedence is preserved:
    /// x.F /= a * b must become x = x.WithF(x.F / (a * b)).
    /// </summary>
    private static ExpressionSyntax ParenthesizeIfBinary(ExpressionSyntax expr)
        => expr is BinaryExpressionSyntax ? SyntaxFactory.ParenthesizedExpression(expr) : expr;

    private static bool IsModifiableLValue(ExpressionSyntax expression)
        => expression switch
        {
            IdentifierNameSyntax => true,
            ThisExpressionSyntax => true,
            ElementAccessExpressionSyntax ea => IsModifiableLValue(ea.Expression),
            MemberAccessExpressionSyntax ma => IsModifiableLValue(ma.Expression),
            _ => false
        };

    private static bool InsideTargetType(SyntaxNode node, string structName)
    {
        var containingType = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        return containingType != null && containingType.Identifier.Text == structName;
    }

    private static bool InsideOwnInstanceMember(SyntaxNode node, string structName)
    {
        var containingType = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (containingType == null || containingType.Identifier.Text != structName)
            return false;

        var member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        return member switch
        {
            ConstructorDeclarationSyntax => true,
            MethodDeclarationSyntax m => !m.Modifiers.Any(x => x.IsKind(SyntaxKind.StaticKeyword)),
            PropertyDeclarationSyntax p => !p.Modifiers.Any(x => x.IsKind(SyntaxKind.StaticKeyword)),
            _ => false
        };
    }

    /// <summary>
    /// Resolves the receiver's type to a target struct name. Returns null when the
    /// receiver resolves to a non-target type (or cannot be resolved). Semantic
    /// model is the primary source; the prebuilt variable map is the fallback.
    /// </summary>
    private string? TryGetTargetStructName(ExpressionSyntax expression)
    {
        if (_semanticModel != null)
        {
            try
            {
                var type = _semanticModel.GetTypeInfo(expression).Type;
                if (type != null)
                {
                    if (type.TypeKind is TypeKind.Error or not TypeKind.Struct)
                        return null;

                    if (_qualifiedNames.Count > 0
                        && type is INamedTypeSymbol namedType)
                    {
                        return _qualifiedNames.TryGetValue(type.Name, out var names)
                               && names.Contains(GetQualifiedTypeName(namedType))
                            ? type.Name
                            : null;
                    }
                    return _structFields.ContainsKey(type.Name) ? type.Name : null;
                }
            }
            catch (ArgumentException)
            {
                // Synthesized node not present in the modeled tree — fall through.
            }
        }

        var rootId = GetRootIdentifier(expression);
        if (rootId != null
            && _variableTypes.TryGetValue(rootId.Identifier.Text, out var typeName)
            && _structFields.ContainsKey(typeName))
        {
            return typeName;
        }
        return null;
    }

    private static IdentifierNameSyntax? GetRootIdentifier(ExpressionSyntax expression)
        => expression switch
        {
            IdentifierNameSyntax id => id,
            MemberAccessExpressionSyntax ma => GetRootIdentifier(ma.Expression),
            ElementAccessExpressionSyntax ea => GetRootIdentifier(ea.Expression),
            _ => null
        };

    private static string GetTypeName(TypeSyntax type)
        => type switch
        {
            IdentifierNameSyntax id => id.Identifier.Text == "var" ? string.Empty : id.Identifier.Text,
            QualifiedNameSyntax qn => qn.Right.Identifier.Text,
            GenericNameSyntax gn => gn.Identifier.Text,
            _ => string.Empty
        };

    /// <summary>
    /// Qualified name from a resolved symbol: namespace + containing-type chain + name.
    /// </summary>
    internal static string GetQualifiedTypeName(INamedTypeSymbol type)
    {
        var parts = new List<string>();
        for (var t = (ITypeSymbol?)type; t is INamedTypeSymbol nts; t = nts.ContainingType)
            parts.Add(nts.Name);
        for (var ns = type.ContainingNamespace; ns != null && !ns.IsGlobalNamespace; ns = ns.ContainingNamespace)
            parts.Add(ns.Name);
        parts.Reverse();
        return string.Join(".", parts);
    }

    /// <summary>
    /// Qualified name from syntax: namespace + containing-type chain + name.
    /// </summary>
    public static string GetQualifiedStructName(StructDeclarationSyntax node)
    {
        var parts = new List<string>();
        for (var n = (SyntaxNode?)node; n != null; n = n.Parent)
        {
            if (n is TypeDeclarationSyntax t)
                parts.Add(t.Identifier.Text);
            else if (n is BaseNamespaceDeclarationSyntax ns)
                parts.Add(ns.Name.ToString());
        }
        parts.Reverse();
        return string.Join(".", parts);
    }
}
