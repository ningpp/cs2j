using System.Collections.Generic;
using System.Linq;
namespace DualTarget
{
    public class CollectionOps
    {
        public int SumEven(List<int> values)
        {
            int sum = 0;
            foreach (int value in values)
            {
                if (value % 2 == 0)
                {
                    sum += value;
                }
            }
            return sum;
        }

        public List<int> FilterAndScale(int[] values, int scale)
        {
            return values.Where(v => v > 0).Select(v => v * scale).ToList();
        }

        public int Lookup(Dictionary<string, int> map, string key)
        {
            if (map.TryGetValue(key, out int value))
            {
                return value;
            }
            return -1;
        }
    }
}
