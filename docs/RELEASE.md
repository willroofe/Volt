# Volt releases (Velopack + GitHub)

Authoritative instructions for building, packaging, and publishing a release. Point assistants here with `@docs/RELEASE.md` when asking for a release.

## Velopack workflow

Volt uses [Velopack](https://github.com/velopack/velopack) for installer packaging and auto-updates, hosted on GitHub Releases.

### Commands (bash)

```bash
# 1. Build
dotnet publish Volt/Volt.csproj -c Release -r win-x64 -o publish
vpk pack --packId Volt --packVersion <VERSION> --packDir publish --mainExe Volt.exe --icon Volt/Resources/Volt.ico -o Volt/Releases

# 2. Create GitHub release with proper notes (vpk auto-generates poor notes from last commit)
gh release create v<VERSION> --title "Volt <VERSION>" --notes "<release notes>"

# 3. Upload assets to the existing release using --merge (must use the same -o as pack; see Checklist)
GH_TOKEN=$(gh auth token) && vpk upload github --repoUrl https://github.com/willroofe/Volt --tag v<VERSION> --token "$GH_TOKEN" --merge --publish -o Volt/Releases
```

### Commands (PowerShell)

```powershell
dotnet publish Volt/Volt.csproj -c Release -r win-x64 -o publish
vpk pack --packId Volt --packVersion <VERSION> --packDir publish --mainExe Volt.exe --icon Volt/Resources/Volt.ico -o Volt/Releases
gh release create v<VERSION> --title "Volt <VERSION>" --notes "<release notes>"
$env:GH_TOKEN = (gh auth token); vpk upload github --repoUrl https://github.com/willroofe/Volt --tag v<VERSION> --token $env:GH_TOKEN --merge --publish -o Volt/Releases
```

### Why this order

Velopack’s default release creation only uses the last commit message, which produces poor notes. Creating the release first with `gh` gives full control over the body; `--merge` uploads assets to that existing release.

### Checklist

- Bump `--packVersion` and `--tag` for each release.
- Write human-readable release notes summarizing all changes since the **last published** release, not just the last commit.
- Use the **same** `-o` / `--outputDir` for `vpk pack` and `vpk upload github` (for example `-o Volt/Releases`). `vpk upload` defaults to `./Releases` at the repo root; if that folder still holds older `.nupkg` or installers from past experiments, Velopack can upload the wrong files to the new GitHub release.
- Velopack auto-generates delta packages when a previous version’s `.nupkg` is in `Volt/Releases/`.
- Release artifacts land in `Volt/Releases/` (Setup.exe, `.nupkg`, Portable.zip).
- The app checks for updates on startup (silent) and via “Check for Updates” in the command palette.
- Install the Velopack CLI with `dotnet tool install -g vpk --framework net8.0` when the machine has .NET 8 and 10 but not 9 (adjust if your SDK layout differs).

## Release notes style

Release notes are for **end users**, not developers. Keep language non-technical and user-facing: describe what the user experiences, not internal implementation details. Avoid jargon like “IPC”, “UI thread”, “framework-dependent”, “status bar encoding” — say what improved from the user’s perspective instead.

**Regression-only bug fixes:** Do not list a bug fix if the bug was introduced and fixed within the same dev cycle. Users never saw those bugs. Only mention bugs that existed in a **prior published** release. When writing notes, cross-reference fixes against what shipped in the last release; omit fixes for bugs introduced after that release.

Each bullet should read as a user-visible change, not a code change.

### GitHub release body format

Match recent published releases (for example **v1.3.0** and **v1.3.2** on GitHub). The body is Markdown with this shape:

1. A single heading: `## What's new` (do not rely on Velopack’s auto-generated notes).
2. One bullet per user-facing change, each on its own line, using an em dash after a short bold lead-in:
   - `- **Short label** — Plain-language sentence about what the user can do or what feels better.`
3. Optional keyboard shortcuts in **bold** when they are part of the feature (for example **Ctrl+Shift+T**).

Example:

```markdown
## What's new

- **Feature name** — What the user gains in everyday use.
- **Another area** — A specific improvement users will notice.
```
