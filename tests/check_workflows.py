"""Check end-to-end workflows after dotnet build, using a temporary data directory."""

import os
import json
from pathlib import Path
import sqlite3
import subprocess
import tempfile
from xml.etree import ElementTree
from zipfile import ZipFile


project = Path(__file__).resolve().parents[1]
app = project / "FileManager/bin/Debug/net10.0/FileManager.dll"
password = "Smoke-test-only-26!"


def run(data, *lines):
    result = subprocess.run(
        ["dotnet", str(app)],
        input="\n".join((*lines, "")),
        capture_output=True,
        encoding="utf-8",
        env={**os.environ, "TISHINA_DATA_DIR": str(data)},
        timeout=30,
    )
    assert result.returncode == 0, (result.stdout, result.stderr)
    return result.stdout


with tempfile.TemporaryDirectory(prefix="tishina-test-") as directory:
    data = Path(directory)
    first = run(
        data, "read", "register", "first_user", password, "login", "first_user", password,
        "write", "note.txt", "Первая версия", ".end", "read", "note.txt",
        "write", "note.txt", "Обновлённая версия", ".end", "logout", "exit",
    )
    for message in ("Сначала войдите", "Учётная запись создана", "Вход выполнен", "Файл создан",
                    "Первая версия", "Файл изменён"):
        assert message in first, first

    second = run(
        data, "register", "second_user", password, "login", "second_user", password,
        "read", "note.txt", "write", "note.txt", "Запись второго пользователя", ".end",
        "logout", "login", "first_user", password, "read", "note.txt", "exit",
    )
    assert "файл не найден" in second and "Обновлённая версия" in second, second
    assert "Запись второго пользователя" not in second, second
    with sqlite3.connect(data / "data.sqlite") as db:
        assert db.execute("SELECT name, size FROM files ORDER BY owner_id").fetchall() == [
            ("note.txt", len("Обновлённая версия\n".encode())),
            ("note.txt", len("Запись второго пользователя\n".encode())),
        ]

    third = run(
        data, "login", "first_user", password, "drives", "list",
        "delete", "note.txt", "нет", "read", "note.txt",
        "delete", "note.txt", "да", "list", "read", "note.txt", "exit",
    )
    for message in ("пользователь> note.txt\n", "Удаление отменено.", "Обновлённая версия",
                    "Файл удалён.", "Нет файлов.", "файл не найден"):
        assert message in third, third
    assert "доступно " in third or "Доступные локальные тома не найдены." in third

    with sqlite3.connect(data / "data.sqlite") as db:
        accounts = db.execute("SELECT id, salt, password_hash, iterations FROM users ORDER BY id").fetchall()
        assert len(accounts) == 2 and accounts[0][1] != accounts[1][1]
        assert accounts[0][2] != accounts[1][2]
        assert all(len(salt) == 16 and len(digest) == 32 and iterations == 600_000
                   for _, salt, digest, iterations in accounts)
        files = db.execute("SELECT owner_id, name, size FROM files ORDER BY owner_id").fetchall()
        assert files == [
            (accounts[1][0], "note.txt", len("Запись второго пользователя\n".encode())),
        ]
        assert not (data / f"user-{accounts[0][0]}" / "note.txt").exists()
        assert (data / f"user-{accounts[1][0]}" / "note.txt").read_text(encoding="utf-8") == "Запись второго пользователя\n"
        events = db.execute("SELECT user_id, action, success FROM operations").fetchall()
        assert (None, "read", 0) in events
        assert (accounts[0][0], "create", 1) in events
        assert (accounts[0][0], "modify", 1) in events
        assert (accounts[1][0], "read", 0) in events
        assert (accounts[0][0], "list", 1) in events
        assert (accounts[0][0], "drives", 1) in events
        assert (accounts[0][0], "delete_cancelled", 1) in events
        assert events.count((accounts[0][0], "delete", 1)) == 1

    documents = run(
        data, "login", "first_user", password,
        "write", "data.json", '{"value": 5}', ".end",
        "write", "data.xml", "<item>пример</item>", ".end",
        "write", "data.json", '{"value":', ".end",
        "write", "data.xml", "<item>", ".end",
        "read", "data.json", "read", "data.xml",
        "save-card", "card.json", "Карточка JSON", "Описание 1",
        "save-card", "card.xml", "Карточка XML", "Книга & журнал <2026>",
        "read-card", "card.json", "read-card", "card.xml",
        "read-card", "data.json", "read-card", "data.xml", "exit",
    )
    for message in ('{"value": 5}', "<item>пример</item>", "некорректный JSON",
                    "некорректный XML", "Заголовок: Карточка JSON", "Описание: Описание 1",
                    "Заголовок: Карточка XML", "Описание: Книга & журнал <2026>",
                    "Ожидается XML-карточка"):
        assert message in documents, documents

    folder = data / f"user-{accounts[0][0]}"
    assert (folder / "data.json").read_text(encoding="utf-8") == '{"value": 5}\n'
    assert (folder / "data.xml").read_text(encoding="utf-8") == "<item>пример</item>\n"
    assert json.loads((folder / "card.json").read_text(encoding="utf-8")) == {
        "Title": "Карточка JSON", "Description": "Описание 1",
    }
    assert ElementTree.parse(folder / "card.xml").findtext("Description") == "Книга & журнал <2026>"
    with sqlite3.connect(data / "data.sqlite") as db:
        assert db.execute("""
            SELECT action, target, success FROM operations
            WHERE action IN ('write', 'read-card') ORDER BY id;
            """).fetchall() == [
                ("write", "data.json", 0), ("write", "data.xml", 0),
                ("read-card", "card.json", 1), ("read-card", "card.xml", 1),
                ("read-card", "data.json", 0), ("read-card", "data.xml", 0),
            ]

    # Обычный ZIP, в котором второй документ имеет неподдерживаемый формат.
    with ZipFile(folder / "mixed.zip", "w") as archive:
        archive.writestr("notes.txt", "Пример")
        archive.writestr("table.csv", "name,size\nnotes,12\n")

    zipped = run(
        data, "login", "first_user", password,
        "zip", "bundle.zip", "data.json, card.xml",
        "zip", "bundle.zip", "data.xml",
        "unzip", "bundle.zip", "copies",
        "unzip", "bundle.zip", "copies",
        "unzip", "mixed.zip", "mixed",
        "list", "read", "copies/data.json", "read-card", "copies/card.xml", "exit",
    )
    assert zipped.count("Архив создан.") == 1, zipped
    assert zipped.count("Архив распакован. Файлов: 2.") == 1, zipped
    for message in ("Каталог назначения уже существует", "В ZIP разрешены только файлы",
                    "copies/data.json\n", '{"value": 5}', "Заголовок: Карточка XML"):
        assert message in zipped, zipped
    assert not (folder / "mixed").exists()
    assert not list(folder.glob(".*.tmp"))
    with ZipFile(folder / "bundle.zip") as archive:
        assert archive.namelist() == ["data.json", "card.xml"]
        for name in archive.namelist():
            assert archive.read(name) == (folder / "copies" / name).read_bytes()

    with sqlite3.connect(data / "data.sqlite") as db:
        assert db.execute("""
            SELECT action, target, success FROM operations
            WHERE action IN ('zip', 'unzip') ORDER BY id;
            """).fetchall() == [
                ("zip", "bundle.zip", 1), ("zip", "bundle.zip", 0),
                ("unzip", "copies", 1), ("unzip", "copies", 0), ("unzip", "mixed", 0),
            ]
        names = {"bundle.zip", "copies/card.xml", "copies/data.json"}
        entries = db.execute("SELECT name, size FROM files WHERE owner_id = ?", (accounts[0][0],))
        assert {name: size for name, size in entries if name in names} == {
            name: (folder / name).stat().st_size for name in names
        }
        assert db.execute("SELECT count(*) FROM files WHERE name LIKE 'mixed/%'").fetchone() == (0,)

    guest_history = run(data, "history", "exit")
    assert "Сначала войдите" in guest_history and "Личная история" not in guest_history, guest_history
    changes = run(data, "login", "first_user", password, "history changes", "exit")
    assert "успех | unzip | copies" in changes and "отказ |" not in changes, changes
    assert "успех | read-card |" not in changes, changes
    errors = run(data, "login", "first_user", password, "history errors", "exit")
    assert "отказ | unzip | mixed" in errors and "успех |" not in errors, errors
    other = run(data, "login", "second_user", password, "history", "exit")
    assert "успех | create | note.txt" in other and "отказ | read | note.txt" in other, other
    assert all(name not in other for name in ("bundle.zip", "copies", "mixed")), other

print("OK: учётные записи, файлы, накопители, JSON/XML, карточки, ZIP и личная история SQLite.")
