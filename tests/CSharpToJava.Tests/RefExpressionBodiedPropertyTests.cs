using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests conversion of C# expression-bodied ref-returning properties
/// (e.g. <c>public ref T ValueRef =&gt; ref item;</c>).
///
/// C# <c>ref</c> returns allow the caller to mutate the underlying field
/// through the returned reference. Java has no equivalent construct — it
/// cannot express "return a reference to a field".
///
/// <para><b>Fix:</b> <c>PropertyTransformer</c> now strips <c>RefTypeSyntax</c>
/// before type resolution (mirroring <c>MethodTransformer.GetReturnType</c>).
/// Additionally, both <c>ConversionContext.MapTypeFromSyntax</c> and
/// <c>TypeMappingService.MapTypeFromSyntax</c> handle <c>RefTypeSyntax</c>
/// defensively, so any path that passes a <c>ref T</c> syntax node will
/// correctly strip the <c>ref</c> modifier and map only the inner type <c>T</c>.</para>
///
/// <para>The <c>ref item</c> expression body is correctly unwrapped to
/// <c>item</c> (via <c>ExpressionTransformerRegistry</c>'s
/// <c>RefExpressionSyntax</c> handler), so the getter body
/// <c>return item;</c> is syntactically valid.</para>
/// </summary>
public class RefExpressionBodiedPropertyTests
{
    /// <summary>
    /// Verifies that the ref-returning expression-bodied property
    /// <c>public ref T ValueRef =&gt; ref item;</c> does NOT leak the C# <c>ref</c>
    /// keyword into the Java getter signature.
    ///
    /// <para>The C# source is a generic linked-list node with an
    /// internal field <c>item</c> of type <c>T</c>, a constructor,
    /// a regular <c>Value</c> property (get/set), and a
    /// <c>ValueRef</c> property returning <c>ref T</c> via expression body.</para>
    ///
    /// <para><b>Correct output:</b> <c>public T getValueRef() { return item; }</c>
    /// The <c>ref</c> modifier must be stripped — Java has no <c>ref</c> keyword.</para>
    /// </summary>
    [Fact]
    public void RefReturningProperty_RefKeywordStrippedFromJavaReturnType()
    {
        var result = Convert(@"
public sealed class DemoLinkedListNode<T>
{
    internal T item;

    public DemoLinkedListNode(T value)
    {
        item = value;
    }

    public T Value
    {
        get { return item; }
        set { item = value; }
    }

    /// <summary>Gets a reference to the value held by the node.</summary>
    public ref T ValueRef => ref item;
}");

        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        var code = result.GeneratedCode;

        // The getter for ValueRef should exist
        Assert.Contains("getValueRef", code);

        // The return type must NOT contain "ref" — Java has no ref keyword.
        // The correct output is: public T getValueRef() { return item; }
        Assert.DoesNotContain("ref T getValueRef", code);

        // The return type should be just "T"
        Assert.Contains(" T getValueRef", code);
    }

    /// <summary>
    /// Verifies that the generated getter body correctly returns the field.
    /// The <c>ref item</c> expression is unwrapped to <c>item</c> by
    /// <c>ExpressionTransformerRegistry</c>'s <c>RefExpressionSyntax</c>
    /// handler, producing <c>return item;</c>.
    /// </summary>
    [Fact]
    public void RefReturningProperty_GetterBodyReturnsItemField()
    {
        var result = Convert(@"
public sealed class DemoLinkedListNode<T>
{
    internal T item;

    public DemoLinkedListNode(T value)
    {
        item = value;
    }

    public T Value
    {
        get { return item; }
        set { item = value; }
    }

    public ref T ValueRef => ref item;
}");

        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        var code = result.GeneratedCode;

        // The getter body should return the item field
        // (ref item -> item via RefExpressionSyntax handler)
        Assert.Contains("return item", code);
    }

    /// <summary>
    /// Verifies that the full class converts without errors and all
    /// expected members are present in the generated Java.
    /// </summary>
    [Fact]
    public void RefReturningProperty_FullClass_AllMembersPresent()
    {
        var result = Convert(@"
public sealed class DemoLinkedListNode<T>
{
    internal T item;

    public DemoLinkedListNode(T value)
    {
        item = value;
    }

    public T Value
    {
        get { return item; }
        set { item = value; }
    }

    /// <summary>Gets a reference to the value held by the node.</summary>
    public ref T ValueRef => ref item;
}");

        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        var code = result.GeneratedCode;

        // The item field should be present (Java: private T item)
        Assert.Contains("item", code);

        // The constructor should be present
        Assert.Contains("DemoLinkedListNode", code);

        // The regular Value property's getter and setter should be present
        Assert.Contains("getValue", code);
        Assert.Contains("setValue", code);

        // The ValueRef property's getter should be present
        Assert.Contains("getValueRef", code);
    }

    /// <summary>
    /// Verifies that the ref-returning property's XML doc comment
    /// is preserved on the generated getter.
    /// </summary>
    [Fact]
    public void RefReturningProperty_DocCommentPreserved()
    {
        var result = Convert(@"
public sealed class DemoLinkedListNode<T>
{
    internal T item;

    public DemoLinkedListNode(T value)
    {
        item = value;
    }

    public T Value
    {
        get { return item; }
        set { item = value; }
    }

    /// <summary>Gets a reference to the value held by the node.</summary>
    public ref T ValueRef => ref item;
}");

        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        var code = result.GeneratedCode;

        // The doc comment should be preserved on the getter
        Assert.Contains("reference to the value", code);
    }

    /// <summary>
    /// Verifies that assignment through the ref-returning property
    /// (e.g. <c>node.ValueRef = newVal;</c>) does not produce invalid Java
    /// like <c>getValueRef() = newVal</c>.
    ///
    /// <para>In C#, <c>node.ValueRef = x</c> writes through the reference,
    /// directly assigning <c>x</c> to <c>item</c>. In Java, since
    /// <c>getValueRef()</c> returns a value (not a reference), the
    /// assignment cannot be a bare <c>getValueRef() = x</c> — that is
    /// invalid Java syntax. It must be rewritten to a setter call.</para>
    /// </summary>
    [Fact]
    public void RefReturningProperty_AssignmentThroughRef_NoInvalidJavaSyntax()
    {
        var result = Convert(@"
public sealed class DemoLinkedListNode<T>
{
    internal T item;

    public DemoLinkedListNode(T value)
    {
        item = value;
    }

    public T Value
    {
        get { return item; }
        set { item = value; }
    }

    public ref T ValueRef => ref item;
}

class Usage
{
    public static void Modify(DemoLinkedListNode<int> node, int newVal)
    {
        node.ValueRef = newVal;
    }
}");

        var code = result.GeneratedCode;

        // The assignment through the ref-returning property must NOT produce
        // invalid Java like "getValueRef() = newVal"
        Assert.DoesNotContain("getValueRef() =", code);
    }

    /// <summary>
    /// Verifies that a compound assignment through the ref-returning property
    /// (e.g. <c>node.ValueRef += 5;</c>) does not produce invalid Java
    /// like <c>getValueRef() += 5</c>.
    ///
    /// <para>C# <c>node.ValueRef += 5</c> reads and writes through the
    /// reference: <c>node.item = node.item + 5</c>. In Java,
    /// <c>getValueRef() += 5</c> is invalid syntax — it must be rewritten
    /// to a setter call like <c>setValue(getValue() + 5)</c>.</para>
    /// </summary>
    [Fact]
    public void RefReturningProperty_CompoundAssignmentThroughRef_NoInvalidJavaSyntax()
    {
        var result = Convert(@"
public sealed class DemoLinkedListNode<T>
{
    internal T item;

    public DemoLinkedListNode(T value)
    {
        item = value;
    }

    public T Value
    {
        get { return item; }
        set { item = value; }
    }

    public ref T ValueRef => ref item;
}

class Usage
{
    public static void Increment(DemoLinkedListNode<int> node)
    {
        node.ValueRef += 5;
    }
}");

        var code = result.GeneratedCode;

        // A compound assignment like += 5 must not produce
        // invalid Java like "getValueRef() += 5"
        Assert.DoesNotContain("getValueRef() +=", code);
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
