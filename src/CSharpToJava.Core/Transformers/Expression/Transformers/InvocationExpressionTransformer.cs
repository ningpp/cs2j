using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression.Utilities;
using CSharpToJava.Core.Utilities;
using CSharpToJava.Core.Transformers.Member;
using CSharpToJava.Core.Transformers;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles method invocation expressions.
/// </summary>
[TransformerRegistration]
public class InvocationExpressionTransformer : IIRExpressionTransformer
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
        ["UInt32"]  = "int",
        ["UInt64"]  = "long",
        ["UInt16"]  = "short",
        ["Single"]  = "float",
        ["Double"]  = "double",
        ["Boolean"] = "boolean",
        ["Char"]    = "char",
    };

    public string Transform(ExpressionSyntax node, ConversionContext context) => node.Kind() switch
    {
        SyntaxKind.InvocationExpression => TransformInvocation((InvocationExpressionSyntax)node, context),
        _ => throw new NotSupportedException($"Invocation expression kind {node.Kind()} not supported.")
    };

    /// <inheritdoc />
    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
    {
        if (node is not InvocationExpressionSyntax invocation)
            return new JavaRawExpression(Transform(node, context));

        var facade = ExpressionTransformerFacade.Instance;

        // nameof(x) → "x" string literal
        if (invocation.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" }
            && invocation.ArgumentList.Arguments.Count == 1)
        {
            var nameofResult = TransformNameof(invocation.ArgumentList.Arguments[0].Expression);
            return new JavaLiteralExpression { Value = nameofResult };
        }

        // ReferenceEquals(a, b) → a == b
        if (invocation.Expression is IdentifierNameSyntax { Identifier.Text: "ReferenceEquals" }
            && invocation.ArgumentList.Arguments.Count == 2)
        {
            return new JavaBinaryExpression
            {
                Left = facade.TransformToIR(invocation.ArgumentList.Arguments[0].Expression, context),
                Operator = "==",
                Right = facade.TransformToIR(invocation.ArgumentList.Arguments[1].Expression, context)
            };
        }

        // Static Equals(a, b) → Objects.equals(a, b)
        if (invocation.Expression is IdentifierNameSyntax { Identifier.Text: "Equals" }
            && invocation.ArgumentList.Arguments.Count == 2
            && IsStaticNullSafeEqualsMethod(context.GetSymbolInfo(invocation).Symbol as IMethodSymbol))
        {
            context.AddImport("java.util.Objects");
            var call = new JavaMethodCallExpression
            {
                Target = new JavaIdentifierExpression { Name = "Objects" },
                MethodName = "equals"
            };
            call.Arguments.Add(facade.TransformToIR(invocation.ArgumentList.Arguments[0].Expression, context));
            call.Arguments.Add(facade.TransformToIR(invocation.ArgumentList.Arguments[1].Expression, context));
            return call;
        }

        // Generic method call: Method<T>(args) → method(args)
        if (invocation.Expression is GenericNameSyntax genericMethodName
            && !HasComplexArguments(invocation.ArgumentList))
        {
            var methodName = ApplyCamelCaseAndMappings(genericMethodName.Identifier.Text, invocation, context);
            var bareMethodSym = context.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (bareMethodSym != null)
                methodName += ConversionContext.GetErasureConflictSuffix(bareMethodSym);
            var call = new JavaMethodCallExpression { MethodName = methodName };
            AddArgumentsAsIR(call, invocation.ArgumentList, context, facade);
            return call;
        }

        // Bare identifier call (non-delegate, non-member-access)
        if (invocation.Expression is IdentifierNameSyntax bareIdent
            && !HasComplexArguments(invocation.ArgumentList))
        {
            // Check for delegate invocation — fall back to raw (complex logic)
            if (context.SemanticModel != null)
            {
                var symInfo = context.GetSymbolInfo(invocation);
                if (symInfo.Symbol is IMethodSymbol { MethodKind: MethodKind.DelegateInvoke })
                    return new JavaRawExpression(Transform(node, context));

                // Fallback for System.Delegate / System.MulticastDelegate typed variables
                // (e.g. from MethodInfo.CreateDelegate(Type) which returns Delegate)
                var irBareTypeInfo = context.GetTypeInfo(bareIdent);
                if (irBareTypeInfo.Type is INamedTypeSymbol irBareDelType
                    && (irBareDelType.ToDisplayString() == "System.Delegate"
                        || irBareDelType.ToDisplayString() == "System.MulticastDelegate"
                        || irBareDelType.TypeKind == TypeKind.Delegate))
                    return new JavaRawExpression(Transform(node, context));
            }

            var methodName = ApplyCamelCaseAndMappings(bareIdent.Identifier.Text, invocation, context);
            var bareMethodSym2 = context.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (bareMethodSym2 != null)
                methodName += ConversionContext.GetErasureConflictSuffix(bareMethodSym2);
            var call = new JavaMethodCallExpression { MethodName = methodName };
            AddArgumentsAsIR(call, invocation.ArgumentList, context, facade);
            return call;
        }

        // MemberAccess invocations and all other complex paths → raw
        return new JavaRawExpression(Transform(node, context));
    }

    /// <summary>
    /// Returns true if any argument uses named parameters or ref/out/in keywords,
    /// which require special handling only available in the string path.
    /// </summary>
    private static bool HasComplexArguments(ArgumentListSyntax argList)
    {
        foreach (var arg in argList.Arguments)
        {
            if (arg.NameColon != null) return true;
            if (arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
                || arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                || arg.RefKindKeyword.IsKind(SyntaxKind.InKeyword))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Transforms each argument expression to IR and adds to the method call.
    /// </summary>
    private static void AddArgumentsAsIR(
        JavaMethodCallExpression call,
        ArgumentListSyntax argList,
        ConversionContext context,
        ExpressionTransformerFacade facade)
    {
        foreach (var arg in argList.Arguments)
        {
            call.Arguments.Add(facade.TransformToIR(arg.Expression, context));
        }
    }

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
                && IsStaticNullSafeEqualsMethod(context.GetSymbolInfo(node).Symbol as IMethodSymbol))
            {
                var leftArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var rightArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                context.AddImport("java.util.Objects");
                return $"Objects.equals({leftArg}, {rightArg})";
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
            var bareMethodSym = context.GetSymbolInfo(node).Symbol as IMethodSymbol;
            if (bareMethodSym != null)
                methodName += ConversionContext.GetErasureConflictSuffix(bareMethodSym);
            var classTokens = bareMethodSym != null ? GetClassTypeTokensForCall(bareMethodSym, context) : null;
            var args = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, methodSymbol: bareMethodSym);
            args = PrependClassTypeTokens(args, classTokens);
            return $"{methodName}({args})";
        }

        // Bare identifier call: e.g. LandmarkClassicalScaling(...) → landmarkClassicalScaling(...)
        // Apply the same camelCase + TypeMappings conversion used for member-access calls.
        if (node.Expression is IdentifierNameSyntax bareIdent)
        {
            // Known delegate field names (semantic model can't resolve DelegateInvoke in project pipeline)
            var bareDelegateMethod = bareIdent.Identifier.Text switch
            {
                "ObstaclesToIgnore" or "obstaclesToIgnore" => "apply",  // Function<Station, Set<Polyline>>
                _ => null
            };
            if (bareDelegateMethod != null)
            {
                var bareFieldRef = facade.Transform(bareIdent, context);
                var bareArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
                return $"{bareFieldRef}.{bareDelegateMethod}({bareArgs})";
            }

            // Delegate invocation: sequence(m) where sequence is a Func/Action field/local/param.
            // Roslyn resolves the invoked method as DelegateInvoke; map it to .apply()/.get()/etc.
            if (context.SemanticModel != null)
            {
                var symInfo = context.GetSymbolInfo(node);
                if (symInfo.Symbol is IMethodSymbol { MethodKind: MethodKind.DelegateInvoke } delegateInvoke)
                {
                    // Check if this is actually an event invocation (e.g., ProgressChanged(sender, args))
                    // Events are a special case - they should use the fire method instead of delegate invocation
                    var identSymbol = context.GetSymbolInfo(bareIdent).Symbol;
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
                    var javaMethod = context.TypeMappings.MapMethod(containingTypeName, "Invoke")
                        ?? InferSamMethodName(delegateInvoke);
                    var delegateArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
                    // Use facade.Transform so properties are emitted as getXxx() rather than bare identifier.
                    // e.g. Sequence(m) where Sequence is a Func<int,double> property → getSequence().apply(m)
                    var delegateReceiver = facade.Transform(bareIdent, context);
                    return $"{delegateReceiver}.{javaMethod}({delegateArgs})";
                }

                // Fallback: the semantic model may not resolve DelegateInvoke when the variable
                // type is System.Delegate (e.g. from MethodInfo.CreateDelegate(Type) which returns
                // Delegate, not the concrete delegate type). Check the expression type directly.
                var bareExprTypeInfo = context.GetTypeInfo(bareIdent);
                if (bareExprTypeInfo.Type is INamedTypeSymbol bareDelegateType
                    && (bareDelegateType.ToDisplayString() == "System.Delegate"
                        || bareDelegateType.ToDisplayString() == "System.MulticastDelegate"
                        || bareDelegateType.TypeKind == TypeKind.Delegate))
                {
                    IMethodSymbol? bareInvokeMethod = bareDelegateType.TypeKind == TypeKind.Delegate
                        ? bareDelegateType.DelegateInvokeMethod
                        : null;
                    string bareJavaMethod = bareInvokeMethod != null
                        ? (context.TypeMappings.MapMethod(bareDelegateType.ToDisplayString(), "Invoke")
                            ?? InferSamMethodName(bareInvokeMethod))
                        : InferSamMethodName(
                            bareExprTypeInfo.ConvertedType?.TypeKind == TypeKind.Delegate
                                && bareExprTypeInfo.ConvertedType is INamedTypeSymbol convertedDel
                                && convertedDel.DelegateInvokeMethod != null
                                ? convertedDel.DelegateInvokeMethod.ReturnsVoid
                                : node.Parent is ExpressionStatementSyntax,
                            node.ArgumentList.Arguments.Count);
                    var bareDelReceiver = facade.Transform(bareIdent, context);
                    var bareDelArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
                    return $"{bareDelReceiver}.{bareJavaMethod}({bareDelArgs})";
                }
            }

            var methodName = ApplyCamelCaseAndMappings(bareIdent.Identifier.Text, node, context);
            var bareMethodSym2 = context.GetSymbolInfo(node).Symbol as IMethodSymbol;
            if (bareMethodSym2 != null)
                methodName += ConversionContext.GetErasureConflictSuffix(bareMethodSym2);
            var classTokens = bareMethodSym2 != null ? GetClassTypeTokensForCall(bareMethodSym2, context) : null;
            var args = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, methodSymbol: bareMethodSym2);
            args = PrependClassTypeTokens(args, classTokens);
            return $"{methodName}({args})";
        }

        // Delegate invocation via non-identifier expressions (e.g. dict[key](args)).
        // The existing IdentifierNameSyntax path above only handles bare identifiers;
        // this catches element-access, member-access, and other expression targets.
        if (context.SemanticModel != null)
        {
            var symInfo = context.GetSymbolInfo(node);
            if (symInfo.Symbol is IMethodSymbol { MethodKind: MethodKind.DelegateInvoke } delegateInvoke)
            {
                var containingTypeName = delegateInvoke.ContainingType.ToDisplayString();
                var javaMethod = context.TypeMappings.MapMethod(containingTypeName, "Invoke")
                    ?? InferSamMethodName(delegateInvoke);
                var delegateArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
                var delegateReceiver = facade.Transform(node.Expression, context);
                return $"{delegateReceiver}.{javaMethod}({delegateArgs})";
            }

            // Fallback: GetSymbolInfo failed (e.g. unresolved project references), but the
            // expression type may still be a delegate — check GetTypeInfo on the expression itself.
            var exprTypeInfo = context.GetTypeInfo(node.Expression);
            if (exprTypeInfo.Type is INamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateType)
            {
                var invokeMethod = delegateType.DelegateInvokeMethod;
                if (invokeMethod != null)
                {
                    var containingTypeName = delegateType.ToDisplayString();
                    var javaMethod = context.TypeMappings.MapMethod(containingTypeName, "Invoke")
                        ?? InferSamMethodName(invokeMethod);
                    var delegateArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
                    var delegateReceiver = facade.Transform(node.Expression, context);
                    return $"{delegateReceiver}.{javaMethod}({delegateArgs})";
                }
            }
        }

        // Explicit .Invoke() on a delegate when semantic analysis cannot resolve the delegate type.
        // In C#, delegate.Invoke(args) is always valid. In Java, the method depends on the functional
        // interface — we use a heuristic based on argument count and value/statement context.
        // System.Reflection.MethodInfo.Invoke is NOT a delegate invocation — skip it.
        if (node.Expression is MemberAccessExpressionSyntax explicitInvokeMa
            && explicitInvokeMa.Name.Identifier.Text == "Invoke"
            && !IsReceiverOfType(explicitInvokeMa.Expression, "System.Reflection.MethodInfo", context))
        {
            int argCount = node.ArgumentList.Arguments.Count;
            bool isStatementContext = node.Parent is ExpressionStatementSyntax;
            string samMethod = isStatementContext
                ? (argCount == 0 ? "run" : "accept")
                : (argCount == 0 ? "get" : "apply");
            var delegateArgs4 = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
            var delegateReceiver4 = facade.Transform(explicitInvokeMa.Expression, context);
            return $"{delegateReceiver4}.{samMethod}({delegateArgs4})";
        }

        // When the invocation expression is a complex expression (element-access, method-call result,
        // etc.) and the semantic model cannot resolve the delegate type (e.g. due to missing project
        // references), calling it directly as `expr(args)` is invalid Java syntax.
        // Apply a structural heuristic: use the Java SAM method name inferred from argument count
        // and whether the result is used in a value context.
        if (node.Expression is not IdentifierNameSyntax
            and not MemberAccessExpressionSyntax
            and not GenericNameSyntax)
        {
            int argCount = node.ArgumentList.Arguments.Count;
            bool isStatementContext = node.Parent is ExpressionStatementSyntax;
            string samMethod = isStatementContext
                ? (argCount == 0 ? "run" : "accept")  // void: Runnable.run / Consumer.accept
                : (argCount == 0 ? "get" : "apply");  // value: Supplier.get / Function.apply
            var delegateArgs3 = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
            var delegateReceiver3 = facade.Transform(node.Expression, context);
            context.Diagnostics.Warning(
                $"Delegate invocation via complex expression '{node.Expression}'; " +
                $"delegate type could not be resolved — using heuristic SAM method '.{samMethod}()'",
                node.GetLocation());
            return $"{delegateReceiver3}.{samMethod}({delegateArgs3})";
        }

        var target = facade.Transform(node.Expression, context);
        var fallbackMethodSym = context.GetSymbolInfo(node).Symbol as IMethodSymbol;
        var classTokenList = fallbackMethodSym != null ? GetClassTypeTokensForCall(fallbackMethodSym, context) : null;
        var args2 = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, methodSymbol: fallbackMethodSym);
        args2 = PrependClassTypeTokens(args2, classTokenList);
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
        if (context.GetSymbolInfo(node).Symbol is IMethodSymbol sym)
        {
            var typeName = sym.ContainingType.ToDisplayString();
            var mapped = context.TypeMappings.MapMethod(typeName, originalName);
            if (mapped != null)
                return ConversionContext.EscapeJavaKeyword(mapped);

            // Primitive type static method mapping (e.g. char.IsLower → Character.isLowerCase)
            // When the containing type is a C# primitive, use the specialized method name mapper
            // that knows about Java wrapper class differences (e.g. IsLower→isLowerCase, not isLower).
            if (sym.IsStatic && TryGetPrimitiveKeyword(sym.ContainingType, out var primKeyword))
            {
                var primMapped = MapPrimitiveStaticMethodName(primKeyword, originalName);
                if (primMapped != originalName)
                    return ConversionContext.EscapeJavaKeyword(primMapped);
            }

            // When a non-operator method's camelCase name would collide with an auto-generated
            // operator method in the same class (e.g. Multiply -> multiply collides with
            // operator *), keep the PascalCase name. The method declaration will be renamed
            // to PascalCase by AddMethodIfNotDuplicate collision resolution, so the call site
            // must use PascalCase too — otherwise it would incorrectly self-recurse into the
            // operator method.
            if (sym.MethodKind != MethodKind.UserDefinedOperator
                && originalName.Length > 0 && char.IsUpper(originalName[0]))
            {
                var camelName = char.ToLowerInvariant(originalName[0]) + originalName[1..];
                if (WouldCollideWithOperatorInType(sym.ContainingType, camelName, sym)
                    || WouldCollideWithPropertyAccessorInType(sym.ContainingType, camelName, sym, context))
                {
                    return ConversionContext.EscapeJavaKeyword(originalName);
                }
            }
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
        // Note: Enqueue/Dequeue methods are handled via TypeMappings (e.g. Queue<T>.Enqueue→add,
        // Queue<T>.Dequeue→remove). Custom heap types (GenericBinaryHeapPriorityQueue, EventQueue,
        // etc.) go through normal camelCase so that call sites and declarations stay consistent.

        // Count() with no args → size() (Collection) or count() (Iterable fallback)
        // Any() with no args → length>0 (array) or iterator().hasNext() (Iterable/Collection)
        if (node.ArgumentList.Arguments.Count == 0)
        {
            var cReceiver = facade.Transform(memberAccess.Expression, context);
            if (memberAccess.Name.Identifier.Text == "Count")
                return $"{cReceiver}.size()";
            if (memberAccess.Name.Identifier.Text == "Any")
            {
                var anyType = context.SemanticModel != null
                    ? context.GetTypeInfo(memberAccess.Expression).Type : null;
                if (anyType?.TypeKind == TypeKind.Array || anyType is IArrayTypeSymbol)
                    return $"({cReceiver}.length > 0)";
                return $"{cReceiver}.iterator().hasNext()";
            }
        }

        // Known delegate property names emitted as private static fields but invoked
        // as methods (semantic model can't resolve DelegateInvoke in project pipeline).
        // Format: Type.delegateName(args) → Type.getDelegateName().accept(args)
        if (memberAccess.Name.Identifier.Text is "ShowDebugCurvesEnumeration" or "ObstaclesToIgnore")
        {
            var dlgReceiver = facade.Transform(memberAccess.Expression, context);
            var dlgMemberName = memberAccess.Name.Identifier.Text;
            var dlgGetter = "get" + char.ToUpperInvariant(dlgMemberName[0]) + dlgMemberName[1..];
            var dlgArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
            return $"{dlgReceiver}.{dlgGetter}().accept({dlgArgs})";
        }

        // Nullable<T>.GetValueOrDefault() → null-coalescing with default value.
        // The LINQ rewriter generates ((items as ICollection<T>)?.Count).GetValueOrDefault()
        // which must become (expr != null ? expr : 0) in Java.
        if (memberAccess.Name.Identifier.Text == "GetValueOrDefault")
        {
            var gvdReceiver = facade.Transform(memberAccess.Expression, context);
            if (node.ArgumentList.Arguments.Count == 0)
            {
                // Determine default from method return type if available
                var methodSym = context.GetSymbolInfo(node).Symbol as IMethodSymbol;
                var defaultVal = (methodSym?.ReturnType.SpecialType) switch
                {
                    SpecialType.System_Int64 => "0L",
                    SpecialType.System_Double => "0.0",
                    SpecialType.System_Single => "0.0f",
                    SpecialType.System_Boolean => "false",
                    _ => "0"
                };
                return $"({gvdReceiver} != null ? {gvdReceiver} : {defaultVal})";
            }
            else if (node.ArgumentList.Arguments.Count == 1)
            {
                var defaultArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"({gvdReceiver} != null ? {gvdReceiver} : {defaultArg})";
            }
        }

        // Fix: Generic type static method call — C# DemoSet<T>.Method() → Java DemoSet.Method().
        // Java forbids type arguments on the class name at a static call site; strip them.
        var receiver = memberAccess.Expression is GenericNameSyntax genericReceiverName
            ? ConversionContext.EscapeJavaKeyword(genericReceiverName.Identifier.Text)
            : facade.Transform(memberAccess.Expression, context);
        var originalMethodName = memberAccess.Name.Identifier.Text;
        var earlyMethodSymbol = context.GetSymbolInfo(node).Symbol as IMethodSymbol;

        if (TryTransformDecimalStaticInvocation(
            node,
            memberAccess,
            originalMethodName,
            context,
            facade,
            out var decimalStaticInvocation))
        {
            return decimalStaticInvocation;
        }

        // Replace receiver with mapped static type reference ONLY when the method is
        // confirmed static.  When the semantic model cannot resolve the method
        // (earlyMethodSymbol is null, e.g. in project conversion with incomplete models),
        // DO NOT assume static — instance methods on fields/locals must keep the
        // original expression as receiver.
        if (earlyMethodSymbol is { IsStatic: true }
            && ExpressionTransformerHelpers.TryGetStaticTypeReceiverJavaReference(
            memberAccess.Expression,
            context,
            boxJavaPrimitiveType: true,
            out var staticReceiver,
            out _))
        {
            receiver = staticReceiver;
        }

        // C# Enum.HasFlag(flag) → Java bitwise check: (receiver & argument) != 0
        // [Flags] enums are mapped to int in Java, so bitwise operations are valid.
        if (originalMethodName == "HasFlag" && node.ArgumentList.Arguments.Count == 1
            && context.SemanticModel != null)
        {
            var receiverType = context.GetTypeInfo(memberAccess.Expression).Type;
            if (receiverType?.TypeKind == TypeKind.Enum
                && (receiverType is INamedTypeSymbol namedHasFlag
                    && (namedHasFlag.GetAttributes().Any(a =>
                            a.AttributeClass?.ToDisplayString() is "System.FlagsAttribute")
                        || context.IsFlagsEnum(namedHasFlag.Name)
                        || context.IsFlagsEnum(namedHasFlag.ToDisplayString()))))
            {
                var flagArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"(({receiver} & {flagArg}) != 0)";
            }
        }

        if (originalMethodName == "ReferenceEquals" && node.ArgumentList.Arguments.Count == 2)
        {
            var leftArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var rightArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            return $"({leftArg} == {rightArg})";
        }

        if (originalMethodName == "GetEnumerator" && IsDictionaryLikeExpression(memberAccess.Expression, context))
        {
            context.AddImport("io.github.ningpp.compat.CSharpEnumerator");
            return $"CSharpEnumerator.from({receiver}.entrySet().iterator())";
        }

        if (originalMethodName == "GetEnumerator" && IsExplicitEnumeratorGetEnumeratorInvocation(node, context))
        {
            context.AddImport("io.github.ningpp.compat.CSharpEnumerator");
            return $"CSharpEnumerator.from({BuildIteratorExpressionForExplicitGetEnumerator(receiver, memberAccess.Expression, context)})";
        }

        // C# Type.GetMethod(name, ...) → Java Class.getDeclaredMethod / getMethod.
        // The C# method name string uses PascalCase; generated Java methods use camelCase.
        // Convert the string literal so the reflection lookup matches the actual Java name.
        //
        // C# Type.GetMethod finds by name alone when no Type[] is given; Java
        // getDeclaredMethod / getMethod require exact parameter types.  When the
        // caller does NOT supply explicit parameter types, emit a ReflectionHelper
        // helper that searches by name instead.
        //
        // BindingFlags → getDeclaredMethod (finds non-public members).
        // Without BindingFlags → getMethod (public members only).
        if (originalMethodName == "GetMethod"
            && earlyMethodSymbol?.ContainingType.ToDisplayString() == "System.Type"
            && node.ArgumentList.Arguments.Count >= 1)
        {
            var methodNameArg = TransformReflectionMethodNameArg(
                node.ArgumentList.Arguments[0].Expression, facade, context);
            bool hasBindingFlags = node.ArgumentList.Arguments.Count >= 2
                && earlyMethodSymbol.Parameters.Length >= 2
                && earlyMethodSymbol.Parameters[1].Type.ToDisplayString() == "System.Reflection.BindingFlags";

            // Determine whether the caller supplied explicit parameter types (Type[]).
            // Overloads:
            //   GetMethod(string)              — no types, no flags
            //   GetMethod(string, BindingFlags) — no types, flags
            //   GetMethod(string, Type[])       — types, no flags
            //   GetMethod(string, BindingFlags, Binder, Type[], ParameterModifier[]) — types + flags
            bool hasExplicitTypes = false;
            int typesArgIndex = -1;
            for (int i = 1; i < earlyMethodSymbol.Parameters.Length; i++)
            {
                if (earlyMethodSymbol.Parameters[i].Type.ToDisplayString() == "System.Type[]")
                {
                    hasExplicitTypes = true;
                    typesArgIndex = i;
                    break;
                }
            }

            if (hasExplicitTypes)
            {
                // Pass parameter types directly — Java can resolve the exact overload.
                var typesExpr = facade.Transform(
                    node.ArgumentList.Arguments[typesArgIndex].Expression, context);
                var javaMethod = hasBindingFlags ? "getDeclaredMethod" : "getMethod";
                return $"{receiver}.{javaMethod}({methodNameArg}, {typesExpr})";
            }
            else
            {
                // No parameter types — use ReflectionHelper to search by name.
                context.AddImport("io.github.ningpp.compat.ReflectionHelper");
                var helperMethod = hasBindingFlags ? "getDeclaredMethodByName" : "getMethodByName";
                return $"ReflectionHelper.{helperMethod}({receiver}, {methodNameArg})";
            }
        }

        if (originalMethodName == "Exit"
            && node.ArgumentList.Arguments.Count >= 1
            && (earlyMethodSymbol?.ContainingType.ToDisplayString() == "System.Environment"
                || ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression,
                    context,
                    "Environment",
                    "System.Environment")))
        {
            var exitCode = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            return $"System.exit({exitCode})";
        }

        // Debug.Fail / Trace.Fail → Debug.fail(message)
        // Use compat Debug class to preserve fail semantics without throwing RuntimeException.
        if (originalMethodName == "Fail"
            && (earlyMethodSymbol?.ContainingType.ToDisplayString() is "System.Diagnostics.Debug" or "System.Diagnostics.Trace"
                || ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression,
                    context,
                    "Debug",
                    "Trace",
                    "System.Diagnostics.Debug",
                    "System.Diagnostics.Trace")))
        {
            context.AddImport("io.github.ningpp.compat.Debug");
            var failArgs = string.Join(", ", node.ArgumentList.Arguments.Select(a => facade.Transform(a.Expression, context)));
            return string.IsNullOrEmpty(failArgs)
                ? "Debug.fail(\"\")"
                : $"Debug.fail({failArgs})";
        }

        // Debug.Assert / Trace.Assert / Contract.Assert
        // Debug.Assert is compiled out in C# Release builds ([Conditional("DEBUG")]).
        // Trace.Assert remains in Release. Contract.Assert requires CONTRACTS_FULL symbol.
        // Java assert is runtime-gated (-ea), which is not equivalent to compile-time removal.
        // → strip Debug.Assert / Contract.Assert as comments; keep Trace.Assert as `assert`.
        if (originalMethodName == "Assert"
            && (earlyMethodSymbol?.ContainingType.ToDisplayString() is "System.Diagnostics.Debug" or "System.Diagnostics.Trace"
                    or "System.Diagnostics.Contracts.Contract"
                || ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression,
                    context,
                    "Debug",
                    "Trace",
                    "Contract",
                    "System.Diagnostics.Debug",
                    "System.Diagnostics.Trace",
                    "System.Diagnostics.Contracts.Contract"))
            && node.ArgumentList.Arguments.Count >= 1)
        {
            bool isTrace = earlyMethodSymbol?.ContainingType.ToDisplayString() == "System.Diagnostics.Trace"
                || ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression, context, "Trace", "System.Diagnostics.Trace");
            var condition = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            string message = node.ArgumentList.Arguments.Count >= 2
                ? facade.Transform(node.ArgumentList.Arguments[1].Expression, context)
                : "";
            if (isTrace)
            {
                return message.Length > 0
                    ? $"assert {condition} : {message}"
                    : $"assert {condition}";
            }
            // Debug.Assert / Contract.Assert → comment (matching C# Release semantics)
            return message.Length > 0
                ? $"// Debug.Assert({condition}, {message});\n"
                : $"// Debug.Assert({condition});\n";
        }

        if (originalMethodName == "GetTempPath"
            && (earlyMethodSymbol?.ContainingType.ToDisplayString() == "System.IO.Path"
                || ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression,
                    context,
                    "Path",
                    "Paths",
                    "System.IO.Path")))
        {
            return "System.getProperty(\"java.io.tmpdir\")";
        }

        if (originalMethodName == "Combine"
            && node.ArgumentList.Arguments.Count >= 2
            && (earlyMethodSymbol?.ContainingType.ToDisplayString() == "System.IO.Path"
                || ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression,
                    context,
                    "Path",
                    "Paths",
                    "System.IO.Path")))
        {
            var combineParts = node.ArgumentList.Arguments
                .Select(arg => facade.Transform(arg.Expression, context))
                .ToList();
            return $"java.nio.file.Paths.get({string.Join(", ", combineParts)}).toString()";
        }

        // System.Type.GetField(string) → ReflectionHelper.getField(class, name)
        // C# Type.GetField returns null when the field doesn't exist;
        // Java Class.getField throws NoSuchFieldException instead.
        if (originalMethodName == "GetField"
            && node.ArgumentList.Arguments.Count == 1
            && earlyMethodSymbol?.ContainingType.ToDisplayString() == "System.Type")
        {
            var fieldName = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            context.AddImport("io.github.ningpp.compat.ReflectionHelper");
            return $"ReflectionHelper.getField({receiver}, {fieldName})";
        }

        // MethodInfo.CreateDelegate(Type) → ReflectionHelper.createDelegate(method, DelegateType.class)
        // MethodInfo.CreateDelegate(Type, object) → ReflectionHelper.createDelegate(method, target, DelegateType.class)
        // MethodInfo.CreateDelegate<T>() → ReflectionHelper.createDelegate(method, T.class)
        // MethodInfo.CreateDelegate<T>(object) → ReflectionHelper.createDelegate(method, target, T.class)
        // C# MethodInfo.CreateDelegate creates a typed delegate from a reflection method;
        // Java has no direct equivalent. Bridge via ReflectionHelper using Proxy.
        if (originalMethodName == "CreateDelegate"
            && (earlyMethodSymbol?.ContainingType.ToDisplayString() == "System.Reflection.MethodInfo"
                || IsMethodInfoReceiver(memberAccess.Expression, context)))
        {
            context.AddImport("io.github.ningpp.compat.ReflectionHelper");

            if (memberAccess.Name is GenericNameSyntax genericCreateDelegate
                && genericCreateDelegate.TypeArgumentList.Arguments.Count > 0)
            {
                var delegateTypeArg = ResolveDelegateTypeArg(
                    genericCreateDelegate.TypeArgumentList.Arguments[0], context);
                if (node.ArgumentList.Arguments.Count == 0)
                {
                    return $"ReflectionHelper.createDelegate({receiver}, {delegateTypeArg}.class)";
                }
                if (node.ArgumentList.Arguments.Count == 1)
                {
                    var targetArg = facade.Transform(
                        node.ArgumentList.Arguments[0].Expression, context);
                    return $"ReflectionHelper.createDelegate({receiver}, {targetArg}, {delegateTypeArg}.class)";
                }
            }
            else
            {
                if (node.ArgumentList.Arguments.Count == 1)
                {
                    var delegateTypeArg = facade.Transform(
                        node.ArgumentList.Arguments[0].Expression, context);
                    return $"ReflectionHelper.createDelegate({receiver}, {delegateTypeArg})";
                }
                if (node.ArgumentList.Arguments.Count == 2)
                {
                    var delegateTypeArg = facade.Transform(
                        node.ArgumentList.Arguments[0].Expression, context);
                    var targetArg = facade.Transform(
                        node.ArgumentList.Arguments[1].Expression, context);
                    return $"ReflectionHelper.createDelegate({receiver}, {targetArg}, {delegateTypeArg})";
                }
            }
        }

        if (originalMethodName == "MoveNext" && node.ArgumentList.Arguments.Count == 0
            && IsEnumeratorMoveNextInvocation(node, context))
        {
            return $"{receiver}.moveNext()";
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
            var resetReceiverType = context.GetTypeInfo(memberAccess.Expression).Type as INamedTypeSymbol;
            var isIteratorLike = resetReceiverType != null
                && (resetReceiverType.Name is "IEnumerator" or "Iterator"
                    || resetReceiverType.AllInterfaces.Any(i => i.Name is "IEnumerator" or "Iterator"));
            if (isIteratorLike)
                return "/* reset unsupported for Java Iterator */";
        }

        if (originalMethodName == "Equals" && node.ArgumentList.Arguments.Count == 2
            && IsStaticNullSafeEqualsMethod(context.GetSymbolInfo(node).Symbol as IMethodSymbol))
        {
            var leftArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var rightArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            context.AddImport("java.util.Objects");
            return $"Objects.equals({leftArg}, {rightArg})";
        }

        if (IsSystemStringMethod(earlyMethodSymbol, memberAccess.Expression, context))
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

            if (originalMethodName == "CompareOrdinal"
                && (node.ArgumentList.Arguments.Count == 2 || node.ArgumentList.Arguments.Count == 5))
            {
                var compareOrdinalArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
                return $"StringHelper.compareOrdinal({compareOrdinalArgs})";
            }
        }

        var stringEqualsReceiverType = context.GetTypeInfo(memberAccess.Expression).Type;
        if (originalMethodName == "Equals"
            && node.ArgumentList.Arguments.Count == 1
            && IsSystemStringType(stringEqualsReceiverType))
        {
            var arg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            context.AddImport("java.util.Objects");
            return $"Objects.equals({receiver}, {arg})";
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

        // Fallback: when semantic model can't resolve receiver type but a StringComparison
        // argument is present, handle common string instance methods to prevent
        // StringComparison.XXX from leaking into generated Java code.
        if (stringEqualsReceiverType == null
            && originalMethodName is "StartsWith" or "EndsWith" or "Equals" or "Contains" or "IndexOf" or "LastIndexOf")
        {
            var lastArgIdx = node.ArgumentList.Arguments.Count - 1;
            if (lastArgIdx >= 1
                && TryGetStringComparisonIgnoreCase(node.ArgumentList.Arguments[lastArgIdx].Expression, context.SemanticModel, out var fallbackIgnoreCase))
            {
                if (originalMethodName is "StartsWith" or "EndsWith" && node.ArgumentList.Arguments.Count == 2)
                {
                    var arg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    var helperMethod = originalMethodName == "StartsWith" ? "startsWith" : "endsWith";
                    return $"StringHelper.{helperMethod}({receiver}, {arg}, {ToJavaBooleanLiteral(fallbackIgnoreCase)})";
                }
                if (originalMethodName == "Equals" && node.ArgumentList.Arguments.Count == 2)
                {
                    var arg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    return $"StringHelper.equals({receiver}, {arg}, {ToJavaBooleanLiteral(fallbackIgnoreCase)})";
                }
                if (originalMethodName == "Contains" && node.ArgumentList.Arguments.Count == 2)
                {
                    var arg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    return $"StringHelper.contains({receiver}, {arg}, {ToJavaBooleanLiteral(fallbackIgnoreCase)})";
                }
                if (originalMethodName == "IndexOf" && node.ArgumentList.Arguments.Count == 2)
                {
                    var arg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    return $"StringHelper.indexOf({receiver}, {arg}, {ToJavaBooleanLiteral(fallbackIgnoreCase)})";
                }
                if (originalMethodName == "IndexOf" && node.ArgumentList.Arguments.Count == 3)
                {
                    var arg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    var startArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                    return $"StringHelper.indexOf({receiver}, {arg}, {startArg}, {ToJavaBooleanLiteral(fallbackIgnoreCase)})";
                }
                if (originalMethodName == "LastIndexOf" && node.ArgumentList.Arguments.Count == 2)
                {
                    var arg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    return $"StringHelper.lastIndexOf({receiver}, {arg}, {ToJavaBooleanLiteral(fallbackIgnoreCase)})";
                }
                if (originalMethodName == "LastIndexOf" && node.ArgumentList.Arguments.Count == 3)
                {
                    var arg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    var startArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                    return $"StringHelper.lastIndexOf({receiver}, {arg}, {startArg}, {ToJavaBooleanLiteral(fallbackIgnoreCase)})";
                }
            }
        }

        // System.Tuple.Create(...) mapping.
        // Avoid emitting Tuple.create(...) which may bind to an unrelated user type named Tuple.
        if (originalMethodName == "Create" && node.ArgumentList.Arguments.Count >= 2)
        {
            bool isSystemTupleCreate = earlyMethodSymbol?.ContainingType?.ToDisplayString() == "System.Tuple"
                || ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression,
                    context,
                    "Tuple",
                    "System.Tuple");
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
            var delegateSymInfo = context.GetSymbolInfo(node);
            if (delegateSymInfo.Symbol is IMethodSymbol { MethodKind: MethodKind.DelegateInvoke } delegateInvoke)
            {
                // Check if this is actually an event invocation (e.g., this.ProgressChanged(sender, args))
                // Events are a special case - they should use the fire method instead of delegate invocation
                var memberSymbol = context.GetSymbolInfo(memberAccess).Symbol;
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
                var javaMethod = context.TypeMappings.MapMethod(containingTypeName, "Invoke")
                    ?? InferSamMethodName(delegateInvoke);
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
                // Rewrite C# {N} format placeholders to Java % specifiers
                if (node.ArgumentList.Arguments.Count > fmtStart
                    && node.ArgumentList.Arguments[fmtStart].Expression is LiteralExpressionSyntax
                        { RawKind: (int)SyntaxKind.StringLiteralExpression } strLit)
                {
                    var rewrittenFormat = RewriteStringFormatLiteral(strLit.Token.ValueText);
                    var remainingArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, fmtStart + 1);
                    return string.IsNullOrEmpty(remainingArgs)
                        ? $"String.format({rewrittenFormat})"
                        : $"String.format({rewrittenFormat}, {remainingArgs})";
                }
                var formatArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, fmtStart);
                return $"String.format({formatArgs})";
            }

            // string.IsNullOrEmpty(s) → StringHelper.isNullOrEmpty(s)
            if (primTypeSyntax.Keyword.Text == "string" && originalMethodName == "IsNullOrEmpty"
                && node.ArgumentList.Arguments.Count >= 1)
            {
                var valueExpr = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"StringHelper.isNullOrEmpty({valueExpr})";
            }

            // string.IsNullOrWhiteSpace(s) → StringHelper.isNullOrWhiteSpace(s)
            if (primTypeSyntax.Keyword.Text == "string" && originalMethodName == "IsNullOrWhiteSpace"
                && node.ArgumentList.Arguments.Count >= 1)
            {
                var valueExpr = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"StringHelper.isNullOrWhiteSpace({valueExpr})";
            }

            // string.Concat(...) → StringHelper.concat(...)
            if (primTypeSyntax.Keyword.Text == "string" && originalMethodName == "Concat"
                && node.ArgumentList.Arguments.Count >= 1)
            {
                var concatArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
                return $"StringHelper.concat({concatArgs})";
            }

            // string.CompareOrdinal(...) → StringHelper.compareOrdinal(...)
            if (primTypeSyntax.Keyword.Text == "string" && originalMethodName == "CompareOrdinal"
                && (node.ArgumentList.Arguments.Count == 2 || node.ArgumentList.Arguments.Count == 5))
            {
                var compareOrdinalArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
                return $"StringHelper.compareOrdinal({compareOrdinalArgs})";
            }

            // string.Compare(...) → StringHelper.compare(...)
            if (primTypeSyntax.Keyword.Text == "string" && originalMethodName == "Compare")
            {
                var compareArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
                return $"StringHelper.compare({compareArgs})";
            }

            // string.Copy(s) → StringHelper.copy(s)
            if (primTypeSyntax.Keyword.Text == "string" && originalMethodName == "Copy"
                && node.ArgumentList.Arguments.Count == 1)
            {
                var copyArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"StringHelper.copy({copyArg})";
            }

            // string.Intern(s) → StringHelper.intern(s)
            if (primTypeSyntax.Keyword.Text == "string" && originalMethodName == "Intern"
                && node.ArgumentList.Arguments.Count == 1)
            {
                var internArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"StringHelper.intern({internArg})";
            }

            // string.IsInterned(s) → StringHelper.isInterned(s)
            if (primTypeSyntax.Keyword.Text == "string" && originalMethodName == "IsInterned"
                && node.ArgumentList.Arguments.Count == 1)
            {
                var isInternedArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"StringHelper.isInterned({isInternedArg})";
            }

            // Numeric TryParse: double.TryParse(s, out result) → MathHelper.tryParseDouble(s, holder)
            if (originalMethodName == "TryParse" && node.ArgumentList.Arguments.Count >= 2)
            {
                string? tryParseHelper = primTypeSyntax.Keyword.Text switch
                {
                    "double" => "MathHelper.tryParseDouble",
                    "float"  => "MathHelper.tryParseFloat",
                    "int"    => "MathHelper.tryParseInt",
                    "long"   => "MathHelper.tryParseLong",
                    "bool"   => "MathHelper.tryParseBool",
                    _        => null
                };
                if (tryParseHelper != null)
                {
                    var helperArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
                    return $"{tryParseHelper}({helperArgs})";
                }
            }

            if (originalMethodName == "Parse")
            {
                var primitiveParseHelper = MapPrimitiveParseHelper(primTypeSyntax.Keyword.Text);
                if (primitiveParseHelper != null)
                {
                    var parseArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
                    return $"{primitiveParseHelper}({parseArgs})";
                }
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
            var symbolInfo = context.GetSymbolInfo(node);
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
            // For user-defined extension methods (not System.Linq.Enumerable/Queryable), activate
            // static lowering: HostClass.method(receiver, args...) instead of receiver.method(args...).
            if (methodSymbol is { IsExtensionMethod: true, MethodKind: MethodKind.ReducedExtension })
            {
                var containingTypeDisplay = methodSymbol.ContainingType.ToDisplayString();
                bool isLinqExtension = containingTypeDisplay is "System.Linq.Enumerable" or "System.Linq.Queryable";
                // Only activate static lowering for user-defined extension methods.
                // LINQ extension methods are handled by the dedicated Stream API block below.
                isExtensionInStaticPath = !isLinqExtension;
            }
        }

        // Inline generic methods with new() constraint: resolve concrete type bindings
        // at the call site and generate inlined code with correct concrete constructor.
        if (methodSymbol != null
            && ObjectCreationTransformer.HasNewConstraintObjectCreation(methodSymbol.OriginalDefinition))
        {
            var inlined = TryInlineNewConstraintMethodCall(
                methodSymbol, node, memberAccess, context);
            if (inlined != null)
                return inlined;
        }

        if (originalMethodName == "ToArray"
            && node.ArgumentList.Arguments.Count == 0
            && TryTransformArrayToArrayCopy(
                receiver,
                methodSymbol,
                node,
                memberAccess,
                context,
                out var arrayToArrayCopy))
        {
            return arrayToArrayCopy;
        }

        if (originalMethodName == "ToArray"
            && node.ArgumentList.Arguments.Count == 1
            && TryTransformArrayListToArrayType(
                receiver,
                methodSymbol,
                node,
                memberAccess,
                context,
                out var arrayListToArrayType))
        {
            return arrayListToArrayType;
        }

        // MSTest Assert.* -> MSTest compatibility Assert.*.
        // JUnit Assertions has different overloads and exception behavior, while
        // MSTest callers may pass formatted messages and catch UnitTestAssertException.
        if (TryMapMSTestAssertInvocation(memberAccess.Expression, context, originalMethodName, methodSymbol, out var mstestAssertName))
        {
            var assertArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
            context.AddImport("Microsoft.VisualStudio.TestTools.UnitTesting.Assert");
            return $"Assert.{mstestAssertName}({assertArgs})";
        }

        // C# String.Format(...) -> Java String.format(...)
        if (originalMethodName == "Format"
            && IsSystemStringMethod(methodSymbol, memberAccess.Expression, context))
        {
            int fmtStart = HasIFormatProviderFirstArg(node, context) ? 1 : 0;
            // Rewrite C# {N} format placeholders to Java % specifiers.
            // Only for the real System.String — custom types named "String"
            // (e.g. Microsoft.Msagl.Text.String) have their own format behavior.
            bool isRealSystemString = methodSymbol?.ContainingType?.SpecialType == SpecialType.System_String
                || memberAccess.Expression is PredefinedTypeSyntax;
            if (isRealSystemString
                && node.ArgumentList.Arguments.Count > fmtStart
                && node.ArgumentList.Arguments[fmtStart].Expression is LiteralExpressionSyntax
                    { RawKind: (int)SyntaxKind.StringLiteralExpression } strLit)
            {
                var rewrittenFormat = RewriteStringFormatLiteral(strLit.Token.ValueText);
                var remainingArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, fmtStart + 1);
                return string.IsNullOrEmpty(remainingArgs)
                    ? $"String.format({rewrittenFormat})"
                    : $"String.format({rewrittenFormat}, {remainingArgs})";
            }
            var fmtArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, fmtStart, methodSymbol);
            return $"String.format({fmtArgs})";
        }

        // Instance collection ToArray() should produce a typed array, not Object[].
        if (originalMethodName == "ToArray"
            && node.ArgumentList.Arguments.Count == 0
            && methodSymbol is { IsExtensionMethod: false }
            && methodSymbol.ReturnType is IArrayTypeSymbol instanceArrayType)
        {
            if (!IsFrameworkCollectionToArray(methodSymbol))
                return $"{receiver}.toArray()";
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
            var receiverType = context.GetTypeInfo(memberAccess.Expression).Type;
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
            // Check if receiver is an array - simplest correct check is .length > 0
            // Wrap in parens so a parent `!` produces `!(arr.length > 0)` instead of `!arr.length > 0`
            var receiverType = context.GetTypeInfo(memberAccess.Expression).Type;
            if (receiverType is IArrayTypeSymbol)
            {
                return $"({receiver}.length > 0)";
            }
            return $"{receiver}.iterator().hasNext()";
        }

        // Array.Sort(array[, comparer]) -> Arrays.sort(array[, comparer])
        // System.Array may map syntactically to Object, so keep a fallback on the receiver text.
        if (originalMethodName == "Sort"
            && node.ArgumentList.Arguments.Count is 1 or 2
            && (methodSymbol?.ContainingType.ToDisplayString() == "System.Array"
                || (methodSymbol == null && ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression,
                    context,
                    "Array",
                    "System.Array",
                    "Object"))))
        {
            var arrayArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            context.AddImport("java.util.Arrays");
            if (node.ArgumentList.Arguments.Count == 1)
            {
                return $"Arrays.sort({arrayArg})";
            }

            var sortArrayType = context.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type as IArrayTypeSymbol;
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

        // Fix: Array.ForEach(array, action) → stream(array).forEach(action)
        // System.Array maps to "Object" in TypeMappings which has no static forEach method.
        // Use centralized helper to produce correct stream for all array types.
        // Check both via semantic model and syntactic fallback (missing assembly reference).
        if (originalMethodName == "ForEach"
            && node.ArgumentList.Arguments.Count >= 2
            && (methodSymbol?.ContainingType.ToDisplayString() == "System.Array"
                || (methodSymbol == null && ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression,
                    context,
                    "Array",
                    "System.Array"))))
        {
            var arrayArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var actionArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            var foreachArrayType = context.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type as IArrayTypeSymbol;
            string streamExpr;
            if (foreachArrayType != null)
                streamExpr = ExpressionTransformerHelpers.BuildArrayStreamExpression(arrayArg, foreachArrayType, context, boxed: true);
            else
            {
                // Fallback when no type info: use Arrays.stream (reference types are expected)
                context.AddImport("java.util.Arrays");
                streamExpr = $"Arrays.stream({arrayArg})";
            }
            return $"{streamExpr}.forEach({actionArg})";
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
                || (methodSymbol == null && ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression,
                    context,
                    "Array",
                    "System.Array"))))
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
                || (methodSymbol == null && ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression,
                    context,
                    "Array",
                    "System.Array",
                    "Object"))))
        {
            var arrayArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var indexArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            var lengthArg = facade.Transform(node.ArgumentList.Arguments[2].Expression, context);
            var endArg = $"({indexArg} + {lengthArg})";

            string defaultValue = "null";
            if (context.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type is IArrayTypeSymbol clearArrayType)
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

        // List<T>.Exists(predicate) → list.stream().anyMatch(predicate)
        // List<T>.TrueForAll(predicate) → list.stream().allMatch(predicate)
        // Java ArrayList has no direct Exists/TrueForAll methods.
        if (originalMethodName is "Exists" or "TrueForAll"
            && node.ArgumentList.Arguments.Count == 1
            && methodSymbol?.ContainingType.Name == "List"
            && methodSymbol.ContainingType.ContainingNamespace?.ToString()?.StartsWith("System") == true)
        {
            var pred = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var matchMethod = originalMethodName == "Exists" ? "anyMatch" : "allMatch";
            return $"{receiver}.stream().{matchMethod}({pred})";
        }

        // System.Threading.Tasks.Parallel.ForEach(source, [options,] action)
        // → StreamSupport.stream(source.spliterator(), true).forEach(action)
        // This preserves compilability in Java while keeping parallel intent.
        if (originalMethodName == "ForEach"
            && node.ArgumentList.Arguments.Count >= 2
            && (methodSymbol?.ContainingType.ToDisplayString() == "System.Threading.Tasks.Parallel"
                || ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression,
                    context,
                    "Parallel",
                    "System.Threading.Tasks.Parallel")))
        {
            var sourceExpr = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var actionArgExpr = node.ArgumentList.Arguments[^1].Expression;
            string actionExpr;
            var isMethodGroupArg = actionArgExpr is IdentifierNameSyntax or MemberAccessExpressionSyntax;

            if (isMethodGroupArg
                && context.GetSymbolInfo(actionArgExpr).Symbol is IMethodSymbol actionMethod)
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
            // Arrays don't have .spliterator() instance method in Java.
            // Use Arrays.spliterator(arr) for arrays, source.spliterator() for collections.
            var sourceArgType = context.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type;
            if (sourceArgType is IArrayTypeSymbol)
            {
                context.AddImport("java.util.Arrays");
                return $"StreamSupport.stream(Arrays.spliterator({sourceExpr}), true).forEach({actionExpr})";
            }
            return $"StreamSupport.stream({sourceExpr}.spliterator(), true).forEach({actionExpr})";
        }

        // System.Array.CreateInstance(type, length) → java.lang.reflect.Array.newInstance(type, length)
        if (originalMethodName == "CreateInstance"
            && node.ArgumentList.Arguments.Count == 2
            && (methodSymbol?.ContainingType.ToDisplayString() == "System.Array"
                || (methodSymbol == null && ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression,
                    context,
                    "Array",
                    "System.Array",
                    "Object"))))
        {
            var typeArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var lengthArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            return $"java.lang.reflect.Array.newInstance({typeArg}, {lengthArg})";
        }

        // System.Activator.CreateInstance(type) → type.getDeclaredConstructor().newInstance()
        if (originalMethodName == "CreateInstance"
            && node.ArgumentList.Arguments.Count == 1
            && (methodSymbol?.ContainingType.ToDisplayString() == "System.Activator"
                || (methodSymbol == null && ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression, context, "Activator", "System.Activator"))))
        {
            var typeArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            return $"{typeArg}.getDeclaredConstructor().newInstance()";
        }

        // System.Type.GetType(string) → Class.forName(string)
        if (originalMethodName == "GetType"
            && node.ArgumentList.Arguments.Count == 1
            && (methodSymbol?.ContainingType.ToDisplayString() == "System.Type"
                || (methodSymbol == null && ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression, context, "Type", "System.Type")))
            && (methodSymbol?.IsStatic ?? true))
        {
            var typeNameArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            return $"Class.forName({typeNameArg})";
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
            var copySourceType = context.GetTypeInfo(memberAccess.Expression).Type;
            if (copySourceType is IArrayTypeSymbol copyArr)
            {
                return $"System.arraycopy({receiver}, 0, {destArrayArg}, {destIndexArg}, {receiver}.length)";
            }
            return $"System.arraycopy({receiver}.toArray(), 0, {destArrayArg}, {destIndexArg}, {receiver}.size())";
        }

        // Dictionary.TryGetValue(key, out value) -> containsKey check + out assignment.
        // C# assigns the out variable on both success and failure.  Java definite
        // assignment follows short-circuit branches, so the expression must assign
        // in both arms while still checking containsKey before any primitive unboxing.
        if (originalMethodName == "TryGetValue"
            && node.ArgumentList.Arguments.Count == 2)
        {
            var keyArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var outArg = node.ArgumentList.Arguments[1];
            var outHolderArg = facade.Transform(outArg.Expression, context);
            // Detect whether the out argument is a holder variable (needs .value access).
            bool isHolder = false;
            if (IsSimpleIdentifier(outHolderArg))
            {
                isHolder = outHolderArg.StartsWith("_", StringComparison.Ordinal)
                    && outHolderArg.EndsWith("Holder", StringComparison.Ordinal);
                if (!isHolder && context.SemanticModel != null)
                {
                    var outArgSymbol = context.GetSymbolInfo(outArg.Expression).Symbol;
                    if (outArgSymbol is IParameterSymbol { RefKind: RefKind.Out or RefKind.Ref })
                        isHolder = true;
                }
                if (!isHolder)
                    isHolder = context.TryGetActiveRefHolder(outHolderArg, out _);
            }
            var assignTarget = isHolder ? $"{outHolderArg}.value" : outHolderArg;
            var defaultValue = GetTryGetValueOutDefault(outArg.Expression, context);

            // Each branch has an assignment as the first evaluated operation, then
            // collapses to the required boolean.  Objects.equals is used only as a
            // sequencing vehicle so the assignment target is evaluated once per arm.
            var successAssign = $"(java.util.Objects.equals(({assignTarget} = {receiver}.get({keyArg})), null) || true)";
            var missAssign = $"(java.util.Objects.equals(({assignTarget} = {defaultValue}), null) && false)";
            return $"({receiver}.containsKey({keyArg}) ? {successAssign} : {missAssign})";
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
            var receiverType = context.GetTypeInfo(memberAccess.Expression).Type;
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

        // Fix: Stopwatch.GetTimestamp() → System.nanoTime()
        // C# Stopwatch.GetTimestamp() returns a high-resolution timestamp in ticks.
        // Java System.nanoTime() is the closest equivalent (nanosecond precision).
        if (originalMethodName == "GetTimestamp"
            && node.ArgumentList.Arguments.Count == 0
            && (methodSymbol?.ContainingType.ToDisplayString() == "System.Diagnostics.Stopwatch"
                || (methodSymbol == null && ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression,
                    context,
                    "Stopwatch",
                    "System.Diagnostics.Stopwatch"))))
        {
            return "System.nanoTime()";
        }

        // Fix: Math.Sign(value) → Integer.signum(value) for int args, (int)Math.signum(value) otherwise.
        // C# Math.Sign always returns int regardless of input type.
        // Java Math.signum(double) returns double, Math.signum(float) returns float — NOT int.
        // Java Integer.signum(int) returns int and is the correct mapping for int arguments.
        if (originalMethodName == "Sign"
            && node.ArgumentList.Arguments.Count == 1
            && (methodSymbol?.ContainingType.ToDisplayString() is "System.Math" or "System.MathF"
                || (methodSymbol == null && ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression,
                    context,
                    "Math",
                    "MathF",
                    "System.Math",
                    "System.MathF"))))
        {
            var signArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var argType = context.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type;
            if (argType?.SpecialType == Microsoft.CodeAnalysis.SpecialType.System_Int32
                || argType?.SpecialType == Microsoft.CodeAnalysis.SpecialType.System_Int64
                || argType?.SpecialType == Microsoft.CodeAnalysis.SpecialType.System_Int16
                || argType?.SpecialType == Microsoft.CodeAnalysis.SpecialType.System_SByte)
            {
                // Integer.signum(int) returns int — direct match
                return argType.SpecialType == Microsoft.CodeAnalysis.SpecialType.System_Int64
                    ? $"Long.signum({signArg})"
                    : $"Integer.signum({signArg})";
            }
            // For float/double, cast the result to int to match C# signature
            return $"(int)Math.signum({signArg})";
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
                || (methodSymbol == null && ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression,
                    context,
                    "Math",
                    "System.Math"))))
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
                || (methodSymbol == null && ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression,
                    context,
                    "Math",
                    "System.Math"))))
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
            var paramCount = methodSymbol.Parameters.Length;
            var mapped = context.TypeMappings.MapMethod(receiverTypeName, originalMethodName, paramCount);
            if (mapped == null)
            {
                var fqn = $"{methodSymbol.ContainingType.ContainingNamespace}.{methodSymbol.ContainingType.Name}";
                mapped = context.TypeMappings.MapMethod(fqn, originalMethodName, paramCount);
            }
            if (mapped != null)
            {
                ExpressionTransformerHelpers.AddImportForMappedHelperMethod(mapped, context);
                methodName = mapped;
            }
        }

        // AddRange(IEnumerable<T>) → Java addAll(Collection<T>): when the argument
        // is NOT a Collection<T>-compatible type (concrete class or interface), Java's
        // addAll() won't accept it.  Use arg.forEach(receiver::add) instead.
        // Keep addAll for: arrays, concrete Collection/List types, ICollection/IList interfaces.
        // Use forEach for: IEnumerable-only interfaces (produce Stream in Java),
        // concrete classes implementing only Iterable (not Collection).
        if (originalMethodName == "AddRange" && methodName == "addAll"
            && node.ArgumentList.Arguments.Count == 1
            && context.SemanticModel != null)
        {
            var argExpr = node.ArgumentList.Arguments[0].Expression;
            var argType = context.GetTypeInfo(argExpr).Type;
            if (argType != null
                && argType is not IArrayTypeSymbol
                && !IsCollectionCompatibleType(argType))
            {
                var arg = facade.Transform(argExpr, context);
                return $"{arg}.forEach({receiver}::add)";
            }
        }

        // Fix: Static type receiver remapping — e.g. System.Console → System.
        // When the receiver expression resolves to a named type symbol (static call site),
        // replace the syntactically-derived receiver string with the TypeMappings Java name
        // so that System.Console.WriteLine(x) → System.out.println(x).
        // Guard: skip when the receiver is a GenericNameSyntax — it was already correctly
        // stripped of its type arguments by the fix above (e.g. DemoSet<string> → DemoSet),
        // and MapType on the containing type would re-introduce them (DemoSet<T>).
        // Guard: only for static methods — instance methods must keep the original receiver.
        INamedTypeSymbol? staticReceiverTypeSymbol = null;
        if (methodSymbol is { IsStatic: true }
            && ExpressionTransformerHelpers.TryGetStaticTypeReceiverJavaReference(
                memberAccess.Expression,
                context,
                boxJavaPrimitiveType: true,
                out var semanticStaticReceiver,
                out var resolvedStaticReceiverType))
        {
            staticReceiverTypeSymbol = resolvedStaticReceiverType;
            receiver = semanticStaticReceiver;

            if (methodName == originalMethodName)
            {
                var staticReceiverTypeName = resolvedStaticReceiverType.ToDisplayString();
                var mappedStaticMethod = context.TypeMappings.MapMethod(
                    staticReceiverTypeName,
                    originalMethodName,
                    methodSymbol.Parameters.Length);
                if (mappedStaticMethod == null)
                {
                    var fqn = $"{resolvedStaticReceiverType.ContainingNamespace}.{resolvedStaticReceiverType.Name}";
                    mappedStaticMethod = context.TypeMappings.MapMethod(
                        fqn,
                        originalMethodName,
                        methodSymbol.Parameters.Length);
                }
                if (mappedStaticMethod != null)
                {
                    ExpressionTransformerHelpers.AddImportForMappedHelperMethod(mappedStaticMethod, context);
                    methodName = mappedStaticMethod;
                }
            }
        }

        if (!ExpressionTransformerHelpers.IsMappedCompatibilityHelperMethod(methodName)
            && ExpressionTransformerHelpers.TryGetStaticReceiverType(
                memberAccess.Expression,
                context,
                out var staticReceiverTypeForMapping))
        {
            var staticReceiverTypeName = staticReceiverTypeForMapping.ToDisplayString();
            var mappedStaticReceiverMethod = context.TypeMappings.MapMethod(
                staticReceiverTypeName,
                originalMethodName,
                methodSymbol?.Parameters.Length);
            if (mappedStaticReceiverMethod == null)
            {
                var fqn = $"{staticReceiverTypeForMapping.ContainingNamespace}.{staticReceiverTypeForMapping.Name}";
                mappedStaticReceiverMethod = context.TypeMappings.MapMethod(
                    fqn,
                    originalMethodName,
                    methodSymbol?.Parameters.Length);
            }
            if (mappedStaticReceiverMethod != null)
            {
                ExpressionTransformerHelpers.AddImportForMappedHelperMethod(mappedStaticReceiverMethod, context);
                methodName = mappedStaticReceiverMethod;

                if (ExpressionTransformerHelpers.TryGetStaticTypeReceiverJavaReference(
                    memberAccess.Expression,
                    context,
                    boxJavaPrimitiveType: true,
                    out var semanticStaticReceiverForMapping,
                    out _))
                {
                    receiver = semanticStaticReceiverForMapping;
                }
            }
        }

        if (!ExpressionTransformerHelpers.IsMappedCompatibilityHelperMethod(methodName)
            && memberAccess.Expression is IdentifierNameSyntax aliasReceiverForMapping
            && context.ResolveAlias(aliasReceiverForMapping.Identifier.Text) is ITypeSymbol aliasTargetForMapping)
        {
            var aliasTargetName = aliasTargetForMapping.ToDisplayString();
            var aliasMapped = context.TypeMappings.MapMethod(
                aliasTargetName,
                originalMethodName,
                methodSymbol?.Parameters.Length);
            if (aliasMapped == null)
            {
                var fqn = $"{aliasTargetForMapping.ContainingNamespace}.{aliasTargetForMapping.Name}";
                aliasMapped = context.TypeMappings.MapMethod(
                    fqn,
                    originalMethodName,
                    methodSymbol?.Parameters.Length);
            }
            if (aliasMapped != null)
            {
                ExpressionTransformerHelpers.AddImportForMappedHelperMethod(aliasMapped, context);
                methodName = aliasMapped;
                var mappedReceiverType = context.TypeMappings.MapType(aliasTargetName);
                if (mappedReceiverType != aliasTargetName)
                    receiver = mappedReceiverType;
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
        // class also has a member with the same name (e.g. field Point).
        // C# properties become getXxx()/setXxx() in Java and don't collide with type names,
        // so exclude them from the collision check.
        if (methodSymbol is { IsStatic: true }
            && context.SemanticModel != null
            && memberAccess.Expression is IdentifierNameSyntax simpleTypeReceiver2
            && context.SemanticModel.GetEnclosingSymbol(node.SpanStart)?.ContainingType is INamedTypeSymbol enclosingType2
            && enclosingType2.GetMembers(simpleTypeReceiver2.Identifier.Text).Any(m => m is not INamedTypeSymbol and not IPropertySymbol))
        {
            var mappedReceiverType = context.MapType(methodSymbol.ContainingType);
            if (!string.IsNullOrWhiteSpace(mappedReceiverType)
                && mappedReceiverType != methodSymbol.ContainingType.ToDisplayString())
            {
                receiver = mappedReceiverType;
            }
            else
            {
                var ns2 = methodSymbol.ContainingType.ContainingNamespace?.ToDisplayString();
                var mappedNs2 = context.NamespaceToPackage(ns2 ?? "");
                if (string.IsNullOrWhiteSpace(mappedNs2))
                    receiver = methodSymbol.ContainingType.Name;
                else
                    receiver = $"{mappedNs2}.{methodSymbol.ContainingType.Name}";
            }
        }
        else if (methodSymbol == null)
        {
            // Semantic fallback: method resolution may fail in large project conversion even when
            // the receiver type symbol is still available (e.g. Console.WriteLine in partially
            // unresolved compilations). Use receiver type to recover method/type mappings.
            if (context.SemanticModel != null)
            {
                var receiverTypeSymbol = context.GetSymbolInfo(memberAccess.Expression).Symbol as INamedTypeSymbol;
                if (receiverTypeSymbol != null)
                {
                    var receiverTypeName = receiverTypeSymbol.ToDisplayString();
                    var mappedByReceiverType = context.TypeMappings.MapMethod(receiverTypeName, originalMethodName);
                    if (mappedByReceiverType != null)
                    {
                        ExpressionTransformerHelpers.AddImportForMappedHelperMethod(mappedByReceiverType, context);
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
            // Skip when the receiver identifier resolves to a field/local/parameter — it IS the
            // member, not a type reference that happens to be shadowed.
            if (context.SemanticModel != null
                && memberAccess.Expression is IdentifierNameSyntax simpleTypeReceiver3
                && context.SemanticModel.GetEnclosingSymbol(node.SpanStart)?.ContainingType is INamedTypeSymbol enclosingType3
                && enclosingType3.GetMembers(simpleTypeReceiver3.Identifier.Text).Any(m => m is not INamedTypeSymbol))
            {
                var receiverSymbol3 = context.GetSymbolInfo(memberAccess.Expression).Symbol;
                // Only override when the identifier is NOT a field/local/parameter/property reference.
                // e.g. field "streamWriter" has type StreamWriter — we must NOT replace the
                // receiver with the type name, or Java sees a static call on the class.
                // Similarly, property "Multiedges" of type Dictionary must keep the getter call,
                // not be replaced by the type name "System.Collections.Generic.Dictionary".
                if (receiverSymbol3 is not (IFieldSymbol or ILocalSymbol or IParameterSymbol or IPropertySymbol))
                {
                    var receiverType = context.GetTypeInfo(memberAccess.Expression).Type as INamedTypeSymbol;
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
            }

            // Syntactic fallback: when the semantic model could not resolve the method (e.g. missing
            // assembly reference), try mapping using the raw syntactic receiver string.  This handles
            // System.Console.WriteLine → System.out.println even without a full Roslyn compilation.
            var syntacticReceiver = memberAccess.Expression.ToString();
            var syntacticMapped = context.TypeMappings.MapMethod(syntacticReceiver, originalMethodName);
            if (syntacticMapped != null)
            {
                ExpressionTransformerHelpers.AddImportForMappedHelperMethod(syntacticMapped, context);
                methodName = syntacticMapped;
                var mappedReceiverType = context.TypeMappings.MapType(syntacticReceiver);
                if (mappedReceiverType != syntacticReceiver)
                    receiver = mappedReceiverType;
            }

            // Semantic type fallback: when the method symbol is unavailable (e.g. .NET 6+ APIs
            // like TryCopyTo in single-file mode), but the receiver's type can still be resolved,
            // use the receiver type to look up TypeMappings. This handles instance method calls
            // like s.TryCopyTo(dest, idx) where 's' is a string variable.
            if (methodName == originalMethodName && context.SemanticModel != null)
            {
                var receiverType = context.GetTypeInfo(memberAccess.Expression).Type;
                if (receiverType != null)
                {
                    var receiverTypeName = receiverType.ToDisplayString();
                    var typeMapped = context.TypeMappings.MapMethod(receiverTypeName, originalMethodName);
                    if (typeMapped == null)
                    {
                        var fqn = $"{receiverType.ContainingNamespace}.{receiverType.Name}";
                        typeMapped = context.TypeMappings.MapMethod(fqn, originalMethodName);
                    }
                    if (typeMapped != null)
                    {
                        ExpressionTransformerHelpers.AddImportForMappedHelperMethod(typeMapped, context);
                        methodName = typeMapped;
                    }
                }
            }

            // Common unresolved fallback: receiver appears as bare "Console" in syntax,
            // but mappings are keyed by "System.Console".
            if (methodName == originalMethodName && syntacticReceiver == "Console")
            {
                var consoleMapped = context.TypeMappings.MapMethod("System.Console", originalMethodName);
                if (consoleMapped != null)
                {
                    ExpressionTransformerHelpers.AddImportForMappedHelperMethod(consoleMapped, context);
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
                    ExpressionTransformerHelpers.AddImportForMappedHelperMethod(stringMapped, context);
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
                    methodName = originalMethodName == "Parse"
                        ? MapPrimitiveParseHelper(primitiveForAlias) ?? aliasMethod
                        : aliasMethod;
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

            // LINQ unresolved fallback: handles LINQ extension methods that were not rewritten
            // by LinqRewriter (single-method chains without terminals, compilation rebuild
            // issues, etc.).  Works with or without a resolved method symbol and with or
            // without a usable semantic model.
            if (originalMethodName is "Where" or "Select" or "SelectMany"
                                   or "OrderBy" or "OrderByDescending"
                && node.ArgumentList.Arguments.Count >= 1)
            {
                // Determine whether the receiver is IEnumerable-like.
                // When the semantic model is available use the precise type check;
                // otherwise assume any syntactically-unresolved call with these names is a
                // LINQ extension method on an iterable receiver.
                ITypeSymbol? unresolvedLinqReceiverType = null;
                bool likelyEnumerable;
                if (context.SemanticModel != null)
                {
                    unresolvedLinqReceiverType = context.GetTypeInfo(memberAccess.Expression).Type;
                    likelyEnumerable = ImplementsIEnumerable(unresolvedLinqReceiverType)
                        || unresolvedLinqReceiverType is IArrayTypeSymbol
                        || unresolvedLinqReceiverType == null
                        || unresolvedLinqReceiverType is IErrorTypeSymbol;
                }
                else
                {
                    likelyEnumerable = true;
                }

                if (likelyEnumerable)
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

                    // Add .collect() terminal when this is the outermost expression in the chain
                    // (not used as a receiver for another method call like .ToList() or .Select())
                    bool needsTerminal = node.Parent is not MemberAccessExpressionSyntax;
                    var terminal = "";
                    if (needsTerminal)
                    {
                        context.AddImport("java.util.stream.Collectors");
                        context.AddImport("java.util.ArrayList");
                        terminal = ".collect(Collectors.toCollection(() -> new ArrayList<>()))";
                    }

                    // Map C# LINQ method → Java Stream method
                    if (originalMethodName == "Where")
                    {
                        var unresolvedArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                        return $"{unresolvedLinqReceiver}.filter({unresolvedArg}){terminal}";
                    }

                    if (originalMethodName == "Select")
                    {
                        var unresolvedArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                        return $"{unresolvedLinqReceiver}.map({unresolvedArg}){terminal}";
                    }

                    if (originalMethodName == "SelectMany")
                    {
                        var unresolvedArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                        return $"{unresolvedLinqReceiver}.flatMap({unresolvedArg}){terminal}";
                    }

                    // OrderBy / OrderByDescending: when the result is discarded (terminal /
                    // standalone statement), sort in-place so the original intent is preserved
                    // (C# OrderBy is non-mutating and returns a new enumerable, so a bare
                    // OrderBy call that discards the result is a no-op — the developer almost
                    // certainly intended to sort the collection).
                    if (originalMethodName is "OrderBy" or "OrderByDescending")
                    {
                        context.AddImport("java.util.Comparator");
                        var orderKeyArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                        var comparator = $"Comparator.comparing({orderKeyArg})";
                        if (originalMethodName == "OrderByDescending")
                            comparator = $"{comparator}.reversed()";

                        if (needsTerminal)
                        {
                            context.AddImport("java.util.Collections");
                            return $"Collections.sort({receiver}, {comparator})";
                        }

                        return $"{unresolvedLinqReceiver}.sorted({comparator})";
                    }
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
                wrapperClass = GetFlagsEnumValueType(namedEnumType, context) == "long" ? "Long" : "Integer";
            }

            // Fallback: flags enum registry covers cross-file scenarios where the enum
            // declaration was seen in a different file in this project compilation.
            if (wrapperClass == null
                && receiverSymbol?.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum
                && (context.IsFlagsEnum(receiverSymbol.Name)
                    || context.IsFlagsEnum(receiverSymbol.ToDisplayString() ?? string.Empty)))
            {
                wrapperClass = receiverSymbol is INamedTypeSymbol namedFlags
                    && GetFlagsEnumValueType(namedFlags, context) == "long"
                    ? "Long"
                    : "Integer";
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

        // StringBuilder.AppendFormat(fmt, args) → sb.append(String.format(fmt, args))
        // Java's StringBuilder has no appendFormat(); use append(String.format()) instead.
        if (originalMethodName == "AppendFormat"
            && (methodSymbol?.ContainingType.ToDisplayString() is "System.Text.StringBuilder"
                || ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression,
                    context,
                    "StringBuilder",
                    "System.Text.StringBuilder")))
        {
            int appendFmtStart = isExtensionInStaticPath ? 1 : 0;
            // Rewrite C# {N} format placeholders to Java % specifiers inside String.format()
            if (node.ArgumentList.Arguments.Count > appendFmtStart
                && node.ArgumentList.Arguments[appendFmtStart].Expression is LiteralExpressionSyntax
                    { RawKind: (int)SyntaxKind.StringLiteralExpression } appendStrLit)
            {
                var rewrittenFormat = RewriteStringFormatLiteral(appendStrLit.Token.ValueText);
                var remainingArgs = ArgumentTransformer.TransformArgumentList(
                    node.ArgumentList, context, facade, appendFmtStart + 1);
                var innerFormatCall = string.IsNullOrEmpty(remainingArgs)
                    ? $"String.format({rewrittenFormat})"
                    : $"String.format({rewrittenFormat}, {remainingArgs})";
                return $"{receiver}.append({innerFormatCall})";
            }
            var fmtArgs = ArgumentTransformer.TransformArgumentList(
                node.ArgumentList, context, facade, appendFmtStart, methodSymbol);
            return $"{receiver}.append(String.format({fmtArgs}))";
        }

        // Delegate .Invoke(args) fallback: when the semantic model couldn't identify this as
        // MethodKind.DelegateInvoke (e.g. in project pipeline with incomplete assembly refs),
        // use InferSamMethodName heuristic based on argument count and expression position.
        // .Invoke() is almost exclusively used for C# delegate invocations.
        // System.Reflection.MethodInfo.Invoke is NOT a delegate invocation — skip it.
        if (methodName == "Invoke" && methodName == originalMethodName
            && !IsMethodInfoReceiver(memberAccess.Expression, context))
        {
            bool seemsVoid = node.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionStatementSyntax;
            int paramCount = node.ArgumentList.Arguments.Count;
            methodName = Type.DelegateTransformer.InferSamMethodName(seemsVoid, paramCount);
        }

        // Apply the same camelCase conversion at call sites that MethodTransformer applies at
        // declaration sites.  Only runs when no explicit TypeMappings override was found so that
        // hand-crafted renames (e.g. Add → add) are never double-processed.
        if (methodName == originalMethodName)
        {
            // Primitive type static method mapping: when the method belongs to a C# primitive type
            // (e.g. char.IsLower), use the specialized mapper that knows Java wrapper class differences.
            if (methodSymbol is { IsStatic: true }
                && TryGetPrimitiveKeyword(methodSymbol.ContainingType, out var primKeywordForMethod))
            {
                var primMethodMapped = MapPrimitiveStaticMethodName(primKeywordForMethod, originalMethodName);
                if (primMethodMapped != originalMethodName)
                {
                    methodName = primMethodMapped;
                    receiver = ExpressionTransformerHelpers.BoxJavaPrimitiveType(primKeywordForMethod);
                }
            }
        }

        if (methodName == originalMethodName)
        {
            // When a non-operator method's camelCase name would collide with an auto-generated
            // operator method in the same class, keep the PascalCase name — the method declaration
            // will be renamed to PascalCase by AddMethodIfNotDuplicate collision resolution.
            bool wouldCollideWithOp = false;
            bool wouldCollideWithPropertyAccessor = false;
            if (methodSymbol != null
                && methodSymbol.MethodKind != MethodKind.UserDefinedOperator
                && originalMethodName.Length > 0 && char.IsUpper(originalMethodName[0]))
            {
                var camelName = char.ToLowerInvariant(originalMethodName[0]) + originalMethodName[1..];
                wouldCollideWithOp = WouldCollideWithOperatorInType(methodSymbol.ContainingType, camelName, methodSymbol);
                wouldCollideWithPropertyAccessor = WouldCollideWithPropertyAccessorInType(
                    methodSymbol.ContainingType,
                    camelName,
                    methodSymbol,
                    context);
            }

            if (!wouldCollideWithOp && !wouldCollideWithPropertyAccessor)
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
                    // Math method names that differ from simple camelCase
                    // Note: "Sign" → "signum" is handled specifically for System.Math / System.MathF
                    // at the call site above (around line 1407).  Do NOT add a universal
                    // "Sign" → "signum" mapping here — it would rewrite custom types' Sign()
                    // methods too (e.g. ApproximateComparer.Sign()).
                    "Ceiling"       => "ceil",
                    "Truncate"      => "truncate",
                    _ when methodName.Length > 0
                        => char.ToLowerInvariant(methodName[0]) + methodName[1..],
                    _ => methodName
                };
            }
        }

        methodName = ConversionContext.EscapeJavaKeyword(methodName);

        // Type-erasure rename: when the resolved overload is the one with fewer type parameters,
        // append the same suffix that MethodTransformer uses at the declaration site.
        if (methodSymbol != null)
            methodName += ConversionContext.GetErasureConflictSuffix(methodSymbol);

        // Issue 5: when promoting to static-call form, start at index 1 to skip the receiver
        // that was already prepended; use 0 for standard instance calls.
        int argStartIndex = isExtensionInStaticPath ? 1 : 0;

        // Fallback: String.IsNullOrEmpty(s) -> StringHelper.isNullOrEmpty(s)
        bool isStringIsNullOrEmpty = originalMethodName == "IsNullOrEmpty"
            && node.ArgumentList.Arguments.Count - argStartIndex >= 1
            && IsSystemStringMethod(methodSymbol, memberAccess.Expression, context);
        if (isStringIsNullOrEmpty)
        {
            var valueExpr = facade.Transform(node.ArgumentList.Arguments[argStartIndex].Expression, context);
            return $"StringHelper.isNullOrEmpty({valueExpr})";
        }

        // Fallback: String.IsNullOrWhiteSpace(s) -> StringHelper.isNullOrWhiteSpace(s)
        bool isStringIsNullOrWhiteSpace = originalMethodName == "IsNullOrWhiteSpace"
            && node.ArgumentList.Arguments.Count - argStartIndex >= 1
            && IsSystemStringMethod(methodSymbol, memberAccess.Expression, context);
        if (isStringIsNullOrWhiteSpace)
        {
            var valueExpr = facade.Transform(node.ArgumentList.Arguments[argStartIndex].Expression, context);
            return $"StringHelper.isNullOrWhiteSpace({valueExpr})";
        }

        // Fallback: String.Concat(...) -> StringHelper.concat(...)
        bool isStringConcat = originalMethodName == "Concat"
            && node.ArgumentList.Arguments.Count - argStartIndex >= 1
            && IsSystemStringMethod(methodSymbol, memberAccess.Expression, context);
        if (isStringConcat)
        {
            var concatArgs = ArgumentTransformer.TransformArgumentList(
                node.ArgumentList, context, facade, argStartIndex, methodSymbol);
            return $"StringHelper.concat({concatArgs})";
        }

        // Static Regex.Match(input, pattern) / Regex.IsMatch(input, pattern) use helper
        // factory methods. Instance regex.Match(input) / regex.IsMatch(input) must stay
        // as receiver.method(input); TypeMappings maps both names to the instance form.
        if (methodSymbol is { IsStatic: true }
            && methodSymbol.ContainingType.ToDisplayString() == "System.Text.RegularExpressions.Regex"
            && originalMethodName is "Match" or "IsMatch")
        {
            var helperArgs = ArgumentTransformer.TransformArgumentList(
                node.ArgumentList, context, facade, argStartIndex, methodSymbol);
            var helperMethod = originalMethodName == "Match" ? "match" : "isMatch";
            return $"Regex.{helperMethod}({helperArgs})";
        }

        // When project compilation leaves Regex instance methods unresolved, TypeMappings
        // can still map Match/IsMatch to the instance Java names.  Use the transformed
        // receiver rather than emitting Regex.match(input), which is the static overload.
        if (methodSymbol == null
            && originalMethodName is "Match" or "IsMatch"
            && node.ArgumentList.Arguments.Count - argStartIndex >= 1
            && LooksLikeRegexInstanceExpression(memberAccess.Expression, context))
        {
            var regexArgs = ArgumentTransformer.TransformArgumentList(
                node.ArgumentList, context, facade, argStartIndex, methodSymbol);
            var regexMethod = originalMethodName == "Match" ? "match" : "isMatch";
            return $"{receiver}.{regexMethod}({regexArgs})";
        }

        // Fallback: unresolved numeric TryParse static calls.
        // Emit converter helper calls instead of invalid Double.TryParse/Integer.TryParse in Java.
        if (originalMethodName == "TryParse" && node.ArgumentList.Arguments.Count - argStartIndex >= 2)
        {
            var helper = GetTryParseHelperMethod(memberAccess.Expression, context);

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
                || ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression,
                    context,
                    "JsonSerializer",
                    "System.Text.Json.JsonSerializer"));
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
                deserializeTargetType = context.GetTypeInfo(genericDeserialize.TypeArgumentList.Arguments[0]).Type;
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
            || methodName.StartsWith("StringHelper.", StringComparison.Ordinal)
            || methodName.StartsWith("Regex.", StringComparison.Ordinal)
            || methodName.StartsWith("Encoding.", StringComparison.Ordinal)
            || methodName.StartsWith("DrawingColor.", StringComparison.Ordinal))
        {
            var helperArgs = ArgumentTransformer.TransformArgumentList(
                node.ArgumentList, context, facade, argStartIndex, methodSymbol);

            // When the original C# method is an instance method (e.g. s.Insert, s.IndexOfAny),
            // the receiver must be passed as the first argument to the static helper method.
            // Static methods (e.g. string.Concat, string.IsNullOrEmpty) don't need this.
            // When methodSymbol is null (e.g. .NET 6+ APIs like TryCopyTo), infer from syntax:
            // if the receiver is not a type name, it's an instance call.
            bool isInstanceCall = methodSymbol is { IsStatic: false }
                || (methodSymbol == null && !LooksLikeTypeReceiver(memberAccess.Expression, context));
            if (isInstanceCall)
            {
                helperArgs = string.IsNullOrEmpty(helperArgs)
                    ? receiver
                    : $"{receiver}, {helperArgs}";
            }

            // Enum.TryParse<T>(name, out result) -> EnumHelper.tryParse(name, holder, T.class)
            // The generic type argument T is not part of the C# argument list, so we must
            // append T.class explicitly so the Java method can resolve the enum at runtime.
            if (methodName == "EnumHelper.tryParse" && methodSymbol != null
                && methodSymbol.TypeArguments.Length > 0)
            {
                var classLiteral = ConversionContext.GetClassLiteral(methodSymbol.TypeArguments[0], context);
                helperArgs = string.IsNullOrEmpty(helperArgs) ? classLiteral : $"{helperArgs}, {classLiteral}";
            }

            return $"{methodName}({helperArgs})";
        }

        if (originalMethodName == "Parse"
            && methodName is "parseInt" or "parseLong" or "parseDouble" or "parseFloat"
            && TryGetParseHelperMethod(memberAccess.Expression, context, out var earlyParseHelper))
        {
            var helperArgs = ArgumentTransformer.TransformArgumentList(
                node.ArgumentList, context, facade, argStartIndex, methodSymbol);
            return $"{earlyParseHelper}({helperArgs})";
        }

        // Regex.Split(input, pattern) -> input.split(pattern)
        bool isRegexSplit = originalMethodName == "Split"
            && node.ArgumentList.Arguments.Count >= 2
            && (methodSymbol?.ContainingType.ToDisplayString() == "System.Text.RegularExpressions.Regex"
                || ExpressionTransformerHelpers.StaticReceiverMatches(
                    memberAccess.Expression,
                    context,
                    "Regex",
                    "System.Text.RegularExpressions.Regex"));
        if (isRegexSplit)
        {
            var splitInput = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
            var splitPattern = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
            return $"{splitInput}.split({splitPattern})";
        }

        // Fix: String.Split(' ') → Java split(" ") — Java's split() takes a String regex, not char.
        // Convert any char literal arguments to their regex-string equivalents.
        bool isStringSplit = originalMethodName == "Split"
            && IsSystemStringMethod(methodSymbol, memberAccess.Expression, context);
        if (isStringSplit && node.ArgumentList.Arguments.Count > argStartIndex)
        {
            if (TryTransformSplitWithRemoveEmptyEntries(node.ArgumentList, receiver, methodName, context, facade, argStartIndex, out var removeEmptySplit))
            {
                return removeEmptySplit;
            }

            var splitArgs = TransformSplitArguments(node.ArgumentList, context, facade, argStartIndex);
            return $"{receiver}.{methodName}({splitArgs})";
        }

        // When IsSystemStringMethod fails (e.g. in project-pipeline compilations
        // where the semantic model can't resolve the receiver type), still try to
        // convert char-literal arguments for the "split" method.
        if (originalMethodName == "Split" && !isStringSplit
            && methodSymbol == null
            && node.ArgumentList.Arguments.Count > argStartIndex)
        {
            if (HasCharLiteralArgument(node.ArgumentList, argStartIndex))
            {
                var splitArgs = TransformSplitArguments(node.ArgumentList, context, facade, argStartIndex);
                return $"{receiver}.{methodName}({splitArgs})";
            }
        }

        // String.TrimStart([chars]) -> stripLeading() for common whitespace trimming usage.
        bool isStringTrimStart = originalMethodName == "TrimStart"
            && IsSystemStringMethod(methodSymbol, memberAccess.Expression, context);
        if (isStringTrimStart)
        {
            return $"{receiver}.stripLeading()";
        }

        // Fix: String.Format("{0}  {1}", a, b) → String.format("%s  %s", a, b)
        // C# uses {N} / {N:specifier} placeholders; Java uses printf-style % specifiers.
        // Only rewrite when the first argument is a string literal — dynamic format strings
        // cannot be statically rewritten and are left as-is.
        bool isStringFormat = originalMethodName == "Format"
            && IsSystemStringMethod(methodSymbol, memberAccess.Expression, context);
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
            && !isExtensionInStaticPath
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
                context.AddImport("java.util.ArrayList");
                firstArg = $"{firstArg}.collect(Collectors.toCollection(() -> new ArrayList<>()))";
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

        // StringWriter.WriteLine(...) → Java's StringWriter only has write(string).
        // Append "\n" to the argument to preserve the newline semantics.
        if (originalMethodName == "WriteLine"
            && methodName == "write"
            && methodSymbol?.ContainingType.ToDisplayString() == "System.IO.StringWriter")
        {
            var writeArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, argStartIndex, methodSymbol);
            if (string.IsNullOrEmpty(writeArgs))
                return $"{receiver}.{methodName}(\"\\n\")";
            return $"{receiver}.{methodName}({writeArgs} + \"\\n\")";
        }

        // StringWriter declared as TextWriter (polymorphic): TextWriter.WriteLine
        // maps to println, but Java's StringWriter has no println.  Detect the
        // actual receiver type and redirect to write(… + "\n").
        if (originalMethodName == "WriteLine"
            && methodName == "println"
            && node.Expression is MemberAccessExpressionSyntax swMa
            && context.GetTypeInfo(swMa.Expression).Type?.ToDisplayString()
                == "System.IO.StringWriter")
        {
            var writeArgs = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, argStartIndex, methodSymbol);
            if (string.IsNullOrEmpty(writeArgs))
                return $"{receiver}.write(\"\\n\")";
            return $"{receiver}.write({writeArgs} + \"\\n\")";
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

            // Enumerable.Empty<T>() → Collections.<T>emptyList()
            // Using Collections.emptyList() instead of Stream.<T>empty() because
            // Stream does not implement Iterable in Java and is incompatible with
            // materialized collections (ArrayList, List) in ternary expressions.
            if (originalMethodName == "Empty")
            {
                context.AddImport("java.util.Collections");
                if (node.Expression is MemberAccessExpressionSyntax ma
                    && ma.Name is GenericNameSyntax gns
                    && gns.TypeArgumentList.Arguments.Count > 0)
                {
                    var typeArg = facade.Transform(gns.TypeArgumentList.Arguments[0], context);
                    return $"Collections.<{typeArg}>emptyList()";
                }
                return "Collections.emptyList()";
            }
        }

        // Array.Empty<T>() → new T[0]
        if (methodSymbol is { IsStatic: true, IsExtensionMethod: false }
            && originalMethodName == "Empty"
            && methodSymbol.ContainingType.ToDisplayString() == "System.Array")
        {
            if (node.Expression is MemberAccessExpressionSyntax ma
                && ma.Name is GenericNameSyntax gns
                && gns.TypeArgumentList.Arguments.Count > 0)
            {
                var typeArg = facade.Transform(gns.TypeArgumentList.Arguments[0], context);
                return $"new {typeArg}[0]";
            }
            return "new Object[0]";
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
            // When stripping AsEnumerable on an array receiver, wrap with ArrayHelper.toList()
            // because Java arrays don't implement Iterable (unlike C# arrays which implement IEnumerable).
            var rcvType = context.GetTypeInfo(memberAccess.Expression).Type;
            if (rcvType is IArrayTypeSymbol arrayType)
            {
                return ExpressionTransformerHelpers.BuildArrayToCollectionExpression(receiver, arrayType, context);
            }
            return receiver;
        }

        // Pre-LINQ-block intercept: ToDictionary may fail semantic resolution; handle by name.
        // Only fire when the LINQ block won't handle it (methodSymbol is null or not a LINQ Enumerable/Queryable extension).
        if (originalMethodName == "ToDictionary" && node.ArgumentList.Arguments.Count >= 2
            && (methodSymbol == null || !methodSymbol.IsExtensionMethod
                || methodSymbol.ContainingType.ToDisplayString() is not ("System.Linq.Enumerable" or "System.Linq.Queryable")))
        {
            context.AddImport("java.util.stream.Collectors");
            context.AddImport("java.util.ArrayList");
            string toMapRcv = receiver;
            ITypeSymbol? rcvElemType = null;
            if (!IsReceiverLinqExtension(memberAccess.Expression, context))
            {
                var rcvType = context.GetTypeInfo(memberAccess.Expression).Type;
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
                // Extension method called as static (e.g. Enumerable.Reverse(points)):
                // the first argument is the real receiver/data source, not the type name.
                if (methodSymbol.MethodKind != MethodKind.ReducedExtension
                    && node.ArgumentList.Arguments.Count > 0)
                {
                    var firstArg = node.ArgumentList.Arguments[0];
                    receiver = facade.Transform(firstArg.Expression, context);
                    var firstArgType = context.GetTypeInfo(firstArg.Expression).Type;
                    receiver = BuildStreamReceiverExpression(
                        receiver,
                        firstArgType,
                        context,
                        boxPrimitiveArrayElements: false,
                        preserveGroupingValueStream: true,
                        receiverSyntaxNode: firstArg.Expression);
                    isExtensionInStaticPath = true;
                }
                else
                {
                    var linqReceiverType = context.GetTypeInfo(memberAccess.Expression).Type;
                    receiver = BuildStreamReceiverExpression(
                        receiver,
                        linqReceiverType,
                        context,
                        boxPrimitiveArrayElements: false,
                        preserveGroupingValueStream: true,
                        receiverSyntaxNode: memberAccess.Expression);
                }
            }

            // ToList → collect(Collectors.toCollection(() -> new ArrayList<>()))
            // C# ToList() returns the concrete List<T> class, so the Java collector must produce
            // ArrayList<T> (not the List<T> interface returned by Collectors.toList()).
            if (originalMethodName == "ToList")
            {
                // If receiver already ends with .collect() (e.g., LINQ rewriter already materialized),
                // return as-is — calling .collect() on an ArrayList is invalid Java.
                if (Utilities.ExpressionTransformerHelpers.StripCollect(receiver) != receiver)
                    return receiver;

                if (receiver.EndsWith(".stream()", StringComparison.Ordinal))
                {
                    context.AddImport("java.util.stream.StreamSupport");
                    var baseReceiver = receiver[..^".stream()".Length];
                    receiver = $"StreamSupport.stream({baseReceiver}.spliterator(), false)";
                }
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.ArrayList");
                return $"{receiver}.collect(Collectors.toCollection(() -> new ArrayList<>()))";
            }

            // ToDictionary → collect(Collectors.toMap(keySelector, valueSelector))
            if (originalMethodName == "ToDictionary" && node.ArgumentList.Arguments.Count >= 2)
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.ArrayList");
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
                context.AddImport("java.util.Comparator");
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
                            var javaElem = context.TypeMapper.MapType(srcType);
                            if (!string.IsNullOrEmpty(javaElem))
                            {
                                var boxedElem = ExpressionTransformerHelpers.BoxJavaPrimitiveType(javaElem);
                                return reversed
                                    ? $"Comparator.<{boxedElem}>naturalOrder().reversed()"
                                    : $"Comparator.<{boxedElem}>naturalOrder()";
                            }
                        }
                        return reversed ? "Comparator.reverseOrder()" : "Comparator.naturalOrder()";
                    }
                    if (methodSymbol.TypeArguments.Length >= 2
                        && TryGetSingleParamLambda(node.ArgumentList.Arguments[argStartIndex].Expression,
                            context, facade, out var lParam, out var lBody))
                    {
                        var srcType  = methodSymbol.TypeArguments[0];
                        var keyType  = methodSymbol.TypeArguments[1];
                        var javaElem = context.TypeMapper.MapType(srcType);
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
                        var cmp = $"Comparator.{comparingFn}({typedParam} -> {lBody})";
                        return reversed ? $"{cmp}.reversed()" : cmp;
                    }
                    // Fallback (no type info): use untyped comparing
                    var fallback = $"Comparator.comparing({sortArgs})";
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
                context.AddImport("java.util.Comparator");
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
                        var javaElem = context.TypeMapper.MapType(srcType);

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
            // Java constraint: IntStream.mapToInt / DoubleStream.mapToDouble / LongStream.mapToLong
            // don't exist — when the receiver is already the matching primitive stream, use map()
            // for the selector form and skip the mapping entirely for the identity form.
            if (originalMethodName == "Sum")
            {
                var sumTargetCat = "int";
                if (methodSymbol.ReturnType != null)
                {
                    var retType = methodSymbol.ReturnType.SpecialType;
                    if (retType == SpecialType.System_Int64)
                        sumTargetCat = "long";
                    else if (retType is SpecialType.System_Double or SpecialType.System_Single or SpecialType.System_Decimal)
                        sumTargetCat = "double";
                }
                var sumReceiverCat = DetectReceiverPrimitiveStreamCategory(memberAccess, receiver, context);
                if (node.ArgumentList.Arguments.Count > 0)
                {
                    var sumArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    var sumMapOp = (sumReceiverCat == sumTargetCat)
                        ? "map"
                        : sumTargetCat switch { "int" => "mapToInt", "long" => "mapToLong", "double" => "mapToDouble", _ => "map" };
                    return $"{receiver}.{sumMapOp}({sumArg}).sum()";
                }
                if (sumReceiverCat != "")
                    return $"{receiver}.sum()";
                var mapMethod = sumTargetCat switch { "int" => "mapToInt", "long" => "mapToLong", "double" => "mapToDouble", _ => "mapToInt" };
                return $"{receiver}.{mapMethod}(x -> x).sum()";
            }

            // Average → mapToDouble + average().orElse(0)
            // All primitive streams (IntStream/LongStream/DoubleStream) have .average(),
            // so skip mapToDouble when the receiver is already any primitive stream.
            // For the selector form, use map() if receiver is already DoubleStream.
            if (originalMethodName == "Average")
            {
                var avgReceiverCat = DetectReceiverPrimitiveStreamCategory(memberAccess, receiver, context);
                if (node.ArgumentList.Arguments.Count > 0)
                {
                    var avgArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    var avgMapOp = (avgReceiverCat == "double") ? "map" : "mapToDouble";
                    return $"{receiver}.{avgMapOp}({avgArg}).average().orElse(0)";
                }
                if (avgReceiverCat != "")
                    return $"{receiver}.average().orElse(0)";
                return $"{receiver}.mapToDouble(x -> x).average().orElse(0)";
            }

            // GroupBy → collect(Collectors.groupingBy(keySelector)).entrySet().stream()
            // The entrySet().stream() makes the result chainable (downstream .map/.filter etc. work).
            // IGrouping<K,V> lambdas translate: g.getKey() → Map.Entry.getKey(), g.stream() → g.getValue().stream()
            if (originalMethodName == "GroupBy")
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.ArrayList");
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
                             + $".collect(Collectors.toCollection(() -> new ArrayList<>()))";
                    }
                    // Element-selector overload
                    var elemArg = facade.Transform(arg1Expr, context);
                    return $"{receiver}.collect(Collectors.groupingBy({keyArg}, Collectors.mapping({elemArg}, Collectors.toCollection(() -> new ArrayList<>())))).entrySet().stream()";
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
                var containsSourceType = context.GetTypeInfo(memberAccess.Expression).Type;
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
                context.AddImport("java.util.Objects");
                return $"{receiver}.anyMatch(_item -> Objects.equals(_item, {valArg}))";
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
                    var otherType = context.GetTypeInfo(concatArgExpr).Type;
                    otherStream = ExpressionTransformerHelpers.BuildStreamExpression(
                        otherArg, otherType, context, boxPrimitiveArrayElements: true);
                }
                }
                var concatReceiver = receiver;
                var concatReceiverType = context.GetTypeInfo(memberAccess.Expression).Type;
                if (concatReceiverType is IArrayTypeSymbol concatArr
                    && concatArr.ElementType.SpecialType is not SpecialType.None
                    && concatArr.ElementType.SpecialType is not SpecialType.System_Object)
                {
                    concatReceiver = $"{concatReceiver}.boxed()";
                }

                var concatStream = $"Stream.concat({concatReceiver}, {otherStream})";
                var concatType = context.GetTypeInfo(node).Type as INamedTypeSymbol;
                bool returnsEnumerable = concatType?.Name == "IEnumerable"
                    && concatType.ContainingNamespace?.ToDisplayString().StartsWith("System") == true;
                bool isChained = node.Parent is MemberAccessExpressionSyntax ma && ma.Expression == node;
                if (returnsEnumerable && !isChained)
                {
                    context.AddImport("java.util.stream.Collectors");
                    context.AddImport("java.util.ArrayList");
                    return $"{concatStream}.collect(Collectors.toCollection(() -> new ArrayList<>()))";
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
                    context.AddImport("java.util.ArrayList");
                    var whereIndexed = $"{whereReceiver}.collect(Collectors.collectingAndThen(Collectors.toCollection(() -> new ArrayList<>()),"
                                     + $" _src -> IntStream.range(0, _src.size())"
                                     + $".filter(_i -> {{ var {whP0} = _src.get(_i); int {whP1} = _i; return {whCond}; }})"
                                     + $".mapToObj(_src::get)))";
                    var whereType = context.GetTypeInfo(node).Type as INamedTypeSymbol;
                    bool whereReturnsEnumerable = whereType?.Name == "IEnumerable"
                        && whereType.ContainingNamespace?.ToDisplayString().StartsWith("System") == true;
                    bool whereIsChained = node.Parent is MemberAccessExpressionSyntax maWhere && maWhere.Expression == node;
                    return whereReturnsEnumerable && !whereIsChained
                        ? $"{whereIndexed}.collect(Collectors.toCollection(() -> new ArrayList<>()))"
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
                    context.AddImport("java.util.ArrayList");
                    var selectIndexed = $"{receiver}.collect(Collectors.collectingAndThen(Collectors.toCollection(() -> new ArrayList<>()),"
                                      + $" _src -> IntStream.range(0, _src.size())"
                                      + $".mapToObj(_i -> {{ var {selP0} = _src.get(_i); int {selP1} = _i; return {selBody}; }})))";
                    var selectType = context.GetTypeInfo(node).Type as INamedTypeSymbol;
                    bool selectReturnsEnumerable = selectType?.Name == "IEnumerable"
                        && selectType.ContainingNamespace?.ToDisplayString().StartsWith("System") == true;
                    bool selectIsChained = node.Parent is MemberAccessExpressionSyntax maSelect && maSelect.Expression == node;
                    return selectReturnsEnumerable && !selectIsChained
                        ? $"{selectIndexed}.collect(Collectors.toCollection(() -> new ArrayList<>()))"
                        : selectIndexed;
                }
                var mapArg = facade.Transform(selectLambdaArg, context);

                // On primitive streams (from primitive arrays), IntStream.map expects int→int.
                // Cross-type selectors need mapToDouble/mapToLong/mapToObj instead of map.
                var selectMapOp = "map";
                var selectSrcType = context.GetTypeInfo(memberAccess.Expression).Type;
                bool selectOnPrimitiveStream = false;
                string selectSrcCat = "";

                if (selectSrcType is IArrayTypeSymbol selectArrType
                    && methodSymbol.TypeArguments.Length >= 2)
                {
                    selectSrcCat = PrimitiveStreamCategory(selectArrType.ElementType.SpecialType);
                    selectOnPrimitiveStream = selectSrcCat != "";
                }

                // Chained LINQ: receiver is IEnumerable<int>, not int[].
                // Detect via receiver string if it's still a primitive stream pipeline.
                // Guard: the source element type (TypeArguments[0]) must be a Java primitive.
                // Arrays.stream(T[]) for reference T produces Stream<T>, not IntStream.
                if (!selectOnPrimitiveStream && methodSymbol.TypeArguments.Length >= 2
                    && PrimitiveStreamCategory(methodSymbol.TypeArguments[0].SpecialType) != ""
                    && !receiver.Contains(".boxed()", StringComparison.Ordinal)
                    && !receiver.Contains(".mapToObj(", StringComparison.Ordinal))
                {
                    if (receiver.Contains("IntStream.range(", StringComparison.Ordinal))
                    {
                        selectSrcCat = "int";
                        selectOnPrimitiveStream = true;
                    }
                    else if (receiver.StartsWith("Arrays.stream(", StringComparison.Ordinal))
                    {
                        // Walk back to verify the Arrays.stream() source is a primitive array.
                        var srcExpr = memberAccess.Expression;
                        while (srcExpr is InvocationExpressionSyntax chainedInv
                            && chainedInv.Expression is MemberAccessExpressionSyntax innerMa)
                            srcExpr = innerMa.Expression;
                        var srcTypeInfo = context.GetTypeInfo(srcExpr).Type;
                        if (srcTypeInfo is IArrayTypeSymbol srcArr
                            && PrimitiveStreamCategory(srcArr.ElementType.SpecialType) != "")
                        {
                            selectSrcCat = PrimitiveStreamCategory(srcArr.ElementType.SpecialType);
                            selectOnPrimitiveStream = true;
                        }
                    }
                }

                if (selectOnPrimitiveStream)
                {
                    var resCat = PrimitiveStreamCategory(methodSymbol.TypeArguments[1].SpecialType);
                    if (resCat != "" && resCat != selectSrcCat)
                        selectMapOp = resCat switch { "double" => "mapToDouble", "long" => "mapToLong", _ => "mapToInt" };
                    else if (resCat == "")
                        selectMapOp = "mapToObj";
                    // When resCat == selectSrcCat, keep "map" (IntStream.map for int→int)
                }

                return $"{receiver}.{selectMapOp}({mapArg})";
            }

            // SelectMany → flatMap(selector)
            if (originalMethodName == "SelectMany")
            {
                if (node.ArgumentList.Arguments.Count >= 2)
                {
                    // Two-arg: SelectMany(collectionSelector, resultSelector)
                    var collArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    var resArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                    // Detect if receiver is a primitive stream (IntStream) that needs .boxed()
                    // before .flatMap() — otherwise flatMap expects IntFunction<IntStream>.
                    var twoArgBoxed = DetectReceiverPrimitiveStreamCategory(memberAccess, receiver, context) != ""
                        ? ".boxed()" : "";
                    return $"{receiver}{twoArgBoxed}.flatMap({collArg}).map({resArg})";
                }
                if (node.ArgumentList.Arguments.Count == 1)
                {
                    // Check for indexed overload: SelectMany((item, index) => ...)
                    var smArgExpr = node.ArgumentList.Arguments[0].Expression;
                    if (TryGetTwoParamLambda(smArgExpr, context, facade, out var smIdxP0, out var smIdxP1, out var smIdxBody))
                    {
                        // SelectMany((item, i) => body): collect to list, IntStream.range for index, flatMap each
                        context.AddImport("java.util.stream.IntStream");
                        context.AddImport("java.util.stream.Collectors");
                        context.AddImport("java.util.ArrayList");
                        // Determine if the inner body returns something we need to stream
                        ExpressionSyntax? smIdxBodyExpr = smArgExpr switch
                        {
                            ParenthesizedLambdaExpressionSyntax pl => pl.ExpressionBody,
                            _ => null
                        };
                        var smIdxBodyRetType = smIdxBodyExpr != null
                            ? context.GetTypeInfo(smIdxBodyExpr).Type
                            : null;
                        string innerStreamExpr;
                        if (smIdxBodyRetType is IArrayTypeSymbol smIdxArr)
                        {
                            var arrStream = ExpressionTransformerHelpers.BuildArrayStreamExpression(
                                smIdxBody, smIdxArr, context, boxed: smIdxArr.ElementType.IsValueType);
                            innerStreamExpr = arrStream;
                        }
                        else if (smIdxBodyRetType != null && ImplementsIEnumerable(smIdxBodyRetType))
                        {
                            innerStreamExpr = BuildStreamReceiverExpression(
                                smIdxBody, smIdxBodyRetType, context,
                                boxPrimitiveArrayElements: false, preserveGroupingValueStream: false);
                        }
                        else
                        {
                            // Assume the body already returns a stream-compatible expression
                            innerStreamExpr = smIdxBody;
                        }
                        return $"{receiver}.collect(Collectors.collectingAndThen(Collectors.toCollection(() -> new ArrayList<>()),"
                             + $" _src -> IntStream.range(0, _src.size())"
                             + $".mapToObj(_i -> {{ var {smIdxP0} = _src.get(_i); int {smIdxP1} = _i; return {innerStreamExpr}; }})"
                             + $".flatMap(java.util.function.Function.identity())))";
                    }

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
                        var bodyRetType = context.GetTypeInfo(smBodyExpr).Type;
                        if (bodyRetType is IArrayTypeSymbol smArr)
                        {
                            string bodyStr = facade.Transform(smBodyExpr, context);
                            var arrStream = ExpressionTransformerHelpers.BuildArrayStreamExpression(
                                bodyStr, smArr, context, boxed: smArr.ElementType.IsValueType);
                            flatMapArg = $"{smParam} -> {arrStream}";
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
                            && context.GetSymbolInfo(smArg.Expression).Symbol is IMethodSymbol selMethod
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
                    // IntStream/LongStream/DoubleStream.flatMap expects same-type stream result
                    // (e.g. IntFunction<IntStream>). When the lambda returns a non-primitive
                    // Stream<T>, we must .boxed() first to get Stream<Integer>.
                    var smReceiverType = context.GetTypeInfo(memberAccess.Expression).Type;
                    bool smNeedsBoxed = smReceiverType is IArrayTypeSymbol smArrType
                        && PrimitiveStreamCategory(smArrType.ElementType.SpecialType) != ""
                        && methodSymbol.TypeArguments.Length >= 2
                        && PrimitiveStreamCategory(methodSymbol.TypeArguments[1].SpecialType) == "";
                    if (!smNeedsBoxed && smReceiverType is not IArrayTypeSymbol)
                    {
                        // For chained LINQ (receiver is IEnumerable<int>, not int[]),
                        // check receiver string for primitive stream indicators.
                        // Guard: the source element type (TypeArguments[0]) must be a Java primitive.
                        // Arrays.stream(T[]) for reference T produces Stream<T>, not IntStream.
                        smNeedsBoxed = methodSymbol.TypeArguments.Length >= 2
                            && PrimitiveStreamCategory(methodSymbol.TypeArguments[0].SpecialType) != ""
                            && (receiver.StartsWith("Arrays.stream(", StringComparison.Ordinal)
                                || receiver.Contains("IntStream.range(", StringComparison.Ordinal))
                            && !receiver.Contains(".boxed()", StringComparison.Ordinal)
                            && !receiver.Contains(".mapToObj(", StringComparison.Ordinal)
                            && PrimitiveStreamCategory(methodSymbol.TypeArguments[1].SpecialType) == "";
                    }
                    // Fallback: string-based heuristic when semantic info is incomplete.
                    // If receiver looks like a primitive IntStream (Arrays.stream on int[])
                    // and flatMapArg returns a reference stream, we need .boxed().
                    // Guard: only apply when source element type is a primitive, or when
                    // we have no semantic info. Without this, Arrays.stream(ReferenceType[])
                    // would incorrectly get .boxed() since it produces Stream<T>, not IntStream.
                    if (!smNeedsBoxed
                        && !receiver.Contains(".boxed()", StringComparison.Ordinal)
                        && !receiver.Contains(".mapToObj(", StringComparison.Ordinal)
                        && (receiver.StartsWith("Arrays.stream(", StringComparison.Ordinal)
                            || receiver.Contains("IntStream.range(", StringComparison.Ordinal))
                        && (flatMapArg.Contains(".stream()", StringComparison.Ordinal)
                            || flatMapArg.Contains("StreamSupport.stream(", StringComparison.Ordinal))
                        && (methodSymbol == null
                            || methodSymbol.TypeArguments.Length < 1
                            || PrimitiveStreamCategory(methodSymbol.TypeArguments[0].SpecialType) != ""))
                    {
                        smNeedsBoxed = true;
                    }
                    var boxedInsert = smNeedsBoxed ? ".boxed()" : "";
                    return $"{receiver}{boxedInsert}.flatMap({flatMapArg})";
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

            // Min()/Max() — primitive streams (IntStream/DoubleStream/LongStream) use
            // parameterless min()/max(); boxed Stream<T> needs Comparator.naturalOrder().
            // Min(selector)/Max(selector) — use mapToInt/mapToDouble/mapToLong for numeric
            // return types so the result is a primitive stream with parameterless min()/max().
            if (originalMethodName is "Min" or "Max")
            {
                var op = originalMethodName == "Min" ? "min" : "max";
                var retSpec = methodSymbol?.ReturnType?.SpecialType ?? SpecialType.None;

                if (node.ArgumentList.Arguments.Count == 0)
                {
                    if (IsPrimitiveStreamContext(retSpec, receiver, memberAccess, context))
                        return $"{receiver}.{op}().orElseThrow()";
                    return $"{receiver}.{op}(java.util.Comparator.naturalOrder()).orElseThrow()";
                }

                // Min(selector) / Max(selector)
                var selArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var mapOp = GetPrimitiveMapOperation(retSpec, memberAccess, receiver, context);
                if (mapOp != null)
                    return $"{receiver}.{mapOp}({selArg}).{op}().orElseThrow()";
                return $"{receiver}.map({selArg}).{op}(java.util.Comparator.naturalOrder()).orElseThrow()";
            }

            // ToArray() → typed toArray() based on element type
            if (originalMethodName == "ToArray")
            {
                return TransformToArrayWithElementType(receiver, methodSymbol, node, memberAccess, context);
            }

            // ToHashSet() → collect(Collectors.toSet())
            if (originalMethodName == "ToHashSet")
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.ArrayList");
                return $"{receiver}.collect(Collectors.toSet())";
            }

            // Reverse() → collect to list, reverse in-place.
            // When chained into further LINQ operations (e.g., .Reverse().ToList()), return list.stream()
            // so subsequent stream operators can chain.  When used standalone (method argument, assignment,
            // foreach), return list directly — cast to Iterable<E> to avoid ambiguity with varargs overloads.
            if (originalMethodName == "Reverse")
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.ArrayList");
                context.AddImport("java.util.Collections");
                bool isChained = node.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax parentAccess
                    && parentAccess.Expression == node;
                var finisher = isChained ? "return list.stream();" : "return list;";
                var collectExpr = $"{receiver}.collect(Collectors.collectingAndThen(Collectors.toCollection(() -> new ArrayList<>()), list -> {{ Collections.reverse(list); {finisher} }}))";
                bool isStatement = node.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionStatementSyntax;
                if (!isChained && !isStatement && methodSymbol?.TypeArguments.Length >= 1)
                {
                    var elemJavaType = context.MapType(methodSymbol.TypeArguments[0]);
                    if (!string.IsNullOrEmpty(elemJavaType))
                        collectExpr = $"(Iterable<{elemJavaType}>) {collectExpr}";
                }
                return collectExpr;
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
                context.AddImport("java.util.ArrayList");
                context.AddImport("java.util.stream.Stream");
                if (node.ArgumentList.Arguments.Count >= 1)
                {
                    var defaultArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    return $"{receiver}.collect(Collectors.collectingAndThen(Collectors.toCollection(() -> new ArrayList<>()), list -> list.isEmpty() ? Stream.of({defaultArg}) : list.stream()))";
                }
                return $"{receiver}.collect(Collectors.collectingAndThen(Collectors.toCollection(() -> new ArrayList<>()), list -> list.isEmpty() ? Stream.of((Object) null) : list.stream()))";
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
                bool primitiveArrayAggregateSource = context.GetTypeInfo(memberAccess.Expression).Type is IArrayTypeSymbol srcArr1
                    && srcArr1.ElementType.IsValueType;
                var aggregateReceiver = primitiveArrayAggregateSource ? $"{receiver}.boxed()" : receiver;

                var seedArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var funcArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                var seedType1 = context.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type;
                var sourceType1 = context.GetTypeInfo(memberAccess.Expression).Type;
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
                bool primitiveArrayAggregateSource = context.GetTypeInfo(memberAccess.Expression).Type is IArrayTypeSymbol srcArr2
                    && srcArr2.ElementType.IsValueType;
                var aggregateReceiver = primitiveArrayAggregateSource ? $"{receiver}.boxed()" : receiver;

                var seedArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                var funcArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                var seedType2 = context.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type;
                var sourceType2 = context.GetTypeInfo(memberAccess.Expression).Type;
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
                context.AddImport("java.util.ArrayList");
                context.AddImport("java.util.stream.IntStream");
                var zipOtherArg0 = node.ArgumentList.Arguments[0];
                var zipOther = facade.Transform(zipOtherArg0.Expression, context);
                var zipOtherType = context.GetTypeInfo(zipOtherArg0.Expression).Type;
                string zipOtherListExpr;
                if (zipOtherType is IArrayTypeSymbol zipArrType2)
                    zipOtherListExpr = ExpressionTransformerHelpers.BuildArrayToCollectionExpression(zipOther, zipArrType2, context);
                else
                    zipOtherListExpr = zipOther;

                // When the receiver is a primitive stream (e.g. IntStream from Arrays.stream(int[])),
                // we must .boxed() before .collect() since IntStream.collect() has a different signature
                // (Supplier, ObjIntConsumer, BiConsumer) and does not accept Collector<T,A,R>.
                var zipReceiver = receiver;
                var zipReceiverType = context.GetTypeInfo(memberAccess.Expression).Type;
                if (zipReceiverType is IArrayTypeSymbol zipSrcArr
                    && PrimitiveStreamCategory(zipSrcArr.ElementType.SpecialType) != "")
                {
                    zipReceiver = $"{receiver}.boxed()";
                }

                if (TryGetTwoParamLambda(node.ArgumentList.Arguments[1].Expression, context, facade, out var zipP0, out var zipP1, out var zipBody))
                {
                    return $"{zipReceiver}.collect(Collectors.collectingAndThen(Collectors.toCollection(() -> new ArrayList<>()),"
                         + $" _left -> {{ var _right = {zipOtherListExpr};"
                         + $" return IntStream.range(0, Math.min(_left.size(), _right.size()))"
                         + $".mapToObj(_i -> {{ var {zipP0} = _left.get(_i); var {zipP1} = _right.get(_i); return {zipBody}; }})"
                         + $".collect(Collectors.toList()); }}))";
                }
                // Fallback: no two-param lambda recognised
                var zipSel = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"{zipReceiver}.collect(Collectors.collectingAndThen(Collectors.toCollection(() -> new ArrayList<>()),"
                     + $" _left -> {{ var _right = {zipOtherListExpr};"
                     + $" return IntStream.range(0, Math.min(_left.size(), _right.size())).mapToObj(_i -> _left.get(_i))"
                     + $".collect(Collectors.toList()); }}))";
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
                    var otherType = context.GetTypeInfo(unionArgExpr).Type;
                    otherStream = ExpressionTransformerHelpers.BuildStreamExpression(
                        otherArg, otherType, context, boxPrimitiveArrayElements: true);
                }
                return $"Stream.concat({receiver}, {otherStream}).distinct()";
            }

            // Intersect(other) → filter elements whose value is in the set
            if (originalMethodName == "Intersect" && node.ArgumentList.Arguments.Count >= 1)
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.ArrayList");
                context.AddImport("java.util.HashSet");
                var isectArg0 = node.ArgumentList.Arguments[0];
                var isectOther = facade.Transform(isectArg0.Expression, context);
                var isectOtherType = context.GetTypeInfo(isectArg0.Expression).Type;
                string isectSet = BuildSetExprFromOther(isectOther, isectOtherType, context);
                return $"{receiver}.filter({isectSet}::contains)";
            }

            // Except(other) → filter elements whose value is NOT in the set
            if (originalMethodName == "Except" && node.ArgumentList.Arguments.Count >= 1)
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.ArrayList");
                context.AddImport("java.util.HashSet");
                var exceptArg0 = node.ArgumentList.Arguments[0];
                var exceptOther = facade.Transform(exceptArg0.Expression, context);
                var exceptOtherType = context.GetTypeInfo(exceptArg0.Expression).Type;
                string exceptSet = BuildSetExprFromOther(exceptOther, exceptOtherType, context);
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
                context.AddImport("java.util.ArrayList");
                var seqOtherArg0 = node.ArgumentList.Arguments[0];
                var seqOtherStr = facade.Transform(seqOtherArg0.Expression, context);
                var seqOtherType = context.GetTypeInfo(seqOtherArg0.Expression).Type;
                string seqOtherList;
                if (seqOtherType is IArrayTypeSymbol seqArr)
                    seqOtherList = ExpressionTransformerHelpers.BuildArrayToCollectionExpression(seqOtherStr, seqArr, context);
                else
                    seqOtherList = $"{seqOtherStr}.stream().collect(Collectors.toCollection(() -> new ArrayList<>()))";
                return $"{receiver}.collect(Collectors.toCollection(() -> new ArrayList<>())).equals({seqOtherList})";
            }

            // TakeLast(n) → collect, then subList from the last n elements, re-stream
            if (originalMethodName == "TakeLast" && node.ArgumentList.Arguments.Count >= 1)
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.ArrayList");
                var nArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.collect(Collectors.collectingAndThen(Collectors.toCollection(() -> new ArrayList<>()),"
                     + $" _l -> _l.subList(Math.max(0, _l.size() - {nArg}), _l.size()).stream()))";
            }

            // SkipLast(n) → collect, then subList omitting the last n elements, re-stream
            if (originalMethodName == "SkipLast" && node.ArgumentList.Arguments.Count >= 1)
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.ArrayList");
                var nArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.collect(Collectors.collectingAndThen(Collectors.toCollection(() -> new ArrayList<>()),"
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
                context.AddImport("java.util.ArrayList");
                if (node.ArgumentList.Arguments.Count >= 2)
                {
                    var keyArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                    var elemArg = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                    return $"{receiver}.collect(Collectors.groupingBy({keyArg}, Collectors.mapping({elemArg}, Collectors.toCollection(() -> new ArrayList<>()))))";
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
                context.AddImport("java.util.ArrayList");
                context.AddImport("java.util.stream.IntStream");
                var nArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.collect(Collectors.collectingAndThen(Collectors.toCollection(() -> new ArrayList<>()),"
                     + $" _src -> {{ int _n = {nArg}; return IntStream.range(0, (_src.size() + _n - 1) / _n)"
                     + $".mapToObj(_i -> _src.subList(_i * _n, Math.min((_i + 1) * _n, _src.size()))); }}))";
            }

            // DistinctBy(keySelector) → groupingBy into LinkedHashMap (preserves order), take first per key
            if (originalMethodName == "DistinctBy" && node.ArgumentList.Arguments.Count >= 1)
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.ArrayList");
                context.AddImport("java.util.LinkedHashMap");
                var selArg = facade.Transform(node.ArgumentList.Arguments[0].Expression, context);
                return $"{receiver}.collect(Collectors.collectingAndThen("
                     + $"Collectors.groupingBy({selArg}, java.util.LinkedHashMap::new, Collectors.toCollection(() -> new ArrayList<>())),"
                     + $" _m -> _m.values().stream().map(_list -> _list.get(0))))";
            }

            // UnionBy(other, keySelector) → concat + distinctBy key
            if (originalMethodName == "UnionBy" && node.ArgumentList.Arguments.Count >= 2)
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.ArrayList");
                context.AddImport("java.util.LinkedHashMap");
                context.AddImport("java.util.stream.Stream");
                var ubOtherArg0 = node.ArgumentList.Arguments[0];
                var ubOther = facade.Transform(ubOtherArg0.Expression, context);
                string ubOtherStream;
                if (IsReceiverLinqExtension(ubOtherArg0.Expression, context))
                    ubOtherStream = ubOther;
                else
                {
                    var ubOtherType = context.GetTypeInfo(ubOtherArg0.Expression).Type;
                    ubOtherStream = ExpressionTransformerHelpers.BuildStreamExpression(
                        ubOther, ubOtherType, context, boxPrimitiveArrayElements: true);
                }
                var ubSel = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"Stream.concat({receiver}, {ubOtherStream})"
                     + $".collect(Collectors.collectingAndThen("
                     + $"Collectors.groupingBy({ubSel}, java.util.LinkedHashMap::new, Collectors.toCollection(() -> new ArrayList<>())),"
                     + $" _m -> _m.values().stream().map(_list -> _list.get(0))))";
            }

            // IntersectBy(otherKeys, keySelector) → filter elements whose key is in the key set
            if (originalMethodName == "IntersectBy" && node.ArgumentList.Arguments.Count >= 2)
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.ArrayList");
                var ibOtherArg0 = node.ArgumentList.Arguments[0];
                var ibOther = facade.Transform(ibOtherArg0.Expression, context);
                var ibOtherType = context.GetTypeInfo(ibOtherArg0.Expression).Type;
                string ibSetExpr = BuildSetExprFromOther(ibOther, ibOtherType, context);
                if (TryGetSingleParamLambda(node.ArgumentList.Arguments[1].Expression, context, facade, out var ibP, out var ibKeyBody))
                    return $"{receiver}.filter({ibP} -> {ibSetExpr}.contains({ibKeyBody}))";
                var ibSel = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"/* TODO: LINQ IntersectBy */ {receiver}.intersectBy({ibOther}, {ibSel})";
            }

            // ExceptBy(otherKeys, keySelector) → filter elements whose key is NOT in the key set
            if (originalMethodName == "ExceptBy" && node.ArgumentList.Arguments.Count >= 2)
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.ArrayList");
                var ebOtherArg0 = node.ArgumentList.Arguments[0];
                var ebOther = facade.Transform(ebOtherArg0.Expression, context);
                var ebOtherType = context.GetTypeInfo(ebOtherArg0.Expression).Type;
                string ebSetExpr = BuildSetExprFromOther(ebOther, ebOtherType, context);
                if (TryGetSingleParamLambda(node.ArgumentList.Arguments[1].Expression, context, facade, out var ebP, out var ebKeyBody))
                    return $"{receiver}.filter({ebP} -> !{ebSetExpr}.contains({ebKeyBody}))";
                var ebSel = facade.Transform(node.ArgumentList.Arguments[1].Expression, context);
                return $"/* TODO: LINQ ExceptBy */ {receiver}.exceptBy({ebOther}, {ebSel})";
            }

            // Join(inner, outerKey, innerKey, resultSelector) → flatMap + filter + map
            if (originalMethodName == "Join" && node.ArgumentList.Arguments.Count >= 4)
            {
                context.AddImport("java.util.Objects");
                var jInnerArg0 = node.ArgumentList.Arguments[0];
                var jInner = facade.Transform(jInnerArg0.Expression, context);
                var jInnerType = context.GetTypeInfo(jInnerArg0.Expression).Type;
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
                     + $".filter({jInnerP} -> Objects.equals({jOuterKey}, {jInnerKey}))"
                     + $".map({jInnerP} -> {jMapBody}))";
            }

            // GroupJoin(inner, outerKey, innerKey, resultSelector) → map outer to (outer, groupList) pairs
            if (originalMethodName == "GroupJoin" && node.ArgumentList.Arguments.Count >= 4)
            {
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.ArrayList");
                context.AddImport("java.util.Objects");
                var gjInnerArg0 = node.ArgumentList.Arguments[0];
                var gjInner = facade.Transform(gjInnerArg0.Expression, context);
                var gjInnerType = context.GetTypeInfo(gjInnerArg0.Expression).Type;
                var gjInnerStream = ExpressionTransformerHelpers.BuildStreamExpression(gjInner, gjInnerType, context);
                TryGetSingleParamLambda(node.ArgumentList.Arguments[1].Expression, context, facade, out var gjOuterP, out var gjOuterKey);
                TryGetSingleParamLambda(node.ArgumentList.Arguments[2].Expression, context, facade, out var gjInnerP, out var gjInnerKey);
                TryGetTwoParamLambda(node.ArgumentList.Arguments[3].Expression, context, facade, out var gjResP0, out var gjResP1, out var gjResBody);
                // Resolve result body outer param name vs actual outer param
                string prebind = gjResP0 != gjOuterP ? $"var {gjResP0} = {gjOuterP}; " : "";
                string gjGroupExpr = $"{gjInnerStream}.filter({gjInnerP} -> Objects.equals({gjOuterKey}, {gjInnerKey})).collect(Collectors.toCollection(() -> new ArrayList<>()))";
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

        if (originalMethodName == "ToString"
            && methodName == "substring"
            && node.ArgumentList.Arguments.Count - argStartIndex == 2
            && IsSystemTextStringBuilder(context.GetTypeInfo(memberAccess.Expression).Type))
        {
            var start = facade.Transform(node.ArgumentList.Arguments[argStartIndex].Expression, context);
            var length = facade.Transform(node.ArgumentList.Arguments[argStartIndex + 1].Expression, context);
            return $"{receiver}.substring({start}, {start} + {length})";
        }

        if (originalMethodName == "Substring"
            && methodName == "substring"
            && node.ArgumentList.Arguments.Count - argStartIndex == 2
            && IsSystemStringMethod(methodSymbol, memberAccess.Expression, context))
        {
            var start = facade.Transform(node.ArgumentList.Arguments[argStartIndex].Expression, context);
            var length = facade.Transform(node.ArgumentList.Arguments[argStartIndex + 1].Expression, context);
            return $"{receiver}.substring({start}, {start} + {length})";
        }

        // Custom collection types can define a parameterless ToArray(). Do not apply Java Stream
        // generator arguments unless the method is actually LINQ Enumerable.ToArray().
        if (originalMethodName == "ToArray"
            && node.ArgumentList.Arguments.Count - argStartIndex == 0
            && methodSymbol?.ContainingType?.ToDisplayString() is not "System.Linq.Enumerable")
        {
            return $"{receiver}.{methodName}()";
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

        if (originalMethodName == "Parse"
            && !methodName.StartsWith("MathHelper.", StringComparison.Ordinal)
            && TryGetParseHelperMethod(memberAccess.Expression, context, out var parseHelper))
        {
            methodName = parseHelper;
        }

        if (originalMethodName == "Parse"
            && methodName.StartsWith("MathHelper.", StringComparison.Ordinal)
            && node.ArgumentList.Arguments.Count - argStartIndex >= 2)
        {
            int lastArgIdx = node.ArgumentList.Arguments.Count - 1;
            if (HasIFormatProviderOrNumberStylesArg(node.ArgumentList.Arguments[lastArgIdx].Expression, context))
            {
                parseStripCount = 1;
                if (node.ArgumentList.Arguments.Count - argStartIndex >= 3
                    && HasIFormatProviderOrNumberStylesArg(node.ArgumentList.Arguments[lastArgIdx - 1].Expression, context))
                    parseStripCount = 2;
            }
        }

        if (originalMethodName == "Parse"
            && methodName.StartsWith("MathHelper.", StringComparison.Ordinal)
            && node.ArgumentList.Arguments.Count - argStartIndex == 2
            && IsNumberStylesArgument(node.ArgumentList.Arguments[argStartIndex + 1].Expression, context))
        {
            var valueArg = facade.Transform(node.ArgumentList.Arguments[argStartIndex].Expression, context);
            var styleArg = facade.Transform(node.ArgumentList.Arguments[argStartIndex + 1].Expression, context);
            return $"{methodName}({valueArg}, {styleArg})";
        }

        var args = parseStripCount > 0
            ? ArgumentTransformer.TransformArgumentList(
                node.ArgumentList, context, facade, argStartIndex, methodSymbol,
                maxArgCount: node.ArgumentList.Arguments.Count - parseStripCount)
            : ArgumentTransformer.TransformArgumentList(
                node.ArgumentList, context, facade, argStartIndex, methodSymbol);

        args = PrependClassTypeTokens(args,
            methodSymbol != null ? GetClassTypeTokensForCall(methodSymbol, context) : null);

        if (originalMethodName == "Parse" && methodName.StartsWith("MathHelper.", StringComparison.Ordinal))
            return $"{methodName}({args})";

        if (methodName.StartsWith("Encoding.", StringComparison.Ordinal)
            || methodName.StartsWith("Regex.", StringComparison.Ordinal)
            || methodName.StartsWith("DrawingColor.", StringComparison.Ordinal))
            return $"{methodName}({args})";

        if (originalMethodName == "Parse"
            && methodName is "parseInt" or "parseLong" or "parseDouble" or "parseFloat"
            && TryGetParseHelperMethod(memberAccess.Expression, context, out var lateParseHelper))
        {
            return $"{lateParseHelper}({args})";
        }

        // List<Integer>.remove(int) 歧义修复：C# Remove(int item) 按值删除，
        // Java remove(int) 按索引删除。需要包装为 Integer.valueOf() 以调用 remove(Object)。
        if (originalMethodName == "Remove"
            && methodName == "remove"
            && node.ArgumentList.Arguments.Count == 1
            && methodSymbol is { Parameters.Length: 1 }
            && methodSymbol.Parameters[0].Type.SpecialType == SpecialType.System_Int32
            && IsListOfBoxedInt(methodSymbol.ContainingType))
        {
            args = $"Integer.valueOf({args})";
        }

        if (methodName == "toList" && string.IsNullOrEmpty(args))
        {
            context.AddImport("java.util.stream.StreamSupport");
            return $"StreamSupport.stream({receiver}.spliterator(), false).toList()";
        }

        if (methodName is "filter" or "map" or "flatMap"
            && context.SemanticModel != null)
        {
            var fallbackStreamType = context.GetTypeInfo(memberAccess.Expression).Type;
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
            // When the original C# method is an instance method, the receiver must be
            // passed as the first argument to the static helper method.
            bool isInstanceCallSafetyNet = methodSymbol is { IsStatic: false }
                || (methodSymbol == null && !LooksLikeTypeReceiver(memberAccess.Expression, context));
            if (isInstanceCallSafetyNet)
            {
                args = string.IsNullOrEmpty(args) ? receiver : $"{receiver}, {args}";
            }

            if (methodName == "EnumHelper.tryParse" && methodSymbol != null
                && methodSymbol.TypeArguments.Length > 0)
            {
                var classLiteral = ConversionContext.GetClassLiteral(methodSymbol.TypeArguments[0], context);
                args = string.IsNullOrEmpty(args) ? classLiteral : $"{args}, {classLiteral}";
            }
            return $"{methodName}({args})";
        }

        // User-defined extension method static lowering:
        // Rewrite receiver.method(args) → HostClass.method(receiver, args)
        // Uses ReducedFrom to get the original unreduced method symbol and its containing type.
        if (isExtensionInStaticPath
            && methodSymbol is { IsExtensionMethod: true, MethodKind: MethodKind.ReducedExtension })
        {
            var originalMethod = methodSymbol.ReducedFrom ?? methodSymbol;
            var hostType = originalMethod.ContainingType;
            var hostTypeName = hostType.Name;

            // Add import for the host class
            var hostNs = hostType.ContainingNamespace?.ToDisplayString();
            if (hostNs is not null and not "<global namespace>" and not "")
            {
                var hostPackage = context.NamespaceToPackage(hostNs);
                if (!string.IsNullOrEmpty(hostPackage))
                    context.AddImport($"{hostPackage}.{hostType.Name}");
            }

            // Transform arguments using the *reduced* method symbol (whose parameters match
            // the call-site arguments — receiver is NOT in node.ArgumentList for ReducedExtension).
            var extensionArgs = ArgumentTransformer.TransformArgumentList(
                node.ArgumentList, context, facade, 0, methodSymbol);
            var allArgs = string.IsNullOrEmpty(extensionArgs)
                ? receiver
                : $"{receiver}, {extensionArgs}";
            return $"{hostTypeName}.{methodName}({allArgs})";
        }

        return $"{receiver}.{methodName}({args})";
    }

    /// <summary>
    /// Transforms the argument list for String.Split(), converting char literal arguments
    /// <summary>
    /// Returns true when any argument starting at <paramref name="argStartIndex"/>
    /// is a character literal.
    /// </summary>
    private static bool HasCharLiteralArgument(ArgumentListSyntax argumentList, int argStartIndex)
    {
        for (int i = argStartIndex; i < argumentList.Arguments.Count; i++)
        {
            if (argumentList.Arguments[i].Expression is LiteralExpressionSyntax lit
                && lit.IsKind(SyntaxKind.CharacterLiteralExpression))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Converts char literal arguments (e.g. ' ', '.') in a C# String.Split call
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
            var typeInfo = context.GetTypeInfo(firstArg);
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

        // Syntactic fallback: check for common CultureInfo patterns.
        // Unwrap cast expressions like (IFormatProvider) CultureInfo.InvariantCulture
        var argExpr = firstArg;
        if (argExpr is CastExpressionSyntax castExpr)
        {
            var castTypeName = castExpr.Type.ToString();
            if (castTypeName is "IFormatProvider" or "System.IFormatProvider")
                return true;
            argExpr = castExpr.Expression;
        }
        var argText = argExpr.ToString();
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
            var typeInfo = context.GetTypeInfo(expr);
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

        // Syntactic fallback — unwrap cast expressions
        var innerExpr = expr;
        if (innerExpr is CastExpressionSyntax castExpr)
        {
            var castTypeName = castExpr.Type.ToString();
            if (castTypeName is "IFormatProvider" or "System.IFormatProvider")
                return true;
            innerExpr = castExpr.Expression;
        }
        var argText = innerExpr.ToString();
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

        var regexText = ch.Length == 1 && "\\^-]".Contains(ch[0])
            ? "\\" + ch
            : ch;

        return StringEscapeHelper.EscapeJavaString(regexText);
    }

    /// <summary>
    /// Escapes a single character for use as a literal pattern in Java's String.split() regex.
    /// Regex metacharacters are prefixed with a backslash so they match literally.
    /// </summary>
    private static string EscapeRegexChar(string ch)
    {
        if (ch.Length == 1 && @"\.^$*+?{}[]|()".Contains(ch[0]))
            return StringEscapeHelper.EscapeJavaString(@"\" + ch);
        return StringEscapeHelper.EscapeJavaString(ch);
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
        return $"\"{StringEscapeHelper.EscapeJavaString(result)}\"";
    }

    /// <summary>
    /// Checks if the given expression is itself a LINQ extension method call (from System.Linq.Enumerable).
    /// Used to avoid injecting .stream() twice in a method chain.
    /// </summary>
    private static bool IsReceiverLinqExtension(ExpressionSyntax expr, ConversionContext context)
    {
        if (context.SemanticModel == null) return false;
        if (expr is not InvocationExpressionSyntax invocation) return false;
        var sym = context.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
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
            || receiver.Contains("IntStream.range(", StringComparison.Ordinal)
            || receiver.Contains("StreamSupport.stream(", StringComparison.Ordinal)
            // Zip / indexed Select / indexed Where use collectingAndThen to produce a
            // stream via IntStream.range().mapToObj(); the outer result IS a stream.
            || receiver.Contains(".collect(Collectors.collectingAndThen(", StringComparison.Ordinal);
    }

    /// <summary>
    /// Transforms Stream.toArray() to produce a correctly-typed array instead of Object[].
    /// For primitive element types (int, long, double): uses mapToXxx().toArray() → primitive array.
    /// For reference types: uses .toArray(TypeName[]::new) → typed array.
    /// Falls back to .toArray() when element type cannot be determined.
    /// </summary>
    private static bool TryTransformArrayListToArrayType(
        string receiver,
        IMethodSymbol? methodSymbol,
        InvocationExpressionSyntax node,
        MemberAccessExpressionSyntax memberAccess,
        ConversionContext context,
        out string transformed)
    {
        transformed = string.Empty;

        var receiverTypeName = context.GetTypeInfo(memberAccess.Expression).Type?.ToDisplayString();
        if (methodSymbol?.ContainingType?.ToDisplayString() != "System.Collections.ArrayList"
            && receiverTypeName != "System.Collections.ArrayList")
        {
            return false;
        }

        if (node.ArgumentList.Arguments[0].Expression is not TypeOfExpressionSyntax typeOfExpression)
            return false;

        var elementType = context.GetTypeInfo(typeOfExpression.Type).Type;
        if (elementType == null)
            return false;

        if (elementType.IsValueType && elementType is not IArrayTypeSymbol)
            return false;

        var arrayCreation = ExpressionTransformerHelpers.BuildJavaArrayCreationForElement(
            elementType,
            context,
            "0");
        transformed = $"{receiver}.toArray({arrayCreation})";
        return true;
    }

    private static string TransformToArrayWithElementType(
        string receiver,
        IMethodSymbol? methodSymbol,
        InvocationExpressionSyntax node,
        MemberAccessExpressionSyntax memberAccess,
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
            var typeInfo3 = context.GetTypeInfo(node);
            if (typeInfo3.ConvertedType is IArrayTypeSymbol targetArray)
                elementType = targetArray.ElementType;
            else if (typeInfo3.Type is IArrayTypeSymbol typeArray)
                elementType = typeArray.ElementType;
        }

        // Method 4: From the receiver expression's IEnumerable<T> type (when methodSymbol is null)
        if (elementType == null && context.SemanticModel != null)
        {
            var receiverTypeInfo = context.GetTypeInfo(memberAccess.Expression);
            if (receiverTypeInfo.Type is INamedTypeSymbol receiverType4)
                elementType = ExtractEnumerableElementType(receiverType4);
            else if (receiverTypeInfo.ConvertedType is INamedTypeSymbol receiverConverted4)
                elementType = ExtractEnumerableElementType(receiverConverted4);
        }

        if (elementType == null)
            return $"{receiver}.toArray()";

        // Primitive types: use mapToInt/mapToLong/mapToDouble + toArray() → returns int[]/long[]/double[]
        // If the receiver is already the matching primitive stream, just .toArray() directly.
        var specialType = elementType.SpecialType;
        var toArrTargetCat = PrimitiveStreamCategory(specialType);
        if (toArrTargetCat != "")
        {
            var toArrReceiverCat = DetectReceiverPrimitiveStreamCategory(memberAccess, receiver, context);
            if (toArrReceiverCat == toArrTargetCat)
                return $"{receiver}.toArray()";
        }
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
        // Guard against LINQ type parameter names (TSource, TKey, etc.) leaking
        // from unresolved generic methods into the generated Java.
        if (javaType is "TSource" or "TResult" or "TKey" or "TElement"
            or "TFirst" or "TSecond" or "TAccumulate")
            javaType = "Object";
        if (elementType.TypeKind == TypeKind.TypeParameter)
        {
            if (TryBuildTypeParameterArrayGenerator(elementType, javaType, context, out var generator))
                return $"{receiver}.toArray({generator})";

            return $"{receiver}.toArray(size -> ({javaType}[]) new Object[size])";
        }
        if (!string.IsNullOrEmpty(javaType) && javaType != "Object")
        {
            // Java cannot create generic arrays (e.g. SimpleEntry<String,Integer>[]::new is illegal).
            // Strip type parameters to use the raw type in the array constructor reference.
            var arrayTypeRef = javaType.Contains('<') ? javaType.Substring(0, javaType.IndexOf('<')) : javaType;
            return $"{receiver}.toArray({arrayTypeRef}[]::new)";
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
            var javaTypeParam = context.MapType(elementType);
            if (TryBuildTypeParameterArrayGenerator(elementType, javaTypeParam, context, out var generator))
                return $"{receiver}.stream().toArray({generator})";

            return $"{receiver}.stream().toArray(size -> ({javaTypeParam}[]) new Object[size])";
        }
        var javaType = context.MapType(elementType);
        if (!string.IsNullOrEmpty(javaType) && javaType != "Object")
        {
            // Strip generic type parameters — Java cannot create generic arrays.
            var arrayTypeRef = javaType.Contains('<') ? javaType.Substring(0, javaType.IndexOf('<')) : javaType;
            return $"{receiver}.toArray({arrayTypeRef}[]::new)";
        }

        return $"{receiver}.toArray()";
    }

    private static bool TryBuildTypeParameterArrayGenerator(
        ITypeSymbol elementType,
        string javaType,
        ConversionContext context,
        out string generator)
    {
        generator = "";
        if (elementType is not ITypeParameterSymbol typeParameter)
            return false;

        if (!context.TryGetRuntimeClassParameter(typeParameter.Name, out var runtimeClassParameter))
            return false;

        generator = $"size -> ({javaType}[]) java.lang.reflect.Array.newInstance({runtimeClassParameter}, size)";
        return true;
    }

    private static bool TryTransformArrayToArrayCopy(
        string receiver,
        IMethodSymbol? methodSymbol,
        InvocationExpressionSyntax node,
        MemberAccessExpressionSyntax memberAccess,
        ConversionContext context,
        out string result)
    {
        result = "";

        if (context.SemanticModel == null)
            return false;

        var receiverType = context.GetTypeInfo(memberAccess.Expression).Type;
        if (receiverType is IArrayTypeSymbol receiverArray)
        {
            var sourceIsGenericArrayReturn = ExpressionTransformerHelpers.IsExpressionFromTypeParameterArrayReturn(
                memberAccess.Expression,
                context,
                requireActualTypeParam: false);
            var factoryElement = sourceIsGenericArrayReturn
                ? ResolveTargetArrayElementType(node, context)
                : null;
            result = BuildArrayCopyExpression(receiver, receiverArray.ElementType, factoryElement, context);
            return true;
        }
        return false;
    }

    private static string BuildArrayCopyExpression(
        string receiver,
        ITypeSymbol elementType,
        ITypeSymbol? factoryElementType,
        ConversionContext context)
    {
        context.AddImport("io.github.ningpp.compat.ArrayHelper");

        if (TryGetPrimitiveArrayHelper(elementType.SpecialType, out var primitiveHelper))
            return factoryElementType == null || primitiveHelper == "copyArray"
                ? $"ArrayHelper.copyArray({receiver})"
                : $"ArrayHelper.{primitiveHelper}({receiver})";

        var javaType = context.MapType(factoryElementType ?? elementType);
        if (string.IsNullOrEmpty(javaType) || javaType == "Object")
            return $"ArrayHelper.copyArray({receiver})";

        var arrayTypeRef = javaType.Contains('<') ? javaType[..javaType.IndexOf('<')] : javaType;
        if (StructCloneHelper.IsUserDefinedStruct(elementType))
            return $"ArrayHelper.copyStructArray({receiver}, {arrayTypeRef}[]::new, item -> item.clone())";

        return factoryElementType == null
            ? $"ArrayHelper.copyArray({receiver})"
            : $"ArrayHelper.copyArray({receiver}, {arrayTypeRef}[]::new)";
    }

    private static ITypeSymbol? ResolveTargetArrayElementType(InvocationExpressionSyntax node, ConversionContext context)
    {
        if (context.SemanticModel == null)
            return null;

        var typeInfo = context.GetTypeInfo(node);
        return (typeInfo.ConvertedType as IArrayTypeSymbol)?.ElementType
            ?? (typeInfo.Type as IArrayTypeSymbol)?.ElementType;
    }

    private static bool TryGetPrimitiveArrayHelper(SpecialType specialType, out string helper)
    {
        helper = specialType switch
        {
            SpecialType.System_Int32 => "toIntArray",
            SpecialType.System_Int64 => "toLongArray",
            SpecialType.System_Double => "toDoubleArray",
            SpecialType.System_Single => "toFloatArray",
            SpecialType.System_Boolean => "toBooleanArray",
            SpecialType.System_Byte => "toByteArray",
            SpecialType.System_Int16 => "toShortArray",
            SpecialType.System_Char => "toCharArray",
            _ => ""
        };
        return helper.Length > 0;
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

    /// <summary>
    /// Returns true if the type is a Collection-compatible type in Java:
    /// ICollection&lt;T&gt;, IList&lt;T&gt;, List&lt;T&gt;, or any type implementing ICollection&lt;T&gt;.
    /// These types map to java.util.Collection or its sub-interfaces, so addAll() works on them.
    /// IEnumerable-only types (e.g. LINQ chain results, Cast&lt;T&gt;()) produce Streams in Java
    /// and are NOT Collection-compatible.
    /// </summary>
    private static bool IsCollectionCompatibleType(ITypeSymbol type)
    {
        var display = type.OriginalDefinition.ToDisplayString();
        if (display is "System.Collections.Generic.ICollection<T>"
                    or "System.Collections.Generic.IList<T>"
                    or "System.Collections.Generic.List<T>"
                    or "System.Collections.Generic.ISet<T>"
                    or "System.Collections.Generic.HashSet<T>"
                    or "System.Collections.Generic.SortedSet<T>"
                    or "System.Collections.Generic.LinkedList<T>")
            return true;

        return type.AllInterfaces.Any(i =>
            i.OriginalDefinition.ToDisplayString() is
                "System.Collections.Generic.ICollection<T>" or
                "System.Collections.Generic.IList<T>");
    }

    /// <summary>
    /// 判断类型是否为 List&lt;int&gt; / IList&lt;int&gt; / ICollection&lt;int&gt; 等
    /// 元素为 int 的集合，用于检测 remove(int) 歧义。
    /// </summary>
    private static bool IsListOfBoxedInt(INamedTypeSymbol? containingType)
    {
        if (containingType == null) return false;
        return HasIntElementType(containingType)
            || containingType.AllInterfaces.Any(HasIntElementType);

        static bool HasIntElementType(INamedTypeSymbol t) =>
            t.TypeArguments.Length == 1
            && t.TypeArguments[0].SpecialType == SpecialType.System_Int32
            && t.Name is "List" or "IList" or "ICollection" or "Collection";
    }

    private static bool LooksLikeMaterializedCollectionExpression(string receiverExpr)
    {
        if (string.IsNullOrWhiteSpace(receiverExpr))
            return false;

        // collectingAndThen produces a stream (the finisher returns a stream), not a collection.
        // Must NOT be detected as materialized.
        if (receiverExpr.Contains(".collect(Collectors.collectingAndThen(", StringComparison.Ordinal))
            return false;

        return receiverExpr.Contains(".collect(Collectors.toCollection(() -> new ArrayList<>()))", StringComparison.Ordinal)
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

    private static string GetTryGetValueOutDefault(ExpressionSyntax outExpression, ConversionContext context)
    {
        var typeSymbol = ResolveTryGetValueOutType(outExpression, context);
        if (typeSymbol == null)
            return "null";

        var javaType = context.MapType(typeSymbol);
        if (string.IsNullOrWhiteSpace(javaType))
            return "null";

        return GetDefaultValueForMappedType(javaType, typeSymbol, context);
    }

    private static ITypeSymbol? ResolveTryGetValueOutType(ExpressionSyntax outExpression, ConversionContext context)
    {
        if (context.SemanticModel == null)
            return null;

        if (outExpression is DeclarationExpressionSyntax decl)
        {
            var declType = context.GetTypeInfo(decl.Type).Type
                ?? context.GetTypeInfo(decl).Type;
            if (declType != null)
                return declType;
        }

        var typeInfo = context.GetTypeInfo(outExpression);
        return typeInfo.Type ?? typeInfo.ConvertedType;
    }

    private static string GetDefaultValueForMappedType(string javaType, ITypeSymbol typeSymbol, ConversionContext context)
    {
        if (typeSymbol is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
            return "null";

        return javaType switch
        {
            "int" or "Integer" => "0",
            "long" or "Long" => "0L",
            "short" or "Short" => "(short)0",
            "byte" or "Byte" => "(byte)0",
            "float" or "Float" => "0.0f",
            "double" or "Double" => "0.0",
            "boolean" or "Boolean" => "false",
            "char" or "Character" => "'\\0'",
            _ => GetNonPrimitiveDefaultValue(javaType, typeSymbol, context)
        };
    }

    private static string GetNonPrimitiveDefaultValue(string javaType, ITypeSymbol typeSymbol, ConversionContext context)
    {
        if (typeSymbol is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType
            && !context.IsFlagsEnum(enumType.Name)
            && !context.IsFlagsEnum(enumType.ToDisplayString()))
        {
            return $"{javaType}.values()[0]";
        }

        if (typeSymbol is INamedTypeSymbol { TypeKind: TypeKind.Struct } namedStruct
            && namedStruct.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T)
        {
            return $"new {javaType}()";
        }

        return "null";
    }

    /// <summary>
    /// 根据委托签名推断 Java 函数式接口的 SAM 方法名。
    /// 用于 TypeMappings 没有显式映射时的回退。
    /// Delegates to DelegateTransformer.InferSamMethodName to keep declaration and call sites in sync.
    /// </summary>
    private static string InferSamMethodName(IMethodSymbol delegateInvoke) =>
        Type.DelegateTransformer.InferSamMethodName(delegateInvoke.ReturnsVoid, delegateInvoke.Parameters.Length);

    private static string InferSamMethodName(bool returnsVoid, int parameterCount) =>
        Type.DelegateTransformer.InferSamMethodName(returnsVoid, parameterCount);

    private static bool IsReceiverOfType(ExpressionSyntax receiver, string typeName, ConversionContext context)
    {
        var typeInfo = context.GetTypeInfo(receiver);
        return typeInfo.Type?.ToDisplayString() == typeName
            || typeInfo.ConvertedType?.ToDisplayString() == typeName;
    }

    private static bool IsMethodInfoReceiver(ExpressionSyntax expr, ConversionContext context)
    {
        // Check via the semantic symbol first
        if (context.GetSymbolInfo(expr).Symbol is IMethodSymbol ms
            && ms.ContainingType.ToDisplayString() == "System.Reflection.MethodInfo")
            return true;
        // Fall back to type info on the receiver expression
        return IsReceiverOfType(expr, "System.Reflection.MethodInfo", context);
    }

    private static string ResolveDelegateTypeArg(TypeSyntax typeSyntax, ConversionContext context)
    {
        var typeInfo = context.GetTypeInfo(typeSyntax);
        if (typeInfo.Type != null)
        {
            var mapped = context.MapType(typeInfo.Type);
            return ExpressionTransformerHelpers.ToRuntimeTypeForClassLiteral(mapped);
        }
        var mappedFromSyntax = context.MapTypeFromSyntax(typeSyntax);
        return ExpressionTransformerHelpers.ToRuntimeTypeForClassLiteral(mappedFromSyntax);
    }

    /// <summary>
    /// Returns the primitive-stream category ("int", "long", "double") for a C# SpecialType,
    /// or empty string if the type doesn't map to a Java primitive stream.
    /// </summary>
    private static string PrimitiveStreamCategory(SpecialType spec) => spec switch
    {
        SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Byte
            or SpecialType.System_UInt32 or SpecialType.System_UInt16 or SpecialType.System_SByte => "int",
        SpecialType.System_Int64 or SpecialType.System_UInt64 => "long",
        SpecialType.System_Double or SpecialType.System_Single or SpecialType.System_Decimal => "double",
        _ => ""
    };

    /// <summary>
    /// Detects whether the Java receiver is a primitive stream (IntStream, LongStream, DoubleStream)
    /// and returns its category ("int", "long", "double"), or "" if it's an object Stream&lt;T&gt;.
    /// Checks both the direct C# array element type and the generated Java receiver string
    /// (for chained LINQ where the C# type is IEnumerable&lt;T&gt;).
    /// </summary>
    private static string DetectReceiverPrimitiveStreamCategory(
        MemberAccessExpressionSyntax memberAccess,
        string receiver,
        ConversionContext context)
    {
        var csType = context.GetTypeInfo(memberAccess.Expression).Type;

        // Direct receiver is a primitive array → Arrays.stream produces matching primitive stream
        if (csType is IArrayTypeSymbol arr)
        {
            var cat = PrimitiveStreamCategory(arr.ElementType.SpecialType);
            if (cat != "") return cat;
        }

        // Chained LINQ: C# type is IEnumerable<T>, but Java receiver is still a primitive stream.
        // Verify the element type IS a primitive and the receiver hasn't been boxed.
        if (csType is INamedTypeSymbol named
            && named.TypeArguments.Length > 0
            && PrimitiveStreamCategory(named.TypeArguments[0].SpecialType) != ""
            && !receiver.Contains(".boxed()", StringComparison.Ordinal)
            && !receiver.Contains(".mapToObj(", StringComparison.Ordinal))
        {
            if (receiver.StartsWith("Arrays.stream(", StringComparison.Ordinal))
            {
                // Arrays.stream(refArray) produces Stream<T> (reference), not a primitive stream.
                // Walk back through chained LINQ calls to find the original source expression
                // and verify it's actually a primitive array.
                var srcExpr = memberAccess.Expression;
                while (srcExpr is InvocationExpressionSyntax chainedInv
                    && chainedInv.Expression is MemberAccessExpressionSyntax innerMa)
                    srcExpr = innerMa.Expression;
                var srcTypeInfo = context.GetTypeInfo(srcExpr).Type;
                if (srcTypeInfo is IArrayTypeSymbol srcArr
                    && PrimitiveStreamCategory(srcArr.ElementType.SpecialType) != "")
                {
                    return PrimitiveStreamCategory(named.TypeArguments[0].SpecialType);
                }
            }
            else if (receiver.Contains("IntStream.range(", StringComparison.Ordinal)
                || receiver.Contains("IntStream.rangeClosed(", StringComparison.Ordinal))
            {
                return PrimitiveStreamCategory(named.TypeArguments[0].SpecialType);
            }
        }

        return "";
    }

    /// <summary>
    /// Determines whether the current LINQ receiver represents a Java primitive stream
    /// (IntStream, DoubleStream, LongStream) so that parameterless min()/max() can be used.
    /// Checks both the C# receiver type (primitive array → Arrays.stream produces primitive stream)
    /// and the Java receiver string (for chained LINQ where the C# type is IEnumerable&lt;T&gt;).
    /// </summary>
    private static bool IsPrimitiveStreamContext(
        SpecialType returnSpecialType,
        string receiver,
        MemberAccessExpressionSyntax memberAccess,
        ConversionContext context)
    {
        if (PrimitiveStreamCategory(returnSpecialType) == "")
            return false;

        // Direct receiver is a primitive array → Arrays.stream(int[]) produces IntStream
        var csType = context.GetTypeInfo(memberAccess.Expression).Type;
        if (csType is IArrayTypeSymbol arr && PrimitiveStreamCategory(arr.ElementType.SpecialType) != "")
            return true;

        // Chained LINQ (e.g. arr.Select(...).Max()): receiver type is IEnumerable<T>,
        // but the Java string reveals it originated from a primitive stream source.
        // Exclude .boxed() and .mapToObj() which convert back to boxed Stream<T>.
        // Also exclude reference-type element sources — Arrays.stream(T[]) for reference T
        // produces Stream<T>, not a primitive stream.
        if (csType is INamedTypeSymbol named
            && named.TypeArguments.Length > 0
            && PrimitiveStreamCategory(named.TypeArguments[0].SpecialType) == "")
        {
            return false;
        }
        if ((receiver.Contains("IntStream.range(", StringComparison.Ordinal)
                || receiver.Contains("IntStream.rangeClosed(", StringComparison.Ordinal))
            && !receiver.Contains(".boxed()", StringComparison.Ordinal)
            && !receiver.Contains(".mapToObj(", StringComparison.Ordinal))
        {
            return true;
        }

        // Arrays.stream(): verify the source is actually a primitive array.
        if (receiver.StartsWith("Arrays.stream(", StringComparison.Ordinal)
            && !receiver.Contains(".boxed()", StringComparison.Ordinal)
            && !receiver.Contains(".mapToObj(", StringComparison.Ordinal))
        {
            var srcExpr = memberAccess.Expression;
            while (srcExpr is InvocationExpressionSyntax chainedInv
                && chainedInv.Expression is MemberAccessExpressionSyntax innerMa)
                srcExpr = innerMa.Expression;
            var srcTypeInfo = context.GetTypeInfo(srcExpr).Type;
            if (srcTypeInfo is IArrayTypeSymbol srcArr
                && PrimitiveStreamCategory(srcArr.ElementType.SpecialType) != "")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the appropriate mapToXxx operation for a Min/Max selector that returns a primitive type.
    /// Handles the Java API constraint that IntStream.mapToInt / DoubleStream.mapToDouble /
    /// LongStream.mapToLong don't exist — uses "map" instead when the receiver is already the
    /// matching primitive stream type.
    /// Returns null when the return type is non-primitive (use regular .map() with Comparator).
    /// </summary>
    private static string? GetPrimitiveMapOperation(
        SpecialType returnSpecialType,
        MemberAccessExpressionSyntax memberAccess,
        string receiver,
        ConversionContext context)
    {
        var category = PrimitiveStreamCategory(returnSpecialType);
        if (category == "")
            return null;

        string mapOp = category switch
        {
            "int" => "mapToInt",
            "long" => "mapToLong",
            "double" => "mapToDouble",
            _ => "map"
        };

        // If the receiver is already the matching primitive stream, use map() instead.
        // (IntStream.mapToInt / DoubleStream.mapToDouble / LongStream.mapToLong don't exist.)
        var receiverCat = DetectReceiverPrimitiveStreamCategory(memberAccess, receiver, context);
        if (receiverCat == category)
            mapOp = "map";

        return mapOp;
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

    /// <summary>
    /// Maps C# built-in primitive static method names to their Java equivalents.
    /// e.g. double.IsInfinity → Double.isInfinite, int.Parse → Integer.parseInt
    /// </summary>
    private static string MapPrimitiveStaticMethodName(string primitiveKeyword, string methodName)
        => (primitiveKeyword, methodName) switch
        {
            // char-specific mappings: C# and Java Character class have different method names
            ("char", "IsLower")          => "isLowerCase",
            ("char", "IsUpper")          => "isUpperCase",
            ("char", "IsWhiteSpace")     => "isWhitespace",
            ("char", "IsControl")        => "isISOControl",
            ("char", "IsSurrogate")      => "isSurrogate",
            ("char", "IsHighSurrogate")  => "isHighSurrogate",
            ("char", "IsLowSurrogate")   => "isLowSurrogate",
            ("char", "IsSurrogatePair")  => "isSurrogatePair",
            ("char", "GetNumericValue")  => "getNumericValue",
            ("char", "ToLower")          => "toLowerCase",
            ("char", "ToLowerInvariant") => "toLowerCase",
            ("char", "ToUpper")          => "toUpperCase",
            ("char", "ToUpperInvariant") => "toUpperCase",
            ("char", "ToString")         => "toString",
            ("char", "ConvertFromUtf32") => "toChars",
            ("char", "ConvertToUtf32")   => "toCodePoint",
            ("char", "Parse")            => "valueOf",
            // generic mappings shared across all primitive types
            (_, "IsInfinity" or "IsPositiveInfinity" or "IsNegativeInfinity") => "isInfinite",
            (_, "IsNaN")    => "isNaN",
            (_, "IsFinite") => "isFinite",
            (_, "Parse")    => primitiveKeyword switch
            {
                "int"    => "parseInt",
                "long"   => "parseLong",
                "double" => "parseDouble",
                "float"  => "parseFloat",
                "short"  => "parseShort",
                "byte"   => "parseByte",
                _        => "parse" + char.ToUpperInvariant(primitiveKeyword[0]) + primitiveKeyword[1..]
            },
            _ when methodName.Length > 0 => char.ToLowerInvariant(methodName[0]) + methodName[1..],
            _ => methodName
        };

    private static string? MapPrimitiveParseHelper(string primitiveKeyword)
        => primitiveKeyword switch
        {
            "int" => "MathHelper.parseInt",
            "long" => "MathHelper.parseLong",
            "double" => "MathHelper.parseDouble",
            "float" => "MathHelper.parseFloat",
            "decimal" => "Decimal.parse",
            "uint" => "Integer.parseUnsignedInt",
            "ulong" => "Long.parseUnsignedLong",
            "ushort" => "Integer.parseUnsignedInt",
            _ => null
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
            SpecialType.System_Byte    => "Integer",
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
    /// Maps a C# primitive type's SpecialType to its keyword form (e.g. System.Char → "char").
    /// Returns true when the type is a recognized C# primitive.
    /// </summary>
    private static bool TryGetPrimitiveKeyword(ITypeSymbol type, out string keyword)
    {
        keyword = type.SpecialType switch
        {
            SpecialType.System_Int32   => "int",
            SpecialType.System_Int64   => "long",
            SpecialType.System_Int16   => "short",
            SpecialType.System_Byte    => "byte",
            SpecialType.System_SByte   => "byte",
            SpecialType.System_UInt32  => "uint",
            SpecialType.System_UInt64  => "ulong",
            SpecialType.System_UInt16  => "ushort",
            SpecialType.System_Single  => "float",
            SpecialType.System_Double  => "double",
            SpecialType.System_Char    => "char",
            SpecialType.System_Boolean => "bool",
            SpecialType.System_Decimal => "decimal",
            _ => string.Empty
        };
        return keyword.Length > 0;
    }

    private static string GetFlagsEnumValueType(INamedTypeSymbol enumType, ConversionContext context)
    {
        if (context.IsFlagsEnum(enumType.Name))
            return context.GetFlagsEnumValueType(enumType.Name);
        if (context.IsFlagsEnum(enumType.ToDisplayString()))
            return context.GetFlagsEnumValueType(enumType.ToDisplayString());

        return enumType.EnumUnderlyingType?.SpecialType is SpecialType.System_Int64 or SpecialType.System_UInt64
            ? "long"
            : "int";
    }

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
        ConversionContext context,
        string originalMethodName,
        IMethodSymbol? methodSymbol,
        out string mstestMethodName)
    {
        mstestMethodName = string.Empty;

        var isAssertReceiver = ExpressionTransformerHelpers.StaticReceiverMatches(
            receiverExpression,
            context,
            "Assert",
            "Microsoft.VisualStudio.TestTools.UnitTesting.Assert");
        var containingType = methodSymbol?.ContainingType.ToDisplayString();
        var isMSTestAssert = containingType == "Microsoft.VisualStudio.TestTools.UnitTesting.Assert";
        if (!isAssertReceiver && !isMSTestAssert)
        {
            return false;
        }

        mstestMethodName = originalMethodName switch
        {
            "AreEqual" => "areEqual",
            "AreNotEqual" => "areNotEqual",
            "AreSame" => "areSame",
            "AreNotSame" => "areNotSame",
            "IsTrue" => "isTrue",
            "IsFalse" => "isFalse",
            "IsNull" => "isNull",
            "IsNotNull" => "isNotNull",
            "Fail" => "fail",
            _ => string.Empty,
        };

        return !string.IsNullOrEmpty(mstestMethodName);
    }

    private static bool TryTransformDecimalStaticInvocation(
        InvocationExpressionSyntax node,
        MemberAccessExpressionSyntax memberAccess,
        string originalMethodName,
        ConversionContext context,
        ExpressionTransformerFacade facade,
        out string result)
    {
        result = string.Empty;

        if (!ExpressionTransformerHelpers.StaticReceiverMatches(
            memberAccess.Expression,
            context,
            "Decimal",
            "decimal",
            "System.Decimal"))
        {
            return false;
        }

        if (originalMethodName is not "Parse" and not "TryParse")
            return false;

        context.AddImport("io.github.ningpp.compat.Decimal");
        var args = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
        result = originalMethodName == "Parse"
            ? $"Decimal.parse({args})"
            : $"Decimal.tryParse({args})";
        return true;
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

    private static string BuildSetExprFromOther(string other, ITypeSymbol? otherType, ConversionContext context)
    {
        if (otherType is IArrayTypeSymbol arrType)
        {
            var streamExpr = ExpressionTransformerHelpers.BuildArrayStreamExpression(
                other, arrType, context, boxed: arrType.ElementType.IsValueType);
            return $"{streamExpr}.collect(Collectors.toSet())";
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
        var type = context.GetTypeInfo(expression).Type as INamedTypeSymbol;
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
        var method = context.GetSymbolInfo(node).Symbol as IMethodSymbol;
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

    private static bool IsExplicitEnumeratorGetEnumeratorInvocation(InvocationExpressionSyntax node, ConversionContext context)
    {
        var method = context.GetSymbolInfo(node).Symbol as IMethodSymbol;
        if (method == null || method.Name != "GetEnumerator" || method.Parameters.Length != 0)
            return false;

        return IsCSharpEnumeratorType(method.ReturnType);
    }

    private static string BuildIteratorExpressionForExplicitGetEnumerator(
        string receiver,
        ExpressionSyntax receiverSyntax,
        ConversionContext context)
    {
        var receiverType = context.GetTypeInfo(receiverSyntax).Type;
        if (receiverType is IArrayTypeSymbol arrayType)
        {
            context.AddImport("io.github.ningpp.compat.ArrayHelper");
            return $"ArrayHelper.toList({receiver}).iterator()";
        }

        return $"{receiver}.iterator()";
    }

    private static bool IsCSharpEnumeratorType(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        return IsCSharpEnumeratorNamedType(named);
    }

    private static bool IsCSharpEnumeratorNamedType(INamedTypeSymbol type)
    {
        static bool IsEnumerator(INamedTypeSymbol nt)
        {
            var ns = nt.ContainingNamespace?.ToDisplayString();
            return nt.Name == "IEnumerator"
                && (ns is "System.Collections" or "System.Collections.Generic");
        }

        if (IsEnumerator(type))
            return true;

        var original = type.OriginalDefinition;
        if (!SymbolEqualityComparer.Default.Equals(original, type) && IsEnumerator(original))
            return true;

        return type.AllInterfaces.Any(iface =>
            IsEnumerator(iface) || IsEnumerator(iface.OriginalDefinition));
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

    private static bool IsSystemTextStringBuilder(ITypeSymbol? typeSymbol)
    {
        return typeSymbol?.ToDisplayString() == "System.Text.StringBuilder";
    }

    private static bool IsFrameworkCollectionToArray(IMethodSymbol methodSymbol)
    {
        var ns = methodSymbol.ContainingType?.ContainingNamespace?.ToDisplayString();
        return ns != null
            && (ns == "System.Collections.Generic"
                || ns == "System.Collections"
                || ns == "System.Linq"
                || ns.StartsWith("System.Collections.", StringComparison.Ordinal));
    }

    private static bool IsSystemStringMethod(
        IMethodSymbol? methodSymbol,
        ExpressionSyntax receiverExpression,
        ConversionContext context)
    {
        if (methodSymbol?.ContainingType?.SpecialType == SpecialType.System_String)
            return true;

        // Instance method call: check the receiver expression's type via semantic model.
        if (context.GetTypeInfo(receiverExpression).Type?.SpecialType
            == SpecialType.System_String)
            return true;

        return ExpressionTransformerHelpers.StaticReceiverMatches(
            receiverExpression,
            context,
            "String",
            "System.String",
            "string");
    }

    private static string? GetTryParseHelperMethod(ExpressionSyntax receiverExpression, ConversionContext context)
    {
        if (ExpressionTransformerHelpers.StaticReceiverMatches(receiverExpression, context, "Double", "double", "System.Double"))
            return "MathHelper.tryParseDouble";

        if (ExpressionTransformerHelpers.StaticReceiverMatches(receiverExpression, context, "Single", "Float", "float", "System.Single"))
            return "MathHelper.tryParseFloat";

        if (ExpressionTransformerHelpers.StaticReceiverMatches(receiverExpression, context, "Int32", "Integer", "int", "System.Int32"))
            return "MathHelper.tryParseInt";

        if (ExpressionTransformerHelpers.StaticReceiverMatches(receiverExpression, context, "Int64", "Long", "long", "System.Int64"))
            return "MathHelper.tryParseLong";

        if (ExpressionTransformerHelpers.StaticReceiverMatches(receiverExpression, context, "Boolean", "bool", "System.Boolean"))
            return "MathHelper.tryParseBool";

        if (ExpressionTransformerHelpers.StaticReceiverMatches(receiverExpression, context, "Decimal", "decimal", "System.Decimal"))
            return "Decimal.tryParse";

        return null;
    }

    private static bool TryGetParseHelperMethod(ExpressionSyntax receiverExpression, ConversionContext context, out string helper)
    {
        helper = string.Empty;

        if (ExpressionTransformerHelpers.StaticReceiverMatches(receiverExpression, context, "Double", "double", "System.Double"))
        {
            helper = "MathHelper.parseDouble";
            return true;
        }

        if (ExpressionTransformerHelpers.StaticReceiverMatches(receiverExpression, context, "Single", "Float", "float", "System.Single"))
        {
            helper = "MathHelper.parseFloat";
            return true;
        }

        if (ExpressionTransformerHelpers.StaticReceiverMatches(receiverExpression, context, "Int32", "Integer", "int", "System.Int32"))
        {
            helper = "MathHelper.parseInt";
            return true;
        }

        if (ExpressionTransformerHelpers.StaticReceiverMatches(receiverExpression, context, "Int64", "Long", "long", "System.Int64"))
        {
            helper = "MathHelper.parseLong";
            return true;
        }

        if (ExpressionTransformerHelpers.StaticReceiverMatches(receiverExpression, context, "Decimal", "decimal", "System.Decimal"))
        {
            helper = "Decimal.parse";
            return true;
        }

        return false;
    }

    private static bool LooksLikeRegexInstanceExpression(ExpressionSyntax receiverExpression, ConversionContext context)
    {
        if (context.SemanticModel != null)
        {
            var receiverType = context.GetTypeInfo(receiverExpression).Type;
            if (receiverType?.ToDisplayString() == "System.Text.RegularExpressions.Regex")
                return true;
        }

        if (receiverExpression is MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax,
                Name: IdentifierNameSyntax memberName
            }
            && memberName.Identifier.Text.Length > 0)
        {
            var receiverText = receiverExpression.ToString();
            if (receiverText.EndsWith("." + memberName.Identifier.Text, StringComparison.Ordinal)
                && memberName.Identifier.Text.StartsWith("Parse", StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (receiverExpression is MemberAccessExpressionSyntax memberAccess)
        {
            var name = memberAccess.Name.Identifier.Text;
            return name.Length > 0 && char.IsUpper(name[0]);
        }

        return false;
    }

    private static bool IsNumberStylesArgument(ExpressionSyntax expr, ConversionContext context)
    {
        if (context.SemanticModel != null)
        {
            var typeName = context.GetTypeInfo(expr).Type?.ToDisplayString();
            if (typeName == "System.Globalization.NumberStyles")
                return true;
        }

        var text = expr.ToString();
        return text.StartsWith("NumberStyles.", StringComparison.Ordinal)
            || text.StartsWith("System.Globalization.NumberStyles.", StringComparison.Ordinal)
            || text.Contains("NumberStyles.", StringComparison.Ordinal);
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

    /// <summary>Resolve receiver type using multiple fallbacks.</summary>
    private static ITypeSymbol? ResolveReceiverType(ExpressionSyntax expr, ConversionContext context)
    {
        if (context.SemanticModel == null) return null;
        var type = context.GetTypeInfo(expr).Type;
        if (type is { TypeKind: not (TypeKind.Error or TypeKind.Unknown) }) return type;
        var sym = context.GetSymbolInfo(expr).Symbol;
        type = sym switch { ILocalSymbol ls => ls.Type, IFieldSymbol fs => fs.Type, IParameterSymbol ps => ps.Type, _ => null };
        if (type is { TypeKind: not (TypeKind.Error or TypeKind.Unknown) }) return type;
        if (expr is IdentifierNameSyntax id && context.VarTypeMap.TryGetValue(id.Identifier.Text, out var vmType))
            return vmType;
        return null;
    }

    private static bool ImplementsInterface(INamedTypeSymbol type, string interfaceFullName)
    {
        if (type.AllInterfaces.Any(i => i.ToDisplayString().StartsWith(interfaceFullName))) return true;
        if (type.ToDisplayString().StartsWith(interfaceFullName)) return true;
        return false;
    }

    /// <summary>
    /// Checks whether a camelCase Java method name would collide with an auto-generated
    /// operator method in the given containing type. A real collision requires the same
    /// Java name AND the same erased parameter signature (count + types). When true,
    /// non-operator methods should keep their PascalCase name at call sites because
    /// AddMethodIfNotDuplicate will rename the declaration to PascalCase.
    /// </summary>
    private static bool WouldCollideWithOperatorInType(INamedTypeSymbol? containingType, string camelCaseName, IMethodSymbol methodSymbol)
    {
        if (containingType == null) return false;
        foreach (var member in containingType.GetMembers())
        {
            if (member is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } ms
                && Member.OperatorTransformer.OpSymbolToJavaName.TryGetValue(ms.Name, out var opJavaName)
                && opJavaName == camelCaseName
                && ms.Parameters.Length == methodSymbol.Parameters.Length)
            {
                // Erased parameter types must also match for AddMethodIfNotDuplicate
                // to flag this as a true collision.
                bool typesMatch = true;
                for (int i = 0; i < ms.Parameters.Length; i++)
                {
                    if (!SymbolEqualityComparer.Default.Equals(
                        ms.Parameters[i].Type,
                        methodSymbol.Parameters[i].Type))
                    {
                        typesMatch = false;
                        break;
                    }
                }
                if (typesMatch)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Mirrors ClassTransformer.AddMethodIfNotDuplicateInternal for user methods whose
    /// normal camelCase Java name collides with an auto-generated property accessor.
    /// In that case the declaration is renamed back to PascalCase, so call sites must
    /// keep PascalCase as well.
    /// </summary>
    private static bool WouldCollideWithPropertyAccessorInType(
        INamedTypeSymbol? containingType,
        string camelCaseName,
        IMethodSymbol methodSymbol,
        ConversionContext context)
    {
        if (containingType == null) return false;

        foreach (var member in containingType.GetMembers())
        {
            if (member is not IPropertySymbol property)
                continue;

            var propertyName = ConversionContext.EscapeJavaKeyword(property.Name);
            var accessorSuffix = ToPascalCase(propertyName);

            if (property.GetMethod != null
                && camelCaseName == "get" + accessorSuffix
                && methodSymbol.Parameters.Length == 0)
            {
                return !AccessorIsPrivate(property.GetMethod, methodSymbol);
            }

            if (property.SetMethod != null
                && camelCaseName == "set" + accessorSuffix
                && methodSymbol.Parameters.Length == 1
                && ErasedJavaType(context.MapType(methodSymbol.Parameters[0].Type))
                    == ErasedJavaType(context.MapType(property.Type)))
            {
                return !AccessorIsPrivate(property.SetMethod, methodSymbol);
            }
        }

        return false;
    }

    private static bool AccessorIsPrivate(IMethodSymbol accessor, IMethodSymbol methodSymbol)
        => accessor.DeclaredAccessibility == Accessibility.Private
            && methodSymbol.DeclaredAccessibility != Accessibility.Private;

    private static string ToPascalCase(string name)
        => string.IsNullOrEmpty(name) ? name : char.ToUpperInvariant(name[0]) + name[1..];

    /// <summary>
    /// Transforms the method-name argument of a Type.GetMethod call, converting a
    /// PascalCase string literal to camelCase so the reflection lookup matches the
    /// actual Java method name.
    /// </summary>
    private static string TransformReflectionMethodNameArg(
        Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax nameExpr,
        ExpressionTransformerFacade facade,
        ConversionContext context)
    {
        if (nameExpr is LiteralExpressionSyntax
            { RawKind: (int)SyntaxKind.StringLiteralExpression } strLit)
        {
            var pascalName = strLit.Token.ValueText;
            var camelName = ConvertPascalCaseMethodName(pascalName);
            return $"\"{camelName}\"";
        }
        return facade.Transform(nameExpr, context);
    }

    private static string ConvertPascalCaseMethodName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        return name switch
        {
            "GetHashCode" => "hashCode",
            "GetEnumerator" => "iterator",
            "GetType" => "getClass",
            "Dispose" => "close",
            "ToLower" => "toLowerCase",
            "ToUpper" => "toUpperCase",
            "ToLowerInvariant" => "toLowerCase",
            "ToUpperInvariant" => "toUpperCase",
            _ when char.IsUpper(name[0])
                => char.ToLowerInvariant(name[0]) + name[1..],
            _ => name
        };
    }

    private static string ErasedJavaType(string type)
    {
        var idx = type.IndexOf('<');
        return idx >= 0 ? type[..idx].TrimEnd() : type;
    }

    /// <summary>
    /// Attempts to inline a generic method call where a type parameter with new()
    /// constraint is instantiated. Resolves concrete type bindings at the call site
    /// and generates inlined code with concrete types substituted.
    /// </summary>
    private static string? TryInlineNewConstraintMethodCall(
        IMethodSymbol methodSymbol,
        InvocationExpressionSyntax node,
        MemberAccessExpressionSyntax memberAccess,
        ConversionContext context)
    {
        if (!methodSymbol.IsGenericMethod)
            return null;

        var origDef = methodSymbol.OriginalDefinition;
        var newConstrainedParams = origDef.TypeParameters
            .Where(tp => tp.HasConstructorConstraint)
            .ToList();

        if (newConstrainedParams.Count == 0)
            return null;

        // Build concrete type arg mapping
        var origParams = origDef.TypeParameters;
        var typeArgs = methodSymbol.TypeArguments;

        if (origParams.Length != typeArgs.Length)
            return null;

        var typeArgMap = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        for (int i = 0; i < origParams.Length; i++)
        {
            if (typeArgs[i] is ITypeParameterSymbol)
                return null; // nested generic — can't resolve concrete type
            typeArgMap[origParams[i]] = typeArgs[i];
        }

        // Try to match the AddToMap pattern and generate inline code
        return TryGenerateAddToMapInline(methodSymbol, typeArgMap, node, context);
    }

    /// <summary>
    /// Generates inlined Java code for the CollectionUtilities.AddToMap pattern:
    ///   TC tc = dict.get(key);
    ///   if (tc == null) { tc = new TC(); dict.put(key, tc); }
    ///   tc.add(value);
    /// </summary>
    private static string? TryGenerateAddToMapInline(
        IMethodSymbol methodSymbol,
        Dictionary<ITypeParameterSymbol, ITypeSymbol> typeArgMap,
        InvocationExpressionSyntax node,
        ConversionContext context)
    {
        var parameters = methodSymbol.Parameters;

        // Must have exactly 3 parameters: (Dictionary, key, value)
        if (parameters.Length < 3)
            return null;

        // Find the Dictionary parameter
        int dictParamIdx = -1;
        for (int i = 0; i < parameters.Length; i++)
        {
            var paramOriginalDisplay = parameters[i].Type.OriginalDefinition.ToDisplayString();
            if (paramOriginalDisplay.StartsWith("System.Collections.Generic.Dictionary<")
                || paramOriginalDisplay.StartsWith("System.Collections.Generic.IDictionary<"))
            {
                dictParamIdx = i;
                break;
            }
        }
        if (dictParamIdx < 0)
            return null;

        // Find the TC type parameter (the one with new() constraint)
        var tcParam = methodSymbol.OriginalDefinition.TypeParameters
            .FirstOrDefault(tp => tp.HasConstructorConstraint);
        if (tcParam == null)
            return null;

        // Get concrete TC type
        if (!typeArgMap.TryGetValue(tcParam, out var tcConcreteSymbol))
            return null;

        string tcType = context.MapType(tcConcreteSymbol);

        // Identify key and value parameter indices
        // The non-dictionary params are key and value.
        // key matches dict.TypeArguments[0]; value is the remaining param
        int keyParamIdx = -1;
        int valueParamIdx = -1;

        if (parameters[dictParamIdx].Type is INamedTypeSymbol dictType
            && dictType.TypeArguments.Length == 2)
        {
            var dictKeyType = dictType.TypeArguments[0];
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i == dictParamIdx) continue;
                if (SymbolEqualityComparer.Default.Equals(parameters[i].Type, dictKeyType))
                    keyParamIdx = i;
                else
                    valueParamIdx = i;
            }
        }

        // Fallback: assume param 1 = key, param 2 = value (canonical AddToMap order)
        if (keyParamIdx < 0) keyParamIdx = dictParamIdx == 0 ? 1 : 0;
        if (valueParamIdx < 0)
        {
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i != dictParamIdx && i != keyParamIdx)
                {
                    valueParamIdx = i;
                    break;
                }
            }
        }

        if (valueParamIdx < 0 || node.ArgumentList.Arguments.Count <= valueParamIdx)
            return null;

        // Verify the method body contains new TC() pattern (syntactic check)
        var origDef = methodSymbol.OriginalDefinition;
        BaseMethodDeclarationSyntax? methodSyntax = null;
        foreach (var syntaxRef in origDef.DeclaringSyntaxReferences)
        {
            if (syntaxRef.GetSyntax() is BaseMethodDeclarationSyntax decl)
            {
                methodSyntax = decl;
                break;
            }
        }

        if (methodSyntax?.Body == null)
            return null;

        // Check: does the body contain "new TC()" where TC is the new()-constrained param?
        bool hasNewTC = false;
        foreach (var descendant in methodSyntax.Body.DescendantNodes())
        {
            if (descendant is ObjectCreationExpressionSyntax creation
                && (creation.ArgumentList == null || creation.ArgumentList.Arguments.Count == 0)
                && creation.Type is IdentifierNameSyntax idName
                && idName.Identifier.Text == tcParam.Name)
            {
                hasNewTC = true;
                break;
            }
        }
        if (!hasNewTC)
            return null;

        // Generate the inlined code
        var facade = ExpressionTransformerFacade.Instance;

        var dictArg = node.ArgumentList.Arguments[dictParamIdx].Expression;
        var dictExpr = facade.Transform(dictArg, context);

        var keyArg = node.ArgumentList.Arguments[keyParamIdx].Expression;
        var keyExpr = facade.Transform(keyArg, context);

        var valueArg = node.ArgumentList.Arguments[valueParamIdx].Expression;
        var valueExpr = facade.Transform(valueArg, context);

        string tmpVar = context.GenerateSyntheticName("_tc");

        // { TC _tc = dict.get(key); if (_tc == null) { _tc = new TC(); dict.put(key, _tc); } _tc.add(value); }
        return $"{{ {tcType} {tmpVar} = {dictExpr}.get({keyExpr}); " +
               $"if ({tmpVar} == null) {{ {tmpVar} = new {tcType}(); {dictExpr}.put({keyExpr}, {tmpVar}); }} " +
               $"{tmpVar}.add({valueExpr}); }}";
    }

    /// <summary>
    /// When a method has had Class&lt;T&gt; parameters prepended (strategy 3 for
    /// method-level type parameters using default(T)), returns the ordered list of
    /// .class literal tokens to prepend at the call site. Returns null when the
    /// method needs no type tokens.
    /// </summary>
    private static List<string>? GetClassTypeTokensForCall(IMethodSymbol methodSymbol, ConversionContext context)
    {
        var originalDef = methodSymbol.OriginalDefinition;
        var containingType = originalDef.ContainingType;
        if (containingType == null)
            return null;

        var containingTypeMetadataName = containingType.MetadataName;
        var methodMetadataName = originalDef.MetadataName;

        // First check the pre-registered set (populated when the method was processed
        // before the call site). If not found, scan the method body directly — the
        // method may be declared after the call site in the source file.
        IReadOnlyList<string>? typeParamNames = null;
        if (context.MethodHasClassParams(containingTypeMetadataName, methodMetadataName))
        {
            typeParamNames = context.GetMethodClassTypeParamNames(containingTypeMetadataName, methodMetadataName);
        }
        else
        {
            // Fallback: scan the method's own syntax to detect method-level type
            // parameters that have default(T) usage. The method's body hasn't been
            // transformed yet, but we can check the C# syntax tree directly.
            typeParamNames = DetectDefaultUsageInMethod(originalDef);
            if (typeParamNames != null && typeParamNames.Count > 0)
            {
                // Register on-the-fly so subsequent call sites find it in the set.
                foreach (var tpName in typeParamNames)
                {
                    context.RequireClassTypeParam(containingTypeMetadataName, methodMetadataName, tpName);
                }
                // Drain immediately — the method's signature will be patched when
                // ApplyPendingClassTypeParams runs after the method is processed.
                context.DrainClassTypeParams();
            }
        }

        if (typeParamNames == null || typeParamNames.Count == 0)
            return null;

        var typeParamMap = new Dictionary<string, int>();
        for (int i = 0; i < originalDef.TypeParameters.Length; i++)
            typeParamMap[originalDef.TypeParameters[i].Name] = i;

        var typeArgs = methodSymbol.TypeArguments;

        var tokens = new List<string>();
        foreach (var tpName in typeParamNames)
        {
            if (typeParamMap.TryGetValue(tpName, out var index) && index < typeArgs.Length)
            {
                var concreteType = typeArgs[index];
                tokens.Add(ConversionContext.GetClassLiteral(concreteType, context));
            }
        }

        return tokens.Count > 0 ? tokens : null;
    }

    /// <summary>
    /// Scans the method's C# syntax for method-level type parameters used in
    /// default expressions (e.g. default(T)). Returns the names of type params
    /// that are used with default(). Used when the method body hasn't been
    /// processed yet (declared after the call site).
    /// </summary>
    private static List<string>? DetectDefaultUsageInMethod(IMethodSymbol methodSymbol)
    {
        var typeParams = methodSymbol.TypeParameters;
        if (typeParams.Length == 0)
            return null;

        List<string>? result = null;
        foreach (var syntaxRef in methodSymbol.DeclaringSyntaxReferences)
        {
            var syntax = syntaxRef.GetSyntax();
            if (syntax is not Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax methodDecl)
                continue;

            // Collect type param names that appear in default() expressions
            var defaultNodes = methodDecl.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.DefaultExpressionSyntax>();
            foreach (var defNode in defaultNodes)
            {
                if (defNode.Type is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax id)
                {
                    foreach (var tp in typeParams)
                    {
                        if (tp.Name == id.Identifier.Text)
                        {
                            result ??= new List<string>();
                            if (!result.Contains(tp.Name))
                                result.Add(tp.Name);
                        }
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Prepends Class&lt;T&gt; type tokens before existing arguments when the
    /// target method requires them. Returns the combined argument string.
    /// </summary>
    private static string PrependClassTypeTokens(string args, List<string>? classTypeTokens)
    {
        if (classTypeTokens == null || classTypeTokens.Count == 0)
            return args;

        var prefix = string.Join(", ", classTypeTokens);
        return string.IsNullOrEmpty(args) ? prefix : $"{prefix}, {args}";
    }

    /// <summary>
    /// Determines whether the receiver expression looks like a type name (static call)
    /// rather than a variable/field/property (instance call).
    /// Used when methodSymbol is null to decide if the receiver should be passed
    /// as the first argument to a static helper method.
    /// </summary>
    private static bool LooksLikeTypeReceiver(ExpressionSyntax expression, ConversionContext context)
    {
        // PredefinedTypeSyntax: int, string, double, etc. → always a type
        if (expression is PredefinedTypeSyntax)
            return true;

        // IdentifierNameSyntax: could be a type or a variable.
        // Use semantic model to distinguish when available.
        if (expression is IdentifierNameSyntax identifier)
        {
            if (context.SemanticModel != null)
            {
                var symbol = context.GetSymbolInfo(identifier).Symbol;
                // If the symbol is a named type, it's a static call
                if (symbol is INamedTypeSymbol)
                    return true;
                // If it's a field/local/parameter/property, it's an instance call
                if (symbol is IFieldSymbol or ILocalSymbol or IParameterSymbol or IPropertySymbol)
                    return false;
            }

            // When semantic model cannot resolve the symbol, check if the identifier
            // matches a type name in TypeMappings. If so, it's a static call.
            var text = identifier.Identifier.Text;
            var mappedType = context.TypeMappings.MapType(text);
            if (mappedType != null && mappedType != text)
                return true;
            // Also check with common namespace prefixes
            if (context.TypeMappings.MapType($"System.Drawing.{text}") != null
                || context.TypeMappings.MapType($"System.{text}") != null)
                return true;

            return false;
        }

        // Qualified name (System.Console, System.IO.File, etc.) → always a type
        if (expression is QualifiedNameSyntax)
            return true;

        // MemberAccessExpressionSyntax as receiver (e.g. System.IO.File.Exists)
        if (expression is MemberAccessExpressionSyntax)
            return true;

        // Anything else (this, base, method calls, etc.) → not a type
        return false;
    }
}
