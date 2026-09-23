using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// A type reference must stay unambiguous when the namespace currently being converted also
/// declares a DIFFERENT type with the same simple name.
/// <para>
/// The converted <c>dotnet-collections-base</c> project has both the non-generic
/// <c>dotnet.System.Collections.IComparer</c> and the generic
/// <c>dotnet.System.Collections.Generic.IComparer&lt;T&gt;</c>. Java resolves a bare
/// <c>IComparer</c> inside package <c>dotnet.System.Collections</c> to the same-package
/// non-generic type — a same-package type beats an on-demand (wildcard) import — so
/// <c>implements IComparer&lt;T&gt;</c> fails with
/// "type dotnet.System.Collections.IComparer does not take parameters".
/// </para>
/// Reproduces CollectionInterfaceBridges.cs (BridgedComparer / BclEqualityComparerAdapter).
/// </summary>
public class CrossNamespaceSameNameTypeTests
{
    [Fact]
    public async Task GenericTypeShadowedByNonGenericSibling_IsPackageQualified()
    {
        var root = CreateProject();

        try
        {
            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(root, new ConversionOptions());

            var bridged = Assert.Single(results, r =>
                string.Equals(r.FileName, "BridgedComparer.java", StringComparison.OrdinalIgnoreCase));
            Assert.True(bridged.Success, string.Join("\n", bridged.Diagnostics));

            var code = bridged.GeneratedCode ?? "";
            Assert.Contains("implements dotnet.System.Collections.Generic.IComparer<T>", code);
            Assert.Contains("dotnet.System.Collections.Generic.IComparer<T> _comparer", code);
            Assert.DoesNotContain("implements IComparer<T>", code);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string CreateProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs2j-shadow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Generic"));

        File.WriteAllText(Path.Combine(root, "Proj.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <Nullable>enable</Nullable>
                <EnableDefaultItems>false</EnableDefaultItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="**\*.cs" Exclude="obj\**\*.*" />
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(root, "Generic", "Interfaces.cs"), """
            namespace dotnet.System.Collections.Generic
            {
                public interface IComparer<in T>
                {
                    int Compare(T? x, T? y);
                }
            }
            """);

        File.WriteAllText(Path.Combine(root, "Interfaces.cs"), """
            namespace dotnet.System.Collections
            {
                public interface IComparer
                {
                    int Compare(object? x, object? y);
                }
            }
            """);

        File.WriteAllText(Path.Combine(root, "Bridges.cs"), """
            using global::System.Collections.Generic;

            namespace dotnet.System.Collections
            {
                internal sealed class BridgedComparer<T> : global::System.Collections.Generic.IComparer<T>
                {
                    private readonly dotnet.System.Collections.Generic.IComparer<T> _comparer;

                    internal BridgedComparer(dotnet.System.Collections.Generic.IComparer<T> comparer)
                    {
                        _comparer = comparer;
                    }

                    public int Compare(T? x, T? y) => _comparer.Compare(x, y);
                }
            }
            """);

        return root;
    }
}
