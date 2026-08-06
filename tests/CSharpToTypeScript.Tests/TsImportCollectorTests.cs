using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

public class TsImportCollectorTests
{
    [Fact]
    public void Add_Compat_Import_Deduplicates()
    {
        var collector = new TsImportCollector();
        collector.AddNamedImport("CsDateTime", "@csharptotypescript/compat");
        collector.AddNamedImport("CsTimeSpan", "@csharptotypescript/compat");
        collector.AddNamedImport("CsDateTime", "@csharptotypescript/compat");  // 重复
        var imports = collector.GetImports();
        Assert.Single(imports);
        Assert.Equal("@csharptotypescript/compat", imports[0].ModulePath);
        Assert.Contains("CsDateTime", imports[0].NamedImports);
        Assert.Contains("CsTimeSpan", imports[0].NamedImports);
    }

    [Fact]
    public void Add_Relative_Import_For_Same_Package_Type()
    {
        var collector = new TsImportCollector(currentNamespace: "App.Models");
        collector.AddRelativeImport("Point", "App.Models");
        var imports = collector.GetImports();
        Assert.Single(imports);
        Assert.Equal("./Point.js", imports[0].ModulePath);
    }

    [Fact]
    public void Add_Relative_Import_For_Child_Namespace()
    {
        var collector = new TsImportCollector(currentNamespace: "App");
        collector.AddRelativeImport("Point", "App.Models");
        var imports = collector.GetImports();
        Assert.Single(imports);
        Assert.Equal("./Models/Point.js", imports[0].ModulePath);
    }

    [Fact]
    public void GetImports_Sorts_By_Module_Path()
    {
        var collector = new TsImportCollector();
        collector.AddNamedImport("Z", "@csharptotypescript/compat");
        collector.AddNamedImport("A", "@csharptotypescript/other");
        var imports = collector.GetImports();
        Assert.Equal("@csharptotypescript/compat", imports[0].ModulePath);
    }

    [Fact]
    public void EmitImports_Returns_Empty_For_No_Imports()
    {
        var collector = new TsImportCollector();
        Assert.Equal("", collector.EmitImports());
    }

    [Fact]
    public void EmitImports_Formats_Named_Imports()
    {
        var collector = new TsImportCollector();
        collector.AddNamedImport("CsDateTime", "@csharptotypescript/compat");
        collector.AddNamedImport("CsTimeSpan", "@csharptotypescript/compat");
        var output = collector.EmitImports();
        Assert.Contains("import { CsDateTime, CsTimeSpan } from \"@csharptotypescript/compat\";", output);
    }
}