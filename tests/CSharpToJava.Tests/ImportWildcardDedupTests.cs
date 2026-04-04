using CSharpToJava.Core.Java;
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
}
