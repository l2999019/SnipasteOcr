using System.Drawing.Drawing2D;
using System.Numerics;
using SnipasteOcr.Native;
using Sdcb.SimdPaddleOCR;

namespace SnipasteOcr;

/// <summary>
/// OCR 结果窗口: 原图 + 识别框叠加显示, 单击选词 / Shift 多选,
/// Ctrl+C 复制选中文字, 支持缩放与全选/全量复制/保存
/// </summary>
public sealed class OcrResultForm : Form
{
    private readonly Bitmap _src;
    private float _scale = 1f;
    private float _minScale;
    private PaddleOcrLine[] _lines = [];
    private readonly HashSet<int> _selected = [];
    private int _hover = -1;
    private CancellationTokenSource? _cts;
    private bool _ocrDone;
    private bool _draggingMove; // 按住空白处拖动移动窗口
    private Point _dragStartScreen; // 按下时的鼠标屏幕坐标
    private Point _dragStartLoc;    // 按下时的窗口位置

    // ===== 字符级拖选 =====
    private bool _charDrag;                              // 正在文本块内拖选
    private bool _charDragMoved;                         // 拖选是否已移动
    private int _dragLine = -1;                          // 拖选所在行
    private int _dragAnchorChar;                         // 拖选起点字符索引
    private Point _dragAnchorClient;                     // 拖选起点 (客户端坐标)
    private (int Line, int Start, int End)? _partial;    // 行内选中范围 [Start, End)
    private float[][] _lineCumW = [];                    // 每行字符累计宽度权重

    private readonly ToolStrip _toolStrip = new();
    private readonly ToolStripButton _zoomOut = new();
    private readonly ToolStripButton _zoomIn = new();
    private readonly ToolStripButton _zoomReset = new();
    private readonly ToolStripLabel _zoomLabel = new();
    private readonly ToolStripButton _selectAll = new();
    private readonly ToolStripButton _copyAll = new();
    private readonly ToolStripButton _copySelection = new();
    private readonly ToolStripButton _saveImage = new();
    private readonly ToolStripButton _colsebtn = new();
    private string _statusText = string.Empty; // 右下角状态提示

    public OcrResultForm(Bitmap source)
    {
        _src = source;
        Text = "SnipasteOCR";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None; // 无标题栏, 边缘细边框可缩放
        BackColor = Color.FromArgb(24, 26, 30);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        BuildToolStrip();
        Controls.Add(_toolStrip);
        // 最小宽度 = 工具栏完整宽度, 小截图时按钮不会被挤掉/折叠进溢出菜单
        MinimumSize = new Size(Math.Max(320, _toolStrip.GetPreferredSize(new Size(0, _toolStrip.Height)).Width + 16), 140);
        // 初始大小: 图片原始尺寸 (1:1 显示) + 工具栏
        Size = SizeFromImage();

        _minScale = ComputeFitScale();
        _scale = _minScale;
        UpdateZoomLabel();
        SetStatus("正在识别\u2026");

        OcrService.Instance.StatusChanged += s =>
        {
            if (IsHandleCreated)
                try { BeginInvoke(() => SetStatus(s)); }
                catch (ObjectDisposedException) { }
        };

        Shown += (_, _) => StartOcr();
    }

