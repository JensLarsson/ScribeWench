using System.IO.Compression;
using System.Text;
using ScribeWench.Core.Models;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace ScribeWench.Tests;

public sealed class EditAttachmentsTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "scribewench-tests", Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_base, "vault");
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
    private static readonly byte[] OtherPng = [0x89, 0x50, 0x4E, 0x47, 9, 9, 9];

    public EditAttachmentsTests() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    private ObsidianVault Vault() => ObsidianVault.Open(new ObsidianConfig { VaultPath = Root });

    private string WriteNote(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static byte[] Docx(params string[] paragraphs)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var body = string.Concat(paragraphs.Select(p => $"<w:p><w:r><w:t>{p}</w:t></w:r></w:p>"));
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            writer.Write($"""<?xml version="1.0"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{body}</w:body></w:document>""");
        }
        return stream.ToArray();
    }

    private static byte[] Pdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText(text, 12, new PdfPoint(25, 700), font);
        return builder.Build();
    }

    [Fact]
    public void Prepare_SortsImagesAndDocuments_AndReadsTheirText()
    {
        var prepared = EditAttachments.Prepare(Vault(),
        [
            new("vol.png", Png),
            new("backstory.md", Encoding.UTF8.GetBytes("﻿# Red\nBorn in Noxus.")),
            new("letter.docx", Docx("Dear Vol,", "The portal is mine. — Orfelt")),
            new("map.pdf", Pdf("The Bandle Woods")),
            new("song.mp3", [1, 2, 3]),
            new("huge.pdf", [], "over 25 MB"),
        ]);

        Assert.Equal(["vol.png"], prepared.Images.Select(i => i.VaultName));
        Assert.Equal(["backstory.md", "letter.docx", "map.pdf"], prepared.Documents.Select(d => d.FileName));
        Assert.Equal("# Red\nBorn in Noxus.", prepared.Documents[0].Text);
        Assert.Equal("Dear Vol,\nThe portal is mine. — Orfelt", prepared.Documents[1].Text);
        Assert.Contains("The Bandle Woods", prepared.Documents[2].Text);
        Assert.Equal(["song.mp3 (not an image or a document)", "huge.pdf (over 25 MB)"], prepared.Skipped);
    }

    [Fact]
    public void Prepare_ABrokenDocument_IsSkipped()
    {
        var prepared = EditAttachments.Prepare(Vault(), [new("notes.docx", [1, 2, 3])]);

        Assert.Empty(prepared.Documents);
        Assert.StartsWith("notes.docx (couldn't read it", Assert.Single(prepared.Skipped));
    }

    [Fact]
    public void ImageNames_AvoidOtherImages_AndReuseTheSameImage()
    {
        Directory.CreateDirectory(Path.Combine(Root, "Attachments"));
        File.WriteAllBytes(Path.Combine(Root, "Attachments", "image.png"), OtherPng);
        File.WriteAllBytes(Path.Combine(Root, "Attachments", "vol.png"), Png);

        var prepared = EditAttachments.Prepare(Vault(),
            [new("image.png", Png), new("image.png", Png), new("vol.png", Png)]);

        Assert.Equal(["image 2.png", "image 3.png", "vol.png"], prepared.Images.Select(i => i.VaultName));
        Assert.True(prepared.Images[2].AlreadyInVault);
    }

    [Fact]
    public void Message_ListsImagesAndDocuments_BeforeTheInstruction()
    {
        var prepared = EditAttachments.Prepare(Vault(), [new("vol.png", Png), new("backstory.txt", Encoding.UTF8.GetBytes("Red was born in Noxus."))]);

        var message = NoteEdits.BuildMessage(Vault(), "Runeterra", "Add this.", "Jens", 400_000, prepared).ReplaceLineEndings("\n");

        Assert.Contains("ATTACHED IMAGES", message);
        Assert.Contains("- vol.png\n", message);
        Assert.Contains("<document name=\"backstory.txt\">\nRed was born in Noxus.\n</document>", message);
        Assert.True(message.IndexOf("ATTACHED DOCUMENTS", StringComparison.Ordinal) < message.IndexOf("INSTRUCTION (from Jens)", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_SavesOnlyTheImagesANoteEmbeds()
    {
        WriteNote("NPCs/Vol.md", "The young lord.\n");
        var vault = Vault();
        var prepared = EditAttachments.Prepare(vault, [new("vol.png", Png), new("extra.png", OtherPng)]);
        var plan = new NoteEdits.Plan("Added his portrait.",
            [new NoteEdits.Edit("NPCs/Vol.md", false, "The young lord.", "![[vol.png]]\n\nThe young lord.")]);

        var outcome = NoteEdits.Apply(vault, plan, Path.Combine(_base, "backups"), DateTime.Now, prepared);

        Assert.Equal(Png, File.ReadAllBytes(Path.Combine(Root, "Attachments", "vol.png")));
        Assert.False(File.Exists(Path.Combine(Root, "Attachments", "extra.png")));
        Assert.Equal(["vol.png"], outcome.SavedImages);
        Assert.Equal(["extra.png"], outcome.UnusedImages);

        var discord = NoteEdits.FormatForDiscord(outcome);
        Assert.Contains("Images added: vol.png", discord);
        Assert.Contains("not kept: extra.png", discord);
    }

    [Fact]
    public void AttachmentsFolder_IsNotOfferedForNewNotes()
    {
        WriteNote("NPCs/Vol.md", "Lord.");
        Directory.CreateDirectory(Path.Combine(Root, "Attachments"));

        Assert.Equal(["NPCs"], VaultUpdates.Folders(Vault()));
    }
}
