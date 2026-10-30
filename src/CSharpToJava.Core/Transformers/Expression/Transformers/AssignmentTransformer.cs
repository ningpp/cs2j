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
        // IEnumerable<T> 赋值槽位：与参数/声明/返回位一致的载体适配
        // （真实链 CycleRemovalWithConstraints `constrainedEdges = constraints;`，
        // 生成 Set<T> 落进 CSharpGenericIterable<T> 字段）。
        rightText = IterableCoercion.CoerceOrSame(
            rhs, rightText, context.GetTypeInfo(lhs).Type, context);
        // IEnumerable<T> 槽位收到的流管道终端表达式：声明/参数位会包
        // CSharpGenericIterable.fromAnyTyped 适配视图（见
        // StatementTransformer.Declarations），赋值位同样必须包——否则 Stream
        // 表达式落进 CSharpGenericIterable 变量（javac: 不兼容的类型；真实链
        // ExtensionMethods.filterMethodBases 的二段 Where 链回填同名局部）。
        if (op == "="
            && rightText.Contains("StreamSupport.stream", StringComparison.Ordinal)
            && !rightText.Contains("CSharpGenericIterable", StringComparison.Ordinal)
            && context.GetTypeInfo(lhs).Type is INamedTypeSymbol { Name: "IEnumerable" } seqTarget
            && seqTarget.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
            && seqTarget.TypeArguments.Length == 1)
        {
            context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
            rightText = $"CSharpGenericIterable.<{context.MapType(seqTarget.TypeArguments[0])}>fromAnyTyped({rightText})";
        }
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

        var assignmentTargetType = context.GetTypeInfo(lhs).Type;
        var assignmentTargetMapped = assignmentTargetType != null
            ? context.MapType(assignmentTargetType)
            : null;
        if (op == "=" && assignmentTargetMapped is not null
            && assignmentTargetMapped.StartsWith("AbstractMap.SimpleImmutableEntry", StringComparison.Ordinal)
            && !rightText.TrimStart().StartsWith("new AbstractMap.SimpleImmutableEntry", StringComparison.Ordinal))
        {
            context.AddImport("java.util.AbstractMap");
            rightText = $"new AbstractMap.SimpleImmutableEntry<>"
                + $"({rightText}.getKey(), {rightText}.getValue())";
        }
        else if (op == "=" && assignmentTargetMapped is not null
            && assignmentTargetMapped is not ("Object" or "var"
                or "int" or "long" or "short" or "byte" or "char"
                or "boolean" or "float" or "double")
            && context.GetTypeInfo(rhs).Type?.SpecialType == SpecialType.System_Object
            && !rightText.TrimStart().StartsWith("(", StringComparison.Ordinal))
        {
            rightText = $"({assignmentTargetMapped}) ({rightText})";
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
        // 泛型实例化的 double/float 属性（ref-out-in 的 ByRef<double>.Value）：
        // Java setter 形参是装箱 Double，int 实参须显式加宽后再装箱。
        if (TypeOperationTransformer.IsBoxedFloatingSlot(p.Type, p.OriginalDefinition.Type))
        {
            right = TypeOperationTransformer.TryCoerceIntegralToFloating(
                a.Right, right, p.Type, c) ?? right;
        }

        // TypeMappings may rename the property's accessor pair (see
        // IdentifierExpressionTransformer.TransformMemberAccess for the read
        // side); only bean-style method names apply to the setter — field-style
        // renames (length/size) are consumed by the lowering pipeline instead.
        // 读取侧与读路径同名：field-style 映射（StringBuilder.Length → length）
        // 只用于 getter；setter 保持 bean 风格（真实链 XsdDuration 的
        // sb.Length += 9 → sb.setLength(sb.length() + 9)，旧代码两侧都派生
        // get/set 前缀，getLength() 在 java.lang.StringBuilder 上不存在）。
        var rawMappedProperty = c.TypeMappings.MapMethod(p.ContainingType.ToDisplayString(), p.Name);
        var mappedPropertyName = JavaNaming.IsBeanAccessorStyleName(rawMappedProperty)
            ? rawMappedProperty
            : null;
        var fieldStyleGetter = !string.IsNullOrEmpty(rawMappedProperty)
            && rawMappedProperty != p.Name
            && rawMappedProperty is "length" or "size"
            ? rawMappedProperty
            : null;
        var getterName = mappedPropertyName ?? fieldStyleGetter ?? ("get" + name);
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
        var containingType = p.ContainingType.ToDisplayString();
        var mappedProperty = c.TypeMappings.MapMethod(containingType, p.Name);
        var mappedPropertyName = JavaNaming.IsBeanAccessorStyleName(mappedProperty)
            ? mappedProperty
            : null;

        // 读取侧映射可能是字段风格 getter（StringBuilder.Length → length），它不能
        // 当作 setter 调用；setter 的真实名字由显式访问器映射 set_<Name> 提供
        // （StringBuilder.set_Length → setLength）。显式 setter 优先于属性名映射，
        // 保证 sb.Length = 61 落成 sb.setLength(61)，而不是 sb.length(61)。
        var explicitSetter = c.TypeMappings.MapMethod(containingType, "set_" + p.Name);
        if (!string.IsNullOrEmpty(explicitSetter)
            && !explicitSetter.Contains('.')
            && !JavaNaming.IsFieldStyleAccessorName(explicitSetter))
        {
            return $"{receiver}{explicitSetter}({valueText})";
        }

        // Ported BCL types (dotnet.xml XmlReaderSettings/XmlWriterSettings) expose
        // C#-style property setters as same-named methods: IgnoreComments = true
        // is IgnoreComments(true), not setIgnoreComments(true). A non-bean mapped
        // name is a direct setter method; field-style getter names (length/size/
        // count) are read-only spellings, and helper-style mappings (dotted) keep
        // the fallback below.
        var directSetter = mappedProperty;
        if (!string.IsNullOrEmpty(directSetter)
            && !directSetter.Contains('.')
            && !JavaNaming.IsBeanAccessorStyleName(directSetter)
            && !JavaNaming.IsFieldStyleAccessorName(directSetter))
        {
            return $"{receiver}{directSetter}({valueText})";
        }

        // 分布在移植类上的 BCL 属性（System.Xml.XmlElement → dotnet.xml.XmlElement）：
        // 移植类的属性已被 member-desugar 展开为 set_InnerText 方法并发射成
        // InnerText(value)，Java bean setter setInnerText() 不存在（真实链
        // root-tests CorrectnessTest setInnerText 编译错误）。与读取侧
        // (IdentifierExpressionTransformer line 597) 对称：只有确实是该形态
        //（get_<Name> 方法在源内声明，同名属性符号不存在）才切换。
        if (mappedPropertyName == null
            && !p.IsStatic
            && c.ExposesCSharpStyleProperty(p.ContainingType, p.Name))
        {
            return $"{receiver}{p.Name}({valueText})";
        }

        var setterName = mappedPropertyName ?? ("set" + name);
        return $"{receiver}{setterName}({valueText})";
    }

    // ── Indexer ─────────────────────────────────────────────────────────────

    private static string Indexer(ElementAccessExpressionSyntax ea, string setter, string getter,
        ExpressionSyntax rhs, ConversionContext c, string? compoundOp = null)
    {
        var f = ExpressionTransformerFacade.Instance;
        var target = f.Transform(ea.Expression, c);
        // Dictionary/Map indexers are generic in Java: the key and value slots
        // are the boxed wrappers, so C# implicit integral → double conversions
        // (d[0] = v on Dictionary<double, V>) need an explicit cast.
        var isMap = IsDictionary(ea.Expression, c);
        var indexer = c.GetSymbolInfo(ea).Symbol as IPropertySymbol;
        var args = string.Join(", ", ea.ArgumentList.Arguments.Select((x, i) =>
        {
            var text = f.Transform(x.Expression, c);
            if (!isMap || indexer == null || i >= indexer.Parameters.Length)
                return text;
            return TypeOperationTransformer.TryCoerceIntegralToFloating(
                x.Expression, text, indexer.Parameters[i].Type, c) ?? text;
        }));
        var val = f.Transform(rhs, c);
        if (compoundOp == null && isMap && indexer != null)
        {
            val = TypeOperationTransformer.TryCoerceIntegralToFloating(
                rhs, val, indexer.Type, c) ?? val;
        }
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
