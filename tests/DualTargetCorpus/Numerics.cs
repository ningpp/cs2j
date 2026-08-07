namespace DualTarget
{
    public class NumericOps
    {
        public int Div(int a, int b)
        {
            return a / b;
        }

        public int? NullableAdd(int? a, int? b)
        {
            if (a == null || b == null)
            {
                return null;
            }
            return a + b;
        }

        public long BigSum(long a, long b)
        {
            return a + b;
        }

        public bool IsPositive(double value)
        {
            return value > 0;
        }
    }
}
