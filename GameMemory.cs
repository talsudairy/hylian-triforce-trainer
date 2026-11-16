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
/// <summary>
/// Where a game runner keeps the game. Both runners run the same game build (GameDataList.Product.110), so the tables,
/// field indices and record layouts are the same; only the addresses differ.
///   Ocarina port (ocarina.exe): guest addresses are host addresses; the program sits at 0x70_xxxx_xxxx.
///   Hylian Triforce (Triforce.exe, the user's own runner): guest memory is mapped at host 0x1000_0000_0000 + guest
///   address, and the 96 MB program image is loaded at guest 0x8000_0000 (read from its log and memory, 2026-11-16).
/// Every pointer the game stores is a guest address; Host() turns it into one this process can read.
/// </summary>
public sealed record Runner(string Name, ulong Arena, ulong ManagerGlobal, ulong ProgramStart, ulong ProgramEnd, ulong ScanStart, ulong ScanEnd)
{
    public static readonly Runner OcarinaPort = new("the Ocarina port", 0, 0x7004c7e180, 0x70_0000_0000, 0x71_0000_0000, 0, 1UL << 47);
    public static readonly Runner HylianTriforce = new("Hylian Triforce", 0x1000_0000_0000, 0x84c9e180, 0x8000_0000, 0x8600_0000,
        0x1000_0000_0000, 0x1000_0000_0000 + (1UL << 39));

    public ulong Host(ulong guest) => Arena + guest;
    /// <summary>True for a pointer into the game's program (a record's descriptor, a handle's class).</summary>
    public bool InProgram(ulong guest) => guest >= ProgramStart && guest < ProgramEnd;
}

public sealed class GameMemory : IDisposable
{
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
    public Runner Runner { get; }
    ulong manager;   // host address

    /// <summary>The writes of the last action, for Undo: where, and the bytes that were there.</summary>
    public List<(Field field, byte[] before)> LastAction { get; } = new();
    public string? LastActionName { get; private set; }

    GameMemory(int pid, IntPtr h, Runner r) { ProcessId = pid; handle = h; Runner = r; }

    /// <summary>
    /// The running game: ocarina.exe, or a renamed copy of it such as ocarina-dlss.exe ("Ocarina of Time.exe" is only
    /// the small launcher that starts it, so it is skipped), or Triforce.exe, the game that Hylian Triforce
    /// (HylianTriforce.exe, its launcher) starts. Attach still verifies the layout before any write.
    /// </summary>
    public static (Process process, Runner runner)? FindGame()
    {
        var all = Process.GetProcesses();
        var port = all.Where(p => p.ProcessName.StartsWith("ocarina", StringComparison.OrdinalIgnoreCase)
                               && !p.ProcessName.Equals("Ocarina of Time", StringComparison.OrdinalIgnoreCase))
                      .OrderBy(p => p.ProcessName.Equals("ocarina", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                      .FirstOrDefault();
        if (port != null) return (port, Runner.OcarinaPort);
        var tri = all.FirstOrDefault(p => p.ProcessName.Equals("Triforce", StringComparison.OrdinalIgnoreCase));
        return tri != null ? (tri, Runner.HylianTriforce) : null;
    }

    /// <summary>Attaches and verifies the manager; throws with a plain reason when anything does not match.</summary>
    public static GameMemory Attach()
    {
        var (p, runner) = FindGame() ?? throw new InvalidOperationException("The game is not running. Start Ocarina of Time or Hylian Triforce first.");
        var h = OpenProcess(Access, false, p.Id);
        if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the game's process");
        var g = new GameMemory(p.Id, h, runner);
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
        try { ptr = U64(Runner.Host(Runner.ManagerGlobal)); manager = Runner.Host(U64(Runner.Host(ptr))); }
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
        return (U32(o), Runner.Host(U64(o + 8)), Runner.Host(U64(o + 0x10)), U32(o + 0x1c));   // host addresses
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
                if (!Runner.InProgram(desc))
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
        LastActionName = actionName;   // before the writes, so a part-done action is undone under its own name
        foreach (var (f, addr, before, after) in plan)
        {
            if (!WriteProcessMemory(handle, addr, after, after.Length, out var n) || n != after.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not write {f.Name}");
            LastAction.Add((f, before));
        }
    }

    /// <summary>Drops the Undo entry, for a change Undo can't put back (one the game holds in its own handles).</summary>
    public void ForgetUndo() { LastAction.Clear(); LastActionName = null; }

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
        ulong addr = Runner.ScanStart;   // Hylian Triforce: only its guest memory, not the runner's own caches
        while (addr < Runner.ScanEnd && VirtualQueryEx(handle, addr, out var mi, Marshal.SizeOf<MemInfo>()) != 0)
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
                        if (Runner.InProgram(desc) && !found.Contains(obj)) found.Add(obj);
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
        if (!Runner.InProgram(BitConverter.ToUInt64(b, 0)) || BitConverter.ToUInt32(b, 12) != hash) return null;
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
