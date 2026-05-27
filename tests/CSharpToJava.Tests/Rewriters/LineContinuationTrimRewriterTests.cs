using CSharpToJava.Core.Java;
using CSharpToJava.Core.Java.Rewriters;

namespace CSharpToJava.Tests.Rewriters;

public class LineContinuationTrimRewriterTests
{
    private static JavaCompilationUnit WrapMethod(string methodName, string body)
    {
        var method = new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.None,
            ReturnType = "void",
            Name = methodName,
            Body = body,
        };
        var classDecl = new JavaClassDeclaration { Name = "Scanner" };
        classDecl.Methods.Add(method);
        var cu = new JavaCompilationUnit();
        cu.TypeDeclarations.Add(classDecl);
        return cu;
    }

    private static JavaCompilationUnit BuildScannerWithBothMethods(
        string trimBody, string scanBody)
    {
        var classDecl = new JavaClassDeclaration { Name = "Scanner" };

        classDecl.Methods.Add(new JavaMethodDeclaration
        {
            ReturnType = "void", Name = "trimString", Body = trimBody,
        });
        classDecl.Methods.Add(new JavaMethodDeclaration
        {
            ReturnType = "int", Name = "scan", Body = scanBody,
        });

        var cu = new JavaCompilationUnit();
        cu.TypeDeclarations.Add(classDecl);
        return cu;
    }

    [Fact]
    public void TrimString_AddsCrOnlyBranch_WhenMissing()
    {
        var body = @"
void trimString() {
    if (stringId.endsWith(""\\\r\n"")) {
        stringId = stringId.substring(0, stringId.length() - 3);
    } else {
        if (stringId.endsWith(""\\\n"")) {
            stringId = stringId.substring(0, stringId.length() - 2);
        }
    }
}";

        var cu = WrapMethod("trimString", body);
        var rewriter = new LineContinuationTrimRewriter();

        rewriter.VisitCompilationUnit(cu);

        Assert.Equal(1, rewriter.RewriteCount);
        Assert.Contains(@"endsWith(""\\\r"")", ((JavaClassDeclaration)cu.TypeDeclarations[0]).Methods[0].Body);
    }

    [Fact]
    public void ScanMethod_SeparatesCase35_WhenGrouped()
    {
        var body = @"
int scan() {
    switch (state) {
case 30, 31, 33, 35:
            stringId += getYytext();
            break;
    }
}";

        var cu = WrapMethod("scan", body);
        var rewriter = new LineContinuationTrimRewriter();

        rewriter.VisitCompilationUnit(cu);

        Assert.Equal(1, rewriter.RewriteCount);
        var result = ((JavaClassDeclaration)cu.TypeDeclarations[0]).Methods[0].Body;
        Assert.Contains("case 30, 31, 33:", result);
        Assert.Contains("case 35:", result);
        Assert.Contains("trimString();", result);
        Assert.DoesNotContain("case 30, 31, 33, 35:", result);
    }

    [Fact]
    public void TrimString_NoChange_WhenCrBranchAlreadyPresent()
    {
        var body = @"
void trimString() {
    if (stringId.endsWith(""\\\r\n"")) {
        stringId = stringId.substring(0, stringId.length() - 3);
    } else if (stringId.endsWith(""\\\n"")) {
        stringId = stringId.substring(0, stringId.length() - 2);
    } else if (stringId.endsWith(""\\\r"")) {
        stringId = stringId.substring(0, stringId.length() - 2);
    }
}";

        var cu = WrapMethod("trimString", body);
        var rewriter = new LineContinuationTrimRewriter();

        rewriter.VisitCompilationUnit(cu);

        Assert.Equal(0, rewriter.RewriteCount);
    }

    [Fact]
    public void UnrelatedMethod_NoChange()
    {
        var body = "System.out.println(\"hello\");";
        var cu = WrapMethod("otherMethod", body);
        var rewriter = new LineContinuationTrimRewriter();

        rewriter.VisitCompilationUnit(cu);

        Assert.Equal(0, rewriter.RewriteCount);
        Assert.Equal(body, ((JavaClassDeclaration)cu.TypeDeclarations[0]).Methods[0].Body);
    }

    [Fact]
    public void BothFixesApplied_WhenBothPatternsFound()
    {
        var trimBody = @"
void trimString() {
    if (stringId.endsWith(""\\\r\n"")) {
        stringId = stringId.substring(0, stringId.length() - 3);
    } else {
        if (stringId.endsWith(""\\\n"")) {
            stringId = stringId.substring(0, stringId.length() - 2);
        }
    }
}";
        var scanBody = @"
int scan() {
    switch (state) {
case 30, 31, 33, 35:
            stringId += getYytext();
            break;
    }
}";

        var cu = BuildScannerWithBothMethods(trimBody, scanBody);
        var rewriter = new LineContinuationTrimRewriter();

        rewriter.VisitCompilationUnit(cu);

        Assert.Equal(2, rewriter.RewriteCount);
    }
}
