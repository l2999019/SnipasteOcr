using System.Text;

namespace SnipasteOcr;

/// <summary>
/// 单个全局热键: Win32 修饰键位 (MOD_ALT/CONTROL/SHIFT/WIN) + 虚拟键码 (VK_*)。
/// 与人类可读文本 (Ctrl+Alt+1) 双向转换, 配置文件因此可以直接手工编辑。
/// </summary>
public readonly struct Hotkey : IEquatable<Hotkey>
{
    // winuser.h: RegisterHotKey 的 fsModifiers 位
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    /// <summary>Win7+: 长按只触发一次, 避免按住快捷键时反复截图</summary>
    public const uint MOD_NOREPEAT = 0x4000;

    public const uint MOD_MASK = MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_WIN;

    private const uint VK_SNAPSHOT = 0x2C; // PrintScreen 无法作为全局热键注册
    private const uint VK_PLUS = 0xBB;     // 与 '=' 共用虚拟键, 按 Shift 即 '+'

    public readonly uint Modifiers;
    public readonly uint Vk;

    public Hotkey(uint modifiers, uint vk)
    {
        Modifiers = modifiers & MOD_MASK;
        Vk = vk;
    }

    public bool IsEmpty => Vk == 0;
    public bool IsFunctionKey => Vk is >= 0x70 and <= 0x87; // F1-F24

    /// <summary>能否交给 RegisterHotKey: 有主键且不冲突; 无修饰键时主键必须是 F1-F24</summary>
    public bool IsRegisterable => !IsEmpty && Vk != VK_SNAPSHOT && (Modifiers != 0 || IsFunctionKey);

    /// <summary>特殊键显示名 (字母/数字/F 键/小键盘数字按规则生成, 这里只列不规则的)</summary>
    private static readonly (string Name, uint Vk)[] SpecialKeys =
    [
        ("Backspace", 0x08), ("Enter", 0x0D), ("Tab", 0x09), ("Space", 0x20), ("Esc", 0x1B),
        ("Home", 0x24), ("End", 0x23), ("PageUp", 0x21), ("PageDown", 0x22),
        ("Insert", 0x2D), ("Delete", 0x2E),
        ("Left", 0x25), ("Up", 0x26), ("Right", 0x27), ("Down", 0x28),
        ("Multiply", 0x6A), ("Add", 0x6B), ("Subtract", 0x6D), ("Decimal", 0x6E), ("Divide", 0x6F),
        (";", 0xBA), ("=", 0xBB), (",", 0xBC), ("-", 0xBD), (".", 0xBE), ("/", 0xBF),
        ("`", 0xC0), ("[", 0xDB), ("\\", 0xDC), ("]", 0xDD), ("'", 0xDE),
    ];

    /// <summary>名称 -> 虚拟键码 (含常用别名), 用于解析配置文件</summary>
    private static readonly Lazy<Dictionary<string, uint>> NameToVk = new(() =>
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        for (uint vk = 0x41; vk <= 0x5A; vk++) map[((char)vk).ToString()] = vk; // A-Z 本身就是大写
        for (uint vk = 0x30; vk <= 0x39; vk++) map[((char)vk).ToString()] = vk;
        for (uint vk = 0x70; vk <= 0x87; vk++) map["F" + (vk - 0x70 + 1)] = vk;
        for (uint vk = 0x60; vk <= 0x69; vk++) map["Num" + (vk - 0x60)] = vk;
        foreach (var (name, vk) in SpecialKeys) map[name] = vk;

        // 别名: 兼容手工编辑时常见的写法
        map["PGUP"] = 0x21;
        map["PGDN"] = 0x22;
        map["DEL"] = 0x2E;
        map["INS"] = 0x2D;
        map["RETURN"] = 0x0D;
        map["ESC"] = 0x1B;
        map["BACK"] = 0x08;
        map["PLUS"] = VK_PLUS;
        map["OEMPLUS"] = VK_PLUS;
        map["OEMMINUS"] = 0xBD;
        for (uint vk = 0x60; vk <= 0x69; vk++) map["NumPad" + (vk - 0x60)] = vk;
        return map;
    });

    /// <summary>虚拟键码 -> 显示名</summary>
    private static string KeyName(uint vk)
    {
        if (vk is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        if (vk is >= 0x60 and <= 0x69) return "Num" + (vk - 0x60);
        foreach (var (name, keyVk) in SpecialKeys)
            if (keyVk == vk) return name;
        return "0x" + vk.ToString("X2");
    }

    /// <summary>显示文本, 如 Ctrl+Alt+1; 未设置时返回 "未设置"</summary>
    public string Display
    {
        get
        {
            if (IsEmpty) return "未设置";
            var sb = new StringBuilder();
            if ((Modifiers & MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((Modifiers & MOD_ALT) != 0) sb.Append("Alt+");
            if ((Modifiers & MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((Modifiers & MOD_WIN) != 0) sb.Append("Win+");
            sb.Append(KeyName(Vk));
            return sb.ToString();
        }
    }

    /// <summary>解析 Display 形式的文本; 失败返回 false (调用方保留默认值)</summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        // 主键本身就是 '+' 时文本形如 "Ctrl++": 先把末尾那个加号当作主键摘掉
        // (只写 "Ctrl+" 这种残缺写法不算, 避免把笔误当成一个合法热键)
        bool plusKey = text.EndsWith("++", StringComparison.Ordinal);
        if (plusKey) text = text[..^1];

        uint mods = 0, vk = 0;
        foreach (string raw in text.Split('+'))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL": mods |= MOD_CONTROL; continue;
                case "ALT":
                case "MENU": mods |= MOD_ALT; continue;
                case "SHIFT": mods |= MOD_SHIFT; continue;
                case "WIN":
                case "WINDOWS":
                case "META": mods |= MOD_WIN; continue;
            }
            if (vk != 0) return false; // 只允许一个主键
            if (!NameToVk.Value.TryGetValue(part, out vk)) return false;
        }

        if (vk == 0 && plusKey) vk = VK_PLUS;
        hotkey = new Hotkey(mods, vk);
        return hotkey.IsRegisterable;
    }

    public bool Equals(Hotkey other) => Modifiers == other.Modifiers && Vk == other.Vk;
    public override bool Equals(object? obj) => obj is Hotkey other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Modifiers, Vk);
    public override string ToString() => Display;

    public static bool operator ==(Hotkey a, Hotkey b) => a.Equals(b);
    public static bool operator !=(Hotkey a, Hotkey b) => !a.Equals(b);
}

