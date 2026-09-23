using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for Class token placement when calling generic methods
/// whose Java signature has a TRAILING Class&lt;?&gt; parameter (added by
/// AddRuntimeClassParametersForTypeParameterArrays).
///
/// In single-file conversion mode, generic methods that use typeof(T) get
/// a LEADING Class&lt;T&gt; _cs2j_T parameter (via AddPendingRuntimeClassParameters).
/// Call sites must PREPEND the Class literal to match.
///
/// In project conversion mode, the same methods may get a TRAILING Class&lt;?&gt;
/// parameter (via AddRuntimeClassParametersForTypeParameterArrays, because
/// GetRequiredTypeParameters detects the requirement via forwarded calls).
/// The MethodHasTrailingClassParams check must correctly identify trailing
/// usage even when GetClassTypeTokensForCall has registered leading params
/// (a false positive from the call-site scan).
/// </summary>
public class DeserializeAsyncClassTokenPlacementTests
{
    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
