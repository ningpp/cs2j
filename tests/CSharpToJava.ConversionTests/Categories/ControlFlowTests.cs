using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# control-flow constructs to Java equivalents.
/// </summary>
public class ControlFlowTests : ConversionTestBase
{
    [Fact]
    public void IfElse_ConvertsToJavaIfElse()
    {
        var result = Convert("class C { public int M(int x) { if (x > 0) { return 1; } else { return 2; } } }");
        AssertConversion(result, "if (x > 0) {", "} else {");
    }

    [Fact]
    public void IfWithoutElse_ConvertsToJavaIf()
    {
        var result = Convert("class C { public void M(int x) { if (x > 0) { x = 1; } } }");
        AssertConversion(result, "if (x > 0) {");
    }

    [Fact]
    public void NestedIf_ConvertsToJava()
    {
        var result = Convert("class C { public int M(int x) { if (x > 0) { if (x < 10) { return x; } } return 0; } }");
        AssertConversion(result, "if (x > 0) {", "if (x < 10) {");
    }

    [Fact]
    public void SwitchInt_ConvertsToJavaSwitch()
    {
        var result = Convert("class C { public int M(int x) { switch (x) { case 1: return 10; default: return 0; } } }");
        AssertConversion(result, "switch (x) {", "case 1:", "default:");
    }

    [Fact]
    public void SwitchMultipleCases_ConvertsToJava()
    {
        var result = Convert("class C { public int M(int x) { switch (x) { case 1: case 2: return 10; default: return 0; } } }");
        AssertConversion(result, "case 1, 2:", "default:");
    }

    [Fact]
    public void SwitchString_ConvertsToJavaStringSwitch()
    {
        var result = Convert("class C { public int M(string s) { switch (s) { case \"a\": return 1; default: return 0; } } }");
        AssertConversion(result, "switch (s) {", "case \"a\":", "default:");
    }

    [Fact]
    public void ForLoop_ConvertsToJavaFor()
    {
        var result = Convert("class C { public int M() { int s = 0; for (int i = 0; i < 10; i++) { s += i; } return s; } }");
        AssertConversion(result, "for (int i = 0; i < 10; i++) {", "s += i;");
    }

    [Fact]
    public void ForEachArray_ConvertsToEnhancedFor()
    {
        var result = Convert("class C { public int M(int[] a) { int s = 0; foreach (var x in a) { s += x; } return s; } }");
        AssertConversion(result, "for (int x : a)");
    }

    [Fact]
    public void ForEachList_ConvertsToEnhancedFor()
    {
        var result = Convert("class C { public int M(System.Collections.Generic.List<int> l) { int s = 0; foreach (var x in l) { s += x; } return s; } }");
        AssertConversion(result, "import io.github.ningpp.compat.CSharpList;", "for (int x : l)");
    }

    [Fact]
    public void WhileLoop_ConvertsToJavaWhile()
    {
        var result = Convert("class C { public int M(int x) { int i = 0; while (i < x) { i++; } return i; } }");
        AssertConversion(result, "while (i < x) {", "i++;");
    }

    [Fact]
    public void DoWhileLoop_ConvertsToJavaDoWhile()
    {
        var result = Convert("class C { public int M(int x) { int i = 0; do { i++; } while (i < x); return i; } }");
        AssertConversion(result, "do {", "} while (i < x);");
    }

    [Fact]
    public void BreakInLoop_ConvertsToJavaBreak()
    {
        var result = Convert("class C { public int M() { for (int i = 0; i < 10; i++) { if (i == 5) break; } return 0; } }");
        AssertConversion(result, "break;");
    }

    [Fact]
    public void ContinueInLoop_ConvertsToJavaContinue()
    {
        var result = Convert("class C { public int M() { int s = 0; for (int i = 0; i < 10; i++) { if (i % 2 == 0) continue; s += i; } return s; } }");
        AssertConversion(result, "continue;");
    }

    [Fact]
    public void NestedForLoops_ConvertsToJava()
    {
        var result = Convert("class C { public int M() { int s = 0; for (int i = 0; i < 3; i++) { for (int j = 0; j < 3; j++) { s += i * j; } } return s; } }");
        AssertConversion(result, "for (int i = 0; i < 3; i++) {", "for (int j = 0; j < 3; j++) {");
    }

    [Fact]
    public void ReturnStatement_ConvertsToJava()
    {
        var result = Convert("class C { public int M() { return 42; } }");
        AssertConversion(result, "return 42;");
    }

