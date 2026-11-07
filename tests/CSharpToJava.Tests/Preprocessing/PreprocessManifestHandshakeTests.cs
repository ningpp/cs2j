using System.Text.Json;
using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Preprocessing.Jcs;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// R12 two-stage handshake: the final preprocessed tree carries a versioned
/// manifest; --no-preprocess consumers re-derive the input hash from the tree
/// and gate java-ready/audit on the required guaranteed capabilities.
/// </summary>
public sealed class PreprocessManifestHandshakeTests
{
    [Fact]
    public void Java_ready_accepts_a_manifest_emitted_for_the_same_tree()
    {
        var tree = CreateTree();

        try
        {
            var path = Emit(tree);

            Assert.True(File.Exists(path));
            Assert.Equal(PreprocessManifestWriter.DefaultFileName, Path.GetFileName(path));

            var result = PreprocessManifestValidator.ValidateForExistingTree(tree, "java-ready");

            Assert.True(result.IsValid, result.Message);
            Assert.Equal("verified", result.SubsetStatus);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void Java_ready_rejects_a_missing_manifest_with_cs2j1410()
    {
        var tree = CreateTree();

        try
        {
            var result = PreprocessManifestValidator.ValidateForExistingTree(tree, "java-ready");

            Assert.False(result.IsValid);
            Assert.StartsWith("CS2J1410:", result.Message, StringComparison.Ordinal);
            Assert.Contains("preprocess", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void Java_ready_rejects_a_wrong_subset_version()
    {
        var tree = CreateTree();

        try
        {
            var path = Emit(tree);
            var json = File.ReadAllText(path);
            File.WriteAllText(
                path,
                json.Replace("\"subsetVersion\": \"JCS-1\"", "\"subsetVersion\": \"JCS-2\""));

            var result = PreprocessManifestValidator.ValidateForExistingTree(tree, "java-ready");

            Assert.False(result.IsValid);
            Assert.StartsWith("CS2J1410:", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void Java_ready_rejects_a_source_change_after_the_manifest_was_emitted()
    {
        var tree = CreateTree();

        try
        {
            Emit(tree);
            File.AppendAllText(Path.Combine(tree, "Program.cs"), "\n// drifted\n");

            var result = PreprocessManifestValidator.ValidateForExistingTree(tree, "java-ready");

            Assert.False(result.IsValid);
            Assert.StartsWith("CS2J1410:", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void Java_ready_rejects_a_partial_required_capability()
    {
        var tree = CreateTree();

        try
        {
            var path = Emit(tree);
            var json = File.ReadAllText(path);
            Assert.Contains("\"status\": \"guaranteed\"", json, StringComparison.Ordinal);
            File.WriteAllText(
                path,
                json.Replace("\"status\": \"guaranteed\"", "\"status\": \"partial\""));

            var result = PreprocessManifestValidator.ValidateForExistingTree(tree, "java-ready");

            Assert.False(result.IsValid);
            Assert.StartsWith("CS2J1410:", result.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void Default_profile_keeps_an_unmanifested_tree_convertible_but_unverified()
    {
        var tree = CreateTree();

        try
        {
            var result = PreprocessManifestValidator.ValidateForExistingTree(tree, "default");

            Assert.True(result.IsValid, result.Message);
            Assert.Equal("unverified", result.SubsetStatus);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void Default_profile_marks_a_matching_manifest_verified()
    {
        var tree = CreateTree();

        try
        {
            Emit(tree);

            var result = PreprocessManifestValidator.ValidateForExistingTree(tree, "default");

            Assert.True(result.IsValid, result.Message);
            Assert.Equal("verified", result.SubsetStatus);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void FindFromInput_walks_up_from_a_project_file_inside_the_tree()
    {
        var tree = CreateTree();

        try
        {
            Emit(tree);

            var found = PreprocessManifestWriter.FindFromInput(
                Path.Combine(tree, "App.csproj"));

            Assert.Equal(
                Path.Combine(tree, PreprocessManifestWriter.DefaultFileName),
                found);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public async Task Cli_no_preprocess_java_ready_without_manifest_exits_one()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(), "cs2j-no-preprocess-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(tempRoot, "source");
        var destinationRoot = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            File.WriteAllText(Path.Combine(sourceRoot, "Program.cs"), "class P { void M() { } }");

            var exit = await Program.MainImpl([
                "convert-project",
                "-s", sourceRoot,
                "-d", destinationRoot,
                "--no-preprocess",
                "--profile", "java-ready",
                "--no-verify-build",
                "--no-verify-tests",
            ]);

            Assert.Equal(1, exit);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Preprocess_only_moves_the_manifest_to_the_destination_root()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(), "cs2j-preprocess-only-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(tempRoot, "source");
        var destinationRoot = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            File.WriteAllText(Path.Combine(sourceRoot, "Program.cs"), "class P { void M() { } }");

            var exit = await Program.MainImpl([
                "convert-project",
                "-s", sourceRoot,
                "-d", destinationRoot,
                "--preprocess-only",
                "--no-verify-build",
                "--no-verify-tests",
            ]);

            Assert.Equal(0, exit);

            var manifestPath = Path.Combine(
                destinationRoot, PreprocessManifestWriter.DefaultFileName);
            Assert.True(File.Exists(manifestPath));

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            Assert.Equal(
                PreprocessManifest.ManifestSchemaVersion,
                document.RootElement.GetProperty("schemaVersion").GetInt32());
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static string Emit(string tree)
    {
        PreprocessManifestEmitter.Emit(
            tree,
            "java-ready",
            PassCatalog.Implemented,
            Array.Empty<PassReportEntry>(),
            residualReportPath: null,
            configuration: "default");

        return Path.Combine(tree, PreprocessManifestWriter.DefaultFileName);
    }

    private static string CreateTree()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "cs2j-manifest-handshake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Program.cs"), "class P { void M() { } }\n");
        File.WriteAllText(
            Path.Combine(root, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        return root;
    }

    private static void DeleteTree(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
