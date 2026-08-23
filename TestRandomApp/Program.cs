using System;

class TestRandom {
    static void Main() {
        var r = new Random(1);
        for (int i = 0; i < 10; i++) {
            Console.WriteLine($"nextDouble[{i}] = {r.NextDouble()}");
        }
    }
}
