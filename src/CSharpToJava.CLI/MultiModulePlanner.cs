namespace CSharpToJava.CLI;

internal sealed class PlannedProjectAssignment
{
    public required DiscoveredProject Project { get; init; }
    public required bool AsTestSources { get; init; }
}

internal sealed class PlannedModule
{
    public required string Name { get; init; }
    public required bool IsTestOnly { get; init; }
    public List<PlannedProjectAssignment> Assignments { get; } = new();
    public HashSet<string> CompileDependencies { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> TestDependencies { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasTestSources => Assignments.Any(a => a.AsTestSources);
}

internal sealed class MultiModulePlan
{
    public required IReadOnlyList<PlannedModule> ModulesInBuildOrder { get; init; }
}

internal static class MultiModulePlanner
{
    public static MultiModulePlan Build(ProjectGraph graph, bool includeTests)
    {
        var modules = new Dictionary<string, PlannedModule>(StringComparer.OrdinalIgnoreCase);
        var moduleByProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var productionProjects = graph.ProjectsInTopologicalOrder
            .Where(p => p.Kind == ProjectKind.Production)
            .ToList();

        foreach (var project in productionProjects)
        {
            var moduleName = MakeUniqueModuleName(project.Name, usedNames);
            var module = new PlannedModule
            {
                Name = moduleName,
                IsTestOnly = false,
            };
            module.Assignments.Add(new PlannedProjectAssignment
            {
                Project = project,
                AsTestSources = false,
            });

            modules[moduleName] = module;
            moduleByProject[project.ProjectFilePath] = moduleName;
        }

        foreach (var project in productionProjects)
        {
            if (!moduleByProject.TryGetValue(project.ProjectFilePath, out var moduleName) || !modules.TryGetValue(moduleName, out var module))
            {
                continue;
            }

            foreach (var refProjectPath in project.ProjectReferences)
            {
                if (moduleByProject.TryGetValue(refProjectPath, out var depModule)
                    && !depModule.Equals(module.Name, StringComparison.OrdinalIgnoreCase))
                {
                    module.CompileDependencies.Add(depModule);
                }
            }
        }

        if (includeTests)
        {
            var testProjects = graph.ProjectsInTopologicalOrder
                .Where(p => p.Kind == ProjectKind.Test)
                .ToList();

            foreach (var testProject in testProjects)
            {
                var referencedProdModules = testProject.ProjectReferences
                    .Where(moduleByProject.ContainsKey)
                    .Select(p => moduleByProject[p])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (referencedProdModules.Count == 1)
                {
                    var target = modules[referencedProdModules[0]];
                    target.Assignments.Add(new PlannedProjectAssignment
                    {
                        Project = testProject,
                        AsTestSources = true,
                    });
                    continue;
                }

                var testModuleName = MakeUniqueModuleName(testProject.Name, usedNames);
                var testModule = new PlannedModule
                {
                    Name = testModuleName,
                    IsTestOnly = true,
                };

                testModule.Assignments.Add(new PlannedProjectAssignment
                {
                    Project = testProject,
                    AsTestSources = true,
                });

                foreach (var dep in referencedProdModules)
                {
                    if (!dep.Equals(testModuleName, StringComparison.OrdinalIgnoreCase))
                    {
                        testModule.TestDependencies.Add(dep);
                    }
                }

                modules[testModuleName] = testModule;
            }
        }

        var ordered = TopologicalModules(modules);
        return new MultiModulePlan
        {
            ModulesInBuildOrder = ordered,
        };
    }

    private static string MakeUniqueModuleName(string rawName, HashSet<string> usedNames)
    {
        var normalized = NormalizeModuleName(rawName);
        if (usedNames.Add(normalized))
        {
            return normalized;
        }

        var index = 2;
        while (true)
        {
            var candidate = $"{normalized}-{index}";
            if (usedNames.Add(candidate))
            {
                return candidate;
            }

            index++;
        }
    }

    private static string NormalizeModuleName(string rawName)
    {
        var chars = rawName.ToLowerInvariant()
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

    private static List<PlannedModule> TopologicalModules(Dictionary<string, PlannedModule> modules)
    {
        var result = new List<PlannedModule>();
        var visited = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in modules.Values)
        {
            Visit(module.Name, modules, visited, result);
        }

        return result;
    }

    private static void Visit(
        string moduleName,
        Dictionary<string, PlannedModule> modules,
        Dictionary<string, int> visited,
        List<PlannedModule> result)
    {
        if (visited.TryGetValue(moduleName, out var state))
        {
            if (state == 2)
            {
                return;
            }

            if (state == 1)
            {
                return;
            }
        }

        visited[moduleName] = 1;

        if (modules.TryGetValue(moduleName, out var module))
        {
            foreach (var dep in module.CompileDependencies.Concat(module.TestDependencies))
            {
                if (modules.ContainsKey(dep))
                {
                    Visit(dep, modules, visited, result);
                }
            }

            result.Add(module);
        }

        visited[moduleName] = 2;
    }
}
