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
    public void InternalFields_ExternalAssignmentViaArray_NotConvertible()
    {
        // Reproduces: XmlTextWriter.TagInfo struct with internal fields assigned
        // externally through array element access: _stack[_top].name = localName;
        var src = """
        class Writer {
            private struct TagInfo {
                internal string name;
                internal string prefix;
                internal int prefixCount;
                internal void Init(int nsTop) {
                    name = null;
                    prefixCount = 0;
                }
            }
            private TagInfo[] _stack = new TagInfo[10];
            private int _top;
            void WriteElement(string localName) {
                _stack[_top].name = localName;
                _stack[_top].prefix = null;
                _stack[_top].prefixCount = 0;
            }
        }
        """;
        var result = RunMaker(src);
        // Should NOT be made readonly because internal fields are assigned externally
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.NotConvertible);
        Assert.DoesNotContain("readonly struct TagInfo", result.OutputCode);
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
        // The constructor bare call should be updated to capture the result and assign fields
        Assert.Contains("__tmp = this.Init(text)", result.OutputCode);
        Assert.Contains("this._dt = __tmp._dt", result.OutputCode);
        Assert.Contains("this._extra = __tmp._extra", result.OutputCode);
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
    public void L5Struct_PropertySetterMigration_ObjectInitializer_ConvertedToConstructor()
    {
        // Reproduces: BorderInfo struct with mutating methods (SetFixed) and public
        // property setters (InnerMargin, FixedPosition, Weight). L5 migration converts
        // setters to WithXxx methods and removes them. Object initializers assigning
        // to those properties must be converted to constructor calls, otherwise the
        // Java converter generates setXxx() calls that no longer exist.
        var src = """
        struct BorderInfo {
            public double InnerMargin { get; set; }
            public double FixedPosition { get; set; }
            public double Weight { get; set; }
            public void SetFixed(double position, double weight) {
                FixedPosition = position;
                Weight = weight;
            }
            public BorderInfo(double innerMargin, double fixedPosition, double weight) {
                InnerMargin = innerMargin;
                FixedPosition = fixedPosition;
                Weight = weight;
            }
        }
        class Reader {
            BorderInfo Read() {
                return new BorderInfo { InnerMargin = 1.0, FixedPosition = 2.0, Weight = 3.0 };
            }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        // Property setters should be migrated to WithXxx methods
        Assert.Contains("WithInnerMargin(", result.OutputCode);
        Assert.Contains("WithFixedPosition(", result.OutputCode);
        Assert.Contains("WithWeight(", result.OutputCode);
        // Object initializer should be converted to constructor call with named parameters
        Assert.Contains("new BorderInfo(innerMargin: 1.0, fixedPosition: 2.0, weight: 3.0)", result.OutputCode);
        Assert.DoesNotContain("{ InnerMargin = 1.0", result.OutputCode);
    }
}
