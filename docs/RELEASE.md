# Volt Releases

Authoritative workflow for building, packaging, and publishing Volt releases with Velopack and GitHub Releases.

Volt's updater reads package metadata from Velopack, while users can open the GitHub release page for the full release. Keep the same Markdown notes in both places:

- `vpk pack --releaseNotes <file>` embeds notes into the package so the in-app update dialog can display `NotesMarkdown`.
- `gh release create --notes-file <file>` publishes the same notes on GitHub.

## Release Steps

Run these from the repository root on `master` after merging `develop`.

```powershell
$version = "1.5.0"
$tag = "v$version"
$notes = "docs/release-notes/$tag.md"

dotnet test Volt.Tests/Volt.Tests.csproj
dotnet publish Volt/Volt.csproj -c Release -r win-x64 -o publish

vpk pack `
  --packId Volt `
  --packVersion $version `
  --packDir publish `
  --mainExe Volt.exe `
  --icon Volt/Resources/Volt.ico `
  --releaseNotes $notes `
  -o Volt/Releases

gh release create $tag `
  --target master `
  --title "Volt $version" `
  --notes-file $notes

$env:GH_TOKEN = (gh auth token)
vpk upload github `
  --repoUrl https://github.com/willroofe/Volt `
  --tag $tag `
  --token $env:GH_TOKEN `
  --merge `
  --publish `
  -o Volt/Releases
```

## Important Details

- Use a three-part SemVer version for Velopack, such as `1.5.0`. A short version like `1.5` is not accepted by `vpk pack`.
- Always pass `--releaseNotes`; otherwise Volt's update dialog can show "Release notes are not available" even when the GitHub release page has notes.
- Use the same `-o` / `--outputDir` for `vpk pack` and `vpk upload github`. Volt uses `Volt/Releases`.
- Velopack generates delta packages when the previous `.nupkg` is still present in `Volt/Releases`.
- The upload step publishes `Volt-win-Setup.exe`, `Volt-win-Portable.zip`, `.nupkg` packages, `releases.win.json`, and legacy `RELEASES`.
- If `vpk` fails because it targets an older .NET runtime, run commands with `$env:DOTNET_ROLL_FORWARD = "Major"` or reinstall the tool for an available runtime.
- If you must repack an already-published tag, delete the existing release assets before re-uploading files with the same names.

## Release Notes

Release notes are for users, not developers. Describe what feels better or what users can now do. Avoid internal wording such as IPC, UI thread, framework-dependent, or implementation names unless users see them directly.

Only mention fixes for bugs that existed in the previous published release. Omit regressions that were introduced and fixed entirely inside the same development cycle.

Use this Markdown shape:

```markdown
## What's new

- **Short label** — Plain-language sentence about the user-visible change.
- **Another area** — Another concrete improvement users will notice.
```

Use optional keyboard shortcuts in bold, such as **Ctrl+Shift+T**, when they are part of the feature.
