using System.Text.Json;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// npm workspace 工程规划（Task 37，spec §6.1）：每个 C# 项目 → 一个 npm 包目录
/// （modules/&lt;project&gt;/），解决方案 → npm workspaces 单仓多包（对应 Maven 多模块）；
/// 项目引用 → workspace 依赖（"workspace:*"）；测试项目 → 独立包 + vitest 配置。
/// 对标 Java 侧 <see cref="CSharpToJava.Core.Pipeline.Planning.JavaModulePlan"/>。
/// </summary>
public enum NpmDependencyKind
{
    Runtime,
    Dev,
}

/// <summary>npm 依赖声明（package.json dependencies / devDependencies 项）。</summary>
public sealed class NpmProjectDependency
{
    public required string PackageName { get; init; }
    public string VersionSpec { get; init; } = "*";

    /// <summary>工作区内部引用 → 版本规格 "workspace:*"（npm workspaces 协议）。</summary>
    public bool IsWorkspace { get; init; }
    public NpmDependencyKind Kind { get; init; } = NpmDependencyKind.Runtime;

    public static NpmProjectDependency Workspace(string packageName) =>
        new() { PackageName = packageName, IsWorkspace = true };

    public static NpmProjectDependency Registry(string packageName, string version = "*") =>
        new() { PackageName = packageName, VersionSpec = version };

    public string ResolvedVersionSpec => IsWorkspace ? "workspace:*" : VersionSpec;
}

/// <summary>资源文件映射（源路径 → 包内 resources/ 下相对路径，对齐 Java 目标 resources 拷贝策略）。</summary>
public sealed class NpmResourceItem
{
    public required string SourcePath { get; init; }
    public required string TargetRelativePath { get; init; }
}

/// <summary>单个 C# 项目 → 一个 npm 包的输出规划。</summary>
public sealed class NpmProjectPlan
{
    public const string DefaultCompatPackage = "@csharptotypescript/compat";

    /// <summary>兼容库版本（与 typescript/csharptotypescript-compat/package.json 同步）。</summary>
    public const string CompatPackageVersion = "0.1.0";

    /// <summary>完整 npm 包名（如 "core" 或 "@proj/core"）。</summary>
    public required string PackageName { get; init; }

    /// <summary>输出目录名（modules/&lt;ModuleName&gt;/，kebab-case，对齐 Java 模块名规约）。</summary>
    public required string ModuleName { get; init; }

    public required bool IsTestOnly { get; init; }

    /// <summary>包内源码根（命名空间 → &lt;SourceRoot&gt;/&lt;namespace&gt;/，每类型一文件，spec §3.3）。</summary>
    public string SourceRoot { get; init; } = "src";

    /// <summary>主源码包内含测试源（与 IsTestOnly 独立：测试项目 → 独立包，见 spec §6.1）。</summary>
    public bool HasTestSources { get; init; }

    public IReadOnlyList<NpmProjectDependency> Dependencies { get; init; } = [];
    public IReadOnlyList<NpmResourceItem> Resources { get; init; } = [];

    /// <summary>测试项目或含测试源 → 需要 vitest 配置。</summary>
    public bool RequiresVitestConfig => IsTestOnly || HasTestSources;

    public static NpmProjectDependency CompatDependency =>
        NpmProjectDependency.Registry(DefaultCompatPackage, CompatPackageVersion);
}

/// <summary>顶层 npm workspace 输出规划（workspaces 根 + 全部项目包）。</summary>
public sealed class NpmWorkspacePlan
{
    public required string WorkspaceName { get; init; }
    public string Version { get; init; } = "1.0.0";

    /// <summary>根 devDependencies 的 typescript 版本（默认与兼容库仓库一致）。</summary>
    public string TypeScriptVersion { get; init; } = "^7.0.2";
    public string VitestVersion { get; init; } = "^4.1.10";

    public required IReadOnlyList<NpmProjectPlan> Projects { get; init; }

    public bool IsSingleProject => Projects.Count == 1;
    public bool HasTestProjects => Projects.Any(project => project.RequiresVitestConfig);

