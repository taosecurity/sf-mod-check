// patch-sources.json (0.15.0): the manifest `patch` writes beside the patch, and the record hash it holds for each
// editor's copy of a patched record. Made-up plugins only.
using System.Security.Cryptography;
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Xunit;

namespace SfModCheck.Tests;

public class PatchSourcesTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    sealed class Game : Sandbox
    {
        public readonly string Data;
        public Game()
        {
            Data = Sub("Data");
            var b = Plugin.New("Base.esm", PluginStyle.Full, "test", "made-up base game");
            var misc = b.MiscItems.AddNew("B_Misc");
            misc.Value = 5;
            misc.Weight = 1;
            using var g = new GameData(Data);
            PluginWriter.Write(b, Data, g, NoGap);
        }

        public void Mod(string name, Action<MiscItem> change)
        {
            var mod = Plugin.New(name, PluginStyle.Light, "test", "made-up mod");
            using var g = new GameData(Data);
            change(mod.MiscItems.GetOrAddAsOverride(g.Open("Base.esm").MiscItems.First(m => m.EditorID == "B_Misc")));
            PluginWriter.Write(mod, Data, g, NoGap);
        }

        public string Out => Path.Combine(Dir, "patch");
        public string Manifest => Path.Combine(Out, "patch-sources.json");

        public (int Code, string Stdout, string Stderr) Patch(params string[] active)
        {
            var txt = Path.Combine(Dir, "Plugins.txt");
            File.WriteAllLines(txt, active.Select(a => "*" + a));
            return ExeTests.RunSplit("patch", "--data", Data, "--plugins", txt, "--out", Out, "--quiet");
        }

        public JsonElement Read() => JsonDocument.Parse(File.ReadAllText(Manifest)).RootElement;

        public string HashOf(JsonElement m, string plugin) =>
            m.GetProperty("records")[0].GetProperty("editors").EnumerateArray()
                .Single(e => e.GetProperty("plugin").GetString() == plugin).GetProperty("hash").GetString()!;
    }

    static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [Fact]
    public void A_patch_gets_a_manifest_with_its_masters_records_and_editors()
    {
        using var game = new Game();
        game.Mod("ModA.esm", m => m.Value = 10);
        game.Mod("ModB.esm", m => m.Weight = 2);
        var (code, stdout, stderr) = game.Patch("Base.esm", "ModA.esm", "ModB.esm");
        Assert.True(code == 0, stdout + stderr);
        var m = game.Read();
        Assert.Equal(1, m.GetProperty("schema").GetInt32());
        Assert.StartsWith("sf-mod-check ", m.GetProperty("tool").GetString());
        Assert.EndsWith("Z", m.GetProperty("made").GetString());
        var patchFile = Path.Combine(game.Out, "ConflictPatch.esm");
        var p = m.GetProperty("patch");
        Assert.Equal("ConflictPatch.esm", p.GetProperty("name").GetString());
        Assert.Equal(new FileInfo(patchFile).Length, p.GetProperty("size").GetInt64());
        Assert.Equal(Sha(patchFile), p.GetProperty("sha256").GetString());
        // file names only, never paths, and without the patch
        Assert.Equal(new[] { "Base.esm", "ModA.esm", "ModB.esm" },
            m.GetProperty("loadOrder").GetProperty("plugins").EnumerateArray().Select(x => x.GetString()));
        var masters = m.GetProperty("masters").EnumerateArray().ToList();
        Assert.Equal(new[] { "Base.esm", "ModA.esm", "ModB.esm" }, masters.Select(x => x.GetProperty("name").GetString()));
        Assert.Equal(new[] { 0, 1, 2 }, masters.Select(x => x.GetProperty("position").GetInt32()));
        var modA = masters[1];
        Assert.Equal(Sha(Path.Combine(game.Data, "ModA.esm")), modA.GetProperty("sha256").GetString());
        Assert.Equal(new FileInfo(Path.Combine(game.Data, "ModA.esm")).Length, modA.GetProperty("size").GetInt64());
        Assert.EndsWith("Z", modA.GetProperty("modified").GetString());
        var rec = Assert.Single(m.GetProperty("records").EnumerateArray());
        Assert.Equal("000800:Base.esm", rec.GetProperty("formKey").GetString());
        Assert.Equal("MiscItem", rec.GetProperty("type").GetString());
        Assert.Equal("B_Misc", rec.GetProperty("editorId").GetString());
        var editors = rec.GetProperty("editors").EnumerateArray().Select(e => e.GetProperty("plugin").GetString()).ToList();
        Assert.Contains("ModA.esm", editors);
        Assert.Contains("ModB.esm", editors);
        var a = game.HashOf(m, "ModA.esm");
        var b = game.HashOf(m, "ModB.esm");
        Assert.Matches("^[0-9a-f]{16}$", a);
        Assert.Matches("^[0-9a-f]{16}$", b);
        Assert.NotEqual(a, b);   // different values, different hashes
        Assert.DoesNotContain(game.Dir, File.ReadAllText(game.Manifest), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_saved_again_copy_keeps_its_hash_and_a_changed_field_changes_it()
    {
        using var game = new Game();
        game.Mod("ModA.esm", m => m.Value = 10);
        game.Mod("ModB.esm", m => m.Weight = 2);
        Assert.Equal(0, game.Patch("Base.esm", "ModA.esm", "ModB.esm").Code);
        var first = game.HashOf(game.Read(), "ModA.esm");

        // saved again with the same values: same hash
        game.Mod("ModA.esm", m => m.Value = 10);
        Assert.Equal(0, game.Patch("Base.esm", "ModA.esm", "ModB.esm").Code);
        Assert.Equal(first, game.HashOf(game.Read(), "ModA.esm"));

        // one field changed: another hash
        game.Mod("ModA.esm", m => m.Value = 11);
        Assert.Equal(0, game.Patch("Base.esm", "ModA.esm", "ModB.esm").Code);
        Assert.NotEqual(first, game.HashOf(game.Read(), "ModA.esm"));
    }

    /// <summary>The record's header bytes the hash leaves out: version control and timestamp (16 to 19), form version
    /// (20 and 21) and the second version-control field (22 and 23).</summary>
    [Fact]
    public void The_hash_leaves_out_the_version_control_timestamp_and_form_version_fields()
    {
        using var game = new Game();
        game.Mod("ModA.esm", m => m.Value = 10);
        var path = Path.Combine(game.Data, "ModA.esm");
        string Hash(string file)
        {
            using var f = new RawFile(file);
            string? h = null;
            new RawWalker(f, deep: false, (_, _, _) => { }) { OnRecord = r => { if (r.Sig == "MISC") h = PatchSources.RecordHash(f, r); } }.Walk();
            return h!;
        }
        long Offset(string file)
        {
            using var f = new RawFile(file);
            long o = -1;
            new RawWalker(f, deep: false, (_, _, _) => { }) { OnRecord = r => { if (r.Sig == "MISC") o = r.Offset; } }.Walk();
            return o;
        }
        var before = Hash(path);
        var off = Offset(path);
        var bytes = File.ReadAllBytes(path);
        for (var i = 16; i < 24; i++) bytes[off + i] ^= 0x5A;
        var tweaked = Path.Combine(game.Sub("tweaked"), "ModA.esm");
        File.WriteAllBytes(tweaked, bytes);
        Assert.Equal(before, Hash(tweaked));
        // a byte of the record's data does count
        bytes[off + 24 + 8] ^= 0x01;
        File.WriteAllBytes(tweaked, bytes);
        Assert.NotEqual(before, Hash(tweaked));
    }

    [Fact]
    public void A_compressed_copy_hashes_like_the_same_data_stored_plain()
    {
        var body = new byte[] { (byte)'E', (byte)'D', (byte)'I', (byte)'D', 2, 0, (byte)'x', 0 };
        Assert.Equal(PatchSources.Hash(0x0, body), PatchSources.Hash(Flags.Compressed, body));
        Assert.NotEqual(PatchSources.Hash(0x0, body), PatchSources.Hash(Flags.Deleted, body));   // other flags count
    }

    [Fact]
    public void Nothing_to_patch_removes_an_old_manifest()
    {
        using var game = new Game();
        game.Mod("ModA.esm", m => m.Value = 10);
        game.Mod("ModB.esm", m => m.Weight = 2);
        Assert.Equal(0, game.Patch("Base.esm", "ModA.esm", "ModB.esm").Code);
        Assert.True(File.Exists(game.Manifest));
        var (code, stdout, _) = game.Patch("Base.esm", "ModA.esm");   // one mod alone: nothing to patch
        Assert.Equal(0, code);
        Assert.StartsWith("RESULT: OK, nothing to patch", ExeTests.ResultLine(stdout));
        Assert.False(File.Exists(game.Manifest));
        Assert.False(File.Exists(Path.Combine(game.Out, "ConflictPatch.esm")));
    }

    [Fact]
    public void The_manifest_and_its_keys_are_in_the_readme_agents_and_changelog()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "PatchSources.cs"))) dir = dir.Parent;
        Assert.True(dir != null, "src\\PatchSources.cs not found above " + AppContext.BaseDirectory);
        string Doc(string n) => File.ReadAllText(Path.Combine(dir!.FullName, n));
        Assert.Contains("`patch-sources.json`", Doc("README.md"));
        Assert.Contains("`patch-sources.json`", Doc("CHANGELOG.md").Split("\n## 0.14.0")[0]);
        var agents = Doc("AGENTS.md");
        foreach (var key in new[] { "schema", "tool", "made", "patch", "loadOrder.plugins", "masters", "position", "modified", "sha256",
                                    "records", "formKey", "type", "editorId", "editors", "plugin", "hash" })
            Assert.True(agents.Contains("`" + key + "`"), $"AGENTS.md doesn't name patch-sources.json's key {key}");
    }

    [Fact]
    public void A_manifest_reads_back()
    {
        using var game = new Game();
        game.Mod("ModA.esm", m => m.Value = 10);
        game.Mod("ModB.esm", m => m.Weight = 2);
        Assert.Equal(0, game.Patch("Base.esm", "ModA.esm", "ModB.esm").Code);
        var m = PatchSources.Read(game.Manifest, out var why);
        Assert.True(m is not null, why);
        Assert.Equal(PatchSources.Schema, m.Schema);
        Assert.Equal("000800:Base.esm", Assert.Single(m.Records).FormKey);
        var bad = Path.Combine(game.Dir, "bad.json");
        File.WriteAllText(bad, "{ not json");
        Assert.Null(PatchSources.Read(bad, out why));
        Assert.Contains("isn't valid JSON", why);
    }
}
