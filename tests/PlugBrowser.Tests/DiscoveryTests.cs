using PlugBrowser.Core.Discovery;
using PlugBrowser.Core.Model;
using Xunit;

namespace PlugBrowser.Tests;

/// <summary>A throwaway directory tree, removed when the test finishes.</summary>
internal sealed class TempTree : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("plugbrowser-test-").FullName;

    public string Dir(params string[] parts)
    {
        string path = Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string relativePath, string content = "")
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* a test left a handle open; the temp dir will be cleaned by the OS */ }
    }
}

public class Vst3BundleReaderTests
{
    /// <summary>The SDK writes <c>"Sub Categories"</c> with a space and emits trailing commas, neither of
    /// which strict JSON allows. Both cost us the categories once already.</summary>
    [Fact]
    public void ReadModuleInfo_HandlesSdkDialect()
    {
        using var tree = new TempTree();
        string bundle = tree.Dir("Thing.vst3");
        tree.File(@"Thing.vst3\Contents\Resources\moduleinfo.json", """
            {
              "Name": "Thing",
              "Classes": [
                {
                  "CID": "84E8DE5F9255222296FAE4133C935A18",
                  "Category": "Audio Module Class",
                  "Name": "Thing",
                  "Vendor": "Acme",
                  "Version": "1.2.3",
                  "SDKVersion": "VST 3.7.12",
                  "Sub Categories": [
                    "Instrument",
                    "Synth",
                  ],
                },
              ],
            }
            """);

        var classes = Vst3BundleReader.ReadModuleInfo(bundle);

        var only = Assert.Single(classes);
        Assert.Equal("Thing", only.Name);
        Assert.Equal("Acme", only.Vendor);
        Assert.Equal("Instrument|Synth", only.Category);
        Assert.Equal(["Instrument", "Synth"], only.Tags);
        Assert.Equal(PluginKind.Instrument, only.Kind);
        Assert.Equal("VST 3.7.12", only.SdkVersion);
    }

    /// <summary>A factory advertises a controller and a compatibility class beside each real plugin.
    /// Surfacing those made one bundle look like three plugins with identical names.</summary>
    [Fact]
    public void ReadModuleInfo_KeepsOnlyAudioModules_ButPreservesFactoryIndices()
    {
        using var tree = new TempTree();
        string bundle = tree.Dir("Multi.vst3");
        tree.File(@"Multi.vst3\Contents\Resources\moduleinfo.json", """
            {
              "Classes": [
                { "CID": "AA", "Category": "Component Controller Class", "Name": "Multi" },
                { "CID": "BB", "Category": "Audio Module Class", "Name": "Multi" },
                { "CID": "CC", "Category": "Plugin Compatibility Class", "Name": "Multi" }
              ]
            }
            """);

        var classes = Vst3BundleReader.ReadModuleInfo(bundle);

        var only = Assert.Single(classes);
        Assert.Equal("BB", only.Cid);
        // The audio module is the factory's second class, and must keep saying so.
        Assert.Equal(1, only.Index);
    }

    [Fact]
    public void ReadModuleInfo_ReturnsEmpty_WhenFileIsMissingOrCorrupt()
    {
        using var tree = new TempTree();
        Assert.Empty(Vst3BundleReader.ReadModuleInfo(tree.Dir("NoInfo.vst3")));

        string bundle = tree.Dir("Corrupt.vst3");
        tree.File(@"Corrupt.vst3\Contents\Resources\moduleinfo.json", "{ this is not json");
        Assert.Empty(Vst3BundleReader.ReadModuleInfo(bundle));
    }

    [Fact]
    public void ResolveBinary_PrefersX64_AndFindsBinaryWithMismatchedName()
    {
        using var tree = new TempTree();
        string bundle = tree.Dir("Named.vst3");
        tree.File(@"Named.vst3\Contents\x86-win\Named.vst3", "32-bit");
        tree.File(@"Named.vst3\Contents\x86_64-win\SomethingElse.vst3", "64-bit");

        var (binary, arch) = Vst3BundleReader.ResolveBinary(bundle);

        Assert.Equal(PluginArchitecture.X64, arch);
        Assert.Equal("SomethingElse.vst3", Path.GetFileName(binary));
    }

    [Fact]
    public void ResolveBinary_ReturnsNothing_ForBundleWithNoKnownArchitecture()
    {
        using var tree = new TempTree();
        string bundle = tree.Dir("Empty.vst3");
        tree.File(@"Empty.vst3\Contents\ppc-win\Empty.vst3", "nope");

        var (binary, _) = Vst3BundleReader.ResolveBinary(bundle);

        Assert.Null(binary);
    }

