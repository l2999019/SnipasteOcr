namespace SnipasteOcr;

/// <summary>
/// 截图流程协调: 启动截图覆盖层, 结束后打开 OCR 窗口或把图片写入剪贴板
/// </summary>
public static class SnipCoordinator
{
    // 跟踪所有打开的窗体, 避免重复启动截图层
    private static readonly HashSet<Form> _openForms = [];

    /// <summary>当前是否有可见的截图覆盖层</summary>
    internal static bool OverlayVisible => _openForms.OfType<SnipOverlayForm>().Any(o => o.IsHandleCreated && o.Visible);
    internal static Form? GetOverlay() => _openForms.OfType<SnipOverlayForm>().FirstOrDefault();

    /// <summary>启动截图; ocr=true 确认后打开 OCR 窗口, 否则图片复制到剪贴板; 已有截图层时忽略</summary>
    public static void Start(bool ocr)
    {
        // 已有截图层在用时忽略
        if (_openForms.OfType<SnipOverlayForm>().Any())
            return;

        var overlay = new SnipOverlayForm(ocr);
        _openForms.Add(overlay);
        overlay.FormClosed += (_, _) => _openForms.Remove(overlay);
        overlay.Show();
    }
}
