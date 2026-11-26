
namespace ScribeWench.Tests;

public class LocalModelsTests
{
    [Theory]
    // RAM, graphics memory, what setup should suggest
    [InlineData(31.1, 15.8, "qwen3.8:27b")]        // the PC the models were tested on
    [InlineData(64, 24, "qwen3.8:27b")]            // the higher-precision build fits too, but we haven't run it: offered, not suggested
    [InlineData(64, 0, "qwen3.8:27b")]
    [InlineData(32, 0, "qwen3.8:27b")]
    [InlineData(32, 8, "qwen3.8:27b")]
    [InlineData(16, 12, "gemma4:26b")]             // a common gaming PC
    [InlineData(16, 8, "gemma4:12b")]
    [InlineData(24, 0, "gemma4:12b")]
    public void Recommend_PicksTheBestTestedModelThatFitsTheMachine(double ramGb, double cardGb, string expected)
    {
        Assert.Equal(expected, LocalModels.Recommend(ramGb, cardGb)?.Name);
    }

    [Theory]
    [InlineData(16, 0)]     // a laptop: the smallest usable model would leave Windows too little
    [InlineData(8, 4)]
    public void Recommend_IsNothing_WhenNoModelThatWritesUsableNotesFits(double ramGb, double cardGb)
    {
        Assert.Null(LocalModels.Recommend(ramGb, cardGb));
    }

    [Fact]
    public void Fits_CountsRamAndGraphicsMemoryTogether_LessWhatWindowsNeeds()
    {
        var largest = LocalModels.Find("qwen3.8:27b-q8_0")!;

        Assert.True(LocalModels.Fits(largest, 64, 0));
        Assert.True(LocalModels.Fits(largest, 32, 16));
        Assert.False(LocalModels.Fits(largest, 32, 8));
        Assert.False(LocalModels.Fits(LocalModels.Find("gemma4:12b")!, 16, 0));
    }

    [Fact]
    public void Pace_FollowsHowMuchOfTheModelIsOnTheCard()
    {
        var qwen = LocalModels.Find("qwen3.8:27b")!;
        var small = LocalModels.Find("gemma4:12b")!;

        Assert.Equal("1-3 min", LocalModels.Pace(small, 15.8));       // all of it on a 16 GB card
        Assert.Equal("3-8 min", LocalModels.Pace(qwen, 15.8));        // measured: 59% on the card
        Assert.Equal(0.59, LocalModels.CardShare(qwen, 15.8), 0.03);
        Assert.Equal(0.77, LocalModels.CardShare(LocalModels.Find("gemma4:26b")!, 15.8), 0.04);
        Assert.Equal("15+ min", LocalModels.Pace(qwen, 8));           // most of it in RAM
        Assert.Equal("~30 min", LocalModels.Pace(small, 0));
        Assert.Equal("1 h+", LocalModels.Pace(qwen, 0));
    }

    [Fact]
    public void Catalog_IsOrderedBestFirst_AndSaysWhichVerdictsAreOurOwn()
    {
        Assert.Equal(["qwen3.8:27b-q8_0", "qwen3.8:27b", "gemma4:26b", "gemma4:12b"], LocalModels.All.Select(m => m.Name));
        Assert.False(LocalModels.Find("qwen3.8:27b-q8_0")!.Tested);
        Assert.All(LocalModels.All.Skip(1), model => Assert.True(model.Tested));
        Assert.Null(LocalModels.Find("llama3"));
        Assert.NotNull(LocalModels.Find(" QWEN3.8:27B "));
    }
}
