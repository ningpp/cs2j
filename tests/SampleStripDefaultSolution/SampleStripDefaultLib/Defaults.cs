namespace SampleStripDefaultLib;

public class Service
{
    public void Process(int a, string b = "default", bool c = true)
    {
    }

    public int Calculate(int x, int y = 10, int z = 20)
    {
        return x + y + z;
    }

    public Service(string name = "unnamed")
    {
    }

    public static string Greet(string name = "world")
    {
        return $"Hello, {name}";
    }
}
