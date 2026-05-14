# Profiling Volt

Volt has an opt-in trace exporter for investigating UI hitches without attaching a debugger.

## Capture a Trace

From PowerShell:

```powershell
$env:VOLT_PROFILE = "1"
dotnet run --project Volt/Volt.csproj -- test-files/test-5mb.json
```

Reproduce the slow action, then close Volt. The trace is written to:

```text
%TEMP%\Volt\Profiles\volt-profile-<timestamp>.json
```

To choose the output file:

```powershell
$env:VOLT_PROFILE = "1"
$env:VOLT_PROFILE_PATH = "C:\Temp\volt-profile.json"
dotnet run --project Volt/Volt.csproj -- test-files/test-5mb.json
```

## View a Trace

Open the JSON file in [Perfetto](https://ui.perfetto.dev/) using **Open trace file**.
The file uses Chrome trace-event JSON, so it can also be opened in `chrome://tracing`.

Useful spans for large-file regressions include:

- `MainWindow.OpenFileInTabAsync`
- `MainWindow.ActivateTab`
- `MainWindow.UpdateActiveTabHooks`
- `Editor.OnRender`
- `Editor.RenderTextVisual`
- `Editor.GetRenderStateAt`
- `Editor.GetTokensForRenderedSegment`
- `Editor.BuildLargeDocumentMatchingPairIndex`
- `Find.GetMatchesInRange`
- `FileTextSource.ReadLinesFromFile`
