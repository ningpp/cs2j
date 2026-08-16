using System.Text.Json;

namespace CSharpToJava.TypeMapping.JavaModel;

/// <summary>
/// Loads Java standard-library metadata from the <c>config/java/</c> directory tree.
///
/// <para>Each subdirectory of the root directory corresponds to a Java module (e.g. <c>java.base</c>).
/// Within a module directory the remaining path mirrors the Java package hierarchy
/// (e.g. <c>java/lang/String.json</c>).  Each JSON file contains the metadata for a single
/// public type.</para>
///
/// <para>Loading is lazy at the module level: a module's types are read from disk only when a type
/// from that module is first requested via <see cref="JavaLibraryIndex"/>.</para>
/// </summary>
public sealed class JavaLibraryLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _rootDirectory;

    /// <summary>
    /// Initialises a new loader that reads metadata from <paramref name="rootDirectory"/>.
    /// </summary>
    /// <param name="rootDirectory">
    /// Absolute or relative path to the root <c>config/java/</c> directory.  Each immediate
    /// child directory is treated as a module name.
    /// </param>
    public JavaLibraryLoader(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = rootDirectory;
    }

    /// <summary>
    /// Returns the names of all modules found in the root directory.
    /// </summary>
    public IReadOnlyList<string> DiscoverModuleNames()
    {
        if (!Directory.Exists(_rootDirectory))
            return [];

        return Directory.GetDirectories(_rootDirectory)
                        .Select(Path.GetFileName)
                        .Where(name => !string.IsNullOrEmpty(name))
                        .Select(name => name!)
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToList();
    }

    /// <summary>
    /// Loads all types for a single module and returns a <see cref="JavaModule"/> instance.
    /// Returns <see langword="null"/> if the module directory does not exist.
    /// </summary>
    public JavaModule? LoadModule(string moduleName)
    {
        var moduleDir = Path.Combine(_rootDirectory, moduleName);
        if (!Directory.Exists(moduleDir))
            return null;

        var types = LoadTypesFromDirectory(moduleDir);
        return new JavaModule(moduleName, types);
    }

    /// <summary>
    /// Eagerly loads every module and returns a fully-populated <see cref="JavaLibrary"/>.
    /// </summary>
    public JavaLibrary LoadAll()
    {
        var modules = DiscoverModuleNames()
            .Select(LoadModule)
            .Where(m => m is not null)
            .Select(m => m!);
        return new JavaLibrary(modules);
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private IEnumerable<JavaTypeInfo> LoadTypesFromDirectory(string directory)
    {
        foreach (var jsonFile in EnumerateJsonFiles(directory))
        {
            var typeInfo = TryLoadTypeInfo(jsonFile);
            if (typeInfo is not null)
                yield return typeInfo;
        }
    }

    private static IEnumerable<string> EnumerateJsonFiles(string directory)
        => Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories);

    private static JavaTypeInfo? TryLoadTypeInfo(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<JavaTypeInfo>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Skip unreadable or malformed files — they should not block the rest.
            return null;
        }
    }
}
