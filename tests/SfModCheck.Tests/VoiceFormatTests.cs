// sf-mod-check voice-format (0.23.0, step 3): made-up .wem headers in made-up archives and folders. 32 kHz and 48 kHz
// are counted and never flagged, a damaged header is counted as unreadable, a .wem with no .ffxanim is counted, lip
// files are found in the plugin's other archives and loose, and inputs that can't be read are exit 2. Each fails on
// 0.22.0, which has no voice-format command.
using System.Buffers.Binary;
using System.Text;
using SfModCheck;
using Xunit;
using static SfModCheck.Tests.Ba2CommandTests;

namespace SfModCheck.Tests;

public class VoiceFormatTests
{
    /// <summary>A made-up .wem: a RIFF (or RIFX) header with a JUNK chunk first, then a fmt chunk, then some data.</summary>
    static byte[] Wem(uint rate, ushort channels = 1, ushort tag = 0xFFFF, bool big = false)
    {
        var o = new MemoryStream();
        void U32(uint v) { var b = new byte[4]; if (big) BinaryPrimitives.WriteUInt32BigEndian(b, v); else BinaryPrimitives.WriteUInt32LittleEndian(b, v); o.Write(b); }
        void U16(ushort v) { var b = new byte[2]; if (big) BinaryPrimitives.WriteUInt16BigEndian(b, v); else BinaryPrimitives.WriteUInt16LittleEndian(b, v); o.Write(b); }
        o.Write(Encoding.ASCII.GetBytes(big ? "RIFX" : "RIFF")); U32(0); o.Write("WAVE"u8);
        o.Write("JUNK"u8); U32(3); o.Write(new byte[4]);   // odd size: padded to 4
        o.Write("fmt "u8); U32(24); U16(tag); U16(channels); U32(rate); U32(rate * 2); U16(2); U16(16); U32(0); U32(0);
        o.Write("data"u8); U32(64); o.Write(new byte[64]);
        return o.ToArray();
    }

    static readonly byte[] Lip = Encoding.ASCII.GetBytes("made-up lip animation");

    [Fact]
    public void ReadFormat_reads_riff_and_rifx_and_names_what_is_wrong_with_a_damaged_header()
    {
        Assert.Equal(new VoiceFormatCommand.WemFormat(0xFFFF, 1, 32000), VoiceFormatCommand.ReadFormat(Wem(32000), out _));
        Assert.Equal(new VoiceFormatCommand.WemFormat(0xFFFF, 2, 48000), VoiceFormatCommand.ReadFormat(Wem(48000, 2, big: true), out _));
        var pcm = VoiceFormatCommand.ReadFormat(Wem(44100, 1, 0x0001), out _)!.Value;
        Assert.Equal(("pcm", "mono"), (pcm.Codec, pcm.ChannelText));
        Assert.Null(VoiceFormatCommand.ReadFormat(Encoding.ASCII.GetBytes("OggS not a wem at all"), out var why));
        Assert.Contains("not a RIFF file", why);
        Assert.Null(VoiceFormatCommand.ReadFormat(Wem(32000)[..36], out why));                        // cut inside the fmt chunk
        Assert.Contains("fmt chunk is too short", why);
        var noFmt = Wem(32000); noFmt[24] = (byte)'X';                                                  // "fmt " becomes "Xmt "
        Assert.Null(VoiceFormatCommand.ReadFormat(noFmt, out why));
        Assert.Contains("no fmt chunk", why);
        Assert.Null(VoiceFormatCommand.ReadFormat(Wem(0), out why));
        Assert.Contains("sample rate of 0", why);
        Assert.Null(VoiceFormatCommand.ReadFormat(new byte[5], out why));
    }

    sealed class Box : Sandbox
    {
        public string Voices => Path.Combine(Dir, "Made - Voices_en.ba2");
        public string Main => Path.Combine(Dir, "Made - Main.ba2");
        public string Plugin => Path.Combine(Dir, "Made.esm");
        public Box()
        {
            File.WriteAllBytes(Voices, Gnrl(
                (@"sound\voice\made.esm\madevoicea\0000a001.wem", Wem(32000), Pack.Stored),
                (@"sound\voice\made.esm\madevoicea\0000a001.ffxanim", Lip, Pack.Stored),
                (@"sound\voice\made.esm\madevoicea\0000a002.wem", Wem(32000), Pack.Stored),     // its lip is in Main
                (@"sound\voice\made.esm\madevoicea\0000a003.wem", Wem(48000), Pack.Zlib),       // no lip file
                (@"sound\voice\made.esm\madevoicea\0000a004.wem", Encoding.ASCII.GetBytes("damaged header"), Pack.Stored),
                (@"sound\voice\made.esm\madevoicea\0000a004.ffxanim", Lip, Pack.Stored)));
            File.WriteAllBytes(Main, Gnrl(
                (@"sound\voice\made.esm\madevoicea\0000a002.ffxanim", Lip, Pack.Stored),
                (@"scripts\made\quest.pex", Lip, Pack.Stored)));
            File.WriteAllBytes(Path.Combine(Dir, "Other - Voices_en.ba2"), Gnrl(
                (@"sound\voice\other.esm\othervoice\0000b001.wem", Wem(44100), Pack.Stored)));
            File.WriteAllBytes(Plugin, [0]);   // never read
            var loose = Sub(@"Sound\Voice\Made.esm\MadeVoiceB");
            File.WriteAllBytes(Path.Combine(loose, "0000c001.wem"), Wem(44100, 2));
            File.WriteAllBytes(Path.Combine(loose, "0000c001.ffxanim"), Lip);
        }
        public (int Code, string Stdout, string Stderr) Run(params string[] args) => ExeTests.RunSplit(["voice-format", .. args]);
    }

