# Project Feature Design

## Overview

Add the ability to create, save, and open **Projects** — collections of multiple folders organized with optional virtual groupings. Projects are saved as `.vproj` files (JSON) that can live anywhere on disk. When a project is open, it replaces the single-folder explorer with a project tree that supports user-defined virtual folders for custom organization.

## Data Model

### `Project` class (new file: `UI/Project.cs`)

In-memory representation of an open project:

- `Name` (string) — display name, defaults to filename without extension
- `FilePath` (string) — absolute path to the `.vproj` file
- `Folders` (List&lt;ProjectFolder&gt;) — real filesystem folders:
  - `Path` (string) — absolute filesystem path
  - `VirtualParent` (string?) — name of the virtual folder it belongs to, or null for project root
- `VirtualFolders` (List&lt;string&gt;) — user-created organizational group names
- `Session` (ProjectSession) — open tabs and their state:
  - `Tabs` (List&lt;ProjectSessionTab&gt;) — each with `FilePath`, `IsDirty`, `CaretLine`, `CaretCol`, `ScrollX`, `ScrollY`
  - `ActiveTabIndex` (int)

### `.vproj` file format

```json
{
  "name": "My Project",
  "virtualFolders": ["Source", "Tests"],
  "folders": [
    { "path": "C:\\Work\\MyApp\\src", "virtualParent": "Source" },
    { "path": "D:\\Libraries\\SharedLib\\tests", "virtualParent": "Tests" },
    { "path": "C:\\Work\\Docs" }
  ],
  "session": {
    "tabs": [
      {
        "filePath": "C:\\Work\\MyApp\\src\\main.cs",
        "isDirty": false,
        "caretLine": 42,
        "caretCol": 0,
        "scrollX": 0.0,
        "scrollY": 1200.0
      }
    ],
    "activeTabIndex": 0
  }
}
```

- Folders without `virtualParent` appear at the project root level
- Virtual folders with no assigned real folders are still displayed (empty groups)

## ProjectManager (`UI/ProjectManager.cs`)

Instance owned by `MainWindow`. Manages the lifecycle of projects.

### Properties

- `CurrentProject` — the active `Project`, or null when no project is open

### Methods

- `NewProject()` — creates an empty in-memory project, prompts for save location via SaveFileDialog
- `OpenProject(string vprojPath)` — deserializes `.vproj` JSON, returns `Project`
- `SaveProject()` — serializes current project to its `FilePath` using `FileHelper.AtomicWriteText`
- `CloseProject()` — saves session state into the project, clears `CurrentProject`, returns explorer to empty state
- `AddFolder(string path)` — adds a real folder to the project (no virtual parent)
- `RemoveFolder(string path)` — removes a real folder from the project
- `CreateVirtualFolder(string name)` — adds a virtual folder
- `RemoveVirtualFolder(string name)` — removes virtual folder, reassigns its children to project root (null virtual parent)
- `AssignToVirtualFolder(string folderPath, string? virtualFolderName)` — moves a real folder into or out of a virtual folder

### Session management

- **No overlap with AppSettings.Session** — when a project is open, session state lives in the `.vproj` file. When no project is open, the existing `AppSettings` session system works as before.
- On `CloseProject` or app shutdown with project open: snapshot open tabs (file paths, caret positions, scroll offsets, dirty state) into `Project.Session`, then `SaveProject()`.
- On `OpenProject`: close all current tabs (prompt to save dirty ones), restore tabs from `Project.Session`, open files, restore caret/scroll positions. If no session tabs, open a single empty tab.

## Explorer Panel Changes

### FileExplorerPanel

Gains two modes:

- **Folder mode** (existing behavior) — single root folder, flat tree, header shows folder name
- **Project mode** — header shows project name, tree shows virtual + real folder structure

### FileTreeItem changes

New `ItemKind` enum: `File`, `Directory`, `VirtualFolder`, `ProjectRoot`

- `ProjectRoot` — non-interactive label at the top showing project name
- `VirtualFolder` — groups real folders, no filesystem path, distinct visual style
- `Directory` and `File` — existing behavior unchanged, lazy-loading subtree intact

### Tree structure in project mode

```
📁 My Project (ProjectRoot)
├── 📂 Source (VirtualFolder)
│   ├── 📁 MyApp/src/ (Directory — real folder)
│   │   ├── main.cs
│   │   └── utils.cs
│   └── 📁 SharedLib/src/ (Directory — real folder)
│       └── helper.cs
├── 📂 Tests (VirtualFolder)
│   ├── 📁 MyApp/tests/ (Directory — real folder)
│   │   └── main_test.cs
│   └── 📁 SharedLib/tests/ (Directory — real folder)
│       └── helper_test.cs
└── 📁 Docs (Directory — unassigned real folder at root)
```

### Context menus (right-click)

- **Project root**: "Add Folder...", "New Virtual Folder", "Close Project"
- **Virtual folder**: "Add Folder...", "Rename", "Remove Virtual Folder"
- **Real folder (top-level in project)**: "Move to Virtual Folder > [list]", "Remove from Project"
- **Files/directories within a real folder**: no project-specific actions

## MainWindow Integration

### File menu additions

Four new items inserted below "Close Folder", with a separator:

- "New Project"
- "Open Project..."
- "Save Project" (disabled when no project open)
- "Close Project" (disabled when no project open)

### Mode exclusivity

- Opening a project closes any open single folder
- Opening a single folder closes any open project
- The explorer panel visibility and side settings are shared

### Tab lifecycle

**Opening a project:**
1. Close all current tabs (prompt to save dirty ones)
2. Restore tabs from `Project.Session`
3. If no session tabs exist, open a single empty tab

**Closing a project:**
1. Snapshot current tabs into `Project.Session`
2. Save the `.vproj` file
3. Close all tabs, open a single empty tab
4. Return explorer to empty state

### App shutdown with project open

- Save session state into the `.vproj` file (not `AppSettings.Session`)
- Store `LastOpenProjectPath` in `AppSettings` so the project is reopened on next launch

### Command palette

Add entries: New Project, Open Project, Save Project, Close Project.

## What's NOT in scope

- Keyboard shortcuts for project actions (can be added later)
- Drag-and-drop from Windows Explorer to add folders
- Recent projects submenu
- Project-specific editor settings (theme, font, tab size overrides)
- Nested virtual folders (virtual folders are single-level only)
