using System.Drawing.Drawing2D;

namespace SnipasteOcr;

/// <summary>
/// 全屏截图覆盖层 (仿 Snipaste): 拖拽框选, 双击/回车确认, Esc/右键取消。
/// 框选后可用画笔/矩形框/箭头标注 (支持自定义颜色, Ctrl+Z 撤销)。
/// </summary>
public sealed class SnipOverlayForm : Form
{
    private readonly Bitmap _screen;
    private readonly bool _useOcr;
    private Point _anchor;
    private bool _dragging;
    private Rectangle? _selection; // 客户区坐标

    // ===== 标注工具 =====

    /// <summary>标注工具类型 (画笔/矩形框/箭头)</summary>
    private enum AnnotKind { Brush, Rect, Arrow }

    /// <summary>一条标注: 画笔存 Points 折线, 矩形/箭头存 Start/End (客户区坐标)</summary>
    private sealed class Annotation
    {
        public readonly AnnotKind Kind;
        public readonly Color Color;
        public readonly List<Point> Points = [];
        public Point Start;
        public Point End;
        public Annotation(AnnotKind kind, Color color, Point start)
        {
            Kind = kind; Color = color; Start = start; End = start;
        }
    }

    private static readonly string[] ToolNames = ["画笔", "矩形框", "箭头"];
    private static readonly string[] ToolGlyphs = ["✎", "▭", "↗"];
    private static readonly Color[] Palette =
    {
        Color.Red, Color.FromArgb(59, 130, 246), Color.FromArgb(34, 197, 94), Color.FromArgb(249, 115, 22),
        Color.Yellow, Color.White, Color.FromArgb(157, 78, 221), Color.FromArgb(30, 33, 38),
    };
    private const int ToolBtnW = 56, ColorBtnSize = 18, UndoBtnW = 48, BarH = 34;
    private const int BarPad = 6, Gap = 4;
    private static readonly (int Kind, int Left)[] ToolLayout =
    {
        (0, 0), (1, ToolBtnW + Gap), (2, 2 * (ToolBtnW + Gap)),
    };
    private static int ColorLeft(int i) => 3 * (ToolBtnW + Gap) + Gap + i * (ColorBtnSize + Gap);
    private static int UndoLeft => 3 * (ToolBtnW + Gap) + Gap + Palette.Length * (ColorBtnSize + Gap) + Gap;
    private static int BarWidth => UndoLeft + UndoBtnW + BarPad;

    private readonly List<Annotation> _annotations = []; // 已完成的标注
    private Annotation? _active;                         // 正在拖动的标注
    private AnnotKind _tool = AnnotKind.Brush;           // 当前工具 (默认画笔)
    private int _colorIdx;                               // 当前颜色 (Palette 下标, 默认红)

    /// <summary>构造覆盖层并在显示前抓取整屏截图; useOcr 决定确认后走 OCR 还是复制图片</summary>
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

    /// <summary>物理像素/逻辑像素比例 (处理多显示器 DPI 缩放)</summary>
    private float ScaleFactor => _screen.Width / (float)ClientSize.Width;

    // ===== 工具条几何 (客户区坐标) =====

    private Rectangle BarRect
    {
        get
        {
            if (_selection is not { } sel) return Rectangle.Empty;
            int x = Math.Clamp(sel.Left, 2, Math.Max(2, ClientSize.Width - BarWidth - 2));
            int y = sel.Top - BarH - 30;
            if (y < 2) y = Math.Min(sel.Bottom + 6, ClientSize.Height - BarH - 2);
            return new Rectangle(x, y, BarWidth, BarH);
        }
    }

    private Rectangle ToolBtnRect(AnnotKind k)
    {
        int left = (int)ToolLayout[(int)k].Left;
        var bar = BarRect;
        return new Rectangle(bar.X + BarPad + left, bar.Y + BarPad, ToolBtnW, BarH - 2 * BarPad);
    }

