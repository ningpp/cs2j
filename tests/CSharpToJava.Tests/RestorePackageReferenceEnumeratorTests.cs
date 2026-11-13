using CSharpToJava.CLI.Preprocessing;

namespace CSharpToJava.Tests;

/// <summary>
/// The NuGet global-cache fallback used when an intermediate tree carries no
/// restore output (chained trees normally have none): package references come
/// from the tree's csproj graph plus the nuspec dependency closure.
/// </summary>
public sealed class RestorePackageReferenceEnumeratorTests : IDisposable
{
    private static readonly string ManagedAssemblyPath =
        typeof(Xunit.FactAttribute).Assembly.Location;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cs2j-nuget-fallback-" + Guid.NewGuid().ToString("N"));
    private readonly string _tree;
    private readonly string _packages;

    public RestorePackageReferenceEnumeratorTests()
    {
        _tree = Path.Combine(_root, "tree");
        _packages = Path.Combine(_root, "packages");
        Directory.CreateDirectory(_tree);
        Directory.CreateDirectory(_packages);
    }

    [Fact]
    public void NoAssets_ResolvesPackageReferenceFromGlobalPackageCache()
    {
        WriteProject("App", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Fake.Alpha" Version="1.2.3" />
              </ItemGroup>
            </Project>
            """);
        CreatePackage("Fake.Alpha", "1.2.3", "FakeAlpha.dll");

        var references = RestorePackageReferenceEnumerator.Enumerate(
            _tree, _packages, out var addedCount);

        var reference = Assert.Single(references);
        Assert.Equal(1, addedCount);
        Assert.EndsWith(
            Path.Combine("fake.alpha", "1.2.3", "lib", "netstandard2.0", "FakeAlpha.dll"),
            reference.Display,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NuspecDependencyClosure_AddsDependencyAssemblies()
    {
        WriteProject("App", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Fake.A" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        CreatePackage("Fake.A", "1.0.0", "FakeA.dll", ("Fake.B", "2.0.0"));
        CreatePackage("Fake.B", "2.0.0", "FakeB.dll", ("Fake.C", "3.0.0"));
        CreatePackage("Fake.C", "3.0.0", "FakeC.dll");

        var references = RestorePackageReferenceEnumerator.Enumerate(
            _tree, _packages, out var addedCount);

        var simpleNames = references
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Display))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(new[] { "FakeA", "FakeB", "FakeC" }, simpleNames);
        Assert.Equal(3, addedCount);
    }

    [Fact]
    public void DuplicateAssemblyNames_KeepHighestVersion_AndTpaAssembliesAreSkipped()
    {
        WriteProject("ProjectA", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Fake.Lib" Version="1.0.0" />
                <PackageReference Include="Fake.Tpa" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        WriteProject("ProjectB", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Fake.Lib" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """);
        CreatePackage("Fake.Lib", "1.0.0", "FakeLib.dll");
        CreatePackage("Fake.Lib", "2.0.0", "FakeLib.dll");
        CreatePackage("Fake.Tpa", "1.0.0", "System.Runtime.dll");

        var references = RestorePackageReferenceEnumerator.Enumerate(
            _tree, _packages, out _);

        var fakeLib = Assert.Single(references);
        Assert.EndsWith(
            Path.Combine("fake.lib", "2.0.0", "lib", "netstandard2.0", "FakeLib.dll"),
            fakeLib.Display,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            references,
            reference => string.Equals(
                Path.GetFileNameWithoutExtension(reference.Display),
                "System.Runtime",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingPackageOrMalformedXml_DoesNotThrow()
    {
        WriteProject("Malformed", "<Project><ItemGroup>");
        WriteProject("App", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Fake.Gone" Version="9.9.9" />
                <PackageReference Include="Fake.Broken" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);

        // Package directory absent: silently skipped.
        // Package with a broken nuspec: the assembly still binds, dependencies
        // are dropped instead of failing the run.
        var brokenDirectory = CreatePackage("Fake.Broken", "1.0.0", "FakeBroken.dll");
        File.WriteAllText(
            Path.Combine(brokenDirectory, "fake.broken.nuspec"),
            "<package><metadata>");
        Directory.CreateDirectory(
            Path.Combine(_packages, "fake.broken", "1.0.1", "lib", "netstandard2.0"));

        var references = RestorePackageReferenceEnumerator.Enumerate(
            _tree, _packages, out var addedCount);

        var reference = Assert.Single(references);
        Assert.EndsWith("FakeBroken.dll", reference.Display, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, addedCount);
    }

    private void WriteProject(string name, string csprojXml)
    {
        var directory = Path.Combine(_tree, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".csproj"), csprojXml);
    }

    /// <summary>
    /// Creates a synthetic installed package: a real managed assembly copied
    /// under a fake package name plus a nuspec carrying the given dependencies.
    /// </summary>
    private string CreatePackage(
        string id,
        string version,
        string assemblyFileName,
        params (string Id, string Version)[] dependencies)
    {
        var packageDirectory = Path.Combine(_packages, id.ToLowerInvariant(), version);
        var libDirectory = Path.Combine(packageDirectory, "lib", "netstandard2.0");
        Directory.CreateDirectory(libDirectory);
        File.Copy(
            ManagedAssemblyPath,
            Path.Combine(libDirectory, assemblyFileName),
            overwrite: true);

        var dependenciesXml = dependencies.Length == 0
            ? string.Empty
            : "<dependencies><group targetFramework=\".NETStandard2.0\">"
              + string.Concat(dependencies.Select(dependency =>
                  $"<dependency id=\"{dependency.Id}\" version=\"[{dependency.Version}, )\" />"))
              + "</group></dependencies>";
        File.WriteAllText(
            Path.Combine(packageDirectory, id.ToLowerInvariant() + ".nuspec"),
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <package>
               <metadata>
                 <id>{id}</id>
                 <version>{version}</version>
                 {dependenciesXml}
               </metadata>
             </package>
             """);
        return packageDirectory;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
