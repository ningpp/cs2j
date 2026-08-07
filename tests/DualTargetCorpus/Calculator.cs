namespace DualTarget
{
    public class Calculator
    {
        public const int MaxOperand = 1000;

        private readonly int _scale;

        public Calculator() : this(1)
        {
        }

        public Calculator(int scale)
        {
            _scale = scale;
        }

        public int Scale => _scale;

        public int Add(int a, int b)
        {
            return (a + b) * _scale;
        }

        public int Add(string a, string b)
        {
            return int.Parse(a) + int.Parse(b);
        }

        public static int Subtract(int a, int b)
        {
            return a - b;
        }

        public double Average(double[] values)
        {
            double sum = 0;
            for (int i = 0; i < values.Length; i++)
            {
                sum += values[i];
            }
            return values.Length == 0 ? 0 : sum / values.Length;
        }
    }
}
