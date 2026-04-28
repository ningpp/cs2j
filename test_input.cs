public class Program
{
    enum VertStatus
    {
        NotVisited,
        InStack,
        Visited,
    }

    public static void Main(string[] args)
    {
        VertStatus[] status = new VertStatus[3];
        System.Console.WriteLine(status[1]);
    }
}
