using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class MethodBaseTypeMappingTests
{
    [Fact]
    public void MethodBase_Parameter_MappedToMemberInfo()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = """
using System.Reflection;

class Sample
{
    private static MethodBase FilterMethodBases(MethodBase[] methodBases, string methodName)
    {
        foreach (var method in methodBases)
        {
            if (method.Name.Equals(methodName))
            {
                return method;
            }
        }
        return null;
    }
}
""",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.DoesNotMatch(@"\bMethodBase\b", code);
        Assert.Contains("MemberInfo", code, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.MemberInfo;", code, StringComparison.Ordinal);
    }
}
