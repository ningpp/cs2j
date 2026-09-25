namespace SampleRecordLib;

// IR-1/IR-2：in-tree 接口连带
public interface ICounter { int Count { get; set; } }
public record struct Counter : ICounter { public int Count { get; set; } }

// IR-5：接口继承
public interface IBase { int Value { get; set; } }
public interface IDerived : IBase { }
public record struct Holder : IDerived { public int Value { get; set; } }
