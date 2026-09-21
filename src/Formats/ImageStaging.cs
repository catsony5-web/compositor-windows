using System.IO;

namespace Compositor.Windows;

internal static class ImageStaging
{
    internal const long MemoryThresholdBytes = 64L * 1024 * 1024;

    // WIC needs seekable image data. Large encoded images must not require a
    // second giant managed array, and MemoryStream cannot hold images over 2 GiB.
    internal static FileStream CreateTemporaryStream() => new(
        Path.Combine(Path.GetTempPath(), "morupixel-image-" + Guid.NewGuid().ToString("N") + ".tmp"),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024,
        FileOptions.DeleteOnClose | FileOptions.SequentialScan);
}
