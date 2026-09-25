using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ChatParser.Tests
{
    // Oracle for the backlog-speed rewrite: the plugin's ORIGINAL parser (regexes, ParseLine and
    // the sequential StreamReader loop from ParseFilesWorker), copied verbatim so the new
    // parallel parser can be compared message-for-message against it on real logs.
    internal sealed class RefMsg
    {
        public DateTime Time;
        public string ChannelKey, Speaker, Text;
        public bool Outgoing;

        public override string ToString()
        {
            return Time.ToString("yyyy-MM-dd HH:mm:ss") + " " + ChannelKey + " " + (Outgoing ? ">" : "<") + Speaker + ": " + Text;
        }
    }

    internal static class ReferenceParser
    {
        private static readonly Regex RxTimestamp = new Regex(
            @"^\((?<unix>\d{9,10})\)\[[^\]]{20,26}\]\s(?<body>.*)$", RegexOptions.Compiled);
        private static readonly Regex RxLink = new Regex(
            @"\\a(?<kind>[A-Z]+)\s[^:]*:(?<text>[^\\]*)\\/a", RegexOptions.Compiled);
        private static readonly Regex RxChanIn = new Regex(
            "^(?<name>\\S+) tells (?<chan>.+?) \\(\\d+\\), \"(?<msg>.*)\"$", RegexOptions.Compiled);
        private static readonly Regex RxChanOut = new Regex(
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
        private static readonly Regex RxOocIn = new Regex(
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

        private static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Every chat message in the files, in read order (the order the old code assigned Seq in).</summary>
        public static List<RefMsg> ParseFiles(string[] files)
        {
            List<RefMsg> parsed = new List<RefMsg>();
            for (int f = 0; f < files.Length; f++)
            {
                using (FileStream fs = new FileStream(files[f], FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs, Encoding.Default, true))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        RefMsg m = ParseLine(line, DateTime.MinValue);
                        if (m != null) parsed.Add(m);
                    }
                }
            }
            return parsed;
        }

        /// <summary>Lines as the old loop counted them: StreamReader.ReadLine calls.</summary>
        public static long CountLines(string[] files)
        {
            long n = 0;
            foreach (string f in files)
            {
                using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs, Encoding.Default, true))
                {
                    while (sr.ReadLine() != null) n++;
                }
            }
            return n;
        }

        /// <summary>The per-tab lists the old ApplyBacklog built: TELL goes to TELL:player and TELLALL.</summary>
        public static Dictionary<string, List<RefMsg>> GroupByView(List<RefMsg> parsed)
        {
            Dictionary<string, List<RefMsg>> views = new Dictionary<string, List<RefMsg>>(StringComparer.OrdinalIgnoreCase);
            Action<string, RefMsg> add = (k, m) =>
            {
                List<RefMsg> l;
                if (!views.TryGetValue(k, out l)) views[k] = l = new List<RefMsg>();
                l.Add(m);
            };
            foreach (RefMsg m in parsed)
            {
                if (m.ChannelKey == "TELL") { add("TELL:" + m.Speaker, m); add("TELLALL", m); }
                else add(m.ChannelKey, m);
            }
            // Stable by read order within one timestamp, like the old (Time, Seq) sort.
            foreach (string k in new List<string>(views.Keys))
            {
                List<RefMsg> l = views[k];
                List<KeyValuePair<int, RefMsg>> idx = new List<KeyValuePair<int, RefMsg>>();
                for (int i = 0; i < l.Count; i++) idx.Add(new KeyValuePair<int, RefMsg>(i, l[i]));
                idx.Sort((a, b) => { int c = a.Value.Time.CompareTo(b.Value.Time); return c != 0 ? c : a.Key.CompareTo(b.Key); });
                views[k] = idx.ConvertAll(p => p.Value);
            }
            return views;
        }

        private static string CleanMarkup(string s)
        {
            if (s.IndexOf("\\a", StringComparison.Ordinal) < 0) return s;
            return RxLink.Replace(s, delegate(Match m)
            {
                string text = m.Groups["text"].Value;
                return m.Groups["kind"].Value == "ITEM" ? "[" + text + "]" : text;
            });
        }

        public static RefMsg ParseLine(string rawLine, DateTime fallbackTime)
        {
            if (string.IsNullOrEmpty(rawLine) || rawLine.Length < 30) return null;

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

        private static RefMsg Msg(DateTime time, string key, string speaker, string text, bool outgoing)
        {
            RefMsg m = new RefMsg();
            m.Time = time;
            m.ChannelKey = key;
            m.Speaker = speaker;
            m.Text = text;
            m.Outgoing = outgoing;
            return m;
        }
    }
}
