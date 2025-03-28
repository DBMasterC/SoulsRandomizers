﻿using SoulsFormats;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using SoulsIds;
using static SoulsIds.Events;
using static SoulsIds.GameSpec;
using static RandomizerCommon.EventConfig;
using static RandomizerCommon.LocationData;
using static RandomizerCommon.LocationData.ItemScope;
using static RandomizerCommon.LocationData.LocationKey;
using static RandomizerCommon.Messages;
using static RandomizerCommon.Permutation;
using static RandomizerCommon.Util;
using static SoulsFormats.EMEVD.Instruction;
using static System.Windows.Forms.Design.AxImporter;
using Org.BouncyCastle.Ocsp;
using System.IO;
using RefactorCommon;

namespace RandomizerCommon
{
    public class PermutationWriter
    {
        private static List<string> itemValueCells = new List<string> { "shopPrice", "Unk9", "Costvalue", "shopId" };

        private GameData game;
        private LocationData data;
        private AnnotationData ann;
        private Events events;
        private EventConfig eventConfig;
        private Messages messages;
        private EldenCoordinator coord;

        private PARAM shops;

        // Determination of whether an item is finite, for price classification purposes
        private readonly Dictionary<ItemKey, bool> isItemFiniteCache = new Dictionary<ItemKey, bool>();

        // Prices for items in a given category
        private readonly Dictionary<PriceCategory, List<int>> prices = new Dictionary<PriceCategory, List<int>>();

        private readonly Dictionary<ItemKey, int> upgradePrices = new Dictionary<ItemKey, int>();

        // List of quantities and drop chances per category
        private readonly Dictionary<PriceCategory, List<Dictionary<int, float>>> dropChances =
            new Dictionary<PriceCategory, List<Dictionary<int, float>>>();
        // Reversed GameData.ItemLotTypes
        // private readonly Dictionary<ItemType, uint> lotValues;

        private static readonly Dictionary<int, float> DEFAULT_CHANCES = new Dictionary<int, float> { { 1, 0.05f } };

        [Localize] private static readonly Text receiveBellBearing =
            new Text("Receive Bell Bearing", "GameMenu_receiveBellBearing");

        public PermutationWriter(GameData game, LocationData data, AnnotationData ann, Events events,
            EventConfig eventConfig, Messages messages = null, EldenCoordinator coord = null)
        {
            this.game = game;
            this.data = data;
            this.ann = ann;
            this.events = events;
            this.eventConfig = eventConfig;
            this.messages = messages;
            this.coord = coord;
            // itemLots = game.Param("ItemLotParam");
            shops = game.Param("ShopLineupParam");
            // lotValues = game.LotItemTypes.ToDictionary(e => e.Value, e => e.Key);
        }

        public enum PriceCategory
        {
            // First few should match ItemType ordering. Non-goods are very broad, to give the chance for some really good deals.
            WEAPON,
            ARMOR,
            RING,
            GOOD_PLACEHOLDER,
            GEM,

            // The rest are mainly goods
            SPELLS,
            ARROWS,
            FINITE_GOOD,
            INFINITE_GOOD,
            UPGRADE,
            TRANSPOSE,

            // Some Sekiro categories
            REGULAR_GOOD,
            UNIQUE_GOOD,
        }

        public class Result
        {
            public Dictionary<ItemKey, int> ItemEventFlags { get; set; }
            public Dictionary<SlotKey, int> SlotEventFlags { get; set; }
            public Dictionary<int, int> MerchantGiftFlags { get; set; }
        }

