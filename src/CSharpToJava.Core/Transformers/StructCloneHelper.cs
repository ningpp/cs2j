using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers;

/// <summary>
/// Shared utilities for struct value-copy (clone) semantics during C#-to-Java conversion.
/// C# structs are value types — assignment, argument passing, and returns all copy the value.
/// Java has no value types, so we insert .clone() calls to preserve these semantics.
/// </summary>
internal static class StructCloneHelper
{
    /// <summary>
    /// Returns true when <paramref name="type"/> is a user-defined C# struct that requires
    /// clone() calls to preserve value-type copy semantics in Java.
    /// Excludes primitive types, enums, Nullable&lt;T&gt;, and types without source declarations.
    /// </summary>
    public static bool IsUserDefinedStruct(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        if (named.TypeKind != TypeKind.Struct || named.TypeKind == TypeKind.Enum)
            return false;

        // C# ValueTuple types (tuple literals like (a, b)) have DeclaringSyntaxReferences
        // pointing at the tuple expression itself, so they would wrongly pass the source
        // declaration check below. They are mapped to immutable io.vavr.TupleN classes in
        // Java — never user-defined structs — so exclude them from clone() insertion.
        if (named.IsTupleType || named.OriginalDefinition.IsTupleType)
            return false;

        if (named.SpecialType != SpecialType.None)
            return false;

        if (named.OriginalDefinition.SpecialType != SpecialType.None)
            return false;

        if (named.OriginalDefinition.ToDisplayString() == "System.Nullable<T>")
            return false;

        return named.DeclaringSyntaxReferences.Length > 0
            || named.OriginalDefinition.DeclaringSyntaxReferences.Length > 0;
    }

    /// <summary>
    /// Returns true when the expression produces a fresh value that doesn't need cloning
    /// (e.g. new Foo(), method return values, default(T)).
    /// </summary>
    public static bool IsCloneableTemporary(ExpressionSyntax expression)
    {
        // Unwrap parentheses
        while (expression is ParenthesizedExpressionSyntax paren)
            expression = paren.Expression;

        return expression is ObjectCreationExpressionSyntax       // new Foo()
            or ImplicitObjectCreationExpressionSyntax              // new()
            or InvocationExpressionSyntax                          // Method() — return is a fresh copy if the method was converted
            or BinaryExpressionSyntax                              // a + b, a - b, etc. – operator returns a fresh value
            or PrefixUnaryExpressionSyntax                         // -x, !x – unary operator returns a fresh value
            or DefaultExpressionSyntax                             // default(T)
            or LiteralExpressionSyntax;                            // literal values
        // Note: ElementAccessExpressionSyntax (array[i]) is intentionally NOT excluded.
        // In C# array[i] for structs returns a copy, but in Java it returns a reference.
        // The existing ArgumentTransformer skips it for backward compatibility.
    }

    /// <summary>
    /// Returns true when the expression is a property access.
    /// Property getters already return .clone() for struct types (via PropertyTransformer
    /// for auto-properties, and via CloneStructValueIfNeeded for custom getter return statements),
    /// so the call site does not need to clone again.
    /// </summary>
    public static bool IsPropertyAccess(ExpressionSyntax expression, SemanticModel? model)
    {
        if (model == null)
            return false;

        // Unwrap parentheses
        while (expression is ParenthesizedExpressionSyntax paren)
            expression = paren.Expression;

        if (expression is not MemberAccessExpressionSyntax and not IdentifierNameSyntax)
            return false;

        var symbol = model.GetSymbolInfo(expression).Symbol;
        return symbol is IPropertySymbol;
    }

    /// <summary>
    /// Checks whether a <c>ref</c> parameter of a user-defined struct type is
    /// "effectively read-only" — i.e., the method body never directly reassigns the
    /// parameter variable and never forwards it as another <c>ref</c>/<c>out</c> argument.
    /// For struct-to-class conversions, such parameters do not need ObjectHolder wrapping
    /// because Java already passes objects by reference, so field reads/writes work naturally.
    /// Supports recursive analysis of ref-forwarding chains.
    /// </summary>
    public static bool IsRefParamEffectivelyReadOnly(IParameterSymbol parameter)
        => IsRefParamReadOnlyCore(parameter, null, null);

