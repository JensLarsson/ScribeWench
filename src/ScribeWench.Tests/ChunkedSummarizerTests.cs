using Microsoft.Extensions.Logging.Abstractions;
using ScribeWench.Core.Models;

namespace ScribeWench.Tests;

public class ChunkedSummarizerTests
{
    private static readonly DateTime Session = new(2026, 11, 22, 14, 30, 0);

    /// <summary>
    /// Stands in for a local model: counts tokens at a fixed number of characters each, refuses a
    /// request longer than its window the way Ollama does, and records what it was sent.
    /// </summary>
    private sealed class FakeModel(int contextTokens, double charactersPerToken = 4.0)
    {
        public sealed record Request(int Id, string System, string User, int MaxOutput, int PromptTokens, string Reply = "")
        {
            public bool IsPart => System.StartsWith("You are taking notes");
            public bool IsCondensing => System.StartsWith("Below are notes");
        }

        /// <summary>Requests the model answered. The summarizer throws one away when it left no room for the reply.</summary>
        public List<Request> Requests { get; } = [];
        /// <summary>Requests the model refused as longer than its window.</summary>
        public List<Request> Refused { get; } = [];
        public bool ReportsTokens { get; init; } = true;
        /// <summary>How long the notes on a part come out, in characters.</summary>
        public int PartNoteCharacters { get; init; } = 600;

        public IEnumerable<Request> Parts => Requests.Where(r => r.IsPart);
        public IEnumerable<Request> Condensing => Requests.Where(r => r.IsCondensing);
        public Request Final => Requests.Last();
        /// <summary>The parts whose notes ended up in the request the session note was written from.</summary>
        public List<Request> PartsUsed => Parts.Where(p => Final.User.Contains(p.Reply)).ToList();
        public bool Fits(Request r) => r.PromptTokens + r.MaxOutput <= contextTokens;

        public Task<ChunkedSummarizer.Reply> Call(string system, string user, int maxOutput, CancellationToken ct)
        {
            var tokens = (int)Math.Ceiling((system.Length + user.Length) / charactersPerToken);
            var request = new Request(Requests.Count + Refused.Count + 1, system, user, maxOutput, tokens);
            if (tokens > contextTokens)
            {
                Refused.Add(request);
                throw new ChunkedSummarizer.RequestTooLongException("the request exceeds the context size");
            }

            string text;
            if (request.IsPart)
                text = $"- notes from request {request.Id}: " + new string('n', PartNoteCharacters);
            else if (request.IsCondensing)
                text = $"- shorter from request {request.Id}: " + new string('s', Math.Min(PartNoteCharacters, (int)(maxOutput * charactersPerToken) / 2));
            else
                text = "## What Happened\nThe party did things.\n\n## Cliffhanger\nA door opened.";
            Requests.Add(request with { Reply = text });
            return Task.FromResult(new ChunkedSummarizer.Reply(text, ReportsTokens ? tokens : 0));
        }

        /// <summary>Nothing was refused or thrown away: every request left room in the window for its reply.</summary>
        public void AssertEveryRequestFits()
        {
            Assert.Empty(Refused);
            Assert.All(Requests, r => Assert.True(Fits(r),
                $"request {r.Id}: {r.PromptTokens} tokens + {r.MaxOutput} for the reply is over the {contextTokens}-token window"));
        }

        /// <summary>
        /// After a wrong first guess: the session note and the notes it was written from all came from
        /// requests that left room for their reply.
        /// </summary>
        public void AssertWhatWasUsedFits()
        {
            Assert.True(Fits(Final), $"the session note came from a request of {Final.PromptTokens} tokens + {Final.MaxOutput}");
            Assert.NotEmpty(PartsUsed);
            Assert.All(PartsUsed, p => Assert.True(Fits(p), $"part request {p.Id} did not leave room for its reply"));
        }
    }

