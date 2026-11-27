using System.Runtime.InteropServices;
using SnipasteOcr.Native;

namespace SnipasteOcr;

/// <summary>
/// 应用入口: 托盘 + 全局热键 + 手动消息循环 (NativeAOT 下 MessageLoop.Run 不可用)
/// 所有关键事件写日志, 便于诊断
/// </summary>
internal static class Program
{
    private static Mutex? _singleInstanceMutex;

    // 托盘菜单点"快捷键设置"时, 从 WndProc 里 PostMessage 出来,
    // 交回消息循环再打开对话框 (避免在窗口过程内嵌套模态循环)
    private const uint WM_SHOW_HOTKEYS = User32.WM_APP + 2;

    [STAThread]
    /// <summary>
    /// 程序入口: 单实例检查 -> 托盘/热键初始化 -> 手动消息循环
    /// </summary>
    private static int Main(string[] args)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            // WinForms 全局初始化 (DPI 感知/默认控件行为)

            _singleInstanceMutex = new Mutex(true, @"Local\SnipasteOcr.SingleInstance", out bool createdNew);
            // 命名 Mutex 防多开: 已存在则提示后退出
            if (!createdNew)
            {
                MessageBox.Show("SnipasteOCR 已在运行 (请查看系统托盘)。", "SnipasteOCR", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            using var tray = new TrayController();
            // 托盘图标 + 不可见宿主窗口; 全局热键也注册在该窗口上
            tray.SnipOcrRequested += () => SnipCoordinator.Start(ocr: true);
            tray.SnipImageRequested += () => SnipCoordinator.Start(ocr: false);
            tray.HotkeysRequested += () =>
                User32.PostMessage(tray.WindowHandle, WM_SHOW_HOTKEYS, IntPtr.Zero, IntPtr.Zero);

            IntPtr hwnd = tray.WindowHandle;

            // 全局热键来自配置文件 (%APPDATA%\SnipasteOcr\hotkeys.txt), 缺失时用默认的 F1 / F2
            var notes = HotkeyManager.Initialize(hwnd);
            tray.SetHotkeyLabels(HotkeyManager.Current);
            if (notes.Count > 0)
            {
                MessageBox.Show(
                    string.Join("\n", notes) +
                    "\n\n仍可通过右键托盘图标操作, 也可在“快捷键设置”里更换热键。",
                    "SnipasteOCR - 热键提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            tray.ExitRequested += () => Environment.Exit(0);

            while (true)
            {
                try
                {
                    // 手动消息泵: 泵空消息队列, 空闲时睡 1ms (NativeAOT 下无 MessageLoop.Run)
                    while (User32.PeekMessage(out var msg, IntPtr.Zero, 0, 0, User32.PM_REMOVE))
                    {
                        if (msg.message == User32.WM_HOTKEY)
                        {
                            int id = msg.wParam.ToInt32();
                            if (id == (int)HotKeyId.SnipOcr)
                                SnipCoordinator.Start(ocr: true);
                            else if (id == (int)HotKeyId.SnipImage)
                                SnipCoordinator.Start(ocr: false);
                        }
                        else if (msg.message == WM_SHOW_HOTKEYS)
                        {
                            ShowHotkeyDialog(tray);
                        }

                        User32.TranslateMessage(ref msg);
                        User32.DispatchMessage(ref msg);
                    }
                    Thread.Sleep(1);
                }
                catch
                {
                    // 极端异常不致命, 降速后继续重试, 避免死循环空转
                    Thread.Sleep(100);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("启动失败:\n" + ex, "SnipasteOCR", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            // 退出前释放 OCR 引擎 (SIMD 内存缓冲)
            OcrService.Instance.Dispose();
        }
    }

    /// <summary>打开快捷键设置对话框; 保存成功后刷新托盘菜单与托盘提示</summary>
    private static void ShowHotkeyDialog(TrayController tray)
    {
        using var dialog = new HotkeyDialog(HotkeyManager.Current, candidate =>
        {
            string? error = HotkeyManager.TryApply(candidate);
            if (error == null) tray.SetHotkeyLabels(HotkeyManager.Current);
            return error;
        });
        dialog.ShowDialog();
    }
   
}
