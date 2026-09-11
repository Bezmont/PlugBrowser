using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using System.Text;
using PlugBrowser.Core.Model;

namespace PlugBrowser.Core.Discovery;

/// <summary>
/// Reads the three things a plugin catalog can learn from a Windows PE binary without ever executing
/// it: its CPU architecture, its exported function names, and its version resource.
/// </summary>
/// <remarks>
/// <para>This is what makes discovery useful on its own. On a real machine most <c>.vst3</c> files are
/// flat DLLs with no <c>moduleinfo.json</c> and no vendor snapshot, so the version resource is the only
/// source of a vendor name short of loading the plugin — and loading is exactly what we want to defer
/// to the sandboxed worker.</para>
/// <para>Exports matter for VST2: a folder of VST2 plugins is usually littered with support DLLs, and
/// the presence of <c>VSTPluginMain</c> is what separates an actual plugin from its helper libraries.</para>
/// <para>Everything here is best-effort. A malformed or truncated file yields nulls rather than throwing,
/// because a scan across hundreds of third-party binaries will meet malformed files and must not stop.</para>
/// <para>Reads are deliberately kept to the few bytes each structure needs. Sample libraries ship
/// enormous DLLs — half a gigabyte is not unusual — and pulling whole PE sections into memory to read a
/// 40-byte header turned a full scan into a minute of pure copying.</para>
/// </remarks>
public sealed record PeImage
{
    public PluginArchitecture Architecture { get; init; }

    /// <summary>Exported symbol names, ordinal-only exports excluded.</summary>
    public ImmutableArray<string> Exports { get; init; } = [];

    /// <summary><c>CompanyName</c> from the version resource.</summary>
    public string? CompanyName { get; init; }

    /// <summary><c>ProductName</c> from the version resource. Frequently absent even when
    /// <see cref="CompanyName"/> is present.</summary>
    public string? ProductName { get; init; }

    /// <summary><c>FileDescription</c> — often the most human-readable name a vendor supplies.</summary>
    public string? FileDescription { get; init; }

    public string? FileVersion { get; init; }

    public bool ExportsSymbol(string name) => Exports.Contains(name, StringComparer.Ordinal);

    /// <summary>Reads <paramref name="path"/>, returning null if it is not a readable PE file.</summary>
    public static PeImage? TryRead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var headers = pe.PEHeaders;
            if (headers.PEHeader is null)
                return null;

            var version = ReadVersionStrings(pe, headers);

            return new PeImage
            {
                Architecture = MapMachine(headers.CoffHeader.Machine),
                Exports = ReadExports(pe, headers),
                CompanyName = Clean(version, "CompanyName"),
                ProductName = Clean(version, "ProductName"),
                FileDescription = Clean(version, "FileDescription"),
                FileVersion = Clean(version, "FileVersion") ?? Clean(version, "ProductVersion"),
            };
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static PluginArchitecture MapMachine(Machine machine) => machine switch
    {
        Machine.Amd64 => PluginArchitecture.X64,
        Machine.I386 => PluginArchitecture.X86,
        Machine.Arm64 => PluginArchitecture.Arm64,
        _ => PluginArchitecture.Unknown,
    };

    private static string? Clean(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    // ---- exports ----------------------------------------------------------------------------

    private static ImmutableArray<string> ReadExports(PEReader pe, PEHeaders headers)
    {
        var dir = headers.PEHeader!.ExportTableDirectory;
        if (dir.RelativeVirtualAddress == 0 || dir.Size == 0)
            return [];

        // IMAGE_EXPORT_DIRECTORY: NumberOfNames at +0x18, AddressOfNames (RVA of an RVA array) at +0x20.
        var header = ReadAt(pe, dir.RelativeVirtualAddress, 0x28);
        if (header.IsEmpty)
            return [];

        uint nameCount = BinaryPrimitives.ReadUInt32LittleEndian(header[0x18..]);
        uint namesRva = BinaryPrimitives.ReadUInt32LittleEndian(header[0x20..]);
        if (nameCount == 0 || nameCount > 65536 || namesRva == 0)
            return [];

        var nameRvas = ReadAt(pe, (int)namesRva, (int)nameCount * 4);
        if (nameRvas.IsEmpty)
            return [];

        var builder = ImmutableArray.CreateBuilder<string>((int)nameCount);
        for (int i = 0; i < nameCount; i++)
        {
            uint strRva = BinaryPrimitives.ReadUInt32LittleEndian(nameRvas[(i * 4)..]);
            if (ReadAsciiZ(pe, (int)strRva) is { Length: > 0 } name)
                builder.Add(name);
        }
        return builder.ToImmutable();
    }

    // ---- version resource -------------------------------------------------------------------

    /// <summary>Walks the PE resource tree to RT_VERSION and parses the blob it points at.</summary>
    /// <remarks>
    /// The tree is three levels deep — type, then name, then language — and each level is an
    /// IMAGE_RESOURCE_DIRECTORY followed by its entries. We take the first name and the first language
    /// under type 16, because a binary with more than one version resource is a curiosity we have no
    /// better rule for. Offsets inside the tree are relative to the start of the resource directory;
    /// only the leaf's OffsetToData is a true RVA, which is why it is used unrebased.
    /// </remarks>
    private static Dictionary<string, string> ReadVersionStrings(PEReader pe, PEHeaders headers)
    {
        const int RtVersion = 16;
        var empty = new Dictionary<string, string>();

        var dir = headers.PEHeader!.ResourceTableDirectory;
        if (dir.RelativeVirtualAddress == 0 || dir.Size == 0)
            return empty;

        int root = dir.RelativeVirtualAddress;
        if (!TryFindEntry(pe, root, root, RtVersion, out int nameDir) ||
            !TryFirstEntry(pe, root, nameDir, out int langDir) ||
            !TryFirstEntry(pe, root, langDir, out int leaf))
            return empty;

        // IMAGE_RESOURCE_DATA_ENTRY: OffsetToData (an RVA) at +0, Size at +4.
        var entry = ReadAt(pe, leaf, 8);
        if (entry.IsEmpty)
            return empty;

        int dataRva = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry);
        int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
        if (size is <= 0 or > 1 << 20)
            return empty;

        var blob = ReadAt(pe, dataRva, size);
        return blob.IsEmpty ? empty : VersionResource.ReadStrings(blob);
    }