    /// <summary>A session of <paramref name="lines"/> lines, two speakers taking turns, each line unique and findable.</summary>
    private static List<TranscriptSegment> Transcript(int lines, int wordsPerLine = 20) =>
        Enumerable.Range(0, lines)
            .Select(i => new TranscriptSegment(
                i % 2 == 0 ? "Erik" : "Anna", (ulong)(i % 2), TimeSpan.FromSeconds(i * 10), TimeSpan.FromSeconds(i * 10 + 8),
                $"line-{i:D5} " + string.Join(' ', Enumerable.Repeat("goblin", wordsPerLine))))
            .ToList();

    private static SummarizationOptions Options(string? context = null, Func<int, string>? within = null, string? partContext = null) =>
        new("Runeterra", Session)
        {
            SystemPrompt = "SESSION NOTE RULES. " + new string('r', 2000),
            Context = context,
            ContextWithin = within,
            PartContext = partContext,
        };

    private static Task<string> Run(FakeModel model, int contextTokens, List<TranscriptSegment> transcript, SummarizationOptions options) =>
        new ChunkedSummarizer(contextTokens, model.Call, NullLogger.Instance).SummarizeAsync(transcript, options);

    [Fact]
    public async Task ShortSession_GoesInOneRequest_WithAllTheContext()
    {
        var model = new FakeModel(16384);
        var options = Options(context: "CAST:\n- **Erik**: DM\n\nVAULT NOTES:\n<note name=\"Vol\">A smuggler.</note>");

        var note = await Run(model, 16384, Transcript(40), options);

        var request = Assert.Single(model.Requests);
        Assert.StartsWith("SESSION NOTE RULES", request.System);
        Assert.Contains("<note name=\"Vol\">A smuggler.</note>", request.User);
        Assert.Contains("TRANSCRIPT:\nErik: line-00000", request.User);
        Assert.Contains("Anna: line-00039", request.User);
        Assert.StartsWith("## What Happened", note);
        model.AssertEveryRequestFits();
    }

    [Fact]
    public async Task SessionThatFillsMostOfTheWindow_StillGoesInOneRequest_WhenItAllFits()
    {
        // The transcript takes about 80% of the room, but the game's notes are small enough to go with it.
        var model = new FakeModel(16384);
        var transcript = Transcript(170);
        var options = Options(context: "CAST:\n- **Erik**: DM\n\n<note name=\"Vol\">A smuggler.</note>");

        await Run(model, 16384, transcript, options);

        var request = Assert.Single(model.Requests);
        Assert.Contains("TRANSCRIPT:\n", request.User);
        Assert.Contains("line-00169", request.User);
        Assert.Contains("<note name=\"Vol\">A smuggler.</note>", request.User);
        model.AssertEveryRequestFits();
    }

    [Fact]
    public async Task LongSession_IsReadInParts_EveryLineOnce_AndEveryRequestFits()
    {
        var model = new FakeModel(8192);
        var transcript = Transcript(900);     // about 130,000 characters: several windows' worth

        var note = await Run(model, 8192, transcript, Options());

        var parts = model.Parts.ToList();
        Assert.True(parts.Count >= 3, $"expected several parts, got {parts.Count}");
        for (var i = 0; i < 900; i++)
        {
            var marker = $"line-{i:D5} ";
            Assert.Equal(1, parts.Count(p => p.User.Contains(marker)));
        }

        // Parts are about the same size rather than full ones and a scrap.
        var sizes = parts.Select(p => p.User.Length).ToList();
        Assert.True(sizes.Min() > sizes.Max() / 2, $"uneven parts: {string.Join(", ", sizes)}");

        // The session note is written from the notes, with the session note rules, and never sees the transcript.
        Assert.StartsWith("SESSION NOTE RULES", model.Final.System);
        Assert.Contains("NOTES ON THE TRANSCRIPT", model.Final.User);
        Assert.Contains($"<part number=\"1\" of=\"", model.Final.User);
        Assert.DoesNotContain("line-00000", model.Final.User);
        Assert.StartsWith("## What Happened", note);
        Assert.Empty(model.Refused);
        model.AssertEveryRequestFits();
    }

