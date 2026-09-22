using System.Runtime.InteropServices;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Compositor.Windows;

// The generated SDK keeps PDF activation factories alive for the entire process.
// On WARP, releasing the PDF device from DLL_PROCESS_DETACH is too late: the OS
// thread pool has already stopped. Own these two factories per call so COM can
// release the renderer normally, before process teardown, without forced unloads.
internal static class NativePdf
{
    static readonly object Gate = new();
    static int active;
    static bool used, stopping;

    internal static IDisposable BeginOperation()
    {
        lock (Gate)
        {
            if (stopping) throw new OperationCanceledException("PDF renderer is shutting down.");
            used = true; active++; return new Operation();
        }
    }
    sealed class Operation : IDisposable
    {
        bool disposed;
        public void Dispose()
        {
            lock (Gate)
            {
                if (disposed) return;
                disposed = true; active--; Monitor.PulseAll(Gate);
            }
        }
    }

    // IPdfDocumentStatics is the documented Windows SDK ABI in windows.data.pdf.h.
    // Slots 0..5 are IUnknown/IInspectable, 6..7 are the file overloads, 8 is stream.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int LoadStream(IntPtr self, IntPtr stream, out IntPtr operation);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int Activate(IntPtr self, out IntPtr instance);
    static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    internal static async Task<PdfDocument> LoadAsync(IRandomAccessStream stream, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var factory = WinRT.ActivationFactory.Get("Windows.Data.Pdf.PdfDocument", new Guid("433a0b5f-c007-4788-90f2-08143d922599"));
        IntPtr input = WinRT.MarshalInterface<IRandomAccessStream>.FromManaged(stream), operation = IntPtr.Zero;
        IAsyncOperation<PdfDocument>? pending = null;
        try
        {
            Marshal.ThrowExceptionForHR(Method<LoadStream>(factory.ThisPtr, 8)(factory.ThisPtr, input, out operation));
            pending = WinRT.MarshalInterface<IAsyncOperation<PdfDocument>>.FromAbi(operation);
            return await pending.AsTask(token).ConfigureAwait(false);
        }
        finally
        {
            if (pending != null && pending.Status != AsyncStatus.Started) pending.Close();
            if (operation != IntPtr.Zero) Marshal.Release(operation);
            if (input != IntPtr.Zero) Marshal.Release(input);
        }
    }

    internal static async Task RenderAsync(PdfPage page, IRandomAccessStream output, PdfPageRenderOptions options, CancellationToken token)
    {
        var pending = page.RenderToStreamAsync(output, options);
        try { await pending.AsTask(token).ConfigureAwait(false); }
        finally { if (pending.Status != AsyncStatus.Started) pending.Close(); }
    }

    internal static PdfPageRenderOptions CreateRenderOptions()
    {
        using var factory = WinRT.ActivationFactory.Get("Windows.Data.Pdf.PdfPageRenderOptions");
        IntPtr instance = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(Method<Activate>(factory.ThisPtr, 6)(factory.ThisPtr, out instance));
            return WinRT.MarshalInspectable<PdfPageRenderOptions>.FromAbi(instance);
        }
        finally { if (instance != IntPtr.Zero) Marshal.Release(instance); }
    }

    [DllImport("ole32.dll")]
    static extern void CoFreeUnusedLibrariesEx(uint unloadDelay, uint reserved);

    internal static void ReleaseUnused()
    {
        // Async PDF work runs in the MTA. COM's unused-library list is apartment
        // scoped, so cleaning only the WPF UI's STA leaves those factories alive.
        var worker = new Thread(ReleaseUnusedCore) { Name = "Morupixel PDF shutdown" };
        worker.SetApartmentState(ApartmentState.MTA); worker.Start(); worker.Join();
        ReleaseUnusedCore();
    }

    static void ReleaseUnusedCore()
    {
        // Only COM servers reporting DllCanUnloadNow == S_OK are eligible.
        // Allow their worker threads time to exit between the two COM passes.
        // WinRT async completion and native event wrappers release references
        // in stages. Give those callbacks time to finish before the next GC pass.
        for (int pass = 0; pass < 3; pass++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers();
            CoFreeUnusedLibrariesEx(100, 0);
            Thread.Sleep(150);
            CoFreeUnusedLibrariesEx(100, 0);
        }
    }

    internal static void Shutdown()
    {
        lock (Gate)
        {
            stopping = true;
            if (!used) return;
            while (active != 0) Monitor.Wait(Gate);
        }
        ReleaseUnused();
    }
}
