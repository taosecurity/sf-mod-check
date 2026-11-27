// `edit` (0.25.0), plan step 2: the raw writer, --set-light and --set-text (design tests 1 to 6, NOTES.md "## 0.25.0
// (SmcDialogue)"). Every plugin is made up by World; no game data.
using System.Text;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Xunit;

namespace SfModCheck.Tests;

public class EditTests
{
    // a made-up INFO with two responses, a MESG with a description, and the clean plugin's records
    static void Lines(StarfieldMod mod)
    {
        var q = mod.Quests.First();
        var topic = q.DialogTopics.First();
        var info = new DialogResponses(mod) { EditorID = "SFM_TwoLines" };
        info.Responses.Add(new DialogResponse { ResponseText = "first line" });
        info.Responses.Add(new DialogResponse { ResponseText = "second line" });
        topic.Responses.Add(info);
        var msg = new Message(mod) { EditorID = "SFM_Message", Description = "an old message" };
        mod.Messages.Add(msg);
    }

    static Style? StyleIn(World w, string name) => RawHeader.Read(Path.Combine(w.Dir, name))?.Style;

    static EditResult Apply(World w, string path, EditPlan plan, Func<string, string?>? masterPath = null) =>
        PluginEdit.Apply(File.ReadAllBytes(path), Path.GetFileName(path), n => StyleIn(w, n), plan, path,
            masterPath ?? (n => File.Exists(Path.Combine(w.Dir, n)) ? Path.Combine(w.Dir, n) : null));

    static EditPlan Text(string record, string text, int response = 1) => new() { Texts = { new TextEdit(record, response, text) } };

    /// <summary>Every record's bytes by (signature, stored FormID).</summary>
    static Dictionary<(string, uint), byte[]> Records(byte[] b) =>
        Bytes.Items(b).Where(i => !i.Group).ToDictionary(i => (i.Sig, Bytes.U32(b, i.Off + 12)), i => b.AsSpan((int)i.Off, 24 + (int)Bytes.U32(b, i.Off + 4)).ToArray());

