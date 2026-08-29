namespace MeetingInterpreter.Models;

public static class LanguageHelper
{
    public static SupportedLanguage ParseLanguage(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return SupportedLanguage.Unknown;
        }

        var normalized = languageCode.Trim().ToLowerInvariant();
        if (normalized is "vi" or "vi-vn")
        {
            return SupportedLanguage.Vietnamese;
        }

        if (normalized is "ko" or "ko-kr")
        {
            return SupportedLanguage.Korean;
        }

        return SupportedLanguage.Unknown;
    }

    public static SupportedLanguage GetTargetLanguage(SupportedLanguage sourceLanguage)
        => sourceLanguage switch
        {
            SupportedLanguage.Vietnamese => SupportedLanguage.Korean,
            SupportedLanguage.Korean => SupportedLanguage.Vietnamese,
            _ => SupportedLanguage.Unknown
        };

    public static string ToGoogleSpeechCode(SupportedLanguage language)
        => language switch
        {
            SupportedLanguage.Vietnamese => "vi-VN",
            SupportedLanguage.Korean => "ko-KR",
            _ => string.Empty
        };

    public static string ToGoogleTranslateCode(SupportedLanguage language)
        => language switch
        {
            SupportedLanguage.Vietnamese => "vi",
            SupportedLanguage.Korean => "ko",
            _ => string.Empty
        };

    public static string ToShortCode(SupportedLanguage language)
        => language switch
        {
            SupportedLanguage.Vietnamese => "VI",
            SupportedLanguage.Korean => "KO",
            _ => "?"
        };

    public static IReadOnlyList<SupportedLanguage> GetSelectableLanguages()
        => new[]
        {
            SupportedLanguage.Vietnamese,
            SupportedLanguage.Korean
        };
}
