using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// C06 unsafe-ffm 预处理 pass 公共门面（C#→C#，设计 §4）。单次 parse：
/// 直接使用调用方已解析树的 <see cref="SemanticModel"/>，规则在内存中顺序
/// 合批：R1 <see cref="UnsafeContextStripper"/> 始终先跑；随后 M4 的 pass
/// 基座规则 R7 <see cref="RefStructDeclRewriter"/>（用户 ref struct 声明
/// 处置：ref-only 者 UF050 拒绝、纯数据型 UF052 去 ref 降级）、
/// <see cref="InlineArrayDetector"/>（UF051）、R9
/// <see cref="RefStructUsingRewriter"/>（Span using 空操作归一/UF055）
/// 随 pass 生效；M1 起 EnableFixed 时同树合批 R2
/// <see cref="FixedBufferFieldRewriter"/> → R3
/// <see cref="FixedStatementRewriter"/>（字段先改数组，使 fixed(byte* p=字段)
/// 走数组路径），随后做 fixed 段 residual 自检；M2 起 EnableStackAlloc 时
/// R4 <see cref="StackAllocRewriter"/> 在 R3 之后合批并做 stackalloc 段
/// residual 自检；M3 起 EnablePointer 时 R5 签名白名单 → R6
/// <see cref="PointerExprRewriter"/> 表达式索引化；M4 起 EnableExtern 时
/// R8 <see cref="ExternInteropRewriter"/> 处理 extern 互操作边界
/// （UF060/UF070，opt-in 白名单命中时归一 InteropFacade）；末端 D14 residual 闸门按段扩展。
/// 拒绝优先：任一 Error 即文件 <see cref="UnsafeFfmResult.Changed"/>=false、
/// 原文保留；输出可被自身二次安全驱动（幂等）。
/// </summary>
public sealed class UnsafeFfmPreprocessor
{
    private readonly UnsafeFfmOptions _options;

    /// <summary>
    /// R8 白名单归一跨文件状态：已注入 InteropFacade 支撑标记类型的
    /// 程序集集合（一个 RewriteFile 序列内同一程序集只注入一次）。
    /// </summary>
    private readonly HashSet<string> _facadeSupportEmittedAssemblies =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 编译级配对计划 catalog（一轮 RewriteFile 序列共享）及其所属编译。
    /// stackalloc 段开启时惰性引导：全树 prescan → 回滚树集合不动点 →
    /// ResetRound + ComputeClosure，保证跨文件声明点/调用点结论一致。
    /// </summary>
    private PointerMethodCatalog? _sessionCatalog;
    private Compilation? _sessionCompilation;

    public UnsafeFfmPreprocessor(UnsafeFfmOptions? options = null)
    {
        _options = options ?? new UnsafeFfmOptions();
    }

    /// <summary>
    /// 取本轮 catalog。stackalloc 降级规则段（连同 fixed/pointer 段）开启时
    /// 返回编译级共享 catalog（首次调用做全树预检引导）；其余开关组合维持
    /// 逐文件新建 catalog 的既有行为。
    /// </summary>
    private PointerMethodCatalog GetCatalog(SemanticModel semanticModel)
    {
        var compilation = semanticModel.Compilation;
        if (_sessionCatalog != null
            && ReferenceEquals(_sessionCompilation, compilation))
        {
            return _sessionCatalog;
        }

        // 非全段开启：保持逐文件 catalog 的历史行为，不扰动 UF020 路径。
        if (!_options.EnableStackAlloc
            || !_options.EnableFixed
            || !_options.EnablePointer)
        {
            return new PointerMethodCatalog(compilation);
        }

        var catalog = BootstrapSessionCatalog(compilation);
        _sessionCatalog = catalog;
        _sessionCompilation = compilation;
        return catalog;
    }

