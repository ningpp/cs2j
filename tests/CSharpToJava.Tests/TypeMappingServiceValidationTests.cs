using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.TypeMapping;
using CSharpToJava.TypeMapping.JavaModel;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for Phase B: TypeMappingService validation via Java metadata diagnostics
/// and ConversionPipeline integration with JavaMetadataPath.
/// </summary>
public class TypeMappingServiceValidationTests
{
    private static readonly string JavaConfigDir = TestPaths.JavaConfigDir;

    // -----------------------------------------------------------------------
    // TypeMappingService.ValidateMappedJavaType
    // -----------------------------------------------------------------------

    [Fact]
    public void ValidateMappedJavaType_KnownType_NoDiagnostic()
    {
        var (service, diagnostics) = CreateService(withJavaLibrary: true);
        service.ValidateMappedJavaType("java.util.ArrayList");
        Assert.Empty(diagnostics.Messages);
    }

    [Fact]
    public void ValidateMappedJavaType_UnknownType_EmitsCS2J4001()
    {
        var (service, diagnostics) = CreateService(withJavaLibrary: true);
        service.ValidateMappedJavaType("com.example.NonExistentType");
        Assert.Single(diagnostics.Messages);
        Assert.Equal("CS2J4001", diagnostics.Messages[0].Code);
        Assert.Contains("com.example.NonExistentType", diagnostics.Messages[0].Message);
    }

    [Fact]
    public void ValidateMappedJavaType_PrimitiveType_NoDiagnostic()
    {
        var (service, diagnostics) = CreateService(withJavaLibrary: true);
        service.ValidateMappedJavaType("int");
        Assert.Empty(diagnostics.Messages);
    }

    [Fact]
    public void ValidateMappedJavaType_WithoutMetadata_NoDiagnostic()
    {
        var (service, diagnostics) = CreateService(withJavaLibrary: false);
        service.ValidateMappedJavaType("com.example.NonExistentType");
        Assert.Empty(diagnostics.Messages); // No metadata → skip validation
    }

    // -----------------------------------------------------------------------
    // TypeMappingService.ValidateMappedJavaMethod
    // -----------------------------------------------------------------------

    [Fact]
    public void ValidateMappedJavaMethod_KnownMethod_NoDiagnostic()
    {
        var (service, diagnostics) = CreateService(withJavaLibrary: true);
        service.ValidateMappedJavaMethod("java.util.ArrayList", "add");
        Assert.Empty(diagnostics.Messages);
    }

    [Fact]
    public void ValidateMappedJavaMethod_UnknownMethod_EmitsCS2J4002()
    {
        var (service, diagnostics) = CreateService(withJavaLibrary: true);
        service.ValidateMappedJavaMethod("java.util.ArrayList", "convertAll");
        Assert.Single(diagnostics.Messages);
        Assert.Equal("CS2J4002", diagnostics.Messages[0].Code);
        Assert.Contains("convertAll", diagnostics.Messages[0].Message);
    }

    [Fact]
    public void ValidateMappedJavaMethod_WithoutMetadata_NoDiagnostic()
    {
        var (service, diagnostics) = CreateService(withJavaLibrary: false);
        service.ValidateMappedJavaMethod("java.util.ArrayList", "convertAll");
        Assert.Empty(diagnostics.Messages); // No metadata → skip validation
    }

    // -----------------------------------------------------------------------
    // TypeMappingService.JavaLibrary property
    // -----------------------------------------------------------------------

    [Fact]
    public void JavaLibrary_WhenSet_IsAccessible()
    {
        var (service, _) = CreateService(withJavaLibrary: true);
        Assert.NotNull(service.JavaLibrary);
    }

    [Fact]
    public void JavaLibrary_WhenNotSet_IsNull()
    {
        var (service, _) = CreateService(withJavaLibrary: false);
        Assert.Null(service.JavaLibrary);
    }

    // -----------------------------------------------------------------------
    // ConversionPipeline integration: JavaMetadataPath option
    // -----------------------------------------------------------------------

    [Fact]
    public void ConversionPipeline_WithJavaMetadataPath_AutoDeducesMethod()
    {
        // Convert C# code that calls HashMap's ContainsKey — the method name mapping
        // should be auto-deduced from Java metadata (PascalCase → camelCase).
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Collections.Generic;

class Sample
{
    bool M()
    {
        var d = new Dictionary<string, int>();
        return d.ContainsKey(""hello"");
    }
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions
            {
                JavaMetadataPath = JavaConfigDir,
            },
        });

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("containsKey", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_WithoutJavaMetadataPath_StillWorks()
    {
        // Without metadata, the pipeline should still work — just no auto-deduction.
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"class Sample { }",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success);
    }

    // -----------------------------------------------------------------------
    // Helper methods
    // -----------------------------------------------------------------------

    private static (TypeMappingService service, DiagnosticCollector diagnostics) CreateService(bool withJavaLibrary)
    {
        var config = new TypeMappingConfig();
        var registry = new TypeMappingRegistry(config);

        if (withJavaLibrary)
        {
            registry.SetJavaLibraryIndex(new JavaLibraryIndex(JavaConfigDir));
        }

        var diagnostics = new DiagnosticCollector();
        var options = new ConversionOptions();
        var importedTypes = new HashSet<string>();
        var service = new TypeMappingService(
            options,
            registry,
            diagnostics,
            importedTypes,
            () => "",
            () => null,
            _ => false);

        return (service, diagnostics);
    }
}
