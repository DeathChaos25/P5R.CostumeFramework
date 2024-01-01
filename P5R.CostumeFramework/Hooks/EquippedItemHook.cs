﻿using P5R.CostumeFramework.Costumes;
using P5R.CostumeFramework.Models;
using p5rpc.lib.interfaces;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.SigScan.ReloadedII.Interfaces;
using static Reloaded.Hooks.Definitions.X64.FunctionAttribute;

namespace P5R.CostumeFramework.Hooks;

internal class EquippedItemHook
{
    [Function(Register.r8, Register.rax, true)]
    private delegate int GapGetOutfitItemId(ushort currentOutfitItemId);
    private IReverseWrapper<GapGetOutfitItemId>? getOutfitItemIdWrapper;
    private IAsmHook? getOutfitItemIdHook;

    [Function(Register.rax, Register.rax, true)]
    private delegate int SetEquippedItem(int itemId);
    private IReverseWrapper<SetEquippedItem>? setEquippedWrapper;
    private IAsmHook? setEquippedHook;

    [Function(CallingConventions.Microsoft)]
    private delegate nint LoadSave(nint param1, nint param2, nint param3);
    private IHook<LoadSave>? loadSaveHook;

    [Function(CallingConventions.Microsoft)]
    private delegate int UpdateCharEquip(Character character, int equipType, nint param3, nint param4);
    private IHook<UpdateCharEquip> updateCharHook;

    [Function(CallingConventions.Microsoft)]
    private delegate void UpdateCharEquipLive(nint partyPtr, Character character, nint param_3, nint param_4, nint param_5);
    private IHook<UpdateCharEquipLive> updateCharHookLive;

    private readonly CostumeRegistry costumes;
    private readonly CostumeMusicService costumeMusic;
    private readonly Dictionary<Character, FakeOutfitItemId> previousOutfitIds = new();
    private readonly IP5RLib p5rLib;

    public EquippedItemHook(
        IStartupScanner scanner,
        IReloadedHooks hooks,
        IP5RLib p5rLib,
        CostumeRegistry costumes,
        CostumeMusicService costumeMusic)
    {
        this.p5rLib = p5rLib;
        this.costumes = costumes;
        this.costumeMusic = costumeMusic;
        scanner.Scan("(GAP Fix) Get Outfit Item ID Hook", "B8 67 66 66 66 41 8D 90", result =>
        {
            var patch = new string[]
            {
                "use64",
                Utilities.PushCallerRegisters,
                hooks.Utilities.GetAbsoluteCallMnemonics(this.GetOutfitItemIdImpl, out this.getOutfitItemIdWrapper),
                Utilities.PopCallerRegisters,
                "test rax, rax",
                "jz original",
                "mov r8, rax",
                "original:"
            };

            this.getOutfitItemIdHook = hooks.CreateAsmHook(patch, result, AsmHookBehaviour.ExecuteFirst).Activate();
        });

        scanner.Scan("Set Equipped Item ID", "43 0F B7 84 ?? ?? ?? ?? ?? 0F B7 CD", result =>
        {
            var patch = new string[]
            {
                "use64",
                Utilities.PushCallerRegisters,
                "mov rax, rbp",
                hooks.Utilities.GetAbsoluteCallMnemonics(this.SetEquippedItemImpl, out this.setEquippedWrapper),
                Utilities.PopCallerRegisters,
                "test rax, rax",
                "jz original",
                "mov rbp, rax",
                "original:",
            };

            this.setEquippedHook = hooks.CreateAsmHook(patch, result, AsmHookBehaviour.ExecuteFirst).Activate();
        });

        scanner.Scan("Load Save Hook", "48 89 6C 24 ?? 56 48 83 EC 20 49 8B F0 8B E9", result =>
        {
            this.loadSaveHook = hooks.CreateHook<LoadSave>(this.LoadSaveImpl, result).Activate();
        });

        //this.updateCharHook = hooks.CreateHook<UpdateCharEquip>(this.UpdateCharEquipImpl, 0x1416bdf90).Activate();
        //this.updateCharHookLive = hooks.CreateHook<UpdateCharEquipLive>(this.UpdateCharEquipLiveImpl, 0x1412adf90).Activate();
    }

    private Dictionary<Character, UpdateEquipParams> equipParams = new();

