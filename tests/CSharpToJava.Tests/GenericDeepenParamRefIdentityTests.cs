using CSharpToJava.Core.GenericPreprocess;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// V-03 (upgrade plan §6.4-R0): <see cref="GenericDeepenParamRef"/> must have
/// ONE identity semantics across CompareTo, the record's Equals/GetHashCode
/// and every default-comparer collection. Historically CompareTo compared
/// only (kind, ordinal) while the record equality included Name, so a
/// SortedSet (CompareTo) deduplicated (T, "T") vs (T, "TValue") to one entry
/// while a List/HashSet (Equals) kept both — the SortedSet/List split that
/// forced the former NamedParamRefComparer local patch. Family-sync
/// collections that intentionally collapse renamed same-slot parameters use
/// the explicit family-slot projection, which these tests pin from the
/// outside through the analyzer's family-sync behavior.
/// </summary>
public sealed class GenericDeepenParamRefIdentityTests
{
    private static readonly GenericDeepenParamRef T0 =
        new(GenericDeepenParamKind.ClassTypeParameter, 0, "T");

    private static readonly GenericDeepenParamRef TValue0 =
        new(GenericDeepenParamKind.ClassTypeParameter, 0, "TValue");

    private static readonly GenericDeepenParamRef T1 =
        new(GenericDeepenParamKind.ClassTypeParameter, 1, "T");

    private static readonly GenericDeepenParamRef M0 =
        new(GenericDeepenParamKind.MethodTypeParameter, 0, "T");

    [Fact]
    public void CompareTo_agrees_with_Equals_for_every_pair()
    {
        // The .NET consistency contract: CompareTo == 0 iff Equals == true.
        var refs = new[] { T0, TValue0, T1, M0 };
        foreach (var x in refs)
        {
            foreach (var y in refs)
            {
                var comparesEqual = x.CompareTo(y) == 0;
                var equals = x.Equals(y);
                Assert.True(
                    comparesEqual == equals,
                    $"CompareTo({x}) == 0 is {comparesEqual} but Equals is {equals}.");
            }
        }
    }

    [Fact]
    public void Distinct_named_same_slot_refs_are_distinct_in_every_default_collection()
    {
        // Before V-03 the SortedSet collapsed these two (CompareTo on
        // (kind, ordinal)) while the List kept both (record equality on all
        // three components) — the dedup split.
        var sorted = new SortedSet<GenericDeepenParamRef> { T0, TValue0 };
        var hash = new HashSet<GenericDeepenParamRef> { T0, TValue0 };
        var list = new List<GenericDeepenParamRef> { T0, TValue0 };

        Assert.Equal(2, sorted.Count);
        Assert.Equal(2, hash.Count);
        Assert.Equal(2, list.Distinct().Count());
    }

    [Fact]
    public void Identical_refs_deduplicate_in_every_default_collection()
    {
        var sorted = new SortedSet<GenericDeepenParamRef> { T0, T0 with { } };
        var hash = new HashSet<GenericDeepenParamRef> { T0, T0 with { } };

        Assert.Single(sorted);
        Assert.Single(hash);
    }

    [Fact]
    public void Ordering_is_kind_then_ordinal_then_name()
    {
        var ordered = new[] { T0, M0, TValue0, T1 }
            .OrderBy(x => x, Comparer<GenericDeepenParamRef>.Default)
            .ToList();

        // ClassTypeParameter < MethodTypeParameter; within class kind by
        // ordinal; within the same slot by name.
        Assert.Equal(
            new[]
            {
                new GenericDeepenParamRef(GenericDeepenParamKind.ClassTypeParameter, 0, "T"),
                new GenericDeepenParamRef(GenericDeepenParamKind.ClassTypeParameter, 0, "TValue"),
                new GenericDeepenParamRef(GenericDeepenParamKind.ClassTypeParameter, 1, "T"),
                new GenericDeepenParamRef(GenericDeepenParamKind.MethodTypeParameter, 0, "T"),
            },
            ordered);
    }

    [Fact]
    public void GetHashCode_agrees_with_identity()
    {
        Assert.Equal(T0.GetHashCode(), (T0 with { }).GetHashCode());
        Assert.NotEqual(T0.GetHashCode(), TValue0.GetHashCode());
    }

    /// <summary>
    /// Family-slot semantics (the deliberate projection): a generic family
    /// whose override RENAMES the type parameter still carries at most ONE
    /// anchor parameter per (kind, ordinal) slot — the family-slot projection
    /// collapses (Class, 0, "T") and (Class, 0, "TValue") to one logical
    /// parameter. This pins the behavior the explicit ParamRefComparer
    /// projection preserves after the record identity was unified.
    /// </summary>
    [Fact]
    public void FamilySync_still_identifies_renamed_same_slot_parameters()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("""
            public class Base<T>
            {
                public virtual System.Type GetToken() => typeof(T);
            }

            public sealed class Derived<TValue> : Base<TValue>
            {
                public override System.Type GetToken() => typeof(TValue);
            }
            """);
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "gd-paramref-identity-" + System.Guid.NewGuid().ToString("N"),
            new[] { tree },
            new[] { Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        var preprocessor = new GenericDeepenPreprocessor();
        preprocessor.BindCompilation(compilation);
        var analysis = preprocessor.Analyze(new GenericDeepenOptions());

        // Both family members get anchors, and NO anchor ever carries two
        // parameters for the same (kind, ordinal) slot — the renamed pair
        // collapses to one logical parameter per slot.
        Assert.NotEmpty(analysis.Anchors);
        foreach (var anchor in analysis.Anchors)
        {
            var slots = anchor.Params.Select(p => (p.Kind, p.Ordinal)).ToList();
            Assert.True(
                slots.Count == slots.Distinct().Count(),
                $"anchor for '{anchor.Method.Name}' carries duplicate slot parameters: " +
                string.Join(", ", anchor.Params));
        }
    }
}
