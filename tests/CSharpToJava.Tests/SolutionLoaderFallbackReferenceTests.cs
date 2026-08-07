using CSharpToJava.Core.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

/// <summary>
/// ?????MSBuildWorkspace ??????/??????????????netstandard2.0
/// ??? NETStandard.Library ????????????????? TS/Java ???????
/// ?????? Java ???SolutionLoader ????? dotnet packs ?????????
/// </summary>
public class SolutionLoaderFallbackReferenceTests
{
    [Fact]
    public async Task ProjectWithoutRestore_GetsFrameworkReferences_Injected()
    {
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs2j_ref_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);
        try
        {
            var csproj = Path.Combine(tempPath, "Lib.csproj");
            File.WriteAllText(csproj, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>netstandard2.0</TargetFramework>
                    <RestorePackages>false</RestorePackages>
                  </PropertyGroup>
                </Project>
                """);
            var libPath = Path.Combine(tempPath, "Lib.cs");
            File.WriteAllText(libPath, """
                using System.Linq;
                public class Lib
                {
                    public int M(int[] xs) => xs.Select(x => x * 2).Sum();
                }
                """);

            SolutionLoader.EnsureMSBuildRegistered();
            using var loader = new SolutionLoader();
            var projects = await loader.TryOpenAsync(csproj, null);

            Assert.NotNull(projects);
            var compilation = projects![0].Compilation;
            if (compilation.References.Count() == 0 && SolutionLoader.GetFallbackFrameworkReferences().Count == 0)
            {
                // ???? dotnet packs ???????????SDK ?????? SDK ????
                return;
            }

            Assert.True(compilation.References.Count() > 0,
                "Expected framework references to be injected when MSBuild resolves none.");

            var tree = compilation.SyntaxTrees.FirstOrDefault(t => t.FilePath == libPath);
            Assert.NotNull(tree);
            var model = compilation.GetSemanticModel(tree!);
            var selectCall = tree!.GetRoot().DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(i => i.Expression.ToString().EndsWith("Select", StringComparison.Ordinal));
            Assert.NotNull(selectCall);

            var symbol = model.GetSymbolInfo(selectCall!).Symbol;
            Assert.NotNull(symbol);
            Assert.Equal("System.Linq.Enumerable", symbol!.ContainingType?.ToDisplayString());
        }
        finally
        {
            try { Directory.Delete(tempPath, recursive: true); } catch { }
        }
    }
}
