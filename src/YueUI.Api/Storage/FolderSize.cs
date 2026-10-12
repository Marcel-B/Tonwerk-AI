namespace YueUI.Api.Storage;

/// <summary>What a folder takes on disk, all levels down.</summary>
public static class FolderSize
{
    /// <summary>Deeper than any folder this app or the model caches build; only a guard against a link that loops.</summary>
    private const int MaxDepth = 32;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
    };

    /// <summary>
    /// Follows links to folders, since <c>deploy/move-to-volume.sh</c> leaves one where a part moved to another
    /// volume, but counts links to files as nothing: Hugging Face caches link every snapshot file to its blob, which
    /// would count each model twice. A file deleted while it is counted counts as nothing.
    /// </summary>
    public static long Of(string path)
    {
        var root = new DirectoryInfo(path);
        if (root.LinkTarget is not null)
        {
            root = Target(root);
        }
        if (root is not { Exists: true })
        {
            return 0;
        }
        long bytes = 0;
        // Every folder by where it really lies, so a second link to it (or one back up) does not count it again.
        var seen = new HashSet<string>(StringComparer.Ordinal) { root.FullName };
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((root, 0));
        while (pending.TryPop(out var current))
        {
            List<FileSystemInfo> entries;
            try
            {
                entries = [.. current.Directory.EnumerateFileSystemInfos("*", Options)];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                if (entry is FileInfo file)
                {
                    bytes += file.LinkTarget is null ? LengthOf(file) : 0;
                }
                else if (entry is DirectoryInfo directory && current.Depth < MaxDepth)
                {
                    var real = directory.LinkTarget is null ? directory : Target(directory);
                    if (real is not null && seen.Add(real.FullName))
                    {
                        pending.Push((real, current.Depth + 1));
                    }
                }
            }
        }
        return bytes;
    }

    /// <summary>A file's length, nothing when it is gone or unreadable.</summary>
    public static long LengthOf(string path) => LengthOf(new FileInfo(path));

    private static long LengthOf(FileInfo file)
    {
        try
        {
            return file.Exists ? file.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>Where a linked folder really lies, null when the link points nowhere (a volume not mounted).</summary>
    public static DirectoryInfo? Target(DirectoryInfo link)
    {
        try
        {
            return link.ResolveLinkTarget(returnFinalTarget: true) is DirectoryInfo { Exists: true } target ? target : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
