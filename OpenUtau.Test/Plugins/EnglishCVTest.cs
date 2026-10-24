using System;
using System.Collections.Generic;
using System.Linq;
using OpenUtau.Api;
using OpenUtau.Core.Ustx;
using OpenUtau.Plugin.Builtin;
using Xunit;

namespace OpenUtau.Plugins {
    public class EnglishCVPhonemizerTest {
        private sealed class Singer : USinger {
            private readonly Dictionary<string, UOto> entries;

            public Singer(params string[] aliases) {
                entries = aliases.ToDictionary(
                    alias => alias, UOto.OfDummy, StringComparer.OrdinalIgnoreCase);
            }

            public override IList<UOto> Otos => entries.Values.ToArray();

            public override bool TryGetMappedOto(string phoneme, int tone,
                string color, out UOto oto) {
                return entries.TryGetValue(phoneme, out oto!);
            }
        }

        private static Phonemizer.Note Note(string lyric, int position = 0) {
            return new Phonemizer.Note {
                lyric = lyric,
                tone = 60,
                position = position,
                duration = 240,
            };
        }

        [Fact]
        public void HelloUsesAlternatePronunciationAndConnectedAlias() {
            var phonemizer = new EnglishCVPhonemizer();
            phonemizer.SetSinger(new Singer("- he_alt", "* lo", "he_alt", "lo"));
            var result = phonemizer.Process(new[] { Note("hello") }, null, null,
                null, null, Array.Empty<Phonemizer.Note>());
            Assert.Equal(new[] { "- he_alt", "* lo" },
                result.phonemes.Select(p => p.phoneme));
            Assert.Equal(new[] { 0, 120 }, result.phonemes.Select(p => p.position));
            Assert.All(result.phonemes, p => Assert.Null(p.error));
        }

        [Fact]
        public void PronounHeDoesNotUseHelloVariant() {
            var phonemizer = new EnglishCVPhonemizer();
            phonemizer.SetSinger(new Singer("he", "he_alt", "* he"));
            var previous = Note("my", -240);
            var result = phonemizer.Process(new[] { Note("he") }, previous, null,
                previous, null, Array.Empty<Phonemizer.Note>());
            Assert.Equal("* he", Assert.Single(result.phonemes).phoneme);
        }

        [Fact]
        public void MissingSyllablesHaveErrors() {
            var phonemizer = new EnglishCVPhonemizer();
            phonemizer.SetSinger(new Singer("he_alt", "lo"));
            var result = phonemizer.Process(new[] { Note("something") }, null,
                null, null, null, Array.Empty<Phonemizer.Note>());
            Assert.Equal(new[] { "some", "thing" },
                result.phonemes.Select(p => p.phoneme));
            Assert.All(result.phonemes, p => Assert.NotNull(p.error));
        }

        [Fact]
        public void ExtenderSpreadsSyllablesAcrossCombinedDuration() {
            var phonemizer = new EnglishCVPhonemizer();
            phonemizer.SetSinger(new Singer("he_alt", "lo"));
            var result = phonemizer.Process(new[] { Note("hello"), Note("+", 240) },
                null, null, null, null, Array.Empty<Phonemizer.Note>());
            Assert.Equal(new[] { 0, 240 },
                result.phonemes.Select(p => p.position));
        }

        [Fact]
        public void HintOverridesWordDictionary() {
            var phonemizer = new EnglishCVPhonemizer();
            phonemizer.SetSinger(new Singer("he", "lo"));
            var note = Note("hello");
            note.phoneticHint = "he lo";
            var result = phonemizer.Process(new[] { note }, null, null,
                null, null, Array.Empty<Phonemizer.Note>());
            Assert.Equal(new[] { "he", "lo" },
                result.phonemes.Select(p => p.phoneme));
        }
    }
}
