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
            var bareMethodSym = context.SemanticModel?.GetSymbolInfo(node).Symbol as IMethodSymbol;
            var args = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, methodSymbol: bareMethodSym);
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
            var bareMethodSym2 = context.SemanticModel?.GetSymbolInfo(node).Symbol as IMethodSymbol;
            var args = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, methodSymbol: bareMethodSym2);
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
        var fallbackMethodSym = context.SemanticModel?.GetSymbolInfo(node).Symbol as IMethodSymbol;
        var args2 = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, methodSymbol: fallbackMethodSym);
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

        // Fix: Delegate invocation via member access (this.sequence(i), this.Sequence(i), obj.cb(x)).
        // Roslyn reports MethodKind.DelegateInvoke when the accessed member is a Func/Action/delegate.
        // `receiver` is the LHS (e.g. "this"); `originalMethodName` is the member name (field or property).
        // For fields:     this.sequence(i) → this.sequence.apply(i)
        // For properties: this.Sequence(i) → this.getSequence().apply(i)  (Java getter convention)
        if (context.SemanticModel != null)
        {
            var delegateSymInfo = context.SemanticModel.GetSymbolInfo(node);
            if (delegateSymInfo.Symbol is IMethodSymbol { MethodKind: MethodKind.DelegateInvoke } delegateInvoke)
            {
                var containingTypeName = delegateInvoke.ContainingType.ToDisplayString();
                var javaMethod = context.TypeMappings.MapMethod(containingTypeName, "Invoke") ?? "apply";
                var delegateArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);

                // If the accessed member is a property, emit the Java getter call.
                var memberSymbol = context.SemanticModel.GetSymbolInfo(memberAccess).Symbol;
                string delegateTarget;
                if (memberSymbol is IPropertySymbol prop)
                {
                    var getterName = "get" + char.ToUpperInvariant(prop.Name[0]) + prop.Name[1..];
                    delegateTarget = $"{receiver}.{getterName}()";
                }
                else
                {
                    delegateTarget = $"{receiver}.{originalMethodName}";
                }

                return $"{delegateTarget}.{javaMethod}({delegateArgs})";
            }
        }

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

        // Fix: Primitive instance method calls → static wrapper form.
        // C# value types (int, long, double, …) can call GetHashCode/CompareTo/ToString via
        // implicit boxing. Java primitives cannot call instance methods; use the boxed-class
        // static equivalents so the generated code compiles without "cannot dereference int".
        //   intVar.GetHashCode()    → Integer.hashCode(intVar)
        //   intVar.CompareTo(other) → Integer.compare(intVar, other)
        //   intVar.ToString()       → String.valueOf(intVar)
        if (methodName == originalMethodName
            && context.SemanticModel != null
            && originalMethodName is "GetHashCode" or "CompareTo" or "ToString")
        {
            var receiverSpecialType = context.SemanticModel
                .GetTypeInfo(memberAccess.Expression).Type?.SpecialType;
            var wrapperClass = GetJavaWrapperForPrimitiveSpecialType(receiverSpecialType);
            if (wrapperClass != null)
            {
                var primArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
                return originalMethodName switch
                {
                    "GetHashCode" => $"{wrapperClass}.hashCode({receiver})",
                    "CompareTo"   => $"{wrapperClass}.compare({receiver}, {primArgs})",
                    "ToString"    => $"String.valueOf({receiver})",
                    _             => $"{wrapperClass}.{char.ToLowerInvariant(originalMethodName[0]) + originalMethodName[1..]}({receiver})"
                };
            }
        }

        // Fix: GC.SuppressFinalize(this) and related System.GC static methods.
        // Java uses automatic garbage collection — these C# IDisposable / finalizer-management
        // patterns have no Java equivalent and must not be emitted (Java has no "GC" class).
        // Emitting a comment is valid Java syntax (comment + empty-statement ';').
        // Must run BEFORE camelCase rename so originalMethodName is still in C# form.
        if ((receiver == "GC" || methodSymbol?.ContainingType.ToDisplayString() == "System.GC")
            && originalMethodName is "SuppressFinalize" or "Collect" or "WaitForPendingFinalizers" or "KeepAlive")
        {
            return "/* GC operation not needed in Java */";
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

        // ── Static Enumerable methods (Range, Repeat, Empty) ──────────────────
        // These are NOT extension methods — they are static factory methods on System.Linq.Enumerable.
        if (methodSymbol is { IsStatic: true, IsExtensionMethod: false }
            && methodSymbol.ContainingType.ToDisplayString() == "System.Linq.Enumerable")
        {
            // Enumerable.Range(start, count) → IntStream.range(start, start + count).boxed()
            if (originalMethodName == "Range" && node.ArgumentList.Arguments.Count == 2)
            {
                context.AddImport("java.util.stream.IntStream");
                var startArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var countArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"IntStream.range({startArg}, {startArg} + {countArg}).boxed()";
            }

            // Enumerable.Repeat(element, count) → Stream.generate(() -> element).limit(count)
            if (originalMethodName == "Repeat" && node.ArgumentList.Arguments.Count == 2)
            {
                context.AddImport("java.util.stream.Stream");
                var elemArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var countArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"Stream.generate(() -> {elemArg}).limit({countArg})";
            }

            // Enumerable.Empty<T>() → Stream.<T>empty()
            if (originalMethodName == "Empty")
            {
                context.AddImport("java.util.stream.Stream");
                if (node.Expression is MemberAccessExpressionSyntax ma
                    && ma.Name is GenericNameSyntax gns
                    && gns.TypeArgumentList.Arguments.Count > 0)
                {
                    var typeArg = facade.Transform(gns.TypeArgumentList.Arguments[0], context);
                    return $"Stream.<{typeArg}>empty()";
                }
                return "Stream.empty()";
            }
        }

        // AsQueryable/AsEnumerable → identity transform: strip the wrapper and return the receiver.
        // In Java, IEnumerable<T> and IQueryable<T> both map to Stream/collection operations;
        // subsequent LINQ calls on the result are handled identically via the same Stream API path.
        // Use name-based detection as a fallback when System.Linq.Queryable.dll is not fully
        // resolved by Roslyn (e.g. when methodSymbol is null due to missing assembly reference).
        if (originalMethodName is "AsQueryable" or "AsEnumerable"
            && (methodSymbol == null
                || (methodSymbol.IsExtensionMethod
                    && methodSymbol.ContainingType.ToDisplayString() is "System.Linq.Queryable" or "System.Linq.Enumerable")))
        {
            return receiver;
        }

        // Pre-LINQ-block intercept: ToDictionary may fail semantic resolution; handle by name.
        // Only fire when the LINQ block won't handle it (methodSymbol is null or not a LINQ Enumerable/Queryable extension).
        if (originalMethodName == "ToDictionary" && node.ArgumentList.Arguments.Count >= 2
            && (methodSymbol == null || !methodSymbol.IsExtensionMethod
                || methodSymbol.ContainingType.ToDisplayString() is not ("System.Linq.Enumerable" or "System.Linq.Queryable")))
        {
            context.AddImport("java.util.stream.Collectors");
            string toMapRcv = receiver;
            ITypeSymbol? rcvElemType = null;
            if (!IsReceiverLinqExtension(memberAccess.Expression, context))
            {
                var rcvType = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type;
                if (rcvType is IArrayTypeSymbol tdArr)
                    rcvElemType = tdArr.ElementType;
                else if (rcvType is INamedTypeSymbol namedRcv && namedRcv.TypeArguments.Length > 0)
                    // Extract collection element type (e.g. List<String> → String) for member rescue
                    rcvElemType = namedRcv.TypeArguments[0];

                toMapRcv = BuildStreamReceiverExpression(
                    toMapRcv,
                    rcvType,
                    context,
                    boxPrimitiveArrayElements: true,
                    preserveGroupingValueStream: false);
            }
            var kArgTD = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var vArgTD = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            // Rescue C# property names that the semantic model could not resolve (e.g. x.Length → x.length())
            vArgTD = RescueCsharpMemberNames(vArgTD, rcvElemType, context);
            return $"{toMapRcv}.collect(Collectors.toMap({kArgTD}, {vArgTD}))";
        }

        // LINQ Stream API fallback: when a LINQ extension method was not rewritten by LinqRewriter
        // (e.g. chains containing OrderBy or SelectMany), inject .stream() on the collection
        // receiver and handle terminal/sorting operations.
        // Also handles System.Linq.Queryable extension methods (AsQueryable() stripped above).
        if (methodSymbol != null
            && (methodSymbol.ContainingType.ToDisplayString() == "System.Linq.Enumerable"
                || methodSymbol.ContainingType.ToDisplayString() == "System.Linq.Queryable")
            && methodSymbol.IsExtensionMethod
            && context.SemanticModel != null)
        {
            if (!IsReceiverLinqExtension(memberAccess.Expression, context))
            {
                var receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression).Type;
                receiver = BuildStreamReceiverExpression(
                    receiver,
                    receiverType,
                    context,
                    boxPrimitiveArrayElements: false,
                    preserveGroupingValueStream: true);
            }

            // ToList → .toList() (Java 16+) or collect(Collectors.toList())
            if (originalMethodName == "ToList")
            {
                bool needsArrayListMaterialization = ShouldMaterializeArrayListForToList(node, context);
                if (context.Options.TargetJavaVersion >= JavaVersion.Java21)
                {
                    var toListExpr = $"{receiver}.toList()";
                    if (needsArrayListMaterialization)
                    {
                        context.AddImport("java.util.ArrayList");
                        return $"new ArrayList<>({toListExpr})";
                    }
                    return toListExpr;
                }
                context.AddImport("java.util.stream.Collectors");
                var collectExpr = $"{receiver}.collect(Collectors.toList())";
                if (needsArrayListMaterialization)
                {
                    context.AddImport("java.util.ArrayList");
                    return $"new ArrayList<>({collectExpr})";
                }
                return collectExpr;
            }

            // ToDictionary → collect(Collectors.toMap(keySelector, valueSelector))
            if (originalMethodName == "ToDictionary" && node.ArgumentList.Arguments.Count >= 2)
            {
                context.AddImport("java.util.stream.Collectors");
                var keyArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var valArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                // Roslyn sometimes fails to bind the second lambda's body when two lambdas in the same
                // call share a parameter name (e.g. x => x, x => x.Length). Use source-type TypeMappings
                // as a rescue pass for unresolved C# property accesses (e.g. ".Length" still uppercased).
                valArg = RescueCsharpMemberNames(valArg, methodSymbol, context);
                return $"{receiver}.collect(Collectors.toMap({keyArg}, {valArg}))";
            }

            // OrderBy/ThenBy → sorted(Comparator.comparing(lambda)) or sorted(Comparator.naturalOrder())
            // ThenBy/ThenByDescending merge into the preceding sorted() call's comparator
            // to produce .sorted(Comparator.comparing(a).thenComparing(b)) instead of
            // two separate .sorted() calls.
            if (originalMethodName is "OrderBy" or "ThenBy")
            {
                var sortArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, argStartIndex);
                if (string.IsNullOrEmpty(sortArgs))
                    return $"{receiver}.sorted()";

                // Detect identity lambda (x -> x): use naturalOrder() to avoid type-inference failure
                bool isIdentAsc = node.ArgumentList.Arguments.Count > argStartIndex
                    && TryGetSingleParamLambda(node.ArgumentList.Arguments[argStartIndex].Expression, context, facade, out var idPAsc, out var idBAsc)
                    && idBAsc.Trim() == idPAsc.Trim();

                // Build comparator with explicit element-type annotation to prevent Java type-inference
                // failure when chaining thenComparing() — e.g. comparingInt((String x) -> x.length())
                // instead of comparing(x -> x.length()) which Java can't type-propagate.
                string BuildOrderByComparator(bool reversed = false)
                {
                    if (isIdentAsc)
                    {
                        if (methodSymbol.TypeArguments.Length >= 1)
                        {
                            var srcType = methodSymbol.TypeArguments[0];
                            var javaElem = context.TypeMappings.MapType(srcType.ToDisplayString());
                            if (string.IsNullOrEmpty(javaElem) || javaElem == srcType.ToDisplayString())
                                javaElem = context.TypeMappings.MapType($"{srcType.ContainingNamespace}.{srcType.Name}");
                            if (!string.IsNullOrEmpty(javaElem))
                            {
                                var boxedElem = ExpressionTransformerHelpers.BoxJavaPrimitiveType(javaElem);
                                return reversed
                                    ? $"java.util.Comparator.<{boxedElem}>naturalOrder().reversed()"
                                    : $"java.util.Comparator.<{boxedElem}>naturalOrder()";
                            }
                        }
                        return reversed ? "java.util.Comparator.reverseOrder()" : "java.util.Comparator.naturalOrder()";
                    }
                    if (methodSymbol.TypeArguments.Length >= 2
                        && TryGetSingleParamLambda(node.ArgumentList.Arguments[argStartIndex].Expression,
                            context, facade, out var lParam, out var lBody))
                    {
                        var srcType  = methodSymbol.TypeArguments[0];
                        var keyType  = methodSymbol.TypeArguments[1];
                        var javaElem = context.TypeMappings.MapType(srcType.ToDisplayString());
                        // Alias types like "string" won't map — retry with FQN (e.g. "System.String" → "String")
                        if (string.IsNullOrEmpty(javaElem) || javaElem == srcType.ToDisplayString())
                            javaElem = context.TypeMappings.MapType($"{srcType.ContainingNamespace}.{srcType.Name}");
                        var comparingFn = keyType.SpecialType switch
                        {
                            SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Byte => "comparingInt",
                            SpecialType.System_Int64  => "comparingLong",
                            SpecialType.System_Double or SpecialType.System_Single => "comparingDouble",
                            _ => "comparing"
                        };
                        var lambdaParamType = !string.IsNullOrEmpty(javaElem)
                            ? ExpressionTransformerHelpers.BoxJavaPrimitiveType(javaElem)
                            : "";
                        var typedParam = !string.IsNullOrEmpty(lambdaParamType)
                            ? $"({lambdaParamType} {lParam})" : lParam;
                        var cmp = $"java.util.Comparator.{comparingFn}({typedParam} -> {lBody})";
                        return reversed ? $"{cmp}.reversed()" : cmp;
                    }
                    // Fallback (no type info): use untyped comparing
                    var fallback = $"java.util.Comparator.comparing({sortArgs})";
                    return reversed ? $"{fallback}.reversed()" : fallback;
                }

                var comparator = BuildOrderByComparator();

                // Merge ThenBy into preceding sorted() if possible
                if (originalMethodName == "ThenBy" && TryExtractSortedComparator(receiver, out var baseReceiver, out var prevComparator))
                {
                    return $"{baseReceiver}.sorted({prevComparator}.thenComparing({(isIdentAsc ? "java.util.Comparator.naturalOrder()" : sortArgs)}))";
                }

                return $"{receiver}.sorted({comparator})";
            }

            // OrderByDescending/ThenByDescending → sorted(Comparator.reverseOrder()) or .reversed()
            if (originalMethodName is "OrderByDescending" or "ThenByDescending")
            {
                var sortArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, argStartIndex);
                if (string.IsNullOrEmpty(sortArgs))
                    return $"{receiver}.sorted(java.util.Comparator.reverseOrder())";

                // Detect identity lambda (x -> x): use reverseOrder() directly
                bool isIdentDesc = node.ArgumentList.Arguments.Count > argStartIndex
                    && TryGetSingleParamLambda(node.ArgumentList.Arguments[argStartIndex].Expression, context, facade, out var idPDesc, out var idBDesc)
                    && idBDesc.Trim() == idPDesc.Trim();

                string BuildDescendingComparator()
                {
                    if (methodSymbol.TypeArguments.Length >= 2
                        && TryGetSingleParamLambda(node.ArgumentList.Arguments[argStartIndex].Expression,
                            context, facade, out var dParam, out var dBody))
                    {
                        var srcType = methodSymbol.TypeArguments[0];
                        var keyType = methodSymbol.TypeArguments[1];
                        var javaElem = context.TypeMappings.MapType(srcType.ToDisplayString());
                        if (string.IsNullOrEmpty(javaElem) || javaElem == srcType.ToDisplayString())
                            javaElem = context.TypeMappings.MapType($"{srcType.ContainingNamespace}.{srcType.Name}");

                        var comparingFn = keyType.SpecialType switch
                        {
                            SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Byte => "comparingInt",
                            SpecialType.System_Int64  => "comparingLong",
                            SpecialType.System_Double or SpecialType.System_Single => "comparingDouble",
                            _ => "comparing"
                        };
                        var lambdaParamType = !string.IsNullOrEmpty(javaElem)
                            ? ExpressionTransformerHelpers.BoxJavaPrimitiveType(javaElem)
                            : "";
                        var typedParam = !string.IsNullOrEmpty(lambdaParamType)
                            ? $"({lambdaParamType} {dParam})" : dParam;
                        return $"java.util.Comparator.{comparingFn}({typedParam} -> {dBody}).reversed()";
                    }

                    return $"java.util.Comparator.comparing({sortArgs}).reversed()";
                }

                var descComparator = BuildDescendingComparator();

                // Merge ThenByDescending into preceding sorted() if possible
                if (originalMethodName == "ThenByDescending" && TryExtractSortedComparator(receiver, out var baseReceiver, out var prevComparator))
                {
                    return $"{baseReceiver}.sorted({prevComparator}.thenComparing({descComparator}))";
                }

                return $"{receiver}.sorted({descComparator})";
            }

            // Sum → mapToInt/mapToLong/mapToDouble + sum()
            if (originalMethodName == "Sum")
            {
                var mapMethod = "mapToInt";
                if (methodSymbol.ReturnType != null)
                {
                    var retType = methodSymbol.ReturnType.SpecialType;
                    if (retType == SpecialType.System_Int64)
                        mapMethod = "mapToLong";
                    else if (retType is SpecialType.System_Double or SpecialType.System_Single or SpecialType.System_Decimal)
                        mapMethod = "mapToDouble";
                }
                if (node.ArgumentList.Arguments.Count > 0)
                {
                    var sumArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    return $"{receiver}.{mapMethod}({sumArg}).sum()";
                }
                return $"{receiver}.{mapMethod}(x -> x).sum()";
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

            // GroupBy → collect(Collectors.groupingBy(keySelector)).entrySet().stream()
            // The entrySet().stream() makes the result chainable (downstream .map/.filter etc. work).
            // IGrouping<K,V> lambdas translate: g.getKey() → Map.Entry.getKey(), g.stream() → g.getValue().stream()
            if (originalMethodName == "GroupBy")
            {
                context.AddImport("java.util.stream.Collectors");
                if (node.ArgumentList.Arguments.Count >= 2)
                {
                    var keyArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    var arg1Expr = node.ArgumentList.Arguments[1].Expression;
                    // Detect resultSelector overload: second arg is a 2-param (key, grouping) lambda
                    // e.g. GroupBy(x => x % 2, (k, g) => new { k, Sum = g.Sum() })
                    if (TryGetTwoParamLambda(arg1Expr, context, facade, out var kParam, out var gParam, out var resultBody))
                    {
                        // Map each grouped entry: collect normally then stream entrySet for result projection
                        context.AddImport("java.util.stream.Stream");
                        return $"{receiver}.collect(Collectors.groupingBy({keyArg}))"
                             + $".entrySet().stream()"
                             + $".map(_e -> {{ var {kParam} = _e.getKey(); var {gParam} = _e.getValue(); return {resultBody}; }})"
                             + $".collect(Collectors.toList())";
                    }
                    // Element-selector overload
                    var elemArg = facade.Transform(arg1Expr, context);
                    return $"{receiver}.collect(Collectors.groupingBy({keyArg}, Collectors.mapping({elemArg}, Collectors.toList()))).entrySet().stream()";
                }
                if (node.ArgumentList.Arguments.Count == 1)
                {
                    var keyArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    // entrySet().stream() makes the result chainable as Stream<Map.Entry<K,List<V>>>
                    return $"{receiver}.collect(Collectors.groupingBy({keyArg})).entrySet().stream()";
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
                context.AddImport("java.util.Arrays");
                var otherArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var otherType = context.SemanticModel.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type;
                string otherStream;
                if (otherType is IArrayTypeSymbol concatArrType)
                    otherStream = concatArrType.ElementType.IsValueType
                        ? $"Arrays.stream({otherArg}).boxed()"
                        : $"Arrays.stream({otherArg})";
                else
                    otherStream = $"{otherArg}.stream()";
                return $"java.util.stream.Stream.concat({receiver}, {otherStream})";
            }

            // Where → filter(predicate)
            // Indexed form Where((element, index) => ...) uses IntStream.range pattern
            if (originalMethodName == "Where" && node.ArgumentList.Arguments.Count >= 1)
            {
                var whereLambdaArg = node.ArgumentList.Arguments[0].Expression;
                if (TryGetTwoParamLambda(whereLambdaArg, context, facade, out var whP0, out var whP1, out var whCond))
                {
                    // Where((x, i) => cond): collect, range, filter by index, re-select element
                    context.AddImport("java.util.stream.IntStream");
                    context.AddImport("java.util.stream.Collectors");
                    return $"{receiver}.collect(Collectors.collectingAndThen(Collectors.toList(),"
                         + $" _src -> IntStream.range(0, _src.size())"
                         + $".filter(_i -> {{ var {whP0} = _src.get(_i); int {whP1} = _i; return {whCond}; }})"
                         + $".mapToObj(_src::get)))";
                }
                var predArg = facade.Transform(whereLambdaArg, context);
                return $"{receiver}.filter({predArg})";
            }

            // Select → map(transform)
            // Indexed form Select((element, index) => ...) uses IntStream.range pattern
            if (originalMethodName == "Select" && node.ArgumentList.Arguments.Count >= 1)
            {
                var selectLambdaArg = node.ArgumentList.Arguments[0].Expression;
                if (TryGetTwoParamLambda(selectLambdaArg, context, facade, out var selP0, out var selP1, out var selBody))
                {
                    // Select((x, i) => body): collect to list, range, project with index
                    context.AddImport("java.util.stream.IntStream");
                    context.AddImport("java.util.stream.Collectors");
                    return $"{receiver}.collect(Collectors.collectingAndThen(Collectors.toList(),"
                         + $" _src -> IntStream.range(0, _src.size())"
                         + $".mapToObj(_i -> {{ var {selP0} = _src.get(_i); int {selP1} = _i; return {selBody}; }})))";
                }
                var mapArg = facade.Transform(selectLambdaArg, context);
                return $"{receiver}.map({mapArg})";
            }

            // SelectMany → flatMap(selector)
            if (originalMethodName == "SelectMany")
            {
                if (node.ArgumentList.Arguments.Count >= 2)
                {
                    // Two-arg: SelectMany(collectionSelector, resultSelector)
                    var collArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    var resArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                    return $"{receiver}.flatMap({collArg}).map({resArg})";
                }
                if (node.ArgumentList.Arguments.Count == 1)
                {
                    // When the selector returns an array, flatMap needs Arrays.stream() wrapping.
                    string flatMapArg;
                    var smArg = node.ArgumentList.Arguments[0];
                    ExpressionSyntax? smBodyExpr = smArg.Expression switch
                    {
                        SimpleLambdaExpressionSyntax sl => sl.ExpressionBody,
                        ParenthesizedLambdaExpressionSyntax pl => pl.ExpressionBody,
                        _ => null
                    };
                    string? smParam = smArg.Expression switch
                    {
                        SimpleLambdaExpressionSyntax sl => sl.Parameter.Identifier.Text,
                        _ => null
                    };
                    if (smBodyExpr != null && smParam != null)
                    {
                        var bodyRetType = context.SemanticModel?.GetTypeInfo(smBodyExpr).Type;
                        if (bodyRetType is IArrayTypeSymbol smArr)
                        {
                            context.AddImport("java.util.Arrays");
                            string bodyStr = facade.Transform(smBodyExpr, context);
                            flatMapArg = smArr.ElementType.IsValueType
                                ? $"{smParam} -> java.util.Arrays.stream({bodyStr}).boxed()"
                                : $"{smParam} -> java.util.Arrays.stream({bodyStr})";
                        }
                        else
                            flatMapArg = facade.Transform(smArg.Expression, context);
                    }
                    else
                        flatMapArg = facade.Transform(smArg.Expression, context);
                    return $"{receiver}.flatMap({flatMapArg})";
                }
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

            // Single(predicate) → filter(predicate).reduce((a,b) -> throw).orElseThrow()
            if (originalMethodName == "Single" && node.ArgumentList.Arguments.Count >= 1)
            {
                var predArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.filter({predArg}).reduce((a, b) -> {{ throw new IllegalStateException(\"Sequence contains more than one element\"); }}).orElseThrow()";
            }

            // Single() → reduce((a, b) -> { throw new IllegalStateException(); }).orElseThrow()
            if (originalMethodName == "Single" && node.ArgumentList.Arguments.Count == 0)
            {
                return $"{receiver}.reduce((a, b) -> {{ throw new IllegalStateException(\"Sequence contains more than one element\"); }}).orElseThrow()";
            }

            // SingleOrDefault(predicate) → filter(predicate).reduce((a,b) -> throw).orElse(null)
            if (originalMethodName == "SingleOrDefault" && node.ArgumentList.Arguments.Count >= 1)
            {
                var predArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.filter({predArg}).reduce((a, b) -> {{ throw new IllegalStateException(\"Sequence contains more than one element\"); }}).orElse(null)";
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
                return $"(int)(long) {receiver}.count()";
            }

            // Count(predicate) → filter(predicate).count()
            if (originalMethodName == "Count" && node.ArgumentList.Arguments.Count >= 1)
            {
                var predArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"(int)(long) {receiver}.filter({predArg}).count()";
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

            // ToArray() → typed toArray() based on element type
            if (originalMethodName == "ToArray")
            {
                return TransformToArrayWithElementType(receiver, methodSymbol, node, context);
            }

            // ToHashSet() → collect(Collectors.toSet())
            if (originalMethodName == "ToHashSet")
            {
                context.AddImport("java.util.stream.Collectors");
                return $"{receiver}.collect(Collectors.toSet())";
            }

            // Reverse() → collect to list, reverse, return
            if (originalMethodName == "Reverse")
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.Collections");
                context.AddImport("java.util.ArrayList");
                return $"{receiver}.collect(Collectors.collectingAndThen(Collectors.toCollection(ArrayList::new), list -> {{ Collections.reverse(list); return list; }}))";
            }

            // Append(item) → Stream.concat(stream, Stream.of(item))
            if (originalMethodName == "Append" && node.ArgumentList.Arguments.Count >= 1)
            {
                context.AddImport("java.util.stream.Stream");
                var itemArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"Stream.concat({receiver}, Stream.of({itemArg}))";
            }

            // Prepend(item) → Stream.concat(Stream.of(item), stream)
            if (originalMethodName == "Prepend" && node.ArgumentList.Arguments.Count >= 1)
            {
                context.AddImport("java.util.stream.Stream");
                var itemArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"Stream.concat(Stream.of({itemArg}), {receiver})";
            }

            // DefaultIfEmpty() → collect and check if empty
            if (originalMethodName == "DefaultIfEmpty")
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.stream.Stream");
                if (node.ArgumentList.Arguments.Count >= 1)
                {
                    var defaultArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    return $"{receiver}.collect(Collectors.collectingAndThen(Collectors.toList(), list -> list.isEmpty() ? Stream.of({defaultArg}) : list.stream()))";
                }
                return $"{receiver}.collect(Collectors.collectingAndThen(Collectors.toList(), list -> list.isEmpty() ? Stream.of((Object) null) : list.stream()))";
            }

            // Aggregate(func) → reduce(func).orElseThrow()
            if (originalMethodName == "Aggregate" && node.ArgumentList.Arguments.Count == 1)
            {
                var funcArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.reduce({funcArg}).orElseThrow()";
            }

            // Aggregate(seed, func, resultSelector) → inline resultSelector applied to reduce result
            if (originalMethodName == "Aggregate" && node.ArgumentList.Arguments.Count >= 3)
            {
                var seedArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var funcArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                string reduceExpr = $"{receiver}.reduce({seedArg}, {funcArg})";
                // Inline the result selector: substitute the reduce expression for the lambda parameter.
                // This avoids raw Function cast which fails due to type erasure (Object * 2 etc.).
                if (TryGetSingleParamLambda(node.ArgumentList.Arguments[2].Expression, context, facade, out var rsParam, out var rsBody))
                {
                    var inlined = System.Text.RegularExpressions.Regex.Replace(
                        rsBody, $@"\b{System.Text.RegularExpressions.Regex.Escape(rsParam)}\b", $"({reduceExpr})");
                    return inlined;
                }
                var resultSelFallback = facade.Transform(node.ArgumentList.Arguments[2].Expression, context);
                return $"((java.util.function.Function<Object,Object>)({resultSelFallback})).apply({reduceExpr})"; 
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

            // Zip(other, resultSelector) → collect both to list then IntStream.range for indexed pairing
            if (originalMethodName == "Zip" && node.ArgumentList.Arguments.Count >= 2)
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.stream.IntStream");
                var zipOtherArg0 = node.ArgumentList.Arguments[0];
                var zipOther = facade.Transform(zipOtherArg0.Expression, context);
                var zipOtherType = context.SemanticModel.GetTypeInfo(zipOtherArg0.Expression).Type;
                string zipOtherListExpr;
                if (zipOtherType is IArrayTypeSymbol zipArrType && zipArrType.ElementType.IsValueType)
                    zipOtherListExpr = $"java.util.Arrays.stream({zipOther}).boxed().collect(java.util.stream.Collectors.toList())";
                else if (zipOtherType is IArrayTypeSymbol)
                    zipOtherListExpr = $"java.util.Arrays.stream({zipOther}).collect(java.util.stream.Collectors.toList())";
                else
                    zipOtherListExpr = zipOther;
                if (TryGetTwoParamLambda(node.ArgumentList.Arguments[1].Expression, context, facade, out var zipP0, out var zipP1, out var zipBody))
                {
                    return $"{receiver}.collect(Collectors.collectingAndThen(Collectors.toList(),"
                         + $" _left -> {{ var _right = {zipOtherListExpr};"
                         + $" return IntStream.range(0, Math.min(_left.size(), _right.size()))"
                         + $".mapToObj(_i -> {{ var {zipP0} = _left.get(_i); var {zipP1} = _right.get(_i); return {zipBody}; }}); }}))";
                }
                // Fallback: no two-param lambda recognised
                var zipSel = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"{receiver}.collect(Collectors.collectingAndThen(Collectors.toList(),"
                     + $" _left -> {{ var _right = {zipOtherListExpr};"
                     + $" return IntStream.range(0, Math.min(_left.size(), _right.size())).mapToObj(_i -> _left.get(_i)); }}))";
            }

            // Union(other) → Stream.concat + distinct
            if (originalMethodName == "Union" && node.ArgumentList.Arguments.Count >= 1)
            {
                context.AddImport("java.util.Arrays");
                var otherArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var otherType = context.SemanticModel.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type;
                string otherStream;
                if (otherType is IArrayTypeSymbol unionArrType)
                    otherStream = unionArrType.ElementType.IsValueType
                        ? $"Arrays.stream({otherArg}).boxed()"
                        : $"Arrays.stream({otherArg})";
                else
                    otherStream = $"{otherArg}.stream()";
                return $"java.util.stream.Stream.concat({receiver}, {otherStream}).distinct()";
            }

            // Intersect(other) → filter elements whose value is in the set
            if (originalMethodName == "Intersect" && node.ArgumentList.Arguments.Count >= 1)
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.HashSet");
                var isectArg0 = node.ArgumentList.Arguments[0];
                var isectOther = facade.Transform(isectArg0.Expression, context);
                var isectOtherType = context.SemanticModel?.GetTypeInfo(isectArg0.Expression).Type;
                string isectSet = BuildSetExprFromOther(isectOther, isectOtherType);
                return $"{receiver}.filter({isectSet}::contains)";
            }

            // Except(other) → filter elements whose value is NOT in the set
            if (originalMethodName == "Except" && node.ArgumentList.Arguments.Count >= 1)
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.HashSet");
                var exceptArg0 = node.ArgumentList.Arguments[0];
                var exceptOther = facade.Transform(exceptArg0.Expression, context);
                var exceptOtherType = context.SemanticModel?.GetTypeInfo(exceptArg0.Expression).Type;
                string exceptSet = BuildSetExprFromOther(exceptOther, exceptOtherType);
                return $"{receiver}.filter(x -> !{exceptSet}.contains(x))";
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
                context.AddImport("java.util.stream.Collectors");
                var seqOtherArg0 = node.ArgumentList.Arguments[0];
                var seqOtherStr = facade.Transform(seqOtherArg0.Expression, context);
                var seqOtherType = context.SemanticModel?.GetTypeInfo(seqOtherArg0.Expression).Type;
                string seqOtherList;
                if (seqOtherType is IArrayTypeSymbol seqArr && seqArr.ElementType.IsValueType)
                {
                    context.AddImport("java.util.Arrays");
                    seqOtherList = $"java.util.Arrays.stream({seqOtherStr}).boxed().collect(Collectors.toList())";
                }
                else if (seqOtherType is IArrayTypeSymbol)
                {
                    context.AddImport("java.util.Arrays");
                    seqOtherList = $"java.util.Arrays.stream({seqOtherStr}).collect(Collectors.toList())";
                }
                else
                    seqOtherList = $"{seqOtherStr}.stream().collect(Collectors.toList())";
                return $"{receiver}.collect(Collectors.toList()).equals({seqOtherList})";
            }

            // TakeLast(n) → collect, then subList from the last n elements, re-stream
            if (originalMethodName == "TakeLast" && node.ArgumentList.Arguments.Count >= 1)
            {
                context.AddImport("java.util.stream.Collectors");
                var nArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.collect(Collectors.collectingAndThen(Collectors.toList(),"
                     + $" _l -> _l.subList(Math.max(0, _l.size() - {nArg}), _l.size()).stream()))";
            }

            // SkipLast(n) → collect, then subList omitting the last n elements, re-stream
            if (originalMethodName == "SkipLast" && node.ArgumentList.Arguments.Count >= 1)
            {
                context.AddImport("java.util.stream.Collectors");
                var nArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.collect(Collectors.collectingAndThen(Collectors.toList(),"
                     + $" _l -> _l.subList(0, Math.max(0, _l.size() - {nArg})).stream()))";
            }

            // MaxBy(keySelector) → max(Comparator.comparing(keySelector)).orElseThrow()
            if (originalMethodName == "MaxBy" && node.ArgumentList.Arguments.Count >= 1)
            {
                var selArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.max(java.util.Comparator.comparing({selArg})).orElseThrow()";
            }

            // MinBy(keySelector) → min(Comparator.comparing(keySelector)).orElseThrow()
            if (originalMethodName == "MinBy" && node.ArgumentList.Arguments.Count >= 1)
            {
                var selArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.min(java.util.Comparator.comparing({selArg})).orElseThrow()";
            }

            // ToLookup(keySelector) → collect(groupingBy(key))
            // ToLookup(keySelector, elementSelector) → collect(groupingBy(key, mapping(elem, toList())))
            if (originalMethodName == "ToLookup")
            {
                context.AddImport("java.util.stream.Collectors");
                if (node.ArgumentList.Arguments.Count >= 2)
                {
                    var keyArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    var elemArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                    return $"{receiver}.collect(Collectors.groupingBy({keyArg}, Collectors.mapping({elemArg}, Collectors.toList())))";
                }
                if (node.ArgumentList.Arguments.Count >= 1)
                {
                    var keyArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    return $"{receiver}.collect(Collectors.groupingBy({keyArg}))";
                }
            }

            // Chunk(n) → collect to list, then partition into fixed-size sub-lists via IntStream.range
            if (originalMethodName == "Chunk" && node.ArgumentList.Arguments.Count >= 1)
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.stream.IntStream");
                var nArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.collect(Collectors.collectingAndThen(Collectors.toList(),"
                     + $" _src -> {{ int _n = {nArg}; return IntStream.range(0, (_src.size() + _n - 1) / _n)"
                     + $".mapToObj(_i -> _src.subList(_i * _n, Math.min((_i + 1) * _n, _src.size()))); }}))";
            }

            // DistinctBy(keySelector) → groupingBy into LinkedHashMap (preserves order), take first per key
            if (originalMethodName == "DistinctBy" && node.ArgumentList.Arguments.Count >= 1)
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.LinkedHashMap");
                var selArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.collect(Collectors.collectingAndThen("
                     + $"Collectors.groupingBy({selArg}, java.util.LinkedHashMap::new, Collectors.toList()),"
                     + $" _m -> _m.values().stream().map(_list -> _list.get(0))))";
            }

            // UnionBy(other, keySelector) → concat + distinctBy key
            if (originalMethodName == "UnionBy" && node.ArgumentList.Arguments.Count >= 2)
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.LinkedHashMap");
                context.AddImport("java.util.stream.Stream");
                var ubOtherArg0 = node.ArgumentList.Arguments[0];
                var ubOther = facade.Transform(ubOtherArg0.Expression, context);
                var ubOtherType = context.SemanticModel.GetTypeInfo(ubOtherArg0.Expression).Type;
                string ubOtherStream;
                if (ubOtherType is IArrayTypeSymbol ubArrType && ubArrType.ElementType.IsValueType)
                    ubOtherStream = $"java.util.Arrays.stream({ubOther}).boxed()";
                else if (ubOtherType is IArrayTypeSymbol)
                    ubOtherStream = $"java.util.Arrays.stream({ubOther})";
                else
                    ubOtherStream = $"{ubOther}.stream()";
                var ubSel = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"java.util.stream.Stream.concat({receiver}, {ubOtherStream})"
                     + $".collect(Collectors.collectingAndThen("
                     + $"Collectors.groupingBy({ubSel}, java.util.LinkedHashMap::new, Collectors.toList()),"
                     + $" _m -> _m.values().stream().map(_list -> _list.get(0))))";
            }

            // IntersectBy(otherKeys, keySelector) → filter elements whose key is in the key set
            if (originalMethodName == "IntersectBy" && node.ArgumentList.Arguments.Count >= 2)
            {
                context.AddImport("java.util.stream.Collectors");
                var ibOtherArg0 = node.ArgumentList.Arguments[0];
                var ibOther = facade.Transform(ibOtherArg0.Expression, context);
                var ibOtherType = context.SemanticModel.GetTypeInfo(ibOtherArg0.Expression).Type;
                string ibSetExpr = BuildSetExprFromOther(ibOther, ibOtherType);
                if (TryGetSingleParamLambda(node.ArgumentList.Arguments[1].Expression, context, facade, out var ibP, out var ibKeyBody))
                    return $"{receiver}.filter({ibP} -> {ibSetExpr}.contains({ibKeyBody}))";
                var ibSel = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"/* TODO: LINQ IntersectBy */ {receiver}.intersectBy({ibOther}, {ibSel})";
            }

            // ExceptBy(otherKeys, keySelector) → filter elements whose key is NOT in the key set
            if (originalMethodName == "ExceptBy" && node.ArgumentList.Arguments.Count >= 2)
            {
                context.AddImport("java.util.stream.Collectors");
                var ebOtherArg0 = node.ArgumentList.Arguments[0];
                var ebOther = facade.Transform(ebOtherArg0.Expression, context);
                var ebOtherType = context.SemanticModel.GetTypeInfo(ebOtherArg0.Expression).Type;
                string ebSetExpr = BuildSetExprFromOther(ebOther, ebOtherType);
                if (TryGetSingleParamLambda(node.ArgumentList.Arguments[1].Expression, context, facade, out var ebP, out var ebKeyBody))
                    return $"{receiver}.filter({ebP} -> !{ebSetExpr}.contains({ebKeyBody}))";
                var ebSel = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"/* TODO: LINQ ExceptBy */ {receiver}.exceptBy({ebOther}, {ebSel})";
            }

            // Join(inner, outerKey, innerKey, resultSelector) → flatMap + filter + map
            if (originalMethodName == "Join" && node.ArgumentList.Arguments.Count >= 4)
            {
                var jInnerArg0 = node.ArgumentList.Arguments[0];
                var jInner = facade.Transform(jInnerArg0.Expression, context);
                var jInnerType = context.SemanticModel.GetTypeInfo(jInnerArg0.Expression).Type;
                var jInnerStream = jInnerType is IArrayTypeSymbol ? $"java.util.Arrays.stream({jInner})" : $"{jInner}.stream()";
                TryGetSingleParamLambda(node.ArgumentList.Arguments[1].Expression, context, facade, out var jOuterP, out var jOuterKey);
                TryGetSingleParamLambda(node.ArgumentList.Arguments[2].Expression, context, facade, out var jInnerP, out var jInnerKey);
                TryGetTwoParamLambda(node.ArgumentList.Arguments[3].Expression, context, facade, out var jResP0, out var jResP1, out var jResBody);
                // Ensure result body can reference outer via jOuterP (captured) and inner via jInnerP (map param)
                string jMapBody;
                if (jResP0 != jOuterP || jResP1 != jInnerP)
                {
                    var rebind = new System.Text.StringBuilder();
                    if (jResP0 != jOuterP) rebind.Append($"var {jResP0} = {jOuterP}; ");
                    if (jResP1 != jInnerP) rebind.Append($"var {jResP1} = {jInnerP}; ");
                    jMapBody = $"{{ {rebind}return {jResBody}; }}";
                }
                else
                    jMapBody = jResBody;
                return $"{receiver}.flatMap({jOuterP} -> {jInnerStream}"
                     + $".filter({jInnerP} -> java.util.Objects.equals({jOuterKey}, {jInnerKey}))"
                     + $".map({jInnerP} -> {jMapBody}))";
            }

            // GroupJoin(inner, outerKey, innerKey, resultSelector) → map outer to (outer, groupList) pairs
            if (originalMethodName == "GroupJoin" && node.ArgumentList.Arguments.Count >= 4)
            {
                context.AddImport("java.util.stream.Collectors");
                var gjInnerArg0 = node.ArgumentList.Arguments[0];
                var gjInner = facade.Transform(gjInnerArg0.Expression, context);
                var gjInnerType = context.SemanticModel.GetTypeInfo(gjInnerArg0.Expression).Type;
                var gjInnerStream = gjInnerType is IArrayTypeSymbol ? $"java.util.Arrays.stream({gjInner})" : $"{gjInner}.stream()";
                TryGetSingleParamLambda(node.ArgumentList.Arguments[1].Expression, context, facade, out var gjOuterP, out var gjOuterKey);
                TryGetSingleParamLambda(node.ArgumentList.Arguments[2].Expression, context, facade, out var gjInnerP, out var gjInnerKey);
                TryGetTwoParamLambda(node.ArgumentList.Arguments[3].Expression, context, facade, out var gjResP0, out var gjResP1, out var gjResBody);
                // Resolve result body outer param name vs actual outer param
                string prebind = gjResP0 != gjOuterP ? $"var {gjResP0} = {gjOuterP}; " : "";
                string gjGroupExpr = $"{gjInnerStream}.filter({gjInnerP} -> java.util.Objects.equals({gjOuterKey}, {gjInnerKey})).collect(java.util.stream.Collectors.toList())";
                return $"{receiver}.map({gjOuterP} -> {{ {prebind}var {gjResP1} = {gjGroupExpr}; return {gjResBody}; }})";
            }

            // Fallback for any unhandled LINQ method: transform args and emit as-is with a TODO comment.
            {
                var fallbackArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, argStartIndex);
                return $"/* TODO: LINQ {originalMethodName} */ {receiver}.{methodName}({fallbackArgs})";
            }
        }

        var args = ArgumentTransformer.TransformArgumentList(
            node.ArgumentList, context, facade, argStartIndex, methodSymbol);

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
        if (sym == null) return false;
        // AsQueryable/AsEnumerable are identity wrappers — don't count as LINQ so .stream() is still injected
        if (sym.Name is "AsQueryable" or "AsEnumerable") return false;
        return sym.ContainingType.ToDisplayString() is "System.Linq.Enumerable" or "System.Linq.Queryable";
    }

    /// <summary>
    /// Transforms Stream.toArray() to produce a correctly-typed array instead of Object[].
    /// For primitive element types (int, long, double): uses mapToXxx().toArray() → primitive array.
    /// For reference types: uses .toArray(TypeName[]::new) → typed array.
    /// Falls back to .toArray() when element type cannot be determined.
    /// </summary>
    private static string TransformToArrayWithElementType(
        string receiver,
        IMethodSymbol? methodSymbol,
        InvocationExpressionSyntax node,
        ConversionContext context)
    {
        // Try to determine the element type from the LINQ method's receiver (IEnumerable<T>)
        ITypeSymbol? elementType = null;

        // Method 1: From the extension method's type arguments (e.g. Enumerable.ToArray<TSource>)
        if (methodSymbol?.TypeArguments.Length >= 1)
        {
            elementType = methodSymbol.TypeArguments[0];
        }

        // Method 2: From the receiver's IEnumerable<T> type argument
        if (elementType == null && methodSymbol?.ReceiverType is INamedTypeSymbol receiverNamed)
        {
            elementType = ExtractEnumerableElementType(receiverNamed);
        }

        // Method 3: From the assignment/declaration context
        if (elementType == null && context.SemanticModel != null)
        {
            var convertedType = context.SemanticModel.GetTypeInfo(node).ConvertedType;
            if (convertedType is IArrayTypeSymbol targetArray)
                elementType = targetArray.ElementType;
        }

        if (elementType == null)
            return $"{receiver}.toArray()";

        // Primitive types: use mapToInt/mapToLong/mapToDouble + toArray() → returns int[]/long[]/double[]
        var specialType = elementType.SpecialType;
        if (specialType is SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Byte)
        {
            return $"{receiver}.mapToInt(Integer::intValue).toArray()";
        }
        if (specialType is SpecialType.System_Int64)
        {
            return $"{receiver}.mapToLong(Long::longValue).toArray()";
        }
        if (specialType is SpecialType.System_Double or SpecialType.System_Single)
        {
            return $"{receiver}.mapToDouble(Double::doubleValue).toArray()";
        }

        // Reference types: .toArray(TypeName[]::new)
        var javaType = context.MapType(elementType);
        if (!string.IsNullOrEmpty(javaType) && javaType != "Object")
        {
            return $"{receiver}.toArray({javaType}[]::new)";
        }

        return $"{receiver}.toArray()";
    }

    /// <summary>
    /// Extracts the element type T from an IEnumerable&lt;T&gt;, ICollection&lt;T&gt;, or similar generic collection type.
    /// </summary>
    private static ITypeSymbol? ExtractEnumerableElementType(INamedTypeSymbol type)
    {
        if (type.TypeArguments.Length > 0
            && type.Name is "IEnumerable" or "ICollection" or "IList" or "IOrderedEnumerable"
                or "IReadOnlyCollection" or "IReadOnlyList" or "List" or "HashSet")
        {
            return type.TypeArguments[0];
        }
        foreach (var iface in type.AllInterfaces)
        {
            if (iface.Name == "IEnumerable" && iface.TypeArguments.Length > 0)
                return iface.TypeArguments[0];
        }
        return null;
    }

    /// <summary>
    /// Builds a Java Stream source expression for a LINQ receiver while preserving
    /// compile validity for receivers mapped to Iterable&lt;T&gt;.
    /// </summary>
    private static string BuildStreamReceiverExpression(
        string receiverExpr,
        ITypeSymbol? receiverType,
        ConversionContext context,
        bool boxPrimitiveArrayElements,
        bool preserveGroupingValueStream)
    {
        if (receiverType is IArrayTypeSymbol arrayType)
        {
            context.AddImport("java.util.Arrays");
            if (boxPrimitiveArrayElements && arrayType.ElementType.IsValueType)
                return $"Arrays.stream({receiverExpr}).boxed()";
            return $"Arrays.stream({receiverExpr})";
        }

        if (preserveGroupingValueStream
            && receiverType is INamedTypeSymbol groupingType
            && groupingType.OriginalDefinition?.ToDisplayString().StartsWith("System.Linq.IGrouping<") == true)
        {
            // GroupBy emits .entrySet().stream(); IGrouping in C# → Map.Entry<K,List<V>>.
            // Stream the group elements via getValue().
            return $"{receiverExpr}.getValue().stream()";
        }

        if (CanCallCollectionStream(receiverType))
            return $"{receiverExpr}.stream()";

        // IEnumerable<T> maps to java.lang.Iterable<T>, which has no .stream().
        context.AddImport("java.util.stream.StreamSupport");
        return $"StreamSupport.stream({receiverExpr}.spliterator(), false)";
    }

    private static bool CanCallCollectionStream(ITypeSymbol? receiverType)
    {
        if (receiverType == null)
            return false;

        if (receiverType.OriginalDefinition?.ToDisplayString() is "System.Collections.Generic.ICollection<T>" or "System.Collections.ICollection")
            return true;

        if (receiverType is not INamedTypeSymbol namedType)
            return false;

        return namedType.AllInterfaces.Any(i =>
            i.OriginalDefinition?.ToDisplayString() is "System.Collections.Generic.ICollection<T>" or "System.Collections.ICollection");
    }

    private static bool ShouldMaterializeArrayListForToList(InvocationExpressionSyntax node, ConversionContext context)
    {
        if (context.SemanticModel == null)
            return false;

        if (node.Parent is ReturnStatementSyntax)
        {
            var enclosingMethod = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (enclosingMethod != null)
            {
                var methodSymbol = context.SemanticModel.GetDeclaredSymbol(enclosingMethod);
                if (methodSymbol != null && IsCsharpListType(methodSymbol.ReturnType))
                    return true;
            }
        }

        if (node.Parent is EqualsValueClauseSyntax eq && eq.Parent is VariableDeclaratorSyntax declarator
            && declarator.Parent is VariableDeclarationSyntax declaration)
        {
            var declaredType = context.SemanticModel.GetTypeInfo(declaration.Type).Type;
            if (declaredType != null && IsCsharpListType(declaredType))
                return true;
        }

        if (node.Parent is AssignmentExpressionSyntax assignment && assignment.Right == node)
        {
            var leftType = context.SemanticModel.GetTypeInfo(assignment.Left).Type;
            if (leftType != null && IsCsharpListType(leftType))
                return true;
        }

        var typeInfo = context.SemanticModel.GetTypeInfo(node);
        var listType = typeInfo.ConvertedType ?? typeInfo.Type;
        if (listType == null)
            return false;

        if (IsCsharpListType(listType))
            return true;

        var mapped = context.TypeMappings.MapType(listType.ToDisplayString());
        if (string.IsNullOrWhiteSpace(mapped) || mapped == listType.ToDisplayString())
            mapped = context.TypeMappings.MapType($"{listType.ContainingNamespace}.{listType.Name}");
        if (string.IsNullOrWhiteSpace(mapped))
            return false;

        var normalized = mapped.StartsWith("java.util.", StringComparison.Ordinal)
            ? mapped["java.util.".Length..]
            : mapped;
        return normalized == "ArrayList" || normalized.StartsWith("ArrayList<", StringComparison.Ordinal);
    }

    private static bool IsCsharpListType(ITypeSymbol type)
    {
        if (type.ToDisplayString() == "System.Collections.ArrayList")
            return true;

        return type is INamedTypeSymbol named
            && named.OriginalDefinition?.ToDisplayString() == "System.Collections.Generic.List<T>";
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
    /// Returns the Java boxed-class name for a C# numeric or boolean primitive SpecialType,
    /// or null when the type is not a primitive that requires static-wrapper conversion.
    /// </summary>
    private static string? GetJavaWrapperForPrimitiveSpecialType(SpecialType? specialType)
        => specialType switch
        {
            SpecialType.System_Int32   => "Integer",
            SpecialType.System_Int64   => "Long",
            SpecialType.System_Int16   => "Short",
            SpecialType.System_Byte    => "Byte",
            SpecialType.System_SByte   => "Byte",
            SpecialType.System_UInt32  => "Integer",
            SpecialType.System_UInt64  => "Long",
            SpecialType.System_UInt16  => "Short",
            SpecialType.System_Single  => "Float",
            SpecialType.System_Double  => "Double",
            SpecialType.System_Char    => "Character",
            SpecialType.System_Boolean => "Boolean",
            _ => null
        };

    /// <summary>
    /// Tries to extract the parameter name and transformed body from a single-parameter lambda.
    /// Handles SimpleLambdaExpressionSyntax (x =&gt; ...) and 1-param ParenthesizedLambdaExpression ((x) =&gt; ...).
    /// Returns false if the expression is not a simple 1-param lambda; body is still set to the
    /// transformed expression as a fallback.
    /// </summary>
    private static bool TryGetSingleParamLambda(
        ExpressionSyntax argExpr,
        ConversionContext context,
        ExpressionTransformerFacade facade,
        out string param,
        out string body)
    {
        if (argExpr is SimpleLambdaExpressionSyntax simple)
        {
            param = simple.Parameter.Identifier.Text;
            body = simple.Body is ExpressionSyntax eb ? facade.Transform(eb, context) : simple.Body.ToString();
            return true;
        }
        if (argExpr is ParenthesizedLambdaExpressionSyntax paren1 && paren1.ParameterList.Parameters.Count == 1)
        {
            param = paren1.ParameterList.Parameters[0].Identifier.Text;
            body = paren1.Body is ExpressionSyntax eb1 ? facade.Transform(eb1, context) : paren1.Body.ToString();
            return true;
        }
        param = "_x";
        body = facade.Transform(argExpr, context);
        return false;
    }

    /// <summary>
    /// Tries to extract two parameter names and a transformed body from a 2-parameter lambda.
    /// Only handles ParenthesizedLambdaExpressionSyntax ((p0, p1) =&gt; ...).
    /// Returns false if the expression is not a 2-param lambda.
    /// </summary>
    private static bool TryGetTwoParamLambda(
        ExpressionSyntax argExpr,
        ConversionContext context,
        ExpressionTransformerFacade facade,
        out string param0,
        out string param1,
        out string body)
    {
        if (argExpr is ParenthesizedLambdaExpressionSyntax paren2 && paren2.ParameterList.Parameters.Count == 2)
        {
            param0 = paren2.ParameterList.Parameters[0].Identifier.Text;
            param1 = paren2.ParameterList.Parameters[1].Identifier.Text;
            body = paren2.Body is ExpressionSyntax eb2 ? facade.Transform(eb2, context) : paren2.Body.ToString();
            return true;
        }
        param0 = "_p0";
        param1 = "_p1";
        body = facade.Transform(argExpr, context);
        return false;
    }

    /// <summary>
    /// Builds a Java Set expression for the given "other keys" argument.
    /// For primitive value-type arrays, uses Arrays.stream().boxed().collect(toSet()).
    /// For reference-type arrays, uses Arrays.stream().collect(toSet()).
    /// For collections, uses new HashSet&lt;&gt;(other).
    /// The resulting set is built inline per filter call (correct but O(n*m); accepted trade-off).
    /// </summary>
    /// <summary>
    /// Rescues C# property-name accesses that the semantic model failed to remap (e.g. ".Length" still
    /// uppercase, no parens). Uses the method's source-type arguments to look up TypeMappings and apply
    /// the correct Java method name. This handles Roslyn's limitation where two lambdas with the same
    /// parameter name in the same call (e.g. x => x, x => x.Length) may not have the second lambda's
    /// body symbols resolved.
    /// </summary>
    private static string RescueCsharpMemberNames(string expr, IMethodSymbol? methodSymbol, ConversionContext context)
    {
        var srcType = methodSymbol?.TypeArguments.Length > 0 ? methodSymbol.TypeArguments[0] : null;
        return RescueCsharpMemberNames(expr, srcType, context);
    }

    private static string RescueCsharpMemberNames(string expr, ITypeSymbol? srcType, ConversionContext context)
    {
        if (srcType == null || !expr.Contains('.'))
            return expr;

        var srcTypeName = srcType.ToDisplayString();
        var fqnSrc = $"{srcType.ContainingNamespace}.{srcType.Name}";

        // Match ".MemberName" (capital letter, no trailing parens) — a C# property not yet converted
        return System.Text.RegularExpressions.Regex.Replace(expr,
            @"\.([A-Z][A-Za-z0-9_]*)(?!\()",
            m =>
            {
                var memberName = m.Groups[1].Value;
                var mapped = context.TypeMappings.MapMethod(srcTypeName, memberName)
                    ?? context.TypeMappings.MapMethod(fqnSrc, memberName);
                if (mapped == null) return m.Value; // no mapping — leave as-is
                return mapped.Contains('.') ? "." + mapped : $".{mapped}()";
            });
    }

    private static string BuildSetExprFromOther(string other, ITypeSymbol? otherType)
    {
        if (otherType is IArrayTypeSymbol arrType)
        {
            if (arrType.ElementType.IsValueType)
                return $"java.util.Arrays.stream({other}).boxed().collect(java.util.stream.Collectors.toSet())";
            return $"java.util.Arrays.stream({other}).collect(java.util.stream.Collectors.toSet())";
        }
        return $"new HashSet<>({other})";
    }

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