    /// <summary>
    /// 全树预检引导：对编译内全部语法树扫描裸指针 stackalloc 站点，迭代
    /// 「回滚树集合 → catalog 重置闭包 → 逐站点可降级判定」至不动点。
    /// 回滚树（prescan 拒绝）内声明的方法不参与配对闭包，活动树发往其的
    /// 强制边被 catalog 忽略，保证落盘产物跨文件签名一致。
    /// </summary>
    private PointerMethodCatalog BootstrapSessionCatalog(Compilation compilation)
    {
        var catalog = new PointerMethodCatalog(compilation);

        // 与 catalog 闭包无关的一次性分类：每棵树的裸指针站点，以及 Span
        // 目标元素不可安全堆化的整文拒绝（prescan 两类拒绝之一）。
        var pointerSites =
            new Dictionary<SyntaxTree, List<(ExpressionSyntax Node,
                VariableDeclaratorSyntax? Declarator)>>();
        var elementRejectedTrees = new HashSet<SyntaxTree>();

        // 含指针/函数指针类型语法的语法树：闭包候选方法必然以 T* 形参声明
        // （声明即 PointerTypeSyntax；ByRef<T*> holder 形参的类型实参亦含），
        // 故 ComputeClosure 只需在这些树上找候选——无该语法的树必无候选。
        // 与 prescan 同一次整树结构扫描内一并判定，避免再次整树遍历。
        var pointerTypeTrees = new HashSet<SyntaxTree>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            CompilationUnitSyntax root;
            try
            {
                root = (CompilationUnitSyntax)tree.GetRoot();
            }
            catch
            {
                continue;
            }

            List<(ExpressionSyntax, VariableDeclaratorSyntax?)>? sites = null;
            var hasPointerType = false;
            SemanticModel? model = null;

            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case StackAllocArrayCreationExpressionSyntax explicitNode:
                        // 语义模型按需惰性创建：只有真正含 stackalloc 的语法树
                        // 才需要绑定（含 stackalloc 的树极少），避免为全编译每
                        // 棵树创建 SemanticModel 的一次性开销。
                        if (!TryGetModel(compilation, tree, ref model)) break;

                        if (StackAllocElementSafety.GetSiteType(model!, explicitNode)
                            is IPointerTypeSymbol)
                        {
                            (sites ??= new List<(ExpressionSyntax, VariableDeclaratorSyntax?)>())
                                .Add((explicitNode, FindPointerSeedDeclarator(explicitNode)));
                            break;
                        }

                        var explicitElement = StackAllocElementSafety.GetExplicitElement(model!, explicitNode);
                        if (explicitElement == null
                            || !StackAllocElementSafety.IsSafeElement(explicitElement, out _))
                        {
                            elementRejectedTrees.Add(tree);
                        }

                        break;

                    case ImplicitStackAllocArrayCreationExpressionSyntax implicitNode:
                        if (!TryGetModel(compilation, tree, ref model)) break;

                        if (StackAllocElementSafety.GetSiteType(model!, implicitNode)
                            is IPointerTypeSymbol)
                        {
                            (sites ??= new List<(ExpressionSyntax, VariableDeclaratorSyntax?)>())
                                .Add((implicitNode, FindPointerSeedDeclarator(implicitNode)));
                            break;
                        }

                        var implicitElement = StackAllocElementSafety.GetImplicitElement(model!, implicitNode);
                        if (implicitElement == null
                            || !StackAllocElementSafety.IsSafeElement(implicitElement, out _))
                        {
                            elementRejectedTrees.Add(tree);
                        }

                        break;

                    case PointerTypeSyntax or FunctionPointerTypeSyntax:
                        hasPointerType = true;
                        break;
                }
            }

            if (sites != null) pointerSites[tree] = sites;
            if (hasPointerType) pointerTypeTrees.Add(tree);
        }

        var disabled = new HashSet<SyntaxTree>(elementRejectedTrees);
        // 闭包候选只在含指针类型语法的树内（见 pointerTypeTrees）；无该语法的
        // 树不含白名单指针形参方法，逐树整方法枚举可整树跳过。
        var closureTrees = pointerTypeTrees.ToList();

        // 回滚树集合不动点：回滚树外发边剔除后闭包只会收缩，可能令更多
        // stackalloc 站点失去配对被调方而不可降级 → 其整树加入回滚集。
        while (true)
        {
            catalog.ResetRound(disabled);

            // 先统一登记活动树全部种子，再预取种子所属方法/局部函数计划，
            // 令信号⑦沿调用图（含跨文件）传播。
            var seedDeclarators =
                new List<(SyntaxTree Tree, VariableDeclaratorSyntax Declarator)>();
            foreach (var (tree, sites) in pointerSites)
            {
                if (disabled.Contains(tree)) continue;
                var model = compilation.GetSemanticModel(tree);
                foreach (var (_, declarator) in sites)
                {
                    if (declarator == null) continue;
                    if (model.GetDeclaredSymbol(declarator)
                        is ILocalSymbol { Type: IPointerTypeSymbol })
                    {
                        seedDeclarators.Add((tree, declarator));
                    }
                }
            }

            catalog.RegisterStackAllocCursorRoots(seedDeclarators
                .Select(s =>
                    compilation.GetSemanticModel(s.Tree)
                        .GetDeclaredSymbol(s.Declarator))
                .Where(s => s != null)!);

            foreach (var (tree, declarator) in seedDeclarators)
            {
                var model = compilation.GetSemanticModel(tree);

                var enclosingLocalFunction = declarator
                    .FirstAncestorOrSelf<LocalFunctionStatementSyntax>();
                if (enclosingLocalFunction != null
                    && model.GetDeclaredSymbol(enclosingLocalFunction)
                        is IMethodSymbol localFunction)
                {
                    catalog.GetPlan(localFunction);
                }

                var enclosingMethod = declarator
                    .FirstAncestorOrSelf<BaseMethodDeclarationSyntax>();
                if (enclosingMethod != null
                    && model.GetDeclaredSymbol(enclosingMethod)
                        is IMethodSymbol method)
                {
                    catalog.GetPlan(method);
                }
            }

            catalog.ComputeClosure(closureTrees.Where(t => !disabled.Contains(t)));

            var newlyDisabled = new HashSet<SyntaxTree>();
            foreach (var (tree, sites) in pointerSites)
            {
                if (disabled.Contains(tree)) continue;
                var model = compilation.GetSemanticModel(tree);

                foreach (var (_, declarator) in sites)
                {
                    if (declarator == null)
                    {
                        newlyDisabled.Add(tree);
                        break;
                    }

                    if (!StackAllocPointerSafety.IsLowerable(
                            declarator, model, catalog,
                            out _, out _))
                    {
                        newlyDisabled.Add(tree);
                        break;
                    }
                }
            }

            if (newlyDisabled.Count == 0) break;
            disabled.UnionWith(newlyDisabled);
        }

        return catalog;
    }

    /// <summary>
    /// 惰性取语法树的语义模型（含 stackalloc 的树才需要）；创建失败返回 false
    /// （与旧口径一致：该树不再参与 prescan）。
    /// </summary>
    private static bool TryGetModel(
        Compilation compilation, SyntaxTree tree, ref SemanticModel? model)
    {
        if (model != null) return true;
        try
        {
            model = compilation.GetSemanticModel(tree);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 改写单个文件。语义模型由调用方在整树编译（allowUnsafe）上一次性提供。
    /// </summary>
    public UnsafeFfmResult RewriteFile(string sourceCode, SemanticModel semanticModel)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);

        var statistics = new UnsafeFfmStatistics();
        var diagnostics = new List<UnsafeFfmDiagnostic>();
        statistics.FilesScanned++;

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;

        // cs2j 生成的支撑/登记产物（顶层命名约定 _Cs2j*.cs：tuple 替换结构、
        // closed-metadata 登记等）是按精确 C# 语义预置的辅助文件，不属于用户
        // unsafe 面。本 pass 无任何规则作用于它们，末端 D14 残留闸门对这类文件
        // 内嵌的指针语法（如 closed 登记里的 typeof(泛型指针实参)）注定误报且
        // 无法由本 pass 改写。与 make-readonly 跳过 _Cs2j* 支撑文件、
        // closed-metadata 残差扫描跳过自身登记文件的既有口径一致：整文原样放行。
        if (IsCs2jGeneratedSupportFile(filePath))
        {
            statistics.FilesSkippedClean++;
            return Rollback(sourceCode, statistics, diagnostics);
        }

        // 语法预筛（零语义查询，只读语法树）：本文件是否可能含 R2/R3/R5/R6
        // 所作用的指针语法（指针/函数指针类型，或 unsafe/fixed/stackalloc
        // 关键字），以及 R8 所作用的 extern/DllImport/LibraryImport/Marshal/
        // NativeLibrary/SafeHandle/HandleRef 站点。指针簇规则的全部触发点都
        // 必须落在这些语法形态上（指针符号的声明即含指针类型语法，配对/holder
        // 亦含指针类型实参），故「无该语法 → 无改写、无诊断、无统计」；整簇
        // 跳过与逐规则空跑输出逐字节一致，却省下各 rewriter 的整树 Visit 与
        // 语义绑定（本 pass 最重的 R6/R3/R5/R8 正是全树空遍历的开销）。
        // 预筛是单次结构扫描，远廉价于被跳过的整树改写。
        var pointerCluster = HasPointerClusterSyntax(root, out var hasStackAlloc);
        var externInterop = HasExternInteropSyntax(root);

        // 配对计划 catalog 提前到 prescan 之前创建：裸指针 stackalloc 可降级
        // 判定（StackAllocPointerSafety）依赖信号⑦强制配对闭包，后者由
        // GetPlan（含 stackalloc 站点的方法）经 catalog 传播；同一 catalog
        // 贯穿 R5/R3/R6，声明点与调用点结论一致。stackalloc 规则段开启时
        // catalog 提升为「单次编译（一轮 RewriteFile 序列）」级：所有文件
        // 改写前先做全树预检与闭包不动点，避免逐文件新建 catalog 造成跨文件
        // 强制结论不一致（声明点 array-only、调用点已配对 → 签名错配）。
        var pointerMethodCatalog = GetCatalog(semanticModel);
        var lowerableStackAllocLocals =
            new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var zeroInvariantStackAllocLocals =
            new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        // M2 拒绝预检（拒绝优先，先于 R1/R2/R3）：stackalloc 站点为裸指针
        // （含 var）且使用可证留在可索引域时不拒绝——交 R3 降级为「伴生堆
        // 数组 + 同名 int 索引」；不可证明不外泄或元素不可安全堆化时直接
        // UF040，避免 R3 先以 UF020 拦截同一构造。
        if (_options.EnableStackAlloc && hasStackAlloc && TryRejectStackAllocSites(
                root, semanticModel, filePath, pointerMethodCatalog, statistics,
                out var prescanDiagnostics,
                lowerableStackAllocLocals, zeroInvariantStackAllocLocals))
        {
            return Rollback(sourceCode, statistics, prescanDiagnostics);
        }

        // R1 始终先跑（unsafe 基座，无独立开关）。
        var newRoot = (CompilationUnitSyntax)new UnsafeContextStripper(statistics).Visit(root)!;

        // R1 残留自检：unsafe 块/unsafe 修饰符残留即规则缺口，UF010 拒绝。
        if (HasUnsafeContextResidual(newRoot))
        {
            statistics.ResidualCount++;
            statistics.Rejections++;
            diagnostics.Add(new UnsafeFfmDiagnostic(
                UnsafeFfmSeverity.Error,
                UnsafeFfmDiagnosticCode.UF010,
                "unsafe-context residual gate: the rewritten tree still contains an unsafe block " +
                "or unsafe modifier after R1 (internal rule gap); the original file was preserved.",
                filePath, 0, 0));
            return Rollback(sourceCode, statistics, diagnostics);
        }

        // R7 + InlineArray + R9（M4 pass 基座，随 pass 生效，与 R1 同级
        // 语义；不挂 EnableExtern）。声明判定先于 using 站点：同文件内
        // 非白名单 ref struct 声明先处置（带 ref-only 成员者 UF050 拒绝、
        // 纯数据型 UF052 去 ref 降级为普通 struct）。
        var refStructResolver = new OriginalSyntaxResolver(root, newRoot);
        var refStructRewriter = new RefStructDeclRewriter(
            semanticModel, statistics, refStructResolver);
        newRoot = (CompilationUnitSyntax)refStructRewriter.Visit(newRoot)!;
        foreach (var diagnostic in refStructRewriter.Diagnostics)
        {
            diagnostics.Add(diagnostic);
        }

        if (refStructRewriter.HasErrors)
        {
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var inlineResolver = new OriginalSyntaxResolver(root, newRoot);
        var inlineDetector = new InlineArrayDetector(
            semanticModel, statistics, inlineResolver);
        newRoot = (CompilationUnitSyntax)inlineDetector.Visit(newRoot)!;
        foreach (var diagnostic in inlineDetector.Diagnostics)
        {
            diagnostics.Add(diagnostic);
        }

        if (inlineDetector.HasErrors)
        {
            return Rollback(sourceCode, statistics, diagnostics);
        }

        var usingResolver = new OriginalSyntaxResolver(root, newRoot);
        var usingRewriter = new RefStructUsingRewriter(
            semanticModel, statistics, usingResolver);
        newRoot = (CompilationUnitSyntax)usingRewriter.Visit(newRoot)!;
        foreach (var diagnostic in usingRewriter.Diagnostics)
        {
            diagnostics.Add(diagnostic);
        }

        if (usingRewriter.HasErrors)
        {
            return Rollback(sourceCode, statistics, diagnostics);
        }

        // R5 指针签名白名单（M3a）：链位在 R1 之后、R2 之前。probe 实测
        // CS8385 证明参数必须先由 byte* 改为 byte[]，R3 的 fixed 才能走数组路径。
        var flowModel = new PointerFlowModel();
        var pointerRewritten = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var resourceFields = new HashSet<IFieldSymbol>(SymbolEqualityComparer.Default);
        var loweredPointerSymbols = Array.Empty<ISymbol>();
        IReadOnlyDictionary<ISymbol, (string ArrayName, bool ReadOnly, string SourceSlot)>
            loweredArrayMap =
            new Dictionary<ISymbol, (string, bool, string)>(SymbolEqualityComparer.Default);
        IReadOnlyDictionary<IMethodSymbol, string> pointerMethodRenames =
            new Dictionary<IMethodSymbol, string>(SymbolEqualityComparer.Default);

        if (_options.EnablePointer && pointerCluster)
        {
            var signatureResolver = new OriginalSyntaxResolver(root, newRoot);
            var signatureRewriter = new PointerSignatureRewriter(
                semanticModel,
                resourceFields,
                flowModel,
                statistics,
                signatureResolver,
                pointerMethodCatalog);
            newRoot = (CompilationUnitSyntax)signatureRewriter.Visit(newRoot)!;
            foreach (var diagnostic in signatureRewriter.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }

            if (signatureRewriter.HasErrors)
            {
                return Rollback(sourceCode, statistics, diagnostics);
            }

            foreach (var symbol in signatureRewriter.RewrittenSymbols)
            {
                pointerRewritten.Add(symbol);
            }

            // R5 配对展开与既有数组重载撞签名（CS0111）时改名指针方法，并记录
            // 「原始符号 → 新名」供 R6 调用点同步改名。
            (newRoot, pointerMethodRenames) = OverloadCollisionDisambiguator.Disambiguate(
                newRoot, root, semanticModel);
        }

        // M1：R2→R3 同树合批，仅在 EnableFixed 时执行。
        // fixed residual 闸门：R6（EnablePointer 时）会继续索引化 R5 域内
        // 的解引用/下标，闸门必须延后到 R6 之后，否则 *p 会被误判为 R3 残留。
        var fixedGatePending = false;
        if (_options.EnableFixed && pointerCluster)
        {
            var fieldResolver = new OriginalSyntaxResolver(root, newRoot);
            var fieldRewriter = new FixedBufferFieldRewriter(
                semanticModel, statistics, fieldResolver);
            newRoot = (CompilationUnitSyntax)fieldRewriter.Visit(newRoot)!;
            foreach (var diagnostic in fieldRewriter.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }

            if (fieldRewriter.HasErrors)
            {
                return Rollback(sourceCode, statistics, diagnostics);
            }

            var statementResolver = new OriginalSyntaxResolver(root, newRoot);
            var loweredFields = new HashSet<IFieldSymbol>(
                fieldRewriter.RewrittenFields, SymbolEqualityComparer.Default);
            foreach (var resourceField in resourceFields)
                loweredFields.Add(resourceField);
            var statementRewriter = new FixedStatementRewriter(
                semanticModel,
                loweredFields,
                pointerRewritten,
                statistics,
                statementResolver,
                pointerMethodCatalog,
                flowModel,
                lowerableStackAllocLocals);
            newRoot = (CompilationUnitSyntax)statementRewriter.Visit(newRoot)!;
            foreach (var diagnostic in statementRewriter.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }

            if (statementRewriter.HasErrors)
            {
                return Rollback(sourceCode, statistics, diagnostics);
            }

            loweredPointerSymbols = statementRewriter.LoweredSymbols.ToArray();
            loweredArrayMap = statementRewriter.LoweredArrayMap;

            if (!_options.EnablePointer)
            {
                // fixed 段 residual 自检：输出树不得残留 fixed 语句、fixed 字段
                // 以及由 fixed 块根生出的指针节点；命中即内部规则缺口 UF010。
                if (HasFixedResidual(newRoot, out var residualReason))
                {
                    statistics.ResidualCount++;
                    statistics.Rejections++;
                    diagnostics.Add(new UnsafeFfmDiagnostic(
                        UnsafeFfmSeverity.Error,
                        UnsafeFfmDiagnosticCode.UF010,
                        "fixed residual gate: " + residualReason +
                        " (internal rule gap); the original file was preserved.",
                        filePath, 0, 0));
                    return Rollback(sourceCode, statistics, diagnostics);
                }
            }
            else
            {
                fixedGatePending = true;
            }
        }

        // M2：R4 仅在 EnableStackAlloc 时执行，链位在 R3 之后。
        if (_options.EnableStackAlloc && hasStackAlloc)
        {
            var stackAllocResolver = new OriginalSyntaxResolver(root, newRoot);
            var stackAllocRewriter = new StackAllocRewriter(
                semanticModel, statistics, stackAllocResolver);
            newRoot = (CompilationUnitSyntax)stackAllocRewriter.Visit(newRoot)!;
            foreach (var diagnostic in stackAllocRewriter.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }

            if (stackAllocRewriter.HasErrors)
            {
                return Rollback(sourceCode, statistics, diagnostics);
            }

            // stackalloc 段 residual 自检：输出树不得残留任何显式/隐式
            // stackalloc 节点；命中即内部规则缺口 UF010。
            if (HasStackAllocResidual(newRoot, out var residualReason))
            {
                statistics.ResidualCount++;
                statistics.Rejections++;
                diagnostics.Add(new UnsafeFfmDiagnostic(
                    UnsafeFfmSeverity.Error,
                    UnsafeFfmDiagnosticCode.UF010,
                    "stackalloc residual gate: " + residualReason +
                    " (internal rule gap); the original file was preserved.",
                    filePath, 0, 0));
                return Rollback(sourceCode, statistics, diagnostics);
            }
        }

        // R6 指针表达式索引化（M3a 基础族）：消费 R5 的 flowModel 与
        // R3 降级符号集，链位在 R4 之后。
        if (_options.EnablePointer && pointerCluster)
        {
            var exprResolver = new OriginalSyntaxResolver(root, newRoot);
            var exprRewriter = new PointerExprRewriter(
                semanticModel,
                flowModel,
                loweredPointerSymbols,
                loweredArrayMap,
                pointerMethodCatalog,
                statistics,
                exprResolver,
                newRoot,
                lowerableStackAllocLocals,
                zeroInvariantStackAllocLocals,
                pointerMethodRenames);
            newRoot = (CompilationUnitSyntax)exprRewriter.Visit(newRoot)!;
            foreach (var diagnostic in exprRewriter.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }

            if (exprRewriter.HasErrors)
            {
                return Rollback(sourceCode, statistics, diagnostics);
            }

            // 延后的 fixed residual 闸门：R6 已收编 R5 域指针使用，剩余即真缺口。
            if (fixedGatePending && HasFixedResidual(newRoot, out var fixedReason))
            {
                statistics.ResidualCount++;
                statistics.Rejections++;
                diagnostics.Add(new UnsafeFfmDiagnostic(
                    UnsafeFfmSeverity.Error,
                    UnsafeFfmDiagnosticCode.UF010,
                    "fixed residual gate: " + fixedReason +
                    " (internal rule gap); the original file was preserved.",
                    filePath, 0, 0));
                return Rollback(sourceCode, statistics, diagnostics);
            }

            // pointer residual 闸门：输出树指针语法节点必须归零（指针类型/
            // function pointer/取址/解引用/指针成员访问）；命中即 UF010。
            if (HasPointerResidual(newRoot, out var pointerReason))
            {
                statistics.ResidualCount++;
                statistics.Rejections++;
                diagnostics.Add(new UnsafeFfmDiagnostic(
                    UnsafeFfmSeverity.Error,
                    UnsafeFfmDiagnosticCode.UF010,
                    "pointer residual gate: " + pointerReason +
                    " (internal rule gap); the original file was preserved.",
                    filePath, 0, 0));
                return Rollback(sourceCode, statistics, diagnostics);
            }
        }

        // R8 extern 互操作边界（M4，EnableExtern）：白名单未加载/未命中 →
        // UF060；命中但布局不可证明 → UF061；命中且可证明 → 归一为
        // InteropFacade 受控声明（C# 响亮 stub，Java FFM 通道消费）；
        // Marshal/NativeLibrary/SafeHandle/HandleRef 站点 UF070。
        ExternInteropRewriter? externRewriter = null;
        if (_options.EnableExtern && externInterop)
        {
            var externResolver = new OriginalSyntaxResolver(root, newRoot);
            externRewriter = new ExternInteropRewriter(
                semanticModel, statistics, externResolver,
                _options.Whitelist, _facadeSupportEmittedAssemblies);
            newRoot = (CompilationUnitSyntax)externRewriter.Visit(newRoot)!;
            foreach (var diagnostic in externRewriter.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }

            if (externRewriter.HasErrors)
            {
                return RollbackExtern(sourceCode, statistics, diagnostics, externRewriter);
            }
        }

        // R8 之后的任何整文回滚都必须释放本单元已登记的跨文件支撑占位与
        // facade 计数，否则同程序集后续成功文件会漏注入 marker。
        UnsafeFfmResult RollbackAfterExtern()
        {
            if (externRewriter != null)
            {
                externRewriter.RollbackSupportEmission();
                statistics.FfmFacadesEmitted -= externRewriter.FacadePlansCount;
            }
            return Rollback(sourceCode, statistics, diagnostics);
        }

        // D14 residual 闸门 M4 扩展（pass 开启即扫）：ref struct 声明与
        // InlineArray 特性属基座始终扫描；EnableExtern 时另扫 extern 修饰符、
        // DllImport/LibraryImport 特性。命中即规则缺口 UF010。
        if (HasM4Residual(newRoot, semanticModel, root,
                _options.EnableExtern, out var m4Reason))
        {
            statistics.ResidualCount++;
            statistics.Rejections++;
            diagnostics.Add(new UnsafeFfmDiagnostic(
                UnsafeFfmSeverity.Error,
                UnsafeFfmDiagnosticCode.UF010,
                "M4 residual gate: " + m4Reason +
                " (internal rule gap); the original file was preserved.",
                filePath, 0, 0));
            return RollbackAfterExtern();
        }

        if (newRoot.IsEquivalentTo(root))
        {
            statistics.FilesSkippedClean++;
            return RollbackAfterExtern();
        }

        var outputCode = FormatAndValidate(newRoot);
        if (outputCode == null)
        {
            statistics.FilesSkippedClean++;
            diagnostics.Add(new UnsafeFfmDiagnostic(
                UnsafeFfmSeverity.Warning,
                UnsafeFfmDiagnosticCode.UF010,
                "unsafe-ffm rewrite failed local parse validation; the original file was preserved " +
                "pending whole-tree build/test verification.",
                filePath, 0, 0));
            return RollbackAfterExtern();
        }

        statistics.FilesTransformed++;
        return new UnsafeFfmResult
        {
            OutputCode = outputCode,
            Changed = true,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// stackalloc 拒绝预检：扫描原始树全部显式/隐式 stackalloc 站点。
    /// Span 目标站点仅做元素安全判定（交 R4 堆化）；裸指针（含 var 自然类型）
    /// 站点先经 <see cref="StackAllocPointerSafety"/> 判定——全部使用可证留在
    /// 可索引域（下标/*p/自增/同源运算/嵌套 fixed/配对源码方法或白名单指针
    /// 元数据方法实参）时登记为可降级局部（R3 降级为「伴生堆数组 + 同名 int
    /// 索引」），否则收集 UF040。返回 true 表示存在拒绝（调用方整文回滚）。
    /// </summary>
    private bool TryRejectStackAllocSites(
        CompilationUnitSyntax root,
        SemanticModel semanticModel,
        string filePath,
        PointerMethodCatalog catalog,
        UnsafeFfmStatistics statistics,
        out List<UnsafeFfmDiagnostic> rejected,
        HashSet<ISymbol> lowerableLocals,
        HashSet<ISymbol> zeroInvariantLocals)
    {
        rejected = new List<UnsafeFfmDiagnostic>();

        // 裸指针降级依赖 R3（fixed/指针局部降级）与 R6（表达式索引化）同时在线。
        var pointerLoweringEnabled = _options.EnableFixed && _options.EnablePointer;

        var pointerSites = new List<(ExpressionSyntax Node, VariableDeclaratorSyntax? Declarator)>();

        foreach (var node in root.DescendantNodes()
                     .OfType<StackAllocArrayCreationExpressionSyntax>())
        {
            var siteType = StackAllocElementSafety.GetSiteType(semanticModel, node);
            if (siteType is IPointerTypeSymbol)
            {
                pointerSites.Add((node, FindPointerSeedDeclarator(node)));
                continue;
            }

            var element = StackAllocElementSafety.GetExplicitElement(semanticModel, node);
            var reason = string.Empty;
            if (element == null || !StackAllocElementSafety.IsSafeElement(element, out reason))
            {
                rejected.Add(MakeUf040(
                    $"stackalloc element type '{element?.ToDisplayString() ?? "?"}' cannot be safely " +
                    "heap-allocated: " + reason + " Rewrite the allocation manually or route it through " +
                    "a supported form.",
                    node, statistics));
            }
        }

        foreach (var node in root.DescendantNodes()
                     .OfType<ImplicitStackAllocArrayCreationExpressionSyntax>())
        {
            var siteType = StackAllocElementSafety.GetSiteType(semanticModel, node);
            if (siteType is IPointerTypeSymbol)
            {
                pointerSites.Add((node, FindPointerSeedDeclarator(node)));
                continue;
            }

            var element = StackAllocElementSafety.GetImplicitElement(semanticModel, node);
            var reason = string.Empty;
            if (element == null || !StackAllocElementSafety.IsSafeElement(element, out reason))
            {
                rejected.Add(MakeUf040(
                    $"implicit stackalloc element type '{element?.ToDisplayString() ?? "?"}' cannot be " +
                    "safely heap-allocated: " + reason + " Rewrite the allocation manually.",
                    node, statistics));
            }
        }

        if (pointerSites.Count == 0) return rejected.Count > 0;

        // 阶段一：统一登记全部候选种子，再预取其所属方法的配对计划，令信号
        // ⑦强制闭包沿 stackalloc 可证根传播（catalog 逐文件存活，不可降级
        // 站点所在文件整文回滚，候选过登记不落盘）。
        var candidateSeeds = new List<VariableDeclaratorSyntax>();
        foreach (var (_, declarator) in pointerSites)
        {
            if (declarator == null) continue;
            if (semanticModel.GetDeclaredSymbol(declarator) is ILocalSymbol { Type: IPointerTypeSymbol })
                candidateSeeds.Add(declarator);
        }

        catalog.RegisterStackAllocCursorRoots(candidateSeeds
            .Select(d => semanticModel.GetDeclaredSymbol(d))
            .Where(s => s != null)!);
        foreach (var declarator in candidateSeeds)
        {
            // 种子可能声明于方法体或局部函数体内；最内层局部函数与其外层
            // 方法均预取计划，使信号⑦强制配对闭包完整传播。
            var enclosingLocalFunction = declarator
                .FirstAncestorOrSelf<LocalFunctionStatementSyntax>();
            if (enclosingLocalFunction != null
                && semanticModel.GetDeclaredSymbol(enclosingLocalFunction) is IMethodSymbol localFunction)
            {
                catalog.GetPlan(localFunction);
            }

            var enclosingMethod = declarator
                .FirstAncestorOrSelf<BaseMethodDeclarationSyntax>();
            if (enclosingMethod != null
                && semanticModel.GetDeclaredSymbol(enclosingMethod) is IMethodSymbol method)
            {
                catalog.GetPlan(method);
            }
        }

        // 阶段二：逐站点安全判定。
        foreach (var (node, declarator) in pointerSites)
        {
            if (!pointerLoweringEnabled || declarator == null)
            {
                rejected.Add(MakeBarePointerUf040(node, statistics));
                continue;
            }

            if (StackAllocPointerSafety.IsLowerable(
                    declarator, semanticModel, catalog, out var lowerReason, out var staysAtZero))
            {
                var local = (ILocalSymbol)semanticModel.GetDeclaredSymbol(declarator)!;
                lowerableLocals.Add(local);
                if (staysAtZero) zeroInvariantLocals.Add(local);
            }
            else
            {
                rejected.Add(MakeUf040(
                    $"stackalloc expression '{node.ToString().Replace("\n", " ")}' assigned to a bare " +
                    "pointer target cannot be lowered to a heap array plus index: " + lowerReason +
                    " Rewrite it as 'Span<T> x = stackalloc T[n];' (or an array form) manually.",
                    node, statistics));
            }
        }

        return rejected.Count > 0;
    }

    /// <summary>
    /// 找到以该 stackalloc 表达式为初值的局部声明符（经括号/checked 包装
    /// 解包）；非声明初值（如直接作实参）返回 null。
    /// </summary>
    private static VariableDeclaratorSyntax? FindPointerSeedDeclarator(
        ExpressionSyntax stackAllocNode)
    {
        var current = stackAllocNode.Parent;
        while (current is ParenthesizedExpressionSyntax or CheckedExpressionSyntax)
            current = current.Parent;

        return current is EqualsValueClauseSyntax
        {
            Parent: VariableDeclaratorSyntax declarator
        } && declarator.Initializer?.Value != null
            ? declarator
            : null;
    }

    private static UnsafeFfmDiagnostic MakeBarePointerUf040(
        SyntaxNode node, UnsafeFfmStatistics statistics)
    {
        var implicitSite = node is ImplicitStackAllocArrayCreationExpressionSyntax;
        var message = implicitSite
            ? $"implicit stackalloc expression '{node}' is used at a bare pointer site (including " +
              "'var', whose natural type for stackalloc is T*); it cannot be converted to a " +
              "Span/array form without changing pointer-only usages. Rewrite it manually with an " +
              "explicit 'Span<T>' target."
            : $"stackalloc expression '{node.ToString().Replace("\n", " ")}' is assigned to a " +
              "bare pointer target (including 'var', whose natural type for stackalloc is T*); " +
              "it cannot be converted to a Span/array local without changing pointer-only usages. " +
              "Rewrite it as 'Span<T> x = stackalloc T[n];' (or an array form) manually.";
        return MakeUf040(message, node, statistics);
    }

    private static UnsafeFfmDiagnostic MakeUf040(
        string message, SyntaxNode node, UnsafeFfmStatistics statistics)
    {
        var line = 1;
        var character = 1;
        try
        {
            var span = node.GetLocation().GetLineSpan();
            line = span.StartLinePosition.Line + 1;
            character = span.StartLinePosition.Character + 1;
        }
        catch
        {
            // 行号不可用时退回 (1,1)。
        }

        statistics.Rejections++;
        statistics.Uf040Rejections++;
        return new UnsafeFfmDiagnostic(
            UnsafeFfmSeverity.Error,
            UnsafeFfmDiagnosticCode.UF040,
            message,
            null, line, character);
    }

    /// <summary>
    /// cs2j 生成的支撑/登记产物命名约定（顶层 <c>_Cs2j*.cs</c>），与
    /// <c>CSharpToJava.CLI.Preprocessing.Adapters.MakeReadonlyPassAdapter.IsGeneratedSupportFile</c>
    /// 同口径。这类文件不是用户 unsafe 面，本 pass 无规则作用于它们。
    /// </summary>
    private static bool IsCs2jGeneratedSupportFile(string? filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName.StartsWith("_Cs2j", StringComparison.Ordinal)
               && fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 指针簇语法预筛：原树是否含 R2/R3/R5/R6 可能作用的语法形态——指针/
    /// 函数指针类型，或 <c>unsafe</c>/<c>fixed</c>/<c>stackalloc</c> 关键字。
    /// 指针符号必然由指针类型语法声明（形参/局部/字段），ByRef&lt;T*&gt; holder
    /// 与元数据指针实参亦含指针类型实参，故无这些形态时指针簇规则必为恒等
    /// 空改写。<paramref name="hasStackAlloc"/> 另输出原树是否含 stackalloc
    /// 关键字，供 M2 拒绝预检/R4 对无 stackalloc 文件整段跳过（无站点时两者
    /// 均为恒等空操作）。仅读语法结构，不做任何语义查询。
    /// </summary>
    private static bool HasPointerClusterSyntax(CompilationUnitSyntax root, out bool hasStackAlloc)
    {
        var pointer = false;
        foreach (var node in root.DescendantNodes())
        {
            if (node is PointerTypeSyntax or FunctionPointerTypeSyntax)
            {
                pointer = true;
                break;
            }
        }

        hasStackAlloc = false;
        foreach (var token in root.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.StackAllocKeyword))
            {
                hasStackAlloc = true;
                pointer = true;
            }
            else if (token.IsKind(SyntaxKind.UnsafeKeyword)
                || token.IsKind(SyntaxKind.FixedKeyword))
            {
                pointer = true;
            }
        }

        return pointer;
    }

    /// <summary>
    /// R8 语法预筛：原树是否含 extern 修饰符、<c>DllImport</c>/<c>LibraryImport</c>
    /// 特性名，或 <c>Marshal</c>/<c>NativeLibrary</c>/<c>SafeHandle</c>/<c>HandleRef</c>
    /// 标识符（含 using 别名/限定名形态——限定名的各段都是标识符，别名来源的
    /// using 指令同样含全名标识符）。R8 的全部触发（extern-like 方法、互操作
    /// API/类型站点）都必须经由这些文本标识符或 extern 关键字进入，无则恒空。
    /// </summary>
    private static bool HasExternInteropSyntax(CompilationUnitSyntax root)
    {
        foreach (var token in root.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.ExternKeyword))
                return true;
        }

        foreach (var node in root.DescendantNodes())
        {
            if (node is IdentifierNameSyntax identifier)
            {
                var text = identifier.Identifier.ValueText;
                if (text is "Marshal" or "NativeLibrary" or "SafeHandle" or "HandleRef"
                    or "DllImport" or "DllImportAttribute"
                    or "LibraryImport" or "LibraryImportAttribute")
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// R1 残留扫描：仍存在 <see cref="UnsafeStatementSyntax"/> 或 unsafe keyword
    /// token（任何位置的 unsafe 修饰符）即视为残留。
    /// </summary>
    private static bool HasUnsafeContextResidual(CompilationUnitSyntax root)
    {
        if (root.DescendantNodes().OfType<UnsafeStatementSyntax>().Any()) return true;
        return root.DescendantTokens().Any(t => t.IsKind(SyntaxKind.UnsafeKeyword));
    }

    /// <summary>
    /// fixed 段 residual 扫描（EnableFixed 时）：
    /// <list type="bullet">
    /// <item>全树不得残留 <see cref="FixedStatementSyntax"/> 或带 fixed 修饰符的字段；</item>
    /// <item>每个被 R3 改写（带注释）的成员范围内不得残留指针类型、取址/解引用、
    /// 指针成员访问等由 fixed 块根生出的指针节点。</item>
    /// </list>
    /// </summary>
    private static bool HasFixedResidual(CompilationUnitSyntax root, out string reason)
    {
        if (root.DescendantNodes().OfType<FixedStatementSyntax>().Any())
        {
            reason = "the rewritten tree still contains a fixed statement";
            return true;
        }

        if (root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Any(f => f.Modifiers.Any(SyntaxKind.FixedKeyword)))
        {
            reason = "the rewritten tree still contains a fixed-keyword field declaration";
            return true;
        }

        foreach (var annotated in root.GetAnnotatedNodes(
                     FixedStatementRewriter.FixedReplacedAnnotation))
        {
            var member = annotated.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
            if (member == null) continue;

            foreach (var node in member.DescendantNodes())
            {
                if (node is PointerTypeSyntax)
                {
                    reason = $"member '{member}' still contains a pointer type after fixed lowering";
                    return true;
                }

                if (node is PrefixUnaryExpressionSyntax prefix
                    && (prefix.Kind() == SyntaxKind.AddressOfExpression
                        || prefix.Kind() == SyntaxKind.PointerIndirectionExpression))
                {
                    reason = $"member '{member}' still contains an address-of/indirection expression " +
                             "after fixed lowering";
                    return true;
                }

                if (node is MemberAccessExpressionSyntax memberAccess
                    && memberAccess.Kind() == SyntaxKind.PointerMemberAccessExpression)
                {
                    reason = $"member '{member}' still contains a pointer member access (p->m) " +
                             "after fixed lowering";
                    return true;
                }
            }

            if (member.DescendantTokens().Any(t => t.IsKind(SyntaxKind.FixedKeyword)))
            {
                reason = $"member '{member}' still contains a fixed keyword after fixed lowering";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// stackalloc 段 residual 扫描（EnableStackAlloc 时）：全树不得残留
    /// <see cref="StackAllocArrayCreationExpressionSyntax"/> 或
    /// <see cref="ImplicitStackAllocArrayCreationExpressionSyntax"/>。
    /// </summary>
    private static bool HasStackAllocResidual(CompilationUnitSyntax root, out string reason)
    {
        if (root.DescendantNodes().OfType<StackAllocArrayCreationExpressionSyntax>().Any())
        {
            reason = "the rewritten tree still contains an explicit stackalloc expression";
            return true;
        }

        if (root.DescendantNodes().OfType<ImplicitStackAllocArrayCreationExpressionSyntax>().Any())
        {
            reason = "the rewritten tree still contains an implicit stackalloc expression";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// pointer 段 residual 扫描（EnablePointer 时）：R5/R6 之后全树不得残留
    /// <list type="bullet">
    /// <item><see cref="PointerTypeSyntax"/> 或 <see cref="FunctionPointerTypeSyntax"/>；</item>
    /// <item>取址/解引用 <see cref="PrefixUnaryExpressionSyntax"/>；</item>
    /// <item>指针成员访问（p-&gt;m）。</item>
    /// </list>
    /// 命中即内部规则缺口 UF010。
    /// </summary>
    private static bool HasPointerResidual(CompilationUnitSyntax root, out string reason)
    {
        if (root.DescendantNodes().OfType<PointerTypeSyntax>().Any())
        {
            reason = "the rewritten tree still contains a pointer type";
            return true;
        }

        if (root.DescendantNodes().OfType<FunctionPointerTypeSyntax>().Any())
        {
            reason = "the rewritten tree still contains a function pointer type";
            return true;
        }

        if (root.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>()
            .Any(p => p.Kind() is SyntaxKind.AddressOfExpression
                or SyntaxKind.PointerIndirectionExpression))
        {
            reason = "the rewritten tree still contains an address-of or pointer indirection expression";
            return true;
        }

        if (root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Any(m => m.Kind() == SyntaxKind.PointerMemberAccessExpression))
        {
            reason = "the rewritten tree still contains a pointer member access (p->m)";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// 取声明直接所属命名空间名（含 file-scoped 与块形式）；全局命名空间返回 null。
    /// 用于识别 C01 释放的 Cs2jByRef 支撑库（holder 随预构建 Java compat 发布，
    /// 不受用户 ref struct 残差门约束）。
    /// </summary>
    private static string? EnclosingNamespace(SyntaxNode node)
    {
        for (var current = node.Parent; current != null; current = current.Parent)
        {
            switch (current)
            {
                case FileScopedNamespaceDeclarationSyntax fs:
                    return fs.Name.ToString();
                case NamespaceDeclarationSyntax ns:
                    return ns.Name.ToString();
            }
        }
        return null;
    }

    /// <summary>
    /// M4 residual 闸门（D14 扩展）：
    /// <list type="bullet">
    /// <item>ref struct 声明残留（struct modifiers 带 RefKeyword，语法明确）；</item>
    /// <item><paramref name="scanExtern"/> 时成员声明带 extern 修饰符
    /// （extern alias 是 ExternAliasDirect，不是成员声明，不命中）；</item>
    /// <item>特性经 resolver 映射回原始树做全名称判定：InlineArray 始终扫；
    /// DllImport/LibraryImport 仅 <paramref name="scanExtern"/> 时扫。</item>
    /// </list>
    /// 命中即规则缺口 UF010。
    /// </summary>
    private static bool HasM4Residual(
        CompilationUnitSyntax newRoot,
        SemanticModel semanticModel,
        CompilationUnitSyntax originalRoot,
        bool scanExtern,
        out string reason)
    {
        if (newRoot.DescendantNodes().OfType<StructDeclarationSyntax>()
            .Any(s => s.Modifiers.Any(SyntaxKind.RefKeyword)
                      && EnclosingNamespace(s) != "Cs2jByRef"))
        {
            reason = "the rewritten tree still contains a ref struct declaration";
            return true;
        }

        if (scanExtern && newRoot.DescendantNodes().OfType<MemberDeclarationSyntax>()
                .Any(m => m.Modifiers.Any(SyntaxKind.ExternKeyword)))
        {
            reason = "the rewritten tree still contains an extern member modifier";
            return true;
        }

        var resolver = new OriginalSyntaxResolver(originalRoot, newRoot);
        foreach (var attribute in newRoot.DescendantNodes().OfType<AttributeSyntax>())
        {
            var original = resolver.Resolve(attribute);
            var symbol = original == null
                ? null
                : semanticModel.GetSymbolInfo(original).Symbol;
            var attributeName = (symbol as IMethodSymbol)?.ContainingType.ToDisplayString();

            if (attributeName == "System.Runtime.CompilerServices.InlineArrayAttribute")
            {
                reason = "the rewritten tree still contains an InlineArray attribute";
                return true;
            }

            if (scanExtern
                && attributeName is "System.Runtime.InteropServices.DllImportAttribute"
                    or "System.Runtime.InteropServices.LibraryImportAttribute")
            {
                reason = $"the rewritten tree still contains a {attributeName} attribute";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>原文回滚：Changed=false，OutputCode 即原始源码。</summary>
    private static UnsafeFfmResult Rollback(
        string sourceCode,
        UnsafeFfmStatistics statistics,
        IReadOnlyList<UnsafeFfmDiagnostic> diagnostics)
        => new()
        {
            OutputCode = sourceCode,
            Changed = false,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };

    /// <summary>
    /// R8 extern rewriter 自身报错时的回滚：除原文保留外，释放该单元已
    /// 登记的跨文件支撑类型占位并扣减 facade 计数。
    /// </summary>
    private static UnsafeFfmResult RollbackExtern(
        string sourceCode,
        UnsafeFfmStatistics statistics,
        IReadOnlyList<UnsafeFfmDiagnostic> diagnostics,
        ExternInteropRewriter externRewriter)
    {
        externRewriter.RollbackSupportEmission();
        statistics.FfmFacadesEmitted -= externRewriter.FacadePlansCount;
        return Rollback(sourceCode, statistics, diagnostics);
    }

    /// <summary>整树格式化 → 原文候选，每个候选重新解析零 Error 才返回。</summary>
    private static string? FormatAndValidate(CompilationUnitSyntax root)
    {
        var candidates = new List<string>();
        using (var workspace = new AdhocWorkspace())
        {
            try
            {
                candidates.Add(Formatter.Format(root, workspace).ToFullString());
            }
            catch
            {
                // 格式化失败不致命，原文候选兜底。
            }
        }
        candidates.Add(root.ToFullString());

        return candidates.FirstOrDefault(candidate =>
        {
            try
            {
                var reparsed = CSharpSyntaxTree.ParseText(
                    candidate, new CSharpParseOptions(LanguageVersion.Latest));
                return !reparsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error);
            }
            catch
            {
                return false;
            }
        });
    }
}
