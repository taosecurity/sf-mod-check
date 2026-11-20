// The .pex reader (0.8.0, chunk C3). Every script here is made up and built byte by byte by PexBytes below: no game
// scripts, no decompiled code. The layout is the one sf-pex-tools' pex_compare.py reads.
using System.Text;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

/// <summary>A value as written in a .pex: its type byte and payload.</summary>
public sealed record PV(byte Type, object? Val)
{
    public static readonly PV None = new(0, null);
    public static PV Id(string s) => new(1, s);
    public static PV Str(string s) => new(2, s);
    public static PV Int(int i) => new(3, i);
    public static PV Flt(float f) => new(4, f);
    public static PV Bool(bool b) => new(5, b);
}

public sealed record PIns(byte Op, PV[] Args, PV[]? Extra = null)
{
    public static PIns CallMethod(string fn, string self, string dest, params PV[] args) => new(0x17, [PV.Id(fn), PV.Id(self), PV.Id(dest)], args);
    public static PIns CallStatic(string type, string fn, string dest, params PV[] args) => new(0x19, [PV.Id(type), PV.Id(fn), PV.Id(dest)], args);
    public static PIns CallParent(string fn, string dest, params PV[] args) => new(0x18, [PV.Id(fn), PV.Id(dest)], args);
    public static PIns Assign(string dest, PV v) => new(0x0D, [PV.Id(dest), v]);
    public static PIns Return(PV v) => new(0x1A, [v]);
}

public sealed record PFn(string Name, string Ret = "None", (string Name, string Type)[]? Params = null,
                         (string Name, string Type)[]? Locals = null, PIns[]? Code = null, byte Flags = 0);

public sealed record PProp(string Name, string Type, string? AutoVar = null, PFn? Get = null, PFn? Set = null);

public sealed record PObj(string Name, string Parent = "", (string Name, string Type, PV Value)[]? Vars = null,
                          PProp[]? Props = null, (string State, PFn[] Fns)[]? States = null);

