namespace SamplePartialLib;

/// <summary>Order service primary part.</summary>
public partial class OrderService
{
    private readonly int _region;

    public int Region => _region;

    partial void OnCreated();

    public void Create()
    {
        OnCreated();
    }
}
