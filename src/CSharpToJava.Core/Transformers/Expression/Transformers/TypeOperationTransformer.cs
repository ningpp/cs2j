using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Type;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>Handles cast / is / as / typeof / default / checked / unchecked / sizeof.</summary>
[TransformerRegistration]
public class TypeOperationTransformer : IIRExpressionTransformer
{
    static TypeOperationTransformer() => ExpressionTransformerRegistry.Register(new[]
    {
        SyntaxKind.CastExpression, SyntaxKind.IsExpression, SyntaxKind.IsPatternExpression, SyntaxKind.AsExpression,
        SyntaxKind.TypeOfExpression, SyntaxKind.DefaultExpression, SyntaxKind.DefaultLiteralExpression,
        SyntaxKind.CheckedExpression, SyntaxKind.UncheckedExpression, SyntaxKind.SizeOfExpression
    }, new TypeOperationTransformer());
    private static readonly Lazy<TypeOperationTransformer> _instance = new(() => new());
    public static TypeOperationTransformer Instance => _instance.Value;
    public JavaExpression TransformToIR(ExpressionSyntax n, ConversionContext c) => new JavaRawExpression(Transform(n, c));
    public string Transform(ExpressionSyntax n, ConversionContext c) => n switch
    {
        CastExpressionSyntax x => Cast(x, c),
        BinaryExpressionSyntax x when x.IsKind(SyntaxKind.IsExpression) => Is(x, c),
        BinaryExpressionSyntax x when x.IsKind(SyntaxKind.AsExpression) => As(x, c),
        IsPatternExpressionSyntax x => IsPattern(x, c),
        TypeOfExpressionSyntax x => TypeOf(x, c),
        DefaultExpressionSyntax x => GetDefaultValueForType(Map(x.Type, c), c.GetTypeInfo(x.Type).Type, c),
        CheckedExpressionSyntax x => (x.IsKind(SyntaxKind.CheckedExpression) ? "/* checked */ " : "") + Expr(c, x.Expression),
        SizeOfExpressionSyntax x => SizeOf(x, c),
        _ when n.IsKind(SyntaxKind.DefaultLiteralExpression) =>
            c.GetTypeInfo(n).ConvertedType is { } t ? GetDefaultValueForType(c.MapType(t), t, c) : "null",
        _ => throw new NotSupportedException($"Type operation kind {n.Kind()} not supported.")
    };
    private static string Expr(ConversionContext c, ExpressionSyntax n) => ExpressionTransformerFacade.Instance.Transform(n, c);
    private static string Map(TypeSyntax ts, ConversionContext c)
    {
        var s = c.GetTypeInfo(ts).Type;
        return s is null or IErrorTypeSymbol ? c.MapTypeFromSyntax(ts) : c.MapType(s);
    }
    // Runtime type form: strip generic arguments and box primitives (for instanceof / .class).
    private static string Rt(string t)
    {
        var s = t.IndexOf('<') is int i && i >= 0 ? t[..i] : t;
        return s switch
        {
            "int" => "Integer", "long" => "Long", "short" => "Short", "byte" => "Byte",
            "double" => "Double", "float" => "Float", "char" => "Character", "boolean" => "Boolean", _ => s
        };
    }

