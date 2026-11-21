// 0.5.4 items 2, 5 and 6 (from the 0.5.3 reviews): plugins whose records could not all be read, the record signatures
// the drop rule relies on, and the lazy read behind the record-type pass's unread plugins.
using System.Reflection;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SfModCheck.Conflict;
using Xunit;

namespace SfModCheck.Tests;

public class ConflictUnreadTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>A made-up game: Base.esm (full) with three keywords; mods are added with Mod(). (A trimmed copy of
    /// ConflictTests' Game, which is private there.)</summary>
    sealed class Game : Sandbox
    {
        public readonly string Data;
        public Game()
        {
            Data = Sub("Data");
            var b = Plugin.New("Base.esm", PluginStyle.Full, "test", "made-up base game");
            for (int i = 0; i < 3; i++) b.Keywords.AddNew($"B_K{i:000}");
            using var g = new GameData(Data);
            PluginWriter.Write(b, Data, g, NoGap);
        }

        public void Mod(string name, Action<StarfieldMod, Func<string, IStarfieldModGetter>> build)
        {
            var mod = Plugin.New(name, PluginStyle.Light, "test", "made-up mod");
            using var g = new GameData(Data);
            build(mod, n => g.Open(n));
            PluginWriter.Write(mod, Data, g, NoGap);
        }

        public string PluginsTxt(string[] active)
        {
            var txt = Path.Combine(Dir, "Plugins.txt");
            File.WriteAllLines(txt, active.Select(a => "*" + a));
            return txt;
        }
    }

    static List<string> Errors(System.Text.Json.JsonElement json) =>
        json.GetProperty("Errors").EnumerateArray().Select(e => e.GetString() ?? "").ToList();

    /// <summary>Makes the first keyword record of a plugin unreadable (a huge size), so Mutagen stops reading its
    /// keywords there; records after it are never read.</summary>
    static void DamageFirstKeyword(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var keywords = Bytes.Items(bytes).Where(i => i.Sig == "KYWD").ToList();
        Bytes.SetU32(bytes, keywords[0].Off + 4, 0x7FFF_FFF0);
        File.WriteAllBytes(path, bytes);
    }

    // Item 2: ModZ's keywords can't all be read, and its unread version of B_K001 sits between ModX's and ModY's. ModY
    // doesn't have ModZ as a master, so ModZ's own change is lost to ModY, but nobody can see it. Until 0.5.4 that went
    // unsaid. Now ModX's loss to ModY, which stands whatever ModZ holds, is still reported, and ModZ is named as compared
    // without (review of the first 0.5.4 fix: skipping the record hid ModX's real loss).
    [Fact]
    public void A_partly_read_plugin_in_the_middle_is_named_and_the_record_still_compared()
    {
        using var game = new Game();
        game.Mod("ModX.esm", (m, open) => m.Keywords.GetOrAddAsOverride(open("Base.esm").Keywords.Skip(1).First()).EditorID = "X_K001");
        game.Mod("ModZ.esm", (m, open) =>
        {
            var b = open("Base.esm");
            m.Keywords.GetOrAddAsOverride(b.Keywords.First());           // damaged below
            m.Keywords.GetOrAddAsOverride(b.Keywords.Skip(1).First()).EditorID = "Z_K001";   // unread: after the damage
        });
        game.Mod("ModY.esm", (m, open) => m.Keywords.GetOrAddAsOverride(open("Base.esm").Keywords.Skip(1).First()).EditorID = "Y_K001");
        DamageFirstKeyword(Path.Combine(game.Data, "ModZ.esm"));

        var txt = game.PluginsTxt(new[] { "Base.esm", "ModX.esm", "ModZ.esm", "ModY.esm" });
        var outDir = Path.Combine(game.Dir, "report");
        // the test change is an Editor ID, which field priorities (0.10.0) show apart as benign: test the unread logic without them
        var (code, stdout, _) = ExeTests.RunSplit("conflict", "--data", game.Data, "--plugins", txt, "--out", outDir, "--quiet", "--no-priorities");
        Assert.True(code == 0, stdout);
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "conflicts.json"))).RootElement;
        var lostKw = Assert.Single(json.GetProperty("lost").EnumerateArray(), l => l.GetProperty("RecordType").GetString() == "Keyword");
        Assert.Equal(("ModX.esm", "ModY.esm"), (lostKw.GetProperty("Loser").GetString(), lostKw.GetProperty("Winner").GetString()));
        var errors = Errors(json);
        Assert.Contains(errors, e => e.StartsWith("ModZ.esm: could not read its keyword records"));
        Assert.Contains(errors, e => e.StartsWith("ModZ.esm: 1 other keyword record was compared without its version: ModZ.esm may change it too"));
        Assert.DoesNotContain(errors, e => e.Contains("was not compared"));
    }

    // Review round 2: a partial form after the unread version supplies only some fields, so ModZ's unread version may
    // still win the others: the record is skipped and counted, never compared without ModZ.
    [Fact]
    public void A_later_partial_form_makes_the_record_not_compared()
    {
        using var game = new Game();
        game.Mod("ModX.esm", (m, open) => m.Keywords.GetOrAddAsOverride(open("Base.esm").Keywords.Skip(1).First()).EditorID = "X_K001");
        game.Mod("ModZ.esm", (m, open) =>
        {
            var b = open("Base.esm");
            m.Keywords.GetOrAddAsOverride(b.Keywords.First());
            m.Keywords.GetOrAddAsOverride(b.Keywords.Skip(1).First()).EditorID = "Z_K001";
        });
        game.Mod("ModY.esm", (m, open) =>
        {
            var k = m.Keywords.GetOrAddAsOverride(open("Base.esm").Keywords.Skip(1).First());
            k.MajorRecordFlagsRaw |= FieldCompare.PartialFormFlag;
        });
        DamageFirstKeyword(Path.Combine(game.Data, "ModZ.esm"));

        var txt = game.PluginsTxt(new[] { "Base.esm", "ModX.esm", "ModZ.esm", "ModY.esm" });
        var outDir = Path.Combine(game.Dir, "report");
        var (code, stdout, _) = ExeTests.RunSplit("conflict", "--data", game.Data, "--plugins", txt, "--out", outDir, "--quiet");
        Assert.True(code == 0, stdout);
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "conflicts.json"))).RootElement;
        Assert.DoesNotContain(json.GetProperty("lost").EnumerateArray(), l => l.GetProperty("RecordType").GetString() == "Keyword");
        var errors = Errors(json);
        Assert.Contains(errors, e => e.StartsWith("ModZ.esm: 1 other keyword record was not compared, because ModZ.esm may change it too"));
        Assert.DoesNotContain(errors, e => e.Contains("compared without its version"));
    }

    // Review round 3: a full version after the partial form beats ModZ on every field, so the record is compared again
    // (ModX's loss to ModW stands) and ModZ is named as compared without.
    [Fact]
    public void A_full_version_after_the_partial_form_lets_the_record_be_compared()
    {
        using var game = new Game();
        game.Mod("ModX.esm", (m, open) => m.Keywords.GetOrAddAsOverride(open("Base.esm").Keywords.Skip(1).First()).EditorID = "X_K001");
        game.Mod("ModZ.esm", (m, open) =>
        {
            var b = open("Base.esm");
            m.Keywords.GetOrAddAsOverride(b.Keywords.First());
            m.Keywords.GetOrAddAsOverride(b.Keywords.Skip(1).First()).EditorID = "Z_K001";
        });
        game.Mod("ModP.esm", (m, open) => m.Keywords.GetOrAddAsOverride(open("Base.esm").Keywords.Skip(1).First()).MajorRecordFlagsRaw |= FieldCompare.PartialFormFlag);
        game.Mod("ModW.esm", (m, open) => m.Keywords.GetOrAddAsOverride(open("Base.esm").Keywords.Skip(1).First()).EditorID = "W_K001");
        DamageFirstKeyword(Path.Combine(game.Data, "ModZ.esm"));

        var txt = game.PluginsTxt(new[] { "Base.esm", "ModX.esm", "ModZ.esm", "ModP.esm", "ModW.esm" });
        var outDir = Path.Combine(game.Dir, "report");
        // Editor ID changes again: without field priorities, as above
        var (code, stdout, _) = ExeTests.RunSplit("conflict", "--data", game.Data, "--plugins", txt, "--out", outDir, "--quiet", "--no-priorities");
        Assert.True(code == 0, stdout);
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "conflicts.json"))).RootElement;
        Assert.Contains(json.GetProperty("lost").EnumerateArray(), l => l.GetProperty("RecordType").GetString() == "Keyword"
            && l.GetProperty("Loser").GetString() == "ModX.esm" && l.GetProperty("Winner").GetString() == "ModW.esm");
        var errors = Errors(json);
        Assert.Contains(errors, e => e.StartsWith("ModZ.esm: 1 other keyword record was compared without its version"));
        Assert.DoesNotContain(errors, e => e.Contains("was not compared"));
    }

    // Review round 4: a later full version whose field can't be read may not beat ModZ on that field, so the record is
    // not compared after all.
    [Fact]
    public void A_later_full_version_with_an_unreadable_field_makes_the_record_not_compared()
    {
        using var game = new Game();
        game.Mod("ModX.esm", (m, open) => m.Keywords.GetOrAddAsOverride(open("Base.esm").Keywords.Skip(1).First()).EditorID = "X_K001");
        game.Mod("ModZ.esm", (m, open) =>
        {
            var b = open("Base.esm");
            m.Keywords.GetOrAddAsOverride(b.Keywords.First());
            m.Keywords.GetOrAddAsOverride(b.Keywords.Skip(1).First()).EditorID = "Z_K001";
        });
        game.Mod("ModW.esm", (m, open) =>
        {
            var k = m.Keywords.GetOrAddAsOverride(open("Base.esm").Keywords.Skip(1).First());
            k.EditorID = "W_K001";
            k.Color = System.Drawing.Color.FromArgb(255, 1, 2, 3);
        });
        DamageFirstKeyword(Path.Combine(game.Data, "ModZ.esm"));
        // ModW's color subrecord says 2 bytes instead of 4: the record still reads, its Color field doesn't
        var path = Path.Combine(game.Data, "ModW.esm");
        var bytes = File.ReadAllBytes(path);
        var kw = Bytes.Items(bytes).Single(i => i.Sig == "KYWD");
        int at = -1;
        for (int o = (int)kw.Off + 24; o < bytes.Length - 6; o++)
            if (bytes[o] == 'C' && bytes[o + 1] == 'N' && bytes[o + 2] == 'A' && bytes[o + 3] == 'M' && Bytes.U16(bytes, o + 4) == 4) { at = o; break; }
        Assert.True(at > 0, "no CNAM in ModW's keyword");
        Bytes.SetU16(bytes, at + 4, 2);
        File.WriteAllBytes(path, bytes);

        var txt = game.PluginsTxt(new[] { "Base.esm", "ModX.esm", "ModZ.esm", "ModW.esm" });
        var outDir = Path.Combine(game.Dir, "report");
        var (code, stdout, _) = ExeTests.RunSplit("conflict", "--data", game.Data, "--plugins", txt, "--out", outDir, "--quiet");
        Assert.True(code == 0, stdout);
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "conflicts.json"))).RootElement;
        var errors = Errors(json);
        Assert.True(errors.Any(e => e.StartsWith("ModZ.esm: 1 other keyword record was not compared, because ModZ.esm may change it too")),
            string.Join(" || ", errors));
        Assert.DoesNotContain(errors, e => e.Contains("compared without its version"));
        Assert.DoesNotContain(json.GetProperty("lost").EnumerateArray(), l => l.GetProperty("RecordType").GetString() == "Keyword");
    }

    // Review round 4: ModY has ModZ as a master, so ModZ's unread version is the base ModY built on: the record can't be
    // compared without it, though a full version (ModY's) loads after it.
    [Fact]
    public void A_later_version_built_on_the_partly_read_plugin_makes_the_record_not_compared()
    {
        using var game = new Game();
        game.Mod("ModX.esm", (m, open) => m.Keywords.GetOrAddAsOverride(open("Base.esm").Keywords.Skip(1).First()).EditorID = "X_K001");
        game.Mod("ModZ.esm", (m, open) =>
        {
            var b = open("Base.esm");
            m.Keywords.GetOrAddAsOverride(b.Keywords.First());
            m.Keywords.GetOrAddAsOverride(b.Keywords.Skip(1).First()).EditorID = "Z_K001";
            m.Keywords.AddNew("Z_Own");
        });
        game.Mod("ModY.esm", (m, open) =>
        {
            m.Keywords.GetOrAddAsOverride(open("ModZ.esm").Keywords.Single(k => k.EditorID == "Z_Own"));   // makes ModZ a master
            m.Keywords.GetOrAddAsOverride(open("Base.esm").Keywords.Skip(1).First()).EditorID = "Y_K001";
        });
        DamageFirstKeyword(Path.Combine(game.Data, "ModZ.esm"));

        var txt = game.PluginsTxt(new[] { "Base.esm", "ModX.esm", "ModZ.esm", "ModY.esm" });
        var outDir = Path.Combine(game.Dir, "report");
        var (code, stdout, _) = ExeTests.RunSplit("conflict", "--data", game.Data, "--plugins", txt, "--out", outDir, "--quiet");
        Assert.True(code == 0, stdout);
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "conflicts.json"))).RootElement;
        Assert.DoesNotContain(json.GetProperty("lost").EnumerateArray(), l => l.GetProperty("RecordType").GetString() == "Keyword");
        var errors = Errors(json);
        Assert.True(errors.Any(e => e.StartsWith("ModZ.esm: ") && e.Contains("keyword record") && e.Contains("not compared, because ModZ.esm may change")), string.Join(" || ", errors));
        Assert.DoesNotContain(errors, e => e.Contains("compared without its version"));
    }

    // Review round 3: every partly-read plugin that stops a comparison is named, not only the first.
    [Fact]
    public void Two_partly_read_plugins_after_the_last_version_are_both_counted()
    {
        using var game = new Game();
        game.Mod("ModX.esm", (m, open) => m.Keywords.GetOrAddAsOverride(open("Base.esm").Keywords.Skip(1).First()).EditorID = "X_K001");
        foreach (var z in new[] { "ModZ1.esm", "ModZ2.esm" })
            game.Mod(z, (m, open) =>
            {
                var b = open("Base.esm");
                m.Keywords.GetOrAddAsOverride(b.Keywords.First());
                m.Keywords.GetOrAddAsOverride(b.Keywords.Skip(1).First()).EditorID = z[..^4] + "_K001";
            });
        DamageFirstKeyword(Path.Combine(game.Data, "ModZ1.esm"));
        DamageFirstKeyword(Path.Combine(game.Data, "ModZ2.esm"));

        var txt = game.PluginsTxt(new[] { "Base.esm", "ModX.esm", "ModZ1.esm", "ModZ2.esm" });
        var outDir = Path.Combine(game.Dir, "report");
        var (code, stdout, _) = ExeTests.RunSplit("conflict", "--data", game.Data, "--plugins", txt, "--out", outDir, "--quiet");
        Assert.True(code == 0, stdout);
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "conflicts.json"))).RootElement;
        var errors = Errors(json);
        Assert.Contains(errors, e => e.StartsWith("ModZ1.esm: 1 other keyword record was not compared"));
        Assert.Contains(errors, e => e.StartsWith("ModZ2.esm: 1 other keyword record was not compared"));
    }

    // Review round 2: every partly-read plugin in the middle is named, not only the first.
    [Fact]
    public void Two_partly_read_plugins_in_the_middle_are_both_named()
    {
        using var game = new Game();
        game.Mod("ModX.esm", (m, open) => m.Keywords.GetOrAddAsOverride(open("Base.esm").Keywords.Skip(1).First()).EditorID = "X_K001");
        foreach (var z in new[] { "ModZ1.esm", "ModZ2.esm" })
            game.Mod(z, (m, open) =>
            {
                var b = open("Base.esm");
                m.Keywords.GetOrAddAsOverride(b.Keywords.First());
                m.Keywords.GetOrAddAsOverride(b.Keywords.Skip(1).First()).EditorID = z[..^4] + "_K001";
            });
        game.Mod("ModY.esm", (m, open) => m.Keywords.GetOrAddAsOverride(open("Base.esm").Keywords.Skip(1).First()).EditorID = "Y_K001");
        DamageFirstKeyword(Path.Combine(game.Data, "ModZ1.esm"));
        DamageFirstKeyword(Path.Combine(game.Data, "ModZ2.esm"));

        var txt = game.PluginsTxt(new[] { "Base.esm", "ModX.esm", "ModZ1.esm", "ModZ2.esm", "ModY.esm" });
        var outDir = Path.Combine(game.Dir, "report");
        var (code, stdout, _) = ExeTests.RunSplit("conflict", "--data", game.Data, "--plugins", txt, "--out", outDir, "--quiet");
        Assert.True(code == 0, stdout);
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "conflicts.json"))).RootElement;
        var errors = Errors(json);
        Assert.Contains(errors, e => e.StartsWith("ModZ1.esm: 1 other keyword record was compared without its version"));
        Assert.Contains(errors, e => e.StartsWith("ModZ2.esm: 1 other keyword record was compared without its version"));
    }

    // Item 5: the drop rule needs the record signature of every record type the comparison passes read. They come from
    // Mutagen (rec.Registration.GetterType), so every concrete Starfield record getter type must have one.
    [Fact]
    public void Every_Starfield_record_type_has_a_known_signature()
    {
        var sigsOf = typeof(Checker).GetMethod("SigsOf", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        var types = typeof(IStarfieldModGetter).Assembly.GetTypes();
        var classes = types.Where(t => t.IsClass && !t.IsAbstract && typeof(IMajorRecord).IsAssignableFrom(t)).ToList();
        // the getter interface of each record class, as the analyzer's AllConcrete finds them
        var getters = types.Where(t => t.IsInterface && t.Name.StartsWith('I') && t.Name.EndsWith("Getter")
                && typeof(IMajorRecordGetter).IsAssignableFrom(t)
                && classes.Any(c => c.FullName == $"{t.Namespace}.{t.Name[1..^"Getter".Length]}"))
            .ToHashSet();
        // and each record class's own registration, which is what the passes are grouped by
        foreach (var c in classes)
            if (c.GetProperty("StaticRegistration", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) is Loqui.ILoquiRegistration reg)
                getters.Add(reg.GetterType);
        Assert.True(getters.Count > 100, $"only {getters.Count} record types found");
        Assert.Contains(typeof(IKeywordGetter), getters);
        var missing = getters.Where(t => sigsOf.Invoke(null, new object[] { t }) is not HashSet<string> { Count: > 0 })
            .Select(t => t.FullName).OrderBy(n => n).ToList();
        Assert.True(missing.Count == 0, "no signature: " + string.Join(", ", missing));
    }

    // Item 6: when the record-type pass can't read a plugin (ModP: its file is gone from where the raw reader looks)
    // but Mutagen can, ModP's overrides are read through Mutagen once. That read is named in "where it stopped" and is
    // held to --max-memory like the other reads.
    [Fact]
    public void The_lazy_read_of_a_type_unread_plugin_is_named_and_held_to_the_memory_limit()
    {
        using var game = new Game();
        FormKey injected = default;
        game.Mod("ModP.esm", (m, open) =>
        {
            var b = open("Base.esm");
            injected = new FormKey(b.ModKey, 0xF00);   // no record in Base.esm has this ID
            m.Keywords.GetOrAddAsOverride(b.Keywords.First()).EditorID = "P_K000";
        });
        // two injected records: the first one makes the lazy read, the second shows the label that follows it
        void Inject(StarfieldMod m, Func<string, IStarfieldModGetter> open, string id)
        {
            open("Base.esm");
            m.Keywords.Add(new Keyword(injected, StarfieldRelease.Starfield) { EditorID = id });
            m.Keywords.Add(new Keyword(new FormKey(injected.ModKey, 0xF01), StarfieldRelease.Starfield) { EditorID = id + "2" });
        }
        game.Mod("ModA.esm", (m, open) => Inject(m, open, "A_Kw"));
        game.Mod("ModB.esm", (m, open) => Inject(m, open, "B_Kw"));
        var txt = game.PluginsTxt(new[] { "Base.esm", "ModP.esm", "ModA.esm", "ModB.esm" });

        var lo = LoadOrderSetup.Build(game.Data, txt);
        var (maxBefore, bytesBefore) = (Analyzer.MaxManagedBytes, Analyzer.ManagedBytes);
        try
        {
            var i = lo.Plugins.FindIndex(p => p.FileName == "ModP.esm");
            lo.Plugins[i] = lo.Plugins[i] with { Path = Path.Combine(game.Dir, "missing", "ModP.esm") };   // the raw pass can't open it
            var seen = new List<string>();
            Analyzer.MaxManagedBytes = 1000;
            // over the limit only while the lazy read is the run's named step
            Analyzer.ManagedBytes = () => { seen.Add(Analyzer.Where); return Analyzer.Where.Contains("ModP.esm to see which records") ? 2000 : 0; };
            var e = Assert.Throws<MemoryLimitException>(() => Analyzer.Run(lo.Plugins));
            Assert.Equal("memory passed 0.0 GB while reading ModP.esm to see which records it overrides", e.Message);
            Assert.Equal("reading ModP.esm to see which records it overrides", Analyzer.Where);   // a stop leaves it named

            // without the limit the run goes on, and the comparison's own label comes back after the read
            seen.Clear();
            Analyzer.ManagedBytes = () => { seen.Add(Analyzer.Where); return 0; };
            var result = Analyzer.Run(lo.Plugins);
            Assert.Contains(result.Errors, x => x.StartsWith("ModP.esm: its records' types could not be read"));
            Assert.Equal("comparing Keyword records", seen[seen.IndexOf("reading ModP.esm to see which records it overrides") + 1]);
        }
        finally
        {
            (Analyzer.MaxManagedBytes, Analyzer.ManagedBytes) = (maxBefore, bytesBefore);
            foreach (var p in lo.Plugins) p.Mod.Dispose();
        }
    }

    // code review 2026-11-18: the assets check's lookups of the records overrides replace build link caches of the
    // masters (Starfield.esm's can be large), so they obey --max-memory: past it they stop, every missing path is
    // reported (the safe side), and the errors say so; the run itself goes on
    [Fact]
    public void The_assets_checks_lookups_stop_at_the_memory_limit_and_report_every_missing_path()
    {
        using var game = new Game();
        game.Mod("ModS.esm", (m, _) => m.MiscItems.AddNew("S_Misc").VirtualMachineAdapter =
            new VirtualMachineAdapter { Scripts = { new ScriptEntry { Name = "SfmTest:Gone" } } });
        game.Mod("ModT.esm", (m, open) => m.MiscItems.GetOrAddAsOverride(open("ModS.esm").MiscItems.First()).Value = 7);
        var txt = game.PluginsTxt(new[] { "Base.esm", "ModS.esm", "ModT.esm" });
        var lo = LoadOrderSetup.Build(game.Data, txt);
        var (maxBefore, bytesBefore) = (Analyzer.MaxManagedBytes, Analyzer.ManagedBytes);
        try
        {
            Analysis Run()
            {
                var a = new Analysis();
                Assets.CheckLoadOrder(lo.Plugins, new[] { game.Data }, Array.Empty<string>(), "en", a);
                return a;
            }
            Analyzer.ManagedBytes = () => 0;
            var under = Run();
            Assert.Equal(new[] { "ModS.esm" }, under.MissingAssets.Select(f => f.Plugin));
            Assert.Equal(1, under.AssetsInherited);
            Assert.Empty(under.Errors);

            Analyzer.MaxManagedBytes = 1000;
            Analyzer.ManagedBytes = () => 2000;
            var over = Run();
            Assert.Equal(new[] { "ModS.esm", "ModT.esm" }, over.MissingAssets.Select(f => f.Plugin));
            Assert.Equal(0, over.AssetsInherited);
            Assert.StartsWith("the assets check passed --max-memory (0.0 GB) while looking up the records overrides replace (at ModT.esm)", Assert.Single(over.Errors));
        }
        finally
        {
            (Analyzer.MaxManagedBytes, Analyzer.ManagedBytes) = (maxBefore, bytesBefore);
            foreach (var p in lo.Plugins) p.Mod.Dispose();
        }
    }

    // 0.10.0 code review round 2: Base.esm's keywords are read only partly, so its own version of B_K001 is unread and
    // the first version seen is ModX's. The record is skipped and counted (ModX is built on Base.esm), so no lost
    // change reaches the field priorities with a wrong "not injected" answer. (Should one ever get there, the analyzer
    // leaves Injected null and FieldPriorities takes it as injected: FieldPrioritiesTests.)
    [Fact]
    public void A_record_whose_owner_was_read_only_partly_is_counted_not_judged_by_priorities()
    {
        using var game = new Game();
        game.Mod("ModX.esm", (m, open) => m.Keywords.GetOrAddAsOverride(open("Base.esm").Keywords.Skip(1).First()).Notes = "X notes");
        game.Mod("ModY.esm", (m, open) => m.Keywords.GetOrAddAsOverride(open("Base.esm").Keywords.Skip(1).First()).Notes = "Y notes");
        DamageFirstKeyword(Path.Combine(game.Data, "Base.esm"));

        var txt = game.PluginsTxt(new[] { "Base.esm", "ModX.esm", "ModY.esm" });
        var outDir = Path.Combine(game.Dir, "report");
        var log = Path.Combine(game.Dir, "debug.log");
        var (code, stdout, _) = ExeTests.RunSplit("conflict", "--data", game.Data, "--plugins", txt, "--out", outDir, "--quiet", "--log", log);
        Assert.True(code == 0, stdout);
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "conflicts.json"))).RootElement;
        Assert.DoesNotContain(json.GetProperty("lost").EnumerateArray(), l => l.GetProperty("RecordType").GetString() == "Keyword");
        Assert.DoesNotContain(json.GetProperty("benign").EnumerateArray(), l => l.GetProperty("RecordType").GetString() == "Keyword");
        Assert.Contains(Errors(json), e => e.StartsWith("Base.esm: ") && e.Contains("was not compared"));
    }
}
