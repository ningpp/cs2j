using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// TS 专属 IR rewriter 装配与发射辅助（Task 38：单文件 SingleFileTsEmitPass 与
/// 项目级 ProjectTsEmitPass 共享同一套装配，保证两类路径行为一致）。
/// 运行 TS 专属 IR rewriter（Java 路径不进入）：属性访问器重组、枚举调用点、
/// 字符串调用、构造器工厂、泛型擦除收尾、接口探针、struct 发射 TS 化、
/// ref/out Holder、record 物化、yield generator、async/await；汇总 compat 符号
/// （发射器据此生成 @csharptotypescript/compat 导入）后由 TypeScriptEmitter 发射。
/// </summary>
/// <summary>
/// 语法树层 rewriter 的多文件预收集协议（Task 38）：项目模式下先对全部文件的根
/// 调用 Precollect（构建跨文件一致的改名表/工厂需求），再逐文件 Visit 改写。
/// 单文件模式无需调用，VisitCompilationUnit 内部自动收集。
/// </summary>
public interface ITsSyntaxPrecollector
{
void Precollect(CompilationUnitSyntax root);
}


public static class TsEmitSupport
{
/// <summary>对已生成的 Java IR 运行 TS 专属 rewriter 并发射 .ts 代码。</summary>
    public static string EmitTypeScript(Java.JavaCompilationUnit compilation, ConversionContext context)
    {
        var (accessorRoles, compatSymbols) = RunTsRewriters(compilation, context);
        var emitter = new TypeScriptEmitter(accessorRoles);
        return emitter.Emit(compilation, context, compatSymbols);
    }

    /// <summary>
    /// 运行 TS 专属 IR rewriter（Java 路径不进入本 pass）。Task 16：属性访问器重组
    /// （类自动属性 getX/setX → TS 字段/访问器 + 调用点改写）；返回角色侧表供
    /// TypeScriptEmitter 发射访问器使用，以及 rewriter 登记的 compat 符号
    /// （Task 18：CsString 等，发射器据此生成 @csharptotypescript/compat 导入）。
    /// M3 在此追加其余 rewriter。
    /// </summary>
    public static (IReadOnlyDictionary<Java.JavaMethodDeclaration, Rewriters.TsAccessorRole>? Roles,
        IReadOnlySet<string> CompatSymbols) RunTsRewriters(
        Java.JavaCompilationUnit compilation, ConversionContext context)
    {
        // TsOverloadDisambiguationRewriter, TsGenericErasureRewriter,
        // TsInterfaceProbeRewriter, TsEqualityRewriter (M3)
        var propertyRewriter = new Rewriters.TsPropertyReconstructionRewriter();
        propertyRewriter.VisitCompilationUnit(compilation);
        // Task 17：枚举转换调用点（getValue/fromValue/ordinal）改写 + Enum.Parse/ToString 诊断
        var enumRewriter = new Rewriters.TsEnumCallRewriter(context);
        enumRewriter.VisitCompilationUnit(compilation);
        // Task 18：字符串调用改写（String.format/StringHelper.formatCs/String.valueOf/
        // StringBuilder 链 → CsString.format/String(...)/模板字符串）
        var stringRewriter = new Rewriters.TsStringCallRewriter(context);
        stringRewriter.VisitCompilationUnit(compilation);
        // Task 23：构造器重载 → Create$N 工厂（IR 层合成；调用点已在语法层改写为 Type.Create$N(...)）
        var ctorFactoryRewriter = new Rewriters.TsCtorFactoryRewriter(context);
        ctorFactoryRewriter.VisitCompilationUnit(compilation);
        // Task 24：泛型擦除收尾（类字面量/类型参数强转/lambda 箭头/工厂参数类型与调用改写）
        var genericErasureRewriter = new Rewriters.TsGenericErasureIrRewriter(context);
        genericErasureRewriter.VisitCompilationUnit(compilation);
        // Task 25：接口运行时探针（is$IXxx 生成 + is/as 调用点改写 + 实现类构造器注入标记）
        var interfaceProbeRewriter = new Rewriters.TsInterfaceProbeRewriter(context);
        interfaceProbeRewriter.VisitCompilationUnit(compilation);
        // Task 27：struct 发射 TS 化（implements Cloneable 移除、clone()/合成 equals/hashCode
        // 方法体 → 合法 TS；值拷贝语义辅助 cloneStruct 归 M4）
        var structRewriter = new Rewriters.TsStructRewriter(context);
        structRewriter.VisitCompilationUnit(compilation);
        // Task 27：ref/out Holder 调用点 TS 化（Ref<T> + refOf/outRef，spec §4.3.1；
        // 参数类型映射在 TsMemberMappingTable.Holder 分支）
        var refOutRewriter = new Rewriters.TsRefOutRewriter(context);
        refOutRewriter.VisitCompilationUnit(compilation);
        // Task 27：record 类物化（CsRecord 基类 + 分量字段/getter/构造器/recordFields）
        var recordRewriter = new Rewriters.TsRecordRewriter(context);
        recordRewriter.VisitCompilationUnit(compilation);
        // Task 27：yield 方法/属性 getter → generator（function* + IterableIterator<T>，
        // spec §1.4.4；需要属性重构的 accessor 角色表区分 getter 与普通方法）
        var yieldRewriter = new Rewriters.TsYieldRewriter(context, propertyRewriter.Roles);
        yieldRewriter.VisitCompilationUnit(compilation);
        // Task 27：async/await → Promise + 原生 await（spec §1.4.5；join→await、
        // completedFuture→Promise.resolve、WhenAll→Promise.all、Task.Run 降级同步 + 诊断）
        var asyncRewriter = new Rewriters.TsAsyncRewriter(context);
        asyncRewriter.VisitCompilationUnit(compilation);
        // Task 39：测试类 Assert.* → vitest expect 链 + this. 剥离（测试类 describe/it
        // 结构在发射器侧处理）。在全部 rewriter 之后运行（改写后的调用形态已定型）。
        var testRewriter = new Rewriters.TsTestAssertRewriter(context);
        testRewriter.VisitCompilationUnit(compilation);
        var compatSymbols = new HashSet<string>(stringRewriter.CompatSymbols, StringComparer.Ordinal);
        compatSymbols.UnionWith(genericErasureRewriter.CompatSymbols);
        // Task 24：语法层泛型擦除 rewriter 登记的 compat 符号（TypeToken/csDefault）
        compatSymbols.UnionWith(context.TsCompatSymbols);
        return (propertyRewriter.Roles, compatSymbols);
    }
}
