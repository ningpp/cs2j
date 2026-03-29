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
        IReadOnlyList<string>? compatPacks = null)
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
    };

    /// <summary>
    /// 默认依赖：vavr + jackson-databind（当前所有项目都需要）。
    /// </summary>
    public static IReadOnlyList<JavaDependency> DefaultDependencies() =>
    [
        new() { GroupId = "io.vavr", ArtifactId = "vavr", Version = "0.10.4" },
        new() { GroupId = "com.fasterxml.jackson.core", ArtifactId = "jackson-databind", Version = "2.17.2" },
    ];

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
}
