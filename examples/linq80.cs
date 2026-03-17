using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

class ProgramLINQ
    {
        static void Main()
        {
            Example01(); Example02(); Example03(); Example04(); Example05();
            Example06(); Example07(); Example08(); Example09(); Example10();
            Example11(); Example12(); Example13(); Example14(); Example15();
            Example16(); Example17(); Example18(); Example19(); Example20();
            Example21(); Example22(); Example23(new List<int> { 1,2,3}); Example24(); Example25();
            Example26(); Example27(); Example28(); Example29(); Example30();
            Example31(); Example32(); Example33(); Example34(); Example35();
            Example36(); Example37(); Example38(); Example39(); Example40();
            Example41(); Example42(); Example43(); Example44(); Example45();
            Example46(); Example47(); Example48(); Example49(); Example50();
            Example51(); Example52(); Example53(); Example54(); Example55();
            Example56(); Example57(); Example58(); Example59(); Example60();
            Example61(); Example62(); Example63(); Example64(); Example65();
            Example66(); Example67(); Example68(); Example69(); Example70();
            Example71(); Example72(); Example73(); Example74(); Example75();
            Example76(); Example77(); Example78(); Example79(); Example80();
        }

        static List<int> nums = new() { 1, 2, 3, 4, 5, 6 };
        static List<string> strs = new() { "a", "bb", "ccc", "dd" };

        static void Example01() { var r = nums.Where(x => x > 3); }
        static void Example02() { var r = nums.Select(x => x * 2); }
        static void Example03() { var r = nums.Where(x => x % 2 == 0).Select(x => x * 10); }
        static void Example04() { var r = from x in nums where x > 2 select x; }
        static void Example05() { var r = nums.First(); }
        static void Example06() { var r = nums.FirstOrDefault(); }
        static void Example07() { var r = nums.Any(x => x > 5); }
        static void Example08() { var r = nums.All(x => x > 0); }
        static void Example09() { var r = nums.Count(x => x > 2); }
        static void Example10() { var r = nums.Distinct(); }

        static void Example11() { var r = nums.OrderBy(x => x); }
        static void Example12() { var r = nums.OrderByDescending(x => x); }
        static void Example13() { var r = strs.OrderBy(x => x.Length).ThenBy(x => x); }
        static void Example14() { var r = nums.Skip(2).Take(3); }
        static void Example15() { var r = nums.Contains(3); }
        static void Example16() { var r = nums.Sum(); }
        static void Example17() { var r = nums.Average(); }
        static void Example18() { var r = nums.Max(); }
        static void Example19() { var r = nums.Min(); }
        static void Example20() { var r = nums.Aggregate((a, b) => a + b); }

        static void Example21() { var r = strs.GroupBy(x => x.Length); }
        static void Example22() { var r = strs.ToDictionary(x => x, x => x.Length); }
        static void Example23(IList<int> nums) { var r = nums.Reverse(); }
        static void Example24() { var r = nums.Concat(new[] { 7, 8 }); }
        static void Example25() { var r = nums.Union(new[] { 3, 7 }); }
        static void Example26() { var r = nums.Intersect(new[] { 2, 3, 9 }); }
        static void Example27() { var r = nums.Except(new[] { 1, 2 }); }
        static void Example28() { var r = nums.SelectMany(x => new[] { x, x * 10 }); }
        static void Example29()
        {
            var a = new[] { new { Id = 1 }, new { Id = 2 } };
            var b = new[] { new { Id = 1, V = "A" } };
            var r = a.Join(b, x => x.Id, y => y.Id, (x, y) => new { x, y });
        }
        static void Example30()
        {
            var a = new[] { new { Id = 1 }, new { Id = 2 } };
            var b = new[] { new { Id = 1, V = "A" } };
            var r = a.GroupJoin(b, x => x.Id, y => y.Id, (x, g) => new { x, g });
        }

        static void Example31() { var r = nums.TakeWhile(x => x < 4); }
        static void Example32() { var r = nums.SkipWhile(x => x < 4); }
        static void Example33() { var r = nums.DefaultIfEmpty(); }
        static void Example34() { var r = nums.ElementAt(2); }
        static void Example35() { var r = nums.ElementAtOrDefault(100); }
        static void Example36() { var r = nums.Single(x => x == 3); }
        static void Example37() { var r = nums.SingleOrDefault(x => x == 100); }
        static void Example38() { var r = nums.Last(); }
        static void Example39() { var r = nums.LastOrDefault(); }
        static void Example40() { var r = nums.Append(7); }

        static void Example41() { var r = nums.Prepend(0); }
        static void Example42() { var r = nums.Zip(new[] { 10, 20, 30 }, (a, b) => a + b); }
        static void Example43() { var r = nums.Chunk(2); }
        static void Example44() { var r = strs.ToLookup(x => x.Length); }
        static void Example45()
        {
            var r = strs.GroupBy(x => x.Length)
                .Select(g => new { g.Key, Count = g.Count() });
        }

        static void Example46()
        {
            var r = from s in strs
                    let len = s.Length
                    where len > 1
                    select len;
        }

        static void Example47()
        {
            var r = from x in nums
                    join y in nums on x equals y into g
                    from y in g.DefaultIfEmpty()
                    select new { x, y };
        }

        static void Example48()
        {
            var r = nums.GroupBy(x => x % 2)
                .Select(g => g.OrderByDescending(x => x).First());
        }

        static void Example49()
        {
            var r = nums.Select((x, i) => new { x, i });
        }

        static void Example50()
        {
            var r = nums.Where((x, i) => i % 2 == 0);
        }

        static void Example51()
        {
            var query = nums.Where(x => x > 2);
            nums.Add(100);
            var r = query.ToList();
        }

        static void Example52()
        {
            IEnumerable<int> r = nums.Where(x => x > 2);
        }

        static void Example53()
        {
            IQueryable<int> r = nums.AsQueryable().Where(x => x > 2);
        }

        static void Example54()
        {
            Expression<Func<int, bool>> expr = x => x > 5;
        }

        static void Example55()
        {
            var r = nums.AsEnumerable().Where(x => x > 1);
        }

        static void Example56()
        {
            var r = nums.Cast<int>();
        }

        static void Example57()
        {
            var objs = new object[] { 1, "a", 2 };
            var r = objs.OfType<int>();
        }

        static void Example58()
        {
            var r = Enumerable.Range(1, 5);
        }

        static void Example59()
        {
            var r = Enumerable.Repeat(1, 3);
        }

        static void Example60()
        {
            var r = Enumerable.Empty<int>();
        }

        static void Example61()
        {
            var r = nums.SequenceEqual(new[] { 1, 2, 3, 4, 5, 6 });
        }

        static void Example62()
        {
            var r = nums.TakeLast(2);
        }

        static void Example63()
        {
            var r = nums.SkipLast(2);
        }

        static void Example64()
        {
            var r = nums.MaxBy(x => x);
        }

        static void Example65()
        {
            var r = nums.MinBy(x => x);
        }

        static void Example66()
        {
            var r = strs.DistinctBy(x => x.Length);
        }

        static void Example67()
        {
            var r = strs.UnionBy(new[] { "dddd" }, x => x.Length);
        }

        static void Example68()
        {
            var r = strs.IntersectBy(new[] { 2, 3 }, x => x.Length);
        }

        static void Example69()
        {
            var r = strs.ExceptBy(new[] { 1 }, x => x.Length);
        }

        static void Example70()
        {
            var r = nums.OrderBy(x => x).ThenByDescending(x => x);
        }

        static void Example71()
        {
            var r = nums.Aggregate(0, (acc, x) => acc + x);
        }

        static void Example72()
        {
            var r = nums.Aggregate(0, (acc, x) => acc + x, acc => acc * 2);
        }

        static void Example73()
        {
            var r = nums.SelectMany(x => Enumerable.Range(1, x));
        }

        static void Example74()
        {
            var r = nums.GroupBy(x => x % 2, (k, g) => new { k, Sum = g.Sum() });
        }

        static void Example75()
        {
            var r = strs.OrderBy(x => x).Reverse();
        }

        static void Example76()
        {
            var r = nums.Where(x => x > 2).DefaultIfEmpty(-1);
        }

        static void Example77()
        {
            var r = nums.Select(x => x.ToString()).ToList();
        }

        static void Example78()
        {
            var r = nums.Where(x => x > 100).Any();
        }

        static void Example79()
        {
            var r = nums.Where(x => x > 100).All(x => x > 0);
        }

        static void Example80()
        {
            var r = nums.Select(x => new { Value = x, Time = DateTime.Now });
        }
    }
