using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("8bit奶蛙桌宠")]
[assembly: AssemblyDescription("离线像素桌宠，4秒动作，24帧/秒")]
[assembly: AssemblyCompany("8bit Naifrog Pet")]
[assembly: AssemblyProduct("8bit奶蛙桌宠")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace Naifrog
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0] == "--self-test")
                {
                    using (Assets assets = new Assets()) Tests.Run(assets, args[1]); return 0;
                }
                bool first;
                using (Mutex mutex = new Mutex(true, "Local\\8bitNaifrogPet_v1", out first))
                {
                    if (!first)
                    {
                        MessageBox.Show("奶蛙已经在运行。请在系统托盘中找到奶蛙图标，双击恢复显示。", "8bit奶蛙桌宠"); return 0;
                    }
                    Native.SetProcessDPIAware(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    string report = args.Length == 2 && args[0] == "--smoke-test" ? args[1] : null;
                    using (Assets assets = new Assets())
                    using (PetWindow window = new PetWindow(assets, report)) Application.Run(window);
                }
                return 0;
            }
            catch (Exception)
            {
                if (args.Length >= 1 && args[0].StartsWith("--")) return 1;
                // No crash upload, log file, local path or stack trace is shown.
                MessageBox.Show("奶蛙无法启动。请确认使用的是完整发布文件，并安装了 Windows 自带的 .NET Framework 4.8。", "8bit奶蛙桌宠", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
    internal static class Tests
    {
        static readonly List<string> passed = new List<string>();
        static void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); passed.Add(name); }
        static void Advance(PetEngine engine, double seconds)
        { for (int i = 0; i < seconds * 120; i++) engine.Advance(1.0 / 120); }
        static List<string> Clicks(Action<ClickArbiter> run)
        {
            ClickArbiter c = new ClickArbiter(); List<string> result = new List<string>();
            c.Recognized += result.Add; run(c); return result;
        }
        public static void Run(Assets assets, string output)
        {
            passed.Clear();
            foreach (Clip c in assets.Clips.Values)
                Check(c.frames[0] == assets.Bases[c.start] && c.frames.Last() == assets.Bases[c.end], "endpoint_" + c.id);
            Check(assets.Clips.Values.Count(c => c.id.StartsWith("A") && c.frames.Length == 96 && c.fps == 24 && c.duration == 4) == 12, "12_actions_4_seconds_24fps");
            PetEngine routing = new PetEngine(assets);
            foreach (string a in assets.Bases.Keys) foreach (string b in assets.Bases.Keys)
            {
                string state = a;
                foreach (string id in routing.Path(a, b)) { Check(assets.Clips[id].start == state, "route_" + a + b + id); state = assets.Clips[id].end; }
                Check(state == b, "reachable_" + a + b);
            }
            Point p = new Point(160, 200);
            Check(Clicks(c => { c.Add(0, p, "head"); c.Flush(.31); }).SequenceEqual(new[] { "A05" }), "head_single_wave");
            Check(Clicks(c => { c.Add(0, p, "head"); c.Add(.2, p, "head"); c.Flush(.5); }).SequenceEqual(new[] { "A09" }), "double_cancels_single");
            Check(Clicks(c => { c.Add(0, p, "belly"); c.Add(.2, p, "belly"); c.Add(.4, p, "belly"); c.Flush(1.3); }).SequenceEqual(new[] { "A04" }), "belly_triple_cancels_double");
            Check(Clicks(c => { c.Add(0, p, "belly"); c.Add(.2, p, "belly"); c.Flush(1.3); }).SequenceEqual(new[] { "A09" }), "belly_double_waits_for_triple_window");
            Check(Clicks(c => { c.Add(0, p, "belly"); c.Add(.8, p, "belly"); c.Flush(1.3); }).SequenceEqual(new[] { "press" }), "spaced_belly_clicks_are_not_double");
            Check(Clicks(c => { c.Add(0, p, "belly"); c.Cancel(); c.Flush(2); }).Count == 0, "drag_consumes_click");
            Check(Clicks(c => { c.Add(0, p, "head"); c.Add(.2, new Point(190, 200), "head"); c.Flush(.6); }).All(s => s != "A09"), "double_has_distance_limit");
            foreach (Clip clip in assets.Clips.Values)
                foreach (double phase in new[] { .25, .5, .75 })
                {
                    PetEngine e = new PetEngine(assets); e.SeekForTest(clip.id, (int)(clip.frames.Length * phase));
                    string before = e.Key; e.BeginDrag(); Advance(e, .5);
                    Check(e.Key == before && e.Dragging, "capture_freezes_" + clip.id + "_" + phase);
                    using (Bitmap source = SpriteRender.Normal(assets.Get(before), false))
                    {
                        Rectangle occupied = SpriteRender.Bounds(source);
                        PointF grab = new PointF(occupied.Left + occupied.Width / 2f, occupied.Top + occupied.Height / 3f);
                        foreach (double direction in new[] { -.12, .12 })
                            using (Bitmap warped = SpriteRender.Drag(source, grab, 1, direction))
                            {
                                Rectangle visible = SpriteRender.Bounds(warped);
                                Check(!visible.IsEmpty && visible.Left > 0 && visible.Top > 0 && visible.Right < SpriteRender.Canvas && visible.Bottom < SpriteRender.Canvas,
                                    "drag_bounds_" + clip.id + "_" + phase + "_" + direction);
                            }
                    }
                    e.EndDrag(false); Advance(e, 10);
                    Check(!e.Dragging && e.Current.end == "S", "release_settles_" + clip.id + "_" + phase);
                }
            PetEngine wake = new PetEngine(assets); wake.SeekForTest("A06", 40); List<string> wakePath = new List<string>(); wake.Started += wakePath.Add;
            wake.Request("A09", true); wake.Request("A04", true); Advance(wake, 10);
            Check(wakePath.Contains("T04") && wakePath.Contains("T02") && wakePath.Contains("A05") && !wakePath.Contains("A09") && !wakePath.Contains("A04"), "sleep_input_only_wakes_and_waves");
            PetEngine idle = new PetEngine(assets); Advance(idle, 326);
            Check(idle.Current.end == "Z", "automatic_sit_then_sleep");
            PetEngine queued = new PetEngine(assets); queued.SeekForTest("A09", 24); queued.Request("A05", true); queued.Request("A04", true);
            List<string> actions = new List<string>(); queued.Started += actions.Add; Advance(queued, 8);
            Check(actions.Contains("A04") && !actions.Contains("A05"), "latest_request_replaces_old_queue");
            PetEngine game = new PetEngine(assets); game.AutoIdle = false; game.StartGame(); Advance(game, 6); game.Request("A09", true); Advance(game, 7);
            Check(game.Current.end == "G", "game_success_returns_to_stance"); game.StopGame(); Advance(game, 4); Check(game.Current.end == "S", "game_stop_returns_to_stand");
            PetEngine paused = new PetEngine(assets); paused.Paused = true; string key = paused.Key; Advance(paused, 10);
            Check(paused.Key == key && paused.Now == 0, "pause_freezes_timeline");
            string folder = Path.GetDirectoryName(Path.GetFullPath(output)); Directory.CreateDirectory(folder);
            using (Bitmap normal = SpriteRender.Normal(assets.Get("rest-00"), false))
            {
                Rectangle bounds = SpriteRender.Bounds(normal);
                Check(bounds.Height > 100 && bounds.Bottom < 384, "normal_render_has_alpha_margin");
                normal.Save(Path.Combine(folder, "render-normal.png"));
                using (Bitmap deform = SpriteRender.Drag(normal, new PointF(192, 190), .8, -.08))
                { Check(!SpriteRender.Bounds(deform).IsEmpty, "drag_render_visible"); deform.Save(Path.Combine(folder, "render-drag.png")); }
                using (Bitmap identity = SpriteRender.Drag(normal, new PointF(192, 190), 0, 0))
                {
                    bool same = true;
                    for (int y = 0; y < normal.Height && same; y++) for (int x = 0; x < normal.Width; x++)
                        if (normal.GetPixel(x, y).ToArgb() != identity.GetPixel(x, y).ToArgb()) { same = false; break; }
                    Check(same, "drag_zero_deformation_is_pixel_identical");
                }
            }
            File.WriteAllText(output, "{\"passed\":" + passed.Count + ",\"failed\":0,\"checks\":[" + string.Join(",", passed.Select(s => "\"" + s + "\"")) + "]}");
        }
    }
}
