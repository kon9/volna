# Contributing

Thanks for wanting to help. Bug reports, translations and pull requests are all welcome.

## Getting started

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/Volna.App      # run the app
dotnet test tests/Volna.Tests           # run the tests
dotnet run --project tools/Demo -- smoke   # build and render the interface without a screen
dotnet format Volna.sln                 # fix the formatting
```

Warnings fail the build on CI, so a pull request should build without any. CI also checks the shell scripts, workflows and Markdown.

Before a release, `bash tools/acceptance.sh` downloads openly licensed videos and songs through the real app and agent and checks every file with ffprobe: quality, container, sound, subtitles, cover, tags, parts of a video, playlists, imports. It needs the network and about fifteen minutes. The Acceptance workflow runs the same on Windows, macOS and Linux.

## How the code is laid out

| Folder | What is in it |
| --- | --- |
| `src/Volna.Core` | Everything without a user interface: `QueueController` (the queue's logic), `Engine` (yt-dlp arguments, error messages, song matching), `Auth` (sign-in), `ToolManager` (first-run install), `Strings/` (translations) |
| `src/Volna.App` | The Avalonia window. It should stay thin: logic belongs in Core, where it can be tested |
| `src/Volna.Agent` | The MCP server and command-line tools for AI assistants |
| `tests/Volna.Tests` | Tests. `FakeBackend` stands in for yt-dlp, so they need no network |
| `tools/Demo` | Renders the real interface headlessly with openly licensed songs and films, for the README's pictures and as a smoke test |
| `tools/IconGen` | Turns `assets/icon.svg` into every icon format |

The queue never talks to the outside world directly: everything goes through `IQueueBackend`. That is what makes it testable, so a new queue behavior should come with a test using `FakeBackend`.

## Adding a language

1. Copy `src/Volna.Core/Strings/en.json` to `<code>.json`, using the [two-letter code](https://en.wikipedia.org/wiki/List_of_ISO_639_language_codes).
2. Translate the values. Keep `{0}` and `{1}` where they are.
3. Add the code to `Loc.Languages` in `Foundation.cs`, and to `Languages` and `LanguageBox` in the main window.

A test checks that every language has exactly the same keys.

## Rules for the repository

These protect the project, so please keep to them:

- **Only openly licensed music and videos in code, tests, docs and screenshots**, such as Kevin MacLeod's music or the Blender films, or invented names. An older project in this field was taken down over exactly this.
- **Never add a way around DRM or paid access.**
- **Never send a person's data anywhere but the site it belongs to.** No analytics, no telemetry.

## Pull requests

Volna is under the GPL, version 3. By opening a pull request you agree that your contribution is under the same license, and that the maintainer may also license it to others under different terms, for example commercial ones. If you cannot agree to that, say so in the pull request before it is merged.

- Keep each one to a single change, and say why it is needed.
- Run the tests. CI runs them on Windows, macOS and Linux.
- Check that it behaves on the platforms you can reach, and say which those were.

For a bug in how a site downloads, first see if it also happens with plain `yt-dlp`. If so, it belongs [there](https://github.com/yt-dlp/yt-dlp/issues).
