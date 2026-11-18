// 0.5.2 item 2: one rule (Fatal.cs) on which errors stop a run. An ordinary read error is skipped and listed as before;
// running out of memory (or stack), or conflict's --max-memory limit, is never swallowed by a helper's catch, so the run
// ends with CANNOT CHECK instead of a report that looks whole.
using Loqui;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Plugins.Binary.Translations;
using Mutagen.Bethesda.Starfield;
using Noggog;
using Noggog.StructuredStrings;
using SfModCheck.Conflict;
using Xunit;
using Impact = SfModCheck.Conflict.Impact;

namespace SfModCheck.Tests;

public class FatalTests
{
    static string NoName(Mutagen.Bethesda.Plugins.IFormLinkGetter l) => l.FormKey.ToString();

    [Fact]
    public void Ordinary_errors_are_recoverable_and_memory_and_stack_errors_are_not()
    {
        Assert.True(Fatal.Recoverable(new InvalidOperationException("damaged field")));
        Assert.True(Fatal.Recoverable(new AggregateException(new IOException("a"), new FormatException("b"))));
        Assert.False(Fatal.Recoverable(new OutOfMemoryException()));
        Assert.False(Fatal.Recoverable(new InsufficientMemoryException()));
        Assert.False(Fatal.Recoverable(new System.Reflection.TargetInvocationException(new OutOfMemoryException())));
        Assert.False(Fatal.Recoverable(new MemoryLimitException("memory passed 8.0 GB")));
        Assert.False(Fatal.Recoverable(new InsufficientExecutionStackException()));
    }

    // 0.5.1 followed InnerException only, which for an AggregateException is just its first error.
    [Fact]
    public void An_out_of_memory_error_second_in_an_AggregateException_is_fatal()
    {
        Assert.True(Fatal.IsFatal(new AggregateException(new IOException("first"), new OutOfMemoryException())));
        Assert.True(Fatal.IsFatal(new InvalidOperationException("outer",
            new AggregateException(new FormatException("x"), new AggregateException(new IOException("y"), new InsufficientExecutionStackException())))));
        Assert.False(Fatal.IsFatal(null));
    }

    [Fact]
    public void The_result_line_after_a_fatal_error_falls_back_to_a_fixed_text()
    {
        Assert.Equal("RESULT: CANNOT CHECK (x)", Fatal.ResultLine(new OutOfMemoryException(), () => "RESULT: CANNOT CHECK (x)"));
        Assert.Equal("RESULT: CANNOT CHECK (out of memory)", Fatal.ResultLine(new OutOfMemoryException(), () => throw new OutOfMemoryException()));
        Assert.Equal("RESULT: CANNOT CHECK (an error that could not be described)", Fatal.ResultLine(new IOException(), () => throw new FormatException()));
    }

    // Code review 0.5.2: after the RESULT line nothing may escape (a closed stderr pipe would print a second RESULT).
    [Fact]
    public void Nothing_escapes_the_writes_after_the_result_line()
    {
        Fatal.AfterResult(() => throw new IOException("pipe closed"));
        Fatal.AfterResult(() => throw new OutOfMemoryException());
    }

    // The helpers conflict and check call used to catch everything (0.5.1 review round 6). Each must still turn an
    // ordinary error into "unreadable" text, and let a fatal one through.
    [Fact]
    public void Land_flags_text_lets_out_of_memory_through()
    {
        Assert.Equal("unreadable", Overrides.LandFlagsText(new ThrowingGrid(() => new InvalidOperationException("damaged XCLC"))));
        Assert.Throws<OutOfMemoryException>(() => Overrides.LandFlagsText(new ThrowingGrid(() => new OutOfMemoryException())));
    }

    [Fact]
    public void Impact_lets_out_of_memory_through_when_it_compares_land_flags()
    {
        var oom = () => (Exception)new OutOfMemoryException();
        Assert.ThrowsAny<Exception>(() => Impact.Differences("Grid", new ThrowingGrid(oom, 1), new ThrowingGrid(oom, 2), NoName));
        // an ordinary error is still counted and skipped
        int n = 0;
        var bad = () => (Exception)new InvalidOperationException("damaged");
        Impact.Differences("Grid", new ThrowingGrid(bad, 1), new ThrowingGrid(bad, 2), NoName, () => n++);
        Assert.Equal(1, n);
    }

    [Fact]
    public void Impact_lets_out_of_memory_through_when_it_reads_a_property()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Impact.Differences("Thing", new Boom(true), new Boom(true), NoName));
        Assert.True(Fatal.IsFatal(ex));
        var ex2 = Assert.ThrowsAny<Exception>(() => Impact.Describe(new Boom(true), NoName));
        Assert.True(Fatal.IsFatal(ex2));
        // an entry named by its Name property (ItemName)
        var ex3 = Assert.ThrowsAny<Exception>(() => Impact.Differences("Items", new List<Named>(), new List<Named> { new(true) }, NoName));
        Assert.True(Fatal.IsFatal(ex3));
        // ordinary errors: still described, not thrown
        Impact.Describe(new Boom(false), NoName);
        Impact.Differences("Items", new List<Named>(), new List<Named> { new(false) }, NoName);
    }

    [Fact]
    public void Field_hash_lets_out_of_memory_through()
    {
        Assert.Equal(2, FieldCompare.DeepHash(new Boom(false), 0));   // an ordinary error: the "couldn't hash" value, as before
        var ex = Assert.ThrowsAny<Exception>(() => FieldCompare.DeepHash(new Boom(true), 0));
        Assert.True(Fatal.IsFatal(ex));
    }

    sealed class Boom(bool fatal)
    {
        public int Value => throw (fatal ? new OutOfMemoryException() : new InvalidOperationException("damaged"));
    }

    sealed class Named(bool fatal)
    {
        public string Name => throw (fatal ? new OutOfMemoryException() : new InvalidOperationException("damaged"));
    }

    /// <summary>A cell grid whose Flags throw the given error.</summary>
    sealed class ThrowingGrid(Func<Exception> error, int x = 1) : ICellGridGetter
    {
        public P2Int Point { get; } = new(x, 2);
        public CellGrid.Flag Flags => throw error();
        ILoquiRegistration ILoquiObject.Registration => throw new NotSupportedException();
        object ICellGridGetter.CommonInstance() => throw new NotSupportedException();
        object? ICellGridGetter.CommonSetterInstance() => throw new NotSupportedException();
        object ICellGridGetter.CommonSetterTranslationInstance() => throw new NotSupportedException();
        object IBinaryItem.BinaryWriteTranslator => throw new NotSupportedException();
        void IBinaryItem.WriteToBinary(MutagenWriter writer, TypedWriteParams translationParams) => throw new NotSupportedException();
        void IPrintable.Print(StructuredStringBuilder sb, string? name) => throw new NotSupportedException();
    }
}
