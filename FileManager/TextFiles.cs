using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FileManager;

internal sealed partial class TextFiles(string root)
{
    internal const int MaxBytes = 1024 * 1024;
    internal const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}\.(?:txt|json|xml)\z")]
    private static partial Regex ValidName();

    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}\z")]
    private static partial Regex ValidFolder();

    public static string CheckName(string name, bool allowArchive = false)
    {
        string[] parts = name.Split('/');
        if (parts.Length is < 1 or > 2 ||
            (parts.Length == 2 && !ValidFolder().IsMatch(parts[0])) ||
            !(ValidName().IsMatch(parts[^1]) ||
                (allowArchive && parts.Length == 1 && IsArchiveName(name))))
            throw new ArgumentException("Используйте имя файла .txt, .json или .xml, при необходимости с одним каталогом: папка/имя (латиница).");
        return name;
    }

    internal static bool IsFlatDocument(string name) => ValidName().IsMatch(name);

    internal static bool IsArchiveName(string name) => name.EndsWith(".zip", StringComparison.Ordinal)
        && ValidFolder().IsMatch(name[..^4]);

    internal static string CheckFolder(string name) => ValidFolder().IsMatch(name) ? name
        : throw new ArgumentException("Введите имя каталога латиницей, без разделителей пути.");

    public (int Size, bool Existed) Write(long userId, string name, string text)
    {
        byte[] bytes = Utf8.GetBytes(text);
        if (bytes.Length > MaxBytes) throw new ArgumentException("Текст превышает 1 МиБ.");
        Documents.Validate(name, text);
        string path = Resolve(userId, name);
        bool existed = File.Exists(path);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp");
        var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            using (output)
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: existed);
        }
        finally
        {
            File.Delete(temporary);
        }
        return (bytes.Length, existed);
    }

    public string Read(long userId, string name)
    {
        using var stream = File.OpenRead(Resolve(userId, name));
        byte[] buffer = new byte[MaxBytes + 1];
        int count = stream.ReadAtLeast(buffer, buffer.Length, false);
        if (count > MaxBytes) throw new IOException("Файл превышает 1 МиБ.");
        return Utf8.GetString(buffer, 0, count);
    }

    public string[] List(long userId)
    {
        string folder = UserDirectory(userId);
        var files = Names(folder).Where(name => IsFlatDocument(name) || IsArchiveName(name)).ToList();
        foreach (var path in Directory.EnumerateDirectories(folder))
        {
            string name = Path.GetFileName(path);
            if (!ValidFolder().IsMatch(name) || new DirectoryInfo(path).LinkTarget is not null) continue;
            files.AddRange(Names(path).Where(IsFlatDocument).Select(file => $"{name}/{file}"));
        }
        return files.Order(StringComparer.Ordinal).ToArray();

        static IEnumerable<string> Names(string directory) => Directory.EnumerateFiles(directory)
            .Where(path => new FileInfo(path).LinkTarget is null).Select(path => Path.GetFileName(path));
    }

    public void Delete(long userId, string name)
    {
        string path = Resolve(userId, name, allowArchive: true);
        if (!File.Exists(path)) throw new FileNotFoundException();
        File.Delete(path);
    }

    internal string UserDirectory(long userId)
    {
        string folder = Path.Combine(root, $"user-{userId.ToString(CultureInfo.InvariantCulture)}");
        EnsurePrivateDirectory(folder);
        return folder;
    }

    internal string Resolve(long userId, string name, bool allowArchive = false)
    {
        CheckName(name, allowArchive);
        string root = UserDirectory(userId);
        string path = Path.Combine(root, name);
        if (name.Contains('/'))
        {
            var folder = new DirectoryInfo(Path.GetDirectoryName(path)!);
            if (folder.LinkTarget is not null) throw new IOException("Символические ссылки не поддерживаются.");
            if (!folder.Exists) throw new DirectoryNotFoundException("Каталог не найден.");
        }
        if (new FileInfo(path).LinkTarget is not null)
            throw new IOException("Символические ссылки не поддерживаются.");
        return path;
    }

    public static void EnsurePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Требуется Linux.");
        if (new DirectoryInfo(path).LinkTarget is not null)
            throw new IOException("Каталог данных не должен быть символической ссылкой.");
        Directory.CreateDirectory(path, PrivateDirectory);
        if (File.GetUnixFileMode(path) != PrivateDirectory)
            throw new IOException("У каталога данных должны быть права 0700.");
    }
}
