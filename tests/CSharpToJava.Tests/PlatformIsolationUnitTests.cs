using CSharpToJava.Core.Analysis;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.PlatformIsolation;
using CSharpToJava.TypeMapping;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CoreDiagnosticSeverity = CSharpToJava.Core.Context.DiagnosticSeverity;

namespace CSharpToJava.Tests;

/// <summary>
/// C42 M1 Core 审计基建单测（设计 §7.1 M1 相关项）：BoundaryInventory 分类矩阵、
/// compat/AssemblyInfo/NativeMethods 豁免、测试工程探针 Warning 降级、报告排序与
/// 字节稳定、InteropBoundaryFacts 迁移后恒 false 契约。
/// </summary>
public class PlatformIsolationUnitTests
{
    private static readonly string TypeMappingPath =
        Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json");

    private static IReadOnlyCollection<string> CompatPrefixes()
        => new TypeMappingRegistry(TypeMappingPath).GetExplicitNamespaceMappings();

    private static CSharpCompilation BuildCompilation(params (string Path, string Content)[] files)
    {
        var trees = files
            .Select(file => CSharpSyntaxTree.ParseText(
                file.Content,
                new CSharpParseOptions(LanguageVersion.Latest),
                file.Path))
            .ToList();
        trees.Add(ConversionPipeline.GlobalUsingsTree);

        return CSharpCompilation.Create(
            "PlatformIsolationTestAsm",
            trees,
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithAllowUnsafe(true)
                .WithUsings("System", "System.Collections.Generic", "System.Linq", "System.Text", "System.Threading.Tasks"));
    }

    private static BoundaryInventory BuildInventory(
        string path,
        string content,
        bool isTestProject = false)
        => BoundaryInventoryBuilder.Build(
            BuildCompilation((path, content)),
            isTestProject,
            CompatPrefixes());

    private static List<BoundarySite> SitesFor(BoundaryInventory inventory, string code)
        => inventory.Sites.Where(site => site.Code == code).ToList();

    [Fact]
    public void Inventory_ScansUiNamespace_ThreeSurfaces()
    {
        var inventory = BuildInventory("Ui.cs", """
            using Microsoft.Maui.Controls;
            class Sample
            {
                void M()
                {
                    Microsoft.Maui.Controls.ContentPage page = null;
                    var brush = Windows.UI.Xaml.Media.Brush.OpacityProperty;
                }
            }
            """);

        var sites = SitesFor(inventory, "CS2J3001");
        Assert.True(sites.Count >= 3, $"expected >= 3 UI sites, got {sites.Count}");

        // Three surfaces: using directive / qualified name / member access.
        Assert.Contains(sites, site => site.SyntaxKind == "UsingDirective");
        Assert.Contains(sites, site => site.SyntaxKind == "QualifiedName");
        Assert.Contains(sites, site => site.SyntaxKind == "SimpleMemberAccessExpression");

        var unexempted = sites.Where(site => site.Exemption == BoundaryExemption.None).ToList();
        Assert.True(unexempted.Count >= 3, $"expected >= 3 unexempted UI sites, got {unexempted.Count}");
        Assert.All(unexempted, site =>
        {
            Assert.Equal(BoundaryFamily.UiNamespace, site.Family);
            Assert.Equal("unsupported-domain", site.Category);
            Assert.Equal(PlatformIsolationDefaultAction.Reject, site.DefaultAction);
            Assert.Equal(PlatformIsolationAction.Reject, PlatformIsolationActionResolver.Resolve(site));
        });
    }

    [Fact]
    public void Inventory_ClassifiesPlatformServiceTypes_AsPlatformTypeReject()
    {
        var inventory = BuildInventory("Platform.cs", """
            class Sample
            {
                void M()
                {
                    var key = new Microsoft.Win32.RegistryKey();
                    var port = new System.IO.Ports.SerialPort();
                    var log = new System.Diagnostics.EventLog();
                }
            }
            """);

        var sites = inventory.Sites
            .Where(site => site.Code == "CS2J3102" && site.Family == BoundaryFamily.PlatformType)
            .ToList();

        Assert.True(sites.Count >= 3, $"expected >= 3 platform-type sites, got {sites.Count}");
        Assert.All(sites, site =>
        {
            Assert.Equal(PlatformIsolationDefaultAction.Reject, site.DefaultAction);
            Assert.Equal(PlatformIsolationAction.Reject, PlatformIsolationActionResolver.Resolve(site));
        });
    }

