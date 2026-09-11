using System.Buffers.Binary;
using System.Text;

namespace PlugBrowser.Core.Discovery;

/// <summary>
/// Parses a Win32 <c>VS_VERSIONINFO</c> blob — the structure behind the Details tab of a file's
/// properties dialog, and the source of the vendor names PlugBrowser shows before a plugin is loaded.
/// </summary>
/// <remarks>
/// The format is a tree of variable-length nodes, each
/// <c>{ WORD wLength; WORD wValueLength; WORD wType; WCHAR szKey[]; padding; value; children }</c>,
/// with every node and value aligned to a 4-byte boundary. We only need the <c>StringFileInfo</c>
/// branch: it holds one <c>StringTable</c> per language, each a flat list of key/value string pairs.
/// We take the first string table, since plugins essentially never ship more than one and picking a
/// specific language would only add ways to find nothing.
/// </remarks>
internal static class VersionResource
{
    /// <summary>Reads the <c>StringFileInfo</c> pairs out of a VS_VERSIONINFO blob.</summary>
    /// <returns>An empty dictionary if the blob is absent or malformed — never throws.</returns>
    public static Dictionary<string, string> ReadStrings(ReadOnlySpan<byte> blob)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!TryReadNode(blob, 0, out var root))
                return result;

            // VS_VERSIONINFO -> StringFileInfo -> StringTable -> String*
            foreach (var child in Children(blob, root))
            {
                if (!child.Key.Equals("StringFileInfo", StringComparison.Ordinal))
                    continue;

                foreach (var table in Children(blob, child))
                {
                    foreach (var pair in Children(blob, table))
                    {
                        if (pair.ValueOffset > 0 && pair.ValueLength > 0)
                            result[pair.Key] = ReadUtf16Z(blob, pair.ValueOffset, pair.ValueLength * 2);
                    }
                    // One string table is enough; see the remarks above.
                    break;
                }
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // Truncated or hostile resource blob — return whatever was parsed before the end.
        }
        return result;
    }

    private readonly record struct Node(int Offset, int Length, int ValueOffset, int ValueLength,
        int ChildrenOffset, string Key);

    private static bool TryReadNode(ReadOnlySpan<byte> blob, int offset, out Node node)
    {
        node = default;
        if (offset < 0 || offset + 6 > blob.Length)
            return false;

        int length = BinaryPrimitives.ReadUInt16LittleEndian(blob[offset..]);
        int valueLength = BinaryPrimitives.ReadUInt16LittleEndian(blob[(offset + 2)..]);
        if (length < 6 || offset + length > blob.Length)
            return false;

        int keyStart = offset + 6;
        int keyEnd = keyStart;
        while (keyEnd + 1 < blob.Length && BinaryPrimitives.ReadUInt16LittleEndian(blob[keyEnd..]) != 0)
            keyEnd += 2;

        string key = Encoding.Unicode.GetString(blob[keyStart..keyEnd]);
        int valueOffset = Align4(keyEnd + 2);
        int childrenOffset = Align4(valueOffset + ValueBytes(valueLength, blob, offset));

        node = new Node(offset, length, valueOffset, valueLength, childrenOffset, key);
        return true;
    }

    /// <summary>wValueLength counts characters for text values and bytes for binary ones
    /// (VS_FIXEDFILEINFO). wType at +4 distinguishes them: 1 = text.</summary>
    private static int ValueBytes(int valueLength, ReadOnlySpan<byte> blob, int nodeOffset)
    {
        int type = nodeOffset + 6 <= blob.Length
            ? BinaryPrimitives.ReadUInt16LittleEndian(blob[(nodeOffset + 4)..])
            : 0;
        return type == 1 ? valueLength * 2 : valueLength;
    }

    private static List<Node> Children(ReadOnlySpan<byte> blob, Node parent)
    {
        var list = new List<Node>();
        int offset = parent.ChildrenOffset;
        int end = parent.Offset + parent.Length;
        while (offset < end && TryReadNode(blob, offset, out var child))
        {
            list.Add(child);
            int next = Align4(offset + child.Length);
            if (next <= offset)
                break; // zero-length node: malformed, stop rather than spin
            offset = next;
        }
        return list;
    }

    private static string ReadUtf16Z(ReadOnlySpan<byte> blob, int offset, int byteLength)
    {
        if (offset < 0 || byteLength <= 0 || offset >= blob.Length)
            return string.Empty;
        byteLength = Math.Min(byteLength, blob.Length - offset);
        return Encoding.Unicode.GetString(blob.Slice(offset, byteLength)).TrimEnd('\0');
    }

    private static int Align4(int value) => (value + 3) & ~3;
}