    /// <summary>
    /// Hierarchy-aware variant of <see cref="IsRefParamEffectivelyReadOnly"/>: the
    /// ref param of an overriding method skips ObjectHolder wrapping only when every
    /// method in the override chain also qualifies. Abstract base declarations have
    /// no body (analysis is conservative → not read-only → holder), so overrides
    /// must render the holder signature too or javac reports "does not override
    /// abstract method" (e.g. ArrayConverter.createCollection vs its abstract base).
    /// </summary>
    public static bool IsRefParamEffectivelyReadOnlyInHierarchy(IParameterSymbol parameter, Compilation? compilation = null)
    {
        if (!IsRefParamEffectivelyReadOnly(parameter))
            return false;

        var method = parameter.ContainingSymbol as IMethodSymbol;
        if (method == null)
            return false;

        int index = method.Parameters.IndexOf(parameter);

        // Java requires an override to repeat the base signature exactly, so the
        // whole hierarchy must agree on the ObjectHolder form. The base method may
        // be read-only while a derived override forwards/mutates the ref parameter
        // (e.g. JsonConverter<T>.OnTryWrite), which forces every member of the
        // chain to use the holder.
        foreach (var hierarchyMethod in GetOverrideHierarchy(method, compilation))
        {
            if (index < 0 || index >= hierarchyMethod.Parameters.Length)
                continue;
            if (!IsRefParamEffectivelyReadOnly(hierarchyMethod.Parameters[index]))
                return false;
        }
        return true;
    }

