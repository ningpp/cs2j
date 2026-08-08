using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Statement;

/// <summary>
/// C# local function lowering: Java has no local functions, so local functions without
/// enclosing captures are hoisted to class level as private methods. They are registered
/// via <see cref="MethodConversionState.RegisterHoistedLocalFunction"/> during a pre-scan
/// of the method-level block (so declarations after return statements are still found)
/// and drained by the enclosing member transformer. Local functions that capture
/// enclosing locals/parameters keep the legacy TODO comment fallback.
/// </summary>
public partial class StatementTransformer
{
    private JavaSyntaxNode TransformLocalFunctionStatement(LocalFunctionStatementSyntax node, ConversionContext context)
    {
        // Capturing local functions cannot be hoisted without closure conversion;
        // keep the legacy TODO comment so the situation remains visible at the call site.
        if (LocalFunctionCapturesEnclosingState(node, context))
        {
            return new JavaStatementNode($"/* TODO: LocalFunctionStatement - {node} */");
        }

        // Hoistable: registered (deduplicated) and emitted as a private class-level
        // method by the enclosing member transformer. Emit nothing inline.
        context.MethodState.RegisterHoistedLocalFunction(node);
        return new JavaStatementNode("");
    }

    /// <summary>
    /// Registers all hoistable local functions in the method-level block, including
    /// declarations that appear after return statements (legal in C#) and local
    /// functions nested inside other local functions.
    /// </summary>
    private static void PrescanLocalFunctions(BlockSyntax block, ConversionContext context)
    {
        foreach (var localFn in block.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
        {
            if (!LocalFunctionCapturesEnclosingState(localFn, context))
            {
                context.MethodState.RegisterHoistedLocalFunction(localFn);
            }
        }
    }

    private static bool LocalFunctionCapturesEnclosingState(LocalFunctionStatementSyntax localFn, ConversionContext context)
    {
        var localFnSymbol = context.GetDeclaredSymbol(localFn) as IMethodSymbol;
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (localFnSymbol == null || body == null)
        {
            // Cannot analyze captures — be conservative and keep the TODO fallback.
            return true;
        }

        // Do not descend into nested local functions: their own parameters/locals
        // belong to the nested function, not to this one.
        foreach (var identifier in body
            .DescendantNodes(d => d is not LocalFunctionStatementSyntax)
            .OfType<IdentifierNameSyntax>())
        {
            var symbol = context.GetSymbolInfo(identifier).Symbol;
            switch (symbol)
            {
                case ILocalSymbol or IParameterSymbol or IRangeVariableSymbol:
                    if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol, localFnSymbol))
                    {
                        return true;
                    }
                    break;
            }
        }

        return false;
    }
}
