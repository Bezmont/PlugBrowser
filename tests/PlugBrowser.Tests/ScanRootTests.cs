using PlugBrowser.App.ViewModels;
using PlugBrowser.Core.Discovery;
using PlugBrowser.Core.Model;
using PlugBrowser.Core.Probing;
using PlugBrowser.Core.Storage;
using Xunit;

namespace PlugBrowser.Tests;

public class ScanRootProviderTests
{
    [Fact]
    public void GetRoots_IncludesConventionalLocations_EnabledByDefault()
    {
        using var tree = new TempTree();
        using var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));

        var roots = ScanRootProvider.GetRoots(store);

        Assert.NotEmpty(roots);
        Assert.All(roots, r => Assert.True(r.Enabled));
        Assert.Contains(roots, r => r.Origin == ScanRootOrigin.Convention);
    }

    /// <summary>Conventional roots are rediscovered every launch, so only the user's decision about
    /// them is stored. Losing that would silently re-enable folders they had switched off.</summary>
    [Fact]
    public void DisablingAConventionalRoot_Persists()
    {
        using var tree = new TempTree();
        string dbPath = Path.Combine(tree.Root, "catalog.db");
        string target;

        using (var store = CatalogStore.Open(dbPath))
        {
            target = ScanRootProvider.GetRoots(store).First(r => r.Origin == ScanRootOrigin.Convention).Path;
            store.SetScanRootEnabled(target, enabled: false, ScanRootOrigin.Convention);
        }

        using (var store = CatalogStore.Open(dbPath))
        {
            var root = ScanRootProvider.GetRoots(store).Single(r => r.Path == target);
            Assert.False(root.Enabled);
        }
    }

    [Fact]
    public void UserRoots_AreAddedRemovableAndPersisted()
    {
        using var tree = new TempTree();
        string dbPath = Path.Combine(tree.Root, "catalog.db");
        string custom = tree.Dir("MyPlugins");

        using (var store = CatalogStore.Open(dbPath))
            store.AddUserScanRoot(custom);

        using (var store = CatalogStore.Open(dbPath))
        {
            var root = ScanRootProvider.GetRoots(store)
                .Single(r => r.Path.Equals(custom, StringComparison.OrdinalIgnoreCase));

            Assert.Equal(ScanRootOrigin.User, root.Origin);
            Assert.True(root.CanRemove);
            Assert.True(root.Exists);

            store.RemoveUserScanRoot(custom);
            Assert.DoesNotContain(ScanRootProvider.GetRoots(store),
                r => r.Path.Equals(custom, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Conventional roots cannot be deleted — they would simply reappear on the next launch,
    /// so the UI offers disabling instead.</summary>
    [Fact]
    public void RemoveUserScanRoot_WillNotDeleteAConventionalRoot()
    {
        using var tree = new TempTree();
        using var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));

        string target = ScanRootProvider.GetRoots(store).First().Path;
        store.SetScanRootEnabled(target, enabled: false, ScanRootOrigin.Convention);
        store.RemoveUserScanRoot(target);

        Assert.False(ScanRootProvider.GetRoots(store).Single(r => r.Path == target).Enabled);
    }

    [Fact]
    public void EnabledRoots_ExcludesDisabledAndMissingFolders()
    {
        using var tree = new TempTree();
        string present = tree.Dir("Present");

        var roots = new[]
        {
            new ScanRoot { Path = present, Origin = ScanRootOrigin.User, Enabled = true, Exists = true },
            new ScanRoot { Path = @"C:\nowhere", Origin = ScanRootOrigin.User, Enabled = true, Exists = false },
            new ScanRoot { Path = present + "2", Origin = ScanRootOrigin.User, Enabled = false, Exists = true },
        };

        Assert.Equal([present], ScanRootProvider.EnabledRoots(roots));
    }

    [Theory]
    [InlineData(@"C:\Plugins\")]
    [InlineData(@"C:\Plugins")]
    [InlineData(@"C:\Plugins\Sub\..")]
    public void Normalize_CollapsesEquivalentSpellings(string path) =>
        Assert.Equal(@"C:\Plugins", ScanRootProvider.Normalize(path));
}

public class ScanServiceTests
{
    /// <summary>The image folder name has to be stable across runs — it is where a plugin's screenshots
    /// live — and distinct for paths that differ only in characters a filename cannot hold.</summary>
    [Fact]
    public void KeyFor_IsStableAndDistinguishesSimilarPaths()
    {
        string a = ScanService.KeyFor(@"C:\Program Files\Common Files\VST3\Comp FET-76.vst3");
        string b = ScanService.KeyFor(@"C:\Program Files\Common Files\VST3\Comp FET-76.vst3");
        string c = ScanService.KeyFor(@"D:\Other\Comp FET-76.vst3");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.DoesNotContain(Path.GetInvalidFileNameChars(), a.Contains);
        // The readable prefix is there so the images folder can be browsed by a human.
        Assert.StartsWith("comp fet-76-", a);
    }

    [Fact]
    public void KeyFor_IsCaseInsensitive_BecauseWindowsPathsAre() =>
        Assert.Equal(
            ScanService.KeyFor(@"C:\VST3\Thing.vst3"),
            ScanService.KeyFor(@"c:\vst3\THING.vst3"));

    /// <summary>
    /// A rescan must not throw away what a previous scan learned.
    /// </summary>
    /// <remarks>
    /// Discovery only knows what the filesystem says, so its entries carry no classes, parameters or
    /// screenshots. Saving those straight over the catalog discarded every previous probe result,
    /// orphaning captured images and making every rescan a full re-probe.
    /// </remarks>
    [Fact]
    public async Task ScanAsync_KeepsPreviousProbeResults_ForUnchangedFiles()
    {
        using var tree = new TempTree();
        using var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));

        // A real PE file so discovery accepts it, with a probe result already recorded against it.
        string dll = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");
        if (!File.Exists(dll))
            return;

        string plugin = Path.Combine(tree.Root, "Thing.dll");
        File.Copy(dll, plugin);
        var info = new FileInfo(plugin);

        store.Save([
            new PluginEntry
            {
                Path = plugin,
                Format = PluginFormat.Vst2,
                Architecture = PluginArchitecture.X64,
                FileSize = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
                State = ProbeState.Probed,
                Classes =
                [
                    new PluginClass
                    {
                        Index = 0,
                        Name = "Previously Probed",
                        ParameterCount = 42,
                        Images =
                        [
                            new PluginImage
                            {
                                Kind = ImageKind.Screenshot,
                                FilePath = @"C:\images	hing.png",
                                Width = 960,
                                Height = 289,
                            },
                        ],
                    },
                ],
            },
        ]);

        var worker = new WorkerRunner(Path.Combine(tree.Root, "no-such-worker.exe"));
        var service = new ScanService(store, worker) { ImageDirectory = tree.Dir("images") };
        await service.ScanAsync([tree.Root]);

        var reloaded = Assert.Single(store.Load());
        Assert.Equal(ProbeState.Probed, reloaded.State);

        var only = Assert.Single(reloaded.Classes);
        Assert.Equal("Previously Probed", only.Name);
        Assert.Equal(42, only.ParameterCount);
        Assert.Single(only.Images);
    }

    /// <summary>
    /// Screenshots for uninstalled plugins must not accumulate forever — a capture is about a megabyte,
    /// so an unpruned cache only ever grows. Anything that is not one of our own folders is left alone.
    /// </summary>
    [Fact]
    public async Task ScanAsync_DeletesImagesForPluginsThatAreGone_ButLeavesStrangersAlone()
    {
        using var tree = new TempTree();
        using var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));

        string images = tree.Dir("images");
        string orphan = Path.Combine(images, ScanService.KeyFor(@"C:\gone\Removed.vst3"));
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "class-0.png"), "stale");

        string stranger = Path.Combine(images, "notes-from-the-user");
        Directory.CreateDirectory(stranger);

        var worker = new WorkerRunner(Path.Combine(tree.Root, "no-such-worker.exe"));
        var service = new ScanService(store, worker) { ImageDirectory = images };
        await service.ScanAsync([tree.Root]);

        Assert.False(Directory.Exists(orphan), "images for an uninstalled plugin should be removed");
        Assert.True(Directory.Exists(stranger), "unrecognised folders must be left untouched");
    }

    /// <summary>Without a worker present the scan must still produce a complete catalog rather than
    /// failing — discovery alone is useful on its own.</summary>
    [Fact]
    public async Task ScanAsync_FallsBackToDiscoveryOnly_WhenWorkerIsMissing()
    {
        using var tree = new TempTree();
        using var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));

        var worker = new WorkerRunner(Path.Combine(tree.Root, "no-such-worker.exe"));
        Assert.False(worker.IsAvailable);

        var service = new ScanService(store, worker) { ImageDirectory = tree.Dir("images") };
        var summary = await service.ScanAsync([tree.Root]);

        Assert.Equal(0, summary.Probed);
        Assert.Equal(0, summary.Failed);
    }
}

