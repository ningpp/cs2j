using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Statement;

/// <summary>
/// C# local function lowering: Java has no local functions, so local functions are
/// hoisted to class level as private methods. Functions that capture enclosing
/// locals/parameters are hoisted too — the captured variables become extra trailing
/// parameters, and references inside the body are renamed to the parameter names.
/// </summary>
public partial class StatementTransformer
{
    private JavaSyntaxNode TransformLocalFunctionStatement(LocalFunctionStatementSyntax node, ConversionContext context)
    {
        // Hoisted (with or without captures) and emitted as a private class-level method
        // by the enclosing member transformer. Captures are collected and expanded during
        // PrescanLocalFunctions, before any call site is converted. Emit nothing inline.
        context.MethodState.RegisterHoistedLocalFunction(node);
        return new JavaStatementNode("");
    }

    /// <summary>
    /// Registers all hoistable local functions in the method-level block, including
    /// declarations that appear after return statements (legal in C#) and local
    /// functions nested inside other local functions. Captures of local functions called
    /// directly by a function are propagated into that function (fixpoint): a hoisted
    /// caller must capture the variables its callees' hoisted call sites need, so their
    /// signatures and call sites agree.
    /// </summary>
    private static void PrescanLocalFunctions(BlockSyntax block, ConversionContext context)
    {
        var localFns = block.DescendantNodes().OfType<LocalFunctionStatementSyntax>().ToList();
        foreach (var localFn in localFns)
        {
            context.MethodState.RegisterHoistedLocalFunction(localFn);
            var captures = CollectLocalFunctionCaptures(localFn, context);
            context.MethodState.RegisterHoistedLocalFunctionCaptures(localFn.Identifier.Text, captures);
            context.MethodState.RegisterHoistedLocalFunctionDefaults(
                localFn.Identifier.Text,
                CollectParameterDefaults(localFn, context));
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var localFn in localFns)
            {
                var current = context.MethodState.GetHoistedLocalFunctionCaptures(localFn.Identifier.Text);
                if (current == null)
                    continue;
                var expanded = new List<MethodConversionState.CapturedLocal>(current);
                bool added = false;
                var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
                if (body != null)
                {
                    foreach (var identifier in body
                        .DescendantNodes(d => d is not LocalFunctionStatementSyntax)
                        .OfType<IdentifierNameSyntax>())
                    {
                        if (identifier.Parent is NameColonSyntax)
                        {
                            continue;
                        }
                        if (context.GetSymbolInfo(identifier).Symbol is not IMethodSymbol { MethodKind: MethodKind.LocalFunction } callee)
                            continue;
                        var calleeCaptures = context.MethodState.GetHoistedLocalFunctionCaptures(callee.Name);
                        if (calleeCaptures == null)
                            continue;
                        foreach (var capture in calleeCaptures)
                        {
                            if (!expanded.Any(existing => SymbolEqualityComparer.Default.Equals(existing.Symbol, capture.Symbol)))
                            {
                                expanded.Add(capture);
                                added = true;
                            }
                        }
                    }
                }
                if (added)
                {
                    context.MethodState.RegisterHoistedLocalFunctionCaptures(localFn.Identifier.Text, expanded);
                    changed = true;
                }
            }
        }
    }

    /// <summary>
    /// Collects the Java literal default values of a local function's regular parameters
    /// (null entry = parameter has no default). Hoisted signatures keep no defaults, so
    /// call sites pad omitted trailing arguments from this list.
    /// </summary>
    internal static List<string?> CollectParameterDefaults(
        LocalFunctionStatementSyntax localFn,
        ConversionContext context)
    {
        var defaults = new List<string?>();
        if (localFn.ParameterList == null)
            return defaults;

        var symbol = context.GetDeclaredSymbol(localFn) as IMethodSymbol;
        foreach (var param in localFn.ParameterList.Parameters)
        {
            string? javaDefault = null;
            if (param.Default != null)
            {
                // Prefer the semantic default value when available (handles consts, enums, etc.).
                object? value = null;
                var paramSymbol = symbol?.Parameters.FirstOrDefault(p => p.Name == param.Identifier.Text);
                if (paramSymbol?.HasExplicitDefaultValue == true)
                {
                    value = paramSymbol.ExplicitDefaultValue;
                }
                if (value != null)
                {
                    javaDefault = ConvertDefaultValueToJavaLiteral(value);
                }
                else
                {
                    var raw = param.Default.Value.ToString();
                    javaDefault = raw switch
                    {
                        "null" => "null",
                        "true" => "true",
                        "false" => "false",
                        _ when raw.Length > 0 && (char.IsDigit(raw[0]) || raw[0] == '-') => raw,
                        _ => raw
                    };
                }
            }
            defaults.Add(javaDefault);
        }
        return defaults;
    }

    private static string ConvertDefaultValueToJavaLiteral(object? value)
    {
        return value switch
        {
            null => "null",
            string s => "\"" + CSharpToJava.Core.Utilities.StringEscapeHelper.EscapeJavaString(s) + "\"",
            char c => "'" + (c == '\'' ? "\\'" : c == '\\' ? "\\\\" : c.ToString()) + "'",
            bool b => b ? "true" : "false",
            long l => l + "L",
            int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            short sh => sh.ToString(System.Globalization.CultureInfo.InvariantCulture),
            byte by => by.ToString(System.Globalization.CultureInfo.InvariantCulture),
            uint ui => (ui > int.MaxValue
                ? ui + "L"
                : ui.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f",
            _ => value.ToString() ?? "null"
        };
    }

    /// <summary>
    /// Collects the enclosing-scope locals/parameters referenced by the local function
    /// body (excluding its own parameters and nested local functions). Empty when the
    /// function captures nothing. Const locals are compile-time constants and are not
    /// captures.
    /// </summary>
    internal static List<MethodConversionState.CapturedLocal> CollectLocalFunctionCaptures(
        LocalFunctionStatementSyntax localFn,
        ConversionContext context)
    {
        var captures = new List<MethodConversionState.CapturedLocal>();
        var localFnSymbol = context.GetDeclaredSymbol(localFn) as IMethodSymbol;
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (localFnSymbol == null || body == null)
            return captures;

        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        // Do not descend into nested local functions: their own parameters/locals
        // belong to the nested function, not to this one. Named-argument names
        // (e.g. `emitLengthCheck: false`) are parameter names, not variable references.
        foreach (var identifier in body
            .DescendantNodes(d => d is not LocalFunctionStatementSyntax)
            .OfType<IdentifierNameSyntax>())
        {
            if (identifier.Parent is NameColonSyntax)
            {
                continue;
            }
            var symbol = context.GetSymbolInfo(identifier).Symbol;
            switch (symbol)
            {
                case ILocalSymbol local when local.HasConstantValue:
                    // Const locals are compile-time constants — the hoisted method can
                    // reference the constant value directly, so they are not captures.
                    break;
                case ILocalSymbol or IParameterSymbol or IRangeVariableSymbol:
                    if (SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol, localFnSymbol))
                        break; // the local function's own parameters/locals
                    if (!seen.Add(symbol))
                        break;
                    var name = symbol.Name;
                    var symbolType = symbol switch
                    {
                        ILocalSymbol ls => ls.Type,
                        IParameterSymbol ps => ps.Type,
                        _ => null
                    };
                    var javaType = symbolType != null && symbolType.TypeKind != TypeKind.Error
                        ? context.MapType(symbolType)
                        : "Object";
                    if (string.IsNullOrWhiteSpace(javaType))
                        javaType = "Object";
                    captures.Add(new MethodConversionState.CapturedLocal(
                        symbol, name, javaType, "_cs2j_cap_" + name));
                    break;
            }
        }

        return captures;
    }
}
