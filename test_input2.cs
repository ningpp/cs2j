using System;

public class Program
{
    [Flags]
    enum Permissions
    {
        None = 0,
        Read = 1,
        Write = 2,
        Execute = 4
    }

    enum Status
    {
        Open = 10,
        Closed = 20
    }

    public static void Main(string[] args)
    {
        Permissions[] perms = new Permissions[3];
        Status[] statuses = new Status[3];
        System.Console.WriteLine(perms[1]);
        System.Console.WriteLine(statuses[1]);
    }
}
