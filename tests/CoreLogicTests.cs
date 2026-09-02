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
    public void AutoCollapsePreferenceSurvivesRestart()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "FlankNote.Tests", System.Guid.NewGuid().ToString("N"));
        var previous = Settings.AutoCollapseNote;
        try
        {
            Settings.AutoCollapseNote = true;
            new NotesStore(directory).Save();
            Settings.AutoCollapseNote = false;

            new NotesStore(directory).Load();

            Assert.True(Settings.AutoCollapseNote);
        }
        finally
        {
            Settings.AutoCollapseNote = previous;
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NoteTransparencyPreferenceSurvivesRestart()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "FlankNote.Tests", System.Guid.NewGuid().ToString("N"));
        var previous = Settings.NoteTransparency;
        try
        {
            Settings.NoteTransparency = -0.55;
            new NotesStore(directory).Save();
            Settings.NoteTransparency = 0;

            new NotesStore(directory).Load();

            Assert.Equal(-0.55, Settings.NoteTransparency, 3);
        }
        finally
        {
            Settings.NoteTransparency = previous;
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(-1, -0.70)]
    [InlineData(0.65, 0.65)]
    [InlineData(2, 0.70)]
    [InlineData(double.NaN, 0)]
    public void NoteTransparencyIsClampedToSupportedRange(double value, double expected)
    {
        Assert.Equal(expected, Settings.ClampNoteTransparency(value), 3);
    }

    [Fact]
    public void TransparencyControlIsCenteredOnTheDefaultMaterial()
    {
        Assert.Equal(0.50, Settings.TransparencyControlValue(0), 3);
        Assert.Equal(-0.70, Settings.NoteTransparencyFromControl(0), 3);
        Assert.Equal(0, Settings.NoteTransparencyFromControl(0.50), 3);
        Assert.Equal(0.70, Settings.NoteTransparencyFromControl(1), 3);
    }

    [Fact]
    public void LegacyOpacitySettingMigratesToTransparency()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "FlankNote.Tests", System.Guid.NewGuid().ToString("N"));
        var previous = Settings.NoteTransparency;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(directory, "notes.json"),
                "{\"notes\":[],\"noteOpacity\":0.55}");

            new NotesStore(directory).Load();

            Assert.Equal(0.45, Settings.NoteTransparency, 3);
        }
        finally
        {
            Settings.NoteTransparency = previous;
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CustomColourAndIndependentWindowSizesSurviveRestart()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "FlankNote.Tests", System.Guid.NewGuid().ToString("N"));
        try
        {
            var store = new NotesStore(directory);
            var first = store.Create("First");
            first.CustomColor = "#123456";
            first.WindowWidth = 520;
            first.WindowHeight = 430;
            store.Update(first);

            var second = store.Create("Second");
            second.WindowWidth = 700;
            second.WindowHeight = 650;
            store.Update(second);

            var reloaded = new NotesStore(directory);
            reloaded.Load();
            var restoredFirst = reloaded.ById(first.Id);
            var restoredSecond = reloaded.ById(second.Id);

            Assert.NotNull(restoredFirst);
            Assert.NotNull(restoredSecond);
            Assert.Equal("#123456", restoredFirst!.CustomColor);
            Assert.Equal(520, restoredFirst.WindowWidth);
            Assert.Equal(430, restoredFirst.WindowHeight);
            Assert.Equal(700, restoredSecond!.WindowWidth);
            Assert.Equal(650, restoredSecond.WindowHeight);
        }
        finally
        {
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CustomColourUsesAdaptiveReadableInk()
    {
        var dark = new Note { CustomColor = "#102030" }.Palette;
        var light = new Note { CustomColor = "#F4E8C8" }.Palette;

        Assert.True(dark.Paper.R > dark.Dash.R);
        Assert.True(dark.Ink.R < dark.Paper.R);
        Assert.True(light.Ink.R < light.Paper.R);
        Assert.Equal("#102030", NoteColor.ToHex(dark.Dash));
    }

    [Fact]
    public void NoteWindowSizeIsClampedToConfiguredAndDisplayLimits()
    {
        var workArea = new System.Windows.Rect(0, 0, 1920, 1080);
        var tooSmall = Geom.WindowSize(new Note { WindowWidth = 10, WindowHeight = 10 }, workArea);
        var tooLarge = Geom.WindowSize(new Note { WindowWidth = 5000, WindowHeight = 5000 }, workArea);

        Assert.Equal(Geom.WindowMinWidth, tooSmall.Width);
        Assert.Equal(Geom.WindowMinHeight, tooSmall.Height);
        Assert.Equal(Geom.WindowMaxWidth, tooLarge.Width);
        Assert.Equal(Geom.WindowMaxHeight, tooLarge.Height);
    }

    [Fact]
    public void DefaultNoteWindowSizeFitsTheCurrentDisplay()
    {
        var desktop = Geom.DefaultWindowSize(new System.Windows.Rect(0, 0, 1920, 1080));
        var compact = Geom.DefaultWindowSize(new System.Windows.Rect(0, 0, 300, 230));

        Assert.Equal(Geom.EditorWidth + Geom.WindowInset, desktop.Width);
        Assert.Equal(Geom.EditorHeight + Geom.WindowInset, desktop.Height);
        Assert.True(compact.Width <= 300);
        Assert.True(compact.Height <= 230);
    }

    [Fact]
    public void NoteResizeHitTestKeepsTheScreenEdgeFixed()
    {
        var size = new System.Windows.Size(500, 400);

        Assert.Equal(Native.HTLEFT,
            NoteWindow.ResizeHitTest(onRight: true, size, new System.Windows.Point(4, 200)));
        Assert.Equal(Native.HTCLIENT,
            NoteWindow.ResizeHitTest(onRight: true, size, new System.Windows.Point(496, 200)));
        Assert.Equal(Native.HTRIGHT,
            NoteWindow.ResizeHitTest(onRight: false, size, new System.Windows.Point(496, 200)));
        Assert.Equal(Native.HTCLIENT,
            NoteWindow.ResizeHitTest(onRight: false, size, new System.Windows.Point(4, 200)));
        Assert.Equal(Native.HTBOTTOMLEFT,
            NoteWindow.ResizeHitTest(onRight: true, size, new System.Windows.Point(4, 396)));
    }

    [Fact]
    public void DeckStylePreferenceSurvivesRestart()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "FlankNote.Tests", System.Guid.NewGuid().ToString("N"));
        var previous = Settings.DeckStyle;
        try
        {
            Settings.DeckStyle = "chips";
            new NotesStore(directory).Save();
            Settings.DeckStyle = "tabs";

            new NotesStore(directory).Load();

            Assert.Equal("chips", Settings.DeckStyle);
        }
        finally
        {
            Settings.DeckStyle = previous;
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
    public void LaterPatchVersionIsNewerThanCurrentVersion()
    {
        Assert.True(GitHubUpdateService.IsNewer("v1.0.1"));
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
