using System.Resources;
using System.Text;

namespace FireGaze.UIText;

/// <summary>
/// Reads resource names and raw payloads without constructing vendor objects.
/// Unknown resource types survive display-text edits and restores byte for byte.
/// </summary>
internal static class UITextBinaryResources
{
    internal sealed record Entry(string Key, string TypeName, byte[] Data)
    {
        public string? StringValue
        {
            get
            {
                if (TypeName != "ResourceTypeCode.String") return null;
                using var stream = new MemoryStream(Data, writable: false);
                using var reader = new BinaryReader(stream, Encoding.UTF8);
                var value = reader.ReadString();
                if (stream.Position != stream.Length)
                    throw new InvalidDataException("String resource contains trailing data.");
                return value;
            }
        }

        public Entry WithStringValue(string value)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                writer.Write(value);
            return new Entry(Key, "ResourceTypeCode.String", stream.ToArray());
        }
    }

    public static List<Entry> Read(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var reader = new ResourceReader(stream);
        var entries = new List<Entry>();
        var enumerator = reader.GetEnumerator();
        while (enumerator.MoveNext())
        {
            // enumerator.Value / DictionaryEntry would deserialize custom types.
            // Only the name and GetResourceData's raw bytes are needed here.
            if (enumerator.Key is not string key)
                throw new InvalidDataException("Resource key is not a string.");
            reader.GetResourceData(key, out var typeName, out var payload);
            entries.Add(new Entry(key, typeName, payload));
        }
        return entries;
    }

    public static byte[] Write(IEnumerable<Entry> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new ResourceWriter(stream))
        {
            foreach (var entry in entries)
                writer.AddResourceData(entry.Key, entry.TypeName, entry.Data);
            writer.Generate();
        }
        return stream.ToArray();
    }
}
