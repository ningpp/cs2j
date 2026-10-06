using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Member;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles simple, compound and coalesce (??=) assignment expressions.
/// Only the core reshaping remains: property setters, indexers, event +=/-=,
/// out/ref holders and discard; everything else lowers to a plain assignment.
/// </summary>
[TransformerRegistration]
public class AssignmentTransformer : IIRExpressionTransformer
{
    static AssignmentTransformer() => ExpressionTransformerRegistry.Register(
        Operators.Select(o => o.Kind).ToArray(), new AssignmentTransformer());

    private static readonly Lazy<AssignmentTransformer> _instance = new(() => new());
    public static AssignmentTransformer Instance => _instance.Value;

    private static readonly (SyntaxKind Kind, string Op)[] Operators =
    {
        (SyntaxKind.SimpleAssignmentExpression, "="),
        (SyntaxKind.AddAssignmentExpression, "+="),
        (SyntaxKind.SubtractAssignmentExpression, "-="),
        (SyntaxKind.MultiplyAssignmentExpression, "*="),
        (SyntaxKind.DivideAssignmentExpression, "/="),
        (SyntaxKind.ModuloAssignmentExpression, "%="),
        (SyntaxKind.AndAssignmentExpression, "&="),
        (SyntaxKind.OrAssignmentExpression, "|="),
        (SyntaxKind.ExclusiveOrAssignmentExpression, "^="),
        (SyntaxKind.LeftShiftAssignmentExpression, "<<="),
        (SyntaxKind.RightShiftAssignmentExpression, ">>="),
        (SyntaxKind.CoalesceAssignmentExpression, "??="),
    };

    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
        => new JavaRawExpression(Transform(node, context), "void");

    public string Transform(ExpressionSyntax node, ConversionContext context)
    {
        var a = (AssignmentExpressionSyntax)node;
        var f = ExpressionTransformerFacade.Instance;
        if (a.IsKind(SyntaxKind.CoalesceAssignmentExpression)) return Coalesce(a, context);

        var op = Operators.First(o => o.Kind == a.Kind()).Op;
        var lhs = a.Left;
        var rhs = a.Right;

        if (op == "=" && lhs is IdentifierNameSyntax { Identifier.ValueText: "_" })
            return f.Transform(rhs, context);

        var symbol = context.GetSymbolInfo(lhs).Symbol;

        if (op is "+=" or "-=" && symbol is IEventSymbol evt)
            return Event(a, op, evt, context);
        if (symbol is IPropertySymbol prop)
            return Property(a, op, prop, context);
        if (lhs is ElementAccessExpressionSyntax ea
            && context.GetTypeInfo(ea.Expression).Type is not IArrayTypeSymbol)
        {
            var setter = IsDictionary(ea.Expression, context) ? "put" : "set";
            return Indexer(ea, setter, "get", rhs, context, op == "=" ? null : op);
        }
        if (op == "=" && lhs is ImplicitElementAccessSyntax iea)
            return $"{(iea.ArgumentList.Arguments.Count == 1 ? "put" : "set")}(" +
                   $"{string.Join(", ", iea.ArgumentList.Arguments.Select(x => f.Transform(x.Expression, context)))}, " +
                   $"{f.Transform(rhs, context)})";
        if (op == "=" && symbol is IParameterSymbol { RefKind: RefKind.Out or RefKind.Ref } param)
            return $"{ConversionContext.EscapeJavaKeyword(param.Name)}.value = {f.Transform(rhs, context)}";

        return $"{f.Transform(lhs, context)} {op} {f.Transform(rhs, context)}";
    }

    // ── Property ────────────────────────────────────────────────────────────

    private static string Property(AssignmentExpressionSyntax a, string op, IPropertySymbol p, ConversionContext c)
    {
        var f = ExpressionTransformerFacade.Instance;
        var receiver = a.Left is MemberAccessExpressionSyntax ma ? f.Transform(ma.Expression, c) + "." : "";
        var name = char.ToUpperInvariant(p.Name[0]) + p.Name[1..];
        var right = f.Transform(a.Right, c);

        // TypeMappings may rename the property's accessor pair (see
        // IdentifierExpressionTransformer.TransformMemberAccess for the read
        // side); only bean-style method names apply here — field-style renames
        // (length/size) are consumed by the lowering pipeline instead.
        var mappedPropertyName = JavaNaming.IsBeanAccessorStyleName(
                c.TypeMappings.MapMethod(p.ContainingType.ToDisplayString(), p.Name))
            ? c.TypeMappings.MapMethod(p.ContainingType.ToDisplayString(), p.Name)
            : null;
        var getterName = mappedPropertyName ?? ("get" + name);
        var setterName = mappedPropertyName ?? ("set" + name);

        if (op == "=" && p.SetMethod == null)
        {
            var field = JavaNaming.GetAutoPropertyBackingFieldName(p);
            return $"{(receiver.Length == 0 ? "this." : receiver)}{field} = {right}";
        }
        if (op == "=")
            return $"{receiver}{setterName}({right})";
        return $"{receiver}{setterName}({receiver}{getterName}() {op[..^1]} {right})";
    }

