using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression.Utilities;
using CSharpToJava.Core.Transformers.Type;
using CSharpToJava.Core.Transformers.Utilities;
using System.Text;
namespace CSharpToJava.Core.Transformers.Expression;
/// <summary>Handles object and array creation expressions.</summary>
[TransformerRegistration]
public class ObjectCreationTransformer : IIRExpressionTransformer
{
    static ObjectCreationTransformer() => ExpressionTransformerRegistry.Register(new[]
    {
        SyntaxKind.ImplicitObjectCreationExpression, SyntaxKind.ObjectCreationExpression,
        SyntaxKind.AnonymousObjectCreationExpression, SyntaxKind.ArrayCreationExpression,
        SyntaxKind.ImplicitArrayCreationExpression, SyntaxKind.ArrayInitializerExpression,
        SyntaxKind.StackAllocArrayCreationExpression
    }, new ObjectCreationTransformer());
    private static readonly Lazy<ObjectCreationTransformer> _instance = new(() => new());
    public static ObjectCreationTransformer Instance => _instance.Value;
    public string Transform(ExpressionSyntax n, ConversionContext c) => n switch
    {
        ObjectCreationExpressionSyntax o => NewObject(
            c.GetTypeInfo(o).Type, o.Type, o.ArgumentList, o.Initializer, c,
            c.GetSymbolInfo(o).Symbol as IMethodSymbol),
        ImplicitObjectCreationExpressionSyntax io => NewObject(
            c.GetTypeInfo(io).Type, null, io.ArgumentList, io.Initializer, c,
            c.GetSymbolInfo(io).Symbol as IMethodSymbol),
        AnonymousObjectCreationExpressionSyntax a => NewAnonymous(a, c),
        ArrayCreationExpressionSyntax ar => NewArray(ar, c),
        ImplicitArrayCreationExpressionSyntax ia => NewImplicitArray(ia, c),
        InitializerExpressionSyntax i => NewBareArray(i, c),
        StackAllocArrayCreationExpressionSyntax s => NewStackAlloc(s, c),
        _ => throw new NotSupportedException($"Object creation kind {n.Kind()} not supported.")
    };
    public JavaExpression TransformToIR(ExpressionSyntax n, ConversionContext c)
        => new JavaRawExpression(Transform(n, c));
    private string NewObject(ITypeSymbol? type, TypeSyntax? ts, ArgumentListSyntax? al, InitializerExpressionSyntax? init, ConversionContext c, IMethodSymbol? constructor = null)
    {
        var name = type is null or IErrorTypeSymbol ? (ts != null ? c.MapTypeFromSyntax(ts) : "Object") : c.MapType(type);
        var f = ExpressionTransformerFacade.Instance;

        // new string(char, int): Java has no such constructor; compat repeats the char.
        if (type?.SpecialType == SpecialType.System_String
            && al is { Arguments.Count: 2 }
            && c.GetTypeInfo(al.Arguments[0].Expression).Type?.SpecialType == SpecialType.System_Char
            && c.GetTypeInfo(al.Arguments[1].Expression).Type?.SpecialType == SpecialType.System_Int32)
        {
            c.AddImport("io.github.ningpp.compat.StringHelper");
            return $"StringHelper.newString({f.Transform(al.Arguments[0].Expression, c)}, {f.Transform(al.Arguments[1].Expression, c)})";
        }

        // new string(ReadOnlySpan<char>/Span<char>): Java has no such constructor;
        // the compat spans render their characters through toString().
        if (type?.SpecialType == SpecialType.System_String
            && al is { Arguments.Count: 1 }
            && c.GetTypeInfo(al.Arguments[0].Expression).Type is INamedTypeSymbol spanArg
            && spanArg.Name is "Span" or "ReadOnlySpan"
            && spanArg.ContainingNamespace?.ToDisplayString() == "System"
            && spanArg.TypeArguments.Length == 1
            && spanArg.TypeArguments[0].SpecialType == SpecialType.System_Char)
        {
            return $"{f.Transform(al.Arguments[0].Expression, c)}.toString()";
        }

        // new StringBuilder(string, int, int, int)：Java 无该构造器。C# 语义是取
        // value.Substring(startIndex, length)，capacity 仅为预分配提示（Java 形态
        // 无对应，安全省略）。substring 按同一参数渲染（真实链 RecordBuilder 的
        // new StringBuilder(comment, begin, index, 2 * comment.Length)，2 站）。
        if (type is INamedTypeSymbol { SpecialType: SpecialType.None } sbType
            && sbType.ToDisplayString() == "System.Text.StringBuilder"
            && al is { Arguments.Count: 4 }
            && c.GetTypeInfo(al.Arguments[0].Expression).Type?.SpecialType == SpecialType.System_String)
        {
            var value = f.Transform(al.Arguments[0].Expression, c);
            var start = f.Transform(al.Arguments[1].Expression, c);
            var length = f.Transform(al.Arguments[2].Expression, c);
            return $"new StringBuilder({value}.substring({start}, {start} + {length}))";
        }
        if (init?.IsKind(SyntaxKind.ObjectInitializerExpression) == true)
        {
            if (init.Expressions.Count == 0) return EmitNew(name, al, type as INamedTypeSymbol, c, constructor);
            var tmp = c.GenerateSyntheticName("_obj");
            c.AddPreStatement($"{name} {tmp} = {EmitNew(name, al, type as INamedTypeSymbol, c, constructor)};");
            foreach (AssignmentExpressionSyntax e in init.Expressions)
            {
                if (e.Left is not IdentifierNameSyntax id) continue;
                var v = f.Transform(e.Right, c);
                c.AddPreStatement(c.GetSymbolInfo(id).Symbol is IPropertySymbol p
                    ? $"{tmp}.set{char.ToUpperInvariant(p.Name[0])}{p.Name[1..]}({v});"
                    : $"{tmp}.{id.Identifier.Text} = {v};");
            }
            return tmp;
        }
        if (init?.IsKind(SyntaxKind.CollectionInitializerExpression) == true)
        {
            var tmp = c.GenerateSyntheticName("_col");
            c.AddPreStatement($"var {tmp} = new {name}();");
            foreach (var e in init.Expressions)
                if (e is InitializerExpressionSyntax { Expressions.Count: 2 } kv)
                    c.AddPreStatement($"{tmp}.put({f.Transform(kv.Expressions[0], c)}, {f.Transform(kv.Expressions[1], c)});");
                else
                    c.AddPreStatement($"{tmp}.add({f.Transform(e, c)});");
            return tmp;
        }
        return EmitNew(name, al, type as INamedTypeSymbol, c, constructor);
    }
    private static string EmitNew(string name, ArgumentListSyntax? al, INamedTypeSymbol? type, ConversionContext c, IMethodSymbol? constructor = null)
    {
        // System.Tuple`2 → Map.Entry：接口不可实例化（javac: Map.Entry是抽象的），
        // 改落到不可变条目实现并登记导入（真实链 XmlTextReaderImpl 的
        // new Map.Entry<Integer, Boolean>(...) 2 处）。
        if (name.StartsWith("Map.Entry", StringComparison.Ordinal))
        {
            name = "AbstractMap.SimpleImmutableEntry" + name["Map.Entry".Length..];
            c.AddImport("java.util.AbstractMap");
        }
        // `new Runnable(methodGroup)` (closure-lift emits `new Action(LocalMethod)`):
        // Java functional interfaces are satisfied by a method reference, not by
        // construction — emit `Owner::method` and let target typing pick the shape.
        if (type is { TypeKind: TypeKind.Delegate }
            && al is { Arguments.Count: 1 })
        {
            // 仅裸方法组（标识符/成员访问）转方法引用。调用表达式
            // `new Callback(GetCallback(x))` 的 GetSymbolInfo 同样绑定被调方法
            // 符号，但它是「调用结果」不是方法组——误转 this::getter 会丢掉
            // 实参（javac: 方法引用无效；真实链 ReflectionXmlSerializationReader
            // 的 new XmlSerializationCollectionFixupCallback(
            // GetCreateCollectionOfObjectsCallback(typeDesc.Type))）。
            if (al.Arguments[0].Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax
                && c.GetSymbolInfo(al.Arguments[0].Expression).Symbol is IMethodSymbol methodGroup)
            {
                var methodReference = EmitDelegateMethodReference(al.Arguments[0].Expression, methodGroup, c);
                // Inside an object[]/collection initializer there is no target type for
                // the method reference (C# boxes the delegate), so cast explicitly.
                return al.Parent?.Parent is InitializerExpressionSyntax
                    ? $"({name}) {methodReference}"
                    : methodReference;
            }

            // `new Func<T>(delegateVar)`：实参是委托变量而非方法组——Java 函数式
            // 接口抽象方法不可实例化（javac: Supplier是抽象的; 无法实例化），SAM
            // 转换为转发 lambda。SAM 方法名必须与 DelegateTransformer 接口发射
            // 同约定（InferSamMethodName）：此前硬编码 invoke()，对自定义委托
            // （Supplier 形 get()）与元数据 Func/Action（apply/accept）都不成立
            // （真实链 modulecore-tests ModuleGenerator 找不到 invoke）。
            if (c.GetTypeInfo(al.Arguments[0].Expression).Type is { TypeKind: TypeKind.Delegate })
            {
                var delegateArg = ExpressionTransformerFacade.Instance.Transform(al.Arguments[0].Expression, c);
                var invoke = type.GetMembers("Invoke").OfType<IMethodSymbol>().FirstOrDefault()
                    ?? type.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.Name == "Invoke");
                var sam = DelegateTransformer.InferSamMethodName(
                    invoke?.ReturnsVoid ?? false,
                    invoke?.Parameters.Length ?? 0);
                if (invoke == null || invoke.Parameters.Length == 0)
                    return $"() -> {delegateArg}.{sam}()";

                var samParams = string.Join(", ", invoke.Parameters.Select(
                    (p, i) => $"{c.MapType(p.Type)} _cs2jSam{i}"));
                var samArgs = string.Join(", ", invoke.Parameters.Select((_, i) => $"_cs2jSam{i}"));
                return $"({samParams}) -> {delegateArg}.{sam}({samArgs})";
            }
        }

