using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Member;

public class EventFieldTransformer : IEventFieldTransformer
{
    public List<JavaSyntaxNode> TransformEvent(EventFieldDeclarationSyntax node, ConversionContext context)
    {
        var result = new List<JavaSyntaxNode>();

        foreach (var variable in node.Declaration.Variables)
        {
            var eventName = variable.Identifier.Text;
            var sig = DetermineListenerSignature(node.Declaration.Type, context);
            // Fix 3: pass original modifiers so static is propagated
            result.AddRange(GenerateEventMembers(eventName, sig, node.Modifiers, context));
        }

        return result;
    }

    public List<JavaSyntaxNode> TransformExplicitEvent(EventDeclarationSyntax node, ConversionContext context)
    {
        var eventName = node.Identifier.Text;
        var sig = DetermineListenerSignature(node.Type, context);

        // Fix 1: translate explicit accessor bodies instead of silently discarding them
        string? addBody = null;
        string? removeBody = null;
        if (node.AccessorList != null)
        {
            var statementTransformer = new Transformers.Statement.StatementTransformer();

            var addAccessor = node.AccessorList.Accessors
                .FirstOrDefault(a => a.IsKind(SyntaxKind.AddAccessorDeclaration));
            if (addAccessor?.Body != null)
                addBody = NormalizeExplicitAccessorBody(statementTransformer.TransformBlock(addAccessor.Body, context));

            var removeAccessor = node.AccessorList.Accessors
                .FirstOrDefault(a => a.IsKind(SyntaxKind.RemoveAccessorDeclaration));
            if (removeAccessor?.Body != null)
                removeBody = NormalizeExplicitAccessorBody(statementTransformer.TransformBlock(removeAccessor.Body, context));
        }

        return GenerateEventMembers(eventName, sig, node.Modifiers, context, addBody, removeBody);
    }

    private static string NormalizeExplicitAccessorBody(string body)
        => System.Text.RegularExpressions.Regex.Replace(body, @"\bvalue\b", "handler");

    private class EventSignature
    {
        public string ListenerType { get; set; } = "Consumer<Object>";
        public List<JavaParameter> Parameters { get; set; } = new List<JavaParameter>();
        public string InvokeCallArguments { get; set; } = "";
        public string InvokeMethodName { get; set; } = "accept";
    }

    private List<JavaSyntaxNode> GenerateEventMembers(
        string eventName,
        EventSignature sig,
        SyntaxTokenList originalModifiers,
        ConversionContext context,
        string? addBody = null,
        string? removeBody = null)
    {
        var result = new List<JavaSyntaxNode>();

        var fieldName = "_" + char.ToLower(eventName[0]) + eventName.Substring(1) + "Listeners";
        var baseName = char.ToUpper(eventName[0]) + eventName.Substring(1);
        var addMethodName = "add" + baseName + "Listener";
        var removeMethodName = "remove" + baseName + "Listener";
        var fireMethodName = "fire" + baseName;

        // Fix 3: propagate static modifier from the event declaration
        bool isStatic = originalModifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
        var staticFlag = isStatic ? JavaModifiers.Static : JavaModifiers.None;

        // Fix 4: match fireXxx visibility to event visibility instead of hardcoding protected
        var fireVisibility = GetFireMethodVisibility(originalModifiers);

        // Fix 2 & Fix 6: use CopyOnWriteArrayList for thread-safe concurrent access and add its import
        context.AddImport("java.util.concurrent.CopyOnWriteArrayList");

        result.Add(new JavaFieldDeclaration
        {
            Type = $"java.util.concurrent.CopyOnWriteArrayList<{sig.ListenerType}>",
            Name = fieldName,
            Modifiers = JavaModifiers.Private | staticFlag,
            Initializer = "new java.util.concurrent.CopyOnWriteArrayList<>()"
        });

        result.Add(new JavaMethodDeclaration
        {
            Name = addMethodName,
            Modifiers = JavaModifiers.Public | staticFlag,
            ReturnType = "void",
            Parameters = { new JavaParameter(sig.ListenerType, "handler") },
            Body = addBody ?? $"{fieldName}.add(handler);",
        });

        result.Add(new JavaMethodDeclaration
        {
            Name = removeMethodName,
            Modifiers = JavaModifiers.Public | staticFlag,
            ReturnType = "void",
            Parameters = { new JavaParameter(sig.ListenerType, "handler") },
            Body = removeBody ?? $"{fieldName}.remove(handler);",
        });

        var fireMethodDecl = new JavaMethodDeclaration
        {
            Name = fireMethodName,
            Modifiers = fireVisibility | staticFlag,
            ReturnType = "void",
            Body = $"for (var _handler : {fieldName}) _handler.{sig.InvokeMethodName}({sig.InvokeCallArguments});",
        };
        fireMethodDecl.Parameters.AddRange(sig.Parameters);
        result.Add(fireMethodDecl);

        return result;
    }