    [Fact]
    public void Inventory_ClassifiesOsProbes_AsPlatformMemberStub()
    {
        var inventory = BuildInventory("Probes.cs", """
            class Sample
            {
                bool A() => System.OperatingSystem.IsWindows();
                string B() => System.Runtime.InteropServices.RuntimeInformation.OSDescription;
                object C() => System.Environment.OSVersion;
            }
            """);

        var members = inventory.Sites
            .Where(site => site.Family == BoundaryFamily.PlatformMember)
            .ToList();

        Assert.True(members.Count >= 3, $"expected >= 3 platform-member sites, got {members.Count}");
        Assert.All(members, site =>
        {
            Assert.Equal(PlatformIsolationDefaultAction.Stub, site.DefaultAction);
            Assert.Equal(PlatformIsolationAction.ProbeRedirect, PlatformIsolationActionResolver.Resolve(site));
        });

        var statistics = PlatformIsolationStatistics.FromSites(inventory.Sites);
        Assert.True(statistics.Stubbed >= 3);
        Assert.True(statistics.InvariantHolds);
    }

    [Fact]
    public void Inventory_ClassifiesOsGuardAttributes_AsStripAttribute()
    {
        var inventory = BuildInventory("Guard.cs", """
            [assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
            namespace Sample;
            [System.Runtime.Versioning.UnsupportedOSPlatform("linux")]
            class C
            {
                [System.Runtime.Versioning.SupportedOSPlatformGuard("macos")]
                void M() { }
            }
            """);

        var sites = SitesFor(inventory, "CS2J3101");
        Assert.True(sites.Count >= 3, $"expected >= 3 guard attribute sites, got {sites.Count}");
        Assert.All(sites, site =>
        {
            Assert.Equal(BoundaryFamily.OsGuardAttribute, site.Family);
            Assert.Equal(PlatformIsolationDefaultAction.Stub, site.DefaultAction);
            Assert.Equal(PlatformIsolationAction.StripAttribute, PlatformIsolationActionResolver.Resolve(site));
        });
    }

    [Fact]
    public void Inventory_ClassifiesNativeInterop_AsRejectOnly()
    {
        var inventory = BuildInventory("Interop.cs", """
            using System.Runtime.InteropServices;
            class Sample
            {
                [DllImport("k.dll")] static extern bool Beep();
                static extern int Raw();
                void M()
                {
                    var pointer = Marshal.AllocHGlobal(0);
                    SafeHandle handle = null;
                }
            }
            """);

        Assert.Contains(inventory.Sites, site => site.Code == "CS2J3201" && site.Family == BoundaryFamily.NativeAttribute);
        Assert.Contains(inventory.Sites, site => site.Code == "CS2J3203" && site.Family == BoundaryFamily.ExternMethod);
        Assert.Contains(inventory.Sites, site => site.Code == "CS2J3202" && site.Family == BoundaryFamily.NativeType);

        Assert.All(
            inventory.Sites.Where(site => site.Category == "native-interop"),
            site =>
            {
                Assert.Equal(PlatformIsolationDefaultAction.Reject, site.DefaultAction);
                Assert.Equal(PlatformIsolationAction.Reject, PlatformIsolationActionResolver.Resolve(site));
            });
    }

    [Fact]
    public void Inventory_MarksCompatMappedUiNamespace_AsSuppressed()
    {
        var inventory = BuildInventory("WinForms.cs", "using System.Windows.Forms; class Sample : Form { }");

        var sites = SitesFor(inventory, "CS2J3001");
        Assert.NotEmpty(sites);
        Assert.All(sites, site =>
        {
            Assert.Equal(BoundaryExemption.CompatMapping, site.Exemption);
            Assert.Equal(PlatformIsolationAction.Suppressed, PlatformIsolationActionResolver.Resolve(site));
        });
    }

    [Fact]
    public void Inventory_SkipsGeneratedAssemblyInfoFile_ForGuardAttributes()
    {
        var inventory = BuildInventory(
            "obj/Debug/net8.0-windows/Test.AssemblyInfo.cs",
            "using System.Runtime.Versioning; [assembly: SupportedOSPlatform(\"windows\")] class Test { }");

        Assert.DoesNotContain(inventory.Sites, site => site.Code == "CS2J3101");
    }