    private Rectangle ColorRect(int i)
    {
        var bar = BarRect;
        return new Rectangle(bar.X + BarPad + ColorLeft(i), bar.Y + (BarH - ColorBtnSize) / 2, ColorBtnSize, ColorBtnSize);
    }

    private Rectangle UndoRect()
    {
        var bar = BarRect;
        return new Rectangle(bar.X + BarPad + UndoLeft, bar.Y + BarPad, UndoBtnW, BarH - 2 * BarPad);
    }

    /// <summary>客户区点命中的工具条按钮: (0, 工具序号) / (1, 颜色序号) / (2, 0)=撤销; 未命中 (-1, -1)</summary>
    private (int Kind, int Index) ToolbarHit(Point p)
    {
        if (_selection is not { } || !BarRect.Contains(p))
            return (-1, -1);
        for (int i = 2; i >= 0; i--)
            if (ToolBtnRect((AnnotKind)i).Contains(p))
                return (0, i);
        for (int i = Palette.Length - 1; i >= 0; i--)
            if (ColorRect(i).Contains(p))
                return (1, i);
        if (UndoRect().Contains(p))
            return (2, 0);
        return (-1, -1);
    }

    // ===== 绘制 =====

    /// <summary>绘制: 全屏底图 + 选区遮罩/边框/尺寸标签 + 标注 + 工具条; 未框选时底部提示</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawImage(_screen, 0, 0, ClientSize.Width, ClientSize.Height);

        if (_selection is { } sel && sel.Width > 2 && sel.Height > 2)
        {
            float sf = ScaleFactor;

            // 选区外半透明遮罩
            using (var dim = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                g.FillRectangle(dim, ClientRectangle);
            // 把选区原样画回 (去除遮罩)
            g.DrawImage(_screen, sel, new RectangleF(sel.X * sf, sel.Y * sf, sel.Width * sf, sel.Height * sf), GraphicsUnit.Pixel);

            // 标注: 限制在选区内绘制, 先画已完成的, 再画正在拖动的
            g.SetClip(sel);
            foreach (var a in _annotations)
                DrawAnnotation(g, a, sf);
            if (_active is not null)
                DrawAnnotation(g, _active, sf);
            g.ResetClip();

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

            DrawToolbar(g);
        }
        else
        {
            // 未框选时底部提示
            using var font = new Font("Microsoft YaHei UI", 10f);
            string hint = _useOcr
                ? "\u62d6\u62fd\u9009\u62e9\u533a\u57df  \u00b7  \u753b\u7b14/\u65b9\u6846/\u7bad\u5934/\u9a6c\u8d5b\u514b\u6807\u6ce8  \u00b7  \u53cc\u51fb/\u56de\u8f66 \u786e\u8ba4  \u00b7  Esc \u53d6\u6d88"
                : "\u62d6\u62fd\u9009\u62e9\u533a\u57df  \u00b7  \u753b\u7b14/\u65b9\u6846/\u7bad\u5934/\u9a6c\u8d5b\u514b\u6807\u6ce8  \u00b7  \u53cc\u51fb \u786e\u8ba4\u590d\u5236\u56fe\u7247  \u00b7  Esc \u53d6\u6d88";
            Size ts = TextRenderer.MeasureText(hint, font);
            Point pt = new((ClientSize.Width - ts.Width) / 2, ClientSize.Height - ts.Height - 18);
            using var bg = new SolidBrush(Color.FromArgb(190, 30, 30, 30));
            g.FillRectangle(bg, pt.X - 12, pt.Y - 4, ts.Width + 24, ts.Height + 8);
            TextRenderer.DrawText(g, hint, font, pt, Color.White);
        }
    }

