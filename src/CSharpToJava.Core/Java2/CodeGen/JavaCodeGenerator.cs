// src/CSharpToJava.Core/Java2/CodeGen/JavaCodeGenerator.cs
namespace CSharpToJava.Core.Java2.CodeGen;

public class JavaCodeGenerator
{
    private readonly TypeDeclarationWriter _typeWriter = new();
    private readonly ImportCollector _importCollector = new();

    public string Generate(IrCompilationUnit unit)
    {
        var w = new IndentedWriter();

        // Package
        if (!string.IsNullOrEmpty(unit.Package))
        {
            w.WriteLine("package " + unit.Package + ";");
            w.WriteLine("");
        }

        // Collect and write imports
        _importCollector.Collect(unit);
        var sortedImports = _importCollector.GetSortedImports();
        foreach (var imp in sortedImports)
            w.WriteLine("import " + imp + ";");
        if (sortedImports.Count > 0)
            w.WriteLine("");

        // Type declarations
        for (int i = 0; i < unit.TypeDeclarations.Count; i++)
        {
            _typeWriter.Write(unit.TypeDeclarations[i], w);
            if (i < unit.TypeDeclarations.Count - 1)
                w.WriteLine("");
        }

        return w.ToString();
    }
}
