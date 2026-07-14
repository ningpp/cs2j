using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# exception handling (try/catch/finally/throw) and exception types to Java.
/// </summary>
public class ExceptionTests : ConversionTestBase
{
    [Fact]
    public void TryCatch_ConvertsToRuntimeException()
    {
        var result = Convert("class C { public void M() { try { var x = 1; } catch (System.Exception e) { var y = 1; } } }");
        AssertConversion(result, "try {", "var x = 1;", "} catch (RuntimeException e) {", "var y = 1;");
    }

    [Fact]
    public void TryCatchFinally_ConvertsToFinally()
    {
        var result = Convert("class C { public void M() { try { } catch (System.Exception e) { } finally { var z = 1; } } }");
        AssertConversion(result, "} catch (RuntimeException e) {", "} finally {", "var z = 1;");
    }

    [Fact]
    public void ThrowNew_ConvertsToRuntimeException()
    {
        var result = Convert("class C { public void M() { throw new System.Exception(\"x\"); } }");
        AssertConversion(result, "throw new RuntimeException(\"x\");");
    }

    [Fact]
    public void ThrowArgumentNull_ConvertsToCompatException()
    {
        var result = Convert("class C { public void M() { throw new System.ArgumentNullException(\"p\"); } }");
        AssertConversion(result,
            "import io.github.ningpp.compat.ArgumentNullException;",
            "throw new ArgumentNullException(\"p\");");
    }

    [Fact]
    public void ThrowRethrow_ConvertsToThrowExceptionVar()
    {
        var result = Convert("class C { public void M() { try { } catch (System.Exception e) { throw; } } }");
        AssertConversion(result, "} catch (RuntimeException e) {", "throw e;");
        AssertJavaDoesNotContain(result, "throw;", "C# rethrow 'throw;' must be rewritten");
    }

    [Fact]
    public void ThrowExpression_ConvertsToSupplierThrowing()
    {
        var result = Convert("class C { public int M(bool c) => c ? throw new System.Exception() : 1; }");
        AssertConversion(result, "((java.util.function.Supplier<Integer>) () -> { throw new RuntimeException(); }).get()");
    }

    [Fact]
    public void ThrowExpression_StringType_UsesSupplierString()
    {
        var result = Convert("class C { public string M(string s) => s ?? throw new System.InvalidOperationException(); }");
        AssertConversion(result, "((java.util.function.Supplier<String>) () -> { throw new IllegalStateException(); }).get()");
        AssertJavaDoesNotContain(result, "Supplier<Object>", "String context throw expression must use Supplier<String>, not Supplier<Object>");
    }

    [Fact]
    public void CatchTyped_ConvertsToSpecificRuntimeException()
    {
        var result = Convert("class C { public void M() { try { } catch (System.InvalidOperationException e) { } catch (System.Exception e) { } } }");
        AssertConversion(result, "} catch (IllegalStateException e) {", "} catch (RuntimeException e) {");
    }

    [Fact]
    public void CatchWithoutType_ConvertsToRuntimeExceptionVar()
    {
        var result = Convert("class C { public void M() { try { } catch { } } }");
        AssertConversion(result, "} catch (RuntimeException _ex) {");
    }

    [Fact]
    public void ExceptionNoCSharpResidue()
    {
        var result = Convert("class C { public void M() { try { throw new System.Exception(\"e\"); } catch (System.Exception e) { var m = e.Message; } } }");
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "getMessage()", "C# Exception.Message must map to Java getMessage()");
    }
}
