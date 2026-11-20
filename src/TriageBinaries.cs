// Triage section "Binaries" (0.8.0, chunk C4): what each .dll and .exe in the mod is, read from its bytes with Pe.cs.
// It never loads or runs a binary. Per file: one row in the Binaries table (machine, build time, size, PDB path,
// protections, signature, SFSE facts), its imports grouped by what they let a program do, the URLs and IP-like
// addresses in its strings, and, for an SFSE plugin, the decoded SFSEPlugin_Version data (SFSE's public PluginAPI.h
// layout). Signatures are checked with WinVerifyTrust on Windows with no revocation check and cache-only URL
// retrieval, so nothing is fetched; elsewhere the cell says "not checked". With --manalyze, Manalyze runs on each
// binary with a fixed list of plugins that work offline (never virustotal, and not authenticode, which may check
// revocation online), with a time limit; anything that goes wrong there is a NOTE.
// SHA-256 and sizes of every file are the Files section's job; this section repeats only the size.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SfModCheck;

/// <summary>The SFSEPluginVersionData an SFSE plugin exports as SFSEPlugin_Version (SFSE's PluginAPI.h).</summary>
public sealed record SfseVersion(uint DataVersion, uint PluginVersion, string Name, string Author, uint AddressIndependence,
    uint StructureIndependence, List<uint> CompatibleVersions, uint SeVersionRequired)
{
    /// <summary>dataVersion, pluginVersion, name[256], author[256], address and structure flags, compatibleVersions[16],
    /// seVersionRequired, two reserved words.</summary>
    public const int Size = 604;
    public const int MinSize = 596;   // through seVersionRequired
    public const uint AddrSignatures = 1, AddrLibrary = 2, AddrLibraryV2 = 4;
    public const uint StructNone = 1, StructInitial = 2, Struct1_8_86 = 4, Struct1_14_70 = 8;

    /// <summary>SFSE's own rule (PluginManager::checkPluginCompatibility): version independent means address
    /// independence (signatures or Address Library v2) and structure independence (no structs or the 1.14.70 layout).</summary>
    public bool VersionIndependent =>
        (AddressIndependence & (AddrSignatures | AddrLibraryV2)) != 0 && (StructureIndependence & (StructNone | Struct1_14_70)) != 0;

    /// <summary>MAKE_EXE_VERSION_EX: major 8 bits, minor 8, build 12, sub 4.</summary>
    public static string Runtime(uint v) => $"{v >> 24}.{(v >> 16) & 0xff}.{(v >> 4) & 0xfff}.{v & 0xf}";

    public static string AddressFlags(uint f) => Flags(f, [(AddrSignatures, "signature scanning"), (AddrLibrary, "Address Library v1"), (AddrLibraryV2, "Address Library v2 (1.15.216+)")]);
    public static string StructureFlags(uint f) => Flags(f, [(StructNone, "no game structures"), (StructInitial, "initial layout"), (Struct1_8_86, "1.8.86+ layout"), (Struct1_14_70, "1.14.70+ layout")]);

    static string Flags(uint f, (uint Bit, string Name)[] names)
    {
        if (f == 0) return "none declared";
        var parts = names.Where(n => (f & n.Bit) != 0).Select(n => n.Name).ToList();
        var known = names.Aggregate(0u, (a, n) => a | n.Bit);
        if ((f & ~known) != 0) parts.Add($"unknown bits 0x{f & ~known:X}");
        return string.Join(", ", parts);
    }

    public static SfseVersion Decode(byte[] b)
    {
        if (b.Length < MinSize) throw new InvalidDataException($"SFSEPlugin_Version is {b.Length} bytes, shorter than SFSE's {Size}");
        uint U(int o) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
        string Str(int o)
        {
            var span = b.AsSpan(o, 256);
            int z = span.IndexOf((byte)0);
            return new string((z >= 0 ? span[..z] : span).ToArray().Select(c => c is >= 0x20 and < 0x7f ? (char)c : '?').ToArray());
        }
        var versions = new List<uint>();
        for (int i = 0; i < 16; i++) { var v = U(528 + i * 4); if (v == 0) break; versions.Add(v); }
        return new SfseVersion(U(0), U(4), Str(8), Str(264), U(520), U(524), versions, U(592));
    }
}

/// <summary>A signature check result: Text for the table, Look when it is worth a person's look.</summary>
public sealed record SignatureInfo(string Text, bool Look);

