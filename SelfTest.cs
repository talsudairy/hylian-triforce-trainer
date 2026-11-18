using System.IO;
using System.IO.Compression;

namespace Tozot;

/// <summary>
/// Hidden test modes (results go to the file given, the app shows no window):
///   --selftest-save &lt;game dir&gt; &lt;log&gt;   on a scratch copy of a profile: each save action, then a read-back
///   --selftest-live &lt;log&gt;              attached to the running game: set, read back, undo, read back
/// </summary>
public static class SelfTest
{
    public static int Run(string[] args)
    {
        var mode = args[0];
        var logPath = args[^1];
        using var log = new StreamWriter(logPath) { AutoFlush = true };
        int fails = 0;
        void Check(string what, bool ok) { log.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) fails++; }
        try
        {
            if (mode == "--selftest-read")   // attached to the running game, reads only: nothing is written
            {
                using var g = GameMemory.Attach();
                Check($"attached to {g.Runner.Name}, process {g.ProcessId}, tables verified", true);
                foreach (var n in new[] { "PlayerStatus.Life", "PlayerStatus.MaxLife", "PlayerStatus.CurrentRupee", "PlayerStatus.MPMax",
                                          "PouchData.Item_DekuNut.StockNum", "IsGet.Weapon_Sword_Master" })
                {
                    var f = Fields.All.FirstOrDefault(x => x.Name == n);
                    if (f == null) { log.WriteLine($"     {n}: not in the field table"); continue; }
                    log.WriteLine($"     {n} = {g.Get(f)}");
                }
                var kept = Kept.Health();
                var t = System.Diagnostics.Stopwatch.StartNew();
                int found = kept.Find(g);
                Check($"hearts handle search: {found} found in {t.Elapsed.TotalSeconds:0.0} s, all valid: {kept.Valid(g)}", found > 0);
                return fails == 0 ? 0 : 1;
            }
            if (mode == "--selftest-bottles")   // on a scratch copy of a profile: remove bottles, then give one back
            {
                var (_, slots, problem) = SaveFile.FindSlots(args[1]);
                Check("slots found: " + (problem ?? slots.Length.ToString()), problem == null);
                var slot = slots.First(d => MainWindow.BottleLetters.Any(b => new SaveFile(d).Get(Fields.Get($"PlayerStatus.AssignableItem.BottleInfo.Bottle{b}.IsValid")) != 0));
                Field valid(string b) => Fields.Get($"PlayerStatus.AssignableItem.BottleInfo.Bottle{b}.IsValid");
                Field item(string b) => Fields.Get($"PlayerStatus.AssignableItem.BottleInfo.Bottle{b}.BottleItem");
                var has = Fields.Get("IsGet.Item_Bottle");
                void Do(IEnumerable<(Field, uint)> changes) { var s = new SaveFile(slot); foreach (var (f, v) in changes.ToList()) s.Set(f, v); s.Write(); }
                string Show() { var s = new SaveFile(slot); return string.Join(" ", MainWindow.BottleLetters.Select(b => $"{b}:{s.Get(valid(b))}")) + $" has-bottle {s.Get(has)}"; }
                long size = new FileInfo(Path.Combine(slot, "progress.sav")).Length;
                log.WriteLine($"     {Path.GetFileName(slot)} before: {Show()}");
                var owned = MainWindow.BottleLetters.Where(b => new SaveFile(slot).Get(valid(b)) != 0).ToList();
                // remove one bottle: it goes, the others stay, Link still has a bottle (if he had more than one)
                Do(MainWindow.BottleRemoval(Array.IndexOf(MainWindow.BottleLetters, owned[0]), new SaveFile(slot).Get));
                var s1 = new SaveFile(slot);
                Check($"bottle {owned[0]} removed and emptied: {Show()}", s1.Get(valid(owned[0])) == 0 && s1.Get(item(owned[0])) == Murmur.Hash("Empty"));
                Check("the other bottles are kept", owned.Skip(1).All(b => s1.Get(valid(b)) != 0));
                Check("still has a bottle while others are left", s1.Get(has) == (owned.Count > 1 ? 1u : 0u));
                bool refused = false;
                try { MainWindow.BottleRemoval(Array.IndexOf(MainWindow.BottleLetters, owned[0]), s1.Get).ToList(); } catch (InvalidOperationException) { refused = true; }
                Check("removing a bottle that isn't owned is refused", refused);
                // remove the rest: no bottle left, "has a bottle" cleared
                foreach (var b in owned.Skip(1)) Do(MainWindow.BottleRemoval(Array.IndexOf(MainWindow.BottleLetters, b), new SaveFile(slot).Get));
                var s2 = new SaveFile(slot);
                Check($"all bottles removed and has-bottle cleared: {Show()}", MainWindow.BottleLetters.All(b => s2.Get(valid(b)) == 0) && s2.Get(has) == 0);
                // give one back
                Do(MainWindow.BottleChanges(0, Array.IndexOf(Fields.BottleContents, "Fairy")));
                var s3 = new SaveFile(slot);
                Check($"Set bottle gives bottle A with a fairy and has-bottle: {Show()}", s3.Get(valid("A")) == 1 && s3.Get(item("A")) == Murmur.Hash("Fairy") && s3.Get(has) == 1);
                Check("file size unchanged", new FileInfo(Path.Combine(slot, "progress.sav")).Length == size);
                return fails == 0 ? 0 : 1;
            }
            if (mode == "--selftest-mpscan")   // reads only: every place the MP / MPMax / Life hashes sit, with the bytes around them
            {
                using var g = GameMemory.Attach();
                log.WriteLine($"attached to {g.Runner.Name}, process {g.ProcessId}");
                foreach (var n in new[] { "PlayerStatus.MP", "PlayerStatus.MPMax", "PlayerStatus.Life" })
                {
                    var f = Fields.Get(n); uint h = Murmur.Hash(n);
                    log.WriteLine($"{n}: record {g.Get(f)}, hash {h:x8}");
                    foreach (var (addr, bytes) in g.FindHashContexts(h))
                    {
                        var hex = string.Join(" ", Enumerable.Range(0, bytes.Length / 4).Select(i => BitConverter.ToUInt32(bytes, i * 4).ToString("x8")));
                        log.WriteLine($"   at {addr:x} (hash at word 4): {hex}");
                    }
                }
                return 0;
            }
            if (mode == "--selftest-hearts")   // infinite hearts: lower the hearts through the game's counter, then the refill
            {
                using var g = GameMemory.Attach();
                Check($"attached to {g.Runner.Name}, process {g.ProcessId}", true);
                var life = Fields.Get("PlayerStatus.Life");
                var kept = Kept.Health();
                Check($"hearts counter found: {kept.Find(g)}", kept.Found > 0);
                uint max = kept.Max(g), l0 = g.Get(life), low = max > 4 ? max - 4 : 1;
                log.WriteLine($"     before: life {l0} of {max}");
                Check("lowered one heart through the game's counter", kept.Set(g, low) == low);
                Thread.Sleep(1500);
                Check($"the game took the lower value (record {g.Get(life)}, expected {low})", g.Get(life) == low);
                Check("refill wrote the counter", kept.TopUp(g) == max);
                Thread.Sleep(1500);
                Check($"hearts full again and held after 1.5 s: {g.Get(life)} of {max}", g.Get(life) == max);
                return fails == 0 ? 0 : 1;
            }
            if (mode == "--selftest-setlife")   // test helper: --selftest-setlife <quarter hearts> <log>
            {
                using var g = GameMemory.Attach();
                var kept = Kept.Health(); kept.Find(g);
                uint v = uint.Parse(args[1]);
                Check($"life set to {v} through the game's counter", kept.Set(g, v) == v);
                return fails == 0 ? 0 : 1;
            }
            if (mode == "--selftest-peek")   // test helper: reads hearts and rupees only
            {
                using var g = GameMemory.Attach();
                log.WriteLine($"life {g.Get(Fields.Get("PlayerStatus.Life"))} max {g.Get(Fields.Get("PlayerStatus.MaxLife"))} rupees {g.Get(Fields.Get("PlayerStatus.CurrentRupee"))}");
                return 0;
            }
            if (mode == "--selftest-setrupees" || mode == "--selftest-setfield")   // test helpers: <n> <log>, or <field> <n> <log>
            {
                using var g = GameMemory.Attach();
                var rup = Fields.Get(mode == "--selftest-setfield" ? args[1] : "PlayerStatus.CurrentRupee");
                if (mode == "--selftest-setfield") args = args.Skip(1).ToArray();
                uint v = uint.Parse(args[1]);
                g.Keep(rup, v);
                Check($"{rup.Name} set to {v}: {g.Get(rup)}", g.Get(rup) == v);
                return fails == 0 ? 0 : 1;
            }
            if (mode == "--selftest-rupees")   // one live change, then Undo: rupees to 99 and back
            {
                using var g = GameMemory.Attach();
                Check($"attached to {g.Runner.Name}, process {g.ProcessId}, tables verified", true);
                var rup = Fields.Get("PlayerStatus.CurrentRupee");
                uint r0 = g.Get(rup);
                log.WriteLine($"     before: rupees {r0}");
                g.Apply("Set rupees (test)", new[] { (rup, 99u) });
                Check($"rupees read back 99: {g.Get(rup)}", g.Get(rup) == 99);
                Thread.Sleep(3000);
                Check($"rupees still 99 after 3 s (the game kept it): {g.Get(rup)}", g.Get(rup) == 99);
                log.WriteLine("     " + g.Undo());
                Check($"undo put rupees back to {r0}: {g.Get(rup)}", g.Get(rup) == r0);
                Thread.Sleep(1000);
                Check($"rupees still {r0} after 1 s: {g.Get(rup)}", g.Get(rup) == r0);
                return fails == 0 ? 0 : 1;
            }
            if (mode == "--selftest-save")
            {
                var (_, slots, problem) = SaveFile.FindSlots(args[1]);
                Check("slots found: " + (problem ?? slots.Length.ToString()), problem == null);
                var slot = slots[0];
                var s = new SaveFile(slot);
                var life = Fields.Get("PlayerStatus.Life"); var max = Fields.Get("PlayerStatus.MaxLife"); var rup = Fields.Get("PlayerStatus.CurrentRupee");
                log.WriteLine($"before: life {s.Get(life)} max {s.Get(max)} rupees {s.Get(rup)}");
                var backup = s.Backup();
                Check("backup has the slot's files", Directory.GetFiles(backup).Length == Directory.GetFiles(slot).Length);
                Check("backup is outside the save folder", !backup.Contains(Path.Combine("profile", "save") + Path.DirectorySeparatorChar));
                s.Set(max, 10 * 4); s.Set(life, s.Get(max));                                   // 10 heart containers, healed
                s.Set(rup, 250);
                foreach (var w in new[] { "Small", "Medium" }) s.Set(Fields.Get($"IsGet.Item_Wallet_{w}"), 1);
                s.Set(Fields.Get("IsGet.Weapon_Sword_Master"), 1);
                s.Set(Fields.Get("IsGet.Item_Arrow_Fire"), 1);
                s.Set(Fields.Get("PlayerStatus.AssignableItem.BottleInfo.BottleB.BottleItem"), Murmur.Hash("BluePotion"));
                s.Write();
                var r = new SaveFile(slot);
                Check($"max life 40 (10 hearts): {r.Get(max)}", r.Get(max) == 40);
                Check($"healed, life 40: {r.Get(life)}", r.Get(life) == 40);
                Check($"rupees 250: {r.Get(rup)}", r.Get(rup) == 250);
                Check("medium wallet", r.Get(Fields.Get("IsGet.Item_Wallet_Medium")) == 1);
                Check("Master Sword", r.Get(Fields.Get("IsGet.Weapon_Sword_Master")) == 1);
                Check("fire arrows", r.Get(Fields.Get("IsGet.Item_Arrow_Fire")) == 1);
                Check("bottle B blue potion", r.Get(Fields.Get("PlayerStatus.AssignableItem.BottleInfo.BottleB.BottleItem")) == Murmur.Hash("BluePotion"));
                Check("file size unchanged", new FileInfo(r.Path).Length == new FileInfo(Path.Combine(backup, "progress.sav")).Length);
                try { r.Set(Fields.Get("PlayerStatus.AssignableItem.BottleInfo.BottleB.BottleItem"), 12345); Check("a value outside the list is refused", false); }
                catch (ArgumentException) { Check("a value outside the list is refused", true); }
            }
            else if (mode == "--selftest-export")
            {
                var game = args[1];
                var account = SaveFile.FindSlots(game).accountDir!;
                var before = Directory.GetFiles(account, "*", SearchOption.AllDirectories).ToDictionary(f => f, f => File.GetLastWriteTimeUtc(f));
                var parent = Path.Combine(game, "export-test");
                Directory.CreateDirectory(parent);
                var (dest, zip, n) = MainWindow.ExportSaveData(game, parent);
                Check($"{n} slots exported", n > 0);
                var copied = Directory.GetFiles(Path.Combine(dest, "save"), "*", SearchOption.AllDirectories).Length;
                Check($"every save file copied ({copied} of {before.Count})", copied == before.Count);
                Check("README written", File.Exists(Path.Combine(dest, "README.txt")));
                using (var z = ZipFile.OpenRead(zip)) Check($"zip holds {z.Entries.Count} entries", z.Entries.Count >= before.Count + 1);
                Check("the game's save files are untouched", before.All(kv => File.GetLastWriteTimeUtc(kv.Key) == kv.Value));
                foreach (var f in Directory.GetFiles(Path.Combine(dest, "save"), "progress.sav", SearchOption.AllDirectories))
                    Check($"{Path.GetFileName(Path.GetDirectoryName(f))} copy is byte-identical", File.ReadAllBytes(f).SequenceEqual(File.ReadAllBytes(Path.Combine(account, Path.GetFileName(Path.GetDirectoryName(f))!, "progress.sav"))));
            }
            else if (mode == "--selftest-ammo")
            {
                using var g = GameMemory.Attach();
                Check("attached and verified", true);
                foreach (var k in new[] { Ammo.Nuts, Ammo.Seeds })
                {
                    var f = Fields.Get(k.Count);
                    uint v0 = g.Get(f), full = Ammo.Full(g, k);
                    log.WriteLine($"{k.Name}: count {v0}, bag size {full}");
                    g.Keep(f, 5);
                    Thread.Sleep(2000);
                    Check($"{k.Name}: a written count stays (5 after 2 s): {g.Get(f)}", g.Get(f) == 5);
                    if (full > 0)
                    {
                        Check($"{k.Name}: switched on, topped up to {full}", Ammo.TopUp(g, k) == full && g.Get(f) == full);
                        Check($"{k.Name}: nothing written when already full", Ammo.TopUp(g, k) == null);
                    }
                    else Check($"{k.Name}: no bag, nothing written", Ammo.TopUp(g, k) == null && g.Get(f) == 5);
                    g.Keep(f, v0);
                    Check($"{k.Name}: count put back to {v0}: {g.Get(f)}", g.Get(f) == v0);
                }
            }
            else if (mode == "--selftest-health")
            {
                using var g = GameMemory.Attach();
                Check("attached and verified", true);
                var life = Fields.Get("PlayerStatus.Life"); var max = Fields.Get("PlayerStatus.MaxLife");
                var h = Kept.Health();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int n = h.Find(g);
                log.WriteLine($"life {g.Get(life)} of {g.Get(max)}; {n} hearts handle(s) found in {sw.Elapsed.TotalSeconds:0.0} s");
                Check("Link's hearts handle found", n >= 1);
                uint full = g.Get(max);
                h.Set(g, 4);   // a hit, the way the game would: the handle drops to one heart and the record follows
                Thread.Sleep(1000);
                Check($"hurt to 1 heart and it stays (life 4 after 1 s): {g.Get(life)}", g.Get(life) == 4);
                Check($"switched on: filled to {full}", h.TopUp(g) == full);
                Thread.Sleep(1000);
                Check($"the game's own record follows, full after 1 s: {g.Get(life)}", g.Get(life) == full);
                Check("nothing written when already full", h.TopUp(g) == null);

                // heart containers, live: one more, then back
                var c = Kept.HeartContainers();
                Check($"heart containers handle found: {c.Find(g)}", c.Found >= 1);
                c.Set(g, full + 4); g.Keep(max, full + 4); h.Set(g, full + 4);
                Thread.Sleep(1500);
                Check($"one more heart container sticks: max {g.Get(max)}, life {g.Get(life)}", g.Get(max) == full + 4 && g.Get(life) == full + 4);
                c.Set(g, full); g.Keep(max, full); h.Set(g, full);
                Thread.Sleep(1500);
                Check($"put back: max {g.Get(max)}, life {g.Get(life)}", g.Get(max) == full && g.Get(life) == full);

                // magic: this works only on a save that has the magic meter
                var mp = Kept.Magic();
                log.WriteLine($"magic: MP {g.Get(Fields.Get("PlayerStatus.MP"))} of {g.Get(Fields.Get("PlayerStatus.MPMax"))}, {mp.Find(g)} handle(s)");
            }
            else if (mode == "--selftest-items")
            {
                using var g = GameMemory.Attach();
                Check("attached and verified", true);
                foreach (var k in Ammo.All)
                {
                    var f = Fields.Get(k.Count);
                    uint v0 = g.Get(f), full = Ammo.Full(g, k);
                    log.WriteLine($"{k.Name}: count {v0}, most {full}");
                    g.Keep(f, 1);
                    Thread.Sleep(1500);
                    Check($"{k.Name}: a written count stays (1 after 1.5 s): {g.Get(f)}", g.Get(f) == 1);
                    if (full > 0)
                    {
                        Check($"{k.Name}: topped up to {full}", Ammo.TopUp(g, k) == full && g.Get(f) == full);
                        Check($"{k.Name}: nothing written when already full", Ammo.TopUp(g, k) == null);
                    }
                    else Check($"{k.Name}: can't carry any, nothing written", Ammo.TopUp(g, k) == null && g.Get(f) == 1);
                    g.Keep(f, v0);
                    Check($"{k.Name}: count put back to {v0}: {g.Get(f)}", g.Get(f) == v0);
                }
                var rup = Fields.Get("PlayerStatus.CurrentRupee");
                uint r0 = g.Get(rup);
                foreach (var w in new[] { "Small", "Medium", "Large" }) log.WriteLine($"wallet {w}: {g.Get(Fields.Get("IsGet.Item_Wallet_" + w))}");
                g.Keep(rup, 99);
                Thread.Sleep(1500);
                Check($"rupees: a written amount stays (99 after 1.5 s): {g.Get(rup)}", g.Get(rup) == 99);
                g.Keep(rup, r0);
                Check($"rupees put back to {r0}: {g.Get(rup)}", g.Get(rup) == r0);
            }
            else if (mode == "--selftest-live")
            {
                using var g = GameMemory.Attach();
                Check("attached and verified", true);
                var rup = Fields.Get("PlayerStatus.CurrentRupee"); var sword = Fields.Get("IsGet.Weapon_Sword_Master");
                var bottle = Fields.Get("PlayerStatus.AssignableItem.BottleInfo.BottleC.BottleItem");
                uint r0 = g.Get(rup), s0 = g.Get(sword), b0 = g.Get(bottle);
                log.WriteLine($"before: rupees {r0} master sword {s0} bottle C {b0:x8}");
                g.Apply("test", new[] { (rup, 321u), (sword, 1u), (bottle, Murmur.Hash("Fairy")) });
                Check($"rupees read back 321: {g.Get(rup)}", g.Get(rup) == 321);
                Check("master sword read back", g.Get(sword) == 1);
                Check("bottle C fairy read back", g.Get(bottle) == Murmur.Hash("Fairy"));
                Thread.Sleep(3000);
                Check($"rupees still 321 after 3 s: {g.Get(rup)}", g.Get(rup) == 321);
                log.WriteLine(g.Undo());
                Check($"undo put rupees back to {r0}: {g.Get(rup)}", g.Get(rup) == r0);
                Check("undo put the sword back", g.Get(sword) == s0);
                Check("undo put the bottle back", g.Get(bottle) == b0);
                try { g.Apply("bad", new[] { (sword, 7u) }); Check("a bad flag value is refused", false); }
                catch (ArgumentException) { Check("a bad flag value is refused", true); }
            }
        }
        catch (Exception ex) { log.WriteLine("FAIL exception: " + ex); fails++; }
        log.WriteLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        return fails;
    }
}