    /// <summary>
    /// 是否把兼容库（@csharptotypescript/compat）作为工作区成员嵌入产物
    /// （Task 40，spec §6.2 验证链）：CLI 把仓库内 compat 包复制为 &lt;dest&gt;/compat，
    /// 根 workspaces 增加 "compat"、模块依赖改相对 file: 路径（workspace 成员 →
    /// file:../&lt;模块&gt;、compat → file:../../compat；npm 11 的 workspace:* 协议
    /// 回归 EUNSUPPORTEDPROTOCOL，npm/cli#8845），使 npm install 无需依赖 registry
    /// 发布即可本地解析（M5 验收：npm install + tsc + vitest 全通过）。
    /// </summary>
    public bool EmbedCompatPackage { get; init; }
}

/// <summary>npm 包名规约辅助（Task 37）。</summary>
public static class NpmNaming
{
    /// <summary>项目名 → npm 安全包名（小写 kebab-case；规约对齐 MultiModulePlanner.NormalizeModuleName）。</summary>
    public static string NormalizePackageName(string projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        var chars = projectName.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var normalized = new string(chars);
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }
        normalized = normalized.Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "module" : normalized;
    }

    /// <summary>scope + 包名 → scoped 包名（"@proj/core"）；scope 为空/空白时原样返回。</summary>
    public static string ScopedPackageName(string? scope, string packageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        return string.IsNullOrWhiteSpace(scope) ? packageName : $"@{scope}/{packageName}";
    }
}

/// <summary>构造 <see cref="NpmWorkspacePlan"/> 的构建器（对标 WorkspacePlanBuilder）。</summary>
public sealed class NpmWorkspacePlanBuilder
{
    private string _workspaceName = "converted-workspace";
    private string _version = "1.0.0";
    private string _typescriptVersion = "^7.0.2";
    private string _vitestVersion = "^4.1.10";
    private string? _scope;
    private bool _embedCompatPackage;
    private readonly List<NpmProjectPlan> _projects = [];

    public NpmWorkspacePlanBuilder WorkspaceName(string workspaceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceName);
        _workspaceName = workspaceName;
        return this;
    }

    public NpmWorkspacePlanBuilder Version(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        _version = version;
        return this;
    }

    public NpmWorkspacePlanBuilder TypeScriptVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        _typescriptVersion = version;
        return this;
    }

    public NpmWorkspacePlanBuilder VitestVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        _vitestVersion = version;
        return this;
    }

    /// <summary>包名 scope（如 "proj" → "@proj/core"）；不设则使用非 scoped 包名。</summary>
    public NpmWorkspacePlanBuilder Scope(string? scope)
    {
        _scope = string.IsNullOrWhiteSpace(scope) ? null : scope;
        return this;
    }

    public NpmWorkspacePlanBuilder AddProject(NpmProjectPlan project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _projects.Add(project);
        return this;
    }

    /// <summary>按项目名添加项目：自动加入 compat 依赖（spec §6.1 模块 package.json 必含）。</summary>
    public NpmWorkspacePlanBuilder AddProject(
        string projectName,
        bool isTestOnly = false,
        bool hasTestSources = false,
        IEnumerable<NpmProjectDependency>? dependencies = null,
        IEnumerable<NpmResourceItem>? resources = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        var moduleName = NpmNaming.NormalizePackageName(projectName);
        var deps = new List<NpmProjectDependency> { NpmProjectPlan.CompatDependency };
        if (dependencies != null)
        {
            deps.AddRange(dependencies);
        }
        _projects.Add(new NpmProjectPlan
        {
            PackageName = NpmNaming.ScopedPackageName(_scope, moduleName),
            ModuleName = moduleName,
            IsTestOnly = isTestOnly,
            HasTestSources = hasTestSources,
            Dependencies = MergeDependencies(deps),
            Resources = resources?.ToList() ?? [],
        });
        return this;
    }

    /// <summary>把兼容库作为工作区成员嵌入产物（见 <see cref="NpmWorkspacePlan.EmbedCompatPackage"/>）。</summary>
    public NpmWorkspacePlanBuilder EmbedCompatPackage(bool embed = true)
    {
        _embedCompatPackage = embed;
        return this;
    }

    public NpmWorkspacePlan Build() => new()
    {
        WorkspaceName = _workspaceName,
        Version = _version,
        TypeScriptVersion = _typescriptVersion,
        VitestVersion = _vitestVersion,
        EmbedCompatPackage = _embedCompatPackage,
        Projects = _projects.AsReadOnly(),
    };

    /// <summary>按包名去重（首个声明优先，保持声明顺序）。</summary>
    private static IReadOnlyList<NpmProjectDependency> MergeDependencies(
        IEnumerable<NpmProjectDependency> dependencies)
    {
        var merged = new List<NpmProjectDependency>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in dependencies)
        {
            if (seen.Add(dependency.PackageName))
            {
                merged.Add(dependency);
            }
        }
        return merged;
    }
}

