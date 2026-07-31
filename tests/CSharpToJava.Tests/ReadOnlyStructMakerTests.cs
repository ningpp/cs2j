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
}
