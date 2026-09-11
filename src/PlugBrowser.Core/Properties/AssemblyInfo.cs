using System.Runtime.CompilerServices;

// VersionResource is an implementation detail of PeImage, but its 4-byte alignment rules are subtle
// enough to deserve tests of their own.
[assembly: InternalsVisibleTo("PlugBrowser.Tests")]
