// The triage "Binaries" section (0.8.0, chunk C4), on PE files made by PeBuilder (no outside binaries).
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

public class TriageBinariesTests
{
    sealed record Run(List<TriageFinding> Findings, TriageReport Report)
    {
        public bool Has(TriageLevel level, string file, string fragment) =>
            Findings.Any(f => f.Level == level && f.File == file && f.Text.Contains(fragment, StringComparison.Ordinal));
        public string All => string.Join("\n", Findings.Select(f => $"{f.Level} {f.File}: {f.Text}"));
        public TriageTable Table(string title) => Report.Tables.Single(t => t.Section == TriageSections.Binaries && t.Title == title);
    }

    static Run Triage(Sandbox s, string? manalyze, params (string Path, byte[] Bytes)[] files)
    {
        var root = s.Sub("mod");
        foreach (var (p, b) in files)
        {
            var full = Path.Combine(root, p.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, b);
        }
        var report = new TriageReport { Tool = "sf-mod-check test" };
        var c = new TriageContext
        {
            Options = new TriageOptions { Manalyze = manalyze }, Kind = TriageInputKind.Folder, InputPath = root, Root = root,
            Files = files.Select(f => Ba2Names.Normalize(f.Path)).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            DataPrefix = "", DataDir = s.Sub("nogame"), Work = s.Sub("work"), Report = report,
        };
        var findings = TriageBinaries.Run(c).ToList();
        report.Findings.AddRange(findings);
        return new Run(findings, report);
    }

    static Run Triage(Sandbox s, params (string Path, byte[] Bytes)[] files) => Triage(s, null, files);

    const string Dll = "sfse\\plugins\\fake.dll";

