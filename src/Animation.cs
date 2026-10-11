using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace Naifrog
{
    [DataContract] public sealed class Clip
    {
        [DataMember] public string id;
        [DataMember] public string name;
        [DataMember] public string start;
        [DataMember] public string end;
        [DataMember] public string[] frames;
        [DataMember] public int fps;
        [DataMember] public double duration;
    }
    [DataContract] public sealed class BasePose
    {
        [DataMember] public string state;
        [DataMember] public string key;
    }
    [DataContract] public sealed class AssetManifest
    {
        [DataMember] public string version;
        [DataMember] public int fps;
        [DataMember] public Clip[] clips;
        [DataMember] public BasePose[] bases;
    }
    public sealed class Assets : IDisposable
    {
        readonly Stream resource;
        readonly ZipArchive archive;
        readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();
        readonly LinkedList<string> order = new LinkedList<string>();
        public readonly Dictionary<string, Clip> Clips;
        public readonly Dictionary<string, string> Bases;
        public Assets()
        {
            resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Naifrog.Animations");
            if (resource == null) throw new InvalidDataException("缺少动画素材。");
            archive = new ZipArchive(resource, ZipArchiveMode.Read, true);
            AssetManifest manifest;
            using (Stream stream = archive.GetEntry("manifest.json").Open())
                manifest = (AssetManifest)new DataContractJsonSerializer(typeof(AssetManifest)).ReadObject(stream);
            Clips = manifest.clips.ToDictionary(c => c.id);
            Bases = manifest.bases.ToDictionary(b => b.state, b => b.key);
            foreach (Clip c in Clips.Values)
            {
                if (c.fps != 24 || c.frames.Length == 0 ||
                    c.frames[0] != Bases[c.start] || c.frames[c.frames.Length - 1] != Bases[c.end])
                    throw new InvalidDataException("动画端点校验失败。");
                if (c.id.StartsWith("A") && (c.frames.Length != 96 || c.duration != 4))
                    throw new InvalidDataException("主动作应为4秒。");
                foreach (string key in c.frames)
                    if (archive.GetEntry("frames/" + key + ".png") == null)
                        throw new InvalidDataException("缺少动画帧。");
            }
        }
        public Bitmap Get(string key)
        {
            Bitmap bitmap;
            if (cache.TryGetValue(key, out bitmap))
            {
                order.Remove(key); order.AddLast(key); return bitmap;
            }
            using (Stream stream = archive.GetEntry("frames/" + key + ".png").Open())
            using (Image decoded = Image.FromStream(stream)) bitmap = new Bitmap(decoded);
            cache.Add(key, bitmap); order.AddLast(key);
            while (cache.Count > 96)
            {
                string oldest = order.First.Value; order.RemoveFirst();
                cache[oldest].Dispose(); cache.Remove(oldest);
            }
            return bitmap;
        }
        public void Dispose()
        {
            foreach (Bitmap b in cache.Values) b.Dispose();
            cache.Clear(); archive.Dispose(); resource.Dispose();
        }
    }

    // Gesture recognition uses only clicks delivered to the pet window.
    public sealed class ClickArbiter
    {
        int count;
        double first, due;
        Point firstPoint;
        bool allBelly;
        bool validDouble;
        string firstRegion;
        public bool Waiting { get { return count != 0; } }
        public event Action<string> Recognized;
        public void Cancel() { count = 0; validDouble = false; }
        void Emit(string action)
        {
            count = 0;
            if (Recognized != null) Recognized(action);
        }
        public void Add(double now, Point point, string region)
        {
            if (count > 0 && now > due) Flush(now);
            if (count == 0)
            {
                count = 1; first = now; firstPoint = point;
                allBelly = region == "belly"; firstRegion = region;
                validDouble = false;
                due = now + (allBelly ? 1.2 : .3); return;
            }
            double distance = Math.Sqrt(Math.Pow(point.X - firstPoint.X, 2) + Math.Pow(point.Y - firstPoint.Y, 2));
            if (!allBelly && (now - first > .3 || distance > 16))
            {
                Emit(firstRegion == "head" ? "A05" : "press");
                Add(now, point, region); return;
            }
            count++;
            if (count == 2) validDouble = now - first <= .3 && distance <= 16;
            allBelly = allBelly && region == "belly";
            if (allBelly && count >= 3 && now - first <= 1.2) Emit("A04");
            else if (!allBelly && count >= 2 && now - first <= .3 && distance <= 16) Emit("A09");
        }
        public void Flush(double now)
        {
            if (count == 0 || now < due) return;
            Emit(count >= 2 && validDouble ? "A09" : firstRegion == "head" ? "A05" : "press");
        }
    }

    public sealed class PetEngine
    {
        public readonly Assets Assets;
        readonly Queue<string> plan = new Queue<string>();
        readonly Dictionary<string, double> cooldown = new Dictionary<string, double>();
        readonly Random random = new Random();
        string pending;
        double expires, playhead, lastInteraction, nextLook, nextTransform;
        bool userPlan, pendingUser, waking, recover;
        public double Now { get; private set; }
        public bool Paused;
        public bool AutoIdle = true;
        public bool Game;
        public bool Dragging { get; private set; }
        public Clip Current { get; private set; }
        public int Index { get { return Math.Min(Current.frames.Length - 1, Math.Max(0, (int)(playhead * Current.fps))); } }
        public string Key { get { return Current.frames[Index]; } }
        public double IdleAge { get { return Now - lastInteraction; } }
        public int CompletedActions { get; private set; }
        public event Action<string> Started;
        public PetEngine(Assets assets)
        {
            Assets = assets; Set("A01"); nextLook = 25; nextTransform = 45;
        }
        void Set(string id)
        {
            Current = Assets.Clips[id]; playhead = 0;
            if (Started != null) Started(id);
        }
        static bool Loop(string id)
        {
            return new[] { "A01", "A02", "A03", "A06", "A07", "A08", "A11" }.Contains(id);
        }
        static double MotionEnd(string id)
        {
            switch (id)
            {
                case "A04": return 3.68; case "A05": return 2.55;
                case "A09": return 2.02; case "A10": return 3.02;
                case "A12": return 2.48; default: return 4;
            }
        }
        public List<string> Path(string start, string end)
        {
            if (start == end) return new List<string>();
            Queue<Tuple<string, List<string>>> queue = new Queue<Tuple<string, List<string>>>();
            HashSet<string> seen = new HashSet<string>(); seen.Add(start);
            queue.Enqueue(Tuple.Create(start, new List<string>()));
            while (queue.Count > 0)
            {
                var item = queue.Dequeue();
                foreach (Clip clip in Assets.Clips.Values.Where(c => c.id.StartsWith("T") && c.start == item.Item1))
                {
                    if (seen.Contains(clip.end)) continue;
                    List<string> route = new List<string>(item.Item2); route.Add(clip.id);
                    if (clip.end == end) return route;
                    seen.Add(clip.end); queue.Enqueue(Tuple.Create(clip.end, route));
                }
            }
            throw new InvalidDataException("没有可用的姿势衔接路径。");
        }
        void AddPath(string start, string end) { foreach (string id in Path(start, end)) plan.Enqueue(id); }
        void StartPending()
        {
            string id = pending; pending = null;
            userPlan = userPlan || pendingUser; pendingUser = false;
            Clip clip = Assets.Clips[id];
            AddPath(Current.end, clip.start); plan.Enqueue(id);
            string end = clip.end;
            if (id == "A02" || id == "A03" || id == "A08") { AddPath(end, "S"); end = "S"; }
            if (Game && id != "A07") AddPath(end, "G");
            if (plan.Count > 0) Set(plan.Dequeue());
        }
        public bool Request(string id, bool user)
        {
            if (!Assets.Clips.ContainsKey(id) || Dragging) return false;
            if (user)
            {
                lastInteraction = Now;
                if (waking) return false;
                if (Current.start == "Z" || Current.end == "Z") { id = "A05"; waking = true; }
                double until;
                if (cooldown.TryGetValue(id, out until) && Now < until) return false;
            }
            pending = id; expires = Now + 3; pendingUser = user;
            return true;
        }
        public void Hover()
        {
            if (Dragging || waking || Current.end == "Z") return;
            lastInteraction = Now;
            if (Current.end == "S" && !Game) Request("A10", true);
        }
        public void Touch() { lastInteraction = Now; }
        public void StartGame() { Game = true; Request("A07", true); }
        public void StopGame()
        {
            Game = false; plan.Clear(); pending = null;
            if (Current.end == "G") { pending = "A01"; expires = Now + 3; }
            lastInteraction = Now;
        }
        public void BeginDrag()
        {
            Dragging = true; pending = null; plan.Clear(); waking = false; recover = false;
            lastInteraction = Now;
        }
        public void EndDrag(bool fast)
        {
            Dragging = false; Game = false; plan.Clear();
            // Continue only the remaining settling frames from the captured pose.
            // Never replay an action from its first frame after release.
            recover = true; userPlan = true;
            AddPath(Current.end, "S");
            if (fast) plan.Enqueue("A12");
            pending = null; lastInteraction = Now;
        }
        string IdleClip(string state)
        {
            switch (state)
            {
                case "C": return "A11"; case "Z": return "A06";
                case "E": return "A02"; case "M": return "A03";
                case "G": return "A07"; case "D": return "A08"; default: return "A01";
            }
        }
        public void Advance(double dt)
        {
            if (Paused) return;
            Now += dt;
            if (Dragging) return;
            if (pending != null && Now > expires) { pending = null; waking = false; }
            double speed = recover ? 2 : pending != null && (Loop(Current.id) || playhead >= MotionEnd(Current.id)) ? 4 : 1;
            playhead += dt * speed;
            if (playhead >= Current.frames.Length / (double)Current.fps)
            {
                string finished = Current.id, state = Current.end;
                if (finished.StartsWith("A"))
                {
                    CompletedActions++;
                    double delay = finished == "A04" || finished == "A09" ? 3 : finished == "A05" ? 2 : finished == "A10" ? 5 : 0;
                    if (delay > 0) cooldown[finished] = Now + delay;
                }
                if (plan.Count > 0) { Set(plan.Dequeue()); return; }
                if (recover) { recover = false; lastInteraction = Now; }
                if (userPlan) { lastInteraction = Now; userPlan = false; waking = false; }
                if (pending != null) { StartPending(); return; }
                if (!Game && state == "G") { AddPath("G", "S"); Set(plan.Dequeue()); return; }
                Set(IdleClip(state));
            }
            if (!AutoIdle || pending != null || plan.Count > 0 || recover) return;
            if (Game)
            {
                if (IdleAge > 20) StopGame(); return;
            }
            if (Current.id != IdleClip(Current.end)) return;
            if (IdleAge >= 300 && Current.end == "C") { Request("A06", false); return; }
            if (IdleAge >= 90 && Current.end == "S") { Request("A11", false); return; }
            if (Current.end != "S") return;
            if (Now >= nextTransform && IdleAge >= 30)
            {
                Request(random.Next(2) == 0 ? "A02" : "A03", false);
                nextTransform = Now + 180; return;
            }
            if (Now >= nextLook && IdleAge > 15)
            {
                Request("A10", false); nextLook = Now + 20 + random.Next(21);
            }
        }
        public void Reset()
        {
            plan.Clear(); pending = null; Dragging = false; recover = false; waking = false;
            userPlan = false; Game = false; lastInteraction = Now; Set("A01");
        }
        internal void SeekForTest(string id, int index)
        {
            Reset(); AutoIdle = false; Set(id); playhead = index / (double)Current.fps;
        }
    }
}
