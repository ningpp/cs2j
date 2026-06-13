using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Pipeline.Compatibility;
using System.Text.RegularExpressions;

namespace CSharpToJava.Core.Pipeline;

/// <summary>
/// Resolves cross-package imports for generated Java files.
/// Adds wildcard imports so all converted types can see each other.
/// Extracted from ProjectConversionPipeline for maintainability.
/// </summary>
public static class CrossPackageImportResolver
{
    internal static readonly string[] SharedCompatibilityHelperClassNames =
    {
        "IntHolder",
        "LongHolder",
        "DoubleHolder",
        "FloatHolder",
        "BoolHolder",
        "CharHolder",
        "ShortHolder",
        "ByteHolder",
        "ObjectHolder",
        "StopwatchHelper",
        "JsonSerializerOptions",
        "JsonSerializer",
        "MathHelper",
        "StringHelper",
        "EnumHelper",
        "FileHelper",
        "FileMode",
        "CultureInfo",
        "LinkedListNode",
        "LinkedListWithNodes",
        "InvalidDataException",
        "MemoryStream",
        "NumberStyles",
        "FileAccess",
        "StreamReader",
        "StreamWriter",
        "TextReader",
        "ThreadHelper",
        "ArrayHelper",
        "DrawingColor",
        "StreamWrapper",
        "IPAddressHelper"
    };

    private static readonly HashSet<string> DefaultWildcardPackages = new(StringComparer.Ordinal)
    {
        "java.util",
        "java.util.function",
        "java.util.stream",
        "java.io"
    };

    /// <summary>
    /// Post-processes all generated Java files by adding wildcard imports for every package.
    /// This ensures any class can reference any other class without needing fully-qualified names.
    /// Uses the preserved IR (JavaCompilationUnit) when available; falls back to string
    /// manipulation for compatibility-generated files that don't have an IR.
    /// </summary>
    public static void AddCrossPackageImports(List<ConversionResult> results, string? sharedCompatibilityPackage = null)
    {
        sharedCompatibilityPackage = ResolveSharedCompatibilityPackage(sharedCompatibilityPackage);

        // Collect all unique non-null packages from generated results
        var allPackages = results
            .Where(r => !string.IsNullOrEmpty(r.Package))
            .Select(r => r.Package!)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        if (!string.IsNullOrWhiteSpace(sharedCompatibilityPackage)
            && !allPackages.Contains(sharedCompatibilityPackage, StringComparer.Ordinal))
        {
            allPackages.Add(sharedCompatibilityPackage);
            allPackages.Sort(StringComparer.Ordinal);
        }

        if (allPackages.Count == 0) return;

        // Build a map of simple class name -> list of packages containing that class.
        // Used to resolve ambiguity when the same class name appears in multiple packages.
        var classNameToPackages = new Dictionary<string, List<string>>();
        foreach (var r in results)
        {
            if (string.IsNullOrEmpty(r.Package) || string.IsNullOrEmpty(r.FileName)) continue;
            var className = System.IO.Path.GetFileNameWithoutExtension(r.FileName);
            if (string.IsNullOrEmpty(className)) continue;
            if (!classNameToPackages.TryGetValue(className, out var pkgList))
                classNameToPackages[className] = pkgList = new List<string>();
            pkgList.Add(r.Package!);
        }

        // Classes appearing in exactly one package that conflict with java.util.*
        // (Set, Iterator, etc.) need an explicit import so our class takes precedence.
        var javaUtilNames = new HashSet<string> { "Set", "Iterator", "AbstractSet", "AbstractMap", "Timer" };
        var javaUtilConflictImport = new Dictionary<string, string>(); // className → pkg
        foreach (var (cn, pkgs) in classNameToPackages)
        {
            if (pkgs.Count == 1 && javaUtilNames.Contains(cn))
                javaUtilConflictImport[cn] = pkgs[0];
        }

        // Classes appearing in 2+ packages: for each file, pick the "preferred" package.
        // Heuristic: the canonical package is the one with the shortest fully-qualified name.
        var multiPackageConflicts = classNameToPackages
            .Where(kv => kv.Value.Count > 1)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var conflictCanonical = multiPackageConflicts
            .ToDictionary(kv => kv.Key,
                kv => kv.Value.OrderBy(p => p.Length).ThenBy(p => p).First());

        // Build the shared compatibility helper name set for efficient lookup
        var helperClassNameSet = new HashSet<string>(SharedCompatibilityHelperClassNames, StringComparer.Ordinal);

        var javaLangClassNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Boolean", "Byte", "Character", "Class", "Double", "Enum", "Exception",
            "Float", "Integer", "Long", "Math", "Object", "RuntimeException", "Short",
            "String", "StringBuilder", "System", "Thread", "Throwable", "Void"
        };

