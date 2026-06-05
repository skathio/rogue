using System.Threading.Tasks;
using Xunit;

namespace SkathIO.Rogue.Tests;

public sealed class UnitTypeTests
{
    [Fact]
    public void Unit_Equals_Unit() => Assert.Equal(Unit.Value, Unit.Value);

    [Fact]
    public void Unit_Equals_Via_Object() => Assert.True(Unit.Value.Equals((object)Unit.Value));

    [Fact]
    public void Unit_GetHashCode_IsZero() => Assert.Equal(0, Unit.Value.GetHashCode());

    [Fact]
    public void Unit_ToString_IsParens() => Assert.Equal("()", Unit.Value.ToString());

    [Fact]
    public void Unit_EqualityOperator_IsTrue() => Assert.True(Unit.Value == default(Unit));

    [Fact]
    public void Unit_InequalityOperator_IsFalse() => Assert.False(Unit.Value != default(Unit));

    [Fact]
    public async Task Unit_Task_IsCompleted()
    {
        var result = await Unit.Task;
        Assert.Equal(Unit.Value, result);
    }
}