        public Result Write(Random random, Permutation permutation, RandomizerOptions opt)
        {
            bool writeSwitch = true;
            foreach (string hintType in ann.HintCategories)
            {
                Console.WriteLine($"-- Hints for {hintType}:");
                bool hasHint = false;
                foreach (KeyValuePair<SlotKey, SlotKey> assign in permutation.Hints[hintType]
                             .OrderBy(e => (game.DisplayName(e.Key.Item), permutation.GetLogOrder(e.Value))))
                {
                    LocationScope scope = data.Location(assign.Value).LocScope;
                    Console.WriteLine(
                        $"{game.DisplayName(assign.Key.Item)}: {ann.GetLocationHint(assign.Value, permutation.SpecialLocation(scope))}");
                    hasHint = true;
                    if (opt[BooleanOption.FullHint])
                    {
                        Console.WriteLine($"- {ann.GetLocationDescription(assign.Value)}");
                    }
                }

                if (!hasHint)
                {
                    Console.WriteLine("(not randomized)");
                }

                Console.WriteLine();
                if (opt[BooleanOption.Silent])
                {
                    writeSwitch = false;
                    break;
                }
            }

            Console.WriteLine("-- End of hints");
#if !DEBUG
            for (int i = 0; i < 30; i++) Console.WriteLine();
#endif

            // Gather all potential prices to select from

            foreach (KeyValuePair<ItemKey, ItemLocations> entry in data.Data)
            {
                ItemKey item = entry.Key;
                // Only Elden Ring has custom weapons, where itemValueCells is not used
                PARAM.Row row = game.Item(item);
                int price = game.EldenRing ? -1 : (int)row[itemValueCells[(int)item.Type]].Value;
                // int sellPrice = (int)row["sellValue"].Value;
                PriceCategory cat = GetPriceCategory(item);
                foreach (ItemLocation itemLoc in entry.Value.Locations.Values)
                {
                    foreach (LocationKey loc in itemLoc.Keys.Where(k => k.Type == LocationType.SHOP))
                    {
                        PARAM.Row shop = shops[loc.ID];
                        if (shop == null) continue;
                        int shopPrice = (int)shop["value"].Value;
                        if (price == -1 && shopPrice == -1) continue;
                        // No custom shops
                        if (game.EldenRing && (byte)shop["costType"].Value > 0) continue;
                        shopPrice = shopPrice == -1 ? price : shopPrice;
                        // Don't price regular items toooo high - looking at you, 20k for Tower Key. Key items are priced separately anyway
                        if (cat == PriceCategory.FINITE_GOOD && shopPrice > 10000) continue;
                        // 0 shop prices are okay in transpose shops in DS3, but does not get categorized
                        // in the same way in Elden Ring
                        if (game.EldenRing && shopPrice <= 0) continue;
                        AddMulti(prices, cat, shopPrice);
                        if (cat == PriceCategory.UPGRADE && itemLoc.Scope.Type == ScopeType.SHOP_INFINITE)
                        {
                            upgradePrices[item] = shopPrice;
                        }
                    }

                    if (itemLoc.Scope.Type == ScopeType.MODEL)
                    {
                        Dictionary<int, float> chances = GetDropChances(item, itemLoc);
                        // Console.WriteLine($"Location for {game.Name(item)}. Chances {string.Join(",", chances)}");
                        if (chances.Count > 0)
                        {
                            AddMulti(dropChances, cat, chances);
                        }
                    }
                }
            }


            List<string> lotSuffixes = game.EldenRing ? new List<string> { "_map", "_enemy" } : new List<string> { "" };

            // TODO: Make this work for DS3 again.
            Func<int, bool> isPermanent = null;
            Func<int, int> shopFlagForFixedFlag = null;
            List<(int, int)> itemLotFlags = null;
            List<(int, int)> shopFlags = null;
            Dictionary<int, ItemKey> trackedFlagItems = new Dictionary<int, ItemKey>();
            // For Elden Ring, map from unpowered flag -> (unpowered item, powered item)
            Dictionary<int, (ItemKey, ItemKey)> greatRunes = new Dictionary<int, (ItemKey, ItemKey)>
            {
                // Godrick's Great Rune
                [171] = (new ItemKey(ItemType.GOOD, 8148), new ItemKey(ItemType.GOOD, 191)),
                // Radahn's Great Rune
                [172] = (new ItemKey(ItemType.GOOD, 8149), new ItemKey(ItemType.GOOD, 192)),
                // Morgott's Great Rune
                [173] = (new ItemKey(ItemType.GOOD, 8150), new ItemKey(ItemType.GOOD, 193)),
                // Rykard's Great Rune
                [174] = (new ItemKey(ItemType.GOOD, 8151), new ItemKey(ItemType.GOOD, 194)),
                // Mohg's Great Rune
                [175] = (new ItemKey(ItemType.GOOD, 8152), new ItemKey(ItemType.GOOD, 195)),
                // Malenia's Great Rune
                [176] = (new ItemKey(ItemType.GOOD, 8153), new ItemKey(ItemType.GOOD, 196)),
                // Great Rune of the Unborn
                [177] = (new ItemKey(ItemType.GOOD, 10080), new ItemKey(ItemType.GOOD, 10080)),
            };
            int greatRuneBaseFlag = greatRunes.Keys.Min();
            int minimumGoodFlag = 50000000;
            if (game.EldenRing)
            {
                minimumGoodFlag = 100000;
                // Do some early processing of config to find new fixed lots, and also item lots to track
                HashSet<int> fixedFlags = new HashSet<int>();
                foreach (EventSpec spec in eventConfig.ItemEvents.Concat(eventConfig.ItemTalks))
                {
                    if (spec.ItemTemplate == null) continue;
                    foreach (ItemTemplate t in spec.ItemTemplate)
                    {
                        if (t.Type.Contains("item") && t.EventFlag != null)
                        {
                            List<int> flags = t.EventFlag.Split(' ').Select(w => int.Parse(w)).ToList();
                            if (t.Type == "fixeditem")
                            {
                                fixedFlags.UnionWith(flags);
                            }
                            else
                            {
                                foreach (int flag in flags)
                                {
                                    trackedFlagItems[flag] = null;
                                }
                            }
                        }
                    }
                }

                isPermanent = eventFlag =>
                {
                    return (eventFlag >= 60000 && eventFlag < 70000) || fixedFlags.Contains(eventFlag);
                };
                int fixedShopStart = 100400;
                shopFlagForFixedFlag = flag =>
                {
                    if (greatRunes.ContainsKey(flag))
                    {
                        int runeBase = flag - greatRuneBaseFlag;
                        return fixedShopStart + runeBase * 10;
                    }

                    return flag;
                };
                itemLotFlags = (game.Params["ItemLotParam_map"].Rows.Concat(game.Params["ItemLotParam_enemy"].Rows))
                    .Where(r => (uint)r["getItemFlagId"].Value > 0)
                    .Select(r => ((int)r.ID, (int)(uint)r["getItemFlagId"].Value)).OrderBy(r => r.Item1).ToList();
                shopFlags = game.Params["ShopLineupParam"].Rows
                    .Where(r => r.ID < 600000 && (uint)r["eventFlag_forStock"].Value > 0)
                    .Select(r => ((int)r.ID, (int)(uint)r["eventFlag_forStock"].Value)).OrderBy(r => r.Item1).ToList();
            }

            HashSet<int> allEventFlags = new HashSet<int>(data.Data.Values.SelectMany(locs =>
                locs.Locations.Values.Select(l => l.Scope.EventID).Where(l => l > 0)));
            if (itemLotFlags != null)
            {
                allEventFlags.UnionWith(itemLotFlags.Select(pair => pair.Item2));
            }

            if (shopFlags != null)
            {
                allEventFlags.UnionWith(shopFlags.Select(pair => pair.Item2));
            }

            int eventFlagForLocation(int id, LocationType type)
            {
                List<(int, int)> flagList = type == LocationType.LOT ? itemLotFlags : shopFlags;
                int searchStep = type == LocationType.LOT ? 1 : 10;
                int index = flagList.FindIndex(r => r.Item1 == id);
                for (int i = index + 1; i < flagList.Count; i++)
                {
                    // Scan forwards for an eligible entry
                    (int newLot, int flag) = flagList[i];
                    if (flag >= minimumGoodFlag)
                    {
                        // Scan for an unused flag
                        while (allEventFlags.Contains(flag))
                        {
                            flag += searchStep;
                        }

                        allEventFlags.Add(flag);
                        return flag;
                    }
                }

                throw new Exception($"{type} {id}, found at index {index}, can't event a dang flag");
            }
            // foreach (int flag in shops.Rows.Select(r => (int)r["EventFlag"].Value).Where(f => f > 1000).Distinct().OrderBy(f => f)) Console.WriteLine($"shop {flag}");

            // Map from item to its final item get flag, to be filled in
            Dictionary<ItemKey, int> itemEventFlags = new Dictionary<ItemKey, int>();
            // Other map from slot to final item get flag, also to be filled in
            Dictionary<SlotKey, int> slotEventFlags = new Dictionary<SlotKey, int>();
            HashSet<ItemKey> trackedSlotItems = new HashSet<ItemKey>();
            // Mapping from old permanent event flag to slot key
            Dictionary<SlotKey, int> permanentSlots = new Dictionary<SlotKey, int>();
            if (isPermanent != null)
            {
                foreach (KeyValuePair<ItemKey, ItemLocations> item in data.Data)
                {
                    foreach (ItemLocation loc in item.Value.Locations.Values)
                    {
                        if (loc.Scope.Type == ScopeType.EVENT)
                        {
                            int eventFlag = loc.Scope.ID;
                            if (isPermanent(eventFlag))
                            {
                                // Console.WriteLine($"Permanent {eventFlag}: {game.Name(item.Key)}");
                                SlotKey source = new SlotKey(item.Key, loc.Scope);
                                if (permanentSlots.ContainsKey(source)) throw new Exception($"{eventFlag}");
                                permanentSlots[source] = eventFlag;
                            }
                            else if (trackedFlagItems.TryGetValue(eventFlag, out ItemKey assign) && assign == null)
                            {
                                // This is pretty hacky: only record the first item, using ItemKey order.
                                // This distinguishes between Fingerslayer Blade (preferred) and Great Ghost Glovewort (not).
                                trackedFlagItems[eventFlag] = item.Key;
                                itemEventFlags[item.Key] = -1;
                                // Console.WriteLine($"Tracking {game.Name(item.Key)} for {eventFlag}");
                            }
                        }
                    }
                }
            }

            // Mapping for merchant gift feature from NpcName id to flag, to hide merchant locations after receiving it
            Dictionary<int, int> merchantGiftFlags = new Dictionary<int, int>();
            // Map from material id (DS3) or shop id (Elden Ring) to boss soul
            Dictionary<int, ItemKey> bossSoulItems = new Dictionary<int, ItemKey>();
            // Materials based on boss souls
            if (game.EldenRing)
            {
                foreach (PARAM.Row row in shops.Rows)
                {
                    if (row.ID >= 101775 && row.ID < 101800)
                    {
                        ItemKey item = new ItemKey((ItemType)(byte)row["equipType"].Value, (int)row["equipId"].Value);
                        if (item.Type == ItemType.GOOD && item.ID >= 2950 && item.ID < 2990)
                        {
                            bossSoulItems[row.ID] = item;
                            itemEventFlags[item] = 0;
                        }
                    }
                }

                // Also, record key item event flags for hints later
                foreach (ItemKey keyItem in ann.ItemGroups["keyitems"])
                {
                    itemEventFlags[keyItem] = 0;
                }

                // Other items for hints, which may not be singletons and are tracked by slot
                trackedSlotItems.UnionWith(ann.ItemGroups["markhints"]);

                // There are a few events/qwcs dependent on getting other items
                // Duplicating boss souls: should depend on getting the boss soul (QWC edit)
                // Duplicating ashes of war: should depend on having the item (event edit)
                // Accessing cookbooks: unfortunately, should depend on having the item (permanent flag)
                // 60150 golden tailoring tools (permanent flag)
                // 11109770 etc. has a bunch of equivalent event flags (equivalent)
                // 1042369416 Lone Wolf Ashes are similar (TODO look through QWCs, but also equivalent-ify these)
            }

            Dictionary<SlotKey, ItemSource> newRows = new Dictionary<SlotKey, ItemSource>();
            Dictionary<string, HashSet<int>> deleteRows = new Dictionary<string, HashSet<int>>();
            // Dump all target data per-source, before wiping it out
            foreach (KeyValuePair<RandomSilo, SiloPermutation> entry in permutation.Silos)
            {
                SiloPermutation silo = entry.Value;
                foreach (SlotKey sourceKey in silo.Mapping.Values.SelectMany(v => v))
                {
                    ItemLocation source = data.Location(sourceKey);
                    foreach (LocationKey locKey in source.Keys)
                    {
                        if (locKey.Type == LocationType.LOT)
                        {
                            AddMulti(deleteRows, locKey.ParamName, locKey.ID);
                        }
                    }

                    // Synthetic items, like Path of the Dragon
                    if (source.Keys.Count() == 0)
                    {
                        newRows[sourceKey] = new ItemSource(source, null);
                        continue;
                    }

                    // Pick one of the source for item data - they should be equivalent.
                    LocationKey key = source.Keys[0];
                    object itemRow;
                    if (key.Type == LocationType.LOT)
                    {
                        PARAM.Row row = game.Params[key.ParamName][key.ID];
                        itemRow = new LotCells
                            { Game = game, Cells = row.Cells.ToDictionary(c => c.Def.InternalName, c => c.Value) };
                    }
                    else
                    {
                        PARAM.Row row = game.Params[key.ParamName][key.ID];
                        itemRow = new ShopCells
                            { Game = game, Cells = row.Cells.ToDictionary(c => c.Def.InternalName, c => c.Value) };
                    }

                    newRows[sourceKey] = new ItemSource(source, itemRow);
                }
            }

            // Hack for 177 event flag Rennala drop.
            // It's the only case a no-item event flag drop is used by the game, it seems, so it's not picked up in LocationData.
            // So don't accidentally automatically grant 177 from Rennala and have it count as acquiring the Great Rune.
            if (game.EldenRing)
            {
                PARAM.Row row = game.Params["ItemLotParam_map"][10182];
                if (row != null && (uint)row["getItemFlagId"].Value == 177)
                {
                    deleteRows["ItemLotParam_map"].Add(10182);
                }
            }

            int dragonFlag = 0;
            Dictionary<int, int> memoryFlags = new Dictionary<int, int>();
            Dictionary<int, byte> itemRarity = new Dictionary<int, byte>();
            if (!game.EldenRing)
            {
                itemRarity = game.Params["ItemLotParam"].Rows
                    .Where(row => deleteRows["ItemLotParam"].Contains(row.ID))
                    .ToDictionary(row => row.ID, row => (byte)row["LotItemRarity"].Value);
            }

            foreach (string paramType in deleteRows.Keys)
            {
                game.Params[paramType].Rows.RemoveAll(row => deleteRows[paramType].Contains(row.ID));
            }

            List<ItemKey> syntheticUniqueItems = new List<ItemKey>();
            if (game.EldenRing)
            {
                syntheticUniqueItems = new List<ItemKey>
                {
                    game.ItemForName("Imbued Sword Key"), game.ItemForName("Imbued Sword Key 2"),
                    game.ItemForName("Imbued Sword Key 3"),
                };
            }

            // Tuple of (area, tag type, description)
            List<(string, string, string)> raceModeInfo = new List<(string, string, string)>();
            Dictionary<int, int> rewrittenFlags = new Dictionary<int, int>();
            Dictionary<int, int> shopPermanentFlags = new Dictionary<int, int>();
            HashSet<string> defaultFilter = new HashSet<string> { "ignore" };
            bool debugPerm = false;
            Console.WriteLine($"-- Spoilers:");
            foreach (KeyValuePair<RandomSilo, SiloPermutation> siloEntry in permutation.Silos)
            {
                RandomSilo siloType = siloEntry.Key;
                SiloPermutation silo = siloEntry.Value;
                if (siloType == RandomSilo.REMOVE) continue;
                foreach (KeyValuePair<SlotKey, List<SlotKey>> mapping in silo.Mapping.OrderBy(e =>
                             permutation.GetLogOrder(e.Key)))
                {
                    SlotKey targetKey = mapping.Key;
                    ItemLocation targetLocation = data.Location(targetKey);
                    // Event flag - it just so happens that most of the time, we can use the scope to find the one event flag to use - scripts don't specially care about one vs the other.
                    int eventFlag = targetLocation.Scope.EventID;
                    foreach (SlotKey sourceKey in mapping.Value)
                    {
                        ItemKey item = sourceKey.Item;
                        if (syntheticUniqueItems.Contains(item))
                        {
                            item = syntheticUniqueItems[0];
                        }

                        int quantity = data.Location(sourceKey).Quantity;
                        string quantityStr = quantity == 1 ? "" : $" {quantity}x";
                        string desc = ann.GetLocationDescription(targetKey, excludeTags: defaultFilter, coord: coord);
                        if (desc != null && writeSwitch)
                        {
                            Console.WriteLine($"{game.DisplayName(item, quantity)}{desc}");
                        }
#if DEBUG
                        if (desc == null && writeSwitch)
                        {
                            desc = ann.GetLocationDescription(targetKey, coord: coord);
                            Console.WriteLine($"{game.DisplayName(item, quantity)}{desc} - ignored");
                        }
#endif
                        bool printChances = true;
                        if (opt[BooleanOption.RaceModeInfo])
                        {
                            HashSet<string> filterTags = ann.RaceModeTags;
                            string raceDesc = ann.GetLocationDescription(targetKey, filterTags);
                            if (!string.IsNullOrEmpty(raceDesc))
                            {
                                // Look up tags just to include better filtering info.
                                // This is slightly outside of the scope of this module.
                                if (ann.Slots.TryGetValue(data.Location(targetKey).LocScope,
                                        out AnnotationData.SlotAnnotation slot))
                                {
                                    filterTags = new HashSet<string>(filterTags);
                                    filterTags.UnionWith(new[] { "night", "minidungeon", "missable", "norandom" });
                                    string raceTags = string.Join(" ",
                                        slot.TagList.Intersect(filterTags).OrderBy(x => x));
                                    raceModeInfo.Add((slot.Area, raceTags, raceDesc));
                                }
                            }
                        }


                        if (!newRows.TryGetValue(sourceKey, out ItemSource source))
                        {
                            throw new Exception($"Error: Expected a param row for {sourceKey} in {siloType}");
                        }

                        ShopCells shopCells = null;
                        LotCells lotCells = null;
                        int price = -1;
                        bool originalShop = false;
                        if (source.Row == null)
                        {
                            // Synthetic item - make up shop entry
                            shopCells = ShopCellsForItem(item);
                            MakeSellable(item);
                        }
                        else if (source.Row is ShopCells)
                        {
                            shopCells = (ShopCells)source.Row;
                            originalShop = true;
                        }
                        else if (source.Row is LotCells)
                        {
                            lotCells = (LotCells)source.Row;
                        }
                        else throw new Exception($"Unknown item source");

                        // TODO: Assigning enemy drops to other enemy drops/infinite shops, should scope which item is being referred to
                        int setEventFlag = -1;
                        foreach (LocationKey target in targetLocation.Keys)
                        {
                            // Console.WriteLine($"{game.Name(item)}: {source.Loc} -> {target.Text}");
                            if (target.Type == LocationType.LOT)
                            {
                                // Console.WriteLine($"{game.Name(item)}: setting lot target, source is {source.Loc}. lotCells {lotCells} shopCells {shopCells}.");
                                if (siloType == RandomSilo.MIXED)
                                {
                                    Warn($"Mixed silo {source.Loc} going to {target}");
                                    continue;
                                }

                                if (lotCells == null)
                                {
                                    ShopCells sourceShop = shopCells;

                                    lotCells = ShopToItemLot(sourceShop, item, random, writeSwitch);
                                }
                                else if (targetLocation.Scope.Type == ScopeType.MODEL)
                                {
                                    if (originalShop)
                                    {
                                        lotCells = ShopToItemLot(shopCells, item, random, writeSwitch);
                                    }
                                    else
                                    {
                                        Dictionary<int, float> chances = GetDropChances(item, data.Location(sourceKey));
                                        if (chances.Count == 0) chances = DEFAULT_CHANCES;
                                        lotCells = ProcessModelLot(lotCells, item, chances,
                                            printChances && writeSwitch);
                                        printChances = false;
                                    }
                                }

                                if (permanentSlots.TryGetValue(sourceKey, out int permanentFlag))
                                {
                                    if (debugPerm)
                                        Console.WriteLine(
                                            $"Changing lot flag from temp {eventFlag} to permanent {permanentFlag}");
                                    rewrittenFlags[eventFlag] = permanentFlag;
                                    lotCells.EventFlag = permanentFlag;
                                }
                                else if (permanentSlots.TryGetValue(targetKey, out int flagToClear))
                                {
                                    if (!rewrittenFlags.TryGetValue(eventFlag, out int tempFlag))
                                    {
                                        tempFlag = eventFlagForLocation(target.BaseID, LocationType.LOT);
                                    }

                                    if (debugPerm)
                                        Console.WriteLine(
                                            $"Changing lot flag from permanent {flagToClear} to temp {tempFlag}");
                                    rewrittenFlags[eventFlag] = tempFlag;
                                    lotCells.EventFlag = tempFlag;
                                }
                                else
                                {
                                    lotCells.EventFlag = eventFlag;
                                }

                                setEventFlag = lotCells.EventFlag;
                                // Crow sources are special items so they won't be removed, they must be overwritten
                                AddLot(target.ParamName, target.BaseID, lotCells, itemRarity,
                                    siloType == RandomSilo.CROW);
                            }
                            else
                            {
                                // Do some filtering for RandomSilo.MIXED
                                if (shopCells == null)
                                {
                                    if (siloType == RandomSilo.MIXED)
                                    {
                                        Warn($"Mixed silo {source.Loc} going to {target}");
                                        continue;
                                    }

                                    shopCells = ItemLotToShop(lotCells, item);
                                }

                                // If mixed, event flag is present or not based on which shop entry this is (infinite or not)
                                bool infiniteMixed = siloType == RandomSilo.MIXED && shopCells.Quantity <= 0;
                                // Ignore scope event flag for shop assignment, because some shops also form multidrops
                                string flagField = game.EldenRing ? "eventFlag_forStock" : "EventFlag";
                                object originalFlag = game.Params[target.ParamName][target.ID][flagField].Value;
                                int shopEventFlag = game.EldenRing ? (int)(uint)originalFlag : (int)originalFlag;
                                if (permanentSlots.TryGetValue(sourceKey, out int permanentFlag))
                                {
                                    if (shopFlagForFixedFlag != null)
                                    {
                                        // Way too many event flags involved here.
                                        // There is permanent flag (persists across NG, applies to item only)
                                        // There is shop permanent flag (previously unused, always set with permanent flag)
                                        // There is old shop flag (does not apply to item)
                                        // TODO: This needs to be reverified after merge with Elden logic
                                        int shopPermanentFlag = shopFlagForFixedFlag(permanentFlag);
                                        shopPermanentFlags[shopPermanentFlag] = permanentFlag;
                                        permanentFlag = shopPermanentFlag;
                                    }

                                    if (debugPerm)
                                        Console.WriteLine(
                                            $"Changing shop flag from temp {shopEventFlag} to permanent {permanentFlag}");
                                    rewrittenFlags[shopEventFlag] = permanentFlag;
                                    shopEventFlag = permanentFlag;
                                }
                                else if (permanentSlots.TryGetValue(targetKey, out int flagToClear))
                                {
                                    if (!rewrittenFlags.TryGetValue(shopEventFlag, out int tempFlag))
                                    {
                                        tempFlag = eventFlagForLocation(target.BaseID, LocationType.SHOP);
                                    }

                                    if (debugPerm)
                                        Console.WriteLine(
                                            $"Changing shop flag from permanent {flagToClear} to temp {tempFlag}");
                                    rewrittenFlags[shopEventFlag] = tempFlag;
                                    shopEventFlag = tempFlag;
                                }

                                shopCells.EventFlag = infiniteMixed ? -1 : shopEventFlag;
                                setEventFlag = shopCells.EventFlag;
                                int baseShop = target.ID / 100;
                                if (price == -1)
                                {
                                    if (siloType == RandomSilo.SELF && shopCells.Cells.ContainsKey("value"))
                                    {
                                        // Don't use price calculation for non-randomized shops (can this ever be not defined?)
                                        // TODO: Why go through this shuffling routine at all?
                                        price = shopCells.Value;
                                    }
                                    else
                                    {
                                        bool isTranspose = targetLocation.Scope.Type == ScopeType.MATERIAL;
                                        price = Price(permutation, siloType, item, isTranspose, random);
                                    }

                                    if (desc != null && writeSwitch)
                                    {
                                        Console.WriteLine($"  (cost: {price})");
                                    }
                                }

                                // Ignoring selected price for offering box
                                int targetPrice = price;

                                // Likewise for custom shops in Elden Ring
                                if (game.EldenRing && target.Subtype == null &&
                                    (byte)shops[target.ID]["costType"].Value > 0)
                                {
                                    targetPrice = (int)shops[target.ID]["value"].Value;
                                }

                                shopCells.Value = targetPrice;
                                SetShop(target, shopCells);
                            }
                        }

                        // Add special flags for specific items
                        // Use sourceKey.Item, instead of item, for synthetic item tracking per source
                        if (itemEventFlags.ContainsKey(sourceKey.Item) && itemEventFlags[sourceKey.Item] <= 0 &&
                            setEventFlag > 0)
                        {
                            itemEventFlags[sourceKey.Item] = setEventFlag;
                        }

                        if (trackedSlotItems.Contains(sourceKey.Item) && setEventFlag > 0)
                        {
                            slotEventFlags[sourceKey] = setEventFlag;
                        }
                    }
                }
            }

            foreach (string suffix in lotSuffixes)
            {
                PARAM itemLots = game.Params["ItemLotParam" + suffix];
                itemLots.Rows = itemLots.Rows.OrderBy(r => r.ID).ToList();
            }

            Console.WriteLine("-- End of item spoilers");
            Console.WriteLine();

            // Hacky convenience function for generating race mode list
            if (opt[BooleanOption.RaceModeInfo])
            {
                List<string> locationDocs = new List<string>
                {
                    "This is a list of all possible important locations: places you may need to check to finish an item randomizer run, depending on options.",
                    "You may configure which categories apply to a run by checking or unchecking important location categories in the randomizer program.",
                    "To see the actual locations of items in a run, check the latest file in the spoiler_logs directory.",
                };
                foreach (string locationDoc in locationDocs)
                {
                    Console.WriteLine(locationDoc);
                    Console.WriteLine();
                }

                Dictionary<string, List<(string, string)>> areaEntries =
                    new Dictionary<string, List<(string, string)>>();
                SortedDictionary<string, string> tagTypes = new SortedDictionary<string, string>
                {
                    ["altboss"] = "50Minor boss",
                    ["altboss minidungeon"] = "51Minor minidungeon boss",
                    ["altboss night"] = "52Minor night boss",
                    ["boss"] = "10Major boss",
                    ["church"] = "20Flask upgrade",
                    ["minidungeon raceshop"] = "41Minidungeon shop",
                    ["minidungeon talisman"] = "61Minidungeon talisman",
                    ["racemode"] = "00Key item",
                    ["raceshop"] = "30Shop",
                    ["seedtree"] = "21Flask upgrade",
                    ["talisman"] = "60Talisman",
                };
                HashSet<string> actualTypes = new HashSet<string>();
                Dictionary<string, List<string>> replaces = new Dictionary<string, List<string>>();
                // Sorta terrible but better than doing it by hand
                string hack = ". Replaces ";
                foreach (var val in raceModeInfo)
                {
                    (string area, string tags, string desc) = val;
                    if (!tagTypes.ContainsKey(tags)) tagTypes[tags] = null;
                    actualTypes.Add(tags);
                    string replace = desc.Substring(desc.IndexOf(hack) + hack.Length).TrimEnd('.');
                    desc = desc.Substring(0, desc.IndexOf(hack));
                    string fullDesc = $"{tagTypes[tags] ?? $"???? {tags}"}{desc}";
                    if (ann.Areas[area].Tags != null && ann.Areas[area].Tags.Contains("minidungeon"))
                    {
                        // Hardcode this one, rare since it's a minidungeon following a key item
                        area = area == "snowfield_hiddenpath" ? "snowfield" : ann.Areas[area].Req.Split(' ')[0];
                    }

                    AddMulti(areaEntries, ann.Areas[area].Text, (fullDesc, tags));
                    if (!tags.Contains("raceshop"))
                    {
                        AddMulti(replaces, fullDesc, replace);
                    }
                }

                HashSet<string> visited = new HashSet<string>();
                Dictionary<string, List<string>> finalEntries = new Dictionary<string, List<string>>();
                foreach (AnnotationData.AreaAnnotation areaAnn in ann.Areas.Values)
                {
                    if (areaAnn.Name == "chapel_start") continue;
                    if (!visited.Add(areaAnn.Text)) continue;
                    if (!areaEntries.TryGetValue(areaAnn.Text, out List<(string, string)> descs)) continue;
                    Console.WriteLine("--- " + areaAnn.Text);
                    foreach ((string desc, string tags) in descs.OrderBy(x => x).Distinct())
                    {
                        string text = desc.Substring(2);
                        if (replaces.ContainsKey(desc))
                            text += $". Replaces {string.Join(", ", replaces[desc].Distinct())}.";
                        Console.WriteLine(text);
                        AddMulti(finalEntries, tags, text);
                    }

                    Console.WriteLine();
                }

                foreach (KeyValuePair<string, string> entry in tagTypes)
                {
                    if (!actualTypes.Contains(entry.Key)) continue;
                    Console.WriteLine($"[\"{entry.Key}\"] = \"{entry.Value}\",  // {finalEntries[entry.Key].Count}");
                }
            }

            // Events
            if (game.EldenRing)
            {
                // Replace duplication boss defeat flags with soul get flags
                foreach (PARAM.Row row in shops.Rows)
                {
                    if (bossSoulItems.TryGetValue(row.ID, out ItemKey soul) &&
                        itemEventFlags.TryGetValue(soul, out int soulFlag) && soulFlag > 0)
                    {
                        row["eventFlag_forRelease"].Value = soulFlag;
                    }
                }

                // Synthetic Rold lot. The location flag is still 40001, but the item's flag is changed.
                int roldFlag = GameData.EldenRingBase + 2010;
                int roldEventId = GameData.EldenRingBase + 1010;
                if (opt.GetStringAsInt(StringOption.Runes_Rold, 0, 7, out _))
                {
                    ItemKey rold = ann.ItemGroups["removerold"][0];
                    if (!(itemEventFlags.TryGetValue(rold, out int flag) && flag > 0))
                    {
                        itemEventFlags[rold] = roldFlag;
                    }
                }

                HashSet<(ItemTemplate, int)> completedTemplates = new HashSet<(ItemTemplate, int)>();

                bool getFlagEdit(string type, int flag, out int targetFlag, out ItemKey item)
                {
                    targetFlag = 0;
                    item = null;
                    if (type.StartsWith("item"))
                    {
                        if (trackedFlagItems.TryGetValue(flag, out item))
                        {
                            if (itemEventFlags.TryGetValue(item, out int itemFlag)
                                && itemFlag > 0 && itemFlag != flag)
                            {
                                targetFlag = itemFlag;
                            }

                            return true;
                        }
                    }
                    else if (type.StartsWith("loc"))
                    {
                        // rewrittenFlags is the alteration of an item lot or shop's flag, so location checks should use the new flag
                        if (rewrittenFlags.TryGetValue(flag, out int newFlag) && newFlag != flag)
                        {
                            targetFlag = newFlag;
                            return true;
                        }
                    }

                    return false;
                }

                // Unfortunately, clone and modify entire Sekiro emevd routine because it depends on EMEDF and we don't have a usable one
                Dictionary<int, EventSpec> templates = eventConfig.ItemEvents.ToDictionary(e => e.ID, e => e);

                Dictionary<(int, int), (int, int)> flagPositions = new Dictionary<(int, int), (int, int)>
                {
                    [(3, 0)] = (1, 1),
                    [(3, 1)] = (1, 2),
                    [(3, 10)] = (1, 2),
                    [(3, 12)] = (1, 1),
                    [(1003, 0)] = (1, 1),
                    [(1003, 1)] = (1, 1),
                    [(1003, 2)] = (1, 1),
                    [(1003, 3)] = (1, 2),
                    [(1003, 4)] = (1, 2),
                    [(1003, 101)] = (1, 1),
                    [(1003, 103)] = (1, 2),
                    [(2003, 17)] = (0, 1),
                    [(2003, 22)] = (0, 1),
                    [(2003, 63)] = (0, 1),
                    [(2003, 66)] = (1, 1),
                    [(2003, 69)] = (1, 1),
                };

                Dictionary<int, EMEVD.Event> commonEvents =
                    game.Emevds["common_func"].Events.ToDictionary(e => (int)e.ID, e => e);
                HashSet<string> specialEdits = new HashSet<string>
                {
                    "ashdupe", "singleton", "removecheck", "volcanoreq", "runearg", "leyndell"
                };
                foreach (KeyValuePair<string, EMEVD> entry in game.Emevds)
                {
                    EMEVD emevd = entry.Value;
                    Dictionary<int, EMEVD.Event> fileEvents = entry.Value.Events.ToDictionary(e => (int)e.ID, e => e);
                    foreach (EMEVD.Event e in emevd.Events)
                    {
                        for (int i = 0; i < e.Instructions.Count; i++)
                        {
                            EMEVD.Instruction init = e.Instructions[i];
                            if (!(init.Bank == 2000 && (init.ID == 0 || init.ID == 6))) continue;
                            List<object> initArgs =
                                init.UnpackArgs(Enumerable.Repeat(ArgType.Int32, init.ArgData.Length / 4));
                            int offset = 2;
                            int callee = (int)initArgs[1];
                            if (!templates.TryGetValue(callee, out EventSpec ev)) continue;
                            if (ev.ItemTemplate.Count == 0) throw new Exception($"event {callee} has no templates");
                            if (ev.ItemTemplate[0].Type == "remove")
                            {
                                // Remove action by removing initialization, for now. Can garbage collect later if desired.
                                e.Instructions[i] = new EMEVD.Instruction(1014, 69);
                                continue;
                            }

                            // Source flag and event to edit. We're not copying the event so only one type of pass is required.
                            List<(int, EMEVD.Event, ItemTemplate)> eventCopies =
                                new List<(int, EMEVD.Event, ItemTemplate)>();
                            foreach (ItemTemplate t in ev.ItemTemplate)
                            {
                                // Types: item itemarg, loc locarg, fixeditem, default, ashdupe, singleton, volcanoreq, remove
                                if (t.Type == "remove" || t.Type == "fixeditem" || t.Type == "default") continue;
                                List<int> templateFlags = t.EventFlag == null
                                    ? new List<int>()
                                    : t.EventFlag.Split(' ').Select(int.Parse).ToList();
                                List<int> flags;
                                if (specialEdits.Contains(t.Type))
                                {
                                    flags = new List<int> { 0 };
                                }
                                else if (t.Type.Contains("arg"))
                                {
                                    if (t.EventFlagArg == null)
                                        throw new Exception(
                                            $"Internal error: No arg defined for item flag in {callee}");
                                    if (!TryArgSpec(t.EventFlagArg.Split(' ').Last(), out int pos))
                                    {
                                        throw new Exception($"Internal error: Bad argspec {callee}");
                                    }

                                    int argFlag = (int)initArgs[offset + pos];
                                    if (!templateFlags.Contains(argFlag))
                                    {
                                        // Console.WriteLine($"{callee}: {t.EventFlagArg} {argFlag} not an item flag");
                                        continue;
                                    }

                                    flags = new List<int> { argFlag };
                                }
                                else
                                {
                                    flags = templateFlags;
                                }

                                foreach (int flag in flags)
                                {
                                    if (t.Type.Contains("arg"))
                                    {
                                        eventCopies.Add((flag, null, t));
                                    }
                                    else if (fileEvents.TryGetValue(callee, out EMEVD.Event theEvent) ||
                                             commonEvents.TryGetValue(callee, out theEvent))
                                    {
                                        if (completedTemplates.Contains((t, flag))) continue;
                                        completedTemplates.Add((t, flag));
                                        eventCopies.Add((flag, theEvent, t));
                                    }
                                    else
                                    {
                                        throw new Exception(
                                            $"Initialized event {callee} but absent from {entry.Key} and not specified in args");
                                    }
                                }
                            }

                            foreach (var copy in eventCopies)
                            {
                                (int flag, EMEVD.Event e2, ItemTemplate t) = copy;
                                // Types: item itemarg, loc locarg, ashdupe, singleton
                                if (t.Type == "ashdupe")
                                {
                                    // In this case, edit the event. Simplified version of it:
                                    // Event 65810. X0_4 = duplication shop qwc, X4_4 = get event flag
                                    // EndIf(EventFlag(X0_4));
                                    // WaitFor(EventFlag(X4_4));
                                    // SetEventFlag(TargetEventFlagType.EventFlag, X0_4, ON);
                                    // if (!EventFlag(65800)) SetEventFlag(TargetEventFlagType.EventFlag, 65800, ON);
                                    // Unfortunately, PlayerHasItem doesn't work for gems. We need to do an off->on check.
                                    OldParams pre = OldParams.Preprocess(e2);
                                    // EndIfEventFlag(EventEndType.End, ON, TargetFlagType.EventFlag, X4_4)
                                    EMEVD.Instruction check = new EMEVD.Instruction(1003, 2,
                                        new List<object> { (byte)0, (byte)1, (byte)0, 0 });
                                    pre.AddParameters(check,
                                        new List<EMEVD.Parameter> { new EMEVD.Parameter(0, 4, 4, 4) });
                                    e2.Instructions.Insert(0, check);
                                    pre.Postprocess();
                                    continue;
                                }
                                else if (t.Type == "singleton")
                                {
                                    OldParams pre = OldParams.Preprocess(e2);
                                    // EndIfEventFlag(EventEndType.End, ON, TargetEventFlagType.EventIDSlotNumber, 0)
                                    EMEVD.Instruction check = new EMEVD.Instruction(1003, 2,
                                        new List<object> { (byte)0, (byte)1, (byte)2, 0 });
                                    e2.Instructions.Insert(0, check);
                                    pre.Postprocess();
                                    continue;
                                }
                                else if (t.Type == "removecheck")
                                {
                                    OldParams pre = OldParams.Preprocess(e2);
                                    if (TryArgSpec(t.EventFlagArg, out int pos))
                                    {
                                        for (int j = e2.Instructions.Count - 1; j >= 0; j--)
                                        {
                                            EMEVD.Instruction ins = e2.Instructions[j];
                                            // EndIfEventFlag(EventEndType.End, ON, TargetEventFlagType.EventFlag, X12_4)
                                            if (ins.Bank == 1003 && ins.ID == 2)
                                            {
                                                EMEVD.Parameter flagParam = e2.Parameters.Find(p =>
                                                    p.InstructionIndex == j && p.SourceStartByte == pos * 4 &&
                                                    p.TargetStartByte == 4);
                                                if (flagParam != null)
                                                {
                                                    e2.Instructions[j] = new EMEVD.Instruction(1014, 69);
                                                    game.WriteEmevds.Add(entry.Key);
                                                }
                                            }
                                        }
                                    }

                                    pre.Postprocess();
                                    continue;
                                }
                                else if (t.Type == "volcanoreq")
                                {
                                    // Don't switch to 3106 and 3107 without joining the Volcano Manor (flag 16009208)
                                    // Accomplish through label and jump
                                    bool addedLabel = false;
                                    for (int j = e2.Instructions.Count - 1; j >= 0; j--)
                                    {
                                        EMEVD.Instruction ins = e2.Instructions[j];
                                        // SetEventFlag(TargetEventFlagType.EventFlag, 3107, ON)
                                        if (ins.Bank == 2003 && ins.ID == 66)
                                        {
                                            List<object> args = ins.UnpackArgs(new[]
                                                { ArgType.Byte, ArgType.UInt32, ArgType.Byte });
                                            if ((uint)args[1] == 3107)
                                            {
                                                EMEVD.Instruction add = new EMEVD.Instruction(1014, 15);
                                                e2.Instructions.Insert(j + 1, add);
                                                addedLabel = true;
                                            }
                                        }

                                        // GotoIfEventFlag(Label.LABEL0, OFF, TargetEventFlagType.EventFlag, 3100)
                                        if (addedLabel && ins.Bank == 1003 && ins.ID == 101)
                                        {
                                            List<object> args = ins.UnpackArgs(new[]
                                                { ArgType.Byte, ArgType.Byte, ArgType.Byte, ArgType.UInt32 });
                                            if ((byte)args[0] == 0 && (uint)args[3] == 3100)
                                            {
                                                EMEVD.Instruction add = new EMEVD.Instruction(
                                                    1003, 101,
                                                    new List<object> { (byte)15, (byte)0, (byte)0, 16009208 });
                                                e2.Instructions.Insert(j + 1, add);
                                                break;
                                            }
                                        }
                                    }

                                    continue;
                                }
                                else if (t.Type == "runearg")
                                {
                                    // First arg position is the rune activation flag (191 through 196)
                                    // Second arg position is the must-show-up flag (boss defeat flag by default)
                                    // Rewrite the second to match the first, when it's a valid activation flag
                                    // This depends on the indices lining up; otherwise, it will require a manual list.
                                    string[] parts = t.EventFlagArg?.Split(' ');
                                    if (parts?.Length != 2
                                        || !TryArgSpec(parts[0], out int activatePos)
                                        || !TryArgSpec(parts[1], out int showPos))
                                    {
                                        throw new Exception($"Internal error: Invalid runearg format {t.EventFlagArg}");
                                    }

                                    int activateFlag = (int)initArgs[offset + activatePos];
                                    if (activateFlag >= 191 && activateFlag <= 196)
                                    {
                                        int getFlag = activateFlag - 20;
                                        initArgs[offset + showPos] = getFlag;
                                        init.PackArgs(initArgs);
                                        game.WriteEmevds.Add(entry.Key);
                                    }

                                    continue;
                                }
                                else if (t.Type == "leyndell")
                                {
                                    // For the leyndell edit, replace the Great Runes flag with a different amount if requested
                                    // It turns out that 180 is a valid flag after 0 GRs.
                                    if (!opt.GetStringAsInt(StringOption.Runes_Leyndell, 0, 7, out int leyndellRunes) ||
                                        leyndellRunes == 2)
                                    {
                                        continue;
                                    }

                                    int unlockFlag = 180 + leyndellRunes;
                                    OldParams pre = OldParams.Preprocess(e2);
                                    for (int j = e2.Instructions.Count - 1; j >= 0; j--)
                                    {
                                        EMEVD.Instruction ins = e2.Instructions[j];
                                        if (flagPositions.TryGetValue((ins.Bank, ins.ID), out (int, int) range))
                                        {
                                            (int aPos, int bPos) = range;
                                            List<object> args =
                                                ins.UnpackArgs(Enumerable.Repeat(ArgType.Int32,
                                                    ins.ArgData.Length / 4));
                                            int flagVal = (int)args[bPos];
                                            if (flagVal != 182) continue;
                                            args[aPos] = args[bPos] = unlockFlag;
                                            ins.PackArgs(args);
                                            game.WriteEmevds.Add(entry.Key);
                                        }
                                    }

                                    pre.Postprocess();
                                    continue;
                                }

                                if (flag <= 0)
                                    throw new Exception($"Internal error: Flag missing for {callee} item flag rewrite");

                                if (!getFlagEdit(t.Type, flag, out int targetFlag, out ItemKey item))
                                {
                                    continue;
                                }

                                if (t.Type == "itemflag")
                                {
                                    // In cases where items are given for quests, the item's presence can't be used
                                    item = null;
                                }

                                bool edited = false;
                                if (t.EventFlagArg != null)
                                {
                                    foreach (string arg in t.EventFlagArg.Split(' '))
                                    {
                                        if (!TryArgSpec(arg, out int pos))
                                        {
                                            throw new Exception($"Internal error: Bad argspec {callee}");
                                        }

                                        initArgs[offset + pos] = targetFlag;
                                        init.PackArgs(initArgs);
                                        edited = true;
                                    }
                                }
                                else if (e2 != null)
                                {
                                    OldParams pre = OldParams.Preprocess(e2);
                                    for (int j = e2.Instructions.Count - 1; j >= 0; j--)
                                    {
                                        EMEVD.Instruction ins = e2.Instructions[j];
                                        if (flagPositions.TryGetValue((ins.Bank, ins.ID), out (int, int) range))
                                        {
                                            (int aPos, int bPos) = range;
                                            List<object> args =
                                                ins.UnpackArgs(Enumerable.Repeat(ArgType.Int32,
                                                    ins.ArgData.Length / 4));
                                            int flagVal = (int)args[bPos];
                                            if (flag != flagVal) continue;
                                            // Custom case for item checks: check item directly, if it can be done in-place
                                            // This doesn't get all of them, there are a few skips/gotos/ends e.g. in 12042400, 1050563700
                                            if (item != null && (int)item.Type <= 3 && ins.Bank == 3 && ins.ID == 0)
                                            {
                                                // 3[00] IfEventFlag(sbyte group, byte flagState, byte flagType, int flag)
                                                args = ins.UnpackArgs(new[]
                                                    { ArgType.SByte, ArgType.Byte, ArgType.Byte, ArgType.Int32 });
                                                // 3[04] IfPlayerHasdoesntHaveItem(sbyte group, byte itemType, int itemId, byte ownState)
                                                e2.Instructions[j] = ins = new EMEVD.Instruction(3, 4);
                                                ins.PackArgs(new List<object>
                                                    { args[0], (byte)item.Type, item.ID, args[1] });
                                                edited = true;
                                            }
                                            // This is an even more involved rewrite for Goto/End, which is needed for consistency with the first case
                                            else if (t.ItemCond != 0 && item != null && (int)item.Type <= 3 &&
                                                     ins.Bank == 1003 && (ins.ID == 1 || ins.ID == 2 || ins.ID == 101))
                                            {
                                                // TODO: This is very iffy. Should use EMEDF for this to pre-transform the event instead.
                                                if (t.ItemCond > 15)
                                                {
                                                    throw new Exception(
                                                        $"Cannot rewrite {callee} with item {item}, ran out of conds");
                                                }

                                                // 1003[1/2/101] [Skip/End/Goto]IfEventFlag(byte control, byte flagState, byte flagType, int flag)
                                                args = ins.UnpackArgs(new[]
                                                    { ArgType.Byte, ArgType.Byte, ArgType.Byte, ArgType.Int32 });
                                                // 3[04] IfPlayerHasdoesntHaveItem(sbyte group, byte itemType, int itemId, byte ownState)
                                                e2.Instructions[j] = new EMEVD.Instruction(
                                                    3, 4,
                                                    new List<object>
                                                        { (sbyte)t.ItemCond, (byte)item.Type, item.ID, args[1] });
                                                // 1000[1/2/101] [Skip/End/Goto]IfConditionGroupStateUncompiled(byte control, byte state, sbyte group)
                                                e2.Instructions.Insert(j + 1, new EMEVD.Instruction(
                                                    1000, ins.ID,
                                                    new List<object> { args[0], (byte)1, (sbyte)t.ItemCond }));
                                                edited = true;
                                                // >:(
                                                t.ItemCond++;
                                            }
                                            else if (targetFlag > 0)
                                            {
                                                args[aPos] = args[bPos] = targetFlag;
                                                ins.PackArgs(args);
                                                edited = true;
                                            }
                                        }
                                    }

                                    pre.Postprocess();
                                }

                                if (!edited)
                                    new Exception($"Couldn't apply flag edit {flag} -> {targetFlag} to {callee}");
                                if (e2 != null && commonEvents.ContainsKey(callee))
                                {
                                    game.WriteEmevds.Add("common_func");
                                }
                                else
                                {
                                    game.WriteEmevds.Add(entry.Key);
                                }
                            }
                        }
                    }

                    void addNewEvent(int id, IEnumerable<EMEVD.Instruction> instrs,
                        EMEVD.Event.RestBehaviorType rest = EMEVD.Event.RestBehaviorType.Default)
                    {
                        EMEVD.Event ev = new EMEVD.Event(id, rest);
                        // ev.Instructions.AddRange(instrs.Select(t => events.ParseAdd(t)));
                        ev.Instructions.AddRange(instrs);
                        emevd.Events.Add(ev);
                        emevd.Events[0].Instructions
                            .Add(new EMEVD.Instruction(2000, 0, new List<object> { 0, (uint)id, (uint)0 }));
                    }

                    if (entry.Key == "common")
                    {
                        List<EMEVD.Instruction> runeInstrs = new List<EMEVD.Instruction>();
                        // Basic basic structure: if get either runes, and the base flag is not set
                        int reg = 1;
                        foreach (KeyValuePair<int, (ItemKey, ItemKey)> rune in greatRunes)
                        {
                            int flag = rune.Key;
                            ItemKey a = rune.Value.Item1;
                            ItemKey b = rune.Value.Item2;
                            runeInstrs.AddRange(new List<EMEVD.Instruction>
                            {
                                // IfEventFlag(reg, OFF, TargetEventFlagType.EventFlag, flag)
                                new EMEVD.Instruction(3, 0, new List<object> { (sbyte)reg, (byte)0, (byte)0, flag }),
                                // IfPlayerHasdoesntHaveItem(-reg, a.Type, a.ID, OwnershipState.Owns = 1)
                                // IfPlayerHasdoesntHaveItem(-reg, b.Type, b.ID, OwnershipState.Owns = 1)
                                new EMEVD.Instruction(3, 4,
                                    new List<object> { (sbyte)-reg, (byte)a.Type, a.ID, (byte)1 }),
                                new EMEVD.Instruction(3, 4,
                                    new List<object> { (sbyte)-reg, (byte)b.Type, b.ID, (byte)1 }),
                                // IfConditionGroup(reg, PASS, -reg)
                                new EMEVD.Instruction(0, 0, new List<object> { (sbyte)reg, (byte)1, (sbyte)-reg }),
                                // IfConditionGroup(-10, PASS, reg)
                                new EMEVD.Instruction(0, 0, new List<object> { (sbyte)-10, (byte)1, (sbyte)reg }),
                            });
                            reg++;
                        }

                        // IfConditionGroup(MAIN, PASS, -10)
                        runeInstrs.Add(new EMEVD.Instruction(0, 0, new List<object> { (sbyte)0, (byte)1, (sbyte)-10 }));
                        // runeInstrs.Add(new EMEVD.Instruction(2003, 4, new List<object> { 997220 }));
                        reg = 1;
                        foreach (KeyValuePair<int, (ItemKey, ItemKey)> rune in greatRunes)
                        {
                            int flag = rune.Key;
                            runeInstrs.AddRange(new List<EMEVD.Instruction>
                            {
                                // SkipIfConditionGroupStateCompiled(1, OFF, reg)
                                new EMEVD.Instruction(1000, 7, new List<object> { (byte)1, (byte)0, (byte)reg }),
                                // SetEventFlag(TargetEventFlagType.EventFlag, flag, ON)
                                new EMEVD.Instruction(2003, 66, new List<object> { (byte)0, flag, (byte)1 }),
                            });
                            reg++;
                        }

                        // We could loop this, but there are weird states (like flag on but no item)
                        // so it's simpler just to make it happen a single time on reload.
                        addNewEvent(19003130, runeInstrs, EMEVD.Event.RestBehaviorType.Restart);
                    }

                    if (entry.Key == "m60_49_53_00" && opt.GetStringAsInt(StringOption.Runes_Rold, 0, 7, out int roldRunes))
                    {
                        int unlockFlag = 180 + roldRunes;
                        // Rold Medallion has been taken out of logic, so make self-contained logic to award it here.
                        // This is similar to Sekiro memory lots, which are invented from whole cloth.
                        // It precludes it from being added in hint logs easily. As an alternative, add it in data scraper.
                        ItemKey rold = ann.ItemGroups["removerold"][0];
                        LotCells roldCells = ShopToItemLot(ShopCellsForItem(rold), rold, random, false);
                        roldCells.EventFlag = roldFlag;
                        AddLot("ItemLotParam_map", roldFlag, roldCells, itemRarity, false);

                        // Just put this in Rold map, otherwise we'd want to add a map check before the radius check
                        List<EMEVD.Instruction> runeInstrs = new List<EMEVD.Instruction>
                        {
                            // EndIfEventFlag(EventEndType.End, ON, TargetEventFlagType.EventFlag, roldFlag)
                            new EMEVD.Instruction(1003, 2, new List<object> { (byte)0, (byte)1, (byte)2, roldFlag }),
                            // IfEventFlag(MAIN, ON, TargetEventFlagType.EventFlag, unlockFlag)
                            new EMEVD.Instruction(3, 0, new List<object> { (sbyte)0, (byte)1, (byte)0, unlockFlag }),
                            // IfEntityInoutsideRadiusOfEntity(OR_01, InsideOutsideState.Inside = 1, 10000, <action button entity>, 10f, 1)
                            new EMEVD.Instruction(3, 3,
                                new List<object> { (sbyte)-1, (byte)1, 10000, 1049531502, 10f, 1 }),
                            new EMEVD.Instruction(3, 3,
                                new List<object> { (sbyte)-1, (byte)1, 10000, 1049531504, 10f, 1 }),
                            // IfConditionGroup(MAIN, PASS, OR_01)
                            new EMEVD.Instruction(0, 0, new List<object> { (sbyte)0, (byte)1, (sbyte)-1 }),
                            // IfPlayerHasdoesntHaveItem(AND_01, type, id, OwnershipState.Owns = 1)
                            new EMEVD.Instruction(3, 4,
                                new List<object> { (byte)1, (byte)rold.Type, rold.ID, (byte)1 }),
                            // EndIfConditionGroupStateUncompiled(EventEndType.End, PASS, AND_01)
                            new EMEVD.Instruction(1000, 2, new List<object> { (byte)0, (byte)1, (sbyte)1 }),
                            // AwardItemLot(roldFlag)
                            new EMEVD.Instruction(2003, 4, new List<object> { roldFlag }),
                        };
                        addNewEvent(roldEventId, runeInstrs, EMEVD.Event.RestBehaviorType.Default);
                        game.WriteEmevds.Add(entry.Key);
                        // "You do not have the required medallion" (msg 20020/20021) -> "You cannot use this without more Great Runes" 20004
                        // But the original string does not appear anywhere? So this remains as-is.
                    }
                }

                // Now ESDs. AST should make this a lot simpler than the Sekiro case
                Dictionary<int, EventSpec> talkTemplates = eventConfig.ItemTalks.ToDictionary(e => e.ID, e => e);
                bool debugEsd = false;

                AST.Expr esdFunction(string name, List<int> args)
                {
                    return new AST.FunctionCall
                    {
                        Name = name,
                        Args = args.Select(a => (AST.Expr)new AST.ConstExpr { Value = a }).ToList(),
                    };
                }

                byte[] rewriteArg(byte[] bytes, Dictionary<int, string> flagEdits)
                {
                    AST.Expr expr = AST.DisassembleExpression(bytes);
                    bool modified = false;
                    expr = expr.Visit(AST.AstVisitor.Post(e =>
                    {
                        // f15 EventFlag(targetFlag), f101 GetEventFlagValue(targetFlag, bits)
                        if (e is AST.FunctionCall call && (call.Name == "f15" || call.Name == "f101") &&
                            call.Args.Count >= 1)
                        {
                            if (debugEsd)
                                Console.WriteLine($"  Check call {call} against {string.Join(", ", flagEdits)}");
                            if (call.Args[0].TryAsInt(out int flag)
                                && flagEdits.TryGetValue(flag, out string editType)
                                && getFlagEdit(editType, flag, out int targetFlag, out ItemKey item))
                            {
                                if (item != null && (int)item.Type <= 3)
                                {
                                    modified = true;
                                    // DoesPlayerHaveItem(type, id)
                                    if (debugEsd)
                                        Console.WriteLine($"  - Rewriting flag {flag} to item {game.Name(item)}");
                                    return esdFunction("f16", new List<int> { (int)item.Type, item.ID });
                                }
                                else if (targetFlag > 0)
                                {
                                    modified = true;
                                    // EventFlag(targetFlag)
                                    if (debugEsd)
                                        Console.WriteLine($"  - Rewriting flag {flag} to new flag {targetFlag}");
                                    return esdFunction("f15", new List<int> { targetFlag });
                                }
                            }
                        }

                        return null;
                    }));
                    return modified ? AST.AssembleExpression(expr) : null;
                }

                List<ESD.Condition> GetConditions(List<ESD.Condition> condList) => Enumerable
                    .Concat(condList, condList.SelectMany(cond => GetConditions(cond.Subconditions))).ToList();

                foreach (KeyValuePair<string, Dictionary<string, ESD>> entry in game.Talk)
                {
                    bool modified = false;
                    foreach (KeyValuePair<string, ESD> esdEntry in entry.Value)
                    {
                        ESD esd = esdEntry.Value;
                        int esdId = int.Parse(esdEntry.Key.Substring(1));
                        if (!talkTemplates.TryGetValue(esdId, out EventSpec spec) || spec.ItemTemplate == null)
                            continue;

                        // We have some edits to do
                        foreach (KeyValuePair<long, Dictionary<long, ESD.State>> machine in esd.StateGroups)
                        {
                            int machineId = (int)machine.Key;
                            string machineName = AST.FormatMachine(machineId);
                            List<ItemTemplate> machineTemplates =
                                spec.ItemTemplate.Where(t => t.Machine == machineName).ToList();
                            if (machineTemplates.Count == 0) continue;

                            Dictionary<int, string> flagEdits = new Dictionary<int, string>();
                            foreach (ItemTemplate t in machineTemplates)
                            {
                                if (debugEsd)
                                    Console.WriteLine(
                                        $"{entry.Key}: Examining {esdId} machine {t.Machine} type {t.Type}");
                                if (t.Type == "default" || t.Type == "fixeditem") continue;
                                if ((t.Type != "item" && t.Type != "loc") || t.EventFlag == null)
                                {
                                    throw new Exception($"Internal error: unknown ESD edit in {esdId} {machineName}");
                                }

                                foreach (int flag in t.EventFlag.Split(' ').Select(int.Parse))
                                {
                                    flagEdits[flag] = t.Type;
                                }
                            }

                            if (flagEdits.Count == 0) continue;

                            foreach (KeyValuePair<long, ESD.State> stateEntry in machine.Value)
                            {
                                int stateId = (int)stateEntry.Key;
                                ESD.State state = stateEntry.Value;
                                List<ESD.Condition> conds = GetConditions(state.Conditions);
                                foreach (ESD.CommandCall cmd in new[]
                                         {
                                             state.EntryCommands, state.WhileCommands, state.ExitCommands,
                                             conds.SelectMany(c => c.PassCommands)
                                         }.SelectMany(c => c))
                                {
                                    for (int i = 0; i < cmd.Arguments.Count; i++)
                                    {
                                        byte[] arg2 = rewriteArg(cmd.Arguments[i], flagEdits);
                                        if (arg2 != null)
                                        {
                                            cmd.Arguments[i] = arg2;
                                            modified = true;
                                        }
                                    }
                                }

                                foreach (ESD.Condition cond in conds)
                                {
                                    byte[] eval2 = rewriteArg(cond.Evaluator, flagEdits);
                                    if (eval2 != null)
                                    {
                                        cond.Evaluator = eval2;
                                        modified = true;
                                    }
                                }
                            }
                        }
                    }

                    if (modified)
                    {
                        if (debugEsd) Console.WriteLine($"Modified flag {entry.Key}");
                        game.WriteESDs.Add(entry.Key);
                    }
                }

                // Finally, a pass for merchant Bell Bearings
                // This is mainly only relevant during item randomizer, as otherwise merchant contents are known.
                int merchantMsg = 28000070;
                game.WriteFMGs = true;
                messages.SetFMGEntry(game, FMGCategory.Menu, "EventTextForTalk", merchantMsg, receiveBellBearing);
                // Mapping from talk id to (item lot, item lot flag)
                Dictionary<int, (int, int)> talkIds = new Dictionary<int, (int, int)>();
                foreach (KeyValuePair<LocationScope, List<SlotKey>> entry in data.Locations)
                {
                    LocationScope locScope = entry.Key;
                    if (!ann.Slots.TryGetValue(locScope, out AnnotationData.SlotAnnotation slot)) continue;
                    if (!slot.TagList.Contains("merchantgift")) continue;
                    foreach (SlotKey itemLocKey in entry.Value)
                    {
                        ItemLocation location = data.Location(itemLocKey);
                        if (location.Scope.Type != ScopeType.EVENT) continue;
                        int eventFlag = location.Scope.EventID;
                        if (eventFlag <= 0) continue;
                        foreach (LocationKey locKey in location.Keys)
                        {
                            if (locKey.Type != LocationType.LOT || locKey.Subtype != "map") continue;
                            int lotId = locKey.BaseID;
                            foreach (EntityId entityId in locKey.Entities)
                            {
                                if (entityId.TalkID > 0)
                                {
                                    talkIds[entityId.TalkID] = (lotId, eventFlag);
                                    if (entityId.NameID > 0)
                                    {
                                        merchantGiftFlags[entityId.NameID] = eventFlag;
                                    }
                                }
                            }
                        }
                    }
                }

                foreach (KeyValuePair<string, Dictionary<string, ESD>> entry in game.Talk)
                {
                    bool modified = false;
                    foreach (KeyValuePair<string, ESD> esdEntry in entry.Value)
                    {
                        ESD esd = esdEntry.Value;
                        int esdId = int.Parse(esdEntry.Key.Substring(1));
                        if (!talkIds.TryGetValue(esdId, out var talkInfo)) continue;
                        (int lotId, int eventFlag) = talkInfo;
                        List<long> purchaseMachines = ESDEdits.FindMachinesWithTalkData(esd, 20000010);
                        ESDEdits.CustomTalkData merchantData = new ESDEdits.CustomTalkData
                        {
                            Msg = merchantMsg,
                            ConsistentID = 68,
                            Condition = new AST.BinaryExpr
                                { Op = "==", Lhs = AST.MakeFunction("f15", eventFlag), Rhs = AST.MakeVal(0) },
                            LeaveMsg = 20000009,
                        };
                        foreach (long machineId in purchaseMachines)
                        {
                            Dictionary<long, ESD.State> machine = esd.StateGroups[machineId];
                            long resultStateId = -1;
                            try
                            {
                                ESDEdits.ModifyCustomTalkEntry(machine, merchantData, true, false, out resultStateId);
                            }
                            catch (InvalidOperationException)
                            {
                            }

                            if (resultStateId < 0) continue;
                            ESD.State resultState = machine[resultStateId];
                            // c1_104 AwardItemLot
                            resultState.EntryCommands.Add(AST.MakeCommand(1, 104, lotId));
                            // not f25 IsMenuOpen(63) and f102 GetCurrentStateElapsedFrames() > 1
                            resultState.Conditions[0].Evaluator = AST.AssembleExpression(new AST.BinaryExpr
                            {
                                Op = "&&",
                                Lhs = new AST.BinaryExpr
                                    { Op = "==", Lhs = AST.MakeFunction("f25", 63), Rhs = AST.MakeVal(0) },
                                Rhs = new AST.BinaryExpr
                                    { Op = ">", Lhs = AST.MakeFunction("f102"), Rhs = AST.MakeVal(1) },
                            });
                            modified = true;
                        }
                    }

                    if (modified)
                    {
                        if (debugEsd) Console.WriteLine($"Modified merchant {entry.Key}");
                        game.WriteESDs.Add(entry.Key);
                    }
                }

                // This could also be applied to enemy randomizer, but it affects logic
                if (opt[BooleanOption.AllCraft])
                {
                    foreach (PARAM.Row row in game.Params["ShopLineupParam_Recipe"].Rows)
                    {
                        row["eventFlag_forRelease"].Value = (uint)0;
                    }
                }
                // End Elden Ring edits
            }

            return new Result
            {
                ItemEventFlags = itemEventFlags, SlotEventFlags = slotEventFlags, MerchantGiftFlags = merchantGiftFlags
            };
        }