public static class TriageBinaries
{
    const string S = TriageSections.Binaries;

    /// <summary>The Manalyze plugins triage asks for: all of them that work offline. Never virustotal (it uploads a
    /// hash), never authenticode (it may check revocation online; triage checks signatures itself), never clamav (needs
    /// a database download).</summary>
    public static readonly string[] ManalyzePlugins = ["compilers", "peid", "strings", "findcrypt", "cryptoaddress", "packer", "imports", "resources", "mitigation", "overlay"];
    public static TimeSpan ManalyzeTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>The signature check (tests can swap it).</summary>
    public static Func<string, PeFile, SignatureInfo> CheckSignature { get; set; } = DefaultSignature;

    sealed record Group(string Name, bool Look, string Why, Func<PeImport, bool> Match);

    static readonly HashSet<string> NetworkDlls = new(StringComparer.OrdinalIgnoreCase)
    {
        "ws2_32.dll", "wsock32.dll", "mswsock.dll", "wininet.dll", "winhttp.dll", "urlmon.dll", "dnsapi.dll", "websocket.dll",
        "httpapi.dll", "netapi32.dll", "iphlpapi.dll", "mpr.dll", "rasapi32.dll", "winhttpcom.dll",
    };
    static readonly HashSet<string> ProcessFns = new(StringComparer.Ordinal)
    {
        "CreateProcessA", "CreateProcessW", "CreateProcessAsUserA", "CreateProcessAsUserW", "CreateProcessWithLogonW",
        "CreateProcessWithTokenW", "CreateProcessInternalW", "ShellExecuteA", "ShellExecuteW", "ShellExecuteExA",
        "ShellExecuteExW", "WinExec", "NtCreateUserProcess", "NtCreateProcess", "NtCreateProcessEx", "RtlCreateUserProcess",
        "system", "_wsystem", "_popen", "_wpopen",
    };
    static readonly Regex CrtSpawn = new(@"^_w?(spawn|exec)(l|le|lp|lpe|v|ve|vp|vpe)$", RegexOptions.CultureInvariant);
    static readonly HashSet<string> InjectionFns = new(StringComparer.Ordinal)
    {
        "VirtualAllocEx", "VirtualAllocExNuma", "VirtualProtectEx", "WriteProcessMemory", "CreateRemoteThread",
        "CreateRemoteThreadEx", "NtCreateThreadEx", "ZwCreateThreadEx", "RtlCreateUserThread", "QueueUserAPC", "QueueUserAPC2",
        "NtQueueApcThread", "NtQueueApcThreadEx", "SetThreadContext", "NtSetContextThread", "Wow64SetThreadContext",
        "NtWriteVirtualMemory", "ZwWriteVirtualMemory", "NtMapViewOfSection", "ZwMapViewOfSection", "NtUnmapViewOfSection",
        "MapViewOfFile2", "MapViewOfFileNuma2",
    };
    static readonly HashSet<string> OtherProcessFns = new(StringComparer.Ordinal)
    {
        "OpenProcess", "ReadProcessMemory", "NtReadVirtualMemory", "CreateToolhelp32Snapshot", "Process32First", "Process32FirstW",
        "Process32Next", "Process32NextW", "EnumProcesses", "K32EnumProcesses", "DebugActiveProcess", "NtOpenProcess",
    };
    static readonly HashSet<string> DynamicFns = new(StringComparer.Ordinal)
    {
        "LoadLibraryA", "LoadLibraryW", "LoadLibraryExA", "LoadLibraryExW", "LdrLoadDll", "GetProcAddress", "LdrGetProcedureAddress",
    };
    static readonly HashSet<string> InputFns = new(StringComparer.Ordinal)
    {
        "SetWindowsHookExA", "SetWindowsHookExW", "GetAsyncKeyState", "RegisterHotKey", "RegisterRawInputDevices", "GetRawInputData",
    };
    static readonly HashSet<string> CryptoDlls = new(StringComparer.OrdinalIgnoreCase) { "bcrypt.dll", "ncrypt.dll", "crypt32.dll", "cryptsp.dll", "cryptbase.dll" };
    static readonly Regex RegistryFn = new(@"^(Reg[A-Z]\w*|Nt(Open|Create|Delete|Set|Query|Enumerate)(Key|ValueKey|Value)\w*|Zw(Open|Create|Delete|Set|Query)(Key|ValueKey)\w*)$", RegexOptions.CultureInvariant);

