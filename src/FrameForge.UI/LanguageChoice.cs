using FrameForge.Core;

namespace FrameForge.UI;

internal sealed record LanguageChoice(AppLanguage Value, string Label)
{
    public static IReadOnlyList<LanguageChoice> All { get; } =
    [
        new(AppLanguage.English, "English"),
        new(AppLanguage.German, "Deutsch"),
        new(AppLanguage.Russian, "Русский")
    ];
}
