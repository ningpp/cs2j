using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.RecordReadOnly;

internal sealed class MethodMigrator : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;
    public MethodMigrator(RewriteContext context) => _context = context;
}
