using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers;
using CSharpToJava.Core.Transformers.Type;

namespace CSharpToJava.Core.Visitors;

/// <summary>
/// C# 到 Java 的语法访问者 - 核心转换入口
/// </summary>
public class CSharpToJavaVisitor : CSharpSyntaxVisitor<JavaSyntaxNode?>
{
    private readonly ConversionContext _context;
    private readonly Transformers.TransformerFactory _factory;

    public CSharpToJavaVisitor(ConversionContext context)
    {
        _context = context;
        _factory = new Transformers.TransformerFactory();
    }

    /// <summary>
    /// 访问编译单元（整个文件）
    /// </summary>
    public override JavaSyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        var compilation = new JavaCompilationUnit();

        // 处理 using 语句
        ProcessUsings(node.Usings, compilation);

        // 处理命名空间声明
        foreach (var member in node.Members)
        {
            if (member is NamespaceDeclarationSyntax namespaceDecl)
            {
                ProcessNamespace(namespaceDecl, compilation);
            }
            else if (member is TypeDeclarationSyntax typeDecl)
            {
                var javaType = VisitTypeDeclaration(typeDecl);
                if (javaType is JavaTypeDeclaration typeDeclNode)
                {
                    compilation.TypeDeclarations.Add(typeDeclNode);
                }
            }
        }

        return compilation;
    }

    /// <summary>
    /// 处理 using 语句
    /// </summary>
    private void ProcessUsings(SyntaxList<UsingDirectiveSyntax> usings, JavaCompilationUnit compilation)
    {
        foreach (var usingDirective in usings)
        {
            if (usingDirective.Name != null)
            {
                var name = usingDirective.Name.ToString();

                // 静态导入转换
                if (usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
                {
                    // 处理静态导入，如 using static System.Math;
                    var javaType = MapUsingToJava(name);
                    if (!string.IsNullOrEmpty(javaType))
                    {
                        compilation.Imports.Add(new JavaImport(javaType, isStatic: true));
                    }
                }
                else
                {
                    // 处理普通导入
                    var javaType = MapUsingToJava(name);
                    if (!string.IsNullOrEmpty(javaType))
                    {
                        compilation.Imports.Add(new JavaImport(javaType));
                    }
                }
            }
        }

        // 添加类型映射所需的导入
        foreach (var import in _context.ImportedTypes)
        {
            if (!compilation.Imports.Any(i => i.Name == import))
            {
                compilation.Imports.Add(new JavaImport(import));
            }
        }
    }

    private string? MapUsingToJava(string csharpUsing)
    {
        // System 命名空间映射
        if (csharpUsing.StartsWith("System."))
        {
            return csharpUsing switch
            {
                "System.Collections.Generic" => null, // 显式导入
                "System.Linq" => null, // Java Stream API 隐式可用
                "System.Threading.Tasks" => null, // CompletableFuture 需要显式导入
                "System" => "java.lang",
                _ => null
            };
        }

        return null;
    }

    /// <summary>
    /// 处理命名空间
    /// </summary>
    private void ProcessNamespace(NamespaceDeclarationSyntax namespaceDecl, JavaCompilationUnit compilation)
    {
        var ns = namespaceDecl.Name.ToString();

        _context.EnterNamespace(ns);

        // 设置包名
        if (string.IsNullOrEmpty(compilation.Package))
        {
            compilation.Package = _context.NamespaceToPackage(ns);
        }

        // 处理命名空间中的成员
        foreach (var member in namespaceDecl.Members)
        {
            if (member is TypeDeclarationSyntax typeDecl)
            {
                var javaType = VisitTypeDeclaration(typeDecl);
                if (javaType is JavaTypeDeclaration typeDeclNode)
                {
                    compilation.TypeDeclarations.Add(typeDeclNode);
                }
            }
        }

        _context.LeaveNamespace();
    }

    /// <summary>
    /// 访问类型声明（类、接口、结构体、枚举等）
    /// </summary>
    public override JavaSyntaxNode? VisitTypeDeclaration(TypeDeclarationSyntax node)
    {
        ITypeTransformer? transformer = node.Kind() switch
        {
            SyntaxKind.ClassDeclaration => _factory.CreateClassTransformer(),
            SyntaxKind.InterfaceDeclaration => _factory.CreateInterfaceTransformer(),
            SyntaxKind.StructDeclaration => _factory.CreateStructTransformer(),
            SyntaxKind.EnumDeclaration => _factory.CreateEnumTransformer(),
            SyntaxKind.RecordDeclaration => _factory.CreateRecordTransformer(),
            _ => null
        };

        if (transformer != null)
        {
            return transformer.Transform(node, _context);
        }

        _context.Diagnostics.Warning($"Unsupported type declaration: {node.Kind()}", node.GetLocation());
        return null;
    }

    /// <summary>
    /// 访问类声明
    /// </summary>
    public override JavaSyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        return VisitTypeDeclaration(node);
    }

    /// <summary>
    /// 访问接口声明
    /// </summary>
    public override JavaSyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        return VisitTypeDeclaration(node);
    }

    /// <summary>
    /// 访问结构体声明
    /// </summary>
    public override JavaSyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        return VisitTypeDeclaration(node);
    }

    /// <summary>
    /// 访问枚举声明
    /// </summary>
    public override JavaSyntaxNode? VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        return VisitTypeDeclaration(node);
    }

    /// <summary>
    /// 访问记录声明
    /// </summary>
    public override JavaSyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        return VisitTypeDeclaration(node);
    }

    /// <summary>
    /// 访问方法声明
    /// </summary>
    public override JavaSyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var transformer = _factory.CreateMethodTransformer();
        return transformer.Transform(node, _context);
    }

    /// <summary>
    /// 访问字段声明
    /// </summary>
    public override JavaSyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        var transformer = _factory.CreateFieldTransformer();
        return transformer.Transform(node, _context);
    }

    /// <summary>
    /// 访问属性声明
    /// </summary>
    public override JavaSyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var transformer = _factory.CreatePropertyTransformer();
        return transformer.Transform(node, _context);
    }

    /// <summary>
    /// 访问语句
    /// </summary>
    public override JavaSyntaxNode? VisitStatement(StatementSyntax node)
    {
        var transformer = _factory.CreateStatementTransformer();
        return transformer.Transform(node, _context);
    }

    /// <summary>
    /// 访问表达式
    /// </summary>
    public override JavaSyntaxNode? VisitExpression(ExpressionSyntax node)
    {
        var transformer = _factory.CreateExpressionTransformer();
        var result = transformer.Transform(node, _context);
        return new JavaExpressionNode(result);
    }

    /// <summary>
    /// 访问成员声明
    /// </summary>
    public override JavaSyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var transformer = _factory.CreateExpressionTransformer();
        var result = transformer.Transform(node, _context);
        return new JavaExpressionNode(result);
    }
}

/// <summary>
/// Java 表达式节点包装器
/// </summary>
internal class JavaExpressionNode : JavaSyntaxNode
{
    public string Expression { get; }

    public JavaExpressionNode(string expression)
    {
        Expression = expression;
    }

    public override string ToString(string indentation)
    {
        return Expression;
    }
}
