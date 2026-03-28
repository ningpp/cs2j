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

        // ReferenceEquals(a, b) -> a == b
        if (node.Expression is IdentifierNameSyntax { Identifier.Text: "ReferenceEquals" }
            && node.ArgumentList.Arguments.Count == 2)
        {
            var leftArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var rightArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            return $"({leftArg} == {rightArg})";
        }

            // Null-safe static equality helpers should preserve C# semantics in Java.
            if (node.Expression is IdentifierNameSyntax { Identifier.Text: "Equals" }
                && node.ArgumentList.Arguments.Count == 2
                && IsStaticNullSafeEqualsMethod(context.SemanticModel?.GetSymbolInfo(node).Symbol as IMethodSymbol))
            {
                var leftArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var rightArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"java.util.Objects.equals({leftArg}, {rightArg})";
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
            if (bareMethodSym != null && ConversionContext.HasTypeErasureConflict(bareMethodSym))
                methodName += ConversionContext.GetErasureRenamedSuffix(bareMethodSym.TypeParameters.Length);
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
                    // Check if this is actually an event invocation (e.g., ProgressChanged(sender, args))
                    // Events are a special case - they should use the fire method instead of delegate invocation
                    var identSymbol = context.SemanticModel.GetSymbolInfo(bareIdent).Symbol;
                    if (identSymbol is IEventSymbol eventSym)
                    {
                        // For events within the same class, use the fire method
                        var currentTypeName = context.CurrentType?.Name;
                        if (currentTypeName != null && eventSym.ContainingType.Name == currentTypeName)
                        {
                            var eventName = bareIdent.Identifier.Text;
                            var fireMethodName = $"fire{char.ToUpperInvariant(eventName[0])}{eventName.Substring(1)}";
                            var eventArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
                            return $"{fireMethodName}({eventArgs})";
                        }
                    }

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
            if (bareMethodSym2 != null && ConversionContext.HasTypeErasureConflict(bareMethodSym2))
                methodName += ConversionContext.GetErasureRenamedSuffix(bareMethodSym2.TypeParameters.Length);
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
            "ToLower"       => "toLowerCase",
            "ToUpper"       => "toUpperCase",
            "ToLowerInvariant" => "toLowerCase",
            "ToUpperInvariant" => "toUpperCase",
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
        var earlyMethodSymbol = context.SemanticModel?.GetSymbolInfo(node).Symbol as IMethodSymbol;

        if (originalMethodName == "ReferenceEquals" && node.ArgumentList.Arguments.Count == 2)
        {
            var leftArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var rightArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            return $"({leftArg} == {rightArg})";
        }

        if (originalMethodName == "GetEnumerator" && IsDictionaryLikeExpression(memberAccess.Expression, context))
        {
            return $"{receiver}.entrySet().iterator()";
        }

        if (originalMethodName == "Exit"
            && node.ArgumentList.Arguments.Count >= 1
            && (earlyMethodSymbol?.ContainingType.ToDisplayString() == "System.Environment"
                || memberAccess.Expression.ToString() is "Environment" or "System.Environment"))
        {
            var exitCode = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            return $"System.exit({exitCode})";
        }

        if (originalMethodName == "GetTempPath"
            && (earlyMethodSymbol?.ContainingType.ToDisplayString() == "System.IO.Path"
                || memberAccess.Expression.ToString() is "Path" or "Paths" or "System.IO.Path"))
        {
            return "System.getProperty(\"java.io.tmpdir\")";
        }

        if (originalMethodName == "Combine"
            && node.ArgumentList.Arguments.Count >= 2
            && (earlyMethodSymbol?.ContainingType.ToDisplayString() == "System.IO.Path"
                || memberAccess.Expression.ToString() is "Path" or "Paths" or "System.IO.Path"))
        {
            var combineLeft = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var combineRight = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            return $"java.nio.file.Paths.get({combineLeft}, {combineRight}).toString()";
        }

        if (originalMethodName == "MoveNext" && node.ArgumentList.Arguments.Count == 0
            && IsEnumeratorMoveNextInvocation(node, context))
        {
            return $"{receiver}.hasNext()";
        }

        // Java21+ semantic mapping for System.Random.Next overloads.
        // C# semantics:
        //   Next()          -> [0, Int32.MaxValue)
        //   Next(maxValue)  -> maxValue <= 0 ? 0 : [0, maxValue)
        //   Next(min, max)  -> min >= max ? min : [min, max)
        // Java Random APIs differ on edge conditions, so preserve C# behavior explicitly.
        if (originalMethodName == "Next"
            && node.ArgumentList.Arguments.Count is 0 or 1 or 2
            && (earlyMethodSymbol?.ContainingType.ToDisplayString() == "System.Random"))
        {
            if (node.ArgumentList.Arguments.Count == 0)
            {
                return $"{receiver}.nextInt(0, Integer.MAX_VALUE)";
            }

            if (node.ArgumentList.Arguments.Count == 1)
            {
                var maxArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"(({maxArg}) <= 0 ? 0 : {receiver}.nextInt({maxArg}))";
            }

            var minArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var maxArg2 = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            return $"(({minArg}) >= ({maxArg2}) ? ({minArg}) : {receiver}.nextInt({minArg}, {maxArg2}))";
        }

        if (originalMethodName == "Reset" && node.ArgumentList.Arguments.Count == 0)
        {
            var resetReceiverType = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type as INamedTypeSymbol;
            var isIteratorLike = resetReceiverType != null
                && (resetReceiverType.Name is "IEnumerator" or "Iterator"
                    || resetReceiverType.AllInterfaces.Any(i => i.Name is "IEnumerator" or "Iterator"));
            if (isIteratorLike)
                return "/* reset unsupported for Java Iterator */";
        }

        if (originalMethodName == "Equals" && node.ArgumentList.Arguments.Count == 2
            && IsStaticNullSafeEqualsMethod(context.SemanticModel?.GetSymbolInfo(node).Symbol as IMethodSymbol))
        {
            var leftArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var rightArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            return $"java.util.Objects.equals({leftArg}, {rightArg})";
        }

        if (IsSystemStringMethod(earlyMethodSymbol, memberAccess.Expression))
        {
            if (originalMethodName == "Equals"
                && node.ArgumentList.Arguments.Count == 3
                && TryGetStringComparisonIgnoreCase(node.ArgumentList.Arguments[2].Expression, context.SemanticModel, out var staticEqualsIgnoreCase))
            {
                var leftArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var rightArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"StringHelper.equals({leftArg}, {rightArg}, {ToJavaBooleanLiteral(staticEqualsIgnoreCase)})";
            }

            if (originalMethodName == "Compare"
                && node.ArgumentList.Arguments.Count == 3
                && TryGetStringComparisonIgnoreCase(node.ArgumentList.Arguments[2].Expression, context.SemanticModel, out var compareIgnoreCase))
            {
                var leftArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var rightArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"StringHelper.compare({leftArg}, {rightArg}, {ToJavaBooleanLiteral(compareIgnoreCase)})";
            }
        }

        var stringEqualsReceiverType = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type;
        if (originalMethodName == "Equals"
            && node.ArgumentList.Arguments.Count == 1
            && IsSystemStringType(stringEqualsReceiverType))
        {
            var arg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            return $"java.util.Objects.equals({receiver}, {arg})";
        }

        if (IsSystemStringType(stringEqualsReceiverType)
            && originalMethodName == "Equals"
            && node.ArgumentList.Arguments.Count == 2
            && TryGetStringComparisonIgnoreCase(node.ArgumentList.Arguments[1].Expression, context.SemanticModel, out var instanceEqualsIgnoreCase))
        {
            var arg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            return $"StringHelper.equals({receiver}, {arg}, {ToJavaBooleanLiteral(instanceEqualsIgnoreCase)})";
        }

        if (IsSystemStringType(stringEqualsReceiverType)
            && originalMethodName is "StartsWith" or "EndsWith"
            && node.ArgumentList.Arguments.Count == 2
            && TryGetStringComparisonIgnoreCase(node.ArgumentList.Arguments[1].Expression, context.SemanticModel, out var prefixIgnoreCase))
        {
            var arg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var helperMethod = originalMethodName == "StartsWith" ? "startsWith" : "endsWith";
            return $"StringHelper.{helperMethod}({receiver}, {arg}, {ToJavaBooleanLiteral(prefixIgnoreCase)})";
        }

        if (IsSystemStringType(stringEqualsReceiverType)
            && originalMethodName == "Contains"
            && node.ArgumentList.Arguments.Count == 2
            && TryGetStringComparisonIgnoreCase(node.ArgumentList.Arguments[1].Expression, context.SemanticModel, out var containsIgnoreCase))
        {
            var valueArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            return $"StringHelper.contains({receiver}, {valueArg}, {ToJavaBooleanLiteral(containsIgnoreCase)})";
        }

        if (IsSystemStringType(stringEqualsReceiverType)
            && originalMethodName == "IndexOf"
            && node.ArgumentList.Arguments.Count == 2
            && TryGetStringComparisonIgnoreCase(node.ArgumentList.Arguments[1].Expression, context.SemanticModel, out var indexOfIgnoreCase))
        {
            var valueArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            return $"StringHelper.indexOf({receiver}, {valueArg}, {ToJavaBooleanLiteral(indexOfIgnoreCase)})";
        }

        if (IsSystemStringType(stringEqualsReceiverType)
            && originalMethodName == "IndexOf"
            && node.ArgumentList.Arguments.Count == 3
            && TryGetStringComparisonIgnoreCase(node.ArgumentList.Arguments[2].Expression, context.SemanticModel, out var indexOfWithStartIgnoreCase))
        {
            var valueArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var startIndexArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            return $"StringHelper.indexOf({receiver}, {valueArg}, {startIndexArg}, {ToJavaBooleanLiteral(indexOfWithStartIgnoreCase)})";
        }

        if (IsSystemStringType(stringEqualsReceiverType)
            && originalMethodName == "LastIndexOf"
            && node.ArgumentList.Arguments.Count == 2
            && TryGetStringComparisonIgnoreCase(node.ArgumentList.Arguments[1].Expression, context.SemanticModel, out var lastIndexOfIgnoreCase))
        {
            var valueArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            return $"StringHelper.lastIndexOf({receiver}, {valueArg}, {ToJavaBooleanLiteral(lastIndexOfIgnoreCase)})";
        }

        if (IsSystemStringType(stringEqualsReceiverType)
            && originalMethodName == "LastIndexOf"
            && node.ArgumentList.Arguments.Count == 3
            && TryGetStringComparisonIgnoreCase(node.ArgumentList.Arguments[2].Expression, context.SemanticModel, out var lastIndexOfWithStartIgnoreCase))
        {
            var valueArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var startIndexArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            return $"StringHelper.lastIndexOf({receiver}, {valueArg}, {startIndexArg}, {ToJavaBooleanLiteral(lastIndexOfWithStartIgnoreCase)})";
        }

        // System.Tuple.Create(...) mapping.
        // Avoid emitting Tuple.create(...) which may bind to an unrelated user type named Tuple.
        if (originalMethodName == "Create" && node.ArgumentList.Arguments.Count >= 2)
        {
            bool isSystemTupleCreate = earlyMethodSymbol?.ContainingType?.ToDisplayString() == "System.Tuple"
                || memberAccess.Expression.ToString() is "Tuple" or "System.Tuple";
            if (isSystemTupleCreate)
            {
                int arity = node.ArgumentList.Arguments.Count;
                if (arity == 2)
                {
                    context.AddImport("java.util.AbstractMap");
                    var keyArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    var valueArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                    return $"new AbstractMap.SimpleEntry<>({keyArg}, {valueArg})";
                }

                if (arity is >= 3 and <= 8)
                {
                    var tupleArgs = string.Join(", ", node.ArgumentList.Arguments.Select(a => facade.Transform(a.Expression, context)));
                    context.AddImport("io.vavr.Tuple");
                    return $"Tuple.of({tupleArgs})";
                }
            }
        }

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
                // Check if this is actually an event invocation (e.g., this.ProgressChanged(sender, args))
                // Events are a special case - they should use the fire method instead of delegate invocation
                var memberSymbol = context.SemanticModel.GetSymbolInfo(memberAccess).Symbol;
                if (memberSymbol is IEventSymbol eventSym)
                {
                    // For events within the same class, use the fire method
                    var currentTypeName = context.CurrentType?.Name;
                    if (currentTypeName != null && eventSym.ContainingType.Name == currentTypeName)
                    {
                        var eventName = originalMethodName;
                        var fireMethodName = $"fire{char.ToUpperInvariant(eventName[0])}{eventName.Substring(1)}";
                        var eventArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
                        return $"{fireMethodName}({eventArgs})";
                    }
                }

                var containingTypeName = delegateInvoke.ContainingType.ToDisplayString();
                var javaMethod = context.TypeMappings.MapMethod(containingTypeName, "Invoke") ?? "apply";
                var delegateArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);

                // If the accessed member is a property, emit the Java getter call.
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
            if (primTypeSyntax.Keyword.Text == "string" && originalMethodName == "Format")
            {
                int fmtStart = HasIFormatProviderFirstArg(node, context) ? 1 : 0;
                var formatArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, fmtStart);
                return $"String.format({formatArgs})";
            }

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

        // MSTest Assert.* -> JUnit Assertions.*
        if (TryMapMSTestAssertInvocation(memberAccess.Expression, originalMethodName, methodSymbol, out var junitAssertName))
        {
            var assertArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
            context.AddImport("org.junit.jupiter.api.Assertions");
            return $"Assertions.{junitAssertName}({assertArgs})";
        }

        // C# String.Format(...) -> Java String.format(...)
        if (originalMethodName == "Format"
            && (methodSymbol?.ContainingType.ToDisplayString() == "System.String"
                || memberAccess.Expression.ToString() is "String" or "System.String"))
        {
            int fmtStart = HasIFormatProviderFirstArg(node, context) ? 1 : 0;
            var fmtArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, fmtStart, methodSymbol);
            return $"String.format({fmtArgs})";
        }

        // Instance collection ToArray() should produce a typed array, not Object[].
        if (originalMethodName == "ToArray"
            && node.ArgumentList.Arguments.Count == 0
            && methodSymbol is { IsExtensionMethod: false }
            && methodSymbol.ReturnType is IArrayTypeSymbol instanceArrayType)
        {
            return TransformInstanceCollectionToArray(receiver, instanceArrayType.ElementType, context);
        }

        // Instance List<T>.RemoveRange(startIndex, count)
        // C# RemoveRange(index, count) → Java subList(index, index + count).clear()
        if (originalMethodName == "RemoveRange"
            && node.ArgumentList.Arguments.Count == 2
            && methodSymbol is { IsExtensionMethod: false }
            && methodSymbol.ContainingType?.Name == "List")
        {
            var startArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var countArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            // Wrap complex expressions in parentheses for the addition
            var countStr = countArg.Contains(' ') ? $"({countArg})" : countArg;
            return $"{receiver}.subList({startArg}, {startArg} + {countStr}).clear()";
        }

        // Instance List<T>.Reverse() mutates the list in-place.
        if (originalMethodName == "Reverse"
            && node.ArgumentList.Arguments.Count == 0
            && methodSymbol is { IsExtensionMethod: false }
            && methodSymbol.ContainingType?.Name == "List")
        {
            context.AddImport("java.util.Collections");
            return $"Collections.reverse({receiver})";
        }

        // Instance List<T>.Sort() with default ordering.
        if (originalMethodName == "Sort"
            && node.ArgumentList.Arguments.Count == 0
            && methodSymbol is { IsExtensionMethod: false }
            && methodSymbol.ContainingType?.Name == "List")
        {
            context.AddImport("java.util.Collections");
            return $"Collections.sort({receiver})";
        }

        if (originalMethodName == "Sort"
            && node.ArgumentList.Arguments.Count == 1
            && methodSymbol is { IsExtensionMethod: false }
            && methodSymbol.ContainingType?.Name == "List"
            && methodSymbol.Parameters.Length == 1
            && methodSymbol.Parameters[0].Type.Name == "IComparer"
            && node.ArgumentList.Arguments[0].Expression is ThisExpressionSyntax)
        {
            return $"{receiver}.sort(this::compare)";
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
        // For arrays → Arrays.stream(receiver).iterator().hasNext() or receiver.length > 0.
        // anyMatch(Predicate) is a Stream<T> terminal op and must not be emitted on Iterable<T>.
        // Any(predicate) with arguments is handled by the LinqRewriter (rewritten to a for-loop).
        if (originalMethodName == "Any"
            && node.ArgumentList.Arguments.Count == 0
            && methodSymbol?.ContainingType.ToDisplayString() == "System.Linq.Enumerable")
        {
            // Check if receiver is an array - arrays don't have .iterator()
            var receiverType = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type;
            if (receiverType is IArrayTypeSymbol anyArrayType)
            {
                context.AddImport("java.util.Arrays");
                return $"Arrays.stream({receiver}).iterator().hasNext()";
            }
            return $"{receiver}.iterator().hasNext()";
        }

        // Array.Sort(array[, comparer]) -> Arrays.sort(array[, comparer])
        // System.Array may map syntactically to Object, so keep a fallback on the receiver text.
        if (originalMethodName == "Sort"
            && node.ArgumentList.Arguments.Count is 1 or 2
            && (methodSymbol?.ContainingType.ToDisplayString() == "System.Array"
                || (methodSymbol == null && memberAccess.Expression.ToString() is "Array" or "System.Array" or "Object")))
        {
            var arrayArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            context.AddImport("java.util.Arrays");
            if (node.ArgumentList.Arguments.Count == 1)
            {
                return $"Arrays.sort({arrayArg})";
            }

            var sortArrayType = context.SemanticModel?.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type as IArrayTypeSymbol;
            bool isPrimitiveArray = sortArrayType?.ElementType.SpecialType is not null
                && sortArrayType.ElementType.SpecialType is not SpecialType.None
                && sortArrayType.ElementType.SpecialType is not SpecialType.System_Object;
            if (isPrimitiveArray)
            {
                // Java primitive arrays do not support comparator overloads.
                return $"Arrays.sort({arrayArg})";
            }

            var comparerArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            return $"Arrays.sort({arrayArg}, {comparerArg})";
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

        // Fix: Array.Copy overloads → System.arraycopy(...)
        // Handle both:
        //   Copy(source, dest, length)
        //   Copy(source, sourceIndex, dest, destIndex, length)
        // When source/dest are type parameter arrays (mapped to List<T>), use List operations instead.
        // Check both via semantic model and syntactic fallback (missing assembly reference).
        if (originalMethodName == "Copy"
            && (node.ArgumentList.Arguments.Count == 3 || node.ArgumentList.Arguments.Count >= 5)
            && (methodSymbol?.ContainingType.ToDisplayString() == "System.Array"
                || (methodSymbol == null && memberAccess.Expression.ToString() is "Array" or "System.Array")))
        {
            if (node.ArgumentList.Arguments.Count == 3)
            {
                var srcArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var destArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                var lengthArg = facade.Transform(node.ArgumentList.Arguments[2].Expression, context);
                return $"System.arraycopy({srcArg}, 0, {destArg}, 0, {lengthArg})";
            }

            var srcArg5 = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var srcIndexArg5 = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            var destArg5 = facade.Transform(node.ArgumentList.Arguments[2].Expression, context);
            var destIndexArg5 = facade.Transform(node.ArgumentList.Arguments[3].Expression, context);
            var lengthArg5 = facade.Transform(node.ArgumentList.Arguments[4].Expression, context);
            return $"System.arraycopy({srcArg5}, {srcIndexArg5}, {destArg5}, {destIndexArg5}, {lengthArg5})";
        }

        // Array.Clear(array, index, length) -> Arrays.fill(array, index, index + length, defaultValue)
        // When semantic info is incomplete, Array may already be mapped to Object, so keep syntactic fallback.
        if (originalMethodName == "Clear"
            && node.ArgumentList.Arguments.Count == 3
            && (methodSymbol?.ContainingType.ToDisplayString() == "System.Array"
                || (methodSymbol == null && memberAccess.Expression.ToString() is "Array" or "System.Array" or "Object")))
        {
            var arrayArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var indexArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            var lengthArg = facade.Transform(node.ArgumentList.Arguments[2].Expression, context);
            var endArg = $"({indexArg} + {lengthArg})";

            string defaultValue = "null";
            if (context.SemanticModel?.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type is IArrayTypeSymbol clearArrayType)
            {
                defaultValue = clearArrayType.ElementType.SpecialType switch
                {
                    SpecialType.System_Boolean => "false",
                    SpecialType.System_Char => "'\\0'",
                    SpecialType.System_Single => "0.0f",
                    SpecialType.System_Double => "0.0d",
                    SpecialType.System_Decimal => "0.0d",
                    SpecialType.System_Int64 or SpecialType.System_UInt64 => "0L",
                    SpecialType.System_Int16 or SpecialType.System_UInt16
                        or SpecialType.System_Int32 or SpecialType.System_UInt32
                        or SpecialType.System_Byte or SpecialType.System_SByte => "0",
                    _ => "null"
                };
            }

            context.AddImport("java.util.Arrays");
            return $"Arrays.fill({arrayArg}, {indexArg}, {endArg}, {defaultValue})";
        }

        // System.Threading.Tasks.Parallel.ForEach(source, [options,] action)
        // → StreamSupport.stream(source.spliterator(), true).forEach(action)
        // This preserves compilability in Java while keeping parallel intent.
        if (originalMethodName == "ForEach"
            && node.ArgumentList.Arguments.Count >= 2
            && (methodSymbol?.ContainingType.ToDisplayString() == "System.Threading.Tasks.Parallel"
                || memberAccess.Expression.ToString() is "Parallel" or "System.Threading.Tasks.Parallel"))
        {
            var sourceExpr = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var actionArgExpr = node.ArgumentList.Arguments[^1].Expression;
            string actionExpr;
            var isMethodGroupArg = actionArgExpr is IdentifierNameSyntax or MemberAccessExpressionSyntax;

            if (isMethodGroupArg
                && context.SemanticModel?.GetSymbolInfo(actionArgExpr).Symbol is IMethodSymbol actionMethod)
            {
                var actionMethodName = ConversionContext.EscapeJavaKeyword(
                    actionMethod.Name.Length > 0
                        ? char.ToLowerInvariant(actionMethod.Name[0]) + actionMethod.Name[1..]
                        : actionMethod.Name);
                actionExpr = actionMethod.IsStatic
                    ? $"{context.MapType(actionMethod.ContainingType)}::{actionMethodName}"
                    : $"this::{actionMethodName}";
            }
            else
            {
                actionExpr = facade.Transform(actionArgExpr, context);
                if (actionArgExpr is IdentifierNameSyntax actionId && char.IsUpper(actionId.Identifier.Text[0]))
                {
                    var inferredMethodName = ConversionContext.EscapeJavaKeyword(
                        char.ToLowerInvariant(actionId.Identifier.Text[0]) + actionId.Identifier.Text[1..]);
                    actionExpr = $"this::{inferredMethodName}";
                }
            }

            context.AddImport("java.util.stream.StreamSupport");
            return $"StreamSupport.stream({sourceExpr}.spliterator(), true).forEach({actionExpr})";
        }

        // System.Array.CreateInstance(type, length) → java.lang.reflect.Array.newInstance(type, length)
        if (originalMethodName == "CreateInstance"
            && node.ArgumentList.Arguments.Count == 2
            && (methodSymbol?.ContainingType.ToDisplayString() == "System.Array"
                || (methodSymbol == null && memberAccess.Expression.ToString() is "Array" or "System.Array" or "Object")))
        {
            var typeArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var lengthArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            return $"java.lang.reflect.Array.newInstance({typeArg}, {lengthArg})";
        }

        // System.Array.SetValue(value, index) → java.lang.reflect.Array.set(arrayObj, index, value)
        if (originalMethodName == "SetValue"
            && node.ArgumentList.Arguments.Count == 2
            && (methodSymbol?.ContainingType.ToDisplayString() == "System.Array"
                || methodSymbol?.ContainingType.ToDisplayString() == "System.Object"))
        {
            var valueArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var indexArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            return $"java.lang.reflect.Array.set({receiver}, {indexArg}, {valueArg})";
        }

        // ICollection<T>.CopyTo(array, arrayIndex) / HashSet<T>.CopyTo(array, index)
        // Java collections do not expose copyTo; use System.arraycopy(source.toArray(), ...).
        if (originalMethodName == "CopyTo" && node.ArgumentList.Arguments.Count == 2)
        {
            var destArrayArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var destIndexArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            var copySourceType = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type;
            if (copySourceType is IArrayTypeSymbol copyArr)
            {
                return $"System.arraycopy({receiver}, 0, {destArrayArg}, {destIndexArg}, {receiver}.length)";
            }
            return $"System.arraycopy({receiver}.toArray(), 0, {destArrayArg}, {destIndexArg}, {receiver}.size())";
        }

        // Dictionary.TryGetValue(key, out value) -> assign holder from get + containsKey check.
        // This keeps short-circuit boolean semantics and avoids invalid Java get(key, out) calls.
        if (originalMethodName == "TryGetValue"
            && node.ArgumentList.Arguments.Count == 2)
        {
            var keyArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var outHolderArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            if (IsSimpleIdentifier(outHolderArg))
            {
                var assignTarget = outHolderArg.StartsWith("_", StringComparison.Ordinal)
                    && outHolderArg.EndsWith("Holder", StringComparison.Ordinal)
                    ? $"{outHolderArg}.value"
                    : outHolderArg;
                return $"(({assignTarget} = {receiver}.get({keyArg})) != null || {receiver}.containsKey({keyArg}))";
            }
        }

        // Fix: Array.GetLength(dim) → Java dimensional length access.
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

        // Fix: Math.Round(value, digits) → BigDecimal.valueOf(value).setScale(digits, RoundingMode.HALF_UP).doubleValue()
        // Java's Math.round() only accepts exactly 1 argument; there is no two-argument overload.
        // Directly mapping C# Math.Round(x, n) → Math.round(x, n) causes a Java compile error:
        //   "no suitable method found for round(double,int)".
        // Use BigDecimal.setScale() which is the idiomatic Java equivalent.
        //
        // Fix: Math.Round(value) (1-arg) → (double)Math.round(value)
        // Java Math.round(double) returns long, but C# Math.Round returns double.
        // An explicit cast preserves the numeric type contract.
        //
        // Check both via semantic model (System.Math / System.MathF) and syntactic fallback
        // (receiver text "Math") for environments with incomplete assembly references.
        if (originalMethodName == "Round"
            && (methodSymbol?.ContainingType.ToDisplayString() is "System.Math" or "System.MathF"
                || (methodSymbol == null && memberAccess.Expression.ToString() is "Math" or "System.Math")))
        {
            var argCount = node.ArgumentList.Arguments.Count;

            if (argCount == 2)
            {
                // Math.Round(value, digits) → BigDecimal.valueOf(value).setScale(digits, RoundingMode.HALF_UP).doubleValue()
                context.AddImport("java.math.BigDecimal");
                context.AddImport("java.math.RoundingMode");
                var valArg    = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var digitsArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"BigDecimal.valueOf({valArg}).setScale({digitsArg}, RoundingMode.HALF_UP).doubleValue()";
            }

            if (argCount == 1)
            {
                // Math.Round(value) → (double)Math.round(value)
                var valArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"(double)Math.round({valArg})";
            }
        }

        // Fix: Math.Log(a, newBase) → Math.log(a) / Math.log(newBase)
        // Java's Math.log() only accepts 1 argument (natural logarithm).
        // C# Math.Log(double a, double newBase) computes log base newBase of a.
        // The correct Java equivalent uses the change-of-base formula:
        //   log_base(a) = Math.log(a) / Math.log(base)
        // Direct mapping → Math.log(a, newBase) causes a Java compile error:
        //   "no suitable method found for log(double,double)".
        // The 1-argument overload Math.Log(x) falls through to TypeMappings (Log → log).
        if (originalMethodName == "Log"
            && (methodSymbol?.ContainingType.ToDisplayString() is "System.Math" or "System.MathF"
                || (methodSymbol == null && memberAccess.Expression.ToString() is "Math" or "System.Math")))
        {
            var argCount = node.ArgumentList.Arguments.Count;

            if (argCount == 2)
            {
                // Math.Log(a, newBase) → Math.log(a) / Math.log(newBase)
                var aArg       = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var newBaseArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"Math.log({aArg}) / Math.log({newBaseArg})";
            }
            // argCount == 1: fall through to TypeMappings (Log → log), which is correct.
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

        // System.Convert static methods → Java boxed-type equivalents (semantic-resolved path)
        if (methodSymbol is { IsStatic: true }
            && methodSymbol.ContainingType.ToDisplayString() == "System.Convert")
        {
            (receiver, methodName) = originalMethodName switch
            {
                "ToBoolean" => ("Boolean", "parseBoolean"),
                "ToInt32"   => ("Integer", "parseInt"),
                "ToInt64"   => ("Long", "parseLong"),
                "ToDouble"  => ("Double", "parseDouble"),
                "ToSingle"  => ("Float", "parseFloat"),
                "ToInt16"   => ("Short", "parseShort"),
                "ToByte"    => ("Byte", "parseByte"),
                "ToString"  => node.ArgumentList.Arguments.Count >= 2
                    ? ("Integer", "toString")
                    : ("String", "valueOf"),
                _ => (receiver, methodName)
            };
        }

        // Java cannot reference a static type receiver with a simple name when the current
        // class also has a member with the same name (e.g. field/property Point).
        // Apply this as a post-step for all static calls, even when receiver symbol lookup
        // did not resolve to INamedTypeSymbol in the branch above.
        if (methodSymbol is { IsStatic: true }
            && context.SemanticModel != null
            && memberAccess.Expression is IdentifierNameSyntax simpleTypeReceiver2
            && context.SemanticModel.GetEnclosingSymbol(node.SpanStart)?.ContainingType is INamedTypeSymbol enclosingType2
            && enclosingType2.GetMembers(simpleTypeReceiver2.Identifier.Text).Any(m => m is not INamedTypeSymbol))
        {
            var ns2 = methodSymbol.ContainingType.ContainingNamespace?.ToDisplayString();
            if (ns2 == "<global namespace>")
                ns2 = string.Empty;
            receiver = string.IsNullOrWhiteSpace(ns2)
                ? methodSymbol.ContainingType.Name
                : $"{ns2}.{methodSymbol.ContainingType.Name}";
        }
        else if (methodSymbol == null)
        {
            // Semantic fallback: method resolution may fail in large project conversion even when
            // the receiver type symbol is still available (e.g. Console.WriteLine in partially
            // unresolved compilations). Use receiver type to recover method/type mappings.
            if (context.SemanticModel != null)
            {
                var receiverTypeSymbol = context.SemanticModel.GetSymbolInfo(memberAccess.Expression).Symbol as INamedTypeSymbol;
                if (receiverTypeSymbol != null)
                {
                    var receiverTypeName = receiverTypeSymbol.ToDisplayString();
                    var mappedByReceiverType = context.TypeMappings.MapMethod(receiverTypeName, originalMethodName);
                    if (mappedByReceiverType != null)
                    {
                        methodName = mappedByReceiverType;
                        var mappedReceiverType = context.TypeMappings.MapType(receiverTypeName);
                        if (mappedReceiverType != receiverTypeName)
                            receiver = mappedReceiverType;
                    }
                }
            }

            // Fallback for unresolved static calls: if a simple receiver name collides with a member
            // in the current type, but semantic type info still resolves it to a named type,
            // force fully-qualified type receiver to avoid Java member/type shadowing.
            if (context.SemanticModel != null
                && memberAccess.Expression is IdentifierNameSyntax simpleTypeReceiver3
                && context.SemanticModel.GetEnclosingSymbol(node.SpanStart)?.ContainingType is INamedTypeSymbol enclosingType3
                && enclosingType3.GetMembers(simpleTypeReceiver3.Identifier.Text).Any(m => m is not INamedTypeSymbol))
            {
                var receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression).Type as INamedTypeSymbol;
                if (receiverType != null)
                {
                    var ns3 = receiverType.ContainingNamespace?.ToDisplayString();
                    if (ns3 == "<global namespace>")
                        ns3 = string.Empty;
                    receiver = string.IsNullOrWhiteSpace(ns3)
                        ? receiverType.Name
                        : $"{ns3}.{receiverType.Name}";
                }
            }

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

            // Common unresolved fallback: receiver appears as bare "Console" in syntax,
            // but mappings are keyed by "System.Console".
            if (methodName == originalMethodName && syntacticReceiver == "Console")
            {
                var consoleMapped = context.TypeMappings.MapMethod("System.Console", originalMethodName);
                if (consoleMapped != null)
                {
                    methodName = consoleMapped;
                    receiver = context.TypeMappings.MapType("System.Console");
                }
            }

            // Console.Error.Write/WriteLine → System.err.print/println
            // Console.Error is a TextWriter property; the format-arg wrapping at the
            // println/print handler below covers multi-argument calls automatically.
            if (methodName == originalMethodName
                && syntacticReceiver is "Console.Error" or "System.Console.Error")
            {
                receiver = "System";
                methodName = originalMethodName switch
                {
                    "Write" => "err.print",
                    "WriteLine" => "err.println",
                    _ => methodName
                };
            }

            if (methodName == originalMethodName && syntacticReceiver == "String")
            {
                var stringMapped = context.TypeMappings.MapMethod("System.String", originalMethodName);
                if (stringMapped != null)
                {
                    methodName = stringMapped;
                    receiver = context.TypeMappings.MapType("System.String");
                }
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

            // System.Convert static methods → Java boxed-type parse/toString equivalents
            if (methodName == originalMethodName
                && syntacticReceiver is "Convert" or "System.Convert")
            {
                (receiver, methodName) = originalMethodName switch
                {
                    "ToBoolean" => ("Boolean", "parseBoolean"),
                    "ToInt32"   => ("Integer", "parseInt"),
                    "ToInt64"   => ("Long", "parseLong"),
                    "ToDouble"  => ("Double", "parseDouble"),
                    "ToSingle"  => ("Float", "parseFloat"),
                    "ToInt16"   => ("Short", "parseShort"),
                    "ToByte"    => ("Byte", "parseByte"),
                    "ToChar"    => ("(char)", ""),        // handled as cast below
                    "ToString"  => node.ArgumentList.Arguments.Count >= 2
                        ? ("Integer", "toString")   // Convert.ToString(val, radix)
                        : ("String", "valueOf"),     // Convert.ToString(val)
                    _ => (receiver, methodName)
                };
            }

            // LINQ unresolved fallback: in large conversions Roslyn may fail to resolve
            // Enumerable.Where/Select, leaving raw method names that later map to invalid
            // List.filter/List.map. If receiver still has IEnumerable-like type info,
            // force a stream pipeline syntactically.
            if (context.SemanticModel != null
                && originalMethodName is "Where" or "Select"
                && node.ArgumentList.Arguments.Count >= 1)
            {
                var unresolvedLinqReceiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression).Type;
                if (ImplementsIEnumerable(unresolvedLinqReceiverType) || unresolvedLinqReceiverType is IArrayTypeSymbol)
                {
                    // Skip .stream() injection if the receiver is already a stream pipeline
                    // (e.g. chained unresolved LINQ: items.Where(...).Select(...))
                    bool receiverAlreadyStream = IsReceiverLinqExtension(memberAccess.Expression, context)
                        || ReceiverLooksLikeStream(receiver);

                    var unresolvedLinqReceiver = receiverAlreadyStream
                        ? receiver
                        : BuildStreamReceiverExpression(
                            receiver,
                            unresolvedLinqReceiverType,
                            context,
                            boxPrimitiveArrayElements: false,
                            preserveGroupingValueStream: true,
                            receiverSyntaxNode: memberAccess.Expression);

                    var unresolvedArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);

                    // Add .collect() terminal when this is the outermost expression in the chain
                    // (not used as a receiver for another method call like .ToList() or .Select())
                    bool needsTerminal = node.Parent is not MemberAccessExpressionSyntax;
                    var terminal = "";
                    if (needsTerminal)
                    {
                        context.AddImport("java.util.stream.Collectors");
                        terminal = ".collect(Collectors.toList())";
                    }

                    if (originalMethodName == "Where")
                    {
                        return $"{unresolvedLinqReceiver}.filter({unresolvedArg}){terminal}";
                    }

                    return $"{unresolvedLinqReceiver}.map({unresolvedArg}){terminal}";
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
        // Also applies to [Flags] enums, which are mapped to int in Java.
        //   flagsEnumVar.ToString() → String.valueOf(flagsEnumVar)  (not .toString() on int)
        if (methodName == originalMethodName
            && context.SemanticModel != null
            && originalMethodName is "GetHashCode" or "CompareTo" or "ToString")
        {
            var receiverSymbol = context.SemanticModel
                .GetTypeInfo(memberAccess.Expression).Type;
            var wrapperClass = GetJavaWrapperForPrimitiveSpecialType(receiverSymbol?.SpecialType);

            // [Flags] enum → int in Java. Detect via Roslyn FlagsAttribute on the enum symbol.
            if (wrapperClass == null
                && receiverSymbol?.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum
                && receiverSymbol is INamedTypeSymbol namedEnumType
                && namedEnumType.GetAttributes().Any(a =>
                    a.AttributeClass?.ToDisplayString() is "System.FlagsAttribute"))
            {
                wrapperClass = "Integer";
            }

            // Fallback: flags enum registry covers cross-file scenarios where the enum
            // declaration was seen in a different file in this project compilation.
            if (wrapperClass == null
                && receiverSymbol?.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum
                && (context.IsFlagsEnum(receiverSymbol.Name)
                    || context.IsFlagsEnum(receiverSymbol.ToDisplayString() ?? string.Empty)))
            {
                wrapperClass = "Integer";
            }

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
                "ToLower"       => "toLowerCase",
                "ToUpper"       => "toUpperCase",
                "ToLowerInvariant" => "toLowerCase",
                "ToUpperInvariant" => "toUpperCase",
                _ when methodName.Length > 0
                    => char.ToLowerInvariant(methodName[0]) + methodName[1..],
                _ => methodName
            };
        }

        methodName = ConversionContext.EscapeJavaKeyword(methodName);

        // Type-erasure rename: when the resolved overload is the one with fewer type parameters,
        // append the same suffix that MethodTransformer uses at the declaration site.
        if (methodSymbol != null && ConversionContext.HasTypeErasureConflict(methodSymbol))
            methodName += ConversionContext.GetErasureRenamedSuffix(methodSymbol.TypeParameters.Length);

        // Issue 5: when promoting to static-call form, start at index 1 to skip the receiver
        // that was already prepended; use 0 for standard instance calls.
        int argStartIndex = isExtensionInStaticPath ? 1 : 0;

        // Fallback: String.IsNullOrEmpty(s) -> (s == null || s.isEmpty())
        bool isStringIsNullOrEmpty = originalMethodName == "IsNullOrEmpty"
            && node.ArgumentList.Arguments.Count - argStartIndex >= 1
            && (methodSymbol?.ContainingType.ToDisplayString() is "string" or "System.String"
                || memberAccess.Expression.ToString() is "String" or "string" or "System.String");
        if (isStringIsNullOrEmpty)
        {
            var valueExpr = facade.Transform(node.ArgumentList.Arguments[argStartIndex].Expression, context);
            return $"({valueExpr} == null || {valueExpr}.isEmpty())";
        }

        // Fallback: String.IsNullOrWhiteSpace(s) -> StringHelper.isNullOrWhiteSpace(s)
        bool isStringIsNullOrWhiteSpace = originalMethodName == "IsNullOrWhiteSpace"
            && node.ArgumentList.Arguments.Count - argStartIndex >= 1
            && (methodSymbol?.ContainingType.ToDisplayString() is "string" or "System.String"
                || memberAccess.Expression.ToString() is "String" or "string" or "System.String");
        if (isStringIsNullOrWhiteSpace)
        {
            var valueExpr = facade.Transform(node.ArgumentList.Arguments[argStartIndex].Expression, context);
            return $"StringHelper.isNullOrWhiteSpace({valueExpr})";
        }

        // Fallback: String.Concat(...) -> StringHelper.concat(...)
        bool isStringConcat = originalMethodName == "Concat"
            && node.ArgumentList.Arguments.Count - argStartIndex >= 1
            && (methodSymbol?.ContainingType.ToDisplayString() is "string" or "System.String"
                || memberAccess.Expression.ToString() is "String" or "string" or "System.String");
        if (isStringConcat)
        {
            var concatArgs = ArgumentTransformer.TransformArgumentList(
                node.ArgumentList, context, facade, argStartIndex, methodSymbol);
            return $"StringHelper.concat({concatArgs})";
        }

        // Fallback: unresolved numeric TryParse static calls.
        // Emit converter helper calls instead of invalid Double.TryParse/Integer.TryParse in Java.
        if (originalMethodName == "TryParse" && node.ArgumentList.Arguments.Count - argStartIndex >= 2)
        {
            var receiverText = memberAccess.Expression.ToString();
            string? helper = receiverText switch
            {
                "Double" or "double" or "System.Double" => "MathHelper.tryParseDouble",
                "Single" or "float" or "Float" or "System.Single" => "MathHelper.tryParseFloat",
                "Int32" or "int" or "Integer" or "System.Int32" => "MathHelper.tryParseInt",
                "Int64" or "long" or "Long" or "System.Int64" => "MathHelper.tryParseLong",
                "Boolean" or "bool" or "System.Boolean" => "MathHelper.tryParseBool",
                _ => null
            };

            if (helper != null)
            {
                var helperArgs = ArgumentTransformer.TransformArgumentList(
                    node.ArgumentList, context, facade, argStartIndex, methodSymbol);
                return $"{helper}({helperArgs})";
            }
        }

        // JsonSerializer.Deserialize<T>(json) needs the runtime class token in Java.
        // Emit deserialize(json, T.class) so the generated variable keeps type information.
        bool isJsonDeserialize = originalMethodName == "Deserialize"
            && node.ArgumentList.Arguments.Count - argStartIndex >= 1
            && (methodSymbol?.ContainingType.ToDisplayString() == "System.Text.Json.JsonSerializer"
                || memberAccess.Expression.ToString() is "JsonSerializer" or "System.Text.Json.JsonSerializer");
        if (isJsonDeserialize)
        {
            ITypeSymbol? deserializeTargetType = null;
            if (methodSymbol?.IsGenericMethod == true && methodSymbol.TypeArguments.Length == 1)
            {
                deserializeTargetType = methodSymbol.TypeArguments[0];
            }

            if (deserializeTargetType == null
                && memberAccess.Name is GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } genericDeserialize
                && context.SemanticModel != null)
            {
                deserializeTargetType = context.SemanticModel.GetTypeInfo(genericDeserialize.TypeArgumentList.Arguments[0]).Type;
            }

            if (deserializeTargetType != null)
            {
                var jsonArg = facade.Transform(node.ArgumentList.Arguments[argStartIndex].Expression, context);
                var javaType = context.MapType(deserializeTargetType);
                return $"{receiver}.{methodName}({jsonArg}, {javaType}.class)";
            }
        }

        // Mappings like Double.TryParse -> MathHelper.tryParseDouble are fully-qualified helper calls.
        // They must not be emitted as receiver.method(...), which would produce invalid
        // Double.MathHelper.tryParseDouble(...).
        if (methodName.StartsWith("MathHelper.", StringComparison.Ordinal)
            || methodName.StartsWith("EnumHelper.", StringComparison.Ordinal)
            || methodName.StartsWith("StringHelper.", StringComparison.Ordinal))
        {
            var helperArgs = ArgumentTransformer.TransformArgumentList(
                node.ArgumentList, context, facade, argStartIndex, methodSymbol);
            return $"{methodName}({helperArgs})";
        }

        // Regex.Split(input, pattern) -> Arrays.asList(input.split(pattern))
        bool isRegexSplit = originalMethodName == "Split"
            && node.ArgumentList.Arguments.Count >= 2
            && (methodSymbol?.ContainingType.ToDisplayString() == "System.Text.RegularExpressions.Regex"
                || memberAccess.Expression.ToString() is "Regex" or "System.Text.RegularExpressions.Regex");
        if (isRegexSplit)
        {
            var splitInput = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var splitPattern = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            context.AddImport("java.util.Arrays");
            return $"Arrays.asList({splitInput}.split({splitPattern}))";
        }

        // Fix: String.Split(' ') → Java split(" ") — Java's split() takes a String regex, not char.
        // Convert any char literal arguments to their regex-string equivalents.
        bool isStringSplit = originalMethodName == "Split"
            && (methodSymbol?.ContainingType.ToDisplayString() is "string" or "System.String"
                || (methodSymbol == null && memberAccess.Expression.ToString() is "string" or "String" or "System.String"));
        if (isStringSplit && node.ArgumentList.Arguments.Count > argStartIndex)
        {
            if (TryTransformSplitWithRemoveEmptyEntries(node.ArgumentList, receiver, methodName, context, facade, argStartIndex, out var removeEmptySplit))
            {
                return removeEmptySplit;
            }

            var splitArgs = TransformSplitArguments(node.ArgumentList, context, facade, argStartIndex);
            return $"{receiver}.{methodName}({splitArgs})";
        }

        // String.TrimStart([chars]) -> stripLeading() for common whitespace trimming usage.
        bool isStringTrimStart = originalMethodName == "TrimStart"
            && (methodSymbol?.ContainingType.ToDisplayString() is "string" or "System.String"
                || (methodSymbol == null && memberAccess.Expression.ToString() is "string" or "String" or "System.String"));
        if (isStringTrimStart)
        {
            return $"{receiver}.stripLeading()";
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

        if (originalMethodName == "CreateRectangleNodeOnData"
            && node.ArgumentList.Arguments.Count - argStartIndex >= 2)
        {
            var firstArg = facade.Transform(node.ArgumentList.Arguments[argStartIndex].Expression, context);
            var secondArg = facade.Transform(node.ArgumentList.Arguments[argStartIndex + 1].Expression, context);
            if ((firstArg.Contains("IntStream.range(", StringComparison.Ordinal)
                 || firstArg.Contains(".stream(", StringComparison.Ordinal)
                 || firstArg.Contains("StreamSupport.stream(", StringComparison.Ordinal))
                && !firstArg.Contains(".collect(", StringComparison.Ordinal)
                && !firstArg.EndsWith(".toList()", StringComparison.Ordinal))
            {
                context.AddImport("java.util.stream.Collectors");
                firstArg = $"{firstArg}.collect(Collectors.toList())";
            }
            return $"{receiver}.{methodName}({firstArg}, {secondArg})";
        }

        // Console.Write/WriteLine(format, args...) and TextWriter/PrintWriter print/println(format, args...)
        // only accept a single argument in Java; multi-arg C# overloads are formatting calls.
        bool isJavaPrintln = methodName == "println"
            || (receiver == "System" && (methodName == "out.println" || methodName == "err.println"));
        bool isJavaPrint = methodName == "print"
            || (receiver == "System" && (methodName == "out.print" || methodName == "err.print"));
        if ((isJavaPrintln || isJavaPrint) && node.ArgumentList.Arguments.Count > argStartIndex + 1)
        {
            var firstArg = node.ArgumentList.Arguments[argStartIndex];
            var remainingArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, argStartIndex + 1);
            var formatExpr = firstArg.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } printlnFmtLit
                ? RewriteStringFormatLiteral(printlnFmtLit.Token.ValueText)
                : facade.Transform(firstArg.Expression, context);
            var printTarget = $"{receiver}.{methodName}";
            return $"{printTarget}(String.format({formatExpr}, {remainingArgs}))";
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
                    preserveGroupingValueStream: false,
                    receiverSyntaxNode: memberAccess.Expression);
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
                var linqReceiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression).Type;
                receiver = BuildStreamReceiverExpression(
                    receiver,
                    linqReceiverType,
                    context,
                    boxPrimitiveArrayElements: false,
                    preserveGroupingValueStream: true,
                    receiverSyntaxNode: memberAccess.Expression);
            }

            // ToList → .toList() (Java 16+) or collect(Collectors.toList())
            if (originalMethodName == "ToList")
            {
                if (receiver.EndsWith(".stream()", StringComparison.Ordinal))
                {
                    context.AddImport("java.util.stream.StreamSupport");
                    var baseReceiver = receiver[..^".stream()".Length];
                    receiver = $"StreamSupport.stream({baseReceiver}.spliterator(), false)";
                }

                bool needsArrayListMaterialization = ShouldMaterializeArrayListForToList(node, context);
                if (context.Options.TargetJavaVersion >= JavaVersion.Java25)
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

            // Contains on stream -> materialize to Set and call contains(value).
            // This avoids lambda capture constraints (effectively-final) in Java loops.
            // Outside loops, use the more idiomatic anyMatch() terminal operation.
            if (originalMethodName == "Contains" && node.ArgumentList.Arguments.Count >= 1)
            {
                var valArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var containsSourceType = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type;
                bool isPrimitiveArraySource = containsSourceType is IArrayTypeSymbol arr
                    && arr.ElementType.SpecialType is not SpecialType.None
                    && arr.ElementType.SpecialType is not SpecialType.System_Object;
                if (isPrimitiveArraySource)
                {
                    return $"{receiver}.boxed().collect(Collectors.toSet()).contains({valArg})";
                }
                // Inside loops, use collect+contains to avoid effectively-final capture issues
                bool insideLoop = node.Ancestors().Any(a => a is ForStatementSyntax
                    || a is ForEachStatementSyntax || a is WhileStatementSyntax
                    || a is DoStatementSyntax);
                if (insideLoop)
                {
                    return $"{receiver}.collect(Collectors.toSet()).contains({valArg})";
                }
                return $"{receiver}.anyMatch(_item -> java.util.Objects.equals(_item, {valArg}))";
            }

            // Concat → Stream.concat(stream, other)
            // If the argument expression is already a LINQ-transformed stream, use it as-is;
            // otherwise wrap via BuildStreamExpression (handles Array / Collection / Iterable / Dictionary).
            if (originalMethodName == "Concat" && node.ArgumentList.Arguments.Count >= 1)
            {
                var concatArgExpr = node.ArgumentList.Arguments[0].Expression;
                var otherArg = facade.Transform(concatArgExpr, context);
                string otherStream;
                if (concatArgExpr is ImplicitArrayCreationExpressionSyntax implicitArray
                    && implicitArray.Initializer.Expressions.Count == 1)
                {
                    var single = facade.Transform(implicitArray.Initializer.Expressions[0], context);
                    otherStream = $"java.util.stream.Stream.of({single})";
                }
                else
                {
                if (IsReceiverLinqExtension(concatArgExpr, context))
                    otherStream = otherArg; // Already a Java stream from LINQ chain transformation
                else
                {
                    var otherType = context.SemanticModel?.GetTypeInfo(concatArgExpr).Type;
                    otherStream = ExpressionTransformerHelpers.BuildStreamExpression(
                        otherArg, otherType, context, boxPrimitiveArrayElements: true);
                }
                }
                var concatReceiver = receiver;
                var concatReceiverType = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type;
                if (concatReceiverType is IArrayTypeSymbol concatArr
                    && concatArr.ElementType.SpecialType is not SpecialType.None
                    && concatArr.ElementType.SpecialType is not SpecialType.System_Object)
                {
                    concatReceiver = $"{concatReceiver}.boxed()";
                }

                var concatStream = $"java.util.stream.Stream.concat({concatReceiver}, {otherStream})";
                var concatType = context.SemanticModel?.GetTypeInfo(node).Type as INamedTypeSymbol;
                bool returnsEnumerable = concatType?.Name == "IEnumerable"
                    && concatType.ContainingNamespace?.ToDisplayString().StartsWith("System") == true;
                bool isChained = node.Parent is MemberAccessExpressionSyntax ma && ma.Expression == node;
                if (returnsEnumerable && !isChained)
                {
                    context.AddImport("java.util.stream.Collectors");
                    return $"{concatStream}.collect(Collectors.toList())";
                }
                return concatStream;
            }

            // Where → filter(predicate)
            // Indexed form Where((element, index) => ...) uses IntStream.range pattern
            if (originalMethodName == "Where" && node.ArgumentList.Arguments.Count >= 1)
            {
                var whereReceiver = receiver;
                if (LooksLikeMaterializedCollectionExpression(whereReceiver))
                {
                    context.AddImport("java.util.stream.StreamSupport");
                    whereReceiver = $"StreamSupport.stream(({whereReceiver}).spliterator(), false)";
                }

                var whereLambdaArg = node.ArgumentList.Arguments[0].Expression;
                if (TryGetTwoParamLambda(whereLambdaArg, context, facade, out var whP0, out var whP1, out var whCond))
                {
                    // Where((x, i) => cond): collect, range, filter by index, re-select element
                    context.AddImport("java.util.stream.IntStream");
                    context.AddImport("java.util.stream.Collectors");
                    var whereIndexed = $"{whereReceiver}.collect(Collectors.collectingAndThen(Collectors.toList(),"
                                     + $" _src -> IntStream.range(0, _src.size())"
                                     + $".filter(_i -> {{ var {whP0} = _src.get(_i); int {whP1} = _i; return {whCond}; }})"
                                     + $".mapToObj(_src::get)))";
                    var whereType = context.SemanticModel?.GetTypeInfo(node).Type as INamedTypeSymbol;
                    bool whereReturnsEnumerable = whereType?.Name == "IEnumerable"
                        && whereType.ContainingNamespace?.ToDisplayString().StartsWith("System") == true;
                    bool whereIsChained = node.Parent is MemberAccessExpressionSyntax maWhere && maWhere.Expression == node;
                    return whereReturnsEnumerable && !whereIsChained
                        ? $"{whereIndexed}.collect(Collectors.toList())"
                        : whereIndexed;
                }
                var predArg = facade.Transform(whereLambdaArg, context);
                return $"{whereReceiver}.filter({predArg})";
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
                    var selectIndexed = $"{receiver}.collect(Collectors.collectingAndThen(Collectors.toList(),"
                                      + $" _src -> IntStream.range(0, _src.size())"
                                      + $".mapToObj(_i -> {{ var {selP0} = _src.get(_i); int {selP1} = _i; return {selBody}; }})))";
                    var selectType = context.SemanticModel?.GetTypeInfo(node).Type as INamedTypeSymbol;
                    bool selectReturnsEnumerable = selectType?.Name == "IEnumerable"
                        && selectType.ContainingNamespace?.ToDisplayString().StartsWith("System") == true;
                    bool selectIsChained = node.Parent is MemberAccessExpressionSyntax maSelect && maSelect.Expression == node;
                    return selectReturnsEnumerable && !selectIsChained
                        ? $"{selectIndexed}.collect(Collectors.toList())"
                        : selectIndexed;
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
                        ParenthesizedLambdaExpressionSyntax pl when pl.ParameterList.Parameters.Count == 1
                            => pl.ParameterList.Parameters[0].Identifier.Text,
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
                        else if (ImplementsIEnumerable(bodyRetType))
                        {
                            string bodyStr = facade.Transform(smBodyExpr, context);
                            string bodyStream = BuildStreamReceiverExpression(
                                bodyStr,
                                bodyRetType,
                                context,
                                boxPrimitiveArrayElements: false,
                                preserveGroupingValueStream: false);
                            flatMapArg = $"{smParam} -> {bodyStream}";
                        }
                        else
                            flatMapArg = facade.Transform(smArg.Expression, context);
                    }
                    else
                    {
                        var selector = facade.Transform(smArg.Expression, context);
                        if (selector.Contains("::", StringComparison.Ordinal)
                            && context.SemanticModel?.GetSymbolInfo(smArg.Expression).Symbol is IMethodSymbol selMethod
                            && ImplementsIEnumerable(selMethod.ReturnType))
                        {
                            var parts = selector.Split(new[] { "::" }, StringSplitOptions.None);
                            if (parts.Length == 2)
                            {
                                context.AddImport("java.util.stream.StreamSupport");
                                var p = "_sm";
                                flatMapArg = $"{p} -> StreamSupport.stream({parts[0]}.{parts[1]}({p}).spliterator(), false)";
                            }
                            else
                            {
                                flatMapArg = selector;
                            }
                        }
                        else
                        {
                            flatMapArg = selector;
                        }
                    }
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

            // SkipWhile → dropWhile(predicate) (Java 25)
            if (originalMethodName == "SkipWhile" && node.ArgumentList.Arguments.Count >= 1)
            {
                var predArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.dropWhile({predArg})";
            }

            // TakeWhile → takeWhile(predicate) (Java 25)
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
                bool primitiveArrayAggregateSource = context.SemanticModel.GetTypeInfo(memberAccess.Expression).Type is IArrayTypeSymbol srcArr1
                    && srcArr1.ElementType.IsValueType;
                var aggregateReceiver = primitiveArrayAggregateSource ? $"{receiver}.boxed()" : receiver;

                var seedArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var funcArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                var seedType1 = context.SemanticModel?.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type;
                var sourceType1 = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type;
                var sourceElemType1 = sourceType1 switch
                {
                    IArrayTypeSymbol arr1 => arr1.ElementType,
                    INamedTypeSymbol named1 => ExtractEnumerableElementType(named1),
                    _ => null
                };
                bool needsAccumulatorReduce1 = primitiveArrayAggregateSource
                    || (seedType1 != null && sourceElemType1 != null
                        && !SymbolEqualityComparer.Default.Equals(seedType1, sourceElemType1));
                string reduceExpr = primitiveArrayAggregateSource
                    ? $"{aggregateReceiver}.reduce({seedArg}, {funcArg}, (__accLeft, __accRight) -> __accRight)"
                    : needsAccumulatorReduce1
                        ? $"{aggregateReceiver}.reduce({seedArg}, {funcArg}, (__accLeft, __accRight) -> __accRight)"
                        : $"{aggregateReceiver}.reduce({seedArg}, {funcArg})";
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
                bool primitiveArrayAggregateSource = context.SemanticModel.GetTypeInfo(memberAccess.Expression).Type is IArrayTypeSymbol srcArr2
                    && srcArr2.ElementType.IsValueType;
                var aggregateReceiver = primitiveArrayAggregateSource ? $"{receiver}.boxed()" : receiver;

                var seedArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var funcArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                var seedType2 = context.SemanticModel?.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type;
                var sourceType2 = context.SemanticModel?.GetTypeInfo(memberAccess.Expression).Type;
                var sourceElemType2 = sourceType2 switch
                {
                    IArrayTypeSymbol arr2 => arr2.ElementType,
                    INamedTypeSymbol named2 => ExtractEnumerableElementType(named2),
                    _ => null
                };
                bool needsAccumulatorReduce2 = primitiveArrayAggregateSource
                    || (seedType2 != null && sourceElemType2 != null
                        && !SymbolEqualityComparer.Default.Equals(seedType2, sourceElemType2));
                return primitiveArrayAggregateSource
                    ? $"{aggregateReceiver}.reduce({seedArg}, {funcArg}, (__accLeft, __accRight) -> __accRight)"
                    : needsAccumulatorReduce2
                        ? $"{aggregateReceiver}.reduce({seedArg}, {funcArg}, (__accLeft, __accRight) -> __accRight)"
                        : $"{aggregateReceiver}.reduce({seedArg}, {funcArg})";
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
                if (zipOtherType is IArrayTypeSymbol zipArrType2 && zipArrType2.ElementType.IsValueType
                    && context.MapType(zipArrType2.ElementType) is "int" or "long" or "double")
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
                var unionArgExpr = node.ArgumentList.Arguments[0].Expression;
                var otherArg = facade.Transform(unionArgExpr, context);
                string otherStream;
                if (IsReceiverLinqExtension(unionArgExpr, context))
                    otherStream = otherArg;
                else
                {
                    var otherType = context.SemanticModel?.GetTypeInfo(unionArgExpr).Type;
                    otherStream = ExpressionTransformerHelpers.BuildStreamExpression(
                        otherArg, otherType, context, boxPrimitiveArrayElements: true);
                }
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
                string ubOtherStream;
                if (IsReceiverLinqExtension(ubOtherArg0.Expression, context))
                    ubOtherStream = ubOther;
                else
                {
                    var ubOtherType = context.SemanticModel?.GetTypeInfo(ubOtherArg0.Expression).Type;
                    ubOtherStream = ExpressionTransformerHelpers.BuildStreamExpression(
                        ubOther, ubOtherType, context, boxPrimitiveArrayElements: true);
                }
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
                var jInnerStream = ExpressionTransformerHelpers.BuildStreamExpression(jInner, jInnerType, context);
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
                var gjInnerStream = ExpressionTransformerHelpers.BuildStreamExpression(gjInner, gjInnerType, context);
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

        // Strip IFormatProvider/CultureInfo arguments from ToString() calls.
        // Java's toString() does not accept locale parameters.
        if (originalMethodName == "ToString"
            && node.ArgumentList.Arguments.Count - argStartIndex == 1
            && HasIFormatProviderFirstArg(node, context))
        {
            argStartIndex = node.ArgumentList.Arguments.Count; // skip all args
        }

        // Strip trailing IFormatProvider/CultureInfo/NumberStyles arguments from Parse methods.
        // Java's Integer.parseInt, Double.parseDouble, etc. do not accept locale/style parameters.
        int parseStripCount = 0;
        if (originalMethodName == "Parse"
            && node.ArgumentList.Arguments.Count - argStartIndex >= 2)
        {
            int lastArgIdx = node.ArgumentList.Arguments.Count - 1;
            if (HasIFormatProviderOrNumberStylesArg(node.ArgumentList.Arguments[lastArgIdx].Expression, context))
            {
                parseStripCount = 1;
                // If second-to-last is also a NumberStyles/IFormatProvider, strip both
                if (node.ArgumentList.Arguments.Count - argStartIndex >= 3
                    && HasIFormatProviderOrNumberStylesArg(node.ArgumentList.Arguments[lastArgIdx - 1].Expression, context))
                {
                    parseStripCount = 2;
                }
            }
        }

        var args = parseStripCount > 0
            ? ArgumentTransformer.TransformArgumentList(
                node.ArgumentList, context, facade, argStartIndex, methodSymbol,
                maxArgCount: node.ArgumentList.Arguments.Count - parseStripCount)
            : ArgumentTransformer.TransformArgumentList(
                node.ArgumentList, context, facade, argStartIndex, methodSymbol);

        if (methodName == "toList" && string.IsNullOrEmpty(args))
        {
            context.AddImport("java.util.stream.StreamSupport");
            return $"StreamSupport.stream({receiver}.spliterator(), false).toList()";
        }

        if (methodName is "filter" or "map" or "flatMap"
            && context.SemanticModel != null)
        {
            var fallbackStreamType = context.SemanticModel.GetTypeInfo(memberAccess.Expression).Type;
            if (ImplementsIEnumerable(fallbackStreamType) || fallbackStreamType is IArrayTypeSymbol)
            {
                var fallbackStreamReceiver = BuildStreamReceiverExpression(
                    receiver,
                    fallbackStreamType,
                    context,
                    boxPrimitiveArrayElements: false,
                    preserveGroupingValueStream: true);
                return $"{fallbackStreamReceiver}.{methodName}({args})";
            }
        }

        // Safety net: when a method mapping returns a fully-qualified helper call
        // (e.g. "StringHelper.compare", "System.getenv"), emit it standalone without the
        // receiver prefix. The primary check at the StringHelper/MathHelper/EnumHelper guard
        // above should have caught most cases, but syntactic fallback paths may bypass it.
        // Do NOT match patterns like "out.println" which are partial receiver chains.
        if (methodName.StartsWith("MathHelper.", StringComparison.Ordinal)
            || methodName.StartsWith("EnumHelper.", StringComparison.Ordinal)
            || methodName.StartsWith("StringHelper.", StringComparison.Ordinal)
            || methodName.StartsWith("System.getenv", StringComparison.Ordinal))
        {
            return $"{methodName}({args})";
        }

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
    /// Checks whether the first argument of an invocation is an IFormatProvider/CultureInfo
    /// that should be stripped when converting to Java (Java's String.format and ToString
    /// do not accept IFormatProvider).
    /// </summary>
    private static bool HasIFormatProviderFirstArg(InvocationExpressionSyntax node, ConversionContext context)
    {
        if (node.ArgumentList.Arguments.Count == 0)
            return false;

        var firstArg = node.ArgumentList.Arguments[0].Expression;

        // Semantic check: resolve the parameter type
        if (context.SemanticModel != null)
        {
            var typeInfo = context.SemanticModel.GetTypeInfo(firstArg);
            var typeName = typeInfo.Type?.ToDisplayString();
            if (typeName is "System.IFormatProvider" or "System.Globalization.CultureInfo"
                or "System.Globalization.NumberFormatInfo")
                return true;
            // Also check interfaces
            if (typeInfo.Type != null)
            {
                foreach (var iface in typeInfo.Type.AllInterfaces)
                {
                    if (iface.ToDisplayString() == "System.IFormatProvider")
                        return true;
                }
            }
        }

        // Syntactic fallback: check for common CultureInfo patterns
        var argText = firstArg.ToString();
        if (argText.StartsWith("CultureInfo.", System.StringComparison.Ordinal)
            || argText == "NumberFormatInfo.InvariantInfo"
            || argText.StartsWith("NumberFormatInfo.", System.StringComparison.Ordinal))
            return true;

        return false;
    }

    /// <summary>
    /// Checks whether an expression is an IFormatProvider, CultureInfo, or NumberStyles argument
    /// that should be stripped when converting Parse methods to Java equivalents.
    /// </summary>
    private static bool HasIFormatProviderOrNumberStylesArg(ExpressionSyntax expr, ConversionContext context)
    {
        // Semantic check
        if (context.SemanticModel != null)
        {
            var typeInfo = context.SemanticModel.GetTypeInfo(expr);
            var typeName = typeInfo.Type?.ToDisplayString();
            if (typeName is "System.IFormatProvider" or "System.Globalization.CultureInfo"
                or "System.Globalization.NumberFormatInfo" or "System.Globalization.NumberStyles")
                return true;
            if (typeInfo.Type != null)
            {
                foreach (var iface in typeInfo.Type.AllInterfaces)
                {
                    if (iface.ToDisplayString() == "System.IFormatProvider")
                        return true;
                }
            }
        }

        // Syntactic fallback
        var argText = expr.ToString();
        if (argText.StartsWith("CultureInfo.", System.StringComparison.Ordinal)
            || argText.StartsWith("NumberFormatInfo.", System.StringComparison.Ordinal)
            || argText.StartsWith("NumberStyles.", System.StringComparison.Ordinal)
            || argText.Contains("getUSCultureInfo()", System.StringComparison.Ordinal)
            || argText == "NumberFormatInfo.InvariantInfo"
            || argText.Contains("Locale.ROOT", System.StringComparison.Ordinal))
            return true;

        return false;
    }

    private static bool TryTransformSplitWithRemoveEmptyEntries(
        ArgumentListSyntax argumentList,
        string receiver,
        string methodName,
        ConversionContext context,
        ExpressionTransformerFacade facade,
        int argStartIndex,
        out string transformed)
    {
        transformed = string.Empty;

        if (argumentList.Arguments.Count <= argStartIndex + 1)
            return false;

        var separatorExpr = argumentList.Arguments[argStartIndex].Expression;
        var optionsExpr = argumentList.Arguments[argStartIndex + 1].Expression;
        var optionsText = optionsExpr.ToString();
        if (!optionsText.Contains("RemoveEmptyEntries", StringComparison.Ordinal))
            return false;

        if (!TryBuildSplitRegexPattern(separatorExpr, out var regexLiteral))
            return false;

        context.AddImport("java.util.Arrays");
        transformed = $"Arrays.stream({receiver}.{methodName}({regexLiteral})).filter(s -> !s.isEmpty()).toArray(String[]::new)";
        return true;
    }

    private static bool TryBuildSplitRegexPattern(ExpressionSyntax separatorExpr, out string regexLiteral)
    {
        regexLiteral = string.Empty;

        if (separatorExpr is LiteralExpressionSyntax charLiteral
            && charLiteral.IsKind(SyntaxKind.CharacterLiteralExpression))
        {
            regexLiteral = $"\"{EscapeRegexChar(charLiteral.Token.ValueText)}\"";
            return true;
        }

        if (separatorExpr is ArrayCreationExpressionSyntax { Initializer: { } arrayInit })
        {
            return TryBuildRegexClassFromInitializer(arrayInit.Expressions, out regexLiteral);
        }

        if (separatorExpr is ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitInit })
        {
            return TryBuildRegexClassFromInitializer(implicitInit.Expressions, out regexLiteral);
        }

        return false;
    }

    private static bool TryBuildRegexClassFromInitializer(
        SeparatedSyntaxList<ExpressionSyntax> expressions,
        out string regexLiteral)
    {
        regexLiteral = string.Empty;
        if (expressions.Count == 0)
            return false;

        var classChars = new List<string>();
        foreach (var expr in expressions)
        {
            if (expr is not LiteralExpressionSyntax lit || !lit.IsKind(SyntaxKind.CharacterLiteralExpression))
                return false;

            classChars.Add(EscapeRegexClassChar(lit.Token.ValueText));
        }

        regexLiteral = $"\"[{string.Concat(classChars)}]\"";
        return true;
    }

    private static string EscapeRegexClassChar(string ch)
    {
        if (string.IsNullOrEmpty(ch))
            return ch;

        if (ch.Length == 1 && "\\^-]".Contains(ch[0]))
            return "\\" + ch;

        return ch;
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
    /// Heuristic: checks if a transformed receiver string already contains Java stream
    /// pipeline operations, indicating it was already converted from a LINQ chain.
    /// Used in the unresolved LINQ fallback to avoid injecting .stream() twice.
    /// </summary>
    private static bool ReceiverLooksLikeStream(string receiver)
    {
        return receiver.Contains(".filter(", StringComparison.Ordinal)
            || receiver.Contains(".map(", StringComparison.Ordinal)
            || receiver.Contains(".flatMap(", StringComparison.Ordinal)
            || receiver.Contains(".sorted(", StringComparison.Ordinal)
            || receiver.EndsWith(".stream()", StringComparison.Ordinal)
            || receiver.Contains("Arrays.stream(", StringComparison.Ordinal)
            || receiver.Contains("StreamSupport.stream(", StringComparison.Ordinal);
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
        if (elementType.TypeKind == TypeKind.TypeParameter)
        {
            // Java cannot create typed arrays for type parameters due to erasure;
            // use lambda-based toArray with unchecked cast: toArray(size -> (T[]) new Object[size])
            return $"{receiver}.toArray(size -> ({javaType}[]) new Object[size])";
        }
        if (!string.IsNullOrEmpty(javaType) && javaType != "Object")
        {
            return $"{receiver}.toArray({javaType}[]::new)";
        }

        return $"{receiver}.toArray()";
    }

    private static string TransformInstanceCollectionToArray(
        string receiver,
        ITypeSymbol elementType,
        ConversionContext context)
    {
        var specialType = elementType.SpecialType;
        if (specialType is SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Byte)
            return $"{receiver}.stream().mapToInt(Integer::intValue).toArray()";
        if (specialType is SpecialType.System_Int64)
            return $"{receiver}.stream().mapToLong(Long::longValue).toArray()";
        if (specialType is SpecialType.System_Double or SpecialType.System_Single)
            return $"{receiver}.stream().mapToDouble(Double::doubleValue).toArray()";

        if (elementType.TypeKind == TypeKind.TypeParameter)
        {
            // Java cannot create typed arrays for type parameters due to erasure;
            // use lambda-based toArray with unchecked cast
            var javaTypeParam = context.MapType(elementType);
            return $"{receiver}.stream().toArray(size -> ({javaTypeParam}[]) new Object[size])";
        }
        var javaType = context.MapType(elementType);
        if (!string.IsNullOrEmpty(javaType) && javaType != "Object")
            return $"{receiver}.toArray({javaType}[]::new)";

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

    private static bool ImplementsIEnumerable(ITypeSymbol? type)
    {
        if (type == null)
            return false;

        if (type is IArrayTypeSymbol)
            return true;

        if (type is INamedTypeSymbol named)
        {
            if (named.Name == "IEnumerable" && named.TypeArguments.Length == 1)
                return true;

            return named.AllInterfaces.Any(i => i.Name == "IEnumerable" && i.TypeArguments.Length == 1);
        }

        return false;
    }

    private static bool LooksLikeMaterializedCollectionExpression(string receiverExpr)
    {
        if (string.IsNullOrWhiteSpace(receiverExpr))
            return false;

        return receiverExpr.Contains(".collect(Collectors.toList())", StringComparison.Ordinal)
            || receiverExpr.Contains(".collect(java.util.stream.Collectors.toList())", StringComparison.Ordinal)
            || receiverExpr.EndsWith(".toList()", StringComparison.Ordinal);
    }

    private static bool IsSimpleIdentifier(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (!(char.IsLetter(text[0]) || text[0] == '_'))
            return false;

        for (int i = 1; i < text.Length; i++)
        {
            var ch = text[i];
            if (!(char.IsLetterOrDigit(ch) || ch == '_'))
                return false;
        }

        return true;
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
        bool preserveGroupingValueStream,
        ExpressionSyntax? receiverSyntaxNode = null)
        => ExpressionTransformerHelpers.BuildStreamExpression(
            receiverExpr, receiverType, context, boxPrimitiveArrayElements, preserveGroupingValueStream, receiverSyntaxNode);

    private static bool CanCallCollectionStream(ITypeSymbol? receiverType)
        => ExpressionTransformerHelpers.CanCallCollectionStream(receiverType);

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

    private static bool TryMapMSTestAssertInvocation(
        ExpressionSyntax receiverExpression,
        string originalMethodName,
        IMethodSymbol? methodSymbol,
        out string junitMethodName)
    {
        junitMethodName = string.Empty;

        var receiverText = receiverExpression.ToString();
        var isAssertReceiver = receiverText is "Assert" or "Microsoft.VisualStudio.TestTools.UnitTesting.Assert";
        var containingType = methodSymbol?.ContainingType.ToDisplayString();
        var isMSTestAssert = containingType == "Microsoft.VisualStudio.TestTools.UnitTesting.Assert";
        if (!isAssertReceiver && !isMSTestAssert)
        {
            return false;
        }

        junitMethodName = originalMethodName switch
        {
            "AreEqual" => "assertEquals",
            "AreNotEqual" => "assertNotEquals",
            "IsTrue" => "assertTrue",
            "IsFalse" => "assertFalse",
            "IsNull" => "assertNull",
            "IsNotNull" => "assertNotNull",
            "Fail" => "fail",
            _ => string.Empty,
        };

        return !string.IsNullOrEmpty(junitMethodName);
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

    private static bool IsDictionaryLikeExpression(ExpressionSyntax expression, ConversionContext context)
    {
        var type = context.SemanticModel?.GetTypeInfo(expression).Type as INamedTypeSymbol;
        if (type == null)
            return false;

        bool IsDictionaryType(INamedTypeSymbol t)
            => t.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
               && t.Name is "Dictionary" or "SortedDictionary" or "IDictionary" or "IReadOnlyDictionary";

        if (IsDictionaryType(type))
            return true;

        return type.AllInterfaces.Any(IsDictionaryType);
    }

    private static bool IsEnumeratorMoveNextInvocation(InvocationExpressionSyntax node, ConversionContext context)
    {
        var method = context.SemanticModel?.GetSymbolInfo(node).Symbol as IMethodSymbol;
        if (method == null || method.Name != "MoveNext" || method.Parameters.Length != 0)
            return false;

        var t = method.ContainingType;
        bool IsEnumerator(INamedTypeSymbol nt)
            => (nt.ContainingNamespace?.ToDisplayString() == "System.Collections" && nt.Name == "IEnumerator")
               || (nt.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic" && nt.Name == "IEnumerator")
               || nt.Name.Contains("Enumerator", StringComparison.Ordinal);

        if (IsEnumerator(t))
            return true;

        return t.AllInterfaces.Any(IsEnumerator);
    }

    private static bool IsStaticNullSafeEqualsMethod(IMethodSymbol? methodSymbol)
    {
        if (methodSymbol is null || !methodSymbol.IsStatic || methodSymbol.Name != "Equals" || methodSymbol.Parameters.Length != 2)
            return false;

        var containingType = methodSymbol.ContainingType;
        return containingType?.SpecialType is SpecialType.System_Object or SpecialType.System_String;
    }

    private static bool IsSystemStringType(ITypeSymbol? typeSymbol)
    {
        return typeSymbol?.SpecialType == SpecialType.System_String;
    }

    private static bool IsSystemStringMethod(IMethodSymbol? methodSymbol, ExpressionSyntax receiverExpression)
    {
        if (methodSymbol?.ContainingType?.SpecialType == SpecialType.System_String)
            return true;

        var receiverText = receiverExpression.ToString();
        return receiverText is "String" or "System.String";
    }

    private static bool TryGetStringComparisonIgnoreCase(ExpressionSyntax expression, SemanticModel? semanticModel, out bool ignoreCase)
    {
        if (semanticModel?.GetConstantValue(expression) is { HasValue: true, Value: int comparisonValue })
        {
            ignoreCase = comparisonValue is 1 or 3 or 5;
            return true;
        }

        if (semanticModel?.GetSymbolInfo(expression).Symbol is IFieldSymbol fieldSymbol
            && fieldSymbol.ContainingType?.ToDisplayString() == "System.StringComparison")
        {
            ignoreCase = fieldSymbol.Name.EndsWith("IgnoreCase", StringComparison.Ordinal);
            return true;
        }

        var text = expression.ToString();
        if (text.Contains("StringComparison.", StringComparison.Ordinal))
        {
            ignoreCase = text.EndsWith("IgnoreCase", StringComparison.Ordinal);
            return true;
        }

        ignoreCase = false;
        return false;
    }

    private static string ToJavaBooleanLiteral(bool value) => value ? "true" : "false";
}
