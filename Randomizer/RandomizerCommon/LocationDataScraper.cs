﻿using SoulsFormats;
using System;
using System.Collections.Generic;
using System.Linq;
using static RandomizerCommon.LocationData;
using static RandomizerCommon.LocationData.LocationKey;
using static RandomizerCommon.LocationData.ItemScope;
using static RandomizerCommon.Util;

namespace RandomizerCommon
{
    public class LocationDataScraper
    {
        private static SortedDictionary<int, string> shopSplits = new SortedDictionary<int, string>
        {
            { 1, null },
            { 30000, "Ludleth" },
            { 40000, "Yuria" },
            { 50000, "Yoel/Yuria" },
            { 110000, "Shrine Handmaid" },
            { 119900, "Untended Graves Handmaid" },
            { 120000, "Greirat" },
            { 130100, "Orbeck spells" },
            { 140000, "Cornyx" },
            { 140100, "Cornyx spells" },
            { 150100, "Karla spells" },
            { 160000, "Irina" },
            { 160100, "Irina spells" },
            { 200000, "Patches" },
            { 760000, "Stone-humped Hag" },
            { 900000, null }
        };

        private bool logUnused;

        public LocationDataScraper(bool logUnused = false)
        {
            this.logUnused = logUnused;
        }

        private static int GetShopType(int shopID)
        {
            int shopType = 1;
            foreach (KeyValuePair<int, string> entry in shopSplits)
            {
                if (entry.Key > shopID)
                {
                    break;
                }
                shopType = entry.Key;
            }
            return shopType;
        }


