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
        Assert.Contains("Iterable<String>", code);
        Assert.Contains("Iterable<Integer>", code);
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
        Assert.Contains("List<String>", code);
        Assert.DoesNotContain("IList", code);
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
        Assert.Contains("private ArrayList<Integer> items", code);
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
        Assert.Contains("Map<String, ArrayList<Integer>>", code);
        Assert.DoesNotContain("Dictionary", code);
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
        Assert.Contains("Comparator<String>", code);
        Assert.DoesNotContain("IComparer", code);
    }

    [Fact]
    public void EventHandlerField_MapsToConsumer()
    {
        var result = Convert(@"
using System;
class Test {
    EventHandler handler;
    EventHandler<string> typedHandler;
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("Consumer handler", code);
        Assert.Contains("Consumer<String>", code);
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
        Assert.Contains("List<String> getNames", code);
        Assert.Contains("Predicate<Integer> getPredicate", code);
        Assert.DoesNotContain("IList<", code);
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
        Assert.DoesNotContain("extends Comparator", code);
        // Both interfaces extracted to helper methods (Java type erasure)
        Assert.Contains("asSegEventComparer", code);
        Assert.Contains("asScanSegmentComparer", code);
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
        Assert.Contains("implements Comparator<String>", code);
        Assert.DoesNotContain("extends Comparator", code);
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
        Assert.Contains("implements Comparator<Derived>", code);
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