        private static readonly Regex phraseRe = new Regex(@"\s*;\s*");

        private PriceCategory GetSekiroPriceCategory(ItemKey key)
        {
            return data.Data[key].Unique && !game.Name(key).Contains("Jizo")
                ? PriceCategory.UNIQUE_GOOD
                : PriceCategory.REGULAR_GOOD;
        }

        private PriceCategory GetPriceCategory(ItemKey key)
        {
            // Effectively don't use transpose category - instead use rules for base category.
            // if (isTranspose) return PriceCategory.TRANSPOSE;
            if (game.EldenRing)
            {
                if (key.Type != ItemType.GOOD)
                {
                    if (key.Type == ItemType.WEAPON && key.ID >= 50000000 && key.ID < 60000000)
                    {
                        return PriceCategory.ARROWS;
                    }

                    if (key.Type == ItemType.CUSTOM)
                    {
                        return PriceCategory.WEAPON;
                    }

                    return (PriceCategory)key.Type;
                }

                if (key.ID >= 4000 && key.ID < 8000) return PriceCategory.SPELLS;
                if (key.ID >= 10100 & key.ID < 11000) return PriceCategory.UPGRADE;
            }
            else
            {
                if (key.Type != ItemType.GOOD)
                {
                    if (key.Type == ItemType.WEAPON && key.ID >= 400000 && key.ID < 500000)
                    {
                        return PriceCategory.ARROWS;
                    }

                    return (PriceCategory)key.Type;
                }

                if (key.ID >= 1200000) return PriceCategory.SPELLS;
                if (key.ID >= 1000 & key.ID <= 1030) return PriceCategory.UPGRADE;
            }

            if (!isItemFiniteCache.ContainsKey(key))
            {
                // If infinite shop, item is infinite
                // If finite shop, item is finite
                // If not in any shops, use lot finiteness
                bool finiteShop = false, infiniteShop = false, infiniteLot = false;
                foreach (ItemLocation loc in data.Data[key].Locations.Values)
                {
                    if (loc.Scope.Type == ScopeType.SHOP_INFINITE)
                    {
                        infiniteShop = true;
                    }
                    else if (loc.Scope.Type == ScopeType.MODEL)
                    {
                        infiniteLot = true;
                    }
                    else if (loc.Scope.Type == ScopeType.MATERIAL || (loc.Scope.Type == ScopeType.EVENT &&
                                                                      loc.Keys.Any(k => k.Type == LocationType.SHOP)))
                    {
                        finiteShop = true;
                    }
                }

                bool isInfinite = infiniteShop || (!finiteShop && infiniteLot);
                isItemFiniteCache[key] = !isInfinite;
            }

            return isItemFiniteCache[key] ? PriceCategory.FINITE_GOOD : PriceCategory.INFINITE_GOOD;
        }