        // Non-respawning entities. This means their drops are somewhat unique and shouldn't be considered farmable.
        // However, the drops are not always guaranteed - most of those are covered by entityItemLots.
        private static readonly HashSet<int> nonRespawningEntities = new HashSet<int>
        {
            3000661,
            3100900,
            3100910,
            3500292,
            3500300,
            3500301,
            3700241,
            3700242,
            3700355,
            3700366,
            3700367,
            3700368,
            3900370,
            3900371,
            3900372,
            3900380
        };
        // Item event flags with equivalent items, of which it is only possible to get one
        private static readonly Dictionary<int, int> equivalentEvents = new Dictionary<int, int>()
        {
            { 74001540, 50006042 },
            { 74001548, 50006042 },
            { 74001556, 50006042 },
            { 74001564, 50006042 },
            { 74501500, 50006624 },
            { 74501510, 50006624 },
            { 74501520, 50006624 },
            { 74501530, 50006624 },
            { 73501010, 50006217 },
            { 73501020, 50006217 },
            { 73501030, 50006217 },
            { 73501040, 50006217 },
            { 73501050, 50006218 },
            { 74001920, 50006110 },
            { 73501080, 50006202 },
            { 73101720, 73901200 },
            { 73101730, 73901210 },
            { 73101740, 73901220 },
            { 73101750, 73901230 },
            { 73101710, 73901190 },
            { 50006621, 55000450 },
            { 50006216, 50006218 },
        };
        // QWC ids which remove items from Handmaid's shop, rather than adding ones
        private static readonly HashSet<int> restrictiveQwcs = new HashSet<int>()
        {
            70000125,
            70000126,
            70000127,
            70000128,
            70000129,
            70000130,
            70000131,
            70000132,
            70000133,
            70000153,
            70000178,
        };
        // Entities which have one-time item drops through scripting, event entity ID to item lot ID.
        // Multiple entities can map to the same lot if any of them would grant the item.
        // If a lot requires a set of entities in some area to be killed, only one is listed here.
        private static readonly Dictionary<int, int> entityItemLots = new Dictionary<int, int>()
        {
            // Bosses. Event 970
            { 3000800, 2000 },
            { 3000899, 2010 },
            { 3000830, 2020 },
            { 3010800, 2030 },
            { 3410830, 2040 },
            { 3100800, 2060 },
            { 3200800, 2070 },
            { 3200850, 2080 },
            { 3300850, 2090 },
            { 3300801, 2100 },
            { 3500800, 2110 },
            { 3700850, 2120 },
            { 3700800, 2130 },
            { 3800800, 2140 },
            { 3800830, 2150 },
            { 3900800, 2170 },
            { 4000800, 2180 },
            { 4000830, 2190 },
            { 4100800, 2200 },
            { 4500800, 2300 },
            { 4500860, 2310 },
            { 5000800, 2330 },
            { 5100800, 2340 },
            { 5100850, 2350 },
            { 5110800, 2360 },
            // Entity does not respawn. Event 20005341
            { 3000630, 21500000 },
            { 3010610, 13103000 },
            { 3010310, 21504000 },
            { 3010311, 21504010 },
            { 3100831, 30600000 },
            { 3100860, 13102000 },
            { 3100610, 21501000 },
            { 3100611, 21501010 },
            { 3200300, 31412000 },
            { 3200259, 21509000 },
            { 3300384, 31000000 },
            { 3300385, 21502030 },
            { 3300386, 21502040 },
            { 3300387, 21502050 },
            { 3300388, 21502060 },
            { 3300389, 21502000 },
            { 3300390, 21502010 },
            { 3300391, 21502020 },
            { 3300560, 52000000 },
            { 3300180, 57800 },
            { 3300182, 57800 },
            { 3300184, 57900 },
            { 3410200, 13210000 },
            { 3410210, 13101000 },
            { 3410370, 21505000 },
            { 3410371, 21505010 },
            { 3410372, 21505020 },
            { 3410373, 21505030 },
            { 3410374, 21505040 },
            { 3410375, 21505050 },
            { 3410376, 21505060 },
            { 3410377, 21505070 },
            { 3500370, 31001000 },
            { 3500372, 21503000 },
            { 3500373, 21503010 },
            { 3500669, 21800010 },
            { 3500194, 58700 },
            { 3700193, 58500 },
            { 3700194, 57900 },
            { 3700240, 22500000 },
            { 3700300, 21507000 },
            { 3700301, 21507010 },
            { 3700302, 21507020 },
            { 3700303, 21507030 },
            { 3700304, 21507040 },
            { 3800500, 30601000 },
            { 3800499, 22000000 },
            { 3800552, 21506020 },
            { 3800553, 21506030 },
            { 3800554, 21506040 },
            { 3800555, 21506000 },
            { 3800556, 21506010 },
            { 3800196, 58400 },
            { 3900192, 58600 },
            { 3900340, 21508000 },
            { 3900341, 21508010 },
            { 3900342, 21508020 },
            { 3900343, 21508030 },
            { 3900373, 20400020 },
            { 4000380, 31002000 },
            { 4000381, 31004000 },
            { 4000382, 31004000 },
            { 4000390, 21509500 },
            { 4500680, 21509600 },
            { 4500682, 21509620 },
            { 4500684, 21509640 },
            { 4500685, 21509650 },
            { 4500687, 21509670 },
            { 4500688, 21509680 },
            { 4500689, 21509690 },
            { 5100290, 21509800 },
            { 5100291, 21509810 },
            // Unused crystal lizards?
            // { 5100292, 21509820 },
            // { 5100293, 21509830 },
            // { 5100295, 21509850 },
            { 5100294, 21509840 },
            { 5100296, 21509860 },
            { 5100170, 59600 },
            { 5100172, 59700 },
            { 5100174, 59800 },
            // Entity respawns, but only drops once. Event 20005350
            { 3000238, 12800420 },
            { 3000352, 11901120 },
            { 3100570, 22800000 },
            { 3200291, 57400 },
            { 3200297, 57500 },
            { 3300200, 22700020 },
            { 3300494, 31200110 },
            { 3300495, 31200210 },
            { 3300510, 22702010 },
            { 3500586, 52230310 },
            { 3700350, 12303010 },
            { 3900259, 20601010 },
            // Entity respawns, but only drops once. Event 20005351
            { 5100300, 62800110 },
            { 5100310, 62800010 },
            { 5100320, 62800210 },
            { 5100240, 62600230 },
            { 5110240, 62600240 },
            // Treasure spawns after pot holding corpse breaks. Event 20005521
            { 3001251, 3000170 },
            // Treasure with no visible attached entity. Event 20005525
            { 3001260, 3000650 },
            { 3101290, 4200 },
            { 3101291, 3100630 },
            // { 3101292, 3100660 },  // Note: This one also has a treasure in the map itself... fixed in the area event script
            { 3201480, 3200300 },
            { 3201481, 3200310 },
            { 3301320, 3300950 },
            { 3301321, 3300960 },
            { 3301322, 3300970 },
            { 3301323, 3300980 },
            { 3501540, 3500850 },
            { 3501541, 3500860 },
            { 3501542, 3500870 },
            { 3501543, 3500880 },
            { 3501544, 3500890 },
            { 3701590, 3700840 },
            { 4001728, 4000300 },
            { 4001222, 4000340 },
            // Armor which spawns after killing another entity in a different area. Event 20005526
            { 3001900, 3000950 },
            { 3101700, 60830 },
            { 4001221, 4000330 },
            // Treasure with no visible attached entity. Event 20005527
            { 5101680, 5100670 },
            { 5101684, 5100900 },
            { 5101685, 5100910 },
            { 5101686, 5100920 },
            // Treasure available at different points in NPC quests. Often shows up in the NPC's final location. Event 20006030
            { 3101715, 62510 },
            { 3501715, 60920 },
            { 3701701, 50600 },
            { 3701722, 53000 },
            { 3901706, 62140 },
            { 4001727, 61610 },
            { 4001750, 60410 },
            { 4001760, 60730 },
            { 4001780, 60810 },
            { 4501711, 55500 },
            { 4501716, 55400 },
            { 5001700, 66200 },
            { 5101705, 66230 },
            // Entity does not respawn. Event 13000380 in High Wall
            { 3000660, 60940 },
            // Item awarded when using Path of the Dragon. Event 13205910 in Archdragon
            // There is no attached visible entity, so grab the closest enemy
            { 3200262, 3200900 },
            { 3200235, 3200910 },
            // Entity does not respawn. Event 13500276 in Cathedral
            { 3500668, 21800110 },
            { 4000700, 60200 },
            // Patches' conditional drop of Catarina Set. Event 20006032
            { 3500721, 52020 },
            { 3500720, 52020 },
            { 4000790, 52020 },
            // Custom unique drops from event scripts in specific maps
            { 3000850, 31410000 },
            { 3000705, 62320 },
            { 3010835, 31411000 },
            { 3010836, 31411100 },
            { 3100741, 4210 },
            { 3300720, 60710 },
            { 3410500, 12902200 },
            { 3700725, 60930 },
            { 3700241, 22501010 },
            { 3700706, 61930 },
            { 3901250, 3900900 },
            { 4500176, 59200 },
            { 4500802, 4700 },
            { 4500701, 55200 },
        };
        // In truth, the entity id/item lot mapping is not 1:1, so this hack is needed.
        private static readonly Dictionary<int, List<int>> additionalEntityItemLots = new Dictionary<int, List<int>>
        {
            {3701701, new List<int>{ 50610 } },
        };

