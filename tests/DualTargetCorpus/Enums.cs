namespace DualTarget
{
    public enum Color
    {
        Red,
        Green,
        Blue,
    }

    [System.Flags]
    public enum Permissions
    {
        None = 0,
        Read = 1,
        Write = 2,
        Execute = 4,
    }

    public class EnumUser
    {
        public string Describe(Color color)
        {
            switch (color)
            {
                case Color.Red:
                    return "red";
                case Color.Green:
                    return "green";
                default:
                    return "blue";
            }
        }

        public bool CanWrite(Permissions permissions)
        {
            return (permissions & Permissions.Write) == Permissions.Write;
        }
    }
}
