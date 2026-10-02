namespace SamplePartialLib;

/// <summary>Repository contract primary part.</summary>
public partial interface IRepository
{
    Customer? Find(int id);
}
