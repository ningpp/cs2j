using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class IntPtrMappingProbe
{
    [Fact]
    public async Task Probe_RefOutParamLocalFn()
    {
        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "Probe.cs",
                Content = @"
public class C
{
    public static bool M(out int parseData)
    {
        parseData = 1;
        bool r = ParseOffset(ref parseData);
        if (r) { parseData = 2; }
        return r;

        static bool ParseOffset(ref int parseData)
        {
            return parseData > 0;
        }
    }
}"
            }
        });
        var sb = new System.Text.StringBuilder();
        foreach (var r in results) { sb.AppendLine(r.GeneratedCode); }
        File.WriteAllText(@"C:\Users\15097\AppData\Local\Temp\opencode\refprobe.txt", sb.ToString());
    }
}