    [Fact]
    public void Import_groups_name_their_functions()
    {
        using var s = new Sandbox();
        var b = new PeBuilder();
        b.Imports.Add(("WS2_32.dll", ["connect", "send"], false));
        b.Imports.Add(("KERNEL32.dll", ["CreateProcessW", "WriteProcessMemory", "CreateRemoteThread", "GetProcAddress", "ReadProcessMemory"], false));
        b.Imports.Add(("ADVAPI32.dll", ["RegOpenKeyExW", "CryptAcquireContextW"], false));
        b.Imports.Add(("WINHTTP.dll", ["WinHttpOpen"], true));
        b.Imports.Add(("USER32.dll", ["GetAsyncKeyState", (ushort)42], false));
        var r = Triage(s, ("SFSE\\Plugins\\Fake.dll", b.Build()));

        Assert.True(r.Has(TriageLevel.Look, Dll, "imports network functions, so it can reach the network: WS2_32.dll: connect, send; WINHTTP.dll: WinHttpOpen"), r.All);
        Assert.True(r.Has(TriageLevel.Look, Dll, "process creation functions, so it can start other programs: CreateProcessW"), r.All);
        Assert.True(r.Has(TriageLevel.Look, Dll, "memory injection functions, so it can write into, or start code in, another process: WriteProcessMemory, CreateRemoteThread"), r.All);
        Assert.True(r.Has(TriageLevel.Note, Dll, "imports registry functions") && r.Has(TriageLevel.Note, Dll, "RegOpenKeyExW"), r.All);
        Assert.True(r.Has(TriageLevel.Note, Dll, "imports crypto functions") && r.Has(TriageLevel.Note, Dll, "CryptAcquireContextW"), r.All);
        Assert.True(r.Has(TriageLevel.Note, Dll, "other processes functions") && r.Has(TriageLevel.Note, Dll, "ReadProcessMemory"), r.All);
        Assert.True(r.Has(TriageLevel.Note, Dll, "run-time loading functions"), r.All);
        Assert.True(r.Has(TriageLevel.Note, Dll, "keyboard and window hooks functions"), r.All);

        var imports = r.Table("Imports");
        Assert.Contains(imports.Rows, row => row[1] == "WINHTTP.dll (delay-loaded)" && row[3] == "WinHttpOpen");
        Assert.Contains(imports.Rows, row => row[1] == "USER32.dll" && row[3] == "GetAsyncKeyState, #42");
        // the plain-style rule: the report never passes a judgement
        foreach (var w in new[] { "malicious", "unsafe", " bad " })
            Assert.DoesNotContain(r.Findings, f => f.Text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_dll_with_ordinary_imports_has_no_look()
    {
        using var s = new Sandbox();
        var b = new PeBuilder();
        b.Imports.Add(("KERNEL32.dll", ["GetModuleHandleW", "VirtualProtect", "OutputDebugStringA"], false));
        b.Exports.Add(("SFSEPlugin_Load", [0xC3]));
        b.Exports.Add(("SFSEPlugin_Version", PeBuilder.SfseVersion("Plain", "Me", 1, 4, 8, [])));
        var r = Triage(s, ("SFSE\\Plugins\\Fake.dll", b.Build()));
        Assert.DoesNotContain(r.Findings, f => f.Level == TriageLevel.Look);
    }

    [Fact]
    public void Sfse_version_data_is_decoded()
    {
        using var s = new Sandbox();
        var b = new PeBuilder { Pdb = @"F:\dev\xse\SFSE_v_0_2_21\sfse\build\SFSEPlugin.pdb" };
        b.Exports.Add(("SFSEPlugin_Load", [0xC3]));
        b.Exports.Add(("SFSEPlugin_Version", PeBuilder.SfseVersion("Fake Fixes", "A. Modder", 21, 2, 0,
            [PeBuilder.Packed(1, 16, 244)], seMin: PeBuilder.Packed(0, 2, 17))));
        var r = Triage(s, ("SFSE\\Plugins\\Fake.dll", b.Build()));

        Assert.True(r.Has(TriageLevel.Note, Dll, "SFSE plugin \"Fake Fixes\" loads only on game version 1.16.244.0 (it declares Address Library v1, which SFSE doesn't count as independence)"), r.All);
        var row = r.Table("SFSE plugins").Rows.Single();
        Assert.Equal([Dll, "Fake Fixes", "A. Modder", "21", "1", "Address Library v1", "none declared", "1.16.244.0", "0.2.17.0", "SFSEPlugin_Load"], row);
        var bin = r.Table("Binaries").Rows.Single();
        Assert.Equal(Dll, bin[0]);
        Assert.Equal("x64", bin[2]);
        Assert.Equal("2026-01-01 00:00:00 UTC", bin[3]);
        Assert.Equal(@"F:\dev\xse\SFSE_v_0_2_21\sfse\build\SFSEPlugin.pdb", bin[5]);
        Assert.Equal("ASLR, DEP", bin[6]);
        Assert.Equal("plugin \"Fake Fixes\", game 1.16.244.0", bin[8]);
        Assert.DoesNotContain(r.Findings, f => f.Level == TriageLevel.Look);
    }

    [Fact]
    public void Sfse_independence_and_faults()
    {
        using var s = new Sandbox();
        PeBuilder Plugin(byte[] version, bool load = true)
        {
            var b = new PeBuilder();
            if (load) b.Exports.Add(("SFSEPlugin_Preload", [0xC3]));
            b.Exports.Add(("SFSEPlugin_Version", version));
            return b;
        }
        var indep = Plugin(PeBuilder.SfseVersion("Indie", "X", 0x01020030, 1 | 4, 1, []));
        var noGames = Plugin(PeBuilder.SfseVersion("Stuck", "X", 1, 0, 0, []));
        var noName = Plugin(PeBuilder.SfseVersion("", "X", 1, 4, 8, [], dataVersion: 0), load: false);
        var noVersion = new PeBuilder();
        noVersion.Exports.Add(("SFSEPlugin_Load", [0xC3]));
        var helper = new PeBuilder();
        helper.Exports.Add(("DoThing", [0xC3]));
        var x86 = Plugin(PeBuilder.SfseVersion("Old", "X", 1, 4, 8, []));
        x86.Is64 = false; x86.Machine = PeFile.MachineI386;
        var r = Triage(s,
            ("sfse\\plugins\\indie.dll", indep.Build()), ("sfse\\plugins\\stuck.dll", noGames.Build()),
            ("sfse\\plugins\\noname.dll", noName.Build()), ("sfse\\plugins\\noversion.dll", noVersion.Build()),
            ("sfse\\plugins\\helper.dll", helper.Build()), ("sfse\\plugins\\old.dll", x86.Build()),
            ("other\\indie2.dll", indep.Build()));

        Assert.True(r.Has(TriageLevel.Note, "sfse\\plugins\\indie.dll", "declares version independence (signature scanning, Address Library v2 (1.15.216+); no game structures), so SFSE loads it on any game version"), r.All);
        Assert.Contains(r.Table("SFSE plugins").Rows, row => row[1] == "Indie" && row[3] == "16908336 (as packed: 1.2.3.0)" && row[4] == "1" && row[9] == "SFSEPlugin_Preload");
        Assert.True(r.Has(TriageLevel.Look, "sfse\\plugins\\stuck.dll", "lists no game versions and doesn't declare version independence"), r.All);
        Assert.True(r.Has(TriageLevel.Look, "sfse\\plugins\\noname.dll", "no name: SFSE disables it"), r.All);
        Assert.True(r.Has(TriageLevel.Look, "sfse\\plugins\\noname.dll", "data version 0"), r.All);
        Assert.True(r.Has(TriageLevel.Look, "sfse\\plugins\\noname.dll", "neither SFSEPlugin_Load nor SFSEPlugin_Preload"), r.All);
        Assert.True(r.Has(TriageLevel.Look, "sfse\\plugins\\noversion.dll", "exports SFSEPlugin_Load but no SFSEPlugin_Version"), r.All);
        Assert.True(r.Has(TriageLevel.Look, "sfse\\plugins\\helper.dll", "in SFSE's Plugins folder but exports no SFSEPlugin_Version"), r.All);
        Assert.True(r.Has(TriageLevel.Look, "sfse\\plugins\\old.dll", "SFSE loads only 64-bit plugins"), r.All);
        Assert.True(r.Has(TriageLevel.Note, "other\\indie2.dll", "outside SFSE\\Plugins"), r.All);
    }

    [Fact]
    public void Urls_and_ip_addresses_in_strings()
    {
        using var s = new Sandbox();
        var b = new PeBuilder();
        b.Ascii.Add("upload to https://example.net/api/v1?x=1 now");
        b.Wide.Add("ftp://files.example.org/drop");
        b.Ascii.Add("<assembly xmlns=\"urn:schemas-microsoft-com:asm.v1\" xmlns:x=\"http://schemas.microsoft.com/SMI/2005/WindowsSettings\">");
        b.Ascii.Add("server=10.20.30.40;port=80");
        b.Ascii.Add("built for 1.16.244.0 and 1.15.216.0");
        var r = Triage(s, ("fake.dll", b.Build()));
        Assert.True(r.Has(TriageLevel.Look, "fake.dll", "has 2 web addresses in its text: https://example.net/api/v1?x=1, ftp://files.example.org/drop"), r.All);
        Assert.True(r.Has(TriageLevel.Look, "fake.dll", "shaped like an IP address (some may be version numbers): 10.20.30.40"), r.All);
        Assert.False(r.Has(TriageLevel.Look, "fake.dll", "1.16.244.0"), r.All);
        Assert.True(r.Has(TriageLevel.Note, "fake.dll", "XML namespace name (addresses used as names in manifests, not fetched): http://schemas.microsoft.com/SMI/2005/WindowsSettings"), r.All);
    }

    [Fact]
    public void Damaged_and_odd_binaries()
    {
        using var s = new Sandbox();
        var good = new PeBuilder().Build();
        var exe = new PeBuilder { Dll = false }.Build();
        var proxy = new PeBuilder().Build();
        var r = Triage(s, ("sfse\\plugins\\junk.dll", System.Text.Encoding.ASCII.GetBytes(new string('x', 100))), ("cut.dll", good[..0x120]),
            ("tool.exe", exe), ("dxgi.dll", proxy));
        Assert.True(r.Has(TriageLevel.Look, "sfse\\plugins\\junk.dll", "can't be read as a Windows program: no MZ header"), r.All);
        Assert.True(r.Has(TriageLevel.Look, "cut.dll", "runs past the end of the file"), r.All);
        Assert.Contains(r.Table("Binaries").Rows, row => row[0] == "cut.dll" && row[1] == "not a readable PE file");
        Assert.True(r.Has(TriageLevel.Note, "tool.exe", "is a program (.exe); triage doesn't run it"), r.All);
        Assert.True(r.Has(TriageLevel.Look, "dxgi.dll", "has the name of a Windows DLL (dxgi.dll)"), r.All);
    }

    [Fact]
    public void Signature_status_is_in_the_table()
    {
        using var s = new Sandbox();
        var r = Triage(s, ("fake.dll", new PeBuilder().Build()));
        var cell = r.Table("Binaries").Rows.Single()[7];
        if (OperatingSystem.IsWindows()) Assert.Equal("not signed", cell);
        else Assert.StartsWith("not checked (not Windows", cell);
    }

    [Fact]
    public void No_binaries_no_tables()
    {
        using var s = new Sandbox();
        var r = Triage(s, ("readme.txt", "hello"u8.ToArray()));
        Assert.Empty(r.Findings);
        Assert.DoesNotContain(r.Report.Tables, t => t.Section == TriageSections.Binaries);
    }

    [Fact]
    public void Manalyze_output_is_parsed()
    {
        const string json = """
            { "C:/x/Fake.dll": { "Summary": { "Architecture": "IMAGE_FILE_MACHINE_AMD64" },
              "Plugins": {
                "imports": { "level": 2, "summary": "The PE contains functions most legitimate programs don't use.",
                             "plugin_output": { "Memory manipulation functions often used by packers": ["VirtualAlloc", "VirtualProtect"] } },
                "mitigation": { "level": 1, "summary": "The following exploit mitigation techniques have been detected",
                                "plugin_output": { "ASLR": "enabled", "DEP": "enabled" } } } } }
            """;
        var rows = TriageBinaries.ParseManalyze(json)!;
        Assert.Equal(2, rows.Count);
        Assert.Equal(new TriageBinaries.ManalyzeRow("imports", 2, "The PE contains functions most legitimate programs don't use.",
            "Memory manipulation functions often used by packers: VirtualAlloc, VirtualProtect"), rows[0]);
        Assert.Equal("ASLR: enabled; DEP: enabled", rows[1].Details);
        Assert.Null(TriageBinaries.ParseManalyze("not json"));
        Assert.Null(TriageBinaries.ParseManalyze("[1,2]"));
        Assert.Empty(TriageBinaries.ParseManalyze("""{ "f": { "Summary": {} } }""")!);
    }

    [Fact]
    public void Manalyze_never_calls_the_file_malware()
    {
        // user's decision 2026-11-19: "binary", not "malware", even in Manalyze's own summaries
        var row = new TriageBinaries.ManalyzeRow("imports", 3, "The PE contains functions mostly used by malware.",
            "Malware-like imports: OpenProcess; MALWARE");
        var note = TriageBinaries.ManalyzeNote(row);
        // the word is left only in the rewording note at the end
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(note, "malware", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count);
        Assert.Contains("reports: The PE contains functions mostly used by binary. (Binary-like imports: OpenProcess; Binary)", note);
        Assert.EndsWith("[Manalyze's wording changed: \"binary\" where it says \"malware\"]", note);
        Assert.DoesNotContain("says: \"", note);   // reworded text is never shown as a quotation

        // a summary without the word is passed on as it is, with no rewording note
        var plain = TriageBinaries.ManalyzeNote(new("mitigation", 2, "No mitigation found.", ""));
        Assert.Equal("Manalyze's mitigation plugin (level 2 of 3) reports: No mitigation found.", plain);
        Assert.Equal("Binary here, binary there", TriageBinaries.PlainManalyze("Malware here, malware there"));
    }

    [Fact]
    public void A_manalyze_that_cannot_run_is_a_note()
    {
        using var s = new Sandbox();
        var fake = Path.Combine(s.Sub("tools"), "manalyze.exe");
        File.WriteAllText(fake, "this is not a program");
        File.WriteAllText(Path.Combine(s.Sub("tools"), "plugin_virustotal.dll"), "x");
        var r = Triage(s, fake, ("fake.dll", new PeBuilder().Build()));
        Assert.True(r.Has(TriageLevel.Note, "", "Manalyze ran offline with these plugins only: compilers, peid"), r.All);
        Assert.True(r.Has(TriageLevel.Note, "", "its VirusTotal plugin is installed beside it but wasn't asked for"), r.All);
        Assert.True(r.Has(TriageLevel.Note, "fake.dll", "Manalyze couldn't be run") || r.Has(TriageLevel.Note, "fake.dll", "Manalyze gave no readable result"), r.All);
        Assert.DoesNotContain("virustotal", string.Join(",", TriageBinaries.ManalyzePlugins));
        Assert.DoesNotContain("authenticode", string.Join(",", TriageBinaries.ManalyzePlugins));
    }

    [Fact]
    public void Sfse_runtime_versions_unpack()
    {
        Assert.Equal("1.16.244.0", SfseVersion.Runtime(0x01100F40));
        Assert.Equal("1.15.216.0", SfseVersion.Runtime(PeBuilder.Packed(1, 15, 216)));
        Assert.Throws<InvalidDataException>(() => SfseVersion.Decode(new byte[100]));
    }
}
