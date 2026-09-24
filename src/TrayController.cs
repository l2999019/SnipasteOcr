using System.Runtime.InteropServices;
using System.Diagnostics;
using System.ComponentModel;
using SnipasteOcr.Native;

namespace SnipasteOcr;

/// <summary>
/// 纯 Win32 托盘图标: 自注册窗口类 + Shell_NotifyIcon + TrackPopupMenu。
/// 不依赖 WinForms 消息机制, 与手动消息循环完全兼容。
/// </summary>
public sealed class TrayController : IDisposable
{
    // 注意: TPM_RETURNCMD 时 TrackPopupMenu 返回的是菜单项"位置"而不是命令ID,
    // 所以这里按可见项位置赋值 (截图识别=0 位置会撞"取消", 用 +0x1000 偏移规避)
    private const int CMD_SNIP_OCR = 0x1000;
    private const int CMD_SNIP_IMAGE = 0x1001;
    private const int CMD_EXIT = 0x1002;
    private static readonly uint TRAY_CALLBACK = User32.WM_APP + 1;

    // 静态持有: 窗口过程委托必须防止被 GC; 单实例引用用于回调
    private static WndProcDelegate _wndProcRef = WndProc;
    private static TrayController? _instance;

    private readonly IntPtr _hwnd;
    private readonly IntPtr _hIcon;
    private readonly IntPtr _hMenu;
    private Icon? _icon;

    /// <summary>宿主窗口句柄 (热键也注册在这里)</summary>
    public IntPtr WindowHandle => _hwnd;

    public event Action? SnipOcrRequested;
    public event Action? SnipImageRequested;
    public event Action? ExitRequested;

    public TrayController()
    {
        _instance = this;

        _hIcon = CreateIcon();
        _icon = Icon.FromHandle(_hIcon);

        // 1. 窗口类 + 不可见宿主窗口
        var wc = new User32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<User32.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcRef),
            hInstance = Kernel32.GetModuleHandle(null),
            lpszClassName = "SnipasteOcr.Host",
        };
        ushort atom = User32.RegisterClassEx(ref wc);
        if (atom == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx 失败");

        // 可见但移到屏幕外: TrackPopupMenu 需要 SetForegroundWindow 成功, 而隐藏窗口无法设为前台
        _hwnd = User32.CreateWindowEx(
            User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE, "SnipasteOcr.Host", "SnipasteOcr", 0,
            -20000, -20000, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx 失败");

        // 2. 弹出菜单
        _hMenu = User32.CreatePopupMenu();

        if (_hMenu == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "CreatePopupMenu 失败");
        }
        User32.AppendMenu(_hMenu, User32.MF_STRING, (IntPtr)CMD_SNIP_OCR, "截图并识别 (F1)");
        User32.AppendMenu(_hMenu, User32.MF_STRING, (IntPtr)CMD_SNIP_IMAGE, "仅截图 (F2)");
        User32.AppendMenu(_hMenu, User32.MF_SEPARATOR, IntPtr.Zero, null);
        User32.AppendMenu(_hMenu, User32.MF_STRING, (IntPtr)CMD_EXIT, "退出");

        // 3. 托盘图标
        var nid = new Shell32.NOTIFYICONDATA
        {
            cbSize = (int)Marshal.SizeOf<Shell32.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Shell32.NIF_MESSAGE | Shell32.NIF_ICON | Shell32.NIF_TIP | Shell32.NIF_VERSION | Shell32.NIF_SHOWTIP,
            uCallbackMessage = TRAY_CALLBACK,
            hIcon = _hIcon,
            szTip = "SnipasteOCR - F1 截图识别 / F2 截图",
            uVersion = 4,
        };
        Shell32.Shell_NotifyIcon(Shell32.NIM_SETVERSION, ref nid);
        if (!Shell32.Shell_NotifyIcon(Shell32.NIM_ADD, ref nid))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Shell_NotifyIcon 失败");
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == TRAY_CALLBACK)
        {
            // Shell_NotifyIcon 回调约定: wParam=图标ID, lParam=鼠标消息, 坐标用 GetCursorPos
            int trayMsg = (int)lParam.ToInt32();
            if (trayMsg == (int)User32.WM_LBUTTONDBLCLK)
            {
                _instance?.SnipOcrRequested?.Invoke();
                return IntPtr.Zero;
            }
            if (trayMsg == (int)User32.WM_RBUTTONUP)
            {
                CursorPos.GetCursorPos(out var pt);
                var inst = _instance;
                if (inst != null) inst.ShowMenu(hWnd, pt.x, pt.y);
                return IntPtr.Zero;
            }
            return IntPtr.Zero;
        }

        return User32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu(IntPtr hWnd, int x, int y)
    {
        // 标准序列: 挂接前台线程 -> 置顶窗口 -> 弹菜单 -> 自身收 WM_NULL -> 解挂
        IntPtr fg = Foreground.GetForegroundWindow();
        uint fgThread = Foreground.GetWindowThreadProcessId(fg, IntPtr.Zero);
        uint curThread = Kernel32.GetCurrentThreadId();
        bool attached = fgThread != curThread && Foreground.AttachThreadInput(curThread, fgThread, true);

        User32.SetForegroundWindow(hWnd);

        int cmd = User32.TrackPopupMenuEx(
            _hMenu,
            User32.TPM_LEFTALIGN |
            User32.TPM_TOPALIGN |
            User32.TPM_RIGHTBUTTON |
            User32.TPM_RETURNCMD,
            x,
            y,
            _hwnd,
            IntPtr.Zero);

        // 文档要求: 菜单所属窗口收到 WM_NULL 并处理掉 (交给主循环, 不在此嵌套泵消息)
        User32.PostMessage(hWnd, Foreground.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        if (attached)
            Foreground.AttachThreadInput(curThread, fgThread, false);

        // TPM_RETURNCMD 返回的是可见项位置(0基), 0=取消; 用 QueryMenuItemInfo 还原命令ID

        switch (cmd)
        {
            case CMD_SNIP_OCR: _instance?.SnipOcrRequested?.Invoke(); break;
            case CMD_SNIP_IMAGE: _instance?.SnipImageRequested?.Invoke(); break;
            case CMD_EXIT: _instance?.ExitRequested?.Invoke(); break;
        }
    }

    private static IntPtr CreateIcon()
    {
        // 从嵌入资源加载应用图标 (32x32, 托盘清晰且省内存)
        var asm = typeof(TrayController).Assembly;
        using var stream = asm.GetManifestResourceStream("SnipasteOcr.appicon.png");
        if (stream != null)
        {
            using var full = new Bitmap(stream);
            using var bmp = new Bitmap(full, new Size(32, 32));
            return bmp.GetHicon();
        }
        // 资源缺失时回退: 蓝色方块占位
        using var fb = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(fb))
            g.Clear(Color.FromArgb(255, 40, 98, 190));
        return fb.GetHicon();
    }

    public void Dispose()
    {
        var nid = new Shell32.NOTIFYICONDATA
        {
            cbSize = (int)Marshal.SizeOf<Shell32.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
        };
        Shell32.Shell_NotifyIcon(Shell32.NIM_DELETE, ref nid);
        User32.DestroyMenu(_hMenu);
        if (_hwnd != IntPtr.Zero)
            User32.DestroyWindow(_hwnd);
        _icon?.Dispose();
        User32.DestroyIcon(_hIcon);
        _instance = null;
    }
}
