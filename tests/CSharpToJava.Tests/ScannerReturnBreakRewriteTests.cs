using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Context;
using System.Text.RegularExpressions;

namespace CSharpToJava.Tests;

public class ScannerReturnBreakRewriteTests
{
    [Fact]
    public async Task ConvertProjectWithPartialMergeAsync_ScannerReturnCharLiteral_DropsUnreachableBreak()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "c2j-scanner-return-break-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "Scanner.cs"), """
                namespace Dot2Graph
                {
                    public class Scanner
                    {
                        public int Next(int state)
                        {
                            switch (state)
                            {
                                case 12:
                                    return ';';
                                default:
                                    return 0;
                            }
                        }
                    }
                }
                """);

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(tempRoot, new ConversionOptions());
            var scanner = results.Single(r => r.FileName == "Scanner.java");
            var output = scanner.GeneratedCode.Replace("\r\n", "\n");

            Assert.Contains("case 12:", output);
            Assert.DoesNotMatch(new Regex(@"return [^\n]+;\n\s*break;"), output);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}