        // Use simple DS1 item randomizer type system for the moment
        private int Price(Permutation permutation, RandomSilo siloType, ItemKey item, bool isTranspose, Random random)
        {
            PriceCategory cat = GetPriceCategory(item);
            ItemKey rowKey = game.FromCustomWeapon(item);
            PARAM.Row row = game.Item(rowKey);
            // Upgrade materials roughly same. Unique ones on sale because of how many are moved to shops usually.
            if (cat == PriceCategory.UPGRADE)
            {
                if (game.EldenRing && upgradePrices.TryGetValue(item, out int upgradePrice))
                {
                    return siloType == RandomSilo.FINITE ? upgradePrice * 8 / 10 : upgradePrice;
                }
            }

            int sellPrice = 0;
            if (rowKey.Type != ItemType.CUSTOM)
            {
                if (row == null)
                {
                    throw new Exception(
                        $"{item} was randomized but it doesn't exist in params, likely due to a merged mod");
                }
                else
                {
                    sellPrice = (int)row["sellValue"].Value;
                }
            }

            // If it's a soul, make it cost a more than the soul cost.
            if (cat == PriceCategory.FINITE_GOOD && sellPrice >= 2000)
            {
                return sellPrice + 1000;
            }

            int price;
            if (permutation.ItemLateness.ContainsKey(item) && item.Type == ItemType.GOOD)
            {
                // From 500 (with range) to 10k (without range) based on game lateness
                double basePrice = 500 + permutation.ItemLateness[item] * (10000 / 1.5 - 500);
                // On sale if not a key item
                if (!permutation.KeyItems.Contains(item)) basePrice /= 2;
                // 50% in either direction
                basePrice = basePrice * (random.NextDouble() + 0.5);
                // Round to next 100 (if less than 2000), 500 or 1000
                List<int> rounds = new List<int> { 500, 1000 };
                if (basePrice < 2000) rounds.Add(100);
                int round = Choice(random, rounds);
                price = (((int)basePrice / round) + 1) * round;
            }
            else
            {
                price = Choice(random, prices[cat]);
                // Here we could also hike up the price for especially good items
            }

            if (price < sellPrice)
            {
                price = sellPrice;
            }

            if (isTranspose && random.NextDouble() < 0.4)
            {
                // TODO: Elden Ring boss shops?
                price = 0;
            }

            return price;
        }

