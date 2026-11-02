using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// docs/MESSAGES.md against the code: every routed message has a row, the row's id and direction are what the code does,
/// and a message that changes lasting state says what heals it or is on the short list of known gaps below.
static class MessageTableTest
{
    static int failures;
    static void Check(string name, bool ok) { if (!ok) { Console.WriteLine("FAIL " + name); failures++; } }

    /// State messages nothing heals. Fixing one removes it from here and from the table; adding one has to be a decision.
    static readonly string[] KnownGaps = { "Share", "Assigned", "Add", "LeaseReq", "LeaseTaken", "ModShare", "ModRemove", "ModEvent" };

    static readonly string[] Kinds = { "state", "stream", "ask", "effect", "session" };

    class Row { public int Id; public string Name, Dir, Kind, Heal, Note; }

    static List<Row> ReadTable(string text)
    {
        var rows = new List<Row>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (!Regex.IsMatch(line, @"^\| *\d+ *\|")) continue;
            var c = line.Trim('|').Split('|').Select(x => x.Trim()).ToArray();
            Check("a row has six columns: " + line, c.Length == 6);
            if (c.Length == 6) rows.Add(new Row { Id = int.Parse(c[0]), Name = c[1], Dir = c[2], Kind = c[3], Heal = c[4], Note = c[5] });
        }
        return rows;
    }

    /// Class -> const name -> value, over every `const byte` in the source.
    static Dictionary<string, Dictionary<string, int>> ReadConsts(Dictionary<string, string> src)
    {
        var all = new Dictionary<string, Dictionary<string, int>>();
        foreach (string text in src.Values)
        {
            var classes = Regex.Matches(text, @"\bclass (\w+)").Cast<Match>().ToList();
            foreach (Match m in Regex.Matches(text, @"const byte ([^;]+);"))
            {
                string owner = classes.LastOrDefault(k => k.Index < m.Index)?.Groups[1].Value ?? "";
                if (!all.TryGetValue(owner, out var names)) all[owner] = names = new Dictionary<string, int>();
                foreach (Match a in Regex.Matches(m.Groups[1].Value, @"(\w+)\s*=\s*(\d+)")) names[a.Groups[1].Value] = int.Parse(a.Groups[2].Value);
            }
        }
        return all;
    }

    static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        string sourceRoot = Path.Combine(root, "src", "TLDRevamp");
        var src = Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllText);
        string allSource = string.Join("\n", src.Values);
        var consts = ReadConsts(src);
        var rows = ReadTable(File.ReadAllText(Path.Combine(root, "docs", "MESSAGES.md")));
        Check("the table has rows", rows.Count > 0);
        Check("ids are unique", rows.Select(r => r.Id).Distinct().Count() == rows.Count);
        Check("sorted by id", rows.Select(r => r.Id).SequenceEqual(rows.Select(r => r.Id).OrderBy(i => i)));

        // what the dispatchers route: Mp.cs's two switches, then Entities' own
        string mp = File.ReadAllText(Path.Combine(sourceRoot, "Net", "Mp.cs"));
        int serverAt = mp.IndexOf("switch (type)"), serverEnd = mp.IndexOf("void OnTransport", serverAt), clientAt = mp.IndexOf("switch (type)", serverEnd);
        Check("Mp.cs has its two dispatchers", serverAt > 0 && serverEnd > serverAt && clientAt > serverEnd);
        string entities = string.Join("\n", src.Where(p => Path.GetFileName(p.Key).StartsWith("Entities")).Select(p => p.Value));
        string Method(string name) => Regex.Match(entities, @"void " + name + @"\(.*?\n        \}\n", RegexOptions.Singleline).Value;
        var toServer = Routed(mp.Substring(serverAt, serverEnd - serverAt), "Entities.ServerReceive(", Method("ServerReceive"), consts);
        var toClient = Routed(mp.Substring(clientAt), "Entities.ClientReceive(", Method("ClientReceive"), consts);
        Check("the dispatchers route messages", toServer.Count > 10 && toClient.Count > 10);

        var byId = rows.ToDictionary(r => r.Id, r => r, EqualityComparer<int>.Default);
        foreach (int id in toServer.Concat(toClient).Distinct().OrderBy(i => i))
            Check("a routed message has a row: id " + id, byId.ContainsKey(id));

        var names = consts.SelectMany(k => k.Value).ToLookup(n => n.Key, n => n.Value);
        foreach (var r in rows)
        {
            Check($"{r.Id} {r.Name}: the code has this name at this id", names[r.Name].Contains(r.Id));
            Check($"{r.Id} {r.Name}: kind '{r.Kind}' is one of the five", Kinds.Contains(r.Kind));
            bool s = toServer.Contains(r.Id), c = toClient.Contains(r.Id);
            string dir = s && c ? "both" : s ? "c>s" : c ? "s>c" : "none";
            Check($"{r.Id} {r.Name}: direction '{r.Dir}', the dispatchers say {dir}", r.Dir == dir || (dir == "none" && r.Dir == "transport"));
            bool lasting = r.Kind == "state" || r.Kind == "stream";
            if (lasting)
            {
                bool gap = r.Heal == "gap";
                var ids = Regex.Matches(r.Heal, @"`(\w+)`").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
                Check($"{r.Id} {r.Name}: says what heals it", gap || ids.Count > 0);
                Check($"{r.Id} {r.Name}: a stream is healed by the next message, it can't be a gap", !(gap && r.Kind == "stream"));
                Check($"{r.Id} {r.Name}: a gap says what stays different", !gap || r.Note.Length > 0);
                foreach (string id in ids) Check($"{r.Id} {r.Name}: `{id}` exists in the source", Regex.IsMatch(allSource, @"\b" + id + @"\b"));
            }
            else Check($"{r.Id} {r.Name}: only state and stream rows have a heal path", r.Heal == "-");
            Check($"{r.Id} {r.Name}: is a gap exactly when the list below says so", (r.Heal == "gap") == KnownGaps.Contains(r.Name));
        }
        foreach (string gap in KnownGaps) Check("the known gap is in the table: " + gap, rows.Any(r => r.Name == gap));

        Console.WriteLine(failures == 0 ? $"ALL PASS ({rows.Count} messages, {KnownGaps.Length} known gaps)" : failures + " failures");
        return failures == 0 ? 0 : 1;
    }

    /// The message ids a dispatcher accepts. The labels in front of a call to Entities' receiver only count if it has a case
    /// for them: the lists name a few it ignores.
    static HashSet<int> Routed(string dispatcher, string forward, string receiver, Dictionary<string, Dictionary<string, int>> consts)
    {
        var ids = Labels(dispatcher, consts);
        var handled = Labels(receiver, consts);
        for (int at = dispatcher.IndexOf(forward); at >= 0; at = dispatcher.IndexOf(forward, at + 1))
        {
            int start = dispatcher.LastIndexOf("break;", at);
            foreach (int id in Labels(dispatcher.Substring(start, at - start), consts)) if (!handled.Contains(id)) ids.Remove(id);
        }
        return ids;
    }

    static HashSet<int> Labels(string code, Dictionary<string, Dictionary<string, int>> consts)
    {
        var ids = new HashSet<int>();
        foreach (Match m in Regex.Matches(code, @"case (?:(\w+)\.)?(\w+):"))
        {
            string owner = m.Groups[1].Success ? m.Groups[1].Value : "Entities";
            if (consts.TryGetValue(owner, out var names) && names.TryGetValue(m.Groups[2].Value, out int id)) { ids.Add(id); continue; }
            // a file's nested class can stand in front of a const of the outer one: a name only one class declares is that one
            var others = consts.Values.Where(n => n.ContainsKey(m.Groups[2].Value)).Select(n => n[m.Groups[2].Value]).Distinct().ToList();
            if (others.Count == 1) ids.Add(others[0]);
        }
        return ids;
    }
}
