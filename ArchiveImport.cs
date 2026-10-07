using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Unimportable;

internal sealed class ArchiveImport : IDisposable
{
    private const long SizeLimit = 2_147_483_648;
    private readonly string workspace;
    private readonly byte[] fingerprint;
    private readonly long sourceSize;
    private readonly DateTime sourceTime;
    private readonly List<(string Source, string Target)> installed = new();

    internal string Source { get; }
    internal string[] Folders { get; }
    internal IEnumerable<string> InstalledFolders => installed.Select(item => item.Target);

    private ArchiveImport(string source, string workspace, byte[] fingerprint, long sourceSize, DateTime sourceTime, string[] folders)
    {
        Source = source;
        this.workspace = workspace;
        this.fingerprint = fingerprint;
        this.sourceSize = sourceSize;
        this.sourceTime = sourceTime;
        Folders = folders;
    }

    internal static ArchiveImport Prepare(string source, string songs, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        source = Path.GetFullPath(source);
        if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("drop an archive file");
        }

        var staging = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(songs))!, ".unimportable");
        CheckParents(staging);
        var workspace = Path.Combine(staging, Guid.NewGuid().ToString("N"));
        var content = Path.Combine(workspace, "content");
        Directory.CreateDirectory(content);
        try
        {
            var copy = Path.Combine(workspace, "archive");
            long length;
            DateTime time;
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                length = input.Length;
                time = File.GetLastWriteTimeUtc(source);
                if (length == 0 || length > SizeLimit)
                {
                    throw new InvalidDataException("archive is empty or larger than 2 gb");
                }

                input.CopyTo(output);
            }

            byte[] fingerprint;
            using (var hash = SHA256.Create())
            using (var input = File.OpenRead(copy))
            {
                fingerprint = hash.ComputeHash(input);
            }

            Extract(copy, content, token);
            File.Delete(copy);
            var folders = new[] { content }.Concat(Directory.EnumerateDirectories(content, "*", SearchOption.AllDirectories))
                .Where(folder => !Path.GetRelativePath(content, folder).Split(Path.DirectorySeparatorChar).Contains("__MACOSX"))
                .Where(folder => GetCharts(folder).Length > 0).ToArray();
            if (folders.Length == 0)
            {
                throw new InvalidDataException("archive contains no beatmaps");
            }

            foreach (var folder in folders)
            {
                token.ThrowIfCancellationRequested();
                if (folders.Any(other => other != folder && folder.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException("song folders must not contain other song folders");
                }

                foreach (var chart in GetCharts(folder))
                {
                    ValidateAudio(chart, token);
                }
            }

            return new ArchiveImport(source, workspace, fingerprint, length, time, folders);
        }
        catch
        {
            DeleteWorkspace(workspace);
            throw;
        }
    }

    private static void Extract(string path, string folder, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var input = File.OpenRead(path);
        var information = ArchiveFactory.GetArchiveInformation(input);
        using var archive = information?.SupportsRandomAccess == true ? ArchiveFactory.OpenArchive(input) : null;
        var options = ExtractionOptions.SafeExtract;
        if (archive != null)
        {
            archive.WriteToDirectory(folder, options);
        }
        else
        {
            using var reader = ReaderFactory.OpenReader(input);
            reader.WriteAllToDirectory(folder, options);
        }

        token.ThrowIfCancellationRequested();
    }

    internal static string SafePath(string root, string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("archive contains an unsafe path");
        }

        return path;
    }

    internal static string[] GetCharts(string folder)
    {
        return Directory.EnumerateFiles(folder).Where(path =>
        {
            var extension = Path.GetExtension(path);
            if (!extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".osu", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var reader = File.OpenText(path);
            var header = new char[128];
            var length = reader.Read(header, 0, header.Length);
            if (new string(header, 0, length).TrimStart().StartsWith("osu file format", StringComparison.Ordinal))
            {
                return true;
            }

            if (new FileInfo(path).Length > 16_777_216)
            {
                return false;
            }

            var sections = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line == "[General]" || line == "[Metadata]" || line == "[TimingPoints]" || line == "[HitObjects]")
                {
                    sections.Add(line);
                    if (sections.Count == 4)
                    {
                        return true;
                    }
                }
            }

            return false;
        }).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static void ValidateAudio(string chart, CancellationToken token)
    {
        if (new FileInfo(chart).Length > 16_777_216)
        {
            throw new InvalidDataException("beatmap is larger than 16 mb");
        }

        string? audio = null;
        var general = false;
        foreach (var raw in File.ReadLines(chart))
        {
            token.ThrowIfCancellationRequested();
            var line = raw.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                general = line == "[General]";
            }
            else if (general && line.StartsWith("AudioFilename:", StringComparison.Ordinal))
            {
                audio = line.Substring("AudioFilename:".Length).Trim();
            }
        }

        if (string.IsNullOrEmpty(audio) || !File.Exists(SafePath(Path.GetDirectoryName(chart)!, audio!)))
        {
            throw new InvalidDataException("beatmap audio is missing");
        }
    }

    internal void Commit(string songs)
    {
        CheckParents(songs);
        Directory.CreateDirectory(songs);
        try
        {
            foreach (var folder in Folders)
            {
                var name = Path.GetFileName(folder);
                if (folder.Equals(Path.Combine(workspace, "content"), StringComparison.OrdinalIgnoreCase))
                {
                    name = Path.GetFileNameWithoutExtension(Source);
                }

                SafePath(songs, name);
                var target = Path.Combine(songs, name);
                for (var index = 2; Directory.Exists(target) || File.Exists(target); index++)
                {
                    target = Path.Combine(songs, name + " (" + index + ")");
                }

                Directory.Move(folder, target);
                installed.Add((folder, target));
            }
        }
        catch
        {
            Rollback();
            throw;
        }
    }

    internal void Rollback()
    {
        for (var index = installed.Count - 1; index >= 0; index--)
        {
            var item = installed[index];
            CheckParents(item.Target);
            Directory.Move(item.Target, item.Source);
            installed.RemoveAt(index);
        }
    }

    internal void DeleteSource()
    {
        if (!File.Exists(Source))
        {
            return;
        }

        if ((File.GetAttributes(Source) & FileAttributes.ReparsePoint) != 0
            || new FileInfo(Source).Length != sourceSize || File.GetLastWriteTimeUtc(Source) != sourceTime)
        {
            throw new IOException("source archive changed after import");
        }

        using (var hash = SHA256.Create())
        using (var input = File.OpenRead(Source))
        {
            if (!fingerprint.SequenceEqual(hash.ComputeHash(input)))
            {
                throw new IOException("source archive changed after import");
            }
        }

        File.Delete(Source);
    }

    internal static void CheckParents(string path)
    {
        for (var folder = new DirectoryInfo(path); folder != null; folder = folder.Parent)
        {
            if (folder.Exists && (folder.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("import folder uses a link");
            }
        }
    }

    private static void DeleteWorkspace(string path)
    {
        var resolved = Path.GetFullPath(path);
        if (Path.GetFileName(Path.GetDirectoryName(resolved)) != ".unimportable"
            || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
        {
            throw new IOException("invalid import workspace");
        }

        CheckParents(resolved);
        if (Directory.Exists(resolved))
        {
            Directory.Delete(resolved, true);
        }
    }

    public void Dispose()
    {
        DeleteWorkspace(workspace);
    }
}
