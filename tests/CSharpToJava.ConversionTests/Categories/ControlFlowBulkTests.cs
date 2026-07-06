using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// High-volume verification of C# control-flow constructs (if/else, else-if, for, foreach, while, do-while,
/// break, continue, switch, nesting) converting to Java equivalents with accurate brace expansion.
/// </summary>
public class ControlFlowBulkTests : ConversionTestBase
{
    [Fact]
    public void IfElse_ConvertsWithBraces()
    {
        var result = Convert("class C { public int M(int x) { if (x > 0) return 1; else return 2; } }");
        AssertConversion(result, "if (x > 0) {", "return 1;", "} else {", "return 2;");
    }

    [Fact]
    public void ElseIf_ConvertsToNestedElse()
    {
        var result = Convert("class C { public int M(int x) { if (x > 0) return 1; else if (x < 0) return -1; else return 0; } }");
        AssertConversion(result, "if (x > 0) {", "} else {", "if (x < 0) {", "return -1;", "} else {", "return 0;");
    }

    [Fact]
    public void ForLoop_ConvertsIdentically()
    {
        var result = Convert("class C { public void M() { for (int i = 0; i < 10; i++) { var x = i; } } }");
        AssertConversion(result, "for (int i = 0; i < 10; i++) {", "var x = i;");
    }

    [Fact]
    public void ForLoopWithBreak_ConvertsIdentically()
    {
        var result = Convert("class C { public void M() { for (int i = 0; i < 10; i++) { if (i == 5) break; } } }");
        AssertConversion(result, "for (int i = 0; i < 10; i++) {", "if (i == 5) {", "break;");
    }

    [Fact]
    public void ForLoopWithContinue_ConvertsIdentically()
    {
        var result = Convert("class C { public void M() { for (int i = 0; i < 10; i++) { if (i == 5) continue; } } }");
        AssertConversion(result, "for (int i = 0; i < 10; i++) {", "if (i == 5) {", "continue;");
    }

    [Fact]
    public void Foreach_ConvertsToEnhancedFor()
    {
        var result = Convert("class C { public void M() { foreach (var x in new int[] { 1, 2 }) { var y = x; } } }");
        AssertConversion(result, "for (int x : new int[] { 1, 2 }) {", "var y = x;");
    }

    [Fact]
    public void WhileLoop_ConvertsIdentically()
    {
        var result = Convert("class C { public void M() { int i = 0; while (i < 10) { i++; } } }");
        AssertConversion(result, "int i = 0;", "while (i < 10) {", "i++;");
    }

    [Fact]
    public void DoWhileLoop_ConvertsIdentically()
    {
        var result = Convert("class C { public void M() { int i = 0; do { i++; } while (i < 10); } }");
        AssertConversion(result, "do {", "i++;", "} while (i < 10);");
    }

    [Fact]
    public void SwitchMultiCase_ConvertsWithCases()
    {
        var result = Convert("class C { public int M(int x) { switch (x) { case 1: return 1; case 2: return 2; default: return 0; } } }");
        AssertConversion(result, "switch (x) {", "case 1:", "return 1;", "case 2:", "return 2;", "default:", "return 0;");
    }

    [Fact]
    public void NestedIf_ConvertsWithNestedBraces()
    {
        var result = Convert("class C { public void M(int x) { if (x > 0) { if (x < 10) { var y = x; } } } }");
        AssertConversion(result, "if (x > 0) {", "if (x < 10) {", "var y = x;");
    }

    [Fact]
    public void IfWithSingleStatement_GetsBraces()
    {
        var result = Convert("class C { public void M(int x) { if (x > 0) x = 1; } }");
        AssertConversion(result, "if (x > 0) {", "x = 1;");
    }

    [Fact]
    public void IfElseWithBraces_GetsNested()
    {
        var result = Convert("class C { public int M(int x) { if (x > 0) { return 1; } else { return 2; } } }");
        AssertConversion(result, "if (x > 0) {", "return 1;", "} else {", "return 2;");
    }

    [Fact]
    public void NestedForLoops_ConvertIdentically()
    {
        var result = Convert("class C { public void M() { for (int i = 0; i < 3; i++) { for (int j = 0; j < 3; j++) { var x = i + j; } } } }");
        AssertConversion(result, "for (int i = 0; i < 3; i++) {", "for (int j = 0; j < 3; j++) {", "var x = i + j;");
    }

    [Fact]
    public void WhileWithBreakContinue_Converts()
    {
        var result = Convert("class C { public void M() { int i = 0; while (i < 10) { i++; if (i == 5) continue; if (i == 9) break; } } }");
        AssertConversion(result, "while (i < 10) {", "i++;", "continue;", "break;");
    }

    [Fact]
    public void SwitchWithMultipleStatements_Converts()
    {
        var result = Convert("class C { public int M(int x) { int r = 0; switch (x) { case 1: r = 1; break; default: r = 0; break; } return r; } }");
        AssertConversion(result, "switch (x) {", "case 1:", "r = 1;", "break;", "default:", "r = 0;");
    }

    [Fact]
    public void ForeachOverStringArray_ConvertsToEnhancedFor()
    {
        var result = Convert("class C { public void M() { foreach (var s in new string[] { \"a\", \"b\" }) { var n = s.Length; } } }");
        AssertConversion(result, "for (String s : new String[] { \"a\", \"b\" }) {", "var n = s.length();");
    }

    [Fact]
    public void IfChainThreeBranches_Converts()
    {
        var result = Convert("class C { public int M(int x) { if (x == 1) return 1; else if (x == 2) return 2; else if (x == 3) return 3; else return 0; } }");
        AssertConversion(result, "if (x == 1) {", "} else {", "if (x == 2) {", "} else {", "if (x == 3) {", "return 0;");
    }

    [Fact]
    public void ControlFlowNoCSharpResidue()
    {
        var result = Convert("class C { public int M(int x) { if (x > 0) return 1; else return 2; } }");
        AssertNoCSharpResidue(result);
    }

    [Fact]
    public void ForLoopNoCSharpResidue()
    {
        var result = Convert("class C { public void M() { for (int i = 0; i < 10; i++) { var x = i; } } }");
        AssertNoCSharpResidue(result);
    }

    [Fact]
    public void SwitchNoCSharpResidue()
    {
        var result = Convert("class C { public int M(int x) { switch (x) { case 1: return 1; default: return 0; } } }");
        AssertNoCSharpResidue(result);
    }
}
