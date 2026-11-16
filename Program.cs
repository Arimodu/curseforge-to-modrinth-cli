using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace cursetomod;

internal class Program
{
    static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "cursetomod");
    static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Color helpers — orange is DarkYellow in most terminals
    static void Write(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ResetColor();
    }
    static void WriteLine(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    static async Task<int> Main(string[] args)
    {
        // Parse CLI args
        string? inputPath = null;
        string? outputPath = null;
        string? cliApiKey = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--cf-api-key" && i + 1 < args.Length)
            {
                cliApiKey = args[++i];
            }
            else if (args[i].StartsWith("--"))
            {
                PrintUsage();
                return 1;
            }
            else if (inputPath is null)
            {
                inputPath = args[i];
            }
            else if (outputPath is null)
            {
                outputPath = args[i];
            }
            else
            {
                PrintUsage();
                return 1;
            }
        }

        if (inputPath is null)
        {
            PrintUsage();
            return 1;
        }

        if (!File.Exists(inputPath))
        {
            WriteLine($"Error: File not found: {inputPath}", ConsoleColor.Red);
            return 1;
        }

        outputPath ??= Path.ChangeExtension(inputPath, ".mrpack");

        // Resolve API key
        string? apiKey = null;
        if (cliApiKey != null)
        {
            Console.Write("Testing provided API key... ");
            cliApiKey = SanitizeApiKey(cliApiKey);
            if (await TestApiKey(cliApiKey))
            {
                WriteLine("valid!", ConsoleColor.Green);
                apiKey = cliApiKey;
            }
            else
            {
                WriteLine("Falling back to stored/prompted key.", ConsoleColor.Yellow);
                Console.WriteLine();
            }
        }

        if (apiKey is null)
        {
            var stored = LoadStoredApiKey();
            if (stored != null)
            {
                // Sanitize once so the validated key is the same one that gets sent later
                stored = SanitizeApiKey(stored);
                Console.Write("Testing stored API key... ");
                if (await TestApiKey(stored))
                {
                    WriteLine("valid!", ConsoleColor.Green);
                    apiKey = stored;
                }
                else
                {
                    WriteLine($"Stored key no longer works ({ConfigPath}).", ConsoleColor.Yellow);
                    Console.WriteLine();
                }
            }
        }

        apiKey ??= await PromptForApiKey();

        // Open input zip and parse manifest
        Console.WriteLine("Reading manifest...");
        using var inputZip = ZipFile.OpenRead(inputPath);
        var manifestEntry = inputZip.GetEntry("manifest.json")
            ?? throw new Exception("No manifest.json found in the zip — is this a CurseForge modpack?");

        CfManifest manifest;
        using (var stream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<CfManifest>(stream, JsonOpts)
                ?? throw new Exception("Failed to parse manifest.json");
        }

        string mcVersion = manifest.Minecraft.Version;
        string loaderRaw = manifest.Minecraft.ModLoaders[0].Id;
        var dashIdx = loaderRaw.IndexOf('-');
        string loaderName = loaderRaw[..dashIdx];
        string loaderVersion = loaderRaw[(dashIdx + 1)..];

        // Map loader name to Modrinth key
        string modrinthLoaderKey = loaderName switch
        {
            "fabric" => "fabric-loader",
            _ => loaderName // forge, neoforge, quilt stay as-is
        };

        Console.WriteLine($"  Pack: {manifest.Name} v{manifest.Version}");
        Console.WriteLine($"  Minecraft {mcVersion}, {loaderName} {loaderVersion}");
        Console.WriteLine($"  {manifest.Files.Count} mods");
        Console.WriteLine();

        // Batch-fetch file metadata from CurseForge
        using var http = CreateHttpClient();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var allFileIds = manifest.Files.Select(f => f.FileID).ToList();
        var cfFiles = new List<CfFileInfo>();

        if (apiKey != null)
        {
            Console.WriteLine("Fetching file info from CurseForge API...");
            http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey);

            // Batch in chunks of 50
            foreach (var chunk in allFileIds.Chunk(50))
            {
                var body = new { fileIds = chunk };
                var resp = await http.PostAsJsonAsync("https://api.curseforge.com/v1/mods/files", body);
                if (!resp.IsSuccessStatusCode)
                {
                    var errBody = await resp.Content.ReadAsStringAsync();
                    WriteLine($"CurseForge API error ({resp.StatusCode}): {errBody}", ConsoleColor.Red);
                    return 1;
                }
                var result = await resp.Content.ReadFromJsonAsync<CfFilesResponse>(JsonOpts);
                if (result?.Data != null)
                    cfFiles.AddRange(result.Data);
            }
        }
        else
        {
            WriteLine("Skipping CurseForge API (no API key). All mods will need manual download.", ConsoleColor.Yellow);
        }

        // Build lookup: fileId -> manifest entry (for projectID/modId)
        var manifestLookup = manifest.Files.ToDictionary(f => f.FileID);

        // Build lookup: fileId -> CfFileInfo
        var cfFileLookup = cfFiles.ToDictionary(f => f.Id);

        // Collect SHA1 hashes for Modrinth lookup
        var sha1ToFileId = new Dictionary<string, int>();
        foreach (var cf in cfFiles)
        {
            var sha1 = cf.Hashes.FirstOrDefault(h =>
                h.Algo == 1 || string.Equals(h.AlgoName, "sha1", StringComparison.OrdinalIgnoreCase));
            if (sha1 != null)
                sha1ToFileId[sha1.Value] = cf.Id;
        }

        // Batch-check Modrinth availability
        Console.WriteLine("Checking Modrinth availability...");
        var modrinthFiles = new Dictionary<string, JsonNode>(); // sha1 -> version file info
        if (sha1ToFileId.Count > 0)
        {
            var mrBody = new { hashes = sha1ToFileId.Keys.ToArray(), algorithm = "sha1" };
            var mrResp = await http.PostAsJsonAsync("https://api.modrinth.com/v2/version_files", mrBody);
            if (mrResp.IsSuccessStatusCode)
            {
                var mrResult = await mrResp.Content.ReadFromJsonAsync<JsonObject>();
                if (mrResult != null)
                {
                    foreach (var (hash, node) in mrResult)
                    {
                        if (node != null)
                            modrinthFiles[hash] = node;
                    }
                }
            }
            else
            {
                WriteLine($"  Warning: Modrinth lookup failed ({mrResp.StatusCode}), will bundle all mods.", ConsoleColor.Yellow);
            }
        }

        // Process each mod
        Console.WriteLine();
        var mrFileEntries = new List<MrFileEntry>();
        var bundledMods = new List<(string FileName, byte[] Data)>();
        var failedMods = new List<FailedMod>();
        int modrinthCount = 0;
        int bundledCount = 0;
        int padWidth = manifest.Files.Count.ToString().Length;

        for (int i = 0; i < manifest.Files.Count; i++)
        {
            var mf = manifest.Files[i];
            var prefix = $"[{(i + 1).ToString().PadLeft(padWidth)}/{manifest.Files.Count}]";

            if (!cfFileLookup.TryGetValue(mf.FileID, out var cfFile))
            {
                Console.Write($"{prefix} ");
                if (apiKey is null)
                {
                    // No API key: we know nothing about the file except its IDs,
                    // so hand it to manual download instead of dropping it.
                    WriteLine($"Project {mf.ProjectID}, file {mf.FileID} — no API key, queued for manual download.", ConsoleColor.DarkYellow);
                    failedMods.Add(new FailedMod
                    {
                        ProjectID = mf.ProjectID,
                        FileID = mf.FileID,
                        FileName = "",
                        DisplayName = $"Project {mf.ProjectID} / File {mf.FileID}"
                    });
                }
                else
                {
                    WriteLine($"FileID {mf.FileID} — not found in CurseForge response, skipping.", ConsoleColor.Yellow);
                }
                continue;
            }

            var sha1Hash = cfFile.Hashes
                .FirstOrDefault(h => h.Algo == 1 || string.Equals(h.AlgoName, "sha1", StringComparison.OrdinalIgnoreCase));
            string? sha1 = sha1Hash?.Value;

            // Check if on Modrinth
            if (sha1 != null && modrinthFiles.TryGetValue(sha1, out var mrVersion))
            {
                // Extract the matching file from Modrinth version
                var mrFile = FindModrinthFile(mrVersion, sha1);
                if (mrFile != null)
                {
                    Console.Write($"{prefix} ");
                    Write($"{cfFile.DisplayName ?? cfFile.FileName}", ConsoleColor.Blue);
                    WriteLine($" — Found on Modrinth \u2713", ConsoleColor.Blue);
                    mrFileEntries.Add(mrFile);
                    modrinthCount++;
                    continue;
                }
            }

            // Not on Modrinth — download from CurseForge
            Console.Write($"{prefix} ");
            Write($"{cfFile.DisplayName ?? cfFile.FileName}", ConsoleColor.DarkYellow);
            Write(" — Not on Modrinth, downloading...", ConsoleColor.DarkYellow);
            var downloaded = await TryDownloadMod(http, cfFile, mf.ProjectID);
            if (downloaded != null)
            {
                WriteLine(" \u2713", ConsoleColor.Green);
                bundledMods.Add((cfFile.FileName, downloaded));
                bundledCount++;
            }
            else
            {
                WriteLine(" FAILED", ConsoleColor.Red);
                failedMods.Add(new FailedMod
                {
                    ProjectID = mf.ProjectID,
                    FileID = mf.FileID,
                    FileName = cfFile.FileName,
                    DisplayName = cfFile.DisplayName ?? cfFile.FileName
                });
            }
        }

        // Manual recovery for failed downloads
        if (failedMods.Count > 0)
        {
            Console.WriteLine();
            WriteLine($"\u26a0 {failedMods.Count} mod(s) could not be downloaded automatically.", ConsoleColor.Yellow);
            var recovered = await ManualRecovery(failedMods);
            foreach (var (fileName, _, data) in recovered)
            {
                bundledMods.Add((fileName, data));
                bundledCount++;
            }
            // Remove recovered from failed count
            failedMods.RemoveAll(f => recovered.Any(r => r.FileID == f.FileID));
        }

        // Build modrinth.index.json
        Console.WriteLine();
        Console.WriteLine("Building modrinth.index.json...");

        var modrinthIndex = new JsonObject
        {
            ["formatVersion"] = 1,
            ["game"] = "minecraft",
            ["versionId"] = manifest.Version,
            ["name"] = manifest.Name,
            ["dependencies"] = new JsonObject
            {
                ["minecraft"] = mcVersion,
                [modrinthLoaderKey] = loaderVersion
            },
            ["files"] = new JsonArray(mrFileEntries.Select(f => f.ToJsonNode()).ToArray())
        };

        // Write output mrpack
        if (File.Exists(outputPath))
        {
            Write($"Output file already exists: ", ConsoleColor.Yellow);
            Console.WriteLine(outputPath);
            // A line typed during manual recovery (e.g. a late "skip") must not answer this prompt
            DiscardStaleLine();
            Console.Write("Overwrite? [Y/n]: ");
            var answer = (await ReadLineAsync())?.Trim();
            // "No" (or end of input) keeps the existing file and writes to a new name
            if (answer is null ||
                string.Equals(answer, "n", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(answer, "no", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(outputPath) ?? ".";
                var name = Path.GetFileNameWithoutExtension(outputPath);
                var ext = Path.GetExtension(outputPath);
                int suffix = 1;
                do
                {
                    outputPath = Path.Combine(dir, $"{name}_{suffix}{ext}");
                    suffix++;
                } while (File.Exists(outputPath));
                Write("Using: ", ConsoleColor.Yellow);
                Console.WriteLine(outputPath);
            }
            else
            {
                File.Delete(outputPath);
            }
        }
        Console.WriteLine("Packaging .mrpack...");

        using (var outZip = ZipFile.Open(outputPath, ZipArchiveMode.Create))
        {
            // Write modrinth.index.json
            var indexEntry = outZip.CreateEntry("modrinth.index.json", CompressionLevel.Optimal);
            using (var stream = indexEntry.Open())
            {
                JsonSerializer.Serialize(stream, modrinthIndex, JsonOpts);
            }

            // Copy overrides from source zip
            int overrideCount = 0;
            foreach (var entry in inputZip.Entries)
            {
                if (!entry.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip directory entries
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var outEntry = outZip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var src = entry.Open();
                using var dst = outEntry.Open();
                await src.CopyToAsync(dst);
                overrideCount++;
            }
            Console.WriteLine($"  Copied {overrideCount} override files.");

            // Write bundled mod jars into overrides/mods/
            foreach (var (fileName, data) in bundledMods)
            {
                var modEntry = outZip.CreateEntry($"overrides/mods/{fileName}", CompressionLevel.Optimal);
                using var stream = modEntry.Open();
                await stream.WriteAsync(data);
            }
        }

        // Summary
        Console.WriteLine();
        Write("Done! ", ConsoleColor.Green);
        Write($"{modrinthCount} mods from Modrinth", ConsoleColor.Blue);
        Console.Write(", ");
        Write($"{bundledCount} mods bundled in overrides", ConsoleColor.DarkYellow);
        if (failedMods.Count > 0)
        {
            Console.Write(", ");
            Write($"{failedMods.Count} mod(s) failed", ConsoleColor.Red);
        }
        Console.WriteLine(".");
        WriteLine($"Output: {outputPath}", ConsoleColor.Green);
        return 0;
    }

    static MrFileEntry? FindModrinthFile(JsonNode mrVersion, string sha1)
    {
        var files = mrVersion["files"]?.AsArray();
        if (files is null) return null;

        foreach (var file in files)
        {
            if (file is null) continue;
            var hashes = file["hashes"];
            var fileSha1 = hashes?["sha1"]?.GetValue<string>();
            if (string.Equals(fileSha1, sha1, StringComparison.OrdinalIgnoreCase))
            {
                return new MrFileEntry
                {
                    Path = $"mods/{file["filename"]?.GetValue<string>() ?? "unknown.jar"}",
                    Sha1 = fileSha1!,
                    Sha512 = hashes?["sha512"]?.GetValue<string>() ?? "",
                    Url = file["url"]?.GetValue<string>() ?? "",
                    FileSize = file["size"]?.GetValue<long>() ?? 0
                };
            }
        }

        // If exact hash match not found, use primary file
        var primary = files.FirstOrDefault(f => f?["primary"]?.GetValue<bool>() == true) ?? files[0];
        if (primary is null) return null;

        var pH = primary["hashes"];
        return new MrFileEntry
        {
            Path = $"mods/{primary["filename"]?.GetValue<string>() ?? "unknown.jar"}",
            Sha1 = pH?["sha1"]?.GetValue<string>() ?? sha1,
            Sha512 = pH?["sha512"]?.GetValue<string>() ?? "",
            Url = primary["url"]?.GetValue<string>() ?? "",
            FileSize = primary["size"]?.GetValue<long>() ?? 0
        };
    }

    static async Task<byte[]?> TryDownloadMod(HttpClient http, CfFileInfo cfFile, int modId)
    {
        // Tier 1: CurseForge API downloadUrl
        if (!string.IsNullOrEmpty(cfFile.DownloadUrl))
        {
            var data = await TryDownload(http, cfFile.DownloadUrl);
            if (data != null) return data;
        }

        // Tier 2: CDN fallback
        int high = cfFile.Id / 1000;
        int low = cfFile.Id % 1000;
        string cdnUrl = $"https://mediafilez.forgecdn.net/files/{high}/{low}/{Uri.EscapeDataString(cfFile.FileName)}";
        {
            var data = await TryDownload(http, cdnUrl);
            if (data != null) return data;
        }

        // Tier 3: CurseForge website download endpoint (follows redirect)
        string websiteUrl = $"https://www.curseforge.com/api/v1/mods/{modId}/files/{cfFile.Id}/download";
        {
            var data = await TryDownload(http, websiteUrl);
            if (data != null) return data;
        }

        return null;
    }

    static async Task<byte[]?> TryDownload(HttpClient http, string url)
    {
        try
        {
            var resp = await http.GetAsync(url);
            if (resp.IsSuccessStatusCode)
                return await resp.Content.ReadAsByteArrayAsync();
        }
        catch
        {
            // Swallow — caller handles failure
        }
        return null;
    }

    static async Task<List<(string FileName, int FileID, byte[] Data)>> ManualRecovery(List<FailedMod> failedMods)
    {
        var results = new List<(string FileName, int FileID, byte[] Data)>();
        var stagingDir = Path.Combine(Path.GetTempPath(), "cursetomod-manual");

        if (Directory.Exists(stagingDir))
            Directory.Delete(stagingDir, true);
        Directory.CreateDirectory(stagingDir);

        // Open folder in explorer
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = stagingDir,
                UseShellExecute = true
            });
        }
        catch
        {
            WriteLine($"  Could not open explorer. Staging folder: {stagingDir}", ConsoleColor.Yellow);
        }

        Console.WriteLine($"A folder has been opened for manual downloads: {stagingDir}");
        Console.WriteLine("For each failed mod, download the file and place it in the folder.");
        Console.WriteLine("Type 'skip' to skip a mod, or press Ctrl+C to skip all remaining.");
        Console.WriteLine();

        foreach (var mod in failedMods.ToList())
        {
            WriteLine($"\u26a0 Could not download: \"{mod.DisplayName}\" (filename: {mod.FileName})", ConsoleColor.Yellow);
            Console.WriteLine($"  Download from: https://www.curseforge.com/minecraft/mc-mods/{mod.ProjectID}/download/{mod.FileID}");
            Console.WriteLine($"  Place the .jar file in the opened folder.");
            Console.Write("  Waiting for file (or type 'skip'): ");

            // Use FileSystemWatcher + Console input concurrently
            using var watcher = new FileSystemWatcher(stagingDir)
            {
                EnableRaisingEvents = true,
                Filter = "*.*"
            };

            var fileTcs = new TaskCompletionSource<string>();

            watcher.Created += (_, e) => fileTcs.TrySetResult(e.FullPath);

            // Also check for files that already exist (dropped before watcher started)
            var existingFiles = Directory.GetFiles(stagingDir);
            if (existingFiles.Length > 0)
                fileTcs.TrySetResult(existingFiles[0]);

            // Wait for either a dropped file or a typed "skip". Console input goes
            // through the shared reader so only one ReadLine is ever pending: a
            // read that is still outstanding when a file arrives is reused for the
            // confirmation prompt below instead of swallowing the next typed line.
            string? filePath = null;
            while (true)
            {
                var lineTask = NextLine();
                var finished = await Task.WhenAny((Task)fileTcs.Task, lineTask);
                if (finished == fileTcs.Task)
                {
                    filePath = fileTcs.Task.Result;
                    break;
                }

                var typed = (await lineTask)?.Trim();
                pendingLine = null;
                // End of input counts as skip, anything else except "skip" is ignored
                if (typed is null || string.Equals(typed, "skip", StringComparison.OrdinalIgnoreCase))
                    break;
            }

            if (filePath is null)
            {
                WriteLine("  Skipped.", ConsoleColor.Yellow);
                continue;
            }

            // Wait a moment for file to finish writing
            await Task.Delay(500);

            var detectedName = Path.GetFileName(filePath);

            // Fuzzy match: check if filename is a reasonable match
            // Without an API key the expected filename is unknown: ask every time.
            bool isMatch = !string.IsNullOrEmpty(mod.FileName) && IsFuzzyMatch(detectedName, mod.FileName);

            if (!isMatch)
            {
                Console.Write($"  Is \"{detectedName}\" the correct file for {mod.DisplayName}? [Y/n]: ");
                var answer = (await ReadLineAsync())?.Trim();
                // End of input (null) must not count as confirmation
                if (answer is null ||
                    string.Equals(answer, "n", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(answer, "no", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(answer, "skip", StringComparison.OrdinalIgnoreCase))
                {
                    WriteLine("  Skipped.", ConsoleColor.Yellow);
                    // Remove the rejected file so it is not offered again for the next mod
                    try { File.Delete(filePath); } catch { }
                    continue;
                }
            }

            try
            {
                var data = await File.ReadAllBytesAsync(filePath);
                // Use the original expected filename for the override (if known)
                results.Add((string.IsNullOrEmpty(mod.FileName) ? detectedName : mod.FileName, mod.FileID, data));
                WriteLine($"  \u2713 Found: {detectedName}", ConsoleColor.Green);

                // Remove the file from staging
                File.Delete(filePath);
            }
            catch (Exception ex)
            {
                WriteLine($"  Error reading file: {ex.Message}", ConsoleColor.Red);
            }
        }

        // Cleanup staging dir
        try { Directory.Delete(stagingDir, true); } catch { }

        return results;
    }

    // Single shared console reader: at most one ReadLine is outstanding at any time,
    // so a read that was started while waiting for a file can never steal a line
    // meant for a later prompt.
    static Task<string?>? pendingLine;

    static Task<string?> NextLine() => pendingLine ??= Task.Run(Console.ReadLine);

    // Drops a line that was already typed before the current prompt was shown.
    static void DiscardStaleLine()
    {
        if (pendingLine is { IsCompleted: true })
            pendingLine = null;
    }

    static async Task<string?> ReadLineAsync()
    {
        var line = await NextLine();
        pendingLine = null;
        return line;
    }

    static bool IsFuzzyMatch(string actual, string expected)
    {
        // Exact match
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            return true;

        // Substring containment: either contains the other
        if (actual.Contains(expected, StringComparison.OrdinalIgnoreCase) ||
            expected.Contains(actual, StringComparison.OrdinalIgnoreCase))
            return true;

        // Strip version-like suffixes and compare base name
        string StripVersion(string s) => s.Split(['-', '_'])[0].ToLowerInvariant();
        if (StripVersion(actual) == StripVersion(expected))
            return true;

        // Levenshtein distance check
        int dist = LevenshteinDistance(actual.ToLowerInvariant(), expected.ToLowerInvariant());
        int maxLen = Math.Max(actual.Length, expected.Length);
        return maxLen > 0 && (double)dist / maxLen < 0.3;
    }

    static int LevenshteinDistance(string a, string b)
    {
        int[,] dp = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) dp[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost);
            }
        }
        return dp[a.Length, b.Length];
    }

    static string? LoadStoredApiKey()
    {
        if (!File.Exists(ConfigPath)) return null;
        try
        {
            var json = JsonSerializer.Deserialize<JsonObject>(File.ReadAllText(ConfigPath));
            return json?["cfApiKey"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    static void SaveApiKey(string key)
    {
        Directory.CreateDirectory(ConfigDir);
        var config = new JsonObject { ["cfApiKey"] = key };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOpts));
    }

    static async Task<string?> PromptForApiKey()
    {
        Console.WriteLine("CurseForge API key required.");
        Console.WriteLine("Get one free at: https://console.curseforge.com/#/api-keys");
        Console.WriteLine("Type 'ignore' to skip (all mods will need manual download), or Ctrl+C to cancel.");
        Console.WriteLine();

        while (true)
        {
            Console.Write("Enter your API key: ");
            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input))
                continue;

            if (string.Equals(input, "ignore", StringComparison.OrdinalIgnoreCase))
            {
                WriteLine("Proceeding without CurseForge API key.", ConsoleColor.Yellow);
                Console.WriteLine();
                return null;
            }

            input = SanitizeApiKey(input);
            Console.Write("Testing API key... ");
            if (await TestApiKey(input))
            {
                WriteLine("valid!", ConsoleColor.Green);
                SaveApiKey(input);
                WriteLine("API key saved.", ConsoleColor.Green);
                Console.WriteLine();
                return input;
            }
            else
            {
                WriteLine("Please check your key and try again, or type 'ignore'.", ConsoleColor.Yellow);
                Console.WriteLine();
            }
        }
    }

    const string UserAgent = "cursetomod/1.0 (+https://github.com/Arimodu/curseforge-to-modrinth-cli)";

    static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        // Modrinth asks every client to send a uniquely identifying User-Agent,
        // and requests without one may get blocked. .NET sends none by default.
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    /// <summary>
    /// Cleans up a pasted API key: strips whitespace, surrounding quotes and
    /// invisible characters (zero-width spaces, BOM, NBSP) that sneak in when
    /// copying from a web page or a terminal.
    /// </summary>
    static string SanitizeApiKey(string key)
    {
        var cleaned = new string(key.Where(c =>
            !char.IsWhiteSpace(c) &&
            !char.IsControl(c) &&
            c is not ('​' or '‌' or '‍' or '⁠' or '﻿' or ' ')).ToArray());

        if (cleaned.Length >= 2 &&
            ((cleaned[0] == '"' && cleaned[^1] == '"') || (cleaned[0] == '\'' && cleaned[^1] == '\'')))
            cleaned = cleaned[1..^1];

        return cleaned;
    }

    static async Task<bool> TestApiKey(string apiKey)
    {
        apiKey = SanitizeApiKey(apiKey);

        // CurseForge keys look like "$2a$10$" followed by 53 characters (60 total).
        // A shell can silently eat the "$..." parts (e.g. PowerShell expands $2a / $10
        // as variables when the key is passed unquoted or in double quotes).
        if (!apiKey.StartsWith("$2a$10$") || apiKey.Length != 60)
        {
            WriteLine("unexpected format.", ConsoleColor.Red);
            WriteLine($"  Got {apiKey.Length} characters starting with \"{(apiKey.Length > 7 ? apiKey[..7] : apiKey)}\".", ConsoleColor.Yellow);
            WriteLine("  CurseForge keys start with \"$2a$10$\" and are 60 characters long.", ConsoleColor.Yellow);
            WriteLine("  On the command line, wrap the key in single quotes: --cf-api-key '$2a$10$...'", ConsoleColor.Yellow);
            Console.Write("  Trying it anyway... ");
        }

        try
        {
            using var http = CreateHttpClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            // TryAddWithoutValidation: the key contains '$', '/' and '.', never let
            // header parsing reject or alter it.
            http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey);

            // /v1/games is the endpoint the CurseForge docs use for "is my key working?"
            var resp = await http.GetAsync("https://api.curseforge.com/v1/games");
            if (resp.IsSuccessStatusCode)
                return true;

            var body = (await resp.Content.ReadAsStringAsync()).Trim();
            WriteLine($"rejected (HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}).", ConsoleColor.Red);
            if (body.Length > 0)
                WriteLine($"  Response: {(body.Length > 300 ? body[..300] + "…" : body)}", ConsoleColor.DarkGray);

            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                WriteLine("  CurseForge returns 403 for keys it does not accept. Known fixes:", ConsoleColor.Yellow);
                WriteLine("   - Copy the key again via the copy button at https://console.curseforge.com/#/api-keys", ConsoleColor.Yellow);
                WriteLine("   - Regenerate the key there and use the new one (old keys stop working)", ConsoleColor.Yellow);
                WriteLine("   - Make sure your CurseForge for Studios account/organization is fully set up (name filled in, terms accepted)", ConsoleColor.Yellow);
                WriteLine("   - Too many requests: wait a few minutes and try again", ConsoleColor.Yellow);
            }
            else if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                WriteLine("  Rate limited by CurseForge. Wait a few minutes and try again.", ConsoleColor.Yellow);
            }
            return false;
        }
        catch (Exception ex)
        {
            var inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            WriteLine("could not reach api.curseforge.com.", ConsoleColor.Red);
            WriteLine($"  {ex.GetType().Name}: {inner.Message}", ConsoleColor.Yellow);
            WriteLine("  This is a network problem, not a key problem (firewall, proxy, antivirus HTTPS scanning, DNS, VPN).", ConsoleColor.Yellow);
            return false;
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine("Usage: cursetomod <input.zip> [output.mrpack]");
        Console.WriteLine();
        Console.WriteLine("Converts a CurseForge modpack (.zip) to Modrinth format (.mrpack).");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --cf-api-key <key>  CurseForge API key (overrides stored key)");
    }
}

