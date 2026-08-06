using System.Text;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.TypeScript.Rewriters;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// TypeScript 发射器总入口：遍历 JavaCompilationUnit IR，编排各 writer 输出 .ts 文本。
/// spec §2.5 — IR 节点级发射映射。
/// </summary>
public sealed class TypeScriptEmitter
{
    public const string FileExtension = ".ts";

    private readonly IReadOnlyDictionary<JavaMethodDeclaration, TsAccessorRole>? _accessorRoles;

    public TypeScriptEmitter(IReadOnlyDictionary<JavaMethodDeclaration, TsAccessorRole>? accessorRoles = null)
    {
        _accessorRoles = accessorRoles;
    }

    public string Emit(JavaCompilationUnit unit, ConversionContext context)
    {
        var mappingPath = context.Options.TypeScriptMappingConfigPath;
        var memberPath = context.Options.TypeScriptMemberMappingPath;
        var mappings = new TsMemberMappingTable(mappingPath, memberPath);
        var imports = new TsImportCollector(unit.Package);

        var sb = new StringBuilder();

        // 发射类型声明，收集 imports
        var typeBodies = new StringBuilder();
        foreach (var type in unit.TypeDeclarations)
        {
            typeBodies.AppendLine(EmitType(type, mappings, imports));
            typeBodies.AppendLine();
        }

        // 头部：ESM imports
        var importBlock = imports.EmitImports();
        if (!string.IsNullOrEmpty(importBlock))
        {
            sb.Append(importBlock);
            sb.AppendLine();
        }

        sb.Append(typeBodies);
        return sb.ToString();
    }

    /// <summary>
    /// 发射单个类型声明（供测试直接调用）。
    /// </summary>
    public string EmitType(JavaTypeDeclaration type, TsMemberMappingTable mappings, TsImportCollector imports)
    {
        var exprWriter = new TsExpressionWriter(mappings, imports);
        var stmtWriter = new TsStatementWriter(exprWriter);
        var memberWriter = new TsMemberWriter(stmtWriter, exprWriter);
        var typeWriter = new TsTypeWriter(memberWriter, accessorRoles: _accessorRoles);
        return typeWriter.Emit(type, 0);
    }
}