    // 工具条: 圆角深色底, 选中工具蓝底, 当前颜色加白圈, 颜色块间竖分隔
    private void DrawToolbar(Graphics g)
    {
        var bar = BarRect;
        using var path = RoundedRect(bar, 8);
        using (var bg = new SolidBrush(Color.FromArgb(235, 30, 33, 38)))
            g.FillPath(bg, path);
        using (var border = new Pen(Color.FromArgb(255, 70, 74, 82), 1f))
            g.DrawPath(border, path);

        using var font = new Font("Microsoft YaHei UI", 9f);
        foreach (var (kind, left) in ToolLayout)
        {
            var r = ToolBtnRect((AnnotKind)kind);
            bool selected = _tool == (AnnotKind)kind;
            using var br = new SolidBrush(selected ? Color.FromArgb(28, 74, 128) : Color.FromArgb(50, 54, 62));
            g.FillPath(br, RoundedRect(r, 5));
            if (selected)
                using (var pen = new Pen(Color.FromArgb(255, 33, 150, 243), 1.4f))
                    g.DrawPath(pen, RoundedRect(r, 5));
            string label = $"{ToolGlyphs[kind]}  {ToolNames[kind]}";
            Size ts = TextRenderer.MeasureText(label, font);
            TextRenderer.DrawText(g, label, font, new Point(r.X + (r.Width - ts.Width) / 2, r.Y + (r.Height - ts.Height) / 2), Color.FromArgb(226, 230, 236));
        }

        // 工具按钮与颜色块之间的竖分隔线
        using (var sep = new Pen(Color.FromArgb(255, 60, 64, 72), 1f))
        {
            int sx = bar.X + BarPad + 3 * (ToolBtnW + Gap) + Gap / 2;
            g.DrawLine(sep, sx, bar.Y + 8, sx, bar.Y + bar.Height - 8);
        }

        for (int i = 0; i < Palette.Length; i++)
        {
            var r = ColorRect(i);
            using (var br = new SolidBrush(Palette[i]))
                g.FillRectangle(br, r);
            if (i == _colorIdx)
                using (var pen = new Pen(Color.White, 2f))
                    g.DrawRectangle(pen, r);
            else
                using (var pen = new Pen(Color.FromArgb(100, 120, 120, 120), 1f))
                    g.DrawRectangle(pen, r);
        }

        var ur = UndoRect();
        using (var br = new SolidBrush(Color.FromArgb(50, 54, 62)))
            g.FillPath(br, RoundedRect(ur, 5));
        string uLabel = "\u21a9 撤销";
        Size uts = TextRenderer.MeasureText(uLabel, font);
        TextRenderer.DrawText(g, uLabel, font, new Point(ur.X + (ur.Width - uts.Width) / 2, ur.Y + (ur.Height - uts.Height) / 2), Color.FromArgb(226, 230, 236));
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ===== 标注绘制 (客户区坐标, 线宽按物理像素换算) =====

    private void DrawAnnotation(Graphics g, Annotation a, float sf)
    {
        switch (a.Kind)
        {
            case AnnotKind.Brush:
                if (a.Points.Count >= 2)
                {
                    using var pen = new Pen(a.Color, 3f / sf)
                    { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawLines(pen, a.Points.ToArray());
                }
                break;
            case AnnotKind.Rect:
            {
                var r = RectFromPoints(a.Start, a.End);
                using var pen = new Pen(a.Color, 3f / sf);
                g.DrawRectangle(pen, r);
                break;
            }
            case AnnotKind.Arrow:
                DrawArrow(g, a.Start, a.End, a.Color, 3f / sf, 14f / sf);
                break;
        }
    }

    // 箭头: 主线缩短 0.6L 避免穿出箭头, 箭头为 30 度张角等腰三角形
    private static void DrawArrow(Graphics g, Point a, Point b, Color color, float width, float headLen)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        float len = (float)Math.Sqrt(dx * dx + dy * dy);
        if (len < 1f) return;
        float ux = dx / len, uy = dy / len;
        float L = Math.Min(headLen, len * 0.6f);
        float ca = (float)Math.Cos(Math.PI / 6), sa = (float)Math.Sin(Math.PI / 6);
        var tip = new PointF(b.X, b.Y);
        var lineEnd = new PointF(b.X - ux * L, b.Y - uy * L);
        var h1 = new PointF(b.X - (ux * ca - uy * sa) * L, b.Y - (uy * ca + ux * sa) * L);
        var h2 = new PointF(b.X - (ux * ca + uy * sa) * L, b.Y - (uy * ca - ux * sa) * L);
        using (var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(pen, a, lineEnd);
        using var path = new GraphicsPath();
        path.AddPolygon(new[] { tip, h1, h2 });
        using (var brush = new SolidBrush(color))
            g.FillPath(brush, path);
    }

    // 撤销: 优先丢弃当前笔, 否则移除最后一笔
    private void Undo()
    {
        if (_active is not null)
            _active = null;
        else if (_annotations.Count > 0)
            _annotations.RemoveAt(_annotations.Count - 1);
        Invalidate();
    }

    // ===== 鼠标交互 =====

    /// <summary>右键: 标注拖选中取消该笔, 否则退出截图; 左键: 工具条命中切换, 否则开始框选/标注</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right)
        {
            if (_active is not null)
            {
                _active = null;
                Invalidate();
            }
            else
                Close();
            return;
        }
        if (e.Button != MouseButtons.Left)
            return;

        // 工具条命中优先: 切换工具/颜色/撤销
        if (_selection is { } sel && sel.Width > 2 && sel.Height > 2)
        {
            var hit = ToolbarHit(e.Location);
            if (hit.Kind == 0) { _tool = (AnnotKind)hit.Index; Invalidate(); return; }
            if (hit.Kind == 1) { _colorIdx = hit.Index; Invalidate(); return; }
            if (hit.Kind == 2) { Undo(); return; }
        }

        _anchor = e.Location;
        _dragging = true;
        // 已有选区时左键拖动直接画标注 (不再新建选区); 否则开始框选
        if (_selection is { } s && s.Width > 2 && s.Height > 2)
        {
            _active = new Annotation(_tool, Palette[_colorIdx], e.Location);
            if (_tool == AnnotKind.Brush)
                _active.Points.Add(e.Location);
        }
    }

    /// <summary>拖动中: 更新标注 (画笔采样折线点, 矩形/箭头更新终点) 或更新选区</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
            return;
        if (_active is not null)
        {
            if (_tool == AnnotKind.Brush)
            {
                var last = _active.Points[^1];
                // 距离 >=2px 才采样, 避免密集冗余点
                if ((last.X - e.X) * (last.X - e.X) + (last.Y - e.Y) * (last.Y - e.Y) >= 4)
                    _active.Points.Add(e.Location);
                _active.End = e.Location;
            }
            else
            {
                _active.End = e.Location;
            }
            Invalidate();
            return;
        }
        var rect = RectFromPoints(_anchor, e.Location);
        // 与 OnMouseUp 同一 5px 阈值: 点击时鼠标常漂移 1~3px,
        // 微小矩形不能覆盖已有选区 (否则单击会把选区"抹掉", 之后双击确认时选区消失)
        if (rect.Width >= 5 && rect.Height >= 5 && _selection != rect)
        {
            _selection = rect;
            Invalidate();
        }
    }

