using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using OpenUtau.Api;
using OpenUtau.Core.Ustx;
using OpenUtau.Plugin.Builtin;
using Xunit;

namespace OpenUtau.Plugins {
    public class EnglishCVPhonemizerTest {
        private sealed class Singer : USinger {
            private readonly Dictionary<string, UOto> entries;
            private readonly string location;

            public Singer(params string[] aliases) : this(string.Empty, aliases) { }

            public Singer(string location, params string[] aliases) {
                this.location = location;
                entries = aliases.ToDictionary(
                    alias => alias, UOto.OfDummy, StringComparer.OrdinalIgnoreCase);
            }

            public override IList<UOto> Otos => entries.Values.ToArray();
            public override string Location => location;

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
        public void ExtraExtenderSustainsLastSyllable() {
            var phonemizer = new EnglishCVPhonemizer();
            phonemizer.SetSinger(new Singer("he_alt", "lo"));
            var result = phonemizer.Process(
                new[] { Note("hello"), Note("+", 240), Note("+", 480) },
                null, null, null, null, Array.Empty<Phonemizer.Note>());
            Assert.Equal(new[] { 0, 240 },
                result.phonemes.Select(p => p.position));
        }

        [Theory]
        [InlineData("mystery=mee")]
        [InlineData("mystery=m iy")]
        [InlineData("mystery=arpabet: m iy")]
        public void ReadableAndLegacyPronunciationsMatchEnglishWords(string entry) {
            string folder = Path.Combine(Path.GetTempPath(), "english-cv-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try {
                File.WriteAllText(Path.Combine(folder, "english-cv-aliases.txt"), entry);
                var phonemizer = new EnglishCVPhonemizer();
                phonemizer.SetSinger(new Singer(folder, "mystery"));
                var result = phonemizer.Process(new[] { Note("me") }, null, null,
                    null, null, Array.Empty<Phonemizer.Note>());
                Assert.Equal("mystery", Assert.Single(result.phonemes).phoneme);
                Assert.Null(result.phonemes[0].error);
            } finally {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void BareArpabetVowelsAreNotEnglishRespellings() {
            string folder = Path.Combine(Path.GetTempPath(), "english-cv-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try {
                File.WriteAllText(Path.Combine(folder, "english-cv-aliases.txt"),
                    "eye_sound=ay\nreduced_sound=ah\nletter_a=english:ay\nopen_a=english:ah\n");
                var phonemizer = new EnglishCVPhonemizer();
                phonemizer.SetSinger(new Singer(folder,
                    "eye_sound", "reduced_sound", "letter_a", "open_a"));
                var field = typeof(EnglishCVPhonemizer).GetField("sounds",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var sounds = (Dictionary<string, string[]>)field!.GetValue(phonemizer)!;
                Assert.Equal(new[] { "ay" }, sounds["eye_sound"]);
                Assert.Equal(new[] { "ah" }, sounds["reduced_sound"]);
                Assert.Equal(new[] { "ey" }, sounds["letter_a"]);
                Assert.Equal(new[] { "aa" }, sounds["open_a"]);
            } finally {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void UnknownAltIsNotSubstitutedForNormalSound() {
            var phonemizer = new EnglishCVPhonemizer();
            phonemizer.SetSinger(new Singer("me_alt"));
            var result = phonemizer.Process(new[] { Note("me") }, null, null,
                null, null, Array.Empty<Phonemizer.Note>());
            Assert.Equal("me", Assert.Single(result.phonemes).phoneme);
            Assert.NotNull(result.phonemes[0].error);
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
