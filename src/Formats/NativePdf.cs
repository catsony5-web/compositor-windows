using System.Collections;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Compositor.Windows;

// Windows provides the PDF renderer (Windows.Data.Pdf). It is called through its documented COM
// ABI (windows.data.pdf.h) with only the members Morupixel uses, instead of shipping the 24 MB
// generated Windows SDK projection. Interface identifiers and method slots come from the
// Windows metadata (Windows.Data.winmd, Windows.Foundation.winmd, Windows.Storage.winmd).
//
// Every native reference is owned and released deterministically. On WARP, releasing the PDF
// device from DLL_PROCESS_DETACH is too late: the OS thread pool has already stopped. COM must
// release the renderer normally, before process teardown, without forced unloads (Shutdown).
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

    // Interface identifiers (GuidAttribute values in the Windows metadata).
    static readonly Guid PdfDocumentStaticsId = new("433a0b5f-c007-4788-90f2-08143d922599");
    static readonly Guid PdfPageRenderOptionsId = new("3c98056f-b7cf-4c29-9a04-52d90267f425");
    static readonly Guid ClosableId = new("30d5a829-7fa4-4026-83bb-d75bae4ea99e");
    static readonly Guid AsyncInfoId = new("00000036-0000-0000-c000-000000000046");
    static readonly Guid RandomAccessStreamId = new("905a0fe1-bc53-11df-8c49-001e4fc686da");
    static readonly Guid StreamId = new("0000000c-0000-0000-c000-000000000046");
    static readonly Guid SequentialStreamId = new("0c733a30-2a1c-11ce-ade5-00aa0044773d");
    static readonly Guid AgileObjectId = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
    internal static readonly Guid ActionCompletedHandlerId = new("a4ed5c81-76c9-40bd-8be6-b1d90fb20ae7");
    // Parameterized interfaces: SHA-1 identifiers derived from their signatures (see NativePdfInteropTests).
    // pinterface({fcdcf02c-e5d8-4478-915a-4d90b74b83a5};rc(Windows.Data.Pdf.PdfDocument;{ac7ebedd-80fa-4089-846e-81b77ff5a86c}))
    internal static readonly Guid DocumentLoadedHandlerId = new("8d4950b3-629d-5d7d-84cc-04c0dcf7942b");
    // pinterface({9fc2b0bb-e446-44e2-aa61-9cab8f636af2};rc(Windows.Data.Pdf.PdfDocument;{ac7ebedd-80fa-4089-846e-81b77ff5a86c}))
    internal static readonly Guid DocumentLoadOperationId = new("d6b166ec-099a-5ee2-ad2e-f4c88614aabb");

    const int Started = 0, Completed = 1, Canceled = 2;
    const int NotInitialized = unchecked((int)0x800401F0), AccessDenied = unchecked((int)0x80030005), InvalidFunction = unchecked((int)0x80030001);
    const int NoInterface = unchecked((int)0x80004002), NotImplemented = unchecked((int)0x80004001);

    [StructLayout(LayoutKind.Sequential)] struct WinSize { public float Width, Height; }
    [StructLayout(LayoutKind.Sequential)] internal struct WinRect { public float X, Y, Width, Height; }
    [StructLayout(LayoutKind.Sequential)] struct WinColor { public byte A, R, G, B; }

    [DllImport("combase.dll", ExactSpelling = true)]
    static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr text);
    [DllImport("combase.dll", ExactSpelling = true)]
    static extern int WindowsDeleteString(IntPtr text);
    [DllImport("combase.dll", ExactSpelling = true)]
    static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);
    [DllImport("combase.dll", ExactSpelling = true)]
    static extern int RoActivateInstance(IntPtr classId, out IntPtr instance);
    [DllImport("ole32.dll", ExactSpelling = true)]
    static extern int CoIncrementMTAUsage(out IntPtr cookie);
    [DllImport("shcore.dll", ExactSpelling = true)]
    static extern int CreateRandomAccessStreamOverStream(IntPtr stream, uint options, in Guid iid, out IntPtr randomAccessStream);
    [DllImport("shcore.dll", ExactSpelling = true)]
    static extern int CreateStreamOverRandomAccessStream(IntPtr randomAccessStream, in Guid iid, out IntPtr stream);
    [DllImport("ole32.dll", ExactSpelling = true)]
    static extern void CoFreeUnusedLibrariesEx(uint unloadDelay, uint reserved);

    static unsafe void* Slot(IntPtr instance, int slot) => (*(void***)instance)[slot];
    static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    static void Release(ref IntPtr instance) { if (instance != IntPtr.Zero) { Marshal.Release(instance); instance = IntPtr.Zero; } }

    static IntPtr Query(IntPtr instance, Guid iid)
    {
        Check(Marshal.QueryInterface(instance, in iid, out IntPtr result));
        return result;
    }

    // Activation needs a multithreaded apartment. Thread-pool threads join the process MTA implicitly
    // once it exists; keep one alive if this process has none yet, as the WinRT projections do.
    static int WithClassId(string name, Func<IntPtr, int> call)
    {
        Check(WindowsCreateString(name, name.Length, out IntPtr classId));
        try
        {
            int result = call(classId);
            if (result == NotInitialized && CoIncrementMTAUsage(out _) >= 0) result = call(classId);
            return result;
        }
        finally { WindowsDeleteString(classId); }
    }

    static IntPtr ActivationFactory(string name, Guid iid)
    {
        IntPtr factory = IntPtr.Zero;
        Check(WithClassId(name, id => RoGetActivationFactory(id, iid, out factory)));
        return factory;
    }

    static IntPtr Activate(string name, Guid iid)
    {
        IntPtr inspectable = IntPtr.Zero;
        Check(WithClassId(name, id => RoActivateInstance(id, out inspectable)));
        try { return Query(inspectable, iid); }
        finally { Release(ref inspectable); }
    }

    // Loads a PDF from a readable, seekable stream. The stream must stay open while the document is used.
    internal static Task<NativePdfDocument> LoadAsync(Stream input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!input.CanRead || !input.CanSeek) throw new ArgumentException("PDF input must be readable and seekable.", nameof(input));
        // Native work runs on the thread pool (multithreaded apartment): completion callbacks never
        // depend on a UI thread that may be blocked waiting for this task.
        return Task.Run(() => LoadCoreAsync(input, token));
    }

    static async Task<NativePdfDocument> LoadCoreAsync(Stream input, CancellationToken token)
    {
        IntPtr stream = ManagedStream.CreateRandomAccessStream(input), operation = IntPtr.Zero;
        try
        {
            operation = StartLoad(stream);
            await WhenCompleted(operation, DocumentLoadedHandlerId, token).ConfigureAwait(false);
            return new NativePdfDocument(LoadResult(operation));
        }
        finally { Finish(ref operation); Release(ref stream); }
    }

    static unsafe IntPtr StartLoad(IntPtr stream)
    {
        IntPtr factory = ActivationFactory("Windows.Data.Pdf.PdfDocument", PdfDocumentStaticsId), operation;
        try
        {
            // IPdfDocumentStatics: 6, 7 LoadFromFileAsync; 8 LoadFromStreamAsync(IRandomAccessStream).
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(factory, 8))(factory, stream, &operation));
            return operation;
        }
        finally { Release(ref factory); }
    }

    static unsafe IntPtr LoadResult(IntPtr operation)
    {
        // IAsyncOperation<PdfDocument>: 6 put_Completed, 7 get_Completed, 8 GetResults -> IPdfDocument.
        IntPtr document;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(operation, 8))(operation, &document));
        return document;
    }

    // Renders a page into an encoded image and decodes it.
    internal static async Task<Raster> RenderAsync(IntPtr page, int width, int height, byte backgroundAlpha, WinRect? source, CancellationToken token)
    {
        IntPtr output = IntPtr.Zero, action = IntPtr.Zero;
        try
        {
            output = Activate("Windows.Storage.Streams.InMemoryRandomAccessStream", RandomAccessStreamId);
            action = StartRender(page, output, width, height, backgroundAlpha, source);
            await WhenCompleted(action, ActionCompletedHandlerId, token).ConfigureAwait(false);
            ActionResult(action);
            token.ThrowIfCancellationRequested();
            using var encoded = new MemoryStream(ReadAll(output), false);
            return Raster.Load(encoded);
        }
        finally { Finish(ref action); Release(ref output); }
    }

    static unsafe IntPtr StartRender(IntPtr page, IntPtr output, int width, int height, byte backgroundAlpha, WinRect? source)
    {
        IntPtr options = Activate("Windows.Data.Pdf.PdfPageRenderOptions", PdfPageRenderOptionsId), action;
        try
        {
            // IPdfPageRenderOptions: 7 put_SourceRect, 9 put_DestinationWidth, 11 put_DestinationHeight, 13 put_BackgroundColor.
            if (source is { } area) Check(((delegate* unmanaged[Stdcall]<IntPtr, WinRect, int>)Slot(options, 7))(options, area));
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(options, 9))(options, (uint)width));
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(options, 11))(options, (uint)height));
            Check(((delegate* unmanaged[Stdcall]<IntPtr, WinColor, int>)Slot(options, 13))(options, new WinColor { A = backgroundAlpha, R = 255, G = 255, B = 255 }));
            // IPdfPage: 7 RenderToStreamAsync(IRandomAccessStream, PdfPageRenderOptions) -> IAsyncAction.
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, int>)Slot(page, 7))(page, output, options, &action));
            return action;
        }
        finally { Release(ref options); }
    }

    static unsafe void ActionResult(IntPtr action) =>
        // IAsyncAction: 8 GetResults reports a failure that completed the action.
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(action, 8))(action));

    static unsafe byte[] ReadAll(IntPtr randomAccessStream)
    {
        // IRandomAccessStream: 6 get_Size. The bytes are read through the stream's IStream view.
        ulong size;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, ulong*, int>)Slot(randomAccessStream, 6))(randomAccessStream, &size));
        var bytes = new byte[checked((int)size)];
        Check(CreateStreamOverRandomAccessStream(randomAccessStream, StreamId, out IntPtr stream));
        try
        {
            // IStream: 3 Read, 5 Seek.
            Check(((delegate* unmanaged[Stdcall]<IntPtr, long, uint, ulong*, int>)Slot(stream, 5))(stream, 0, 0, null));
            int offset = 0;
            fixed (byte* target = bytes)
                while (offset < bytes.Length)
                {
                    uint read;
                    Check(((delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, uint*, int>)Slot(stream, 3))(stream, target + offset, (uint)(bytes.Length - offset), &read));
                    if (read == 0) throw new EndOfStreamException("The PDF renderer returned a truncated image.");
                    offset += (int)read;
                }
        }
        finally { Release(ref stream); }
        return bytes;
    }

    internal static unsafe uint PageCount(IntPtr document)
    {
        // IPdfDocument: 6 GetPage, 7 get_PageCount.
        uint count;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(document, 7))(document, &count));
        return count;
    }

    internal static unsafe IntPtr Page(IntPtr document, uint index)
    {
        IntPtr page;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(document, 6))(document, index, &page));
        return page;
    }

    internal static unsafe (double Width, double Height) PageSize(IntPtr page)
    {
        // IPdfPage: 10 get_Size, in device-independent pixels.
        WinSize size;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, WinSize*, int>)Slot(page, 10))(page, &size));
        return (size.Width, size.Height);
    }

    internal static unsafe void ClosePage(ref IntPtr page)
    {
        if (page == IntPtr.Zero) return;
        // IClosable: 6 Close releases the page's renderer resources now.
        if (Marshal.QueryInterface(page, in ClosableId, out IntPtr closable) >= 0)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(closable, 6))(closable);
            Marshal.Release(closable);
        }
        Release(ref page);
    }

    // Waits for an asynchronous operation or action through its completion handler.
    static async Task WhenCompleted(IntPtr operation, Guid handlerId, CancellationToken token)
    {
        var completion = new Completion();
        Subscribe(operation, completion, handlerId);
        int status;
        using (token.Register(static state => Cancel((IntPtr)state!), operation))
            status = await completion.Task.ConfigureAwait(false);
        if (status == Completed) return;
        if (status == Canceled) throw new OperationCanceledException(token);
        throw ErrorOf(operation);
    }

    static unsafe void Subscribe(IntPtr operation, Completion completion, Guid handlerId)
    {
        IntPtr handler = Callbacks.Create(completion, handlerId);
        try
        {
            // IAsyncOperation<T> and IAsyncAction: 6 put_Completed. Called at once if already finished.
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(operation, 6))(operation, handler));
        }
        finally { Release(ref handler); }
    }

    static unsafe void Cancel(IntPtr operation)
    {
        // IAsyncInfo: 9 Cancel. The operation then completes with the Canceled status.
        if (Marshal.QueryInterface(operation, in AsyncInfoId, out IntPtr info) < 0) return;
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(info, 9))(info);
        Marshal.Release(info);
    }

    static unsafe Exception ErrorOf(IntPtr operation)
    {
        // IAsyncInfo: 8 get_ErrorCode. The HRESULT stays on the exception (e.g. a password-protected PDF).
        IntPtr info = Query(operation, AsyncInfoId);
        try
        {
            int error;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, 8))(info, &error));
            return Marshal.GetExceptionForHR(error < 0 ? error : unchecked((int)0x80004005))!;
        }
        finally { Release(ref info); }
    }

    static unsafe void Finish(ref IntPtr operation)
    {
        if (operation == IntPtr.Zero) return;
        // IAsyncInfo: 7 get_Status, 10 Close. A finished operation is closed before its last release.
        if (Marshal.QueryInterface(operation, in AsyncInfoId, out IntPtr info) >= 0)
        {
            int status;
            if (((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(info, 7))(info, &status) >= 0 && status != Started)
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(info, 10))(info);
            Marshal.Release(info);
        }
        Release(ref operation);
    }

    // Test hook: whether a loading operation answers its parameterized interface identifier.
    internal static async Task<bool> LoadOperationSupportsAsync(Stream input, Guid iid)
    {
        return await Task.Run(async () =>
        {
            IntPtr stream = ManagedStream.CreateRandomAccessStream(input), operation = IntPtr.Zero;
            try
            {
                operation = StartLoad(stream);
                bool supported = Marshal.QueryInterface(operation, in iid, out IntPtr typed) >= 0;
                if (supported) Marshal.Release(typed);
                await WhenCompleted(operation, DocumentLoadedHandlerId, default).ConfigureAwait(false);
                IntPtr document = LoadResult(operation); Release(ref document);
                return supported;
            }
            finally { Finish(ref operation); Release(ref stream); }
        }).ConfigureAwait(false);
    }

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

    // Completion handler object. Windows calls Invoke once, on any thread.
    sealed class Completion
    {
        readonly TaskCompletionSource<int> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<int> Task => source.Task;
        internal void Complete(int status) => source.TrySetResult(status);
    }

    // A .NET stream seen by Windows as a read-only COM IStream, wrapped into an IRandomAccessStream
    // by the system. Clones share the stream and a lock but keep their own position.
    sealed unsafe class ManagedStream
    {
        static readonly ConditionalWeakTable<Stream, object> Gates = new();
        readonly Stream stream;
        readonly object gate;
        long position;

        ManagedStream(Stream stream, object gate, long position) { this.stream = stream; this.gate = gate; this.position = position; }

        internal static IntPtr CreateRandomAccessStream(Stream input)
        {
            IntPtr stream = Callbacks.Create(new ManagedStream(input, Gates.GetValue(input, _ => new object()), 0), StreamId);
            try
            {
                Check(CreateRandomAccessStreamOverStream(stream, 0, RandomAccessStreamId, out IntPtr randomAccessStream));
                return randomAccessStream;
            }
            finally { Release(ref stream); }
        }

        internal int Read(byte* target, uint count, uint* read)
        {
            int total;
            lock (gate)
            {
                stream.Position = position;
                total = stream.Read(new Span<byte>(target, (int)Math.Min(count, int.MaxValue)));
                position += total;
            }
            if (read != null) *read = (uint)total;
            return 0;
        }

        internal int Seek(long move, uint origin, ulong* result)
        {
            long next;
            lock (gate)
            {
                next = origin switch { 0 => move, 1 => position + move, 2 => stream.Length + move, _ => -1 };
                if (next < 0) return InvalidFunction;
                position = next;
            }
            if (result != null) *result = (ulong)next;
            return 0;
        }

        internal long Length { get { lock (gate) return stream.Length; } }

        internal ManagedStream Clone() { lock (gate) return new(stream, gate, position); }
    }

    // COM identities for the completion handlers and the input stream. Built once; each object
    // also answers IAgileObject, so Windows may call it from any thread without marshaling.
    sealed unsafe class Callbacks : ComWrappers
    {
        static readonly Callbacks Instance = new();
        static readonly ComInterfaceEntry* HandlerEntries, StreamEntries;

        static Callbacks()
        {
            GetIUnknownImpl(out IntPtr queryInterface, out IntPtr addRef, out IntPtr release);
            IntPtr* unknown = Table(3, queryInterface, addRef, release);
            IntPtr* handler = Table(4, queryInterface, addRef, release);
            handler[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int>)&Invoke;
            IntPtr* stream = Table(14, queryInterface, addRef, release);
            stream[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, uint*, int>)&StreamRead;
            stream[4] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, uint*, int>)&StreamWrite;
            stream[5] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, long, uint, ulong*, int>)&StreamSeek;
            stream[6] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, ulong, int>)&StreamSetSize;
            stream[7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ulong, ulong*, ulong*, int>)&StreamCopyTo;
            stream[8] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int>)&StreamCommit;
            stream[9] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&StreamRevert;
            stream[10] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, uint, int>)&StreamLock;
            stream[11] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, uint, int>)&StreamLock;
            stream[12] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, StatStg*, uint, int>)&StreamStat;
            stream[13] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&StreamClone;
            HandlerEntries = Entries((DocumentLoadedHandlerId, (IntPtr)handler), (ActionCompletedHandlerId, (IntPtr)handler), (AgileObjectId, (IntPtr)unknown));
            StreamEntries = Entries((StreamId, (IntPtr)stream), (SequentialStreamId, (IntPtr)stream), (AgileObjectId, (IntPtr)unknown));
        }

        static IntPtr* Table(int slots, IntPtr queryInterface, IntPtr addRef, IntPtr release)
        {
            var table = (IntPtr*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(Callbacks), IntPtr.Size * slots);
            table[0] = queryInterface; table[1] = addRef; table[2] = release;
            return table;
        }

        static ComInterfaceEntry* Entries(params (Guid Iid, IntPtr Table)[] interfaces)
        {
            var entries = (ComInterfaceEntry*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(Callbacks), sizeof(ComInterfaceEntry) * interfaces.Length);
            for (int i = 0; i < interfaces.Length; i++) entries[i] = new ComInterfaceEntry { IID = interfaces[i].Iid, Vtable = interfaces[i].Table };
            return entries;
        }

        // Returns an owned pointer to the requested interface of a new COM identity for the object.
        internal static IntPtr Create(object instance, Guid iid)
        {
            IntPtr unknown = Instance.GetOrCreateComInterfaceForObject(instance, CreateComInterfaceFlags.None);
            try { return Query(unknown, iid); }
            finally { Marshal.Release(unknown); }
        }

        protected override ComInterfaceEntry* ComputeVtables(object obj, CreateComInterfaceFlags flags, out int count)
        {
            count = 3;
            return obj switch { Completion => HandlerEntries, ManagedStream => StreamEntries, _ => throw new NotSupportedException() };
        }
        protected override object CreateObject(IntPtr externalComObject, CreateObjectFlags flags) => throw new NotSupportedException();
        protected override void ReleaseObjects(IEnumerable objects) => throw new NotSupportedException();

        static T Self<T>(IntPtr self) where T : class => ComInterfaceDispatch.GetInstance<T>((ComInterfaceDispatch*)self);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        static int Invoke(IntPtr self, IntPtr asyncInfo, int status)
        {
            try { Self<Completion>(self).Complete(status); return 0; }
            catch (Exception e) { return e.HResult; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        static int StreamRead(IntPtr self, byte* target, uint count, uint* read)
        {
            try { return Self<ManagedStream>(self).Read(target, count, read); }
            catch (Exception e) { if (read != null) *read = 0; return e.HResult; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        static int StreamWrite(IntPtr self, byte* source, uint count, uint* written) { if (written != null) *written = 0; return AccessDenied; }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        static int StreamSeek(IntPtr self, long move, uint origin, ulong* result)
        {
            try { return Self<ManagedStream>(self).Seek(move, origin, result); }
            catch (Exception e) { return e.HResult; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        static int StreamSetSize(IntPtr self, ulong size) => AccessDenied;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        static int StreamCopyTo(IntPtr self, IntPtr target, ulong count, ulong* read, ulong* written) => NotImplemented;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        static int StreamCommit(IntPtr self, uint flags) => 0;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        static int StreamRevert(IntPtr self) => 0;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        static int StreamLock(IntPtr self, ulong offset, ulong count, uint type) => InvalidFunction;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        static int StreamStat(IntPtr self, StatStg* stat, uint flags)
        {
            try
            {
                if (stat == null) return unchecked((int)0x80030009); // STG_E_INVALIDPOINTER
                *stat = default; stat->Type = 2; stat->Size = (ulong)Self<ManagedStream>(self).Length; // STGTY_STREAM, STGM_READ
                return 0;
            }
            catch (Exception e) { return e.HResult; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        static int StreamClone(IntPtr self, IntPtr* clone)
        {
            try
            {
                if (clone == null) return unchecked((int)0x80030009);
                *clone = Create(Self<ManagedStream>(self).Clone(), StreamId);
                return 0;
            }
            catch (Exception e) { *clone = IntPtr.Zero; return e.HResult == 0 ? NoInterface : e.HResult; }
        }
    }

    // STATSTG with blittable fields (the name pointer stays null).
    [StructLayout(LayoutKind.Sequential)]
    struct StatStg
    {
        public IntPtr Name; public uint Type; public ulong Size;
        public long Modified, Created, Accessed;
        public uint Mode, LocksSupported; public Guid Clsid; public uint StateBits, Reserved;
    }
}

// A loaded PDF document (Windows.Data.Pdf.PdfDocument). Dispose releases the native document.
internal sealed class NativePdfDocument : IDisposable
{
    IntPtr document;
    internal NativePdfDocument(IntPtr document) { this.document = document; PageCount = NativePdf.PageCount(document); }
    internal uint PageCount { get; }
    internal NativePdfPage GetPage(uint index) => new(NativePdf.Page(Live, index));
    IntPtr Live => document != IntPtr.Zero ? document : throw new ObjectDisposedException(nameof(NativePdfDocument));
    public void Dispose() { if (document != IntPtr.Zero) { Marshal.Release(document); document = IntPtr.Zero; } }
}

// One page (Windows.Data.Pdf.PdfPage). Sizes are device-independent pixels (1/96 inch).
internal sealed class NativePdfPage : IDisposable
{
    IntPtr page;
    internal NativePdfPage(IntPtr page) { this.page = page; (Width, Height) = NativePdf.PageSize(page); }
    internal double Width { get; }
    internal double Height { get; }
    IntPtr Live => page != IntPtr.Zero ? page : throw new ObjectDisposedException(nameof(NativePdfPage));

    // backgroundAlpha 255 renders on white, 0 keeps transparency; source is in page DIPs.
    internal Task<Raster> RenderAsync(int width, int height, byte backgroundAlpha, System.Windows.Rect? source, CancellationToken token)
    {
        NativePdf.WinRect? area = source is { } r ? new NativePdf.WinRect { X = (float)r.X, Y = (float)r.Y, Width = (float)r.Width, Height = (float)r.Height } : null;
        IntPtr live = Live;
        // Like loading, rendering starts on the thread pool so no step waits for a UI thread.
        return Task.Run(() => NativePdf.RenderAsync(live, width, height, backgroundAlpha, area, token));
    }

    public void Dispose() => NativePdf.ClosePage(ref page);
}