/// <summary>Builds a .pex file from objects. Strings go into the table in first-use order.</summary>
public static class PexBytes
{
    public static byte[] Build(PObj[] objects, byte major = 3, byte minor = 12, ushort game = 4, bool debug = true)
    {
        var strings = new List<string>();
        var index = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var body = new MemoryStream();
        var w = new BinaryWriter(body);
        void S(string s)
        {
            if (!index.TryGetValue(s, out var i)) { i = (ushort)strings.Count; strings.Add(s); index[s] = i; }
            w.Write(i);
        }
        void V(PV v)
        {
            w.Write(v.Type);
            switch (v.Type)
            {
                case 1: case 2: S((string)v.Val!); break;
                case 3: w.Write((int)v.Val!); break;
                case 4: w.Write((float)v.Val!); break;
                case 5: w.Write((byte)((bool)v.Val! ? 1 : 0)); break;
            }
        }
        void Fn(PFn f)
        {
            S(f.Ret); S(""); w.Write(0u); w.Write(f.Flags);
            var ps = f.Params ?? []; w.Write((ushort)ps.Length); foreach (var (n, t) in ps) { S(n); S(t); }
            var ls = f.Locals ?? []; w.Write((ushort)ls.Length); foreach (var (n, t) in ls) { S(n); S(t); }
            var code = f.Code ?? []; w.Write((ushort)code.Length);
            foreach (var i in code)
            {
                w.Write(i.Op);
                foreach (var a in i.Args) V(a);
                if (i.Extra != null) { V(PV.Int(i.Extra.Length)); foreach (var a in i.Extra) V(a); }
            }
        }

        w.Write((byte)(debug ? 1 : 0));
        if (debug)
        {
            w.Write(0UL);
            var fns = objects.SelectMany(o => (o.States ?? []).SelectMany(s => s.Fns.Select(f => (o.Name, s.State, f.Name)))).ToList();
            w.Write((ushort)fns.Count);
            foreach (var (o, s, f) in fns) { S(o); S(s); S(f); w.Write((byte)0); w.Write((ushort)1); w.Write((ushort)7); }
            w.Write((ushort)0); w.Write((ushort)0);
        }
        w.Write((ushort)1); S("hidden"); w.Write((byte)0);   // one user flag
        w.Write((ushort)objects.Length);
        foreach (var o in objects)
        {
            S(o.Name);
            var sizeAt = body.Position;
            w.Write(0u);
            var start = body.Position;
            S(o.Parent); S(""); w.Write((byte)0); w.Write(0u); S("");
            w.Write((ushort)0);   // structs
            var vars = o.Vars ?? []; w.Write((ushort)vars.Length);
            foreach (var (n, t, v) in vars) { S(n); S(t); w.Write(0u); V(v); w.Write((byte)0); }
            if (minor >= 12) w.Write((ushort)0);   // guards
            var props = o.Props ?? []; w.Write((ushort)props.Length);
            foreach (var p in props)
            {
                S(p.Name); S(p.Type); S(""); w.Write(0u);
                byte fl = (byte)(p.AutoVar != null ? 7 : (p.Get != null ? 1 : 0) | (p.Set != null ? 2 : 0));
                w.Write(fl);
                if (p.AutoVar != null) S(p.AutoVar);
                else { if (p.Get != null) Fn(p.Get); if (p.Set != null) Fn(p.Set); }
            }
            var states = o.States ?? []; w.Write((ushort)states.Length);
            foreach (var (state, fns) in states)
            {
                S(state); w.Write((ushort)fns.Length);
                foreach (var f in fns) { S(f.Name); Fn(f); }
            }
            var end = body.Position;
            body.Position = sizeAt; w.Write((uint)(end - start + 4)); body.Position = end;
        }
        w.Flush();

        var file = new MemoryStream();
        var h = new BinaryWriter(file);
        h.Write(0xFA57C0DEu); h.Write(major); h.Write(minor); h.Write(game); h.Write(0x12345678UL);
        void WStr(string s) { var b = Encoding.UTF8.GetBytes(s); h.Write((ushort)b.Length); h.Write(b); }
        WStr("Made.psc"); WStr("tester"); WStr("testbox");
        h.Write((ushort)strings.Count); foreach (var s in strings) WStr(s);
        h.Write(body.ToArray());
        h.Flush();
        return file.ToArray();
    }

    /// <summary>A small script with every part the reader knows: a variable, an auto property, a property with a get,
    /// two states, a local, a parameter, and method, static and parent calls.</summary>
    public static byte[] Sample(byte minor = 12) => Build(
    [
        new PObj("MyMod:Quest", "Quest",
            Vars: [("::Target_var", "ObjectReference", PV.None), ("Count", "Int", PV.Int(3)), ("Name", "String", PV.Str("Hello"))],
            Props: [new PProp("Target", "ObjectReference", AutoVar: "::Target_var"),
                    new PProp("Half", "Float", Get: new PFn("get", "Float", Code: [PIns.Return(PV.Flt(0.5f))]))],
            States:
            [
                ("", [
                    new PFn("OnInit", Locals: [("::temp0", "Actor"), ("::NoneVar", "None")], Code:
                    [
                        PIns.CallStatic("Game", "GetPlayer", "::temp0"),
                        PIns.CallMethod("MoveTo", "::temp0", "::NoneVar", PV.Id("::Target_var"), PV.Flt(0f)),
                        PIns.CallMethod("Helper", "self", "::NoneVar", PV.Int(1)),
                        PIns.CallStatic("Debug", "Trace", "::NoneVar", PV.Str("MyMod: started"), PV.Int(0)),
                        PIns.CallParent("OnInit", "::NoneVar"),
                    ]),
                    new PFn("Helper", Params: [("akRef", "ObjectReference")], Locals: [("::NoneVar", "None")], Code:
                    [
                        PIns.CallMethod("Disable", "akRef", "::NoneVar", PV.Bool(false)),
                        PIns.Assign("Count", PV.Int(4)),
                    ]),
                    new PFn("GetDistanceNative", "Float", Flags: 3),
                ]),
                ("Busy", [new PFn("OnInit", Code: [PIns.Return(PV.None)])]),
            ]),
    ], minor: minor);
}

