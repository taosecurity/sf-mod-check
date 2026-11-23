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
public sealed record PexDebugPropertyGroup(string Object, string Group, string Doc, uint UserFlags, List<string> Properties);
/// <summary>A struct's members in the order the source declares them.</summary>
public sealed record PexDebugStructOrder(string Object, string Struct, List<string> Members);

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
    public List<PexDebugPropertyGroup> DebugPropertyGroups { get; } = new();
    public List<PexDebugStructOrder> DebugStructOrders { get; } = new();
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
                    string o = SRef(), g = SRef(), d = SRef();   // property group: object, name, doc, user flags
                    var flags = U32();
                    var names = new List<string>();
                    for (int k = 0, m = U16(); k < m; k++) names.Add(SRef());
                    pex.DebugPropertyGroups.Add(new PexDebugPropertyGroup(o, g, d, flags, names));
                }
                for (int i = 0, n = U16(); i < n; i++)
                {
                    string o = SRef(), s = SRef();               // struct order: object, struct
                    var names = new List<string>();
                    for (int k = 0, m = U16(); k < m; k++) names.Add(SRef());
                    pex.DebugStructOrders.Add(new PexDebugStructOrder(o, s, names));
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

    // ---- the listing (0.15.0: release --tested) ----

    /// <summary>The script as plain lines that two builds of the same source share (a C# port of sf-pex-tools'
    /// pex_compare.py listing, also MIT). String-table numbers are already their text; user flags, variables, guards,
    /// properties, states, the functions in a state, locals, struct members and debug entries are sorted, because the
    /// compiler writes them in a build-dependent order; temporaries (::tempN) are renamed ::t1, ::t2 ... in the order
    /// each function's code first uses them. Left out: the compile time, the source file's time, the source file's
    /// folder, the user and the machine. Kept as written: instruction order, parameters, objects and struct order.
    /// Two scripts are equivalent when their listings are equal. With ignoreDebug the debug entries are left out.</summary>
    public static List<string> Listing(PexFile pex, bool ignoreDebug = false)
    {
        var src = pex.SourceFile.Replace('\\', '/');
        var o = new List<string> { $"pex {pex.Version} game {pex.GameId}", $"source {src[(src.LastIndexOf('/') + 1)..]}" };
        o.AddRange(Sorted(pex.UserFlags.Select(u => $"userflag {u.Name} = bit {u.Bit}")));
        foreach (var obj in pex.Objects)
        {
            o.Add($"object {obj.Name} extends {Q(obj.Parent)}");
            o.Add($"  doc {Q(obj.Doc)}  const {(obj.Const ? 1 : 0)}  userflags 0x{obj.UserFlags:x}  autostate {Q(obj.AutoState)}");
            var structs = obj.Structs.Select(s => new List<string> { $"  struct {s.Name}" }
                .Concat(Sorted(s.Members.Select(m => $"    member {m.Name} : {m.Type}  flags 0x{m.Flags:x} = {V(m.Value)}  const {(m.Const ? 1 : 0)}  doc {Q(m.Doc)}"))).ToList());
            foreach (var b in SortedBlocks(structs)) o.AddRange(b);
            o.AddRange(Sorted(obj.Variables.Select(v => $"  variable {v.Name} : {v.Type}  flags 0x{v.UserFlags:x} = {V(v.Value)}  const {(v.Const ? 1 : 0)}")));
            o.AddRange(Sorted(obj.Guards.Select(g => $"  guard {g}")));
            var props = obj.Properties.Select(p =>
            {
                var b = new List<string> { $"  property {p.Name} : {p.Type}  doc {Q(p.Doc)}  userflags 0x{p.UserFlags:x}  flags 0x{p.Flags:x}" };
                if (p.AutoVar != null) b.Add($"    autovar {p.AutoVar}");
                if (p.Get != null) { b.Add("    get"); b.AddRange(FunctionLines(p.Get, "      ")); }
                if (p.Set != null) { b.Add("    set"); b.AddRange(FunctionLines(p.Set, "      ")); }
                return b;
            });
            var states = obj.States.Select(s => new List<string> { $"  state {Q(s.Name)}" }
                .Concat(SortedBlocks(s.Functions.Select(f => new List<string> { $"    function {f.Name}" }.Concat(FunctionLines(f, "      ")).ToList()))
                    .SelectMany(b => b)).ToList());
            foreach (var b in SortedBlocks(props).Concat(SortedBlocks(states))) o.AddRange(b);
        }
        if (!ignoreDebug && pex.HasDebug)
        {
            var debug = pex.DebugFunctions.Select(d => $"debug function {d.Object}.{d.State}.{d.Function} type {d.Type} lines [{string.Join(", ", d.Lines)}]")
                .Concat(pex.DebugPropertyGroups.Select(g => $"debug propertygroup {g.Object}.{Q(g.Group)} doc {Q(g.Doc)} flags 0x{g.UserFlags:x} [{string.Join(", ", Sorted(g.Properties))}]"))
                .Concat(pex.DebugStructOrders.Select(s => $"debug structorder {s.Object}.{s.Struct} [{string.Join(", ", s.Members)}]"));
            o.AddRange(Sorted(debug));
        }
        return o;
    }

    static List<string> FunctionLines(PexFunction f, string indent)
    {
        var lines = new List<string> { $"{indent}returns {f.ReturnType}  doc {Q(f.Doc)}  userflags 0x{f.UserFlags:x}  flags 0x{f.Flags:x}" };
        lines.AddRange(f.Params.Select(p => $"{indent}param {p.Name} : {p.Type}"));
        var rename = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var i in f.Code)
            foreach (var a in i.Args.Concat(i.Extra))
                if (a.IsIdent && a.Text.StartsWith("::temp", StringComparison.Ordinal) && !rename.ContainsKey(a.Text))
                    rename[a.Text] = $"::t{rename.Count + 1}";
        foreach (var l in f.Locals.OrderBy(l => l.Type, StringComparer.Ordinal).ThenBy(l => l.Name, StringComparer.Ordinal))   // temps the code never uses
            if (l.Name.StartsWith("::temp", StringComparison.Ordinal) && !rename.ContainsKey(l.Name))
                rename[l.Name] = $"::t{rename.Count + 1}";
        string R(string n) => rename.TryGetValue(n, out var r) ? r : n;
        lines.AddRange(Sorted(f.Locals.Select(l => $"{indent}local {R(l.Name)} : {l.Type}")));
        for (int k = 0; k < f.Code.Count; k++)
        {
            var i = f.Code[k];
            // a variadic opcode's argument count is implied by the arguments listed
            var args = i.Args.Concat(i.Extra).Select(a => a.IsIdent ? "ident:" + R(a.Text) : V(a));
            lines.Add($"{indent}{k,4} {i.Name} {string.Join(", ", args)}");
        }
        return lines;
    }

    static string V(PexValue v) => v.Kind switch
    {
        PexValueKind.None => "None",
        PexValueKind.Ident => "ident:" + v.Text,
        PexValueKind.Str => "str:" + Q(v.Text),
        PexValueKind.Int => "int:" + v.Int.ToString(System.Globalization.CultureInfo.InvariantCulture),
        // the float's bits, so two values that print alike are never taken as equal
        PexValueKind.Float => $"float:{v.Float.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} (0x{BitConverter.SingleToUInt32Bits(v.Float):x8})",
        _ => "bool:" + (v.Int != 0 ? "True" : "False"),
    };

    /// <summary>A string in quotes with control characters escaped, so a line break in a doc string can't fake a line.</summary>
    static string Q(string s)
    {
        var b = new StringBuilder("'");
        foreach (var c in s)
            b.Append(c switch { '\\' => "\\\\", '\'' => "\\'", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ when c < 0x20 => $"\\x{(int)c:x2}", _ => c.ToString() });
        return b.Append('\'').ToString();
    }

    static IEnumerable<string> Sorted(IEnumerable<string> lines) => lines.OrderBy(l => l, StringComparer.Ordinal);

    /// <summary>Blocks of lines in a fixed order that depends only on their content.</summary>
    static IEnumerable<List<string>> SortedBlocks(IEnumerable<List<string>> blocks) =>
        blocks.OrderBy(b => string.Join("\n", b), StringComparer.Ordinal);

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
