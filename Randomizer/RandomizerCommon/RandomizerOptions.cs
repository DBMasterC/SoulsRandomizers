﻿using System;
using System.Collections.Generic;
using System.Linq;
using static SoulsIds.GameSpec;

namespace RandomizerCommon
{
    public class RandomizerOptions
    {
        private SortedDictionary<string, bool> opt = new SortedDictionary<string, bool>();
        private SortedDictionary<string, string> str = new SortedDictionary<string, string>();
        private Dictionary<string, float> num = new Dictionary<string, float>();
        private int difficulty;

        public RandomizerOptions Copy()
        {
            // Copies most things, except not the seed and preset (maybe can revisit this when revisiting DS3)
            return new RandomizerOptions()
            {
                opt = new SortedDictionary<string, bool>(opt),
                str = new SortedDictionary<string, string>(str),
                num = new Dictionary<string, float>(num),
                difficulty = difficulty,
                Seed = Seed,
                Seed2 = Seed2,
                Preset = Preset,
            };
        }

        public static int EldenRingVersion = 10;

        public RandomizerOptions()
        {
            int version = EldenRingVersion;
            for (int i = 1; i < version; i++)
            {
                opt[$"v{i}"] = false;
            }

            opt[$"v{version}"] = true;
        }

        public static RandomizerOptions Parse(IEnumerable<string> args, 
            Predicate<string> optionsFilter = null)
        {
            RandomizerOptions options = new RandomizerOptions();
            uint seed = 0;
            uint seed2 = 0;
            int difficulty = 0;
            List<string> preset = new List<string>();
            string op = null;
            int numIndex = 0;
            foreach (string arg in args)
            {
                if (arg == "--preset")
                {
                    op = "preset";
                    continue;
                }
                else if (arg.StartsWith("--"))
                {
                    op = null;
                }

                if (op == "preset")
                {
                    preset.Add(arg);
                }
                else if (uint.TryParse(arg, out uint num))
                {
                    if (numIndex == 0)
                    {
                        difficulty = (int)num;
                    }
                    else if (numIndex == 1)
                    {
                        seed = num;
                    }
                    else if (numIndex == 2)
                    {
                        seed2 = num;
                    }

                    numIndex++;
                }
                else if (arg.Contains(":"))
                {
                    string[] parts = arg.Split(new[] { ':' }, 2);
                    options.str[parts[0]] = parts[1];
                }
                else
                {
                    if (optionsFilter != null && !optionsFilter(arg)) continue;
                    options[arg] = true;
                }
            }

            options.Difficulty = difficulty;
            options.Seed = seed;
            options.Seed2 = seed2;
            if (options.str.TryGetValue("bias", out string valStr) && int.TryParse(valStr, out int val))
            {
                options.Difficulty = val;
                options.str.Remove("bias");
            }

            if (options.str.TryGetValue("seed", out valStr) && uint.TryParse(valStr, out uint uval))
            {
                options.Seed = uval;
                options.str.Remove("seed");
            }

            if (options.str.TryGetValue("seed2", out valStr) && uint.TryParse(valStr, out uval))
            {
                options.Seed2 = uval;
                options.str.Remove("seed2");
            }

            if (preset.Count > 0) options.Preset = string.Join(" ", preset);
            return options;
        }

        public bool this[string name]
        {
            get
            {
                if (name.StartsWith("invert"))
                {
                    name = "no" + name.Substring(6);
                    return !(opt.ContainsKey(name) ? opt[name] : false);
                }

                return opt.ContainsKey(name) ? opt[name] : false;
            }
            set
            {
                if (name.StartsWith("invert"))
                {
                    name = "no" + name.Substring(6);
                    opt[name] = !value;
                }
                else if (!name.StartsWith("default"))
                {
                    opt[name] = value;
                }
            }
        }

        public bool GetInt(string name, out int val)
        {
            val = 0;
            return str.TryGetValue(name, out string s) && int.TryParse(s, out val);
        }

        public bool GetInt(string name, int min, int max, out int val)
        {
            val = 0;
            return str.TryGetValue(name, out string s) && int.TryParse(s, out val) && val >= min && val <= max;
        }

