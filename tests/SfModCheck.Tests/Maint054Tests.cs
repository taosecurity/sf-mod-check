// 0.5.4 maintenance (the to-do from the 0.5.3 reviews): one version number, and a/an for signatures read as letters.
// The report-cleanup tests (items 4 and 7) are in ConflictTests.cs, next to the other report-failure tests.
using SfModCheck.Conflict;
using Xunit;

namespace SfModCheck.Tests;

public class Maint054Tests
{
    [Theory]
    [InlineData("0.5.3+5a98c1a0123456789abcdef0123456789abcdef", "0.5.3")]
    [InlineData("0.6.0-beta.1+abc", "0.6.0-beta.1")]
    [InlineData("0.5.3", "0.5.3")]
    public void Informational_version_loses_the_commit(string informational, string expected) =>
        Assert.Equal(expected, ProgramVersion.Clean(informational));

    [Fact]
    public void Current_version_is_the_assemblys_without_a_commit()
    {
        // against the informational version itself, so a prerelease <Version> (0.6.0-beta.1) passes too (review)
        var info = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(ProgramVersion).Assembly)!.InformationalVersion;
        Assert.Equal(info.Split('+')[0], ProgramVersion.Current);
        Assert.DoesNotContain("+", ProgramVersion.Current);
    }

    // Spelled out letter by letter, first letter's name starts with a vowel sound: "an"
    [Theory]
    [InlineData("FLST", "an FLST")]
    [InlineData("FFKW", "an FFKW")]
    [InlineData("HDPT", "an HDPT")]
    [InlineData("LVLI", "an LVLI")]
    [InlineData("LVLN", "an LVLN")]
    [InlineData("LVLP", "an LVLP")]
    [InlineData("MGEF", "an MGEF")]
    [InlineData("MSTT", "an MSTT")]
    [InlineData("NPC_", "an NPC_")]
    [InlineData("RFGP", "an RFGP")]
    [InlineData("SNDR", "an SNDR")]
    [InlineData("SMQN", "an SMQN")]
    // Said as words: the first letter decides, as before
    [InlineData("MISC", "a MISC")]
    [InlineData("NAVM", "a NAVM")]
    [InlineData("SCEN", "a SCEN")]
    [InlineData("FURN", "a FURN")]
    [InlineData("HAZD", "a HAZD")]
    [InlineData("LIGH", "a LIGH")]
    [InlineData("LCTN", "a LCTN")]
    [InlineData("CELL", "a CELL")]
    [InlineData("ARMO", "an ARMO")]
    [InlineData("ACHR", "an ACHR")]
    [InlineData("OTFT", "an OTFT")]
    [InlineData("WEAP", "a WEAP")]
    // Spelled out, but the first letter's name starts with a consonant sound (or is a vowel): unchanged
    [InlineData("KYWD", "a KYWD")]
    [InlineData("GMST", "a GMST")]
    [InlineData("CSTY", "a CSTY")]
    [InlineData("ECZN", "an ECZN")]
    public void Signatures_get_a_or_an_by_how_they_are_read(string sig, string expected) =>
        Assert.Equal(expected, Impact.WithArticle(sig));

    // Review of 0.5.4: a hand-kept list drifts. Every Starfield signature that starts with a letter whose name starts
    // with a vowel sound (F, H, L, M, N, R, S, X) must be listed as spelled out or as said as a word, so a new one (from
    // a Mutagen update) fails here until someone decides.
    [Fact]
    public void Every_signature_that_could_take_an_is_classified()
    {
        var sigsOf = typeof(Checker).GetMethod("SigsOf", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var sigs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var c in typeof(Mutagen.Bethesda.Starfield.IStarfieldModGetter).Assembly.GetTypes()
                     .Where(t => t.IsClass && !t.IsAbstract && typeof(Mutagen.Bethesda.Plugins.Records.IMajorRecord).IsAssignableFrom(t)))
            if (c.GetProperty("StaticRegistration", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)?.GetValue(null) is Loqui.ILoquiRegistration reg
                && sigsOf.Invoke(null, new object[] { reg.GetterType }) is HashSet<string> s)
                sigs.UnionWith(s);
        Assert.True(sigs.Count > 100, $"only {sigs.Count} signatures found");
        var open = sigs.Where(s => "FHLMNRSX".Contains(s[0]) && !Impact.ReadAsLetters.Contains(s) && !Impact.SaidAsWords.Contains(s)).ToList();
        Assert.True(open.Count == 0, "not classified: " + string.Join(" ", open));
        // and nothing is listed twice
        Assert.Empty(Impact.ReadAsLetters.Intersect(Impact.SaidAsWords));
    }
}
