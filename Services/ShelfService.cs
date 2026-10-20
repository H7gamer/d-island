// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.IO;
using System.Text.Json;

namespace DIsland.Services;

/// <summary>
/// The file shelf: paths dropped on the island, kept until dragged out or removed. Stores references
/// (not copies), in %LocalAppData%\DIsland\shelf.json so the shelf survives restarts.
/// </summary>
public sealed class ShelfService
{
    public const int MaxItems = 60;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DIsland", "shelf.json");

    private readonly List<string> _items = new();

    public ShelfService()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) is { } saved)
                _items.AddRange(saved.Where(p => !string.IsNullOrWhiteSpace(p)).Take(MaxItems));
        }
        catch (Exception)
        {
            // Unreadable shelf: start empty.
        }
    }

    public IReadOnlyList<string> Items => _items;

    public event Action? Changed;

    /// <summary>Adds paths not already on the shelf (newest last). Returns how many were added.</summary>
    public int Add(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (_items.Count >= MaxItems) break;
            if (_items.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            _items.Add(path);
            added++;
        }
        if (added > 0) Save();
        return added;
    }

    public void Remove(string path)
    {
        if (_items.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0) Save();
    }

    /// <summary>Drops items whose file or folder no longer exists (moved out, deleted).</summary>
    public void RemoveMissing()
    {
        if (_items.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p)) > 0) Save();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_items));
        }
        catch (Exception)
        {
            // Non-fatal: the shelf just won't persist.
        }
        Changed?.Invoke();
    }
}
