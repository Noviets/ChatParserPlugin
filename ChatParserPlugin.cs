using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using Advanced_Combat_Tracker;

// EQ2 Chat Parser plugin for Advanced Combat Tracker.
//
// Splits chat out of the EQ2 log stream into per-channel tabs (Tells / Group / Raid /
// Guild / Officers / Say / Shout / OOC / Auction / custom "#Name" channels). Tells get
// a nested tab per player holding both sides of the conversation, most recent
// conversation first, plus an "All Tells" chronological view. The "Latest" tab
// interleaves every channel, but only for chat that arrives live.
//
// Two input paths:
//   * Live - hooks ACT's OnLogLineRead, so whatever log ACT is following streams in.
//   * Backlog - "Load Log File(s)…" reads any eq2log_*.txt directly (no ACT import),
//     cutting the files into blocks that are parsed in parallel.
//
// This file is deliberately C# 5 only (no string interpolation, ?., expression bodies)
// so ACT's built-in compiler can load the .cs directly via Plugins -> Browse.
namespace ChatParser
{
    public class ChatParserPlugin : UserControl, IActPluginV1
    {
        // A full re-render (filter change / backlog load) draws at most this many of the
        // newest messages; older ones stay in memory and reachable via the filter box.
        private const int MaxRenderMessages = 4000;

        // A hidden tab gaining more live messages than this in one batch (an ACT log import) is
        // just marked for one redraw when it is next shown, rather than appended to.
        private const int MaxLiveAppend = 50;

        // Backlog files are read in blocks of about this many bytes, cut back to a line break,
        // and the blocks parsed in parallel. Internal so tests can shrink it to stress the seams.
        internal static int ParseBlockBytes = 1024 * 1024;

        // Tests point this at a temp file so they never touch the real ACT config.
        internal static string SettingsPathOverride = null;

        private const string LatestKey = "LATEST";
        private const string AllTellsKey = "TELLALL";

        private class ChatMessage
        {
            public DateTime Time;
            public long Seq;            // tie-breaker so sorts are deterministic within one second
            public string Speaker;      // other party for tells; sender for incoming; "" for other outgoing
            public string Text;
            public bool Outgoing;
            public string ChannelKey;   // "TELL", "Group", ..., or "CHAN:<name>"
        }

        private class ChannelView
        {
            public TabPage Page;
            public RichTextBox Box;
            public List<ChatMessage> Messages = new List<ChatMessage>();
            public bool NeedsRender;
            public bool ShowChannel;    // Latest tab: each line says which channel it came from
            public int DrawnLines;      // messages in Box; live appends grow it until a redraw trims it
            public DateTime LastTime = DateTime.MinValue; // newest message, for ordering tell tabs
            public long LastSeq;
        }

        // ---------------------------------------------------------------- parsing

        // "(1783263492)[Mon Jul  6 00:58:12 2026] body" — the unix prefix is authoritative.
        private static readonly Regex RxTimestamp = new Regex(
            @"^\((?<unix>\d{9,10})\)\[[^\]]{20,26}\]\s(?<body>.*)$", RegexOptions.Compiled);

        // EQ2 link markup: \aPC -1 Harlow:Harlow\/a, \aITEM 123 456 0 0 0:Arcane Greatwall\/a, \aNPC ...
        private static readonly Regex RxLink = new Regex(
            @"\\a(?<kind>[A-Z]+)\s[^:]*:(?<text>[^\\]*)\\/a", RegexOptions.Compiled);

        // Chat patterns run against the markup-cleaned body. Order matters only where noted.
        private static readonly Regex RxChanIn = new Regex(
            "^(?<name>\\S+) tells (?<chan>.+?) \\(\\d+\\), \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxChanOut = new Regex(          // before RxTellOut
            "^You tell (?<chan>.+?) \\(\\d+\\), \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxTellIn = new Regex(
            "^(?<name>\\S+) tells you, \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxTellOut = new Regex(
            "^You tell (?<name>\\S+), \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxGroupIn = new Regex(
            "^(?<name>\\S+) says to the group, \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxGroupOut = new Regex(
            "^You say to the group, \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxRaidIn = new Regex(
            "^(?<name>\\S+) says to the raid party, \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxRaidOut = new Regex(
            "^You say to the raid party, \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxGuildIn = new Regex(
            "^(?<name>\\S+) says to the guild, \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxGuildOut = new Regex(
            "^You say to the guild, \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxOfficerIn = new Regex(
            "^(?<name>\\S+) says to the officers, \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxOfficerOut = new Regex(
            "^You say to the officers, \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxOocIn = new Regex(           // before RxSayIn
            "^(?<name>\\S+) says out of character, \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxOocOut = new Regex(
            "^You say out of character, \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxShoutIn = new Regex(
            "^(?<name>\\S+) shouts, \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxShoutOut = new Regex(
            "^You shout, \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxAuctionIn = new Regex(
            "^(?<name>\\S+) auctions, \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxAuctionOut = new Regex(
            "^You auction, \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxSayIn = new Regex(
            "^(?<name>\\S+) says, \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxSayOut = new Regex(
            "^You say, \"(?<msg>.*)\"$", RegexOptions.Compiled);

        // ---------------------------------------------------------------- themes

        // Palette slots, which are also the RTF \colortbl indexes (index 0 is RTF's "auto").
        private const int CMuted = 1, COutgoing = 2, CText = 3, CTell = 4, CGroup = 5, CRaid = 6,
            CGuild = 7, COfficers = 8, CSay = 9, CShout = 10, COoc = 11, CAuction = 12, CCustom = 13,
            PaletteSize = 14;

        private sealed class Theme
        {
            public bool Dark;
            public readonly Color[] Palette = new Color[PaletteSize];
            public Color ChatBack, Chrome, ChromeFore, InputBack, ButtonBack, Border, TabBack, StatsFore;
        }

        private static Theme LightTheme()
        {
            Theme t = new Theme();
            t.ChatBack = Color.White;
            t.Palette[CMuted] = Color.Gray;
            t.Palette[COutgoing] = Color.RoyalBlue;
            t.Palette[CText] = Color.Black;
            t.Palette[CTell] = Color.DarkMagenta;
            t.Palette[CGroup] = Color.Teal;
            t.Palette[CRaid] = Color.DarkOrange;
            t.Palette[CGuild] = Color.Green;
            t.Palette[COfficers] = Color.SeaGreen;
            t.Palette[CSay] = Color.FromArgb(64, 64, 64);
            t.Palette[CShout] = Color.Firebrick;
            t.Palette[COoc] = Color.DarkCyan;
            t.Palette[CAuction] = Color.DarkGoldenrod;
            t.Palette[CCustom] = Color.SlateBlue;
            t.StatsFore = Color.DimGray;
            return t;
        }

