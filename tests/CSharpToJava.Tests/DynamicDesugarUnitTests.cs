using CSharpToJava.Core.DynamicDesugar;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// C25 M1（纯审计）Core 单测（设计 §7.1 矩阵）：dynamic 声明/操作/边界 API/
/// 元数据 [Dynamic] 站点分类、DS002 P2 口径、表达式树零噪声、object 不计数（D6）、
/// audit 字节不变、报告 JSON 跨运行字节稳定、strict 分级。
/// </summary>
public class DynamicDesugarUnitTests
{
    private const string TestPath = "Test.cs";

    private static CSharpCompilation BuildCompilation(string content, string path = TestPath)
    {
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(
                content,
                new CSharpParseOptions(LanguageVersion.Latest),
                path),
            ConversionPipeline.GlobalUsingsTree,
        };

        return CSharpCompilation.Create(
            "DynamicDesugarTestAsm",
            trees,
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithAllowUnsafe(true)
                .WithUsings(
                    "System",
                    "System.Collections.Generic",
                    "System.Linq",
                    "System.Text",
                    "System.Threading.Tasks"));
    }

    private static DynamicSiteInventory BuildInventory(string content, string path = TestPath)
    {
        var preprocessor = new DynamicDesugarPreprocessor();
        preprocessor.BindCompilation(BuildCompilation(content, path));
        return preprocessor.BuildInventory();
    }

    private static DynamicApplyResult RunAll(string content, DynamicDesugarOptions? options = null)
    {
        var preprocessor = new DynamicDesugarPreprocessor(options);
        preprocessor.BindCompilation(BuildCompilation(content));
        return preprocessor.RewriteAll();
    }

    private static List<DynamicSiteRecord> Sites(DynamicSiteInventory inventory, string code)
        => inventory.Sites.Where(site => site.Code == code).ToList();

    // ---------- 声明族 ----------

    [Fact]
    public void Audit_SealedStraightLine_LocalDeclPlusLateBoundMemberSites()
    {
        var inventory = BuildInventory("""
            sealed class SealedFoo
            {
                public void M() { }
            }

            class Sample
            {
                void F()
                {
                    dynamic d = new SealedFoo();
                    d.M();
                }
            }
            """);

        var locals = Sites(inventory, DynamicDesugarDiagnosticCodes.LocalUnprovable);
        Assert.Single(locals);
        Assert.Equal(DynamicSiteKind.DynamicLocalDecl, locals[0].Kind);
        Assert.Equal("SealedFoo", locals[0].OriginType);
        Assert.Contains("dynamic local", locals[0].Message);

        // d.M() 经运行时绑定器晚绑定：Roslyn 5 实测仅 invocation 节点为 DS013
        // （方法组成员访问不独立计站点）。
        var memberOps = Sites(inventory, DynamicDesugarDiagnosticCodes.DynamicMemberOperation);
        var invocationSite = Assert.Single(memberOps);
        Assert.Equal(DynamicSiteKind.DynamicInvocation, invocationSite.Kind);

        // M1 审计：具名 Warning，整树 Success。
        var result = RunAll("""
            sealed class SealedFoo
            {
                public void M() { }
            }

            class Sample
            {
                void F()
                {
                    dynamic d = new SealedFoo();
                    d.M();
                }
            }
            """);
        Assert.False(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == DynamicDesugarDiagnosticCodes.LocalUnprovable
                          && diagnostic.Severity == DynamicDesugarSeverity.Warning);
    }

    [Fact]
    public void Audit_DynamicParameterAndReturn_CrossBoundarySites()
    {
        var inventory = BuildInventory("""
            class Sample
            {
                dynamic Take(dynamic x)
                {
                    return x;
                }
            }
            """);

        var boundary = Sites(inventory, DynamicDesugarDiagnosticCodes.CrossBoundarySignature);
        Assert.Equal(2, boundary.Count);
        Assert.Contains(boundary, site => site.Kind == DynamicSiteKind.DynamicParameter);
        Assert.Contains(boundary, site => site.Kind == DynamicSiteKind.DynamicReturn);
        Assert.All(boundary, site => Assert.Contains("Sample", site.Message));
    }

    [Fact]
    public void Audit_DynamicFieldAndProperty_FieldLikeStorageSites()
    {
        var inventory = BuildInventory("""
            class Sample
            {
                dynamic F;
                dynamic P { get; set; }
            }
            """);

        var fieldLike = Sites(inventory, DynamicDesugarDiagnosticCodes.FieldLikeStorage);
        Assert.Equal(2, fieldLike.Count);
        Assert.All(fieldLike, site =>
        {
            Assert.Equal(DynamicSiteKind.DynamicFieldOrProperty, site.Kind);
            Assert.Contains("field/property/event/indexer", site.Message);
        });
    }

    [Fact]
    public void Audit_DynamicIndexer_ElementAccessSites()
    {
        var inventory = BuildInventory("""
            class Sample
            {
                void F()
                {
                    dynamic d = new System.Dynamic.ExpandoObject();
                    d[0] = 1;
                    var v = d["key"];
                }
            }
            """);

        var elementAccess = Sites(inventory, DynamicDesugarDiagnosticCodes.DynamicElementAccess);
        Assert.Equal(2, elementAccess.Count);
        Assert.All(
            elementAccess,
            site => Assert.Equal(DynamicSiteKind.DynamicElementAccess, site.Kind));
    }

    // ---------- 操作族 ----------

    [Fact]
    public void Audit_DynamicBinaryCompoundIncrement_OperationSites()
    {
        var inventory = BuildInventory("""
            class Sample
            {
                void F()
                {
                    dynamic d = 1;
                    var a = d + 1;
                    d += 1;
                    d++;
                }
            }
            """);

        var operations = Sites(inventory, DynamicDesugarDiagnosticCodes.DynamicOperation);
        Assert.Equal(3, operations.Count);
        Assert.Contains(operations, site => site.SyntaxKindName == "AddExpression");
        Assert.Contains(operations, site => site.SyntaxKindName == "AddAssignmentExpression");
        // 节点类型是 PostfixUnaryExpressionSyntax，具体 kind 名为 PostIncrementExpression。
        Assert.Contains(operations, site => site.SyntaxKindName == "PostIncrementExpression");
    }

    [Fact]
    public void Audit_StaticArgumentIntoDynamicParameter_ConversionSiteWithOriginType()
    {
        var inventory = BuildInventory("""
            class Sample
            {
                void DynamicArg(dynamic x) { }

                void Call()
                {
                    DynamicArg(42);
                }
            }
            """);

        var conversions = Sites(inventory, DynamicDesugarDiagnosticCodes.DynamicConversion);
        var argumentSite = Assert.Single(conversions);
        Assert.Equal(DynamicSiteKind.DynamicConversion, argumentSite.Kind);
        Assert.Equal("int", argumentSite.OriginType);
        Assert.Contains("int", argumentSite.Message);
    }

    [Fact]
    public void Audit_ExplicitCastThenStaticCall_P2InfoOnlyNoMemberSite()
    {
        var source = """
            sealed class SealedFoo
            {
                public void M() { }
            }

            class Sample
            {
                void F()
                {
                    dynamic d = new SealedFoo();
                    ((SealedFoo)d).M();
                }
            }
            """;

        var inventory = BuildInventory(source);
        var p2 = Sites(inventory, DynamicDesugarDiagnosticCodes.ExplicitCastP2);
        var cast = Assert.Single(p2);
        Assert.Equal(DynamicProvability.P2Info, cast.Provability);
        Assert.Equal("SealedFoo", cast.OriginType);
        Assert.Empty(Sites(inventory, DynamicDesugarDiagnosticCodes.DynamicMemberOperation));

        var result = RunAll(source);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == DynamicDesugarDiagnosticCodes.ExplicitCastP2
                          && diagnostic.Severity == DynamicDesugarSeverity.Info);
    }

    // ---------- 边界 API 族 ----------

    [Fact]
    public void Audit_ExpandoDynamicObjectAndRuntimeBinder_BoundarySites()
    {
        var inventory = BuildInventory("""
            using Microsoft.CSharp.RuntimeBinder;

            class MyDynamic : System.Dynamic.DynamicObject
            {
            }

            class Sample
            {
                void F()
                {
                    dynamic x = new System.Dynamic.ExpandoObject();
                }
            }
            """);

        var expandoSites = Sites(inventory, DynamicDesugarDiagnosticCodes.ExpandoJsonInterop);
        Assert.Equal(2, expandoSites.Count);
        Assert.Contains(
            expandoSites,
            site => site.Kind == DynamicSiteKind.ExpandoOrDynamicObject
                    && site.BoundSymbolName == "System.Dynamic.DynamicObject");
        Assert.Contains(
            expandoSites,
            site => site.Kind == DynamicSiteKind.ExpandoOrDynamicObject
                    && site.BoundSymbolName == "System.Dynamic.ExpandoObject");

        var runtimeBinder = Sites(inventory, DynamicDesugarDiagnosticCodes.RuntimeBinderApi);
        var usingSite = Assert.Single(runtimeBinder);
        Assert.Equal(DynamicSiteKind.RuntimeBinderApi, usingSite.Kind);
        // Microsoft.CSharp.dll 不在测试引用集：文本保守识别（R11），消息带人工确认提示。
        Assert.True(usingSite.SymbolUnresolved);
        Assert.Contains("未解析引用", usingSite.Message);
    }

    // ---------- 元数据族 ----------

    [Fact]
    public void Audit_SourceWrittenDynamicAttribute_AttributeEntriesAndObjectControl()
    {
        var inventory = BuildInventory("""
            using System.Runtime.CompilerServices;

            class Sample
            {
                [Dynamic]
                object F;

                void M([Dynamic] object x) { }

                [return: Dynamic]
                object Get() => null;

                void Control()
                {
                    object o = 1;
                }
            }
            """);

        var attributeEntries = Sites(inventory, DynamicDesugarDiagnosticCodes.DynamicAttributeEntry);
        Assert.Equal(3, attributeEntries.Count);
        Assert.All(
            attributeEntries,
            site => Assert.Equal(DynamicSiteKind.DynamicAttributeEntry, site.Kind));

        // D6：普通 object 局部（无 [Dynamic]）不计数——整树恰为 3 个站点。
        Assert.Equal(3, inventory.Sites.Count);
    }

    [Fact]
    public void Audit_VarDynamicCastInitializer_OnlyLocalDeclSite()
    {
        var inventory = BuildInventory("""
            class Sample
            {
                void F()
                {
                    var x = (dynamic)1;
                }
            }
            """);

        var site = Assert.Single(inventory.Sites);
        Assert.Equal(DynamicSiteKind.DynamicLocalDecl, site.Kind);
        Assert.Equal(DynamicDesugarDiagnosticCodes.LocalUnprovable, site.Code);
    }

    // ---------- 表达式树 / 干净文件 ----------

    [Fact]
    public void Audit_DynamicInsideExpressionTree_ZeroC25Diagnostics()
    {
        const string source = """
            using System.Linq.Expressions;

            class Sample
            {
                void F()
                {
                    Expression<System.Func<dynamic>> et = () => (dynamic)1;
                    Expression<System.Func<int>> et2 = () => { dynamic z = 1; return z; };
                }
            }
            """;

        // 语句体形态带 CS0834/CS1963 基线错误；两种形态都不得叠加任何 C25 站点（R10）。
        var inventory = BuildInventory(source);
        Assert.Empty(inventory.Sites);

        var result = RunAll(source);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Audit_CleanFile_NoSitesNoDiagnostics()
    {
        var result = RunAll("""
            sealed class Sample
            {
                int Add(int a, int b) => a + b;
            }
            """);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, result.Statistics.SitesTotal);
        Assert.True(result.Statistics.FilesSkippedClean >= 1);
    }

    // ---------- 输出契约：字节不变 / 报告稳定 / 分级 ----------

    [Fact]
    public void Audit_RewriteFile_OutputBytesUnchanged()
    {
        const string source = """
            class Sample
            {
                void F()
                {
                    dynamic d = 1;
                    d.ToString();
                }
            }
            """;

        var preprocessor = new DynamicDesugarPreprocessor();
        preprocessor.BindCompilation(BuildCompilation(source));
        var result = preprocessor.RewriteFile(TestPath, source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.True(result.FileSites.Count >= 1);
        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.True(diagnostic.Line >= 1);
            Assert.True(diagnostic.Column >= 1);
            Assert.False(string.IsNullOrWhiteSpace(diagnostic.Message));
        });
    }

    [Fact]
    public void Audit_Report_TwoSerializationsAreByteIdentical()
    {
        var preprocessor = new DynamicDesugarPreprocessor();
        preprocessor.BindCompilation(BuildCompilation("""
            class Sample
            {
                dynamic F;

                void M(dynamic x)
                {
                    dynamic d = x;
                    d++;
                }
            }
            """));

        var serializer = new DynamicAuditReportSerializer();
        var first = serializer.Serialize(preprocessor.CloseReport());
        var second = serializer.Serialize(preprocessor.CloseReport());

        Assert.Equal(first, second);
        Assert.Contains("\"tool\": \"cs2j dynamic-desugar\"", first);
        Assert.DoesNotContain("generatedAt", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("guid", first, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"mode\": \"audit\"", first);
        Assert.Contains("\"dynamicFieldOrProperty\": 1", first);
    }

    [Fact]
    public void Audit_DefaultMode_HasNoErrors()
    {
        var result = RunAll("""
            class Sample
            {
                void F()
                {
                    dynamic d = 1;
                    d.ToString();
                }
            }
            """);
        Assert.False(result.HasErrors);
        Assert.All(result.Diagnostics, diagnostic =>
            Assert.NotEqual(DynamicDesugarSeverity.Error, diagnostic.Severity));
    }

    [Fact]
    public void Strict_WarningSites_EscalateToErrors()
    {
        var result = RunAll(
            """
            class Sample
            {
                void F()
                {
                    dynamic d = 1;
                    d.ToString();
                }
            }
            """,
            new DynamicDesugarOptions { Strict = true });

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Severity == DynamicDesugarSeverity.Error);
    }

    [Fact]
    public void Strict_Ds002StaysInfo_PolicyLevel()
    {
        var strict = new DynamicDesugarOptions { Strict = true };

        var p2Site = new DynamicSiteRecord
        {
            Kind = DynamicSiteKind.DynamicConversion,
            FilePath = TestPath,
            SpanStart = 0,
            SpanEnd = 1,
            Line = 0,
            Column = 0,
            SyntaxKindName = "CastExpression",
            Code = DynamicDesugarDiagnosticCodes.ExplicitCastP2,
            Message = "p2",
            Provability = DynamicProvability.P2Info,
        };

        Assert.Equal(
            DynamicDesugarSeverity.Info,
            DynamicDesugarSeverityPolicy.Resolve(p2Site, strict));

        var ds010Site = new DynamicSiteRecord
        {
            Kind = DynamicSiteKind.DynamicLocalDecl,
            FilePath = TestPath,
            SpanStart = 0,
            SpanEnd = 1,
            Line = 0,
            Column = 0,
            SyntaxKindName = "VariableDeclarator",
            Code = DynamicDesugarDiagnosticCodes.LocalUnprovable,
            Message = "local",
        };
        Assert.Equal(
            DynamicDesugarSeverity.Warning,
            DynamicDesugarSeverityPolicy.Resolve(ds010Site, new DynamicDesugarOptions()));
        Assert.Equal(
            DynamicDesugarSeverity.Error,
            DynamicDesugarSeverityPolicy.Resolve(ds010Site, strict));
    }

    [Fact]
    public void RewriteFile_AccumulatorOverload_AggregatesSitesAndDiagnostics()
    {
        var compilation = BuildCompilation("""
            class Sample
            {
                void F()
                {
                    dynamic d = 1;
                    d.ToString();
                }
            }
            """);
        var preprocessor = new DynamicDesugarPreprocessor();
        preprocessor.BindCompilation(compilation);

        var statistics = new DynamicDesugarStatistics();
        var sink = new List<DynamicDesugarDiagnostic>();
        var source = compilation.SyntaxTrees.First(tree => tree.FilePath == TestPath).GetText().ToString();
        var result = preprocessor.RewriteFile(
            TestPath,
            source,
            compilation.GetSemanticModel(compilation.SyntaxTrees.First(tree => tree.FilePath == TestPath)),
            statistics,
            sink);

        Assert.False(result.Changed);
        Assert.Equal(1, statistics.FilesScanned);
        Assert.Equal(result.Diagnostics.Count, sink.Count);
        Assert.Equal(result.FileSites.Count, statistics.SitesTotal);
    }
}
