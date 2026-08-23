using System;

class Program
{
    static void Main()
    {
        var r = new Random(1);
        Console.Write("C# Random(1).Next(2): ");
        for (int i = 0; i < 20; i++)
        {
            Console.Write(r.Next(2) + " ");
        }
        Console.WriteLine();
    }
}
