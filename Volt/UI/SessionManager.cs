namespace Volt;

internal record RestoredTab(
    string? FilePath, bool IsDirty, string? SavedContent,
    int CaretLine, int CaretCol, double ScrollVertical, double ScrollHorizontal);

internal record RestoredSession(List<RestoredTab> Tabs, int ActiveTabIndex);

internal class SessionManager
{
    /// <summary>
    /// Builds a SessionSettings object from the current tab state.
    /// Saves dirty/untitled tab content to the session directory.
    /// The caller is responsible for calling SessionSettings.ClearSessionDir() before this method.
    /// </summary>
    public SessionSettings SaveSession(IReadOnlyList<TabInfo> tabs, TabInfo? activeTab, string? folderPath = null)
    {
        var result = new SessionSettings();
        int activeIdx = 0;

        foreach (var t in tabs)
        {
            bool dirty = t.Editor.IsDirty;
            bool untitled = t.FilePath == null;

            // Skip empty untitled tabs
            if (untitled && !dirty && string.IsNullOrEmpty(t.Editor.GetContent()))
                continue;

            if (t == activeTab)
                activeIdx = result.Tabs.Count;

            int idx = result.Tabs.Count;

            // Save content for dirty or untitled tabs
            if (dirty || untitled)
            {
                if (folderPath != null)
                    result.SaveFolderTabContent(folderPath, idx, t.Editor.GetContent());
                else
                    result.SaveTabContent(idx, t.Editor.GetContent());
            }

            result.Tabs.Add(new SessionTab
            {
                FilePath = t.FilePath,
                IsDirty = dirty,
                CaretLine = t.Editor.CaretLine,
                CaretCol = t.Editor.CaretCol,
                ScrollVertical = t.Editor.VerticalOffset,
                ScrollHorizontal = t.Editor.HorizontalOffset,
            });
        }

        result.ActiveTabIndex = activeIdx;
        return result;
    }

    /// <summary>
    /// Reads session data and returns a data structure describing what tabs to restore.
    /// Does NOT create any UI elements — the caller is responsible for creating tabs from the result.
    /// </summary>
    public RestoredSession RestoreSession(SessionSettings session, string? folderPath = null)
    {
        var restoredTabs = new List<RestoredTab>();
        int activeTabIndex = 0;
        int tabIndex = 0;

        foreach (var st in session.Tabs)
        {
            // Skip file-backed tabs whose file no longer exists and aren't dirty
            if (st.FilePath != null && !st.IsDirty && !System.IO.File.Exists(st.FilePath))
            {
                tabIndex++;
                continue;
            }

            string? filePath = st.FilePath;
            bool isDirty = false;
            string? savedContent = null;

            if (st.FilePath != null && System.IO.File.Exists(st.FilePath))
            {
                if (st.IsDirty)
                {
                    savedContent = folderPath != null
                        ? SessionSettings.LoadFolderTabContent(folderPath, tabIndex)
                        : SessionSettings.LoadTabContent(tabIndex);
                    isDirty = true;
                }
            }
            else if (st.FilePath == null)
            {
                savedContent = folderPath != null
                    ? SessionSettings.LoadFolderTabContent(folderPath, tabIndex)
                    : SessionSettings.LoadTabContent(tabIndex);
                if (savedContent != null)
                    isDirty = true;
            }
            else
            {
                savedContent = folderPath != null
                    ? SessionSettings.LoadFolderTabContent(folderPath, tabIndex)
                    : SessionSettings.LoadTabContent(tabIndex);
                if (savedContent == null)
                {
                    tabIndex++;
                    continue;
                }
                filePath = null;
                isDirty = true;
            }

            restoredTabs.Add(new RestoredTab(
                filePath, isDirty, savedContent,
                st.CaretLine, st.CaretCol, st.ScrollVertical, st.ScrollHorizontal));

            if (tabIndex == session.ActiveTabIndex)
                activeTabIndex = restoredTabs.Count - 1;

            tabIndex++;
        }

        return new RestoredSession(restoredTabs, activeTabIndex);
    }
}
