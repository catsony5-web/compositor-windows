namespace Compositor.Windows;

public sealed partial class MainWindow
{
    internal static void RunRenderingLifecycleTests(Action<string, Action> test)
    {
        test("closing discards queued frames and rejects late render callbacks", () =>
        {
            // The window is constructed offscreen; no dispatcher loop or native
            // window is started. Simulate work queued behind an active render.
            var window = new MainWindow(null) { headlessTesting = true };
            var render = new CancellationTokenSource();
            var preview = new CancellationTokenSource();
            var job = new CancellationTokenSource();
            window.renderCts = render; window.textPreviewCts = preview; window.jobCts = job;
            window.pendingFullRender = true; window.pendingGestureRender = true;
            window.gestureRenderTimer.Start();
            long generation = window.renderGeneration;

            window.StopRenderingForShutdown();
            if (!window.renderShutdown || window.pendingFullRender || window.pendingGestureRender ||
                window.gestureRenderTimer.IsEnabled || !render.IsCancellationRequested ||
                !preview.IsCancellationRequested || !job.IsCancellationRequested ||
                window.renderGeneration <= generation)
                throw new InvalidOperationException("Shutdown left queued rendering active");

            window.headlessTesting = false;
            window.QueueRender(); window.QueueRender(true);
            window.OnGestureRenderTick(null, EventArgs.Empty);
            if (window.pendingFullRender || window.pendingGestureRender || window.rendering || window.gestureRenderTimer.IsEnabled)
                throw new InvalidOperationException("Late callback scheduled rendering after shutdown");
            render.Dispose(); job.Dispose();
            // The preview source is normally disposed by its async worker.
            preview.Dispose();
        });
    }
}