        // C09 Java 半区：C# 的 new ThreadLocal<T>() 首访返回 default(T)（值类型为
        // 0/false/默认 struct），而 java.lang.ThreadLocal 首访恒为 null，值类型
        // 拆箱即 NPE。无参构造非 Nullable 值类型 T 时，补一个每线程默认值工厂
        // （C# 侧 () => default(T) 与无参 holder 语义恒等）。引用类型与 Nullable<T>
        // 的默认本就是 null，保持无参构造。预处理产物与用户直写的 ThreadLocal 同走此路。
        if (al is null or { Arguments.Count: 0 }
            && type is { IsValueType: false, TypeArguments.Length: 1 } named
            && named.OriginalDefinition.MetadataName == "ThreadLocal`1"
            && named.ContainingNamespace?.ToDisplayString() == "System.Threading"
            && named.TypeArguments[0] is { } held
            && held.IsValueType
            && held is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
        {
            var defaultValue = TypeOperationTransformer.GetDefaultValueForType(c.MapType(held), held, c);
            return $"new {name}(() -> {defaultValue})";
        }

        // `new ValueTask<T>(x)` / `new ValueTask(t)` 经类型映射落到 CompletableFuture，
        // 而 Java CompletableFuture 只有公共无参构造（javac: 无法将构造器
        // CompletableFuture应用到给定类型；真实链 XmlTextReaderImpl.ParseTextAsync）。
        // C# 形状分支：
        //   new ValueTask<T>(Task<T> task) → Task 本体（ValueTask 只是 Task 包装），直通实参；
        //   new ValueTask<T>(T value)      → 已完成任务 CompletableFuture.completedFuture(value)。
        // `new Task(...)`（冷任务，需 Start）语义不同且工程内无使用，不在此处理。
        if (type is INamedTypeSymbol valueTaskType
            && valueTaskType.OriginalDefinition.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks"
            && valueTaskType.OriginalDefinition.Name == "ValueTask"
            && constructor is { Parameters.Length: 1 } valueTaskCtor)
        {
            var arg = ArgumentTransformer.TransformArgumentList(
                al, c, ExpressionTransformerFacade.Instance, methodSymbol: constructor);
            var param = valueTaskCtor.Parameters[0].Type;
            var paramIsTaskLike = param is INamedTypeSymbol paramNamed
                && paramNamed.OriginalDefinition.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks"
                && paramNamed.OriginalDefinition.Name is "Task" or "ValueTask";
            return paramIsTaskLike ? arg : $"CompletableFuture.completedFuture({arg})";
        }

        var args = ArgumentTransformer.TransformArgumentList(
            al, c, ExpressionTransformerFacade.Instance, methodSymbol: constructor);
        return $"new {name}({args})";
    }
    /// <summary>
    /// Renders a single-method-group delegate construction as a Java method
    /// reference: local/static identifier → <c>Owner::camelName</c>, member
    /// access → <c>receiver::camelName</c> (instance) or the mapped owner type.
    /// </summary>
    internal static string EmitDelegateMethodReference(
        ExpressionSyntax groupExpression, IMethodSymbol methodGroup, ConversionContext c)
    {
        string owner;
        if (groupExpression is MemberAccessExpressionSyntax memberAccess)
        {
            // A primitive-keyword receiver is a Java keyword, not a type name:
            // char.IsHighSurrogate → Character::isHighSurrogate.
            owner = memberAccess.Expression is PredefinedTypeSyntax
                ? PrimitiveWrapperName(methodGroup.ContainingType.SpecialType) ?? c.MapType(methodGroup.ContainingType)
                : ExpressionTransformerFacade.Instance.Transform(memberAccess.Expression, c);
        }
        else
        {
            owner = methodGroup.IsStatic
                ? c.MapType(methodGroup.ContainingType)
                : "this";
        }

        var methodName = methodGroup.Name.Length == 0
            ? methodGroup.Name
            : MapPrimitiveWrapperMethodGroupName(methodGroup, methodGroup.Name);
        return $"{owner}::{methodName}";
    }

