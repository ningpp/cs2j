using SampleUnsafeFfmLib;

var message = new byte[] { 1, 2, 3, 4, 5 };

var sum = FrameHeader.Summarize(message, out var last);
Console.WriteLine($"sum={sum},last={last}");
Console.WriteLine($"first={FrameHeader.FirstPayloadByte()}");
Console.WriteLine($"viaHolder={FrameHeader.ViaHolder(new FrameHolder())}");
