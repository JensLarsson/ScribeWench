namespace ScribeWench.AI.Backends;

// A local model setup offers to download through Ollama.
// MemoryGb: what it takes while running with the usual context window, graphics memory and RAM together.
// Tested: whether we have run it on a real session. Setup suggests only those, and offers the rest.
public sealed record LocalModel(string Name, double DownloadGb, double MemoryGb, string Verdict, bool Tested);

// The local models setup knows, best first, and which of them suits a machine. The list is from
// November 2026 and will age; any other model can be typed in by name.
public static class LocalModels
{
    // Holds a three-hour session with a game's notes in one request, which gave the cleanest notes in testing.
    public const int ContextSize = 65536;

    // Left alone for Windows, the bot, Whisper and whatever else is open.
    public const double HeadroomGb = 10;

    // On the 16 GB card we tested with, Ollama left about this much unused.
    public const double CardOverheadGb = 2.5;

    public static IReadOnlyList<LocalModel> All { get; } =
    [
        // The same model as the next one with less rounding of its weights (8 bits instead of 4).
        new("qwen3.8:27b-q8_0", 30, 35, "same model, higher precision", Tested: false),
        new("qwen3.8:27b", 18, 22, "best in our tests", Tested: true),
        new("gemma4:26b", 19, 18, "close second, twice as fast", Tested: true),
        new("gemma4:12b", 8, 10.5, "fast; likely to be inaccurate", Tested: true),
    ];

    public static LocalModel? Find(string name) =>
        All.FirstOrDefault(m => string.Equals(m.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    // Memory a model can have on a machine with this much RAM and graphics memory.
    public static double RoomGb(double ramGb, double cardGb) => Math.Max(0, ramGb + cardGb - HeadroomGb);

    public static bool Fits(LocalModel model, double ramGb, double cardGb) => model.MemoryGb <= RoomGb(ramGb, cardGb);

    // How much of the model sits on the graphics card, 0 to 1. Measured on a 15.8 GB card: 59% of
    // qwen3.8:27b and 77% of gemma4:26b, which this comes within a few points of.
    public static double CardShare(LocalModel model, double cardGb) => Math.Clamp((cardGb - CardOverheadGb) / model.MemoryGb, 0, 1);

    // The best tested model that fits, or null when the machine is too small for any of them.
    public static LocalModel? Recommend(double ramGb, double cardGb) =>
        All.FirstOrDefault(m => m.Tested && Fits(m, ramGb, cardGb));

    // Roughly how long a three-hour session takes. The first two come from our runs on a 16 GB
    // card; the rest are estimates from those.
    public static string Pace(LocalModel model, double cardGb)
    {
        var share = CardShare(model, cardGb);
        if (share >= 0.9) return "1-3 min";
        if (share >= 0.5) return "3-8 min";
        if (share > 0) return "15+ min";
        return model.MemoryGb <= 12
            ? "~30 min"
            : "1 h+";
    }
}