    /// <summary>结束拖动: 提交标注 (太小的丢弃) 或结束框选; 小于 5px 视为单击保留上次选区</summary>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging || e.Button != MouseButtons.Left)
            return;
        _dragging = false;

        if (_active is not null)
        {
            var a = _active;
            _active = null;
            bool keep = a.Kind switch
            {
                AnnotKind.Brush => a.Points.Count >= 2,
                _ => a.Start != a.End,
            };
            if (keep)
                _annotations.Add(a);
            Invalidate();
            return;
        }

        var rect = RectFromPoints(_anchor, e.Location);
        // 小于 5px 视为单击: 保留上一次选区 (兼容双击确认, 双击第二下不会清空选区)
        if (rect.Width >= 5 && rect.Height >= 5)
            _selection = rect;
        Invalidate();
    }

    /// <summary>双击确认选区 (标注拖选中忽略, 防止误确认)</summary>
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        // 注意: 双击消息在第二次 MouseUp 之前到达, 此时 OnMouseDown 已为第二次按下
        // 创建了一条 1 点 _active, 不能因此拦截确认 (真正的拖选不会产生双击消息)
        // 双击落在工具条上 (例如连点撤销) 时只算按钮操作, 不能触发确认
        if (_selection is { } sel && sel.Width > 2 && sel.Height > 2)
        {
            var hit = ToolbarHit(e.Location);
            if (hit.Kind is 0 or 1 or 2)
                return;
        }
        Confirm();
    }

    /// <summary>Esc 取消, 回车确认, Ctrl+Z 撤销上一笔标注</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
            Close();
        else if (e.KeyCode == Keys.Enter)
            Confirm();
        else if (e.Control && e.KeyCode == Keys.Z)
            Undo();
    }

    /// <summary>两点归一化为左上角 + 宽高的矩形</summary>
    private static Rectangle RectFromPoints(Point a, Point b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        return new Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    /// <summary>
    /// 确认选区: 客户区坐标换算为物理像素后裁剪原图并烘焙标注;
    /// OCR 模式打开结果窗口, 否则图片写入剪贴板; 未框选时默认取屏幕中央 1/2 区域
    /// </summary>
    private void Confirm()
    {
        _active = null;
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
        // 最近邻插值裁剪, 保持像素精确 (不做缩放失真)
        using (var g = Graphics.FromImage(crop))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_screen, new Rectangle(0, 0, physical.Width, physical.Height), physical, GraphicsUnit.Pixel);
        }
        BakeAnnotations(crop, physical);

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

    // 把标注烘焙进裁剪图: 客户区坐标 -> 物理像素并减去裁剪原点
    private void BakeAnnotations(Bitmap crop, Rectangle physical)
    {
        if (_annotations.Count == 0) return;
        float sf = ScaleFactor;
        using var g = Graphics.FromImage(crop);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var a in _annotations)
        {
            switch (a.Kind)
            {
                case AnnotKind.Brush:
                    if (a.Points.Count >= 2)
                    {
                        var pts = a.Points
                            .Select(p => new PointF((float)(p.X * sf - physical.X), (float)(p.Y * sf - physical.Y)))
                            .ToArray();
                        using var pen = new Pen(a.Color, 3f)
                        { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
                        g.DrawLines(pen, pts);
                    }
                    break;
                case AnnotKind.Rect:
                {
                    var r = RectFromPoints(a.Start, a.End);
                    var cr = new RectangleF(
                        (float)(r.X * sf - physical.X), (float)(r.Y * sf - physical.Y),
                        (float)r.Width * sf, (float)r.Height * sf);
                    using var pen = new Pen(a.Color, 3f);
                    g.DrawRectangle(pen, cr);
                    break;
                }
                case AnnotKind.Arrow:
                {
                    var a1 = new PointF((float)(a.Start.X * sf - physical.X), (float)(a.Start.Y * sf - physical.Y));
                    var a2 = new PointF((float)(a.End.X * sf - physical.X), (float)(a.End.Y * sf - physical.Y));
                    var ia1 = new Point((int)a1.X, (int)a1.Y);
                    var ia2 = new Point((int)a2.X, (int)a2.Y);
                    DrawArrow(g, ia1, ia2, a.Color, 3f, 14f);
                    break;
                }
            }
        }
    }

    /// <summary>释放全屏位图</summary>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _screen.Dispose();
        base.OnFormClosed(e);
    }
}
