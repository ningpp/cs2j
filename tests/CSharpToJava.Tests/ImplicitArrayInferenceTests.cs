using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ImplicitArrayInferenceTests
{

    private static ConversionOptions CreateProjectOptions()
    {
        return new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            PreferStreamApi = false,
        };
    }
}
