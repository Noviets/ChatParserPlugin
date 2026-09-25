using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Advanced_Combat_Tracker;

namespace ChatParser.Tests
{
    // Hosts the real plugin control the way ACT does (a TabPage inside a shown Form) and drives
    // it through the same entry points ACT uses: BuildUi, OnLogLineRead and ParseFilesWorker.
    // Non-public members are reached by reflection so the same tests run against the old and
    // the new source.
    internal sealed class Harness : IDisposable
    {
        internal const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public readonly Form Form;
        public readonly TabPage Host;
        public readonly ChatParserPlugin P;

        public Harness()
        {
            Form = new Form();
            Form.Text = "ChatParser test host";
            Form.StartPosition = FormStartPosition.Manual;
            Form.Location = new Point(60, 60);
            Form.Size = new Size(1100, 720);
            Form.ShowInTaskbar = false;

            TabControl outer = new TabControl();
            outer.Dock = DockStyle.Fill;
            Host = new TabPage("Chat Parser");
            outer.TabPages.Add(Host);
            Form.Controls.Add(outer);

            P = new ChatParserPlugin();
            Call("BuildUi");
            Host.Controls.Add(P);
            P.Dock = DockStyle.Fill;
            IntPtr forceHandle = P.Handle; // as InitPlugin does
            Form.Show();
            Pump(150);
        }

        public void Dispose()
        {
            Form.Close();
            Form.Dispose();
            Pump(20);
        }

        // ------------------------------------------------------------ reflection

        public object Call(string method, params object[] args)
        {
            MethodInfo m = typeof(ChatParserPlugin).GetMethod(method, Any);
            if (m == null) throw new MissingMethodException("ChatParserPlugin." + method + " does not exist");
            try { return m.Invoke(m.IsStatic ? null : P, args); }
            catch (TargetInvocationException e) { throw new Exception(method + " threw: " + e.InnerException, e.InnerException); }
        }

        public object Field(string name)
        {
            FieldInfo f = typeof(ChatParserPlugin).GetField(name, Any);
            if (f == null) throw new MissingFieldException("ChatParserPlugin." + name + " does not exist");
            return f.GetValue(f.IsStatic ? null : P);
        }

        public static bool SetStatic(string name, object value)
        {
            FieldInfo f = typeof(ChatParserPlugin).GetField(name, Any);
            if (f == null || !f.IsStatic) return false;
            f.SetValue(null, value);
            return true;
        }

        public static object GetStatic(string name)
        {
            FieldInfo f = typeof(ChatParserPlugin).GetField(name, Any);
            return f == null ? null : f.GetValue(null);
        }

        public TabControl Main { get { return (TabControl)Field("_tcMain"); } }
        public TabControl Tells { get { return (TabControl)Field("_tcTells"); } }
        public Label Stats { get { return (Label)Field("_lblStats"); } }
        public TextBox FilterBox { get { return (TextBox)Field("_txtFilter"); } }
        public CheckBox AutoScroll { get { return (CheckBox)Field("_chkAutoScroll"); } }

        public View V(string key)
        {
            IDictionary d = (IDictionary)Field("_views");
            return d.Contains(key) ? new View(d[key]) : null;
        }

        public List<string> ViewKeys()
        {
            IDictionary d = (IDictionary)Field("_views");
            return d.Keys.Cast<string>().ToList();
        }

        public List<View> AllViews()
        {
            IDictionary d = (IDictionary)Field("_views");
            return d.Values.Cast<object>().Select(o => new View(o)).ToList();
        }

        public CheckBox FindCheckBox(string text)
        {
            return All(P).OfType<CheckBox>().FirstOrDefault(c => c.Text == text);
        }

        public static IEnumerable<Control> All(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (Control d in All(c)) yield return d;
            }
        }

        public TabPage MainPage(string text)
        {
            return Main.TabPages.Cast<TabPage>().FirstOrDefault(p => p.Text == text);
        }

