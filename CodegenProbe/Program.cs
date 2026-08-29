using System.Runtime.CompilerServices;
using NickStrupat;

// Calls one cell operation per method, so that a disassembler asked for these names gets the operation
// with everything the JIT decided about it already applied. The methods on Atomic<T> are marked for
// inlining and so are never compiled under their own names; these wrappers are what can be looked at.
//
// Nothing here is measured or asserted. CodegenTests runs this with DOTNET_JitDisasm set and reads what
// comes out.
Probe.Run();

file static class Probe
{
    public static void Run()
    {
        var word = new Atomic<Int64>(1);
        var wide = new Atomic<Decimal>(1m);
        var reference = new Atomic<String>("x");

        // The disassembler is asked for optimised code with tiering off, so one round would do.
        // A few is cheap and leaves the loop obviously a loop.
        var sink = 0L;
        for (var i = 0; i < 1_000; i++)
        {
            sink += ProbeReadWord(word);
            sink += (Int64)ProbeReadWide(wide);
            sink += ProbeReadReference(reference).Length;
            ProbeWriteWord(word, i);
            sink += ProbeIncrementWord(word);
            sink += (Int64)ProbeIncrementWide(wide);
        }

        Console.WriteLine(sink);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Int64 ProbeReadWord(Atomic<Int64> cell) => cell.Read();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Decimal ProbeReadWide(Atomic<Decimal> cell) => cell.Read();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static String ProbeReadReference(Atomic<String> cell) => cell.Read();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ProbeWriteWord(Atomic<Int64> cell, Int64 value) => cell.Write(value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Int64 ProbeIncrementWord(Atomic<Int64> cell) => cell.Increment();

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Decimal ProbeIncrementWide(Atomic<Decimal> cell) => cell.Increment();
}
