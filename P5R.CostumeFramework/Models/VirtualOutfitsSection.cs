﻿using System.Runtime.InteropServices;

namespace P5R.CostumeFramework.Models;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct VirtualOutfitsSection
{
    /// <summary>
    /// Amount of "sets" of outfits for mods. Essentially
    /// equivalent to max amount of new costumes per character.
    /// </summary>
    public const int MOD_OUTFIT_SETS = 32;
    public const int NUM_OUTFITS = 286 + (MOD_OUTFIT_SETS * 10);

    public VirtualOutfitsSection()
    {
        var outfits = GetOutfits();
        var size = sizeof(OutfitEntry) * outfits.Length;
        this.size = ((uint)size).ToBigEndian();

        fixed (byte* ptr = outfitsData)
        {
            for (int i = 0; i < outfits.Length; i++)
            {
                Marshal.StructureToPtr(
                    outfits[i],
                    (nint)(ptr + (sizeof(OutfitEntry) * i)),
                    false);
            }
        }
    }

    public uint size;
    public fixed byte outfitsData[NUM_OUTFITS * 32];

    private static OutfitEntry[] GetOutfits()
    {
        var entries = new OutfitEntry[NUM_OUTFITS];
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i].icon = 4;
            entries[i].unknown8 = 0x6400;
            entries[i].unknown10 = 0x1400;

            if (i == 13)
            {
                entries[i].equippableFlags |= EquippableUsers.Akechi;
                entries[i].unknown11 = 0x0104;
            }
            else if (i < 16)
            {
                entries[i].unknown2 = 0xFFFF;
                entries[i].equippableFlags = (EquippableUsers)0xFFFF;
                entries[i].unknown11 = 0x0104;
            }
            else
            {
                entries[i].equippableFlags |= ItemTbl.OrderedEquippable[(i - 16) % 10];
                entries[i].unknown11 = 0x1F03;
            }
        }

        return entries;
    }
}
