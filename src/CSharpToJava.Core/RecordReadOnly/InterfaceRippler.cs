using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.RecordReadOnly;

internal sealed class InterfaceRippler : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;
    public InterfaceRippler(RewriteContext context) => _context = context;
}
