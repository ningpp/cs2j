using CSharpToJava.Core.ReadOnlyStructMaker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

public class ReadOnlyStructMakerL5Tests
{
    private static ReadOnlyStructMakerResult RunMaker(string source, ReadOnlyStructMakerOptions? options = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new Core.ReadOnlyStructMaker.ReadOnlyStructMaker()
            .MakeReadOnly(tree, compilation.GetSemanticModel(tree), options);
    }

    [Fact]
    public void VoidMutatingMethod_MigratedToReturnStruct()
    {
        var src = """
        struct Counter {
            private int _value;
            public void Increment() { _value++; }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly struct Counter", result.OutputCode);
        Assert.Contains("Counter Increment()", result.OutputCode);
        Assert.Contains("var result = this;", result.OutputCode);
        Assert.Contains("return result;", result.OutputCode);
    }

    [Fact]
    public void ReturnThisMethod_MigratedToReturnCopy()
    {
        var src = """
        struct Acc {
            private int _v;
            public Acc Add(int n) { _v += n; return this; }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("var result = this;", result.OutputCode);
        Assert.Contains("return result;", result.OutputCode);
        Assert.DoesNotContain("return this;", result.OutputCode);
    }

    [Fact]
    public void MultipleMutatingMethods_AllMigrated()
    {
        var src = """
        struct Rect {
            private double _w;
            private double _h;
            public void SetWidth(double w) { _w = w; }
            public void SetHeight(double h) { _h = h; }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly struct Rect", result.OutputCode);
        Assert.Contains("Rect SetWidth(", result.OutputCode);
        Assert.Contains("Rect SetHeight(", result.OutputCode);
    }

    [Fact]
    public void NonMutatingMethod_NotMigrated()
    {
        var src = """
        struct S {
            private int _v;
            public int GetValue() { return _v; }
            public void SetValue(int v) { _v = v; }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        // GetValue should remain int return type
        Assert.Contains("int GetValue()", result.OutputCode);
        // SetValue should be migrated
        Assert.Contains("S SetValue(", result.OutputCode);
    }

    [Fact]
    public void L5_Statistics_Tracked()
    {
        var src = """
        struct S {
            private int _v;
            public void Inc() { _v++; }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Level5_MethodMigrate);
        Assert.Equal(1, result.Statistics.StructsConverted);
    }

    [Fact]
    public void VoidCallSite_UpdatedToAssignment()
    {
        var src = """
        struct Counter {
            private int _value;
            public void Increment() { _value++; }
        }
        class User {
            void M() {
                var c = new Counter();
                c.Increment();
            }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("c = c.Increment()", result.OutputCode);
    }

    [Fact]
    public void ArrayElementCallSite_UpdatedToAssignment()
    {
        var src = """
        struct Size {
            private double _w;
            public void Pad(double p) { _w += p; }
        }
        class User {
            void M(Size[] sizes) {
                sizes[0].Pad(5);
            }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("sizes[0] = sizes[0].Pad(5)", result.OutputCode);
    }

    [Fact]
    public void ReturnThisCallSite_NotUpdated()
    {
        var src = """
        struct Acc {
            private int _v;
            public Acc Add(int n) { _v += n; return this; }
        }
        class User {
            void M() {
                var a = new Acc();
                var b = a.Add(5);
            }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        // return-this call sites should NOT be changed to assignment
        Assert.Contains("var b = a.Add(5)", result.OutputCode);
        Assert.DoesNotContain("a = a.Add(5)", result.OutputCode);
    }

    [Fact]
    public void OtherReturnMethod_GetsOutParameter()
    {
        var src = """
        struct Rect {
            private double _left;
            public bool AddWithCheck(double x) {
                bool wider = x < _left;
                if (wider) _left = x;
                return wider;
            }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("out Rect newStatus", result.OutputCode);
        Assert.Contains("newStatus = result", result.OutputCode);
    }

    [Fact]
    public void PropertySetter_MigratedToWithMethod()
    {
        var src = """
        struct Box {
            private double _w;
            public double W {
                get { return _w; }
                set { _w = value; }
            }
            public void Pad(double p) { _w += p; }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly struct Box", result.OutputCode);
        Assert.Contains("WithW(", result.OutputCode);
    }

    [Fact]
    public void ValidatedSetter_PreservesValidation()
    {
        var src = """
        struct Config {
            private double _weight;
            public double Weight {
                get { return _weight; }
                set {
                    if (value <= 0) throw new System.ArgumentOutOfRangeException("value");
                    _weight = value;
                }
            }
            public void Reset() { _weight = 1.0; }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("WithWeight(", result.OutputCode);
        Assert.Contains("ArgumentOutOfRangeException", result.OutputCode);
    }
}
