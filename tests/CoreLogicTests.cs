using Xunit;
using System.Linq;
using System.Windows.Documents;

namespace FlankNote.Tests;

public sealed class CoreLogicTests
{
    [Fact]
    public void MarkdownPreviewRendersCommonMarkdownBlocks()
    {
        var document = MarkdownPreview.CreateDocument(
            "# Release notes\n\n- [x] Done\n- [ ] Next\n\n> Important\n\n```text\ncode\n```\n\n| Name | Value |\n| --- | --- |\n| One | Two |",
            NoteColor.At(0));

        Assert.Contains(document.Blocks.OfType<Paragraph>(), block => block.FontSize > document.FontSize);
        Assert.Single(document.Blocks.OfType<List>());
        Assert.Single(document.Blocks.OfType<Section>());
        Assert.Single(document.Blocks.OfType<Table>());
        string visibleText = new TextRange(document.ContentStart, document.ContentEnd).Text;
        Assert.Contains("☑", visibleText);
        Assert.Contains("code", visibleText);
    }

    [Fact]
    public void MarkdownPreviewRendersDashListsAsNativeListMarkers()
    {
        var document = MarkdownPreview.CreateDocument("- first\n- second", NoteColor.At(0));

        var list = Assert.Single(document.Blocks.OfType<List>());
        Assert.Equal(System.Windows.TextMarkerStyle.Disc, list.MarkerStyle);
        string visibleText = new TextRange(document.ContentStart, document.ContentEnd).Text;
        Assert.Contains("first", visibleText);
        Assert.DoesNotContain("- first", visibleText);
    }

    [Fact]
    public void PlainTextPreviewKeepsMarkdownCharactersLiteral()
    {
        var document = MarkdownPreview.CreatePlainTextDocument(
            "# 中文标题\n- 普通横线\n---", NoteColor.At(0));

        string visibleText = new TextRange(document.ContentStart, document.ContentEnd).Text;
        Assert.Contains("# 中文标题", visibleText);
        Assert.Contains("- 普通横线", visibleText);
        Assert.Contains("---", visibleText);
        Assert.Empty(document.Blocks.OfType<List>());
    }

    [Fact]
    public void TaskProgressCountsOpenAndDoneLines()
    {
        var result = Tasks.Progress("☐ one\n☑ two\nplain");
        Assert.Equal(1, result.Done);
        Assert.Equal(2, result.Total);
    }

    [Fact]
    public void PlainTextModeIgnoresMarkdownTaskSyntax()
    {
        var result = Tasks.Progress("- [x] Markdown\n☐ Native", markdownEnabled: false);

        Assert.Equal(0, result.Done);
        Assert.Equal(1, result.Total);
        Assert.Equal("- [ ] literal", Tasks.Strip("- [ ] literal", markdownEnabled: false));
    }

    [Fact]
    public void ToggleTaskRoundTrips()
    {
        Assert.Equal("☑ item", Tasks.Toggle("☐ item"));
        Assert.Equal("☐ item", Tasks.Toggle("☑ item"));
        Assert.Equal("☑ item", Tasks.Toggle("item"));
    }

    [Fact]
    public void MarkdownTaskCheckboxesAreRecognizedAndPreserved()
    {
        Assert.True(Tasks.IsOpen("- [ ] item"));
        Assert.True(Tasks.IsDone("- [x] item"));
        Assert.True(Tasks.IsOpen("1. [ ] item"));
        Assert.True(Tasks.IsDone("2) [X] item"));
        Assert.Equal("item", Tasks.Strip("- [ ] item"));
        Assert.Equal("item", Tasks.Strip("1. [ ] item"));
        Assert.Equal("- [x] item", Tasks.Toggle("- [ ] item"));
        Assert.Equal("- [ ] item", Tasks.Toggle("- [x] item"));
        Assert.Equal("2) [ ] item", Tasks.Toggle("2) [X] item"));
        Assert.Equal("- [ ] next", Tasks.Continuation("- [x] item", "next"));
        Assert.Equal("1. [ ] next", Tasks.Continuation("1. [x] item", "next"));
        Assert.Equal(7, Tasks.ContentOffset("1. [ ] item"));
        Assert.True(Tasks.IsMarkerOffset("1. [ ] item", 4));
        Assert.False(Tasks.IsMarkerOffset("1. [ ] item", 7));
    }