        public List<string> TellTabOrder()
        {
            return Tells.TabPages.Cast<TabPage>().Skip(1).Select(p => p.Text).ToList();
        }

        public ChatView CurrentBox()
        {
            object v = Call("CurrentView");
            return v == null ? null : new ChatView(new View(v));
        }

        // ------------------------------------------------------------ driving

        /// <summary>A live line through ACT's OnLogLineRead hook, then the UI queue drained.</summary>
        public void Live(string line, bool isImport = false)
        {
            Call("OnLogLineRead", isImport, new LogLineEventArgs(line, 0, DateTime.Now, "", false));
            WaitForLiveQueue();
        }

        /// <summary>Pumps until the plugin has taken every queued live line (it batches them on a short timer).</summary>
        public void WaitForLiveQueue()
        {
            FieldInfo queued = typeof(ChatParserPlugin).GetField("_drainQueued", Any);
            if (queued != null && !PumpUntil(delegate { return !(bool)queued.GetValue(P); }, 5000))
                throw new TimeoutException("live lines still queued after 5 s");
            Flush();
        }

        public sealed class LoadResult
        {
            public long ElapsedMs, MaxUiGapMs;
            public string Status;
            public override string ToString() { return ElapsedMs + " ms total, longest UI freeze " + MaxUiGapMs + " ms; \"" + Status + "\""; }
        }

        /// <summary>Backlog load through ParseFilesWorker on a worker thread, as the Load button does.</summary>
        public LoadResult Load(params string[] files)
        {
            Stats.Text = "";
            Stopwatch sw = Stopwatch.StartNew();
            long last = 0, maxGap = 0;
            System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
            tick.Interval = 10;
            tick.Tick += delegate
            {
                long now = sw.ElapsedMilliseconds;
                maxGap = Math.Max(maxGap, now - last);
                last = now;
            };
            tick.Start();
            Exception err = null;
            Thread t = new Thread(delegate()
            {
                try { Call("ParseFilesWorker", new object[] { files }); }
                catch (Exception e) { err = e; }
            });
            t.IsBackground = true;
            t.Start();
            bool done = PumpUntil(delegate { return Stats.Text.StartsWith("Loaded ") || err != null; }, 900000);
            long total = sw.ElapsedMilliseconds;
            tick.Stop();
            tick.Dispose();
            maxGap = Math.Max(maxGap, total - last);
            if (err != null) throw err;
            if (!done) throw new TimeoutException("backlog load never finished: " + Stats.Text);
            Flush();
            LoadResult r = new LoadResult();
            r.ElapsedMs = total;
            r.MaxUiGapMs = maxGap;
            r.Status = Stats.Text;
            return r;
        }

        public sealed class ImportResult
        {
            public bool Done;
            public long ElapsedMs, FeedMs, MaxUiGapMs;
            public int Gen2Collections;
            public override string ToString() { return "all drawn after " + ElapsedMs + " ms (ACT thread fed lines for " + FeedMs + " ms), longest UI freeze " + MaxUiGapMs + " ms, gen2 GCs during import " + Gen2Collections; }
        }

