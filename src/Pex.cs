// A reader for compiled Papyrus scripts (.pex), for the triage command's Scripts section (0.8.0, chunk C3). It reads
// the bytecode as stored: the header (with the game id and the PEX version), the string table, the debug info, the
// user flags and each object with its parent, structs, variables, guards, properties, states and functions, down to
// every instruction and its arguments. It never decompiles and never writes source: the triage report lists names and
// calls only.
//
// The layout is the one sf-pex-tools' pex_compare.py reads (Fallout 4 PEX 3.9 and Starfield PEX 3.12, little endian),
// and tools\pex_parity.py checks that both give the same call lists on real scripts. Every read is bounds-checked: a
// damaged or crafted file gives an InvalidDataException naming the byte offset, never a hang, a huge allocation or a
// crash. Counts are 16-bit except an instruction's argument count, which is checked against the bytes left.
using System.Buffers.Binary;
using System.Text;

namespace SfModCheck;

public enum PexValueKind : byte { None = 0, Ident = 1, Str = 2, Int = 3, Float = 4, Bool = 5 }

/// <summary>One argument or constant: an identifier (a variable, a type or a function name), a string, a number or a
/// bool. Text holds the identifier or string, Int the integer or bool (0/1), Float the float.</summary>
public readonly record struct PexValue(PexValueKind Kind, string Text, int Int, float Float)
{
    public bool IsIdent => Kind == PexValueKind.Ident;
    public bool IsStr => Kind == PexValueKind.Str;

    public override string ToString() => Kind switch
    {
        PexValueKind.None => "None",
        PexValueKind.Ident => Text,
        PexValueKind.Str => "\"" + Text + "\"",
        PexValueKind.Int => Int.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PexValueKind.Float => Float.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => Int != 0 ? "True" : "False",
    };
}

/// <summary>One instruction: the opcode, its name (as sf-pex-tools names it), the fixed arguments, and for the
/// variadic opcodes (calls, guards) the extra arguments after the count.</summary>
public sealed record PexInstruction(byte Op, string Name, List<PexValue> Args, List<PexValue> Extra);

public sealed record PexVariable(string Name, string Type);

public sealed class PexFunction
{
    /// <summary>The function's name; for a property's accessor "get" or "set".</summary>
    public required string Name { get; init; }
    /// <summary>The state it is in ("" = the empty state); for a property accessor, the property's name.</summary>
    public required string State { get; init; }
    public bool IsPropertyAccessor { get; init; }
    public required string ReturnType { get; init; }
    public required string Doc { get; init; }
    public uint UserFlags { get; init; }
    public byte Flags { get; init; }
    public bool IsGlobal => (Flags & 1) != 0;
    public bool IsNative => (Flags & 2) != 0;
    public List<PexVariable> Params { get; } = new();
    public List<PexVariable> Locals { get; } = new();
    public List<PexInstruction> Code { get; } = new();
}

public sealed record PexStructMember(string Name, string Type, uint Flags, PexValue Value, bool Const, string Doc);
public sealed record PexStruct(string Name, List<PexStructMember> Members);
public sealed record PexObjectVariable(string Name, string Type, uint UserFlags, PexValue Value, bool Const);

public sealed class PexProperty
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string Doc { get; init; }
    public uint UserFlags { get; init; }
    public byte Flags { get; init; }
    /// <summary>The variable behind an auto property; null otherwise.</summary>
    public string? AutoVar { get; init; }
    public PexFunction? Get { get; set; }
    public PexFunction? Set { get; set; }
}

public sealed record PexState(string Name, List<PexFunction> Functions);

public sealed class PexObject
{
    public required string Name { get; init; }
    /// <summary>The script it extends ("" when none).</summary>
    public required string Parent { get; init; }
    public required string Doc { get; init; }
    public bool Const { get; init; }
    public uint UserFlags { get; init; }
    public required string AutoState { get; init; }
    public List<PexStruct> Structs { get; } = new();
    public List<PexObjectVariable> Variables { get; } = new();
    public List<string> Guards { get; } = new();
    public List<PexProperty> Properties { get; } = new();
    public List<PexState> States { get; } = new();

    /// <summary>Every function with code or a declaration: those in states, then the property accessors.</summary>
    public IEnumerable<PexFunction> Functions() =>
        States.SelectMany(s => s.Functions).Concat(Properties.SelectMany(p => new[] { p.Get, p.Set }).OfType<PexFunction>());
}

public sealed record PexDebugFunction(string Object, string State, string Function, byte Type, List<ushort> Lines);

