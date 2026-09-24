using System.Drawing.Imaging;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Medium;

namespace SnipasteOcr;

/// <summary>
/// OCR 服务: 懒加载 SimdPaddleOCR 引擎 (模型来自 NuGet 嵌入资源, 完全离线)。
/// 对 Bitmap 仅做瞬时 LockBits 复制, 推理在独立字节缓冲上进行,
/// 不持有位图锁, UI 线程可随时重绘。
/// </summary>
public sealed class OcrService : IDisposable
{
    private static readonly Lazy<OcrService> _instance = new(() => new OcrService());
    public static OcrService Instance => _instance.Value;

    private readonly object _gate = new();
    private PaddleOcrAll? _ocr;

    /// <summary>状态提示 (可能在后台线程触发, 订阅方需自行切回 UI 线程)</summary>
    public event Action<string>? StatusChanged;

    public PaddleOcrResult Recognize(Bitmap image, CancellationToken cancellationToken)
    {
        // 瞬时锁定: 仅复制像素, 微秒级; 推理期间不持有位图锁
        byte[] pixels = Snapshot(image, out int width, out int height, out int stride);

        var ocr = EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();

        lock (pixels)
        {
            return ocr.Run(pixels.AsSpan(), width, height, stride, ImagePixelFormat.Bgra32);
        }
    }

    private static byte[] Snapshot(Bitmap image, out int width, out int height, out int stride)
    {
        width = image.Width;
        height = image.Height;
        BitmapData data = image.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            stride = data.Stride;
            byte[] pixels = new byte[stride * height];
            // 瞬时拷贝: Marshal.Copy 直接 Scan0 -> 托管数组, 无需 unsafe
            System.Runtime.InteropServices.Marshal.Copy(
                data.Scan0, pixels, 0, stride * height);
            return pixels;
        }
        finally
        {
            image.UnlockBits(data);
        }
    }

    private PaddleOcrAll EnsureInitialized()
    {
        lock (_gate)
        {
            if (_ocr is not null)
                return _ocr;

            StatusChanged?.Invoke("正在加载 OCR 引擎\u2026");

            var options = new PaddleOcrOptions
            {
                // ChineseV6Small bundle 不含方向分类 CLS 模型, 必须关闭
                UseDirectionClassification = true,
                LineWorkerCount = 0, // min(ProcessorCount, 4)
            };

            _ocr = PaddleOcrAll.Load(ChineseV6MediumModels.Default, options);
            StatusChanged?.Invoke("引擎就绪, 正在识别\u2026");
            return _ocr;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _ocr?.Dispose();
            _ocr = null;
        }
    }
}
