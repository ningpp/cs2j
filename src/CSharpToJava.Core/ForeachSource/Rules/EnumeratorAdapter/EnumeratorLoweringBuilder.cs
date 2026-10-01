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
    bool DisposeBoxed);

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
        ForeachBinding binding,
        SemanticModel model,
        ForeachSourceRewriteContext context)
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

        var declaredLocal = model.GetDeclaredSymbol(node) as ILocalSymbol;
        var elementType = declaredLocal?.Type ?? binding.ElementType;
        var isVar = node.Type is IdentifierNameSyntax { Identifier.Text: "var" };
        var needsCast = elementType != null
                        && binding.CurrentProperty != null
                        && !SymbolEqualityComparer.Default.Equals(binding.CurrentProperty.Type, elementType);

        var itemIdentifier = node.Identifier.WithoutTrivia();
        TypeSyntax declaredTypeSyntax;
        ExpressionSyntax itemInitializer = currentAccess;
        if (isVar && !needsCast)
        {
            declaredTypeSyntax = SyntaxFactory.IdentifierName("var");
        }
        else
        {
            var rendered = elementType!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            declaredTypeSyntax = SyntaxFactory.ParseTypeName(rendered);
            if (needsCast)
            {
                itemInitializer = SyntaxFactory.CastExpression(
                    declaredTypeSyntax.WithoutTrivia(),
                    currentAccess);
            }
        }

        var itemDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(declaredTypeSyntax)
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

        // Dispose classification (M1 probe-pinned on the actually referenced
        // Roslyn 5.0 / net10 surface — see design backfill 2026-10-01):
        //   * ForEachStatementInfo.DisposeMethod is NOT a reliable "needs
        //     dispose" signal: it reports IDisposable.Dispose for any type
        //     convertible to IDisposable (non-sealed classes, types with a
        //     mere pattern Dispose()), and is null for structs.
        //   * Runtime foreach semantics call Dispose only when the enumerator
        //     type actually implements System.IDisposable. A public
        //     parameterless Dispose() WITHOUT IDisposable is never invoked by
        //     foreach (verified with emitted-assembly probes), and on net10
        //     the non-generic IEnumerator no longer lists IDisposable either.
        // Hard invariant 4: non-IDisposable enumerators get no try/finally.
        var disposeGenerated = false;
        var disposeBoxed = false;
        if (EnumeratorImplementsDisposable(binding.EnumeratorType, model.Compilation))
        {
            ExpressionSyntax disposeReceiver = enumeratorAccess;
            if (!TryGetDirectDisposeAccess(binding.EnumeratorType!, out _))
            {
                // Explicit interface implementation (or an interface-typed
                // enumerator handled above by TryGetDirectDisposeAccess): go
                // through the interface cast.
                var disposableType = model.Compilation.GetSpecialType(SpecialType.System_IDisposable);
                var disposableSyntax = SyntaxFactory.ParseTypeName(
                    disposableType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                disposeReceiver = SyntaxFactory.CastExpression(disposableSyntax, enumeratorAccess);
                disposeBoxed = binding.EnumeratorType?.IsValueType == true;
                // TODO(M2): CS2J5455 boxed-struct Dispose counter/strict warning.
            }

            var disposeCall = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        disposeReceiver,
                        SyntaxFactory.Token(SyntaxKind.DotToken),
                        SyntaxFactory.IdentifierName("Dispose")),
                    SyntaxFactory.ArgumentList()));

            loopStatement = SyntaxFactory.TryStatement(
                SyntaxFactory.Block(loopStatement),
                SyntaxFactory.List<CatchClauseSyntax>(),
                SyntaxFactory.FinallyClause(SyntaxFactory.Block(disposeCall)));
            disposeGenerated = true;
        }

        var statements = SyntaxFactory.List(new[] { sourceDecl, enumeratorDecl, loopStatement });
        // Original node trivia lands on the first replacement statement; the
        // block itself (E07-style multi-statement replacement) carries the
        // trailing trivia. Labels inside the moved body are preserved.
        var first = statements[0].WithLeadingTrivia(node.GetLeadingTrivia());
        statements = statements.Replace(statements[0], first);
        var block = SyntaxFactory.Block(statements)
            .WithTrailingTrivia(node.GetTrailingTrivia());

        return new EnumeratorLowering(
            block,
            DirectEnumerator: binding.DirectEnumerator,
            ExtensionGetEnumerator: binding.GetEnumeratorMethod is { IsExtensionMethod: true },
            StructEnumerator: binding.EnumeratorType?.IsValueType == true,
            DisposeGenerated: disposeGenerated,
            DisposeBoxed: disposeBoxed);
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
    /// <c>((System.IDisposable)__cs2jFaE).Dispose()</c>.
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