    static readonly Group[] Groups =
    [
        new("network", true, "can reach the network", i => NetworkDlls.Contains(i.Dll)),
        new("process creation", true, "can start other programs", i => i.Name is { } n && (ProcessFns.Contains(n) || CrtSpawn.IsMatch(n))),
        new("memory injection", true, "can write into, or start code in, another process", i => i.Name is { } n && InjectionFns.Contains(n)),
        new("other processes", false, "can open or read other processes (a crash logger may read its own)", i => i.Name is { } n && OtherProcessFns.Contains(n)),
        new("registry", false, "reads or writes the Windows registry", i => i.Name is { } n && RegistryFn.IsMatch(n) && !NetworkDlls.Contains(i.Dll)),
        new("crypto", false, "uses Windows cryptography (hashes, encryption or certificates)",
            i => CryptoDlls.Contains(i.Dll) || (i.Name is { } n && n.StartsWith("Crypt", StringComparison.Ordinal))),
        new("keyboard and window hooks", false, "reads the keyboard or hooks window messages (hotkeys work this way)", i => i.Name is { } n && InputFns.Contains(n)),
        new("run-time loading", false, "loads DLLs or looks up functions while running, so the import list may not be all it calls", i => i.Name is { } n && DynamicFns.Contains(n)),
    ];

