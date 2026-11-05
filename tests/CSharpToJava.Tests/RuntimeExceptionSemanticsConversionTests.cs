using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for exception-semantics conversion (iter-1 NPE/IllegalArgument/
/// IllegalState/Cancellation cluster):
/// <list type="bullet">
///   <item>MSTest <c>[ExpectedException]</c> → JUnit 5 <c>assertThrows</c> (the attribute
///         used to be dropped, so an expected throw surfaced as a test ERROR).</item>
///   <item>MSTest <c>[ClassInitialize]</c> → JUnit 5 <c>@BeforeAll</c> (static setup never
///         ran, leaving reference-data fields null).</item>
///   <item>Structs that declare their own <c>Clone()</c> keep that conversion as <c>clone()</c>
///         but expose <c>__cs2jValueCopy()</c> for verbatim C# value copies.</item>
/// </list>
/// </summary>
public class RuntimeExceptionSemanticsConversionTests
{
    private const string TestAttributes = """
        using Microsoft.VisualStudio.TestTools.UnitTesting;

        namespace Microsoft.VisualStudio.TestTools.UnitTesting
        {
            public class TestClassAttribute : System.Attribute { }
            public class TestMethodAttribute : System.Attribute { }
            public class TestContext { }
            public class ExpectedExceptionAttribute : System.Attribute
            {
                public ExpectedExceptionAttribute(System.Type exceptionType) { }
            }
            public class ClassInitializeAttribute : System.Attribute { }
            public class ClassCleanupAttribute : System.Attribute { }
        }
        """;

    private static string WithAttributes(string body) => TestAttributes + "\n" + body;

    // ==================== [ExpectedException] → assertThrows ====================

    [Fact]
    public void ExpectedException_InvalidOperation_MapsToIllegalStateAssertThrows()
    {
        var code = Convert(WithAttributes("""
            [TestClass]
            public class SampleTests
            {
                [TestMethod]
                [ExpectedException(typeof(System.InvalidOperationException))]
                public void TrimFails()
                {
                    throw new System.InvalidOperationException();
                }
            }
            """));

        Assert.Contains("Assertions.assertThrows(IllegalStateException.class", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectedException_Argument_MapsToIllegalArgumentAssertThrows()
    {
        var code = Convert(WithAttributes("""
            [TestClass]
            public class SampleTests
            {
                [TestMethod]
                [ExpectedException(typeof(System.ArgumentException))]
                public void SettingsReject()
                {
                    throw new System.ArgumentException("bad");
                }
            }
            """));

        Assert.Contains("assertThrows(IllegalArgumentException.class", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectedException_OperationCanceled_MapsToCancellationException()
    {
        var code = Convert(WithAttributes("""
            [TestClass]
            public class SampleTests
            {
                [TestMethod]
                [ExpectedException(typeof(System.OperationCanceledException))]
                public void Canceled()
                {
                    throw new System.OperationCanceledException();
                }
            }
            """));

        Assert.Contains(
            "assertThrows(java.util.concurrent.CancellationException.class",
            code,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectedException_NullReference_MapsToNullPointerAssertThrows()
    {
        var code = Convert(WithAttributes("""
            [TestClass]
            public class SampleTests
            {
                [TestMethod]
                [ExpectedException(typeof(System.NullReferenceException))]
                public void NullBoundary()
                {
                    object value = null;
                    value.ToString();
                }
            }
            """));

        Assert.Contains("assertThrows(NullPointerException.class", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectedException_SystemException_MapsToRuntimeException()
    {
        var code = Convert(WithAttributes("""
            [TestClass]
            public class SampleTests
            {
                [TestMethod]
                [ExpectedException(typeof(System.Exception))]
                public void AnyFailure()
                {
                    throw new System.Exception("boom");
                }
            }
            """));

        Assert.Contains("assertThrows(RuntimeException.class", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectedException_PlainTestMethod_StillEmitsTestAnnotation()
    {
        var code = Convert(WithAttributes("""
            [TestClass]
            public class SampleTests
            {
                [TestMethod]
                [ExpectedException(typeof(System.InvalidOperationException))]
                public void TrimFails()
                {
                    throw new System.InvalidOperationException();
                }
            }
            """));

        Assert.Contains("@Test", code, StringComparison.Ordinal);
        Assert.Contains("public void trimFails()", code, StringComparison.Ordinal);
    }

    // ==================== [ClassInitialize] → @BeforeAll ====================

    [Fact]
    public void ClassInitialize_BecomesBeforeAll()
    {
        var code = Convert(WithAttributes("""
            [TestClass]
            public class ReferenceDataTests
            {
                private static object reference;

                [ClassInitialize]
                public static void ClassInitialize(TestContext context)
                {
                    reference = new object();
                }

                [TestMethod]
                public void UsesReference()
                {
                    Microsoft.VisualStudio.TestTools.UnitTesting.Assert.IsNotNull(reference);
                }
            }
            """));

        Assert.Contains("@BeforeAll", code, StringComparison.Ordinal);
        Assert.Contains("public static void classInitialize(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("public void classInitialize(", code, StringComparison.Ordinal);
        Assert.Contains("ExtendWith(MSTestExtension.class)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassCleanup_BecomesAfterAll()
    {
        var code = Convert(WithAttributes("""
            [TestClass]
            public class ReferenceDataTests
            {
                [ClassCleanup]
                public static void ClassCleanup()
                {
                }

                [TestMethod]
                public void Plain()
                {
                }
            }
            """));

        Assert.Contains("@AfterAll", code, StringComparison.Ordinal);
    }

    // ==================== struct own Clone() vs value copy ====================

    [Fact]
    public void StructDeclaringClone_KeepsCloneAndEmitsValueCopyMethod()
    {
        var code = Convert("""
            public struct Blob
            {
                public int A;
                public Blob Clone()
                {
                    return new Blob();
                }
            }

            public class Holder
            {
                public Blob Copy(Blob value)
                {
                    return value;
                }
            }
            """);

        // The user conversion keeps its Java name...
        Assert.Contains("public Blob clone()", code, StringComparison.Ordinal);
        // ...and a verbatim field copy is emitted for implicit C# value copies.
        Assert.Contains("public Blob __cs2jValueCopy()", code, StringComparison.Ordinal);
        Assert.Contains("super.clone()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void StructDeclaringClone_ImplicitCopyUsesValueCopyNotClone()
    {
        var code = Convert("""
            public struct Blob
            {
                public int A;
                public Blob Clone()
                {
                    return new Blob();
                }
            }

            public class Holder
            {
                public Blob Copy(Blob value)
                {
                    return value;
                }
            }
            """);

        Assert.Contains("return value.__cs2jValueCopy();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("return value.clone();", code, StringComparison.Ordinal);
    }

    [Fact]
    public void StructWithoutClone_StillUsesGeneratedCloneForValueCopy()
    {
        var code = Convert("""
            public struct Pair
            {
                public int A;
                public int B;
            }

            public class Holder
            {
                public Pair Copy(Pair value)
                {
                    return value;
                }
            }
            """);

        Assert.Contains("public Pair clone()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("__cs2jValueCopy", code, StringComparison.Ordinal);
        Assert.Contains("return value.clone();", code, StringComparison.Ordinal);
    }

    private static string Convert(string sourceCode)
    {
        var result = new ConversionPipeline().Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        return result.GeneratedCode;
    }
}
