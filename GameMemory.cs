using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tozot;

/// <summary>
/// Live access to the running game (the Ocarina Recomp port, ocarina.exe), whose guest addresses are this process's
/// addresses. The path is the game's own, read from its code: the global at 0x7004c7e180 points to a pointer to the
/// GameData manager; type block k sits at manager + 0x10 + 0x60*k with {u32 count, u64 records, u64 hash map, ..,
/// u32 capacity at +0x1c}; the map holds (hash, index) pairs probed from hash % capacity; record index is the field's
/// position in its type's table. Before anything is written: the block's count must be the schema's, the map must
/// return the field's own index, and the record must carry a descriptor pointer into the game's program.
/// A record holds its default value and two current copies; only the current copies are written:
///   Bool  24-byte records, copies at +0x16 and +0x17 (one byte each)
///   Int   40-byte records, copies at +0x1c and +0x20
///   Enum  56-byte records, copies at +0x1c and +0x20 (the value's name hash)
/// </summary>
public sealed class GameMemory : IDisposable
{
    const ulong ManagerGlobal = 0x7004c7e180;
    const uint Access = 0x0010 | 0x0020 | 0x0008 | 0x0400;   // VM_READ | VM_WRITE | VM_OPERATION | QUERY_INFORMATION

    [DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr h, ulong addr, byte[] buf, nint size, out nint read);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool WriteProcessMemory(IntPtr h, ulong addr, byte[] buf, nint size, out nint written);
    [DllImport("kernel32")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);

    readonly IntPtr handle;
    public int ProcessId { get; }
    ulong manager;

    /// <summary>The writes of the last action, for Undo: where, and the bytes that were there.</summary>
    public List<(Field field, byte[] before)> LastAction { get; } = new();
    public string? LastActionName { get; private set; }

    GameMemory(int pid, IntPtr h) { ProcessId = pid; handle = h; }

    public static Process? FindGame() =>
        Process.GetProcessesByName("ocarina").FirstOrDefault();

    /// <summary>Attaches and verifies the manager; throws with a plain reason when anything does not match.</summary>
    public static GameMemory Attach()
    {
        var p = FindGame() ?? throw new InvalidOperationException("The game is not running. Start Ocarina of Time first.");
        var h = OpenProcess(Access, false, p.Id);
        if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the game's process");
        var g = new GameMemory(p.Id, h);
        try { g.Verify(); }
        catch { g.Dispose(); throw; }
        return g;
    }

    public bool Alive => GetExitCodeProcess(handle, out var c) && c == 259;

    byte[] Read(ulong addr, int n)
    {
        var b = new byte[n];
        if (!ReadProcessMemory(handle, addr, b, n, out var got) || got != n)
            throw new InvalidOperationException($"Could not read the game's memory at {addr:x}.");
        return b;
    }
    ulong U64(ulong a) => BitConverter.ToUInt64(Read(a, 8));
    uint U32(ulong a) => BitConverter.ToUInt32(Read(a, 4));

    void Verify()
    {
        ulong ptr;
        try { ptr = U64(ManagerGlobal); manager = U64(ptr); }
        catch { throw new InvalidOperationException("The game is still starting, or this is not the supported game version. Load your save, then try again."); }
        foreach (var (type, count) in new[] { (FieldType.Bool, Fields.BoolCount), (FieldType.Int, Fields.IntCount), (FieldType.Enum, Fields.EnumCount) })
        {
            var (n, _, _, cap) = Block(type);
            if (n != count || cap != 2 * count)
                throw new InvalidOperationException(
                    $"The game's {type} table has {n} entries where {count} are expected. Load your save first; if it still fails, this game version is not supported.");
        }
    }

    (uint count, ulong records, ulong map, uint cap) Block(FieldType t)
    {
        ulong o = manager + 0x10 + 0x60 * (ulong)(int)t;
        return (U32(o), U64(o + 8), U64(o + 0x10), U32(o + 0x1c));
    }

    static (int size, int first, int width) Layout(FieldType t) => t switch
    {
        FieldType.Bool => (24, 0x16, 1),
        FieldType.Int => (40, 0x1c, 4),
        _ => (56, 0x1c, 4),
    };

    /// <summary>The record of a field, found the way the game finds it and checked against the schema.</summary>
    ulong Record(Field f)
    {
        var (count, records, map, cap) = Block(f.Type);
        uint hash = Murmur.Hash(f.Name);
        uint slot = hash % cap;
        for (uint i = 0; i < cap; i++, slot = (slot + 1) % cap)
        {
            var e = Read(map + slot * 8UL, 8);
            uint eh = BitConverter.ToUInt32(e, 0), idx = BitConverter.ToUInt32(e, 4);
            if (eh == hash)
            {
                if (idx != f.Index || idx >= count)
                    throw new InvalidOperationException($"{f.Name}: the game's table does not match the expected layout. Nothing was changed.");
                ulong rec = records + idx * (ulong)Layout(f.Type).size;
                ulong desc = U64(rec);
                if (desc >> 32 != 0x70)
                    throw new InvalidOperationException($"{f.Name}: the record does not look like a GameData record. Nothing was changed.");
                return rec;
            }
            if (eh == 0) break;
        }
        throw new InvalidOperationException($"{f.Name} was not found in the running game. Nothing was changed.");
    }

    public uint Get(Field f)
    {
        var (_, first, width) = Layout(f.Type);
        var b = Read(Record(f) + (ulong)first, width);
        return width == 1 ? b[0] : BitConverter.ToUInt32(b);
    }

