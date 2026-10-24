using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenUtau.Api;
using OpenUtau.Core.G2p;
using OpenUtau.Core.Ustx;
using Serilog;

namespace OpenUtau.Plugin.Builtin {
    [Phonemizer("English CV Phonemizer", "EN CV", language: "EN")]
    public class EnglishCVPhonemizer : Phonemizer {
        private static readonly Dictionary<string, string[]> DefaultWords =
            new(StringComparer.OrdinalIgnoreCase) {
                ["hello"] = new[] { "he_alt", "lo" },
                ["something"] = new[] { "some", "thing" },
            };

        private static readonly Dictionary<string, string[]> DefaultSounds =
            new(StringComparer.OrdinalIgnoreCase) {
                ["a"] = new[] { "ey" },
                ["a_alt"] = new[] { "aa" },
                ["he"] = new[] { "hh", "iy" },
                ["he_alt"] = new[] { "hh", "eh" },
                ["me"] = new[] { "m", "iy" },
                ["me_alt"] = new[] { "m", "eh" },
                ["lo"] = new[] { "l", "ow" },
            };

        private readonly Dictionary<string, string[]> words =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string[]> sounds =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string alias, string[] phones)> available =
            new();
        private USinger? singer;
        private ArpabetG2p? g2p;

        public override void SetSinger(USinger singer) {
            this.singer = singer;
            words.Clear();
            sounds.Clear();
            available.Clear();

            foreach (var entry in DefaultWords) {
                words[entry.Key] = entry.Value;
            }
            foreach (var entry in DefaultSounds) {
                sounds[entry.Key] = entry.Value;
            }

            try {
                g2p ??= new ArpabetG2p();
            } catch (Exception e) {
                Log.Error(e, "Failed to initialize English CV pronunciation dictionary.");
                g2p = null;
            }

            LoadEntries(Path.Combine(PluginDir, "english-cv-words.txt"), words);
            LoadEntries(Path.Combine(PluginDir, "english-cv-aliases.txt"), sounds);

            if (!string.IsNullOrWhiteSpace(singer?.Location)) {
                LoadEntries(Path.Combine(singer.Location, "english-cv-words.txt"), words);
                LoadEntries(Path.Combine(singer.Location, "english-cv-aliases.txt"), sounds);
            }

            if (singer == null) {
                return;
            }

            var names = singer.Otos
                .Where(oto => oto != null && !string.IsNullOrWhiteSpace(oto.Alias))
                .Select(oto => StripPrefix(oto.Alias))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (string name in names) {
                if (name.Length == 0 || name.Contains(' ')) {
                    continue;
                }

                string[]? phones;
                if (!sounds.TryGetValue(name, out phones)) {
                    if (name.EndsWith("_alt", StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }
                    phones = GetPhones(name);
                }
                if (phones == null || phones.Length == 0 || g2p == null ||
                    phones.Count(g2p.IsVowel) != 1) {
                    continue;
                }
                available.Add((name, phones));
            }
        }

        private static void LoadEntries(string path, Dictionary<string, string[]> dictionary) {
            if (!File.Exists(path)) {
                return;
            }
            try {
                foreach (string raw in File.ReadLines(path)) {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) {
                        continue;
                    }
                    int equals = line.IndexOf('=');
                    if (equals <= 0 || equals == line.Length - 1) {
                        continue;
                    }
                    string key = line.Substring(0, equals).Trim();
                    string[] value = line.Substring(equals + 1)
                        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (key.Length > 0 && value.Length > 0) {
                        dictionary[key] = value;
                    }
                }
            } catch (Exception e) {
                Log.Error(e, "Failed to load English CV dictionary {Path}.", path);
            }
        }

        private static string StripPrefix(string alias) {
            if (alias.StartsWith("- ", StringComparison.Ordinal) ||
                alias.StartsWith("* ", StringComparison.Ordinal)) {
                return alias.Substring(2);
            }
            return alias;
        }

        private string[]? GetPhones(string word) {
            try {
                return g2p?.Query(word.ToLowerInvariant());
            } catch (Exception e) {
                Log.Warning(e, "English CV pronunciation lookup failed for {Word}.", word);
                return null;
            }
        }

