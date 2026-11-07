using CSharpToJava.Core.Preprocessing.Jcs;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// R12: the consumer-side input hash must be stable across absolute roots,
/// sensitive to source/project-asset content, insensitive to timestamps, and
/// must ignore bin/obj/.cs2j-* intermediate trees.
/// </summary>
public sealed class ManifestInputHashTests
{
    [Fact]
    public void Hash_is_stable_when_the_same_tree_is_copied_to_another_root()
    {
        var first = CreateTree();
        var second = CreateTree();

        try
        {
            Assert.Equal(Hash(first), Hash(second));
        }
        finally
        {
            DeleteTree(first);
            DeleteTree(second);
        }
    }

    [Fact]
    public void Hash_changes_when_any_source_file_content_changes()
    {
        var tree = CreateTree();

        try
        {
            var before = Hash(tree);
            File.AppendAllText(Path.Combine(tree, "Program.cs"), "\n// changed\n");
            Assert.NotEqual(before, Hash(tree));
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void Hash_changes_when_the_tfm_or_a_reference_changes()
    {
        var tree = CreateTree();

        try
        {
            var before = Hash(tree);
            var project = Path.Combine(tree, "App.csproj");
            var text = File.ReadAllText(project);
            File.WriteAllText(project, text.Replace("<TargetFramework>net10.0", "<TargetFramework>net9.0"));
            Assert.NotEqual(before, Hash(tree));

            var afterTfm = Hash(tree);
            File.WriteAllText(
                project,
                text.Replace("Version=\"8.0.0\"", "Version=\"9.0.0\""));
            Assert.NotEqual(afterTfm, Hash(tree));
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void Hash_is_insensitive_to_file_timestamps()
    {
        var tree = CreateTree();

        try
        {
            var before = Hash(tree);
            File.SetLastWriteTimeUtc(
                Path.Combine(tree, "Program.cs"),
                DateTime.UtcNow.AddDays(-7));
            Assert.Equal(before, Hash(tree));
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void Hash_ignores_bin_obj_and_cs2j_intermediate_directories()
    {
        var tree = CreateTree();

        try
        {
            var before = Hash(tree);
            foreach (var directory in new[] { "bin", "obj", ".cs2j-preprocessed-src", ".git" })
            {
                var path = Path.Combine(tree, directory);
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "Skipped.cs"), "class Skipped { }");
            }

            Assert.Equal(before, Hash(tree));
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void Hash_normalizes_crlf_to_lf()
    {
        var lfTree = CreateTree();
        var crlfTree = CreateTree();

        try
        {
            File.WriteAllText(
                Path.Combine(lfTree, "Program.cs"),
                "class P\n{\n    void M() { }\n}\n");
            File.WriteAllText(
                Path.Combine(crlfTree, "Program.cs"),
                "class P\r\n{\r\n    void M() { }\r\n}\r\n");

            Assert.Equal(Hash(lfTree), Hash(crlfTree));
        }
        finally
        {
            DeleteTree(lfTree);
            DeleteTree(crlfTree);
        }
    }

    private static string Hash(string treeRoot)
        => ManifestInputHash.Compute(
            treeRoot,
            ManifestSolutionVariant.FromTree(treeRoot, "default"));

    private static string CreateTree()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "cs2j-manifest-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Lib"));

        File.WriteAllText(
            Path.Combine(root, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="System.Text.Json" Version="8.0.0" />
                <ProjectReference Include="Lib/Lib.csproj" />
                <Reference Include="System.Runtime" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(root, "Program.cs"), "class P { void M() { } }\n");
        File.WriteAllText(
            Path.Combine(root, "Lib", "Lib.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net10.0;netstandard2.0</TargetFrameworks>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(root, "Lib", "L.cs"), "class L { }\n");
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
