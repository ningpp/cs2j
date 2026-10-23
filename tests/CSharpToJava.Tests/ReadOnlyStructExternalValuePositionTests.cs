using CSharpToJava.Core.ReadOnlyStructMaker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// make-readonly 回归：外部类对目标 readonly struct 字段的<b>值位</b>写
/// （声明初值/实参/赋值 RHS 中的 <c>r.F++</c> 与 <c>r.F = v</c>）必须提升为
/// 「temp 读 + With 写回」语句，表达式替换为 temp——真实链 java-keywords
/// 阶段 CS0122（XmlBinaryReader.AddName/ReadName\_Ref/NameFlush 的
/// _symbolTables.symCount++ / qnameCount++ / 连环赋值未重写）。
/// </summary>
public class ReadOnlyStructExternalValuePositionTests
{
    private static ReadOnlyStructMakerResult RunMaker(string source, ReadOnlyStructMakerOptions? options = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return new Core.ReadOnlyStructMaker.ReadOnlyStructMaker()
            .MakeReadOnly(tree, compilation.GetSemanticModel(tree), options);
    }

    private const string Source = """
        class Reader
        {
            SymbolTables _symbolTables;

            internal int AddName()
            {
                int symNum = _symbolTables.symCount++;
                return symNum;
            }

            internal int ReadNameRef()
            {
                int qnameNum = 0;
                qnameNum = _symbolTables.qnameCount++;
                return qnameNum;
            }

            internal void NameFlush()
            {
                _symbolTables.symCount = _symbolTables.qnameCount = 1;
            }
        }

        struct SymbolTables
        {
            public int symCount;
            public int qnameCount;
        }
        """;

    [Fact]
    public void ExternalIncrement_InDeclarationInitializer_IsHoistedToTempAndWith()
    {
        var result = RunMaker(Source);

        Assert.True(result.Changed);
        // 原始字段自增不得残留（CS0122：readonly 字段不可变）。
        Assert.DoesNotContain("_symbolTables.symCount++", result.OutputCode, StringComparison.Ordinal);
        // temp 读 + With 写回，声明初值取 temp（后缀自增取旧值）。
        Assert.Contains("WithsymCount(", result.OutputCode, StringComparison.Ordinal);
        Assert.Matches(@"int symNum = __cs2jOld\w+;", result.OutputCode);
    }

    [Fact]
    public void ExternalIncrement_InAssignmentRhs_IsHoistedToTempAndWith()
    {
        var result = RunMaker(Source);

        Assert.True(result.Changed);
        Assert.DoesNotContain("_symbolTables.qnameCount++", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("WithqnameCount(", result.OutputCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalChainedAssignment_InStatement_IsExpandedToWithChain()
    {
        var result = RunMaker(Source);

        Assert.True(result.Changed);
        // 连环赋值 _symbolTables.symCount = _symbolTables.qnameCount = 1 中，
        // 内层 qnameCount 写不得以裸字段赋值残留（CS0122）。
        Assert.DoesNotContain("qnameCount = 1", result.OutputCode.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("symCount = 1", result.OutputCode.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("WithqnameCount(", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("WithsymCount(", result.OutputCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ChainedAssignment_InsideSwitchCase_HoistsWithinSection()
    {
        // 状态机形态：值位连环赋值位于后面的 case，temp 声明必须落在同一
        // section 内（真实链：StringConcat case 5 被提升到 case 0 → CS0103/CS0841）。
        var src = """
        class Reader
        {
            SymbolTables _symbolTables;

            internal int Run(int state)
            {
                switch (state)
                {
                    case 0:
                        return 0;
                    case 5:
                        int n = _symbolTables.symCount = _symbolTables.qnameCount = 3;
                        return n;
                    default:
                        return -1;
                }
            }
        }

        struct SymbolTables
        {
            public int symCount;
            public int qnameCount;
        }
        """;
        var result = RunMaker(src);

        Assert.True(result.Changed);
        var output = result.OutputCode.Replace("\r\n", "\n");
        Assert.DoesNotContain("symCount = _symbolTables.qnameCount = 3", output, StringComparison.Ordinal);
        // 提升产生的 temp 声明不得越出所属 switch section（case 0 中不得出现）。
        var case0 = output[output.IndexOf("case 0:", StringComparison.Ordinal)..output.IndexOf("case 5:", StringComparison.Ordinal)];
        Assert.DoesNotContain("__cs2jOld", case0, StringComparison.Ordinal);
    }
}