public class PexTests
{
    [Fact]
    public void Reads_every_part_of_a_script()
    {
        var pex = Pex.Read(PexBytes.Sample(), "Quest.pex");
        Assert.Equal("3.12", pex.Version);
        Assert.True(pex.IsStarfield);
        Assert.Equal(4, pex.GameId);
        Assert.Equal("Made.psc", pex.SourceFile);
        Assert.Equal("tester", pex.User);
        Assert.True(pex.HasDebug);
        Assert.Equal(4, pex.DebugFunctions.Count);
        Assert.Equal([("hidden", (byte)0)], pex.UserFlags);
        var o = Assert.Single(pex.Objects);
        Assert.Equal("MyMod:Quest", o.Name);
        Assert.Equal("Quest", o.Parent);
        Assert.Equal(3, o.Variables.Count);
        Assert.Equal("Hello", o.Variables[2].Value.Text);
        Assert.Equal(2, o.Properties.Count);
        Assert.Equal("::Target_var", o.Properties[0].AutoVar);
        Assert.NotNull(o.Properties[1].Get);
        Assert.Equal(2, o.States.Count);
        Assert.Equal("Busy", o.States[1].Name);
        var init = o.States[0].Functions[0];
        Assert.Equal("OnInit", init.Name);
        Assert.Equal(5, init.Code.Count);
        Assert.Equal("callstatic", init.Code[0].Name);
        Assert.Equal(2, init.Code[1].Extra.Count);
        Assert.Equal(0f, init.Code[1].Extra[1].Float);
        var native = o.States[0].Functions[2];
        Assert.True(native.IsNative);
        Assert.True(native.IsGlobal);
        Assert.Equal(5, o.Functions().Count());   // 4 in states + the get
    }

    [Fact]
    public void Calls_name_the_called_type()
    {
        var calls = Pex.Calls(Pex.Read(PexBytes.Sample(), "Quest.pex")).ToList();
        Assert.Equal(
        [
            "static Game.GetPlayer", "method Actor.MoveTo", "method MyMod:Quest.Helper", "static Debug.Trace",
            "parent Quest.OnInit", "method ObjectReference.Disable",
        ], calls.Select(c => $"{c.Kind} {c.Type}.{c.Name}"));
        Assert.Equal("OnInit", calls[0].Caller.Name);
        Assert.Equal("MyMod: started", calls[3].Args[0].Text);
        Assert.Contains(Pex.StringConstants(Pex.Read(PexBytes.Sample(), "q")), s => s.Text == "Hello" && s.Function == null);
    }

    [Fact]
    public void An_object_variable_gives_the_type_and_an_unknown_one_gives_a_question_mark()
    {
        var bytes = PexBytes.Build([new PObj("A", "ObjectReference", Vars: [("Other", "Actor", PV.None)], States:
            [("", [new PFn("Go", Code: [PIns.CallMethod("Kill", "Other", "::NoneVar"), PIns.CallMethod("Foo", "Nowhere", "::NoneVar")])])])]);
        var calls = Pex.Calls(Pex.Read(bytes, "a.pex")).ToList();
        Assert.Equal("Actor", calls[0].Type);
        Assert.Equal("?", calls[1].Type);
    }

    [Fact]
    public void Fallout_4_scripts_are_read_too()
    {
        var pex = Pex.Read(PexBytes.Sample(minor: 9), "f4.pex");
        Assert.Equal("3.9", pex.Version);
        Assert.False(pex.IsStarfield);
        Assert.Single(pex.Objects);
    }

    static string Fails(byte[] b) => Assert.Throws<InvalidDataException>(() => Pex.Read(b, "x.pex")).Message;

    [Fact]
    public void Every_cut_off_file_fails_with_a_clear_error()
    {
        var b = PexBytes.Sample();
        for (int n = 0; n < b.Length; n++)
        {
            var msg = Fails(b[..n]);
            Assert.StartsWith("x.pex: ", msg);
            Assert.Contains(" at byte ", msg);
        }
    }