        // Process each result
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (string.IsNullOrEmpty(r.GeneratedCode)) continue;

            if (r.Compilation is not null)
            {
                // ── IR path: modify JavaCompilationUnit.Imports directly ──
                AddCrossPackageImportsToIR(r, allPackages, classNameToPackages, javaUtilConflictImport,
                    multiPackageConflicts, conflictCanonical,
                    sharedCompatibilityPackage, helperClassNameSet, javaLangClassNames);
                r.SyncGeneratedCodeFromIR();
            }
            else
            {
                // ── Fallback string path: for files without preserved IR ──
                AddCrossPackageImportsViaString(r, allPackages, javaUtilConflictImport,
                    multiPackageConflicts, conflictCanonical,
                    sharedCompatibilityPackage);
            }
        }
    }

    private static string? ResolveSharedCompatibilityPackage(string? sharedCompatibilityPackage)
    {
        return string.IsNullOrWhiteSpace(sharedCompatibilityPackage)
            ? null
            : CompatibilityRuntime.JavaPackage;
    }

    /// <summary>
    /// Adds cross-package imports by modifying the IR's import list.
    /// </summary>
    private static void AddCrossPackageImportsToIR(
        ConversionResult r,
        List<string> allPackages,
        Dictionary<string, List<string>> classNameToPackages,
        Dictionary<string, string> javaUtilConflictImport,
        Dictionary<string, List<string>> multiPackageConflicts,
        Dictionary<string, string> conflictCanonical,
        string? sharedCompatibilityPackage,
        HashSet<string> helperClassNameSet,
        HashSet<string> javaLangClassNames)
    {
        var cu = r.Compilation!;

        // Rewrite helper class imports to use shared compatibility package
        if (!string.IsNullOrWhiteSpace(sharedCompatibilityPackage))
        {
            for (int j = 0; j < cu.Imports.Count; j++)
            {
                var imp = cu.Imports[j];
                if (imp.IsWildcard || imp.IsStatic) continue;

                // Extract simple class name from fully-qualified import
                var lastDot = imp.Name.LastIndexOf('.');
                if (lastDot < 0) continue;
                var simpleName = imp.Name[(lastDot + 1)..];

                if (helperClassNameSet.Contains(simpleName))
                {
                    cu.Imports[j] = new JavaImport($"{sharedCompatibilityPackage}.{simpleName}");
                }
            }
        }

        // Add cross-package wildcard imports (skip own package)
        foreach (var pkg in allPackages)
        {
            if (pkg == r.Package) continue;
            cu.Imports.Add(new JavaImport(pkg, isWildcard: true));
        }

        // Add explicit single-type imports for java.util.* conflicts
        foreach (var (className, pkg) in javaUtilConflictImport)
        {
            if (r.Package != pkg)
                cu.Imports.Add(new JavaImport($"{pkg}.{className}"));
        }

        // Add explicit single-type imports for cross-package class name conflicts
        foreach (var (className, packages) in multiPackageConflicts)
        {
            var preferredPkg = packages.Contains(r.Package!)
                ? r.Package!
                : conflictCanonical[className];
            cu.Imports.Add(new JavaImport($"{preferredPkg}.{className}"));
        }

        RemoveAmbiguousSingleTypeImports(cu, classNameToPackages, javaLangClassNames);
    }

    private static void RemoveAmbiguousSingleTypeImports(
        JavaCompilationUnit cu,
        Dictionary<string, List<string>> classNameToPackages,
        HashSet<string> javaLangClassNames)
    {
        var deduped = new List<JavaImport>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var import in cu.Imports)
        {
            if (!import.IsWildcard && !import.IsStatic)
            {
                var packageName = PackageName(import.Name);
                var simpleName = SimpleName(import.Name);
                if (packageName == cu.Package)
                    continue;

                if (!string.IsNullOrWhiteSpace(cu.Package)
                    && classNameToPackages.TryGetValue(simpleName, out var packages)
                    && packages.Contains(cu.Package, StringComparer.Ordinal))
                {
                    continue;
                }
            }

            var key = ImportKey(import);
            if (seen.Add(key))
                deduped.Add(import);
        }

        var selectedExplicitImportKeys = deduped
            .Where(i => !i.IsWildcard && !i.IsStatic)
            .GroupBy(i => SimpleName(i.Name), StringComparer.Ordinal)
            .Select(g => SelectPreferredExplicitImport(g.ToList(), javaLangClassNames))
            .Where(i => i is not null)
            .Select(i => ImportKey(i!))
            .ToHashSet(StringComparer.Ordinal);

        cu.Imports.Clear();
        foreach (var import in deduped)
        {
            if (import.IsWildcard || import.IsStatic || selectedExplicitImportKeys.Contains(ImportKey(import)))
                cu.Imports.Add(import);
        }
    }

    private static JavaImport? SelectPreferredExplicitImport(List<JavaImport> imports, HashSet<string> javaLangClassNames)
    {
        if (imports.Count == 0)
            return null;

        if (imports.Count == 1)
            return javaLangClassNames.Contains(SimpleName(imports[0].Name))
                && PackageName(imports[0].Name) == "java.lang"
                    ? null
                    : imports[0];

        return imports
            .OrderBy(i => DefaultWildcardPackages.Contains(PackageName(i.Name)) ? 1 : 0)
            .ThenBy(i => PackageName(i.Name) == "java.lang" ? 1 : 0)
            .ThenBy(i => i.Name.Length)
            .ThenBy(i => i.Name, StringComparer.Ordinal)
            .First();
    }

    private static string ImportKey(JavaImport import)
    {
        return $"{(import.IsStatic ? "static:" : "type:")}{(import.IsWildcard ? "wild:" : "single:")}{import.Name}";
    }

    private static string SimpleName(string importName)
    {
        var lastDot = importName.LastIndexOf('.');
        return lastDot >= 0 ? importName[(lastDot + 1)..] : importName;
    }

    private static string PackageName(string importName)
    {
        var lastDot = importName.LastIndexOf('.');
        return lastDot > 0 ? importName[..lastDot] : "";
    }

    /// <summary>
    /// Fallback: adds cross-package imports via string manipulation for files without preserved IR
    /// (e.g. compatibility-generated helper classes).
    /// </summary>
    private static void AddCrossPackageImportsViaString(
        ConversionResult r,
        List<string> allPackages,
        Dictionary<string, string> javaUtilConflictImport,
        Dictionary<string, List<string>> multiPackageConflicts,
        Dictionary<string, string> conflictCanonical,
        string? sharedCompatibilityPackage)
    {
        // Build the cross-package import block (skip own package)
        var crossImports = new System.Text.StringBuilder();
        foreach (var pkg in allPackages)
        {
            if (pkg == r.Package) continue;
            crossImports.AppendLine($"import {pkg}.*;");
        }
        var crossImportBlock = crossImports.ToString();

        // Find insertion point: after the last existing import/package line
        var lines = r.GeneratedCode.Split('\n').ToList();
        int lastImportIdx = -1;
        for (int li = 0; li < lines.Count; li++)
        {
            var trimmed = lines[li].TrimStart();
            if (trimmed.StartsWith("import ") || trimmed.StartsWith("package "))
                lastImportIdx = li;
            else if (lastImportIdx >= 0 && !string.IsNullOrWhiteSpace(trimmed))
                break;
        }
        // Insert cross-package imports after lastImportIdx
        var insertAt = lastImportIdx >= 0 ? lastImportIdx + 1 : 0;
        var toInsert = crossImportBlock.Split('\n').Select(l => l.TrimEnd()).ToList();

        // Add explicit single-type-imports for classes that conflict with java.util.*
        foreach (var (className, pkg) in javaUtilConflictImport)
        {
            if (r.Package != pkg)
                toInsert.Add($"import {pkg}.{className};");
        }

        // Add explicit single-type-imports for cross-package class name conflicts
        foreach (var (className, packages) in multiPackageConflicts)
        {
            var preferredPkg = packages.Contains(r.Package!)
                ? r.Package!
                : conflictCanonical[className];
            toInsert.Add($"import {preferredPkg}.{className};");
        }

        lines.InsertRange(insertAt, toInsert);
        r.GeneratedCode = string.Join("\n", lines);

        if (!string.IsNullOrWhiteSpace(sharedCompatibilityPackage))
        {
            foreach (var helperClassName in SharedCompatibilityHelperClassNames)
            {
                r.GeneratedCode = Regex.Replace(
                    r.GeneratedCode,
                    $@"^import\s+[A-Za-z0-9_.]+\.{helperClassName};\s*$",
                    $"import {sharedCompatibilityPackage}.{helperClassName};",
                    RegexOptions.Multiline);
            }
        }
    }
}
