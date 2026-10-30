using System.Text;

namespace RGBcontroller.Core.Config;

/// <summary>Writes files so that readers see either the old or the new content, never a partial file.</summary>
public static class AtomicFile
{
    public const string TempSuffix = ".tmp";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(string path, string contents)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var tempPath = fullPath + TempSuffix;

        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(Utf8NoBom.GetBytes(contents));
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(fullPath))
        {
            File.Replace(tempPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, fullPath);
        }
    }
}
