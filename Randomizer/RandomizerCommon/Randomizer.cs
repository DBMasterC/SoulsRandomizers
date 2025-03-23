using System;
using System.IO;
using System.Linq;
using SoulsIds;
using YamlDotNet.Serialization;
using static RandomizerCommon.Messages;
using static SoulsIds.GameSpec;
using System.Collections.Generic;
using RefactorCommon;

namespace RandomizerCommon
{
    public class Randomizer
    {
        [Localize] private static readonly Text loadPhase = new Text("Loading game data", "Randomizer_loadPhase");
        [Localize] private static readonly Text enemyPhase = new Text("Randomizing enemies", "Randomizer_enemyPhase");
        [Localize] private static readonly Text itemPhase = new Text("Randomizing items", "Randomizer_itemPhase");
        [Localize] private static readonly Text editPhase = new Text("Editing game files", "Randomizer_editPhase");
        [Localize] private static readonly Text savePhase = new Text("Writing game files", "Randomizer_savePhase");

        [Localize] private static readonly Text saveMapPhase =
            new Text("Writing map data: {0}%", "Randomizer_saveMapPhase");

        [Localize] private static readonly Text restartMsg = new Text(
            "Error: Mismatch between regulation.bin and other randomizer files.\nMake sure all randomizer files are present, the game has been restarted\nafter randomization, and the game and regulation.bin versions are compatible.",
            "GameMenu_restart");

        [Localize] private static readonly Text mergeMissingError =
            new Text("Error merging mods: directory {0} not found", "Randomizer_mergeMissingError");

        [Localize] private static readonly Text mergeWrongDirError =
            new Text("Error merging mods: already running from {0} directory", "Randomizer_mergeWrongDirError");

        public static readonly string EldenVersion = "v0.8";

