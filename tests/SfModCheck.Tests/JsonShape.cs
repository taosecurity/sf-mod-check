// sf-mod-check 0.26.0: checks that a JSON object holds exactly the keys a design names, each with the right kind, so a
// renamed, dropped or added key in overrides' or summary's --json fails a test (plan step 5). 0.27.0 uses it for cell,
// find --where and ba2 find.
using System.Text.Json;
using Xunit;

namespace SfModCheck.Tests;

static class JsonShape
{
    /// <summary>The kinds a key may have: a nullable string or number is "string?" or "number?".</summary>
    static bool Fits(JsonValueKind kind, string want) => want switch
    {
        "string" => kind == JsonValueKind.String,
        "string?" => kind is JsonValueKind.String or JsonValueKind.Null,
        "number" => kind == JsonValueKind.Number,
        "bool" => kind is JsonValueKind.True or JsonValueKind.False,
        "array" => kind == JsonValueKind.Array,
        "array?" => kind is JsonValueKind.Array or JsonValueKind.Null,
        "object" => kind == JsonValueKind.Object,
        "object?" => kind is JsonValueKind.Object or JsonValueKind.Null,
        _ => throw new ArgumentException("unknown kind " + want),
    };

    /// <summary>Asserts that o has exactly these keys (name:kind), no more and no fewer, and returns o.</summary>
    public static JsonElement Exactly(JsonElement o, string where, params string[] keys)
    {
        Assert.Equal(JsonValueKind.Object, o.ValueKind);
        var want = keys.Select(k => k.Split(':')).ToDictionary(p => p[0], p => p[1]);
        var have = o.EnumerateObject().Select(p => p.Name).ToList();
        Assert.True(want.Keys.Order().SequenceEqual(have.Order()),
            $"{where}: keys {string.Join(", ", have.Order())}; the design names {string.Join(", ", want.Keys.Order())}");
        foreach (var (name, kind) in want)
            Assert.True(Fits(o.GetProperty(name).ValueKind, kind), $"{where}.{name} is {o.GetProperty(name).ValueKind}, not {kind}");
        return o;
    }

    /// <summary>Asserts that every item of the array is of the given kind.</summary>
    public static void AllOf(JsonElement array, string kind, string where)
    {
        Assert.Equal(JsonValueKind.Array, array.ValueKind);
        foreach (var e in array.EnumerateArray()) Assert.True(Fits(e.ValueKind, kind), $"{where}: an item is {e.ValueKind}, not {kind}");
    }
}