    [Fact]
    public void MarkdownBulletsExposeTheirSourceMarkerPosition()
    {
        Assert.True(Markdown.TryGetBullet("  - item", out var markerIndex));
        Assert.Equal(2, markerIndex);
        Assert.False(Markdown.TryGetBullet("☐ item", out _));
        Assert.False(Markdown.TryGetBullet("- - -", out _));
        Assert.False(Markdown.CanBeSetextHeadingText("- item"));
        Assert.False(Markdown.CanBeSetextHeadingText("1. item"));
        Assert.False(Markdown.CanBeSetextHeadingText("> quote"));
        Assert.True(Markdown.CanBeSetextHeadingText("plain title"));
        Assert.True(Markdown.TryGetFenceOpening("```csharp", out var fenceLength));
        Assert.Equal(3, fenceLength);
        Assert.True(Markdown.IsFenceClosing("```", fenceLength));
    }

    [Fact]
    public void MarkdownBulletRendersOutsideCaretLineAndRestoresAtCaret()
    {
        var document = new FlowDocument();
        var first = new Paragraph(new Run("- first"));
        var second = new Paragraph(new Run("* second"));
        document.Blocks.Add(first);
        document.Blocks.Add(second);

        Markdown.StyleDocument(document, NoteColor.At(0), 14, first);

        Assert.Equal("- first", new TextRange(first.ContentStart, first.ContentEnd).Text);
        Assert.Equal("• second", new TextRange(second.ContentStart, second.ContentEnd).Text);
        Assert.Equal("* second", Markdown.SourceText(second));

        Markdown.StyleDocument(document, NoteColor.At(0), 14, second);

        Assert.Equal("• first", new TextRange(first.ContentStart, first.ContentEnd).Text);
        Assert.Equal("- first", Markdown.SourceText(first));
        Assert.Equal("* second", new TextRange(second.ContentStart, second.ContentEnd).Text);
        Assert.Equal("* second", Markdown.SourceText(second));
    }

    [Fact]
    public void ThematicBreakShowsOnlySourceAtCaret()
    {
        var document = new FlowDocument();
        var paragraph = new Paragraph(new Run("---"));
        document.Blocks.Add(paragraph);

        Markdown.StyleDocument(document, NoteColor.At(0), 14, paragraph);

        Assert.Equal(0, paragraph.BorderThickness.Bottom);
        Assert.Equal("---", new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text);

        Markdown.StyleDocument(document, NoteColor.At(0), 14, activeParagraph: null);

        Assert.Equal(1, paragraph.BorderThickness.Bottom);
        var range = new TextRange(paragraph.ContentStart, paragraph.ContentEnd);
        Assert.Equal(0.1, (double)range.GetPropertyValue(TextElement.FontSizeProperty), 3);

        Markdown.StyleDocument(document, NoteColor.At(0), 14, paragraph);

        Assert.Equal(0, paragraph.BorderThickness.Bottom);
        Assert.Equal(14, (double)range.GetPropertyValue(TextElement.FontSizeProperty), 3);
    }

    [Fact]
    public void MarkdownHeadingMarkersRenderOutsideCaretLine()
    {
        var document = new FlowDocument();
        var paragraph = new Paragraph(new Run("## Heading"));
        document.Blocks.Add(paragraph);

        Markdown.StyleDocument(document, NoteColor.At(0), 14, paragraph);

        var marker = new TextRange(
            Markdown.PositionAtTextOffset(paragraph, 0),
            Markdown.PositionAtTextOffset(paragraph, 2));
        Assert.Equal(14, (double)marker.GetPropertyValue(TextElement.FontSizeProperty), 3);

        Markdown.StyleDocument(document, NoteColor.At(0), 14, activeParagraph: null);

        Assert.Equal(0.1, (double)marker.GetPropertyValue(TextElement.FontSizeProperty), 3);
        var heading = new TextRange(
            Markdown.PositionAtTextOffset(paragraph, 3),
            Markdown.PositionAtTextOffset(paragraph, 10));
        Assert.Equal(18, (double)heading.GetPropertyValue(TextElement.FontSizeProperty), 3);
    }

    [Fact]
    public void MarkdownCaretLinePreservesChineseSourceText()
    {
        var document = new FlowDocument();
        var paragraph = new Paragraph(new Run("# 中文输入法"));
        document.Blocks.Add(paragraph);

        Markdown.StyleDocument(document, NoteColor.At(0), 14, paragraph);

        Assert.Equal("# 中文输入法", Markdown.SourceText(paragraph));
        Assert.Equal("# 中文输入法", new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text);
    }