    [Fact]
    public async Task EachPart_GetsTheCast_AndTheNotesOnThePartBefore()
    {
        var model = new FakeModel(8192);
        var options = Options(partContext: "CAST:\n- **Erik**: DM\n\nKNOWN NAMES: Vol; Bandle City");

        await Run(model, 8192, Transcript(900), options);

        var parts = model.Parts.ToList();
        Assert.All(parts, p => Assert.Contains("KNOWN NAMES: Vol; Bandle City", p.User));
        Assert.DoesNotContain("NOTES ON THE PART BEFORE", parts[0].User);
        Assert.Contains("NOTES ON THE PART BEFORE", parts[1].User);
        Assert.Contains(parts[0].Reply, parts[1].User);
        Assert.Contains("This is part 2 of about", parts[1].User);
    }

    [Fact]
    public async Task GameNotes_AreCutToTheRoomThatIsLeft()
    {
        var model = new FakeModel(8192);
        var full = "CAST\n" + new string('v', 200_000);      // far more than the window
        var asked = new List<int>();
        string Within(int max)
        {
            asked.Add(max);
            return "CAST\n" + new string('v', Math.Max(0, max - 5));
        }

        await Run(model, 8192, Transcript(900), Options(context: full, within: Within));

        Assert.NotEmpty(asked);
        Assert.Contains("CAST\nvvv", model.Final.User);
        Assert.True(model.Final.User.Length < 40_000);
        Assert.Empty(model.Refused);
        model.AssertEveryRequestFits();
    }

    [Fact]
    public async Task GameNotesThatCannotBeCut_AreLeftOut_ButTheCastStays()
    {
        var model = new FakeModel(8192);
        var options = Options(context: "RULES\n" + new string('v', 200_000), within: _ => new string('v', 150_000),
            partContext: "CAST:\n- **Erik**: DM");

        var note = await Run(model, 8192, Transcript(900), options);

        Assert.DoesNotContain("vvvv", model.Final.User);
        Assert.Contains("- **Erik**: DM", model.Final.User);
        Assert.StartsWith("## What Happened", note);
        model.AssertEveryRequestFits();
    }

    [Fact]
    public async Task WhenTextTakesMoreTokensThanGuessed_ItPlansAgainWithLess()
    {
        // 1.5 characters per token: twice as many tokens as the summarizer first assumes.
        // (Short notes, so they all fit in the last request as written and can be traced back to their parts.)
        var model = new FakeModel(8192, charactersPerToken: 1.5) { PartNoteCharacters = 100 };

        var note = await Run(model, 8192, Transcript(200), Options());

        Assert.NotEmpty(model.Refused);                  // the first plan was too big...
        Assert.StartsWith("## What Happened", note);     // ...and it recovered
        Assert.Empty(model.Condensing);
        for (var i = 0; i < 200; i++)
            Assert.Equal(1, model.PartsUsed.Count(p => p.User.Contains($"line-{i:D5} ")));
        model.AssertWhatWasUsedFits();
    }

    [Fact]
    public async Task ShortSessionThatTurnsOutNotToFit_FallsBackToParts()
    {
        // Guessed at 3 characters per token this fits in one request. At 1.5 the model takes it but has
        // no room left to answer in full, so that reply is thrown away and the session is read in parts.
        var model = new FakeModel(8192, charactersPerToken: 1.5);

        await Run(model, 8192, Transcript(60), Options());

        Assert.Contains("TRANSCRIPT:\n", model.Requests[0].User);
        Assert.False(model.Fits(model.Requests[0]));
        Assert.Contains("NOTES ON THE TRANSCRIPT", model.Final.User);
        for (var i = 0; i < 60; i++)
            Assert.Equal(1, model.PartsUsed.Count(p => p.User.Contains($"line-{i:D5} ")));
        model.AssertWhatWasUsedFits();
    }