public class InspectResultTests
{
    /// <summary>
    /// A hung plugin must come back as timed out, not failed.
    /// </summary>
    /// <remarks>
    /// The worker's watchdog ends the process with <c>Environment.FailFast</c>, which exits with
    /// <c>0xC0000409</c> - the same shape as a crash from the parent's side. Every hung plugin was
    /// therefore filed as "failed to load", which is both wrong and less useful: a timeout is worth
    /// retrying with a longer budget, a crash is not. The flag has to survive the JSON hop.
    /// </remarks>
    [Fact]
    public void TimedOut_SurvivesTheRoundTrip()
    {
        using var tree = new TempTree();
        string path = Path.Combine(tree.Root, "result.json");

        File.WriteAllText(path, new InspectResult
        {
            Path = @"C:\x\Hangs.vst3",
            Loaded = true,
            TimedOut = true,
            Error = "Gave up after 45s.",
        }.ToJson());

        var reloaded = InspectResult.FromJsonFile(path);

        Assert.NotNull(reloaded);
        Assert.True(reloaded.TimedOut);
        Assert.True(reloaded.Loaded);
        Assert.False(reloaded.Enumerated);
    }

    [Fact]
    public void FromJsonFile_ReturnsNull_ForMissingOrCorruptReports()
    {
        using var tree = new TempTree();

        Assert.Null(InspectResult.FromJsonFile(Path.Combine(tree.Root, "absent.json")));
        Assert.Null(InspectResult.FromJsonFile(tree.File("bad.json", "{ not json")));
    }

