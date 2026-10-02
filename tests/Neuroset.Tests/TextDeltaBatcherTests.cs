using FluentAssertions;
using Neuroset.Application;

namespace Neuroset.Tests;

public sealed class TextDeltaBatcherTests
{
    [Fact]
    public void Thousands_Of_Small_Deltas_Are_Combined_Into_One_Update()
    {
        var batcher = new TextDeltaBatcher(TimeSpan.FromHours(1));
        var expected = new string('x', 12_000);
        foreach (var character in expected) batcher.Append(character.ToString());

        batcher.TryDrain(out _, force: false).Should().BeFalse();
        batcher.TryDrain(out var combined, force: true).Should().BeTrue();
        combined.Should().Be(expected);
        batcher.HasPending.Should().BeFalse();
    }

    [Fact]
    public void Zero_Interval_Allows_Immediate_Flush()
    {
        var batcher = new TextDeltaBatcher(TimeSpan.Zero);
        batcher.Append(new string('a', 6_000));

        batcher.TryDrain(out var combined).Should().BeTrue();
        combined.Should().HaveLength(6_000);
    }
}
