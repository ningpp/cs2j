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

    public string Emit(JavaCompilationUnit unit, ConversionContext context, IReadOnlySet<string>? compatSymbols = null)
    {
        var mappingPath = context.Options.TypeScriptMappingConfigPath;
        var memberPath = context.Options.TypeScriptMemberMappingPath;
        var mappings = new TsMemberMappingTable(mappingPath, memberPath, context.Options.TypeMappingConfigPath);
        var imports = new TsImportCollector(unit.Package);

        // Task 18：rewriter 改写出的 compat 符号（CsString 等）在此登记导入
        //（rewriter 先于发射器运行且无共享实例，符号经 RunTsRewriters → pass 传入）
        if (compatSymbols != null)
        {
            foreach (var symbol in compatSymbols)
            {
                imports.AddNamedImport(symbol, "@csharptotypescript/compat");
            }
        }

        var sb = new StringBuilder();

        // Task 17：收集 [Flags] 枚举名（共享管线已转为静态 int 常量类，按 TypeMapper 登记识别）
        var flagsEnumNames = unit.TypeDeclarations
            .OfType<Java.JavaClassDeclaration>()
            .Where(cls => context.IsFlagsEnum(cls.Name))
            .Select(cls => cls.Name)
            .ToHashSet(StringComparer.Ordinal);

        // 发射类型声明，收集 imports
        var typeBodies = new StringBuilder();
        foreach (var type in unit.TypeDeclarations)
        {
            typeBodies.AppendLine(EmitType(type, mappings, imports, flagsEnumNames));
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
    public string EmitType(JavaTypeDeclaration type, TsMemberMappingTable mappings, TsImportCollector imports,
        IReadOnlySet<string>? flagsEnumNames = null)
    {
        var exprWriter = new TsExpressionWriter(mappings, imports);
        var stmtWriter = new TsStatementWriter(exprWriter);
        var memberWriter = new TsMemberWriter(stmtWriter, exprWriter);
        var typeWriter = new TsTypeWriter(memberWriter, accessorRoles: _accessorRoles, flagsEnumNames: flagsEnumNames);
        return typeWriter.Emit(type, 0);
    }
}