    /// <summary>
    /// Method-group references on primitive wrappers must honor the same
    /// C#→Java name table as invocations (char.IsWhiteSpace →
    /// Character::isWhitespace); plain camel-casing emits a nonexistent
    /// `isWhiteSpace` and the method reference fails to compile.
    /// </summary>
    private static string MapPrimitiveWrapperMethodGroupName(IMethodSymbol methodGroup, string name)
    {
        if (methodGroup.ContainingType?.SpecialType == SpecialType.System_Char)
        {
            var mapped = name switch
            {
                "IsLower" => "isLowerCase",
                "IsUpper" => "isUpperCase",
                "IsWhiteSpace" => "isWhitespace",
                "IsControl" => "isISOControl",
                "IsSurrogate" => "isSurrogate",
                "IsHighSurrogate" => "isHighSurrogate",
                "IsLowSurrogate" => "isLowSurrogate",
                "IsSurrogatePair" => "isSurrogatePair",
                "GetNumericValue" => "getNumericValue",
                "ToLower" or "ToLowerInvariant" => "toLowerCase",
                "ToUpper" or "ToUpperInvariant" => "toUpperCase",
                "ToString" => "toString",
                "ConvertFromUtf32" => "toChars",
                "ConvertToUtf32" => "toCodePoint",
                "Parse" => "valueOf",
                _ => null
            };
            if (mapped != null)
                return mapped;
        }
        else if (methodGroup.ContainingType is { SpecialType: SpecialType.System_Single or SpecialType.System_Double })
        {
            var mapped = name switch
            {
                "IsInfinity" => "isInfinite",
                "IsNaN" => "isNaN",
                "IsFinite" => "isFinite",
                _ => null
            };
            if (mapped != null)
                return mapped;
        }
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static string PrimitiveWrapperName(SpecialType specialType) => specialType switch
    {
        SpecialType.System_Boolean => "Boolean",
        SpecialType.System_Byte or SpecialType.System_UInt16 or SpecialType.System_Int32
            or SpecialType.System_UInt32 => "Integer",
        SpecialType.System_SByte => "Byte",
        SpecialType.System_Int16 => "Short",
        SpecialType.System_Char => "Character",
        SpecialType.System_Int64 or SpecialType.System_UInt64 => "Long",
        SpecialType.System_Single => "Float",
        SpecialType.System_Double => "Double",
        _ => null
    };

    private static string NewAnonymous(AnonymousObjectCreationExpressionSyntax n, ConversionContext c)
    {
        // C21 M5: anonymous object creation is lowered to a named type by the
        // anonymous-type-lift preprocessor (default-on in the convert-project
        // chain) before Java conversion. The legacy Map.of(...) arm is retired;
        // a residual anonymous creation on the direct pipeline is a hard error
        // instead of an untyped heterogeneous map that drops shape/equality.
        throw new NotSupportedException(
            "CS2J normalization required: anonymous object creation 'new { ... }' must be lifted to a named type " +
            "by the anonymous-type-lift preprocessor before Java conversion.");
    }
    private string NewArray(ArrayCreationExpressionSyntax n, ConversionContext c)
    {
        var f = ExpressionTransformerFacade.Instance;
        var es = c.GetTypeInfo(n.Type.ElementType).Type;
        var elem = es?.SpecialType switch
        {
            SpecialType.System_Byte => "byte",
            SpecialType.System_SByte => "int",
            _ => es != null ? c.MapType(es) : c.MapTypeFromSyntax(n.Type.ElementType)
        };
        var sizes = n.Type.RankSpecifiers.SelectMany(rs => rs.Sizes
            .Select(s =>
            {
                if (s.IsKind(SyntaxKind.OmittedArraySizeExpression))
                    return "";
                var sizeExpr = f.Transform(s, c);
                // C# allows new byte[stream.Length] where Length is long; Java
                // requires int for array sizes. Cast long/ulong/nint/nuint
                // expressions to int to avoid compilation failure.
                var sizeType = c.GetTypeInfo(s).Type?.SpecialType;
                if (sizeType is SpecialType.System_Int64 or SpecialType.System_UInt64
                    or SpecialType.System_IntPtr or SpecialType.System_UIntPtr)
                {
                    sizeExpr = $"(int) ({sizeExpr})";
                }
                return sizeExpr;
            })).ToList();
        return EmitArray(elem, sizes, n.Initializer, c, es?.TypeKind == TypeKind.TypeParameter,
            elemSymbol: es, needsValueFill: NeedsValueFill(es, init: n.Initializer != null));
    }
    private string NewImplicitArray(ImplicitArrayCreationExpressionSyntax n, ConversionContext c)
    {
        var at = (c.GetTypeInfo(n).Type ?? c.GetTypeInfo(n).ConvertedType) as IArrayTypeSymbol;
        return EmitArray(at != null ? c.MapType(at.ElementType) : "Object",
            Enumerable.Repeat("", n.Commas.Count + 1).ToList(), n.Initializer, c,
            elemSymbol: at?.ElementType,
            needsValueFill: NeedsValueFill(at?.ElementType, init: n.Initializer != null));
    }
    private string NewBareArray(InitializerExpressionSyntax n, ConversionContext c)
    {
        var arr = (c.GetTypeInfo(n).ConvertedType ?? c.GetTypeInfo(n).Type) as IArrayTypeSymbol;
        if (arr != null)
            return EmitArray(arr.ElementType.SpecialType == SpecialType.System_Byte ? "byte" : c.MapType(arr.ElementType),
                Enumerable.Repeat("", arr.Rank).ToList(), n, c,
                elemSymbol: arr.ElementType,
                needsValueFill: NeedsValueFill(arr.ElementType, init: n.Expressions.Count > 0));
        if (n.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Type: ArrayTypeSyntax at } } })
            return EmitArray(c.MapTypeFromSyntax(at.ElementType),
                Enumerable.Repeat("", at.RankSpecifiers.FirstOrDefault()?.Sizes.Count ?? 1).ToList(), n, c);
        return ArrayBody(n, "Object", c);
    }

    /// <summary>
    /// C# 结构体数组元素默认已初始化（default(T)），而转换产物把结构体降为 Java
    /// class——`new NamespaceDeclaration[8]` 的元素全是 null，首访问即 NPE（真实链
    /// XmlNamespaceManager 构造器 _nsdecls[0].Set(...)）。有初始化器的不涉及；仅
    /// 非基元/非枚举/非 decimal 的值类型元素改走 compat 工厂（无可用无参构造器时
    /// 返回普通 null 数组，与现状一致）。
    /// </summary>
    private static bool NeedsValueFill(ITypeSymbol? elementType, bool init)
        => !init
           && elementType is INamedTypeSymbol
           {
               TypeKind: TypeKind.Struct,
               SpecialType: SpecialType.None,
               OriginalDefinition.SpecialType: SpecialType.None
           };
    private static string EmitArray(string elem, List<string> sizes, InitializerExpressionSyntax? init,
        ConversionContext c, bool typeParam = false,
        ITypeSymbol? elemSymbol = null, bool needsValueFill = false)
    {
        // C# decimal[] elements default to 0m; Java Decimal reference arrays
        // start as null. Allocate through the compat factory which fills with
        // Decimal.ZERO (only for sized 1D creation without an initializer).
        if (init == null && elem == "Decimal" && sizes.Count == 1
            && sizes[0] is { Length: > 0 } length)
        {
            c.AddImport("io.github.ningpp.compat.Decimal");
            return $"Decimal.array({length})";
        }
        // 结构体元素默认实例填充（见 NeedsValueFill）。
        if (init == null && needsValueFill && elemSymbol != null && sizes.Count == 1
            && sizes[0] is { Length: > 0 } fillLength)
        {
            c.AddImport("io.github.ningpp.compat.ArrayHelper");
            var classLiteral = ConversionContext.GetClassLiteral(elemSymbol, c);
            return $"ArrayHelper.filledValueArray({classLiteral}, {fillLength})";
        }
        var gen = elem.IndexOf('<');
        // 大嵌入字节数组降位 base64（见 ArrayBody）后是自包含表达式，没有
        // `new byte[]` 前缀：必须整体短路返回，否则拼出
        // `new byte[]ArrayHelper.fromBase64("…")`（真实链 xmlcoretest-tests
        // FilePathUtil.s_fileN）。多维尺寸不支持（base64 只承载一维）。
        if (init != null && sizes.Count == 1
            && TryEmbeddedByteArrayBody(init, elem, c) is { } base64Body)
        {
            return base64Body;
        }
        var sb = new StringBuilder();
        if (typeParam || gen > 0)
            sb.Append('(').Append(elem).Append(string.Concat(Enumerable.Repeat("[]", sizes.Count))).Append(") ");
        sb.Append("new ").Append(typeParam ? "Object" : gen > 0 ? elem[..gen] : elem);
        for (var i = 0; i < sizes.Count; i++)
            sb.Append('[').Append(init == null ? sizes[i] : "").Append(']');
        if (init != null) sb.Append(ArrayBody(init, elem, c));
        return sb.ToString();
    }
    private static string ArrayBody(InitializerExpressionSyntax init, string elem, ConversionContext c)
    {
        // 大嵌入字节数组（测试资源整文件内嵌，真实链 FilePathUtil 的 s_fileN，单行
        // 4.7MB）超出 javac 单方法 64KB 字节码上限（代码过长/常量过多）。全常量
        // 字面量时改走 base64：ArrayHelper.fromBase64("<b64>")，字段初始化保持单行。
        // 先判后渲染：命中时无需逐元素 Transform（4.7MB 数组的字面量转换代价大）。
        if (TryEmbeddedByteArrayBody(init, elem, c) is { } base64Body)
        {
            return base64Body;
        }

        var f = ExpressionTransformerFacade.Instance;
        var vals = init.Expressions.Select(e =>
        {
            var v = f.Transform(e, c);
            // C# byte is unsigned: an element expression frequently lowers to a
            // widened int (0xF4 & 0xFF, UnsignedMath.toByteInt32, ...), which Java
            // rejects in a byte[] initializer. Restore the narrowing cast.
            if (elem == "byte" && !v.TrimStart().StartsWith("(byte)", StringComparison.Ordinal))
                v = $"(byte) ({v})";
            return v;
        }).ToList();
        return $"{{ {string.Join(", ", vals)} }}";
    }

    /// <summary>
    /// <c>ArrayHelper.fromBase64("…")</c> when <paramref name="init"/> is a large
    /// all-constant byte initializer, else null. The result is a self-contained
    /// expression (it allocates the array itself), so callers must not prepend
    /// <c>new byte[]</c> to it.
    /// </summary>
    private static string? TryEmbeddedByteArrayBody(
        InitializerExpressionSyntax init, string elem, ConversionContext c)
    {
        if (elem != "byte" || init.Expressions.Count < 1000)
        {
            return null;
        }

        var bytes = new List<byte>(init.Expressions.Count);
        foreach (var e in init.Expressions)
        {
            if (e is LiteralExpressionSyntax lit
                && lit.IsKind(SyntaxKind.NumericLiteralExpression)
                && lit.Token.Value is int byteValue
                && byteValue is >= 0 and <= 255)
            {
                bytes.Add((byte)byteValue);
            }
            else
            {
                return null;
            }
        }

        c.AddImport("io.github.ningpp.compat.ArrayHelper");
        return $"ArrayHelper.fromBase64(\"{Convert.ToBase64String(bytes.ToArray())}\")";
    }
    private string NewStackAlloc(StackAllocArrayCreationExpressionSyntax n, ConversionContext c)
        => throw new NotSupportedException(
            $"CS2J normalization required: stackalloc in {n.Kind()} must be lowered by a preprocessor before Java conversion.");
    internal static string WrapArrayForCollectionArg(string expr, IArrayTypeSymbol arrayType, ConversionContext c)
        => expr;
    public static bool HasNewConstraintObjectCreation(IMethodSymbol method)
        => method.TypeParameters.Any(tp => tp.HasConstructorConstraint);
}
