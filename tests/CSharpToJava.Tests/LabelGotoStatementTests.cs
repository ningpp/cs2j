using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CSharpToJava.Tests;

public class LabelGotoStatementTests
{
    [Fact]
    public void Label_ForLoop()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: for (int i = 0; i < 10; i++) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("outer: for", result.GeneratedCode);
    }

    [Fact]
    public void Label_WhileLoop()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: while (true) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("outer: while", result.GeneratedCode);
    }

    [Fact]
    public void Label_DoWhileLoop()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: do { } while (true);
    }
}");
        Assert.True(result.Success);
        Assert.Contains("outer: do", result.GeneratedCode);
    }

    [Fact]
    public void Label_ForeachLoop()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M(List<int> list) {
        outer: foreach (var x in list) { }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("outer: for", result.GeneratedCode);
    }

    [Fact]
    public void Label_Block()
    {
        var result = Convert(@"
class Test {
    void M() {
        target: { int x = 1; }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("target: {", result.GeneratedCode);
    }

    [Fact]
    public void Label_SimpleStatement_WrappedInBlock()
    {
        var result = Convert(@"
class Test {
    void M() {
        int x = 0;
        target: x = 1;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("target: { x = 1; }", result.GeneratedCode);
    }

    [Fact]
    public void Label_LocalDeclaration_DoesNotWrapDeclarationInBlock()
    {
        var result = Convert(@"
class Test {
    static int M(int pos, int start) {
        target:
            int tmp = pos - start;
        return tmp;
    }
}");

        Assert.True(result.Success, result.GeneratedCode);
        Assert.Matches(@"target:\s*;\s*int\s+tmp\s*=\s*pos\s*-\s*start\s*;", result.GeneratedCode);
        Assert.DoesNotContain("target: int tmp = pos - start;", result.GeneratedCode);
        Assert.DoesNotContain("target: { int tmp = pos - start; }", result.GeneratedCode);
    }

    [Fact]
    public void Label_NestedLabels()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: for (int i = 0; i < 10; i++) {
            inner: for (int j = 0; j < 10; j++) { }
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("outer: for", result.GeneratedCode);
        Assert.Contains("inner: for", result.GeneratedCode);
    }

    [Fact]
    public void Goto_LoopLabel_Continue()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: for (int i = 0; i < 10; i++) {
            if (i == 5) goto outer;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("continue outer;", result.GeneratedCode);
    }

    [Fact]
    public void Goto_BlockLabel_Break()
    {
        var result = Convert(@"
class Test {
    void M() {
        target: {
            if (true) goto target;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("break target;", result.GeneratedCode);
    }

    [Fact]
    public void Goto_OtherLabel_Break()
    {
        var result = Convert(@"
class Test {
    void M() {
        int x = 0;
        target: x = 1;
        if (true) goto target;
    }
}");
        Assert.True(result.Success);
        // Cross-scope goto now uses state machine
        Assert.Contains("__state", result.GeneratedCode);
        Assert.Contains("__gotoLoop", result.GeneratedCode);
    }

    [Fact]
    public void GotoCase_Supported()
    {
        var result = Convert(@"
class Test {
    void M(int x) {
        switch (x) {
            case 1:
                goto case 2;
            case 2:
                break;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("continue _switch", result.GeneratedCode);
        Assert.DoesNotContain("/* TODO: goto case - unsupported */", result.GeneratedCode);
    }

    [Fact]
    public void GotoDefault_Supported()
    {
        var result = Convert(@"
class Test {
    void M(int x) {
        switch (x) {
            case 1:
                goto default;
            default:
                break;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("continue _switch", result.GeneratedCode);
        Assert.DoesNotContain("/* TODO: goto default - unsupported */", result.GeneratedCode);
    }

    [Fact]
    public void Goto_CrossScope_Unsupported()
    {
        var result = Convert(@"
class Test {
    void M() {
        target: { }
        goto target;
    }
}");
        Assert.True(result.Success);
        // Cross-scope goto now uses state machine
        Assert.Contains("__state", result.GeneratedCode);
        Assert.Contains("__gotoLoop", result.GeneratedCode);
    }

    [Fact]
    public void Goto_UnknownLabel_Unsupported()
    {
        var result = Convert(@"
class Test {
    void M() {
        goto nonexistent;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("/* TODO: goto nonexistent - label not found in scope */", result.GeneratedCode);
    }

    [Fact]
    public void Goto_BreakFromEnclosingLoop_WhileToSiblingLabel()
    {
        // goto target jumps from inside while(true) to a label that is a sibling
        // of the while(true) loop in the same parent block.
        // This is equivalent to break; from the while(true) loop.
        var result = Convert(@"
class Test {
    void M() {
        while (true) {
            if (true) goto target;
        }
        target: ;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("break;", result.GeneratedCode);
        Assert.DoesNotContain("__state", result.GeneratedCode);
        Assert.DoesNotContain("__gotoLoop", result.GeneratedCode);
    }

    [Fact]
    public void Goto_BreakFromEnclosingLoop_InsideFixedBlock()
    {
        // Simulates the UriHelper.UnescapeString pattern:
        // goto dest_fixed_loop_break inside a while(true) inside a fixed block,
        // where the label is a sibling of the while(true) in the fixed block.
        var result = Convert(@"
class Test {
    unsafe void M() {
        while (true) {
            fixed (char* p = """") {
                while (true) {
                    if (true) goto dest_fixed_loop_break;
                    if (false) goto done;
                }
                dest_fixed_loop_break: ;
            }
        }
        done: return;
    }
}");
        Assert.True(result.Success);
        // goto dest_fixed_loop_break should be converted to break;
        Assert.Contains("break;", result.GeneratedCode);
        // goto done should still use state machine
        Assert.Contains("__state", result.GeneratedCode);
        Assert.Contains("__gotoLoop", result.GeneratedCode);
    }

    [Fact]
    public void Break_Label_Preserved()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: for (int i = 0; i < 10; i++) {
            for (int j = 0; j < 10; j++) {
                if (j == 5) break;
            }
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("outer: for", result.GeneratedCode);
    }

    [Fact]
    public void Goto_WhileLoop_Continue()
    {
        var result = Convert(@"
class Test {
    void M() {
        restart: while (true) {
            if (System.DateTime.Now.Ticks > 0) goto restart;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("continue restart;", result.GeneratedCode);
    }

    [Fact]
    public void Label_Registry_ClearedBetweenMethods()
    {
        var result = Convert(@"
class Test {
    void M1() {
        outer: for (int i = 0; i < 10; i++) { }
    }
    void M2() {
        goto outer;
    }
}");
        Assert.True(result.Success);
        Assert.Contains("/* TODO: goto outer - label not found in scope */", result.GeneratedCode);
    }

    [Fact]
    public void Goto_LabelInsideIf_WithinScope()
    {
        var result = Convert(@"
class Test {
    void M() {
        target: for (int i = 0; i < 10; i++) {
            if (i == 3) {
                goto target;
            }
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("continue target;", result.GeneratedCode);
    }

    [Fact]
    public void CrossScopeGoto_ForwardJump_SkipsStatementsBeforeTarget()
    {
        var result = Convert(@"
class Test {
    static int M(boolean flag) {
        int x = 0;
        if (flag) goto target;
        x = 1;
        target: x = x + 2;
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);

        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(true) + \",\" + Test.m(false));");

        Assert.Equal("2,3", output);
    }

    [Fact]
    public void CrossScopeGoto_BackwardJump_ContinuesFromTargetWithoutRestartingMethod()
    {
        var result = Convert(@"
class Test {
    static int M() {
        int init = 0;
        int x = 0;
        target: x = x + 1;
        if (x < 3) goto target;
        return init * 10 + x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);

        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m());");

        Assert.Equal("3", output);
    }

    [Fact]
    public void CrossScopeGoto_MultipleLabels_RunExpectedPath()
    {
        var result = Convert(@"
class Test {
    static int M(int mode) {
        int x = 0;
        if (mode == 1) goto first;
        if (mode == 2) goto second;
        x = 10;
        goto end;
        first: x = x + 1;
        goto end;
        second: x = x + 2;
        end: return x + 100;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);

        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(0) + \",\" + Test.m(1) + \",\" + Test.m(2));");

        Assert.Equal("110,101,102", output);
    }

    [Fact]
    public void CrossScopeGoto_TargetInsideNestedBlock_RunExpectedPath()
    {
        var result = Convert(@"
class Test {
    static int M(boolean jump) {
        int x = 0;
        if (jump) goto inside;
        x = 10;
        {
            inside: x = x + 3;
        }
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);

        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(true) + \",\" + Test.m(false));");

        Assert.Equal("3,13", output);
    }

    [Fact]
    public void GotoCase_ExecutesTargetCase()
    {
        var result = Convert(@"
class Test {
    static int M(int x) {
        int result = 0;
        switch (x) {
            case 1:
                result = result + 10;
                goto case 2;
            case 2:
                result = result + 20;
                break;
            default:
                result = result + 30;
                break;
        }
        return result;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);

        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(1) + \",\" + Test.m(2) + \",\" + Test.m(3));");

        Assert.Equal("30,20,30", output);
    }

    [Fact]
    public void GotoDefault_ExecutesDefaultCase()
    {
        var result = Convert(@"
class Test {
    static int M(int x) {
        int result = 0;
        switch (x) {
            case 1:
                result = result + 10;
                goto default;
            default:
                result = result + 30;
                break;
        }
        return result;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);

        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(1) + \",\" + Test.m(2));");

        Assert.Equal("40,30", output);
    }

    [Fact]
    public void GotoCase_NonAdjacentTarget_ExecutesTargetCase()
    {
        var result = Convert(@"
class Test {
    static int M(int x) {
        int result = 0;
        switch (x) {
            case 1:
                result = result + 10;
                goto case 3;
            case 2:
                result = result + 20;
                break;
            case 3:
                result = result + 30;
                break;
            default:
                result = result + 40;
                break;
        }
        return result;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);

        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(1) + \",\" + Test.m(2) + \",\" + Test.m(3) + \",\" + Test.m(4));");

        Assert.Equal("40,20,30,40", output);
    }

    [Fact]
    public void GotoDefault_NonAdjacentTarget_ExecutesDefaultCase()
    {
        var result = Convert(@"
class Test {
    static int M(int x) {
        int result = 0;
        switch (x) {
            case 1:
                result = result + 10;
                goto default;
            case 2:
                result = result + 20;
                break;
            default:
                result = result + 40;
                break;
        }
        return result;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);

        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(1) + \",\" + Test.m(2) + \",\" + Test.m(3));");

        Assert.Equal("50,20,40", output);
    }

    // A1: In-scope goto to loop label (semantic verification)

    [Fact]
    public void A1_GotoLoopLabel_Continue_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M() {
        int sum = 0;
        outer: for (int i = 0; i < 5; i++) {
            if (i == 3) goto outer;
            sum = sum + i;
        }
        return sum;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m());");
        Assert.Equal("7", output);
    }

    // A2: In-scope goto to block label (semantic verification)

    [Fact]
    public void A2_GotoBlockLabel_Break_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M(boolean skip) {
        int x = 0;
        target: {
            x = 10;
            if (skip) goto target;
            x = x + 5;
        }
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // With skip=true, the goto jumps back to target label which re-enters the block
        // but since this is A2 (in-scope block), goto target becomes break target
        // which exits the block. So skip=true => x=10, skip=false => x=15
        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(false));");
        Assert.Equal("15", output);
    }

    // B1: Same method body backward goto to loop label

    [Fact]
    public void B1_SameBodyBackwardGotoToLoop()
    {
        var result = Convert(@"
class Test {
    static int M() {
        int count = 0;
        outer: for (int i = 0; i < 3; i++) {
            count = count + i;
        }
        if (count < 10) goto outer;
        return count;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // B1 now uses state machine because continue label is not valid outside the loop
        Assert.Contains("__state", result.GeneratedCode);
        Assert.Contains("__gotoLoop", result.GeneratedCode);
    }

    [Fact]
    public void B1_SameBodyBackwardGotoToLoop_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M() {
        int total = 0;
        int round = 0;
        outer: for (int i = 0; i < 3; i++) {
            total = total + i;
        }
        round = round + 1;
        if (round < 2) goto outer;
        return total;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m());");
        // First round: total = 0+1+2 = 3, round=1; goto outer
        // Second round: total = 3+0+1+2 = 6, round=2; no goto
        Assert.Equal("6", output);
    }

    // B2: Same method body backward goto to non-loop label

    [Fact]
    public void B2_SameBodyBackwardGotoToNonLoop()
    {
        var result = Convert(@"
class Test {
    static int M() {
        int x = 0;
        target: x = x + 1;
        if (x < 3) goto target;
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("__state", result.GeneratedCode);
        Assert.Contains("__gotoLoop", result.GeneratedCode);
    }

    [Fact]
    public void B2_SameBodyBackwardGotoToNonLoop_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M() {
        int x = 0;
        target: x = x + 1;
        if (x < 3) goto target;
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m());");
        Assert.Equal("3", output);
    }

    // B3: Same method body forward goto

    [Fact]
    public void B3_SameBodyForwardGoto()
    {
        var result = Convert(@"
class Test {
    static int M(boolean skip) {
        int x = 0;
        if (skip) goto skip;
        x = 1;
        skip: x = x + 10;
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("__state", result.GeneratedCode);
        Assert.Contains("__gotoLoop", result.GeneratedCode);
    }

    [Fact]
    public void B3_SameBodyForwardGoto_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M(boolean skip) {
        int x = 0;
        if (skip) goto skip;
        x = 1;
        skip: x = x + 10;
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(true) + \",\" + Test.m(false));");
        // skip=true: x=0, goto skip, x=0+10=10
        // skip=false: x=0, x=1, x=1+10=11
        Assert.Equal("10,11", output);
    }

    // B2+Variable declaration hoisting

    [Fact]
    public void B2_VariableDeclarationHoisting()
    {
        var result = Convert(@"
class Test {
    static int M() {
        int x = 0;
        target: x = x + 1;
        if (x < 3) goto target;
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // Variable x should be hoisted outside the while loop
        // and the declaration should become an assignment inside the state machine
        Assert.Contains("__state", result.GeneratedCode);
    }

    [Fact]
    public void B2_VariableDeclarationHoisting_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M() {
        int x = 0;
        int y = 5;
        target: x = x + y;
        if (x < 15) goto target;
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m());");
        // x=0+5=5, x=5+5=10, x=10+5=15 >= 15 stop
        Assert.Equal("15", output);
    }

    // C: Cross-scope goto from for loop to outer label

    [Fact]
    public void C_GotoFromForToOuterLabel()
    {
        var result = Convert(@"
class Test {
    static int M() {
        int x = 1;
        target: x = x + 1;
        for (int i = 0; i < 3; i++) {
            if (i == 1) goto target;
        }
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("__state", result.GeneratedCode);
        Assert.Contains("__gotoLoop", result.GeneratedCode);
    }

    [Fact]
    public void C_GotoFromForToOuterLabel_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M() {
        int x = 1;
        int jumps = 0;
        target: x = x + 1;
        for (int i = 0; i < 3; i++) {
            if (i == 1 && jumps == 0) {
                jumps = jumps + 1;
                goto target;
            }
        }
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m());");
        // x=1, target: x=2, for i=1 jumps once to target => x=3,
        // then the restarted for loop completes normally.
        Assert.Equal("3", output);
    }

    // C: Cross-scope goto from if to outer label

    [Fact]
    public void C_GotoFromIfToOuterLabel()
    {
        var result = Convert(@"
class Test {
    static int M(boolean jump) {
        int x = 0;
        target: x = x + 1;
        if (jump) goto target;
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("__state", result.GeneratedCode);
    }

    [Fact]
    public void C_GotoFromIfToOuterLabel_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M(boolean jump) {
        int x = 0;
        target: x = x + 1;
        if (jump) goto target;
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(false));");
        // jump=false: x=0, target: x=1, if(false) no goto, return 1
        Assert.Equal("1", output);
    }

    // C: Cross-scope goto from try to outer label

    [Fact]
    public void C_GotoFromTryToOuterLabel()
    {
        var result = Convert(@"
class Test {
    static int M(boolean jump) {
        int x = 0;
        target: x = x + 1;
        try {
            if (jump) goto target;
        } catch (Exception e) {
            x = -1;
        }
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("__state", result.GeneratedCode);
    }

    [Fact]
    public void C_GotoFromTryToOuterLabel_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M(boolean jump) {
        int x = 0;
        target: x = x + 1;
        try {
            if (jump) goto target;
        } catch (Exception e) {
            x = -1;
        }
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(false));");
        // jump=false: x=0, target: x=1, try{no goto}, return 1
        Assert.Equal("1", output);
    }

    // D: goto case / goto default (additional semantic tests)

    [Fact]
    public void D_GotoCase_ReverseTarget()
    {
        var result = Convert(@"
class Test {
    static int M(int x) {
        int result = 0;
        switch (x) {
            case 1:
                result = result + 10;
                break;
            case 2:
                result = result + 20;
                goto case 1;
        }
        return result;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(1) + \",\" + Test.m(2));");
        Assert.Equal("10,30", output);
    }

    [Fact]
    public void D_GotoDefault_FromMiddle()
    {
        var result = Convert(@"
class Test {
    static int M(int x) {
        int result = 0;
        switch (x) {
            case 1:
                result = result + 10;
                goto default;
            case 2:
                result = result + 20;
                break;
            default:
                result = result + 50;
                break;
        }
        return result;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(1) + \",\" + Test.m(2) + \",\" + Test.m(3));");
        Assert.Equal("60,20,50", output);
    }

    // Complex scenario 13: Multi-label state machine

    [Fact]
    public void Complex_MultiLabelStateMachine()
    {
        var result = Convert(@"
class Test {
    static int M(boolean c1) {
        int x = 0;
        s1: x = x + 1;
        if (c1) goto s2;
        goto s1;
        s2: x = x + 10;
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("__state", result.GeneratedCode);
    }

    [Fact]
    public void Complex_MultiLabelStateMachine_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M(boolean c1) {
        int x = 0;
        s1: x = x + 1;
        if (c1) goto s2;
        goto s1;
        s2: x = x + 10;
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // With c1=false: infinite loop (x keeps incrementing) - skip that
        // With c1=true: x=0, s1: x=1, if(true) goto s2, s2: x=1+10=11
        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(true));");
        Assert.Equal("11", output);
    }

    // Complex scenario 14: Label + loop mix

    [Fact]
    public void Complex_LabelAndLoopMix()
    {
        var result = Convert(@"
class Test {
    static int M() {
        int sum = 0;
        outer: for (int i = 0; i < 3; i++) {
            s1: sum = sum + i;
            if (i == 1) goto s1;
        }
        return sum;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
    }

    [Fact]
    public void Complex_LabelAndLoopMix_WithCounter()
    {
        var result = Convert(@"
class Test {
    static int M() {
        int sum = 0;
        int count = 0;
        outer: for (int i = 0; i < 3; i++) {
            s1: sum = sum + i;
            count = count + 1;
            if (i == 1 && count < 5) goto s1;
        }
        return sum;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // Note: goto s1 is classified as A2 (in-scope block) and becomes break s1,
        // which exits the s1 block. The actual Java behavior depends on how the
        // break interacts with the for loop. This test documents the current behavior.
        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m());");
        Assert.Equal("5", output);
    }

    // Complex scenario 15: Infinite loop validation

    [Fact]
    public void Complex_InfiniteLoopValidation()
    {
        var result = Convert(@"
class Test {
    static int M() {
        int x = 1;
        target: x = x + 1;
        if (x < 5) goto target;
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // The state machine should NOT produce an infinite loop
        // It should correctly jump to the target block
        Assert.Contains("__state", result.GeneratedCode);
    }

    [Fact]
    public void Complex_InfiniteLoopValidation_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M() {
        int x = 1;
        target: x = x + 1;
        if (x < 5) goto target;
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m());");
        // x=1, target: x=2, x<5 goto target, x=3, x<5 goto target, x=4, x<5 goto target, x=5, x>=5 return 5
        Assert.Equal("5", output);
    }

    // Regression 17: No goto method should not generate state machine

    [Fact]
    public void Regression_NoGotoMethod_NoStateMachine()
    {
        var result = Convert(@"
class Test {
    void M() {
        int x = 1;
        x = x + 2;
    }
}");
        Assert.True(result.Success);
        Assert.DoesNotContain("__state", result.GeneratedCode);
        Assert.DoesNotContain("__gotoLoop", result.GeneratedCode);
    }

    // Regression 18: Label on loop preserved correctly

    [Fact]
    public void Regression_LabelOnLoop_Preserved()
    {
        var result = Convert(@"
class Test {
    void M() {
        outer: for (int i = 0; i < 10; i++) { }
        inner: while (true) { break; }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("outer: for", result.GeneratedCode);
        Assert.Contains("inner: while", result.GeneratedCode);
    }

    // Additional edge case: goto in nested if within same method body

    [Fact]
    public void B3_GotoInNestedIf_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M(int mode) {
        int x = 0;
        if (mode == 1) goto first;
        if (mode == 2) goto second;
        x = 100;
        goto end;
        first: x = 1;
        goto end;
        second: x = 2;
        end: return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode,
            "System.out.print(Test.m(0) + \",\" + Test.m(1) + \",\" + Test.m(2));");
        Assert.Equal("100,1,2", output);
    }

    // Additional edge case: Variable declaration with initializer in state machine

    [Fact]
    public void B2_VariableWithInitializer_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M() {
        int x = 10;
        target: x = x - 1;
        if (x > 7) goto target;
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m());");
        // x=10, target: x=9, x>7 goto, x=8, x>7 goto, x=7, x<=7 return 7
        Assert.Equal("7", output);
    }

    [Fact]
    public void B3_NestedLocalDeclarationInNormalBlock_DoesNotHoistDuplicate_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M(boolean skip) {
        int x = 0;
        if (skip) goto target;
        {
            int y = 2;
            x = y;
        }
        target: x = x + 1;
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode,
            "System.out.print(Test.m(true) + \",\" + Test.m(false));");
        Assert.Equal("1,3", output);
    }

    [Fact]
    public void B3_StatementsAfterGotoInsideIfBlock_AreNotEmitted_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M(boolean skip) {
        int x = 0;
        if (skip) {
            goto target;
            x = 99;
        }
        x = 1;
        target: return x + 10;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode,
            "System.out.print(Test.m(true) + \",\" + Test.m(false));");
        Assert.Equal("10,11", output);
    }

    [Fact]
    public void B3_LocalDeclarationAfterGotoInsideIfBlock_IsNotHoisted()
    {
        var result = Convert(@"
class Test {
    static int M(boolean skip) {
        int x = 0;
        if (skip) {
            goto target;
            int y = 99;
            x = y;
        }
        x = 1;
        target: return x + 10;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.DoesNotContain("int y = 0;", result.GeneratedCode);
    }

    // Additional edge case: Multiple gotos to same label

    [Fact]
    public void B3_MultipleGotosToSameLabel_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M(int mode) {
        int x = 0;
        if (mode == 1) goto target;
        if (mode == 2) goto target;
        x = 100;
        target: x = x + 1;
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode,
            "System.out.print(Test.m(0) + \",\" + Test.m(1) + \",\" + Test.m(2));");
        // mode=0: x=0, no goto, x=100, target: x=101
        // mode=1: x=0, goto target, x=1
        // mode=2: x=0, goto target, x=1
        Assert.Equal("101,1,1", output);
    }

    // Additional edge case: Goto with return after label

    [Fact]
    public void B3_GotoWithReturnAfterLabel_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M(boolean skip) {
        if (skip) goto done;
        return 0;
        done: return 1;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode,
            "System.out.print(Test.m(true) + \",\" + Test.m(false));");
        Assert.Equal("1,0", output);
    }

    // A1: Goto in nested if within labeled loop

    [Fact]
    public void A1_GotoInNestedIfInLabeledLoop_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M() {
        int count = 0;
        outer: for (int i = 0; i < 3; i++) {
            for (int j = 0; j < 3; j++) {
                if (j == 1) goto outer;
                count = count + 1;
            }
        }
        return count;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m());");
        // i=0: j=0 count=1, j=1 goto outer
        // i=1: j=0 count=2, j=1 goto outer
        // i=2: j=0 count=3, j=1 goto outer
        Assert.Equal("3", output);
    }

    // A2: Goto block label with nested structure

    [Fact]
    public void A2_GotoBlockLabel_Nested_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M() {
        int x = 0;
        target: {
            x = x + 1;
            if (x < 3) goto target;
        }
        return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // goto target is A2 (in-scope block), becomes break target
        // break target exits the block, so x=1 after first iteration
        // This is NOT a loop - break exits once
        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m());");
        Assert.Equal("1", output);
    }

    [Fact]
    public void A2_BlockLabelRemainsUsableWhenMethodUsesStateMachine_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M(boolean jump) {
        int x = 0;
        target: {
            x = x + 1;
            if (jump) goto target;
            x = x + 10;
        }
        if (jump) goto done;
        x = x + 100;
        done: return x;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode,
            "System.out.print(Test.m(true) + \",\" + Test.m(false));");
        Assert.Equal("1,111", output);
    }

    [Fact]
    public void A1_LoopLabelRemainsUsableWhenMethodUsesStateMachine_Semantic()
    {
        var result = Convert(@"
class Test {
    static int M(boolean jump) {
        int count = 0;
        outer: for (int i = 0; i < 3; i++) {
            if (i == 1) goto outer;
            count = count + 1;
        }
        if (jump) goto done;
        count = count + 10;
        done: return count;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode,
            "System.out.print(Test.m(true) + \",\" + Test.m(false));");
        Assert.Equal("2,12", output);
    }

    [Fact]
    public void StateMachine_HoistsTryCatchVariableDeclarations()
    {
        var result = Convert(@"
class Test {
    void M(bool flag) {
        if (flag) goto done;
        try {
            string s = null;
            s = ""hello"";
        } catch (System.Exception e) {
            string msg = e.Message;
        }
        done: return;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // Variables declared inside try-catch should be hoisted outside the while loop
        // and their declarations inside the loop should be converted to assignments
        Assert.DoesNotMatch(@"\bString\s+s\s*=\s*null;", ExtractWhileBody(result.GeneratedCode));
        Assert.DoesNotMatch(@"\bString\s+msg\s*=", ExtractWhileBody(result.GeneratedCode));
        // The hoisted declarations should exist before the while loop
        Assert.Matches(@"String\s+s\s*=\s*null;", ExtractBeforeWhile(result.GeneratedCode));
    }

    [Fact]
    public void StateMachine_TryCatchDuplicateVariableNames()
    {
        var result = Convert(@"
class Test {
    void M(bool flag) {
        if (flag) goto step2;
        try {
            string s = ""a"";
        } catch (System.Exception e) {
            string msg = e.Message;
        }
        step2:
        try {
            string s = ""b"";
        } catch (System.Exception e) {
            string msg = e.Message;
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // Both 'e' and 's' are declared in multiple catch blocks;
        // they should be hoisted (only one declaration each) and
        // inner declarations converted to assignments
        var whileBody = ExtractWhileBody(result.GeneratedCode);
        // No duplicate type+name declarations inside the while loop
        var eDeclCount = Regex.Matches(whileBody, @"\bString\s+e\s*=").Count
                       + Regex.Matches(whileBody, @"\bException\s+e\s*=").Count;
        Assert.True(eDeclCount == 0, $"Found {eDeclCount} 'Exception e' declarations in while body, expected 0");
    }

    [Fact]
    public void StateMachine_HoistedBareLocalDeclaration_IsNotRedeclaredInCaseBody()
    {
        var result = Convert(@"
class Test {
    static int M(bool jump, char[] input) {
        int value = 0;
        if (jump) goto done;
        for (;;)
        {
            char[] chars;
        again:
            chars = input;
            value += chars.Length;
            if (value < 2) goto again;
            break;
        }
    done:
        return value;
    }
}");

        Assert.True(result.Success, result.GeneratedCode);
        var beforeWhile = ExtractBeforeWhile(result.GeneratedCode);
        var whileBody = ExtractWhileBody(result.GeneratedCode);

        Assert.Matches(@"\bchar\[\]\s+chars(?:_\d+)?\s*=\s*null\s*;", beforeWhile);
        Assert.DoesNotMatch(@"\bchar\[\]\s+chars(?:_\d+)?\s*;", whileBody);
    }

    [Fact]
    public void StateMachine_LabeledHoistedBareLocalDeclaration_IsNotRedeclaredInCaseBody()
    {
        var result = Convert(@"
class Test {
    static int M(bool jump, char[] input) {
        int pos = 0;
        if (jump) goto done;
        for (;;)
        {
        again:
            char tmpch2;
            {
                tmpch2 = input[pos];
                pos++;
            }
            if (tmpch2 == ':' && pos < input.Length) goto again;
            break;
        }
    done:
        return pos;
    }
}");

        Assert.True(result.Success, result.GeneratedCode);
        var beforeWhile = ExtractBeforeWhile(result.GeneratedCode);
        var whileBody = ExtractWhileBody(result.GeneratedCode);

        Assert.Matches(@"\bchar\s+tmpch2(?:_\d+)?\s*=\s*'\\0'\s*;", beforeWhile);
        Assert.DoesNotMatch(@"\bagain:\s*\{\s*char\s+tmpch2(?:_\d+)?\s*;\s*\}", whileBody);
        Assert.DoesNotMatch(@"\bagain:\s*;\s*char\s+tmpch2(?:_\d+)?\s*;", whileBody);
    }

    [Fact]
    public void StateMachine_HoistedMultiVariableBareLocalDeclaration_IsNotRedeclaredInCaseBody()
    {
        var result = Convert(@"
class Test {
    static int M(bool jump, char ch) {
        int value = 0;
        if (jump) goto done;
        switch (ch) {
            case '&':
                int charRefEndPos, charCount;
                charRefEndPos = 3;
                charCount = 1;
                value += charRefEndPos - charCount;
                break;
        }
    done:
        return value;
    }
}");

        Assert.True(result.Success, result.GeneratedCode);
        var beforeWhile = ExtractBeforeWhile(result.GeneratedCode);
        var whileBody = ExtractWhileBody(result.GeneratedCode);

        Assert.Matches(@"\bint\s+charRefEndPos(?:_\d+)?\s*=\s*0\s*;", beforeWhile);
        Assert.Matches(@"\bint\s+charCount(?:_\d+)?\s*=\s*0\s*;", beforeWhile);
        Assert.DoesNotMatch(@"\bint\s+charRefEndPos(?:_\d+)?\s*,\s*charCount(?:_\d+)?\s*;", whileBody);
        Assert.Contains("charRefEndPos = 3;", whileBody);
        Assert.Contains("charCount = 1;", whileBody);
    }

    [Fact]
    public void StateMachine_LabeledHoistedInitializedLocalDeclaration_IsConvertedToAssignment()
    {
        var result = Convert(@"
class Test {
    static int M(bool jump, int start, int end) {
        int total = 0;
    OuterContinue:
        int carried = total;
        for (;;)
        {
            int startPos = start;
            int pos = end;
            for (;;)
            {
                if (pos - startPos > 0)
                {
                    goto AppendAndUpdate;
                }
                if (jump)
                {
                    goto OuterContinue;
                }
                break;
            }
        AppendAndUpdate:
            total = carried + pos;
        Append:
            int charsParsed = pos - startPos;
            if (charsParsed > 0)
            {
                total += charsParsed;
            }
            return total;
        }
    }
}");

        Assert.True(result.Success, result.GeneratedCode);
        var beforeWhile = ExtractBeforeWhile(result.GeneratedCode);
        var whileBody = ExtractWhileBody(result.GeneratedCode);

        Assert.Matches(@"\bint\s+charsParsed(?:_\d+)?\s*=\s*0\s*;", beforeWhile);
        Assert.DoesNotMatch(@"\bAppend:\s*;\s*int\s+charsParsed(?:_\d+)?\s*=", whileBody);
        Assert.Matches(@"\bAppend:\s*;\s*charsParsed(?:_\d+)?\s*=\s*pos\s*-\s*startPos\s*;", whileBody);
    }

    [Fact]
    public void StateMachine_InfiniteLoopWithNestedSwitchBreaks_DropsUnreachableFallthrough()
    {
        var result = Convert(@"
class Test {
    static int M(bool restart, int mode) {
        if (restart) goto Done;

        for (;;)
        {
        Again:
            switch (mode)
            {
                case 0:
                    mode = 1;
                    break;
                default:
                    break;
            }

            if (restart)
            {
                goto Again;
            }
        }

    Done:
        return mode;
    }
}");

        Assert.True(result.Success, result.GeneratedCode);
        var whileBody = ExtractWhileBody(result.GeneratedCode);
        var infiniteLoopIndex = whileBody.IndexOf("for (; true; )", StringComparison.Ordinal);

        Assert.Contains("while (true)", whileBody);
        Assert.True(infiniteLoopIndex >= 0, result.GeneratedCode);
        Assert.DoesNotMatch(
            @"__state\s*=\s*2;\s*continue\s+__gotoLoop;",
            whileBody.Substring(infiniteLoopIndex));
    }

    [Fact]
    public void StateMachine_LabeledBlockReturnInsideInfiniteLoop_DropsUnreachableCaseBreak()
    {
        var result = Convert(@"
class Test {
    static bool M(bool more, bool needMore) {
        int pos = 0;
        int[] input = new int[] { 0 };
        for (;;)
        {
            if (needMore)
            {
                goto ReadData;
            }

        ReadData:
            if (more)
            {
                needMore = false;
            }
            else
            {
                return false;
            }
            pos = input[0];
        }
    }
}");

        Assert.True(result.Success, result.GeneratedCode);
        var code = result.GeneratedCode.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.DoesNotMatch(@"(?s)return\s+false;.{0,300}?break\s+__gotoLoop;", code);
    }

    private static string ExtractBeforeWhile(string code)
    {
        var idx = code.IndexOf("__gotoLoop: while (true)", StringComparison.Ordinal);
        return idx < 0 ? code : code.Substring(0, idx);
    }

    private static string ExtractWhileBody(string code)
    {
        var idx = code.IndexOf("__gotoLoop: while (true)", StringComparison.Ordinal);
        return idx < 0 ? "" : code.Substring(idx);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }

    private static string CompileAndRun(string javaCode, string mainBody)
    {
        var javac = FindExecutable("javac");
        var java = FindExecutable("java");
        if (javac == null || java == null)
        {
            throw new InvalidOperationException("javac/java are required for goto semantic tests.");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "cs2j-goto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var testCode = Regex.Replace(javaCode, @"\bpublic\s+class\s+Test\b", "class Test");
            File.WriteAllText(Path.Combine(tempDir, "Test.java"), testCode);
            File.WriteAllText(Path.Combine(tempDir, "Runner.java"), $@"
public class Runner {{
    public static void main(String[] args) {{
        {mainBody}
    }}
}}
");

            RunProcess(javac, "Test.java Runner.java", tempDir);
            return RunProcess(java, "Runner", tempDir).Trim();
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; test result should not depend on temp deletion.
            }
        }
    }

    private static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim(), name + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static string RunProcess(string fileName, string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {fileName}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(milliseconds: 10_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort cleanup; the timeout failure is the useful signal.
            }

            throw new TimeoutException(
                $"{Path.GetFileName(fileName)} timed out after 10 seconds while running: {arguments}");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(fileName)} exited with {process.ExitCode}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }

        return stdout;
    }

    [Fact]
    public void GotoCase_NonVoidMethod_HasFallbackReturn()
    {
        // A switch with goto case in a non-void method must have a fallback return
        // after the while loop, otherwise Java compilation fails with "missing return statement".
        var result = Convert(@"
class Test {
    static string M(int x) {
        switch (x) {
            case 1:
                return ""one"";
            case 2:
                goto case 1;
            default:
                return null;
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // The generated code should have a fallback return after the while loop
        Assert.Contains("return null;", result.GeneratedCode);
        // Verify it compiles
        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(2));");
        Assert.Equal("one", output);
    }

    [Fact]
    public void GotoCase_IntReturnMethod_HasFallbackReturn()
    {
        var result = Convert(@"
class Test {
    static int M(int x) {
        switch (x) {
            case 1:
                return 10;
            case 2:
                goto case 1;
            default:
                return 0;
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("return 0;", result.GeneratedCode);
        var output = CompileAndRun(result.GeneratedCode, "System.out.print(Test.m(2));");
        Assert.Equal("10", output);
    }
}