        // Items given in the talk system. These can have all sorts of conditions attached.
        // A few of these can also drop on the NPC's death, usually with the same event flag.
        // TODO: Use ESD parsing system from SekiroLocationDataScraper
        private static readonly List<int> talkLots = new List<int>()
        {
            4207,
            4217,
            4220,
            4226,
            4230,
            4237,
            4240,
            4250,
            4260,
            4267,
            4270,
            60300,
            // 60310, Twin Princes Greatsword - no event id, and at most one per randomizer playthrough anyway
            60400,
            60600,
            60700,
            60703,
            60610,
            60630,
            60720,
            60805,
            60900,
            60910,
            61000,
            61200,
            61300,
            61310,
            61400,
            61900,
            62000,
            62010,
            62100,
            62103,
            62105,
            62120,
            62130,
            62300,
            62310,
            62500,
            62600,
            63100,
            63110,
            65400,
            65500,
            66210,
            66220,
            66300,
            66310,
        };
        private static readonly List<int> crowLots = new List<int>()
        {
            // Could also model items, but not worth the effort here
            4300,
            4301,
            4302,
            4303,
            4304,
            4305,
            4306,
            4307,
            4308,
            4309,
            4310,
            4311,
            4320,
            4321,
            4322,
            4323,
            4324,
            4325,
            4326,
            4327,
            4328,
            4329,
            4330,
        };
    }
}