public sealed class PexFile
{
    public byte Major { get; init; }
    public byte Minor { get; init; }
    /// <summary>The game id in the header (1 Skyrim, 2 Fallout 4, 3 Fallout 76, 4 Starfield).</summary>
    public ushort GameId { get; init; }
    public ulong CompileTime { get; init; }
    public required string SourceFile { get; init; }
    public required string User { get; init; }
    public required string Machine { get; init; }
    public required List<string> Strings { get; init; }
    public bool HasDebug { get; set; }
    public List<PexDebugFunction> DebugFunctions { get; } = new();
    public List<(string Name, byte Bit)> UserFlags { get; } = new();
    public List<PexObject> Objects { get; } = new();

    public bool IsStarfield => Major == 3 && Minor == 12;
    public string Version => $"{Major}.{Minor}";
}

public static class Pex
{
    public const uint Magic = 0xFA57C0DE;
    /// <summary>The largest script this reader takes. The game's largest scripts are well under 1 MB.</summary>
    public const int MaxBytes = 64 * 1024 * 1024;

    /// <summary>opcode: (name, fixed argument count, variadic). A variadic opcode is followed by an int count and that
    /// many more arguments. The same table as sf-pex-tools; 0x2F-0x32 are Starfield's (PEX 3.12) additions.</summary>
    public static readonly (string Name, int Fixed, bool Variadic)[] Opcodes =
    [
        ("nop", 0, false), ("iadd", 3, false), ("fadd", 3, false), ("isub", 3, false), ("fsub", 3, false),
        ("imul", 3, false), ("fmul", 3, false), ("idiv", 3, false), ("fdiv", 3, false), ("imod", 3, false),
        ("not", 2, false), ("ineg", 2, false), ("fneg", 2, false), ("assign", 2, false), ("cast", 2, false),
        ("cmp_eq", 3, false), ("cmp_lt", 3, false), ("cmp_le", 3, false), ("cmp_gt", 3, false), ("cmp_ge", 3, false),
        ("jmp", 1, false), ("jmpt", 2, false), ("jmpf", 2, false), ("callmethod", 3, true), ("callparent", 2, true),
        ("callstatic", 3, true), ("return", 1, false), ("strcat", 3, false), ("propget", 3, false), ("propset", 3, false),
        ("array_create", 2, false), ("array_length", 2, false), ("array_getelement", 3, false),
        ("array_setelement", 3, false), ("array_findelement", 4, false), ("array_rfindelement", 4, false),
        ("is", 3, false), ("struct_create", 1, false), ("struct_get", 3, false), ("struct_set", 3, false),
        ("array_findstruct", 5, false), ("array_rfindstruct", 5, false), ("array_add", 3, false),
        ("array_insert", 3, false), ("array_removelast", 1, false), ("array_remove", 3, false), ("array_clear", 1, false),
        ("array_getallmatchingstructs", 6, false), ("lock_guards", 0, true), ("unlock_guards", 0, true),
        ("try_lock_guards", 1, true),
    ];

    public const byte CallMethod = 0x17, CallParent = 0x18, CallStatic = 0x19;

