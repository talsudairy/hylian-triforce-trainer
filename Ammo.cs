namespace Tozot;

/// <summary>
/// Infinite items: while a switch is on, the count is put back to the most the player can carry whenever it drops.
/// Switched off, nothing is written and the count works as normal again. The most follows the biggest bag owned
/// (Bags[i] holds Sizes[i]); NoBag is the most with none of them, and Needs, when set, is an item without which
/// nothing is filled (no Deku Sticks before the first stick, for example).
/// </summary>
public static class Ammo
{
    public sealed record Kind(string Name, string Count, string[] Bags, uint[] Sizes, uint NoBag, string? Needs = null);

    public static readonly Kind Nuts = new("Deku Nuts", "PouchData.Item_DekuNut.StockNum",
        new[] { "Item_NutBag_Small", "Item_NutBag_Medium", "Item_NutBag_Large" }, new uint[] { 20, 30, 40 }, 20);

    public static readonly Kind Seeds = new("Deku Seeds", "PouchData.Item_DekuSeed.StockNum",
        new[] { "Item_BulletBag_Small", "Item_BulletBag_Medium", "Item_BulletBag_Large" }, new uint[] { 30, 40, 50 }, 0);

    public static readonly Kind Sticks = new("Deku Sticks", "PouchData.Item_DekuStick.StockNum",
        new[] { "Item_StickBag_Small", "Item_StickBag_Medium", "Item_StickBag_Large" }, new uint[] { 10, 20, 30 }, 10, Needs: "Item_DekuStick");

    public static readonly Kind Arrows = new("Arrows", "PouchData.Item_Arrow_Normal.StockNum",
        new[] { "Item_Quiver_Small", "Item_Quiver_Medium", "Item_Quiver_Large" }, new uint[] { 30, 40, 50 }, 0);

    public static readonly Kind Bombs = new("Bombs", "PouchData.Item_Bomb.StockNum",
        new[] { "Item_BombBag_Small", "Item_BombBag_Medium", "Item_BombBag_Large" }, new uint[] { 20, 30, 40 }, 0);

    public static readonly Kind Bombchus = new("Bombchus", "PouchData.Item_Bombchu.StockNum",
        new[] { "Item_Bombchu" }, new uint[] { 50 }, 0);

    public static readonly Kind Beans = new("Magic Beans", "PouchData.Item_MagicBean.StockNum",
        new[] { "Item_MagicBean" }, new uint[] { 10 }, 0);

    public static readonly Kind[] All = { Nuts, Sticks, Seeds, Arrows, Bombs, Bombchus, Beans };

    /// <summary>How many the player can carry now; 0 when they can't carry any yet.</summary>
    public static uint Full(GameMemory g, Kind k)
    {
        if (k.Needs != null && g.Get(Fields.Get("IsGet." + k.Needs)) == 0) return 0;
        for (int i = k.Bags.Length - 1; i >= 0; i--)
            if (g.Get(Fields.Get("IsGet." + k.Bags[i])) != 0) return k.Sizes[i];
        return k.NoBag;
    }

    /// <summary>Tops the count up if it dropped. Returns the count written, or null when nothing was needed.</summary>
    public static uint? TopUp(GameMemory g, Kind k)
    {
        uint full = Full(g, k);
        if (full == 0) return null;
        var f = Fields.Get(k.Count);
        if (g.Get(f) >= full) return null;
        g.Keep(f, full);
        return full;
    }
}