    /// <summary>The deep raw walk finds no problem (record and group sizes add up, fields split, zlib decodes).</summary>
    static void Walks(string path)
    {
        var problems = new List<string>();
        using var f = new RawFile(path);
        new RawWalker(f, deep: true, (sev, off, text) => problems.Add($"{sev} {off}: {text}")).Walk();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    static string Save(World w, string name, byte[] b)
    {
        var p = Path.Combine(w.Dir, name + ".esm");
        File.WriteAllBytes(p, b);
        return p;
    }

    static uint OwnId(World w, byte[] b, string sig) =>
        Bytes.Items(b).Where(i => !i.Group && i.Sig == sig).Select(i => Bytes.U32(b, i.Off + 12)).First();

    // 1. --set-light on a full plugin with own IDs inside 0x800-0xFFF: only the flag changes
    [Fact]
    public void SetLight_ChangesOnlyTheFlag()
    {
        using var w = new World();
        var full = w.Patch("full", b => { Bytes.SetU32(b, 8, Bytes.U32(b, 8) & ~Flags.Light); return b; });
        var r = Apply(w, full, new EditPlan { SetLight = true });
        Assert.Equal(w.CleanBytes, r.Output);
        Assert.Equal("type: full -> light", Assert.Single(r.Changes).What);
    }

    // 2. --set-light refusals
    [Theory]
    [InlineData(0x7FFu)]
    [InlineData(0x1000u)]
    public void SetLight_RefusedForAnOwnIdOutsideTheLightRange(uint local)
    {
        using var w = new World();
        var bad = w.Patch("outside", b =>
        {
            Bytes.SetU32(b, 8, Bytes.U32(b, 8) & ~Flags.Light);
            var kw = Bytes.Items(b).First(i => !i.Group && i.Sig == "KYWD");
            // own IDs written full style: top byte = the number of full masters (1, TestFull)
            Bytes.SetU32(b, kw.Off + 12, 0x01000000 | local);
            return b;
        });
        var e = Assert.Throws<EditRefusedException>(() => Apply(w, bad, new EditPlan { SetLight = true }));
        Assert.Contains($"KYWD {local:X6}", e.Reason);
        Assert.Contains("1 own record ", e.Reason);
    }

    [Theory]
    [InlineData(Flags.Light, "light")]
    [InlineData(Flags.Medium, "medium")]
    public void SetLight_RefusedWhenNotFull(uint flag, string style)
    {
        using var w = new World();
        var p = w.Patch("styled", b => { Bytes.SetU32(b, 8, (Bytes.U32(b, 8) & ~Flags.Light) | flag); return b; });
        var e = Assert.Throws<EditRefusedException>(() => Apply(w, p, new EditPlan { SetLight = true }));
        Assert.Contains($"already a {style} plugin", e.Reason);
    }

    // 3. --set-text on INFO responses 1 and 2 and a MESG, longer and shorter: only that field changes, sizes add up
    [Theory]
    [InlineData("SFM_TwoLines", 1, "a much longer first line than before, with ü and ✓")]
    [InlineData("SFM_TwoLines", 2, "2nd")]
    [InlineData("SFM_Message", 1, "a new message that is longer")]
    [InlineData("SFM_Message", 1, "short")]
    public void SetText_ChangesOnlyThatField(string edid, int response, string text)
    {
        using var w = new World();
        var input = w.Build("lines", Lines);
        var inBytes = File.ReadAllBytes(input);
        var r = Apply(w, input, Text(edid, text, response));
        var outPath = Save(w, "lines-out", r.Output);
        Walks(outPath);

        var before = Records(inBytes);
        var after = Records(r.Output);
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        var changed = before.Keys.Where(k => !before[k].AsSpan().SequenceEqual(after[k])).ToList();
        var key = Assert.Single(changed);
        Assert.Equal(edid == "SFM_Message" ? "MESG" : "INFO", key.Item1);

        // the changed record differs only in that one field
        var oldF = Subrecords.Parse(before[key].AsSpan(24), out _);
        var newF = Subrecords.Parse(after[key].AsSpan(24), out _);
        Assert.Equal(oldF.Select(f => f.Type), newF.Select(f => f.Type));
        var field = key.Item1 == "MESG" ? "DESC" : "NAM1";
        int seen = 0;
        for (int i = 0; i < oldF.Count; i++)
        {
            var target = oldF[i].Type == field && ++seen == response;
            if (target) Assert.Equal(Encoding.UTF8.GetBytes(text + "\0"), newF[i].Data);
            else Assert.Equal(oldF[i].Data, newF[i].Data);
        }

        // Mutagen reads the new text back (it reads in-plugin text as Windows-1252, so only ASCII compares as text;
        // the UTF-8 bytes are checked above)
        Assert.Equal(edid == "SFM_Message" ? "description (DESC)" : $"response {response} text (NAM1)", Assert.Single(r.Changes).What);
        if (text.Any(c => c > 127)) return;
        using var mod = w.Game.OpenPlugin(outPath);
        if (key.Item1 == "MESG") Assert.Equal(text, mod.Messages.Single(m => m.EditorID == "SFM_Message").Description?.String);
        else
        {
            var info = mod.Quests.SelectMany(q => q.DialogTopics).SelectMany(t => t.Responses).Single(i => i.EditorID == "SFM_TwoLines");
            Assert.Equal(response == 1 ? text : "first line", info.Responses[0].ResponseText.String);
            Assert.Equal(response == 2 ? text : "second line", info.Responses[1].ResponseText.String);
        }
    }

    // 4. --set-text on a compressed INFO: recompressed, still flagged, reads back
    [Fact]
    public void SetText_RecompressesACompressedInfo()
    {
        using var w = new World();
        var plain = File.ReadAllBytes(w.Build("lines", Lines));
        var infos = Bytes.Items(plain).Where(i => !i.Group && i.Sig == "INFO").ToList();
        var two = infos.Single(i => RecordIndex.EditorId(plain.AsSpan((int)i.Off + 24, (int)Bytes.U32(plain, i.Off + 4))) == "SFM_TwoLines");
        var packed = Bytes.Compress(plain, two, 0);
        var input = Save(w, "packed", packed);
        Walks(input);

        var r = Apply(w, input, Text("SFM_TwoLines", "packed and edited", 2));
        var outPath = Save(w, "packed-out", r.Output);
        Walks(outPath);
        var rec = Bytes.Items(r.Output).Single(i => !i.Group && i.Sig == "INFO" && Bytes.U32(r.Output, i.Off + 12) == Bytes.U32(plain, two.Off + 12));
        Assert.NotEqual(0u, Bytes.U32(r.Output, rec.Off + 8) & Flags.Compressed);
        // the other records are untouched
        var before = Records(packed);
        var after = Records(r.Output);
        Assert.Single(before.Keys, k => !before[k].AsSpan().SequenceEqual(after[k]));

        using var mod = w.Game.OpenPlugin(outPath);
        var info = mod.Quests.SelectMany(q => q.DialogTopics).SelectMany(t => t.Responses).Single(i => i.EditorID == "SFM_TwoLines");
        Assert.Equal("first line", info.Responses[0].ResponseText.String);
        Assert.Equal("packed and edited", info.Responses[1].ResponseText.String);
    }

    // 5. --set-text refusals
    [Fact]
    public void SetText_RefusedForALocalizedPlugin()
    {
        using var w = new World();
        var input = w.Build("lines", Lines);
        var loc = Save(w, "localized", File.ReadAllBytes(input).Also(b => Bytes.SetU32(b, 8, Bytes.U32(b, 8) | Flags.Localized)));
        var e = Assert.Throws<EditRefusedException>(() => Apply(w, loc, Text("SFM_TwoLines", "x")));
        Assert.Contains("localized", e.Reason);
        Assert.Contains("Strings\\localized_<language>.STRINGS", e.Reason);
    }

    [Theory]
    [InlineData("SFM_NoSuchLine", 1, "holds no EditorID SFM_NoSuchLine")]
    [InlineData("SFM_Keyword", 1, "is a KYWD record")]
    [InlineData("SFM_TwoLines", 0, "has 2 responses, so it has no response 0")]
    [InlineData("SFM_TwoLines", 3, "has 2 responses, so it has no response 3")]
    [InlineData("SFM_Message", 2, "--response can only be 1")]
    [InlineData("000FFF:lines.esm", 1, "holds no record 000FFF:lines.esm")]
    [InlineData("12:34", 1, "isn't a FormKey")]
    public void SetText_Refused(string record, int response, string why)
    {
        using var w = new World();
        var input = w.Build("lines", Lines);
        var e = Assert.Throws<EditRefusedException>(() => Apply(w, input, Text(record, "x", response)));
        Assert.Contains(why, e.Reason);
    }

    [Fact]
    public void Refused_WithNoEditAndForANonPlugin()
    {
        using var w = new World();
        var input = w.Build("lines", Lines);
        Assert.Contains("No edit", Assert.Throws<EditRefusedException>(() => Apply(w, input, new EditPlan())).Reason);
        var junk = Save(w, "junk", Encoding.ASCII.GetBytes("PK this is not a plugin at all, just text"));
        Assert.Contains("not a plugin", Assert.Throws<EditRefusedException>(() => Apply(w, junk, new EditPlan { SetLight = true })).Reason);
        // a record size that runs past the end of its group
        var cut = Save(w, "cut", File.ReadAllBytes(input)[..^10]);
        Assert.Contains("can't be read to the end", Assert.Throws<EditRefusedException>(() => Apply(w, cut, new EditPlan { SetLight = true })).Reason);
    }

    // 6. --set-text by EditorID, FormKey and console FormID give the same file
    [Fact]
    public void SetText_ByEditorIdFormKeyAndConsoleId()
    {
        using var w = new World();
        var input = w.Build("lines", Lines);
        var b = File.ReadAllBytes(input);
        var msg = OwnId(w, b, "MESG");
        var local = msg & 0xFFF;   // a light plugin: 12 bits
        var byName = Apply(w, input, Text("sfm_message", "same")).Output;   // letter case doesn't matter
        Assert.Equal(byName, Apply(w, input, Text($"{local:X6}:lines.esm", "same")).Output);
        Assert.Equal(byName, Apply(w, input, Text($"FE012{local:X3}", "same")).Output);   // its place in the load order is ignored
        Assert.Equal(byName, Apply(w, input, Text($"0xFE7FF{local:X3}", "same")).Output);
        Assert.Equal("set-text", Assert.Single(Apply(w, input, Text($"FE012{local:X3}", "same")).Changes).Edit);
    }

    // code review 0.25.0 round 5: a console FormID keeps only its local part, so it is refused when that part also
    // matches an override the plugin carries (it could mean either record); the FormKey still names each one
    [Fact]
    public void SetText_ConsoleIdRefusedWhenItAlsoMatchesAnOverride()
    {
        using var w = new World();
        var input = w.Build("lines", m =>
        {
            Lines(m);
            m.Keywords.GetOrAddAsOverride(w.FullMod.Keywords.Single(k => k.FormKey == w.FullKw));
        });
        var local = w.FullKw.ID & 0xFFF;
        var e = Assert.Throws<EditRefusedException>(() => Apply(w, input, Text($"FE000{local:X3}", "x")));
        Assert.Contains("could mean an override", e.Reason);
        Assert.Contains($"{w.FullKw.ID:X6}:{World.Full}", e.Reason);
        Assert.Contains("FormKey", e.Fix);
        // an own record whose local part matches no override is still found by its console FormID
        var msg = OwnId(w, File.ReadAllBytes(input), "MESG") & 0xFFF;
        if (msg != local) Assert.Single(Apply(w, input, Text($"FE012{msg:X3}", "same")).Changes);
    }

    // code review 0.25.0 round 5: a refusal that names a master read from the plugin prints no control characters
    [Fact]
    public void Refusal_PrintsNoControlCharactersFromThePlugin()
    {
        using var w = new World();
        var bad = w.Patch("esc", b =>
        {
            var name = "TestLight.esm"u8.ToArray();
            var at = Enumerable.Range(0, b.Length - name.Length).First(i => b.AsSpan(i, name.Length).SequenceEqual(name));
            b[at + 5] = 0x1B;   // "TestL<ESC>ght.esm", a master that doesn't exist
            Bytes.SetU32(b, 8, Bytes.U32(b, 8) & ~Flags.Light);
            return b;
        });
        var (code, stdout, stderr) = InProcess(bad, "--out", Path.Combine(w.Dir, "esc-out.esm"), "--set-light", "--data", w.Dir);
        Assert.True(code == 2, stdout + stderr);
        Assert.Contains("RESULT: REFUSED", stdout);
        Assert.DoesNotContain('\u001b', stdout + stderr);
        Assert.Contains("TestL\\u001Bght.esm", stdout + stderr, StringComparison.OrdinalIgnoreCase);
    }

    // set-light and set-text together: both changes, one pass
    [Fact]
    public void SetLightAndSetText_Together()
    {
        using var w = new World();
        var light = File.ReadAllBytes(w.Build("lines", Lines));
        var full = Save(w, "both", ((byte[])light.Clone()).Also(b => Bytes.SetU32(b, 8, Bytes.U32(b, 8) & ~Flags.Light)));
        var r = Apply(w, full, new EditPlan { SetLight = true, Texts = { new TextEdit("SFM_Message", 1, "both") } });
        Assert.Equal(Apply(w, Save(w, "lightonly", light), Text("SFM_Message", "both")).Output, r.Output);
        Assert.Equal(["set-light", "set-text"], r.Changes.Select(c => c.Edit));
    }

    // --drop-master (plan step 3): design tests 7, 8, 9 and the drop-master half of 12

    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>A made-up master with one keyword, written into the test folder; returns the keyword.</summary>
    static FormKey ExtraMaster(World w, string name, PluginStyle style)
    {
        var m = Plugin.New(name, style, "test", "made-up extra master");
        var kw = m.Keywords.AddNew(Path.GetFileNameWithoutExtension(name) + "_Keyword");
        using (var g = new GameData(w.Dir)) PluginWriter.Write(m, w.Dir, g, NoGap);
        return kw.FormKey;
    }

    /// <summary>A plugin with exactly these masters, in this order (unused ones included), written as &lt;file&gt;.esm.</summary>
    static string BuildWith(World w, string file, PluginStyle style, string[] masters, Action<StarfieldMod> fill)
    {
        using var g = new GameData(w.Dir);
        foreach (var m in masters) g.Open(m);
        var mod = Plugin.New("SfmDrop.esm", style, "test", "drop-master test plugin");
        fill(mod);
        var dir = w.Sub("build-" + file);
        PluginWriter.Write(mod, dir, g, new WriteOptions { GapCheck = false, FixEffects = false, Masters = masters.Select(m => ModKey.FromFileName(m)).ToList() });
        var final = Path.Combine(w.Dir, file + ".esm");
        File.Move(Path.Combine(dir, "SfmDrop.esm"), final);
        return final;
    }

    static List<string> MastersOf(byte[] b) =>
        Subrecords.Parse(b.AsSpan(24, (int)Bytes.U32(b, 4)), out _).Where(f => f.Type == "MAST").Select(f => Encoding.UTF8.GetString(f.Data).TrimEnd('\0')).ToList();

    static IdMap MapOf(World w, byte[] b, string self) =>
        new(self, (Bytes.U32(b, 8) & Flags.Light) != 0 ? Style.Light : (Bytes.U32(b, 8) & Flags.Medium) != 0 ? Style.Medium : Style.Full,
            MastersOf(b).Select(m => (m, StyleIn(w, m) ?? Style.Full)));

    /// <summary>Every record ID and FormID group label of the output names the same (file, local ID) under the new
    /// master list as the input's does under the old one, and Mutagen reads the same records and links.</summary>
    static void SameMeaning(World w, string inPath, string outPath)
    {
        byte[] a = File.ReadAllBytes(inPath), b = File.ReadAllBytes(outPath);
        IdMap ma = MapOf(w, a, "self"), mb = MapOf(w, b, "self");
        var ia = Bytes.Items(a);
        var ib = Bytes.Items(b);
        Assert.Equal(ia.Select(i => i.Sig), ib.Select(i => i.Sig));
        for (int i = 0; i < ia.Count; i++)
        {
            if (ia[i].Group && ia[i].GroupType is not (1 or 6 or 7 or 8 or 9 or 10)) continue;
            var (fa, la, _) = ma.Decode(Bytes.U32(a, ia[i].Off + (ia[i].Group ? 8 : 12)));
            var (fb, lb, _) = mb.Decode(Bytes.U32(b, ib[i].Off + (ib[i].Group ? 8 : 12)));
            Assert.Equal((fa, la), (fb, lb));
        }
        using var g = new GameData(w.Dir);
        foreach (var m in MastersOf(a)) g.Open(m);
        using var ra = g.OpenPlugin(inPath);
        using var rb = g.OpenPlugin(outPath);
        static List<string> Seen(IStarfieldModGetter mod) => mod.EnumerateMajorRecords().SelectMany(r =>
            new[] { r.FormKey }.Concat(r.EnumerateFormLinks().Where(l => !l.IsNull).Select(l => l.FormKey))
                .Select(k => (k.ModKey == mod.ModKey ? "self" : k.ModKey.FileName.String) + ":" + k.ID.ToString("X6"))).ToList();
        Assert.Equal(Seen(ra), Seen(rb));
    }

    // 7. a full master in the middle: own IDs, a later full master's IDs, group labels, ONAM and a payload FormID in a
    // compressed record are all renumbered
    [Fact]
    public void DropMaster_FullMasterInTheMiddle()
    {
        using var w = new World();
        ExtraMaster(w, "UnusedFull.esm", PluginStyle.Full);
        var later = ExtraMaster(w, "LaterFull.esm", PluginStyle.Full);
        var built = BuildWith(w, "drop7-plain", PluginStyle.Full, [World.Full, "UnusedFull.esm", "LaterFull.esm"], mod =>
        {
            var kw = mod.Keywords.AddNew("SFM_Keyword");
            var misc = mod.MiscItems.AddNew("SFM_Misc");
            misc.Keywords = [kw.ToLink<IKeywordGetter>(), later.ToLink<IKeywordGetter>(), w.FullKw.ToLink<IKeywordGetter>()];
            var q = mod.Quests.AddNew("SFM_Quest");
            var topic = new DialogTopic(mod) { EditorID = "SFM_Topic", Quest = q.ToLink<IQuestGetter>() };
            q.DialogTopics.Add(topic);
            var info = new DialogResponses(mod) { EditorID = "SFM_Info" };
            info.Responses.Add(new DialogResponse { Emotion = later.ToLink<IKeywordGetter>(), ResponseText = "hello" });
            topic.Responses.Add(info);
            var cell = new Cell(mod) { EditorID = "SFM_Cell" };
            cell.Temporary.Add(new PlacedObject(mod) { Base = misc.ToNullableLink<IPlaceableObjectGetter>() });
            mod.Cells.Add(new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock, SubBlocks = [new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = [cell] }] });
        });
        var plain = File.ReadAllBytes(built);
        // ONAM by hand: LaterFull's keyword (index 2 -> 1) and TestFull's (index 0, unchanged)
        var onam = BitConverter.GetBytes(0x02000000u | later.ID).Concat(BitConverter.GetBytes(w.FullKw.ID)).ToArray();
        var withOnam = Bytes.RewriteHeader(plain, f => f.Add(("ONAM", onam)));
        var misc = Bytes.Items(withOnam).Single(i => !i.Group && i.Sig == "MISC");
        var input = Save(w, "drop7", Bytes.Compress(withOnam, misc, 0));
        Walks(input);

