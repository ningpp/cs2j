using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers;
using CSharpToJava.Core.Transformers.Type;
using CSharpToJava.Core.Abstractions;

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
            else if (member is EnumDeclarationSyntax enumDecl)
            {
                // EnumDeclarationSyntax is NOT a TypeDeclarationSyntax in Roslyn
                var javaEnum = VisitEnumDeclaration(enumDecl);
                if (javaEnum is JavaTypeDeclaration enumDeclNode)
                {
                    compilation.TypeDeclarations.Add(enumDeclNode);
                }
            }
            else if (member is DelegateDeclarationSyntax delegateDecl)
            {
                var delegateTransformer = _factory.CreateDelegateTransformer();
                var javaDelegate = delegateTransformer.TransformDelegate(delegateDecl, _context);
                if (javaDelegate != null)
                {
                    compilation.TypeDeclarations.Add(javaDelegate);
                }
            }
        }

        // Flush imports accumulated during type-body visitation (fields, methods, parameters).
        // ProcessUsings runs before the members are visited, so any AddImport calls from
        // type transformers are not yet reflected in compilation.Imports at that point.
        foreach (var import in _context.ImportedTypes)
        {
            if (!compilation.Imports.Any(i => i.Name == import))
            {
                compilation.Imports.Add(new JavaImport(import));
            }
        }

        // Emit synthesized records from anonymous type projections as nested types
        // of the last class, or as top-level types if no class exists.
        if (_context.SynthesizedRecords.Count > 0)
        {
            var lastClass = compilation.TypeDeclarations.OfType<JavaClassDeclaration>().LastOrDefault();
            foreach (var rec in _context.SynthesizedRecords)
            {
                var recordDecl = new JavaClassDeclaration
                {
                    Name = rec.RecordName,
                    IsRecord = true,
                    // Nested records are implicitly static; top-level records don't need static
                    Modifiers = lastClass != null
                        ? JavaModifiers.Private | JavaModifiers.Static
                        : JavaModifiers.None,
                };
                foreach (var field in rec.Fields)
                {
                    recordDecl.RecordComponents.Add(new JavaRecordComponent(field.JavaType, field.Name));
                }

                if (lastClass != null)
                {
                    lastClass.NestedTypes.Add(recordDecl);
                }
                else
                {
                    compilation.TypeDeclarations.Add(recordDecl);
                }
            }
            _context.ClearSynthesizedRecords();
        }

        return compilation;
    }

    /// <summary>
    /// 处理 using 语句
    /// </summary>
    private void ProcessUsings(SyntaxList<UsingDirectiveSyntax> usings, JavaCompilationUnit compilation)
    {
        // 清除之前的别名（每个文件独立）
        _context.ClearAliases();

        foreach (var usingDirective in usings)
        {
            ProcessUsingDirective(usingDirective, compilation);
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

    /// <summary>
    /// 处理单个 using 指令
    /// </summary>
    private void ProcessUsingDirective(UsingDirectiveSyntax usingDirective, JavaCompilationUnit compilation)
    {
        if (usingDirective.Name == null) return;

        // 处理别名：using P2 = Core.Geometry.Point;
        if (usingDirective.Alias != null)
        {
            ProcessUsingAlias(usingDirective);
            return;
        }

        var name = usingDirective.Name.ToString();

        // 静态导入转换：using static System.Math;
        if (usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
        {
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

    /// <summary>
    /// 处理 using 别名
    /// </summary>
    private void ProcessUsingAlias(UsingDirectiveSyntax usingDirective)
    {
        if (usingDirective.Alias == null || usingDirective.Name == null) return;

        var aliasName = usingDirective.Alias.Name.Identifier.Text;
        var location = usingDirective.Alias.Name.GetLocation();

        // 获取语义信息
        var semanticModel = _context.GetSemanticModelForTree(usingDirective.SyntaxTree);
        if (semanticModel == null) return;

        // 获取别名指向的符号
        var symbolInfo = semanticModel.GetSymbolInfo(usingDirective.Name);
        var aliasSymbol = symbolInfo.Symbol;

        // 如果直接获取失败，尝试通过别名符号获取
        if (aliasSymbol == null && symbolInfo.CandidateSymbols.Length > 0)
        {
            aliasSymbol = symbolInfo.CandidateSymbols[0];
        }

        if (aliasSymbol == null)
        {
            _context.Diagnostics.Warning(
                $"Could not resolve alias '{aliasName}' to a type",
                location
            );
            return;
        }

        // 处理 IAliasSymbol（Roslyn 的别名符号）
        ITypeSymbol? typeSymbol = null;
        if (aliasSymbol is IAliasSymbol aliasSym)
        {
            typeSymbol = aliasSym.Target as ITypeSymbol;
        }
        else if (aliasSymbol is ITypeSymbol ts)
        {
            typeSymbol = ts;
        }

        if (typeSymbol == null)
        {
            _context.Diagnostics.Error(
                $"Alias '{aliasName}' must refer to a type, not a {aliasSymbol.Kind}",
                location
            );
            return;
        }

        // 注册别名
        if (_context.RegisterUsingAlias(aliasName, typeSymbol, location))
        {
            // 为目标类型添加必要的导入
            var javaType = _context.MapType(typeSymbol);

            // 从完全限定类型名提取包名（如果需要）
            var lastDotIndex = javaType.LastIndexOf('.');
            if (lastDotIndex > 0)
            {
                var packageName = javaType.Substring(0, lastDotIndex);

                // 检查是否需要添加导入（排除 java.lang）
                if (!packageName.StartsWith("java.lang"))
                {
                    _context.AddImport(javaType);
                }
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

        // Preserve custom namespace imports so cross-project symbols remain resolvable
        // after conversion to Java modules.
        var mappedNamespace = _context.NamespaceToPackage(csharpUsing);
        if (string.IsNullOrWhiteSpace(mappedNamespace))
        {
            return null;
        }

        if (mappedNamespace.EndsWith(".*", StringComparison.Ordinal))
        {
            return mappedNamespace;
        }

        return mappedNamespace + ".*";
    }

    /// <summary>
    /// 处理命名空间
    /// </summary>
    private void ProcessNamespace(NamespaceDeclarationSyntax namespaceDecl, JavaCompilationUnit compilation)
    {
        var ns = namespaceDecl.Name.ToString();

        _context.EnterNamespace(ns);

        if (namespaceDecl.Usings.Count > 0)
        {
            ProcessUsings(namespaceDecl.Usings, compilation);
        }

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
            else if (member is EnumDeclarationSyntax enumDecl)
            {
                // EnumDeclarationSyntax is NOT a TypeDeclarationSyntax in Roslyn
                var javaEnum = VisitEnumDeclaration(enumDecl);
                if (javaEnum is JavaTypeDeclaration enumDeclNode)
                {
                    compilation.TypeDeclarations.Add(enumDeclNode);
                }
            }
            else if (member is DelegateDeclarationSyntax delegateDecl)
            {
                var delegateTransformer = _factory.CreateDelegateTransformer();
                var javaDelegate = delegateTransformer.TransformDelegate(delegateDecl, _context);
                if (javaDelegate != null)
                {
                    compilation.TypeDeclarations.Add(javaDelegate);
                }
            }
        }

        _context.LeaveNamespace();
    }

    /// <summary>
    /// 访问类型声明（类、接口、结构体、枚举等）
    /// </summary>
    public virtual JavaSyntaxNode? VisitTypeDeclaration(TypeDeclarationSyntax node)
    {
        ITypeTransformer? transformer = node.Kind() switch
        {
            SyntaxKind.ClassDeclaration => _factory.CreateClassTransformer(),
            SyntaxKind.InterfaceDeclaration => _factory.CreateInterfaceTransformer(),
            SyntaxKind.StructDeclaration => _factory.CreateStructTransformer(),
            SyntaxKind.EnumDeclaration => _factory.CreateEnumTransformer(),
            SyntaxKind.RecordDeclaration => _factory.CreateRecordTransformer(),
            SyntaxKind.RecordStructDeclaration => _factory.CreateRecordTransformer(),
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
        var transformer = new Transformers.Type.EnumTransformer();
        return transformer.TransformEnum(node, _context);
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
    public virtual JavaSyntaxNode? VisitStatement(StatementSyntax node)
    {
        var transformer = _factory.CreateStatementTransformer();
        return transformer.Transform(node, _context);
    }

    /// <summary>
    /// 访问表达式
    /// </summary>
    public virtual JavaSyntaxNode? VisitExpression(ExpressionSyntax node)
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
