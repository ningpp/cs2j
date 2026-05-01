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
        foreach (var type in ir.TypeDeclarations)
            WalkType(type, context);
    }

    private static void WalkType(IrTypeDeclaration type, ConversionContext context)
    {
        CheckNode(type);
        foreach (var method in type.Methods)
        {
            if (method.Body != null) WalkBlock(method.Body, context);
        }
        if (type is IrClassDeclaration cls)
        {
            foreach (var ctor in cls.Constructors)
                if (ctor.Body != null) WalkBlock(ctor.Body, context);
        }
        foreach (var nested in type.NestedTypes)
            WalkType(nested, context);
    }

    private static void WalkBlock(IrBlockStatement block, ConversionContext context)
    {
        foreach (var stmt in block.Statements)
            WalkStatement(stmt, context);
    }

    private static void WalkStatement(IrStatement stmt, ConversionContext context)
    {
        CheckNode(stmt);
        switch (stmt)
        {
            case IrBlockStatement b: WalkBlock(b, context); break;
            case IrExpressionStatement es: WalkExpression(es.Expression, context); break;
            case IrVariableDeclarationStatement vd: if (vd.Initializer != null) WalkExpression(vd.Initializer, context); break;
            case IrReturnStatement rs: if (rs.Expression != null) WalkExpression(rs.Expression, context); break;
            case IrIfStatement ifs: WalkExpression(ifs.Condition, context); WalkStatement(ifs.ThenBody, context); if (ifs.ElseBody != null) WalkStatement(ifs.ElseBody, context); break;
            case IrForEachStatement fe: WalkExpression(fe.Collection, context); WalkStatement(fe.Body, context); break;
            case IrForStatement f: if (f.Condition != null) WalkExpression(f.Condition, context); WalkStatement(f.Body, context); break;
            case IrWhileStatement w: WalkExpression(w.Condition, context); WalkStatement(w.Body, context); break;
            case IrDoWhileStatement dw: WalkExpression(dw.Condition, context); WalkStatement(dw.Body, context); break;
            case IrTryCatchStatement tc: WalkBlock(tc.TryBody, context); foreach (var cc in tc.CatchClauses) WalkBlock(cc.Body, context); if (tc.FinallyBody != null) WalkBlock(tc.FinallyBody, context); break;
            case IrThrowStatement th: WalkExpression(th.Expression, context); break;
            case IrSwitchStatement sw: WalkExpression(sw.Expression, context); foreach (var sec in sw.Sections) foreach (var s in sec.Statements) WalkStatement(s, context); break;
        }
    }

    private static void WalkExpression(IrExpression expr, ConversionContext context)
    {
        CheckNode(expr);
        switch (expr)
        {
            case IrBinaryExpression bin: WalkExpression(bin.Left, context); WalkExpression(bin.Right, context); break;
            case IrUnaryExpression un: WalkExpression(un.Operand, context); break;
            case IrConditionalExpression cond: WalkExpression(cond.Condition, context); WalkExpression(cond.WhenTrue, context); WalkExpression(cond.WhenFalse, context); break;
            case IrCastExpression cast: WalkExpression(cast.Expression, context); break;
            case IrNewExpression n: foreach (var a in n.Arguments) WalkExpression(a, context); break;
            case IrMemberAccessExpression mem: WalkExpression(mem.Target, context); break;
            case IrInvocationExpression inv: if (inv.Target != null) WalkExpression(inv.Target, context); foreach (var a in inv.Arguments) WalkExpression(a, context); break;
            case IrAssignmentExpression asgn: WalkExpression(asgn.Target, context); WalkExpression(asgn.Value, context); break;
            case IrArrayAccessExpression arr: WalkExpression(arr.Target, context); WalkExpression(arr.Index, context); break;
            case IrLambdaExpression lam: if (lam.ExpressionBody != null) WalkExpression(lam.ExpressionBody, context); if (lam.BlockBody != null) WalkBlock(lam.BlockBody, context); break;
            case IrInstanceOfExpression inst: WalkExpression(inst.Expression, context); break;
        }
    }

    private static void CheckNode(IrNode node)
    {
        var typeName = node.GetType().Name;
        if (typeName.StartsWith("IrCSharp") || typeName.StartsWith("CSharp"))
            throw new InvalidOperationException($"C# extension node '{typeName}' remains after Lowering phase");
    }
}