    private static IEnumerable<IMethodSymbol> GetOverrideHierarchy(
        IMethodSymbol method,
        Compilation? compilation)
    {
        var result = new List<IMethodSymbol>();
        var current = method;
        while (current != null)
        {
            result.Add(current);
            current = current.OverriddenMethod;
        }

        // The whole override family must agree on the holder-vs-raw parameter shape.
        // When the analyzed method is itself a derived override, its SIBLINGS (other
        // overrides of the same root) may mutate the ref parameter even if this
        // method doesn't — e.g. JsonConverter<T>.ReadWithQuotes: CharConverter
        // re-reads the reader (forcing the holder form) while GuidConverter is
        // read-only. Without collecting siblings, the family mixed shapes and the
        // read-only overrides emitted a raw parameter — a Java OVERLOAD, not a
        // virtual override — so dictionary-key reads dispatched to the base and
        // threw. Collect every method whose override chain reaches the family root.
        var root = result[^1];

        if (compilation != null)
        {
            foreach (var candidate in compilation.GetSymbolsWithName(method.Name, SymbolFilter.Member)
                         .OfType<IMethodSymbol>())
            {
                if (SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, method.OriginalDefinition))
                {
                    continue;
                }
                var overridden = candidate.OverriddenMethod;
                while (overridden != null)
                {
                    if (SymbolEqualityComparer.Default.Equals(
                            overridden.OriginalDefinition,
                            root.OriginalDefinition))
                    {
                        result.Add(candidate);
                        break;
                    }
                    overridden = overridden.OverriddenMethod;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Checks whether a <b>value</b> parameter of a user-defined struct type is
    /// "effectively read-only" in the method body — i.e., the method never modifies
    /// the parameter variable itself, its fields, or forwards it via ref/out to a
    /// method that could modify it.
    /// When true, call sites can skip cloning the struct argument.
    /// </summary>
    public static bool IsValueParamEffectivelyReadOnly(IParameterSymbol parameter, Compilation? compilation)
    {
        if (parameter.RefKind != RefKind.None)
            return false;
        if (!IsUserDefinedStruct(parameter.Type))
            return false;
        var method = parameter.ContainingSymbol;
        if (method == null)
            return false;
        return IsValueParamReadOnlyCore(parameter, compilation,
            new HashSet<ISymbol>(SymbolEqualityComparer.Default));
    }

    // ── Core analysis (supports recursive ref-forwarding with cycle detection) ─

    private static bool IsRefParamReadOnlyCore(IParameterSymbol parameter,
        Compilation? compilation, HashSet<ISymbol>? visited)
    {
        if (parameter.RefKind != RefKind.Ref)
            return false;
        if (!IsUserDefinedStruct(parameter.Type))
            return false;
        var method = parameter.ContainingSymbol;
        if (method == null)
            return false;

        visited ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        if (!visited.Add(parameter))
            return false; // cycle → conservative

        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            var syntax = syntaxRef.GetSyntax();
            SyntaxNode? body = GetMethodBody(syntax);
            if (body == null)
                return false;

            var paramName = parameter.Name;
            foreach (var node in body.DescendantNodes())
            {
                if (node is AssignmentExpressionSyntax assignment
                    && assignment.Left is IdentifierNameSyntax lhsIdent
                    && lhsIdent.Identifier.Text == paramName)
                    return false;

                if (node is ArgumentSyntax arg
                    && arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                    && arg.Expression is IdentifierNameSyntax refIdent
                    && refIdent.Identifier.Text == paramName)
                {
                    if (!IsRefForwardingSafe(arg, syntaxRef.SyntaxTree, compilation, visited))
                        return false;
                    continue;
                }

                if (node is ArgumentSyntax outArg
                    && outArg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
                    && outArg.Expression is IdentifierNameSyntax outIdent
                    && outIdent.Identifier.Text == paramName)
                    return false;

                if (IsUnaryMutationOf(node, paramName))
                    return false;
            }
        }
        return true;
    }

    private static bool IsValueParamReadOnlyCore(IParameterSymbol parameter,
        Compilation? compilation, HashSet<ISymbol> visited)
    {
        var method = parameter.ContainingSymbol;
        if (method == null) return false;
        if (!visited.Add(parameter)) return false;

        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            var syntax = syntaxRef.GetSyntax();
            SyntaxNode? body = GetMethodBody(syntax);
            if (body == null) return false;

            var paramName = parameter.Name;
            foreach (var node in body.DescendantNodes())
            {
                // Direct assignment: paramName = expr
                if (node is AssignmentExpressionSyntax assignment
                    && assignment.Left is IdentifierNameSyntax lhsIdent
                    && lhsIdent.Identifier.Text == paramName)
                    return false;

                // Field assignment through the parameter: paramName.field = expr
                if (node is AssignmentExpressionSyntax fieldAssign
                    && IsChainedMemberAccessFrom(fieldAssign.Left, paramName))
                    return false;

                // Ref/out forwarding: Method(ref paramName) or Method(ref paramName.field)
                if (node is ArgumentSyntax arg
                    && (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                        || arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)))
                {
                    if (arg.Expression is IdentifierNameSyntax refIdent
                        && refIdent.Identifier.Text == paramName)
                    {
                        if (arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                            return false;
                        if (!IsRefForwardingSafe(arg, syntaxRef.SyntaxTree, compilation, visited))
                            return false;
                        continue;
                    }
                    if (IsChainedMemberAccessFrom(arg.Expression, paramName))
                        return false;
                }

                // Prefix/postfix on the parameter or its fields
                if (IsUnaryMutationOf(node, paramName))
                    return false;
            }
        }
        return true;
    }

    private static SyntaxNode? GetMethodBody(SyntaxNode syntax) => syntax switch
    {
        MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
        LocalFunctionStatementSyntax l => (SyntaxNode?)l.Body ?? l.ExpressionBody,
        _ => null
    };

    private static bool IsRefForwardingSafe(ArgumentSyntax arg, SyntaxTree syntaxTree,
        Compilation? compilation, HashSet<ISymbol> visited)
    {
        if (compilation == null)
            return false;
        SemanticModel? model;
        try { model = compilation.GetSemanticModel(syntaxTree); }
        catch { return false; }

        var invocation = arg.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        if (invocation == null) return false;
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol calledMethod)
            return false;

        var argList = arg.Parent as ArgumentListSyntax;
        if (argList == null) return false;
        int argIndex = argList.Arguments.IndexOf(arg);
        if (argIndex < 0 || argIndex >= calledMethod.Parameters.Length)
            return false;

        return IsRefParamReadOnlyCore(calledMethod.Parameters[argIndex], compilation, visited);
    }

