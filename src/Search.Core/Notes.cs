using System.Collections;
using System.Text.Json;

namespace Search.Core;

// The small notes the Mac keeps in its settings beside the files (UserDefaults keys such as "spaces",
// "space.current", "bookmarks.lastFolder.<file>"): one string per key, kept in notes.json in the store's folder and
// written a moment after each change. The models that keep them take an IDictionary, so tests can give them a plain one.
public sealed class Notes : IDictionary<string, string>
{
    private readonly Dictionary<string, string> _notes;
    private readonly string _file;

    public Notes(string file = "notes.json")
    {
        _file = Store.File(file);
        _notes = Read(_file);
    }

    private static Dictionary<string, string> Read(string file)
    {
        try { return System.IO.File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllBytes(file)) ?? new() : new(); }
        catch (JsonException) { Store.Quarantine(file); return new(); }
    }

    private void Save() { var copy = new Dictionary<string, string>(_notes); Disk.Write(_file, () => JsonSerializer.SerializeToUtf8Bytes(copy)); }

    public string this[string key] { get => _notes[key]; set { if (_notes.TryGetValue(key, out var was) && was == value) return; _notes[key] = value; Save(); } }
    public ICollection<string> Keys => _notes.Keys;
    public ICollection<string> Values => _notes.Values;
    public int Count => _notes.Count;
    public bool IsReadOnly => false;
    public void Add(string key, string value) { _notes.Add(key, value); Save(); }
    public void Add(KeyValuePair<string, string> item) => Add(item.Key, item.Value);
    public void Clear() { _notes.Clear(); Save(); }
    public bool Contains(KeyValuePair<string, string> item) => _notes.TryGetValue(item.Key, out var v) && v == item.Value;
    public bool ContainsKey(string key) => _notes.ContainsKey(key);
    public void CopyTo(KeyValuePair<string, string>[] array, int index) => ((ICollection<KeyValuePair<string, string>>)_notes).CopyTo(array, index);
    public bool Remove(string key) { if (!_notes.Remove(key)) return false; Save(); return true; }
    public bool Remove(KeyValuePair<string, string> item) => Contains(item) && Remove(item.Key);
    public bool TryGetValue(string key, out string value) => _notes.TryGetValue(key, out value!);
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _notes.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
