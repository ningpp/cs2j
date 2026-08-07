using System.Collections.Generic;
using Xunit;
using DualTarget;

namespace DualTarget.Tests
{
    public class FeatureTests
    {
        [Fact]
        public void Enum_Describe_And_Flags()
        {
            var user = new EnumUser();
            Assert.Equal("red", user.Describe(Color.Red));
            var perms = Permissions.Read | Permissions.Write;
            Assert.True(user.CanWrite(perms));
            Assert.False(user.CanWrite(Permissions.Read));
        }

        [Fact]
        public void String_Helper_Works()
        {
            var helper = new StringHelper();
            Assert.Equal("Hello, Ada!", helper.Greet("Ada"));
            Assert.Equal("user=Ada count=3", helper.Interpolate("Ada", 3));
            Assert.True(helper.ContainsIgnoreCase("Hello World", "world"));
            Assert.Equal("a,b,c", helper.JoinWithComma(new[] { "a", "b", "c" }));
        }

        [Fact]
        public void Exceptions_Are_Caught()
        {
            var flow = new ExceptionFlow();
            Assert.Equal(-1, flow.Guard(-5));
            Assert.Equal(-2, flow.Guard(0));
            Assert.Equal(25, flow.Guard(4));
        }

        [Fact]
        public void Numerics_And_Collections()
        {
            var numerics = new NumericOps();
            Assert.Equal(3, numerics.Div(7, 2));
            Assert.Null(numerics.NullableAdd(null, 1));
            Assert.Equal(5, numerics.NullableAdd(2, 3));
            Assert.Equal(9L, numerics.BigSum(4, 5));

            var ops = new CollectionOps();
            Assert.Equal(6, ops.SumEven(new List<int> { 1, 2, 3, 4 }));
            Assert.Equal(new List<int> { 2, 4 }, ops.FilterAndScale(new[] { 1, -2, 2 }, 2));
            Assert.Equal(42, ops.Lookup(new Dictionary<string, int> { ["answer"] = 42 }, "answer"));
            Assert.Equal(-1, ops.Lookup(new Dictionary<string, int>(), "missing"));
        }

        [Fact]
        public void Generics_Work()
        {
            var box = new Box<int>(7);
            Assert.Equal(7, box.GetValue());
            box.SetValue(8);
            Assert.Equal(8, box.GetValue());
            var user = new GenericUser();
            Assert.Equal("box:8", user.Describe(box));
            Assert.Equal(3, user.Numbers().Count);
        }
    }
}
