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
    public void InternalFields_ExternalAssignmentViaArray_ConvertedViaWithMethods()
    {
        // Reproduces: XmlTextWriter.TagInfo struct with internal fields assigned
        // externally through array element access: _stack[_top].name = localName;
        // Combined L4+L5 conversion: fields become get-only properties with WithXxx
        // methods, mutating methods are migrated, and external assignments are
        // rewritten to WithXxx calls.
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
        Assert.True(result.Changed);
        Assert.Contains("readonly struct TagInfo", result.OutputCode);
        Assert.Contains("TagInfo Init(", result.OutputCode);
        // External array-element assignments are rewritten via WithXxx
        Assert.Contains("_stack[_top] = _stack[_top].Withname(localName)", result.OutputCode);
        Assert.DoesNotContain(result.Diagnostics, d => d.Level == ConversionLevel.NotConvertible);
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
        // Object initializer should be converted to a WithXxx chain (robust regardless
        // of constructor parameter names)
        Assert.Contains("new BorderInfo().WithInnerMargin(1.0).WithFixedPosition(2.0).WithWeight(3.0)", result.OutputCode);
        Assert.DoesNotContain("{ InnerMargin = 1.0", result.OutputCode);
    }

    [Fact]
    public void PartialStructMethodDeclaredOnlyInStubFile_DoesNotCrash()
    {
        // Reproduces: System.Text.Json ref-assembly stubs (ref\System.Text.Json.cs).
        // The real declaration `struct ArrayEnumerator` (no partial) merges with the
        // stub `partial struct ArrayEnumerator` in the ref file, so member symbols
        // like Dispose resolve their DeclaringSyntaxReference to the stub tree.
        // StructAnalyzer must only analyze methods declared in the model's own tree.
        var srcTree = CSharpSyntaxTree.ParseText("""
        struct Frame {
            private int _x;
            public void Reset() { _x = 0; }
        }
        """, path: "src.cs");
        var stubTree = CSharpSyntaxTree.ParseText("""
        partial struct Frame {
            public void Dispose() { }
        }
        """, path: "stub.cs");
        var compilation = CSharpCompilation.Create("test",
            new[] { srcTree, stubTree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = new Core.ReadOnlyStructMaker.ReadOnlyStructMaker()
            .MakeReadOnly(srcTree, compilation.GetSemanticModel(srcTree));

        // Stub-only method must not crash analysis; the real method is migrated.
        Assert.True(result.Changed);
        Assert.Contains("Frame Reset(", result.OutputCode);
    }

    [Fact]
    public void LocalDeclarationTypeNamedLikeField_NotRewrittenToMemberAccess()
    {
        // Reproduces: System.Text.Json WriteStackFrame struct whose public fields
        // share names with types (e.g. `public JsonClassInfo JsonClassInfo;`).
        // Method migration rewrites implicit field accesses to result.<field>, but
        // must NOT rewrite identifiers in TYPE position (local declaration types),
        // otherwise Roslyn's VisitVariableDeclaration cast (TypeSyntax) crashes.
        var src = """
        class JsonClassInfo {
            public JsonClassInfo PropertyInfoForClassInfo;
        }
        class JsonConverter {
            public JsonConverter ConverterBase;
        }
        struct Frame {
            public JsonClassInfo JsonClassInfo;
            public JsonConverter InitializeReEntry() {
                JsonClassInfo classInfo = null;
                JsonClassInfo = classInfo;
                return JsonClassInfo.ConverterBase;
            }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        // The local declaration type must stay a plain identifier
        Assert.Contains("JsonClassInfo classInfo = null", result.OutputCode);
        // No invalid member access in type position
        Assert.DoesNotContain("result.JsonClassInfo classInfo", result.OutputCode);
        // Implicit field READS are still rewritten to result.JsonClassInfo
        Assert.Contains("result.JsonClassInfo.ConverterBase", result.OutputCode);
    }

    [Fact]
    public void PrefixDecrementInArrayIndex_PreservesOffByOneSemantics()
    {
        // Reproduces: WriteStack.Pop `Current = _previous[--_count - 1];`.
        // The hoisted temp must keep the original value semantics:
        //   --_count - 1 == (oldCount - 1) - 1, NOT oldCount - 1.
        var src = """
        struct Frame {
            public int Current;
        }
        struct Stack2 {
            private int _count;
            private Frame[] _previous;
            public Frame Pop() {
                if (_count > 1) {
                    Current = _previous[--_count - 1];
                }
                return Current;
            }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        // The decrement write-back must use the OLD value minus one…
        Assert.Contains("__cs2jOld_count_0 - 1)", result.OutputCode);
        // …and the array index must be (old - 1) - 1, i.e. old - 2.
        Assert.Contains("__cs2jOld_count_0 - 1) - 1", result.OutputCode);
        Assert.DoesNotContain("_previous[__cs2jOld_count_0 - 1]", result.OutputCode);
    }

    [Fact]
    public void PrefixIncrementInExpression_ReturnsNewValue()
    {
        // --/++ used as an operand must yield the NEW value (temp ± 1),
        // not the old value, and must be parenthesized for precedence.
        var src = """
        struct Counter2 {
            private int _count;
            public int Next() {
                int x = --_count * 2;
                return x;
            }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("__cs2jOld_count_0 - 1) * 2", result.OutputCode);
        Assert.DoesNotContain("__cs2jOld_count_0 * 2", result.OutputCode);
    }

    [Fact]
    public void BareInstanceMethodCall_ReadsResultNotStaleThis()
    {
        // Reproduces: BitStack.Pop calling PopFromArray() after decrementing
        // _currentDepth through the `result` local. The bare call must be
        // rewritten to result.PopFromArray() so it observes the new depth.
        var src = """
        struct Stack3 {
            private int _currentDepth;
            public bool Pop() {
                _currentDepth--;
                bool inObject = false;
                if (_currentDepth >= 2) {
                    inObject = PopFromArray();
                }
                return inObject;
            }
            private bool PopFromArray() { return _currentDepth > 0; }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("inObject = result.PopFromArray()", result.OutputCode);
        Assert.DoesNotContain("= PopFromArray()", result.OutputCode);
        Assert.Contains("result = result.With_currentDepth(result._currentDepth - 1)", result.OutputCode);
    }

    [Fact]
    public void MixedPublicAndPrivateFields_WithMethodsPreserveAllState()
    {
        // Reproduces: System.Text.Json ReadStack/WriteStack structs with BOTH public
        // fields (converted to get-only properties) and private fields. The full
        // constructor must assign every storage member; otherwise WithCurrent etc.
        // silently drop _count/_previous and deserialization breaks.
        var src = """
        class JsonClassInfo { }
        struct Frame {
            public JsonClassInfo JsonClassInfo;
        }
        struct Stack4 {
            private int _count;
            private Frame[] _previous;
            public Frame Current;
            public int FlushThreshold;
            public Stack4 Reset() {
                Current = new Frame();
                return Current;
            }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        // The full constructor must cover private fields AND converted public props.
        Assert.Contains("_count", result.OutputCode);
        Assert.Contains("internal Stack4(", result.OutputCode);
        // WithCurrent must pass the private fields back through the constructor.
        Assert.Contains("WithCurrent(Frame current) => new Stack4(count: this._count", result.OutputCode);
        // With_count must pass the public properties back through the constructor.
        Assert.Contains("With_count(int _count) => new Stack4(count: _count, previous: this._previous, current: this.Current", result.OutputCode);
    }

    [Fact]
    public void MutatingInterfaceImplementation_NotConverted()
    {
        // Reproduces: IEnumerator/ITestClass-implementing structs (e.g.
        // JsonElement.ObjectEnumerator, SimpleTestStruct). Interface dispatch
        // happens on a boxed instance; the explicit implementation shim cannot
        // write the migrated struct result back through the box, so the mutation
        // would be silently lost. Such structs must stay mutable.
        var src = """
        interface IIt { void Advance(); }
        struct It : IIt {
            private int _cur;
            public int Current { get; }
            public void Advance() { _cur++; }
        }
        """;
        var result = RunMaker(src);
        Assert.False(result.Changed);
    }

    [Fact]
    public void SetterWithValidation_NotConverted()
    {
        // Reproduces: JsonReaderOptions — setter validation (if/throw) is silently
        // dropped by the L3 conversion (generated ctor assigns raw values).
        var src = """
        struct Opts {
            private int _maxDepth;
            public int MaxDepth {
                get => _maxDepth;
                set { if (value < 0) throw new System.ArgumentOutOfRangeException(); _maxDepth = value; }
            }
        }
        """;
        var result = RunMaker(src);
        Assert.False(result.Changed);
    }

    [Fact]
    public void PlainBackingFieldSetter_StillConverted()
    {
        // A setter that is a single plain `_field = value;` write carries no
        // semantics to preserve — the L3 conversion may proceed.
        var src = """
        struct Opts2 {
            private int _maxDepth;
            public int MaxDepth {
                get => _maxDepth;
                set { _maxDepth = value; }
            }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly struct Opts2", result.OutputCode);
    }

    [Fact]
    public void GenericStruct_MigratedMethodsKeepTypeParameter()
    {
        // Reproduces: ArrayBuilder<T> — void Add() migration emitted a bare
        // `ArrayBuilder` return type / object creation instead of `ArrayBuilder<T>`
        // (CS0305). The WithXxx methods generated from the compose constructor must
        // also carry the type parameter.
        var src = """
        struct ArrayBuilder<T> {
            private T[] _array;
            private int _count;
            public void Add(T item) { _count++; }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("ArrayBuilder<T> Add(", result.OutputCode);
        Assert.Contains("ArrayBuilder<T> With_array(", result.OutputCode);
        Assert.Contains("new ArrayBuilder<T>(", result.OutputCode);
        Assert.DoesNotContain("ArrayBuilder Add(", result.OutputCode);
        Assert.DoesNotContain("new ArrayBuilder(", result.OutputCode);
    }

    [Fact]
    public void GenericStruct_SetterWithMethod_KeepsTypeParameter()
    {
        // Body-based WithXxx generated from a property setter must use the full
        // generic struct type (ArrayBuilder<T>), not the bare name.
        var src = """
        struct Box<T> {
            private T _value;
            public T Value {
                get { return _value; }
                set { _value = value; }
            }
            public void Set(T v) { _value = v; }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("Box<T> WithValue(", result.OutputCode);
        Assert.DoesNotContain("Box WithValue(", result.OutputCode);
    }
}