    /// <summary>One user action: every write is verified first and the old bytes kept, so Undo can put them back.</summary>
    public void Apply(string actionName, IEnumerable<(Field field, uint value)> changes)
    {
        var plan = new List<(Field f, ulong addr, byte[] before, byte[] after)>();
        foreach (var (f, v) in changes)
        {
            if (f.Type == FieldType.Bool && v > 1) throw new ArgumentException($"{f.Name}: a yes/no field takes 0 or 1.");
            if (f.Type == FieldType.Enum && !Fields.BottleContents.Any(x => Murmur.Hash(x) == v))
                throw new ArgumentException($"{f.Name}: not a value this field can hold.");
            var (_, first, width) = Layout(f.Type);
            ulong addr = Record(f) + (ulong)first;   // verified lookup, before any write
            var before = Read(addr, 2 * width);
            var after = new byte[2 * width];
            for (int k = 0; k < 2; k++)
                if (width == 1) after[k] = (byte)v;
                else BitConverter.GetBytes(v).CopyTo(after, 4 * k);
            plan.Add((f, addr, before, after));
        }
        LastAction.Clear();
        foreach (var (f, addr, before, after) in plan)
        {
            if (!WriteProcessMemory(handle, addr, after, after.Length, out var n) || n != after.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not write {f.Name}");
            LastAction.Add((f, before));
        }
        LastActionName = actionName;
    }

    /// <summary>A repeated write that is not an action (no Undo entry): the infinite-ammo refill. Verified lookup first.</summary>
    public void Keep(Field f, uint v)
    {
        if (f.Type != FieldType.Int) throw new ArgumentException($"{f.Name}: only number fields are kept topped up.");
        var (_, first, _) = Layout(f.Type);
        ulong addr = Record(f) + (ulong)first;
        var after = new byte[8];
        BitConverter.GetBytes(v).CopyTo(after, 0);
        BitConverter.GetBytes(v).CopyTo(after, 4);
        if (!WriteProcessMemory(handle, addr, after, after.Length, out var n) || n != after.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not write {f.Name}");
    }

    /// <summary>Puts back what the last action replaced, after finding each field again the verified way.</summary>
    public string Undo()
    {
        if (LastAction.Count == 0) return "Nothing to undo.";
        foreach (var (f, before) in LastAction)
        {
            var (_, first, _) = Layout(f.Type);
            ulong addr = Record(f) + (ulong)first;
            if (!WriteProcessMemory(handle, addr, before, before.Length, out var n) || n != before.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not restore {f.Name}");
        }
        var name = LastActionName;
        LastAction.Clear();
        LastActionName = null;
        return $"Undid \"{name}\".";
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemInfo { public ulong Base, AllocBase; public uint AllocProtect, Pad1; public ulong Size; public uint State, Protect, Type, Pad2; }
    [DllImport("kernel32", SetLastError = true)] static extern nint VirtualQueryEx(IntPtr h, ulong addr, out MemInfo info, nint size);

    /// <summary>
    /// The game's own handles for an Int field: small objects {u64 class pointer into the program, u32 value, u32 name
    /// hash} that a game system keeps and sends to GameData every frame, so the record follows the handle (a write to
    /// the record alone is undone the next frame). PlayerStatus.Life lives in one, read from f_7000163b20, which
    /// queues the handle's value through f_7000163b70. Searches the game's writable memory; takes a few seconds.
    /// </summary>
    public List<ulong> FindHandles(uint hash)
    {
        var found = new List<ulong>();
        var key = BitConverter.GetBytes(hash);
        const int Chunk = 16 << 20;
        var buf = new byte[Chunk];
        ulong addr = 0;
        while (addr < 1UL << 47 && VirtualQueryEx(handle, addr, out var mi, Marshal.SizeOf<MemInfo>()) != 0)
        {
            if (mi.State == 0x1000 && mi.Protect == 0x04)   // MEM_COMMIT, PAGE_READWRITE
                for (ulong off = 0; off < mi.Size; off += Chunk - 16)
                {
                    int n = (int)Math.Min((ulong)Chunk, mi.Size - off);
                    if (!ReadProcessMemory(handle, mi.Base + off, buf, n, out var got) || got < 16) continue;
                    var span = buf.AsSpan(0, (int)got);
                    for (int i = 12, j; (j = span[i..].IndexOf(key)) >= 0; i += j + 4)
                    {
                        int at = i + j;
                        if ((at & 3) != 0) continue;
                        ulong desc = BitConverter.ToUInt64(buf, at - 12);
                        ulong obj = mi.Base + off + (ulong)at - 12;
                        if (desc >> 32 == 0x70 && !found.Contains(obj)) found.Add(obj);
                    }
                }
            addr = mi.Base + mi.Size;
        }
        return found;
    }

    /// <summary>The value in a handle found by FindHandles, or null when the memory no longer holds that handle.</summary>
    public uint? HandleValue(ulong obj, uint hash)
    {
        var b = new byte[16];
        if (!ReadProcessMemory(handle, obj, b, 16, out var got) || got != 16) return null;
        if (BitConverter.ToUInt64(b, 0) >> 32 != 0x70 || BitConverter.ToUInt32(b, 12) != hash) return null;
        return BitConverter.ToUInt32(b, 8);
    }

    /// <summary>Writes a handle's value (checked again first); no Undo entry, like Keep.</summary>
    public bool KeepHandle(ulong obj, uint hash, uint v)
    {
        if (HandleValue(obj, hash) == null) return false;
        var b = BitConverter.GetBytes(v);
        return WriteProcessMemory(handle, obj + 8, b, 4, out var n) && n == 4;
    }

    public void Dispose() => CloseHandle(handle);
}
