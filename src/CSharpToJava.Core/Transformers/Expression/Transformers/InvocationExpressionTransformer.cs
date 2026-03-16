using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Transformers.Expression.Utilities;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles method invocation expressions.
/// </summary>
[TransformerRegistration]
public class InvocationExpressionTransformer : IExpressionTransformer
{
    static InvocationExpressionTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.InvocationExpression
        }, new InvocationExpressionTransformer());
    }

    private static readonly Lazy<InvocationExpressionTransformer> _instance = new(() => new());
    public static InvocationExpressionTransformer Instance => _instance.Value;

    // Maps C# type alias identifiers (without System. namespace) → Java primitive keyword.
    // Used in the syntactic fallback to handle Int32.Parse(), Int64.Parse(), etc.
    private static readonly Dictionary<string, string> _csharpAliasToJavaPrimitive = new()
    {
        ["Int32"]   = "int",
        ["Int64"]   = "long",
        ["Int16"]   = "short",
        ["Byte"]    = "byte",
        ["SByte"]   = "byte",
        ["Single"]  = "float",
        ["Double"]  = "double",
        ["Boolean"] = "boolean",
        ["Char"]    = "char",
    };

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.InvocationExpression => TransformInvocation((InvocationExpressionSyntax)node, context),
            _ => throw new NotSupportedException($"Invocation expression kind {node.Kind()} not supported.")
        };

    private string TransformInvocation(InvocationExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // Issue 6 — nameof(x) → "x" string literal; nameof(List<int>) → "List"
        if (node.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" } &&
            node.ArgumentList.Arguments.Count == 1)
        {
            return TransformNameof(node.ArgumentList.Arguments[0].Expression);
        }

        // Issue 1 & 5: member-access invocations need method-name mapping and
        // extension-receiver double-insertion guarding.
        if (node.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            return TransformMemberInvocation(node, memberAccess, context, facade);
        }

        // Fix: Explicit generic method call — C# Method<T, U>(args) → Java Method(args).
        // Java does not support specifying type arguments at the call site in statement position;
        // type inference is used instead.  Strip the type arguments from the method name.
        if (node.Expression is GenericNameSyntax genericMethodName)
        {
            var methodName = ApplyCamelCaseAndMappings(genericMethodName.Identifier.Text, node, context);
            var args = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
            return $"{methodName}({args})";
        }

        // Bare identifier call: e.g. LandmarkClassicalScaling(...) → landmarkClassicalScaling(...)
        // Apply the same camelCase + TypeMappings conversion used for member-access calls.
        if (node.Expression is IdentifierNameSyntax bareIdent)
        {
            // Delegate invocation: sequence(m) where sequence is a Func/Action field/local/param.
            // Roslyn resolves the invoked method as DelegateInvoke; map it to .apply()/.get()/etc.
            if (context.SemanticModel != null)
            {
                var symInfo = context.SemanticModel.GetSymbolInfo(node);
                if (symInfo.Symbol is IMethodSymbol { MethodKind: MethodKind.DelegateInvoke } delegateInvoke)
                {
                    var containingTypeName = delegateInvoke.ContainingType.ToDisplayString();
                    var javaMethod = context.TypeMappings.MapMethod(containingTypeName, "Invoke") ?? "apply";
                    var delegateArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
                    // Use facade.Transform so properties are emitted as getXxx() rather than bare identifier.
                    // e.g. Sequence(m) where Sequence is a Func<int,double> property → getSequence().apply(m)
                    var delegateReceiver = facade.Transform(bareIdent, context);
                    return $"{delegateReceiver}.{javaMethod}({delegateArgs})";
                }
            }

            var methodName = ApplyCamelCaseAndMappings(bareIdent.Identifier.Text, node, context);
            var args = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
            return $"{methodName}({args})";
        }

        var target = facade.Transform(node.Expression, context);
        var args2 = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
        return $"{target}({args2})";
    }

    /// <summary>
    /// Issue 6: nameof(expr) → Java string literal with the last dotted segment.
    /// Strips generic type arguments so nameof(List&lt;int&gt;) → "List".
    /// </summary>
    private static string TransformNameof(ExpressionSyntax argument)
    {
        var text = argument.ToString();
        var last = text.Split('.').Last();
        // Issue 6 fix: strip generic type arguments from the last segment
        var angleBracketIdx = last.IndexOf('<');
        if (angleBracketIdx >= 0)
            last = last[..angleBracketIdx];
        return $"\"{last}\"";
    }

    /// <summary>
    /// Applies TypeMappings lookup then camelCase conversion to a bare (unqualified) method name.
    /// Used for calls without a receiver: Foo(...) and Foo&lt;T&gt;(...).
    /// </summary>
    private static string ApplyCamelCaseAndMappings(string originalName, InvocationExpressionSyntax node, ConversionContext context)
    {
        var methodName = originalName;

        // Try TypeMappings via semantic model (receiver type required for lookup, skip if unavailable)
        if (context.SemanticModel?.GetSymbolInfo(node).Symbol is IMethodSymbol sym)
        {
            var typeName = sym.ContainingType.ToDisplayString();
            var mapped = context.TypeMappings.MapMethod(typeName, originalName);
            if (mapped != null)
                return ConversionContext.EscapeJavaKeyword(mapped);
        }

        // Apply the same well-known renames + camelCase used in TransformMemberInvocation
        methodName = methodName switch
        {
            "GetHashCode"   => "hashCode",
            "GetEnumerator" => "iterator",
            "GetType"       => "getClass",
            "Dispose"       => "close",
            _ when methodName.Length > 0
                => char.ToLowerInvariant(methodName[0]) + methodName[1..],
            _ => methodName
        };

        return ConversionContext.EscapeJavaKeyword(methodName);
    }

    /// <summary>
    /// Issue 1: Applies method-name mapping from TypeMappings at the call site.
    /// Issue 5: Guards against double-insertion of the extension method receiver in the static-call path.
    /// Issue 4: Delegates to ArgumentTransformer's indexed-loop implementation (no O(n²) IndexOf).
    /// </summary>
    private static string TransformMemberInvocation(
        InvocationExpressionSyntax node,
        MemberAccessExpressionSyntax memberAccess,
        ConversionContext context,
        ExpressionTransformerFacade facade)
    {
        // Fix: Generic type static method call — C# DemoSet<T>.Method() → Java DemoSet.Method().
        // Java forbids type arguments on the class name at a static call site; strip them.
        var receiver = memberAccess.Expression is GenericNameSyntax genericReceiverName
            ? ConversionContext.EscapeJavaKeyword(genericReceiverName.Identifier.Text)
            : facade.Transform(memberAccess.Expression, context);
        var originalMethodName = memberAccess.Name.Identifier.Text;

        // Fix: Primitive type static method call — C# double.IsInfinity(x) → Java Double.isInfinite(x).
        if (memberAccess.Expression is PredefinedTypeSyntax primTypeSyntax)
        {
            var boxedReceiver = ExpressionTransformerHelpers.BoxedTypeName(primTypeSyntax);
            var mappedMethod  = MapPrimitiveStaticMethodName(primTypeSyntax.Keyword.Text, originalMethodName);
            var primArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
            return $"{boxedReceiver}.{mappedMethod}({primArgs})";
        }

        IMethodSymbol? methodSymbol = null;
        bool isExtensionInStaticPath = false;

        if (context.SemanticModel != null)
        {
            var symbolInfo = context.SemanticModel.GetSymbolInfo(node);
            methodSymbol = symbolInfo.Symbol as IMethodSymbol;

            // Issue 5: detect reduced extension method; set isExtensionInStaticPath = true
            // when promoting to a static call so the receiver is not double-passed as arg[0].
            // Currently instance-call form is kept, so isExtensionInStaticPath stays false.
            if (methodSymbol is { IsExtensionMethod: true, MethodKind: MethodKind.ReducedExtension })
                isExtensionInStaticPath = false;
        }

        // Fix: First()/Last() on arrays → indexed access (arrays are not streams).
        // String.split() returns String[] in Java; arrays do not have stream terminal ops
        // like findFirst()/reduce(). Use indexed access instead.
        if (originalMethodName is "First" or "FirstOrDefault" or "Last" or "LastOrDefault"
            && context.SemanticModel != null)
        {
            var receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression).Type;
            if (receiverType is IArrayTypeSymbol)
            {
                return originalMethodName is "First" or "FirstOrDefault"
                    ? $"{receiver}[0]"
                    : $"{receiver}[{receiver}.length - 1]";
            }
        }

        // Fix: Any() with no arguments on IEnumerable<T> → receiver.iterator().hasNext().
        // anyMatch(Predicate) is a Stream<T> terminal op and must not be emitted on Iterable<T>.
        // Any(predicate) with arguments is handled by the LinqRewriter (rewritten to a for-loop).
        if (originalMethodName == "Any"
            && node.ArgumentList.Arguments.Count == 0
            && methodSymbol?.ContainingType.ToDisplayString() == "System.Linq.Enumerable")
        {
            return $"{receiver}.iterator().hasNext()";
        }

        // Issue 1: apply method-name mapping from the type-mapping registry.
        string methodName = originalMethodName;
        if (methodSymbol != null)
        {
            // ToDisplayString() uses C# keyword aliases for primitive types:
            // e.g. System.Int32 → "int", System.Int64 → "long".
            // TypeMappings.json uses the fully-qualified "System.Int32" form,
            // so the lookup with the keyword alias would miss. Try the FQN as a fallback.
            var receiverTypeName = methodSymbol.ContainingType.ToDisplayString();
            var mapped = context.TypeMappings.MapMethod(receiverTypeName, originalMethodName);
            if (mapped == null)
            {
                var fqn = $"{methodSymbol.ContainingType.ContainingNamespace}.{methodSymbol.ContainingType.Name}";
                mapped = context.TypeMappings.MapMethod(fqn, originalMethodName);
            }
            if (mapped != null)
                methodName = mapped;
        }

        // Fix: Static type receiver remapping — e.g. System.Console → System.
        // When the receiver expression resolves to a named type symbol (static call site),
        // replace the syntactically-derived receiver string with the TypeMappings Java name
        // so that System.Console.WriteLine(x) → System.out.println(x).
        // Guard: skip when the receiver is a GenericNameSyntax — it was already correctly
        // stripped of its type arguments by the fix above (e.g. DemoSet<string> → DemoSet),
        // and MapType on the containing type would re-introduce them (DemoSet<T>).
        if (methodSymbol != null && context.SemanticModel != null
            && memberAccess.Expression is not GenericNameSyntax)
        {
            var receiverExprSymbol = context.SemanticModel.GetSymbolInfo(memberAccess.Expression).Symbol;
            if (receiverExprSymbol is INamedTypeSymbol)
            {
                var containingTypeName = methodSymbol.ContainingType.ToDisplayString();
                receiver = context.TypeMappings.MapType(containingTypeName);
                // Box any primitive type so static methods are called on the wrapper class.
                // e.g. System.Int32 maps to "int", but Int32.Parse → Integer.parseInt not int.parseInt.
                receiver = ExpressionTransformerHelpers.BoxJavaPrimitiveType(receiver);
            }
        }
        else if (methodSymbol == null)
        {
            // Syntactic fallback: when the semantic model could not resolve the method (e.g. missing
            // assembly reference), try mapping using the raw syntactic receiver string.  This handles
            // System.Console.WriteLine → System.out.println even without a full Roslyn compilation.
            var syntacticReceiver = memberAccess.Expression.ToString();
            var syntacticMapped = context.TypeMappings.MapMethod(syntacticReceiver, originalMethodName);
            if (syntacticMapped != null)
            {
                methodName = syntacticMapped;
                var mappedReceiverType = context.TypeMappings.MapType(syntacticReceiver);
                if (mappedReceiverType != syntacticReceiver)
                    receiver = mappedReceiverType;
            }

            // Handle C# type alias identifiers (Int32, Int64, etc.) that appear without a namespace.
            // TypeMappings uses "System.Int32" keys, so the syntactic lookup above misses these.
            if (methodName == originalMethodName
                && _csharpAliasToJavaPrimitive.TryGetValue(syntacticReceiver, out var primitiveForAlias))
            {
                var aliasMethod = MapPrimitiveStaticMethodName(primitiveForAlias, originalMethodName);
                if (aliasMethod != originalMethodName)
                {
                    methodName = aliasMethod;
                    receiver = ExpressionTransformerHelpers.BoxJavaPrimitiveType(primitiveForAlias);
                }
            }
        }

        // Apply the same camelCase conversion at call sites that MethodTransformer applies at
        // declaration sites.  Only runs when no explicit TypeMappings override was found so that
        // hand-crafted renames (e.g. Add → add) are never double-processed.
        if (methodName == originalMethodName)
        {
            methodName = methodName switch
            {
                "GetHashCode"   => "hashCode",
                "GetEnumerator" => "iterator",
                "GetType"       => "getClass",
                "Dispose"       => "close",
                _ when methodName.Length > 0
                    => char.ToLowerInvariant(methodName[0]) + methodName[1..],
                _ => methodName
            };
        }

        methodName = ConversionContext.EscapeJavaKeyword(methodName);

        // Issue 5: when promoting to static-call form, start at index 1 to skip the receiver
        // that was already prepended; use 0 for standard instance calls.
        int argStartIndex = isExtensionInStaticPath ? 1 : 0;

        // Fix: String.Split(' ') → Java split(" ") — Java's split() takes a String regex, not char.
        // Convert any char literal arguments to their regex-string equivalents.
        if (originalMethodName == "Split" && node.ArgumentList.Arguments.Count > argStartIndex)
        {
            var splitArgs = TransformSplitArguments(node.ArgumentList, context, facade, argStartIndex);
            return $"{receiver}.{methodName}({splitArgs})";
        }

        var args = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, argStartIndex);

        return $"{receiver}.{methodName}({args})";
    }

    /// <summary>
    /// Transforms the argument list for String.Split(), converting char literal arguments
    /// to string literals suitable for Java's split() regex parameter.
    /// e.g. ' ' → " ", '.' → "\\."
    /// </summary>
    private static string TransformSplitArguments(
        ArgumentListSyntax argumentList,
        ConversionContext context,
        ExpressionTransformerFacade facade,
        int argStartIndex)
    {
        var parts = new List<string>();
        var arguments = argumentList.Arguments;
        for (int i = argStartIndex; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if (arg.Expression is LiteralExpressionSyntax charLit
                && charLit.IsKind(SyntaxKind.CharacterLiteralExpression))
            {
                parts.Add($"\"{EscapeRegexChar(charLit.Token.ValueText)}\"");
            }
            else
            {
                parts.Add(facade.Transform(arg.Expression, context));
            }
        }
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Escapes a single character for use as a literal pattern in Java's String.split() regex.
    /// Regex metacharacters are prefixed with a backslash so they match literally.
    /// </summary>
    private static string EscapeRegexChar(string ch)
    {
        if (ch.Length == 1 && @"\.^$*+?{}[]|()".Contains(ch[0]))
            return @"\" + ch;
        return ch;
    }

    /// <summary>
    /// Maps C# built-in primitive static method names to their Java equivalents.
    /// e.g. double.IsInfinity → Double.isInfinite, int.Parse → Integer.parseInt
    /// </summary>
    private static string MapPrimitiveStaticMethodName(string primitiveKeyword, string methodName)
        => methodName switch
        {
            "IsInfinity" or "IsPositiveInfinity" or "IsNegativeInfinity" => "isInfinite",
            "IsNaN"    => "isNaN",
            "IsFinite" => "isFinite",
            "Parse"    => primitiveKeyword switch
            {
                "int"    => "parseInt",
                "long"   => "parseLong",
                "double" => "parseDouble",
                "float"  => "parseFloat",
                "short"  => "parseShort",
                "byte"   => "parseByte",
                _        => "parse" + char.ToUpperInvariant(primitiveKeyword[0]) + primitiveKeyword[1..]
            },
            _ => methodName
        };
}