    [Fact]
    public void Damaged_headers_and_tables_name_the_problem()
    {
        var good = PexBytes.Sample();
        Assert.Contains("no PEX signature", Fails([1, 2, 3, 4, 5, 6, 7, 8]));
        Assert.Contains("Skyrim", Fails([0xFA, 0x57, 0xC0, 0xDE, 3, 2, 0, 1]));
        var v = (byte[])good.Clone(); v[5] = 2;
        Assert.Contains("PEX version 3.2", Fails(v));
        Assert.Contains("unread bytes at the end", Fails([.. good, 0]));

        var bad = PexBytes.Build([new PObj("A", States: [("", [new PFn("F", Code: [new PIns(0x40, [])])])])]);
        Assert.Contains("unknown opcode 0x40", Fails(bad));
        var huge = PexBytes.Build([new PObj("A", States: [("", [new PFn("F", Code: [new PIns(0x17, [PV.Id("f"), PV.Id("self"), PV.Id("x")], [])])])])]);
        // the call's count (an int value: type 3 then 4 bytes) is the last 5 bytes before the end of the code; make it huge
        int countAt = Find(huge, [3, 0, 0, 0, 0]);
        huge[countAt + 1] = 0xFF; huge[countAt + 2] = 0xFF; huge[countAt + 3] = 0xFF; huge[countAt + 4] = 0x7F;
        Assert.Contains("argument count", Fails(huge));
    }

    static int Find(byte[] hay, byte[] needle)
    {
        for (int i = hay.Length - needle.Length; i >= 0; i--)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        throw new InvalidOperationException("not found");
    }

    [Fact]
    public void A_string_number_past_the_table_is_refused()
    {
        // every string in the table is used, so dropping the last one leaves an index past the end of the table
        var b = PexBytes.Build([new PObj("A")]);
        var pex = Pex.Read(b, "x");
        Assert.True(pex.Strings.Count > 1);
        Assert.Contains("past the end of the string table", Fails(CutLastString(b, pex)));
    }

    /// <summary>The same file with the string table's count one lower and the last string's bytes removed.</summary>
    static byte[] CutLastString(byte[] b, PexFile pex)
    {
        int p = 4 + 2 + 2 + 8;
        for (int i = 0; i < 3; i++) p += 2 + BitConverter.ToUInt16(b, p);
        int countAt = p;
        p += 2;
        int lastAt = 0;
        for (int i = 0; i < pex.Strings.Count; i++) { lastAt = p; p += 2 + BitConverter.ToUInt16(b, p); }
        var list = b.ToList();
        list.RemoveRange(lastAt, p - lastAt);
        var o = list.ToArray();
        BitConverter.TryWriteBytes(o.AsSpan(countAt), (ushort)(pex.Strings.Count - 1));
        return o;
    }

    [Fact]
    public void A_wrong_object_size_is_refused()
    {
        var b = PexBytes.Build([new PObj("A")]);
        // an empty object's body is 21 bytes (parent, doc, const, user flags, auto state and five empty counts); its
        // size field counts itself too
        var pex = Pex.Read(b, "x");
        int sizeAt = Find(b, BitConverter.GetBytes(21u + 4));
        b[sizeAt] += 9;
        Assert.Contains("size field says", Fails(b));
        Assert.Single(pex.Objects);
    }

    [Fact]
    public void Random_damage_never_crashes_or_hangs()
    {
        var good = PexBytes.Sample();
        var rnd = new Random(1234);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 5000; i++)
        {
            var b = (byte[])good.Clone();
            for (int k = rnd.Next(1, 4); k > 0; k--) b[rnd.Next(16, b.Length)] = (byte)rnd.Next(256);
            try { Pex.Read(b, "x.pex"); }
            catch (InvalidDataException) { }
        }
        Assert.True(sw.Elapsed.TotalSeconds < 20, $"5000 damaged files took {sw.Elapsed.TotalSeconds:F1} s");
    }

    [Fact]
    public void A_file_larger_than_any_script_is_refused()
    {
        using var s = new Sandbox();
        var path = Path.Combine(s.Dir, "big.pex");
        using (var f = File.Create(path)) f.SetLength(Pex.MaxBytes + 1L);
        Assert.Contains("larger than any compiled script", Assert.Throws<InvalidDataException>(() => Pex.ReadFile(path)).Message);
    }
}
