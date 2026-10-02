using SamplePartialLib;

var service = new OrderService();
service.Create();
Console.WriteLine($"SamplePartialApp: region={service.Region}");

var reader = new Utf8Reader(new byte[] { 1, 2, 3 });
Console.WriteLine($"SamplePartialApp: first={reader.First}, length={reader.Length}");

var property = new PartialProperty();
property.Set(42);
Console.WriteLine($"SamplePartialApp: value={property.Value}");
