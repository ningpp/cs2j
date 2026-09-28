using SampleGenericDeepenLib;

var latest = new Latest<string>().Remember("a");
if (Latest<string>.Value != latest) return 1;
if (Node<string>.Empty.Next is not null) return 2;
if (Echo<string>.Identity("x") != "x") return 3;
if (new TypeTokenCache<string>().Describe("a") != "typed:a") return 4;
if (Guard.DescribeError<InvalidOperationException>(() => throw new InvalidOperationException("b")) != "caught:b") return 5;
if (new HashingProcessor<int>().Key(1) != typeof(int).GetHashCode().ToString()) return 6;
if (Outer<int>.Inner.Describe() != "Int32") return 7;
return 0;
