using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.TypeMapping;
using Xunit;namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// compat 集合实现类上返回非 bool/void 的 C# <c>Remove</c> 与 compat 接口的
/// <c>boolean remove(Object)</c> 擦除后同签名但返回类型不兼容，声明端必须改名为
/// <c>remove_2</c>。该后缀由符号形状确定性推导，必须让声明端和调用点（哪怕调用点
/// 属于另一个单独转换的工程，ConversionContext 与改名注册表都是新的）得到同一个
/// 名字。真实链：SDK 的 dotnet-xml-document-tests 调用 dotnet-xml 里被改名的
/// XmlAttributeCollection.Remove。
/// </summary>
public class CompatCollectionRemoveRenameTests : ConversionTestBase
{
    private const string DeclarationSource = """
        using System.Collections.Generic;

        public class Node
        {
        }

        public class MyTree<T> : IEnumerable<T>
        {
            public Node Remove(T item) { return null; }
            public IEnumerator<T> GetEnumerator() { return null; }
        }
        """;

    private const string CallerSource = """
        class RemoveCaller
        {
            public Node Drop(MyTree<Node> tree, Node node)
            {
                tree.Remove(node);
                var removed = tree.Remove(node);
                return removed;
            }
        }
        """;

    [Fact]
    public void NonBooleanRemove_DeclarationAndCallSite_UseDerivedName()
    {
        var result = Convert("""
            using System.Collections;
            using System.Collections.Generic;

            class Node
            {
            }

            class MyTree<T> : IEnumerable<T>
            {
                public Node Remove(T item) { return null; }
                public IEnumerator<T> GetEnumerator() { return null; }
            }

            class Caller
            {
                public Node Drop(MyTree<Node> tree, Node node)
                {
                    return tree.Remove(node);
                }
            }
            """);

        AssertSuccess(result);
        // 声明端：Node Remove(T) → remove_2。
        AssertJavaContains(result, "public Node remove_2(T item)");
        // 调用点：tree.Remove(node) → tree.remove_2(node)。
        AssertJavaContains(result, "tree.remove_2(node)");
        AssertJavaDoesNotContain(result, "tree.remove(node)");
        // compat 接口要求的 boolean remove(Object) 桥仍在（返回被删节点即非 null）。
        AssertJavaContains(result, "boolean remove(Object");
    }

    /// <summary>
    /// 任务里点名的形状：泛型 <c>ICollection&lt;T&gt;</c> 实现类 + 公开的 <c>T Remove(T)</c>。
    /// 接口槽由显式实现满足（返回 bool，Java 侧仍是 boolean remove(Object)），公开
    /// 的强类型 Remove 与接口成员擦除同签名（T 擦除为 Object）但返回类型不兼容，
    /// 必须改名为 remove_2；调用点也必须绑到 remove_2。
    /// </summary>
    [Fact]
    public void NonBooleanRemoveOnICollectionImplementor_IsRenamedAndCalled()
    {
        var result = Convert("""
            using System.Collections;
            using System.Collections.Generic;

            class Item
            {
            }

            class MyCollection<T> : ICollection<T>
            {
                public T Remove(T item) { return item; }
                bool ICollection<T>.Remove(T item) { return false; }
                public int Count { get { return 0; } }
                public bool IsReadOnly { get { return false; } }
                public void Add(T item) { }
                public void Clear() { }
                public bool Contains(T item) { return false; }
                public void CopyTo(T[] array, int arrayIndex) { }
                public IEnumerator<T> GetEnumerator() { return null; }
                IEnumerator IEnumerable.GetEnumerator() { return null; }
            }

            class Caller
            {
                public Item Drop(MyCollection<Item> collection, Item item)
                {
                    return collection.Remove(item);
                }
            }
            """);

        AssertSuccess(result);
        // 公开强类型方法改名；调用点跟随。
        AssertJavaContains(result, "public T remove_2(T item)");
        AssertJavaContains(result, "collection.remove_2(item)");
        AssertJavaDoesNotContain(result, "collection.remove(item)");
        // 显式接口实现提供 compat 接口要求的 boolean remove(Object)。
        AssertJavaContains(result, "boolean remove(Object");
    }

