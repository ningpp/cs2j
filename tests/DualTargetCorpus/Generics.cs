using System.Collections.Generic;
namespace DualTarget
{
    public class Box<T>
    {
        private T _value;

        public Box(T value)
        {
            _value = value;
        }

        public T GetValue()
        {
            return _value;
        }

        public void SetValue(T value)
        {
            _value = value;
        }
    }

    public class GenericUser
    {
        public string Describe<T>(Box<T> box)
        {
            return "box:" + box.GetValue();
        }

        public List<int> Numbers()
        {
            return new List<int> { 1, 2, 3 };
        }
    }
}
