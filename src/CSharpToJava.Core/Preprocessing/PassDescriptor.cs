namespace CSharpToJava.Core.Preprocessing;

/// <summary>
/// Logical pipeline layer a preprocess pass belongs to. The layer is informational
/// (used by --list-passes and future batching); execution order today is determined
/// solely by <see cref="PassDescriptor.RunAfterIds"/>.
/// </summary>
public enum PassStage
{
    /// <summary>Lexical / directive normalization; pure syntax, no semantic model.</summary>
    Lexical,

    /// <summary>Declaration-level rewrites (members, generated types, structs).</summary>
    Declarative,

    /// <summary>Expression-level sugar rewrites.</summary>
    Expression,

    /// <summary>Statement / control-flow desugaring.</summary>
    ControlFlow,

    /// <summary>State-machine / closure generation (iterators, async, display classes).</summary>
    StateMachine,

    /// <summary>Type-model / ABI decisions (generics erasure, unsafe, platform types).</summary>
    Abi,

    /// <summary>Naming-safety pass that must run last, immediately before conversion.</summary>
    FinalNaming,
}

/// <summary>Implementation status of a registered pass.</summary>
public enum PassStatus
{
    /// <summary>Fully implemented and executable.</summary>
    Implemented,

    /// <summary>
    /// Placeholder registration. Disabled stubs cost nothing and never touch the tree;
    /// explicitly enabling a stub fails loudly with CS2J9001 instead of silently no-opping.
    /// </summary>
    Stub,
}

/// <summary>
/// Support library project a pass injects into the intermediate tree. The CLI uses
/// this both for injection orchestration and for filtering the library out of the
/// later Java conversion / fingerprint snapshot.
/// </summary>
public enum PassSupportLibrary
{
    None,
    Cs2jPrelude,
    Cs2jTuples,
    Cs2jUnsignedMath,
    Cs2jArray,
    Cs2jRefOutIn,
}

/// <summary>
/// Immutable metadata describing one C# → C# preprocessing pass. The registry is the
/// single source of truth for pass identity, ordering, defaults and CLI surface;
/// adding a future preprocessor starts with registering one descriptor.
/// </summary>
public sealed record PassDescriptor
{
    public PassDescriptor(
        string id,
        string displayName,
        string intermediateDirectoryName,
        PassStage stage,
        PassStatus status,
        bool defaultEnabled,
        IReadOnlyList<string>? runAfterIds = null,
        PassSupportLibrary supportLibrary = PassSupportLibrary.None,
        bool needsSemanticModel = true,
        bool verifyBuildByDefault = false,
        bool verifyTestsByDefault = false,
        string? description = null,
        string? catalogItems = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Pass id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Pass display name is required.", nameof(displayName));
        if (string.IsNullOrWhiteSpace(intermediateDirectoryName))
            throw new ArgumentException("Intermediate directory name is required.", nameof(intermediateDirectoryName));

        Id = id;
        DisplayName = displayName;
        IntermediateDirectoryName = intermediateDirectoryName;
        Stage = stage;
        Status = status;
        DefaultEnabled = defaultEnabled;
        RunAfterIds = runAfterIds ?? Array.Empty<string>();
        SupportLibrary = supportLibrary;
        NeedsSemanticModel = needsSemanticModel;
        VerifyBuildByDefault = verifyBuildByDefault;
        VerifyTestsByDefault = verifyTestsByDefault;
        Description = description;
        CatalogItems = catalogItems;
    }

    /// <summary>Stable kebab-case identifier; equals the CLI <c>--enable-pass</c> / <c>--pass</c> name.</summary>
    public string Id { get; }

    /// <summary>Human-readable name shown by <c>--list-passes</c> and logs.</summary>
    public string DisplayName { get; }

    /// <summary>Intermediate tree directory (typically <c>.cs2j-&lt;id&gt;-src</c>).</summary>
    public string IntermediateDirectoryName { get; }

    /// <summary>Pipeline layer (informational).</summary>
    public PassStage Stage { get; }

    /// <summary>Implemented or placeholder.</summary>
    public PassStatus Status { get; }

    /// <summary>Whether the pass runs in <c>convert-project</c> when no explicit toggle is given.</summary>
    public bool DefaultEnabled { get; }

    /// <summary>Ordering contract: this pass runs after every listed pass id.</summary>
    public IReadOnlyList<string> RunAfterIds { get; }

    /// <summary>Support library injected/filtered by this pass, if any.</summary>
    public PassSupportLibrary SupportLibrary { get; }

    /// <summary>Whether the runner builds a whole-tree CSharpCompilation for this pass.</summary>
    public bool NeedsSemanticModel { get; }

    /// <summary>Default for intermediate-tree <c>dotnet build</c> verification.</summary>
    public bool VerifyBuildByDefault { get; }

    /// <summary>Default for intermediate-tree <c>dotnet test</c> verification.</summary>
    public bool VerifyTestsByDefault { get; }

    /// <summary>One-line description for <c>--list-passes</c>.</summary>
    public string? Description { get; }

    /// <summary>Preprocessor-catalog candidate ids covered by this pass (e.g. "C03, C37"), informational.</summary>
    public string? CatalogItems { get; }
}
