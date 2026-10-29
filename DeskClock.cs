// 桌面时钟组件 DeskClock —— 无边框、逐像素透明的桌面时间/日期挂件
// 编译: csc /target:winexe /out:DeskClock.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll DeskClock.cs
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class DeskClock
{
    // ---------------- 设置 ----------------
    static bool showSeconds = true;
    static bool showDate = true;
    static bool hour24 = true;
    static bool locked = false;
    static int zOrder = 0;            // 0 桌面底层（常显、被其它窗口遮挡） 1 普通窗口 2 置顶显示
    static bool BottomMode { get { return zOrder == 0; } }
    static readonly string[] ZORDER_NAMES = new string[] { "桌面底层", "普通窗口", "置顶显示" };
    static int bgKind = 0;            // 0深色玻璃 1羽化玻璃 2取色自适应 3磨砂玻璃 4纯文字阴影 5纯文字描边
    static int bgOpacity = 1;         // 0=80% 1=100% 2=125% 3=150%
    static Bitmap srcBlur;            // 磨砂玻璃底图（抓屏模糊）
    static Color srcAvg = Color.FromArgb(74, 78, 88);   // 取色自适应的背景平均色
    static bool srcOk = false;
    static int capW = 0, capH = 0;
    static int sizeIdx = 2;           // 对应 SIZES
    static int posX = int.MinValue;
    static int posY = int.MinValue;
    static float scale = 1f;

    static readonly float[] SIZES = new float[] { 44f, 56f, 72f, 92f, 116f };
    static readonly string[] SIZE_NAMES = new string[] { "小", "中", "大", "特大", "超大" };
    static readonly string[] BG_NAMES = new string[] { "深色玻璃", "羽化玻璃", "取色自适应", "磨砂玻璃", "纯文字·阴影", "纯文字·描边" };
    static readonly float[] OPACITY_MUL = new float[] { 0.80f, 1.00f, 1.25f, 1.50f };
    static readonly string[] OPACITY_NAMES = new string[] { "80%", "100%", "125%", "150%" };

    // ---------------- 运行时 ----------------
    static WidgetForm form;
    static System.Windows.Forms.Timer timer;
    static ContextMenuStrip menu;
    static ToolStripMenuItem[] bgItems;
    static ToolStripMenuItem[] opacityItems;
    static ToolStripMenuItem[] sizeItems;
    static ToolStripMenuItem[] zItems;
    static string settingsPath;
    static string startupLnk;
    static string renderKey = "";
    static bool dragging = false;
    static bool lastPushOk = false;
    static int lastPushErr = 0;
    static bool shown = true;
    static bool zTest = false;
    static int zTick = 0;
    static int zTickP = 0;
    static bool zDirty = false;
    static bool sinking = false;
    static Point dragCursor;
    static int dragX, dragY;
    static IntPtr memDc = IntPtr.Zero, hbmp = IntPtr.Zero, bits = IntPtr.Zero;
    static int surfW = 0, surfH = 0;
    static Mutex mutex;
    static Bitmap measureBmp;
    static Graphics measureG;
    static string[] installedFonts;
    static readonly StringFormat typo = new StringFormat(StringFormat.GenericTypographic);

    [STAThread]
    static void Main(string[] args)
    {
        bool preview = false, selfTest = false, visTest = false, menuShot = false;
        int forceBg = -1;
        string wall = null, outDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--preview") preview = true;
            else if (args[i] == "--selftest") selfTest = true;
            else if (args[i] == "--vistest") visTest = true;
            else if (args[i] == "--menushot") menuShot = true;
            else if (args[i] == "--ztest") zTest = true;
            else if (args[i] == "--bg" && i + 1 < args.Length) { int nb; if (int.TryParse(args[i + 1], out nb) && nb >= 0 && nb < 6) { forceBg = nb; i++; } }
            else if (args[i] == "--wall" && i + 1 < args.Length) wall = args[++i];
            else if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
        }

        bool created;
        mutex = new Mutex(true, "DeskClockWidget_SingleInstance", out created);
        if (!created && !preview) return;   // 已有实例

        TryDpiAware();
        scale = GetDpiForSystem() / 96f;
        if (scale <= 0.1f || scale > 4f) scale = 1f;

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        settingsPath = Path.Combine(Path.Combine(appData, "DeskClock"), "settings.ini");
        startupLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "桌面时钟.lnk");
        Load();
        if (forceBg >= 0) bgKind = forceBg;
        if (zTest) { zOrder = 0; zTick = 0; }

        measureBmp = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        measureG = Graphics.FromImage(measureBmp);
        measureG.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        CacheFonts();

        if (preview) { MakePreviews(wall, outDir); return; }
        if (selfTest) { SelfTest(); return; }
        if (visTest) { VisTest(); return; }
        if (menuShot) { MakeMenuShot(); return; }

        form = new WidgetForm();
        form.FormBorderStyle = FormBorderStyle.None;
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.TopMost = (zOrder == 2);
        form.Text = "桌面时钟";
        form.Size = new Size(8, 8);
        form.Show();
        ApplyZOrder();

        BuildMenu();
        form.ContextMenuStrip = menu;
        form.MouseDown += delegate(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && !locked)
            {
                dragging = true;
                dragCursor = Cursor.Position;
                dragX = posX; dragY = posY;
            }
        };
        form.MouseMove += delegate(object s, MouseEventArgs e)
        {
            if (!dragging) return;
            Point p = Cursor.Position;
            posX = dragX + (p.X - dragCursor.X);
            posY = dragY + (p.Y - dragCursor.Y);
            ClampToScreen(surfW, surfH, false);
            Push(surfW, surfH);
        };
        form.MouseUp += delegate(object s, MouseEventArgs e)
        {
            if (dragging) { dragging = false; RefreshCapture(true, true); Save(); }
        };
        form.MouseWheel += delegate(object s, MouseEventArgs e)
        {
            if (e.Delta > 0) sizeIdx = Math.Min(SIZES.Length - 1, sizeIdx + 1);
            else sizeIdx = Math.Max(0, sizeIdx - 1);
            SyncChecks();
            Dirty();
        };
        form.FormClosing += delegate(object s, FormClosingEventArgs e)
        {
            Save();
            ReleaseSurface();
        };

        timer = new System.Windows.Forms.Timer();
        timer.Interval = 200;
        timer.Tick += delegate(object s, EventArgs e) { OnTick(); };
        timer.Start();

        Render();
        RefreshCapture(true, true);
        Application.Run(form);
    }

    // ---------------- 主循环 ----------------
    // ---------------- z 序自检 ----------------
    static string TopWindowClass(IntPtr h)
    {
        StringBuilder c = new StringBuilder(200);
        GetClassName(h, c, c.Capacity);
        return c.ToString();
    }

    static void DumpZ(StringBuilder sb, string title)
    {
        sb.AppendLine("--- " + title + " ---");
        IntPtr cur = GetTopWindow(IntPtr.Zero);
        int i = 0, mine = -1, shellIdx = -1;
        string last = "", aboveMe = "";
        while (cur != IntPtr.Zero && i < 400)
        {
            string cn = TopWindowClass(cur);
            if (cur == form.Handle) { mine = i; aboveMe = last; }
            if ((cn == "Progman" || cn == "WorkerW") && shellIdx < 0) shellIdx = i;

            if (i <= 5) sb.AppendLine("   z" + i + " : " + cn + (cur == form.Handle ? "   <== 我们的时钟" : ""));
            last = cn;
            cur = GetWindow(cur, GW_HWNDNEXT);
            i++;
        }
        sb.AppendLine("   时钟 z 序 = " + mine + "（0 = 最前）");
        sb.AppendLine("   紧邻上方窗口 = " + (aboveMe == "" ? "(已在最前)" : aboveMe));
        sb.AppendLine("   最靠前桌面窗口 z 序 = " + shellIdx);
        sb.AppendLine("   时钟在桌面层之上(不会被壁纸盖住) = " + (mine >= 0 && shellIdx >= 0 && mine < shellIdx));
        sb.AppendLine();
    }

    static void WriteZTest()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("结果=成功");
        sb.AppendLine("显示层级=" + ZORDER_NAMES[zOrder] + " (zOrder=" + zOrder + ")");
        sb.AppendLine("窗口可见=" + IsWindowVisible(form.Handle));
        sb.AppendLine("扩展样式=0x" + GetWindowLong(form.Handle, GWL_EXSTYLE).ToString("X8"));
        { IntPtr ai; IntPtr sh = FindFrontShellWindow(out ai);
          sb.AppendLine("最靠前的桌面窗口 = " + (sh == IntPtr.Zero ? "(未找到 -> 沉底兜底)" : TopWindowClass(sh)));
          sb.AppendLine("插入目标(它上方那一格) = " + (ai == IntPtr.Zero ? "(无 -> 沉底兜底)" : TopWindowClass(ai))); }
        DumpZ(sb, "初始（桌面上）");
        Form other = new Form();
        other.Text = "ztest-other";
        other.StartPosition = FormStartPosition.Manual;
        other.Location = new Point(100, 100);
        other.Size = new Size(420, 300);
        other.Show();
        other.Activate();
        Application.DoEvents();
        System.Threading.Thread.Sleep(400);
        Application.DoEvents();
        DumpZ(sb, "打开并激活另一个普通窗口后");
        try { form.Activate(); } catch { }
        Application.DoEvents();
        System.Threading.Thread.Sleep(300);
        Application.DoEvents();
        DumpZ(sb, "尝试激活时钟自身后（应仍在最底）");
        other.Close();
        try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "ztest.txt"), sb.ToString(), Encoding.UTF8); } catch { }
    }

    static void OnTick()
    {
        ApplyVisibility();
        if (zTest && ++zTick == 10) { WriteZTest(); Environment.Exit(0); }
        if (BottomMode && (zDirty || (++zTickP % 5) == 0)) AssertBottom();
        if (!shown) return;
        DateTime now = DateTime.Now;
        string key = (showSeconds ? now.ToString("HH:mm:ss") : now.ToString("HH:mm"))
            + "|" + (showDate ? 1 : 0) + "|" + (hour24 ? 1 : 0)
            + "|" + bgKind + "|" + bgOpacity + "|" + sizeIdx + "|" + scale.ToString("0.###");
        if (key != renderKey) { renderKey = key; Render(); }
    }

    static void Dirty() { renderKey = ""; RefreshCapture(true, false); Render(); }

    static void Render()
    {
        if (form == null) return;
        float baseS = SIZES[sizeIdx] * scale;
        Layout L = Build(DateTime.Now, baseS);
        try
        {
            EnsureSurface(L.W, L.H);
            using (Graphics g = Graphics.FromHdc(memDc))
            {
                g.Clear(Color.FromArgb(0, 0, 0, 0));
                DrawLayout(g, L, baseS, 0f, 0f);
            }
            ClampToScreen(L.W, L.H, posX == int.MinValue);
            Push(L.W, L.H);
        }
        finally { L.Dispose(); }
    }

    // ---------------- 只在桌面获得焦点时显示 ----------------
    static bool IsDesktopClass(string cls)
    {
        if (cls == "Progman" || cls == "WorkerW" || cls == "SysListView32" || cls == "SHELLDLL_DefView") return true;
        return false;
    }

    static bool IsDesktopForeground()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return true;                       // 无法判断时保持显示
        if (form != null && fg == form.Handle) return true;
        uint pid = 0;
        GetWindowThreadProcessId(fg, out pid);
        if (pid != 0 && pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return true;   // 组件自己的窗口(含右键菜单)
        StringBuilder sb = new StringBuilder(120);
        GetClassName(fg, sb, sb.Capacity);
        return IsDesktopClass(sb.ToString());
    }

    static void PushToBottom()
    {
        if (form == null) return;
        SetWindowPos(form.Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // 找 z 序最靠前的“桌面类”窗口（Progman / WorkerW），并回传它紧上方的那一格
    // 把窗口摆到那一格 = 压在壁纸和图标之上、又被所有应用窗口压住
    static IntPtr FindFrontShellWindow(out IntPtr aboveIt)
    {
        aboveIt = IntPtr.Zero;
        if (form == null) return IntPtr.Zero;
        IntPtr cur = GetTopWindow(IntPtr.Zero);
        IntPtr prev = IntPtr.Zero;
        int guard = 0;
        while (cur != IntPtr.Zero && guard++ < 600)
        {
            string cn = TopWindowClass(cur);
            if ((cn == "Progman" || cn == "WorkerW") && cur != form.Handle)
            {
                aboveIt = prev;
                return cur;
            }
            prev = cur;
            cur = GetWindow(cur, GW_HWNDNEXT);
        }
        return IntPtr.Zero;
    }

    static void SinkToDesktopLayer()
    {
        if (form == null) return;
        IntPtr aboveIt;
        IntPtr shell = FindFrontShellWindow(out aboveIt);
        if (shell == IntPtr.Zero || aboveIt == IntPtr.Zero) { PushToBottom(); return; }   // 兜底
        SetWindowPos(form.Handle, aboveIt, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    static void AssertBottom()
    {
        sinking = true;
        zDirty = false;
        SinkToDesktopLayer();
        sinking = false;
        zDirty = false;
    }

    static void ApplyZOrder()
    {
        if (form == null) return;
        form.TopMost = (zOrder == 2);
        if (zOrder == 2) SetWindowPos(form.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        else if (BottomMode) AssertBottom();
        renderKey = "";
    }

    static void ApplyVisibility()
    {
        if (form == null || shown) return;   // 正常一直显示；万一被谁藏了就拉回来
        shown = true;
        ShowWindow(form.Handle, SW_SHOWNOACTIVATE);
        if (zOrder == 2) SetWindowPos(form.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        else if (BottomMode) AssertBottom();
        renderKey = "";
        Render();
    }

    // ---------------- 背景绘制（六种可选） ----------------
    static bool IsCardMode() { return bgKind <= 3; }

    static void PaintCard(Graphics g, RectangleF card, float ox, float oy, float sh)
    {
        float mul = OPACITY_MUL[bgOpacity];
        int A = (int)Math.Min(255f, 150f * mul);
        float rad = Math.Min(card.Height * 0.24f, 28f * scale);
        using (GraphicsPath p = new GraphicsPath())
        {
            AddRound(p, card, rad);
            if (bgKind == 0 || ((bgKind == 2 || bgKind == 3) && !srcOk))
            {
                using (LinearGradientBrush b = new LinearGradientBrush(
                    new RectangleF(card.X, card.Y - 1, card.Width, card.Height + 2),
                    Color.FromArgb(A, 38, 43, 54), Color.FromArgb(A, 11, 13, 18), 90f))
                    g.FillPath(b, p);
                using (Pen pen = new Pen(Color.FromArgb((int)Math.Min(255f, 56f * mul), 255, 255, 255), Math.Max(1f, 1.2f * scale)))
                    g.DrawPath(pen, p);
            }
            else if (bgKind == 1)
            {
                int layerA = (int)Math.Max(3f, 13f * mul);
                float step = Math.Max(0.5f, sh / 13f);
                for (int i = 0; i < 14; i++)
                {
                    float inset = (13 - i) * step;
                    RectangleF rr = card;
                    rr.Inflate(-inset, -inset);
                    if (rr.Width < 6f || rr.Height < 6f) continue;
                    using (GraphicsPath p2 = new GraphicsPath())
                    {
                        AddRound(p2, rr, rad);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(layerA, 14, 16, 22))) g.FillPath(b, p2);
                    }
                }
            }
            else if (bgKind == 2)
            {
                int lo = (int)((srcAvg.R + srcAvg.G + srcAvg.B) * 0.1176f);
                Color c1 = Color.FromArgb(A, Math.Min(255, srcAvg.R / 3 + lo / 3),
                                              Math.Min(255, srcAvg.G / 3 + lo / 3),
                                              Math.Min(255, srcAvg.B / 3 + lo / 3));
                Color c2 = Color.FromArgb(A, Math.Max(0, c1.R - 22), Math.Max(0, c1.G - 22), Math.Max(0, c1.B - 22));
                using (LinearGradientBrush b = new LinearGradientBrush(
                    new RectangleF(card.X, card.Y - 1, card.Width, card.Height + 2), c1, c2, 90f))
                    g.FillPath(b, p);
                using (Pen pen = new Pen(Color.FromArgb((int)Math.Min(255f, 46f * mul), 255, 255, 255), Math.Max(1f, 1.1f * scale)))
                    g.DrawPath(pen, p);
            }
            else
            {
                using (TextureBrush tb = new TextureBrush(srcBlur))
                {
                    tb.TranslateTransform(ox, oy);
                    g.FillPath(tb, p);
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)Math.Min(255f, 64f * mul), 12, 14, 20)))
                    g.FillPath(b, p);
                using (Pen pen = new Pen(Color.FromArgb((int)Math.Min(255f, 64f * mul), 255, 255, 255), Math.Max(1f, 1.2f * scale)))
                    g.DrawPath(pen, p);
            }
        }
    }

    // ---------------- 抓屏：取色 / 磨砂底图 ----------------
    static Bitmap CaptureScreen(int x, int y, int w, int h)
    {
        if (w < 2 || h < 2) return null;
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        BITMAPINFO bmi = new BITMAPINFO();
        bmi.h.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
        bmi.h.biWidth = w; bmi.h.biHeight = -h; bmi.h.biPlanes = 1; bmi.h.biBitCount = 32; bmi.h.biCompression = 0;
        IntPtr bits2;
        IntPtr hb = CreateDIBSection(screen, ref bmi, 0, out bits2, IntPtr.Zero, 0);
        IntPtr old = SelectObject(mem, hb);
        bool ok = BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020);
        Bitmap bmp = null;
        if (ok)
        {
            int len = w * h * 4;
            byte[] buf = new byte[len];
            Marshal.Copy(bits2, buf, 0, len);
            for (int i = 3; i < len; i += 4) buf[i] = 255;
            bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(buf, 0, bd.Scan0, len);
            bmp.UnlockBits(bd);
        }
        SelectObject(mem, old);
        DeleteObject(hb);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
        return bmp;
    }

    static Color AverageColor(Bitmap src, Rectangle r)
    {
        using (Bitmap small = new Bitmap(8, 8, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, new Rectangle(0, 0, 8, 8), r, GraphicsUnit.Pixel);
            }
            int rr = 0, gg = 0, bb = 0;
            for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
            {
                Color c = small.GetPixel(x, y);
                rr += c.R; gg += c.G; bb += c.B;
            }
            return Color.FromArgb(rr / 64, gg / 64, bb / 64);
        }
    }

    static Bitmap BlurBitmap(Bitmap src, int factor)
    {
        Bitmap cur = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(cur)) g.DrawImage(src, new Rectangle(0, 0, cur.Width, cur.Height));
        for (int pass = 0; pass < 2; pass++)
        {
            int sw = Math.Max(1, cur.Width / factor), shh = Math.Max(1, cur.Height / factor);
            Bitmap small = new Bitmap(sw, shh, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(cur, new Rectangle(0, 0, sw, shh));
            }
            Bitmap back = new Bitmap(cur.Width, cur.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(back))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(small, new Rectangle(0, 0, back.Width, back.Height));
            }
            cur.Dispose(); small.Dispose();
            cur = back;
        }
        return cur;
    }

    static Bitmap ExtractBlur(Bitmap src, Rectangle r, int factor)
    {
        Bitmap crop = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(crop))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
        }
        Bitmap b = BlurBitmap(crop, factor);
        crop.Dispose();
        return b;
    }

    static void DisposeCapture()
    {
        if (srcBlur != null) { srcBlur.Dispose(); srcBlur = null; }
    }

    static void PrepareCapture()
    {
        if (surfW < 2 || surfH < 2 || posX == int.MinValue) { srcOk = false; capW = 0; capH = 0; return; }
        Bitmap shot = null;
        try { shot = CaptureScreen(posX, posY, surfW, surfH); }
        catch { shot = null; }
        if (shot == null) { srcOk = false; capW = 0; capH = 0; return; }
        srcAvg = AverageColor(shot, new Rectangle(0, 0, shot.Width, shot.Height));
        Bitmap bl = BlurBitmap(shot, 12);
        shot.Dispose();
        DisposeCapture();
        srcBlur = bl;
        srcOk = true;
        capW = surfW; capH = surfH;
    }

    static void RefreshCapture(bool allowHide, bool force)
    {
        if (form == null) return;
        if (!(bgKind == 2 || bgKind == 3)) { DisposeCapture(); srcOk = false; return; }
        if (!force && srcBlur != null && capW == surfW && capH == surfH) return;
        bool was = shown;
        if (allowHide && was) ShowWindow(form.Handle, SW_HIDE);
        PrepareCapture();
        if (allowHide && was)
        {
            ShowWindow(form.Handle, SW_SHOWNOACTIVATE);
            if (zOrder == 2) SetWindowPos(form.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            else if (BottomMode) AssertBottom();
        }
    }

    // ---------------- 版面计算 ----------------
    class Layout : IDisposable
    {
        public Font fTime, fSec, fLabel;
        public string t, s, am, date;
        public SizeF st, ss, sa, sd;
        public float rowH, gapSec, gapAm, gapY, padX, padY, sh, contentW, contentH;
        public int W, H;
        public void Dispose()
        {
            if (fTime != null) fTime.Dispose();
            if (fSec != null) fSec.Dispose();
            if (fLabel != null) fLabel.Dispose();
        }
    }

    static Layout Build(DateTime now, float baseS)
    {
        Layout L = new Layout();
        L.fTime = PickFont(baseS, true);
        L.fSec = PickFont(baseS * 0.46f, true);
        L.fLabel = PickFont(baseS * 0.33f, false);
        L.t = hour24 ? now.ToString("HH:mm") : now.ToString("h:mm");
        L.s = showSeconds ? now.ToString("ss") : "";
        DateTimeFormatInfo dfi = CultureInfo.CurrentCulture.DateTimeFormat;
        L.am = hour24 ? "" : (now.Hour < 12 ? dfi.AMDesignator : dfi.PMDesignator);
        L.date = showDate ? (now.ToString("M月d日") + " " + dfi.GetDayName(now.DayOfWeek)) : "";

        // 用“最宽形态”测量，避免数字宽度差异导致窗口每秒抖动
        L.st = measureG.MeasureString("88:88", L.fTime, PointF.Empty, typo);
        L.ss = L.s.Length > 0 ? measureG.MeasureString("88", L.fSec, PointF.Empty, typo) : SizeF.Empty;
        L.sa = L.am.Length > 0 ? measureG.MeasureString("上午", L.fLabel, PointF.Empty, typo) : SizeF.Empty;
        L.sd = L.date.Length > 0 ? measureG.MeasureString("88月88日 星期三", L.fLabel, PointF.Empty, typo) : SizeF.Empty;

        L.rowH = L.fTime.Height;
        L.gapSec = baseS * 0.12f;
        L.gapAm = baseS * 0.10f;
        L.gapY = baseS * 0.10f;
        L.contentW = L.st.Width;
        if (L.ss.Width > 0) L.contentW += L.gapSec + L.ss.Width;
        if (L.sa.Width > 0) L.contentW += L.gapAm + L.sa.Width;
        if (L.sd.Width > L.contentW) L.contentW = L.sd.Width;
        L.contentH = L.rowH + (L.sd.Height > 0 ? L.gapY + L.sd.Height : 0);

        if (IsCardMode()) { L.padX = baseS * 0.50f; L.padY = baseS * 0.38f; L.sh = baseS * 0.20f; }
        else { L.padX = baseS * 0.07f; L.padY = baseS * 0.05f; L.sh = baseS * 0.14f; }
        L.W = (int)Math.Ceiling(L.contentW + L.padX * 2 + L.sh * 2);
        L.H = (int)Math.Ceiling(L.contentH + L.padY * 2 + L.sh * 2);
        return L;
    }

    static void DrawLayout(Graphics g, Layout L, float baseS, float ox, float oy)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float x0 = ox + L.sh + L.padX;
        float y0 = oy + L.sh + L.padY;

        if (IsCardMode())
            PaintCard(g, new RectangleF(ox + L.sh, oy + L.sh, L.W - L.sh * 2, L.H - L.sh * 2), ox, oy, L.sh);

        float sr = (bgKind == 5) ? baseS * 0.011f : baseS * 0.030f;
        int sa = (bgKind == 5) ? 165 : 88;
        DrawTxt(g, L.t, L.fTime, x0, y0, L.contentW, L.rowH, StringAlignment.Near, StringAlignment.Near, Color.White, baseS, sr, sa);
        if (L.sa.Width > 0)
        {
            DrawTxt(g, L.am, L.fLabel, x0 + L.st.Width + L.gapAm, y0, L.contentW - L.st.Width - L.gapAm, L.rowH,
                StringAlignment.Far, StringAlignment.Far, Color.FromArgb(236, 255, 255, 255), baseS, sr, sa);
        }
        if (L.ss.Width > 0)
        {
            DrawTxt(g, L.s, L.fSec, x0, y0, L.contentW, L.rowH, StringAlignment.Far, StringAlignment.Far,
                Color.FromArgb(232, 255, 255, 255), baseS, sr, sa);
        }
        if (L.sd.Width > 0)
        {
            DrawTxt(g, L.date, L.fLabel, x0, y0 + L.rowH + L.gapY, L.contentW, L.sd.Height + 1f,
                StringAlignment.Near, StringAlignment.Near, Color.FromArgb(240, 255, 255, 255), baseS, sr, sa);
        }
    }

    static void DrawTxt(Graphics g, string s, Font f, float x, float y, float w, float h,
        StringAlignment align, StringAlignment lineAlign, Color c, float baseS, float shadowR, int shadowA)
    {
        float r = shadowR;
        float[] ox = new float[] { 0f, 1f, -1f, 1f, -1f, 0f, 2f, -2f, 2f, -2f };
        float[] oy = new float[] { 1f, 1f, 1f, 2f, 2f, 2f, 3f, 3f, 1f, 1f };
        using (StringFormat fmt = new StringFormat(typo))
        {
            fmt.Alignment = align;
            fmt.LineAlignment = lineAlign;
            fmt.FormatFlags = StringFormatFlags.NoWrap;
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(shadowA, 0, 0, 0)))
            {
                for (int i = 0; i < ox.Length; i++)
                    g.DrawString(s, f, sb, new RectangleF(x + ox[i] * r, y + oy[i] * r, w, h), fmt);
            }
            using (SolidBrush sb = new SolidBrush(c))
                g.DrawString(s, f, sb, new RectangleF(x, y, w, h), fmt);
        }
    }

    static void AddRound(GraphicsPath p, RectangleF r, float rad)
    {
        float d = Math.Max(1f, rad * 2f);
        p.AddArc(r.X, r.Y, d, d, 180f, 90f);
        p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
        p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
        p.CloseFigure();
    }

    // ---------------- 字体 ----------------
    static void CacheFonts()
    {
        using (InstalledFontCollection c = new InstalledFontCollection())
        {
            FontFamily[] fams = c.Families;
            installedFonts = new string[fams.Length];
            for (int i = 0; i < fams.Length; i++) installedFonts[i] = fams[i].Name;
        }
    }

    static bool HasFont(string name)
    {
        if (installedFonts == null) return false;
        for (int i = 0; i < installedFonts.Length; i++)
            if (string.Equals(installedFonts[i], name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static Font PickFont(float size, bool digits)
    {
        string[] cands = digits
            ? new string[] { "Bahnschrift Light", "Bahnschrift SemiLight", "Bahnschrift",
                             "Segoe UI Variable Display Light", "Segoe UI Light", "Segoe UI",
                             "Microsoft YaHei UI Light", "Microsoft YaHei UI" }
            : new string[] { "Microsoft YaHei UI Light", "Microsoft YaHei UI", "Microsoft YaHei",
                             "Segoe UI Variable Text Light", "Segoe UI" };
        for (int i = 0; i < cands.Length; i++)
        {
            if (!HasFont(cands[i])) continue;
            try { return new Font(new FontFamily(cands[i]), size, FontStyle.Regular, GraphicsUnit.Pixel); }
            catch { }
        }
        return new Font(FontFamily.GenericSansSerif, size, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    // ---------------- 分层窗口 ----------------
    static void EnsureSurface(int w, int h)
    {
        if (w < 1) w = 1;
        if (h < 1) h = 1;
        if (memDc != IntPtr.Zero && (w != surfW || h != surfH)) ReleaseSurface();
        if (memDc != IntPtr.Zero) return;
        IntPtr screen = GetDC(IntPtr.Zero);
        memDc = CreateCompatibleDC(screen);
        BITMAPINFO bmi = new BITMAPINFO();
        bmi.h.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
        bmi.h.biWidth = w;
        bmi.h.biHeight = -h;
        bmi.h.biPlanes = 1;
        bmi.h.biBitCount = 32;
        bmi.h.biCompression = 0;
        hbmp = CreateDIBSection(screen, ref bmi, 0, out bits, IntPtr.Zero, 0);
        SelectObject(memDc, hbmp);
        ReleaseDC(IntPtr.Zero, screen);
        surfW = w; surfH = h;
    }

    static void ReleaseSurface()
    {
        if (hbmp != IntPtr.Zero) { DeleteObject(hbmp); hbmp = IntPtr.Zero; }
        if (memDc != IntPtr.Zero) { DeleteDC(memDc); memDc = IntPtr.Zero; }
        bits = IntPtr.Zero;
        surfW = 0; surfH = 0;
    }

    static void Push(int w, int h)
    {
        if (memDc == IntPtr.Zero || form == null) return;
        POINT pd = new POINT(); pd.x = posX; pd.y = posY;
        SIZE sz = new SIZE(); sz.cx = w; sz.cy = h;
        POINT ps = new POINT();
        BLENDFUNCTION bf = new BLENDFUNCTION();
        bf.op = 0; bf.flags = 0; bf.constAlpha = 255; bf.alphaFmt = 1;
        lastPushOk = UpdateLayeredWindow(form.Handle, IntPtr.Zero, ref pd, ref sz, memDc, ref ps, 0, ref bf, ULW_ALPHA);
        lastPushErr = Marshal.GetLastWin32Error();
    }

    static void ClampToScreen(int w, int h, bool placeDefault)
    {
        if (w < 1) w = 1;
        if (h < 1) h = 1;
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        int margin = (int)(26 * scale);
        if (placeDefault || posX == int.MinValue)
        {
            posX = wa.Right - w - margin;
            posY = wa.Top + margin;
            return;
        }
        int minX = wa.Left - w + (int)(90 * scale);
        int maxX = wa.Right - (int)(90 * scale);
        int minY = wa.Top;
        int maxY = wa.Bottom - (int)(30 * scale);
        if (posX < minX) posX = minX;
        if (posX > maxX) posX = maxX;
        if (posY < minY) posY = minY;
        if (posY > maxY) posY = maxY;
    }

    // ---------------- 右键菜单 ----------------
    static void BuildMenu()
    {
        menu = new ContextMenuStrip();
        menu.ShowImageMargin = false;    // 不需要图片列
        menu.ShowCheckMargin = true;     // 但必须保留对号列（默认关，关掉就对号全没）
        menu.Font = PickFont(15f * scale, false);

        ToolStripMenuItem miSec = new ToolStripMenuItem("显示秒");
        miSec.CheckOnClick = true; miSec.Checked = showSeconds;
        miSec.CheckedChanged += delegate { showSeconds = miSec.Checked; Dirty(); Save(); };
        menu.Items.Add(miSec);

        ToolStripMenuItem miDate = new ToolStripMenuItem("显示日期");
        miDate.CheckOnClick = true; miDate.Checked = showDate;
        miDate.CheckedChanged += delegate { showDate = miDate.Checked; Dirty(); Save(); };
        menu.Items.Add(miDate);

        ToolStripMenuItem mi24 = new ToolStripMenuItem("24 小时制");
        mi24.CheckOnClick = true; mi24.Checked = hour24;
        mi24.CheckedChanged += delegate { hour24 = mi24.Checked; Dirty(); Save(); };
        menu.Items.Add(mi24);

        menu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem miBg = new ToolStripMenuItem("背景");
        bgItems = new ToolStripMenuItem[BG_NAMES.Length];
        for (int i = 0; i < BG_NAMES.Length; i++)
        {
            int idx = i;
            ToolStripMenuItem it = new ToolStripMenuItem(BG_NAMES[i]);
            it.Checked = (bgKind == i);
            it.Click += delegate { bgKind = idx; renderKey = ""; RefreshCapture(true, true); SyncChecks(); Save(); Render(); };
            miBg.DropDownItems.Add(it);
            bgItems[i] = it;
        }
        menu.Items.Add(miBg);

        ToolStripMenuItem miOp = new ToolStripMenuItem("不透明度");
        opacityItems = new ToolStripMenuItem[OPACITY_NAMES.Length];
        for (int i = 0; i < OPACITY_NAMES.Length; i++)
        {
            int idx = i;
            ToolStripMenuItem it = new ToolStripMenuItem(OPACITY_NAMES[i]);
            it.Checked = (bgOpacity == i);
            it.Click += delegate { bgOpacity = idx; SyncChecks(); Dirty(); Save(); };
            miOp.DropDownItems.Add(it);
            opacityItems[i] = it;
        }
        menu.Items.Add(miOp);

        ToolStripMenuItem miSize = new ToolStripMenuItem("字号");
        sizeItems = new ToolStripMenuItem[SIZE_NAMES.Length];
        for (int i = 0; i < SIZE_NAMES.Length; i++)
        {
            int idx = i;
            ToolStripMenuItem it = new ToolStripMenuItem(SIZE_NAMES[i]);
            it.Checked = (sizeIdx == i);
            it.Click += delegate { sizeIdx = idx; SyncChecks(); Dirty(); Save(); };
            miSize.DropDownItems.Add(it);
            sizeItems[i] = it;
        }
        menu.Items.Add(miSize);

        menu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem miLock = new ToolStripMenuItem("锁定位置");
        miLock.CheckOnClick = true; miLock.Checked = locked;
        miLock.CheckedChanged += delegate { locked = miLock.Checked; Save(); };
        menu.Items.Add(miLock);

        ToolStripMenuItem miZ = new ToolStripMenuItem("显示层级");
        zItems = new ToolStripMenuItem[ZORDER_NAMES.Length];
        for (int i = 0; i < ZORDER_NAMES.Length; i++)
        {
            int idx = i;
            ToolStripMenuItem it = new ToolStripMenuItem(ZORDER_NAMES[i]);
            it.Checked = (zOrder == i);
            it.Click += delegate { zOrder = idx; ApplyZOrder(); ApplyVisibility(); SyncChecks(); Save(); };
            miZ.DropDownItems.Add(it);
            zItems[i] = it;
        }
        menu.Items.Add(miZ);


        ToolStripMenuItem miAuto = new ToolStripMenuItem("开机自启");
        miAuto.CheckOnClick = true;
        miAuto.Checked = File.Exists(startupLnk);
        miAuto.CheckedChanged += delegate { SetAutostart(miAuto.Checked); };
        menu.Items.Add(miAuto);

        ToolStripMenuItem miReset = new ToolStripMenuItem("回到右上角");
        miReset.Click += delegate { posX = int.MinValue; Dirty(); Save(); };
        menu.Items.Add(miReset);

        menu.Items.Add(new ToolStripSeparator());

        ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
        miExit.Click += delegate { form.Close(); };
        menu.Items.Add(miExit);
    }

    // ---------------- 菜单截图（自检用） ----------------
    static void MakeMenuShot()
    {
        BuildMenu();
        Form host = new Form();
        host.FormBorderStyle = FormBorderStyle.None;
        host.ShowInTaskbar = false;
        host.StartPosition = FormStartPosition.Manual;
        host.Location = new Point(-4000, -4000);
        host.Size = new Size(8, 8);
        host.Show();

        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        menu.Show(host, new Point(0, 0));
        Application.DoEvents();
        System.Threading.Thread.Sleep(250);
        Application.DoEvents();
        using (Bitmap bmp = new Bitmap(menu.Width, menu.Height))
        {
            menu.DrawToBitmap(bmp, new Rectangle(0, 0, menu.Width, menu.Height));
            bmp.Save(Path.Combine(dir, "menu.png"), ImageFormat.Png);
        }

        int subNo = 0;
        for (int i = 0; i < menu.Items.Count; i++)
        {
            ToolStripMenuItem it = menu.Items[i] as ToolStripMenuItem;
            if (it == null || it.DropDownItems.Count == 0) continue;
            subNo++;
            it.ShowDropDown();
            Application.DoEvents();
            System.Threading.Thread.Sleep(250);
            Application.DoEvents();
            using (Bitmap bmp2 = new Bitmap(it.DropDown.Width, it.DropDown.Height))
            {
                it.DropDown.DrawToBitmap(bmp2, new Rectangle(0, 0, it.DropDown.Width, it.DropDown.Height));
                bmp2.Save(Path.Combine(dir, "menu-sub" + subNo + ".png"), ImageFormat.Png);
            }
            it.HideDropDown();
        }
        menu.Close();
        host.Close();
    }
    static void SyncChecks()
    {
        if (bgItems != null)
            for (int i = 0; i < bgItems.Length; i++) bgItems[i].Checked = (i == bgKind);
        if (opacityItems != null)
            for (int i = 0; i < opacityItems.Length; i++) opacityItems[i].Checked = (i == bgOpacity);
        if (sizeItems != null)
            for (int i = 0; i < sizeItems.Length; i++) sizeItems[i].Checked = (i == sizeIdx);
        if (zItems != null)
            for (int i = 0; i < zItems.Length; i++) zItems[i].Checked = (i == zOrder);
    }

    static void SetAutostart(bool on)
    {
        try
        {
            if (on)
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                object sh = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { startupLnk });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { Application.ExecutablePath });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(Application.ExecutablePath) });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "桌面时钟组件" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            }
            else if (File.Exists(startupLnk)) File.Delete(startupLnk);
        }
        catch { }
    }

    // ---------------- 设置存取 ----------------
    static void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(settingsPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("seconds=" + (showSeconds ? 1 : 0));
            sb.AppendLine("date=" + (showDate ? 1 : 0));
            sb.AppendLine("hour24=" + (hour24 ? 1 : 0));
            sb.AppendLine("bg=" + bgKind);
            sb.AppendLine("bgopacity=" + bgOpacity);
            sb.AppendLine("size=" + sizeIdx);
            sb.AppendLine("locked=" + (locked ? 1 : 0));
            sb.AppendLine("zorder=" + zOrder);
            if (posX != int.MinValue) { sb.AppendLine("x=" + posX); sb.AppendLine("y=" + posY); }
            File.WriteAllText(settingsPath, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    static void Load()
    {
        try
        {
            if (!File.Exists(settingsPath)) return;
            string[] lines = File.ReadAllLines(settingsPath);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                bool on = v == "1";
                int n;
                switch (k)
                {
                    case "seconds": showSeconds = on; break;
                    case "date": showDate = on; break;
                    case "hour24": hour24 = on; break;
                    case "locked": locked = on; break;
                    case "zorder": if (int.TryParse(v, out n) && n >= 0 && n < ZORDER_NAMES.Length) zOrder = n; break;
                    case "topmost": zOrder = on ? 2 : 1; break;   // 旧版键
                    case "style": if (int.TryParse(v, out n)) bgKind = (n == 1 ? 4 : 0); break;   // 旧版键：0 卡片 / 1 纯文字
                    case "bg": if (int.TryParse(v, out n) && n >= 0 && n < BG_NAMES.Length) bgKind = n; break;
                    case "bgopacity": if (int.TryParse(v, out n) && n >= 0 && n < OPACITY_NAMES.Length) bgOpacity = n; break;
                    case "size": if (int.TryParse(v, out n) && n >= 0 && n < SIZES.Length) sizeIdx = n; break;
                    case "x": if (int.TryParse(v, out n)) posX = n; break;
                    case "y": if (int.TryParse(v, out n)) posY = n; break;
                }
            }
        }
        catch { }
    }

    // ---------------- 自检 ----------------
    static void SelfTest()
    {
        string report = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "selftest.txt");
        string msg;
        try
        {
            form = new WidgetForm();
            form.FormBorderStyle = FormBorderStyle.None;
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Size = new Size(8, 8);
            form.Show();
            Render();
            Layout L = Build(DateTime.Now, SIZES[sizeIdx] * scale);
            string font = L.fTime.FontFamily.Name;
            string label = L.fLabel.FontFamily.Name;
            IntPtr fgh = GetForegroundWindow();
            StringBuilder fgc = new StringBuilder(120);
            GetClassName(fgh, fgc, fgc.Capacity);
            string fgClass = fgc.ToString();
            bool fgIsDesk = IsDesktopForeground();
            L.Dispose();
            msg = string.Format("结果=成功{0}DPI缩放={1}{0}屏幕={2}{0}表面尺寸={3}x{4}{0}位置={5},{6}{0}数字字体={7}{0}文字字体={8}{0}分层窗口推送={9} (err={10}){0}背景={15} 不透明度={16} 抓屏可用={17}{0}前景窗口类={11}{0}判定为桌面={12}{0}分类(Progman)={13} 分类(Chrome_WidgetWin_1)={14}{0}",
                Environment.NewLine, scale, Screen.PrimaryScreen.Bounds, surfW, surfH, posX, posY, font, label, lastPushOk, lastPushErr, fgClass, fgIsDesk, IsDesktopClass("Progman"), IsDesktopClass("Chrome_WidgetWin_1"), BG_NAMES[bgKind], OPACITY_NAMES[bgOpacity], srcOk);
            form.Close();
        }
        catch (Exception ex) { msg = "结果=失败" + Environment.NewLine + ex.ToString(); }
        try { File.WriteAllText(report, msg, Encoding.UTF8); } catch { }
    }

    // ---------------- 显示/隐藏机制自检 ----------------
    static string FgClass()
    {
        IntPtr fg = GetForegroundWindow();
        StringBuilder sb = new StringBuilder(120);
        GetClassName(fg, sb, sb.Capacity);
        string s = sb.ToString();
        return s.Length == 0 ? "(无)" : s;
    }

    static void VisTest()
    {
        string report = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "vistest.txt");
        string msg;
        try
        {
            form = new WidgetForm();
            form.FormBorderStyle = FormBorderStyle.None;
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Size = new Size(8, 8);
            form.Show();
            Render();
            string a = "1) 初始状态: 可见=" + IsWindowVisible(form.Handle) + " 推送=" + lastPushOk;
            ShowWindow(form.Handle, SW_HIDE);
            string b = "2) 执行隐藏: 可见=" + IsWindowVisible(form.Handle);
            ShowWindow(form.Handle, SW_SHOWNOACTIVATE);
            if (zOrder == 2) SetWindowPos(form.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            else if (BottomMode) AssertBottom();
            renderKey = "";
            Render();
            string c = "3) 重新显示: 可见=" + IsWindowVisible(form.Handle) + " 推送=" + lastPushOk + " 表面=" + surfW + "x" + surfH;
            string d = "4) 当前前台窗口类=" + FgClass() + " 判定为桌面=" + IsDesktopForeground();
            msg = "结果=成功" + Environment.NewLine + a + Environment.NewLine + b + Environment.NewLine + c + Environment.NewLine + d + Environment.NewLine;
        }
        catch (Exception ex) { msg = "结果=失败" + Environment.NewLine + ex.ToString(); }
        try { File.WriteAllText(report, msg, Encoding.UTF8); } catch { }
    }

    // ---------------- 效果预览图 ----------------
    static void MakePreviews(string wall, string outDir)
    {
        scale = 1.5f;
        if (string.IsNullOrEmpty(outDir)) outDir = ".";
        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
        DateTime demo = new DateTime(2026, 10, 8, 21, 53, 47);
        int cw = 2560, ch = 1440;
        int keepBg = bgKind, keepOp = bgOpacity, keepSize = sizeIdx;
        bool keepSec = showSeconds, keepDate = showDate;
        showSeconds = true; showDate = true; sizeIdx = 2;
        try
        {
            using (Bitmap wallBmp = new Bitmap(cw, ch, PixelFormat.Format32bppArgb))
            {
                using (Graphics wg = Graphics.FromImage(wallBmp)) DrawBackdrop(wg, wall, cw, ch);

                int cropW = 780, cropH = 420, lab = 34, gap = 18, margin = 18;
                int sheetW = margin * 2 + cropW * 3 + gap * 2;
                int sheetH = margin * 2 + (lab + cropH) * 2 + gap;
                using (Bitmap sheet = new Bitmap(sheetW, sheetH, PixelFormat.Format32bppArgb))
                {
                    using (Font lf = PickFont(24f, false))
                    {
                        using (Graphics sg = Graphics.FromImage(sheet))
                        {
                            sg.Clear(Color.FromArgb(255, 22, 24, 28));
                            sg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                            using (SolidBrush lb = new SolidBrush(Color.FromArgb(255, 236, 238, 242)))
                            {
                                for (int i = 0; i < BG_NAMES.Length; i++)
                                {
                                    bgKind = i;
                                    float baseS = SIZES[sizeIdx] * scale;
                                    Layout L = Build(demo, baseS);
                                    int px = cw - L.W - (int)(26 * scale), py = (int)(26 * scale);
                                    Rectangle rgn = new Rectangle(px, py, L.W, L.H);
                                    DisposeCapture();
                                    srcBlur = ExtractBlur(wallBmp, rgn, 12);
                                    srcAvg = AverageColor(wallBmp, rgn);
                                    srcOk = true;
                                    using (Bitmap canvas = new Bitmap(cw, ch, PixelFormat.Format32bppArgb))
                                    {
                                        using (Graphics g = Graphics.FromImage(canvas))
                                        {
                                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                            g.DrawImage(wallBmp, new Rectangle(0, 0, cw, ch));
                                            DrawLayout(g, L, baseS, px, py);
                                        }
                                        int col = i % 3, rowi = i / 3;
                                        int dx = margin + col * (cropW + gap);
                                        int dy = margin + rowi * (lab + cropH + gap);
                                        sg.DrawString((i + 1) + ". " + BG_NAMES[i], lf, lb, dx, dy);
                                        int cropX = Math.Min(cw - cropW, Math.Max(0, px + L.W + 40 - cropW));
                                        sg.DrawImage(canvas, new Rectangle(dx, dy + lab, cropW, cropH),
                                            new Rectangle(cropX, 0, cropW, cropH), GraphicsUnit.Pixel);
                                    }
                                    L.Dispose();
                                }
                            }
                        }
                    }
                    sheet.Save(Path.Combine(outDir, "预览-背景对比.png"), ImageFormat.Png);
                }
            }
        }
        finally
        {
            bgKind = keepBg; bgOpacity = keepOp; showSeconds = keepSec; showDate = keepDate; sizeIdx = keepSize;
            srcOk = false; capW = 0; capH = 0;
            DisposeCapture();
        }
    }

    static void DrawBackdrop(Graphics g, string wall, int cw, int ch)
    {
        bool ok = false;
        if (!string.IsNullOrEmpty(wall) && File.Exists(wall))
        {
            try
            {
                using (Image img = Image.FromFile(wall))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, new Rectangle(0, 0, cw, ch));
                    ok = true;
                }
            }
            catch { }
        }
        if (!ok)
        {
            using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(0, 0, cw, ch),
                Color.FromArgb(255, 42, 54, 78), Color.FromArgb(255, 14, 16, 24), 35f))
                g.FillRectangle(b, new Rectangle(0, 0, cw, ch));
        }
    }

    // ---------------- DPI ----------------
    static void TryDpiAware()
    {
        try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; }
        catch { }
        try { SetProcessDPIAware(); }
        catch { }
    }

    // ---------------- Win32 ----------------
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount;
        public uint biCompression; public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public uint biClrUsed; public uint biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFO { public BITMAPINFOHEADER h; public uint colors; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte op, flags, constAlpha, alphaFmt; }

    const int ULW_ALPHA = 2;
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int SW_HIDE = 0;
    const int SW_SHOWNOACTIVATE = 4;
    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOACTIVATE = 0x0010;
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    const uint SWP_NOZORDER = 0x0004;
    const uint GW_HWNDNEXT = 2;
    const int GWL_EXSTYLE = -20;
    const int WM_WINDOWPOSCHANGING = 0x0046;
    const int WM_WINDOWPOSCHANGED = 0x0047;
    [StructLayout(LayoutKind.Sequential)] struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

    class WidgetForm : Form
    {
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        // 桌面底层模式：z 序一旦被改动就打标记，交给主循环重新摆位
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_WINDOWPOSCHANGED && BottomMode && !sinking) zDirty = true;
            base.WndProc(ref m);
        }
    }

    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bitsPtr, IntPtr sec, uint off);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref POINT pptDst, ref SIZE psize, IntPtr src, ref POINT pptSrc, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder cls, int max);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int idx);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
