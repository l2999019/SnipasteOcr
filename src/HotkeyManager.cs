using SnipasteOcr.Native;

namespace SnipasteOcr;

/// <summary>
/// 全局热键的注册与热更新: 启动时读配置注册, 改快捷键时先注销再注册。
/// 新组合键被别的程序占用时自动回滚成原配置, 不会把用户留在"没有热键"的状态。
/// </summary>
public static class HotkeyManager
{
    private static IntPtr _hwnd;

    /// <summary>当前生效的配置 (注册失败回退后可能与文件内容不同)</summary>
    public static HotkeySettings Current { get; private set; } = new();

    /// <summary>启动时调用: 读配置并在宿主窗口上注册; 返回需要弹窗提示的条目 (无问题则为空)</summary>
    public static List<string> Initialize(IntPtr hostWindow)
    {
        _hwnd = hostWindow;
        Current = HotkeySettings.Load();
        UnregisterAll();

        var notes = new List<string>();
        RegisterWithFallback(notes, HotKeyId.SnipOcr, HotkeySettings.LabelOcr, Current.SnipOcr, HotkeySettings.DefaultOcr);
        RegisterWithFallback(notes, HotKeyId.SnipImage, HotkeySettings.LabelImage, Current.SnipImage, HotkeySettings.DefaultImage);
        return notes;
    }

    /// <summary>
    /// 应用新配置: 全部注册成功才生效并写文件; 任一项失败则回滚到原配置。
    /// 返回 null 表示成功, 否则返回失败原因
    /// </summary>
    public static string? TryApply(HotkeySettings next)
    {
        if (next.SelfConflict)
            return $"两个功能不能用同一个快捷键 ({next.SnipOcr.Display})";
        if (!next.SnipOcr.IsRegisterable)
            return $"“{HotkeySettings.LabelOcr}”的快捷键 {next.SnipOcr.Display} 无效";
        if (!next.SnipImage.IsRegisterable)
            return $"“{HotkeySettings.LabelImage}”的快捷键 {next.SnipImage.Display} 无效";

        var previous = Current;
        UnregisterAll();

        string? conflict = TryRegisterAll(next);
        if (conflict != null)
        {
            UnregisterAll();
            TryRegisterAll(previous);
            return $"{conflict}; 已保持原快捷键 ({previous.SnipOcr.Display} / {previous.SnipImage.Display})";
        }

        Current = next.Clone();
        try
        {
            Current.Save();
        }
        catch (Exception ex)
        {
            return $"快捷键已生效, 但写入配置文件失败, 下次启动会回到旧值 ({ex.Message})";
        }
        return null;
    }

    /// <summary>启动阶段: 配置里的热键注册不上时, 尝试用默认值顶替并说明情况</summary>
    private static void RegisterWithFallback(
        List<string> notes, HotKeyId id, string label, Hotkey hk, Hotkey fallback)
    {
        if (TryRegister(id, hk)) return;

        if (hk != fallback && TryRegister(id, fallback))
        {
            notes.Add($"“{label}”的快捷键 {hk.Display} 已被其他程序占用, 本次改用默认 {fallback.Display}");
            return;
        }
        notes.Add($"“{label}”的快捷键 {hk.Display} 注册失败 (可能被其他程序占用)");
    }

    /// <summary>注册全部热键, 返回第一条失败原因 (全部成功返回 null)</summary>
    private static string? TryRegisterAll(HotkeySettings s)
    {
        if (!TryRegister(HotKeyId.SnipOcr, s.SnipOcr))
            return $"“{HotkeySettings.LabelOcr}”的快捷键 {s.SnipOcr.Display} 已被占用";
        if (!TryRegister(HotKeyId.SnipImage, s.SnipImage))
            return $"“{HotkeySettings.LabelImage}”的快捷键 {s.SnipImage.Display} 已被占用";
        return null;
    }

    private static void UnregisterAll()
    {
        User32.UnregisterHotKey(_hwnd, (int)HotKeyId.SnipOcr);
        User32.UnregisterHotKey(_hwnd, (int)HotKeyId.SnipImage);
    }

    /// <summary>MOD_NOREPEAT: 按住不放不会连续触发截图</summary>
    private static bool TryRegister(HotKeyId id, Hotkey hk) =>
        hk.IsRegisterable &&
        User32.RegisterHotKey(_hwnd, (int)id, hk.Modifiers | Hotkey.MOD_NOREPEAT, hk.Vk);
}
