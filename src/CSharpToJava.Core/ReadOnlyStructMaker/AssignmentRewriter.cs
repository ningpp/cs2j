using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

/// <summary>
/// Rewrites external field assignments to WithXxx method calls:
/// - obj.Field = value → obj = obj.WithField(value)
/// - obj.Field += value → obj = obj.WithField(obj.Field + value)
/// - obj.Field++ → obj = obj.WithField(obj.Field + 1)
/// - obj.Field-- → obj = obj.WithField(obj.Field - 1)
/// - new S { F = v } → new S(f: v) (when constructor exists)
///
/// Uses semantic model to verify that the receiver's type is actually one of the
/// target structs, preventing incorrect conversion of classes with same-named fields.
/// </summary>
public sealed class AssignmentRewriter : CSharpSyntaxRewriter
{
    private readonly Dictionary<string, HashSet<string>> _structFields = new(); // structName -> fieldNames
    private readonly HashSet<string> _targetFieldNames = new(); // all field names across all target structs
    private readonly SemanticModel? _semanticModel;
    private readonly Dictionary<string, string> _variableTypes = new(); // variableName -> typeName
    // struct NAME -> qualified names (namespace + containing-type chain). Same-named
    // structs declared inside different types (e.g. XmlTextWriter.Namespace vs
    // XmlWellFormedWriter.Namespace) must not be conflated: a receiver whose resolved
    // symbol is not one of the registered qualified names is NOT a target.
    private readonly Dictionary<string, HashSet<string>> _qualifiedNames = new(StringComparer.Ordinal);
    // L5 structs: properties are public get-only (not private fields with getXxx() getters).
    // Reads from these structs should use property access (obj.Prop), not getXxx().
    private readonly HashSet<string> _propertyAccessStructs = new();
    // Structs that actually have WithXxx methods — only these may be targets of
    // object-initializer-to-With-chain rewrites.
    private readonly HashSet<string> _withChainStructs = new();
    // L3 data-container structs (no WithXxx methods): external writes to their
    // get-only properties are reconstructed through the generated constructor:
    //   x.Prop = v  →  x = new S(p1: x.P1, ..., pj: v)
    // Key: struct name; value: public get-only property names and the constructor's
    // parameter names (in order).
    private readonly Dictionary<string, (HashSet<string> Props, List<string> CtorParams)> _l3Structs = new();

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
        if (qualifiedNames != null)
        {
            foreach (var kvp in qualifiedNames)
                _qualifiedNames[kvp.Key] = new HashSet<string>(kvp.Value, StringComparer.Ordinal);
        }
        if (propertyAccessStructs != null)
            _propertyAccessStructs.UnionWith(propertyAccessStructs);
        if (withChainStructs != null)
            _withChainStructs.UnionWith(withChainStructs);
        else
            _withChainStructs.UnionWith(structFields.Keys); // legacy behavior: assume all have With methods
        if (l3Structs != null)
        {
            foreach (var kvp in l3Structs)
                _l3Structs[kvp.Key] = kvp.Value;
        }
        // Collect all field names for quick lookup
        foreach (var fields in structFields.Values)
        {
            foreach (var field in fields)
            {
                _targetFieldNames.Add(field);
            }
        }
    }

    /// <summary>
    /// Builds a map of variable names to their declared types from the original syntax tree.
    /// This must be called BEFORE the tree is modified by other rewriters.
    /// </summary>
    public void BuildVariableTypeMap(SyntaxNode root)
    {
        _variableTypes.Clear();
        foreach (var node in root.DescendantNodes())
        {
            // Handle local variable declarations: Type x = ...; or var x = ...;
            if (node is LocalDeclarationStatementSyntax localDecl &&
                localDecl.Declaration.Variables.Count == 1)
            {
                var variable = localDecl.Declaration.Variables[0];
                var varTypeName = GetTypeName(localDecl.Declaration.Type);
                if (!string.IsNullOrEmpty(varTypeName))
                {
                    _variableTypes[variable.Identifier.Text] = varTypeName;
                }
                else if (variable.Initializer?.Value != null)
                {
                    // For 'var' declarations, try to get the type from the initializer
                    var initTypeName = GetTypeNameFromExpression(variable.Initializer.Value);
                    if (!string.IsNullOrEmpty(initTypeName))
                    {
                        _variableTypes[variable.Identifier.Text] = initTypeName;
                    }
                }
            }
            // Handle field declarations: Type fieldName;
            else if (node is FieldDeclarationSyntax fieldDecl &&
                     fieldDecl.Declaration.Variables.Count == 1)
            {
                var variable = fieldDecl.Declaration.Variables[0];
                var varTypeName = GetTypeName(fieldDecl.Declaration.Type);
                if (!string.IsNullOrEmpty(varTypeName))
                {
                    _variableTypes[variable.Identifier.Text] = varTypeName;
                }
            }
        }
    }

    private static string GetTypeNameFromExpression(ExpressionSyntax expr)
    {
        if (expr is ObjectCreationExpressionSyntax objCreate)
        {
            return GetTypeName(objCreate.Type);
        }
        return string.Empty;
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        // Tuple assignment to struct members: (a.b, c.d) = (x, y) — split into
        // sequential single assignments so each member write gets With-ified.
        if (node.Expression is AssignmentExpressionSyntax
            {
                Left: TupleExpressionSyntax tupleLeft,
                Right: TupleExpressionSyntax tupleRight
            } tupleAssign &&
            tupleLeft.Arguments.Count == tupleRight.Arguments.Count)
        {
            var statements = new List<StatementSyntax>();
            // Detect swap patterns: if any RHS expression references a LHS target,
            // hoist ALL RHS values into temp locals first to preserve simultaneous
            // assignment semantics (e.g. (a, b) = (b, a) must use temps).
            var leftTexts = tupleLeft.Arguments
                .Select(a => a.Expression.ToString())
                .ToHashSet();
            bool needsHoisting = false;
            for (var i = 0; i < tupleRight.Arguments.Count; i++)
            {
                var rightStr = tupleRight.Arguments[i].Expression.ToString();
                if (leftTexts.Any(lt => rightStr.Contains(lt)))
                {
                    needsHoisting = true;
                    break;
                }
            }
            if (needsHoisting)
            {
                for (var i = 0; i < tupleLeft.Arguments.Count; i++)
                {
                    var rightExpr = (ExpressionSyntax)Visit(tupleRight.Arguments[i].Expression)!;
                    var tempDecl = SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(
                            SyntaxFactory.IdentifierName("var").WithTrailingTrivia(SyntaxFactory.Whitespace(" ")))
                        .WithVariables(SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.VariableDeclarator($"_cs2jSwap{i}")
                                .WithInitializer(SyntaxFactory.EqualsValueClause(rightExpr)))));
                    statements.Add(tempDecl);
                }
                for (var i = 0; i < tupleLeft.Arguments.Count; i++)
                {
                    var tempIdent = SyntaxFactory.IdentifierName($"_cs2jSwap{i}");
                    var single = SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        tupleLeft.Arguments[i].Expression,
                        tempIdent);
                    var rewritten = (ExpressionSyntax)(VisitAssignmentExpression(single) ?? single);
                    statements.Add(SyntaxFactory.ExpressionStatement(rewritten));
                }
            }
            else
            {
                for (var i = 0; i < tupleLeft.Arguments.Count; i++)
                {
                    var rightExpr = (ExpressionSyntax)Visit(tupleRight.Arguments[i].Expression)!;
                    var single = SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        tupleLeft.Arguments[i].Expression,
                        rightExpr);
                    var rewritten = (ExpressionSyntax)(VisitAssignmentExpression(single) ?? single);
                    statements.Add(SyntaxFactory.ExpressionStatement(rewritten));
                }
            }
            return SyntaxFactory.Block(statements).WithTriviaFrom(node);
        }

        // Chained struct field assignment: a.x = a.y = value
        // In C#, `a.x = a.y = 1` assigns 1 to both fields (the inner assignment
        // returns the assigned value, not the struct). But after With-ification,
        // `a = a.Withy(1)` returns the struct, so nesting it inside
        // `a = a.Withx(a = a.Withy(1))` passes a struct where an int is expected.
        // Expand to two separate statements: first the inner assignment,
        // then the outer assignment using the same value.
        if (node.Expression is AssignmentExpressionSyntax chainedOuter &&
            chainedOuter.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
            chainedOuter.Right is AssignmentExpressionSyntax chainedInner &&
            chainedInner.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
            chainedInner.Left is MemberAccessExpressionSyntax)
        {
            // Recursively visit the inner assignment first (it may itself be chained)
            var innerStatement = SyntaxFactory.ExpressionStatement(chainedInner);
            var visitedInner = (StatementSyntax)VisitExpressionStatement(innerStatement)!;
            // Visit the outer assignment, replacing the inner assignment with its RHS value
            var outerWithRhsValue = SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                chainedOuter.Left,
                chainedInner.Right);
            var visitedOuter = (StatementSyntax)VisitExpressionStatement(
                SyntaxFactory.ExpressionStatement(outerWithRhsValue))!;
            // Combine: inner first (assigns the inner field), then outer (assigns the outer field)
            var statements = new List<StatementSyntax>();
            if (visitedInner is BlockSyntax innerBlock)
                statements.AddRange(innerBlock.Statements);
            else
                statements.Add(visitedInner);
            if (visitedOuter is BlockSyntax outerBlock)
                statements.AddRange(outerBlock.Statements);
            else
                statements.Add(visitedOuter);
            return SyntaxFactory.Block(statements).WithTriviaFrom(node);
        }

        return base.VisitExpressionStatement(node);
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        // `return this.field = expr;` or `return obj.field = expr;` inside migrated
        // methods: C# assignment returns the assigned value (field type), but after
        // With-ification `result = result.Withfield(expr)` returns the struct.
        // Expand to: { field_assignment; return expr; }
        if (node.Expression is AssignmentExpressionSyntax returnAssign &&
            returnAssign.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
            returnAssign.Left is MemberAccessExpressionSyntax)
        {
            // Visit the assignment (will With-ify if it targets a struct field)
            var visitedAssign = (ExpressionSyntax)VisitAssignmentExpression(returnAssign)!;
            // If it was rewritten (changed from original), expand into block
            if (!SyntaxFactory.AreEquivalent(returnAssign, visitedAssign))
            {
                var assignStatement = SyntaxFactory.ExpressionStatement(visitedAssign)
                    .WithLeadingTrivia(node.GetLeadingTrivia());
                var returnStatement = SyntaxFactory.ReturnStatement(returnAssign.Right)
                    .WithLeadingTrivia(SyntaxFactory.Whitespace("            "));
                return SyntaxFactory.Block(assignStatement, returnStatement)
                    .WithTriviaFrom(node);
            }
        }
        return base.VisitReturnStatement(node);
    }

    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        // `Type x = obj.field = value;` — C# assigns value to obj.field AND to x.
        // After With-ification `obj = obj.Withfield(value)` returns the struct,
        // not the field value. Expand to:
        //   { obj.field = value; Type x = value; }
        if (node.Declaration.Variables.Count == 1 &&
            node.Declaration.Variables[0].Initializer?.Value is AssignmentExpressionSyntax initAssign &&
            initAssign.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
            initAssign.Left is MemberAccessExpressionSyntax)
        {
            // Visit the assignment (will With-ify if it targets a struct field)
            var visitedAssign = (ExpressionSyntax)VisitAssignmentExpression(initAssign)!;
            // If it was rewritten (changed from original), expand into block
            if (!SyntaxFactory.AreEquivalent(initAssign, visitedAssign))
            {
                var assignStatement = SyntaxFactory.ExpressionStatement(visitedAssign)
                    .WithLeadingTrivia(node.GetLeadingTrivia());
                // Declare the variable with the RHS value (not the assignment result)
                var newDecl = SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(node.Declaration.Type)
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(node.Declaration.Variables[0].Identifier)
                            .WithInitializer(SyntaxFactory.EqualsValueClause(initAssign.Right)))))
                    .WithLeadingTrivia(SyntaxFactory.Whitespace("            "));
                return SyntaxFactory.Block(assignStatement, newDecl)
                    .WithTriviaFrom(node);
            }
        }
        return base.VisitLocalDeclarationStatement(node);
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        if (node.Left is not MemberAccessExpressionSyntax memberAccess)
            return base.VisitAssignmentExpression(node);

        if (memberAccess.Name is not IdentifierNameSyntax fieldName)
            return base.VisitAssignmentExpression(node);

        var fieldNameText = fieldName.Identifier.Text;

        // Quick check: field name must be in our target fields
        if (!_targetFieldNames.Contains(fieldNameText))
            return base.VisitAssignmentExpression(node);

        // Syntactic check: receiver must be a simple identifier or element access
        // (to ensure we're modifying a variable, not a property return value)
        if (!IsModifiableLValue(memberAccess.Expression))
            return base.VisitAssignmentExpression(node);

        // Semantic check: verify the receiver's type is actually one of our target structs
        var structName = TryGetTargetStructName(memberAccess.Expression);
        if (structName == null)
            return base.VisitAssignmentExpression(node); // Resolved to non-target type - don't rewrite
        if (structName == string.Empty)
        {
            // Type unresolvable (e.g., generic base class field). Use field-name fallback.
            structName = TryGetStructNameByFieldNameAny(fieldNameText);
            if (structName == null)
                return base.VisitAssignmentExpression(node);
        }

        // Don't convert inside non-static members of the struct itself (WithXxx method
        // bodies and migrated methods manage their own writes). STATIC members take
        // struct-typed parameters/locals and must be rewritten like external code.
        // Exception: writes through LOCAL variables (e.g. the `result` accumulator of
        // migrated L5 methods, or other struct-typed locals like `ArrayEnumerator ator`)
        // behave exactly like external writes and MUST be rewritten — the struct's own
        // migration only handles writes through `this`/`result` receiver chains, not
        // through arbitrary locals. `this.X = v` inside the struct's own members
        // (constructors, WithXxx helpers) stays untouched.
        if (memberAccess.Expression is not IdentifierNameSyntax &&
            InsideOwnInstanceMember(node, structName))
            return base.VisitAssignmentExpression(node);

        // A `this.X = ...` write inside a type that is NOT one of the target structs
        // must never be rewritten (the member belongs to that class, not a struct).
        // This guards against unresolvable receiver types falling back to field-name
        // guessing (e.g. a class field sharing a struct field's name).
        if (memberAccess.Expression is ThisExpressionSyntax &&
            !InsideTargetType(node, structName))
            return base.VisitAssignmentExpression(node);

        var receiver = memberAccess.Expression;

        // For compound assignments, the read of obj.Field should use the getter method.
        // L5 structs use property access (obj.Prop); L4 structs use getXxx().
        var getterAccess = CreateReadAccess(receiver, fieldNameText, structName);

        // Handle different assignment types
        ExpressionSyntax newValue;
        switch (node.Kind())
        {
            case SyntaxKind.SimpleAssignmentExpression:
                // Visit the right side to convert any field reads within it
                newValue = (ExpressionSyntax)Visit(node.Right)!;
                break;
            case SyntaxKind.AddAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.AddExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            case SyntaxKind.SubtractAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.SubtractExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            case SyntaxKind.MultiplyAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.MultiplyExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            case SyntaxKind.DivideAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.DivideExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            case SyntaxKind.ModuloAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.ModuloExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            case SyntaxKind.AndAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.BitwiseAndExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            case SyntaxKind.ExclusiveOrAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.ExclusiveOrExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            case SyntaxKind.OrAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.BitwiseOrExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            case SyntaxKind.LeftShiftAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.LeftShiftExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            case SyntaxKind.RightShiftAssignmentExpression:
                newValue = SyntaxFactory.BinaryExpression(
                    SyntaxKind.RightShiftExpression, getterAccess, ParenthesizeIfBinary((ExpressionSyntax)Visit(node.Right)!));
                break;
            default:
                return base.VisitAssignmentExpression(node);
        }

        // Generate: receiver = receiver.WithField(newValue)
        //   or, for L3 data-container structs: receiver = new S(p1: receiver.P1, ..., pj: newValue)
        // Then lift through member-access receivers (receiver is a.b → a = a.WithB(...))
        // so the full chain a.b.c = v becomes a = a.WithB(a.b.WithC(v)).
        return BuildWriteAssignment(memberAccess.Expression, fieldNameText, structName, newValue, node);
    }

    /// <summary>
    /// Builds the rewritten assignment for <c>receiver.Member = value</c>. Handles the
    /// single-level write and then lifts through member-access receivers so nested
    /// writes (<c>a.b.c = v</c>) become a = a.WithB(a.b.WithC(v)) — the innermost
    /// WithXxx call is produced first and each outer level wraps it.
    /// </summary>
    private AssignmentExpressionSyntax BuildWriteAssignment(
        ExpressionSyntax receiver,
        string memberName,
        string structName,
        ExpressionSyntax value,
        SyntaxNode triviaFrom)
    {
        ExpressionSyntax valueExpr;
        if (_withChainStructs.Contains(structName))
        {
            valueExpr = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    receiver.WithoutTrivia(),
                    SyntaxFactory.IdentifierName("With" + memberName)),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(value))));
        }
        else if (_l3Structs.TryGetValue(structName, out var l3Info))
        {
            valueExpr = BuildL3Reconstruction(receiver, memberName, value, structName, l3Info);
            if (valueExpr == null)
                return (AssignmentExpressionSyntax)base.VisitAssignmentExpression(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression, receiver, value));
        }
        else
        {
            // Unknown struct shape — leave the assignment untouched.
            return (AssignmentExpressionSyntax)base.VisitAssignmentExpression(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression, receiver, value));
        }

        // Lift through member-access receivers.
        var currentReceiver = receiver;
        while (currentReceiver is MemberAccessExpressionSyntax outerMa &&
               outerMa.Name is IdentifierNameSyntax outerId &&
               _targetFieldNames.Contains(outerId.Identifier.Text) &&
               IsModifiableLValue(outerMa.Expression))
        {
            var outerStruct = TryGetTargetStructName(outerMa.Expression);
            if (outerStruct == null || outerStruct == string.Empty)
                break;
            if (_withChainStructs.Contains(outerStruct))
            {
                valueExpr = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        outerMa.Expression.WithoutTrivia(),
                        SyntaxFactory.IdentifierName("With" + outerId.Identifier.Text)),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(valueExpr))));
            }
            else if (_l3Structs.TryGetValue(outerStruct, out var outerL3))
            {
                var reconstructed = BuildL3Reconstruction(outerMa.Expression, outerId.Identifier.Text, valueExpr, outerStruct, outerL3);
                if (reconstructed == null)
                    break;
                valueExpr = reconstructed;
            }
            else
            {
                break;
            }
            currentReceiver = outerMa.Expression;
        }

        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            currentReceiver.WithoutTrivia(),
            valueExpr).WithTriviaFrom(triviaFrom);
    }

    /// <summary>
    /// Rebuilds an L3 struct instance through its constructor:
    /// <c>x = new S(p1: x.P1, ..., pj: value, ...)</c>. Every constructor parameter is
    /// mapped to the matching public property (case-insensitive, leading underscores
    /// stripped); the written member's parameter receives the new value. Returns null
    /// when any parameter has no readable property.
    /// </summary>
    private ExpressionSyntax? BuildL3Reconstruction(
        ExpressionSyntax receiver,
        string memberName,
        ExpressionSyntax value,
        string structName,
        (HashSet<string> Props, List<string> CtorParams) info)
    {
        var arguments = new List<ArgumentSyntax>();
        foreach (var param in info.CtorParams)
        {
            var normalizedParam = param.TrimStart('_');
            string? readProp = null;
            foreach (var prop in info.Props)
            {
                if (string.Equals(prop.TrimStart('_'), normalizedParam, StringComparison.OrdinalIgnoreCase))
                {
                    readProp = prop;
                    break;
                }
            }
            if (readProp == null)
                return null;

            ExpressionSyntax argValue;
            if (readProp == memberName)
            {
                argValue = value;
            }
            else
            {
                argValue = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    receiver.WithoutTrivia(),
                    SyntaxFactory.IdentifierName(readProp));
            }
            arguments.Add(SyntaxFactory.Argument(argValue)
                .WithNameColon(SyntaxFactory.NameColon(
                    SyntaxFactory.IdentifierName(param))));
        }

        return SyntaxFactory.ObjectCreationExpression(
                SyntaxFactory.Token(SyntaxKind.NewKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.IdentifierName(structName),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)),
                initializer: null);
    }

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (node.Kind() is not (SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression))
            return base.VisitPrefixUnaryExpression(node);

        if (node.Operand is not MemberAccessExpressionSyntax memberAccess)
            return base.VisitPrefixUnaryExpression(node);

        // Statement-level mutation: rewrite in place to a WithXxx write-back.
        if (node.Parent is ExpressionStatementSyntax)
        {
            var result = HandleUnaryMutation(memberAccess, node, node.Kind() == SyntaxKind.PreIncrementExpression);
            // HandleUnaryMutation returns null when checks fail; fall back to base visit to avoid null nodes
            return result ?? base.VisitPrefixUnaryExpression(node);
        }

        // Nested in a larger expression (initializer, argument, condition, ...):
        // a bare assignment cannot substitute the unary's VALUE semantics, so the
        // block-level hoister rewrites these into temps + a WithXxx statement.
        return node;
    }

    public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        if (node.Kind() is not (SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression))
            return base.VisitPostfixUnaryExpression(node);

        if (node.Operand is not MemberAccessExpressionSyntax memberAccess)
            return base.VisitPostfixUnaryExpression(node);

        // Statement-level mutation: rewrite in place to a WithXxx write-back.
        if (node.Parent is ExpressionStatementSyntax)
        {
            var result = HandleUnaryMutation(memberAccess, node, node.Kind() == SyntaxKind.PostIncrementExpression);
            // HandleUnaryMutation returns null when checks fail; fall back to base visit to avoid null nodes
            return result ?? base.VisitPostfixUnaryExpression(node);
        }

        // Nested in a larger expression — handled by the block-level hoister.
        return node;
    }

    /// <summary>
    /// Hoists target-struct unary mutations nested inside larger expressions
    /// (<c>int t = x.f++;</c>, <c>M(x.f++)</c>) into temps plus a WithXxx write-back
    /// statement, preserving the unary's VALUE semantics:
    /// <code>
    /// var __cs2jOldf_0 = x.f;
    /// x = x.Withf(__cs2jOldf_0 + 1);
    /// int t = __cs2jOldf_0 + 1;   // postfix value = old
    /// </code>
    /// </summary>
    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        node = (BlockSyntax)base.VisitBlock(node)!;

        var newStatements = new List<StatementSyntax>();
        var changed = false;
        foreach (var statement in node.Statements)
        {
            // Flatten nested blocks produced by VisitLocalDeclarationStatement /
            // VisitExpressionStatement / VisitReturnStatement when they split
            // chained assignments. In switch-case contexts the inner block would
            // create a new scope, hiding locals from subsequent statements.
            if (statement is BlockSyntax innerBlock)
            {
                newStatements.AddRange(innerBlock.Statements);
                changed = true;
                continue;
            }

            var hoister = new UnaryHoister(this);
            var rewrittenStatement = (StatementSyntax)hoister.Visit(statement)!;
            if (!ReferenceEquals(rewrittenStatement, statement) || hoister.Hoisted.Count > 0)
                changed = true;
            newStatements.AddRange(hoister.Hoisted);
            newStatements.Add(rewrittenStatement);
        }
        return changed ? node.WithStatements(SyntaxFactory.List(newStatements)) : node;
    }

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
    {
        node = (SwitchSectionSyntax)base.VisitSwitchSection(node)!;

        // Flatten nested blocks produced by VisitLocalDeclarationStatement /
        // VisitExpressionStatement / VisitReturnStatement in switch cases,
        // so local variables stay visible to subsequent statements in the case.
        // BUT: do NOT flatten a single top-level BlockSyntax that wraps the
        // entire case body — that block provides variable scoping in C# and
        // removing it would cause duplicate variable declarations across
        // sibling cases that declare the same local names (e.g. 'type').
        var newStatements = new List<StatementSyntax>();
        var changed = false;
        // Skip flattening when the section has exactly one statement that is
        // a BlockSyntax — this is the original C# case body block.
        var isSingleBlockCase = node.Statements.Count == 1
            && node.Statements[0] is BlockSyntax;
        foreach (var statement in node.Statements)
        {
            if (!isSingleBlockCase && statement is BlockSyntax innerBlock)
            {
                newStatements.AddRange(innerBlock.Statements);
                changed = true;
            }
            else
            {
                newStatements.Add(statement);
            }
        }
        return changed ? node.WithStatements(SyntaxFactory.List(newStatements)) : node;
    }

    /// <summary>
    /// Finds target-struct unary mutations that need the block-level hoist. Skips
    /// statement-root mutations (handled in place) and mutations inside loop headers
    /// (evaluated per iteration — cannot be hoisted before the loop).
    /// </summary>
    private sealed class UnaryHoister : CSharpSyntaxRewriter
    {
        private readonly AssignmentRewriter _parent;
        internal readonly List<StatementSyntax> Hoisted = new();

        public UnaryHoister(AssignmentRewriter parent)
        {
            _parent = parent;
        }

        public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
        {
            if (node.Kind() is not (SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression))
                return base.VisitPostfixUnaryExpression(node);
            if (node.Parent is ExpressionStatementSyntax)
                return node;
            if (!CanHoist(node))
                return base.VisitPostfixUnaryExpression(node);
            var hoisted = TryHoist(node, node.Operand, node.Kind() == SyntaxKind.PostIncrementExpression, isPrefix: false);
            return hoisted ?? base.VisitPostfixUnaryExpression(node);
        }

        public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
        {
            if (node.Kind() is not (SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression))
                return base.VisitPrefixUnaryExpression(node);
            if (node.Parent is ExpressionStatementSyntax)
                return node;
            if (!CanHoist(node))
                return base.VisitPrefixUnaryExpression(node);
            var hoisted = TryHoist(node, node.Operand, node.Kind() == SyntaxKind.PreIncrementExpression, isPrefix: true);
            return hoisted ?? base.VisitPrefixUnaryExpression(node);
        }

        /// <summary>
        /// The unary must not sit inside a loop header (condition/declaration/
        /// incrementor — evaluated repeatedly) or an anonymous function (the hoisted
        /// statements would land outside the closure's scope).
        /// </summary>
        private static bool CanHoist(SyntaxNode node)
        {
            for (var n = node.Parent; n != null; n = n.Parent)
            {
                switch (n)
                {
                    case ExpressionStatementSyntax or BlockSyntax:
                        return true;
                    case ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax:
                        return false;
                    case AnonymousFunctionExpressionSyntax or QueryExpressionSyntax:
                        return false;
                    case StatementSyntax or MemberDeclarationSyntax:
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Hoists a single unary mutation into a temp + WithXxx write-back. The unary
        /// node is replaced by the temp (postfix: old value) or a parenthesized
        /// <c>temp ± 1</c> (prefix: new value). Returns null when the target does not
        /// resolve — the caller falls back to plain visitation.
        /// </summary>
        private SyntaxNode? TryHoist(ExpressionSyntax unaryNode, ExpressionSyntax operand,
            bool isIncrement, bool isPrefix)
        {
            if (operand is not MemberAccessExpressionSyntax memberAccess)
                return null;
            if (!_parent.TryResolveUnaryTarget(memberAccess, unaryNode, out var structName, out var fieldName))
                return null;

            var tempName = "__cs2jOld" + char.ToUpperInvariant(fieldName[0]) + fieldName.Substring(1) + "_" + _parent.NextUnaryTempName();

            // var __cs2jOldf_0 = x.f;  (L4: x.getf(); L5: x.f)
            Hoisted.Add(SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var")
                        .WithTrailingTrivia(SyntaxFactory.Space))
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(tempName)
                            .WithInitializer(SyntaxFactory.EqualsValueClause(
                                _parent.CreateReadAccess(memberAccess.Expression, fieldName, structName)))))));

            // x = x.Withf(__cs2jOldf_0 + 1);
            var write = _parent.BuildWriteAssignment(memberAccess.Expression, fieldName, structName,
                SyntaxFactory.BinaryExpression(
                    isIncrement ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression,
                    SyntaxFactory.IdentifierName(tempName),
                    SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1))),
                unaryNode);
            Hoisted.Add(SyntaxFactory.ExpressionStatement(write));

            if (isPrefix)
            {
                return SyntaxFactory.ParenthesizedExpression(
                    SyntaxFactory.BinaryExpression(
                        isIncrement ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression,
                        SyntaxFactory.IdentifierName(tempName),
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1))));
            }

            // Postfix: the unary's value is the OLD field value.
            return SyntaxFactory.IdentifierName(tempName);
        }
    }

    private int _unaryTempCounter;

    internal int NextUnaryTempName() => _unaryTempCounter++;

    /// <summary>
    /// Converts read accesses to converted struct fields: obj.Field → obj.getField().
    /// Write accesses are handled by VisitAssignmentExpression/VisitPrefixUnaryExpression/VisitPostfixUnaryExpression
    /// which return without calling base, so their children are not visited here.
    /// </summary>
    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        // Only handle simple member access (obj.Field)
        if (node.Name is not IdentifierNameSyntax fieldName)
            return base.VisitMemberAccessExpression(node);

        var fieldNameText = fieldName.Identifier.Text;

        // Quick check: field name must be in our target fields
        if (!_targetFieldNames.Contains(fieldNameText))
            return base.VisitMemberAccessExpression(node);

        // Semantic check: verify the receiver's type is actually one of our target structs
        var structName = TryGetTargetStructName(node.Expression);
        if (structName == null || structName == string.Empty)
        {
            // For reads, don't use field-name fallback (too risky for static access like Direction.South)
            // Only try syntactic parameter check for simple identifiers
            if (node.Expression is IdentifierNameSyntax receiverId)
                structName = TryGetParameterStructType(receiverId.Identifier.Text, node);
            if (structName == null || structName == string.Empty)
                return base.VisitMemberAccessExpression(node);
        }

        // Don't convert if this is inside the struct itself (struct can access its own private fields)
        var containingStruct = node.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
        if (containingStruct != null && _structFields.ContainsKey(containingStruct.Identifier.Text))
            return base.VisitMemberAccessExpression(node);

        // L5 structs have public get-only properties — reads should stay as property access (obj.Prop),
        // not be rewritten to getXxx() which doesn't exist.
        if (_propertyAccessStructs.Contains(structName))
            return base.VisitMemberAccessExpression(node);

        // L4 structs: convert obj.Field → obj.getField()
        var getterName = JavaNaming.GetPropertyGetterName(fieldNameText);
        var getterCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                (ExpressionSyntax)Visit(node.Expression)!,
                SyntaxFactory.IdentifierName(getterName)),
            SyntaxFactory.ArgumentList())
            .WithTriviaFrom(node);

        return getterCall;
    }

    /// <summary>
    /// Syntactic fallback: checks if a variable name is a parameter of a target struct type
    /// by looking at the enclosing method's parameter list. Returns the struct name or null.
    /// </summary>
    private string? TryGetParameterStructType(string varName, SyntaxNode node)
    {
        var enclosingMethod = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (enclosingMethod != null)
        {
            foreach (var param in enclosingMethod.ParameterList.Parameters)
            {
                if (param.Identifier.Text == varName && param.Type != null)
                {
                    var typeName = GetTypeName(param.Type);
                    if (!string.IsNullOrEmpty(typeName) && _structFields.ContainsKey(typeName))
                        return typeName;
                }
            }
        }

        // Also check enclosing constructor
        var enclosingCtor = node.Ancestors().OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        if (enclosingCtor != null)
        {
            foreach (var param in enclosingCtor.ParameterList.Parameters)
            {
                if (param.Identifier.Text == varName && param.Type != null)
                {
                    var typeName = GetTypeName(param.Type);
                    if (!string.IsNullOrEmpty(typeName) && _structFields.ContainsKey(typeName))
                        return typeName;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a read access expression for a struct field/property.
    /// L4 structs: obj.getField() (private field with getter method).
    /// L5 structs: obj.Field (public get-only property, no getter method).
    /// </summary>
    private ExpressionSyntax CreateReadAccess(ExpressionSyntax receiver, string fieldName, string structName)
    {
        if (_propertyAccessStructs.Contains(structName))
        {
            // L5: property access
            return SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver.WithoutTrivia(),
                SyntaxFactory.IdentifierName(fieldName));
        }

        // L4: getter method call
        var getterName = "get" + fieldName;
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver.WithoutTrivia(),
                SyntaxFactory.IdentifierName(getterName)),
            SyntaxFactory.ArgumentList());
    }

    /// <summary>
    /// Shared target resolution for unary mutations on struct fields. Returns the
    /// struct name and field name when the mutation targets a converted struct.
    /// </summary>
    private bool TryResolveUnaryTarget(MemberAccessExpressionSyntax memberAccess, SyntaxNode originalNode,
        out string structName, out string fieldName)
    {
        structName = null!;
        fieldName = null!;

        if (memberAccess.Name is not IdentifierNameSyntax fieldNameNode)
            return false;
        fieldName = fieldNameNode.Identifier.Text;

        // Quick check: field name must be in our target fields
        if (!_targetFieldNames.Contains(fieldName))
            return false;

        // Syntactic check: receiver must be a simple identifier or element access
        if (!IsModifiableLValue(memberAccess.Expression))
            return false;

        // Semantic check: verify the receiver's type is actually one of our target structs
        var resolved = TryGetTargetStructName(memberAccess.Expression);
        if (resolved == null)
            return false; // Resolved to non-target type - don't rewrite
        if (resolved == string.Empty)
        {
            // Type unresolvable. Use field-name fallback.
            resolved = TryGetStructNameByFieldNameAny(fieldName);
            if (resolved == null)
                return false;
        }
        structName = resolved;

        // Skip non-static members of the struct itself (see VisitAssignmentExpression).
        if (memberAccess.Expression is not IdentifierNameSyntax &&
            InsideOwnInstanceMember(originalNode, structName))
            return false;

        // `this.X` inside a non-target type is never a struct field write.
        if (memberAccess.Expression is ThisExpressionSyntax &&
            !InsideTargetType(originalNode, structName))
            return false;

        return true;
    }

    private SyntaxNode? HandleUnaryMutation(MemberAccessExpressionSyntax memberAccess,
        ExpressionSyntax originalNode, bool isIncrement)
    {
        if (!TryResolveUnaryTarget(memberAccess, originalNode, out var structName, out var fieldNameText))
            return null;

        var receiver = memberAccess.Expression;

        // obj.Field++ → obj = obj.WithField(obj.getField() + 1)
        // L5 structs use property access (obj.Prop); L4 structs use getXxx().
        var getterAccess = CreateReadAccess(receiver, fieldNameText, structName);

        var increment = SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal(1));

        var newValue = SyntaxFactory.BinaryExpression(
            isIncrement ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression,
            getterAccess,
            increment);

        return BuildWriteAssignment(receiver, fieldNameText, structName, newValue, originalNode);
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        if (node.Initializer == null || !node.Initializer.Expressions.Any())
            return base.VisitObjectCreationExpression(node);

        var typeName = GetTypeName(node.Type);
        if (!_structFields.ContainsKey(typeName))
            return base.VisitObjectCreationExpression(node);

        return RewriteObjectInitializer(typeName, node.Initializer, node)
            ?? base.VisitObjectCreationExpression(node);
    }

    /// <summary>
    /// Handles target-typed `new() { F = v }` initializers. The target type is
    /// resolved semantically; when unresolvable the initializer is left untouched.
    /// </summary>
    public override SyntaxNode? VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        if (node.Initializer == null || !node.Initializer.Expressions.Any())
            return base.VisitImplicitObjectCreationExpression(node);

        string? typeName = null;
        try
        {
            typeName = _semanticModel?.GetTypeInfo(node).Type?.Name;
        }
        catch (ArgumentException)
        {
            // Node synthesized by an earlier pass — not in the modeled tree.
        }
        if (typeName == null || !_structFields.ContainsKey(typeName))
            return base.VisitImplicitObjectCreationExpression(node);

        var rewritten = RewriteObjectInitializer(typeName, node.Initializer, node);
        return rewritten == null ? base.VisitImplicitObjectCreationExpression(node) : rewritten;
    }

    private ExpressionSyntax? RewriteObjectInitializer(
        string typeName,
        InitializerExpressionSyntax initializer,
        ExpressionSyntax node)
    {
        var fields = _structFields[typeName];
        var initAssignments = initializer.Expressions
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is IdentifierNameSyntax id && fields.Contains(id.Identifier.Text))
            .ToList();
        if (initAssignments.Count == 0)
            return null;

        // Only rewrite initializers whose members ALL have a matching entry; partial
        // initializers rely on default values and are handled by the default ctor + With chain.

        // Prefer a With-method chain: new S { F = v } → new S().WithF(v)
        // This is robust regardless of constructor parameter names (CS1739-safe) and
        // keeps default values for members not mentioned in the initializer.
        // Only for structs that actually have WithXxx methods.
        if (_withChainStructs.Contains(typeName) && _propertyAccessStructs.Contains(typeName))
        {
            ExpressionSyntax expr = node is ObjectCreationExpressionSyntax oc
                ? oc.WithInitializer(null).WithArgumentList(SyntaxFactory.ArgumentList())
                : SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.Token(SyntaxKind.NewKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.IdentifierName(typeName),
                    SyntaxFactory.ArgumentList(),
                    initializer: null);
            foreach (var assignment in initAssignments)
            {
                var memberName = ((IdentifierNameSyntax)assignment.Left).Identifier.Text;
                expr = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        expr,
                        SyntaxFactory.IdentifierName("With" + memberName)),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument((ExpressionSyntax)Visit(assignment.Right)!))));
            }
            return expr.WithTriviaFrom(node);
        }

        // Legacy path (L3/L4 structs without WithXxx methods): convert to a
        // named-argument constructor call. PARTIAL initializers (members omitted)
        // are completed with `param: default` — the generated L3 constructors
        // require every storage parameter, and unmentioned members keep their
        // default (zero) values, matching the struct initializer semantics.
        var arguments = new List<ArgumentSyntax>();
        var mentioned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var expr in initializer.Expressions.OfType<AssignmentExpressionSyntax>())
        {
            if (expr.Left is IdentifierNameSyntax id && fields.Contains(id.Identifier.Text))
            {
                mentioned.Add(id.Identifier.Text);
                var paramName = ToCamelCase(id.Identifier.Text);
                // Visit the value so NESTED object initializers of other converted
                // structs (e.g. new JobStruct { Title = ... } inside EmployeeStruct)
                // are rewritten recursively.
                arguments.Add(SyntaxFactory.Argument((ExpressionSyntax)Visit(expr.Right)!)
                    .WithNameColon(SyntaxFactory.NameColon(
                        SyntaxFactory.IdentifierName(paramName))));
            }
        }

        // Complete partial initializers with defaults for every other storage member.
        foreach (var field in fields)
        {
            if (mentioned.Contains(field)) continue;
            var paramName = ToCamelCase(field);
            if (arguments.Any(a => a.NameColon?.Name.Identifier.Text == paramName)) continue;
            arguments.Add(SyntaxFactory.Argument(
                    SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression))
                .WithNameColon(SyntaxFactory.NameColon(
                    SyntaxFactory.IdentifierName(paramName))));
        }

        var argumentList = SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments));
        if (node is ObjectCreationExpressionSyntax objectCreation)
        {
            return objectCreation.WithInitializer(null)
                .WithArgumentList(argumentList);
        }
        // Implicit object creation: new() { ... } — rebuild as an explicit creation
        // so the constructor arguments are applied.
        return SyntaxFactory.ObjectCreationExpression(
                SyntaxFactory.Token(SyntaxKind.NewKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.IdentifierName(typeName),
                argumentList,
                initializer: null)
            .WithTriviaFrom(node);
    }

    /// <summary>
    /// True when the enclosing type declaration (class/struct) is the given target
    /// struct. Used to protect `this.X` writes inside unrelated classes.
    /// </summary>
    private static bool InsideTargetType(SyntaxNode node, string structName)
    {
        var containingType = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        return containingType != null && containingType.Identifier.Text == structName;
    }

    /// <summary>
    /// True when the node sits inside a NON-STATIC member (method/property/constructor)
    /// of the target struct itself. Writes there are managed by the struct's own
    /// migration (WithXxx bodies, ctor backing-field rewrites). Static members operate
    /// on struct-typed parameters/locals and behave like external code.
    /// </summary>
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
    /// Checks if the expression is a modifiable l-value (simple variable or array element).
    /// </summary>
    private static bool IsModifiableLValue(ExpressionSyntax expression)
    {
        return expression switch
        {
            IdentifierNameSyntax => true,
            ThisExpressionSyntax => true,
            ElementAccessExpressionSyntax ea => IsModifiableLValue(ea.Expression),
            MemberAccessExpressionSyntax ma => IsModifiableLValue(ma.Expression),
            _ => false
        };
    }

    /// <summary>
    /// Wraps a BinaryExpression in parentheses to preserve C# compound-assignment semantics.
    /// For example, `c.X /= 2.0 * (mb - ma)` means `c.X = c.X / (2.0 * (mb - ma))`.
    /// Without parenthesization, the generated `c.getX() / 2.0 * (mb - ma)` would parse
    /// as `(c.getX() / 2.0) * (mb - ma)` — changing the semantics.
    /// </summary>
    private static ExpressionSyntax ParenthesizeIfBinary(ExpressionSyntax expr)
    {
        if (expr is BinaryExpressionSyntax)
            return SyntaxFactory.ParenthesizedExpression(expr);
        return expr;
    }

    private static string GetTypeName(TypeSyntax type)
    {
        return type switch
        {
            IdentifierNameSyntax id => id.Identifier.Text == "var" ? string.Empty : id.Identifier.Text,
            QualifiedNameSyntax qn => qn.Right.Identifier.Text,
            GenericNameSyntax gn => gn.Identifier.Text,
            _ => string.Empty
        };
    }

    /// <summary>
    /// Qualified name of a struct from its resolved symbol: namespace + containing-type
    /// chain + own name (e.g. "dotnet.xml.core.XmlTextWriter.Namespace"). Used to
    /// disambiguate same-named structs declared inside different types.
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
    /// Qualified name of a struct declaration from its syntax: namespace (file-scoped
    /// or block-scoped) + containing-type chain + own name. Mirrors
    /// <see cref="GetQualifiedTypeName(INamedTypeSymbol)"/> for the syntax side.
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

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// Resolves the target struct name for an expression, or null if not a target struct.
    /// Used to determine whether field/property assignments should be converted to With* calls.
    /// This prevents incorrect conversion when a class has fields with the same names as a struct.
    /// Returns: struct name if target, null if resolved to non-target, empty string if unresolvable.
    /// </summary>
    private string? TryGetTargetStructName(ExpressionSyntax expression)
    {
        // Use semantic model FIRST (handles using aliases like P2 -> Point correctly)
        if (_semanticModel != null)
        {
            try
            {
                var typeInfo = _semanticModel.GetTypeInfo(expression);
                var type = typeInfo.Type;

                if (type != null)
                {
                    // An error type means the receiver's type failed to bind, typically an
                    // external class (TrackBar, Ellipse, ...) whose assembly is not part of
                    // the preprocessing compilation. Converted structs are always declared
                    // in this same compilation, so an error type can never be a target
                    // struct. Treat it as resolved-to-non-target instead of falling back to
                    // field-name guessing, which would wrongly rewrite e.g.
                    // `trackBar.Value = ...` into `trackBar = trackBar.WithValue(...)`.
                    if (type.TypeKind == TypeKind.Error)
                        return null;

                    // Require an actual struct — a class with the same name as a
                    // converted struct (e.g. a test helper `Person` class next to a
                    // `Person` struct) must never be rewritten.
                    if (type.TypeKind != TypeKind.Struct)
                        return null;
                    // Qualified-name check: when the targets were registered with
                    // their namespace/containing-type chain, a receiver resolving to a
                    // DIFFERENT same-named struct (e.g. XmlWellFormedWriter.Namespace
                    // vs the converted XmlTextWriter.Namespace) must not be rewritten.
                    if (_qualifiedNames.Count > 0)
                    {
                        if (type is INamedTypeSymbol namedType &&
                            _qualifiedNames.TryGetValue(type.Name, out var names) &&
                            names.Contains(GetQualifiedTypeName(namedType)))
                            return type.Name;
                        return null;
                    }
                    if (_structFields.ContainsKey(type.Name))
                        return type.Name;
                    return null; // Resolved to non-target type
                }
            }
            catch (ArgumentException)
            {
                // Node not in syntax tree - fall through
            }
        }

        // Fallback: variable type map (fast path, but doesn't resolve aliases)
        if (expression is IdentifierNameSyntax simpleId)
        {
            var varName = simpleId.Identifier.Text;
            if (_variableTypes.TryGetValue(varName, out var typeName))
            {
                if (_structFields.ContainsKey(typeName))
                    return typeName;
                // Don't return null here - the type map might have an alias.
                // Fall through to unresolvable.
            }
        }

        // Fallback: try root identifier
        var identifierName = GetRootIdentifier(expression);
        if (identifierName != null)
        {
            var rootVarName = identifierName.Identifier.Text;
            if (_variableTypes.TryGetValue(rootVarName, out var rootTypeName))
            {
                if (_structFields.ContainsKey(rootTypeName))
                    return rootTypeName;
                // The root variable's type IS known and it is not a target struct
                // (e.g. a class with same-named fields). Never fall back to
                // field-name guessing in this case.
                return null;
            }
        }

        // Type truly unresolvable
        return string.Empty;
    }

    /// <summary>
    /// Gets the root identifier name from an expression chain.
    /// For "info.MoreInfo.Path", returns the "info" identifier.
    /// For "o.Path", returns the "o" identifier.
    /// </summary>
    private static IdentifierNameSyntax? GetRootIdentifier(ExpressionSyntax expression)
    {
        return expression switch
        {
            IdentifierNameSyntax id => id,
            MemberAccessExpressionSyntax ma => GetRootIdentifier(ma.Expression),
            ElementAccessExpressionSyntax ea => GetRootIdentifier(ea.Expression),
            _ => null
        };
    }

    /// <summary>
    /// Last-resort fallback for WRITE operations: finds a target struct containing the field name.
    /// No length restriction (writes MUST be rewritten for readonly correctness).
    /// Still requires the field to be unique to one target struct to minimize false positives.
    /// </summary>
    private string? TryGetStructNameByFieldNameAny(string fieldName)
    {
        string? match = null;
        int matchCount = 0;
        foreach (var (structName, fields) in _structFields)
        {
            if (fields.Contains(fieldName))
            {
                match = structName;
                matchCount++;
            }
        }
        return matchCount == 1 ? match : null;
    }

    /// <summary>
    /// Conservative fallback: only applies to distinctive field names (length > 3).
    /// Used for contexts where false positives are more dangerous.
    /// </summary>
    private string? TryGetStructNameByFieldName(string fieldName)
    {
        if (fieldName.Length <= 3)
            return null;
        return TryGetStructNameByFieldNameAny(fieldName);
    }
}
