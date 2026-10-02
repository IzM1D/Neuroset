using FluentAssertions;
using Neuroset.Application;

namespace Neuroset.Tests;

public sealed class MarkdownMathNormalizerTests
{
    [Fact]
    public void Model_Style_Bracketed_Boxed_Formula_Is_Recognized_And_Formatted()
    {
        var prepared=MarkdownMathNormalizer.Prepare("[\n\\boxed{1,855,545,978,831,780}\n]");

        prepared.MathFragments.Should().ContainSingle();
        prepared.Markdown.Should().Contain("NSMATHFRAGMENT").And.NotContain("\\boxed");
        var fragment=prepared.MathFragments.Values.Single();
        fragment.IsDisplay.Should().BeTrue();
        MarkdownMathNormalizer.FormatLatex(fragment.Latex).Should().Be("⟦ 1,855,545,978,831,780 ⟧");
    }

    [Fact]
    public void Common_Latex_Commands_Are_Converted_To_Readable_Math()
    {
        MarkdownMathNormalizer.FormatLatex(@"\frac{x^2}{\sqrt{y_1}} \le \infty")
            .Should().Be("x²⁄√(y₁) ≤ ∞");
    }

    [Fact]
    public void Math_Inside_Code_Fences_Is_Not_Rewritten()
    {
        var prepared=MarkdownMathNormalizer.Prepare("~~~python\nprint(r'\\boxed{1}')\n~~~");

        prepared.MathFragments.Should().BeEmpty();
        prepared.Markdown.Should().Contain(@"\boxed{1}");
    }
}
