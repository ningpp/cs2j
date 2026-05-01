using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.HIR;
using CSharpToJava.Core.Java2;
using CSharpToJava.Core.Java2.CodeGen;
using CSharpToJava.Core.Lowering;
using CSharpToJava.TypeMapping;

namespace CSharpToJava.Core.Pipeline;

public class NewConversionResult
{
    public bool Success { get; set; }
    public string GeneratedCode { get; set; } = "";
    public List<DiagnosticMessage> Diagnostics { get; set; } = new();
    public string? FileName { get; set; }
}

public class NewConversionPipeline
{
    private readonly List<ILoweringPass> _loweringPasses;

    public NewConversionPipeline()
    {
        _loweringPasses = new List<ILoweringPass>
        {
            new LowerRefOut(), new LowerYield(), new LowerUsing(),
            new LowerProperty(), new LowerIndexer(), new LowerOperator(),
            new LowerEvent(), new LowerDelegate(), new LowerStruct(),
            new LowerPatternMatch(), new VariableResolution(),
        };
    }

    public NewConversionResult Convert(string sourceCode, ConversionOptions options, string? fileName = null)
    {
        var typeMappings = new TypeMappingRegistry(options.TypeMappingConfigPath);
        var context = new ConversionContext(options, typeMappings);
        var result = new NewConversionResult { FileName = fileName };

        try
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);
            if (syntaxTree.GetDiagnostics().Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
            {
                foreach (var diag in syntaxTree.GetDiagnostics())
                    context.Diagnostics.Error(diag.GetMessage(), diag.Location);
                result.Diagnostics = context.Diagnostics.Messages.ToList();
                return result;
            }

            var compilation = CSharpCompilation.Create("TempAssembly", new[] { syntaxTree },
                references: new[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
                },
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            context.SemanticModel = compilation.GetSemanticModel(syntaxTree);
            context.ProjectCompilation = compilation;

            // Phase 2: HIR Generation
            var root = (CompilationUnitSyntax)syntaxTree.GetRoot();
            var hirGenerator = new CSharpToJavaHIRGenerator();
            IrCompilationUnit ir = hirGenerator.Generate(root, context);

            // Phase 3: Lowering
            foreach (var pass in _loweringPasses)
                ir = pass.Apply(ir, context);

            // Phase 4: Validation
            ValidateNoCSharpNodes(ir, context);

            // Phase 5: CodeGen
            var codeGen = new JavaCodeGenerator();
            var javaCode = codeGen.Generate(ir);

            result.Success = context.Diagnostics.Messages.All(m => m.Severity != CSharpToJava.Core.Context.DiagnosticSeverity.Error);
            result.GeneratedCode = javaCode;
            result.Diagnostics = context.Diagnostics.Messages.ToList();
        }
        catch (Exception ex)
        {
            context.Diagnostics.Error("Pipeline error: " + ex.Message);
            result.Diagnostics = context.Diagnostics.Messages.ToList();
        }
        return result;
    }

    private static void ValidateNoCSharpNodes(IrCompilationUnit ir, ConversionContext context)
    {
        // Walk types and check for remaining CSharp nodes
        var found = new List<string>();
        foreach (var type in ir.TypeDeclarations)
            CollectCSharpNodes(type, found);
        foreach (var nodeType in found)
            context.Diagnostics.Error("Remaining C# extension node after Lowering: " + nodeType, code: "CS2J5001");
    }

    private static void CollectCSharpNodes(IrTypeDeclaration type, List<string> found)
    {
        // This is a simplified check. Full implementation would walk the entire IR tree.
        if (type.GetType().Name.StartsWith("IrCSharp")) found.Add(type.GetType().Name);
        foreach (var nested in type.NestedTypes) CollectCSharpNodes(nested, found);
    }
}
