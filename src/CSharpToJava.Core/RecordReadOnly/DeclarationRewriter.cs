using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.RecordReadOnly;

internal sealed class DeclarationRewriter : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;
    public DeclarationRewriter(RewriteContext context) => _context = context;
}
