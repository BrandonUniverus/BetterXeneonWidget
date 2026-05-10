using System.IO.Compression;
using System.Reflection;

namespace BetterXeneonWidget.Installer;

/// <summary>
/// Pulls files out of the embedded payload.zip. The zip is loaded lazily and
/// kept open for the lifetime of the process so we don't re-decompress per
/// extract — the host exe alone is ~160 MB.
/// </summary>
internal static class Resources
{
    private static ZipArchive? _archive;
    private static Stream? _archiveStream;

    public static void ExtractTo(string entryName, string destPath)
    {
        var archive = OpenArchive();
        var entry = archive.GetEntry(entryName)
            ?? throw new InvalidOperationException($"Embedded payload missing entry: {entryName}");

        using var src = entry.Open();
        using var dst = File.Create(destPath);
        src.CopyTo(dst);
    }

    public static long EntryLength(string entryName)
    {
        var archive = OpenArchive();
        return archive.GetEntry(entryName)?.Length ?? 0;
    }

    private static ZipArchive OpenArchive()
    {
        if (_archive != null) return _archive;

        var asm = Assembly.GetExecutingAssembly();
        _archiveStream = asm.GetManifestResourceStream("payload.zip")
            ?? throw new InvalidOperationException(
                "Embedded payload.zip not found. Build via tools/build-installer.mjs.");
        _archive = new ZipArchive(_archiveStream, ZipArchiveMode.Read);
        return _archive;
    }
}
