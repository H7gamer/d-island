// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace DIsland.Services;

public sealed class TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
}

/// <summary>
/// The Notes page's text and the To-Do list, in %LocalAppData%\DIsland\notes.json. Kept on this PC
/// only. Typing is saved half a second after it stops (and on exit); list changes at once.
/// </summary>
public sealed class NotesService : IDisposable
{
    public const int MaxTodos = 200;
    public const int MaxNoteLength = 20000;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DIsland", "notes.json");

    private readonly List<TodoItem> _todos = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string _note = "";

    private sealed class Data
    {
        public string Note { get; set; } = "";
        public List<TodoItem> Todos { get; set; } = new();
    }

    public NotesService()
    {
        _saveTimer.Tick += (_, _) => Save();
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) is { } saved)
            {
                _note = saved.Note ?? "";
                _todos.AddRange(saved.Todos.Where(t => !string.IsNullOrWhiteSpace(t.Text)).Take(MaxTodos));
            }
        }
        catch (Exception)
        {
            // Unreadable file: start empty, but keep the old one in case it can be recovered by hand.
            try { File.Copy(FilePath, FilePath + ".unreadable", overwrite: true); }
            catch (Exception) { /* nothing to keep */ }
        }
    }

    /// <summary>The list changed (not raised for note typing, which the text box itself shows).</summary>
    public event Action? Changed;

    public string Note
    {
        get => _note;
        set
        {
            value ??= "";
            if (value.Length > MaxNoteLength) value = value[..MaxNoteLength];
            if (value == _note) return;
            _note = value;
            _saveTimer.Stop();
            _saveTimer.Start(); // debounced: saved once typing pauses
        }
    }

    /// <summary>Open tasks first (oldest first, the order they were added), then finished ones.</summary>
    public IReadOnlyList<TodoItem> Todos => _todos.Where(t => !t.Done).Concat(_todos.Where(t => t.Done)).ToList();

    public int OpenCount => _todos.Count(t => !t.Done);
    public int DoneCount => _todos.Count(t => t.Done);

    public bool Add(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || _todos.Count >= MaxTodos) return false;
        _todos.Add(new TodoItem { Text = text.Length > 300 ? text[..300] : text });
        SaveAndNotify();
        return true;
    }

    public void SetDone(Guid id, bool done)
    {
        if (_todos.FirstOrDefault(t => t.Id == id) is not { } item || item.Done == done) return;
        item.Done = done;
        SaveAndNotify();
    }

    public void Remove(Guid id)
    {
        if (_todos.RemoveAll(t => t.Id == id) > 0) SaveAndNotify();
    }

    public void ClearDone()
    {
        if (_todos.RemoveAll(t => t.Done) > 0) SaveAndNotify();
    }

    /// <summary>Writes any pending typing now.</summary>
    public void Flush()
    {
        if (_saveTimer.IsEnabled) Save();
    }

    public void Dispose() => Flush();

    private void SaveAndNotify()
    {
        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        _saveTimer.Stop();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Write a temporary file first, then swap it in: a crash mid-write never loses the notes.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Data { Note = _note, Todos = _todos }));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }
}
