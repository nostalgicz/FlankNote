namespace FlankNote;

/// <summary>Small, explicit bilingual layer for the application's UI strings.</summary>
static class Loc
{
    public static bool Chinese => Settings.Language == "zh";
    public static string T(string english, string chinese) => Chinese ? chinese : english;

    public static string ColourName(string english) => !Chinese ? english : english switch
    {
        "Lemon" => "黄",
        "Peach" => "橙",
        "Rose" => "粉",
        "Lilac" => "紫",
        "Sky" => "蓝",
        "Mint" => "绿",
        "Sand" => "棕",
        "Slate" => "灰",
        "White" => "白",
        _ => english,
    };
}