        /// <summary>
        /// An ACT log import: ACT's reader thread raises OnLogLineRead(isImport: true) for every
        /// line as fast as it reads the file. Pumps the UI until done() holds.
        /// </summary>
        public ImportResult ActImport(IList<string> lines, Func<bool> done, int linesPerSecond = 0)
        {
            int gen2 = GC.CollectionCount(2);
            Stopwatch sw = Stopwatch.StartNew();
            long last = 0, maxGap = 0, feedMs = -1;
            System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
            tick.Interval = 10;
            tick.Tick += delegate
            {
                long now = sw.ElapsedMilliseconds;
                maxGap = Math.Max(maxGap, now - last);
                last = now;
            };
            tick.Start();
            MethodInfo hook = typeof(ChatParserPlugin).GetMethod("OnLogLineRead", Any);
            Exception err = null;
            Thread t = new Thread(delegate()
            {
                try
                {
                    for (int i = 0; i < lines.Count; i++)
                    {
                        // Optional steady pace, as when ACT reads a file while also parsing combat.
                        if (linesPerSecond > 0)
                            while (sw.ElapsedMilliseconds * linesPerSecond < i * 1000L) Thread.Sleep(0);
                        hook.Invoke(P, new object[] { true, new LogLineEventArgs(lines[i], 0, DateTime.Now, "", false) });
                    }
                }
                catch (Exception e) { err = e; }
                feedMs = sw.ElapsedMilliseconds;
            });
            t.IsBackground = true;
            t.Start();
            bool ok = PumpUntil(delegate { return err != null || (!t.IsAlive && done()); }, 300000);
            ImportResult r = new ImportResult();
            r.ElapsedMs = sw.ElapsedMilliseconds;
            tick.Stop();
            tick.Dispose();
            r.MaxUiGapMs = Math.Max(maxGap, r.ElapsedMs - last);
            r.FeedMs = feedMs;
            r.Gen2Collections = GC.CollectionCount(2) - gen2;
            if (err != null) throw err;
            r.Done = ok;
            Flush();
            return r;
        }

        public void Select(TabPage page)
        {
            TabControl tc = (TabControl)page.Parent;
            if (tc == Tells) Main.SelectedTab = (TabPage)Tells.Parent;
            tc.SelectedTab = page;
        }

        public static void Flush()
        {
            for (int i = 0; i < 5; i++) Application.DoEvents();
        }

        public static void Pump(int ms)
        {
            Stopwatch sw = Stopwatch.StartNew();
            do { Application.DoEvents(); Thread.Sleep(2); } while (sw.ElapsedMilliseconds < ms);
        }

        public static bool PumpUntil(Func<bool> cond, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (!cond())
            {
                if (sw.ElapsedMilliseconds > timeoutMs) return false;
                Application.DoEvents();
                Thread.Sleep(1);
            }
            return true;
        }

        // ------------------------------------------------------------ screen

        public Bitmap Capture()
        {
            Form.TopMost = true;
            Form.Activate();
            Form.BringToFront();
            Pump(300);
            Rectangle r = CaptureArea;
            Bitmap bmp = new Bitmap(r.Width, r.Height);
            using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            Form.TopMost = false;
            return bmp;
        }

        // Inside of the test window only: Form.Bounds includes invisible resize borders that
        // show whatever is on the desktop behind the window.
        private Rectangle CaptureArea
        {
            get { return Form.RectangleToScreen(Form.ClientRectangle); }
        }