        // White on black. Every label color keeps at least 4.5:1 contrast against the black.
        private static Theme DarkTheme()
        {
            Theme t = new Theme();
            t.Dark = true;
            t.ChatBack = Color.Black;
            t.Palette[CMuted] = Color.FromArgb(140, 140, 140);
            t.Palette[COutgoing] = Color.CornflowerBlue;
            t.Palette[CText] = Color.White;
            t.Palette[CTell] = Color.Violet;
            t.Palette[CGroup] = Color.Turquoise;
            t.Palette[CRaid] = Color.Orange;
            t.Palette[CGuild] = Color.LimeGreen;
            t.Palette[COfficers] = Color.PaleGreen;
            t.Palette[CSay] = Color.LightGray;
            t.Palette[CShout] = Color.Tomato;
            t.Palette[COoc] = Color.SkyBlue;
            t.Palette[CAuction] = Color.Gold;
            t.Palette[CCustom] = Color.MediumPurple;
            t.Chrome = Color.FromArgb(30, 30, 30);
            t.ChromeFore = Color.FromArgb(230, 230, 230);
            t.InputBack = Color.FromArgb(45, 45, 45);
            t.ButtonBack = Color.FromArgb(55, 55, 55);
            t.Border = Color.FromArgb(85, 85, 85);
            t.TabBack = Color.FromArgb(45, 45, 45);
            t.StatsFore = Color.FromArgb(160, 160, 160);
            return t;
        }

        // The native tab strip ignores BackColor, so in dark mode it is painted here instead.
        private sealed class ThemedTabControl : TabControl
        {
            private const ControlStyles DarkStyles = ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw;

            private bool _dark;
            public Color Strip, TabBack, TabSelected, TabFore, Border;

            public bool Dark
            {
                get { return _dark; }
                set
                {
                    _dark = value;
                    SetStyle(DarkStyles, value);
                    Invalidate();
                }
            }

            // WinForms only hands the native control its font while UserPaint is off, and the
            // native control sizes the tabs with that font, so both calls run unstyled.
            protected override void OnHandleCreated(EventArgs e)
            {
                SetStyle(DarkStyles, false);
                base.OnHandleCreated(e);
                SetStyle(DarkStyles, _dark);
            }

