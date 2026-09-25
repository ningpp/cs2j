namespace CSharpToJava.Core.ArrayMethodRewriter;

public sealed record ArrayMethodRewriterOptions(
    bool Verbose = false,
    bool Strict = false,
    bool EnableInstanceRewrite = true,
    bool EnableStaticRewrite = true,
    bool EnableForeachFlatten = true);
