namespace DualTarget
{
    public class StringHelper
    {
        public string Greet(string name)
        {
            return "Hello, " + name + "!";
        }

        public string Interpolate(string name, int count)
        {
            return $"user={name} count={count}";
        }

        public bool ContainsIgnoreCase(string text, string part)
        {
            return text.ToLower().Contains(part.ToLower());
        }

        public string JoinWithComma(string[] parts)
        {
            return string.Join(",", parts);
        }
    }
}
