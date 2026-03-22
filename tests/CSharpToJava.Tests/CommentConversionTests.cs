using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class CommentConversionTests
{
    private static string ConvertCode(string code, bool generateJavaDoc = true)
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = code,
            Options = new ConversionOptions
            {
                TargetJavaVersion = JavaVersion.Java17,
                GenerateJavaDoc = generateJavaDoc
            }
        });

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        return result.GeneratedCode;
    }

    [Fact]
    public void XmlDocumentation_OnClassAndMethod_GeneratesJavadoc()
    {
        const string code = """
            namespace Test
            {
                /// <summary>
                /// Service summary.
                /// </summary>
                class Sample
                {
                    /// <summary>
                    /// Adds two values.
                    /// </summary>
                    /// <param name="left">Left value.</param>
                    /// <param name="right">Right value.</param>
                    /// <returns>The sum.</returns>
                    public int Add(int left, int right)
                    {
                        return left + right;
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        Assert.Contains("/**", java);
        Assert.Contains("* Service summary.", java);
        Assert.Contains("* Adds two values.", java);
        Assert.Contains("* @param left Left value.", java);
        Assert.Contains("* @param right Right value.", java);
        Assert.Contains("* @return The sum.", java);
    }

    [Fact]
    public void NoJavaDoc_DisablesXmlDocs_ButPreservesRegularComments()
    {
        const string code = """
            namespace Test
            {
                // keep me
                /// <summary>
                /// hidden doc
                /// </summary>
                class Sample
                {
                }
            }
            """;

        var java = ConvertCode(code, generateJavaDoc: false);

        Assert.Contains("// keep me", java);
        Assert.DoesNotContain("hidden doc", java);
        Assert.DoesNotContain("/**", java);
    }

    [Fact]
    public void MethodBody_PreservesLeadingAndTrailingComments()
    {
        const string code = """
            namespace Test
            {
                class Sample
                {
                    void Run()
                    {
                        // before assignment
                        var value = 1; // trailing assignment
                        /* after assignment */
                        value++;
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        Assert.Contains("// before assignment", java);
        Assert.Contains("// trailing assignment", java);
        Assert.Contains("/* after assignment */", java);
    }
}