    /// <summary>
    /// Map a C# delegate's invoke signature to the Java functional interface used for the
    /// generated event listener list. Void delegates with N parameters map to Runnable /
    /// Consumer / BiConsumer; non-void delegates map to Supplier / Function / BiFunction.
    /// This keeps the add/remove method parameter type consistent with the fire invocation
    /// (which passes every delegate parameter to the SAM method).
    /// </summary>
    private static string ResolveDelegateListenerType(IMethodSymbol invokeMethod, ConversionContext context)
    {
        var parameters = invokeMethod.Parameters;
        if (invokeMethod.ReturnsVoid)
        {
            if (parameters.Length == 0)
                return "Runnable";
            if (parameters.Length == 1)
                return $"Consumer<{context.MapType(parameters[0].Type)}>";
            if (parameters.Length == 2)
            {
                context.AddImport("java.util.function.BiConsumer");
                return $"BiConsumer<Object, {context.MapType(parameters[1].Type)}>";
            }
            // 3+ parameter void delegate — fall back to a generic single-arg consumer wrapper
            // (rare; an exact @FunctionalInterface would be required for precise typing).
            return "Consumer<Object>";
        }

        var returnType = context.MapType(invokeMethod.ReturnType);
        if (parameters.Length == 0)
            return $"Supplier<{returnType}>";
        if (parameters.Length == 1)
            return $"Function<{context.MapType(parameters[0].Type)}, {returnType}>";
        if (parameters.Length == 2)
        {
            context.AddImport("java.util.function.BiFunction");
            return $"BiFunction<{context.MapType(parameters[0].Type)}, {context.MapType(parameters[1].Type)}, {returnType}>";
        }
        return "Consumer<Object>";
    }

    // Fix 4: compute fire method visibility from the event's declared C# modifiers
    private static JavaModifiers GetFireMethodVisibility(SyntaxTokenList originalModifiers)
    {
        foreach (var mod in originalModifiers)
        {
            switch (mod.Kind())
            {
                case SyntaxKind.PrivateKeyword:
                    return JavaModifiers.Private;
                case SyntaxKind.InternalKeyword:
                    // internal → package-private in Java (no visibility keyword)
                    return JavaModifiers.None;
                case SyntaxKind.ProtectedKeyword:
                    return JavaModifiers.Protected;
                case SyntaxKind.PublicKeyword:
                    // public event → protected fire method (more restrictive than the event itself)
                    return JavaModifiers.Protected;
            }
        }
        // No explicit visibility → private (most restrictive default)
        return JavaModifiers.Private;
    }

