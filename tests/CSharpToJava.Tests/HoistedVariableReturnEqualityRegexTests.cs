using System.Text.RegularExpressions;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that ConvertHoistedDeclarationsToAssignments does not strip the return keyword
/// when a hoisted variable appears in a return statement using == (equality) operator.
/// </summary>
public class HoistedVariableReturnEqualityRegexTests
{
    [Fact]
    public void Regex_DoesNotMatch_ReturnStatementWithEquality()
    {
        // This is the OLD regex pattern that had the bug
        var varName = "c";
        var pattern = $@"((?<=^\s*)[\w.]+(?:<[^>]+>)?(?:\[\])*)\s+\b{Regex.Escape(varName)}\b\s*=";

        var code = "            return c == '<';";
        var match = Regex.Match(code, pattern, RegexOptions.Multiline);

        Assert.True(match.Success);
        Assert.Equal("return c =", match.Value);
        // The replacement would be: c = （stripping "return "）
        var replaced = Regex.Replace(code, pattern, $"{varName} =", RegexOptions.Multiline);
        Assert.Equal("            c == '<';", replaced);
    }

    [Fact]
    public void Regex_Fixed_DoesNotMatch_ReturnStatementWithEquality()
    {
        // This is the FIXED regex pattern with (?!=)
        var varName = "c";
        var pattern = $@"((?<=^\s*)[\w.]+(?:<[^>]+>)?(?:\[\])*)\s+\b{Regex.Escape(varName)}\b\s*=(?!=)";

        var code = "            return c == '<';";
        var match = Regex.Match(code, pattern, RegexOptions.Multiline);

        Assert.False(match.Success,
            "The regex should NOT match 'return c ==' because the = is followed by = (equality operator)");
    }

    [Fact]
    public void Regex_Fixed_StillMatches_RealAssignment()
    {
        var varName = "c";
        var pattern = $@"((?<=^\s*)[\w.]+(?:<[^>]+>)?(?:\[\])*)\s+\b{Regex.Escape(varName)}\b\s*=(?!=)";

        // Real assignment: char c = '\0';
        var code = "        char c = '\0';";
        var match = Regex.Match(code, pattern, RegexOptions.Multiline);

        Assert.True(match.Success,
            "The regex should still match real declaration-assignments like 'char c = ...'");
        Assert.Equal("char c =", match.Value);

        var replaced = Regex.Replace(code, pattern, $"{varName} =", RegexOptions.Multiline);
        Assert.Equal("        c = '\0';", replaced);
    }
}
