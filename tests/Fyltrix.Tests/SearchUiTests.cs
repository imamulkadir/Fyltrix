using System.Diagnostics;
using System.Windows.Threading;
using Fyltrix.Core;
using Fyltrix.UI;

namespace Fyltrix.Tests;

/// <summary>The search box in the real MainWindow: recursive search, scope, cancel, and acting on results.</summary>
[Collection("UI")]
public class SearchUiTests
{
    private static void Projects(TempDir t)
    {
        t.File("Projects/ProjectA/src/App.jsx", "export default App");
        t.File("Projects/ProjectA/README.md", "# A");
        t.File("Projects/ProjectB/assets/logo.png", "png");
        t.File("Projects/ProjectC/documentation.pdf", "%PDF");
        t.File("Projects/ProjectC/one/two/three/deep-target.txt", "found me");
        t.File("Projects/top.txt", "top");
    }

    [Fact]
    public void Enter_searches_all_subfolders_for_files_and_folders()
    {
        using var t = new TempDir();
        Projects(t);
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t["Projects"]);
            try
            {
                var view = w.View!;
                Assert.Equal("Subfolders", w.SearchScopeLabel);
                Assert.Equal("Search Projects and subfolders", w.SearchHintText);
                Sta.Render(w.SearchBoxElement, "search-box-2x", scale: 2);

                string[] Search(string q)
                {
                    w.TypeSearch(q);
                    w.PressEnterInSearch();
                    Sta.WaitUntil(() => view.IsSearchResults && !view.IsSearching, "search for " + q);
                    return view.Items.Select(i => i.FullPath).ToArray();
                }

                Assert.Equal(new[] { t["Projects/ProjectB/assets/logo.png"] }, Search("logo"));
                Assert.Equal(new[] { t["Projects/ProjectA/src/App.jsx"] }, Search("App"));
                Assert.Equal(new[] { t["Projects/ProjectC/one/two/three/deep-target.txt"] }, Search("deep-target")); // three folders down
                var folder = Search("ProjectB");
                Assert.Equal(new[] { t["Projects/ProjectB"] }, folder);
                Assert.True(view.Items.Single().IsDirectory);
                Assert.Contains(t["Projects/ProjectA/README.md"], Search("*.md"));
                Sta.Render(w, "search-recursive");

                // open a folder result in Fyltrix (folders only: opening a file would start another app on the desktop);
                // then "Open file location" for a file result
                Search("ProjectB");
                var projectB = view.Items.Single();
                Assert.True(projectB.IsDirectory);
                w.OpenItem(view, projectB);
                Sta.WaitUntil(() => !view.IsSearchResults && view.CurrentPath == t["Projects/ProjectB"], "folder result opened");
                Search("logo");
                var logo = view.Items.Single();
                var menu = w.BuildContextMenu(view, new List<FileItem> { logo });
                MainWindowUiTests.Click(MainWindowUiTests.Item(menu, "Open file location"));
                Sta.WaitUntil(() => !view.IsSearchResults && view.CurrentPath == t["Projects/ProjectB/assets"], "file location");
                Sta.WaitUntil(() => view.SelectedItems.Any(i => i.Name == "logo.png"), "file selected in its folder");

                // Esc / clear ends results and returns to the folder
                view.Navigate(t["Projects"]);
                Sta.WaitUntil(() => view.CurrentPath == t["Projects"] && view.Items.Count > 0, "back in Projects");
                Search("logo");
                w.ClickSearchClearOrStop();
                Sta.WaitUntil(() => !view.IsSearchResults && view.Items.Any(i => i.Name == "ProjectA"), "results cleared");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void This_folder_only_scope_filters_without_searching_subfolders_and_ctrl_enter_opens_a_tab()
    {
        using var t = new TempDir();
        Projects(t);
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t["Projects"], s => s.SearchScope = SearchScope.CurrentFolder);
            try
            {
                var view = w.View!;
                Assert.Equal("This folder", w.SearchScopeLabel);
                Assert.Equal("Filter Projects", w.SearchHintText);
                w.TypeSearch("logo");
                w.PressEnterInSearch();
                Sta.DoEvents(300);
                Assert.False(view.IsSearchResults);
                Assert.Empty(System.Windows.Data.CollectionViewSource.GetDefaultView(view.Items).Cast<FileItem>());
                w.TypeSearch("top");
                Sta.WaitUntil(() => System.Windows.Data.CollectionViewSource.GetDefaultView(view.Items).Cast<FileItem>().Select(i => i.Name).SequenceEqual(new[] { "top.txt" }), "quick filter");

                w.Settings.SearchScope = SearchScope.Subfolders;
                w.TypeSearch("logo");
                w.PressEnterInSearch(ctrl: true);
                Sta.WaitUntil(() => w.View != view && w.View!.IsSearchResults && !w.View.IsSearching, "results in a new tab");
                Assert.Equal(new[] { "logo.png" }, w.View!.Items.Select(i => i.Name));
                Assert.False(view.IsSearchResults); // the original tab is untouched
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void A_large_search_can_be_stopped_and_the_window_stays_responsive()
    {
        using var t = new TempDir();
        for (int d = 0; d < 300; d++)
        {
            var dir = Directory.CreateDirectory(t[$"big/d{d:000}/inner"]).FullName;
            for (int f = 0; f < 40; f++) File.WriteAllText(Path.Combine(dir, $"file{f:00}.txt"), "");
        }
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t["big"]);
            try
            {
                var view = w.View!;
                w.TypeSearch("file");
                w.PressEnterInSearch();
                Assert.True(view.IsSearching);
                Sta.DoEvents(30); // let the first results land
                Assert.True(w.SearchStopVisible || !view.IsSearching);

                // the UI thread keeps answering while the search runs on a worker
                var sw = Stopwatch.StartNew();
                w.Dispatcher.Invoke(() => { }, DispatcherPriority.Input);
                Assert.True(sw.ElapsedMilliseconds < 500, $"UI thread blocked for {sw.ElapsedMilliseconds} ms");

                bool stopped = view.IsSearching;
                if (stopped) w.ClickSearchClearOrStop(); // the stop button
                Assert.False(view.IsSearching);
                int after = view.Items.Count;
                Sta.DoEvents(600);
                Assert.Equal(after, view.Items.Count); // nothing more arrives after stopping
                if (stopped) Assert.True(after < 12000, $"{after} results after stopping");
                else Assert.Equal(12000, after); // finished first: every match (more than the old 10 000 cap)
                Assert.True(view.IsSearchResults); // partial results stay
            }
            finally { w.Close(); }
        });
    }
}
