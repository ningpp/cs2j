using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;

namespace CSharpToJava.Tests;

[Collection(MsBuildCompileEvaluatorOverridesCollection.CollectionName)]
public sealed class ConditionalPassAdapterResolvedTfmTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs2j-conditional-adapter-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProjectTfm_IsResolvedPerFile_SoNetBranchSurvivesAndNegationIsStripped()
    {
        const string code = """
            #if NET
            class NetOnly {}
            #endif
            #if !NET
            class NotNet {}
            #endif
            """;
        var projectDirectory = Path.Combine(_root, "App");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(
            Path.Combine(projectDirectory, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>$(NetCoreAppCurrent);netstandard2.0</TargetFrameworks></PropertyGroup>
            </Project>
            """);
        var sourceFile = Path.Combine(projectDirectory, "Code.cs");
        File.WriteAllText(sourceFile, code);
        MsBuildCompileEvaluator.EvaluationOverride = _ => new MsBuildCompileEvaluation
        {
            Files = Array.Empty<string>(),
            TargetFramework = "net10.0",
        };

        try
        {
            var adapter = new ConditionalPassAdapter(
                verbose: false,
                filterUnsupportedProjects: true,
                compatNamespacePrefixes: null,
                configuration: "Release",
                tfm: null,
                removeRegions: false);

            var pure = await adapter.RewriteFilePureAsync(new PassFileContext
            {
                SourceCode = code,
                SourceFilePath = sourceFile,
                RelativePath = "Code.cs",
                DestinationFilePath = Path.Combine(_root, "out", "Code.cs"),
            });

            var rewrite = Assert.IsType<PassFileRewrite>(pure.Rewrite);
            Assert.Contains("class NetOnly", rewrite.OutputCode, StringComparison.Ordinal);
            Assert.DoesNotContain("class NotNet", rewrite.OutputCode, StringComparison.Ordinal);
        }
        finally
        {
            MsBuildCompileEvaluator.EvaluationOverride = null;
            ResolvedProjectTfm.ClearCache();
        }
    }

    [Fact]
    public async Task SharedFile_WithConflictingOwnerSymbols_IsPreservedWithWarning()
    {
        const string code = """
            #if LibA
            public
            #else
            internal
            #endif
            class Shared {}
            """;
        Directory.CreateDirectory(Path.Combine(_root, "Common"));
        var sourceFile = Path.Combine(_root, "Common", "Shared.cs");
        File.WriteAllText(sourceFile, code);
        foreach (var name in new[] { "LibA", "LibB" })
        {
            Directory.CreateDirectory(Path.Combine(_root, name));
            File.WriteAllText(
                Path.Combine(_root, name, name + ".csproj"),
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                    <DefineConstants>{name}</DefineConstants>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="../Common/Shared.cs" /></ItemGroup>
                </Project>
                """);
        }

        var adapter = new ConditionalPassAdapter(
            verbose: false,
            filterUnsupportedProjects: true,
            compatNamespacePrefixes: null,
            configuration: "Release",
            tfm: null,
            removeRegions: false,
            sourceRoot: _root);

        var pure = await adapter.RewriteFilePureAsync(new PassFileContext
        {
            SourceCode = code,
            SourceFilePath = sourceFile,
            RelativePath = "Common/Shared.cs",
            DestinationFilePath = Path.Combine(_root, "out", "Common", "Shared.cs"),
        });
        adapter.MergeFilePureResult(pure);

        Assert.Null(pure.Rewrite);
        var warning = Assert.Single(adapter.Diagnostics);
        Assert.StartsWith("[Warning]", warning, StringComparison.Ordinal);
        Assert.Contains("preserved conditional directives", warning, StringComparison.Ordinal);
        Assert.False(adapter.HasErrors);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