        public void Randomize(
            RandomizerOptions opt,
            Action<string> notify = null,
            string outPath = null,
            Preset preset = null,
            Messages messages = null,
            bool encrypted = true,
            string gameExe = null,
            MergedMods modDirs = null)
        {
            messages = messages ?? new Messages(null);
            string distDir = "diste";
            if (!Directory.Exists(distDir))
            {
                // From Release/Debug dirs
                distDir = $@"..\..\..\{distDir}";
                opt[BooleanOption.DryRun] = true;
            }

            if (!Directory.Exists(distDir))
            {
                throw new Exception("Missing data directory");
            }

            if (outPath == null)
            {
                outPath = opt[BooleanOption.Uxm]
                    ? Path.GetDirectoryName(gameExe)
                    : Directory.GetCurrentDirectory();
            }

            bool header = true;
#if DEV
            header = !opt.GetOptions().Any(o => o.StartsWith("dump")) && !opt[BooleanOption.ConfigGen];
#endif
            if (!header)
            {
                notify = null;
            }

            if (header)
            {
                Console.WriteLine($"Options and seed: {opt}");
                Console.WriteLine();
            }

            int seed = (int)opt.Seed;

            notify?.Invoke(messages.Get(loadPhase));

            if (opt[BooleanOption.MergeMods])
            {
                // Previous Elden Ring UXM merge behavior
                // modDir = Path.GetDirectoryName(gameExe);
                string modPath = "mod";
                DirectoryInfo modDirInfo = new DirectoryInfo($@"{outPath}\..\{modPath}");
                modDirs = new MergedMods(modDirInfo.FullName);
            }

            modDirs = modDirs ?? new MergedMods();
            foreach (string modDir in modDirs.Dirs)
            {
                DirectoryInfo modDirInfo = new DirectoryInfo(modDir);
                if (modDirInfo != null)
                {
                    string outModDir = modDirInfo.FullName;
                    if (!modDirInfo.Exists)
                    {
                        throw new Exception(messages.Get(mergeMissingError, outModDir));
                    }

                    if (outModDir != null && new DirectoryInfo(outPath).FullName == outModDir)
                    {
                        // This should be filtered out earlier if merging via toml
                        throw new Exception(messages.Get(mergeWrongDirError, modDirInfo.Name));
                    }
                }
            }

            GameData game = new GameData(distDir);
            game.Load(modDirs);

#if DEBUG
            if (opt[BooleanOption.Update])
            {
                MiscSetup.UpdateEldenRing(game, opt);
                return;
            }
            // game.UnDcx(ForGame(FromGame.ER).GameDir + @"\map\mapstudio"); return;
            // game.SearchParamInt(14000800); return;
            // foreach (string lang in MiscSetup.Langs.Keys) game.DumpMessages(GameSpec.ForGame(GameSpec.FromGame.ER).GameDir + $@"\msg\{lang}"); return;
            // foreach (string lang in MiscSetup.Langs.Keys) game.DumpMessages(GameSpec.ForGame(GameSpec.FromGame.AC6).GameDir + $@"\msg\{lang}"); return;
            // game.UnDcx(ForGame(FromGame.AC6).GameDir + @"\map\mapstudio"); return;
            // game.DumpMessages(GameSpec.ForGame(GameSpec.FromGame.ER).GameDir + @"\msg\engus"); return;
            // game.DumpMessages(GameSpec.ForGame(GameSpec.FromGame.DS1R).GameDir + @"\msg\ENGLISH"); return;
            // MiscSetup.CombineSFX(game.Maps.Keys.Concat(new[] { "dlc1", "dlc2" }).ToList(), GameSpec.ForGame(GameSpec.FromGame.DS3).GameDir + @"\combinedai", true); return;
            // MiscSetup.CombineAI(game.Maps.Keys.ToList(), ForGame(FromGame.DS3).GameDir + @"\combinedai", true); return;
#endif

            // Prologue
            if (header)
            {
                if (game.HasMods) Console.WriteLine();
                if (opt[BooleanOption.EnemyRandomization])
                {
                    Console.WriteLine(
                        "Ctrl+F 'Boss placements' or 'Miniboss placements' or 'Basic placements' to see enemy placements.");
                }

                if (opt[BooleanOption.ItemRandomization])
                {
                    Console.WriteLine(
                        "Ctrl+F 'Hints' to see item placement hints, or Ctrl+F for a specific item name.");
                }

                Console.WriteLine($"Version: {EldenVersion}");

                if (preset != null)
                {
                    Console.WriteLine();
                    Console.WriteLine($"-- Preset");
                    Console.WriteLine(preset.ToYamlString());
                }

                Console.WriteLine();
#if !DEBUG
                for (int i = 0; i < 50; i++) Console.WriteLine();
#endif
            }

            // Slightly different high-level algorithm for each game.
            if (game.EldenRing)
            {
#if DEBUG
                if (!opt[BooleanOption.ItemRandomization])
                {
                    new EldenDataPrinter().PrintData(game, opt);
                    return;
                }
#endif
                // Base character data on a few things: seed, logic options, logical preset contents.
                // In theory, could also look at loaded params/maps, but try this for now.
                int trueSeed =
                    (int)Util.JavaStringHash(opt.LogicString() + "&&" +
                                             (preset == null ? "" : preset.ToStableString()));
                LocationData data = null;
                PermutationWriter.Result permResult = null;
                CharacterWriter characters = null;
                if (opt[BooleanOption.ItemRandomization])
                {
                    notify?.Invoke(messages.Get(itemPhase));
                    EventConfig itemEventConfig;
                    using (var reader = File.OpenText($@"{game.Dir}\Base\itemevents.txt"))
                    {
                        IDeserializer deserializer = new DeserializerBuilder().Build();
                        itemEventConfig = deserializer.Deserialize<EventConfig>(reader);
                    }

                    EldenCoordinator coord = new EldenCoordinator(game, opt[BooleanOption.DebugCoords]);
                    if (opt[BooleanOption.DumpCoords])
                    {
                        coord.DumpJS(game);
                        return;
                    }

                    EldenLocationDataScraper scraper = new EldenLocationDataScraper();
                    data = scraper.FindItems(game, coord, opt);
                    if (data == null || opt[BooleanOption.DumpLot] || opt[BooleanOption.DumpItemFlag])
                    {
                        return;
                    }

                    AnnotationData ann = new AnnotationData(game, data);
                    ann.Load(opt);
                    if (opt[BooleanOption.DumpAnn])
                    {
                        ann.Save(initial: false, filter: opt[BooleanOption.AnnFilter], coord: coord);
                        return;
                    }

                    if (opt[BooleanOption.DumpFog])
                    {
                        new ReverseEnemyOrder().FogElden(opt, game, data, ann, coord);
                        return;
                    }

                    ann.ProcessRestrictions(opt, null);
                    ann.AddSpecialItems();
                    ann.AddMaterialItems(opt[BooleanOption.Mats]);

                    Random random = new Random(seed);
                    Permutation perm = new Permutation(game, data, ann, messages, explain: opt[BooleanOption.Explain]);
                    perm.Logic(random, opt, preset);

                    notify?.Invoke(messages.Get(editPhase));
                    random = new Random(seed + 1);
                    PermutationWriter writer =
                        new PermutationWriter(game, data, ann, null, itemEventConfig, messages, coord);
                    permResult = writer.Write(random, perm, opt);

                    if (opt[BooleanOption.MarkAreas])
                    {
                        new HintMarker(game, data, ann, messages, coord).Write(opt, perm, permResult);
                    }

                    if (opt[BooleanOption.Mats])
                    {
                        new EldenMaterialRandomizer(game, data, ann).Randomize(opt, perm);
                    }

                    random = new Random(trueSeed);
                    characters = new CharacterWriter(game, data);
                    characters.Write(random, opt);
                }
                else if (!(opt[BooleanOption.NoOutfits] && opt[BooleanOption.NoStarting]))
                {
                    // Partially load item data just for identifying eligible starting weapons
                    EldenCoordinator coord = new EldenCoordinator(game, false);
                    EldenLocationDataScraper scraper = new EldenLocationDataScraper();
                    data = scraper.FindItems(game, coord, opt);

                    Random random = new Random(trueSeed);
                    characters = new CharacterWriter(game, data);
                    characters.Write(random, opt);
                }

                if (opt[BooleanOption.EnemyRandomization])
                {
                    notify?.Invoke(messages.Get(enemyPhase));

                    EventConfig enemyConfig;
                    string emedfPath = null;
                    string path = $@"{game.Dir}\Base\events.txt";
#if DEV
                    if (opt[BooleanOption.Full] || opt[BooleanOption.ConfigGen])
                    {
                        emedfPath = @"configs\diste\er-common.emedf.json";
                        path = @"configs\diste\events.txt";
                    }
#endif
                    using (var reader = File.OpenText(path))
                    {
                        IDeserializer deserializer = new DeserializerBuilder().Build();
                        enemyConfig = deserializer.Deserialize<EventConfig>(reader);
                    }

                    Events events = new Events(
                        emedfPath,
                        darkScriptMode: true,
                        paramAwareMode: true,
                        valueSpecs: enemyConfig.ValueTypes);
                    EnemyLocations enemyLocs = new EnemyRandomizer(game, events, enemyConfig).Run(opt, preset);

                    if (enemyLocs != null && characters != null && !opt[BooleanOption.NoOutfits])
                    {
                        characters.SetSpecialOutfits(opt, enemyLocs);
                    }
                }
#if DEV
                // Should add some global logging levels
                if (!header) return;
#endif

                if (!opt[BooleanOption.NoGesture])
                {
                    new GestureRandomizer(game).Randomize(opt);
                }

                MiscSetup.EldenCommonPass(game, opt, messages, permResult);

                if (!opt[BooleanOption.DryRun])
                {
                    notify?.Invoke(messages.Get(savePhase));
                    string options =
                        $"Produced by Elden Ring Randomizer {EldenVersion} by thefifthmatt. Do not distribute. Options and seed: {opt}";
                    int mapPercent = -1;

                    void notifyMap(double val)
                    {
                        int percent = (int)Math.Floor(val * 100);
                        if (percent > mapPercent && percent <= 100)
                        {
#if !DEBUG
                            notify?.Invoke(messages.Get(saveMapPhase, percent));
#endif
                            mapPercent = percent;
                        }
                    }

                    game.WriteFMGs = true;
                    messages.SetFMGEntry(
                        game, FMGCategory.Menu, "EventTextForMap",
                        RuntimeParamChecker.RestartMessageId, restartMsg);

                    game.SaveEldenRing(outPath, opt[BooleanOption.Uxm], options, notifyMap);
                }
            }
        }
    }
}