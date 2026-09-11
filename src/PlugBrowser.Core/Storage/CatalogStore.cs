using Microsoft.Data.Sqlite;
using PlugBrowser.Core.Model;

namespace PlugBrowser.Core.Storage;

/// <summary>
/// The catalog: everything discovered about the machine's plugins, in a SQLite database under
/// <c>%LOCALAPPDATA%\PlugBrowser</c>.
/// </summary>
/// <remarks>
/// <para>Two rules are inherited from StreamRecorder's JSON catalog, both learned the hard way:</para>
/// <para><b>Failures are cached too.</b> A plugin that crashes the worker is expensive — it costs a full
/// process launch and a timeout. Recording the failure means the next scan skips it instead of paying
/// that cost again, which is the difference between a fast rescan and a slow one.</para>
/// <para><b>A schema bump discards everything.</b> When probing logic changes, previously cached
/// failures may no longer be failures, so <see cref="SchemaVersion"/> is raised and the tables are
/// rebuilt rather than migrated. The catalog is a cache of the filesystem; it can always be rebuilt, and
/// the only durable data — favourites and the blacklist — is preserved across the rebuild.</para>
/// </remarks>
public sealed class CatalogStore : IDisposable
{
    /// <summary>Raise this whenever discovery or probing changes what a cached row would contain.
    /// Doing so drops the cached plugin rows and forces a full rescan on next launch.</summary>
    public const int SchemaVersion = 2;

    private readonly SqliteConnection _connection;

    private CatalogStore(SqliteConnection connection) => _connection = connection;

    /// <summary>Default catalog location.</summary>
    public static string DefaultPath => Path.Combine(DefaultDirectory, "catalog.db");

    /// <summary>Directory holding the catalog and the captured images beside it.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlugBrowser");

    /// <summary>Directory captured screenshots are written to.</summary>
    public static string ImageDirectory => Path.Combine(DefaultDirectory, "images");

    /// <summary>Opens (creating if needed) the catalog at <paramref name="databasePath"/>.</summary>
    public static CatalogStore Open(string? databasePath = null)
    {
        string path = databasePath ?? DefaultPath;
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();

        var store = new CatalogStore(connection);
        store.Initialize();
        return store;
    }

    private void Initialize()
    {
        Execute("PRAGMA journal_mode = WAL;");
        Execute("PRAGMA foreign_keys = ON;");

        Execute("CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);");

        // Absent means a brand-new database with nothing to discard. Any other mismatch — older,
        // newer, or unparseable — means the cached rows were produced by different logic.
        int? existing = ReadSchemaVersion();
        if (existing != null && existing != SchemaVersion)
            DropCachedTables();

        CreateTables();
        Execute("INSERT OR REPLACE INTO meta (key, value) VALUES ('schema_version', $v);",
            ("$v", SchemaVersion.ToString()));
    }

    /// <summary>The schema version recorded in the database, or null if none is recorded.</summary>
    /// <remarks>Returns a nullable rather than using 0 for "absent": 0 is a perfectly storable value, and
    /// conflating the two meant a database claiming version 0 skipped the rebuild it needed.</remarks>
    private int? ReadSchemaVersion()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";

