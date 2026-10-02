using SamplePartialLib;

namespace SamplePartialTests;

/// <summary>
/// Behavior-equivalence gate: these tests must pass on both the original and the
/// merged intermediate trees (design §7.3).
/// </summary>
public sealed class BehaviorTests
{
    [Fact]
    public void OrderService_Create_Runs_Classic_Partial_Hook()
    {
        var service = new OrderService();
        service.Create();
        Assert.True(service.Region >= 0);
    }

    [Fact]
    public void Customer_Record_Keeps_Primary_Constructor_Parameters()
    {
        var customer = new Customer(1);
        Assert.Equal(1, customer.Id);
    }

    [Fact]
    public void Customer_Second_Part_Member_Survives()
    {
        var customer = new Customer(1) { Name = "x" };
        Assert.Equal("x", customer.Name);
    }

    [Fact]
    public void Utf8Reader_Merges_Methods_Across_Parts()
    {
        var reader = new Utf8Reader(new byte[] { 7, 8 });
        Assert.Equal(7, reader.First);
        Assert.Equal(2, reader.Length);
    }

    [Fact]
    public void IRepository_Contract_Unions_Members()
    {
        IRepository repository = new InMemoryRepository();
        Assert.NotNull(repository.Find(0));
        repository.Save(new Customer(1));
    }

    [Fact]
    public void ExtendedPartial_Method_Resolves()
    {
        Assert.Equal("extended", new ExtendedPartial().Name);
    }

    [Fact]
    public void PartialEvent_Add_Remove_And_Raise()
    {
        var sample = new PartialEvent();
        var raised = 0;
        sample.Changed += () => raised++;
        sample.Raise();
        sample.Changed -= () => raised++;
        Assert.Equal(1, raised);
    }

    [Fact]
    public void PartialProperty_Defining_And_Implementation()
    {
        var sample = new PartialProperty();
        Assert.Equal(0, sample.Value);
        sample.Set(9);
        Assert.Equal(9, sample.Value);
    }

    [Fact]
    public void PartialConstructor_Defining_And_Implementation()
    {
        Assert.Equal(0, new PartialConstructor().Id);
        Assert.Equal(5, new PartialConstructor(5).Id);
    }

    [Fact]
    public void Outer_Nested_Partial_Inner_Merges()
    {
        Outer.Inner inner = new();
        Assert.Equal(1, inner.A());
        Assert.Equal(2, inner.B());
    }

    private sealed class InMemoryRepository : IRepository
    {
        public Customer? Find(int id) => new(id);

        public void Save(Customer customer)
        {
        }
    }
}
