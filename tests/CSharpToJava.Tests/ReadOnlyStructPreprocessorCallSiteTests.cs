using CSharpToJava.CLI;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for the project-level (cross-file) call-site update pass that runs after
/// the readonly-struct rewrite (UpdateCrossFileCallSites).
/// </summary>
public class ReadOnlyStructPreprocessorCallSiteTests
{
    private static async Task<(string SourceRoot, string IntermediateRoot)> CreateSandboxAsync(
        (string Name, string Content)[] files)
    {
        var temp = Path.Combine(Path.GetTempPath(), "cs2j-ros-callsite-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(temp, "source");
        var intermediateRoot = Path.Combine(temp, "intermediate");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(intermediateRoot);
        foreach (var (name, content) in files)
        {
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, name), content);
            await File.WriteAllTextAsync(Path.Combine(intermediateRoot, name), content);
        }
        return (sourceRoot, intermediateRoot);
    }

    [Fact]
    public async Task GenericMigratedStruct_VoidCallSite_UpdatedToAssignment()
    {
        // The rewritten struct returns ArrayBuilder<T> (GenericNameSyntax). The
        // cross-file pass must still register Add as migrated and rewrite
        // `pathSegments.Add(x);` → `pathSegments = pathSegments.Add(x);`.
        var (sourceRoot, intermediateRoot) = await CreateSandboxAsync(new[]
        {
            ("ArrayBuilder.cs", """
            namespace App;
            internal readonly struct ArrayBuilder<T> {
                private T[] _array;
                private int _count;
                public ArrayBuilder<T> Add(T item) { var result = this; result._count++; return result; }
            }
            """),
            ("User.cs", """
            namespace App;
            class User {
                public string[] Segments(string path) {
                    var pathSegments = new ArrayBuilder<string>();
                    pathSegments.Add(path);
                    return pathSegments.ToArray();
                }
            }
            """),
        });

        // The preprocessor registers migrated structs by diagnostic StructName, which
        // for generic structs carries the type parameters ("ArrayBuilder<T>").
        var migratedStructs = new Dictionary<string, string> { ["ArrayBuilder<T>"] = "ArrayBuilder<T>" };
        var request = new ProjectReadOnlyPreprocessRequest
        {
            SourcePath = sourceRoot,
            DestinationRoot = intermediateRoot,
            VerifyBuild = false,
            VerifyTests = false,
        };

        await ProjectReadOnlyStructPreprocessor.UpdateCrossFileCallSites(
            intermediateRoot, sourceRoot,
            new List<string> { Path.Combine(sourceRoot, "ArrayBuilder.cs"), Path.Combine(sourceRoot, "User.cs") },
            migratedStructs, request);

        var updated = await File.ReadAllTextAsync(Path.Combine(intermediateRoot, "User.cs"));
        Assert.Contains("pathSegments = pathSegments.Add(", updated);
    }
}
