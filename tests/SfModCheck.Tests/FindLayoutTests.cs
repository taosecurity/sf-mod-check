// sf-mod-check find --layout (0.22.0, E) on made-up plugins: the subrecords of the winning copy in file order with
// their signatures, sizes and previews, in a load order and with --plugin, text and JSON; a truncated record lists the
// subrecords before the bad one and names the problem. Each fails on 0.21.0, where --layout is an unknown option.
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using SfModCheck.Conflict;
using Xunit;

namespace SfModCheck.Tests;

public class FindLayoutTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>LayBase.esm (full): misc item LB_Misc "Made-up Gadget" (value 5). LayMod.esm (light) overrides it with
    /// value 9 and adds keyword LM_Key. Plugins.txt lists both; Data\ holds only these.</summary>
    sealed class Game : Sandbox
    {
        public readonly string Data, PluginsTxt, ModPath;
        public readonly FormKey Misc, Key;

        public Game()
        {
            Data = Sub("Data");
            var b = Plugin.New("LayBase.esm", PluginStyle.Full, "test", "made-up base");
            var misc = b.MiscItems.AddNew("LB_Misc");
            misc.Name = "Made-up Gadget";
            misc.Value = 5;
            Misc = misc.FormKey;
            using (var g = new GameData(Data)) PluginWriter.Write(b, Data, g, NoGap);
            var mod = Plugin.New("LayMod.esm", PluginStyle.Light, "test", "made-up mod");
            using (var g = new GameData(Data))
            {
                mod.MiscItems.GetOrAddAsOverride(g.Open("LayBase.esm").MiscItems.First()).Value = 9;
                Key = mod.Keywords.AddNew("LM_Key").FormKey;
                PluginWriter.Write(mod, Data, g, NoGap);
            }
            ModPath = Path.Combine(Data, "LayMod.esm");
            PluginsTxt = Path.Combine(Dir, "Plugins.txt");
            File.WriteAllLines(PluginsTxt, ["*LayBase.esm", "*LayMod.esm"]);
        }

        public (int Code, string Stdout, string Stderr) Find(params string[] args) =>
            ExeTests.RunSplit(new[] { "find" }.Concat(args).Concat(["--data", Data, "--plugins", PluginsTxt]).ToArray());

        public (int Code, string Stdout, string Stderr) FindPlugin(params string[] args) =>
            ExeTests.RunSplit(new[] { "find" }.Concat(args).Concat(["--plugin", ModPath, "--data", Data]).ToArray());
    }

    /// <summary>The subrecords of the copy of a record whose EDID holds editorId, read straight from the file with the
    /// structural pass's own splitter (an independent check of the order and sizes --layout prints).</summary>
    static List<(string Type, int Size)> RawFields(string path, string editorId)
    {
        var want = Encoding.ASCII.GetBytes(editorId + "\0");
        using var f = new RawFile(path);
        List<(string, int)>? got = null;
        var w = new RawWalker(f, deep: true, (_, _, _) => { })
        {
            OnFields = (_, fields) =>
            {
                if (got == null && fields.Count > 0 && fields[0].Type == "EDID" && fields[0].Data.AsSpan().SequenceEqual(want))
                    got = fields.Select(x => (x.Type, x.Data.Length)).ToList();
            },
        };
        w.Walk();
        return got ?? throw new InvalidOperationException($"{editorId} not in {path}");
    }

    [Fact]
    public void Layout_lists_the_winning_copys_subrecords_in_file_order_with_sizes_and_previews()
    {
        using var game = new Game();
        var (code, stdout, stderr) = game.Find("LB_Misc", "--layout");
        Assert.True(code == 0, stdout + stderr);
        var expect = RawFields(game.ModPath, "LB_Misc");   // the winner is LayMod.esm's copy
        Assert.Equal("EDID", expect[0].Type);
        Assert.Contains($"  layout (LayMod.esm, record at 0x", stdout);
        Assert.Contains($", {expect.Count} subrecords", stdout);
        Assert.Matches(@"\n {4} {5}0  EDID {7}8  ""LB_Misc""", stdout);
        Assert.Contains("\"Made-up Gadget\"", stdout);
        // every subrecord in the file's order, at the offset its predecessors add up to
        var lines = stdout.Split('\n').Select(l => l.TrimEnd('\r')).SkipWhile(l => !l.StartsWith("  layout (")).Skip(1)
            .TakeWhile(l => l.StartsWith("    ")).ToList();
        Assert.Equal(expect.Count, lines.Count);
        long off = 0;
        for (int i = 0; i < expect.Count; i++)
        {
            var parts = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(off.ToString(), parts[0]);
            Assert.Equal(expect[i].Type, parts[1]);
            Assert.Equal(expect[i].Size.ToString(), parts[2]);
            off += 6 + expect[i].Size;
        }

        // JSON: the same list under "layout", and nothing new without --layout
        (code, stdout, _) = game.Find(game.Misc.ToString(), "--layout", "--json");
        Assert.Equal(0, code);
        var lay = Assert.Single(JsonDocument.Parse(stdout).RootElement.GetProperty("matches").EnumerateArray()).GetProperty("layout");
        Assert.Equal("LayMod.esm", lay.GetProperty("plugin").GetString());
        Assert.Equal("MISC", lay.GetProperty("signature").GetString());
        Assert.False(lay.GetProperty("compressed").GetBoolean());
        Assert.Equal(expect.Count, lay.GetProperty("subrecordCount").GetInt32());
        var subs = lay.GetProperty("subrecords").EnumerateArray().ToList();
        Assert.Equal(expect.Select(e => e.Type), subs.Select(s => s.GetProperty("signature").GetString()));
        Assert.Equal(expect.Select(e => (long)e.Size), subs.Select(s => s.GetProperty("size").GetInt64()));
        Assert.Equal("\"LB_Misc\"", subs[0].GetProperty("preview").GetString());
        Assert.Equal(lay.GetProperty("bodySize").GetInt64(), expect.Sum(e => 6L + e.Size));
        Assert.False(lay.TryGetProperty("problem", out _));

        (code, stdout, _) = game.Find(game.Misc.ToString(), "--json");
        Assert.Equal(0, code);
        Assert.False(Assert.Single(JsonDocument.Parse(stdout).RootElement.GetProperty("matches").EnumerateArray()).TryGetProperty("layout", out _));
        (code, stdout, _) = game.Find("LB_Misc");
        Assert.DoesNotContain("layout (", stdout);
    }

    [Fact]
    public void Layout_works_with_plugin_for_one_record_and_for_the_whole_plugin_listing()
    {
        using var game = new Game();
        var (code, stdout, stderr) = game.FindPlugin($"{game.Key}", "--layout", "--verbose");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains($"layout: {game.Key} in LayMod.esm: record at 0x", stderr);   // --verbose logs the read
        Assert.Contains("  layout (LayMod.esm, record at 0x", stdout);
        Assert.Matches(@"\n {4} {5}0  EDID {7}7  ""LM_Key""", stdout);
        Assert.Matches(@"record at 0x[0-9A-F]+\): KYWD [0-9A-F]{8}, form version \d+, flags 0x[0-9A-F]{8}, \d+ bytes, \d+ subrecords", stdout);

        (code, stdout, stderr) = game.FindPlugin("--layout", "--json");
        Assert.True(code == 0, stdout + stderr);
        var ms = JsonDocument.Parse(stdout).RootElement.GetProperty("matches").EnumerateArray().ToList();
        Assert.Equal(2, ms.Count);
        foreach (var m in ms)
        {
            var lay = m.GetProperty("layout");
            Assert.Equal("LayMod.esm", lay.GetProperty("plugin").GetString());
            Assert.Equal(m.GetProperty("type").GetString(), lay.GetProperty("signature").GetString());
            Assert.Equal("EDID", lay.GetProperty("subrecords")[0].GetProperty("signature").GetString());
        }
    }

    [Fact]
    public void A_truncated_record_lists_the_subrecords_before_the_bad_one_and_names_the_problem()
    {
        using var game = new Game();
        // Make LM_Key's last subrecord claim 40 more bytes than the record holds.
        var bytes = File.ReadAllBytes(game.ModPath);
        var expect = RawFields(game.ModPath, "LM_Key");
        var edid = IndexOf(bytes, Encoding.ASCII.GetBytes("EDID\x07\0LM_Key\0"));
        Assert.True(edid > 0);
        long at = edid;
        for (int i = 0; i < expect.Count - 1; i++) at += 6 + expect[i].Size;
        Assert.Equal(expect[^1].Type, Encoding.ASCII.GetString(bytes, (int)at, 4));
        BitConverter.TryWriteBytes(bytes.AsSpan((int)at + 4, 2), (ushort)(expect[^1].Size + 40));
        var bad = Path.Combine(game.Sub("bad"), "LayMod.esm");
        File.WriteAllBytes(bad, bytes);

        var files = new Dictionary<string, (ModKey Key, Style Style)>(StringComparer.OrdinalIgnoreCase)
        {
            ["LayBase.esm"] = (ModKey.FromFileName("LayBase.esm"), Style.Full),
            ["LayMod.esm"] = (ModKey.FromFileName("LayMod.esm"), Style.Light),
        };
        var p = new PluginInfo(1, "LayMod.esm", false, ["LayBase.esm"], null!) { Path = bad };
        var lay = RecordLayouts.Read(p, game.Key, files, ["LayBase.esm"], RecordLayouts.MaxJson);
        Assert.True(lay.FileOffset > 0);
        Assert.Equal("KYWD", lay.Sig);
        Assert.Equal(expect.Count - 1, lay.Total);
        Assert.Equal(expect.Take(expect.Count - 1).Select(e => e.Type), lay.Fields.Select(f => f.Sig));
        Assert.Equal($"{expect[^1].Type} at offset {at - edid} says {expect[^1].Size + 40} bytes, but only {expect[^1].Size} are left in the record", lay.Problem);
        var text = string.Join("\n", RecordLayouts.Lines(lay));
        Assert.Contains("    problem: ", text);
        Assert.Equal(lay.Problem, JsonDocument.Parse(RecordLayouts.Json(lay).ToJsonString()).RootElement.GetProperty("problem").GetString());

        // a record the walk can't reach (the file is cut inside it) says so instead of failing
        var cut = Path.Combine(game.Sub("cut"), "LayMod.esm");
        File.WriteAllBytes(cut, bytes[..(edid + 3)]);
        lay = RecordLayouts.Read(p with { Path = cut }, game.Key, files, ["LayBase.esm"], RecordLayouts.MaxJson);
        Assert.Equal(-1, lay.FileOffset);
        Assert.StartsWith("the record wasn't reached: the file can't be read past a bad spot", lay.Problem);
        Assert.StartsWith("  layout (LayMod.esm): can't be shown: the record wasn't reached", RecordLayouts.Lines(lay).Single());
    }

    [Fact]
    public void Parse_handles_XXXX_stray_bytes_and_the_cap_and_previews_stay_short_and_on_one_line()
    {
        var ms = new MemoryStream();
        void Field(string sig, byte[] data, bool big = false)
        {
            if (big) { ms.Write(Encoding.ASCII.GetBytes("XXXX")); ms.Write(BitConverter.GetBytes((ushort)4)); ms.Write(BitConverter.GetBytes((uint)data.Length)); }
            ms.Write(Encoding.ASCII.GetBytes(sig));
            ms.Write(BitConverter.GetBytes((ushort)(big ? 0 : data.Length)));
            ms.Write(data);
        }
        Field("EDID", Encoding.ASCII.GetBytes("Line\nBreak\0"));
        Field("DATA", BitConverter.GetBytes(0x0001295Au));
        Field("BIGF", new byte[70000], big: true);
        Field("ONE_", [7]);
        ms.Write([1, 2, 3]);   // stray bytes
        var body = ms.ToArray();

        var (fields, total, problem) = RecordLayouts.Parse(body, 10);
        Assert.Equal(4, total);
        Assert.Equal(["EDID", "DATA", "BIGF", "ONE_"], fields.Select(f => f.Sig));
        Assert.Equal("\"Line\\nBreak\"", fields[0].Preview);
        Assert.Equal("0x0001295A", fields[1].Preview);
        Assert.True(fields[2].Big);
        Assert.Equal(70000, fields[2].Size);
        Assert.Equal(new string('0', 32) + "...", fields[2].Preview);
        Assert.Equal(17 + 10 + 10, fields[2].Offset);   // after EDID (6+11) DATA (6+4) and the unlisted XXXX (10)
        Assert.Equal("7", fields[3].Preview);
        Assert.Equal($"3 stray bytes at offset {body.Length - 3}, too few for a subrecord header", problem);

        (fields, total, _) = RecordLayouts.Parse(body, 2);
        Assert.Equal(4, total);
        Assert.Equal(2, fields.Count);

        var longText = Encoding.UTF8.GetBytes(new string('a', 200) + "\0");
        Assert.Equal("\"" + new string('a', RecordLayouts.PreviewChars) + "...\"", RecordLayouts.Preview(longText));
        Assert.Equal("(empty)", RecordLayouts.Preview([]));
        Assert.Equal("0x00325B1B", RecordLayouts.Preview([0x1B, 0x5B, 0x32, 0x00]));
        Assert.Equal("1b5b324a00", RecordLayouts.Preview([0x1B, 0x5B, 0x32, 0x4A, 0x00]));   // an escape byte is never text
    }

    [Fact]
    public void ReadMany_reads_several_records_of_a_plugin_in_one_pass_as_Read_does_each()
    {
        // Code review round 1: a --plugin listing with --layout walked the whole file once per listed record.
        using var game = new Game();
        var files = new Dictionary<string, (ModKey Key, Style Style)>(StringComparer.OrdinalIgnoreCase)
        {
            ["LayBase.esm"] = (ModKey.FromFileName("LayBase.esm"), Style.Full),
            ["LayMod.esm"] = (ModKey.FromFileName("LayMod.esm"), Style.Light),
        };
        var p = new PluginInfo(1, "LayMod.esm", false, ["LayBase.esm"], null!) { Path = game.ModPath };
        var missing = new FormKey(ModKey.FromFileName("LayMod.esm"), 0xFFE);
        var many = RecordLayouts.ReadMany(p, [game.Misc, game.Key, missing], files, ["LayBase.esm"], RecordLayouts.MaxJson);
        Assert.Equal(3, many.Count);
        foreach (var k in new[] { game.Misc, game.Key })
        {
            var one = RecordLayouts.Read(p, k, files, ["LayBase.esm"], RecordLayouts.MaxJson);
            Assert.True(one.FileOffset > 0);
            Assert.Equal(one.FileOffset, many[k].FileOffset);
            Assert.Equal(one.Fields, many[k].Fields);
            Assert.Null(many[k].Problem);
        }
        Assert.Equal("MISC", many[game.Misc].Sig);
        Assert.Equal("KYWD", many[game.Key].Sig);
        Assert.Equal("the record wasn't found in the file's raw layout", many[missing].Problem);
    }

    [Fact]
    public void A_cut_XXXX_says_the_data_ran_out_not_a_wrong_size()
    {
        var body = Encoding.ASCII.GetBytes("XXXX").Concat(BitConverter.GetBytes((ushort)4)).Concat(new byte[] { 1, 2 }).ToArray();
        var (_, _, problem) = RecordLayouts.Parse(body, 10);
        Assert.Equal("XXXX at offset 0 says 4 bytes, but only 2 are left in the record", problem);
    }

    static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