    private static bool IsChainedMemberAccessFrom(ExpressionSyntax expression, string identifierName)
    {
        if (expression is not MemberAccessExpressionSyntax)
            return false;
        var current = expression;
        while (current is MemberAccessExpressionSyntax ma)
            current = ma.Expression;
        while (current is ElementAccessExpressionSyntax ea)
            current = ea.Expression;
        return current is IdentifierNameSyntax id && id.Identifier.Text == identifierName;
    }

    private static bool IsUnaryMutationOf(SyntaxNode node, string paramName)
    {
        if (node is PrefixUnaryExpressionSyntax prefix
            && IsIdentOrChainedFrom(prefix.Operand, paramName))
            return true;
        if (node is PostfixUnaryExpressionSyntax postfix
            && IsIdentOrChainedFrom(postfix.Operand, paramName))
            return true;
        return false;
    }

    private static bool IsIdentOrChainedFrom(ExpressionSyntax expr, string identifierName)
    {
        if (expr is IdentifierNameSyntax id && id.Identifier.Text == identifierName)
            return true;
        return IsChainedMemberAccessFrom(expr, identifierName);
    }

    /// <summary>
    /// Appends .clone() to the transformed expression, handling complex expressions
    /// that need parenthesizing.
    /// </summary>
    public static string BuildCloneExpression(ExpressionSyntax originalSyntax, string transformedExpression)
    {
        var trimmed = transformedExpression.Trim();
        if (trimmed.Length == 0)
            return transformedExpression;

        if (CanCallCloneDirectly(originalSyntax))
            return $"{transformedExpression}.clone()";

        return $"(({transformedExpression})).clone()";
    }

    /// <summary>
    /// Checks whether .clone() can be appended directly (simple expressions)
    /// vs needing parenthesization (complex expressions).
    /// </summary>
    private static bool CanCallCloneDirectly(ExpressionSyntax expression)
        => expression is IdentifierNameSyntax
            or ThisExpressionSyntax
            or BaseExpressionSyntax
            or MemberAccessExpressionSyntax
            or ElementAccessExpressionSyntax;

    /// <summary>
    /// For assignment/declaration/return contexts: returns the expression with .clone()
    /// appended if the type is a user-defined struct and the expression is not a temporary.
    /// </summary>
    public static string CloneStructValueIfNeeded(
        ExpressionSyntax exprNode,
        string transformedExpr,
        ITypeSymbol? exprType,
        ConversionContext context)
    {
        if (context.SemanticModel == null)
            return transformedExpr;

        if (!IsUserDefinedStruct(exprType))
            return transformedExpr;

        if (IsCloneableTemporary(exprNode))
            return transformedExpr;

        // Skip if the expression already ends with .clone()
        if (transformedExpr.TrimEnd().EndsWith(".clone()"))
            return transformedExpr;

        // Await results (x.join()) lose their generic type in Java and surface as
        // Object — cast back to the struct type BEFORE calling clone(), otherwise
        // Object.clone() (protected) fails to compile.
        if (exprNode is AwaitExpressionSyntax awaitNode
            && transformedExpr.Trim().EndsWith(".join()", StringComparison.Ordinal))
        {
            var mappedStruct = context.MapType(exprType);
            if (!string.IsNullOrWhiteSpace(mappedStruct) && mappedStruct != "Object")
            {
                context.AddImportsForTypePublic(exprType.ToDisplayString());
                return $"(({mappedStruct}) {transformedExpr}).clone()";
            }
        }

        return BuildCloneExpression(exprNode, transformedExpr);
    }
}
