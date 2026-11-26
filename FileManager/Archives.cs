using System.IO.Compression;

namespace FileManager;

internal sealed class Archives(TextFiles files)
{
    private const int MaxEntries = 100;
    private const int MaxArchiveBytes = 16 * 1024 * 1024;
    private const int MaxTotalBytes = 16 * 1024 * 1024;

    public static string CheckArchiveName(string name) => TextFiles.IsArchiveName(name) ? name
        : throw new ArgumentException("Введите имя архива с расширением .zip, без каталога.");

    public long Create(long owner, string target, IReadOnlyList<string> sources)
    {
        if (sources.Count is < 1 or > MaxEntries)
            throw new ArgumentException($"Укажите от 1 до {MaxEntries} файлов.");

        string destination = files.Resolve(owner, CheckArchiveName(target), allowArchive: true);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var inputs = new List<(string Name, string Path)>();
        foreach (string source in sources)
        {
            string path = files.Resolve(owner, source);
            string name = Path.GetFileName(path);
            if (!names.Add(name)) throw new ArgumentException("Имена файлов внутри архива должны различаться.");
            inputs.Add((name, path));
        }

        string temporary = Path.Combine(files.UserDirectory(owner), $".{Guid.NewGuid():N}.tmp");
        var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            long size;
            using (output)
            {
                int remaining = MaxTotalBytes;
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var input in inputs)
                    {
                        using var source = File.OpenRead(input.Path);
                        using var entry = archive.CreateEntry(input.Name, CompressionLevel.Optimal).Open();
                        remaining -= CopyLimited(source, entry, Math.Min(TextFiles.MaxBytes, remaining));
                    }
                }
                size = output.Length;
                if (size > MaxArchiveBytes) throw new IOException("Размер ZIP превышает 16 МиБ.");
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: false);
            return size;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public List<(string Name, long Size)> Extract(long owner, string source, string target)
    {
        TextFiles.CheckFolder(target);
        string root = files.UserDirectory(owner);
        string destination = Path.Combine(root, target);
        if (Directory.Exists(destination) || File.Exists(destination) ||
            new DirectoryInfo(destination).LinkTarget is not null)
            throw new IOException("Каталог назначения уже существует.");

        using var input = File.OpenRead(files.Resolve(owner, CheckArchiveName(source), allowArchive: true));
        if (input.Length > MaxArchiveBytes) throw new IOException("Размер ZIP превышает 16 МиБ.");
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count is < 1 or > MaxEntries)
            throw new IOException($"В архиве должно быть от 1 до {MaxEntries} файлов.");

        string temporary = Path.Combine(root, $".{Guid.NewGuid():N}.tmp");
        TextFiles.EnsurePrivateDirectory(temporary);
        var extracted = new List<(string Name, long Size)>();
        try
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            int remaining = MaxTotalBytes;
            foreach (var entry in archive.Entries)
            {
                string name = entry.FullName;
                int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (!TextFiles.IsFlatDocument(name) || unixType is not (0 or 0x8000) || !names.Add(name))
                    throw new IOException("В ZIP разрешены только файлы .txt, .json и .xml без каталогов, ссылок и повторяющихся имён.");

                int limit = Math.Min(TextFiles.MaxBytes, remaining);
                if (entry.Length > limit) throw new IOException("Превышен лимит: 1 МиБ на файл или 16 МиБ на все файлы.");
                using var contents = new MemoryStream();
                using var entryStream = entry.Open();
                int size = CopyLimited(entryStream, contents, limit);
                if (size != entry.Length) throw new InvalidDataException("Размер записи ZIP не совпадает с фактическим.");
                var bytes = contents.GetBuffer().AsSpan(0, size);
                Documents.Validate(name, TextFiles.Utf8.GetString(bytes));
                using var output = new FileStream(Path.Combine(temporary, name), FileMode.CreateNew, FileAccess.Write);
                output.Write(bytes);
                output.Flush(flushToDisk: true);
                remaining -= size;
                extracted.Add(($"{target}/{name}", size));
            }
            Directory.Move(temporary, destination);
            return extracted;
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    private static int CopyLimited(Stream source, Stream destination, int limit)
    {
        byte[] buffer = new byte[16 * 1024];
        int total = 0;
        int read;
        while ((read = source.Read(buffer)) != 0)
        {
            if (read > limit - total)
                throw new IOException("Превышен лимит: 1 МиБ на файл или 16 МиБ на все файлы.");
            destination.Write(buffer.AsSpan(0, read));
            total += read;
        }
        return total;
    }
}
