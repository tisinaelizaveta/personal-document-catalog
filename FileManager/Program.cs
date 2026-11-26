using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Data.Sqlite;

namespace FileManager;

internal static partial class Program
{
    [GeneratedRegex(@"\A[a-z0-9_]{3,32}\z")]
    private static partial Regex ValidUsername();

    private static int Main()
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("Приложение рассчитано на Linux.");
            return 1;
        }
        try
        {
            string data = Path.GetFullPath(Environment.GetEnvironmentVariable("TISHINA_DATA_DIR") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tishina-file-manager"));
            TextFiles.EnsurePrivateDirectory(data);
            using var instanceLock = new FileStream(Path.Combine(data, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite);
            instanceLock.Lock(0, 1);
            using var db = new Database(Path.Combine(data, "data.sqlite"));
            Run(db, new TextFiles(data));
            return 0;
        }
        catch (SqliteException)
        {
            Console.Error.WriteLine("Ошибка SQLite. Работа остановлена. Если ошибка произошла при изменении файлов, диск и метаданные могут не совпадать.");
            return 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"Работа остановлена: {error.Message}");
            return 1;
        }
    }

    private static void Run(Database db, TextFiles files)
    {
        long? user = null;
        var archives = new Archives(files);
        Console.WriteLine("Введите help для списка команд, exit — для выхода.");
        while (true)
        {
            Console.Write(user is null ? "гость> " : "пользователь> ");
            string? input = Console.ReadLine();
            if (input is null) return;
            string command = input.Trim().ToLowerInvariant();
            string? target = null;
            try
            {
                switch (command)
                {
                    case "register":
                    case "login":
                        if (user is not null) throw new InvalidOperationException("Сначала выйдите из учётной записи.");
                        string username = Prompt("Логин: ").Trim().ToLowerInvariant();
                        if (!ValidUsername().IsMatch(username))
                            throw new ArgumentException("Логин: от 3 до 32 латинских букв, цифр и подчёркиваний.");
                        string password = ReadPassword();
                        if (password.Length > 256 || (command == "register" && password.Length < 12))
                            throw new ArgumentException("Пароль при регистрации: от 12 до 256 символов.");
                        if (command == "register")
                        {
                            if (!db.Register(username, password))
                                throw new InvalidOperationException("Такой логин уже занят.");
                            db.Log(null, "register", username, success: true);
                            Console.WriteLine("Учётная запись создана.");
                        }
                        else
                        {
                            user = db.Authenticate(username, password);
                            if (user is null)
                            {
                                Thread.Sleep(1000);
                                throw new InvalidOperationException("Неверный логин или пароль.");
                            }
                            db.Log(user, "login", null, success: true);
                            Console.WriteLine("Вход выполнен.");
                        }
                        break;
                    case "logout":
                        db.Log(RequireUser(user), "logout", null, success: true);
                        user = null;
                        Console.WriteLine("Вы вышли из учётной записи.");
                        break;
                    case "read":
                    case "write":
                    case "delete":
                        long owner = RequireUser(user);
                        target = TextFiles.CheckName(Prompt("Имя файла: ").Trim(), allowArchive: command == "delete");
                        if (command == "read")
                        {
                            string text = files.Read(owner, target);
                            db.Log(owner, "read", target, success: true);
                            Console.WriteLine(ForDisplay(text));
                        }
                        else if (command == "write")
                        {
                            Save(db, files, owner, target, ReadText());
                        }
                        else
                        {
                            if (!string.Equals(Prompt($"Удалить «{target}»? Введите да для подтверждения: ").Trim(),
                                "да", StringComparison.OrdinalIgnoreCase))
                            {
                                db.Log(owner, "delete_cancelled", target, success: true);
                                Console.WriteLine("Удаление отменено.");
                                break;
                            }
                            files.Delete(owner, target);
                            db.RecordDelete(owner, target);
                            Console.WriteLine("Файл удалён.");
                        }
                        break;
                    case "save-card":
                    case "read-card":
                        long cardOwner = RequireUser(user);
                        target = TextFiles.CheckName(Prompt("Имя карточки (.json или .xml): ").Trim());
                        Documents.RequireCardFormat(target);
                        if (command == "save-card")
                        {
                            var card = new DocumentCard(Prompt("Заголовок: "), Prompt("Описание: "));
                            Save(db, files, cardOwner, target, Documents.EncodeCard(target, card));
                        }
                        else
                        {
                            var card = Documents.DecodeCard(target, files.Read(cardOwner, target));
                            db.Log(cardOwner, "read-card", target, success: true);
                            Console.WriteLine($"Заголовок: {ForDisplay(card.Title)}");
                            Console.WriteLine($"Описание: {ForDisplay(card.Description)}");
                        }
                        break;
                    case "zip":
                        long zipOwner = RequireUser(user);
                        target = Archives.CheckArchiveName(Prompt("Имя нового ZIP: ").Trim());
                        string[] sources = Prompt("Имена исходных файлов через запятую: ").Split(',', StringSplitOptions.TrimEntries);
                        long zipSize = archives.Create(zipOwner, target, sources);
                        db.RecordArchive(zipOwner, "zip", target, [(target, zipSize)]);
                        Console.WriteLine("Архив создан.");
                        break;
                    case "unzip":
                        long unzipOwner = RequireUser(user);
                        string source = Archives.CheckArchiveName(Prompt("Имя ZIP: ").Trim());
                        target = TextFiles.CheckFolder(Prompt("Имя нового каталога: ").Trim());
                        var extracted = archives.Extract(unzipOwner, source, target);
                        db.RecordArchive(unzipOwner, "unzip", target, extracted);
                        Console.WriteLine($"Архив распакован. Файлов: {extracted.Count}.");
                        break;
                    case "list":
                        long listOwner = RequireUser(user);
                        string[] names = files.List(listOwner);
                        db.Log(listOwner, "list", null, success: true);
                        Console.WriteLine(names.Length == 0 ? "Нет файлов." : string.Join('\n', names));
                        break;
                    case "drives":
                        long drivesUser = RequireUser(user);
                        var volumes = Drives.DescribeLocalVolumes();
                        db.Log(drivesUser, "drives", null, success: true);
                        Console.WriteLine(volumes.Count == 0 ? "Доступные локальные тома не найдены." : string.Join('\n', volumes));
                        break;
                    case "history":
                    case "history changes":
                    case "history errors":
                        long historyOwner = RequireUser(user);
                        var filter = command switch
                        {
                            "history changes" => HistoryFilter.Changes,
                            "history errors" => HistoryFilter.Errors,
                            _ => HistoryFilter.All
                        };
                        var entries = db.ReadHistory(historyOwner, filter);
                        db.Log(historyOwner, "history", filter.ToString(), success: true);
                        Console.WriteLine("Личная история (UTC; до 20 записей):");
                        if (entries.Count == 0) Console.WriteLine("Нет записей для этого фильтра.");
                        foreach (var entry in entries)
                            Console.WriteLine($"{entry.Timestamp} | {(entry.Success ? "успех" : "отказ")} | " +
                                $"{ForDisplay(entry.Action)} | {ForDisplay(entry.Target ?? "—")}");
                        break;
                    case "help":
                        db.Log(user, "help", null, success: true);
                        Console.WriteLine("""
                            register / login / logout — учётная запись
                            list — список файлов; write / read / delete — работа с .txt, .json и .xml
                            save-card / read-card — сохранить и прочитать карточку в JSON или XML
                            zip / unzip — создать и распаковать ZIP; delete также удаляет архив
                            history / history changes / history errors — личная история, изменения и отказы
                            drives — сведения о накопителях; exit — выход
                            """);
                        break;
                    case "exit":
                        db.Log(user, "exit", null, success: true);
                        return;
                    default:
                        if (command.Length == 0) break;
                        db.Log(user, "unknown", null, success: false);
                        Console.WriteLine("Неизвестная команда. Введите help.");
                        break;
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException or JsonException or XmlException or NotSupportedException)
            {
                db.Log(user, command, target, success: false);
                string message = error switch
                {
                    FileNotFoundException => "файл не найден.",
                    JsonException => "некорректный JSON или поля карточки.",
                    XmlException => "некорректный XML. DTD не поддерживаются.",
                    InvalidDataException => "некорректный или неподдерживаемый ZIP.",
                    DecoderFallbackException => "файл должен быть в кодировке UTF-8.",
                    _ => error.Message
                };
                Console.WriteLine($"Ошибка: {message}");
            }
        }
    }

    private static void Save(Database db, TextFiles files, long user, string name, string content)
    {
        var (size, existed) = files.Write(user, name, content);
        db.RecordWrite(user, name, size, existed);
        Console.WriteLine(existed ? "Файл изменён." : "Файл создан.");
    }

    private static string ForDisplay(string text) => string.Concat(text
        .Select(c => char.IsControl(c) && c is not ('\n' or '\t') ? '�' : c));

    private static long RequireUser(long? user) =>
        user ?? throw new InvalidOperationException("Сначала войдите в учётную запись.");

    private static string Prompt(string label)
    {
        Console.Write(label);
        return Console.ReadLine() ?? throw new EndOfStreamException("Ввод прерван.");
    }

    private static string ReadText()
    {
        Console.WriteLine("Введите текст. Строка .end завершает ввод:");
        var text = new StringBuilder();
        int bytes = 0;
        while (true)
        {
            string line = Console.ReadLine() ?? throw new EndOfStreamException("Ввод прерван; файл не изменён.");
            if (line == ".end") return text.ToString();
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(line) + 1);
            if (bytes > TextFiles.MaxBytes) throw new ArgumentException("Текст превышает 1 МиБ.");
            text.Append(line).Append('\n');
        }
    }

    private static string ReadPassword()
    {
        if (Console.IsInputRedirected) return Prompt("Пароль: ");
        Console.Write("Пароль (Esc — отмена): ");
        var password = new StringBuilder();
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) return password.ToString();
                if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException("Ввод пароля отменён.");
                if (key.Key == ConsoleKey.Backspace && password.Length > 0) password.Length--;
                else if (!char.IsControl(key.KeyChar) && password.Length < 257) password.Append(key.KeyChar);
            }
        }
        finally
        {
            Console.WriteLine();
        }
    }
}
