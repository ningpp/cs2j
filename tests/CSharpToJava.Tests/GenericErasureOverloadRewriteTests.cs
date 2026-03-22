using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class GenericErasureOverloadRewriteTests
{
    [Fact]
    public async Task ConvertProjectWithPartialMergeAsync_ResultVerifierBase_RewritesErasureConflictingDumpRectanglesOverloads()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "c2j-erasure-overload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "ResultVerifierBase.cs"), """
                using System.Collections.Generic;

                public class VariableDef
                {
                    public double Left { get; set; }
                    public double Top { get; set; }
                    public double Right { get; set; }
                    public double Bottom { get; set; }
                }

                public class ClusterDef
                {
                    public double Left { get; set; }
                    public double Top { get; set; }
                    public double Right { get; set; }
                    public double Bottom { get; set; }
                }

                public class ResultVerifierBase
                {
                    public bool DumpRectCoordinates => true;

                    public void WriteLine(string line) { }
                    public void WriteLine(string format, params object[] args) { }
                    public void WriteLine() { }

                    private void DumpRectangles(IEnumerable<VariableDef> iterVariableDefs)
                    {
                        foreach (VariableDef varDef in iterVariableDefs)
                        {
                            WriteLine("{0}", varDef.Left);
                        }
                    }

                    public void DumpRectangles(IEnumerable<ClusterDef> iterClusterDefs)
                    {
                        foreach (ClusterDef clusDef in iterClusterDefs)
                        {
                            WriteLine("{0}", clusDef.Left);
                        }
                    }

                    public void PostCheckResults(IEnumerable<VariableDef> iterVariableDefs)
                    {
                        DumpRectangles(iterVariableDefs);
                    }
                }
                """);

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(tempRoot, new ConversionOptions());
            var output = results.Single(r => r.FileName == "ResultVerifierBase.java").GeneratedCode.Replace("\r\n", "\n");

            Assert.Contains("public void dumpRectangles(Iterable<VariableDef> iterVariableDefs)", output);
            Assert.Contains("public void dumpClusterRectangles(Iterable<ClusterDef> iterClusterDefs)", output);
            Assert.Contains("dumpRectangles(iterVariableDefs);", output);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}