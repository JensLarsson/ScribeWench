using ScribeWench.AI.Backends;
using ScribeWench.Core.Models;

namespace ScribeWench.CLI.Wizard.Writers;

// What setup found on this PC that decides which ways to write notes are offered first.
// LocalModel: the best tested local model this PC has the memory for, if any.
public sealed record WriterFindings(bool ClaudeCodeInstalled, LocalModel? LocalModel);

/// <summary>
/// Strategy for setting up one way to get the session notes written, each matching a backend.
/// The step that lists them never branches on which is which.
/// </summary>
public interface IWriterSetup
{
    /// <summary>The ai.backend value it sets up.</summary>
    string Backend { get; }

    /// <summary>Where it goes in the list, lowest first: what suits this PC comes first.</summary>
    int Order(WriterFindings found);

    /// <summary>Its entry in the list, as markup.</summary>
    string Label(WriterFindings found);

    /// <summary>The settings in a line, for the review.</summary>
    string Describe(AiConfig ai);

    /// <summary>Asks its questions. False when the user backed out, which leads back to the list.</summary>
    Task<bool> ConfigureAsync(AiConfig ai, CancellationToken ct);
}