    /// <summary>Zero classes only means "exposes nothing" once enumeration actually finished.</summary>
    [Fact]
    public void NoClassesExposed_RequiresACompletedEnumeration()
    {
        var killedPartway = new InspectResult { Path = "x", Loaded = true };
        var genuinelyEmpty = new InspectResult { Path = "x", Loaded = true, Enumerated = true };

        Assert.False(killedPartway.NoClassesExposed);
        Assert.True(genuinelyEmpty.NoClassesExposed);
    }
}

public class OptionsViewModelTests
{
    [Fact]
    public void AddRoot_RejectsDuplicates()
    {
        using var tree = new TempTree();
        using var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));
        var viewModel = new OptionsViewModel(store);
        string custom = tree.Dir("Extra");

        Assert.True(viewModel.AddRoot(custom));
        Assert.False(viewModel.AddRoot(custom));
        Assert.Single(viewModel.Roots, r => r.Path.Equals(custom, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TogglingARoot_PersistsImmediately()
    {
        using var tree = new TempTree();
        string dbPath = Path.Combine(tree.Root, "catalog.db");
        string custom = tree.Dir("Extra");

        using (var store = CatalogStore.Open(dbPath))
        {
            var viewModel = new OptionsViewModel(store);
            viewModel.AddRoot(custom);
            viewModel.Roots.Single(r => r.Path.Equals(custom, StringComparison.OrdinalIgnoreCase))
                .IsEnabled = false;
        }

        using (var store = CatalogStore.Open(dbPath))
        {
            var reopened = new OptionsViewModel(store);
            Assert.False(reopened.Roots
                .Single(r => r.Path.Equals(custom, StringComparison.OrdinalIgnoreCase)).IsEnabled);
        }
    }
}

public class AppStateTests
{
    private static PluginEntry Entry(string name, PluginFormat format) => new()
    {
        Path = $@"C:\x\{name}.vst3",
        Format = format,
        Architecture = PluginArchitecture.X64,
        FileVendor = "Acme",
        Classes = [new PluginClass { Index = 0, Name = name, Kind = PluginKind.Effect }],
    };

    [Fact]
    public void FiltersAndGeometry_SurviveARestart()
    {
        using var tree = new TempTree();
        string dbPath = Path.Combine(tree.Root, "catalog.db");

        using (var store = CatalogStore.Open(dbPath))
        {
            store.Save([Entry("A", PluginFormat.Vst3), Entry("B", PluginFormat.Vst2)]);
            var viewModel = new MainWindowViewModel(store);
            viewModel.LoadCatalog();

            viewModel.ShowVst2 = false;
            viewModel.ProblemsOnly = true;
            viewModel.DetailPaneWidth = 520;
            viewModel.SaveState(1100, 700, maximized: false);
        }

        using (var store = CatalogStore.Open(dbPath))
        {
            var viewModel = new MainWindowViewModel(store);
            viewModel.LoadCatalog();
            viewModel.RestoreState();

            Assert.False(viewModel.ShowVst2);
            Assert.True(viewModel.ProblemsOnly);
            Assert.True(viewModel.ShowVst3);

            var geometry = viewModel.SavedWindowGeometry;
            Assert.NotNull(geometry);
            Assert.Equal(1100, geometry.Value.Width);
            Assert.Equal(700, geometry.Value.Height);
            Assert.Equal(520, viewModel.DetailPaneWidth);
        }
    }

    /// <summary>The pane never shrinks below its original width, and a width saved on a large monitor
    /// cannot swallow the gallery on a small one.</summary>
    [Fact]
    public void DetailPaneWidth_IsClampedToAUsableRange()
    {
        using var tree = new TempTree();
        using var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));
        var viewModel = new MainWindowViewModel(store);

        viewModel.DetailPaneWidth = 100;
        Assert.Equal(MainWindowViewModel.MinDetailPaneWidth, viewModel.DetailPaneWidth);

        viewModel.DetailPaneWidth = 5000;
        Assert.Equal(MainWindowViewModel.MaxDetailPaneWidth, viewModel.DetailPaneWidth);
    }

    /// <summary>A geometry saved on a monitor that is no longer attached must not open an unusable
    /// window, so implausible values are discarded rather than applied.</summary>
    [Fact]
    public void SavedWindowGeometry_RejectsImplausibleSizes()
    {
        using var tree = new TempTree();
        using var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));

        store.SetSetting("window.width", "20");
        store.SetSetting("window.height", "20");

        Assert.Null(new MainWindowViewModel(store).SavedWindowGeometry);
    }

    /// <summary>Restoring a vendor filter for a vendor that is no longer installed would open the
    /// window on an empty gallery with no obvious cause.</summary>
    [Fact]
    public void RestoreState_IgnoresAVendorThatIsNoLongerInstalled()
    {
        using var tree = new TempTree();
        using var store = CatalogStore.Open(Path.Combine(tree.Root, "catalog.db"));
        store.Save([Entry("A", PluginFormat.Vst3)]);
        store.SetSetting("filter.vendor", "Vanished Audio Ltd");

        var viewModel = new MainWindowViewModel(store);
        viewModel.LoadCatalog();
        viewModel.RestoreState();

        Assert.Equal(MainWindowViewModel.AnyVendor, viewModel.SelectedVendor);
        Assert.Single(viewModel.Visible);
    }
}

