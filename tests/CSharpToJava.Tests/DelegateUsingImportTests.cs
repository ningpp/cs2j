using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class DelegateUsingImportTests
{
    [Fact]
    public async Task ProjectConversion_Delegate_ShouldKeepUsingImports()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "c2j-delegate-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Curve.cs"), """
                namespace A
                {
                    public class Curve {}
                }
                """);

            await File.WriteAllTextAsync(Path.Combine(tempDir, "D.cs"), """
                using A;

                namespace B
                {
                    public delegate Curve MakeCurve();
                }
                """);

            var pipeline = new ConversionPipeline();
            var options = new ConversionOptions();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(tempDir, options);

            var delegateFile = results.FirstOrDefault(r => r.FileName == "MakeCurve.java");
            Assert.NotNull(delegateFile);
            Assert.Contains("import A.*;", delegateFile!.GeneratedCode);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ProjectConversion_NestedDelegate_ShouldKeepUsingImports()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "c2j-nested-delegate-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Curve.cs"), """
                namespace A
                {
                    public class Curve {}
                }
                """);

            await File.WriteAllTextAsync(Path.Combine(tempDir, "Host.cs"), """
                using A;

                namespace B
                {
                    public delegate Curve BuildCurve();

                    public class Host
                    {
                        public BuildCurve Builder;
                    }
                }
                """);

            var pipeline = new ConversionPipeline();
            var options = new ConversionOptions();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(tempDir, options);

            var delegateFile = results.FirstOrDefault(r => r.FileName == "BuildCurve.java");
            Assert.NotNull(delegateFile);
            Assert.Contains("import A.*;", delegateFile!.GeneratedCode);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