        public void SetInt(string name, int? maybeVal)
        {
            if (maybeVal is int val)
            {
                str[name] = val.ToString();
            }
            else
            {
                str.Remove(name);
            }
        }

        public int Difficulty
        {
            get { return difficulty; }
            set
            {
                difficulty = Math.Max(0, Math.Min(100, value));
                // Linear scaling for these params, from 0 to 1. But severity may depend on game
                if (Game == FromGame.ER)
                {
                    // So far, unfair is not used in ER
                    num["unfairweight"] = FromRange(40, 80);
                    num["veryunfairweight"] = FromRange(70, 100);
                    num["keyitemdifficulty"] = FromRange(30, 100);
                    num["allitemdifficulty"] = FromRange(0, 100);
                }
                else
                {
                    num["unfairweight"] = FromRange(40, 80);
                    num["veryunfairweight"] = FromRange(70, 100);
                    num["keyitemdifficulty"] = FromRange(20, 60);
                    num["allitemdifficulty"] = FromRange(0, 80);
                }

                // This one is a multiplicative weight, but important for distributing key items throughout the game.
                float key;
                if (difficulty == 0) key = 1;
                else if (difficulty < 20) key = 2 + 2 * FromRange(0, 20);
                else if (difficulty < 60) key = 4 + 6 * FromRange(20, 60);
                else key = 10 + 90 * FromRange(60, 100);
                num["keyitemchainweight"] = key;
            }
        }

        private float FromRange(int start, int end)
        {
            if (difficulty < start) return 0;
            if (difficulty >= end) return 1;
            return 1f * (difficulty - start) / (end - start);
        }

        private FromGame Game { get; set; }
        public uint Seed { get; set; }
        public uint Seed2 { get; set; }
        public string Preset { get; set; }

        public string GameNameForFile => Game.ToString();

        public float GetNum(string name)
        {
            return num[name];
        }

        // Options which are purely aesthetic or related to installation
        private static HashSet<string> logiclessOptions = new HashSet<string> { "mergemods", "uxm", "bossbgm" };

        // Boolean options which apply (not mapped options)
        public SortedSet<string> GetLogicOptions()
        {
            return new SortedSet<string>(
                opt.Where(e => e.Value && !logiclessOptions.Contains(e.Key)).Select(e => e.Key));
        }

        public SortedSet<string> GetOptions()
        {
            return new SortedSet<string>(opt.Where(e => e.Value).Select(e => e.Key));
        }

        public string ConfigString(bool includeSeed = false, bool includePreset = false, bool onlyLogic = true)
        {
            SortedSet<string> words = onlyLogic ? GetLogicOptions() : GetOptions();
            words.UnionWith(str.Select(e => $"{e.Key}:{e.Value}"));
            string result = string.Join(" ", words);
            // Colon syntax should be safe to use for other games, but test it out first.
            // At some point, we could switch to using the str dictionary directly.
            result += Game == FromGame.ER ? $" bias:{Difficulty}" : $" {Difficulty}";
            if (includeSeed)
            {
                result += Game == FromGame.ER ? $" seed:{Seed}" : $" {Seed}";
                if (Seed2 != 0 && Seed2 != Seed)
                {
                    result += Game == FromGame.ER ? $" seed2:{Seed2}" : $" {Seed2}";
                }
            }

            if (!string.IsNullOrEmpty(Preset) && includePreset)
            {
                result += $" --preset {Preset}";
            }

            return result;
        }

        public string FullString() => ConfigString(includeSeed: true, includePreset: true, onlyLogic: false);
        public string LogicString() => ConfigString(includeSeed: true, includePreset: false, onlyLogic: true);
        public override string ToString() => ConfigString(includeSeed: true, includePreset: true, onlyLogic: false);

        public string ConfigHash() =>
            (Util.JavaStringHash(ConfigString(includeSeed: false, includePreset: true, onlyLogic: true)) % 99999)
            .ToString().PadLeft(5, '0');
    }
}