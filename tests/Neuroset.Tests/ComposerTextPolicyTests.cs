using FluentAssertions;
using Neuroset.Application;

namespace Neuroset.Tests;

public sealed class ComposerTextPolicyTests
{
    [Fact]
    public void A_Small_Paste_Stays_In_The_Editor()
    {
        ComposerTextPolicy.KeepOutOfEditor("hello", 0).Should().BeFalse();
        ComposerTextPolicy.KeepOutOfEditor(string.Empty, 0).Should().BeFalse();
        ComposerTextPolicy.KeepOutOfEditor(null, 0).Should().BeFalse();
    }

    [Fact]
    public void A_Huge_Paste_Is_Kept_Out_Of_The_Editor()
    {
        ComposerTextPolicy.KeepOutOfEditor(new string('x', ComposerTextPolicy.MaximumInlinePasteCharacters + 1), 0).Should().BeTrue();
    }

    [Fact]
    public void Many_Medium_Pastes_Stop_Filling_The_Editor()
    {
        var paste = new string('x', 1_000);

        ComposerTextPolicy.KeepOutOfEditor(paste, ComposerTextPolicy.MaximumComposerCharacters - 500).Should().BeTrue();
        ComposerTextPolicy.KeepOutOfEditor(paste, 100).Should().BeFalse();
    }

    [Fact]
    public void Composition_Sends_Every_Block_Verbatim()
    {
        var blocks = new[] { PastedTextBlock.Create("first block\n", "a"), PastedTextBlock.Create("second block", "b") };

        ComposerTextPolicy.Compose("  Look at this  ", blocks).Should().Be("Look at this\n\nfirst block\n\nsecond block");
        ComposerTextPolicy.Compose("   ", blocks).Should().Be("first block\n\nsecond block");
        ComposerTextPolicy.Compose("only typed", null).Should().Be("only typed");
        ComposerTextPolicy.Compose("   ", []).Should().BeEmpty();
    }

    [Fact]
    public void Sending_Requires_Typed_Text_Or_A_Pasted_Block()
    {
        ComposerTextPolicy.HasContent("   ", []).Should().BeFalse();
        ComposerTextPolicy.HasContent("   ", [PastedTextBlock.Create("payload", "a")]).Should().BeTrue();
        ComposerTextPolicy.HasContent("hi", []).Should().BeTrue();
    }

    [Fact]
    public void Only_An_Oversized_Message_Is_Clipped_For_Display()
    {
        var small = new string('x', 100);
        ComposerTextPolicy.NeedsClipping(small).Should().BeFalse();
        ComposerTextPolicy.ClipForDisplay(small).Should().Be(small);

        var huge = new string('x', ComposerTextPolicy.MaximumRenderedMessageCharacters * 2);
        ComposerTextPolicy.NeedsClipping(huge).Should().BeTrue();
        ComposerTextPolicy.ClipForDisplay(huge).Length.Should().Be(ComposerTextPolicy.MaximumRenderedMessageCharacters);
    }

    [Fact]
    public void Clipping_Prefers_A_Nearby_Line_Break()
    {
        var text = new string('x', ComposerTextPolicy.MaximumRenderedMessageCharacters - 500) + "\n" + new string('y', 5_000);

        var clipped = ComposerTextPolicy.ClipForDisplay(text);

        clipped.Should().NotEndWith("y");
        clipped.Length.Should().Be(ComposerTextPolicy.MaximumRenderedMessageCharacters - 500);
    }
}
