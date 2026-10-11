using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Naifrog
{
    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int x, y; public Point(int a, int b) { x = a; y = b; } }
        [StructLayout(LayoutKind.Sequential)] public struct Size { public int cx, cy; public Size(int a, int b) { cx = a; cy = b; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct Blend { public byte operation, flags, alpha, format; }
        [DllImport("user32.dll", SetLastError = true)] public static extern bool UpdateLayeredWindow(IntPtr window, IntPtr dc, ref Point position, ref Size size, IntPtr sourceDc, ref Point source, uint key, ref Blend blend, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    }
    public static class SpriteRender
    {
        public const int Canvas = 384;
        public static Bitmap Normal(Bitmap sprite, bool pressed)
        {
            Bitmap result = new Bitmap(Canvas, Canvas, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(result))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                if (pressed) g.DrawImage(sprite, new Rectangle(63, 68, 258, 252), new Rectangle(0, 0, 256, 256), GraphicsUnit.Pixel);
                else g.DrawImageUnscaled(sprite, 64, 64);
            }
            return result;
        }
        // A reversible affine deformation keeps the actual captured pose and its
        // grab point continuous. It does not substitute a standing pose mid-air.
        public static Bitmap Drag(Bitmap captured, PointF grab, double strength, double shear)
        {
            Bitmap result = new Bitmap(Canvas, Canvas, PixelFormat.Format32bppPArgb);
            Rectangle occupied = Bounds(captured);
            for (int attempt = 0; attempt < 12; attempt++)
            {
                double testX = 1 + .18 * strength, testY = 1 - .12 * strength, testShear = shear * strength;
                bool fits = true;
                foreach (PointF p in new[] { new PointF(occupied.Left, occupied.Top), new PointF(occupied.Right, occupied.Top), new PointF(occupied.Left, occupied.Bottom), new PointF(occupied.Right, occupied.Bottom) })
                {
                    double x = grab.X + (p.X - grab.X) * testX + (p.Y - grab.Y) * testShear;
                    double y = grab.Y + (p.Y - grab.Y) * testY;
                    if (x < 1 || x > Canvas - 1 || y < 1 || y > Canvas - 1) fits = false;
                }
                if (fits) break; strength *= .8;
            }
            float sx = (float)(1 + .18 * strength), sy = (float)(1 - .12 * strength), sh = (float)(shear * strength);
            using (Graphics g = Graphics.FromImage(result))
            using (Matrix matrix = new Matrix(sx, 0, sh, sy, grab.X * (1 - sx) - sh * grab.Y, grab.Y * (1 - sy)))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.Transform = matrix; g.DrawImageUnscaled(captured, 0, 0);
            }
            return result;
        }
        public static Rectangle Bounds(Bitmap bitmap)
        {
            int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
            BitmapData locked = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[bitmap.Width * 4];
                for (int y = 0; y < bitmap.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(locked.Scan0, y * locked.Stride), row, 0, row.Length);
                    for (int x = 0; x < bitmap.Width; x++) if (row[x * 4 + 3] > 32)
                    { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
                }
            }
            finally { bitmap.UnlockBits(locked); }
            return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }
    }
    public sealed class PetWindow : Form
    {
        readonly Assets assets;
        readonly PetEngine engine;
        readonly ClickArbiter clicks = new ClickArbiter();
        readonly Timer timer = new Timer();
        readonly Stopwatch watch = Stopwatch.StartNew();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly NotifyIcon tray = new NotifyIcon();
        ToolStripMenuItem gameMenu;
        bool syncingMenu;
        double previous, hoverStarted = -1, pressUntil, dragStarted, releaseStarted = -1, maxSpeed, strength, shear;
        bool mouseHeld, drag, hoverFired, recoveryHeld;
        Point mouseDownScreen, mouseDownWindow, priorMouse;
        Point releaseFrom, releaseTo;
        PointF grab;
        PointF recoveryGrab;
        double scale = 1;
        Bitmap latest, captured;
        string lastKey;
        bool lastPressed;
        string boundsKey;
        Rectangle currentBounds;
        readonly string smokeReport;
        int draws, drawFailures;
        public PetWindow(Assets source, string report)
        {
            assets = source; engine = new PetEngine(assets); smokeReport = report;
            Text = "8bit奶蛙桌宠"; FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            ClientSize = new System.Drawing.Size(SpriteRender.Canvas, SpriteRender.Canvas);
            DoubleBuffered = true;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 16, area.Bottom - Height + 60);
            using (Stream icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("Naifrog.Icon"))
                Icon = new Icon(icon);
            tray.Icon = Icon; tray.Text = "8bit奶蛙桌宠 v1.0"; tray.Visible = true;
            BuildMenu(); tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { Reveal(); };
            clicks.Recognized += delegate(string action)
            {
                if (action == "press") return;
                if (engine.Game) action = "A09";
                engine.Request(action, true);
            };
            MouseDown += OnPetDown; MouseMove += OnPetMove; MouseUp += OnPetUp;
            timer.Interval = 16; timer.Tick += OnTick;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams c = base.CreateParams; c.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000; return c; }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            base.WndProc(ref m);
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e); previous = watch.Elapsed.TotalSeconds; RenderNormal(); timer.Start();
        }
        void BuildMenu()
        {
            ToolStripMenuItem title = new ToolStripMenuItem("8bit奶蛙桌宠 · v1.0"); title.Enabled = false; menu.Items.Add(title);
            ToolStripMenuItem pause = new ToolStripMenuItem("暂停动画与待机"); pause.CheckOnClick = true;
            pause.CheckedChanged += delegate { engine.Paused = pause.Checked; clicks.Cancel(); }; menu.Items.Add(pause);
            ToolStripMenuItem auto = new ToolStripMenuItem("自动待机 / 坐下 / 睡觉"); auto.Checked = true; auto.CheckOnClick = true;
            auto.CheckedChanged += delegate { engine.AutoIdle = auto.Checked; }; menu.Items.Add(auto);
            ToolStripMenuItem top = new ToolStripMenuItem("保持置顶"); top.Checked = true; top.CheckOnClick = true;
            top.CheckedChanged += delegate { TopMost = top.Checked; }; menu.Items.Add(top);
            menu.Items.Add("回到桌面右下角", null, delegate { Reveal(); });
            menu.Items.Add("隐藏桌宠（托盘可恢复）", null, delegate { Hide(); clicks.Cancel(); });
            ToolStripMenuItem sizes = new ToolStripMenuItem("大小");
            foreach (double value in new[] { .75, 1.0, 1.5, 2.0 })
            {
                double selected = value;
                sizes.DropDownItems.Add((value * 100).ToString("0") + "%", null, delegate { ResizePet(selected); });
            }
            menu.Items.Add(sizes);
            ToolStripMenuItem actions = new ToolStripMenuItem("看看动作");
            for (int n = 1; n <= 12; n++)
            {
                string id = "A" + n.ToString("00");
                actions.DropDownItems.Add(id + " " + assets.Clips[id].name, null,
                    delegate { engine.Paused = false; pause.Checked = false; engine.Request(id, true); Show(); });
            }
            menu.Items.Add(actions);
            gameMenu = new ToolStripMenuItem("小游戏：点我跳跃"); gameMenu.CheckOnClick = true;
            gameMenu.CheckedChanged += delegate
            { if (!syncingMenu) { if (gameMenu.Checked) engine.StartGame(); else engine.StopGame(); } }; menu.Items.Add(gameMenu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("使用说明", null, delegate
            {
                MessageBox.Show("悬停：好奇歪头\n单击头部：挥手\n单击身体：轻压回应\n双击：跳跃\n1.2秒内肚皮三连点：大笑\n按住移动超过6像素：拖动\n右键或托盘：菜单\n\n无互动90秒坐下，5分钟睡觉。\n点击睡着的奶蛙：醒来后挥手。\n\n4秒动作，24帧/秒。离线运行，无联网、键盘监听、截屏或剪贴板读取。\n设置和位置只在本次运行中生效。", "8bit奶蛙桌宠 · 使用说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            menu.Items.Add("退出", null, delegate { Close(); });
        }
        void Reveal()
        {
            engine.Reset(); clicks.Cancel(); hoverFired = false; hoverStarted = -1;
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.Right - Width - 16, area.Bottom - Height + (int)(60 * scale));
            Show(); lastKey = null; RenderNormal();
        }
        void ResizePet(double value)
        {
            if (drag || releaseStarted >= 0) return;
            double ratio = value / scale; Point foot = new Point(Left + (int)(192 * scale), Top + (int)(288 * scale));
            scale = value; ClientSize = new System.Drawing.Size((int)(384 * scale), (int)(384 * scale));
            Location = new Point(foot.X - (int)(192 * scale), foot.Y - (int)(288 * scale));
            ClampLocation(); lastKey = null; RenderNormal();
        }
        void ClampLocation()
        { Location = ConstrainedLocation(Location); }
        Point ConstrainedLocation(Point position)
        {
            Rectangle area = Screen.FromPoint(new Point(position.X + Width / 2, position.Y + Height / 2)).WorkingArea;
            // Only the visible sprite must remain in the working area; its alpha margin may extend outside.
            int pad = (int)(64 * scale);
            int visible = (int)(256 * scale);
            int x = Math.Max(area.Left - pad, Math.Min(position.X, area.Right - visible - pad));
            int y = Math.Max(area.Top - pad, Math.Min(position.Y, area.Bottom - visible - pad));
            return new Point(x, y);
        }
        bool Hit(Point client)
        {
            if (latest == null) return false;
            int x = (int)(client.X / scale), y = (int)(client.Y / scale);
            return x >= 0 && y >= 0 && x < latest.Width && y < latest.Height && latest.GetPixel(x, y).A > 32;
        }
        string HitRegion(Point logical)
        {
            Rectangle bounds = SpriteRender.Bounds(latest);
            if (logical.Y < bounds.Top + bounds.Height * .36) return "head";
            if (logical.Y < bounds.Top + bounds.Height * .84 && Math.Abs(logical.X - (bounds.Left + bounds.Width / 2)) < bounds.Width * .32) return "belly";
            return "body";
        }
        void OnPetDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { menu.Show(Cursor.Position); return; }
            if (e.Button != MouseButtons.Left || !Hit(e.Location)) return;
            if (releaseStarted >= 0)
            {
                double t = Math.Min(1, (watch.Elapsed.TotalSeconds - releaseStarted) / .3);
                strength *= 1 - t * t * (3 - 2 * t);
                recoveryGrab = grab; recoveryHeld = true; releaseStarted = -1;
            }
            mouseHeld = true; Capture = true; mouseDownScreen = Cursor.Position; mouseDownWindow = Location; priorMouse = mouseDownScreen;
            grab = new PointF((float)(e.X / scale), (float)(e.Y / scale));
            pressUntil = watch.Elapsed.TotalSeconds + .25; engine.Touch();
        }
        void OnPetMove(object sender, MouseEventArgs e)
        {
            if (!mouseHeld) return;
            Point current = Cursor.Position;
            double distance = Math.Sqrt(Math.Pow(current.X - mouseDownScreen.X, 2) + Math.Pow(current.Y - mouseDownScreen.Y, 2));
            if (!drag && distance > 6)
            {
                if (captured != null) captured.Dispose();
                captured = (Bitmap)latest.Clone(); engine.BeginDrag(); clicks.Cancel();
                recoveryHeld = false;
                drag = true; dragStarted = watch.Elapsed.TotalSeconds; strength = 0; shear = 0; maxSpeed = 0;
            }
            if (drag) Location = new Point(mouseDownWindow.X + current.X - mouseDownScreen.X, mouseDownWindow.Y + current.Y - mouseDownScreen.Y);
        }
        void OnPetUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !mouseHeld) return;
            mouseHeld = false; Capture = false;
            if (drag)
            {
                drag = false; releaseStarted = watch.Elapsed.TotalSeconds;
                releaseFrom = Location; releaseTo = ConstrainedLocation(Location); return;
            }
            if (recoveryHeld)
            {
                recoveryHeld = false; grab = recoveryGrab; releaseStarted = watch.Elapsed.TotalSeconds;
                releaseFrom = Location; releaseTo = ConstrainedLocation(Location); return;
            }
            if (!Hit(e.Location)) return;
            Point logical = new Point((int)(e.X / scale), (int)(e.Y / scale));
            if (engine.Current.end == "Z" || engine.Current.start == "Z") { clicks.Cancel(); engine.Request("A05", true); }
            else clicks.Add(engine.Now, logical, HitRegion(logical));
        }
        void OnTick(object sender, EventArgs e)
        {
            double now = watch.Elapsed.TotalSeconds, dt = Math.Min(.1, now - previous); previous = now;
            if (smokeReport != null)
            {
                if (now > .8 && now < .85) engine.Request("A09", true);
                if (now >= 8)
                {
                    File.WriteAllText(smokeReport, "{\"window_created\":true,\"draws\":" + draws + ",\"layered_window_failures\":" + drawFailures + ",\"asset_load\":true}");
                    Close(); return;
                }
            }
            if (!Visible) return;
            if (mouseHeld && recoveryHeld) return;
            if (drag)
            {
                Point point = Cursor.Position;
                double velocity = Math.Sqrt(Math.Pow(point.X - priorMouse.X, 2) + Math.Pow(point.Y - priorMouse.Y, 2)) / Math.Max(.001, dt) / scale;
                maxSpeed = Math.Max(maxSpeed, velocity);
                double desired = Math.Min(1, velocity / 650 + .18);
                strength += (desired - strength) * Math.Min(1, dt * 10);
                double targetShear = Math.Max(-.12, Math.Min(.12, -(point.X - priorMouse.X) / Math.Max(.001, dt) / scale / 9000));
                shear += (targetShear - shear) * Math.Min(1, dt * 8);
                priorMouse = point;
                Replace(SpriteRender.Drag(captured, grab, strength, shear)); DrawLayer(); return;
            }
            if (releaseStarted >= 0)
            {
                double t = Math.Min(1, (now - releaseStarted) / .3), ease = t * t * (3 - 2 * t);
                Location = new Point((int)Math.Round(releaseFrom.X + (releaseTo.X - releaseFrom.X) * ease), (int)Math.Round(releaseFrom.Y + (releaseTo.Y - releaseFrom.Y) * ease));
                Replace(SpriteRender.Drag(captured, grab, strength * (1 - ease), shear)); DrawLayer();
                if (t >= 1)
                {
                    releaseStarted = -1; captured.Dispose(); captured = null;
                    engine.EndDrag(maxSpeed >= 480); lastKey = null; pressUntil = 0;
                }
                return;
            }
            engine.Advance(dt); clicks.Flush(engine.Now);
            if (gameMenu.Checked != engine.Game)
            { syncingMenu = true; gameMenu.Checked = engine.Game; syncingMenu = false; }
            Point local = PointToClient(Cursor.Position);
            if (boundsKey != engine.Key) { currentBounds = SpriteRender.Bounds(assets.Get(engine.Key)); boundsKey = engine.Key; }
            Rectangle bounds = currentBounds;
            // Cursor coordinates are used only near this pet, never recorded.
            Rectangle near = new Rectangle((int)((bounds.X + 64) * scale), (int)((bounds.Y + 64) * scale), (int)(bounds.Width * scale), (int)(bounds.Height * scale));
            near.Inflate((int)(48 * scale), (int)(48 * scale));
            if (!mouseHeld && near.Contains(local))
            {
                if (hoverStarted < 0) hoverStarted = now;
                if (!hoverFired && now - hoverStarted >= .3) { engine.Hover(); hoverFired = true; }
            }
            else { hoverStarted = -1; hoverFired = false; }
            bool pressed = now < pressUntil;
            if (lastKey != engine.Key || lastPressed != pressed) { RenderNormal(); }
        }
        void RenderNormal()
        {
            bool pressed = watch.Elapsed.TotalSeconds < pressUntil;
            Replace(SpriteRender.Normal(assets.Get(engine.Key), pressed));
            lastKey = engine.Key; lastPressed = pressed; DrawLayer();
        }
        void Replace(Bitmap bitmap) { if (latest != null) latest.Dispose(); latest = bitmap; }
        void DrawLayer()
        {
            if (latest == null || !IsHandleCreated) return;
            using (Bitmap display = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(display))
                {
                    g.CompositingMode = CompositingMode.SourceCopy; g.InterpolationMode = InterpolationMode.NearestNeighbor; g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(latest, new Rectangle(0, 0, Width, Height), new Rectangle(0, 0, 384, 384), GraphicsUnit.Pixel);
                }
                IntPtr screen = Native.GetDC(IntPtr.Zero), memory = Native.CreateCompatibleDC(screen), handle = IntPtr.Zero, old = IntPtr.Zero;
                try
                {
                    handle = display.GetHbitmap(Color.FromArgb(0)); old = Native.SelectObject(memory, handle);
                    Native.Point destination = new Native.Point(Left, Top), origin = new Native.Point(0, 0);
                    Native.Size size = new Native.Size(Width, Height);
                    Native.Blend blend = new Native.Blend { operation = 0, flags = 0, alpha = 255, format = 1 };
                    if (!Native.UpdateLayeredWindow(Handle, screen, ref destination, ref size, memory, ref origin, 0, ref blend, 2)) drawFailures++;
                    draws++;
                }
                finally
                {
                    if (old != IntPtr.Zero) Native.SelectObject(memory, old);
                    if (handle != IntPtr.Zero) Native.DeleteObject(handle);
                    Native.DeleteDC(memory); Native.ReleaseDC(IntPtr.Zero, screen);
                }
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Stop(); timer.Dispose(); tray.Visible = false; tray.Dispose(); menu.Dispose();
                if (latest != null) latest.Dispose(); if (captured != null) captured.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
