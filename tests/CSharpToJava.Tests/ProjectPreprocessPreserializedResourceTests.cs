using CSharpToJava.CLI;

namespace CSharpToJava.Tests;

/// <summary>
/// Each preprocessor prepares its own intermediate tree, and later passes copy
/// the previous pass' Directory.Build.targets. The preserialized-resource
/// snippet must therefore only be injected when it is not present yet.
/// </summary>
public sealed class ProjectPreprocessPreserializedResourceTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "cs2j-preserialized-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }

    [Fact]
    public void EnsurePreserializedResourceSupport_CalledTwiceOnSameTree_InjectsSnippetOnce()
    {
        var intermediateRoot = Path.Combine(_tempRoot, "intermediate");
        Directory.CreateDirectory(intermediateRoot);
        var assemblyPath = @"C:\fake\nuget\A&B\System.Resources.Extensions.dll";

        ProjectPreprocessSource.EnsurePreserializedResourceSupport(
            intermediateRoot, "[test]", false, assemblyPath);
        ProjectPreprocessSource.EnsurePreserializedResourceSupport(
            intermediateRoot, "[test]", false, assemblyPath);

        var targetsPath = Path.Combine(intermediateRoot, "Directory.Build.targets");
        var content = File.ReadAllText(targetsPath);

        Assert.Equal(1, CountOccurrences(content, "ReferencePathWithRefAssemblies"));
        Assert.Contains("A&amp;B", content);
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
