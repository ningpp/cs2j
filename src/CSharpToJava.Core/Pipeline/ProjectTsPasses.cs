using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Pipeline;

/// <summary>
/// TS 目标语法树层 rewriter 序列（Task 38，spec §2.4）：数值语义 → 重载消歧 →
/// 泛型擦除 → 相等语义 → 索引器 → 模式匹配，逐文件改写后重建编译。
/// 与 SingleFileTsEmitPass 的 1a-1f 同序；项目模式按文件分发语义模型
/// （rewriter 内部经 context.GetSemanticModelForNode 逐节点解析，多文件各树
/// 模型不同）；重载/泛型 rewriter 先 Precollect 全部文件再统一改写（跨文件
/// 调用点与改名表一致）。必须在 ProjectPartialTypeNormalizationPass 之前运行
/// （类型组基于改写后树构建）。
/// </summary>
public sealed class ProjectTsSyntaxRewritePass : ICs2jPass<ProjectPassState>
{
    public string Name => nameof(ProjectTsSyntaxRewritePass);
    public Cs2jPassStage Stage => Cs2jPassStage.Normalize;

    public void Execute(ProjectPassState state)
    {
        if (state.Context.ProjectCompilation == null)
        {
            return;
        }

        // 1a. 数值语义（Task 19：整数除法/窄化/bigint 混算）
        RunStage(state, () => new TypeScript.Rewriters.TsNumericSemanticsRewriter(state.Context));
        // 1b. 重载消歧（Task 23）：先预收集全部文件改名表，再统一改写
        RunStage(state, () => new TypeScript.Rewriters.TsOverloadDisambiguationRewriter(state.Context), precollect: true);
        // 1c. 泛型擦除（Task 24）：先预收集全部文件工厂需求（跨文件透传闭包）
        RunStage(state, () => new TypeScript.Rewriters.TsGenericErasureRewriter(state.Context), precollect: true);
        // 1d. 相等语义（Task 26：record/struct/Equals 覆写类 → CsObject.equals）
        RunStage(state, () => new TypeScript.Rewriters.TsEqualityRewriter(state.Context));
        // 1e. 索引器调用点（Task 27：b[i] → get_Item/set_Item）
        RunStage(state, () => new TypeScript.Rewriters.TsIndexerRewriter(state.Context));
        // 1f. 模式匹配（Task 27：声明模式/switch 模式 → instanceof + 守卫）
        RunStage(state, () => new TypeScript.Rewriters.TsPatternMatchingRewriter());
    }

    private static void RunStage(
        ProjectPassState state,
        Func<CSharpSyntaxRewriter> factory,
        bool precollect = false)
    {
        var trees = state.Compilation.SyntaxTrees
            .Where(tree => !string.IsNullOrWhiteSpace(tree.FilePath)
                && !tree.FilePath.StartsWith("<", StringComparison.Ordinal))
            .ToList();
        if (trees.Count == 0)
        {
            return;
        }

        var rewriter = factory();
        if (precollect && rewriter is TypeScript.ITsSyntaxPrecollector collector)
        {
            foreach (var tree in trees)
            {
                collector.Precollect((CompilationUnitSyntax)tree.GetRoot());
            }
        }

        var changed = new List<(SyntaxTree Old, SyntaxTree New)>();
        foreach (var tree in trees)
        {
            var root = tree.GetRoot();
            var newRoot = rewriter.Visit(root);
            if (newRoot is CompilationUnitSyntax newUnit && !ReferenceEquals(newUnit, root))
            {
                changed.Add((tree, tree.WithRootAndOptions(newUnit, tree.Options)));
            }
        }

        if (changed.Count == 0)
        {
            return;
        }

        foreach (var (oldTree, newTree) in changed)
        {
            state.Compilation = state.Compilation.ReplaceSyntaxTree(oldTree, newTree);
        }
        ProjectLinqDesugarPass.RebuildProjectStateAfterCompilationChange(state);
    }
}

/// <summary>
/// TS 目标类型发射 pass（Task 38，spec §2.4：ProjectTypeEmitPass 的 TS 变体）：
/// 复用共享前端 TypeGroupResolver.ConvertTypeGroup（Roslyn 前端 + 目标无关 IR 生成；
/// 仅跑用户注册 rewriter，Java 专属内置 rewriter 不进入），对每个类型组的 Java IR
/// 运行 TS 专属 rewriter 并用 TypeScriptEmitter 二次映射发射 .ts。
/// </summary>
public sealed class ProjectTsEmitPass : ICs2jPass<ProjectPassState>
{
    private readonly IReadOnlyList<Java.JavaSyntaxRewriter> _irRewriters;

