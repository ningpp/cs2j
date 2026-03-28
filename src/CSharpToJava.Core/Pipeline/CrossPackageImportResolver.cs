using CSharpToJava.Core.Context;
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
        "XmlNodeType",
        "ReadState",
        "XmlConvert",
        "XmlReaderSettings",
        "XmlWriterSettings",
        "XmlReader",
        "XmlTextReader",
        "XmlWriter",
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
        "TextReader",
        "ThreadHelper",
        "ArrayHelper"
    };

    /// <summary>
    /// Post-processes all generated Java files by adding wildcard imports for every MSAGL package.
    /// This ensures any class can reference any other MSAGL class without needing fully-qualified names.
    /// </summary>
    public static void AddCrossPackageImports(List<ConversionResult> results, string? sharedCompatibilityPackage = null)
    {
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

        if (!string.IsNullOrWhiteSpace(sharedCompatibilityPackage)
            && !allPackages.Contains(CompatibilityClassGenerator.MSTestCompatibilityPackage, StringComparer.Ordinal))
        {
            allPackages.Add(CompatibilityClassGenerator.MSTestCompatibilityPackage);
            allPackages.Sort(StringComparer.Ordinal);
        }

        if (allPackages.Count == 0) return;

        // Build a map of simple class name -> list of packages containing that class.
        // Used to resolve ambiguity when the same class name appears in multiple MSAGL packages.
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

        // Classes appearing in exactly one MSAGL package that conflict with java.util.*
        // (Set, Iterator, etc.) need an explicit import so the MSAGL class takes precedence.
        var javaUtilNames = new HashSet<string> { "Set", "Iterator", "AbstractSet", "AbstractMap", "Timer" };
        var javaUtilConflictImport = new Dictionary<string, string>(); // className → MSAGL pkg
        foreach (var (cn, pkgs) in classNameToPackages)
        {
            if (pkgs.Count == 1 && javaUtilNames.Contains(cn))
                javaUtilConflictImport[cn] = pkgs[0];
        }

        // Classes appearing in 2+ MSAGL packages: for each file, pick the "preferred" package.
        // Heuristic: the canonical package is the one with the shortest fully-qualified name
        // (i.e. the most central/core package).
        var msaglConflicts = classNameToPackages
            .Where(kv => kv.Value.Count > 1)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var msaglConflictCanonical = msaglConflicts
            .ToDictionary(kv => kv.Key,
                kv => kv.Value.OrderBy(p => p.Length).ThenBy(p => p).First());

        // Build the cross-package import block
        var crossImports = new System.Text.StringBuilder();
        foreach (var pkg in allPackages)
            crossImports.AppendLine($"import {pkg}.*;");
        var crossImportBlock = crossImports.ToString();

        // Prepend cross-package imports after the first existing import/package lines
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (string.IsNullOrEmpty(r.GeneratedCode)) continue;
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
            // These explicit imports take precedence over the java.util.* wildcard
            foreach (var (className, pkg) in javaUtilConflictImport)
            {
                if (r.Package != pkg)  // Don't add explicit import for the class's own package
                    toInsert.Add($"import {pkg}.{className};");
            }

            // Add explicit single-type-imports for MSAGL-MSAGL class name conflicts.
            // Files in one of the conflicting packages import their own version;
            // all other files import the canonical (shortest-named) package's version.
            foreach (var (className, packages) in msaglConflicts)
            {
                var preferredPkg = packages.Contains(r.Package!)
                    ? r.Package!
                    : msaglConflictCanonical[className];
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
}
