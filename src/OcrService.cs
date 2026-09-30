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

    // 引擎初始化互斥锁 (双重检查: 初始化只执行一次)
    private readonly object _gate = new();
    // 懒加载的 OCR 引擎实例
    private PaddleOcrAll? _ocr;

    /// <summary>状态提示 (可能在后台线程触发, 订阅方需自行切回 UI 线程)</summary>
    public event Action<string>? StatusChanged;

    /// <summary>
    /// 识别文字。入参是 TrySnapshot 预先拷出的像素缓冲,
    /// 后台推理期间不接触源 Bitmap, 避免与 UI 线程的 DrawImage 抢位图锁
    /// </summary>
    public PaddleOcrResult Recognize(byte[] pixels, int width, int height, int stride, CancellationToken cancellationToken)
    {
        var ocr = EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        return ocr.Run(pixels.AsSpan(), width, height, stride, ImagePixelFormat.Bgra32);
    }

    /// <summary>
    /// 快照: LockBits 只读锁定 + Marshal.Copy 拷贝像素, 立即解锁 (微秒级)。
    /// 应在 UI 线程、窗口显示之前调用: 若后台线程的 LockBits 与 UI 的 DrawImage 并发,
    /// GDI+ 同一 Bitmap 的锁会冲突 (DrawImage 内部也持锁), 抛 "Bitmap region is already locked"。
    /// </summary>
    public static byte[]? TrySnapshot(Bitmap image, out int width, out int height, out int stride)
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

    /// <summary>懒加载 OCR 引擎; 首次调用需加载模型, 耗时较长</summary>
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
                // 注: 当前加载的是 ChineseV6Medium, 自带 CLS 模型, 可开启方向分类
                UseDirectionClassification = true,
                LineWorkerCount = 0, // min(ProcessorCount, 4)
            };

            _ocr = PaddleOcrAll.Load(ChineseV6MediumModels.Default, options);
            StatusChanged?.Invoke("引擎就绪, 正在识别\u2026");
            return _ocr;
        }
    }

    /// <summary>释放引擎占用的 SIMD 内存</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _ocr?.Dispose();
            _ocr = null;
        }
    }
}