        return command.ExecuteScalar() switch
        {
            string s when int.TryParse(s, out int v) => v,
            // A row that is present but unreadable is a mismatch, not an absence.
            not null => int.MinValue,
            _ => null,
        };
    }

    /// <summary>Drops the derived tables on a schema bump. <c>favorites</c> and <c>blacklist</c> are
    /// deliberately spared: they are the user's own data, not a cache of the filesystem.</summary>
    private void DropCachedTables()
    {
        Execute("DROP TABLE IF EXISTS images;");
        Execute("DROP TABLE IF EXISTS classes;");
        Execute("DROP TABLE IF EXISTS plugins;");
    }

    private void CreateTables()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS plugins (
                path             TEXT PRIMARY KEY,
                format           INTEGER NOT NULL,
                architecture     INTEGER NOT NULL,
                binary_path      TEXT,
                file_size        INTEGER NOT NULL,
                last_write_ticks INTEGER NOT NULL,
                file_vendor      TEXT,
                file_product     TEXT,
                file_version     TEXT,
                state            INTEGER NOT NULL,
                state_detail     TEXT
            );
            """);

        Execute("""
            CREATE TABLE IF NOT EXISTS classes (
                plugin_path    TEXT NOT NULL REFERENCES plugins(path) ON DELETE CASCADE,
                class_index    INTEGER NOT NULL,
                cid            TEXT,
                name           TEXT NOT NULL,
                vendor         TEXT,
                category       TEXT,
                class_category TEXT,
                version        TEXT,
                sdk_version    TEXT,
                kind           INTEGER NOT NULL,
                param_count    INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (plugin_path, class_index)
            );
            """);

        Execute("""
            CREATE TABLE IF NOT EXISTS images (
                plugin_path TEXT NOT NULL REFERENCES plugins(path) ON DELETE CASCADE,
                class_index INTEGER NOT NULL,
                kind        INTEGER NOT NULL,
                file_path   TEXT NOT NULL,
                width       INTEGER NOT NULL,
                height      INTEGER NOT NULL,
                method      INTEGER NOT NULL,
                PRIMARY KEY (plugin_path, class_index, kind)
            );
            """);

        // Kept across schema bumps: the user chose these, we did not derive them.
        Execute("CREATE TABLE IF NOT EXISTS favorites (path TEXT PRIMARY KEY);");

        // Which folders the scanner walks, and whether each is switched on. User data, so it survives a
        // schema bump: re-deriving it would silently re-enable roots the user had deliberately turned off.
        Execute("""
            CREATE TABLE IF NOT EXISTS scan_roots (
                path    TEXT PRIMARY KEY,
                enabled INTEGER NOT NULL DEFAULT 1,
                origin  INTEGER NOT NULL
            );
            """);

        // Free-form application state: window geometry, last-used options, and similar.
        Execute("CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);");
        Execute("""
            CREATE TABLE IF NOT EXISTS blacklist (
                path      TEXT PRIMARY KEY,
                reason    TEXT,
                added_utc INTEGER NOT NULL
            );
            """);

        Execute("CREATE INDEX IF NOT EXISTS idx_classes_name ON classes(name);");
        Execute("CREATE INDEX IF NOT EXISTS idx_plugins_format ON plugins(format);");
    }

    /// <summary>
    /// True if the catalog already holds a current row for this file, meaning a rescan can skip it.
    /// </summary>
    /// <remarks>Keyed on size and last-write time together. Either alone is forgeable by an installer
    /// that preserves timestamps or replaces a file with one of identical length.</remarks>
    public bool IsUpToDate(string path, long fileSize, DateTime lastWriteUtc)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM plugins WHERE path = $p AND file_size = $s AND last_write_ticks = $t;";
        command.Parameters.AddWithValue("$p", path);
        command.Parameters.AddWithValue("$s", fileSize);
        command.Parameters.AddWithValue("$t", lastWriteUtc.Ticks);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>Writes entries to the catalog, replacing any existing rows for the same paths.</summary>
    public void Save(IEnumerable<PluginEntry> entries)
    {
        using var transaction = _connection.BeginTransaction();

        foreach (var entry in entries)
        {
            // Replacing the plugin row cascades the old classes and images away, so a plugin that lost a
            // class in an update does not keep a stale one.
            Execute("DELETE FROM plugins WHERE path = $p;", transaction, ("$p", entry.Path));

            Execute("""
                INSERT INTO plugins
                    (path, format, architecture, binary_path, file_size, last_write_ticks,
                     file_vendor, file_product, file_version, state, state_detail)
                VALUES ($path, $format, $arch, $binary, $size, $ticks,
                        $vendor, $product, $version, $state, $detail);
                """, transaction,
                ("$path", entry.Path),
                ("$format", (int)entry.Format),
                ("$arch", (int)entry.Architecture),
                ("$binary", entry.BinaryPath),
                ("$size", entry.FileSize),
                ("$ticks", entry.LastWriteUtc.Ticks),
                ("$vendor", entry.FileVendor),
                ("$product", entry.FileProduct),
                ("$version", entry.FileVersion),
                ("$state", (int)entry.State),
                ("$detail", entry.StateDetail));

            foreach (var c in entry.Classes)
            {
                Execute("""
                    INSERT INTO classes
                        (plugin_path, class_index, cid, name, vendor, category, class_category,
                         version, sdk_version, kind, param_count)
                    VALUES ($path, $index, $cid, $name, $vendor, $category, $classCategory,
                            $version, $sdk, $kind, $paramCount);
                    """, transaction,
                    ("$path", entry.Path),
                    ("$index", c.Index),
                    ("$cid", c.Cid),
                    ("$name", c.Name),
                    ("$vendor", c.Vendor),
                    ("$category", c.Category),
                    ("$classCategory", c.ClassCategory),
                    ("$version", c.Version),
                    ("$sdk", c.SdkVersion),
                    ("$kind", (int)c.Kind),
                    ("$paramCount", c.ParameterCount));

                foreach (var image in c.Images)
                {
                    Execute("""
                        INSERT OR REPLACE INTO images
                            (plugin_path, class_index, kind, file_path, width, height, method)
                        VALUES ($path, $index, $kind, $file, $w, $h, $method);
                        """, transaction,
                        ("$path", entry.Path),
                        ("$index", c.Index),
                        ("$kind", (int)image.Kind),
                        ("$file", image.FilePath),
                        ("$w", image.Width),
                        ("$h", image.Height),
                        ("$method", (int)image.Method));
                }
            }
        }

        transaction.Commit();
    }

    /// <summary>Reads the whole catalog back, classes and images attached.</summary>
    public IReadOnlyList<PluginEntry> Load()
    {
        var classesByPath = LoadClasses();
        var entries = new List<PluginEntry>();

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT path, format, architecture, binary_path, file_size, last_write_ticks,
                   file_vendor, file_product, file_version, state, state_detail
            FROM plugins;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string path = reader.GetString(0);
            entries.Add(new PluginEntry
            {
                Path = path,
                Format = (PluginFormat)reader.GetInt32(1),
                Architecture = (PluginArchitecture)reader.GetInt32(2),
                BinaryPath = reader.IsDBNull(3) ? null : reader.GetString(3),
                FileSize = reader.GetInt64(4),
                LastWriteUtc = new DateTime(reader.GetInt64(5), DateTimeKind.Utc),
                FileVendor = reader.IsDBNull(6) ? null : reader.GetString(6),
                FileProduct = reader.IsDBNull(7) ? null : reader.GetString(7),
                FileVersion = reader.IsDBNull(8) ? null : reader.GetString(8),
                State = (ProbeState)reader.GetInt32(9),
                StateDetail = reader.IsDBNull(10) ? null : reader.GetString(10),
                Classes = classesByPath.TryGetValue(path, out var classes) ? classes : [],
            });
        }
        return entries;
    }

    private Dictionary<string, List<PluginClass>> LoadClasses()
    {
        var images = LoadImages();
        var result = new Dictionary<string, List<PluginClass>>(StringComparer.OrdinalIgnoreCase);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT plugin_path, class_index, cid, name, vendor, category, class_category,
                   version, sdk_version, kind, param_count
            FROM classes ORDER BY plugin_path, class_index;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string path = reader.GetString(0);
            int index = reader.GetInt32(1);

            var pluginClass = new PluginClass
            {
                Index = index,
                Cid = reader.IsDBNull(2) ? null : reader.GetString(2),
                Name = reader.GetString(3),
                Vendor = reader.IsDBNull(4) ? null : reader.GetString(4),
                Category = reader.IsDBNull(5) ? null : reader.GetString(5),
                ClassCategory = reader.IsDBNull(6) ? null : reader.GetString(6),
                Version = reader.IsDBNull(7) ? null : reader.GetString(7),
                SdkVersion = reader.IsDBNull(8) ? null : reader.GetString(8),
                Kind = (PluginKind)reader.GetInt32(9),
                ParameterCount = reader.GetInt32(10),
                Images = images.TryGetValue((path, index), out var list) ? list : [],
            };
            pluginClass = pluginClass with { Tags = Discovery.Vst3BundleReader.SplitCategory(pluginClass.Category) };

            if (!result.TryGetValue(path, out var classes))
                result[path] = classes = [];
            classes.Add(pluginClass);
        }
        return result;
    }

    private Dictionary<(string Path, int Index), List<PluginImage>> LoadImages()
    {
        var result = new Dictionary<(string, int), List<PluginImage>>();

        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT plugin_path, class_index, kind, file_path, width, height, method FROM images;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = (reader.GetString(0), reader.GetInt32(1));
            if (!result.TryGetValue(key, out var list))
                result[key] = list = [];

            list.Add(new PluginImage
            {
                Kind = (ImageKind)reader.GetInt32(2),
                FilePath = reader.GetString(3),
                Width = reader.GetInt32(4),
                Height = reader.GetInt32(5),
                Method = (CaptureMethod)reader.GetInt32(6),
            });
        }

        // Vendor snapshots outrank captured screenshots, so the UI can take the first image blindly.
        foreach (var list in result.Values)
            list.Sort((a, b) => a.Kind.CompareTo(b.Kind));

        return result;
    }

    /// <summary>Removes catalog rows whose files are no longer on disk.</summary>
    /// <returns>The number of entries dropped.</returns>
    public int PruneMissing()
    {
        var gone = Load()
            .Where(e => !File.Exists(e.Path) && !Directory.Exists(e.Path))
            .Select(e => e.Path)
            .ToList();

        using var transaction = _connection.BeginTransaction();
        foreach (var path in gone)
            Execute("DELETE FROM plugins WHERE path = $p;", transaction, ("$p", path));
        transaction.Commit();

        return gone.Count;
    }

    // ---- scan roots -------------------------------------------------------------------------

    /// <summary>Reads the stored enable/disable state and user-added roots.</summary>
    /// <returns>Path to (enabled, origin). Conventional roots absent from this map have simply never
    /// been toggled, and default to enabled.</returns>
    public IReadOnlyDictionary<string, (bool Enabled, ScanRootOrigin Origin)> GetScanRootState()
    {
        var result = new Dictionary<string, (bool, ScanRootOrigin)>(StringComparer.OrdinalIgnoreCase);

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT path, enabled, origin FROM scan_roots;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = (reader.GetInt32(1) != 0, (ScanRootOrigin)reader.GetInt32(2));

        return result;
    }

    /// <summary>Records whether a root is included in scans.</summary>
    public void SetScanRootEnabled(string path, bool enabled, ScanRootOrigin origin) => Execute("""
        INSERT INTO scan_roots (path, enabled, origin) VALUES ($p, $e, $o)
        ON CONFLICT(path) DO UPDATE SET enabled = $e;
        """, ("$p", path), ("$e", enabled ? 1 : 0), ("$o", (int)origin));

    /// <summary>Adds a user-chosen scan root.</summary>
    public void AddUserScanRoot(string path) =>
        SetScanRootEnabled(path, enabled: true, ScanRootOrigin.User);

    /// <summary>Forgets a user-added root. Conventional and registry roots are rediscovered every
    /// launch, so removing them would have no lasting effect — they are disabled instead.</summary>
    public void RemoveUserScanRoot(string path) =>
        Execute("DELETE FROM scan_roots WHERE path = $p AND origin = $o;",
            ("$p", path), ("$o", (int)ScanRootOrigin.User));

    // ---- settings ---------------------------------------------------------------------------

    /// <summary>Reads a stored application setting.</summary>
    public string? GetSetting(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $k;";
        command.Parameters.AddWithValue("$k", key);
        return command.ExecuteScalar() as string;
    }

    /// <summary>Stores an application setting, or clears it when <paramref name="value"/> is null.</summary>
    public void SetSetting(string key, string? value) => Execute(
        value is null
            ? "DELETE FROM settings WHERE key = $k;"
            : "INSERT OR REPLACE INTO settings (key, value) VALUES ($k, $v);",
        ("$k", key), ("$v", value));

    public IReadOnlySet<string> GetFavorites() =>
        ReadPathSet("SELECT path FROM favorites;");

    public void SetFavorite(string path, bool isFavorite) => Execute(
        isFavorite
            ? "INSERT OR IGNORE INTO favorites (path) VALUES ($p);"
            : "DELETE FROM favorites WHERE path = $p;",
        ("$p", path));

    public IReadOnlySet<string> GetBlacklist() =>
        ReadPathSet("SELECT path FROM blacklist;");

    /// <summary>Marks a plugin as never to be loaded — the escape hatch for one that crashes or hangs
    /// the worker every time.</summary>
    public void Blacklist(string path, string? reason) => Execute("""
        INSERT OR REPLACE INTO blacklist (path, reason, added_utc) VALUES ($p, $r, $t);
        """, ("$p", path), ("$r", reason), ("$t", DateTime.UtcNow.Ticks));

    public void Unblacklist(string path) =>
        Execute("DELETE FROM blacklist WHERE path = $p;", ("$p", path));

    private HashSet<string> ReadPathSet(string sql)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters) =>
        Execute(sql, null, parameters);

    private void Execute(string sql, SqliteTransaction? transaction,
        params (string Name, object? Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}