    private void BuildToolStrip()
    {
        _toolStrip.GripStyle = ToolStripGripStyle.Hidden;
        _toolStrip.RenderMode = ToolStripRenderMode.Professional;
        _toolStrip.Renderer = new DarkToolStripRenderer();
        _toolStrip.BackColor = Color.FromArgb(38, 41, 47);
        _toolStrip.ForeColor = Color.FromArgb(226, 230, 236);

        _zoomOut.Text = "-";
        _zoomOut.ToolTipText = "缩小 ( - )";
        _zoomOut.Click += (_, _) => Zoom(1f / 1.25f);

        _zoomLabel.Text = "100%";
        _zoomLabel.AutoSize = false;
        _zoomLabel.Width = 52;
        _zoomLabel.TextAlign = ContentAlignment.MiddleCenter;

        _zoomIn.Text = "+";
        _zoomIn.ToolTipText = "放大 ( + )";
        _zoomIn.Click += (_, _) => Zoom(1.25f);

        _zoomReset.Text = "适应";
        _zoomReset.ToolTipText = "适应窗口 ( 0 )";
        _zoomReset.Click += (_, _) => { _scale = _minScale; FitWindowToImage(); UpdateZoomLabel(); Invalidate(); };

        _selectAll.Text = "全选";
        _selectAll.ToolTipText = "选择全部识别结果 ( Ctrl+A )";
        _selectAll.Click += (_, _) => { _selected.Clear(); _partial = null; for (int i = 0; i < _lines.Length; i++) _selected.Add(i); Invalidate(); };

        _copySelection.Text = "复制选中";
        _copySelection.ToolTipText = "复制选中文本 ( Ctrl+C )";
        _copySelection.Click += (_, _) => CopyText(GetSelectedText());

        _copyAll.Text = "复制全部";
        _copyAll.ToolTipText = "复制全部识别文本";
        _copyAll.Click += (_, _) => CopyText(GetAllText());

        _saveImage.Text = "保存图片";
        _saveImage.ToolTipText = "保存截图为 PNG";
        _saveImage.Click += (_, _) => SaveImage();

        _colsebtn.Text = "关闭";
        _colsebtn.ToolTipText = "关闭当前窗口";
        _colsebtn.Click += (_, _) => this.Close();


        _toolStrip.Items.Add(_zoomOut);
        _toolStrip.Items.Add(_zoomLabel);
        _toolStrip.Items.Add(_zoomIn);
        _toolStrip.Items.Add(_zoomReset);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_selectAll);
        _toolStrip.Items.Add(_copySelection);
        _toolStrip.Items.Add(_copyAll);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_saveImage);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_colsebtn);

    }

    private float ComputeFitScale()
    {
        float dpi = DpiFactor();
        float availW = Math.Max(100f, (ClientSize.Width - 4) * dpi);
        float availH = Math.Max(100f, (ClientSize.Height - _toolStrip.Height - 4) * dpi);
        return Math.Min(1f, Math.Min(availW / _src.Width, availH / _src.Height));
    }

    // _src 为物理像素, 客户区/鼠标坐标为逻辑像素, 1:1 显示需按 DPI 换算
    private static float DpiFactor()
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96f;
    }

    // 初始窗口尺寸: 图片 1:1 (逻辑像素) + 工具栏; 超出屏幕工作区时按比例收缩
    private Size SizeFromImage()
    {
        float dpi = DpiFactor();
        // 物理像素图片 1:1 显示, 客户区逻辑尺寸 = 物理尺寸 / dpi
        int w = (int)Math.Round(_src.Width / dpi) + 8;
        int h = (int)Math.Round(_src.Height / dpi) + _toolStrip.Height + 8;
        Rectangle wa = SystemInformation.WorkingArea;
        float s = Math.Min(1f, Math.Min(wa.Width * 0.95f / w, wa.Height * 0.95f / h));
        if (s < 1f)
        {
            w = (int)Math.Round(w * s);
            h = (int)Math.Round(h * s);
        }
        return new Size(Math.Max(w, MinimumSize.Width), Math.Max(h, MinimumSize.Height));
    }

    private void Zoom(float factor)
    {
        _scale = Math.Clamp(_scale * factor, _minScale, 8f);
        FitWindowToImage();
        UpdateZoomLabel();
        Invalidate();
    }

    // 缩放时窗口跟着图片大小联动; 图片比窗口大 (被钳到 fit) 时窗口不变
    // 缩放时窗口跟着图片显示大小联动: 放大窗口变大, 缩小窗口变小
    private void FitWindowToImage()
    {
        float dpi = DpiFactor();
        int newW = Math.Max(MinimumSize.Width, (int)Math.Round(_src.Width * _scale / dpi) + 8);
        int newH = Math.Max(MinimumSize.Height, (int)Math.Round(_src.Height * _scale / dpi) + _toolStrip.Height + 8);
        if (newW == ClientSize.Width && newH == ClientSize.Height)
            return; // 尺寸没变
        var wa = SystemInformation.WorkingArea;
        if (newW > wa.Width || newH > wa.Height)
            return; // 超出屏幕工作区, 窗口不再变大 (图片按窗口居中裁切)
        // 保持左上角不动, 限制在工作区内
        int x = Math.Min(Location.X, wa.Right - newW);
        int y = Math.Min(Location.Y, wa.Bottom - newH);
        if (x < wa.Left) x = wa.Left;
        if (y < wa.Top) y = wa.Top;
        Location = new Point(x, y);
        Size = new Size(newW, newH);
    }

    // 窗口被手动缩放/移动后: 1:1 能放下就自动放大到 1:1 ("联动"的另一半)
    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        float fit = ComputeFitScale();
        _minScale = fit;
        if (_scale < fit)
        {
            _scale = fit;
            UpdateZoomLabel();
        }
        Invalidate();
    }

    private void UpdateZoomLabel()
    {
        _zoomLabel.Text = $"{(int)Math.Round(_scale * 100)}%";
    }

    // ===== 坐标换算 (图片以缩放后尺寸居中绘制) =====

    private RectangleF ImageRect()
    {
        // 返回物理像素坐标 (绘制时直接可用)
        float dpi = DpiFactor();
        float w = _src.Width * _scale;
        float h = _src.Height * _scale;
        float x = (ClientSize.Width * dpi - w) / 2f;
        float y = _toolStrip.Height * dpi + (ClientSize.Height * dpi - _toolStrip.Height * dpi - h) / 2f;
        return new RectangleF(x, y, w, h);
    }

    // 4 点多边形 (原图坐标 -> 客户区坐标)
    private PointF[] BoxToClientPoints(PaddleOcrDetectionBox box)
    {
        var ir = ImageRect();
        float dpi = DpiFactor();
        return new[]
        {
            new PointF(((float)box.X1 * _scale + ir.X) / dpi, ((float)box.Y1 * _scale + ir.Y) / dpi),
            new PointF(((float)box.X2 * _scale + ir.X) / dpi, ((float)box.Y2 * _scale + ir.Y) / dpi),
            new PointF(((float)box.X3 * _scale + ir.X) / dpi, ((float)box.Y3 * _scale + ir.Y) / dpi),
            new PointF(((float)box.X4 * _scale + ir.X) / dpi, ((float)box.Y4 * _scale + ir.Y) / dpi),
        };
    }

    // ===== 绘制 =====

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        // ImageRect 返回物理像素坐标, 绘制需换算回逻辑像素 (DPI 感知)
        float dpi = DpiFactor();
        var ir = ImageRect();
        var cr = new RectangleF(ir.X / dpi, ir.Y / dpi, ir.Width / dpi, ir.Height / dpi);
        using (var br = new SolidBrush(Color.FromArgb(255, 38, 41, 47)))
            g.FillRectangle(br, cr);
        using (var pen = new Pen(Color.FromArgb(255, 70, 74, 82)))
            g.DrawRectangle(pen, cr.X - 0.5f, cr.Y - 0.5f, cr.Width + 1f, cr.Height + 1f);

        if (_scale >= 0.95f)
        {
            g.DrawImage(_src, cr);
        }
        else
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_src, cr);
        }

        if (_ocrDone)
        {
            using var selBrush = new SolidBrush(Color.FromArgb(90, 33, 150, 243));
            using var selPen = new Pen(Color.FromArgb(255, 33, 150, 243), 2f);
            using var hoverPen = new Pen(Color.FromArgb(255, 255, 193, 7), 1.6f) { DashStyle = DashStyle.Dash };
            using var idlePen = new Pen(Color.FromArgb(160, 33, 150, 243), 1.2f);

            for (int i = 0; i < _lines.Length; i++)
            {
                if (string.IsNullOrEmpty(_lines[i].Text))
                    continue;

                var pts = BoxToClientPoints(_lines[i].Box);
                bool selected = _selected.Contains(i);
                bool hover = i == _hover;

                if (selected)
                {
                    g.FillPolygon(selBrush, pts);
                    g.DrawPolygon(selPen, pts);
                    // 部分选中: 再高亮行内的字符范围
                    if (_partial is { } p && p.Line == i)
                    {
                        using var strongBrush = new SolidBrush(Color.FromArgb(160, 33, 150, 243));
                        using var strongPen = new Pen(Color.FromArgb(255, 33, 150, 243), 1.4f);
                        var cp = CharRangePoints(i, p.Start, p.End);
                        g.FillPolygon(strongBrush, cp);
                        g.DrawPolygon(strongPen, cp);
                    }
                }
                else if (hover)
                    g.DrawPolygon(hoverPen, pts);
                else
                    g.DrawPolygon(idlePen, pts);
            }
        }
        DrawStatus(g);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { /* OnPaint 内已填充背景 */ }

    // 右下角状态提示 (半透明底 + 浅色文字)
    private void DrawStatus(Graphics g)
    {
        if (string.IsNullOrEmpty(_statusText))
            return;
        using var font = new Font("Microsoft YaHei UI", 9f);
        Size ts = TextRenderer.MeasureText(_statusText, font);
        int padX = 10, padY = 4;
        int bw = ts.Width + padX * 2, bh = ts.Height + padY * 2;
        int bx = ClientSize.Width - bw - 8;
        int by = ClientSize.Height - bh - 8;
        if (bx < 4) bx = 4;
        using var bg = new SolidBrush(Color.FromArgb(190, 30, 33, 38));
        g.FillRectangle(bg, bx, by, bw, bh);
        TextRenderer.DrawText(g, _statusText, font, new Point(bx + padX, by + padY), Color.FromArgb(226, 230, 236), Color.FromArgb(190, 30, 33, 38), TextFormatFlags.EndEllipsis);
    }

    private void SetStatus(string text)
    {
        if (_statusText == text)
            return;
        _statusText = text;
        Invalidate(); // 右下角状态需重绘
    }

    // ===== 鼠标交互 =====

    private int HitTest(Point clientPt)
    {
        if (!_ocrDone)
            return -1;

        var ir = ImageRect();
        // 逻辑客户区坐标 -> 物理像素 -> 原图坐标
        float dpi = DpiFactor();
        float ix = (clientPt.X * dpi - ir.X) / _scale;
        float iy = (clientPt.Y * dpi - ir.Y) / _scale;

        // 从最后绘制的框开始命中 (后者在上层)
        for (int i = _lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrEmpty(_lines[i].Text))
                continue;
            if (IsInBox(_lines[i].Box, ix, iy))
                return i;
        }
        return -1;
    }

    // 点在 4 边形内 (射线法)
    private static bool IsInBox(PaddleOcrDetectionBox box, float x, float y)
    {
        float[] xs = { box.X1, box.X2, box.X3, box.X4 };
        float[] ys = { box.Y1, box.Y2, box.Y3, box.Y4 };
        bool inside = false;
        for (int i = 0, j = 3; i < 4; j = i++)
        {
            if ((ys[i] > y) != (ys[j] > y))
            {
                float t = (y - ys[j]) / (ys[i] - ys[j]);
                float xi = xs[j] + t * (xs[i] - xs[j]);
                if (x < xi)
                    inside = !inside;
            }
        }
        return inside;
    }

    // ===== 字符定位: 检测框是 4 边形, 按字符宽度权重把字符投影到行内位置 =====

    private static float CharWeight(char c) => c == ' ' || c == '\u3000' ? 0.35f : (c < 256 ? 0.6f : 1.0f);

    private static float[][] BuildCharWeights(PaddleOcrLine[] lines)
    {
        var cum = new float[lines.Length][];
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Text;
            var w = new float[t.Length + 1];
            for (int j = 0; j < t.Length; j++)
                w[j + 1] = w[j] + CharWeight(t[j]);
            cum[i] = w;
        }
        return cum;
    }

    // 点在边 (A->B) 上的投影参数 u (0..1)
    private static float ProjU(float ax, float ay, float bx, float by, float x, float y)
    {
        float dx = bx - ax, dy = by - ay;
        float len2 = dx * dx + dy * dy;
        if (len2 < 1e-6f) return 0f;
        return Math.Clamp(((x - ax) * dx + (y - ay) * dy) / len2, 0f, 1f);
    }

    // 原图坐标 -> 行内字符索引 (0..n, n = 文本长度)
    private int CharIndexAt(int line, float ix, float iy)
    {
        var cum = _lineCumW[line];
        if (cum.Length < 2) return 0;
        var b = _lines[line].Box;
        float u = (ProjU(b.X1, b.Y1, b.X2, b.Y2, ix, iy) + ProjU(b.X4, b.Y4, b.X3, b.Y3, ix, iy)) / 2f;
        if (_lines[line].AppliedRotationDegrees == 180)
            u = 1f - u; // 180 度旋转时文字方向与框顶边相反
        float target = u * cum[cum.Length - 1];
        int idx = 0;
        for (int k = 0; k < cum.Length - 1; k++)
            if (cum[k] <= target) idx = k;
        return idx;
    }

    // 行内字符范围 -> 高亮四边形 (逻辑客户端坐标)
    private PointF[] CharRangePoints(int line, int start, int end)
    {
        var ir = ImageRect();
        float dpi = DpiFactor();
        var b = _lines[line].Box;
        var cum = _lineCumW[line];
        float total = cum[cum.Length - 1];
        float t0 = total > 0 ? cum[start] / total : 0f;
        float t1 = total > 0 ? cum[Math.Min(end, cum.Length - 1)] / total : 1f;
        if (_lines[line].AppliedRotationDegrees == 180)
        {
            (t0, t1) = (1f - t1, 1f - t0);
        }
        float uA = Math.Min(t0, t1), uB = Math.Max(t0, t1);
        // 插值点先算原图坐标再整体乘 _scale (与 BoxToClientPoints 一致); 旧写法只对偏移乘 scale, 非 1:1 时错位
        PointF Top(float u) => new((((float)b.X1 + ((float)b.X2 - (float)b.X1) * u) * _scale + ir.X) / dpi,
                                   (((float)b.Y1 + ((float)b.Y2 - (float)b.Y1) * u) * _scale + ir.Y) / dpi);
        PointF Bot(float u) => new((((float)b.X4 + ((float)b.X3 - (float)b.X4) * u) * _scale + ir.X) / dpi,
                                   (((float)b.Y4 + ((float)b.Y3 - (float)b.Y4) * u) * _scale + ir.Y) / dpi);
        return new[] { Top(uA), Top(uB), Bot(uB), Bot(uA) };
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        int hit = HitTest(e.Location);
        if (hit < 0)
        {
            if ((Control.ModifierKeys & Keys.Shift) == 0)
            {
                _selected.Clear();
                Invalidate();
            }
            // 无边框窗体: 按住空白处拖动可移动窗口
            _draggingMove = true;
            _dragStartScreen = Cursor.Position;
            _dragStartLoc = Location;
            return;
        }

        if ((Control.ModifierKeys & Keys.Shift) != 0)
        {
            if (_selected.Contains(hit)) _selected.Remove(hit);
            else _selected.Add(hit);
        }
        else
        {
            // 进入拖选; 未移动时 (见 OnMouseUp) 视为单击选中整行
            _charDrag = true;
            _charDragMoved = false;
            _dragLine = hit;
            var ir = ImageRect();
            float dpi = DpiFactor();
            _dragAnchorChar = CharIndexAt(hit, (e.X * dpi - ir.X) / _scale, (e.Y * dpi - ir.Y) / _scale);
            _dragAnchorClient = e.Location;
            _selected.Clear();
            _selected.Add(hit); // 整行淡高亮 + 行内字符强高亮
            _partial = null;
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_draggingMove)
        {
            if (e.Button == MouseButtons.Left)
            {
                // 用绝对屏幕坐标计算, 避免窗口移动后客户端坐标基准变化导致抖动
                Location = new Point(_dragStartLoc.X + Cursor.Position.X - _dragStartScreen.X,
                                    _dragStartLoc.Y + Cursor.Position.Y - _dragStartScreen.Y);
                return; // 拖动中不做 hover 检测, 减少重绘
            }
            else
            {
                _draggingMove = false;
                return;
            }
        }
        if (_charDrag)
        {
            if (e.Button == MouseButtons.Left)
            {
                // 拖选: 更新当前行内的字符范围
                if (_dragLine >= 0)
                {
                    if (!IsInBox(_lines[_dragLine].Box, (e.X * DpiFactor() - ImageRect().X) / _scale, (e.Y * DpiFactor() - ImageRect().Y) / _scale))
                    {
                        _charDrag = false; // 拖出文本块结束拖选
                        return;
                    }
                    if (Math.Abs(e.X - _dragAnchorClient.X) + Math.Abs(e.Y - _dragAnchorClient.Y) >= 3)
                        _charDragMoved = true;
                    if (_charDragMoved)
                    {
                        var ir = ImageRect();
                        float dpi = DpiFactor();
                        int cur = CharIndexAt(_dragLine, (e.X * dpi - ir.X) / _scale, (e.Y * dpi - ir.Y) / _scale);
                        string t = _lines[_dragLine].Text;
                        int a = _dragAnchorChar, b = cur;
                        _partial = (_dragLine, Math.Min(a, b), Math.Max(a, b));
                        // 拖到块边缘时, 选区自动贴到行首/行尾
                        var box = _lines[_dragLine].Box;
                        float ix = (e.X * dpi - ir.X) / _scale;
                        if (cur == 0 && ix < Math.Min(Math.Min(box.X1, box.X2), Math.Min(box.X3, box.X4)) - 2f)
                            _partial = (_dragLine, 0, Math.Min(a, t.Length));
                        if (cur == t.Length && ix > Math.Max(Math.Max(box.X1, box.X2), Math.Max(box.X3, box.X4)) + 2f)
                            _partial = (_dragLine, Math.Max(a, 0), t.Length);
                        Invalidate();
                    }
                }
                return;
            }
            _charDrag = false;
            return;
        }
        int hit = HitTest(e.Location);
        if (hit != _hover)
        {
            _hover = hit;
            if (hit >= 0)
            {
                Cursor = Cursors.Hand;
                SetStatus(_lines[hit].Text);
            }
            else
            {
                Cursor = Cursors.Default;
                UpdateIdleStatus();
            }
            Invalidate();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((Control.ModifierKeys & Keys.Control) == 0)
            return;
        Zoom(e.Delta > 0 ? 1.15f : 1f / 1.15f);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
            _draggingMove = false;
        if (_charDrag && e.Button == MouseButtons.Left)
        {
            _charDrag = false;
            if (!_charDragMoved)
            {
                // 未拖动 = 单击: 选中整行
                if (_dragLine >= 0)
                {
                    _selected.Clear();
                    _selected.Add(_dragLine);
                    _partial = null;
                    Invalidate();
                }
                else
                {
                    _selected.Clear();
                    _partial = null;
                    Invalidate();
                }
            }
            _dragLine = -1;
            _charDragMoved = false;
            UpdateIdleStatus();
        }
    }

    // 无边框窗体: 边缘 6 像素内返回对应命中区, 由系统处理缩放并显示缩放光标;
    // 另外在 WM_KEYDOWN 兜底处理 Ctrl+C (手动消息循环下 ProcessCmdKey 可能不生效)
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        const int WM_KEYDOWN = 0x0100;
        if (m.Msg == WM_NCHITTEST && IsHandleCreated)
        {
            const int margin = 6;
            int lp = m.LParam.ToInt32();
            Point pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)(lp >> 16)));
            bool l = pt.X < margin, r = pt.X >= ClientSize.Width - margin;
            bool t = pt.Y < margin, b = pt.Y >= ClientSize.Height - margin;
            if (l && t) { m.Result = (IntPtr)13; return; } // HTTOPLEFT
            if (r && t) { m.Result = (IntPtr)14; return; } // HTTOPRIGHT
            if (l && b) { m.Result = (IntPtr)16; return; } // HTBOTTOMLEFT
            if (r && b) { m.Result = (IntPtr)17; return; } // HTBOTTOMRIGHT
            if (l) { m.Result = (IntPtr)10; return; }     // HTLEFT
            if (r) { m.Result = (IntPtr)11; return; }     // HTRIGHT
            if (t) { m.Result = (IntPtr)12; return; }     // HTTOP
            if (b) { m.Result = (IntPtr)15; return; }     // HTBOTTOM
        }
        else if (m.Msg == WM_KEYDOWN && IsHandleCreated)
        {
            int key = (int)m.WParam.ToInt64();
            bool ctrl = (User32.GetKeyState(0x11) & 0x8000) != 0; // VK_CONTROL 高位 = 当前按下
            if (ctrl && key == 0x43)
            {
                CopyText(GetSelectedText());
                m.Result = (IntPtr)0;
                return;
            }
        }
        base.WndProc(ref m);
    }

    // ===== 键盘 =====

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        const int WM_KEYDOWN = 0x0100;
        if (msg.Msg != WM_KEYDOWN)
            return base.ProcessCmdKey(ref msg, keyData);

        switch (keyData)
        {
            case Keys.Control | Keys.C:
                CopyText(GetSelectedText());
                return true;
            case Keys.Control | Keys.A:
                _selected.Clear();
                _partial = null;
                for (int i = 0; i < _lines.Length; i++) _selected.Add(i);
                Invalidate();
                return true;
            case Keys.Add:
            case Keys.Oemplus:
                Zoom(1.25f);
                return true;
            case Keys.Subtract:
            case Keys.OemMinus:
                Zoom(1f / 1.25f);
                return true;
            case Keys.D0:
            case Keys.NumPad0:
                _scale = _minScale;
                FitWindowToImage();
                UpdateZoomLabel();
                Invalidate();
                return true;
            case Keys.Escape:
                Close();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ===== 文本 =====

    private string GetAllText()
    {
        if (_lines.Length == 0)
            return string.Empty;
        return string.Join("\n", _lines.Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
    }

    private string GetSelectedText()
    {
        if (_selected.Count == 0)
            return _partial is { } p ? _lines[p.Line].Text.Substring(p.Start, p.End - p.Start) : string.Empty;
        var parts = new List<string>();
        foreach (int i in _selected.OrderBy(i => i))
        {
            string t = _lines[i].Text;
            if (_partial is { } p && p.Line == i)
                t = t.Substring(p.Start, p.End - p.Start);
            if (t.Length > 0)
                parts.Add(t);
        }
        return string.Join("\n", parts);
    }

    private bool HasSelection() => _selected.Count > 0 || _partial is not null;

    private int SelectedCharCount()
    {
        int n = 0;
        foreach (int i in _selected)
        {
            if (_partial is { } p && p.Line == i)
                n += p.End - p.Start;
            else
                n += _lines[i].Text.Length;
        }
        if (_selected.Count == 0 && _partial is { } q)
            n += q.End - q.Start;
        return n;
    }

    private void UpdateIdleStatus()
    {
        if (!_ocrDone)
            return;
        if (HasSelection())
            SetStatus($"已选 {SelectedCharCount()} 字符 (Ctrl+C 复制)");
        else
            SetStatus($"{_lines.Length} 个文本块 · 单击/拖选 · Shift 多选 · Ctrl+滚轮缩放");
    }

    private void CopyText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
                SetStatus("没有可复制的文本");
            return;
        }
        try
        {
            Clipboard.SetText(text);
            SetStatus($"已复制 {text.Length} 字符");
        }
        catch
        {
            SetStatus("复制失败 (剪贴板被占用?)");
        }
    }

    private void SaveImage()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "PNG 图片|*.png",
            FileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                _src.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
                SetStatus("图片已保存");
            }
            catch (Exception ex)
            {
                SetStatus("保存失败: " + ex.Message);
            }
        }
    }

    // ===== OCR =====

    private void StartOcr()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var service = OcrService.Instance;
        var img = _src;

        Task.Run(() =>
        {
            try
            {
                var result = service.Recognize(img, token);
                if (token.IsCancellationRequested || !this.IsHandleCreated)
                    return;
                this.BeginInvoke(() =>
                {
                    if (token.IsCancellationRequested)
                        return;
                    _lines = result.Lines;
                    _selected.Clear();
                    _partial = null;
                    _lineCumW = BuildCharWeights(_lines);
                    _hover = -1;
                    _ocrDone = true;
                    _minScale = ComputeFitScale();
                    if (_scale < _minScale) _scale = _minScale;
                    UpdateZoomLabel();
                    UpdateIdleStatus();
                    Invalidate();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!this.IsHandleCreated)
                    return;
                this.BeginInvoke(() =>
                {
                    SetStatus("识别失败: " + ex.Message);
                    _ocrDone = true;
                });
            }
        });
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _src.Dispose();
        base.OnFormClosed(e);
    }
}

