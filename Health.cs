namespace Tozot;

/// <summary>
/// A meter the game keeps in its own handle object rather than in GameData: Link's hearts (PlayerStatus.Life) and his
/// magic (PlayerStatus.MP). A game system holds the handle and sends its value to GameData every frame, so the record
/// follows the handle and a write to the record alone is undone the next frame (see GameMemory.FindHandles). While a
/// switch is on, the handle is put back to the meter's maximum (the Max record) whenever it drops.
/// </summary>
public sealed class Kept
{
    public static Kept Health() => new("PlayerStatus.Life", "PlayerStatus.MaxLife");
    public static Kept Magic() => new("PlayerStatus.MP", "PlayerStatus.MPMax");
    /// <summary>Heart containers: the MaxLife handle sits right after the Life handle; only Set is used on it.</summary>
    public static Kept HeartContainers() => new("PlayerStatus.MaxLife", "PlayerStatus.MaxLife");

    public string ValueName { get; }
    public string MaxName { get; }
    public uint Hash { get; }
    List<ulong> handles = new();

    Kept(string value, string max) { ValueName = value; MaxName = max; Hash = Murmur.Hash(value); }

    public int Found => handles.Count;

    /// <summary>Searches for the handle (a few seconds; safe off the UI thread). Returns how many were found.</summary>
    public int Find(GameMemory g)
    {
        handles = g.FindHandles(Hash);
        return handles.Count;
    }

    /// <summary>True while every handle found is still in place (false after the game frees them, e.g. on the title screen).</summary>
    public bool Valid(GameMemory g) => handles.Count > 0 && handles.All(h => g.HandleValue(h, Hash) != null);

    public uint Max(GameMemory g) => g.Get(Fields.Get(MaxName));

    /// <summary>Fills the meter if it dropped. Returns the value written, or null when nothing was needed.</summary>
    public uint? TopUp(GameMemory g) => Set(g, Max(g), onlyIfLower: true);

    /// <summary>Writes a value into the handle (and so, next frame, into the game). Null when nothing was written.</summary>
    public uint? Set(GameMemory g, uint v, bool onlyIfLower = false)
    {
        if (v == 0 && onlyIfLower) return null;
        uint? wrote = null;
        foreach (var h in handles)
            if (g.HandleValue(h, Hash) is uint cur && (!onlyIfLower || cur < v) && g.KeepHandle(h, Hash, v)) wrote = v;
        return wrote;
    }
}
