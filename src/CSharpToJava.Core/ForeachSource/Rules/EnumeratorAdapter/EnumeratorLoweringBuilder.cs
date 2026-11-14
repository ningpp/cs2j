using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ForeachSource;

/// <summary>
/// Result of lowering one duck-typed foreach site (design §1.3/§4.3/§4.4).
/// The flags drive per-bucket statistics in <see cref="EnumeratorAdapterRule"/>.
/// </summary>
internal sealed record EnumeratorLowering(
    BlockSyntax Block,
    bool DirectEnumerator,
    bool ExtensionGetEnumerator,
    bool StructEnumerator,
    bool DisposeGenerated,
    bool DisposeBoxed,
    bool RefStructPatternDispose);

/// <summary>
/// C40 FA skeleton builder: caches the source in <c>__cs2jFaSrc</c>, binds the
/// enumerator in a <c>var __cs2jFaE</c> local, emits <c>while(MoveNext())</c>
/// with the Current fetch as the first per-iteration statement, and — only
/// when the enumerator type actually implements <c>System.IDisposable</c>
/// (M1 probe-pinned, see design backfill) — wraps the loop in try/finally
/// (design decisions 2/4/5/6/7/13, hard invariants 4/5/6).
/// </summary>
internal static class EnumeratorLoweringBuilder
{
    public static EnumeratorLowering Lower(
        ForEachStatementSyntax node,
        ForEachStatementSyntax semanticNode,
        ForeachBinding binding,
        SemanticModel model,
        ForeachSourceRewriteContext context,
        RefKind elementRefKind = RefKind.None)
    {
        var sourceName = context.FreshName(ForeachSourceSyntaxFacts.FaSourceName, node);
        var enumeratorName = context.FreshName(ForeachSourceSyntaxFacts.FaEnumeratorName, node);
        var sourceAccess = SyntaxFactory.IdentifierName(sourceName);
        var enumeratorAccess = SyntaxFactory.IdentifierName(enumeratorName);

        // Invariant 6: the source expression is evaluated exactly once.
        var sourceDecl = VarLocal(sourceName, node.Expression);

        // Decision 4: direct collection-is-enumerator pattern copies the
        // source once; otherwise the instance call syntax is kept even when
        // the bound GetEnumerator is an extension method (decision 2: the
        // C# compiler re-resolves it from the preserved using directives).
        ExpressionSyntax enumeratorInit = binding.DirectEnumerator
            ? sourceAccess
            : SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    sourceAccess,
                    SyntaxFactory.Token(SyntaxKind.DotToken),
                    SyntaxFactory.IdentifierName("GetEnumerator")),
                SyntaxFactory.ArgumentList());
        var enumeratorDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(enumeratorName)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(enumeratorInit)))));

        // Decision 7/13: Current is fetched exactly once per iteration, as the
        // first statement of the body. A (TElement) cast is inserted only when
        // CurrentProperty.Type differs from the iteration local's type
        // (non-generic object Current + foreach(int x in e), explicit
        // narrowing, …); var without conversion stays var.
        var currentAccess = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            enumeratorAccess,
            SyntaxFactory.Token(SyntaxKind.DotToken),
            SyntaxFactory.IdentifierName("Current"));

        // Original tree node (nested-foreach case): the declared-symbol query
        // must not run against the rebuilt visited node.
        var declaredLocal = model.GetDeclaredSymbol(semanticNode) as ILocalSymbol;
        var elementType = declaredLocal?.Type ?? binding.ElementType;
        var isVar = node.Type is IdentifierNameSyntax { Identifier.Text: "var" };
        var needsCast = elementRefKind == RefKind.None
                        && elementType != null
                        && binding.CurrentProperty != null
                        && !SymbolEqualityComparer.Default.Equals(binding.CurrentProperty.Type, elementType);

        var itemIdentifier = node.Identifier.WithoutTrivia();
        TypeSyntax declaredTypeSyntax;
        ExpressionSyntax itemInitializer = currentAccess;
        if (elementRefKind == RefKind.None && isVar && !needsCast)
        {
            declaredTypeSyntax = SyntaxFactory.IdentifierName("var");
        }
        else
        {
            // FR-3 (ref/ref-readonly Current) and explicit/cast cases always
            // render the fully qualified concrete type; var is illegal in a
            // ref local declaration and ref casts do not exist, so FR-3 callers
            // guarantee type identity before reaching here.
            var rendered = elementType!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            declaredTypeSyntax = SyntaxFactory.ParseTypeName(rendered);
            if (needsCast)
            {
                itemInitializer = SyntaxFactory.CastExpression(
                    declaredTypeSyntax.WithoutTrivia(),
                    currentAccess);
            }
        }

        VariableDeclarationSyntax itemVariableDeclaration;
        if (elementRefKind is RefKind.Ref or RefKind.RefReadOnly)
        {
            // FR-3: ref T item = ref __cs2jFaE.Current;  (readonly token when RefReadOnly)
            var refType = SyntaxFactory.RefType(
                SyntaxFactory.Token(SyntaxKind.RefKeyword),
                elementRefKind == RefKind.RefReadOnly
                    ? SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)
                    : default,
                declaredTypeSyntax);
            itemInitializer = SyntaxFactory.RefExpression(
                SyntaxFactory.Token(SyntaxKind.RefKeyword),
                currentAccess);
            itemVariableDeclaration = SyntaxFactory.VariableDeclaration(refType);
        }
        else
        {
            itemVariableDeclaration = SyntaxFactory.VariableDeclaration(declaredTypeSyntax);
        }

        var itemDecl = SyntaxFactory.LocalDeclarationStatement(
            itemVariableDeclaration
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(itemIdentifier)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(itemInitializer)))));

        var body = ForeachSourceSyntaxFacts.PrependStatements(
            node.Statement, new[] { itemDecl });

        var moveNextCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                enumeratorAccess,
                SyntaxFactory.Token(SyntaxKind.DotToken),
                SyntaxFactory.IdentifierName("MoveNext")),
            SyntaxFactory.ArgumentList());
        StatementSyntax loopStatement = SyntaxFactory.WhileStatement(moveNextCall, body);

        // Dispose classification (M1 + M2 probes on the referenced Roslyn
        // 5.0 / net10 surface — see spec backfills 2026-10-01). A finally is
        // emitted iff either:
        //   (a) the enumerator type actually implements System.IDisposable
        //       (class/plain-struct case). ForEachStatementInfo.DisposeMethod
        //       is NOT a reliable signal here: it reports IDisposable.Dispose
        //       for merely-convertible types (unsealed classes with a
        //       pattern-only Dispose()) and is null for plain structs with a
        //       public Dispose(); a public parameterless Dispose() WITHOUT
        //       IDisposable is never invoked by foreach for these shapes.
        //   (b) the enumerator type is a ref struct (IsRefLikeType) and the
        //       binding gives an accessible parameterless void Dispose()
        //       pattern method (M2 probe-pinned: for ref structs
        //       info.DisposeMethod IS the compiler-selected pattern symbol;
        //       foreach calls it directly, without boxing).
        // A ref struct CAN implement IDisposable since C# 13, but only through
        // explicit interface implementations: it still cannot be converted to
        // the interface, so that shape is disposed through a constrained
        // generic helper instead of an interface cast (see below).
        // On net10 the non-generic IEnumerator no longer lists IDisposable.
        // Hard invariant 4: every other enumerator gets no try/finally.
        var implementsDisposable =
            EnumeratorImplementsDisposable(binding.EnumeratorType, model.Compilation);
        var refStructPatternDispose = !implementsDisposable
                                      && IsRefStructPatternDispose(binding.EnumeratorType, binding.DisposeMethod);

        var disposeGenerated = false;
        var disposeBoxed = false;
        LocalFunctionStatementSyntax? refStructDisposeHelper = null;
        if (implementsDisposable)
        {
            ExpressionSyntax disposeCall;
            if (TryGetDirectDisposeAccess(binding.EnumeratorType!, out _))
            {
                disposeCall = DisposeCall(enumeratorAccess);
            }
            else if (binding.EnumeratorType is { IsRefLikeType: true })
            {
                // Ref-like enumerator implementing IDisposable only through an
                // explicit interface implementation (legal since C# 13). It can
                // never be converted to the interface (CS0030 — ref structs do
                // not box), so neither the direct call (the explicit member is
                // not reachable through the type, CS1061) nor the interface cast
                // below is legal. foreach itself disposes these via a
                // constrained, unboxed interface call; the only source-level
                // equivalent is a generic helper constrained with
                // `allows ref struct`, which binds to the same implementation.
                var helperName = context.FreshName(ForeachSourceSyntaxFacts.FaDisposeHelperName, node);
                var parameterName = context.FreshName(
                    ForeachSourceSyntaxFacts.FaDisposeHelperParameterName, node);
                refStructDisposeHelper = BuildRefStructDisposeHelper(helperName, parameterName);
                disposeCall = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.IdentifierName(helperName),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(SyntaxFactory.RefExpression(
                            SyntaxFactory.Token(SyntaxKind.RefKeyword),
                            enumeratorAccess)))));
            }
            else
            {
                // Explicit interface implementation (or an interface-typed
                // enumerator handled above by TryGetDirectDisposeAccess): go
                // through the interface cast. Value types box here (CS2J5455,
                // risk 1 observable point).
                var disposableType = model.Compilation.GetSpecialType(SpecialType.System_IDisposable);
                var disposableSyntax = SyntaxFactory.ParseTypeName(
                    disposableType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                // Parenthesize the cast: without the outer pair the member
                // access would parse as ((IDisposable)(e.Dispose())).
                disposeCall = DisposeCall(SyntaxFactory.ParenthesizedExpression(
                    SyntaxFactory.CastExpression(disposableSyntax, enumeratorAccess)));
                disposeBoxed = binding.EnumeratorType?.IsValueType == true;
            }

            loopStatement = WrapTryFinally(loopStatement, disposeCall);
            disposeGenerated = true;
        }
        else if (refStructPatternDispose)
        {
            // Pattern Dispose on a ref struct: direct, unboxed invocation of
            // the compiler-resolved accessible Dispose() method.
            loopStatement = WrapTryFinally(loopStatement, DisposeCall(enumeratorAccess));
            disposeGenerated = true;
        }

        var generatedStatements = new List<StatementSyntax> { sourceDecl, enumeratorDecl };
        if (refStructDisposeHelper != null)
        {
            generatedStatements.Add(refStructDisposeHelper);
        }

        generatedStatements.Add(loopStatement);
        var statements = SyntaxFactory.List(generatedStatements);
        // Both leading and trailing trivia of the replaced statement belong to
        // the synthesized block (E07-style multi-statement replacement), never
        // to its first statement: leading trivia printed after the block's `{`
        // would push a `#if NET … #else … #endif` region (and its inactive
        // code) inside the braces, leaving the branches with unbalanced braces.
        // On the block the whole `#if/#else` region wraps the braces uniformly.
        // Labels inside the moved body are preserved.
        var block = SyntaxFactory.Block(statements)
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia());

        return new EnumeratorLowering(
            block,
            DirectEnumerator: binding.DirectEnumerator,
            ExtensionGetEnumerator: binding.GetEnumeratorMethod is { IsExtensionMethod: true },
            StructEnumerator: binding.EnumeratorType?.IsValueType == true,
            DisposeGenerated: disposeGenerated,
            DisposeBoxed: disposeBoxed,
            RefStructPatternDispose: refStructPatternDispose);
    }

    private static StatementSyntax WrapTryFinally(
        StatementSyntax loopStatement,
        ExpressionSyntax disposeCall)
    {
        return SyntaxFactory.TryStatement(
            SyntaxFactory.Block(loopStatement),
            SyntaxFactory.List<CatchClauseSyntax>(),
            SyntaxFactory.FinallyClause(SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(disposeCall))));
    }

    private static ExpressionSyntax DisposeCall(ExpressionSyntax receiver)
        => SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver,
                SyntaxFactory.Token(SyntaxKind.DotToken),
                SyntaxFactory.IdentifierName("Dispose")),
            SyntaxFactory.ArgumentList());

    /// <summary>
    /// Source-level equivalent of the compiler's constrained interface call for
    /// ref-like enumerators whose Dispose() is only reachable through an
    /// explicit IDisposable implementation: a generic local function with the
    /// (C# 13) <c>allows ref struct</c> anti-constraint, called with
    /// <c>ref</c> so no copy and no boxing is involved. A non-generic direct
    /// call does not compile (the explicit member is not a member of the
    /// type), and an interface cast is CS0030 (ref structs cannot be boxed).
    /// Such input requires C# 13 at the call site anyway, so the anti-constraint
    /// never exceeds the input language version.
    /// </summary>
    private static LocalFunctionStatementSyntax BuildRefStructDisposeHelper(
        string helperName,
        string parameterName)
    {
        var text =
            $"static void {helperName}<TCs2jFaDisposable>(ref TCs2jFaDisposable {parameterName}) " +
            $"where TCs2jFaDisposable : global::System.IDisposable, allows ref struct " +
            $"{{ {parameterName}.Dispose(); }}";
        return (LocalFunctionStatementSyntax)SyntaxFactory.ParseStatement(
            text,
            options: new CSharpParseOptions(LanguageVersion.Latest));
    }

    /// <summary>
    /// Ref-struct pattern-Dispose predicate (M2 probe-pinned): the enumerator
    /// type is ref-like and the binding's DisposeMethod is the compiler-
    /// selected accessible parameterless void instance Dispose() declared on
    /// that type itself. The compiler only produces this symbol when it would
    /// emit the pattern call (a ref struct's only interface implementations are
    /// explicit ones and it is never convertible to the interface), so
    /// the lowered direct <c>__cs2jFaE.Dispose()</c> binds identically.
    /// </summary>
    private static bool IsRefStructPatternDispose(
        ITypeSymbol? enumeratorType,
        IMethodSymbol? disposeMethod)
    {
        if (enumeratorType is not { IsRefLikeType: true })
        {
            return false;
        }

        return disposeMethod is
        {
            IsStatic: false,
            Parameters.Length: 0,
            DeclaredAccessibility: Accessibility.Public,
        }
               && disposeMethod.ReturnsVoid
               && SymbolEqualityComparer.Default.Equals(
                   disposeMethod.ContainingType, enumeratorType);
    }

    /// <summary>
    /// True when the enumerator static type actually implements
    /// <c>System.IDisposable</c>: the type is the interface itself, or it is
    /// present in <see cref="ITypeSymbol.AllInterfaces"/>. Symbol-wise this
    /// matches the runtime behavior (a class declaring <c>: IEnumerator</c>
    /// without a Dispose method compiles but is genuinely not IDisposable —
    /// AllInterfaces omits it, exactly like the runtime interface map).
    /// </summary>
    private static bool EnumeratorImplementsDisposable(ITypeSymbol? enumeratorType, Compilation compilation)
    {
        if (enumeratorType == null)
        {
            return false;
        }

        if (enumeratorType.SpecialType == SpecialType.System_IDisposable)
        {
            return true;
        }

        foreach (var iface in enumeratorType.AllInterfaces)
        {
            if (iface.SpecialType == SpecialType.System_IDisposable)
            {
                return true;
            }
        }

        // Fallback for missing special-type bindings (defensive; compare by
        // metadata name instead of identity).
        var disposable = compilation.GetTypeByMetadataName("System.IDisposable");
        if (disposable == null)
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(enumeratorType, disposable))
        {
            return true;
        }

        foreach (var iface in enumeratorType.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, disposable))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <c>__cs2jFaE.Dispose()</c> binds directly on the static
    /// enumerator type without a cast: an interface-typed enumerator (member
    /// access through <c>IEnumerator&lt;T&gt;</c>/<c>IDisposable</c> always
    /// resolves), or a concrete type/base type exposing a public parameterless
    /// <c>void Dispose()</c>. Value types reached this way stay unboxed. When
    /// false the type is known to implement IDisposable through an explicit
    /// interface implementation, so the call is emitted as
    /// <c>((System.IDisposable)__cs2jFaE).Dispose()</c> — except for ref-like
    /// types, which cannot be converted to the interface at all (CS0030) and
    /// take the constrained generic-helper path instead.
    /// </summary>
    private static bool TryGetDirectDisposeAccess(ITypeSymbol enumeratorType, out IMethodSymbol? disposeMethod)
    {
        disposeMethod = null;

        if (enumeratorType.TypeKind == TypeKind.Interface)
        {
            return true;
        }

        for (var current = enumeratorType; current != null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers("Dispose"))
            {
                if (member is not IMethodSymbol method
                    || method is { IsStatic: true }
                    || method.Parameters.Length != 0
                    || method.DeclaredAccessibility != Accessibility.Public)
                {
                    continue;
                }

                if (method.ReturnsVoid
                    || method.ReturnType.SpecialType == SpecialType.System_Void)
                {
                    disposeMethod = method;
                    return true;
                }
            }
        }

        return false;
    }

    private static LocalDeclarationStatementSyntax VarLocal(string name, ExpressionSyntax initializer)
        => SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(name)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(initializer)))));
}
