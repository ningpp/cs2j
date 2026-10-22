using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// List&lt;T&gt;.RemoveAll(Predicate&lt;T&gt;) must lower to the compat helper
/// removeAllMatching(Predicate), not the camelCase removeAll(...) that Java binds to
/// Collection.removeAll(Collection) (element-wise mass removal — wrong overload and
/// uncompilable for a lambda/method-reference argument). Regression from
/// dotnet-text-regularexpressions RegexCharClass.PopulateCategoryIdToName.
/// </summary>
public class ListRemoveAllPredicateTests
{
    private static async Task<string> ConvertAsync(string source)
    {
        var results = await new ProjectConversionPipeline(new ConversionOptions())
            .ConvertProjectAsync(
                new[] { new SourceFile { FilePath = "Test.cs", Content = source } },
                projectName: "Demo");
        foreach (var failure in results.Where(r => !r.Success))
            throw new Xunit.Sdk.XunitException(
                string.Join("\n", failure.Diagnostics) + "\n---\n" + failure.GeneratedCode);
        return string.Join("\n", results.Select(r => r.GeneratedCode));
    }

    [Fact]
    public async Task RemoveAllLambda_LowersToRemoveAllMatching()
    {
        var code = await ConvertAsync("""
            using System.Collections.Generic;

            public class Test
            {
                public int DropEmpty(List<string> items)
                {
                    return items.RemoveAll(s => s.Length == 0);
                }
            }
            """);

        Assert.Contains(".removeAllMatching(", code);
        Assert.DoesNotContain(".removeAll(", code);
    }

    [Fact]
    public async Task RemoveAllPredicateVariable_LowersToRemoveAllMatching()
    {
        // A Predicate-typed variable (method group shape) must take the same bridge:
        // the gate matches the overload signature, not the lambda literal shape.
        var code = await ConvertAsync("""
            using System;
            using System.Collections.Generic;

            public class Test
            {
                public int Drop(Predicate<string> match, List<string> items)
                {
                    return items.RemoveAll(match);
                }
            }
            """);

        Assert.Contains(".removeAllMatching(match)", code);
        Assert.DoesNotContain(".removeAll(", code);
    }

    [Fact]
    public async Task CustomRemoveAll_KeepsPlainCamelCase()
    {
        // A user type that merely happens to declare RemoveAll is not List<T>;
        // the bridge must not swallow it.
        var code = await ConvertAsync("""
            using System.Collections.Generic;

            public class Bag
            {
                private readonly List<object> _items = new List<object>();

                public int RemoveAll(object filter)
                {
                    int before = _items.Count;
                    _items.Clear();
                    return before;
                }

                public int ClearAll()
                {
                    return RemoveAll(null);
                }
            }
            """);

        Assert.DoesNotContain(".removeAllMatching(", code);
    }

    [Fact]
    public async Task RemoveNonPredicate_KeepsPlainRemove()
    {
        // RemoveAll with a collection-shaped argument is not the C# overload; and
        // plain Remove(item) must keep its Java removeAll-free rendering.
        var code = await ConvertAsync("""
            using System.Collections.Generic;

            public class Test
            {
                public void Drop(List<string> items, string first)
                {
                    items.Remove(first);
                }
            }
            """);

        Assert.DoesNotContain(".removeAllMatching(", code);
    }
}
