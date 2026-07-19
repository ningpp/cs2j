using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for explicit interface property implementations that conflict with a class's
/// own property of the same name. The explicit interface accessor must keep the standard
/// Java name to satisfy the interface, while the class property accessor is renamed and
/// all internal references are updated.
/// </summary>
public class ExplicitInterfacePropertyConflictTests
{
    /// <summary>
    /// SchemaEntity-style scenario: explicit interface property IEntity.Name returns string
    /// and accesses the class's own Name property (returning a wrapper type). The generated
    /// Java must not produce a recursive getName() call.
    /// </summary>
    [Fact]
    public void ExplicitInterfaceProperty_ConflictsWithClassProperty_ClassPropertyRenamed()
    {
        var result = Convert(@"
public interface IEntity
{
    string Name { get; }
}

public class EntityName
{
    public string Value { get; }
}

public class Entity : IEntity
{
    private EntityName _name;

    string IEntity.Name
    {
        get { return this.Name.Value; }
    }

    internal EntityName Name
    {
        get { return _name; }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode;

        // Interface implementation must exist with standard name
        Assert.Contains("public String getName()", code, StringComparison.Ordinal);
        // Class property accessor must be renamed to avoid conflict
        Assert.Contains("public EntityName getName$Class()", code, StringComparison.Ordinal);
        // Explicit interface body must call the renamed class property accessor,
        // not recurse into itself.
        Assert.Contains("this.getName$Class().getValue()", code, StringComparison.Ordinal);
        // The recursive pattern must not appear
        Assert.DoesNotContain("this.getName().getValue()", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reverse declaration order: class property declared before the explicit interface
    /// property. The fix must still rename the class property accessor and update references.
    /// </summary>
    [Fact]
    public void ClassPropertyBeforeExplicitInterfaceProperty_ClassPropertyRenamed()
    {
        var result = Convert(@"
public interface IEntity
{
    string Name { get; }
}

public class EntityName
{
    public string Value { get; }
}

public class Entity : IEntity
{
    private EntityName _name;

    internal EntityName Name
    {
        get { return _name; }
    }

    string IEntity.Name
    {
        get { return this.Name.Value; }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode;

        Assert.Contains("public String getName()", code, StringComparison.Ordinal);
        Assert.Contains("public EntityName getName$Class()", code, StringComparison.Ordinal);
        Assert.Contains("this.getName$Class().getValue()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("this.getName().getValue()", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Enumerator-style scenario: a class implements IEnumerator and also exposes a typed
    /// Current property. External code that references the typed Current must call the
    /// renamed getter, not the object-returning explicit interface getter.
    /// </summary>
    [Fact]
    public void ExplicitInterfaceProperty_EnumeratorCurrent_ClassPropertyRenamed()
    {
        var result = Convert(@"
using System;
using System.Collections;

public sealed class SchemaEnumerator : IEnumerator
{
    private IDictionaryEnumerator _enumerator;

    public SchemaEnumerator(Hashtable table)
    {
        _enumerator = table.GetEnumerator();
    }

    object IEnumerator.Current
    {
        get { return this.Current; }
    }

    public Schema Current
    {
        get { return new Schema(); }
    }

    public bool MoveNext() => _enumerator.MoveNext();
    public void Reset() => _enumerator.Reset();
}

public class Schema { }

public class Container
{
    public void CopyTo(Schema[] array, int index, SchemaEnumerator e)
    {
        for (; e.MoveNext(); )
        {
            Schema schema = e.Current;
            if (schema != null)
            {
                array[index++] = e.Current;
            }
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode;
        System.IO.File.WriteAllText(@"d:\code\cs2j\enum-test-output.java", code);

        // Explicit interface accessor keeps standard name
        Assert.Contains("public Object getCurrent()", code, StringComparison.Ordinal);
        // Class property accessor is renamed
        Assert.Contains("public Schema getCurrent$Class()", code, StringComparison.Ordinal);
        // External access on the enumerator class must use the renamed getter
        Assert.Contains("Schema schema = e.getCurrent$Class()", code, StringComparison.Ordinal);
        Assert.Contains("array[index++] = e.getCurrent$Class()", code, StringComparison.Ordinal);
        // Must not call the object-returning getter for typed access
        Assert.DoesNotContain("e.getCurrent()", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
