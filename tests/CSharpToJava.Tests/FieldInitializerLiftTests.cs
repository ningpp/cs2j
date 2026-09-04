using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for field initializer pre-statement lifting.
///
/// When a C# field initializer requires pre-statements (e.g. Interlocked.Increment
/// wraps a static field in an IntHolder), those pre-statements must be lifted into
/// the constructor — not inlined into the field initializer as a multi-line string.
///
/// Broken output (before fix):
///   private int _objectID = /* Object initializer — consider moving to constructor. */
///   io.github.ningpp.compat.IntHolder _s_objectTypeCountRef = new io.github.ningpp.compat.IntHolder(s_objectTypeCount)
///   Interlocked.increment(_s_objectTypeCountRef);
///
/// Correct output (after fix):
///   private int _objectID;
///   // In constructor:
///   IntHolder _s_objectTypeCountRef = new IntHolder(s_objectTypeCount);
///   this._objectID = Interlocked.increment(_s_objectTypeCountRef);
/// </summary>
public class FieldInitializerLiftTests
{
    [Fact]
    public void InterlockedIncrement_StaticFieldInitializer_LiftsToConstructor()
    {
        var result = Convert(@"
public class DataItem
{
    private static int s_typeCount;
    private readonly int _objectID = System.Threading.Interlocked.Increment(ref s_typeCount);
    public int ObjectID { get { return _objectID; } }
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // The field must NOT contain multi-line broken initializer
        Assert.DoesNotContain("Object initializer", code, StringComparison.Ordinal);
        Assert.DoesNotContain("consider moving to constructor", code, StringComparison.Ordinal);

        // The declaration should be a simple field or assigned in constructor
        Assert.Contains("_objectID", code);

        // Verify it still works semantically - check no broken syntax in the output
        Assert.DoesNotContain("IntHolder s_typeCountRef = new", code, StringComparison.Ordinal);
    }

    [Fact]
    public void InterlockedIncrement_InstanceField_NoBrokenInitializer()
    {
        var result = Convert(@"
public class Sequencer
{
    private static int s_counter;
    private readonly int _id = System.Threading.Interlocked.Increment(ref s_counter);
    public Sequencer() { }
    public int GetID() { return _id; }
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // Field initializer must be a single valid Java expression, not multi-statement
        Assert.DoesNotContain("/* Object initializer", code, StringComparison.Ordinal);

        // Counter must be preserved functionally
        Assert.Contains("interlocked.Increment", code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MultipleFieldsWithInitializerStatements_AllLiftedCorrectly()
    {
        var result = Convert(@"
public class MultiField
{
    private static int s_typeCount;
    private readonly int _id = System.Threading.Interlocked.Increment(ref s_typeCount);
    private readonly int _hash = System.Threading.Interlocked.Increment(ref s_typeCount);
    public MultiField() { }
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // No broken comment patterns
        Assert.DoesNotContain("consider moving to constructor", code, StringComparison.Ordinal);

        // Both fields should exist
        Assert.Contains("_id", code);
        Assert.Contains("_hash", code);
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
