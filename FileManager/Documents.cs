using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace FileManager;

internal sealed record DocumentCard(string Title, string Description);

internal static class Documents
{
    private static readonly JsonSerializerOptions CardJson = new()
    {
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static void Validate(string name, string text)
    {
        switch (Path.GetExtension(name))
        {
            case ".json":
                using (JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 })) { }
                break;
            case ".xml":
                using (var reader = OpenXml(text))
                    while (reader.Read()) { }
                break;
        }
    }

    public static void RequireCardFormat(string name)
    {
        if (Path.GetExtension(name) is not (".json" or ".xml"))
            throw new ArgumentException("Для карточки выберите расширение .json или .xml.");
    }

    public static string EncodeCard(string name, DocumentCard card)
    {
        RequireCardFormat(name);
        CheckCard(card);
        return Path.GetExtension(name) == ".json"
            ? JsonSerializer.Serialize(card, CardJson)
            : new XElement("DocumentCard", new XElement("Title", card.Title),
                new XElement("Description", card.Description)).ToString();
    }

    public static DocumentCard DecodeCard(string name, string text)
    {
        RequireCardFormat(name);
        DocumentCard? card;
        if (Path.GetExtension(name) == ".json")
        {
            card = JsonSerializer.Deserialize<DocumentCard>(text, CardJson);
        }
        else
        {
            using var reader = OpenXml(text);
            var root = XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root;
            if (root is null || root.Name != "DocumentCard" || root.HasAttributes)
                throw new ArgumentException("Ожидается XML-карточка DocumentCard.");
            var fields = root.Elements().ToArray();
            if (fields.Length != 2 || fields.Count(field => field.Name == "Title") != 1 ||
                fields.Count(field => field.Name == "Description") != 1 ||
                fields.Any(field => field.HasElements || field.HasAttributes) ||
                root.Nodes().OfType<XText>().Any(node => !string.IsNullOrWhiteSpace(node.Value)))
                throw new ArgumentException("Карточка должна содержать только поля Title и Description.");
            card = new DocumentCard(root.Element("Title")!.Value, root.Element("Description")!.Value);
        }
        return CheckCard(card);
    }

    private static DocumentCard CheckCard(DocumentCard? card)
    {
        if (card is null || string.IsNullOrWhiteSpace(card.Title) || card.Title.Length > 120 ||
            card.Description is null || card.Description.Length > 2000)
            throw new ArgumentException("Карточка: непустой заголовок до 120 и описание до 2000 символов.");
        return card;
    }

    private static XmlReader OpenXml(string text) => XmlReader.Create(new StringReader(text), new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = TextFiles.MaxBytes,
        CloseInput = true
    });
}
