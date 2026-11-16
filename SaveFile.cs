using System.IO;

namespace Tozot;

/// <summary>
/// One save slot's progress.sav (GameData format): a header, then (name hash, value) pairs grouped by type, each group
/// after a zero-hash marker whose value is the type's section number (Bool 0, Int 2, Enum 6). Values of these three types
/// sit in the pair itself. Only fields of the table in Fields.cs are touched, each found by its exact hash and checked to
/// sit in its type's section.
/// </summary>
public sealed class SaveFile
{
    const uint Magic = 0x01020304;
    readonly byte[] data;
    readonly Dictionary<uint, (int offset, int section)> pairs = new();

    public string SlotDir { get; }
    public string Path => System.IO.Path.Combine(SlotDir, "progress.sav");

    public SaveFile(string slotDir)
    {
        SlotDir = slotDir;
        data = File.ReadAllBytes(Path);
        if (data.Length < 0x28 || BitConverter.ToUInt32(data, 0) != Magic)
            throw new InvalidDataException("This is not an Ocarina of Time save (wrong header).");
        int end = (int)BitConverter.ToUInt32(data, 8);
        if (end <= 0x20 || end > data.Length) throw new InvalidDataException("The save's header is damaged.");
        int section = -1;
        for (int o = 0x20; o + 8 <= end; o += 8)
        {
            uint h = BitConverter.ToUInt32(data, o);
            if (h == 0) { section = (int)BitConverter.ToUInt32(data, o + 4); continue; }
            pairs[h] = (o, section);
        }
    }

    int OffsetOf(Field f)
    {
        uint h = Murmur.Hash(f.Name);
        if (!pairs.TryGetValue(h, out var p))
            throw new InvalidDataException($"{f.Name} is not in this save.");
        if (p.section != (int)f.Type)
            throw new InvalidDataException($"{f.Name} is not where a {f.Type} field belongs in this save: not changed.");
        return p.offset + 4;
    }

    public bool Has(Field f)
    {
        return pairs.TryGetValue(Murmur.Hash(f.Name), out var p) && p.section == (int)f.Type;
    }

    public uint Get(Field f) => BitConverter.ToUInt32(data, OffsetOf(f));

    public void Set(Field f, uint value)
    {
        if (f.Type == FieldType.Bool && value > 1) throw new ArgumentException("A yes/no field takes 0 or 1.");
        if (f.Type == FieldType.Enum && !Fields.BottleContents.Any(v => Murmur.Hash(v) == value))
            throw new ArgumentException("Not a value this field can hold.");
        BitConverter.GetBytes(value).CopyTo(data, OffsetOf(f));
    }

    /// <summary>Copies the whole slot folder into &lt;profile&gt;\TOZOT-backups and returns the backup's folder.</summary>
    public string Backup()
    {
        // <profile>\save\<account>\slot_NN -> <profile>\TOZOT-backups\slot_NN-<time>; outside the save folder, which the
        // game copies whole into its working copy
        var profile = Directory.GetParent(SlotDir)!.Parent!.Parent!.FullName;
        var dest = System.IO.Path.Combine(profile, "TOZOT-backups",
            $"{System.IO.Path.GetFileName(SlotDir)}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(SlotDir))
            File.Copy(file, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(file)), overwrite: false);
        return dest;
    }

    /// <summary>Writes the save through a temporary file, so a failed write never leaves half a save.</summary>
    public void Write()
    {
        var tmp = Path + ".tozot-tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, Path, overwrite: true);
    }

    /// <summary>The game's committed save folder (&lt;profile&gt;\save\&lt;account&gt;) and its slots.</summary>
    public static (string? accountDir, string[] slots, string? problem) FindSlots(string gameDir)
    {
        var save = System.IO.Path.Combine(gameDir, "profile", "save");
        if (!Directory.Exists(save)) return (null, Array.Empty<string>(), $"No saves found in {save}.");
        var accounts = Directory.GetDirectories(save);
        if (accounts.Length != 1) return (null, Array.Empty<string>(), $"Expected one account folder in {save}, found {accounts.Length}.");
        var tx = System.IO.Path.Combine(gameDir, "profile", "save-tx");
        if (Directory.Exists(tx) && Directory.GetDirectories(tx, "ready-*").Length > 0)
            return (accounts[0], Array.Empty<string>(),
                "The game has a save it has not finished writing (save-tx\\ready). Start the game once and quit from its menu, then try again.");
        var slots = Directory.GetDirectories(accounts[0], "slot_*")
            .Where(d => File.Exists(System.IO.Path.Combine(d, "progress.sav"))).OrderBy(d => d).ToArray();
        return (accounts[0], slots, slots.Length == 0 ? "No save slots found." : null);
    }
}
