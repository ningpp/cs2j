namespace SampleAttributeNormalizeLib;

public sealed class CallSiteSamples
{
    private readonly ICallerLog _log;

    public CallSiteSamples(ICallerLog log)
    {
        _log = log;
        _log.Notify("ctor site");
    }

    public string Property
    {
        get
        {
            _log.Notify("property getter");
            return string.Empty;
        }
    }

    public string this[int index]
    {
        get
        {
            _log.Notify("indexer getter");
            return string.Empty;
        }
    }

    public void LambdaAndLocalFunction()
    {
        Action lambda = () => _log.Notify("lambda site");
        lambda();

        void Local() => _log.Notify("local function site");
        Local();
    }
}
