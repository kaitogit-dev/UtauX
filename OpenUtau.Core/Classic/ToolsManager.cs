using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using OpenUtau.Core;
using OpenUtau.Core.Util;
using Serilog;

namespace OpenUtau.Classic {
    public class ToolsManager : SingletonBase<ToolsManager> {
        static object _locker = new object();

        private readonly List<IResampler> resamplers = new List<IResampler>();
        private readonly List<IWavtool> wavtools = new List<IWavtool>();
        private readonly Dictionary<string, IResampler> resamplersMap
            = new Dictionary<string, IResampler>();
        private readonly Dictionary<string, IWavtool> wavtoolsMap
            = new Dictionary<string, IWavtool>();

        public List<IResampler> Resamplers {
            get {
                lock (_locker) {
                    return resamplers.ToList();
                }
            }
        }

        public List<IWavtool> Wavtools {
            get {
                lock (_locker) {
                    return wavtools.ToList();
                }
            }
        }

        IResampler LoadResampler(string filePath, string basePath) {
            if (!File.Exists(filePath)) {
                return null;
            }
            string ext = Path.GetExtension(filePath).ToLower();
            if ((OS.IsWindows() || !string.IsNullOrEmpty(Preferences.Default.WinePath)) && (ext == ".exe" || ext == ".bat")) {
                return new ExeResampler(filePath, basePath);
            } 
            if (!OS.IsWindows() && (ext == ".sh" || string.IsNullOrEmpty(ext))) {
                return new ExeResampler(filePath, basePath);
            }
            return null;
        }

        IWavtool LoadWavtool(string filePath, string basePath) {
            if (!File.Exists(filePath)) {
                return null;
            }
            string ext = Path.GetExtension(filePath).ToLower();
            if ((OS.IsWindows() || !string.IsNullOrEmpty(Preferences.Default.WinePath)) && (ext == ".exe" || ext == ".bat")) {
                return new ExeWavtool(filePath, basePath);
            } 
            if (!OS.IsWindows() && (ext == ".sh" || string.IsNullOrEmpty(ext))) {
                return new UnixWavtool(filePath, basePath);
            }
            return null;
        }

        public void Initialize() {
            lock (_locker) {
                InstallMoresampler();
                SearchResamplers();
                SearchWavtools();
            }
        }

        private static void InstallMoresampler() {
            if (!OS.IsWindows() && string.IsNullOrEmpty(Preferences.Default.WinePath)) {
                return;
            }

            var paths = PathManager.Inst;
            string resampler = Path.Combine(paths.ResamplersPath, "moresampler.exe");
            string wavtool = Path.Combine(paths.WavtoolsPath, "moresampler.exe");
            if (File.Exists(resampler) && File.Exists(wavtool)) {
                return;
            }

            string previousData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Personal), "OpenUtau");
            string[] candidates = {
                resampler,
                wavtool,
                Path.Combine(paths.DataPath, "moresampler.exe"),
                Path.Combine(paths.RootPath, "moresampler.exe"),
                Path.Combine(previousData, "Resamplers", "moresampler.exe"),
                Path.Combine(previousData, "Wavtools", "moresampler.exe"),
            };