    /// <summary>Windows DLL names a game loads from its own folder before Windows' copy: a DLL with one of these names in
    /// the game folder is loaded in place of Windows' own (how ReShade and ASI loaders get in).</summary>
    static readonly HashSet<string> ProxyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dxgi.dll", "d3d11.dll", "d3d12.dll", "dinput8.dll", "version.dll", "winmm.dll", "winhttp.dll", "xinput1_3.dll",
        "xinput1_4.dll", "xinput9_1_0.dll", "dsound.dll", "dbghelp.dll", "d3d9.dll", "wininet.dll", "iphlpapi.dll",
    };

    static readonly Regex UrlRx = new(@"\b(?:https?|ftp|wss?)://[A-Za-z0-9\-._~%!$&'()*+,;=:@/?#\[\]]{3,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex IpRx = new(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?![\d.])", RegexOptions.CultureInvariant);
    /// <summary>Hosts that appear as XML namespace names in manifests and resources: names, not addresses fetched.</summary>
    static readonly HashSet<string> NamespaceHosts = new(StringComparer.OrdinalIgnoreCase) { "schemas.microsoft.com", "www.w3.org", "schemas.xmlsoap.org", "ns.adobe.com", "purl.org" };

    public static IEnumerable<TriageFinding> Run(TriageContext c)
    {
        var bins = c.Binaries;
        if (bins.Count == 0) return [];
        var findings = new List<TriageFinding>();
        var table = c.Report.Table(S, "Binaries", "File", "Kind", "Machine", "Build time", "Size", "PDB path", "Protections", "Signed", "SFSE");
        var imports = c.Report.Table(S, "Imports", "File", "DLL", "Count", "Functions");
        TriageTable? sfse = null;
        var readable = new List<(string Full, string Rel)>();
        foreach (var full in bins)
        {
            var rel = Ba2Names.Normalize(Path.GetRelativePath(c.Root, full));
            PeFile pe;
            long size;
            try
            {
                size = new FileInfo(full).Length;
                pe = PeFile.Read(full);
            }
            catch (InvalidDataException e)
            {
                Log.Write($"binaries: {rel}: not readable as PE: {e.Message}");
                findings.Add(new(TriageLevel.Look, S, rel, $"has a {RelPath.Extension(rel)} name but can't be read as a Windows program: {e.Message}"));
                table.Add(rel, "not a readable PE file", "", "", $"{new FileInfo(full).Length:N0}", "", "", "", "");
                continue;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                findings.Add(new(TriageLevel.Look, S, rel, $"couldn't be opened: {e.Message}"));
                continue;
            }
            readable.Add((full, rel));
            Log.Write($"binaries: {rel}: {pe.MachineName} {(pe.IsDll ? "DLL" : "EXE")}, {pe.Sections.Count} sections, {pe.Imports.Count} imports, {pe.Exports.Count} exports, {pe.Problems.Count} problems");
            var sig = SafeSignature(full, pe);
            if (sig.Look) findings.Add(new(TriageLevel.Look, S, rel, $"signature: {sig.Text}"));
            string sfseCell = Sfse(pe, rel, findings, ref sfse, c.Report);
            table.Add(rel, Kind(pe), pe.MachineName, BuildTime(pe), $"{size:N0}", pe.PdbPath ?? "", Protections(pe), sig.Text, sfseCell);

            foreach (var p in pe.Problems)
                findings.Add(new(TriageLevel.Look, S, rel, $"part of the file couldn't be read ({p}); the lists for it may be short"));
            if (pe.IsDotNet)
                findings.Add(new(TriageLevel.Note, S, rel, ".NET assembly: what it uses comes from .NET references, which this section doesn't list; its strings were still searched"));
            if (pe.TlsCallbacks > 0)
                findings.Add(new(TriageLevel.Note, S, rel, $"has {pe.TlsCallbacks} TLS callback{(pe.TlsCallbacks == 1 ? "" : "s")} (code that runs when it is loaded, before its main entry point; the C runtime adds some)"));
            if (pe.IsExe)
                findings.Add(new(TriageLevel.Note, S, rel, "is a program (.exe); triage doesn't run it. The mod's description should say what it is for"));
            if (rel.EndsWith(".asi", StringComparison.Ordinal))
                findings.Add(new(TriageLevel.Look, S, rel, "is an ASI plugin: an ASI loader puts it into the game's own process, where it can do anything the game can (not SFSE's plugin system, so none of SFSE's version checks apply)"));
            if (pe.IsDll && !rel.Contains("sfse\\plugins\\", StringComparison.Ordinal) && ProxyNames.Contains(RelPath.FileName(rel)))
                findings.Add(new(TriageLevel.Look, S, rel, $"has the name of a Windows DLL ({RelPath.FileName(rel)}): put in the game's folder, the game loads it in place of Windows' own copy (ReShade and ASI loaders work this way)"));

            foreach (var g in pe.Imports.GroupBy(i => (Dll: i.Dll.ToLowerInvariant(), i.Delayed)).OrderBy(g => g.Key.Dll, StringComparer.Ordinal))
            {
                var names = g.Select(i => i.Display).Distinct().ToList();
                imports.Add(rel, g.First().Dll + (g.Key.Delayed ? " (delay-loaded)" : ""), names.Count.ToString(), Cap(names, 80));
            }
            foreach (var grp in Groups)
            {
                var hit = pe.Imports.Where(grp.Match).ToList();
                if (hit.Count == 0) continue;
                var what = grp.Name is "network" or "crypto"
                    ? string.Join("; ", hit.GroupBy(i => i.Dll.ToLowerInvariant()).Select(d => $"{d.First().Dll}: {Cap(d.Select(i => i.Display).Distinct().ToList(), 25)}"))
                    : Cap(hit.Select(i => i.Display).Distinct().ToList(), 25);
                findings.Add(new(grp.Look ? TriageLevel.Look : TriageLevel.Note, S, rel, $"imports {grp.Name} functions, so it {grp.Why}: {what}"));
            }
            Strings(pe, rel, findings);
        }
        if (c.Options.Manalyze is { } m && readable.Count > 0)
            findings.AddRange(Manalyze(m, readable, c));
        return findings;
    }

    static string Cap(List<string> items, int max) =>
        items.Count <= max ? string.Join(", ", items) : string.Join(", ", items.Take(max)) + $", and {items.Count - max} more";

    static string Kind(PeFile pe)
    {
        var k = pe.IsDll ? "DLL" : pe.IsExe ? $"program ({pe.SubsystemName})" : "PE image";
        if (pe.IsDotNet) k += ", .NET";
        if (pe.Exports.Count > 0) k += $", {pe.Exports.Count} export{(pe.Exports.Count == 1 ? "" : "s")}";
        return k;
    }

    static string Protections(PeFile pe)
    {
        var p = new List<string> { pe.Aslr ? "ASLR" : "no ASLR", pe.Dep ? "DEP" : "no DEP" };
        if (pe.Cfg) p.Add("CFG");
        return string.Join(", ", p);
    }

    static string BuildTime(PeFile pe)
    {
        if (pe.Reproducible) return $"not a date (reproducible build; the field holds 0x{pe.TimeDateStamp:X8})";
        if (pe.TimeDateStamp == 0) return "not set";
        var t = pe.BuildTime;
        var s = t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";
        if (t > DateTimeOffset.UtcNow.AddDays(2) || t.Year < 2000) s += " (not a real date: some build tools put a hash here)";
        return s;
    }

    // ---- SFSE ----

    static string Sfse(PeFile pe, string rel, List<TriageFinding> findings, ref TriageTable? table, TriageReport report)
    {
        bool inPlugins = rel.Contains("sfse\\plugins\\", StringComparison.Ordinal);
        var ver = pe.Export("SFSEPlugin_Version");
        bool load = pe.Export("SFSEPlugin_Load") != null, preload = pe.Export("SFSEPlugin_Preload") != null;
        foreach (var other in new[] { "F4SEPlugin_Query", "F4SEPlugin_Load", "SKSEPlugin_Query", "SKSEPlugin_Load", "OBSEPlugin_Load", "NVSEPlugin_Load" })
            if (pe.Export(other) != null)
            {
                findings.Add(new(TriageLevel.Look, S, rel, $"exports {other}, a plugin function of another game's script extender (Fallout 4, Skyrim, Oblivion or New Vegas): it may be made for another game"));
                break;
            }
        if (!pe.IsDll) return "";
        if (ver == null)
        {
            if (load || preload)
            {
                findings.Add(new(TriageLevel.Look, S, rel, $"exports {(load ? "SFSEPlugin_Load" : "SFSEPlugin_Preload")} but no SFSEPlugin_Version: SFSE reads SFSEPlugin_Version first and skips a plugin without it (\"no version data\")"));
                return "SFSE functions, no version data";
            }
            if (inPlugins)
            {
                findings.Add(new(TriageLevel.Look, S, rel, "is in SFSE's Plugins folder but exports no SFSEPlugin_Version: SFSE skips it (\"no version data\"), so something else must load it, or it does nothing"));
                return "in SFSE\\Plugins, not an SFSE plugin";
            }
            return "";
        }
        if (pe.Machine != PeFile.MachineAmd64)
            findings.Add(new(TriageLevel.Look, S, rel, $"is an SFSE plugin built for {pe.MachineName}; SFSE loads only 64-bit plugins"));
        if (!inPlugins)
            findings.Add(new(TriageLevel.Note, S, rel, "is an SFSE plugin outside SFSE\\Plugins; SFSE looks for plugins only in Data\\SFSE\\Plugins"));
        if (!load && !preload)
            findings.Add(new(TriageLevel.Look, S, rel, "exports SFSEPlugin_Version but neither SFSEPlugin_Load nor SFSEPlugin_Preload, so SFSE has nothing to call"));
        var bytes = ver.Forwarder == null ? pe.BytesAt(ver.Rva, SfseVersion.Size) ?? pe.BytesAt(ver.Rva, SfseVersion.MinSize) : null;
        if (bytes == null)
        {
            findings.Add(new(TriageLevel.Look, S, rel, $"SFSEPlugin_Version (RVA 0x{ver.Rva:X}) can't be read from the file{(ver.Forwarder != null ? $" (it forwards to {ver.Forwarder})" : "")}"));
            return "SFSE plugin, version data unreadable";
        }
        var v = SfseVersion.Decode(bytes);
        table ??= report.Table(S, "SFSE plugins", "File", "Name", "Author", "Plugin version", "Data version", "Finds addresses by", "Game structures",
            "Game versions", "Minimum SFSE", "Entry points");
        var games = v.VersionIndependent ? "any (version independent by SFSE's rule)"
            : v.CompatibleVersions.Count == 0 ? "none listed" : string.Join(", ", v.CompatibleVersions.Select(SfseVersion.Runtime));
        var entry = string.Join(", ", new[] { load ? "SFSEPlugin_Load" : null, preload ? "SFSEPlugin_Preload" : null }.Where(x => x != null));
        table.Add(rel, v.Name, v.Author, PluginVersion(v.PluginVersion), v.DataVersion.ToString(), SfseVersion.AddressFlags(v.AddressIndependence),
            SfseVersion.StructureFlags(v.StructureIndependence), games, v.SeVersionRequired == 0 ? "any" : SfseVersion.Runtime(v.SeVersionRequired), entry);

        if (v.DataVersion == 0) findings.Add(new(TriageLevel.Look, S, rel, "SFSEPlugin_Version has data version 0: SFSE disables it (\"bad version data\")"));
        else if (v.DataVersion != 1) findings.Add(new(TriageLevel.Note, S, rel, $"SFSEPlugin_Version has data version {v.DataVersion}; the SFSE this reader follows uses 1"));
        if (v.Name.Length == 0) findings.Add(new(TriageLevel.Look, S, rel, "SFSEPlugin_Version has no name: SFSE disables it (\"no name specified\")"));
        if (v.VersionIndependent)
            findings.Add(new(TriageLevel.Note, S, rel, $"SFSE plugin \"{v.Name}\" declares version independence ({SfseVersion.AddressFlags(v.AddressIndependence)}; {SfseVersion.StructureFlags(v.StructureIndependence)}), so SFSE loads it on any game version"));
        else if (v.CompatibleVersions.Count == 0)
            findings.Add(new(TriageLevel.Look, S, rel, $"SFSE plugin \"{v.Name}\" lists no game versions and doesn't declare version independence, so SFSE loads it on no game version"));
        else
        {
            var why = (v.AddressIndependence & SfseVersion.AddrLibrary) != 0 && (v.AddressIndependence & SfseVersion.AddrLibraryV2) == 0
                ? " (it declares Address Library v1, which SFSE doesn't count as independence)"
                : v.AddressIndependence == 0 ? " (it declares neither Address Library nor signature scanning)" : "";
            findings.Add(new(TriageLevel.Note, S, rel, $"SFSE plugin \"{v.Name}\" loads only on game version{(v.CompatibleVersions.Count == 1 ? "" : "s")} {games}{why}; after a game update it needs a new build"));
        }
        return $"plugin \"{v.Name}\", game {(v.VersionIndependent ? "any" : games)}";
    }

    static string PluginVersion(uint v) => v >= 0x10000 ? $"{v} (as packed: {SfseVersion.Runtime(v)})" : v.ToString();

    // ---- strings ----

    static void Strings(PeFile pe, string rel, List<TriageFinding> findings)
    {
        var urls = new List<string>();
        var ns = new List<string>();
        var ips = new List<string>();
        int inCert = 0;
        var cert = pe.Certificate;
        foreach (var s in pe.Strings())
        {
            bool certString = cert is { } ct && s.Offset >= ct.Offset && s.Offset < (long)ct.Offset + ct.Size;
            foreach (Match m in UrlRx.Matches(s.Text))
            {
                if (certString) { inCert++; continue; }
                var u = m.Value.TrimEnd('.', ',', ')', ']', ';', '\'');
                (NamespaceHosts.Contains(Host(u)) ? ns : urls).Add(u);
            }
            if (certString) continue;
            foreach (Match m in IpRx.Matches(s.Text))
            {
                var parts = m.Value.Split('.').Select(int.Parse).ToArray();
                // version numbers like 1.16.244.0 have the same shape; an address ending in 0 or 255, or starting with 0, is rarely a host
                if (parts[0] == 0 || parts[3] == 0 || parts[3] == 255) continue;
                ips.Add(m.Value);
            }
        }
        urls = urls.Distinct().ToList(); ns = ns.Distinct().ToList(); ips = ips.Distinct().ToList();
        if (urls.Count > 0)
            findings.Add(new(TriageLevel.Look, S, rel, $"has {urls.Count} web address{(urls.Count == 1 ? "" : "es")} in its text: {Cap(urls, 20)}"));
        if (ips.Count > 0)
            findings.Add(new(TriageLevel.Look, S, rel, $"has {ips.Count} text{(ips.Count == 1 ? "" : "s")} shaped like an IP address (some may be version numbers): {Cap(ips, 20)}"));
        if (ns.Count > 0)
            findings.Add(new(TriageLevel.Note, S, rel, $"has {ns.Count} XML namespace name{(ns.Count == 1 ? "" : "s")} (addresses used as names in manifests, not fetched): {Cap(ns, 10)}"));
        if (inCert > 0)
            findings.Add(new(TriageLevel.Note, S, rel, $"its signature block holds {inCert} certificate address{(inCert == 1 ? "" : "es")} (certificate authorities' pages; not part of the program)"));
    }

    static string Host(string url)
    {
        var i = url.IndexOf("://", StringComparison.Ordinal);
        var rest = i >= 0 ? url[(i + 3)..] : url;
        var end = rest.IndexOfAny(['/', ':', '?', '#']);
        return end >= 0 ? rest[..end] : rest;
    }

    // ---- signatures ----

    static SignatureInfo SafeSignature(string path, PeFile pe)
    {
        try { return CheckSignature(path, pe); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Write($"binaries: signature check of {path} failed: {e}");
            return new SignatureInfo($"not checked ({e.GetType().Name}: {e.Message})", false);
        }
    }

    public static SignatureInfo DefaultSignature(string path, PeFile pe)
    {
        if (!OperatingSystem.IsWindows())
            return new SignatureInfo(pe.Certificate != null ? "not checked (not Windows; it has a signature block)" : "not checked (not Windows; no signature block)", false);
        uint r = Authenticode.Verify(path);
        string? subject = null;
        if (pe.Certificate != null)
        {
            try
            {
#pragma warning disable SYSLIB0057
                using var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
                subject = cert.Subject;
            }
            catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or ArgumentException) { }
        }
        var who = subject != null ? $" by {subject}" : "";
        Log.Write($"binaries: WinVerifyTrust {path}: 0x{r:X8}{who}");
        return r switch
        {
            0 => new($"signed{who}, valid", false),
            0x800B0100 => pe.Certificate != null
                ? new("has a signature block that Windows doesn't read as a signature", true)
                : new("not signed", false),
            0x80096010 => new($"signed{who}, but the file was changed after signing (the signature doesn't match its bytes)", true),
            0x800B0109 => new($"signed{who}, with a certificate Windows doesn't trust (self-signed or unknown root)", false),
            0x800B010A => new($"signed{who}, but the certificate chain can't be built offline", false),
            0x800B0101 => new($"signed{who}, with an expired certificate and no timestamp", false),
            0x800B0111 => new($"signed{who}, with a certificate this PC is set to distrust", true),
            0x800B010C => new($"signed{who}, with a revoked certificate", true),
            0x800B0003 or 0x800B0001 => new("not checked (Windows has no signature reader for this file)", false),
            0x80092026 => new($"signed{who}, refused by this PC's security settings", false),
            _ => new($"signed{who}, not valid (WinVerifyTrust 0x{r:X8})", true),
        };
    }

    static class Authenticode
    {
        static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1, WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2;
        const uint WTD_REVOCATION_CHECK_NONE = 0x10, WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000, WTD_DISABLE_MD2_MD4 = 0x2000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TrustFileInfo
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true)]
        static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid action, ref TrustData data);

        /// <summary>WinVerifyTrust on a file: no UI, no revocation check, URLs only from the local cache (no network).</summary>
        public static uint Verify(string path)
        {
            var pPath = Marshal.StringToHGlobalUni(path);
            var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFileInfo>());
            try
            {
                Marshal.StructureToPtr(new TrustFileInfo { cbStruct = (uint)Marshal.SizeOf<TrustFileInfo>(), pcwszFilePath = pPath }, pFile, false);
                var d = new TrustData
                {
                    cbStruct = (uint)Marshal.SizeOf<TrustData>(),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = pFile,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_DISABLE_MD2_MD4,
                };
                var action = GenericVerifyV2;
                uint r = WinVerifyTrust(IntPtr.Zero, ref action, ref d);
                d.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(IntPtr.Zero, ref action, ref d);
                return r;
            }
            finally
            {
                Marshal.FreeHGlobal(pFile);
                Marshal.FreeHGlobal(pPath);
            }
        }
    }

    // ---- Manalyze ----

    static List<TriageFinding> Manalyze(string exe, List<(string Full, string Rel)> files, TriageContext c)
    {
        var findings = new List<TriageFinding>();
        var plugins = string.Join(",", ManalyzePlugins);
        var dir = RelPath.DirectoryName(Path.GetFullPath(exe)) ?? "";
        bool vt = false;
        try { vt = Directory.Exists(dir) && Directory.EnumerateFiles(dir, "plugin_virustotal*.dll").Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        findings.Add(new(TriageLevel.Note, S, "", $"Manalyze ran offline with these plugins only: {plugins.Replace(",", ", ")} (never virustotal, which sends a hash online){(vt ? "; its VirusTotal plugin is installed beside it but wasn't asked for" : "")}"));
        var table = c.Report.Table(S, "Manalyze", "File", "Plugin", "Level", "Summary", "Details");
        foreach (var (full, rel) in files)
        {
            try
            {
                var (code, stdout, stderr, timedOut) = RunProcess(exe, ["-o", "json", "-p", plugins, "--log-level", "error", full], c.Work, ManalyzeTimeout);
                Log.Write($"binaries: manalyze {rel}: exit {code}, {stdout.Length} chars out{(timedOut ? ", timed out" : "")}");
                if (timedOut)
                {
                    findings.Add(new(TriageLevel.Note, S, rel, $"Manalyze didn't finish within {ManalyzeTimeout.TotalSeconds:F0} seconds and was stopped"));
                    continue;
                }
                var rows = ParseManalyze(stdout);
                if (rows == null)
                {
                    var why = stderr.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
                    findings.Add(new(TriageLevel.Note, S, rel, $"Manalyze gave no readable result (exit code {code}{(why != "" ? $": {why}" : "")})"));
                    continue;
                }
                foreach (var r in rows) table.Add(rel, r.Plugin, LevelText(r.Level), PlainManalyze(r.Summary), PlainManalyze(r.Details));
                foreach (var r in rows.Where(r => r.Level >= 2))
                    findings.Add(new(TriageLevel.Note, S, rel, ManalyzeNote(r)));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Write($"binaries: manalyze on {rel} failed: {e}");
                findings.Add(new(TriageLevel.Note, S, rel, $"Manalyze couldn't be run: {e.Message}"));
            }
        }
        return findings;
    }

    static string LevelText(int level) => level switch
    {
        <= 1 => "information",
        2 => "level 2 of 3",
        _ => "level 3 of 3 (Manalyze's highest)",
    };

    public sealed record ManalyzeRow(string Plugin, int Level, string Summary, string Details);

    static readonly Regex MalwareWord = new(@"\bmalware\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Manalyze's text with "malware" said as "binary" (user's decision, 2026-11-19): the report never calls
    /// the analyzed file malware, even when quoting a tool.</summary>
    public static string PlainManalyze(string text) => MalwareWord.Replace(text, m => char.IsUpper(m.Value[0]) ? "Binary" : "binary");

    /// <summary>The NOTE for one Manalyze result. Reworded text is never in quotation marks as if it were Manalyze's own
    /// words, and the line says that it was reworded.</summary>
    public static string ManalyzeNote(ManalyzeRow r)
    {
        var summary = PlainManalyze(r.Summary);
        var details = PlainManalyze(r.Details);
        var reworded = summary != r.Summary || details != r.Details;
        return $"Manalyze's {r.Plugin} plugin ({LevelText(r.Level)}) reports: {summary}" + (details != "" ? $" ({details})" : "") +
               (reworded ? " [Manalyze's wording changed: \"binary\" where it says \"malware\"]" : "");
    }

    /// <summary>Manalyze's JSON ({ "&lt;file&gt;": { "Summary": {...}, "Plugins": { name: { level, summary, plugin_output } } } })
    /// as rows; null when it isn't that shape.</summary>
    public static List<ManalyzeRow>? ParseManalyze(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var file in doc.RootElement.EnumerateObject())
            {
                if (file.Value.ValueKind != JsonValueKind.Object) continue;
                var rows = new List<ManalyzeRow>();
                if (!file.Value.TryGetProperty("Plugins", out var pl) || pl.ValueKind != JsonValueKind.Object) return rows;
                foreach (var p in pl.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Object) continue;
                    int level = p.Value.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var li) ? li : 0;
                    var summary = p.Value.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
                    var details = p.Value.TryGetProperty("plugin_output", out var o) ? Flatten(o) : "";
                    if (details.Length > 400) details = details[..400] + "...";
                    rows.Add(new ManalyzeRow(p.Name, level, summary, details));
                }
                return rows;
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    static string Flatten(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => string.Join("; ", e.EnumerateObject().Select(p => $"{p.Name}: {Flatten(p.Value)}")),
        JsonValueKind.Array => string.Join(", ", e.EnumerateArray().Select(Flatten)),
        JsonValueKind.String => e.GetString() ?? "",
        _ => e.ToString(),
    };

    const int MaxOutputChars = 4 * 1024 * 1024;

    static (int Code, string Out, string Err, bool TimedOut) RunProcess(string exe, string[] args, string cwd, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            CreateNoWindow = true, WorkingDirectory = cwd,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        var o = new StringBuilder();
        var er = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (o) { if (o.Length < MaxOutputChars) o.AppendLine(e.Data); } };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (er) { if (er.Length < 64 * 1024) er.AppendLine(e.Data); } };
        p.Start();
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return (-1, "", "", true);
        }
        p.WaitForExit();
        lock (o) lock (er) return (p.ExitCode, o.ToString(), er.ToString(), false);
    }
}
