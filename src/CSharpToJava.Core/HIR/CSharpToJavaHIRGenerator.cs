using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.HIR;

public class CSharpToJavaHIRGenerator : CSharpSyntaxVisitor<IrNode?>, IHIRGenerator
{
    private ConversionContext _context = null!;
    private readonly HIRTypeGenerator _typeGen = new();
    private readonly HIRStatementGenerator _stmtGen = new();
    private readonly HIRExpressionGenerator _exprGen = new();
    private readonly HIRImportResolver _importResolver = new();

    public IrCompilationUnit Generate(CompilationUnitSyntax root, ConversionContext context)
    {
        _context = context;
        var unit = new IrCompilationUnit();
        foreach (var usingDirective in root.Usings)
            _importResolver.ProcessUsing(usingDirective, unit, context);
        foreach (var member in root.Members)
        {
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    ProcessNamespace(ns, unit);
                    break;
                case TypeDeclarationSyntax typeDecl:
                    var type = _typeGen.Generate(typeDecl, context);
                    if (type != null) unit.TypeDeclarations.Add(type);
                    break;
                case EnumDeclarationSyntax enumDecl:
                    var enm = _typeGen.GenerateEnum(enumDecl, context);
                    if (enm != null) unit.TypeDeclarations.Add(enm);
                    break;
                case DelegateDeclarationSyntax delegateDecl:
                    var del = _typeGen.GenerateDelegate(delegateDecl, context);
                    if (del != null) unit.TypeDeclarations.Add(del);
                    break;
            }
        }
        foreach (var imp in context.ImportedTypes)
            if (!unit.Imports.Contains(imp))
                unit.Imports.Add(imp);
        return unit;
    }

    private void ProcessNamespace(BaseNamespaceDeclarationSyntax ns, IrCompilationUnit unit)
    {
        _context.EnterNamespace(ns.Name.ToString());
        if (string.IsNullOrEmpty(unit.Package))
            unit.Package = _context.NamespaceToPackage(ns.Name.ToString());
        foreach (var member in ns.Members)
        {
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax nestedNs:
                    ProcessNamespace(nestedNs, unit);
                    break;
                case TypeDeclarationSyntax typeDecl:
                    var type = _typeGen.Generate(typeDecl, _context);
                    if (type != null) unit.TypeDeclarations.Add(type);
                    break;
                case EnumDeclarationSyntax enumDecl:
                    var enm = _typeGen.GenerateEnum(enumDecl, _context);
                    if (enm != null) unit.TypeDeclarations.Add(enm);
                    break;
            }
        }
        _context.LeaveNamespace();
    }
}