    // ── Indexer ─────────────────────────────────────────────────────────────

    private static string Indexer(ElementAccessExpressionSyntax ea, string setter, string getter,
        ExpressionSyntax rhs, ConversionContext c, string? compoundOp = null)
    {
        var f = ExpressionTransformerFacade.Instance;
        var target = f.Transform(ea.Expression, c);
        var args = string.Join(", ", ea.ArgumentList.Arguments.Select(x => f.Transform(x.Expression, c)));
        var val = f.Transform(rhs, c);
        return compoundOp == null
            ? $"{target}.{setter}({args}, {val})"
            : $"{target}.{setter}({args}, {target}.{getter}({args}) {compoundOp[..^1]} {val})";
    }

    private static bool IsDictionary(ExpressionSyntax expression, ConversionContext c)
    {
        if (c.GetTypeInfo(expression).Type is not INamedTypeSymbol t) return false;
        static bool MapLike(INamedTypeSymbol x) =>
            x.Name.Contains("Dictionary") || x.Name.Contains("SortedList") || x.Name.Contains("Map");
        return MapLike(t) || t.AllInterfaces.Any(i => i is INamedTypeSymbol n && MapLike(n));
    }

    // ── Event subscription ──────────────────────────────────────────────────

    private static string Event(AssignmentExpressionSyntax a, string op, IEventSymbol evt, ConversionContext c)
    {
        var f = ExpressionTransformerFacade.Instance;
        var rawRight = f.Transform(a.Right, c);
        // Custom event accessor body (`add { E += value; }`): the generated add method
        // names its single parameter after the listener (e.g. "handler"), not "value".
        if (a.Right is IdentifierNameSyntax { Identifier.ValueText: "value" }
            && c.CurrentMethod?.Parameters.Length == 1)
        {
            rawRight = ConversionContext.EscapeJavaKeyword(c.CurrentMethod.Parameters[0].Name);
        }
        var handler = HandlerLambda(rawRight, a.Right, c);
        var action = op == "+=" ? "add" : "remove";

        if (a.Left is MemberAccessExpressionSyntax ma)
        {
            var qualifier = EventListenerNaming.GetInterfaceQualifier(evt);
            var method = op == "+="
                ? EventListenerNaming.GetAddMethodName(qualifier, evt.Name)
                : EventListenerNaming.GetRemoveMethodName(qualifier, evt.Name);
            return $"{f.Transform(ma.Expression, c)}.{method}({handler})";
        }

        var inDeclaringType = c.CurrentEnclosingRoslynType != null
            && SymbolEqualityComparer.Default.Equals(evt.ContainingType, c.CurrentEnclosingRoslynType);
        if (inDeclaringType)
        {
            var field = EventListenerNaming.GetFieldName(EventListenerNaming.GetInterfaceQualifier(evt), evt.Name);
            return $"{field}.{action}({handler})";
        }
        var inheritedMethod = op == "+="
            ? EventListenerNaming.GetAddMethodName(EventListenerNaming.GetInterfaceQualifier(evt), evt.Name)
            : EventListenerNaming.GetRemoveMethodName(EventListenerNaming.GetInterfaceQualifier(evt), evt.Name);
        return $"{inheritedMethod}({handler})";
    }

    /// <summary>
    /// A bare method-group handler (Handler / b.Handler) is invalid as a functional-interface
    /// value; derive an explicit lambda from the referenced method's signature.
    /// </summary>
    private static string HandlerLambda(string handler, ExpressionSyntax rhs, ConversionContext c)
    {
        if (handler.Contains("->") || handler.Contains("=>") || handler.Contains("(") || handler.Contains("::"))
            return handler;

        string? receiver = null;
        if (rhs is MemberAccessExpressionSyntax ma)
            receiver = ExpressionTransformerFacade.Instance.Transform(ma.Expression, c);

        var method = c.GetSymbolInfo(rhs).Symbol as IMethodSymbol
            ?? (rhs is IdentifierNameSyntax id
                ? c.CurrentEnclosingRoslynType?.GetMembers(id.Identifier.Text).OfType<IMethodSymbol>().FirstOrDefault()
                : null);
        if (method == null) return handler;

        var name = char.ToLowerInvariant(method.Name[0]) + method.Name[1..];
        var parameters = string.Join(", ", method.Parameters.Select(p => ConversionContext.EscapeJavaKeyword(p.Name)));
        receiver ??= method.IsStatic ? method.ContainingType.Name : "";
        var prefix = receiver.Length == 0 ? "" : receiver + ".";
        var arrow = "->";
        return $"({parameters}) {arrow} {prefix}{name}({parameters})";
    }

    // ── ??= ─────────────────────────────────────────────────────────────────

    private static string Coalesce(AssignmentExpressionSyntax a, ConversionContext c)
    {
        var f = ExpressionTransformerFacade.Instance;
        var left = f.Transform(a.Left, c);
        var right = f.Transform(a.Right, c);
        c.AddPreStatement($"if ({left} == null) {left} = {right}");
        return left;
    }
}
