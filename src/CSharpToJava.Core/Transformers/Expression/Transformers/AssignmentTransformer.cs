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
        // Element access must be handled before the property branch: a C# indexer
        // (`ref T this[int]`, List/Span/...) is an IPropertySymbol, and its
        // SetMethod is null for ref-return indexers. Falling into the property
        // path would lose the index/value and emit `this.this[] = ...`.
        if (lhs is ElementAccessExpressionSyntax ea
            && context.GetTypeInfo(ea.Expression).Type is not IArrayTypeSymbol)
        {
            // C# StringBuilder's Chars[int] indexer maps to Java charAt/setCharAt.
            var builderReceiver = context.GetTypeInfo(ea.Expression).Type
                is INamedTypeSymbol { Name: "StringBuilder" } sb
                && sb.ContainingNamespace?.ToDisplayString() == "System.Text";
            var setter = builderReceiver ? "setCharAt"
                : IsDictionary(ea.Expression, context) ? "put" : "set";
            var getter = builderReceiver ? "charAt" : "get";
            return Indexer(ea, setter, getter, rhs, context, op == "=" ? null : op);
        }
        if (symbol is IPropertySymbol prop)
            return Property(a, op, prop, context);
        if (op == "=" && lhs is ImplicitElementAccessSyntax iea)
            return $"{(iea.ArgumentList.Arguments.Count == 1 ? "put" : "set")}(" +
                   $"{string.Join(", ", iea.ArgumentList.Arguments.Select(x => f.Transform(x.Expression, context)))}, " +
                   $"{f.Transform(rhs, context)})";
        // C# struct self-assignment (`this = default;` / `this = clone;` from the
        // struct-value/ref-out-in lowering). Java structs are classes: copy fields.
        // Must run before the ref/out parameter branch — Roslyn exposes the implicit
        // struct `this` as a Ref parameter whose Java keyword-escaped name is thisValue.
        if (op == "=" && lhs.IsKind(SyntaxKind.ThisExpression))
        {
            context.AddImport("io.github.ningpp.compat.StructCloneHelper");
            return $"StructCloneHelper.copyInto(this, {f.Transform(rhs, context)})";
        }

        if (op == "=" && symbol is IParameterSymbol { RefKind: RefKind.Out or RefKind.Ref } param)
            return $"{ConversionContext.EscapeJavaKeyword(param.Name)}.value = {f.Transform(rhs, context)}";

        var rightText = f.Transform(rhs, context);
        // C# 允许方法组直接赋给兼容委托（真实链 XmlReaderSettings：
        // `_addValidationFunc = AddValidationInternal;`）。标识符转换器只会发出
        // 原名（javac: 找不到符号 变量 AddValidationInternal），委托目标存在时
        // 必须落成 Java 方法引用 this::addValidationInternal。
        if (op == "="
            && context.GetTypeInfo(lhs).Type is { } delegateTarget
            && ArgumentTransformer.TryEmitDelegateMethodReference(rhs, delegateTarget, context, out var methodRef))
        {
            rightText = methodRef;
        }
        rightText = SpanCoercion.CoerceOrSame(
            rhs, rightText, context.GetTypeInfo(lhs).Type, context);
        // IList 家族赋值槽位：与参数/声明/返回位一致的适配视图
        // （真实链 FunctionQuery/StringFunctions 克隆构造器 `_args = tmp`）。
        rightText = IListCoercion.CoerceOrSame(
            rhs, rightText, context.GetTypeInfo(lhs).Type, context);
        // C# 隐式数值 → decimal 赋值（真实链 SchemaSetCompiler `maxOccurs = 0;`、
        // RangeContentValidator `rangeCounters[...] = 0;`）：Java 无基元→引用提升，
        // 须经 compat Decimal 工厂包装。
        rightText = TypeOperationTransformer.TryCoerceToDecimal(
            rhs, rightText, context.GetTypeInfo(lhs).Type, context) ?? rightText;
        if (op == "=" && JavaNarrowTarget(lhs, context) is { } narrowTarget)
        {
            // C# implicit/explicit narrowing conversions to byte/short/char have no
            // Java counterpart; the numeric-boundary lowering returns widened
            // int/long values, so the assignment site must restore the cast.
            rightText = $"({narrowTarget}) ({rightText})";
        }

        return $"{f.Transform(lhs, context)} {op} {rightText}";
    }

    /// <summary>
    /// Java primitive target type of a simple assignment when it is narrower than
    /// int (byte/short/char); null for all other targets.
    /// </summary>
    private static string? JavaNarrowTarget(ExpressionSyntax lhs, ConversionContext context)
    {
        ITypeSymbol? targetType = null;
        if (lhs is ElementAccessExpressionSyntax element
            && context.GetTypeInfo(element.Expression).Type is IArrayTypeSymbol array)
        {
            return array.ElementType.SpecialType switch
            {
                // C# byte[] stays Java byte[] (see TypeMappingService array mapping).
                SpecialType.System_Byte => "byte",
                SpecialType.System_SByte => "byte",
                SpecialType.System_Int16 => "short",
                SpecialType.System_Char => "char",
                _ => null,
            };
        }

        targetType = context.GetSymbolInfo(lhs).Symbol switch
        {
            ILocalSymbol local => local.Type,
            IFieldSymbol field => field.Type,
            IParameterSymbol parameter => parameter.Type,
            _ => null,
        };

        return targetType?.SpecialType switch
        {
            SpecialType.System_Byte => "byte",
            SpecialType.System_SByte => "byte",
            SpecialType.System_Int16 => "short",
            SpecialType.System_Char => "char",
            _ => null,
        };
    }

    // ── Property ────────────────────────────────────────────────────────────

    private static string Property(AssignmentExpressionSyntax a, string op, IPropertySymbol p, ConversionContext c)
    {
        // An indexer is only assignable through element access, which is
        // dispatched before this method. Reaching here means the index and value
        // are unavailable; fail clearly instead of emitting an invalid
        // `this.this[]` (the indexer symbol's name is `this[]`).
        if (p.IsIndexer)
        {
            throw new NotSupportedException(
                $"Indexer assignment to '{p.ToDisplayString()}' must go through element access.");
        }

        var f = ExpressionTransformerFacade.Instance;
        var receiver = a.Left is MemberAccessExpressionSyntax ma ? f.Transform(ma.Expression, c) + "." : "";
        var name = char.ToUpperInvariant(p.Name[0]) + p.Name[1..];
        var right = f.Transform(a.Right, c);
        // C# 隐式数值 → decimal 属性赋值（真实链 XmlSchemaInference
        // `el.MinOccurs = 0`、SchemaSetCompiler `virtualSeq.MinOccurs = 1`）：
        // setter 形参落 compat Decimal，数值须经 Decimal.valueOf 包装。
        right = TypeOperationTransformer.TryCoerceToDecimal(a.Right, right, p.Type, c) ?? right;

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
            return PropertySetterCall(a.Left, p, right, c);
        return $"{receiver}{setterName}({receiver}{getterName}() {op[..^1]} {right})";
    }

    /// <summary>
    /// Renders a plain property setter call `{receiver}.set{Name}({valueText})`
    /// with the same accessor-name mapping Property() uses. Shared with the
    /// statement-level chained-assignment split (C# `a.P = b.Q = v;`), whose
    /// setter statements must not diverge from the single-assignment path.
    /// </summary>
    internal static string PropertySetterCall(ExpressionSyntax left, IPropertySymbol p, string valueText, ConversionContext c)
    {
        var f = ExpressionTransformerFacade.Instance;
        var receiver = left is MemberAccessExpressionSyntax ma ? f.Transform(ma.Expression, c) + "." : "";
        var name = char.ToUpperInvariant(p.Name[0]) + p.Name[1..];
        var mappedPropertyName = JavaNaming.IsBeanAccessorStyleName(
                c.TypeMappings.MapMethod(p.ContainingType.ToDisplayString(), p.Name))
            ? c.TypeMappings.MapMethod(p.ContainingType.ToDisplayString(), p.Name)
            : null;
        var setterName = mappedPropertyName ?? ("set" + name);
        return $"{receiver}{setterName}({valueText})";
    }

    // ── Indexer ─────────────────────────────────────────────────────────────

    private static string Indexer(ElementAccessExpressionSyntax ea, string setter, string getter,
        ExpressionSyntax rhs, ConversionContext c, string? compoundOp = null)
    {
        var f = ExpressionTransformerFacade.Instance;
        var target = f.Transform(ea.Expression, c);
        var args = string.Join(", ", ea.ArgumentList.Arguments.Select(x => f.Transform(x.Expression, c)));
        var val = f.Transform(rhs, c);
        if (compoundOp == null)
            return $"{target}.{setter}({args}, {val})";
        var compoundValue = $"{target}.{getter}({args}) {compoundOp[..^1]} {val}";
        // 窄元素类型（char/byte/short）：Java 二元运算提升为 int，setter 需要原类型。
        var narrowElement = c.GetTypeInfo(ea).Type?.SpecialType switch
        {
            SpecialType.System_Char => "char",
            SpecialType.System_Byte or SpecialType.System_SByte => "byte",
            SpecialType.System_Int16 => "short",
            _ => null,
        };
        if (narrowElement != null)
            compoundValue = $"({narrowElement}) ({compoundValue})";
        return $"{target}.{setter}({args}, {compoundValue})";
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