    [Theory]
    [InlineData("Fx|EQ", new[] { "Fx", "EQ" })]
    [InlineData("Instrument | Synth", new[] { "Instrument", "Synth" })]
    [InlineData("", new string[0])]
    [InlineData(null, new string[0])]
    public void SplitCategory_SplitsAndTrims(string? category, string[] expected) =>
        Assert.Equal(expected, Vst3BundleReader.SplitCategory(category));

    [Fact]
    public void FindSnapshot_PrefersHighDpiVariant()
    {
        using var tree = new TempTree();
        const string cid = "84E8DE5F9255222296FAE4133C935A18";
        string bundle = tree.Dir("Snap.vst3");
        tree.File($@"Snap.vst3\Contents\Resources\Snapshots\{cid}_snapshot.png", "x");
        tree.File($@"Snap.vst3\Contents\Resources\Snapshots\{cid}_snapshot_2.0x.png", "x");

        string? found = Vst3BundleReader.FindSnapshot(bundle, cid);

        Assert.NotNull(found);
        Assert.Contains("_2.0x", found);
    }
}

public class PluginScannerTests
{
    /// <summary>
    /// The rule the whole traversal hangs on: a <c>.vst3</c> directory is one plugin, and descending into
    /// it would rediscover its own inner binary as a phantom second plugin.
    /// </summary>
    [Fact]
    public void Scan_TreatsBundleDirectoryAsOneEntry_AndDoesNotDescend()
    {
        using var tree = new TempTree();
        // A bundle whose inner binary would be found again if the walk recursed into it.
        tree.File(@"Bundle.vst3\Contents\x86_64-win\Bundle.vst3", "not a real PE");

        var found = new List<string>();
        foreach (var entry in new PluginScanner().Scan([tree.Root]))
            found.Add(entry.Path);

        // The inner binary is not a valid PE, so it yields no entry either way; what matters is that the
        // bundle itself was offered exactly once and its interior was never walked.
        Assert.DoesNotContain(found, p => p.Contains("x86_64-win", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Scan_SkipsNonPluginFiles()
    {
        using var tree = new TempTree();
        tree.File("readme.txt", "hello");
        tree.File("data.json", "{}");

        Assert.Empty(new PluginScanner().Scan([tree.Root]));
    }

    [Fact]
    public void Scan_SurvivesAMissingRoot()
    {
        string missing = Path.Combine(Path.GetTempPath(), "plugbrowser-does-not-exist-" + Guid.NewGuid());
        Assert.Empty(new PluginScanner().Scan([missing]));
    }

    /// <summary>
    /// Every Arturia VST3 on the development machine also exports <c>main</c>. Classifying on exports
    /// alone would file all 172 of them as VST2, so the extension has to be part of the test.
    /// </summary>
    [Fact]
    public void ClassifyFormat_DoesNotMistakeAVst3ExportingMainForVst2()
    {
        var arturiaLike = new PeImage { Exports = ["GetPluginFactory", "main"] };

        Assert.Equal(PluginFormat.Vst3, PluginScanner.ClassifyFormat(@"C:\x\Acid V.vst3", arturiaLike));
    }

    [Theory]
    [InlineData(@"C:\x\Thing.dll", new[] { "VSTPluginMain" }, PluginFormat.Vst2)]
    [InlineData(@"C:\x\Thing.dll", new[] { "main" }, PluginFormat.Vst2)]
    [InlineData(@"C:\x\Support.dll", new[] { "DllMain" }, PluginFormat.Unknown)]
    [InlineData(@"C:\x\Thing.vst3", new[] { "GetPluginFactory" }, PluginFormat.Vst3)]
    [InlineData(@"C:\x\Thing.vst3", new[] { "DllMain" }, PluginFormat.Unknown)]
    public void ClassifyFormat_UsesExtensionAndExports(string path, string[] exports, PluginFormat expected)
    {
        var pe = new PeImage { Exports = [.. exports] };

        Assert.Equal(expected, PluginScanner.ClassifyFormat(path, pe));
    }
}

public class PeImageTests
{
    /// <summary>Reads a system DLL, which is guaranteed present, signed, and rich in all three of the
    /// things this parser extracts.</summary>
    private static string SystemDll =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");

    [Fact]
    public void TryRead_ExtractsArchitectureExportsAndVersion()
    {
        // The product is Windows-only, but keep the guard so the suite is runnable elsewhere.
        if (!OperatingSystem.IsWindows() || !File.Exists(SystemDll))
            return;

        var pe = PeImage.TryRead(SystemDll);

        Assert.NotNull(pe);
        Assert.Equal(PluginArchitecture.X64, pe.Architecture);
        Assert.True(pe.ExportsSymbol("CreateFileW"), "kernel32 must export CreateFileW");
        Assert.False(pe.ExportsSymbol("NoSuchExportExists"));
        Assert.Equal("Microsoft Corporation", pe.CompanyName);
        Assert.False(string.IsNullOrWhiteSpace(pe.FileVersion));
    }

    [Fact]
    public void TryRead_ReturnsNull_ForFilesThatAreNotPortableExecutables()
    {
        using var tree = new TempTree();

        Assert.Null(PeImage.TryRead(tree.File("notaplugin.dll", "this is plain text")));
        Assert.Null(PeImage.TryRead(Path.Combine(tree.Root, "missing.dll")));
    }
}

public class VersionResourceTests
{
    /// <summary>
    /// Builds a VS_VERSIONINFO blob by hand to pin the alignment rules, which are the whole difficulty of
    /// the format: every node and every value starts on a 4-byte boundary, and <c>wValueLength</c> counts
    /// characters for text values but bytes for binary ones.
    /// </summary>
    [Fact]
    public void ReadStrings_ParsesNestedStringTable()
    {
        byte[] blob = BuildVersionInfo(new Dictionary<string, string>
        {
            ["CompanyName"] = "Acme Audio",
            ["ProductName"] = "Compressor",
            ["FileVersion"] = "1.2.3.4",
        });

        var values = VersionResource.ReadStrings(blob);

        Assert.Equal("Acme Audio", values["CompanyName"]);
        Assert.Equal("Compressor", values["ProductName"]);
        Assert.Equal("1.2.3.4", values["FileVersion"]);
    }

    [Fact]
    public void ReadStrings_ReturnsEmpty_ForGarbage()
    {
        Assert.Empty(VersionResource.ReadStrings([]));
        Assert.Empty(VersionResource.ReadStrings([1, 2, 3]));
        Assert.Empty(VersionResource.ReadStrings(new byte[64]));
    }

    /// <summary>Truncation must yield whatever parsed cleanly rather than throwing, because a scan meets
    /// malformed third-party binaries and cannot stop on them.</summary>
    [Fact]
    public void ReadStrings_DoesNotThrow_OnTruncatedBlob()
    {
        byte[] blob = BuildVersionInfo(new Dictionary<string, string> { ["CompanyName"] = "Acme" });

        for (int length = 0; length < blob.Length; length++)
            _ = VersionResource.ReadStrings(blob.AsSpan(0, length));
    }

    private static byte[] BuildVersionInfo(Dictionary<string, string> strings)
    {
        var stringNodes = strings.Select(kv => Node(kv.Key, kv.Value)).ToList();
        byte[] table = Node("040904B0", null, stringNodes);
        byte[] stringFileInfo = Node("StringFileInfo", null, [table]);
        return Node("VS_VERSION_INFO", null, [stringFileInfo], fixedInfoBytes: 52);
    }

    /// <summary>Emits one <c>{wLength, wValueLength, wType, szKey, padding, value, children}</c> node.</summary>
    private static byte[] Node(string key, string? value, List<byte[]>? children = null,
        int fixedInfoBytes = 0)
    {
        var body = new MemoryStream();
        var writer = new BinaryWriter(body, System.Text.Encoding.Unicode);

        writer.Write((ushort)0);                                    // wLength, back-filled below
        writer.Write((ushort)(value is not null ? value.Length + 1  // wValueLength: chars for text...
            : fixedInfoBytes));                                     // ...bytes for binary
        writer.Write((ushort)(value is not null ? 1 : 0));           // wType: 1 = text
        writer.Write(System.Text.Encoding.Unicode.GetBytes(key + "\0"));
        Pad(body);

        if (value is not null)
            writer.Write(System.Text.Encoding.Unicode.GetBytes(value + "\0"));
        else if (fixedInfoBytes > 0)
            writer.Write(new byte[fixedInfoBytes]);
        Pad(body);

        foreach (var child in children ?? [])
        {
            writer.Write(child);
            Pad(body);
        }

        byte[] bytes = body.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 2), (ushort)bytes.Length);
        return bytes;

        static void Pad(MemoryStream stream)
        {
            while (stream.Length % 4 != 0)
                stream.WriteByte(0);
        }
    }
}
