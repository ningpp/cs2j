using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class IListPropertySlotCoercionTests
{
    /// <summary>
    /// 属性初始化器槽位：C# 数组初始化 IReadOnlyList&lt;T&gt; 属性
    /// （真实链 BalloonLayout.NonTreeEdges = Array.Empty&lt;Edge&gt;()），
    /// 数组与 CSharpIList 无继承关系，须包 ArrayHelper.asListView 适配视图。
    /// </summary>
    [Fact]
    public void PropertyInitializer_ArrayToGenericIList_UsesCSharpIListArrayAdapter()
    {
        var result = Convert("""
            using System;
            using System.Collections.Generic;

            class Sample
            {
                public IReadOnlyList<string> Items { get; private set; } = Array.Empty<string>();
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("CSharpIList.from(ArrayHelper.asListView(", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// 属性赋值槽位：C# List&lt;T&gt; 值赋给 IReadOnlyList&lt;T&gt; 属性
    /// （真实链 BalloonLayout.setNonTreeEdges(tree.NonTreeEdges)），
    /// 须落 CSharpIList.from 适配视图。
    /// </summary>
    [Fact]
    public void PropertyAssignment_ListToGenericIList_UsesCSharpIListAdapter()
    {
        var result = Convert("""
            using System.Collections.Generic;

            class Tree
            {
                private List<string> _items;
                public List<string> Items { get { return _items; } set { _items = value; } }
            }

            class Sample
            {
                public IReadOnlyList<string> Items { get; set; }

                void M(Tree t)
                {
                    Items = t.Items;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("setItems(CSharpIList.from(t.getItems()))", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
