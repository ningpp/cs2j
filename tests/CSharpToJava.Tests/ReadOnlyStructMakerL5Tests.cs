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
        Assert.Contains("Counter Increment()", result.OutputCode);
        Assert.Contains("Counter result = this;", result.OutputCode);
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
        Assert.Contains("Acc result = this;", result.OutputCode);
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

    [Fact]
    public void CallSiteUpdater_DoesNotUpdateSameNameMethodsOnOtherTypes()
    {
        // Reproduces: SmallXmlNodeList.Add migrated, but List<object>.Add in same file
        // should NOT be transformed to assignment
        var src = """
        using System.Collections.Generic;
        class Container {
            private struct MyStruct {
                private object _field;
                public void Add(object value) { _field = value; }
            }
            void DoWork() {
                var s = new MyStruct();
                s.Add("hello");
                var list = new List<object>();
                list.Add("world");
            }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        // The struct's own call site should be updated
        Assert.Contains("s = s.Add(", result.OutputCode);
        // List<object>.Add should NOT be updated
        Assert.DoesNotContain("list = list.Add(", result.OutputCode);
    }

    [Fact]
    public void ConstructorBareCallToMigratedMethod_UpdatedToFieldAssignment()
    {
        // Reproduces: XsdDateTime struct where constructor calls InitiateXsdDateTime(parser)
        // as a bare call (no receiver). The migrated method returns the struct, but
        // the constructor discards the return value, leaving fields uninitialized.
        var src = """
        struct MyDate {
            private object _dt;
            private int _extra;
            public MyDate(string text) : this() {
                Init(text);
            }
            private void Init(string text) {
                _dt = text;
                _extra = 1;
            }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        // The migrated method should return MyDate
        Assert.Contains("MyDate Init(", result.OutputCode);
        // The constructor bare call is expanded into a temp + field-wise copy
        // (Java cannot assign to `this`).
        Assert.Contains("var __cs2jSelf = this.Init(text);", result.OutputCode);
        Assert.Contains("this._dt = __cs2jSelf._dt;", result.OutputCode);
        Assert.Contains("this._extra = __cs2jSelf._extra;", result.OutputCode);
        // Ensure no bare "Init(text);" without a receiver (check line doesn't start with just Init)
        var lines = result.OutputCode.Split('\n');
        Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith("Init(text)"));
    }

    [Fact]
    public void MethodModifyingLocalVariableField_NotConsideredMutating()
    {
        // Reproduces: CompassVector.ToPoint() modifies local Point p (p.X += 1)
        // which should NOT be classified as mutating the struct.
        var src = """
        struct Point {
            public double X;
            public double Y;
        }
        struct Compass {
            private int _dir;
            public Compass(int dir) { _dir = dir; }
            public Point ToPoint() {
                var p = new Point();
                if (_dir == 1) p.X += 1;
                if (_dir == 2) p.Y += 1;
                return p;
            }
        }
        """;
        var result = RunMaker(src);
        // Compass should be converted to readonly (DirectAdd) since ToPoint()
        // does NOT mutate the struct, only a local variable.
        Assert.True(result.Changed);
        Assert.Contains("readonly struct Compass", result.OutputCode);
        // Should NOT have method migration artifacts
        Assert.DoesNotContain("out Compass", result.OutputCode);
        Assert.DoesNotContain("newStatus", result.OutputCode);
    }

    [Fact]
    public void CtorAssignmentToPrivateSetterProperty_Unqualified_RewrittenToBackingField()
    {
        // Reproduces the SDK FacetsCompiler shape: L5 method migration removes the
        // private setter, but the constructor assigns the property with an unqualified
        // name (`Limit = limit;`). That assignment must be redirected to the setter's
        // backing field or the rewritten struct fails with CS0200.
        var src = """
        struct Bag {
            private int _count;
            private int? __limit;
            private bool _dirty;
            private int Limit {
                get { return __limit ?? 10; }
                set { __limit = value; }
            }
            public Bag(int count, int limit) {
                _count = count;
                _dirty = false;
                Limit = limit;
            }
            public void Add() { _count++; }
            public int Total { get { return _count + Limit; } }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.MethodMigrate);
        Assert.Contains("readonly struct Bag", result.OutputCode);
        Assert.Contains("__limit = limit", result.OutputCode);
        Assert.DoesNotContain("Limit = limit", result.OutputCode);
        Assert.Empty(GetCompileErrors(result.OutputCode!));
    }

    private static IReadOnlyList<Diagnostic> GetCompileErrors(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("test_output",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
    }
}