/// <summary>
/// 深色工具栏渲染器: 文字浅色, 悬停/按下用蓝色高亮
/// </summary>
internal sealed class DarkToolStripRenderer : ToolStripProfessionalRenderer
{
    public DarkToolStripRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // 统一浅色文字 (含状态标签/缩放比例标签)
        e.TextColor = e.Item.Enabled ? Color.FromArgb(226, 230, 236) : Color.FromArgb(110, 114, 120);
        base.OnRenderItemText(e);
    }
}

internal sealed class DarkColorTable : ProfessionalColorTable
{
    private static readonly Color Bg = Color.FromArgb(38, 41, 47);
    private static readonly Color Hover = Color.FromArgb(50, 54, 62);
    private static readonly Color Active = Color.FromArgb(28, 74, 128);

    public override Color ToolStripGradientBegin => Bg;
    public override Color ToolStripGradientMiddle => Bg;
    public override Color ToolStripGradientEnd => Bg;
    public override Color ToolStripBorder => Color.FromArgb(56, 60, 68);
    public override Color ToolStripDropDownBackground => Bg;
    public override Color ImageMarginGradientBegin => Bg;
    public override Color ImageMarginGradientMiddle => Bg;
    public override Color ImageMarginGradientEnd => Bg;
    public override Color SeparatorDark => Color.FromArgb(60, 64, 72);
    public override Color SeparatorLight => Color.FromArgb(60, 64, 72);
    public override Color ButtonSelectedHighlight => Hover;
    public override Color ButtonSelectedGradientBegin => Hover;
    public override Color ButtonSelectedGradientEnd => Hover;
    public override Color ButtonPressedGradientBegin => Active;
    public override Color ButtonPressedGradientEnd => Active;
    public override Color ButtonSelectedBorder => Color.FromArgb(255, 33, 150, 243);
    public override Color ButtonPressedBorder => Color.FromArgb(255, 33, 150, 243);
}
