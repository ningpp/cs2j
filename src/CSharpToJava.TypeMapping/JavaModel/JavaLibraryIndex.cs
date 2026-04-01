namespace CSharpToJava.TypeMapping.JavaModel;

/// <summary>
/// Provides a queryable, lazily-populated index over the Java standard-library metadata loaded
/// by <see cref="JavaLibraryLoader"/>.
///
/// <para>The index is lazy at the module level: a module's types are loaded from disk only when a
/// type belonging to that module is first requested.  Subsequent lookups into the same module are
/// served from an in-memory cache with <c>O(1)</c> dictionary lookups.</para>
/// </summary>
public sealed class JavaLibraryIndex
{
    private readonly JavaLibraryLoader _loader;

    // canonical name → type (populated lazily, one module at a time)
    private readonly Dictionary<string, JavaTypeInfo> _typeIndex = new(StringComparer.Ordinal);

    // module name → loaded flag (false = not yet loaded)
    private readonly Dictionary<string, bool> _loadedModules;

    // canonical name → module name (built while loading)
    private readonly Dictionary<string, string> _typeToModule = new(StringComparer.Ordinal);

    private readonly object _lock = new();

    /// <summary>
    /// Creates an index that reads metadata from <paramref name="rootDirectory"/>.
    /// </summary>
    /// <param name="rootDirectory">
    /// Path to the root directory that contains one sub-directory per Java module
    /// (e.g. <c>config/java/</c>).
    /// </param>
    public JavaLibraryIndex(string rootDirectory)
    {
        _loader = new JavaLibraryLoader(rootDirectory);

        // Discover module names up-front so we know which modules exist without loading them.
        _loadedModules = _loader.DiscoverModuleNames()
                                .ToDictionary(n => n, _ => false, StringComparer.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Public query API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Looks up a type by its canonical Java name (e.g. <c>"java.lang.String"</c>).
    /// Returns <see langword="null"/> if the type is not found in the metadata.
    /// </summary>
    public JavaTypeInfo? FindType(string canonicalName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalName);

        // Fast path: already indexed.
        lock (_lock)
        {
            if (_typeIndex.TryGetValue(canonicalName, out var cached))
                return cached;
        }

        // Determine which module this type likely belongs to and load it.
        EnsureModuleLoaded(GuessModule(canonicalName));

        lock (_lock)
        {
            return _typeIndex.TryGetValue(canonicalName, out var t) ? t : null;
        }
    }

    /// <summary>
    /// Returns all public methods with the given <paramref name="methodName"/> declared on
    /// <paramref name="typeName"/>.  Returns an empty array when the type is not found or has no
    /// matching methods.
    /// </summary>
    public IReadOnlyList<JavaMethodInfo> FindMethods(string typeName, string methodName)
    {
        var typeInfo = FindType(typeName);
        if (typeInfo is null)
            return [];

        return typeInfo.DeclaredPublicMethods
                       .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
                       .ToList();
    }

    /// <summary>
    /// Returns all public constructors declared on <paramref name="typeName"/>.
    /// Returns an empty array when the type is not found.
    /// </summary>
    public IReadOnlyList<JavaConstructorInfo> FindConstructors(string typeName)
    {
        var typeInfo = FindType(typeName);
        return typeInfo?.DeclaredPublicConstructors ?? [];
    }

    /// <summary>
    /// Returns the complete set of supertypes (super-class + all interfaces, recursively) for
    /// <paramref name="typeName"/>, in breadth-first order.  The type itself is not included.
    /// Returns an empty list when the type is not found.
    /// </summary>
    public IReadOnlyList<string> GetSupertypes(string typeName)
    {
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        EnqueueDirectSupertypes(typeName, queue, visited, result);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            EnqueueDirectSupertypes(current, queue, visited, result);
        }

        return result;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="from"/> is assignable to
    /// <paramref name="to"/> according to the Java inheritance metadata.
    ///
    /// <para>A type is always assignable to itself.  Otherwise the full supertype chain of
    /// <paramref name="from"/> is checked.</para>
    /// </summary>
    public bool IsAssignableTo(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
            return true;

        return GetSupertypes(from).Contains(to, StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns the Java module that contains <paramref name="typeName"/>, or
    /// <see langword="null"/> if the type is not found in the metadata.
    /// </summary>
    public string? GetModuleFor(string typeName)
    {
        // Make sure the type is loaded.
        FindType(typeName);

        lock (_lock)
        {
            return _typeToModule.TryGetValue(typeName, out var m) ? m : null;
        }
    }

    /// <summary>
    /// Returns the names of all known modules discovered in the root directory.
    /// </summary>
    public IReadOnlyList<string> KnownModuleNames
    {
        get
        {
            lock (_lock)
            {
                return _loadedModules.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            }
        }
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private void EnqueueDirectSupertypes(
        string typeName,
        Queue<string> queue,
        HashSet<string> visited,
        List<string> result)
    {
        var typeInfo = FindType(typeName);
        if (typeInfo is null)
            return;

        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(typeInfo.SuperClass))
            candidates.Add(typeInfo.SuperClass);
        candidates.AddRange(typeInfo.Interfaces);

        foreach (var supertype in candidates)
        {
            if (visited.Add(supertype))
            {
                result.Add(supertype);
                queue.Enqueue(supertype);
            }
        }
    }

    /// <summary>
    /// Heuristic: the module name is the first two dot-separated segments of the canonical name
    /// if they look like a Java module (e.g. <c>java.lang.String</c> → <c>java.base</c> is not
    /// directly inferable, so we fall back to loading all unloaded modules until the type is
    /// found).
    ///
    /// <para>In practice this means:
    /// <list type="bullet">
    ///   <item>Types in <c>java.*</c> packages are in <c>java.base</c> (first attempt).</item>
    ///   <item>Types in <c>javax.*</c> packages trigger a broader search.</item>
    /// </list>
    /// </para>
    /// </summary>
    private string? GuessModule(string canonicalName)
    {
        // java.lang.*, java.util.*, java.io.*, etc. → java.base
        if (canonicalName.StartsWith("java.", StringComparison.Ordinal))
            return "java.base";

        // javax.xml.* → java.xml
        if (canonicalName.StartsWith("javax.xml.", StringComparison.Ordinal))
            return "java.xml";

        // javax.naming.* → java.naming
        if (canonicalName.StartsWith("javax.naming.", StringComparison.Ordinal))
            return "java.naming";

        // javax.sql.* → java.sql
        if (canonicalName.StartsWith("javax.sql.", StringComparison.Ordinal))
            return "java.sql";

        // javax.script.* → java.scripting
        if (canonicalName.StartsWith("javax.script.", StringComparison.Ordinal))
            return "java.scripting";

        // No confident guess — caller will load all remaining modules.
        return null;
    }

    private void EnsureModuleLoaded(string? moduleName)
    {
        if (moduleName is not null)
        {
            bool needsLoading;
            lock (_lock)
            {
                needsLoading = _loadedModules.TryGetValue(moduleName, out bool loaded) && !loaded;
            }

            if (needsLoading)
            {
                LoadModule(moduleName);
                return;
            }
        }

        // If we don't know the module or it's already loaded, load all remaining modules.
        LoadAllUnloaded();
    }

    private void LoadModule(string moduleName)
    {
        lock (_lock)
        {
            // Double-checked locking.
            if (_loadedModules.TryGetValue(moduleName, out bool loaded) && loaded)
                return;
        }

        var module = _loader.LoadModule(moduleName);

        lock (_lock)
        {
            if (module is not null)
            {
                foreach (var typeInfo in module.TypesByCanonicalName.Values)
                {
                    _typeIndex[typeInfo.CanonicalName] = typeInfo;
                    _typeToModule[typeInfo.CanonicalName] = moduleName;
                }
            }

            _loadedModules[moduleName] = true;
        }
    }

    private void LoadAllUnloaded()
    {
        List<string> toLoad;
        lock (_lock)
        {
            toLoad = _loadedModules.Where(kv => !kv.Value).Select(kv => kv.Key).ToList();
        }

        foreach (var moduleName in toLoad)
        {
            LoadModule(moduleName);
        }
    }
}