    public void ForceUpdateCharEquip(Character character)
    {
        if (this.equipParams.TryGetValue(character, out var charParams))
        {
            Log.Debug("Force update.");
            var currentEquip = this.p5rLib.GET_EQUIP(character, EquipSlot.Melee);
            this.p5rLib.FlowCaller.SET_EQUIP((int)character, (int)EquipSlot.Melee, 249);
            //this.UpdateCharEquipImpl(character, )
            //this.UpdateCharEquipLiveImpl(charParams.Param1, character, charParams.Param3, charParams.Param4, charParams.Param5);
            this.p5rLib.FlowCaller.SET_EQUIP((int)character, (int)EquipSlot.Melee, currentEquip);
        }
    }

    private void UpdateCharEquipLiveImpl(nint partyPtr, Character character, nint param_3, nint param_4, nint param_5)
    {
        if (Enum.IsDefined(character) && !this.equipParams.ContainsKey(character))
        {
            this.equipParams[character] = new(partyPtr, param_3, param_4, param_5);
        }

        Log.Debug($"UpdateCharEquip: {partyPtr:X} || {character} || {param_3} || {param_4} || {param_5:X}");
        this.updateCharHookLive.OriginalFunction(partyPtr, character, param_3, param_4, param_5);
    }

    public int UpdateCharEquipImpl(Character character, int equipType, nint param3, nint param4)
    {
        Log.Information($"Checking equip: {character} || {equipType} || {param3} || {param4}");
        return this.updateCharHook.OriginalFunction(character, equipType, param3, param4);
    }

    /// <summary>
    /// Fix issues caused by mod outfits using item IDs too large which
    /// break some math somewhere, likely in determining/formatting the GAP file path.
    /// </summary>
    private int GetOutfitItemIdImpl(ushort currentOutfitItemId)
    {
        if (this.costumes.TryGetModCostume(currentOutfitItemId, out var costume))
        {
            if (this.previousOutfitIds.TryGetValue(costume.Character, out var fakeOutfitId))
            {
                if (fakeOutfitId.OriginalId == costume.ItemId)
                {
                    Log.Debug($"GAP Get Outfit Item ID overwritten (previous): {costume.Character} || Original: {fakeOutfitId.OriginalId} || New: {fakeOutfitId.NewId}");
                    return fakeOutfitId.NewId;
                }
            }

            var setId = VirtualOutfitsSection.GetOutfitSetId(costume.ItemId);
            var newSetId = GetNewSetId(setId);
            var prevSetId = fakeOutfitId != null ? VirtualOutfitsSection.GetOutfitSetId(fakeOutfitId.NewId) : -1;

            // Increment new set ID if same as previous causing same
            // item ID to be calculated, causing the outfit to not update.
            if (newSetId == prevSetId)
            {
                newSetId = GetNewSetId(newSetId + 1);
            }

            var equipId = (int)costume.Character - 1;
            var newOutfitItemId = 0x7010 + newSetId * 10 + equipId;
            this.previousOutfitIds[costume.Character] = new(costume.ItemId, newOutfitItemId);

            Log.Debug($"GAP Get Outfit Item ID overwritten: {costume.Character} || Equip ID: {equipId} || Original: {currentOutfitItemId} || New: {newOutfitItemId}");
            Log.Debug($"Original Set ID: {setId} || New Set ID: {newSetId}");
            return newOutfitItemId;
        }

        return 0;
    }

    /// <summary>
    /// For applying initial costume BGM state since they're not equipped through
    /// equip function.
    /// </summary>
    private nint LoadSaveImpl(nint param1, nint param2, nint param3)
    {
        var result = this.loadSaveHook!.OriginalFunction(param1, param2, param3);
        this.costumeMusic.Refresh();
        return result;
    }

    /// <summary>
    /// Apply costume BGM on equip.
    /// </summary>
    private int SetEquippedItemImpl(int itemId)
    {
        if (VirtualOutfitsSection.IsOutfit(itemId))
        {
            this.costumeMusic.Refresh(itemId);
        }

        return 0;
    }

    private static int GetNewSetId(int currentSetId)
        => (currentSetId % 4) + VirtualOutfitsSection.GAME_OUTFIT_SETS + 1;

    private record FakeOutfitItemId(int OriginalId, int NewId);

    private record UpdateEquipParams(nint Param1, nint Param3, nint Param4, nint Param5);
}