public class AspectRatioTests
{
    private static PluginItemViewModel WithImage(int width, int height) => new(
        new PluginEntry
        {
            Path = @"C:\x\Thing.vst3",
            Format = PluginFormat.Vst3,
            Classes =
            [
                new PluginClass
                {
                    Index = 0,
                    Name = "Thing",
                    Images =
                    [
                        new PluginImage
                        {
                            Kind = ImageKind.Screenshot,
                            FilePath = @"C:\images\thing.png",
                            Width = width,
                            Height = height,
                        },
                    ],
                },
            ],
        },
        isFavorite: false);

    /// <summary>Editor shapes vary enormously — Comp FET-76 is 3.3:1, Analog Lab V is 1.5:1 — so the
    /// card height must follow the image rather than a fixed box.</summary>
    [Theory]
    [InlineData(960, 289)]
    [InlineData(1440, 986)]
    [InlineData(637, 230)]
    public void AspectRatio_MatchesTheCapturedEditor(int width, int height)
    {
        var item = WithImage(width, height);

        Assert.Equal((double)width / height, item.AspectRatio, precision: 5);
        Assert.Equal(Math.Round(PluginItemViewModel.CardImageWidth * height / (double)width),
            item.ThumbnailHeight);
    }

    /// <summary>One pathological plugin should not be able to produce a card tall enough to wreck the
    /// grid, so the ratio is clamped.</summary>
    [Theory]
    [InlineData(10000, 10)]
    [InlineData(10, 10000)]
    public void AspectRatio_IsClamped(int width, int height)
    {
        double ratio = WithImage(width, height).AspectRatio;

        Assert.InRange(ratio, 0.5, 6.0);
    }

    [Fact]
    public void AspectRatio_FallsBackToADefault_WithoutAnImage()
    {
        var item = new PluginItemViewModel(
            new PluginEntry { Path = @"C:\x\Thing.vst3", Format = PluginFormat.Vst3 },
            isFavorite: false);

        Assert.Equal(PluginItemViewModel.DefaultAspectRatio, item.AspectRatio);
        Assert.False(item.HasImage);
        Assert.Null(item.ThumbnailPath);
    }
}
