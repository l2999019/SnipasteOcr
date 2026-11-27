using SnipasteOcr.Native;

namespace SnipasteOcr;

/// <summary>
/// 快捷键设置对话框: 点中录入框后直接按下新的组合键即可录制,
/// 保存时由 HotkeyManager 重新注册; 冲突则回滚并保持对话框打开。
/// 纯代码布局 (项目不使用窗体设计器), 配色与 OCR 结果窗口一致。
/// </summary>
public sealed class HotkeyDialog : Form
{
    private static readonly string[] RowLabels = ["截图并识别文字", "截图复制到剪贴板"];

    private readonly Func<HotkeySettings, string?> _apply;
    private readonly HotkeySettings _draft;
    private readonly CaptureBox[] _boxes = new CaptureBox[2];
    private readonly Label _note = new();
    private bool _finished;

    private readonly Font _uiFont = new("Microsoft YaHei UI", 9f);
    private readonly Font _titleFont = new("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
    private readonly Font _hintFont = new("Microsoft YaHei UI", 8.5f);

    // 配色常量 (名字避开 Form.Text / Control 的 BackColor 等成员)
    private static readonly Color Bg = Color.FromArgb(38, 41, 47);
    private static readonly Color Fg = Color.FromArgb(226, 230, 236);
    private static readonly Color BoxBack = Color.FromArgb(56, 60, 68);
    private static readonly Color Muted = Color.FromArgb(150, 158, 170);
    private static readonly Color Error = Color.FromArgb(240, 113, 110);
    private static readonly Color Info = Color.FromArgb(120, 190, 255);

    /// <param name="apply">保存回调: 返回 null 表示已生效, 否则返回错误文本</param>
    public HotkeyDialog(HotkeySettings current, Func<HotkeySettings, string?> apply)
    {
        _draft = current.Clone();
        _apply = apply;

        Text = "快捷键设置";
        Font = _uiFont;
        BackColor = Bg;
        ForeColor = Fg;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 250);
        KeyPreview = true;

        BuildLayout();

        KeyDown += (_, e) =>
        {
            // 焦点不在录入框时 (例如刚点过按钮) 也能用回车保存 / Esc 关闭
            if (ActiveControl is CaptureBox) return;
            if (e.KeyCode == Keys.Enter) SaveDraft();
            else if (e.KeyCode == Keys.Escape) Finish(DialogResult.Cancel);
        };
        Shown += (_, _) =>
        {
            User32.SetForegroundWindow(Handle);
            Activate();
            _boxes[0].Focus();
        };
    }

    /// <summary>两行录入框 + 说明 + 错误提示 + 三个按钮</summary>
    private void BuildLayout()
    {
        var controls = new List<Control>
        {
            new Label
            {
                Text = "全局快捷键",
                Font = _titleFont,
                ForeColor = Fg,
                BackColor = Color.Transparent,
                Location = new Point(24, 18),
                AutoSize = true,
            },
            new Label
            {
                Text = "点击输入框后, 直接按下新的组合键即可录制。仅 F1-F24 可以单独使用, 其余按键需搭配 Ctrl / Alt / Shift / Win。",
                Font = _hintFont,
                ForeColor = Muted,
                BackColor = Color.Transparent,
                Location = new Point(24, 132),
                Size = new Size(392, 34),
            },
            new Label
            {
                Text = "录制 Win 组合键时可能弹出开始菜单, 不影响已录制的快捷键。",
                Font = _hintFont,
                ForeColor = Muted,
                BackColor = Color.Transparent,
                Location = new Point(24, 166),
                AutoSize = true,
            },
            _note,
        };

        int[] ys = [58, 94];
        for (int i = 0; i < 2; i++)
        {
            controls.Add(new Label
            {
                Text = RowLabels[i],
                ForeColor = Fg,
                BackColor = Color.Transparent,
                Location = new Point(24, ys[i] + 4),
                Size = new Size(160, 20),
            });

            var box = new CaptureBox(i, OnCaptured, SaveDraft, () => Finish(DialogResult.Cancel))
            {
                Location = new Point(196, ys[i]),
                Size = new Size(150, 28),
            };
            box.Text = (i == 0 ? _draft.SnipOcr : _draft.SnipImage).Display;
            _boxes[i] = box;
            controls.Add(box);
        }

        _note.Font = _hintFont;
        _note.ForeColor = Muted;
        _note.BackColor = Color.Transparent;
        _note.Location = new Point(24, 190);
        _note.Size = new Size(392, 20);
        _note.TextAlign = ContentAlignment.MiddleLeft;

        controls.Add(MakeButton("恢复默认", 24, 214, 92, RestoreDefaults));
        controls.Add(MakeButton("保存", 248, 214, 80, SaveDraft));
        controls.Add(MakeButton("取消", 336, 214, 80, () => Finish(DialogResult.Cancel)));

        Controls.AddRange([.. controls]);
    }

