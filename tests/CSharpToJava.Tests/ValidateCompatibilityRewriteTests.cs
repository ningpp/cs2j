using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ValidateCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_Validate_RewritesExceptionAndDebuggerResidue()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            public final class Validate {
                private static boolean raiseInteractiveAssert(Exception ex) {
                    var exceptionToUse = ex.getInnerException() != null ? ex.getInnerException() : ex;
                    raiseInteractiveAssert(exceptionToUse.toString());
                    return true;
                }

                public static void raiseInteractiveAssert(String message) {
                    assert false : message;
                    Debugger.breakValue();
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("Validate.java", generated);

        Assert.Contains("private static boolean raiseInteractiveAssert(Throwable ex)", output);
        Assert.Contains("var exceptionToUse = ex.getCause() != null ? ex.getCause() : ex;", output);
        Assert.DoesNotContain("getInnerException()", output);
        Assert.DoesNotContain("Debugger.breakValue();", output);
    }

    [Fact]
    public void ApplyCompatibilityRewritesForTesting_Validate_RewritesAssertionCatchAndIgnoreCaseEquality()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            public final class Validate {
                public static <T> void areEqual(String expected, String actual, boolean ignoreCase, CultureInfo culture, String message) {
                    try {
                        Assertions.assertEquals(expected, actual, ignoreCase, culture, message);
                    } catch (UnitTestAssertException ex) {
                        throw ex;
                    }
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("Validate.java", generated);

        Assert.Contains("catch (AssertionError ex)", output);
        Assert.Contains("Assertions.assertTrue(ignoreCase ? java.util.Objects.equals(expected == null ? null : expected.toLowerCase(java.util.Locale.ROOT), actual == null ? null : actual.toLowerCase(java.util.Locale.ROOT)) : java.util.Objects.equals(expected, actual), message);", output);
        Assert.DoesNotContain("UnitTestAssertException", output);
        Assert.DoesNotContain("Assertions.assertEquals(expected, actual, ignoreCase, culture, message);", output);
    }
}