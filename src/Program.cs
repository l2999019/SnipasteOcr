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

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            ApplicationConfiguration.Initialize();

            _singleInstanceMutex = new Mutex(true, @"Local\SnipasteOcr.SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("SnipasteOCR 已在运行 (请查看系统托盘)。", "SnipasteOCR", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            using var tray = new TrayController();
            tray.SnipOcrRequested += () => SnipCoordinator.Start(ocr: true);
            tray.SnipImageRequested += () => SnipCoordinator.Start(ocr: false);

            IntPtr hwnd = tray.WindowHandle;

            var failures = new List<string>();
            if (!User32.RegisterHotKey(hwnd, (int)HotKeyId.SnipOcr, 0, (uint)Keys.F1))
            {
                failures.Add($"F1 注册失败: {Marshal.GetLastWin32Error()}");
            }
            if (!User32.RegisterHotKey(hwnd, (int)HotKeyId.SnipImage, 0, (uint)Keys.F2))
            {
                failures.Add($"F2 注册失败: {Marshal.GetLastWin32Error()}");
            }

            if (failures.Count > 0)
            {
                MessageBox.Show(
                    "以下热键注册失败 (可能被浏览器/IDE 等程序占用):\n  " + string.Join("\n  ", failures) +
                    "\n\n仍可右键系统托盘图标操作。",
                    "SnipasteOCR - 热键冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            tray.ExitRequested += () => Environment.Exit(0);

            while (true)
            {
                try
                {
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

                        User32.TranslateMessage(ref msg);
                        User32.DispatchMessage(ref msg);
                    }
                    Thread.Sleep(1);
                }
                catch
                {
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
            OcrService.Instance.Dispose();
        }
    }

   
}
