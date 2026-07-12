using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class CSharpXmlCompileRegressionTests
{
    [Fact]
    public void ExceptionMessageProperty_MapsToGetMessage()
    {
        var result = Convert("""
            using System;

            class Sample
            {
                string Read(Exception ex) => ex.Message;
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("return ex.getMessage();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".Message", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void XunitThrowsAsyncGeneric_PrependsClassLiteral()
    {
        var result = Convert("""
            using System;
            using System.Threading.Tasks;
            using Xunit;

            class Sample
            {
                Task<ArgumentException> Read(Func<Task> action)
                {
                    return Assert.ThrowsAsync<ArgumentException>(action);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Assert.throwsAsync(ArgumentException.class", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Assert.throwsAsync(action)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void XunitThrowsResultMessage_MapsExceptionMessageGetter()
    {
        var result = Convert("""
            using System;
            using Xunit;

            class Sample
            {
                void Throws<T>(Action action, string message)
                    where T : Exception
                {
                    Assert.Equal(Assert.Throws<T>(action).Message, message);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Assert.throws_(_cs2j_T, action).getMessage()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".Message", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FuncTaskParameter_UsesWildcardCompletableFutureSupplier()
    {
        var result = Convert("""
            using System;
            using System.Threading.Tasks;
            using Xunit;

            class Sample
            {
                Task<ArgumentException> Read(Func<Task> testCode)
                {
                    return Assert.ThrowsAsync<ArgumentException>(testCode);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Supplier<CompletableFuture<?>> testCode", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Assert.throwsAsync(ArgumentException.class, testCode)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Supplier<CompletableFuture> testCode", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringJoin_GenericClassArray_UsesStringHelperJoin()
    {
        var result = Convert("""
            using System;

            class Sample
            {
                string JoinTypes(Type[] types) => string.Join<Type>(", ", types);
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("import io.github.ningpp.compat.StringHelper;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("StringHelper.join(\", \", types)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.join(\", \", types)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeOf_MethodTypeParameter_UsesRuntimeClassParameter()
    {
        var result = Convert("""
            using System;

            class Sample
            {
                Type Read<T>() => typeof(T);
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Class<T> _cs2j_T", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return _cs2j_T;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("T.class", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NonGenericICollection_MapsToCSharpCollection()
    {
        var result = Convert("""
            using System;
            using System.Collections;

            class Bag : ICollection
            {
                public int Count => 0;
                public bool IsSynchronized => false;
                public object SyncRoot => this;
                public void CopyTo(Array array, int index) { }
                public IEnumerator GetEnumerator() => null;
            }

            class Sample
            {
                int Read(Bag bag, Array array)
                {
                    ICollection collection = bag;
                    collection.CopyTo(array, 0);
                    return collection.Count;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("import io.github.ningpp.compat.CSharpICollection;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("class Bag implements CSharpICollection", result.GeneratedCode, StringComparison.Ordinal);
        // Local variable with CSharpICollection<?> type is simplified to var
        // (consistent with other collection interface types like CSharpGenericIterable<T>)
        Assert.Contains("var collection = bag;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("collection.copyTo(array, 0);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return collection.size();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public int size()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("java.util.Collection collection", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CSharpIterable", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(", Iterable", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Iterable<Object>", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcreteCopyTo_OnNonFrameworkCollection_RemainsInstanceCall()
    {
        var result = Convert("""
            using System.Collections;

            class Item { }

            class ItemCollection : IEnumerable
            {
                public void CopyTo(Item[] array, int index) { }
                public IEnumerator GetEnumerator() => null;
            }

            class Sample
            {
                void Copy(ItemCollection target, Item[] destinationArray)
                {
                    target.CopyTo(destinationArray, 0);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("target.copyTo(destinationArray, 0);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("target.toArray()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("System.arraycopy(target.toArray()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericArrayHelperInvokedWithPrimitiveType_UsesObjectReflectionArray()
    {
        var result = Convert("""
            using System;

            class Sample
            {
                object Read(string[] values, Type destinationType)
                {
                    if (destinationType == typeof(int[]))
                        return ToArray<int>(values);

                    return ToArray<string>(values);
                }

                T[] ToArray<T>(string[] values)
                {
                    T[] result = new T[values.Length];
                    for (int i = 0; i < values.Length; i++)
                        result[i] = default(T);
                    return result;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Object toArray", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("TypeHelper.newArrayInstance(clazz", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("java.lang.reflect.Array.set(result", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("(int[]) toArray(values, Integer.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("(String[]) toArray(values, String.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("toArray(Integer.class, values, Integer.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("<T> T[] toArray", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitIEnumeratorMoveNextBridge_DoesNotReplaceAdvancingImplementation()
    {
        var result = Convert("""
            using System.Collections;

            class Sample : IEnumerator
            {
                int index = -1;

                bool IEnumerator.MoveNext()
                {
                    return this.MoveNext();
                }

                internal bool MoveNext()
                {
                    index++;
                    return index < 2;
                }

                void IEnumerator.Reset()
                {
                    index = -1;
                }

                object IEnumerator.Current
                {
                    get { return index; }
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("boolean moveNext()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("index++;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return this.moveNext();", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringBuilderStringOperations_UseCompatHelperForDotNetNullAndRangeSemantics()
    {
        var result = Convert("""
            using System.Text;

            class Sample
            {
                string Edit(string current, string value, int offset, int count)
                {
                    var builder = new StringBuilder(current);
                    builder.Append(value);
                    builder.Insert(offset, value);
                    builder.Remove(offset, count);
                    return builder.ToString().Substring(offset, count);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("import io.github.ningpp.compat.StringHelper;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("new StringBuilder(StringHelper.stringBuilderInitialValue(current))", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("StringHelper.append(builder, value);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("StringHelper.insert(builder, offset, value);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("StringHelper.remove(builder, offset, count);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("StringHelper.substring(builder.toString(), offset, count)", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
