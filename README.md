# ScribeWench

A Discord bot that records a tabletop RPG session from a voice channel, transcribes it on your PC with Whisper, and has an AI write the session notes.

It runs on Windows. The bot is your own: you create it on Discord, which is free and takes a few minutes, and run it on your PC.

## Setup

```
scribewench setup
```

Double-clicking `scribewench.exe` does the same the first time. Setup takes you through four things:

1. **The Discord bot.** Creating it, pasting its token and adding it to your server.
2. **Transcription.** The Whisper model that suits your graphics card, downloaded for you.
3. **Who writes the notes.** Claude, a local model through [Ollama](https://ollama.com) (free and private; setup installs it), or any service with an OpenAI-compatible API, such as OpenAI, DeepSeek or OpenRouter.
4. **Where the notes go.** `Documents\ScribeWench` unless you say otherwise.

With "Do it for me", setup only asks for the token and who writes the notes, and picks the rest to suit the PC.

Run `scribewench setup` again to change a setting, or `scribewench setup --dry-run` to go through it without changing anything.

Settings are saved in `~/.scribewench/config.json`. The bot's token is in there, so keep that file to yourself.

## Use

```
scribewench run
```

Then, in Discord:

| Command | Action |
|---|---|
| `!scribe create-game <name>` | Add a game (a campaign or a one-shot) |
| `!scribe join <game>` | Join your voice channel and start recording |
| `!scribe stop` | Stop recording and write the summary |
| `!scribe status` | Show what is being recorded and for how long |
| `!scribe list-games` | List this server's games |
| `!scribe remove-game <name>` | Take a game off the list; its notes are kept |
| `!scribe edit <game> <correction>` | Correct the game's notes in your own words; attach images or documents to add them |
| `!scribe instructions-show <game>` | Send the instructions the summaries are written from, as a file |
| `!scribe instructions-edit <game>` | Replace them with the attached file |
| `!scribe instructions-reset <game>` | Go back to the default instructions |
| `!scribe help` | Show the command list |

## Notes

The summary is posted in Discord and saved as a note. Notes are plain Markdown, one folder per game, and the notes folder can be opened in [Obsidian](https://obsidian.md) as a vault:

```
Documents\ScribeWench\
  Curse of Strahd\
    Cast.md             who plays whom
    Sessions\           one note per session
    Transcripts\
    NPCs\, Places\, …   your own notes
```

- The AI reads the game's notes along with the transcript, so it knows who and what things are and links them with `[[wikilinks]]`.
- After each session it adds what happened to the game's other notes (a death, a new owner, where someone was last seen) and makes notes for new names that matter.
- A note is copied to `~/.scribewench/edit-backups/` before the bot changes it.

Everything in a game's folder except `Transcripts\` is sent to the AI you chose, so keep nothing there that shouldn't leave the PC.

To write a summary again from a saved transcript:

```
scribewench summarize "path\to\transcript.md"
```

This adds a second note. With `--replace`, it replaces the session's note instead.

## Build

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9).

```
dotnet publish src/ScribeWench.CLI/ScribeWench.CLI.csproj -c Release -r win-x64 -o ./publish/win-x64
```

The result is `publish/win-x64/scribewench.exe`. The DLLs next to it must stay in the same folder.