        private string[] FindAliases(string word) {
            if (words.TryGetValue(word, out var mapped)) {
                return mapped;
            }

            if (singer != null && singer.Otos.Any(oto =>
                string.Equals(StripPrefix(oto.Alias), word, StringComparison.OrdinalIgnoreCase))) {
                return new[] { word };
            }

            string[]? phones = GetPhones(word);
            if (phones == null || phones.Length == 0) {
                return new[] { word };
            }

            int count = phones.Length;
            var best = new List<string>?[count + 1];
            best[count] = new List<string>();

            for (int i = count - 1; i >= 0; i--) {
                foreach (var candidate in available) {
                    int end = i + candidate.phones.Length;
                    if (end > count || best[end] == null) {
                        continue;
                    }
                    bool matches = true;
                    for (int j = 0; j < candidate.phones.Length; j++) {
                        if (!string.Equals(phones[i + j], candidate.phones[j],
                            StringComparison.OrdinalIgnoreCase)) {
                            matches = false;
                            break;
                        }
                    }
                    if (!matches) {
                        continue;
                    }
                    var path = new List<string> { candidate.alias };
                    path.AddRange(best[end]!);
                    if (best[i] == null || path.Count < best[i]!.Count) {
                        best[i] = path;
                    }
                }
            }

            return best[0]?.ToArray() ?? new[] { word };
        }

        private bool HasOto(string alias, Note note, PhonemeAttributes attr) {
            int shift = attr.toneShift ?? GetParentToneShift();
            string color = attr.voiceColor ?? GetParentVoiceColor();
            int? alternate = attr.alternate ?? GetParentAlternate();
            if (singer == null) {
                return false;
            }
            if (alternate.HasValue &&
                singer.TryGetMappedOto(alias + alternate.Value, note.tone + shift, color, out _)) {
                return true;
            }
            return singer.TryGetMappedOto(alias, note.tone + shift, color, out _);
        }

        private string ChooseOto(string alias, bool atStart, Note note, PhonemeAttributes attr, out bool found) {
            if (alias.StartsWith("- ", StringComparison.Ordinal) ||
                alias.StartsWith("* ", StringComparison.Ordinal)) {
                found = HasOto(alias, note, attr);
                return alias;
            }

            string first = (atStart ? "- " : "* ") + alias;
            string second = (atStart ? "* " : "- ") + alias;
            foreach (string candidate in new[] { first, alias, second }) {
                if (HasOto(candidate, note, attr)) {
                    found = true;
                    return candidate;
                }
            }

            found = false;
            return alias;
        }

        public override Result Process(Note[] notes, Note? prev, Note? next,
            Note? prevNeighbour, Note? nextNeighbour, Note[] prevs) {
            if (notes.Length == 0) {
                return new Result { phonemes = Array.Empty<Phoneme>() };
            }

            Note note = notes[0];
            string lyric = note.lyric?.Trim() ?? string.Empty;
            if (lyric.Length == 0) {
                return MakeSimpleResult(string.Empty);
            }

            if (lyric.StartsWith("?", StringComparison.Ordinal)) {
                return MakeSimpleResult(lyric.Substring(1));
            }

            string[] aliases;
            if (!string.IsNullOrWhiteSpace(note.phoneticHint)) {
                aliases = note.phoneticHint.Split(
                    (char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            } else if (lyric.StartsWith("- ", StringComparison.Ordinal) ||
                       lyric.StartsWith("* ", StringComparison.Ordinal)) {
                aliases = new[] { lyric };
            } else {
                aliases = FindAliases(lyric.ToLowerInvariant());
            }

            if (aliases.Length == 0) {
                return MakeSimpleResult(lyric);
            }

            int end = notes[^1].position + notes[^1].duration;
            int duration = Math.Max(1, end - note.position);
            var phonemes = new Phoneme[aliases.Length];

            for (int i = 0; i < aliases.Length; i++) {
                PhonemeAttributes attr = note.phonemeAttributes?
                    .FirstOrDefault(a => a.index == i) ?? default;
                int position = (int)((long)duration * i / aliases.Length);
                bool atStart = i == 0 && !prevNeighbour.HasValue;
                string alias = ChooseOto(aliases[i], atStart, note, attr, out bool found);
                phonemes[i] = new Phoneme {
                    index = i,
                    phoneme = alias,
                    position = position,
                    error = found ? null : new FileNotFoundException(
                        $"English CV syllable '{aliases[i]}' is not in the singer's OTO."),
                };
            }
            return new Result { phonemes = phonemes };
        }
    }
}
