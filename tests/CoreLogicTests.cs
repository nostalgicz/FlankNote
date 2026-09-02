using Xunit;

namespace FlankNote.Tests;

public sealed class CoreLogicTests
{
    [Fact]
    public void TaskProgressCountsOpenAndDoneLines()
    {
        var result = Tasks.Progress("☐ one\n☑ two\nplain");
        Assert.Equal(1, result.Done);
        Assert.Equal(2, result.Total);
    }

    [Fact]
    public void ToggleTaskRoundTrips()
    {
        Assert.Equal("☑ item", Tasks.Toggle("☐ item"));
        Assert.Equal("☐ item", Tasks.Toggle("☑ item"));
        Assert.Equal("☑ item", Tasks.Toggle("item"));
    }

    [Fact]
    public void DeriveTitleRemovesHeadingAndTaskMarker()
    {
        var note = new Note { Body = "## ☐ Plan the release\nDetails" };
        note.DeriveTitle();
        Assert.Equal("Plan the release", note.Title);
    }

    [Fact]
    public void CustomTitleSurvivesBodyUpdate()
    {
        var note = new Note { Body = "Original body", Title = "My title", HasCustomTitle = true };
        NotesStore.I.Update(note);
        note.Body = "A different first line";
        NotesStore.I.Update(note);
        Assert.Equal("My title", note.Title);
    }

    [Fact]
    public void AutoTitleTracksBodyUntilUserEditsTitle()
    {
        var note = new Note { Body = "First line" };
        note.DeriveTitle();
        Assert.Equal("First line", note.Title);

        note.Body = "Second line";
        NotesStore.I.Update(note);
        Assert.Equal("Second line", note.Title);

        note.Title = "Pinned title";
        note.HasCustomTitle = true;
        note.Body = "Third line";
        NotesStore.I.Update(note);
        Assert.Equal("Pinned title", note.Title);
    }

    [Fact]
    public void AutoTitleDoesNotBecomeCustomWhenOnlyBodyChanges()
    {
        var note = new Note { Body = "First line" };
        note.DeriveTitle();
        note.Body = "Second line";
        NotesStore.I.Update(note);
        Assert.False(note.HasCustomTitle);
        Assert.Equal("Second line", note.Title);
    }

    [Fact]
    public void RestoringDeletedArchivedNoteKeepsItArchived()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "FlankNote.Tests", System.Guid.NewGuid().ToString("N"));
        try
        {
            var store = new NotesStore(directory);
            var note = new Note { Archived = true, Order = 0 };

            store.Restore(note, archived: true);

            Assert.True(note.Archived);
            Assert.Contains(note, store.Notes);
            Assert.Empty(store.Active);
        }
        finally
        {
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExistingInstallWithoutWelcomeFieldsDoesNotTriggerWelcomeOnUpgrade()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "FlankNote.Tests", System.Guid.NewGuid().ToString("N"));
        var previous = Settings.FirstRunCompleted;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(directory, "notes.json"),
                "{\"notes\":[]}");
            Settings.FirstRunCompleted = false;

            new NotesStore(directory).Load();

            Assert.True(Settings.FirstRunCompleted);
        }
        finally
        {
            Settings.FirstRunCompleted = previous;
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WelcomeShownMarkerSurvivesRestart()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "FlankNote.Tests", System.Guid.NewGuid().ToString("N"));
        var previous = Settings.FirstRunCompleted;
        try
        {
            Settings.FirstRunCompleted = true;
            new NotesStore(directory).Save();
            Settings.FirstRunCompleted = false;

            new NotesStore(directory).Load();

            Assert.True(Settings.FirstRunCompleted);
        }
        finally
        {
            Settings.FirstRunCompleted = previous;
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ParsesLatestReleaseFromGitHubAtomFeed()
    {
        var document = System.Xml.Linq.XDocument.Parse("""
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>tag:github.com,2008:Repository/1/v1.0.0</id>
                <updated>2026-09-02T06:07:11Z</updated>
                <link rel="alternate" type="text/html" href="https://github.com/nostalgicz/FlankNote/releases/tag/v1.0.0" />
                <title>FlankNote v1.0.0</title>
                <content type="html">&lt;p&gt;Preview version.&lt;/p&gt;</content>
              </entry>
            </feed>
            """);

        var release = GitHubUpdateService.ParseFeed(document);

        Assert.NotNull(release);
        Assert.Equal("v1.0.0", release.TagName);
        Assert.Equal("FlankNote v1.0.0", release.Name);
        Assert.Equal("Preview version.", release.Body);
        Assert.Equal("https://github.com/nostalgicz/FlankNote/releases/tag/v1.0.0", release.HtmlUrl);
    }

    [Fact]
    public void PublishedV1IsNewerThanLocalTestVersion()
    {
        Assert.True(GitHubUpdateService.IsNewer("v1.0.0"));
    }

    [Fact]
    public async System.Threading.Tasks.Task InstallerDownloadIsPromotedOnlyAfterTheFileIsClosed()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "FlankNote.Tests", System.Guid.NewGuid().ToString("N"));
        var destination = System.IO.Path.Combine(directory, "FlankNote-Setup-x64.exe");
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            await using var source = new System.IO.MemoryStream(payload);

            await GitHubUpdateService.SaveInstallerAsync(source, destination, payload.Length);

            Assert.Equal(payload, await System.IO.File.ReadAllBytesAsync(destination));
            Assert.False(System.IO.File.Exists(destination + ".download"));
        }
        finally
        {
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }
}
