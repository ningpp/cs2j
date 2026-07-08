namespace SampleRefOutInLib
{
    public class Calculator
    {
        public static void Swap(ref int a, ref int b)
        {
            int t = a;
            a = b;
            b = t;
        }

        public static bool TryParse(string s, out Point p)
        {
            p = new Point(0, 0);
            return true;
        }
    }
}