        private void AddLot(string paramName, int baseLot, LotCells cells, Dictionary<int, byte> itemRarity,
            bool overwrite)
        {
            PARAM itemLots = game.Param(paramName);
            int targetLot = baseLot;
            PARAM.Row row = null;
            if (overwrite)
            {
                row = itemLots[targetLot];
            }

            if (row == null)
            {
                while (!overwrite && itemLots[targetLot] != null)
                {
                    targetLot++;
                }

                row = game.AddRow(paramName, targetLot);
            }

            // Console.WriteLine($"Setting lot {baseLot}: {string.Join(", ", cells.Cells)}");
            foreach (KeyValuePair<string, object> cell in cells.Cells)
            {
                if (cell.Key == "LotItemRarity")
                {
                    continue;
                }

                row[cell.Key].Value = cell.Value;
            }

            if (itemRarity.ContainsKey(baseLot))
            {
                row["LotItemRarity"].Value = itemRarity[baseLot];
            }
        }

        private void SetShop(LocationKey target, ShopCells cells)
        {
            PARAM.Row row = game.Params[target.ParamName][target.ID];
            foreach (KeyValuePair<string, object> cell in cells.Cells)
            {
                if (cell.Key == "qwcID" || cell.Key == "eventFlag_forRelease" || cell.Key == "mtrlId"
                    || cell.Key == "costType") continue;
                row[cell.Key].Value = cell.Value;
            }

            if (game.EldenRing)
            {
                if (!cells.Cells.ContainsKey("nameMsgId"))
                {
                    row["nameMsgId"].Value = -1;
                }

                if (!cells.Cells.ContainsKey("iconId"))
                {
                    row["iconId"].Value = -1;
                }
            }
        }

