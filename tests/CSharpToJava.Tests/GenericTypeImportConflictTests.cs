using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class GenericTypeImportConflictTests
{
    [Fact]
    public async Task ProjectConversion_ShouldAddExplicitImportForConflictingGenericType()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "c2j-generic-import-conflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Set.cs"), """
                namespace Microsoft.Msagl.Core.DataStructures
                {
                    public class Set<T>
                    {
                    }
                }
                """);

            await File.WriteAllTextAsync(Path.Combine(tempDir, "C.cs"), """
                using Microsoft.Msagl.Core.DataStructures;

                namespace Microsoft.Msagl.Drawing
                {
                    public class C
                    {
                        public Set<int> Values = new Set<int>();
                    }
                }
                """);

            var pipeline = new ConversionPipeline();
            var options = new ConversionOptions();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(tempDir, options);

            var converted = results.FirstOrDefault(r => r.FileName == "C.java");
            Assert.NotNull(converted);
            Assert.Contains("import Microsoft.Msagl.Core.DataStructures.Set;", converted!.GeneratedCode);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