        /// <summary>Pixel of a screenshot taken by Capture, addressed in a control's client coordinates.</summary>
        public Color PixelAt(Bitmap shot, Control c, Point clientPt)
        {
            Point s = c.PointToScreen(clientPt);
            return shot.GetPixel(s.X - CaptureArea.X, s.Y - CaptureArea.Y);
        }
    }

    internal sealed class View
    {
        private readonly object _v;
        public View(object v) { _v = v; }
        private object F(string n) { return _v.GetType().GetField(n, Harness.Any).GetValue(_v); }
        public RichTextBox Box { get { return (RichTextBox)F("Box"); } }
        public TabPage Page { get { return (TabPage)F("Page"); } }
        public IList Messages { get { return (IList)F("Messages"); } }
        public bool NeedsRender { get { return (bool)F("NeedsRender"); } }
        public List<RefMsg> Msgs() { return Messages.Cast<object>().Select(Harness2.ToRef).ToList(); }
    }

    internal sealed class ChatView
    {
        public readonly View View;
        public ChatView(View v) { View = v; }
        public RichTextBox Box { get { return View.Box; } }
    }

    internal static class Harness2
    {
        public static RefMsg ToRef(object m)
        {
            Type t = m.GetType();
            RefMsg r = new RefMsg();
            r.Time = (DateTime)t.GetField("Time", Harness.Any).GetValue(m);
            r.ChannelKey = (string)t.GetField("ChannelKey", Harness.Any).GetValue(m);
            r.Speaker = (string)t.GetField("Speaker", Harness.Any).GetValue(m);
            r.Text = (string)t.GetField("Text", Harness.Any).GetValue(m);
            r.Outgoing = (bool)t.GetField("Outgoing", Harness.Any).GetValue(m);
            return r;
        }
    }

    // ---------------------------------------------------------------- RichTextBox probes

    internal static class Rtb
    {
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private const int EM_GETFIRSTVISIBLELINE = 0xCE;
        private const int WM_GETFONT = 0x31;

        /// <summary>The HFONT the native window draws with; zero means the bold system font.</summary>
        public static IntPtr NativeFont(Control c)
        {
            return SendMessage(c.Handle, WM_GETFONT, IntPtr.Zero, IntPtr.Zero);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CHARFORMAT2
        {
            public int cbSize;
            public uint dwMask, dwEffects;
            public int yHeight, yOffset, crTextColor;
            public byte bCharSet, bPitchAndFamily;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szFaceName;
            public short wWeight, sSpacing;
            public int crBackColor, lcid, dwReserved;
            public short sStyle, wKerning;
            public byte bUnderlineType, bAnimation, bRevAuthor, bReserved1;
        }

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendCharFormat(IntPtr hWnd, int msg, IntPtr wParam, ref CHARFORMAT2 lParam);
        private const int EM_GETCHARFORMAT = 0x43A, SCF_SELECTION = 1;
        private const uint CFM_LINK = 0x20, CFE_LINK = 0x20;

        /// <summary>Whether RichEdit's URL detection marked the first char of the last occurrence of needle as a link.</summary>
        public static bool IsLink(RichTextBox b, string needle)
        {
            int at = b.Text.LastIndexOf(needle, StringComparison.Ordinal);
            if (at < 0) throw new Exception("\"" + needle + "\" not in box text");
            b.Select(at + 1, 1);
            CHARFORMAT2 cf = new CHARFORMAT2();
            cf.cbSize = Marshal.SizeOf(typeof(CHARFORMAT2));
            cf.dwMask = CFM_LINK;
            SendCharFormat(b.Handle, EM_GETCHARFORMAT, (IntPtr)SCF_SELECTION, ref cf);
            return (cf.dwEffects & CFE_LINK) != 0;
        }

        public static int FirstVisibleLine(RichTextBox b)
        {
            return (int)SendMessage(b.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
        }

        public static int LastLine(RichTextBox b)
        {
            return b.GetLineFromCharIndex(b.TextLength);
        }

        /// <summary>Display line at the bottom edge of the visible area.</summary>
        public static int BottomVisibleLine(RichTextBox b)
        {
            int idx = b.GetCharIndexFromPosition(new Point(4, b.ClientSize.Height - 4));
            return b.GetLineFromCharIndex(idx);
        }

        /// <summary>
        /// True when the newest message line is on screen and, when the text is taller than the
        /// box, sits at the bottom edge of a full view (not scrolled up past the end with blank
        /// space below). The old code leaves one empty line after it, which is allowed for.
        /// </summary>
        public static bool ShowsLastMessage(RichTextBox b, string lastMessageText)
        {
            string text = b.Text;
            int at = text.LastIndexOf(lastMessageText, StringComparison.Ordinal);
            if (at < 0) return false;
            int msgLine = b.GetLineFromCharIndex(at);
            if (FirstVisibleLine(b) > msgLine || msgLine > BottomVisibleLine(b)) return false;
            if (FirstVisibleLine(b) == 0) return true; // everything fits
            int lineHeight = b.GetPositionFromCharIndex(b.GetFirstCharIndexFromLine(1)).Y - b.GetPositionFromCharIndex(0).Y;
            int y = b.GetPositionFromCharIndex(at).Y;
            return lineHeight > 0 && y >= b.ClientSize.Height - 3 * lineHeight;
        }

        /// <summary>Color of the first character of the last occurrence of needle.</summary>
        public static Color ColorOf(RichTextBox b, string needle)
        {
            int at = b.Text.LastIndexOf(needle, StringComparison.Ordinal);
            if (at < 0) throw new Exception("\"" + needle + "\" not in box text");
            int ss = b.SelectionStart, sl = b.SelectionLength;
            b.Select(at, 1);
            Color c = b.SelectionColor;
            b.Select(ss, sl);
            return c;
        }

        public static double Luminance(Color c)
        {
            Func<int, double> lin = v => { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); };
            return 0.2126 * lin(c.R) + 0.7152 * lin(c.G) + 0.0722 * lin(c.B);
        }

        public static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }
    }

    // ---------------------------------------------------------------- fixture factory

    internal static class Fx
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static readonly DateTime T0 = new DateTime(2026, 5, 17, 20, 0, 0, DateTimeKind.Utc);
        public static string Dir;

        /// <summary>An EQ2 log line: "(unix)[Sun May 17 20:17:04 2026] body".</summary>
        public static string Line(DateTime utc, string body)
        {
            long unix = (long)(utc - Epoch).TotalSeconds;
            DateTime l = utc.ToLocalTime();
            string bracket = l.ToString("ddd MMM ", CultureInfo.InvariantCulture) + l.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2)
                + l.ToString(" HH:mm:ss yyyy", CultureInfo.InvariantCulture);
            return "(" + unix.ToString(CultureInfo.InvariantCulture) + ")[" + bracket + "] " + body;
        }

        public static string Stamp(DateTime utc) { return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"); }

        public static string PC(string n) { return "\\aPC -1 " + n + ":" + n + "\\/a"; }
        public static string TellIn(string n, string m) { return PC(n) + " tells you, \"" + m + "\""; }
        public static string TellOut(string n, string m) { return "You tell " + n + ", \"" + m + "\""; }
        public static string GroupIn(string n, string m) { return PC(n) + " says to the group, \"" + m + "\""; }
        public static string RaidIn(string n, string m) { return PC(n) + " says to the raid party, \"" + m + "\""; }
        public static string GuildIn(string n, string m) { return PC(n) + " says to the guild, \"" + m + "\""; }
        public static string GuildOut(string m) { return "You say to the guild, \"" + m + "\""; }
        public static string OfficerIn(string n, string m) { return PC(n) + " says to the officers, \"" + m + "\""; }
        public static string OocIn(string n, string m) { return PC(n) + " says out of character, \"" + m + "\""; }
        public static string ShoutIn(string n, string m) { return PC(n) + " shouts, \"" + m + "\""; }
        public static string AuctionIn(string n, string m) { return PC(n) + " auctions, \"" + m + "\""; }
        public static string SayIn(string n, string m) { return PC(n) + " says, \"" + m + "\""; }
        public static string ChanIn(string n, string chan, string m) { return PC(n) + " tells " + chan + " (3), \"" + m + "\""; }
        public static string ChanOut(string chan, string m) { return "You tell " + chan + " (3), \"" + m + "\""; }
        public static string Spam(int i) { return "YOU hit a sanctum chaperon for " + (1000 + i) + " crushing damage."; }

        public static string Write(string name, IEnumerable<string> lines, Encoding enc = null, string newline = "\r\n")
        {
            string path = Path.Combine(Dir, name);
            using (StreamWriter w = new StreamWriter(path, false, enc ?? Encoding.Default))
            {
                w.NewLine = newline;
                foreach (string l in lines) w.WriteLine(l);
            }
            return path;
        }

        /// <summary>n guild lines one second apart with combat spam between, like a raid night.</summary>
        public static string GuildBacklog(string name, int n, DateTime start)
        {
            List<string> lines = new List<string>();
            for (int i = 0; i < n; i++)
            {
                DateTime t = start.AddSeconds(i);
                lines.Add(Line(t, Spam(i)));
                lines.Add(Line(t, GuildIn("Speaker" + (i % 7), "guild line " + i + " lorem ipsum dolor sit amet, consectetur adipiscing")));
                lines.Add(Line(t, Spam(i + 1)));
            }
            return Write(name, lines);
        }
    }
}
