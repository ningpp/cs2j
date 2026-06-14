using System;

class Program
{
    static void Main()
    {
        // === TimeSpan ===
        Console.WriteLine("=== TimeSpan ===");
        var ts1 = new TimeSpan(1, 2, 3);
        Console.WriteLine($"new TimeSpan(1,2,3).ToString() = {ts1}");
        Console.WriteLine($"ts1.Days = {ts1.Days}");
        Console.WriteLine($"ts1.Hours = {ts1.Hours}");
        Console.WriteLine($"ts1.Minutes = {ts1.Minutes}");
        Console.WriteLine($"ts1.Seconds = {ts1.Seconds}");
        Console.WriteLine($"ts1.Milliseconds = {ts1.Milliseconds}");
        Console.WriteLine($"ts1.TotalDays = {ts1.TotalDays}");
        Console.WriteLine($"ts1.TotalHours = {ts1.TotalHours}");
        Console.WriteLine($"ts1.TotalMinutes = {ts1.TotalMinutes}");
        Console.WriteLine($"ts1.TotalSeconds = {ts1.TotalSeconds}");
        Console.WriteLine($"ts1.TotalMilliseconds = {ts1.TotalMilliseconds}");
        Console.WriteLine($"ts1.Ticks = {ts1.Ticks}");

        var ts2 = TimeSpan.FromHours(1.5);
        Console.WriteLine($"TimeSpan.FromHours(1.5) = {ts2}");
        Console.WriteLine($"TimeSpan.FromMinutes(90) = {TimeSpan.FromMinutes(90)}");
        Console.WriteLine($"TimeSpan.FromSeconds(3661) = {TimeSpan.FromSeconds(3661)}");
        Console.WriteLine($"TimeSpan.FromMilliseconds(1500) = {TimeSpan.FromMilliseconds(1500)}");
        Console.WriteLine($"TimeSpan.FromTicks(10000000) = {TimeSpan.FromTicks(10000000)}");
        Console.WriteLine($"TimeSpan.Zero = {TimeSpan.Zero}");
        Console.WriteLine($"ts1.Add(ts2) = {ts1.Add(ts2)}");
        Console.WriteLine($"ts1.Subtract(ts2) = {ts1.Subtract(ts2)}");
        Console.WriteLine($"ts1.Negate() = {ts1.Negate()}");
        Console.WriteLine($"ts1.Duration() = {ts1.Duration()}");
        Console.WriteLine($"(-ts1) = {-ts1}");
        Console.WriteLine($"ts1.CompareTo(ts2) = {ts1.CompareTo(ts2)}");

        // Parse
        Console.WriteLine($"TimeSpan.Parse(\"1:02:03\") = {TimeSpan.Parse("1:02:03")}");
        Console.WriteLine($"TimeSpan.Parse(\"-1:02:03\") = {TimeSpan.Parse("-1:02:03")}");
        Console.WriteLine($"TimeSpan.Parse(\"1.02:03:04.0050000\") = {TimeSpan.Parse("1.02:03:04.0050000")}");

        // === DateTime ===
        Console.WriteLine();
        Console.WriteLine("=== DateTime ===");
        var dt1 = new DateTime(2024, 6, 15, 10, 30, 45);
        Console.WriteLine($"new DateTime(2024,6,15,10,30,45).ToString() = {dt1}");
        Console.WriteLine($"dt1.Year = {dt1.Year}");
        Console.WriteLine($"dt1.Month = {dt1.Month}");
        Console.WriteLine($"dt1.Day = {dt1.Day}");
        Console.WriteLine($"dt1.Hour = {dt1.Hour}");
        Console.WriteLine($"dt1.Minute = {dt1.Minute}");
        Console.WriteLine($"dt1.Second = {dt1.Second}");
        Console.WriteLine($"dt1.Millisecond = {dt1.Millisecond}");
        Console.WriteLine($"dt1.DayOfWeek = {dt1.DayOfWeek}");
        Console.WriteLine($"dt1.DayOfYear = {dt1.DayOfYear}");
        Console.WriteLine($"dt1.Ticks = {dt1.Ticks}");
        Console.WriteLine($"dt1.Kind = {dt1.Kind}");
        Console.WriteLine($"dt1.Date = {dt1.Date}");
        Console.WriteLine($"dt1.TimeOfDay = {dt1.TimeOfDay}");

        var dt2 = dt1.AddDays(10);
        Console.WriteLine($"dt1.AddDays(10) = {dt2}");
        Console.WriteLine($"dt1.AddHours(2) = {dt1.AddHours(2)}");
        Console.WriteLine($"dt1.AddMinutes(30) = {dt1.AddMinutes(30)}");
        Console.WriteLine($"dt1.AddSeconds(60) = {dt1.AddSeconds(60)}");
        Console.WriteLine($"dt1.AddMonths(3) = {dt1.AddMonths(3)}");
        Console.WriteLine($"dt1.AddYears(1) = {dt1.AddYears(1)}");
        Console.WriteLine($"dt1.Add(ts1) = {dt1.Add(ts1)}");

        Console.WriteLine($"DateTime.DaysInMonth(2024, 2) = {DateTime.DaysInMonth(2024, 2)}");
        Console.WriteLine($"DateTime.DaysInMonth(2023, 2) = {DateTime.DaysInMonth(2023, 2)}");
        Console.WriteLine($"DateTime.IsLeapYear(2024) = {DateTime.IsLeapYear(2024)}");
        Console.WriteLine($"DateTime.IsLeapYear(2023) = {DateTime.IsLeapYear(2023)}");

        Console.WriteLine($"DateTime.Parse(\"2024-06-15\") = {DateTime.Parse("2024-06-15")}");
        Console.WriteLine($"DateTime.Parse(\"2024-06-15T10:30:45\") = {DateTime.Parse("2024-06-15T10:30:45")}");

        // === DateTimeOffset ===
        Console.WriteLine();
        Console.WriteLine("=== DateTimeOffset ===");
        var dto1 = new DateTimeOffset(2024, 6, 15, 10, 30, 45, TimeSpan.FromHours(8));
        Console.WriteLine($"new DateTimeOffset(2024,6,15,10,30,45,+08:00).ToString() = {dto1}");
        Console.WriteLine($"dto1.DateTime = {dto1.DateTime}");
        Console.WriteLine($"dto1.LocalDateTime = {dto1.LocalDateTime}");
        Console.WriteLine($"dto1.UtcDateTime = {dto1.UtcDateTime}");
        Console.WriteLine($"dto1.Offset = {dto1.Offset}");
        Console.WriteLine($"dto1.Year = {dto1.Year}");
        Console.WriteLine($"dto1.Month = {dto1.Month}");
        Console.WriteLine($"dto1.Day = {dto1.Day}");
        Console.WriteLine($"dto1.Hour = {dto1.Hour}");
        Console.WriteLine($"dto1.Minute = {dto1.Minute}");
        Console.WriteLine($"dto1.Second = {dto1.Second}");
        Console.WriteLine($"dto1.DayOfWeek = {dto1.DayOfWeek}");
        Console.WriteLine($"dto1.DayOfYear = {dto1.DayOfYear}");

        // === DateOnly ===
        Console.WriteLine();
        Console.WriteLine("=== DateOnly ===");
        var do1 = new DateOnly(2024, 6, 15);
        Console.WriteLine($"new DateOnly(2024,6,15).ToString() = {do1}");
        Console.WriteLine($"do1.Year = {do1.Year}");
        Console.WriteLine($"do1.Month = {do1.Month}");
        Console.WriteLine($"do1.Day = {do1.Day}");
        Console.WriteLine($"do1.DayOfWeek = {do1.DayOfWeek}");
        Console.WriteLine($"do1.DayOfYear = {do1.DayOfYear}");
        Console.WriteLine($"do1.DayNumber = {do1.DayNumber}");
        Console.WriteLine($"do1.AddDays(10) = {do1.AddDays(10)}");
        Console.WriteLine($"do1.AddMonths(3) = {do1.AddMonths(3)}");
        Console.WriteLine($"do1.AddYears(1) = {do1.AddYears(1)}");
        Console.WriteLine($"DateOnly.FromDateTime(dt1) = {DateOnly.FromDateTime(dt1)}");
        Console.WriteLine($"DateOnly.Parse(\"2024-06-15\") = {DateOnly.Parse("2024-06-15")}");

        // === TimeOnly ===
        Console.WriteLine();
        Console.WriteLine("=== TimeOnly ===");
        var to1 = new TimeOnly(10, 30, 45);
        Console.WriteLine($"new TimeOnly(10,30,45).ToString() = {to1}");
        Console.WriteLine($"to1.Hour = {to1.Hour}");
        Console.WriteLine($"to1.Minute = {to1.Minute}");
        Console.WriteLine($"to1.Second = {to1.Second}");
        Console.WriteLine($"to1.Millisecond = {to1.Millisecond}");
        Console.WriteLine($"to1.Ticks = {to1.Ticks}");
        Console.WriteLine($"to1.AddHours(2) = {to1.AddHours(2)}");
        Console.WriteLine($"to1.AddMinutes(30) = {to1.AddMinutes(30)}");
        Console.WriteLine($"to1.Add(TimeSpan.FromHours(1)) = {to1.Add(TimeSpan.FromHours(1))}");
        Console.WriteLine($"TimeOnly.FromDateTime(dt1) = {TimeOnly.FromDateTime(dt1)}");
        Console.WriteLine($"TimeOnly.Parse(\"10:30:45\") = {TimeOnly.Parse("10:30:45")}");
        Console.WriteLine($"to1.CompareTo(new TimeOnly(11,0,0)) = {to1.CompareTo(new TimeOnly(11, 0, 0))}");
    }
}