    /// <summary>
    /// 两个工程分别用独立的 ConversionContext 转换：先转声明工程，再把它的编译结果
    /// 作为 CompilationReference 挂在调用方工程上（与 MSBuild ProjectReference 同形）。
    /// 调用方工程看不到声明工程的擦除冲突改名注册表，仍必须自己推出 remove_2。
    /// </summary>
    [Fact]
    public async Task NonBooleanRemove_CallerProjectWithFreshContext_UsesDerivedName()
    {
        var options = new ConversionOptions { TypeMappingConfigPath = TypeMappingConfigPath };

        var declarationContext = new ConversionContext(
            options, new TypeMappingRegistry(options.TypeMappingConfigPath));
        var declarationCompilation = ProjectCompilationBuilder.BuildCompilation(
            new[]
            {
                new SourceFile { FilePath = "MyTree.cs", Content = DeclarationSource },
            },
            declarationContext);
        Assert.NotNull(declarationCompilation);

        var declarationResults = await new ProjectConversionPipeline(options)
            .ConvertProjectAsync(
                declarationCompilation!,
                EmitOnly("MyTree.cs"),
                projectName: "Lib");
        var declarationCode = ConcatGenerated(declarationResults);
        Assert.True(
            declarationResults.All(r => r.Success),
            "Declaration project failed:\n"
            + string.Join("\n", declarationResults.SelectMany(r => r.Diagnostics))
            + "\n---\n" + declarationCode);
        Assert.Contains("remove_2(", declarationCode, StringComparison.Ordinal);
        Assert.Contains("boolean remove(Object", declarationCode, StringComparison.Ordinal);

        // 调用方工程：独立编译 + 独立 ProjectConversionPipeline（新 Context、空注册表）。
        var callerTree = CSharpSyntaxTree.ParseText(CallerSource, path: "RemoveCaller.cs");
        var callerCompilation = CSharpCompilation.Create(
            "LibTestsAssembly",
            new[] { callerTree },
            ProjectCompilationBuilder.GetMetadataReferences()
                .Append(declarationCompilation!.ToMetadataReference())
                .ToList(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var callerResults = await new ProjectConversionPipeline(options)
            .ConvertProjectAsync(callerCompilation, EmitOnly("RemoveCaller.cs"), projectName: "LibTests");
        var callerCode = ConcatGenerated(callerResults);
        Assert.True(
            callerResults.All(r => r.Success),
            "Caller project failed:\n"
            + string.Join("\n", callerResults.SelectMany(r => r.Diagnostics))
            + "\n---\n" + callerCode);
        Assert.Contains("tree.remove_2(node)", callerCode, StringComparison.Ordinal);
        Assert.DoesNotContain("tree.remove(node)", callerCode, StringComparison.Ordinal);
        // 表达式语句与 var 初始化两条渲染路径都必须改名（SDK 现场两种调用形态都有）。
        Assert.Equal(2, Regex.Matches(callerCode, Regex.Escape("tree.remove_2(node)")).Count);
    }

    private static string ConcatGenerated(IEnumerable<ConversionResult> results)
        => string.Join("\n", results.Select(r => r.GeneratedCode));

    /// <summary>
    /// 只发射本工程的文件：引用的工程源码树会出现在 GlobalNamespace 里但不在当前
    /// compilation.SyntaxTrees 中，不能当成本工程的发射目标（真实 CLI 以 emitFilePaths
    /// 过滤，跨工程类型只做符号来源）。
    /// </summary>
    private static HashSet<string> EmitOnly(string filePath)
        => new(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(filePath) };
}