        public Dictionary<int, float> GetDropChances(ItemKey key, ItemLocation itemLoc)
        {
            Dictionary<int, float> chances = new Dictionary<int, float>();
            foreach (LocationKey loc in itemLoc.Keys.Where(k => k.Type == LocationType.LOT))
            {
                float chance = chances.TryGetValue(loc.Quantity, out float c) ? c : 1;
                chances[loc.Quantity] = Math.Min(chance, loc.Chance);
            }

            return chances;
        }

        private LotCells ProcessModelLot(LotCells lotCells, ItemKey key, Dictionary<int, float> sourceChances,
            bool print)
        {
            lotCells = lotCells.DeepCopy();
            // Clear existing items out
            for (int i = 1; i <= 8; i++)
            {
                lotCells[i] = null;
                lotCells.SetPoints(i, 0);
                lotCells.SetQuantity(i, 0);
            }

            // Disable resource drops in Sekiro as well
            SetItemLotChances(lotCells, key, sourceChances, print);
            return lotCells;
        }

        private ShopCells ShopCellsForItem(ItemKey item)
        {
            ShopCells cells = new ShopCells
            {
                Game = game,
                Cells = new Dictionary<string, object>(),
            };
            cells.Item = game.FromCustomWeapon(item);
            cells.Quantity = 1;
            return cells;
        }

