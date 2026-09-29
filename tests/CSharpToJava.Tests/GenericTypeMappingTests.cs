using System.Text.RegularExpressions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests verifying that generic .NET types are correctly mapped to Java equivalents
/// with type arguments preserved and primitives boxed.
/// </summary>
public class GenericTypeMappingTests
{

    [Fact]
    public void ActionField_MapsToConsumer_PreservesTypeArg()
    {
        var result = Convert(@"
using System;
class Test {
    Action<string> onChanged;
    Action<int, string> onPair;
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("Consumer<String>", code);
        Assert.Contains("BiConsumer<Integer, String>", code);
        Assert.DoesNotContain("Action<", code);
    }

    [Fact]
    public void FuncField_MapsToFunction_PreservesTypeArgs()
    {
        var result = Convert(@"
using System;
class Test {
    Func<int> supplier;
    Func<int, string> converter;
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("Supplier<Integer>", code);
        Assert.Contains("Function<Integer, String>", code);
        Assert.DoesNotContain("Func<", code);
    }

    [Fact]
    public void FuncTaskField_MapsTaskToCompletableFuture()
    {
        var result = Convert(@"
using System;
using System.Threading.Tasks;
class Test {
    Func<Task> supplier;
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        // System.Threading.Tasks.Task maps to java.util.concurrent.CompletableFuture:
        // the async SDK code calls isDone()/join()/completedFuture on it, which the
        // synchronous compat Task class does not provide.
        Assert.Contains("Supplier<CompletableFuture<?>> supplier", code);
        Assert.DoesNotContain("Supplier<Task>", code);
    }

    [Fact]
    public void PublicField_StaysPublic()
    {
        var result = Convert(@"
class Test {
    public int count;
    protected string name;
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("public int count", code);
        Assert.Contains("protected String name", code);
    }

    [Fact]
    public void EventHandlerField_MapsToBiConsumer()
    {
        var result = Convert(@"
using System;
class Test {
    EventHandler handler;
    EventHandler<string> typedHandler;
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("BiConsumer<Object, EventArgs> handler", code);
        Assert.Contains("BiConsumer<Object, String>", code);
        Assert.DoesNotContain("EventHandler", code);
    }

    [Fact]
    public void ICloneableInterface_MapsToCloneable()
    {
        var result = Convert(@"
using System;
class Test : ICloneable {
    public object Clone() { return null; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("Cloneable", code);
        Assert.DoesNotContain("ICloneable", code);
    }

    [Fact]
    public void ClassImplementsIComparable_UsesImplements()
    {
        var result = Convert(@"
using System;
class ComparableThing : IComparable<ComparableThing> {
    public int CompareTo(ComparableThing other) { return 0; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("implements Comparable<ComparableThing>", code);
        Assert.DoesNotContain("extends Comparable", code);
    }

    [Fact]
    public void ValueListBuilder_DefinedInSource_DoesNotImportCompat()
    {
        // When the C# project includes its own ValueListBuilder<T> source (e.g. in
        // namespace System.Collections.Generic), the converter must NOT add the
        // compat import io.github.ningpp.compat.ValueListBuilder, which would
        // shadow the project's own converted class via single-type-import precedence.
        var result = Convert(@"
using System.Collections.Generic;
namespace System.Collections.Generic
{
    internal struct ValueListBuilder<T>
    {
        public int Length => 0;
        public void Append(T item) { }
    }
}
class Test {
    private ValueListBuilder<string> _builder;
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.DoesNotContain("io.github.ningpp.compat.ValueListBuilder", code);
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
