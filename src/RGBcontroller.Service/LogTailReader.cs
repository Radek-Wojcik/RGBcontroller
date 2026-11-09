using RGBcontroller.Ipc;

namespace RGBcontroller.Service;

/// <summary>Reads the end of the newest rolling log file of a component (the diagnostics page).</summary>
internal static class LogTailReader
{
    /// <param name="directory">The log directory.</param>
    /// <param name="component">"service" or "agent": files are named <c>component-YYYYMMDD.log</c>, <c>-001</c> after a size roll.</param>
    public static LogTail Read(string directory, string component, int lines)
    {
        if (!Directory.Exists(directory))
        {
            return new LogTail(string.Empty, []);
        }

        var file = new DirectoryInfo(directory).GetFiles(component + "-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (file is null)
        {
            return new LogTail(string.Empty, []);
        }

        // The logger holds the file open for writing; share it. Keep only the last lines while reading.
        var queue = new Queue<string>(lines);
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (queue.Count == lines)
            {
                queue.Dequeue();
            }

            queue.Enqueue(line);
        }

        return new LogTail(file.Name, [.. queue]);
    }
}