/// <summary>
/// 全局热键配置: 两个功能各持有一个热键, 存于 %APPDATA%\SnipasteOcr\hotkeys.txt。
/// 文件是纯文本 key=value, 用户可以不经过对话框直接改。
/// </summary>
public sealed class HotkeySettings
{
    /// <summary>功能名: 托盘菜单与热键提示共用同一套叫法</summary>
    public const string LabelOcr = "截图识别";
    public const string LabelImage = "仅截图";

    public static readonly Hotkey DefaultOcr = new(0, 0x70);   // F1
    public static readonly Hotkey DefaultImage = new(0, 0x71); // F2

    public Hotkey SnipOcr;
    public Hotkey SnipImage;

    public HotkeySettings() : this(DefaultOcr, DefaultImage) { }

    public HotkeySettings(Hotkey snipOcr, Hotkey snipImage)
    {
        SnipOcr = snipOcr;
        SnipImage = snipImage;
    }

    public HotkeySettings Clone() => new(SnipOcr, SnipImage);

    /// <summary>两个功能抢到同一个组合键 (自己跟自己冲突, 不必尝试注册)</summary>
    public bool SelfConflict => !SnipOcr.IsEmpty && SnipOcr == SnipImage;

    /// <summary>配置文件路径 (%APPDATA%\SnipasteOcr\hotkeys.txt)</summary>
    public static string FilePath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnipasteOcr");
            return Path.Combine(dir, "hotkeys.txt");
        }
    }

    /// <summary>读取配置; 文件缺失/损坏/编码异常时逐项退回默认值, 不抛异常</summary>
    public static HotkeySettings Load()
    {
        var settings = new HotkeySettings();
        try
        {
            foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                if (line.TrimStart().StartsWith('#')) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;

                string name = line[..eq].Trim();
                string value = line[(eq + 1)..];
                if (name.Equals("snip_ocr", StringComparison.OrdinalIgnoreCase) && Hotkey.TryParse(value, out var ocr))
                    settings.SnipOcr = ocr;
                else if (name.Equals("snip_image", StringComparison.OrdinalIgnoreCase) && Hotkey.TryParse(value, out var image))
                    settings.SnipImage = image;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到就用默认值, 启动不该被配置问题卡住
        }
        return settings;
    }

    /// <summary>写回配置文件 (带注释, 便于手工编辑); 失败由调用方决定如何提示</summary>
    public void Save()
    {
        string path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n',
            "# SnipasteOCR 全局热键配置 (改完重启程序生效, 也可以在托盘菜单 \"快捷键设置\" 里改)",
            "# 修饰键可写 Ctrl / Alt / Shift / Win; 不带修饰键时主键只能是 F1-F24",
            "# 常用主键: 字母 A-Z, 数字 0-9, F1-F24, Num0-Num9, Enter, Space, Esc, Home, End, Insert, Delete, 方向键, ; , . / [ ] ' \\ ` = -",
            $"snip_ocr={SnipOcr.Display}",
            $"snip_image={SnipImage.Display}",
            "") + "\n", new UTF8Encoding(false));
    }
}
