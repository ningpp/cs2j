using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Member;

/// <summary>
/// Converts C# event field declarations into a Java listener-list pattern.
///
/// C# input:
///   public event EventHandler&lt;LayoutProgressEventArgs&gt; LayoutStarted;
///
/// Java output (4 members added to the class):
///   private java.util.List&lt;java.util.function.Consumer&lt;LayoutProgressEventArgs&gt;&gt; _layoutStartedListeners = new java.util.ArrayList&lt;&gt;();
///   public void addLayoutStartedListener(java.util.function.Consumer&lt;LayoutProgressEventArgs&gt; h) { _layoutStartedListeners.add(h); }
///   public void removeLayoutStartedListener(java.util.function.Consumer&lt;LayoutProgressEventArgs&gt; h) { _layoutStartedListeners.remove(h); }
///   protected void fireLayoutStarted(Object sender, LayoutProgressEventArgs args) { for (var _h : _layoutStartedListeners) _h.accept(args); }
/// </summary>
public class EventFieldTransformer
{
    /// <summary>
    /// Returns a list of Java members that represent the event pattern.
    /// </summary>
    public List<JavaSyntaxNode> TransformEvent(EventFieldDeclarationSyntax node, ConversionContext context)
    {
        var result = new List<JavaSyntaxNode>();

        foreach (var variable in node.Declaration.Variables)
        {
            var eventName = variable.Identifier.Text;
            result.AddRange(GenerateEventMembers(node, eventName, context));
        }

        return result;
    }

    /// <summary>
    /// Handles EventDeclarationSyntax (explicit add/remove accessors).
    /// We still generate the listener list + add/remove using the custom accessor bodies.
    /// </summary>
    public List<JavaSyntaxNode> TransformExplicitEvent(EventDeclarationSyntax node, ConversionContext context)
    {
        var eventName = node.Identifier.Text;
        var (listenerType, argsType) = DetermineListenerTypes(node.Type, context);
        return GenerateEventMembers(null, eventName, context,
            delegateType: listenerType, delegateArgsType: argsType);
    }

    private List<JavaSyntaxNode> GenerateEventMembers(EventFieldDeclarationSyntax? node, string eventName,
        ConversionContext context, string? delegateType = null, string? delegateArgsType = null)
    {
        var result = new List<JavaSyntaxNode>();

        // Determine listener type
        string listenerType;
        string argsType;
        if (delegateType == null && node != null)
        {
            (listenerType, argsType) = DetermineListenerTypes(node.Declaration.Type, context);
        }
        else
        {
            listenerType = delegateType ?? "Consumer<Object>";
            argsType = delegateArgsType ?? "Object";
        }

        var fieldName = "_" + char.ToLower(eventName[0]) + eventName.Substring(1) + "Listeners";
        var baseName = char.ToUpper(eventName[0]) + eventName.Substring(1);
        var addMethod = "add" + baseName + "Listener";
        var removeMethod = "remove" + baseName + "Listener";
        var fireMethod = "fire" + baseName;

        // Visibility from event declaration
        var eventMods = node != null ? GetModifiers(node.Modifiers) : JavaModifiers.None;

        // 1. Private listener list field
        result.Add(new JavaFieldDeclaration
        {
            Type = $"java.util.List<{listenerType}>",
            Name = fieldName,
            Modifiers = JavaModifiers.Private,
            Initializer = "new java.util.ArrayList<>()"
        });

        // 2. addXListener
        result.Add(new JavaMethodDeclaration
        {
            Name = addMethod,
            Modifiers = JavaModifiers.Public,
            ReturnType = "void",
            Parameters = { new JavaParameter(listenerType, "handler") },
            Body = $"{fieldName}.add(handler);",
        });

        // 3. removeXListener
        result.Add(new JavaMethodDeclaration
        {
            Name = removeMethod,
            Modifiers = JavaModifiers.Public,
            ReturnType = "void",
            Parameters = { new JavaParameter(listenerType, "handler") },
            Body = $"{fieldName}.remove(handler);",
        });

        // 4. fireX (protected)
        string fireBody;
        if (listenerType.StartsWith("Consumer<") || listenerType == "Consumer")
        {
            fireBody = $"for (var _handler : {fieldName}) _handler.accept(args);";
        }
        else
        {
            // Custom delegate type - call invoke
            fireBody = $"for (var _handler : {fieldName}) _handler.invoke(sender, args);";
        }

        result.Add(new JavaMethodDeclaration
        {
            Name = fireMethod,
            Modifiers = JavaModifiers.Protected,
            ReturnType = "void",
            Parameters = { new JavaParameter("Object", "sender"), new JavaParameter(argsType, "args") },
            Body = fireBody,
        });

        return result;
    }

