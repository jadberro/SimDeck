namespace SimDeck.Core.Sources;

/// <summary>
/// What a source wants to say about itself.
///
/// Each source knows its own failure modes, so it explains them rather than
/// the UI type-testing and guessing. Line is the one-liner; Hint is what to
/// do about it, empty when there is nothing wrong.
/// </summary>
public readonly record struct SourceStatus(string Line, string Hint);
