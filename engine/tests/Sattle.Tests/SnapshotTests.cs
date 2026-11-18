using Sattle.Runtime;

namespace Sattle.Tests;

/// <summary>The stored form of a seal, for a field no definition reaches. That a seal restored into a fresh engine is the same state in every field and continues like the original is `SealRestoreTests` of the Differential, on every world (Proposition 11.10, premise S1); through a host and its stores it is `SealTests`.</summary>
public class SnapshotTests
{
    sealed class Pointers { public nint Signed; public nuint Unsigned; }

    /// <summary>Since .NET 11 the field of a native integer has its own type: the walk wrote it as a value type whose field is one, without end, and the seal overflowed the stack. A native integer is written as a number.</summary>
    [Theory]
    [InlineData(0L, 0UL)] [InlineData(-1L, 1UL)] [InlineData(long.MaxValue, ulong.MaxValue)]
    public void A_native_integer_in_the_state_is_saved_and_loaded_as_a_number(long signed, ulong unsigned)
    {
        var loaded = new Pointers();
        StateSnapshot.Load(StateSnapshot.Save(new Pointers { Signed = (nint)signed, Unsigned = (nuint)unsigned }), loaded);
        Assert.Equal(((nint)signed, (nuint)unsigned), (loaded.Signed, loaded.Unsigned));
    }
}