        var r = Apply(w, input, new EditPlan { DropMaster = "unusedfull.esm" });   // letter case doesn't matter
        var outPath = Save(w, "drop7-out", r.Output);
        Walks(outPath);
        Assert.Equal([World.Full, "LaterFull.esm"], MastersOf(r.Output));
        SameMeaning(w, input, outPath);
        var what = Assert.Single(r.Changes).What;
        Assert.Contains("master UnusedFull.esm removed", what);
        Assert.DoesNotContain(" 0 record IDs", what);
        Assert.DoesNotContain(" 0 group labels", what);
        Assert.Contains(" 1 ONAM entries", what);
        Assert.DoesNotContain(" 0 FormIDs in fields", what);
        // own IDs: top byte 3 -> 2; the MISC is still compressed
        var outMisc = Bytes.Items(r.Output).Single(i => !i.Group && i.Sig == "MISC");
        Assert.Equal(0x02u, Bytes.U32(r.Output, outMisc.Off + 12) >> 24);
        Assert.NotEqual(0u, Bytes.U32(r.Output, outMisc.Off + 8) & Flags.Compressed);
        var outOnam = Subrecords.Parse(r.Output.AsSpan(24, (int)Bytes.U32(r.Output, 4)), out _).Single(f => f.Type == "ONAM").Data;
        Assert.Equal(BitConverter.GetBytes(0x01000000u | later.ID).Concat(BitConverter.GetBytes(w.FullKw.ID)), outOnam);
    }

    // 8. a medium master and a light master, each followed by a used master of the same style
    [Theory]
    [InlineData("UnusedMedium.esm")]
    [InlineData("UnusedLight.esm")]
    public void DropMaster_MediumAndLight(string drop)
    {
        using var w = new World();
        ExtraMaster(w, "UnusedMedium.esm", PluginStyle.Medium);
        ExtraMaster(w, "UnusedLight.esm", PluginStyle.Light);
        string[] masters = [World.Full, "UnusedMedium.esm", World.Medium, "UnusedLight.esm", World.Light];
        var input = BuildWith(w, "drop8", PluginStyle.Light, masters, mod =>
        {
            var kw = mod.Keywords.AddNew("SFM_Keyword");
            mod.MiscItems.AddNew("SFM_Misc").Keywords = [kw.ToLink<IKeywordGetter>(), w.MediumKw.ToLink<IKeywordGetter>(), w.LightKw.ToLink<IKeywordGetter>()];
        });
        var r = Apply(w, input, new EditPlan { DropMaster = drop });
        var outPath = Save(w, "drop8-out", r.Output);
        Walks(outPath);
        Assert.Equal(masters.Where(m => m != drop), MastersOf(r.Output));
        SameMeaning(w, input, outPath);
        Assert.DoesNotContain(" 0 FormIDs in fields", Assert.Single(r.Changes).What);
        // the later master of the same style moved down one place in the stored IDs
        var later = drop == "UnusedMedium.esm" ? 0xFD010000u | w.MediumKw.ID : 0xFE001000u | w.LightKw.ID;
        var moved = drop == "UnusedMedium.esm" ? 0xFD000000u | w.MediumKw.ID : 0xFE000000u | w.LightKw.ID;
        var inMisc = Body(File.ReadAllBytes(input), "MISC");
        var outMisc = Body(r.Output, "MISC");
        Assert.True(Contains(inMisc, later) && !Contains(inMisc, moved));
        // (the old value may come back: after a light drop the plugin's own keyword, also local 800, is stored as
        // FE001800; SameMeaning above checked what every ID names)
        Assert.True(Contains(outMisc, moved));
    }

    static byte[] Body(byte[] b, string sig)
    {
        var i = Bytes.Items(b).Single(x => !x.Group && x.Sig == sig);
        return b.AsSpan((int)i.Off + 24, (int)Bytes.U32(b, i.Off + 4)).ToArray();
    }

    static bool Contains(byte[] hay, uint v)
    {
        var n = BitConverter.GetBytes(v);
        for (int i = 0; i + 4 <= hay.Length; i++) if (hay.AsSpan(i, 4).SequenceEqual(n)) return true;
        return false;
    }

    // 9. refusals: used by a FormLink; used only by a raw value (the checks disagree); master file missing; not a master
    [Fact]
    public void DropMaster_Refused()
    {
        using var w = new World();
        var unusedKw = ExtraMaster(w, "UnusedFull.esm", PluginStyle.Full);
        var input = BuildWith(w, "drop9", PluginStyle.Full, [World.Full, "UnusedFull.esm"], mod =>
        {
            mod.MiscItems.AddNew("SFM_Misc").Keywords = [w.FullKw.ToLink<IKeywordGetter>()];
            mod.Messages.Add(new Message(mod) { EditorID = "SFM_Message", Description = "ABCD" });
        });

        string Why(string path, string drop, Func<string, string?>? mp = null) =>
            Assert.Throws<EditRefusedException>(() => Apply(w, path, new EditPlan { DropMaster = drop }, mp)).Reason;

        Assert.Contains("TestFull.esm is still used: Mutagen finds 1 link", Why(input, World.Full));
        Assert.Contains("isn't a master of drop9.esm (its masters: TestFull.esm, UnusedFull.esm)", Why(input, "Nope.esm"));
        Assert.Contains("UnusedFull.esm can't be found", Why(input, "UnusedFull.esm", _ => null));

        // a text field that holds UnusedFull's keyword ID as stored (index 1): only the raw scan sees it
        var b = File.ReadAllBytes(input);
        var at = Enumerable.Range(0, b.Length - 4).Single(i => b.AsSpan(i, 5).SequenceEqual("ABCD\0"u8));
        Bytes.SetU32(b, at, 0x01000000u | unusedKw.ID);
        var hidden = Save(w, "drop9-hidden", b);
        Assert.Contains("The two use checks disagree on UnusedFull.esm: Mutagen finds 0 uses, the raw scan finds 1.", Why(hidden, "UnusedFull.esm"));

        // and the clean one drops
        Assert.Equal([World.Full], MastersOf(Apply(w, input, new EditPlan { DropMaster = "UnusedFull.esm" }).Output));
    }

    /// <summary>A made-up full master Other.esm with one keyword at this local ID.</summary>
    static Keyword OtherMaster(World w, uint id)
    {
        var other = Plugin.New("Other.esm", PluginStyle.Full, "test", "made-up master with a high ID");
        var kw = new Keyword(new FormKey(other.ModKey, id), StarfieldRelease.Starfield) { EditorID = "Other_Keyword" };
        other.Keywords.Add(kw);
        using (var g = new GameData(w.Dir)) PluginWriter.Write(other, w.Dir, g, NoGap);
        return kw;
    }

    /// <summary>The plugin with these bytes written over its MESG description (made as "ABCDEFGH").</summary>
    static string WithDescription(World w, string input, string name, byte[] bytes)
    {
        var b = File.ReadAllBytes(input);
        var at = Enumerable.Range(0, b.Length - 9).Single(i => b.AsSpan(i, 9).SequenceEqual("ABCDEFGH\0"u8));
        bytes.CopyTo(b, at);
        return Save(w, name, b);
    }

    // code review 0.25.0 round 1: a FormID of a later master in a field Mutagen doesn't read as a link (here a text
    // field), with no link to that record anywhere, would keep its old index; it is refused, not left or guessed
    [Fact]
    public void DropMaster_RefusesAFormIdMutagenDoesNotList()
    {
        using var w = new World();
        ExtraMaster(w, "UnusedFull.esm", PluginStyle.Full);
        OtherMaster(w, 0x414243);
        var input = BuildWith(w, "unread", PluginStyle.Full, [World.Full, "UnusedFull.esm", "Other.esm"], mod =>
        {
            mod.MiscItems.AddNew("SFM_Misc").Keywords = [w.FullKw.ToLink<IKeywordGetter>()];
            mod.Messages.Add(new Message(mod) { EditorID = "SFM_Message", Description = "ABCDEFGH" });
        });
        Assert.Equal([World.Full, "Other.esm"], MastersOf(Apply(w, input, new EditPlan { DropMaster = "UnusedFull.esm" }).Output));

        // Other's keyword as stored (index 2), in the text only
        var hidden = WithDescription(w, input, "unread-hidden", BitConverter.GetBytes(0x02000000u | 0x414243));
        var why = Assert.Throws<EditRefusedException>(() => Apply(w, hidden, new EditPlan { DropMaster = "UnusedFull.esm" })).Reason;
        Assert.Contains("stores 02414243 in its DESC field at byte 0, which names a record of Other.esm (414243)", why);
        Assert.Contains("Mutagen doesn't read that field as a link", why);
        // a number that names no record of Other.esm is left alone
        var noRecord = WithDescription(w, input, "unread-norecord", BitConverter.GetBytes(0x02000000u | 0x414244));
        Assert.Equal([World.Full, "Other.esm"], MastersOf(Apply(w, noRecord, new EditPlan { DropMaster = "UnusedFull.esm" }).Output));
        // and Other.esm missing is a refusal, since the value can't be checked
        Assert.Contains("Other.esm can't be found",
            Assert.Throws<EditRefusedException>(() => Apply(w, hidden, new EditPlan { DropMaster = "UnusedFull.esm" },
                n => n == "Other.esm" ? null : Path.Combine(w.Dir, n))).Reason);
    }

    // code review 0.25.0 round 1: two overlapping windows that both look like a linked FormID (here one byte apart
    // from a real-looking one) are refused; the old scan rewrote the first and skipped the second
    [Fact]
    public void DropMaster_RefusesOverlappingLookalikes()
    {
        using var w = new World();
        ExtraMaster(w, "UnusedFull.esm", PluginStyle.Full);
        var kw = OtherMaster(w, 0x414202);
        var input = BuildWith(w, "overlap", PluginStyle.Full, [World.Full, "UnusedFull.esm", "Other.esm"], mod =>
        {
            mod.MiscItems.AddNew("SFM_Misc").Keywords = [kw.FormKey.ToLink<IKeywordGetter>()];
            mod.Messages.Add(new Message(mod) { EditorID = "SFM_Message", Description = "ABCDEFGH" });
        });
        // bytes 1..4 and 4..7 both read 02414202, Other's keyword as stored
        var hidden = WithDescription(w, input, "overlap-hidden", [0x58, 0x02, 0x42, 0x41, 0x02, 0x42, 0x41, 0x02]);
        var why = Assert.Throws<EditRefusedException>(() => Apply(w, hidden, new EditPlan { DropMaster = "UnusedFull.esm" })).Reason;
        Assert.Contains("DESC field holds two overlapping values that both look like FormIDs (at bytes 1 and 4)", why);
        // apart (bytes 0..3 and 4..7), both are renumbered
        var apart = WithDescription(w, input, "overlap-apart", [0x02, 0x42, 0x41, 0x02, 0x02, 0x42, 0x41, 0x02]);
        var r = Apply(w, apart, new EditPlan { DropMaster = "UnusedFull.esm" });
        var desc = Subrecords.Parse(Body(r.Output, "MESG"), out _).Single(f => f.Type == "DESC").Data;
        Assert.Equal(new byte[] { 0x02, 0x42, 0x41, 0x01, 0x02, 0x42, 0x41, 0x01, 0 }, desc);
    }

    // code review 0.25.0 round 2: a window inside a link window (here byte 1 of a link at byte 0) that happens to name
    // an unlinked record of a renumbered master is not a FormID of its own, so the drop isn't refused for it
    [Fact]
    public void DropMaster_WindowInsideALinkIsNotRefused()
    {
        using var w = new World();
        ExtraMaster(w, "UnusedFull.esm", PluginStyle.Full);
        var other = Plugin.New("Other.esm", PluginStyle.Full, "test", "made-up master with two keywords");
        var kw = new Keyword(new FormKey(other.ModKey, 0x414202), StarfieldRelease.Starfield) { EditorID = "Other_Keyword" };
        other.Keywords.Add(kw);
        other.Keywords.Add(new Keyword(new FormKey(other.ModKey, 0x024142), StarfieldRelease.Starfield) { EditorID = "Other_Unlinked" });
        using (var g = new GameData(w.Dir)) PluginWriter.Write(other, w.Dir, g, NoGap);
        var input = BuildWith(w, "inside", PluginStyle.Full, [World.Full, "UnusedFull.esm", "Other.esm"], mod =>
        {
            mod.MiscItems.AddNew("SFM_Misc").Keywords = [kw.FormKey.ToLink<IKeywordGetter>()];
            mod.Messages.Add(new Message(mod) { EditorID = "SFM_Message", Description = "ABCDEFGH" });
        });
        // links at bytes 0 and 4 (02414202); byte 1 reads 02024142, Other's unlinked keyword
        var b = WithDescription(w, input, "inside-b", [0x02, 0x42, 0x41, 0x02, 0x02, 0x42, 0x41, 0x02]);
        var r = Apply(w, b, new EditPlan { DropMaster = "UnusedFull.esm" });
        var desc = Subrecords.Parse(Body(r.Output, "MESG"), out _).Single(f => f.Type == "DESC").Data;
        Assert.Equal(new byte[] { 0x02, 0x42, 0x41, 0x01, 0x02, 0x42, 0x41, 0x01, 0 }, desc);
    }

    // code review 0.25.0 round 1: --set-text takes "Name", eid:Name and editorid:Name as an EditorID, as find does
    [Fact]
    public void SetText_ForcedEditorIdForms()
    {
        using var w = new World();
        var input = w.Build("forced", Lines);
        foreach (var spec in new[] { "eid:SFM_Message", "\"SFM_Message\"", "editorid:SFM_Message" })
        {
            var r = Apply(w, input, Text(spec, "forced"));
            Assert.Equal("set-text", Assert.Single(r.Changes).Edit);
        }
        Assert.Contains("holds no EditorID 0000ABCD", Assert.Throws<EditRefusedException>(() => Apply(w, input, Text("eid:0000ABCD", "x"))).Reason);
        // an empty EditorID after the prefix or in the quotes is named as such, not taken as a FormKey (round 2)
        foreach (var spec in new[] { "eid:", "editorid: ", "\"\"" })
            Assert.Contains("names no EditorID", Assert.Throws<EditRefusedException>(() => Apply(w, input, Text(spec, "x"))).Reason);
    }

    // 12 (drop-master half). --drop-master with --set-light: the drop's file with the light flag set
    [Fact]
    public void DropMasterAndSetLight_Together()
    {
        using var w = new World();
        ExtraMaster(w, "UnusedFull.esm", PluginStyle.Full);
        var input = BuildWith(w, "drop12", PluginStyle.Full, [World.Full, "UnusedFull.esm"], mod =>
            mod.MiscItems.AddNew("SFM_Misc").Keywords = [mod.Keywords.AddNew("SFM_Keyword").ToLink<IKeywordGetter>(), w.FullKw.ToLink<IKeywordGetter>()]);
        var both = Apply(w, input, new EditPlan { DropMaster = "UnusedFull.esm", SetLight = true });
        var dropOnly = Apply(w, input, new EditPlan { DropMaster = "UnusedFull.esm" }).Output;
        Bytes.SetU32(dropOnly, 8, Bytes.U32(dropOnly, 8) | Flags.Light);
        Assert.Equal(dropOnly, both.Output);
        Assert.Equal(["drop-master", "set-light"], both.Changes.Select(c => c.Edit));
        Walks(Save(w, "drop12-out", both.Output));
    }

    // the temporary file is new, sits in its own new folder next to out under the input's name, and never overwrites
    [Fact]
    public void WriteTemp_MakesANewFileNextToOut()
    {
        using var w = new World();
        var outPath = Path.Combine(w.Dir, "new.esm");
        var a = PluginEdit.WriteTemp(outPath, "in.esm", [1, 2, 3]);
        var b = PluginEdit.WriteTemp(outPath, "in.esm", [4]);
        Assert.NotEqual(a, b);
        Assert.StartsWith(outPath + ".tmp-", a);
        Assert.Equal("in.esm", Path.GetFileName(a));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(a));
        Assert.False(File.Exists(outPath));
        PluginEdit.DeleteTemp(a);
        PluginEdit.DeleteTemp(b);
        Assert.False(Directory.Exists(Path.GetDirectoryName(a)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(b)));
        Assert.Throws<ArgumentException>(() => PluginEdit.WriteTemp(outPath, Path.Combine("..", "x.esm"), [1]));
    }

    // ---- plan step 4: the proof (EditProof.Run) and the refusals of the input and --out; design tests 10 to 13

    static EditOutcome Prove(World w, string input, string outName, EditPlan plan) =>
        EditProof.Run(input, Path.Combine(w.Dir, outName), plan, w.Dir, []);

    /// <summary>Nothing is left next to out: no temporary file or folder.</summary>
    static void NoTemp(World w, string outName) =>
        Assert.Empty(Directory.EnumerateFileSystemEntries(w.Dir, outName + ".tmp-*"));

    static void AllPassed(EditOutcome o)
    {
        Assert.True(o.Kept, string.Join("\n", o.Proof.Select(p => $"{p.Name}: {p.Passed} {p.Reason}")));
        Assert.Equal(["diff", "gap", "check"], o.Proof.Select(p => p.Name));
        Assert.All(o.Proof, p => Assert.True(p.Passed, p.Reason));
    }

    // a kept --set-light: the file at --out is the edit's output, nothing else is left
    [Fact]
    public void Proof_KeepsAProvedSetLight()
    {
        using var w = new World();
        var full = w.Patch("full", b => { Bytes.SetU32(b, 8, Bytes.U32(b, 8) & ~Flags.Light); return b; });
        var o = Prove(w, full, "light.esm", new EditPlan { SetLight = true });
        AllPassed(o);
        Assert.Equal(w.CleanBytes, File.ReadAllBytes(Path.Combine(w.Dir, "light.esm")));
        Assert.Contains("1 header change", o.Proof[0].Reason);
        NoTemp(w, "light.esm");
    }

    // 12 (set-text half). --set-text (two records, one with non-ASCII text) with --set-light, through the proof
    [Fact]
    public void Proof_KeepsSetTextWithSetLight()
    {
        using var w = new World();
        var light = File.ReadAllBytes(w.Build("lines", Lines));
        var full = Save(w, "both", light.Also(b => Bytes.SetU32(b, 8, Bytes.U32(b, 8) & ~Flags.Light)));
        var plan = new EditPlan { SetLight = true, Texts = { new TextEdit("SFM_Message", 1, "both edits"), new TextEdit("SFM_TwoLines", 2, "zweite Zeile ü ✓") } };
        var o = Prove(w, full, "both-out.esm", plan);
        AllPassed(o);
        Assert.Contains("2 changed records", o.Proof[0].Reason);
        Assert.Equal(o.Edit.Output, File.ReadAllBytes(Path.Combine(w.Dir, "both-out.esm")));
        NoTemp(w, "both-out.esm");
    }

    // --drop-master through the proof: the renumbered records are noise (same meaning), so it passes
    [Fact]
    public void Proof_KeepsADropMaster()
    {
        using var w = new World();
        ExtraMaster(w, "UnusedFull.esm", PluginStyle.Full);
        var input = BuildWith(w, "dropproof", PluginStyle.Full, ["UnusedFull.esm", World.Full], mod =>
            mod.MiscItems.AddNew("SFM_Misc").Keywords = [mod.Keywords.AddNew("SFM_Keyword").ToLink<IKeywordGetter>(), w.FullKw.ToLink<IKeywordGetter>()]);
        var o = Prove(w, input, "dropproof-out.esm", new EditPlan { DropMaster = "UnusedFull.esm" });
        AllPassed(o);
        Assert.Equal([World.Full], MastersOf(File.ReadAllBytes(Path.Combine(w.Dir, "dropproof-out.esm"))));
        NoTemp(w, "dropproof-out.esm");
    }

    // 10. a number in a text field that equals a linked ID by accident is renumbered; the proof's diff sees the text
    // change, deletes the file and names the record
    [Fact]
    public void Proof_CatchesAnAccidentalRenumbering()
    {
        using var w = new World();
        ExtraMaster(w, "UnusedFull.esm", PluginStyle.Full);
        var other = Plugin.New("Other.esm", PluginStyle.Full, "test", "made-up master with a high ID");
        var kw = new Keyword(new FormKey(other.ModKey, 0x414243), StarfieldRelease.Starfield) { EditorID = "Other_Keyword" };
        other.Keywords.Add(kw);
        using (var g = new GameData(w.Dir)) PluginWriter.Write(other, w.Dir, g, NoGap);
        var input = BuildWith(w, "accident", PluginStyle.Full, [World.Full, "UnusedFull.esm", "Other.esm"], mod =>
        {
            mod.MiscItems.AddNew("SFM_Misc").Keywords = [kw.FormKey.ToLink<IKeywordGetter>()];
            mod.Messages.Add(new Message(mod) { EditorID = "SFM_Message", Description = "ABCD" });
        });
        // the text now holds Other's keyword as stored (index 2): "CBA" and a 02 byte, a link value by accident
        var b = File.ReadAllBytes(input);
        var at = Enumerable.Range(0, b.Length - 4).Single(i => b.AsSpan(i, 5).SequenceEqual("ABCD\0"u8));
        Bytes.SetU32(b, at, 0x02000000u | 0x414243);
        File.WriteAllBytes(input, b);

        var o = Prove(w, input, "accident-out.esm", new EditPlan { DropMaster = "UnusedFull.esm" });
        Assert.False(o.Kept);
        var failed = Assert.IsType<ProofResult>(o.Failed);
        Assert.Equal("diff", failed.Name);
        Assert.Contains("MESG SFM_Message", failed.Reason);
        Assert.Contains("Description", failed.Reason);
        Assert.False(File.Exists(Path.Combine(w.Dir, "accident-out.esm")));
        NoTemp(w, "accident-out.esm");
        Assert.Equal(b, File.ReadAllBytes(input));
    }

    // 11. a forced bad write (one byte of an untouched record flipped) is caught: nothing at --out, no temporary file
    [Fact]
    public void Proof_CatchesABadWrite()
    {
        using var w = new World();
        var full = w.Patch("full", x => { Bytes.SetU32(x, 8, Bytes.U32(x, 8) & ~Flags.Light); return x; });
        var before = File.ReadAllBytes(full);
        EditProof.CorruptForTest = bytes =>
        {
            var at = Enumerable.Range(0, bytes.Length - 12).First(i => bytes.AsSpan(i, 12).SequenceEqual("SFM_Keyword\0"u8));
            bytes[at + 10] = (byte)'e';   // SFM_Keyword -> SFM_Keywore
            return bytes;
        };
        EditOutcome o;
        try { o = Prove(w, full, "bad.esm", new EditPlan { SetLight = true }); }
        finally { EditProof.CorruptForTest = null; }
        Assert.False(o.Kept);
        var failed = Assert.IsType<ProofResult>(o.Failed);
        Assert.Equal("diff", failed.Name);
        Assert.Contains("KYWD", failed.Reason);
        Assert.Contains("no edit touches it", failed.Reason);
        Assert.False(File.Exists(Path.Combine(w.Dir, "bad.esm")));
        NoTemp(w, "bad.esm");
        Assert.Equal(before, File.ReadAllBytes(full));
    }

    // code review round 3: a write that drops the --set-text edit (the target comes out byte-identical) is caught
    [Fact]
    public void Proof_CatchesASetTextThatWasNotWritten()
    {
        using var w = new World();
        var input = w.Build("lines-unwritten", Lines);
        var before = File.ReadAllBytes(input);
        EditProof.CorruptForTest = _ => (byte[])before.Clone();
        EditOutcome o;
        try { o = Prove(w, input, "unwritten.esm", new EditPlan { Texts = { new TextEdit("SFM_Message", 1, "never written") } }); }
        finally { EditProof.CorruptForTest = null; }
        Assert.False(o.Kept);
        var failed = Assert.IsType<ProofResult>(o.Failed);
        Assert.Equal("diff", failed.Name);
        Assert.Contains("doesn't hold the new text as written", failed.Reason);
        Assert.False(File.Exists(Path.Combine(w.Dir, "unwritten.esm")));
        NoTemp(w, "unwritten.esm");
    }

    // code review round 3: the header's ONAM is proved too (renumbered right passes, a wrong entry fails)
    [Fact]
    public void Proof_ChecksTheRenumberedOnam()
    {
        using var w = new World();
        ExtraMaster(w, "UnusedFull.esm", PluginStyle.Full);
        var built = BuildWith(w, "onamproof-plain", PluginStyle.Full, ["UnusedFull.esm", World.Full], mod =>
            mod.MiscItems.AddNew("SFM_Misc").Keywords = [w.FullKw.ToLink<IKeywordGetter>()]);
        var onam = BitConverter.GetBytes(0x01000000u | w.FullKw.ID);   // TestFull's keyword, index 1 -> 0
        var input = Save(w, "onamproof", Bytes.RewriteHeader(File.ReadAllBytes(built), f => f.Add(("ONAM", onam))));
        AllPassed(Prove(w, input, "onamproof-out.esm", new EditPlan { DropMaster = "UnusedFull.esm" }));

        // the old index left in place now names an own record: the proof names the entry
        EditProof.CorruptForTest = bytes => Bytes.RewriteHeader(bytes, f => f[f.FindIndex(x => x.Type == "ONAM")] = ("ONAM", onam));
        EditOutcome o;
        try { o = Prove(w, input, "onambad.esm", new EditPlan { DropMaster = "UnusedFull.esm" }); }
        finally { EditProof.CorruptForTest = null; }
        Assert.False(o.Kept);
        var failed = Assert.IsType<ProofResult>(o.Failed);
        Assert.Equal("diff", failed.Name);
        Assert.Contains("ONAM entry 1 names another record", failed.Reason);
        Assert.False(File.Exists(Path.Combine(w.Dir, "onambad.esm")));
    }

    // code review round 3: check's master count in a message ("(5 full masters)") doesn't make an old ERROR look new
    [Fact]
    public void Proof_MessagesMatchAcrossAMasterCount()
    {
        static uint Same(uint v) => v;
        Assert.Equal(EditProof.Normal("ids: X: master index past the end of the full master list (5 full masters)", "A.esm", Same),
                     EditProof.Normal("ids: X: master index past the end of the full master list (4 full masters)", "A.esm", null));
        // code review round 4: a structure message's file offset moves with the edit
        Assert.Equal(EditProof.Normal("structure: at 0x1A2C: a damaged record", "A.esm", null),
                     EditProof.Normal("structure: at 0x1A36: a damaged record", "A.esm", null));
    }

    // 13. --out refusals and an input inside a zip; nothing is written for any of them
    [Fact]
    public void Refusals_OfTheInputAndOut()
    {
        using var w = new World();
        var full = w.Patch("full", x => { Bytes.SetU32(x, 8, Bytes.U32(x, 8) & ~Flags.Light); return x; });
        var plan = new EditPlan { SetLight = true };
        string Why(string input, string? outPath) =>
            Assert.Throws<EditRefusedException>(() => EditProof.Run(input, outPath, plan, w.Dir, [])).Reason;

        File.WriteAllBytes(Path.Combine(w.Dir, "taken.esm"), [1]);
        Directory.CreateDirectory(Path.Combine(w.Dir, "mod.zip"));
        Assert.Contains("--out is missing", Why(full, null));
        Assert.Contains("is the input", Why(full, full));
        Assert.Contains("is the input", Why(full, Path.Combine(w.Dir, ".", "full.esm")));
        Assert.Contains("already exists", Why(full, Path.Combine(w.Dir, "taken.esm")));
        Assert.Contains("is a folder", Why(full, w.Dir));
        Assert.Contains("wildcard", Why(full, Path.Combine(w.Dir, "*.esm")));
        Assert.Contains("isn't named like a plugin", Why(full, Path.Combine(w.Dir, "out.txt")));
        Assert.Contains("archive or inside one", Why(full, Path.Combine(w.Dir, "mod.zip", "out.esm")));
        Assert.Contains("doesn't exist", Why(full, Path.Combine(w.Dir, "nope", "out.esm")));

        var zip = Path.Combine(w.Dir, "download.zip");
        File.WriteAllBytes(zip, "PK\x03\x04 not really"u8.ToArray());
        var pk = Path.Combine(w.Dir, "renamed.esm");
        File.WriteAllBytes(pk, "PK\x03\x04 not really"u8.ToArray());
        var outPath = Path.Combine(w.Dir, "out.esm");
        Assert.Contains("archive or inside one", Why(zip, outPath));
        Assert.Contains("archive or inside one", Why(Path.Combine(w.Dir, "mod.zip", "full.esm"), outPath));
        Assert.Contains("is an archive", Why(pk, outPath));
        Assert.Contains("is a folder", Why(w.Dir, outPath));
        Assert.Contains("wildcard", Why(Path.Combine(w.Dir, "*.esm"), outPath));
        Assert.Contains("doesn't exist", Why(Path.Combine(w.Dir, "missing.esm"), outPath));
        Assert.False(File.Exists(outPath));
        NoTemp(w, "out.esm");
    }

    // ---- plan step 5: the command (EditCli.cs); design test 14 and the exit codes

    /// <summary>The command in process (so the thread-static CorruptForTest hook reaches it).</summary>
    static (int Code, string Stdout, string Stderr) InProcess(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var code = EditCommand.Run(args, "9.9.9", "byline", o, e);
        return (code, o.ToString(), e.ToString());
    }

    static (int Code, string Stdout, string Stderr) Exe(params string[] args) => ExeTests.RunSplit(new[] { "edit" }.Concat(args).ToArray());

    static string FullCopy(World w) => w.Patch("full", x => { Bytes.SetU32(x, 8, Bytes.U32(x, 8) & ~Flags.Light); return x; });

    // 14. --json for a kept edit and for a failed proof
    [Fact]
    public void Json_ForAKeptEditAndAFailedProof()
    {
        using var w = new World();
        var full = FullCopy(w);
        var kept = Path.Combine(w.Dir, "json-kept.esm");
        var (code, stdout, _) = InProcess(full, "--out", kept, "--set-light", "--data", w.Dir, "--json");
        Assert.True(code == 0, stdout);
        var j = System.Text.Json.Nodes.JsonNode.Parse(stdout)!;
        Assert.Equal("sf-mod-check edit", (string?)j["tool"]);
        Assert.Equal("9.9.9", (string?)j["version"]);
        Assert.Equal(full, (string?)j["input"]);
        Assert.Equal(kept, (string?)j["out"]);
        Assert.True((bool)j["kept"]!);
        Assert.Equal("kept", (string?)j["result"]);
        var edit = Assert.Single(j["edits"]!.AsArray())!;
        Assert.Equal("set-light", (string?)edit["edit"]);
        Assert.Null(edit["record"]);
        Assert.Equal("type: full -> light", (string?)edit["changed"]);
        foreach (var p in new[] { "diff", "gap", "check" })
        {
            Assert.True((bool)j["proof"]![p]!["passed"]!, p);
            Assert.False(string.IsNullOrEmpty((string?)j["proof"]![p]!["reason"]), p);
        }
        Assert.Null(j["error"]);
        Assert.Equal(w.CleanBytes, File.ReadAllBytes(kept));

        var bad = Path.Combine(w.Dir, "json-bad.esm");
        EditProof.CorruptForTest = bytes =>
        {
            var at = Enumerable.Range(0, bytes.Length - 12).First(i => bytes.AsSpan(i, 12).SequenceEqual("SFM_Keyword\0"u8));
            bytes[at + 10] = (byte)'e';
            return bytes;
        };
        try { (code, stdout, _) = InProcess(full, "--out", bad, "--set-light", "--data", w.Dir, "--json"); }
        finally { EditProof.CorruptForTest = null; }
        Assert.True(code == 1, stdout);
        j = System.Text.Json.Nodes.JsonNode.Parse(stdout)!;
        Assert.False((bool)j["kept"]!);
        Assert.Equal("not kept", (string?)j["result"]);
        Assert.False((bool)j["proof"]!["diff"]!["passed"]!);
        Assert.Contains("KYWD", (string?)j["proof"]!["diff"]!["reason"]);
        Assert.StartsWith("the diff proof failed: ", (string?)j["error"]);
        Assert.False(File.Exists(bad));
        NoTemp(w, "json-bad.esm");
    }

    // exit 1 in text: the failed proof, "not kept" and the RESULT line
    [Fact]
    public void Command_Exit1_WhenAProofFails()
    {
        using var w = new World();
        var full = FullCopy(w);
        var bad = Path.Combine(w.Dir, "text-bad.esm");
        EditProof.CorruptForTest = bytes =>
        {
            var at = Enumerable.Range(0, bytes.Length - 12).First(i => bytes.AsSpan(i, 12).SequenceEqual("SFM_Keyword\0"u8));
            bytes[at + 10] = (byte)'e';
            return bytes;
        };
        (int Code, string Stdout, string Stderr) r;
        try { r = InProcess(full, "--out", bad, "--set-light", "--data", w.Dir); }
        finally { EditProof.CorruptForTest = null; }
        Assert.True(r.Code == 1, r.Stdout + r.Stderr);
        Assert.Contains("proof diff: FAILED: ", r.Stdout);
        Assert.Contains("not kept: the diff proof failed", r.Stdout);
        Assert.StartsWith("RESULT: NOT KEPT (the diff proof failed", ExeTests.ResultLine(r.Stdout));
        Assert.False(File.Exists(bad));
    }

    // exit 0 through the built program: each change, each proof, "kept" and the RESULT line
    [Fact]
    public void Command_Exit0_KeepsTheProvedFile()
    {
        using var w = new World();
        var light = File.ReadAllBytes(w.Build("lines", Lines));
        var full = Save(w, "cmd", light.Also(b => Bytes.SetU32(b, 8, Bytes.U32(b, 8) & ~Flags.Light)));
        var outPath = Path.Combine(w.Dir, "cmd-out.esm");
        var log = Path.Combine(w.Dir, "edit-log.txt");
        var (code, stdout, stderr) = Exe(full, "--out", outPath, "--set-light", "--set-text", "SFM_TwoLines", "--response", "2", "--text", "a new second line",
            "--set-text", "SFM_Message", "--text", "a new message", "--data", w.Dir, "--log", log);
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("edit set-light: type: full -> light", stdout);
        Assert.Contains("response 2 text (NAM1)", stdout);
        Assert.Contains("description (DESC)", stdout);
        foreach (var p in new[] { "diff", "gap", "check" }) Assert.Contains($"proof {p}: passed: ", stdout);
        Assert.Contains("kept " + outPath, stdout);
        Assert.StartsWith("RESULT: KEPT (3 proofs passed; cmd-out.esm;", ExeTests.ResultLine(stdout));
        Assert.True(File.Exists(outPath));
        var logText = File.ReadAllText(log);
        Assert.Contains("edit: proof diff: passed", logText);
        Assert.Contains("edit: kept", logText);
        Assert.Contains("sf-mod-check", stderr);   // the byline (not with --json or --quiet)
    }

    // exit 2: a refusal (with its reason and fix), bad arguments and a bad --log; nothing is written
    [Fact]
    public void Command_Exit2_RefusalsAndBadArguments()
    {
        using var w = new World();
        var full = FullCopy(w);
        var before = File.ReadAllBytes(full);
        var outPath = Path.Combine(w.Dir, "never.esm");

        var r = Exe(full, "--out", full, "--set-light", "--data", w.Dir);
        Assert.Equal(2, r.Code);
        Assert.StartsWith("RESULT: REFUSED (--out ", ExeTests.ResultLine(r.Stdout));
        Assert.Contains("edit refused: --out", r.Stderr);
        Assert.Contains("is the input", r.Stderr);
        Assert.Contains("Give a new file name", r.Stderr);

        r = Exe(full, "--out", outPath, "--data", w.Dir, "--json");
        Assert.Equal(2, r.Code);
        var j = System.Text.Json.Nodes.JsonNode.Parse(r.Stdout)!;
        Assert.Equal("refused", (string?)j["result"]);
        Assert.Equal("No edit was given.", (string?)j["error"]);
        Assert.Contains("--set-light", (string?)j["fix"]);
        Assert.False((bool)j["kept"]!);

        foreach (var (args, why) in new (string[], string)[]
                 {
                     ([full, "--out", outPath, "--text", "x"], "--text belongs to a --set-text"),
                     ([full, "--out", outPath, "--set-text", "SFM_Message"], "has no --text after it"),
                     ([full, "--out", outPath, "--set-text", "SFM_Message", "--response", "two", "--text", "x"], "--response needs a whole number"),
                     ([full, "--out", outPath, "--drop-master", "A.esm", "--drop-master", "B.esm"], "give --drop-master once"),
                     ([full, full, "--out", outPath, "--set-light"], "one plugin per run"),
                     ([full, "--out", outPath, "--set-light", "--nope"], "unknown option --nope"),
                     ([full, "--out", outPath, "--set-light", "--log", full], "plugin, archive or script"),
                     ([full, "--out", outPath, "--set-light", "--log", w.Dir], "that is a folder"),
                     ([full, "--out", outPath, "--set-light", "--data", Path.Combine(w.Dir, "no-such-data")], "doesn't exist"),
                 })
        {
            r = Exe(args);
            Assert.True(r.Code == 2, string.Join(" ", args) + "\n" + r.Stdout + r.Stderr);
            Assert.Contains(why, r.Stderr);
            Assert.StartsWith("RESULT: ", ExeTests.ResultLine(r.Stdout));
        }
        Assert.False(File.Exists(outPath));
        NoTemp(w, "never.esm");
        Assert.Equal(before, File.ReadAllBytes(full));
    }

    // --log is checked against the input and --out by path, before the log is opened
    [Fact]
    public void LogProblem_RefusesTheInputAndOut()
    {
        using var w = new World();
        var full = FullCopy(w);
        var txtInput = Path.Combine(w.Dir, "notes.txt");
        Assert.Contains("that is the input", EditCommand.LogProblem(txtInput, txtInput, Path.Combine(w.Dir, "o.esm")));
        Assert.Contains("that is --out", EditCommand.LogProblem(Path.Combine(w.Dir, "o.txt"), full, Path.Combine(w.Dir, ".", "o.txt")));
        Assert.Contains("plugin, archive or script", EditCommand.LogProblem(full, full, null));
        Assert.Null(EditCommand.LogProblem(Path.Combine(w.Dir, "edit-log.txt"), full, Path.Combine(w.Dir, "o.esm")));
    }

    // --help: stdout, exit 0, no RESULT line; the main help names edit
    [Fact]
    public void Help_ForEditAndInTheMainHelp()
    {
        var r = Exe("--help");
        Assert.Equal(0, r.Code);
        Assert.StartsWith("usage: sf-mod-check edit <plugin> --out", r.Stdout);
        Assert.DoesNotContain("RESULT:", r.Stdout);
        var main = ExeTests.RunSplit("--help");
        Assert.Equal(0, main.Code);
        Assert.Contains("sf-mod-check edit <plugin> --out <new plugin>", main.Stdout);
    }

    // the parser: --response before or after --text belongs to its --set-text; several --set-text
    [Fact]
    public void Parse_SetTextResponseAndText()
    {
        var o = EditCommand.Parse(["a.esm", "--out", "b.esm", "--set-text", "X", "--response", "2", "--text", "one",
                                   "--set-text", "Y", "--text", "two", "--response", "3", "--set-text", "Z", "--text", "three"], out var err, out _);
        Assert.True(o != null, err);
        Assert.Equal([new TextEdit("X", 2, "one"), new TextEdit("Y", 3, "two"), new TextEdit("Z", 1, "three")], o!.Plan.Texts);
        Assert.Null(EditCommand.Parse(["a.esm", "--set-text", "X", "--response", "2", "--response", "3", "--text", "t"], out err, out _));
        Assert.Contains("once for each --set-text", err);
        // a second --response after the --text gets the same message (code review 0.25.0 round 1)
        Assert.Null(EditCommand.Parse(["a.esm", "--set-text", "X", "--response", "2", "--text", "t", "--response", "3"], out err, out _));
        Assert.Contains("once for each --set-text", err);
        Assert.Null(EditCommand.Parse(["a.esm", "--out", "b.esm", "--response", "2"], out err, out _));
        Assert.Contains("--response belongs to a --set-text", err);
        // --text takes the next argument as it is: leading dashes, or empty
        o = EditCommand.Parse(["a.esm", "--out", "b.esm", "--set-text", "X", "--text", "--Static--", "--set-text", "Y", "--text", ""], out err, out _);
        Assert.True(o != null, err);
        Assert.Equal([new TextEdit("X", 1, "--Static--"), new TextEdit("Y", 1, "")], o!.Plan.Texts);
        Assert.Null(EditCommand.Parse(["a.esm", "--out", "b.esm", "--set-text", "X", "--text"], out err, out _));
        Assert.Contains("needs the new text", err);
        // but one of edit's own options right after --text is a missing text, not the text (code review round 2)
        foreach (var opt in new[] { "--set-light", "--JSON", "--out" })
        {
            Assert.Null(EditCommand.Parse(["a.esm", "--out", "b.esm", "--set-text", "X", "--text", opt], out err, out _));
            Assert.Contains($"the next argument is the option {opt}", err);
        }
        Assert.Null(EditCommand.Parse(["--out", "b.esm", "--set-light"], out err, out _));
        Assert.Contains("give the plugin to edit", err);
    }
}

static class AlsoExt
{
    public static byte[] Also(this byte[] b, Action<byte[]> f) { f(b); return b; }
}
