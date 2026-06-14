namespace CSharpToJava.Core.Pipeline.Planning;

/// <summary>
/// 从已知信息构造 <see cref="JavaWorkspacePlan"/> 的构建器。
/// 提供从低层数据（模块名/依赖列表）转换为完整输出规划的桥接。
/// </summary>
public sealed class WorkspacePlanBuilder
{
    private string _groupId = "com.example";
    private string _artifactId = "project";
    private string _version = "1.0-SNAPSHOT";
    private string _javaVersion = "Java25";
    private readonly List<JavaModulePlan> _modules = [];

    public WorkspacePlanBuilder GroupId(string groupId) { _groupId = groupId; return this; }
    public WorkspacePlanBuilder ArtifactId(string artifactId) { _artifactId = artifactId; return this; }
    public WorkspacePlanBuilder Version(string version) { _version = version; return this; }
    public WorkspacePlanBuilder JavaVersion(string javaVersion) { _javaVersion = javaVersion; return this; }

    /// <summary>
    /// 添加一个模块到规划中。
    /// </summary>
    public WorkspacePlanBuilder AddModule(JavaModulePlan module)
    {
        _modules.Add(module);
        return this;
    }

    /// <summary>
    /// 构建单模块规划。
    /// </summary>
    public WorkspacePlanBuilder AddSingleModule(
        bool hasTests,
        IReadOnlyList<JavaDependency>? dependencies = null,
        IReadOnlyList<string>? compatPacks = null,
        IReadOnlyList<JavaRuntimeBridgeRequirement>? runtimeBridges = null)
    {
        _modules.Add(new JavaModulePlan
        {
            ModuleName = _artifactId,
            IsTestOnly = false,
            SourceSets = new JavaSourceSets
            {
                TestSources = hasTests ? ["test"] : [],
            },
            Dependencies = dependencies ?? DefaultDependencies(),
            RequiredCompatPacks = compatPacks ?? [],
            RequiredRuntimeBridges = runtimeBridges ?? [],
        });
        return this;
    }

    public JavaWorkspacePlan Build() => new()
    {
        GroupId = _groupId,
        ArtifactId = _artifactId,
        Version = _version,
        JavaVersion = _javaVersion,
        Modules = _modules.AsReadOnly(),
        RequiredRuntimeBridges = MergeRuntimeBridges(_modules.SelectMany(module => module.RequiredRuntimeBridges)),
    };

    /// <summary>
    /// 默认依赖：vavr + jackson-databind + csharptojava-compat（当前所有项目都需要）。
    /// </summary>
    public static IReadOnlyList<JavaDependency> DefaultDependencies() =>
    [
        new() { GroupId = "io.vavr", ArtifactId = "vavr", Version = "0.10.4" },
        new() { GroupId = "com.fasterxml.jackson.core", ArtifactId = "jackson-databind", Version = "2.17.2" },
        new() { GroupId = "io.github.ningpp", ArtifactId = "csharptojava-compat", Version = "1.0-SNAPSHOT" },
        new() { GroupId = "io.github.ningpp", ArtifactId = "System.Private.Uri", Version = "0.0.1-SNAPSHOT" },
        new() { GroupId = "io.github.ningpp", ArtifactId = "System.Private.Xml", Version = "0.0.1-SNAPSHOT" },
    ];

    /// <summary>
    /// 从 Maven 坐标字符串构建外部依赖。
    /// 约定格式为 groupId:artifactId:version。
    /// </summary>
    public static JavaDependency ExternalDependency(string coordinate, JavaDependencyScope scope = JavaDependencyScope.Compile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coordinate);

        var parts = coordinate.Split(':');
        if (parts.Length < 3)
        {
            throw new ArgumentException($"Unsupported Maven coordinate '{coordinate}'. Expected 'groupId:artifactId:version'.", nameof(coordinate));
        }

        return new JavaDependency
        {
            GroupId = parts[0],
            ArtifactId = parts[1],
            Version = string.Join(':', parts.Skip(2)),
            Scope = scope,
        };
    }

    /// <summary>
    /// 合并并去重依赖，保持首个依赖的版本与顺序。
    /// </summary>
    public static IReadOnlyList<JavaDependency> MergeDependencies(IEnumerable<JavaDependency> dependencies)
    {
        var merged = new List<JavaDependency>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dependency in dependencies)
        {
            var key = $"{dependency.GroupId}|{dependency.ArtifactId}|{dependency.Scope}|{dependency.IsInternal}";
            if (seen.Add(key))
            {
                merged.Add(dependency);
            }
        }

        return merged;
    }

    public static IReadOnlyList<JavaRuntimeBridgeRequirement> MergeRuntimeBridges(
        IEnumerable<JavaRuntimeBridgeRequirement> runtimeBridges)
    {
        var merged = new List<JavaRuntimeBridgeRequirement>();
        var indexByBridgeId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var runtimeBridge in runtimeBridges)
        {
            if (!indexByBridgeId.TryGetValue(runtimeBridge.BridgeId, out var index))
            {
                indexByBridgeId[runtimeBridge.BridgeId] = merged.Count;
                merged.Add(CloneRuntimeBridge(runtimeBridge));
                continue;
            }

            var existing = merged[index];
            merged[index] = new JavaRuntimeBridgeRequirement
            {
                BridgeId = existing.BridgeId,
                Description = existing.Description,
                RequiredCompatPacks = existing.RequiredCompatPacks
                    .Concat(runtimeBridge.RequiredCompatPacks)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(pack => pack, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Dependencies = MergeDependencies(existing.Dependencies.Concat(runtimeBridge.Dependencies)),
            };
        }

        return merged;
    }

    /// <summary>
    /// 从模块间依赖名称创建内部模块引用依赖。
    /// </summary>
    public static JavaDependency InternalModuleRef(string groupId, string moduleName, JavaDependencyScope scope = JavaDependencyScope.Compile) =>
        new()
        {
            GroupId = groupId,
            ArtifactId = moduleName,
            Version = "${project.version}",
            Scope = scope,
            IsInternal = true,
        };

    private static JavaRuntimeBridgeRequirement CloneRuntimeBridge(JavaRuntimeBridgeRequirement runtimeBridge)
    {
        return new JavaRuntimeBridgeRequirement
        {
            BridgeId = runtimeBridge.BridgeId,
            Description = runtimeBridge.Description,
            RequiredCompatPacks = runtimeBridge.RequiredCompatPacks
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(pack => pack, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Dependencies = MergeDependencies(runtimeBridge.Dependencies),
        };
    }
}
