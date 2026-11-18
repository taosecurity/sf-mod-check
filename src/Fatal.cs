// The one rule on which errors stop a run (0.5.2). Both commands catch read errors in many places so that one bad record
// or field is skipped and listed instead of stopping everything. Some errors must never be skipped that way: carrying on
// would leave a report or a result that looks whole but isn't. Every catch that `check` and `conflict` reach asks
// Recoverable() first and lets the others through to the command's top handler, which prints CANNOT CHECK.
using SfModCheck.Conflict;

namespace SfModCheck;

public static class Fatal
{
    /// <summary>True for an error that must stop the run: conflict's --max-memory limit (MemoryLimitException), running
    /// out of memory (OutOfMemoryException and its InsufficientMemoryException), and InsufficientExecutionStackException.
    /// The last is thrown when a thread's stack is nearly used up: the C# compiler puts that check in every record type's
    /// ToString, and Mutagen, System.Text.Json and System.Linq.Expressions call it too, so it can happen here and
    /// carrying on after it is unsafe. Wrapped errors count: the inner exceptions are followed (reflection wraps an error
    /// from a property getter in TargetInvocationException), and so is every inner exception of an AggregateException,
    /// not only the first.</summary>
    public static bool IsFatal(Exception? e) => IsFatal(e, 0);

    static bool IsFatal(Exception? e, int depth)
    {
        // depth: a guard against an exception chain that loops back on itself; real chains are a few levels deep
        for (var x = e; x is not null && depth < 64; x = x.InnerException, depth++)
        {
            if (x is MemoryLimitException or OutOfMemoryException or InsufficientExecutionStackException) return true;
            if (x is AggregateException ag)
                foreach (var inner in ag.InnerExceptions)
                    if (IsFatal(inner, depth + 1)) return true;
        }
        return false;
    }

    /// <summary>A failure the run can carry on past (skip the record, field or group and list it): every error that
    /// isn't fatal. Used as `catch (Exception e) when (Fatal.Recoverable(e))`.</summary>
    public static bool Recoverable(Exception e) => !IsFatal(e);

    /// <summary>The top handlers' RESULT line. After a fatal error the memory the run held is released first (the
    /// objects are no longer reachable once the error has left the check), so the message itself can be built; if
    /// building it still fails, a fixed text is used. Never throws.</summary>
    public static string ResultLine(Exception e, Func<string> make)
    {
        if (IsFatal(e)) { try { GC.Collect(); GC.WaitForPendingFinalizers(); } catch (Exception) { /* best effort */ } }
        try { return make(); }
        catch (Exception)
        {
            // fixed texts: building any other string may fail the same way
            return IsFatal(e) ? "RESULT: CANNOT CHECK (out of memory)" : "RESULT: CANNOT CHECK (an error that could not be described)";
        }
    }

    /// <summary>Runs the extra messages after a RESULT line (stderr note, log). Any error while writing them (out of
    /// memory, a closed stderr pipe) is dropped, so the exit code stays as decided and the RESULT line already printed
    /// stays the only one (code review 0.5.2).</summary>
    public static void AfterResult(Action write)
    {
        try { write(); }
        catch (Exception) { /* the RESULT line has been printed; nothing may change it now */ }
    }
}
