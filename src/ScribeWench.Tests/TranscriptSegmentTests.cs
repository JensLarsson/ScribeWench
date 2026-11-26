using ScribeWench.Core.Models;

namespace ScribeWench.Tests;

public class TranscriptSegmentTests
{
    [Fact]
    public void Segments_SortByStart_ProducesChronologicalOrder()
    {
        var segments = new List<TranscriptSegment>
        {
            new("Bob",   2, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), "Second"),
            new("Alice", 1, TimeSpan.FromSeconds(0),  TimeSpan.FromSeconds(5),  "First"),
            new("Bob",   2, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(25), "Third"),
        };

        var sorted = segments.OrderBy(s => s.Start).ToList();

        Assert.Equal("First",  sorted[0].Text);
        Assert.Equal("Second", sorted[1].Text);
        Assert.Equal("Third",  sorted[2].Text);
    }

    [Fact]
    public void Segments_FilterBySpeaker_ReturnsCorrectSubset()
    {
        var segments = new List<TranscriptSegment>
        {
            new("Alice", 1, TimeSpan.Zero, TimeSpan.FromSeconds(5), "Hello"),
            new("Bob",   2, TimeSpan.Zero, TimeSpan.FromSeconds(5), "World"),
            new("Alice", 1, TimeSpan.Zero, TimeSpan.FromSeconds(5), "Again"),
        };

        var alice = segments.Where(s => s.SpeakerLabel == "Alice").ToList();

        Assert.Equal(2, alice.Count);
        Assert.All(alice, s => Assert.Equal("Alice", s.SpeakerLabel));
    }

    [Fact]
    public void SummarizationOptions_SessionDateTime_RoundTrips()
    {
        var dt = new DateTime(2026, 5, 21, 19, 30, 0);
        var options = new SummarizationOptions("Test Campaign", dt);

        Assert.Equal(dt, options.SessionDateTime);
        Assert.Equal("Test Campaign", options.CampaignName);
    }
}