        private void SetItemLotChances(LotCells cells, ItemKey key, Dictionary<int, float> quants, bool print)
        {
            int drop = 0;
            int i = 1;
            foreach (KeyValuePair<int, float> quant in quants)
            {
                cells[i] = key;
                cells.SetQuantity(i, quant.Key);
                int points = (int)Math.Round(1000 * quant.Value);
                cells.SetPoints(i, points);
                if (print) Console.WriteLine($"  Drop chance for {quant.Key}: {points / 10.0}%");
                drop += points;
                i++;
                if (i >= 8) break;
            }

            cells[i] = null;
            cells.SetPoints(i, (short)Math.Max(0, 1000 - drop));
        }

        private LotCells ShopToItemLot(ShopCells shopCells, ItemKey key, Random random, bool print)
        {
            LotCells lotCells = new LotCells { Game = game, Cells = new Dictionary<string, object>() };
            // Disable resource drop flag in Sekiro

            lotCells[1] = shopCells.Item;
            lotCells.SetQuantity(1, 0);
            int quantity = shopCells.Quantity;
            if (quantity > 0)
            {
                // Ring of sacrifice multi-drops do not work in DS3
                // ...make this shop-only instead?
                if (key.Equals(new ItemKey(ItemType.RING, 20210)) && quantity > 1)
                {
                    quantity = 1;
                }

                lotCells.SetQuantity(1, quantity);
                lotCells.SetPoints(1, 100);
            }
            else
            {
                PriceCategory cat = GetPriceCategory(key);
                Dictionary<int, float> chances;
                if (dropChances.TryGetValue(cat, out List<Dictionary<int, float>> allChances))
                {
                    chances = Choice(random, allChances);
                }
                else
                {
                    chances = DEFAULT_CHANCES;
                }

                SetItemLotChances(lotCells, key, chances, print);
            }

            return lotCells;
        }

