namespace ScribeWench.CLI.Wizard;

// The Whisper models setup offers, most accurate first.
public static class WhisperCatalog
{
    public static readonly (string Key, long Bytes, string About)[] Models =
    [
        ("large", 3_095_033_483, "most accurate; needs a graphics card"),
        ("medium", 1_533_763_059, "a little less accurate, half the size"),
        ("small", 487_601_967, "fair; slow without a graphics card"),
        ("base", 147_951_465, "runs on any PC; gets names wrong more often"),
        ("tiny", 77_691_713, "fastest, least accurate"),
    ];

    // The size of the download.
    public static long Bytes(string model) => Models.FirstOrDefault(m => m.Key == model, Models[0]).Bytes;

    // The model that suits a graphics card with this much memory; 0 for none.
    public static string Recommended(double cardGb) =>
        cardGb >= 7.5 ? "large"
        : cardGb >= 3.5 ? "medium"
        : cardGb >= 1.5 ? "small"
        : "base";
}
