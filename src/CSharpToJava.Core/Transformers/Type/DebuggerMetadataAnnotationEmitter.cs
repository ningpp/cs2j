using Microsoft.CodeAnalysis;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Utilities;

namespace CSharpToJava.Core.Transformers.Type;

/// <summary>
/// 类型级 [DebuggerDisplay("...")] / [DebuggerTypeProxy(typeof(T))] →
/// 运行时可见的 Java 注解 @io.github.ningpp.compat.DebuggerDisplay / @DebuggerTypeProxy。
/// </summary>
/// <remarks>
/// <para>
/// 这两个特性在 .NET 里可被反射读到（Type.GetTypeInfo().CustomAttributes），移植的
/// 测试工具 System.Diagnostics.DebuggerAttributes（validateDebuggerDisplayReferences /
/// getProxyType）正是通过 CustomAttributes 过滤并断言"恰好一个"，缺失时抛
/// InvalidOperationException（Java 侧 IllegalStateException("Expected one ...")）。
/// compat 桥 TypeInfo.getCustomAttributes() 已经把 @DebuggerDisplay/@DebuggerTypeProxy
/// 注解物化为 CustomAttributeData，但转换器此前把两特性按"纯调试器元数据"透传盘点，
/// 生成类上没有任何注解，反射桥读到 0 个 —— 6 个 debuggerAttributeTests 因此失败。
/// </para>
/// <para>
/// 注解值必须是编译期常量：DebuggerDisplay 取位置实参 0 的字符串；DebuggerTypeProxy
/// 取 typeof(T) 的泛型定义裸类名（Java 不允许 Foo&lt;Bar&gt;.class）。仅支持 typeof 形式；
/// 字符串形式（[DebuggerTypeProxy("Ns.Type")]）无运行时常量可发，维持盘点透传（语料未用）。
/// DebuggerDisplay 的具名参数（Type/Name）与额外位置实参不在 compat 注解契约内，不发射。
/// </para>
/// </remarks>
internal static class DebuggerMetadataAnnotationEmitter
{
    private const string DebuggerDisplayAttributeMetadataName = "System.Diagnostics.DebuggerDisplayAttribute";
    private const string DebuggerTypeProxyAttributeMetadataName = "System.Diagnostics.DebuggerTypeProxyAttribute";

    public static void Emit(JavaClassDeclaration javaClass, INamedTypeSymbol? symbol, ConversionContext context)
    {
        if (symbol == null)
            return;

        foreach (var attr in symbol.GetAttributes())
        {
            var attributeClass = attr.AttributeClass?.OriginalDefinition;
            if (attributeClass == null)
                continue;

            switch (attributeClass.ToDisplayString())
            {
                case DebuggerDisplayAttributeMetadataName:
                    EmitDebuggerDisplay(javaClass, attr, context);
                    break;
                case DebuggerTypeProxyAttributeMetadataName:
                    EmitDebuggerTypeProxy(javaClass, attr, context);
                    break;
            }
        }
    }

    private static void EmitDebuggerDisplay(JavaClassDeclaration javaClass, AttributeData attr, ConversionContext context)
    {
        if (attr.ConstructorArguments.Length == 0 || attr.ConstructorArguments[0].Value is not string displayText)
            return;

        context.AddImport("io.github.ningpp.compat.DebuggerDisplay");
        javaClass.Annotations.Add(new JavaAnnotation(
            $"DebuggerDisplay(\"{StringEscapeHelper.EscapeJavaString(displayText)}\")"));
    }

    private static void EmitDebuggerTypeProxy(JavaClassDeclaration javaClass, AttributeData attr, ConversionContext context)
    {
        if (attr.ConstructorArguments.Length == 0)
            return;

        // .NET 允许 typeof(Foo<Bar>)；注解值要求编译期常量，取泛型定义裸类名
        // （compat 桥按 raw Class 记录，运行时无法再特化，与 .NET 的构造特化语义差异
        // 属于 Java 泛型擦除的既有边界）。MapType 只为跨命名空间引用注册 import；
        // 类名本身取 arity-safe 裸名，避免 MapType 对泛型定义渲染出 Foo<T> 形态。
        if (attr.ConstructorArguments[0].Value is not INamedTypeSymbol named)
            return;

        var definition = named.OriginalDefinition;
        var mapped = context.MapType(definition);
        var rawJavaName = definition.ContainingType == null
            ? context.GetJavaTopLevelTypeName(definition)
            : StripTypeArguments(mapped);
        if (string.IsNullOrEmpty(rawJavaName))
            return;

        context.AddImport("io.github.ningpp.compat.DebuggerTypeProxy");
        javaClass.Annotations.Add(new JavaAnnotation($"DebuggerTypeProxy({rawJavaName}.class)"));
    }

    /// <summary>Map&lt;K,V&gt; → Map；无类型实参时原样返回。</summary>
    private static string StripTypeArguments(string mapped)
    {
        var cut = mapped.IndexOf('<');
        return cut > 0 ? mapped[..cut] : mapped;
    }
}
