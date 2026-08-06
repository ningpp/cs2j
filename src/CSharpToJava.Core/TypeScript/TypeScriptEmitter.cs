using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// TypeScript 发射器总入口：编排各 writer 输出 .ts 文本。
/// Task 9 最小版：提供 EmitType 供类型级发射测试；Task 10 补充 Emit 总入口。
/// </summary>
public sealed class TypeScriptEmitter
{
    /// <summary>
    /// 发射单个类型声明（供测试直接调用）。
    /// </summary>
    public string EmitType(JavaTypeDeclaration type, TsMemberMappingTable mappings, TsImportCollector imports)
    {
        var exprWriter = new TsExpressionWriter(mappings, imports);
        var stmtWriter = new TsStatementWriter(exprWriter);
        var memberWriter = new TsMemberWriter(stmtWriter, exprWriter);
        var typeWriter = new TsTypeWriter(memberWriter);
        return typeWriter.Emit(type, 0);
    }
}