    public static PexFile ReadFile(string path)
    {
        var len = new FileInfo(path).Length;
        if (len > MaxBytes) throw new InvalidDataException($"{Path.GetFileName(path)} is {len:N0} bytes, larger than any compiled script ({MaxBytes / (1024 * 1024)} MB at most)");
        return Read(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    /// <summary>Reads one script's bytes. Throws InvalidDataException, naming the script and the byte offset, when the
    /// bytes aren't a whole PEX 3.9 or 3.12 file.</summary>
    public static PexFile Read(byte[] data, string name)
    {
        if (data.Length > MaxBytes) throw new InvalidDataException($"{name} is {data.Length:N0} bytes, larger than any compiled script");
        return new Reader(data, name).File();
    }

    sealed class Reader(byte[] b, string name)
    {
        int p;
        List<string> strings = new();

        InvalidDataException Bad(string what, int at) => new($"{name}: {what} at byte {at}");

        void Need(int n)
        {
            if (n < 0 || p > b.Length - n)
                throw Bad($"the file ends early (wanted {n} more bytes; damaged or cut off)", p);
        }

        byte U8() { Need(1); return b[p++]; }
        ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p)); p += 2; return v; }
        uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4; return v; }
        int S32() { Need(4); var v = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)); p += 4; return v; }
        ulong U64() { Need(8); var v = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p)); p += 8; return v; }
        float F32() { Need(4); var v = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(p)); p += 4; return v; }

        string WStr()
        {
            int n = U16();
            Need(n);
            var s = Encoding.UTF8.GetString(b, p, n);
            p += n;
            return s;
        }

        string SRef()
        {
            int i = U16();
            if (i >= strings.Count) throw Bad($"string number {i} is past the end of the string table ({strings.Count} strings)", p - 2);
            return strings[i];
        }

        PexValue Value()
        {
            int at = p;
            var t = U8();
            return t switch
            {
                0 => new PexValue(PexValueKind.None, "", 0, 0),
                1 => new PexValue(PexValueKind.Ident, SRef(), 0, 0),
                2 => new PexValue(PexValueKind.Str, SRef(), 0, 0),
                3 => new PexValue(PexValueKind.Int, "", S32(), 0),
                4 => new PexValue(PexValueKind.Float, "", 0, F32()),
                5 => new PexValue(PexValueKind.Bool, "", U8() != 0 ? 1 : 0, 0),
                _ => throw Bad($"unknown value type {t}", at),
            };
        }

        PexFunction Function(string fname, string state, bool accessor)
        {
            var f = new PexFunction
            {
                Name = fname, State = state, IsPropertyAccessor = accessor,
                ReturnType = SRef(), Doc = SRef(), UserFlags = U32(), Flags = U8(),
            };
            for (int i = 0, n = U16(); i < n; i++) f.Params.Add(new PexVariable(SRef(), SRef()));
            for (int i = 0, n = U16(); i < n; i++) f.Locals.Add(new PexVariable(SRef(), SRef()));
            for (int i = 0, n = U16(); i < n; i++)
            {
                int at = p;
                var op = U8();
                if (op >= Opcodes.Length) throw Bad($"unknown opcode 0x{op:x2}", at);
                var (opName, fixedArgs, variadic) = Opcodes[op];
                var args = new List<PexValue>(fixedArgs);
                for (int k = 0; k < fixedArgs; k++) args.Add(Value());
                var extra = new List<PexValue>();
                if (variadic)
                {
                    int cat = p;
                    var count = Value();
                    if (count.Kind != PexValueKind.Int) throw Bad($"the argument count of {opName} is a {count.Kind}, not an integer", cat);
                    // every value takes at least one byte, so a count larger than the bytes left is damage
                    if (count.Int < 0 || count.Int > b.Length - p) throw Bad($"the argument count {count.Int} of {opName} doesn't fit in the file", cat);
                    for (int k = 0; k < count.Int; k++) extra.Add(Value());
                }
                f.Code.Add(new PexInstruction(op, opName, args, extra));
            }
            return f;
        }

        public PexFile File()
        {
            var magic = U32();
            if (magic == 0xDEC057FA) throw Bad("this is a Skyrim (big-endian) script; only Fallout 4 and Starfield scripts are read", 0);
            if (magic != Magic) throw Bad("not a compiled Papyrus script (no PEX signature)", 0);
            byte major = U8(), minor = U8();
            if (!((major == 3 && minor == 9) || (major == 3 && minor == 12)))
                throw Bad($"PEX version {major}.{minor} isn't read (3.12 is Starfield's, 3.9 Fallout 4's)", 4);
            bool starfield = minor >= 12;
            var game = U16();
            var compiled = U64();
            string source = WStr(), user = WStr(), machine = WStr();
            int count = U16();
            strings = new List<string>(count);
            for (int i = 0; i < count; i++) strings.Add(WStr());
            var pex = new PexFile
            {
                Major = major, Minor = minor, GameId = game, CompileTime = compiled,
                SourceFile = source, User = user, Machine = machine, Strings = strings,
            };

            pex.HasDebug = U8() != 0;
            if (pex.HasDebug)
            {
                U64();  // the source file's modification time
                for (int i = 0, n = U16(); i < n; i++)
                {
                    string o = SRef(), s = SRef(), fn = SRef();
                    var type = U8();
                    var lines = new List<ushort>();
                    for (int k = 0, m = U16(); k < m; k++) lines.Add(U16());
                    pex.DebugFunctions.Add(new PexDebugFunction(o, s, fn, type, lines));
                }
                for (int i = 0, n = U16(); i < n; i++)
                {
                    SRef(); SRef(); SRef(); U32();   // property group: object, name, doc, user flags
                    for (int k = 0, m = U16(); k < m; k++) SRef();
                }
                for (int i = 0, n = U16(); i < n; i++)
                {
                    SRef(); SRef();                  // struct order: object, struct
                    for (int k = 0, m = U16(); k < m; k++) SRef();
                }
            }

            for (int i = 0, n = U16(); i < n; i++) pex.UserFlags.Add((SRef(), U8()));

            for (int i = 0, n = U16(); i < n; i++)
            {
                int objAt = p;
                var oname = SRef();
                var size = U32();
                int start = p;
                var o = new PexObject
                {
                    Name = oname, Parent = SRef(), Doc = SRef(), Const = U8() != 0, UserFlags = U32(), AutoState = SRef(),
                };
                for (int k = 0, m = U16(); k < m; k++)
                {
                    var st = new PexStruct(SRef(), new());
                    for (int j = 0, q = U16(); j < q; j++)
                        st.Members.Add(new PexStructMember(SRef(), SRef(), U32(), Value(), U8() != 0, SRef()));
                    o.Structs.Add(st);
                }
                for (int k = 0, m = U16(); k < m; k++)
                    o.Variables.Add(new PexObjectVariable(SRef(), SRef(), U32(), Value(), U8() != 0));
                if (starfield)
                    for (int k = 0, m = U16(); k < m; k++) o.Guards.Add(SRef());
                for (int k = 0, m = U16(); k < m; k++)
                {
                    string pn = SRef(), pt = SRef(), pd = SRef();
                    uint puf = U32();
                    byte pfl = U8();
                    var prop = new PexProperty { Name = pn, Type = pt, Doc = pd, UserFlags = puf, Flags = pfl, AutoVar = (pfl & 4) != 0 ? SRef() : null };
                    if ((pfl & 4) == 0)
                    {
                        if ((pfl & 1) != 0) prop.Get = Function("get", pn, true);
                        if ((pfl & 2) != 0) prop.Set = Function("set", pn, true);
                    }
                    o.Properties.Add(prop);
                }
                for (int k = 0, m = U16(); k < m; k++)
                {
                    var state = new PexState(SRef(), new());
                    for (int j = 0, q = U16(); j < q; j++) state.Functions.Add(Function(SRef(), state.Name, false));
                    o.States.Add(state);
                }
                // the size field counts itself in some compilers' output and not in others'
                long read = p - start;
                if (read != (long)size - 4 && read != size)
                    throw Bad($"object {oname} took {read} bytes but its size field says {size}", objAt);
                pex.Objects.Add(o);
            }
            if (p != b.Length) throw Bad($"{b.Length - p} unread bytes at the end (a layout this reader doesn't know)", p);
            return pex;
        }
    }

    // ---- calls ----

    /// <summary>One call in a script's code. Kind is "method", "static" or "parent". Type is the called script or type
    /// (for a method call the declared type of the variable it is called on; "?" when it can't be told), Name the
    /// function. Args are the arguments after the result variable.</summary>
    public sealed record Call(PexObject Object, PexFunction Caller, string Kind, string Type, string Name, string Self, List<PexValue> Args);

    /// <summary>Every call in every function of the file, in code order. A method call's type comes from the variable
    /// it is called on: self (the object), a parameter, a local, or an object variable (auto properties are variables);
    /// a parent call's type is the object's parent.</summary>
    public static IEnumerable<Call> Calls(PexFile pex)
    {
        foreach (var o in pex.Objects)
        {
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in o.Variables) vars.TryAdd(v.Name, v.Type);
            foreach (var f in o.Functions())
            {
                var scope = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in f.Params) scope.TryAdd(v.Name, v.Type);
                foreach (var v in f.Locals) scope.TryAdd(v.Name, v.Type);
                foreach (var i in f.Code)
                {
                    switch (i.Op)
                    {
                        case CallMethod:
                        {
                            var self = i.Args[1].Text;
                            string type = string.Equals(self, "self", StringComparison.OrdinalIgnoreCase) ? o.Name
                                : scope.TryGetValue(self, out var t) ? t : vars.TryGetValue(self, out var t2) ? t2 : "?";
                            yield return new Call(o, f, "method", type, i.Args[0].Text, self, i.Extra);
                            break;
                        }
                        case CallStatic:
                            yield return new Call(o, f, "static", i.Args[0].Text, i.Args[1].Text, "", i.Extra);
                            break;
                        case CallParent:
                            yield return new Call(o, f, "parent", o.Parent == "" ? "?" : o.Parent, i.Args[0].Text, "parent", i.Extra);
                            break;
                    }
                }
            }
        }
    }

    /// <summary>Every string constant in the file: in code, variable defaults and struct member defaults.</summary>
    public static IEnumerable<(PexObject Object, PexFunction? Function, string Text)> StringConstants(PexFile pex)
    {
        foreach (var o in pex.Objects)
        {
            foreach (var v in o.Variables) if (v.Value.IsStr) yield return (o, null, v.Value.Text);
            foreach (var s in o.Structs) foreach (var m in s.Members) if (m.Value.IsStr) yield return (o, null, m.Value.Text);
            foreach (var f in o.Functions())
                foreach (var i in f.Code)
                    foreach (var a in i.Args.Concat(i.Extra))
                        if (a.IsStr) yield return (o, f, a.Text);
        }
    }
}
