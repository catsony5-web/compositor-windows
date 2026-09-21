using System.IO;

namespace Compositor.Windows;

public static class CapacityFormatTests
{
    public static void Run(Action<string, Action> test)
    {
        static void RejectBeforeOutput(Document document, string messagePart)
        {
            using var output = new NoWriteStream();
            try { PhotoshopCompatibility.Write(document, output, layers: true); }
            catch (InvalidDataException e) when (e.Message.Contains(messagePart, StringComparison.Ordinal)) { return; }
            throw new InvalidOperationException("PSD capacity validation did not reject the document before output.");
        }

        test("PSD v1 rejects dimensions above 30000 before allocating a render", () =>
        {
            var document = new Document { Width = 30_001, Height = 1 };
            document.Add(new Layer { Pixels = new Raster(1, 1) });
            RejectBeforeOutput(document, "30,000px");
        });

        test("PSD v1 rejects a signed section overflow before allocating a render", () =>
        {
            var document = new Document { Width = 24_000, Height = 16_000 };
            var pixel = new Raster(1, 1);
            document.Add(new Layer { Name = "First", Pixels = pixel });
            document.Add(new Layer { Name = "Second", Pixels = pixel });
            RejectBeforeOutput(document, "PSD 레이어 섹션");
        });
    }

    // If a preflight regresses, fail at the first header byte before the writer
    // reaches its full-canvas rendering step. The tests retain only tiny rasters.
    sealed class NoWriteStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("PSD wrote output before rejecting capacity.");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new InvalidOperationException("PSD wrote output before rejecting capacity.");
        public override void WriteByte(byte value) => throw new InvalidOperationException("PSD wrote output before rejecting capacity.");
    }
}