    private Button MakeButton(string text, int x, int y, int width, Action onClick)
    {
        var button = new Button
        {
            Text = text,
            Font = _uiFont,
            Location = new Point(x, y),
            Size = new Size(width, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(60, 64, 72),
            ForeColor = Fg,
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(96, 102, 112);
        button.Click += (_, _) => onClick();
        return button;
    }

    // ===== 录制与校验 =====

    /// <summary>录到新组合键: 先挡掉与另一功能重复/无法注册的组合, 再填入输入框</summary>
    private void OnCaptured(CaptureBox box, Hotkey hk)
    {
        Hotkey other = box.Slot == 0 ? _draft.SnipImage : _draft.SnipOcr;
        if (hk == other)
        {
            SetNote($"这个组合键已经分给了另一个功能 ({other.Display})", true);
            return;
        }
        if (!hk.IsRegisterable)
        {
            SetNote($"单独使用 {hk.Display} 不行, 请搭配 Ctrl / Alt / Shift / Win", true);
            return;
        }

        if (box.Slot == 0) _draft.SnipOcr = hk;
        else _draft.SnipImage = hk;
        box.Text = hk.Display;
        SetNote(string.Empty, false);
    }

    private void RestoreDefaults()
    {
        _draft.SnipOcr = HotkeySettings.DefaultOcr;
        _draft.SnipImage = HotkeySettings.DefaultImage;
        _boxes[0].Text = _draft.SnipOcr.Display;
        _boxes[1].Text = _draft.SnipImage.Display;
        SetNote("已填入默认快捷键, 点击“保存”生效", false);
    }

    /// <summary>保存: 交给 HotkeyManager 重新注册, 成功即关闭, 失败保留对话框让用户改</summary>
    private void SaveDraft()
    {
        string? error = _apply(_draft);
        if (error == null) Finish(DialogResult.OK);
        else SetNote(error, true);
    }

    private void SetNote(string text, bool isError)
    {
        _note.Text = text;
        _note.ForeColor = isError ? Error : Info;
    }

    /// <summary>只允许关闭一次: 回车/Esc/按钮可能重复触发</summary>
    private void Finish(DialogResult result)
    {
        if (_finished) return;
        _finished = true;
        DialogResult = result;
        Close();
    }

    /// <summary>
    /// 快捷键录入框: 屏蔽所有输入, 只把按键组合上报。
    /// 修饰键用 GetKeyState 现场查询 (WinForms 的 e.Control/e.Alt 认不出 Win 键,
    /// 且 Alt 组合下标志位不可靠), 主键取 e.KeyCode。
    /// </summary>
    private sealed class CaptureBox : TextBox
    {
        private readonly Action<CaptureBox, Hotkey> _onCombo;
        private readonly Action _onEnter;
        private readonly Action _onEscape;

        public readonly int Slot;

        public CaptureBox(int slot, Action<CaptureBox, Hotkey> onCombo, Action onEnter, Action onEscape)
        {
            Slot = slot;
            _onCombo = onCombo;
            _onEnter = onEnter;
            _onEscape = onEscape;

            ReadOnly = true;
            Multiline = false;
            BorderStyle = BorderStyle.FixedSingle;
            BackColor = BoxBack;
            ForeColor = Fg;
            TextAlign = HorizontalAlignment.Center;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Tab) return; // 保留 Tab 切换焦点
            e.SuppressKeyPress = true;

            if (e.KeyCode == Keys.Enter)
            {
                _onEnter();
                return;
            }
            if (e.KeyCode == Keys.Escape)
            {
                _onEscape();
                return;
            }

            uint vk = (uint)e.KeyCode;
            if (IsModifierOrLock(vk)) return; // 只按下修饰键: 继续等主键

            uint mods = 0;
            if (IsDown(0x11)) mods |= Hotkey.MOD_CONTROL; // VK_CONTROL
            if (IsDown(0x12)) mods |= Hotkey.MOD_ALT;     // VK_MENU
            if (IsDown(0x10)) mods |= Hotkey.MOD_SHIFT;   // VK_SHIFT
            if (IsDown(0x5B) || IsDown(0x5C)) mods |= Hotkey.MOD_WIN; // VK_LWIN / VK_RWIN

            _onCombo(this, new Hotkey(mods, vk));
        }

        /// <summary>焦点进入时选中现有文本, 看起来就是"等待录入"</summary>
        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            SelectAll();
        }

        private static bool IsDown(int vk) => (User32.GetKeyState(vk) & 0x8000) != 0;

        /// <summary>修饰键/锁定键/PrintScreen 不能当主键</summary>
        private static bool IsModifierOrLock(uint vk) =>
            vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0x14 or 0x90 or 0x91 or 0x2C;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _uiFont.Dispose();
            _titleFont.Dispose();
            _hintFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
