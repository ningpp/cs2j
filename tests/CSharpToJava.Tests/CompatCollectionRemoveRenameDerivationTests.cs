using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Transformers.Member;
using CSharpToJava.Core.Transformers.Utilities;
using CSharpToJava.TypeMapping;

namespace CSharpToJava.Tests;

/// <summary>
/// compat 集合实现类的非 bool/void <c>Remove</c> 改名必须由符号本身确定性推导，
/// 不能依赖 per-project 的擦除冲突改名注册表：调用点可能属于另一个单独转换的
/// 工程（新 ConversionContext、空注册表）。这些用例直接对同一个符号用两个全新
/// Context 求名，断言声明端与任意调用点得到同一个 <c>remove_2</c>。
/// </summary>
public class CompatCollectionRemoveRenameDerivationTests
{
    private const string RenamableSource = """
        using System.Collections.Generic;

        class Node
        {
        }

        class MyTree<T> : IEnumerable<T>
        {
            public Node Remove(T item) { return null; }
            public IEnumerator<T> GetEnumerator() { return null; }
        }
        """;

    [Fact]
    public void NonBooleanRemoveOnCompatCollection_IsDerivedIdenticallyFromFreshContexts()
    {
        var symbol = GetDeclaredMethod(RenamableSource, "MyTree", "Remove");
        Assert.NotNull(symbol);

        var options = CreateOptions();
        var first = CreateContext(options);
        var second = CreateContext(options);

        Assert.True(CompatCollectionConformance.IsRenamableRemove(symbol, first));
        Assert.True(CompatCollectionConformance.IsRenamableRemove(symbol, second));
        Assert.Equal("remove_2", MethodTransformer.GetJavaMethodName("Remove", symbol, first));
        Assert.Equal("remove_2", MethodTransformer.GetJavaMethodName("Remove", symbol, second));

        // 前提确认：两个 Context 的改名注册表都是空的，名字只可能来自符号推导。
        Assert.False(first.TryGetErasureConflictMethodName(symbol!, out _));
        Assert.False(second.TryGetErasureConflictMethodName(symbol!, out _));
    }

    [Fact]
    public void BooleanRemoveOnCompatCollection_KeepsPlainRemoveName()
    {
        var symbol = GetDeclaredMethod("""
            using System.Collections.Generic;

            class MyBag<T> : IEnumerable<T>
            {
                public bool Remove(T item) { return false; }
                public IEnumerator<T> GetEnumerator() { return null; }
            }
            """, "MyBag", "Remove");
        Assert.NotNull(symbol);

        var context = CreateContext(CreateOptions());
        Assert.False(CompatCollectionConformance.IsRenamableRemove(symbol, context));
        Assert.Equal("remove", MethodTransformer.GetJavaMethodName("Remove", symbol, context));
    }

    [Fact]
    public void NonBooleanRemoveOnPlainClass_KeepsMappedName()
    {
        var symbol = GetDeclaredMethod("""
            class Node
            {
            }

            class Plain
            {
                public Node Remove(Node node) { return node; }
            }
            """, "Plain", "Remove");
        Assert.NotNull(symbol);

        var context = CreateContext(CreateOptions());
        Assert.False(CompatCollectionConformance.IsRenamableRemove(symbol, context));
        Assert.Equal("remove", MethodTransformer.GetJavaMethodName("Remove", symbol, context));
    }

    /// <summary>
    /// 元数据类型（BCL List&lt;T&gt;）由 compat 提供 Java 成员形状，绝不能凭空派生
    /// remove_2。List&lt;T&gt;.Remove 返回 bool，形状上先被排除；这里额外锁定
    /// “只对源码声明生效” 的守卫对元数据符号成立。
    /// </summary>
    [Fact]
    public void MetadataCollectionRemove_IsNeverRenamed()
    {
        var compilation = ProjectCompilationBuilder.BuildCompilation(
            new[] { new SourceFile { FilePath = "Probe.cs", Content = "class Probe { }" } },
            CreateContext(CreateOptions()));
        Assert.NotNull(compilation);

        var remove = compilation!.GetTypeByMetadataName("System.Collections.Generic.List`1")?
            .GetMembers("Remove")
            .OfType<IMethodSymbol>()
            .SingleOrDefault(member => member.Parameters.Length == 1);
        Assert.NotNull(remove);

        var context = CreateContext(CreateOptions());
        Assert.False(CompatCollectionConformance.IsRenamableRemove(remove, context));
    }

    private static ConversionOptions CreateOptions() => new()
    {
        TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
    };

    private static ConversionContext CreateContext(ConversionOptions options)
        => new(options, new TypeMappingRegistry(options.TypeMappingConfigPath));

    private static IMethodSymbol? GetDeclaredMethod(string source, string typeName, string methodName)
    {
        var context = CreateContext(CreateOptions());
        var compilation = ProjectCompilationBuilder.BuildCompilation(
            new[] { new SourceFile { FilePath = "Probe.cs", Content = source } },
            context);
        Assert.NotNull(compilation);

        var tree = compilation!.SyntaxTrees.Single(t => t.FilePath == "Probe.cs");
        var model = compilation.GetSemanticModel(tree);
        var declaration = tree.GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == methodName
                         && m.Ancestors().OfType<ClassDeclarationSyntax>()
                             .Any(c => c.Identifier.Text == typeName));
        return model.GetDeclaredSymbol(declaration) as IMethodSymbol;
    }
}