            protected override void OnFontChanged(EventArgs e)
            {
                SetStyle(DarkStyles, false);
                base.OnFontChanged(e);
                SetStyle(DarkStyles, _dark);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (!_dark)
                {
                    base.OnPaint(e);
                    return;
                }
                Graphics g = e.Graphics;
                using (SolidBrush strip = new SolidBrush(Strip)) g.FillRectangle(strip, ClientRectangle);
                using (Pen border = new Pen(Border))
                {
                    for (int i = 0; i < TabCount; i++)
                    {
                        Rectangle r = GetTabRect(i);
                        using (SolidBrush b = new SolidBrush(i == SelectedIndex ? TabSelected : TabBack)) g.FillRectangle(b, r);
                        g.DrawRectangle(border, r.X, r.Y, r.Width - 1, r.Height - 1);
                        TextRenderer.DrawText(g, TabPages[i].Text, Font, r, TabFore,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                            | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                    }
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private const int WM_SETREDRAW = 0x000B, WM_VSCROLL = 0x0115, SB_BOTTOM = 7;

        // ---------------------------------------------------------------- state

        private Label _actStatus;
        private long _seq;
        private long _total;
        private string _filter = string.Empty;
        private Theme _theme;
        private bool _suppressTabEvents;

        // Live lines parsed on ACT's thread, waiting for the UI thread to take them as a batch.
        private readonly object _pendingLock = new object();
        private List<PendingLine> _pending = new List<PendingLine>();
        private bool _drainQueued;
        private const int DrainIntervalMs = 50;
        private readonly Stopwatch _drainClock = Stopwatch.StartNew();
        private long _lastDrainMs = -DrainIntervalMs;
        private System.Windows.Forms.Timer _drainTimer;

        private FlowLayoutPanel _top;
        private ThemedTabControl _tcMain;
        private ThemedTabControl _tcTells;
        private TabPage _pageTells;
        private Button _btnLoad;
        private Button _btnClear;
        private TextBox _txtFilter;
        private CheckBox _chkAutoScroll;
        private CheckBox _chkDark;
        private Label _lblStats;
        private System.Windows.Forms.Timer _filterDebounce;
        private ContextMenuStrip _boxMenu;
        private Font _chatFont;

        private readonly Dictionary<string, ChannelView> _views =
            new Dictionary<string, ChannelView>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<TabPage, ChannelView> _pageToView =
            new Dictionary<TabPage, ChannelView>();

        private static readonly string[] FixedChannels =
            { "Group", "Raid", "Guild", "Officers", "Say", "Shout", "OOC", "Auction" };

        // ---------------------------------------------------------------- IActPluginV1

        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        {
            _actStatus = pluginStatusText;
            pluginScreenSpace.Text = "Chat Parser";

            BuildUi();
            pluginScreenSpace.Controls.Add(this);
            Dock = DockStyle.Fill;

            // Force handle creation so BeginInvoke works even before the tab is first shown.
            IntPtr forceHandle = Handle;

            ActGlobals.oFormActMain.OnLogLineRead += OnLogLineRead;
            _actStatus.Text = "Chat Parser started.";
        }

        public void DeInitPlugin()
        {
            ActGlobals.oFormActMain.OnLogLineRead -= OnLogLineRead;
            if (_actStatus != null) _actStatus.Text = "Chat Parser exited.";
        }

        // ---------------------------------------------------------------- settings

        private static string SettingsPath()
        {
            if (SettingsPathOverride != null) return SettingsPathOverride;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Advanced Combat Tracker\\Config\\ChatParserPlugin.config.xml");
        }

        /// <summary>Dark mode unless the settings file says otherwise.</summary>
        private static bool LoadDarkMode()
        {
            try
            {
                string path = SettingsPath();
                if (!File.Exists(path)) return true;
                XmlDocument doc = new XmlDocument();
                doc.Load(path);
                XmlNode n = doc.SelectSingleNode("/ChatParser/DarkMode");
                bool b;
                if (n != null && bool.TryParse(n.InnerText.Trim(), out b)) return b;
            }
            catch (Exception)
            {
                // Unreadable settings: fall back to the default.
            }
            return true;
        }

        private void SaveSettings()
        {
            try
            {
                string path = SettingsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                XmlDocument doc = new XmlDocument();
                XmlElement root = doc.CreateElement("ChatParser");
                doc.AppendChild(root);
                XmlElement dark = doc.CreateElement("DarkMode");
                dark.InnerText = _chkDark.Checked ? "true" : "false";
                root.AppendChild(dark);
                doc.Save(path);
            }
            catch (Exception)
            {
                // Settings are a convenience; never break the plugin over them.
            }
        }

        // ---------------------------------------------------------------- UI construction

        private void BuildUi()
        {
            _chatFont = new Font("Segoe UI", 9.5f);
            _theme = LoadDarkMode() ? DarkTheme() : LightTheme();

            _boxMenu = new ContextMenuStrip();
            _boxMenu.Items.Add("Copy", null, OnMenuCopy);
            _boxMenu.Items.Add("Select All", null, OnMenuSelectAll);

            _top = new FlowLayoutPanel();
            _top.Dock = DockStyle.Top;
            _top.AutoSize = true;
            _top.WrapContents = true;
            _top.Padding = new Padding(4, 4, 4, 0);

            _btnLoad = new Button();
            _btnLoad.Text = "Load Log File(s)…";
            _btnLoad.AutoSize = true;
            _btnLoad.Click += OnLoadClick;
            _top.Controls.Add(_btnLoad);

            _btnClear = new Button();
            _btnClear.Text = "Clear All";
            _btnClear.AutoSize = true;
            _btnClear.Click += OnClearClick;
            _top.Controls.Add(_btnClear);

            Label lblFilter = new Label();
            lblFilter.Text = "Filter:";
            lblFilter.AutoSize = true;
            lblFilter.Margin = new Padding(12, 8, 3, 0);
            _top.Controls.Add(lblFilter);

            _txtFilter = new TextBox();
            _txtFilter.Width = 180;
            _txtFilter.Margin = new Padding(0, 4, 3, 0);
            _txtFilter.TextChanged += OnFilterChanged;
            _top.Controls.Add(_txtFilter);

            _chkAutoScroll = new CheckBox();
            _chkAutoScroll.Text = "Auto-scroll";
            _chkAutoScroll.Checked = true;
            _chkAutoScroll.AutoSize = true;
            _chkAutoScroll.Margin = new Padding(10, 6, 3, 0);
            _top.Controls.Add(_chkAutoScroll);

            _chkDark = new CheckBox();
            _chkDark.Text = "Dark mode";
            _chkDark.Checked = _theme.Dark;
            _chkDark.AutoSize = true;
            _chkDark.Margin = new Padding(10, 6, 3, 0);
            _chkDark.CheckedChanged += OnDarkModeChanged;
            _top.Controls.Add(_chkDark);

            _lblStats = new Label();
            _lblStats.Text = "0 messages.";
            _lblStats.AutoSize = true;
            _lblStats.Margin = new Padding(14, 8, 3, 0);
            _top.Controls.Add(_lblStats);

            _tcMain = new ThemedTabControl();
            _tcMain.Dock = DockStyle.Fill;
            _tcMain.SelectedIndexChanged += OnAnyTabChanged;

            // Latest comes first so it is what the plugin opens on.
            ChannelView latest = NewView("Latest");
            latest.ShowChannel = true;
            _views[LatestKey] = latest;
            _tcMain.TabPages.Add(latest.Page);

            // Tells tab hosts a nested tab control: "All Tells" + one tab per player.
            _pageTells = new TabPage("Tells");
            _tcTells = new ThemedTabControl();
            _tcTells.Dock = DockStyle.Fill;
            _tcTells.SelectedIndexChanged += OnAnyTabChanged;
            _pageTells.Controls.Add(_tcTells);
            _tcMain.TabPages.Add(_pageTells);

            ChannelView allTells = NewView("All Tells");
            _views[AllTellsKey] = allTells;
            _tcTells.TabPages.Add(allTells.Page);

            for (int i = 0; i < FixedChannels.Length; i++)
            {
                ChannelView v = NewView(FixedChannels[i]);
                _views[FixedChannels[i]] = v;
                _tcMain.TabPages.Add(v.Page);
            }

            Controls.Add(_tcMain);
            Controls.Add(_top);

            _filterDebounce = new System.Windows.Forms.Timer();
            _filterDebounce.Interval = 350;
            _filterDebounce.Tick += OnFilterDebounce;

            _drainTimer = new System.Windows.Forms.Timer();
            _drainTimer.Tick += OnDrainTimer;

            ApplyTheme();
        }

        private ChannelView NewView(string title)
        {
            ChannelView v = new ChannelView();
            v.Page = new TabPage(title);

            v.Box = new RichTextBox();
            v.Box.Dock = DockStyle.Fill;
            v.Box.ReadOnly = true;
            v.Box.BorderStyle = BorderStyle.None;
            v.Box.Font = _chatFont;
            v.Box.HideSelection = false;
            v.Box.DetectUrls = true;
            v.Box.ContextMenuStrip = _boxMenu;
            v.Box.HandleCreated += OnBoxHandleCreated;
            ColorView(v);

            v.Page.Controls.Add(v.Box);
            _pageToView[v.Page] = v;
            return v;
        }

        private void ColorView(ChannelView v)
        {
            v.Box.BackColor = _theme.ChatBack;
            v.Box.ForeColor = _theme.Palette[CText];
            if (_theme.Dark) v.Page.BackColor = _theme.ChatBack;
            else v.Page.ResetBackColor();
        }

        /// <summary>Colors every control for the current theme and redraws the chat in it.</summary>
        private void ApplyTheme()
        {
            Theme t = _theme;
            if (t.Dark)
            {
                BackColor = t.Chrome;
                _top.BackColor = t.Chrome;
                _pageTells.BackColor = t.Chrome;
                _txtFilter.BackColor = t.InputBack;
                _txtFilter.ForeColor = t.ChromeFore;
                _txtFilter.BorderStyle = BorderStyle.FixedSingle;
            }
            else
            {
                ResetBackColor();
                _top.ResetBackColor();
                _pageTells.ResetBackColor();
                _txtFilter.ResetBackColor();
                _txtFilter.ResetForeColor();
                _txtFilter.BorderStyle = BorderStyle.Fixed3D;
            }

            foreach (Control c in _top.Controls)
            {
                if (c == _txtFilter) continue;
                Button b = c as Button;
                if (b != null && t.Dark)
                {
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = t.Border;
                    b.BackColor = t.ButtonBack;
                }
                else if (b != null)
                {
                    b.FlatStyle = FlatStyle.Standard;
                    b.ResetBackColor();
                    b.UseVisualStyleBackColor = true;
                }
                if (t.Dark) c.ForeColor = t.ChromeFore;
                else c.ResetForeColor();
            }
            _lblStats.ForeColor = t.StatsFore;

            ThemeTabs(_tcMain);
            ThemeTabs(_tcTells);

            // Colors are baked into each tab's text, so every tab is redrawn (lazily) in the new theme.
            foreach (KeyValuePair<string, ChannelView> kv in _views)
            {
                ColorView(kv.Value);
                kv.Value.NeedsRender = true;
            }
            RenderIfNeeded(CurrentView());
        }

        private void ThemeTabs(ThemedTabControl tc)
        {
            tc.Strip = _theme.Chrome;
            tc.TabBack = _theme.TabBack;
            tc.TabSelected = _theme.ChatBack;
            tc.TabFore = _theme.ChromeFore;
            tc.Border = _theme.Border;
            tc.Dark = _theme.Dark;
        }

        private ChannelView GetTellView(string player)
        {
            string key = "TELL:" + player;
            ChannelView v;
            if (_views.TryGetValue(key, out v)) return v;

            v = NewView(player);
            _views[key] = v;
            _tcTells.TabPages.Add(v.Page); // SortTellTabs moves it into place
            return v;
        }

        private ChannelView GetChannelView(string channelKey)
        {
            ChannelView v;
            if (_views.TryGetValue(channelKey, out v)) return v;

            // Only custom channels ("CHAN:<name>") are created dynamically.
            string display = "#" + channelKey.Substring(5);
            v = NewView(display);
            _views[channelKey] = v;
            _tcMain.TabPages.Add(v.Page);
            return v;
        }

        private List<ChannelView> TargetViews(ChatMessage m)
        {
            List<ChannelView> targets = new List<ChannelView>(3);
            if (m.ChannelKey == "TELL")
            {
                targets.Add(GetTellView(m.Speaker));
                targets.Add(_views[AllTellsKey]);
            }
            else
            {
                targets.Add(GetChannelView(m.ChannelKey));
            }
            return targets;
        }

        private static void Track(ChannelView v, ChatMessage m)
        {
            if (m.Time > v.LastTime || (m.Time == v.LastTime && m.Seq > v.LastSeq))
            {
                v.LastTime = m.Time;
                v.LastSeq = m.Seq;
            }
        }

        // Most recent conversation first; name order only breaks exact ties.
        private static int CompareRecency(ChannelView a, ChannelView b)
        {
            int c = b.LastTime.CompareTo(a.LastTime);
            if (c != 0) return c;
            c = b.LastSeq.CompareTo(a.LastSeq);
            return c != 0 ? c : string.Compare(a.Page.Text, b.Page.Text, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Orders the player tabs most recent conversation first; "All Tells" stays first.</summary>
        private void SortTellTabs()
        {
            int n = _tcTells.TabPages.Count;
            List<ChannelView> players = new List<ChannelView>(n);
            bool inOrder = true;
            for (int i = 1; i < n; i++)
            {
                ChannelView v = _pageToView[_tcTells.TabPages[i]];
                if (players.Count > 0 && CompareRecency(players[players.Count - 1], v) > 0) inOrder = false;
                players.Add(v);
            }
            if (inOrder) return;
            players.Sort(CompareRecency);

            // Remove and re-add rather than Insert: TabPages.Insert is unreliable before the
            // tab control has a handle, and the plugin tab may not have been opened yet.
            TabPage selected = _tcTells.SelectedTab;
            _suppressTabEvents = true;
            _tcTells.SuspendLayout();
            try
            {
                for (int i = n - 1; i >= 1; i--) _tcTells.TabPages.RemoveAt(i);
                for (int i = 0; i < players.Count; i++) _tcTells.TabPages.Add(players[i].Page);
                if (selected != null) _tcTells.SelectedTab = selected;
            }
            finally
            {
                _tcTells.ResumeLayout();
                _suppressTabEvents = false;
            }
            if (_tcMain.SelectedTab == _pageTells) RenderIfNeeded(CurrentView());
        }

        // ---------------------------------------------------------------- line parsing

        private static string CleanMarkup(string s)
        {
            if (s.IndexOf("\\a", StringComparison.Ordinal) < 0) return s;
            return RxLink.Replace(s, delegate(Match m)
            {
                string text = m.Groups["text"].Value;
                return m.Groups["kind"].Value == "ITEM" ? "[" + text + "]" : text;
            });
        }

        /// <summary>Parses one raw log line into a ChatMessage, or null for non-chat lines.</summary>
        private static ChatMessage ParseLine(string rawLine, DateTime fallbackTime)
        {
            if (string.IsNullOrEmpty(rawLine) || rawLine.Length < 30) return null;

            // Every chat line contains `, "`: cheap rejection for the combat-spam majority,
            // done before the timestamp regex so non-chat lines cost one scan and nothing else.
            if (rawLine.IndexOf(", \"", StringComparison.Ordinal) < 0) return null;

            DateTime time = fallbackTime;
            string body = rawLine;
            Match ts = RxTimestamp.Match(rawLine);
            if (ts.Success)
            {
                long unix;
                if (long.TryParse(ts.Groups["unix"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out unix))
                    time = UnixEpoch.AddSeconds(unix).ToLocalTime();
                body = ts.Groups["body"].Value;
            }

            if (body.IndexOf(", \"", StringComparison.Ordinal) < 0) return null;

            body = CleanMarkup(body);

            Match m;
            if ((m = RxChanOut.Match(body)).Success)
                return Msg(time, "CHAN:" + m.Groups["chan"].Value, "", m.Groups["msg"].Value, true);
            if ((m = RxChanIn.Match(body)).Success)
                return Msg(time, "CHAN:" + m.Groups["chan"].Value, m.Groups["name"].Value, m.Groups["msg"].Value, false);
            if ((m = RxTellIn.Match(body)).Success)
                return Msg(time, "TELL", m.Groups["name"].Value, m.Groups["msg"].Value, false);
            if ((m = RxTellOut.Match(body)).Success)
                return Msg(time, "TELL", m.Groups["name"].Value, m.Groups["msg"].Value, true);
            if ((m = RxGroupIn.Match(body)).Success)
                return Msg(time, "Group", m.Groups["name"].Value, m.Groups["msg"].Value, false);
            if ((m = RxGroupOut.Match(body)).Success)
                return Msg(time, "Group", "", m.Groups["msg"].Value, true);
            if ((m = RxRaidIn.Match(body)).Success)
                return Msg(time, "Raid", m.Groups["name"].Value, m.Groups["msg"].Value, false);
            if ((m = RxRaidOut.Match(body)).Success)
                return Msg(time, "Raid", "", m.Groups["msg"].Value, true);
            if ((m = RxGuildIn.Match(body)).Success)
                return Msg(time, "Guild", m.Groups["name"].Value, m.Groups["msg"].Value, false);
            if ((m = RxGuildOut.Match(body)).Success)
                return Msg(time, "Guild", "", m.Groups["msg"].Value, true);
            if ((m = RxOfficerIn.Match(body)).Success)
                return Msg(time, "Officers", m.Groups["name"].Value, m.Groups["msg"].Value, false);
            if ((m = RxOfficerOut.Match(body)).Success)
                return Msg(time, "Officers", "", m.Groups["msg"].Value, true);
            if ((m = RxOocIn.Match(body)).Success)
                return Msg(time, "OOC", m.Groups["name"].Value, m.Groups["msg"].Value, false);
            if ((m = RxOocOut.Match(body)).Success)
                return Msg(time, "OOC", "", m.Groups["msg"].Value, true);
            if ((m = RxShoutIn.Match(body)).Success)
                return Msg(time, "Shout", m.Groups["name"].Value, m.Groups["msg"].Value, false);
            if ((m = RxShoutOut.Match(body)).Success)
                return Msg(time, "Shout", "", m.Groups["msg"].Value, true);
            if ((m = RxAuctionIn.Match(body)).Success)
                return Msg(time, "Auction", m.Groups["name"].Value, m.Groups["msg"].Value, false);
            if ((m = RxAuctionOut.Match(body)).Success)
                return Msg(time, "Auction", "", m.Groups["msg"].Value, true);
            if ((m = RxSayIn.Match(body)).Success)
                return Msg(time, "Say", m.Groups["name"].Value, m.Groups["msg"].Value, false);
            if ((m = RxSayOut.Match(body)).Success)
                return Msg(time, "Say", "", m.Groups["msg"].Value, true);

            return null;
        }

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static ChatMessage Msg(DateTime time, string key, string speaker, string text, bool outgoing)
        {
            ChatMessage m = new ChatMessage();
            m.Time = time;
            m.ChannelKey = key;
            m.Speaker = speaker;
            m.Text = text;
            m.Outgoing = outgoing;
            return m;
        }

        // ---------------------------------------------------------------- live path

        private void OnLogLineRead(bool isImport, LogLineEventArgs logInfo)
        {
            // Runs on ACT's log thread: parse here (regexes are thread-safe), marshal only hits.
            ChatMessage m = ParseLine(logInfo.logLine, logInfo.detectedTime);
            if (m == null) return;
            m.Seq = Interlocked.Increment(ref _seq);

            // Queue, and wake the UI once per batch rather than once per line: an ACT log import
            // raises thousands of lines a second. Latest shows chat as it happens, so import
            // lines skip it.
            bool wake;
            lock (_pendingLock)
            {
                _pending.Add(new PendingLine(m, !isImport));
                wake = !_drainQueued;
                _drainQueued = true;
            }
            if (!wake) return;
            try
            {
                BeginInvoke(new Action(ScheduleDrain));
            }
            catch (Exception)
            {
                // Control disposed mid-shutdown; drop the lines.
            }
        }

        // A line after a quiet spell is shown at once. Under a flood, batches are taken at most
        // every DrainIntervalMs off a timer, whose low-priority message lets painting and input
        // through in between; posting the next batch straight away would starve both.
        private void ScheduleDrain()
        {
            long wait = DrainIntervalMs - (_drainClock.ElapsedMilliseconds - _lastDrainMs);
            if (wait <= 0)
            {
                DrainPending();
                return;
            }
            _drainTimer.Interval = (int)wait;
            _drainTimer.Start();
        }

        private void OnDrainTimer(object sender, EventArgs e)
        {
            _drainTimer.Stop();
            DrainPending();
        }

        private void DrainPending()
        {
            _lastDrainMs = _drainClock.ElapsedMilliseconds;
            List<PendingLine> batch;
            lock (_pendingLock)
            {
                batch = _pending;
                _pending = new List<PendingLine>();
                _drainQueued = false;
            }
            AddLiveMessages(batch);
        }

        private void AddLiveMessages(List<PendingLine> batch)
        {
            // Route everything first, remembering what each tab gained, then draw once per tab.
            Dictionary<ChannelView, List<ChatMessage>> added = new Dictionary<ChannelView, List<ChatMessage>>();
            List<ChannelView> touched = new List<ChannelView>();
            bool tells = false;
            for (int i = 0; i < batch.Count; i++)
            {
                ChatMessage m = batch[i].Message;
                _total++;
                if (m.ChannelKey == "TELL") tells = true;
                List<ChannelView> targets = TargetViews(m);
                if (batch[i].ToLatest) targets.Add(_views[LatestKey]);
                for (int t = 0; t < targets.Count; t++)
                {
                    ChannelView v = targets[t];
                    v.Messages.Add(m);
                    Track(v, m);
                    List<ChatMessage> list;
                    if (!added.TryGetValue(v, out list))
                    {
                        added[v] = list = new List<ChatMessage>();
                        touched.Add(v);
                    }
                    list.Add(m);
                }
            }

            ChannelView current = CurrentView();
            for (int i = 0; i < touched.Count; i++)
            {
                ChannelView v = touched[i];
                List<ChatMessage> list = added[v];
                if (v.NeedsRender) continue;               // will be redrawn from Messages anyway
                if (!v.Box.IsHandleCreated || (v != current && list.Count > MaxLiveAppend))
                {
                    v.NeedsRender = true;                  // no handle yet, or a hidden flood: redraw when shown
                    continue;
                }
                List<ChatMessage> shown = list;
                if (_filter.Length > 0)
                {
                    shown = new List<ChatMessage>();
                    for (int k = 0; k < list.Count; k++)
                        if (PassesFilter(list[k])) shown.Add(list[k]);
                }
                if (shown.Count > 0) AppendMessages(v, shown, _chkAutoScroll.Checked && v == current);
                // A long import keeps growing the shown tab; one redraw trims it to the newest lines.
                if (v.DrawnLines > 2 * MaxRenderMessages) v.NeedsRender = true;
            }
            if (tells) SortTellTabs();
            RenderIfNeeded(current);
            UpdateStats(null);
        }

        private struct PendingLine
        {
            public readonly ChatMessage Message;
            public readonly bool ToLatest;

            public PendingLine(ChatMessage message, bool toLatest)
            {
                Message = message;
                ToLatest = toLatest;
            }
        }

        // ---------------------------------------------------------------- backlog path

        private sealed class ParsedBlock
        {
            public List<ChatMessage> Messages;
            public long Lines;
            public string Error;
        }

        private sealed class ParseProgress
        {
            public long Total;
            public long Done;
            public long LastReportMs = -1000;
            public readonly Stopwatch Clock = Stopwatch.StartNew();
        }

        // One tab's share of a backlog load; First names the tab when it has to be created.
        private sealed class Bucket
        {
            public string Key;
            public ChatMessage First;
            public List<ChatMessage> Items = new List<ChatMessage>();
        }

        private void OnLoadClick(object sender, EventArgs e)
        {
            string[] files;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Select EQ2 log file(s) to parse";
                dlg.Filter = "EQ2 log files (eq2log_*.txt)|eq2log_*.txt|Text files (*.txt)|*.txt|All files (*.*)|*.*";
                dlg.Multiselect = true;
                if (Directory.Exists("C:\\EverQuest II\\logs")) dlg.InitialDirectory = "C:\\EverQuest II\\logs";
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                files = dlg.FileNames;
            }

            _btnLoad.Enabled = false;
            UpdateStats("Parsing…");
            ThreadPool.QueueUserWorkItem(delegate { ParseFilesWorker(files); });
        }

        // Reads the files sequentially in blocks and parses the blocks in parallel, then groups
        // and sorts per tab off the UI thread so ApplyBacklog only has to merge.
        private void ParseFilesWorker(string[] files)
        {
            ParseProgress progress = new ParseProgress();
            for (int f = 0; f < files.Length; f++)
            {
                try { progress.Total += new FileInfo(files[f]).Length; }
                catch (Exception) { }
            }

            List<Task<ParsedBlock>> blocks = new List<Task<ParsedBlock>>();
            SemaphoreSlim slots = new SemaphoreSlim(Math.Max(2, Math.Min(Environment.ProcessorCount, 8)));
            string error = null;
            try
            {
                for (int f = 0; f < files.Length; f++)
                    QueueFileBlocks(files[f], blocks, slots, progress);
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            Task[] pending = blocks.ToArray();
            try
            {
                while (!Task.WaitAll(pending, 250)) ReportProgress(progress);
            }
            catch (AggregateException) { } // each block records its own error below

            List<ChatMessage> parsed = new List<ChatMessage>();
            long lines = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i].Status != TaskStatus.RanToCompletion) continue;
                ParsedBlock b = blocks[i].Result;
                parsed.AddRange(b.Messages);
                lines += b.Lines;
                if (b.Error != null && error == null) error = b.Error;
            }

            // Seq in read order keeps same-second messages in file order, as before.
            long seq = Interlocked.Add(ref _seq, parsed.Count) - parsed.Count;
            for (int i = 0; i < parsed.Count; i++) parsed[i].Seq = ++seq;

            List<Bucket> buckets = GroupForViews(parsed);
            Parallel.ForEach(buckets, delegate(Bucket b) { b.Items.Sort(CompareMessages); });

            int count = parsed.Count;
            long totalLines = lines;
            int fileCount = files.Length;
            string err = error;
            double seconds = progress.Clock.Elapsed.TotalSeconds;
            try
            {
                BeginInvoke(new Action(delegate { ApplyBacklog(buckets, count, totalLines, fileCount, seconds, err); }));
            }
            catch (Exception) { }
        }

        private void QueueFileBlocks(string path, List<Task<ParsedBlock>> blocks, SemaphoreSlim slots, ParseProgress progress)
        {
            // Share-friendly open so the currently-written log can be read too.
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                1 << 16, FileOptions.SequentialScan))
            {
                byte[] head = new byte[4];
                int bom;
                Encoding enc = BlockEncoding(head, ReadFully(fs, head, 0, head.Length), out bom);
                if (enc == null)
                {
                    // UTF-16/32: a '\n' byte is not a line break there, so read it line by line.
                    blocks.Add(Task.Factory.StartNew<ParsedBlock>(delegate { return ParseWithReader(path); }));
                    return;
                }
                fs.Position = bom;

                byte[] carry = new byte[0];
                while (true)
                {
                    int size = Math.Max(1, ParseBlockBytes);
                    byte[] buf = new byte[carry.Length + size];
                    Buffer.BlockCopy(carry, 0, buf, 0, carry.Length);
                    int read = ReadFully(fs, buf, carry.Length, size);
                    int len = carry.Length + read;
                    bool eof = read < size;

                    // Blocks end on a line break; the partial line after it starts the next block.
                    int cut = eof ? len : LastNewline(buf, carry.Length, len) + 1;
                    if (cut == 0)
                    {
                        carry = buf; // no break yet: one line longer than a block, keep reading it
                    }
                    else
                    {
                        carry = new byte[len - cut];
                        Buffer.BlockCopy(buf, cut, carry, 0, carry.Length);
                        QueueBlock(buf, cut, enc, blocks, slots, progress);
                    }
                    ReportProgress(progress);
                    if (eof) break;
                }
            }
        }

        private static void QueueBlock(byte[] data, int length, Encoding enc, List<Task<ParsedBlock>> blocks,
            SemaphoreSlim slots, ParseProgress progress)
        {
            slots.Wait(); // bounds the blocks held in memory at once
            blocks.Add(Task.Factory.StartNew<ParsedBlock>(delegate
            {
                try
                {
                    return ParseBlock(data, length, enc);
                }
                catch (Exception ex)
                {
                    ParsedBlock failed = new ParsedBlock();
                    failed.Messages = new List<ChatMessage>();
                    failed.Error = ex.Message;
                    return failed;
                }
                finally
                {
                    Interlocked.Add(ref progress.Done, length);
                    slots.Release();
                }
            }));
        }

        private static ParsedBlock ParseBlock(byte[] data, int length, Encoding enc)
        {
            ParsedBlock r = new ParsedBlock();
            string s = enc.GetString(data, 0, length);
            r.Messages = new List<ChatMessage>();

            // Lines end at "\n", "\r\n" or a lone "\r", as StreamReader.ReadLine splits them
            // (real logs have "\r\r\n" endings and stray "\r" inside NPC dialog).
            for (int nl = s.IndexOf('\n'); nl >= 0; nl = s.IndexOf('\n', nl + 1)) r.Lines++;
            for (int cr = s.IndexOf('\r'); cr >= 0; cr = s.IndexOf('\r', cr + 1))
                if (cr + 1 == s.Length || s[cr + 1] != '\n') r.Lines++;
            if (s.Length > 0 && s[s.Length - 1] != '\n' && s[s.Length - 1] != '\r') r.Lines++;

            // Only lines holding the `, "` every chat line has are cut out and parsed.
            int pos = 0;
            while (pos < s.Length)
            {
                int hit = s.IndexOf(", \"", pos, StringComparison.Ordinal);
                if (hit < 0) break;
                int nlStart = s.LastIndexOf('\n', hit) + 1;
                int start = Math.Max(nlStart, s.LastIndexOf('\r', hit, hit - nlStart + 1) + 1);
                int end = s.IndexOf('\n', hit);
                if (end < 0) end = s.Length;
                int stop = s.IndexOf('\r', hit, end - hit);
                if (stop < 0) stop = end;
                ChatMessage m = ParseLine(s.Substring(start, stop - start), DateTime.MinValue);
                if (m != null) r.Messages.Add(m);
                pos = stop + 1; // after a lone "\r" the rest of the physical line is a line of its own
            }
            return r;
        }

        private static ParsedBlock ParseWithReader(string path)
        {
            ParsedBlock r = new ParsedBlock();
            r.Messages = new List<ChatMessage>();
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs, Encoding.Default, true))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        r.Lines++;
                        ChatMessage m = ParseLine(line, DateTime.MinValue);
                        if (m != null) r.Messages.Add(m);
                    }
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }
            return r;
        }

        // Same BOM detection StreamReader does. Null for UTF-16/32, whose line breaks span two
        // or four bytes and so cannot be found byte-wise.
        private static Encoding BlockEncoding(byte[] head, int len, out int bomLength)
        {
            bomLength = 0;
            if (len >= 2 && ((head[0] == 0xFE && head[1] == 0xFF) || (head[0] == 0xFF && head[1] == 0xFE))) return null;
            if (len >= 4 && head[0] == 0 && head[1] == 0 && head[2] == 0xFE && head[3] == 0xFF) return null;
            if (len >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                bomLength = 3;
                return new UTF8Encoding(false);
            }
            return Encoding.Default;
        }

        private static int ReadFully(Stream s, byte[] buf, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buf, offset + total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        private static int LastNewline(byte[] buf, int from, int to)
        {
            for (int i = to - 1; i >= from; i--)
                if (buf[i] == (byte)'\n') return i;
            return -1;
        }

        private void ReportProgress(ParseProgress p)
        {
            long now = p.Clock.ElapsedMilliseconds;
            if (now - p.LastReportMs < 250) return;
            p.LastReportMs = now;
            string text = "Parsing… " + (Interlocked.Read(ref p.Done) / 1048576).ToString("N0") + " of "
                + (p.Total / 1048576).ToString("N0") + " MB.";
            try
            {
                BeginInvoke(new Action(delegate { UpdateStats(text); }));
            }
            catch (Exception) { }
        }

        // Per-tab lists in first-seen order, so new custom channel tabs open in log order as before.
        private static List<Bucket> GroupForViews(List<ChatMessage> parsed)
        {
            Dictionary<string, Bucket> byKey = new Dictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);
            List<Bucket> order = new List<Bucket>();
            for (int i = 0; i < parsed.Count; i++)
            {
                ChatMessage m = parsed[i];
                if (m.ChannelKey == "TELL")
                {
                    AddToBucket(byKey, order, "TELL:" + m.Speaker, m);
                    AddToBucket(byKey, order, AllTellsKey, m);
                }
                else
                {
                    AddToBucket(byKey, order, m.ChannelKey, m);
                }
            }
            return order;
        }

        private static void AddToBucket(Dictionary<string, Bucket> byKey, List<Bucket> order, string key, ChatMessage m)
        {
            Bucket b;
            if (!byKey.TryGetValue(key, out b))
            {
                b = new Bucket();
                b.Key = key;
                b.First = m;
                byKey[key] = b;
                order.Add(b);
            }
            b.Items.Add(m);
        }

        private void ApplyBacklog(List<Bucket> buckets, int count, long lines, int fileCount, double seconds, string error)
        {
            for (int i = 0; i < buckets.Count; i++)
            {
                Bucket b = buckets[i];
                ChannelView v = b.Key.StartsWith("TELL:", StringComparison.Ordinal)
                    ? GetTellView(b.First.Speaker)
                    : GetChannelView(b.Key);
                v.Messages = MergeSorted(v.Messages, b.Items);
                Track(v, v.Messages[v.Messages.Count - 1]);
                v.NeedsRender = true;
            }
            SortTellTabs();

            _total += count;
            _btnLoad.Enabled = true;
            RenderIfNeeded(CurrentView());

            string status = "Loaded " + count.ToString("N0") + " chat messages from "
                + fileCount + " file(s), " + lines.ToString("N0") + " lines scanned in "
                + seconds.ToString("0.0") + " s.";
            if (error != null) status += " Stopped early: " + error;
            UpdateStats(status);
        }

        // Both lists are normally already in (Time, Seq) order, so merging keeps the tab sorted
        // without a full sort. A tab that is not (live lines from an ACT import) is re-sorted.
        private static List<ChatMessage> MergeSorted(List<ChatMessage> a, List<ChatMessage> b)
        {
            if (a.Count == 0) return b;
            List<ChatMessage> r = new List<ChatMessage>(a.Count + b.Count);
            bool aSorted = true;
            for (int k = 1; k < a.Count && aSorted; k++) aSorted = CompareMessages(a[k - 1], a[k]) <= 0;
            if (!aSorted)
            {
                r.AddRange(a);
                r.AddRange(b);
                r.Sort(CompareMessages);
                return r;
            }
            int i = 0, j = 0;
            while (i < a.Count && j < b.Count) r.Add(CompareMessages(a[i], b[j]) <= 0 ? a[i++] : b[j++]);
            for (; i < a.Count; i++) r.Add(a[i]);
            for (; j < b.Count; j++) r.Add(b[j]);
            return r;
        }

        private static int CompareMessages(ChatMessage a, ChatMessage b)
        {
            int c = a.Time.CompareTo(b.Time);
            return c != 0 ? c : a.Seq.CompareTo(b.Seq);
        }

        // ---------------------------------------------------------------- rendering

        private bool PassesFilter(ChatMessage m)
        {
            if (_filter.Length == 0) return true;
            if (m.Text.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return m.Speaker.Length > 0 && m.Speaker.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int ChannelSlot(string key)
        {
            switch (key)
            {
                case "TELL": return CTell;
                case "Group": return CGroup;
                case "Raid": return CRaid;
                case "Guild": return CGuild;
                case "Officers": return COfficers;
                case "Say": return CSay;
                case "Shout": return CShout;
                case "OOC": return COoc;
                case "Auction": return CAuction;
                default: return CCustom; // custom channels
            }
        }

        private static string ChannelTitle(string key)
        {
            if (key == "TELL") return "Tell";
            if (key.StartsWith("CHAN:", StringComparison.Ordinal)) return "#" + key.Substring(5);
            return key;
        }

        private static string SpeakerLabel(ChatMessage m)
        {
            if (m.ChannelKey == "TELL")
                return (m.Outgoing ? "To " : "From ") + m.Speaker + ":";
            return m.Outgoing ? "You:" : m.Speaker + ":";
        }

        private static string Stamp(ChatMessage m)
        {
            return m.Time == DateTime.MinValue
                ? "????-??-?? ??:??:??  "
                : m.Time.ToString("yyyy-MM-dd HH:mm:ss") + "  ";
        }

        /// <summary>
        /// Live path: adds messages to the end of a tab that is already drawn, as one RTF insert.
        /// Each separate append costs milliseconds once a tab holds a few thousand lines, even
        /// with painting off, which is what made the tab visibly crawl down.
        /// </summary>
        private void AppendMessages(ChannelView v, List<ChatMessage> list, bool scroll)
        {
            RichTextBox box = v.Box;
            string rtf = BuildRtf(list, 0, null, v.ShowChannel, box.TextLength > 0);
            SetRedraw(box, false);
            try
            {
                box.Select(box.TextLength, 0);
                box.SelectedRtf = rtf;
                if (scroll) ScrollToEnd(box);
            }
            finally
            {
                SetRedraw(box, true);
                box.Invalidate();
            }
            v.DrawnLines += list.Count;
        }

        /// <summary>Messages as one RTF document; after existing text when leadingBreak is set.</summary>
        private string BuildRtf(List<ChatMessage> list, int start, string notice, bool showChannel, bool leadingBreak)
        {
            Color[] pal = _theme.Palette;
            StringBuilder sb = new StringBuilder(512 + (list.Count - start) * 160);
            sb.Append(@"{\rtf1\ansi\deff0\uc1{\fonttbl{\f0\fnil ");
            AppendRtfText(sb, _chatFont.Name);
            sb.Append(@";}}{\colortbl ;");
            for (int i = 1; i < pal.Length; i++)
                sb.Append(@"\red").Append(pal[i].R).Append(@"\green").Append(pal[i].G).Append(@"\blue").Append(pal[i].B).Append(';');
            sb.Append(@"}\pard\f0\fs").Append((int)Math.Round(_chatFont.SizeInPoints * 2)).Append(' ');

            bool first = !leadingBreak;
            if (notice != null)
            {
                RtfRun(sb, CMuted, notice);
                first = false;
            }
            for (int i = start; i < list.Count; i++)
            {
                if (!first) sb.Append("\\par\r\n");
                first = false;
                ChatMessage m = list[i];
                int chan = ChannelSlot(m.ChannelKey);
                RtfRun(sb, CMuted, Stamp(m));
                if (showChannel) RtfRun(sb, chan, "[" + ChannelTitle(m.ChannelKey) + "] ");
                RtfRun(sb, m.Outgoing ? COutgoing : chan, SpeakerLabel(m) + " ");
                RtfRun(sb, CText, m.Text);
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static void RtfRun(StringBuilder sb, int color, string text)
        {
            sb.Append(@"\cf").Append(color).Append(' ');
            AppendRtfText(sb, text);
        }

        private static void AppendRtfText(StringBuilder sb, string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' || c == '{' || c == '}') sb.Append('\\').Append(c);
                else if (c == '\t') sb.Append(@"\tab ");
                else if (c < ' ') continue;
                else if (c < 0x80) sb.Append(c);
                else sb.Append(@"\u").Append((int)(short)c).Append('?');
            }
        }

        // Caret to the end, then the view to the bottom, natively. RichTextBox.ScrollToCaret
        // copies the whole text three times per call, which under a flood of live lines was
        // enough garbage to force a full GC several times a second.
        private static void ScrollToEnd(RichTextBox box)
        {
            box.Select(box.TextLength, 0);
            SendMessage(box.Handle, WM_VSCROLL, (IntPtr)SB_BOTTOM, IntPtr.Zero);
        }

        private static void SetRedraw(Control c, bool on)
        {
            if (c.IsHandleCreated) SendMessage(c.Handle, WM_SETREDRAW, (IntPtr)(on ? 1 : 0), IntPtr.Zero);
        }

        private bool RenderIfNeeded(ChannelView v)
        {
            if (v == null || !v.NeedsRender) return false;
            if (!v.Box.IsHandleCreated) return false; // HandleCreated hook will re-enter

            v.NeedsRender = false;
            RichTextBox box = v.Box;

            List<ChatMessage> list;
            if (_filter.Length == 0)
            {
                list = v.Messages;
            }
            else
            {
                list = new List<ChatMessage>();
                for (int i = 0; i < v.Messages.Count; i++)
                    if (PassesFilter(v.Messages[i])) list.Add(v.Messages[i]);
            }

            if (list.Count == 0)
            {
                box.Clear();
                v.DrawnLines = 0;
                return true;
            }

            int start = 0;
            string notice = null;
            if (list.Count > MaxRenderMessages)
            {
                start = list.Count - MaxRenderMessages;
                notice = "- showing last " + MaxRenderMessages.ToString("N0") + " of "
                    + list.Count.ToString("N0") + " messages; use Filter to narrow -";
            }

            // One RTF load instead of thousands of appends: the tab appears already drawn and
            // already scrolled, rather than visibly filling in line by line.
            string rtf = BuildRtf(list, start, notice, v.ShowChannel, false);
            v.DrawnLines = list.Count - start;
            SetRedraw(box, false);
            try
            {
                box.Rtf = rtf;
                if (_chkAutoScroll.Checked) ScrollToEnd(box);
            }
            finally
            {
                SetRedraw(box, true);
                box.Invalidate();
            }
            return true;
        }

        /// <summary>Brings the shown tab up to date and, with auto-scroll on, onto its newest line.</summary>
        private void ShowView(ChannelView v)
        {
            if (v == null) return;
            if (RenderIfNeeded(v)) return; // a render already scrolled
            if (_chkAutoScroll.Checked && v.Box.IsHandleCreated && v.Box.TextLength > 0) ScrollToEnd(v.Box);
        }

        private ChannelView CurrentView()
        {
            TabPage sel = _tcMain.SelectedTab;
            if (sel == null) return null;
            if (sel == _pageTells) sel = _tcTells.SelectedTab;
            if (sel == null) return null;
            ChannelView v;
            return _pageToView.TryGetValue(sel, out v) ? v : null;
        }

        private void UpdateStats(string text)
        {
            _lblStats.Text = text != null ? text : _total.ToString("N0") + " messages.";
        }

        // ---------------------------------------------------------------- UI events

        private void OnAnyTabChanged(object sender, EventArgs e)
        {
            if (_suppressTabEvents) return;
            ShowView(CurrentView());
        }

        private void OnBoxHandleCreated(object sender, EventArgs e)
        {
            // First time a tab is shown its RichTextBox gets a handle; draw anything deferred.
            RichTextBox box = (RichTextBox)sender;
            box.BeginInvoke(new Action(delegate { RenderIfNeeded(CurrentView()); }));
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && _tcMain != null) RenderIfNeeded(CurrentView());
        }

        private void OnDarkModeChanged(object sender, EventArgs e)
        {
            _theme = _chkDark.Checked ? DarkTheme() : LightTheme();
            ApplyTheme();
            SaveSettings();
        }

        private void OnFilterChanged(object sender, EventArgs e)
        {
            _filterDebounce.Stop();
            _filterDebounce.Start();
        }

        private void OnFilterDebounce(object sender, EventArgs e)
        {
            _filterDebounce.Stop();
            string f = _txtFilter.Text.Trim();
            if (f == _filter) return;
            _filter = f;
            foreach (KeyValuePair<string, ChannelView> kv in _views)
                kv.Value.NeedsRender = true;
            RenderIfNeeded(CurrentView());
        }

        private void OnClearClick(object sender, EventArgs e)
        {
            DialogResult r = MessageBox.Show(FindForm(),
                "Clear all parsed chat from every tab?", "Chat Parser",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
            ClearAll();
        }

        private void ClearAll()
        {
            foreach (KeyValuePair<string, ChannelView> kv in _views)
            {
                ChannelView v = kv.Value;
                v.Messages.Clear();
                v.NeedsRender = false;
                v.LastTime = DateTime.MinValue;
                v.LastSeq = 0;
                v.DrawnLines = 0;
                if (v.Box.IsHandleCreated) v.Box.Clear();
                else v.Box.Text = string.Empty;
            }
            _total = 0;
            UpdateStats(null);
        }

        private void OnMenuCopy(object sender, EventArgs e)
        {
            ChannelView v = CurrentView();
            if (v != null && v.Box.SelectionLength > 0) v.Box.Copy();
        }

        private void OnMenuSelectAll(object sender, EventArgs e)
        {
            ChannelView v = CurrentView();
            if (v != null) v.Box.SelectAll();
        }
    }
}
