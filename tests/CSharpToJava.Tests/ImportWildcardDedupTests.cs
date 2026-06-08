using CSharpToJava.Core.Java;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class ImportWildcardDedupTests
{
    [Fact]
    public void ExplicitJdkImport_RemovedWhenCoveredByJdkWildcard()
    {
        // java.util.ArrayList should be removed when java.util.* exists
        var cu = new JavaCompilationUnit { Package = "test" };
        cu.Imports.Add(new JavaImport("java.util", isWildcard: true));
        cu.Imports.Add(new JavaImport("java.util.ArrayList"));

        var output = cu.ToString("");

        Assert.Contains("import java.util.*;", output);
        Assert.DoesNotContain("import java.util.ArrayList;", output);
    }

    [Fact]
    public void ExplicitProjectImport_PreservedEvenWhenCoveredByProjectWildcard()
    {
        // Microsoft.Msagl.Core.DataStructures.Set should NOT be removed even though
        // Microsoft.Msagl.Core.DataStructures.* exists — it may disambiguate against java.util.*
        var cu = new JavaCompilationUnit { Package = "test" };
        cu.Imports.Add(new JavaImport("java.util", isWildcard: true));
        cu.Imports.Add(new JavaImport("Microsoft.Msagl.Core.DataStructures", isWildcard: true));
        cu.Imports.Add(new JavaImport("Microsoft.Msagl.Core.DataStructures.Set"));

        var output = cu.ToString("");

        Assert.Contains("import java.util.*;", output);
        Assert.Contains("import Microsoft.Msagl.Core.DataStructures.*;", output);
        Assert.Contains("import Microsoft.Msagl.Core.DataStructures.Set;", output);
    }

    [Fact]
    public void StaticImport_NeverRemovedByWildcardDedup()
    {
        var cu = new JavaCompilationUnit { Package = "test" };
        cu.Imports.Add(new JavaImport("java.lang.Math", isWildcard: false, isStatic: true));
        cu.Imports.Add(new JavaImport("java.lang", isWildcard: true));

        var output = cu.ToString("");

        Assert.Contains("import static java.lang.Math;", output);
    }

    [Fact]
    public void CrossPackageImports_DoNotRewriteCompatImportsToProjectGroupPackage()
    {
        var cu = new JavaCompilationUnit { Package = "sample" };
        cu.Imports.Add(new JavaImport("io.github.ningpp.compat.GCHandle"));

        var results = new List<ConversionResult>
        {
            new()
            {
                Success = true,
                FileName = "Sample.java",
                Package = "sample",
                Compilation = cu,
                GeneratedCode = cu.ToString(""),
            },
        };

        CrossPackageImportResolver.AddCrossPackageImports(results, "io.github.ningpp.csharp.uri.compat");

        Assert.Contains("import io.github.ningpp.compat.GCHandle;", results[0].GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.*;", results[0].GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("import io.github.ningpp.csharp.uri.compat.*;", results[0].GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("import io.github.ningpp.csharp.uri.compat.GCHandle;", results[0].GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossPackageImports_StringFallbackDoesNotRewriteCompatImportsToProjectGroupPackage()
    {
        var results = new List<ConversionResult>
        {
            new()
            {
                Success = true,
                FileName = "Sample.java",
                Package = "sample",
                GeneratedCode = """
                    package sample;

                    import io.github.ningpp.compat.GCHandle;

                    class Sample {}
                    """,
            },
        };

        CrossPackageImportResolver.AddCrossPackageImports(results, "io.github.ningpp.csharp.uri.compat");

        Assert.Contains("import io.github.ningpp.compat.GCHandle;", results[0].GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.*;", results[0].GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("import io.github.ningpp.csharp.uri.compat.*;", results[0].GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("import io.github.ningpp.csharp.uri.compat.GCHandle;", results[0].GeneratedCode, StringComparison.Ordinal);
    }
}
