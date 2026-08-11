using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.Transformers.Member;

/// <summary>
/// Naming for generated event listener members (add/remove/fire methods and the
/// backing listener-list field).
/// <para>
/// Events declared in an interface (or implemented from one) use interface-qualified
/// member names ? <c>addIViewerMouseDownListener</c> instead of <c>addMouseDownListener</c>.
/// This is required because Java rejects same-erasure overloads: a class implementing
/// <c>IViewer.MouseDown</c> while extending <c>System.Windows.Forms.Control</c> would
/// otherwise collide with the base class's <c>addMouseDownListener(BiConsumer&lt;Object, MouseEventArgs&gt;)</c>.
/// </para>
/// </summary>
internal static class EventListenerNaming
{
    /// <summary>
    /// Returns the interface name to prefix when the event is declared in an interface
    /// or implicitly implements an interface event of the same name/type; null otherwise.
    /// </summary>
    public static string? GetInterfaceQualifier(IEventSymbol evt)
    {
        var containing = evt.ContainingType;
        if (containing == null) return null;
        if (containing.TypeKind == TypeKind.Interface) return containing.Name;

        foreach (var iface in containing.AllInterfaces)
        {
            var ifaceEvt = iface.GetMembers(evt.Name).OfType<IEventSymbol>().FirstOrDefault();
            if (ifaceEvt == null) continue;
            if (EventTypesMatch(ifaceEvt.Type, evt.Type)) return iface.Name;
        }
        return null;
    }

    private static bool EventTypesMatch(ITypeSymbol? a, ITypeSymbol? b)
    {
        if (a == null || b == null) return false;
        if (SymbolEqualityComparer.Default.Equals(a, b)) return true;
        // When the delegate type is unresolved (missing reference assembly), match by
        // display string so implicit interface implementations are still recognized.
        return a is IErrorTypeSymbol ea && b is IErrorTypeSymbol eb
            && ea.ToDisplayString() == eb.ToDisplayString();
    }

    public static string GetFieldName(string? qualifier, string eventName)
    {
        var core = char.ToLowerInvariant(eventName[0]) + eventName[1..];
        if (string.IsNullOrEmpty(qualifier)) return "_" + core + "Listeners";
        return "_" + char.ToLowerInvariant(qualifier[0]) + qualifier[1..] + core + "Listeners";
    }

    public static string GetAddMethodName(string? qualifier, string eventName)
        => BuildListenerMethodName("add", qualifier, eventName);

    public static string GetRemoveMethodName(string? qualifier, string eventName)
        => BuildListenerMethodName("remove", qualifier, eventName);

    public static string GetFireMethodName(string? qualifier, string eventName)
    {
        var baseName = char.ToUpperInvariant(eventName[0]) + eventName[1..];
        return string.IsNullOrEmpty(qualifier) ? "fire" + baseName : "fire" + qualifier + baseName;
    }

    private static string BuildListenerMethodName(string action, string? qualifier, string eventName)
    {
        var baseName = char.ToUpperInvariant(eventName[0]) + eventName[1..];
        return string.IsNullOrEmpty(qualifier)
            ? action + baseName + "Listener"
            : action + qualifier + baseName + "Listener";
    }
}
