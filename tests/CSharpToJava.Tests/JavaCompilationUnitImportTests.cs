using CSharpToJava.Core.Java;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression: when a dotnet System.Collections(.Generic) on-demand import
/// suppresses the standard java.util.* wildcard, explicit java.util.SomeType
/// imports must still be emitted. They used to be dropped as "redundant"
/// against the wildcard that was itself suppressed, leaving simple names like
/// Objects unresolved (javac: 找不到符号 变量 Objects).
/// </summary>
public class JavaCompilationUnitImportTests
{
    [Fact]
    public void SuppressedJavaUtilWildcard_KeepsExplicitJavaUtilImport()
    {
        var cu = new JavaCompilationUnit("P");
        cu.Imports.Add(new JavaImport("java.util", isWildcard: true));
        cu.Imports.Add(new JavaImport("java.util.Objects"));
        cu.Imports.Add(new JavaImport("dotnet.System.Collections.Generic", isWildcard: true));
        var cls = new JavaClassDeclaration { Name = "Demo" };
        cls.Methods.Add(new JavaMethodDeclaration
        {
            Name = "m",
            Body = "check(Objects.equals(a, b));"
        });
        cu.TypeDeclarations.Add(cls);

        var code = cu.ToString("");

        Assert.DoesNotContain("import java.util.*;", code, StringComparison.Ordinal);
        Assert.Contains("import java.util.Objects;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SuppressedJavaUtilWildcard_BackfillsUsedKnownTypeWithoutExplicitImport()
    {
        var cu = new JavaCompilationUnit("P");
        cu.Imports.Add(new JavaImport("java.util", isWildcard: true));
        cu.Imports.Add(new JavaImport("dotnet.System.Collections.Generic", isWildcard: true));
        var cls = new JavaClassDeclaration { Name = "Demo" };
        cls.Methods.Add(new JavaMethodDeclaration
        {
            Name = "m",
            Body = "return Objects.equals(a, b);"
        });
        cu.TypeDeclarations.Add(cls);

        var code = cu.ToString("");

        Assert.Contains("import java.util.Objects;", code, StringComparison.Ordinal);
    }
}