    [Fact]
    public void DeriveTitleRemovesHeadingAndTaskMarker()
    {
        var note = new Note { Body = "## ☐ Plan the release\nDetails", MarkdownEnabled = true };
        note.DeriveTitle();
        Assert.Equal("Plan the release", note.Title);
    }

    [Fact]
    public void PlainTextTitleKeepsLeadingHash()
    {
        var note = new Note { Body = "# 中文标题", MarkdownEnabled = false };

        note.DeriveTitle();

        Assert.Equal("# 中文标题", note.Title);

        note.Body = "- [ ] 原样文本";
        note.DeriveTitle();
        Assert.Equal("- [ ] 原样文本", note.Title);
    }

    [Fact]
    public void PerNoteTextModeSurvivesRestart()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "FlankNote.Tests", System.Guid.NewGuid().ToString("N"));
        bool previous = Settings.Markdown;
        try
        {
            Settings.Markdown = true;
            var store = new NotesStore(directory);
            var markdownNote = store.Create("# Markdown");
            var plainNote = store.Create("# 纯文本");
            plainNote.MarkdownEnabled = false;
            store.Update(plainNote);

            var reloaded = new NotesStore(directory);
            reloaded.Load();

            Assert.True(reloaded.ById(markdownNote.Id)!.UsesMarkdown);
            Assert.False(reloaded.ById(plainNote.Id)!.UsesMarkdown);
        }
        finally
        {
            Settings.Markdown = previous;
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NewNotesUseCurrentMarkdownDefault(bool markdownDefault)
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "FlankNote.Tests", System.Guid.NewGuid().ToString("N"));
        bool previous = Settings.Markdown;
        try
        {
            Settings.Markdown = markdownDefault;

            var note = new NotesStore(directory).Create("# text");

            Assert.Equal(markdownDefault, note.UsesMarkdown);
            Assert.Equal(markdownDefault ? "text" : "# text", note.Title);
        }
        finally
        {
            Settings.Markdown = previous;
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacyNotesInheritSavedMarkdownDefault(bool markdownDefault)
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "FlankNote.Tests", System.Guid.NewGuid().ToString("N"));
        bool previous = Settings.Markdown;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(directory, "notes.json"),
                $"{{\"notes\":[{{\"Id\":\"legacy\",\"Body\":\"# text\"}}],\"markdown\":{markdownDefault.ToString().ToLowerInvariant()}}}");

            var store = new NotesStore(directory);
            store.Load();

            Assert.Equal(markdownDefault, store.ById("legacy")!.UsesMarkdown);
            Assert.Equal(markdownDefault, store.ById("legacy")!.MarkdownEnabled);
        }
        finally
        {
            Settings.Markdown = previous;
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
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
    public void OverlayFullscreenPreferenceSurvivesRestart()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "FlankNote.Tests", System.Guid.NewGuid().ToString("N"));
        var previous = Settings.OverlayFullscreen;
        try
        {
            Settings.OverlayFullscreen = false;
            new NotesStore(directory).Save();
            Settings.OverlayFullscreen = true;

            new NotesStore(directory).Load();

            Assert.False(Settings.OverlayFullscreen);
        }
        finally
        {
            Settings.OverlayFullscreen = previous;
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
                <content type="html">&lt;h2&gt;新增&lt;/h2&gt;&lt;ul&gt;&lt;li&gt;支持列表&lt;/li&gt;&lt;li&gt;修复问题&lt;/li&gt;&lt;/ul&gt;</content>
              </entry>
            </feed>
            """);

        var release = GitHubUpdateService.ParseFeed(document);

        Assert.NotNull(release);
        Assert.Equal("v1.0.0", release.TagName);
        Assert.Equal("FlankNote v1.0.0", release.Name);
        Assert.Equal("## 新增\n- 支持列表\n- 修复问题", release.Body);
        Assert.Equal("https://github.com/nostalgicz/FlankNote/releases/tag/v1.0.0", release.HtmlUrl);
    }

    [Fact]
    public void LaterPatchVersionIsNewerThanCurrentVersion()
    {
        var current = typeof(GitHubUpdateService).Assembly.GetName().Version ?? new System.Version(0, 0, 0);
        var later = new System.Version(current.Major, current.Minor, current.Build + 1);
        Assert.True(GitHubUpdateService.IsNewer($"v{later}"));
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
