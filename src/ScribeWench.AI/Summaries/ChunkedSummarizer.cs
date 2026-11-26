using System.Text;
using Microsoft.Extensions.Logging;
using ScribeWench.Core.Models;

namespace ScribeWench.AI.Summaries;

// Writes a session note with a model whose context window can't be counted on to hold a whole
// session. Whatever fits is sent as it is. A transcript that is too long is read a part at a time,
// with notes taken on each part, and the session note is written from the notes. Every request is
// sized so that it and its reply fit in the window together, so nothing is dropped silently.
public sealed class ChunkedSummarizer
{
    // PromptTokens: what the model counted in the request; 0 when it doesn't say.
    // CutOff: the reply ran out of room.
    public sealed record Reply(string Text, int PromptTokens = 0, bool CutOff = false);

    // maxOutputTokens is the most it may reply with, thinking included.
    public delegate Task<Reply> ModelCall(string systemPrompt, string userMessage, int maxOutputTokens, CancellationToken ct);

    // Thrown by a ModelCall when the model refuses a request as too long for its window.
    public sealed class RequestTooLongException(string message) : Exception(message);

    // Below this there isn't room for the instructions, some transcript and a reply.
    public const int MinContextTokens = 4096;

    // However large the window: notes on a part are a fixed length at most, so longer parts mean thinner notes.
    private const int MaxPartTokens = 12_000;

    // Assumed until the model has told us. Deliberately low (English prose is nearer 4): guessing too
    // many tokens only wastes a little room, guessing too few overflows the window.
    private const double InitialCharactersPerToken = 3.0;

    private const int MaxAttempts = 5;

    private readonly int _context;
    private readonly int _thinking;
    private readonly ModelCall _call;
    private readonly ILogger _logger;
    private double _charactersPerToken = InitialCharactersPerToken;
    private bool _measured;

    // thinkingTokens: room kept free in every request for a thinking model's thinking.
    public ChunkedSummarizer(int contextTokens, ModelCall call, ILogger logger, int thinkingTokens = 0)
    {
        _context = Math.Max(MinContextTokens, contextTokens);
        _thinking = Math.Clamp(thinkingTokens, 0, _context / 4);
        _call = call;
        _logger = logger;
    }

    // Room kept free for the session note with its Cast Updates and Vault Updates sections.
    private int NoteTokens => Math.Clamp(_context / 4, 1024, 8192);

    // Room kept free for the notes on one part.
    private int PartNoteTokens => Math.Clamp(_context / 8, 512, 2048);

    // Left unused in every request: the chat template's own tokens, and some give in the estimates.
    private int Slack => Math.Max(64, _context / 50);

    private int Tokens(int characters) => (int)Math.Ceiling(characters / _charactersPerToken);
    private int Tokens(string text) => Tokens(text.Length);
    private int Characters(int tokens) => (int)Math.Floor(Math.Max(0, tokens) * _charactersPerToken);

    // About 0.6 words to a token, for telling the model how long its notes may be.
    private static int Words(int tokens) => Math.Max(100, tokens * 6 / 10 / 50 * 50);

    public async Task<string> SummarizeAsync(
        IReadOnlyList<TranscriptSegment> transcript, SummarizationOptions options, CancellationToken ct = default)
    {
        var lines = TranscriptLines.Compact(transcript);
        var transcriptText = TranscriptLines.Format(lines);
        var system = Prompts.SystemPromptFor(options);
        List<Part>? parts = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // Everything but the instructions and the reply: the transcript (or the notes on it) and the game's notes.
            var room = _context - Tokens(system) - NoteTokens - _thinking - Slack;

            string user;
            // In one request when it all fits, or when the transcript leaves at least 40% of the room for
            // the game's notes (cut down to that). A longer transcript is read in parts.
            var everything = Tokens(transcriptText) + Tokens(options.Context?.Length ?? 0) + Tokens(200);
            if (parts is null && (everything <= room || Tokens(transcriptText) <= room * 6 / 10))
            {
                var context = ContextWithin(options, Characters(room - Tokens(transcriptText)) - 200);
                user = Prompts.BuildUserMessage(transcriptText, options with { Context = context });
                _logger.LogInformation("Summary: the whole transcript in one request (about {Tokens:N0} tokens of a {Context:N0}-token window)",
                    Tokens(system) + Tokens(user), _context);
            }
            else
            {
                parts ??= await ReadInPartsAsync(lines, options, ct);
                parts = await CondenseAsync(parts, room / 2, ct);
                var notes = FormatParts(parts);
                var context = ContextWithin(options, Characters(room - Tokens(notes)) - 600);
                user = Prompts.BuildUserMessageFromNotes(notes, parts.Count, options with { Context = context });
                _logger.LogInformation("Summary: writing the session note from the notes on {Parts} part(s) (about {Tokens:N0} tokens of a {Context:N0}-token window)",
                    parts.Count, Tokens(system) + Tokens(user), _context);
            }

            var reply = await CallAsync(system, user, NoteTokens, "Session note", ct);
            if (reply is null) continue;    // it didn't fit after all; the estimate is corrected, so plan again

            if (reply.CutOff)
                _logger.LogWarning("The session note was cut off at {Tokens:N0} tokens, so its last sections may be missing. A larger context window (ai.ollama.contextSize) leaves more room for it.", NoteTokens);
            return reply.Text;
        }

