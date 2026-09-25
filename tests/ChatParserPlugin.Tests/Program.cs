using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace ChatParser.Tests
{
    // Usage: ChatParserPlugin.Tests.exe [--only <substring>] [--no-real-logs]
    //        ChatParserPlugin.Tests.exe --bench [--verify] [--block <bytes>] <log files...>
    // Drives the real plugin UI in a test window. Exit code 0 = all pass.
    // Sections 1-5 map to the five requested changes; "guard" tests pin existing behaviour.
    // The real-log tests use the eq2log_*.txt paths listed one per line in real-logs.local.txt
    // (next to this project, git-ignored); without that file they are skipped.
    internal static class Program
    {
        private static int _failures, _passes, _skips;
        private static string _only;
        private static bool _realLogs = true;
        private static readonly List<string> Failed = new List<string>();

        private static string RealLogList
        {
            get { return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "real-logs.local.txt")); }
        }

        private static string[] RealLogs()
        {
            if (!File.Exists(RealLogList)) return new string[0];
            return File.ReadAllLines(RealLogList).Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#")).ToArray();
        }

        private static string ArtifactsDir
        {
            get { return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "artifacts")); }
        }

        private static string SettingsPath;

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Fx.Dir = Path.Combine(Path.GetTempPath(), "ChatParserPlugin.Tests", Process.GetCurrentProcess().Id.ToString());
            Directory.CreateDirectory(Fx.Dir);
            Directory.CreateDirectory(ArtifactsDir);

            // Never touch the real ACT config: point the plugin's settings file at the temp dir.
            SettingsPath = Path.Combine(Fx.Dir, "ChatParserPlugin.config.xml");
            Console.WriteLine("settings override: " + (Harness.SetStatic("SettingsPathOverride", SettingsPath) ? SettingsPath : "(plugin has no SettingsPathOverride)"));

            if (args.Length > 0 && args[0] == "--bench") return Bench(args.Skip(1).ToArray());
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--only" && i + 1 < args.Length) _only = args[++i];
                if (args[i] == "--no-real-logs") _realLogs = false;
            }

            try
            {
                Section("guards: parsing and routing", RoutingTests);
                Section("1. dark mode", DarkModeTests);
                Section("2. tab switch lands on the newest line instantly", TabSwitchTests);
                Section("3. backlog load speed", LoadSpeedTests);
                Section("4. tell tabs sorted by most recent", TellOrderTests);
                Section("5. Latest tab (live chat only)", LatestTests);
                Section("screenshots", Screenshots);
            }
            finally
            {
                try { Directory.Delete(Fx.Dir, true); } catch (Exception) { }
            }
            return Report();
        }

        // ================================================================ guards

        private static void RoutingTests()
        {
            Run("guard_each_chat_format_routes_to_its_tab", delegate
            {
                using (Harness h = new Harness())
                {
                    DateTime t = Fx.T0;
                    h.Live(Fx.Line(t, Fx.TellIn("Brindle", "got a spare shard")));
                    h.Live(Fx.Line(t, Fx.TellOut("Brindle", "thanks mate")));
                    h.Live(Fx.Line(t, Fx.GroupIn("Tavish", "inc adds ")));
                    h.Live(Fx.Line(t, "You say to the group, \"inc\""));
                    h.Live(Fx.Line(t, Fx.RaidIn("Cogsworth", "mana check please")));
                    h.Live(Fx.Line(t, Fx.GuildIn("Quillon", "grats all")));
                    h.Live(Fx.Line(t, Fx.OfficerIn("Marrowyn", "raid invites at eight")));
                    h.Live(Fx.Line(t, Fx.OocIn("Bob", "ooc words")));
                    h.Live(Fx.Line(t, Fx.ShoutIn("Starweave", "up the east stairs")));
                    h.Live(Fx.Line(t, Fx.AuctionIn("Seller", "WTS \\aITEM 123 456 0 0 0:Arcane Greatwall\\/a")));
                    h.Live(Fx.Line(t, Fx.SayIn("Harlow", "hello there")));
                    h.Live(Fx.Line(t, Fx.ChanIn("Fennick", "LFG", "lvl 60 wizard lfg")));
                    h.Live(Fx.Line(t, Fx.ChanOut("LFG", "inv pls")));
                    h.Live(Fx.Line(t, "\\aNPC 29713 a sanctum chaperon:a sanctum chaperon\\/a says, \"Intruders!\""));
                    h.Live(Fx.Line(t, Fx.Spam(1)));

                    Dictionary<string, string[]> want = new Dictionary<string, string[]>
                    {
                        { "TELL:Brindle", new[] { "got a spare shard", "thanks mate" } },
                        { "TELLALL", new[] { "got a spare shard", "thanks mate" } },
                        { "Group", new[] { "inc adds ", "inc" } },
                        { "Raid", new[] { "mana check please" } },
                        { "Guild", new[] { "grats all" } },
                        { "Officers", new[] { "raid invites at eight" } },
                        { "OOC", new[] { "ooc words" } },
                        { "Shout", new[] { "up the east stairs" } },
                        { "Auction", new[] { "WTS [Arcane Greatwall]" } },
                        { "Say", new[] { "hello there" } },
                        { "CHAN:LFG", new[] { "lvl 60 wizard lfg", "inv pls" } },
                    };
                    foreach (KeyValuePair<string, string[]> kv in want)
                    {
                        View v = h.V(kv.Key);
                        if (v == null) return "no view " + kv.Key;
                        string[] got = v.Msgs().Select(m => m.Text).ToArray();
                        if (!got.SequenceEqual(kv.Value)) return kv.Key + " = [" + string.Join(" | ", got) + "]";
                    }
                    return null;
                }
            });

            Run("guard_filter_narrows_the_rendered_tab", delegate
            {
                using (Harness h = new Harness())
                {
                    h.Load(Fx.GuildBacklog("filter.txt", 40, Fx.T0));
                    View g = h.V("Guild");
                    h.Select(g.Page);
                    h.FilterBox.Text = "guild line 17 ";
                    Harness.Pump(700);
                    string text = g.Box.Text;
                    if (!text.Contains("guild line 17 ")) return "filtered text missing: " + Snip(text);
                    if (text.Contains("guild line 18 ")) return "filter did not hide other lines";
                    return null;
                }
            });

            Run("guard_urls_in_chat_are_still_clickable_links", delegate
            {
                using (Harness h = new Harness())
                {
                    string f = Fx.Write("urls.txt", new[] { Fx.Line(Fx.T0, Fx.GuildIn("Linker", "see https://eq2.example.org/backlog for it")) });
                    h.Load(f);
                    View g = h.V("Guild");
                    h.Select(g.Page);
                    Harness.PumpUntil(delegate { return !g.NeedsRender; }, 60000);
                    h.Live(Fx.Line(Fx.T0.AddMinutes(1), Fx.GuildIn("Linker", "and https://eq2.example.org/live too")));
                    Harness.Flush();
                    if (!Rtb.IsLink(g.Box, "https://eq2.example.org/backlog")) return "URL in backlog-rendered text is not a link";
                    if (!Rtb.IsLink(g.Box, "https://eq2.example.org/live")) return "URL in live-appended text is not a link";
                    return Rtb.IsLink(g.Box, "Linker:") ? "speaker label detected as a link (probe is broken)" : null;
                }
            });

            Run("guard_live_appends_add_exactly_one_line_each", delegate
            {
                using (Harness h = new Harness())
                {
                    h.Load(Fx.GuildBacklog("lines.txt", 3, Fx.T0));
                    View g = h.V("Guild");
                    h.Select(g.Page);
                    Harness.PumpUntil(delegate { return !g.NeedsRender; }, 60000);
                    for (int i = 0; i < 3; i++) h.Live(Fx.Line(Fx.T0.AddMinutes(1 + i), Fx.GuildIn("Livey", "live " + i)));
                    string[] got = g.Box.Text.Split('\n');
                    string[] want =
                    {
                        Fx.Stamp(Fx.T0) + "  Speaker0: guild line 0 lorem ipsum dolor sit amet, consectetur adipiscing",
                        Fx.Stamp(Fx.T0.AddSeconds(1)) + "  Speaker1: guild line 1 lorem ipsum dolor sit amet, consectetur adipiscing",
                        Fx.Stamp(Fx.T0.AddSeconds(2)) + "  Speaker2: guild line 2 lorem ipsum dolor sit amet, consectetur adipiscing",
                        Fx.Stamp(Fx.T0.AddMinutes(1)) + "  Livey: live 0",
                        Fx.Stamp(Fx.T0.AddMinutes(2)) + "  Livey: live 1",
                        Fx.Stamp(Fx.T0.AddMinutes(3)) + "  Livey: live 2",
                    };
                    // The old code ended every line with a newline, leaving one empty line at the end.
                    List<string> lines = got.ToList();
                    if (lines.Count == want.Length + 1 && lines[lines.Count - 1] == "") lines.RemoveAt(lines.Count - 1);
                    return lines.SequenceEqual(want) ? null : "box lines: " + string.Join(" | ", got);
                }
            });

            Run("guard_live_line_after_a_quiet_spell_shows_at_once", delegate
            {
                using (Harness h = new Harness())
                {
                    View g = h.V("Guild");
                    h.Select(g.Page);
                    Harness.Pump(200);                                   // nothing arriving
                    h.Call("OnLogLineRead", false, new Advanced_Combat_Tracker.LogLineEventArgs(
                        Fx.Line(Fx.T0, Fx.GuildIn("Quick", "straight away")), 0, DateTime.Now, "", false));
                    Harness.Flush();                                     // no waiting on any timer
                    return g.Box.Text.Contains("Quick: straight away") ? null : "line not shown on the first UI pass";
                }
            });

            Run("guard_non_ascii_chat_renders_intact", delegate
            {
                using (Harness h = new Harness())
                {
                    string text = "caf\u00e9 cr\u00e8me \u00fcber na\u00efve \u2713 \u65e5\u672c {braces} back\\slash";
                    h.Load(Fx.Write("unicode.txt", new[] { Fx.Line(Fx.T0, Fx.GuildIn("Uni", "backlog " + text)) }, new UTF8Encoding(true)));
                    View g = h.V("Guild");
                    h.Select(g.Page);
                    Harness.PumpUntil(delegate { return !g.NeedsRender; }, 60000);
                    h.Live(Fx.Line(Fx.T0.AddMinutes(1), Fx.GuildIn("Uni", "live " + text)));
                    string box = g.Box.Text;
                    if (!box.Contains("Uni: backlog " + text)) return "backlog render: " + Snip(box);
                    return box.Contains("Uni: live " + text) ? null : "live append: " + Snip(box);
                }
            });

            Run("guard_live_lines_respect_an_active_filter", delegate
            {
                using (Harness h = new Harness())
                {
                    View g = h.V("Guild");
                    h.Select(g.Page);
                    h.FilterBox.Text = "keepme";
                    Harness.Pump(700);
                    h.Live(Fx.Line(Fx.T0, Fx.GuildIn("A", "keepme please")));
                    h.Live(Fx.Line(Fx.T0.AddSeconds(1), Fx.GuildIn("B", "hide this one")));
                    string box = g.Box.Text;
                    if (g.Messages.Count != 2) return "Guild holds " + g.Messages.Count;
                    return box.Contains("keepme please") && !box.Contains("hide this one") ? null : "filtered box: " + Snip(box);
                }
            });

            Run("guard_backlog_load_resorts_a_tab_that_act_imports_left_out_of_order", delegate
            {
                using (Harness h = new Harness())
                {
                    h.Live(Fx.Line(Fx.T0.AddHours(1), Fx.GuildIn("Now", "live newest")));
                    h.Live(Fx.Line(Fx.T0, Fx.GuildIn("Old", "imported oldest")), true);  // appended after a newer line
                    h.Load(Fx.Write("resort.txt", new[] { Fx.Line(Fx.T0.AddMinutes(30), Fx.GuildIn("Mid", "backlog middle")) }));
                    string got = string.Join(",", h.V("Guild").Msgs().Select(m => m.Speaker));
                    return got == "Old,Mid,Now" ? null : "Guild order " + got;
                }
            });

            Run("guard_render_cap_draws_newest_4000_with_notice", delegate
            {
                using (Harness h = new Harness())
                {
                    h.Load(Fx.GuildBacklog("cap.txt", 4500, Fx.T0));
                    View g = h.V("Guild");
                    h.Select(g.Page);
                    Harness.PumpUntil(delegate { return !g.NeedsRender; }, 60000);
                    string text = g.Box.Text;
                    if (!text.Contains("showing last 4,000 of 4,500")) return "notice missing: " + Snip(text);
                    if (text.Contains("guild line 499 ")) return "line older than the cap was drawn";
                    if (!text.Contains("guild line 500 ") || !text.Contains("guild line 4499 ")) return "newest 4000 not all drawn";
                    return null;
                }
            });
        }

        // ================================================================ 1. dark mode

        private static void DarkModeTests()
        {
            Run("dark_mode_checkbox_exists_and_is_on_by_default", delegate
            {
                DeleteSettings();
                using (Harness h = new Harness())
                {
                    CheckBox cb = h.FindCheckBox("Dark mode");
                    if (cb == null) return "no \"Dark mode\" checkbox in the plugin UI";
                    return cb.Checked ? null : "Dark mode is unchecked on first run";
                }
            });

            Run("dark_mode_chat_is_white_text_on_black", delegate
            {
                DeleteSettings();
                using (Harness h = new Harness())
                {
                    h.Load(Fx.GuildBacklog("dark1.txt", 20, Fx.T0));
                    View g = h.V("Guild");
                    h.Select(g.Page);
                    h.Live(Fx.Line(Fx.T0.AddMinutes(5), Fx.GuildIn("Livey", "live body text")));
                    List<string> bad = new List<string>();
                    foreach (View v in h.AllViews())
                    {
                        if (v.Box.BackColor.ToArgb() != Color.Black.ToArgb() || v.Box.ForeColor.ToArgb() != Color.White.ToArgb())
                            bad.Add(v.Page.Text + " back=" + v.Box.BackColor.Name + " fore=" + v.Box.ForeColor.Name);
                    }
                    if (bad.Count > 0) return "chat boxes not white-on-black: " + string.Join(", ", bad.Take(4));
                    Color backlogBody = Rtb.ColorOf(g.Box, "guild line 19 ");
                    Color liveBody = Rtb.ColorOf(g.Box, "live body text");
                    if (backlogBody.ToArgb() != Color.White.ToArgb()) return "backlog message text is " + backlogBody;
                    if (liveBody.ToArgb() != Color.White.ToArgb()) return "live message text is " + liveBody;
                    return null;
                }
            });

            Run("dark_mode_every_label_color_readable_on_black", delegate
            {
                DeleteSettings();
                using (Harness h = new Harness())
                {
                    LiveEveryChannel(h);
                    View latest = h.V("LATEST");
                    if (latest == null) return "no Latest view to render every channel into";
                    RichTextBox b = latest.Box;
                    if (b.BackColor.ToArgb() != Color.Black.ToArgb()) return "Latest background is " + b.BackColor;
                    string[] labels =
                    {
                        Fx.Stamp(Fx.T0), "[Tell]", "From Brindle:", "To Brindle:", "[Group]", "Tavish:", "[Raid]", "Cogsworth:",
                        "[Guild]", "Quillon:", "[Officers]", "Marrowyn:", "[OOC]", "Bob:", "[Shout]", "Starweave:",
                        "[Auction]", "Seller:", "[Say]", "Harlow:", "[#LFG]", "Fennick:", "You:",
                    };
                    List<string> bad = new List<string>();
                    foreach (string l in labels)
                    {
                        Color c = Rtb.ColorOf(b, l);
                        double cr = Rtb.Contrast(c, b.BackColor);
                        if (cr < 4.5) bad.Add(l + " " + c.R + "," + c.G + "," + c.B + " = " + cr.ToString("0.0") + ":1");
                    }
                    return bad.Count == 0 ? null : "below 4.5:1 on black: " + string.Join("; ", bad);
                }
            });

            Run("dark_mode_off_switches_rendered_text_to_black_on_white", delegate
            {
                DeleteSettings();
                using (Harness h = new Harness())
                {
                    CheckBox cb = h.FindCheckBox("Dark mode");
                    if (cb == null) return "no \"Dark mode\" checkbox";
                    h.Load(Fx.GuildBacklog("dark2.txt", 20, Fx.T0));
                    View g = h.V("Guild");
                    h.Select(g.Page);
                    h.Live(Fx.Line(Fx.T0.AddMinutes(5), Fx.GuildIn("Livey", "live body text")));
                    cb.Checked = false;
                    Harness.Flush();
                    if (g.Box.BackColor.ToArgb() != Color.White.ToArgb()) return "background still " + g.Box.BackColor;
                    if (!g.Box.Text.Contains("guild line 19 ") || !g.Box.Text.Contains("live body text")) return "text lost on theme switch";
                    Color body = Rtb.ColorOf(g.Box, "guild line 19 ");
                    Color live = Rtb.ColorOf(g.Box, "live body text");
                    Color speaker = Rtb.ColorOf(g.Box, "Speaker5:");
                    if (body.ToArgb() != Color.Black.ToArgb() || live.ToArgb() != Color.Black.ToArgb()) return "body text " + body + " / " + live;
                    if (speaker.ToArgb() != Color.Green.ToArgb()) return "guild speaker label " + speaker + " (light theme uses Green)";
                    return null;
                }
            });

            Run("dark_mode_choice_is_remembered_next_session", delegate
            {
                // The real file may exist (ACT itself writes it); the test must just never touch it.
                string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Advanced Combat Tracker", "Config", "ChatParserPlugin.config.xml");
                DateTime realBefore = File.Exists(real) ? File.GetLastWriteTimeUtc(real) : DateTime.MinValue;
                DeleteSettings();
                using (Harness h = new Harness())
                {
                    CheckBox cb = h.FindCheckBox("Dark mode");
                    if (cb == null) return "no \"Dark mode\" checkbox";
                    cb.Checked = false;
                    Harness.Flush();
                }
                using (Harness h = new Harness())
                {
                    if (h.FindCheckBox("Dark mode").Checked) return "turned off, but on again next session";
                    if (h.V("Guild").Box.BackColor.ToArgb() != Color.White.ToArgb()) return "restored off but boxes not light";
                    h.FindCheckBox("Dark mode").Checked = true;
                    Harness.Flush();
                }
                using (Harness h = new Harness())
                {
                    if (!h.FindCheckBox("Dark mode").Checked) return "turned back on, but off next session";
                }
                DateTime realAfter = File.Exists(real) ? File.GetLastWriteTimeUtc(real) : DateTime.MinValue;
                return realAfter != realBefore ? "tests wrote the real ACT config file " + real : null;
            });

            Run("dark_mode_tab_strip_keeps_its_font_after_toggling_off", delegate
            {
                DeleteSettings();
                Size toggled;
                using (Harness h = new Harness())
                {
                    IntPtr darkFont = Rtb.NativeFont(h.Main);
                    if (darkFont == IntPtr.Zero) return "dark tab strip has no font set (native control falls back to the bold system font)";
                    h.FindCheckBox("Dark mode").Checked = false;
                    Harness.Flush();
                    if (Rtb.NativeFont(h.Main) == IntPtr.Zero) return "no font set after toggling to light";
                    toggled = h.Main.GetTabRect(0).Size;
                }
                using (Harness h = new Harness()) // starts light: the settings file now says so
                {
                    Size fresh = h.Main.GetTabRect(0).Size;
                    return toggled == fresh ? null : "Latest tab is " + toggled + " after toggling, " + fresh + " when started light";
                }
            });

            Run("dark_mode_frame_and_tab_strip_are_dark", delegate
            {
                DeleteSettings();
                using (Harness h = new Harness())
                {
                    LiveEveryChannel(h);
                    using (Bitmap shot = h.Capture())
                    {
                        shot.Save(Path.Combine(ArtifactsDir, "dark_frame_probe.png"), ImageFormat.Png);
                        TabControl tc = h.Main;
                        Rectangle tab0 = tc.GetTabRect(0);
                        Dictionary<string, Color> probes = new Dictionary<string, Color>
                        {
                            { "top bar", h.PixelAt(shot, h.P, new Point(h.P.Width - 6, 3)) },
                            { "empty tab strip", h.PixelAt(shot, tc, new Point(tc.Width - 8, tab0.Top + tab0.Height / 2)) },
                            { "tab header", h.PixelAt(shot, tc, new Point(tab0.Left + 3, tab0.Top + 3)) },
                            { "filter box", h.PixelAt(shot, h.FilterBox, new Point(h.FilterBox.ClientSize.Width - 4, h.FilterBox.ClientSize.Height / 2)) },
                        };
                        List<string> bright = probes.Where(p => Rtb.Luminance(p.Value) > 0.06)
                            .Select(p => p.Key + " " + p.Value.R + "," + p.Value.G + "," + p.Value.B).ToList();
                        return bright.Count == 0 ? null : "bright in dark mode: " + string.Join("; ", bright);
                    }
                }
            });
        }

        // ================================================================ 2. tab switching

        private static void TabSwitchTests()
        {
            Run("tab_switch_after_backlog_draws_within_400ms", delegate
            {
                using (Harness h = new Harness())
                {
                    h.Load(Fx.GuildBacklog("switch.txt", 6000, Fx.T0));
                    View g = h.V("Guild");
                    Stopwatch sw = Stopwatch.StartNew();
                    h.Select(g.Page);
                    Harness.PumpUntil(delegate { return !g.NeedsRender && g.Box.TextLength > 0; }, 120000);
                    long ms = sw.ElapsedMilliseconds;
                    Console.WriteLine("        Guild tab with 6,000 backlog messages drawn in " + ms + " ms");
                    return ms <= 400 ? null : "switching to the tab took " + ms + " ms";
                }
            });

            Run("act_import_of_6000_lines_lands_on_newest_line_within_2s", delegate
            {
                using (Harness h = new Harness())
                {
                    View g = h.V("Guild"), raid = h.V("Raid");
                    h.Select(g.Page);
                    Harness.Flush();
                    List<string> lines = new List<string>();
                    for (int i = 0; i < 6000; i++)
                    {
                        DateTime t = Fx.T0.AddSeconds(i);
                        lines.Add(Fx.Line(t, Fx.Spam(i)));
                        lines.Add(Fx.Line(t, i % 2 == 0 ? Fx.GuildIn("Speaker" + (i % 7), "guild line " + i + " imported")
                                                         : Fx.RaidIn("Caller", "raid line " + i + " imported")));
                    }
                    Harness.ImportResult r = h.ActImport(lines, delegate
                    {
                        return g.Messages.Count == 3000 && raid.Messages.Count == 3000 && !g.NeedsRender;
                    });
                    Console.WriteLine("        ACT import of 6,000 chat lines: " + r);
                    if (!r.Done) return "import never finished drawing: " + r;
                    if (!Rtb.ShowsLastMessage(g.Box, "guild line 5998 imported")) return "shown tab not on its newest line";
                    View latest = h.V("LATEST");
                    if (latest != null && latest.Messages.Count != 0) return "imported lines reached Latest";
                    List<string> bad = new List<string>();
                    if (r.ElapsedMs > 2000) bad.Add("took " + r.ElapsedMs + " ms to finish drawing");
                    if (r.MaxUiGapMs > 250) bad.Add("UI froze for " + r.MaxUiGapMs + " ms");
                    return bad.Count == 0 ? null : string.Join("; ", bad);
                }
            });

            Run("act_import_at_a_steady_pace_keeps_up_without_crawling", delegate
            {
                using (Harness h = new Harness())
                {
                    View g = h.V("Guild");
                    h.Select(g.Page);
                    Harness.Flush();
                    List<string> lines = new List<string>();
                    for (int i = 0; i < 6000; i++)
                    {
                        lines.Add(Fx.Line(Fx.T0.AddSeconds(i), Fx.Spam(i)));
                        lines.Add(Fx.Line(Fx.T0.AddSeconds(i), Fx.GuildIn("Speaker" + (i % 7), "paced line " + i + " end")));
                    }
                    // 6,000 lines/s for 2 s: small batches rather than one flood.
                    Harness.ImportResult r = h.ActImport(lines, delegate { return g.Messages.Count == 6000; }, 6000);
                    Console.WriteLine("        paced ACT import: " + r);
                    if (!r.Done) return "import never finished: " + r;
                    if (!Rtb.ShowsLastMessage(g.Box, "paced line 5999 end")) return "shown tab not on its newest line";
                    List<string> bad = new List<string>();
                    if (r.ElapsedMs - r.FeedMs > 500) bad.Add("finished " + (r.ElapsedMs - r.FeedMs) + " ms after ACT's last line");
                    if (r.MaxUiGapMs > 250) bad.Add("UI froze for " + r.MaxUiGapMs + " ms");
                    // Full collections are what stall the UI thread; a steady import should cause few.
                    if (r.Gen2Collections > 10) bad.Add(r.Gen2Collections + " full GCs during the import");
                    return bad.Count == 0 ? null : string.Join("; ", bad);
                }
            });

            Run("guard_long_act_import_into_shown_tab_is_trimmed_to_newest_lines", delegate
            {
                using (Harness h = new Harness())
                {
                    View g = h.V("Guild");
                    h.Select(g.Page);
                    Harness.Flush();
                    List<string> lines = new List<string>();
                    for (int i = 0; i < 9000; i++) lines.Add(Fx.Line(Fx.T0.AddSeconds(i), Fx.GuildIn("Long", "long " + i + " end")));
                    Harness.ImportResult r = h.ActImport(lines, delegate { return g.Messages.Count == 9000 && !g.NeedsRender; }, 20000);
                    if (!r.Done) return "import never finished: " + r;
                    int drawn = g.Box.Lines.Length;
                    if (drawn > 2 * 4000 + 1) return drawn + " lines left in the box";
                    if (g.Box.Text.Contains("Long: long 0 end")) return "oldest lines never trimmed";
                    return Rtb.ShowsLastMessage(g.Box, "long 8999 end") ? null : "not on the newest line";
                }
            });

            Run("act_import_fills_hidden_tabs_so_switching_is_instant", delegate
            {
                using (Harness h = new Harness())
                {
                    View g = h.V("Guild"), raid = h.V("Raid");
                    h.Select(g.Page);
                    Harness.Flush();
                    List<string> lines = new List<string>();
                    for (int i = 0; i < 2000; i++)
                        lines.Add(Fx.Line(Fx.T0.AddSeconds(i), i % 2 == 0 ? Fx.GuildIn("G", "guild " + i + " end") : Fx.RaidIn("R", "raid " + i + " end")));
                    Harness.ImportResult r = h.ActImport(lines, delegate { return g.Messages.Count == 1000 && raid.Messages.Count == 1000; });
                    if (!r.Done) return "import never finished: " + r;
                    Stopwatch sw = Stopwatch.StartNew();
                    h.Select(raid.Page);
                    Harness.PumpUntil(delegate { return !raid.NeedsRender && raid.Box.TextLength > 0; }, 120000);
                    long ms = sw.ElapsedMilliseconds;
                    Harness.Flush();
                    if (!Rtb.ShowsLastMessage(raid.Box, "raid 1999 end")) return "Raid tab not on its newest line";
                    return ms <= 400 ? null : "switching to Raid took " + ms + " ms";
                }
            });

            Run("guard_tab_switch_after_backlog_shows_newest_line", delegate
            {
                using (Harness h = new Harness())
                {
                    h.Load(Fx.GuildBacklog("switch2.txt", 6000, Fx.T0));
                    View g = h.V("Guild");
                    h.Select(g.Page);
                    Harness.PumpUntil(delegate { return !g.NeedsRender && g.Box.TextLength > 0; }, 120000);
                    Harness.Flush();
                    return Rtb.ShowsLastMessage(g.Box, "guild line 5999 ") ? null
                        : "newest line not visible; first visible line " + Rtb.FirstVisibleLine(g.Box) + " of " + Rtb.LastLine(g.Box);
                }
            });

            Run("guard_tab_switch_back_after_hidden_live_lines_shows_newest_line", delegate
            {
                using (Harness h = new Harness())
                {
                    h.Load(Fx.GuildBacklog("switch3.txt", 300, Fx.T0));
                    View g = h.V("Guild");
                    h.Select(g.Page);
                    Harness.PumpUntil(delegate { return !g.NeedsRender; }, 60000);
                    g.Box.Select(0, 0);
                    g.Box.ScrollToCaret();                       // user scrolled up to read history
                    Harness.Flush();
                    if (Rtb.FirstVisibleLine(g.Box) != 0) return "setup: could not scroll to top";
                    h.Select(h.V("Raid").Page);
                    for (int i = 0; i < 40; i++)
                        h.Live(Fx.Line(Fx.T0.AddHours(1).AddSeconds(i), Fx.GuildIn("Livey", "live after switch " + i + " end")));
                    h.Select(g.Page);
                    Harness.Flush();
                    return Rtb.ShowsLastMessage(g.Box, "live after switch 39 end") ? null
                        : "newest line not visible; first visible line " + Rtb.FirstVisibleLine(g.Box) + " of " + Rtb.LastLine(g.Box);
                }
            });
        }

        // ================================================================ 3. backlog speed

        private static void LoadSpeedTests()
        {
            Run("guard_parser_matches_original_on_edge_case_fixtures_at_every_block_size", delegate
            {
                string[] files = EdgeCaseFixtures();
                Dictionary<string, List<RefMsg>> want = ReferenceParser.GroupByView(ReferenceParser.ParseFiles(files));
                object original = Harness.GetStatic("ParseBlockBytes");
                try
                {
                    foreach (int size in new[] { 0, 1, 7, 64, 4096 })
                    {
                        if (size > 0 && !Harness.SetStatic("ParseBlockBytes", size)) continue; // old code: no block knob
                        using (Harness h = new Harness())
                        {
                            h.Load(files);
                            string diff = Diff(want, h);
                            if (diff != null) return "block size " + (size == 0 ? "default" : size.ToString()) + ": " + diff;
                        }
                    }
                }
                finally
                {
                    if (original != null) Harness.SetStatic("ParseBlockBytes", original);
                }
                return null;
            });

            Run("parser_splits_lines_on_lone_carriage_returns_like_the_original", delegate
            {
                // Real logs hold "\r\r\n" endings and lone "\r" inside lines; ReadLine broke lines there.
                DateTime t = Fx.T0;
                string path = Path.Combine(Fx.Dir, "lone_cr.txt");
                File.WriteAllText(path,
                    Fx.Line(t, Fx.GuildIn("Crcr", "doubled cr ending")) + "\r\r\n"
                    + Fx.Line(t.AddSeconds(1), Fx.GuildIn("Inside", "before\rafter")) + "\r\n"
                    + Fx.Line(t.AddSeconds(2), Fx.GuildIn("One", "first")) + "\r" + Fx.Line(t.AddSeconds(3), Fx.GuildIn("Two", "second")) + "\r\n"
                    + Fx.Line(t.AddSeconds(4), "Lore and Legend: Aviak") + "\r\r\n"
                    + Fx.Line(t.AddSeconds(5), Fx.GuildIn("Tail", "ends on a lone cr")) + "\r", Encoding.Default);
                string[] files = { path };
                Dictionary<string, List<RefMsg>> want = ReferenceParser.GroupByView(ReferenceParser.ParseFiles(files));
                long wantLines = ReferenceParser.CountLines(files);
                object original = Harness.GetStatic("ParseBlockBytes");
                try
                {
                    foreach (int size in new[] { 0, 1, 7, 64 })
                    {
                        if (size > 0 && !Harness.SetStatic("ParseBlockBytes", size)) continue;
                        using (Harness h = new Harness())
                        {
                            Harness.LoadResult r = h.Load(files);
                            string at = "block size " + (size == 0 ? "default" : size.ToString()) + ": ";
                            string diff = Diff(want, h);
                            if (diff != null) return at + diff;
                            long lines = LinesScanned(r.Status);
                            if (lines != wantLines) return at + lines + " lines scanned, original counted " + wantLines;
                        }
                    }
                }
                finally
                {
                    if (original != null) Harness.SetStatic("ParseBlockBytes", original);
                }
                return null;
            });

            string[] real = RealLogs();
            if (!_realLogs || real.Length == 0 || real.Any(f => !File.Exists(f)))
            {
                Skip("real-log tests", "--no-real-logs, or no readable log files listed in " + RealLogList);
                return;
            }
            long bytes = real.Sum(f => new FileInfo(f).Length);
            double mb = bytes / 1048576.0;
            Harness.LoadResult r = null;
            Dictionary<string, List<RefMsg>> realWant = null;
            string realDiff = null;
            Run("backlog_load_of_real_logs_at_250MB_per_s_or_better", delegate
            {
                using (Harness h = new Harness())
                {
                    r = h.Load(real);
                    double rate = mb / (r.ElapsedMs / 1000.0);
                    Console.WriteLine("        " + mb.ToString("0") + " MB in " + r.ElapsedMs + " ms = "
                        + rate.ToString("0") + " MB/s; longest UI freeze " + r.MaxUiGapMs + " ms");
                    realWant = ReferenceParser.GroupByView(ReferenceParser.ParseFiles(real));
                    realDiff = Diff(realWant, h);
                    return rate >= 250 ? null : "loaded at " + rate.ToString("0") + " MB/s (" + r.ElapsedMs + " ms)";
                }
            });
            if (Filtered("backlog_load_of_real_logs_at_250MB_per_s_or_better"))
            {
                Skip("real-log freeze and equivalence tests", "the load they measure was filtered out by --only");
                return;
            }
            Run("backlog_load_of_real_logs_never_freezes_ui_over_250ms", delegate
            {
                if (r == null) return "load did not run";
                return r.MaxUiGapMs <= 250 ? null : "UI thread blocked for " + r.MaxUiGapMs + " ms";
            });
            Run("guard_parser_matches_original_on_real_logs", delegate
            {
                if (realWant == null) return "load did not run";
                Console.WriteLine("        " + realWant.Sum(kv => kv.Value.Count) + " view entries across " + realWant.Count + " tabs compared");
                long wantLines = ReferenceParser.CountLines(real), gotLines = LinesScanned(r.Status);
                if (realDiff == null && gotLines != wantLines) return gotLines + " lines scanned, original counted " + wantLines;
                return realDiff;
            });
        }

        private static string[] EdgeCaseFixtures()
        {
            DateTime t = Fx.T0;
            List<string> a = new List<string>
            {
                "",
                Fx.Line(t, Fx.Spam(1)),
                Fx.Line(t, Fx.GuildIn("Alpha", "he said, \"hi\" to me")),              // `, "` inside the message
                Fx.Line(t, Fx.GuildIn("Alpha", "caf\u00e9 cr\u00e8me")),              // ANSI high chars
                Fx.Line(t.AddSeconds(1), Fx.TellIn("Brava", "link \\aITEM 1 2 0 0 0:Arcane Greatwall\\/a ok")),
                Fx.Line(t.AddSeconds(1), Fx.TellOut("Brava", "thx")),
                Fx.Line(t.AddSeconds(2), Fx.ChanIn("Fennick", "LFG", "lvl 60 wizard lfg")),
                Fx.Line(t.AddSeconds(2), Fx.ChanOut("New Channel", "spaces in name")),
                Fx.Line(t.AddSeconds(3), "\\aNPC 1 a guard:a guard\\/a says, \"halt\""),  // NPC: not captured
                Fx.GuildIn("Nostamp", "line without timestamp prefix"),                  // no (unix) prefix
                "short, \"x\"",                                                           // under 30 chars
                Fx.Line(t.AddSeconds(-30), Fx.GuildIn("Early", "older than the rest")),  // out of order
                Fx.Line(t.AddSeconds(4), Fx.RaidIn("Cogsworth", "trailing quote missing")).TrimEnd('"'),
                Fx.Line(t.AddSeconds(5), "You say, \"" + new string('w', 9000) + "\""),  // longer than small blocks
            };
            for (int i = 0; i < 200; i++)
            {
                a.Add(Fx.Line(t.AddSeconds(10 + i), Fx.Spam(i)));
                a.Add(Fx.Line(t.AddSeconds(10 + i), i % 2 == 0 ? Fx.GroupIn("Gee" + (i % 5), "grp " + i) : Fx.SayIn("Sayer", "say " + i)));
            }
            string f1 = Fx.Write("edge_crlf.txt", a);
            string f2 = Fx.Write("edge_lf.txt", a.Select(l => l.Replace("Alpha", "Lf")), null, "\n");
            string f3 = Fx.Write("edge_utf8bom.txt", new[] { Fx.Line(t.AddSeconds(6), Fx.GuildIn("Bom", "na\u00efve \u00fcber")) }, new UTF8Encoding(true));
            string f4 = Fx.Write("edge_utf16.txt", new[] { Fx.Line(t.AddSeconds(7), Fx.GuildIn("Wide", "utf16 \u00e9")) }, new UnicodeEncoding(false, true));
            string f5 = Path.Combine(Fx.Dir, "edge_empty.txt");
            File.WriteAllBytes(f5, new byte[0]);
            string f6 = Path.Combine(Fx.Dir, "edge_crlf_only.txt");
            File.WriteAllBytes(f6, new byte[] { 13, 10 });
            string f7 = Path.Combine(Fx.Dir, "edge_no_final_newline.txt");
            File.WriteAllText(f7, Fx.Line(t.AddSeconds(8), Fx.Spam(9)) + "\r\n" + Fx.Line(t.AddSeconds(8), Fx.GuildIn("Last", "no newline after me")), Encoding.Default);
            return new[] { f1, f2, f3, f4, f5, f6, f7 };
        }

        /// <summary>First difference between the reference per-tab lists and the plugin's, or null.</summary>
        private static string Diff(Dictionary<string, List<RefMsg>> want, Harness h)
        {
            foreach (KeyValuePair<string, List<RefMsg>> kv in want)
            {
                View v = h.V(kv.Key);
                if (v == null) return "missing tab " + kv.Key;
                List<RefMsg> got = v.Msgs();
                for (int i = 0; i < Math.Max(got.Count, kv.Value.Count); i++)
                {
                    string g = i < got.Count ? got[i].ToString() : "(none)";
                    string w = i < kv.Value.Count ? kv.Value[i].ToString() : "(none)";
                    if (g != w) return kv.Key + "[" + i + "] got " + Snip(g) + " want " + Snip(w);
                }
            }
            foreach (string k in h.ViewKeys())
            {
                if (k == "LATEST") continue;
                if (!want.ContainsKey(k) && h.V(k).Messages.Count > 0) return "unexpected tab " + k;
            }
            return null;
        }

        // ================================================================ 4. tell tab order

        private static string TellFixture()
        {
            DateTime t = Fx.T0;
            return Fx.Write("tells.txt", new[]
            {
                Fx.Line(t.AddMinutes(10), Fx.TellIn("Mike", "old one from mike")),
                Fx.Line(t.AddHours(1), Fx.TellIn("Alice", "alice oldest")),
                Fx.Line(t.AddHours(1).AddSeconds(5), Fx.TellOut("Alice", "reply to alice")),
                Fx.Line(t.AddHours(2), Fx.TellOut("Mike", "mike middle")),
                Fx.Line(t.AddHours(3), Fx.TellIn("Zed", "zed newest")),
            });
        }

        private static void TellOrderTests()
        {
            Run("tell_tabs_sorted_most_recent_first_after_backlog", delegate
            {
                using (Harness h = new Harness())
                {
                    h.Load(TellFixture());
                    string got = string.Join(",", h.TellTabOrder());
                    return got == "Zed,Mike,Alice" ? null : "order " + got;
                }
            });

            Run("live_tell_moves_sender_tab_to_front", delegate
            {
                using (Harness h = new Harness())
                {
                    h.Load(TellFixture());
                    h.Live(Fx.Line(Fx.T0.AddHours(4), Fx.TellIn("Alice", "alice again")));
                    string got = string.Join(",", h.TellTabOrder());
                    return got == "Alice,Zed,Mike" ? null : "order " + got;
                }
            });

            Run("live_sent_tell_moves_recipient_tab_to_front", delegate
            {
                using (Harness h = new Harness())
                {
                    h.Load(TellFixture());
                    h.Live(Fx.Line(Fx.T0.AddHours(4), Fx.TellOut("Mike", "to mike now")));
                    string got = string.Join(",", h.TellTabOrder());
                    return got == "Mike,Zed,Alice" ? null : "order " + got;
                }
            });

            Run("live_tell_from_new_player_opens_tab_at_front", delegate
            {
                using (Harness h = new Harness())
                {
                    h.Load(TellFixture());
                    h.Live(Fx.Line(Fx.T0.AddHours(4), Fx.TellIn("Bob", "hi, new here")));
                    string got = string.Join(",", h.TellTabOrder());
                    return got == "Bob,Zed,Mike,Alice" ? null : "order " + got;
                }
            });

            Run("guard_tell_reorder_keeps_selected_tab_and_its_text", delegate
            {
                using (Harness h = new Harness())
                {
                    h.Load(TellFixture());
                    View mike = h.V("TELL:Mike");
                    h.Select(mike.Page);
                    Harness.PumpUntil(delegate { return !mike.NeedsRender; }, 60000);
                    h.Live(Fx.Line(Fx.T0.AddHours(4), Fx.TellIn("Alice", "alice again")));
                    Harness.Flush();
                    if (h.Tells.SelectedTab != mike.Page) return "selected tab became " + (h.Tells.SelectedTab == null ? "none" : h.Tells.SelectedTab.Text);
                    if (h.CurrentBox().Box != mike.Box) return "current view is not Mike's";
                    if (!mike.Box.Text.Contains("mike middle") || !mike.Box.Text.Contains("old one from mike")) return "Mike's text lost: " + Snip(mike.Box.Text);
                    return null;
                }
            });
        }

        // ================================================================ 5. Latest tab

        private static void LiveEveryChannel(Harness h)
        {
            DateTime t = Fx.T0;
            h.Live(Fx.Line(t, Fx.TellIn("Brindle", "got a spare shard")));
            h.Live(Fx.Line(t.AddSeconds(1), Fx.TellOut("Brindle", "thanks mate")));
            h.Live(Fx.Line(t.AddSeconds(2), Fx.GroupIn("Tavish", "inc adds ")));
            h.Live(Fx.Line(t.AddSeconds(3), Fx.RaidIn("Cogsworth", "mana check please")));
            h.Live(Fx.Line(t.AddSeconds(4), Fx.GuildIn("Quillon", "grats all")));
            h.Live(Fx.Line(t.AddSeconds(5), Fx.OfficerIn("Marrowyn", "raid invites at eight")));
            h.Live(Fx.Line(t.AddSeconds(6), Fx.OocIn("Bob", "ooc words")));
            h.Live(Fx.Line(t.AddSeconds(7), Fx.ShoutIn("Starweave", "up the east stairs")));
            h.Live(Fx.Line(t.AddSeconds(8), Fx.AuctionIn("Seller", "WTS stuff")));
            h.Live(Fx.Line(t.AddSeconds(9), Fx.SayIn("Harlow", "hello there")));
            h.Live(Fx.Line(t.AddSeconds(10), Fx.ChanIn("Fennick", "LFG", "lvl 60 wizard lfg")));
            h.Live(Fx.Line(t.AddSeconds(11), Fx.ChanOut("LFG", "inv pls")));
        }

        private static void LatestTests()
        {
            Run("latest_tab_is_the_first_tab", delegate
            {
                using (Harness h = new Harness())
                {
                    string first = h.Main.TabPages[0].Text;
                    if (first != "Latest") return "first tab is " + first;
                    return h.Main.SelectedIndex == 0 ? null : "Latest is not selected on start";
                }
            });

            Run("latest_tab_shows_live_chat_from_every_channel_in_order", delegate
            {
                using (Harness h = new Harness())
                {
                    LiveEveryChannel(h);
                    View l = h.V("LATEST");
                    if (l == null) return "no Latest view";
                    string[] want =
                    {
                        "[Tell] From Brindle: got a spare shard", "[Tell] To Brindle: thanks mate", "[Group] Tavish: inc adds ",
                        "[Raid] Cogsworth: mana check please", "[Guild] Quillon: grats all", "[Officers] Marrowyn: raid invites at eight",
                        "[OOC] Bob: ooc words", "[Shout] Starweave: up the east stairs", "[Auction] Seller: WTS stuff",
                        "[Say] Harlow: hello there", "[#LFG] Fennick: lvl 60 wizard lfg", "[#LFG] You: inv pls",
                    };
                    string text = l.Box.Text;
                    int at = -1;
                    foreach (string w in want)
                    {
                        int i = text.IndexOf(w, StringComparison.Ordinal);
                        if (i < 0) return "missing \"" + w + "\" in: " + Snip(text);
                        if (i < at) return "\"" + w + "\" out of order";
                        at = i;
                    }
                    if (!text.StartsWith(Fx.Stamp(Fx.T0))) return "lines are not timestamped: " + Snip(text);
                    return l.Messages.Count == want.Length ? null : "Latest holds " + l.Messages.Count + " messages";
                }
            });

            Run("latest_tab_ignores_backlog_loads", delegate
            {
                using (Harness h = new Harness())
                {
                    View l = h.V("LATEST");
                    if (l == null) return "no Latest view";
                    h.Live(Fx.Line(Fx.T0.AddDays(1), Fx.GuildIn("Livey", "live one")));
                    h.Load(Fx.GuildBacklog("latest_backlog.txt", 50, Fx.T0));
                    if (h.V("Guild").Messages.Count != 51) return "Guild got " + h.V("Guild").Messages.Count;
                    if (l.Messages.Count != 1) return "Latest holds " + l.Messages.Count + " messages after a backlog load";
                    return l.Box.Text.Contains("guild line") ? "backlog text drawn in Latest" : null;
                }
            });

            Run("latest_tab_ignores_act_log_imports", delegate
            {
                using (Harness h = new Harness())
                {
                    View l = h.V("LATEST");
                    if (l == null) return "no Latest view";
                    h.Live(Fx.Line(Fx.T0, Fx.GuildIn("Imported", "from an ACT import")), true);
                    if (h.V("Guild").Messages.Count != 1) return "imported line did not reach Guild";
                    return l.Messages.Count == 0 && l.Box.TextLength == 0 ? null : "imported line shown in Latest";
                }
            });

            Run("guard_latest_tab_respects_filter", delegate
            {
                using (Harness h = new Harness())
                {
                    View l = h.V("LATEST");
                    if (l == null) return "no Latest view";
                    LiveEveryChannel(h);
                    h.FilterBox.Text = "grats all";
                    Harness.Pump(700);
                    string text = l.Box.Text;
                    return text.Contains("Quillon: grats all") && !text.Contains("Harlow") ? null : "filtered Latest: " + Snip(text);
                }
            });

            Run("guard_clear_all_empties_latest", delegate
            {
                using (Harness h = new Harness())
                {
                    View l = h.V("LATEST");
                    if (l == null) return "no Latest view";
                    LiveEveryChannel(h);
                    h.Call("ClearAll");
                    Harness.Flush();
                    return l.Messages.Count == 0 && l.Box.TextLength == 0 ? null : "Latest still has " + l.Messages.Count;
                }
            });
        }

        // ================================================================ screenshots

        private static void Screenshots()
        {
            Run("screenshots_written", delegate
            {
                DeleteSettings();
                using (Harness h = new Harness())
                {
                    h.Load(Fx.GuildBacklog("shots.txt", 5000, Fx.T0.AddDays(-1)), TellFixture());
                    LiveEveryChannel(h);
                    Save(h, "dark_latest.png");
                    h.Select(h.V("Guild").Page);
                    Save(h, "dark_guild_after_backlog.png");
                    h.Select(h.V("TELL:Brindle").Page);
                    Save(h, "dark_tells.png");
                    h.Select(h.V("TELL:Mike").Page);
                    h.Live(Fx.Line(Fx.T0.AddHours(5), Fx.TellIn("Alice", "alice jumps to the front")));
                    Save(h, "dark_tells_reordered_while_mike_selected.png");
                    CheckBox cb = h.FindCheckBox("Dark mode");
                    if (cb != null)
                    {
                        cb.Checked = false;
                        h.Select(h.V("Guild").Page);
                        Save(h, "light_guild.png");
                    }
                }
                return null;
            });
        }

        private static void Save(Harness h, string name)
        {
            Harness.Flush();
            using (Bitmap b = h.Capture()) b.Save(Path.Combine(ArtifactsDir, name), ImageFormat.Png);
            Console.WriteLine("        screenshot: " + Path.Combine(ArtifactsDir, name));
        }

        // ================================================================ bench

        private static int Bench(string[] files)
        {
            bool verify = files.Length > 0 && files[0] == "--verify";
            if (verify) files = files.Skip(1).ToArray();
            if (files.Length > 1 && files[0] == "--block")
            {
                Console.WriteLine("block size " + files[1] + ": " + Harness.SetStatic("ParseBlockBytes", int.Parse(files[1])));
                files = files.Skip(2).ToArray();
            }
            long bytes = files.Sum(f => new FileInfo(f).Length);
            using (Harness h = new Harness())
            {
                Harness.LoadResult r = h.Load(files);
                int msgs = h.AllViews().Sum(v => v.Messages.Count);
                Console.WriteLine(files.Length + " file(s), " + (bytes / 1048576) + " MB: " + r);
                Console.WriteLine("throughput " + (bytes / 1048576.0 / (r.ElapsedMs / 1000.0)).ToString("0") + " MB/s, " + msgs + " view entries");
                if (verify)
                {
                    string diff = Diff(ReferenceParser.GroupByView(ReferenceParser.ParseFiles(files)), h);
                    long want = ReferenceParser.CountLines(files), got = LinesScanned(r.Status);
                    Console.WriteLine("verify vs original parser: " + (diff ?? "every tab identical") + "; lines " + got + " vs original " + want);
                    return diff == null && got == want ? 0 : 1;
                }
            }
            return 0;
        }

        // ================================================================ runner

        private static void DeleteSettings()
        {
            if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
        }

        private static long LinesScanned(string status)
        {
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(status, @"([\d,.]+) lines scanned");
            if (!m.Success) throw new Exception("no line count in status \"" + status + "\"");
            return long.Parse(m.Groups[1].Value.Replace(",", "").Replace(".", ""));
        }

        private static string Snip(string s)
        {
            s = s.Replace("\n", "\\n");
            return s.Length > 160 ? s.Substring(0, 160) + "..." : s;
        }

        private static void Section(string name, Action body)
        {
            Console.WriteLine();
            Console.WriteLine("== " + name);
            body();
        }

        private static bool Filtered(string name)
        {
            return _only != null && name.IndexOf(_only, StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static void Run(string name, Func<string> test)
        {
            if (Filtered(name)) return;
            string fail;
            Stopwatch sw = Stopwatch.StartNew();
            try { fail = test(); }
            catch (Exception e) { fail = "threw " + e.GetType().Name + ": " + e.Message; }
            Check(name + "  (" + sw.ElapsedMilliseconds + " ms)", fail == null, fail);
        }

        private static void Check(string name, bool ok, string detail)
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name + (ok ? "" : "\n          " + detail));
            if (ok) _passes++;
            else { _failures++; Failed.Add(name); }
        }

        private static void Skip(string name, string why)
        {
            Console.WriteLine("  SKIP  " + name + " (" + why + ")");
            _skips++;
        }

        private static int Report()
        {
            Console.WriteLine();
            Console.WriteLine(_passes + " passed, " + _failures + " failed, " + _skips + " skipped");
            foreach (string f in Failed) Console.WriteLine("  failed: " + f);
            Console.WriteLine(_failures == 0 ? "ALL PASS" : _failures + " FAILURE(S)");
            return _failures == 0 ? 0 : 1;
        }
    }
}