    [Fact]
    public void Inventory_SkipsNativeMethodsFile_ForNativeInterop()
    {
        var inventory = BuildInventory("NativeMethods.cs", """
            using System.Runtime.InteropServices;
            class NativeMethods
            {
                [DllImport("k.dll")] static extern bool Beep();
            }
            """);

        Assert.DoesNotContain(inventory.Sites, site => site.Category == "native-interop");
    }

    [Fact]
    public void Inventory_DowngradesTestProjectProbes_ExceptOsDescription()
    {
        var inventory = BuildInventory("Tests.cs", """
            class Sample
            {
                bool A() => System.OperatingSystem.IsWindows();
                string B() => System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            }
            """, isTestProject: true);

        var windows = inventory.Sites
            .Where(site => site.Identity == "System.OperatingSystem.IsWindows")
            .ToList();
        Assert.NotEmpty(windows);
        Assert.All(windows, site =>
        {
            Assert.Equal(CoreDiagnosticSeverity.Warning, site.Severity);
            Assert.Equal(BoundaryExemption.TestProbeWarning, site.Exemption);
        });

        var description = inventory.Sites
            .Where(site => site.Identity.EndsWith("OSDescription", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(description);
        Assert.All(description, site =>
        {
            Assert.Equal(CoreDiagnosticSeverity.Error, site.Severity);
            Assert.Equal(BoundaryExemption.None, site.Exemption);
        });
    }

    [Fact]
    public void Inventory_SortsSites_ByFileLineColumnCode()
    {
        var compilation = BuildCompilation(
            ("Zebra.cs", "class Z { bool A() => System.OperatingSystem.IsWindows(); }"),
            ("Alpha.cs", "class A { bool B() => System.OperatingSystem.IsLinux(); }"));

        var inventory = BoundaryInventoryBuilder.Build(compilation, isTestProject: false, CompatPrefixes());

        for (var i = 1; i < inventory.Sites.Count; i++)
        {
            Assert.True(CompareSites(inventory.Sites[i - 1], inventory.Sites[i]) <= 0,
                $"site {i - 1} should not sort after site {i}");
        }

        Assert.Equal("Alpha.cs", inventory.Sites[0].File);
    }

    [Fact]
    public void Report_SerializationIsByteStable_AndUtf8WithoutBom()
    {
        var inventory = BuildInventory("Probes.cs", """
            class Sample
            {
                bool A() => System.OperatingSystem.IsWindows();
            }
            """);
        var result = PlatformIsolationReportBuilder.Build(
            inventory,
            new PlatformIsolationOptions(),
            config: null);

        var serializer = new PlatformIsolationReportSerializer();
        var first = serializer.Serialize(result.Report);
        var second = serializer.Serialize(result.Report);
        Assert.Equal(first, second);

        Assert.Equal(result.Statistics.TotalSites, result.Report.Totals.Sites);
        Assert.True(result.Statistics.InvariantHolds);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PI070");

        var path = Path.Combine(Path.GetTempPath(), $"cs2j-pi-{Guid.NewGuid():N}.json");
        try
        {
            serializer.WriteToFile(path, result.Report);
            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "report must be UTF-8 without BOM");
            Assert.Equal(first, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Report_AppliesConfigPolicySuppress_AndDetectsStaleEntries()
    {
        var inventory = BuildInventory("Platform.cs", """
            class Sample
            {
                void M()
                {
                    var key = new Microsoft.Win32.RegistryKey();
                }
            }
            """);

        var target = inventory.Sites.First(site => site.Family == BoundaryFamily.PlatformType);
        var config = new PlatformIsolationConfig
        {
            Policies = new Dictionary<string, PlatformIsolationDefaultAction>
            {
                ["PlatformType"] = PlatformIsolationDefaultAction.Stub,
            },
            Suppress =
            [
                new PlatformIsolationSuppressEntry
                {
                    File = target.File,
                    Span = new[] { target.SpanStart, target.SpanEnd },
                    Family = "PlatformType",
                    Reason = "known residual",
                    Owner = "c42",
                },
                new PlatformIsolationSuppressEntry
                {
                    File = "ghost.cs",
                    Span = new[] { 0, 1 },
                    Family = "PlatformType",
                    Reason = "stale",
                    Owner = "c42",
                },
            ],
        };

        var result = PlatformIsolationReportBuilder.Build(inventory, new PlatformIsolationOptions(), config);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PI060");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PI061");
        Assert.True(result.Statistics.Suppressed >= 1);
        Assert.True(result.Statistics.Stubbed >= 1);
        Assert.True(result.Statistics.InvariantHolds);
    }

    [Fact]
    public void Preprocessor_CachesInventory_AndIsReportOnly()
    {
        var preprocessor = new PlatformIsolationPreprocessor();
        preprocessor.BindCompilation(
            BuildCompilation(("P.cs", "class C { bool A() => System.OperatingSystem.IsWindows(); }")),
            new PlatformIsolationOptions(),
            isTestProject: false,
            compatNamespacePrefixes: CompatPrefixes());

        var first = preprocessor.BuildInventory();
        var second = preprocessor.BuildInventory();
        Assert.Same(first, second);

        var result = preprocessor.BuildReport();
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PI001");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "PI070");

        Assert.Null(preprocessor.RewriteFile("P.cs", "class C { }"));
    }

    [Fact]
    public void InteropBoundaryFacts_KeepsFrozenPlaceholders()
    {
        Assert.Equal("Cs2j.Generation.InteropFacadeAttribute", InteropBoundaryFacts.InteropFacadeMarker);
        Assert.Equal("Cs2j.Generation.PlatformStubAttribute", InteropBoundaryFacts.PlatformStubMarker);

        var compilation = BuildCompilation(("Facts.cs", "class C { static extern int Raw(); }"));
        var tree = compilation.SyntaxTrees.First(t => t.FilePath == "Facts.cs");
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var model = compilation.GetSemanticModel(tree);

        Assert.True(InteropBoundaryFacts.IsExternLikeMethod(model, method));
        Assert.False(InteropBoundaryFacts.IsNormalizedInteropFacade(model, method));
        Assert.False(InteropBoundaryFacts.IsPlatformIsolationGenerated(tree));
        Assert.False(InteropBoundaryFacts.IsPlatformIsolationGenerated(method));
    }

    [Fact]
    public void BoundaryTables_LocksTheSixPrefixTables()
    {
    Assert.Equal(
        new[]
        {
            "System.Windows.Forms",
            "System.Windows.Controls",
            "System.Windows",
            "Microsoft.Maui",
            "Windows.UI.Xaml",
            "System.Xaml",
        },
        BoundaryTables.UiNamespacePrefixes);

    Assert.Equal(
        new[]
        {
            "Microsoft.Win32.Registry",
            "Microsoft.Win32.RegistryKey",
            "System.Diagnostics.EventLog",
            "System.Management.ManagementObject",
            "System.Management.ManagementObjectSearcher",
            "System.ServiceProcess.ServiceController",
            "System.DirectoryServices.DirectoryEntry",
            "System.DirectoryServices.DirectorySearcher",
            "System.IO.Ports.SerialPort",
        },
        BoundaryTables.PlatformTypePrefixes);

    Assert.Equal(
        new[]
        {
            "System.Environment.OSVersion",
            "System.OperatingSystem.IsWindows",
            "System.OperatingSystem.IsLinux",
            "System.OperatingSystem.IsMacOS",
            "System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform",
            "System.Runtime.InteropServices.RuntimeInformation.OSDescription",
            "System.Runtime.InteropServices.RuntimeInformation.OSArchitecture",
            "System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture",
        },
        BoundaryTables.PlatformMemberPrefixes);

    Assert.Equal(
        new[]
        {
            "SupportedOSPlatform",
            "UnsupportedOSPlatform",
            "SupportedOSPlatformGuard",
            "UnsupportedOSPlatformGuard",
        },
        BoundaryTables.OsGuardAttributeNames);

    Assert.Equal(
        new[]
        {
            "DllImport",
            "LibraryImport",
            "ComImport",
            "MarshalAs",
            "UnmanagedCallersOnly",
            "GeneratedComInterface",
        },
        BoundaryTables.NativeInteropAttributeNames);

    Assert.Equal(
        new[]
        {
            "System.Runtime.InteropServices.Marshal",
            "System.Runtime.InteropServices.NativeLibrary",
            "System.Runtime.InteropServices.HandleRef",
            "System.Runtime.InteropServices.SafeHandle",
        },
        BoundaryTables.NativeInteropTypePrefixes);
}

[Fact]
    public void BoundaryTables_LocksClassificationHelpers()
    {
    Assert.True(BoundaryTables.IsPlatformMemberIdentity("System.OperatingSystem.IsWindows"));
    Assert.False(BoundaryTables.IsPlatformMemberIdentity("Microsoft.Win32.RegistryKey"));
    Assert.True(BoundaryTables.IsPlatformTypeIdentity("Microsoft.Win32.RegistryKey"));
    Assert.False(BoundaryTables.IsPlatformTypeIdentity("System.OperatingSystem.IsWindows"));

    Assert.True(BoundaryTables.IsOsGuardAttribute("SupportedOSPlatform"));
    Assert.True(BoundaryTables.IsOsGuardAttribute("UnsupportedOSPlatformGuard"));
    Assert.False(BoundaryTables.IsOsGuardAttribute("DllImport"));

    Assert.True(BoundaryTables.IsNativeInteropAttribute("DllImport"));
    Assert.True(BoundaryTables.IsNativeInteropAttribute("GeneratedComInterface"));
    Assert.False(BoundaryTables.IsNativeInteropAttribute("SupportedOSPlatform"));

    // NormalizeAttributeName strips the suffix and the namespace.
    Assert.Equal("DllImport", BoundaryTables.NormalizeAttributeName("System.Runtime.InteropServices.DllImportAttribute"));
    Assert.Equal("MarshalAs", BoundaryTables.NormalizeAttributeName("MarshalAs"));
}

[Fact]
    public void BoundaryTables_LocksAnalyzerMessageTemplates()
    {
    var compilation = BuildCompilation(
        ("Ui.cs", "using Microsoft.Maui.Controls; class U { }"),
        ("Guard.cs", "[System.Runtime.Versioning.SupportedOSPlatform(\"windows\")] class G { }"),
        ("Platform.cs", """
            class P
            {
                void M() { var key = new Microsoft.Win32.RegistryKey(); }
                bool A() => System.OperatingSystem.IsWindows();
            }
            """),
        ("Interop.cs", """
            using System.Runtime.InteropServices;
            class I
            {
                [DllImport("k.dll")] static extern bool Beep();
                static extern int Raw();
                void M()
                {
                    var pointer = Marshal.AllocHGlobal(0);
                    SafeHandle handle = null;
                }
            }
            """));

    var inventory = BoundaryInventoryBuilder.Build(compilation, isTestProject: false, CompatPrefixes());

    Assert.Contains(inventory.Sites, site =>
        site.Code == "CS2J3001"
        && site.Message == "Namespace 'Microsoft.Maui.Controls' is not supported by the Java-only conversion pipeline.");
    Assert.Contains(inventory.Sites, site =>
        site.Code == "CS2J3101"
        && site.Message == "Platform boundary attribute 'SupportedOSPlatform' must be isolated before Java conversion.");
    Assert.Contains(inventory.Sites, site =>
        site.Code == "CS2J3102"
        && site.Message == "Platform-specific API 'System.OperatingSystem.IsWindows' must be isolated before Java conversion.");
    Assert.Contains(inventory.Sites, site =>
        site.Code == "CS2J3102"
        && site.Message == "Platform-specific type 'Microsoft.Win32.RegistryKey' must be isolated before Java conversion.");
    Assert.Contains(inventory.Sites, site =>
        site.Code == "CS2J3201"
        && site.Message == "Native interop attribute 'DllImport' is not supported by the Java-only conversion pipeline.");
    Assert.Contains(inventory.Sites, site =>
        site.Code == "CS2J3202"
        && site.Message == "Native interop API 'System.Runtime.InteropServices.Marshal.AllocHGlobal' is not supported by the Java-only conversion pipeline.");
    Assert.Contains(inventory.Sites, site =>
        site.Code == "CS2J3202"
        && site.Message == "Native interop type 'System.Runtime.InteropServices.SafeHandle' is not supported by the Java-only conversion pipeline.");
    Assert.Contains(inventory.Sites, site =>
        site.Code == "CS2J3203"
        && site.Message == "Extern method 'Raw' is not supported by the Java-only conversion pipeline.");
}

private static int CompareSites(BoundarySite left, BoundarySite right)
    {
        var byFile = string.CompareOrdinal(left.File, right.File);
        if (byFile != 0) return byFile;
        var byLine = left.Line.CompareTo(right.Line);
        if (byLine != 0) return byLine;
        var byColumn = left.Column.CompareTo(right.Column);
        if (byColumn != 0) return byColumn;
        return string.CompareOrdinal(left.Code, right.Code);
    }
}