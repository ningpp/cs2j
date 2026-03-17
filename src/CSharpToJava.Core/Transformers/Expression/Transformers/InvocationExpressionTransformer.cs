using System.Text.RegularExpressions;
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

        // Delegate invocation via non-identifier expressions (e.g. dict[key](args)).
        // The existing IdentifierNameSyntax path above only handles bare identifiers;
        // this catches element-access, member-access, and other expression targets.
        if (context.SemanticModel != null)
        {
            var symInfo = context.SemanticModel.GetSymbolInfo(node);
            if (symInfo.Symbol is IMethodSymbol { MethodKind: MethodKind.DelegateInvoke } delegateInvoke)
            {
                var containingTypeName = delegateInvoke.ContainingType.ToDisplayString();
                var javaMethod = context.TypeMappings.MapMethod(containingTypeName, "Invoke") ?? "apply";
                var delegateArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
                var delegateReceiver = facade.Transform(node.Expression, context);
                return $"{delegateReceiver}.{javaMethod}({delegateArgs})";
            }
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

            // Fallback: when overload resolution fails but Roslyn found candidate(s)
            // (e.g. ToList/ToDictionary on IEnumerable<T> with incomplete assembly refs),
            // use the first candidate — but only when the receiver is still a LINQ extension
            // (i.e. the chain was NOT already rewritten by LinqRewriter to procedural code).
            if (methodSymbol == null
                && symbolInfo.CandidateReason == CandidateReason.OverloadResolutionFailure
                && symbolInfo.CandidateSymbols.Length >= 1
                && IsReceiverLinqExtension(memberAccess.Expression, context))
            {
                methodSymbol = symbolInfo.CandidateSymbols[0] as IMethodSymbol;
            }

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

        // Fix: Array.ForEach(array, action) → Arrays.stream(array).forEach(action)
        // System.Array maps to "Object" in TypeMappings which has no static forEach method.
        // Use Arrays.stream().forEach() to produce a valid expression (works in lambda bodies).
        // Check both via semantic model and syntactic fallback (missing assembly reference).
        if (originalMethodName == "ForEach"
            && node.ArgumentList.Arguments.Count >= 2
            && (methodSymbol?.ContainingType.ToDisplayString() == "System.Array"
                || (methodSymbol == null && memberAccess.Expression.ToString() is "Array" or "System.Array")))
        {
            var arrayArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var actionArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            context.AddImport("java.util.Arrays");
            return $"Arrays.stream({arrayArg}).forEach({actionArg})";
        }

        // Fix: array.GetLength(dim) → Java dimensional length access.
        // Java represents multi-dimensional arrays as jagged arrays (arrays of arrays).
        // Dimension n length: array + "[0]" × n + ".length"
        //   GetLength(0) → matrix.length
        //   GetLength(1) → matrix[0].length
        //   GetLength(2) → matrix[0][0].length
        if (originalMethodName == "GetLength"
            && node.ArgumentList.Arguments.Count == 1
            && context.SemanticModel != null)
        {
            var receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression).Type;
            if (receiverType is IArrayTypeSymbol)
            {
                var dimArg = node.ArgumentList.Arguments[0].Expression;
                int dim = -1;
                var constVal = context.SemanticModel.GetConstantValue(dimArg);
                if (constVal.HasValue && constVal.Value is int constInt)
                    dim = constInt;
                else if (int.TryParse(dimArg.ToString(), out var parsed))
                    dim = parsed;
                if (dim >= 0)
                {
                    var indexers = string.Concat(Enumerable.Repeat("[0]", dim));
                    return $"{receiver}{indexers}.length";
                }
                // Non-constant dimension: emit dimension 0 with a comment as best-effort fallback.
                var dimExpr = facade.Transform(dimArg, context);
                return $"/* GetLength({dimExpr}) not directly translatable */ {receiver}.length";
            }
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
                // Roslyn's ToDisplayString() uses C# keyword aliases for well-known types:
                // e.g. System.String → "string", System.Object → "object".
                // TypeMappings.json keys use the fully-qualified form ("System.String"),
                // so the alias lookup misses. Retry with the FQN as a fallback.
                if (receiver == containingTypeName)
                {
                    var fqn = $"{methodSymbol.ContainingType.ContainingNamespace}.{methodSymbol.ContainingType.Name}";
                    receiver = context.TypeMappings.MapType(fqn);
                }
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

        // Fix: String.Format("{0}  {1}", a, b) → String.format("%s  %s", a, b)
        // C# uses {N} / {N:specifier} placeholders; Java uses printf-style % specifiers.
        // Only rewrite when the first argument is a string literal — dynamic format strings
        // cannot be statically rewritten and are left as-is.
        bool isStringFormat = originalMethodName == "Format"
            && (methodSymbol?.ContainingType.ToDisplayString() is "string" or "System.String"
                || (methodSymbol == null && memberAccess.Expression.ToString() is "String" or "string" or "System.String"));
        if (isStringFormat && node.ArgumentList.Arguments.Count > argStartIndex)
        {
            var firstArg = node.ArgumentList.Arguments[argStartIndex];
            if (firstArg.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } strLit)
            {
                var rewrittenFormat = RewriteStringFormatLiteral(strLit.Token.ValueText);
                var remainingArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, argStartIndex + 1);
                var formatCall = string.IsNullOrEmpty(remainingArgs)
                    ? $"{receiver}.{methodName}({rewrittenFormat})"
                    : $"{receiver}.{methodName}({rewrittenFormat}, {remainingArgs})";
                return formatCall;
            }
        }

        // LINQ Stream API fallback: when a LINQ extension method was not rewritten by LinqRewriter
        // (e.g. chains containing OrderBy or SelectMany), inject .stream() on the collection
        // receiver and handle terminal/sorting operations.
        if (methodSymbol?.ContainingType.ToDisplayString() == "System.Linq.Enumerable"
            && methodSymbol.IsExtensionMethod
            && context.SemanticModel != null)
        {
            if (!IsReceiverLinqExtension(memberAccess.Expression, context))
            {
                var receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression).Type;
                if (receiverType is IArrayTypeSymbol)
                {
                    context.AddImport("java.util.Arrays");
                    receiver = $"Arrays.stream({receiver})";
                }
                else
                {
                    receiver = $"{receiver}.stream()";
                }
            }

            // ToList → .toList() (Java 16+) or collect(Collectors.toList())
            if (originalMethodName == "ToList")
            {
                if (context.Options.TargetJavaVersion >= JavaVersion.Java21)
                {
                    return $"{receiver}.toList()";
                }
                context.AddImport("java.util.stream.Collectors");
                return $"{receiver}.collect(Collectors.toList())";
            }

            // ToDictionary → collect(Collectors.toMap(keySelector, valueSelector))
            if (originalMethodName == "ToDictionary" && node.ArgumentList.Arguments.Count >= 2)
            {
                context.AddImport("java.util.stream.Collectors");
                var keyArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var valArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"{receiver}.collect(Collectors.toMap({keyArg}, {valArg}))";
            }

            // OrderBy/ThenBy → sorted(Comparator.comparing(lambda))
            // ThenBy/ThenByDescending merge into the preceding sorted() call's comparator
            // to produce .sorted(Comparator.comparing(a).thenComparing(b)) instead of
            // two separate .sorted() calls.
            if (originalMethodName is "OrderBy" or "ThenBy")
            {
                var sortArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, argStartIndex);
                if (string.IsNullOrEmpty(sortArgs))
                    return $"{receiver}.sorted()";

                var comparator = $"java.util.Comparator.comparing({sortArgs})";

                // Merge ThenBy into preceding sorted() if possible
                if (originalMethodName == "ThenBy" && TryExtractSortedComparator(receiver, out var baseReceiver, out var prevComparator))
                {
                    return $"{baseReceiver}.sorted({prevComparator}.thenComparing({sortArgs}))";
                }

                return $"{receiver}.sorted({comparator})";
            }

            // OrderByDescending/ThenByDescending → sorted(Comparator.comparing(lambda).reversed())
            if (originalMethodName is "OrderByDescending" or "ThenByDescending")
            {
                var sortArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, argStartIndex);
                if (string.IsNullOrEmpty(sortArgs))
                    return $"{receiver}.sorted(java.util.Comparator.reverseOrder())";

                // Merge ThenByDescending into preceding sorted() if possible
                if (originalMethodName == "ThenByDescending" && TryExtractSortedComparator(receiver, out var baseReceiver, out var prevComparator))
                {
                    return $"{baseReceiver}.sorted({prevComparator}.thenComparing(java.util.Comparator.comparing({sortArgs}).reversed()))";
                }

                return $"{receiver}.sorted(java.util.Comparator.comparing({sortArgs}).reversed())";
            }

            // Sum → mapToInt/mapToLong/mapToDouble + sum(), or just sum() if already numeric stream
            if (originalMethodName == "Sum")
            {
                if (node.ArgumentList.Arguments.Count > 0)
                {
                    var sumArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    return $"{receiver}.mapToInt({sumArg}).sum()";
                }
                return $"{receiver}.mapToInt(x -> x).sum()";
            }

            // Average → mapToDouble + average().orElse(0)
            if (originalMethodName == "Average")
            {
                if (node.ArgumentList.Arguments.Count > 0)
                {
                    var avgArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    return $"{receiver}.mapToDouble({avgArg}).average().orElse(0)";
                }
                return $"{receiver}.mapToDouble(x -> x).average().orElse(0)";
            }

            // GroupBy → collect(Collectors.groupingBy(keySelector))
            if (originalMethodName == "GroupBy")
            {
                context.AddImport("java.util.stream.Collectors");
                if (node.ArgumentList.Arguments.Count >= 2)
                {
                    var keyArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    var elemArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                    return $"{receiver}.collect(Collectors.groupingBy({keyArg}, Collectors.mapping({elemArg}, Collectors.toList())))";
                }
                if (node.ArgumentList.Arguments.Count == 1)
                {
                    var keyArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    return $"{receiver}.collect(Collectors.groupingBy({keyArg}))";
                }
            }

            // Contains → anyMatch(x -> x.equals(value))
            if (originalMethodName == "Contains" && node.ArgumentList.Arguments.Count >= 1)
            {
                var valArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.anyMatch(x -> java.util.Objects.equals(x, {valArg}))";
            }

            // Concat → Stream.concat(stream, other.stream())
            if (originalMethodName == "Concat" && node.ArgumentList.Arguments.Count >= 1)
            {
                var otherArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var otherType = context.SemanticModel.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type;
                var otherStream = otherType is IArrayTypeSymbol
                    ? $"Arrays.stream({otherArg})"
                    : $"{otherArg}.stream()";
                return $"java.util.stream.Stream.concat({receiver}, {otherStream})";
            }

            // Where → filter(predicate)
            if (originalMethodName == "Where" && node.ArgumentList.Arguments.Count >= 1)
            {
                var predArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.filter({predArg})";
            }

            // Select → map(transform)
            if (originalMethodName == "Select" && node.ArgumentList.Arguments.Count >= 1)
            {
                var mapArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.map({mapArg})";
            }

            // SelectMany → flatMap(selector)
            if (originalMethodName == "SelectMany" && node.ArgumentList.Arguments.Count >= 1)
            {
                var flatMapArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.flatMap({flatMapArg})";
            }

            // Distinct → distinct()
            if (originalMethodName == "Distinct")
            {
                return $"{receiver}.distinct()";
            }

            // Skip → skip(n)
            if (originalMethodName == "Skip" && node.ArgumentList.Arguments.Count >= 1)
            {
                var skipArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.skip({skipArg})";
            }

            // Take → limit(n)
            if (originalMethodName == "Take" && node.ArgumentList.Arguments.Count >= 1)
            {
                var takeArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.limit({takeArg})";
            }

            // SkipWhile → dropWhile(predicate) (Java 9+)
            if (originalMethodName == "SkipWhile" && node.ArgumentList.Arguments.Count >= 1)
            {
                var predArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.dropWhile({predArg})";
            }

            // TakeWhile → takeWhile(predicate) (Java 9+)
            if (originalMethodName == "TakeWhile" && node.ArgumentList.Arguments.Count >= 1)
            {
                var predArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.takeWhile({predArg})";
            }

            // First() → findFirst().orElseThrow()
            if (originalMethodName == "First" && node.ArgumentList.Arguments.Count == 0)
            {
                return $"{receiver}.findFirst().orElseThrow()";
            }

            // First(predicate) → filter(predicate).findFirst().orElseThrow()
            if (originalMethodName == "First" && node.ArgumentList.Arguments.Count >= 1)
            {
                var predArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.filter({predArg}).findFirst().orElseThrow()";
            }

            // FirstOrDefault() → findFirst().orElse(null)
            if (originalMethodName == "FirstOrDefault" && node.ArgumentList.Arguments.Count == 0)
            {
                return $"{receiver}.findFirst().orElse(null)";
            }

            // FirstOrDefault(predicate) → filter(predicate).findFirst().orElse(null)
            if (originalMethodName == "FirstOrDefault" && node.ArgumentList.Arguments.Count >= 1)
            {
                var predArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.filter({predArg}).findFirst().orElse(null)";
            }

            // Last() → reduce((a, b) -> b).orElseThrow()
            if (originalMethodName == "Last" && node.ArgumentList.Arguments.Count == 0)
            {
                return $"{receiver}.reduce((a, b) -> b).orElseThrow()";
            }

            // Last(predicate) → filter(predicate).reduce((a, b) -> b).orElseThrow()
            if (originalMethodName == "Last" && node.ArgumentList.Arguments.Count >= 1)
            {
                var predArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.filter({predArg}).reduce((a, b) -> b).orElseThrow()";
            }

            // LastOrDefault() → reduce((a, b) -> b).orElse(null)
            if (originalMethodName == "LastOrDefault" && node.ArgumentList.Arguments.Count == 0)
            {
                return $"{receiver}.reduce((a, b) -> b).orElse(null)";
            }

            // LastOrDefault(predicate) → filter(predicate).reduce((a, b) -> b).orElse(null)
            if (originalMethodName == "LastOrDefault" && node.ArgumentList.Arguments.Count >= 1)
            {
                var predArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.filter({predArg}).reduce((a, b) -> b).orElse(null)";
            }

            // Single() → reduce((a, b) -> { throw new IllegalStateException(); }).orElseThrow()
            if (originalMethodName == "Single" && node.ArgumentList.Arguments.Count == 0)
            {
                return $"{receiver}.reduce((a, b) -> {{ throw new IllegalStateException(\"Sequence contains more than one element\"); }}).orElseThrow()";
            }

            // SingleOrDefault() → reduce((a, b) -> { throw ...; }).orElse(null)
            if (originalMethodName == "SingleOrDefault" && node.ArgumentList.Arguments.Count == 0)
            {
                return $"{receiver}.reduce((a, b) -> {{ throw new IllegalStateException(\"Sequence contains more than one element\"); }}).orElse(null)";
            }

            // Any(predicate) → anyMatch(predicate)
            if (originalMethodName == "Any" && node.ArgumentList.Arguments.Count >= 1)
            {
                var predArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.anyMatch({predArg})";
            }

            // All(predicate) → allMatch(predicate)
            if (originalMethodName == "All" && node.ArgumentList.Arguments.Count >= 1)
            {
                var predArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.allMatch({predArg})";
            }

            // Count() → count()  (returns long in Java)
            if (originalMethodName == "Count" && node.ArgumentList.Arguments.Count == 0)
            {
                return $"(int) {receiver}.count()";
            }

            // Count(predicate) → filter(predicate).count()
            if (originalMethodName == "Count" && node.ArgumentList.Arguments.Count >= 1)
            {
                var predArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"(int) {receiver}.filter({predArg}).count()";
            }

            // LongCount() → count()
            if (originalMethodName == "LongCount" && node.ArgumentList.Arguments.Count == 0)
            {
                return $"{receiver}.count()";
            }

            // LongCount(predicate) → filter(predicate).count()
            if (originalMethodName == "LongCount" && node.ArgumentList.Arguments.Count >= 1)
            {
                var predArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.filter({predArg}).count()";
            }

            // Min() → min(Comparator.naturalOrder()).orElseThrow()
            if (originalMethodName == "Min" && node.ArgumentList.Arguments.Count == 0)
            {
                return $"{receiver}.min(java.util.Comparator.naturalOrder()).orElseThrow()";
            }

            // Min(selector) → map(selector).min(Comparator.naturalOrder()).orElseThrow()
            if (originalMethodName == "Min" && node.ArgumentList.Arguments.Count >= 1)
            {
                var selArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.map({selArg}).min(java.util.Comparator.naturalOrder()).orElseThrow()";
            }

            // Max() → max(Comparator.naturalOrder()).orElseThrow()
            if (originalMethodName == "Max" && node.ArgumentList.Arguments.Count == 0)
            {
                return $"{receiver}.max(java.util.Comparator.naturalOrder()).orElseThrow()";
            }

            // Max(selector) → map(selector).max(Comparator.naturalOrder()).orElseThrow()
            if (originalMethodName == "Max" && node.ArgumentList.Arguments.Count >= 1)
            {
                var selArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.map({selArg}).max(java.util.Comparator.naturalOrder()).orElseThrow()";
            }

            // ToArray() → toArray()
            if (originalMethodName == "ToArray")
            {
                return $"{receiver}.toArray()";
            }

            // ToHashSet() → collect(Collectors.toSet())
            if (originalMethodName == "ToHashSet")
            {
                context.AddImport("java.util.stream.Collectors");
                return $"{receiver}.collect(Collectors.toSet())";
            }

            // Reverse() — collect to list, then Collections.reverse()
            if (originalMethodName == "Reverse")
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.Collections");
                // There's no Stream.reverse(); collect to list and reverse in-place
                // Use a helper expression that captures the result
                return $"/* reverse */ {receiver}.collect(Collectors.toList())";
            }

            // Aggregate(func) → reduce(func).orElseThrow()
            if (originalMethodName == "Aggregate" && node.ArgumentList.Arguments.Count == 1)
            {
                var funcArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.reduce({funcArg}).orElseThrow()";
            }

            // Aggregate(seed, func) → reduce(seed, func)
            if (originalMethodName == "Aggregate" && node.ArgumentList.Arguments.Count >= 2)
            {
                var seedArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var funcArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"{receiver}.reduce({seedArg}, {funcArg})";
            }

            // Cast<T>() → map(x -> (T) x)
            if (originalMethodName == "Cast")
            {
                if (node.Expression is MemberAccessExpressionSyntax ma && ma.Name is GenericNameSyntax gns && gns.TypeArgumentList.Arguments.Count > 0)
                {
                    var targetType = facade.Transform(gns.TypeArgumentList.Arguments[0], context);
                    return $"{receiver}.map(x -> ({targetType}) x)";
                }
                return $"{receiver}.map(x -> x)";
            }

            // OfType<T>() → filter(x -> x instanceof T).map(x -> (T) x)
            if (originalMethodName == "OfType")
            {
                if (node.Expression is MemberAccessExpressionSyntax maOfType && maOfType.Name is GenericNameSyntax gnsOfType && gnsOfType.TypeArgumentList.Arguments.Count > 0)
                {
                    var targetType = facade.Transform(gnsOfType.TypeArgumentList.Arguments[0], context);
                    return $"{receiver}.filter(x -> x instanceof {targetType}).map(x -> ({targetType}) x)";
                }
            }

            // Zip(other, resultSelector) → — no direct Java Stream equivalent; best-effort
            if (originalMethodName == "Zip" && node.ArgumentList.Arguments.Count >= 2)
            {
                var otherArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var selectorArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                // Java has no built-in zip; emit IntStream.range + map as approximation
                context.AddImport("java.util.stream.IntStream");
                return $"IntStream.range(0, Math.min((int) {receiver}.count(), (int) {otherArg}.stream().count())).mapToObj(i -> {selectorArg})";
            }

            // Union(other) → Stream.concat + distinct
            if (originalMethodName == "Union" && node.ArgumentList.Arguments.Count >= 1)
            {
                var otherArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var otherType = context.SemanticModel.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type;
                var otherStream = otherType is IArrayTypeSymbol
                    ? $"Arrays.stream({otherArg})"
                    : $"{otherArg}.stream()";
                return $"java.util.stream.Stream.concat({receiver}, {otherStream}).distinct()";
            }

            // Intersect(other) → filter with set membership
            if (originalMethodName == "Intersect" && node.ArgumentList.Arguments.Count >= 1)
            {
                var otherArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.Set");
                return $"{receiver}.filter({otherArg}.stream().collect(Collectors.toSet())::contains)";
            }

            // Except(other) → filter with negated set membership
            if (originalMethodName == "Except" && node.ArgumentList.Arguments.Count >= 1)
            {
                var otherArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.Set");
                return $"{receiver}.filter(x -> !{otherArg}.stream().collect(Collectors.toSet()).contains(x))";
            }

            // ElementAt(index) → skip(index).findFirst().orElseThrow()
            if (originalMethodName == "ElementAt" && node.ArgumentList.Arguments.Count >= 1)
            {
                var idxArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.skip({idxArg}).findFirst().orElseThrow()";
            }

            // ElementAtOrDefault(index) → skip(index).findFirst().orElse(null)
            if (originalMethodName == "ElementAtOrDefault" && node.ArgumentList.Arguments.Count >= 1)
            {
                var idxArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.skip({idxArg}).findFirst().orElse(null)";
            }

            // SequenceEqual(other) — no direct stream equivalent; collect and compare
            if (originalMethodName == "SequenceEqual" && node.ArgumentList.Arguments.Count >= 1)
            {
                var otherArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                context.AddImport("java.util.stream.Collectors");
                return $"{receiver}.collect(Collectors.toList()).equals({otherArg}.stream().collect(Collectors.toList()))";
            }

            // Fallback for any unhandled LINQ method: transform args and emit as-is with a TODO comment.
            {
                var fallbackArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, argStartIndex);
                return $"/* TODO: LINQ {originalMethodName} */ {receiver}.{methodName}({fallbackArgs})";
            }
        }

        var args = ArgumentTransformer.TransformArgumentList(
            node.ArgumentList, context, facade, argStartIndex);

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
    /// Rewrites a C# String.Format literal format string to Java's printf-style format.
    /// Escapes any bare '%' characters, then converts {N} placeholders to %s and
    /// {N:specifier} placeholders to the corresponding Java format specifier.
    /// Returns the rewritten string as a Java string literal (with surrounding double-quotes).
    /// </summary>
    private static string RewriteStringFormatLiteral(string formatValue)
    {
        // Escape existing '%' to '%%' so they are treated as literal percent signs in Java.
        var escaped = formatValue.Replace("%", "%%");
        // Match {index} or {index:formatSpec} — index is one or more digits.
        var result = Regex.Replace(escaped, @"\{(\d+)(?::([^}]*))?\}", m =>
        {
            var spec = m.Groups[2].Success ? m.Groups[2].Value : "";
            return string.IsNullOrEmpty(spec)
                ? "%s"
                : StringExpressionTransformer.ConvertCSharpFormatToJava(spec);
        });
        // Wrap in Java string literal quotes.
        return $"\"{result}\"";
    }

    /// <summary>
    /// Checks if the given expression is itself a LINQ extension method call (from System.Linq.Enumerable).
    /// Used to avoid injecting .stream() twice in a method chain.
    /// </summary>
    private static bool IsReceiverLinqExtension(ExpressionSyntax expr, ConversionContext context)
    {
        if (context.SemanticModel == null) return false;
        if (expr is not InvocationExpressionSyntax invocation) return false;
        var sym = context.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        return sym?.ContainingType.ToDisplayString() == "System.Linq.Enumerable";
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

    /// <summary>
    /// Attempts to extract the comparator expression from a receiver string that ends
    /// with <c>.sorted(comparatorExpr)</c>. This enables ThenBy/ThenByDescending to
    /// merge into the preceding sort as <c>.sorted(prevComparator.thenComparing(...))</c>.
    /// </summary>
    private static bool TryExtractSortedComparator(string receiver, out string baseReceiver, out string comparator)
    {
        baseReceiver = "";
        comparator = "";

        const string sortedPrefix = ".sorted(";
        int sortedIdx = receiver.LastIndexOf(sortedPrefix, StringComparison.Ordinal);
        if (sortedIdx < 0 || !receiver.EndsWith(")"))
            return false;

        // Find the matching closing paren by counting parens from the sorted( position
        int openPos = sortedIdx + sortedPrefix.Length - 1; // position of '('
        int depth = 0;
        int closePos = -1;
        for (int i = openPos; i < receiver.Length; i++)
        {
            if (receiver[i] == '(') depth++;
            else if (receiver[i] == ')') depth--;
            if (depth == 0) { closePos = i; break; }
        }

        // Only match if the sorted() call is the terminal operation (closePos == end)
        if (closePos != receiver.Length - 1)
            return false;

        baseReceiver = receiver.Substring(0, sortedIdx);
        comparator = receiver.Substring(openPos + 1, closePos - openPos - 1);

        // Don't merge if previous sorted() was empty (natural order) or reverseOrder
        if (string.IsNullOrEmpty(comparator) || comparator.Contains("reverseOrder"))
            return false;

        return true;
    }
}