    /// <summary>Finds the child of a resource directory whose integer id is <paramref name="id"/>.</summary>
    private static bool TryFindEntry(PEReader pe, int rootRva, int dirRva, int id, out int childRva) =>
        TryScanEntries(pe, rootRva, dirRva, id, out childRva);

    /// <summary>Takes the first child of a resource directory, whatever its id.</summary>
    private static bool TryFirstEntry(PEReader pe, int rootRva, int dirRva, out int childRva) =>
        TryScanEntries(pe, rootRva, dirRva, null, out childRva);

    private static bool TryScanEntries(PEReader pe, int rootRva, int dirRva, int? wantedId, out int childRva)
    {
        childRva = 0;

        // IMAGE_RESOURCE_DIRECTORY is 16 bytes; named entries come first, then id entries.
        var header = ReadAt(pe, dirRva, 16);
        if (header.IsEmpty)
            return false;

        int named = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
        int byId = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
        int entries = named + byId;
        if (entries is <= 0 or > 8192)
            return false;

        var table = ReadAt(pe, dirRva + 16, entries * 8);
        if (table.IsEmpty)
            return false;

        for (int i = 0; i < entries; i++)
        {
            var e = table[(i * 8)..];
            uint nameField = BinaryPrimitives.ReadUInt32LittleEndian(e);
            uint offsetField = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);

            bool isNamed = (nameField & 0x8000_0000) != 0;
            if (wantedId is not null && (isNamed || nameField != (uint)wantedId.Value))
                continue;

            // The high bit marks a subdirectory; the rest is an offset from the resource root.
            childRva = rootRva + (int)(offsetField & 0x7FFF_FFFF);
            return true;
        }
        return false;
    }

    // ---- targeted reads ---------------------------------------------------------------------

    /// <summary>Reads exactly <paramref name="length"/> bytes at <paramref name="rva"/>.</summary>
    /// <returns>An empty span if the range is not backed by a section — the signal for "give up on this
    /// structure", which is why every caller checks <c>IsEmpty</c> rather than catching.</returns>
    private static ReadOnlySpan<byte> ReadAt(PEReader pe, int rva, int length)
    {
        if (rva <= 0 || length <= 0)
            return default;

        try
        {
            var block = pe.GetSectionData(rva);
            if (block.Length < length)
                return default;
            return block.GetContent(0, length).AsSpan();
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentOutOfRangeException
                                      or InvalidOperationException)
        {
            return default;
        }
    }

    /// <summary>Reads a null-terminated ASCII string at <paramref name="rva"/>.</summary>
    /// <remarks>Export names are short, so a single bounded read beats growing a buffer. 512 bytes is far
    /// beyond any real symbol and keeps a corrupt table from causing a large allocation.</remarks>
    private static string? ReadAsciiZ(PEReader pe, int rva)
    {
        const int MaxSymbolLength = 512;
        if (rva <= 0)
            return null;

        try
        {
            var block = pe.GetSectionData(rva);
            int length = Math.Min(MaxSymbolLength, block.Length);
            if (length <= 0)
                return null;

            var bytes = block.GetContent(0, length).AsSpan();
            int end = bytes.IndexOf((byte)0);
            return end <= 0 ? null : Encoding.ASCII.GetString(bytes[..end]);
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentOutOfRangeException
                                      or InvalidOperationException)
        {
            return null;
        }
    }
}
