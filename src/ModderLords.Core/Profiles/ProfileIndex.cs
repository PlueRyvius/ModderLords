using System.Text.Json;

namespace ModderLords.Core.Profiles;

/// <summary>
/// What the Profiles window remembers across profiles (docs/PROFILE-MANAGER.md): the recently used ones, for the
/// quick-switch menu, and the group names, so a group can exist before anything is in it. A file of its own
/// (<c>profile-index.json</c> beside <c>ui-state.json</c>, not in the profiles folder, where every *.json is a profile):
/// ui-state.json is written whole by several holders, which would undo each other's changes to these lists.
/// </summary>
public sealed class ProfileIndex
{
    public List<string> Recent { get; set; } = new();
    public List<string> Groups { get; set; } = new();
}

public static class ProfileIndexStore
{
    /// <summary>How many recent profiles the quick-switch menu offers.</summary>
    public const int RecentCount = 6;

    public static string Path => PathOverride ?? System.IO.Path.Combine(ProfileStore.RootDir, "profile-index.json");

    /// <summary>Tests only: where the index lives, so a test never touches the real recent list.</summary>
    internal static string? PathOverride { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();

    /// <summary>Never throws: a broken index costs the recent list, not the app.</summary>
    public static ProfileIndex Load()
    {
        try { return File.Exists(Path) ? JsonSerializer.Deserialize<ProfileIndex>(File.ReadAllText(Path), Json) ?? new() : new(); }
        catch { return new(); }
    }

    private static void Change(Action<ProfileIndex> change)
    {
        lock (Gate)
        {
            var index = Load();
            change(index);
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var tmp = Path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(index, Json));
                File.Move(tmp, Path, overwrite: true);
            }
            catch { }
        }
    }

    /// <summary>A profile was opened: it goes to the front of the recent list.</summary>
    public static void Used(string name) => Change(i =>
    {
        i.Recent.RemoveAll(r => r.Equals(name, StringComparison.OrdinalIgnoreCase));
        i.Recent.Insert(0, name);
        if (i.Recent.Count > RecentCount) i.Recent.RemoveRange(RecentCount, i.Recent.Count - RecentCount);
    });

    public static void Renamed(string oldName, string newName) => Change(i =>
    {
        for (var n = 0; n < i.Recent.Count; n++)
            if (i.Recent[n].Equals(oldName, StringComparison.OrdinalIgnoreCase)) i.Recent[n] = newName;
    });

    public static void Forget(string name) => Change(i => i.Recent.RemoveAll(r => r.Equals(name, StringComparison.OrdinalIgnoreCase)));

    public static void AddGroup(string group) => Change(i =>
    {
        if (!i.Groups.Contains(group, StringComparer.OrdinalIgnoreCase)) i.Groups.Add(group);
    });

    public static void RemoveGroup(string group) => Change(i => i.Groups.RemoveAll(g => g.Equals(group, StringComparison.OrdinalIgnoreCase)));
}
