using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression;

namespace CSharpToJava.Core.Transformers.Statement;

public partial class StatementTransformer
{
    /// <summary>
    /// Local declarations map directly: one explicit Java type, one or more
    /// declarators with optional initializers, 'const'/'readonly' → final.
    /// Implicit 'var' is resolved through the semantic model. ref locals,
    /// pointers, fixed-size buffers and tuple deconstructions must be lowered
    /// by a C#→C# preprocessor and fail fast here.
    /// </summary>
    private JavaSyntaxNode TransformLocalDeclaration(LocalDeclarationStatementSyntax stmt, ConversionContext context)
    {
        if (stmt.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
        {
            throw new NotSupportedException(
                "CS2J normalization required: using-declarations are handled as try-with-resources by the statement dispatcher.");
        }

        if (stmt.Declaration.Type is RefTypeSyntax)
        {
            throw new NotSupportedException(
                "CS2J normalization required: ref local must be lowered by a preprocessor before Java conversion.");
        }

        if (stmt.Declaration.Type is TupleTypeSyntax)
        {
            throw new NotSupportedException(
                "CS2J normalization required: deconstruction local declaration must be lowered by a preprocessor before Java conversion.");
        }

        var javaType = ResolveLocalJavaType(stmt, context);
        var isFinal = stmt.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword) || m.IsKind(SyntaxKind.ReadOnlyKeyword));

        var exprTransformer = ExpressionTransformerFacade.Instance;
        var holderStatements = new List<string>();
        var plainDeclarators = new List<string>();
        JavaExpression? singleInitializerIR = null;
        string? singleResolvedInitType = null;

        foreach (var variable in stmt.Declaration.Variables)
        {
            var localSymbol = context.GetDeclaredSymbol(variable) as ILocalSymbol;
            if (localSymbol?.Type is IPointerTypeSymbol)
            {
                throw new NotSupportedException(
                    $"CS2J normalization required: pointer local '{variable.Identifier.Text}' must be lowered by a preprocessor before Java conversion.");
            }

            var name = context.MethodState.GetEffectiveLocalJavaName(
                context.GetDeclaredSymbol(variable),
                ConversionContext.EscapeJavaKeyword(variable.Identifier.Text));

            var initExprText = variable.Initializer != null
                ? exprTransformer.Transform(variable.Initializer.Value, context)
                : null;

            // A lambda-captured, externally reassigned local is rewritten to a
            // one-element holder array; emit the holder declaration in place of
            // the ordinary local declaration.
            var origName = variable.Identifier.Text;
            if (context.MethodState.HasPendingLambdaCaptureHolder(origName)
                && context.MethodState.TryGetPendingLambdaCaptureHolder(origName, variable, out var capType, out var holderName))
            {
                var holderInit = initExprText ?? GetJavaDefaultForType(capType);
                var holderStatement = capType.Contains('<')
                    ? $"{capType}[] {holderName} = ({capType}[]) new Object[] {{ {holderInit} }}"
                    : $"{capType}[] {holderName} = new {capType}[] {{ {holderInit} }}";
                holderStatements.Add(holderStatement);
                context.MethodState.ActivateLambdaCaptureHolder(origName, variable);
                continue;
            }

            plainDeclarators.Add(initExprText != null ? $"{name} = {initExprText}" : name);

            if (stmt.Declaration.Variables.Count == 1 && variable.Initializer != null)
            {
                singleInitializerIR = new JavaRawExpression(initExprText!);
                var initType = context.GetTypeInfo(variable.Initializer.Value) is { } ti
                    ? (ti.Type ?? ti.ConvertedType)
                    : null;
                if (initType is { TypeKind: not TypeKind.Error })
                {
                    var mapped = context.MapType(initType);
                    if (!string.IsNullOrWhiteSpace(mapped))
                        singleResolvedInitType = mapped;
                }
            }
        }

        var preStatements = context.HasPendingPreStatements ? context.DrainPreStatements() : new List<string>();
        var postStatements = context.HasPendingPostStatements ? context.DrainPostStatements() : new List<string>();

        // Fast path: one plain declarator, no surrounding statements → structured IR.
        if (holderStatements.Count == 0 && plainDeclarators.Count == 1
            && preStatements.Count == 0 && postStatements.Count == 0)
        {
            return new JavaVariableDeclarationStatement
            {
                Type = javaType,
                Name = stmt.Declaration.Variables.Count == 1
                    ? context.MethodState.GetEffectiveLocalJavaName(
                        context.GetDeclaredSymbol(stmt.Declaration.Variables[0]),
                        ConversionContext.EscapeJavaKeyword(stmt.Declaration.Variables[0].Identifier.Text))
                    : plainDeclarators[0],
                Initializer = singleInitializerIR,
                IsFinal = isFinal,
                ResolvedInitializerType = singleResolvedInitType
            };
        }

        var emitted = new List<JavaSyntaxNode>();
        foreach (var pre in preStatements)
            emitted.Add(new JavaStatementNode(pre.TrimEnd(';') + ";"));
        foreach (var holder in holderStatements)
            emitted.Add(new JavaStatementNode(holder + ";"));
        if (plainDeclarators.Count > 0)
        {
            var finalKeyword = isFinal ? "final " : "";
            emitted.Add(new JavaStatementNode($"{finalKeyword}{javaType} {string.Join(", ", plainDeclarators)};"));
        }
        foreach (var post in postStatements)
            emitted.Add(new JavaStatementNode(post.TrimEnd(';') + ";"));

        return emitted.Count == 1 ? emitted[0] : new JavaMemberCollection(emitted.ToArray());
    }

    private static string ResolveLocalJavaType(LocalDeclarationStatementSyntax stmt, ConversionContext context)
    {
        // C# 'var' maps directly to Java 'var' (Java 10+ local variable inference).
        if (stmt.Declaration.Type is IdentifierNameSyntax { Identifier.Text: "var" })
            return "var";

        var firstVariable = stmt.Declaration.Variables.FirstOrDefault();
        var localType = firstVariable != null
            ? (context.GetDeclaredSymbol(firstVariable) as ILocalSymbol)?.Type
            : null;

        string javaType;
        if (localType is { } type && type is not IErrorTypeSymbol)
        {
            javaType = context.MapType(type);
        }
        else
        {
            javaType = context.MapTypeFromSyntax(stmt.Declaration.Type);
        }

        if (string.IsNullOrWhiteSpace(javaType) || javaType == "var")
        {
            throw new NotSupportedException(
                $"CS2J normalization required: cannot resolve the Java type of local declaration '{stmt.Declaration.Type}' without a semantic model; add an explicit type before Java conversion.");
        }

        return javaType;
    }

    private JavaSyntaxNode TransformYieldReturn(YieldStatementSyntax? stmt, ConversionContext context)
        => throw new NotSupportedException(
            "CS2J normalization required: 'yield return' must be lowered by a preprocessor (iterator lowering) before Java conversion.");

    private JavaSyntaxNode TransformYieldBreak(YieldStatementSyntax? stmt, ConversionContext context)
        => throw new NotSupportedException(
            "CS2J normalization required: 'yield break' must be lowered by a preprocessor (iterator lowering) before Java conversion.");
}