        private ShopCells ItemLotToShop(LotCells lotCells, ItemKey itemKey)
        {
            ShopCells shopCells = new ShopCells { Game = game, Cells = new Dictionary<string, object>() };
            // For an item like this, assume QWC id stays the same
            ItemKey lotKey = null;
            int totalPoints = 0;
            for (int i = 1; i <= 8; i++)
            {
                totalPoints += lotCells.GetPoints(i);
            }

            for (int i = 1; i <= 8; i++)
            {
                lotKey = lotCells[i];
                if (!itemKey.Equals(lotKey))
                {
                    lotKey = null;
                    continue;
                }

                if (game.EldenRing && lotKey.Type == ItemType.CUSTOM)
                {
                    // If this still returns a custom id, it'll just be invisible
                    shopCells.Item = game.FromCustomWeapon(lotKey);
                }
                else
                {
                    shopCells.Item = lotKey;
                }

                int basePoints = lotCells.GetPoints(i);
                if (basePoints == totalPoints)
                {
                    // TODO: If no event id or material id, this won't do much. But that is intended?
                    shopCells.Quantity = lotCells.GetQuantity(i);
                }
                else
                {
                    shopCells.Quantity = -1;
                }

                break;
            }

            if (lotKey == null)
            {
                throw new Exception(
                    $"Internal error: Invalid source location for {itemKey} from {string.Join(", ", lotCells.Cells.Select(e => e.Key + " = " + e.Value))}");
            }

            MakeSellable(lotKey);
            return shopCells;
        }

        private void MakeSellable(ItemKey key)
        {
            // TODO: Anything for Elden Ring?
        }

        public abstract class ItemRow<T> where T : ItemRow<T>, new()
        {
            public GameData Game { get; set; }
            public Dictionary<string, object> Cells { get; set; }

            public T DeepCopy()
            {
                return new T { Game = Game, Cells = new Dictionary<string, object>(Cells) };
            }
        }

        public class ShopCells : ItemRow<ShopCells>
        {
            public ItemKey Item
            {
                get
                {
                    return new ItemKey((ItemType)(byte)Cells["equipType"],
                        (int)Cells[Game.EldenRing ? "equipId" : "EquipId"]);
                }
                set
                {
                    Cells[Game.EldenRing ? "equipId" : "EquipId"] = value.ID;
                    Cells["equipType"] = (byte)value.Type;
                }
            }

            public int EventFlag
            {
                get => Game.EldenRing ? (int)(uint)Cells["eventFlag_forStock"] : (int)Cells["EventFlag"];
                set
                {
                    if (Game.EldenRing)
                    {
                        Cells["eventFlag_forStock"] = value > 0 ? (uint)value : 0u;
                    }
                    else
                    {
                        Cells["EventFlag"] = value > 0 ? value : -1;
                    }
                }
            }

            public int Quantity
            {
                get => (short)Cells["sellQuantity"];
                set { Cells["sellQuantity"] = (short)value; }
            }

            public int Value
            {
                get => (int)Cells["value"];
                set { Cells["value"] = value; }
            }
        }

        // TODO: Ugh we should probably use subtyping for this
        public class LotCells : ItemRow<LotCells>
        {
            public int EventFlag
            {
                get => Game.EldenRing ? (int)(uint)Cells["getItemFlagId"] : (int)Cells["getItemFlagId"];
                set
                {
                    if (Game.EldenRing)
                    {
                        Cells["getItemFlagId"] = value > 0 ? (uint)value : 0u;
                    }
                    else
                    {
                        Cells["getItemFlagId"] = value > 0 ? value : -1;
                    }
                }
            }

            public ItemKey this[int i]
            {
                get
                {
                    if (Game.EldenRing)
                    {
                        int id = (int)Cells[$"lotItemId0{i}"];
                        if (id == 0) return null;
                        return new ItemKey(Game.LotItemTypes[(uint)(int)Cells[$"lotItemCategory0{i}"]], id);
                    }
                    else
                    {
                        int id = (int)Cells[$"ItemLotId{i}"];
                        if (id == 0) return null;
                        return new ItemKey(Game.LotItemTypes[(uint)Cells[$"LotItemCategory0{i}"]], id);
                    }
                }
                set
                {
                    if (Game.EldenRing)
                    {
                        Cells[$"lotItemId0{i}"] = value == null ? 0 : value.ID;
                        Cells[$"lotItemCategory0{i}"] = value == null ? 0 : (int)Game.LotValues[value.Type];
                        // TODO: Do this for other games as well? Does this work?
                        // Normally this is not set for non-random drops, but it shouldn't be incorrect to add it either.
                        Cells[$"enableLuck0{i}"] = value == null ? (ushort)0 : (ushort)1;
                    }
                    else
                    {
                        Cells[$"ItemLotId{i}"] = value == null ? 0 : value.ID;
                        Cells[$"LotItemCategory0{i}"] = value == null ? 0xFFFFFFFFu : Game.LotValues[value.Type];
                    }
                }
            }

            public int GetPoints(int i)
            {
                if (Game.EldenRing)
                {
                    return (ushort)Cells[$"lotItemBasePoint0{i}"];
                }
                else
                {
                    return (short)Cells[$"LotItemBasePoint0{i}"];
                }
            }

            public void SetPoints(int i, int points)
            {
                if (Game.EldenRing)
                {
                    Cells[$"lotItemBasePoint0{i}"] = (ushort)points;
                }
                else
                {
                    Cells[$"LotItemBasePoint0{i}"] = (short)points;
                }
            }

            public int GetQuantity(int i) => (byte)Cells[$"lotItemNum0{i}"];

            public void SetQuantity(int i, int quantity) => Cells[$"lotItemNum0{i}"] = (byte)quantity;
        }

        private class ItemSource
        {
            public readonly ItemLocation Loc;

            // Maybe use a Row object? But it might be nice to edit shops in place...
            public readonly object Row;

            public ItemSource(ItemLocation loc, object row)
            {
                this.Loc = loc;
                this.Row = row;
            }
        }
    }
}