    private EventSignature DetermineListenerSignature(TypeSyntax typeSyntax, ConversionContext context)
    {
        var sig = new EventSignature();
        var typeInfo = context.GetTypeInfo(typeSyntax);

        if (typeInfo.Type is INamedTypeSymbol namedType)
        {
            var origDef = namedType.OriginalDefinition;
            var origName = origDef.Name;
            var origNs = origDef.ContainingNamespace?.ToDisplayString();
            // In some reference assemblies the converter resolves the BCL delegates
            // EventHandler/Action with a global (empty) containing namespace rather than "System".
            // Match by name so the special-case mapping still applies in that case.
            bool isBclDelegate = origNs == "System" || origNs == "<global namespace>" || string.IsNullOrEmpty(origNs);

            if (origName == "EventHandler" && isBclDelegate && namedType.TypeArguments.Length == 0)
            {
                context.AddImport("java.util.function.BiConsumer");
                sig.ListenerType = "BiConsumer<Object, Object>";
                sig.Parameters.Add(new JavaParameter("Object", "sender"));
                sig.Parameters.Add(new JavaParameter("Object", "args"));
                sig.InvokeCallArguments = "sender, args";
                sig.InvokeMethodName = "accept";
                return sig;
            }

            if (origName == "EventHandler" && isBclDelegate && namedType.TypeArguments.Length == 1)
            {
                var argType = context.MapType(namedType.TypeArguments[0]);
                context.AddImport("java.util.function.BiConsumer");
                sig.ListenerType = $"BiConsumer<Object, {argType}>";
                sig.Parameters.Add(new JavaParameter("Object", "sender"));
                sig.Parameters.Add(new JavaParameter(argType, "args"));
                sig.InvokeCallArguments = "sender, args";
                sig.InvokeMethodName = "accept";
                return sig;
            }

            if (origName == "Action" && isBclDelegate && namedType.TypeArguments.Length == 0)
            {
                sig.ListenerType = "Runnable";
                sig.InvokeCallArguments = "";
                sig.InvokeMethodName = "run";
                return sig;
            }

            if (origName == "Action" && isBclDelegate && namedType.TypeArguments.Length == 1)
            {
                var argType = context.MapType(namedType.TypeArguments[0]);
                sig.ListenerType = $"Consumer<{argType}>";
                sig.Parameters.Add(new JavaParameter(argType, "arg"));
                sig.InvokeCallArguments = "arg";
                sig.InvokeMethodName = "accept";
                return sig;
            }

            // Derive the listener type from the delegate's actual invoke signature so that
            // multi-parameter void delegates (e.g. EventHandler<T> → (sender, e)) map to the
            // correct Java functional interface (BiConsumer), keeping add/remove/fire consistent.
            // Previously the listener type came from MapType(namedType), which collapses a 2-parameter
            // void delegate to Consumer<T> (single-arg) and breaks the fire call `accept(sender, args)`.
            //
            // However, for custom (non-BCL) delegate types, we must use the mapped delegate type name
            // as the listener type instead of a standard Java functional interface (BiConsumer).
            // This is because DelegateHelper.combine/remove expect matching types, and the event
            // backing field uses the custom delegate type (e.g., XmlNodeChangedEventHandler).
            var invokeMethod = namedType.DelegateInvokeMethod;
            if (invokeMethod != null)
            {
                if (isBclDelegate)
                {
                    sig.ListenerType = ResolveDelegateListenerType(invokeMethod, context);
                }
                else
                {
                    sig.ListenerType = context.MapType(namedType);
                }
                var pNames = new List<string>();
                foreach (var p in invokeMethod.Parameters)
                {
                    var pType = context.MapType(p.Type);
                    var pName = p.Name;
                    sig.Parameters.Add(new JavaParameter(pType, pName));
                    pNames.Add(pName);
                }
                sig.InvokeCallArguments = string.Join(", ", pNames);
                bool isVoid = invokeMethod.ReturnsVoid;
                sig.InvokeMethodName = Type.DelegateTransformer.InferSamMethodName(isVoid, invokeMethod.Parameters.Length);
            }
            else
            {
                // No resolvable invoke signature: most C# events are (sender, args) 2-parameter
                // void delegates, so default to BiConsumer to stay consistent with the fire body.
                sig.ListenerType = "BiConsumer<Object, Object>";
                context.AddImport("java.util.function.BiConsumer");
                sig.Parameters.Add(new JavaParameter("Object", "sender"));
                sig.Parameters.Add(new JavaParameter("Object", "args"));
                sig.InvokeCallArguments = "sender, args";
                sig.InvokeMethodName = "accept";
            }

            return sig;
        }

        // Fix 5: fallback path without semantic model — handle common delegate types to avoid name mangling
        if (typeSyntax is GenericNameSyntax generic)
        {
            if (generic.Identifier.Text == "EventHandler" && generic.TypeArgumentList.Arguments.Count == 1)
            {
                var argTypeSyntax = generic.TypeArgumentList.Arguments[0];
                var argTypeInfo = context.GetTypeInfo(argTypeSyntax);
                var argType = argTypeInfo.Type != null
                    ? context.MapType(argTypeInfo.Type)
                    : context.MapTypeFromSyntax(argTypeSyntax);

                context.AddImport("java.util.function.BiConsumer");
                sig.ListenerType = $"BiConsumer<Object, {argType}>";
                sig.Parameters.Add(new JavaParameter("Object", "sender"));
                sig.Parameters.Add(new JavaParameter(argType, "args"));
                sig.InvokeCallArguments = "sender, args";
                sig.InvokeMethodName = "accept";
                return sig;
            }

            if (generic.Identifier.Text == "Action" && generic.TypeArgumentList.Arguments.Count == 1)
            {
                var argTypeSyntax = generic.TypeArgumentList.Arguments[0];
                var argTypeInfo = context.GetTypeInfo(argTypeSyntax);
                var argType = argTypeInfo.Type != null
                    ? context.MapType(argTypeInfo.Type)
                    : context.MapTypeFromSyntax(argTypeSyntax);

                sig.ListenerType = $"Consumer<{argType}>";
                sig.Parameters.Add(new JavaParameter(argType, "arg"));
                sig.InvokeCallArguments = "arg";
                sig.InvokeMethodName = "accept";
                return sig;
            }
        }

        // Fix 7: handle non-generic EventHandler/Action when semantic model cannot resolve them
        if (typeSyntax is IdentifierNameSyntax ident)
        {
            if (ident.Identifier.Text == "EventHandler")
            {
                context.AddImport("java.util.function.BiConsumer");
                sig.ListenerType = "BiConsumer<Object, Object>";
                sig.Parameters.Add(new JavaParameter("Object", "sender"));
                sig.Parameters.Add(new JavaParameter("Object", "args"));
                sig.InvokeCallArguments = "sender, args";
                sig.InvokeMethodName = "accept";
                return sig;
            }
            if (ident.Identifier.Text == "Action")
            {
                sig.ListenerType = "Runnable";
                sig.InvokeCallArguments = "";
                sig.InvokeMethodName = "run";
                return sig;
            }
        }

        sig.ListenerType = typeSyntax is IdentifierNameSyntax id ? id.Identifier.Text : typeSyntax.ToString();
        sig.Parameters.Add(new JavaParameter("Object", "sender"));
        sig.Parameters.Add(new JavaParameter("Object", "args"));
        sig.InvokeCallArguments = "sender, args";
        sig.InvokeMethodName = Type.DelegateTransformer.InferSamMethodName(true, 2);

        return sig;
    }

    private static JavaModifiers GetModifiers(SyntaxTokenList modifiers)
    {
        JavaModifiers result = JavaModifiers.None;
        foreach (var mod in modifiers)
        {
            result |= mod.Kind() switch
            {
                SyntaxKind.PublicKeyword => JavaModifiers.Public,
                SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                SyntaxKind.InternalKeyword => JavaModifiers.Public,
                SyntaxKind.StaticKeyword => JavaModifiers.Static,
                _ => JavaModifiers.None,
            };
        }
        return result;
    }
}
