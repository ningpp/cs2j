using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Member;

public class EventFieldTransformer
{
    public List<JavaSyntaxNode> TransformEvent(EventFieldDeclarationSyntax node, ConversionContext context)
    {
        var result = new List<JavaSyntaxNode>();

        foreach (var variable in node.Declaration.Variables)
        {
            var eventName = variable.Identifier.Text;
            var sig = DetermineListenerSignature(node.Declaration.Type, context);
            result.AddRange(GenerateEventMembers(node, eventName, sig));
        }

        return result;
    }

    public List<JavaSyntaxNode> TransformExplicitEvent(EventDeclarationSyntax node, ConversionContext context)
    {
        var eventName = node.Identifier.Text;
        var sig = DetermineListenerSignature(node.Type, context);
        return GenerateEventMembers(null, eventName, sig);
    }

    private class EventSignature
    {
        public string ListenerType { get; set; } = "java.util.function.Consumer<Object>";
        public List<JavaParameter> Parameters { get; set; } = new List<JavaParameter>();
        public string InvokeCallArguments { get; set; } = "";
        public string InvokeMethodName { get; set; } = "accept";
    }

    private List<JavaSyntaxNode> GenerateEventMembers(EventFieldDeclarationSyntax? node, string eventName, EventSignature sig)
    {
        var result = new List<JavaSyntaxNode>();

        var fieldName = "_" + char.ToLower(eventName[0]) + eventName.Substring(1) + "Listeners";
        var baseName = char.ToUpper(eventName[0]) + eventName.Substring(1);
        var addMethod = "add" + baseName + "Listener";
        var removeMethod = "remove" + baseName + "Listener";
        var fireMethod = "fire" + baseName;

        result.Add(new JavaFieldDeclaration
        {
            Type = $"java.util.List<{sig.ListenerType}>",
            Name = fieldName,
            Modifiers = JavaModifiers.Private,
            Initializer = "new java.util.ArrayList<>()"
        });

        result.Add(new JavaMethodDeclaration
        {
            Name = addMethod,
            Modifiers = JavaModifiers.Public,
            ReturnType = "void",
            Parameters = { new JavaParameter(sig.ListenerType, "handler") },
            Body = $"{fieldName}.add(handler);",
        });

        result.Add(new JavaMethodDeclaration
        {
            Name = removeMethod,
            Modifiers = JavaModifiers.Public,
            ReturnType = "void",
            Parameters = { new JavaParameter(sig.ListenerType, "handler") },
            Body = $"{fieldName}.remove(handler);",
        });

        result.Add(new JavaMethodDeclaration
        {
            Name = fireMethod,
            Modifiers = JavaModifiers.Protected,
            ReturnType = "void",
            Parameters = sig.Parameters,
            Body = $"for (var _handler : {fieldName}) _handler.{sig.InvokeMethodName}({sig.InvokeCallArguments});",
        });

        return result;
    }

    private EventSignature DetermineListenerSignature(TypeSyntax typeSyntax, ConversionContext context)
    {
        var sig = new EventSignature();
        var typeInfo = context.SemanticModel?.GetTypeInfo(typeSyntax);

        if (typeInfo.HasValue && typeInfo.Value.Type is INamedTypeSymbol namedType)
        {
            var origDef = namedType.OriginalDefinition;
            var origName = origDef.Name;
            var origNs = origDef.ContainingNamespace?.ToDisplayString();

            if (origName == "EventHandler" && origNs == "System" && namedType.TypeArguments.Length == 0)
            {
                sig.ListenerType = "java.util.function.Consumer<Object>";
                sig.Parameters.Add(new JavaParameter("Object", "sender"));
                sig.Parameters.Add(new JavaParameter("Object", "args"));
                sig.InvokeCallArguments = "args"; 
                sig.InvokeMethodName = "accept";
                return sig;
            }

            if (origName == "EventHandler" && origNs == "System" && namedType.TypeArguments.Length == 1)
            {
                var argType = context.MapType(namedType.TypeArguments[0]);
                sig.ListenerType = $"java.util.function.Consumer<{argType}>";
                sig.Parameters.Add(new JavaParameter("Object", "sender"));
                sig.Parameters.Add(new JavaParameter(argType, "args"));
                sig.InvokeCallArguments = "args";
                sig.InvokeMethodName = "accept";
                return sig;
            }

            if (origName == "Action" && origNs == "System" && namedType.TypeArguments.Length == 0)
            {
                sig.ListenerType = "Runnable";
                sig.InvokeCallArguments = "";
                sig.InvokeMethodName = "run";
                return sig;
            }

            if (origName == "Action" && origNs == "System" && namedType.TypeArguments.Length == 1)
            {
                var argType = context.MapType(namedType.TypeArguments[0]);
                sig.ListenerType = $"java.util.function.Consumer<{argType}>";
                sig.Parameters.Add(new JavaParameter(argType, "arg"));
                sig.InvokeCallArguments = "arg";
                sig.InvokeMethodName = "accept";
                return sig;
            }

            sig.ListenerType = context.MapType(namedType);
            var invokeMethod = namedType.DelegateInvokeMethod;
            if (invokeMethod != null)
            {
                var pNames = new List<string>();
                foreach (var p in invokeMethod.Parameters)
                {
                    var pType = context.MapType(p.Type);
                    var pName = p.Name;
                    sig.Parameters.Add(new JavaParameter(pType, pName));
                    pNames.Add(pName);
                }
                sig.InvokeCallArguments = string.Join(", ", pNames);
                sig.InvokeMethodName = "invoke";
            }
            else
            {
                sig.Parameters.Add(new JavaParameter("Object", "sender"));
                sig.Parameters.Add(new JavaParameter("Object", "args"));
                sig.InvokeCallArguments = "sender, args";
                sig.InvokeMethodName = "invoke";
            }

            return sig;
        }

        if (typeSyntax is GenericNameSyntax generic && generic.Identifier.Text == "EventHandler" && generic.TypeArgumentList.Arguments.Count == 1)
        {
            var argTypeSyntax = generic.TypeArgumentList.Arguments[0];
            var argTypeInfo = context.SemanticModel?.GetTypeInfo(argTypeSyntax);
            var argType = argTypeInfo.HasValue && argTypeInfo.Value.Type != null ? context.MapType(argTypeInfo.Value.Type) : argTypeSyntax.ToString();
            
            sig.ListenerType = $"java.util.function.Consumer<{argType}>";
            sig.Parameters.Add(new JavaParameter("Object", "sender"));
            sig.Parameters.Add(new JavaParameter(argType, "args"));
            sig.InvokeCallArguments = "args";
            sig.InvokeMethodName = "accept";
            return sig;
        }

        sig.ListenerType = typeSyntax is IdentifierNameSyntax ident ? ident.Identifier.Text : typeSyntax.ToString();
        sig.Parameters.Add(new JavaParameter("Object", "sender"));
        sig.Parameters.Add(new JavaParameter("Object", "args"));
        sig.InvokeCallArguments = "sender, args";
        sig.InvokeMethodName = "invoke";

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