    [Fact]
    public async Task NotesTooLongForOneRequest_AreShortenedFirst()
    {
        // Long notes on many parts: together they would crowd out the reply and the game's notes.
        var model = new FakeModel(4096) { PartNoteCharacters = 1800 };

        var note = await Run(model, 4096, Transcript(700), Options());

        Assert.NotEmpty(model.Condensing);
        Assert.Contains("- shorter from request ", model.Final.User);
        Assert.StartsWith("## What Happened", note);
        model.AssertEveryRequestFits();
    }

    [Fact]
    public async Task ModelThatDoesNotCountTokens_StillGetsRequestsThatFit()
    {
        var model = new FakeModel(8192) { ReportsTokens = false };

        var note = await Run(model, 8192, Transcript(900), Options());

        Assert.StartsWith("## What Happened", note);
        Assert.Empty(model.Refused);
        model.AssertEveryRequestFits();
    }

    [Fact]
    public async Task WindowTooSmallForEvenOneLine_SaysSo()
    {
        // 0.2 characters per token: nothing useful ever fits.
        var model = new FakeModel(4096, charactersPerToken: 0.2);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(model, 4096, Transcript(200), Options()));
        Assert.Contains("context", ex.Message);
    }

    [Theory]
    [InlineData("the prompt is longer than the context length currently available to the model; shorten the prompt, adjust the context length in settings, or use a model with a longer context length", true)]
    [InlineData("input exceeds the maximum context length", true)]
    [InlineData("model 'gemma4:12b' not found, try pulling it first", false)]
    [InlineData("model requires more system memory (9.1 GiB) than is available (4.2 GiB)", false)]
    public void OllamaErrors_ThatMeanTheRequestWasTooLong_AreToldFromTheRest(string error, bool tooLong) =>
        Assert.Equal(tooLong, OllamaBackend.LooksLikeTooLong(error));

    [Fact]
    public void Compact_JoinsEachSpeakersRun_InTimeOrder_WithoutTimestamps()
    {
        var segments = new List<TranscriptSegment>
        {
            new("Anna", 2, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(24), "I open the door."),
            new("Erik", 1, TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(5), "You stand before"),
            new("Erik", 1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9), "an iron door."),
            new("Erik", 1, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(33), "   "),
        };

        var lines = TranscriptLines.Compact(segments);

        Assert.Equal(["Erik: You stand before an iron door.", "Anna: I open the door."], lines.Select(l => l.ToString()));
        Assert.Equal(TimeSpan.Zero, lines[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(9), lines[0].End);
        Assert.Equal("Erik: You stand before an iron door.\nAnna: I open the door.\n", TranscriptLines.Format(lines));
    }

    [Fact]
    public void Compact_DropsALineTheSpeakerJustSaid_AndSplitsVeryLongText()
    {
        var segments = new List<TranscriptSegment>();
        for (var i = 0; i < 7; i++)
            segments.Add(new("Ralkern", 1, TimeSpan.FromSeconds(i * 2), TimeSpan.FromSeconds(i * 2 + 2), "I'm sorry, but I'm not sure."));
        segments.Add(new("Jzns", 2, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), "Get up on shore."));
        segments.Add(new("Ralkern", 1, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(22), "Fine."));
        segments.Add(new("Pasted", 3, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), string.Join(' ', Enumerable.Repeat("word", 600))));

        var lines = TranscriptLines.Compact(segments);

        Assert.Equal(1, lines.Count(l => l.Text.Contains("I'm sorry, but I'm not sure.")));
        Assert.Contains(lines, l => l.Speaker == "Jzns");
        Assert.Contains(lines, l => l.Text.EndsWith("Fine."));
        Assert.All(lines, l => Assert.True(l.Text.Length <= TranscriptLines.MaxLineCharacters));
        Assert.Equal(600, lines.Where(l => l.Speaker == "Pasted").Sum(l => l.Text.Split(' ').Length));
    }
}
