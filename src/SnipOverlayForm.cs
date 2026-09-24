using System.Drawing.Drawing2D;

namespace SnipasteOcr;

/// <summary>
/// 全屏截图覆盖层 (仿 Snipaste): 拖拽框选, 双击/回车确认, Esc/右键取消
/// </summary>
public sealed class SnipOverlayForm : Form
{
    private readonly Bitmap _screen;
    private readonly bool _useOcr;
    private Point _anchor;
    private bool _dragging;
    private Rectangle? _selection; // 客户区坐标

    public SnipOverlayForm(bool useOcr)
    {
        _useOcr = useOcr;

        // 先抓取屏幕 (必须在本窗体显示之前)
        Rectangle vs = SystemInformation.VirtualScreen;
        _screen = new Bitmap(vs.Width, vs.Height);
        using (var g = Graphics.FromImage(_screen))
            g.CopyFromScreen(vs.Left, vs.Top, 0, 0, _screen.Size, CopyPixelOperation.SourceCopy);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        Cursor = Cursors.Cross;
        StartPosition = FormStartPosition.Manual;
        Bounds = vs;
        BackColor = Color.Black;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    private float ScaleFactor => _screen.Width / (float)ClientSize.Width;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImage(_screen, 0, 0, ClientSize.Width, ClientSize.Height);

        if (_selection is { } sel && sel.Width > 2 && sel.Height > 2)
        {
            float sf = ScaleFactor;

            // 选区外半透明遮罩
            using (var dim = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                g.FillRectangle(dim, ClientRectangle);
            // 把选区原样画回 (去除遮罩)
            g.DrawImage(_screen, sel, new RectangleF(sel.X * sf, sel.Y * sf, sel.Width * sf, sel.Height * sf), GraphicsUnit.Pixel);

            // 边框 + 尺寸标签
            using (var pen = new Pen(Color.White, 2f))
                g.DrawRectangle(pen, sel);
            using (var pen2 = new Pen(Color.FromArgb(255, 33, 150, 243), 2f))
                g.DrawRectangle(pen2, new Rectangle(sel.X + 1, sel.Y + 1, Math.Max(0, sel.Width - 2), Math.Max(0, sel.Height - 2)));

            string label = $"{(int)Math.Round(sel.Width * sf)} \u00d7 {(int)Math.Round(sel.Height * sf)}";
            using var font = new Font("Microsoft YaHei UI", 9f);
            Size ts = TextRenderer.MeasureText(label, font);
            float ly = sel.Top - ts.Height - 8;
            if (ly < 0) ly = sel.Top + 4;
            using var bg = new SolidBrush(Color.FromArgb(210, 33, 150, 243));
            g.FillRectangle(bg, sel.Left + 4, ly, ts.Width + 10, ts.Height + 6);
            TextRenderer.DrawText(g, label, font, new Point((int)(sel.Left + 9), (int)(ly + 3)), Color.White);
        }
        else
        {
            // 未框选时底部提示
            using var font = new Font("Microsoft YaHei UI", 10f);
            string hint = _useOcr ? "\u62d6\u62fd\u9009\u62e9\u533a\u57df  \u00b7  \u53cc\u51fb/\u56de\u8f66 \u786e\u8ba4  \u00b7  Esc \u53d6\u6d88" : "\u62d6\u62fd\u9009\u62e9\u533a\u57df  \u00b7  \u53cc\u51fb \u786e\u8ba4\u590d\u5236\u56fe\u7247  \u00b7  Esc \u53d6\u6d88";
            Size ts = TextRenderer.MeasureText(hint, font);
            Point pt = new((ClientSize.Width - ts.Width) / 2, ClientSize.Height - ts.Height - 18);
            using var bg = new SolidBrush(Color.FromArgb(190, 30, 30, 30));
            g.FillRectangle(bg, pt.X - 12, pt.Y - 4, ts.Width + 24, ts.Height + 8);
            TextRenderer.DrawText(g, hint, font, pt, Color.White);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right)
        {
            Close();
            return;
        }
        if (e.Button != MouseButtons.Left)
            return;
        _anchor = e.Location;
        _dragging = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            var rect = RectFromPoints(_anchor, e.Location);
            if (_selection != rect)
            {
                _selection = rect;
                Invalidate();
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging || e.Button != MouseButtons.Left)
            return;
        _dragging = false;

        var rect = RectFromPoints(_anchor, e.Location);
        // 小于 5px 视为单击: 保留上一次选区 (兼容双击确认, 双击第二下不会清空选区)
        if (rect.Width >= 5 && rect.Height >= 5)
            _selection = rect;
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        Confirm();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
            Close();
        else if (e.KeyCode == Keys.Enter)
            Confirm();
    }

    private static Rectangle RectFromPoints(Point a, Point b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        return new Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private void Confirm()
    {
        float sf = ScaleFactor;
        Rectangle physical;
        if (_selection is { } sel && sel.Width > 2 && sel.Height > 2)
        {
            int x = Math.Max(0, (int)(sel.X * sf));
            int y = Math.Max(0, (int)(sel.Y * sf));
            int w = Math.Max(1, (int)Math.Round(sel.Width * sf));
            int h = Math.Max(1, (int)Math.Round(sel.Height * sf));
            w = Math.Min(w, _screen.Width - x);
            h = Math.Min(h, _screen.Height - y);
            physical = new Rectangle(x, y, w, h);
        }
        else
        {
            // 未框选: 取屏幕中央 1/2 区域
            int w = Math.Max(200, _screen.Width / 2);
            int h = Math.Max(150, _screen.Height / 2);
            physical = new Rectangle((_screen.Width - w) / 2, (_screen.Height - h) / 2, w, h);
        }

        var crop = new Bitmap(physical.Width, physical.Height);
        using (var g = Graphics.FromImage(crop))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_screen, new Rectangle(0, 0, physical.Width, physical.Height), physical, GraphicsUnit.Pixel);
        }

        Close();

        if (_useOcr)
        {
            var form = new OcrResultForm(crop);

            form.Show();
        }
        else
        {
            try
            {
                Clipboard.SetImage(crop);
            }
            catch
            {
                // 剪贴板被占用时忽略
            }
            crop.Dispose();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _screen.Dispose();
        base.OnFormClosed(e);
    }
}
