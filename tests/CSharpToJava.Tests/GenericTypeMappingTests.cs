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
    public void IEnumerableField_MapsToIterable_PreservesTypeArg()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    IEnumerable<string> items;
    IEnumerable<int> numbers;
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpGenericIterable<String>", code);
        Assert.Contains("CSharpGenericIterable<Integer>", code);
        Assert.DoesNotContain("IEnumerable", code);
    }

    [Fact]
    public void IListProperty_MapsToList_PreservesTypeArg()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    public IList<string> Names { get; set; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpGenericIList<String>", code);
        Assert.DoesNotMatch(new Regex(@"\bIList\b"), code);
    }

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
    public void FieldWithoutAccessModifier_GetsPrivateInJava()
    {
        var result = Convert(@"
class Test {
    int count;
    string name;
    List<int> items;
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("private int count", code);
        Assert.Contains("private String name", code);
        Assert.Contains("private CSharpList<Integer> items", code);
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
    public void NestedGenericTypes_MapRecursivelyWithBoxing()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    Dictionary<string, List<int>> lookup;
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpDictionary<String, CSharpList<Integer>>", code);
        Assert.DoesNotMatch(new Regex(@"\bDictionary\b"), code);
    }

    [Fact]
    public void IComparerField_MapsToComparator()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    IComparer<string> comparer;
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpGenericComparer<String>", code);
        Assert.DoesNotContain("IComparer", code);
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
    public void ConstructorParameter_GenericTypePreserved()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Shape { }
class World {
    public World(IEnumerable<Shape> obstacles) { }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("Iterable<Shape> obstacles", code);
        Assert.DoesNotContain("IEnumerable<Shape>", code);
    }

    [Fact]
    public void MethodReturnType_GenericPreserved()
    {
        var result = Convert(@"
using System;
using System.Collections.Generic;
class Factory {
    IList<string> GetNames() { return null; }
    Func<int, bool> GetPredicate() { return null; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("CSharpGenericIList<String> getNames", code);
        Assert.Contains("Predicate<Integer> getPredicate", code);
        Assert.DoesNotMatch(new Regex(@"\bIList<"), code);
        Assert.DoesNotContain("Func<", code);
    }

    [Fact]
    public void ClassImplementsTwoIComparerInterfaces_UsesHelperMethodsNotExtends()
    {
        var result = Convert(@"
using System.Collections.Generic;
class ScanSegment { }
class SegmentIntersector : IComparer<SegmentIntersector.SegEvent>, IComparer<ScanSegment> {
    internal class SegEvent { }
    public int Compare(SegEvent a, SegEvent b) { return 0; }
    public int Compare(ScanSegment a, ScanSegment b) { return 0; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;

        // Must NOT use "extends" for interfaces
        Assert.DoesNotContain("extends CSharpGenericComparer", code);
        // Both interfaces extracted to helper methods (Java type erasure)
        Assert.Contains("asSegEventComparer", code);
        Assert.Contains("asScanSegmentComparer", code);
    }

    [Fact]
    public void ErasureConflictWithGenericTypeArg_StripsTypeParametersFromHelperMethodName()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Foo { }
class Bar : IComparer<KeyValuePair<string, Foo>>, IComparer<Foo> {
    public int Compare(KeyValuePair<string, Foo> a, KeyValuePair<string, Foo> b) { return 0; }
    public int Compare(Foo a, Foo b) { return 0; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;

        // Type arguments containing '<' must not leak into the Java identifier
        Assert.Contains("asCSharpKeyValuePairComparer", code);
        Assert.DoesNotMatch(new Regex(@"as[A-Za-z_$][\w$]*<"), code);
    }


    [Fact]
    public void ClassImplementsIComparerOfOwnNestedType_QualifiesNestedTypeInImplementsClause()
    {
        var result = Convert(@"
using System.Collections.Generic;
class SegmentIntersector : IComparer<SegmentIntersector.SegEvent> {
    internal class SegEvent { }
    public int Compare(SegEvent a, SegEvent b) { return 0; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;

        Assert.Contains("class SegmentIntersector implements CSharpGenericComparer<SegmentIntersector.SegEvent>", code);
        Assert.DoesNotContain("class SegmentIntersector implements CSharpGenericComparer<SegEvent>", code);
    }

    [Fact]
    public void ClassImplementsSingleIComparer_UsesImplements()
    {
        var result = Convert(@"
using System.Collections.Generic;
class MyComparer : IComparer<string> {
    public int Compare(string a, string b) { return 0; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("implements CSharpGenericComparer<String>", code);
        Assert.DoesNotContain("extends CSharpGenericComparer", code);
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
    public void ClassExtendsBaseAndImplementsInterface_CorrectExtendsAndImplements()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Base { }
class Derived : Base, IComparer<Derived> {
    public int Compare(Derived a, Derived b) { return 0; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("extends Base", code);
        Assert.Contains("implements CSharpGenericComparer<Derived>", code);
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
