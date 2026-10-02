using System.ComponentModel.DataAnnotations;

namespace SamplePartialLib;

/// <summary>Order service second part.</summary>
public partial class OrderService
{
    [Required]
    private string? _name;

    public string? Name => _name;

    partial void OnCreated()
    {
    }
}
