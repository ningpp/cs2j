namespace CSharpToJava.Core.RefOutInPreprocessor;

public sealed class RefOutInRewriterStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int RefParamsRewritten;
    public int OutParamsRewritten;
    public int InByValueParams;
    public int ScopedRemoved;
    public int DelegatesRewritten;
    public int RefReturnsRewritten;
    public int RefLocalsRewritten;
    public int RefFieldsRewritten;
    public int ArrayElementProxies;
    public int RefStructParamsRewritten;
    public int CallSitesLifted;
    public int HoldersInjected;
    public int AccessorProxies;
    public int LockedContractMembers;
    public int InOverloadKept;
    public int ExternalRefArgumentsKept;
}
