using SampleUnsafeFfmLib;

var message = new byte[] { 1, 2, 3, 4, 5 };

var sum = FrameHeader.Summarize(message, out var last);
Console.WriteLine($"sum={sum},last={last}");
Console.WriteLine($"first={FrameHeader.FirstPayloadByte()}");
Console.WriteLine($"viaHolder={FrameHeader.ViaHolder(new FrameHolder())}");

// M2 stackalloc 场景的冒烟调用。
Console.WriteLine($"hex={FrameText.CountHexDigits("0aZf9")}");
foreach (var len in FrameText.ScanFieldLengths(message, fieldCount: 2))
    Console.WriteLine($"field={len}");
Console.WriteLine($"asyncSum={FrameText.SumHeaderBytesAsync(message).GetAwaiter().GetResult()}");
Console.WriteLine($"gotoTotal={FrameText.GotoScanTotal(message, stride: 1)}");