    [Fact]
    public void An_archive_counts_each_format_finds_lip_files_in_its_plugins_archives_and_flags_nothing()
    {
        using var b = new Box();
        var (code, stdout, stderr) = b.Run(b.Voices, "--verbose");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("Made - Voices_en.ba2 (archive): 4 voice files (.wem)", stdout);
        Assert.Matches(@"wwise-vorbis\s+mono\s+32,000 Hz\s+2\s", stdout);
        Assert.Matches(@"wwise-vorbis\s+mono\s+48,000 Hz\s+1\s", stdout);                // the zlib entry is unpacked
        Assert.Contains("lip files (.ffxanim): 3 in an archive, 0 loose, 1 with none (first: sound\\voice\\made.esm\\madevoicea\\0000a003.wem", stdout);
        Assert.Contains("1 stored compressed", stdout);
        Assert.Contains("unreadable: 1 (first: sound\\voice\\made.esm\\madevoicea\\0000a004.wem: it is not a RIFF file)", stdout);
        Assert.DoesNotContain("44,100", stdout);                                          // Other's archive isn't read
        Assert.DoesNotContain("WARNING", stdout + stderr);
        Assert.DoesNotContain("ERROR", stdout + stderr);
        Assert.Contains("RESULT: REPORT (4 voice files in 1 input, 2 formats, 1 with no lip file, 1 unreadable)", stdout);
        Assert.Contains("log: voice-format: Made - Voices_en.ba2: 6 files, 4 voice .wem", stderr);
    }

    [Fact]
    public void A_plugin_reads_its_own_archives_and_its_loose_voice_folder_only()
    {
        using var b = new Box();
        var (code, stdout, stderr) = b.Run(b.Plugin);
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("Made.esm (plugin, 2 archives): 5 voice files (.wem)", stdout);
        Assert.Matches(@"wwise-vorbis\s+stereo\s+44,100 Hz\s+1\s+e\.g\. sound\\voice\\made\.esm\\madevoiceb\\0000c001\.wem", stdout);
        Assert.Contains("lip files (.ffxanim): 3 in an archive, 1 loose, 1 with none", stdout);
        Assert.DoesNotContain("other.esm", stdout);
        Assert.Contains("RESULT: REPORT (5 voice files in 1 input, 3 formats", stdout);
    }

    [Fact]
    public void A_folder_reads_its_loose_voice_files()
    {
        using var b = new Box();
        var (code, stdout, _) = b.Run(b.Dir);
        Assert.Equal(0, code);
        Assert.Contains("(folder): 1 voice file (.wem)", stdout);
        Assert.Contains("lip files (.ffxanim): 0 in an archive, 1 loose, 0 with none", stdout);
    }

    [Fact]
    public void No_input_a_missing_input_or_only_a_damaged_archive_is_exit_2()
    {
        using var b = new Box();
        Assert.Equal(2, b.Run().Code);
        var missing = b.Run(Path.Combine(b.Dir, "Nothere.esm"));
        Assert.Equal(2, missing.Code);
        Assert.Contains("RESULT: CANNOT CHECK (there is no file or folder", missing.Stdout);
        var bad = Path.Combine(b.Dir, "Broken - Voices_en.ba2");
        File.WriteAllBytes(bad, Encoding.ASCII.GetBytes("not an archive at all, just text"));
        var (code, stdout, _) = b.Run(bad);
        Assert.Equal(2, code);
        Assert.Contains("not read: Broken - Voices_en.ba2: Broken - Voices_en.ba2 is not a BA2 archive", stdout);
        Assert.Contains("RESULT: CANNOT CHECK (no input could be read", stdout);
        Assert.Equal(2, b.Run(b.Plugin, "--bogus").Code);
        Assert.Equal(0, b.Run("--help").Code);
    }
}