            try {
                string? source = candidates.FirstOrDefault(File.Exists);
                if (source != null) {
                    foreach (string target in new[] { resampler, wavtool }) {
                        if (!File.Exists(target)) {
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            File.Copy(source, target);
                        }
                    }
                    return;
                }

                string[] archives = {
                    Path.Combine(paths.DataPath, "moresampler.zip"),
                    Path.Combine(paths.RootPath, "moresampler.zip"),
                };
                string? archivePath = archives.FirstOrDefault(File.Exists);
                if (archivePath != null) {
                    using var archive = ZipFile.OpenRead(archivePath);
                    var executable = archive.Entries.FirstOrDefault(entry =>
                        string.Equals(Path.GetFileName(entry.FullName), "moresampler.exe",
                            StringComparison.OrdinalIgnoreCase)
                        && entry.Length > 0 && entry.Length <= 32 * 1024 * 1024);
                    if (executable != null) {
                        foreach (string target in new[] { resampler, wavtool }) {
                            if (!File.Exists(target)) {
                                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                                executable.ExtractToFile(target);
                            }
                        }
                        return;
                    }
                }

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                byte[] binary = client.GetByteArrayAsync(
                    "https://raw.githubusercontent.com/hungrierr/Moresampler/dcbb2ccef2e9da74e1cdd5f880c5e00051c4f76e/moresampler.exe")
                    .GetAwaiter().GetResult();
                if (binary.Length != 948736) {
                    throw new InvalidDataException("Unexpected moresampler size.");
                }

                byte[] header = Encoding.ASCII.GetBytes($"blob {binary.Length}\0");
                byte[] gitBlob = new byte[header.Length + binary.Length];
                Buffer.BlockCopy(header, 0, gitBlob, 0, header.Length);
                Buffer.BlockCopy(binary, 0, gitBlob, header.Length, binary.Length);
                if (!Convert.ToHexString(SHA1.HashData(gitBlob)).Equals(
                    "E0DAE5D889E11306827800F1DFA80F6143DBBA2D", StringComparison.Ordinal)) {
                    throw new InvalidDataException("Moresampler checksum mismatch.");
                }

                foreach (string target in new[] { resampler, wavtool }) {
                    if (File.Exists(target)) {
                        continue;
                    }
                    string directory = Path.GetDirectoryName(target)!;
                    Directory.CreateDirectory(directory);
                    string temporary = Path.Combine(directory, Path.GetRandomFileName());
                    try {
                        File.WriteAllBytes(temporary, binary);
                        File.Move(temporary, target, false);
                    } finally {
                        if (File.Exists(temporary)) {
                            File.Delete(temporary);
                        }
                    }
                }
            } catch (Exception e) {
                Log.Error(e, "Failed to install moresampler.");
            }
        }

        public void SearchResamplers() {
            resamplers.Clear();
            resamplersMap.Clear();
            resamplers.Add(new WorldlineResampler());
            resamplers.Add(new HifisamplerResampler());
            var builtInNames = resamplers.Select(r => r.ToString()).ToHashSet();
            string basePath = PathManager.Inst.ResamplersPath;
            try {
                Directory.CreateDirectory(basePath);
                foreach (var file in Directory.EnumerateFiles(basePath, "*", new EnumerationOptions() {
                    RecurseSubdirectories = true
                })) {
                    var driver = LoadResampler(file, basePath);
                    if (driver == null) {
                        continue;
                    }
                    // A file named like a built-in resampler would replace it in resamplersMap.
                    if (builtInNames.Contains(driver.ToString())) {
                        Log.Warning($"Skipped resampler {file}: name conflict.");
                        continue;
                    }
                    resamplers.Add(driver);
                }
            } catch (Exception e) {
                Log.Error(e, "Failed to search resamplers.");
                resamplers.Clear();
            }
            foreach (var resampler in resamplers) {
                resamplersMap[resampler.ToString()] = resampler;
            }
        }

        public void SearchWavtools() {
            wavtools.Clear();
            wavtoolsMap.Clear();
            wavtools.Add(new SharpWavtool(true));
            wavtools.Add(new SharpWavtool(false));
            string basePath = PathManager.Inst.WavtoolsPath;
            try {
                Directory.CreateDirectory(basePath);
                foreach (var file in Directory.EnumerateFiles(basePath, "*", new EnumerationOptions() {
                    RecurseSubdirectories = true
                })) {
                    var driver = LoadWavtool(file, basePath);
                    if (driver != null) {
                        wavtools.Add(driver);
                    }
                }
            } catch (Exception e) {
                Log.Error(e, "Failed to search resamplers.");
                wavtools.Clear();
            }
            foreach (var wavtool in wavtools) {
                wavtoolsMap[wavtool.ToString()] = wavtool;
            }
        }

        public IResampler GetResampler(string name) {
            lock (_locker) {
                if (name != null && resamplersMap.TryGetValue(name, out var resampler)) {
                    return resampler;
                } else {
                    return resamplersMap[WorldlineResampler.name];
                }
            }
        }

        public IWavtool GetWavtool(string name) {
            lock (_locker) {
                if (name != null && wavtoolsMap.TryGetValue(name, out var wavtool)) {
                    return wavtool;
                } else {
                    return wavtoolsMap[SharpWavtool.nameConvergence];
                }
            }
        }
    }
}
