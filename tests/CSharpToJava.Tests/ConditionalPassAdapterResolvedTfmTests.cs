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
    public async Task SharedFile_WithConflictingOwnerSymbols_IsStrippedWithMergedSymbols()
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

        Assert.NotNull(pure.Rewrite);
        Assert.Contains("public", pure.Rewrite.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("internal", pure.Rewrite.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("#if", pure.Rewrite.OutputCode, StringComparison.Ordinal);
        var warning = Assert.Single(adapter.Diagnostics);
        Assert.StartsWith("[Warning]", warning, StringComparison.Ordinal);
        Assert.Contains("merged preprocessor symbols", warning, StringComparison.Ordinal);
        Assert.False(adapter.HasErrors);
    }

    [Fact]
    public async Task SharedFile_VisibilitySplit_MaterializesPrivateCopyForDissentingOwner()
    {
        const string code = """
            #if LIB_PUBLIC
            public
            #else
            internal
            #endif
            class Shared {}
            """;
        var source = Path.Combine(_root, "src");
        var dest = Path.Combine(_root, "out");
        var sharedRel = Path.Combine("Common", "Shared.cs");
        var sharedSource = Path.Combine(source, sharedRel);
        Directory.CreateDirectory(Path.GetDirectoryName(sharedSource)!);
        File.WriteAllText(sharedSource, code);

        // Lib defines LIB_PUBLIC (=> public); Gen does not (=> internal).
        foreach (var (name, define) in new[] { ("Lib", "LIB_PUBLIC"), ("Gen", "") })
        {
            var dir = Path.Combine(source, name);
            Directory.CreateDirectory(dir);
            var defineXml = string.IsNullOrEmpty(define) ? "" : $"<DefineConstants>{define}</DefineConstants>";
            File.WriteAllText(Path.Combine(dir, name + ".csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                    {defineXml}
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
            sourceRoot: source);

        // Simulate the main pass writing the merged (union => public) shared form.
        var sharedDest = Path.Combine(dest, sharedRel);
        var pure = await adapter.RewriteFilePureAsync(new PassFileContext
        {
            SourceCode = code,
            SourceFilePath = sharedSource,
            RelativePath = sharedRel,
            DestinationFilePath = sharedDest,
        });
        Directory.CreateDirectory(Path.GetDirectoryName(sharedDest)!);
        await File.WriteAllTextAsync(sharedDest, pure.Rewrite!.OutputCode);
        Assert.Contains("public", pure.Rewrite.OutputCode, StringComparison.Ordinal);

        foreach (var name in new[] { "Lib", "Gen" })
        {
            var destDir = Path.Combine(dest, name);
            Directory.CreateDirectory(destDir);
            File.Copy(Path.Combine(source, name, name + ".csproj"), Path.Combine(destDir, name + ".csproj"));
        }

        await adapter.AfterFilesAsync(new PassTreeContext
        {
            Layout = new ProjectPreprocessSourceLayout(source, source, Array.Empty<string>()),
            SourceRoot = source,
            IntermediateRoot = dest,
            DestinationRoot = dest,
            ProjectGraph = null,
            Exclusion = new SupportedProjectFiltering.ExclusionInfo(),
            SourceFiles = new[] { sharedSource },
            CsFiles = new[] { sharedSource },
            ExternalSharedCompileFiles = Array.Empty<string>(),
            InPlace = false,
            Snapshot = new InPlaceSourceSnapshot(false),
        });

        // Gen dissents (internal): private copy + csproj redirect, no directives.
        var genCopy = Path.Combine(dest, "Gen", ".cs2j-conditional", sharedRel);
        Assert.True(File.Exists(genCopy));
        var genCopyCode = await File.ReadAllTextAsync(genCopy);
        Assert.Contains("internal", genCopyCode, StringComparison.Ordinal);
        Assert.DoesNotContain("public", genCopyCode, StringComparison.Ordinal);
        Assert.DoesNotContain("#if", genCopyCode, StringComparison.Ordinal);
        var genCsproj = await File.ReadAllTextAsync(Path.Combine(dest, "Gen", "Gen.csproj"));
        Assert.Contains("Remove=\"../Common/Shared.cs\"", genCsproj, StringComparison.Ordinal);
        Assert.Contains(".cs2j-conditional/Common/Shared.cs", genCsproj, StringComparison.Ordinal);

        // Lib agrees with the union (public): no private copy, csproj untouched.
        Assert.False(File.Exists(Path.Combine(dest, "Lib", ".cs2j-conditional", sharedRel)));
        var libCsproj = await File.ReadAllTextAsync(Path.Combine(dest, "Lib", "Lib.csproj"));
        Assert.DoesNotContain(".cs2j-conditional", libCsproj, StringComparison.Ordinal);

        // The shared form stays public for the agreeing owner.
        Assert.Contains("public", await File.ReadAllTextAsync(sharedDest), StringComparison.Ordinal);
    }

    /// <summary>
    /// The default chain must resolve the Release configuration: the preprocessed
    /// tree is verified with `dotnet build/test -c Release`, so `#if DEBUG`'s else
    /// branch has to survive (Regex.CompileToAssembly depends on it).
    /// </summary>
    [Fact]
    public async Task DefaultChain_ResolvesRelease_SoReleaseBranchSurvivesAndDebugBranchIsStripped()
    {
        const string code = """
            class Always {}
            #if DEBUG
            class DebugOnly {}
            #else
            class ReleaseOnly {}
            #endif
            """;
        var treeRoot = Path.Combine(_root, "default-chain-release");
        var projectDirectory = Path.Combine(treeRoot, "App");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(
            Path.Combine(projectDirectory, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        var sourceFile = Path.Combine(projectDirectory, "Code.cs");
        File.WriteAllText(sourceFile, code);

        var adapter = PassChainFactory.CreateAdapter(
            PassIds.Conditional, CreateDefaultChainConfig(treeRoot));

        var pure = await adapter.RewriteFilePureAsync(new PassFileContext
        {
            SourceCode = code,
            SourceFilePath = sourceFile,
            RelativePath = "Code.cs",
            DestinationFilePath = Path.Combine(_root, "out", "Code.cs"),
        });

        var rewrite = Assert.IsType<PassFileRewrite>(pure.Rewrite);
        Assert.Contains("class ReleaseOnly", rewrite.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("class DebugOnly", rewrite.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("#if", rewrite.OutputCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Release resolution must not override a project's explicit DefineConstants:
    /// the runtime Generator project declares DEBUG itself and keeps that branch.
    /// </summary>
    [Fact]
    public async Task DefaultChain_KeepsDebugBranch_WhenProjectDeclaresDebugExplicitly()
    {
        const string code = """
            class Always {}
            #if DEBUG
            class DebugOnly {}
            #else
            class ReleaseOnly {}
            #endif
            """;
        var treeRoot = Path.Combine(_root, "default-chain-explicit-debug");
        var projectDirectory = Path.Combine(treeRoot, "Generator");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(
            Path.Combine(projectDirectory, "Generator.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <DefineConstants>DEBUG</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
        var sourceFile = Path.Combine(projectDirectory, "Code.cs");
        File.WriteAllText(sourceFile, code);

        var adapter = PassChainFactory.CreateAdapter(
            PassIds.Conditional, CreateDefaultChainConfig(treeRoot));

        var pure = await adapter.RewriteFilePureAsync(new PassFileContext
        {
            SourceCode = code,
            SourceFilePath = sourceFile,
            RelativePath = "Code.cs",
            DestinationFilePath = Path.Combine(_root, "out", "Code.cs"),
        });

        var rewrite = Assert.IsType<PassFileRewrite>(pure.Rewrite);
        Assert.Contains("class DebugOnly", rewrite.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("class ReleaseOnly", rewrite.OutputCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// LN-3 must not fail files the conditional pass deliberately preserved
    /// (shared sources compiled by several projects with conflicting symbol sets):
    /// their residual directives are reported as one warning instead of errors.
    /// Without a resolvable tree root the same file still fails.
    /// </summary>
    [Fact]
    public async Task LexicalNormalize_ResidualDirectives_ReportErrors()
    {
        const string code = """
            #if LibA
            public
            #else
            internal
            #endif
            class Shared {}
            """;
        var sourceFile = Path.Combine(_root, "Common", "Shared.cs");
        Directory.CreateDirectory(Path.Combine(_root, "Common"));
        File.WriteAllText(sourceFile, code);

        var context = new PassFileContext
        {
            SourceCode = code,
            SourceFilePath = sourceFile,
            RelativePath = "Common/Shared.cs",
            DestinationFilePath = Path.Combine(_root, "out", "Common", "Shared.cs"),
        };

        var adapter = new LexicalNormalizePassAdapter(
            new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions(),
            verbose: false,
            filterUnsupportedProjects: true,
            compatNamespacePrefixes: null);
        adapter.MergeFilePureResult(await adapter.RewriteFilePureAsync(context));

        Assert.True(adapter.HasErrors);
    }

    private static PassChainConfiguration CreateDefaultChainConfig(string sourceRoot) =>
        new()
        {
            SourceRoot = sourceRoot,
            MakerOptionsFactory = () => new CSharpToJava.Core.ReadOnlyStructMaker.ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new CSharpToJava.Core.RecordReadOnly.RecordReadOnlyOptions(),
            JavaKeywordOptionsFactory = () => new CSharpToJava.Core.JavaKeywords.JavaKeywordOptions(),
            MemberDesugarOptionsFactory = () => new CSharpToJava.Core.MemberDesugar.MemberDesugarOptions(),
            StructValueSemanticsOptionsFactory = () => new CSharpToJava.Core.StructValueSemantics.StructValueSemanticsOptions(),
            VarianceAuditOptionsFactory = () => new CSharpToJava.Core.VarianceAudit.VarianceAuditOptions(),
            EnumDeclarationOptionsFactory = () => new CSharpToJava.Core.EnumDeclaration.EnumDeclarationOptions(),
            LexicalNormalizeOptionsFactory = () => new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions(),
            EqualityConcatOptionsFactory = () => new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
            UnsafeFfmOptionsFactory = () => new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
            ExpressionDesugarOptionsFactory = () => new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions(),
            LinqChannelOptionsFactory = () => new CSharpToJava.Core.LinqChannel.LinqChannelOptions(),
        };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
