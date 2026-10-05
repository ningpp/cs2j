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

    private static List<DynamicSiteRecord> LocalSites(DynamicSiteInventory inventory)
        => inventory.Sites.Where(site => site.Kind == DynamicSiteKind.DynamicLocalDecl).ToList();

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

        // M2 细化（预期变更）：sealed 单点直线局部由 FlowTypeProver 证为 P1，
        // DS010 → DS001 / ProvenP1（M1 时该站点是 DS010 Unclassified）。
        var locals = Sites(inventory, DynamicDesugarDiagnosticCodes.ProvenConcretized);
        var local = Assert.Single(locals);
        Assert.Equal(DynamicSiteKind.DynamicLocalDecl, local.Kind);
        Assert.Equal(DynamicProvability.ProvenP1, local.Provability);
        Assert.Null(local.UnprovableReason);
        Assert.Equal("SealedFoo", local.OriginType);
        Assert.Contains("provably concretizable to 'SealedFoo'", local.Message);
        Assert.Empty(Sites(inventory, DynamicDesugarDiagnosticCodes.LocalUnprovable));

        // d.M() 经运行时绑定器晚绑定：Roslyn 5 实测仅 invocation 节点为 DS013
        // （方法组成员访问不独立计站点）。
        var memberOps = Sites(inventory, DynamicDesugarDiagnosticCodes.DynamicMemberOperation);
        var invocationSite = Assert.Single(memberOps);
        Assert.Equal(DynamicSiteKind.DynamicInvocation, invocationSite.Kind);

        // 审计模式：P1 站点 DS001 Info（尚未改写），整树 Success。
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
            diagnostic => diagnostic.Code == DynamicDesugarDiagnosticCodes.ProvenConcretized
                          && diagnostic.Severity == DynamicDesugarSeverity.Info
                          && diagnostic.Message.Contains("SealedFoo"));
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

    // ---------- M2 FlowTypeProver：P1 正例 ----------

    [Fact]
    public void M2_Proven_SealedClassIntString_SingleAssignments()
    {
        var inventory = BuildInventory("""
            sealed class SealedFoo
            {
                public void M() { }
            }

            class Sample
            {
                void SealedCase()
                {
                    dynamic d = new SealedFoo();
                    d.M();
                }

                void IntCase()
                {
                    dynamic i = 42;
                    i.ToString();
                }

                void StringCase()
                {
                    dynamic s = "hello";
                    s.ToString();
                }
            }
            """);

        var locals = LocalSites(inventory);
        Assert.Equal(3, locals.Count);
        Assert.All(locals, site =>
        {
            Assert.Equal(DynamicProvability.ProvenP1, site.Provability);
            Assert.Null(site.UnprovableReason);
            Assert.Equal(DynamicDesugarDiagnosticCodes.ProvenConcretized, site.Code);
        });
        Assert.Equal("SealedFoo", locals[0].OriginType);
        Assert.Equal("int", locals[1].OriginType);
        Assert.Equal("string", locals[2].OriginType);
    }

    [Fact]
    public void M2_Proven_MultipleUsePoints_DoNotChangeProvability()
    {
        var inventory = BuildInventory("""
            sealed class SealedFoo
            {
                public int Value { get; }
                public SealedFoo Self() => this;
                public void M() { }
            }

            class Sample
            {
                void Consume(object o) { }

                void F()
                {
                    dynamic d = new SealedFoo();
                    d.M();                 // 调用使用点
                    d.Self().M();          // 链式成员/调用
                    Consume(d);            // 实参传递（DS016 边界）
                    var v = d.Value;       // 动态成员读取（v 自身另计不可证，不影响 d）
                }
            }
            """);

        var dSite = LocalSites(inventory).Single(site => site.BoundSymbolName == "d");
        Assert.Equal(DynamicProvability.ProvenP1, dSite.Provability);
        Assert.Equal("SealedFoo", dSite.OriginType);
    }

    [Fact]
    public void M2_Proven_ForEachSealedElement_ProvenP1()
    {
        var inventory = BuildInventory("""
            using System.Collections.Generic;

            sealed class SealedFoo
            {
                public void M() { }
            }

            class Sample
            {
                void F(List<SealedFoo> items)
                {
                    foreach (dynamic x in items)
                    {
                        x.M();
                    }
                }
            }
            """);

        var local = Assert.Single(LocalSites(inventory));
        Assert.Equal(DynamicProvability.ProvenP1, local.Provability);
        Assert.Equal("SealedFoo", local.OriginType);
    }

    [Fact]
    public void M2_Proven_DeconstructionDynamicComponent_DrillsTupleElementType()
    {
        var inventory = BuildInventory("""
            using System.Collections.Generic;

            sealed class SealedFoo
            {
                public void M() { }
            }

            class Sample
            {
                void F()
                {
                    (dynamic a, int b) = (new SealedFoo(), 1);
                    a.M();
                }

                void G(IEnumerable<(dynamic, int)> sequence)
                {
                    foreach (var (x, y) in sequence)
                    {
                        x.ToString();
                    }
                }
            }
            """);

        var locals = LocalSites(inventory);
        Assert.Equal(2, locals.Count);

        // 解构声明 dynamic 分量：按分量位置钻取右值元组类型 → SealedFoo 可证。
        Assert.Equal(DynamicProvability.ProvenP1, locals[0].Provability);
        Assert.Equal("SealedFoo", locals[0].OriginType);

        // foreach 解构分量来自 IEnumerable<(dynamic, int)>：元素分量本身 dynamic → 不可证。
        Assert.Equal(DynamicProvability.Unprovable, locals[1].Provability);
        Assert.Equal(DynamicUnprovableReason.DynamicAssignment, locals[1].UnprovableReason);
    }

    // ---------- M2 FlowTypeProver：不可证原因（每条一个） ----------

    [Fact]
    public void M2_Unprovable_BaseAndDerivedAssignments_MultipleAssignmentTypes()
    {
        var inventory = BuildInventory("""
            class Base { }
            class Derived : Base { }

            class Sample
            {
                void F(bool cond)
                {
                    dynamic d = new Derived();
                    if (cond)
                    {
                        d = new Base();
                    }
                }
            }
            """);

        var local = Assert.Single(LocalSites(inventory));
        Assert.Equal(DynamicProvability.Unprovable, local.Provability);
        Assert.Equal(DynamicUnprovableReason.MultipleAssignmentTypes, local.UnprovableReason);
        Assert.Equal(DynamicDesugarDiagnosticCodes.LocalUnprovable, local.Code);
    }

    [Fact]
    public void M2_Unprovable_DynamicMethodReturn_DynamicAssignment()
    {
        var inventory = BuildInventory("""
            class Sample
            {
                dynamic GetDynamic() => 1;

                void F()
                {
                    dynamic d = GetDynamic();
                }
            }
            """);

        var local = Assert.Single(LocalSites(inventory));
        Assert.Equal(DynamicUnprovableReason.DynamicAssignment, local.UnprovableReason);
    }

    [Fact]
    public void M2_Unprovable_UnresolvedSymbol_MissingSymbol()
    {
        // 基线编译错误树：右值符号缺失 → Error/null 类型点保守不可证（R10）。
        var inventory = BuildInventory("""
            class Sample
            {
                void F()
                {
                    dynamic d = MissingType.Make();
                }
            }
            """);

        var local = Assert.Single(LocalSites(inventory));
        Assert.Equal(DynamicUnprovableReason.UnresolvedSymbol, local.UnprovableReason);
        Assert.Contains("unresolved symbol", local.Message);
    }

    [Fact]
    public void M2_Unprovable_ZeroAssignments_UnresolvedSymbol()
    {
        var inventory = BuildInventory("""
            class Sample
            {
                void F()
                {
                    dynamic d;
                }
            }
            """);

        var local = Assert.Single(LocalSites(inventory));
        Assert.Equal(DynamicUnprovableReason.UnresolvedSymbol, local.UnprovableReason);
        Assert.Contains("unresolved symbol", local.Message);

        // unresolved-symbol 的 DS010 不被基线错误抑制规则吞掉：审计仍发 Warning。
        var result = RunAll("""
            class Sample
            {
                void F()
                {
                    dynamic d;
                }
            }
            """);
        var diagnostic = Assert.Single(
            result.Diagnostics.Where(d => d.Code == DynamicDesugarDiagnosticCodes.LocalUnprovable));
        Assert.Equal(DynamicDesugarSeverity.Warning, diagnostic.Severity);
        Assert.Contains("unresolved symbol", diagnostic.Message);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void M2_Unprovable_RefOutInArguments_ByRefEscape()
    {
        var inventory = BuildInventory("""
            class Sample
            {
                void TakeRef(ref dynamic x) { }
                void GiveOut(out dynamic x) { x = 1; }
                void ReadIn(in dynamic x) { }

                void RefCase()
                {
                    dynamic d = 1;
                    TakeRef(ref d);
                }

                void OutCase()
                {
                    dynamic d = 1;
                    GiveOut(out d);
                }

                void InCase()
                {
                    dynamic d = 1;
                    ReadIn(in d);
                }
            }
            """);

        var locals = LocalSites(inventory);
        Assert.Equal(3, locals.Count);
        Assert.All(
            locals,
            site => Assert.Equal(DynamicUnprovableReason.ByRefEscape, site.UnprovableReason));
    }

    [Fact]
    public void M2_Unprovable_LambdaCapture_Captured()
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
                    System.Action run = () => d.M();
                }
            }
            """);

        var local = Assert.Single(LocalSites(inventory));
        Assert.Equal(DynamicUnprovableReason.Captured, local.UnprovableReason);
    }

    [Fact]
    public void M2_Unprovable_LocalFunctionCapture_Captured()
    {
        var inventory = BuildInventory("""
            class Sample
            {
                void F()
                {
                    dynamic d = 1;

                    void Local()
                    {
                        d.ToString();
                    }

                    Local();
                }
            }
            """);

        var local = Assert.Single(LocalSites(inventory));
        Assert.Equal(DynamicUnprovableReason.Captured, local.UnprovableReason);
    }

    [Fact]
    public void M2_Unprovable_LockUsingFixedAwait_Captured()
    {
        var inventory = BuildInventory("""
            using System;
            using System.Threading.Tasks;

            sealed class SealedFoo { }

            class Sample
            {
                static IDisposable MakeDisposable() => null!;

                void LockCase()
                {
                    dynamic d = new SealedFoo();
                    lock (d) { }
                }

                void UsingCase()
                {
                    using dynamic d = MakeDisposable();
                }

                async Task AwaitUsingCase()
                {
                    await using dynamic d = MakeDisposable();
                }

                unsafe void FixedCase()
                {
                    int[] arr = { 1, 2, 3 };
                    fixed (int* p = arr)
                    {
                        dynamic d = 1;
                        d.ToString();
                    }
                }

                async Task AwaitCase()
                {
                    dynamic d = 1;
                    await d;
                }
            }
            """);

        var locals = LocalSites(inventory);
        Assert.Equal(5, locals.Count);
        Assert.All(
            locals,
            site => Assert.Equal(DynamicUnprovableReason.Captured, site.UnprovableReason));
    }

    [Fact]
    public void M2_Unprovable_OpenTypeSingleAssignment_SealedNotSatisfied()
    {
        var inventory = BuildInventory("""
            class Open { }

            class Sample
            {
                void F()
                {
                    dynamic d = new Open();
                }
            }
            """);

        var local = Assert.Single(LocalSites(inventory));
        Assert.Equal(DynamicUnprovableReason.SealedNotSatisfied, local.UnprovableReason);
        Assert.Contains("sealed not satisfied", local.Message);
    }

    [Fact]
    public void M2_Unprovable_IntAndNullableIntMix_MultipleAssignmentTypes()
    {
        var inventory = BuildInventory("""
            class Sample
            {
                void F(bool cond)
                {
                    dynamic d = 1;
                    if (cond)
                    {
                        d = (int?)2;
                    }
                }
            }
            """);

        var local = Assert.Single(LocalSites(inventory));
        // 可空差异 int/int? 保守按异类型处理（不合并，宁假阳）。
        Assert.Equal(DynamicUnprovableReason.MultipleAssignmentTypes, local.UnprovableReason);
        Assert.Contains("multiple assignment types", local.Message);
    }

    // ---------- M2 诊断/统计/报告 ----------

    [Fact]
    public void M2_Diagnostic_ProvenLocal_DS001InfoNamesType()
    {
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
        var diagnostic = Assert.Single(
            result.Diagnostics.Where(d => d.Code == DynamicDesugarDiagnosticCodes.ProvenConcretized));
        Assert.Equal(DynamicDesugarSeverity.Info, diagnostic.Severity);
        Assert.Contains("SealedFoo", diagnostic.Message);
        Assert.Contains("rewrite mode concretizes the declaration", diagnostic.Message);
        // 不得谎称已改写。
        Assert.Contains("no rewrite has been performed", diagnostic.Message);
    }

    [Fact]
    public void M2_Diagnostic_UnprovableLocal_DS010WarningWithNamedReason()
    {
        var result = RunAll("""
            class Open { }

            class Sample
            {
                void F()
                {
                    dynamic d = new Open();
                }
            }
            """);

        Assert.False(result.HasErrors);
        var diagnostic = Assert.Single(
            result.Diagnostics.Where(d => d.Code == DynamicDesugarDiagnosticCodes.LocalUnprovable));
        Assert.Equal(DynamicDesugarSeverity.Warning, diagnostic.Severity);
        Assert.Contains("sealed not satisfied", diagnostic.Message);
        Assert.Contains(
            "register the site in Cs2jDynamicAudit.json",
            diagnostic.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void M2_Strict_DS010EscalatesToError()
    {
        var result = RunAll(
            """
            class Open { }

            class Sample
            {
                void F()
                {
                    dynamic d = new Open();
                }
            }
            """,
            new DynamicDesugarOptions { Strict = true });

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == DynamicDesugarDiagnosticCodes.LocalUnprovable
                          && diagnostic.Severity == DynamicDesugarSeverity.Error
                          && diagnostic.Message.Contains("sealed not satisfied"));
    }

    [Fact]
    public void M2_Statistics_ClassifiedProvenUnprovableCountedWithoutDoubleCount()
    {
        var result = RunAll("""
            sealed class SealedFoo { }
            class Open { }

            class Sample
            {
                void ProvenCase()
                {
                    dynamic a = new SealedFoo();
                }

                void UnprovableCase()
                {
                    dynamic b = new Open();
                }
            }
            """);

        Assert.Equal(2, result.Statistics.DeclarationsClassified);
        Assert.Equal(1, result.Statistics.ProvenDeclarations);
        Assert.Equal(1, result.Statistics.UnprovableSites);
    }

    [Fact]
    public void M2_Report_EmitsProvabilityReasonAndProvenType_ByteStable()
    {
        const string source = """
            sealed class SealedFoo { }
            class Open { }

            class Sample
            {
                void ProvenCase()
                {
                    dynamic a = new SealedFoo();
                }

                void UnprovableCase()
                {
                    dynamic b = new Open();
                }
            }
            """;

        var preprocessor = new DynamicDesugarPreprocessor();
        preprocessor.BindCompilation(BuildCompilation(source));
        var serializer = new DynamicAuditReportSerializer();

        var first = serializer.Serialize(preprocessor.CloseReport());
        var second = serializer.Serialize(preprocessor.CloseReport());
        Assert.Equal(first, second);
        Assert.Contains("\"provability\": \"provenP1\"", first);
        Assert.Contains("\"provability\": \"unprovable\"", first);
        Assert.Contains("\"reason\": \"sealedNotSatisfied\"", first);
        Assert.Contains("\"originType\": \"SealedFoo\"", first);
        Assert.DoesNotContain("generatedAt", first, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- M2 A1：声明具体化重写（设计 §4.5/D10/D14） ----------

    private static readonly DynamicDesugarOptions RewriteOptions = new() { Mode = DynamicMode.Rewrite };

    private static DynamicApplyResult RunRewrite(
        string content,
        DynamicDesugarPreprocessor? preprocessor = null)
    {
        preprocessor ??= new DynamicDesugarPreprocessor(RewriteOptions);
        preprocessor.BindCompilation(BuildCompilation(content));
        return preprocessor.RewriteAll();
    }

    private static DynamicFileResult TestFileResult(DynamicApplyResult result)
        => Assert.Single(result.Files.Where(f => f.FilePath == TestPath));

    private static string? FirstLocalTypeText(string code)
    {
        var root = CSharpSyntaxTree.ParseText(code).GetCompilationUnitRoot();
        return root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.LocalDeclarationStatementSyntax>()
            .FirstOrDefault()?.Declaration.Type.ToString();
    }

    private const string SealedOnlySource = """
        sealed class SealedFoo { }

        class Sample
        {
            void F()
            {
                dynamic d = new SealedFoo();
            }
        }
        """;

    [Fact]
    public void A1_Rewrite_SealedClass_SingleDeclarationConcretized()
    {
        var result = RunRewrite(SealedOnlySource);
        var file = TestFileResult(result);

        Assert.True(file.Changed);
        Assert.DoesNotContain("dynamic", file.OutputCode);
        Assert.Equal("SealedFoo", FirstLocalTypeText(file.OutputCode));
        Assert.Contains("SealedFoo d = new SealedFoo();", file.OutputCode);

        // DS001 完成时措辞（Info；声明已落地）。
        var ds001 = Assert.Single(
            file.Diagnostics,
            d => d.Code == DynamicDesugarDiagnosticCodes.ProvenConcretized);
        Assert.Equal(DynamicDesugarSeverity.Info, ds001.Severity);
        Assert.Contains(
            "concretized dynamic local declaration to 'SealedFoo' at Test.cs(",
            ds001.Message);
        Assert.DoesNotContain("no rewrite has been performed", ds001.Message);

        Assert.False(result.HasErrors);
        Assert.Equal(1, result.Statistics.FilesTransformed);
        Assert.Equal(1, result.Statistics.DeclarationsRewritten);
    }

    [Fact]
    public void A1_Rewrite_ValueType_DeclarationConcretizedToInt()
    {
        const string source = """
            class Sample
            {
                void F()
                {
                    dynamic i = 42;
                }
            }
            """;

        var file = TestFileResult(RunRewrite(source));
        Assert.True(file.Changed);
        Assert.Equal("int", FirstLocalTypeText(file.OutputCode));
        Assert.Equal(1, file.Diagnostics.Count(d =>
            d.Code == DynamicDesugarDiagnosticCodes.ProvenConcretized
            && d.Message.Contains("concretized dynamic local declaration to 'int' at")));
    }

    [Fact]
    public void A1_Rewrite_String_DeclarationConcretizedToString()
    {
        const string source = """
            class Sample
            {
                void F()
                {
                    dynamic s = "hi";
                }
            }
            """;

        var file = TestFileResult(RunRewrite(source));
        Assert.True(file.Changed);
        Assert.Equal("string", FirstLocalTypeText(file.OutputCode));
        Assert.Contains(
            "to 'string' at",
            Assert.Single(
                file.Diagnostics,
                d => d.Code == DynamicDesugarDiagnosticCodes.ProvenConcretized).Message);
    }

    [Fact]
    public void A1_Rewrite_VarInferredDynamic_ConcretizedViaSourceReturnLookThrough()
    {
        // var 推断为 dynamic（静态 dynamic 返回方法）：M2 窄口径源内透视——静态方法、
        // 单一表达式体、唯一 sealed 返回类型 SealedFoo → var 位点证为 SealedFoo。
        const string source = """
            sealed class SealedFoo { }

            class Sample
            {
                static dynamic GetIt() => new SealedFoo();

                void F()
                {
                    var x = GetIt();
                }
            }
            """;

        var file = TestFileResult(RunRewrite(source));
        Assert.True(file.Changed);
        Assert.Equal("SealedFoo", FirstLocalTypeText(file.OutputCode));
        Assert.Contains("SealedFoo x = GetIt();", file.OutputCode);
        Assert.DoesNotContain("var x", file.OutputCode);
        Assert.Single(file.Diagnostics, d =>
            d.Code == DynamicDesugarDiagnosticCodes.ProvenConcretized
            && d.Message.Contains("to 'SealedFoo' at"));
    }

    [Fact]
    public void A1_Rewrite_ForeachDynamic_ConcretizedToElementType()
    {
        const string source = """
            sealed class SealedFoo { }

            class Sample
            {
                void F()
                {
                    var items = new System.Collections.Generic.List<SealedFoo>();
                    foreach (dynamic item in items)
                    {
                    }
                }
            }
            """;

        var file = TestFileResult(RunRewrite(source));
        Assert.True(file.Changed);
        Assert.DoesNotContain("dynamic", file.OutputCode);
        var root = CSharpSyntaxTree.ParseText(file.OutputCode).GetCompilationUnitRoot();
        var forEach = Assert.Single(root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ForEachStatementSyntax>());
        Assert.Equal("SealedFoo", forEach.Type.ToString());
        Assert.Equal("item", forEach.Identifier.Text);
    }

    [Fact]
    public void A1_Rewrite_MultipleDeclaratorsSameType_SharedDeclarationConcretizedOnce()
    {
        const string source = """
            sealed class SealedFoo { }

            class Sample
            {
                void F()
                {
                    dynamic a = new SealedFoo(), b = new SealedFoo();
                }
            }
            """;

        var result = RunRewrite(source);
        var file = TestFileResult(result);
        Assert.True(file.Changed);
        Assert.Contains("SealedFoo a = new SealedFoo(), b = new SealedFoo();", file.OutputCode);
        Assert.DoesNotContain("dynamic", file.OutputCode);
        // 共享 VariableDeclaration 只替换一次类型位 → 计 1 个声明。
        Assert.Equal(1, result.Statistics.DeclarationsRewritten);
    }

    [Fact]
    public void A1_Rewrite_MultipleDeclaratorsDifferentTypes_DeclarationRetainedAndDs010Error()
    {
        const string source = """
            sealed class SealedFoo { }

            class Sample
            {
                void F()
                {
                    dynamic a = new SealedFoo(), b = 1;
                }
            }
            """;

        var result = RunRewrite(source);
        var file = TestFileResult(result);

        // 异 T：共享声明整体保留（字节不变），两个分量各 DS010（MultipleAssignmentTypes）。
        Assert.False(file.Changed);
        Assert.Equal(source, file.OutputCode);
        Assert.Equal(2, file.Diagnostics.Count(d =>
            d.Code == DynamicDesugarDiagnosticCodes.LocalUnprovable
            && d.Severity == DynamicDesugarSeverity.Error));
        Assert.True(result.HasErrors);
        Assert.Equal(0, result.Statistics.FilesTransformed);
        Assert.Equal(0, result.Statistics.DeclarationsRewritten);
    }

    [Fact]
    public void A1_Rewrite_ThreeCandidates_TriedInOrderAndFirstAcceptingWins()
    {
        var preprocessor = new DynamicDesugarPreprocessor(RewriteOptions)
        {
            RewriteValidator = new RecordingValidator(),
        };
        var result = RunRewrite(SealedOnlySource, preprocessor);
        var file = TestFileResult(result);

        Assert.True(file.Changed);
        // 记录器拒绝前两个格式化候选、仅放行未格式化候选：验证候选按固定顺序逐个尝试，
        // 且候选 3（原文 trivia 兜底）真实可选——输出经其重新 parse + 同口径编译校验。
        var recorder = (RecordingValidator)preprocessor.RewriteValidator!;
        Assert.Equal(
            new[]
            {
                DynamicRewriteValidator.CandidateKind.ChangedSpanFormatted,
                DynamicRewriteValidator.CandidateKind.WholeFileFormatted,
                DynamicRewriteValidator.CandidateKind.Unformatted,
            },
            recorder.SeenKinds.ToArray());
        // 未格式化候选文本携带替换后的具体类型。
        Assert.Contains("SealedFoo d = new SealedFoo();", recorder.CandidateTexts[^1]);
        Assert.Contains("SealedFoo", file.OutputCode);
    }

    [Fact]
    public void A1_Rewrite_AllCandidatesRejected_SourceRetainedAndDs040Error()
    {
        var preprocessor = new DynamicDesugarPreprocessor(RewriteOptions)
        {
            RewriteValidator = new RejectAllValidator(),
        };
        var result = RunRewrite(SealedOnlySource, preprocessor);
        var file = TestFileResult(result);

        Assert.False(file.Changed);
        Assert.Equal(SealedOnlySource, file.OutputCode);
        var ds040 = Assert.Single(
            file.Diagnostics,
            d => d.Code == DynamicDesugarDiagnosticCodes.RewriteSyntaxInvalid);
        Assert.Equal(DynamicDesugarSeverity.Error, ds040.Severity);
        Assert.Contains("three-candidate validation", ds040.Message);
        Assert.Contains("source retained byte-for-byte", ds040.Message);
        Assert.True(result.HasErrors);
        Assert.Equal(0, result.Statistics.FilesTransformed);
        Assert.Equal(0, result.Statistics.DeclarationsRewritten);
    }

    [Fact]
    public void A1_Rewrite_Bom_PreservedOnOutput()
    {
        var source = "\uFEFF" + SealedOnlySource;
        var file = TestFileResult(RunRewrite(source));

        Assert.True(file.Changed);
        Assert.StartsWith("\uFEFF", file.OutputCode, StringComparison.Ordinal);
        Assert.False(file.OutputCode.StartsWith("\uFEFF\uFEFF", StringComparison.Ordinal));
        Assert.Contains("SealedFoo d = new SealedFoo();", file.OutputCode);
    }

    [Fact]
    public void A1_Rewrite_CleanFile_ByteCopiedAndSkippedClean()
    {
        const string source = """
            class Sample
            {
                void F()
                {
                    int x = 1;
                }
            }
            """;

        var result = RunRewrite(source);
        var file = TestFileResult(result);

        Assert.False(file.Changed);
        Assert.Equal(source, file.OutputCode);
        Assert.Empty(file.Diagnostics);
        Assert.Equal(0, result.Statistics.FilesTransformed);
        // 全部语法树（Test.cs + 全局 using 树）均无 dynamic → 全部 clean 字节复制。
        Assert.Equal(result.Statistics.FilesScanned, result.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void A1_Rewrite_Idempotent_SecondPassTransformsNothing()
    {
        var first = RunRewrite(SealedOnlySource);
        var rewritten = TestFileResult(first).OutputCode;
        Assert.True(TestFileResult(first).Changed);

        var secondPreprocessor = new DynamicDesugarPreprocessor(RewriteOptions);
        secondPreprocessor.BindCompilation(BuildCompilation(rewritten));
        var second = secondPreprocessor.RewriteAll();

        Assert.All(second.Files, file => Assert.False(file.Changed));
        Assert.Equal(0, second.Statistics.FilesTransformed);
        Assert.Equal(0, second.Statistics.DeclarationsRewritten);
        Assert.False(second.HasErrors);
        // 二次输出与一次输出字节一致。
        Assert.Equal(rewritten, TestFileResult(second).OutputCode);
    }

    [Fact]
    public void A1_AuditRegression_ProvenDeclarationSourceByteIdentical()
    {
        var result = RunAll(SealedOnlySource);
        var file = TestFileResult(result);

        Assert.False(file.Changed);
        Assert.Equal(SealedOnlySource, file.OutputCode);
        Assert.Equal("dynamic", FirstLocalTypeText(file.OutputCode));
        var ds001 = Assert.Single(
            file.Diagnostics,
            d => d.Code == DynamicDesugarDiagnosticCodes.ProvenConcretized);
        Assert.Equal(DynamicDesugarSeverity.Info, ds001.Severity);
        Assert.Contains("no rewrite has been performed", ds001.Message);
        Assert.DoesNotContain("concretized dynamic local declaration", ds001.Message);
        Assert.False(result.HasErrors);
    }

    // 三候选记录器：记录顺序与文本；拒绝两个格式化候选、仅让未格式化候选走默认校验，
    // 用于验证候选 3 兜底链真实生效。
    private sealed class RecordingValidator : DynamicRewriteValidator
    {
        public List<DynamicRewriteValidator.CandidateKind> SeenKinds { get; } = new();

        public List<string> CandidateTexts { get; } = new();

        public override bool Accepts(
            Candidate candidate,
            SyntaxTree originalTree,
            Compilation compilation,
            IReadOnlySet<string> baselineErrorKeys)
        {
            SeenKinds.Add(candidate.Kind);
            CandidateTexts.Add(candidate.Text);
            return candidate.Kind == DynamicRewriteValidator.CandidateKind.Unformatted
                && base.Accepts(candidate, originalTree, compilation, baselineErrorKeys);
        }
    }

    // 全失败保护：拒绝全部候选 → DS040 fail-closed。
    private sealed class RejectAllValidator : DynamicRewriteValidator
    {
        public override bool Accepts(
            Candidate candidate,
            SyntaxTree originalTree,
            Compilation compilation,
            IReadOnlySet<string> baselineErrorKeys)
            => false;
    }
}
