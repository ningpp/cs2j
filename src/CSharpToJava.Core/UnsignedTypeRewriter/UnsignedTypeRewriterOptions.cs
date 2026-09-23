namespace CSharpToJava.Core.UnsignedTypeRewriter;

public sealed record UnsignedTypeRewriterOptions(
    bool Verbose = false,
    bool Strict = false,
    bool EnableOperatorRewrite = true,
    bool EnableCastRewrite = true,
    bool EnableLiteralRewrite = true);
