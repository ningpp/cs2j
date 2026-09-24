using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.RecordReadOnly;

internal sealed class MutationSiteRewriter : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;
    public MutationSiteRewriter(RewriteContext context) => _context = context;
}
