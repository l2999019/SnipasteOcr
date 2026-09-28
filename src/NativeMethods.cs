using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Win32 P/Invoke 声明集: 消息循环 / 热键 / 窗口 / 菜单 / 托盘 / 输入模拟。
/// 用于 NativeAOT 手动消息循环与纯 Win32 托盘实现
/// </summary>
namespace SnipasteOcr.Native;

    /// <summary>热键 ID (传给 RegisterHotKey, WM_HOTKEY 消息 wParam 返回)</summary>
public enum HotKeyId
{
    SnipOcr = 0xB001,
    SnipImage = 0xB002,
}

public static class User32
{
    // ===== 消息常量 =====
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_APP = 0x8000;
    public const int WM_DESTROY = 0x0002;
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int WM_COMMAND = 0x0111;
    public const int HTCAPTION = 2;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WS_EX_TOOLWINDOW = 0x0080;
    public const uint WS_EX_NOACTIVATE = 0x0800;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint TPM_LEFTALIGN = 0;
    public const uint TPM_TOPMOST = 0x0200;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_CENTERALIGN = 0x0004;
    public const uint TPM_RIGHTALIGN = 0x0008;

    public const uint TPM_TOPALIGN = 0x0000;
    public const uint TPM_VCENTERALIGN = 0x0010;
    public const uint TPM_BOTTOMALIGN = 0x0020;

    public const uint TPM_LEFTBUTTON = 0x0000;
    public const uint TPM_RIGHTBUTTON = 0x0002;



    // ===== 菜单常量 =====
    public const uint MF_STRING = 0;
    public const uint MF_SEPARATOR = 0x800;

    // ===== 结构体 =====

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public System.Drawing.Point pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    // ===== 消息循环 =====

    // winuser.h: PM_NOREMOVE 0x0000 / PM_REMOVE 0x0001 / PM_NOYIELD 0x0002
    public const uint PM_NOREMOVE = 0x0000;
    public const uint PM_REMOVE = 0x0001;
    public const uint PM_NOYIELD = 0x0002;

    /// <summary>从消息队列取消息; hWnd=0 取当前线程全部消息, PM_REMOVE 表示取出</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
    /// <summary>查询虚拟键状态 (高位 0x8000 = 当前按下); 用于 WndProc 兜底判断 Ctrl</summary>
    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);

    /// <summary>把键盘/鼠标消息翻译成字符消息 (WM_CHAR 等), 配合 DispatchMessage 使用</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);
    /// <summary>把消息派发给对应窗口的 WndProc</summary>

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    // ===== 热键 =====

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    /// <summary>注册全局热键 (无修饰键, 仅 F1/F2 这类虚拟键)</summary>
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    /// <summary>注销热键</summary>
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ===== 窗口类 / 窗口 =====

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    /// <summary>注册窗口类; 成功返回类原子号, 失败 0</summary>
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    /// <summary>创建窗口 (含扩展样式), 返回窗口句柄</summary>
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    /// <summary>默认窗口过程: 未处理的系统消息交给系统 (默认光标/边框/销毁等)</summary>
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    /// <summary>销毁窗口 (异步, 会发 WM_DESTROY)</summary>
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    /// <summary>显示/隐藏窗口; SW_HIDE 隐藏, SW_SHOWNA 显示但不抢焦点</summary>
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public const int SW_HIDE = 0;
    public const int SW_SHOWNA = 8;

    [DllImport("user32.dll")]
    /// <summary>把窗口设为前台 (弹菜单前必须, 否则菜单立即消失)</summary>
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // ===== 菜单 =====

    [DllImport("user32.dll", SetLastError = true)]
    /// <summary>创建弹出菜单</summary>
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    /// <summary>追加菜单项 (文本项传 ID, 分隔线传 MF_SEPARATOR + 空文本)</summary>
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIdNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    /// <summary>销毁菜单</summary>
    public static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    /// <summary>在指定屏幕坐标弹出菜单并阻塞等待选择; TPM_RETURNCMD 时返回可见项位置(0基)</summary>
    public static extern int TrackPopupMenuEx(
    IntPtr hMenu,
    uint uFlags,
    int x,
    int y,
    IntPtr hWnd,
    IntPtr lptpm);

    // ===== 图标 =====


    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    /// <summary>向窗口异步投递消息 (弹菜单后补一个 WM_NULL, 让主循环知道菜单已处理完毕)</summary>
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    /// <summary>销毁图标句柄</summary>
    public static extern bool DestroyIcon(IntPtr handle);
}

public static class Shell32
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    public const uint NIM_ADD = 0;
    public const uint NIM_MODIFY = 1;
    public const uint NIM_DELETE = 2;
    public const uint NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 1;
    public const uint NIF_ICON = 2;
    public const uint NIF_TIP = 4;
    public const uint NIF_VERSION = 4;
    public const uint NIF_SHOWTIP = 0x80000000;

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);
}

/// <summary>Win32 窗口过程委托 (必须静态持有引用, 防止被 GC 回收)</summary>
public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

public static class Kernel32
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();
}

public static class Input
{
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint dwTime;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>模拟一次按下+释放 F1/F2</summary>
    public static void SendKey(ushort vk)
    {
        var down = new INPUT { type = 0 }; down.ki.wVk = vk;
        var up = new INPUT { type = 0 }; up.ki.wVk = vk; up.ki.dwFlags = 2;
        SendInput(2, new[] { down, up }, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }
}

public static class Foreground
{
    public const uint WM_NULL = 0x0000;

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
}

public static class CursorPos
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);
}

public static class Menu
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MENUITEMINFOW
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        [MarshalAs(UnmanagedType.LPWStr)] public string? dwTypeData;
        public uint cch;
        public int iBitmap;
        public IntPtr hbmpItem;
        public uint dwStateBitsMask;
        public uint dwStateNid;
        public IntPtr dwTypeDataExtra;
    }

    public const uint MIIM_ID = 0x00000002;
    public const int MFI_UNCHECKED = 0x0000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryMenuItemInfo(IntPtr hMenu, uint uItemID, bool fByPosition, ref MENUITEMINFOW lpmii);
}
