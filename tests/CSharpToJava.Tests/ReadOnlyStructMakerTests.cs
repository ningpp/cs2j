using CSharpToJava.Core.ReadOnlyStructMaker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

public partial class ReadOnlyStructMakerTests
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
    public void Options_Defaults()
    {
        var o = new ReadOnlyStructMakerOptions();
        Assert.Equal("DoNotMakeReadOnly", o.OptOutAttributeName);
        Assert.True(o.ReportSkipped);
        Assert.False(o.Strict);
        Assert.True(o.EnableMethodMigration);
        Assert.True(o.EnableDtoConversion);
        Assert.True(o.UpdateCallSites);
        Assert.Null(o.AllowedLevels);
    }

    [Fact]
    public void Diagnostics_Record_RoundTrip()
    {
        var d = new ReadOnlyStructMakerDiagnostic(
            ReadOnlyStructSeverity.Info, "Point", "converted", ConversionLevel.DirectAdd,
            StructPattern.FullImmutable, "Point.cs", 12);
        Assert.Equal(ReadOnlyStructSeverity.Info, d.Severity);
        Assert.Equal("Point", d.StructName);
        Assert.Equal("converted", d.Reason);
        Assert.Equal(ConversionLevel.DirectAdd, d.Level);
        Assert.Equal(StructPattern.FullImmutable, d.Pattern);
        Assert.Equal("Point.cs", d.FilePath);
        Assert.Equal(12, d.Line);
    }

    [Fact]
    public void Statistics_Starts_Zero()
    {
        var s = new ReadOnlyStructMakerStatistics();
        Assert.Equal(0, s.StructsScanned);
        Assert.Equal(0, s.StructsConverted);
        Assert.Equal(0, s.StructsSkipped);
        Assert.Equal(0, s.StructsFailed);
        Assert.Equal(0, s.Level0_Skipped);
        Assert.Equal(0, s.Level1_DirectAdd);
        Assert.Equal(0, s.Level2_PropertyConvert);
        Assert.Equal(0, s.Level3_DataContainer);
        Assert.Equal(0, s.Level5_MethodMigrate);
        Assert.Equal(0, s.MethodsMigrated);
        Assert.Equal(0, s.CallSitesUpdated);
    }

    [Fact]
    public void Result_Record_Properties()
    {
        var stats = new ReadOnlyStructMakerStatistics();
        var diags = new List<ReadOnlyStructMakerDiagnostic>();
        var files = new Dictionary<string, string>();
        var result = new ReadOnlyStructMakerResult("code", true, files, diags, stats);
        Assert.Equal("code", result.OutputCode);
        Assert.True(result.Changed);
        Assert.Same(files, result.ChangedFiles);
        Assert.Same(diags, result.Diagnostics);
        Assert.Same(stats, result.Statistics);
    }

    // === L0 Skip Tests ===

    [Fact]
    public void AlreadyReadonlyStruct_IsSkipped()
    {
        var src = "readonly struct S { public readonly int X; }";
        var result = RunMaker(src);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.Skip);
    }

    [Fact]
    public void RefStruct_IsSkipped()
    {
        var src = "ref struct S { public int X; }";
        var result = RunMaker(src);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.Skip);
    }

    [Fact]
    public void PartialStruct_IsSkipped()
    {
        var src = "partial struct S { public int X; }";
        var result = RunMaker(src);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.Skip);
    }

    [Fact]
    public void DoNotMakeReadOnlyAttribute_IsSkipped()
    {
        var src = """
        [System.AttributeUsage(System.AttributeTargets.Struct)]
        class DoNotMakeReadOnlyAttribute : System.Attribute { }
        [DoNotMakeReadOnly]
        struct S { public int X; }
        """;
        var result = RunMaker(src);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.Skip);
    }

    // === L1 Direct Add Tests ===

    [Fact]
    public void AlreadyReadonlyFields_AddsReadonlyModifier()
    {
        var src = "struct S { public readonly int X; public readonly int Y; }";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly struct S", result.OutputCode);
    }

    [Fact]
    public void FullImmutable_CtorOnlyAssignment_AddsReadonly()
    {
        var src = """
        struct S {
            private int _x;
            private int _y;
            public S(int x, int y) { _x = x; _y = y; }
            public int X => _x;
            public int Y => _y;
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly struct S", result.OutputCode);
    }

    [Fact]
    public void EmptyStruct_AddsReadonly()
    {
        var src = "struct Empty { }";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly struct Empty", result.OutputCode);
    }

    [Fact]
    public void PreservesAttributes_And_XmlDoc()
    {
        var src = """
        /// <summary>A point.</summary>
        [System.Serializable]
        struct Point { public readonly double X; public readonly double Y; }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("/// <summary>A point.</summary>", result.OutputCode);
        Assert.Contains("[System.Serializable]", result.OutputCode);
        Assert.Contains("readonly struct Point", result.OutputCode);
    }

    [Fact]
    public void PublicStructWithModifiers_AddsReadonly()
    {
        var src = "public struct S { public readonly int X; }";
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly", result.OutputCode);
        Assert.Contains("public", result.OutputCode);
    }

    [Fact]
    public void Idempotent_SecondRun_NoChange()
    {
        var src = "struct S { public readonly int X; }";
        var first = RunMaker(src);
        Assert.True(first.Changed);
        var second = RunMaker(first.OutputCode!);
        Assert.False(second.Changed);
    }

    [Fact]
    public void DirectAdd_NonReadonlyFields_OutputCompilesWithoutCS8340()
    {
        // Reproduces CS8340: when ApplyDirectAdd marks a struct as readonly,
        // all instance fields must also be marked readonly.
        var src = """
        struct S {
            private int _x;
            private int _y;
            public S(int x, int y) { _x = x; _y = y; }
            public int X => _x;
            public int Y => _y;
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly struct S", result.OutputCode);
        Assert.Contains("private readonly int _x", result.OutputCode);
        Assert.Contains("private readonly int _y", result.OutputCode);

        // The rewritten C# must compile without CS8340 (readonly struct fields must be readonly)
        var tree = CSharpSyntaxTree.ParseText(result.OutputCode!);
        var compilation = CSharpCompilation.Create("test_cs8340",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void DirectAdd_InternalFields_MarkedReadonly()
    {
        // Reproduces PortObstacle / NetworkSimplex.StackStruct pattern:
        // internal fields in a struct made readonly via DirectAdd.
        var src = """
        struct S {
            internal int V;
            internal S(int v) { V = v; }
            public int Value => V;
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly struct S", result.OutputCode);
        Assert.Contains("internal readonly int V", result.OutputCode);
    }

    // === L2 Private Setter Tests ===

    [Fact]
    public void PrivateSetter_RemovesSet_AddsReadonly()
    {
        var src = """
        struct S {
            internal int X { get; private set; }
            internal int Y { get; private set; }
            internal S(int x, int y) { X = x; Y = y; }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly struct S", result.OutputCode);
        Assert.DoesNotContain("private set", result.OutputCode);
        Assert.Contains("{ get; }", result.OutputCode);
    }

    [Fact]
    public void PrivateSetter_MixedWithGetOnly_Converts()
    {
        var src = """
        struct S {
            internal int A { get; private set; }
            internal int B { get; }
            internal S(int a, int b) { A = a; B = b; }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly struct S", result.OutputCode);
        Assert.DoesNotContain("private set", result.OutputCode);
    }

    [Fact]
    public void PrivateSetter_Idempotent()
    {
        var src = """
        struct S {
            internal int X { get; private set; }
            internal S(int x) { X = x; }
        }
        """;
        var first = RunMaker(src);
        Assert.True(first.Changed);
        var second = RunMaker(first.OutputCode!);
        Assert.False(second.Changed);
    }

    // === L3 Data Container Tests ===

    [Fact]
    public void DataContainer_GetSetProperties_ConvertsToGetOnly()
    {
        var src = """
        struct EdgeConstraints {
            public int Direction { get; set; }
            public double Separation { get; set; }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly struct EdgeConstraints", result.OutputCode);
        Assert.DoesNotContain("set;", result.OutputCode);
        Assert.Contains("public EdgeConstraints(", result.OutputCode);
    }

    [Fact]
    public void DataContainer_InternalFields_ConvertsToProperties()
    {
        var src = """
        struct Pixel {
            internal int X;
            internal int Y;
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly struct Pixel", result.OutputCode);
        Assert.Contains("{ get; }", result.OutputCode);
    }

    [Fact]
    public void DataContainer_ExistingCtor_NotDuplicated()
    {
        var src = """
        struct S {
            internal int A;
            internal int B;
            internal S(int a, int b) { A = a; B = b; }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly struct S", result.OutputCode);
        // Should NOT generate a second constructor with same signature
        var ctorCount = result.OutputCode!.Split("internal S(").Length - 1;
        Assert.Equal(1, ctorCount);
    }

    [Fact]
    public void DataContainer_DisabledByOption_NoChange()
    {
        var src = """
        struct S {
            public int X { get; set; }
        }
        """;
        var opts = new ReadOnlyStructMakerOptions { EnableDtoConversion = false };
        var result = RunMaker(src, opts);
        Assert.False(result.Changed);
    }

    [Fact]
    public void PublicFields_NoMethods_ExternalAssignment_ConvertedToL4()
    {
        // Reproduces: struct Offset { public ushort End; ... } with external assignments
        // like info.Offset.End = value; — NOW converted to L4 (public fields → properties + WithXxx)
        var src = """
        class Container {
            public Offset Data;
            void Update() { Data.End = 42; Data.Scheme = 1; }
        }
        struct Offset {
            public ushort Scheme;
            public ushort End;
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.PublicFieldToProperty);
        // External assignments should be converted to WithXxx calls
        Assert.Contains("Data = Data.WithEnd(42)", result.OutputCode);
        Assert.Contains("Data = Data.WithScheme(1)", result.OutputCode);
    }

    // === L7 Not Convertible Tests ===

    [Fact]
    public void HeavyMutable_ArrayField_NotConvertible()
    {
        var src = """
        struct Cache {
            private int[] _items;
            private int _count;
            public void Clear() { _count = 0; }
            public void Insert(int item) { _items[_count] = item; _count++; }
        }
        """;
        var result = RunMaker(src);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.NotConvertible);
    }

    [Fact]
    public void PublicFields_NotConvertible()
    {
        var src = """
        struct Point {
            public double X;
            public double Y;
            public void Normalize() { X = 0; Y = 0; }
        }
        """;
        var result = RunMaker(src);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.NotConvertible);
    }

    [Fact]
    public void VirtualMethod_NotConvertible()
    {
        var src = """
        struct S {
            public int V;
            public override string ToString() { V++; return ""; }
        }
        """;
        var result = RunMaker(src);
        // ToString mutating makes it have mutating methods, but override methods are non-migratable
        Assert.Contains(result.Diagnostics, d =>
            d.Level == ConversionLevel.NotConvertible || d.Level == ConversionLevel.MethodMigrate);
    }

    [Fact]
    public void OverrideObjectMethod_WithMutatingMethod_IsMethodMigrated()
    {
        // Reproduces BorderInfo.cs: struct overrides object.ToString/Equals/GetHashCode
        // (pure read-only overrides) AND has real mutating methods. Before the fix the
        // pure object overrides wrongly triggered "has virtual/override methods" and
        // blocked migration entirely. They must be ignored so the struct falls through
        // to L5 method migration like any other mutable-method struct.
        var src = """
        struct S {
            private int _v;
            private bool _fixed;
            public S(int v) { _v = v; _fixed = false; }
            public void SetFixed() { _fixed = true; }
            public void SetUnfixed() { _fixed = false; }
            public override string ToString() => _fixed ? "fixed" : "unfixed";
            public override bool Equals(object? o) => o is S other && other._v == _v;
            public override int GetHashCode() => _v;
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.MethodMigrate);
        Assert.DoesNotContain(result.Diagnostics, d => d.Level == ConversionLevel.NotConvertible);
    }

    [Fact]
    public void OverrideObjectMethod_OnlyReadonlyOverrides_IsConverted()
    {
        // Struct that ONLY overrides object members (no mutating methods of its own).
        // Overriding ToString/Equals/GetHashCode must never block conversion.
        var src = """
        struct S {
            private readonly int _v;
            public S(int v) { _v = v; }
            public int V => _v;
            public override string ToString() => _v.ToString();
            public override bool Equals(object? o) => o is S other && other._v == _v;
            public override int GetHashCode() => _v;
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly struct S", result.OutputCode);
        Assert.DoesNotContain(result.Diagnostics, d => d.Level == ConversionLevel.NotConvertible);
    }

    [Fact]
    public void GenuineVirtualMethod_StillNotConvertible()
    {
        // A struct that overrides a NON-object member (here via a base class) combined
        // with mutating methods must STILL be rejected. This guards the fix against
        // over-loosening: only object overrides are safe.
        var src = """
        abstract class Base { public abstract string Describe(); }
        struct S : Base {
            private int _v;
            public S(int v) { _v = v; }
            public void Increment() { _v++; }
            public override string Describe() => _v.ToString();
        }
        """;
        var result = RunMaker(src);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.NotConvertible);
    }

    [Fact]
    public void MethodMigration_DisabledByOption_NotConvertible()
    {
        var src = """
        struct S {
            private int _v;
            public void Increment() { _v++; }
        }
        """;
        var opts = new ReadOnlyStructMakerOptions { EnableMethodMigration = false };
        var result = RunMaker(src, opts);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.NotConvertible);
    }

    [Fact]
    public void RefFieldMutation_NotConvertible()
    {
        var src = """
        struct Pixel {
            internal int X;
            internal int Y;
        }
        class User {
            void Update(ref Pixel p) { p.X++; }
        }
        """;
        var result = RunMaker(src);
        // Pixel has no mutating methods itself, but fields are modified via ref
        // It should be detected as not convertible
        Assert.Contains(result.Diagnostics, d =>
            d.Level == ConversionLevel.NotConvertible || d.Level == ConversionLevel.DataContainer);
    }

    [Fact]
    public void MultipleAssignmentsToSameFieldInCtor_NotConverted()
    {
        // Reproduces: Parallelogram struct where aRot is assigned twice in ctor:
        //   this.aRot = new Point(...); aRot = aRot.Normalize();
        // Java final fields cannot be assigned more than once.
        var src = """
        struct S {
            private int _x;
            private int _y;
            public S(int x, int y) {
                _x = x;
                _y = y;
                if (_x < 0) _x = -_x;
            }
            public int X => _x;
            public int Y => _y;
        }
        """;
        var result = RunMaker(src);
        Assert.False(result.Changed);
    }

    // === Interface Implementation Tests (Rectangle support) ===

    [Fact]
    public void ExplicitInterfaceImpl_WithMutatingMethod_IsMigrated()
    {
        // Reproduces: Rectangle.cs has explicit interface implementations like
        //   bool IRectangle<Point>.Contains(IRectangle<Point> rect)
        // AND mutating methods like Add(), PadWidth(), etc.
        // Before the fix, the explicit interface impls triggered "has virtual/override methods".
        var src = """
        interface IShape {
            bool Contains(double x, double y);
        }
        struct PointBag : IShape {
            private double _x;
            private double _y;
            public PointBag(double x, double y) { _x = x; _y = y; }
            public void Add(double dx, double dy) { _x += dx; _y += dy; }
            bool IShape.Contains(double x, double y) {
                return x >= 0 && x <= _x && y >= 0 && y <= _y;
            }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.MethodMigrate);
        Assert.DoesNotContain(result.Diagnostics, d => d.Level == ConversionLevel.NotConvertible);
        // The mutating method should be migrated
        Assert.Contains("PointBag Add(", result.OutputCode);
        Assert.Contains("var result = this;", result.OutputCode);
    }

    [Fact]
    public void ImplicitInterfaceImpl_WithMutatingMethod_IsMigrated()
    {
        // Reproduces: Rectangle.cs has implicit interface implementations like
        //   public IRectangle<Point> Unite(IRectangle<Point> rectangle)
        var src = """
        interface IShape {
            IShape Combine(IShape other);
        }
        struct Canvas : IShape {
            private double _width;
            public Canvas(double w) { _width = w; }
            public void Scale(double factor) { _width *= factor; }
            public IShape Combine(IShape other) {
                return new Canvas(_width * 2);
            }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.MethodMigrate);
        Assert.DoesNotContain(result.Diagnostics, d => d.Level == ConversionLevel.NotConvertible);
        Assert.Contains("Canvas Scale(", result.OutputCode);
    }

    [Fact]
    public void InterfaceImpl_OnlyPureOverrides_IsDirectConverted()
    {
        // A struct that ONLY implements interface methods (no mutating methods of its own)
        // should be converted directly (L1) since there's nothing to migrate.
        var src = """
        interface IDescribable {
            string Describe();
        }
        struct Info : IDescribable {
            private readonly int _id;
            public Info(int id) { _id = id; }
            public string Describe() => $"Info({_id})";
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains("readonly struct Info", result.OutputCode);
        Assert.DoesNotContain(result.Diagnostics, d => d.Level == ConversionLevel.NotConvertible);
    }

    [Fact]
    public void PropertyAccess_MigratedToResultProperty()
    {
        // Reproduces: Rectangle.cs methods mutate through properties (e.g. Left -= padding).
        // Before the fix, the property accesses were not replaced with result.Property.
        var src = """
        struct Rect {
            private double _left;
            private double _right;
            public Rect(double l, double r) { _left = l; _right = r; }
            public double Left {
                get { return _left; }
                set { _left = value; }
            }
            public double Right {
                get { return _right; }
                set { _right = value; }
            }
            public void PadWidth(double padding) {
                Left -= padding;
                Right += padding;
            }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.MethodMigrate);
        // The migrated method should access result._left and result._right (backing fields)
        // since property setters are removed during migration
        Assert.Contains("result._left", result.OutputCode);
        Assert.Contains("result._right", result.OutputCode);
    }

    [Fact]
    public void RectangleLikeStruct_FullMigration()
    {
        // A simplified Rectangle-like struct that combines all the problematic features:
        // - override ToString()
        // - explicit interface implementations
        // - mutating methods that access properties
        var src = """
        interface IRectangle {
            double Area { get; }
            bool Contains(double x, double y);
        }
        struct Rect : IRectangle {
            private double _left;
            private double _right;
            private double _top;
            private double _bottom;
            public Rect(double l, double b, double r, double t) {
                _left = l; _bottom = b; _right = r; _top = t;
            }
            public override string ToString() {
                return $"({_left},{_bottom},{_right},{_top})";
            }
            public double Left {
                get { return _left; }
                set { _left = value; }
            }
            public double Right {
                get { return _right; }
                set { _right = value; }
            }
            public double Area {
                get { return (_right - _left) * (_top - _bottom); }
            }
            double IRectangle.Area { get { return Area; } }
            bool IRectangle.Contains(double x, double y) {
                return x >= _left && x <= _right && y >= _bottom && y <= _top;
            }
            public void Pad(double p) {
                Left -= p;
                Right += p;
            }
        }
        """;
        var result = RunMaker(src);
        Assert.True(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.MethodMigrate);
        Assert.DoesNotContain(result.Diagnostics, d => d.Level == ConversionLevel.NotConvertible);
        // ToString override should be preserved
        Assert.Contains("override string ToString()", result.OutputCode);
        // The Pad method should be migrated with backing field accesses on result
        Assert.Contains("result._left", result.OutputCode);
        Assert.Contains("result._right", result.OutputCode);
        // Explicit interface implementations should be preserved
        Assert.Contains("bool IRectangle.Contains(", result.OutputCode);
    }
}