    private (string listenerType, string argsType) DetermineListenerTypes(TypeSyntax typeSyntax, ConversionContext context)
    {
        // Try resolving via semantic model
        var typeInfo = context.SemanticModel?.GetTypeInfo(typeSyntax);
        if (typeInfo.HasValue && typeInfo.Value.Type is INamedTypeSymbol namedType)
        {
            var origDef = namedType.OriginalDefinition;
            var origName = origDef.Name;          // e.g. "EventHandler"
            var origNs   = origDef.ContainingNamespace?.ToDisplayString(); // e.g. "System"
            // EventHandler (non-generic) -> Consumer<Object>
            if (origName == "EventHandler" && origNs == "System" && namedType.TypeArguments.Length == 0)
                return ("Consumer<Object>", "Object");
            // EventHandler<TArgs> -> Consumer<TArgs>
            if (origName == "EventHandler" && origNs == "System" && namedType.TypeArguments.Length == 1)
            {
                var argType = context.MapType(namedType.TypeArguments[0]);
                return ($"Consumer<{argType}>", argType);
            }
            // Action -> Runnable
            if (origName == "Action" && origNs == "System" && namedType.TypeArguments.Length == 0)
                return ("Runnable", "Object");
            // Action<T> -> Consumer<T>
            if (origName == "Action" && origNs == "System" && namedType.TypeArguments.Length == 1)
            {
                var argType = context.MapType(namedType.TypeArguments[0]);
                return ($"Consumer<{argType}>", argType);
            }
            // Custom delegate type - use its full mapped name
            var mappedType = context.MapType(namedType);
            return (mappedType, "Object");
        }

        // Fallback: parse syntax
        if (typeSyntax is GenericNameSyntax generic)
        {
            var name = generic.Identifier.Text;
            if (name == "EventHandler" && generic.TypeArgumentList.Arguments.Count == 1)
            {
                var argTypeSyntax = generic.TypeArgumentList.Arguments[0];
                var argTypeInfo = context.SemanticModel?.GetTypeInfo(argTypeSyntax);
                var argType = argTypeInfo.HasValue && argTypeInfo.Value.Type != null
                    ? context.MapType(argTypeInfo.Value.Type)
                    : argTypeSyntax.ToString();
                return ($"Consumer<{argType}>", argType);
            }
        }
        if (typeSyntax is IdentifierNameSyntax ident)
        {
            if (ident.Identifier.Text == "EventHandler")
                return ("Consumer<Object>", "Object");
            return (ident.Identifier.Text, "Object");
        }

        return ("Consumer<Object>", "Object");
    }

    private string GetEventType(TypeSyntax typeSyntax, ConversionContext context)
    {
        var (listenerType, _) = DetermineListenerTypes(typeSyntax, context);
        return listenerType;
    }

    private static JavaModifiers GetModifiers(SyntaxTokenList modifiers)
    {
        JavaModifiers result = JavaModifiers.None;
        foreach (var mod in modifiers)
        {
            result |= mod.Kind() switch
            {
                SyntaxKind.PublicKeyword => JavaModifiers.Public,
                SyntaxKind.InternalKeyword => JavaModifiers.Public,
                SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                SyntaxKind.StaticKeyword => JavaModifiers.Static,
                _ => JavaModifiers.None
            };
        }
        return result;
    }
}