    /// <summary>
    /// Class literal name usable at a cast/<c>as</c> site: the raw (erased)
    /// type for generic instantiations, the component type plus <c>[]</c> for
    /// arrays. Null when no class literal exists (primitive values, type
    /// parameters, generic-array element types).
    /// </summary>
    private static string? RuntimeClassName(ITypeSymbol? type, ConversionContext c)
    {
        switch (type)
        {
            case null:
                return null;
            case IArrayTypeSymbol { Rank: 1 } array:
                return RuntimeClassName(array.ElementType, c) is { } element ? $"{element}[]" : null;
            case ITypeParameterSymbol:
                return null;
            case INamedTypeSymbol named:
                if (named.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64
                    or SpecialType.System_Int16 or SpecialType.System_Byte or SpecialType.System_SByte
                    or SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_UInt64
                    or SpecialType.System_Char or SpecialType.System_Double or SpecialType.System_Single
                    or SpecialType.System_Boolean or SpecialType.System_Decimal)
                {
                    return null;
                }
                var mapped = Rt(c.MapType(named));
                return mapped.Length > 0 && !mapped.Contains('<') ? mapped : null;
            default:
                return null;
        }
    }
    private static string Cast(CastExpressionSyntax n, ConversionContext c)
    {
        var e = Expr(c, n.Expression);
        var sym = c.GetTypeInfo(n.Type).Type;
        var srcSym = c.GetTypeInfo(n.Expression).Type;
        if (sym is IPointerTypeSymbol) return e; // all pointers lower to MemorySegment

        // C# 方法组到委托的显式转换（用户代码或 goto 消除产物，真实链
        // ReaderCreateUtil 的 `(Func<CharType,string>)UnicodeCharHelper.GetValidCharacters`）
        // 落函数式接口方法引用——Java 的 cast 不可用（javac 找不到符号 变量 X）。
        if (sym is INamedTypeSymbol { TypeKind: TypeKind.Delegate }
            && c.GetSymbolInfo(n.Expression).Symbol is IMethodSymbol
            && ArgumentTransformer.TryEmitDelegateMethodReference(n.Expression, sym, c, out var castMethodRef))
        {
            return castMethodRef;
        }

        // C# user-defined implicit/explicit conversions are emitted as static
        // toXxx(...) methods on the declaring type (OperatorTransformer
        // .TransformConversion); Java has no user-defined casts, so a bare
        // `(Target)(expr)` would not compile (real chain: DatatypeImplementation's
        // `(DatatypeImplementation)s_xdrTypes[i]` over its SchemaDatatypeMap
        // explicit operator).
        var castModel = c.GetSemanticModelForNode(n);
        if (castModel != null && sym != null)
        {
            var userDefined = castModel.ClassifyConversion(n.Expression, sym);
            if (userDefined.IsUserDefined
                && userDefined.MethodSymbol is IMethodSymbol convMethod
                // 源内声明的转换运算符才有 OperatorTransformer 生成的 toXxx 静态方法；
                // 元数据类型的转换由 MemberDesugar.Operators 预处理管线的自有命名降形，
                // 不进入本分支（保持原有直译行为）。
                && convMethod.ContainingType.Locations.Any(l => l.IsInSource))
            {
                var containing = c.MapType(convMethod.ContainingType);
                var methodName = $"to{Member.OperatorTransformer.BuildConversionMethodSuffix(c.MapType(convMethod.ReturnType))}";
                return $"{containing}.{methodName}({e})";
            }
        }

        // C# casts to IEnumerable<T> become compat adapters: generated collections
        // may implement the non-generic interfaces only, and Java cannot prove a
        // class→interface cast is valid in all shapes.
        if (sym is INamedTypeSymbol { Name: "IEnumerable", TypeArguments.Length: 1 } enumerableCast
            && enumerableCast.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic")
        {
            c.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
            var element = c.MapType(enumerableCast.TypeArguments[0]);
            return $"CSharpGenericIterable.<{element}>fromAnyTyped({e})";
        }

        // C# 从 IEnumerable 家族向具体集合/数组的向下转换（C# 语义是运行时类型
        // 检查）：兼容载体是独立接口，生成集合类并不实现它，Java 侧两个映射类型
        // 不可转换。先经适配器还原原始值再落 Java 强转（真实链 MSAGL
        // `inEdges_ = (Set<Edge>)value;`、`((Set<Edge>)(node.OutEdges())).clear()`）。
        if (sym != null && srcSym != null
            && IterableCoercion.IsGenericIterableSlot(srcSym, c)
            && !IterableCoercion.IsGenericIterableSlot(sym, c)
            && c.MapType(sym) is { Length: > 0 } castTarget
            && castTarget is not ("int" or "long" or "short" or "byte" or "char" or "double" or "float" or "boolean"))
        {
            c.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
            return $"({castTarget}) CSharpGenericIterable.unwrap({e})";
        }

        // Casts to the __suppress__ sentinel (ISerializable et al.): the sentinel
        // is an interface, but the source is often a FINAL generated class —
        // javac rejects a provably-disjoint final-class→interface cast. Routing
        // through Object keeps the cast legal (it fails at runtime only when the
        // class really does not implement the sentinel, which the converted
        // assertion code expects as an exception anyway).
        if (sym != null && c.MapType(sym) == "__suppress__")
        {
            c.AddImport("io.github.ningpp.compat.__suppress__");
            return $"(__suppress__)(Object)({e})";
        }

        // C# implicit array/string → Span<T>/ReadOnlySpan<T> conversions become
        // explicit casts in the intermediate tree; the compat types are plain
        // classes, so render the construction/asSpan bridge instead of a Java cast.
        if (SpanCoercion.TryCoerce(n.Expression, e, sym, c, out var spanCast))
            return spanCast;

        // Plain enum casts (including C# lifted Nullable<E> casts, which map to
        // the same Java enum reference type). Ordinal enums: (E)i ->
        // E.values()[i], (int)e -> e.ordinal(). Value enums (explicit values /
        // non-int underlying): fromValue/getValue.
        if (TryUnwrapNullableEnum(sym, out var targetEnum))
        {
            // (E)null / (E?)null: the Java enum reference itself is nullable,
            // so the bare null literal passes through unchanged.
            if (n.Expression.IsKind(SyntaxKind.NullLiteralExpression))
                return e;
            var target = Map(n.Type, c);
            var richTarget = EnumTransformer.IsValueEnum(targetEnum);
            var targetStorage = EnumTransformer.StorageTypeName(
                targetEnum.EnumUnderlyingType?.SpecialType ?? SpecialType.System_Int32);
            // 元数据 flags 枚举（BindingFlags/MethodAttributes 等）由 compat 以 int
            // 常量提供，Java 侧没有值类：转成底层整数即可。
            if (EnumTransformer.IsIntConstantMetadataEnum(targetEnum))
                return $"({targetStorage})({e})";
            if (TryUnwrapNullableEnum(srcSym, out var sourceEnum))
            {
                if (SymbolEqualityComparer.Default.Equals(sourceEnum, targetEnum))
                    return e;
                // 枚举互转按 C# 语义取**数值**：源枚举若带显式值（value enum，
                // 如 TextBlockType.Text=4），Java ordinal() 是声明序（Text=1），
                // 拿它索引目标 values() 会错位（真实链 XPathDocumentBuilder：
                // Whitespace(ordinal 3) 被索引成 Namespace，文本节点类型全乱，
                // xpath-tests/xpathdocument-tests 各 593 例失败）。
                var sourceIndex = EnumTransformer.IsValueEnum(sourceEnum)
                    ? $"{e}.getValue()"
                    : $"{e}.ordinal()";
                if (!richTarget)
                    return $"{target}.values()[{sourceIndex}]";
                return $"{target}.fromValue(({targetStorage}){sourceIndex})";
            }
            // C# unboxing from object is type-strict: `(E)obj` succeeds only when obj
            // holds a boxed E (never a boxed int — that needs `(E)(int)obj`). Lower
            // the object-typed source to a plain Java reference cast.
            if (srcSym?.SpecialType == SpecialType.System_Object)
                return $"({target})({e})";
            if (richTarget)
                return $"{target}.fromValue(({targetStorage}){e})";
            var index = srcSym?.SpecialType is SpecialType.System_Int64 or SpecialType.System_UInt64
                ? $"(int)({e})"
                : e;
            return $"{target}.values()[{index}]";
        }
        if (TryUnwrapNullableEnum(srcSym, out var srcEnum))
            e = EnumTransformer.IsIntConstantMetadataEnum(srcEnum)
                ? e
                : EnumTransformer.IsValueEnum(srcEnum)
                    ? $"{e}.getValue()"
                    : $"{e}.ordinal()";

        // C# numeric-to-decimal conversions (C34 DC-9 operand promotion emits
        // them explicitly): Java has no primitive->reference cast, so they must
        // become factory calls against the compat Decimal. valueOf(int)/(long)/
        // (double) cover the signed and narrow sources (char/short/byte widen to
        // the int overload); real uint/ulong keep their magnitude through the
        // unsigned helpers (the Java int/long bit pattern is reinterpreted there).
        // An identity (decimal)d cast is dropped.
        if (sym?.SpecialType == SpecialType.System_Decimal)
        {
            if (srcSym?.SpecialType == SpecialType.System_Decimal)
                return e;
            if (srcSym?.SpecialType is SpecialType.System_UInt32)
            {
                c.AddImport("io.github.ningpp.compat.UnsignedMath");
                return $"UnsignedMath.toDecimalUInt32({e})";
            }
            if (srcSym?.SpecialType is SpecialType.System_UInt64)
            {
                c.AddImport("io.github.ningpp.compat.UnsignedMath");
                return $"UnsignedMath.toDecimalUInt64({e})";
            }
            if (srcSym is null || IsNumericPromotableToDecimal(srcSym))
            {
                c.AddImport("io.github.ningpp.compat.Decimal");
                return $"Decimal.valueOf({e})";
            }
        }

        // Unboxing a Nullable<T> primitive: C# throws InvalidOperationException
        // (mapped to IllegalStateException) on null rather than NPE. Route
        // through the compat guard, then cast to the target primitive (covers
        // narrowing such as (short)int?).
        if (srcSym is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableSource
            && sym is not null && IsPrimitiveSpecialType(sym.SpecialType))
        {
            c.AddImport("io.github.ningpp.compat.Nullable");
            var targetPrimitive = Map(n.Type, c);
            return IsJavaPrimitive(targetPrimitive)
                ? $"({targetPrimitive})(Nullable.value({e}))"
                : $"Nullable.value({e})";
        }

        // Unboxing: a reference-typed (or boxed/Nullable/dynamic) operand cast to
        // a primitive must become a cast to the boxed wrapper; Java then unboxes
        // via assignment/binary context. Dropping the cast entirely, as the
        // primitive-to-primitive branch below does, yields `int x = objectExpr`,
        // which does not compile (e.g. non-generic IEnumerator foreach lowering
        // emits `int item = (int)enumerator.Current`).
        if (srcSym is not null && sym is not null
            && sym.SpecialType == SpecialType.System_Int32
            && srcSym.SpecialType == SpecialType.System_Double)
        {
            return $"((Double)({e})).intValue()";
        }

        if (srcSym is not null && sym is not null
            && IsPrimitiveSpecialType(sym.SpecialType)
            && IsBoxedOrReferenceSource(srcSym))
        {
            return $"({BoxedWrapperName(sym.SpecialType)})({e})";
        }

        // (int[])objectExpr: C# Array.CreateInstance(typeof(int), n) returns
        // int[], and (int[])objectExpr unboxes it. In Java, the runtime value
        // is Integer[] or Object[] (from the generic ToArray method), which
        // cannot be cast to int[]. Route through CSharpArray.toPrimitiveArray
        // to unbox each element (real chain: XmlReader.readContentAs(int[].class)).
        if (sym is IArrayTypeSymbol primArr
            && IsPrimitiveSpecialType(primArr.ElementType.SpecialType)
            && srcSym is not null && IsBoxedOrReferenceSource(srcSym)
            && srcSym.SpecialType != SpecialType.System_Array)
        {
            var t = Map(n.Type, c);
            c.AddImport("io.github.ningpp.compat.CSharpArray");
            return $"CSharpArray.toPrimitiveArray({e}, {t}.class)";
        }

        // IList<T>/IEnumerable<T> -> T[]: C# allows the explicit array cast, but
        // CSharpIList/CSharpList are not Java arrays. Materialize through
        // CSharpArray.toTypedArray (which now accepts Iterable sources).
        if (sym is IArrayTypeSymbol { Rank: 1 } listArr
            && srcSym is not IArrayTypeSymbol
            && srcSym is not null
            && c.MapType(srcSym) is var srcMapped
            && (srcMapped.StartsWith("CSharpIList", StringComparison.Ordinal)
                || srcMapped.StartsWith("CSharpList", StringComparison.Ordinal)
                || srcMapped.StartsWith("CSharpGenericIterable", StringComparison.Ordinal)))
        {
            var targetArray = Map(n.Type, c);
            var element = c.MapType(listArr.ElementType);
            c.AddImport("io.github.ningpp.compat.CSharpArray");
            return $"({targetArray}) CSharpArray.toTypedArray({e}, {element}.class)";
        }

        // System.Array → T[]：源经 array-method 归一为 CSharpArray 视图（现在
        // 包括 CSharpArray.createInstance 的返回值），Java 不接受 CSharpArray→T[]
        // 的直接强转（javac 不可转换对；真实链解码器 `_buffer = (byte[])buffer`
        // 3 处），经 tryAs 解包（null 安全，与 C# 失配抛异常相比静默返回 null，
        // 调用方均有 Debug.Assert 保护）。类型参数元素无运行时 class（T[].class
        // 非法）：以 Object[].class 解包后按擦除形态强转——(T[])Object[] 是合法
        // unchecked 强转（真实链 XmlListConverter/ArrayBuilder 的
        // (T[])Array.CreateInstance(typeof(T), n)，compat createInstance 现返回
        // CSharpArray 包装视图）。
        if (sym is IArrayTypeSymbol castArr
            && srcSym?.SpecialType == SpecialType.System_Array)
        {
            var t = Map(n.Type, c);
            c.AddImport("io.github.ningpp.compat.CSharpArray");
            if (castArr.ElementType is ITypeParameterSymbol || t.Contains('<'))
            {
                return $"({t})(CSharpArray.tryAs({e}, Object[].class))";
            }
            return $"CSharpArray.tryAs({e}, {t}.class)";
        }

        // T[] → System.Array（C# 数组的隐式上转经显式 (Array) 修饰落在此处）：
        // Java 数组与 compat CSharpArray 是互不相关的类型，直接 (CSharpArray)
        // 强转不可编译（真实链 QUT ShiftReduceParser 的
        // Array.Copy((Array)this.array, (Array)objArray, n)）。经写通视图
        // asArrayView 包装——compat 的 ArrayHelper.copy(Object,Object,int)
        // 解包视图后即 System.arraycopy，与 C# Array.Copy 同语义。
        if (sym?.SpecialType == SpecialType.System_Array
            && srcSym is IArrayTypeSymbol)
        {
            c.AddImport("io.github.ningpp.compat.CSharpArray");
            return $"CSharpArray.asArrayView({e})";
        }

        // C# DateTimeOffset ← DateTime 隐式转换（元数据 op_Implicit，非源内
        // 声明，上面的 user-defined 分支不接；真实链 XmlDateTimeConverter 的
        // (DateTimeOffset)atomicValue.ValueAsDateTime()）。
        if (sym is INamedTypeSymbol { SpecialType: SpecialType.None } dateTimeOffsetTarget
            && dateTimeOffsetTarget.ToDisplayString() == "System.DateTimeOffset"
            && srcSym is INamedTypeSymbol { SpecialType: SpecialType.System_DateTime })
        {
            c.AddImport("io.github.ningpp.compat.CSharpDateTimeOffset");
            return $"CSharpDateTimeOffset.fromDateTime({e})";
        }

        // C# decimal → 整型/浮点的显式收窄（真实链 XmlValueConverter 的
        // DecimalToInt32/DecimalToInt64 `(long)value`、XmlUntypedStringConverter
        // `(double)` 等，7 站）：Java 无引用 Decimal→基元强转，走 Decimal 的
        // value 方法——setScale(0, DOWN)+*Exact 的截断向零语义与 C# decimal
        // 显式转换一致（越界抛异常对应 C# 恒 checked 的 decimal→integral）。
        if (srcSym?.SpecialType == SpecialType.System_Decimal
            && sym?.SpecialType is SpecialType.System_Int32
                or SpecialType.System_Int64
                or SpecialType.System_Single
                or SpecialType.System_Double)
        {
            var valueMethod = sym.SpecialType switch
            {
                SpecialType.System_Int32 => "intValue",
                SpecialType.System_Int64 => "longValue",
                SpecialType.System_Single => "floatValue",
                _ => "doubleValue",
            };
            return $"({e}).{valueMethod}()";
        }
        // decimal → ulong：long 载体的无符号位型经 BigDecimal 截断取低 64 位
        //（生成代码在调用点已有 [0, 2^64) 域检查，如 DecimalToUInt64）。
        if (srcSym?.SpecialType == SpecialType.System_Decimal
            && sym?.SpecialType == SpecialType.System_UInt64)
        {
            c.AddImport("io.github.ningpp.compat.UnsignedMath");
            return $"UnsignedMath.toUInt64Decimal({e})";
        }

        return sym?.SpecialType switch
        {
            SpecialType.System_Int32 when NeedsExplicitIntCast(srcSym) => $"(int)({e})",
            SpecialType.System_Int32 => e,
            SpecialType.System_Byte => $"({e} & 0xFF)",                                   // unsigned narrowing → mask
            SpecialType.System_UInt16 => $"(((int)({e})) & 0xFFFF)",
            // uint 重写面是 int 载体+掩码；来源为装箱/引用形态时必须先解箱：
            // `(uint)objectExpr` 直接落 `(expr & 0xFFFFFFFFL)` 会让 javac 报
            // 「二元运算符 '&' 的操作数类型错误」（真实链 Datatype_unsignedInt
            // .compare 的 ((uint)value1).CompareTo((uint)value2) 等，9 站）。
            // Integer 在二元数值上下文自动解箱（JLS 5.6），与 int 载体路径同语义。
            SpecialType.System_UInt32 when srcSym is not null && IsBoxedOrReferenceSource(srcSym)
                => $"(int)(((Integer)({e})) & 0xFFFFFFFFL)",
            SpecialType.System_UInt32 => $"(int)(({e}) & 0xFFFFFFFFL)",
            // (float)doubleExpr 的内层解箱已产出 (Double)(obj) 形态：javac 拒绝
            // Double→float 强转组合（JLS 5.5；真实链 XmlNumeric2Converter 的
            // (float)value），经 floatValue() 取值。
            SpecialType.System_Single when srcSym?.SpecialType == SpecialType.System_Double
                && e.StartsWith("(Double)", StringComparison.Ordinal)
                => $"({e}).floatValue()",
            _ => $"({Map(n.Type, c)})({e})"
        };
    }

    /// <summary>
    /// Unwraps a type that is either an enum or a lifted <see cref="Nullable{T}"/>
    /// whose underlying type is an enum. Both lower to the same Java enum
    /// reference type, so casts between them are identity conversions.
    /// </summary>
    private static bool TryUnwrapNullableEnum(ITypeSymbol? type, out INamedTypeSymbol enumType)
    {
        if (type is INamedTypeSymbol named && named.TypeKind == TypeKind.Enum)
        {
            enumType = named;
            return true;
        }
        if (type is INamedTypeSymbol nullable
            && nullable.IsValueType
            && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && nullable.TypeArguments is [INamedTypeSymbol { TypeKind: TypeKind.Enum } inner])
        {
            enumType = inner;
            return true;
        }
        enumType = null!;
        return false;
    }

    /// <summary>
    /// Java requires an explicit narrowing cast for these → int conversions;
    /// all other int casts (byte/short/char/int widening) are implicit.
    /// </summary>
    private static bool NeedsExplicitIntCast(ITypeSymbol? source)
        => source?.SpecialType is SpecialType.System_Single
            or SpecialType.System_Double
            or SpecialType.System_Int64
            or SpecialType.System_UInt64;

    private static bool IsPrimitiveSpecialType(SpecialType special)
        => special is SpecialType.System_Boolean
            or SpecialType.System_Int32
            or SpecialType.System_Int64
            or SpecialType.System_Int16
            or SpecialType.System_SByte
            or SpecialType.System_Byte
            or SpecialType.System_Char
            or SpecialType.System_Single
            or SpecialType.System_Double;

    private static bool IsJavaPrimitive(string? name)
        => name is "boolean" or "int" or "long" or "short" or "byte"
            or "char" or "float" or "double";

    /// <summary>
    /// A cast source whose Java representation is a reference (needs a wrapper
    /// cast before unboxing): reference types, boxed value types, Nullable
    /// wrappers, and dynamic.
    /// </summary>
    private static bool IsBoxedOrReferenceSource(ITypeSymbol source)
        => source.IsReferenceType
            || source.TypeKind == TypeKind.Dynamic
            || (source is INamedTypeSymbol named
                && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T);

    private static string BoxedWrapperName(SpecialType special)
        => special switch
        {
            SpecialType.System_Boolean => "Boolean",
            SpecialType.System_Int32 => "Integer",
            SpecialType.System_Int64 => "Long",
            SpecialType.System_Int16 => "Short",
            SpecialType.System_SByte => "Byte",
            // C# byte maps to Java int at runtime (unsigned 8 → signed 32),
            // so unboxing casts must target Integer, not Byte — the runtime
            // value is an Integer box. (Byte is only used for typeof(byte)
            // type-key distinction in TypeOf.)
            SpecialType.System_Byte => "Integer",
            SpecialType.System_UInt16 => "Integer",
            SpecialType.System_Char => "Character",
            SpecialType.System_Single => "Float",
            SpecialType.System_Double => "Double",
            _ => "Object"
        };

    /// <summary>
    /// Numeric sources whose rendered Java primitive converts losslessly through
    /// <c>Decimal.valueOf</c> (and enums via ordinal). Real uint/ulong are handled
    /// separately via the unsigned-magnitude helpers and must not appear here.
    /// </summary>
    private static bool IsNumericPromotableToDecimal(ITypeSymbol source)
        => source.SpecialType is SpecialType.System_SByte
            or SpecialType.System_Byte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_Int64
            or SpecialType.System_Char
            or SpecialType.System_Single
            or SpecialType.System_Double
            || source.TypeKind == TypeKind.Enum;

    /// <summary>
    /// True when a target slot declared as <c>double</c>/<c>float</c> is
    /// represented by the Java wrapper type instead of the primitive: the slot
    /// is a generic instantiation (a type parameter substituted with double —
    /// <c>ByRef&lt;double&gt;.Value</c>, <c>List&lt;double&gt;.Add</c>,
    /// <c>Dictionary&lt;double, V&gt;.ContainsKey</c>) or a
    /// <c>Nullable&lt;double&gt;</c>. Java widens int→double only while the
    /// target stays primitive; at these reference-typed slots the value must
    /// be cast before javac will box it.
    /// </summary>
    public static bool IsBoxedFloatingSlot(ITypeSymbol? targetType, ITypeSymbol? originalDefinitionType)
    {
        var unwrapped = UnwrapNullable(targetType);
        if (unwrapped?.SpecialType is not (SpecialType.System_Double or SpecialType.System_Single))
            return false;
        return originalDefinitionType is ITypeParameterSymbol
            || targetType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
    }

    /// <summary>
    /// C# implicit integral → double/float conversion at a slot that Java
    /// represents with the boxed wrapper. The C# conversion widens while the
    /// value is still primitive and only then boxes; Java applies no widening
    /// in a reference-typed invocation/assignment context, so the emitted
    /// argument needs an explicit cast (<c>setValue(0)</c> →
    /// <c>setValue((double) (0))</c>). Returns null when no coercion applies.
    /// </summary>
    public static string? TryCoerceIntegralToFloating(
        ExpressionSyntax value, string text, ITypeSymbol? targetType, ConversionContext context)
    {
        var target = UnwrapNullable(targetType);
        if (target?.SpecialType is not (SpecialType.System_Double or SpecialType.System_Single))
            return null;

        // The source must already be a Java primitive carrier: a nullable
        // integral (Integer) cannot be widened by a (double) cast, so leave it
        // to the dedicated nullable lowering.
        var source = context.GetTypeInfo(value).Type;
        if (source?.SpecialType is not (SpecialType.System_SByte
            or SpecialType.System_Byte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_Int64
            or SpecialType.System_Char))
        {
            // uint/ulong carriers are excluded: their Java int/long carrier may
            // hold a wrapped bit pattern, so a direct (double) cast would not
            // reproduce the C# unsigned value.
            return null;
        }

        var cast = target.SpecialType == SpecialType.System_Single ? "float" : "double";
        if (text.TrimStart().StartsWith($"({cast})", StringComparison.Ordinal))
            return null;
        return $"({cast}) ({text})";
    }

    private static ITypeSymbol? UnwrapNullable(ITypeSymbol? type)
        => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
           && nullable.TypeArguments.Length == 1
            ? nullable.TypeArguments[0]
            : type;

    /// <summary>
    /// Implicit C# numeric → decimal conversion at an assignment/argument slot
    /// whose target type is System.Decimal (real chains: SchemaSetCompiler's
    /// <c>virtualSeq.MinOccurs = 1</c>, Exporter's <c>int minOccurs</c> into
    /// <c>element.MinOccurs</c>, Numeric10FacetsChecker(long.MinValue, ...)).
    /// Java has no primitive→reference promotion, so the value must go through
    /// the compat Decimal factory: valueOf for promotable/narrow sources,
    /// UnsignedMath magnitude helpers for real uint/ulong. Returns null when
    /// the target is not decimal or the source is already decimal.
    /// </summary>
    public static string? TryCoerceToDecimal(ExpressionSyntax value, string text, ITypeSymbol? targetType, ConversionContext context)
    {
        if (targetType?.SpecialType != SpecialType.System_Decimal)
            return null;
        var sourceType = context.GetTypeInfo(value).Type;
        if (sourceType?.SpecialType == SpecialType.System_Decimal)
            return null;
        if (sourceType is not null)
        {
            if (sourceType.SpecialType == SpecialType.System_UInt32)
            {
                // uint literals above int range render with an L suffix; the
                // (int) cast is an identity for already-int values and keeps the
                // bit pattern for the L-literal ones (compat takes the raw pattern).
                context.AddImport("io.github.ningpp.compat.UnsignedMath");
                return $"UnsignedMath.toDecimalUInt32((int)({text}))";
            }
            if (sourceType.SpecialType == SpecialType.System_UInt64)
            {
                context.AddImport("io.github.ningpp.compat.UnsignedMath");
                return $"UnsignedMath.toDecimalUInt64({text})";
            }
            if (!IsNumericPromotableToDecimal(sourceType))
                return null;
        }
        context.AddImport("io.github.ningpp.compat.Decimal");
        return $"Decimal.valueOf({text})";
    }
    private static string Is(BinaryExpressionSyntax n, ConversionContext c)
    {
        var left = Expr(c, n.Left);
        if (c.GetTypeInfo(n.Right).Type is ITypeParameterSymbol tp)
            return $"{TypeParameterToken(n, tp, c)}.isInstance({left})"; // Java forbids instanceof T
        // System.Array 装载的数组经 array-method 归一为 CSharpArray 视图；
        // `x is T[]` 的 instanceof 对 CSharpArray 恒为 false（且 javac 拒绝
        // CSharpArray→T[] 的不可转换比较），须经 tryAs 解包判定。
        // 类型参数元素除外：T[].class 非法（javac 无法从类型变量中进行选择）。
        if (c.GetTypeInfo(n.Right).Type is IArrayTypeSymbol isArr
            && isArr.ElementType is not ITypeParameterSymbol
            && c.GetTypeInfo(n.Left).Type?.SpecialType == SpecialType.System_Array)
        {
            var isT = Map((TypeSyntax)n.Right, c);
            if (!isT.Contains('<'))
            {
                c.AddImport("io.github.ningpp.compat.CSharpArray");
                return $"CSharpArray.tryAs({left}, {isT}.class) != null";
            }
        }
        return $"{left} instanceof {Rt(Map((TypeSyntax)n.Right, c))}";
    }
    private static string As(BinaryExpressionSyntax n, ConversionContext c)
    {
        var e = Expr(c, n.Left);
        if (n.Left is InvocationExpressionSyntax or ConditionalAccessExpressionSyntax
            or ElementAccessExpressionSyntax or AssignmentExpressionSyntax)
        {
            var tmp = c.GenerateSyntheticName("_asExpr"); // hoist side effects, evaluate once
            var hoisted = $"var {tmp} = {e};";
            if (n.Right is ArrayTypeSyntax
                && IsPropertyGetValueInvocation(n.Left))
            {
                // PropertyInfo.getValue emulates .NET's Array.ToString() by wrapping array
                // results in an ArrayPropertyWrapper; C# `GetValue(...) as T[]` sees the raw
                // runtime array, so the hoisted value unwraps before the instanceof/cast.
                c.AddImport("io.github.ningpp.compat.PropertyInfo");
                hoisted = $"var {tmp} = PropertyInfo.unwrapArrayValue({e});";
            }

            c.AddPreStatement(hoisted);
            e = tmp;
        }
        var t = Map((TypeSyntax)n.Right, c);
        var asSrcType = c.GetTypeInfo(n.Left).Type ?? c.GetTypeInfo(n.Left).ConvertedType;

        // `x as IEnumerable<T>` from a value C# knows to be enumerable always
        // succeeds: adapt instead of running a Java instanceof the compat
        // carriers never satisfy (real chain MSAGL `feedbackSet as IEnumerable<IEdge>`
        // over a Set<T> value).
        if (asSrcType != null && !asSrcType.IsValueType
            && IterableCoercion.IsGenericIterableSlot(c.GetTypeInfo(n.Right).Type, c)
            && !IterableCoercion.IsGenericIterableSlot(asSrcType, c)
            && asSrcType.AllInterfaces.Concat(new[] { asSrcType }).Any(i =>
                i.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IEnumerable<T>"))
        {
            c.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
            var element = c.GetTypeInfo(n.Right).Type is INamedTypeSymbol { TypeArguments.Length: 1 } asTarget
                ? c.MapType(asTarget.TypeArguments[0])
                : "Object";
            return $"CSharpGenericIterable.<{element}>fromAnyTyped({e})";
        }

        // `x as ConcreteCollection/T[]` out of the carrier world: unwrap the
        // compat adapter first and re-check the runtime type (real chain MSAGL
        // `pushingNodes as GeomNode[]`).
        if (asSrcType != null
            && IterableCoercion.IsGenericIterableSlot(asSrcType, c)
            && !IterableCoercion.IsGenericIterableSlot(c.GetTypeInfo(n.Right).Type, c)
            && RuntimeClassName(c.GetTypeInfo(n.Right).Type, c) is { } asTargetClass)
        {
            c.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
            return $"CSharpGenericIterable.tryAs({e}, {asTargetClass}.class)";
        }
        // System.Array 源上的数组目标经 tryAs：array-method 归一出的 CSharpArray
        // 视图与 T[] 是 javac 不可转换对（instanceof/cast 双拒；真实链
        // ConstraintStruct 的 `arr as XmlAtomicValue[]` 6 处），tryAs 对裸数组
        // 与包装视图行为一致且 null 安全。object 源保持旧 instanceof 路径
        //（PropertyGetValueAsArrayTests 的解包+instanceof 形态）；泛型与类型
        // 参数元素同样保持（List<String>[].class / T[].class 不合法）。
        if (n.Right is ArrayTypeSyntax asArrType
            && c.GetTypeInfo(asArrType.ElementType).Type is not ITypeParameterSymbol
            && c.GetTypeInfo(n.Left).Type?.SpecialType == SpecialType.System_Array
            && !t.Contains('<'))
        {
            c.AddImport("io.github.ningpp.compat.CSharpArray");
            return $"CSharpArray.tryAs({e}, {t}.class)";
        }
        return $"({e} instanceof {Rt(t)} ? ({t})({e}) : null)";
    }

    /// <summary>`expr.GetValue(...)` / `expr.getValue(...)` — the reflection property-value read.</summary>
    private static bool IsPropertyGetValueInvocation(ExpressionSyntax expression)
        => expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member }
            && member.Name is IdentifierNameSyntax name
            && (name.Identifier.ValueText == "GetValue" || name.Identifier.ValueText == "getValue");
    private static string IsPattern(IsPatternExpressionSyntax n, ConversionContext c)
    {
        var e = Expr(c, n.Expression);
        return n.Pattern switch
        {
            DeclarationPatternSyntax d => DeclPattern(e, d, c),
            // JavaGenericPatternRewriter lowers generic declaration patterns to a
            // type-only pattern plus a hoisted cast declaration; C#9 `x is T` also
            // parses here for qualified types. Same reified form as the binary `is`.
            TypePatternSyntax tp => $"{e} instanceof {Rt(Map(tp.Type, c))}",
            ConstantPatternSyntax { Expression: LiteralExpressionSyntax l } when l.IsKind(SyntaxKind.NullLiteralExpression) => $"{e} == null",
            DiscardPatternSyntax => "true",
            _ => "/* TODO: unsupported pattern */ true"
        };
    }
    private static string DeclPattern(string e, DeclarationPatternSyntax d, ConversionContext c)
    {
        var t = Rt(Map(d.Type, c));
        var v = ConversionContext.EscapeJavaKeyword(d.Designation.ToString());
        return (int)c.Options.TargetJavaVersion >= 16
            ? $"{e} instanceof {t} {v}"
            : $"{e} instanceof {t} && ({v} = ({t}){e}) != null";
    }
    private static string TypeOf(TypeOfExpressionSyntax n, ConversionContext c)
    {
        if (c.GetTypeInfo(n.Type).Type is ITypeParameterSymbol tp) return TypeParameterToken(n, tp, c);
        // Unbound generic `typeof(X<>)` (real chain Types.IsOptionalValue's
        // `typeof(Nullable<>).GetGenericTypeDefinition()`): GetTypeInfo yields the
        // constructed definition, whose empty argument list otherwise falls through
        // to the type-parameter fallback and emits `T.class` (javac: 找不到符号 类 T).
        // Map the erased definition host instead. The omitted argument still counts
        // as one entry (`Nullable<>`), so match on OmittedTypeArgument.
        if (n.Type is GenericNameSyntax unboundGeneric
            && unboundGeneric.TypeArgumentList.Arguments.Any(a => a is OmittedTypeArgumentSyntax))
        {
            var infoType = c.GetTypeInfo(n.Type).Type;
            if (infoType is INamedTypeSymbol { } unboundNamed && infoType is not IErrorTypeSymbol)
            {
                var definition = unboundNamed.OriginalDefinition;
                // MapType unwraps Nullable<T> to its element type (the type
                // parameter "T" for the definition itself); the class literal of
                // the unbound form must be the mapped host class instead.
                if (definition.SpecialType == SpecialType.System_Nullable_T)
                {
                    var metadataKey = $"{definition.ContainingNamespace?.ToDisplayString()}.{definition.MetadataName}";
                    if (c.TypeMappings.MapType(metadataKey) is { } nullableHost)
                    {
                        foreach (var import in c.TypeMappings.GetRequiredImports(metadataKey))
                            c.AddImport(import);
                        return $"{Rt(nullableHost)}.class";
                    }
                    return "Optional.class";
                }
                return $"{Rt(c.MapType(definition))}.class";
            }
            // Error-typed operand (unbound generics are not first-class types):
            // resolve the generic definition through the enclosing namespaces and
            // the file's using directives.
            var metadataName = $"{unboundGeneric.Identifier.ValueText}`{unboundGeneric.Arity}";
            var resolved = ResolveGenericDefinitionByMetadataName(n, metadataName, c);
            if (resolved != null)
                return $"{Rt(c.MapType(resolved))}.class";
        }
        // C# unsigned primitives byte/ushort map to Java int at runtime (same
        // as int/uint), so typeof(byte) and typeof(int) both produce
        // Integer.class — they become indistinguishable. When C# code uses
        // typeof(byte) as a type-dispatch key (e.g. XmlUntypedStringConverter
        // checks destinationType == typeof(byte) before typeof(int)), the
        // collision makes int-typed requests match the byte branch. Use the
        // C#-name-matching wrapper (Byte/Short) as a distinct typeof key.
        var typeSymbol = c.GetTypeInfo(n.Type).Type;
        if (typeSymbol != null)
        {
            var typeofWrapper = TypeOfWrapperName(typeSymbol.SpecialType);
            if (typeofWrapper != null)
                return $"{typeofWrapper}.class";
        }
        return $"{Rt(Map(n.Type, c))}.class";
    }

    /// <summary>
    /// Maps C# unsigned primitive types to a distinct Java wrapper class name
    /// for use in typeof() expressions. C# byte → Byte, ushort → Short (even
    /// though they map to int/Integer at runtime) so typeof(byte) and
    /// typeof(int) produce different Class keys. Returns null for types that
    /// don't need a distinct key.
    /// </summary>
    private static string? TypeOfWrapperName(SpecialType special)
        => special switch
        {
            SpecialType.System_Byte => "Byte",
            SpecialType.System_UInt16 => "Short",
            _ => null
        };

    /// <summary>
    /// Resolves an unbound-generic typeof operand (<c>Nullable&lt;&gt;</c>) to its
    /// generic definition symbol by probing the enclosing namespaces, the file's
    /// using directives, and System (in that order) against the compilation.
    /// </summary>
    private static INamedTypeSymbol? ResolveGenericDefinitionByMetadataName(
        TypeOfExpressionSyntax site, string metadataName, ConversionContext c)
    {
        var compilation = c.ProjectCompilation;
        if (compilation == null)
            return null;

        var candidates = new List<string>();
        for (var ancestor = site.Ancestors().FirstOrDefault(); ancestor != null; ancestor = ancestor.Parent)
        {
            if (ancestor is NamespaceDeclarationSyntax nsDecl)
                candidates.Insert(0, nsDecl.Name.ToString());
            else if (ancestor is FileScopedNamespaceDeclarationSyntax fileNs)
                candidates.Insert(0, fileNs.Name.ToString());
        }
        if (c.SemanticModel?.SyntaxTree?.GetCompilationUnitRoot() is { } root)
        {
            foreach (var usingDir in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                if (usingDir.Name != null && usingDir.Alias == null)
                    candidates.Add(usingDir.Name.ToString());
            }
        }
        candidates.Add("System");

        return candidates
            .Select(ns => compilation.GetTypeByMetadataName($"{ns}.{metadataName}"))
            .FirstOrDefault(t => t != null);
    }

    /// <summary>
    /// Java cannot form <c>T.class</c> for an erased type parameter. Prefer the
    /// generic-deepen anchor local (<c>__cs2j_token_T</c>) when one is in scope;
    /// otherwise — including inside the anchor's own initializer — degrade to
    /// <c>Object.class</c> so the generated expression always references a
    /// declared symbol.
    /// </summary>
    private static string TypeParameterToken(SyntaxNode site, ITypeParameterSymbol typeParameter, ConversionContext context)
    {
        var anchorName = "__cs2j_token_" + typeParameter.Name;
        // Only locals injected into the enclosing executable scope are usable; a
        // class-level (field initializer) site must not pick up an anchor from a
        // method body elsewhere in the same type.
        var scope = site.Ancestors().FirstOrDefault(a => a is
            MethodDeclarationSyntax or LocalFunctionStatementSyntax or
            AccessorDeclarationSyntax or LambdaExpressionSyntax or
            ConstructorDeclarationSyntax or OperatorDeclarationSyntax);
        var anchor = scope?
            .DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.Text == anchorName);
        if (anchor == null)
            return TypeParameterBoundClassLiteral(typeParameter, context);

        // The anchor's own `= typeof(T)` must not reference the variable being declared.
        if (anchor.Initializer?.Span.Contains(site.Span) == true)
            return TypeParameterBoundClassLiteral(typeParameter, context);

        return anchorName;
    }

    private static string TypeParameterBoundClassLiteral(
        ITypeParameterSymbol typeParameter, ConversionContext context)
    {
        var bound = typeParameter.ConstraintTypes.FirstOrDefault();
        return bound is INamedTypeSymbol && bound.SpecialType != SpecialType.System_Object
            ? ConversionContext.GetClassLiteral(bound, context)
            : "Object.class";
    }
    /// <summary>Primitives get zero, structs get new T(), Nullable/enum-default etc. as shown; otherwise null.</summary>
    internal static string GetDefaultValueForType(string typeName, ITypeSymbol? typeSymbol, ConversionContext context)
    {
        if (typeSymbol is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T }) return "null";
        var zero = typeName switch
        {
            "int" => "0", "long" => "0L", "short" => "(short)0", "byte" => "0",
            "float" => "0.0f", "double" => "0.0", "boolean" => "false",
            "char" => "'\\0'", "Decimal" => "Decimal.ZERO", _ => null
        };
        if (zero != null)
        {
            if (zero == "Decimal.ZERO") context.AddImport("io.github.ningpp.compat.Decimal");
            return zero;
        }
        if (typeSymbol is INamedTypeSymbol { IsTupleType: true } tuple)
        {
            context.AddImport("io.vavr.Tuple");
            return $"Tuple.of({string.Join(", ", tuple.TupleElements.Select(el => GetDefaultValueForType(context.MapType(el.Type), el.Type, context)))})";
        }
        // Serialization-only BCL structs (StreamingContext et al.) map to the
        // __suppress__ sentinel, which is not instantiable — default is null.
        if (typeName == "__suppress__") return "null";
        if (typeSymbol is INamedTypeSymbol { TypeKind: TypeKind.Struct }) return $"new {typeName}()";
        if (typeSymbol is INamedTypeSymbol { TypeKind: TypeKind.Enum } en) return $"{en.Name}.values()[0]";
        return "null";
    }
    private static string SizeOf(SizeOfExpressionSyntax n, ConversionContext c) => c.GetTypeInfo(n.Type).Type?.SpecialType switch
    {
        SpecialType.System_Boolean or SpecialType.System_SByte => "1",
        SpecialType.System_Char or SpecialType.System_Int16 or SpecialType.System_UInt16 => "2",
        SpecialType.System_Byte or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Single => "4",
        SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Double
            or SpecialType.System_IntPtr or SpecialType.System_UIntPtr => "8",
        SpecialType.System_Decimal => "16",
        _ => "/* sizeof */"
    };
}