        throw new InvalidOperationException(
            $"The session doesn't fit in the model's context window ({_context:N0} tokens) even in parts. Set a larger context size, or use a model with a larger window.");
    }

    // ── reading the transcript in parts ──────────────────────────────────────

    private sealed record Part(TimeSpan Start, TimeSpan End, string Notes);

    private async Task<List<Part>> ReadInPartsAsync(List<TranscriptLine> lines, SummarizationOptions options, CancellationToken ct)
    {
        var system = Prompts.PartNotesSystemPrompt(Words(PartNoteTokens));
        var parts = new List<Part>();
        var next = 0;
        var failures = 0;
        var expected = 0;

        while (next < lines.Count)
        {
            // The cast and the names, and the notes on the part before so this one picks up where it left off.
            // Each gets an eighth of the window at most.
            var known = Fit(options.PartContext, Characters(_context / 8), keepEnd: false);
            var before = parts.Count == 0 ? "" : Fit(parts[^1].Notes, Characters(_context / 8), keepEnd: true);

            var fixedTokens = Tokens(system) + Tokens(known) + Tokens(before) + Tokens(400) + PartNoteTokens + _thinking + Slack;
            var roomCharacters = Characters(Math.Min(MaxPartTokens, _context - fixedTokens));
            if (roomCharacters < lines[next].Length)
                throw new InvalidOperationException(
                    $"The model's context window ({_context:N0} tokens) is too small to read the transcript even a line at a time. Set a larger context size.");

            // Parts of about equal length, rather than full ones and a scrap at the end.
            var remaining = lines.Skip(next).Sum(l => (long)l.Length);
            var partsLeft = (int)Math.Ceiling(remaining / (double)roomCharacters);
            var target = Math.Min(roomCharacters, remaining / partsLeft + 1);
            if (expected == 0 || parts.Count + partsLeft > expected) expected = parts.Count + partsLeft;

            var end = next;
            long taken = 0;
            while (end < lines.Count && (end == next || taken + lines[end].Length <= target))
                taken += lines[end++].Length;

            var number = parts.Count + 1;
            var from = lines[next].Start;
            var to = lines[end - 1].End;
            var user = new StringBuilder()
                .Append($"Campaign: {options.CampaignName}\n")
                .Append($"Session date: {options.SessionDateTime:yyyy-MM-dd HH:mm}\n")
                .Append($"This is part {number} of about {expected} of the session's transcript, {Clock(from)} to {Clock(to)} into the recording.\n\n");
            if (known.Length > 0) user.Append(known.TrimEnd()).Append("\n\n");
            if (before.Length > 0)
                user.Append("NOTES ON THE PART BEFORE THIS ONE (so you know where things stand; don't repeat them):\n")
                    .Append(before.TrimEnd()).Append("\n\n");
            user.Append($"TRANSCRIPT, PART {number}:\n").Append(TranscriptLines.Format(lines.Skip(next).Take(end - next)));

            _logger.LogInformation("Summary: reading part {Number} of about {Expected} ({From} to {To}, {Lines} lines)",
                number, expected, Clock(from), Clock(to), end - next);

            var reply = await CallAsync(system, user.ToString(), PartNoteTokens, $"Part {number}", ct);
            if (reply is null)
            {
                if (++failures >= MaxAttempts)
                    throw new InvalidOperationException(
                        $"Part {number} of the transcript doesn't fit in the model's context window ({_context:N0} tokens). Set a larger context size.");
                continue;   // same place, smaller part
            }

            failures = 0;
            parts.Add(new Part(from, to, reply.Text.Trim()));
            _logger.LogDebug("Notes on part {Number}:\n{Notes}", number, parts[^1].Notes);
            next = end;
        }

        return parts;
    }

    // Shortens the notes until they fit, by rewriting neighbouring parts as one, two at a time. Rarely
    // needed: only with a small window or a very long session.
    private async Task<List<Part>> CondenseAsync(List<Part> parts, int budgetTokens, CancellationToken ct)
    {
        var failures = 0;
        while (parts.Count > 1 && Tokens(FormatParts(parts)) > budgetTokens)
        {
            var merged = new List<Part>();
            var share = Math.Max(200, Math.Min(PartNoteTokens, budgetTokens / ((parts.Count + 1) / 2) - 50));
            var system = Prompts.CondenseNotesSystemPrompt(Words(share));
            _logger.LogInformation("Summary: the notes on {Parts} parts are too long for one request; shortening them two at a time", parts.Count);

            var failed = false;
            for (var i = 0; i < parts.Count; i += 2)
            {
                if (i + 1 == parts.Count) { merged.Add(parts[i]); break; }     // odd one out; it gets its turn next round

                var pair = new List<Part> { parts[i], parts[i + 1] };
                var reply = await CallAsync(system, FormatParts(pair), share, $"Notes on parts {i + 1}-{i + 2}", ct);
                if (reply is null) { failed = true; break; }
                merged.Add(new Part(pair[0].Start, pair[1].End, reply.Text.Trim()));
            }

            if (failed)
            {
                if (++failures >= MaxAttempts)
                    throw new InvalidOperationException(
                        $"The notes on the transcript don't fit in the model's context window ({_context:N0} tokens). Set a larger context size.");
                continue;
            }
            parts = merged;
        }

        // One set of notes left and still too long: only possible when the reply limit was ignored.
        if (parts.Count == 1 && Tokens(FormatParts(parts)) > budgetTokens)
            parts = [parts[0] with { Notes = Fit(parts[0].Notes, Characters(budgetTokens) - 200, keepEnd: false) }];
        return parts;
    }

    private static string FormatParts(IReadOnlyList<Part> parts)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < parts.Count; i++)
            sb.Append($"<part number=\"{i + 1}\" of=\"{parts.Count}\" time=\"{Clock(parts[i].Start)} to {Clock(parts[i].End)}\">\n")
              .Append(parts[i].Notes).Append("\n</part>\n\n");
        return sb.ToString();
    }

    private static string Clock(TimeSpan time) => time.ToString(@"hh\:mm\:ss");

    // ── fitting things in ────────────────────────────────────────────────────

    // The game's notes in the room given: all of them when they fit, else with note text left out. When
    // even the names don't fit, only the cast and the names that do.
    private string? ContextWithin(SummarizationOptions options, int maxCharacters)
    {
        var full = options.Context;
        if (string.IsNullOrWhiteSpace(full)) return null;
        if (full.Length <= maxCharacters) return full;

        var shorter = options.ContextWithin?.Invoke(maxCharacters);
        if (shorter is not null && shorter.Length <= maxCharacters)
        {
            _logger.LogInformation("Summary: the game's notes take {Full:N0} characters and there is room for {Room:N0}, so the text of the oldest notes is left out ({Sent:N0} sent)",
                full.Length, maxCharacters, shorter.Length);
            return shorter;
        }

        _logger.LogWarning(
            "The game's notes don't fit in the model's context window ({Context:N0} tokens), even listed by name only. This summary is written without them, so it has no links and adds nothing to the cast or the other notes. A larger context size fixes that.",
            _context);
        var known = Fit(options.PartContext, maxCharacters, keepEnd: false);
        return known.Length > 0 ? known : null;
    }

    // Cut at a line break, keeping the start or the end.
    private static string Fit(string? text, int maxCharacters, bool keepEnd)
    {
        if (string.IsNullOrEmpty(text) || maxCharacters <= 0) return "";
        if (text.Length <= maxCharacters) return text;

        if (keepEnd)
        {
            var from = text.IndexOf('\n', text.Length - maxCharacters);
            return from < 0 ? text[^maxCharacters..] : text[(from + 1)..];
        }
        var to = text.LastIndexOf('\n', maxCharacters);
        return to <= 0 ? text[..maxCharacters] : text[..to];
    }

    // Null when the request turned out not to fit in the window with its reply. The estimate of how many
    // characters make a token has then been lowered, so the caller plans again with less.
    private async Task<Reply?> CallAsync(string system, string user, int answerTokens, string what, CancellationToken ct)
    {
        var maxOutput = answerTokens + _thinking;
        var characters = system.Length + user.Length;

        Reply reply;
        try
        {
            reply = await _call(system, user, maxOutput, ct);
        }
        catch (RequestTooLongException ex)
        {
            _charactersPerToken = Math.Max(1.0, _charactersPerToken * 0.75);
            _logger.LogWarning("{What}: the model refused the request as too long for its window ({Reason}). Trying again with less.", what, ex.Message);
            return null;
        }

        if (reply.PromptTokens <= 0) return reply;     // the backend can't tell us; trust the estimate

        var measured = characters / (double)reply.PromptTokens;
        if (reply.PromptTokens + maxOutput > _context)
        {
            // The request was counted (or cut) at more than there was room for. Its real size is at least
            // what was counted, so the true ratio is no higher than the one measured.
            _charactersPerToken = Math.Max(1.0, Math.Min(_charactersPerToken, measured) * 0.8);
            _logger.LogWarning("{What}: the request came to {Tokens:N0} tokens, too many for a {Context:N0}-token window with {Output:N0} kept for the reply. Trying again with less.",
                what, reply.PromptTokens, _context, maxOutput);
            return null;
        }

        // A little under what was measured, because the mix of text differs from one request to the next.
        // The first measurement replaces the guess; after that only a lower one counts.
        var cautious = Math.Clamp(measured * 0.9, 1.0, 6.0);
        if (!_measured || cautious < _charactersPerToken) _charactersPerToken = cautious;
        _measured = true;
        return reply;
    }
}
