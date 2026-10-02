namespace Neuroset.Application;

/// <summary>
/// A chunk of pasted text kept outside the editor. The WinUI TextBox lays out its whole content on
/// the UI thread, so a very large paste freezes the window before any of our code runs; the text is
/// stored here instead, shown as a chip, and joined back into the message verbatim on send.
/// </summary>
public sealed record PastedTextBlock(Guid Id, string Text, string Label)
{
    public static PastedTextBlock Create(string text, string label) => new(Guid.NewGuid(), text, label);
}

/// <summary>Size limits that keep the composer and the message list responsive.</summary>
public static class ComposerTextPolicy
{
    /// <summary>A paste this small is cheap to lay out, so it goes straight into the editor.</summary>
    public const int MaximumInlinePasteCharacters = 6_000;

    /// <summary>Total editor content the UI thread can re-measure per keystroke without stuttering.</summary>
    public const int MaximumComposerCharacters = 20_000;

    /// <summary>Rendered message length: Markdown inlines are built on the UI thread.</summary>
    public const int MaximumRenderedMessageCharacters = 40_000;

    public static bool KeepOutOfEditor(string? pastedText, int currentComposerLength)
    {
        if (string.IsNullOrEmpty(pastedText)) return false;
        return pastedText.Length > MaximumInlinePasteCharacters
            || currentComposerLength + pastedText.Length > MaximumComposerCharacters;
    }

    /// <summary>Joins the typed text and every pasted block verbatim: the model must see all of it.</summary>
    public static string Compose(string? composerText, IEnumerable<PastedTextBlock>? blocks)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(composerText)) parts.Add(composerText!.Trim());
        foreach (var block in blocks ?? [])
            if (!string.IsNullOrEmpty(block.Text)) parts.Add(block.Text.Trim('\r', '\n'));
        return string.Join("\n\n", parts);
    }

    public static bool HasContent(string? composerText, IEnumerable<PastedTextBlock>? blocks) =>
        !string.IsNullOrWhiteSpace(composerText) || (blocks?.Any(block => !string.IsNullOrEmpty(block.Text)) ?? false);

    public static bool NeedsClipping(string? text) => (text?.Length ?? 0) > MaximumRenderedMessageCharacters;

    /// <summary>Clips at a line break when one is close, so the visible text does not end mid-word.</summary>
    public static string ClipForDisplay(string? text)
    {
        if (!NeedsClipping(text)) return text ?? string.Empty;
        var clipped = text![..MaximumRenderedMessageCharacters];
        var lastBreak = clipped.LastIndexOf('\n');
        return lastBreak > MaximumRenderedMessageCharacters - 2_000 ? clipped[..lastBreak] : clipped;
    }
}