    public ProjectTsEmitPass(IReadOnlyList<Java.JavaSyntaxRewriter> irRewriters)
    {
        _irRewriters = irRewriters;
    }

    public string Name => nameof(ProjectTsEmitPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Emit;

    public void Execute(ProjectPassState state)
    {
        // Pre-scan：解析 var 声明局部类型（与 Java emit pass 一致，供下游 fallback 使用）
        VarTypeResolver.PreScan(state.Compilation, state.Context);

        // 预登记 [Flags] 枚举（与 Java emit pass 一致；name-based flags 注册表）
        ProjectTypeEmitPass.PreRegisterFlagsEnums(state);

        foreach (var typeGroup in state.TypeGroups)
        {
            var candidatePaths = ProjectTypeEmitPass.CollectCandidatePaths(typeGroup);
            if (candidatePaths.Any(path => state.BlockedFilePaths.Contains(ProjectTypeEmitPass.NormalizeFilePath(path))))
            {
                continue;
            }

            if (!ProjectTypeEmitPass.ShouldEmitTypeGroup(candidatePaths, state.EmitFilePaths))
            {
                continue;
            }

            var result = TypeGroupResolver.ConvertTypeGroup(typeGroup, state.Compilation, state.Context, _irRewriters);
            if (result != null)
            {
                if (result.Success && result.Compilation != null)
                {
                    // 共享前端产出 Java IR；TS 二次映射发射（Task 38）
                    result.GeneratedCode = TypeScript.TsEmitSupport.EmitTypeScript(result.Compilation, state.Context);
                    result.FileName = Path.ChangeExtension(result.FileName, ".ts");
                }

                var diagnostics = state.GetDiagnosticsForPaths(candidatePaths);
                if (diagnostics.Count > 0)
                {
                    result.Diagnostics = result.Diagnostics.Concat(diagnostics).ToList();
                    result.Success = result.Success && diagnostics.All(diagnostic => diagnostic.Severity != Context.DiagnosticSeverity.Error);
                }

                state.Results.Add(result);
            }
        }
    }
}

/// <summary>
/// 项目级 npm 依赖收集 pass（Task 38，spec §2.4：ProjectJavaModuleDependencyPass
/// 的 TS 变体）：扫描生成 TS 的 ESM 导入（import ... from "pkg"），把包名
/// （@scope/name 或 name）收集到 result.NpmPackageDependencies；CLI 据此写模块
/// package.json dependencies（Task 40 接线）。相对路径导入（./ ../）跳过。
/// </summary>
public sealed class ProjectNpmDependencyPass : ICs2jPass<ProjectPassState>
{
    public string Name => nameof(ProjectNpmDependencyPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Emit;

    public void Execute(ProjectPassState state)
    {
        foreach (var result in state.Results)
        {
            if (string.IsNullOrWhiteSpace(result.GeneratedCode) || !result.Success)
            {
                continue;
            }

            var packages = CollectNpmPackages(result.GeneratedCode);
            if (packages.Count > 0)
            {
                result.NpmPackageDependencies = packages;
            }
        }
    }

    private static HashSet<string> CollectNpmPackages(string code)
    {
        var packages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in code.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("import ", StringComparison.Ordinal)
                && !trimmed.StartsWith("export ", StringComparison.Ordinal))
            {
                continue;
            }

            // import { X } from "pkg"; / export ... from "pkg";
            var fromIndex = trimmed.IndexOf(" from ", StringComparison.Ordinal);
            if (fromIndex < 0)
            {
                continue;
            }
            var rest = trimmed[(fromIndex + " from ".Length)..].Trim();
            if (rest.Length < 2 || (rest[0] != '"' && rest[0] != '\''))
            {
                continue;
            }
            var endIndex = rest.IndexOf(rest[0], 1);
            if (endIndex < 0)
            {
                continue;
            }
            var modulePath = rest[1..endIndex];
            // 仅收集 npm 包导入：相对路径/绝对路径/扩展名路径跳过
            if (modulePath.StartsWith(".") || modulePath.StartsWith("/") || modulePath.Contains(".js"))
            {
                continue;
            }
            packages.Add(modulePath);
        }
        return packages;
    }
}