// === Data Models ===

class CfManifest
{
    public CfMinecraft Minecraft { get; set; } = new();
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public List<CfModFile> Files { get; set; } = [];
    public string Overrides { get; set; } = "overrides";
}

class CfMinecraft
{
    public string Version { get; set; } = "";
    public List<CfModLoader> ModLoaders { get; set; } = [];
}

class CfModLoader
{
    public string Id { get; set; } = "";
}

class CfModFile
{
    [JsonPropertyName("projectID")]
    public int ProjectID { get; set; }
    [JsonPropertyName("fileID")]
    public int FileID { get; set; }
    public bool Required { get; set; }
}

class CfFilesResponse
{
    public List<CfFileInfo> Data { get; set; } = [];
}

class CfFileInfo
{
    public int Id { get; set; }
    public int ModId { get; set; }
    public string FileName { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? DownloadUrl { get; set; }
    public long FileLength { get; set; }
    public List<CfHash> Hashes { get; set; } = [];
}

class CfHash
{
    public string Value { get; set; } = "";
    public int Algo { get; set; } // 1 = sha1, 2 = md5
    [JsonIgnore]
    public string? AlgoName { get; set; }
}

class FailedMod
{
    public int ProjectID { get; set; }
    public int FileID { get; set; }
    public string FileName { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

class MrFileEntry
{
    public string Path { get; set; } = "";
    public string Sha1 { get; set; } = "";
    public string Sha512 { get; set; } = "";
    public string Url { get; set; } = "";
    public long FileSize { get; set; }

    public JsonNode ToJsonNode() => new JsonObject
    {
        ["path"] = Path,
        ["hashes"] = new JsonObject
        {
            ["sha1"] = Sha1,
            ["sha512"] = Sha512
        },
        ["downloads"] = new JsonArray(Url),
        ["fileSize"] = FileSize,
        ["env"] = new JsonObject
        {
            ["client"] = "required",
            ["server"] = "required"
        }
    };
}
