namespace Tozot;

/// <summary>
/// A meter the game keeps in its own memory rather than in GameData: Link's hearts (PlayerStatus.Life) and his magic
/// (PlayerStatus.MP). A game system holds the value and sends it to GameData every frame, so the record follows it and a
/// write to the record alone is undone the next frame. While a switch is on, the value is put back to the meter's
/// maximum (the Max record) whenever it drops. The game holds the two meters in two different shapes:
///   Hearts: a handle {u64 class pointer into the program, u32 value, u32 name hash} (GameMemory.FindHandles).
///   Magic:  a tagged slot {u32 MP hash, u32 MPMax hash, u32 third hash, u32 value}: the value 12 bytes after the MP
///           hash (GameMemory.FindTagged). Checked live in Hylian Triforce on 2026-11-21: setting that value to 200
///           moved the MP record to 200, and it held; the handle search finds nothing for MP.
/// </summary>
public sealed class Kept
{
    /// <summary>The third tag of the magic slot, as the game stores it (its field name is not known).</summary>
    const uint MagicThirdTag = 0x3afa3349;

    public static Kept Health() => new("PlayerStatus.Life", "PlayerStatus.MaxLife");
    public static Kept Magic() => new("PlayerStatus.MP", "PlayerStatus.MPMax",
        new[] { Murmur.Hash("PlayerStatus.MP"), Murmur.Hash("PlayerStatus.MPMax"), MagicThirdTag });
    /// <summary>Heart containers: the MaxLife handle sits right after the Life handle; only Set is used on it.</summary>
    public static Kept HeartContainers() => new("PlayerStatus.MaxLife", "PlayerStatus.MaxLife");

    public string ValueName { get; }
    public string MaxName { get; }
    public uint Hash { get; }
    readonly uint[]? tags;   // set: a tagged slot (magic); null: a handle (hearts)
    List<ulong> handles = new();

    Kept(string value, string max, uint[]? tags = null) { ValueName = value; MaxName = max; Hash = Murmur.Hash(value); this.tags = tags; }

    public int Found => handles.Count;

    /// <summary>Searches for the game's copy (a few seconds; safe off the UI thread). Returns how many were found.</summary>
    public int Find(GameMemory g)
    {
        handles = tags != null ? g.FindTagged(tags) : g.FindHandles(Hash);
        return handles.Count;
    }

    uint? Value(GameMemory g, ulong h) => tags != null ? g.TaggedValue(h, tags) : g.HandleValue(h, Hash);
    bool Write(GameMemory g, ulong h, uint v) => tags != null ? g.KeepTagged(h, tags, v) : g.KeepHandle(h, Hash, v);

    /// <summary>True while every copy found is still in place (false after the game frees them, e.g. on the title screen).</summary>
    public bool Valid(GameMemory g) => handles.Count > 0 && handles.All(h => Value(g, h) != null);

    public uint Max(GameMemory g) => g.Get(Fields.Get(MaxName));

    /// <summary>Fills the meter if it dropped. Returns the value written, or null when nothing was needed.</summary>
    public uint? TopUp(GameMemory g) => Set(g, Max(g), onlyIfLower: true);

    /// <summary>Writes a value into the game's copy (and so, next frame, into the record). Null when nothing was written.</summary>
    public uint? Set(GameMemory g, uint v, bool onlyIfLower = false)
    {
        if (v == 0 && onlyIfLower) return null;
        uint? wrote = null;
        foreach (var h in handles)
            if (Value(g, h) is uint cur && (!onlyIfLower || cur < v) && Write(g, h, v)) wrote = v;
        return wrote;
    }
}