/// <summary>
/// npm 工程文件生成器（Task 37，spec §6.1 输出结构）：
/// 根 package.json（workspaces）、tsconfig.base.json（strict/ES2022/NodeNext/
/// useDefineForClassFields:false）、模块 package.json（compat + workspace:* 引用）、
/// 模块 tsconfig.json（extends base）、测试包 vitest.config.ts。
/// </summary>
public static class NpmProjectFiles
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>workspaces 根 package.json（"workspaces": ["modules/*"]）。</summary>
    public static string GenerateRootPackageJson(NpmWorkspacePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var content = new Dictionary<string, object?>
        {
            ["name"] = plan.WorkspaceName,
            ["version"] = plan.Version,
            ["private"] = true,
            ["workspaces"] = plan.EmbedCompatPackage ? new[] { "modules/*", "compat" } : new[] { "modules/*" },
            ["devDependencies"] = new Dictionary<string, object?>
            {
                ["typescript"] = plan.TypeScriptVersion,
                ["vitest"] = plan.VitestVersion,
            },
        };
        return Serialize(content);
    }

    /// <summary>
    /// tsconfig.base.json：strict、target ES2022、module/moduleResolution NodeNext、
    /// declaration、useDefineForClassFields:false（关键：默认 true 时派生类字段声明
    /// 会以 define 语义覆写基类构造器赋值，破坏 C# 初始化语义，spec §6.1）。
    /// </summary>
    public static string GenerateTsconfigBase() =>
        """
        {
          "compilerOptions": {
            "strict": true,
            "target": "ES2022",
            "module": "NodeNext",
            "moduleResolution": "NodeNext",
            "declaration": true,
            "useDefineForClassFields": false,
            "esModuleInterop": true,
            "skipLibCheck": true,
            "forceConsistentCasingInFileNames": true
          }
        }
        """;

    /// <summary>模块 package.json：name/version/private/type:module + dependencies（compat + workspace 引用）。</summary>
    /// <summary>
    /// 模块 package.json。includeEntryPoint（Task 40 跨模块导入接线）：被其他模块
    /// 引用的模块输出 src/index.ts barrel 入口（main/types → ./src/index.ts，
    /// tsc 与 vitest 均按 TS 源码解析，spec §3.3 跨包导入可解析）。
    /// </summary>
    public static string GenerateProjectPackageJson(
        NpmWorkspacePlan plan, NpmProjectPlan project, bool includeEntryPoint = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(project);
        var runtime = new Dictionary<string, object?>();
        var dev = new Dictionary<string, object?>();
        foreach (var dependency in project.Dependencies
            .OrderBy(dependency => dependency.PackageName, StringComparer.Ordinal))
        {
            var target = dependency.Kind == NpmDependencyKind.Dev ? dev : runtime;
            target[dependency.PackageName] = ResolveVersionSpec(plan, project, dependency);
        }

        var content = new Dictionary<string, object?>
        {
            ["name"] = project.PackageName,
            ["version"] = plan.Version,
            ["private"] = true,
            ["type"] = "module",
            ["dependencies"] = runtime,
        };
        if (includeEntryPoint)
        {
            content["main"] = "./src/index.ts";
            content["types"] = "./src/index.ts";
        }
        if (dev.Count > 0)
        {
            content["devDependencies"] = dev;
        }
        if (project.RequiresVitestConfig)
        {
            content["scripts"] = new Dictionary<string, object?>
            {
                ["test"] = "vitest run",
            };
        }
        return Serialize(content);
    }

    /// <summary>
    /// global.d.ts 类型桩（Task 41）：共享前端在 TS 目标下仍产出 Java 风味名
    /// （CSharpList/CSharpDictionary/Integer/String 等，既有已知限制，Task 36 登记）——
    /// L3 tsc 需要这些名字可解析；类型别名在运行时擦除，不影响 vitest 执行。
    /// 与单文件测试 GlobalStub（TsMemberMappingEmissionTests）同源。
    /// </summary>
    public static string GenerateGlobalTypeStub() =>
        """
        type Integer = number;
        type Long = bigint;
        type Boolean = boolean;
        type Double = number;
        type Float = number;
        type Short = number;
        type Byte = number;
        type Character = string;
        type String = string;
        type CSharpList<T> = Array<T>;
        type CSharpDictionary<K, V> = import("@csharptotypescript/compat").CsDictionary<K, V>;
        type CSharpHashSet<T> = import("@csharptotypescript/compat").CsSet<T>;
        type CSharpStack<T> = Array<T>;
        type CSharpICollection<T> = Iterable<T>;
        type CSharpGenericComparer<T> = { compare(a: T, b: T): number };
        type CsKeyValuePair<K, V> = import("@csharptotypescript/compat").CsKeyValuePair<K, V>;
        // ─── Round 23：Java 风味 CSharp* compat 名（io.github.ningpp.compat 面）───
        // C# IEnumerator<T>/IEnumerable 桥接类 implements 契约 + 值位 from() 工厂
        // （CSharpGenericEnumerator 56 / CSharpEnumerator 24 / CSharpGenericIList 52
        // TS2304）。getCurrent 可空宽化（Java 风味 Integer 元素），调用点按
        // C# Current 契约（moveNext()==true 后有效）自行断言。
        interface CSharpGenericEnumerator<T> {
            hasNext(): boolean;
            next(): T | null;
            moveNext(): boolean;
            getCurrent(): T | null;
        }
        declare const CSharpGenericEnumerator: typeof import("@csharptotypescript/compat").CsEnumerator;
        // IList<T> → CSharpGenericIList<T>：TS 结构契约 = 生成集合类（implements，
        // 含 Round 23 补发的 get()/toArray() 桥接）与 Array（CSharpList 别名，
        // polyfill 面）的公共面；iterator() 双形态（生成类枚举器 / Array 迭代器）；
        // length 可选（生成类无该成员，仅消费端 Array 形态读写）。
        interface CSharpGenericIList<T> {
            [Symbol.iterator](): Iterator<T>;
            add(item: T): void;
            remove(item: any): boolean;
            indexOf(item: any): number;
            insert(index: number, item: T): void;
            removeAt(index: number): void;
            size(): number;
            get(index: number): T;
            set(index: number, value: T): void;
            contains(item: any): boolean;
            toArray(mapper?: ((n: number) => T[]) | T[]): T[];
            length?: number;
            iterator(): CSharpGenericEnumerator<T> | IterableIterator<T>;
        }
        // 非泛型 IEnumerator：C# Current 为 object → any（与 Java Object→any 映射一致）。
        type CSharpEnumerator = import("@csharptotypescript/compat").CsEnumerator<any>;
        declare const CSharpEnumerator: typeof import("@csharptotypescript/compat").CsEnumerator;
        type CSharpRandom = import("@csharptotypescript/compat").CsRandom;
        declare const CSharpRandom: typeof import("@csharptotypescript/compat").CsRandom;
        type CSharpQueue<T> = import("@csharptotypescript/compat").CsQueue<T>;
        declare const CSharpQueue: typeof import("@csharptotypescript/compat").CsQueue;
        type CSharpLinkedList<T> = import("@csharptotypescript/compat").CsLinkedList<T>;
        declare const CSharpLinkedList: typeof import("@csharptotypescript/compat").CsLinkedList;
        type CSharpLinkedListNode<T> = import("@csharptotypescript/compat").CsLinkedListNode<T>;
        declare const CSharpLinkedListNode: typeof import("@csharptotypescript/compat").CsLinkedListNode;
        type CSharpSortedDict<K, V> = import("@csharptotypescript/compat").CsSortedDictionary<K, V>;
        declare const CSharpSortedDict: typeof import("@csharptotypescript/compat").CsSortedDictionary;
        type CSharpKeyValuePair<K, V> = import("@csharptotypescript/compat").CsKeyValuePair<K, V>;
        declare const CSharpKeyValuePair: typeof import("@csharptotypescript/compat").CsKeyValuePair;
        type CSharpGenericIterable<T> = Iterable<T>;
        type CSharpArray = import("@csharptotypescript/compat").CsArray;
        declare const CSharpArray: typeof import("@csharptotypescript/compat").CsArray;
        type CSharpObjQueue = import("@csharptotypescript/compat").CsQueue<unknown>;
        declare const CSharpObjQueue: typeof import("@csharptotypescript/compat").CsQueue;
        // Round 19：值位同名 const 合并（TS 允许 type+const 同名声明）——生成的
        // "new CSharpDictionary<K,V>(capacity)" / "new CSharpHashSet<T>(...)" 调用点
        // 经此解析为 compat 类构造器（TS2693）。CSharpList/CSharpStack 的值位由
        // TsRawStatementNormalizer/EmitNew 改写为 []/Array.from，无需 const。
        declare const CSharpDictionary: typeof import("@csharptotypescript/compat").CsDictionary;
        declare const CSharpHashSet: typeof import("@csharptotypescript/compat").CsSet;
        // Round 19：Java 风味 IList/Stack 方法面（CSharpList/CSharpStack = Array 别名，
        // 生成调用点保留 .get(i)/.set(i,v)/.removeAt(i)/.peek() 等形态）。声明与
        // @csharptotypescript/compat 的 ArrayCompat 运行时 polyfill 一一对应——
        // compat 包根入口副作用导入保证任意 compat 导入即安装。
        interface Array<T> {
            get(index: number): T;
            set(index: number, value: T): void;
            add(value: T): void;
            addAll(values: Iterable<T>): void;
            remove(value: T): boolean;
            removeAt(index: number): void;
            insert(index: number, value: T): void;
            removeRange(start: number, end: number): void;
            size(): number;
            toArray<U = T>(mapper?: ((n: number) => U[]) | U[]): U[];
            iterator(): IterableIterator<T>;
            spliterator(): IterableIterator<T>;
            exists(predicate: (value: T) => boolean): boolean;
            ensureCapacity(minimumCapacity: number): void;
            peek(): T;
            contains(item: any): boolean;
        }
        """;

    /// <summary>模块 tsconfig.json：extends 工作区根 tsconfig.base.json。</summary>
    public static string GenerateProjectTsconfig(NpmProjectPlan project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var content = new Dictionary<string, object?>
        {
            ["extends"] = "../../tsconfig.base.json",
            ["compilerOptions"] = new Dictionary<string, object?>
            {
                ["outDir"] = "dist",
                ["rootDir"] = project.SourceRoot,
            },
            ["include"] = new[] { project.SourceRoot },
        };
        return Serialize(content);
    }

    /// <summary>测试包 vitest.config.ts（spec §6.1：测试项目 → 独立包 + vitest 配置）。</summary>
    public static string GenerateVitestConfig(NpmProjectPlan project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return $$"""
            import { defineConfig } from "vitest/config";

            export default defineConfig({
                test: {
                    include: ["{{project.SourceRoot}}/**/*.test.ts"],
                },
            });
            """;
    }

    /// <summary>
    /// 依赖版本规格解析（Task 40）：兼容库嵌入工作区时——workspace 引用改相对
    /// file: 路径（modules/&lt;依赖模块&gt;，npm 11 的 workspace: 协议回归
    /// EUNSUPPORTEDPROTOCOL，npm/cli#8845，file: 全版本可用且语义一致）；
    /// compat 依赖改 file:../../compat（本地解析，无需 registry 发布）。
    /// 未嵌入时保持 Task 37 语义（workspace:* / registry 版本）。
    /// </summary>
    private static string ResolveVersionSpec(NpmWorkspacePlan plan, NpmProjectPlan project, NpmProjectDependency dependency)
    {
        var versionSpec = dependency.ResolvedVersionSpec;
        if (!plan.EmbedCompatPackage)
        {
            return versionSpec;
        }

        if (dependency.IsWorkspace)
        {
            var member = plan.Projects.FirstOrDefault(p =>
                string.Equals(p.PackageName, dependency.PackageName, StringComparison.Ordinal));
            return member != null ? $"file:../{member.ModuleName}" : versionSpec;
        }

        if (dependency.PackageName == NpmProjectPlan.DefaultCompatPackage)
        {
            return "file:../../compat";
        }

        return versionSpec;
    }

    private static string Serialize(Dictionary<string, object?> content) =>
        JsonSerializer.Serialize(content, JsonOptions);
}
