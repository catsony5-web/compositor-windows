using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Compositor.Windows;

/// <summary>Local U²-Net/U²-NetP compatible inference. The package includes pinned Apache-2.0 U²-NetP weights; images never leave the device.</summary>
public static class BackgroundRemoval
{
    public static string DefaultModelPath => Path.Combine(AppContext.BaseDirectory, "models", "u2netp.onnx");

    public static byte[] CreateMask(Raster source, string? modelPath = null, CancellationToken cancellationToken = default)
    {
        try { return CreateMaskCore(source, modelPath, cancellationToken); }
        catch (Exception error) when (IsNativeLoadError(error))
        {
            throw new InvalidOperationException("AI 엔진을 불러오지 못했습니다. ZIP 전체를 압축 해제했는지 확인하고 Microsoft Visual C++ x64 재배포 패키지(2019 이후 최신 버전)를 설치한 뒤 Morupixel을 다시 실행하세요.\n공식 설치 안내: https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist", error);
        }
    }
    static bool IsNativeLoadError(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) return true;
        return false;
    }
    static byte[] CreateMaskCore(Raster source, string? modelPath, CancellationToken cancellationToken)
    {
        modelPath ??= DefaultModelPath;
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(modelPath)) throw new FileNotFoundException("AI 모델이 없습니다. U²-NetP 호환 u2netp.onnx 모델을 선택하세요. 모델의 배포 출처와 사용 조건은 docs/BACKGROUND_REMOVAL.md를 참고하세요.", modelPath);
        var info = new FileInfo(modelPath);
        if (info.Length < 16 || info.Length > 512L * 1024 * 1024) throw new InvalidDataException("AI 모델 파일 크기는 16바이트 이상 512MB 이하로 제한됩니다.");
        using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount)), InterOpNumThreads = 1 };
        using var session = new InferenceSession(modelPath, options);
        if (session.InputMetadata.Count != 1) throw new NotSupportedException("RGB 입력 하나를 사용하는 U²-Net 호환 모델이 필요합니다.");
        var input = session.InputMetadata.Single();
        var shape = input.Value.Dimensions;
        if (input.Value.ElementType != typeof(float) || shape.Length != 4 || shape[0] != 1 || shape[1] != 3 || shape[2] != 320 || shape[3] != 320)
            throw new NotSupportedException("지원 모델 입력은 float32 [1, 3, 320, 320]입니다. U²-Net/U²-NetP ONNX 모델을 사용하세요.");
        var resized = ImportExport.Resize(source, 320, 320);
        var values = new float[3 * 320 * 320];
        double[] means = [.485, .456, .406], deviations = [.229, .224, .225];
        // U²-Net's reference preprocessing normalizes by the image maximum, not a fixed 255.
        double maximum = 1e-6;
        for (int i = 0; i < 320 * 320; i++) for (int c = 0; c < 3; c++) maximum = Math.Max(maximum, resized.Data[i * 4 + c]);
        for (int i = 0; i < 320 * 320; i++) for (int c = 0; c < 3; c++)
            values[c * 320 * 320 + i] = (float)((resized.Data[i * 4 + (2 - c)] / maximum - means[c]) / deviations[c]);
        cancellationToken.ThrowIfCancellationRequested();
        var tensor = new DenseTensor<float>(values, [1, 3, 320, 320]);
        using var runOptions = new RunOptions();
        using var registration = cancellationToken.Register(() => runOptions.Terminate = true);
        try
        {
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(input.Key, tensor)], [session.OutputNames[0]], runOptions);
            var output = results.First().AsTensor<float>();
            if (output.Rank != 4 || output.Dimensions[0] != 1 || output.Dimensions[1] != 1 || output.Dimensions[2] != 320 || output.Dimensions[3] != 320)
                throw new NotSupportedException("모델의 첫 출력이 float32 [1, 1, 320, 320] 마스크가 아닙니다.");
            var predicted = output.ToArray();
            if (predicted.Any(n => !float.IsFinite(n))) throw new InvalidDataException("모델이 유효하지 않은 마스크 값을 반환했습니다.");
            float min = predicted.Min(), max = predicted.Max();
            var mask = new Raster(320, 320);
            for (int i = 0; i < predicted.Length; i++)
            {
                // A constant model output contains no usable segmentation; preserve a foreground confidence
                // rather than dividing by zero or silently returning an empty result.
                byte value = max - min < 1e-8 ? Imaging.Byte(Math.Clamp(predicted[i], 0, 1) * 255) : Imaging.Byte((predicted[i] - min) / (max - min) * 255);
                mask.Data[i * 4] = mask.Data[i * 4 + 1] = mask.Data[i * 4 + 2] = value; mask.Data[i * 4 + 3] = 255;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var full = ImportExport.Resize(mask, source.Width, source.Height);
            var coverage = new byte[source.Width * source.Height];
            for (int i = 0; i < coverage.Length; i++) coverage[i] = full.Data[i * 4];
            return coverage;
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
    }

    public static byte[] CombineMasks(byte[] foreground, byte[]? existing)
    {
        if (existing != null && existing.Length != foreground.Length) throw new ArgumentException("마스크 크기가 다릅니다.");
        var result = (byte[])foreground.Clone();
        if (existing != null) for (int i = 0; i < result.Length; i++) result[i] = (byte)((result[i] * existing[i] + 127) / 255);
        return result;
    }
}