    [Fact]
    public void EmptyStatement_ConvertsToJava()
    {
        var result = Convert("class C { public void M() { ; } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
    }

    [Fact]
    public void IfElseIfChain_ConvertsToJava()
    {
        var result = Convert("class C { public int M(int x) { if (x > 0) return 1; else if (x < 0) return -1; else return 0; } }");
        AssertConversion(result, "if (x > 0)", "if (x < 0)", "return 1;", "return -1;", "return 0;");
    }

    [Fact]
    public void SwitchWithBreakBetweenCases_ConvertsToJava()
    {
        var result = Convert("class C { public int M(int x) { int r = 0; switch (x) { case 1: r = 1; break; case 2: r = 2; break; } return r; } }");
        AssertConversion(result, "case 1:", "case 2:", "break;");
    }

    [Fact]
    public void ForLoopWithBreakAndContinue_ConvertsToJava()
    {
        var result = Convert("class C { public int M() { for (int i = 0; i < 100; i++) { if (i == 50) break; if (i % 3 == 0) continue; } return 0; } }");
        AssertConversion(result, "for (int i = 0; i < 100; i++) {", "break;", "continue;");
    }

    [Fact]
    public void WhileTrueLoop_ConvertsToJava()
    {
        var result = Convert("class C { public int M() { int i = 0; while (true) { i++; if (i > 10) break; } return i; } }");
        AssertConversion(result, "while (true) {", "break;");
    }

    [Fact]
    public void TernaryAsStatement_ConvertsToJava()
    {
        var result = Convert("class C { public int M(int x) { int y = x > 0 ? 1 : -1; return y; } }");
        AssertConversion(result, "int y = (x > 0 ? 1 : -1);");
    }

    [Fact]
    public void IfReturnEarly_ConvertsToJava()
    {
        var result = Convert("class C { public bool M(int x) { if (x < 0) return false; return true; } }");
        AssertConversion(result, "if (x < 0) {", "return false;", "return true;");
    }

    [Fact]
    public void SwitchEnum_ConvertsToJava()
    {
        var result = Convert("enum Color { Red, Green } class C { public int M(Color c) { switch (c) { case Color.Red: return 1; default: return 0; } } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "switch");
    }

    [Fact]
    public void ForEachWithIndexViaLocal_ConvertsToJava()
    {
        var result = Convert("class C { public int M(int[] a) { int idx = 0; foreach (var x in a) { idx++; } return idx; } }");
        AssertConversion(result, "for (int x : a)", "idx++;");
    }

    [Fact]
    public void NestedIfElseChain_ConvertsToJava()
    {
        var result = Convert("class C { public string M(int x) { if (x == 1) return \"a\"; else if (x == 2) return \"b\"; else if (x == 3) return \"c\"; else return \"z\"; } }");
        AssertConversion(result, "if (x == 1)", "if (x == 2)", "if (x == 3)", "return \"a\";", "return \"b\";", "return \"c\";", "return \"z\";");
    }

    [Fact]
    public void DoWhileWithBreak_ConvertsToJava()
    {
        var result = Convert("class C { public int M() { int i = 0; do { i++; if (i >= 5) break; } while (true); return i; } }");
        AssertConversion(result, "do {", "} while (true);", "break;");
    }

    [Fact]
    public void ForLoopDecrement_ConvertsToJava()
    {
        var result = Convert("class C { public int M() { int s = 0; for (int i = 10; i >= 0; i--) { s += i; } return s; } }");
        AssertConversion(result, "for (int i = 10; i >= 0; i--) {");
    }

    [Fact]
    public void BooleanConditionWhile_ConvertsToJava()
    {
        var result = Convert("class C { public int M(bool go) { int i = 0; while (go) { i++; } return i; } }");
        AssertConversion(result, "while (go) {");
    }

    [Fact]
    public void IfWithBlockAndSingleStatement_ConvertsToJava()
    {
        var result = Convert("class C { public void M(int x) { if (x > 0) { x = 1; } if (x < 0) x = -1; } }");
        AssertConversion(result, "if (x > 0) {", "if (x < 0) {", "x = -1;");
    }

    [Fact]
    public void SwitchWithMultipleStatementsPerCase_ConvertsToJava()
    {
        var result = Convert("class C { public int M(int x) { int a = 0; int b = 0; switch (x) { case 1: a = 1; b = 2; break; } return a + b; } }");
        AssertConversion(result, "case 1:", "a = 1;", "b = 2;", "break;");
    }

    [Fact]
    public void ForEachOverStringArray_ConvertsToJava()
    {
        var result = Convert("class C { public int M(string[] a) { int s = 0; foreach (var v in a) { s += v.Length; } return s; } }");
        AssertConversion(result, "for (String v : a)", "v.length()");
    }

    [Fact]
    public void NestedForeachLoops_ConvertsToJava()
    {
        var result = Convert("class C { public int M(int[][] a) { int s = 0; foreach (var row in a) { foreach (var v in row) { s += v; } } return s; } }");
        AssertConversion(result, "for (int[] row : a)", "for (int v : row)");
    }

    [Fact]
    public void IfElseReturnTernaryEquivalent_ConvertsToJava()
    {
        var result = Convert("class C { public int M(int x) { if (x > 0) return x; else return 0; } }");
        AssertConversion(result, "if (x > 0) {", "return x;", "return 0;");
    }

    [Fact]
    public void ContinueInWhileLoop_ConvertsToJava()
    {
        var result = Convert("class C { public int M() { int s = 0; int i = 0; while (i < 10) { i++; if (i % 2 == 0) continue; s += i; } return s; } }");
        AssertConversion(result, "while (i < 10) {", "continue;");
    }

    [Fact]
    public void ForLoopWithDeclarationOnly_ConvertsToJava()
    {
        var result = Convert("class C { public void M() { for (int i = 0; i < 5; i++) { } } }");
        AssertConversion(result, "for (int i = 0; i < 5; i++) {");
    }

    [Fact]
    public void IfInsideFor_ConvertsToJava()
    {
        var result = Convert("class C { public int M(int[] a) { int c = 0; for (int i = 0; i < a.Length; i++) { if (a[i] > 0) c++; } return c; } }");
        AssertConversion(result, "for (int i = 0; i < a.length; i++) {", "if (a[i] > 0) {", "c++;");
    }

    [Fact]
    public void SwitchDefaultAtTop_ConvertsToJava()
    {
        var result = Convert("class C { public int M(int x) { switch (x) { default: return -1; case 0: return 0; } } }");
        AssertConversion(result, "default:", "case 0:");
    }

    [Fact]
    public void WhileLoopWithCompoundCondition_ConvertsToJava()
    {
        var result = Convert("class C { public int M(int a, int b) { int i = 0; while (i < a && i < b) { i++; } return i; } }");
        AssertConversion(result, "while (i < a && i < b) {");
    }

    [Fact]
    public void ForLoopSumPattern_ConvertsToJava()
    {
        var result = Convert("class C { public int M() { int sum = 0; for (int i = 1; i <= 100; i++) { sum += i; } return sum; } }");
        AssertConversion(result, "for (int i = 1; i <= 100; i++) {", "sum += i;");
    }

    [Fact]
    public void IfElseVariableAssignment_ConvertsToJava()
    {
        var result = Convert("class C { public int M(int x) { int r; if (x > 0) r = 1; else r = 2; return r; } }");
        AssertConversion(result, "if (x > 0) {", "r = 1;", "r = 2;");
    }

    [Fact]
    public void DoWhileFalseCondition_ConvertsToJava()
    {
        var result = Convert("class C { public int M() { int i = 0; do { i++; } while (i < 0); return i; } }");
        AssertConversion(result, "do {", "} while (i < 0);");
    }

    [Fact]
    public void SwitchChar_ConvertsToJava()
    {
        var result = Convert("class C { public int M(char c) { switch (c) { case 'a': return 1; default: return 0; } } }");
        AssertConversion(result, "switch (c) {", "case 'a':", "default:");
    }

    [Fact]
    public void NestedControlFlow_ConvertsToJava()
    {
        var result = Convert("class C { public int M(int[] a) { int total = 0; for (int i = 0; i < a.Length; i++) { if (a[i] < 0) continue; for (int j = 0; j < a[i]; j++) { total += j; } } return total; } }");
        AssertConversion(result, "for (int i = 0; i < a.length; i++) {", "continue;", "for (int j = 0; j < a[i]; j++) {");
    }

    [Fact]
    public void IfWithMethodCall_ConvertsToJava()
    {
        var result = Convert("class C { public bool Check(int x) { return x > 0; } public int M(int x) { if (Check(x)) return 1; return 0; } }");
        AssertConversion(result, "if (check(x)) {", "return 1;");
    }

    [Fact]
    public void ForLoopWithTwoInits_ConvertsToJava()
    {
        var result = Convert("class C { public int M() { int s = 0; for (int i = 0, n = 10; i < n; i++) { s += i; } return s; } }");
        AssertConversion(result, "for (int i = 0, n = 10; i < n; i++) {");
    }

    [Fact]
    public void BreakOuterLoopViaLabel_ConvertsToJava()
    {
        var result = Convert("class C { public void M() { for (int i = 0; i < 10; i++) { for (int j = 0; j < 10; j++) { if (j == 5) break; } } } }");
        AssertConversion(result, "for (int i = 0; i < 10; i++) {", "for (int j = 0; j < 10; j++) {", "break;");
    }

    [Fact]
    public void ReturnInsideForEach_ConvertsToJava()
    {
        var result = Convert("class C { public int M(int[] a) { foreach (var x in a) { if (x > 5) return x; } return 0; } }");
        AssertConversion(result, "for (int x : a)", "if (x > 5) {", "return x;");
    }

    [Fact]
    public void ComplexConditionIf_ConvertsToJava()
    {
        var result = Convert("class C { public bool M(int a, int b, int c) { if (a > 0 && b > 0 || c > 0) return true; return false; } }");
        AssertConversion(result, "if (a > 0 && b > 0 || c > 0) {", "return true;");
    }

    [Fact]
    public void SwitchWithReturnInCase_ConvertsToJava()
    {
        var result = Convert("class C { public string M(int x) { switch (x) { case 1: return \"one\"; case 2: return \"two\"; default: return \"?\"; } } }");
        AssertConversion(result, "case 1:", "case 2:", "default:", "return \"one\";");
    }
}
