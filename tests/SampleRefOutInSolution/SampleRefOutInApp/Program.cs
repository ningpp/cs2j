using SampleRefOutInLib;

class Program
{
    static void Main()
    {
        int x = 1, y = 2;
        Calculator.Swap(ref x, ref y);

        Calculator.TryParse("1,2", out var p);

        var holder = new GenericHolder<int>();
        int val = 5;
        holder.Update(ref val);
    }
}
